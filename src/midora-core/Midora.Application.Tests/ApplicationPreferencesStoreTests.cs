using System.Text;

namespace Midora.Application.Tests;

public sealed class ApplicationPreferencesStoreTests
{
    [Fact]
    public void MissingFileUsesSpecifiedDefaultsWithoutNotice()
    {
        using TemporaryDirectory directory = new();
        ApplicationPreferencesLoadResult result = new ApplicationPreferencesStore(
            Path.Combine(directory.Path, "preferences.json")).Load();

        Assert.Equal(ApplicationPreferences.Default, result.Preferences);
        Assert.Null(result.Notice);
        Assert.Null(result.Preferences.RealtimeAudio.PlaybackOutputDeviceId);
        Assert.Equal(100, result.Preferences.RealtimeAudio.RenderAheadMilliseconds);
        Assert.Equal(50, result.Preferences.RealtimeAudio.DeviceBufferRequestMilliseconds);
        Assert.Equal(500, result.Preferences.RealtimeAudio.MaximumSampleVoicesPerUnitStream);
        Assert.Equal(
            AudioCachePreferences.DefaultMaximumReusableBytes,
            result.Preferences.AudioCache.MaximumReusableBytes);
    }

    [Fact]
    public void RoundTripIsDeterministicAndKeepsPickerPurposesSeparate()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "preferences.json");
        ApplicationPreferencesStore store = new(path);
        ApplicationPreferences preferences = new(
            new RealtimeAudioPreferences("endpoint-id", 2_000, 200, 16_777_216),
            new AudioCachePreferences(Path.Combine(directory.Path, "cache"), 0),
            new ApplicationRecentDirectories(
                Path.Combine(directory.Path, "open"),
                Path.Combine(directory.Path, "save"),
                Path.Combine(directory.Path, "sf2"),
                Path.Combine(directory.Path, "midi"),
                Path.Combine(directory.Path, "audio")));

        Assert.True(store.Save(preferences).Succeeded);
        byte[] first = File.ReadAllBytes(path);
        Assert.True(store.Save(preferences).Succeeded);
        byte[] second = File.ReadAllBytes(path);
        ApplicationPreferencesLoadResult loaded = store.Load();

        Assert.Equal(first, second);
        Assert.Null(loaded.Notice);
        Assert.Equal(preferences, loaded.Preferences);
        Assert.NotEqual(
            loaded.Preferences.RecentDirectories.MidiExport,
            loaded.Preferences.RecentDirectories.AudioRender);
    }

    [Theory]
    [InlineData(20, 5, 1)]
    [InlineData(2_000, 200, 16_777_216)]
    public void RealtimeAudioBoundaryValuesAreAccepted(
        int renderAhead,
        int deviceRequest,
        int sampleVoices)
    {
        RealtimeAudioPreferences preferences = new(
            null,
            renderAhead,
            deviceRequest,
            sampleVoices);

        preferences.Validate();
    }

    [Theory]
    [InlineData(19, 50, 500)]
    [InlineData(2_001, 50, 500)]
    [InlineData(100, 4, 500)]
    [InlineData(100, 201, 500)]
    [InlineData(100, 50, 0)]
    [InlineData(100, 50, 16_777_217)]
    public void RealtimeAudioOutOfRangeValuesAreRejectedWithoutClamp(
        int renderAhead,
        int deviceRequest,
        int sampleVoices)
    {
        RealtimeAudioPreferences preferences = new(
            null,
            renderAhead,
            deviceRequest,
            sampleVoices);

        Assert.Throws<ArgumentOutOfRangeException>(preferences.Validate);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2}")]
    [InlineData("{\"schemaVersion\":1,\"unknown\":true}")]
    [InlineData("{\"schemaVersion\":1,\"playbackOutputDeviceId\":null,\"renderAheadMilliseconds\":19,\"deviceBufferRequestMilliseconds\":50,\"realtimeMaximumSampleVoicesPerUnitStream\":500,\"audioCacheRootPath\":\"C:\\\\cache\",\"maximumReusableAudioCacheBytes\":17179869184,\"recentDirectories\":{}}")]
    [InlineData("not-json")]
    public void UnsupportedCorruptOrInvalidFileUsesDefaultsAndNotice(string json)
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "preferences.json");
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        ApplicationPreferencesLoadResult result = new ApplicationPreferencesStore(path).Load();

        Assert.Equal(ApplicationPreferences.Default, result.Preferences);
        Assert.Equal("PreferenceReadFailed", result.Notice?.Code);
        Assert.NotNull(result.Notice?.Error);
    }

    [Fact]
    public void OversizedFileUsesDefaultsInsteadOfAllocatingUnboundedInput()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "preferences.json");
        using (FileStream stream = File.Create(path))
        {
            stream.SetLength((1024 * 1024) + 1);
        }

        ApplicationPreferencesLoadResult result = new ApplicationPreferencesStore(path).Load();

        Assert.Equal(ApplicationPreferences.Default, result.Preferences);
        Assert.Equal("PreferenceReadFailed", result.Notice?.Code);
    }

    [Fact]
    public void FailedReplacementPreservesPreviouslyPublishedPreferences()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "preferences.json");
        ApplicationPreferencesStore store = new(path);
        ApplicationPreferences original = new(
            new RealtimeAudioPreferences("original", 100, 50, 500),
            new AudioCachePreferences(Path.Combine(directory.Path, "cache"), 1024),
            ApplicationRecentDirectories.Empty);
        ApplicationPreferences replacement = original with
        {
            RealtimeAudio = original.RealtimeAudio with { PlaybackOutputDeviceId = "new" }
        };
        Assert.True(store.Save(original).Succeeded);

        ApplicationPreferencesSaveResult failed;
        using (FileStream locked = new(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            failed = store.Save(replacement);
        }

        Assert.False(failed.Succeeded);
        Assert.Equal("PreferenceWriteFailed", failed.Notice?.Code);
        Assert.Equal(original, store.Load().Preferences);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void RelativeRecentDirectoryIsRejectedWithoutSilentRewrite()
    {
        using TemporaryDirectory directory = new();
        ApplicationPreferences invalid = new(
            RealtimeAudioPreferences.Default,
            new AudioCachePreferences(Path.Combine(directory.Path, "cache"), 1024),
            ApplicationRecentDirectories.Empty with { MidiExport = "relative" });
        ApplicationPreferencesStore store = new(
            Path.Combine(directory.Path, "preferences.json"));

        Assert.Throws<ArgumentException>(() => store.Save(invalid));
        Assert.False(File.Exists(store.FilePath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(long.MaxValue)]
    public void AudioCacheQuotaBoundaryValuesAreAccepted(long maximumReusableBytes)
    {
        using TemporaryDirectory directory = new();
        new AudioCachePreferences(directory.Path, maximumReusableBytes).Validate();
    }

    [Fact]
    public void AudioCacheRejectsRelativeUncAndNegativeQuota()
    {
        Assert.Throws<ArgumentException>(() => new AudioCachePreferences("relative", 0).Validate());
        Assert.Throws<ArgumentException>(() =>
            new AudioCachePreferences(@"\\server\share\Midora", 0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AudioCachePreferences(@"C:\Midora\AudioCache", -1).Validate());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"midora-preference-tests-{Guid.NewGuid():N}");
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
