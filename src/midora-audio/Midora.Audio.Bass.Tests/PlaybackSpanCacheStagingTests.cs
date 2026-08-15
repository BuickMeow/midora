using Midora.AudioDevice;
using Midora.Midi;

namespace Midora.Audio.Bass.Tests;

public sealed class PlaybackSpanCacheStagingTests
{
    private static readonly string SoundFontSha256 = new('a', 64);

    [Fact]
    public void CompleteMissPublishesAndSecondStageIsAnExactHit()
    {
        using TemporaryDirectory directory = new();
        TestInputs inputs = TestInputs.Create(directory.Path);
        using AudioCacheSessionStore store = new(inputs.CacheRoot, 4096);
        CacheAccess access = new(store);
        MidiRenderPlan plan = CreatePlan(port: 0, channel: 0);

        using (PlaybackSpanCacheStaging first = Assert.IsType<PlaybackSpanCacheStaging>(
            PlaybackSpanCacheStaging.Create(
                plan, access, SoundFontSha256, inputs.Native, 500, AudioMasterSettings.LimiterV1)))
        {
            Assert.False(first.Hit);
            using FileStream stream = new(
                first.FilePath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite);
            stream.Position = AudioPcmCachePayload.HeaderByteCount;
            using BinaryWriter writer = new(stream);
            writer.Write(0.1f);
            writer.Write(-0.1f);
            writer.Write(0.2f);
            writer.Write(-0.2f);
            writer.Flush();
            stream.Flush(flushToDisk: true);
            first.PublishCompleted(access, completedRenderFrame: 2, totalFrameCount: 2);
        }

        using PlaybackSpanCacheStaging second = Assert.IsType<PlaybackSpanCacheStaging>(
            PlaybackSpanCacheStaging.Create(
                plan, access, SoundFontSha256, inputs.Native, 500, AudioMasterSettings.LimiterV1));
        Assert.True(second.Hit);
        using FileStream cached = new(
            second.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        cached.Position = AudioPcmCachePayload.HeaderByteCount;
        using BinaryReader reader = new(cached);
        Assert.Equal([0.1f, -0.1f, 0.2f, -0.2f],
            new[] { reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() });
    }

    [Fact]
    public void IncompleteOrMonitoringInvalidatedCaptureIsNotPublished()
    {
        using TemporaryDirectory directory = new();
        TestInputs inputs = TestInputs.Create(directory.Path);
        using AudioCacheSessionStore store = new(inputs.CacheRoot, 4096);
        CacheAccess access = new(store);
        MidiRenderPlan plan = CreatePlan(0, 0);
        string incompleteKey;
        string invalidatedKey;

        using (PlaybackSpanCacheStaging incomplete = Assert.IsType<PlaybackSpanCacheStaging>(
            PlaybackSpanCacheStaging.Create(
                plan, access, SoundFontSha256, inputs.Native, 500, AudioMasterSettings.LimiterV1)))
        {
            incompleteKey = incomplete.Key;
            incomplete.PublishCompleted(access, completedRenderFrame: 1, totalFrameCount: 2);
        }
        Assert.False(ContainsReusable(store, incompleteKey));

        using (PlaybackSpanCacheStaging invalidated = Assert.IsType<PlaybackSpanCacheStaging>(
            PlaybackSpanCacheStaging.Create(
                plan, access, SoundFontSha256, inputs.Native, 501, AudioMasterSettings.LimiterV1)))
        {
            invalidatedKey = invalidated.Key;
            invalidated.InvalidateCapture();
            invalidated.PublishCompleted(access, completedRenderFrame: 2, totalFrameCount: 2);
        }
        Assert.False(ContainsReusable(store, invalidatedKey));
    }

    [Fact]
    public void KeyIsRouteIndependentButIncludesFinalOutputSettings()
    {
        MidiRenderPlan first = CreatePlan(port: 0, channel: 0);
        MidiRenderPlan rerouted = CreatePlan(port: 15, channel: 15);
        PlaybackSpanMasterSettings master = new(-0.1f, 1f, 50f, true);
        string firstKey = PlaybackSpanCacheKey.Create(
            first, new string('a', 64), "native", 500, master);

        Assert.Equal(firstKey, PlaybackSpanCacheKey.Create(
            rerouted, new string('a', 64), "native", 500, master));
        Assert.NotEqual(firstKey, PlaybackSpanCacheKey.Create(
            first, new string('a', 64), "native", 501, master));
        Assert.NotEqual(firstKey, PlaybackSpanCacheKey.Create(
            first, new string('a', 64), "native", 500, master with { VolumeDecibels = -3f }));
    }

    [Fact]
    public void LongPlaybackSpanDoesNotMaterializeTheWholeCacheBeforeRollingStartup()
    {
        using TemporaryDirectory directory = new();
        TestInputs inputs = TestInputs.Create(directory.Path);
        using AudioCacheSessionStore store = new(inputs.CacheRoot, 64 * 1024 * 1024);
        CacheAccess access = new(store);
        MidiRenderPlan plan = new(
            sampleRate: 1_000,
            totalFrameCount: 60_000,
            ports: [],
            sourceIds: [101]);

        Assert.Null(PlaybackSpanCacheStaging.Create(
            plan,
            access,
            SoundFontSha256,
            inputs.Native,
            500,
            AudioMasterSettings.LimiterV1));
        Assert.Equal(0, store.GetSnapshot().TransientBytes);
    }

    private static MidiRenderPlan CreatePlan(byte port, byte channel)
    {
        const long sourceId = 101;
        MidiPortRenderPlan portPlan = new(port,
        [
            new ScheduledMidiMessage(0, MidiMessage.NoteOn(channel, 60, 100), 0),
            new ScheduledMidiMessage(2, MidiMessage.NoteOff(channel, 60, 0), 0)
        ]);
        MidiUnitFragmentRenderPlan fragment = new(
            port,
            channel,
            trackId: 102,
            segmentId: 103,
            eventInstrumentId: 104,
            instanceGroupId: 105,
            subVoiceId: 106,
            sourceIndex: 0,
            startFrame: 0,
            endFrame: 2,
            semanticFingerprint: new string('c', 64),
            [
                new ScheduledMidiMessage(0, MidiMessage.NoteOn(0, 60, 100), 0),
                new ScheduledMidiMessage(2, MidiMessage.NoteOff(0, 60, 0), 0)
            ]);
        return new MidiRenderPlan(48_000, 2, [portPlan], [sourceId], [], [fragment]);
    }

    private readonly record struct TestInputs(
        string CacheRoot,
        string SoundFont,
        string Native)
    {
        public static TestInputs Create(string root)
        {
            string cache = Path.Combine(root, "cache");
            string soundFont = WriteFile(root, "project.sf2", [1, 2, 3]);
            string native = Path.Combine(root, "native");
            Directory.CreateDirectory(native);
            _ = WriteFile(native, "bass.dll", [4]);
            _ = WriteFile(native, "bassmidi.dll", [5]);
            _ = WriteFile(native, "basswasapi.dll", [6]);
            return new(cache, soundFont, native);
        }
    }

    private static string WriteFile(string directory, string name, byte[] bytes)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static bool ContainsReusable(AudioCacheSessionStore store, string key)
    {
        using MemoryStream destination = new();
        return store.TryCopyReusable(key, destination, out _);
    }

    private sealed class CacheAccess(AudioCacheSessionStore store) : IAudioPcmCacheSessionAccess
    {
        public AudioCacheSessionSnapshot? AudioCacheSnapshot => store.GetSnapshot();
        public void InvalidateReusableAudio(string key) => store.InvalidateReusable(key);
        public AudioCacheSessionStore.AudioRecoverySpool CreateTransientAudioSpool(long lengthBytes) =>
            store.CreateRecoverySpool(lengthBytes);
        public void DisableReusableAudioRetention(string reason) =>
            store.DisableReusableRetention(reason);
        public AudioCachePublishResult PublishReusableAudio(string key, Stream source, long payloadLength) =>
            store.PublishReusable(key, source, payloadLength);
        public bool TryCopyReusableAudio(string key, Stream destination, out long payloadLength) =>
            store.TryCopyReusable(key, destination, out payloadLength);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"midora-span-staging-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
