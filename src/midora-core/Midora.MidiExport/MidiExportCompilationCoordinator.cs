using System.Collections.ObjectModel;
using Midora.Compiler;
using Midora.Domain;
using Midora.OutputPlanning;

namespace Midora.MidiExport;

public sealed class MidiExportCompilationRequest
{
    public required MidoraProject Project { get; init; }
    public required MidiExportMode Mode { get; init; }
    public required MidiExportRoutingStrategy Routing { get; init; }
    public long StartTick { get; init; }
    public long? EndTick { get; init; }
    public IReadOnlySet<MidoraId>? SelectedTrackIds { get; init; }
    public bool TreatWarningsAsErrors { get; init; }
}

public sealed record MidiExportTrackSnapshot(
    MidoraId TrackId,
    string StableSourceKey,
    int ProjectDisplayOrder,
    string DisplayName,
    bool Participates,
    string? ExclusionReason);

public sealed class MidiExportCompilationResult
{
    internal MidiExportCompilationResult(
        MidiExportMode mode,
        MidiExportRoutingStrategy routing,
        CanonicalCompiledResult compiledResult,
        MidiExportLogicalTrackLayout[] layouts,
        MidiExportTrackSnapshot[] tracks,
        byte[] usedZeroBasedPorts)
    {
        Mode = mode;
        Routing = routing;
        CompiledResult = compiledResult;
        Layouts = Array.AsReadOnly(layouts);
        Tracks = Array.AsReadOnly(tracks);
        UsedZeroBasedPorts = Array.AsReadOnly(usedZeroBasedPorts);
    }

    public bool Succeeded => CompiledResult.IsConsumable && !CompiledResult.IsPartial;
    public MidiExportMode Mode { get; }
    public MidiExportRoutingStrategy Routing { get; }
    public CanonicalCompiledResult CompiledResult { get; }
    public ReadOnlyCollection<MidiExportLogicalTrackLayout> Layouts { get; }
    public ReadOnlyCollection<MidiExportTrackSnapshot> Tracks { get; }
    public ReadOnlyCollection<byte> UsedZeroBasedPorts { get; }
    public IReadOnlyList<CompilerDiagnostic> Diagnostics => CompiledResult.Diagnostics;
}

public sealed class MidiExportCompilationCoordinator
{
    private readonly MidoraCompiler _compiler;

    public MidiExportCompilationCoordinator(MidoraCompiler compiler)
    {
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
    }

    public MidiExportCompilationResult Compile(MidiExportCompilationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Project);
        if (request.StartTick < 0 || request.EndTick < request.StartTick)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The MIDI export range is invalid.");
        }

        HashSet<MidoraId>? selected = request.SelectedTrackIds?.ToHashSet();
        CanonicalCompiledResult compiled = _compiler.CompileFull(
            request.Project,
            new CompilationRequest
            {
                Purpose = CompilationPurpose.MidiExport,
                StartTick = request.StartTick,
                EndTick = request.EndTick,
                IncludedTrackIds = selected,
                TreatWarningsAsErrors = request.TreatWarningsAsErrors
            });

        HashSet<MidoraId> instrumentIds = request.Project.EventInstruments
            .Select(instrument => instrument.Id)
            .ToHashSet();
        List<MidiExportTrackSnapshot> trackSnapshots = [];
        List<MidiExportLogicalTrackLayout> layouts = [];
        for (int index = 0; index < request.Project.Tracks.Count; index++)
        {
            LogicalTrack track = request.Project.Tracks[index];
            bool selectedForTask = selected is null || selected.Contains(track.Id);
            bool bound = track.EventInstrumentId.HasValue
                && instrumentIds.Contains(track.EventInstrumentId.Value);
            bool participates = selectedForTask && bound;
            int projectDisplayOrder = index + 1;
            string displayName = InitialReleaseOutputNaming.GetLogicalTrackDisplayName(
                track.Name,
                projectDisplayOrder);
            string? exclusion = participates
                ? null
                : !selectedForTask
                    ? "Not selected"
                    : "No valid Event Instrument binding";
            trackSnapshots.Add(new(
                track.Id,
                track.Id.ToString(),
                projectDisplayOrder,
                displayName,
                participates,
                exclusion));
            if (!participates)
            {
                continue;
            }

            byte[] ports = compiled.Allocations.ToArray()
                .Where(allocation => allocation.TrackId == track.Id)
                .Select(allocation => allocation.ZeroBasedPort)
                .Concat(compiled.Events.ToArray()
                    .Where(value => value.Source.TrackId == track.Id)
                    .Select(value => value.ZeroBasedPort))
                .Distinct()
                .Order()
                .ToArray();
            Dictionary<byte, string> names = ports.ToDictionary(
                port => port,
                port => InitialReleaseOutputNaming.GetEventTrackName(
                    track.Name,
                    projectDisplayOrder,
                    port + 1));
            layouts.Add(new(track.Id, names));
        }

        byte[] usedPorts = compiled.Events.ToArray()
            .Select(value => value.ZeroBasedPort)
            .Distinct()
            .Order()
            .ToArray();
        return new(
            request.Mode,
            request.Routing,
            compiled,
            layouts.ToArray(),
            trackSnapshots.ToArray(),
            usedPorts);
    }
}
