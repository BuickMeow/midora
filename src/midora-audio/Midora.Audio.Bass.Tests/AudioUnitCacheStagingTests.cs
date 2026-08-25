using Midora.AudioDevice;
using Midora.Midi;
using System.Security.Cryptography;
using System.Text;

namespace Midora.Audio.Bass.Tests;

public sealed class AudioUnitCacheStagingTests
{
    private static readonly string SoundFontSetCacheIdentity = new('a', 64);

    [Fact]
    public void SegmentStageRejectsPlaybackViewGenerationThatDroppedChannelMode()
    {
        using TemporaryDirectory directory = new();
        string cacheRoot = Path.Combine(directory.Path, "cache");
        string native = Path.Combine(directory.Path, "native");
        string manifests = Path.Combine(directory.Path, "manifests");
        Directory.CreateDirectory(native);
        Directory.CreateDirectory(manifests);
        _ = WriteFile(native, "bass.dll", [2]);
        _ = WriteFile(native, "bassmidi.dll", [3]);
        _ = WriteFile(native, "basswasapi.dll", [4]);
        MidiRenderPlan plan = CreateSegmentPlan();
        MidiSegmentRenderPlan segment = plan.Segments[0];
        string legacyKey = CreateLegacySegmentPcmCacheKey(
            segment,
            plan.SampleRate,
            SoundFontSetCacheIdentity,
            AudioUnitCacheStaging.ComputeNativeIdentity(native),
            500);
        byte[] legacyPayload = new byte[checked((int)segment.PcmPayloadByteCount)];
        AudioPcmCachePayload.WriteHeader(
            legacyPayload,
            new AudioFormat(plan.SampleRate, 2, AudioSampleFormat.Float32),
            segment.FrameCount);
        using AudioCacheSessionStore store = new(cacheRoot, 4096);
        Assert.True(store.PublishReusable(legacyKey, legacyPayload).Published);
        CacheAccess access = new(store);

        using AudioSegmentCacheStaging staging = Assert.IsType<AudioSegmentCacheStaging>(
            AudioSegmentCacheStaging.Create(
                plan,
                access,
                SoundFontSetCacheIdentity,
                native,
                500,
                manifests));

        Assert.False(staging.Plan.Segments[0].PcmCacheHit);
        Assert.NotEqual(legacyKey, staging.Plan.Segments[0].PcmCacheKey);
    }

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
            AudioUnitCacheStaging.Create(plan, access, SoundFontSetCacheIdentity, native, 500));
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
            AudioUnitCacheStaging.Create(plan, access, SoundFontSetCacheIdentity, native, 500));
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
                CreatePlan(0, 0), access, SoundFontSetCacheIdentity, native, 500)))
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
                rolling, access, SoundFontSetCacheIdentity, native, 500));

        Assert.Same(rolling.EventPageProvider, staging.Plan.EventPageProvider);
        Assert.NotNull(staging.Plan.UnitFragments[0].PcmCacheKey);
    }

    [Fact]
    public void QuotaFullSegmentMissKeepsTheOriginalLiveSynthesisPlan()
    {
        using TemporaryDirectory directory = new();
        string cacheRoot = Path.Combine(directory.Path, "cache");
        string native = Path.Combine(directory.Path, "native");
        string manifests = Path.Combine(directory.Path, "manifests");
        Directory.CreateDirectory(native);
        Directory.CreateDirectory(manifests);
        _ = WriteFile(native, "bass.dll", [2]);
        _ = WriteFile(native, "bassmidi.dll", [3]);
        _ = WriteFile(native, "basswasapi.dll", [4]);
        using AudioCacheSessionStore store = new(cacheRoot, 112);
        string retainedKey = AudioCacheSessionStore.ComputeKey([1]);
        string rejectedKey = AudioCacheSessionStore.ComputeKey([2]);
        Assert.True(store.PublishReusable(retainedKey, new byte[16]).Published);
        Assert.False(store.PublishReusable(rejectedKey, new byte[16]).Published);
        Assert.Equal(
            AudioCacheRetentionState.DisabledByQuota,
            store.GetSnapshot().RetentionState);
        CacheAccess access = new(store);
        MidiRenderPlan plan = CreateSegmentPlan();

        AudioSegmentCacheStaging? staging = AudioSegmentCacheStaging.Create(
            plan,
            access,
            SoundFontSetCacheIdentity,
            native,
            500,
            manifests);

        Assert.Null(staging);
        Assert.False(plan.Segments[0].PcmCacheHit);
        Assert.Null(plan.Segments[0].PcmCacheKey);
        Assert.NotEmpty(plan.UnitFragments[0].Events.ToArray());
    }

    [Fact]
    public void InitiallyDisabledPureMidiChildDoesNotPublishIncompleteRootSegment()
    {
        using TemporaryDirectory directory = new();
        string cacheRoot = Path.Combine(directory.Path, "cache");
        string native = Path.Combine(directory.Path, "native");
        string manifests = Path.Combine(directory.Path, "manifests");
        Directory.CreateDirectory(native);
        Directory.CreateDirectory(manifests);
        _ = WriteFile(native, "bass.dll", [2]);
        _ = WriteFile(native, "bassmidi.dll", [3]);
        _ = WriteFile(native, "basswasapi.dll", [4]);
        using AudioCacheSessionStore store = new(cacheRoot, 4096);
        CacheAccess access = new(store);

        using AudioSegmentCacheStaging staging = Assert.IsType<AudioSegmentCacheStaging>(
            AudioSegmentCacheStaging.Create(
                CreateInitiallyMutedPureMidiSegmentPlan(),
                access,
                SoundFontSetCacheIdentity,
                native,
                500,
                manifests));
        string key = Assert.IsType<string>(staging.Plan.Segments[0].PcmCacheKey);

        staging.PublishCompleted(access, staging.Plan.TotalFrameCount);

        Assert.False(store.TryReadReusable(key, out _));
        Assert.Equal(
            AudioCacheRetentionState.Enabled,
            store.GetSnapshot().RetentionState);
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

    private static MidiRenderPlan CreateSegmentPlan()
    {
        MidiRenderPlan source = CreatePlan(0, 0);
        MidiSegmentRenderPlan segment = new(
            trackId: 101,
            segmentId: 102,
            sourceIndex: 0,
            startFrame: 0,
            endFrame: 2,
            semanticFingerprint: new string('d', 64));
        return new MidiRenderPlan(
            source.SampleRate,
            source.TotalFrameCount,
            source.Ports,
            source.SourceIds,
            source.InitiallyDisabledSourceIndices,
            source.UnitFragments,
            [segment],
            source.UnitDescriptors,
            source.EventPageProvider,
            source.EventStreamDescriptor,
            source.CacheSourceBindings,
            source.ReferencedPresetKeys);
    }

    private static MidiRenderPlan CreateInitiallyMutedPureMidiSegmentPlan()
    {
        const long trackId = 101;
        const long rootId = 201;
        const long segmentId = 102;
        MidiPortRenderPlan port = new(0,
        [
            new ScheduledMidiMessage(0, MidiMessage.NoteOn(0, 60, 100), 0)
        ]);
        MidiUnitFragmentRenderPlan fragment = new(
            canonicalZeroBasedPortNumber: 0,
            canonicalZeroBasedChannelNumber: 0,
            trackId,
            segmentId,
            eventInstrumentId: 0,
            instanceGroupId: rootId,
            subVoiceId: 202,
            sourceIndex: 1,
            startFrame: 0,
            endFrame: 2,
            semanticFingerprint: new string('e', 64),
            events:
            [
                new ScheduledMidiMessage(0, MidiMessage.NoteOn(0, 60, 100), 0)
            ],
            midiChannelRootId: rootId);
        MidiSegmentRenderPlan segment = new(
            trackId,
            segmentId,
            sourceIndex: 1,
            startFrame: 0,
            endFrame: 2,
            semanticFingerprint: new string('f', 64));
        return new MidiRenderPlan(
            48_000,
            2,
            [port],
            [trackId, rootId],
            initiallyDisabledSourceIndices: [0],
            unitFragments: [fragment],
            segments: [segment],
            cacheSourceBindings: [new MidiRenderCacheSourceBinding(0, 1)]);
    }

    private static string WriteFile(string directory, string name, byte[] bytes)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string CreateLegacySegmentPcmCacheKey(
        MidiSegmentRenderPlan segment,
        int sampleRate,
        string soundFontSetCacheIdentity,
        string nativeBaselineIdentity,
        int maximumSampleVoicesPerUnitStream)
    {
        using MemoryStream payload = new();
        using (BinaryWriter writer = new(payload, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("MIDORA_SAMPLE_DOMAIN_SEGMENT_PCM_KEY_V2");
            writer.Write(2);
            writer.Write(segment.SemanticFingerprint);
            writer.Write(segment.FrameCount);
            writer.Write(sampleRate);
            writer.Write(soundFontSetCacheIdentity);
            writer.Write(nativeBaselineIdentity);
            writer.Write(maximumSampleVoicesPerUnitStream);
            writer.Write(true);
            writer.Write(true);
            writer.Write(1f);
            writer.Write(0f);
            writer.Write(16_384);
            writer.Write(2);
            writer.Write((int)AudioSampleFormat.Float32);
        }
        return Convert.ToHexStringLower(SHA256.HashData(
            payload.GetBuffer().AsSpan(0, checked((int)payload.Length))));
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
