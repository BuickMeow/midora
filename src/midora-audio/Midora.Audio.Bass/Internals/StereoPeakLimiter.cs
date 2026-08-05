using System.Runtime.CompilerServices;

namespace Midora.Audio.Bass.Internals;

internal struct StereoPeakLimiter
{
    private readonly float _ceiling;
    private readonly float _releaseCoefficient;
    private float _gain;

    public StereoPeakLimiter(int sampleRate, float ceiling, float releaseMilliseconds)
    {
        _ceiling = ceiling;
        float releaseSamples = sampleRate * releaseMilliseconds / 1_000f;
        _releaseCoefficient = MathF.Exp(-1f / releaseSamples);
        _gain = 1f;
    }

    public readonly float Gain => _gain;

    public void Reset()
    {
        _gain = 1f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Process(float left, float right, out float outputLeft, out float outputRight)
    {
        if (!float.IsFinite(left) || !float.IsFinite(right))
        {
            outputLeft = 0;
            outputRight = 0;
            return false;
        }

        float peak = MathF.Max(MathF.Abs(left), MathF.Abs(right));
        float targetGain = peak > _ceiling ? _ceiling / peak : 1f;

        if (targetGain < _gain)
        {
            _gain = targetGain;
        }
        else
        {
            _gain = 1f - ((1f - _gain) * _releaseCoefficient);
        }

        outputLeft = left * _gain;
        outputRight = right * _gain;
        return float.IsFinite(outputLeft) && float.IsFinite(outputRight);
    }
}
