namespace Midora.Audio.Bass.Tests;

public sealed class BassMidiSoundFontLoadabilityValidatorTests
{
    [Fact]
    public async Task PinnedBassMidiAcceptsTheIntegrationSoundFont()
    {
        NativeAudioIntegrationEnvironment.LoadBassMidi();
        string soundFontPath = NativeAudioIntegrationEnvironment.RequireSoundFontPath();
        BassMidiSoundFontLoadabilityValidator validator = new();

        await validator.ValidateAsync(soundFontPath);
    }

    [Fact]
    public async Task PinnedBassMidiRejectsAnInvalidSf2Payload()
    {
        NativeAudioIntegrationEnvironment.LoadBassMidi();
        string path = Path.Combine(
            Path.GetTempPath(),
            $"midora-invalid-sf2-{Guid.NewGuid():N}.sf2");
        try
        {
            await File.WriteAllBytesAsync(path, "not an sf2"u8.ToArray());
            BassMidiSoundFontLoadabilityValidator validator = new();

            SoundFontLoadabilityException error =
                await Assert.ThrowsAsync<SoundFontLoadabilityException>(
                    async () => await validator.ValidateAsync(path));

            Assert.Equal(SoundFontLoadabilityFailure.UnsupportedOrCorrupt, error.Failure);
            Assert.Equal(Midora.NativeInterops.Bass.BASS.BASS_ERROR_FILEFORM, error.BackendErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MissingFileIsRejectedBeforeNativeValidation()
    {
        BassMidiSoundFontLoadabilityValidator validator = new();
        string path = Path.Combine(
            Path.GetTempPath(),
            $"midora-missing-sf2-{Guid.NewGuid():N}.sf2");

        SoundFontLoadabilityException error =
            await Assert.ThrowsAsync<SoundFontLoadabilityException>(
                async () => await validator.ValidateAsync(path));

        Assert.Equal(SoundFontLoadabilityFailure.Missing, error.Failure);
        Assert.Null(error.BackendErrorCode);
    }
}
