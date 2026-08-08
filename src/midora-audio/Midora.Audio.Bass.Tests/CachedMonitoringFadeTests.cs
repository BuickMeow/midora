namespace Midora.Audio.Bass.Tests;

public sealed class CachedMonitoringFadeTests
{
    [Fact]
    public void FourMillisecondCachedMuteFadePreservesStartAndReachesSilence()
    {
        const int fadeFrames = 192;
        float[] samples = Enumerable.Repeat(1f, (fadeFrames + 16) * 2).ToArray();

        int remaining = BassMidiRenderer.ApplyCachedMuteFade(
            samples,
            fadeFrames,
            fadeFrames);

        Assert.Equal(0, remaining);
        Assert.Equal(1f, samples[0]);
        Assert.Equal(1f, samples[1]);
        Assert.Equal(1f / fadeFrames, samples[(fadeFrames - 1) * 2], 6);
        Assert.Equal(0f, samples[fadeFrames * 2]);
        Assert.Equal(0f, samples[^1]);
    }

    [Fact]
    public void CachedMuteFadeIsIndependentOfRenderBlockPartitioning()
    {
        const int fadeFrames = 192;
        float[] whole = Enumerable.Repeat(0.75f, 256 * 2).ToArray();
        float[] partitioned = whole.ToArray();

        _ = BassMidiRenderer.ApplyCachedMuteFade(whole, fadeFrames, fadeFrames);
        int remaining = BassMidiRenderer.ApplyCachedMuteFade(
            partitioned.AsSpan(0, 73 * 2),
            fadeFrames,
            fadeFrames);
        remaining = BassMidiRenderer.ApplyCachedMuteFade(
            partitioned.AsSpan(73 * 2),
            remaining,
            fadeFrames);

        Assert.Equal(0, remaining);
        Assert.Equal(whole, partitioned);
    }
}
