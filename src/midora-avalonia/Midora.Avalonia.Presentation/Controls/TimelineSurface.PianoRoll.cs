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

    /// <summary>Review-only: disables merged levels so the exact paths can be measured.</summary>
    public static bool PianoRollMergingEnabled { get; set; } = true;

    private readonly TimelineVertexBatchCache<TimelinePianoRollBatchKey> _pianoRollBatchCache = new();
    private readonly TimelineQuadBatchBuilder _pianoRollQuads = new();
    private readonly List<TimelineRenderItem> _pianoRollVisible = [];
    private TimelinePianoRollBlockAggregator _blockAggregator;
    private int _pianoRollVisibleCount;
    private int _pianoRollBlockTicks;

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

        double devicePixel = GetDevicePixelWidth();
        double verticalInset = 2 * devicePixel;
        int visibleCount = Source.CountInRange(
            viewport.StartTick,
            viewport.EndTick,
            viewport.FirstLane,
            viewport.LastLaneExclusive);
        _pianoRollVisibleCount = visibleCount;
        int blockTicks = PianoRollMergingEnabled
            ? TimelinePianoRollLod.SelectBlockTicks(viewport.PixelsPerTick)
            : 0;
        _pianoRollBlockTicks = blockTicks;

        // Merged levels aggregate notes per fixed tick block (at most a few pixels wide on screen);
        // every other level draws exact notes.
        TimelineNoteVertexBatch? batch = null;
        if (blockTicks > 0)
        {
            batch = GetOrBuildPianoRollBlockBatch(viewport, devicePixel, verticalInset, blockTicks);
        }
        else if (visibleCount > GpuNoteBatchThreshold)
        {
            batch = GetOrBuildPianoRollBatch(viewport, devicePixel, verticalInset);
        }

        if (batch is not null)
        {
            Rect clip = new(0, RulerHeight, width, Math.Max(0, height - RulerHeight));
            context.Custom(new TimelineNoteDrawOperation(
                [new TimelineNoteDrawEntry(batch, clip, 0, 0)],
                clip));
        }

        if (blockTicks > 0)
        {
            // Merged blocks cover every note of the visible range.
            FlushShapes(context);
            return;
        }

        _pianoRollItems.Clear();
        Source.QueryInto(
            viewport.StartTick,
            viewport.EndTick,
            viewport.FirstLane,
            viewport.LastLaneExclusive,
            _pianoRollItems);
        _pianoRollVisible.Clear();
        foreach (TimelineRenderItem item in _pianoRollItems)
        {
            if (IsVisiblePianoRollNote(item, viewport, height))
            {
                _pianoRollVisible.Add(item);
            }
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

    /// <summary>
    /// Merged level batch: every (lane, tick block) that contains notes becomes one segment spanning
    /// the block's first start to its maximum end, exactly like the reference summary layer. Because
    /// the block is at most a few pixels wide, gaps inside it are sub-pixel and the result reads as
    /// the same picture; a segment can still extend past its block when a note is sustained, which
    /// is the note's real extent.
    /// </summary>
    private TimelineNoteVertexBatch? GetOrBuildPianoRollBlockBatch(
        TimelineViewport viewport,
        double devicePixel,
        double verticalInset,
        int blockTicks)
    {
        TimelinePianoRollBatchKey key = CreatePianoRollBatchKey(viewport, devicePixel, blockTicks);
        if (_pianoRollBatchCache.TryGet(key, out TimelineNoteVertexBatch cached))
        {
            return cached;
        }

        int laneCount = Math.Max(1, viewport.LastLaneExclusive - viewport.FirstLane);
        long firstBlock = viewport.StartTick / blockTicks;
        int blockCount = (int)Math.Min(
            1_000_000,
            Math.Max(1, viewport.TickLength / blockTicks + 2));
        _blockAggregator.Begin(viewport.FirstLane, laneCount, firstBlock, blockCount, blockTicks);
        Source!.VisitItems(
            viewport.StartTick,
            viewport.EndTick,
            viewport.FirstLane,
            viewport.LastLaneExclusive,
            ref _blockAggregator);

        if (_blockAggregator.TouchedCells.Count == 0)
        {
            return null;
        }

        double rowHeight = Math.Max(devicePixel, viewport.LaneHeight - verticalInset * 2);
        foreach (int cell in _blockAggregator.TouchedCells)
        {
            int lane = _blockAggregator.LaneOf(cell);
            double left = Math.Max(0, viewport.TickToX(_blockAggregator.MinimumStart(cell)));
            double right = Math.Min(viewport.Width, viewport.TickToX(_blockAggregator.MaximumEnd(cell)));
            if (right < left + devicePixel)
            {
                right = left + devicePixel;
            }

            float top = (float)(GetLaneTop(viewport, lane) + verticalInset);
            _pianoRollQuads.Add((float)left, top, (float)right, (float)(top + rowHeight));
        }

        (SKVertices[] batches, int quadCount) = _pianoRollQuads.Complete();
        TimelineNoteVertexBatch built = TimelineNoteVertexBatch.FromQuads(batches, quadCount, key.Color);
        _pianoRollBatchCache.Store(key, built);
        return built;
    }

    private TimelinePianoRollBatchKey CreatePianoRollBatchKey(
        TimelineViewport viewport,
        double devicePixel,
        int blockTicks) =>
        new(
            Source!,
            blockTicks,
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
            WithOpacity(TimelineNoteVertexBatch.ColorOf(NoteBrush), 0.78));

    private TimelineNoteVertexBatch? GetOrBuildPianoRollBatch(
        TimelineViewport viewport,
        double devicePixel,
        double verticalInset)
    {
        TimelinePianoRollBatchKey key = CreatePianoRollBatchKey(viewport, devicePixel, blockTicks: 0);
        uint color = key.Color;
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
/// Identity of one piano roll vertex batch: the render source, the merged tick block (0 for the exact
/// note layer), the bounded range fingerprint, the zoom and lane geometry, the visible range and the
/// paint color. Every input that moves a vertex is part of the key, so a hit can never draw stale
/// data.
/// </summary>
internal readonly record struct TimelinePianoRollBatchKey(
    object Source,
    int BlockTicks,
    ulong RangeFingerprint,
    double PixelsPerTick,
    double LaneHeight,
    double DevicePixel,
    long StartTick,
    long EndTick,
    int FirstLane,
    int LastLaneExclusive,
    uint Color);
