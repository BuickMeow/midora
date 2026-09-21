using System.Globalization;
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

        public static readonly global::Avalonia.Media.Color TextPrimary =
            global::Avalonia.Media.Color.FromRgb(0xF1, 0xF3, 0xF5);

        public static readonly global::Avalonia.Media.Color SegmentNotePreview =
            global::Avalonia.Media.Color.FromRgb(0xBD, 0xC7, 0xCF);

        public static readonly global::Avalonia.Media.Color EventPreview =
            global::Avalonia.Media.Color.FromRgb(0xE5, 0x3D, 0x44);

        public static readonly global::Avalonia.Media.Color NoteSelected =
            global::Avalonia.Media.Color.FromRgb(0x8F, 0x24, 0x29);

        public static readonly global::Avalonia.Media.Color Red =
            global::Avalonia.Media.Color.FromRgb(0xE5, 0x48, 0x4D);

        /// <summary>White-key piano-roll rows: lighter than the surface, matching the keyboard strip.</summary>
        public static readonly global::Avalonia.Media.Color PianoWhiteKeyRow =
            global::Avalonia.Media.Color.FromRgb(0x1B, 0x20, 0x27);

        public static readonly global::Avalonia.Media.Color Info =
            global::Avalonia.Media.Color.FromRgb(0x62, 0xA6, 0xF6);

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
    private static readonly SolidColorBrush TextPrimaryBrush = new(Color.TextPrimary);
    private static readonly SolidColorBrush SegmentNotePreviewBrush = new(Color.SegmentNotePreview);
    private static readonly SolidColorBrush EventPreviewBrush = new(Color.EventPreview);
    private static readonly SolidColorBrush NoteSelectedBrush = new(Color.NoteSelected);
    private static readonly SolidColorBrush TextSecondaryBrush = new(
        global::Avalonia.Media.Color.FromRgb(0xA7, 0xAF, 0xBB));
    private static readonly SolidColorBrush MarkerChipTextBrush = new(
        global::Avalonia.Media.Color.FromRgb(0xB7, 0xBF, 0xCC));
    private static readonly SolidColorBrush MarkerChipBackgroundBrush = new(
        global::Avalonia.Media.Color.FromRgb(0x0E, 0x11, 0x15));
    private static readonly Pen MarkerChipPen = new(TextTertiaryBrush, 1);
    private static readonly SolidColorBrush RedBrush = new(Color.Red);
    private static readonly SolidColorBrush PianoWhiteKeyRowBrush = new(Color.PianoWhiteKeyRow);
    private static readonly SolidColorBrush EditCursorBrush = new(Color.Info);
    private static readonly SolidColorBrush TimeRangeFillBrush = new(
        global::Avalonia.Media.Color.FromArgb(0x24, 0x62, 0xA6, 0xF6));
    private static readonly SolidColorBrush TimeRangeRulerFillBrush = new(
        global::Avalonia.Media.Color.FromArgb(0x59, 0x62, 0xA6, 0xF6));
    private static readonly SolidColorBrush MarqueeFillBrush = new(Color.MarqueeFill);
    private static readonly SolidColorBrush LaneDimBrush = new(
        global::Avalonia.Media.Color.FromArgb(0x66, 0x05, 0x06, 0x07));
    private static readonly Pen BorderPen = new(new SolidColorBrush(Color.Border), 1);
    private static readonly Pen GridPen = new(GridBrush, 1);
    private static readonly Pen BeatGridPen = new(
        new SolidColorBrush(Color.Border, 0.32),
        1);
    private static readonly Pen RulerTickPen = new(TextPrimaryBrush, 2);
    private static readonly Pen SelectedOutlinePen = new(NoteBrush, 1.5);
    private static readonly Pen HoverOutlinePen = new(TextTertiaryBrush, 1);
    private static readonly Pen CursorPen = new(RedBrush, 1);
    private static readonly Pen EditCursorPen = new(EditCursorBrush, 1)
    {
        DashStyle = new DashStyle([3, 2], 0)
    };
    private static readonly Pen TimeRangeEdgePen = new(EditCursorBrush, 1);
    private static readonly Pen MarqueePen = new(RedBrush, 1);
    private static readonly Typeface SurfaceTypeface = new(
        FontFamily.Default,
        FontStyle.Normal,
        FontWeight.Normal,
        FontStretch.Normal);
    private static readonly Typeface SurfaceSemiBoldTypeface = new(
        FontFamily.Default,
        FontStyle.Normal,
        FontWeight.SemiBold,
        FontStretch.Normal);

    private readonly Dictionary<(uint Accent, bool Selected), SolidColorBrush> _segmentFillCache = [];
    private readonly List<TimelineRenderItem> _rulerMarkerScratch = [];
    private readonly HashSet<int> _mutedLanes = [];
    private readonly HashSet<int> _soloedLanes = [];

    /// <summary>Runtime-only lane filtering: muted lanes, or non-soloed lanes while any solo is active.</summary>
    public void SetLaneMute(int lane, bool muted)
    {
        bool changed = muted ? _mutedLanes.Add(lane) : _mutedLanes.Remove(lane);
        if (changed)
        {
            InvalidateVisual();
        }
    }

    public void SetLaneSolo(int lane, bool soloed)
    {
        bool changed = soloed ? _soloedLanes.Add(lane) : _soloedLanes.Remove(lane);
        if (changed)
        {
            InvalidateVisual();
        }
    }

    public void ClearLaneStates()
    {
        if (_mutedLanes.Count == 0 && _soloedLanes.Count == 0)
        {
            return;
        }

        _mutedLanes.Clear();
        _soloedLanes.Clear();
        InvalidateVisual();
    }

    private bool IsLaneFiltered(int lane) =>
        _mutedLanes.Contains(lane) || (_soloedLanes.Count > 0 && !_soloedLanes.Contains(lane));

    /// <summary>Playback position drawn over the whole surface with a red cursor line.</summary>
    private void DrawPlaybackCursor(DrawingContext context, TimelineViewport viewport, double height)
    {
        long tick = PlaybackTick;
        if (tick < 0 || tick < viewport.StartTick || tick > viewport.EndTick)
        {
            return;
        }

        double x = SnapToDevicePixel(viewport.TickToX(tick), GetDevicePixelWidth());
        context.DrawLine(CursorPen, new Point(x, 0), new Point(x, height));
        context.FillRectangle(RedBrush, new Rect(x - 2, 0, 4, 4), 1f);
    }

    /// <summary>
    /// Edit Cursor: a blue dashed line over the content area, visually distinct from the red
    /// solid Playback Cursor and from the Time Range band (SRS 20.1.2).
    /// </summary>
    private void DrawEditCursor(DrawingContext context, TimelineViewport viewport, double height)
    {
        long tick = EditCursorTick;
        if (tick < 0 || tick < viewport.StartTick || tick > viewport.EndTick)
        {
            return;
        }

        double x = SnapToDevicePixel(viewport.TickToX(tick), GetDevicePixelWidth());
        double rulerHeight = Math.Min(RulerHeight, height);
        context.DrawLine(EditCursorPen, new Point(x, rulerHeight), new Point(x, height));
        context.FillRectangle(EditCursorBrush, new Rect(x - 2, rulerHeight, 4, 4), 1f);
    }

    /// <summary>
    /// Time Range Selection: a translucent band with edges over the ruler and the content, so it
    /// can coexist with both cursors without sharing their marker style (SRS 20.1.2/20.1.3).
    /// </summary>
    private void DrawTimeRange(
        DrawingContext context,
        TimelineViewport viewport,
        double width,
        double height)
    {
        long start = TimeRangeStartTick;
        long end = TimeRangeEndTick;
        if (start < 0 || end <= start)
        {
            return;
        }

        double left = Math.Max(0, viewport.TickToX(start));
        double right = Math.Min(width, viewport.TickToX(end));
        if (right <= left)
        {
            return;
        }

        double rulerHeight = Math.Min(RulerHeight, height);
        if (height > rulerHeight)
        {
            context.FillRectangle(
                TimeRangeFillBrush,
                new Rect(left, rulerHeight, right - left, height - rulerHeight));
        }

        context.FillRectangle(
            TimeRangeRulerFillBrush,
            new Rect(left, 0, right - left, rulerHeight));
        double devicePixel = GetDevicePixelWidth();
        double leftX = SnapToDevicePixel(left, devicePixel);
        double rightX = SnapToDevicePixel(right, devicePixel);
        context.DrawLine(TimeRangeEdgePen, new Point(leftX, 0), new Point(leftX, height));
        context.DrawLine(TimeRangeEdgePen, new Point(rightX, 0), new Point(rightX, height));
    }

    /// <summary>Conductor marker labels projected into the ruler band.</summary>
    private void DrawRulerMarkerLabels(DrawingContext context, TimelineViewport viewport, double width)
    {
        if (Source is null)
        {
            return;
        }

        _rulerMarkerScratch.Clear();
        Source.QueryInto(viewport.StartTick, viewport.EndTick, 0, 1, _rulerMarkerScratch);
        bool arrangement = SurfaceMode == TimelineSurfaceMode.Arrangement;
        foreach (TimelineRenderItem item in _rulerMarkerScratch)
        {
            if (item.Kind != TimelineItemKind.Marker || string.IsNullOrEmpty(item.Label))
            {
                continue;
            }

            double x = viewport.TickToX(item.StartTick);
            if (x < 0 || x > width)
            {
                continue;
            }

            if (!arrangement)
            {
                DrawLabel(context, item.Label, x + 3, 3, Math.Max(0, width - x - 5));
                continue;
            }

            DrawMarkerChip(context, item.Label, x, width);
        }
    }

    /// <summary>WPF arrangement ruler draws markers as bordered chips on the ruler top row.</summary>
    private void DrawMarkerChip(DrawingContext context, string label, double x, double width)
    {
        FormattedText formatted = new(
            label,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            SurfaceSemiBoldTypeface,
            9,
            MarkerChipTextBrush);
        try
        {
            double chipWidth = Math.Clamp(formatted.Width + 10, 12, 90);
            Rect chip = new(
                Math.Round(x) + 0.5,
                1.5,
                Math.Min(chipWidth, Math.Max(0, width - x)),
                15);
            if (chip.Width <= 1)
            {
                return;
            }

            context.DrawRectangle(MarkerChipBackgroundBrush, MarkerChipPen, chip, 3, 3);
            using (context.PushClip(new RoundedRect(chip)))
            {
                context.DrawText(
                    formatted,
                    new Point(chip.X + 5, chip.Y + Math.Max(0, (chip.Height - formatted.Height) / 2)));
            }
        }
        finally
        {
            (formatted as IDisposable)?.Dispose();
        }
    }

    /// <summary>Pitch-oriented modes show the highest visible pitch at the top.</summary>
    private bool UsesPitchLanes =>
        SurfaceMode is TimelineSurfaceMode.PianoRoll or TimelineSurfaceMode.Velocity;

    /// <summary>Absolute lane for a top-down row in the current mode.</summary>
    private int LaneAtRow(TimelineViewport viewport, int row) =>
        UsesPitchLanes
            ? viewport.LastLaneExclusive - 1 - row
            : viewport.FirstLane + row;

    /// <summary>Absolute lane at a content-relative Y coordinate in the current mode.</summary>
    private int LaneFromContentY(TimelineViewport viewport, double contentY) =>
        LaneAtRow(viewport, viewport.YToLane(contentY) - viewport.FirstLane);

    /// <summary>Vertical position of an absolute lane in the current mode.</summary>
    private double GetLaneTop(TimelineViewport viewport, int lane)
    {
        int row = UsesPitchLanes
            ? viewport.LastLaneExclusive - 1 - lane
            : lane - viewport.FirstLane;
        return RulerHeight + row * viewport.LaneHeight;
    }

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
