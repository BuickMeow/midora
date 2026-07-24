namespace Midora.Midi.Tests;

public unsafe class MidiWireChannelMessageTest
{
    [Fact]
    public void ShouldCreateCorrectly()
    {
        MidiMessage m1 = MidiMessage.ControlChange(4, 3, 127);
        MidiMessage m2 = MidiMessage.NoteOn(2, 63, 100);
        MidiMessage m3 = MidiMessage.ProgramChange(2, 27);
        MidiMessage m4 = MidiMessage.PitchWheelChange(15, 3274);
        
        Assert.Equal(0b__1011_0100, m1.Byte0);
        Assert.Equal(0b__1001_0010, m2.Byte0);
        Assert.Equal(0b__1100_0010, m3.Byte0);
        Assert.Equal(0b__1110_1111, m4.Byte0);

        Assert.Equal(4, m1.ChannelNumber);
        Assert.Equal(2, m2.ChannelNumber);
        Assert.Equal(2, m3.ChannelNumber);
        Assert.Equal(15, m4.ChannelNumber);

        Assert.Equal(MidiMessageType.ControlChange, m1.MessageType);
        Assert.Equal(MidiMessageType.NoteOn, m2.MessageType);
        Assert.Equal(MidiMessageType.ProgramChange, m3.MessageType);
        Assert.Equal(MidiMessageType.PitchWheelChange, m4.MessageType);

        Assert.Equal(3, m1.Length);
        Assert.Equal(3, m2.Length);
        Assert.Equal(2, m3.Length);
        Assert.Equal(3, m4.Length);

        Assert.Equal(3, m1.Byte1);
        Assert.Equal(127, m1.Byte2);
        Assert.Equal(63, m2.Byte1);
        Assert.Equal(100, m2.Byte2);
        Assert.Equal(27, m3.Byte1);
        Assert.Equal(0, m3.Byte2);
        Assert.Equal(74, m4.Byte1);
        Assert.Equal(25, m4.Byte2);
        Assert.Equal(3274, m4.DataPositive14Bit);
    }

    [Fact]
    public void ShouldWriteCorrectly()
    {
        MidiMessage m1 = MidiMessage.ControlChange(4, 3, 127);
        MidiMessage m3 = MidiMessage.ProgramChange(2, 27);
        MidiMessage m4 = MidiMessage.PitchWheelChange(15, 3274);

        Span<byte> buffer = stackalloc byte[32];

        int len1 = m1.WriteTo(buffer);
        int len3 = m3.WriteTo(buffer, len1);
        int len4 = m4.WriteTo(buffer[(len1 + len3)..]);

        Assert.Equal(3, len1);
        Assert.Equal(2, len3);
        Assert.Equal(3, len4);

        uint raw1 = (uint)(buffer[0] | (buffer[1] << 8) | (buffer[2] << 16));
        uint raw3 = (uint)(buffer[3] | (buffer[4] << 8));
        uint raw4 = (uint)(buffer[5] | (buffer[6] << 8) | (buffer[7] << 16));

        Assert.Equal(m1.RawWithoutLength, raw1);
        Assert.Equal(m3.RawWithoutLength, raw3);
        Assert.Equal(m4.RawWithoutLength, raw4);
    }

    [Fact]
    public void ShouldHandleSystemMessagesCorrectly()
    {
        MidiMessage s1 = MidiMessage.SongPositionPointer(8191);
        MidiMessage s2 = MidiMessage.TuneRequest();
        MidiMessage s3 = MidiMessage.StartSystemExclusive();
        MidiMessage s4 = MidiMessage.SystemExclusiveContent(9);
        MidiMessage s5 = MidiMessage.SystemExclusiveContent(32);
        MidiMessage s6 = MidiMessage.SystemExclusiveContent(113);
        MidiMessage s7 = MidiMessage.EndofSystemExclusive();

        Assert.Equal(MidiMessageType.System, s1.MessageType);
        Assert.Equal(MidiMessageType.System, s2.MessageType);
        Assert.Equal(MidiMessageType.System, s3.MessageType);
        Assert.True(s4.IsSysExContent);
        Assert.True(s5.IsSysExContent);
        Assert.True(s6.IsSysExContent);
        Assert.Equal(MidiMessageType.System, s7.MessageType);

        Assert.Equal(3, s1.Length);
        Assert.Equal(1, s2.Length);
        Assert.Equal(1, s3.Length);
        Assert.Equal(1, s4.Length);
        Assert.Equal(1, s5.Length);
        Assert.Equal(1, s6.Length);
        Assert.Equal(1, s7.Length);

        Assert.Equal(8191, s1.DataPositive14Bit);
        Assert.Equal(0, s2.Byte1);
        Assert.Equal(0, s3.Byte1);
        Assert.Equal(9, s4.Byte0);
        Assert.Equal(32, s5.Byte0);
        Assert.Equal(113, s6.Byte0);
        Assert.Equal(0, s7.Byte1);
    }
}
