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
    private int _pianoRollVisibleCount;
    private int _pianoRollMergeFactor = 1;

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
        int mergeFactor = PianoRollMergingEnabled
            ? TimelinePianoRollLod.SelectMergeFactor(AverageNoteWidthPixels(viewport, visibleCount))
            : 1;
        _pianoRollMergeFactor = mergeFactor;

        // Merged levels draw one quad per grouped envelope; every other level draws exact notes.
        TimelineNoteVertexBatch? batch = null;
        if (mergeFactor > 1)
        {
            TimelinePianoRollBatchKey envelopeKey =
                CreatePianoRollBatchKey(viewport, devicePixel, mergeFactor);
            batch = _pianoRollBatchCache.TryGet(envelopeKey, out TimelineNoteVertexBatch cached)
                ? cached
                : BuildPianoRollEnvelopeBatch(
                    envelopeKey,
                    viewport,
                    devicePixel,
                    verticalInset,
                    mergeFactor);
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

        if (mergeFactor > 1)
        {
            // Merged levels cover every note, so there is no separate selection or hover layer.
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
    /// Merged level batch: one quad per (lane, mergeFactor notes) envelope. The envelope spans the
    /// grouped notes' first start to their maximum end, which covers every one of them, and lanes
    /// are pitches, so the vertical extent stays exact.
    /// </summary>
    private TimelineNoteVertexBatch? BuildPianoRollEnvelopeBatch(
        in TimelinePianoRollBatchKey key,
        TimelineViewport viewport,
        double devicePixel,
        double verticalInset,
        int mergeFactor)
    {
        PianoRollEnvelopeSink sink = new()
        {
            Builder = _pianoRollQuads,
            Viewport = viewport,
            DevicePixel = devicePixel,
            VerticalInset = verticalInset,
            RowHeight = Math.Max(devicePixel, viewport.LaneHeight - verticalInset * 2),
            RulerOffset = RulerHeight,
            PitchLanes = UsesPitchLanes
        };
        Source!.VisitMergedEnvelopes(
            mergeFactor,
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

    private struct PianoRollEnvelopeSink : TimelineLaneChunkIndex.IChunkSink
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

    private TimelinePianoRollBatchKey CreatePianoRollBatchKey(
        TimelineViewport viewport,
        double devicePixel,
        int mergeFactor) =>
        new(
            Source!,
            mergeFactor,
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
        TimelinePianoRollBatchKey key = CreatePianoRollBatchKey(viewport, devicePixel, mergeFactor: 1);
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
/// Identity of one piano roll vertex batch: the render source, the level of detail (items per merged
/// envelope, 1 for exact notes), the bounded range fingerprint, the zoom and lane geometry, the
/// visible range and the paint color. Every input that moves a vertex is part of the key, so a hit
/// can never draw stale data.
/// </summary>
internal readonly record struct TimelinePianoRollBatchKey(
    object Source,
    int MergeFactor,
    ulong RangeFingerprint,
    double PixelsPerTick,
    double LaneHeight,
    double DevicePixel,
    long StartTick,
    long EndTick,
    int FirstLane,
    int LastLaneExclusive,
    uint Color);
