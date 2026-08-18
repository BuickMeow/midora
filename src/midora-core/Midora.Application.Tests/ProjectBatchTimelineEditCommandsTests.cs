using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectBatchTimelineEditCommandsTests
{
    [Fact]
    public void SharedNoteMoveIsAtomicAndOneUndoRestoresEveryValue()
    {
        (MidoraProject project, _, Segment segment, LogicalNote first, LogicalNote second) =
            CreateProject();
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        long nextStableId = project.NextStableId;

        ProjectEditExecution execution = document.Execute(
            ProjectDomainEditCommands.MoveLogicalNotes(
                segment.Id,
                [second.Id, first.Id],
                tickDelta: 20,
                pitchDelta: 2));

        Assert.True(execution.Changed);
        Assert.Equal((20, 62), (first.StartTick, first.Note));
        Assert.Equal((140, 66), (second.StartTick, second.Note));
        Assert.Equal(nextStableId, project.NextStableId);
        Assert.Single(document.History);
        AssertMatchesFull(compilation);

        document.Undo();

        Assert.Equal((0, 60), (first.StartTick, first.Note));
        Assert.Equal((120, 64), (second.StartTick, second.Note));
        Assert.False(document.IsModified);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void MoveDiscardsOnlyNotesThatCrossThePitchBoundaryAndUndoRestoresThem()
    {
        (MidoraProject project, _, Segment segment, LogicalNote first, LogicalNote second) =
            CreateProject();
        second.Note = 127;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        long nextStableId = project.NextStableId;

        document.Execute(
            ProjectDomainEditCommands.MoveLogicalNotes(
                segment.Id,
                [first.Id, second.Id],
                tickDelta: 10,
                pitchDelta: 1));

        Assert.Equal((10, 61), (first.StartTick, first.Note));
        Assert.DoesNotContain(second, segment.Notes);
        Assert.Equal(nextStableId, project.NextStableId);
        Assert.Single(document.History);
        AssertMatchesFull(compilation);

        document.Undo();

        Assert.Equal((0, 60), (first.StartTick, first.Note));
        Assert.Equal((120, 127), (second.StartTick, second.Note));
        Assert.Equal([first, second], segment.Notes);
        Assert.False(document.IsModified);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void EdgeVelocityAndAlignmentCommandsUseSharedBatchSemantics()
    {
        (MidoraProject project, _, Segment segment, LogicalNote first, LogicalNote second) =
            CreateProject();
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.AdjustLogicalNoteEdges(
            segment.Id,
            [first.Id, second.Id],
            startDelta: 10,
            endDelta: 30));
        Assert.Equal((10L, 80L), (first.StartTick, first.LengthTicks));
        Assert.Equal((130L, 80L), (second.StartTick, second.LengthTicks));

        document.Execute(ProjectDomainEditCommands.SetLogicalNoteVelocities(
            segment.Id,
            [first.Id, second.Id],
            value: -10,
            ProjectBatchValueEditMode.RelativeAdjust));
        Assert.Equal(90, first.Velocity);
        Assert.Equal(70, second.Velocity);

        document.Execute(ProjectDomainEditCommands.AlignLogicalNotes(
            segment.Id,
            [first.Id, second.Id],
            first.Id,
            LogicalNoteAlignment.Start));
        Assert.Equal(first.StartTick, second.StartTick);
        Assert.Equal(3, document.History.Count);
        AssertMatchesFull(compilation);

        document.Undo();
        Assert.Equal(130, second.StartTick);
        document.Undo();
        Assert.Equal((100, 80), (first.Velocity, second.Velocity));
        document.Undo();
        Assert.Equal((0L, 60L), (first.StartTick, first.LengthTicks));
        Assert.Equal((120L, 60L), (second.StartTick, second.LengthTicks));
        Assert.False(document.IsModified);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void DuplicateNotesAlignsEarliestTickAllocatesFreshIdsAndRedoKeepsIdentity()
    {
        (MidoraProject project, LogicalTrack track, Segment source, LogicalNote first, LogicalNote second) =
            CreateProject();
        Segment target = new(project)
        {
            ProjectStartTick = 480,
            LengthTicks = 480
        };
        track.Segments.Add(target);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.DuplicateLogicalNotes(
            source.Id,
            [second.Id, first.Id],
            target.Id,
            newEarliestStartTick: 30));

        LogicalNote[] copies = target.Notes.ToArray();
        Assert.Equal(2, copies.Length);
        Assert.Equal([30L, 150L], copies.Select(value => value.StartTick));
        Assert.Equal([60, 64], copies.Select(value => value.Note));
        Assert.All(copies, value => Assert.DoesNotContain(value.Id, new[] { first.Id, second.Id }));
        Assert.Equal(2, copies.Select(value => value.Id).Distinct().Count());
        Assert.Single(document.History);
        AssertMatchesFull(compilation);

        document.Undo();
        Assert.Empty(target.Notes);
        document.Redo();
        Assert.Equal(copies, target.Notes);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void BatchDeleteRestoresOriginalObjectsAndOrdering()
    {
        (MidoraProject project, _, Segment segment, LogicalNote first, LogicalNote second) =
            CreateProject();
        LogicalNote third = new(project)
        {
            StartTick = 240,
            LengthTicks = 60,
            Note = 67,
            Velocity = 70
        };
        segment.Notes.Add(third);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.DeleteLogicalNotes(
            segment.Id,
            [third.Id, first.Id]));
        Assert.Equal([second], segment.Notes);

        document.Undo();
        Assert.Equal([first, second, third], segment.Notes);
        Assert.False(document.IsModified);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void SegmentBatchMovePreservesTrackOffsetsAndUsesOneUndo()
    {
        MidoraProject project = new(480);
        LogicalTrack targetPrimary = new(project) { Name = "Target 1" };
        LogicalTrack targetSecondary = new(project) { Name = "Target 2" };
        LogicalTrack sourcePrimary = new(project) { Name = "Source 1" };
        LogicalTrack sourceSecondary = new(project) { Name = "Source 2" };
        project.Tracks.AddRange(
            [targetPrimary, targetSecondary, sourcePrimary, sourceSecondary]);
        Segment first = new(project) { ProjectStartTick = 100, LengthTicks = 60 };
        Segment second = new(project) { ProjectStartTick = 200, LengthTicks = 60 };
        sourcePrimary.Segments.Add(first);
        sourceSecondary.Segments.Add(second);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.MoveSegments(
            [second.Id, first.Id],
            first.Id,
            targetPrimary.Id,
            newPrimaryStartTick: 300));

        Assert.Empty(sourcePrimary.Segments);
        Assert.Empty(sourceSecondary.Segments);
        Assert.Equal([first], targetPrimary.Segments);
        Assert.Equal([second], targetSecondary.Segments);
        Assert.Equal(300, first.ProjectStartTick);
        Assert.Equal(400, second.ProjectStartTick);
        Assert.Single(document.History);
        AssertMatchesFull(compilation);

        document.Undo();
        Assert.Equal([first], sourcePrimary.Segments);
        Assert.Equal([second], sourceSecondary.Segments);
        Assert.Empty(targetPrimary.Segments);
        Assert.Empty(targetSecondary.Segments);
        Assert.Equal(100, first.ProjectStartTick);
        Assert.Equal(200, second.ProjectStartTick);
        Assert.False(document.IsModified);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void SegmentBatchOverlapRejectsWithoutMovingAnySource()
    {
        MidoraProject project = new(480);
        LogicalTrack target = new(project) { Name = "Target" };
        LogicalTrack source = new(project) { Name = "Source" };
        project.Tracks.AddRange([target, source]);
        Segment occupied = new(project) { ProjectStartTick = 100, LengthTicks = 100 };
        Segment first = new(project) { ProjectStartTick = 0, LengthTicks = 40 };
        Segment second = new(project) { ProjectStartTick = 50, LengthTicks = 40 };
        target.Segments.Add(occupied);
        source.Segments.Add(first);
        source.Segments.Add(second);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        long nextStableId = project.NextStableId;

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.MoveSegments(
                [first.Id, second.Id],
                first.Id,
                target.Id,
                newPrimaryStartTick: 80)));

        Assert.Equal([occupied], target.Segments);
        Assert.Equal([first, second], source.Segments);
        Assert.Equal((0L, 50L), (first.ProjectStartTick, second.ProjectStartTick));
        Assert.Equal(nextStableId, project.NextStableId);
        Assert.Empty(document.History);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void SegmentBatchDuplicateDeepCopiesAllOwnedStableIds()
    {
        MidoraProject project = new(480);
        LogicalTrack targetPrimary = new(project) { Name = "Target 1" };
        LogicalTrack targetSecondary = new(project) { Name = "Target 2" };
        LogicalTrack sourcePrimary = new(project) { Name = "Source 1" };
        LogicalTrack sourceSecondary = new(project) { Name = "Source 2" };
        project.Tracks.AddRange(
            [targetPrimary, targetSecondary, sourcePrimary, sourceSecondary]);
        MidoraId externalParameterId = MidoraId.FromSequence(999_999);
        Segment first = new(project) { ProjectStartTick = 10, LengthTicks = 100 };
        LogicalNote note = new(project)
        {
            StartTick = 4,
            LengthTicks = 20,
            Note = 60,
            Velocity = 100
        };
        LogicalParameterLane lane = new(project) { ParameterId = externalParameterId };
        CurvePoint point = new(project, 5, 0.5);
        first.Notes.Add(note);
        lane.Points.Add(point);
        first.ParameterLanes.Add(lane);
        Segment second = new(project) { ProjectStartTick = 210, LengthTicks = 100 };
        sourcePrimary.Segments.Add(first);
        sourceSecondary.Segments.Add(second);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.DuplicateSegments(
            [first.Id, second.Id],
            first.Id,
            targetPrimary.Id,
            newPrimaryStartTick: 500));

        Segment firstCopy = Assert.Single(targetPrimary.Segments);
        Segment secondCopy = Assert.Single(targetSecondary.Segments);
        LogicalNote noteCopy = Assert.Single(firstCopy.Notes);
        LogicalParameterLane laneCopy = Assert.Single(firstCopy.ParameterLanes);
        CurvePoint pointCopy = Assert.Single(laneCopy.Points);
        Assert.Equal((500L, 700L), (firstCopy.ProjectStartTick, secondCopy.ProjectStartTick));
        Assert.NotEqual(first.Id, firstCopy.Id);
        Assert.NotEqual(second.Id, secondCopy.Id);
        Assert.NotEqual(note.Id, noteCopy.Id);
        Assert.NotEqual(lane.Id, laneCopy.Id);
        Assert.NotEqual(point.Id, pointCopy.Id);
        Assert.Equal(externalParameterId, laneCopy.ParameterId);
        AssertMatchesFull(compilation);

        document.Undo();
        Assert.Empty(targetPrimary.Segments);
        Assert.Empty(targetSecondary.Segments);
        document.Redo();
        Assert.Same(firstCopy, Assert.Single(targetPrimary.Segments));
        Assert.Same(secondCopy, Assert.Single(targetSecondary.Segments));
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void LogicalParameterPointBatchUsesSharedDeltaAndAtomicValueMode()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument" };
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Amount",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 10
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
        CurvePoint first = new(project, 10, 2);
        CurvePoint second = new(project, 30, 4);
        CurvePoint unselected = new(project, 100, 6);
        lane.Points.AddRange([first, second, unselected]);
        segment.ParameterLanes.Add(lane);
        track.Segments.Add(segment);
        project.Tracks.Add(track);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.MoveLogicalParameterPoints(
            segment.Id,
            lane.Id,
            [second.Id, first.Id],
            tickDelta: 20));
        Assert.Equal([30L, 50L, 100L], lane.Points.Select(value => value.Tick));

        document.Execute(ProjectDomainEditCommands.SetLogicalParameterPointValues(
            segment.Id,
            lane.Id,
            [first.Id, second.Id],
            value: 1.5,
            ProjectBatchValueEditMode.RelativeAdjust));
        Assert.Equal([3.5, 5.5, 6], lane.Points.Select(value => value.Value));
        Assert.Equal(2, document.History.Count);
        AssertMatchesFull(compilation);

        long nextStableId = project.NextStableId;
        document.Execute(ProjectDomainEditCommands.MoveLogicalParameterPoints(
            segment.Id,
            lane.Id,
            [first.Id, second.Id],
            tickDelta: 50));
        Assert.Equal([80L, 100L], lane.Points.Select(value => value.Tick));
        Assert.Contains(lane.Points, value => value.Id == first.Id);
        Assert.DoesNotContain(lane.Points, value => value.Id == second.Id);
        Assert.Contains(lane.Points, value => value.Id == unselected.Id);
        Assert.Equal(nextStableId, project.NextStableId);
        Assert.Equal(3, document.History.Count);

        document.Undo();
        Assert.Equal([30L, 50L, 100L], lane.Points.Select(value => value.Tick));
        Assert.Contains(lane.Points, value => value.Id == second.Id);

        document.Undo();
        Assert.Equal([2d, 4d, 6d], lane.Points.Select(value => value.Value));
        document.Undo();
        Assert.Equal([10L, 30L, 100L], lane.Points.Select(value => value.Tick));
        Assert.False(document.IsModified);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void DuplicateNotesAppliesSharedTimeAndPitchDeltaInOneUndo()
    {
        (MidoraProject project, _, Segment segment, LogicalNote first, LogicalNote second) =
            CreateProject();
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.DuplicateLogicalNotes(
            segment.Id,
            [second.Id, first.Id],
            segment.Id,
            newEarliestStartTick: 30,
            pitchDelta: 5));

        LogicalNote[] copies = segment.Notes
            .Where(item => item.Id != first.Id && item.Id != second.Id)
            .ToArray();
        Assert.Equal([30L, 150L], copies.Select(item => item.StartTick));
        Assert.Equal([65, 69], copies.Select(item => item.Note));
        Assert.Equal([60L, 60L], copies.Select(item => item.LengthTicks));
        Assert.Single(document.History);
        AssertMatchesFull(compilation);

        document.Undo();
        Assert.Equal([first, second], segment.Notes);
        Assert.False(document.IsModified);

        document.Redo();
        Assert.Equal(copies, segment.Notes.Skip(2));
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void ExactSetLogicalNoteValuesIsAtomicAndUndoRestoresMixedValues()
    {
        (MidoraProject project, _, Segment segment, LogicalNote first, LogicalNote second) =
            CreateProject();
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.SetLogicalNoteValues(
            segment.Id,
            [second.Id, first.Id],
            lengthTicks: 90,
            velocity: 72));

        Assert.Equal((90L, 72), (first.LengthTicks, first.Velocity));
        Assert.Equal((90L, 72), (second.LengthTicks, second.Velocity));
        Assert.Single(document.History);
        AssertMatchesFull(compilation);

        document.Undo();

        Assert.Equal((60L, 100), (first.LengthTicks, first.Velocity));
        Assert.Equal((60L, 80), (second.LengthTicks, second.Velocity));
        Assert.False(document.IsModified);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void LogicalNoteBatchEndResizeSaturatesEachLengthIndependently()
    {
        (MidoraProject project, _, Segment segment, LogicalNote first, LogicalNote second) =
            CreateProject();
        first.LengthTicks = 100;
        second.LengthTicks = 20;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.AdjustLogicalNoteEdges(
            segment.Id,
            [first.Id, second.Id],
            startDelta: 0,
            endDelta: -60));

        Assert.Equal((40L, 1L), (first.LengthTicks, second.LengthTicks));
        Assert.Single(document.History);
        document.Undo();
        Assert.Equal((100L, 20L), (first.LengthTicks, second.LengthTicks));
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void LogicalNoteBatchStartResizeSaturatesEachLengthIndependently()
    {
        (MidoraProject project, _, Segment segment, LogicalNote first, LogicalNote second) =
            CreateProject();
        first.StartTick = 100;
        first.LengthTicks = 100;
        second.StartTick = 300;
        second.LengthTicks = 20;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.AdjustLogicalNoteEdges(
            segment.Id,
            [first.Id, second.Id],
            startDelta: 60,
            endDelta: 0));

        Assert.Equal((160L, 40L), (first.StartTick, first.LengthTicks));
        Assert.Equal((319L, 1L), (second.StartTick, second.LengthTicks));
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void SegmentBatchEndResizeSaturatesEachLengthIndependentlyAndUsesOneUndo()
    {
        MidoraProject project = new(480);
        LogicalTrack firstTrack = new(project) { Name = "First" };
        LogicalTrack secondTrack = new(project) { Name = "Second" };
        Segment first = new(project) { ProjectStartTick = 0, LengthTicks = 100 };
        Segment second = new(project) { ProjectStartTick = 0, LengthTicks = 20 };
        firstTrack.Segments.Add(first);
        secondTrack.Segments.Add(second);
        project.Tracks.Add(firstTrack);
        project.Tracks.Add(secondTrack);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.AdjustSegmentEdges(
            [first.Id, second.Id],
            startDelta: 0,
            endDelta: -60));

        Assert.Equal((40L, 1L), (first.LengthTicks, second.LengthTicks));
        Assert.Single(document.History);
        document.Undo();
        Assert.Equal((100L, 20L), (first.LengthTicks, second.LengthTicks));
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void SegmentBatchStartResizeSaturatesEachLengthIndependently()
    {
        MidoraProject project = new(480);
        LogicalTrack firstTrack = new(project) { Name = "First" };
        LogicalTrack secondTrack = new(project) { Name = "Second" };
        Segment first = new(project) { ProjectStartTick = 100, LengthTicks = 100 };
        Segment second = new(project) { ProjectStartTick = 300, LengthTicks = 20 };
        firstTrack.Segments.Add(first);
        secondTrack.Segments.Add(second);
        project.Tracks.Add(firstTrack);
        project.Tracks.Add(secondTrack);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.AdjustSegmentEdges(
            [first.Id, second.Id],
            startDelta: 60,
            endDelta: 0));

        Assert.Equal((160L, 40L), (first.ProjectStartTick, first.LengthTicks));
        Assert.Equal((319L, 1L), (second.ProjectStartTick, second.LengthTicks));
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void SegmentStartResizeExpandsPastSourceZeroWithoutDiscardingHiddenContent()
    {
        MidoraProject project = new(480);
        LogicalTrack track = new(project) { Name = "Track" };
        Segment segment = new(project)
        {
            ProjectStartTick = 100,
            LengthTicks = 100,
            ContentOffsetTick = 20
        };
        LogicalNote hidden = new(project)
        {
            StartTick = 0,
            LengthTicks = 10,
            Note = 48,
            Velocity = 80
        };
        LogicalNote exposed = new(project)
        {
            StartTick = 30,
            LengthTicks = 10,
            Note = 60,
            Velocity = 90
        };
        LogicalParameterLane lane = new(project) { ParameterId = MidoraId.FromSequence(900_010) };
        CurvePoint hiddenPoint = new(project, 5, 0.25);
        CurvePoint exposedPoint = new(project, 40, 0.75);
        lane.Points.AddRange([hiddenPoint, exposedPoint]);
        segment.Notes.AddRange([hidden, exposed]);
        segment.ParameterLanes.Add(lane);
        track.Segments.Add(segment);
        project.Tracks.Add(track);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.AdjustSegmentEdges(
            [segment.Id],
            startDelta: -50,
            endDelta: 0));

        Assert.Equal((50L, 150L, 0L), (
            segment.ProjectStartTick,
            segment.LengthTicks,
            segment.ContentOffsetTick));
        Assert.Equal((30L, 60L), (hidden.StartTick, exposed.StartTick));
        Assert.Equal([35L, 70L], lane.Points.Select(value => value.Tick));
        Assert.Equal(80, segment.ProjectStartTick + hidden.StartTick - segment.ContentOffsetTick);
        Assert.Equal(110, segment.ProjectStartTick + exposed.StartTick - segment.ContentOffsetTick);

        document.Undo();
        Assert.Equal((100L, 100L, 20L), (
            segment.ProjectStartTick,
            segment.LengthTicks,
            segment.ContentOffsetTick));
        Assert.Equal((0L, 30L), (hidden.StartTick, exposed.StartTick));
        Assert.Equal([5L, 40L], lane.Points.Select(value => value.Tick));
        document.Redo();
        Assert.Equal([35L, 70L], lane.Points.Select(value => value.Tick));
    }

    [Fact]
    public void VelocityPaintUsesPerNoteValuesAndOneUndoUnit()
    {
        (MidoraProject project, _, Segment segment, LogicalNote first, LogicalNote second) = CreateProject();
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.PaintLogicalNoteVelocities(
            segment.Id,
            new Dictionary<MidoraId, int>
            {
                [second.Id] = 117,
                [first.Id] = 24
            }));

        Assert.Equal((24, 117), (first.Velocity, second.Velocity));
        Assert.Single(document.History);
        document.Undo();
        Assert.Equal((100, 80), (first.Velocity, second.Velocity));
        Assert.False(document.IsModified);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void TemplateVelocityPaintUsesPerNoteValuesAndOneUndoUnit()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument" };
        SubVoice voice = new(project) { Name = "Voice" };
        TemplateEvent first = TemplateEvent.Note(project, 0, 60, 60, 100);
        TemplateEvent second = TemplateEvent.Note(project, 120, 60, 64, 80);
        voice.Events.AddRange([first, second]);
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.PaintTemplateNoteVelocities(
            instrument.Id,
            voice.Id,
            new Dictionary<MidoraId, int>
            {
                [second.Id] = 117,
                [first.Id] = 24
            }));

        Assert.Equal((24, 117), (first.Value, second.Value));
        Assert.Single(document.History);
        document.Undo();
        Assert.Equal((100, 80), (first.Value, second.Value));
        Assert.False(document.IsModified);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void TemplateNoteBatchMoveUsesOneUndoAndPreservesRelativePlacement()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument", TemplateLengthTicks = 480 };
        SubVoice voice = new(project) { Name = "Voice" };
        TemplateEvent first = TemplateEvent.Note(project, 0, 60, 60, 100);
        TemplateEvent second = TemplateEvent.Note(project, 120, 90, 64, 80);
        voice.Events.AddRange([first, second]);
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.MoveTemplateNotes(
            instrument.Id,
            voice.Id,
            [second.Id, first.Id],
            tickDelta: 30,
            pitchDelta: 2));

        Assert.Equal((30L, 62), (first.Tick, first.Number));
        Assert.Equal((150L, 66), (second.Tick, second.Number));
        Assert.Single(document.History);
        document.Undo();
        Assert.Equal((0L, 60), (first.Tick, first.Number));
        Assert.Equal((120L, 64), (second.Tick, second.Number));
        Assert.False(document.IsModified);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void TemplateNoteBatchEndResizeSaturatesEachLengthIndependently()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument", TemplateLengthTicks = 480 };
        SubVoice voice = new(project) { Name = "Voice" };
        TemplateEvent first = TemplateEvent.Note(project, 0, 100, 60, 100);
        TemplateEvent second = TemplateEvent.Note(project, 120, 20, 64, 80);
        voice.Events.AddRange([first, second]);
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.AdjustTemplateNoteEdges(
            instrument.Id,
            voice.Id,
            [first.Id, second.Id],
            startDelta: 0,
            endDelta: -60));

        Assert.Equal((40L, 1L), (first.LengthTicks, second.LengthTicks));
        Assert.Single(document.History);
        document.Undo();
        Assert.Equal((100L, 20L), (first.LengthTicks, second.LengthTicks));
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void TemplateNoteBatchStartResizeSaturatesEachLengthIndependently()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument", TemplateLengthTicks = 480 };
        SubVoice voice = new(project) { Name = "Voice" };
        TemplateEvent first = TemplateEvent.Note(project, 100, 100, 60, 100);
        TemplateEvent second = TemplateEvent.Note(project, 300, 20, 64, 80);
        voice.Events.AddRange([first, second]);
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.AdjustTemplateNoteEdges(
            instrument.Id,
            voice.Id,
            [first.Id, second.Id],
            startDelta: 60,
            endDelta: 0));

        Assert.Equal((160L, 40L), (first.Tick, first.LengthTicks));
        Assert.Equal((319L, 1L), (second.Tick, second.LengthTicks));
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void TemplateNoteMoveDiscardsOnlyNotesThatCrossThePitchBoundaryAndUndoRestoresThem()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument", TemplateLengthTicks = 480 };
        SubVoice voice = new(project) { Name = "Voice" };
        TemplateEvent first = TemplateEvent.Note(project, 0, 60, 60, 100);
        TemplateEvent second = TemplateEvent.Note(project, 120, 90, 127, 80);
        voice.Events.AddRange([first, second]);
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.MoveTemplateNotes(
            instrument.Id,
            voice.Id,
            [first.Id, second.Id],
            tickDelta: 20,
            pitchDelta: 1));

        Assert.Equal((20L, 61), (first.Tick, first.Number));
        Assert.DoesNotContain(second, voice.Events);
        AssertMatchesFull(compilation);

        document.Undo();

        Assert.Equal((0L, 60), (first.Tick, first.Number));
        Assert.Equal((120L, 127), (second.Tick, second.Number));
        Assert.Equal([first, second], voice.Events);
        Assert.False(document.IsModified);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void TemplateNoteCopyDragCommandDeepCopiesNotesAndKeepsRedoIdentity()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument", TemplateLengthTicks = 480 };
        SubVoice voice = new(project) { Name = "Voice" };
        TemplateEvent first = TemplateEvent.Note(project, 0, 60, 60, 100);
        TemplateEvent second = TemplateEvent.Note(project, 120, 90, 64, 80);
        first.NumberMappings.Add(new ValueMappingStep(project) { Constant = 2 });
        voice.Events.AddRange([first, second]);
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.DuplicateTemplateNotes(
            instrument.Id,
            voice.Id,
            [second.Id, first.Id],
            newEarliestTick: 500,
            pitchDelta: 2));

        TemplateEvent[] copies = voice.Events.Skip(2).ToArray();
        Assert.Equal([500L, 620L], copies.Select(item => item.Tick));
        Assert.Equal([62, 66], copies.Select(item => item.Number));
        Assert.Equal(710, instrument.TemplateLengthTicks);
        Assert.NotEqual(first.Id, copies[0].Id);
        Assert.Equal(first.NumberMappings.Id, copies[0].NumberMappings.Id);
        Assert.Single(copies[0].NumberMappings);
        Assert.Same(first.NumberMappings[0], copies[0].NumberMappings[0]);
        Assert.Single(document.History);
        AssertMatchesFull(compilation);

        document.Undo();
        Assert.Equal([first, second], voice.Events);
        Assert.Equal(480, instrument.TemplateLengthTicks);
        Assert.False(document.IsModified);

        document.Redo();
        Assert.Equal(copies, voice.Events.Skip(2));
        Assert.Equal(710, instrument.TemplateLengthTicks);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void LogicalParameterPointCombinedDragIsOneAtomicUndoUnit()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument" };
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Amount",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 10
        };
        instrument.LogicalParameters.Add(parameter);
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project) { Name = "Track", EventInstrumentId = instrument.Id };
        Segment segment = new(project) { LengthTicks = 480 };
        LogicalParameterLane lane = new(project) { ParameterId = parameter.Id };
        CurvePoint first = new(project, 10, 2);
        CurvePoint second = new(project, 30, 4);
        lane.Points.AddRange([first, second]);
        segment.ParameterLanes.Add(lane);
        track.Segments.Add(segment);
        project.Tracks.Add(track);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.AdjustLogicalParameterPoints(
            segment.Id,
            lane.Id,
            [second.Id, first.Id],
            tickDelta: 20,
            valueDelta: 1.5));

        Assert.Equal([30L, 50L], lane.Points.Select(value => value.Tick));
        Assert.Equal([3.5, 5.5], lane.Points.Select(value => value.Value));
        Assert.Single(document.History);
        AssertMatchesFull(compilation);

        document.Undo();
        Assert.Equal([10L, 30L], lane.Points.Select(value => value.Tick));
        Assert.Equal([2d, 4d], lane.Points.Select(value => value.Value));
        Assert.False(document.IsModified);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void ExactSetLogicalParameterPointsUsesOneUndoAndValidatesWholeBatch()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument" };
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Amount",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 10
        };
        instrument.LogicalParameters.Add(parameter);
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project) { Name = "Track", EventInstrumentId = instrument.Id };
        Segment segment = new(project) { LengthTicks = 480 };
        LogicalParameterLane lane = new(project) { ParameterId = parameter.Id };
        CurvePoint first = new(project, 10, 2, CurveInterpolation.Linear);
        CurvePoint second = new(project, 30, 4, CurveInterpolation.Linear);
        lane.Points.AddRange([first, second]);
        segment.ParameterLanes.Add(lane);
        track.Segments.Add(segment);
        project.Tracks.Add(track);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.SetLogicalParameterPoints(
            segment.Id,
            lane.Id,
            [first.Id, second.Id],
            value: 7.5,
            interpolation: CurveInterpolation.Step));

        Assert.Equal([7.5, 7.5], lane.Points.Select(item => item.Value));
        Assert.All(lane.Points, item => Assert.Equal(CurveInterpolation.Step, item.Interpolation));
        Assert.Single(document.History);
        AssertMatchesFull(compilation);

        document.Undo();

        Assert.Equal([2d, 4d], lane.Points.Select(item => item.Value));
        Assert.All(lane.Points, item => Assert.Equal(CurveInterpolation.Linear, item.Interpolation));
        Assert.False(document.IsModified);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void MixedConductorBatchDeleteRejectsRequiredInitialEventsAndRestoresOrdering()
    {
        MidoraProject project = new(480);
        TempoChange tempo = new(project, 100, 90m);
        TimeSignatureChange timeSignature = new(project, 200, 3, 4);
        ProjectMarker marker = new(project, 150, "Marker");
        project.Conductor.Tempos.Add(tempo);
        project.Conductor.TimeSignatures.Add(timeSignature);
        project.Conductor.Markers.Add(marker);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.DeleteConductorEvents(
                [project.Conductor.Tempos[0].Id, marker.Id])));
        Assert.Empty(document.History);

        document.Execute(ProjectDomainEditCommands.DeleteConductorEvents(
            [timeSignature.Id, marker.Id, tempo.Id]));
        Assert.Single(project.Conductor.Tempos);
        Assert.Single(project.Conductor.TimeSignatures);
        Assert.Empty(project.Conductor.Markers);
        Assert.Single(document.History);

        document.Undo();
        Assert.Equal([0L, 100L], project.Conductor.Tempos.Select(value => value.Tick));
        Assert.Equal([0L, 200L], project.Conductor.TimeSignatures.Select(value => value.Tick));
        Assert.Same(marker, Assert.Single(project.Conductor.Markers));
        AssertMatchesFull(compilation);
    }

    private static (
        MidoraProject Project,
        LogicalTrack Track,
        Segment Segment,
        LogicalNote First,
        LogicalNote Second) CreateProject()
    {
        MidoraProject project = new(480);
        LogicalTrack track = new(project) { Name = "Track" };
        Segment segment = new(project) { LengthTicks = 480 };
        LogicalNote first = new(project)
        {
            StartTick = 0,
            LengthTicks = 60,
            Note = 60,
            Velocity = 100
        };
        LogicalNote second = new(project)
        {
            StartTick = 120,
            LengthTicks = 60,
            Note = 64,
            Velocity = 80
        };
        segment.Notes.Add(first);
        segment.Notes.Add(second);
        track.Segments.Add(segment);
        project.Tracks.Add(track);
        return (project, track, segment, first, second);
    }

    private static ProjectDocumentSession PersistedDocument(ProjectCompilationSession compilation)
    {
        ProjectDocumentSession result = new(compilation, ProjectDocumentOrigin.Persisted);
        result.MarkSaveSucceeded();
        return result;
    }

    private static void AssertMatchesFull(ProjectCompilationSession compilation)
    {
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult expected = compiler.CompileFull(compilation.Project);
        Assert.Equal(expected.Fingerprint, compilation.LastAttempt.Fingerprint);
        Assert.Equal(expected.IsConsumable, compilation.LastAttempt.IsConsumable);
    }
}
