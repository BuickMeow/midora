using Avalonia;
using Avalonia.Media;
using Midora.Avalonia.Presentation.Rendering;

namespace Midora.Avalonia.Presentation.Controls;

public sealed partial class TimelineSurface
{
    private readonly List<TimelineRenderItem> _pianoRollItems = [];
    private static Pen? _noteSelectedPen;

    private static Pen NoteSelectedPen => _noteSelectedPen ??= new Pen(RedBrush, 1.5);

    private void DrawPianoRollSurface(
        DrawingContext context,
        TimelineViewport viewport,
        double width,
        double height)
    {
        DrawLaneBackgrounds(context, viewport, width, height);
        DrawPitchLabels(context, viewport, width, height);
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
            context.DrawRectangle(NoteBrush, selected ? NoteSelectedPen : null, bounds, 2, 2);
        }
    }

    private void DrawPitchLabels(
        DrawingContext context,
        TimelineViewport viewport,
        double width,
        double height)
    {
        for (int index = 0; index < viewport.LaneCount; index++)
        {
            double laneTop = RulerHeight + index * viewport.LaneHeight;
            if (laneTop >= height)
            {
                break;
            }

            long pitch = LaneAtRow(viewport, index);
            if (pitch is < 0 or > 127)
            {
                continue;
            }

            string? label = PianoKeyPresentation.GetOctaveCLabel((int)pitch);
            if (label is null)
            {
                continue;
            }

            DrawLabel(
                context,
                label,
                4,
                laneTop + Math.Max(0, (viewport.LaneHeight - 12) / 2),
                Math.Max(0, width - 8));
        }
    }

    /// <summary>Piano-roll lanes are absolute MIDI pitches; the viewport shows [FirstLane, FirstLane + visible).</summary>
    private static long GetPitchForLane(int lane) =>
        lane is >= 0 and <= 127 ? lane : -1;
}
