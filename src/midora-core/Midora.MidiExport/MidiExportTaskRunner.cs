using System.Collections.ObjectModel;
using Midora.Compiler;
using Midora.OutputPlanning;

namespace Midora.MidiExport;

public enum MidiExportTaskStatus
{
    Succeeded,
    Failed,
    Cancelled
}

public sealed class MidiExportTaskRequest
{
    public required MidiExportCompilationResult Compilation { get; init; }
    public required MidiExportFrozenOutputPlan OutputPlan { get; init; }
    public MidiExportReadmeRequest? Readme { get; init; }
    public bool OverwriteAuthorized { get; init; }
}

public sealed class MidiExportTaskResult
{
    internal MidiExportTaskResult(
        MidiExportTaskStatus status,
        CompilerDiagnostic[] compilerDiagnostics,
        MidiExportArtifactDiagnostic[] artifactDiagnostics,
        MidiExportOutputResult? output,
        MidiExportOutputException? outputFailure)
    {
        Status = status;
        CompilerDiagnostics = Array.AsReadOnly(compilerDiagnostics);
        ArtifactDiagnostics = Array.AsReadOnly(artifactDiagnostics);
        Output = output;
        OutputFailure = outputFailure;
    }

    public MidiExportTaskStatus Status { get; }
    public ReadOnlyCollection<CompilerDiagnostic> CompilerDiagnostics { get; }
    public ReadOnlyCollection<MidiExportArtifactDiagnostic> ArtifactDiagnostics { get; }
    public MidiExportOutputResult? Output { get; }
    public MidiExportOutputException? OutputFailure { get; }
}

public sealed class MidiExportTaskRunner
{
    private readonly MidiExportOutputTransaction _outputTransaction;

    public MidiExportTaskRunner()
        : this(new MidiExportOutputTransaction())
    {
    }

    internal MidiExportTaskRunner(MidiExportOutputTransaction outputTransaction)
    {
        _outputTransaction = outputTransaction ?? throw new ArgumentNullException(nameof(outputTransaction));
    }

    public async Task<MidiExportTaskResult> ExecuteAsync(
        MidiExportTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Compilation);
        ArgumentNullException.ThrowIfNull(request.OutputPlan);
        if (request.Compilation.Mode != request.OutputPlan.Mode)
        {
            throw new ArgumentException(
                "The frozen MIDI export compilation and output plan modes differ.",
                nameof(request));
        }
        if (!request.Compilation.Succeeded)
        {
            return new(
                MidiExportTaskStatus.Failed,
                request.Compilation.Diagnostics.ToArray(),
                [],
                null,
                null);
        }

        MidiExportArtifactBuildResult artifacts = BuildArtifacts(request);
        if (!artifacts.Succeeded)
        {
            return new(
                MidiExportTaskStatus.Failed,
                request.Compilation.Diagnostics.ToArray(),
                artifacts.Diagnostics.ToArray(),
                null,
                null);
        }

        try
        {
            MidiExportOutputResult output = await _outputTransaction.PublishAsync(
                request.OutputPlan,
                artifacts.Artifacts,
                request.OverwriteAuthorized,
                cancellationToken).ConfigureAwait(false);
            return new(
                MidiExportTaskStatus.Succeeded,
                request.Compilation.Diagnostics.ToArray(),
                [],
                output,
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(
                MidiExportTaskStatus.Cancelled,
                request.Compilation.Diagnostics.ToArray(),
                [],
                null,
                null);
        }
        catch (MidiExportOutputException exception)
        {
            return new(
                MidiExportTaskStatus.Failed,
                request.Compilation.Diagnostics.ToArray(),
                [],
                null,
                exception);
        }
    }

    private static MidiExportArtifactBuildResult BuildArtifacts(MidiExportTaskRequest request)
    {
        MidiExportCompilationResult compilation = request.Compilation;
        CanonicalCompiledResult compiled = compilation.CompiledResult;
        return compilation.Mode switch
        {
            MidiExportMode.WholeProject => MidiExportArtifactBuilder.BuildWholeProject(
                request.OutputPlan,
                new WholeProjectMidiEncodingRequest
                {
                    CompiledResult = compiled,
                    ConductorTrackName = InitialReleaseOutputNaming.ConductorTrackName,
                    LogicalTracks = compilation.Layouts
                },
                request.Readme),
            MidiExportMode.PerLogicalTrack => MidiExportArtifactBuilder.BuildLogicalTracks(
                request.OutputPlan,
                compilation.Layouts.Select(layout => new LogicalTrackMidiArtifactRequest(
                    compilation.Tracks.Single(track => track.TrackId == layout.TrackId).StableSourceKey,
                    new LogicalTrackMidiEncodingRequest
                    {
                        CompiledResult = compiled,
                        ConductorTrackName = InitialReleaseOutputNaming.ConductorTrackName,
                        LogicalTrack = layout
                    })),
                request.Readme),
            MidiExportMode.PerPort => MidiExportArtifactBuilder.BuildPorts(
                request.OutputPlan,
                compilation.UsedZeroBasedPorts.Select(port => new PortMidiArtifactRequest(
                    new PortMidiEncodingRequest
                    {
                        CompiledResult = compiled,
                        ConductorTrackName = InitialReleaseOutputNaming.ConductorTrackName,
                        LogicalTracks = compilation.Layouts,
                        ZeroBasedOriginalPort = port
                    })),
                request.Readme),
            _ => throw new ArgumentOutOfRangeException(nameof(compilation.Mode))
        };
    }
}
