namespace Midora.Audio.Bass;

public sealed record class BassMidiRendererSettings
{
    public BassMidiRendererSettings(
        BassMidiNoteOffPolicy noteOffPolicy,
        BassMidiInterpolation interpolation,
        BassMidiSampleLoading sampleLoading,
        int maximumVoices,
        float cpuLimitPercent,
        int maximumWorkFrameCount)
    {
        if (!Enum.IsDefined(noteOffPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(noteOffPolicy));
        }

        if (!Enum.IsDefined(interpolation))
        {
            throw new ArgumentOutOfRangeException(nameof(interpolation));
        }

        if (!Enum.IsDefined(sampleLoading))
        {
            throw new ArgumentOutOfRangeException(nameof(sampleLoading));
        }

        if (maximumVoices < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumVoices));
        }

        if (!float.IsFinite(cpuLimitPercent) || cpuLimitPercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(cpuLimitPercent));
        }

        if (maximumWorkFrameCount is < 16 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumWorkFrameCount));
        }

        NoteOffPolicy = noteOffPolicy;
        Interpolation = interpolation;
        SampleLoading = sampleLoading;
        MaximumVoices = maximumVoices;
        CpuLimitPercent = cpuLimitPercent;
        MaximumWorkFrameCount = maximumWorkFrameCount;
    }

    public BassMidiNoteOffPolicy NoteOffPolicy { get; }

    public BassMidiInterpolation Interpolation { get; }

    public BassMidiSampleLoading SampleLoading { get; }

    public int MaximumVoices { get; }

    public float CpuLimitPercent { get; }

    public int MaximumWorkFrameCount { get; }
}

public enum BassMidiNoteOffPolicy : byte
{
    ReleaseAllMatchingNotes,
    ReleaseOldestMatchingNote
}

public enum BassMidiInterpolation : byte
{
    BassDefault,
    Sinc
}

public enum BassMidiSampleLoading : byte
{
    OnDemand,
    PreloadAllReferencedSamples
}
