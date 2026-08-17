using Midora.Domain;
using Midora.Midi;

namespace Midora.Compiler.Tests;

public sealed class CompilationEventStatisticsTests
{
    [Fact]
    public void StatisticsCountPositiveVelocityNoteOnsAndEveryCanonicalMidiEvent()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 960);
        fixture.Voice.Events.Add(TemplateEvent.ControlChange(fixture.Project, 0, 11, 96));
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 480, 240);

        using MidoraCompiler compiler = new();
        CanonicalCompiledResult result = compiler.CompileFull(fixture.Project);
        CanonicalMidiEvent[] events = result.Events.ToArray();
        int expectedNoteOns = events.Count(value =>
            value.Message.MessageType == MidiMessageType.NoteOn
            && value.Message.Byte2 != 0);

        Assert.True(result.IsConsumable);
        Assert.Equal(2, expectedNoteOns);
        Assert.Equal(expectedNoteOns, result.Statistics.NoteOnEventCount);
        Assert.Equal(events.Length, result.Statistics.EventCount);
        Assert.True(result.Statistics.EventCount > result.Statistics.NoteOnEventCount);
    }

    [Fact]
    public void NonConsumableResultReportsNoCompiledMidiEvents()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Segment.LengthTicks = 0;

        using MidoraCompiler compiler = new();
        CanonicalCompiledResult result = compiler.CompileFull(fixture.Project);

        Assert.False(result.IsConsumable);
        Assert.Empty(result.Events.ToArray());
        Assert.Equal(0, result.Statistics.NoteOnEventCount);
        Assert.Equal(0, result.Statistics.EventCount);
    }
}
