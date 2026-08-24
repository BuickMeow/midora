namespace Midora.Audio;

public sealed record AudioFileRenderWorkerPreparation(
    IReadOnlyList<SoundFontConfiguration> SoundFonts,
    int SampleRate,
    int MaximumSampleVoicesPerUnitStream,
    float MasterVolumeDecibels);

public sealed record AudioFileRenderWorkerRequest(
    MidiRenderPlan Plan,
    IReadOnlyList<SoundFontConfiguration> SoundFonts,
    string SoundFontSetCacheIdentity,
    string TemporaryOutputPath,
    int MaximumSampleVoicesPerUnitStream,
    float MasterVolumeDecibels,
    IAudioPcmCacheSessionAccess? AudioCache = null);

public readonly record struct AudioFileRenderWorkerProgress(
    AudioWorkerState State,
    long RenderedFrameCount,
    long TotalFrameCount);

public readonly record struct AudioFileRenderWorkerResult(
    long FrameCount,
    long FileByteCount,
    long RenderingThreadAllocatedBytes);

public enum AudioFileRenderWorkerFailureStage
{
    Preparing,
    Rendering,
    Finalizing
}

public sealed class AudioFileRenderWorkerException : MidoraAudioException
{
    public AudioFileRenderWorkerException(
        AudioFileRenderWorkerFailureStage stage,
        string message,
        IReadOnlyList<string>? residualPaths = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Stage = stage;
        ResidualPaths = residualPaths ?? Array.Empty<string>();
    }

    public AudioFileRenderWorkerFailureStage Stage { get; }
    public IReadOnlyList<string> ResidualPaths { get; }
}

public sealed class AudioFileRenderCancelledException : OperationCanceledException
{
    public AudioFileRenderCancelledException(
        CancellationToken cancellationToken,
        IReadOnlyList<string>? residualPaths = null)
        : base("Audio file rendering was cancelled.", cancellationToken)
    {
        ResidualPaths = residualPaths ?? Array.Empty<string>();
    }

    public IReadOnlyList<string> ResidualPaths { get; }
}

public interface IAudioFileRenderWorker
{
    Task PrepareAsync(
        AudioFileRenderWorkerPreparation preparation,
        CancellationToken cancellationToken = default);

    Task<AudioFileRenderWorkerResult> RenderAsync(
        AudioFileRenderWorkerRequest request,
        IProgress<AudioFileRenderWorkerProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
