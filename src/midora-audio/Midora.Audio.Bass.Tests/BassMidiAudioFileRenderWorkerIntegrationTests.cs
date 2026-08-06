using System.Runtime.Versioning;
using Midora.AudioDevice.Wave;
using Midora.Midi;

namespace Midora.Audio.Bass.Tests;

[SupportedOSPlatform("windows")]
public sealed class BassMidiAudioFileRenderWorkerIntegrationTests
{
    private static string SoundFontPath =>
        NativeAudioIntegrationEnvironment.RequireSoundFontPath();

    [Fact]
    public async Task WorkerHostRendersValidatedNonSilentWaveThroughFormalFileProtocol()
    {
        string managedWorkerPath = NativeAudioIntegrationEnvironment.RequireManagedWorkerPath();
        string? formalWorkerPath = Environment.GetEnvironmentVariable(
            "MIDORA_TEST_NATIVE_AOT_FILE_WORKER");
        NativeAudioIntegrationEnvironment.LoadBassMidi();
        string nativeDirectory = NativeAudioIntegrationEnvironment.RequireNativeDirectory();
        _ = SoundFontPath;
        string outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"midora-file-worker-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        string outputPath = Path.Combine(outputDirectory, "rendered.wav");
        try
        {
            BassMidiAudioFileRenderWorker worker = string.IsNullOrWhiteSpace(formalWorkerPath)
                ? new(
                    managedWorkerPath,
                    nativeDirectory,
                    TimeSpan.FromSeconds(30),
                    allowManagedTestWorker: true)
                : new(
                    formalWorkerPath,
                    nativeDirectory,
                    TimeSpan.FromSeconds(30));
            MidiRenderPlan plan = new(
                48_000,
                4_096,
                [new MidiPortRenderPlan(0,
                [
                    new ScheduledMidiMessage(0, MidiMessage.ProgramChange(0, 0)),
                    new ScheduledMidiMessage(0, MidiMessage.NoteOn(0, 60, 100)),
                    new ScheduledMidiMessage(2_048, MidiMessage.NoteOff(0, 60, 0))
                ])]);

            await worker.PrepareAsync(new(
                SoundFontPath,
                plan.SampleRate,
                BassMidiPolyphonyConfiguration.DefaultMaximumSampleVoiceCount,
                -0.1f));
            AudioFileRenderWorkerResult rendered = await worker.RenderAsync(new(
                plan,
                SoundFontPath,
                outputPath,
                BassMidiPolyphonyConfiguration.DefaultMaximumSampleVoiceCount,
                -0.1f));

            WaveFileSize size = WaveFileValidation.ValidateInitialReleaseFile(
                outputPath,
                plan.SampleRate,
                plan.TotalFrameCount);
            Assert.Equal(plan.TotalFrameCount, rendered.FrameCount);
            Assert.Equal(size.FileByteCount, rendered.FileByteCount);
            Assert.Equal(0, rendered.RenderingThreadAllocatedBytes);
            byte[] bytes = File.ReadAllBytes(outputPath);
            Assert.Contains(bytes.AsSpan(WaveFileSize.HeaderByteCount).ToArray(), value => value != 0);
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }
}
