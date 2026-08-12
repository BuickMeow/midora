using System.ComponentModel;
using System.Runtime.Versioning;
using Midora.Audio;
using Midora.Domain;
using Midora.Midi;
using Midora.Playback;
using Midora.Playback.BassWasapi;
using Xunit.Sdk;

namespace Midora.Audio.Bass.Tests;

[SupportedOSPlatform("windows")]
public sealed class BassMidiAudioWorkerSessionPolicyTests
{
    [Fact]
    public void ManagedRealtimeWorkerPausesAppliesHeldPreviewGenerationAndResumes()
    {
        VerifyHeldPreviewWorkerRoundTrip(
            NativeAudioIntegrationEnvironment.RequireManagedWorkerPath(),
            allowManagedTestWorker: true);
    }

    [Fact]
    public void NativeAotRealtimeWorkerPausesAppliesHeldPreviewGenerationAndResumes()
    {
        string? configured = Environment.GetEnvironmentVariable(
            "MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER");
        if (string.IsNullOrWhiteSpace(configured) || !File.Exists(configured))
        {
            throw SkipException.ForSkip(
                "Native AOT held-preview integration requires MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER.");
        }
        VerifyHeldPreviewWorkerRoundTrip(
            Path.GetFullPath(configured),
            allowManagedTestWorker: false);
    }

    [Fact]
    public void NativeAotRealtimeWorkerOpensTransferredRecoverySpool()
    {
        string? configured = Environment.GetEnvironmentVariable(
            "MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER");
        if (string.IsNullOrWhiteSpace(configured) || !File.Exists(configured))
        {
            throw SkipException.ForSkip(
                "Native AOT recovery-spool integration requires MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER.");
        }

        string nativeDirectory = NativeAudioIntegrationEnvironment.RequireNativeDirectory();
        string soundFontPath = NativeAudioIntegrationEnvironment.RequireSoundFontPath();
        MidiRenderPlan plan = new(48_000, 480_000, []);
        string cacheRoot = Path.Combine(
            Path.GetTempPath(),
            $"midora-worker-recovery-{Guid.NewGuid():N}");
        try
        {
            using AudioCacheSessionStore cache = new(cacheRoot, 0);
            using AudioCacheSessionStore.AudioRecoverySpool spool =
                cache.CreateRecoverySpool(checked(plan.TotalFrameCount * 2L * sizeof(float)));
            spool.ReleaseFileHandleForExternalUse();

            using BassMidiAudioWorkerSession session = new(
                plan,
                soundFontPath,
                new BassMidiRendererSettings(500, 256),
                AudioMasterSettings.LimiterV1,
                renderAheadMilliseconds: 20,
                deviceBufferRequestMilliseconds: 50,
                deviceId: null,
                Path.GetFullPath(configured),
                nativeDirectory,
                preparingTimeout: TimeSpan.FromSeconds(30),
                allowManagedTestWorker: false,
                audioCache: null,
                bufferingRecoverySpoolPath: spool.Path,
                bufferingRecoveryMemoryFrameCapacity: plan.TotalFrameCount);

            Assert.True(
                session.Status.State is AudioWorkerState.Playing or AudioWorkerState.Buffering);
            Assert.True(File.Exists(spool.Path));
            session.Stop(flush: true, TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void NativeAotRealtimeWorkerConsumesAnAudibleNotePlanWithoutBuffering()
    {
        string? configured = Environment.GetEnvironmentVariable(
            "MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER");
        if (string.IsNullOrWhiteSpace(configured) || !File.Exists(configured))
        {
            throw SkipException.ForSkip(
                "Native AOT audible-note integration requires MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER.");
        }

        string nativeDirectory = NativeAudioIntegrationEnvironment.RequireNativeDirectory();
        string soundFontPath = NativeAudioIntegrationEnvironment.RequireSoundFontPath();
        MidiRenderPlan plan = new(
            48_000,
            480_000,
            [new MidiPortRenderPlan(
                0,
                [
                    new(0, MidiMessage.ProgramChange(0, 0)),
                    new(4_800, MidiMessage.NoteOn(0, 60, 100)),
                    new(240_000, MidiMessage.NoteOff(0, 60, 0))
                ])]);
        string cacheRoot = Path.Combine(
            Path.GetTempPath(),
            $"midora-worker-audible-{Guid.NewGuid():N}");
        try
        {
            using AudioCacheSessionStore cache = new(cacheRoot, 16 * 1024 * 1024);
            using AudioCacheSessionStore.AudioRecoverySpool recovery =
                cache.CreateRecoverySpool(checked(plan.TotalFrameCount * 2L * sizeof(float)));
            recovery.ReleaseFileHandleForExternalUse();
            using BassMidiAudioWorkerSession session = new(
                plan,
                soundFontPath,
                new BassMidiRendererSettings(500, 256),
                AudioMasterSettings.LimiterV1,
                renderAheadMilliseconds: 100,
                deviceBufferRequestMilliseconds: 50,
                deviceId: null,
                Path.GetFullPath(configured),
                nativeDirectory,
                preparingTimeout: TimeSpan.FromSeconds(30),
                allowManagedTestWorker: false,
                audioCache: new CacheAccess(cache),
                bufferingRecoverySpoolPath: recovery.Path,
                bufferingRecoveryMemoryFrameCapacity: plan.TotalFrameCount,
                playbackSpanCacheEnabled: true);

            long deadline = Environment.TickCount64 + 2_000;
            AudioWorkerStatus status = session.Status;
            while (status.PositionFrame < 48_000
                && status.State is AudioWorkerState.Playing or AudioWorkerState.Buffering
                && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(10);
                status = session.Status;
            }

            Assert.Equal(AudioWorkerState.Playing, status.State);
            Assert.True(status.PositionFrame >= 48_000, $"Position={status.PositionFrame}; render={status.RenderPositionFrame}.");
            Assert.Equal(0, status.UnderrunCount);
            session.Stop(flush: true, TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void NativeAotRealtimeWorkerAppliesRepeatedMonitoringAfterRollingProducerReachedEos()
    {
        string? configured = Environment.GetEnvironmentVariable(
            "MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER");
        if (string.IsNullOrWhiteSpace(configured) || !File.Exists(configured))
        {
            throw SkipException.ForSkip(
                "Native AOT monitoring integration requires MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER.");
        }

        string nativeDirectory = NativeAudioIntegrationEnvironment.RequireNativeDirectory();
        string soundFontPath = NativeAudioIntegrationEnvironment.RequireSoundFontPath();
        MidiRenderPlan plan = new(
            48_000,
            96_000,
            [new MidiPortRenderPlan(
                0,
                [
                    new(0, MidiMessage.ProgramChange(0, 0), 0),
                    new(0, MidiMessage.NoteOn(0, 60, 100), 0),
                    new(90_000, MidiMessage.NoteOff(0, 60, 0), 0)
                ])],
            sourceIds: [1]);
        using BassMidiAudioWorkerSession session = new(
            plan,
            soundFontPath,
            new BassMidiRendererSettings(500, 256),
            AudioMasterSettings.LimiterV1,
            renderAheadMilliseconds: 20,
            deviceBufferRequestMilliseconds: 50,
            deviceId: null,
            Path.GetFullPath(configured),
            nativeDirectory,
            preparingTimeout: TimeSpan.FromSeconds(30),
            allowManagedTestWorker: false,
            playbackSpanCacheEnabled: true);

        MidiMonitoringCommand[] commands = new MidiMonitoringCommand[16];
        for (int index = 0; index < commands.Length; index++)
        {
            commands[index] = (index & 1) == 0
                ? MidiMonitoringCommand.DisableSource(0)
                : MidiMonitoringCommand.EnableSource(0);
        }
        session.EnqueueMonitoringCommands(commands);

        long deadline = Environment.TickCount64 + 1_000;
        AudioWorkerStatus status = session.Status;
        while (status.RenderPositionFrame < 24_000
            && status.State is AudioWorkerState.Playing or AudioWorkerState.Buffering
            && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(5);
            status = session.Status;
        }
        Assert.Contains(
            status.State,
            new[] { AudioWorkerState.Playing, AudioWorkerState.Buffering });
        Assert.Equal(0, status.FaultCode);
        session.Stop(flush: true, TimeSpan.FromSeconds(5));
        AudioWorkerStatus stopped = session.Status;
        Assert.Equal(AudioWorkerState.Stopped, stopped.State);
        Assert.Equal(0, stopped.CallbackAllocatedBytes);
        Assert.Equal(0, stopped.RenderingAllocatedBytes);
    }

    [Fact]
    public void NativeAotDesktopPlaybackPipelineCompilesAndConsumesProjectNotes()
    {
        string? configured = Environment.GetEnvironmentVariable(
            "MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER");
        if (string.IsNullOrWhiteSpace(configured) || !File.Exists(configured))
        {
            throw SkipException.ForSkip(
                "Native AOT desktop-pipeline integration requires MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER.");
        }

        string nativeDirectory = NativeAudioIntegrationEnvironment.RequireNativeDirectory();
        string soundFontPath = NativeAudioIntegrationEnvironment.RequireSoundFontPath();
        string cacheRoot = Path.Combine(
            Path.GetTempPath(),
            $"midora-desktop-playback-{Guid.NewGuid():N}");
        try
        {
            MidoraProject project = CreateAudibleProject();
            using ProjectCompilationSession compilation = new(project, soundFontPath);
            _ = compilation.ConfigureAudioCache(cacheRoot, 16 * 1024 * 1024);
            using BassWasapiChildPlaybackBackend backend = new(new(
                Path.GetFullPath(configured),
                nativeDirectory,
                DeviceId: null,
                RenderAheadMilliseconds: 100,
                DeviceBufferRequestMilliseconds: 50,
                new BassMidiRendererSettings(500, 256),
                AudioMasterSettings.LimiterV1,
                TimeSpan.FromSeconds(30)));
            using PlaybackController controller = new(compilation, backend);

            controller.Start();
            long deadline = Environment.TickCount64 + 2_000;
            while (controller.CurrentTick < 240
                && controller.State is PlaybackState.Playing or PlaybackState.Buffering
                && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(10);
                controller.Update();
            }

            Assert.Equal(PlaybackState.Playing, controller.State);
            Assert.True(controller.CurrentTick >= 240, $"Tick={controller.CurrentTick}; position={backend.PositionFrames}; render={backend.RenderPositionFrames}.");
            Assert.Equal(0, backend.UnderrunCount);
            controller.Stop();
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void NativeAotDesktopPlaybackCompletesTwiceWithPlaybackSpanCache()
    {
        string? configured = Environment.GetEnvironmentVariable(
            "MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER");
        if (string.IsNullOrWhiteSpace(configured) || !File.Exists(configured))
        {
            throw SkipException.ForSkip(
                "Native AOT repeated-playback integration requires MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER.");
        }

        string nativeDirectory = NativeAudioIntegrationEnvironment.RequireNativeDirectory();
        string soundFontPath = NativeAudioIntegrationEnvironment.RequireSoundFontPath();
        string cacheRoot = Path.Combine(
            Path.GetTempPath(),
            $"midora-repeated-playback-{Guid.NewGuid():N}");
        try
        {
            MidoraProject project = CreateAudibleProject();
            using ProjectCompilationSession compilation = new(project, soundFontPath);
            _ = compilation.ConfigureAudioCache(cacheRoot, 16 * 1024 * 1024);
            using BassWasapiChildPlaybackBackend backend = new(new(
                Path.GetFullPath(configured),
                nativeDirectory,
                DeviceId: null,
                RenderAheadMilliseconds: 100,
                DeviceBufferRequestMilliseconds: 50,
                new BassMidiRendererSettings(500, 256),
                AudioMasterSettings.LimiterV1,
                TimeSpan.FromSeconds(30)));
            using PlaybackController controller = new(compilation, backend);

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                controller.Start();
                long deadline = Environment.TickCount64 + 5_000;
                while (controller.State is PlaybackState.Playing or PlaybackState.Buffering
                    && Environment.TickCount64 < deadline)
                {
                    Thread.Sleep(2);
                    controller.Update();
                }

                Assert.True(
                    controller.State == PlaybackState.Stopped,
                    $"Attempt={attempt}; state={controller.State}; error={controller.LastError}; "
                    + $"workerStateFault={backend.FaultDescription}");
                Assert.Null(controller.LastError);
            }
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, recursive: true);
            }
        }
    }

    private static void VerifyHeldPreviewWorkerRoundTrip(
        string workerPath,
        bool allowManagedTestWorker)
    {
        string nativeDirectory = NativeAudioIntegrationEnvironment.RequireNativeDirectory();
        string soundFontPath = NativeAudioIntegrationEnvironment.RequireSoundFontPath();
        MidiRenderPlan initial = new(48_000, 480_000, []);
        using BassMidiAudioWorkerSession session = new(
            initial,
            soundFontPath,
            new BassMidiRendererSettings(750, 256),
            AudioMasterSettings.LimiterV1,
            renderAheadMilliseconds: 20,
            deviceBufferRequestMilliseconds: 50,
            deviceId: null,
            workerPath,
            nativeDirectory,
            preparingTimeout: TimeSpan.FromSeconds(30),
            allowManagedTestWorker);

        long frontier = session.PauseHeldPreviewAtProducerFrontier(TimeSpan.FromSeconds(5));
        Assert.True(frontier > 0);
        Assert.Equal(AudioWorkerState.HeldPreviewPaused, session.Status.State);
        MidiRenderPlan replacement = new(48_000, Math.Max(576_000, frontier + 48_000), []);

        session.ReplaceHeldPreviewFutureAndResume(
            replacement,
            frontier,
            TimeSpan.FromSeconds(5));

        AudioWorkerStatus status = session.Status;
        Assert.Equal(1, status.HeldPreviewPlanGeneration);
        Assert.NotEqual(AudioWorkerState.HeldPreviewPaused, status.State);
        session.Stop(flush: true, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void StartupFailureReleasesOwnedPlanDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"midora-realtime-worker-startup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string nativeWorker = Path.Combine(directory, "Midora.Audio.Bass.Worker.exe");
        string soundFont = Path.Combine(directory, "project.sf2");
        File.WriteAllBytes(nativeWorker, [0]);
        File.WriteAllBytes(soundFont, [0]);
        HashSet<string> before = EnumerateOwnedPlanDirectories();
        try
        {
            _ = Assert.Throws<Win32Exception>(() => new BassMidiAudioWorkerSession(
                CreatePlan(),
                soundFont,
                new BassMidiRendererSettings(750, 256),
                AudioMasterSettings.LimiterV1,
                100,
                50,
                null,
                nativeWorker,
                directory,
                TimeSpan.FromSeconds(1)));

            Assert.Empty(EnumerateOwnedPlanDirectories().Except(before));
        }
        finally
        {
            foreach (string residual in EnumerateOwnedPlanDirectories().Except(before))
            {
                Directory.Delete(residual, recursive: true);
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MissingSoundFontFailsBeforeCreatingOwnedPlanDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"midora-realtime-worker-startup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string nativeWorker = Path.Combine(directory, "Midora.Audio.Bass.Worker.exe");
        File.WriteAllBytes(nativeWorker, [0]);
        HashSet<string> before = EnumerateOwnedPlanDirectories();
        try
        {
            _ = Assert.Throws<FileNotFoundException>(() => new BassMidiAudioWorkerSession(
                CreatePlan(),
                Path.Combine(directory, "missing.sf2"),
                new BassMidiRendererSettings(750, 256),
                AudioMasterSettings.LimiterV1,
                100,
                50,
                null,
                nativeWorker,
                directory,
                TimeSpan.FromSeconds(1)));

            Assert.True(before.SetEquals(EnumerateOwnedPlanDirectories()));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FormalRealtimeClientAcceptsOnlyNativeExecutableWorker()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"midora-realtime-worker-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string nativeWorker = Path.Combine(directory, "Midora.Audio.Bass.Worker.exe");
        string managedWorker = Path.Combine(directory, "Midora.Audio.Bass.Worker.dll");
        string otherWorker = Path.Combine(directory, "Midora.Audio.Bass.Worker.bin");
        File.WriteAllBytes(nativeWorker, [0]);
        File.WriteAllBytes(managedWorker, [0]);
        File.WriteAllBytes(otherWorker, [0]);
        try
        {
            BassMidiAudioWorkerSession.ValidateWorkerLaunchPath(
                nativeWorker,
                allowManagedTestWorker: false);
            Assert.Throws<InvalidDataException>(() =>
                BassMidiAudioWorkerSession.ValidateWorkerLaunchPath(
                    managedWorker,
                    allowManagedTestWorker: false));
            BassMidiAudioWorkerSession.ValidateWorkerLaunchPath(
                managedWorker,
                allowManagedTestWorker: true);
            Assert.Throws<InvalidDataException>(() =>
                BassMidiAudioWorkerSession.ValidateWorkerLaunchPath(
                    otherWorker,
                    allowManagedTestWorker: true));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(AudioWorkerState.Stopped, 0, true)]
    [InlineData(AudioWorkerState.Completed, 0, true)]
    [InlineData(AudioWorkerState.OutputDeviceUnavailable, 0, true)]
    [InlineData(AudioWorkerState.Playing, 0, false)]
    [InlineData(AudioWorkerState.Stopped, 1, false)]
    [InlineData(AudioWorkerState.OutputDeviceUnavailable, 1, false)]
    [InlineData(AudioWorkerState.Faulted, 1, false)]
    public void TerminalExitRequiresSuccessCodeAndTerminalState(
        AudioWorkerState state,
        int exitCode,
        bool expected)
    {
        Assert.Equal(
            expected,
            BassMidiAudioWorkerSession.IsSuccessfulTerminalExit(state, exitCode));
    }

    private static MidiRenderPlan CreatePlan() => new(48_000, 0, []);

    private static MidoraProject CreateAudibleProject()
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
        LogicalTrack track = new(project)
        {
            Name = "Track",
            EventInstrumentId = instrument.Id
        };
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

    private sealed class CacheAccess(AudioCacheSessionStore store) : IAudioPcmCacheSessionAccess
    {
        public AudioCacheSessionSnapshot? AudioCacheSnapshot => store.GetSnapshot();

        public bool TryCopyReusableAudio(
            string key,
            Stream destination,
            out long payloadLength) => store.TryCopyReusable(key, destination, out payloadLength);

        public AudioCachePublishResult PublishReusableAudio(
            string key,
            Stream source,
            long payloadLength) => store.PublishReusable(key, source, payloadLength);

        public void InvalidateReusableAudio(string key) => store.InvalidateReusable(key);

        public AudioCacheSessionStore.AudioRecoverySpool CreateTransientAudioSpool(
            long lengthBytes) => store.CreateRecoverySpool(lengthBytes);

        public void DisableReusableAudioRetention(string reason) =>
            store.DisableReusableRetention(reason);
    }

    private static HashSet<string> EnumerateOwnedPlanDirectories() =>
        Directory.EnumerateDirectories(
            Path.GetTempPath(),
            "midora-audio-worker-*")
        .Select(Path.GetFullPath)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
