using Midora.Domain;
using Midora.Desktop.Presentation.Interaction;
using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace Midora.Desktop.Presentation.Rendering;

[Flags]
public enum TimelineItemState
{
    None = 0,
    Selected = 1 << 0,
    Primary = 1 << 1,
    Muted = 1 << 2,
    OutsideActiveRange = 1 << 3,
    Invalid = 1 << 4,
    Broken = 1 << 5,
    Incompatible = 1 << 6,
    Damaged = 1 << 7,
    Preview = 1 << 8,
    HitTestDisabled = 1 << 9
}

public enum TimelineItemKind
{
    Segment,
    LogicalNote,
    DirectMidiNote,
    DirectMidiEvent,
    OpaqueMidiEvent,
    LogicalParameterPoint,
    LogicalParameterCurve,
    TemplateNote,
    TemplateEvent,
    ConductorEvent,
    Marker,
    ProjectEndMarker,
    LifecycleBoundary
    , Velocity
}

[Flags]
public enum TimelineLaneState
{
    None = 0,
    Muted = 1 << 0,
    Solo = 1 << 1
}

public enum ArrangementLaneKind
{
    Conductor,
    EventInstrument,
    MidiChannelRoot,
    LogicalTrack,
    PureMidiTrack,
    DamagedEventInstrument,
    DamagedMidiChannelRoot,
    DamagedLogicalTrack,
    DamagedPureMidiTrack
}

public readonly record struct ArrangementLaneDescriptor(
    int Lane,
    ArrangementLaneKind Kind,
    MidoraId? ObjectId,
    MidoraId? ParentId,
    int Depth,
    bool IsExpanded,
    bool HasChildren,
    bool CanContainSegments)
{
    /// <summary>
    /// Invisible state-sharing identity. For Logical Tracks this is an Event
    /// Instrument Usage; for Pure MIDI Tracks this is a MIDI Channel Root.
    /// </summary>
    public MidoraId? SharedGroupId { get; init; }

    public bool IsSharedGroup { get; init; }
    public bool IsSharedGroupStart { get; init; }
    public bool IsSharedGroupEnd { get; init; }
    public int SharedGroupMemberCount { get; init; }
}

public readonly record struct TimelineRenderItem(
    MidoraId Id,
    TimelineItemKind Kind,
    long StartTick,
    long EndTick,
    int Lane,
    double Value,
    int ZIndex,
    TimelineItemState State)
{
    public long Length => checked(EndTick - StartTick);
    public double SecondaryValue { get; init; }
    public CurveInterpolation Interpolation { get; init; } = CurveInterpolation.Linear;
    public string Label { get; init; } = string.Empty;
    public uint AccentColor { get; init; }
}

public readonly record struct TimelineSegmentPreviewNote(
    double NormalizedStart,
    double NormalizedEnd,
    int Pitch);

public readonly record struct TimelineSegmentPreviewEvent(
    double NormalizedTick,
    double NormalizedValue);

public interface ITimelineSegmentPreviewSource
{
    bool HasNoteContent { get; }
    bool HasEventContent { get; }
    ulong NoteContentFingerprint { get; }
    ulong EventContentFingerprint { get; }
    void QueryNotes(
        double normalizedStart,
        double normalizedEnd,
        List<TimelineSegmentPreviewNote> destination);
    void QueryEvents(
        double normalizedStart,
        double normalizedEnd,
        List<TimelineSegmentPreviewEvent> destination);

    void VisitNotes(
        double normalizedStart,
        double normalizedEnd,
        Action<TimelineSegmentPreviewNote> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        List<TimelineSegmentPreviewNote> values = [];
        QueryNotes(normalizedStart, normalizedEnd, values);
        foreach (TimelineSegmentPreviewNote value in values) visitor(value);
    }

    void VisitEvents(
        double normalizedStart,
        double normalizedEnd,
        Action<TimelineSegmentPreviewEvent> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        List<TimelineSegmentPreviewEvent> values = [];
        QueryEvents(normalizedStart, normalizedEnd, values);
        foreach (TimelineSegmentPreviewEvent value in values) visitor(value);
    }

    ulong GetTileContentFingerprint(
        bool eventLayer,
        double deviceSegmentWidth,
        long tileX)
    {
        ulong content = eventLayer ? EventContentFingerprint : NoteContentFingerprint;
        ulong transform = TimelineContentFingerprint.Combine(
            unchecked((ulong)BitConverter.DoubleToInt64Bits(deviceSegmentWidth)),
            unchecked((ulong)tileX));
        return TimelineContentFingerprint.Combine(content, transform);
    }
}

public sealed class TimelineSegmentPreview
{
    private const int MaximumTileFingerprintEntries = 8192;
    private readonly TimelineSegmentPreviewNote[] _notes;
    private readonly double[] _noteMaximumEndPrefix;
    private readonly TimelineSegmentPreviewEvent[] _events;
    private readonly ITimelineSegmentPreviewSource? _source;
    private readonly ConcurrentDictionary<SegmentPreviewTileFingerprintKey, Lazy<ulong>>
        _tileFingerprints = [];
    private readonly ConcurrentQueue<SegmentPreviewTileFingerprintKey> _tileFingerprintOrder = [];

    public TimelineSegmentPreview(
        MidoraId segmentId,
        IEnumerable<TimelineSegmentPreviewNote> notes,
        IEnumerable<TimelineSegmentPreviewEvent>? events = null)
    {
        if (segmentId.Value <= 0) throw new ArgumentOutOfRangeException(nameof(segmentId));
        ArgumentNullException.ThrowIfNull(notes);
        TimelineSegmentPreviewNote[] materialized = notes.ToArray();
        foreach (TimelineSegmentPreviewNote note in materialized)
        {
            if (!double.IsFinite(note.NormalizedStart)
                || !double.IsFinite(note.NormalizedEnd)
                || note.NormalizedStart < 0
                || note.NormalizedEnd > 1
                || note.NormalizedEnd <= note.NormalizedStart
                || note.Pitch is < 0 or > 127)
            {
                throw new ArgumentException("A Segment preview note is outside its normalized range.", nameof(notes));
            }
        }
        Array.Sort(materialized, static (left, right) =>
        {
            int value = left.NormalizedStart.CompareTo(right.NormalizedStart);
            if (value != 0) return value;
            value = left.NormalizedEnd.CompareTo(right.NormalizedEnd);
            return value != 0 ? value : left.Pitch.CompareTo(right.Pitch);
        });
        SegmentId = segmentId;
        _notes = materialized;
        _noteMaximumEndPrefix = new double[materialized.Length];
        double maximumEnd = 0;
        for (int index = 0; index < materialized.Length; index++)
        {
            maximumEnd = Math.Max(maximumEnd, materialized[index].NormalizedEnd);
            _noteMaximumEndPrefix[index] = maximumEnd;
        }
        Notes = Array.AsReadOnly(_notes);
        TimelineSegmentPreviewEvent[] materializedEvents = events?.ToArray() ?? [];
        foreach (TimelineSegmentPreviewEvent value in materializedEvents)
        {
            if (!double.IsFinite(value.NormalizedTick)
                || !double.IsFinite(value.NormalizedValue)
                || value.NormalizedTick < 0
                || value.NormalizedTick > 1
                || value.NormalizedValue < 0
                || value.NormalizedValue > 1)
            {
                throw new ArgumentException(
                    "A Segment preview event is outside its normalized range.",
                    nameof(events));
            }
        }
        Array.Sort(materializedEvents, static (left, right) =>
        {
            int value = left.NormalizedTick.CompareTo(right.NormalizedTick);
            return value != 0 ? value : left.NormalizedValue.CompareTo(right.NormalizedValue);
        });
        _events = materializedEvents;
        Events = Array.AsReadOnly(_events);
        NoteContentFingerprint = TimelineContentFingerprint.ForSegmentPreviewNotes(_notes);
        EventContentFingerprint = TimelineContentFingerprint.ForSegmentPreviewEvents(_events);
        HasNoteContent = _notes.Length != 0;
        HasEventContent = _events.Length != 0;
        ContentFingerprint = TimelineContentFingerprint.Combine(
            NoteContentFingerprint,
            EventContentFingerprint);
    }

    public TimelineSegmentPreview(MidoraId segmentId, ITimelineSegmentPreviewSource source)
    {
        if (segmentId.Value <= 0) throw new ArgumentOutOfRangeException(nameof(segmentId));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        SegmentId = segmentId;
        _notes = [];
        _noteMaximumEndPrefix = [];
        _events = [];
        Notes = Array.Empty<TimelineSegmentPreviewNote>();
        Events = Array.Empty<TimelineSegmentPreviewEvent>();
        HasNoteContent = source.HasNoteContent;
        HasEventContent = source.HasEventContent;
        NoteContentFingerprint = source.NoteContentFingerprint;
        EventContentFingerprint = source.EventContentFingerprint;
        ContentFingerprint = TimelineContentFingerprint.Combine(
            NoteContentFingerprint,
            EventContentFingerprint);
    }

    public MidoraId SegmentId { get; }
    public IReadOnlyList<TimelineSegmentPreviewNote> Notes { get; }
    public IReadOnlyList<TimelineSegmentPreviewEvent> Events { get; }
    public bool HasNoteContent { get; }
    public bool HasEventContent { get; }
    public ulong NoteContentFingerprint { get; }
    public ulong EventContentFingerprint { get; }
    public ulong ContentFingerprint { get; }

    internal void QueryNotes(
        double normalizedStart,
        double normalizedEnd,
        List<TimelineSegmentPreviewNote> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!double.IsFinite(normalizedStart)
            || !double.IsFinite(normalizedEnd)
            || normalizedEnd <= normalizedStart)
        {
            return;
        }
        if (_source is not null)
        {
            _source.QueryNotes(normalizedStart, normalizedEnd, destination);
            return;
        }
        int first = FirstPrefixEndGreaterThan(normalizedStart);
        int lastExclusive = FirstNoteStartAtOrAfter(normalizedEnd);
        for (int index = first; index < lastExclusive; index++)
        {
            TimelineSegmentPreviewNote note = _notes[index];
            if (note.NormalizedEnd > normalizedStart)
            {
                destination.Add(note);
            }
        }
    }

    internal void QueryEvents(
        double normalizedStart,
        double normalizedEnd,
        List<TimelineSegmentPreviewEvent> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!double.IsFinite(normalizedStart)
            || !double.IsFinite(normalizedEnd)
            || normalizedEnd <= normalizedStart)
        {
            return;
        }
        if (_source is not null)
        {
            _source.QueryEvents(normalizedStart, normalizedEnd, destination);
            return;
        }
        int first = FirstEventAtOrAfter(normalizedStart);
        int lastExclusive = FirstEventAtOrAfter(normalizedEnd);
        for (int index = first; index < lastExclusive; index++)
        {
            destination.Add(_events[index]);
        }
    }

    internal void VisitNotes(
        double normalizedStart,
        double normalizedEnd,
        Action<TimelineSegmentPreviewNote> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        if (!double.IsFinite(normalizedStart)
            || !double.IsFinite(normalizedEnd)
            || normalizedEnd <= normalizedStart)
        {
            return;
        }
        if (_source is not null)
        {
            _source.VisitNotes(normalizedStart, normalizedEnd, visitor);
            return;
        }
        int first = FirstPrefixEndGreaterThan(normalizedStart);
        int lastExclusive = FirstNoteStartAtOrAfter(normalizedEnd);
        for (int index = first; index < lastExclusive; index++)
        {
            TimelineSegmentPreviewNote note = _notes[index];
            if (note.NormalizedEnd > normalizedStart) visitor(note);
        }
    }

    internal void VisitEvents(
        double normalizedStart,
        double normalizedEnd,
        Action<TimelineSegmentPreviewEvent> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        if (!double.IsFinite(normalizedStart)
            || !double.IsFinite(normalizedEnd)
            || normalizedEnd <= normalizedStart)
        {
            return;
        }
        if (_source is not null)
        {
            _source.VisitEvents(normalizedStart, normalizedEnd, visitor);
            return;
        }
        int first = FirstEventAtOrAfter(normalizedStart);
        int lastExclusive = FirstEventAtOrAfter(normalizedEnd);
        for (int index = first; index < lastExclusive; index++) visitor(_events[index]);
    }

    internal ulong GetTileContentFingerprint(
        bool eventLayer,
        double deviceSegmentWidth,
        long tileX)
    {
        if (!double.IsFinite(deviceSegmentWidth) || deviceSegmentWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deviceSegmentWidth));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(tileX);
        SegmentPreviewTileFingerprintKey key = new(
            eventLayer,
            BitConverter.DoubleToInt64Bits(deviceSegmentWidth),
            tileX);
        Lazy<ulong> created = new(
                () => _source is not null
                    ? _source.GetTileContentFingerprint(eventLayer, deviceSegmentWidth, tileX)
                    : eventLayer
                        ? TimelineSegmentPreviewRasterizer.ComputeEventTileContentFingerprint(
                            this,
                            deviceSegmentWidth,
                            tileX)
                        : TimelineSegmentPreviewRasterizer.ComputeNoteTileContentFingerprint(
                            this,
                            deviceSegmentWidth,
                            tileX),
                LazyThreadSafetyMode.ExecutionAndPublication);
        Lazy<ulong> pending = _tileFingerprints.GetOrAdd(key, created);
        if (ReferenceEquals(created, pending))
        {
            _tileFingerprintOrder.Enqueue(key);
            EvictOldTileFingerprint();
        }
        try
        {
            return pending.Value;
        }
        catch
        {
            _tileFingerprints.TryRemove(
                new KeyValuePair<SegmentPreviewTileFingerprintKey, Lazy<ulong>>(key, pending));
            throw;
        }
    }

    private int FirstPrefixEndGreaterThan(double value)
    {
        int low = 0;
        int high = _noteMaximumEndPrefix.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (_noteMaximumEndPrefix[middle] <= value) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private int FirstNoteStartAtOrAfter(double value)
    {
        int low = 0;
        int high = _notes.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (_notes[middle].NormalizedStart < value) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private int FirstEventAtOrAfter(double value)
    {
        int low = 0;
        int high = _events.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (_events[middle].NormalizedTick < value) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private void EvictOldTileFingerprint()
    {
        while (_tileFingerprints.Count > MaximumTileFingerprintEntries
            && _tileFingerprintOrder.TryDequeue(out SegmentPreviewTileFingerprintKey oldest))
        {
            _tileFingerprints.TryRemove(oldest, out _);
            break;
        }
    }

    private readonly record struct SegmentPreviewTileFingerprintKey(
        bool EventLayer,
        long DeviceSegmentWidthKey,
        long TileX);
}

public interface ITimelineRenderItemSource
{
    long Count { get; }
    long MaximumEndTick { get; }
    ulong ContentFingerprint { get; }
    bool HasHitTestableItems => Count > 0;

    /// <summary>
    /// Returns a bounded-cost fingerprint for a presentation range. Large
    /// paged sources override this with page metadata; the default deliberately
    /// invalidates broadly but never enumerates source items on the UI thread.
    /// </summary>
    ulong GetRangeFingerprint(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive)
    {
        ulong range = TimelineContentFingerprint.Combine(
            unchecked((ulong)startTick),
            unchecked((ulong)endTick));
        range = TimelineContentFingerprint.Combine(
            range,
            TimelineContentFingerprint.Combine(
                unchecked((ulong)firstLane),
                unchecked((ulong)lastLaneExclusive)));
        return TimelineContentFingerprint.Combine(ContentFingerprint, range);
    }

    void QueryInto(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        List<TimelineRenderItem> destination);

    void VisitInto(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        Action<TimelineRenderItem> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        List<TimelineRenderItem> values = [];
        QueryInto(startTick, endTick, firstLane, lastLaneExclusive, values);
        foreach (TimelineRenderItem value in values) visitor(value);
    }

    bool TryGetById(MidoraId id, out TimelineRenderItem item);

    void QueryByIds(
        IReadOnlySet<MidoraId> ids,
        List<TimelineRenderItem> destination)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(destination);
        foreach (MidoraId id in ids)
        {
            if (TryGetById(id, out TimelineRenderItem item))
                destination.Add(item);
        }
    }

    IEnumerable<TimelineRenderItem> EnumerateAll();

    void AccumulateOverviewDensity(long extent, Span<int> destination)
    {
    }
}

/// <summary>
/// Exact cache-only access for an out-of-core item source. UI-thread consumers
/// use these members instead of the blocking <see cref="ITimelineRenderItemSource"/>
/// methods. A false result means Pending, never an empty range.
/// </summary>
public interface IPreparedTimelineRenderItemSource : ITimelineRenderItemSource
{
    bool TryQueryIntoCached(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        List<TimelineRenderItem> destination);

    void PrefetchRange(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        CancellationToken cancellationToken);

    bool TryQueryByIdsCached(
        IReadOnlySet<MidoraId> ids,
        List<TimelineRenderItem> destination);

    void PrefetchIds(
        IReadOnlySet<MidoraId> ids,
        CancellationToken cancellationToken);
}

/// <summary>
/// Marks an item source whose range fingerprint is metadata-only and cannot
/// decode pages, wait for I/O, or otherwise block the WPF render thread.
/// </summary>
public interface INonBlockingTimelineFingerprintSource : ITimelineRenderItemSource
{
}

public enum TimelineRasterAggregateKind
{
    PianoNotes,
    Velocity,
    EventPoints
}

/// <summary>
/// Optional raster-only projection whose work is bounded by the fixed output
/// column count rather than by the number of source objects. Aggregate output
/// is deliberately excluded from all semantic query and hit-test APIs.
/// </summary>
public interface ITimelineRasterAggregateSource
{
    bool TryAccumulateRasterColumns(
        TimelineRasterAggregateKind kind,
        TimelineRasterColumnProjection projection,
        int firstLane,
        int lastLaneExclusive,
        Span<TimelineRasterColumnSummary> destination,
        out int sourceWorkCount);
}

public readonly record struct TimelineSelectionMetrics(
    int Count,
    long MinimumStartTick,
    long MaximumEndTick,
    int MinimumLane,
    int MaximumLane,
    double MinimumValue,
    double MaximumValue,
    TimelineRenderItem EarliestItem);

/// <summary>
/// Supplies the complete, bounded-cost horizontal overview for an editor.
/// Implementations may project paged content through summaries; callers must
/// not infer editable objects or hit-test results from these presentation lines.
/// </summary>
public interface ITimelineOverviewSource
{
    ulong ContentFingerprint { get; }

    /// <summary>
    /// Returns a bounded-cost fingerprint for a presentation range. Large
    /// paged sources override this with page metadata; the default deliberately
    /// invalidates broadly but never enumerates source items on the UI thread.
    /// </summary>
    ulong GetRangeFingerprint(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive)
    {
        ulong range = TimelineContentFingerprint.Combine(
            unchecked((ulong)startTick),
            unchecked((ulong)endTick));
        range = TimelineContentFingerprint.Combine(
            range,
            TimelineContentFingerprint.Combine(
                unchecked((ulong)firstLane),
                unchecked((ulong)lastLaneExclusive)));
        return TimelineContentFingerprint.Combine(ContentFingerprint, range);
    }
    long MaximumEndTick { get; }

    void Accumulate(
        long extent,
        Span<byte> noteStartColumns,
        Span<byte> eventColumns);
}

public sealed class MaterializedTimelineOverviewSource : ITimelineOverviewSource
{
    private readonly long[] _noteStartTicks;
    private readonly long[] _eventTicks;

    public MaterializedTimelineOverviewSource(
        IEnumerable<long> noteStartTicks,
        IEnumerable<long> eventTicks)
    {
        ArgumentNullException.ThrowIfNull(noteStartTicks);
        ArgumentNullException.ThrowIfNull(eventTicks);
        _noteStartTicks = noteStartTicks.ToArray();
        _eventTicks = eventTicks.ToArray();
        if (_noteStartTicks.Any(static tick => tick < 0)
            || _eventTicks.Any(static tick => tick < 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(noteStartTicks),
                "Timeline overview ticks must be non-negative.");
        }
        ContentFingerprint = TimelineContentFingerprint.ForOverviewTicks(
            _noteStartTicks,
            _eventTicks);
        long maximumTick = _noteStartTicks.Concat(_eventTicks).DefaultIfEmpty(-1).Max();
        MaximumEndTick = maximumTick < 0
            ? 0
            : maximumTick == long.MaxValue
                ? long.MaxValue
                : maximumTick + 1;
    }

    public ulong ContentFingerprint { get; }
    public long MaximumEndTick { get; }

    public void Accumulate(
        long extent,
        Span<byte> noteStartColumns,
        Span<byte> eventColumns)
    {
        ValidateOverviewColumns(extent, noteStartColumns, eventColumns);
        foreach (long tick in _noteStartTicks)
            MarkOverviewColumn(noteStartColumns, tick, extent);
        foreach (long tick in _eventTicks)
            MarkOverviewColumn(eventColumns, tick, extent);
    }

    public static void MarkOverviewColumn(Span<byte> destination, long tick, long extent)
    {
        if (destination.IsEmpty) return;
        int x = Math.Clamp(
            (int)(Math.Max(0, tick) / (double)extent * destination.Length),
            0,
            destination.Length - 1);
        destination[x] = 1;
    }

    public static void ValidateOverviewColumns(
        long extent,
        Span<byte> noteStartColumns,
        Span<byte> eventColumns)
    {
        if (extent <= 0) throw new ArgumentOutOfRangeException(nameof(extent));
        if (noteStartColumns.Length != eventColumns.Length)
        {
            throw new ArgumentException("Timeline overview channels must have equal widths.");
        }
    }
}

public sealed class TimelineSelectionSnapshot
{
    private readonly ImmutableHashSet<MidoraId> _ids;
    private readonly Dictionary<TimelineItemKind, TimelineSelectionMetrics> _metrics;

    public TimelineSelectionSnapshot(
        long revision,
        IEnumerable<MidoraId> ids,
        MidoraId? primary,
        IEnumerable<TimelineRenderItem>? resolvedItems = null)
        : this(
            revision,
            ids as ImmutableHashSet<MidoraId> ?? ids.ToImmutableHashSet(),
            primary,
            resolvedItems,
            metrics: null,
            trustIds: false)
    {
    }

    private TimelineSelectionSnapshot(
        long revision,
        ImmutableHashSet<MidoraId> ids,
        MidoraId? primary,
        IEnumerable<TimelineRenderItem>? resolvedItems,
        IReadOnlyDictionary<TimelineItemKind, TimelineSelectionMetrics>? metrics,
        bool trustIds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        ArgumentNullException.ThrowIfNull(ids);
        _ids = ids;
        if (!trustIds && _ids.Any(static id => id.Value <= 0))
        {
            throw new ArgumentException("Selection contains an invalid object reference.", nameof(ids));
        }
        if (primary is MidoraId primaryId && !_ids.Contains(primaryId))
        {
            throw new ArgumentException("Primary selection must belong to the selection set.", nameof(primary));
        }
        Revision = revision;
        Primary = primary;
        _metrics = metrics is null ? BuildMetrics(resolvedItems) : new(metrics);
    }

    public TimelineSelectionSnapshot(
        long revision,
        IEnumerable<MidoraId> ids,
        MidoraId? primary,
        IReadOnlyDictionary<TimelineItemKind, TimelineSelectionMetrics> metrics)
        : this(
            revision,
            ids as ImmutableHashSet<MidoraId> ?? ids.ToImmutableHashSet(),
            primary,
            resolvedItems: null,
            metrics,
            trustIds: false)
    {
    }

    internal static TimelineSelectionSnapshot FromTrustedIds(
        long revision,
        ImmutableHashSet<MidoraId> ids,
        MidoraId? primary,
        IEnumerable<TimelineRenderItem>? resolvedItems = null,
        IReadOnlyDictionary<TimelineItemKind, TimelineSelectionMetrics>? metrics = null) =>
        new(revision, ids, primary, resolvedItems, metrics, trustIds: true);

    public static TimelineSelectionSnapshot FromWorkspaceSelection(
        WorkspaceSelection selection,
        IEnumerable<TimelineRenderItem>? resolvedItems = null,
        IReadOnlyDictionary<TimelineItemKind, TimelineSelectionMetrics>? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return FromTrustedIds(
            selection.Revision,
            selection.SharedIds,
            selection.Primary,
            resolvedItems,
            metrics);
    }

    public long Revision { get; }
    public MidoraId? Primary { get; }
    public int Count => _ids.Count;
    public IEnumerable<MidoraId> Ids => _ids;
    public IReadOnlySet<MidoraId> IdSet => _ids;
    public bool Contains(MidoraId id) => _ids.Contains(id);

    internal ImmutableHashSet<MidoraId> SharedIds => _ids;

    public bool TryGetMetrics(
        TimelineItemKind kind,
        out TimelineSelectionMetrics metrics) =>
        _metrics.TryGetValue(kind, out metrics);

    internal ulong GetRangeRevisionFingerprint(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive)
    {
        if (Count == 0) return 0;
        if (_metrics.Count == 0) return unchecked((ulong)Revision);
        foreach (TimelineSelectionMetrics metrics in _metrics.Values)
        {
            if (metrics.MinimumStartTick < endTick
                && metrics.MaximumEndTick > startTick
                && metrics.MinimumLane < lastLaneExclusive
                && metrics.MaximumLane >= firstLane)
            {
                return unchecked((ulong)Revision);
            }
        }
        return 0;
    }

    private Dictionary<TimelineItemKind, TimelineSelectionMetrics> BuildMetrics(
        IEnumerable<TimelineRenderItem>? resolvedItems)
    {
        Dictionary<TimelineItemKind, TimelineSelectionMetrics> result = [];
        if (resolvedItems is null) return result;
        foreach (TimelineRenderItem item in resolvedItems)
        {
            if (!_ids.Contains(item.Id)) continue;
            if (!result.TryGetValue(item.Kind, out TimelineSelectionMetrics current))
            {
                result.Add(item.Kind, new(
                    1,
                    item.StartTick,
                    item.EndTick,
                    item.Lane,
                    item.Lane,
                    item.Value,
                    item.Value,
                    item));
                continue;
            }
            TimelineRenderItem earliest = item.StartTick < current.EarliestItem.StartTick
                || item.StartTick == current.EarliestItem.StartTick
                && item.Id.CompareTo(current.EarliestItem.Id) < 0
                    ? item
                    : current.EarliestItem;
            result[item.Kind] = new(
                checked(current.Count + 1),
                Math.Min(current.MinimumStartTick, item.StartTick),
                Math.Max(current.MaximumEndTick, item.EndTick),
                Math.Min(current.MinimumLane, item.Lane),
                Math.Max(current.MaximumLane, item.Lane),
                Math.Min(current.MinimumValue, item.Value),
                Math.Max(current.MaximumValue, item.Value),
                earliest);
        }
        return result;
    }
}

public readonly record struct TimelineViewport(
    long StartTick,
    long EndTick,
    int FirstLane,
    int LaneCount,
    double Width,
    double Height,
    double LaneHeight)
{
    public long TickLength => checked(EndTick - StartTick);
    public int LastLaneExclusive => checked(FirstLane + LaneCount);
    public double PixelsPerTick => Width / TickLength;

    public void Validate()
    {
        if (StartTick < 0 || EndTick <= StartTick)
        {
            throw new ArgumentOutOfRangeException(nameof(EndTick));
        }
        if (FirstLane < 0 || LaneCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(LaneCount));
        }
        if (!double.IsFinite(Width) || Width <= 0
            || !double.IsFinite(Height) || Height <= 0
            || !double.IsFinite(LaneHeight) || LaneHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Width));
        }
    }

    public double TickToX(long tick)
    {
        Validate();
        return (tick - StartTick) * PixelsPerTick;
    }

    public long XToTick(double x)
    {
        Validate();
        if (!double.IsFinite(x))
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }
        double clamped = Math.Clamp(x, 0, Width);
        double value = StartTick + (clamped / Width * TickLength);
        return checked((long)Math.Round(value, MidpointRounding.AwayFromZero));
    }

    public long XToContainingTick(double x)
    {
        Validate();
        if (!double.IsFinite(x))
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }
        double clamped = Math.Clamp(x, 0, Math.BitDecrement(Width));
        double value = StartTick + (clamped / Width * TickLength);
        return Math.Clamp(
            checked((long)Math.Floor(value)),
            StartTick,
            checked(EndTick - 1));
    }

    public int YToLane(double y)
    {
        Validate();
        if (!double.IsFinite(y))
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }
        int relative = (int)Math.Floor(Math.Clamp(y, 0, Height - double.Epsilon) / LaneHeight);
        return Math.Clamp(checked(FirstLane + relative), FirstLane, LastLaneExclusive - 1);
    }
}

public sealed class TimelineRenderSnapshot
{
    private const int MaximumPianoFingerprintEntries = 16384;
    private const int MaximumConductorFingerprintEntries = 8192;
    private const int MaximumQueryScratchCapacity = 4096;
    private static readonly ConcurrentBag<List<TimelineRenderItem>> QueryScratchPool = [];
    private readonly ConcurrentDictionary<PianoTileFingerprintKey, Lazy<ulong>>
        _pianoTileFingerprints = [];
    private readonly ConcurrentDictionary<ConductorTileFingerprintKey, Lazy<ulong>>
        _conductorTileFingerprints = [];
    private readonly ConcurrentQueue<PianoTileFingerprintKey> _pianoTileFingerprintOrder = [];
    private readonly ConcurrentQueue<ConductorTileFingerprintKey> _conductorTileFingerprintOrder = [];
    private readonly ITimelineRenderItemSource? _itemSource;
    private readonly ITimelineOverviewSource? _overviewSource;
    private readonly long _materializedMaximumEndTick;
    private readonly int _materializedMaximumLane;
    private readonly Dictionary<int, SegmentPlacementInterval[]> _segmentIntervalsByLane;

    public TimelineRenderSnapshot(
        long semanticRevision,
        string projectionKey,
        IEnumerable<TimelineRenderItem> items,
        IReadOnlyList<string>? laneLabels = null,
        IReadOnlyList<TimelineLaneState>? laneStates = null,
        IReadOnlyDictionary<MidoraId, TimelineSegmentPreview>? segmentPreviews = null,
        IReadOnlyList<string>? laneSecondaryLabels = null,
        IReadOnlyList<uint>? laneColors = null,
        IReadOnlyList<ArrangementLaneDescriptor>? arrangementLanes = null,
        ITimelineRenderItemSource? itemSource = null,
        ITimelineOverviewSource? overviewSource = null)
    {
        if (semanticRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(semanticRevision));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionKey);
        ArgumentNullException.ThrowIfNull(items);

        TimelineRenderItem[] materialized = items.ToArray();
        foreach (TimelineRenderItem item in materialized)
        {
            if (item.Id.Value <= 0 || item.StartTick < 0 || item.EndTick <= item.StartTick || item.Lane < 0)
            {
                throw new ArgumentException("A timeline render item has invalid identity, range, or lane.", nameof(items));
            }
            if (!double.IsFinite(item.Value))
            {
                throw new ArgumentException("A timeline render item value must be finite.", nameof(items));
            }
        }

        SemanticRevision = semanticRevision;
        ProjectionKey = projectionKey.Trim();
        _itemSource = itemSource;
        _overviewSource = overviewSource;
        _materializedMaximumEndTick = materialized.Length == 0
            ? 0
            : materialized.Max(static value => value.EndTick);
        _materializedMaximumLane = materialized.Length == 0
            ? -1
            : materialized.Max(static value => value.Lane);
        _segmentIntervalsByLane = materialized
            .Where(static value => value.Kind == TimelineItemKind.Segment)
            .GroupBy(static value => value.Lane)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .OrderBy(static value => value.StartTick)
                    .ThenBy(static value => value.EndTick)
                    .ThenBy(static value => value.Id)
                    .Select(static value => new SegmentPlacementInterval(
                        value.StartTick,
                        value.EndTick))
                    .ToArray());
        Items = Array.AsReadOnly(materialized);
        LaneLabels = laneLabels is null
            ? Array.Empty<string>()
            : Array.AsReadOnly(laneLabels.Select(static label => label?.Trim() ?? string.Empty).ToArray());
        LaneStates = laneStates is null
            ? Array.Empty<TimelineLaneState>()
            : Array.AsReadOnly(laneStates.ToArray());
        LaneSecondaryLabels = laneSecondaryLabels is null
            ? Array.Empty<string>()
            : Array.AsReadOnly(laneSecondaryLabels.Select(static label => label?.Trim() ?? string.Empty).ToArray());
        LaneColors = laneColors is null
            ? Array.Empty<uint>()
            : Array.AsReadOnly(laneColors.ToArray());
        SegmentPreviews = segmentPreviews is null
            ? new Dictionary<MidoraId, TimelineSegmentPreview>()
            : new Dictionary<MidoraId, TimelineSegmentPreview>(segmentPreviews);
        ArrangementLanes = arrangementLanes is null
            ? Array.Empty<ArrangementLaneDescriptor>()
            : Array.AsReadOnly(arrangementLanes.ToArray());
        Index = new TimelineIntervalIndex(materialized);
        ItemsById = materialized
            .GroupBy(static item => item.Id)
            .ToDictionary(static group => group.Key, static group => group.OrderByDescending(item => item.ZIndex).First());
        ContentFingerprint = TimelineContentFingerprint.Combine(
            TimelineContentFingerprint.Combine(
                TimelineContentFingerprint.ForRenderItems(materialized),
                itemSource?.ContentFingerprint ?? 0),
            overviewSource?.ContentFingerprint ?? 0);
        ConductorPreviewFingerprint = TimelineContentFingerprint.ForConductorPreview(materialized);
        HasConductorPreviewItems = materialized.Any(static item => item.Kind is
            TimelineItemKind.ConductorEvent or TimelineItemKind.Marker);
        HasHitTestableItems = materialized.Any(static item =>
                !item.State.HasFlag(TimelineItemState.HitTestDisabled))
            || itemSource?.HasHitTestableItems == true;
    }

    public long SemanticRevision { get; }
    public string ProjectionKey { get; }
    public IReadOnlyList<TimelineRenderItem> Items { get; }
    public IReadOnlyList<string> LaneLabels { get; }
    public IReadOnlyList<TimelineLaneState> LaneStates { get; }
    public IReadOnlyList<string> LaneSecondaryLabels { get; }
    public IReadOnlyList<uint> LaneColors { get; }
    public IReadOnlyDictionary<MidoraId, TimelineSegmentPreview> SegmentPreviews { get; }
    public IReadOnlyList<ArrangementLaneDescriptor> ArrangementLanes { get; }
    public TimelineIntervalIndex Index { get; }
    public IReadOnlyDictionary<MidoraId, TimelineRenderItem> ItemsById { get; }
    public ulong ContentFingerprint { get; }
    public ulong ConductorPreviewFingerprint { get; }
    public bool HasConductorPreviewItems { get; }
    public bool HasHitTestableItems { get; }
    public bool HasDedicatedOverview => _overviewSource is not null;
    public long TotalItemCount => checked(Items.Count + (_itemSource?.Count ?? 0));
    public int MaterializedMaximumLane => _materializedMaximumLane;
    public long MaximumEndTick => Math.Max(
        Math.Max(
            _materializedMaximumEndTick,
            _itemSource?.MaximumEndTick ?? 0),
        _overviewSource?.MaximumEndTick ?? 0);

    internal void GetSegmentPlacementInfo(
        int lane,
        long tick,
        out bool occupied,
        out long nextStartTick)
    {
        occupied = false;
        nextStartTick = long.MaxValue;
        if (!_segmentIntervalsByLane.TryGetValue(
                lane,
                out SegmentPlacementInterval[]? intervals)
            || intervals.Length == 0)
        {
            return;
        }

        int low = 0;
        int high = intervals.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (intervals[middle].StartTick <= tick) low = middle + 1;
            else high = middle;
        }
        if (low < intervals.Length) nextStartTick = intervals[low].StartTick;
        if (low > 0)
        {
            SegmentPlacementInterval previous = intervals[low - 1];
            occupied = previous.StartTick <= tick && tick < previous.EndTick;
        }
    }

    public void AccumulateOverviewDensity(long extent, Span<int> destination)
    {
        if (extent <= 0) throw new ArgumentOutOfRangeException(nameof(extent));
        if (destination.IsEmpty) return;
        foreach (TimelineRenderItem item in Items)
        {
            int x = Math.Clamp((int)(item.StartTick / (double)extent * destination.Length), 0, destination.Length - 1);
            if (destination[x] < int.MaxValue) destination[x]++;
        }
        _itemSource?.AccumulateOverviewDensity(extent, destination);
    }

    public void AccumulateOverviewChannels(
        long extent,
        Span<byte> noteStartColumns,
        Span<byte> eventColumns)
    {
        MaterializedTimelineOverviewSource.ValidateOverviewColumns(
            extent,
            noteStartColumns,
            eventColumns);
        if (noteStartColumns.IsEmpty) return;
        if (_overviewSource is not null)
        {
            _overviewSource.Accumulate(extent, noteStartColumns, eventColumns);
            return;
        }

        foreach (TimelineRenderItem item in Items)
        {
            Span<byte> destination = item.Kind is TimelineItemKind.DirectMidiEvent
                or TimelineItemKind.OpaqueMidiEvent
                or TimelineItemKind.LogicalParameterPoint
                or TimelineItemKind.LogicalParameterCurve
                or TimelineItemKind.TemplateEvent
                    ? eventColumns
                    : noteStartColumns;
            MaterializedTimelineOverviewSource.MarkOverviewColumn(
                destination,
                item.StartTick,
                extent);
        }
    }

    public void QueryInto(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        List<TimelineRenderItem> destination)
    {
        Index.QueryInto(startTick, endTick, firstLane, lastLaneExclusive, destination);
        _itemSource?.QueryInto(startTick, endTick, firstLane, lastLaneExclusive, destination);
    }

    public bool TryQueryIntoCached(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        List<TimelineRenderItem> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        List<TimelineRenderItem> staged = RentQueryScratch();
        try
        {
            Index.QueryInto(startTick, endTick, firstLane, lastLaneExclusive, staged);
            if (_itemSource is IPreparedTimelineRenderItemSource prepared
                && !prepared.TryQueryIntoCached(
                    startTick,
                    endTick,
                    firstLane,
                    lastLaneExclusive,
                    staged))
            {
                return false;
            }
            if (_itemSource is not null and not IPreparedTimelineRenderItemSource)
                _itemSource.QueryInto(startTick, endTick, firstLane, lastLaneExclusive, staged);
            // Cached range queries have replace semantics: Pending leaves the
            // caller untouched, Success replaces the previous query atomically.
            destination.Clear();
            destination.AddRange(staged);
            return true;
        }
        finally
        {
            ReturnQueryScratch(staged);
        }
    }

    public void PrefetchRange(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        CancellationToken cancellationToken) =>
        (_itemSource as IPreparedTimelineRenderItemSource)?.PrefetchRange(
            startTick,
            endTick,
            firstLane,
            lastLaneExclusive,
            cancellationToken);

    internal void VisitInto(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        Action<TimelineRenderItem> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        Index.VisitInto(startTick, endTick, firstLane, lastLaneExclusive, visitor);
        _itemSource?.VisitInto(
            startTick,
            endTick,
            firstLane,
            lastLaneExclusive,
            visitor);
    }

    internal bool HasExternalItemSource => _itemSource is not null;
    internal bool CanComputeTileFingerprintSynchronously =>
        _itemSource is null or INonBlockingTimelineFingerprintSource;
    internal ulong ExternalItemSourceFingerprint => _itemSource?.ContentFingerprint ?? 0;

    internal bool TryAccumulateRasterColumns(
        TimelineRasterAggregateKind kind,
        TimelineRasterColumnProjection projection,
        int firstLane,
        int lastLaneExclusive,
        Span<TimelineRasterColumnSummary> destination,
        out int sourceWorkCount)
    {
        destination.Clear();
        sourceWorkCount = 0;
        if (destination.IsEmpty || lastLaneExclusive <= firstLane)
            return true;
        if (_itemSource is not null and not ITimelineRasterAggregateSource)
            return false;

        List<TimelineRenderItem> materialized = [];
        Index.QueryInto(
            projection.StartTick,
            projection.EndTick,
            firstLane,
            lastLaneExclusive,
            materialized);
        foreach (TimelineRenderItem item in materialized)
        {
            if (!MatchesAggregateKind(item.Kind, kind)) continue;
            int lane = kind == TimelineRasterAggregateKind.PianoNotes ? item.Lane : 0;
            ulong low = lane is >= 0 and < 64 ? 1UL << lane : 0;
            ulong high = lane is >= 64 and < 128 ? 1UL << (lane - 64) : 0;
            IncludeRasterColumns(
                destination,
                projection,
                item.StartTick,
                item.EndTick,
                low,
                high,
                item.Value,
                item.Value,
                1);
            sourceWorkCount++;
        }
        return _itemSource is not ITimelineRasterAggregateSource aggregate
            || aggregate.TryAccumulateRasterColumns(
                kind,
                projection,
                firstLane,
                lastLaneExclusive,
                destination,
                out int externalWork)
                && AddWork(ref sourceWorkCount, externalWork);
    }

    private static bool AddWork(ref int destination, int value)
    {
        destination = destination > int.MaxValue - value
            ? int.MaxValue
            : destination + value;
        return true;
    }

    private static bool MatchesAggregateKind(
        TimelineItemKind itemKind,
        TimelineRasterAggregateKind aggregateKind) => aggregateKind switch
        {
            TimelineRasterAggregateKind.PianoNotes => itemKind is
                TimelineItemKind.LogicalNote
                or TimelineItemKind.DirectMidiNote
                or TimelineItemKind.TemplateNote,
            TimelineRasterAggregateKind.Velocity => itemKind == TimelineItemKind.Velocity,
            TimelineRasterAggregateKind.EventPoints => itemKind is
                TimelineItemKind.LogicalParameterPoint
                or TimelineItemKind.DirectMidiEvent
                or TimelineItemKind.OpaqueMidiEvent,
            _ => false
        };

    private static void IncludeRasterColumns(
        Span<TimelineRasterColumnSummary> destination,
        TimelineRasterColumnProjection projection,
        long contentStartTick,
        long contentEndTick,
        ulong laneMaskLow,
        ulong laneMaskHigh,
        double minimumValue,
        double maximumValue,
        int approximateSourceCount)
    {
        if ((laneMaskLow | laneMaskHigh) == 0)
        {
            return;
        }
        if (!projection.TryGetColumns(
                contentStartTick,
                contentEndTick,
                out int first,
                out int lastExclusive)) return;
        for (int column = first; column < lastExclusive; column++)
        {
            destination[column].Include(
                laneMaskLow,
                laneMaskHigh,
                minimumValue,
                maximumValue,
                approximateSourceCount);
        }
    }

    internal ulong GetExternalRangeFingerprint(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive) =>
        _itemSource?.GetRangeFingerprint(
            startTick,
            endTick,
            firstLane,
            lastLaneExclusive) ?? 0;

    internal void QueryMaterializedInto(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        List<TimelineRenderItem> destination) =>
        Index.QueryInto(
            startTick,
            endTick,
            firstLane,
            lastLaneExclusive,
            destination);

    public void HitTestInto(
        long tick,
        long toleranceTicks,
        int lane,
        List<TimelineRenderItem> destination)
    {
        if (tick < 0 || toleranceTicks < 0 || lane < 0)
            throw new ArgumentOutOfRangeException(nameof(tick));
        long start = Math.Max(0, tick - Math.Min(tick, toleranceTicks));
        long end = tick > long.MaxValue - toleranceTicks - 1
            ? long.MaxValue
            : tick + toleranceTicks + 1;
        QueryInto(start, end, lane, checked(lane + 1), destination);
        destination.RemoveAll(static item =>
            item.State.HasFlag(TimelineItemState.HitTestDisabled));
        destination.Sort(static (x, y) =>
        {
            int value = y.ZIndex.CompareTo(x.ZIndex);
            if (value != 0) return value;
            value = x.Length.CompareTo(y.Length);
            return value != 0 ? value : x.Id.CompareTo(y.Id);
        });
    }

    public bool TryHitTestCached(
        long tick,
        long toleranceTicks,
        int lane,
        List<TimelineRenderItem> destination)
    {
        if (tick < 0 || toleranceTicks < 0 || lane < 0)
            throw new ArgumentOutOfRangeException(nameof(tick));
        long start = Math.Max(0, tick - Math.Min(tick, toleranceTicks));
        long end = tick > long.MaxValue - toleranceTicks - 1
            ? long.MaxValue
            : tick + toleranceTicks + 1;
        if (!TryQueryIntoCached(start, end, lane, checked(lane + 1), destination))
            return false;
        destination.RemoveAll(static item =>
            item.State.HasFlag(TimelineItemState.HitTestDisabled));
        destination.Sort(static (x, y) =>
        {
            int value = y.ZIndex.CompareTo(x.ZIndex);
            if (value != 0) return value;
            value = x.Length.CompareTo(y.Length);
            return value != 0 ? value : x.Id.CompareTo(y.Id);
        });
        return true;
    }

    public bool TryGetItem(MidoraId id, out TimelineRenderItem item)
    {
        if (ItemsById.TryGetValue(id, out item)) return true;
        return _itemSource?.TryGetById(id, out item) == true;
    }

    public bool TryGetItemCached(
        MidoraId id,
        out TimelineRenderItem item,
        out bool ready)
    {
        if (ItemsById.TryGetValue(id, out item))
        {
            ready = true;
            return true;
        }
        if (_itemSource is not IPreparedTimelineRenderItemSource prepared)
        {
            ready = true;
            return _itemSource?.TryGetById(id, out item) == true;
        }
        List<TimelineRenderItem> result = [];
        ready = prepared.TryQueryByIdsCached(new HashSet<MidoraId> { id }, result);
        if (ready && result.Count != 0)
        {
            item = result[0];
            return true;
        }
        item = default;
        return false;
    }

    public void QueryByIds(
        IReadOnlySet<MidoraId> ids,
        List<TimelineRenderItem> destination)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(destination);
        HashSet<MidoraId>? remaining = null;
        foreach (MidoraId id in ids)
        {
            if (ItemsById.TryGetValue(id, out TimelineRenderItem item))
            {
                destination.Add(item);
                continue;
            }
            (remaining ??= []).Add(id);
        }
        if (remaining is { Count: > 0 })
            _itemSource?.QueryByIds(remaining, destination);
    }

    public bool TryQueryByIdsCached(
        IReadOnlySet<MidoraId> ids,
        List<TimelineRenderItem> destination)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(destination);
        List<TimelineRenderItem> staged = RentQueryScratch();
        HashSet<MidoraId>? remaining = null;
        try
        {
            foreach (MidoraId id in ids)
            {
                if (ItemsById.TryGetValue(id, out TimelineRenderItem item))
                    staged.Add(item);
                else
                    (remaining ??= []).Add(id);
            }
            if (remaining is not { Count: > 0 })
            {
                destination.AddRange(staged);
                return true;
            }
            if (_itemSource is IPreparedTimelineRenderItemSource prepared)
            {
                if (!prepared.TryQueryByIdsCached(remaining, staged)) return false;
                destination.AddRange(staged);
                return true;
            }
            _itemSource?.QueryByIds(remaining, staged);
            destination.AddRange(staged);
            return true;
        }
        finally
        {
            ReturnQueryScratch(staged);
        }
    }

    private static List<TimelineRenderItem> RentQueryScratch() =>
        QueryScratchPool.TryTake(out List<TimelineRenderItem>? value)
            ? value
            : new(capacity: 16);

    private static void ReturnQueryScratch(List<TimelineRenderItem> value)
    {
        if (value.Capacity > MaximumQueryScratchCapacity) return;
        value.Clear();
        QueryScratchPool.Add(value);
    }

    public void PrefetchIds(
        IReadOnlySet<MidoraId> ids,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (_itemSource is not IPreparedTimelineRenderItemSource prepared) return;
        HashSet<MidoraId> remaining = [.. ids];
        remaining.ExceptWith(ItemsById.Keys);
        if (remaining.Count != 0) prepared.PrefetchIds(remaining, cancellationToken);
    }

    public IEnumerable<TimelineRenderItem> EnumerateAllItems() =>
        _itemSource is null ? Items : Items.Concat(_itemSource.EnumerateAll());

    public bool Matches(long semanticRevision, string projectionKey) =>
        SemanticRevision == semanticRevision
        && string.Equals(ProjectionKey, projectionKey, StringComparison.Ordinal);

    internal ulong GetPianoTileContentFingerprint(
        int horizontalLod,
        int verticalLod,
        long tileX,
        long tileY)
        => GetPianoTileContentFingerprint(
            TimelineRasterLod.GetScale(horizontalLod),
            TimelineRasterLod.GetScale(verticalLod),
            tileX,
            tileY);

    internal ulong GetPianoTileContentFingerprint(
        double devicePixelsPerTick,
        double devicePixelsPerLane,
        long tileX,
        long tileY)
    {
        PianoTileFingerprintKey key = new(
            BitConverter.DoubleToInt64Bits(devicePixelsPerTick),
            BitConverter.DoubleToInt64Bits(devicePixelsPerLane),
            tileX,
            tileY);
        Lazy<ulong> created = new(
                () => TimelinePianoTileRasterizer.ComputeContentFingerprint(
                    this,
                    devicePixelsPerTick,
                    devicePixelsPerLane,
                    tileX,
                    tileY),
                LazyThreadSafetyMode.ExecutionAndPublication);
        Lazy<ulong> pending = _pianoTileFingerprints.GetOrAdd(key, created);
        if (ReferenceEquals(created, pending))
        {
            _pianoTileFingerprintOrder.Enqueue(key);
            EvictOldPianoFingerprint();
        }
        try
        {
            return pending.Value;
        }
        catch
        {
            _pianoTileFingerprints.TryRemove(
                new KeyValuePair<PianoTileFingerprintKey, Lazy<ulong>>(key, pending));
            throw;
        }
    }

    private readonly record struct PianoTileFingerprintKey(
        long HorizontalScaleKey,
        long VerticalScaleKey,
        long TileX,
        long TileY);

    internal ulong GetConductorTileContentFingerprint(
        double devicePixelsPerTick,
        long tileX,
        double dpiScaleX)
    {
        ConductorTileFingerprintKey key = new(
            BitConverter.DoubleToInt64Bits(devicePixelsPerTick),
            tileX,
            BitConverter.DoubleToInt64Bits(dpiScaleX));
        Lazy<ulong> created = new(
                () => TimelineConductorTileRasterizer.ComputeContentFingerprint(
                    this,
                    devicePixelsPerTick,
                    tileX,
                    dpiScaleX),
                LazyThreadSafetyMode.ExecutionAndPublication);
        Lazy<ulong> pending = _conductorTileFingerprints.GetOrAdd(key, created);
        if (ReferenceEquals(created, pending))
        {
            _conductorTileFingerprintOrder.Enqueue(key);
            EvictOldConductorFingerprint();
        }
        try
        {
            return pending.Value;
        }
        catch
        {
            _conductorTileFingerprints.TryRemove(
                new KeyValuePair<ConductorTileFingerprintKey, Lazy<ulong>>(key, pending));
            throw;
        }
    }

    private readonly record struct ConductorTileFingerprintKey(
        long HorizontalScaleKey,
        long TileX,
        long DpiScaleXKey);

    private void EvictOldPianoFingerprint()
    {
        if (_pianoTileFingerprints.Count <= MaximumPianoFingerprintEntries) return;
        while (_pianoTileFingerprintOrder.TryDequeue(out PianoTileFingerprintKey oldest))
        {
            _pianoTileFingerprints.TryRemove(oldest, out _);
            break;
        }
    }

    private void EvictOldConductorFingerprint()
    {
        if (_conductorTileFingerprints.Count <= MaximumConductorFingerprintEntries) return;
        while (_conductorTileFingerprintOrder.TryDequeue(out ConductorTileFingerprintKey oldest))
        {
            _conductorTileFingerprints.TryRemove(oldest, out _);
            break;
        }
    }

    private readonly record struct SegmentPlacementInterval(
        long StartTick,
        long EndTick);
}

public static class TimelineContentFingerprint
{
    private const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static ulong ForSegmentPreview(
        IEnumerable<TimelineSegmentPreviewNote> notes,
        IEnumerable<TimelineSegmentPreviewEvent>? events = null)
    {
        return Combine(
            ForSegmentPreviewNotes(notes),
            ForSegmentPreviewEvents(events ?? []));
    }

    public static ulong ForSegmentPreviewNotes(IEnumerable<TimelineSegmentPreviewNote> notes)
    {
        ulong hash = Offset;
        foreach (TimelineSegmentPreviewNote note in notes)
        {
            Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(note.NormalizedStart)));
            Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(note.NormalizedEnd)));
            Add(ref hash, unchecked((ulong)note.Pitch));
        }
        return hash;
    }

    public static ulong ForSegmentPreviewEvents(IEnumerable<TimelineSegmentPreviewEvent> events)
    {
        ulong hash = Offset;
        foreach (TimelineSegmentPreviewEvent value in events)
        {
            Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(value.NormalizedTick)));
            Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(value.NormalizedValue)));
        }
        return hash;
    }

    public static ulong Combine(ulong first, ulong second)
    {
        ulong hash = Offset;
        Add(ref hash, first);
        Add(ref hash, second);
        return hash;
    }

    public static ulong ForRenderItems(IEnumerable<TimelineRenderItem> items)
    {
        ulong hash = Offset;
        foreach (TimelineRenderItem item in items)
        {
            Add(ref hash, unchecked((ulong)item.Id.Value));
            Add(ref hash, unchecked((ulong)item.Kind));
            Add(ref hash, unchecked((ulong)item.StartTick));
            Add(ref hash, unchecked((ulong)item.EndTick));
            Add(ref hash, unchecked((ulong)item.Lane));
            Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(item.Value)));
            Add(ref hash, unchecked((ulong)item.ZIndex));
            TimelineItemState contentState = item.State
                & ~(TimelineItemState.Selected | TimelineItemState.Primary);
            Add(ref hash, unchecked((ulong)contentState));
            Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(item.SecondaryValue)));
            Add(ref hash, unchecked((ulong)item.Interpolation));
            Add(ref hash, item.AccentColor);
        }
        return hash;
    }

    public static ulong ForConductorPreview(IEnumerable<TimelineRenderItem> items)
    {
        ulong hash = Offset;
        foreach (TimelineRenderItem item in items)
        {
            if (item.Kind is not (TimelineItemKind.ConductorEvent
                or TimelineItemKind.Marker
                or TimelineItemKind.ProjectEndMarker))
            {
                continue;
            }
            Add(ref hash, unchecked((ulong)item.Kind));
            Add(ref hash, unchecked((ulong)item.StartTick));
            Add(ref hash, unchecked((ulong)item.ZIndex));
            Add(ref hash, item.AccentColor);
        }
        return hash;
    }

    public static ulong ForOverviewTicks(
        IEnumerable<long> noteStartTicks,
        IEnumerable<long> eventTicks)
    {
        ulong hash = Offset;
        Add(ref hash, 0x4e4f544553UL);
        foreach (long tick in noteStartTicks)
            Add(ref hash, unchecked((ulong)tick));
        Add(ref hash, 0x4556454e5453UL);
        foreach (long tick in eventTicks)
            Add(ref hash, unchecked((ulong)tick));
        return hash;
    }

    public static ulong ForPianoTileItems(IEnumerable<TimelineRenderItem> items)
    {
        ulong hash = Offset;
        foreach (TimelineRenderItem item in items)
        {
            if (item.Kind is not (TimelineItemKind.LogicalNote
                or TimelineItemKind.DirectMidiNote
                or TimelineItemKind.TemplateNote))
            {
                continue;
            }
            Add(ref hash, unchecked((ulong)item.Kind));
            Add(ref hash, unchecked((ulong)item.StartTick));
            Add(ref hash, unchecked((ulong)item.EndTick));
            Add(ref hash, unchecked((ulong)item.Lane));
            TimelineItemState contentState = item.State
                & ~(TimelineItemState.Selected | TimelineItemState.Primary);
            Add(ref hash, unchecked((ulong)contentState));
        }
        return hash;
    }

    public static ulong ForVelocityTileItems(
        IEnumerable<TimelineRenderItem> items,
        TimelineSelectionSnapshot? selection)
    {
        ulong hash = Offset;
        foreach (TimelineRenderItem item in items)
        {
            if (item.Kind != TimelineItemKind.Velocity) continue;
            Add(ref hash, unchecked((ulong)item.Id.Value));
            Add(ref hash, unchecked((ulong)item.StartTick));
            Add(ref hash, unchecked((ulong)item.EndTick));
            Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(item.Value)));
            Add(ref hash, unchecked((ulong)item.ZIndex));
            bool selected = selection?.Contains(item.Id)
                ?? item.State.HasFlag(TimelineItemState.Selected);
            Add(ref hash, selected ? 1UL : 0UL);
        }
        return hash;
    }

    public static ulong ForLocalSelection(
        IEnumerable<TimelineRenderItem> items,
        TimelineSelectionSnapshot selection,
        bool includePrimary = false)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(selection);
        ulong hash = Offset;
        foreach (TimelineRenderItem item in items)
        {
            if (!selection.Contains(item.Id)) continue;
            Add(ref hash, unchecked((ulong)item.Id.Value));
            Add(ref hash, unchecked((ulong)item.Kind));
            Add(ref hash, unchecked((ulong)item.StartTick));
            Add(ref hash, unchecked((ulong)item.EndTick));
            Add(ref hash, unchecked((ulong)item.Lane));
            Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(item.Value)));
            if (includePrimary) Add(ref hash, selection.Primary == item.Id ? 1UL : 0UL);
        }
        return hash;
    }

    public static ulong WithSelection(ulong contentFingerprint, long selectionRevision)
    {
        ulong hash = contentFingerprint;
        Add(ref hash, unchecked((ulong)selectionRevision));
        return hash;
    }

    private static void Add(ref ulong hash, ulong value)
    {
        for (int index = 0; index < sizeof(ulong); index++)
        {
            hash ^= (byte)value;
            hash *= Prime;
            value >>= 8;
        }
    }
}
