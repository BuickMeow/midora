using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace Midora.AudioDevice;

public sealed unsafe class BufferingRecoveryRenderSource : IAudioRenderSource, IDisposable
{
    private readonly IAudioRenderSource _underlying;
    private readonly MemoryMappedFile? _mapping;
    private readonly MemoryMappedViewAccessor? _view;
    private readonly long _capacityFrameCount;
    private readonly int _workFrameCount;
    private readonly bool _ownsFrameMemory;
    private byte* _acquiredPointer;
    private byte* _frames;
    private float* _workBuffer;
    private long _replayFrameCount;
    private long _replayPosition;
    private int _replaying;
    private bool _disposed;

    public BufferingRecoveryRenderSource(
        IAudioRenderSource underlying,
        string spoolPath,
        int workFrameCount)
    {
        _underlying = underlying ?? throw new ArgumentNullException(nameof(underlying));
        ArgumentException.ThrowIfNullOrWhiteSpace(spoolPath);
        if (workFrameCount < 16)
        {
            throw new ArgumentOutOfRangeException(nameof(workFrameCount));
        }
        string path = Path.GetFullPath(spoolPath);
        long byteLength = new FileInfo(path).Length;
        if (byteLength <= 0 || byteLength % underlying.Format.BytesPerFrame != 0)
        {
            throw new InvalidDataException(
                "The Buffering recovery spool is empty or not frame-aligned.");
        }

        _capacityFrameCount = byteLength / underlying.Format.BytesPerFrame;
        _workFrameCount = workFrameCount;
        MemoryMappedFile? mapping = null;
        MemoryMappedViewAccessor? view = null;
        byte* acquiredPointer = null;
        try
        {
            mapping = MemoryMappedFile.CreateFromFile(
                path,
                FileMode.Open,
                mapName: null,
                capacity: byteLength,
                MemoryMappedFileAccess.ReadWrite);
            view = mapping.CreateViewAccessor(
                0,
                byteLength,
                MemoryMappedFileAccess.ReadWrite);
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref acquiredPointer);
            _mapping = mapping;
            _view = view;
            _acquiredPointer = acquiredPointer;
            _frames = acquiredPointer + view.PointerOffset;
            _workBuffer = AllocateWorkBuffer(underlying, workFrameCount);
        }
        catch
        {
            if (view is not null && acquiredPointer != null)
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
            view?.Dispose();
            mapping?.Dispose();
            throw;
        }
    }

    public BufferingRecoveryRenderSource(
        IAudioRenderSource underlying,
        long capacityFrameCount,
        int workFrameCount)
    {
        _underlying = underlying ?? throw new ArgumentNullException(nameof(underlying));
        underlying.Format.Validate();
        if (capacityFrameCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacityFrameCount));
        }
        if (workFrameCount < 16)
        {
            throw new ArgumentOutOfRangeException(nameof(workFrameCount));
        }

        _capacityFrameCount = capacityFrameCount;
        _workFrameCount = workFrameCount;
        nuint byteLength = checked((nuint)capacityFrameCount
            * (nuint)underlying.Format.BytesPerFrame);
        _frames = (byte*)NativeMemory.Alloc(byteLength);
        if (_frames is null)
        {
            throw new OutOfMemoryException(
                "The in-memory Buffering recovery reservation could not be allocated.");
        }
        _ownsFrameMemory = true;
        try
        {
            _workBuffer = AllocateWorkBuffer(underlying, workFrameCount);
        }
        catch
        {
            NativeMemory.Free(_frames);
            _frames = null;
            throw;
        }
    }

    public AudioFormat Format => _underlying.Format;

    public long CapacityFrameCount => _capacityFrameCount;

    public bool IsReplaying => Volatile.Read(ref _replaying) != 0;

    public AudioPullResult PullFrames(float* destination, int requestedFrameCount)
    {
        if (_disposed || destination == null || requestedFrameCount < 0)
        {
            return AudioPullResult.Fault();
        }
        if (!IsReplaying)
        {
            return _underlying.PullFrames(destination, requestedFrameCount);
        }

        long remaining = _replayFrameCount - _replayPosition;
        int replayed = (int)Math.Min(requestedFrameCount, remaining);
        if (replayed != 0)
        {
            nuint byteCount = checked((nuint)replayed * (nuint)Format.BytesPerFrame);
            NativeMemory.Copy(
                _frames + checked(_replayPosition * Format.BytesPerFrame),
                destination,
                byteCount);
            _replayPosition += replayed;
        }
        if (_replayPosition != _replayFrameCount)
        {
            return AudioPullResult.Continue(replayed);
        }

        Volatile.Write(ref _replaying, 0);
        if (replayed == requestedFrameCount)
        {
            return AudioPullResult.Continue(replayed);
        }
        AudioPullResult tail = _underlying.PullFrames(
            destination + (replayed * Format.ChannelCount),
            requestedFrameCount - replayed);
        return tail.Status switch
        {
            AudioPullStatus.Fault => AudioPullResult.Fault(replayed + tail.FrameCount),
            // The replay cursor has already advanced. Returning Buffering here would
            // report zero frames and make the render-ahead worker discard the replayed
            // prefix, permanently moving the producer source ahead of the ring. A later
            // recovery that ends at EOS would then fail before its requested endpoint.
            AudioPullStatus.Buffering => replayed == 0
                ? AudioPullResult.Buffering()
                : AudioPullResult.Continue(replayed),
            AudioPullStatus.EndOfStream => AudioPullResult.EndOfStream(replayed + tail.FrameCount),
            _ => AudioPullResult.Continue(replayed + tail.FrameCount)
        };
    }

    public void PrepareRecovery(
        AudioFrameRingBuffer ring,
        long recoveryEndFrame)
    {
        _ = PrepareRecoveryCore(ring, recoveryEndFrame, cancellationRequested: null);
    }

    public bool TryPrepareRecovery(
        AudioFrameRingBuffer ring,
        long recoveryEndFrame,
        Func<bool> cancellationRequested)
    {
        ArgumentNullException.ThrowIfNull(cancellationRequested);
        return PrepareRecoveryCore(ring, recoveryEndFrame, cancellationRequested);
    }

    /// <summary>
    /// Discards a prepared underrun replay before a paused outer producer
    /// replaces its monitoring future. The caller must own the producer
    /// frontier so PullFrames cannot run concurrently.
    /// </summary>
    public void DiscardPreparedRecoveryForMonitoring()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _replayPosition = 0;
        _replayFrameCount = 0;
        Volatile.Write(ref _replaying, 0);
    }

    private bool PrepareRecoveryCore(
        AudioFrameRingBuffer ring,
        long recoveryEndFrame,
        Func<bool>? cancellationRequested)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(ring);
        if (ring.Format != Format || !ring.IsBuffering || IsReplaying)
        {
            throw new InvalidOperationException(
                "A recovery span requires a matching latched ring and no active replay.");
        }
        long failureFrame = ring.ReadPositionFrame;
        long recoveryFrames = recoveryEndFrame - failureFrame;
        if (recoveryFrames <= 0 || recoveryFrames > _capacityFrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(recoveryEndFrame));
        }

        int copied = ring.CopyAvailableFramesTo((float*)_frames, checked((int)Math.Min(
            _capacityFrameCount,
            int.MaxValue)));
        if (copied > recoveryFrames)
        {
            throw new InvalidOperationException(
                "The producer frontier is beyond the requested recovery endpoint.");
        }
        long prepared = copied;
        while (prepared < recoveryFrames)
        {
            if (cancellationRequested?.Invoke() == true)
            {
                return false;
            }
            int requested = (int)Math.Min(_workFrameCount, recoveryFrames - prepared);
            AudioPullResult result = _underlying.PullFrames(_workBuffer, requested);
            if (result.Status == AudioPullStatus.Buffering && result.FrameCount == 0)
            {
                // A cache read-ahead source may need its dedicated I/O thread to
                // refill. Recovery is already off the realtime render callback and
                // must wait for the complete natural interval instead of failing or
                // resuming in short bursts.
                Thread.Sleep(1);
                continue;
            }
            if (!result.IsValidForRequest(requested)
                || result.Status is AudioPullStatus.Fault or AudioPullStatus.Buffering
                || result.FrameCount == 0)
            {
                throw new InvalidDataException(
                    "The audio renderer could not complete the latched recovery span.");
            }
            NativeMemory.Copy(
                _workBuffer,
                _frames + checked(prepared * Format.BytesPerFrame),
                checked((nuint)result.FrameCount * (nuint)Format.BytesPerFrame));
            prepared += result.FrameCount;
            if (result.Status == AudioPullStatus.EndOfStream && prepared != recoveryFrames)
            {
                throw new EndOfStreamException(
                    "The audio renderer ended before the recovery endpoint.");
            }
        }

        if (cancellationRequested?.Invoke() == true)
        {
            return false;
        }

        ring.ResetBufferedFramesAtReadPosition();
        _replayPosition = 0;
        _replayFrameCount = recoveryFrames;
        Volatile.Write(ref _replaying, 1);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_workBuffer != null)
        {
            NativeMemory.Free(_workBuffer);
            _workBuffer = null;
        }
        _view?.Flush();
        if (_view is not null && _acquiredPointer != null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _acquiredPointer = null;
            _frames = null;
        }
        else if (_ownsFrameMemory && _frames != null)
        {
            NativeMemory.Free(_frames);
            _frames = null;
        }
        _view?.Dispose();
        _mapping?.Dispose();
    }

    private static float* AllocateWorkBuffer(
        IAudioRenderSource underlying,
        int workFrameCount)
    {
        float* result = (float*)NativeMemory.Alloc(
            checked((nuint)workFrameCount * (nuint)underlying.Format.BytesPerFrame));
        return result is not null
            ? result
            : throw new OutOfMemoryException(
                "The Buffering recovery work buffer could not be allocated.");
    }
}
