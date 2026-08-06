using Midora.Domain;

namespace Midora.Compiler.Tests;

public sealed class Int64BoundaryCompilationTests
{
    [Fact]
    public void NoteAndLifecycleDurationsClipBeforeInt64Addition()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 10);
        fixture.Segment.ProjectStartTick = long.MaxValue - 10;
        fixture.Instrument.TemplateLengthTicks = long.MaxValue;
        fixture.Instrument.ShortLifecycle = ShortNoteLifecycle.OneShot;
        fixture.Voice.Events.Add(TemplateEvent.Note(
            fixture.Project,
            tick: 0,
            lengthTicks: long.MaxValue,
            note: 60,
            velocity: 100));
        fixture.Voice.Events.Add(TemplateEvent.Program(
            fixture.Project,
            tick: long.MaxValue - 1,
            program: 1));
        _ = CompilerTestProject.AddNote(
            fixture.Segment,
            fixture.Instrument,
            start: 0,
            length: long.MaxValue);

        using MidoraCompiler compiler = new();
        CanonicalCompiledResult result = compiler.CompileFull(fixture.Project);

        Assert.True(
            result.IsConsumable,
            string.Join(" | ", result.Diagnostics.Select(value =>
                $"{value.Code}:{value.Severity}:{value.Message}")));
        Assert.False(result.IsPartial);
        Assert.Equal(long.MaxValue, result.EndTick);
        ChannelUnitAllocation allocation = Assert.Single(result.Allocations.ToArray());
        Assert.Equal(long.MaxValue - 10, allocation.StartTick);
        Assert.Equal(long.MaxValue, allocation.EndTick);
    }

    [Fact]
    public void ContentOffsetTranslationSubtractsBeforeProjectTickAddition()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 10);
        fixture.Segment.ProjectStartTick = long.MaxValue - 10;
        fixture.Segment.ContentOffsetTick = long.MaxValue - 10;
        fixture.Voice.Events.Add(TemplateEvent.Program(fixture.Project, 0, 1));
        _ = CompilerTestProject.AddNote(
            fixture.Segment,
            fixture.Instrument,
            start: long.MaxValue - 10,
            length: 10);

        using MidoraCompiler compiler = new();
        CanonicalCompiledResult result = compiler.CompileFull(fixture.Project);

        Assert.True(
            result.IsConsumable,
            string.Join(" | ", result.Diagnostics.Select(value =>
                $"{value.Code}:{value.Severity}:{value.Message}")));
        CanonicalMidiEvent program = Assert.Single(result.Events.ToArray(), value =>
            value.Role == CanonicalEventRole.Program);
        Assert.Equal(long.MaxValue - 10, program.Tick);
    }

    [Fact]
    public void LoopOccurrencesStopAtInt64BoundaryWithoutOverflow()
    {
        var fixture = CompilerTestProject.Create(segmentLength: long.MaxValue);
        fixture.Instrument.TemplateLengthTicks = long.MaxValue - 1;
        fixture.Instrument.RequiresChannelIsolation = true;
        fixture.Instrument.LoopStartTick = long.MaxValue - 100;
        fixture.Instrument.LoopEndTick = long.MaxValue - 50;
        fixture.Voice.Events.Add(TemplateEvent.Program(
            fixture.Project,
            tick: long.MaxValue - 75,
            program: 1));
        fixture.Voice.Events.Add(TemplateEvent.Program(
            fixture.Project,
            tick: long.MaxValue - 2,
            program: 2));
        _ = CompilerTestProject.AddNote(
            fixture.Segment,
            fixture.Instrument,
            start: 0,
            length: long.MaxValue);

        using MidoraCompiler compiler = new();
        CanonicalCompiledResult result = compiler.CompileFull(fixture.Project);

        Assert.True(
            result.IsConsumable,
            string.Join(" | ", result.Diagnostics.Select(value =>
                $"{value.Code}:{value.Severity}:{value.Message}")));
        Assert.Equal(long.MaxValue, result.EndTick);
        Assert.Contains(result.Events.ToArray(), value =>
            value.Role == CanonicalEventRole.Program
            && value.Tick == long.MaxValue - 75);
        Assert.DoesNotContain(result.Events.ToArray(), value =>
            value.Role == CanonicalEventRole.Program
            && value.Message.Byte1 == 2);
    }
}
