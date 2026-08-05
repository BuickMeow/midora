using Midora.Audio;

namespace Midora.Audio.Bass.Tests;

public sealed class InitialReleaseAudioRuntimePolicyTests
{
    [Fact]
    public void InitialReleaseUsesFixedTwoHundredFiftySixFrameWorkBlock()
    {
        Assert.Equal(256, InitialReleaseAudioRuntimePolicy.WorkFrameCount);
    }

    [Theory]
    [InlineData(8_000, 20, 160)]
    [InlineData(44_100, 101, 4_455)]
    [InlineData(48_000, 100, 4_800)]
    [InlineData(192_000, 2_000, 384_000)]
    public void RingCapacityRoundsRequestedDurationUpToWholeFrames(
        int sampleRate,
        int milliseconds,
        int expectedFrames)
    {
        Assert.Equal(expectedFrames,
            InitialReleaseAudioRuntimePolicy.BufferMillisecondsToFrameCapacity(sampleRate, milliseconds));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(48_000, 0)]
    [InlineData(-1, 100)]
    [InlineData(48_000, -1)]
    public void RingCapacityRejectsNonPositiveInputs(int sampleRate, int milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            InitialReleaseAudioRuntimePolicy.BufferMillisecondsToFrameCapacity(sampleRate, milliseconds));
    }

    [Theory]
    [InlineData(160, 160)]
    [InlineData(256, 256)]
    [InlineData(4_800, 256)]
    public void ProducerWorkBlockUsesFormalMaximumWithoutExpandingRing(
        int capacityFrames,
        int expectedWorkFrames)
    {
        Assert.Equal(expectedWorkFrames,
            InitialReleaseAudioRuntimePolicy.WorkFramesForRingCapacity(capacityFrames));
    }
}
