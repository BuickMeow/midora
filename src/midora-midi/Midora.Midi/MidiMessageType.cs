using System.Runtime.CompilerServices;

namespace Midora.Midi;

public enum MidiMessageType : byte
{
    NoteOff = 0b__1000_0000,
    NoteOn = 0b__1001_0000,
    PolyphonicKeyPressure = 0b__1010_0000,
    ControlChange = 0b__1011_0000,
    ProgramChange = 0b__1100_0000,
    ChannelPressure = 0b__1101_0000,
    PitchWheelChange = 0b__1110_0000,
    System = 0b__1111_0000,
}
