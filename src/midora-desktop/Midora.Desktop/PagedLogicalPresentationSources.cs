using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Desktop;

internal static class TimelinePresentationPaging
{
    public const long MaterializedItemThreshold = 4096;

    public static (TimelineRenderItem[] Items, ITimelineRenderItemSource? Source) Adapt(
        ITimelineRenderItemSource? source)
    {
        if (source is null) return ([], null);
        return source.Count <= MaterializedItemThreshold
            ? (source.EnumerateAll().ToArray(), null)
            : ([], source);
    }
}

internal enum LogicalNoteTimelineProjection
{
    Notes,
    Velocities
}

internal sealed class PagedLogicalNoteTimelineItemSource : ITimelineRenderItemSource
{
    private readonly Segment _segment;
    private readonly LogicalNoteCollection _notes;
    private readonly LogicalNoteQuerySnapshot _snapshot;
    private readonly LogicalNoteTimelineProjection _projection;
    private readonly IReadOnlySet<MidoraId> _selectedIds;
    private readonly MidoraId? _primaryId;

    public PagedLogicalNoteTimelineItemSource(
        Segment segment,
        LogicalNoteTimelineProjection projection,
        IReadOnlySet<MidoraId>? selectedIds = null,
        MidoraId? primaryId = null,
        LogicalNoteQuerySnapshot? snapshot = null)
    {
        _segment = segment ?? throw new ArgumentNullException(nameof(segment));
        _notes = segment.Notes;
        _projection = projection;
        _selectedIds = selectedIds ?? EmptySelection;
        _primaryId = primaryId;
        _snapshot = snapshot ?? segment.Notes.CreateQuerySnapshot();
    }

    private static IReadOnlySet<MidoraId> EmptySelection { get; } = new HashSet<MidoraId>();

    public long Count => _snapshot.Count;
    public long MaximumEndTick => _snapshot.MaximumEndTick;
    public ulong ContentFingerprint => PureMidiPresentationFingerprint.Create(
        null,
        unchecked((long)_snapshot.ContentFingerprint),
        _segment.ContentOffsetTick,
        _segment.LengthTicks,
        (long)_projection);

    public ulong GetRangeFingerprint(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive)
    {
        if (_projection == LogicalNoteTimelineProjection.Notes)
        {
            int minimumNote = Math.Clamp(128 - lastLaneExclusive, 0, 127);
            int maximumNote = Math.Clamp(127 - firstLane, 0, 127);
            return maximumNote < minimumNote
                ? 0
                : _snapshot.GetRangeFingerprint(
                    startTick,
                    endTick,
                    minimumNote,
                    maximumNote);
        }
        return _snapshot.GetRangeFingerprint(startTick, endTick);
    }

    public void QueryInto(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        List<TimelineRenderItem> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        VisitInto(startTick, endTick, firstLane, lastLaneExclusive, destination.Add);
    }

    public void VisitInto(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        Action<TimelineRenderItem> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        if (endTick <= startTick || lastLaneExclusive <= firstLane) return;
        if (_projection == LogicalNoteTimelineProjection.Notes)
        {
            int minimumNote = Math.Clamp(128 - lastLaneExclusive, 0, 127);
            int maximumNote = Math.Clamp(127 - firstLane, 0, 127);
            if (maximumNote < minimumNote) return;
            foreach (LogicalNoteSnapshotValue value in _snapshot.QueryValues(
                startTick,
                endTick,
                minimumNote,
                maximumNote))
            {
                visitor(ToNoteItem(value));
            }
            return;
        }

        if (firstLane > 0 || lastLaneExclusive <= 0) return;
        foreach (LogicalNoteSnapshotValue value in _snapshot.QueryValues(startTick, endTick))
            visitor(ToVelocityItem(value));
    }

    public bool TryGetById(MidoraId id, out TimelineRenderItem item)
    {
        if (_notes.TryGetById(id, out LogicalNote? value) && value is not null)
        {
            item = _projection == LogicalNoteTimelineProjection.Notes
                ? ToNoteItem(value)
                : ToVelocityItem(value);
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
        foreach (MidoraId id in ids)
        {
            if (TryGetById(id, out TimelineRenderItem value)) destination.Add(value);
        }
    }

    public IEnumerable<TimelineRenderItem> EnumerateAll() =>
        _snapshot.EnumerateAll().Select(value =>
            _projection == LogicalNoteTimelineProjection.Notes
                ? ToNoteItem(value)
                : ToVelocityItem(value));

    public void AccumulateOverviewDensity(long extent, Span<int> destination)
    {
        if (_projection != LogicalNoteTimelineProjection.Notes
            || extent <= 0
            || destination.IsEmpty)
        {
            return;
        }
        Span<byte> occupied = destination.Length <= 4096
            ? stackalloc byte[destination.Length]
            : new byte[destination.Length];
        _snapshot.AccumulateStartColumns(extent, occupied);
        for (int index = 0; index < occupied.Length; index++)
        {
            if (occupied[index] != 0 && destination[index] < int.MaxValue)
                destination[index]++;
        }
    }

    private TimelineRenderItem ToNoteItem(LogicalNote value) => new(
        value.Id,
        TimelineItemKind.LogicalNote,
        value.StartTick,
        checked(value.StartTick + value.LengthTicks),
        127 - Math.Clamp(value.Note, 0, 127),
        value.Velocity,
        1,
        State(value.Id, value.StartTick, value.Note is < 0 or > 127));

    private TimelineRenderItem ToNoteItem(LogicalNoteSnapshotValue value) => new(
        value.Id,
        TimelineItemKind.LogicalNote,
        value.StartTick,
        checked(value.StartTick + value.LengthTicks),
        127 - Math.Clamp(value.Note, 0, 127),
        value.Velocity,
        1,
        State(value.Id, value.StartTick, value.Note is < 0 or > 127));

    private TimelineRenderItem ToVelocityItem(LogicalNote value) => new(
        value.Id,
        TimelineItemKind.Velocity,
        value.StartTick,
        checked(value.StartTick + 1),
        0,
        value.Velocity / 127d,
        value.Note,
        State(value.Id, value.StartTick, invalid: false));

    private TimelineRenderItem ToVelocityItem(LogicalNoteSnapshotValue value) => new(
        value.Id,
        TimelineItemKind.Velocity,
        value.StartTick,
        checked(value.StartTick + 1),
        0,
        value.Velocity / 127d,
        value.Note,
        State(value.Id, value.StartTick, invalid: false));

    private TimelineItemState State(MidoraId id, long tick, bool invalid)
    {
        TimelineItemState state = tick < _segment.ContentOffsetTick || tick >= _segment.ContentEndTick
            ? TimelineItemState.OutsideActiveRange
            : TimelineItemState.None;
        if (invalid) state |= TimelineItemState.Invalid;
        if (_selectedIds.Contains(id)) state |= TimelineItemState.Selected;
        if (_primaryId == id) state |= TimelineItemState.Primary;
        return state;
    }
}

internal readonly record struct LogicalSegmentPreviewEventValue(
    long Tick,
    double NormalizedValue);

internal sealed class LogicalSegmentPreviewSource : ITimelineSegmentPreviewSource
{
    private readonly LogicalNoteQuerySnapshot _notes;
    private readonly LogicalSegmentPreviewEventValue[] _events;
    private readonly long _visibleStart;
    private readonly long _visibleEnd;
    private readonly long _length;

    public LogicalSegmentPreviewSource(
        Segment segment,
        LogicalNoteQuerySnapshot notes,
        IEnumerable<LogicalSegmentPreviewEventValue> events)
    {
        ArgumentNullException.ThrowIfNull(segment);
        _notes = notes ?? throw new ArgumentNullException(nameof(notes));
        ArgumentNullException.ThrowIfNull(events);
        _events = events
            .OrderBy(static value => value.Tick)
            .ThenBy(static value => value.NormalizedValue)
            .ToArray();
        _visibleStart = segment.ContentOffsetTick;
        _visibleEnd = segment.ContentEndTick;
        _length = segment.LengthTicks;
        NoteContentFingerprint = PureMidiPresentationFingerprint.Create(
            null,
            unchecked((long)notes.ContentFingerprint),
            _visibleStart,
            _length,
            0x4c4f474943414c4e);
        EventContentFingerprint = TimelineContentFingerprint.ForSegmentPreviewEvents(
            _events.Select(ToPreviewEvent));
    }

    public bool HasNoteContent => _notes.Count != 0;
    public bool HasEventContent => _events.Length != 0;
    public ulong NoteContentFingerprint { get; }
    public ulong EventContentFingerprint { get; }

    public void QueryNotes(
        double normalizedStart,
        double normalizedEnd,
        List<TimelineSegmentPreviewNote> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        VisitNotes(normalizedStart, normalizedEnd, destination.Add);
    }

    public void QueryEvents(
        double normalizedStart,
        double normalizedEnd,
        List<TimelineSegmentPreviewEvent> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        VisitEvents(normalizedStart, normalizedEnd, destination.Add);
    }

    public void VisitNotes(
        double normalizedStart,
        double normalizedEnd,
        Action<TimelineSegmentPreviewNote> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        (long startTick, long endTick) = ToContentRange(normalizedStart, normalizedEnd);
        foreach (LogicalNoteSnapshotValue note in _notes.QueryValues(
            startTick,
            endTick,
            int.MinValue,
            int.MaxValue))
        {
            long noteEnd = note.StartTick > long.MaxValue - Math.Max(1, note.LengthTicks)
                ? long.MaxValue
                : note.StartTick + Math.Max(1, note.LengthTicks);
            long clippedStart = Math.Max(_visibleStart, note.StartTick);
            long clippedEnd = Math.Min(_visibleEnd, noteEnd);
            if (clippedEnd <= clippedStart) continue;
            visitor(new(
                (clippedStart - _visibleStart) / (double)_length,
                (clippedEnd - _visibleStart) / (double)_length,
                Math.Clamp(note.Note, 0, 127)));
        }
    }

    public void VisitEvents(
        double normalizedStart,
        double normalizedEnd,
        Action<TimelineSegmentPreviewEvent> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        (long startTick, long endTick) = ToContentRange(normalizedStart, normalizedEnd);
        foreach (LogicalSegmentPreviewEventValue value in _events)
        {
            if (value.Tick < startTick) continue;
            if (value.Tick >= endTick) break;
            visitor(ToPreviewEvent(value));
        }
    }

    public ulong GetTileContentFingerprint(
        bool eventLayer,
        double deviceSegmentWidth,
        long tileX)
    {
        double normalizedStart = Math.Max(
            0,
            (tileX * TimelineSegmentPreviewRasterizer.TileSize - 1d) / deviceSegmentWidth);
        double normalizedEnd = Math.Min(
            Math.BitIncrement(1d),
            ((tileX + 1d) * TimelineSegmentPreviewRasterizer.TileSize + 1d)
            / deviceSegmentWidth);
        (long startTick, long endTick) = ToContentRange(normalizedStart, normalizedEnd);
        if (eventLayer)
        {
            List<TimelineSegmentPreviewEvent> values = [];
            QueryEvents(normalizedStart, normalizedEnd, values);
            return TimelineContentFingerprint.ForSegmentPreviewEvents(values);
        }
        return _notes.GetRangeFingerprint(startTick, Math.Max(startTick + 1, endTick));
    }

    private (long StartTick, long EndTick) ToContentRange(
        double normalizedStart,
        double normalizedEnd)
    {
        long start = checked(_visibleStart
            + (long)Math.Floor(Math.Clamp(normalizedStart, 0, 1) * _length));
        long end = checked(_visibleStart
            + (long)Math.Ceiling(Math.Clamp(normalizedEnd, 0, 1) * _length));
        return (
            Math.Clamp(start, _visibleStart, _visibleEnd),
            Math.Clamp(end, _visibleStart, _visibleEnd));
    }

    private TimelineSegmentPreviewEvent ToPreviewEvent(LogicalSegmentPreviewEventValue value) =>
        new(
            (value.Tick - _visibleStart) / (double)_length,
            Math.Clamp(value.NormalizedValue, 0, 1));
}

internal sealed class LogicalSegmentOverviewSource : ITimelineOverviewSource
{
    private readonly LogicalNoteQuerySnapshot _notes;
    private readonly long[] _eventTicks;

    public LogicalSegmentOverviewSource(
        LogicalNoteQuerySnapshot notes,
        IEnumerable<long> eventTicks)
    {
        _notes = notes ?? throw new ArgumentNullException(nameof(notes));
        ArgumentNullException.ThrowIfNull(eventTicks);
        _eventTicks = eventTicks.Order().ToArray();
        MaximumEndTick = Math.Max(
            notes.MaximumEndTick,
            _eventTicks.Length == 0
                ? 0
                : _eventTicks[^1] == long.MaxValue ? long.MaxValue : _eventTicks[^1] + 1);
        ContentFingerprint = TimelineContentFingerprint.Combine(
            notes.ContentFingerprint,
            TimelineContentFingerprint.ForOverviewTicks([], _eventTicks));
    }

    public ulong ContentFingerprint { get; }
    public long MaximumEndTick { get; }

    public void Accumulate(
        long extent,
        Span<byte> noteStartColumns,
        Span<byte> eventColumns)
    {
        MaterializedTimelineOverviewSource.ValidateOverviewColumns(
            extent,
            noteStartColumns,
            eventColumns);
        _notes.AccumulateStartColumns(extent, noteStartColumns);
        foreach (long tick in _eventTicks)
            MaterializedTimelineOverviewSource.MarkOverviewColumn(eventColumns, tick, extent);
    }
}

internal enum TemplateNoteTimelineProjection
{
    Notes,
    Velocities
}

internal sealed class PagedTemplateNoteTimelineItemSource : ITimelineRenderItemSource
{
    private readonly SubVoice _voice;
    private readonly TemplateEventQuerySnapshot _snapshot;
    private readonly TemplateNoteTimelineProjection _projection;
    private readonly IReadOnlySet<MidoraId> _selectedIds;
    private readonly MidoraId? _primaryId;

    public PagedTemplateNoteTimelineItemSource(
        SubVoice voice,
        TemplateNoteTimelineProjection projection,
        IReadOnlySet<MidoraId>? selectedIds = null,
        MidoraId? primaryId = null,
        TemplateEventQuerySnapshot? snapshot = null)
    {
        _voice = voice ?? throw new ArgumentNullException(nameof(voice));
        _projection = projection;
        _selectedIds = selectedIds ?? EmptySelection;
        _primaryId = primaryId;
        _snapshot = snapshot ?? voice.Events.CreateQuerySnapshot();
    }

    private static IReadOnlySet<MidoraId> EmptySelection { get; } = new HashSet<MidoraId>();

    // The immutable snapshot deliberately stores Note and non-Note template
    // events together. Count is an upper bound used only for capacity hints.
    public long Count => _snapshot.Count;
    public long MaximumEndTick => _snapshot.MaximumEndTick;
    public ulong ContentFingerprint => PureMidiPresentationFingerprint.Create(
        null,
        unchecked((long)_snapshot.ContentFingerprint),
        (long)_projection,
        0x535542564f494345);

    public ulong GetRangeFingerprint(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive)
    {
        if (_projection == TemplateNoteTimelineProjection.Notes)
        {
            int minimumNote = Math.Clamp(128 - lastLaneExclusive, 0, 127);
            int maximumNote = Math.Clamp(127 - firstLane, 0, 127);
            return maximumNote < minimumNote
                ? 0
                : _snapshot.GetNoteRangeFingerprint(
                    startTick,
                    endTick,
                    minimumNote,
                    maximumNote);
        }
        return _snapshot.GetNoteRangeFingerprint(startTick, endTick);
    }

    public void QueryInto(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        List<TimelineRenderItem> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        VisitInto(startTick, endTick, firstLane, lastLaneExclusive, destination.Add);
    }

    public void VisitInto(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        Action<TimelineRenderItem> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        if (endTick <= startTick || lastLaneExclusive <= firstLane) return;
        if (_projection == TemplateNoteTimelineProjection.Notes)
        {
            int minimumNote = Math.Clamp(128 - lastLaneExclusive, 0, 127);
            int maximumNote = Math.Clamp(127 - firstLane, 0, 127);
            if (maximumNote < minimumNote) return;
            foreach (TemplateEventSnapshotValue value in _snapshot.QueryNotes(
                startTick,
                endTick,
                minimumNote,
                maximumNote))
            {
                visitor(ToNoteItem(value));
            }
            return;
        }

        if (firstLane > 0 || lastLaneExclusive <= 0) return;
        foreach (TemplateEventSnapshotValue value in _snapshot.QueryNotes(startTick, endTick))
            visitor(ToVelocityItem(value));
    }

    public bool TryGetById(MidoraId id, out TimelineRenderItem item)
    {
        if (_voice.Events.TryGetById(id, out TemplateEvent? value)
            && value is { Kind: TemplateEventKind.Note })
        {
            item = _projection == TemplateNoteTimelineProjection.Notes
                ? ToNoteItem(value)
                : ToVelocityItem(value);
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
        foreach (MidoraId id in ids)
        {
            if (TryGetById(id, out TimelineRenderItem item)) destination.Add(item);
        }
    }

    public IEnumerable<TimelineRenderItem> EnumerateAll() =>
        _snapshot.EnumerateAll()
            .Where(static value => value.Kind == TemplateEventKind.Note)
            .Select(value => _projection == TemplateNoteTimelineProjection.Notes
                ? ToNoteItem(value)
                : ToVelocityItem(value));

    public void AccumulateOverviewDensity(long extent, Span<int> destination)
    {
        if (_projection != TemplateNoteTimelineProjection.Notes
            || extent <= 0
            || destination.IsEmpty)
        {
            return;
        }
        Span<byte> noteColumns = destination.Length <= 4096
            ? stackalloc byte[destination.Length]
            : new byte[destination.Length];
        Span<byte> eventColumns = destination.Length <= 4096
            ? stackalloc byte[destination.Length]
            : new byte[destination.Length];
        _snapshot.AccumulateOverviewColumns(extent, noteColumns, eventColumns);
        for (int index = 0; index < noteColumns.Length; index++)
        {
            if (noteColumns[index] != 0 && destination[index] < int.MaxValue)
                destination[index]++;
        }
    }

    private TimelineRenderItem ToNoteItem(TemplateEvent value) => new(
        value.Id,
        TimelineItemKind.TemplateNote,
        value.Tick,
        checked(value.Tick + value.LengthTicks),
        127 - Math.Clamp(value.Number, 0, 127),
        value.Value / 127d,
        1,
        State(value.Id, value.Number is < 0 or > 127));

    private TimelineRenderItem ToNoteItem(TemplateEventSnapshotValue value) => new(
        value.Id,
        TimelineItemKind.TemplateNote,
        value.Tick,
        checked(value.Tick + value.LengthTicks),
        127 - Math.Clamp(value.Number, 0, 127),
        value.Value / 127d,
        1,
        State(value.Id, value.Number is < 0 or > 127));

    private TimelineRenderItem ToVelocityItem(TemplateEvent value) => new(
        value.Id,
        TimelineItemKind.Velocity,
        value.Tick,
        checked(value.Tick + 1),
        0,
        value.Value / 127d,
        value.Number,
        State(value.Id, invalid: false));

    private TimelineRenderItem ToVelocityItem(TemplateEventSnapshotValue value) => new(
        value.Id,
        TimelineItemKind.Velocity,
        value.Tick,
        checked(value.Tick + 1),
        0,
        value.Value / 127d,
        value.Number,
        State(value.Id, invalid: false));

    private TimelineItemState State(MidoraId id, bool invalid)
    {
        TimelineItemState state = invalid
            ? TimelineItemState.Invalid
            : TimelineItemState.None;
        if (_selectedIds.Contains(id)) state |= TimelineItemState.Selected;
        if (_primaryId == id) state |= TimelineItemState.Primary;
        return state;
    }
}

internal sealed class TemplateEventOverviewSource : ITimelineOverviewSource
{
    private readonly TemplateEventQuerySnapshot _snapshot;

    public TemplateEventOverviewSource(TemplateEventQuerySnapshot snapshot) =>
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));

    public ulong ContentFingerprint => _snapshot.ContentFingerprint;
    public long MaximumEndTick => _snapshot.MaximumEndTick;

    public void Accumulate(
        long extent,
        Span<byte> noteStartColumns,
        Span<byte> eventColumns)
    {
        MaterializedTimelineOverviewSource.ValidateOverviewColumns(
            extent,
            noteStartColumns,
            eventColumns);
        _snapshot.AccumulateOverviewColumns(extent, noteStartColumns, eventColumns);
    }
}
