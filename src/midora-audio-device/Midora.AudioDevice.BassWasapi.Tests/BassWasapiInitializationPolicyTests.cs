using Midora.AudioDevice.BassWasapi.Internals;
using Midora.AudioDevice.BassWasapi.Settings;
using Midora.NativeInterops.BassWasapi;

namespace Midora.AudioDevice.BassWasapi.Tests;

public sealed class BassWasapiInitializationPolicyTests
{
    [Fact]
    public void PinnedBassWasapiVersionIsAcceptedExactly()
    {
        BassWasapiOutputDeviceFactory.ValidateExactVersion(BassWasapiOutputDeviceFactory.SupportedVersion);
    }

    [Theory]
    [InlineData(0x02040400u)]
    [InlineData(0x02040402u)]
    [InlineData(0x02040500u)]
    public void DifferentBassWasapiRevisionIsRejected(uint actualVersion)
    {
        MidoraAudioDeviceException exception = Assert.Throws<MidoraAudioDeviceException>(
            () => BassWasapiOutputDeviceFactory.ValidateExactVersion(actualVersion));

        Assert.Contains($"0x{actualVersion:x8}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("0x02040401", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InitialReleaseRequestsOnlySharedEventDrivenFloatStereo()
    {
        Assert.Equal(0, BassWasapiInitializationPolicy.RequestedFrequency);
        Assert.Equal(2, BassWasapiInitializationPolicy.RequestedChannelCount);
        Assert.Equal(8, BassWasapiInitializationPolicy.BytesPerFrame);
        Assert.Equal(0f, BassWasapiInitializationPolicy.RequestedPeriodSeconds);
        Assert.Equal(BASSWASAPI.BASS_WASAPI_EVENT, BassWasapiInitializationPolicy.InitializationFlags);
        Assert.Equal(0u, BassWasapiInitializationPolicy.InitializationFlags
            & (BASSWASAPI.BASS_WASAPI_EXCLUSIVE
                | BASSWASAPI.BASS_WASAPI_AUTOFORMAT
                | BASSWASAPI.BASS_WASAPI_BUFFER
                | BASSWASAPI.BASS_WASAPI_ASYNC));
    }

    [Theory]
    [InlineData(BASSWASAPI.BASS_WASAPI_EVENT, 48_000u, 2u, BASSWASAPI.BASS_WASAPI_FORMAT_FLOAT, true)]
    [InlineData(BASSWASAPI.BASS_WASAPI_EVENT | BASSWASAPI.BASS_WASAPI_EXCLUSIVE, 48_000u, 2u, BASSWASAPI.BASS_WASAPI_FORMAT_FLOAT, false)]
    [InlineData(0u, 48_000u, 2u, BASSWASAPI.BASS_WASAPI_FORMAT_FLOAT, false)]
    [InlineData(BASSWASAPI.BASS_WASAPI_EVENT, 0u, 2u, BASSWASAPI.BASS_WASAPI_FORMAT_FLOAT, false)]
    [InlineData(BASSWASAPI.BASS_WASAPI_EVENT, 48_000u, 6u, BASSWASAPI.BASS_WASAPI_FORMAT_FLOAT, false)]
    [InlineData(BASSWASAPI.BASS_WASAPI_EVENT, 48_000u, 2u, BASSWASAPI.BASS_WASAPI_FORMAT_32BIT, false)]
    public void RuntimeFormatMustRemainSharedEventDrivenFloatStereo(
        uint flags,
        uint sampleRate,
        uint channelCount,
        uint sampleFormat,
        bool expected)
    {
        Assert.Equal(expected, BassWasapiInitializationPolicy.IsSupportedRuntimeFormat(
            flags, sampleRate, channelCount, sampleFormat));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(50)]
    [InlineData(200)]
    public void DeviceBufferRequestAcceptsSpecifiedBoundaries(int milliseconds)
    {
        BassWasapiAudioOutputDeviceSettings settings = new(milliseconds);

        Assert.Equal(milliseconds, settings.DeviceBufferRequestMilliseconds);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(201)]
    public void DeviceBufferRequestRejectsValuesOutsideSpecifiedBoundaries(int milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BassWasapiAudioOutputDeviceSettings(milliseconds));
    }

    [Theory]
    [InlineData(8u, 1u)]
    [InlineData(800u, 100u)]
    public void RuntimeBufferByteCountConvertsToStereoFloatFrames(uint bytes, uint expectedFrames)
    {
        Assert.True(BassWasapiInitializationPolicy.TryGetBufferFrameCount(bytes, out uint frames));
        Assert.Equal(expectedFrames, frames);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(7u)]
    [InlineData(9u)]
    public void RuntimeBufferByteCountMustBePositiveAndFrameAligned(uint bytes)
    {
        Assert.False(BassWasapiInitializationPolicy.TryGetBufferFrameCount(bytes, out uint frames));
        Assert.Equal(0u, frames);
    }

    [Theory]
    [InlineData(BASSWASAPI.BASS_DEVICE_ENABLED, true)]
    [InlineData(BASSWASAPI.BASS_DEVICE_ENABLED | BASSWASAPI.BASS_DEVICE_DEFAULT, true)]
    [InlineData(0u, false)]
    [InlineData(BASSWASAPI.BASS_DEVICE_INPUT | BASSWASAPI.BASS_DEVICE_ENABLED, false)]
    [InlineData(BASSWASAPI.BASS_DEVICE_LOOPBACK | BASSWASAPI.BASS_DEVICE_ENABLED, false)]
    [InlineData(BASSWASAPI.BASS_DEVICE_UNPLUGGED | BASSWASAPI.BASS_DEVICE_ENABLED, false)]
    [InlineData(BASSWASAPI.BASS_DEVICE_DISABLED | BASSWASAPI.BASS_DEVICE_ENABLED, false)]
    public void DeviceEnumerationIncludesOnlyEnabledPresentNonLoopbackOutputs(uint flags, bool expected)
    {
        Assert.Equal(expected, BassWasapiOutputDeviceFactory.IsEligibleOutputDevice(flags));
    }

    [Fact]
    public void InvalidDeviceIndexIsTheOnlySuccessfulEnumerationTerminator()
    {
        BassWasapiOutputDeviceFactory.ValidateEnumerationTerminalError(
            Midora.NativeInterops.Bass.BASS.BASS_ERROR_DEVICE);

        MidoraAudioDeviceException failure = Assert.Throws<MidoraAudioDeviceException>(() =>
            BassWasapiOutputDeviceFactory.ValidateEnumerationTerminalError(
                BASSWASAPI.BASS_ERROR_WASAPI));
        Assert.Contains(BASSWASAPI.BASS_ERROR_WASAPI.ToString(), failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    public void WasapiEnumerationRequiresExplicitUtf8DeviceInformationMode(uint configuredValue)
    {
        Assert.Throws<MidoraAudioDeviceException>(() =>
            BassWasapiOutputDeviceFactory.ValidateUtf8DeviceInformationMode(configuredValue));
    }

    [Fact]
    public void Utf8DeviceInformationModeAcceptsEnabledValue()
    {
        BassWasapiOutputDeviceFactory.ValidateUtf8DeviceInformationMode(1);
        BassWasapiOutputDeviceFactory.ValidateUtf8DeviceInformationMode(2);
    }

    [Fact]
    public void CallbackPullContractForbidsAdvancingMusicDuringBuffering()
    {
        Assert.True(BassWasapiOutputDevice.IsValidCallbackPullResult(
            AudioPullResult.Buffering(), 256));
        Assert.True(BassWasapiOutputDevice.IsValidCallbackPullResult(
            AudioPullResult.Continue(256), 256));
        Assert.True(BassWasapiOutputDevice.IsValidCallbackPullResult(
            AudioPullResult.EndOfStream(17), 256));
        Assert.False(BassWasapiOutputDevice.IsValidCallbackPullResult(
            new AudioPullResult(1, AudioPullStatus.Buffering), 256));
        Assert.False(BassWasapiOutputDevice.IsValidCallbackPullResult(
            AudioPullResult.Continue(-1), 256));
        Assert.False(BassWasapiOutputDevice.IsValidCallbackPullResult(
            AudioPullResult.Continue(257), 256));
        Assert.False(BassWasapiOutputDevice.IsValidCallbackPullResult(
            AudioPullResult.Continue(0), 256));
        Assert.False(BassWasapiOutputDevice.IsValidCallbackPullResult(
            new AudioPullResult(0, (AudioPullStatus)byte.MaxValue), 256));
        Assert.True(BassWasapiOutputDevice.IsValidCallbackPullResult(
            AudioPullResult.Continue(0), 0));
    }

    [Theory]
    [InlineData(1_000, 800, 900)]
    [InlineData(100, 1_600, 0)]
    [InlineData(0, 0, 0)]
    public void AudibleFrontierExcludesFramesStillBufferedByWasapi(
        long submittedFrames,
        uint bufferedBytes,
        long expectedAudibleFrames)
    {
        Assert.Equal(
            expectedAudibleFrames,
            BassWasapiOutputDevice.CalculateAudibleFrameCount(
                submittedFrames,
                bufferedBytes));
    }

    [Fact]
    public void OnlySelectedDeviceDisableOrFailureIsADeviceLoss()
    {
        Assert.True(BassWasapiOutputDevice.IsDeviceLossNotification(
            BASSWASAPI.BASS_WASAPI_NOTIFY_DISABLED, 7, 7));
        Assert.True(BassWasapiOutputDevice.IsDeviceLossNotification(
            BASSWASAPI.BASS_WASAPI_NOTIFY_FAIL, 7, 7));
        Assert.False(BassWasapiOutputDevice.IsDeviceLossNotification(
            BASSWASAPI.BASS_WASAPI_NOTIFY_DISABLED, 8, 7));
        Assert.False(BassWasapiOutputDevice.IsDeviceLossNotification(
            BASSWASAPI.BASS_WASAPI_NOTIFY_FAIL, 8, 7));
        Assert.False(BassWasapiOutputDevice.IsDeviceLossNotification(
            BASSWASAPI.BASS_WASAPI_NOTIFY_ENABLED, 7, 7));
        Assert.False(BassWasapiOutputDevice.IsDeviceLossNotification(
            BASSWASAPI.BASS_WASAPI_NOTIFY_DEFOUTPUT, 7, 7));
    }

    [Fact]
    public void DefaultMappingChangeInvalidatesOnlySystemDefaultSelection()
    {
        Assert.True(BassWasapiOutputDevice.IsOutputSelectionInvalidated(
            followsSystemDefault: true,
            defaultDeviceChanged: true,
            deviceLost: false));
        Assert.False(BassWasapiOutputDevice.IsOutputSelectionInvalidated(
            followsSystemDefault: false,
            defaultDeviceChanged: true,
            deviceLost: false));
        Assert.False(BassWasapiOutputDevice.IsOutputSelectionInvalidated(
            followsSystemDefault: true,
            defaultDeviceChanged: false,
            deviceLost: false));
        Assert.True(BassWasapiOutputDevice.IsOutputSelectionInvalidated(
            followsSystemDefault: false,
            defaultDeviceChanged: false,
            deviceLost: true));
    }
}
