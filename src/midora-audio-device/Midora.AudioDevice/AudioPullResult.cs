namespace Midora.AudioDevice;

public readonly record struct AudioPullResult(
    int FrameCount,
    AudioPullStatus Status)
{
    public bool IsValidForRequest(int requestedFrameCount) =>
        requestedFrameCount >= 0
        && FrameCount >= 0
        && FrameCount <= requestedFrameCount
        && Status is AudioPullStatus.Continue
            or AudioPullStatus.Buffering
            or AudioPullStatus.EndOfStream
            or AudioPullStatus.Fault
        && (Status != AudioPullStatus.Buffering || FrameCount == 0)
        && (requestedFrameCount == 0 || Status != AudioPullStatus.Continue || FrameCount > 0);

    public static AudioPullResult Continue(int frameCount) =>
        new(frameCount, AudioPullStatus.Continue);

    public static AudioPullResult EndOfStream(int frameCount = 0) =>
        new(frameCount, AudioPullStatus.EndOfStream);

    public static AudioPullResult Buffering() =>
        new(0, AudioPullStatus.Buffering);

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
