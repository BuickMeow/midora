using Avalonia;
using Avalonia.Media;
using Midora.Avalonia.Presentation.Rendering;

namespace Midora.Avalonia.Presentation.Controls;

public sealed partial class TimelineSurface
{
    private readonly List<TimelineRenderItem> _conductorViewItems = [];
    private static Pen? _conductorMarkerPen;

    private static Pen ConductorMarkerPen => _conductorMarkerPen ??= new Pen(TextTertiaryBrush, 1);

    private void DrawConductorSurface(
        DrawingContext context,
        TimelineViewport viewport,
        double width,
        double height)
    {
        if (Source is null)
        {
            return;
        }

        _conductorViewItems.Clear();
        Source.QueryInto(
            viewport.StartTick,
            viewport.EndTick,
            viewport.FirstLane,
            viewport.LastLaneExclusive,
            _conductorViewItems);
        _conductorViewItems.Sort(static (left, right) =>
        {
            int value = left.StartTick.CompareTo(right.StartTick);
            return value != 0 ? value : left.Id.CompareTo(right.Id);
        });

        int maximumStack = Math.Max(0, (int)((viewport.Height - 16) / 15));
        double previousX = double.NegativeInfinity;
        int stack = 0;
        foreach (TimelineRenderItem item in _conductorViewItems)
        {
            if (!IsConductorViewKind(item.Kind)
                || item.Lane < viewport.FirstLane
                || item.Lane >= viewport.LastLaneExclusive)
            {
                continue;
            }

            double x = viewport.TickToX(item.StartTick);
            if (x < 0 || x > viewport.Width)
            {
                continue;
            }

            stack = x - previousX <= 8 ? Math.Min(stack + 1, maximumStack) : 0;
            previousX = x;
            double labelY = RulerHeight + 4 + stack * 15;
            double centerY = labelY + 6;
            if (IsMarkerKind(item.Kind))
            {
                context.DrawLine(
                    ConductorMarkerPen,
                    new Point(x, RulerHeight),
                    new Point(x, RulerHeight + 12));
            }

            bool selected = SelectedId == item.Id || item.State.HasFlag(TimelineItemState.Selected);
            context.DrawEllipse(RedBrush, selected ? SelectionPen : null, new Point(x, centerY), 2.5, 2.5);
            string label = ConductorRenderItemSource.GetDisplayLabel(item);
            if (string.IsNullOrEmpty(label))
            {
                continue;
            }

            DrawLabel(context, label, x + 5, labelY, Math.Max(0, width - x - 7));
        }
    }

    private static bool IsConductorViewKind(TimelineItemKind kind) => kind is
        TimelineItemKind.ConductorEvent
        or TimelineItemKind.TempoPoint
        or TimelineItemKind.Marker
        or TimelineItemKind.ProjectEndMarker
        or TimelineItemKind.LifecycleBoundary;

    private static bool IsMarkerKind(TimelineItemKind kind) => kind is
        TimelineItemKind.Marker
        or TimelineItemKind.ProjectEndMarker
        or TimelineItemKind.LifecycleBoundary;
}
