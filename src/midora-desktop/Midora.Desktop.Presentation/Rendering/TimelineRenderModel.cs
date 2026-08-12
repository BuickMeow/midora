using Midora.Domain;

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
    LogicalParameterPoint,
    LogicalParameterCurve,
    TemplateNote,
    TemplateEvent,
    ConductorEvent,
    Marker,
    ProjectEndMarker,
    LifecycleBoundary
    ,Velocity
}

[Flags]
public enum TimelineLaneState
{
    None = 0,
    Muted = 1 << 0,
    Solo = 1 << 1
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
}

public readonly record struct TimelineSegmentPreviewNote(
    double NormalizedStart,
    double NormalizedEnd,
    int Pitch);

public sealed class TimelineSegmentPreview
{
    public TimelineSegmentPreview(
        MidoraId segmentId,
        IEnumerable<TimelineSegmentPreviewNote> notes)
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
        SegmentId = segmentId;
        Notes = Array.AsReadOnly(materialized);
        ContentFingerprint = TimelineContentFingerprint.ForSegmentPreview(materialized);
    }

    public MidoraId SegmentId { get; }
    public IReadOnlyList<TimelineSegmentPreviewNote> Notes { get; }
    public ulong ContentFingerprint { get; }
}

public sealed class TimelineSelectionSnapshot
{
    private readonly HashSet<MidoraId> _ids;

    public TimelineSelectionSnapshot(
        long revision,
        IEnumerable<MidoraId> ids,
        MidoraId? primary)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        ArgumentNullException.ThrowIfNull(ids);
        _ids = new HashSet<MidoraId>(ids);
        if (_ids.Any(static id => id.Value <= 0))
        {
            throw new ArgumentException("Selection contains an invalid stable ID.", nameof(ids));
        }
        if (primary is MidoraId primaryId && !_ids.Contains(primaryId))
        {
            throw new ArgumentException("Primary selection must belong to the selection set.", nameof(primary));
        }
        Revision = revision;
        Primary = primary;
    }

    public long Revision { get; }
    public MidoraId? Primary { get; }
    public int Count => _ids.Count;
    public IEnumerable<MidoraId> Ids => _ids;
    public bool Contains(MidoraId id) => _ids.Contains(id);
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
    private readonly object _pianoTileFingerprintGate = new();
    private readonly Dictionary<PianoTileFingerprintKey, ulong> _pianoTileFingerprints = [];

    public TimelineRenderSnapshot(
        long semanticRevision,
        string projectionKey,
        IEnumerable<TimelineRenderItem> items,
        IReadOnlyList<string>? laneLabels = null,
        IReadOnlyList<TimelineLaneState>? laneStates = null,
        IReadOnlyDictionary<MidoraId, TimelineSegmentPreview>? segmentPreviews = null)
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
        Items = Array.AsReadOnly(materialized);
        LaneLabels = laneLabels is null
            ? Array.Empty<string>()
            : Array.AsReadOnly(laneLabels.Select(static label => label?.Trim() ?? string.Empty).ToArray());
        LaneStates = laneStates is null
            ? Array.Empty<TimelineLaneState>()
            : Array.AsReadOnly(laneStates.ToArray());
        SegmentPreviews = segmentPreviews is null
            ? new Dictionary<MidoraId, TimelineSegmentPreview>()
            : new Dictionary<MidoraId, TimelineSegmentPreview>(segmentPreviews);
        Index = new TimelineIntervalIndex(materialized);
        ItemsById = materialized
            .GroupBy(static item => item.Id)
            .ToDictionary(static group => group.Key, static group => group.OrderByDescending(item => item.ZIndex).First());
        ContentFingerprint = TimelineContentFingerprint.ForRenderItems(materialized);
    }

    public long SemanticRevision { get; }
    public string ProjectionKey { get; }
    public IReadOnlyList<TimelineRenderItem> Items { get; }
    public IReadOnlyList<string> LaneLabels { get; }
    public IReadOnlyList<TimelineLaneState> LaneStates { get; }
    public IReadOnlyDictionary<MidoraId, TimelineSegmentPreview> SegmentPreviews { get; }
    public TimelineIntervalIndex Index { get; }
    public IReadOnlyDictionary<MidoraId, TimelineRenderItem> ItemsById { get; }
    public ulong ContentFingerprint { get; }

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
        lock (_pianoTileFingerprintGate)
        {
            if (_pianoTileFingerprints.TryGetValue(key, out ulong fingerprint))
            {
                return fingerprint;
            }
            fingerprint = TimelinePianoTileRasterizer.ComputeContentFingerprint(
                this,
                devicePixelsPerTick,
                devicePixelsPerLane,
                tileX,
                tileY);
            _pianoTileFingerprints.Add(key, fingerprint);
            return fingerprint;
        }
    }

    private readonly record struct PianoTileFingerprintKey(
        long HorizontalScaleKey,
        long VerticalScaleKey,
        long TileX,
        long TileY);
}

internal static class TimelineContentFingerprint
{
    private const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static ulong ForSegmentPreview(IEnumerable<TimelineSegmentPreviewNote> notes)
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
        }
        return hash;
    }

    public static ulong ForPianoTileItems(IEnumerable<TimelineRenderItem> items)
    {
        ulong hash = Offset;
        foreach (TimelineRenderItem item in items)
        {
            if (item.Kind is not (TimelineItemKind.LogicalNote or TimelineItemKind.TemplateNote))
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
            Add(ref hash, selection?.Contains(item.Id) == true ? 1UL : 0UL);
        }
        Add(ref hash, unchecked((ulong)(selection?.Revision ?? 0)));
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
