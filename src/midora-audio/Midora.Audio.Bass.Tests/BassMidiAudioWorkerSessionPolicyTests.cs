using System.ComponentModel;
using System.Runtime.Versioning;
using Midora.Audio;
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

    private static HashSet<string> EnumerateOwnedPlanDirectories() =>
        Directory.EnumerateDirectories(
            Path.GetTempPath(),
            "midora-audio-worker-*")
        .Select(Path.GetFullPath)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
