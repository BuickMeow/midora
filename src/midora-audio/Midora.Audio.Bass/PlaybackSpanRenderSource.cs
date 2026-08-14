using Midora.AudioDevice;
using System.Runtime.InteropServices;

namespace Midora.Audio.Bass;

internal unsafe interface IPlaybackSpanFallbackSource : IAudioRenderSource
{
    long PositionFrames { get; }

    long TotalFrameCount { get; }

    void ResetForMonitoringColdStart(
        long producerFrontierFrame,
        ReadOnlySpan<MidiMonitoringCommand> commands);
}

internal interface IMonitoringResettableRenderSource
{
    void ResetForMonitoringColdStart(
        long producerFrontierFrame,
        ReadOnlySpan<MidiMonitoringCommand> commands);
}

internal sealed unsafe class PlaybackSpanRenderSource
    : IAudioRenderSource,
      IMonitoringResettableRenderSource,
      IDisposable
{
    private const int MonitoringTransitionMilliseconds = 4;
    private readonly IPlaybackSpanFallbackSource _underlying;
    private readonly AsyncPcmFileStream _cacheIo;
    private readonly bool _cacheHit;
    private readonly long _totalFrameCount;
    private readonly int _transitionFrameCount;
    private long _positionFrames;
    private int _fallbackRequested;
    private int _usingUnderlying;
    private int _transitionRemainingFrames;
    private int _captureInvalidated;
    private bool _disposed;

    public PlaybackSpanRenderSource(
        IPlaybackSpanFallbackSource underlying,
        string stagingPath,
        bool cacheHit)
    {
        _underlying = underlying ?? throw new ArgumentNullException(nameof(underlying));
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingPath);
        Format = underlying.Format;
        _totalFrameCount = underlying.TotalFrameCount;
        _cacheHit = cacheHit;
        _transitionFrameCount = Math.Max(
            1,
            checked((Format.SampleRate * MonitoringTransitionMilliseconds + 999) / 1000));
        string path = Path.GetFullPath(stagingPath);
        long expectedLength = checked(
            AudioPcmCachePayload.HeaderByteCount
            + (_totalFrameCount * Format.BytesPerFrame));
        if (!File.Exists(path) || new FileInfo(path).Length != expectedLength)
        {
            throw new InvalidDataException(
                "The playback-span cache staging length does not match the render plan.");
        }
        Span<byte> header = stackalloc byte[AudioPcmCachePayload.HeaderByteCount];
        using (FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 1,
            FileOptions.RandomAccess))
        {
            stream.ReadExactly(header);
        }
        long headerFrameCount = AudioPcmCachePayload.ValidateHeader(header, Format);
        if (headerFrameCount != _totalFrameCount)
        {
            throw new InvalidDataException(
                "The playback-span cache frame count does not match the render plan.");
        }
        AsyncPcmFileStream? cacheIo = null;
        try
        {
            cacheIo = new AsyncPcmFileStream(
                path,
                AudioPcmCachePayload.HeaderByteCount,
                _totalFrameCount,
                Format,
                readMode: cacheHit);
            _cacheIo = cacheIo;
        }
        catch
        {
            cacheIo?.Dispose();
            throw;
        }
    }

    public AudioFormat Format { get; }

    public long PositionFrames => _cacheHit && Volatile.Read(ref _usingUnderlying) == 0
        ? Volatile.Read(ref _positionFrames)
        : _underlying.PositionFrames;

    public long TotalFrameCount => _totalFrameCount;

    public bool CacheHit => _cacheHit;

    public bool CacheCaptureInvalidated => !_cacheHit
        && (_cacheIo.IsFaulted || Volatile.Read(ref _captureInvalidated) != 0);

    public void RequestMonitoringFallback()
    {
        if (_cacheHit && Volatile.Read(ref _usingUnderlying) == 0)
        {
            Volatile.Write(ref _fallbackRequested, 1);
        }
    }

    public void ResetForMonitoringColdStart(
        long producerFrontierFrame,
        ReadOnlySpan<MidiMonitoringCommand> commands)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (producerFrontierFrame < 0 || producerFrontierFrame > _totalFrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(producerFrontierFrame));
        }
        _underlying.ResetForMonitoringColdStart(producerFrontierFrame, commands);
        _positionFrames = producerFrontierFrame;
        _transitionRemainingFrames = 0;
        Volatile.Write(ref _fallbackRequested, 0);
        Volatile.Write(ref _usingUnderlying, 1);
        if (!_cacheHit)
        {
            Volatile.Write(ref _captureInvalidated, 1);
        }
    }

    public void FinalizeCapture()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_cacheHit)
        {
            _cacheIo.WaitForWritingCompletion();
        }
    }

    public AudioPullResult PullFrames(float* destination, int requestedFrameCount)
    {
        if (_disposed || destination == null || requestedFrameCount < 0)
        {
            return AudioPullResult.Fault();
        }
        if (!_cacheHit)
        {
            if (Volatile.Read(ref _captureInvalidated) != 0)
            {
                AudioPullResult uncached = _underlying.PullFrames(destination, requestedFrameCount);
                if (uncached.IsValidForRequest(requestedFrameCount))
                {
                    _positionFrames += uncached.FrameCount;
                }
                return uncached;
            }
            int targetFrameCount = (int)Math.Min(
                requestedFrameCount,
                _totalFrameCount - _positionFrames);
            if (_cacheIo.IsFaulted)
            {
                AudioPullResult uncached = _underlying.PullFrames(destination, requestedFrameCount);
                if (uncached.IsValidForRequest(requestedFrameCount))
                {
                    _positionFrames += uncached.FrameCount;
                }
                return uncached;
            }
            if (!_cacheIo.CanWriteFrames(targetFrameCount))
            {
                return AudioPullResult.Buffering();
            }
            AudioPullResult rendered = _underlying.PullFrames(destination, requestedFrameCount);
            if (rendered.IsValidForRequest(requestedFrameCount) && rendered.FrameCount > 0)
            {
                if (!_cacheIo.TryWriteFrames(destination, rendered.FrameCount))
                {
                    return AudioPullResult.Fault(rendered.FrameCount);
                }
                _positionFrames += rendered.FrameCount;
            }
            if (rendered.Status == AudioPullStatus.EndOfStream)
            {
                _cacheIo.CompleteWriting();
            }
            return rendered;
        }

        long position = _positionFrames;
        if (Volatile.Read(ref _usingUnderlying) == 0
            && Volatile.Read(ref _fallbackRequested) != 0)
        {
            _underlying.ResetForMonitoringColdStart(position, []);
            _transitionRemainingFrames = _transitionFrameCount;
            Volatile.Write(ref _usingUnderlying, 1);
        }
        if (Volatile.Read(ref _usingUnderlying) != 0)
        {
            AudioPullResult rendered = _underlying.PullFrames(destination, requestedFrameCount);
            if (!rendered.IsValidForRequest(requestedFrameCount))
            {
                return AudioPullResult.Fault(rendered.FrameCount);
            }
            if (_transitionRemainingFrames > 0 && rendered.FrameCount > 0)
            {
                ApplyMonitoringFadeIn(destination, rendered.FrameCount);
            }
            _positionFrames += rendered.FrameCount;
            return rendered;
        }

        int frameCount = (int)Math.Min(requestedFrameCount, _totalFrameCount - position);
        if (frameCount > 0 && !_cacheIo.TryReadFrames(destination, frameCount))
        {
            return _cacheIo.IsFaulted
                ? AudioPullResult.Fault()
                : AudioPullResult.Buffering();
        }
        _positionFrames = position + frameCount;
        return position + frameCount == _totalFrameCount
            ? AudioPullResult.EndOfStream(frameCount)
            : AudioPullResult.Continue(frameCount);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _cacheIo.Dispose();
    }

    private void ApplyMonitoringFadeIn(
        float* destination,
        int frameCount)
    {
        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            if (_transitionRemainingFrames <= 0)
            {
                return;
            }
            int transitionIndex = _transitionFrameCount - _transitionRemainingFrames;
            float newWeight = _transitionFrameCount == 1
                ? 1f
                : transitionIndex / (float)(_transitionFrameCount - 1);
            int destinationSampleIndex = frameIndex * Format.ChannelCount;
            destination[destinationSampleIndex] *= newWeight;
            destination[destinationSampleIndex + 1] *= newWeight;
            _transitionRemainingFrames--;
        }
    }
}
