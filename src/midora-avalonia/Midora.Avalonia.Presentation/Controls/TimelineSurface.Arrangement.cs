using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Midora.Avalonia.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Avalonia.Presentation.Controls;

public sealed partial class TimelineSurface
{
    private void DrawLaneBackgrounds(
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

            if ((index & 1) != 0)
            {
                double laneBottom = Math.Min(height, laneTop + viewport.LaneHeight);
                context.FillRectangle(
                    LaneAlternateBrush,
                    new Rect(0, laneTop, width, Math.Max(0, laneBottom - laneTop)),
                    1f);
            }

            context.DrawLine(
                BorderPen,
                new Point(0, Math.Round(laneTop) + 0.5),
                new Point(width, Math.Round(laneTop) + 0.5));

            if (IsLaneFiltered(viewport.FirstLane + index))
            {
                double laneBottom = Math.Min(height, laneTop + viewport.LaneHeight);
                context.FillRectangle(
                    LaneDimBrush,
                    new Rect(0, laneTop, width, Math.Max(0, laneBottom - laneTop)),
                    1f);
            }
        }
    }

    private void DrawGrid(
        DrawingContext context,
        TimelineViewport viewport,
        double width,
        double height)
    {
        long minimumTickSpacing = Math.Max(
            1,
            (long)Math.Ceiling(GridMinimumPixelSpacing / viewport.PixelsPerTick));
        ProjectTimeSignatureMap map = GetTimeSignatureMap();
        _gridLines.Clear();
        TimelineGridPresentation.BuildArrangementBarGridLines(
            viewport.StartTick,
            viewport.EndTick,
            map,
            _gridLines,
            minimumTickSpacing);
        double nextLabelX = 0;
        foreach (TimelineGridLine line in _gridLines)
        {
            double x = Math.Round(viewport.TickToX(line.Tick)) + 0.5;
            if (x < 0 || x > width)
            {
                continue;
            }

            context.DrawLine(GridPen, new Point(x, RulerHeight), new Point(x, height));
            context.DrawLine(
                BorderPen,
                new Point(x, Math.Max(0, RulerHeight - 4)),
                new Point(x, RulerHeight));
            if (line.Kind != TimelineGridLineKind.Bar || x < nextLabelX)
            {
                continue;
            }

            string label = map.GetBarBounds(line.Tick).Bar.ToString(CultureInfo.InvariantCulture);
            nextLabelX = x + DrawLabel(context, label, x + 2, RulerHeight - 14, width - x - 4) + 8;
        }
    }

    private void DrawTrackNames(DrawingContext context, TimelineViewport viewport, double width)
    {
        if (TrackNames is not { } trackNames)
        {
            return;
        }

        for (int index = 0; index < viewport.LaneCount; index++)
        {
            double laneTop = RulerHeight + index * viewport.LaneHeight;
            if (laneTop >= Bounds.Height)
            {
                break;
            }

            string? name = trackNames(viewport.FirstLane + index);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            FormattedText formatted = new(
                name,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                SurfaceTypeface,
                11,
                TextTertiaryBrush);
            try
            {
                formatted.MaxTextWidth = Math.Max(0, width - 12);
                context.DrawText(
                    formatted,
                    new Point(
                        6,
                        laneTop + Math.Max(0, (viewport.LaneHeight - formatted.Height) / 2)));
            }
            finally
            {
                (formatted as IDisposable)?.Dispose();
            }
        }
    }

    private void DrawItems(DrawingContext context, TimelineViewport viewport, double height)
    {
        _visibleItems.Clear();
        Source!.QueryInto(
            viewport.StartTick,
            viewport.EndTick,
            viewport.FirstLane,
            viewport.LastLaneExclusive,
            _visibleItems);
        foreach (TimelineRenderItem item in _visibleItems)
        {
            if (item.Lane < viewport.FirstLane
                || item.Lane >= viewport.LastLaneExclusive
                || item.EndTick <= viewport.StartTick
                || item.StartTick >= viewport.EndTick)
            {
                continue;
            }

            DrawRenderItem(context, viewport, item, height);
        }
    }

    private void DrawRenderItem(
        DrawingContext context,
        TimelineViewport viewport,
        in TimelineRenderItem item,
        double height)
    {
        if (IsLaneFiltered(item.Lane))
        {
            return;
        }

        double laneTop = RulerHeight + (item.Lane - viewport.FirstLane) * viewport.LaneHeight;
        if (laneTop >= height || laneTop + viewport.LaneHeight <= RulerHeight)
        {
            return;
        }

        double left = Math.Max(-1, viewport.TickToX(item.StartTick));
        double right = Math.Min(viewport.Width + 1, viewport.TickToX(item.EndTick));
        bool selected = SelectedId == item.Id || item.State.HasFlag(TimelineItemState.Selected);
        bool hovered = _hoverItemId == item.Id;
        switch (item.Kind)
        {
            case TimelineItemKind.Segment:
                DrawSegment(context, viewport, item, laneTop, left, right, selected, hovered);
                break;
            case TimelineItemKind.ConductorEvent:
            case TimelineItemKind.TempoPoint:
            case TimelineItemKind.Marker:
            case TimelineItemKind.ProjectEndMarker:
            case TimelineItemKind.LifecycleBoundary:
                DrawConductorPoint(context, viewport, item, laneTop, selected, hovered);
                break;
            default:
                DrawThinItem(context, viewport, item, laneTop, left, right, selected, hovered);
                break;
        }
    }

    private void DrawSegment(
        DrawingContext context,
        TimelineViewport viewport,
        in TimelineRenderItem item,
        double laneTop,
        double left,
        double right,
        bool selected,
        bool hovered)
    {
        Rect bounds = new(
            left + 1,
            laneTop + 1,
            Math.Max(1, right - left - 2),
            Math.Max(1, viewport.LaneHeight - 2));
        context.DrawRectangle(
            SegmentFillFor(item.AccentColor, selected),
            BorderPen,
            bounds,
            2,
            2);
        if (PreviewProvider is { } provider && bounds.Width >= 3 && bounds.Height >= 3)
        {
            ITimelineSegmentPreviewSource? preview = provider(item);
            if (preview is not null)
            {
                DrawSegmentPreview(context, bounds, preview);
            }
        }

        if (selected)
        {
            context.DrawRectangle(null, SelectedOutlinePen, bounds, 2, 2);
        }
        else if (hovered)
        {
            context.DrawRectangle(null, HoverOutlinePen, bounds, 2, 2);
        }
    }

    private void DrawSegmentPreview(
        DrawingContext context,
        Rect bounds,
        ITimelineSegmentPreviewSource preview)
    {
        double devicePixel = GetDevicePixelWidth();
        using (context.PushClip(bounds))
        {
            if (preview.HasNoteContent)
            {
                _previewNotes.Clear();
                preview.QueryNotes(0, 1, _previewNotes);
                foreach (TimelineSegmentPreviewNote note in _previewNotes)
                {
                    double x = SnapToDevicePixel(
                        bounds.X + Math.Clamp(note.NormalizedStart, 0, 1) * bounds.Width,
                        devicePixel);
                    double pitch = Math.Clamp(note.Pitch, 0, 127);
                    double top = bounds.Y + (127 - pitch) / 127d * bounds.Height;
                    context.FillRectangle(
                        NoteBrush,
                        new Rect(x, top, devicePixel, Math.Max(devicePixel, bounds.Bottom - top)),
                        1f);
                }
            }

            if (preview.HasEventContent)
            {
                _previewEvents.Clear();
                preview.QueryEvents(0, 1, _previewEvents);
                foreach (TimelineSegmentPreviewEvent value in _previewEvents)
                {
                    double x = SnapToDevicePixel(
                        bounds.X + Math.Clamp(value.NormalizedTick, 0, 1) * bounds.Width,
                        devicePixel);
                    double top = bounds.Y
                        + (1 - Math.Clamp(value.NormalizedValue, 0, 1)) * bounds.Height;
                    context.FillRectangle(
                        EventBrush,
                        new Rect(x, top, devicePixel, Math.Max(devicePixel, bounds.Bottom - top)),
                        1f);
                }
            }
        }
    }

    private static void DrawConductorPoint(
        DrawingContext context,
        TimelineViewport viewport,
        in TimelineRenderItem item,
        double laneTop,
        bool selected,
        bool hovered)
    {
        double x = viewport.TickToX(item.StartTick);
        if (x < -ConductorPointRadius || x > viewport.Width + ConductorPointRadius)
        {
            return;
        }

        double centerY = laneTop + viewport.LaneHeight / 2;
        context.DrawEllipse(
            RedBrush,
            selected || hovered ? SelectedOutlinePen : null,
            new Point(x, centerY),
            ConductorPointRadius,
            ConductorPointRadius);
    }

    private void DrawThinItem(
        DrawingContext context,
        TimelineViewport viewport,
        in TimelineRenderItem item,
        double laneTop,
        double left,
        double right,
        bool selected,
        bool hovered)
    {
        double devicePixel = GetDevicePixelWidth();
        if (IsNoteKind(item.Kind))
        {
            double lineLeft = Math.Max(0, left);
            double lineRight = Math.Min(viewport.Width, right);
            if (lineRight <= lineLeft)
            {
                return;
            }

            double y = laneTop + viewport.LaneHeight / 2;
            context.FillRectangle(
                selected ? RedBrush : NoteBrush,
                new Rect(lineLeft, y, lineRight - lineLeft, devicePixel),
                1f);
            if (selected || hovered)
            {
                context.DrawRectangle(
                    null,
                    SelectedOutlinePen,
                    new Rect(lineLeft, y - 1, lineRight - lineLeft, devicePixel + 2));
            }

            return;
        }

        double normalized = double.IsFinite(item.Value) ? Math.Clamp(item.Value, 0, 1) : 0.5;
        double configuredY = laneTop + (1 - normalized) * viewport.LaneHeight;
        double minimumY = laneTop + devicePixel;
        double maximumY = laneTop + viewport.LaneHeight - devicePixel;
        double top = maximumY < minimumY
            ? laneTop + viewport.LaneHeight / 2
            : Math.Clamp(configuredY, minimumY, maximumY);
        double x = SnapToDevicePixel(Math.Clamp(left, 0, viewport.Width), devicePixel);
        context.FillRectangle(
            selected ? RedBrush : EventBrush,
            new Rect(
                x,
                top,
                devicePixel,
                Math.Max(devicePixel, laneTop + viewport.LaneHeight - top)),
            1f);
        if (selected || hovered)
        {
            context.DrawEllipse(null, SelectedOutlinePen, new Point(x, top), 4, 4);
        }
    }

    private static bool IsNoteKind(TimelineItemKind kind) => kind is
        TimelineItemKind.LogicalNote
        or TimelineItemKind.DirectMidiNote
        or TimelineItemKind.TemplateNote
        or TimelineItemKind.Velocity;

    private void DrawEditCursor(DrawingContext context, TimelineViewport viewport, double height)
    {
        if (0 < viewport.StartTick || 0 >= viewport.EndTick)
        {
            return;
        }

        double x = Math.Round(viewport.TickToX(0)) + 0.5;
        context.DrawLine(CursorPen, new Point(x, 0), new Point(x, height));
    }

    private void DrawMarquee(DrawingContext context, double width, double height)
    {
        if (!_isMarqueeVisible || height <= RulerHeight)
        {
            return;
        }

        Rect content = new(0, RulerHeight, width, height - RulerHeight);
        Rect rectangle = _marqueeRect.Intersect(content);
        if (rectangle.Width <= 0 || rectangle.Height <= 0)
        {
            return;
        }

        context.FillRectangle(MarqueeFillBrush, rectangle, 1f);
        context.DrawRectangle(null, MarqueePen, rectangle);
    }

    private double DrawLabel(
        DrawingContext context,
        string text,
        double x,
        double y,
        double maximumWidth)
    {
        if (maximumWidth <= 0)
        {
            return 0;
        }

        FormattedText formatted = new(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            SurfaceTypeface,
            10,
            TextTertiaryBrush);
        try
        {
            formatted.MaxTextWidth = maximumWidth;
            context.DrawText(formatted, new Point(x, y));
            return formatted.Width;
        }
        finally
        {
            (formatted as IDisposable)?.Dispose();
        }
    }
}
