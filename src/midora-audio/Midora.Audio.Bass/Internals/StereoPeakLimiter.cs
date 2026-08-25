using System.Runtime.CompilerServices;
using System.Numerics;

namespace Midora.Audio.Bass.Internals;

/// <summary>
/// Stereo-linked look-ahead limiter used by the final realtime/offline output bus.
/// The caller supplies current output frames followed by the future analysis
/// horizon, so the limiter adds no output frames and does not shift the timeline.
/// </summary>
internal sealed unsafe class StereoLookAheadLimiter
{
    private const int OversamplingFactor = 4;
    private const int InterpolationTapCount = 16;
    private const int InterpolationHistoryFrameCount = 7;
    private const int InterpolationFutureFrameCount = 8;
    private const int FirstInterpolationTap = -InterpolationHistoryFrameCount;
    private readonly float _ceiling;
    private readonly float _releaseCoefficient;
    private readonly int _lookAheadFrameCount;
    private readonly int _holdFrameCount;
    private readonly float[] _peakByFrame;
    private readonly float[] _attackSlopeByFrame;
    private readonly float[] _attackInterceptByFrame;
    private readonly float[] _interpolationCoefficients;
    private readonly float[] _historyLeft = new float[InterpolationHistoryFrameCount];
    private readonly float[] _historyRight = new float[InterpolationHistoryFrameCount];
    private float _gain = 1f;
    private int _holdFramesRemaining;

    public StereoLookAheadLimiter(
        int sampleRate,
        float ceiling,
        float lookAheadMilliseconds,
        float holdMilliseconds,
        float releaseMilliseconds,
        int maximumOutputFrameCount)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }
        if (!float.IsFinite(ceiling) || ceiling is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ceiling));
        }
        if (!float.IsFinite(lookAheadMilliseconds) || lookAheadMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lookAheadMilliseconds));
        }
        if (!float.IsFinite(holdMilliseconds) || holdMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(holdMilliseconds));
        }
        if (!float.IsFinite(releaseMilliseconds) || releaseMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(releaseMilliseconds));
        }
        if (maximumOutputFrameCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumOutputFrameCount));
        }

        _ceiling = ceiling;
        _lookAheadFrameCount = Math.Max(
            1,
            checked((int)Math.Ceiling(sampleRate * lookAheadMilliseconds / 1_000d)));
        _holdFrameCount = Math.Max(
            0,
            checked((int)Math.Ceiling(sampleRate * holdMilliseconds / 1_000d)));
        float releaseSamples = sampleRate * releaseMilliseconds / 1_000f;
        _releaseCoefficient = MathF.Exp(-1f / releaseSamples);

        int maximumAnalysisFrameCount = checked(
            maximumOutputFrameCount + RequiredFutureFrameCount);
        _peakByFrame = new float[maximumAnalysisFrameCount];
        _attackSlopeByFrame = new float[maximumAnalysisFrameCount];
        _attackInterceptByFrame = new float[maximumAnalysisFrameCount];
        _interpolationCoefficients = CreateInterpolationCoefficients();
    }

    public int RequiredFutureFrameCount => checked(
        _lookAheadFrameCount + InterpolationFutureFrameCount);

    public int LookAheadFrameCount => _lookAheadFrameCount;

    public float Gain => _gain;

    public void Reset()
    {
        _gain = 1f;
        _holdFramesRemaining = 0;
        Array.Clear(_historyLeft);
        Array.Clear(_historyRight);
    }

    public bool Process(
        ReadOnlySpan<float> interleavedInput,
        int outputFrameCount,
        Span<float> interleavedOutput)
    {
        if ((interleavedInput.Length & 1) != 0
            || outputFrameCount < 0
            || outputFrameCount > interleavedInput.Length / 2
            || interleavedOutput.Length < checked(outputFrameCount * 2))
        {
            throw new ArgumentOutOfRangeException(nameof(outputFrameCount));
        }

        fixed (float* input = interleavedInput)
        fixed (float* output = interleavedOutput)
        {
            return Process(input, interleavedInput.Length / 2, outputFrameCount, output);
        }
    }

    public bool Process(
        float* interleavedInput,
        int inputFrameCount,
        int outputFrameCount,
        float* interleavedOutput)
    {
        if (interleavedInput == null
            || interleavedOutput == null
            || inputFrameCount < 0
            || inputFrameCount > _peakByFrame.Length
            || outputFrameCount < 0
            || outputFrameCount > inputFrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(inputFrameCount));
        }
        if (outputFrameCount == 0)
        {
            return true;
        }

        for (int frameIndex = 0; frameIndex < inputFrameCount; frameIndex++)
        {
            float left = interleavedInput[frameIndex * 2];
            float right = interleavedInput[(frameIndex * 2) + 1];
            if (!float.IsFinite(left) || !float.IsFinite(right))
            {
                return false;
            }

            float peak = MathF.Max(MathF.Abs(left), MathF.Abs(right));
            for (int phaseIndex = 0; phaseIndex < OversamplingFactor - 1; phaseIndex++)
            {
                float interpolatedLeft = 0;
                float interpolatedRight = 0;
                int coefficientOffset = phaseIndex * InterpolationTapCount;
                for (int tapIndex = 0; tapIndex < InterpolationTapCount; tapIndex++)
                {
                    int relativeFrameIndex = frameIndex + FirstInterpolationTap + tapIndex;
                    float coefficient = _interpolationCoefficients[coefficientOffset + tapIndex];
                    if (relativeFrameIndex >= 0)
                    {
                        if (relativeFrameIndex < inputFrameCount)
                        {
                            interpolatedLeft += interleavedInput[relativeFrameIndex * 2] * coefficient;
                            interpolatedRight += interleavedInput[(relativeFrameIndex * 2) + 1] * coefficient;
                        }
                    }
                    else if (relativeFrameIndex >= -InterpolationHistoryFrameCount)
                    {
                        int historyIndex = InterpolationHistoryFrameCount + relativeFrameIndex;
                        interpolatedLeft += _historyLeft[historyIndex] * coefficient;
                        interpolatedRight += _historyRight[historyIndex] * coefficient;
                    }
                }
                peak = MathF.Max(
                    peak,
                    MathF.Max(MathF.Abs(interpolatedLeft), MathF.Abs(interpolatedRight)));
            }
            if (!float.IsFinite(peak))
            {
                return false;
            }
            _peakByFrame[frameIndex] = peak;
            float requiredGain = RequiredGain(peak);
            float attackSlope = (1f - requiredGain) / _lookAheadFrameCount;
            _attackSlopeByFrame[frameIndex] = attackSlope;
            _attackInterceptByFrame[frameIndex] = requiredGain
                + (frameIndex * attackSlope);
        }

        for (int frameIndex = 0; frameIndex < outputFrameCount; frameIndex++)
        {
            float targetGain = FindStrictestAttackGain(frameIndex, inputFrameCount);

            if (targetGain < _gain)
            {
                _gain = targetGain;
                _holdFramesRemaining = _holdFrameCount;
            }
            else if (_holdFramesRemaining > 0)
            {
                _holdFramesRemaining--;
            }
            else
            {
                float releasedGain = 1f - ((1f - _gain) * _releaseCoefficient);
                _gain = MathF.Min(targetGain, releasedGain);
            }

            int sampleIndex = frameIndex * 2;
            float outputLeft = interleavedInput[sampleIndex] * _gain;
            float outputRight = interleavedInput[sampleIndex + 1] * _gain;
            if (!float.IsFinite(outputLeft) || !float.IsFinite(outputRight))
            {
                return false;
            }
            interleavedOutput[sampleIndex] = outputLeft;
            interleavedOutput[sampleIndex + 1] = outputRight;
        }

        for (int frameIndex = 0; frameIndex < outputFrameCount; frameIndex++)
        {
            for (int historyIndex = 1;
                historyIndex < InterpolationHistoryFrameCount;
                historyIndex++)
            {
                _historyLeft[historyIndex - 1] = _historyLeft[historyIndex];
                _historyRight[historyIndex - 1] = _historyRight[historyIndex];
            }
            _historyLeft[^1] = interleavedInput[frameIndex * 2];
            _historyRight[^1] = interleavedInput[(frameIndex * 2) + 1];
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float RequiredGain(float peak) => peak > _ceiling ? _ceiling / peak : 1f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float FindStrictestAttackGain(int frameIndex, int inputFrameCount)
    {
        int endExclusive = Math.Min(
            inputFrameCount,
            checked(frameIndex + _lookAheadFrameCount + 1));
        int futureFrameIndex = frameIndex;
        int vectorWidth = Vector<float>.Count;
        Vector<float> frame = new(frameIndex);
        Vector<float> minimum = new(1f);
        int vectorEnd = endExclusive - vectorWidth;
        for (; futureFrameIndex <= vectorEnd; futureFrameIndex += vectorWidth)
        {
            Vector<float> intercept = new(_attackInterceptByFrame, futureFrameIndex);
            Vector<float> slope = new(_attackSlopeByFrame, futureFrameIndex);
            minimum = Vector.Min(minimum, intercept - (slope * frame));
        }

        float targetGain = 1f;
        for (int lane = 0; lane < vectorWidth; lane++)
        {
            targetGain = MathF.Min(targetGain, minimum[lane]);
        }
        for (; futureFrameIndex < endExclusive; futureFrameIndex++)
        {
            targetGain = MathF.Min(
                targetGain,
                _attackInterceptByFrame[futureFrameIndex]
                    - (frameIndex * _attackSlopeByFrame[futureFrameIndex]));
        }
        return Math.Clamp(targetGain, 0f, 1f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float[] CreateInterpolationCoefficients()
    {
        float[] coefficients = new float[(OversamplingFactor - 1) * InterpolationTapCount];
        for (int phaseIndex = 0; phaseIndex < OversamplingFactor - 1; phaseIndex++)
        {
            double phase = (phaseIndex + 1d) / OversamplingFactor;
            double sum = 0;
            int coefficientOffset = phaseIndex * InterpolationTapCount;
            for (int tapIndex = 0; tapIndex < InterpolationTapCount; tapIndex++)
            {
                int tap = FirstInterpolationTap + tapIndex;
                double distance = phase - tap;
                double coefficient = Sinc(distance) * Sinc(distance / 8d);
                coefficients[coefficientOffset + tapIndex] = (float)coefficient;
                sum += coefficient;
            }
            for (int tapIndex = 0; tapIndex < InterpolationTapCount; tapIndex++)
            {
                coefficients[coefficientOffset + tapIndex] = (float)(
                    coefficients[coefficientOffset + tapIndex] / sum);
            }
        }
        return coefficients;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Sinc(double value)
    {
        if (Math.Abs(value) < 1e-12)
        {
            return 1d;
        }
        double radians = Math.PI * value;
        return Math.Sin(radians) / radians;
    }
}
