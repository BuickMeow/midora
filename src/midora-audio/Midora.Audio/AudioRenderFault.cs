namespace Midora.Audio;

public readonly record struct AudioRenderFault(
    AudioRenderFaultCode Code,
    int NativeErrorCode,
    int ZeroBasedPortNumber,
    long SampleFrame)
{
    public static AudioRenderFault None =>
        new(AudioRenderFaultCode.None, 0, -1, -1);
}

public enum AudioRenderFaultCode : byte
{
    None,
    InvalidPullRequest,
    BassMidiEventSubmissionFailed,
    BassMidiDecodeFailed,
    BassMidiShortRead,
    PcmCacheReadFailed,
    NonFiniteSample
}
