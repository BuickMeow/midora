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

public enum SourceOrigin
{
    Unspecified,
    TemplateEvent,
    ValueCurve,
    LogicalParameterMapping,
    MergedInitialState,
    ProjectResetDefaults,
    RangeRestore,
    CompilerBoundaryCleanup
}

public readonly record struct SourceReference(
    MidoraId TrackId = default,
    MidoraId SegmentId = default,
    MidoraId LogicalNoteId = default,
    MidoraId EventInstrumentId = default,
    MidoraId SubVoiceId = default,
    MidoraId SourceEventId = default,
    long Tick = -1,
    MidoraId LogicalParameterId = default,
    MidoraId LogicalParameterMappingId = default,
    MidoraId MappingStepId = default,
    MidoraId MappingFunctionId = default,
    MidoraId ValueCurveId = default,
    MidoraId EnvelopeId = default,
    SourceOrigin Origin = SourceOrigin.Unspecified);

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
    MidiExport,
    AudioRender,
    LogicalTrackAudioRender
}

public enum CompilationFailureStage
{
    SemanticValidation,
    InstanceExpansion,
    OverlapValidation,
    ResourceAllocation,
    WarningPolicy
}

public sealed class CompilationRequest
{
    public CompilationPurpose Purpose { get; init; } = CompilationPurpose.FullProject;
    public long StartTick { get; init; }
    public long? EndTick { get; init; }
    public bool TreatWarningsAsErrors { get; init; }
    public bool CollectDebugDiagnostics { get; init; }
    public HashSet<MidoraId>? IncludedTrackIds { get; init; }
    public HashSet<MidoraId>? IncludedSubVoiceIds { get; init; }

    /// <summary>
    /// Internal held-preview causal window. The instance gate remains open for the whole window,
    /// MappingContext.GateLength is Int64.MaxValue, and the range end must not synthesize cleanup.
    /// </summary>
    public bool HeldPreviewGateOpen { get; init; }

    /// <summary>
    /// Internal held-preview Gate End override. The instance ends at its source Note length, while
    /// MappingContext.GateLength observes the frozen user/draft Gate length from Gate Start.
    /// </summary>
    public long? HeldPreviewFinalGateLengthTicks { get; init; }
}

public enum CompilationEndTickSource
{
    ExplicitRequest,
    ProjectEndMarker,
    NaturalContent
}

public sealed class CompilationContextSummary
{
    private readonly MidoraId[] _includedTrackIds;
    private readonly MidoraId[] _includedSubVoiceIds;

    internal CompilationContextSummary(
        CompilationRequest request,
        long resolvedEndTick,
        CompilationEndTickSource endTickSource)
    {
        ArgumentNullException.ThrowIfNull(request);
        Purpose = request.Purpose;
        StartTick = request.StartTick;
        RequestedEndTick = request.EndTick;
        EndTick = resolvedEndTick;
        EndTickSource = endTickSource;
        IncludesAllTracks = request.IncludedTrackIds is null;
        IncludesAllSubVoices = request.IncludedSubVoiceIds is null;
        _includedTrackIds = request.IncludedTrackIds?
            .OrderBy(id => id)
            .ToArray() ?? [];
        _includedSubVoiceIds = request.IncludedSubVoiceIds?
            .OrderBy(id => id)
            .ToArray() ?? [];
        TreatWarningsAsErrors = request.TreatWarningsAsErrors;
        CollectDebugDiagnostics = request.CollectDebugDiagnostics;
    }

    public CompilationPurpose Purpose { get; }
    public long StartTick { get; }
    public long? RequestedEndTick { get; }
    public long EndTick { get; }
    public CompilationEndTickSource EndTickSource { get; }
    public bool IncludesAllTracks { get; }
    public bool IncludesAllSubVoices { get; }
    public bool TreatWarningsAsErrors { get; }
    public bool CollectDebugDiagnostics { get; }
    public bool IsFullProject => Purpose == CompilationPurpose.FullProject && IncludesAllTracks;
    public bool IsPlayback => Purpose == CompilationPurpose.Playback;
    public bool IsPreview => Purpose is CompilationPurpose.SegmentPreview
        or CompilationPurpose.EventInstrumentPreview;
    public bool IsMidiExportPreparation => Purpose == CompilationPurpose.MidiExport;
    public bool IsAudioRenderPreparation => Purpose is CompilationPurpose.AudioRender
        or CompilationPurpose.LogicalTrackAudioRender;
    public bool UsesProjectEndMarkerAsDefault => EndTickSource == CompilationEndTickSource.ProjectEndMarker;
    public ReadOnlySpan<MidoraId> IncludedTrackIds => _includedTrackIds;
    public ReadOnlySpan<MidoraId> IncludedSubVoiceIds => _includedSubVoiceIds;
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
    MidoraId SegmentId,
    MidoraId EventInstrumentId,
    MidoraId InstanceId,
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
public readonly record struct CanonicalEndMarker(MidoraId Id, long Tick);

public sealed class CanonicalConductor
{
    private readonly CanonicalTempo[] _tempos;
    private readonly CanonicalTimeSignature[] _timeSignatures;
    private readonly CanonicalTimeSignature[] _sourceTimeSignatureMap;
    private readonly CanonicalKeySignature[] _keySignatures;
    private readonly CanonicalMarker[] _markers;

    internal CanonicalConductor(
        CanonicalTempo[] tempos,
        CanonicalTimeSignature[] timeSignatures,
        CanonicalTimeSignature[] sourceTimeSignatureMap,
        CanonicalKeySignature[] keySignatures,
        CanonicalMarker[] markers,
        CanonicalEndMarker? endMarker)
    {
        _tempos = tempos;
        _timeSignatures = timeSignatures;
        _sourceTimeSignatureMap = sourceTimeSignatureMap;
        _keySignatures = keySignatures;
        _markers = markers;
        EndMarker = endMarker;
    }

    public ReadOnlySpan<CanonicalTempo> Tempos => _tempos;
    public ReadOnlySpan<CanonicalTimeSignature> TimeSignatures => _timeSignatures;
    public ReadOnlySpan<CanonicalTimeSignature> SourceTimeSignatureMap =>
        _sourceTimeSignatureMap;
    public ReadOnlySpan<CanonicalKeySignature> KeySignatures => _keySignatures;
    public ReadOnlySpan<CanonicalMarker> Markers => _markers;
    public CanonicalEndMarker? EndMarker { get; }
    public long? EndMarkerTick => EndMarker?.Tick;
}

public sealed class CanonicalCompiledResult
{
    private readonly CanonicalMidiEvent[] _events;
    private readonly ChannelUnitAllocation[] _allocations;
    private readonly CompilerDiagnostic[] _diagnostics;

    internal CanonicalCompiledResult(
        int ticksPerQuarterNote,
        CompilationContextSummary context,
        CanonicalMidiEvent[] events,
        CanonicalConductor conductor,
        ChannelUnitAllocation[] allocations,
        CompilerDiagnostic[] diagnostics,
        bool isPartial,
        bool isConsumable,
        CompilationFailureStage? failureStage,
        long fingerprint,
        CompilationStatistics statistics)
    {
        TicksPerQuarterNote = ticksPerQuarterNote;
        Context = context ?? throw new ArgumentNullException(nameof(context));
        StartTick = context.StartTick;
        EndTick = context.EndTick;
        _events = events;
        Conductor = conductor;
        _allocations = allocations;
        _diagnostics = diagnostics;
        Purpose = context.Purpose;
        IsPartial = isPartial;
        IsConsumable = isConsumable;
        FailureStage = failureStage;
        Fingerprint = fingerprint;
        Statistics = statistics;
    }

    public int TicksPerQuarterNote { get; }
    public CompilationContextSummary Context { get; }
    public long StartTick { get; }
    public long EndTick { get; }
    public CompilationPurpose Purpose { get; }
    public bool IsPartial { get; }
    public bool IsConsumable { get; }
    public CompilationFailureStage? FailureStage { get; }
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
    int ExpandedInstanceCount,
    int EventCount,
    int PeakChannelUnitCount)
{
    public int ExpandedSegmentCount { get; init; }
    public int ParticipatingEventInstrumentCount { get; init; }
    public int ParticipatingSubVoiceCount { get; init; }
    public int UsedPortCount { get; init; }
    public ResourceShortageDetails? ResourceShortage { get; init; }
}

public sealed class ResourceShortageDetails
{
    private readonly MidoraId[] _trackIds;
    private readonly MidoraId[] _segmentIds;
    private readonly MidoraId[] _logicalNoteIds;
    private readonly MidoraId[] _eventInstrumentIds;
    private readonly MidoraId[] _subVoiceIds;

    internal ResourceShortageDetails(
        TickRange range,
        int requestedChannelUnitCount,
        int availableChannelUnitCount,
        IEnumerable<MidoraId> trackIds,
        IEnumerable<MidoraId> segmentIds,
        IEnumerable<MidoraId> logicalNoteIds,
        IEnumerable<MidoraId> eventInstrumentIds,
        IEnumerable<MidoraId> subVoiceIds)
    {
        Range = range;
        RequestedChannelUnitCount = requestedChannelUnitCount;
        AvailableChannelUnitCount = availableChannelUnitCount;
        _trackIds = FreezeIds(trackIds);
        _segmentIds = FreezeIds(segmentIds);
        _logicalNoteIds = FreezeIds(logicalNoteIds);
        _eventInstrumentIds = FreezeIds(eventInstrumentIds);
        _subVoiceIds = FreezeIds(subVoiceIds);
    }

    public TickRange Range { get; }
    public int RequestedChannelUnitCount { get; }
    public int AvailableChannelUnitCount { get; }
    public ReadOnlySpan<MidoraId> TrackIds => _trackIds;
    public ReadOnlySpan<MidoraId> SegmentIds => _segmentIds;
    public ReadOnlySpan<MidoraId> LogicalNoteIds => _logicalNoteIds;
    public ReadOnlySpan<MidoraId> EventInstrumentIds => _eventInstrumentIds;
    public ReadOnlySpan<MidoraId> SubVoiceIds => _subVoiceIds;

    private static MidoraId[] FreezeIds(IEnumerable<MidoraId> ids) => ids
        .Distinct()
        .OrderBy(id => id)
        .ToArray();
}

public readonly record struct CompilerRunTelemetry(
    int RecompiledTrackCount,
    int ReusedTrackCount)
{
    public int RecompiledSegmentCount { get; init; }
    public int ReusedSegmentCount { get; init; }
    public int StateConvergenceCount { get; init; }
    public long? EarliestDirtyTick { get; init; }
}
