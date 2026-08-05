namespace Midora.AudioDevice;

public readonly record struct AudioPullResult(
    int FrameCount,
    AudioPullStatus Status)
{
    public static AudioPullResult Continue(int frameCount) =>
        new(frameCount, AudioPullStatus.Continue);

    public static AudioPullResult EndOfStream(int frameCount = 0) =>
        new(frameCount, AudioPullStatus.EndOfStream);

    public static AudioPullResult Fault(int frameCount = 0) =>
        new(frameCount, AudioPullStatus.Fault);
}

public enum AudioPullStatus : byte
{
    Continue,
    Buffering,
    EndOfStream,
    Fault
}
