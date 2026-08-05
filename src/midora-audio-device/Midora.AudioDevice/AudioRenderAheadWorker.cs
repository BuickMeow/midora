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
        _thread.Join();
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
                if (_destination.FreeFrameCount < _workFrameCount)
                {
                    Thread.Sleep(1);
                    continue;
                }

                AudioPullResult result = _source.PullFrames(_workBuffer, _workFrameCount);
                if (result.Status == AudioPullStatus.Fault
                    || result.FrameCount < 0
                    || result.FrameCount > _workFrameCount)
                {
                    _destination.FaultProducer();
                    break;
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
            Volatile.Write(
                ref _renderingThreadAllocatedBytes,
                GC.GetAllocatedBytesForCurrentThread() - allocatedBeforeRendering);
            Volatile.Write(ref _finished, 1);
        }
    }
}
