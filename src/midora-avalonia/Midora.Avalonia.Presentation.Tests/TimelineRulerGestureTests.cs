using Midora.Avalonia.Presentation.Controls;

namespace Midora.Avalonia.Presentation.Tests;

/// <summary>
/// Covers the SRS 20.1.3 ruler rules that do not depend on platform pointer routing: the click
/// threshold, the frozen Ctrl state, and the "no Time Range fallback" rule for Ctrl drags.
/// </summary>
public sealed class TimelineRulerGestureTests
{
    private const double Threshold = 3;

    [Fact]
    public void ClickSeeksThePlaybackCursor()
    {
        TimelineRulerGesture gesture = new();
        gesture.Begin(100, 10, startTick: 960, editCursorRequested: false);
        gesture.Move(101, 10, 960, Threshold);

        TimelineRulerGestureResult result = gesture.Complete(960);

        Assert.Equal(TimelineRulerGestureKind.Seek, result.Kind);
        Assert.Equal(960, result.Tick);
        Assert.False(gesture.IsActive);
    }

    [Fact]
    public void CtrlClickMovesOnlyTheEditCursor()
    {
        TimelineRulerGesture gesture = new();
        gesture.Begin(100, 10, startTick: 960, editCursorRequested: true);
        gesture.Move(100, 10, 960, Threshold);

        TimelineRulerGestureResult result = gesture.Complete(960);

        Assert.Equal(TimelineRulerGestureKind.EditCursor, result.Kind);
        Assert.Equal(960, result.Tick);
    }

    [Fact]
    public void DragCreatesANormalizedTimeRange()
    {
        TimelineRulerGesture gesture = new();
        gesture.Begin(400, 10, startTick: 960, editCursorRequested: false);
        gesture.Move(200, 10, 480, Threshold);

        TimelineRulerGestureResult result = gesture.Complete(480);

        Assert.Equal(TimelineRulerGestureKind.TimeRange, result.Kind);
        Assert.Equal(480, result.StartTick);
        Assert.Equal(960, result.EndTick);
    }

    [Fact]
    public void CtrlDragIsNeitherAClickNorATimeRange()
    {
        TimelineRulerGesture gesture = new();
        gesture.Begin(400, 10, startTick: 960, editCursorRequested: true);
        gesture.Move(200, 10, 480, Threshold);

        TimelineRulerGestureResult result = gesture.Complete(480);

        Assert.Equal(TimelineRulerGestureKind.None, result.Kind);
    }

    [Fact]
    public void ModifierChangeDuringTheDragDoesNotSwitchTheGesture()
    {
        TimelineRulerGesture gesture = new();
        gesture.Begin(100, 10, startTick: 240, editCursorRequested: false);
        // A later Ctrl press is irrelevant: the intent is frozen at pointer down.
        gesture.Move(400, 10, 960, Threshold);

        TimelineRulerGestureResult result = gesture.Complete(960);

        Assert.Equal(TimelineRulerGestureKind.TimeRange, result.Kind);
    }

    [Fact]
    public void CancelledGestureRequestsNothing()
    {
        TimelineRulerGesture gesture = new();
        gesture.Begin(100, 10, startTick: 240, editCursorRequested: false);
        gesture.Cancel();

        TimelineRulerGestureResult result = gesture.Complete(240);

        Assert.Equal(TimelineRulerGestureKind.None, result.Kind);
        Assert.False(gesture.IsActive);
    }

    [Fact]
    public void ZeroLengthDragStillProducesAPositiveRange()
    {
        TimelineRulerGesture gesture = new();
        gesture.Begin(400, 10, startTick: 480, editCursorRequested: false);
        gesture.Move(200, 10, 480, Threshold);

        TimelineRulerGestureResult result = gesture.Complete(480);

        Assert.Equal(TimelineRulerGestureKind.TimeRange, result.Kind);
        Assert.Equal(480, result.StartTick);
        Assert.Equal(481, result.EndTick);
    }
}
