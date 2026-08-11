using NativeBass = Midora.NativeInterops.Bass.BASS;

namespace Midora.Audio.Bass.Internals;

internal sealed unsafe class ParallelBassMidiDecodeCoordinator : IDisposable
{
    private readonly uint[] _streamHandles;
    private readonly float* _scratch;
    private readonly int _scratchFrameStride;
    private readonly bool[] _required;
    private readonly uint[] _results;
    private readonly int[] _errors;
    private readonly Thread[] _threads;
    private readonly AutoResetEvent[] _starts;
    private readonly AutoResetEvent _completed = new(initialState: false);
    private readonly CountdownEvent _started;
    private int _nextUnitIndex;
    private int _completedWorkerCount;
    private uint _requestedByteCount;
    private int _stopRequested;
    private int _startupError;
    private long _workerAllocatedBytes;
    private bool _disposed;

    public ParallelBassMidiDecodeCoordinator(
        uint[] streamHandles,
        float* scratch,
        int scratchFrameStride,
        int concurrency)
    {
        ArgumentNullException.ThrowIfNull(streamHandles);
        if (streamHandles.Length == 0
            || scratch == null
            || scratchFrameStride <= 0
            || concurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(concurrency));
        }
        _streamHandles = streamHandles;
        _scratch = scratch;
        _scratchFrameStride = scratchFrameStride;
        _required = new bool[streamHandles.Length];
        _results = new uint[streamHandles.Length];
        _errors = new int[streamHandles.Length];
        int workerCount = Math.Min(streamHandles.Length, concurrency) - 1;
        _threads = new Thread[workerCount];
        _starts = new AutoResetEvent[workerCount];
        _started = new CountdownEvent(workerCount);
        for (int index = 0; index < workerCount; index++)
        {
            int workerIndex = index;
            _starts[index] = new AutoResetEvent(initialState: false);
            _threads[index] = new Thread(() => RunWorker(workerIndex))
            {
                IsBackground = true,
                Name = $"Midora Segment Decode {index + 1}",
                Priority = ThreadPriority.AboveNormal
            };
            _threads[index].Start();
        }
        _started.Wait();
        if (Volatile.Read(ref _startupError) != 0)
        {
            Dispose();
            throw new MidoraAudioException(
                $"A parallel BASSMIDI decode worker could not select the no-sound device; BASS error {_startupError}.");
        }
    }

    public Span<bool> Required => _required;

    public uint GetResult(int unitIndex) => _results[unitIndex];

    public int GetError(int unitIndex) => _errors[unitIndex];

    public long WorkerAllocatedBytes => Volatile.Read(ref _workerAllocatedBytes);

    public void Decode(uint requestedByteCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _requestedByteCount = requestedByteCount;
        _nextUnitIndex = -1;
        _completedWorkerCount = 0;
        for (int index = 0; index < _starts.Length; index++)
        {
            _starts[index].Set();
        }
        ProcessWork();
        if (_threads.Length != 0)
        {
            _completed.WaitOne();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Volatile.Write(ref _stopRequested, 1);
        foreach (AutoResetEvent start in _starts)
        {
            start.Set();
        }
        foreach (Thread thread in _threads)
        {
            thread.Join();
        }
        foreach (AutoResetEvent start in _starts)
        {
            start.Dispose();
        }
        _started.Dispose();
        _completed.Dispose();
    }

    private void RunWorker(int workerIndex)
    {
        if (NativeBass.SetDevice(0) == 0)
        {
            Interlocked.CompareExchange(
                ref _startupError,
                NativeBass.ErrorGetCode(),
                0);
        }
        _started.Signal();
        AutoResetEvent start = _starts[workerIndex];
        while (true)
        {
            start.WaitOne();
            if (Volatile.Read(ref _stopRequested) != 0)
            {
                return;
            }
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            ProcessWork();
            Interlocked.Add(
                ref _workerAllocatedBytes,
                GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
            if (Interlocked.Increment(ref _completedWorkerCount) == _threads.Length)
            {
                _completed.Set();
            }
        }
    }

    private void ProcessWork()
    {
        while (true)
        {
            int unitIndex = Interlocked.Increment(ref _nextUnitIndex);
            if (unitIndex >= _streamHandles.Length)
            {
                return;
            }
            if (!_required[unitIndex])
            {
                continue;
            }
            uint result = NativeBass.ChannelGetData(
                _streamHandles[unitIndex],
                _scratch + (unitIndex * _scratchFrameStride * 2),
                _requestedByteCount);
            _results[unitIndex] = result;
            _errors[unitIndex] = result == uint.MaxValue
                ? NativeBass.ErrorGetCode()
                : 0;
        }
    }
}
