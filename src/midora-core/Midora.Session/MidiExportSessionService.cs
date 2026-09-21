using Midora.Compiler;
using Midora.Domain;
using Midora.MidiExport;
using Midora.OutputPlanning;
using Midora.Persistence;

namespace Midora.Session;

/// <summary>Host-neutral MIDI export options, mirroring the WPF desktop facade.</summary>
public sealed record MidiExportSessionOptions(
    MidiExportMode Mode,
    MidiExportRoutingStrategy Routing,
    string OutputDirectory,
    long StartTick,
    long? EndTick,
    bool IncludeReadme,
    bool TreatWarningsAsErrors,
    IReadOnlySet<MidoraId>? SelectedTrackIds = null);

/// <summary>
/// Frozen export plan: compilation, output paths and optional README are prepared before
/// the task starts so overwrite authorization can be granted against exact target paths.
/// </summary>
public sealed class PreparedMidiExport
{
    public required MidiExportCompilationResult Compilation { get; init; }

    public required MidiExportFrozenOutputPlan OutputPlan { get; init; }

    public MidiExportReadmeRequest? Readme { get; init; }

    public bool Succeeded => Compilation.Succeeded && OutputPlan.Succeeded;

    public bool RequiresOverwriteAuthorization => OutputPlan.RequiresOverwriteAuthorization;

    public IReadOnlyList<string> PlannedPaths =>
        OutputPlan.Targets.Select(target => target.FullPath).ToArray();

    public IReadOnlyList<string> Problems
    {
        get
        {
            List<string> problems = [];
            long count = Compilation.Diagnostics.Count;
            for (long index = 0; index < count; index++)
            {
                CompilerDiagnostic diagnostic = Compilation.Diagnostics[index];
                if (diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
                {
                    problems.Add($"{diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");
                }
            }

            problems.AddRange(OutputPlan.Diagnostics.Select(
                diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
            return problems;
        }
    }
}

public sealed record MidiExportSessionOutcome(
    bool Succeeded,
    string? FailureMessage,
    IReadOnlyList<string> WrittenPaths,
    IReadOnlyList<string> Messages,
    MidiExportPaddingSummary PaddingSummary);

/// <summary>
/// Application-level MIDI export entry point shared by hosts that own a Project document.
/// Wraps the MidiExport compilation coordinator, output planner and task runner; it holds
/// no UI state and performs no display work.
/// </summary>
public static class MidiExportSessionService
{
    public static PreparedMidiExport Prepare(
        MidoraProject project,
        string? currentProjectPath,
        MidoraProjectFileInformationV1? fileInformation,
        string softwareVersion,
        MidiExportSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(softwareVersion);

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
                fileInformation?.CreatedWithSoftwareVersion ?? softwareVersion,
                fileInformation?.LastSavedWithSoftwareVersion ?? softwareVersion,
                softwareVersion,
                DateTimeOffset.UtcNow);
        }

        return new()
        {
            Compilation = compilation,
            OutputPlan = plan,
            Readme = readme
        };
    }

    public static async Task<MidiExportSessionOutcome> ExecuteAsync(
        PreparedMidiExport prepared,
        bool overwriteAuthorized,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        MidiExportTaskRunner runner = new();
        MidiExportTaskResult result = await runner.ExecuteAsync(
            new MidiExportTaskRequest
            {
                Compilation = prepared.Compilation,
                OutputPlan = prepared.OutputPlan,
                Readme = prepared.Readme,
                OverwriteAuthorized = overwriteAuthorized
            },
            cancellationToken).ConfigureAwait(false);

        List<string> messages = result.ArtifactDiagnostics
            .Select(diagnostic => $"{diagnostic.SourceKey}: {diagnostic.Diagnostic.Code} {diagnostic.Diagnostic.Message}")
            .ToList();
        messages.AddRange(result.Output?.Diagnostics.Select(
            diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"
                + (diagnostic.Path is null ? string.Empty : $" ({diagnostic.Path})")) ?? []);

        if (result.Status == MidiExportTaskStatus.Succeeded && result.Output is { } output)
        {
            return new(
                true,
                null,
                output.Items
                    .Where(item => item.State == MidiExportOutputItemState.Completed)
                    .Select(item => item.FullPath)
                    .ToArray(),
                messages,
                result.PaddingSummary);
        }

        string failure = result.Status == MidiExportTaskStatus.Cancelled
            ? "MIDI export was cancelled."
            : result.OutputFailure?.Message
                ?? result.ArtifactDiagnostics.FirstOrDefault()?.Diagnostic.Message
                ?? "MIDI export failed.";
        return new(false, failure, [], messages, result.PaddingSummary);
    }
}
