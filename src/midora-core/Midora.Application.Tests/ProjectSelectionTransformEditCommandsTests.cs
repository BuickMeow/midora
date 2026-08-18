using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectSelectionTransformEditCommandsTests
{
    [Fact]
    public void LogicalNoteTransformsUseVisualBoundsAndUndoExactly()
    {
        (MidoraProject project, Segment segment, LogicalNote first, LogicalNote second) =
            CreateLogicalNoteProject();
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.FlipLogicalNotesHorizontal(
            segment.Id,
            [first.Id, second.Id]));
        Assert.Equal((40L, 10L), (first.StartTick, second.StartTick));
        Assert.Equal((20L, 10L), (first.LengthTicks, second.LengthTicks));
        document.Undo();
        Assert.Equal((10L, 50L), (first.StartTick, second.StartTick));

        document.Execute(ProjectDomainEditCommands.FlipLogicalNotesVertical(
            segment.Id,
            [first.Id, second.Id]));
        Assert.Equal((64, 60), (first.Note, second.Note));
        document.Undo();
        Assert.Equal((60, 64), (first.Note, second.Note));

        document.Execute(ProjectDomainEditCommands.ScaleLogicalNotes(
            segment.Id,
            [first.Id, second.Id],
            factor: 2));
        Assert.Equal((10L, 90L), (first.StartTick, second.StartTick));
        Assert.Equal((40L, 20L), (first.LengthTicks, second.LengthTicks));
        document.Undo();

        document.Execute(ProjectDomainEditCommands.TransposeLogicalNotes(
            segment.Id,
            [first.Id, second.Id],
            semitones: 64));
        Assert.Same(first, Assert.Single(segment.Notes));
        Assert.Equal(124, first.Note);
        document.Undo();
        Assert.Equal([first, second], segment.Notes);
        Assert.Equal((60, 64), (first.Note, second.Note));
    }

    [Fact]
    public void TemplateNoteTransformsUseVisualBoundsAndRestoreDiscardedNotes()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            TemplateLengthTicks = 480
        };
        SubVoice voice = new(project) { Name = "Voice" };
        TemplateEvent first = TemplateEvent.Note(project, 10, 20, 60, 100);
        TemplateEvent second = TemplateEvent.Note(project, 50, 10, 64, 80);
        voice.Events.AddRange([first, second]);
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.FlipTemplateNotesHorizontal(
            instrument.Id,
            voice.Id,
            [first.Id, second.Id]));
        Assert.Equal((40L, 10L), (first.Tick, second.Tick));
        document.Undo();

        document.Execute(ProjectDomainEditCommands.FlipTemplateNotesVertical(
            instrument.Id,
            voice.Id,
            [first.Id, second.Id]));
        Assert.Equal((64, 60), (first.Number, second.Number));
        document.Undo();

        document.Execute(ProjectDomainEditCommands.ScaleTemplateNotes(
            instrument.Id,
            voice.Id,
            [first.Id, second.Id],
            factor: 2));
        Assert.Equal((10L, 90L), (first.Tick, second.Tick));
        Assert.Equal((40L, 20L), (first.LengthTicks, second.LengthTicks));
        document.Undo();

        document.Execute(ProjectDomainEditCommands.TransposeTemplateNotes(
            instrument.Id,
            voice.Id,
            [first.Id, second.Id],
            semitones: 64));
        Assert.Same(first, Assert.Single(voice.Events));
        Assert.Equal(124, first.Number);
        document.Undo();
        Assert.Equal([first, second], voice.Events);
        Assert.Equal((60, 64), (first.Number, second.Number));
    }

    [Fact]
    public void SegmentTransformsIgnoreHiddenContentAndCanMirrorSegmentWindows()
    {
        MidoraProject project = new(480);
        LogicalTrack track = new(project) { Name = "Track" };
        Segment first = new(project)
        {
            ProjectStartTick = 100,
            LengthTicks = 100,
            ContentOffsetTick = 50
        };
        LogicalNote hidden = new(project)
        {
            StartTick = 10,
            LengthTicks = 10,
            Note = 30,
            Velocity = 80
        };
        LogicalNote exposed = new(project)
        {
            StartTick = 60,
            LengthTicks = 10,
            Note = 70,
            Velocity = 90
        };
        LogicalParameterLane lane = new(project) { ParameterId = MidoraId.FromSequence(900_001) };
        CurvePoint hiddenPoint = new(project, 40, 0.25);
        CurvePoint exposedPoint = new(project, 70, 0.75);
        lane.Points.AddRange([hiddenPoint, exposedPoint]);
        first.Notes.AddRange([hidden, exposed]);
        first.ParameterLanes.Add(lane);
        Segment second = new(project)
        {
            ProjectStartTick = 300,
            LengthTicks = 50,
            ContentOffsetTick = 0
        };
        track.Segments.AddRange([first, second]);
        project.Tracks.Add(track);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.FlipSegmentsHorizontal(
            [first.Id],
            SegmentSelectionTransformScope.ExposedContentOnly));
        Assert.Equal(10, hidden.StartTick);
        Assert.Equal(130, exposed.StartTick);
        Assert.Equal(40, lane.Points.Single(value => value.Id == hiddenPoint.Id).Tick);
        Assert.Equal(129, lane.Points.Single(value => value.Id == exposedPoint.Id).Tick);
        Assert.Equal(100, first.ProjectStartTick);
        document.Undo();

        document.Execute(ProjectDomainEditCommands.FlipSegmentsHorizontal(
            [first.Id, second.Id],
            SegmentSelectionTransformScope.ExposedContentAndSegments));
        Assert.Equal((250L, 100L), (first.ProjectStartTick, second.ProjectStartTick));
        Assert.Equal(10, hidden.StartTick);
        document.Undo();
        Assert.Equal((100L, 300L), (first.ProjectStartTick, second.ProjectStartTick));
        Assert.Equal(60, exposed.StartTick);
        Assert.Equal(70, lane.Points.Single(value => value.Id == exposedPoint.Id).Tick);

        document.Execute(ProjectDomainEditCommands.FlipSegmentsVertical([first.Id]));
        Assert.Equal(30, hidden.Note);
        Assert.Equal(57, exposed.Note);
        document.Undo();

        document.Execute(ProjectDomainEditCommands.ScaleSegments(
            [first.Id],
            factor: 2,
            SegmentSelectionTransformScope.ExposedContentOnly));
        Assert.Equal((100L, 100L, 50L), (
            first.ProjectStartTick,
            first.LengthTicks,
            first.ContentOffsetTick));
        Assert.Equal((10L, 70L, 20L), (
            hidden.StartTick,
            exposed.StartTick,
            exposed.LengthTicks));
        Assert.Equal(40, lane.Points.Single(value => value.Id == hiddenPoint.Id).Tick);
        Assert.Equal(90, lane.Points.Single(value => value.Id == exposedPoint.Id).Tick);
        document.Undo();

        document.Execute(ProjectDomainEditCommands.TransposeSegments([first.Id], semitones: 60));
        Assert.Same(hidden, Assert.Single(first.Notes));
        document.Undo();
        Assert.Equal([hidden, exposed], first.Notes);
    }

    [Fact]
    public void SegmentContentScaleUsesLaterPointWhenRoundingCollapsesTicks()
    {
        MidoraProject project = new(480);
        LogicalTrack track = new(project) { Name = "Track" };
        Segment segment = new(project) { LengthTicks = 100 };
        LogicalParameterLane lane = new(project) { ParameterId = MidoraId.FromSequence(900_002) };
        CurvePoint first = new(project, 10, 0.25);
        CurvePoint second = new(project, 11, 0.75);
        lane.Points.AddRange([first, second]);
        segment.ParameterLanes.Add(lane);
        track.Segments.Add(segment);
        project.Tracks.Add(track);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.ScaleSegments(
            [segment.Id],
            factor: 0.01,
            SegmentSelectionTransformScope.ExposedContentOnly));

        CurvePoint replacement = Assert.Single(lane.Points);
        Assert.Equal(second.Id, replacement.Id);
        Assert.Equal(0, replacement.Tick);
        document.Undo();
        Assert.Equal([first, second], lane.Points);
        document.Redo();
        Assert.Equal(second.Id, Assert.Single(lane.Points).Id);
    }

    [Fact]
    public void PointTransformUsesLaterEditedPointForSameTickCollision()
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
        LogicalTrack track = new(project) { Name = "Track", EventInstrumentId = instrument.Id };
        Segment segment = new(project) { LengthTicks = 480 };
        LogicalParameterLane lane = new(project) { ParameterId = parameter.Id };
        CurvePoint first = new(project, 0, 0.1);
        CurvePoint second = new(project, 10, 0.2);
        CurvePoint incumbent = new(project, 20, 0.3);
        lane.Points.AddRange([first, second, incumbent]);
        segment.ParameterLanes.Add(lane);
        track.Segments.Add(segment);
        project.Tracks.Add(track);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.ScaleLogicalParameterPoints(
            segment.Id,
            lane.Id,
            [first.Id, second.Id],
            factor: 2));

        Assert.Equal([first.Id, second.Id], lane.Points.Select(value => value.Id));
        Assert.Equal([0L, 20L], lane.Points.Select(value => value.Tick));
        document.Undo();
        Assert.Equal([first, second, incumbent], lane.Points);
        Assert.Equal([0L, 10L, 20L], lane.Points.Select(value => value.Tick));
        document.Redo();
        Assert.Equal([first.Id, second.Id], lane.Points.Select(value => value.Id));
    }

    [Fact]
    public void SubVoicePointTransformUsesExactTargetAndLaterEditedPointWins()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            TemplateLengthTicks = 480
        };
        SubVoice voice = new(project) { Name = "Voice" };
        TemplateEvent first = TemplateEvent.ControlChange(project, 0, 11, 20);
        TemplateEvent second = TemplateEvent.ControlChange(project, 10, 11, 40);
        TemplateEvent incumbent = TemplateEvent.ControlChange(project, 20, 11, 60);
        voice.Events.AddRange([first, second, incumbent]);
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.ScaleSubVoiceEventPoints(
            instrument.Id,
            voice.Id,
            [first.Id, second.Id],
            MidiValueTarget.ControlChange(11),
            factor: 2));

        Assert.Equal([first, second], voice.Events);
        Assert.Equal([0L, 20L], voice.Events.Select(value => value.Tick));
        document.Undo();
        Assert.Equal([first, second, incumbent], voice.Events);
        Assert.Equal([0L, 10L, 20L], voice.Events.Select(value => value.Tick));

        document.Execute(ProjectDomainEditCommands.FlipSubVoiceEventPointsHorizontal(
            instrument.Id,
            voice.Id,
            [first.Id, second.Id],
            MidiValueTarget.ControlChange(11)));
        Assert.Equal((10L, 0L, 20L), (first.Tick, second.Tick, incumbent.Tick));
        document.Undo();
        Assert.Equal((0L, 10L, 20L), (first.Tick, second.Tick, incumbent.Tick));
    }

    [Fact]
    public void BatchEditExpressionsApplyInDependencyOrderAndClampOrDiscardResults()
    {
        (MidoraProject project, Segment segment, LogicalNote first, LogicalNote second) =
            CreateLogicalNoteProject();
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        using BatchEditExpressionProgram program = BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.Velocity] = "=k1 * 2",
                [BatchEditField.KeyNumber] = "=k0 + 1",
                [BatchEditField.Gate] = "50%",
                [BatchEditField.Tick] = "=t0 + tr"
            });

        document.Execute(ProjectDomainEditCommands.BatchEditLogicalNotes(
            segment.Id,
            [first.Id, second.Id],
            program));

        Assert.Equal((61, 65), (first.Note, second.Note));
        Assert.Equal((122, 127), (first.Velocity, second.Velocity));
        Assert.Equal((10L, 5L), (first.LengthTicks, second.LengthTicks));
        Assert.Equal((10L, 90L), (first.StartTick, second.StartTick));

        document.Undo();
        Assert.Equal((60, 64), (first.Note, second.Note));
        Assert.Equal((10L, 50L), (first.StartTick, second.StartTick));

        using BatchEditExpressionProgram discard = BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.Velocity] = string.Empty,
                [BatchEditField.KeyNumber] = "+100",
                [BatchEditField.Gate] = string.Empty,
                [BatchEditField.Tick] = string.Empty
            });
        document.Execute(ProjectDomainEditCommands.BatchEditLogicalNotes(
            segment.Id,
            [first.Id, second.Id],
            discard));
        Assert.Empty(segment.Notes);
        document.Undo();
        Assert.Equal([first, second], segment.Notes);
    }

    [Fact]
    public void BatchExpressionValidationRejectsSelfAndIndirectCycles()
    {
        Assert.Throws<ArgumentException>(() => BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.Velocity] = "=v1 + 1",
                [BatchEditField.KeyNumber] = string.Empty,
                [BatchEditField.Gate] = string.Empty,
                [BatchEditField.Tick] = string.Empty
            }));

        Assert.Throws<ArgumentException>(() => BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.Velocity] = "=k1",
                [BatchEditField.KeyNumber] = "=g1",
                [BatchEditField.Gate] = "=v1",
                [BatchEditField.Tick] = string.Empty
            }));

        Assert.Throws<ArgumentException>(() => BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.PointValue] = "=new Random().Next()",
                [BatchEditField.Tick] = string.Empty
            }));

        Assert.Throws<ArgumentException>(() => BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.Velocity] = "=p0",
                [BatchEditField.KeyNumber] = string.Empty,
                [BatchEditField.Gate] = string.Empty,
                [BatchEditField.Tick] = string.Empty
            }));

        Assert.Throws<ArgumentException>(() => BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.PointValue] = "=v0",
                [BatchEditField.Tick] = string.Empty
            }));
    }

    [Fact]
    public void BatchEditRoundsBeforeTickBoundaryAndDeletesArbitrarilyLargeKeys()
    {
        MidoraProject project = new(480);
        LogicalTrack track = new(project) { Name = "Track" };
        Segment segment = new(project) { LengthTicks = 480 };
        LogicalNote note = new(project)
        {
            StartTick = 20,
            LengthTicks = 40,
            Note = 60,
            Velocity = 100
        };
        segment.Notes.Add(note);
        track.Segments.Add(segment);
        project.Tracks.Add(track);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        using BatchEditExpressionProgram nearZeroTick = BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.Velocity] = string.Empty,
                [BatchEditField.KeyNumber] = string.Empty,
                [BatchEditField.Gate] = string.Empty,
                [BatchEditField.Tick] = "=-0.49"
            });

        document.Execute(ProjectDomainEditCommands.BatchEditLogicalNotes(
            segment.Id,
            [note.Id],
            nearZeroTick));
        Assert.Equal(0, note.StartTick);
        document.Undo();

        using BatchEditExpressionProgram hugeKey = BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.Velocity] = string.Empty,
                [BatchEditField.KeyNumber] = "=1e300",
                [BatchEditField.Gate] = string.Empty,
                [BatchEditField.Tick] = string.Empty
            });
        document.Execute(ProjectDomainEditCommands.BatchEditLogicalNotes(
            segment.Id,
            [note.Id],
            hugeKey));
        Assert.Empty(segment.Notes);
        document.Undo();
        Assert.Same(note, Assert.Single(segment.Notes));
    }

    [Fact]
    public void SegmentNoteBatchTickCanExpandLeftWithoutMovingHiddenContent()
    {
        MidoraProject project = new(480);
        LogicalTrack track = new(project) { Name = "Track" };
        Segment segment = new(project)
        {
            ProjectStartTick = 100,
            LengthTicks = 100,
            ContentOffsetTick = 50
        };
        LogicalNote hidden = new(project)
        {
            StartTick = 10,
            LengthTicks = 10,
            Note = 60,
            Velocity = 80
        };
        LogicalNote selected = new(project)
        {
            StartTick = 60,
            LengthTicks = 10,
            Note = 64,
            Velocity = 100
        };
        segment.Notes.AddRange([hidden, selected]);
        track.Segments.Add(segment);
        project.Tracks.Add(track);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        using BatchEditExpressionProgram program = BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.Velocity] = string.Empty,
                [BatchEditField.KeyNumber] = string.Empty,
                [BatchEditField.Gate] = string.Empty,
                [BatchEditField.Tick] = "-40"
            });

        document.Execute(ProjectDomainEditCommands.BatchEditLogicalNotes(
            segment.Id,
            [selected.Id],
            program));

        Assert.Equal((70L, 130L, 20L), (
            segment.ProjectStartTick,
            segment.LengthTicks,
            segment.ContentOffsetTick));
        Assert.Equal(20, selected.StartTick);
        Assert.Equal(60, checked(segment.ProjectStartTick + hidden.StartTick - segment.ContentOffsetTick));
        Assert.Equal(70, checked(segment.ProjectStartTick + selected.StartTick - segment.ContentOffsetTick));

        document.Undo();
        Assert.Equal((100L, 100L, 50L), (
            segment.ProjectStartTick,
            segment.LengthTicks,
            segment.ContentOffsetTick));
        Assert.Equal(60, selected.StartTick);
        Assert.Equal([hidden, selected], segment.Notes);
    }

    [Fact]
    public void SegmentPointBatchLeftExpansionRejectsTrackOverlapAtomically()
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
        LogicalTrack track = new(project)
        {
            Name = "Track",
            EventInstrumentId = instrument.Id
        };
        Segment blocker = new(project)
        {
            ProjectStartTick = 40,
            LengthTicks = 50
        };
        Segment segment = new(project)
        {
            ProjectStartTick = 100,
            LengthTicks = 100,
            ContentOffsetTick = 50
        };
        LogicalParameterLane lane = new(project) { ParameterId = parameter.Id };
        CurvePoint point = new(project, 60, 0.5);
        lane.Points.Add(point);
        segment.ParameterLanes.Add(lane);
        track.Segments.AddRange([blocker, segment]);
        project.Tracks.Add(track);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        using BatchEditExpressionProgram program = BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.PointValue] = string.Empty,
                [BatchEditField.Tick] = "-40"
            });

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.BatchEditLogicalParameterPoints(
                segment.Id,
                lane.Id,
                [point.Id],
                program)));

        Assert.Equal((100L, 100L, 50L), (
            segment.ProjectStartTick,
            segment.LengthTicks,
            segment.ContentOffsetTick));
        Assert.Equal(60, point.Tick);
        Assert.Empty(document.History);
    }

    [Fact]
    public void LogicalParameterPointBatchReplacesSameTickIncumbent()
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
        LogicalTrack track = new(project)
        {
            Name = "Track",
            EventInstrumentId = instrument.Id
        };
        Segment segment = new(project) { LengthTicks = 480 };
        LogicalParameterLane lane = new(project) { ParameterId = parameter.Id };
        CurvePoint first = new(project, 10, 0.25);
        CurvePoint second = new(project, 20, 0.75);
        lane.Points.AddRange([first, second]);
        segment.ParameterLanes.Add(lane);
        track.Segments.Add(segment);
        project.Tracks.Add(track);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        using BatchEditExpressionProgram program = BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.PointValue] = string.Empty,
                [BatchEditField.Tick] = "10"
            });

        document.Execute(ProjectDomainEditCommands.BatchEditLogicalParameterPoints(
            segment.Id,
            lane.Id,
            [second.Id],
            program));

        CurvePoint replacement = Assert.Single(lane.Points);
        Assert.Equal(second.Id, replacement.Id);
        Assert.Equal(10, replacement.Tick);
        document.Undo();
        Assert.Equal([first, second], lane.Points);
        document.Redo();
        Assert.Equal(second.Id, Assert.Single(lane.Points).Id);
    }

    [Fact]
    public void SubVoiceEventPointBatchReplacesSameTargetAndTickIncumbent()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument" };
        SubVoice voice = new(project) { Name = "Voice" };
        TemplateEvent first = TemplateEvent.ControlChange(project, 10, 11, 20);
        TemplateEvent second = TemplateEvent.ControlChange(project, 20, 11, 80);
        voice.Events.AddRange([first, second]);
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        using BatchEditExpressionProgram program = BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.PointValue] = string.Empty,
                [BatchEditField.Tick] = "10"
            });

        document.Execute(ProjectDomainEditCommands.BatchEditSubVoiceEventPoints(
            instrument.Id,
            voice.Id,
            [second.Id],
            MidiValueTarget.ControlChange(11),
            program));

        Assert.Same(second, Assert.Single(voice.Events));
        Assert.Equal(10, second.Tick);
        document.Undo();
        Assert.Equal([first, second], voice.Events);
        Assert.Equal(20, second.Tick);
        document.Redo();
        Assert.Same(second, Assert.Single(voice.Events));
    }

    [Fact]
    public void SubVoiceEventPointBatchClampsValueAndDeletesNegativeTickResult()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            TemplateLengthTicks = 480
        };
        SubVoice voice = new(project) { Name = "Voice" };
        TemplateEvent first = TemplateEvent.ControlChange(project, 10, 11, 20);
        TemplateEvent second = TemplateEvent.ControlChange(project, 20, 11, 80);
        voice.Events.AddRange([first, second]);
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        using BatchEditExpressionProgram clamp = BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.PointValue] = "*10",
                [BatchEditField.Tick] = string.Empty
            });

        document.Execute(ProjectDomainEditCommands.BatchEditSubVoiceEventPoints(
            instrument.Id,
            voice.Id,
            [first.Id],
            MidiValueTarget.ControlChange(11),
            clamp));
        Assert.Equal(127, first.Value);
        document.Undo();
        Assert.Equal(20, first.Value);

        using BatchEditExpressionProgram discard = BatchEditExpressionProgram.Compile(
            new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.PointValue] = string.Empty,
                [BatchEditField.Tick] = "-30"
            });
        document.Execute(ProjectDomainEditCommands.BatchEditSubVoiceEventPoints(
            instrument.Id,
            voice.Id,
            [second.Id],
            MidiValueTarget.ControlChange(11),
            discard));
        Assert.Same(first, Assert.Single(voice.Events));
        document.Undo();
        Assert.Equal([first, second], voice.Events);
    }

    private static (MidoraProject Project, Segment Segment, LogicalNote First, LogicalNote Second)
        CreateLogicalNoteProject()
    {
        MidoraProject project = new(480);
        LogicalTrack track = new(project) { Name = "Track" };
        Segment segment = new(project) { LengthTicks = 480 };
        LogicalNote first = new(project)
        {
            StartTick = 10,
            LengthTicks = 20,
            Note = 60,
            Velocity = 100
        };
        LogicalNote second = new(project)
        {
            StartTick = 50,
            LengthTicks = 10,
            Note = 64,
            Velocity = 80
        };
        segment.Notes.AddRange([first, second]);
        track.Segments.Add(segment);
        project.Tracks.Add(track);
        return (project, segment, first, second);
    }

    private static ProjectDocumentSession PersistedDocument(ProjectCompilationSession compilation)
    {
        ProjectDocumentSession result = new(compilation, ProjectDocumentOrigin.Persisted);
        result.MarkSaveSucceeded();
        return result;
    }
}
