using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Midora.Avalonia.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Avalonia.Presentation.Controls;

public sealed partial class TimelineSurface
{
    private static class Color
    {
        public static readonly global::Avalonia.Media.Color Surface =
            global::Avalonia.Media.Color.FromRgb(0x09, 0x0B, 0x0E);

        public static readonly global::Avalonia.Media.Color LaneAlternate =
            global::Avalonia.Media.Color.FromRgb(0x0E, 0x11, 0x15);

        public static readonly global::Avalonia.Media.Color Grid =
            global::Avalonia.Media.Color.FromRgb(0x1A, 0x1F, 0x27);

        public static readonly global::Avalonia.Media.Color Border =
            global::Avalonia.Media.Color.FromRgb(0x2A, 0x30, 0x3A);

        public static readonly global::Avalonia.Media.Color Segment =
            global::Avalonia.Media.Color.FromRgb(0x42, 0x4E, 0x58);

        public static readonly global::Avalonia.Media.Color SelectedSegment =
            global::Avalonia.Media.Color.FromRgb(0x30, 0x3B, 0x45);

        public static readonly global::Avalonia.Media.Color Note =
            global::Avalonia.Media.Color.FromRgb(0xA3, 0xB2, 0xBE);

        public static readonly global::Avalonia.Media.Color Event =
            global::Avalonia.Media.Color.FromRgb(0x4A, 0x2F, 0x34);

        public static readonly global::Avalonia.Media.Color TextTertiary =
            global::Avalonia.Media.Color.FromRgb(0x74, 0x7E, 0x8C);

        public static readonly global::Avalonia.Media.Color Red =
            global::Avalonia.Media.Color.FromRgb(0xE5, 0x48, 0x4D);

        public static readonly global::Avalonia.Media.Color MarqueeFill =
            global::Avalonia.Media.Color.FromArgb(38, 0xE5, 0x48, 0x4D);
    }

    private static readonly SolidColorBrush SurfaceBrush = new(Color.Surface);
    private static readonly SolidColorBrush LaneAlternateBrush = new(Color.LaneAlternate);
    private static readonly SolidColorBrush GridBrush = new(Color.Grid);
    private static readonly SolidColorBrush SegmentBrush = new(Color.Segment);
    private static readonly SolidColorBrush SelectedSegmentBrush = new(Color.SelectedSegment);
    private static readonly SolidColorBrush NoteBrush = new(Color.Note);
    private static readonly SolidColorBrush EventBrush = new(Color.Event);
    private static readonly SolidColorBrush TextTertiaryBrush = new(Color.TextTertiary);
    private static readonly SolidColorBrush RedBrush = new(Color.Red);
    private static readonly SolidColorBrush MarqueeFillBrush = new(Color.MarqueeFill);
    private static readonly Pen BorderPen = new(new SolidColorBrush(Color.Border), 1);
    private static readonly Pen GridPen = new(GridBrush, 1);
    private static readonly Pen SelectedOutlinePen = new(NoteBrush, 1.5);
    private static readonly Pen HoverOutlinePen = new(TextTertiaryBrush, 1);
    private static readonly Pen CursorPen = new(RedBrush, 1);
    private static readonly Pen MarqueePen = new(RedBrush, 1);
    private static readonly Typeface SurfaceTypeface = new(
        FontFamily.Default,
        FontStyle.Normal,
        FontWeight.Normal,
        FontStretch.Normal);

    private readonly Dictionary<(uint Accent, bool Selected), SolidColorBrush> _segmentFillCache = [];

    /// <summary>
    /// Segment band fill derived from the track accent color, darkened for the arrangement
    /// background. Falls back to the fixed segment gray when no accent is present.
    /// </summary>
    private SolidColorBrush SegmentFillFor(uint accent, bool selected)
    {
        if (accent == 0)
        {
            return selected ? SelectedSegmentBrush : SegmentBrush;
        }

        if (_segmentFillCache.TryGetValue((accent, selected), out SolidColorBrush? cached))
        {
            return cached;
        }

        if (_segmentFillCache.Count > 64)
        {
            _segmentFillCache.Clear();
        }

        byte alpha = (byte)(accent >> 24);
        double factor = selected ? 0.8 : 0.55;
        byte red = (byte)Math.Clamp((byte)(accent >> 16) * factor, 0, 255);
        byte green = (byte)Math.Clamp((byte)(accent >> 8) * factor, 0, 255);
        byte blue = (byte)Math.Clamp((byte)accent * factor, 0, 255);
        var brush = new SolidColorBrush(global::Avalonia.Media.Color.FromArgb(
            alpha == 0 ? (byte)0xFF : alpha,
            red,
            green,
            blue));
        _segmentFillCache[(accent, selected)] = brush;
        return brush;
    }

    private bool TryCreateViewport(out TimelineViewport viewport)
    {
        viewport = default;
        double width = Bounds.Width;
        double height = Bounds.Height - RulerHeight;
        double laneHeight = LaneHeight;
        if (!(width > 0) || !(height > 0) || !(laneHeight > 0))
        {
            return false;
        }

        long span = Math.Clamp(TickSpan, 1, long.MaxValue / 2);
        long start = Math.Clamp(StartTick, 0, long.MaxValue - span - 1);
        double laneCountValue = Math.Ceiling(height / laneHeight);
        int laneCount = laneCountValue >= int.MaxValue ? int.MaxValue : Math.Max(1, (int)laneCountValue);
        viewport = new TimelineViewport(
            start,
            start + span,
            Math.Max(0, FirstLane),
            laneCount,
            width,
            height,
            laneHeight);
        return true;
    }

    private ProjectTimeSignatureMap GetTimeSignatureMap()
    {
        long ticksPerQuarterNote = Math.Clamp(TicksPerQuarterNote, 1, 32_767);
        if (_timeSignatureMap is null
            || _timeSignatureMapTicksPerQuarterNote != ticksPerQuarterNote)
        {
            _timeSignatureMap = new ProjectTimeSignatureMap(
                (int)ticksPerQuarterNote,
                [new ProjectTimeSignaturePoint(new MidoraId(1), 0, 4, 4)]);
            _timeSignatureMapTicksPerQuarterNote = ticksPerQuarterNote;
        }

        return _timeSignatureMap;
    }

    private double GetDevicePixelWidth()
    {
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        return double.IsFinite(scaling) && scaling > 0 ? 1 / scaling : 1;
    }

    private static double SnapToDevicePixel(double value, double devicePixel) =>
        Math.Round(value / devicePixel) * devicePixel;

    private static Rect NormalizeRect(Point first, Point second) => new(
        Math.Min(first.X, second.X),
        Math.Min(first.Y, second.Y),
        Math.Abs(first.X - second.X),
        Math.Abs(first.Y - second.Y));
}
