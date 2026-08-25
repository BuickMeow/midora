using Midora.Audio.Bass.Internals;

namespace Midora.Audio.Bass.Tests;

public sealed class StereoLookAheadLimiterTests
{
    [Fact]
    public void LinksStereoAndNeverExceedsCeiling()
    {
        StereoLookAheadLimiter limiter = CreateLimiter();
        float[] input = new float[(1 + limiter.RequiredFutureFrameCount) * 2];
        float[] output = new float[2];
        input[0] = 2f;
        input[1] = 0.5f;

        Assert.True(limiter.Process(input, 1, output));

        Assert.InRange(MathF.Abs(output[0]), 0, AudioMasterSettings.LimiterCeilingV2);
        Assert.Equal(output[0] * 0.25f, output[1], 5);
        Assert.Equal(AudioMasterSettings.LimiterCeilingV2 / 2f, limiter.Gain, 5);
    }

    [Fact]
    public void FourTimesInterpolationDetectsAnInterSampleOvershoot()
    {
        StereoLookAheadLimiter limiter = CreateLimiter();
        float[] input = new float[(2 + limiter.RequiredFutureFrameCount) * 2];
        float[] output = new float[2];
        input[0] = 1f;
        input[1] = 1f;
        input[2] = 1f;
        input[3] = 1f;

        Assert.True(limiter.Process(input, 1, output));

        Assert.True(limiter.Gain < AudioMasterSettings.LimiterCeilingV2);
        Assert.InRange(MathF.Abs(output[0]), 0, AudioMasterSettings.LimiterCeilingV2);
        Assert.InRange(MathF.Abs(output[1]), 0, AudioMasterSettings.LimiterCeilingV2);
    }

    [Fact]
    public void LookAheadRampsGainBeforeAnIsolatedPeak()
    {
        StereoLookAheadLimiter limiter = CreateLimiter(maximumOutputFrameCount: 256);
        int lookAheadFrames = limiter.LookAheadFrameCount;
        int outputFrameCount = lookAheadFrames + 1;
        float[] input = new float[(outputFrameCount + limiter.RequiredFutureFrameCount) * 2];
        float[] output = new float[outputFrameCount * 2];
        for (int frame = 0; frame < outputFrameCount; frame++)
        {
            input[frame * 2] = 0.25f;
            input[(frame * 2) + 1] = 0.25f;
        }
        input[lookAheadFrames * 2] = 2f;
        input[(lookAheadFrames * 2) + 1] = 2f;

        Assert.True(limiter.Process(input, outputFrameCount, output));

        Assert.InRange(output[0], 0.249f, 0.251f);
        Assert.True(output[(lookAheadFrames - 1) * 2] < 0.2f);
        Assert.InRange(
            MathF.Abs(output[lookAheadFrames * 2]),
            0,
            AudioMasterSettings.LimiterCeilingV2);
    }

    [Fact]
    public void EveryFuturePeakContributesItsOwnAttackConstraint()
    {
        StereoLookAheadLimiter limiter = CreateLimiter(maximumOutputFrameCount: 1);
        int lookAheadFrames = limiter.LookAheadFrameCount;
        float[] input = new float[(1 + limiter.RequiredFutureFrameCount) * 2];
        float[] output = new float[2];

        // The distant peak is the largest raw peak, but at the edge of the
        // look-ahead window it does not yet require attenuation. The closer
        // peak must still begin its own attack at the current frame.
        input[0] = 0.5f;
        input[1] = 0.5f;
        const int closePeakFrame = 20;
        input[closePeakFrame * 2] = 1.25f;
        input[(closePeakFrame * 2) + 1] = 1.25f;
        input[lookAheadFrames * 2] = 100f;
        input[(lookAheadFrames * 2) + 1] = 100f;

        Assert.True(limiter.Process(input, 1, output));

        float closePeakRequiredGain = AudioMasterSettings.LimiterCeilingV2 / 1.25f;
        float expectedMaximumGain = closePeakRequiredGain
            + ((1f - closePeakRequiredGain) * closePeakFrame / lookAheadFrames);
        Assert.InRange(limiter.Gain, 0, expectedMaximumGain + 1e-5f);
    }

    [Fact]
    public void ProcessingIsIndependentOfCallerBlockSize()
    {
        const int frameCount = 4_096;
        float[] input = new float[frameCount * 2];
        for (int frame = 0; frame < frameCount; frame++)
        {
            input[frame * 2] = MathF.Sin(frame * 0.019f) * (frame % 257 == 0 ? 2.5f : 0.8f);
            input[(frame * 2) + 1] = MathF.Cos(frame * 0.013f) * (frame % 389 == 0 ? 2f : 0.7f);
        }

        float[] output37 = ProcessInBlocks(input, 37);
        float[] output256 = ProcessInBlocks(input, 256);

        for (int sample = 0; sample < output37.Length; sample++)
        {
            Assert.Equal(output256[sample], output37[sample], 5);
        }
    }

    [Fact]
    public void ProcessingDoesNotAllocateAfterConstruction()
    {
        StereoLookAheadLimiter limiter = CreateLimiter();
        int inputFrameCount = 256 + limiter.RequiredFutureFrameCount;
        float[] input = new float[inputFrameCount * 2];
        float[] output = new float[256 * 2];
        for (int sample = 0; sample < input.Length; sample++)
        {
            input[sample] = MathF.Sin(sample * 0.031f) * 1.5f;
        }
        Assert.True(limiter.Process(input, 256, output));
        limiter.Reset();

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool succeeded = limiter.Process(input, 256, output);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(succeeded);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void AdversarialProgramDoesNotProduceAnInterSamplePeakAboveUnity()
    {
        const int frameCount = 8_192;
        float[] input = new float[frameCount * 2];
        for (int frame = 0; frame < frameCount; frame++)
        {
            float burst = frame % 701 is >= 330 and < 350 ? 4f : 1f;
            input[frame * 2] = MathF.Sin(frame * 2.71f) * burst;
            input[(frame * 2) + 1] = MathF.Cos(frame * 2.37f) * burst * 0.83f;
        }

        float[] output = ProcessInBlocks(input, 37);
        float measuredPeak = MeasureFourTimesInterSamplePeak(output);

        Assert.InRange(
            measuredPeak,
            0,
            1f);
    }

    [Fact]
    public void RejectsNonFiniteInputAndResetRestoresUnity()
    {
        StereoLookAheadLimiter limiter = CreateLimiter();
        float[] input = new float[(1 + limiter.RequiredFutureFrameCount) * 2];
        float[] output = new float[2];
        input[0] = 2f;
        input[1] = 2f;
        Assert.True(limiter.Process(input, 1, output));
        limiter.Reset();

        Assert.Equal(1f, limiter.Gain);
        input[0] = float.NaN;
        Assert.False(limiter.Process(input, 1, output));
        input[0] = 0;
        input[1] = float.PositiveInfinity;
        Assert.False(limiter.Process(input, 1, output));
    }

    [Fact]
    public void LimiterSettingsAreFixedAndEnabled()
    {
        AudioMasterSettings settings = AudioMasterSettings.LimiterV2;

        Assert.Equal(2, AudioMasterSettings.LimiterAlgorithmVersion);
        Assert.Equal(-0.1f, settings.VolumeDecibels);
        Assert.Equal(0.8912509f, settings.LimiterCeiling);
        Assert.Equal(5f, AudioMasterSettings.LimiterLookAheadMilliseconds);
        Assert.Equal(10f, AudioMasterSettings.LimiterHoldMilliseconds);
        Assert.Equal(100f, settings.LimiterReleaseMilliseconds);
        Assert.True(settings.LimiterEnabled);
    }

    private static StereoLookAheadLimiter CreateLimiter(int maximumOutputFrameCount = 256) => new(
        48_000,
        AudioMasterSettings.LimiterCeilingV2,
        AudioMasterSettings.LimiterLookAheadMilliseconds,
        AudioMasterSettings.LimiterHoldMilliseconds,
        AudioMasterSettings.LimiterReleaseMillisecondsV2,
        maximumOutputFrameCount);

    private static float[] ProcessInBlocks(float[] input, int blockFrameCount)
    {
        StereoLookAheadLimiter limiter = CreateLimiter(blockFrameCount);
        float[] output = new float[input.Length];
        int totalFrameCount = input.Length / 2;
        int position = 0;
        while (position < totalFrameCount)
        {
            int outputFrameCount = Math.Min(blockFrameCount, totalFrameCount - position);
            int inputFrameCount = Math.Min(
                totalFrameCount - position,
                outputFrameCount + limiter.RequiredFutureFrameCount);
            Assert.True(limiter.Process(
                input.AsSpan(position * 2, inputFrameCount * 2),
                outputFrameCount,
                output.AsSpan(position * 2, outputFrameCount * 2)));
            position += outputFrameCount;
        }
        return output;
    }

    private static float MeasureFourTimesInterSamplePeak(float[] interleaved)
    {
        const int firstTap = -7;
        const int tapCount = 16;
        int frameCount = interleaved.Length / 2;
        float peak = 0;
        for (int frame = 0; frame < frameCount; frame++)
        {
            peak = MathF.Max(
                peak,
                MathF.Max(
                    MathF.Abs(interleaved[frame * 2]),
                    MathF.Abs(interleaved[(frame * 2) + 1])));
            for (int phaseIndex = 1; phaseIndex < 4; phaseIndex++)
            {
                double phase = phaseIndex / 4d;
                double coefficientSum = 0;
                double left = 0;
                double right = 0;
                for (int tapIndex = 0; tapIndex < tapCount; tapIndex++)
                {
                    int tap = firstTap + tapIndex;
                    double distance = phase - tap;
                    double coefficient = Sinc(distance) * Sinc(distance / 8d);
                    coefficientSum += coefficient;
                    int sourceFrame = frame + tap;
                    if (sourceFrame is >= 0 && sourceFrame < frameCount)
                    {
                        left += interleaved[sourceFrame * 2] * coefficient;
                        right += interleaved[(sourceFrame * 2) + 1] * coefficient;
                    }
                }
                peak = MathF.Max(
                    peak,
                    checked((float)(Math.Max(Math.Abs(left), Math.Abs(right))
                        / coefficientSum)));
            }
        }
        return peak;
    }

    private static double Sinc(double value)
    {
        if (Math.Abs(value) < 1e-12)
        {
            return 1;
        }
        double radians = Math.PI * value;
        return Math.Sin(radians) / radians;
    }
}
