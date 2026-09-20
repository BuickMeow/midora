using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Midora.Avalonia.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Avalonia.Presentation.Controls;

public sealed class TimelineSurface : Control
{
    private const double RulerHeight = 24;
    private const double DefaultLaneHeight = 28;
    private const double MarqueeDragThreshold = 3;
    private const double GridMinimumPixelSpacing = 6;
    private const double ZoomStep = 1.25;
    private const double WheelPanFraction = 0.1;
    private const double ConductorPointRadius = 2.5;
    private const long DefaultTickSpan = 1920;
    private const int DefaultModeTicksPerQuarterNote = 480;

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

    public static readonly StyledProperty<ITimelineRenderItemSource?> SourceProperty =
        AvaloniaProperty.Register<TimelineSurface, ITimelineRenderItemSource?>(nameof(Source));

    public static readonly StyledProperty<Func<TimelineRenderItem, ITimelineSegmentPreviewSource?>?> PreviewProviderProperty =
        AvaloniaProperty.Register<TimelineSurface, Func<TimelineRenderItem, ITimelineSegmentPreviewSource?>?>(
            nameof(PreviewProvider));

    public static readonly StyledProperty<long> StartTickProperty =
        AvaloniaProperty.Register<TimelineSurface, long>(
            nameof(StartTick),
            coerce: static (_, value) => Math.Max(0, value));

    public static readonly StyledProperty<long> TickSpanProperty =
        AvaloniaProperty.Register<TimelineSurface, long>(
            nameof(TickSpan),
            defaultValue: DefaultTickSpan,
            coerce: static (_, value) => Math.Max(1, value));

    public static readonly StyledProperty<int> FirstLaneProperty =
        AvaloniaProperty.Register<TimelineSurface, int>(
            nameof(FirstLane),
            coerce: static (_, value) => Math.Max(0, value));

    public static readonly StyledProperty<double> LaneHeightProperty =
        AvaloniaProperty.Register<TimelineSurface, double>(
            nameof(LaneHeight),
            defaultValue: DefaultLaneHeight,
            coerce: static (_, value) => double.IsFinite(value) && value > 0 ? value : DefaultLaneHeight);

    public static readonly StyledProperty<bool> GridVisibleProperty =
        AvaloniaProperty.Register<TimelineSurface, bool>(nameof(GridVisible), defaultValue: true);

    public static readonly StyledProperty<long> TicksPerQuarterNoteProperty =
        AvaloniaProperty.Register<TimelineSurface, long>(
            nameof(TicksPerQuarterNote),
            defaultValue: DefaultModeTicksPerQuarterNote,
            coerce: static (_, value) => Math.Clamp(value, 1, 32_767));

    public static readonly StyledProperty<MidoraId?> SelectedIdProperty =
        AvaloniaProperty.Register<TimelineSurface, MidoraId?>(nameof(SelectedId));

    public static readonly StyledProperty<Func<int, string?>?> TrackNamesProperty =
        AvaloniaProperty.Register<TimelineSurface, Func<int, string?>?>(nameof(TrackNames));

    private readonly List<TimelineRenderItem> _visibleItems = [];
    private readonly List<TimelineRenderItem> _queryScratch = [];
    private readonly List<TimelineSegmentPreviewNote> _previewNotes = [];
    private readonly List<TimelineSegmentPreviewEvent> _previewEvents = [];
    private readonly List<TimelineGridLine> _gridLines = [];
    private ProjectTimeSignatureMap? _timeSignatureMap;
    private long _timeSignatureMapTicksPerQuarterNote = -1;
    private Point _panOrigin;
    private long _panStartTick;
    private bool _isPanning;
    private Point _dragOrigin;
    private bool _isLeftDragging;
    private bool _isMarqueeVisible;
    private Rect _marqueeRect;
    private MidoraId? _hoverItemId;
    private long _lastPointerTick = long.MinValue;
    private bool _suppressSelectionEvent;

    static TimelineSurface()
    {
        AffectsRender<TimelineSurface>(
            SourceProperty,
            PreviewProviderProperty,
            StartTickProperty,
            TickSpanProperty,
            FirstLaneProperty,
            LaneHeightProperty,
            GridVisibleProperty,
            TicksPerQuarterNoteProperty,
            SelectedIdProperty,
            TrackNamesProperty);
    }

    public TimelineSurface()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public ITimelineRenderItemSource? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public Func<TimelineRenderItem, ITimelineSegmentPreviewSource?>? PreviewProvider
    {
        get => GetValue(PreviewProviderProperty);
        set => SetValue(PreviewProviderProperty, value);
    }

    public long StartTick
    {
        get => GetValue(StartTickProperty);
        set => SetValue(StartTickProperty, value);
    }

    public long TickSpan
    {
        get => GetValue(TickSpanProperty);
        set => SetValue(TickSpanProperty, value);
    }

    public int FirstLane
    {
        get => GetValue(FirstLaneProperty);
        set => SetValue(FirstLaneProperty, value);
    }

    public double LaneHeight
    {
        get => GetValue(LaneHeightProperty);
        set => SetValue(LaneHeightProperty, value);
    }

    public bool GridVisible
    {
        get => GetValue(GridVisibleProperty);
        set => SetValue(GridVisibleProperty, value);
    }

    public long TicksPerQuarterNote
    {
        get => GetValue(TicksPerQuarterNoteProperty);
        set => SetValue(TicksPerQuarterNoteProperty, value);
    }

    public MidoraId? SelectedId
    {
        get => GetValue(SelectedIdProperty);
        set => SetValue(SelectedIdProperty, value);
    }

    public Func<int, string?>? TrackNames
    {
        get => GetValue(TrackNamesProperty);
        set => SetValue(TrackNamesProperty, value);
    }

    public event EventHandler<long>? PointerTickChanged;

    public event EventHandler<TimelineRenderItem?>? SelectionChanged;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? 360 : availableSize.Height;
        return new Size(Math.Max(0, width), Math.Max(0, height));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double width = Bounds.Width;
        double height = Bounds.Height;
        context.FillRectangle(SurfaceBrush, new Rect(0, 0, Math.Max(0, width), Math.Max(0, height)), 1f);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        double rulerHeight = Math.Min(RulerHeight, height);
        context.FillRectangle(LaneAlternateBrush, new Rect(0, 0, width, rulerHeight), 1f);
        context.DrawLine(
            BorderPen,
            new Point(0, Math.Round(rulerHeight) + 0.5),
            new Point(width, Math.Round(rulerHeight) + 0.5));

        if (!TryCreateViewport(out TimelineViewport viewport))
        {
            return;
        }

        DrawLaneBackgrounds(context, viewport, width, height);
        if (GridVisible)
        {
            DrawGrid(context, viewport, width, height);
        }

        DrawTrackNames(context, viewport, width);
        if (Source is not null)
        {
            DrawItems(context, viewport, height);
        }

        DrawEditCursor(context, viewport, height);
        DrawMarquee(context, width, height);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SelectedIdProperty || _suppressSelectionEvent)
        {
            return;
        }

        TimelineRenderItem? item = null;
        if (SelectedId is MidoraId id
            && Source is not null
            && Source.TryGetById(id, out TimelineRenderItem resolved))
        {
            item = resolved;
        }

        SelectionChanged?.Invoke(this, item);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        PointerPoint point = e.GetCurrentPoint(this);
        if (point.Properties.IsMiddleButtonPressed)
        {
            _isPanning = true;
            _panOrigin = point.Position;
            _panStartTick = StartTick;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isLeftDragging = true;
        _dragOrigin = point.Position;
        _isMarqueeVisible = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point position = e.GetPosition(this);
        if (_isPanning)
        {
            if (TryCreateViewport(out TimelineViewport panViewport)
                && panViewport.PixelsPerTick > 0)
            {
                long delta = TimelineTickMath.RoundSignedDistance(
                    (_panOrigin.X - position.X) / panViewport.PixelsPerTick);
                SetCurrentValue(StartTickProperty, TimelineTickMath.Pan(_panStartTick, delta));
            }

            RaisePointerTickChanged(position);
            return;
        }

        if (_isLeftDragging)
        {
            double dx = position.X - _dragOrigin.X;
            double dy = position.Y - _dragOrigin.Y;
            if (!_isMarqueeVisible
                && dx * dx + dy * dy >= MarqueeDragThreshold * MarqueeDragThreshold)
            {
                _isMarqueeVisible = true;
            }

            if (_isMarqueeVisible)
            {
                _marqueeRect = NormalizeRect(_dragOrigin, position);
                InvalidateVisual();
            }

            RaisePointerTickChanged(position);
            return;
        }

        UpdateHover(position);
        RaisePointerTickChanged(position);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isPanning && e.InitialPressMouseButton == MouseButton.Middle)
        {
            _isPanning = false;
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (!_isLeftDragging || e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        Point position = e.GetPosition(this);
        _isLeftDragging = false;
        bool wasMarquee = _isMarqueeVisible;
        _isMarqueeVisible = false;
        e.Pointer.Capture(null);
        if (wasMarquee)
        {
            ApplyMarqueeSelection(_marqueeRect);
        }
        else
        {
            ApplyClickSelection(position);
        }

        _marqueeRect = default;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _isPanning = false;
        _isLeftDragging = false;
        _isMarqueeVisible = false;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoverItemId is null)
        {
            return;
        }

        _hoverItemId = null;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        double delta = e.Delta.Y;
        if (!double.IsFinite(delta) || delta == 0)
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            ZoomAt(e.GetPosition(this), delta);
        }
        else
        {
            PanByWheel(delta);
        }

        e.Handled = true;
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
            nextLabelX = x + DrawLabel(context, label, x + 2, 4, width - x - 4) + 8;
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
        Rect bounds = new(left, laneTop, Math.Max(1, right - left), viewport.LaneHeight);
        context.DrawRectangle(
            selected ? SelectedSegmentBrush : SegmentBrush,
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

    private void PanByWheel(double delta)
    {
        long step = Math.Max(1, (long)Math.Round(Math.Max(1, TickSpan) * WheelPanFraction));
        long direction = delta > 0 ? -1 : 1;
        SetCurrentValue(StartTickProperty, TimelineTickMath.Pan(StartTick, direction * step));
    }

    private void ZoomAt(Point position, double delta)
    {
        if (!TryCreateViewport(out TimelineViewport viewport))
        {
            return;
        }

        long currentSpan = viewport.TickLength;
        double factor = delta > 0 ? 1 / ZoomStep : ZoomStep;
        long minimumSpan = Math.Max(1, TicksPerQuarterNote / 8);
        long maximumSpan = ComputeMaximumZoomSpan(minimumSpan);
        long newSpan = Math.Clamp(
            TimelineTickMath.ScaleSpan(currentSpan, factor),
            minimumSpan,
            maximumSpan);
        if (newSpan == currentSpan)
        {
            return;
        }

        long anchorTick = viewport.XToTick(position.X);
        double ratio = (double)(anchorTick - viewport.StartTick) / currentSpan;
        long newStart = TimelineTickMath.RoundSignedDistance(anchorTick - ratio * newSpan);
        newStart = Math.Clamp(newStart, 0, long.MaxValue - newSpan - 1);
        SetCurrentValue(TickSpanProperty, newSpan);
        SetCurrentValue(StartTickProperty, newStart);
        InvalidateVisual();
    }

    private long ComputeMaximumZoomSpan(long minimumSpan)
    {
        long maximumEnd = Source?.MaximumEndTick ?? 0;
        if (maximumEnd <= 0)
        {
            return Math.Max(minimumSpan, TickSpan);
        }

        long doubled = maximumEnd > long.MaxValue / 2 ? long.MaxValue / 2 : maximumEnd * 2;
        return Math.Max(minimumSpan, doubled);
    }

    private void UpdateHover(Point position)
    {
        MidoraId? hover = TryHitTest(position, out TimelineRenderItem item) ? item.Id : null;
        if (_hoverItemId == hover)
        {
            return;
        }

        _hoverItemId = hover;
        InvalidateVisual();
    }

    private void RaisePointerTickChanged(Point position)
    {
        if (!TryCreateViewport(out TimelineViewport viewport))
        {
            return;
        }

        long tick = viewport.XToTick(position.X);
        if (tick == _lastPointerTick)
        {
            return;
        }

        _lastPointerTick = tick;
        PointerTickChanged?.Invoke(this, tick);
    }

    private bool TryHitTest(Point position, out TimelineRenderItem item)
    {
        item = default;
        if (Source is null || !TryCreateViewport(out TimelineViewport viewport))
        {
            return false;
        }

        double contentY = position.Y - RulerHeight;
        if (position.X < 0
            || position.X > viewport.Width
            || contentY < 0
            || contentY >= viewport.LaneCount * viewport.LaneHeight)
        {
            return false;
        }

        int lane = viewport.YToLane(contentY);
        long tick = viewport.XToContainingTick(position.X);
        _queryScratch.Clear();
        Source.QueryInto(tick, tick + 1, lane, lane + 1, _queryScratch);
        return TryChooseTopmost(static _ => true, out item);
    }

    private void ApplyClickSelection(Point position)
    {
        SelectItem(TryHitTest(position, out TimelineRenderItem item) ? item : null);
    }

    private void ApplyMarqueeSelection(Rect rectangle)
    {
        if (Source is null || !TryCreateViewport(out TimelineViewport viewport))
        {
            SelectItem(null);
            return;
        }

        long startTick = viewport.XToTick(rectangle.Left);
        long endTick = viewport.XToContainingTick(rectangle.Right);
        if (endTick <= startTick)
        {
            endTick = startTick + 1;
        }

        double topContent = Math.Max(RulerHeight, rectangle.Top);
        double bottomContent = Math.Min(
            RulerHeight + viewport.LaneCount * viewport.LaneHeight,
            rectangle.Bottom);
        if (bottomContent <= topContent)
        {
            SelectItem(null);
            return;
        }

        int firstLane = viewport.YToLane(topContent - RulerHeight);
        int lastLane = viewport.YToLane(bottomContent - RulerHeight - double.Epsilon);
        int lastLaneExclusive = Math.Min(viewport.LastLaneExclusive, lastLane + 1);
        _queryScratch.Clear();
        Source.QueryInto(startTick, endTick, firstLane, lastLaneExclusive, _queryScratch);
        bool Intersects(TimelineRenderItem candidate) =>
            candidate.EndTick > startTick && candidate.StartTick < endTick;
        SelectItem(TryChooseTopmost(Intersects, out TimelineRenderItem item) ? item : null);
    }

    private bool TryChooseTopmost(
        Func<TimelineRenderItem, bool> predicate,
        out TimelineRenderItem item)
    {
        item = default;
        bool found = false;
        foreach (TimelineRenderItem candidate in _queryScratch)
        {
            if (candidate.State.HasFlag(TimelineItemState.HitTestDisabled) || !predicate(candidate))
            {
                continue;
            }

            if (!found || CompareHitOrder(candidate, item) < 0)
            {
                item = candidate;
                found = true;
            }
        }

        return found;
    }

    private static int CompareHitOrder(in TimelineRenderItem left, in TimelineRenderItem right)
    {
        int byZ = right.ZIndex.CompareTo(left.ZIndex);
        if (byZ != 0)
        {
            return byZ;
        }

        long leftLength = left.EndTick - left.StartTick;
        long rightLength = right.EndTick - right.StartTick;
        int byLength = leftLength.CompareTo(rightLength);
        return byLength != 0 ? byLength : left.Id.CompareTo(right.Id);
    }

    private void SelectItem(TimelineRenderItem? item)
    {
        MidoraId? id = item?.Id;
        if (SelectedId == id)
        {
            return;
        }

        _suppressSelectionEvent = true;
        try
        {
            SetCurrentValue(SelectedIdProperty, id);
        }
        finally
        {
            _suppressSelectionEvent = false;
        }

        SelectionChanged?.Invoke(this, item);
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
