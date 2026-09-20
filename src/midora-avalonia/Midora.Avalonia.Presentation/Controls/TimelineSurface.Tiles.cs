using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Midora.Avalonia.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Avalonia.Presentation.Controls;

/// <summary>
/// WPF-parity Segment preview tiles. The approved WPF baseline rasterizes each
/// Segment's note/event preview once into fixed-width tiles (96 px per quarter note at the
/// fixed preview LOD) and then blits images; the live per-note fallback only covers tiles
/// that are not ready yet. This keeps the Arrangement note noise identical to WPF and turns
/// the steady-state frame into a handful of textured blits.
/// </summary>
public sealed partial class TimelineSurface
{
    private const int MaximumSegmentPreviewCacheEntries = 64;
    private const int MaximumSegmentPreviewTileEntries = 768;

    private static readonly ConcurrentDictionary<SegmentPreviewTileKey, SegmentPreviewTile>
        SegmentPreviewTiles = new();
    private static readonly ConcurrentQueue<SegmentPreviewTileKey> SegmentPreviewTileOrder = new();

    private readonly Dictionary<MidoraId, SegmentPreviewCacheEntry> _segmentPreviewCache = [];
    private readonly HashSet<SegmentPreviewTileKey> _segmentPreviewRequests = [];
    private readonly CancellationTokenSource _rasterCancellation = new();
    private readonly long _rasterConsumerId = TimelineRasterCache.CreateConsumerId();

    private readonly record struct SegmentPreviewTileKey(
        long SegmentId,
        ulong NoteFingerprint,
        ulong EventFingerprint,
        int Lod,
        long Tile,
        uint NoteColor,
        uint EventColor);

    private readonly record struct SegmentPreviewTile(WriteableBitmap Bitmap);

    private sealed record SegmentPreviewCacheEntry(
        ulong NoteFingerprint,
        ulong EventFingerprint,
        TimelineSegmentPreview Preview);

    /// <summary>Cancels outstanding tile requests when the surface leaves the tree.</summary>
    private void CancelRasterRequests()
    {
        if (!_rasterCancellation.IsCancellationRequested)
        {
            _rasterCancellation.Cancel();
        }
    }

    /// <summary>
    /// Draws the fixed-width preview tiles for one Segment. Returns true when every visible
    /// tile was available; false means the caller should keep the live fallback for this frame.
    /// </summary>
    private bool DrawSegmentPreviewTiles(
        DrawingContext context,
        Rect visibleBounds,
        Rect fullSegmentBounds,
        TimelineRenderItem item,
        ITimelineSegmentPreviewSource source,
        long segmentLengthTicks,
        int ticksPerQuarterNote,
        double pixelsPerTick,
        global::Avalonia.Media.Color noteColor,
        global::Avalonia.Media.Color eventColor)
    {
        if (!source.HasNoteContent && !source.HasEventContent)
        {
            return true;
        }

        if (!(visibleBounds.Width > 0)
            || !(fullSegmentBounds.Width > 0)
            || !(fullSegmentBounds.Height > 0)
            || !(pixelsPerTick > 0)
            || segmentLengthTicks <= 0
            || ticksPerQuarterNote <= 0)
        {
            return true;
        }

        int lod = TimelineSegmentPreviewRasterizer.SelectDisplayLod(
            pixelsPerTick,
            ticksPerQuarterNote);
        long contentWidth = TimelineSegmentPreviewRasterizer.GetFixedPreviewContentWidth(
            segmentLengthTicks,
            ticksPerQuarterNote,
            lod);
        const int tileSize = TimelineSegmentPreviewRasterizer.FixedPreviewTileSize;
        long tileCount = 1 + ((contentWidth - 1) / tileSize);
        double normalizedVisibleLeft = Math.Clamp(
            (visibleBounds.Left - fullSegmentBounds.Left) / fullSegmentBounds.Width,
            0,
            1);
        double normalizedVisibleRight = Math.Clamp(
            (visibleBounds.Right - fullSegmentBounds.Left) / fullSegmentBounds.Width,
            0,
            1);
        if (normalizedVisibleRight <= normalizedVisibleLeft)
        {
            return true;
        }

        long firstTile = Math.Max(
            0,
            (long)Math.Floor(normalizedVisibleLeft * contentWidth / tileSize));
        long lastTile = Math.Min(
            tileCount - 1,
            (long)Math.Floor(Math.BitDecrement(normalizedVisibleRight * contentWidth) / tileSize));
        if (lastTile < firstTile)
        {
            return true;
        }

        TimelineSegmentPreview preview = GetSegmentPreview(item, source);
        uint noteArgb = ToArgb(noteColor);
        uint eventArgb = ToArgb(eventColor);
        double scaling = Math.Max(0.1, GetRenderScaling());
        DpiScale dpi = new(scaling, scaling);
        bool complete = true;
        using (context.PushClip(new RoundedRect(visibleBounds)))
        {
            for (long tile = firstTile; tile <= lastTile; tile++)
            {
                SegmentPreviewTileKey key = new(
                    item.Id.Value,
                    source.NoteContentFingerprint,
                    source.EventContentFingerprint,
                    lod,
                    tile,
                    noteArgb,
                    eventArgb);
                if (SegmentPreviewTiles.TryGetValue(key, out SegmentPreviewTile cached))
                {
                    long tileLeft = checked(tile * tileSize);
                    Rect destination = TimelineRasterPlacement.GetSegmentPreviewTileDestination(
                        fullSegmentBounds,
                        contentWidth,
                        tileLeft,
                        cached.Bitmap.PixelSize.Width,
                        dpi);
                    context.DrawImage(cached.Bitmap, destination);
                    continue;
                }

                complete = false;
                if (_segmentPreviewRequests.Add(key))
                {
                    RequestSegmentPreviewTile(
                        key,
                        preview,
                        contentWidth,
                        segmentLengthTicks,
                        ticksPerQuarterNote,
                        lod);
                }
            }
        }

        return complete;
    }

    private TimelineSegmentPreview GetSegmentPreview(
        TimelineRenderItem item,
        ITimelineSegmentPreviewSource source)
    {
        if (_segmentPreviewCache.TryGetValue(item.Id, out SegmentPreviewCacheEntry? entry)
            && entry.NoteFingerprint == source.NoteContentFingerprint
            && entry.EventFingerprint == source.EventContentFingerprint)
        {
            return entry.Preview;
        }

        List<TimelineSegmentPreviewNote> notes = [];
        if (source.HasNoteContent)
        {
            source.QueryNotes(0, 1, notes);
        }

        List<TimelineSegmentPreviewEvent> events = [];
        if (source.HasEventContent)
        {
            source.QueryEvents(0, 1, events);
        }

        TimelineSegmentPreview preview = new(item.Id, notes, events);
        if (_segmentPreviewCache.Count >= MaximumSegmentPreviewCacheEntries)
        {
            _segmentPreviewCache.Clear();
        }

        _segmentPreviewCache[item.Id] = new SegmentPreviewCacheEntry(
            source.NoteContentFingerprint,
            source.EventContentFingerprint,
            preview);
        return preview;
    }

    private void RequestSegmentPreviewTile(
        SegmentPreviewTileKey key,
        TimelineSegmentPreview preview,
        long contentWidth,
        long segmentLengthTicks,
        int ticksPerQuarterNote,
        int lod)
    {
        CancellationToken token = _rasterCancellation.Token;
        if (token.IsCancellationRequested)
        {
            return;
        }

        global::Avalonia.Media.Color noteColor = FromArgb(key.NoteColor);
        global::Avalonia.Media.Color eventColor = FromArgb(key.EventColor);
        long tile = key.Tile;
        TimelineRasterCacheKey cacheKey = new(
            TimelineRasterLayer.ArrangementSegmentPreview,
            $"arrangement-segment-preview:{key.SegmentId}",
            unchecked(key.NoteFingerprint ^ key.EventFingerprint),
            contentWidth,
            TimelineSegmentPreviewRasterizer.Height,
            tile,
            lod,
            key.NoteColor,
            key.EventColor,
            0,
            96,
            96);
        bool accepted = TimelineRasterCache.Shared.Request(
            cacheKey,
            workToken => TimelineSegmentPreviewRasterizer.RasterizeFixedPreviewTile(
                preview,
                segmentLengthTicks,
                ticksPerQuarterNote,
                lod,
                tile,
                noteColor,
                eventColor,
                workToken),
            Dispatcher.UIThread,
            () => OnSegmentPreviewTileCompleted(key, cacheKey),
            token,
            TimelineRasterRequestPriority.Visible,
            _rasterConsumerId);
        if (!accepted)
        {
            _segmentPreviewRequests.Remove(key);
        }
    }

    private void OnSegmentPreviewTileCompleted(
        SegmentPreviewTileKey key,
        TimelineRasterCacheKey cacheKey)
    {
        if (!TimelineRasterCache.Shared.TryGet(cacheKey, out WriteableBitmap? bitmap)
            || bitmap is null)
        {
            _segmentPreviewRequests.Remove(key);
            return;
        }

        SegmentPreviewTiles[key] = new SegmentPreviewTile(bitmap);
        SegmentPreviewTileOrder.Enqueue(key);
        while (SegmentPreviewTileOrder.Count > MaximumSegmentPreviewTileEntries
               && SegmentPreviewTileOrder.TryDequeue(out SegmentPreviewTileKey oldest))
        {
            SegmentPreviewTiles.TryRemove(oldest, out _);
        }

        InvalidateVisual();
    }


    // ---- P3 shape batch (Skia canvas) ------------------------------------

    private readonly TimelineShapeBatch _shapeBatch = new();

    /// <summary>Queues a filled rectangle for the batched Skia shape pass.</summary>
    private void AddFill(IBrush brush, Rect rect, double radius = 0, double opacity = 1)
    {
        if (TryGetShapeColor(brush, out uint color, opacity))
        {
            _shapeBatch.AddRect(rect, color, null, radius);
        }
    }

    /// <summary>Queues an outlined (optionally filled) rectangle for the batched shape pass.</summary>
    private void AddShape(
        IBrush? fill,
        IBrush? stroke,
        Rect rect,
        double radius = 0,
        double opacity = 1)
    {
        uint fillColor = 0;
        uint strokeColor = 0;
        bool hasFill = fill is not null && TryGetShapeColor(fill, out fillColor, opacity);
        bool hasStroke = stroke is not null && TryGetShapeColor(stroke, out strokeColor, opacity);
        if (!hasFill && !hasStroke)
        {
            return;
        }

        _shapeBatch.AddRect(
            rect,
            hasFill ? fillColor : 0,
            hasStroke ? strokeColor : null,
            radius);
    }

    private void AddShape(
        IBrush? fill,
        IPen? stroke,
        Rect rect,
        double radius = 0,
        double opacity = 1) =>
        AddShape(fill, stroke?.Brush, rect, radius, opacity);

    /// <summary>Queues a line for the batched Skia shape pass.</summary>
    private void AddLine(IBrush brush, Point from, Point to, double width = 1)
    {
        if (TryGetShapeColor(brush, out uint color))
        {
            _shapeBatch.AddLine(from.X, from.Y, to.X, to.Y, color, width);
        }
    }

    private void AddLine(IPen? pen, Point from, Point to)
    {
        if (pen?.Brush is { } brush)
        {
            AddLine(brush, from, to, pen.Thickness);
        }
    }

    /// <summary>Queues an ellipse for the batched Skia shape pass.</summary>
    private void AddEllipse(IBrush? fill, IPen? stroke, Point center, double radiusX, double radiusY)
    {
        uint fillColor = 0;
        uint strokeColor = 0;
        bool hasFill = fill is not null && TryGetShapeColor(fill, out fillColor);
        bool hasStroke = stroke?.Brush is not null && TryGetShapeColor(stroke.Brush, out strokeColor);
        if (!hasFill && !hasStroke)
        {
            return;
        }

        _shapeBatch.AddEllipse(
            center.X,
            center.Y,
            radiusX,
            radiusY,
            hasFill ? fillColor : 0,
            hasStroke ? strokeColor : 0,
            hasStroke);
    }

    /// <summary>Executes the accumulated shape batch as one leased-Skia draw operation.</summary>
    private void FlushShapes(DrawingContext context)
    {
        if (_shapeBatch.Count == 0)
        {
            return;
        }

        context.Custom(new TimelineShapeDrawOperation(
            new Rect(Bounds.Size),
            _shapeBatch.Build()));
        _shapeBatch.Reset();
    }

    private static bool TryGetShapeColor(IBrush brush, out uint color, double opacity = 1)
    {
        if (brush is not ISolidColorBrush solid)
        {
            color = 0;
            return false;
        }

        opacity = Math.Clamp(brush.Opacity * opacity, 0, 1);
        byte alpha = (byte)Math.Clamp(Math.Round(solid.Color.A * opacity), 0, 255);
        color = ((uint)alpha << 24)
            | ((uint)solid.Color.R << 16)
            | ((uint)solid.Color.G << 8)
            | solid.Color.B;
        return alpha != 0;
    }

    private double GetRenderScaling() =>
        TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;

    private static uint ToArgb(global::Avalonia.Media.Color color) =>
        ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

    private static global::Avalonia.Media.Color FromArgb(uint argb) => global::Avalonia.Media.Color.FromArgb(
        (byte)(argb >> 24),
        (byte)(argb >> 16),
        (byte)(argb >> 8),
        (byte)argb);
}
