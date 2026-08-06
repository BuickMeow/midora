using System.Collections.ObjectModel;
using Midora.OutputPlanning;

namespace Midora.MidiExport;

public enum MidiExportMode
{
    WholeProject,
    PerLogicalTrack,
    PerPort
}

public sealed record MidiExportPlannedTarget(
    string SourceKey,
    long SourceOrder,
    string FileName,
    string FullPath,
    bool ExistedAtFreeze);

public sealed record MidiExportOutputPlanDiagnostic(string Code, string Message, string? SourceKey = null);

public sealed class MidiExportFrozenOutputPlan
{
    internal MidiExportFrozenOutputPlan(
        MidiExportMode mode,
        string outputDirectory,
        bool outputDirectoryExistedAtFreeze,
        MidiExportPlannedTarget[] targets,
        MidiExportOutputPlanDiagnostic[] diagnostics)
    {
        Mode = mode;
        OutputDirectory = outputDirectory;
        OutputDirectoryExistedAtFreeze = outputDirectoryExistedAtFreeze;
        Targets = Array.AsReadOnly(targets);
        Diagnostics = Array.AsReadOnly(diagnostics);
    }

    public MidiExportMode Mode { get; }
    public string OutputDirectory { get; }
    public bool OutputDirectoryExistedAtFreeze { get; }
    public bool Succeeded => Diagnostics.Count == 0;
    public bool RequiresOverwriteAuthorization => Targets.Any(target => target.ExistedAtFreeze);
    public ReadOnlyCollection<MidiExportPlannedTarget> Targets { get; }
    public ReadOnlyCollection<MidiExportOutputPlanDiagnostic> Diagnostics { get; }
}

public static class MidiExportOutputPlanner
{
    private const string InvalidPathCode = "MIDORA-MIDI-EXPORT-OUTPUT-PATH";
    private const int MaximumWindowsPathCodeUnits = 32_767;

    public static MidiExportFrozenOutputPlan PlanWholeProject(
        string outputDirectory,
        string? projectName,
        string? currentProjectFileStem,
        bool includeReadme) =>
        Freeze(
            MidiExportMode.WholeProject,
            outputDirectory,
            InitialReleaseOutputNaming.PlanWholeProjectMidi(
                projectName,
                currentProjectFileStem,
                includeReadme));

    public static MidiExportFrozenOutputPlan PlanLogicalTracks(
        string outputDirectory,
        IEnumerable<LogicalTrackOutputName> selectedTracks,
        int totalProjectTrackCount,
        bool includeReadme) =>
        Freeze(
            MidiExportMode.PerLogicalTrack,
            outputDirectory,
            InitialReleaseOutputNaming.PlanLogicalTrackMidi(
                selectedTracks,
                totalProjectTrackCount,
                includeReadme));

    public static MidiExportFrozenOutputPlan PlanPorts(
        string outputDirectory,
        IEnumerable<int> originalOneBasedPorts,
        bool includeReadme) =>
        Freeze(
            MidiExportMode.PerPort,
            outputDirectory,
            InitialReleaseOutputNaming.PlanPortMidi(originalOneBasedPorts, includeReadme));

    private static MidiExportFrozenOutputPlan Freeze(
        MidiExportMode mode,
        string outputDirectory,
        OutputFileNamePlan fileNamePlan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(fileNamePlan);

        List<MidiExportOutputPlanDiagnostic> diagnostics = fileNamePlan.Diagnostics
            .Select(value => new MidiExportOutputPlanDiagnostic(value.Code, value.Message, value.SourceKey))
            .ToList();
        string normalizedDirectory;
        try
        {
            normalizedDirectory = Path.GetFullPath(outputDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            diagnostics.Add(new(InvalidPathCode, exception.Message));
            return new(mode, outputDirectory, false, [], diagnostics.ToArray());
        }

        if (File.Exists(normalizedDirectory))
        {
            diagnostics.Add(new(
                InvalidPathCode,
                "The selected MIDI export output directory is an existing file."));
        }

        List<MidiExportPlannedTarget> targets = [];
        foreach (PlannedOutputFileName target in fileNamePlan.Targets)
        {
            string fullPath = Path.Combine(normalizedDirectory, target.FileName);
            if (fullPath.Length >= MaximumWindowsPathCodeUnits)
            {
                diagnostics.Add(new(
                    InvalidPathCode,
                    "The legalized MIDI export target exceeds the Windows path representation limit.",
                    target.SourceKey));
                continue;
            }
            if (Directory.Exists(fullPath))
            {
                diagnostics.Add(new(
                    InvalidPathCode,
                    "A planned MIDI export file target is an existing directory.",
                    target.SourceKey));
            }
            targets.Add(new(
                target.SourceKey,
                target.SourceOrder,
                target.FileName,
                fullPath,
                File.Exists(fullPath)));
        }
        if (!targets.Any(target => target.FileName.EndsWith(".mid", StringComparison.OrdinalIgnoreCase)))
        {
            diagnostics.Add(new(
                InvalidPathCode,
                "A MIDI export output plan must contain at least one .mid artifact."));
        }

        return new(
            mode,
            normalizedDirectory,
            Directory.Exists(normalizedDirectory),
            targets.ToArray(),
            diagnostics.ToArray());
    }
}
