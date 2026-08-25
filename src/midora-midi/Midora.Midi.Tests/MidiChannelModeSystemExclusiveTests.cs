namespace Midora.Midi.Tests;

public sealed class MidiChannelModeSystemExclusiveTests
{
    [Theory]
    [InlineData(0x10, 9)]
    [InlineData(0x11, 0)]
    [InlineData(0x19, 8)]
    [InlineData(0x1a, 10)]
    [InlineData(0x1f, 15)]
    public void ParsesAndRetargetsRolandGsPartMode(byte partAddress, byte channel)
    {
        byte mode = 1;
        byte checksum = checked((byte)((128 - ((0x40 + partAddress + 0x15 + mode) & 0x7f)) & 0x7f));
        byte[] payload = [0x41, 0x10, 0x42, 0x12, 0x40, partAddress, 0x15, mode, checksum, 0xf7];

        Assert.True(MidiChannelModeSystemExclusive.TryParseF0Payload(payload, out var value));
        Assert.Equal(channel, value.TargetChannel);
        Assert.True(value.IsPercussion);

        Span<byte> normalized = stackalloc byte[MidiChannelModeSystemExclusive.MaximumEncodedByteCount];
        int length = value.WriteNormalizedForChannel(0, normalized);
        Assert.Equal(
            new byte[] { 0xf0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x11, 0x15, 0x01, 0x19, 0xf7 },
            normalized[..length].ToArray());
    }

    [Fact]
    public void ParsesAndRetargetsYamahaXgPartMode()
    {
        byte[] payload = [0x43, 0x10, 0x4c, 0x08, 0x09, 0x07, 0x00, 0xf7];

        Assert.True(MidiChannelModeSystemExclusive.TryParseF0Payload(payload, out var value));
        Assert.Equal((byte)9, value.TargetChannel);
        Assert.False(value.IsPercussion);

        Span<byte> normalized = stackalloc byte[MidiChannelModeSystemExclusive.MaximumEncodedByteCount];
        int length = value.WriteNormalizedForChannel(0, normalized);
        Assert.Equal(
            new byte[] { 0xf0, 0x43, 0x10, 0x4c, 0x08, 0x00, 0x07, 0x00, 0xf7 },
            normalized[..length].ToArray());
    }

    [Fact]
    public void RejectsUnrelatedOrInvalidSystemExclusive()
    {
        Assert.False(MidiChannelModeSystemExclusive.TryParseF0Payload(
            [0x7e, 0x7f, 0x09, 0x01, 0xf7], out _));
        Assert.False(MidiChannelModeSystemExclusive.TryParseF0Payload(
            [0x41, 0x10, 0x42, 0x12, 0x40, 0x10, 0x15, 0x01, 0x00, 0xf7], out _));
    }
}
