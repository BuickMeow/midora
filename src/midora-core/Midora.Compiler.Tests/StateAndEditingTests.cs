using Midora.Domain;
using Midora.Midi;

namespace Midora.Compiler.Tests;

public sealed class StateAndEditingTests
{
    [Fact]
    public void TickZeroUserEventSuppressesConflictingInitialState()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Voice.InitialState.Controllers[11] = 80;
        fixture.Voice.Events.Add(TemplateEvent.ControlChange(fixture.Project, 0, 11, 100));
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);
        CanonicalMidiEvent[] expressionAtStart = result.Events.ToArray().Where(value => value.Tick == 0
            && value.Message.MessageType == MidiMessageType.ControlChange && value.Message.Byte1 == 11).ToArray();

        CanonicalMidiEvent value = Assert.Single(expressionAtStart);
        Assert.Equal((byte)100, value.Message.Byte2);
        Assert.Equal(CanonicalEventRole.ControlChange, value.Role);
    }

    [Fact]
    public void ResetTouchesOnlyUsedTargetsAndUsesProjectDefaults()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 480);
        fixture.Project.GlobalResetDefaults.Controllers[11] = 77;
        fixture.Voice.Events.Add(TemplateEvent.ControlChange(fixture.Project, 0, 11, 100));
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);
        CanonicalMidiEvent[] resets = result.Events.ToArray().Where(value => value.Role == CanonicalEventRole.Reset).ToArray();

        CanonicalMidiEvent reset = Assert.Single(resets, value =>
            value.Tick == 240
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 11);
        Assert.Equal((byte)11, reset.Message.Byte1);
        Assert.Equal((byte)77, reset.Message.Byte2);
        Assert.Single(resets, value =>
            value.Tick == 480
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 120
            && value.Message.Byte2 == 0);
        Assert.DoesNotContain(result.Events.ToArray(), value => value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 is 121 or 123);
    }

    [Fact]
    public void SegmentSplitCutsCrossingNoteAndWritesRightParameterStartState()
    {
        MidoraProject project = new(480);
        Segment source = new(project) { ProjectStartTick = 100, LengthTicks = 400, ContentOffsetTick = 20 };
        source.Notes.Add(new LogicalNote(project) { StartTick = 100, LengthTicks = 200, Note = 60, Velocity = 100 });
        LogicalParameterLane lane = new(project) { ParameterId = project.AllocateStableId() };
        lane.Points.Add(new(project, 20, 0));
        lane.Points.Add(new(project, 420, 1));
        source.ParameterLanes.Add(lane);

        SegmentSplitResult split = SegmentEditing.Split(project, source, 300);

        Assert.Equal(200, split.Left.LengthTicks);
        Assert.Equal(200, split.Right.LengthTicks);
        LogicalNote leftNote = Assert.Single(split.Left.Notes);
        Assert.Equal(120, leftNote.LengthTicks); // content split tick = 220
        Assert.Empty(split.Right.Notes);
        CurvePoint rightStart = Assert.Single(split.Right.ParameterLanes[0].Points, value => value.Tick == 220);
        Assert.Equal(0.5, rightStart.Value, 12);
    }

    [Fact]
    public void SegmentSplitBeforeFirstParameterPointPreservesImplicitDefault()
    {
        MidoraProject project = new(480);
        Segment source = new(project) { LengthTicks = 400 };
        LogicalParameterLane lane = new(project) { ParameterId = project.AllocateStableId() };
        lane.Points.Add(new(project, 300, 0.8));
        source.ParameterLanes.Add(lane);

        SegmentSplitResult split = SegmentEditing.Split(project, source, 200);

        Assert.Empty(split.Left.ParameterLanes[0].Points);
        CurvePoint point = Assert.Single(split.Right.ParameterLanes[0].Points);
        Assert.Equal(300, point.Tick);
        Assert.Equal(0.8, point.Value);
    }

    [Fact]
    public void CanonicalResultFreezesAllConductorContext()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Project.Conductor.Tempos.Add(new(fixture.Project, 480, 90));
        fixture.Project.Conductor.TimeSignatures.Add(new(fixture.Project, 960, 3, 4));
        fixture.Project.Conductor.KeySignatures.Add(new(fixture.Project, 0, -2, true));
        fixture.Project.Conductor.Markers.Add(new(fixture.Project, 720, "Verse"));

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest { EndTick = 1_200 });

        Assert.Equal(2, result.Conductor.Tempos.Length);
        Assert.Equal(2, result.Conductor.TimeSignatures.Length);
        Assert.Single(result.Conductor.KeySignatures.ToArray());
        Assert.Single(result.Conductor.Markers.ToArray());
    }

    [Fact]
    public void DiscreteTemplatePointWinsOverCurveAtTheSameTick()
    {
        var fixture = CompilerTestProject.Create();
        ValueCurve curve = new(fixture.Project) { Target = MidiValueTarget.ControlChange(1) };
        curve.Points.Add(new(fixture.Project, 0, 20));
        curve.Points.Add(new(fixture.Project, 100, 40));
        fixture.Voice.Curves.Add(curve);
        fixture.Voice.Events.Add(TemplateEvent.ControlChange(fixture.Project, 0, 1, 80));
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);
        CanonicalMidiEvent controller = Assert.Single(result.Events.ToArray(), value => value.Tick == 0
            && value.Message.MessageType == MidiMessageType.ControlChange && value.Message.Byte1 == 1);

        Assert.Equal((byte)80, controller.Message.Byte2);
    }
}
