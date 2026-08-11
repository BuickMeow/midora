using Midora.Audio;
using Midora.Domain;

namespace Midora.Playback.Tests;

public sealed class ProjectCompilationSessionAudioCacheTests
{
    [Fact]
    public void SampleDomainGenerationResetPreservesCompletedReusableEntries()
    {
        using TemporaryDirectory cache = new();
        using ProjectCompilationSession session = new(new MidoraProject(192));
        _ = session.ConfigureAudioCache(cache.Path, 4_096);
        string key = AudioCacheSessionStore.ComputeKey([1, 2, 3]);
        byte[] payload = [4, 5, 6, 7];
        Assert.True(session.PublishReusableAudio(key, payload).Published);

        _ = session.ResetAudioCacheGenerations();

        Assert.True(session.TryReadReusableAudio(key, out byte[] restored));
        Assert.Equal(payload, restored);
    }

    [Fact]
    public void ReapplyingIdenticalCachePreferencesKeepsTheCurrentSessionEntries()
    {
        using TemporaryDirectory cache = new();
        using ProjectCompilationSession session = new(new MidoraProject(192));
        _ = session.ConfigureAudioCache(cache.Path, 4_096);
        string key = AudioCacheSessionStore.ComputeKey([8, 9, 10]);
        byte[] payload = [11, 12, 13, 14];
        Assert.True(session.PublishReusableAudio(key, payload).Published);

        _ = session.ConfigureAudioCache(
            cache.Path + Path.DirectorySeparatorChar,
            4_096);

        Assert.True(session.TryReadReusableAudio(key, out byte[] restored));
        Assert.Equal(payload, restored);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"midora-compilation-cache-{Guid.NewGuid():N}");
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
