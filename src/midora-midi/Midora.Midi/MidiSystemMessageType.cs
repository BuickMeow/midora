namespace Midora.Midi;

public enum MidiSystemMessageType : byte
{
    StartSystemExclusive = 0b__1111_0000,
    SongPositionPointer = 0b__1111_0010,
    SongSelect = 0b__1111_0011,
    TuneRequest = 0b__1111_0110,
    EndofSystemExclusive = 0b__1111_0111,
    TimingClock = 0b__1111_1000,
    Start = 0b__1111_1010,
    Continue = 0b__1111_1011,
    Stop = 0b__1111_1100,
    ActiveSensing = 0b__1111_1110,
    Reset = 0b__1111_1111,
}
