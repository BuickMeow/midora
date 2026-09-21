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

    /// <summary>
    /// Below this many visible notes the exact per-note path is always used. Settable so a review
    /// run can compare level of detail against the exact paths inside one process.
    /// </summary>
    public static int PianoRollLodMinimumNotes { get; set; } = 2_000;

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

        double devicePixel = GetDevicePixelWidth();
        double verticalInset = 2 * devicePixel;
        int visibleCount = Source.CountInRange(
            viewport.StartTick,
            viewport.EndTick,
            viewport.FirstLane,
            viewport.LastLaneExclusive);
        _pianoRollVisibleCount = visibleCount;
        bool lod = visibleCount >= PianoRollLodMinimumNotes
            && AverageNoteWidthPixels(viewport, visibleCount) < 1.0;

        // Dense views merge notes per chunk (one quad per chunk instead of one per note); the
        // envelope is a conservative union, and selected notes are painted over it exactly.
        TimelineNoteVertexBatch? batch = lod
            ? GetOrBuildPianoRollBatch(viewport, devicePixel, verticalInset, lod: true)
            : visibleCount > GpuNoteBatchThreshold
                ? GetOrBuildPianoRollBatch(viewport, devicePixel, verticalInset, lod: false)
                : null;
        if (batch is not null)
        {
            Rect clip = new(0, RulerHeight, width, Math.Max(0, height - RulerHeight));
            context.Custom(new TimelineNoteDrawOperation(
                [new TimelineNoteDrawEntry(batch, clip, 0, 0)],
                clip));
        }

        if (lod)
        {
            if (Source.HasAnySelection || SelectedId is not null)
            {
                DrawPianoRollSelectionOverlay(viewport, height, devicePixel, verticalInset);
            }

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

    /// <summary>Average width of one visible note in device-independent pixels.</summary>
    private static double AverageNoteWidthPixels(TimelineViewport viewport, int visibleNotes)
    {
        if (visibleNotes <= 0)
        {
            return double.PositiveInfinity;
        }

        return viewport.TickLength / (double)visibleNotes * viewport.PixelsPerTick;
    }

    /// <summary>
    /// LOD batch: one quad per (lane, chunk) envelope. A chunk holds 256 notes, so a dense view
    /// costs a few hundred quads instead of hundreds of thousands, and the envelope is the chunk's
    /// [first start, maximum end] so no visible note is dropped.
    /// </summary>
    private TimelineNoteVertexBatch? BuildPianoRollLodBatch(
        in TimelinePianoRollBatchKey key,
        TimelineViewport viewport,
        double devicePixel,
        double verticalInset)
    {
        PianoRollLodSink sink = new()
        {
            Builder = _pianoRollQuads,
            Viewport = viewport,
            DevicePixel = devicePixel,
            VerticalInset = verticalInset,
            RowHeight = Math.Max(devicePixel, viewport.LaneHeight - verticalInset * 2),
            RulerOffset = RulerHeight,
            PitchLanes = UsesPitchLanes
        };
        Source!.VisitChunks(
            viewport.StartTick,
            viewport.EndTick,
            viewport.FirstLane,
            viewport.LastLaneExclusive,
            ref sink);
        if (sink.Count == 0)
        {
            _pianoRollQuads.Complete();
            return null;
        }

        (SKVertices[] batches, int quadCount) = _pianoRollQuads.Complete();
        TimelineNoteVertexBatch built = TimelineNoteVertexBatch.FromQuads(batches, quadCount, key.Color);
        _pianoRollBatchCache.Store(key, built);
        return built;
    }

    /// <summary>Exact shape layer for selected notes, drawn over the LOD batch.</summary>
    private void DrawPianoRollSelectionOverlay(
        TimelineViewport viewport,
        double height,
        double devicePixel,
        double verticalInset)
    {
        double rowHeight = Math.Max(devicePixel, viewport.LaneHeight - verticalInset * 2);
        Source!.VisitInto(
            viewport.StartTick,
            viewport.EndTick,
            viewport.FirstLane,
            viewport.LastLaneExclusive,
            item =>
            {
                if (!IsNoteKind(item.Kind)
                    || !IsVisiblePianoRollNote(item, viewport, height)
                    || (SelectedId != item.Id
                        && !item.State.HasFlag(TimelineItemState.Selected)))
                {
                    return;
                }

                double left = Math.Max(0, viewport.TickToX(item.StartTick));
                double right = Math.Min(viewport.Width, viewport.TickToX(item.EndTick));
                if (right <= left)
                {
                    return;
                }

                Rect bounds = new(
                    left,
                    GetLaneTop(viewport, item.Lane) + verticalInset,
                    right - left,
                    rowHeight);
                AddShape(NoteSelectedBrush, BorderPen, bounds, 0);
                AddShape(null, SelectionPen, bounds, 0);
            });
    }

    private struct PianoRollLodSink : TimelineLaneChunkIndex.IChunkSink
    {
        public TimelineQuadBatchBuilder Builder;
        public TimelineViewport Viewport;
        public double DevicePixel;
        public double VerticalInset;
        public double RowHeight;
        public double RulerOffset;
        public bool PitchLanes;
        public int Count;

        public void Chunk(int lane, long startTick, long endTick, int itemCount)
        {
            if (itemCount == 0)
            {
                return;
            }

            double left = Math.Max(0, Viewport.TickToX(startTick));
            double right = Math.Min(Viewport.Width, Viewport.TickToX(endTick));
            if (right < left + DevicePixel)
            {
                right = left + DevicePixel;
            }

            int row = PitchLanes
                ? Viewport.LastLaneExclusive - 1 - lane
                : lane - Viewport.FirstLane;
            float top = (float)(RulerOffset + row * Viewport.LaneHeight + VerticalInset);
            Builder.Add((float)left, top, (float)right, (float)(top + RowHeight));
            Count++;
        }
    }

    private TimelineNoteVertexBatch? GetOrBuildPianoRollBatch(
        TimelineViewport viewport,
        double devicePixel,
        double verticalInset,
        bool lod)
    {
        uint color = WithOpacity(TimelineNoteVertexBatch.ColorOf(NoteBrush), 0.78);
        var key = new TimelinePianoRollBatchKey(
            Source!,
            lod,
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

        if (lod)
        {
            return BuildPianoRollLodBatch(key, viewport, devicePixel, verticalInset);
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
    bool Lod,
    ulong RangeFingerprint,
    double PixelsPerTick,
    double LaneHeight,
    double DevicePixel,
    long StartTick,
    long EndTick,
    int FirstLane,
    int LastLaneExclusive,
    uint Color);
