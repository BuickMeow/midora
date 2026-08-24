using System.Collections.ObjectModel;
using Midora.Domain;
using Midora.OutputPlanning;

namespace Midora.AudioRender;

public sealed record AudioRenderPlannedTarget(
    string SourceKey,
    MidoraId TrackId,
    long SourceOrder,
    string FileName,
    string FullPath,
    bool ExistedAtFreeze);

public sealed class AudioRenderFrozenOutputPlan
{
    internal AudioRenderFrozenOutputPlan(
        AudioRenderMode mode,
        string outputDirectory,
        bool outputDirectoryExistedAtFreeze,
        AudioRenderPlannedTarget[] targets,
        AudioRenderDiagnostic[] diagnostics)
    {
        Mode = mode;
        OutputDirectory = outputDirectory;
        OutputDirectoryExistedAtFreeze = outputDirectoryExistedAtFreeze;
        Targets = Array.AsReadOnly(targets);
        Diagnostics = Array.AsReadOnly(diagnostics);
    }

    public AudioRenderMode Mode { get; }
    public string OutputDirectory { get; }
    public bool OutputDirectoryExistedAtFreeze { get; }
    public ReadOnlyCollection<AudioRenderPlannedTarget> Targets { get; }
    public ReadOnlyCollection<AudioRenderDiagnostic> Diagnostics { get; }
    public bool Succeeded => Diagnostics.All(value => value.Severity != AudioRenderDiagnosticSeverity.Error);
    public bool RequiresOverwriteAuthorization => Targets.Any(value => value.ExistedAtFreeze);
}

public static class AudioRenderOutputPlanner
{
    private const int MaximumWindowsPathCodeUnits = 32_767;
    private const string PathErrorCode = "MIDORA-AUDIO-RENDER-OUTPUT-PATH";

    public static string SuggestWholeMixFileName(
        string? projectName,
        string? currentProjectFileStem)
    {
        OutputFileNamePlan plan = InitialReleaseOutputNaming.PlanWholeProjectAudio(
            projectName,
            currentProjectFileStem);
        if (!plan.Succeeded)
        {
            throw new InvalidDataException(plan.Diagnostics[0].Message);
        }
        return plan.Targets[0].FileName;
    }

    public static AudioRenderFrozenOutputPlan PlanWholeMix(
        string targetFilePath,
        IEnumerable<string>? forbiddenTargetPaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFilePath);
        List<AudioRenderDiagnostic> diagnostics = [];
        string normalizedInput;
        try
        {
            normalizedInput = Path.GetFullPath(targetFilePath);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            diagnostics.Add(Error(exception.Message));
            return new(AudioRenderMode.WholeMix, targetFilePath, false, [], diagnostics.ToArray());
        }

        string extension = Path.GetExtension(normalizedInput);
        if (extension.Length == 0)
        {
            normalizedInput += ".wav";
            extension = ".wav";
        }
        else if (!string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error("The Whole Mix target must use the .wav extension."));
        }

        string directory = Path.GetDirectoryName(normalizedInput) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(normalizedInput);
        OutputFileNamePlan legalized = WindowsOutputFileNamePlanner.PlanSingleDirectory(
        [
            new(AudioRenderCompilationCoordinator.WholeMixSourceKey, 0, stem, extension)
        ]);
        diagnostics.AddRange(legalized.Diagnostics.Select(value => new AudioRenderDiagnostic(
            value.Code,
            AudioRenderDiagnosticSeverity.Error,
            value.Message,
            value.SourceKey)));
        if (!legalized.Succeeded)
        {
            return new(
                AudioRenderMode.WholeMix,
                directory,
                Directory.Exists(directory),
                [],
                diagnostics.ToArray());
        }

        string finalPath = Path.Combine(directory, legalized.Targets[0].FileName);
        ValidateTarget(finalPath, forbiddenTargetPaths, diagnostics, AudioRenderCompilationCoordinator.WholeMixSourceKey);
        AudioRenderPlannedTarget target = new(
            AudioRenderCompilationCoordinator.WholeMixSourceKey,
            default,
            0,
            legalized.Targets[0].FileName,
            finalPath,
            File.Exists(finalPath));
        return new(
            AudioRenderMode.WholeMix,
            directory,
            Directory.Exists(directory),
            [target],
            diagnostics.ToArray());
    }

    public static AudioRenderFrozenOutputPlan PlanLogicalTracks(
        string outputDirectory,
        IEnumerable<AudioRenderTrackSnapshot> tracks,
        int totalProjectTrackCount,
        IEnumerable<string>? forbiddenTargetPaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(tracks);
        AudioRenderTrackSnapshot[] materialized = tracks
            .Where(value => value.Participates)
            .OrderBy(value => value.ProjectDisplayOrder)
            .ToArray();
        OutputFileNamePlan names = InitialReleaseOutputNaming.PlanLogicalTrackAudio(
            materialized.Select(value => new LogicalTrackOutputName(
                value.StableSourceKey,
                value.ProjectDisplayOrder,
                value.DisplayName)),
            totalProjectTrackCount);
        List<AudioRenderDiagnostic> diagnostics = names.Diagnostics
            .Select(value => new AudioRenderDiagnostic(
                value.Code,
                AudioRenderDiagnosticSeverity.Error,
                value.Message,
                value.SourceKey))
            .ToList();
        string directory;
        try
        {
            directory = Path.GetFullPath(outputDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            diagnostics.Add(Error(exception.Message));
            return new(AudioRenderMode.PerLogicalTrack, outputDirectory, false, [], diagnostics.ToArray());
        }

        if (File.Exists(directory))
        {
            diagnostics.Add(Error("The Per Logical Track output directory is an existing file."));
        }
        if (!names.Succeeded)
        {
            return new(
                AudioRenderMode.PerLogicalTrack,
                directory,
                Directory.Exists(directory),
                [],
                diagnostics.ToArray());
        }

        Dictionary<string, AudioRenderTrackSnapshot> byPlannerKey = materialized.ToDictionary(
            value => "logical-track:" + value.StableSourceKey,
            StringComparer.Ordinal);
        List<AudioRenderPlannedTarget> targets = [];
        foreach (PlannedOutputFileName name in names.Targets)
        {
            AudioRenderTrackSnapshot track = byPlannerKey[name.SourceKey];
            string finalPath = Path.Combine(directory, name.FileName);
            ValidateTarget(finalPath, forbiddenTargetPaths, diagnostics, track.StableSourceKey);
            targets.Add(new(
                track.StableSourceKey,
                track.TrackId,
                name.SourceOrder,
                name.FileName,
                finalPath,
                File.Exists(finalPath)));
        }
        if (targets.Count == 0)
        {
            diagnostics.Add(Error("The audio render output plan has no valid WAV target."));
        }

        return new(
            AudioRenderMode.PerLogicalTrack,
            directory,
            Directory.Exists(directory),
            targets.ToArray(),
            diagnostics.ToArray());
    }

    private static void ValidateTarget(
        string targetPath,
        IEnumerable<string>? forbiddenTargetPaths,
        List<AudioRenderDiagnostic> diagnostics,
        string sourceKey)
    {
        if (targetPath.Length >= MaximumWindowsPathCodeUnits)
        {
            diagnostics.Add(Error(
                "The legalized audio render target exceeds the Windows path representation limit.",
                sourceKey,
                targetPath));
        }
        if (Directory.Exists(targetPath))
        {
            diagnostics.Add(Error(
                "A planned WAV file target is an existing directory.",
                sourceKey,
                targetPath));
        }
        if (forbiddenTargetPaths is null)
        {
            return;
        }

        foreach (string forbidden in forbiddenTargetPaths)
        {
            if (string.IsNullOrWhiteSpace(forbidden))
            {
                continue;
            }
            string normalized;
            try
            {
                normalized = Path.GetFullPath(forbidden);
            }
            catch (Exception exception) when (exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
            {
                diagnostics.Add(Error(
                    "A forbidden output target path is invalid: " + exception.Message,
                    sourceKey,
                    targetPath));
                continue;
            }
            if (string.Equals(targetPath, normalized, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Error(
                    "A WAV target cannot overwrite the current Project or an enabled application SoundFont.",
                    sourceKey,
                    targetPath));
            }
        }
    }

    private static AudioRenderDiagnostic Error(
        string message,
        string? sourceKey = null,
        string? finalPath = null) =>
        new(
            PathErrorCode,
            AudioRenderDiagnosticSeverity.Error,
            message,
            sourceKey,
            default,
            finalPath);
}
