using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Rendering;
using System.Windows.Input;

namespace Midora.Desktop.Presentation.Interaction;

public enum TimelinePointerIntent
{
    Default,
    Crosshair,
    Erase,
    Split,
    Move,
    ResizeHorizontal
}

public static class TimelineToolPolicy
{
    public static bool IsDirectEditingSurface(TimelineSurfaceMode surfaceMode) =>
        surfaceMode is TimelineSurfaceMode.Arrangement or TimelineSurfaceMode.PianoRoll;

    public static bool StartsMarqueeBeforeItemHit(
        TimelineToolMode toolMode,
        TimelineSurfaceMode surfaceMode,
        int clickCount) =>
        clickCount == 1
        && toolMode == TimelineToolMode.Select
        && IsDirectEditingSurface(surfaceMode);

    public static bool ForcesVelocityTrace(
        MouseButton button,
        ModifierKeys modifiers) =>
        button == MouseButton.Left && (modifiers & ModifierKeys.Alt) != 0;

    public static bool ForcesItemMove(
        TimelineToolMode toolMode,
        TimelineSurfaceMode surfaceMode,
        TimelineItemKind itemKind,
        ModifierKeys modifiers) =>
        (modifiers & ModifierKeys.Alt) != 0
        && toolMode == TimelineToolMode.Draw
        && IsDirectEditingSurface(surfaceMode)
        && IsDirectManipulationItem(itemKind);

    public static TimelineItemEditKind ResolveItemEditKind(
        TimelineToolMode toolMode,
        TimelineSurfaceMode surfaceMode,
        TimelineItemKind itemKind,
        ModifierKeys modifiers,
        bool isNearStart,
        bool isNearEnd)
    {
        if (itemKind == TimelineItemKind.LogicalParameterPoint
            || ForcesItemMove(toolMode, surfaceMode, itemKind, modifiers))
        {
            return TimelineItemEditKind.Move;
        }
        if (isNearStart) return TimelineItemEditKind.ResizeStart;
        return isNearEnd ? TimelineItemEditKind.ResizeEnd : TimelineItemEditKind.Move;
    }

    public static bool CanBeginItemEdit(
        TimelineToolMode toolMode,
        TimelineSurfaceMode surfaceMode,
        TimelineItemKind itemKind) =>
        IsDirectEditingSurface(surfaceMode)
            ? toolMode == TimelineToolMode.Draw && IsDirectManipulationItem(itemKind)
            : toolMode != TimelineToolMode.Erase;

    public static bool SupportsCopyDrag(
        TimelineToolMode toolMode,
        TimelineSurfaceMode surfaceMode,
        TimelineItemKind itemKind,
        TimelineItemEditKind editKind) =>
        toolMode == TimelineToolMode.Draw
        && editKind == TimelineItemEditKind.Move
        && surfaceMode is TimelineSurfaceMode.Arrangement or TimelineSurfaceMode.PianoRoll
        && itemKind is TimelineItemKind.Segment
            or TimelineItemKind.LogicalNote
            or TimelineItemKind.TemplateNote;

    public static bool RequestsBackgroundCreation(
        TimelineToolMode toolMode,
        TimelineSurfaceMode surfaceMode,
        int clickCount) =>
        IsDirectEditingSurface(surfaceMode)
            ? toolMode == TimelineToolMode.Draw
            : clickCount == 2 || toolMode == TimelineToolMode.Draw;

    public static TimelinePointerIntent GetPointerIntent(
        TimelineToolMode toolMode,
        TimelineSurfaceMode surfaceMode,
        bool isInContent,
        TimelineItemKind? itemKind,
        bool isNearHorizontalEdge,
        ModifierKeys modifiers = ModifierKeys.None)
    {
        if (!isInContent) return TimelinePointerIntent.Default;
        if (!IsDirectEditingSurface(surfaceMode))
        {
            if (toolMode == TimelineToolMode.Draw) return TimelinePointerIntent.Crosshair;
            if (toolMode == TimelineToolMode.Erase) return TimelinePointerIntent.Erase;
            if (itemKind is null) return TimelinePointerIntent.Default;
            return isNearHorizontalEdge
                ? TimelinePointerIntent.ResizeHorizontal
                : TimelinePointerIntent.Move;
        }
        if (toolMode == TimelineToolMode.Select) return TimelinePointerIntent.Crosshair;
        if (toolMode == TimelineToolMode.Erase) return TimelinePointerIntent.Erase;
        if (itemKind is null) return TimelinePointerIntent.Default;
        if (toolMode == TimelineToolMode.Split)
        {
            return itemKind == TimelineItemKind.Segment
                ? TimelinePointerIntent.Split
                : TimelinePointerIntent.Default;
        }
        if (toolMode != TimelineToolMode.Draw || !IsDirectManipulationItem(itemKind.Value))
        {
            return TimelinePointerIntent.Default;
        }
        if (ForcesItemMove(toolMode, surfaceMode, itemKind.Value, modifiers))
        {
            return TimelinePointerIntent.Move;
        }
        return isNearHorizontalEdge
            ? TimelinePointerIntent.ResizeHorizontal
            : TimelinePointerIntent.Move;
    }

    private static bool IsDirectManipulationItem(TimelineItemKind itemKind) =>
        itemKind is TimelineItemKind.Segment
            or TimelineItemKind.LogicalNote
            or TimelineItemKind.TemplateNote;
}
