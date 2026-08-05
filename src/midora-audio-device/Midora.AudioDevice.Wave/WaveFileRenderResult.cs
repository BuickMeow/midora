namespace Midora.AudioDevice.Wave;

public readonly record struct WaveFileRenderResult(
    long FrameCount,
    long FileByteCount,
    long RenderingThreadAllocatedBytes,
    long SourcePullAllocatedBytes,
    long SampleWriteAllocatedBytes);
