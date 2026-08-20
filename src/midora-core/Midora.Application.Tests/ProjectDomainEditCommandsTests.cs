using Midora.Audio;
using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;
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

        Assert.Equal(second.Id, project.ResolveEventInstrumentDefinitionId(track));
        Assert.Equal("Strings", track.LastBoundEventInstrumentName);
        Assert.Same(segment, track.Segments[0]);
        Assert.Same(note, track.Segments[0].Notes[0]);
        Assert.Same(lane, track.Segments[0].ParameterLanes[0]);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();

        Assert.Equal(project.EventInstruments[0].Id, project.ResolveEventInstrumentDefinitionId(track));
        Assert.Equal("Historical", track.LastBoundEventInstrumentName);
        AssertCurrentCompilationMatchesFull(compilation);

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.BindLogicalTrack(track.Id, null)));
        Assert.Equal(project.EventInstruments[0].Id, project.ResolveEventInstrumentDefinitionId(track));
        Assert.Equal("Historical", track.LastBoundEventInstrumentName);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public async Task BackgroundBindingChangeRebuildsExistingSegmentForTheNewInstrument()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = Assert.Single(project.Tracks);
        Assert.Single(track.Segments).ParameterLanes.Clear();
        EventInstrument originalInstrument = project.EventInstruments[0];
        EventInstrument replacementInstrument = CreateInstrument(project, "Strings");
        TemplateEvent replacementNote = Assert.Single(
            Assert.Single(replacementInstrument.SubVoices).Events,
            value => value.Kind == TemplateEventKind.Note);
        replacementNote.Number = 72;
        using ProjectCompilationSession compilation = new(
            project,
            executionMode: ProjectCompilationExecutionMode.Background,
            backgroundDebounce: TimeSpan.Zero);
        ProjectDocumentSession document = PersistedDocument(compilation);
        CanonicalAudioUnitFragment before = Assert.Single(
            CanonicalAudioUnitProjection.Create(compilation.LastAttempt).Fragments.ToArray());

        document.Execute(ProjectDomainEditCommands.BindLogicalTrack(
            track.Id,
            replacementInstrument.Id));
        CanonicalCompiledResult current = await compilation.EnsureCurrentCompilationAsync();

        CanonicalMidiEvent noteOn = Assert.Single(
            current.Events.ToArray(),
            value => value.Role == CanonicalEventRole.NoteOn);
        Assert.Equal((byte)72, noteOn.Message.Byte1);
        Assert.Equal(replacementInstrument.Id, noteOn.Source.EventInstrumentId);
        Assert.NotEqual(originalInstrument.Id, noteOn.Source.EventInstrumentId);
        CanonicalAudioUnitFragment after = Assert.Single(
            CanonicalAudioUnitProjection.Create(current).Fragments.ToArray());
        Assert.Equal(replacementInstrument.Id, after.EventInstrumentId);
        Assert.NotEqual(before.SemanticFingerprint, after.SemanticFingerprint);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public async Task NewlyCreatedEditedAndBoundInstrumentCompilesWithoutSaveOrReload()
    {
        MidoraProject project = new(480);
        using ProjectCompilationSession compilation = new(
            project,
            executionMode: ProjectCompilationExecutionMode.Background,
            backgroundDebounce: TimeSpan.Zero);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.CreateEventInstrument("Live Instrument"));
        EventInstrument instrument = Assert.Single(project.EventInstruments);
        SubVoice voice = Assert.Single(instrument.SubVoices);
        document.Execute(ProjectDomainEditCommands.CreateTemplateNote(
            instrument.Id,
            voice.Id,
            tick: 0,
            lengthTicks: 480,
            note: 67,
            velocity: 101));
        document.Execute(ProjectDomainEditCommands.CreateLogicalTrack(
            "Live Track",
            instrument.Id));
        LogicalTrack track = Assert.Single(project.Tracks);
        document.Execute(ProjectDomainEditCommands.CreateSegment(track.Id, 0, 480));
        Segment segment = Assert.Single(track.Segments);
        document.Execute(ProjectDomainEditCommands.CreateLogicalNote(
            segment.Id,
            startTick: 0,
            lengthTicks: 480,
            note: 67,
            velocity: 101));

        CanonicalCompiledResult current = await compilation.EnsureCurrentCompilationAsync();

        Assert.True(current.IsConsumable);
        CanonicalMidiEvent noteOn = Assert.Single(
            current.Events.ToArray(),
            value => value.Role == CanonicalEventRole.NoteOn);
        Assert.Equal(instrument.Id, noteOn.Source.EventInstrumentId);
        Assert.Equal(track.Id, noteOn.Source.TrackId);
        Assert.Equal(segment.Id, noteOn.Source.SegmentId);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public async Task NewlyCreatedInstrumentCanReceiveAnExistingSegmentTrackAfterSeparateCompilations()
    {
        MidoraProject project = new(480);
        EventInstrument original = CreateInstrument(project, "Original");
        LogicalTrack track = new(project) {
            Name = "Existing Track",
            LastBoundEventInstrumentName = original.Name
        };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, original.Id);
        Segment segment = new(project) { LengthTicks = 960 };
        segment.Notes.Add(new LogicalNote(project)
        {
            StartTick = 0,
            LengthTicks = 480,
            Note = 64,
            Velocity = 100
        });
        track.Segments.Add(segment);
        using ProjectCompilationSession compilation = new(
            project,
            executionMode: ProjectCompilationExecutionMode.Background,
            backgroundDebounce: TimeSpan.Zero);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.CreateEventInstrument("Created Later"));
        EventInstrument instrument = Assert.Single(
            project.EventInstruments,
            value => value.Name == "Created Later");
        _ = await compilation.EnsureCurrentCompilationAsync();

        SubVoice voice = Assert.Single(instrument.SubVoices);
        document.Execute(ProjectDomainEditCommands.CreateTemplateNote(
            instrument.Id,
            voice.Id,
            tick: 0,
            lengthTicks: 480,
            note: 60,
            velocity: 100));
        _ = await compilation.EnsureCurrentCompilationAsync();

        document.Execute(ProjectDomainEditCommands.BindLogicalTrack(track.Id, instrument.Id));
        CanonicalCompiledResult current = await compilation.EnsureCurrentCompilationAsync();

        Assert.True(current.IsConsumable);
        CanonicalMidiEvent noteOn = Assert.Single(
            current.Events.ToArray(),
            value => value.Role == CanonicalEventRole.NoteOn);
        Assert.Equal((byte)64, noteOn.Message.Byte1);
        Assert.Equal(instrument.Id, noteOn.Source.EventInstrumentId);
        Assert.Equal(track.Id, noteOn.Source.TrackId);
        Assert.Equal(segment.Id, noteOn.Source.SegmentId);
        Assert.NotEmpty(CanonicalAudioUnitProjection.Create(current).Fragments.ToArray());

        MidiRenderPlan plan = compilation.GetOrCreateRealtimeRenderPlan(
            current,
            sampleRate: 48_000,
            new HashSet<MidoraId> { track.Id });
        Assert.NotEmpty(plan.UnitFragments.ToArray());
        Assert.NotEmpty(plan.Segments.ToArray());
        Assert.Contains(
            plan.Ports.ToArray().SelectMany(value => value.Events.ToArray()),
            value => value.Message.MessageType == MidiMessageType.NoteOn
                && value.Message.Byte2 > 0);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void LogicalTrackReorderAndDeleteRestoreOrderIdentityAndRenderSelection()
    {
        MidoraProject project = CreateProject();
        LogicalTrack first = project.Tracks[0];
        EventInstrument parent = project.EventInstruments[0];
        LogicalTrack second = new(project) {
            Name = "Second",
            LastBoundEventInstrumentName = parent.Name
        };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, second, parent.Id);
        project.AudioRender.ExplicitLogicalTrackIds.Add(first.Id);
        long nextStableId = project.NextStableId;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.ReorderLogicalTrack(first.Id, 1));
        Assert.Equal(
            [second.Id, first.Id],
            project.LogicalTracksInArrangementOrder().Select(value => value.Id));
        document.Undo();
        Assert.Equal(
            [first.Id, second.Id],
            project.LogicalTracksInArrangementOrder().Select(value => value.Id));

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
    public void LogicalTrackReorderMovesExactlyOneTrackInSixTrackProject()
    {
        MidoraProject project = new(480);
        EventInstrument parent = CreateInstrument(project, "Parent");
        LogicalTrack[] tracks = Enumerable.Range(0, 6)
            .Select(index => new LogicalTrack(project)
            {
                Name = $"Track {index + 1}",
                LastBoundEventInstrumentName = parent.Name
            })
            .ToArray();
        foreach (LogicalTrack track in tracks)
        {
            ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, parent.Id);
        }
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.ReorderLogicalTrack(tracks[4].Id, 1));

        Assert.Equal(
            [tracks[0].Id, tracks[4].Id, tracks[1].Id, tracks[2].Id, tracks[3].Id, tracks[5].Id],
            project.LogicalTracksInArrangementOrder().Select(value => value.Id));
        document.Undo();
        Assert.Equal(
            tracks.Select(value => value.Id),
            project.LogicalTracksInArrangementOrder().Select(value => value.Id));
        document.Redo();
        Assert.Equal(
            [tracks[0].Id, tracks[4].Id, tracks[1].Id, tracks[2].Id, tracks[3].Id, tracks[5].Id],
            project.LogicalTracksInArrangementOrder().Select(value => value.Id));
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
    public void ReferencedEventInstrumentDeleteIsRejectedUntilItsUsageIsRemoved()
    {
        MidoraProject project = CreateProject();
        EventInstrument first = project.EventInstruments[0];
        EventInstrument second = CreateInstrument(project, "Strings");
        LogicalTrack track = project.Tracks[0];
        track.LastBoundEventInstrumentName = "Exact Old Snapshot";
        long nextStableId = project.NextStableId;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.DeleteEventInstrument(
                first.Id,
                referencedDeletionConfirmed: false)));

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.DeleteEventInstrument(
                first.Id,
                referencedDeletionConfirmed: true)));
        Assert.Contains(first, project.EventInstruments);
        Assert.Contains(track, project.Tracks);

        document.Execute(ProjectDomainEditCommands.DeleteLogicalTrack(
            track.Id,
            nonEmptyDeletionConfirmed: true));
        document.Execute(ProjectDomainEditCommands.DeleteEventInstrument(
            first.Id,
            referencedDeletionConfirmed: true));
        Assert.Equal([second], project.EventInstruments);
        Assert.DoesNotContain(track, project.Tracks);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        document.Undo();
        Assert.Same(first, project.EventInstruments[0]);
        Assert.Contains(track, project.Tracks);
        Assert.Equal(first.Id, project.ResolveEventInstrumentDefinitionId(track));
        Assert.Equal("Exact Old Snapshot", track.LastBoundEventInstrumentName);
        Assert.Equal(nextStableId, project.NextStableId);
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
        Assert.Equal(
            [second.Id, first.Id],
            project.EventInstruments.Select(value => value.Id));
        Assert.True(document.IsModified);
        Assert.Equal(originalFingerprint, compilation.LastAttempt.Fingerprint);

        document.Undo();
        Assert.Equal(
            [first.Id, second.Id],
            project.EventInstruments.Select(value => value.Id));
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
        EventInstrumentUsage usage = project.FindEventInstrumentUsage(track)!;
        usage.EventInstrumentId = damagedInstrumentId;
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
        long nextStableId = project.NextStableId;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.DeleteDamagedEventInstrument(damagedInstrumentId));
        document.Execute(ProjectDomainEditCommands.DeleteDamagedLogicalTrack(damagedTrackId));
        Assert.Empty(project.DamagedEventInstruments);
        Assert.Empty(project.DamagedLogicalTracks);
        Assert.DoesNotContain(track, project.Tracks);
        Assert.DoesNotContain(damagedTrackId, project.AudioRender.ExplicitLogicalTrackIds);

        document.Undo();
        document.Undo();
        Assert.Same(damagedInstrument, project.DamagedEventInstruments[0]);
        Assert.Same(damagedTrack, project.DamagedLogicalTracks[0]);
        Assert.Contains(track, project.Tracks);
        Assert.Equal(damagedInstrumentId, project.ResolveEventInstrumentDefinitionId(track));
        Assert.Equal("Old", track.LastBoundEventInstrumentName);
        Assert.Contains(damagedTrackId, project.AudioRender.ExplicitLogicalTrackIds);
        Assert.Equal(nextStableId, project.NextStableId);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void DamagedMidiRootDeletionCascadesAndUndoRestoresRetainedSubtree()
    {
        MidoraProject project = CreateProject();
        MidoraId damagedRootId = project.AllocateStableId();
        PureMidiTrack retainedTrack = new(project)
        {
            Name = "Retained MIDI Track",
            MidiChannelRootId = damagedRootId
        };
        retainedTrack.Segments.Add(new MidiSegment(project)
        {
            LengthTicks = 480
        });
        MidoraId damagedTrackId = project.AllocateStableId();
        DamagedProjectObject damagedTrack = new(
            damagedTrackId,
            "Damaged MIDI Track",
            "pure-midi-tracks/damaged.pb",
            "broken child",
            1,
            damagedRootId);
        DamagedProjectObject damagedRoot = new(
            damagedRootId,
            "Damaged Root",
            "midi-channel-roots/damaged.pb",
            "broken parent",
            project.ArrangementTracks.Count,
            ChildIds: Array.AsReadOnly(new[] { retainedTrack.Id, damagedTrackId }));
        project.PureMidiTracks.Add(retainedTrack);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, retainedTrack.Id));
        project.DamagedPureMidiTracks.Add(damagedTrack);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, damagedTrack.Id));
        project.DamagedMidiChannelRoots.Add(damagedRoot);
        long nextStableId = project.NextStableId;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.DeleteDamagedMidiChannelRoot(damagedRootId));

        Assert.DoesNotContain(retainedTrack, project.PureMidiTracks);
        Assert.Empty(project.DamagedPureMidiTracks);
        Assert.Empty(project.DamagedMidiChannelRoots);
        Assert.DoesNotContain(project.ArrangementTracks, value => value.TrackId == retainedTrack.Id);
        Assert.DoesNotContain(project.ArrangementTracks, value => value.TrackId == damagedTrackId);

        document.Undo();

        Assert.Same(retainedTrack, Assert.Single(project.PureMidiTracks, value => value.Id == retainedTrack.Id));
        Assert.Same(damagedTrack, Assert.Single(project.DamagedPureMidiTracks));
        Assert.Same(damagedRoot, Assert.Single(project.DamagedMidiChannelRoots));
        Assert.Contains(project.ArrangementTracks, value => value.TrackId == retainedTrack.Id);
        Assert.Contains(project.ArrangementTracks, value => value.TrackId == damagedTrackId);
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
            EventInstrumentUsageId = sourceTrack.EventInstrumentUsageId
        };
        Segment blocker = new(project) { ProjectStartTick = 2_000, LengthTicks = 480 };
        targetTrack.Segments.Add(blocker);
        project.Tracks.Add(targetTrack);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, targetTrack.Id));
        long nextStableId = project.NextStableId;
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
        long nextStableId = project.NextStableId;
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
        LogicalNote leftHidden = new(project)
        {
            StartTick = 960,
            LengthTicks = 120,
            Note = 67,
            Velocity = 70
        };
        left.Notes.Add(leftHidden);
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
        long nextStableId = project.NextStableId;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.JoinSegments(left.Id, right.Id));

        Segment joined = Assert.Single(track.Segments);
        Assert.Equal(left.Id, joined.Id);
        Assert.Equal(0, joined.ProjectStartTick);
        Assert.Equal(1_440, joined.LengthTicks);
        Assert.Contains(joined.Notes, value => value.Id == leftHidden.Id && value.StartTick == 960);
        Assert.DoesNotContain(joined.Notes, value => value.Id == right.Notes[0].Id);
        LogicalParameterLane joinedLane = Assert.Single(joined.ParameterLanes);
        CurvePoint point = Assert.Single(joinedLane.Points, value => value.Tick == 960);
        Assert.Equal(0.25, point.Value);
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
        LogicalTrack track = new(project) { Name = "Track"};
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
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
        Assert.Equal(expected.FailureStage, actual.FailureStage);
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
