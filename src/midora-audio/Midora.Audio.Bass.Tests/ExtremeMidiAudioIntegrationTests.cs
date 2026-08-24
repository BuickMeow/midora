using Midora.Application;
using Midora.AudioDevice;
using Midora.Compiler;
using Midora.Midi;
using Midora.Playback;
using Xunit.Abstractions;

namespace Midora.Audio.Bass.Tests;

public sealed class ExtremeMidiAudioIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public unsafe void StreamingPlanHonorsBoundaryRevealedAfterCurrentFrameEventsAreConsumed()
    {
        NativeAudioIntegrationEnvironment.LoadBassMidi();
        string soundFontPath = NativeAudioIntegrationEnvironment.RequireSoundFontPath();
        string directory = Path.Combine(
            Path.GetTempPath(),
            "midora-stream-boundary-audio-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        MidiRenderPlan sourcePlan = new(
            sampleRate: 48_000,
            totalFrameCount: 512,
            ports: [],
            unitDescriptors: [new MidiRenderUnitDescriptor(0, 0)],
            eventPageProvider: new FixedEventPageProvider(
            [
                new(0, new(0, MidiMessage.ProgramChange(0, 0))),
                new(0, new(1, MidiMessage.NoteOn(0, 60, 100))),
                new(0, new(200, MidiMessage.NoteOff(0, 60, 0)))
            ]),
            referencedPresetKeys: [0]);
        try
        {
            using MidiRenderEventStreamProducer producer = Assert.IsType<MidiRenderEventStreamProducer>(
                MidiRenderEventStreamProducer.Create(sourcePlan, directory));
            MidiRenderPlan plan = sourcePlan.WithEventStreamDescriptor(producer.Descriptor);
            using BassMidiRenderer renderer = new(
                plan,
                soundFontPath,
                new BassMidiRendererSettings(
                    BassMidiPolyphonyConfiguration.DefaultMaximumSampleVoicesPerUnitStream,
                    maximumWorkFrameCount: 256),
                AudioMasterSettings.LimiterV2);
            float[] samples = new float[512 * 2];
            int completed = 0;
            long deadline = Environment.TickCount64 + 10_000;
            fixed (float* destination = samples)
            {
                while (completed < 512 && Environment.TickCount64 < deadline)
                {
                    AudioPullResult result = renderer.PullFrames(destination + completed * 2, 512 - completed);
                    if (result.Status == AudioPullStatus.Fault) break;
                    completed += result.FrameCount;
                    if (result.FrameCount == 0) Thread.Yield();
                }
            }

            Assert.Equal(512, completed);
            Assert.Equal(AudioRenderFaultCode.None, renderer.Fault.Code);
            Assert.Contains(samples, static value => value != 0);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public unsafe void OptInImportedSampleStreamsThroughRealBassWithoutFault()
    {
        string? path = Environment.GetEnvironmentVariable("MIDORA_AUDIO_SAMPLE_MIDI_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            output.WriteLine("Set MIDORA_AUDIO_SAMPLE_MIDI_PATH to run the opt-in imported-MIDI audio gate.");
            return;
        }

        path = Path.GetFullPath(path);
        Assert.True(File.Exists(path), $"Audio sample does not exist: {path}");
        NativeAudioIntegrationEnvironment.LoadBassMidi();
        string soundFontPath = NativeAudioIntegrationEnvironment.RequireSoundFontPath();
        string directory = Path.Combine(
            Path.GetTempPath(),
            "midora-extreme-midi-audio-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        MidiProjectImportResult imported = MidiProjectImportService.ImportFile(
            path,
            Path.GetFileNameWithoutExtension(path));
        try
        {
            using MidoraCompiler compiler = new();
            CanonicalCompiledResult compiled = compiler.CompileFull(imported.Project);
            Assert.True(compiled.IsConsumable, string.Join(Environment.NewLine, compiled.Diagnostics));
            MidiRenderPlan sourcePlan = MidiRenderPlanAdapter.CreateRealtime(compiled, 48_000);
            using MidiRenderEventStreamProducer producer = Assert.IsType<MidiRenderEventStreamProducer>(
                MidiRenderEventStreamProducer.Create(sourcePlan, directory));
            MidiRenderPlan plan = sourcePlan.WithEventStreamDescriptor(producer.Descriptor);
            BassMidiRendererSettings settings = new(
                BassMidiPolyphonyConfiguration.DefaultMaximumSampleVoicesPerUnitStream,
                maximumWorkFrameCount: 256);
            using BassMidiRenderer renderer = new(
                plan,
                soundFontPath,
                settings,
                AudioMasterSettings.LimiterV2,
                segmentProducerConcurrency: 4);

            long requestedEnd;
            if (long.TryParse(
                    Environment.GetEnvironmentVariable("MIDORA_AUDIO_SAMPLE_END_TICK"),
                    out long configuredEndTick))
            {
                TempoSampleMap map = new(compiled.TicksPerQuarterNote, compiled.Tempos);
                requestedEnd = Math.Clamp(
                    map.TickToSampleFrame(configuredEndTick, compiled.StartTick, 48_000),
                    1,
                    plan.TotalFrameCount);
            }
            else if (long.TryParse(
                    Environment.GetEnvironmentVariable("MIDORA_AUDIO_SAMPLE_END_FRAME"),
                    out long configuredEndFrame))
            {
                requestedEnd = Math.Clamp(configuredEndFrame, 1, plan.TotalFrameCount);
            }
            else
            {
                requestedEnd = Math.Min(plan.TotalFrameCount, 2 * 48_000L);
            }
            float[] block = new float[2_048 * 2];
            long completed = 0;
            int timeoutSeconds = int.TryParse(
                    Environment.GetEnvironmentVariable("MIDORA_AUDIO_SAMPLE_TIMEOUT_SECONDS"),
                    out int configuredTimeoutSeconds)
                ? Math.Clamp(configuredTimeoutSeconds, 1, 600)
                : 60;
            long deadline = Environment.TickCount64 + timeoutSeconds * 1_000L;
            fixed (float* destination = block)
            {
                while (completed < requestedEnd && Environment.TickCount64 < deadline)
                {
                    int request = (int)Math.Min(2_048, requestedEnd - completed);
                    AudioPullResult result = renderer.PullFrames(destination, request);
                    if (result.Status == AudioPullStatus.Fault) break;
                    completed += result.FrameCount;
                    if (result.FrameCount == 0) Thread.Yield();
                }
            }

            output.WriteLine(
                $"sample={path}; completed={completed}; requestedEnd={requestedEnd}; fault={renderer.Fault}");
            Assert.True(completed == requestedEnd, $"The sample stopped at frame {completed}: {renderer.Fault}");
            Assert.Equal(AudioRenderFaultCode.None, renderer.Fault.Code);
        }
        finally
        {
            imported.Project.Dispose();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class FixedEventPageProvider(
        IReadOnlyList<ScheduledPortMidiMessage> events) : IMidiRenderEventPageProvider
    {
        public IEnumerable<ScheduledPortMidiMessage> Query(
            long startFrame,
            long endFrame,
            CancellationToken cancellationToken = default)
        {
            foreach (ScheduledPortMidiMessage value in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (value.Scheduled.SampleFrame >= startFrame
                    && value.Scheduled.SampleFrame < endFrame)
                {
                    yield return value;
                }
            }
        }
    }
}
