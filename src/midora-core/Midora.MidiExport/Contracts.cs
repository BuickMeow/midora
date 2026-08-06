using Midora.Compiler;
using Midora.Domain;

namespace Midora.MidiExport;

public enum MidiExportDiagnosticCategory
{
    CanonicalConsistency,
    Encoding
}

public sealed record MidiExportDiagnostic(
    string Code,
    MidiExportDiagnosticCategory Category,
    string Message,
    SourceReference Source = default);

public sealed class MidiExportLogicalTrackLayout
{
    private readonly Dictionary<byte, string> _eventTrackNamesByPort;

    public MidiExportLogicalTrackLayout(
        MidoraId trackId,
        IReadOnlyDictionary<byte, string> eventTrackNamesByPort)
    {
        ArgumentNullException.ThrowIfNull(eventTrackNamesByPort);
        if (trackId == default)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId));
        }
        foreach ((byte port, string name) in eventTrackNamesByPort)
        {
            if (port > 15)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(eventTrackNamesByPort),
                    "Midora MIDI export ports must be in the zero-based range 0..15.");
            }
            ArgumentNullException.ThrowIfNull(name);
        }
        TrackId = trackId;
        _eventTrackNamesByPort = new(eventTrackNamesByPort);
    }

    public MidoraId TrackId { get; }
    public IReadOnlyDictionary<byte, string> EventTrackNamesByPort => _eventTrackNamesByPort;
}

public sealed class WholeProjectMidiEncodingRequest
{
    public required CanonicalCompiledResult CompiledResult { get; init; }
    public required string ConductorTrackName { get; init; }
    public required IReadOnlyList<MidiExportLogicalTrackLayout> LogicalTracks { get; init; }
}

public sealed class LogicalTrackMidiEncodingRequest
{
    public required CanonicalCompiledResult CompiledResult { get; init; }
    public required string ConductorTrackName { get; init; }
    public required MidiExportLogicalTrackLayout LogicalTrack { get; init; }
}

public sealed class PortMidiEncodingRequest
{
    public required CanonicalCompiledResult CompiledResult { get; init; }
    public required string ConductorTrackName { get; init; }
    public required IReadOnlyList<MidiExportLogicalTrackLayout> LogicalTracks { get; init; }
    public required byte ZeroBasedOriginalPort { get; init; }
}

public sealed class MidiExportEncodingResult
{
    internal MidiExportEncodingResult(byte[] fileBytes, MidiExportDiagnostic[] diagnostics)
    {
        FileBytes = fileBytes;
        Diagnostics = diagnostics;
    }

    public bool Succeeded => FileBytes.Length != 0 && Diagnostics.Count == 0;
    public byte[] FileBytes { get; }
    public IReadOnlyList<MidiExportDiagnostic> Diagnostics { get; }
}
