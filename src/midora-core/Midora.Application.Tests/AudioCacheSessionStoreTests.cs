using System.IO.MemoryMappedFiles;
using System.Text;
using Midora.Audio;

namespace Midora.Application.Tests;

public sealed class AudioCacheSessionStoreTests
{
    [Fact]
    public void ReusableEntryPublishesAtomicallyAndRoundTrips()
    {
        using TemporaryDirectory root = new();
        byte[] keyBytes = Encoding.UTF8.GetBytes("canonical-unit-key");
        byte[] expected = Enumerable.Range(0, 128).Select(value => (byte)value).ToArray();
        string key = AudioCacheSessionStore.ComputeKey(keyBytes);

        using AudioCacheSessionStore store = new(root.Path, 4096);
        AudioCachePublishResult first = store.PublishReusable(key, expected);
        AudioCachePublishResult second = store.PublishReusable(key, expected);

        Assert.True(first.Published);
        Assert.False(first.AlreadyPresent);
        Assert.True(second.Published);
        Assert.True(second.AlreadyPresent);
        Assert.True(store.TryReadReusable(key, out byte[] actual));
        Assert.Equal(expected, actual);
        Assert.Equal(320, store.GetSnapshot().ReusableBytes);
        Assert.Equal(1, store.GetSnapshot().PackFileCount);
        Assert.Empty(Directory.GetFiles(
            Path.Combine(store.SessionPath, "reusable"),
            "*.tmp"));
    }

    [Fact]
    public void StreamingEntryPublishesAndCopiesWithoutWholePayloadMaterialization()
    {
        using TemporaryDirectory root = new();
        byte[] expected = Enumerable.Range(0, 200_000)
            .Select(value => (byte)(value * 31))
            .ToArray();
        string key = AudioCacheSessionStore.ComputeKey([7, 7, 7]);

        using AudioCacheSessionStore store = new(root.Path, 1_000_000);
        using MemoryStream source = new(expected, writable: false);
        AudioCachePublishResult publish = store.PublishReusable(key, source, expected.Length);
        using MemoryStream copied = new();

        Assert.True(publish.Published);
        Assert.True(store.TryCopyReusable(key, copied, out long copiedLength));
        Assert.Equal(expected.Length, copiedLength);
        Assert.Equal(expected, copied.ToArray());
    }

    [Fact]
    public void ZeroQuotaDisablesRetentionButKeepsTransientRecoverySpool()
    {
        using TemporaryDirectory root = new();
        using AudioCacheSessionStore store = new(root.Path, 0);
        string key = AudioCacheSessionStore.ComputeKey([1, 2, 3]);

        AudioCachePublishResult publish = store.PublishReusable(key, [4, 5, 6]);

        Assert.False(publish.Published);
        Assert.Equal(AudioCacheRetentionState.DisabledByPreference, publish.RetentionState);
        Assert.Equal(
            AudioCacheWarningCode.AudioCacheRetentionDisabled,
            publish.Warning.Code);

        string spoolPath;
        using (AudioCacheSessionStore.AudioRecoverySpool spool = store.CreateRecoverySpool(256))
        {
            spoolPath = spool.Path;
            spool.Stream.Write([1, 2, 3, 4]);
            AudioCacheSessionSnapshot active = store.GetSnapshot();
            Assert.Equal(256, active.TransientBytes);
            Assert.Equal(256, active.PeakTransientBytes);
            Assert.True(File.Exists(spoolPath));
        }

        AudioCacheSessionSnapshot released = store.GetSnapshot();
        Assert.Equal(0, released.TransientBytes);
        Assert.Equal(256, released.PeakTransientBytes);
        Assert.False(File.Exists(spoolPath));
    }

    [Fact]
    public void RecoverySpoolTransfersItsFileHandleWithoutReleasingTheReservation()
    {
        using TemporaryDirectory root = new();
        using AudioCacheSessionStore store = new(root.Path, 4096);
        AudioCacheSessionStore.AudioRecoverySpool spool = store.CreateRecoverySpool(256);
        string path = spool.Path;

        spool.ReleaseFileHandleForExternalUse();

        Assert.Throws<ObjectDisposedException>(() => _ = spool.Stream);
        Assert.Equal(256, store.GetSnapshot().TransientBytes);
        using (MemoryMappedFile mapping = MemoryMappedFile.CreateFromFile(
            path,
            FileMode.Open,
            mapName: null,
            capacity: 256,
            MemoryMappedFileAccess.ReadWrite))
        using (MemoryMappedViewAccessor view = mapping.CreateViewAccessor())
        {
            view.Write(0, (byte)0x5a);
            Assert.Equal(0x5a, view.ReadByte(0));
        }

        spool.Dispose();

        Assert.Equal(0, store.GetSnapshot().TransientBytes);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void QuotaFullStopsNewRetentionWithoutHidingExistingEntries()
    {
        using TemporaryDirectory root = new();
        using AudioCacheSessionStore store = new(root.Path, 112);
        string firstKey = AudioCacheSessionStore.ComputeKey([1]);
        string secondKey = AudioCacheSessionStore.ComputeKey([2]);

        Assert.True(store.PublishReusable(firstKey, new byte[16]).Published);
        AudioCachePublishResult full = store.PublishReusable(secondKey, new byte[16]);

        Assert.False(full.Published);
        Assert.Equal(AudioCacheRetentionState.DisabledByQuota, full.RetentionState);
        Assert.True(store.TryReadReusable(firstKey, out byte[] first));
        Assert.Equal(16, first.Length);
        Assert.False(store.TryReadReusable(secondKey, out _));
    }

    [Fact]
    public void StagingWriteFailureDisablesOnlyEnabledReusableRetention()
    {
        using TemporaryDirectory root = new();
        using AudioCacheSessionStore enabled = new(root.Path, 4096);

        enabled.DisableReusableRetention("Injected reusable staging failure.");

        AudioCacheSessionSnapshot failed = enabled.GetSnapshot();
        Assert.Equal(AudioCacheRetentionState.DisabledByWriteFailure, failed.RetentionState);
        Assert.Equal(AudioCacheWarningCode.AudioCacheRetentionDisabled, failed.Warning.Code);
        Assert.Contains("Injected reusable staging failure", failed.Warning.Message, StringComparison.Ordinal);

        using TemporaryDirectory zeroRoot = new();
        using AudioCacheSessionStore disabledByPreference = new(zeroRoot.Path, 0);
        disabledByPreference.DisableReusableRetention("Must not replace the preference state.");

        AudioCacheSessionSnapshot unchanged = disabledByPreference.GetSnapshot();
        Assert.Equal(AudioCacheRetentionState.DisabledByPreference, unchanged.RetentionState);
        Assert.DoesNotContain("Must not replace", unchanged.Warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CorruptEntryIsIsolatedAndReportedInsteadOfBeingReturned()
    {
        using TemporaryDirectory root = new();
        using AudioCacheSessionStore store = new(root.Path, 4096);
        string key = AudioCacheSessionStore.ComputeKey([9, 8, 7]);
        Assert.True(store.PublishReusable(key, new byte[32]).Published);
        string path = Directory.GetFiles(
            Path.Combine(store.SessionPath, "reusable"),
            "pack-*.mcap").Single();
        using (FileStream corrupt = new(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read))
        {
            corrupt.Position = 16 + 96;
            corrupt.WriteByte(0xff);
        }

        Assert.False(store.TryReadReusable(key, out _));
        Assert.True(File.Exists(path));
        Assert.True(store.GetSnapshot().DeadReusableBytes > 0);
        Assert.Equal(
            AudioCacheWarningCode.AudioCacheCorruptAndRebuilt,
            store.GetSnapshot().Warning.Code);
    }

    [Fact]
    public void ThousandsOfLogicalEntriesUseOnePackAndOneIndexInsteadOfThousandsOfFiles()
    {
        using TemporaryDirectory root = new();
        using AudioCacheSessionStore store = new(root.Path, 16 * 1024 * 1024);

        for (int index = 0; index < 2_000; index++)
        {
            string key = AudioCacheSessionStore.ComputeKey(BitConverter.GetBytes(index));
            Assert.True(store.PublishReusable(key, new byte[32]).Published);
        }

        string reusable = Path.Combine(store.SessionPath, "reusable");
        Assert.Single(Directory.GetFiles(reusable, "pack-*.mcap"));
        Assert.Single(Directory.GetFiles(reusable, "*.mcix"));
        Assert.Equal(2, Directory.GetFiles(reusable).Length);
    }

    [Fact]
    public void PendingBackgroundBatchIsImmediatelyReusableAndDrainsIntoPack()
    {
        using TemporaryDirectory root = new();
        using AudioCacheSessionStore store = new(root.Path, 1024 * 1024);
        byte[] expected = Enumerable.Range(0, 64 * 1024)
            .Select(value => (byte)(value * 17))
            .ToArray();
        string key = AudioCacheSessionStore.ComputeKey([4, 5, 6]);
        AudioCacheSessionStore.AudioRecoverySpool spool =
            store.CreateRecoverySpool(expected.Length);
        spool.Stream.Write(expected);
        spool.Stream.Flush();

        store.QueueReusableBatch(
            spool,
            [new AudioCachePublishSlice(key, 0, expected.Length)]);

        using MemoryStream immediate = new();
        Assert.True(store.TryCopyReusable(key, immediate, out long length));
        Assert.Equal(expected.Length, length);
        Assert.Equal(expected, immediate.ToArray());
        Assert.True(SpinWait.SpinUntil(
            () => store.GetSnapshot().PendingPublishCount == 0,
            TimeSpan.FromSeconds(5)));
        Assert.Equal(0, store.GetSnapshot().WriterBacklogBytes);
        Assert.True(SpinWait.SpinUntil(
            () => store.GetSnapshot().TransientBytes == 0,
            TimeSpan.FromSeconds(5)));
        Assert.Equal(0, store.GetSnapshot().TransientBytes);
        Assert.True(store.TryReadReusable(key, out byte[] packed));
        Assert.Equal(expected, packed);
    }

    [Fact]
    public void PendingBackgroundBatchCanBeReadThroughALeaseAfterPackPublication()
    {
        using TemporaryDirectory root = new();
        using AudioCacheSessionStore store = new(root.Path, 8 * 1024 * 1024);
        byte[] expected = Enumerable.Range(0, 4 * 1024 * 1024)
            .Select(value => (byte)(value * 29))
            .ToArray();
        string key = AudioCacheSessionStore.ComputeKey([7, 6, 5]);
        AudioCacheSessionStore.AudioRecoverySpool spool =
            store.CreateRecoverySpool(expected.Length);
        string spoolPath = spool.Path;
        spool.Stream.Write(expected);
        spool.Stream.Flush();

        store.QueueReusableBatch(
            spool,
            [new AudioCachePublishSlice(key, 0, expected.Length)]);
        using ReusableAudioReadLease lease = Assert.IsType<ReusableAudioReadLease>(
            store.AcquireReusableReadLease([key]));
        ReusableAudioReadEntry entry = lease.Entries[key];

        Assert.Equal(expected.Length, entry.PayloadLength);
        Assert.All(
            entry.Extents,
            extent => Assert.Contains(
                extent.Kind,
                new[]
                {
                    ReusableAudioReadExtentKind.RawPayload,
                    ReusableAudioReadExtentKind.PackBlock
                }));
        Assert.True(SpinWait.SpinUntil(
            () => store.GetSnapshot().PendingPublishCount == 0,
            TimeSpan.FromSeconds(5)));
        if (entry.Extents[0].Kind == ReusableAudioReadExtentKind.RawPayload)
        {
            Assert.True(File.Exists(spoolPath));
            using FileStream pending = new(
                entry.Extents[0].Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            pending.Position = entry.Extents[0].FileOffset;
            byte[] actual = new byte[expected.Length];
            pending.ReadExactly(actual);
            Assert.Equal(expected, actual);
        }

        lease.Dispose();
        Assert.True(SpinWait.SpinUntil(
            () => !File.Exists(spoolPath),
            TimeSpan.FromSeconds(5)));
        Assert.Equal(0, store.GetSnapshot().TransientBytes);
    }

    [Fact]
    public void SupersededGenerationRemainsReadableUntilCompactionButIsNotLive()
    {
        using TemporaryDirectory root = new();
        using AudioCacheSessionStore store = new(root.Path, 1024 * 1024);
        string first = AudioCacheSessionStore.ComputeKey([1, 1]);
        string second = AudioCacheSessionStore.ComputeKey([2, 2]);

        store.RegisterReusableGeneration("segment:1:2", first);
        Assert.True(store.PublishReusable(first, new byte[32]).Published);
        store.RegisterReusableGeneration("segment:1:2", second);
        Assert.True(store.PublishReusable(second, new byte[64]).Published);

        AudioCacheSessionSnapshot snapshot = store.GetSnapshot();
        Assert.Equal(64 + (2 * 96), snapshot.ReusableBytes);
        Assert.True(snapshot.DeadReusableBytes >= 32 + 96);
        Assert.True(store.TryReadReusable(first, out byte[] oldGeneration));
        Assert.Equal(32, oldGeneration.Length);

        store.RegisterReusableGeneration("segment:1:2", first);
        Assert.Equal(32 + 96, store.GetSnapshot().ReusableBytes);
    }

    [Fact]
    public void CompactionPublishesNewIndexBeforeRemovingObsoleteGeneration()
    {
        using TemporaryDirectory root = new();
        string reusable = Path.Combine(root.Path, "reusable");
        using AudioCachePackStore store = new(reusable, 1024 * 1024);
        string oldKey = AudioCacheSessionStore.ComputeKey([8, 1]);
        string liveKey = AudioCacheSessionStore.ComputeKey([8, 2]);
        store.RegisterGeneration("segment:8:9", oldKey);
        using (MemoryStream oldPayload = new(new byte[64]))
        {
            Assert.True(store.Publish(oldKey, oldPayload, 64, segmentCompleted: true).Published);
        }
        store.RegisterGeneration("segment:8:9", liveKey);
        using (MemoryStream livePayload = new(new byte[96]))
        {
            Assert.True(store.Publish(liveKey, livePayload, 96, segmentCompleted: true).Published);
        }

        Assert.True(store.TryCompact(
            isStopped: true,
            idleDuration: TimeSpan.Zero,
            minimumDeadBytes: 1,
            minimumDeadRatio: 0.01,
            minimumHeadroomBytes: 0));

        Assert.False(store.TryRead(oldKey, out _));
        Assert.True(store.TryRead(liveKey, out byte[] live));
        Assert.Equal(96, live.Length);
        Assert.Equal(1, store.PackCount);
        Assert.Equal(0, store.DeadBytes);
        Assert.True(File.Exists(Path.Combine(reusable, "pack-index.mcix")));
    }

    [Fact]
    public void PackRolloverAndCompactionKeepFileCountBounded()
    {
        using TemporaryDirectory root = new();
        using AudioCachePackStore store = new(
            Path.Combine(root.Path, "reusable"),
            maximumLiveBytes: 64 * 1024,
            maximumGenerationBytes: 700);
        List<string> currentKeys = [];
        for (int index = 0; index < 20; index++)
        {
            string first = AudioCacheSessionStore.ComputeKey(
                BitConverter.GetBytes(index * 2));
            string second = AudioCacheSessionStore.ComputeKey(
                BitConverter.GetBytes((index * 2) + 1));
            string owner = "segment:" + index;
            store.RegisterGeneration(owner, first);
            using (MemoryStream payload = new(new byte[96]))
            {
                Assert.True(store.Publish(first, payload, 96, true).Published);
            }
            store.RegisterGeneration(owner, second);
            using (MemoryStream payload = new(new byte[96]))
            {
                Assert.True(store.Publish(second, payload, 96, true).Published);
            }
            currentKeys.Add(second);
        }
        Assert.True(store.PackCount > 1);

        Assert.True(store.TryCompact(true, TimeSpan.Zero, 1, 0.01, 0));

        Assert.Equal(currentKeys.Count, currentKeys.Count(key => store.TryRead(key, out _)));
        Assert.InRange(store.PackCount, 1, 10);
        Assert.Equal(0, store.DeadBytes);
    }

    [Fact]
    public void ClearInactiveCacheOnlyDeletesRecognizedInactiveSessionChildren()
    {
        using TemporaryDirectory root = new();
        using AudioCacheSessionStore store = new(root.Path, 4096);
        string staleName = "session-" + Guid.NewGuid().ToString("N");
        string stale = Path.Combine(root.Path, staleName);
        Directory.CreateDirectory(stale);
        File.WriteAllText(
            Path.Combine(stale, "session.manifest"),
            "MIDORA_AUDIO_CACHE_SESSION_V1\n" + staleName + "\n",
            new UTF8Encoding(false));
        string unknown = Path.Combine(root.Path, "session-unknown");
        Directory.CreateDirectory(unknown);
        string unrelated = Path.Combine(root.Path, "keep.txt");
        File.WriteAllText(unrelated, "keep");

        Assert.Equal(1, store.ClearInactiveSessions());

        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(unknown));
        Assert.True(File.Exists(unrelated));
        Assert.True(Directory.Exists(store.SessionPath));
    }

    [Fact]
    public void DisposeRemovesOnlyTheOwnedSessionDirectory()
    {
        using TemporaryDirectory root = new();
        string unrelated = Path.Combine(root.Path, "unrelated.txt");
        File.WriteAllText(unrelated, "keep");
        string sessionPath;
        using (AudioCacheSessionStore store = new(root.Path, 4096))
        {
            sessionPath = store.SessionPath;
            Assert.True(Directory.Exists(sessionPath));
        }

        Assert.False(Directory.Exists(sessionPath));
        Assert.True(File.Exists(unrelated));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"midora-audio-cache-tests-{Guid.NewGuid():N}");
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
