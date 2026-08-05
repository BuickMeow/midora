using Midora.Domain;
using Midora.Midi;

namespace Midora.Compiler;

public enum DiagnosticSeverity
{
    Debug,
    Info,
    Warning,
    Error
}

public readonly record struct SourceReference(
    MidoraId ProjectId,
    MidoraId TrackId = default,
    MidoraId SegmentId = default,
    MidoraId LogicalNoteId = default,
    MidoraId EventInstrumentId = default,
    MidoraId SubVoiceId = default,
    MidoraId SourceEventId = default,
    long Tick = -1);

public sealed record CompilerDiagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    SourceReference Source);

public enum CompilationPurpose
{
    FullProject,
    Range,
    Playback,
    SegmentPreview,
    EventInstrumentPreview,
    AudioRender
}

public sealed class CompilationRequest
{
    public CompilationPurpose Purpose { get; init; } = CompilationPurpose.FullProject;
    public long StartTick { get; init; }
    public long? EndTick { get; init; }
    public bool TreatWarningsAsErrors { get; init; }
    public HashSet<MidoraId>? IncludedTrackIds { get; init; }
    public HashSet<MidoraId>? IncludedSubVoiceIds { get; init; }
}

public enum CanonicalEventRole : byte
{
    NoteOff = 0,
    Reset = 1,
    RangeRestore = 2,
    InitialState = 3,
    Bank = 4,
    Program = 5,
    Parameter = 6,
    ControlChange = 7,
    PitchBend = 8,
    LogicalParameter = 9,
    NoteOn = 10
}

public readonly record struct CanonicalMidiEvent(
    long Tick,
    byte ZeroBasedPort,
    byte ZeroBasedChannel,
    MidiMessage Message,
    CanonicalEventRole Role,
    long StableOrder,
    long SemanticTargetKey,
    long SemanticGroup,
    SourceReference Source);

public readonly record struct ChannelUnitAllocation(
    MidoraId TrackId,
    MidoraId EventInstrumentId,
    MidoraId InstanceGroupId,
    MidoraId SubVoiceId,
    long StartTick,
    long EndTick,
    byte ZeroBasedPort,
    byte ZeroBasedChannel);

public readonly record struct CanonicalTempo(
    MidoraId SourceId,
    long Tick,
    decimal BeatsPerMinute,
    bool IsRangeRestore = false)
{
    public CanonicalTempo(long tick, decimal beatsPerMinute)
        : this(default, tick, beatsPerMinute)
    {
    }
}
public readonly record struct CanonicalTimeSignature(
    MidoraId SourceId,
    long Tick,
    int Numerator,
    int Denominator,
    bool IsRangeRestore = false);
public readonly record struct CanonicalKeySignature(
    MidoraId SourceId,
    long Tick,
    int SharpsFlats,
    bool IsMinor,
    bool IsRangeRestore = false);
public readonly record struct CanonicalMarker(MidoraId Id, long Tick, string Name);

public sealed class CanonicalConductor
{
    private readonly CanonicalTempo[] _tempos;
    private readonly CanonicalTimeSignature[] _timeSignatures;
    private readonly CanonicalKeySignature[] _keySignatures;
    private readonly CanonicalMarker[] _markers;

    internal CanonicalConductor(
        CanonicalTempo[] tempos,
        CanonicalTimeSignature[] timeSignatures,
        CanonicalKeySignature[] keySignatures,
        CanonicalMarker[] markers,
        long? endMarkerTick)
    {
        _tempos = tempos;
        _timeSignatures = timeSignatures;
        _keySignatures = keySignatures;
        _markers = markers;
        EndMarkerTick = endMarkerTick;
    }

    public ReadOnlySpan<CanonicalTempo> Tempos => _tempos;
    public ReadOnlySpan<CanonicalTimeSignature> TimeSignatures => _timeSignatures;
    public ReadOnlySpan<CanonicalKeySignature> KeySignatures => _keySignatures;
    public ReadOnlySpan<CanonicalMarker> Markers => _markers;
    public long? EndMarkerTick { get; }
}

public sealed class CanonicalCompiledResult
{
    private readonly CanonicalMidiEvent[] _events;
    private readonly ChannelUnitAllocation[] _allocations;
    private readonly CompilerDiagnostic[] _diagnostics;

    internal CanonicalCompiledResult(
        MidoraId projectId,
        int ticksPerQuarterNote,
        long startTick,
        long endTick,
        CanonicalMidiEvent[] events,
        CanonicalConductor conductor,
        ChannelUnitAllocation[] allocations,
        CompilerDiagnostic[] diagnostics,
        CompilationPurpose purpose,
        bool isPartial,
        bool isConsumable,
        long fingerprint,
        CompilationStatistics statistics)
    {
        ProjectId = projectId;
        TicksPerQuarterNote = ticksPerQuarterNote;
        StartTick = startTick;
        EndTick = endTick;
        _events = events;
        Conductor = conductor;
        _allocations = allocations;
        _diagnostics = diagnostics;
        Purpose = purpose;
        IsPartial = isPartial;
        IsConsumable = isConsumable;
        Fingerprint = fingerprint;
        Statistics = statistics;
    }

    public MidoraId ProjectId { get; }
    public int TicksPerQuarterNote { get; }
    public long StartTick { get; }
    public long EndTick { get; }
    public CompilationPurpose Purpose { get; }
    public bool IsPartial { get; }
    public bool IsConsumable { get; }
    public long Fingerprint { get; }
    public CompilationStatistics Statistics { get; }
    public CanonicalConductor Conductor { get; }
    public ReadOnlySpan<CanonicalMidiEvent> Events => _events;
    public ReadOnlySpan<CanonicalTempo> Tempos => Conductor.Tempos;
    public ReadOnlySpan<ChannelUnitAllocation> Allocations => _allocations;
    public IReadOnlyList<CompilerDiagnostic> Diagnostics => _diagnostics;
}

public readonly record struct CompilationStatistics(
    int SourceTrackCount,
    int RecompiledTrackCount,
    int ReusedTrackCount,
    int ExpandedInstanceCount,
    int EventCount,
    int PeakChannelUnitCount,
    TimeSpan Elapsed);
