namespace Midora.Audio.Bass;

public sealed record class AudioMasterSettings
{
    public const int LimiterAlgorithmVersion = 1;

    public AudioMasterSettings(
        float volumeDecibels,
        float limiterCeiling,
        float limiterReleaseMilliseconds,
        bool limiterEnabled = true)
    {
        if (!float.IsFinite(volumeDecibels) || volumeDecibels > 0)
        {
            throw new ArgumentOutOfRangeException(nameof(volumeDecibels));
        }

        if (!float.IsFinite(limiterCeiling) || limiterCeiling is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limiterCeiling));
        }

        if (!float.IsFinite(limiterReleaseMilliseconds) || limiterReleaseMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limiterReleaseMilliseconds));
        }

        VolumeDecibels = volumeDecibels;
        LimiterCeiling = limiterCeiling;
        LimiterReleaseMilliseconds = limiterReleaseMilliseconds;
        LimiterEnabled = limiterEnabled;
    }

    public float VolumeDecibels { get; }

    public float LimiterCeiling { get; }

    public float LimiterReleaseMilliseconds { get; }

    public bool LimiterEnabled { get; }

    public static AudioMasterSettings LimiterV1 { get; } = new(-0.1f, 1f, 50f);
}
