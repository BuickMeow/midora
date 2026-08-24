using System.Runtime.Versioning;
using Midora.Midi;

namespace Midora.Audio.Bass.Tests;

[SupportedOSPlatform("windows")]
public sealed class PersistentBassMidiAudioWorkerHostIntegrationTests
{
    private const int TestSampleRate = 48_000;

    [Fact]
    public void MappedSfzConfigurationSurvivesPersistentFormalPlaybackPreparation()
    {
        string nativeDirectory = NativeAudioIntegrationEnvironment.RequireNativeDirectory();
        string? configuredFormalWorker = Environment.GetEnvironmentVariable(
            "MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER");
        bool useManagedWorker = string.IsNullOrWhiteSpace(configuredFormalWorker);
        string workerPath = useManagedWorker
            ? NativeAudioIntegrationEnvironment.RequireManagedWorkerPath()
            : Path.GetFullPath(configuredFormalWorker!);
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"midora-persistent-sfz-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string samplePath = Path.Combine(directory, "tone.wav");
            string sfzPath = Path.Combine(directory, "tone.sfz");
            WriteTestWave(samplePath);
            File.WriteAllText(
                sfzPath,
                "<region> sample=tone.wav key=60 pitch_keycenter=60 ampeg_release=0.01");
            using PersistentBassMidiAudioWorkerHost host = new(
                workerPath,
                nativeDirectory,
                [new SoundFontConfiguration(sfzPath, new(11, 22, 33))],
                TimeSpan.FromSeconds(30),
                allowManagedTestWorker: useManagedWorker);
            BassMidiAudioWorkerProbeResult probe = host.Probe(
                deviceId: null,
                deviceBufferRequestMilliseconds: 50);
            MidiRenderPlan plan = new(
                probe.ActualSampleRate,
                totalFrameCount: Math.Max(1, probe.ActualSampleRate / 100),
                ports: []);

            using PersistentBassMidiAudioWorkerSession session = new(
                host,
                plan,
                new string('a', 64),
                new BassMidiRendererSettings(
                    BassMidiPolyphonyConfiguration.DefaultMaximumSampleVoicesPerUnitStream,
                    InitialReleaseAudioRuntimePolicy.WorkFrameCount),
                AudioMasterSettings.LimiterV2,
                renderAheadMilliseconds: 100,
                deviceBufferRequestMilliseconds: 50,
                deviceId: null,
                preparingTimeout: TimeSpan.FromSeconds(30),
                audioCache: null,
                bufferingRecoverySpoolPath: null,
                bufferingRecoveryMemoryFrameCapacity: 0,
                playbackSpanCacheEnabled: false);
            session.Stop(flush: true, TimeSpan.FromSeconds(30));

            Assert.Equal(0, session.ExitCode);
            Assert.Equal(0, session.Status.FaultCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

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
                AudioMasterSettings.LimiterV2,
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

    [Fact]
    public void PagedPersistentPlaybackResumesAfterSourceIsReenabled()
    {
        string soundFontPath = NativeAudioIntegrationEnvironment.RequireSoundFontPath();
        string nativeDirectory = NativeAudioIntegrationEnvironment.RequireNativeDirectory();
        string? configuredFormalWorker = Environment.GetEnvironmentVariable(
            "MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER");
        bool useManagedWorker = string.IsNullOrWhiteSpace(configuredFormalWorker);
        string workerPath = useManagedWorker
            ? NativeAudioIntegrationEnvironment.RequireManagedWorkerPath()
            : Path.GetFullPath(configuredFormalWorker!);
        using PersistentBassMidiAudioWorkerHost host = new(
            workerPath,
            nativeDirectory,
            soundFontPath,
            TimeSpan.FromSeconds(30),
            allowManagedTestWorker: useManagedWorker);
        BassMidiAudioWorkerProbeResult probe = host.Probe(
            deviceId: null,
            deviceBufferRequestMilliseconds: 50);
        long totalFrameCount = checked((long)probe.ActualSampleRate * 8);
        MidiRenderPlan plan = new(
            probe.ActualSampleRate,
            totalFrameCount,
            [],
            sourceIds: [1],
            unitDescriptors: [new MidiRenderUnitDescriptor(0, 0)],
            eventPageProvider: new PeriodicControllerPageProvider(probe.ActualSampleRate),
            referencedPresetKeys: [0]);
        using PersistentBassMidiAudioWorkerSession session = new(
            host,
            plan,
            NativeAudioIntegrationEnvironment.RequireSoundFontSetCacheIdentity(soundFontPath),
            new BassMidiRendererSettings(
                BassMidiPolyphonyConfiguration.DefaultMaximumSampleVoicesPerUnitStream,
                InitialReleaseAudioRuntimePolicy.WorkFrameCount),
            AudioMasterSettings.LimiterV2,
            renderAheadMilliseconds: 100,
            deviceBufferRequestMilliseconds: 50,
            deviceId: null,
            preparingTimeout: TimeSpan.FromSeconds(30),
            audioCache: null,
            bufferingRecoverySpoolPath: null,
            bufferingRecoveryMemoryFrameCapacity: 0,
            playbackSpanCacheEnabled: false);

        AudioWorkerStatus initial = WaitForProgress(session, 0, TimeSpan.FromSeconds(3));
        session.EnqueueMonitoringCommands([MidiMonitoringCommand.DisableSource(0)]);
        AudioWorkerStatus muted = WaitForProgress(
            session,
            initial.PositionFrame,
            TimeSpan.FromSeconds(3));
        session.EnqueueMonitoringCommands([MidiMonitoringCommand.EnableSource(0)]);
        AudioWorkerStatus resumed = WaitForProgress(
            session,
            muted.PositionFrame,
            TimeSpan.FromSeconds(3));
        Thread.Sleep(250);
        AudioWorkerStatus sustained = session.Status;

        Assert.Equal(0, resumed.FaultCode);
        Assert.Equal(AudioWorkerState.Playing, sustained.State);
        Assert.True(
            sustained.PositionFrame > resumed.PositionFrame,
            $"Paged playback resumed only transiently; resumed={resumed.PositionFrame}; "
                + $"after={sustained.PositionFrame}; render={sustained.RenderPositionFrame}; "
                + $"state={sustained.State}; stderr={session.StandardError}.");
        session.Stop(flush: true, TimeSpan.FromSeconds(30));
        Assert.Equal(AudioWorkerState.Stopped, session.Status.State);
    }

    private static AudioWorkerStatus WaitForProgress(
        PersistentBassMidiAudioWorkerSession session,
        long positionBefore,
        TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + checked((long)timeout.TotalMilliseconds);
        AudioWorkerStatus status = session.Status;
        while (status.PositionFrame <= positionBefore
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
        Assert.True(
            status.PositionFrame > positionBefore,
            $"Paged playback did not advance; before={positionBefore}; "
                + $"after={status.PositionFrame}; render={status.RenderPositionFrame}; "
                + $"state={status.State}; stderr={session.StandardError}.");
        return status;
    }

    private static void WriteTestWave(string path)
    {
        const int channelCount = 1;
        const int bitsPerSample = 16;
        const int sampleCount = 4_800;
        const int byteRate = TestSampleRate * channelCount * bitsPerSample / 8;
        const int blockAlign = channelCount * bitsPerSample / 8;
        const int dataLength = sampleCount * blockAlign;
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using BinaryWriter writer = new(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((ushort)1);
        writer.Write((ushort)channelCount);
        writer.Write(TestSampleRate);
        writer.Write(byteRate);
        writer.Write((ushort)blockAlign);
        writer.Write((ushort)bitsPerSample);
        writer.Write("data"u8);
        writer.Write(dataLength);
        for (int index = 0; index < sampleCount; index++)
        {
            double phase = (2d * Math.PI * 440d * index) / TestSampleRate;
            writer.Write((short)Math.Round(Math.Sin(phase) * 16_000d));
        }
    }

    private sealed class PeriodicControllerPageProvider(int sampleRate)
        : IMidiRenderEventPageProvider
    {
        private readonly long _stepFrames = Math.Max(1, sampleRate / 100);

        public IEnumerable<ScheduledPortMidiMessage> Query(
            long startFrame,
            long endFrame,
            CancellationToken cancellationToken = default)
        {
            long first = checked(((startFrame + _stepFrames - 1) / _stepFrames) * _stepFrames);
            for (long frame = first; frame < endFrame; frame += _stepFrames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new(
                    0,
                    new(
                        frame,
                        MidiMessage.ControlChange(0, 7, 100),
                        0));
            }
        }
    }
}
