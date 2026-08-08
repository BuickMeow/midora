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
        try
        {
            bool[] hitUnits = new bool[16 * 16];
            bool[] missUnits = new bool[16 * 16];
            foreach (MidiUnitFragmentRenderPlan fragment in plan.UnitFragments)
            {
                hitUnits[fragment.CanonicalUnitNumber] |= fragment.PcmCacheHit;
                missUnits[fragment.CanonicalUnitNumber] |=
                    fragment.PcmCacheKey is not null && !fragment.PcmCacheHit;
            }
            for (int unit = 0; unit < hitUnits.Length; unit++)
            {
                if (hitUnits[unit])
                {
                    _readers[unit] = new ReaderSlot(
                        checked((nuint)ReaderCapacityFrames * (nuint)_format.BytesPerFrame));
                }
                if (missUnits[unit])
                {
                    _writers[unit] = new WriterSlot(
                        checked((nuint)WriterCapacityFrames * (nuint)_format.BytesPerFrame));
                }
            }
            _file = new FileStream(
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
                Name = "Midora Unit PCM Cache I/O",
                Priority = ThreadPriority.BelowNormal
            };
            _thread.Start();
        }
        catch
        {
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
        CopyFromReaderRing(slot, destination, relativeStart, frameCount);
        Volatile.Write(ref slot.ConsumerPosition, relativeStart + frameCount);
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
        long payloadOffset = checked(
            fragment.PcmCachePayloadOffset + AudioPcmCachePayload.HeaderByteCount);
        if (Volatile.Read(ref slot.RequestedPayloadOffset) != payloadOffset)
        {
            Volatile.Write(ref slot.RequestedStartFrame, relativeStart);
            Volatile.Write(ref slot.RequestedTotalFrameCount, fragment.EndFrame - fragment.StartFrame);
            Volatile.Write(ref slot.RequestedPayloadOffset, payloadOffset);
        }
        return Volatile.Read(ref slot.ReadyPayloadOffset) == payloadOffset
            && Volatile.Read(ref slot.ConsumerPosition) == relativeStart
            && Volatile.Read(ref slot.ProducerPosition) - relativeStart >= frameCount;
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
        long payloadOffset = checked(
            fragment.PcmCachePayloadOffset + AudioPcmCachePayload.HeaderByteCount);
        if (Volatile.Read(ref slot.RequestedPayloadOffset) != payloadOffset)
        {
            Volatile.Write(ref slot.RequestedStartFrame, relativeStart);
            Volatile.Write(ref slot.RequestedTotalFrameCount, fragment.EndFrame - fragment.StartFrame);
            Volatile.Write(ref slot.RequestedPayloadOffset, payloadOffset);
        }
        if (Volatile.Read(ref slot.ReadyPayloadOffset) != payloadOffset
            || slot.ProducerPosition != relativeStart)
        {
            return false;
        }
        long consumed = Volatile.Read(ref slot.ConsumerPosition);
        return frameCount <= WriterCapacityFrames - (relativeStart - consumed);
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
        long produced = slot.ProducerPosition;
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
                for (int i = 0; i < _writers.Length; i++)
                {
                    progressed |= DrainOneWriter(_writers[i]);
                }
                for (int i = 0; i < _readers.Length; i++)
                {
                    progressed |= FillOneReader(_readers[i]);
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
        long requestedOffset = Volatile.Read(ref slot.RequestedPayloadOffset);
        if (requestedOffset >= 0 && slot.ActivePayloadOffset != requestedOffset)
        {
            long oldConsumed = slot.ConsumerPosition;
            long oldProduced = Volatile.Read(ref slot.ProducerPosition);
            if (slot.ActivePayloadOffset >= 0 && oldConsumed != oldProduced)
            {
                return WriteAvailableFrames(slot, force: true);
            }
            Volatile.Write(ref slot.ReadyPayloadOffset, -1);
            long start = Volatile.Read(ref slot.RequestedStartFrame);
            slot.ActivePayloadOffset = requestedOffset;
            slot.TotalFrameCount = Volatile.Read(ref slot.RequestedTotalFrameCount);
            slot.ConsumerPosition = start;
            slot.ProducerPosition = start;
            Volatile.Write(ref slot.ReadyPayloadOffset, requestedOffset);
        }
        return WriteAvailableFrames(
            slot,
            force: Volatile.Read(ref _completionRequested) != 0
                || requestedOffset != slot.ActivePayloadOffset);
    }

    private bool WriteAvailableFrames(WriterSlot slot, bool force)
    {
        long consumed = slot.ConsumerPosition;
        long produced = Volatile.Read(ref slot.ProducerPosition);
        long available = produced - consumed;
        if (available == 0
            || !force && available < WriterFlushThresholdFrames
                && produced != slot.TotalFrameCount)
        {
            return false;
        }
        int index = (int)(consumed % WriterCapacityFrames);
        int frames = (int)Math.Min(available, WriterCapacityFrames - index);
        try
        {
            RandomAccess.Write(
                _handle,
                new ReadOnlySpan<byte>(
                    slot.Buffer + (index * _format.BytesPerFrame),
                    checked(frames * _format.BytesPerFrame)),
                checked(slot.ActivePayloadOffset + (consumed * _format.BytesPerFrame)));
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
        long requestedOffset = Volatile.Read(ref slot.RequestedPayloadOffset);
        if (requestedOffset < 0)
        {
            return false;
        }
        if (slot.ActivePayloadOffset != requestedOffset)
        {
            Volatile.Write(ref slot.ReadyPayloadOffset, -1);
            long start = Volatile.Read(ref slot.RequestedStartFrame);
            slot.ActivePayloadOffset = requestedOffset;
            slot.TotalFrameCount = Volatile.Read(ref slot.RequestedTotalFrameCount);
            Volatile.Write(ref slot.ConsumerPosition, start);
            slot.ProducerPosition = start;
            Volatile.Write(ref slot.ReadyPayloadOffset, requestedOffset);
        }

        long consumed = Volatile.Read(ref slot.ConsumerPosition);
        long produced = slot.ProducerPosition;
        int free = checked((int)(ReaderCapacityFrames - (produced - consumed)));
        if (free == 0 || produced == slot.TotalFrameCount)
        {
            return false;
        }
        int index = (int)(produced % ReaderCapacityFrames);
        int frames = (int)Math.Min(
            Math.Min(free, ReaderCapacityFrames - index),
            slot.TotalFrameCount - produced);
        try
        {
            ReadExactly(
                new Span<byte>(
                    slot.Buffer + (index * _format.BytesPerFrame),
                    checked(frames * _format.BytesPerFrame)),
                checked(requestedOffset + (produced * _format.BytesPerFrame)));
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

    private sealed unsafe class ReaderSlot : IDisposable
    {
        public ReaderSlot(nuint byteCount)
        {
            Buffer = (byte*)NativeMemory.Alloc(byteCount);
            if (Buffer is null)
            {
                throw new OutOfMemoryException("A Unit PCM read-ahead hot-set could not be allocated.");
            }
        }

        public byte* Buffer;
        public long RequestedPayloadOffset = -1;
        public long RequestedStartFrame;
        public long RequestedTotalFrameCount;
        public long ReadyPayloadOffset = -1;
        public long ActivePayloadOffset = -1;
        public long TotalFrameCount;
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
        public WriterSlot(nuint byteCount)
        {
            Buffer = (byte*)NativeMemory.Alloc(byteCount);
            if (Buffer is null)
            {
                throw new OutOfMemoryException("A Unit PCM writer hot-set could not be allocated.");
            }
        }

        public byte* Buffer;
        public long RequestedPayloadOffset = -1;
        public long RequestedStartFrame;
        public long RequestedTotalFrameCount;
        public long ReadyPayloadOffset = -1;
        public long ActivePayloadOffset = -1;
        public long TotalFrameCount;
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
