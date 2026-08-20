using Microsoft.Win32.SafeHandles;
using Midora.AudioDevice;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Runtime.InteropServices;

namespace Midora.Audio.Bass;

// Test-compatibility adapter for the retired Unit-fragment cache bridge. Formal
// playback no longer constructs this type; it keeps the former low-level I/O
// tests useful while their coverage is migrated to Segment schedules.
internal sealed unsafe class UnitPcmCacheIoBridge : IDisposable
{
    private readonly SegmentPcmCacheIoBridge _inner;
    private readonly MidiSegmentRenderPlan[] _segmentsByPayloadOffset;

    public UnitPcmCacheIoBridge(string path, MidiRenderPlan plan)
    {
        MidiSegmentRenderPlan[] segments = plan.UnitFragments.ToArray()
            .Select(value => new MidiSegmentRenderPlan(
                value.TrackId,
                value.InstanceGroupId,
                value.SourceIndex,
                value.StartFrame,
                value.EndFrame,
                value.SemanticFingerprint,
                value.PcmCacheKey,
                value.PcmCachePayloadOffset,
                value.PcmCacheHit))
            .OrderBy(value => value.SourceIndex)
            .ThenBy(value => value.StartFrame)
            .ToArray();
        _segmentsByPayloadOffset = segments
            .OrderBy(value => value.PcmCachePayloadOffset)
            .ToArray();
        MidiRenderPlan compatibility = new(
            plan.SampleRate,
            plan.TotalFrameCount,
            plan.Ports,
            plan.SourceIds,
            plan.InitiallyDisabledSourceIndices,
            unitFragments: [],
            segments);
        _inner = new(path, readManifestPath: null, compatibility);
    }

    public bool ReadFaulted => _inner.ReadFaulted;
    public bool WriteFaulted => _inner.WriteFaulted;

    public bool TryReadFrames(
        MidiUnitFragmentRenderPlan fragment,
        long globalStartFrame,
        float* destination,
        int frameCount) => _inner.TryReadFrames(
            Resolve(fragment), globalStartFrame, destination, frameCount);

    public bool IsReadReady(
        MidiUnitFragmentRenderPlan fragment,
        long globalStartFrame,
        int frameCount) => _inner.IsReadReady(
            Resolve(fragment), globalStartFrame, frameCount);

    public bool CanWriteFrames(
        MidiUnitFragmentRenderPlan fragment,
        long globalStartFrame,
        int frameCount) => _inner.CanWriteFrames(
            Resolve(fragment), globalStartFrame, frameCount);

    public bool TryQueueWrite(
        MidiUnitFragmentRenderPlan fragment,
        long globalStartFrame,
        float* source,
        int frameCount) => _inner.TryQueueWrite(
            Resolve(fragment), globalStartFrame, source, frameCount);

    public void CompleteWritesAndWait() => _inner.CompleteWritesAndWait();

    public void Dispose() => _inner.Dispose();

    private MidiSegmentRenderPlan Resolve(MidiUnitFragmentRenderPlan fragment)
    {
        long payloadOffset = fragment.PcmCachePayloadOffset;
        int low = 0;
        int high = _segmentsByPayloadOffset.Length - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            MidiSegmentRenderPlan candidate = _segmentsByPayloadOffset[middle];
            if (candidate.PcmCachePayloadOffset < payloadOffset)
            {
                low = middle + 1;
            }
            else if (candidate.PcmCachePayloadOffset > payloadOffset)
            {
                high = middle - 1;
            }
            else
            {
                return candidate;
            }
        }
        throw new InvalidOperationException(
            "The compatibility Unit-fragment has no Segment cache schedule.");
    }
}

internal sealed unsafe class SegmentPcmCacheIoBridge : IDisposable
{
    private const int ReaderCapacityFrames = 16_384;
    private const int MinimumWriterCapacityFrames = 16_384;
    private const int WriterFlushThresholdFrames = 16_384;
    private readonly FileStream _file;
    private readonly SafeFileHandle _handle;
    private readonly ReusableAudioPackReader? _directReader;
    private readonly AudioCachePackJournalWriter? _journalWriter;
    private readonly ReaderSlot?[] _readers;
    private readonly WriterSlot?[] _writers;
    private readonly AudioFormat _format;
    private readonly Thread _thread;
    private int _completionRequested;
    private int _threadFinished;
    private Exception? _readFault;
    private Exception? _writeFault;
    private bool _disposed;

    public SegmentPcmCacheIoBridge(
        string path,
        string? readManifestPath,
        MidiRenderPlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(plan);
        string fullPath = Path.GetFullPath(path);
        _format = new(plan.SampleRate, 2, AudioSampleFormat.Float32);
        _readers = new ReaderSlot?[plan.SourceIds.Length];
        _writers = new WriterSlot?[plan.SourceIds.Length];
        FileStream? file = null;
        ReusableAudioPackReader? directReader = null;
        AudioCachePackJournalWriter? journalWriter = null;
        try
        {
            string journalDirectory = AudioCachePackJournal.GetDirectoryPath(fullPath);
            bool usePackJournal = Directory.Exists(journalDirectory);
            IReadOnlyDictionary<string, ReusableAudioReadEntry>? directEntries =
                string.IsNullOrWhiteSpace(readManifestPath)
                    ? null
                    : ReusableAudioReadManifest.Read(readManifestPath);
            if (directEntries is not null)
            {
                directReader = new ReusableAudioPackReader(directEntries);
                _directReader = directReader;
            }
            List<MidiSegmentRenderPlan>?[] hitFragments =
                new List<MidiSegmentRenderPlan>?[plan.SourceIds.Length];
            List<MidiSegmentRenderPlan>?[] missFragments =
                new List<MidiSegmentRenderPlan>?[plan.SourceIds.Length];
            foreach (MidiSegmentRenderPlan fragment in plan.Segments)
            {
                if (fragment.PcmCacheKey is null)
                {
                    continue;
                }
                List<MidiSegmentRenderPlan>?[] destination = fragment.PcmCacheHit
                    ? hitFragments
                    : missFragments;
                destination[fragment.SourceIndex] ??= [];
                destination[fragment.SourceIndex]!.Add(fragment);
            }
            int writerCount = missFragments.Count(value => value is { Count: > 0 });
            long poolBytes =
                RollingAudioPreparationPolicy.ComputeRamBlockPoolBytesForCurrentMachine();
            int writerCapacityFrames = writerCount == 0
                ? MinimumWriterCapacityFrames
                : checked((int)Math.Min(
                    int.MaxValue,
                    Math.Max(
                        MinimumWriterCapacityFrames,
                        poolBytes / writerCount / _format.BytesPerFrame
                            / RollingAudioPreparationPolicy.SegmentBlockFrameCount
                            * RollingAudioPreparationPolicy.SegmentBlockFrameCount)));
            for (int sourceIndex = 0; sourceIndex < hitFragments.Length; sourceIndex++)
            {
                if (hitFragments[sourceIndex] is { Count: > 0 } hits)
                {
                    _readers[sourceIndex] = new ReaderSlot(
                        checked((nuint)ReaderCapacityFrames * (nuint)_format.BytesPerFrame),
                        CreateCacheFragments(hits));
                }
                if (missFragments[sourceIndex] is { Count: > 0 } misses)
                {
                    _writers[sourceIndex] = new WriterSlot(
                        checked((nuint)writerCapacityFrames * (nuint)_format.BytesPerFrame),
                        writerCapacityFrames,
                        CreateCacheFragments(misses),
                        usePackJournal);
                }
            }
            if (usePackJournal)
            {
                journalWriter = new AudioCachePackJournalWriter(journalDirectory);
                Span<byte> pcmHeader = stackalloc byte[AudioPcmCachePayload.HeaderByteCount];
                HashSet<string> initializedKeys = new(StringComparer.Ordinal);
                foreach (MidiSegmentRenderPlan fragment in plan.Segments)
                {
                    if (fragment.PcmCacheKey is null
                        || fragment.PcmCacheHit
                        || !initializedKeys.Add(fragment.PcmCacheKey))
                    {
                        continue;
                    }
                    pcmHeader.Clear();
                    AudioPcmCachePayload.WriteHeader(
                        pcmHeader,
                        _format,
                        fragment.FrameCount);
                    journalWriter.WriteBlock(
                        fragment.PcmCacheKey!,
                        blockIndex: 0,
                        checked((int)AudioCachePackStore.ComputeBlockCount(
                            fragment.PcmPayloadByteCount)),
                        fragment.PcmPayloadByteCount,
                        pcmHeader);
                }
                _journalWriter = journalWriter;
            }
            _file = file = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite,
                bufferSize: 1,
                FileOptions.RandomAccess);
            _handle = _file.SafeFileHandle;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "Midora Segment PCM Cache I/O",
                Priority = ThreadPriority.AboveNormal
            };
            _thread.Start();
        }
        catch
        {
            file?.Dispose();
            directReader?.Dispose();
            journalWriter?.Dispose();
            DisposeBuffers();
            throw;
        }
    }

    public bool ReadFaulted => Volatile.Read(ref _readFault) is not null;

    internal Exception? ReadFault => _readFault;

    internal string? ReadFaultText => _readFault?.ToString();

    public bool WriteFaulted => Volatile.Read(ref _writeFault) is not null;

    public bool TryReadFrames(
        MidiSegmentRenderPlan fragment,
        long globalStartFrame,
        float* destination,
        int frameCount)
    {
        if (!IsReadReady(fragment, globalStartFrame, frameCount))
        {
            return false;
        }
        ReaderSlot slot = _readers[fragment.SourceIndex]!;
        long relativeStart = globalStartFrame - fragment.StartFrame;
        if (!TryResolveStreamPosition(
            slot.FragmentsByPayloadOffset,
            fragment,
            relativeStart,
            out long streamStart))
        {
            return false;
        }
        CopyFromReaderRing(slot, destination, streamStart, frameCount);
        Volatile.Write(ref slot.ConsumerPosition, streamStart + frameCount);
        return true;
    }

    public bool IsReadReady(
        MidiSegmentRenderPlan fragment,
        long globalStartFrame,
        int frameCount)
    {
        ReaderSlot slot = _readers[fragment.SourceIndex]
            ?? throw new InvalidOperationException("A cache-hit Segment has no read-ahead slot.");
        long relativeStart = globalStartFrame - fragment.StartFrame;
        if (!TryResolveStreamPosition(
            slot.FragmentsByPayloadOffset,
            fragment,
            relativeStart,
            out long streamStart))
        {
            return false;
        }
        if (Volatile.Read(ref slot.ConsumerPosition) != streamStart)
        {
            if (Volatile.Read(ref slot.RequestedStreamPosition) != streamStart)
            {
                Volatile.Write(ref slot.RequestedStreamPosition, streamStart);
                Interlocked.Increment(ref slot.RequestVersion);
            }
            return false;
        }
        if (Volatile.Read(ref slot.ProducerPosition) == streamStart
            && Volatile.Read(ref slot.RequestVersion) == slot.ActiveRequestVersion)
        {
            Volatile.Write(ref slot.RequestedStreamPosition, streamStart);
            Interlocked.Increment(ref slot.RequestVersion);
        }
        if (Volatile.Read(ref slot.ProducerPosition) - streamStart < frameCount)
        {
            return false;
        }
        return true;
    }

    public bool CanWriteFrames(
        MidiSegmentRenderPlan fragment,
        long globalStartFrame,
        int frameCount)
    {
        if (WriteFaulted)
        {
            return true;
        }
        WriterSlot slot = _writers[fragment.SourceIndex]
            ?? throw new InvalidOperationException("A cache-miss Segment has no writer slot.");
        long relativeStart = globalStartFrame - fragment.StartFrame;
        if (!TryResolveStreamPosition(
            slot.FragmentsByPayloadOffset,
            fragment,
            relativeStart,
            out long streamStart))
        {
            return false;
        }
        if (Volatile.Read(ref slot.ProducerPosition) != streamStart)
        {
            return false;
        }
        long consumed = Volatile.Read(ref slot.ConsumerPosition);
        return frameCount <= slot.CapacityFrames - (streamStart - consumed);
    }

    public bool TryQueueWrite(
        MidiSegmentRenderPlan fragment,
        long globalStartFrame,
        float* source,
        int frameCount)
    {
        if (WriteFaulted)
        {
            return true;
        }
        if (source == null || frameCount < 0
            || !CanWriteFrames(fragment, globalStartFrame, frameCount))
        {
            return false;
        }
        WriterSlot slot = _writers[fragment.SourceIndex]!;
        long relativeStart = globalStartFrame - fragment.StartFrame;
        if (!TryResolveStreamPosition(
            slot.FragmentsByPayloadOffset,
            fragment,
            relativeStart,
            out long streamStart))
        {
            return false;
        }
        long produced = Volatile.Read(ref slot.ProducerPosition);
        CopyIntoWriterRing(slot, source, produced, frameCount);
        Volatile.Write(ref slot.ProducerPosition, produced + frameCount);
        return true;
    }

    public void CompleteWritesAndWait()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Volatile.Write(ref _completionRequested, 1);
        if (Volatile.Read(ref _threadFinished) == 0)
        {
            _thread.Join();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Volatile.Write(ref _completionRequested, 1);
        if (Volatile.Read(ref _threadFinished) == 0)
        {
            _thread.Join();
        }
        _file.Dispose();
        _directReader?.Dispose();
        _journalWriter?.Dispose();
        DisposeBuffers();
    }

    private void Run()
    {
        try
        {
            while (true)
            {
                bool progressed = false;
                for (int i = 0; i < _readers.Length; i++)
                {
                    progressed |= FillOneReader(_readers[i]);
                }
                for (int i = 0; i < _writers.Length; i++)
                {
                    progressed |= DrainOneWriter(_writers[i]);
                }
                if (Volatile.Read(ref _completionRequested) != 0
                    && WritersAreDrained())
                {
                    break;
                }
                if (!progressed)
                {
                    Thread.Sleep(1);
                }
            }
            if (!WriteFaulted)
            {
                if (_journalWriter is not null)
                {
                    _journalWriter.Complete();
                }
                else
                {
                    _file.Flush(flushToDisk: true);
                }
            }
        }
        catch (Exception exception)
        {
            if (Volatile.Read(ref _readFault) is null)
            {
                Volatile.Write(ref _writeFault, exception);
            }
        }
        finally
        {
            Volatile.Write(ref _threadFinished, 1);
        }
    }

    private bool DrainOneWriter(WriterSlot? slot)
    {
        if (slot is null)
        {
            return false;
        }
        if (WriteFaulted)
        {
            Volatile.Write(ref slot.ConsumerPosition, Volatile.Read(ref slot.ProducerPosition));
            return false;
        }
        return WriteAvailableFrames(
            slot,
            force: Volatile.Read(ref _completionRequested) != 0);
    }

    private bool WriteAvailableFrames(WriterSlot slot, bool force)
    {
        long consumed = slot.ConsumerPosition;
        long produced = Volatile.Read(ref slot.ProducerPosition);
        long available = produced - consumed;
        if (available == 0)
        {
            return false;
        }
        if (!TryFindCacheFragment(slot.Fragments, consumed, out CacheFragment fragment))
        {
            Volatile.Write(ref _writeFault,
                new InvalidDataException("A Segment PCM writer position is outside its cache schedule."));
            return false;
        }
        if (_journalWriter is not null)
        {
            return WriteAvailableFramesToJournal(
                slot,
                fragment,
                consumed,
                available);
        }
        if (!force && available < WriterFlushThresholdFrames
            && produced < fragment.StreamEndFrame)
        {
            return false;
        }
        int index = (int)(consumed % slot.CapacityFrames);
        int frames = (int)Math.Min(
            Math.Min(available, slot.CapacityFrames - index),
            fragment.StreamEndFrame - consumed);
        try
        {
            RandomAccess.Write(
                _handle,
                new ReadOnlySpan<byte>(
                    slot.Buffer + (index * _format.BytesPerFrame),
                    checked(frames * _format.BytesPerFrame)),
                checked(fragment.PayloadOffset
                    + AudioPcmCachePayload.HeaderByteCount
                    + ((consumed - fragment.StreamStartFrame) * _format.BytesPerFrame)));
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _writeFault, exception);
        }
        Volatile.Write(ref slot.ConsumerPosition, consumed + frames);
        return true;
    }

    private bool FillOneReader(ReaderSlot? slot)
    {
        if (slot is null || ReadFaulted)
        {
            return false;
        }
        int requestedVersion = Volatile.Read(ref slot.RequestVersion);
        if (slot.ActiveRequestVersion != requestedVersion)
        {
            long requestedPosition = Volatile.Read(ref slot.RequestedStreamPosition);
            Volatile.Write(ref slot.ConsumerPosition, requestedPosition);
            slot.ProducerPosition = requestedPosition;
            slot.ActiveRequestVersion = requestedVersion;
        }

        long consumed = Volatile.Read(ref slot.ConsumerPosition);
        long produced = slot.ProducerPosition;
        int free = checked((int)(ReaderCapacityFrames - (produced - consumed)));
        if (free == 0 || produced == slot.TotalStreamFrameCount)
        {
            return false;
        }
        if (!TryFindCacheFragment(slot.Fragments, produced, out CacheFragment fragment))
        {
            Volatile.Write(ref _readFault,
                new InvalidDataException("A Segment PCM reader position is outside its cache schedule."));
            return false;
        }
        int index = (int)(produced % ReaderCapacityFrames);
        int frames = (int)Math.Min(
            Math.Min(Math.Min(free, ReaderCapacityFrames - index),
                fragment.StreamEndFrame - produced),
            slot.TotalStreamFrameCount - produced);
        try
        {
            Span<byte> destination = new(
                slot.Buffer + (index * _format.BytesPerFrame),
                checked(frames * _format.BytesPerFrame));
            long payloadRelativeOffset = checked(
                AudioPcmCachePayload.HeaderByteCount
                + ((produced - fragment.StreamStartFrame) * _format.BytesPerFrame));
            if (fragment.CacheKey is not null
                && _directReader?.Contains(fragment.CacheKey) == true)
            {
                _directReader.ReadExactly(
                    fragment.CacheKey,
                    payloadRelativeOffset,
                    destination);
            }
            else
            {
                ReadExactly(
                    destination,
                    checked(fragment.PayloadOffset + payloadRelativeOffset));
            }
            Volatile.Write(ref slot.ProducerPosition, produced + frames);
            return true;
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _readFault, exception);
            return false;
        }
    }

    private void ReadExactly(Span<byte> destination, long fileOffset)
    {
        int completed = 0;
        while (completed < destination.Length)
        {
            int read = RandomAccess.Read(
                _handle,
                destination[completed..],
                checked(fileOffset + completed));
            if (read == 0)
            {
                throw new EndOfStreamException("A staged Segment PCM cache payload ended early.");
            }
            completed += read;
        }
    }

    private void CopyFromReaderRing(
        ReaderSlot slot,
        float* destination,
        long position,
        int frameCount)
    {
        int index = (int)(position % ReaderCapacityFrames);
        int first = Math.Min(frameCount, ReaderCapacityFrames - index);
        NativeMemory.Copy(
            slot.Buffer + (index * _format.BytesPerFrame),
            destination,
            checked((nuint)first * (nuint)_format.BytesPerFrame));
        int remaining = frameCount - first;
        if (remaining != 0)
        {
            NativeMemory.Copy(
                slot.Buffer,
                destination + (first * _format.ChannelCount),
                checked((nuint)remaining * (nuint)_format.BytesPerFrame));
        }
    }

    private void CopyIntoWriterRing(
        WriterSlot slot,
        float* source,
        long position,
        int frameCount)
    {
        int index = (int)(position % slot.CapacityFrames);
        int first = Math.Min(frameCount, slot.CapacityFrames - index);
        NativeMemory.Copy(
            source,
            slot.Buffer + (index * _format.BytesPerFrame),
            checked((nuint)first * (nuint)_format.BytesPerFrame));
        int remaining = frameCount - first;
        if (remaining != 0)
        {
            NativeMemory.Copy(
                source + (first * _format.ChannelCount),
                slot.Buffer,
                checked((nuint)remaining * (nuint)_format.BytesPerFrame));
        }
    }

    private bool WritersAreDrained()
    {
        for (int i = 0; i < _writers.Length; i++)
        {
            WriterSlot? slot = _writers[i];
            if (slot is not null
                && Volatile.Read(ref slot.ConsumerPosition)
                    != Volatile.Read(ref slot.ProducerPosition))
            {
                return false;
            }
        }
        return true;
    }

    private bool WriteAvailableFramesToJournal(
        WriterSlot slot,
        CacheFragment fragment,
        long consumed,
        long available)
    {
        if (fragment.CacheKey is null || slot.JournalBlockBuffer is null)
        {
            Volatile.Write(ref _writeFault,
                new InvalidDataException("A journaled Segment PCM writer has no cache identity."));
            return false;
        }
        if (!string.Equals(
            slot.ActiveJournalKey,
            fragment.CacheKey,
            StringComparison.Ordinal))
        {
            if (slot.JournalBufferedBytes != 0)
            {
                Volatile.Write(ref _writeFault,
                    new InvalidDataException("A Segment PCM journal changed entries with a partial block."));
                return false;
            }
            slot.ActiveJournalKey = fragment.CacheKey;
            slot.NextJournalBlockIndex = 1;
        }

        int ringIndex = (int)(consumed % slot.CapacityFrames);
        int availableBlockFrames =
            (AudioCachePackStore.BlockPayloadBytes - slot.JournalBufferedBytes)
                / _format.BytesPerFrame;
        int frames = checked((int)Math.Min(
            Math.Min(
                Math.Min(available, slot.CapacityFrames - ringIndex),
                fragment.StreamEndFrame - consumed),
            availableBlockFrames));
        if (frames <= 0)
        {
            Volatile.Write(ref _writeFault,
                new InvalidDataException("A Segment PCM journal block made no forward progress."));
            return false;
        }
        try
        {
            int byteCount = checked(frames * _format.BytesPerFrame);
            new ReadOnlySpan<byte>(
                    slot.Buffer + (ringIndex * _format.BytesPerFrame),
                    byteCount)
                .CopyTo(slot.JournalBlockBuffer.AsSpan(slot.JournalBufferedBytes));
            slot.JournalBufferedBytes += byteCount;
            long nextConsumed = consumed + frames;
            bool fragmentCompleted = nextConsumed == fragment.StreamEndFrame;
            if (slot.JournalBufferedBytes == AudioCachePackStore.BlockPayloadBytes
                || fragmentCompleted)
            {
                long payloadLength = checked(
                    AudioPcmCachePayload.HeaderByteCount
                    + (fragment.FrameCount * _format.BytesPerFrame));
                int blockCount = checked((int)AudioCachePackStore.ComputeBlockCount(payloadLength));
                _journalWriter!.WriteBlock(
                    fragment.CacheKey,
                    slot.NextJournalBlockIndex++,
                    blockCount,
                    payloadLength,
                    slot.JournalBlockBuffer.AsSpan(0, slot.JournalBufferedBytes));
                slot.JournalBufferedBytes = 0;
                if (fragmentCompleted)
                {
                    if (slot.NextJournalBlockIndex != blockCount)
                    {
                        throw new InvalidDataException(
                            "A completed Segment PCM journal has an unexpected block count.");
                    }
                    slot.ActiveJournalKey = null;
                    slot.NextJournalBlockIndex = 0;
                }
            }
            Volatile.Write(ref slot.ConsumerPosition, nextConsumed);
            return true;
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _writeFault, exception);
            Volatile.Write(ref slot.ConsumerPosition, consumed + frames);
            return true;
        }
    }

    private static CacheFragment[] CreateCacheFragments(
        IEnumerable<MidiSegmentRenderPlan> fragments)
    {
        long streamStart = 0;
        List<CacheFragment> result = [];
        foreach (MidiSegmentRenderPlan fragment in fragments
            .OrderBy(value => value.StartFrame)
            .ThenBy(value => value.SegmentId))
        {
            long frameCount = fragment.EndFrame - fragment.StartFrame;
            result.Add(new(
                fragment.PcmCachePayloadOffset,
                fragment.PcmCacheKey,
                streamStart,
                frameCount));
            streamStart = checked(streamStart + frameCount);
        }
        return result.ToArray();
    }

    private static bool TryResolveStreamPosition(
        CacheFragment[] fragmentsByPayloadOffset,
        MidiSegmentRenderPlan fragment,
        long relativeStart,
        out long streamPosition)
    {
        if (fragment.PcmCacheKey is not null
            && fragment.PcmCacheHit
            && fragment.PcmCachePayloadOffset < 0)
        {
            foreach (CacheFragment candidate in fragmentsByPayloadOffset)
            {
                if (string.Equals(candidate.CacheKey, fragment.PcmCacheKey, StringComparison.Ordinal)
                    && relativeStart >= 0
                    && relativeStart <= candidate.FrameCount)
                {
                    streamPosition = checked(candidate.StreamStartFrame + relativeStart);
                    return true;
                }
            }
            streamPosition = 0;
            return false;
        }
        int low = 0;
        int high = fragmentsByPayloadOffset.Length - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            CacheFragment candidate = fragmentsByPayloadOffset[middle];
            if (candidate.PayloadOffset < fragment.PcmCachePayloadOffset)
            {
                low = middle + 1;
                continue;
            }
            if (candidate.PayloadOffset > fragment.PcmCachePayloadOffset)
            {
                high = middle - 1;
                continue;
            }
            if (relativeStart >= 0 && relativeStart <= candidate.FrameCount)
            {
                streamPosition = checked(candidate.StreamStartFrame + relativeStart);
                return true;
            }
            break;
        }
        streamPosition = 0;
        return false;
    }

    private static CacheFragment[] SortByPayloadOffset(CacheFragment[] fragments)
    {
        CacheFragment[] sorted = (CacheFragment[])fragments.Clone();
        Array.Sort(
            sorted,
            static (left, right) => left.PayloadOffset.CompareTo(right.PayloadOffset));
        for (int i = 1; i < sorted.Length; i++)
        {
            if (sorted[i - 1].PayloadOffset == sorted[i].PayloadOffset
                && sorted[i].PayloadOffset >= 0)
            {
                throw new InvalidDataException(
                    "A Segment PCM cache schedule contains duplicate payload offsets.");
            }
        }
        return sorted;
    }

    private static bool TryFindCacheFragment(
        CacheFragment[] fragments,
        long streamPosition,
        out CacheFragment fragment)
    {
        int low = 0;
        int high = fragments.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (fragments[middle].StreamEndFrame <= streamPosition)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        if (low < fragments.Length
            && streamPosition >= fragments[low].StreamStartFrame)
        {
            fragment = fragments[low];
            return true;
        }
        fragment = default;
        return false;
    }

    private void DisposeBuffers()
    {
        for (int i = 0; i < _readers.Length; i++)
        {
            _readers[i]?.Dispose();
            _readers[i] = null;
            _writers[i]?.Dispose();
            _writers[i] = null;
        }
    }

    private readonly record struct CacheFragment(
        long PayloadOffset,
        string? CacheKey,
        long StreamStartFrame,
        long FrameCount)
    {
        public long StreamEndFrame => checked(StreamStartFrame + FrameCount);
    }

    private sealed class ReusableAudioPackReader : IDisposable
    {
        private const uint EntryMagic = 0x4541434d; // MCAE
        private const int EntryVersion = 2;
        private const int EntryHeaderSize = 96;
        private const int MaximumBlockPayloadBytes =
            RollingAudioPreparationPolicy.SegmentBlockFrameCount * 2 * sizeof(float);
        private readonly IReadOnlyDictionary<string, ReusableAudioReadEntry> _entries;
        private readonly Dictionary<string, SafeFileHandle> _handles =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly byte[] _blockBuffer = new byte[MaximumBlockPayloadBytes];
        private readonly byte[] _headerBuffer = new byte[EntryHeaderSize];
        private readonly byte[] _digestBuffer = new byte[32];
        private bool _disposed;

        public ReusableAudioPackReader(
            IReadOnlyDictionary<string, ReusableAudioReadEntry> entries)
        {
            _entries = entries;
            byte[] pcmHeader = new byte[AudioPcmCachePayload.HeaderByteCount];
            try
            {
                foreach (ReusableAudioReadEntry entry in entries.Values)
                {
                    foreach (ReusableAudioReadExtent extent in entry.Extents)
                    {
                        string fullPath = Path.GetFullPath(extent.Path);
                        if (_handles.ContainsKey(fullPath))
                        {
                            continue;
                        }
                        _handles.Add(
                            fullPath,
                            File.OpenHandle(
                                fullPath,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.ReadWrite | FileShare.Delete,
                                FileOptions.RandomAccess));
                    }
                    Span<byte> header = pcmHeader;
                    ReadExactly(entry.Key, 0, header);
                    long frameCount = AudioPcmCachePayload.ValidateHeader(
                        header,
                        new AudioFormat(
                            BinaryPrimitives.ReadInt32LittleEndian(header[8..]),
                            BinaryPrimitives.ReadInt32LittleEndian(header[12..]),
                            (AudioSampleFormat)BinaryPrimitives.ReadInt32LittleEndian(header[16..])));
                    long expectedLength = checked(
                        AudioPcmCachePayload.HeaderByteCount
                        + (frameCount * 2 * sizeof(float)));
                    if (expectedLength != entry.PayloadLength)
                    {
                        throw new InvalidDataException(
                            "A reusable Segment PCM payload length does not match its header.");
                    }
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public bool Contains(string key) => _entries.ContainsKey(key);

        public void ReadExactly(string key, long payloadOffset, Span<byte> destination)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(key, out ReusableAudioReadEntry? entry)
                || payloadOffset < 0
                || payloadOffset > entry.PayloadLength - destination.Length)
            {
                throw new InvalidDataException(
                    "A reusable-audio direct read is outside its manifest entry.");
            }

            int completed = 0;
            long position = payloadOffset;
            while (completed < destination.Length)
            {
                ReusableAudioReadExtent extent = FindExtent(entry.Extents, position);
                int relative = checked((int)(position - extent.LogicalOffset));
                int copied = Math.Min(
                    destination.Length - completed,
                    checked((int)(extent.PayloadLength - relative)));
                if (extent.Kind == ReusableAudioReadExtentKind.RawPayload)
                {
                    SafeFileHandle rawHandle = _handles[Path.GetFullPath(extent.Path)];
                    ReadExactly(
                        rawHandle,
                        destination.Slice(completed, copied),
                        checked(extent.FileOffset + relative));
                }
                else
                {
                    ReadAndValidatePackExtent(key, entry.PayloadLength, extent);
                    _blockBuffer.AsSpan(relative, copied).CopyTo(destination[completed..]);
                }
                completed += copied;
                position += copied;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (SafeFileHandle handle in _handles.Values)
            {
                handle.Dispose();
            }
            _handles.Clear();
        }

        private void ReadAndValidatePackExtent(
            string key,
            long totalPayloadLength,
            ReusableAudioReadExtent extent)
        {
            SafeFileHandle handle = _handles[Path.GetFullPath(extent.Path)];
            int payloadLength = checked((int)extent.PayloadLength);
            Span<byte> header = _headerBuffer;
            ReadExactly(handle, header, extent.FileOffset);
            byte[] keyBytes = Convert.FromHexString(key);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != EntryMagic
                || BinaryPrimitives.ReadInt32LittleEndian(header[4..]) != EntryVersion
                || BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != extent.BlockIndex
                || BinaryPrimitives.ReadInt32LittleEndian(header[12..]) != extent.BlockCount
                || BinaryPrimitives.ReadInt64LittleEndian(header[16..]) != totalPayloadLength
                || BinaryPrimitives.ReadInt32LittleEndian(header[24..]) != payloadLength
                || BinaryPrimitives.ReadInt32LittleEndian(header[28..]) != 0
                || !CryptographicOperations.FixedTimeEquals(header[32..64], keyBytes))
            {
                throw new InvalidDataException(
                    "A reusable-audio Pack block header is invalid.");
            }
            ReadExactly(
                handle,
                _blockBuffer.AsSpan(0, payloadLength),
                checked(extent.FileOffset + EntryHeaderSize));
            SHA256.HashData(
                _blockBuffer.AsSpan(0, payloadLength),
                _digestBuffer);
            if (!CryptographicOperations.FixedTimeEquals(_digestBuffer, header[64..96]))
            {
                throw new InvalidDataException(
                    "A reusable-audio Pack block checksum is invalid.");
            }
        }

        private static ReusableAudioReadExtent FindExtent(
            IReadOnlyList<ReusableAudioReadExtent> extents,
            long position)
        {
            int low = 0;
            int high = extents.Count;
            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                ReusableAudioReadExtent candidate = extents[middle];
                if (candidate.LogicalOffset + candidate.PayloadLength <= position)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            if (low >= extents.Count || position < extents[low].LogicalOffset)
            {
                throw new InvalidDataException(
                    "A reusable-audio manifest has a payload gap.");
            }
            return extents[low];
        }

        private static void ReadExactly(
            SafeFileHandle handle,
            Span<byte> destination,
            long fileOffset)
        {
            int completed = 0;
            while (completed < destination.Length)
            {
                int read = RandomAccess.Read(
                    handle,
                    destination[completed..],
                    checked(fileOffset + completed));
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        "A reusable-audio read extent ended early.");
                }
                completed += read;
            }
        }
    }

    private sealed unsafe class ReaderSlot : IDisposable
    {
        public ReaderSlot(nuint byteCount, CacheFragment[] fragments)
        {
            Fragments = fragments;
            FragmentsByPayloadOffset = SortByPayloadOffset(fragments);
            TotalStreamFrameCount = fragments[^1].StreamEndFrame;
            Buffer = (byte*)NativeMemory.Alloc(byteCount);
            if (Buffer is null)
            {
                throw new OutOfMemoryException("A Segment PCM read-ahead hot-set could not be allocated.");
            }
        }

        public byte* Buffer;
        public CacheFragment[] Fragments { get; }
        public CacheFragment[] FragmentsByPayloadOffset { get; }
        public long TotalStreamFrameCount { get; }
        public long RequestedStreamPosition;
        public int RequestVersion;
        public int ActiveRequestVersion;
        public long ConsumerPosition;
        public long ProducerPosition;

        public void Dispose()
        {
            if (Buffer != null)
            {
                NativeMemory.Free(Buffer);
                Buffer = null;
            }
        }
    }

    private sealed unsafe class WriterSlot : IDisposable
    {
        public WriterSlot(
            nuint byteCount,
            int capacityFrames,
            CacheFragment[] fragments,
            bool usePackJournal)
        {
            CapacityFrames = capacityFrames;
            Fragments = fragments;
            FragmentsByPayloadOffset = SortByPayloadOffset(fragments);
            JournalBlockBuffer = usePackJournal
                ? new byte[AudioCachePackStore.BlockPayloadBytes]
                : null;
            Buffer = (byte*)NativeMemory.Alloc(byteCount);
            if (Buffer is null)
            {
                throw new OutOfMemoryException("A Segment PCM writer hot-set could not be allocated.");
            }
        }

        public byte* Buffer;
        public int CapacityFrames { get; }
        public CacheFragment[] Fragments { get; }
        public CacheFragment[] FragmentsByPayloadOffset { get; }
        public byte[]? JournalBlockBuffer { get; }
        public string? ActiveJournalKey { get; set; }
        public int JournalBufferedBytes { get; set; }
        public int NextJournalBlockIndex { get; set; }
        public long ConsumerPosition;
        public long ProducerPosition;

        public void Dispose()
        {
            if (Buffer != null)
            {
                NativeMemory.Free(Buffer);
                Buffer = null;
            }
        }
    }
}
