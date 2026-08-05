namespace Midora.AudioDevice.Wave;

public readonly record struct WaveFileSize(
    long FrameCount,
    uint DataByteCount,
    uint RiffChunkSize,
    long FileByteCount)
{
    public const int HeaderByteCount = 56;
    public const int StereoFloat32BytesPerFrame = 8;

    public static WaveFileSize Calculate(long frameCount)
    {
        if (frameCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        }

        long dataByteCount = checked(frameCount * StereoFloat32BytesPerFrame);
        long riffChunkSize = checked(dataByteCount + 48);
        if (dataByteCount > uint.MaxValue || riffChunkSize > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameCount),
                "The output cannot be represented by an initial-release RIFF/WAVE file.");
        }

        return new WaveFileSize(
            frameCount,
            (uint)dataByteCount,
            (uint)riffChunkSize,
            checked(dataByteCount + HeaderByteCount));
    }
}
