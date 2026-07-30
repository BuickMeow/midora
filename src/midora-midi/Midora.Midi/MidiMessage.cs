using System.Runtime.CompilerServices;

namespace Midora.Midi;

public readonly struct MidiMessage
{
    public const uint StatusMask
        = 0b__0000_0000__0000_0000__0000_0000__1111_1111;

    public const uint MessageTypeMask
        = 0b__0000_0000__0000_0000__0000_0000__1111_0000;

    public const uint ChannelNumberMask
        = 0b__0000_0000__0000_0000__0000_0000__0000_1111;

    public const uint Data1Mask
        = 0b__0000_0000__0000_0000__1111_1111__0000_0000;

    public const int Data1MaskOffset
        = 8;

    public const uint Data2Mask
        = 0b__0000_0000__1111_1111__0000_0000__0000_0000;

    public const int Data2MaskOffset
        = 16;

    public const int MessageLengthOffset
        = 24;

    public static MidiMessage NoteOff(byte channelNumber, byte key, byte velocity = 0)
    {
        return new(channelNumber, MidiMessageType.NoteOff, key, velocity);
    }

    public static MidiMessage NoteOn(byte channelNumber, byte key, byte velocity)
    {
        return new(channelNumber, MidiMessageType.NoteOn, key, velocity);
    }

    public static MidiMessage PolyphonicKeyPressure(byte channelNumber, byte key, byte value)
    {
        return new(channelNumber, MidiMessageType.PolyphonicKeyPressure, key, value);
    }

    public static MidiMessage ControlChange(byte channelNumber, byte controllerNumber, byte value)
    {
        return new(channelNumber, MidiMessageType.ControlChange, controllerNumber, value);
    }

    public static MidiMessage ProgramChange(byte channelNumber, byte programNumber)
    {
        return new(channelNumber, MidiMessageType.ProgramChange, programNumber, 0);
    }

    public static MidiMessage ChannelPressure(byte channelNumber, byte value)
    {
        return new(channelNumber, MidiMessageType.ChannelPressure, value, 0);
    }

    public static MidiMessage PitchWheelChange(byte channelNumber, ushort valuePositive14Bit)
    {
        return new(channelNumber,
            MidiMessageType.PitchWheelChange,
            (byte)(valuePositive14Bit & 0b__0111_1111),
            (byte)((valuePositive14Bit >> 7) & 0b__0111_1111));
    }

    public static MidiMessage StartSystemExclusive()
    {
        return new(MidiSystemMessageType.StartSystemExclusive);
    }

    public static MidiMessage SongPositionPointer(ushort beats14Bit)
    {
        return new(MidiSystemMessageType.SongPositionPointer,
            (byte)(beats14Bit & 0b__0111_1111),
            (byte)((beats14Bit >> 7) & 0b__0111_1111));
    }

    public static MidiMessage SongSelect(byte sequence)
    {
        return new(MidiSystemMessageType.SongSelect, sequence);
    }

    public static MidiMessage TuneRequest()
    {
        return new(MidiSystemMessageType.TuneRequest);
    }

    public static MidiMessage EndofSystemExclusive()
    {
        return new(MidiSystemMessageType.EndofSystemExclusive);
    }

    public static MidiMessage TimingClock()
    {
        return new(MidiSystemMessageType.TimingClock);
    }

    public static MidiMessage Start()
    {
        return new(MidiSystemMessageType.Start);
    }

    public static MidiMessage Continue()
    {
        return new(MidiSystemMessageType.Continue);
    }

    public static MidiMessage Stop()
    {
        return new(MidiSystemMessageType.Stop);
    }

    public static MidiMessage ActiveSensing()
    {
        return new(MidiSystemMessageType.ActiveSensing);
    }

    public static MidiMessage Reset()
    {
        return new(MidiSystemMessageType.Reset);
    }

    public static MidiMessage SystemExclusiveContent(byte data)
    {
        return new(data);
    }

    private MidiMessage(byte channelNumber, MidiMessageType messageType, byte data1, byte data2)
    {
        _raw = (uint)channelNumber
            | (uint)messageType
            | (uint)(data1 << Data1MaskOffset)
            | (uint)(data2 << Data2MaskOffset)
            | (uint)(GetMessageLengthByMessageType(messageType) << MessageLengthOffset);
    }

    private MidiMessage(MidiSystemMessageType systemMessageType, byte data1 = 0, byte data2 = 0)
    {
        _raw = (uint)systemMessageType
            | (uint)(data1 << Data1MaskOffset)
            | (uint)(data2 << Data2MaskOffset)
            | (uint)(GetMessageLengthBySystemMessageType(systemMessageType) << MessageLengthOffset);
    }

    private MidiMessage(byte byte0)
    {
        _raw = byte0
            | (uint)(1 << MessageLengthOffset);
    }

    private readonly uint _raw;

    internal readonly uint Raw => _raw;
    internal readonly uint RawWithoutLength => _raw & 0b__0000_0000__1111_1111__1111_1111__1111_1111;
    public readonly bool IsSysExContent => (_raw & (0b__1000_0000)) == 0;
    public readonly MidiMessageType MessageType => (MidiMessageType)(_raw & MessageTypeMask);
    public readonly MidiSystemMessageType SystemMessageType => (MidiSystemMessageType)((byte)_raw);
    public readonly byte ChannelNumber => (byte)(_raw & ChannelNumberMask);
    public readonly byte Byte0 => (byte)_raw;
    public readonly byte Byte1 => (byte)((_raw & Data1Mask) >> Data1MaskOffset);
    public readonly byte Byte2 => (byte)((_raw & Data2Mask) >> Data2MaskOffset);
    public readonly ushort DataPositive14Bit => (ushort)((Byte1 & 0b__0111_1111) | ((Byte2 & 0b__0111_1111) << 7));
    public readonly int Length => (int)(_raw >> MessageLengthOffset);

    public readonly int WriteTo(Span<byte> destination)
    {
        int length = Length;

        switch (length)
        {
            case 0:
                break;
            case 1:
                destination[0] = (byte)_raw;
                break;
            case 2:
                destination[0] = (byte)_raw;
                destination[1] = (byte)(_raw >> 8);
                break;
            case 3:
                destination[0] = (byte)_raw;
                destination[1] = (byte)(_raw >> 8);
                destination[2] = (byte)(_raw >> 16);
                break;
            default:
                ThrowInvalidMidiLengthException(length);
                break;
        }

        return length;
    }

    public readonly int WriteTo(Span<byte> destination, int startIndex)
    {
        int length = Length;

        switch (length)
        {
            case 1:
                destination[0 + startIndex] = (byte)_raw;
                break;
            case 2:
                destination[0 + startIndex] = (byte)_raw;
                destination[1 + startIndex] = (byte)(_raw >> 8);
                break;
            case 3:
                destination[0 + startIndex] = (byte)_raw;
                destination[1 + startIndex] = (byte)(_raw >> 8);
                destination[2 + startIndex] = (byte)(_raw >> 16);
                break;
            default:
                ThrowInvalidMidiLengthException(length);
                break;
        }

        return length;
    }

    public readonly unsafe int WriteTo(void* destination)
    {
        int length = Length;

        switch (length)
        {
            case 1:
                *(byte*)destination = (byte)_raw;
                break;
            case 2:
                *(ushort*)destination = (ushort)_raw;
                break;
            case 3:
                *(byte*)destination = (byte)_raw;
                *(ushort*)((byte*)destination + 1) = (ushort)(_raw >> 8);
                break;
            default:
                ThrowInvalidMidiLengthException(length);
                break;
        }

        return length;
    }

    public readonly unsafe int WriteTo(byte* destination, nint startIndex)
    {
        int length = Length;

        destination += startIndex;

        switch (length)
        {
            case 1:
                *destination = (byte)_raw;
                break;
            case 2:
                *(ushort*)destination = (ushort)_raw;
                break;
            case 3:
                *destination = (byte)_raw;
                *(ushort*)(destination + 1) = (ushort)(_raw >> 8);
                break;
            default:
                ThrowInvalidMidiLengthException(length);
                break;
        }

        return length;
    }

    public readonly int WriteTo(Stream stream)
    {
        int length = Length;
        Span<byte> buffer = stackalloc byte[length];

        switch (length)
        {
            case 1:
                buffer[0] = (byte)_raw;
                break;
            case 2:
                buffer[0] = (byte)_raw;
                buffer[1] = (byte)(_raw >> 8);
                break;
            case 3:
                buffer[0] = (byte)_raw;
                buffer[1] = (byte)(_raw >> 8);
                buffer[2] = (byte)(_raw >> 16);
                break;
            default:
                ThrowInvalidMidiLengthException(length);
                break;
        }

        stream.Write(buffer);

        return length;
    }

    public readonly byte[] ToArray()
    {
        int length = Length;
        byte[] result = new byte[length];

        switch (length)
        {
            case 1:
                result[0] = (byte)_raw;
                break;
            case 2:
                result[0] = (byte)_raw;
                result[1] = (byte)(_raw >> 8);
                break;
            case 3:
                result[0] = (byte)_raw;
                result[1] = (byte)(_raw >> 8);
                result[2] = (byte)(_raw >> 16);
                break;
            default:
                ThrowInvalidMidiLengthException(length);
                break;
        }

        return result;
    }

    private static void ThrowInvalidMidiLengthException(int length)
    {
        throw new MidoraMidiException($"Invalid midi message length: {length}");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte GetMessageLengthByMessageType(MidiMessageType messageType)
    {
        return messageType switch
        {
            MidiMessageType.NoteOff => 3,
            MidiMessageType.NoteOn => 3,
            MidiMessageType.PolyphonicKeyPressure => 3,
            MidiMessageType.ControlChange => 3,
            MidiMessageType.ProgramChange => 2,
            MidiMessageType.ChannelPressure => 2,
            MidiMessageType.PitchWheelChange => 3,
            _ => 1
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte GetMessageLengthBySystemMessageType(MidiSystemMessageType systemMessageType)
    {
        return systemMessageType switch
        {
            MidiSystemMessageType.SongPositionPointer => 3,
            MidiSystemMessageType.SongSelect => 2,
            _ => 1
        };
    }
}
