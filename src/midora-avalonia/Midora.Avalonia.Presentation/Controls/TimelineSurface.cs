using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Midora.Avalonia.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Avalonia.Presentation.Controls;

public sealed partial class TimelineSurface : Control
{
    private const double DefaultRulerHeight = 24;
    private const double ArrangementRulerHeight = 32;
    private double RulerHeight => SurfaceMode == TimelineSurfaceMode.Arrangement
        ? ArrangementRulerHeight
        : DefaultRulerHeight;
    private const double DefaultLaneHeight = 28;
    private const double MarqueeDragThreshold = 3;
    private const double GridMinimumPixelSpacing = 6;
    private const double ZoomStep = 1.25;
    private const double WheelPanFraction = 0.1;
    private const double ConductorPointRadius = 2.5;    private const long DefaultTickSpan = 1920;
    private const int DefaultModeTicksPerQuarterNote = 480;

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
            ShowTrackNamesProperty,
            PlaybackTickProperty,
            TicksPerQuarterNoteProperty,
            SelectedIdProperty,
            TrackNamesProperty,
            SurfaceModeProperty,
            FirstPitchProperty,
            PitchCountProperty,
            ValueMinimumProperty,
            ValueMaximumProperty);
    }

    public TimelineSurface()
    {
        ClipToBounds = true;
        Focusable = true;
    }

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

    /// <summary>Draws lane names inside the content; hosts with a header column set this false.</summary>
    public static readonly StyledProperty<bool> ShowTrackNamesProperty =
        AvaloniaProperty.Register<TimelineSurface, bool>(nameof(ShowTrackNames), defaultValue: true);

    /// <summary>Playback position drawn as a red cursor line; -1 hides it.</summary>
    public static readonly StyledProperty<long> PlaybackTickProperty =
        AvaloniaProperty.Register<TimelineSurface, long>(nameof(PlaybackTick), -1);

    public static readonly StyledProperty<long> TicksPerQuarterNoteProperty =
        AvaloniaProperty.Register<TimelineSurface, long>(
            nameof(TicksPerQuarterNote),
            defaultValue: DefaultModeTicksPerQuarterNote,
            coerce: static (_, value) => Math.Clamp(value, 1, 32_767));

    public static readonly StyledProperty<MidoraId?> SelectedIdProperty =
        AvaloniaProperty.Register<TimelineSurface, MidoraId?>(nameof(SelectedId));

    public static readonly StyledProperty<Func<int, string?>?> TrackNamesProperty =
        AvaloniaProperty.Register<TimelineSurface, Func<int, string?>?>(nameof(TrackNames));

    public static readonly StyledProperty<TimelineSurfaceMode> SurfaceModeProperty =
        AvaloniaProperty.Register<TimelineSurface, TimelineSurfaceMode>(
            nameof(SurfaceMode),
            defaultValue: TimelineSurfaceMode.Arrangement);

    public static readonly StyledProperty<int> LaneCountProperty =
        AvaloniaProperty.Register<TimelineSurface, int>(nameof(LaneCount), 1);

    private static readonly StyledProperty<int> VisibleLaneCountProperty =
        AvaloniaProperty.Register<TimelineSurface, int>(nameof(VisibleLaneCount));

    private static readonly StyledProperty<int> MaximumFirstLaneProperty =
        AvaloniaProperty.Register<TimelineSurface, int>(nameof(MaximumFirstLane));

    private static readonly StyledProperty<long> ExtentEndTickProperty =
        AvaloniaProperty.Register<TimelineSurface, long>(nameof(ExtentEndTick));

    private static readonly StyledProperty<long> MaximumStartTickProperty =
        AvaloniaProperty.Register<TimelineSurface, long>(nameof(MaximumStartTick));

    public int LaneCount
    {
        get => GetValue(LaneCountProperty);
        set => SetValue(LaneCountProperty, value);
    }

    public int VisibleLaneCount => GetValue(VisibleLaneCountProperty);

    public int MaximumFirstLane => GetValue(MaximumFirstLaneProperty);

    public long ExtentEndTick => GetValue(ExtentEndTickProperty);

    public long MaximumStartTick => GetValue(MaximumStartTickProperty);

    public static readonly StyledProperty<int> FirstPitchProperty =
        AvaloniaProperty.Register<TimelineSurface, int>(nameof(FirstPitch), defaultValue: 48);

    public static readonly StyledProperty<int> PitchCountProperty =
        AvaloniaProperty.Register<TimelineSurface, int>(nameof(PitchCount), defaultValue: 36);

    public static readonly StyledProperty<double> ValueMinimumProperty =
        AvaloniaProperty.Register<TimelineSurface, double>(nameof(ValueMinimum), defaultValue: 0d);

    public static readonly StyledProperty<double> ValueMaximumProperty =
        AvaloniaProperty.Register<TimelineSurface, double>(nameof(ValueMaximum), defaultValue: 127d);

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

    public bool ShowTrackNames
    {
        get => GetValue(ShowTrackNamesProperty);
        set => SetValue(ShowTrackNamesProperty, value);
    }

    public long PlaybackTick
    {
        get => GetValue(PlaybackTickProperty);
        set => SetValue(PlaybackTickProperty, value);
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

    public TimelineSurfaceMode SurfaceMode
    {
        get => GetValue(SurfaceModeProperty);
        set => SetValue(SurfaceModeProperty, value);
    }

    public int FirstPitch
    {
        get => GetValue(FirstPitchProperty);
        set => SetValue(FirstPitchProperty, value);
    }

    public int PitchCount
    {
        get => GetValue(PitchCountProperty);
        set => SetValue(PitchCountProperty, value);
    }

    public double ValueMinimum
    {
        get => GetValue(ValueMinimumProperty);
        set => SetValue(ValueMinimumProperty, value);
    }

    public double ValueMaximum
    {
        get => GetValue(ValueMaximumProperty);
        set => SetValue(ValueMaximumProperty, value);
    }

    public event EventHandler<long>? PointerTickChanged;

    public event EventHandler<TimelineRenderItem?>? SelectionChanged;

    public event EventHandler<int>? LaneActivated;

    /// <summary>Raised when an arrangement Segment is double-clicked (lane, segment start tick).</summary>
    public event EventHandler<TimelineSegmentActivation>? SegmentActivated;

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

        if (SurfaceMode is not (TimelineSurfaceMode.Arrangement or TimelineSurfaceMode.General))
        {
            RenderModeSurface(context, width, height);
            UpdateViewportMetrics();
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
        FlushShapes(context);

        DrawRulerMarkerLabels(context, viewport, width);
        if (ShowTrackNames)
        {
            DrawTrackNames(context, viewport, width);
        }
        if (Source is not null)
        {
            DrawItems(context, viewport, height);
        }
        FlushShapes(context);
        if (Source is not null)
        {
            DrawSegmentPreviewDeferred(context, viewport);
        }

        DrawEditCursor(context, viewport, height);
        DrawPlaybackCursor(context, viewport, height);
        DrawMarquee(context, width, height);
        UpdateViewportMetrics();
    }

    /// <summary>
    /// Publishes the read-only scroll metrics consumed by the lane and time scrollbars.
    /// Mirrors the WPF surface's vertical/horizontal viewport metric refresh.
    /// </summary>
    private void UpdateViewportMetrics()    {
        long extent = Math.Max(0, Source?.MaximumEndTick ?? 0);
        SetCurrentValue(ExtentEndTickProperty, extent);
        long span = Math.Clamp(TickSpan, 1, long.MaxValue / 2);
        SetCurrentValue(MaximumStartTickProperty, Math.Max(0, extent - span));
        if (!TryCreateViewport(out TimelineViewport viewport))
        {
            return;
        }

        SetCurrentValue(VisibleLaneCountProperty, viewport.LaneCount);
        SetCurrentValue(
            MaximumFirstLaneProperty,
            Math.Max(0, Math.Max(1, LaneCount) - viewport.LaneCount));
    }

    private void RenderModeSurface(DrawingContext context, double width, double height)
    {
        if (!TryCreateViewport(out TimelineViewport viewport))
        {
            return;
        }

        double rulerHeight = Math.Min(RulerHeight, height);
        context.FillRectangle(LaneAlternateBrush, new Rect(0, 0, width, rulerHeight), 1f);
        context.DrawLine(
            BorderPen,
            new Point(0, Math.Round(rulerHeight) + 0.5),
            new Point(width, Math.Round(rulerHeight) + 0.5));
        if (GridVisible)
        {
            DrawGrid(context, viewport, width, height);
        }
        FlushShapes(context);

        DrawRulerMarkerLabels(context, viewport, width);

        switch (SurfaceMode)
        {
            case TimelineSurfaceMode.PianoRoll:
                DrawPianoRollSurface(context, viewport, width, height);
                break;
            case TimelineSurfaceMode.Velocity:
                DrawVelocitySurface(context, viewport, width, height);
                break;
            case TimelineSurfaceMode.EventLanes:
                DrawEventLaneSurface(context, viewport, width, height);
                break;
            case TimelineSurfaceMode.Conductor:
                DrawConductorSurface(context, viewport, width, height);
                break;
        }

        DrawPlaybackCursor(context, viewport, height);
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
        if (OnEditingPointerPressed(e, point))
        {
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 2)
        {
            RaiseActivation(point.Position);
        }

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

    private void RaiseActivation(Point position)
    {
        // Lane/segment activation is an Arrangement concept; pitch/event modes do not raise it.
        if (SurfaceMode is not (TimelineSurfaceMode.Arrangement or TimelineSurfaceMode.General))
        {
            return;
        }

        if (!TryCreateViewport(out TimelineViewport viewport))
        {
            return;
        }

        double contentY = position.Y - RulerHeight;
        if (position.X < 0
            || position.X > viewport.Width
            || contentY < 0
            || contentY >= viewport.LaneCount * viewport.LaneHeight)
        {
            return;
        }

        if (TryHitTest(position, out TimelineRenderItem item)
            && item.Kind == TimelineItemKind.Segment)
        {
            SegmentActivated?.Invoke(this, new TimelineSegmentActivation(item.Lane, item.StartTick));
            return;
        }

        int lane = viewport.YToLane(contentY);
        if (lane >= 1)
        {
            LaneActivated?.Invoke(this, lane);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point position = e.GetPosition(this);
        if (OnEditingPointerMoved(e, position))
        {
            return;
        }

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

        if (OnEditingPointerReleased(e, e.GetPosition(this)))
        {
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
        RaisePointerTickChanged(default, isInside: false);
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
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && UsesPitchLanes)
        {
            ScrollLanes(delta);
        }
        else
        {
            PanByWheel(delta);
        }

        e.Handled = true;
    }

    /// <summary>Shift+wheel scrolls the pitch window (pitch-oriented modes).</summary>
    private void ScrollLanes(double delta)
    {
        if (!TryCreateViewport(out TimelineViewport viewport))
        {
            return;
        }

        int step = Math.Max(1, (int)Math.Round(3 * delta));
        int maximumFirstLane = Math.Max(0, Math.Max(1, LaneCount) - viewport.LaneCount);
        SetCurrentValue(
            FirstLaneProperty,
            Math.Clamp(viewport.FirstLane - step, 0, maximumFirstLane));
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

    private void RaisePointerTickChanged(Point position, bool isInside = true)
    {
        if (!isInside)
        {
            if (_lastPointerTick == -1)
            {
                return;
            }

            _lastPointerTick = -1;
            PointerTickChanged?.Invoke(this, -1);
            return;
        }

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

        int lane = LaneFromContentY(viewport, contentY);
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

        int firstLane = LaneFromContentY(viewport, topContent - RulerHeight);
        int lastLane = LaneFromContentY(viewport, bottomContent - RulerHeight - double.Epsilon);
        int rangeStart = Math.Max(viewport.FirstLane, Math.Min(firstLane, lastLane));
        int rangeEnd = Math.Min(viewport.LastLaneExclusive - 1, Math.Max(firstLane, lastLane));
        if (rangeEnd < rangeStart)
        {
            SelectItem(null);
            return;
        }

        int lastLaneExclusive = rangeEnd + 1;
        _queryScratch.Clear();
        Source.QueryInto(startTick, endTick, rangeStart, lastLaneExclusive, _queryScratch);
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
}
