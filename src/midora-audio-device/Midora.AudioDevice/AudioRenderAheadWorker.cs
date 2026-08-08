using System.Runtime.InteropServices;

namespace Midora.AudioDevice;

public sealed unsafe class AudioRenderAheadWorker : IDisposable
{
    private readonly IAudioRenderSource _source;
    private readonly AudioFrameRingBuffer _destination;
    private readonly int _workFrameCount;
    private readonly Thread _thread;
    private float* _workBuffer;
    private int _stopRequested;
    private int _pauseRequested;
    private int _paused;
    private int _started;
    private int _finished;
    private long _renderingThreadAllocatedBytes;
    private bool _disposed;

    public AudioRenderAheadWorker(
        IAudioRenderSource source,
        AudioFrameRingBuffer destination,
        int workFrameCount,
        string threadName = "Midora Audio Render-Ahead")
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

        long timeoutMilliseconds = checked((long)Math.Ceiling(timeout.TotalMilliseconds));
        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (!IsPaused)
        {
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

                if (_destination.FreeFrameCount < _workFrameCount)
                {
                    Thread.Sleep(1);
                    continue;
                }

                AudioPullResult result = _source.PullFrames(_workBuffer, _workFrameCount);
                if (!result.IsValidForRequest(_workFrameCount)
                    || result.Status == AudioPullStatus.Fault)
                {
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
                    _destination.FaultProducer();
                    break;
                }

                if (result.Status == AudioPullStatus.EndOfStream)
                {
                    _destination.CompleteProducer();
                    break;
                }
            }
        }
        catch
        {
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
}
