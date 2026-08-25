namespace Midora.Midi;

public enum MidiChannelModeSystemExclusiveKind : byte
{
    RolandGsPartMode = 1,
    YamahaXgPartMode = 2
}

public readonly record struct MidiChannelModeSystemExclusive(
    MidiChannelModeSystemExclusiveKind Kind,
    byte TargetChannel,
    byte DeviceId,
    byte ModeValue)
{
    public const int MaximumEncodedByteCount = 11;

    public bool IsPercussion => ModeValue != 0;

    public static bool TryParseF0Payload(
        ReadOnlySpan<byte> payload,
        out MidiChannelModeSystemExclusive value)
    {
        if (TryParseRolandGs(payload, out value)
            || TryParseYamahaXg(payload, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    public int WriteNormalizedForChannel(byte targetChannel, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetChannel, (byte)15);
        if (destination.Length < MaximumEncodedByteCount)
            throw new ArgumentException("The destination is too small.", nameof(destination));

        return Kind switch
        {
            MidiChannelModeSystemExclusiveKind.RolandGsPartMode =>
                WriteRolandGs(targetChannel, destination),
            MidiChannelModeSystemExclusiveKind.YamahaXgPartMode =>
                WriteYamahaXg(targetChannel, destination),
            _ => throw new InvalidOperationException("The channel-mode SysEx kind is invalid.")
        };
    }

    private static bool TryParseRolandGs(
        ReadOnlySpan<byte> payload,
        out MidiChannelModeSystemExclusive value)
    {
        if (payload.Length != 10
            || payload[0] != 0x41
            || payload[1] is < 0x10 or > 0x1f
            || payload[2] != 0x42
            || payload[3] != 0x12
            || payload[4] != 0x40
            || payload[5] is < 0x10 or > 0x1f
            || payload[6] != 0x15
            || payload[7] > 2
            || payload[8] != RolandChecksum(payload[4], payload[5], payload[6], payload[7])
            || payload[9] != 0xf7)
        {
            value = default;
            return false;
        }

        value = new(
            MidiChannelModeSystemExclusiveKind.RolandGsPartMode,
            ChannelFromRolandPartAddress(payload[5]),
            payload[1],
            payload[7]);
        return true;
    }

    private static bool TryParseYamahaXg(
        ReadOnlySpan<byte> payload,
        out MidiChannelModeSystemExclusive value)
    {
        if (payload.Length != 8
            || payload[0] != 0x43
            || payload[1] is < 0x10 or > 0x1f
            || payload[2] != 0x4c
            || payload[3] != 0x08
            || payload[4] > 15
            || payload[5] != 0x07
            || payload[6] > 2
            || payload[7] != 0xf7)
        {
            value = default;
            return false;
        }

        value = new(
            MidiChannelModeSystemExclusiveKind.YamahaXgPartMode,
            payload[4],
            payload[1],
            payload[6]);
        return true;
    }

    private int WriteRolandGs(byte targetChannel, Span<byte> destination)
    {
        byte partAddress = RolandPartAddressFromChannel(targetChannel);
        destination[0] = 0xf0;
        destination[1] = 0x41;
        destination[2] = DeviceId;
        destination[3] = 0x42;
        destination[4] = 0x12;
        destination[5] = 0x40;
        destination[6] = partAddress;
        destination[7] = 0x15;
        destination[8] = ModeValue;
        destination[9] = RolandChecksum(0x40, partAddress, 0x15, ModeValue);
        destination[10] = 0xf7;
        return 11;
    }

    private int WriteYamahaXg(byte targetChannel, Span<byte> destination)
    {
        destination[0] = 0xf0;
        destination[1] = 0x43;
        destination[2] = DeviceId;
        destination[3] = 0x4c;
        destination[4] = 0x08;
        destination[5] = targetChannel;
        destination[6] = 0x07;
        destination[7] = ModeValue;
        destination[8] = 0xf7;
        return 9;
    }

    private static byte ChannelFromRolandPartAddress(byte address)
    {
        int part = address & 0x0f;
        return checked((byte)(part switch
        {
            0 => 9,
            <= 9 => part - 1,
            _ => part
        }));
    }

    private static byte RolandPartAddressFromChannel(byte channel) =>
        checked((byte)(0x10 | (channel switch
        {
            9 => 0,
            < 9 => channel + 1,
            _ => channel
        })));

    private static byte RolandChecksum(byte address1, byte address2, byte address3, byte data) =>
        checked((byte)((128 - ((address1 + address2 + address3 + data) & 0x7f)) & 0x7f));
}
