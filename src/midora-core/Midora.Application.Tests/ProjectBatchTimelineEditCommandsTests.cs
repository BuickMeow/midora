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
    public void InvalidBatchResultRejectsBeforeAnyMutationOrHistoryEntry()
    {
        (MidoraProject project, _, Segment segment, LogicalNote first, LogicalNote second) =
            CreateProject();
        second.Note = 127;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        long nextStableId = project.NextStableId;

        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.MoveLogicalNotes(
                segment.Id,
                [first.Id, second.Id],
                tickDelta: 10,
                pitchDelta: 1)));

        Assert.Equal((0, 60), (first.StartTick, first.Note));
        Assert.Equal((120, 127), (second.StartTick, second.Note));
        Assert.Equal(nextStableId, project.NextStableId);
        Assert.Empty(document.History);
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
        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.MoveLogicalParameterPoints(
                segment.Id,
                lane.Id,
                [first.Id, second.Id],
                tickDelta: 50)));
        Assert.Equal([30L, 50L, 100L], lane.Points.Select(value => value.Tick));
        Assert.Equal(nextStableId, project.NextStableId);
        Assert.Equal(2, document.History.Count);

        document.Undo();
        Assert.Equal([2d, 4d, 6d], lane.Points.Select(value => value.Value));
        document.Undo();
        Assert.Equal([10L, 30L, 100L], lane.Points.Select(value => value.Tick));
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
