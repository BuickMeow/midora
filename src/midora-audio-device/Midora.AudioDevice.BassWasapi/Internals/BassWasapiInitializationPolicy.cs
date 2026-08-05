using Midora.NativeInterops.BassWasapi;

namespace Midora.AudioDevice.BassWasapi.Internals;

internal static class BassWasapiInitializationPolicy
{
    public const int RequestedFrequency = 0;
    public const int RequestedChannelCount = 2;
    public const int BytesPerFrame = RequestedChannelCount * sizeof(float);
    public const uint InitializationFlags = BASSWASAPI.BASS_WASAPI_EVENT;
    public const float RequestedPeriodSeconds = 0f;

    public static bool IsSupportedRuntimeFormat(
        uint initializationFlags,
        uint sampleRate,
        uint channelCount,
        uint sampleFormat) =>
        (initializationFlags & BASSWASAPI.BASS_WASAPI_EVENT) != 0
        && (initializationFlags & BASSWASAPI.BASS_WASAPI_EXCLUSIVE) == 0
        && sampleRate is > 0 and <= int.MaxValue
        && channelCount == RequestedChannelCount
        && sampleFormat == BASSWASAPI.BASS_WASAPI_FORMAT_FLOAT;

    public static bool TryGetBufferFrameCount(uint bufferByteCount, out uint frameCount)
    {
        if (bufferByteCount == 0 || bufferByteCount % BytesPerFrame != 0)
        {
            frameCount = 0;
            return false;
        }

        frameCount = bufferByteCount / BytesPerFrame;
        return true;
    }
}
