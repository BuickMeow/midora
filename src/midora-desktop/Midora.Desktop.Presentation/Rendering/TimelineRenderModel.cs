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
    public TimelineRenderSnapshot(
        long semanticRevision,
        string projectionKey,
        IEnumerable<TimelineRenderItem> items,
        IReadOnlyList<string>? laneLabels = null,
        IReadOnlyList<TimelineLaneState>? laneStates = null)
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
        Index = new TimelineIntervalIndex(materialized);
    }

    public long SemanticRevision { get; }
    public string ProjectionKey { get; }
    public IReadOnlyList<TimelineRenderItem> Items { get; }
    public IReadOnlyList<string> LaneLabels { get; }
    public IReadOnlyList<TimelineLaneState> LaneStates { get; }
    public TimelineIntervalIndex Index { get; }

    public bool Matches(long semanticRevision, string projectionKey) =>
        SemanticRevision == semanticRevision
        && string.Equals(ProjectionKey, projectionKey, StringComparison.Ordinal);
}
