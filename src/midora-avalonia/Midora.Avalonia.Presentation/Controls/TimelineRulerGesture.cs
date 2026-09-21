namespace Midora.Avalonia.Presentation.Controls;

/// <summary>Outcome kind of a completed ruler gesture (SRS 20.1.3).</summary>
public enum TimelineRulerGestureKind
{
    /// <summary>Nothing is requested: a Ctrl drag that crossed the threshold.</summary>
    None,

    /// <summary>Move the Playback Cursor to <see cref="TimelineRulerGestureResult.Tick"/>.</summary>
    Seek,

    /// <summary>Move the Edit Cursor to <see cref="TimelineRulerGestureResult.Tick"/>.</summary>
    EditCursor,

    /// <summary>Create a Time Range Selection from <c>StartTick</c> to <c>EndTick</c>.</summary>
    TimeRange
}

public readonly record struct TimelineRulerGestureResult(
    TimelineRulerGestureKind Kind,
    long Tick,
    long StartTick,
    long EndTick);

/// <summary>
/// Pure ruler gesture state machine for SRS 20.1.3: a click sets the Playback Cursor, a Ctrl+click
/// only sets the Edit Cursor (never seeking or clearing the selection), and a drag creates a Time
/// Range Selection. The Ctrl state and the pointer origin are frozen at pointer down, and a drag
/// that crosses the drag threshold is neither treated as that click nor degraded to a Time Range
/// when Ctrl was held.
/// </summary>
public sealed class TimelineRulerGesture
{
    private double _originX;
    private double _originY;
    private bool _editCursorRequested;

    public bool IsActive { get; private set; }

    public long StartTick { get; private set; }

    public long CurrentTick { get; private set; }

    public bool ExceededThreshold { get; private set; }

    /// <summary>Begins a gesture; <paramref name="editCursorRequested"/> is the frozen Ctrl state.</summary>
    public void Begin(double originX, double originY, long startTick, bool editCursorRequested)
    {
        IsActive = true;
        ExceededThreshold = false;
        _originX = originX;
        _originY = originY;
        _editCursorRequested = editCursorRequested;
        StartTick = Math.Max(0, startTick);
        CurrentTick = StartTick;
    }

    public void Move(double x, double y, long tick, double dragThreshold)
    {
        if (!IsActive)
        {
            return;
        }

        CurrentTick = Math.Max(0, tick);
        if (ExceededThreshold)
        {
            return;
        }

        double dx = x - _originX;
        double dy = y - _originY;
        ExceededThreshold = dx * dx + dy * dy >= dragThreshold * dragThreshold;
    }

    public TimelineRulerGestureResult Complete(long tick)
    {
        if (!IsActive)
        {
            return new(TimelineRulerGestureKind.None, 0, 0, 0);
        }

        long completedTick = Math.Max(0, tick);
        bool editCursor = _editCursorRequested;
        bool exceeded = ExceededThreshold;
        Cancel();
        if (editCursor)
        {
            return exceeded
                ? new(TimelineRulerGestureKind.None, completedTick, 0, 0)
                : new(TimelineRulerGestureKind.EditCursor, completedTick, completedTick, completedTick);
        }

        if (!exceeded)
        {
            return new(TimelineRulerGestureKind.Seek, completedTick, completedTick, completedTick);
        }

        (long start, long end) = Normalize(StartTick, completedTick);
        return new(TimelineRulerGestureKind.TimeRange, completedTick, start, end);
    }

    public void Cancel()
    {
        IsActive = false;
        ExceededThreshold = false;
        _editCursorRequested = false;
    }

    private static (long Start, long End) Normalize(long left, long right)
    {
        long start = Math.Min(left, right);
        long end = Math.Max(left, right);
        if (end <= start)
        {
            end = start + 1;
        }

        return (start, end);
    }
}
