namespace Midora.Audio.Bass;

public sealed record class BassMidiRendererSettings
{
    public BassMidiRendererSettings(
        int maximumSampleVoiceCount,
        int maximumWorkFrameCount)
    {
        if (maximumSampleVoiceCount is < 1 or > BassMidiPolyphonyConfiguration.MaximumSampleVoiceCount)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSampleVoiceCount));
        }

        if (maximumWorkFrameCount is < 16 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumWorkFrameCount));
        }

        MaximumSampleVoiceCount = maximumSampleVoiceCount;
        MaximumWorkFrameCount = maximumWorkFrameCount;
    }

    public int MaximumSampleVoiceCount { get; }

    public int MaximumWorkFrameCount { get; }
}

public sealed record class BassMidiPolyphonyConfiguration
{
    public const int DefaultMaximumSampleVoiceCount = 750;

    // BASS_ATTRIB_MIDI_VOICES is transported as float. Keep every accepted integer exact.
    public const int MaximumSampleVoiceCount = 1 << 24;

    public BassMidiPolyphonyConfiguration(
        int realtimeMaximumSampleVoiceCount = DefaultMaximumSampleVoiceCount,
        int offlineMaximumSampleVoiceCount = DefaultMaximumSampleVoiceCount)
    {
        Validate(realtimeMaximumSampleVoiceCount, nameof(realtimeMaximumSampleVoiceCount));
        Validate(offlineMaximumSampleVoiceCount, nameof(offlineMaximumSampleVoiceCount));
        RealtimeMaximumSampleVoiceCount = realtimeMaximumSampleVoiceCount;
        OfflineMaximumSampleVoiceCount = offlineMaximumSampleVoiceCount;
    }

    public static BassMidiPolyphonyConfiguration Default { get; } = new();

    public int RealtimeMaximumSampleVoiceCount { get; }

    public int OfflineMaximumSampleVoiceCount { get; }

    public BassMidiRendererSettings CreateRealtimeRendererSettings(int maximumWorkFrameCount) =>
        new(RealtimeMaximumSampleVoiceCount, maximumWorkFrameCount);

    public BassMidiRendererSettings CreateOfflineRendererSettings(int maximumWorkFrameCount) =>
        new(OfflineMaximumSampleVoiceCount, maximumWorkFrameCount);

    private static void Validate(int value, string parameterName)
    {
        if (value is < 1 or > MaximumSampleVoiceCount)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
