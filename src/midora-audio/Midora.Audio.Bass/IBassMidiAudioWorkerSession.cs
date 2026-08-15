namespace Midora.Audio.Bass;

internal interface IBassMidiAudioWorkerSession : IDisposable
{
    AudioWorkerStatus Status { get; }
    int? ExitCode { get; }
    string? StandardError { get; }
    void EnqueueMonitoringCommands(ReadOnlySpan<MidiMonitoringCommand> commands);
    long PauseHeldPreviewAtProducerFrontier(TimeSpan timeout);
    void ReplaceHeldPreviewFutureAndResume(
        MidiRenderPlan plan,
        long producerFrontierFrame,
        TimeSpan timeout);
    void ResumeHeldPreviewFromProducerFrontier(TimeSpan timeout);
    void BeginBufferingRecovery(long recoveryEndFrame);
    void Stop(bool flush, TimeSpan timeout);
}
