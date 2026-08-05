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
}
