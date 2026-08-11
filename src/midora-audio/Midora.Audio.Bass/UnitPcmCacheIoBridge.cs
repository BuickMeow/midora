using Microsoft.Win32.SafeHandles;
using Midora.AudioDevice;
using System.Runtime.InteropServices;

namespace Midora.Audio.Bass;

internal sealed unsafe class UnitPcmCacheIoBridge : IDisposable
{
    private const int ReaderCapacityFrames = 16_384;
    private const int WriterCapacityFrames = 16_384;
    private const int WriterFlushThresholdFrames = 4_096;
    private readonly FileStream _file;
    private readonly SafeFileHandle _handle;
    private readonly ReaderSlot?[] _readers = new ReaderSlot[16 * 16];
    private readonly WriterSlot?[] _writers = new WriterSlot[16 * 16];
    private readonly AudioFormat _format;
    private readonly Thread _thread;
    private int _completionRequested;
    private int _threadFinished;
    private Exception? _readFault;
    private Exception? _writeFault;
    private bool _disposed;

    public UnitPcmCacheIoBridge(string path, MidiRenderPlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(plan);
        string fullPath = Path.GetFullPath(path);
        _format = new(plan.SampleRate, 2, AudioSampleFormat.Float32);
        FileStream? file = null;
        try
        {
            List<MidiUnitFragmentRenderPlan>?[] hitFragments =
                new List<MidiUnitFragmentRenderPlan>?[16 * 16];
            List<MidiUnitFragmentRenderPlan>?[] missFragments =
                new List<MidiUnitFragmentRenderPlan>?[16 * 16];
            foreach (MidiUnitFragmentRenderPlan fragment in plan.UnitFragments)
            {
                if (fragment.PcmCacheKey is null)
                {
                    continue;
                }
                List<MidiUnitFragmentRenderPlan>?[] destination = fragment.PcmCacheHit
                    ? hitFragments
                    : missFragments;
                destination[fragment.CanonicalUnitNumber] ??= [];
                destination[fragment.CanonicalUnitNumber]!.Add(fragment);
            }
            for (int unit = 0; unit < hitFragments.Length; unit++)
            {
                if (hitFragments[unit] is { Count: > 0 } hits)
                {
                    _readers[unit] = new ReaderSlot(
                        checked((nuint)ReaderCapacityFrames * (nuint)_format.BytesPerFrame),
                        CreateCacheFragments(hits));
                }
                if (missFragments[unit] is { Count: > 0 } misses)
                {
                    _writers[unit] = new WriterSlot(
                        checked((nuint)WriterCapacityFrames * (nuint)_format.BytesPerFrame),
                        CreateCacheFragments(misses));
                }
            }
            _file = file = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite,
                bufferSize: 1,
                FileOptions.RandomAccess);
            _handle = _file.SafeFileHandle;
            for (int unit = 0; unit < _readers.Length; unit++)
            {
                PrimeReader(_readers[unit]);
            }
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "Midora Unit PCM Cache I/O",
                Priority = ThreadPriority.AboveNormal
            };
            _thread.Start();
        }
        catch
        {
            file?.Dispose();
            DisposeBuffers();
            throw;
        }
    }

    public bool ReadFaulted => Volatile.Read(ref _readFault) is not null;

    public bool WriteFaulted => Volatile.Read(ref _writeFault) is not null;

    public bool TryReadFrames(
        MidiUnitFragmentRenderPlan fragment,
        long globalStartFrame,
        float* destination,
        int frameCount)
    {
        if (!IsReadReady(fragment, globalStartFrame, frameCount))
        {
            return false;
        }
        ReaderSlot slot = _readers[fragment.CanonicalUnitNumber]!;
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
        MidiUnitFragmentRenderPlan fragment,
        long globalStartFrame,
        int frameCount)
    {
        ReaderSlot slot = _readers[fragment.CanonicalUnitNumber]
            ?? throw new InvalidOperationException("A cache-hit Unit has no read-ahead slot.");
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
        return Volatile.Read(ref slot.ProducerPosition) - streamStart >= frameCount;
    }

    public bool CanWriteFrames(
        MidiUnitFragmentRenderPlan fragment,
        long globalStartFrame,
        int frameCount)
    {
        if (WriteFaulted)
        {
            return true;
        }
        WriterSlot slot = _writers[fragment.CanonicalUnitNumber]
            ?? throw new InvalidOperationException("A cache-miss Unit has no writer slot.");
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
        return frameCount <= WriterCapacityFrames - (streamStart - consumed);
    }

    public bool TryQueueWrite(
        MidiUnitFragmentRenderPlan fragment,
        long globalStartFrame,
        float* source,
        int frameCount)
    {
        if (WriteFaulted)
        {
            return true;
        }
        if (source == null || frameCount < 0 || frameCount > WriterCapacityFrames
            || !CanWriteFrames(fragment, globalStartFrame, frameCount))
        {
            return false;
        }
        WriterSlot slot = _writers[fragment.CanonicalUnitNumber]!;
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
                _file.Flush(flushToDisk: true);
            }
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _writeFault, exception);
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
                new InvalidDataException("A Unit PCM writer position is outside its fragment schedule."));
            return false;
        }
        if (!force && available < WriterFlushThresholdFrames
            && produced < fragment.StreamEndFrame)
        {
            return false;
        }
        int index = (int)(consumed % WriterCapacityFrames);
        int frames = (int)Math.Min(
            Math.Min(available, WriterCapacityFrames - index),
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
                new InvalidDataException("A Unit PCM reader position is outside its fragment schedule."));
            return false;
        }
        int index = (int)(produced % ReaderCapacityFrames);
        int frames = (int)Math.Min(
            Math.Min(Math.Min(free, ReaderCapacityFrames - index),
                fragment.StreamEndFrame - produced),
            slot.TotalStreamFrameCount - produced);
        try
        {
            ReadExactly(
                new Span<byte>(
                    slot.Buffer + (index * _format.BytesPerFrame),
                    checked(frames * _format.BytesPerFrame)),
                checked(fragment.PayloadOffset
                    + AudioPcmCachePayload.HeaderByteCount
                    + ((produced - fragment.StreamStartFrame) * _format.BytesPerFrame)));
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
                throw new EndOfStreamException("A staged Unit PCM cache payload ended early.");
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
        int index = (int)(position % WriterCapacityFrames);
        int first = Math.Min(frameCount, WriterCapacityFrames - index);
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

    private void PrimeReader(ReaderSlot? slot)
    {
        if (slot is null)
        {
            return;
        }
        while (slot.ProducerPosition - Volatile.Read(ref slot.ConsumerPosition)
            < ReaderCapacityFrames
            && slot.ProducerPosition < slot.TotalStreamFrameCount)
        {
            if (!FillOneReader(slot))
            {
                if (ReadFaulted)
                {
                    throw new InvalidDataException(
                        "The initial Unit PCM cache read-ahead failed.",
                        Volatile.Read(ref _readFault));
                }
                break;
            }
        }
    }

    private static CacheFragment[] CreateCacheFragments(
        IEnumerable<MidiUnitFragmentRenderPlan> fragments)
    {
        long streamStart = 0;
        List<CacheFragment> result = [];
        foreach (MidiUnitFragmentRenderPlan fragment in fragments
            .OrderBy(value => value.StartFrame)
            .ThenBy(value => value.InstanceGroupId)
            .ThenBy(value => value.SubVoiceId))
        {
            long frameCount = fragment.EndFrame - fragment.StartFrame;
            result.Add(new(
                fragment.PcmCachePayloadOffset,
                streamStart,
                frameCount));
            streamStart = checked(streamStart + frameCount);
        }
        return result.ToArray();
    }

    private static bool TryResolveStreamPosition(
        CacheFragment[] fragmentsByPayloadOffset,
        MidiUnitFragmentRenderPlan fragment,
        long relativeStart,
        out long streamPosition)
    {
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
            if (sorted[i - 1].PayloadOffset == sorted[i].PayloadOffset)
            {
                throw new InvalidDataException(
                    "A Unit PCM cache schedule contains duplicate payload offsets.");
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
        long StreamStartFrame,
        long FrameCount)
    {
        public long StreamEndFrame => checked(StreamStartFrame + FrameCount);
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
                throw new OutOfMemoryException("A Unit PCM read-ahead hot-set could not be allocated.");
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
        public WriterSlot(nuint byteCount, CacheFragment[] fragments)
        {
            Fragments = fragments;
            FragmentsByPayloadOffset = SortByPayloadOffset(fragments);
            Buffer = (byte*)NativeMemory.Alloc(byteCount);
            if (Buffer is null)
            {
                throw new OutOfMemoryException("A Unit PCM writer hot-set could not be allocated.");
            }
        }

        public byte* Buffer;
        public CacheFragment[] Fragments { get; }
        public CacheFragment[] FragmentsByPayloadOffset { get; }
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
