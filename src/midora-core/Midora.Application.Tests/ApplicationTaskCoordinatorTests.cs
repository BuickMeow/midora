using Midora.Audio;
using Midora.Compiler;
using Midora.Domain;
using Midora.Persistence;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ApplicationTaskCoordinatorTests
{
    [Fact]
    public async Task SoundFontMonitorInvalidationStopsActivePlaybackAndReleasesEditLock()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"midora-coordinator-sf2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string projectPath = Path.Combine(directory, "Song.midora");
            string soundFontPath = Path.Combine(directory, "Piano.sf2");
            await File.WriteAllBytesAsync(soundFontPath, [1, 2, 3, 4]);
            ExternalSoundFontBindingV1 binding = await SoundFontBindingV1.BindExternalAsync(
                projectPath,
                soundFontPath);
            MidoraProject project = CreateProject();
            project.SoundFont.SetReference(binding.Reference);
            using ProjectCompilationSession session = new(project);
            using ProjectSoundFontRuntimeSession soundFontRuntime = new(
                session,
                new AcceptingSoundFontValidator());
            Assert.True((await soundFontRuntime.RefreshAsync(projectPath)).IsAvailable);
            FakeBackend backend = new();
            using PlaybackController playback = new(session, backend);
            using ApplicationTaskCoordinator coordinator = new(
                session,
                playback,
                soundFontRuntime);
            TaskCompletionSource invalidated = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            soundFontRuntime.AvailabilityChanged += (_, _) =>
            {
                if (soundFontRuntime.Current.Availability
                    == ProjectSoundFontAvailability.VerificationRequired)
                {
                    invalidated.TrySetResult();
                }
            };
            coordinator.StartMainPlayback();

            await File.WriteAllBytesAsync(soundFontPath, [5, 6, 7, 8]);
            await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, backend.StopCount);
            Assert.Equal(PlaybackState.Stopped, playback.State);
            Assert.Equal(ApplicationTaskKind.None, coordinator.ActiveTaskKind);
            Assert.Equal(ApplicationTaskPhase.Idle, coordinator.Phase);
            Assert.False(session.EditsLocked);
            Assert.Null(session.EffectiveSoundFontPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PlaybackOwnsSingleTaskAndExplicitCompileDoesNotAutoPreemptIt()
    {
        using TestContext fixture = TestContext.Create();
        fixture.Coordinator.StartMainPlayback();

        Assert.Equal(ApplicationTaskKind.MainPlayback, fixture.Coordinator.ActiveTaskKind);
        Assert.Equal(ApplicationTaskPhase.Running, fixture.Coordinator.Phase);
        Assert.Equal(ApplicationLockLevel.ProjectEdit, fixture.Coordinator.LockLevel);
        Assert.True(fixture.Session.EditsLocked);
        Assert.Throws<InvalidOperationException>(() => fixture.Coordinator.StartMainPlayback());

        ApplicationTaskExecution<int> compile = await fixture.Coordinator.ExecuteAsync(
            ApplicationTaskKind.ExplicitCompile,
            (_, _) => Task.FromResult(1));

        Assert.Equal(ApplicationTaskOutcome.RejectedBusy, compile.Outcome);
        Assert.Equal(0, fixture.Backend.StopCount);
        fixture.Coordinator.StopPlayback();
        Assert.False(fixture.Coordinator.IsBusy);
        Assert.False(fixture.Session.EditsLocked);
    }

    [Fact]
    public void CoordinatorKeepsHeldPreviewExclusiveThroughGateEndAndCancellation()
    {
        using TestContext fixture = TestContext.Create();
        EventInstrument instrument = fixture.Session.Project.EventInstruments[0];

        fixture.Coordinator.StartHeldEventInstrumentPreview(
            new EventInstrumentPreviewRequest(instrument.Id, Tempo: 120m));

        Assert.Equal(ApplicationTaskKind.EventInstrumentPreview, fixture.Coordinator.ActiveTaskKind);
        Assert.True(fixture.Playback.IsHeldPreviewGateOpen);
        HeldPreviewGateEndReport report = fixture.Coordinator.EndHeldPreviewGate(240);
        Assert.Equal(240, report.FinalGateLengthTicks);
        Assert.False(fixture.Playback.IsHeldPreviewGateOpen);
        Assert.Equal(ApplicationTaskKind.EventInstrumentPreview, fixture.Coordinator.ActiveTaskKind);
        fixture.Coordinator.StopPlayback();

        fixture.Coordinator.StartHeldEventInstrumentPreview(
            new EventInstrumentPreviewRequest(instrument.Id, Tempo: 120m));
        fixture.Coordinator.CancelHeldPreview();

        Assert.Equal(ApplicationTaskKind.None, fixture.Coordinator.ActiveTaskKind);
        Assert.Equal(ApplicationTaskPhase.Idle, fixture.Coordinator.Phase);
        Assert.False(fixture.Session.EditsLocked);
    }

    [Fact]
    public void DraftNoteCommitSucceedsAfterGateEndAndAlsoWhenPreviewCouldNotStart()
    {
        using TestContext fixture = TestContext.Create();
        LogicalTrack track = fixture.Session.Project.Tracks[0];
        Segment segment = track.Segments[0];
        SegmentNotePreviewRequest request = new(
            track.Id,
            segment.Id,
            StartTick: 240,
            Pitch: 67,
            Velocity: 105);
        fixture.Coordinator.StartHeldSegmentNotePreview(request);
        Assert.True(fixture.Session.EditsLocked);
        ProjectChangeSet changes = new();
        changes.TrackIds.Add(track.Id);

        SegmentNotePlacementPreviewCompletion completed =
            fixture.Coordinator.CompleteSegmentNotePlacement(
                120,
                () => fixture.Session.ApplyEdit(
                    project => project.Tracks[0].Segments[0].Notes.Add(new LogicalNote(project)
                    {
                        StartTick = 240,
                        LengthTicks = 120,
                        Note = 67,
                        Velocity = 105
                    }),
                    changes));

        Assert.Null(completed.PreviewError);
        Assert.NotNull(completed.GateEndReport);
        Assert.False(fixture.Session.EditsLocked);
        Assert.Contains(fixture.Session.Project.Tracks[0].Segments[0].Notes, value =>
            value.StartTick == 240 && value.Note == 67);
        fixture.Coordinator.StopPlayback();

        SegmentNotePreviewRequest invalid = request with { SegmentId = new MidoraId(long.MaxValue) };
        Exception? previewFailure = fixture.Coordinator.TryStartHeldSegmentNotePreview(invalid);
        Assert.NotNull(previewFailure);
        int noteCount = fixture.Session.Project.Tracks[0].Segments[0].Notes.Count;
        SegmentNotePlacementPreviewCompletion withoutPreview =
            fixture.Coordinator.CompleteSegmentNotePlacement(
                60,
                () => fixture.Session.ApplyEdit(
                    project => project.Tracks[0].Segments[0].Notes.Add(new LogicalNote(project)
                    {
                        StartTick = 480,
                        LengthTicks = 60,
                        Note = 69,
                        Velocity = 100
                    }),
                    changes));

        Assert.Null(withoutPreview.GateEndReport);
        Assert.Null(withoutPreview.PreviewError);
        Assert.Equal(noteCount + 1, fixture.Session.Project.Tracks[0].Segments[0].Notes.Count);
    }

    [Fact]
    public async Task SaveAutoStopsPlaybackAndNeverRestartsIt()
    {
        using TestContext fixture = TestContext.Create();
        fixture.Coordinator.StartMainPlayback();
        bool observedLockedAndStopped = false;

        ApplicationTaskExecution<int> save = await fixture.Coordinator.ExecuteAsync(
            ApplicationTaskKind.SaveProject,
            (_, _) =>
            {
                observedLockedAndStopped = fixture.Playback.State == PlaybackState.Stopped
                    && fixture.Session.EditsLocked;
                return Task.FromResult(7);
            });

        Assert.Equal(ApplicationTaskOutcome.Completed, save.Outcome);
        Assert.Equal(7, save.Value);
        Assert.True(observedLockedAndStopped);
        Assert.Equal(1, fixture.Backend.StopCount);
        Assert.Equal(PlaybackState.Stopped, fixture.Playback.State);
        Assert.False(fixture.Coordinator.IsBusy);
        Assert.False(fixture.Session.EditsLocked);
    }

    [Fact]
    public async Task RiskyCommandRequiresOneTimeContinuationAfterPlaybackCleanupFailure()
    {
        using TestContext fixture = TestContext.Create();
        fixture.Coordinator.StartMainPlayback();
        fixture.Backend.ThrowOnStop = true;
        int executionCount = 0;

        ApplicationTaskExecution<int> first = await fixture.Coordinator.ExecuteAsync(
            ApplicationTaskKind.MidiExport,
            (_, _) =>
            {
                executionCount++;
                return Task.FromResult(1);
            });

        Assert.Equal(ApplicationTaskOutcome.RequiresPlaybackCleanupConfirmation, first.Outcome);
        Assert.NotNull(first.PlaybackCleanupError);
        PlaybackCleanupContinuation continuation = Assert.IsType<PlaybackCleanupContinuation>(
            first.PlaybackCleanupContinuation);
        Assert.Equal(0, executionCount);
        Assert.Equal(PlaybackState.Error, fixture.Playback.State);

        ApplicationTaskExecution<int> continued = await fixture.Coordinator.ExecuteAsync(
            ApplicationTaskKind.MidiExport,
            (_, _) =>
            {
                executionCount++;
                return Task.FromResult(2);
            },
            continuation);

        Assert.Equal(ApplicationTaskOutcome.Completed, continued.Outcome);
        Assert.Equal(2, continued.Value);
        Assert.Same(first.PlaybackCleanupError, continued.PlaybackCleanupError);
        Assert.Equal(1, executionCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.ExecuteAsync(
            ApplicationTaskKind.MidiExport,
            (_, _) => Task.FromResult(3),
            continuation));
    }

    [Fact]
    public async Task SaveContinuesAfterPlaybackCleanupFailureWithoutRiskConfirmation()
    {
        using TestContext fixture = TestContext.Create();
        fixture.Coordinator.StartMainPlayback();
        fixture.Backend.ThrowOnStop = true;

        ApplicationTaskExecution<int> save = await fixture.Coordinator.ExecuteAsync(
            ApplicationTaskKind.SaveCopy,
            (_, _) => Task.FromResult(9));

        Assert.Equal(ApplicationTaskOutcome.Completed, save.Outcome);
        Assert.Equal(9, save.Value);
        Assert.NotNull(save.PlaybackCleanupError);
        Assert.Null(save.PlaybackCleanupContinuation);
    }

    [Fact]
    public async Task RunningTaskHoldsNestedProjectLockAndRejectsConcurrentCommandsWithoutQueueing()
    {
        using TestContext fixture = TestContext.Create();
        using IDisposable outerLock = fixture.Session.AcquireProjectEditLock();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ApplicationTaskExecution<int>> running = fixture.Coordinator.ExecuteAsync(
            ApplicationTaskKind.AudioRender,
            async (context, token) =>
            {
                context.ReportPhase(ApplicationTaskPhase.Running);
                entered.SetResult();
                await release.Task.WaitAsync(token);
                return 4;
            });
        await entered.Task;

        Assert.Equal(ApplicationTaskKind.AudioRender, fixture.Coordinator.ActiveTaskKind);
        Assert.Equal(ApplicationLockLevel.FullApplication, fixture.Coordinator.LockLevel);
        Assert.True(fixture.Session.EditsLocked);
        ApplicationTaskExecution<int> rejected = await fixture.Coordinator.ExecuteAsync(
            ApplicationTaskKind.SaveProject,
            (_, _) => Task.FromResult(5));
        Assert.Equal(ApplicationTaskOutcome.RejectedBusy, rejected.Outcome);

        release.SetResult();
        Assert.Equal(ApplicationTaskOutcome.Completed, (await running).Outcome);
        Assert.True(fixture.Session.EditsLocked);
        outerLock.Dispose();
        Assert.False(fixture.Session.EditsLocked);
    }

    [Fact]
    public async Task CancellableTaskTransitionsAndReleasesProjectLock()
    {
        using TestContext fixture = TestContext.Create();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseCancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ApplicationTaskExecution<int>> running = fixture.Coordinator.ExecuteAsync(
            ApplicationTaskKind.AudioRender,
            async (_, token) =>
            {
                using CancellationTokenRegistration registration = token.Register(
                    () => cancellationObserved.TrySetResult());
                entered.SetResult();
                await releaseCancellation.Task;
                token.ThrowIfCancellationRequested();
                return 1;
            });
        await entered.Task;

        Assert.True(fixture.Coordinator.RequestCancellation());
        await cancellationObserved.Task;
        Assert.Equal(ApplicationTaskPhase.Cancelling, fixture.Coordinator.Phase);
        releaseCancellation.SetResult();
        ApplicationTaskExecution<int> result = await running;

        Assert.Equal(ApplicationTaskOutcome.Cancelled, result.Outcome);
        Assert.False(fixture.Coordinator.RequestCancellation());
        Assert.False(fixture.Session.EditsLocked);
        Assert.False(fixture.Coordinator.IsBusy);
    }

    [Fact]
    public async Task SaveIsNonCancellableOnceAdmitted()
    {
        using TestContext fixture = TestContext.Create();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ApplicationTaskExecution<int>> running = fixture.Coordinator.ExecuteAsync(
            ApplicationTaskKind.SaveProject,
            async (_, _) =>
            {
                entered.SetResult();
                await release.Task;
                return 6;
            });
        await entered.Task;

        Assert.False(fixture.Coordinator.RequestCancellation());
        release.SetResult();
        Assert.Equal(ApplicationTaskOutcome.Completed, (await running).Outcome);
    }

    [Fact]
    public async Task ProjectSwitchStopsPlaybackThenResolvesDraftsAndUnsavedChangesInOrder()
    {
        using TestContext fixture = TestContext.Create();
        fixture.Coordinator.StartMainPlayback();
        RecordingSwitchActions actions = new(fixture.Session)
        {
            HasFunctionDrafts = true,
            DraftResolution = FunctionDraftResolution.Apply,
            HasUnsavedProjectChanges = true,
            UnsavedResolution = UnsavedProjectResolution.SaveProject
        };

        ApplicationTaskExecution<ProjectSwitchGuardResult<string>> execution =
            await fixture.Coordinator.ExecuteProjectSwitchAsync(
                ApplicationTaskKind.OpenProject,
                actions);

        Assert.Equal(ApplicationTaskOutcome.Completed, execution.Outcome);
        ProjectSwitchGuardResult<string> result = Assert.IsType<ProjectSwitchGuardResult<string>>(
            execution.Value);
        Assert.Equal(ProjectSwitchGuardStatus.Completed, result.Status);
        Assert.Equal("switched", result.Value);
        Assert.Equal(1, fixture.Backend.StopCount);
        Assert.Equal(
            ["resolve-drafts", "apply-drafts", "resolve-unsaved", "save", "switch"],
            actions.Events);
        Assert.False(actions.WasEditLockedWhileApplyingDrafts);
        Assert.True(actions.WasEditLockedWhileResolvingUnsaved);
        Assert.True(actions.WasEditLockedWhileSaving);
        Assert.True(actions.WasEditLockedWhileSwitching);
        Assert.False(fixture.Session.EditsLocked);
    }

    [Fact]
    public async Task ProjectSwitchCancellationAtDraftStageSkipsUnsavedPromptAndSwitch()
    {
        using TestContext fixture = TestContext.Create();
        RecordingSwitchActions actions = new(fixture.Session)
        {
            HasFunctionDrafts = true,
            DraftResolution = FunctionDraftResolution.Cancel,
            HasUnsavedProjectChanges = true
        };

        ApplicationTaskExecution<ProjectSwitchGuardResult<string>> execution =
            await fixture.Coordinator.ExecuteProjectSwitchAsync(
                ApplicationTaskKind.CloseProject,
                actions);

        Assert.Equal(ApplicationTaskOutcome.Completed, execution.Outcome);
        Assert.Equal(ProjectSwitchGuardStatus.Cancelled, execution.Value?.Status);
        Assert.Equal(["resolve-drafts"], actions.Events);
        Assert.False(fixture.Session.EditsLocked);
    }

    [Fact]
    public async Task ProjectSwitchReportsUnavailableSaveWithoutPerformingSwitch()
    {
        using TestContext fixture = TestContext.Create();
        RecordingSwitchActions actions = new(fixture.Session)
        {
            HasUnsavedProjectChanges = true,
            CanSaveProject = false,
            UnsavedResolution = UnsavedProjectResolution.SaveProject
        };

        ApplicationTaskExecution<ProjectSwitchGuardResult<string>> execution =
            await fixture.Coordinator.ExecuteProjectSwitchAsync(
                ApplicationTaskKind.Exit,
                actions);

        Assert.Equal(ApplicationTaskOutcome.Completed, execution.Outcome);
        Assert.Equal(ProjectSwitchGuardStatus.SaveUnavailable, execution.Value?.Status);
        Assert.Equal(["resolve-unsaved"], actions.Events);
        Assert.False(fixture.Session.EditsLocked);
    }

    [Fact]
    public async Task OpenCancellationWaitsForNonCancellableNestedSaveThenSkipsSwitch()
    {
        using TestContext fixture = TestContext.Create();
        using CancellationTokenSource cancellation = new();
        RecordingSwitchActions actions = new(fixture.Session)
        {
            HasUnsavedProjectChanges = true,
            UnsavedResolution = UnsavedProjectResolution.SaveProject,
            SaveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously),
            SaveRelease = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        Task<ApplicationTaskExecution<ProjectSwitchGuardResult<string>>> running =
            fixture.Coordinator.ExecuteProjectSwitchAsync(
                ApplicationTaskKind.OpenProject,
                actions,
                cancellationToken: cancellation.Token);
        await actions.SaveEntered.Task;

        cancellation.Cancel();
        Assert.False(running.IsCompleted);
        actions.SaveRelease.SetResult();
        ApplicationTaskExecution<ProjectSwitchGuardResult<string>> execution = await running;

        Assert.Equal(ApplicationTaskOutcome.Cancelled, execution.Outcome);
        Assert.Equal(["resolve-unsaved", "save"], actions.Events);
        Assert.False(fixture.Session.EditsLocked);
    }

    [Fact]
    public async Task ProjectSwitchCountsGuardWorkThenPausesAtActualSwitchBoundary()
    {
        ManualTimeProvider clock = new();
        using TestContext fixture = TestContext.Create(clock);
        clock.Advance(TimeSpan.FromSeconds(1));
        RecordingSwitchActions actions = new(fixture.Session)
        {
            HasUnsavedProjectChanges = true,
            UnsavedResolution = UnsavedProjectResolution.SaveProject,
            SaveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously),
            SaveRelease = new(TaskCreationOptions.RunContinuationsAsynchronously),
            SwitchEntered = new(TaskCreationOptions.RunContinuationsAsynchronously),
            SwitchRelease = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };

        Task<ApplicationTaskExecution<ProjectSwitchGuardResult<string>>> running =
            fixture.Coordinator.ExecuteProjectSwitchAsync(
                ApplicationTaskKind.CloseProject,
                actions);
        await actions.SaveEntered.Task;
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(3_000, fixture.Session.SnapshotTotalEditingTimeMilliseconds());

        actions.SaveRelease.SetResult();
        await actions.SwitchEntered.Task;
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(3_000, fixture.Session.SnapshotTotalEditingTimeMilliseconds());

        actions.SwitchRelease.SetResult();
        ApplicationTaskExecution<ProjectSwitchGuardResult<string>> execution = await running;
        Assert.Equal(ApplicationTaskOutcome.Completed, execution.Outcome);
        Assert.Equal(ProjectSwitchGuardStatus.Completed, execution.Value?.Status);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(3_000, fixture.Session.SnapshotTotalEditingTimeMilliseconds());
    }

    [Fact]
    public async Task CancelledProjectSwitchLeavesEditingTimeRunningWithoutBackfill()
    {
        ManualTimeProvider clock = new();
        using TestContext fixture = TestContext.Create(clock);
        clock.Advance(TimeSpan.FromSeconds(1));
        RecordingSwitchActions actions = new(fixture.Session)
        {
            HasUnsavedProjectChanges = true,
            UnsavedResolution = UnsavedProjectResolution.Cancel
        };

        ApplicationTaskExecution<ProjectSwitchGuardResult<string>> execution =
            await fixture.Coordinator.ExecuteProjectSwitchAsync(
                ApplicationTaskKind.Exit,
                actions);
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(ApplicationTaskOutcome.Completed, execution.Outcome);
        Assert.Equal(ProjectSwitchGuardStatus.Cancelled, execution.Value?.Status);
        Assert.Equal(3_000, fixture.Session.SnapshotTotalEditingTimeMilliseconds());
    }

    [Fact]
    public async Task FailedActualProjectSwitchResumesEditingTimeAfterFailure()
    {
        ManualTimeProvider clock = new();
        using TestContext fixture = TestContext.Create(clock);
        clock.Advance(TimeSpan.FromSeconds(1));
        RecordingSwitchActions actions = new(fixture.Session)
        {
            SwitchError = new InvalidOperationException("Injected switch failure.")
        };

        ApplicationTaskExecution<ProjectSwitchGuardResult<string>> execution =
            await fixture.Coordinator.ExecuteProjectSwitchAsync(
                ApplicationTaskKind.OpenProject,
                actions);
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(ApplicationTaskOutcome.Failed, execution.Outcome);
        Assert.IsType<InvalidOperationException>(execution.Error);
        Assert.Equal(3_000, fixture.Session.SnapshotTotalEditingTimeMilliseconds());
    }

    [Fact]
    public void RealtimePreferencesRequireStoppedIdleStateAndInvalidateSamplePlans()
    {
        using TemporaryDirectory directory = new();
        using TestContext fixture = TestContext.Create();
        string preferencePath = Path.Combine(directory.Path, "preferences.json");
        string cacheRoot = Path.Combine(directory.Path, "cache");
        ApplicationPreferences initial = ApplicationPreferences.Default with
        {
            AudioCache = new AudioCachePreferences(cacheRoot, 4096)
        };
        Assert.True(new ApplicationPreferencesStore(preferencePath).Save(initial).Succeeded);
        ApplicationPreferencesService preferences = new(
            new ApplicationPreferencesStore(preferencePath),
            fixture.Coordinator,
            fixture.Session);
        MidiRenderPlan originalPlan = fixture.Session.GetOrCreateRenderPlan(48_000);
        string key = AudioCacheSessionStore.ComputeKey([1, 2, 3]);
        Assert.True(fixture.Session.PublishReusableAudio(key, [4, 5, 6]).Published);
        AudioCacheSessionSnapshot originalCache = fixture.Session.AudioCacheSnapshot!.Value;
        int changeEvents = 0;
        preferences.RealtimeAudioPreferencesChanged += (_, _) => changeEvents++;

        fixture.Coordinator.StartMainPlayback();
        ApplicationPreferenceUpdateResult whilePlaying = preferences.UpdateRealtimeAudio(
            new RealtimeAudioPreferences("device-1", 200, 75, 1_000));
        Assert.Equal(
            ApplicationPreferenceUpdateStatus.RejectedPlaybackNotStopped,
            whilePlaying.Status);
        fixture.Coordinator.StopPlayback();

        ApplicationPreferenceUpdateResult applied = preferences.UpdateRealtimeAudio(
            new RealtimeAudioPreferences("device-1", 200, 75, 1_000));

        Assert.Equal(ApplicationPreferenceUpdateStatus.Applied, applied.Status);
        Assert.Equal(1, changeEvents);
        Assert.Equal("device-1", preferences.Current.RealtimeAudio.PlaybackOutputDeviceId);
        Assert.NotSame(originalPlan, fixture.Session.GetOrCreateRenderPlan(48_000));
        AudioCacheSessionSnapshot replacementCache = fixture.Session.AudioCacheSnapshot!.Value;
        Assert.Equal(originalCache.SessionPath, replacementCache.SessionPath);
        Assert.True(Directory.Exists(originalCache.SessionPath));
        Assert.True(fixture.Session.TryReadReusableAudio(key, out byte[] retainedPayload));
        Assert.Equal([4, 5, 6], retainedPayload);
        ApplicationPreferencesLoadResult reloaded = new ApplicationPreferencesStore(
            preferencePath).Load();
        Assert.Null(reloaded.Notice);
        Assert.Equal(preferences.Current, reloaded.Preferences);
    }

    [Fact]
    public void AudioCachePreferencesRecreateTheProjectSessionStoreOnlyWhileStopped()
    {
        using TemporaryDirectory directory = new();
        using TestContext fixture = TestContext.Create();
        string preferencePath = Path.Combine(directory.Path, "preferences.json");
        string firstRoot = Path.Combine(directory.Path, "cache-a");
        string secondRoot = Path.Combine(directory.Path, "cache-b");
        ApplicationPreferences initial = ApplicationPreferences.Default with
        {
            AudioCache = new AudioCachePreferences(firstRoot, 4096)
        };
        Assert.True(new ApplicationPreferencesStore(preferencePath).Save(initial).Succeeded);
        ApplicationPreferencesService preferences = new(
            new ApplicationPreferencesStore(preferencePath),
            fixture.Coordinator,
            fixture.Session);
        AudioCacheSessionSnapshot first = fixture.Session.AudioCacheSnapshot!.Value;
        string key = AudioCacheSessionStore.ComputeKey([1, 2, 3]);
        Assert.True(fixture.Session.PublishReusableAudio(key, [4, 5, 6]).Published);

        fixture.Coordinator.StartMainPlayback();
        ApplicationPreferenceUpdateResult rejected = preferences.UpdateAudioCache(
            new AudioCachePreferences(secondRoot, 0));
        Assert.Equal(
            ApplicationPreferenceUpdateStatus.RejectedPlaybackNotStopped,
            rejected.Status);
        Assert.Equal(first.SessionPath, fixture.Session.AudioCacheSnapshot!.Value.SessionPath);
        fixture.Coordinator.StopPlayback();

        ApplicationPreferenceUpdateResult applied = preferences.UpdateAudioCache(
            new AudioCachePreferences(secondRoot, 0));
        AudioCacheSessionSnapshot second = fixture.Session.AudioCacheSnapshot!.Value;

        Assert.True(applied.Succeeded);
        Assert.Equal(Path.GetFullPath(secondRoot), second.RootPath);
        Assert.Equal(AudioCacheRetentionState.DisabledByPreference, second.RetentionState);
        Assert.False(Directory.Exists(first.SessionPath));
        Assert.False(fixture.Session.TryReadReusableAudio(key, out _));
    }

    [Fact]
    public async Task RealtimePreferencesRejectOtherActiveApplicationTask()
    {
        using TestContext fixture = TestContext.Create();
        using TemporaryDirectory directory = new();
        ApplicationPreferencesService preferences = new(
            new ApplicationPreferencesStore(Path.Combine(directory.Path, "preferences.json")),
            fixture.Coordinator,
            fixture.Session);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ApplicationTaskExecution<int>> task = fixture.Coordinator.ExecuteAsync(
            ApplicationTaskKind.AudioRender,
            async (_, _) =>
            {
                entered.SetResult();
                await release.Task;
                return 1;
            });
        await entered.Task;

        ApplicationPreferenceUpdateResult result = preferences.UpdateRealtimeAudio(
            new RealtimeAudioPreferences(null, 200, 75, 1_000));

        Assert.Equal(ApplicationPreferenceUpdateStatus.RejectedApplicationBusy, result.Status);
        release.SetResult();
        await task;
    }

    [Fact]
    public void PreferenceWriteFailureSwitchesRuntimeToSafeDefaults()
    {
        using TestContext fixture = TestContext.Create();
        using TemporaryDirectory directory = new();
        string blockedParent = Path.Combine(directory.Path, "blocked");
        ApplicationPreferencesService preferences = new(
            new ApplicationPreferencesStore(Path.Combine(blockedParent, "preferences.json")),
            fixture.Coordinator,
            fixture.Session);
        Assert.Equal(
            ApplicationPreferenceUpdateStatus.Applied,
            preferences.UpdateRealtimeAudio(
                new RealtimeAudioPreferences("device-1", 200, 75, 1_000)).Status);
        Directory.Delete(blockedParent, recursive: true);
        File.WriteAllText(blockedParent, "not a directory");

        ApplicationPreferenceUpdateResult failed = preferences.UpdateRecentDirectory(
            RecentDirectoryPurpose.MidiExport,
            directory.Path);

        Assert.Equal(ApplicationPreferenceUpdateStatus.FailedUsingDefaults, failed.Status);
        Assert.Equal("PreferenceWriteFailed", failed.Notice?.Code);
        Assert.Equal(ApplicationPreferences.Default, preferences.Current);
    }

    [Fact]
    public void ResetPreferencesRestoresAllDefaultsAndInvalidatesRealtimePlan()
    {
        using TestContext fixture = TestContext.Create();
        using TemporaryDirectory directory = new();
        ApplicationPreferencesService preferences = new(
            new ApplicationPreferencesStore(Path.Combine(directory.Path, "preferences.json")),
            fixture.Coordinator,
            fixture.Session);
        Assert.True(preferences.UpdateRealtimeAudio(
            new RealtimeAudioPreferences("device-1", 200, 75, 1_000)).Succeeded);
        Assert.True(preferences.UpdateRecentDirectory(
            RecentDirectoryPurpose.OpenProject,
            directory.Path).Succeeded);
        MidiRenderPlan beforeReset = fixture.Session.GetOrCreateRenderPlan(48_000);

        ApplicationPreferenceUpdateResult reset = preferences.ResetToDefaults();

        Assert.True(reset.Succeeded);
        Assert.Equal(ApplicationPreferences.Default, preferences.Current);
        Assert.NotSame(beforeReset, fixture.Session.GetOrCreateRenderPlan(48_000));
        Assert.Equal(
            ApplicationPreferences.Default,
            new ApplicationPreferencesStore(
                Path.Combine(directory.Path, "preferences.json")).Load().Preferences);
    }

    private sealed class TestContext : IDisposable
    {
        private readonly string _soundFontPath;

        private TestContext(
            string soundFontPath,
            ProjectCompilationSession session,
            FakeBackend backend,
            PlaybackController playback,
            ApplicationTaskCoordinator coordinator)
        {
            _soundFontPath = soundFontPath;
            Session = session;
            Backend = backend;
            Playback = playback;
            Coordinator = coordinator;
        }

        public ProjectCompilationSession Session { get; }
        public FakeBackend Backend { get; }
        public PlaybackController Playback { get; }
        public ApplicationTaskCoordinator Coordinator { get; }

        public static TestContext Create(TimeProvider? editingTimeProvider = null)
        {
            string soundFont = Path.GetTempFileName();
            ProjectCompilationSession session = new(
                CreateProject(),
                soundFont,
                editingTimeProvider);
            FakeBackend backend = new();
            PlaybackController playback = new(session, backend);
            return new(
                soundFont,
                session,
                backend,
                playback,
                new ApplicationTaskCoordinator(session, playback));
        }

        public void Dispose()
        {
            Coordinator.Dispose();
            Playback.Dispose();
            Session.Dispose();
            File.Delete(_soundFontPath);
        }
    }

    private sealed class AcceptingSoundFontValidator : ISoundFontLoadabilityValidator
    {
        public ValueTask ValidateAsync(
            string soundFontPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private static MidoraProject CreateProject()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project)
        {
            Name = "Piano",
            TemplateLengthTicks = 480,
            OverlapPolicy = OverlapPolicy.Warn
        };
        SubVoice voice = new(project);
        voice.Events.Add(TemplateEvent.Note(project, 0, 480, 60, 100));
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project) { Name = "Track", EventInstrumentId = instrument.Id };
        Segment segment = new(project) { LengthTicks = 960 };
        segment.Notes.Add(new LogicalNote(project)
        {
            LengthTicks = 480,
            Note = 60,
            Velocity = 100
        });
        track.Segments.Add(segment);
        project.Tracks.Add(track);
        return project;
    }

    private sealed class FakeBackend
        : IRealtimePlaybackBackend, IHeldPreviewRealtimePlaybackBackend
    {
        public int ActualSampleRate => 48_000;
        public long PositionFrames { get; private set; }
        public long RenderPositionFrames => PositionFrames;
        public bool IsBuffering => false;
        public bool IsCompleted => false;
        public bool IsFaulted => false;
        public string? FaultDescription => null;
        public bool OutputDeviceSelectionRequired => false;
        public string? OutputDeviceSelectionReason => null;
        public int StopCount { get; private set; }
        public bool ThrowOnStop { get; set; }
        public bool HeldProducerPaused { get; private set; }

        public int Prepare() => ActualSampleRate;

        public void Start(MidiRenderPlan plan, string soundFontPath, PlaybackMasterConfiguration master)
        {
            PositionFrames = 0;
        }

        public void ApplyMonitoringCommands(ReadOnlySpan<MidiMonitoringCommand> commands)
        {
        }

        public long PauseHeldPreviewAtProducerFrontier(TimeSpan timeout)
        {
            HeldProducerPaused = true;
            return RenderPositionFrames;
        }

        public void ReplaceHeldPreviewFutureAndResume(
            MidiRenderPlan plan,
            long producerFrontierFrame,
            TimeSpan timeout)
        {
            HeldProducerPaused = false;
        }

        public void ResumeHeldPreviewFromProducerFrontier()
        {
            HeldProducerPaused = false;
        }

        public void Stop(bool flush)
        {
            StopCount++;
            if (ThrowOnStop)
            {
                throw new InvalidOperationException("Injected playback cleanup failure.");
            }
        }

        public void Reset()
        {
        }

        public void SelectOutputDevice(string? deviceId)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingSwitchActions(ProjectCompilationSession session)
        : IProjectSwitchGuardActions<string>
    {
        public bool HasFunctionDrafts { get; set; }
        public bool HasUnsavedProjectChanges { get; set; }
        public bool CanSaveProject { get; set; } = true;
        public FunctionDraftResolution DraftResolution { get; set; } =
            FunctionDraftResolution.Discard;
        public UnsavedProjectResolution UnsavedResolution { get; set; } =
            UnsavedProjectResolution.CloseWithoutSaving;
        public List<string> Events { get; } = [];
        public bool WasEditLockedWhileApplyingDrafts { get; private set; }
        public bool WasEditLockedWhileResolvingUnsaved { get; private set; }
        public bool WasEditLockedWhileSaving { get; private set; }
        public bool WasEditLockedWhileSwitching { get; private set; }
        public TaskCompletionSource? SaveEntered { get; set; }
        public TaskCompletionSource? SaveRelease { get; set; }
        public TaskCompletionSource? SwitchEntered { get; set; }
        public TaskCompletionSource? SwitchRelease { get; set; }
        public Exception? SwitchError { get; set; }

        public ValueTask<FunctionDraftResolution> ResolveFunctionDraftsAsync(
            CancellationToken cancellationToken)
        {
            Events.Add("resolve-drafts");
            return ValueTask.FromResult(DraftResolution);
        }

        public Task ApplyFunctionDraftsAsync(CancellationToken cancellationToken)
        {
            Events.Add("apply-drafts");
            WasEditLockedWhileApplyingDrafts = session.EditsLocked;
            session.ApplyEdit(
                project => project.Metadata.ProjectName = "Draft applied",
                ProjectChangeSet.Everything);
            return Task.CompletedTask;
        }

        public Task DiscardFunctionDraftsAsync(CancellationToken cancellationToken)
        {
            Events.Add("discard-drafts");
            return Task.CompletedTask;
        }

        public ValueTask<UnsavedProjectResolution> ResolveUnsavedProjectAsync(
            CancellationToken cancellationToken)
        {
            Events.Add("resolve-unsaved");
            WasEditLockedWhileResolvingUnsaved = session.EditsLocked;
            return ValueTask.FromResult(UnsavedResolution);
        }

        public async Task SaveProjectAsync(CancellationToken cancellationToken)
        {
            Events.Add("save");
            WasEditLockedWhileSaving = session.EditsLocked;
            Assert.False(cancellationToken.CanBeCanceled);
            SaveEntered?.SetResult();
            if (SaveRelease is not null)
            {
                await SaveRelease.Task;
            }
        }

        public async Task<string> PerformProjectSwitchAsync(CancellationToken cancellationToken)
        {
            Events.Add("switch");
            WasEditLockedWhileSwitching = session.EditsLocked;
            SwitchEntered?.SetResult();
            if (SwitchRelease is not null)
            {
                await SwitchRelease.Task;
            }
            if (SwitchError is not null)
            {
                throw SwitchError;
            }
            return "switched";
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan duration) =>
            _timestamp = checked(_timestamp + duration.Ticks);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"midora-application-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
            else if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }
}
