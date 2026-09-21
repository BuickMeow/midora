using Avalonia;
using Avalonia.Media;
using Midora.Avalonia.Presentation.Rendering;

namespace Midora.Avalonia.Presentation.Controls;

public sealed partial class TimelineSurface
{
    private readonly List<TimelineRenderItem> _pianoRollItems = [];
    private static readonly Pen SelectionPen = new(
        new SolidColorBrush(global::Avalonia.Media.Color.FromRgb(0xF2, 0x55, 0x5A)),
        2);

    private void DrawPianoRollSurface(
        DrawingContext context,
        TimelineViewport viewport,
        double width,
        double height)
    {
        if (Source is null)
        {
            return;
        }

        _pianoRollItems.Clear();
        Source.QueryInto(
            viewport.StartTick,
            viewport.EndTick,
            viewport.FirstLane,
            viewport.LastLaneExclusive,
            _pianoRollItems);
        double devicePixel = GetDevicePixelWidth();
        double verticalInset = 2 * devicePixel;
        foreach (TimelineRenderItem item in _pianoRollItems)
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

            double left = Math.Max(0, viewport.TickToX(item.StartTick));
            double right = Math.Min(viewport.Width, viewport.TickToX(item.EndTick));
            if (right <= left)
            {
                continue;
            }

            Rect bounds = new(
                left,
                laneTop + verticalInset,
                right - left,
                Math.Max(devicePixel, viewport.LaneHeight - verticalInset * 2));
            bool selected = SelectedId == item.Id || item.State.HasFlag(TimelineItemState.Selected);
            AddShape(selected ? NoteSelectedBrush : NoteBrush, BorderPen, bounds, 0, 0.78);
            if (selected)
            {
                AddShape(null, SelectionPen, bounds, 0);
            }
        }

        FlushShapes(context);
    }


    /// <summary>Piano-roll lanes are absolute MIDI pitches; the viewport shows [FirstLane, FirstLane + visible).</summary>
    private static long GetPitchForLane(int lane) =>
        lane is >= 0 and <= 127 ? lane : -1;
}
