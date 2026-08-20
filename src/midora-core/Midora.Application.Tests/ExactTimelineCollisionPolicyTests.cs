using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ExactTimelineCollisionPolicyTests
{
    [Fact]
    public void MovingLogicalNoteOntoExistingExactKeyDiscardsMoverAndUndoRestoresIt()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalTrack track = new(project) { Name = "Track" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 480 };
        LogicalNote incumbent = new(project)
        {
            StartTick = 100,
            LengthTicks = 40,
            Note = 64,
            Velocity = 90
        };
        LogicalNote mover = new(project)
        {
            StartTick = 20,
            LengthTicks = 80,
            Note = 64,
            Velocity = 100
        };
        segment.Notes.AddRange([incumbent, mover]);
        track.Segments.Add(segment);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.MoveLogicalNotes(
            segment.Id,
            [mover.Id],
            tickDelta: 80,
            pitchDelta: 0));

        Assert.Same(incumbent, Assert.Single(segment.Notes));
        document.Undo();
        Assert.Equal([incumbent, mover], segment.Notes);
        Assert.Equal(20, mover.StartTick);
        document.Redo();
        Assert.Same(incumbent, Assert.Single(segment.Notes));
    }

    [Fact]
    public void LogicalParameterCreationAndMoveReplaceExistingPointAtExactTick()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument" };
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Amount",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1
        };
        instrument.LogicalParameters.Add(parameter);
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project) { Name = "Track"};
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 480 };
        LogicalParameterLane lane = new(project) { ParameterId = parameter.Id };
        CurvePoint incumbent = new(project, 100, 0.25);
        CurvePoint mover = new(project, 40, 0.75);
        lane.Points.AddRange([incumbent, mover]);
        segment.ParameterLanes.Add(lane);
        track.Segments.Add(segment);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.CreateLogicalParameterPoint(
            segment.Id,
            lane.Id,
            100,
            0.5,
            CurveInterpolation.Linear));
        CurvePoint created = Assert.Single(lane.Points, point => point.Id != mover.Id);
        Assert.Equal((100L, 0.5), (created.Tick, created.Value));
        Assert.DoesNotContain(incumbent, lane.Points);

        document.Execute(ProjectDomainEditCommands.MoveLogicalParameterPoints(
            segment.Id,
            lane.Id,
            [mover.Id],
            tickDelta: 60));
        CurvePoint moved = Assert.Single(lane.Points);
        Assert.Equal(mover.Id, moved.Id);
        Assert.Equal((100L, 0.75), (moved.Tick, moved.Value));

        document.Undo();
        Assert.Contains(created, lane.Points);
        Assert.Contains(mover, lane.Points);
        Assert.Equal(40, mover.Tick);
        document.Undo();
        Assert.Equal([incumbent, mover], lane.Points);
        document.Redo();
        Assert.Contains(created, lane.Points);
        Assert.DoesNotContain(incumbent, lane.Points);
    }

    [Fact]
    public void TemplateNoteCreationAndMoveKeepExistingExactNote()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument", TemplateLengthTicks = 480 };
        SubVoice voice = new(project) { Name = "Voice" };
        TemplateEvent incumbent = TemplateEvent.Note(project, 100, 40, 64, 90);
        TemplateEvent mover = TemplateEvent.Note(project, 20, 80, 64, 100);
        voice.Events.AddRange([incumbent, mover]);
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.CreateTemplateNote(
            instrument.Id,
            voice.Id,
            100,
            20,
            64,
            80));
        Assert.Equal([incumbent, mover], voice.Events);

        document.Execute(ProjectDomainEditCommands.UpdateTemplateNote(
            instrument.Id,
            voice.Id,
            mover.Id,
            100,
            mover.LengthTicks,
            64,
            mover.Value,
            mover.FollowPitchDelta));
        Assert.Same(incumbent, Assert.Single(voice.Events));

        document.Undo();
        Assert.Equal([incumbent, mover], voice.Events);
        Assert.Equal(20, mover.Tick);
        document.Redo();
        Assert.Same(incumbent, Assert.Single(voice.Events));
    }

    [Fact]
    public void ScopedCollisionResolutionDoesNotCleanUnrelatedPreexistingCollisions()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalTrack track = new(project) { Name = "Track" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment unrelated = new(project)
        {
            ProjectStartTick = 0,
            LengthTicks = 240
        };
        LogicalNote unrelatedFirst = new(project)
        {
            StartTick = 10,
            LengthTicks = 20,
            Note = 60,
            Velocity = 90
        };
        LogicalNote unrelatedSecond = new(project)
        {
            StartTick = 10,
            LengthTicks = 40,
            Note = 60,
            Velocity = 100
        };
        unrelated.Notes.AddRange([unrelatedFirst, unrelatedSecond]);

        Segment edited = new(project)
        {
            ProjectStartTick = 240,
            LengthTicks = 240
        };
        LogicalNote incumbent = new(project)
        {
            StartTick = 100,
            LengthTicks = 20,
            Note = 64,
            Velocity = 90
        };
        LogicalNote mover = new(project)
        {
            StartTick = 20,
            LengthTicks = 20,
            Note = 64,
            Velocity = 100
        };
        edited.Notes.AddRange([incumbent, mover]);
        track.Segments.AddRange([unrelated, edited]);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.MoveLogicalNotes(
            edited.Id,
            [mover.Id],
            tickDelta: 80,
            pitchDelta: 0));

        Assert.Equal([unrelatedFirst, unrelatedSecond], unrelated.Notes);
        Assert.Same(incumbent, Assert.Single(edited.Notes));
        document.Undo();
        Assert.Equal([unrelatedFirst, unrelatedSecond], unrelated.Notes);
        Assert.Equal([incumbent, mover], edited.Notes);
    }

    [Fact]
    public void BankComponentsUseIndependentExactTargetsAndLaterSameTargetReplacesIncumbent()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument", TemplateLengthTicks = 480 };
        SubVoice voice = new(project) { Name = "Voice" };
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.CreateTemplateBank(
            instrument.Id,
            voice.Id,
            tick: 100,
            bankMsb: 12,
            bankLsb: null));
        document.Execute(ProjectDomainEditCommands.CreateTemplateBank(
            instrument.Id,
            voice.Id,
            tick: 100,
            bankMsb: null,
            bankLsb: 34));
        TemplateEvent retainedLsb = Assert.Single(voice.Events, value => value.HasBankLsb);
        TemplateEvent replacedMsb = Assert.Single(voice.Events, value => value.HasBankMsb);

        document.Execute(ProjectDomainEditCommands.CreateTemplateBank(
            instrument.Id,
            voice.Id,
            tick: 100,
            bankMsb: 56,
            bankLsb: null));

        TemplateEvent replacementMsb = Assert.Single(voice.Events, value => value.HasBankMsb);
        Assert.NotSame(replacedMsb, replacementMsb);
        Assert.Equal(56, replacementMsb.Value);
        Assert.Contains(retainedLsb, voice.Events);
        Assert.DoesNotContain(replacedMsb, voice.Events);
        document.Undo();
        Assert.Contains(replacedMsb, voice.Events);
        Assert.Contains(retainedLsb, voice.Events);
        Assert.DoesNotContain(replacementMsb, voice.Events);
        document.Redo();
        Assert.Contains(replacementMsb, voice.Events);
        Assert.DoesNotContain(replacedMsb, voice.Events);
    }

    [Fact]
    public void MovingTemplateEventPointOntoExistingTargetKeepsMoverAndUndoRestoresBoth()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument", TemplateLengthTicks = 480 };
        SubVoice voice = new(project) { Name = "Voice" };
        TemplateEvent incumbent = TemplateEvent.ControlChange(project, 100, 11, 32);
        TemplateEvent mover = TemplateEvent.ControlChange(project, 40, 11, 96);
        voice.Events.AddRange([incumbent, mover]);
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.AdjustSubVoiceEventPoints(
            instrument.Id,
            voice.Id,
            [mover.Id],
            MidiValueTarget.ControlChange(11),
            tickDelta: 60,
            valueDelta: 0,
            duplicate: false));

        Assert.Same(mover, Assert.Single(voice.Events));
        Assert.Equal(100, mover.Tick);
        document.Undo();
        Assert.Equal([incumbent, mover], voice.Events);
        Assert.Equal(40, mover.Tick);
        document.Redo();
        Assert.Same(mover, Assert.Single(voice.Events));
    }

    [Fact]
    public void WholeSubVoiceDuplicateDropsLaterExactCollisionsInTheNewOwner()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument", TemplateLengthTicks = 480 };
        SubVoice source = new(project) { Name = "Voice" };
        TemplateEvent first = TemplateEvent.Note(project, 20, 40, 60, 80);
        TemplateEvent later = TemplateEvent.Note(project, 20, 90, 60, 120);
        source.Events.AddRange([first, later]);
        instrument.SubVoices.Add(source);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.DuplicateSubVoice(
            instrument.Id,
            source.Id));

        SubVoice copy = instrument.SubVoices[1];
        TemplateEvent copied = Assert.Single(copy.Events);
        Assert.Equal((40L, 80), (copied.LengthTicks, copied.Value));
        Assert.Equal([first, later], source.Events);
    }

    private static ProjectDocumentSession PersistedDocument(ProjectCompilationSession compilation)
    {
        ProjectDocumentSession result = new(compilation, ProjectDocumentOrigin.Persisted);
        result.MarkSaveSucceeded();
        return result;
    }
}
