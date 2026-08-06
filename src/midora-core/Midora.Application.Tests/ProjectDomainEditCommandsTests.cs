using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectDomainEditCommandsTests
{
    [Fact]
    public void LogicalTrackRenameTrimsValidTextAllowsEmptyAndRejectsInvalidText()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        ProjectEditExecution noOp = document.Execute(
            ProjectDomainEditCommands.RenameLogicalTrack(track.Id, "  Track  "));
        Assert.False(noOp.Changed);

        document.Execute(ProjectDomainEditCommands.RenameLogicalTrack(track.Id, "   "));
        Assert.Equal(string.Empty, track.Name);
        Assert.True(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Equal("Track", track.Name);
        Assert.False(document.IsModified);
        Assert.Throws<ArgumentException>(() => document.Execute(
            ProjectDomainEditCommands.RenameLogicalTrack(track.Id, "Line\nBreak")));
        Assert.Throws<ArgumentException>(() => document.Execute(
            ProjectDomainEditCommands.RenameLogicalTrack(track.Id, new string('x', 257))));
        Assert.Throws<ArgumentException>(() => document.Execute(
            ProjectDomainEditCommands.RenameLogicalTrack(track.Id, "\ud800")));
        Assert.Single(document.History);
        Assert.True(document.CanRedo);
    }

    [Fact]
    public void LogicalTrackBindingPreservesContentAndRestoresExactLastKnownName()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        Segment segment = track.Segments[0];
        LogicalNote note = segment.Notes[0];
        LogicalParameterLane lane = segment.ParameterLanes[0];
        EventInstrument second = CreateInstrument(project, "Strings");
        track.LastBoundEventInstrumentName = "Historical";
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.BindLogicalTrack(track.Id, second.Id));

        Assert.Equal(second.Id, track.EventInstrumentId);
        Assert.Equal("Strings", track.LastBoundEventInstrumentName);
        Assert.Same(segment, track.Segments[0]);
        Assert.Same(note, track.Segments[0].Notes[0]);
        Assert.Same(lane, track.Segments[0].ParameterLanes[0]);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();

        Assert.Equal(project.EventInstruments[0].Id, track.EventInstrumentId);
        Assert.Equal("Historical", track.LastBoundEventInstrumentName);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Execute(ProjectDomainEditCommands.BindLogicalTrack(track.Id, null));
        Assert.Null(track.EventInstrumentId);
        Assert.Equal("Piano", track.LastBoundEventInstrumentName);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void LogicalTrackReorderAndDeleteRestoreOrderIdentityAndRenderSelection()
    {
        MidoraProject project = CreateProject();
        LogicalTrack first = project.Tracks[0];
        LogicalTrack second = new(project) { Name = "Second" };
        project.Tracks.Add(second);
        project.AudioRender.ExplicitLogicalTrackIds.Add(first.Id);
        UInt128 nextStableId = project.NextStableId;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.ReorderLogicalTrack(first.Id, 1));
        Assert.Equal([second, first], project.Tracks);
        document.Undo();
        Assert.Equal([first, second], project.Tracks);

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.DeleteLogicalTrack(first.Id, nonEmptyDeletionConfirmed: false)));
        document.Execute(ProjectDomainEditCommands.DeleteLogicalTrack(
            first.Id,
            nonEmptyDeletionConfirmed: true));
        Assert.DoesNotContain(first, project.Tracks);
        Assert.DoesNotContain(first.Id, project.AudioRender.ExplicitLogicalTrackIds);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Same(first, project.Tracks[0]);
        Assert.Contains(first.Id, project.AudioRender.ExplicitLogicalTrackIds);
        Assert.Equal(nextStableId, project.NextStableId);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void EventInstrumentRenameIsValidatedAndUpdatesBoundLastKnownNameReversibly()
    {
        MidoraProject project = CreateProject();
        EventInstrument instrument = project.EventInstruments[0];
        LogicalTrack track = project.Tracks[0];
        track.LastBoundEventInstrumentName = "Before Snapshot";
        _ = CreateInstrument(project, "Strings");
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.RenameEventInstrument(
            instrument.Id,
            "  Grand Piano  "));

        Assert.Equal("Grand Piano", instrument.Name);
        Assert.Equal("Grand Piano", track.LastBoundEventInstrumentName);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Equal("Piano", instrument.Name);
        Assert.Equal("Before Snapshot", track.LastBoundEventInstrumentName);
        Assert.Throws<ArgumentException>(() => document.Execute(
            ProjectDomainEditCommands.RenameEventInstrument(instrument.Id, " strings ")));
        Assert.Throws<ArgumentException>(() => document.Execute(
            ProjectDomainEditCommands.RenameEventInstrument(instrument.Id, "Bad\rName")));
        Assert.False(document.IsModified);
    }

    [Fact]
    public void EventInstrumentDeleteRequiresConfirmationAndRestoresBindingsAndIndex()
    {
        MidoraProject project = CreateProject();
        EventInstrument first = project.EventInstruments[0];
        EventInstrument second = CreateInstrument(project, "Strings");
        LogicalTrack track = project.Tracks[0];
        track.LastBoundEventInstrumentName = "Exact Old Snapshot";
        UInt128 nextStableId = project.NextStableId;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.DeleteEventInstrument(
                first.Id,
                referencedDeletionConfirmed: false)));

        document.Execute(ProjectDomainEditCommands.DeleteEventInstrument(
            first.Id,
            referencedDeletionConfirmed: true));

        Assert.Equal([second], project.EventInstruments);
        Assert.Null(track.EventInstrumentId);
        Assert.Equal("Piano", track.LastBoundEventInstrumentName);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Same(first, project.EventInstruments[0]);
        Assert.Equal(first.Id, track.EventInstrumentId);
        Assert.Equal("Exact Old Snapshot", track.LastBoundEventInstrumentName);
        Assert.Equal(nextStableId, project.NextStableId);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void FolderCommandsRestoreNamesOrderAndMembershipWithoutRecompilationDrift()
    {
        MidoraProject project = CreateProject();
        EventInstrument instrument = project.EventInstruments[0];
        EventInstrumentLibraryFolder first = EventInstrumentLibrary.CreateFolder(project, "Keys");
        EventInstrumentLibraryFolder second = EventInstrumentLibrary.CreateFolder(project, "Orchestral");
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        long initialFingerprint = compilation.LastAttempt.Fingerprint;

        document.Execute(ProjectDomainEditCommands.RenameEventInstrumentFolder(first.Id, "  Keyboard  "));
        document.Execute(ProjectDomainEditCommands.MoveEventInstrumentToFolder(instrument.Id, first.Id));
        document.Execute(ProjectDomainEditCommands.ReorderEventInstrumentFolder(first.Id, 1));
        Assert.Equal("Keyboard", first.Name);
        Assert.Equal(first.Id, instrument.LibraryFolderId);
        Assert.Equal([second, first], project.EventInstrumentFolders);
        Assert.Equal(initialFingerprint, compilation.LastAttempt.Fingerprint);

        document.Execute(ProjectDomainEditCommands.DeleteEventInstrumentFolder(first.Id));
        Assert.Null(instrument.LibraryFolderId);
        Assert.Equal([second], project.EventInstrumentFolders);
        document.Undo();
        Assert.Same(first, project.EventInstrumentFolders[1]);
        Assert.Equal(first.Id, instrument.LibraryFolderId);

        document.Undo();
        Assert.Equal([first, second], project.EventInstrumentFolders);
        document.Undo();
        Assert.Null(instrument.LibraryFolderId);
        document.Undo();
        Assert.Equal("Keys", first.Name);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void EventInstrumentReorderIsProjectHistoryButDoesNotChangeCanonicalResult()
    {
        MidoraProject project = CreateProject();
        EventInstrument first = project.EventInstruments[0];
        EventInstrument second = CreateInstrument(project, "Strings");
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        long originalFingerprint = compilation.LastAttempt.Fingerprint;

        document.Execute(ProjectDomainEditCommands.ReorderEventInstrument(first.Id, 1));
        Assert.Equal([second, first], project.EventInstruments);
        Assert.True(document.IsModified);
        Assert.Equal(originalFingerprint, compilation.LastAttempt.Fingerprint);

        document.Undo();
        Assert.Equal([first, second], project.EventInstruments);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void DamagedPlaceholderDeletesRestoreBindingsSelectionAndStableOrdering()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        MidoraId damagedInstrumentId = project.AllocateStableId();
        DamagedProjectObject damagedInstrument = new(
            damagedInstrumentId,
            "Damaged Piano",
            "event-instruments/damaged.pb",
            "broken",
            1);
        project.DamagedEventInstruments.Add(damagedInstrument);
        track.EventInstrumentId = damagedInstrumentId;
        track.LastBoundEventInstrumentName = "Old";
        MidoraId damagedTrackId = project.AllocateStableId();
        DamagedProjectObject damagedTrack = new(
            damagedTrackId,
            "Damaged Track",
            "logical-tracks/damaged.pb",
            "broken",
            0);
        project.DamagedLogicalTracks.Add(damagedTrack);
        project.AudioRender.ExplicitLogicalTrackIds.Add(damagedTrackId);
        UInt128 nextStableId = project.NextStableId;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.DeleteDamagedEventInstrument(damagedInstrumentId));
        document.Execute(ProjectDomainEditCommands.DeleteDamagedLogicalTrack(damagedTrackId));
        Assert.Empty(project.DamagedEventInstruments);
        Assert.Empty(project.DamagedLogicalTracks);
        Assert.Null(track.EventInstrumentId);
        Assert.Equal("Damaged Piano", track.LastBoundEventInstrumentName);
        Assert.DoesNotContain(damagedTrackId, project.AudioRender.ExplicitLogicalTrackIds);

        document.Undo();
        document.Undo();
        Assert.Same(damagedInstrument, project.DamagedEventInstruments[0]);
        Assert.Same(damagedTrack, project.DamagedLogicalTracks[0]);
        Assert.Equal(damagedInstrumentId, track.EventInstrumentId);
        Assert.Equal("Old", track.LastBoundEventInstrumentName);
        Assert.Contains(damagedTrackId, project.AudioRender.ExplicitLogicalTrackIds);
        Assert.Equal(nextStableId, project.NextStableId);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void SegmentMoveSupportsCrossTrackAndRejectsNegativeOrOverlappingTargets()
    {
        MidoraProject project = CreateProject();
        LogicalTrack sourceTrack = project.Tracks[0];
        Segment segment = sourceTrack.Segments[0];
        LogicalTrack targetTrack = new(project)
        {
            Name = "Target",
            EventInstrumentId = sourceTrack.EventInstrumentId
        };
        Segment blocker = new(project) { ProjectStartTick = 2_000, LengthTicks = 480 };
        targetTrack.Segments.Add(blocker);
        project.Tracks.Add(targetTrack);
        UInt128 nextStableId = project.NextStableId;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.MoveSegment(segment.Id, targetTrack.Id, -1)));
        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.MoveSegment(segment.Id, targetTrack.Id, 1_800)));

        document.Execute(ProjectDomainEditCommands.MoveSegment(segment.Id, targetTrack.Id, 960));
        Assert.Empty(sourceTrack.Segments);
        Assert.Same(segment, targetTrack.Segments[0]);
        Assert.Equal(960, segment.ProjectStartTick);
        Assert.Equal(nextStableId, project.NextStableId);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Same(segment, sourceTrack.Segments[0]);
        Assert.Equal(0, segment.ProjectStartTick);
        Assert.Equal([blocker], targetTrack.Segments);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void SegmentWindowEditPreservesHiddenContentAndRejectsInvalidRanges()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        Segment segment = track.Segments[0];
        LogicalNote hiddenNote = new(project)
        {
            StartTick = 1_200,
            LengthTicks = 120,
            Note = 64,
            Velocity = 90
        };
        segment.Notes.Add(hiddenNote);
        Segment blocker = new(project) { ProjectStartTick = 1_200, LengthTicks = 240 };
        track.Segments.Add(blocker);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.SetSegmentWindow(segment.Id, 0, 0, 0)));
        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.SetSegmentWindow(segment.Id, 400, 960, 0)));

        document.Execute(ProjectDomainEditCommands.SetSegmentWindow(segment.Id, 240, 480, 240));
        Assert.Equal(240, segment.ProjectStartTick);
        Assert.Equal(480, segment.LengthTicks);
        Assert.Equal(240, segment.ContentOffsetTick);
        Assert.Contains(hiddenNote, segment.Notes);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Equal(0, segment.ProjectStartTick);
        Assert.Equal(960, segment.LengthTicks);
        Assert.Equal(0, segment.ContentOffsetTick);
        Assert.Contains(hiddenNote, segment.Notes);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void SegmentDeleteRequiresConfirmationAndRestoresExactObjectAndIndex()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        Segment segment = track.Segments[0];
        UInt128 nextStableId = project.NextStableId;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.DeleteSegment(segment.Id, nonEmptyDeletionConfirmed: false)));
        document.Execute(ProjectDomainEditCommands.DeleteSegment(
            segment.Id,
            nonEmptyDeletionConfirmed: true));
        Assert.Empty(track.Segments);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Same(segment, track.Segments[0]);
        Assert.Equal(nextStableId, project.NextStableId);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void JoinSegmentsPreservesAbsoluteContentConflictRuleAndStableIdCounter()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        Segment left = track.Segments[0];
        left.LengthTicks = 480;
        LogicalParameterLane leftLane = left.ParameterLanes[0];
        leftLane.Points.Clear();
        leftLane.Points.Add(new CurvePoint(project, 960, 0.25));
        Segment right = new(project)
        {
            ProjectStartTick = 960,
            LengthTicks = 480,
            ContentOffsetTick = 0
        };
        right.Notes.Add(new LogicalNote(project)
        {
            StartTick = 0,
            LengthTicks = 240,
            Note = 67,
            Velocity = 90
        });
        LogicalParameterLane rightLane = new(project) { ParameterId = leftLane.ParameterId };
        rightLane.Points.Add(new CurvePoint(project, 0, 0.75));
        right.ParameterLanes.Add(rightLane);
        track.Segments.Add(right);
        UInt128 nextStableId = project.NextStableId;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.JoinSegments(left.Id, right.Id));

        Segment joined = Assert.Single(track.Segments);
        Assert.Equal(left.Id, joined.Id);
        Assert.Equal(0, joined.ProjectStartTick);
        Assert.Equal(1_440, joined.LengthTicks);
        Assert.Contains(joined.Notes, value => value.Id == right.Notes[0].Id && value.StartTick == 960);
        LogicalParameterLane joinedLane = Assert.Single(joined.ParameterLanes);
        CurvePoint point = Assert.Single(joinedLane.Points, value => value.Tick == 960);
        Assert.Equal(0.75, point.Value);
        Assert.Equal(nextStableId, project.NextStableId);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Equal([left, right], track.Segments);
        Assert.Equal(nextStableId, project.NextStableId);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Redo();
        Assert.Same(joined, Assert.Single(track.Segments));
        Assert.Equal(nextStableId, project.NextStableId);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    private static ProjectDocumentSession PersistedDocument(ProjectCompilationSession compilation) =>
        new(compilation, ProjectDocumentOrigin.Persisted);

    private static MidoraProject CreateProject()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = CreateInstrument(project, "Piano");
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Expression",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1,
            DefaultValue = 0.5
        };
        instrument.LogicalParameters.Add(parameter);
        LogicalTrack track = new(project) { Name = "Track", EventInstrumentId = instrument.Id };
        Segment segment = new(project) { LengthTicks = 960 };
        segment.Notes.Add(new LogicalNote(project)
        {
            LengthTicks = 480,
            Note = 60,
            Velocity = 100
        });
        LogicalParameterLane lane = new(project) { ParameterId = parameter.Id };
        lane.Points.Add(new CurvePoint(project, 0, 0.5));
        segment.ParameterLanes.Add(lane);
        track.Segments.Add(segment);
        project.Tracks.Add(track);
        return project;
    }

    private static EventInstrument CreateInstrument(MidoraProject project, string name)
    {
        EventInstrument instrument = new(project)
        {
            Name = name,
            TemplateLengthTicks = 480,
            OverlapPolicy = OverlapPolicy.Warn
        };
        SubVoice voice = new(project);
        voice.Events.Add(TemplateEvent.Note(project, 0, 480, 60, 100));
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        return instrument;
    }

    private static void AssertCurrentCompilationMatchesFull(ProjectCompilationSession compilation)
    {
        using MidoraCompiler fullCompiler = new();
        CanonicalCompiledResult expected = fullCompiler.CompileFull(compilation.Project);
        CanonicalCompiledResult actual = compilation.LastAttempt;
        Assert.Equal(expected.IsConsumable, actual.IsConsumable);
        Assert.Equal(expected.IsPartial, actual.IsPartial);
        Assert.Equal(expected.StartTick, actual.StartTick);
        Assert.Equal(expected.EndTick, actual.EndTick);
        Assert.Equal(expected.Fingerprint, actual.Fingerprint);
        Assert.Equal(expected.Statistics, actual.Statistics);
        Assert.Equal(expected.Events.ToArray(), actual.Events.ToArray());
        Assert.Equal(expected.Allocations.ToArray(), actual.Allocations.ToArray());
        Assert.Equal(expected.Conductor.Tempos.ToArray(), actual.Conductor.Tempos.ToArray());
        Assert.Equal(
            expected.Conductor.TimeSignatures.ToArray(),
            actual.Conductor.TimeSignatures.ToArray());
        Assert.Equal(
            expected.Conductor.KeySignatures.ToArray(),
            actual.Conductor.KeySignatures.ToArray());
        Assert.Equal(expected.Conductor.Markers.ToArray(), actual.Conductor.Markers.ToArray());
        Assert.Equal(expected.Conductor.EndMarker, actual.Conductor.EndMarker);
        Assert.Equal(expected.Diagnostics, actual.Diagnostics);
    }
}
