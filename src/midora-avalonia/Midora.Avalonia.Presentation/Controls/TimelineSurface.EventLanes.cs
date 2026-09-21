using Avalonia;
using Avalonia.Media;
using Midora.Avalonia.Presentation.Rendering;

namespace Midora.Avalonia.Presentation.Controls;

public sealed partial class TimelineSurface
{
    private readonly List<TimelineRenderItem> _eventLaneItems = [];
    private Pen? _eventLanePen;
    private double _eventLanePenDevicePixel;

    private void DrawEventLaneSurface(
        DrawingContext context,
        TimelineViewport viewport,
        double width,
        double height)
    {
        if (Source is null)
        {
            return;
        }

        _eventLaneItems.Clear();
        _visibleItems.Clear();
        Source.QueryInto(
            viewport.StartTick,
            viewport.EndTick,
            viewport.FirstLane,
            viewport.LastLaneExclusive,
            _visibleItems);
        foreach (TimelineRenderItem item in _visibleItems)
        {
            if (IsNoteKind(item.Kind)
                || item.Lane < viewport.FirstLane
                || item.Lane >= viewport.LastLaneExclusive
                || item.StartTick < viewport.StartTick
                || item.StartTick >= viewport.EndTick)
            {
                continue;
            }

            _eventLaneItems.Add(item);
        }

        _eventLaneItems.Sort(static (left, right) =>
        {
            int value = left.Lane.CompareTo(right.Lane);
            if (value != 0) return value;
            value = left.StartTick.CompareTo(right.StartTick);
            if (value != 0) return value;
            value = left.Kind.CompareTo(right.Kind);
            return value != 0 ? value : left.Id.CompareTo(right.Id);
        });

        int index = 0;
        while (index < _eventLaneItems.Count)
        {
            int lane = _eventLaneItems[index].Lane;
            int laneEnd = index + 1;
            while (laneEnd < _eventLaneItems.Count && _eventLaneItems[laneEnd].Lane == lane)
            {
                laneEnd++;
            }

            DrawEventLaneGroup(context, viewport, height, index, laneEnd);
            index = laneEnd;
        }
    }

    private void DrawEventLaneGroup(
        DrawingContext context,
        TimelineViewport viewport,
        double height,
        int startIndex,
        int endIndex)
    {
        double laneTop = GetLaneTop(viewport, _eventLaneItems[startIndex].Lane);
        if (laneTop >= height || laneTop + viewport.LaneHeight <= RulerHeight)
        {
            return;
        }

        Pen stepPen = GetEventLanePen(GetDevicePixelWidth());
        double previousX = 0;
        double previousY = 0;
        for (int index = startIndex; index < endIndex; index++)
        {
            TimelineRenderItem item = _eventLaneItems[index];
            double x = viewport.TickToX(item.StartTick);
            double y = ResolveEventLaneY(viewport, laneTop, item.Value);
            if (index > startIndex)
            {
                AddLine(stepPen, new Point(previousX, previousY), new Point(x, previousY));
                AddLine(stepPen, new Point(x, previousY), new Point(x, y));
            }

            bool selected = SelectedId == item.Id || item.State.HasFlag(TimelineItemState.Selected);
            AddFill(selected ? RedBrush : EventBrush, new Rect(x - 1.5, y - 1.5, 3, 3));
            previousX = x;
            previousY = y;
        }
    }

    private Pen GetEventLanePen(double devicePixel)
    {
        if (_eventLanePen is null || _eventLanePenDevicePixel != devicePixel)
        {
            _eventLanePen = new Pen(EventBrush, devicePixel);
            _eventLanePenDevicePixel = devicePixel;
        }

        return _eventLanePen;
    }

    private double ResolveEventLaneY(TimelineViewport viewport, double laneTop, double value)
    {
        double minimum = ValueMinimum;
        double span = ValueMaximum - minimum;
        double normalized = double.IsFinite(value) && double.IsFinite(minimum) && double.IsFinite(span)
            ? Math.Clamp((value - minimum) / Math.Max(double.Epsilon, span), 0, 1)
            : 0;
        if (!double.IsFinite(normalized))
        {
            normalized = 0;
        }

        double rowBottom = laneTop + viewport.LaneHeight;
        return rowBottom - normalized * (viewport.LaneHeight - 6) - 3;
    }
}
