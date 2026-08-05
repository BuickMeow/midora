namespace Midora.Audio;

public static class InitialReleaseAudioRuntimePolicy
{
    public const int WorkFrameCount = 256;

    public static int BufferMillisecondsToFrameCapacity(int sampleRate, int bufferMilliseconds)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }
        if (bufferMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferMilliseconds));
        }

        long numerator = checked((long)sampleRate * bufferMilliseconds);
        return checked((int)((numerator + 999) / 1_000));
    }

    public static int WorkFramesForRingCapacity(int capacityFrameCount)
    {
        if (capacityFrameCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacityFrameCount));
        }

        return Math.Min(WorkFrameCount, capacityFrameCount);
    }
}
