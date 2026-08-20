using Midora.AudioDevice;
using Midora.Midi;

namespace Midora.Audio.Bass.Tests;

public sealed class AudioUnitCacheStagingTests
{
    private static readonly string SoundFontSha256 = new('a', 64);

    [Fact]
    public void MissPublishesCompleteRawUnitPcmAndSecondStageIsAHit()
    {
        using TemporaryDirectory directory = new();
        string cacheRoot = Path.Combine(directory.Path, "cache");
        string soundFont = WriteFile(directory.Path, "project.sf2", [1, 2, 3, 4]);
        string native = Path.Combine(directory.Path, "native");
        Directory.CreateDirectory(native);
        _ = WriteFile(native, "bass.dll", [5]);
        _ = WriteFile(native, "bassmidi.dll", [6]);
        _ = WriteFile(native, "basswasapi.dll", [7]);
        MidiRenderPlan plan = CreatePlan(port: 0, channel: 0);

        using AudioCacheSessionStore store = new(cacheRoot, 4096);
        CacheAccess access = new(store);
        using AudioUnitCacheStaging first = Assert.IsType<AudioUnitCacheStaging>(
            AudioUnitCacheStaging.Create(plan, access, SoundFontSha256, native, 500));
        string firstPath = first.FilePath;
        MidiUnitFragmentRenderPlan firstFragment = first.Plan.UnitFragments[0];
        Assert.False(firstFragment.PcmCacheHit);
        Assert.NotNull(firstFragment.PcmCacheKey);

        using (FileStream stream = new(firstPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        using (BinaryWriter writer = new(stream))
        {
            stream.Position = firstFragment.PcmCachePayloadOffset
                + AudioPcmCachePayload.HeaderByteCount;
            writer.Write(0.25f);
            writer.Write(-0.25f);
            writer.Write(0.5f);
            writer.Write(-0.5f);
        }
        first.PublishCompleted(access, completedRenderFrame: 2);

        using AudioUnitCacheStaging second = Assert.IsType<AudioUnitCacheStaging>(
            AudioUnitCacheStaging.Create(plan, access, SoundFontSha256, native, 500));
        string secondPath = second.FilePath;
        MidiUnitFragmentRenderPlan secondFragment = second.Plan.UnitFragments[0];
        Assert.True(secondFragment.PcmCacheHit);
        Assert.Equal(firstFragment.PcmCacheKey, secondFragment.PcmCacheKey);
        using FileStream cached = new(secondPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        cached.Position = secondFragment.PcmCachePayloadOffset
            + AudioPcmCachePayload.HeaderByteCount;
        using BinaryReader reader = new(cached);
        Assert.Equal([0.25f, -0.25f, 0.5f, -0.5f],
            new[] { reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() });
    }

    [Fact]
    public void SampleDomainKeyDoesNotDependOnCanonicalRoute()
    {
        MidiUnitFragmentRenderPlan first = CreatePlan(port: 0, channel: 0).UnitFragments[0];
        MidiUnitFragmentRenderPlan second = CreatePlan(port: 15, channel: 15).UnitFragments[0];

        string firstKey = MidiUnitPcmCacheKey.Create(
            first, 48_000, new string('a', 64), "native-v1", 500);
        string secondKey = MidiUnitPcmCacheKey.Create(
            second, 48_000, new string('a', 64), "native-v1", 500);

        Assert.Equal(firstKey, secondKey);
        Assert.NotEqual(firstKey, MidiUnitPcmCacheKey.Create(
            second, 44_100, new string('a', 64), "native-v1", 500));
        Assert.NotEqual(firstKey, MidiUnitPcmCacheKey.Create(
            second, 48_000, new string('b', 64), "native-v1", 500));
        Assert.NotEqual(firstKey, MidiUnitPcmCacheKey.Create(
            second, 48_000, new string('a', 64), "native-v2", 500));
        Assert.NotEqual(firstKey, MidiUnitPcmCacheKey.Create(
            second, 48_000, new string('a', 64), "native-v1", 501));
    }

    [Fact]
    public void StagingUsesAndReleasesTheConfiguredSessionTransientStore()
    {
        using TemporaryDirectory directory = new();
        string cacheRoot = Path.Combine(directory.Path, "cache");
        string soundFont = WriteFile(directory.Path, "project.sf2", [1]);
        string native = Path.Combine(directory.Path, "native");
        Directory.CreateDirectory(native);
        _ = WriteFile(native, "bass.dll", [2]);
        _ = WriteFile(native, "bassmidi.dll", [3]);
        _ = WriteFile(native, "basswasapi.dll", [4]);
        using AudioCacheSessionStore store = new(cacheRoot, 4096);
        CacheAccess access = new(store);
        string stagingPath;

        using (AudioUnitCacheStaging staging = Assert.IsType<AudioUnitCacheStaging>(
            AudioUnitCacheStaging.Create(
                CreatePlan(0, 0), access, SoundFontSha256, native, 500)))
        {
            stagingPath = staging.FilePath;
            AudioCacheSessionSnapshot active = store.GetSnapshot();
            Assert.True(active.TransientBytes > 0);
            Assert.StartsWith(store.SessionPath, stagingPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(stagingPath));
        }

        Assert.Equal(0, store.GetSnapshot().TransientBytes);
        Assert.False(File.Exists(stagingPath));
    }

    [Fact]
    public void RollingEventProviderDoesNotDisableReusableUnitStaging()
    {
        using TemporaryDirectory directory = new();
        string cacheRoot = Path.Combine(directory.Path, "cache");
        string native = Path.Combine(directory.Path, "native");
        Directory.CreateDirectory(native);
        _ = WriteFile(native, "bass.dll", [2]);
        _ = WriteFile(native, "bassmidi.dll", [3]);
        _ = WriteFile(native, "basswasapi.dll", [4]);
        MidiRenderPlan original = CreatePlan(0, 0);
        MidiRenderPlan rolling = new(
            original.SampleRate,
            original.TotalFrameCount,
            original.Ports,
            original.SourceIds,
            original.InitiallyDisabledSourceIndices,
            original.UnitFragments,
            original.Segments,
            original.UnitDescriptors,
            eventPageProvider: new EmptyEventPageProvider());
        using AudioCacheSessionStore store = new(cacheRoot, 4096);
        CacheAccess access = new(store);

        using AudioUnitCacheStaging staging = Assert.IsType<AudioUnitCacheStaging>(
            AudioUnitCacheStaging.Create(
                rolling, access, SoundFontSha256, native, 500));

        Assert.Same(rolling.EventPageProvider, staging.Plan.EventPageProvider);
        Assert.NotNull(staging.Plan.UnitFragments[0].PcmCacheKey);
    }

    private static MidiRenderPlan CreatePlan(byte port, byte channel)
    {
        const long sourceId = 101;
        MidiMessage note = MidiMessage.NoteOn(channel, 60, 100);
        MidiPortRenderPlan portPlan = new(port, [new ScheduledMidiMessage(0, note, 0)]);
        MidiUnitFragmentRenderPlan fragment = new(
            port,
            channel,
            sourceId,
            segmentId: 102,
            eventInstrumentId: 103,
            instanceGroupId: 104,
            subVoiceId: 105,
            sourceIndex: 0,
            startFrame: 0,
            endFrame: 2,
            semanticFingerprint: new string('c', 64),
            [new ScheduledMidiMessage(
                0,
                MidiMessage.NoteOn(0, 60, 100),
                0)]);
        return new MidiRenderPlan(48_000, 2, [portPlan], [sourceId], [], [fragment]);
    }

    private static string WriteFile(string directory, string name, byte[] bytes)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private sealed class CacheAccess(AudioCacheSessionStore store) : IAudioPcmCacheSessionAccess
    {
        public AudioCacheSessionSnapshot? AudioCacheSnapshot => store.GetSnapshot();

        public void InvalidateReusableAudio(string key) => store.InvalidateReusable(key);

        public AudioCacheSessionStore.AudioRecoverySpool CreateTransientAudioSpool(
            long lengthBytes) => store.CreateRecoverySpool(lengthBytes);

        public void DisableReusableAudioRetention(string reason) =>
            store.DisableReusableRetention(reason);

        public AudioCachePublishResult PublishReusableAudio(
            string key,
            Stream source,
            long payloadLength) => store.PublishReusable(key, source, payloadLength);

        public bool TryCopyReusableAudio(
            string key,
            Stream destination,
            out long payloadLength) => store.TryCopyReusable(key, destination, out payloadLength);
    }

    private sealed class EmptyEventPageProvider : IMidiRenderEventPageProvider
    {
        public IEnumerable<ScheduledPortMidiMessage> Query(
            long startFrame,
            long endFrame,
            CancellationToken cancellationToken = default) => [];
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"midora-unit-cache-tests-{Guid.NewGuid():N}");
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
