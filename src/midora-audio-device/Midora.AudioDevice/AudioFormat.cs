namespace Midora.AudioDevice;

public readonly record struct AudioFormat(
    int SampleRate,
    int ChannelCount,
    AudioSampleFormat SampleFormat)
{
    public int BytesPerSample =>
        SampleFormat switch
        {
            AudioSampleFormat.Int16 => 2,
            AudioSampleFormat.Int24 => 3,
            AudioSampleFormat.Int32 => 4,
            AudioSampleFormat.Float32 => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(SampleFormat))
        };

    public int BytesPerFrame => checked(ChannelCount * BytesPerSample);

    public void Validate()
    {
        if (SampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SampleRate));
        }

        if (ChannelCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ChannelCount));
        }

        _ = BytesPerFrame;
    }
}

public enum AudioSampleFormat
{
    Int16,
    Int24,
    Int32,
    Float32
}
