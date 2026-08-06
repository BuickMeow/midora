using Midora.Audio;
using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;

namespace Midora.Playback.Tests;

public sealed class PlaybackTests
{
    [Fact]
    public void ProjectOpenTimeCountsAllActiveSessionTimeAndPausesOnlyForSuspendOrClosing()
    {
        ManualTimeProvider clock = new();
        MidoraProject project = CreateProject();
        ProjectCompilationSession session = new(project, editingTimeProvider: clock);
        long fingerprint = session.LastAttempt.Fingerprint;

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(10_000, session.SnapshotTotalEditingTimeMilliseconds());

        session.NotifySystemSuspending();
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(10_000, session.SnapshotTotalEditingTimeMilliseconds());

        session.NotifySystemResumed();
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Equal(10_250, session.SnapshotTotalEditingTimeMilliseconds());

        session.BeginProjectClosing();
        session.NotifySystemSuspending();
        session.CancelProjectClosing();
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(10_250, session.SnapshotTotalEditingTimeMilliseconds());

        session.NotifySystemResumed();
        clock.Advance(TimeSpan.FromMilliseconds(750));
        Assert.Equal(11_000, session.SnapshotTotalEditingTimeMilliseconds());
        Assert.Equal(
            fingerprint,
            session.Recompile(new ProjectChangeSet()).Fingerprint);
        session.Dispose();

        Assert.Equal(11_000, project.Metadata.TotalEditingTimeMilliseconds);
    }

    [Fact]
    public void ProjectRejectsConcurrentEditingTimeOwnersAndAllowsNextOpenAfterClose()
    {
        ManualTimeProvider clock = new();
        MidoraProject project = CreateProject();
        ProjectCompilationSession first = new(project, editingTimeProvider: clock);

        Assert.Throws<InvalidOperationException>(() =>
            new ProjectCompilationSession(project, editingTimeProvider: clock));

        clock.Advance(TimeSpan.FromMilliseconds(1_500));
        first.Dispose();
        clock.Advance(TimeSpan.FromSeconds(1));
        using ProjectCompilationSession reopened = new(project, editingTimeProvider: clock);
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(3_500, reopened.SnapshotTotalEditingTimeMilliseconds());
    }

    [Fact]
    public void TempoMapUsesCumulativeTempoAndSingleFinalRounding()
    {
        TempoSampleMap map = new(480, [new(0, 120m), new(480, 60m)]);

        Assert.Equal(24_000, map.TickToSampleFrame(480, 0, 48_000));
        Assert.Equal(72_000, map.TickToSampleFrame(960, 0, 48_000));
        Assert.Equal(960, map.SampleFrameToTick(72_000, 0, 48_000, 2_000));
    }

    [Fact]
    public void TempoMapRoundsOnceAfterIntegratingFromNonZeroOriginAcrossTempoBoundary()
    {
        TempoSampleMap map = new(480, [new(0, 120m), new(480, 60m)]);

        // [240, 480) and [480, 600) each last 0.25 seconds. Rounding each
        // segment independently would produce 0 + 0 frames instead of 1.
        Assert.Equal(0, map.TickToSampleFrame(599, 240, 1));
        Assert.Equal(1, map.TickToSampleFrame(600, 240, 1));
    }

    [Theory]
    [InlineData(8_000)]
    [InlineData(44_100)]
    [InlineData(48_000)]
    [InlineData(192_000)]
    [InlineData(12_345)]
    public void AdapterSupportsAllRequiredSampleRateShapes(int sampleRate)
    {
        MidoraProject project = CreateProject();
        CanonicalCompiledResult compiled = new MidoraCompiler().CompileFull(
            project,
            new CompilationRequest { EndTick = 960 });

        MidiRenderPlan plan = MidiRenderPlanAdapter.Create(compiled, sampleRate);

        Assert.Equal(sampleRate, plan.SampleRate);
        Assert.Equal(sampleRate, plan.TotalFrameCount); // 960 ticks at 120 BPM = 1 second
        Assert.NotEmpty(plan.Ports.ToArray());
    }

    [Fact]
    public void RealtimeAdapterUsesPositiveDeviceRateOutsideFileRenderRange()
    {
        MidoraProject project = CreateProject();
        CanonicalCompiledResult compiled = new MidoraCompiler().CompileFull(project);

        MidiRenderPlan realtime = MidiRenderPlanAdapter.CreateRealtime(
            compiled, 384_000, new HashSet<MidoraId> { project.Tracks[0].Id });

        Assert.Equal(384_000, realtime.SampleRate);
        Assert.Throws<ArgumentOutOfRangeException>(() => MidiRenderPlanAdapter.Create(compiled, 384_000));
    }

    [Fact]
    public void RealtimeAdapterPreservesMutedTrackEventsAndMarksTheirSourceDisabled()
    {
        MidoraProject project = CreateProject();
        CanonicalCompiledResult compiled = new MidoraCompiler().CompileFull(project);

        MidiRenderPlan plan = MidiRenderPlanAdapter.CreateRealtime(
            compiled,
            48_000,
            new HashSet<MidoraId>());

        Assert.Equal([project.Tracks[0].Id.Value], plan.SourceIds.ToArray());
        Assert.Equal([0], plan.InitiallyDisabledSourceIndices.ToArray());
        Assert.Contains(plan.Ports[0].Events.ToArray(), value => value.SourceIndex == 0);
    }

    [Fact]
    public void EditRecompilesIncrementallyAndInvalidatesSamplePlanCache()
    {
        MidoraProject project = CreateProject();
        ProjectCompilationSession session = new(project);
        MidiRenderPlan first = session.GetOrCreateRenderPlan(48_000);
        LogicalTrack track = project.Tracks[0];
        ProjectChangeSet changes = new();
        changes.TrackIds.Add(track.Id);

        CanonicalCompiledResult result = session.ApplyEdit(
            value => value.Tracks[0].Segments[0].Notes[0].Note = 67,
            changes);
        MidiRenderPlan second = session.GetOrCreateRenderPlan(48_000);

        Assert.True(result.IsConsumable);
        Assert.NotSame(first, second);
        Assert.Equal(1, session.LastCompilationTelemetry.RecompiledTrackCount);
    }

    [Fact]
    public void StartStopSeekAreColdStartsAndLockEdits()
    {
        string soundFont = Path.GetTempFileName();
        try
        {
            MidoraProject project = CreateProject();
            ProjectCompilationSession session = new(project, soundFont);
            FakeBackend backend = new();
            using PlaybackController controller = new(session, backend);

            controller.Start();
            Assert.Equal(PlaybackState.Playing, controller.State);
            Assert.True(session.EditsLocked);
            Assert.Null(project.SoundFont.Reference);
            Assert.Throws<InvalidOperationException>(() =>
                session.SetEffectiveSoundFontPath(null));
            Assert.Throws<InvalidOperationException>(() => session.ApplyEdit(_ => { }, new ProjectChangeSet()));

            controller.Seek(240);
            Assert.Equal(2, backend.StartCount);
            Assert.Equal(1, backend.StopCount);
            Assert.Equal(PlaybackState.Playing, controller.State);

            controller.Stop();
            Assert.Equal(PlaybackState.Stopped, controller.State);
            Assert.False(session.EditsLocked);
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void InvalidCurrentSourceCannotReusePreviousSuccessfulAudioPlan()
    {
        MidoraProject project = CreateProject();
        ProjectCompilationSession session = new(project);
        _ = session.GetOrCreateRenderPlan(48_000);
        ProjectChangeSet changes = new();
        changes.TrackIds.Add(project.Tracks[0].Id);

        CanonicalCompiledResult invalid = session.ApplyEdit(
            value => value.Tracks[0].Segments[0].Notes[0].Velocity = 0,
            changes);

        Assert.False(invalid.IsConsumable);
        Assert.Throws<InvalidOperationException>(() => session.GetOrCreateRenderPlan(48_000));
    }

    [Fact]
    public void StopCursorBehaviorUsesOriginalTaskStartEvenAfterSeek()
    {
        string soundFont = Path.GetTempFileName();
        try
        {
            MidoraProject project = CreateProject();
            FakeBackend backend = new();
            using PlaybackController controller = new(new(project, soundFont), backend);

            controller.Start(0);
            controller.Seek(240);
            backend.PositionFrames = 12_000;
            Assert.Equal(480, controller.CurrentTick);
            controller.Stop();

            Assert.Equal(0, controller.CurrentTick);
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void NaturalCompletionLoopsThroughColdRangeStart()
    {
        string soundFont = Path.GetTempFileName();
        try
        {
            MidoraProject project = CreateProject();
            FakeBackend backend = new();
            using PlaybackController controller = new(new(project, soundFont), backend);
            controller.SetLoop(new TickRange(240, 480));
            controller.Start();

            backend.IsCompleted = true;
            controller.Update();

            Assert.Equal(2, backend.StartCount);
            Assert.Equal(1, backend.StopCount);
            Assert.Equal(PlaybackState.Playing, controller.State);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void MuteSoloAreRuntimeOnlyAndResetPlaybackEngineClearsBackend()
    {
        string soundFont = Path.GetTempFileName();
        try
        {
            MidoraProject project = CreateProject();
            FakeBackend backend = new();
            ProjectCompilationSession session = new(project, soundFont);
            using PlaybackController controller = new(session, backend);
            MidoraId trackId = project.Tracks[0].Id;
            long fingerprint = session.LastAttempt.Fingerprint;

            controller.Start();
            controller.SetTrackMuted(trackId, true);
            controller.SetTrackSolo(trackId, true);
            controller.SetTrackMuted(trackId, false);
            controller.ResetPlaybackEngine();

            Assert.Equal(fingerprint, session.LastAttempt.Fingerprint);
            Assert.Equal(1, backend.StartCount);
            Assert.Contains(backend.MonitoringCommands,
                value => value.Kind == MidiMonitoringCommandKind.SetSourceEnabled && !value.SourceEnabled);
            Assert.Contains(backend.MonitoringCommands,
                value => value.Kind == MidiMonitoringCommandKind.SetSourceEnabled && value.SourceEnabled);
            Assert.Equal(1, backend.ResetCount);
            Assert.Equal(PlaybackState.Stopped, controller.State);
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void FailedMonitoringCommandRollsBackRuntimeFilterState()
    {
        string soundFont = Path.GetTempFileName();
        try
        {
            MidoraProject project = CreateProject();
            FakeBackend backend = new() { ThrowMonitoringCommands = true };
            using PlaybackController controller = new(new ProjectCompilationSession(project, soundFont), backend);
            MidoraId trackId = project.Tracks[0].Id;
            controller.Start();

            Assert.Throws<InvalidOperationException>(() => controller.SetTrackMuted(trackId, true));
            backend.ThrowMonitoringCommands = false;
            controller.SetTrackMuted(trackId, true);

            Assert.Equal(2, backend.MonitoringApplyCount);
            Assert.Contains(backend.MonitoringCommands,
                value => value.Kind == MidiMonitoringCommandKind.SetSourceEnabled && !value.SourceEnabled);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void MonitoringRestoreUsesActivePlanRoutingInsteadOfColdRangeReallocation()
    {
        string soundFont = Path.GetTempFileName();
        try
        {
            (MidoraProject project, LogicalTrack restoredTrack) = CreateMonitoringRoutingProject();
            ProjectCompilationSession session = new(project, soundFont);
            ChannelUnitAllocation activeAllocation = Assert.Single(
                session.LastAttempt.Allocations.ToArray(),
                value => value.TrackId == restoredTrack.Id);
            Assert.Equal((byte)1, activeAllocation.ZeroBasedChannel);
            FakeBackend backend = new();
            using PlaybackController controller = new(session, backend);
            controller.Start();
            backend.PositionFrames = 15_000; // tick 300 at 120 BPM / 48 kHz

            controller.SetTrackMuted(restoredTrack.Id, true);
            int commandCountBeforeRestore = backend.MonitoringCommands.Count;
            controller.SetTrackMuted(restoredTrack.Id, false);

            MidiMonitoringCommand[] restoreCommands = backend.MonitoringCommands
                .Skip(commandCountBeforeRestore)
                .ToArray();
            Assert.Contains(restoreCommands, value =>
                value.Kind == MidiMonitoringCommandKind.SetSourceEnabled
                && value.SourceEnabled);
            MidiMonitoringCommand programRestore = Assert.Single(restoreCommands, value =>
                value.Kind == MidiMonitoringCommandKind.SendMessage
                && value.Message.MessageType == MidiMessageType.ProgramChange);
            Assert.Equal(activeAllocation.ZeroBasedPort, programRestore.ZeroBasedPortNumber);
            Assert.Equal(activeAllocation.ZeroBasedChannel, programRestore.Message.ChannelNumber);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void StartWithoutExplicitTickUsesStoppedCursor()
    {
        string soundFont = Path.GetTempFileName();
        try
        {
            MidoraProject project = CreateProject();
            FakeBackend backend = new();
            using PlaybackController controller = new(new(project, soundFont), backend);

            controller.Seek(240);
            controller.Start();

            Assert.Equal(240, controller.CurrentTick);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void ZeroLengthPlaybackPreparesAndImmediatelyReturnsToStopped()
    {
        string soundFont = Path.GetTempFileName();
        try
        {
            MidoraProject project = CreateProject();
            FakeBackend backend = new();
            using PlaybackController controller = new(new(project, soundFont), backend);

            controller.Start(240, 240);

            Assert.Equal(PlaybackState.Stopped, controller.State);
            Assert.Equal(PlaybackTaskKind.None, controller.ActiveTaskKind);
            Assert.Equal(1, backend.PrepareCount);
            Assert.Equal(0, backend.StartCount);

            controller.Start(0, 240);
            Assert.Equal(PlaybackState.Playing, controller.State);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void EventInstrumentPreviewSharesBackendExclusivelyAndPreservesMainCursor()
    {
        string soundFont = Path.GetTempFileName();
        try
        {
            MidoraProject project = CreateProject();
            FakeBackend backend = new();
            ProjectCompilationSession session = new(project, soundFont);
            using PlaybackController controller = new(session, backend);
            controller.Seek(240);

            controller.StartEventInstrumentPreview(new EventInstrumentPreviewRequest(
                project.EventInstruments[0].Id,
                Pitch: 67,
                GateLengthTicks: 240,
                Tempo: 100m));

            Assert.Equal(PlaybackTaskKind.EventInstrumentPreview, controller.ActiveTaskKind);
            Assert.Equal(PlaybackState.Playing, controller.State);
            Assert.Equal(240, controller.CurrentTick);
            Assert.Equal(0, controller.CurrentTaskTick);
            Assert.True(session.EditsLocked);
            Assert.Throws<InvalidOperationException>(() => controller.StartSegmentPreview(
                project.Tracks[0].Id, project.Tracks[0].Segments[0].Id));
            Assert.Equal(1, backend.PrepareCount);

            controller.Stop();

            Assert.Equal(PlaybackTaskKind.None, controller.ActiveTaskKind);
            Assert.Equal(240, controller.CurrentTick);
            Assert.False(session.EditsLocked);
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void PreparingStateIsPublishedOnlyAfterProjectEditsAreLocked()
    {
        string soundFont = Path.GetTempFileName();
        try
        {
            MidoraProject project = CreateProject();
            ProjectCompilationSession session = new(project, soundFont);
            FakeBackend backend = new();
            using PlaybackController controller = new(session, backend);
            List<bool> preparingLockStates = [];
            controller.StateChanged += (_, _) =>
            {
                if (controller.State == PlaybackState.Preparing)
                {
                    preparingLockStates.Add(session.EditsLocked);
                }
            };

            controller.Start();
            controller.Stop();
            controller.StartEventInstrumentPreview(new EventInstrumentPreviewRequest(
                project.EventInstruments[0].Id,
                Pitch: 67,
                GateLengthTicks: 240,
                Tempo: 100m));

            Assert.Equal([true, true], preparingLockStates);
            Assert.True(session.EditsLocked);
            controller.Stop();
            Assert.False(session.EditsLocked);
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void PreviewCompilationFailureClearsTaskAndProjectEditLock()
    {
        string soundFont = Path.GetTempFileName();
        try
        {
            MidoraProject project = CreateProject();
            ProjectCompilationSession session = new(project, soundFont);
            FakeBackend backend = new();
            using PlaybackController controller = new(session, backend);

            Assert.Throws<ArgumentException>(() => controller.StartEventInstrumentPreview(
                new EventInstrumentPreviewRequest(MidoraId.FromParts(ulong.MaxValue, ulong.MaxValue))));

            Assert.Equal(PlaybackState.Error, controller.State);
            Assert.Equal(PlaybackTaskKind.None, controller.ActiveTaskKind);
            Assert.False(session.EditsLocked);
            Assert.Equal(0, backend.PrepareCount);
            Assert.NotNull(controller.LastError);

            controller.StartEventInstrumentPreview(new EventInstrumentPreviewRequest(
                project.EventInstruments[0].Id,
                Pitch: 67,
                GateLengthTicks: 240,
                Tempo: 100m));

            Assert.Equal(PlaybackState.Playing, controller.State);
            Assert.Equal(1, backend.ResetCount);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void RealtimePreviewUsesPositiveActualDeviceRateOutsideFileRange()
    {
        string soundFont = Path.GetTempFileName();
        try
        {
            MidoraProject project = CreateProject();
            FakeBackend backend = new() { ActualSampleRate = 384_000 };
            using PlaybackController controller = new(new(project, soundFont), backend);

            controller.StartEventInstrumentPreview(new EventInstrumentPreviewRequest(
                project.EventInstruments[0].Id,
                Pitch: 67,
                GateLengthTicks: 240,
                Tempo: 100m));

            Assert.Equal(PlaybackState.Playing, controller.State);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void SegmentPreviewNaturalCompletionDoesNotMoveMainCursor()
    {
        string soundFont = Path.GetTempFileName();
        try
        {
            MidoraProject project = CreateProject();
            FakeBackend backend = new();
            using PlaybackController controller = new(new(project, soundFont), backend);
            controller.Seek(300);
            controller.StartSegmentPreview(project.Tracks[0].Id, project.Tracks[0].Segments[0].Id);
            backend.IsCompleted = true;

            controller.Update();

            Assert.Equal(PlaybackState.Stopped, controller.State);
            Assert.Equal(PlaybackTaskKind.None, controller.ActiveTaskKind);
            Assert.Equal(300, controller.CurrentTick);
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void BackendFaultStopsTaskUnlocksEditsAndPlayCanRecoverDirectly()
    {
        string soundFont = Path.GetTempFileName();
        try
        {
            MidoraProject project = CreateProject();
            FakeBackend backend = new();
            ProjectCompilationSession session = new(project, soundFont);
            using PlaybackController controller = new(session, backend);
            controller.Start();
            backend.PositionFrames = 12_000;
            backend.IsFaulted = true;
            backend.FaultDescription = "synthetic backend fault";

            controller.Update();

            Assert.Equal(PlaybackState.Error, controller.State);
            Assert.Equal(PlaybackTaskKind.None, controller.ActiveTaskKind);
            Assert.False(session.EditsLocked);
            Assert.Contains("synthetic", controller.LastError?.Message);
            Assert.Equal(240, controller.CurrentTick);

            backend.IsFaulted = false;
            controller.Start();

            Assert.Equal(PlaybackState.Playing, controller.State);
            Assert.Equal(1, backend.ResetCount);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void MissingSoundFontStartFailureClearsTaskAndEditLockForRecovery()
    {
        MidoraProject project = CreateProject();
        ProjectCompilationSession session = new(project);
        FakeBackend backend = new();
        using PlaybackController controller = new(session, backend);

        Assert.Throws<InvalidOperationException>(() => controller.Start());

        Assert.Equal(PlaybackState.Stopped, controller.State);
        Assert.Equal(PlaybackTaskKind.None, controller.ActiveTaskKind);
        Assert.False(session.EditsLocked);
        Assert.Null(backend.LastStartedPlan);

        string soundFont = Path.GetTempFileName();
        try
        {
            session.SetEffectiveSoundFontPath(soundFont);
            controller.Start();

            Assert.Equal(PlaybackState.Playing, controller.State);
            Assert.Equal(0, backend.ResetCount);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void PrepareFailureClearsTaskAndEditLockForDirectRecovery()
    {
        string soundFont = Path.GetTempFileName();
        try
        {
            MidoraProject project = CreateProject();
            ProjectCompilationSession session = new(project, soundFont);
            FakeBackend backend = new() { ThrowPrepare = true };
            using PlaybackController controller = new(session, backend);

            Assert.Throws<InvalidOperationException>(() => controller.Start());

            Assert.Equal(PlaybackState.Error, controller.State);
            Assert.Equal(PlaybackTaskKind.None, controller.ActiveTaskKind);
            Assert.False(session.EditsLocked);
            backend.ThrowPrepare = false;

            controller.Start();

            Assert.Equal(PlaybackState.Playing, controller.State);
            Assert.Equal(1, backend.ResetCount);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void StopFailureClearsActiveStateAndUnlocksBeforeRecovery()
    {
        string soundFont = Path.GetTempFileName();
        try
        {
            MidoraProject project = CreateProject();
            ProjectCompilationSession session = new(project, soundFont);
            FakeBackend backend = new();
            using PlaybackController controller = new(session, backend);
            controller.Start();
            backend.ThrowStop = true;

            Assert.Throws<InvalidOperationException>(() => controller.Stop());

            Assert.Equal(PlaybackState.Error, controller.State);
            Assert.Equal(PlaybackTaskKind.None, controller.ActiveTaskKind);
            Assert.False(session.EditsLocked);
            Assert.NotNull(controller.LastError);
            backend.ThrowStop = false;

            controller.Start();

            Assert.Equal(PlaybackState.Playing, controller.State);
            Assert.Equal(1, backend.ResetCount);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void ResetContinuesAfterStopFailureAndRecoversToStopped()
    {
        string soundFont = Path.GetTempFileName();
        try
        {
            MidoraProject project = CreateProject();
            ProjectCompilationSession session = new(project, soundFont);
            FakeBackend backend = new();
            using PlaybackController controller = new(session, backend);
            controller.Start();
            backend.ThrowStop = true;

            controller.ResetPlaybackEngine();

            Assert.Equal(1, backend.StopCount);
            Assert.Equal(1, backend.ResetCount);
            Assert.Equal(PlaybackState.Stopped, controller.State);
            Assert.Equal(PlaybackTaskKind.None, controller.ActiveTaskKind);
            Assert.Null(controller.LastError);
            Assert.False(session.EditsLocked);
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void ResetFromStoppedClearsBackendWithoutStoppingOrChangingProjectCompilation()
    {
        string soundFont = Path.GetTempFileName();
        try
        {
            MidoraProject project = CreateProject();
            ProjectCompilationSession session = new(project, soundFont);
            FakeBackend backend = new();
            using PlaybackController controller = new(session, backend);
            long fingerprint = session.LastAttempt.Fingerprint;

            controller.ResetPlaybackEngine();

            Assert.Equal(0, backend.StopCount);
            Assert.Equal(1, backend.ResetCount);
            Assert.Equal(PlaybackState.Stopped, controller.State);
            Assert.Equal(fingerprint, session.LastAttempt.Fingerprint);
            Assert.Null(controller.LastError);
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void ResetFailurePreservesErrorAndAggregatesStopFailure()
    {
        string soundFont = Path.GetTempFileName();
        try
        {
            MidoraProject project = CreateProject();
            ProjectCompilationSession session = new(project, soundFont);
            FakeBackend backend = new();
            using PlaybackController controller = new(session, backend);
            controller.Start();
            backend.ThrowStop = true;
            backend.ThrowReset = true;

            AggregateException failure = Assert.Throws<AggregateException>(controller.ResetPlaybackEngine);

            Assert.Equal(2, failure.InnerExceptions.Count);
            Assert.Equal(1, backend.StopCount);
            Assert.Equal(1, backend.ResetCount);
            Assert.Equal(PlaybackState.Error, controller.State);
            Assert.Equal(PlaybackTaskKind.None, controller.ActiveTaskKind);
            Assert.Same(failure, controller.LastError);
            Assert.False(session.EditsLocked);
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void DirectPlayRecoveryKeepsErrorWhenBackendResetFails()
    {
        string soundFont = Path.GetTempFileName();
        try
        {
            MidoraProject project = CreateProject();
            ProjectCompilationSession session = new(project, soundFont);
            FakeBackend backend = new() { ThrowPrepare = true };
            using PlaybackController controller = new(session, backend);
            Assert.Throws<InvalidOperationException>(() => controller.Start());
            backend.ThrowPrepare = false;
            backend.ThrowReset = true;

            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => controller.Start());

            Assert.Equal(1, backend.ResetCount);
            Assert.Equal(PlaybackState.Error, controller.State);
            Assert.Equal(PlaybackTaskKind.None, controller.ActiveTaskKind);
            Assert.Same(failure, controller.LastError);
            Assert.False(session.EditsLocked);
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void SeekRestartStopFailureClearsTaskAndPreservesCurrentCursor()
    {
        string soundFont = Path.GetTempFileName();
        try
        {
            MidoraProject project = CreateProject();
            ProjectCompilationSession session = new(project, soundFont);
            FakeBackend backend = new();
            using PlaybackController controller = new(session, backend);
            controller.Start();
            backend.PositionFrames = 12_000;
            Assert.Equal(240, controller.CurrentTick);
            backend.ThrowStop = true;

            Assert.Throws<InvalidOperationException>(() => controller.Seek(480));

            Assert.Equal(PlaybackState.Error, controller.State);
            Assert.Equal(PlaybackTaskKind.None, controller.ActiveTaskKind);
            Assert.Equal(240, controller.CurrentTick);
            Assert.False(session.EditsLocked);
            backend.ThrowStop = false;

            controller.Start();

            Assert.Equal(PlaybackState.Playing, controller.State);
            Assert.Equal(1, backend.ResetCount);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
        }
    }

    [Fact]
    public void InvalidEffectiveLoopRangeIsRejectedBeforeMutatingPlaybackState()
    {
        string soundFont = Path.GetTempFileName();
        try
        {
            MidoraProject project = CreateProject();
            FakeBackend backend = new();
            using PlaybackController controller = new(new(project, soundFont), backend);
            controller.Seek(600);
            controller.SetLoop(new TickRange(240, 480));

            Assert.Throws<ArgumentOutOfRangeException>(() => controller.Start());

            Assert.Equal(PlaybackState.Stopped, controller.State);
            Assert.Equal(PlaybackTaskKind.None, controller.ActiveTaskKind);
            Assert.Equal(0, backend.PrepareCount);

            controller.Seek(0);
            controller.Start();
            Assert.Throws<ArgumentOutOfRangeException>(() => controller.Seek(600));
            Assert.Equal(PlaybackState.Playing, controller.State);
            Assert.Equal(0, backend.StopCount);
            controller.Stop();
        }
        finally
        {
            File.Delete(soundFont);
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

    private static (MidoraProject Project, LogicalTrack RestoredTrack) CreateMonitoringRoutingProject()
    {
        MidoraProject project = new(480);

        EventInstrument firstInstrument = new(project)
        {
            Name = "First",
            RootNote = 60,
            TemplateLengthTicks = 480,
            OverlapPolicy = OverlapPolicy.Warn
        };
        SubVoice firstVoice = new(project);
        firstVoice.Events.Add(TemplateEvent.Note(project, 0, 480, 60, 100));
        firstInstrument.SubVoices.Add(firstVoice);
        project.EventInstruments.Add(firstInstrument);
        LogicalTrack firstTrack = new(project)
        {
            Name = "First",
            EventInstrumentId = firstInstrument.Id
        };
        Segment firstSegment = new(project) { LengthTicks = 960 };
        firstSegment.Notes.Add(new LogicalNote(project)
        {
            StartTick = 0,
            LengthTicks = 240,
            Note = 60,
            Velocity = 100
        });
        firstTrack.Segments.Add(firstSegment);
        project.Tracks.Add(firstTrack);

        EventInstrument restoredInstrument = new(project)
        {
            Name = "Restored",
            RootNote = 60,
            TemplateLengthTicks = 480,
            OverlapPolicy = OverlapPolicy.Warn
        };
        SubVoice restoredVoice = new(project);
        restoredVoice.InitialState.Program = 42;
        restoredVoice.Events.Add(TemplateEvent.Note(project, 0, 480, 60, 100));
        restoredInstrument.SubVoices.Add(restoredVoice);
        project.EventInstruments.Add(restoredInstrument);
        LogicalTrack restoredTrack = new(project)
        {
            Name = "Restored",
            EventInstrumentId = restoredInstrument.Id
        };
        Segment restoredSegment = new(project) { LengthTicks = 960 };
        restoredSegment.Notes.Add(new LogicalNote(project)
        {
            StartTick = 120,
            LengthTicks = 480,
            Note = 60,
            Velocity = 100
        });
        restoredTrack.Segments.Add(restoredSegment);
        project.Tracks.Add(restoredTrack);

        return (project, restoredTrack);
    }

    private sealed class FakeBackend : IRealtimePlaybackBackend
    {
        public int ActualSampleRate { get; init; } = 48_000;
        public long PositionFrames { get; set; }
        public long RenderPositionFrames => PositionFrames;
        public bool IsBuffering => false;
        public bool IsCompleted { get; set; }
        public bool IsFaulted { get; set; }
        public string? FaultDescription { get; set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int ResetCount { get; private set; }
        public int PrepareCount { get; private set; }
        public bool ThrowMonitoringCommands { get; set; }
        public int MonitoringApplyCount { get; private set; }
        public bool ThrowPrepare { get; set; }
        public bool ThrowStop { get; set; }
        public bool ThrowReset { get; set; }
        public MidiRenderPlan? LastStartedPlan { get; private set; }
        public List<MidiMonitoringCommand> MonitoringCommands { get; } = [];
        public int Prepare()
        {
            PrepareCount++;
            if (ThrowPrepare)
            {
                throw new InvalidOperationException("Injected prepare failure.");
            }
            return ActualSampleRate;
        }
        public void Start(MidiRenderPlan plan, string soundFontPath, PlaybackMasterConfiguration master)
        {
            Assert.Equal(ActualSampleRate, plan.SampleRate);
            Assert.True(File.Exists(soundFontPath));
            Assert.Equal(-0.1f, master.VolumeDecibels);
            StartCount++;
            LastStartedPlan = plan;
            PositionFrames = 0;
            IsCompleted = false;
        }
        public void Stop(bool flush)
        {
            Assert.True(flush);
            StopCount++;
            if (ThrowStop)
            {
                throw new InvalidOperationException("Injected stop failure.");
            }
        }
        public void ApplyMonitoringCommands(ReadOnlySpan<MidiMonitoringCommand> commands)
        {
            MonitoringApplyCount++;
            if (ThrowMonitoringCommands)
            {
                throw new InvalidOperationException("Injected monitoring failure.");
            }
            MonitoringCommands.AddRange(commands);
        }
        public void Reset()
        {
            ResetCount++;
            if (ThrowReset)
            {
                throw new InvalidOperationException("Injected reset failure.");
            }
            PositionFrames = 0;
        }
        public void Dispose()
        {
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 8, 6, 0, 0, 0, TimeSpan.Zero);
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan amount)
        {
            if (amount < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(amount));
            }
            _utcNow += amount;
            _timestamp = checked(_timestamp + amount.Ticks);
        }
    }
}
