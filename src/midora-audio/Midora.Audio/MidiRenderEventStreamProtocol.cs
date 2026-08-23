using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using Midora.Midi;

namespace Midora.Audio;

public sealed class MidiRenderEventStreamProducer : IDisposable
{
    private const int QuerySliceMilliseconds = 250;
    private const int WriteBatchRecordCount = 16_384;
    private readonly IMidiRenderEventPageProvider _provider;
    private readonly MidiRenderEventDemandState _demand;
    private readonly MidiRenderEventStreamControl _control;
    private readonly FileStream _data;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _monitoringGate = new();
    private readonly object _rewindGate = new();
    private readonly ManualResetEventSlim _rewindApplied = new(initialState: true);
    private readonly Task _task;
    private readonly long _totalFrameCount;
    private readonly long _querySliceFrames;
    private long _queriedWindowCount;
    private long _emittedRecordCount;
    private long _fullySuppressedWindowCount;
    private long _suppressedSourceWindowCount;
    private long _producedThroughFrame;
    private long _pendingRewindFrame = -1;
    private int _monitoringTimeoutFault;
    private int _faulted;
    private bool _disposed;

    private MidiRenderEventStreamProducer(
        IMidiRenderEventPageProvider provider,
        MidiRenderEventDemandState demand,
        string directory,
        int sampleRate,
        long totalFrameCount)
    {
        _provider = provider;
        _demand = demand;
        _totalFrameCount = totalFrameCount;
        _querySliceFrames = Math.Max(1, checked((long)sampleRate * QuerySliceMilliseconds / 1_000));
        Directory.CreateDirectory(directory);
        string identity = Guid.NewGuid().ToString("N");
        string dataPath = Path.Combine(directory, $"midi-events-{identity}.mres");
        string controlName = $"Midora.MidiEvents.{identity}";
        _data = new FileStream(
            dataPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read | FileShare.Delete,
            1024 * 1024,
            FileOptions.SequentialScan);
        _control = MidiRenderEventStreamControl.Create(controlName);
        Descriptor = new(controlName, dataPath);
        long startupFrames = Math.Min(
            totalFrameCount,
            RollingAudioPreparationPolicy.MillisecondsToFrames(
                sampleRate,
                RollingAudioPreparationPolicy.StartupMilliseconds));
        _control.RequestThrough(startupFrames);
        _task = Task.Factory.StartNew(
            Run,
            _cancellation.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public MidiRenderEventStreamDescriptor Descriptor { get; }

    public MidiRenderEventProducerSnapshot Snapshot => new(
        Interlocked.Read(ref _queriedWindowCount),
        Interlocked.Read(ref _emittedRecordCount),
        Interlocked.Read(ref _fullySuppressedWindowCount),
        Interlocked.Read(ref _suppressedSourceWindowCount),
        Interlocked.Read(ref _producedThroughFrame),
        _control.RequestedThroughFrame,
        Volatile.Read(ref _faulted) != 0);

    public static MidiRenderEventStreamProducer? Create(
        MidiRenderPlan plan,
        string directory)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return plan.EventPageProvider is null
            ? null
            : new(
                plan.EventPageProvider,
                new MidiRenderEventDemandState(plan),
                directory,
                plan.SampleRate,
                plan.TotalFrameCount);
    }

    public void ApplyMonitoringCommands(
        ReadOnlySpan<MidiMonitoringCommand> commands,
        long rewindFrame)
    {
        if (rewindFrame < 0 || rewindFrame > _totalFrameCount)
            throw new ArgumentOutOfRangeException(nameof(rewindFrame));
        lock (_monitoringGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_demand.ApplyMonitoringCommands(commands)) return;
            lock (_rewindGate)
            {
                if (Volatile.Read(ref _faulted) != 0)
                    throw new InvalidOperationException("The rolling MIDI event producer has faulted.");
                Volatile.Write(ref _pendingRewindFrame, rewindFrame);
                _rewindApplied.Reset();
            }
            if (!_rewindApplied.Wait(TimeSpan.FromSeconds(5))
                && !_rewindApplied.IsSet)
            {
                // A demand change without the matching event generation would
                // eventually starve the Worker. Terminate this producer as an
                // explicit playback fault instead of leaving a latent gap.
                Volatile.Write(ref _monitoringTimeoutFault, 1);
                _cancellation.Cancel();
                _ = _rewindApplied.Wait(TimeSpan.FromSeconds(1));
                throw new TimeoutException(
                    "The rolling MIDI event producer did not publish its monitoring generation within 5 seconds.");
            }
            if (Volatile.Read(ref _faulted) != 0)
                throw new InvalidOperationException("The rolling MIDI event producer faulted while rebuilding monitoring demand.");
        }
    }

    private void Run()
    {
        byte[] buffer = new byte[WriteBatchRecordCount * MidiRenderEventStreamControl.RecordByteCount];
        long producedThrough = 0;
        long recordCount = 0;
        long generation = 0;
        long baseRecordOffset = 0;
        long previousFrame = -1;
        bool completionPublished = false;
        try
        {
            while (true)
            {
                _cancellation.Token.ThrowIfCancellationRequested();
                if (TryApplyPendingRewind(
                    ref producedThrough,
                    ref recordCount,
                    ref generation,
                    ref baseRecordOffset,
                    ref previousFrame))
                {
                    completionPublished = false;
                    continue;
                }
                if (producedThrough >= _totalFrameCount)
                {
                    if (!completionPublished)
                    {
                        _control.Publish(
                            recordCount,
                            _totalFrameCount,
                            completed: true,
                            generation,
                            baseRecordOffset);
                        completionPublished = true;
                    }
                    Thread.Sleep(1);
                    continue;
                }
                long requested = Math.Min(_totalFrameCount, _control.RequestedThroughFrame);
                if (requested <= producedThrough)
                {
                    Thread.Sleep(1);
                    continue;
                }
                long sliceFrames = Math.Min(
                    _querySliceFrames,
                    Math.Max(1, requested - producedThrough));
                long sliceEnd = Math.Min(requested, producedThrough + sliceFrames);
                int bufferedRecords = 0;
                long emittedThisSlice = 0;
                bool interruptedForRewind = false;
                MidiRenderEventDemandSnapshot demand = _demand.Capture();
                int demandedSources = demand.CountDemandedSources(producedThrough, sliceEnd);
                Interlocked.Add(
                    ref _suppressedSourceWindowCount,
                    demand.SourceCount - demandedSources);
                if (demandedSources == 0)
                {
                    Interlocked.Increment(ref _fullySuppressedWindowCount);
                }
                IEnumerable<ScheduledPortMidiMessage> values =
                    _provider is IMidiRenderEventDemandAwarePageProvider demandAware
                        ? demandAware.Query(
                            producedThrough,
                            sliceEnd,
                            demand,
                            _cancellation.Token)
                        : _provider.Query(
                            producedThrough,
                            sliceEnd,
                            _cancellation.Token);
                Interlocked.Increment(ref _queriedWindowCount);
                foreach (ScheduledPortMidiMessage value in values)
                {
                    // Monitoring demand can change while a dense page is being
                    // enumerated. Abandon the unpublished old-generation suffix
                    // immediately instead of making the UI wait for the entire
                    // 250 ms event window to drain.
                    if (Volatile.Read(ref _pendingRewindFrame) >= 0)
                    {
                        interruptedForRewind = true;
                        break;
                    }
                    long frame = value.Scheduled.SampleFrame;
                    if (frame < producedThrough || frame >= sliceEnd || frame < previousFrame)
                    {
                        throw new InvalidDataException(
                            "The rolling MIDI event provider returned an out-of-window or unordered event.");
                    }
                    WriteRecord(
                        buffer.AsSpan(
                            bufferedRecords * MidiRenderEventStreamControl.RecordByteCount,
                            MidiRenderEventStreamControl.RecordByteCount),
                        value);
                    bufferedRecords++;
                    emittedThisSlice++;
                    previousFrame = frame;
                    if (bufferedRecords == WriteBatchRecordCount)
                    {
                        _data.Write(buffer);
                        recordCount = checked(recordCount + bufferedRecords);
                        bufferedRecords = 0;
                    }
                }
                if (interruptedForRewind)
                {
                    continue;
                }
                if (bufferedRecords != 0)
                {
                    _data.Write(buffer, 0, checked(
                        bufferedRecords * MidiRenderEventStreamControl.RecordByteCount));
                    recordCount = checked(recordCount + bufferedRecords);
                }
                _data.Flush(flushToDisk: false);
                Interlocked.Add(ref _emittedRecordCount, emittedThisSlice);
                producedThrough = sliceEnd;
                Interlocked.Exchange(ref _producedThroughFrame, producedThrough);
                _control.Publish(
                    recordCount,
                    producedThrough,
                    completed: false,
                    generation,
                    baseRecordOffset);
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            if (Volatile.Read(ref _monitoringTimeoutFault) != 0)
            {
                Volatile.Write(ref _faulted, 1);
                _control.PublishFault();
            }
            _rewindApplied.Set();
        }
        catch
        {
            Volatile.Write(ref _faulted, 1);
            _control.PublishFault();
            _rewindApplied.Set();
        }
    }

    private bool TryApplyPendingRewind(
        ref long producedThrough,
        ref long recordCount,
        ref long generation,
        ref long baseRecordOffset,
        ref long previousFrame)
    {
        lock (_rewindGate)
        {
            long rewindFrame = Volatile.Read(ref _pendingRewindFrame);
            if (rewindFrame < 0) return false;
            producedThrough = rewindFrame;
            Volatile.Write(ref _pendingRewindFrame, -1);
            recordCount = 0;
            previousFrame = producedThrough - 1;
            generation = checked(generation + 1);
            if (_data.Position % MidiRenderEventStreamControl.RecordByteCount != 0)
                throw new InvalidDataException("The rolling MIDI event stream is not record-aligned.");
            baseRecordOffset = _data.Position / MidiRenderEventStreamControl.RecordByteCount;
            Interlocked.Exchange(ref _producedThroughFrame, producedThrough);
            _control.Publish(
                recordCount,
                producedThrough,
                completed: producedThrough >= _totalFrameCount,
                generation,
                baseRecordOffset);
            _rewindApplied.Set();
            return true;
        }
    }

    private static void WriteRecord(Span<byte> destination, ScheduledPortMidiMessage value)
    {
        destination.Clear();
        BinaryPrimitives.WriteInt64LittleEndian(destination, value.Scheduled.SampleFrame);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], value.Scheduled.Message.PackedValue);
        BinaryPrimitives.WriteInt32LittleEndian(destination[12..], value.Scheduled.SourceIndex);
        destination[16] = value.ZeroBasedPortNumber;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cancellation.Cancel();
        _rewindApplied.Set();
        try { _task.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        _data.Dispose();
        _control.Dispose();
        _rewindApplied.Dispose();
        _cancellation.Dispose();
        try { File.Delete(Descriptor.DataFilePath); } catch (IOException) { }
    }
}

public readonly record struct MidiRenderEventProducerSnapshot(
    long QueriedWindowCount,
    long EmittedRecordCount,
    long FullySuppressedWindowCount,
    long SuppressedSourceWindowCount,
    long ProducedThroughFrame,
    long RequestedThroughFrame,
    bool IsFaulted)
{
    public long ProducerLagFrames => Math.Max(0, RequestedThroughFrame - ProducedThroughFrame);
}

public sealed class MidiRenderEventStreamReader : IDisposable
{
    private const int RingCapacity = 262_144;
    private const int ReadBatchRecordCount = 16_384;
    private readonly MidiRenderEventStreamControl _control;
    private readonly FileStream _data;
    private readonly ScheduledPortMidiMessage[] _ring = new ScheduledPortMidiMessage[RingCapacity];
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _task;
    private readonly object _feederGate = new();
    private long _readPosition;
    private long _writePosition;
    private long _requestedThroughFrame;
    private long _safeThroughFrame;
    private int _completed;
    private int _faulted;
    private long _loadedCount;
    private long _fileOffset;
    private long _activeGeneration;
    private long _generationBaseRecordOffset;
    private long _minimumSampleFrame;
    private bool _disposed;

    public MidiRenderEventStreamReader(MidiRenderEventStreamDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        _control = MidiRenderEventStreamControl.Open(descriptor.ControlMapName);
        _data = new FileStream(
            descriptor.DataFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            1024 * 1024,
            FileOptions.RandomAccess);
        MidiRenderEventStreamPublishedState published = _control.ReadPublishedState();
        _activeGeneration = published.Generation;
        _generationBaseRecordOffset = published.BaseRecordOffset;
        _fileOffset = checked(
            _generationBaseRecordOffset * MidiRenderEventStreamControl.RecordByteCount);
        _requestedThroughFrame = _control.RequestedThroughFrame;
        _task = Task.Factory.StartNew(
            Run,
            _cancellation.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public long SafeThroughFrame => Volatile.Read(ref _safeThroughFrame);
    public bool IsCompleted => Volatile.Read(ref _completed) != 0;
    public bool IsFaulted => Volatile.Read(ref _faulted) != 0;

    public void RequestThrough(long frame)
    {
        if (frame < 0) return;
        long current;
        do
        {
            current = Volatile.Read(ref _requestedThroughFrame);
            if (frame <= current) return;
        }
        while (Interlocked.CompareExchange(ref _requestedThroughFrame, frame, current) != current);
    }

    public bool TryPeek(out ScheduledPortMidiMessage value)
    {
        long read = Volatile.Read(ref _readPosition);
        if (read >= Volatile.Read(ref _writePosition))
        {
            value = default;
            return false;
        }
        value = _ring[(int)(read % RingCapacity)];
        return true;
    }

    public bool TryDequeue(out ScheduledPortMidiMessage value)
    {
        long read = Volatile.Read(ref _readPosition);
        if (read >= Volatile.Read(ref _writePosition))
        {
            value = default;
            return false;
        }
        value = _ring[(int)(read % RingCapacity)];
        Volatile.Write(ref _readPosition, read + 1);
        return true;
    }

    public void Seek(long sampleFrame)
    {
        if (sampleFrame < 0) throw new ArgumentOutOfRangeException(nameof(sampleFrame));
        lock (_feederGate)
        {
            Span<byte> record = stackalloc byte[MidiRenderEventStreamControl.RecordByteCount];
            while (true)
            {
                MidiRenderEventStreamPublishedState published = _control.ReadPublishedState();
                long low = 0;
                long high = published.RecordCount;
                while (low < high)
                {
                    long middle = low + ((high - low) >> 1);
                    _data.Position = checked(
                        (published.BaseRecordOffset + middle)
                        * MidiRenderEventStreamControl.RecordByteCount);
                    _data.ReadExactly(record);
                    long frame = BinaryPrimitives.ReadInt64LittleEndian(record);
                    if (frame < sampleFrame) low = middle + 1;
                    else high = middle;
                }

                // A monitoring change appends and publishes a new generation.
                // Never splice a binary-search result from an obsolete generation
                // into the newly published suffix.
                MidiRenderEventStreamPublishedState latest = _control.ReadPublishedState();
                if (latest.Generation != published.Generation
                    || latest.BaseRecordOffset != published.BaseRecordOffset)
                {
                    continue;
                }

                _loadedCount = low;
                _activeGeneration = published.Generation;
                _generationBaseRecordOffset = published.BaseRecordOffset;
                _minimumSampleFrame = sampleFrame;
                _fileOffset = checked(
                    (_generationBaseRecordOffset + low)
                    * MidiRenderEventStreamControl.RecordByteCount);
                Volatile.Write(ref _readPosition, 0);
                Volatile.Write(ref _writePosition, 0);
                Volatile.Write(ref _safeThroughFrame, Math.Min(
                    sampleFrame,
                    published.ThroughFrame));
                Volatile.Write(ref _completed, 0);
                return;
            }
        }
    }

    private void Run()
    {
        byte[] buffer = new byte[ReadBatchRecordCount * MidiRenderEventStreamControl.RecordByteCount];
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                _control.RequestThrough(Volatile.Read(ref _requestedThroughFrame));
                int free;
                lock (_feederGate)
                {
                    // Seek also owns _feederGate while it replaces the active
                    // generation and its loaded counters. Read the published
                    // snapshot only after taking the same lock; otherwise a
                    // feeder can retain generation N, wait behind a Seek to
                    // generation N+1, and then combine N's committed count with
                    // N+1's loaded count. Dense streams make that race wide
                    // enough to fault the reader during Mute/Solo replacement.
                    MidiRenderEventStreamPublishedState published =
                        _control.ReadPublishedState();
                    if (published.IsFaulted)
                    {
                        Volatile.Write(ref _faulted, 1);
                        return;
                    }
                    if (published.Generation != Volatile.Read(ref _activeGeneration))
                    {
                        free = 0;
                        goto WaitForNextSnapshot;
                    }
                    long committedCount = published.RecordCount;
                    long write = Volatile.Read(ref _writePosition);
                    long read = Volatile.Read(ref _readPosition);
                    long partiallyLoadedThroughFrame = -1;
                    if (committedCount < _loadedCount)
                    {
                        throw new InvalidDataException(
                            "The rolling MIDI event stream committed count moved behind its loaded prefix.");
                    }
                    free = checked((int)Math.Min(
                        RingCapacity - (write - read),
                        committedCount - _loadedCount));
                    if (free > 0)
                    {
                        int take = Math.Min(ReadBatchRecordCount, free);
                        int bytes = checked(take * MidiRenderEventStreamControl.RecordByteCount);
                        _data.Position = _fileOffset;
                        _data.ReadExactly(buffer.AsSpan(0, bytes));
                        int enqueued = 0;
                        for (int index = 0; index < take; index++)
                        {
                            ScheduledPortMidiMessage record = ReadRecord(buffer.AsSpan(
                                index * MidiRenderEventStreamControl.RecordByteCount,
                                MidiRenderEventStreamControl.RecordByteCount));
                            // A monitoring generation is published at its rewind
                            // frame before the producer has necessarily caught up
                            // to the Worker's later audible frontier. Seek can only
                            // binary-search the prefix published at that instant;
                            // records appended afterwards may still precede the
                            // requested frame and must remain permanently filtered.
                            if (record.Scheduled.SampleFrame < _minimumSampleFrame)
                            {
                                continue;
                            }
                            _ring[(int)((write + enqueued) % RingCapacity)] = record;
                            enqueued++;
                            partiallyLoadedThroughFrame = record.Scheduled.SampleFrame;
                        }
                        _fileOffset = checked(_fileOffset + bytes);
                        _loadedCount += take;
                        Volatile.Write(ref _writePosition, write + enqueued);
                    }
                    MidiRenderEventStreamPublishedState latest =
                        _control.ReadPublishedState();
                    if (latest.Generation == _activeGeneration)
                    {
                        published = latest;
                    }
                    committedCount = published.RecordCount;
                    if (_loadedCount == committedCount)
                    {
                        Volatile.Write(ref _safeThroughFrame, published.ThroughFrame);
                        if (published.IsCompleted)
                        {
                            Volatile.Write(ref _completed, 1);
                        }
                    }
                    else if (partiallyLoadedThroughFrame >= 0)
                    {
                        // The ring can hold only a prefix of a very dense committed
                        // window. Every event before the last loaded event frame is
                        // nevertheless present, so expose that frame as an exclusive
                        // render frontier. The renderer can advance to it, drain its
                        // current-frame batch and release ring capacity without ever
                        // rendering past a possibly partial same-frame suffix.
                        long currentSafeThrough = Volatile.Read(ref _safeThroughFrame);
                        if (partiallyLoadedThroughFrame > currentSafeThrough)
                        {
                            Volatile.Write(
                                ref _safeThroughFrame,
                                partiallyLoadedThroughFrame);
                        }
                    }
                }
            WaitForNextSnapshot:
                Thread.Sleep(free == 0 ? 1 : 0);
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch
        {
            Volatile.Write(ref _faulted, 1);
        }
    }

    private static ScheduledPortMidiMessage ReadRecord(ReadOnlySpan<byte> source)
    {
        long frame = BinaryPrimitives.ReadInt64LittleEndian(source);
        uint packed = BinaryPrimitives.ReadUInt32LittleEndian(source[8..]);
        int sourceIndex = BinaryPrimitives.ReadInt32LittleEndian(source[12..]);
        byte port = source[16];
        return new(port, new(frame, MidiMessage.FromPackedValue(packed), sourceIndex));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cancellation.Cancel();
        try { _task.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        _data.Dispose();
        _control.Dispose();
        _cancellation.Dispose();
    }
}

internal sealed class MidiRenderEventStreamControl : IDisposable
{
    private const int Capacity = 4096;
    private const long Magic = 0x314d52545345524d;
    private const int Version = 3;
    private const long MagicOffset = 0;
    private const long VersionOffset = 8;
    private const long RequestedOffset = 16;
    private const long CommittedCountOffset = 24;
    private const long CommittedThroughOffset = 32;
    private const long StateOffset = 40;
    private const long SequenceOffset = 48;
    private const long GenerationOffset = 56;
    private const long BaseRecordOffset = 64;
    public const int RecordByteCount = 24;
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;

    private MidiRenderEventStreamControl(MemoryMappedFile mapping, MemoryMappedViewAccessor view)
    {
        _mapping = mapping;
        _view = view;
    }

    public long RequestedThroughFrame => _view.ReadInt64(RequestedOffset);
    public MidiRenderEventStreamPublishedState ReadPublishedState()
    {
        while (true)
        {
            long before = _view.ReadInt64(SequenceOffset);
            if ((before & 1) != 0)
            {
                Thread.SpinWait(1);
                continue;
            }
            Thread.MemoryBarrier();
            long recordCount = _view.ReadInt64(CommittedCountOffset);
            long throughFrame = _view.ReadInt64(CommittedThroughOffset);
            int state = _view.ReadInt32(StateOffset);
            long generation = _view.ReadInt64(GenerationOffset);
            long baseRecordOffset = _view.ReadInt64(BaseRecordOffset);
            Thread.MemoryBarrier();
            long after = _view.ReadInt64(SequenceOffset);
            if (before == after && (after & 1) == 0)
            {
                if (recordCount < 0
                    || throughFrame < 0
                    || state is < 0 or > 2
                    || generation < 0
                    || baseRecordOffset < 0
                    || baseRecordOffset > (long.MaxValue / RecordByteCount) - recordCount)
                {
                    throw new InvalidDataException(
                        "The rolling MIDI event stream published an invalid state.");
                }
                return new(
                    recordCount,
                    throughFrame,
                    state,
                    generation,
                    baseRecordOffset);
            }
        }
    }

    public static MidiRenderEventStreamControl Create(string name)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Named MIDI event streams require Windows.");
        MemoryMappedFile mapping = MemoryMappedFile.CreateNew(name, Capacity);
        MemoryMappedViewAccessor view = mapping.CreateViewAccessor(0, Capacity);
        MidiRenderEventStreamControl result = new(mapping, view);
        view.Write(MagicOffset, Magic);
        view.Write(VersionOffset, Version);
        return result;
    }

    public static MidiRenderEventStreamControl Open(string name)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Named MIDI event streams require Windows.");
        MemoryMappedFile mapping = MemoryMappedFile.OpenExisting(name);
        MemoryMappedViewAccessor view = mapping.CreateViewAccessor(0, Capacity);
        if (view.ReadInt64(MagicOffset) != Magic || view.ReadInt32(VersionOffset) != Version)
        {
            view.Dispose();
            mapping.Dispose();
            throw new InvalidDataException("The rolling MIDI event stream header is invalid.");
        }
        return new(mapping, view);
    }

    public void RequestThrough(long frame)
    {
        long current = RequestedThroughFrame;
        if (frame > current)
        {
            _view.Write(RequestedOffset, frame);
            Thread.MemoryBarrier();
        }
    }

    public void Publish(
        long recordCount,
        long throughFrame,
        bool completed,
        long generation,
        long baseRecordOffset)
    {
        if (recordCount < 0 || throughFrame < 0 || generation < 0 || baseRecordOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(recordCount));
        long sequence = _view.ReadInt64(SequenceOffset);
        _view.Write(SequenceOffset, checked(sequence + 1));
        Thread.MemoryBarrier();
        _view.Write(CommittedCountOffset, recordCount);
        _view.Write(CommittedThroughOffset, throughFrame);
        _view.Write(StateOffset, completed ? 1 : 0);
        _view.Write(GenerationOffset, generation);
        _view.Write(BaseRecordOffset, baseRecordOffset);
        Thread.MemoryBarrier();
        _view.Write(SequenceOffset, checked(sequence + 2));
    }

    public void PublishFault()
    {
        long sequence = _view.ReadInt64(SequenceOffset);
        _view.Write(SequenceOffset, checked(sequence + 1));
        Thread.MemoryBarrier();
        _view.Write(StateOffset, 2);
        Thread.MemoryBarrier();
        _view.Write(SequenceOffset, checked(sequence + 2));
    }

    public void Dispose()
    {
        _view.Dispose();
        _mapping.Dispose();
    }
}

internal readonly record struct MidiRenderEventStreamPublishedState(
    long RecordCount,
    long ThroughFrame,
    int State,
    long Generation,
    long BaseRecordOffset)
{
    public bool IsCompleted => State == 1;
    public bool IsFaulted => State == 2;
}
