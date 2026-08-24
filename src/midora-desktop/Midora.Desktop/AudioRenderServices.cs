using System.IO;
using Midora.Audio.Bass;
using Midora.AudioRender;
using Midora.Compiler;
using Midora.Domain;
using Midora.Persistence;

namespace Midora.Desktop;

public sealed record DesktopAudioRenderOptions(
    AudioRenderMode Mode,
    string OutputPath,
    long StartTick,
    long? EndTick,
    int SampleRate,
    int MaximumSampleVoicesPerUnitStream,
    bool TreatWarningsAsErrors,
    IReadOnlySet<MidoraId>? SelectedTrackIds = null);

public sealed class PreparedDesktopAudioRender : IAsyncDisposable
{
    public required AudioRenderCompilationResult Compilation { get; init; }
    public required AudioRenderFrozenOutputPlan OutputPlan { get; init; }
    public required AudioRenderSoundFontSnapshot SoundFont { get; init; }
    public required BassMidiAudioFileRenderWorker Worker { get; init; }
    public required int SampleRate { get; init; }
    public required int MaximumSampleVoicesPerUnitStream { get; init; }
    public required double MasterVolumeDecibels { get; init; }
    public bool Succeeded => Compilation.HasRenderableOutput && OutputPlan.Succeeded;
    public ValueTask DisposeAsync() => SoundFont.DisposeAsync();
}

public static class DesktopAudioRenderService
{
    public static async Task<PreparedDesktopAudioRender> PrepareAsync(
        MidoraProject project,
        string? currentProjectPath,
        IReadOnlyList<string> soundFontPaths,
        DesktopAudioRenderOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(options);
        if (!FormalAudioWorkerLocator.TryCreateFileRenderWorker(
                out BassMidiAudioFileRenderWorker? worker,
                out string? workerFailure))
        {
            throw new InvalidOperationException(workerFailure);
        }

        AudioRenderSoundFontSnapshot? soundFont = null;
        try
        {
            soundFont = await AudioRenderSoundFontSnapshot.CreateAsync(
                soundFontPaths,
                cancellationToken).ConfigureAwait(false);
            using MidoraCompiler compiler = new();
            AudioRenderCompilationResult compilation = new AudioRenderCompilationCoordinator(compiler).Compile(new()
            {
                Project = project,
                Mode = options.Mode,
                StartTick = options.StartTick,
                EndTick = options.EndTick,
                SelectedTrackIds = options.SelectedTrackIds,
                TreatWarningsAsErrors = options.TreatWarningsAsErrors
            });
            string[] forbidden = new[] { currentProjectPath }
                .Concat(soundFont.SoundFontPaths)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.GetFullPath(path!))
                .ToArray();
            AudioRenderFrozenOutputPlan plan = options.Mode switch
            {
                AudioRenderMode.WholeMix => AudioRenderOutputPlanner.PlanWholeMix(
                    options.OutputPath,
                    forbidden),
                AudioRenderMode.PerLogicalTrack => AudioRenderOutputPlanner.PlanLogicalTracks(
                    options.OutputPath,
                    compilation.Tracks,
                    project.Tracks.Count,
                    forbidden),
                _ => throw new ArgumentOutOfRangeException(nameof(options))
            };
            PreparedDesktopAudioRender prepared = new()
            {
                Compilation = compilation,
                OutputPlan = plan,
                SoundFont = soundFont,
                Worker = worker!,
                SampleRate = options.SampleRate,
                MaximumSampleVoicesPerUnitStream = options.MaximumSampleVoicesPerUnitStream,
                MasterVolumeDecibels = project.Playback.MasterVolumeDecibels
            };
            soundFont = null;
            return prepared;
        }
        finally
        {
            if (soundFont is not null) await soundFont.DisposeAsync().ConfigureAwait(false);
        }
    }
}
