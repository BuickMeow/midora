using Midora.Audio.Bass.Internals;

namespace Midora.Audio.Bass.Tests;

public sealed class NativeBassEnvironmentTests
{
    [Fact]
    public void PinnedBassAndBassMidiVersionsAreAcceptedExactly()
    {
        BassNativeRuntime.ValidateExactVersion(
            "BASS",
            BassNativeRuntime.SupportedBassVersion,
            BassNativeRuntime.SupportedBassVersion);
        BassNativeRuntime.ValidateExactVersion(
            "BASSMIDI",
            BassNativeRuntime.SupportedBassMidiVersion,
            BassNativeRuntime.SupportedBassMidiVersion);
    }

    [Theory]
    [InlineData(0x02041202u, 0x02041203u)]
    [InlineData(0x02041204u, 0x02041203u)]
    [InlineData(0x02041001u, 0x02041000u)]
    public void DifferentBassRevisionIsRejectedEvenWhenApiMajorMatches(uint actual, uint expected)
    {
        MidoraAudioException exception = Assert.Throws<MidoraAudioException>(
            () => BassNativeRuntime.ValidateExactVersion("BASS component", actual, expected));

        Assert.Contains($"0x{actual:x8}", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"0x{expected:x8}", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    public void ProcessRuntimeRejectsUnavailableUtf8DeviceInformationMode(uint configuredValue)
    {
        Assert.Throws<MidoraAudioException>(() =>
            BassNativeRuntime.ValidateUtf8DeviceInformationMode(configuredValue));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    public void ProcessRuntimeAcceptsEnabledUtf8DeviceInformationMode(uint configuredValue)
    {
        BassNativeRuntime.ValidateUtf8DeviceInformationMode(configuredValue);
    }
}
