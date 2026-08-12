using Midora.AudioDevice;
using Midora.Midi;
using System.Runtime.InteropServices;

namespace Midora.Audio.Bass;

internal sealed unsafe class RollingPreparationRenderSource : IAudioRenderSource, IDisposable
{
    private const int MonitoringTransitionMilliseconds = 4;
    private readonly IAudioRenderSource _underlying;
    private readonly AudioFrameRingBuffer _prepared;
    private readonly AudioRenderAheadWorker _worker;
    private readonly int _lowWatermarkFrames;
    private readonly int _resumeWatermarkFrames;
    private readonly int _transitionFrameCount;
    private float* _transitionBuffer;
    private long _positionFrames;
    private int _watermarkBuffering;
    private int _transitionTotalFrames;
    private int _transitionRemainingFrames;
    private bool _disposed;

    public RollingPreparationRenderSource(
        IAudioRenderSource underlying,
        long totalFrameCount,
        TimeSpan startupTimeout)
    {
        _underlying = underlying ?? throw new ArgumentNullException(nameof(underlying));
        if (totalFrameCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalFrameCount));
        }
        if (startupTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(startupTimeout));
        }
        int highFrames = RollingAudioPreparationPolicy.MillisecondsToFrames(
            underlying.Format.SampleRate,
            RollingAudioPreparationPolicy.TargetHighWatermarkMilliseconds);
        _lowWatermarkFrames = RollingAudioPreparationPolicy.MillisecondsToFrames(
            underlying.Format.SampleRate,
            RollingAudioPreparationPolicy.LowWatermarkMilliseconds);
        _resumeWatermarkFrames = RollingAudioPreparationPolicy.MillisecondsToFrames(
            underlying.Format.SampleRate,
            RollingAudioPreparationPolicy.ResumeWatermarkMilliseconds);
        _transitionFrameCount = RollingAudioPreparationPolicy.MillisecondsToFrames(
            underlying.Format.SampleRate,
            MonitoringTransitionMilliseconds);
        _transitionBuffer = (float*)NativeMemory.Alloc(
            checked((nuint)_transitionFrameCount * (nuint)underlying.Format.BytesPerFrame));
        if (_transitionBuffer is null)
        {
            throw new OutOfMemoryException(
                "The rolling monitoring-transition buffer could not be allocated.");
        }
        _prepared = new AudioFrameRingBuffer(underlying.Format, highFrames);
        int workFrames = InitialReleaseAudioRuntimePolicy.WorkFramesForRingCapacity(highFrames);
        _worker = new AudioRenderAheadWorker(
            underlying,
            _prepared,
            workFrames,
            "Midora Rolling Segment Preparation",
            allowRestartAfterEndOfStream: true);
        _worker.Start();

        int startupFrames = (int)Math.Min(
            totalFrameCount,
            RollingAudioPreparationPolicy.MillisecondsToFrames(
                underlying.Format.SampleRate,
                RollingAudioPreparationPolicy.StartupMilliseconds));
        long deadline = Environment.TickCount64 + checked((long)startupTimeout.TotalMilliseconds);
        while (_prepared.AvailableFrameCount < startupFrames
            && !_prepared.ProducerCompleted
            && !_prepared.ProducerFaulted)
        {
            if (Environment.TickCount64 >= deadline)
            {
                StopPreparation();
                throw new TimeoutException(
                    "Rolling audio preparation did not reach the 2-second startup watermark in time.");
            }
            Thread.Sleep(1);
        }
        if (_prepared.ProducerFaulted)
        {
            throw new MidoraAudioException(
                "Rolling audio preparation faulted before the startup watermark.");
        }
    }

    public AudioFormat Format => _underlying.Format;

    public long PositionFrames => Volatile.Read(ref _positionFrames);

    public int PreparedFrameCount => _prepared.AvailableFrameCount;

    public long RenderingThreadAllocatedBytes => _worker.RenderingThreadAllocatedBytes;

    public AudioPullResult PullFrames(float* destination, int requestedFrameCount)
    {
        if (_disposed || destination == null || requestedFrameCount < 0)
        {
            return AudioPullResult.Fault();
        }
        if (_prepared.ProducerFaulted)
        {
            return AudioPullResult.Fault();
        }
        int available = _prepared.AvailableFrameCount;
        if (!_prepared.ProducerCompleted)
        {
            if (Volatile.Read(ref _watermarkBuffering) != 0)
            {
                if (available < _resumeWatermarkFrames)
                {
                    return AudioPullResult.Buffering();
                }
                Volatile.Write(ref _watermarkBuffering, 0);
            }
            else if (available < _lowWatermarkFrames)
            {
                Volatile.Write(ref _watermarkBuffering, 1);
                return AudioPullResult.Buffering();
            }
        }
        if (_prepared.IsBuffering)
        {
            if (!_prepared.ProducerCompleted && available < _resumeWatermarkFrames)
            {
                return AudioPullResult.Buffering();
            }
            _prepared.ReleaseBuffering();
        }
        AudioPullResult result = _prepared.PullFrames(destination, requestedFrameCount);
        if (result.FrameCount != 0)
        {
            ApplyMonitoringTransition(destination, result.FrameCount);
            _positionFrames += result.FrameCount;
        }
        return result;
    }

    public void StopPreparation() => _worker.Stop();

    public void ResetForMonitoringColdStart(
        TimeSpan timeout) =>
        _ = ResetForMonitoringColdStartCore(
            timeout,
            [],
            cancellationRequested: null);

    public bool TryResetForMonitoringColdStart(
        TimeSpan timeout,
        ReadOnlySpan<MidiMonitoringCommand> commands,
        Func<bool> cancellationRequested)
    {
        ArgumentNullException.ThrowIfNull(cancellationRequested);
        return ResetForMonitoringColdStartCore(
            timeout,
            commands,
            cancellationRequested);
    }

    private bool ResetForMonitoringColdStartCore(
        TimeSpan timeout,
        ReadOnlySpan<MidiMonitoringCommand> commands,
        Func<bool>? cancellationRequested)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_underlying is not IMonitoringResettableRenderSource resettable)
        {
            throw new InvalidOperationException(
                "The rolling preparation source cannot reset its underlying renderer.");
        }
        if (!_worker.TryPauseAtProducerFrontier(timeout, cancellationRequested))
        {
            return false;
        }
        try
        {
            if (cancellationRequested?.Invoke() == true)
            {
                return false;
            }
            long frontier = PositionFrames;
            _transitionTotalFrames = _prepared.CopyPrefixFramesTo(
                _transitionBuffer,
                _transitionFrameCount);
            _transitionRemainingFrames = _transitionTotalFrames;
            _prepared.DiscardBufferedFramesAtReadPosition();
            resettable.ResetForMonitoringColdStart(frontier, commands);
            _ = _worker.RestartCompletedProducerAtPausedFrontier();
            Volatile.Write(ref _watermarkBuffering, 0);
            return true;
        }
        finally
        {
            _worker.ResumeFromProducerFrontier();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _worker.Dispose();
        _prepared.Dispose();
        if (_transitionBuffer != null)
        {
            NativeMemory.Free(_transitionBuffer);
            _transitionBuffer = null;
        }
    }

    private void ApplyMonitoringTransition(float* destination, int frameCount)
    {
        int remaining = _transitionRemainingFrames;
        int total = _transitionTotalFrames;
        if (remaining <= 0 || total <= 0)
        {
            return;
        }
        int transitionFrames = Math.Min(frameCount, remaining);
        int alreadyConsumed = total - remaining;
        for (int frameIndex = 0; frameIndex < transitionFrames; frameIndex++)
        {
            int transitionIndex = alreadyConsumed + frameIndex;
            float newWeight = transitionIndex / (float)total;
            float oldWeight = 1f - newWeight;
            int sampleIndex = frameIndex * Format.ChannelCount;
            int transitionSampleIndex = transitionIndex * Format.ChannelCount;
            destination[sampleIndex] =
                (_transitionBuffer[transitionSampleIndex] * oldWeight)
                + (destination[sampleIndex] * newWeight);
            destination[sampleIndex + 1] =
                (_transitionBuffer[transitionSampleIndex + 1] * oldWeight)
                + (destination[sampleIndex + 1] * newWeight);
        }
        _transitionRemainingFrames = remaining - transitionFrames;
    }
}
