namespace Midora.Audio.Bass;

public sealed record class BassMidiRendererSettings
{
    public BassMidiRendererSettings(
        int maximumSampleVoicesPerUnitStream,
        int maximumWorkFrameCount)
    {
        if (maximumSampleVoicesPerUnitStream is < 1
            or > BassMidiPolyphonyConfiguration.MaximumSampleVoicesPerUnitStream)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSampleVoicesPerUnitStream));
        }

        if (maximumWorkFrameCount is < 16 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumWorkFrameCount));
        }

        MaximumSampleVoicesPerUnitStream = maximumSampleVoicesPerUnitStream;
        MaximumWorkFrameCount = maximumWorkFrameCount;
    }

    public int MaximumSampleVoicesPerUnitStream { get; }

    public int MaximumWorkFrameCount { get; }
}

public sealed record class BassMidiPolyphonyConfiguration
{
    public const int DefaultMaximumSampleVoicesPerUnitStream = 500;

    // BASS_ATTRIB_MIDI_VOICES is transported as float. Keep every accepted integer exact.
    public const int MaximumSampleVoicesPerUnitStream = 1 << 24;

    public BassMidiPolyphonyConfiguration(
        int realtimeMaximumSampleVoicesPerUnitStream = DefaultMaximumSampleVoicesPerUnitStream,
        int offlineMaximumSampleVoicesPerUnitStream = DefaultMaximumSampleVoicesPerUnitStream)
    {
        Validate(
            realtimeMaximumSampleVoicesPerUnitStream,
            nameof(realtimeMaximumSampleVoicesPerUnitStream));
        Validate(
            offlineMaximumSampleVoicesPerUnitStream,
            nameof(offlineMaximumSampleVoicesPerUnitStream));
        RealtimeMaximumSampleVoicesPerUnitStream = realtimeMaximumSampleVoicesPerUnitStream;
        OfflineMaximumSampleVoicesPerUnitStream = offlineMaximumSampleVoicesPerUnitStream;
    }

    public static BassMidiPolyphonyConfiguration Default { get; } = new();

    public int RealtimeMaximumSampleVoicesPerUnitStream { get; }

    public int OfflineMaximumSampleVoicesPerUnitStream { get; }

    public BassMidiRendererSettings CreateRealtimeRendererSettings(int maximumWorkFrameCount) =>
        new(RealtimeMaximumSampleVoicesPerUnitStream, maximumWorkFrameCount);

    public BassMidiRendererSettings CreateOfflineRendererSettings(int maximumWorkFrameCount) =>
        new(OfflineMaximumSampleVoicesPerUnitStream, maximumWorkFrameCount);

    private static void Validate(int value, string parameterName)
    {
        if (value is < 1 or > MaximumSampleVoicesPerUnitStream)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
