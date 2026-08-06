using Midora.Domain;

namespace Midora.Persistence.Tests;

public sealed class ExternalSoundFontVerificationCacheV1Tests
{
    [Fact]
    public async Task ReusesOnlyAnUninvalidatedFullIdentityAndSupportsForcedRecheck()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string projectPath = Path.Combine(directory, "Song.midora");
            string soundFontPath = Path.Combine(directory, "Piano.sf2");
            await File.WriteAllBytesAsync(soundFontPath, [1, 2, 3, 4]);
            ExternalSoundFontBindingV1 binding = await SoundFontBindingV1.BindExternalAsync(
                projectPath,
                soundFontPath);
            using ExternalSoundFontVerificationCacheV1 cache = new();

            ExternalSoundFontVerificationV1 first = await cache.VerifyAsync(
                projectPath,
                binding.Reference);
            ExternalSoundFontVerificationV1 cached = await cache.VerifyAsync(
                projectPath,
                binding.Reference);

            Assert.Same(first, cached);
            Assert.True(first.HashMatches);
            Assert.NotNull(first.FileStamp);
            Assert.False(cache.IsInvalidated);
            Assert.True(cache.TryConfirmCurrent(binding.Reference));
            Assert.Equal(1, cache.FullHashComputationCount);

            ExternalSoundFontVerificationV1 forced = await cache.VerifyAsync(
                projectPath,
                binding.Reference,
                forceFullVerification: true);

            Assert.NotSame(first, forced);
            Assert.True(forced.HashMatches);
            Assert.Equal(2, cache.FullHashComputationCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FileMonitorInvalidatesAndNextVerificationHashesChangedContent()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string projectPath = Path.Combine(directory, "Song.midora");
            string soundFontPath = Path.Combine(directory, "Piano.sf2");
            await File.WriteAllBytesAsync(soundFontPath, [1, 2, 3, 4]);
            ExternalSoundFontBindingV1 binding = await SoundFontBindingV1.BindExternalAsync(
                projectPath,
                soundFontPath);
            using ExternalSoundFontVerificationCacheV1 cache = new();
            _ = await cache.VerifyAsync(projectPath, binding.Reference);
            TaskCompletionSource invalidated = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            cache.Invalidated += (_, _) => invalidated.TrySetResult();

            await File.WriteAllBytesAsync(soundFontPath, [5, 6, 7, 8]);
            await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(cache.IsInvalidated);
            ExternalSoundFontVerificationV1 changed = await cache.VerifyAsync(
                projectPath,
                binding.Reference);
            Assert.False(changed.HashMatches);
            Assert.True(changed.RequiresWarning);
            Assert.True(cache.FullHashComputationCount >= 2);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WindowsFileIdentityDetectsReplacementWithSameSizeAndTimestamp()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string soundFontPath = Path.Combine(directory, "Piano.sf2");
            string replacementPath = Path.Combine(directory, "Replacement.sf2");
            await File.WriteAllBytesAsync(soundFontPath, [1, 2, 3, 4]);
            ExternalSoundFontFileStampV1 before =
                SoundFontFileSnapshotReaderV1.CaptureStamp(soundFontPath);
            await File.WriteAllBytesAsync(replacementPath, [5, 6, 7, 8]);
            File.SetLastWriteTimeUtc(
                replacementPath,
                DateTime.FromFileTimeUtc(before.LastWriteFileTimeUtc));

            File.Move(replacementPath, soundFontPath, overwrite: true);
            File.SetLastWriteTimeUtc(
                soundFontPath,
                DateTime.FromFileTimeUtc(before.LastWriteFileTimeUtc));
            ExternalSoundFontFileStampV1 after =
                SoundFontFileSnapshotReaderV1.CaptureStamp(soundFontPath);

            Assert.Equal(before.VolumeSerialNumber, after.VolumeSerialNumber);
            Assert.NotEqual(before.FileId, after.FileId);
            Assert.Equal(before.FileSizeBytes, after.FileSizeBytes);
            Assert.Equal(before.LastWriteFileTimeUtc, after.LastWriteFileTimeUtc);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AudioStartStampGateRejectsSameMetadataFileReplacement()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string projectPath = Path.Combine(directory, "Song.midora");
            string soundFontPath = Path.Combine(directory, "Piano.sf2");
            string replacementPath = Path.Combine(directory, "Replacement.sf2");
            await File.WriteAllBytesAsync(soundFontPath, [1, 2, 3, 4]);
            ExternalSoundFontBindingV1 binding = await SoundFontBindingV1.BindExternalAsync(
                projectPath,
                soundFontPath);
            using ExternalSoundFontVerificationCacheV1 cache = new();
            ExternalSoundFontVerificationV1 verified = await cache.VerifyAsync(
                projectPath,
                binding.Reference);
            ExternalSoundFontFileStampV1 stamp = Assert.IsType<ExternalSoundFontFileStampV1>(
                verified.FileStamp);
            await File.WriteAllBytesAsync(replacementPath, [5, 6, 7, 8]);
            File.SetLastWriteTimeUtc(
                replacementPath,
                DateTime.FromFileTimeUtc(stamp.LastWriteFileTimeUtc));
            File.Move(replacementPath, soundFontPath, overwrite: true);
            File.SetLastWriteTimeUtc(
                soundFontPath,
                DateTime.FromFileTimeUtc(stamp.LastWriteFileTimeUtc));

            Assert.False(cache.TryConfirmCurrent(binding.Reference));
            Assert.True(cache.IsInvalidated);
            Assert.Equal(1, cache.FullHashComputationCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MissingResourceDoesNotCreateAHashCacheEntry()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string projectPath = Path.Combine(directory, "Song.midora");
            ExternalProjectSoundFontReference reference = new(
                "Missing.sf2",
                "Missing.sf2",
                new string('0', 64),
                1);
            using ExternalSoundFontVerificationCacheV1 cache = new();

            ExternalSoundFontVerificationV1 result = await cache.VerifyAsync(
                projectPath,
                reference);

            Assert.Equal(ExternalSoundFontResolutionKind.Missing, result.Resolution);
            Assert.False(result.IsReadable);
            Assert.True(cache.IsInvalidated);
            Assert.Equal(0, cache.FullHashComputationCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"midora-sf2-cache-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
