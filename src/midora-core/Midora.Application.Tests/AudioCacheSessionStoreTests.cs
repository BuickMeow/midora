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
        Assert.Equal(176, store.GetSnapshot().ReusableBytes);
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
        using AudioCacheSessionStore store = new(root.Path, 80);
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
        string path = Path.Combine(store.SessionPath, "reusable", key + ".mcac");
        File.WriteAllBytes(path, [1, 2, 3]);

        Assert.False(store.TryReadReusable(key, out _));
        Assert.False(File.Exists(path));
        Assert.Equal(
            AudioCacheWarningCode.AudioCacheCorruptAndRebuilt,
            store.GetSnapshot().Warning.Code);
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
