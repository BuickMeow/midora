using Midora.Midi;

namespace Midora.Audio;

public readonly record struct ScheduledMidiMessage(
    long SampleFrame,
    MidiMessage Message,
    int SourceIndex = -1);
