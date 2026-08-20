using System.Buffers.Binary;

namespace Midora.Midi;

public readonly record struct StandardMidiFileStreamHeader(
    ushort Format,
    ushort TrackCount,
    int TicksPerQuarterNote,
    long FileByteCount);

public readonly record struct StreamedStandardMidiFileEvent(
    int SourceTrackIndex,
    long Tick,
    long Order,
    StandardMidiFileEventKind Kind,
    MidiMessage Message,
    byte Type,
    ReadOnlyMemory<byte> Data,
    int DataLength,
    long SourceByteOffset,
    bool PayloadWasRead);

public readonly record struct StandardMidiFileStreamTrackResult(
    int SourceTrackIndex,
    long EndTick,
    long EventCount,
    long ChunkByteCount);

public readonly record struct StandardMidiFileStreamResult(
    StandardMidiFileStreamHeader Header,
    long EventCount,
    long PayloadByteCount,
    IReadOnlyList<StandardMidiFileStreamTrackResult> Tracks);

public interface IStandardMidiFileStreamVisitor
{
    void OnHeader(StandardMidiFileStreamHeader header);
    void OnTrackStart(int sourceTrackIndex, long chunkByteCount);
    bool ShouldReadPayload(
        int sourceTrackIndex,
        StandardMidiFileEventKind kind,
        byte type,
        int payloadByteCount);
    void OnEvent(in StreamedStandardMidiFileEvent value);
    void OnTrackEnd(StandardMidiFileStreamTrackResult result);
}

public static partial class StandardMidiFile
{
    public static void ValidateType1(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        StandardMidiFileStreamResult result = ScanType0Or1(
            source,
            ValidationVisitor.Instance,
            cancellationToken);
        if (result.Header.Format != 1)
            throw new MidoraMidiException("The file is not an SMF Type 1 file.");
    }

    public static StandardMidiFileStreamResult ScanType0Or1(
        Stream source,
        IStandardMidiFileStreamVisitor visitor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(visitor);
        if (!source.CanRead || !source.CanSeek)
            throw new ArgumentException("Streaming SMF input must be readable and seekable.", nameof(source));
        source.Position = 0;
        StreamCursor cursor = new(source);
        Require(cursor, "MThd"u8);
        uint headerLength = UInt32(cursor);
        if (headerLength != 6)
            throw new MidoraMidiException($"SMF header length must be 6, but was {headerLength}.");
        ushort format = UInt16(cursor);
        ushort trackCount = UInt16(cursor);
        ushort division = UInt16(cursor);
        if (format is not 0 and not 1)
            throw new MidoraMidiException(
                $"Only SMF Format 0 and Format 1 are supported, but the source is Format {format}.");
        if (trackCount == 0 || format == 0 && trackCount != 1)
            throw new MidoraMidiException("The SMF header contains an invalid Track count.");
        if (division == 0 || (division & 0x8000) != 0)
            throw new MidoraMidiException(
                "Only a positive TPQN division in the range 1..32767 is supported; SMPTE division is not supported.");

        StandardMidiFileStreamHeader header = new(format, trackCount, division, source.Length);
        visitor.OnHeader(header);
        StandardMidiFileStreamTrackResult[] tracks = new StandardMidiFileStreamTrackResult[trackCount];
        long totalEventCount = 0;
        long totalPayloadBytes = 0;
        for (int trackIndex = 0; trackIndex < trackCount; trackIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Require(cursor, "MTrk"u8);
            uint chunkLength = UInt32(cursor);
            long trackEndOffset = checked(cursor.Position + chunkLength);
            if (trackEndOffset > source.Length)
                throw new MidoraMidiException(
                    $"SMF MTrk {trackIndex} length exceeds the remaining input bytes.");
            visitor.OnTrackStart(trackIndex, chunkLength);
            long tick = 0;
            long order = 0;
            long trackEventCount = 0;
            byte runningStatus = 0;
            bool sawEndOfTrack = false;
            while (cursor.Position < trackEndOffset)
            {
                if ((trackEventCount & 0x3fff) == 0) cancellationToken.ThrowIfCancellationRequested();
                long eventOffset = cursor.Position;
                int delta = Variable(cursor, trackEndOffset);
                tick = checked(tick + delta);
                byte first = Byte(cursor, trackEndOffset);
                byte status;
                byte? firstData = null;
                if (first < 0x80)
                {
                    if (runningStatus == 0)
                        throw new MidoraMidiException(
                            $"SMF MTrk {trackIndex} uses Running Status before a channel status at byte {eventOffset}.");
                    status = runningStatus;
                    firstData = first;
                }
                else
                {
                    status = first;
                    runningStatus = status is >= 0x80 and <= 0xef ? status : (byte)0;
                }

                if (status is >= 0x80 and <= 0xef)
                {
                    int dataCount = (status & 0xf0) is 0xc0 or 0xd0 ? 1 : 2;
                    byte data1 = firstData ?? DataByte(cursor, trackEndOffset, trackIndex, eventOffset);
                    byte data2 = dataCount == 2
                        ? DataByte(cursor, trackEndOffset, trackIndex, eventOffset)
                        : (byte)0;
                    uint packed = status
                        | ((uint)data1 << MidiMessage.Data1MaskOffset)
                        | ((uint)data2 << MidiMessage.Data2MaskOffset)
                        | ((uint)(dataCount + 1) << MidiMessage.MessageLengthOffset);
                    MidiMessage message;
                    try
                    {
                        message = MidiMessage.FromPackedValue(packed);
                    }
                    catch (ArgumentException exception)
                    {
                        throw new MidoraMidiException(
                            $"SMF MTrk {trackIndex} contains an invalid channel event at byte {eventOffset}: {exception.Message}");
                    }
                    StreamedStandardMidiFileEvent value = new(
                        trackIndex,
                        tick,
                        order++,
                        StandardMidiFileEventKind.ChannelVoice,
                        message,
                        0,
                        default,
                        0,
                        eventOffset,
                        false);
                    visitor.OnEvent(in value);
                    totalEventCount++;
                    trackEventCount++;
                    continue;
                }

                if (status == 0xff)
                {
                    byte type = Byte(cursor, trackEndOffset);
                    int length = Variable(cursor, trackEndOffset);
                    if (type == EndOfTrackMetaType)
                    {
                        if (length != 0 || sawEndOfTrack || cursor.Position != trackEndOffset)
                            throw new MidoraMidiException(
                                $"SMF MTrk {trackIndex} End Of Track must be one zero-length final event.");
                        sawEndOfTrack = true;
                        continue;
                    }
                    ReadOnlyMemory<byte> data = ReadPayload(
                        cursor,
                        trackEndOffset,
                        length,
                        trackIndex,
                        eventOffset,
                        visitor.ShouldReadPayload(
                            trackIndex,
                            StandardMidiFileEventKind.Meta,
                            type,
                            length),
                        out bool payloadWasRead);
                    StreamedStandardMidiFileEvent value = new(
                        trackIndex,
                        tick,
                        order++,
                        StandardMidiFileEventKind.Meta,
                        default,
                        type,
                        data,
                        length,
                        eventOffset,
                        payloadWasRead);
                    visitor.OnEvent(in value);
                    totalEventCount++;
                    trackEventCount++;
                    totalPayloadBytes = checked(totalPayloadBytes + length);
                    continue;
                }

                if (status is 0xf0 or 0xf7)
                {
                    int length = Variable(cursor, trackEndOffset);
                    ReadOnlyMemory<byte> data = ReadPayload(
                        cursor,
                        trackEndOffset,
                        length,
                        trackIndex,
                        eventOffset,
                        visitor.ShouldReadPayload(
                            trackIndex,
                            StandardMidiFileEventKind.SystemExclusive,
                            status,
                            length),
                        out bool payloadWasRead);
                    StreamedStandardMidiFileEvent value = new(
                        trackIndex,
                        tick,
                        order++,
                        StandardMidiFileEventKind.SystemExclusive,
                        default,
                        status,
                        data,
                        length,
                        eventOffset,
                        payloadWasRead);
                    visitor.OnEvent(in value);
                    totalEventCount++;
                    trackEventCount++;
                    totalPayloadBytes = checked(totalPayloadBytes + length);
                    continue;
                }

                throw new MidoraMidiException(
                    $"SMF MTrk {trackIndex} contains unsupported status 0x{status:x2} at byte {eventOffset}.");
            }
            if (!sawEndOfTrack)
                throw new MidoraMidiException($"SMF MTrk {trackIndex} has no End Of Track event.");
            StandardMidiFileStreamTrackResult trackResult = new(
                trackIndex,
                tick,
                trackEventCount,
                chunkLength);
            tracks[trackIndex] = trackResult;
            visitor.OnTrackEnd(trackResult);
        }
        if (cursor.Position != source.Length)
            throw new MidoraMidiException("Unexpected bytes follow the declared SMF tracks.");
        return new(header, totalEventCount, totalPayloadBytes, tracks);
    }

    private sealed class ValidationVisitor : IStandardMidiFileStreamVisitor
    {
        public static ValidationVisitor Instance { get; } = new();
        public void OnHeader(StandardMidiFileStreamHeader header) { }
        public void OnTrackStart(int sourceTrackIndex, long chunkByteCount) { }
        public bool ShouldReadPayload(
            int sourceTrackIndex,
            StandardMidiFileEventKind kind,
            byte type,
            int payloadByteCount) => false;
        public void OnEvent(in StreamedStandardMidiFileEvent value) { }
        public void OnTrackEnd(StandardMidiFileStreamTrackResult result) { }
    }

    private static ReadOnlyMemory<byte> ReadPayload(
        StreamCursor cursor,
        long trackEndOffset,
        int length,
        int trackIndex,
        long eventOffset,
        bool shouldRead,
        out bool payloadWasRead)
    {
        if (length < 0 || cursor.Position > trackEndOffset - length)
            throw new MidoraMidiException(
                $"SMF MTrk {trackIndex} event payload is truncated at byte {eventOffset}.");
        payloadWasRead = shouldRead;
        if (!shouldRead)
        {
            cursor.Skip(length);
            return default;
        }
        byte[] result = new byte[length];
        cursor.ReadExactly(result);
        return result;
    }

    private static byte DataByte(
        StreamCursor cursor,
        long trackEndOffset,
        int trackIndex,
        long eventOffset)
    {
        byte value = Byte(cursor, trackEndOffset);
        if (value >= 0x80)
            throw new MidoraMidiException(
                $"SMF MTrk {trackIndex} channel data is not 7-bit at byte {eventOffset}.");
        return value;
    }

    private static int Variable(StreamCursor cursor, long maximumPosition)
    {
        int value = 0;
        for (int index = 0; index < 4; index++)
        {
            byte item = Byte(cursor, maximumPosition);
            value = checked((value << 7) | (item & 0x7f));
            if ((item & 0x80) == 0) return value;
        }
        throw new MidoraMidiException("SMF variable-length quantity exceeds four bytes.");
    }

    private static void Require(StreamCursor cursor, ReadOnlySpan<byte> expected)
    {
        Span<byte> actual = stackalloc byte[4];
        if (expected.Length != actual.Length)
            throw new ArgumentOutOfRangeException(nameof(expected));
        cursor.ReadExactly(actual);
        if (!actual.SequenceEqual(expected))
            throw new MidoraMidiException(
                $"Expected SMF chunk identifier '{System.Text.Encoding.ASCII.GetString(expected)}'.");
    }

    private static ushort UInt16(StreamCursor cursor)
    {
        Span<byte> bytes = stackalloc byte[2];
        cursor.ReadExactly(bytes);
        return BinaryPrimitives.ReadUInt16BigEndian(bytes);
    }

    private static uint UInt32(StreamCursor cursor)
    {
        Span<byte> bytes = stackalloc byte[4];
        cursor.ReadExactly(bytes);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    private static byte Byte(StreamCursor cursor, long maximumPosition)
    {
        if (cursor.Position >= maximumPosition)
            throw new MidoraMidiException("SMF input is truncated.");
        return cursor.ReadByte();
    }

    private sealed class StreamCursor
    {
        private readonly Stream _source;
        private readonly byte[] _buffer = new byte[64 * 1024];
        private int _bufferOffset;
        private int _bufferCount;

        public StreamCursor(Stream source) => _source = source;

        public long Position { get; private set; }

        public byte ReadByte()
        {
            if (_bufferOffset == _bufferCount) Fill();
            Position++;
            return _buffer[_bufferOffset++];
        }

        public void ReadExactly(Span<byte> destination)
        {
            int written = 0;
            while (written < destination.Length)
            {
                if (_bufferOffset == _bufferCount) Fill();
                int copy = Math.Min(destination.Length - written, _bufferCount - _bufferOffset);
                _buffer.AsSpan(_bufferOffset, copy).CopyTo(destination[written..]);
                _bufferOffset += copy;
                written += copy;
                Position += copy;
            }
        }

        public void Skip(int byteCount)
        {
            if (byteCount < 0) throw new ArgumentOutOfRangeException(nameof(byteCount));
            int remaining = byteCount;
            while (remaining != 0)
            {
                if (_bufferOffset == _bufferCount) Fill();
                int skip = Math.Min(remaining, _bufferCount - _bufferOffset);
                _bufferOffset += skip;
                remaining -= skip;
                Position += skip;
            }
        }

        private void Fill()
        {
            _bufferCount = _source.Read(_buffer);
            _bufferOffset = 0;
            if (_bufferCount == 0) throw new MidoraMidiException("SMF input is truncated.");
        }
    }
}
