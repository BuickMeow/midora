using System.Runtime.InteropServices;

namespace Midora.Audio.Bass.Tests;

public sealed class BassMidiSoundFontLoadabilityValidatorTests
{
    private const string SoundFontPath =
        @"D:\Soundfonts\sf2\sDetrimental Concert Grand Piano.sf2";

    static BassMidiSoundFontLoadabilityValidatorTests()
    {
        string bassPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Midora",
            "Native",
            "BASS",
            "win-x64");
        _ = NativeLibrary.Load(Path.Combine(bassPath, "bass.dll"));
        _ = NativeLibrary.Load(Path.Combine(bassPath, "bassmidi.dll"));
    }

    [Fact]
    public async Task PinnedBassMidiAcceptsTheIntegrationSoundFont()
    {
        Assert.True(File.Exists(SoundFontPath), $"Missing integration-test SoundFont: {SoundFontPath}");
        BassMidiSoundFontLoadabilityValidator validator = new();

        await validator.ValidateAsync(SoundFontPath);
    }

    [Fact]
    public async Task PinnedBassMidiRejectsAnInvalidSf2Payload()
    {
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
