using System.Runtime.Versioning;

namespace Midora.Audio.Bass.Tests;

[SupportedOSPlatform("windows")]
public sealed class PersistentBassMidiAudioWorkerHostIntegrationTests
{
    [Fact]
    public void OneWorkerProcessCompletesTwoFormalSilentPlaybackTasks()
    {
        string soundFontPath = NativeAudioIntegrationEnvironment.RequireSoundFontPath();
        string nativeDirectory = NativeAudioIntegrationEnvironment.RequireNativeDirectory();
        string? configuredFormalWorker = Environment.GetEnvironmentVariable(
            "MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER");
        bool useManagedWorker = string.IsNullOrWhiteSpace(configuredFormalWorker);
        string workerPath = useManagedWorker
            ? NativeAudioIntegrationEnvironment.RequireManagedWorkerPath()
            : Path.GetFullPath(configuredFormalWorker!);
        if (!File.Exists(workerPath))
        {
            throw new FileNotFoundException(
                "The configured persistent audio Worker does not exist.",
                workerPath);
        }
        using PersistentBassMidiAudioWorkerHost host = new(
            workerPath,
            nativeDirectory,
            soundFontPath,
            TimeSpan.FromSeconds(30),
            allowManagedTestWorker: useManagedWorker);
        int processId = host.ProcessId;

        BassMidiAudioWorkerProbeResult probe = host.Probe(
            deviceId: null,
            deviceBufferRequestMilliseconds: 50);
        Assert.True(probe.ActualSampleRate > 0);
        Assert.True(probe.ActualDeviceBufferFrameCount > 0);

        MidiRenderPlan plan = new(
            probe.ActualSampleRate,
            totalFrameCount: Math.Max(1, probe.ActualSampleRate / 100),
            ports: []);
        for (int iteration = 0; iteration < 2; iteration++)
        {
            using PersistentBassMidiAudioWorkerSession session = new(
                host,
                plan,
                new string('a', 64),
                new BassMidiRendererSettings(
                    BassMidiPolyphonyConfiguration.DefaultMaximumSampleVoicesPerUnitStream,
                    InitialReleaseAudioRuntimePolicy.WorkFrameCount),
                AudioMasterSettings.LimiterV1,
                renderAheadMilliseconds: 100,
                deviceBufferRequestMilliseconds: 50,
                deviceId: null,
                preparingTimeout: TimeSpan.FromSeconds(30),
                audioCache: null,
                bufferingRecoverySpoolPath: null,
                bufferingRecoveryMemoryFrameCapacity: 0,
                playbackSpanCacheEnabled: false);
            session.Stop(flush: true, TimeSpan.FromSeconds(30));

            Assert.Equal(processId, host.ProcessId);
            Assert.False(host.HasExited);
            Assert.Equal(0, session.ExitCode);
        }
    }
}
