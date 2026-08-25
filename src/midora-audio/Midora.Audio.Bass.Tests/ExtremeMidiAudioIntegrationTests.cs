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
            using ProjectCompilationSession compilationSession = new(imported.Project);
            CanonicalCompiledResult compiled = compilationSession.CompileForPlayback(0, null);
            Assert.True(compiled.IsConsumable, string.Join(Environment.NewLine, compiled.Diagnostics));
            string[] audibleTrackNames = (Environment.GetEnvironmentVariable(
                    "MIDORA_AUDIO_SAMPLE_AUDIBLE_TRACK_NAMES") ?? string.Empty)
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            HashSet<Midora.Domain.MidoraId>? audibleTrackIds = audibleTrackNames.Length == 0
                ? null
                : imported.Project.PureMidiTracks
                    .Where(track => audibleTrackNames.Contains(track.Name, StringComparer.Ordinal))
                    .Select(track => track.Id)
                    .ToHashSet();
            if (audibleTrackIds is not null)
                Assert.Equal(audibleTrackNames.Length, audibleTrackIds.Count);
            HashSet<Midora.Domain.MidoraId> demandedTracks = audibleTrackIds
                ?? imported.Project.Tracks.Select(track => track.Id)
                    .Concat(imported.Project.PureMidiTracks.Select(track => track.Id))
                    .ToHashSet();
            MidiRenderPlan sourcePlan = compilationSession.GetOrCreateRealtimeRenderPlan(
                compiled,
                48_000,
                demandedTracks);
            using AudioCacheSessionStore? cacheStore = Environment.GetEnvironmentVariable(
                    "MIDORA_AUDIO_SAMPLE_USE_SEGMENT_CACHE") == "1"
                ? new AudioCacheSessionStore(Path.Combine(directory, "cache"), 4L * 1024 * 1024 * 1024)
                : null;
            CacheAccess? cacheAccess = cacheStore is null ? null : new(cacheStore);
            using AudioSegmentCacheStaging? cacheStaging = cacheAccess is null
                ? null
                : AudioSegmentCacheStaging.Create(
                    sourcePlan,
                    cacheAccess,
                    NativeAudioIntegrationEnvironment.RequireSoundFontSetCacheIdentity(soundFontPath),
                    NativeAudioIntegrationEnvironment.RequireNativeDirectory(),
                    BassMidiPolyphonyConfiguration.DefaultMaximumSampleVoicesPerUnitStream,
                    directory);
            MidiRenderPlan stagedPlan = cacheStaging?.Plan ?? sourcePlan;
            using MidiRenderEventStreamProducer producer = Assert.IsType<MidiRenderEventStreamProducer>(
                MidiRenderEventStreamProducer.Create(stagedPlan, directory));
            MidiRenderPlan transportPlan = stagedPlan.WithEventStreamDescriptor(producer.Descriptor);
            string planPath = Path.Combine(directory, "compiled-audio-plan.mdap");
            MidiRenderPlanFile.Write(planPath, transportPlan);
            MidiRenderPlan plan = MidiRenderPlanFile.Read(planPath);
            BassMidiRendererSettings settings = new(
                BassMidiPolyphonyConfiguration.DefaultMaximumSampleVoicesPerUnitStream,
                maximumWorkFrameCount: 256);
            SoundFontConfiguration[] soundFonts = [new(soundFontPath, null)];
            using PersistentBassMidiSoundFont persistentSoundFont = new(soundFonts);
            using BassMidiRenderer renderer = new(
                plan,
                soundFonts,
                settings,
                AudioMasterSettings.LimiterV2,
                cacheStaging?.FilePath,
                segmentProducerConcurrency: 4,
                cacheReadManifestPath: cacheStaging?.ReadManifestPath,
                persistentSoundFont: persistentSoundFont);

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
            long audibleStartFrame = long.TryParse(
                    Environment.GetEnvironmentVariable("MIDORA_AUDIO_SAMPLE_AUDIBLE_START_TICK"),
                    out long audibleStartTick)
                ? new TempoSampleMap(compiled.TicksPerQuarterNote, compiled.Tempos)
                    .TickToSampleFrame(audibleStartTick, compiled.StartTick, 48_000)
                : 0;
            float peak = 0;
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
                    long blockStart = completed;
                    completed += result.FrameCount;
                    if (completed > audibleStartFrame)
                    {
                        int firstSample = checked((int)Math.Max(
                            0,
                            (audibleStartFrame - blockStart) * 2));
                        for (int index = firstSample; index < result.FrameCount * 2; index++)
                            peak = Math.Max(peak, Math.Abs(block[index]));
                    }
                    if (result.FrameCount == 0) Thread.Yield();
                }
            }

            output.WriteLine(
                $"sample={path}; completed={completed}; requestedEnd={requestedEnd}; peak={peak}; fault={renderer.Fault}");
            Assert.True(completed == requestedEnd, $"The sample stopped at frame {completed}: {renderer.Fault}");
            Assert.Equal(AudioRenderFaultCode.None, renderer.Fault.Code);
            if (audibleTrackIds is not null)
                Assert.True(peak > 0.00001f, $"The selected sample Tracks produced no audible PCM; peak={peak}.");
        }
        finally
        {
            imported.Project.Dispose();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class CacheAccess(AudioCacheSessionStore store) : IAudioPcmCacheSessionAccess
    {
        public AudioCacheSessionSnapshot? AudioCacheSnapshot => store.GetSnapshot();
        public bool SupportsReusableAudioPackJournals => store.SupportsReusableAudioPackJournals;
        public bool TryCopyReusableAudio(string key, Stream destination, out long payloadLength) =>
            store.TryCopyReusable(key, destination, out payloadLength);
        public ReusableAudioReadLease? AcquireReusableAudioReadLease(IReadOnlyList<string> keys) =>
            store.AcquireReusableReadLease(keys);
        public AudioCachePublishResult PublishReusableAudio(
            string key,
            Stream source,
            long payloadLength) => store.PublishReusable(key, source, payloadLength);
        public void InvalidateReusableAudio(string key) => store.InvalidateReusable(key);
        public AudioCacheSessionStore.AudioRecoverySpool CreateTransientAudioSpool(long lengthBytes) =>
            store.CreateRecoverySpool(lengthBytes);
        public AudioCacheSessionStore.AudioRecoverySpool CreateSparseTransientAudioSpool(long lengthBytes) =>
            store.CreateRecoverySpool(lengthBytes, sparse: true);
        public void AdoptReusableAudioPackJournals(
            AudioCacheSessionStore.AudioRecoverySpool spool,
            string journalDirectory,
            IReadOnlyCollection<string> completedKeys) =>
            store.AdoptReusableAudioPackJournals(spool, journalDirectory, completedKeys);
        public void DisableReusableAudioRetention(string reason) =>
            store.DisableReusableRetention(reason);
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
