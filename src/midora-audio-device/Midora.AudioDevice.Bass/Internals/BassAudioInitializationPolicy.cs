namespace Midora.AudioDevice.Bass.Internals;

/// <summary>
/// macOS-first BASS native output policy. Mirrors the pinned format rules of the dormant
/// Windows WASAPI path: stereo interleaved float32 at the device's actual rate.
/// </summary>
internal static class BassAudioInitializationPolicy
{
    // macOS spike (2026-09-21): BASS_Init with freq 0 fails with BASS_ERROR_FORMAT on macOS,
    // and BASS follows the device's native rate regardless of the requested value. The actual
    // rate must always be read back from BASS_GetInfo().freq after initialization.
    public const uint RequestedFrequency = 48_000;
    public const int RequestedChannelCount = 2;
    public const uint InitializationFlags = 0;
    public const uint PositionModeByte = 0;
    public const uint MinimumUpdatePeriodMilliseconds = 5;
    public const uint MinimumBufferMilliseconds = 100;

    public static bool IsSupportedRuntimeFormat(AudioFormat format) =>
        format.ChannelCount == RequestedChannelCount
        && format.SampleFormat == AudioSampleFormat.Float32
        && format.SampleRate is > 0 and <= int.MaxValue;

    public static bool TryGetFrameCount(uint byteCount, int bytesPerFrame, out int frameCount)
    {
        if (bytesPerFrame <= 0 || byteCount == 0 || byteCount % (uint)bytesPerFrame != 0)
        {
            frameCount = 0;
            return false;
        }

        frameCount = (int)(byteCount / (uint)bytesPerFrame);
        return true;
    }

    public static (uint UpdatePeriod, uint Buffer) ResolveBufferConfiguration(int requestedMilliseconds)
    {
        uint period = (uint)Math.Max(requestedMilliseconds, (int)MinimumUpdatePeriodMilliseconds);
        uint buffer = Math.Max(period * 2, MinimumBufferMilliseconds);
        return (period, buffer);
    }
}
