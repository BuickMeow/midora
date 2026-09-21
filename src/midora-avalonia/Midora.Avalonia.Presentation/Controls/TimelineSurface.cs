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
    private readonly TimelineRulerGesture _rulerGesture = new();
    private double _horizontalWheelTicksResidual;
    private double _laneWheelResidual;
    private double _zoomWheelResidual;

    /// <summary>
    /// Per-event noise floor for wheel deltas. Trackpads report a small cross-axis component on
    /// every event; without a floor it would accumulate into drift on the other axis.
    /// </summary>
    private const double WheelNoiseFloor = 0.05;

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
            EditCursorTickProperty,
            TimeRangeStartTickProperty,
            TimeRangeEndTickProperty,
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
        // Touch/pen pinch (and Windows precision touchpads) arrive through the gesture recognizer.
        // The macOS backend has no magnify support, so Ctrl+wheel remains the trackpad zoom path
        // mandated by SRS 20.1.5.
        GestureRecognizers.Add(new PinchGestureRecognizer());
        AddHandler(InputElement.PinchEvent, OnPinchGesture);
        // macOS and Windows precision touchpads report a relative magnification per event.
        AddHandler(InputElement.PointerTouchPadGestureMagnifyEvent, OnTouchPadMagnify);
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

    /// <summary>Edit position drawn as a blue dashed cursor line; -1 hides it (SRS 20.1.2).</summary>
    public static readonly StyledProperty<long> EditCursorTickProperty =
        AvaloniaProperty.Register<TimelineSurface, long>(nameof(EditCursorTick), -1);

    /// <summary>Time Range Selection start tick; -1 hides the range (SRS 20.1.2).</summary>
    public static readonly StyledProperty<long> TimeRangeStartTickProperty =
        AvaloniaProperty.Register<TimelineSurface, long>(nameof(TimeRangeStartTick), -1);

    /// <summary>Time Range Selection end tick (exclusive); -1 hides the range.</summary>
    public static readonly StyledProperty<long> TimeRangeEndTickProperty =
        AvaloniaProperty.Register<TimelineSurface, long>(nameof(TimeRangeEndTick), -1);

    /// <summary>
    /// Effective operation step used to snap the Playback Cursor, the Edit Cursor and the Time
    /// Range start/length (SRS 20.1.4); 1 when Snap is disabled.
    /// </summary>
    public static readonly StyledProperty<long> OperationStepTicksProperty =
        AvaloniaProperty.Register<TimelineSurface, long>(
            nameof(OperationStepTicks),
            defaultValue: 1,
            coerce: static (_, value) => Math.Max(1, value));

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

    public long EditCursorTick
    {
        get => GetValue(EditCursorTickProperty);
        set => SetValue(EditCursorTickProperty, value);
    }

    public long TimeRangeStartTick
    {
        get => GetValue(TimeRangeStartTickProperty);
        set => SetValue(TimeRangeStartTickProperty, value);
    }

    public long TimeRangeEndTick
    {
        get => GetValue(TimeRangeEndTickProperty);
        set => SetValue(TimeRangeEndTickProperty, value);
    }

    public long OperationStepTicks
    {
        get => GetValue(OperationStepTicksProperty);
        set => SetValue(OperationStepTicksProperty, value);
    }

    public event EventHandler<long>? PointerTickChanged;

    /// <summary>Raised when the ruler asks to move the Playback Cursor (SRS 20.1.3).</summary>
    public event EventHandler<long>? PlaybackCursorRequested;

    /// <summary>Raised when the ruler or an empty content click asks to move the Edit Cursor.</summary>
    public event EventHandler<long>? EditCursorRequested;

    /// <summary>Raised when a ruler drag completes a Time Range Selection.</summary>
    public event EventHandler<TimelineTimeRangeEventArgs>? TimeRangeSelected;

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

    private int _renderFrameCount;
    private double _renderTotalMilliseconds;
    private double _renderMaximumMilliseconds;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        long renderStart = Environment.TickCount64;

        double width = Bounds.Width;
        double height = Bounds.Height;
        context.FillRectangle(SurfaceBrush, new Rect(0, 0, Math.Max(0, width), Math.Max(0, height)), 1f);
        if (width <= 0 || height <= 0)
        {
            TraceRenderFrame(renderStart);
            return;
        }

        if (SurfaceMode is not (TimelineSurfaceMode.Arrangement or TimelineSurfaceMode.General))
        {
            RenderModeSurface(context, width, height);
            UpdateViewportMetrics();
            TraceRenderFrame(renderStart);
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
            TraceRenderFrame(renderStart);
            return;
        }

        DrawLaneBackgrounds(context, viewport, width, height);
        // Lane/key backgrounds are the base layer: they must be painted before the grid so the
        // vertical bar and beat lines stay visible on white-key rows (SRS 18.1.7).
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
            FlushShapes(context);
        }

        DrawTimeRange(context, viewport, width, height);
        DrawEditCursor(context, viewport, height);
        DrawPlaybackCursor(context, viewport, height);
        DrawMarquee(context, width, height);
        UpdateViewportMetrics();
        TraceRenderFrame(renderStart);
    }

    /// <summary>
    /// Review-only frame cost trace (MIDORA_TIMELINE_TRACE=1): reports the average and worst
    /// render duration every 60 frames so preview/tile regressions stay measurable.
    /// </summary>
    private void TraceRenderFrame(long renderStart)
    {
        if (Environment.GetEnvironmentVariable("MIDORA_TIMELINE_TRACE") != "1")
        {
            return;
        }

        double elapsed = Environment.TickCount64 - renderStart;
        _renderFrameCount++;
        _renderTotalMilliseconds += elapsed;
        _renderMaximumMilliseconds = Math.Max(_renderMaximumMilliseconds, elapsed);
        if (_renderFrameCount % 60 != 0)
        {
            return;
        }

        Console.Out.WriteLine(
            $"MIDORA-RENDER frames={_renderFrameCount} avg={_renderTotalMilliseconds / _renderFrameCount:F2} ms max={_renderMaximumMilliseconds:F0} ms");
        Console.Out.Flush();
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

        if (point.Properties.IsLeftButtonPressed && IsRulerInteractive(point.Position))
        {
            // The Ctrl state and the pointer origin are frozen at pointer down (SRS 20.1.3).
            _rulerGesture.Begin(
                point.Position.X,
                point.Position.Y,
                RulerTick(point.Position),
                e.KeyModifiers.HasFlag(KeyModifiers.Control));
            e.Pointer.Capture(this);
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

        if (_rulerGesture.IsActive)
        {
            _rulerGesture.Move(
                position.X,
                position.Y,
                RulerTick(position),
                MarqueeDragThreshold);
            InvalidateVisual();
            RaisePointerTickChanged(position);
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
        if (_rulerGesture.IsActive && e.InitialPressMouseButton == MouseButton.Left)
        {
            // Complete before releasing the capture: releasing it raises PointerCaptureLost, which
            // cancels any still-active gesture.
            TimelineRulerGestureResult result = _rulerGesture.Complete(
                SnapTick(RulerTick(e.GetPosition(this))));
            e.Pointer.Capture(null);
            ApplyRulerGestureResult(result);
            e.Handled = true;
            return;
        }

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
        _rulerGesture.Cancel();
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
        double deltaX = e.Delta.X;
        double deltaY = e.Delta.Y;
        bool hasX = double.IsFinite(deltaX) && deltaX != 0;
        bool hasY = double.IsFinite(deltaY) && deltaY != 0;
        if (!hasX && !hasY)
        {
            return;
        }

        // SRS 20.1.5 keeps Ctrl+Wheel horizontal zoom, Shift+Wheel horizontal scroll and
        // middle-button pan. A trackpad reports both axes at once, so the remaining wheel
        // deltas pan the timeline freely in both directions instead of discarding Delta.X.
        if (hasY && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            ZoomAt(e.GetPosition(this), deltaY);
            e.Handled = true;
            return;
        }

        if (hasY && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (UsesPitchLanes)
            {
                ScrollLanes(deltaY);
            }
            else
            {
                PanHorizontally(-deltaY);
            }

            e.Handled = true;
            return;
        }

        if (hasX && Math.Abs(deltaX) > WheelNoiseFloor)
        {
            PanHorizontally(deltaX);
        }

        if (hasY && Math.Abs(deltaY) > WheelNoiseFloor)
        {
            PanVertically(deltaY);
        }

        e.Handled = true;
    }

    /// <summary>
    /// Horizontal wheel/trackpad delta. Positive deltas scroll toward earlier ticks, matching the
    /// WPF reference and the vertical convention.
    /// </summary>
    private void PanHorizontally(double delta)
    {
        // Continuous panning: the wheel fraction of the visible span is converted to ticks and
        // accumulated, so scrolling moves by individual ticks instead of quantized span steps.
        long span = Math.Max(1, TickSpan);
        _horizontalWheelTicksResidual += -delta * span * WheelPanFraction;
        long whole = (long)Math.Truncate(_horizontalWheelTicksResidual);
        if (whole == 0)
        {
            return;
        }

        _horizontalWheelTicksResidual -= whole;
        SetCurrentValue(StartTickProperty, TimelineTickMath.Pan(StartTick, whole));
    }

    /// <summary>Vertical wheel/trackpad delta: scrolls lanes, or the value window in value modes.</summary>
    private void PanVertically(double delta)
    {
        if (!TryCreateViewport(out TimelineViewport viewport))
        {
            return;
        }

        if (SurfaceMode is TimelineSurfaceMode.EventLanes or TimelineSurfaceMode.Velocity)
        {
            // The value lanes currently render the fixed ValueMinimum..ValueMaximum window, so
            // there is no vertical viewport to scroll yet (SRS 20.1.5 value-axis scrolling).
            return;
        }

        // Three lanes per wheel notch, accumulated across sub-unit trackpad deltas so a small
        // vertical component cannot be forced into an upward step (SRS 20.1.5 panning).
        _laneWheelResidual += delta * 3;
        int whole = (int)Math.Truncate(_laneWheelResidual);
        if (whole == 0)
        {
            return;
        }

        _laneWheelResidual -= whole;
        int maximumFirstLane = Math.Max(0, Math.Max(1, LaneCount) - viewport.LaneCount);
        // Pitch lanes are bottom-up, so the same wheel direction must move the opposite way to
        // match what the row order shows.
        int laneDelta = UsesPitchLanes ? whole : -whole;
        SetCurrentValue(
            FirstLaneProperty,
            Math.Clamp(viewport.FirstLane + laneDelta, 0, maximumFirstLane));
    }

    /// <summary>
    /// The ruler band of every tick timeline except the bottom value lanes, which do not create
    /// a Time Range Selection (SRS 20.1.3).
    /// </summary>
    private bool IsRulerInteractive(Point position) =>
        SurfaceMode is not (TimelineSurfaceMode.EventLanes or TimelineSurfaceMode.Velocity)
        && position.Y >= 0
        && position.Y < RulerHeight
        && position.X >= 0
        && position.X <= Bounds.Width;

    private void ApplyRulerGestureResult(TimelineRulerGestureResult result)
    {
        switch (result.Kind)
        {
            case TimelineRulerGestureKind.Seek:
                PlaybackCursorRequested?.Invoke(this, result.Tick);
                break;
            case TimelineRulerGestureKind.EditCursor:
                SetCurrentValue(EditCursorTickProperty, result.Tick);
                EditCursorRequested?.Invoke(this, result.Tick);
                break;
            case TimelineRulerGestureKind.TimeRange:
                SetCurrentValue(TimeRangeStartTickProperty, result.StartTick);
                SetCurrentValue(TimeRangeEndTickProperty, result.EndTick);
                TimeRangeSelected?.Invoke(
                    this,
                    new TimelineTimeRangeEventArgs(result.StartTick, result.EndTick));
                break;
        }

        InvalidateVisual();
    }

    private long RulerTick(Point position)
    {
        if (!TryCreateViewport(out TimelineViewport viewport))
        {
            return 0;
        }

        return Math.Max(0, viewport.XToTick(position.X));
    }

    /// <summary>Snaps a tick to the effective operation step (SRS 20.1.4).</summary>
    private long SnapTick(long tick)
    {
        long step = Math.Max(1, OperationStepTicks);
        if (step <= 1)
        {
            return Math.Max(0, tick);
        }

        return Math.Max(0, (long)Math.Round(tick / (double)step, MidpointRounding.AwayFromZero) * step);
    }

    private static (long Start, long End) NormalizeTickRange(long left, long right)
    {
        long start = Math.Min(left, right);
        long end = Math.Max(left, right);
        if (end <= start)
        {
            end = start + 1;
        }

        return (start, end);
    }

    /// <summary>
    /// Trackpad pinch zoom. SRS 20.1.5 defines Ctrl+Wheel zoom; pinch mirrors it so the pointer
    /// anchor, bounds and the shared ruler/lane/canvas viewport stay identical.
    /// </summary>
    private void OnPinchGesture(object? sender, PinchEventArgs e)
    {
        if (ApplyPinchZoom(e.Scale, e.ScaleOrigin))
        {
            e.Handled = true;
        }
    }

    private void OnTouchPadMagnify(object? sender, PointerDeltaEventArgs e)
    {
        double magnitude = e.Delta.X != 0 ? e.Delta.X : e.Delta.Y;
        if (ApplyMagnifyDelta(magnitude, e.GetPosition(this)))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Applies a relative touchpad magnification delta (NSEvent.magnification style) anchored at
    /// the gesture origin. A full pinch reports roughly +-1.0 in total, so each event only zooms a
    /// few percent and the motion stays smooth.
    /// </summary>
    public bool ApplyMagnifyDelta(double magnitude, Point origin)
    {
        if (!double.IsFinite(magnitude) || Math.Abs(magnitude) < 1e-4)
        {
            return false;
        }

        ZoomBy(origin, Math.Pow(ZoomStep, -magnitude * 2));
        return true;
    }

    /// <summary>
    /// Applies a trackpad pinch to the shared time viewport. Returns false when the reported scale
    /// carries no zoom, so the caller can leave the event unhandled.
    /// </summary>
    public bool ApplyPinchZoom(double scale, Point origin)
    {
        if (!double.IsFinite(scale) || scale <= 0 || Math.Abs(scale - 1) < 1e-6)
        {
            return false;
        }

        // Use the reported scale so the gesture tracks the fingers, clamped against a single
        // extreme event (SRS 20.1.5 zoom bounds still apply inside ZoomBy).
        double factor = 1 / Math.Clamp(scale, 0.2, 5);
        ZoomBy(origin, factor);
        return true;
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

    private void ZoomAt(Point position, double delta)
    {
        // Continuous zoom for trackpads: accumulate the wheel delta and apply it as a power of the
        // zoom step, so a slow two-finger scroll zooms smoothly instead of 1.25x per event.
        _zoomWheelResidual += delta;
        double whole = Math.Truncate(_zoomWheelResidual);
        if (whole == 0)
        {
            return;
        }

        _zoomWheelResidual -= whole;
        ZoomBy(position, Math.Pow(ZoomStep, -whole));
    }

    private void ZoomBy(Point position, double factor)
    {
        if (!TryCreateViewport(out TimelineViewport viewport))
        {
            return;
        }

        long currentSpan = viewport.TickLength;
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
        bool hit = TryHitTest(position, out TimelineRenderItem item);
        SelectItem(hit ? item : null);
        if (hit
            || SurfaceMode is not (TimelineSurfaceMode.Arrangement or TimelineSurfaceMode.General)
            || position.Y < RulerHeight)
        {
            return;
        }

        // An empty content click still positions the Edit Cursor, including the Conductor row
        // (SRS 18.1.7); a double click on that row must not create an object.
        long tick = SnapTick(RulerTick(position));
        SetCurrentValue(EditCursorTickProperty, tick);
        EditCursorRequested?.Invoke(this, tick);
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
