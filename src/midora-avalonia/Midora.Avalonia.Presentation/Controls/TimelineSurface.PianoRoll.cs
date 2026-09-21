using Avalonia;
using Avalonia.Media;
using Midora.Avalonia.Presentation.Rendering;
using SkiaSharp;

namespace Midora.Avalonia.Presentation.Controls;

public sealed partial class TimelineSurface
{
    private readonly List<TimelineRenderItem> _pianoRollItems = [];
    private static readonly Pen SelectionPen = new(
        new SolidColorBrush(global::Avalonia.Media.Color.FromRgb(0xF2, 0x55, 0x5A)),
        2);

    private readonly TimelineVertexBatchCache<TimelinePianoRollBatchKey> _pianoRollBatchCache = new();
    private readonly TimelineQuadBatchBuilder _pianoRollQuads = new();
    private readonly List<TimelineRenderItem> _pianoRollVisible = [];
    private int _pianoRollVisibleCount;

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
        _pianoRollVisible.Clear();
        foreach (TimelineRenderItem item in _pianoRollItems)
        {
            if (IsVisiblePianoRollNote(item, viewport, height))
            {
                _pianoRollVisible.Add(item);
            }
        }

        int visibleCount = _pianoRollVisible.Count;
        _pianoRollVisibleCount = visibleCount;
        // Large note sets are drawn from one immutable vertex batch; the batch covers every visible
        // note and the selected notes are painted over it by the exact shape pass.
        TimelineNoteVertexBatch? batch = visibleCount > GpuNoteBatchThreshold
            ? GetOrBuildPianoRollBatch(viewport, devicePixel, verticalInset)
            : null;
        if (batch is not null)
        {
            Rect clip = new(0, RulerHeight, width, Math.Max(0, height - RulerHeight));
            context.Custom(new TimelineNoteDrawOperation(
                [new TimelineNoteDrawEntry(batch, clip, 0, 0)],
                clip));
        }

        foreach (TimelineRenderItem item in _pianoRollVisible)
        {
            bool selected = SelectedId == item.Id || item.State.HasFlag(TimelineItemState.Selected);
            if (batch is not null && !selected)
            {
                continue;
            }

            double laneTop = GetLaneTop(viewport, item.Lane);
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
            if (batch is not null)
            {
                AddShape(NoteSelectedBrush, BorderPen, bounds, 0);
                AddShape(null, SelectionPen, bounds, 0);
            }
            else
            {
                AddShape(selected ? NoteSelectedBrush : NoteBrush, BorderPen, bounds, 0, 0.78);
                if (selected)
                {
                    AddShape(null, SelectionPen, bounds, 0);
                }
            }
        }

        FlushShapes(context);
    }

    /// <summary>
    /// Pixel visibility only: the tick and lane ranges are already applied by the source query, so
    /// this only rejects lanes that fall outside the painted area.
    /// </summary>
    private bool IsVisiblePianoRollNote(
        TimelineRenderItem item,
        TimelineViewport viewport,
        double height)
    {
        double laneTop = GetLaneTop(viewport, item.Lane);
        return laneTop < height && laneTop + viewport.LaneHeight > RulerHeight;
    }

    private TimelineNoteVertexBatch? GetOrBuildPianoRollBatch(
        TimelineViewport viewport,
        double devicePixel,
        double verticalInset)
    {
        uint color = WithOpacity(TimelineNoteVertexBatch.ColorOf(NoteBrush), 0.78);
        var key = new TimelinePianoRollBatchKey(
            Source!,
            Source!.GetRangeFingerprint(
                viewport.StartTick,
                viewport.EndTick,
                viewport.FirstLane,
                viewport.LastLaneExclusive),
            viewport.PixelsPerTick,
            viewport.LaneHeight,
            devicePixel,
            viewport.StartTick,
            viewport.EndTick,
            viewport.FirstLane,
            viewport.LastLaneExclusive,
            color);
        if (_pianoRollBatchCache.TryGet(key, out TimelineNoteVertexBatch cached))
        {
            return cached;
        }

        double rowHeight = Math.Max(devicePixel, viewport.LaneHeight - verticalInset * 2);
        int count = 0;
        foreach (TimelineRenderItem item in _pianoRollVisible)
        {
            if (!IsNoteKind(item.Kind))
            {
                continue;
            }

            double left = Math.Max(0, viewport.TickToX(item.StartTick));
            double right = Math.Min(viewport.Width, viewport.TickToX(item.EndTick));
            if (right <= left)
            {
                continue;
            }

            float top = (float)(GetLaneTop(viewport, item.Lane) + verticalInset);
            _pianoRollQuads.Add((float)left, top, (float)right, (float)(top + rowHeight));
            count++;
        }

        if (count == 0)
        {
            _pianoRollQuads.Complete();
            return null;
        }

        (SKVertices[] batches, int quadCount) = _pianoRollQuads.Complete();
        TimelineNoteVertexBatch built = TimelineNoteVertexBatch.FromQuads(batches, quadCount, color);
        _pianoRollBatchCache.Store(key, built);
        return built;
    }

    /// <summary>Applies the shape path's fill opacity to a batch paint color.</summary>
    private static uint WithOpacity(uint argb, double opacity)
    {
        uint alpha = (uint)Math.Clamp(
            (int)Math.Round(((argb >> 24) & 0xFF) * opacity),
            0,
            255);
        return (argb & 0x00FFFFFFu) | (alpha << 24);
    }

    /// <summary>Piano-roll lanes are absolute MIDI pitches; the viewport shows [FirstLane, FirstLane + visible).</summary>
    private static long GetPitchForLane(int lane) =>
        lane is >= 0 and <= 127 ? lane : -1;
}

/// <summary>
/// Identity of one piano roll vertex batch: the render source, the bounded range fingerprint, the
/// zoom and lane geometry, the visible range and the paint color. Every input that moves a vertex
/// is part of the key, so a hit can never draw stale data.
/// </summary>
internal readonly record struct TimelinePianoRollBatchKey(
    object Source,
    ulong RangeFingerprint,
    double PixelsPerTick,
    double LaneHeight,
    double DevicePixel,
    long StartTick,
    long EndTick,
    int FirstLane,
    int LastLaneExclusive,
    uint Color);
