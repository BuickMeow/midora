namespace Midora.AudioDevice;

public unsafe interface IAudioRenderSource
{
    AudioFormat Format { get; }

    AudioPullResult PullFrames(float* destination, int requestedFrameCount);
}
