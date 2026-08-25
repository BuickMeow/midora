using Midora.Compiler;
using System.IO;
using Midora.Domain;
using Midora.MidiExport;
using Midora.OutputPlanning;

namespace Midora.Desktop;

public sealed record DesktopMidiExportOptions(
    MidiExportMode Mode,
    MidiExportRoutingStrategy Routing,
    string OutputDirectory,
    long StartTick,
    long? EndTick,
    bool IncludeReadme,
    bool TreatWarningsAsErrors,
    IReadOnlySet<MidoraId>? SelectedTrackIds = null);

public sealed class PreparedDesktopMidiExport
{
    public required MidiExportCompilationResult Compilation { get; init; }
    public required MidiExportFrozenOutputPlan OutputPlan { get; init; }
    public MidiExportReadmeRequest? Readme { get; init; }
    public bool Succeeded => Compilation.Succeeded && OutputPlan.Succeeded;
}

public static class DesktopMidiExportService
{
    public static PreparedDesktopMidiExport Prepare(
        MidoraProject project,
        string? currentProjectPath,
        string? createdWithSoftwareVersion,
        string? lastSavedWithSoftwareVersion,
        DesktopMidiExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(options);
        using MidoraCompiler compiler = new();
        MidiExportCompilationResult compilation = new MidiExportCompilationCoordinator(compiler).Compile(new()
        {
            Project = project,
            Mode = options.Mode,
            Routing = options.Routing,
            StartTick = options.StartTick,
            EndTick = options.EndTick,
            SelectedTrackIds = options.SelectedTrackIds,
            TreatWarningsAsErrors = options.TreatWarningsAsErrors
        });

        string? currentStem = currentProjectPath is null
            ? null
            : Path.GetFileNameWithoutExtension(currentProjectPath);
        MidiExportFrozenOutputPlan plan = options.Mode switch
        {
            MidiExportMode.WholeProject => MidiExportOutputPlanner.PlanWholeProject(
                options.OutputDirectory,
                project.Metadata.ProjectName,
                currentStem,
                options.IncludeReadme),
            MidiExportMode.PerLogicalTrack => MidiExportOutputPlanner.PlanLogicalTracks(
                options.OutputDirectory,
                compilation.Tracks
                    .Where(track => track.Participates)
                    .Select(track => new LogicalTrackOutputName(
                        track.StableSourceKey,
                        track.ProjectDisplayOrder,
                        track.DisplayName)),
                project.Tracks.Count,
                options.IncludeReadme),
            MidiExportMode.PerPort => MidiExportOutputPlanner.PlanPorts(
                options.OutputDirectory,
                compilation.UsedZeroBasedPorts.Select(port => port + 1),
                options.IncludeReadme),
            _ => throw new ArgumentOutOfRangeException(nameof(options))
        };

        MidiExportReadmeRequest? readme = null;
        if (options.IncludeReadme && compilation.Succeeded && plan.Succeeded)
        {
            MidiExportRangeSource rangeSource = options.EndTick.HasValue
                ? MidiExportRangeSource.Manual
                : project.Conductor.EndMarker is null
                    ? MidiExportRangeSource.NaturalContentEnd
                    : MidiExportRangeSource.ProjectEndMarker;
            readme = MidiExportReadmeFactory.Create(
                project,
                compilation,
                plan,
                rangeSource,
                createdWithSoftwareVersion ?? MidoraSoftwareVersion.InformationalVersion,
                lastSavedWithSoftwareVersion ?? MidoraSoftwareVersion.InformationalVersion,
                MidoraSoftwareVersion.InformationalVersion,
                DateTimeOffset.UtcNow);
        }

        return new()
        {
            Compilation = compilation,
            OutputPlan = plan,
            Readme = readme
        };
    }
}
