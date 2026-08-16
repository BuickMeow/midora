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

public readonly record struct MidiExportChannelUnit
{
    public MidiExportChannelUnit(byte zeroBasedPort, byte zeroBasedChannel)
    {
        if (zeroBasedPort > 15)
        {
            throw new ArgumentOutOfRangeException(
                nameof(zeroBasedPort),
                "Midora MIDI export ports must be in the zero-based range 0..15.");
        }
        if (zeroBasedChannel > 15)
        {
            throw new ArgumentOutOfRangeException(
                nameof(zeroBasedChannel),
                "MIDI channels must be in the zero-based range 0..15.");
        }

        ZeroBasedPort = zeroBasedPort;
        ZeroBasedChannel = zeroBasedChannel;
    }

    public byte ZeroBasedPort { get; }
    public byte ZeroBasedChannel { get; }
}

public sealed class MidiExportLogicalTrackLayout
{
    private readonly Dictionary<MidiExportChannelUnit, string> _eventTrackNamesByUnit;

    public MidiExportLogicalTrackLayout(
        MidoraId trackId,
        IReadOnlyDictionary<MidiExportChannelUnit, string> eventTrackNamesByUnit)
    {
        ArgumentNullException.ThrowIfNull(eventTrackNamesByUnit);
        if (trackId == default)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId));
        }
        foreach ((MidiExportChannelUnit unit, string name) in eventTrackNamesByUnit)
        {
            if (unit.ZeroBasedPort > 15 || unit.ZeroBasedChannel > 15)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(eventTrackNamesByUnit),
                    "Midora MIDI export Channel Units must use Port and Channel values in the zero-based range 0..15.");
            }
            ArgumentNullException.ThrowIfNull(name);
        }
        TrackId = trackId;
        _eventTrackNamesByUnit = new(eventTrackNamesByUnit);
    }

    public MidoraId TrackId { get; }
    public IReadOnlyDictionary<MidiExportChannelUnit, string> EventTrackNamesByUnit =>
        _eventTrackNamesByUnit;
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
