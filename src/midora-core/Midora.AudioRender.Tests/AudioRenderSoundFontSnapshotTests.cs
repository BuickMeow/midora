namespace Midora.AudioRender.Tests;

public sealed class AudioRenderSoundFontSnapshotTests
{
    [Fact]
    public async Task OrderedApplicationSoundFontsAreFrozenWithoutCopyingContent()
    {
        using TemporaryDirectory directory = new();
        string first = directory.PathFor("First.sf2");
        string second = directory.PathFor("Second.sf2");
        await File.WriteAllBytesAsync(first, [1, 2, 3]);
        await File.WriteAllBytesAsync(second, [4, 5]);

        await using AudioRenderSoundFontSnapshot snapshot =
            await AudioRenderSoundFontSnapshot.CreateAsync([first, second]);

        Assert.Equal([Path.GetFullPath(first), Path.GetFullPath(second)], snapshot.SoundFontPaths);
        Assert.Equal(5, snapshot.TotalFileSizeBytes);
        Assert.Equal(64, snapshot.CacheIdentity.Length);
        Assert.Equal(2, Directory.GetFiles(directory.Path).Length);
    }

    [Fact]
    public async Task EmptyApplicationSoundFontListIsRejected()
    {
        AudioRenderSoundFontException failure = await Assert.ThrowsAsync<AudioRenderSoundFontException>(
            () => AudioRenderSoundFontSnapshot.CreateAsync([]));

        Assert.Equal(AudioRenderSoundFontFailure.NoEnabledSoundFonts, failure.Failure);
    }

    [Fact]
    public async Task MissingEnabledSoundFontIsRejectedOnlyWhenAudioTaskFreezesIt()
    {
        using TemporaryDirectory directory = new();
        string missing = directory.PathFor("Missing.sf2");

        AudioRenderSoundFontException failure = await Assert.ThrowsAsync<AudioRenderSoundFontException>(
            () => AudioRenderSoundFontSnapshot.CreateAsync([missing]));

        Assert.Equal(AudioRenderSoundFontFailure.Missing, failure.Failure);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"midora-audio-render-sf-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string PathFor(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
