using Midora.AudioDevice;

namespace Midora.Audio;

public interface IMidiRenderer : IAudioRenderSource, IDisposable
{
    long PositionFrames { get; }

    long TotalFrameCount { get; }

    AudioRenderFault Fault { get; }
}
