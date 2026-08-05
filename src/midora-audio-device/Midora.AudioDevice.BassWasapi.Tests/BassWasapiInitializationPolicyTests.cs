using Midora.AudioDevice.BassWasapi.Internals;
using Midora.AudioDevice.BassWasapi.Settings;
using Midora.NativeInterops.BassWasapi;

namespace Midora.AudioDevice.BassWasapi.Tests;

public sealed class BassWasapiInitializationPolicyTests
{
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
}
