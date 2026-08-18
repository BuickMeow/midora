using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Midora.Desktop.Presentation.Rendering;

internal enum TimelineRasterLayer
{
    ArrangementSegmentPreview,
    PianoNotes,
    PianoSelection,
    PianoDragPreview,
    VelocityBars,
    EventPoints,
    EventPointSelection
}

internal readonly record struct TimelineRasterCacheKey(
    TimelineRasterLayer Layer,
    string ProjectionKey,
    ulong ContentFingerprint,
    long HorizontalScaleKey,
    long VerticalScaleKey,
    long TileX,
    long TileY,
    uint NormalColor,
    uint WarningColor,
    uint OutlineColor,
    int DpiX,
    int DpiY);

public sealed class TimelineRasterBuffer
{
    public TimelineRasterBuffer(int width, int height, byte[] pixels, int candidateCount = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != checked(width * height * 4))
        {
            throw new ArgumentException("The Pbgra32 payload length does not match its dimensions.", nameof(pixels));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(candidateCount);
        Width = width;
        Height = height;
        Pixels = pixels;
        CandidateCount = candidateCount;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride => checked(Width * 4);
    public byte[] Pixels { get; }
    public int CandidateCount { get; }
    public long ByteSize => Pixels.LongLength;

    internal BitmapSource CreateFrozenBitmap()
    {
        BitmapSource bitmap = BitmapSource.Create(
            Width,
            Height,
            96,
            96,
            PixelFormats.Pbgra32,
            null,
            Pixels,
            Stride);
        bitmap.Freeze();
        return bitmap;
    }
}

public static class TimelineRasterLod
{
    private const int LevelsPerOctave = 2;

    public static int Quantize(double devicePixelsPerUnit)
    {
        if (!double.IsFinite(devicePixelsPerUnit) || devicePixelsPerUnit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(devicePixelsPerUnit));
        }
        return checked((int)Math.Round(
            Math.Log2(devicePixelsPerUnit) * LevelsPerOctave,
            MidpointRounding.AwayFromZero));
    }

    public static double GetScale(int level) => Math.Pow(2, level / (double)LevelsPerOctave);
}

public static class TimelineRasterPlacement
{
    public static Rect GetUnclippedItemBounds(
        TimelineViewport viewport,
        TimelineRenderItem item,
        double laneHeaderWidth,
        double rulerHeight,
        double laneHeight)
    {
        viewport.Validate();
        if (!double.IsFinite(laneHeaderWidth) || laneHeaderWidth < 0
            || !double.IsFinite(rulerHeight) || rulerHeight < 0
            || !double.IsFinite(laneHeight) || laneHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(laneHeight));
        }
        double left = laneHeaderWidth + viewport.TickToX(item.StartTick);
        double right = laneHeaderWidth + viewport.TickToX(item.EndTick);
        double top = rulerHeight + (item.Lane - viewport.FirstLane) * laneHeight + 3;
        return new(left, top, Math.Max(1, right - left), Math.Max(3, laneHeight - 6));
    }

    public static Rect GetPianoTileDestination(
        TimelineViewport viewport,
        int horizontalLod,
        int verticalLod,
        long tileX,
        long tileY,
        double laneHeaderWidth,
        double rulerHeight,
        double laneHeight)
        => GetPianoTileDestination(
            viewport,
            TimelineRasterLod.GetScale(horizontalLod),
            TimelineRasterLod.GetScale(verticalLod),
            tileX,
            tileY,
            laneHeaderWidth,
            rulerHeight,
            laneHeight);

    public static Rect GetPianoTileDestination(
        TimelineViewport viewport,
        double devicePixelsPerTick,
        double devicePixelsPerLane,
        long tileX,
        long tileY,
        double laneHeaderWidth,
        double rulerHeight,
        double laneHeight)
    {
        viewport.Validate();
        if (!double.IsFinite(laneHeaderWidth) || laneHeaderWidth < 0
            || !double.IsFinite(rulerHeight) || rulerHeight < 0
            || !double.IsFinite(laneHeight) || laneHeight <= 0
            || !double.IsFinite(devicePixelsPerTick) || devicePixelsPerTick <= 0
            || !double.IsFinite(devicePixelsPerLane) || devicePixelsPerLane <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(laneHeight));
        }

        double dpiScaleX = devicePixelsPerTick / viewport.PixelsPerTick;
        double dpiScaleY = devicePixelsPerLane / laneHeight;
        double tileDeviceX = tileX * TimelinePianoTileRasterizer.TileSize
            - TimelinePianoTileRasterizer.Gutter;
        double tileDeviceY = tileY * TimelinePianoTileRasterizer.TileSize
            - TimelinePianoTileRasterizer.Gutter;
        return new(
            laneHeaderWidth + (tileDeviceX - viewport.StartTick * devicePixelsPerTick) / dpiScaleX,
            rulerHeight + (tileDeviceY - viewport.FirstLane * devicePixelsPerLane) / dpiScaleY,
            TimelinePianoTileRasterizer.RasterSize / dpiScaleX,
            TimelinePianoTileRasterizer.RasterSize / dpiScaleY);
    }

    public static Rect GetPianoTileCoreDestination(
        TimelineViewport viewport,
        int horizontalLod,
        int verticalLod,
        long tileX,
        long tileY,
        double laneHeaderWidth,
        double rulerHeight,
        double laneHeight)
        => GetPianoTileCoreDestination(
            viewport,
            TimelineRasterLod.GetScale(horizontalLod),
            TimelineRasterLod.GetScale(verticalLod),
            tileX,
            tileY,
            laneHeaderWidth,
            rulerHeight,
            laneHeight);

    public static Rect GetPianoTileCoreDestination(
        TimelineViewport viewport,
        double devicePixelsPerTick,
        double devicePixelsPerLane,
        long tileX,
        long tileY,
        double laneHeaderWidth,
        double rulerHeight,
        double laneHeight)
    {
        viewport.Validate();
        if (!double.IsFinite(laneHeaderWidth) || laneHeaderWidth < 0
            || !double.IsFinite(rulerHeight) || rulerHeight < 0
            || !double.IsFinite(laneHeight) || laneHeight <= 0
            || !double.IsFinite(devicePixelsPerTick) || devicePixelsPerTick <= 0
            || !double.IsFinite(devicePixelsPerLane) || devicePixelsPerLane <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(laneHeight));
        }

        double dpiScaleX = devicePixelsPerTick / viewport.PixelsPerTick;
        double dpiScaleY = devicePixelsPerLane / laneHeight;
        double tileDeviceX = tileX * TimelinePianoTileRasterizer.TileSize;
        double tileDeviceY = tileY * TimelinePianoTileRasterizer.TileSize;
        return new(
            laneHeaderWidth + (tileDeviceX - viewport.StartTick * devicePixelsPerTick) / dpiScaleX,
            rulerHeight + (tileDeviceY - viewport.FirstLane * devicePixelsPerLane) / dpiScaleY,
            TimelinePianoTileRasterizer.TileSize / dpiScaleX,
            TimelinePianoTileRasterizer.TileSize / dpiScaleY);
    }

    public static Rect GetVelocityTileDestination(
        TimelineViewport viewport,
        int horizontalLod,
        long tileX,
        double laneHeaderWidth,
        double rulerHeight,
        double valueTop,
        double valueBottom)
    {
        viewport.Validate();
        double lodPixelsPerTick = TimelineRasterLod.GetScale(horizontalLod);
        double tileTick = (tileX * TimelineVelocityTileRasterizer.TileSize
            - TimelineVelocityTileRasterizer.Gutter) / lodPixelsPerTick;
        return new(
            laneHeaderWidth + (tileTick - viewport.StartTick) * viewport.PixelsPerTick,
            valueTop,
            TimelineVelocityTileRasterizer.RasterWidth / lodPixelsPerTick * viewport.PixelsPerTick,
            valueBottom - valueTop);
    }

    public static Rect GetVelocityTileCoreDestination(
        TimelineViewport viewport,
        int horizontalLod,
        long tileX,
        double laneHeaderWidth,
        double rulerHeight,
        double valueTop,
        double valueBottom)
    {
        double lodPixelsPerTick = TimelineRasterLod.GetScale(horizontalLod);
        double tileTick = tileX * TimelineVelocityTileRasterizer.TileSize / lodPixelsPerTick;
        return new(
            laneHeaderWidth + (tileTick - viewport.StartTick) * viewport.PixelsPerTick,
            valueTop,
            TimelineVelocityTileRasterizer.TileSize / lodPixelsPerTick * viewport.PixelsPerTick,
            valueBottom - valueTop);
    }

    public static Rect GetEventPointTileDestination(
        TimelineViewport viewport,
        double devicePixelsPerTick,
        double devicePixelsPerValue,
        long tileX,
        long tileY,
        double laneHeaderWidth,
        double rulerHeight,
        double valueMinimum,
        double valueMaximum,
        double dpiScaleX,
        double dpiScaleY)
    {
        ValidateEventPointPlacement(
            viewport,
            devicePixelsPerTick,
            devicePixelsPerValue,
            laneHeaderWidth,
            rulerHeight,
            valueMinimum,
            valueMaximum,
            dpiScaleX,
            dpiScaleY);
        int gutterX = TimelineEventPointTileRasterizer.GetGutter(dpiScaleX);
        int gutterY = TimelineEventPointTileRasterizer.GetGutter(dpiScaleY);
        double valueRange = valueMaximum - valueMinimum;
        double contentHeight = Math.Max(1, viewport.Height);
        double worldLeft = tileX * TimelineEventPointTileRasterizer.TileSize - gutterX;
        double worldTop = tileY * TimelineEventPointTileRasterizer.TileSize - gutterY;
        double sourceTopValue = 1 - worldTop / devicePixelsPerValue;
        return new(
            laneHeaderWidth
                + (worldLeft / devicePixelsPerTick - viewport.StartTick) * viewport.PixelsPerTick,
            rulerHeight + (valueMaximum - sourceTopValue) / valueRange * contentHeight,
            TimelineEventPointTileRasterizer.GetRasterSize(dpiScaleX)
                / devicePixelsPerTick * viewport.PixelsPerTick,
            TimelineEventPointTileRasterizer.GetRasterSize(dpiScaleY)
                / devicePixelsPerValue / valueRange * contentHeight);
    }

    public static Rect GetEventPointTileCoreDestination(
        TimelineViewport viewport,
        double devicePixelsPerTick,
        double devicePixelsPerValue,
        long tileX,
        long tileY,
        double laneHeaderWidth,
        double rulerHeight,
        double valueMinimum,
        double valueMaximum,
        double dpiScaleX,
        double dpiScaleY)
    {
        ValidateEventPointPlacement(
            viewport,
            devicePixelsPerTick,
            devicePixelsPerValue,
            laneHeaderWidth,
            rulerHeight,
            valueMinimum,
            valueMaximum,
            dpiScaleX,
            dpiScaleY);
        double valueRange = valueMaximum - valueMinimum;
        double contentHeight = Math.Max(1, viewport.Height);
        double worldLeft = tileX * TimelineEventPointTileRasterizer.TileSize;
        double worldTop = tileY * TimelineEventPointTileRasterizer.TileSize;
        double sourceTopValue = 1 - worldTop / devicePixelsPerValue;
        return new(
            laneHeaderWidth
                + (worldLeft / devicePixelsPerTick - viewport.StartTick) * viewport.PixelsPerTick,
            rulerHeight + (valueMaximum - sourceTopValue) / valueRange * contentHeight,
            TimelineEventPointTileRasterizer.TileSize
                / devicePixelsPerTick * viewport.PixelsPerTick,
            TimelineEventPointTileRasterizer.TileSize
                / devicePixelsPerValue / valueRange * contentHeight);
    }

    private static void ValidateEventPointPlacement(
        TimelineViewport viewport,
        double devicePixelsPerTick,
        double devicePixelsPerValue,
        double laneHeaderWidth,
        double rulerHeight,
        double valueMinimum,
        double valueMaximum,
        double dpiScaleX,
        double dpiScaleY)
    {
        viewport.Validate();
        if (!double.IsFinite(devicePixelsPerTick) || devicePixelsPerTick <= 0
            || !double.IsFinite(devicePixelsPerValue) || devicePixelsPerValue <= 0
            || !double.IsFinite(laneHeaderWidth) || laneHeaderWidth < 0
            || !double.IsFinite(rulerHeight) || rulerHeight < 0
            || !double.IsFinite(valueMinimum) || !double.IsFinite(valueMaximum)
            || valueMinimum < 0 || valueMaximum > 1 || valueMaximum <= valueMinimum
            || !double.IsFinite(dpiScaleX) || dpiScaleX <= 0
            || !double.IsFinite(dpiScaleY) || dpiScaleY <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(devicePixelsPerTick));
        }
    }
}

public readonly struct TimelinePianoTileRasterRequest
{
    public TimelinePianoTileRasterRequest(
        TimelineRenderSnapshot snapshot,
        int horizontalLod,
        int verticalLod,
        long tileX,
        long tileY,
        Color normalColor,
        Color warningColor)
        : this(
            snapshot,
            TimelineRasterLod.GetScale(horizontalLod),
            TimelineRasterLod.GetScale(verticalLod),
            tileX,
            tileY,
            normalColor,
            warningColor)
    {
    }

    public TimelinePianoTileRasterRequest(
        TimelineRenderSnapshot snapshot,
        double devicePixelsPerTick,
        double devicePixelsPerLane,
        long tileX,
        long tileY,
        Color normalColor,
        Color warningColor)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!double.IsFinite(devicePixelsPerTick) || devicePixelsPerTick <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(devicePixelsPerTick));
        }
        if (!double.IsFinite(devicePixelsPerLane) || devicePixelsPerLane <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(devicePixelsPerLane));
        }
        Snapshot = snapshot;
        DevicePixelsPerTick = devicePixelsPerTick;
        DevicePixelsPerLane = devicePixelsPerLane;
        TileX = tileX;
        TileY = tileY;
        NormalColor = normalColor;
        WarningColor = warningColor;
    }

    public TimelineRenderSnapshot Snapshot { get; }
    public double DevicePixelsPerTick { get; }
    public double DevicePixelsPerLane { get; }
    public long TileX { get; }
    public long TileY { get; }
    public Color NormalColor { get; }
    public Color WarningColor { get; }

    public TimelineRasterBuffer Rasterize() => TimelinePianoTileRasterizer.Rasterize(
        Snapshot,
        DevicePixelsPerTick,
        DevicePixelsPerLane,
        TileX,
        TileY,
        NormalColor,
        WarningColor);
}

public static class TimelinePianoTileRasterizer
{
    public const int TileSize = 256;
    public const int Gutter = 1;
    public const int RasterSize = TileSize + Gutter * 2;

    public static ulong ComputeContentFingerprint(
        TimelineRenderSnapshot snapshot,
        int horizontalLod,
        int verticalLod,
        long tileX,
        long tileY)
        => ComputeContentFingerprint(
            snapshot,
            TimelineRasterLod.GetScale(horizontalLod),
            TimelineRasterLod.GetScale(verticalLod),
            tileX,
            tileY);

    public static ulong ComputeContentFingerprint(
        TimelineRenderSnapshot snapshot,
        double devicePixelsPerTick,
        double devicePixelsPerLane,
        long tileX,
        long tileY)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!TryGetTileBounds(
                devicePixelsPerTick,
                devicePixelsPerLane,
                tileX,
                tileY,
                out long startTick,
                out long endTick,
                out int firstLane,
                out int lastLaneExclusive))
        {
            return TimelineContentFingerprint.ForPianoTileItems([]);
        }
        List<TimelineRenderItem> candidates = [];
        snapshot.Index.QueryInto(startTick, endTick, firstLane, lastLaneExclusive, candidates);
        return TimelineContentFingerprint.ForPianoTileItems(candidates);
    }

    public static TimelineRasterBuffer Rasterize(
        TimelineRenderSnapshot snapshot,
        int horizontalLod,
        int verticalLod,
        long tileX,
        long tileY,
        Color normalColor,
        Color warningColor,
        TimelineSelectionSnapshot? selection = null,
        bool selectionOnly = false,
        Color? outlineColor = null)
        => Rasterize(
            snapshot,
            TimelineRasterLod.GetScale(horizontalLod),
            TimelineRasterLod.GetScale(verticalLod),
            tileX,
            tileY,
            normalColor,
            warningColor,
            selection,
            selectionOnly,
            outlineColor);

    public static TimelineRasterBuffer Rasterize(
        TimelineRenderSnapshot snapshot,
        double devicePixelsPerTick,
        double devicePixelsPerLane,
        long tileX,
        long tileY,
        Color normalColor,
        Color warningColor,
        TimelineSelectionSnapshot? selection = null,
        bool selectionOnly = false,
        Color? outlineColor = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!TryGetTileBounds(
                devicePixelsPerTick,
                devicePixelsPerLane,
                tileX,
                tileY,
                out long startTick,
                out long endTick,
                out int firstLane,
                out int lastLaneExclusive))
        {
            return new(RasterSize, RasterSize, new byte[RasterSize * RasterSize * 4]);
        }

        double worldLeft = tileX * (double)TileSize - Gutter;
        double worldTop = tileY * (double)TileSize - Gutter;
        List<TimelineRenderItem> candidates = [];
        snapshot.Index.QueryInto(startTick, endTick, firstLane, lastLaneExclusive, candidates);
        byte[] pixels = new byte[RasterSize * RasterSize * 4];
        foreach (TimelineRenderItem item in candidates)
        {
            if (item.Kind is not (TimelineItemKind.LogicalNote or TimelineItemKind.TemplateNote))
            {
                continue;
            }
            bool selected = selection?.Contains(item.Id)
                ?? item.State.HasFlag(TimelineItemState.Selected);
            if (selectionOnly && !selected)
            {
                continue;
            }

            double verticalInset = Math.Min(3, devicePixelsPerLane * 0.25);
            int rawLeft = RoundPixelBoundary(item.StartTick * devicePixelsPerTick - worldLeft);
            int rawRight = Math.Max(
                rawLeft + 1,
                RoundPixelBoundary(item.EndTick * devicePixelsPerTick - worldLeft));
            int rawTop = RoundPixelBoundary(
                item.Lane * devicePixelsPerLane - worldTop + verticalInset);
            int rawBottom = Math.Max(
                rawTop + 1,
                RoundPixelBoundary(
                    (item.Lane + 1d) * devicePixelsPerLane - worldTop - verticalInset));
            int left = Math.Clamp(rawLeft, 0, RasterSize);
            int right = Math.Clamp(rawRight, 0, RasterSize);
            int top = Math.Clamp(rawTop, 0, RasterSize);
            int bottom = Math.Clamp(rawBottom, 0, RasterSize);
            if (right <= left || bottom <= top)
            {
                continue;
            }

            Color color = item.State.HasFlag(TimelineItemState.Invalid)
                || item.State.HasFlag(TimelineItemState.Broken)
                ? warningColor
                : normalColor;
            double opacity = selectionOnly
                ? item.State.HasFlag(TimelineItemState.OutsideActiveRange) ? 0.7 : 0.94
                : item.State.HasFlag(TimelineItemState.OutsideActiveRange) ? 0.35 : 0.78;
            if (color.A != 0)
            {
                FillRectangle(pixels, RasterSize, left, top, right, bottom, color, opacity);
            }
            DrawRectangleOutline(
                pixels,
                RasterSize,
                left,
                top,
                right,
                bottom,
                outlineColor ?? Darken(color),
                selectionOnly ? 1 : 0.82,
                drawLeft: rawLeft >= 0,
                drawTop: rawTop >= 0,
                drawRight: rawRight <= RasterSize,
                drawBottom: rawBottom <= RasterSize);
        }
        return new(RasterSize, RasterSize, pixels, candidates.Count);
    }

    private static bool TryGetTileBounds(
        double devicePixelsPerTick,
        double devicePixelsPerLane,
        long tileX,
        long tileY,
        out long startTick,
        out long endTick,
        out int firstLane,
        out int lastLaneExclusive)
    {
        if (!double.IsFinite(devicePixelsPerTick) || devicePixelsPerTick <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(devicePixelsPerTick));
        }
        if (!double.IsFinite(devicePixelsPerLane) || devicePixelsPerLane <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(devicePixelsPerLane));
        }
        double worldLeft = tileX * (double)TileSize - Gutter;
        double worldTop = tileY * (double)TileSize - Gutter;
        if (worldLeft + RasterSize <= 0 || worldTop + RasterSize <= 0)
        {
            startTick = 0;
            endTick = 1;
            firstLane = 0;
            lastLaneExclusive = 1;
            return false;
        }

        startTick = Math.Max(0, FloorToLong(worldLeft / devicePixelsPerTick));
        endTick = Math.Max(
            startTick + 1,
            CeilingToLong((worldLeft + RasterSize) / devicePixelsPerTick));
        firstLane = Math.Max(0, FloorToInt(worldTop / devicePixelsPerLane));
        lastLaneExclusive = Math.Min(
            128,
            Math.Max(
                firstLane + 1,
                CeilingToInt((worldTop + RasterSize) / devicePixelsPerLane)));
        return firstLane < 128;
    }

    private static int RoundPixelBoundary(double value) => checked((int)Math.Floor(value + 0.5));

    private static long FloorToLong(double value) => value <= long.MinValue
        ? long.MinValue
        : value >= long.MaxValue
            ? long.MaxValue
            : (long)Math.Floor(value);

    private static long CeilingToLong(double value) => value <= long.MinValue
        ? long.MinValue
        : value >= long.MaxValue
            ? long.MaxValue
            : (long)Math.Ceiling(value);

    private static int FloorToInt(double value) => value <= int.MinValue
        ? int.MinValue
        : value >= int.MaxValue
            ? int.MaxValue
            : (int)Math.Floor(value);

    private static int CeilingToInt(double value) => value <= int.MinValue
        ? int.MinValue
        : value >= int.MaxValue
            ? int.MaxValue
            : (int)Math.Ceiling(value);

    internal static void FillRectangle(
        byte[] pixels,
        int width,
        int left,
        int top,
        int right,
        int bottom,
        Color color,
        double opacity)
    {
        byte alpha = (byte)Math.Clamp(
            (int)Math.Round(color.A * Math.Clamp(opacity, 0, 1), MidpointRounding.AwayFromZero),
            0,
            255);
        byte blue = Premultiply(color.B, alpha);
        byte green = Premultiply(color.G, alpha);
        byte red = Premultiply(color.R, alpha);
        for (int y = top; y < bottom; y++)
        {
            int offset = checked((y * width + left) * 4);
            for (int x = left; x < right; x++, offset += 4)
            {
                if (pixels[offset + 3] > alpha)
                {
                    continue;
                }
                pixels[offset] = blue;
                pixels[offset + 1] = green;
                pixels[offset + 2] = red;
                pixels[offset + 3] = alpha;
            }
        }
    }

    internal static void DrawRectangleOutline(
        byte[] pixels,
        int width,
        int left,
        int top,
        int right,
        int bottom,
        Color color,
        double opacity,
        bool drawLeft = true,
        bool drawTop = true,
        bool drawRight = true,
        bool drawBottom = true)
    {
        if (right <= left || bottom <= top) return;
        if (drawTop)
            FillRectangle(pixels, width, left, top, right, Math.Min(top + 1, bottom), color, opacity);
        if (drawBottom)
            FillRectangle(pixels, width, left, Math.Max(top, bottom - 1), right, bottom, color, opacity);
        if (drawLeft)
            FillRectangle(pixels, width, left, top, Math.Min(left + 1, right), bottom, color, opacity);
        if (drawRight)
            FillRectangle(pixels, width, Math.Max(left, right - 1), top, right, bottom, color, opacity);
    }

    private static Color Darken(Color color) => Color.FromArgb(
        color.A,
        (byte)(color.R * 0.42),
        (byte)(color.G * 0.42),
        (byte)(color.B * 0.42));

    private static byte Premultiply(byte value, byte alpha) =>
        (byte)((value * alpha + 127) / 255);
}

public static class TimelineSegmentPreviewRasterizer
{
    public const int ContentWidth = 512;
    public const int Width = ContentWidth;
    public const int Height = 64;

    public static TimelineRasterBuffer Rasterize(TimelineSegmentPreview preview, Color noteColor)
    {
        ArgumentNullException.ThrowIfNull(preview);
        byte[] pixels = new byte[Width * Height * 4];
        double pitchTravel = Height - 1;
        foreach (TimelineSegmentPreviewNote note in preview.Notes)
        {
            int left = Math.Clamp(
                RoundNormalizedBoundary(note.NormalizedStart),
                0,
                ContentWidth - 1);
            int right = Math.Clamp(
                Math.Max(left + 1, RoundNormalizedBoundary(note.NormalizedEnd)),
                1,
                ContentWidth);
            int top = Math.Clamp(
                (int)Math.Round((127 - note.Pitch) / 127d * pitchTravel, MidpointRounding.AwayFromZero),
                0,
                Height - 1);
            int bottom = Math.Min(Height, top + 2);
            if (bottom - top < 2)
            {
                top = Math.Max(0, bottom - 2);
            }
            TimelinePianoTileRasterizer.FillRectangle(
                pixels,
                Width,
                left,
                top,
                right,
                bottom,
                noteColor,
                0.72);
        }
        return new(Width, Height, pixels, preview.Notes.Count);
    }

    private static int RoundNormalizedBoundary(double value) =>
        checked((int)Math.Floor(value * ContentWidth + 0.5));
}

public static class TimelineEventPointTileRasterizer
{
    public const int TileSize = 256;
    public const double PointRadius = 4;
    public const double SelectionRadius = 6;

    public static int GetGutter(double dpiScale)
    {
        if (!double.IsFinite(dpiScale) || dpiScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpiScale));
        }
        return checked((int)Math.Ceiling(SelectionRadius * dpiScale) + 1);
    }

    public static int GetRasterSize(double dpiScale) =>
        checked(TileSize + GetGutter(dpiScale) * 2);

    public static TimelineRasterBuffer Rasterize(
        TimelineRenderSnapshot snapshot,
        TimelineSelectionSnapshot? selection,
        double devicePixelsPerTick,
        double devicePixelsPerValue,
        long tileX,
        long tileY,
        double dpiScaleX,
        double dpiScaleY,
        Color normalColor,
        Color primaryColor,
        Color borderColor,
        bool selectionOnly = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!double.IsFinite(devicePixelsPerTick) || devicePixelsPerTick <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(devicePixelsPerTick));
        }
        if (!double.IsFinite(devicePixelsPerValue) || devicePixelsPerValue <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(devicePixelsPerValue));
        }
        int gutterX = GetGutter(dpiScaleX);
        int gutterY = GetGutter(dpiScaleY);
        int width = checked(TileSize + gutterX * 2);
        int height = checked(TileSize + gutterY * 2);
        double worldLeft = tileX * (double)TileSize - gutterX;
        double worldTop = tileY * (double)TileSize - gutterY;
        long startTick = Math.Max(0, FloorToLong(worldLeft / devicePixelsPerTick));
        long endTick = Math.Max(
            startTick + 1,
            CeilingToLong((worldLeft + width) / devicePixelsPerTick));
        List<TimelineRenderItem> candidates = [];
        snapshot.Index.QueryInto(startTick, endTick, 0, 1, candidates);
        candidates.RemoveAll(static item => item.Kind != TimelineItemKind.LogicalParameterPoint);
        if (selectionOnly)
        {
            candidates.RemoveAll(item => selection?.Contains(item.Id) != true);
        }

        byte[] pixels = new byte[checked(width * height * 4)];
        double pointRadiusX = Math.Max(1, PointRadius * dpiScaleX);
        double pointRadiusY = Math.Max(1, PointRadius * dpiScaleY);
        double selectionRadiusX = Math.Max(pointRadiusX + 1, SelectionRadius * dpiScaleX);
        double selectionRadiusY = Math.Max(pointRadiusY + 1, SelectionRadius * dpiScaleY);
        double outlineX = Math.Max(1, dpiScaleX);
        double outlineY = Math.Max(1, dpiScaleY);
        foreach (TimelineRenderItem item in candidates)
        {
            double worldY = (1 - Math.Clamp(item.Value, 0, 1)) * devicePixelsPerValue;
            double centerX = item.StartTick * devicePixelsPerTick - worldLeft;
            double centerY = worldY - worldTop;
            if (centerX + selectionRadiusX < 0 || centerX - selectionRadiusX >= width
                || centerY + selectionRadiusY < 0 || centerY - selectionRadiusY >= height)
            {
                continue;
            }

            bool selected = selection?.Contains(item.Id)
                ?? item.State.HasFlag(TimelineItemState.Selected);
            bool primary = selection is not null
                ? selection.Primary == item.Id
                : item.State.HasFlag(TimelineItemState.Primary);
            if (selected)
            {
                FillEllipse(
                    pixels,
                    width,
                    height,
                    centerX,
                    centerY,
                    selectionRadiusX,
                    selectionRadiusY,
                    primary ? primaryColor : normalColor);
            }
            FillEllipse(
                pixels,
                width,
                height,
                centerX,
                centerY,
                pointRadiusX,
                pointRadiusY,
                borderColor);
            FillEllipse(
                pixels,
                width,
                height,
                centerX,
                centerY,
                Math.Max(0.5, pointRadiusX - outlineX),
                Math.Max(0.5, pointRadiusY - outlineY),
                normalColor);
        }
        return new(width, height, pixels, candidates.Count);
    }

    private static void FillEllipse(
        byte[] pixels,
        int width,
        int height,
        double centerX,
        double centerY,
        double radiusX,
        double radiusY,
        Color color)
    {
        int left = Math.Max(0, checked((int)Math.Floor(centerX - radiusX)));
        int right = Math.Min(width, checked((int)Math.Ceiling(centerX + radiusX)));
        int top = Math.Max(0, checked((int)Math.Floor(centerY - radiusY)));
        int bottom = Math.Min(height, checked((int)Math.Ceiling(centerY + radiusY)));
        byte alpha = color.A;
        byte blue = Premultiply(color.B, alpha);
        byte green = Premultiply(color.G, alpha);
        byte red = Premultiply(color.R, alpha);
        for (int y = top; y < bottom; y++)
        {
            double normalizedY = (y + 0.5 - centerY) / radiusY;
            double normalizedYSquared = normalizedY * normalizedY;
            if (normalizedYSquared > 1) continue;
            int offset = checked((y * width + left) * 4);
            for (int x = left; x < right; x++, offset += 4)
            {
                double normalizedX = (x + 0.5 - centerX) / radiusX;
                if (normalizedX * normalizedX + normalizedYSquared > 1) continue;
                pixels[offset] = blue;
                pixels[offset + 1] = green;
                pixels[offset + 2] = red;
                pixels[offset + 3] = alpha;
            }
        }
    }

    private static byte Premultiply(byte value, byte alpha) =>
        (byte)((value * alpha + 127) / 255);

    private static long FloorToLong(double value) => value <= long.MinValue
        ? long.MinValue
        : value >= long.MaxValue ? long.MaxValue : (long)Math.Floor(value);

    private static long CeilingToLong(double value) => value <= long.MinValue
        ? long.MinValue
        : value >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(value);
}

public static class TimelineVelocityTileRasterizer
{
    public const int TileSize = 256;
    public const int StemWidth = 3;
    public const int MarkerSize = 7;
    public const int Gutter = MarkerSize / 2 + 1;
    public const int RasterWidth = TileSize + Gutter * 2;
    public const int RasterHeight = 256;

    public static TimelineRasterBuffer Rasterize(
        TimelineRenderSnapshot snapshot,
        TimelineSelectionSnapshot? selection,
        int horizontalLod,
        long tileX,
        Color normalColor,
        Color selectedColor,
        Color borderColor)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        double pixelsPerTick = TimelineRasterLod.GetScale(horizontalLod);
        double worldLeft = tileX * (double)TileSize - Gutter;
        long startTick = Math.Max(0, FloorToLong(worldLeft / pixelsPerTick));
        long endTick = Math.Max(startTick + 1, CeilingToLong((worldLeft + RasterWidth) / pixelsPerTick));
        List<TimelineRenderItem> candidates = [];
        snapshot.Index.QueryInto(startTick, endTick, 0, 1, candidates);
        candidates.Sort(static (left, right) =>
        {
            int byTick = left.StartTick.CompareTo(right.StartTick);
            if (byTick != 0) return byTick;
            int byPitch = left.ZIndex.CompareTo(right.ZIndex);
            return byPitch != 0 ? byPitch : left.Id.CompareTo(right.Id);
        });
        byte[] pixels = new byte[RasterWidth * RasterHeight * 4];
        foreach (TimelineRenderItem item in candidates)
        {
            if (item.Kind != TimelineItemKind.Velocity) continue;
            bool selected = selection?.Contains(item.Id)
                ?? item.State.HasFlag(TimelineItemState.Selected);
            int rawCenter = checked((int)Math.Round(
                item.StartTick * pixelsPerTick - worldLeft,
                MidpointRounding.AwayFromZero));
            int rawLeft = rawCenter - StemWidth / 2;
            int rawRight = rawLeft + StemWidth;
            int left = Math.Clamp(rawLeft, 0, RasterWidth);
            int right = Math.Clamp(rawRight, 0, RasterWidth);
            int top = Math.Clamp(
                (int)Math.Round((1 - Math.Clamp(item.Value, 0, 1)) * (RasterHeight - 1), MidpointRounding.AwayFromZero),
                0,
                RasterHeight - 1);
            if (right <= left) continue;
            Color fill = selected ? selectedColor : normalColor;
            TimelinePianoTileRasterizer.FillRectangle(
                pixels, RasterWidth, left, top, right, RasterHeight, fill, selected ? 0.4 : 0.2);
            TimelinePianoTileRasterizer.DrawRectangleOutline(
                pixels, RasterWidth, left, top, right, RasterHeight, borderColor, selected ? 1 : 0.86,
                drawLeft: rawLeft >= 0,
                drawTop: true,
                drawRight: rawRight <= RasterWidth,
                drawBottom: true);
            int rawMarkerLeft = rawCenter - MarkerSize / 2;
            int rawMarkerRight = rawMarkerLeft + MarkerSize;
            int markerLeft = Math.Clamp(rawMarkerLeft, 0, RasterWidth);
            int markerRight = Math.Clamp(rawMarkerRight, 0, RasterWidth);
            int markerBottom = Math.Min(RasterHeight, top + MarkerSize);
            TimelinePianoTileRasterizer.FillRectangle(
                pixels, RasterWidth, markerLeft, top, markerRight, markerBottom, fill, 1);
            TimelinePianoTileRasterizer.DrawRectangleOutline(
                pixels, RasterWidth, markerLeft, top, markerRight, markerBottom, borderColor, 1,
                drawLeft: rawMarkerLeft >= 0,
                drawTop: true,
                drawRight: rawMarkerRight <= RasterWidth,
                drawBottom: true);
        }
        return new(RasterWidth, RasterHeight, pixels, candidates.Count);
    }

    public static ulong ComputeContentFingerprint(
        TimelineRenderSnapshot snapshot,
        TimelineSelectionSnapshot? selection,
        int horizontalLod,
        long tileX)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        double pixelsPerTick = TimelineRasterLod.GetScale(horizontalLod);
        double worldLeft = tileX * (double)TileSize - Gutter;
        long startTick = Math.Max(0, FloorToLong(worldLeft / pixelsPerTick));
        long endTick = Math.Max(startTick + 1, CeilingToLong((worldLeft + RasterWidth) / pixelsPerTick));
        List<TimelineRenderItem> candidates = [];
        snapshot.Index.QueryInto(startTick, endTick, 0, 1, candidates);
        return TimelineContentFingerprint.ForVelocityTileItems(candidates, selection);
    }

    private static long FloorToLong(double value) => value <= long.MinValue
        ? long.MinValue
        : value >= long.MaxValue ? long.MaxValue : (long)Math.Floor(value);

    private static long CeilingToLong(double value) => value <= long.MinValue
        ? long.MinValue
        : value >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(value);
}

internal sealed class TimelineRasterCache
{
    public const long MaximumBytes = 256L * 1024 * 1024;
    public const int MaximumInFlight = 64;
    private const int MaximumWorkers = 2;
    private readonly object _gate = new();
    private readonly Dictionary<TimelineRasterCacheKey, CacheEntry> _completed = [];
    private readonly Dictionary<TimelineRasterCacheKey, List<Completion>> _inFlight = [];
    private readonly LinkedList<TimelineRasterCacheKey> _lru = [];
    private readonly SemaphoreSlim _workers = new(MaximumWorkers, MaximumWorkers);
    private long _currentBytes;
    private long _generation;

    public static TimelineRasterCache Shared { get; } = new();

    public long CurrentBytes
    {
        get { lock (_gate) return _currentBytes; }
    }

    public int CompletedCount
    {
        get { lock (_gate) return _completed.Count; }
    }

    public bool TryGet(TimelineRasterCacheKey key, out BitmapSource? bitmap)
    {
        lock (_gate)
        {
            if (!_completed.TryGetValue(key, out CacheEntry? entry))
            {
                bitmap = null;
                return false;
            }
            _lru.Remove(entry.Node);
            _lru.AddFirst(entry.Node);
            bitmap = entry.Bitmap;
            return true;
        }
    }

    public bool Request(
        TimelineRasterCacheKey key,
        Func<TimelineRasterBuffer> factory,
        Dispatcher dispatcher,
        Action completion)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(completion);
        long generation;
        lock (_gate)
        {
            if (_completed.ContainsKey(key))
            {
                dispatcher.BeginInvoke(completion, DispatcherPriority.Render);
                return true;
            }
            if (_inFlight.TryGetValue(key, out List<Completion>? completions))
            {
                completions.Add(new(dispatcher, completion));
                return true;
            }
            if (_inFlight.Count >= MaximumInFlight)
            {
                return false;
            }
            _inFlight.Add(key, [new(dispatcher, completion)]);
            generation = _generation;
        }

        _ = Task.Run(async () =>
        {
            BitmapSource? bitmap = null;
            long bytes = 0;
            try
            {
                await _workers.WaitAsync().ConfigureAwait(false);
                try
                {
                    TimelineRasterBuffer buffer = factory();
                    bitmap = buffer.CreateFrozenBitmap();
                    bytes = buffer.ByteSize;
                }
                finally
                {
                    _workers.Release();
                }
            }
            catch (Exception exception)
            {
                Trace.TraceError($"Timeline rasterization failed: {exception}");
            }

            List<Completion> callbacks;
            lock (_gate)
            {
                callbacks = _inFlight.Remove(key, out List<Completion>? pending) ? pending : [];
                if (bitmap is not null && generation == _generation && bytes <= MaximumBytes)
                {
                    LinkedListNode<TimelineRasterCacheKey> node = _lru.AddFirst(key);
                    _completed[key] = new(bitmap, bytes, node);
                    _currentBytes = checked(_currentBytes + bytes);
                    TrimLocked();
                }
            }
            foreach (Completion callback in callbacks)
            {
                _ = callback.Dispatcher.BeginInvoke(callback.Action, DispatcherPriority.Render);
            }
        });
        return true;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _generation = checked(_generation + 1);
            _completed.Clear();
            _lru.Clear();
            _currentBytes = 0;
        }
    }

    private void TrimLocked()
    {
        while (_currentBytes > MaximumBytes && _lru.Last is LinkedListNode<TimelineRasterCacheKey> node)
        {
            _lru.RemoveLast();
            if (_completed.Remove(node.Value, out CacheEntry? entry))
            {
                _currentBytes -= entry.Bytes;
            }
        }
    }

    private sealed record CacheEntry(
        BitmapSource Bitmap,
        long Bytes,
        LinkedListNode<TimelineRasterCacheKey> Node);

    private sealed record Completion(Dispatcher Dispatcher, Action Action);
}

public static class TimelineRasterCacheSession
{
    public static long CurrentBytes => TimelineRasterCache.Shared.CurrentBytes;
    public static int CompletedCount => TimelineRasterCache.Shared.CompletedCount;
    public static void Clear() => TimelineRasterCache.Shared.Clear();
}
