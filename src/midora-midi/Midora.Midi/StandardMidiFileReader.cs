using System.Buffers.Binary;

namespace Midora.Midi;

public readonly record struct ParsedStandardMidiFileEvent(
    long Tick,
    long Order,
    StandardMidiFileEventKind Kind,
    MidiMessage Message,
    byte Type,
    ReadOnlyMemory<byte> Data,
    int SourceByteOffset);

public sealed class ParsedStandardMidiFileTrack
{
    internal ParsedStandardMidiFileTrack(
        int sourceTrackIndex,
        long endTick,
        ParsedStandardMidiFileEvent[] events)
    {
        SourceTrackIndex = sourceTrackIndex;
        EndTick = endTick;
        Events = events;
    }

    public int SourceTrackIndex { get; }
    public long EndTick { get; }
    public IReadOnlyList<ParsedStandardMidiFileEvent> Events { get; }
}

public sealed class ParsedStandardMidiFile
{
    internal ParsedStandardMidiFile(
        ushort format,
        int ticksPerQuarterNote,
        ParsedStandardMidiFileTrack[] tracks)
    {
        Format = format;
        TicksPerQuarterNote = ticksPerQuarterNote;
        Tracks = tracks;
    }

    public ushort Format { get; }
    public int TicksPerQuarterNote { get; }
    public IReadOnlyList<ParsedStandardMidiFileTrack> Tracks { get; }
}

public static partial class StandardMidiFile
{
    public const int MaximumImportFileByteCount = 512 * 1024 * 1024;
    public const int MaximumImportEventCount = 16 * 1024 * 1024;
    public const int MaximumImportEventPayloadByteCount = 256 * 1024 * 1024;

    public static ParsedStandardMidiFile ParseType0Or1(ReadOnlySpan<byte> file)
    {
        if (file.Length > MaximumImportFileByteCount)
        {
            throw new MidoraMidiException(
                $"SMF input exceeds the bounded {MaximumImportFileByteCount}-byte admission limit.");
        }

        int position = 0;
        Require(file, ref position, "MThd"u8);
        uint headerLength = UInt32(file, ref position);
        if (headerLength != 6)
        {
            throw new MidoraMidiException($"SMF header length must be 6, but was {headerLength}.");
        }
        ushort format = UInt16(file, ref position);
        ushort trackCount = UInt16(file, ref position);
        ushort division = UInt16(file, ref position);
        if (format is not 0 and not 1)
        {
            throw new MidoraMidiException(
                $"Only SMF Format 0 and Format 1 are supported, but the source is Format {format}.");
        }
        if (trackCount == 0 || format == 0 && trackCount != 1)
        {
            throw new MidoraMidiException("The SMF header contains an invalid Track count.");
        }
        if (division == 0 || (division & 0x8000) != 0)
        {
            throw new MidoraMidiException(
                "Only a positive TPQN division in the range 1..32767 is supported; SMPTE division is not supported.");
        }

        ParsedStandardMidiFileTrack[] tracks = new ParsedStandardMidiFileTrack[trackCount];
        int totalEventCount = 0;
        int totalPayloadBytes = 0;
        for (int trackIndex = 0; trackIndex < trackCount; trackIndex++)
        {
            Require(file, ref position, "MTrk"u8);
            uint chunkLength = UInt32(file, ref position);
            if (chunkLength > int.MaxValue || position > file.Length - checked((int)chunkLength))
            {
                throw new MidoraMidiException(
                    $"SMF MTrk {trackIndex} length exceeds the remaining input bytes.");
            }
            int trackEndOffset = checked(position + (int)chunkLength);
            ReadOnlySpan<byte> trackBytes = file[..trackEndOffset];
            List<ParsedStandardMidiFileEvent> events = [];
            long tick = 0;
            long order = 0;
            byte runningStatus = 0;
            bool sawEndOfTrack = false;
            while (position < trackEndOffset)
            {
                int eventOffset = position;
                int delta = Variable(trackBytes, ref position);
                tick = checked(tick + delta);
                byte first = Byte(trackBytes, ref position);
                byte status;
                byte? firstData = null;
                if (first < 0x80)
                {
                    if (runningStatus == 0)
                    {
                        throw new MidoraMidiException(
                            $"SMF MTrk {trackIndex} uses Running Status before a channel status at byte {eventOffset}.");
                    }
                    status = runningStatus;
                    firstData = first;
                }
                else
                {
                    status = first;
                    if (status is >= 0x80 and <= 0xef)
                    {
                        runningStatus = status;
                    }
                    else
                    {
                        // Meta and SysEx do not carry Running Status into the next event.
                        runningStatus = 0;
                    }
                }

                if (status is >= 0x80 and <= 0xef)
                {
                    int dataCount = (status & 0xf0) is 0xc0 or 0xd0 ? 1 : 2;
                    byte data1 = firstData ?? DataByte(trackBytes, ref position, trackIndex, eventOffset);
                    byte data2 = dataCount == 2
                        ? DataByte(trackBytes, ref position, trackIndex, eventOffset)
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
                    AddParsedEvent(events, ref totalEventCount, ref totalPayloadBytes, new(
                        tick,
                        order++,
                        StandardMidiFileEventKind.ChannelVoice,
                        message,
                        0,
                        default,
                        eventOffset));
                    continue;
                }

                if (status == 0xff)
                {
                    byte type = Byte(trackBytes, ref position);
                    int length = Variable(trackBytes, ref position);
                    byte[] payload = Payload(trackBytes, ref position, length, trackIndex, eventOffset);
                    if (type == EndOfTrackMetaType)
                    {
                        if (length != 0 || sawEndOfTrack || position != trackEndOffset)
                        {
                            throw new MidoraMidiException(
                                $"SMF MTrk {trackIndex} End Of Track must be one zero-length final event.");
                        }
                        sawEndOfTrack = true;
                        continue;
                    }
                    AddParsedEvent(events, ref totalEventCount, ref totalPayloadBytes, new(
                        tick,
                        order++,
                        StandardMidiFileEventKind.Meta,
                        default,
                        type,
                        payload,
                        eventOffset));
                    continue;
                }

                if (status is 0xf0 or 0xf7)
                {
                    int length = Variable(trackBytes, ref position);
                    byte[] payload = Payload(trackBytes, ref position, length, trackIndex, eventOffset);
                    AddParsedEvent(events, ref totalEventCount, ref totalPayloadBytes, new(
                        tick,
                        order++,
                        StandardMidiFileEventKind.SystemExclusive,
                        default,
                        status,
                        payload,
                        eventOffset));
                    continue;
                }

                throw new MidoraMidiException(
                    $"SMF MTrk {trackIndex} contains unsupported status 0x{status:x2} at byte {eventOffset}.");
            }
            if (!sawEndOfTrack)
            {
                throw new MidoraMidiException($"SMF MTrk {trackIndex} has no End Of Track event.");
            }
            tracks[trackIndex] = new(trackIndex, tick, events.ToArray());
        }
        if (position != file.Length)
        {
            throw new MidoraMidiException("Unexpected bytes follow the declared SMF tracks.");
        }
        return new(format, division, tracks);
    }

    private static void AddParsedEvent(
        ICollection<ParsedStandardMidiFileEvent> events,
        ref int totalEventCount,
        ref int totalPayloadBytes,
        ParsedStandardMidiFileEvent value)
    {
        totalEventCount++;
        totalPayloadBytes = checked(totalPayloadBytes + value.Data.Length);
        if (totalEventCount > MaximumImportEventCount
            || totalPayloadBytes > MaximumImportEventPayloadByteCount)
        {
            throw new MidoraMidiException(
                "SMF input exceeds the bounded event-count or event-payload admission limit.");
        }
        events.Add(value);
    }

    private static byte[] Payload(
        ReadOnlySpan<byte> source,
        ref int position,
        int length,
        int trackIndex,
        int eventOffset)
    {
        if (length < 0 || position > source.Length - length)
        {
            throw new MidoraMidiException(
                $"SMF MTrk {trackIndex} event payload is truncated at byte {eventOffset}.");
        }
        byte[] result = source.Slice(position, length).ToArray();
        position += length;
        return result;
    }

    private static byte DataByte(
        ReadOnlySpan<byte> source,
        ref int position,
        int trackIndex,
        int eventOffset)
    {
        byte value = Byte(source, ref position);
        if (value >= 0x80)
        {
            throw new MidoraMidiException(
                $"SMF MTrk {trackIndex} channel data is not 7-bit at byte {eventOffset}.");
        }
        return value;
    }

    private static int Variable(ReadOnlySpan<byte> source, ref int position)
    {
        int value = 0;
        for (int index = 0; index < 4; index++)
        {
            byte item = Byte(source, ref position);
            value = checked((value << 7) | (item & 0x7f));
            if ((item & 0x80) == 0)
            {
                return value;
            }
        }
        throw new MidoraMidiException("SMF variable-length quantity exceeds four bytes.");
    }

    private static void Require(ReadOnlySpan<byte> source, ref int position, ReadOnlySpan<byte> expected)
    {
        if (position > source.Length - expected.Length
            || !source.Slice(position, expected.Length).SequenceEqual(expected))
        {
            throw new MidoraMidiException(
                $"Expected SMF chunk identifier '{System.Text.Encoding.ASCII.GetString(expected)}'.");
        }
        position += expected.Length;
    }

    private static ushort UInt16(ReadOnlySpan<byte> source, ref int position)
    {
        if (position > source.Length - 2)
        {
            throw new MidoraMidiException("SMF input is truncated.");
        }
        ushort value = BinaryPrimitives.ReadUInt16BigEndian(source[position..]);
        position += 2;
        return value;
    }

    private static uint UInt32(ReadOnlySpan<byte> source, ref int position)
    {
        if (position > source.Length - 4)
        {
            throw new MidoraMidiException("SMF input is truncated.");
        }
        uint value = BinaryPrimitives.ReadUInt32BigEndian(source[position..]);
        position += 4;
        return value;
    }

    private static byte Byte(ReadOnlySpan<byte> source, ref int position)
    {
        if ((uint)position >= (uint)source.Length)
        {
            throw new MidoraMidiException("SMF input is truncated.");
        }
        return source[position++];
    }
}
