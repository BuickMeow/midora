using Avalonia;
using Avalonia.Media;
using Midora.Avalonia.Presentation.Rendering;

namespace Midora.Avalonia.Presentation.Controls;

public sealed partial class TimelineSurface
{
    private readonly List<TimelineRenderItem> _velocityItems = [];

    private void DrawVelocitySurface(
        DrawingContext context,
        TimelineViewport viewport,
        double width,
        double height)
    {
        DrawLaneBackgrounds(context, viewport, width, height);
        FlushShapes(context);
        DrawPitchLabels(context, viewport, width, height);
        if (Source is null)
        {
            return;
        }

        _velocityItems.Clear();
        Source.QueryInto(
            viewport.StartTick,
            viewport.EndTick,
            viewport.FirstLane,
            viewport.LastLaneExclusive,
            _velocityItems);
        double devicePixel = GetDevicePixelWidth();
        double barWidth = 3 * devicePixel;
        double divisor = Math.Max(1, ValueMaximum);
        foreach (TimelineRenderItem item in _velocityItems)
        {
            if (!IsNoteKind(item.Kind)
                || item.Lane < viewport.FirstLane
                || item.Lane >= viewport.LastLaneExclusive
                || item.EndTick <= viewport.StartTick
                || item.StartTick >= viewport.EndTick)
            {
                continue;
            }

            double laneTop = GetLaneTop(viewport, item.Lane);
            if (laneTop >= height || laneTop + viewport.LaneHeight <= RulerHeight)
            {
                continue;
            }

            double rowBottom = laneTop + viewport.LaneHeight;
            double fraction = double.IsFinite(item.Value)
                ? Math.Clamp(item.Value / divisor, 0, 1)
                : 0;
            if (!double.IsFinite(fraction))
            {
                fraction = 0;
            }

            double top = rowBottom - fraction * Math.Max(0, viewport.LaneHeight - 4);
            double x = Math.Clamp(
                viewport.TickToX(item.StartTick),
                0,
                Math.Max(0, viewport.Width - barWidth));
            bool selected = SelectedId == item.Id || item.State.HasFlag(TimelineItemState.Selected);
            AddFill(
                selected ? RedBrush : NoteBrush,
                new Rect(x, top, barWidth, Math.Max(devicePixel, rowBottom - top)));
        }

        FlushShapes(context);
    }
}
