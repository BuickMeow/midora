namespace Midora.AudioDevice.Wave;

public interface IWaveFileRenderMonitor
{
    bool IsCancellationRequested { get; }

    void ReportRenderedFrames(long renderedFrameCount);

    void BeginFinalizing();
}
