using System.Runtime.InteropServices;

namespace Midora.AudioDevice;

public sealed unsafe class AudioRenderAheadWorker : IDisposable
{
    private readonly IAudioRenderSource _source;
    private readonly AudioFrameRingBuffer _destination;
    private readonly int _workFrameCount;
    private readonly bool _allowRestartAfterEndOfStream;
    private readonly Thread _thread;
    private float* _workBuffer;
    private int _stopRequested;
    private int _pauseRequested;
    private int _paused;
    private int _waitingForProducerRestart;
    private int _started;
    private int _finished;
    private string? _faultReason;
    private Exception? _faultException;
    private long _renderingThreadAllocatedBytes;
    private bool _disposed;

    public AudioRenderAheadWorker(
        IAudioRenderSource source,
        AudioFrameRingBuffer destination,
        int workFrameCount,
        string threadName = "Midora Audio Render-Ahead",
        bool allowRestartAfterEndOfStream = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (source.Format != destination.Format)
        {
            throw new ArgumentException("The render source and ring formats do not match.", nameof(destination));
        }

        if (workFrameCount < 16 || workFrameCount > destination.CapacityFrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(workFrameCount));
        }

        _source = source;
        _destination = destination;
        _workFrameCount = workFrameCount;
        _allowRestartAfterEndOfStream = allowRestartAfterEndOfStream;
        _workBuffer = (float*)NativeMemory.Alloc(
            checked((nuint)workFrameCount * (nuint)source.Format.BytesPerFrame));
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = threadName,
            Priority = ThreadPriority.AboveNormal
        };
    }

    public bool IsStarted => Volatile.Read(ref _started) != 0;

    public bool IsFinished => Volatile.Read(ref _finished) != 0;

    public bool IsPaused => Volatile.Read(ref _paused) != 0;

    public string? FaultDescription => Volatile.Read(ref _faultException)?.ToString()
        ?? Volatile.Read(ref _faultReason);

    public long RenderingThreadAllocatedBytes => Volatile.Read(ref _renderingThreadAllocatedBytes);

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The render-ahead worker can only be started once.");
        }

        _thread.Start();
    }

    public void Stop()
    {
        if (!IsStarted || IsFinished)
        {
            return;
        }

        Volatile.Write(ref _stopRequested, 1);
        Volatile.Write(ref _pauseRequested, 0);
        _thread.Join();
    }

    public void PauseAtProducerFrontier(TimeSpan timeout)
    {
        _ = TryPauseAtProducerFrontier(timeout, cancellationRequested: null);
    }

    public bool TryPauseAtProducerFrontier(
        TimeSpan timeout,
        Func<bool>? cancellationRequested)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        if (!IsStarted || IsFinished)
        {
            throw new InvalidOperationException("The render-ahead producer is not active.");
        }
        if (Interlocked.Exchange(ref _pauseRequested, 1) != 0)
        {
            throw new InvalidOperationException("The render-ahead producer is already pausing or paused.");
        }
        if (cancellationRequested?.Invoke() == true)
        {
            CancelPauseRequest();
            return false;
        }

        long timeoutMilliseconds = checked((long)Math.Ceiling(timeout.TotalMilliseconds));
        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (!IsPaused)
        {
            if (cancellationRequested?.Invoke() == true)
            {
                CancelPauseRequest();
                return false;
            }
            if (IsFinished)
            {
                Volatile.Write(ref _pauseRequested, 0);
                throw new InvalidOperationException(
                    "The render-ahead producer completed before reaching a pause frontier.");
            }
            if (Environment.TickCount64 >= deadline)
            {
                Volatile.Write(ref _pauseRequested, 0);
                throw new TimeoutException(
                    "The render-ahead producer did not pause within the requested timeout.");
            }
            Thread.Sleep(1);
        }
        return true;
    }

    public void ResumeFromProducerFrontier()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsPaused || Volatile.Read(ref _pauseRequested) == 0)
        {
            throw new InvalidOperationException("The render-ahead producer is not paused.");
        }
        Volatile.Write(ref _pauseRequested, 0);
        while (IsPaused && !IsFinished)
        {
            Thread.Yield();
        }
    }

    /// <summary>
    /// Reopens an opted-in producer after it reached EOS while remaining alive
    /// for a later source generation. Returns false when the paused producer had
    /// not reached EOS.
    /// </summary>
    public bool RestartCompletedProducerAtPausedFrontier()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_allowRestartAfterEndOfStream)
        {
            throw new InvalidOperationException(
                "This render-ahead producer does not allow an EOS restart.");
        }
        if (!IsPaused || Volatile.Read(ref _pauseRequested) == 0)
        {
            throw new InvalidOperationException(
                "A completed producer can only restart at a paused frontier.");
        }
        if (Volatile.Read(ref _waitingForProducerRestart) == 0)
        {
            return false;
        }

        _destination.ReopenCompletedProducerAtEmptyFrontier();
        Volatile.Write(ref _waitingForProducerRestart, 0);
        return true;
    }

    /// <summary>
    /// Invalidates the current producer generation after its destination has
    /// been externally reset at a stable paused frontier. Unlike an EOS-only
    /// restart, this also resumes a generation that was still active when its
    /// prepared suffix became obsolete.
    /// </summary>
    public void RestartGenerationAtPausedFrontier()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_allowRestartAfterEndOfStream)
        {
            throw new InvalidOperationException(
                "This render-ahead producer does not allow a generation restart.");
        }
        if (!IsPaused || Volatile.Read(ref _pauseRequested) == 0)
        {
            throw new InvalidOperationException(
                "A producer generation can only restart at a paused frontier.");
        }

        Volatile.Write(ref _waitingForProducerRestart, 0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
        if (_workBuffer != null)
        {
            NativeMemory.Free(_workBuffer);
            _workBuffer = null;
        }
    }

    private void Run()
    {
        long allocatedBeforeRendering = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            while (Volatile.Read(ref _stopRequested) == 0)
            {
                if (Volatile.Read(ref _pauseRequested) != 0)
                {
                    Volatile.Write(ref _paused, 1);
                    while (Volatile.Read(ref _pauseRequested) != 0
                        && Volatile.Read(ref _stopRequested) == 0)
                    {
                        Thread.Sleep(1);
                    }
                    Volatile.Write(ref _paused, 0);
                    continue;
                }

                if (Volatile.Read(ref _waitingForProducerRestart) != 0)
                {
                    Thread.Sleep(1);
                    continue;
                }

                if (_destination.FreeFrameCount < _workFrameCount)
                {
                    Thread.Sleep(1);
                    continue;
                }

                AudioPullResult result = _source.PullFrames(_workBuffer, _workFrameCount);
                if (!result.IsValidForRequest(_workFrameCount)
                    || result.Status == AudioPullStatus.Fault)
                {
                    Volatile.Write(
                        ref _faultReason,
                        result.Status == AudioPullStatus.Fault
                            ? "The render source returned Fault."
                            : "The render source returned an invalid pull result.");
                    _destination.FaultProducer();
                    break;
                }

                if (result.Status == AudioPullStatus.Buffering)
                {
                    Thread.Sleep(1);
                    continue;
                }

                if (result.FrameCount != 0
                    && !_destination.TryWriteFrames(_workBuffer, result.FrameCount))
                {
                    Volatile.Write(
                        ref _faultReason,
                        "The render-ahead destination rejected a produced frame block.");
                    _destination.FaultProducer();
                    break;
                }

                if (result.Status == AudioPullStatus.EndOfStream)
                {
                    _destination.CompleteProducer();
                    if (!_allowRestartAfterEndOfStream)
                    {
                        break;
                    }
                    Volatile.Write(ref _waitingForProducerRestart, 1);
                }
            }
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _faultException, exception);
            _destination.FaultProducer();
        }
        finally
        {
            Volatile.Write(ref _paused, 0);
            Volatile.Write(
                ref _renderingThreadAllocatedBytes,
                GC.GetAllocatedBytesForCurrentThread() - allocatedBeforeRendering);
            Volatile.Write(ref _finished, 1);
        }
    }

    private void CancelPauseRequest()
    {
        Volatile.Write(ref _pauseRequested, 0);
        while (IsPaused && !IsFinished)
        {
            Thread.Yield();
        }
    }
}
