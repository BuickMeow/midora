using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Midora.Avalonia.Presentation.Controls;

namespace Midora.Avalonia.Presentation.Tests;

/// <summary>
/// Verifies the SRS 20.1.2/20.1.3/20.1.5 ruler, cursor, zoom and pan interactions of the shared
/// timeline surface through synthetic pointer input.
/// </summary>
public sealed class TimelineSurfaceInteractionTests
{
    private const double RulerY = 10;
    private const double ContentY = 120;

    [AvaloniaFact]
    public void RulerClickRequestsPlaybackCursorAtSnappedTick()
    {
        (Window window, TimelineSurface surface) = CreateSurface();
        List<long> seeks = [];
        List<long> edits = [];
        surface.PlaybackCursorRequested += (_, tick) => seeks.Add(tick);
        surface.EditCursorRequested += (_, tick) => edits.Add(tick);

        Click(window, surface, 410);

        Assert.Equal([960L], seeks);
        Assert.Empty(edits);
    }

    [AvaloniaFact]
    public void CtrlRulerClickMovesOnlyTheEditCursor()
    {
        (Window window, TimelineSurface surface) = CreateSurface();
        List<long> seeks = [];
        List<long> edits = [];
        surface.PlaybackCursorRequested += (_, tick) => seeks.Add(tick);
        surface.EditCursorRequested += (_, tick) => edits.Add(tick);

        Click(window, surface, 410, RawInputModifiers.Control);

        Assert.Empty(seeks);
        Assert.Equal([960L], edits);
        Assert.Equal(960, surface.EditCursorTick);
    }

    [AvaloniaFact]
    public void RulerDragCreatesTimeRangeWithoutSeeking()
    {
        (Window window, TimelineSurface surface) = CreateSurface();
        List<long> seeks = [];
        List<TimelineTimeRangeEventArgs> ranges = [];
        surface.PlaybackCursorRequested += (_, tick) => seeks.Add(tick);
        surface.TimeRangeSelected += (_, range) => ranges.Add(range);

        window.MouseDown(new Point(200, RulerY), MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseMove(new Point(420, RulerY), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(420, RulerY), MouseButton.Left, RawInputModifiers.None);

        Assert.Empty(seeks);
        TimelineTimeRangeEventArgs range = Assert.Single(ranges);
        Assert.Equal(480, range.StartTick);
        Assert.Equal(960, range.EndTick);
        Assert.Equal(480, surface.TimeRangeStartTick);
        Assert.Equal(960, surface.TimeRangeEndTick);
    }

    [AvaloniaFact]
    public void CtrlRulerDragDoesNotCreateTimeRangeOrSeek()
    {
        (Window window, TimelineSurface surface) = CreateSurface();
        List<long> seeks = [];
        List<long> edits = [];
        List<TimelineTimeRangeEventArgs> ranges = [];
        surface.PlaybackCursorRequested += (_, tick) => seeks.Add(tick);
        surface.EditCursorRequested += (_, tick) => edits.Add(tick);
        surface.TimeRangeSelected += (_, range) => ranges.Add(range);

        window.MouseDown(
            new Point(200, RulerY),
            MouseButton.Left,
            RawInputModifiers.Control | RawInputModifiers.LeftMouseButton);
        window.MouseMove(
            new Point(420, RulerY),
            RawInputModifiers.Control | RawInputModifiers.LeftMouseButton);
        window.MouseUp(
            new Point(420, RulerY),
            MouseButton.Left,
            RawInputModifiers.Control);

        Assert.Empty(seeks);
        Assert.Empty(edits);
        Assert.Empty(ranges);
        Assert.Equal(-1, surface.TimeRangeStartTick);
    }

    [AvaloniaFact]
    public void EmptyContentClickMovesTheEditCursor()
    {
        (Window window, TimelineSurface surface) = CreateSurface();
        List<long> edits = [];
        surface.EditCursorRequested += (_, tick) => edits.Add(tick);

        Click(window, surface, 410, y: ContentY);

        Assert.Equal([960L], edits);
    }

    [AvaloniaFact]
    public void WheelPansBothAxes()
    {
        (Window window, TimelineSurface surface) = CreateSurface();
        surface.LaneCount = 40;
        surface.FirstLane = 10;

        // Negative X scrolls toward later ticks; positive Y scrolls toward earlier lanes,
        // matching the WPF reference convention.
        window.MouseWheel(new Point(400, ContentY), new Vector(-1, 1), RawInputModifiers.None);

        Assert.True(surface.StartTick > 0, "a horizontal wheel delta must scroll time");
        Assert.True(surface.FirstLane < 10, "a vertical wheel delta must scroll lanes");
    }

    [AvaloniaFact]
    public void SubUnitTrackpadDeltasAccumulateWithoutLosingDirection()
    {
        (Window window, TimelineSurface surface) = CreateSurface();
        surface.LaneCount = 40;
        surface.FirstLane = 10;

        // A horizontal swipe with a tiny vertical component must not scroll lanes at all, and the
        // horizontal direction must survive sub-unit deltas.
        for (int step = 0; step < 12; step++)
        {
            window.MouseWheel(
                new Point(400, ContentY),
                new Vector(-0.1, 0.05),
                RawInputModifiers.None);
        }

        Assert.True(surface.StartTick > 0, "accumulated horizontal deltas must scroll time");
        Assert.Equal(10, surface.FirstLane);

        // Repeating the same vertical direction must eventually scroll the expected way.
        for (int step = 0; step < 40; step++)
        {
            window.MouseWheel(new Point(400, ContentY), new Vector(0, -0.1), RawInputModifiers.None);
        }

        Assert.True(surface.FirstLane > 10, "accumulated downward deltas must scroll later lanes");
    }

    [AvaloniaFact]
    public void ShiftWheelKeepsHorizontalScroll()
    {
        (Window window, TimelineSurface surface) = CreateSurface();
        surface.LaneCount = 40;
        surface.FirstLane = 10;

        window.MouseWheel(
            new Point(400, ContentY),
            new Vector(0, 1),
            RawInputModifiers.Shift);

        Assert.True(surface.StartTick > 0, "Shift+wheel must keep scrolling time (SRS 20.1.5)");
        Assert.Equal(10, surface.FirstLane);
    }

    [AvaloniaFact]
    public void PinchZoomsHorizontallyAroundTheGestureOrigin()
    {
        (Window window, TimelineSurface surface) = CreateSurface();
        long spanBefore = surface.TickSpan;
        long tickUnderOrigin = TickAt(surface, 400);

        Assert.True(surface.ApplyPinchZoom(1.5, new Point(400, ContentY)));

        Assert.True(surface.TickSpan < spanBefore, "pinching out must zoom in");
        long tickAfter = TickAt(surface, 400);
        Assert.InRange(Math.Abs(tickAfter - tickUnderOrigin), 0, 2);
    }

    [AvaloniaFact]
    public void TouchPadMagnifyZoomsHorizontallyAroundTheGestureOrigin()
    {
        (Window window, TimelineSurface surface) = CreateSurface();
        _ = window;
        long spanBefore = surface.TickSpan;
        long tickUnderOrigin = TickAt(surface, 400);

        Assert.True(surface.ApplyMagnifyDelta(0.5, new Point(400, ContentY)));

        Assert.True(surface.TickSpan < spanBefore, "magnifying out must zoom in");
        Assert.InRange(Math.Abs(TickAt(surface, 400) - tickUnderOrigin), 0, 3);
    }

    private static long TickAt(TimelineSurface surface, double x) =>
        surface.StartTick + (long)Math.Round(x / surface.Bounds.Width * surface.TickSpan);

    private static void Click(
        Window window,
        TimelineSurface surface,
        double x,
        RawInputModifiers modifiers = RawInputModifiers.None,
        double y = RulerY)
    {
        _ = surface;
        window.MouseDown(new Point(x, y), MouseButton.Left, modifiers);
        window.MouseUp(new Point(x, y), MouseButton.Left, modifiers);
    }

    private static (Window Window, TimelineSurface Surface) CreateSurface()
    {
        TimelineSurface surface = new()
        {
            SurfaceMode = TimelineSurfaceMode.Arrangement,
            StartTick = 0,
            TickSpan = 1920,
            TicksPerQuarterNote = 480,
            OperationStepTicks = 240,
            LaneCount = 1
        };
        Window window = new() { Width = 800, Height = 400, Content = surface };
        window.Show();
        return (window, surface);
    }
}
