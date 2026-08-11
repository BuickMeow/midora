using Midora.Audio;
using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectConductorAndSettingsEditCommandsTests
{
    [Fact]
    public void TempoUpdatePreservesIdentityAndRejectsInvalidOrConflictingValues()
    {
        MidoraProject project = new(480);
        TempoChange initial = project.Conductor.Tempos[0];
        TempoChange later = new(project, 960, 90m);
        project.Conductor.Tempos.Add(later);
        long nextStableId = project.NextStableId;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        ProjectEditExecution noOp = document.Execute(
            ProjectDomainEditCommands.UpdateTempo(initial.Id, 0, 120m));
        Assert.False(noOp.Changed);

        document.Execute(ProjectDomainEditCommands.UpdateTempo(initial.Id, 0, 128.5m));
        TempoChange replacement = project.Conductor.Tempos[0];
        Assert.NotSame(initial, replacement);
        Assert.Equal(initial.Id, replacement.Id);
        Assert.Equal(128.5m, replacement.BeatsPerMinute);
        Assert.Equal(nextStableId, project.NextStableId);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Same(initial, project.Conductor.Tempos[0]);
        Assert.Equal(nextStableId, project.NextStableId);
        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTempo(initial.Id, 960, 120m)));
        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTempo(later.Id, 0, 90m)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTempo(initial.Id, 0, 0m)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTempo(initial.Id, 0, 0.000001m)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTempo(initial.Id, 0, 120_000_001m)));
        Assert.False(document.IsModified);
    }

    [Fact]
    public void TempoAndTimeSignatureDeleteProtectTickZeroAndRestoreLaterEvents()
    {
        MidoraProject project = new(480);
        TempoChange initialTempo = project.Conductor.Tempos[0];
        TempoChange laterTempo = new(project, 960, 100m);
        project.Conductor.Tempos.Add(laterTempo);
        TimeSignatureChange initialSignature = project.Conductor.TimeSignatures[0];
        TimeSignatureChange laterSignature = new(project, 1_920, 3, 4);
        project.Conductor.TimeSignatures.Add(laterSignature);
        long nextStableId = project.NextStableId;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.DeleteTempo(initialTempo.Id)));
        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.DeleteTimeSignature(initialSignature.Id)));

        document.Execute(ProjectDomainEditCommands.DeleteTempo(laterTempo.Id));
        document.Execute(ProjectDomainEditCommands.DeleteTimeSignature(laterSignature.Id));
        Assert.Equal([initialTempo], project.Conductor.Tempos);
        Assert.Equal([initialSignature], project.Conductor.TimeSignatures);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        document.Undo();
        Assert.Same(laterTempo, project.Conductor.Tempos[1]);
        Assert.Same(laterSignature, project.Conductor.TimeSignatures[1]);
        Assert.Equal(nextStableId, project.NextStableId);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void TimeAndKeySignatureUpdatesValidateRangesUniquenessAndUndoExactly()
    {
        MidoraProject project = new(480);
        TimeSignatureChange initial = project.Conductor.TimeSignatures[0];
        KeySignatureChange key = new(project, 240, -3, isMinor: false);
        KeySignatureChange laterKey = new(project, 480, 2, isMinor: true);
        project.Conductor.KeySignatures.AddRange([key, laterKey]);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateTimeSignature(initial.Id, 0, 7, 8));
        document.Execute(ProjectDomainEditCommands.UpdateKeySignature(key.Id, 360, 4, isMinor: true));
        Assert.Equal((7, 8), (
            project.Conductor.TimeSignatures[0].Numerator,
            project.Conductor.TimeSignatures[0].Denominator));
        Assert.Equal((360, 4, true), (
            project.Conductor.KeySignatures[0].Tick,
            project.Conductor.KeySignatures[0].SharpsFlats,
            project.Conductor.KeySignatures[0].IsMinor));
        AssertCurrentCompilationMatchesFull(compilation);

        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTimeSignature(initial.Id, 0, 100, 4)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTimeSignature(initial.Id, 0, 4, 3)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateKeySignature(key.Id, 360, 8, false)));
        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateKeySignature(key.Id, laterKey.Tick, 0, false)));

        document.Undo();
        document.Undo();
        Assert.Same(initial, project.Conductor.TimeSignatures[0]);
        Assert.Same(key, project.Conductor.KeySignatures[0]);
        Assert.False(document.IsModified);
    }

    [Fact]
    public void KeySignatureDeleteRestoresOriginalIndexAndIdentity()
    {
        MidoraProject project = new(480);
        KeySignatureChange first = new(project, 0, 0, false);
        KeySignatureChange second = new(project, 480, -2, true);
        project.Conductor.KeySignatures.AddRange([first, second]);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.DeleteKeySignature(first.Id));
        Assert.Equal([second], project.Conductor.KeySignatures);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Equal([first, second], project.Conductor.KeySignatures);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void MarkerCommandsAllowDuplicatesButValidateTextAndRestoreExactObjects()
    {
        MidoraProject project = new(480);
        ProjectMarker first = new(project, 240, "Intro");
        ProjectMarker second = new(project, 240, "Intro");
        project.Conductor.Markers.AddRange([first, second]);
        long nextStableId = project.NextStableId;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateProjectMarker(first.Id, 240, "   "));
        ProjectMarker replacement = project.Conductor.Markers[0];
        Assert.Equal(string.Empty, replacement.Name);
        Assert.Equal(first.Id, replacement.Id);
        Assert.Equal(240, replacement.Tick);
        Assert.Equal(nextStableId, project.NextStableId);
        AssertCurrentCompilationMatchesFull(compilation);

        Assert.Throws<ArgumentException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateProjectMarker(first.Id, 240, "Bad\nName")));
        document.Execute(ProjectDomainEditCommands.DeleteProjectMarker(second.Id));
        Assert.Equal([replacement], project.Conductor.Markers);
        document.Undo();
        Assert.Same(second, project.Conductor.Markers[1]);
        document.Undo();
        Assert.Same(first, project.Conductor.Markers[0]);
        Assert.Equal(nextStableId, project.NextStableId);
        Assert.False(document.IsModified);
    }

    [Fact]
    public void ExistingEndMarkerMoveAndDeleteAreReversibleWithoutAllocatingIds()
    {
        MidoraProject project = new(480);
        project.SetEndMarker(960);
        ProjectEndMarker marker = project.Conductor.EndMarker!;
        long nextStableId = project.NextStableId;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateProjectEndMarker(1_920));
        Assert.Same(marker, project.Conductor.EndMarker);
        Assert.Equal(1_920, marker.Tick);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Execute(ProjectDomainEditCommands.DeleteProjectEndMarker());
        Assert.Null(project.Conductor.EndMarker);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Same(marker, project.Conductor.EndMarker);
        Assert.Equal(1_920, marker.Tick);
        document.Undo();
        Assert.Same(marker, project.Conductor.EndMarker);
        Assert.Equal(960, marker.Tick);
        Assert.Equal(nextStableId, project.NextStableId);
        Assert.False(document.IsModified);
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateProjectEndMarker(-1)));
    }

    [Fact]
    public void PlaybackSettingsAreAtomicValidatedAndDoNotChangeCanonicalResult()
    {
        MidoraProject project = new(480);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        CanonicalCompiledResult original = compilation.LastAttempt;

        document.Execute(ProjectDomainEditCommands.UpdatePlaybackSettings(
            -6.5,
            limiterEnabled: false,
            StopCursorBehavior.StayAtStoppedTick));
        Assert.Equal(-6.5, project.Playback.MasterVolumeDecibels);
        Assert.False(project.Playback.LimiterEnabled);
        Assert.Equal(StopCursorBehavior.StayAtStoppedTick, project.Playback.StopCursorBehavior);
        Assert.Equal(original.Fingerprint, compilation.LastAttempt.Fingerprint);
        Assert.True(document.IsModified);

        document.Undo();
        Assert.Equal(-0.1, project.Playback.MasterVolumeDecibels);
        Assert.True(project.Playback.LimiterEnabled);
        Assert.Equal(StopCursorBehavior.ReturnToPlaybackStart, project.Playback.StopCursorBehavior);
        Assert.False(document.IsModified);
        Assert.Equal(original.Fingerprint, compilation.LastAttempt.Fingerprint);
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdatePlaybackSettings(
                0.01,
                true,
                StopCursorBehavior.ReturnToPlaybackStart)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdatePlaybackSettings(
                double.NaN,
                true,
                StopCursorBehavior.ReturnToPlaybackStart)));
    }

    [Fact]
    public void MidiExportSettingsAreAtomicValidatedAndDoNotChangeCanonicalResult()
    {
        MidoraProject project = new(480);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        long originalFingerprint = compilation.LastAttempt.Fingerprint;

        document.Execute(ProjectDomainEditCommands.UpdateMidiExportSettings(
            ProjectMidiExportMode.PerPort,
            ProjectRangeMode.ManualRange,
            240,
            3_840,
            ProjectMidiExportTrackSelectionMode.ExplicitAtTaskStart,
            ProjectMidiExportRoutingStrategy.Preserve,
            includeReadme: false,
            treatWarningsAsErrors: true));

        Assert.Equal(ProjectMidiExportMode.PerPort, project.Export.Mode);
        Assert.Equal(ProjectRangeMode.ManualRange, project.Export.RangeMode);
        Assert.Equal(240, project.Export.ManualStartTick);
        Assert.Equal(3_840, project.Export.ManualEndTick);
        Assert.Equal(
            ProjectMidiExportTrackSelectionMode.ExplicitAtTaskStart,
            project.Export.TrackSelectionMode);
        Assert.Equal(ProjectMidiExportRoutingStrategy.Preserve, project.Export.Routing);
        Assert.False(project.Export.IncludeReadme);
        Assert.True(project.Export.TreatWarningsAsErrors);
        Assert.Equal(originalFingerprint, compilation.LastAttempt.Fingerprint);
        Assert.True(document.IsModified);

        document.Undo();

        Assert.Equal(ProjectMidiExportMode.WholeProject, project.Export.Mode);
        Assert.Equal(ProjectRangeMode.ProjectDefaultRange, project.Export.RangeMode);
        Assert.Null(project.Export.ManualStartTick);
        Assert.Null(project.Export.ManualEndTick);
        Assert.Equal(
            ProjectMidiExportTrackSelectionMode.AllValidLogicalTracks,
            project.Export.TrackSelectionMode);
        Assert.Equal(ProjectMidiExportRoutingStrategy.Compact, project.Export.Routing);
        Assert.True(project.Export.IncludeReadme);
        Assert.False(project.Export.TreatWarningsAsErrors);
        Assert.Equal(originalFingerprint, compilation.LastAttempt.Fingerprint);
        Assert.False(document.IsModified);
    }

    [Fact]
    public void TimeSignatureCommandsRejectTpqIncompatibleDenominatorAtomically()
    {
        MidoraProject project = new(1);
        TimeSignatureChange initial = project.Conductor.TimeSignatures[0];
        long nextStableId = project.NextStableId;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        Assert.Throws<ArgumentException>(() => document.Execute(
            ProjectDomainEditCommands.CreateTimeSignature(4, 3, 8)));
        Assert.Throws<ArgumentException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTimeSignature(initial.Id, 0, 3, 8)));

        Assert.Equal(nextStableId, project.NextStableId);
        Assert.Equal([initial], project.Conductor.TimeSignatures);
        Assert.False(document.IsModified);
        Assert.False(document.CanUndo);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void MidiExportSettingsRejectInconsistentRangeAndUnknownEnums()
    {
        using ProjectCompilationSession compilation = new(new MidoraProject(480));
        ProjectDocumentSession document = PersistedDocument(compilation);

        Assert.Throws<ArgumentException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateMidiExportSettings(
                ProjectMidiExportMode.WholeProject,
                ProjectRangeMode.ProjectDefaultRange,
                0,
                480,
                ProjectMidiExportTrackSelectionMode.AllValidLogicalTracks,
                ProjectMidiExportRoutingStrategy.Compact,
                true,
                false)));
        Assert.Throws<ArgumentException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateMidiExportSettings(
                ProjectMidiExportMode.WholeProject,
                ProjectRangeMode.ManualRange,
                480,
                480,
                ProjectMidiExportTrackSelectionMode.AllValidLogicalTracks,
                ProjectMidiExportRoutingStrategy.Compact,
                true,
                false)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateMidiExportSettings(
                (ProjectMidiExportMode)int.MaxValue,
                ProjectRangeMode.ProjectDefaultRange,
                null,
                null,
                ProjectMidiExportTrackSelectionMode.AllValidLogicalTracks,
                ProjectMidiExportRoutingStrategy.Compact,
                true,
                false)));
        Assert.Empty(document.History);
        Assert.False(document.IsModified);
    }

    [Fact]
    public void AudioRenderSettingsFreezeExplicitIdsAndUndoExactSnapshot()
    {
        MidoraProject project = CreateProjectWithTracks();
        LogicalTrack first = project.Tracks[0];
        LogicalTrack second = project.Tracks[1];
        List<MidoraId> requested = [second.Id];
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        long originalFingerprint = compilation.LastAttempt.Fingerprint;
        IProjectEditCommand command = ProjectDomainEditCommands.UpdateAudioRenderSettings(
            AudioRenderMode.PerLogicalTrack,
            ProjectRangeMode.ManualRange,
            240,
            3_840,
            ProjectTrackSelectionMode.ExplicitLogicalTrackIds,
            requested,
            44_100,
            1_024);
        requested[0] = first.Id;

        document.Execute(command);

        Assert.Equal(AudioRenderMode.PerLogicalTrack, project.AudioRender.Mode);
        Assert.Equal(ProjectRangeMode.ManualRange, project.AudioRender.RangeMode);
        Assert.Equal(240, project.AudioRender.ManualStartTick);
        Assert.Equal(3_840, project.AudioRender.ManualEndTick);
        Assert.Equal(ProjectTrackSelectionMode.ExplicitLogicalTrackIds, project.AudioRender.TrackSelectionMode);
        Assert.Equal([second.Id], project.AudioRender.ExplicitLogicalTrackIds);
        Assert.Equal(44_100, project.AudioRender.SampleRate);
        Assert.Equal(1_024, project.AudioRender.MaximumSampleVoicesPerUnitStream);
        Assert.Equal(originalFingerprint, compilation.LastAttempt.Fingerprint);

        document.Undo();
        Assert.Equal(AudioRenderMode.WholeMix, project.AudioRender.Mode);
        Assert.Equal(ProjectRangeMode.ProjectDefaultRange, project.AudioRender.RangeMode);
        Assert.Null(project.AudioRender.ManualStartTick);
        Assert.Null(project.AudioRender.ManualEndTick);
        Assert.Equal(ProjectTrackSelectionMode.AllValidLogicalTracks, project.AudioRender.TrackSelectionMode);
        Assert.Empty(project.AudioRender.ExplicitLogicalTrackIds);
        Assert.Equal(AudioRenderProjectSettings.DefaultSampleRate, project.AudioRender.SampleRate);
        Assert.Equal(
            AudioRenderProjectSettings.DefaultSampleVoicesPerUnitStream,
            project.AudioRender.MaximumSampleVoicesPerUnitStream);
        Assert.False(document.IsModified);
        Assert.Equal(originalFingerprint, compilation.LastAttempt.Fingerprint);
    }

    [Fact]
    public void AudioRenderSettingsRejectInconsistentRangesSelectionsAndValueBounds()
    {
        MidoraProject project = CreateProjectWithTracks();
        MidoraId trackId = project.Tracks[0].Id;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        Assert.Throws<ArgumentException>(() => document.Execute(
            AudioSettings(ProjectRangeMode.ProjectDefaultRange, 0, 480, [], 48_000, 750)));
        Assert.Throws<ArgumentException>(() => document.Execute(
            AudioSettings(ProjectRangeMode.ManualRange, 480, 480, [], 48_000, 750)));
        Assert.Throws<ArgumentException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateAudioRenderSettings(
                AudioRenderMode.WholeMix,
                ProjectRangeMode.ProjectDefaultRange,
                null,
                null,
                ProjectTrackSelectionMode.AllValidLogicalTracks,
                [trackId],
                48_000,
                750)));
        Assert.Throws<ArgumentException>(() => document.Execute(
            AudioSettings(
                ProjectRangeMode.ProjectDefaultRange,
                null,
                null,
                [MidoraId.FromSequence(project.NextStableId)],
                48_000,
                750)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            AudioSettings(ProjectRangeMode.ProjectDefaultRange, null, null, [], 7_999, 750)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            AudioSettings(ProjectRangeMode.ProjectDefaultRange, null, null, [], 48_000, 0)));
        Assert.Throws<ArgumentException>(() => ProjectDomainEditCommands.UpdateAudioRenderSettings(
            AudioRenderMode.WholeMix,
            ProjectRangeMode.ProjectDefaultRange,
            null,
            null,
            ProjectTrackSelectionMode.ExplicitLogicalTrackIds,
            [trackId, trackId],
            48_000,
            750));
        Assert.Empty(document.History);
        Assert.False(document.IsModified);
    }

    [Fact]
    public void AudioRenderVoiceChangeAndHistoryPreserveCompletedSessionPcmEntries()
    {
        string cacheRoot = Path.Combine(
            Path.GetTempPath(),
            $"midora-offline-voice-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cacheRoot);
        try
        {
            MidoraProject project = CreateProjectWithTracks();
            using ProjectCompilationSession compilation = new(project);
            _ = compilation.ConfigureAudioCache(cacheRoot, 4096);
            ProjectDocumentSession document = PersistedDocument(compilation);
            string key = AudioCacheSessionStore.ComputeKey([1, 2, 3]);
            Assert.True(compilation.PublishReusableAudio(key, [4, 5, 6]).Published);
            string beforeEdit = compilation.AudioCacheSnapshot!.Value.SessionPath;
            long canonicalFingerprint = compilation.LastAttempt.Fingerprint;

            document.Execute(ProjectDomainEditCommands.UpdateAudioRenderSettings(
                AudioRenderMode.WholeMix,
                ProjectRangeMode.ProjectDefaultRange,
                null,
                null,
                ProjectTrackSelectionMode.AllValidLogicalTracks,
                [],
                AudioRenderProjectSettings.DefaultSampleRate,
                AudioRenderProjectSettings.DefaultSampleVoicesPerUnitStream + 1));

            string afterEdit = compilation.AudioCacheSnapshot!.Value.SessionPath;
            Assert.Equal(beforeEdit, afterEdit);
            Assert.True(Directory.Exists(beforeEdit));
            Assert.True(compilation.TryReadReusableAudio(key, out byte[] afterEditPayload));
            Assert.Equal([4, 5, 6], afterEditPayload);
            Assert.Equal(canonicalFingerprint, compilation.LastAttempt.Fingerprint);

            document.Undo();

            string afterUndo = compilation.AudioCacheSnapshot!.Value.SessionPath;
            Assert.Equal(afterEdit, afterUndo);
            Assert.True(compilation.TryReadReusableAudio(key, out byte[] afterUndoPayload));
            Assert.Equal([4, 5, 6], afterUndoPayload);
            Assert.Equal(canonicalFingerprint, compilation.LastAttempt.Fingerprint);

            document.Redo();

            string afterRedo = compilation.AudioCacheSnapshot!.Value.SessionPath;
            Assert.Equal(afterUndo, afterRedo);
            Assert.True(compilation.TryReadReusableAudio(key, out byte[] afterRedoPayload));
            Assert.Equal([4, 5, 6], afterRedoPayload);
            Assert.Equal(canonicalFingerprint, compilation.LastAttempt.Fingerprint);
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, recursive: true);
            }
        }
    }

    private static IProjectEditCommand AudioSettings(
        ProjectRangeMode rangeMode,
        long? startTick,
        long? endTick,
        IEnumerable<MidoraId> trackIds,
        int sampleRate,
        int voices) =>
        ProjectDomainEditCommands.UpdateAudioRenderSettings(
            AudioRenderMode.WholeMix,
            rangeMode,
            startTick,
            endTick,
            ProjectTrackSelectionMode.ExplicitLogicalTrackIds,
            trackIds,
            sampleRate,
            voices);

    private static ProjectDocumentSession PersistedDocument(ProjectCompilationSession compilation) =>
        new(compilation, ProjectDocumentOrigin.Persisted);

    private static MidoraProject CreateProjectWithTracks()
    {
        MidoraProject project = new(480);
        project.Tracks.Add(new LogicalTrack(project) { Name = "First" });
        project.Tracks.Add(new LogicalTrack(project) { Name = "Second" });
        return project;
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
