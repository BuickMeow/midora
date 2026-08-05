using Midora.Audio.Bass.Internals;

namespace Midora.Audio.Bass.Tests;

public sealed class StereoPeakLimiterTests
{
    [Fact]
    public void LinksStereoAndNeverExceedsCeiling()
    {
        StereoPeakLimiter limiter = new(48_000, 1f, 50f);

        Assert.True(limiter.Process(2f, 0.5f, out float left, out float right));

        Assert.Equal(1f, left);
        Assert.Equal(0.25f, right);
        Assert.Equal(0.5f, limiter.Gain);
    }

    [Fact]
    public void RejectsNonFiniteInputAndResetRestoresUnity()
    {
        StereoPeakLimiter limiter = new(48_000, 1f, 50f);
        _ = limiter.Process(2f, 2f, out _, out _);
        limiter.Reset();

        Assert.Equal(1f, limiter.Gain);
        Assert.False(limiter.Process(float.NaN, 0, out _, out _));
        Assert.False(limiter.Process(0, float.PositiveInfinity, out _, out _));
    }

    [Fact]
    public void ReleaseUsesFiftyMillisecondTimeConstantAtEverySampleRate()
    {
        StereoPeakLimiter limiter48K = new(48_000, 1f, 50f);
        StereoPeakLimiter limiter96K = new(96_000, 1f, 50f);
        _ = limiter48K.Process(2f, 2f, out _, out _);
        _ = limiter96K.Process(2f, 2f, out _, out _);

        for (int frame = 0; frame < 2_400; frame++)
        {
            _ = limiter48K.Process(0, 0, out _, out _);
        }
        for (int frame = 0; frame < 4_800; frame++)
        {
            _ = limiter96K.Process(0, 0, out _, out _);
        }

        float expected = 1f - (0.5f / MathF.E);
        Assert.InRange(limiter48K.Gain, expected - 0.0001f, expected + 0.0001f);
        Assert.InRange(limiter96K.Gain, expected - 0.0001f, expected + 0.0001f);
        Assert.InRange(MathF.Abs(limiter48K.Gain - limiter96K.Gain), 0, 0.0001f);
    }

    [Fact]
    public void LimiterV1SettingsAreFixedAndEnabled()
    {
        AudioMasterSettings settings = AudioMasterSettings.LimiterV1;

        Assert.Equal(1, AudioMasterSettings.LimiterAlgorithmVersion);
        Assert.Equal(-0.1f, settings.VolumeDecibels);
        Assert.Equal(1f, settings.LimiterCeiling);
        Assert.Equal(50f, settings.LimiterReleaseMilliseconds);
        Assert.True(settings.LimiterEnabled);
    }
}
