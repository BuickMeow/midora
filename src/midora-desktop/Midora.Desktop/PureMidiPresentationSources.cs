using System.Text;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Desktop;

internal sealed class PagedMidiSegmentPreviewSource(MidiSegment segment) : ITimelineSegmentPreviewSource
{
    private readonly MidiSegment _segment = segment ?? throw new ArgumentNullException(nameof(segment));

    public bool HasNoteContent => _segment.Notes.Count != 0;

    public bool HasEventContent => _segment.ChannelEvents.Count != 0
        || _segment.OpaqueEvents.Count != 0;

    public ulong NoteContentFingerprint => PureMidiPresentationFingerprint.Create(
        _segment.PagedContentFingerprint,
        _segment.Notes.Generation,
        _segment.ContentOffsetTick,
        _segment.LengthTicks,
        1);

    public ulong EventContentFingerprint => PureMidiPresentationFingerprint.Create(
        _segment.PagedContentFingerprint,
        _segment.ChannelEvents.Generation,
        _segment.OpaqueEvents.Generation,
        _segment.ContentOffsetTick,
        _segment.LengthTicks,
        2);

    public void QueryNotes(
        double normalizedStart,
        double normalizedEnd,
        List<TimelineSegmentPreviewNote> destination)
    {
        (long startTick, long endTick) = ToContentRange(normalizedStart, normalizedEnd);
        if (endTick <= startTick) return;
        long visibleStart = _segment.ContentOffsetTick;
        long visibleEnd = _segment.ContentEndTick;
        foreach (DirectMidiNoteValue note in _segment.Notes.QueryValues(startTick, endTick))
        {
            long noteEnd = note.StartTick > long.MaxValue - Math.Max(1, note.LengthTicks)
                ? long.MaxValue
                : note.StartTick + Math.Max(1, note.LengthTicks);
            long clippedStart = Math.Max(visibleStart, note.StartTick);
            long clippedEnd = Math.Min(visibleEnd, noteEnd);
            if (clippedEnd <= clippedStart) continue;
            destination.Add(new(
                (clippedStart - visibleStart) / (double)_segment.LengthTicks,
                (clippedEnd - visibleStart) / (double)_segment.LengthTicks,
                Math.Clamp(note.Key, 0, 127)));
        }
    }

    public void QueryEvents(
        double normalizedStart,
        double normalizedEnd,
        List<TimelineSegmentPreviewEvent> destination)
    {
        (long startTick, long endTick) = ToContentRange(normalizedStart, normalizedEnd);
        if (endTick <= startTick) return;
        long visibleStart = _segment.ContentOffsetTick;
        foreach (DirectMidiChannelEventValue value in _segment.ChannelEvents.QueryValues(startTick, endTick))
        {
            if (value.Kind is DirectMidiChannelEventKind.NoteOn or DirectMidiChannelEventKind.NoteOff)
                continue;
            destination.Add(new(
                (value.Tick - visibleStart) / (double)_segment.LengthTicks,
                Normalize(value)));
        }
        foreach (OpaqueMidiEventValue value in _segment.OpaqueEvents.QueryValues(startTick, endTick))
        {
            destination.Add(new(
                (value.Tick - visibleStart) / (double)_segment.LengthTicks,
                1));
        }
    }

    public void VisitNotes(
        double normalizedStart,
        double normalizedEnd,
        Action<TimelineSegmentPreviewNote> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        (long startTick, long endTick) = ToContentRange(normalizedStart, normalizedEnd);
        if (endTick <= startTick) return;
        long visibleStart = _segment.ContentOffsetTick;
        long visibleEnd = _segment.ContentEndTick;
        foreach (DirectMidiNoteValue note in _segment.Notes.QueryValues(startTick, endTick))
        {
            long noteEnd = note.StartTick > long.MaxValue - Math.Max(1, note.LengthTicks)
                ? long.MaxValue
                : note.StartTick + Math.Max(1, note.LengthTicks);
            long clippedStart = Math.Max(visibleStart, note.StartTick);
            long clippedEnd = Math.Min(visibleEnd, noteEnd);
            if (clippedEnd <= clippedStart) continue;
            visitor(new(
                (clippedStart - visibleStart) / (double)_segment.LengthTicks,
                (clippedEnd - visibleStart) / (double)_segment.LengthTicks,
                Math.Clamp(note.Key, 0, 127)));
        }
    }

    public void VisitEvents(
        double normalizedStart,
        double normalizedEnd,
        Action<TimelineSegmentPreviewEvent> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        (long startTick, long endTick) = ToContentRange(normalizedStart, normalizedEnd);
        if (endTick <= startTick) return;
        long visibleStart = _segment.ContentOffsetTick;
        foreach (DirectMidiChannelEventValue value in _segment.ChannelEvents.QueryValues(startTick, endTick))
        {
            if (value.Kind is DirectMidiChannelEventKind.NoteOn or DirectMidiChannelEventKind.NoteOff)
                continue;
            visitor(new(
                (value.Tick - visibleStart) / (double)_segment.LengthTicks,
                Normalize(value)));
        }
        foreach (OpaqueMidiEventValue value in _segment.OpaqueEvents.QueryValues(startTick, endTick))
        {
            visitor(new(
                (value.Tick - visibleStart) / (double)_segment.LengthTicks,
                1));
        }
    }

    private (long StartTick, long EndTick) ToContentRange(double normalizedStart, double normalizedEnd)
    {
        double clampedStart = Math.Clamp(normalizedStart, 0, 1);
        double clampedEnd = Math.Clamp(normalizedEnd, 0, 1);
        long start = checked(_segment.ContentOffsetTick
            + (long)Math.Floor(clampedStart * _segment.LengthTicks));
        long end = checked(_segment.ContentOffsetTick
            + (long)Math.Ceiling(clampedEnd * _segment.LengthTicks));
        return (
            Math.Clamp(start, _segment.ContentOffsetTick, _segment.ContentEndTick),
            Math.Clamp(end, _segment.ContentOffsetTick, _segment.ContentEndTick));
    }

    private static double Normalize(DirectMidiChannelEventValue value) => value.Kind switch
    {
        DirectMidiChannelEventKind.PolyphonicKeyPressure => value.Data2 / 127d,
        DirectMidiChannelEventKind.ControlChange => value.Data2 / 127d,
        DirectMidiChannelEventKind.ProgramChange => value.Data1 / 127d,
        DirectMidiChannelEventKind.ChannelPressure => value.Data1 / 127d,
        DirectMidiChannelEventKind.PitchBend => ((value.Data2 << 7) | value.Data1) / 16383d,
        _ => value.Data2 / 127d
    };
}

internal enum DirectMidiTimelineProjection
{
    Notes,
    Velocities,
    ChannelEvents,
    OpaqueEvents
}

internal sealed class PagedDirectMidiTimelineItemSource : ITimelineRenderItemSource
{
    private readonly MidiSegment _segment;
    private readonly DirectMidiTimelineProjection _projection;
    private readonly DirectMidiEventLaneTarget? _eventTarget;
    private readonly HashSet<MidoraId> _selectedIds;
    private readonly MidoraId? _primaryId;
    private readonly long _count;

    public PagedDirectMidiTimelineItemSource(
        MidiSegment segment,
        DirectMidiTimelineProjection projection,
        DirectMidiEventLaneTarget? eventTarget = null,
        IEnumerable<MidoraId>? selectedIds = null,
        MidoraId? primaryId = null)
    {
        _segment = segment ?? throw new ArgumentNullException(nameof(segment));
        _projection = projection;
        _eventTarget = eventTarget;
        _selectedIds = selectedIds is null ? [] : new HashSet<MidoraId>(selectedIds);
        _primaryId = primaryId;
        _count = projection switch
        {
            DirectMidiTimelineProjection.Notes or DirectMidiTimelineProjection.Velocities => segment.Notes.Count,
            DirectMidiTimelineProjection.OpaqueEvents => segment.OpaqueEvents.Count,
            DirectMidiTimelineProjection.ChannelEvents => eventTarget is null
                ? 0
                : segment.ChannelEvents.Count,
            _ => 0
        };
    }

    public long Count => _count;
    public long MaximumEndTick => _segment.ContentEndTick;
    public ulong ContentFingerprint => PureMidiPresentationFingerprint.Create(
        _segment.PagedContentFingerprint,
        _segment.Notes.Generation,
        _segment.ChannelEvents.Generation,
        _segment.OpaqueEvents.Generation,
        _segment.ContentOffsetTick,
        _segment.LengthTicks,
        (long)_projection,
        _eventTarget is null ? -1 : (long)_eventTarget.Value.Kind,
        _eventTarget?.Data1 ?? -1);

    public void QueryInto(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        List<TimelineRenderItem> destination)
    {
        if (endTick <= startTick || lastLaneExclusive <= firstLane) return;
        switch (_projection)
        {
            case DirectMidiTimelineProjection.Notes:
                {
                    int minimumKey = Math.Clamp(128 - lastLaneExclusive, 0, 127);
                    int maximumKey = Math.Clamp(127 - firstLane, 0, 127);
                    if (maximumKey < minimumKey) return;
                    foreach (DirectMidiNoteValue note in _segment.Notes.QueryValues(
                        startTick,
                        endTick,
                        minimumKey,
                        maximumKey))
                    {
                        destination.Add(ToNoteItem(note));
                    }
                    break;
                }
            case DirectMidiTimelineProjection.Velocities:
                if (firstLane > 0 || lastLaneExclusive <= 0) return;
                foreach (DirectMidiNoteValue note in _segment.Notes.QueryValues(startTick, endTick))
                    destination.Add(ToVelocityItem(note));
                break;
            case DirectMidiTimelineProjection.ChannelEvents:
                if (firstLane > 0 || lastLaneExclusive <= 0 || _eventTarget is null) return;
                foreach (DirectMidiChannelEventValue value in _segment.ChannelEvents.QueryValues(startTick, endTick))
                {
                    if (ToLaneTarget(value) == _eventTarget.Value)
                        destination.Add(ToEventItem(value));
                }
                break;
            case DirectMidiTimelineProjection.OpaqueEvents:
                if (firstLane > 0 || lastLaneExclusive <= 0) return;
                foreach (OpaqueMidiEventValue value in _segment.OpaqueEvents.QueryValues(startTick, endTick))
                    destination.Add(ToOpaqueItem(value));
                break;
        }
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
        switch (_projection)
        {
            case DirectMidiTimelineProjection.Notes:
                {
                    int minimumKey = Math.Clamp(128 - lastLaneExclusive, 0, 127);
                    int maximumKey = Math.Clamp(127 - firstLane, 0, 127);
                    if (maximumKey < minimumKey) return;
                    foreach (DirectMidiNoteValue note in _segment.Notes.QueryValues(
                        startTick,
                        endTick,
                        minimumKey,
                        maximumKey))
                    {
                        visitor(ToNoteItem(note));
                    }
                    break;
                }
            case DirectMidiTimelineProjection.Velocities:
                if (firstLane > 0 || lastLaneExclusive <= 0) return;
                foreach (DirectMidiNoteValue note in _segment.Notes.QueryValues(startTick, endTick))
                    visitor(ToVelocityItem(note));
                break;
            case DirectMidiTimelineProjection.ChannelEvents:
                if (firstLane > 0 || lastLaneExclusive <= 0 || _eventTarget is null) return;
                foreach (DirectMidiChannelEventValue value in _segment.ChannelEvents.QueryValues(startTick, endTick))
                {
                    if (ToLaneTarget(value) == _eventTarget.Value) visitor(ToEventItem(value));
                }
                break;
            case DirectMidiTimelineProjection.OpaqueEvents:
                if (firstLane > 0 || lastLaneExclusive <= 0) return;
                foreach (OpaqueMidiEventValue value in _segment.OpaqueEvents.QueryValues(startTick, endTick))
                    visitor(ToOpaqueItem(value));
                break;
        }
    }

    public bool TryGetById(MidoraId id, out TimelineRenderItem item)
    {
        switch (_projection)
        {
            case DirectMidiTimelineProjection.Notes:
                if (_segment.Notes.TryGetById(id, out DirectMidiNote? note) && note is not null)
                {
                    item = ToNoteItem(note);
                    return true;
                }
                break;
            case DirectMidiTimelineProjection.Velocities:
                if (_segment.Notes.TryGetById(id, out DirectMidiNote? velocityNote) && velocityNote is not null)
                {
                    item = ToVelocityItem(velocityNote);
                    return true;
                }
                break;
            case DirectMidiTimelineProjection.ChannelEvents:
                if (_segment.ChannelEvents.TryGetById(id, out DirectMidiChannelEvent? value)
                    && value is not null
                    && _eventTarget is not null
                    && TimelineWorkspaceViewModel.ToDirectMidiLaneTarget(value) == _eventTarget.Value)
                {
                    item = ToEventItem(value);
                    return true;
                }
                break;
            case DirectMidiTimelineProjection.OpaqueEvents:
                if (_segment.OpaqueEvents.TryGetById(id, out OpaqueMidiEvent? opaque) && opaque is not null)
                {
                    item = ToOpaqueItem(opaque);
                    return true;
                }
                break;
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
        switch (_projection)
        {
            case DirectMidiTimelineProjection.Notes:
                foreach (DirectMidiNoteMatch match in _segment.Notes.ResolveByIds(ids))
                    destination.Add(ToNoteItem(match.Value));
                break;
            case DirectMidiTimelineProjection.Velocities:
                foreach (DirectMidiNoteMatch match in _segment.Notes.ResolveByIds(ids))
                    destination.Add(ToVelocityItem(match.Value));
                break;
            case DirectMidiTimelineProjection.ChannelEvents when _eventTarget is DirectMidiEventLaneTarget target:
                foreach (DirectMidiChannelEventMatch match in _segment.ChannelEvents.ResolveByIds(ids))
                {
                    if (TimelineWorkspaceViewModel.ToDirectMidiLaneTarget(match.Value) == target)
                        destination.Add(ToEventItem(match.Value));
                }
                break;
            case DirectMidiTimelineProjection.OpaqueEvents:
                foreach (OpaqueMidiEventMatch match in _segment.OpaqueEvents.ResolveByIds(ids))
                    destination.Add(ToOpaqueItem(match.Value));
                break;
        }
    }

    public IEnumerable<TimelineRenderItem> EnumerateAll() => _projection switch
    {
        DirectMidiTimelineProjection.Notes => _segment.Notes.Select(ToNoteItem),
        DirectMidiTimelineProjection.Velocities => _segment.Notes.Select(ToVelocityItem),
        DirectMidiTimelineProjection.ChannelEvents when _eventTarget is DirectMidiEventLaneTarget target =>
            _segment.ChannelEvents
                .Where(value => TimelineWorkspaceViewModel.ToDirectMidiLaneTarget(value) == target)
                .Select(ToEventItem),
        DirectMidiTimelineProjection.OpaqueEvents => _segment.OpaqueEvents.Select(ToOpaqueItem),
        _ => []
    };

    public void AccumulateOverviewDensity(long extent, Span<int> destination)
    {
        if (_projection != DirectMidiTimelineProjection.Notes
            || extent <= 0
            || destination.IsEmpty)
        {
            return;
        }

        Span<byte> occupiedColumns = destination.Length <= 4096
            ? stackalloc byte[destination.Length]
            : new byte[destination.Length];
        _segment.Notes.AccumulateOverviewColumns(extent, occupiedColumns);
        for (int x = 0; x < occupiedColumns.Length; x++)
        {
            if (occupiedColumns[x] != 0 && destination[x] < int.MaxValue)
                destination[x]++;
        }
    }

    private TimelineRenderItem ToNoteItem(DirectMidiNote note) => new(
        note.Id,
        TimelineItemKind.DirectMidiNote,
        note.StartTick,
        checked(note.StartTick + note.LengthTicks),
        127 - Math.Clamp(note.Key, 0, 127),
        note.NoteOnVelocity,
        1,
        State(note.Id, note.StartTick, note.Key is < 0 or > 127));

    private TimelineRenderItem ToNoteItem(DirectMidiNoteValue note) => new(
        note.Id,
        TimelineItemKind.DirectMidiNote,
        note.StartTick,
        checked(note.StartTick + note.LengthTicks),
        127 - Math.Clamp(note.Key, 0, 127),
        note.NoteOnVelocity,
        1,
        State(note.Id, note.StartTick, note.Key is < 0 or > 127));

    private TimelineRenderItem ToVelocityItem(DirectMidiNote note) => new(
        note.Id,
        TimelineItemKind.Velocity,
        note.StartTick,
        checked(note.StartTick + 1),
        0,
        note.NoteOnVelocity / 127d,
        note.Key,
        State(note.Id, note.StartTick, invalid: false));

    private TimelineRenderItem ToVelocityItem(DirectMidiNoteValue note) => new(
        note.Id,
        TimelineItemKind.Velocity,
        note.StartTick,
        checked(note.StartTick + 1),
        0,
        note.NoteOnVelocity / 127d,
        note.Key,
        State(note.Id, note.StartTick, invalid: false));

    private TimelineRenderItem ToEventItem(DirectMidiChannelEvent value) => new(
        value.Id,
        TimelineItemKind.DirectMidiEvent,
        value.Tick,
        checked(value.Tick + 1),
        0,
        TimelineWorkspaceViewModel.NormalizeDirectMidiEventValue(value),
        1,
        State(value.Id, value.Tick, invalid: false));

    private TimelineRenderItem ToEventItem(DirectMidiChannelEventValue value) => new(
        value.Id,
        TimelineItemKind.DirectMidiEvent,
        value.Tick,
        checked(value.Tick + 1),
        0,
        Normalize(value),
        1,
        State(value.Id, value.Tick, invalid: false));

    private TimelineRenderItem ToOpaqueItem(OpaqueMidiEvent value) => new(
        value.Id,
        TimelineItemKind.OpaqueMidiEvent,
        value.Tick,
        checked(value.Tick + 1),
        0,
        1,
        1,
        State(value.Id, value.Tick, invalid: false))
    {
        Label = TimelineWorkspaceViewModel.OpaqueMidiEventLabel(value)
    };

    private TimelineRenderItem ToOpaqueItem(OpaqueMidiEventValue value) => new(
        value.Id,
        TimelineItemKind.OpaqueMidiEvent,
        value.Tick,
        checked(value.Tick + 1),
        0,
        1,
        1,
        State(value.Id, value.Tick, invalid: false));

    private static DirectMidiEventLaneTarget ToLaneTarget(DirectMidiChannelEventValue value) => new(
        value.Kind,
        value.Kind is DirectMidiChannelEventKind.ControlChange
            or DirectMidiChannelEventKind.PolyphonicKeyPressure
            or DirectMidiChannelEventKind.NoteOn
            or DirectMidiChannelEventKind.NoteOff
            ? value.Data1
            : 0);

    private static double Normalize(DirectMidiChannelEventValue value) => value.Kind switch
    {
        DirectMidiChannelEventKind.PolyphonicKeyPressure => value.Data2 / 127d,
        DirectMidiChannelEventKind.ControlChange => value.Data2 / 127d,
        DirectMidiChannelEventKind.ProgramChange => value.Data1 / 127d,
        DirectMidiChannelEventKind.ChannelPressure => value.Data1 / 127d,
        DirectMidiChannelEventKind.PitchBend => ((value.Data2 << 7) | value.Data1) / 16383d,
        _ => value.Data2 / 127d
    };

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

/// <summary>
/// Exact, device-column-bounded overview projection for a complete Direct MIDI
/// Segment. Paged sources use their ordered endpoint indexes and may only use a
/// page range without decoding when the complete page maps to one output column.
/// Collection generations keep the surface cache coherent with copy-on-write edits.
/// </summary>
internal sealed class PureMidiSegmentOverviewSource : ITimelineOverviewSource
{
    private readonly MidiSegment _segment;

    public PureMidiSegmentOverviewSource(MidiSegment segment)
    {
        _segment = segment ?? throw new ArgumentNullException(nameof(segment));
        MaximumEndTick = ResolveMaximumEndTick(segment);
    }

    public long MaximumEndTick { get; }

    public ulong ContentFingerprint => PureMidiPresentationFingerprint.Create(
        _segment.PagedContentFingerprint,
        _segment.Notes.Generation,
        _segment.ChannelEvents.Generation,
        _segment.OpaqueEvents.Generation,
        _segment.ContentOffsetTick,
        _segment.LengthTicks,
        0x4f56455256494557);

    public void Accumulate(
        long extent,
        Span<byte> noteStartColumns,
        Span<byte> eventColumns)
    {
        if (extent <= 0) throw new ArgumentOutOfRangeException(nameof(extent));
        if (noteStartColumns.Length != eventColumns.Length)
            throw new ArgumentException("Timeline overview channels must have equal widths.");
        if (noteStartColumns.IsEmpty) return;

        _segment.Notes.AccumulateOverviewColumns(extent, noteStartColumns);
        _segment.ChannelEvents.AccumulateOverviewColumns(
            extent,
            noteStartColumns,
            eventColumns);
        _segment.OpaqueEvents.AccumulateOverviewColumns(extent, eventColumns);
    }

    private static long ResolveMaximumEndTick(MidiSegment segment)
    {
        long maximum = segment.ContentEndTick;
        foreach (PureMidiContentRangeSummary summary in segment.Notes.GetOverviewRangeSummaries()
            .Concat(segment.ChannelEvents.GetOverviewRangeSummaries())
            .Concat(segment.OpaqueEvents.GetOverviewRangeSummaries()))
        {
            long end = summary.MaximumTick == long.MaxValue
                ? long.MaxValue
                : summary.MaximumTick + 1;
            maximum = Math.Max(maximum, end);
        }
        return maximum;
    }
}

internal static class PureMidiPresentationFingerprint
{
    private const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static ulong Create(string? text, params long[] values)
    {
        ulong hash = Offset;
        if (text is not null)
        {
            foreach (byte value in Encoding.UTF8.GetBytes(text))
            {
                hash ^= value;
                hash *= Prime;
            }
        }
        foreach (long value in values)
        {
            hash ^= unchecked((ulong)value);
            hash *= Prime;
        }
        return hash;
    }
}
