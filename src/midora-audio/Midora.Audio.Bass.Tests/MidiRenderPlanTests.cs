using Midora.Midi;

namespace Midora.Audio.Bass.Tests;

public sealed class MidiRenderPlanTests
{
    [Fact]
    public void RejectsForbiddenEffectsControllers()
    {
        ScheduledMidiMessage[] cc91 = [new(0, MidiMessage.ControlChange(0, 91, 1))];
        ScheduledMidiMessage[] cc93 = [new(0, MidiMessage.ControlChange(0, 93, 1))];

        _ = Assert.Throws<ArgumentException>(() => new MidiPortRenderPlan(0, cc91));
        _ = Assert.Throws<ArgumentException>(() => new MidiPortRenderPlan(0, cc93));
    }

    [Fact]
    public void PreservesSameFrameEventOrder()
    {
        ScheduledMidiMessage[] events =
        [
            new(10, MidiMessage.ControlChange(0, 0, 1)),
            new(10, MidiMessage.ProgramChange(0, 7)),
            new(10, MidiMessage.NoteOn(0, 60, 100))
        ];

        MidiPortRenderPlan plan = new(0, events);

        Assert.Equal(0, plan.Events[0].Message.Byte1);
        Assert.Equal(7, plan.Events[1].Message.Byte1);
        Assert.Equal(60, plan.Events[2].Message.Byte1);
    }

    [Fact]
    public void RejectsUnsortedEventsAndPorts()
    {
        ScheduledMidiMessage[] events =
        [
            new(20, MidiMessage.NoteOn(0, 60, 100)),
            new(10, MidiMessage.NoteOff(0, 60))
        ];

        _ = Assert.Throws<ArgumentException>(() => new MidiPortRenderPlan(0, events));

        MidiPortRenderPlan port1 = new(1, ReadOnlySpan<ScheduledMidiMessage>.Empty);
        MidiPortRenderPlan port0 = new(0, ReadOnlySpan<ScheduledMidiMessage>.Empty);
        _ = Assert.Throws<ArgumentException>(() => new MidiRenderPlan(48_000, 100, [port1, port0]));
    }
}
