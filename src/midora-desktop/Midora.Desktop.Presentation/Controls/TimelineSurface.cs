using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Globalization;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Desktop.Presentation.Controls;

public sealed class TimelineItemEventArgs(
    TimelineRenderItem item,
    long tick,
    int lane,
    ModifierKeys modifiers,
    bool isDoubleClick,
    bool isCopyDragStart = false) : RoutedEventArgs
{
    public TimelineRenderItem Item { get; } = item;
    public long Tick { get; } = tick;
    public int Lane { get; } = lane;
    public ModifierKeys Modifiers { get; } = modifiers;
    public bool IsDoubleClick { get; } = isDoubleClick;
    public bool IsCopyDragStart { get; } = isCopyDragStart;
}

public sealed class TimelineMarqueeEventArgs(
    IReadOnlyList<MidoraId> itemIds,
    ModifierKeys modifiers) : RoutedEventArgs
{
    public IReadOnlyList<MidoraId> ItemIds { get; } = itemIds;
    public ModifierKeys Modifiers { get; } = modifiers;
}

public sealed class TimelineRulerEventArgs(long tick) : RoutedEventArgs
{
    public long Tick { get; } = tick;
}

public sealed class TimelineTimeRangeEventArgs(long startTick, long endTick) : RoutedEventArgs
{
    public long StartTick { get; } = startTick;
    public long EndTick { get; } = endTick;
}

public sealed class TimelinePointEventArgs(
    long tick,
    int lane,
    double normalizedValue,
    ModifierKeys modifiers,
    bool isDoubleClick) : RoutedEventArgs
{
    public long Tick { get; } = tick;
    public int Lane { get; } = lane;
    public double NormalizedValue { get; } = normalizedValue;
    public ModifierKeys Modifiers { get; } = modifiers;
    public bool IsDoubleClick { get; } = isDoubleClick;
}

public sealed class TimelineLanePreviewEventArgs(int lane, int pitch, int velocity) : RoutedEventArgs
{
    public int Lane { get; } = lane;
    public int Pitch { get; } = pitch;
    public int Velocity { get; } = velocity;
}

public sealed class TimelineNotePlacementEventArgs(
    long startTick,
    long endTick,
    int pitch,
    int velocity) : RoutedEventArgs
{
    public long StartTick { get; } = startTick;
    public long EndTick { get; } = endTick;
    public long LengthTicks => Math.Max(1, checked(EndTick - StartTick));
    public int Pitch { get; } = pitch;
    public int Velocity { get; } = velocity;
}

public sealed class TimelineSegmentPlacementEventArgs(
    long startTick,
    long endTick,
    int lane) : RoutedEventArgs
{
    public long StartTick { get; } = startTick;
    public long EndTick { get; } = endTick;
    public long LengthTicks => Math.Max(1, checked(EndTick - StartTick));
    public int Lane { get; } = lane;
}

public enum TimelineSurfaceMode
{
    General,
    Arrangement,
    PianoRoll,
    EventLanes,
    Conductor,
    Velocity
}

public sealed class TimelineVelocityEditEventArgs(
    IReadOnlyDictionary<MidoraId, int> velocities) : RoutedEventArgs
{
    public IReadOnlyDictionary<MidoraId, int> Velocities { get; } = velocities;
}

public enum TimelineToolMode
{
    Select,
    Draw,
    Erase,
    Split
}

public enum TimelineLaneHeaderCommand
{
    ToggleMute,
    ToggleSolo
}

public sealed class TimelineLaneHeaderCommandEventArgs(
    int lane,
    TimelineLaneHeaderCommand command) : RoutedEventArgs
{
    public int Lane { get; } = lane;
    public TimelineLaneHeaderCommand Command { get; } = command;
}

public sealed class TimelineLaneHeaderEventArgs(int lane) : RoutedEventArgs
{
    public int Lane { get; } = lane;
}

public sealed class TimelineLaneHeaderReorderEventArgs(int sourceLane, int targetLane) : RoutedEventArgs
{
    public int SourceLane { get; } = sourceLane;
    public int TargetLane { get; } = targetLane;
}

public enum TimelineItemEditKind
{
    Move,
    ResizeStart,
    ResizeEnd
}

public sealed class TimelineItemEditEventArgs(
    TimelineRenderItem item,
    TimelineItemEditKind editKind,
    long tickDelta,
    int laneDelta,
    double valueDelta,
    ModifierKeys modifiers,
    bool copyRequested = false) : RoutedEventArgs
{
    public TimelineRenderItem Item { get; } = item;
    public TimelineItemEditKind EditKind { get; } = editKind;
    public long TickDelta { get; } = tickDelta;
    public int LaneDelta { get; } = laneDelta;
    public double ValueDelta { get; } = valueDelta;
    public ModifierKeys Modifiers { get; } = modifiers;
    public bool CopyRequested { get; } = copyRequested;
}

public sealed class TimelineSurface : Control
{
    public static readonly RoutedEvent AltGestureConsumedEvent = EventManager.RegisterRoutedEvent(
        nameof(AltGestureConsumed),
        RoutingStrategy.Bubble,
        typeof(RoutedEventHandler),
        typeof(TimelineSurface));

    public static readonly DependencyProperty SnapshotProperty = DependencyProperty.Register(
        nameof(Snapshot),
        typeof(TimelineRenderSnapshot),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnViewportMetricsChanged));

    public static readonly DependencyProperty SelectionSnapshotProperty = DependencyProperty.Register(
        nameof(SelectionSnapshot),
        typeof(TimelineSelectionSnapshot),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RulerSnapshotProperty = DependencyProperty.Register(
        nameof(RulerSnapshot),
        typeof(TimelineRenderSnapshot),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StartTickProperty = DependencyProperty.Register(
        nameof(StartTick),
        typeof(long),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TickSpanProperty = DependencyProperty.Register(
        nameof(TickSpan),
        typeof(long),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(3072L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FirstLaneProperty = DependencyProperty.Register(
        nameof(FirstLane),
        typeof(int),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(
            0,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnViewportMetricsChanged,
            CoerceFirstLane));

    public static readonly DependencyProperty LaneHeightProperty = DependencyProperty.Register(
        nameof(LaneHeight),
        typeof(double),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(
            24d,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnViewportMetricsChanged));

    public static readonly DependencyProperty GridStepTicksProperty = DependencyProperty.Register(
        nameof(GridStepTicks),
        typeof(long),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(192L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OperationStepTicksProperty = DependencyProperty.Register(
        nameof(OperationStepTicks),
        typeof(long),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(48L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DisplayGridUsesBarsProperty = DependencyProperty.Register(
        nameof(DisplayGridUsesBars),
        typeof(bool),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OperationUsesBarsProperty = DependencyProperty.Register(
        nameof(OperationUsesBars),
        typeof(bool),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty TimeSignatureMapProperty = DependencyProperty.Register(
        nameof(TimeSignatureMap),
        typeof(ProjectTimeSignatureMap),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DefaultCreationLengthTicksProperty = DependencyProperty.Register(
        nameof(DefaultCreationLengthTicks),
        typeof(long),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(192L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DefaultVelocityProperty = DependencyProperty.Register(
        nameof(DefaultVelocity),
        typeof(int),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(100));

    public static readonly DependencyProperty ValueAxisMinimumProperty = DependencyProperty.Register(
        nameof(ValueAxisMinimum),
        typeof(double),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueAxisMaximumProperty = DependencyProperty.Register(
        nameof(ValueAxisMaximum),
        typeof(double),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(127d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueAxisIntegralProperty = DependencyProperty.Register(
        nameof(ValueAxisIntegral),
        typeof(bool),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PlaybackCursorTickProperty = DependencyProperty.Register(
        nameof(PlaybackCursorTick),
        typeof(long?),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EditCursorTickProperty = DependencyProperty.Register(
        nameof(EditCursorTick),
        typeof(long?),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RangeStartTickProperty = DependencyProperty.Register(
        nameof(RangeStartTick),
        typeof(long?),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RangeEndTickProperty = DependencyProperty.Register(
        nameof(RangeEndTick),
        typeof(long?),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TimeRangeStartTickProperty = DependencyProperty.Register(
        nameof(TimeRangeStartTick),
        typeof(long?),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TimeRangeEndTickProperty = DependencyProperty.Register(
        nameof(TimeRangeEndTick),
        typeof(long?),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SurfaceModeProperty = DependencyProperty.Register(
        nameof(SurfaceMode),
        typeof(TimelineSurfaceMode),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(
            TimelineSurfaceMode.General,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnViewportMetricsChanged));

    public static readonly DependencyProperty CanEditProperty = DependencyProperty.Register(
        nameof(CanEdit),
        typeof(bool),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(true));

    public static readonly DependencyProperty ToolModeProperty = DependencyProperty.Register(
        nameof(ToolMode),
        typeof(TimelineToolMode),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(TimelineToolMode.Select));

    public static readonly DependencyProperty GridVisibleProperty = DependencyProperty.Register(
        nameof(GridVisible),
        typeof(bool),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyPropertyKey MaximumFirstLanePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(MaximumFirstLane),
        typeof(int),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(0));

    public static readonly DependencyProperty MaximumFirstLaneProperty = MaximumFirstLanePropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey VisibleLaneCountPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(VisibleLaneCount),
        typeof(int),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(1));

    public static readonly DependencyProperty VisibleLaneCountProperty = VisibleLaneCountPropertyKey.DependencyProperty;

    public static readonly DependencyProperty ValueScrollOffsetProperty = DependencyProperty.Register(
        nameof(ValueScrollOffset),
        typeof(double),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(
            0d,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnValueScrollOffsetChanged,
            CoerceValueScrollOffset));

    private static readonly DependencyPropertyKey ValueScrollMaximumPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(ValueScrollMaximum),
        typeof(double),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(0d));

    public static readonly DependencyProperty ValueScrollMaximumProperty = ValueScrollMaximumPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey ValueScrollViewportSizePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(ValueScrollViewportSize),
        typeof(double),
        typeof(TimelineSurface),
        new FrameworkPropertyMetadata(1d));

    public static readonly DependencyProperty ValueScrollViewportSizeProperty = ValueScrollViewportSizePropertyKey.DependencyProperty;

    private readonly List<TimelineRenderItem> _visibleItems = new(capacity: 512);
    private readonly List<TimelineRenderItem> _rulerItems = new(capacity: 64);
    private readonly List<TimelineRenderItem> _hitItems = new(capacity: 16);
    private readonly List<MidoraId> _marqueeIds = new(capacity: 128);
    private Brush? _penBorderBrush;
    private Brush? _penInfoBrush;
    private Brush? _penTextBrush;
    private Brush? _penRedBrush;
    private Brush? _penSegmentSelectionBrush;
    private Pen? _borderPen;
    private Pen? _infoPen;
    private Pen? _textPen;
    private Pen? _redPen;
    private Pen? _selectionPen;
    private Pen? _segmentSelectionPen;
    private Pen? _editCursorPen;
    private Pen? _marqueePen;
    private readonly Dictionary<string, FormattedText> _textCache = new(StringComparer.Ordinal);
    private double _cachedPixelsPerDip;
    private Point? _panOrigin;
    private long _panStartTick;
    private int _panFirstLane;
    private double _panValueScrollOffset;
    private Point? _marqueeOrigin;
    private Point? _marqueeCurrent;
    private TimelineRenderItem? _dragItem;
    private TimelineItemEditKind _dragKind;
    private Point _dragOrigin;
    private long _dragOriginTick;
    private int _dragOriginLane;
    private long _dragCurrentTick;
    private int _dragCurrentLane;
    private bool _dragActivated;
    private bool _dragCopyRequested;
    private ModifierKeys _dragModifiers;
    private TimelineLanePreviewEventArgs? _activeLanePreview;
    private long? _notePlacementStartTick;
    private long _notePlacementCurrentTick;
    private Point _notePlacementOrigin;
    private bool _notePlacementActivated;
    private int _notePlacementPitch;
    private int _notePlacementVelocity;
    private long? _segmentPlacementStartTick;
    private long _segmentPlacementCurrentTick;
    private int _segmentPlacementLane;
    private Point _segmentPlacementOrigin;
    private bool _segmentPlacementActivated;
    private Point? _rulerDragOrigin;
    private long _rulerDragStartTick;
    private long _rulerDragCurrentTick;
    private Point? _hoverPoint;
    private Point? _velocityOrigin;
    private MouseButton _velocityButton;
    private MidoraId? _velocityDirectItemId;
    private bool _velocitySelectionRestricted;
    private readonly Dictionary<MidoraId, int> _velocityEdits = [];
    private readonly List<Point> _velocityTracePoints = new(capacity: 128);
    private int? _hoverLaneHeader;
    private int? _pressedLaneHeader;
    private int _laneHeaderDragTarget;
    private Point _laneHeaderDragOrigin;
    private bool _laneHeaderDragActivated;
    private readonly HashSet<TimelineRasterCacheKey> _requestedRasterKeys = [];
    private readonly List<PianoTileDrawEntry> _pianoTileDrawEntries = new(capacity: 64);
    private readonly List<PianoTileDrawEntry> _pianoTileFallbackEntries = new(capacity: 64);
    private readonly List<TimelineRasterCacheKey> _pianoTileVisibleKeys = new(capacity: 64);
    private PianoRasterFrame? _lastCompletePianoNoteFrame;
    private PianoRasterFrame? _lastCompletePianoSelectionFrame;
    private readonly List<VelocityTileDrawEntry> _velocityTileDrawEntries = new(capacity: 32);
    private readonly List<VelocityTileDrawEntry> _velocityTileFallbackEntries = new(capacity: 32);
    private readonly List<TimelineRasterCacheKey> _velocityTileVisibleKeys = new(capacity: 32);
    private VelocityRasterFrame? _lastCompleteVelocityFrame;
    private double _valueViewMinimum;
    private double _valueViewMaximum = 1;

    public TimelineSurface()
    {
        ClipToBounds = true;
        Focusable = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Cursor = Cursors.Arrow;
        FocusVisualStyle = null;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new RenderedSurfaceAutomationPeer(this, "TimelineSurface");

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        _ = UIElementAutomationPeer.CreatePeerForElement(this);
    }

    public TimelineRenderSnapshot? Snapshot
    {
        get => (TimelineRenderSnapshot?)GetValue(SnapshotProperty);
        set => SetValue(SnapshotProperty, value);
    }

    public TimelineSelectionSnapshot? SelectionSnapshot
    {
        get => (TimelineSelectionSnapshot?)GetValue(SelectionSnapshotProperty);
        set => SetValue(SelectionSnapshotProperty, value);
    }

    public TimelineRenderSnapshot? RulerSnapshot
    {
        get => (TimelineRenderSnapshot?)GetValue(RulerSnapshotProperty);
        set => SetValue(RulerSnapshotProperty, value);
    }

    public long StartTick
    {
        get => (long)GetValue(StartTickProperty);
        set => SetValue(StartTickProperty, value);
    }

    public long TickSpan
    {
        get => (long)GetValue(TickSpanProperty);
        set => SetValue(TickSpanProperty, value);
    }

    public int FirstLane
    {
        get => (int)GetValue(FirstLaneProperty);
        set => SetValue(FirstLaneProperty, value);
    }

    public int MaximumFirstLane => (int)GetValue(MaximumFirstLaneProperty);

    public int VisibleLaneCount => (int)GetValue(VisibleLaneCountProperty);

    public double ValueScrollOffset
    {
        get => (double)GetValue(ValueScrollOffsetProperty);
        set => SetValue(ValueScrollOffsetProperty, value);
    }

    public double ValueScrollMaximum => (double)GetValue(ValueScrollMaximumProperty);

    public double ValueScrollViewportSize => (double)GetValue(ValueScrollViewportSizeProperty);

    public double LaneHeight
    {
        get => (double)GetValue(LaneHeightProperty);
        set => SetValue(LaneHeightProperty, value);
    }

    public long GridStepTicks
    {
        get => (long)GetValue(GridStepTicksProperty);
        set => SetValue(GridStepTicksProperty, value);
    }

    public long OperationStepTicks
    {
        get => (long)GetValue(OperationStepTicksProperty);
        set => SetValue(OperationStepTicksProperty, Math.Max(1, value));
    }

    public bool DisplayGridUsesBars
    {
        get => (bool)GetValue(DisplayGridUsesBarsProperty);
        set => SetValue(DisplayGridUsesBarsProperty, value);
    }

    public bool OperationUsesBars
    {
        get => (bool)GetValue(OperationUsesBarsProperty);
        set => SetValue(OperationUsesBarsProperty, value);
    }

    public ProjectTimeSignatureMap? TimeSignatureMap
    {
        get => (ProjectTimeSignatureMap?)GetValue(TimeSignatureMapProperty);
        set => SetValue(TimeSignatureMapProperty, value);
    }

    public long DefaultCreationLengthTicks
    {
        get => (long)GetValue(DefaultCreationLengthTicksProperty);
        set => SetValue(DefaultCreationLengthTicksProperty, Math.Max(1, value));
    }

    public int DefaultVelocity
    {
        get => (int)GetValue(DefaultVelocityProperty);
        set => SetValue(DefaultVelocityProperty, Math.Clamp(value, 1, 127));
    }

    public double ValueAxisMinimum
    {
        get => (double)GetValue(ValueAxisMinimumProperty);
        set => SetValue(ValueAxisMinimumProperty, value);
    }

    public double ValueAxisMaximum
    {
        get => (double)GetValue(ValueAxisMaximumProperty);
        set => SetValue(ValueAxisMaximumProperty, value);
    }

    public bool ValueAxisIntegral
    {
        get => (bool)GetValue(ValueAxisIntegralProperty);
        set => SetValue(ValueAxisIntegralProperty, value);
    }

    public long? PlaybackCursorTick
    {
        get => (long?)GetValue(PlaybackCursorTickProperty);
        set => SetValue(PlaybackCursorTickProperty, value);
    }

    public long? EditCursorTick
    {
        get => (long?)GetValue(EditCursorTickProperty);
        set => SetValue(EditCursorTickProperty, value);
    }

    public long? RangeStartTick
    {
        get => (long?)GetValue(RangeStartTickProperty);
        set => SetValue(RangeStartTickProperty, value);
    }

    public long? RangeEndTick
    {
        get => (long?)GetValue(RangeEndTickProperty);
        set => SetValue(RangeEndTickProperty, value);
    }

    public long? TimeRangeStartTick
    {
        get => (long?)GetValue(TimeRangeStartTickProperty);
        set => SetValue(TimeRangeStartTickProperty, value);
    }

    public long? TimeRangeEndTick
    {
        get => (long?)GetValue(TimeRangeEndTickProperty);
        set => SetValue(TimeRangeEndTickProperty, value);
    }

    public TimelineSurfaceMode SurfaceMode
    {
        get => (TimelineSurfaceMode)GetValue(SurfaceModeProperty);
        set => SetValue(SurfaceModeProperty, value);
    }

    public bool CanEdit
    {
        get => (bool)GetValue(CanEditProperty);
        set => SetValue(CanEditProperty, value);
    }
    public TimelineToolMode ToolMode
    {
        get => (TimelineToolMode)GetValue(ToolModeProperty);
        set => SetValue(ToolModeProperty, value);
    }
    public bool GridVisible
    {
        get => (bool)GetValue(GridVisibleProperty);
        set => SetValue(GridVisibleProperty, value);
    }

    public event EventHandler<TimelineItemEventArgs>? ItemInvoked;
    public event EventHandler<TimelinePointEventArgs>? BackgroundInvoked;
    public event EventHandler<TimelineItemEditEventArgs>? ItemEditCompleted;
    public event EventHandler<TimelineMarqueeEventArgs>? MarqueeCompleted;
    public event EventHandler<TimelineRulerEventArgs>? RulerClicked;
    public event EventHandler<TimelineTimeRangeEventArgs>? TimeRangeSelected;
    public event EventHandler<TimelineItemEventArgs>? SegmentSplitRequested;
    public event EventHandler<TimelineLanePreviewEventArgs>? LanePreviewPressed;
    public event EventHandler<TimelineLanePreviewEventArgs>? LanePreviewReleased;
    public event EventHandler<TimelineNotePlacementEventArgs>? NotePlacementStarted;
    public event EventHandler<TimelineNotePlacementEventArgs>? NotePlacementCompleted;
    public event EventHandler? NotePlacementCancelled;
    public event EventHandler<TimelineSegmentPlacementEventArgs>? SegmentPlacementCompleted;
    public event EventHandler? SegmentPlacementCancelled;
    public event EventHandler<TimelineLaneHeaderCommandEventArgs>? LaneHeaderCommandInvoked;
    public event EventHandler<TimelineLaneHeaderEventArgs>? LaneHeaderInvoked;
    public event EventHandler<TimelineLaneHeaderEventArgs>? LaneHeaderContextRequested;
    public event EventHandler<TimelineLaneHeaderReorderEventArgs>? LaneHeaderReorderCompleted;
    public event EventHandler<TimelineVelocityEditEventArgs>? VelocityEditCompleted;
    public event EventHandler? ViewportChanged;
    public event RoutedEventHandler AltGestureConsumed
    {
        add => AddHandler(AltGestureConsumedEvent, value);
        remove => RemoveHandler(AltGestureConsumedEvent, value);
    }

    public double LaneHeaderWidth => GetLaneHeaderWidth();

    public bool TryGetArrangementLaneHeader(Point point, out int lane)
    {
        lane = -1;
        if (SurfaceMode != TimelineSurfaceMode.Arrangement
            || point.X < 0
            || point.X >= GetLaneHeaderWidth()
            || point.Y < GetRulerHeight()
            || !TryCreateViewport(out TimelineViewport viewport))
        {
            return false;
        }
        int candidate = viewport.YToLane(point.Y - GetRulerHeight());
        if ((uint)candidate >= (uint)(Snapshot?.LaneLabels.Count ?? 0)) return false;
        lane = candidate;
        return true;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateVerticalViewportMetrics();
    }

    private static void OnViewportMetricsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is TimelineSurface surface)
        {
            surface.UpdateVerticalViewportMetrics();
        }
    }

    private static object CoerceFirstLane(DependencyObject dependencyObject, object baseValue)
    {
        TimelineSurface surface = (TimelineSurface)dependencyObject;
        return Math.Clamp((int)baseValue, 0, surface.ComputeMaximumFirstLane());
    }

    private static object CoerceValueScrollOffset(DependencyObject dependencyObject, object baseValue)
    {
        TimelineSurface surface = (TimelineSurface)dependencyObject;
        double value = (double)baseValue;
        return double.IsFinite(value)
            ? Math.Clamp(value, 0, surface.ValueScrollMaximum)
            : 0d;
    }

    private static void OnValueScrollOffsetChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        TimelineSurface surface = (TimelineSurface)dependencyObject;
        double range = Math.Clamp(surface._valueViewMaximum - surface._valueViewMinimum, 1d / 64, 1);
        double offset = Math.Clamp((double)args.NewValue, 0, Math.Max(0, 1 - range));
        surface._valueViewMaximum = 1 - offset;
        surface._valueViewMinimum = surface._valueViewMaximum - range;
        surface.InvalidateVisual();
        surface.ViewportChanged?.Invoke(surface, EventArgs.Empty);
    }

    private void UpdateVerticalViewportMetrics()
    {
        int visibleLaneCount = ComputeVisibleLaneCount();
        int maximum = Math.Max(0, ComputeTotalLaneCount() - visibleLaneCount);
        SetValue(VisibleLaneCountPropertyKey, visibleLaneCount);
        SetValue(MaximumFirstLanePropertyKey, maximum);
        CoerceValue(FirstLaneProperty);
        UpdateValueScrollMetrics();
    }

    private int ComputeMaximumFirstLane() =>
        Math.Max(0, ComputeTotalLaneCount() - ComputeVisibleLaneCount());

    private int ComputeTotalLaneCount()
    {
        return SurfaceMode == TimelineSurfaceMode.PianoRoll
            ? 128
            : SurfaceMode is TimelineSurfaceMode.EventLanes or TimelineSurfaceMode.Velocity
                ? 1
                : Math.Max(
                    Snapshot?.LaneLabels.Count ?? 0,
                    Snapshot?.Items.Count > 0 ? Snapshot.Items.Max(item => item.Lane) + 1 : 0);
    }

    private int ComputeVisibleLaneCount()
    {
        double contentHeight = Math.Max(0, ActualHeight - GetRulerHeight());
        return LaneHeight > 0 && double.IsFinite(LaneHeight)
            ? Math.Max(1, (int)Math.Ceiling(contentHeight / LaneHeight))
            : 1;
    }

    private void UpdateValueScrollMetrics()
    {
        double range = Math.Clamp(_valueViewMaximum - _valueViewMinimum, 1d / 64, 1);
        double maximum = Math.Max(0, 1 - range);
        SetValue(ValueScrollMaximumPropertyKey, maximum);
        SetValue(ValueScrollViewportSizePropertyKey, range);
        CoerceValue(ValueScrollOffsetProperty);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!TryCreateViewport(out TimelineViewport viewport))
        {
            return;
        }

        Brush surface = Brush("Brush.Surface.0", Color.FromRgb(9, 11, 14));
        Brush alternate = Brush("Brush.Surface.1", Color.FromRgb(14, 17, 21));
        Brush border = Brush("Brush.Border", Color.FromRgb(42, 48, 58));
        Brush red = Brush("Brush.Red", Color.FromRgb(229, 72, 77));
        Brush redDark = Brush("Brush.Red.Dark", Color.FromRgb(143, 36, 41));
        Brush info = Brush("Brush.Info", Color.FromRgb(98, 166, 246));
        Brush warning = Brush("Brush.Warning", Color.FromRgb(232, 179, 75));
        Brush text = Brush("Brush.Text.Primary", Color.FromRgb(241, 243, 245));
        Brush segment = Brush("Brush.Segment", Color.FromRgb(66, 78, 88));
        Brush selectedSegment = Brush("Brush.Segment.Selected", Color.FromRgb(48, 59, 69));
        Brush segmentSelection = Brush("Brush.Segment.Selection", Color.FromRgb(145, 166, 184));
        Brush segmentNotePreview = Brush("Brush.Segment.NotePreview", Color.FromRgb(189, 199, 207));
        Brush segmentPianoNote = Brush("Brush.Segment.PianoNote", Color.FromRgb(163, 178, 190));
        Brush segmentPianoOutside = Brush("Brush.Segment.PianoOutside", Color.FromRgb(2, 3, 4));
        Brush pianoWhiteKey = Brush("Brush.PianoKey.White", Color.FromRgb(212, 216, 221));
        Brush pianoBlackKey = Brush("Brush.PianoKey.Black", Color.FromRgb(21, 24, 29));
        Brush pianoKeyLabel = Brush("Brush.PianoKey.Label", Color.FromRgb(37, 43, 51));
        EnsurePens(border, info, text, red, segmentSelection);

        drawingContext.DrawRectangle(surface, null, new Rect(0, 0, ActualWidth, ActualHeight));
        double laneHeaderWidth = GetLaneHeaderWidth();
        double rulerHeight = GetRulerHeight();
        for (int relativeLane = 0; relativeLane < viewport.LaneCount; relativeLane++)
        {
            double y = rulerHeight + relativeLane * LaneHeight;
            int absoluteLane = viewport.FirstLane + relativeLane;
            bool shaded = SurfaceMode == TimelineSurfaceMode.PianoRoll
                ? !PianoKeyPresentation.IsBlackKey(127 - absoluteLane)
                : (relativeLane & 1) != 0;
            if (shaded)
            {
                drawingContext.DrawRectangle(
                    alternate,
                    null,
                    new Rect(laneHeaderWidth, y, Math.Max(0, ActualWidth - laneHeaderWidth), Math.Min(LaneHeight, ActualHeight - y)));
            }
            drawingContext.DrawLine(_borderPen, new Point(0, y), new Point(ActualWidth, y));
        }

        DrawPianoOutsideActiveRange(
            drawingContext,
            viewport,
            segmentPianoOutside,
            laneHeaderWidth,
            rulerHeight);
        if (GridVisible) DrawGrid(drawingContext, viewport, laneHeaderWidth, rulerHeight);
        if (SurfaceMode is TimelineSurfaceMode.Velocity or TimelineSurfaceMode.EventLanes)
        {
            DrawValueGrid(drawingContext, text, laneHeaderWidth, rulerHeight, drawLabels: false);
        }
        DrawActiveRange(drawingContext, viewport, segmentSelection, laneHeaderWidth, rulerHeight);
        DrawTimeRangeSelection(drawingContext, viewport, info, laneHeaderWidth, rulerHeight);

        TimelineRenderSnapshot? snapshot = Snapshot;
        if (snapshot is not null)
        {
            if (SurfaceMode == TimelineSurfaceMode.PianoRoll)
            {
                DrawPianoNoteTiles(
                    drawingContext,
                    viewport,
                    red,
                    warning,
                    segmentPianoNote,
                    laneHeaderWidth,
                    rulerHeight);
                DrawPianoSelectionOverlay(
                    drawingContext,
                    viewport,
                    redDark,
                    warning,
                    red,
                    laneHeaderWidth,
                    rulerHeight);
            }
            else if (SurfaceMode == TimelineSurfaceMode.Velocity)
            {
                DrawVelocityTiles(
                    drawingContext,
                    viewport,
                    segmentPianoNote,
                    red,
                    border,
                    laneHeaderWidth,
                    rulerHeight);
                DrawVelocityEditOverlay(
                    drawingContext,
                    viewport,
                    segmentPianoNote,
                    red,
                    laneHeaderWidth,
                    rulerHeight);
            }
            else
            {
                snapshot.Index.QueryInto(
                    viewport.StartTick,
                    viewport.EndTick,
                    viewport.FirstLane,
                    viewport.LastLaneExclusive,
                    _visibleItems);
                foreach (TimelineRenderItem item in _visibleItems)
                {
                    DrawItem(
                        drawingContext,
                        viewport,
                        item,
                        red,
                        redDark,
                        info,
                        warning,
                        segment,
                        selectedSegment,
                        segmentNotePreview,
                        segmentPianoNote,
                        laneHeaderWidth,
                        rulerHeight);
                }
            }
        }

        DrawDragPreview(drawingContext, viewport, laneHeaderWidth, rulerHeight);
        DrawDirectManipulationHover(drawingContext, viewport, laneHeaderWidth, rulerHeight);
        DrawCreationHoverPreview(drawingContext, viewport, red, info, laneHeaderWidth, rulerHeight);
        DrawSegmentPlacementPreview(drawingContext, viewport, laneHeaderWidth, rulerHeight);
        DrawNotePlacementPreview(drawingContext, viewport, red, laneHeaderWidth, rulerHeight);
        DrawCursor(drawingContext, viewport, EditCursorTick, _editCursorPen!, laneHeaderWidth, rulerHeight);
        DrawCursor(drawingContext, viewport, PlaybackCursorTick, _redPen!, laneHeaderWidth, rulerHeight);
        DrawTimelineChrome(
            drawingContext,
            viewport,
            alternate,
            border,
            text,
            pianoWhiteKey,
            pianoBlackKey,
            pianoKeyLabel,
            laneHeaderWidth,
            rulerHeight);
        if (SurfaceMode is TimelineSurfaceMode.Velocity or TimelineSurfaceMode.EventLanes)
        {
            DrawValueGrid(drawingContext, text, laneHeaderWidth, rulerHeight, drawLabels: true);
        }
        DrawRulerOverview(drawingContext, viewport, text, warning, laneHeaderWidth, rulerHeight);
        DrawMarquee(drawingContext, viewport, info);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        Point point = e.GetPosition(this);
        if (e.ChangedButton == MouseButton.Middle)
        {
            _panOrigin = point;
            _panStartTick = StartTick;
            _panFirstLane = FirstLane;
            _panValueScrollOffset = ValueScrollOffset;
            CaptureMouse();
            e.Handled = true;
            return;
        }
        if (!TryCreateViewport(out TimelineViewport viewport))
        {
            return;
        }

        if (SurfaceMode == TimelineSurfaceMode.Velocity
            && CanEdit
            && e.ChangedButton is MouseButton.Left or MouseButton.Right
            && point.X >= GetLaneHeaderWidth()
            && point.Y >= GetRulerHeight())
        {
            bool forceTrace = TimelineToolPolicy.ForcesVelocityTrace(
                e.ChangedButton,
                Keyboard.Modifiers);
            _velocityOrigin = point;
            _velocityButton = e.ChangedButton;
            _velocityEdits.Clear();
            _velocitySelectionRestricted = Snapshot?.Items.Any(item =>
                item.Kind == TimelineItemKind.Velocity
                && IsSelected(item)) == true;
            _velocityDirectItemId = e.ChangedButton == MouseButton.Left
                && !forceTrace
                && TryHitVelocityBar(point, viewport, out TimelineRenderItem directItem)
                ? directItem.Id
                : null;
            if (forceTrace)
            {
                RaiseEvent(new RoutedEventArgs(AltGestureConsumedEvent, this));
            }
            _velocityTracePoints.Clear();
            if (_velocityDirectItemId is MidoraId directId)
            {
                UpdateSingleVelocity(directId, point.Y, GetRulerHeight());
            }
            else
            {
                _velocityTracePoints.Add(ClampVelocityTracePoint(point));
            }
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        double laneHeaderWidth = GetLaneHeaderWidth();
        double rulerHeight = GetRulerHeight();
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (point.X >= laneHeaderWidth && point.Y >= 0 && point.Y < rulerHeight)
        {
            long rulerTick = viewport.XToTick(point.X - laneHeaderWidth);
            _rulerDragOrigin = point;
            _rulerDragStartTick = rulerTick;
            _rulerDragCurrentTick = rulerTick;
            CaptureMouse();
            e.Handled = true;
            return;
        }
        int lane = viewport.YToLane(point.Y - rulerHeight);
        if (SurfaceMode == TimelineSurfaceMode.Arrangement
            && point.X < laneHeaderWidth
            && point.Y >= rulerHeight
            && TryGetArrangementLaneCommand(point.X, out TimelineLaneHeaderCommand laneCommand))
        {
            LaneHeaderCommandInvoked?.Invoke(this, new(lane, laneCommand));
            e.Handled = true;
            return;
        }
        if (SurfaceMode == TimelineSurfaceMode.Arrangement
            && point.X < laneHeaderWidth
            && point.Y >= rulerHeight)
        {
            _pressedLaneHeader = lane;
            _laneHeaderDragTarget = lane;
            _laneHeaderDragOrigin = point;
            _laneHeaderDragActivated = false;
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (point.X < laneHeaderWidth
            && point.Y >= rulerHeight
            && SurfaceMode is TimelineSurfaceMode.EventLanes or TimelineSurfaceMode.Conductor)
        {
            LaneHeaderInvoked?.Invoke(this, new(lane));
            e.Handled = true;
            return;
        }
        long tick = viewport.XToTick(point.X - laneHeaderWidth);
        if (SurfaceMode == TimelineSurfaceMode.PianoRoll
            && point.X < laneHeaderWidth
            && point.Y >= rulerHeight)
        {
            int pitch = Math.Clamp(127 - lane, 0, 127);
            int velocity = Math.Clamp(
                (int)Math.Round(32 + point.X / Math.Max(1, laneHeaderWidth) * 95),
                1,
                127);
            _activeLanePreview = new(lane, pitch, velocity);
            CaptureMouse();
            LanePreviewPressed?.Invoke(this, _activeLanePreview);
            e.Handled = true;
            return;
        }
        bool pointIsInContent = point.X >= laneHeaderWidth && point.Y >= rulerHeight;
        double laneOffset = Math.Clamp(point.Y - rulerHeight - (lane - viewport.FirstLane) * LaneHeight, 0, LaneHeight);
        double normalizedValue = SurfaceMode is TimelineSurfaceMode.EventLanes or TimelineSurfaceMode.Velocity
            ? ValueYToNormalized(point.Y, rulerHeight)
            : 1 - laneOffset / Math.Max(1, LaneHeight);
        if (pointIsInContent
            && TimelineToolPolicy.StartsMarqueeBeforeItemHit(ToolMode, SurfaceMode, e.ClickCount))
        {
            _marqueeOrigin = point;
            _marqueeCurrent = point;
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (pointIsInContent)
        {
            PopulateTimelineHitItems(
                point,
                viewport,
                preferDirectEditEdges: CanEdit
                    && ToolMode == TimelineToolMode.Draw
                    && TimelineToolPolicy.IsDirectEditingSurface(SurfaceMode));
            if (_hitItems.Count == 0 && Snapshot is not null)
            {
                long pointTolerance = Math.Max(1, checked((long)Math.Ceiling(4 / viewport.PixelsPerTick)));
                Snapshot.Index.HitTestInto(tick, pointTolerance, lane, _hitItems);
                _hitItems.RemoveAll(item => item.Kind != TimelineItemKind.LogicalParameterPoint);
            }
            _hitItems.RemoveAll(item => item.Kind == TimelineItemKind.LogicalParameterPoint
                && Math.Abs(
                    SurfaceMode == TimelineSurfaceMode.EventLanes
                        ? NormalizedToValueY(item.Value, rulerHeight) - point.Y
                        : rulerHeight + (item.Lane - viewport.FirstLane) * LaneHeight
                          + 4 + (1 - Math.Clamp(item.Value, 0, 1)) * Math.Max(1, LaneHeight - 8)
                          - point.Y) > 8);
        }
        else
        {
            _hitItems.Clear();
        }
        if (_hitItems.Count != 0)
        {
            if (ToolMode == TimelineToolMode.Select && e.ClickCount == 1)
            {
                RaiseBackgroundInvoked(point, viewport, isDoubleClick: false);
            }
            int hitIndex = 0;
            if ((modifiers & ModifierKeys.Alt) != 0 && _hitItems.Count > 1)
            {
                int primaryIndex = _hitItems.FindIndex(item =>
                    IsPrimary(item));
                hitIndex = primaryIndex < 0 ? 0 : (primaryIndex + 1) % _hitItems.Count;
            }
            TimelineRenderItem hit = _hitItems[hitIndex];
            ItemInvoked?.Invoke(
                this,
                new TimelineItemEventArgs(
                    hit,
                    tick,
                    lane,
                    modifiers,
                    e.ClickCount == 2));
            if (CanEdit
                && ToolMode == TimelineToolMode.Split
                && SurfaceMode == TimelineSurfaceMode.Arrangement
                && hit.Kind == TimelineItemKind.Segment)
            {
                SegmentSplitRequested?.Invoke(
                    this,
                    new TimelineItemEventArgs(hit, tick, lane, modifiers, isDoubleClick: false));
                e.Handled = true;
                return;
            }
            if (e.ClickCount == 1
                && CanEdit
                && TimelineToolPolicy.CanBeginItemEdit(ToolMode, SurfaceMode, hit.Kind))
            {
                double left = laneHeaderWidth + viewport.TickToX(hit.StartTick);
                double right = laneHeaderWidth + viewport.TickToX(hit.EndTick);
                _dragKind = TimelineToolPolicy.ResolveItemEditKind(
                    ToolMode,
                    SurfaceMode,
                    hit.Kind,
                    modifiers,
                    isNearStart: Math.Abs(point.X - left)
                        <= TimelineToolPolicy.DirectEditEdgeTolerancePixels,
                    isNearEnd: Math.Abs(point.X - right)
                        <= TimelineToolPolicy.DirectEditEdgeTolerancePixels);
                if (TimelineToolPolicy.ForcesItemMove(
                        ToolMode,
                        SurfaceMode,
                        hit.Kind,
                        modifiers))
                {
                    RaiseEvent(new RoutedEventArgs(AltGestureConsumedEvent, this));
                }
                _dragItem = hit;
                _dragModifiers = modifiers;
                _dragOrigin = point;
                _dragOriginTick = tick;
                _dragOriginLane = lane;
                _dragCurrentTick = tick;
                _dragCurrentLane = lane;
                _dragActivated = false;
                _dragCopyRequested = (modifiers & ModifierKeys.Control) != 0
                    && TimelineToolPolicy.SupportsCopyDrag(
                        ToolMode,
                        SurfaceMode,
                        hit.Kind,
                        _dragKind);
                CaptureMouse();
            }
        }
        else
        {
            if (CanEdit
                && ToolMode == TimelineToolMode.Draw
                && SurfaceMode == TimelineSurfaceMode.Arrangement
                && pointIsInContent)
            {
                long snappedStart = SnapAbsolute(tick);
                _segmentPlacementStartTick = snappedStart;
                _segmentPlacementCurrentTick = snappedStart > long.MaxValue - Math.Max(1, DefaultCreationLengthTicks)
                    ? long.MaxValue
                    : snappedStart + Math.Max(1, DefaultCreationLengthTicks);
                _segmentPlacementLane = lane;
                _segmentPlacementOrigin = point;
                _segmentPlacementActivated = false;
                CaptureMouse();
                InvalidateVisual();
                e.Handled = true;
                return;
            }
            if (CanEdit
                && ToolMode == TimelineToolMode.Draw
                && SurfaceMode == TimelineSurfaceMode.PianoRoll
                && pointIsInContent)
            {
                long snappedStart = SnapAbsolute(tick);
                _notePlacementStartTick = snappedStart;
                _notePlacementCurrentTick = checked(snappedStart + Math.Max(1, DefaultCreationLengthTicks));
                _notePlacementPitch = Math.Clamp(127 - lane, 0, 127);
                _notePlacementVelocity = Math.Clamp(DefaultVelocity, 1, 127);
                _notePlacementOrigin = point;
                _notePlacementActivated = false;
                CaptureMouse();
                NotePlacementStarted?.Invoke(this, new(
                    snappedStart,
                    _notePlacementCurrentTick,
                    _notePlacementPitch,
                    _notePlacementVelocity));
                InvalidateVisual();
                e.Handled = true;
                return;
            }
            bool requestsCreation = CanEdit
                && TimelineToolPolicy.RequestsBackgroundCreation(ToolMode, SurfaceMode, e.ClickCount);
            BackgroundInvoked?.Invoke(
                this,
                new TimelinePointEventArgs(
                    tick,
                    lane,
                    normalizedValue,
                    Keyboard.Modifiers,
                    requestsCreation));
            if (requestsCreation)
            {
                e.Handled = true;
                return;
            }
            if (!pointIsInContent)
            {
                e.Handled = true;
                return;
            }
            _marqueeOrigin = point;
            _marqueeCurrent = point;
            CaptureMouse();
            InvalidateVisual();
        }
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (SurfaceMode == TimelineSurfaceMode.Velocity) return;
        if (!TryCreateViewport(out TimelineViewport viewport)) return;
        Point point = e.GetPosition(this);
        double laneHeaderWidth = GetLaneHeaderWidth();
        double rulerHeight = GetRulerHeight();
        if (SurfaceMode == TimelineSurfaceMode.Arrangement
            && TryGetArrangementLaneHeader(point, out int headerLane))
        {
            LaneHeaderContextRequested?.Invoke(this, new(headerLane));
            return;
        }
        if (point.X < laneHeaderWidth || point.Y < rulerHeight) return;
        long tick = viewport.XToContainingTick(point.X - laneHeaderWidth);
        int lane = viewport.YToLane(point.Y - rulerHeight);
        Snapshot?.Index.HitTestInto(tick, 0, lane, _hitItems);
        if (_hitItems.Count == 0) return;
        TimelineRenderItem hit = _hitItems[0];
        ItemInvoked?.Invoke(
            this,
            new TimelineItemEventArgs(hit, tick, lane, Keyboard.Modifiers, isDoubleClick: false));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point point = e.GetPosition(this);
        _hoverPoint = point;
        int? previousHoverLaneHeader = _hoverLaneHeader;
        _hoverLaneHeader = TryGetArrangementLaneHeader(point, out int hoverLane)
            ? hoverLane
            : null;
        if (_pressedLaneHeader is int pressedLane
            && e.LeftButton == MouseButtonState.Pressed
            && TryCreateViewport(out TimelineViewport headerViewport))
        {
            int nextTarget = headerViewport.YToLane(point.Y - GetRulerHeight());
            int laneCount = Snapshot?.LaneLabels.Count ?? 0;
            _laneHeaderDragTarget = laneCount == 0 ? 0 : Math.Clamp(nextTarget, 0, laneCount - 1);
            _laneHeaderDragActivated |= Math.Abs(point.Y - _laneHeaderDragOrigin.Y) >= 3;
            Cursor = _laneHeaderDragActivated ? Cursors.SizeNS : Cursors.Arrow;
            InvalidateVisual();
            return;
        }
        bool hoverChangesVisual = ToolMode == TimelineToolMode.Draw
            && SurfaceMode is TimelineSurfaceMode.Arrangement or TimelineSurfaceMode.PianoRoll;
        if (_velocityOrigin is Point velocityOrigin
            && (e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed))
        {
            if (_velocityDirectItemId is MidoraId directId)
            {
                UpdateSingleVelocity(directId, point.Y, GetRulerHeight());
            }
            else
            {
                UpdateVelocityTrace(velocityOrigin, point);
            }
            InvalidateVisual();
            return;
        }
        if (e.LeftButton == MouseButtonState.Pressed
            && (_notePlacementStartTick is not null
                || _segmentPlacementStartTick is not null
                || _dragItem is not null))
        {
            AutoScrollEditGesture(point);
        }
        if (_segmentPlacementStartTick is long segmentStart
            && e.LeftButton == MouseButtonState.Pressed
            && TryCreateViewport(out TimelineViewport segmentPlacementViewport))
        {
            _segmentPlacementActivated |= Math.Abs(point.X - _segmentPlacementOrigin.X) >= 3;
            if (_segmentPlacementActivated)
            {
                long rawEnd = segmentPlacementViewport.XToTick(point.X - GetLaneHeaderWidth());
                long rawDelta = Math.Max(1, checked(rawEnd - segmentStart));
                long snappedDelta = SnapOperationDelta(rawDelta, checked(segmentStart + rawDelta));
                _segmentPlacementCurrentTick = checked(segmentStart + Math.Max(1, snappedDelta));
            }
            InvalidateVisual();
            return;
        }
        if (_notePlacementStartTick is not null
            && e.LeftButton == MouseButtonState.Pressed
            && TryCreateViewport(out TimelineViewport placementViewport))
        {
            _notePlacementActivated |= Math.Abs(point.X - _notePlacementOrigin.X) >= 3;
            if (_notePlacementActivated)
            {
                long rawEnd = placementViewport.XToTick(point.X - GetLaneHeaderWidth());
                long rawDelta = Math.Max(1, checked(rawEnd - _notePlacementStartTick.Value));
                long snappedDelta = SnapOperationDelta(rawDelta, checked(_notePlacementStartTick.Value + rawDelta));
                _notePlacementCurrentTick = checked(_notePlacementStartTick.Value + Math.Max(1, snappedDelta));
            }
            InvalidateVisual();
            return;
        }
        if (_dragItem is TimelineRenderItem dragItem && e.LeftButton == MouseButtonState.Pressed
            && TryCreateViewport(out TimelineViewport dragViewport))
        {
            _dragCurrentTick = dragViewport.XToTick(point.X - GetLaneHeaderWidth());
            _dragCurrentLane = dragViewport.YToLane(point.Y - GetRulerHeight());
            bool wasActivated = _dragActivated;
            _dragActivated |= Math.Abs(point.X - _dragOrigin.X) >= 3
                || Math.Abs(point.Y - _dragOrigin.Y) >= 3;
            if (!wasActivated && _dragActivated && _dragCopyRequested)
            {
                ItemInvoked?.Invoke(
                    this,
                    new TimelineItemEventArgs(
                        dragItem,
                        _dragOriginTick,
                        _dragOriginLane,
                        _dragModifiers,
                        isDoubleClick: false,
                        isCopyDragStart: true));
            }
            if (_dragActivated)
            {
                Cursor = _dragKind == TimelineItemEditKind.Move
                    ? Cursors.SizeAll
                    : Cursors.SizeWE;
            }
            InvalidateVisual();
            return;
        }
        if (_panOrigin is Point pan && e.MiddleButton == MouseButtonState.Pressed
            && TryCreateViewport(out TimelineViewport viewport))
        {
            double deltaX = point.X - pan.X;
            double deltaY = point.Y - pan.Y;
            long tickDelta = checked((long)Math.Round(
                deltaX / viewport.PixelsPerTick,
                MidpointRounding.AwayFromZero));
            StartTick = Math.Max(0, _panStartTick - tickDelta);
            if (SurfaceMode is TimelineSurfaceMode.EventLanes or TimelineSurfaceMode.Velocity)
            {
                double contentHeight = Math.Max(1, ActualHeight - GetRulerHeight());
                ValueScrollOffset = _panValueScrollOffset
                    - deltaY / contentHeight * ValueScrollViewportSize;
            }
            else
            {
                FirstLane = _panFirstLane - (int)Math.Round(deltaY / LaneHeight);
            }
            ViewportChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (_marqueeOrigin is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            _marqueeCurrent = point;
            InvalidateVisual();
        }
        if (_rulerDragOrigin is not null && e.LeftButton == MouseButtonState.Pressed
            && TryCreateViewport(out TimelineViewport rulerViewport))
        {
            _rulerDragCurrentTick = rulerViewport.XToTick(point.X - GetLaneHeaderWidth());
            InvalidateVisual();
            return;
        }
        if (TryCreateViewport(out TimelineViewport hoverViewport))
        {
            UpdateHoverCursor(point, hoverViewport);
        }
        if (hoverChangesVisual || previousHoverLaneHeader != _hoverLaneHeader)
        {
            InvalidateVisual();
        }
    }

    private void AutoScrollEditGesture(Point point)
    {
        const double edge = 24;
        long horizontalStep = Math.Max(1, TickSpan / 48);
        bool changed = false;
        if (point.X < GetLaneHeaderWidth() + edge && StartTick > 0)
        {
            StartTick = Math.Max(0, StartTick - horizontalStep);
            changed = true;
        }
        else if (point.X > ActualWidth - edge)
        {
            long maximumStart = Math.Max(0, long.MaxValue - Math.Max(1, TickSpan));
            long next = StartTick >= maximumStart - Math.Min(horizontalStep, maximumStart)
                ? maximumStart
                : StartTick + horizontalStep;
            if (next != StartTick)
            {
                StartTick = next;
                changed = true;
            }
        }
        if (point.Y < GetRulerHeight() + edge && FirstLane > 0)
        {
            FirstLane--;
            changed = true;
        }
        else if (point.Y > ActualHeight - edge)
        {
            if (FirstLane < MaximumFirstLane)
            {
                FirstLane++;
                changed = true;
            }
        }
        if (!changed) return;
        ViewportChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.Left && _pressedLaneHeader is int pressedLane)
        {
            bool reordered = _laneHeaderDragActivated && _laneHeaderDragTarget != pressedLane;
            int targetLane = _laneHeaderDragTarget;
            _pressedLaneHeader = null;
            _laneHeaderDragActivated = false;
            Cursor = Cursors.Arrow;
            ReleaseMouseCapture();
            if (reordered)
            {
                LaneHeaderReorderCompleted?.Invoke(this, new(pressedLane, targetLane));
            }
            else
            {
                LaneHeaderInvoked?.Invoke(this, new(pressedLane));
            }
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (_velocityOrigin is not null && e.ChangedButton == _velocityButton)
        {
            if (_velocityDirectItemId is null && TryCreateViewport(out TimelineViewport velocityViewport))
            {
                UpdateVelocityTrace(_velocityOrigin.Value, e.GetPosition(this));
                BuildVelocityEditsFromTrace(velocityViewport);
            }
            IReadOnlyDictionary<MidoraId, int> result = new Dictionary<MidoraId, int>(_velocityEdits);
            _velocityOrigin = null;
            _velocityDirectItemId = null;
            _velocitySelectionRestricted = false;
            _velocityEdits.Clear();
            _velocityTracePoints.Clear();
            ReleaseMouseCapture();
            if (result.Count > 0) VelocityEditCompleted?.Invoke(this, new(result));
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left
            && _notePlacementStartTick is long placementStart)
        {
            long placementEnd = Math.Max(checked(placementStart + 1), _notePlacementCurrentTick);
            _notePlacementStartTick = null;
            ReleaseMouseCapture();
            NotePlacementCompleted?.Invoke(this, new(
                placementStart,
                placementEnd,
                _notePlacementPitch,
                _notePlacementVelocity));
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left
            && _segmentPlacementStartTick is long segmentPlacementStart)
        {
            long segmentPlacementEnd = Math.Max(
                checked(segmentPlacementStart + 1),
                _segmentPlacementCurrentTick);
            int segmentPlacementLane = _segmentPlacementLane;
            _segmentPlacementStartTick = null;
            ReleaseMouseCapture();
            SegmentPlacementCompleted?.Invoke(this, new(
                segmentPlacementStart,
                segmentPlacementEnd,
                segmentPlacementLane));
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left && _rulerDragOrigin is Point rulerOrigin)
        {
            long rawStart = Math.Min(_rulerDragStartTick, _rulerDragCurrentTick);
            long rawEnd = Math.Max(_rulerDragStartTick, _rulerDragCurrentTick);
            long start = SnapAbsolute(rawStart);
            long length = SnapOperationDelta(Math.Max(1, checked(rawEnd - rawStart)), rawEnd);
            long end = checked(start + Math.Max(1, length));
            bool isDrag = Math.Abs(e.GetPosition(this).X - rulerOrigin.X) >= 3 && end > start;
            _rulerDragOrigin = null;
            ReleaseMouseCapture();
            if (isDrag)
            {
                TimeRangeSelected?.Invoke(this, new(start, end));
            }
            else
            {
                RulerClicked?.Invoke(this, new(SnapAbsolute(_rulerDragStartTick)));
            }
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left
            && _activeLanePreview is TimelineLanePreviewEventArgs preview)
        {
            _activeLanePreview = null;
            ReleaseMouseCapture();
            LanePreviewReleased?.Invoke(this, preview);
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left && _dragItem is TimelineRenderItem item)
        {
            if (_dragActivated)
            {
                ItemEditCompleted?.Invoke(
                    this,
                    new TimelineItemEditEventArgs(
                        item,
                        _dragKind,
                        checked(_dragCurrentTick - _dragOriginTick),
                        checked(_dragCurrentLane - _dragOriginLane),
                        -(e.GetPosition(this).Y - _dragOrigin.Y) / Math.Max(1, LaneHeight),
                        _dragModifiers,
                        _dragCopyRequested));
            }
            ClearItemDrag();
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Middle && _panOrigin is not null)
        {
            _panOrigin = null;
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left && _marqueeOrigin is Point origin)
        {
            Point current = _marqueeCurrent ?? origin;
            CompleteMarquee(origin, current);
            _marqueeOrigin = null;
            _marqueeCurrent = null;
            ReleaseMouseCapture();
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (_notePlacementStartTick is not null)
        {
            _notePlacementStartTick = null;
            NotePlacementCancelled?.Invoke(this, EventArgs.Empty);
        }
        if (_segmentPlacementStartTick is not null)
        {
            _segmentPlacementStartTick = null;
            SegmentPlacementCancelled?.Invoke(this, EventArgs.Empty);
        }
        if (_activeLanePreview is TimelineLanePreviewEventArgs preview)
        {
            _activeLanePreview = null;
            LanePreviewReleased?.Invoke(this, preview);
        }
        ClearItemDrag();
        _panOrigin = null;
        _marqueeOrigin = null;
        _marqueeCurrent = null;
        _rulerDragOrigin = null;
        _velocityOrigin = null;
        _velocityDirectItemId = null;
        _velocitySelectionRestricted = false;
        _velocityEdits.Clear();
        _velocityTracePoints.Clear();
        _pressedLaneHeader = null;
        _laneHeaderDragActivated = false;
        InvalidateVisual();
        base.OnLostMouseCapture(e);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!TryCreateViewport(out TimelineViewport viewport))
        {
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            Point pointer = e.GetPosition(this);
            if (SurfaceMode is TimelineSurfaceMode.EventLanes or TimelineSurfaceMode.Velocity
                && pointer.X < GetLaneHeaderWidth())
            {
                ZoomValueAxis(pointer.Y, e.Delta, GetRulerHeight());
                ViewportChanged?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }
            if (SurfaceMode == TimelineSurfaceMode.PianoRoll
                && pointer.X < GetLaneHeaderWidth())
            {
                int anchorLane = viewport.YToLane(pointer.Y - GetRulerHeight());
                double verticalFactor = e.Delta > 0 ? 1.2 : 1 / 1.2;
                double oldHeight = LaneHeight;
                double newHeight = Math.Clamp(oldHeight * verticalFactor, 8, 128);
                double relative = (pointer.Y - GetRulerHeight()) / Math.Max(1, oldHeight);
                LaneHeight = newHeight;
                FirstLane = Math.Max(0, anchorLane - (int)Math.Floor(relative));
                ViewportChanged?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }
            long anchor = viewport.XToTick(e.GetPosition(this).X - GetLaneHeaderWidth());
            double factor = e.Delta > 0 ? 0.8 : 1.25;
            long newSpan = Math.Clamp(
                checked((long)Math.Round(TickSpan * factor, MidpointRounding.AwayFromZero)),
                16,
                1L << 50);
            double anchorRatio = (anchor - StartTick) / (double)TickSpan;
            long newStart = checked(anchor - (long)Math.Round(newSpan * anchorRatio));
            StartTick = Math.Max(0, newStart);
            TickSpan = newSpan;
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            long delta = Math.Max(1, TickSpan / 10);
            StartTick = e.Delta > 0 ? Math.Max(0, StartTick - delta) : checked(StartTick + delta);
        }
        else
        {
            if (SurfaceMode is TimelineSurfaceMode.EventLanes or TimelineSurfaceMode.Velocity)
            {
                double delta = Math.Max(1d / 256, ValueScrollViewportSize / 8);
                ValueScrollOffset += e.Delta > 0 ? -delta : delta;
            }
            else
            {
                int lanes = Math.Max(1, (int)Math.Ceiling(ActualHeight / LaneHeight) / 4);
                FirstLane += e.Delta > 0 ? -lanes : lanes;
            }
        }
        ViewportChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        bool hoverChangedVisual = _hoverPoint is not null
            && ToolMode == TimelineToolMode.Draw
            && SurfaceMode is TimelineSurfaceMode.Arrangement or TimelineSurfaceMode.PianoRoll;
        hoverChangedVisual |= _hoverLaneHeader is not null;
        _hoverPoint = null;
        _hoverLaneHeader = null;
        if (_dragItem is null)
        {
            bool directEditing = TimelineToolPolicy.IsDirectEditingSurface(SurfaceMode);
            Cursor = directEditing
                ? ToolMode == TimelineToolMode.Select ? Cursors.Cross : Cursors.Arrow
                : ToolMode == TimelineToolMode.Draw ? Cursors.Cross : Cursors.Arrow;
        }
        if (hoverChangedVisual)
        {
            InvalidateVisual();
        }
        base.OnMouseLeave(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (IsAltKey(e))
        {
            RefreshHoverIntent();
        }
        if (e.Key == Key.Escape && _pressedLaneHeader is not null)
        {
            _pressedLaneHeader = null;
            _laneHeaderDragActivated = false;
            Cursor = Cursors.Arrow;
            ReleaseMouseCapture();
            InvalidateVisual();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && (_panOrigin is not null || _marqueeOrigin is not null || _rulerDragOrigin is not null))
        {
            _panOrigin = null;
            _marqueeOrigin = null;
            _marqueeCurrent = null;
            _rulerDragOrigin = null;
            ReleaseMouseCapture();
            InvalidateVisual();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _dragItem is not null)
        {
            ClearItemDrag();
            ReleaseMouseCapture();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _notePlacementStartTick is not null)
        {
            _notePlacementStartTick = null;
            ReleaseMouseCapture();
            NotePlacementCancelled?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _segmentPlacementStartTick is not null)
        {
            _segmentPlacementStartTick = null;
            ReleaseMouseCapture();
            SegmentPlacementCancelled?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (IsAltKey(e))
        {
            RefreshHoverIntent();
        }
        base.OnKeyUp(e);
    }

    private bool TryCreateViewport(out TimelineViewport viewport)
    {
        double contentWidth = Math.Max(0, ActualWidth - GetLaneHeaderWidth());
        double contentHeight = Math.Max(0, ActualHeight - GetRulerHeight());
        int laneCount = LaneHeight > 0 && double.IsFinite(LaneHeight)
            ? Math.Max(1, (int)Math.Ceiling(contentHeight / LaneHeight))
            : 1;
        long span = TickSpan > 0 ? TickSpan : 1;
        long start = Math.Max(0, StartTick);
        long end = start <= long.MaxValue - span ? start + span : long.MaxValue;
        viewport = new(
            start,
            end,
            Math.Max(0, FirstLane),
            laneCount,
            contentWidth,
            contentHeight,
            LaneHeight);
        if (contentWidth <= 0 || contentHeight <= 0 || LaneHeight <= 0 || end <= start)
        {
            return false;
        }
        viewport.Validate();
        return true;
    }

    private void CompleteMarquee(Point origin, Point current)
    {
        if (Snapshot is null || !TryCreateViewport(out TimelineViewport viewport))
        {
            return;
        }
        if (!TryGetMarqueeBounds(
                origin,
                current,
                viewport,
                out _,
                out long start,
                out long end,
                out int firstLane,
                out int lastLaneExclusive))
        {
            RaiseBackgroundInvoked(origin, viewport, isDoubleClick: false);
            return;
        }
        Snapshot.Index.QueryInto(start, end, firstLane, lastLaneExclusive, _visibleItems);
        _marqueeIds.Clear();
        foreach (TimelineRenderItem item in _visibleItems)
        {
            if ((item.State & TimelineItemState.HitTestDisabled) == 0)
            {
                _marqueeIds.Add(item.Id);
            }
        }
        MarqueeCompleted?.Invoke(
            this,
            new TimelineMarqueeEventArgs(_marqueeIds.ToArray(), Keyboard.Modifiers));
    }

    private void DrawGrid(
        DrawingContext context,
        TimelineViewport viewport,
        double laneHeaderWidth,
        double rulerHeight)
    {
        long grid = Math.Max(1, GridStepTicks);
        long first = TimelineGridQuantization.GetGridTickAtOrAfter(
            viewport.StartTick,
            grid,
            DisplayGridUsesBars,
            TimeSignatureMap);
        for (long tick = first; tick < viewport.EndTick;)
        {
            double x = laneHeaderWidth + Math.Round(viewport.TickToX(tick)) + 0.5;
            context.DrawLine(_borderPen, new Point(x, rulerHeight), new Point(x, ActualHeight));
            if (tick == long.MaxValue)
            {
                break;
            }
            long next = TimelineGridQuantization.GetNextGridTick(
                tick,
                grid,
                DisplayGridUsesBars,
                TimeSignatureMap);
            if (next <= tick)
            {
                break;
            }
            tick = next;
        }
    }

    private void DrawActiveRange(
        DrawingContext context,
        TimelineViewport viewport,
        Brush brush,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (SurfaceMode == TimelineSurfaceMode.PianoRoll)
        {
            return;
        }
        if (RangeStartTick is not long start || RangeEndTick is not long end || end <= start)
        {
            return;
        }
        double left = laneHeaderWidth + viewport.TickToX(Math.Max(viewport.StartTick, start));
        double right = laneHeaderWidth + viewport.TickToX(Math.Min(viewport.EndTick, end));
        if (right > left)
        {
            context.PushOpacity(0.16);
            context.DrawRectangle(brush, null, new Rect(left, rulerHeight, right - left, Math.Max(0, ActualHeight - rulerHeight)));
            context.Pop();
        }
    }

    private void DrawPianoOutsideActiveRange(
        DrawingContext context,
        TimelineViewport viewport,
        Brush outsideBrush,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (SurfaceMode != TimelineSurfaceMode.PianoRoll
            || RangeStartTick is not long start
            || RangeEndTick is not long end
            || end <= start)
        {
            return;
        }

        double contentLeft = laneHeaderWidth;
        double contentRight = ActualWidth;
        double rangeLeft = Math.Clamp(
            laneHeaderWidth + viewport.TickToX(start),
            contentLeft,
            contentRight);
        double rangeRight = Math.Clamp(
            laneHeaderWidth + viewport.TickToX(end),
            contentLeft,
            contentRight);
        double height = Math.Max(0, ActualHeight - rulerHeight);
        context.PushOpacity(0.72);
        if (rangeLeft > contentLeft)
        {
            context.DrawRectangle(
                outsideBrush,
                null,
                new Rect(contentLeft, rulerHeight, rangeLeft - contentLeft, height));
        }
        if (rangeRight < contentRight)
        {
            context.DrawRectangle(
                outsideBrush,
                null,
                new Rect(rangeRight, rulerHeight, contentRight - rangeRight, height));
        }
        context.Pop();
    }

    private void DrawTimeRangeSelection(
        DrawingContext drawingContext,
        TimelineViewport viewport,
        Brush info,
        double laneHeaderWidth,
        double rulerHeight)
    {
        long? startValue = TimeRangeStartTick;
        long? endValue = TimeRangeEndTick;
        if (_rulerDragOrigin is not null)
        {
            startValue = Math.Min(_rulerDragStartTick, _rulerDragCurrentTick);
            endValue = Math.Max(_rulerDragStartTick, _rulerDragCurrentTick);
        }
        if (startValue is not long start || endValue is not long end || end <= start)
        {
            return;
        }
        long visibleStart = Math.Max(viewport.StartTick, start);
        long visibleEnd = Math.Min(viewport.EndTick, end);
        if (visibleEnd <= visibleStart) return;
        double left = laneHeaderWidth + viewport.TickToX(visibleStart);
        double right = laneHeaderWidth + viewport.TickToX(visibleEnd);
        Rect selection = new(
            left,
            rulerHeight,
            Math.Max(1, right - left),
            Math.Max(0, ActualHeight - rulerHeight));
        drawingContext.PushOpacity(0.14);
        drawingContext.DrawRectangle(info, null, selection);
        drawingContext.Pop();
        drawingContext.DrawRectangle(null, _infoPen, selection);
        drawingContext.DrawRectangle(info, null, new Rect(left, 0, Math.Max(1, right - left), 3));
    }

    private void DrawPianoNoteTiles(
        DrawingContext context,
        TimelineViewport viewport,
        Brush subVoiceNoteBrush,
        Brush warningBrush,
        Brush segmentNoteBrush,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (Snapshot is not TimelineRenderSnapshot snapshot)
        {
            return;
        }
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double actualPixelsPerTickDevice = viewport.PixelsPerTick * dpi.DpiScaleX;
        double actualPixelsPerLaneDevice = LaneHeight * dpi.DpiScaleY;
        long horizontalScaleKey = BitConverter.DoubleToInt64Bits(actualPixelsPerTickDevice);
        long verticalScaleKey = BitConverter.DoubleToInt64Bits(actualPixelsPerLaneDevice);
        long firstVisibleTileX = FloorToLong(
            viewport.StartTick * actualPixelsPerTickDevice / TimelinePianoTileRasterizer.TileSize);
        long lastVisibleTileX = FloorToLong(
            Math.Max(viewport.StartTick, viewport.EndTick - 1) * actualPixelsPerTickDevice
            / TimelinePianoTileRasterizer.TileSize);
        long firstVisibleTileY = FloorToLong(
            viewport.FirstLane * actualPixelsPerLaneDevice / TimelinePianoTileRasterizer.TileSize);
        long lastVisibleTileY = FloorToLong(
            Math.Max(viewport.FirstLane, viewport.LastLaneExclusive - 1) * actualPixelsPerLaneDevice
            / TimelinePianoTileRasterizer.TileSize);
        Color normalColor = GetSolidColor(
            RangeStartTick is not null && RangeEndTick is not null
                ? segmentNoteBrush
                : subVoiceNoteBrush,
            Color.FromRgb(163, 178, 190));
        Color warningColor = GetSolidColor(warningBrush, Color.FromRgb(232, 179, 75));
        int dpiX = checked((int)Math.Round(dpi.DpiScaleX * 1024, MidpointRounding.AwayFromZero));
        int dpiY = checked((int)Math.Round(dpi.DpiScaleY * 1024, MidpointRounding.AwayFromZero));
        Rect contentBounds = new(
            laneHeaderWidth,
            rulerHeight,
            Math.Max(0, ActualWidth - laneHeaderWidth),
            Math.Max(0, ActualHeight - rulerHeight));
        _pianoTileDrawEntries.Clear();
        _pianoTileVisibleKeys.Clear();
        bool allVisibleTilesReady = true;
        context.PushClip(new RectangleGeometry(contentBounds));
        for (int ring = 0; ring <= 1; ring++)
        {
            long firstX = Math.Max(0, firstVisibleTileX - ring);
            long lastX = Math.Max(firstX, lastVisibleTileX + ring);
            long firstY = Math.Max(0, firstVisibleTileY - ring);
            long lastY = Math.Max(firstY, lastVisibleTileY + ring);
            for (long tileY = firstY; tileY <= lastY; tileY++)
            {
                for (long tileX = firstX; tileX <= lastX; tileX++)
                {
                    bool visible = tileX >= firstVisibleTileX && tileX <= lastVisibleTileX
                        && tileY >= firstVisibleTileY && tileY <= lastVisibleTileY;
                    if (ring == 1 && visible)
                    {
                        continue;
                    }
                    TimelinePianoTileRasterRequest request = new(
                        snapshot,
                        actualPixelsPerTickDevice,
                        actualPixelsPerLaneDevice,
                        tileX,
                        tileY,
                        normalColor,
                        warningColor);
                    TimelineRasterCacheKey key = new(
                        TimelineRasterLayer.PianoNotes,
                        snapshot.ProjectionKey,
                        snapshot.GetPianoTileContentFingerprint(
                            actualPixelsPerTickDevice,
                            actualPixelsPerLaneDevice,
                            request.TileX,
                            request.TileY),
                        horizontalScaleKey,
                        verticalScaleKey,
                        request.TileX,
                        request.TileY,
                        ColorToArgb(normalColor),
                        ColorToArgb(warningColor),
                        0,
                        dpiX,
                        dpiY);
                    if (TimelineRasterCache.Shared.TryGet(key, out BitmapSource? bitmap))
                    {
                        if (visible && bitmap is not null)
                        {
                            _pianoTileDrawEntries.Add(new(key, bitmap));
                        }
                        if (visible) _pianoTileVisibleKeys.Add(key);
                        continue;
                    }
                    if (visible)
                    {
                        allVisibleTilesReady = false;
                        _pianoTileVisibleKeys.Add(key);
                    }
                    RequestRaster(
                        key,
                        request.Rasterize);
                }
            }
        }
        PresentPianoRasterLayer(
            context,
            viewport,
            snapshot.ProjectionKey,
            actualPixelsPerTickDevice,
            actualPixelsPerLaneDevice,
            laneHeaderWidth,
            rulerHeight,
            allVisibleTilesReady,
            ref _lastCompletePianoNoteFrame);
        context.Pop();
    }

    private void DrawPianoSelectionOverlay(
        DrawingContext context,
        TimelineViewport viewport,
        Brush selectedBrush,
        Brush warningBrush,
        Brush outlineBrush,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (Snapshot is not TimelineRenderSnapshot snapshot
            || SelectionSnapshot is not TimelineSelectionSnapshot { Count: > 0 } selection)
        {
            _lastCompletePianoSelectionFrame = null;
            return;
        }
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double actualPixelsPerTickDevice = viewport.PixelsPerTick * dpi.DpiScaleX;
        double actualPixelsPerLaneDevice = LaneHeight * dpi.DpiScaleY;
        long horizontalScaleKey = BitConverter.DoubleToInt64Bits(actualPixelsPerTickDevice);
        long verticalScaleKey = BitConverter.DoubleToInt64Bits(actualPixelsPerLaneDevice);
        long firstTileX = Math.Max(0, FloorToLong(
            viewport.StartTick * actualPixelsPerTickDevice / TimelinePianoTileRasterizer.TileSize));
        long lastTileX = Math.Max(firstTileX, FloorToLong(
            Math.Max(viewport.StartTick, viewport.EndTick - 1) * actualPixelsPerTickDevice
            / TimelinePianoTileRasterizer.TileSize));
        long firstTileY = Math.Max(0, FloorToLong(
            viewport.FirstLane * actualPixelsPerLaneDevice / TimelinePianoTileRasterizer.TileSize));
        long lastTileY = Math.Max(firstTileY, FloorToLong(
            Math.Max(viewport.FirstLane, viewport.LastLaneExclusive - 1) * actualPixelsPerLaneDevice
            / TimelinePianoTileRasterizer.TileSize));
        Color selectedColor = GetSolidColor(selectedBrush, Color.FromRgb(143, 36, 41));
        Color warningColor = GetSolidColor(warningBrush, Color.FromRgb(232, 179, 75));
        Color outlineColor = GetSolidColor(outlineBrush, Color.FromRgb(229, 61, 68));
        int dpiX = checked((int)Math.Round(dpi.DpiScaleX * 1024, MidpointRounding.AwayFromZero));
        int dpiY = checked((int)Math.Round(dpi.DpiScaleY * 1024, MidpointRounding.AwayFromZero));
        Rect contentBounds = new(
            laneHeaderWidth,
            rulerHeight,
            Math.Max(0, ActualWidth - laneHeaderWidth),
            Math.Max(0, ActualHeight - rulerHeight));
        _pianoTileDrawEntries.Clear();
        _pianoTileVisibleKeys.Clear();
        bool allVisibleTilesReady = true;
        context.PushClip(new RectangleGeometry(contentBounds));
        for (long tileY = firstTileY; tileY <= lastTileY; tileY++)
        {
            for (long tileX = firstTileX; tileX <= lastTileX; tileX++)
            {
                ulong contentFingerprint = TimelineContentFingerprint.WithSelection(
                    snapshot.GetPianoTileContentFingerprint(
                        actualPixelsPerTickDevice,
                        actualPixelsPerLaneDevice,
                        tileX,
                        tileY),
                    selection.Revision);
                TimelineRasterCacheKey key = new(
                    TimelineRasterLayer.PianoSelection,
                    snapshot.ProjectionKey,
                    contentFingerprint,
                    horizontalScaleKey,
                    verticalScaleKey,
                    tileX,
                    tileY,
                    ColorToArgb(selectedColor),
                    ColorToArgb(warningColor),
                    ColorToArgb(outlineColor),
                    dpiX,
                    dpiY);
                if (TimelineRasterCache.Shared.TryGet(key, out BitmapSource? bitmap))
                {
                    if (bitmap is not null)
                    {
                        _pianoTileDrawEntries.Add(new(key, bitmap));
                    }
                    _pianoTileVisibleKeys.Add(key);
                    continue;
                }
                allVisibleTilesReady = false;
                _pianoTileVisibleKeys.Add(key);
                long requestTileX = tileX;
                long requestTileY = tileY;
                RequestRaster(
                    key,
                    () => TimelinePianoTileRasterizer.Rasterize(
                        snapshot,
                        actualPixelsPerTickDevice,
                        actualPixelsPerLaneDevice,
                        requestTileX,
                        requestTileY,
                        selectedColor,
                        warningColor,
                        selection,
                        selectionOnly: true,
                        outlineColor: outlineColor));
            }
        }
        PresentPianoRasterLayer(
            context,
            viewport,
            snapshot.ProjectionKey,
            actualPixelsPerTickDevice,
            actualPixelsPerLaneDevice,
            laneHeaderWidth,
            rulerHeight,
            allVisibleTilesReady,
            ref _lastCompletePianoSelectionFrame);
        context.Pop();

        if (selection.Primary is MidoraId primaryId
            && snapshot.ItemsById.TryGetValue(primaryId, out TimelineRenderItem primaryItem)
            && primaryItem.Kind is TimelineItemKind.LogicalNote or TimelineItemKind.TemplateNote
            && primaryItem.EndTick > viewport.StartTick
            && primaryItem.StartTick < viewport.EndTick
            && primaryItem.Lane >= viewport.FirstLane
            && primaryItem.Lane < viewport.LastLaneExclusive)
        {
            Rect bounds = TimelineRasterPlacement.GetUnclippedItemBounds(
                viewport, primaryItem, laneHeaderWidth, rulerHeight, LaneHeight);
            if (bounds.Width > 4 && bounds.Height > 4)
            {
                context.DrawRectangle(
                    null,
                    _selectionPen,
                    new Rect(bounds.Left + 1, bounds.Top + 1, bounds.Width - 2, bounds.Height - 2));
            }
        }
    }

    private void PresentPianoRasterLayer(
        DrawingContext context,
        TimelineViewport viewport,
        string projectionKey,
        double currentPixelsPerTickDevice,
        double currentPixelsPerLaneDevice,
        double laneHeaderWidth,
        double rulerHeight,
        bool allVisibleTilesReady,
        ref PianoRasterFrame? lastCompleteFrame)
    {
        if (allVisibleTilesReady)
        {
            DrawPianoTileEntries(
                context,
                viewport,
                _pianoTileDrawEntries,
                currentPixelsPerTickDevice,
                currentPixelsPerLaneDevice,
                laneHeaderWidth,
                rulerHeight);
            if (lastCompleteFrame?.Matches(projectionKey, _pianoTileVisibleKeys) != true)
            {
                lastCompleteFrame = new(projectionKey, _pianoTileVisibleKeys.ToArray());
            }
            _pianoTileDrawEntries.Clear();
            return;
        }

        if (TryDrawCompletePianoFallback(
                context,
                viewport,
                projectionKey,
                laneHeaderWidth,
                rulerHeight,
                lastCompleteFrame))
        {
            _pianoTileDrawEntries.Clear();
            return;
        }

        DrawPianoTileEntries(
            context,
            viewport,
            _pianoTileDrawEntries,
            currentPixelsPerTickDevice,
            currentPixelsPerLaneDevice,
            laneHeaderWidth,
            rulerHeight);
        _pianoTileDrawEntries.Clear();
    }

    private bool TryDrawCompletePianoFallback(
        DrawingContext context,
        TimelineViewport viewport,
        string projectionKey,
        double laneHeaderWidth,
        double rulerHeight,
        PianoRasterFrame? frame)
    {
        if (frame is null
            || !string.Equals(frame.ProjectionKey, projectionKey, StringComparison.Ordinal))
        {
            return false;
        }

        _pianoTileFallbackEntries.Clear();
        foreach (TimelineRasterCacheKey key in frame.Keys)
        {
            if (!TimelineRasterCache.Shared.TryGet(key, out BitmapSource? bitmap)
                || bitmap is null)
            {
                _pianoTileFallbackEntries.Clear();
                return false;
            }
            _pianoTileFallbackEntries.Add(new(key, bitmap));
        }

        foreach (PianoTileDrawEntry entry in _pianoTileFallbackEntries)
        {
            double pixelsPerTickDevice = BitConverter.Int64BitsToDouble(entry.Key.HorizontalScaleKey);
            double pixelsPerLaneDevice = BitConverter.Int64BitsToDouble(entry.Key.VerticalScaleKey);
            DrawPianoTile(
                context,
                viewport,
                entry,
                pixelsPerTickDevice,
                pixelsPerLaneDevice,
                laneHeaderWidth,
                rulerHeight);
        }
        _pianoTileFallbackEntries.Clear();
        return true;
    }

    private void DrawPianoTileEntries(
        DrawingContext context,
        TimelineViewport viewport,
        IReadOnlyList<PianoTileDrawEntry> entries,
        double pixelsPerTickDevice,
        double pixelsPerLaneDevice,
        double laneHeaderWidth,
        double rulerHeight)
    {
        foreach (PianoTileDrawEntry entry in entries)
        {
            DrawPianoTile(
                context,
                viewport,
                entry,
                pixelsPerTickDevice,
                pixelsPerLaneDevice,
                laneHeaderWidth,
                rulerHeight);
        }
    }

    private void DrawPianoTile(
        DrawingContext context,
        TimelineViewport viewport,
        PianoTileDrawEntry entry,
        double pixelsPerTickDevice,
        double pixelsPerLaneDevice,
        double laneHeaderWidth,
        double rulerHeight)
    {
        Rect destination = TimelineRasterPlacement.GetPianoTileDestination(
            viewport,
            pixelsPerTickDevice,
            pixelsPerLaneDevice,
            entry.Key.TileX,
            entry.Key.TileY,
            laneHeaderWidth,
            rulerHeight,
            LaneHeight);
        Rect coreDestination = TimelineRasterPlacement.GetPianoTileCoreDestination(
            viewport,
            pixelsPerTickDevice,
            pixelsPerLaneDevice,
            entry.Key.TileX,
            entry.Key.TileY,
            laneHeaderWidth,
            rulerHeight,
            LaneHeight);
        context.PushClip(new RectangleGeometry(coreDestination));
        context.DrawImage(entry.Bitmap, destination);
        context.Pop();
    }

    private void RequestRaster(TimelineRasterCacheKey key, Func<TimelineRasterBuffer> factory)
    {
        if (!_requestedRasterKeys.Add(key))
        {
            return;
        }
        bool accepted = TimelineRasterCache.Shared.Request(
            key,
            factory,
            Dispatcher,
            () =>
            {
                _requestedRasterKeys.Remove(key);
                if (Snapshot is TimelineRenderSnapshot snapshot
                    && string.Equals(snapshot.ProjectionKey, key.ProjectionKey, StringComparison.Ordinal))
                {
                    InvalidateVisual();
                }
            });
        if (!accepted)
        {
            _requestedRasterKeys.Remove(key);
        }
    }

    private static long FloorToLong(double value) => value <= long.MinValue
        ? long.MinValue
        : value >= long.MaxValue
            ? long.MaxValue
            : (long)Math.Floor(value);

    private static Color GetSolidColor(Brush brush, Color fallback) =>
        brush is SolidColorBrush solid ? solid.Color : fallback;

    private static uint ColorToArgb(Color color) =>
        ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

    private bool IsSelected(TimelineRenderItem item) =>
        SelectionSnapshot?.Contains(item.Id)
        ?? item.State.HasFlag(TimelineItemState.Selected);

    private bool IsPrimary(TimelineRenderItem item) =>
        SelectionSnapshot is TimelineSelectionSnapshot selection
            ? selection.Primary == item.Id
            : item.State.HasFlag(TimelineItemState.Primary);

    private void DrawItem(
        DrawingContext context,
        TimelineViewport viewport,
        TimelineRenderItem item,
        Brush red,
        Brush redDark,
        Brush info,
        Brush warning,
        Brush segment,
        Brush selectedSegment,
        Brush segmentNotePreview,
        Brush segmentPianoNote,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (item.Kind == TimelineItemKind.Velocity)
        {
            DrawVelocityBar(
                context,
                viewport,
                item,
                segmentPianoNote,
                red,
                laneHeaderWidth,
                rulerHeight);
            return;
        }
        if (item.Kind == TimelineItemKind.LogicalParameterCurve)
        {
            DrawCurve(context, viewport, item, info, laneHeaderWidth, rulerHeight);
            return;
        }
        if (item.Kind == TimelineItemKind.LogicalParameterPoint)
        {
            DrawCurvePoint(context, viewport, item, info, laneHeaderWidth, rulerHeight);
            return;
        }
        double left = laneHeaderWidth + Math.Max(-1, viewport.TickToX(item.StartTick));
        double right = laneHeaderWidth + Math.Min(viewport.Width + 1, viewport.TickToX(item.EndTick));
        double top = rulerHeight + (item.Lane - viewport.FirstLane) * LaneHeight + 3;
        double height = Math.Max(3, LaneHeight - 6);
        if (right <= left || top >= ActualHeight || top + height <= 0)
        {
            return;
        }

        bool selected = IsSelected(item);
        bool isSegmentPianoRoll = SurfaceMode == TimelineSurfaceMode.PianoRoll
            && RangeStartTick is not null
            && RangeEndTick is not null;
        Brush fill = item.State.HasFlag(TimelineItemState.Invalid)
            || item.State.HasFlag(TimelineItemState.Broken)
            ? warning
            : item.Kind == TimelineItemKind.Segment
                ? selected ? selectedSegment : segment
            : item.Kind is TimelineItemKind.LogicalNote or TimelineItemKind.TemplateNote
                ? selected ? redDark : isSegmentPianoRoll ? segmentPianoNote : red
                : info;
        double opacity = item.State.HasFlag(TimelineItemState.OutsideActiveRange)
            ? 0.35
            : item.Kind == TimelineItemKind.Segment ? 0.88 : 0.78;
        context.PushOpacity(opacity);
        Rect rectangle = new(left, top, Math.Max(1, right - left), height);
        context.DrawRoundedRectangle(fill, _borderPen, rectangle, 2, 2);
        context.Pop();

        if (item.Kind == TimelineItemKind.Segment
            && Snapshot?.SegmentPreviews.TryGetValue(item.Id, out TimelineSegmentPreview? preview) == true)
        {
            Rect fullSegmentBounds = TimelineRasterPlacement.GetUnclippedItemBounds(
                viewport,
                item,
                laneHeaderWidth,
                rulerHeight,
                LaneHeight);
            DrawSegmentPreview(
                context,
                rectangle,
                fullSegmentBounds,
                preview,
                segmentNotePreview);
        }

        if (selected)
        {
            Pen selectionPen = item.Kind == TimelineItemKind.Segment
                ? _segmentSelectionPen!
                : _selectionPen!;
            context.DrawRoundedRectangle(null, selectionPen, rectangle, 2, 2);
            if (IsPrimary(item)
                && rectangle.Width > 4
                && rectangle.Height > 4)
            {
                Rect primary = new(
                    rectangle.Left + 1,
                    rectangle.Top + 1,
                    rectangle.Width - 2,
                    rectangle.Height - 2);
                context.DrawRoundedRectangle(null, selectionPen, primary, 1, 1);
            }
        }
    }

    private void DrawSegmentPreview(
        DrawingContext context,
        Rect visibleSegmentBounds,
        Rect fullSegmentBounds,
        TimelineSegmentPreview preview,
        Brush noteBrush)
    {
        if (preview.Notes.Count == 0
            || visibleSegmentBounds.Width <= 0
            || visibleSegmentBounds.Height <= 0
            || fullSegmentBounds.Width <= 0
            || fullSegmentBounds.Height <= 0)
        {
            return;
        }
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        Color noteColor = GetSolidColor(noteBrush, Color.FromRgb(189, 199, 207));
        TimelineRasterCacheKey key = new(
            TimelineRasterLayer.ArrangementSegmentPreview,
            $"segment-preview:{preview.SegmentId.Value}",
            preview.ContentFingerprint,
            0,
            0,
            preview.SegmentId.Value,
            0,
            ColorToArgb(noteColor),
            0,
            0,
            checked((int)Math.Round(dpi.DpiScaleX * 1024, MidpointRounding.AwayFromZero)),
            checked((int)Math.Round(dpi.DpiScaleY * 1024, MidpointRounding.AwayFromZero)));
        if (TimelineRasterCache.Shared.TryGet(key, out BitmapSource? bitmap) && bitmap is not null)
        {
            context.PushClip(new RectangleGeometry(visibleSegmentBounds));
            context.DrawImage(bitmap, fullSegmentBounds);
            context.Pop();
            return;
        }
        if (!_requestedRasterKeys.Add(key)) return;
        bool accepted = TimelineRasterCache.Shared.Request(
            key,
            () => TimelineSegmentPreviewRasterizer.Rasterize(preview, noteColor),
            Dispatcher,
            () =>
            {
                _requestedRasterKeys.Remove(key);
                if (Snapshot?.SegmentPreviews.TryGetValue(preview.SegmentId, out TimelineSegmentPreview? current) == true
                    && current.ContentFingerprint == preview.ContentFingerprint)
                {
                    InvalidateVisual();
                }
            });
        if (!accepted)
        {
            _requestedRasterKeys.Remove(key);
        }
    }

    private void DrawVelocityTiles(
        DrawingContext context,
        TimelineViewport viewport,
        Brush normalBrush,
        Brush selectedBrush,
        Brush borderBrush,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (Snapshot is not TimelineRenderSnapshot snapshot) return;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int horizontalLod = TimelineRasterLod.Quantize(viewport.PixelsPerTick * dpi.DpiScaleX);
        double lodPixelsPerTick = TimelineRasterLod.GetScale(horizontalLod);
        long firstTileX = Math.Max(0, FloorToLong(
            viewport.StartTick * lodPixelsPerTick / TimelineVelocityTileRasterizer.TileSize));
        long lastTileX = Math.Max(firstTileX, FloorToLong(
            Math.Max(viewport.StartTick, viewport.EndTick - 1) * lodPixelsPerTick
            / TimelineVelocityTileRasterizer.TileSize));
        TimelineSelectionSnapshot? selection = SelectionSnapshot;
        Color normalColor = GetSolidColor(normalBrush, Color.FromRgb(163, 178, 190));
        Color selectedColor = GetSolidColor(selectedBrush, Color.FromRgb(229, 61, 68));
        Color borderColor = GetSolidColor(borderBrush, Color.FromRgb(49, 58, 69));
        int dpiX = checked((int)Math.Round(dpi.DpiScaleX * 1024, MidpointRounding.AwayFromZero));
        int dpiY = checked((int)Math.Round(dpi.DpiScaleY * 1024, MidpointRounding.AwayFromZero));
        double valueRange = Math.Max(1d / 256, _valueViewMaximum - _valueViewMinimum);
        double contentHeight = Math.Max(1, ActualHeight - rulerHeight);
        double valueTop = rulerHeight + (_valueViewMaximum - 1) / valueRange * contentHeight;
        double valueBottom = rulerHeight + _valueViewMaximum / valueRange * contentHeight;
        Rect contentBounds = new(
            laneHeaderWidth,
            rulerHeight,
            Math.Max(0, ActualWidth - laneHeaderWidth),
            Math.Max(0, ActualHeight - rulerHeight));
        _velocityTileDrawEntries.Clear();
        _velocityTileVisibleKeys.Clear();
        bool allVisibleTilesReady = true;
        context.PushClip(new RectangleGeometry(contentBounds));
        for (int ring = 0; ring <= 1; ring++)
        {
            long firstX = Math.Max(0, firstTileX - ring);
            long lastX = Math.Max(firstX, lastTileX + ring);
            for (long tileX = firstX; tileX <= lastX; tileX++)
            {
                bool visible = tileX >= firstTileX && tileX <= lastTileX;
                if (ring == 1 && visible) continue;
                ulong fingerprint = TimelineVelocityTileRasterizer.ComputeContentFingerprint(
                    snapshot, selection, horizontalLod, tileX);
                TimelineRasterCacheKey key = new(
                    TimelineRasterLayer.VelocityBars,
                    snapshot.ProjectionKey,
                    fingerprint,
                    horizontalLod,
                    0,
                    tileX,
                    0,
                    ColorToArgb(normalColor),
                    ColorToArgb(selectedColor),
                    ColorToArgb(borderColor),
                    dpiX,
                    dpiY);
                if (TimelineRasterCache.Shared.TryGet(key, out BitmapSource? bitmap))
                {
                    if (visible && bitmap is not null)
                    {
                        _velocityTileDrawEntries.Add(new(key, bitmap));
                    }
                    if (visible) _velocityTileVisibleKeys.Add(key);
                    continue;
                }
                if (visible)
                {
                    allVisibleTilesReady = false;
                    _velocityTileVisibleKeys.Add(key);
                }
                long requestTileX = tileX;
                RequestRaster(
                    key,
                    () => TimelineVelocityTileRasterizer.Rasterize(
                        snapshot,
                        selection,
                        horizontalLod,
                        requestTileX,
                        normalColor,
                        selectedColor,
                        borderColor));
            }
        }
        PresentVelocityRasterLayer(
            context,
            viewport,
            snapshot.ProjectionKey,
            horizontalLod,
            laneHeaderWidth,
            rulerHeight,
            valueTop,
            valueBottom,
            allVisibleTilesReady);
        context.Pop();
    }

    private void PresentVelocityRasterLayer(
        DrawingContext context,
        TimelineViewport viewport,
        string projectionKey,
        int currentHorizontalLod,
        double laneHeaderWidth,
        double rulerHeight,
        double valueTop,
        double valueBottom,
        bool allVisibleTilesReady)
    {
        if (allVisibleTilesReady)
        {
            DrawVelocityTileEntries(
                context,
                viewport,
                _velocityTileDrawEntries,
                currentHorizontalLod,
                laneHeaderWidth,
                rulerHeight,
                valueTop,
                valueBottom);
            if (_lastCompleteVelocityFrame?.Matches(projectionKey, _velocityTileVisibleKeys) != true)
            {
                _lastCompleteVelocityFrame = new(projectionKey, _velocityTileVisibleKeys.ToArray());
            }
            _velocityTileDrawEntries.Clear();
            return;
        }

        if (TryDrawCompleteVelocityFallback(
                context,
                viewport,
                projectionKey,
                laneHeaderWidth,
                rulerHeight,
                valueTop,
                valueBottom))
        {
            _velocityTileDrawEntries.Clear();
            return;
        }

        DrawVelocityTileEntries(
            context,
            viewport,
            _velocityTileDrawEntries,
            currentHorizontalLod,
            laneHeaderWidth,
            rulerHeight,
            valueTop,
            valueBottom);
        _velocityTileDrawEntries.Clear();
    }

    private bool TryDrawCompleteVelocityFallback(
        DrawingContext context,
        TimelineViewport viewport,
        string projectionKey,
        double laneHeaderWidth,
        double rulerHeight,
        double valueTop,
        double valueBottom)
    {
        if (_lastCompleteVelocityFrame is not VelocityRasterFrame frame
            || !string.Equals(frame.ProjectionKey, projectionKey, StringComparison.Ordinal))
        {
            return false;
        }

        _velocityTileFallbackEntries.Clear();
        foreach (TimelineRasterCacheKey key in frame.Keys)
        {
            if (!TimelineRasterCache.Shared.TryGet(key, out BitmapSource? bitmap)
                || bitmap is null)
            {
                _velocityTileFallbackEntries.Clear();
                return false;
            }
            _velocityTileFallbackEntries.Add(new(key, bitmap));
        }

        foreach (VelocityTileDrawEntry entry in _velocityTileFallbackEntries)
        {
            DrawVelocityTile(
                context,
                viewport,
                entry,
                checked((int)entry.Key.HorizontalScaleKey),
                laneHeaderWidth,
                rulerHeight,
                valueTop,
                valueBottom);
        }
        _velocityTileFallbackEntries.Clear();
        return true;
    }

    private void DrawVelocityTileEntries(
        DrawingContext context,
        TimelineViewport viewport,
        IReadOnlyList<VelocityTileDrawEntry> entries,
        int horizontalLod,
        double laneHeaderWidth,
        double rulerHeight,
        double valueTop,
        double valueBottom)
    {
        foreach (VelocityTileDrawEntry entry in entries)
        {
            DrawVelocityTile(
                context,
                viewport,
                entry,
                horizontalLod,
                laneHeaderWidth,
                rulerHeight,
                valueTop,
                valueBottom);
        }
    }

    private static void DrawVelocityTile(
        DrawingContext context,
        TimelineViewport viewport,
        VelocityTileDrawEntry entry,
        int horizontalLod,
        double laneHeaderWidth,
        double rulerHeight,
        double valueTop,
        double valueBottom)
    {
        Rect destination = TimelineRasterPlacement.GetVelocityTileDestination(
            viewport, horizontalLod, entry.Key.TileX, laneHeaderWidth, rulerHeight,
            valueTop, valueBottom);
        Rect coreDestination = TimelineRasterPlacement.GetVelocityTileCoreDestination(
            viewport, horizontalLod, entry.Key.TileX, laneHeaderWidth, rulerHeight,
            valueTop, valueBottom);
        context.PushClip(new RectangleGeometry(coreDestination));
        context.DrawImage(entry.Bitmap, destination);
        context.Pop();
    }

    private void DrawVelocityEditOverlay(
        DrawingContext context,
        TimelineViewport viewport,
        Brush normalBrush,
        Brush selectedBrush,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (_velocityOrigin is null) return;
        if (_velocityDirectItemId is MidoraId directId
            && Snapshot is TimelineRenderSnapshot snapshot
            && _velocityEdits.ContainsKey(directId)
            && snapshot.ItemsById.TryGetValue(directId, out TimelineRenderItem item)
            && item.EndTick > viewport.StartTick
            && item.StartTick < viewport.EndTick)
        {
            DrawVelocityBar(
                context,
                viewport,
                item,
                normalBrush,
                selectedBrush,
                laneHeaderWidth,
                rulerHeight);
            return;
        }
        DrawVelocityTrace(context, laneHeaderWidth, rulerHeight);
    }

    private void DrawVelocityBar(
        DrawingContext context,
        TimelineViewport viewport,
        TimelineRenderItem item,
        Brush normalBrush,
        Brush selectedBrush,
        double laneHeaderWidth,
        double rulerHeight)
    {
        double value = _velocityEdits.TryGetValue(item.Id, out int edited)
            ? edited / 127d
            : Math.Clamp(item.Value, 1d / 127d, 1);
        double centerX = laneHeaderWidth + viewport.TickToX(item.StartTick);
        double valueY = NormalizedToValueY(value, rulerHeight);
        double zeroY = NormalizedToValueY(0, rulerHeight);
        double top = Math.Clamp(Math.Min(valueY, zeroY), rulerHeight, ActualHeight);
        double bottom = Math.Clamp(Math.Max(valueY, zeroY), rulerHeight, ActualHeight);
        const double stemWidth = TimelineVelocityTileRasterizer.StemWidth;
        const double markerSize = TimelineVelocityTileRasterizer.MarkerSize;
        Rect bar = new(centerX - stemWidth / 2, top, stemWidth, Math.Max(1, bottom - top));
        bool selected = IsSelected(item);
        Brush barBrush = selected ? selectedBrush : normalBrush;
        context.PushOpacity(selected ? 0.4 : 0.2);
        context.DrawRectangle(barBrush, null, bar);
        context.Pop();
        context.PushOpacity(selected ? 1 : 0.86);
        context.DrawRectangle(null, _borderPen, bar);
        context.Pop();

        if (centerX + markerSize / 2 >= laneHeaderWidth && centerX - markerSize / 2 < ActualWidth)
        {
            Rect onsetMarker = new(
                centerX - markerSize / 2,
                top,
                markerSize,
                markerSize);
            context.DrawRectangle(barBrush, _borderPen, onsetMarker);
        }
    }

    private void DrawVelocityTrace(
        DrawingContext context,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (_velocityTracePoints.Count == 0) return;
        Rect contentBounds = new(
            laneHeaderWidth,
            rulerHeight,
            Math.Max(0, ActualWidth - laneHeaderWidth),
            Math.Max(0, ActualHeight - rulerHeight));
        context.PushClip(new RectangleGeometry(contentBounds));
        if (_velocityTracePoints.Count == 1)
        {
            Point point = _velocityTracePoints[0];
            context.DrawEllipse(_selectionPen!.Brush, null, point, 2, 2);
        }
        else
        {
            StreamGeometry geometry = new();
            using (StreamGeometryContext geometryContext = geometry.Open())
            {
                geometryContext.BeginFigure(_velocityTracePoints[0], isFilled: false, isClosed: false);
                for (int index = 1; index < _velocityTracePoints.Count; index++)
                {
                    geometryContext.LineTo(_velocityTracePoints[index], isStroked: true, isSmoothJoin: true);
                }
            }
            geometry.Freeze();
            context.DrawGeometry(null, _selectionPen, geometry);
        }
        context.Pop();
    }

    private void DrawValueGrid(
        DrawingContext context,
        Brush text,
        double laneHeaderWidth,
        double rulerHeight,
        bool drawLabels)
    {
        double minimum = ValueAxisMinimum;
        double maximum = ValueAxisMaximum;
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum)
        {
            minimum = 0;
            maximum = 127;
        }
        string? previousLabel = null;
        for (int index = 0; index <= 4; index++)
        {
            double screenRatio = index / 4d;
            double normalized = _valueViewMinimum
                + screenRatio * (_valueViewMaximum - _valueViewMinimum);
            double value = minimum + normalized * (maximum - minimum);
            string labelText = ValueAxisIntegral
                ? Math.Round(value, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)
                : value.ToString("0.###", CultureInfo.InvariantCulture);
            if (string.Equals(previousLabel, labelText, StringComparison.Ordinal)) continue;
            previousLabel = labelText;
            double y = NormalizedToValueY(normalized, rulerHeight);
            if (drawLabels)
            {
                FormattedText label = GetFormattedText(labelText, text, 9, FontWeights.Normal);
                double labelY = Math.Clamp(
                    y - label.Height / 2,
                    rulerHeight,
                    Math.Max(rulerHeight, ActualHeight - label.Height));
                context.DrawText(label, new Point(Math.Max(2, laneHeaderWidth - label.Width - 4), labelY));
            }
            else
            {
                context.DrawLine(_borderPen, new Point(laneHeaderWidth, y), new Point(ActualWidth, y));
            }
        }
    }

    private void ApplyVelocityTraceSegment(
        Point from,
        Point to,
        TimelineViewport viewport)
    {
        if (Snapshot is null) return;
        double header = GetLaneHeaderWidth();
        double ruler = GetRulerHeight();
        long fromTick = viewport.XToTick(from.X - header);
        long toTick = viewport.XToTick(to.X - header);
        long minimum = Math.Min(fromTick, toTick);
        long maximum = Math.Max(fromTick, toTick);
        double span = toTick - fromTick;
        Snapshot.Index.QueryInto(
            minimum,
            maximum == long.MaxValue ? long.MaxValue : Math.Max(minimum + 1, maximum + 1),
            0,
            1,
            _visibleItems);
        foreach (TimelineRenderItem item in _visibleItems)
        {
            if (item.Kind != TimelineItemKind.Velocity
                || (_velocitySelectionRestricted && !IsSelected(item))
                || item.EndTick <= minimum
                || item.StartTick > maximum)
            {
                continue;
            }
            long sampleTick = Math.Clamp(item.StartTick, minimum, maximum);
            double ratio = span == 0 ? 1 : (sampleTick - fromTick) / span;
            double y = from.Y + (to.Y - from.Y) * Math.Clamp(ratio, 0, 1);
            int velocity = Math.Clamp(
                (int)Math.Round(
                    ValueYToNormalized(y, ruler) * 127,
                    MidpointRounding.AwayFromZero),
                1,
                127);
            _velocityEdits[item.Id] = velocity;
        }
    }

    private void UpdateVelocityTrace(Point origin, Point point)
    {
        Point clamped = ClampVelocityTracePoint(point);
        if (_velocityButton == MouseButton.Right)
        {
            _velocityTracePoints.Clear();
            _velocityTracePoints.Add(ClampVelocityTracePoint(origin));
            _velocityTracePoints.Add(clamped);
            return;
        }

        if (_velocityTracePoints.Count == 0)
        {
            _velocityTracePoints.Add(ClampVelocityTracePoint(origin));
        }
        Point previous = _velocityTracePoints[^1];
        double deltaX = clamped.X - previous.X;
        double deltaY = clamped.Y - previous.Y;
        if (deltaX * deltaX + deltaY * deltaY >= 1)
        {
            _velocityTracePoints.Add(clamped);
        }
        else
        {
            _velocityTracePoints[^1] = clamped;
        }
    }

    private Point ClampVelocityTracePoint(Point point) => new(
        Math.Clamp(point.X, GetLaneHeaderWidth(), Math.Max(GetLaneHeaderWidth(), ActualWidth)),
        Math.Clamp(point.Y, GetRulerHeight(), Math.Max(GetRulerHeight(), ActualHeight)));

    private void BuildVelocityEditsFromTrace(TimelineViewport viewport)
    {
        _velocityEdits.Clear();
        if (_velocityTracePoints.Count == 0) return;
        if (_velocityTracePoints.Count == 1)
        {
            ApplyVelocityTraceSegment(_velocityTracePoints[0], _velocityTracePoints[0], viewport);
            return;
        }
        for (int index = 1; index < _velocityTracePoints.Count; index++)
        {
            ApplyVelocityTraceSegment(_velocityTracePoints[index - 1], _velocityTracePoints[index], viewport);
        }
    }

    private void UpdateSingleVelocity(MidoraId id, double y, double rulerHeight)
    {
        int velocity = Math.Clamp(
            (int)Math.Round(ValueYToNormalized(y, rulerHeight) * 127, MidpointRounding.AwayFromZero),
            1,
            127);
        _velocityEdits[id] = velocity;
    }

    private bool TryHitVelocityBar(
        Point point,
        TimelineViewport viewport,
        out TimelineRenderItem item)
    {
        item = default;
        if (Snapshot is null) return false;
        double header = GetLaneHeaderWidth();
        double ruler = GetRulerHeight();
        long tick = viewport.XToTick(point.X - header);
        double horizontalTolerance = TimelineVelocityTileRasterizer.MarkerSize / 2 + 2;
        long tolerance = Math.Max(1, (long)Math.Ceiling(horizontalTolerance / viewport.PixelsPerTick));
        Snapshot.Index.HitTestInto(tick, tolerance, 0, _visibleItems);
        for (int index = 0; index < _visibleItems.Count; index++)
        {
            TimelineRenderItem candidate = _visibleItems[index];
            if (candidate.Kind != TimelineItemKind.Velocity)
            {
                continue;
            }
            double centerX = header + viewport.TickToX(candidate.StartTick);
            double value = _velocityEdits.TryGetValue(candidate.Id, out int edited)
                ? edited / 127d
                : Math.Clamp(candidate.Value, 1d / 127d, 1);
            double top = NormalizedToValueY(value, ruler);
            double bottom = NormalizedToValueY(0, ruler);
            if (Math.Abs(point.X - centerX) <= horizontalTolerance
                && point.Y >= top - 2
                && point.Y <= Math.Max(top + TimelineVelocityTileRasterizer.MarkerSize, bottom + 2))
            {
                item = candidate;
                return true;
            }
        }
        return false;
    }

    private void DrawCurve(
        DrawingContext context,
        TimelineViewport viewport,
        TimelineRenderItem item,
        Brush info,
        double laneHeaderWidth,
        double rulerHeight)
    {
        double left = laneHeaderWidth + viewport.TickToX(item.StartTick);
        double right = laneHeaderWidth + viewport.TickToX(item.EndTick);
        double y1 = SurfaceMode == TimelineSurfaceMode.EventLanes
            ? NormalizedToValueY(item.Value, rulerHeight)
            : rulerHeight + (item.Lane - viewport.FirstLane) * LaneHeight
              + 4 + (1 - Math.Clamp(item.Value, 0, 1)) * Math.Max(1, LaneHeight - 8);
        double y2 = SurfaceMode == TimelineSurfaceMode.EventLanes
            ? NormalizedToValueY(item.SecondaryValue, rulerHeight)
            : rulerHeight + (item.Lane - viewport.FirstLane) * LaneHeight
              + 4 + (1 - Math.Clamp(item.SecondaryValue, 0, 1)) * Math.Max(1, LaneHeight - 8);
        if (item.Interpolation == CurveInterpolation.Step)
        {
            context.DrawLine(_infoPen, new Point(left, y1), new Point(right, y1));
            context.DrawLine(_infoPen, new Point(right, y1), new Point(right, y2));
        }
        else
        {
            context.DrawLine(_infoPen, new Point(left, y1), new Point(right, y2));
        }
    }

    private void DrawCurvePoint(
        DrawingContext context,
        TimelineViewport viewport,
        TimelineRenderItem item,
        Brush info,
        double laneHeaderWidth,
        double rulerHeight)
    {
        double x = laneHeaderWidth + viewport.TickToX(item.StartTick);
        double y = SurfaceMode == TimelineSurfaceMode.EventLanes
            ? NormalizedToValueY(item.Value, rulerHeight)
            : rulerHeight + (item.Lane - viewport.FirstLane) * LaneHeight
              + 4 + (1 - Math.Clamp(item.Value, 0, 1)) * Math.Max(1, LaneHeight - 8);
        context.DrawEllipse(info, _borderPen, new Point(x, y), 4, 4);
        if (IsSelected(item))
        {
            context.DrawEllipse(null, IsPrimary(item) ? _textPen : _infoPen, new Point(x, y), 6, 6);
        }
    }

    private void DrawCursor(
        DrawingContext context,
        TimelineViewport viewport,
        long? tick,
        Pen pen,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (tick is not long value || value < viewport.StartTick || value >= viewport.EndTick)
        {
            return;
        }
        double x = laneHeaderWidth + Math.Round(viewport.TickToX(value)) + 0.5;
        context.DrawLine(pen, new Point(x, rulerHeight), new Point(x, ActualHeight));
    }

    private void DrawDragPreview(
        DrawingContext context,
        TimelineViewport viewport,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (!_dragActivated || _dragItem is not TimelineRenderItem item)
        {
            return;
        }

        long rawTickDelta = checked(_dragCurrentTick - _dragOriginTick);
        long snapTarget = _dragKind == TimelineItemEditKind.ResizeEnd
            ? checked(item.EndTick + rawTickDelta)
            : checked(item.StartTick + rawTickDelta);
        long tickDelta = SnapOperationDelta(rawTickDelta, snapTarget);
        int laneDelta = checked(_dragCurrentLane - _dragOriginLane);
        long start = item.StartTick;
        long end = item.EndTick;
        int lane = item.Lane;
        switch (_dragKind)
        {
            case TimelineItemEditKind.Move:
                start = Math.Max(0, checked(start + tickDelta));
                end = checked(start + item.Length);
                lane = Math.Max(0, checked(lane + laneDelta));
                break;
            case TimelineItemEditKind.ResizeStart:
                start = Math.Clamp(checked(start + tickDelta), 0, end - 1);
                break;
            case TimelineItemEditKind.ResizeEnd:
                end = Math.Max(start + 1, checked(end + tickDelta));
                break;
        }

        double left = laneHeaderWidth + viewport.TickToX(start);
        double right = laneHeaderWidth + viewport.TickToX(end);
        double top = rulerHeight + (lane - viewport.FirstLane) * LaneHeight + 2;
        Rect rectangle = new(left, top, Math.Max(1, right - left), Math.Max(3, LaneHeight - 4));
        context.DrawRoundedRectangle(null, _marqueePen, rectangle, 2, 2);
        if (_dragCopyRequested)
        {
            FormattedText copyMarker = GetFormattedText(
                "+",
                Brush("Brush.Text.Primary", Color.FromRgb(241, 243, 245)),
                12,
                FontWeights.Bold);
            context.DrawText(copyMarker, new Point(rectangle.Left + 4, rectangle.Top + 1));
        }
    }

    private void DrawDirectManipulationHover(
        DrawingContext context,
        TimelineViewport viewport,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (_dragItem is not null
            || !CanEdit
            || _hoverPoint is not Point pointer
            || !TryHitTimelineItem(pointer, viewport, out TimelineRenderItem item)
            || !TimelineToolPolicy.CanBeginItemEdit(ToolMode, SurfaceMode, item.Kind))
        {
            return;
        }

        double left = laneHeaderWidth + viewport.TickToX(item.StartTick);
        double right = laneHeaderWidth + viewport.TickToX(item.EndTick);
        double top = rulerHeight + (item.Lane - viewport.FirstLane) * LaneHeight + 2;
        Rect bounds = new(left, top, Math.Max(2, right - left), Math.Max(3, LaneHeight - 4));
        context.PushOpacity(0.65);
        context.DrawRoundedRectangle(null, _marqueePen, bounds, 2, 2);
        context.Pop();
    }

    private void DrawTimelineChrome(
        DrawingContext context,
        TimelineViewport viewport,
        Brush chrome,
        Brush border,
        Brush text,
        Brush pianoWhiteKey,
        Brush pianoBlackKey,
        Brush pianoKeyLabel,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (rulerHeight > 0)
        {
            context.DrawRectangle(chrome, null, new Rect(0, 0, ActualWidth, rulerHeight));
            context.DrawLine(_borderPen, new Point(0, rulerHeight - 0.5), new Point(ActualWidth, rulerHeight - 0.5));
        }
        if (laneHeaderWidth > 0)
        {
            context.DrawRectangle(chrome, null, new Rect(0, rulerHeight, laneHeaderWidth, Math.Max(0, ActualHeight - rulerHeight)));
            context.DrawLine(_borderPen, new Point(laneHeaderWidth - 0.5, 0), new Point(laneHeaderWidth - 0.5, ActualHeight));
        }

        if (SurfaceMode == TimelineSurfaceMode.PianoRoll)
        {
            DrawPianoKeyboard(
                context,
                viewport,
                pianoWhiteKey,
                pianoBlackKey,
                pianoKeyLabel,
                laneHeaderWidth,
                rulerHeight);
        }
        else if (SurfaceMode is not (TimelineSurfaceMode.EventLanes or TimelineSurfaceMode.Velocity))
        {
            IReadOnlyList<string> labels = Snapshot?.LaneLabels ?? Array.Empty<string>();
            IReadOnlyList<string> secondaryLabels = Snapshot?.LaneSecondaryLabels ?? Array.Empty<string>();
            IReadOnlyList<TimelineLaneState> states = Snapshot?.LaneStates ?? Array.Empty<TimelineLaneState>();
            Brush secondaryText = Brush("Brush.Text.Tertiary", Color.FromRgb(103, 113, 128));
            Brush hoverBackground = Brush("Brush.Surface.2", Color.FromRgb(20, 24, 30));
            Brush pressedBackground = Brush("Brush.Surface.0", Color.FromRgb(9, 11, 14));
            context.PushClip(new RectangleGeometry(new Rect(0, rulerHeight, laneHeaderWidth, Math.Max(0, ActualHeight - rulerHeight))));
            for (int relativeLane = 0; relativeLane < viewport.LaneCount; relativeLane++)
            {
                int lane = viewport.FirstLane + relativeLane;
                if ((uint)lane >= (uint)labels.Count || string.IsNullOrWhiteSpace(labels[lane]))
                {
                    continue;
                }
                double laneTop = rulerHeight + relativeLane * LaneHeight;
                if (SurfaceMode == TimelineSurfaceMode.Arrangement
                    && (_hoverLaneHeader == lane || _pressedLaneHeader == lane))
                {
                    context.DrawRectangle(
                        _pressedLaneHeader == lane ? pressedBackground : hoverBackground,
                        null,
                        new Rect(0, laneTop, laneHeaderWidth, LaneHeight));
                }
                FormattedText formatted = GetFormattedText(labels[lane], text, 11, FontWeights.Normal);
                string secondaryLabel = (uint)lane < (uint)secondaryLabels.Count
                    ? secondaryLabels[lane]
                    : string.Empty;
                FormattedText? secondaryFormatted = secondaryLabel.Length == 0
                    ? null
                    : GetFormattedText(secondaryLabel, secondaryText, 9, FontWeights.Normal);
                double combinedHeight = formatted.Height + (secondaryFormatted?.Height ?? 0) + (secondaryFormatted is null ? 0 : 1);
                double y = laneTop + Math.Max(0, (LaneHeight - combinedHeight) / 2);
                context.PushClip(new RectangleGeometry(new Rect(6, laneTop, Math.Max(0, laneHeaderWidth - 52), LaneHeight)));
                context.DrawText(formatted, new Point(8, y));
                if (secondaryFormatted is not null)
                {
                    context.DrawText(secondaryFormatted, new Point(8, y + formatted.Height + 1));
                }
                context.Pop();
                if (SurfaceMode == TimelineSurfaceMode.Arrangement)
                {
                    TimelineLaneState state = (uint)lane < (uint)states.Count
                        ? states[lane]
                        : TimelineLaneState.None;
                    double commandY = laneTop + Math.Max(0, (LaneHeight - 16) / 2) + 1;
                    DrawArrangementLaneCommand(
                        context,
                        "M",
                        142,
                        commandY,
                        state.HasFlag(TimelineLaneState.Muted),
                        text);
                    DrawArrangementLaneCommand(
                        context,
                        "S",
                        161,
                        commandY,
                        state.HasFlag(TimelineLaneState.Solo),
                        text);
                }
            }
            if (_pressedLaneHeader is int sourceLane && _laneHeaderDragActivated)
            {
                double insertionY = rulerHeight
                    + (_laneHeaderDragTarget - viewport.FirstLane) * LaneHeight
                    + (_laneHeaderDragTarget > sourceLane ? LaneHeight : 0);
                context.DrawLine(_infoPen, new Point(0, insertionY + 0.5), new Point(laneHeaderWidth, insertionY + 0.5));
            }
            context.Pop();
        }

        if (rulerHeight <= 0)
        {
            return;
        }
        if (laneHeaderWidth > 0)
        {
            context.DrawText(GetFormattedText("TICK", text, 10, FontWeights.SemiBold), new Point(8, 5));
        }

        long major = Math.Max(1, GridStepTicks);
        while (major <= long.MaxValue / 2 && major * viewport.PixelsPerTick < 88)
        {
            major *= 2;
        }
        long first = viewport.StartTick / major * major;
        if (first < viewport.StartTick && first <= long.MaxValue - major)
        {
            first += major;
        }
        for (long tick = first; tick < viewport.EndTick;)
        {
            double x = laneHeaderWidth + Math.Round(viewport.TickToX(tick)) + 0.5;
            context.DrawLine(_textPen, new Point(x, rulerHeight - 5), new Point(x, rulerHeight));
            context.DrawText(GetFormattedText(tick.ToString(CultureInfo.InvariantCulture), text, 10, FontWeights.Normal), new Point(x + 4, 4));
            if (tick > long.MaxValue - major)
            {
                break;
            }
            tick += major;
        }
    }

    private void DrawPianoKeyboard(
        DrawingContext context,
        TimelineViewport viewport,
        Brush whiteKey,
        Brush blackKey,
        Brush labelBrush,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (laneHeaderWidth <= 0 || ActualHeight <= rulerHeight)
        {
            return;
        }

        context.PushClip(new RectangleGeometry(
            new Rect(0, rulerHeight, laneHeaderWidth, ActualHeight - rulerHeight)));
        double blackKeyWidth = Math.Max(12, Math.Round(laneHeaderWidth * 0.68));
        for (int relativeLane = 0; relativeLane < viewport.LaneCount; relativeLane++)
        {
            int lane = viewport.FirstLane + relativeLane;
            int midiNote = Math.Clamp(127 - lane, 0, 127);
            double y = rulerHeight + relativeLane * LaneHeight;
            double height = Math.Min(LaneHeight, ActualHeight - y);
            if (height <= 0)
            {
                break;
            }

            Rect whiteBounds = new(0, y, laneHeaderWidth, height);
            context.DrawRectangle(whiteKey, _borderPen, whiteBounds);
            if (PianoKeyPresentation.IsBlackKey(midiNote))
            {
                Rect blackBounds = new(
                    0,
                    y + 1,
                    blackKeyWidth,
                    Math.Max(1, height - 2));
                context.DrawRoundedRectangle(blackKey, _borderPen, blackBounds, 1, 1);
                continue;
            }

            string? label = PianoKeyPresentation.GetOctaveCLabel(midiNote);
            if (label is null)
            {
                continue;
            }
            double fontSize = Math.Clamp(LaneHeight * 0.56, 8, 11);
            FormattedText formatted = GetFormattedText(
                label,
                labelBrush,
                fontSize,
                FontWeights.SemiBold);
            double labelX = Math.Max(4, laneHeaderWidth - formatted.Width - 6);
            double labelY = y + (height - formatted.Height) / 2;
            context.DrawText(formatted, new Point(labelX, labelY));
        }
        context.Pop();
    }

    private void DrawRulerOverview(
        DrawingContext context,
        TimelineViewport viewport,
        Brush text,
        Brush warning,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (rulerHeight <= 0 || RulerSnapshot is not TimelineRenderSnapshot snapshot)
        {
            return;
        }

        snapshot.Index.QueryInto(viewport.StartTick, viewport.EndTick, 0, 1, _rulerItems);
        foreach (TimelineRenderItem item in _rulerItems)
        {
            double x = laneHeaderWidth + Math.Round(viewport.TickToX(item.StartTick)) + 0.5;
            Brush brush = item.Kind == TimelineItemKind.ProjectEndMarker ? warning : text;
            Pen pen = item.Kind == TimelineItemKind.ProjectEndMarker ? _redPen! : _infoPen!;
            context.DrawLine(pen, new Point(x, Math.Max(1, rulerHeight - 9)), new Point(x, rulerHeight));

            StreamGeometry marker = new();
            using (StreamGeometryContext geometry = marker.Open())
            {
                geometry.BeginFigure(new Point(x - 3, rulerHeight - 9), true, true);
                geometry.LineTo(new Point(x + 3, rulerHeight - 9), true, false);
                geometry.LineTo(new Point(x, rulerHeight - 5), true, false);
            }
            marker.Freeze();
            context.DrawGeometry(brush, null, marker);

            if (item.Label.Length != 0)
            {
                FormattedText label = GetFormattedText(item.Label, brush, 9, FontWeights.SemiBold);
                double labelX = Math.Min(
                    Math.Max(laneHeaderWidth + 2, x + 4),
                    Math.Max(laneHeaderWidth + 2, ActualWidth - label.Width - 2));
                context.DrawText(label, new Point(labelX, Math.Max(1, rulerHeight - label.Height - 1)));
            }
        }
    }

    private void DrawArrangementLaneCommand(
        DrawingContext context,
        string label,
        double x,
        double textY,
        bool active,
        Brush text)
    {
        Rect bounds = new(x, Math.Floor(textY - 1), 16, 16);
        context.DrawRectangle(active ? _penRedBrush : null, active ? _redPen : _borderPen, bounds);
        FormattedText formatted = GetFormattedText(label, text, 9, FontWeights.SemiBold);
        context.DrawText(
            formatted,
            new Point(
                bounds.X + (bounds.Width - formatted.Width) / 2,
                bounds.Y + (bounds.Height - formatted.Height) / 2));
    }

    private static bool TryGetArrangementLaneCommand(
        double x,
        out TimelineLaneHeaderCommand command)
    {
        if (x >= 140 && x < 160)
        {
            command = TimelineLaneHeaderCommand.ToggleMute;
            return true;
        }
        if (x >= 160 && x < 180)
        {
            command = TimelineLaneHeaderCommand.ToggleSolo;
            return true;
        }
        command = default;
        return false;
    }

    private void DrawMarquee(
        DrawingContext context,
        TimelineViewport viewport,
        Brush info)
    {
        if (_marqueeOrigin is not Point origin || _marqueeCurrent is not Point current)
        {
            return;
        }
        if (!TryGetMarqueeBounds(
                origin,
                current,
                viewport,
                out Rect rectangle,
                out _,
                out _,
                out _,
                out _))
        {
            return;
        }
        context.PushOpacity(0.22);
        context.DrawRectangle(info, null, rectangle);
        context.Pop();
        context.DrawRectangle(null, _marqueePen, rectangle);
    }

    private bool TryGetMarqueeBounds(
        Point origin,
        Point current,
        TimelineViewport viewport,
        out Rect displayBounds,
        out long startTick,
        out long endTick,
        out int firstLane,
        out int lastLaneExclusive)
    {
        double laneHeaderWidth = GetLaneHeaderWidth();
        double rulerHeight = GetRulerHeight();
        Rect rawBounds = new(origin, current);
        rawBounds.Intersect(new Rect(laneHeaderWidth, rulerHeight, viewport.Width, viewport.Height));
        if (rawBounds.IsEmpty || (rawBounds.Width < 2 && rawBounds.Height < 2))
        {
            displayBounds = Rect.Empty;
            startTick = endTick = 0;
            firstLane = lastLaneExclusive = 0;
            return false;
        }

        double contentLeft = laneHeaderWidth;
        double contentRight = laneHeaderWidth + viewport.Width;
        long rawAnchor = viewport.XToTick(
            Math.Clamp(origin.X, contentLeft, contentRight) - laneHeaderWidth);
        long rawMoving = viewport.XToTick(
            Math.Clamp(current.X, contentLeft, contentRight) - laneHeaderWidth);
        TimelineGridQuantization.SnappedRange snapped = TimelineGridQuantization.SnapRangeFromAnchor(
            Math.Max(0, rawAnchor),
            Math.Max(0, rawMoving),
            Math.Max(1, OperationStepTicks),
            OperationUsesBars,
            TimeSignatureMap);
        startTick = snapped.StartTick;
        endTick = snapped.EndTick;
        if (endTick <= startTick)
        {
            displayBounds = Rect.Empty;
            firstLane = lastLaneExclusive = 0;
            return false;
        }

        firstLane = viewport.YToLane(rawBounds.Top - rulerHeight);
        double inclusiveBottom = Math.Max(rawBounds.Top, Math.BitDecrement(rawBounds.Bottom));
        int lastLane = viewport.YToLane(inclusiveBottom - rulerHeight);
        lastLaneExclusive = checked(lastLane + 1);
        double left = laneHeaderWidth + viewport.TickToX(startTick);
        double right = laneHeaderWidth + viewport.TickToX(endTick);
        double top = rulerHeight + (firstLane - viewport.FirstLane) * LaneHeight;
        double bottom = rulerHeight + (lastLaneExclusive - viewport.FirstLane) * LaneHeight;
        displayBounds = new Rect(
            new Point(left, top),
            new Point(right, bottom));
        return true;
    }

    private void DrawNotePlacementPreview(
        DrawingContext context,
        TimelineViewport viewport,
        Brush red,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (_notePlacementStartTick is not long start) return;
        long end = Math.Max(checked(start + 1), _notePlacementCurrentTick);
        int lane = 127 - _notePlacementPitch;
        double left = laneHeaderWidth + viewport.TickToX(start);
        double right = laneHeaderWidth + viewport.TickToX(end);
        double top = rulerHeight + (lane - viewport.FirstLane) * LaneHeight + 1;
        Rect bounds = new(left, top, Math.Max(2, right - left), Math.Max(2, LaneHeight - 2));
        context.DrawRectangle(null, _marqueePen, bounds);
    }

    private void DrawSegmentPlacementPreview(
        DrawingContext context,
        TimelineViewport viewport,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if (_segmentPlacementStartTick is not long start) return;
        long end = Math.Max(checked(start + 1), _segmentPlacementCurrentTick);
        if (Snapshot is not null)
        {
            long nextStart = Snapshot.Items
                .Where(item => item.Kind == TimelineItemKind.Segment
                    && item.Lane == _segmentPlacementLane
                    && item.StartTick > start)
                .Select(item => item.StartTick)
                .DefaultIfEmpty(long.MaxValue)
                .Min();
            if (nextStart != long.MaxValue) end = Math.Min(end, nextStart);
        }
        if (end <= start) return;
        double left = laneHeaderWidth + viewport.TickToX(start);
        double right = laneHeaderWidth + viewport.TickToX(end);
        double top = rulerHeight + (_segmentPlacementLane - viewport.FirstLane) * LaneHeight + 2;
        Rect bounds = new(left, top, Math.Max(2, right - left), Math.Max(3, LaneHeight - 4));
        context.DrawRectangle(null, _marqueePen, bounds);
    }

    private void DrawCreationHoverPreview(
        DrawingContext context,
        TimelineViewport viewport,
        Brush red,
        Brush info,
        double laneHeaderWidth,
        double rulerHeight)
    {
        if ((_notePlacementStartTick is not null || _segmentPlacementStartTick is not null)
            || _dragItem is not null
            || ToolMode != TimelineToolMode.Draw
            || _hoverPoint is not Point pointer
            || pointer.X < laneHeaderWidth
            || pointer.Y < rulerHeight
            || SurfaceMode is not (TimelineSurfaceMode.Arrangement or TimelineSurfaceMode.PianoRoll))
        {
            return;
        }
        if (TryHitTimelineItem(pointer, viewport, out _)) return;
        int lane = viewport.YToLane(pointer.Y - rulerHeight);
        long start = SnapAbsolute(viewport.XToTick(pointer.X - laneHeaderWidth));
        long end = start > long.MaxValue - Math.Max(1, DefaultCreationLengthTicks)
            ? long.MaxValue
            : start + Math.Max(1, DefaultCreationLengthTicks);
        bool invalid = false;
        if (SurfaceMode == TimelineSurfaceMode.Arrangement && Snapshot is not null)
        {
            long nextStart = long.MaxValue;
            foreach (TimelineRenderItem item in Snapshot.Items)
            {
                if (item.Kind != TimelineItemKind.Segment || item.Lane != lane) continue;
                if (start >= item.StartTick && start < item.EndTick)
                {
                    invalid = true;
                    break;
                }
                if (item.StartTick > start && item.StartTick < nextStart) nextStart = item.StartTick;
            }
            if (!invalid && nextStart != long.MaxValue) end = Math.Min(end, nextStart);
            invalid |= end <= start;
        }
        double left = laneHeaderWidth + viewport.TickToX(start);
        double right = laneHeaderWidth + viewport.TickToX(end);
        double top = rulerHeight + (lane - viewport.FirstLane) * LaneHeight + 2;
        Rect bounds = new(left, top, Math.Max(2, right - left), Math.Max(3, LaneHeight - 4));
        Pen pen = invalid ? _redPen! : _marqueePen!;
        context.PushOpacity(0.9);
        context.DrawRectangle(null, pen, bounds);
        context.Pop();
    }

    private void UpdateHoverCursor(Point point, TimelineViewport viewport)
    {
        if (SurfaceMode == TimelineSurfaceMode.Velocity)
        {
            _velocitySelectionRestricted = SelectionSnapshot?.Count > 0
                || Snapshot?.Items.Any(static item =>
                    item.Kind == TimelineItemKind.Velocity
                    && item.State.HasFlag(TimelineItemState.Selected)) == true;
            if (TimelineToolPolicy.ForcesVelocityTrace(MouseButton.Left, Keyboard.Modifiers))
            {
                Cursor = Cursors.Cross;
                return;
            }
            Cursor = TryHitVelocityBar(point, viewport, out _)
                ? Cursors.SizeNS
                : Cursors.Arrow;
            return;
        }
        double header = GetLaneHeaderWidth();
        double ruler = GetRulerHeight();
        if (point.X < header || point.Y < ruler)
        {
            Cursor = Cursors.Arrow;
            return;
        }
        bool hasItem = TryHitTimelineItem(point, viewport, out TimelineRenderItem hit);
        bool nearEdge = false;
        if (hasItem)
        {
            double left = header + viewport.TickToX(hit.StartTick);
            double right = header + viewport.TickToX(hit.EndTick);
            nearEdge = Math.Abs(point.X - left) <= TimelineToolPolicy.DirectEditEdgeTolerancePixels
                || Math.Abs(point.X - right) <= TimelineToolPolicy.DirectEditEdgeTolerancePixels;
        }
        TimelinePointerIntent intent = TimelineToolPolicy.GetPointerIntent(
            ToolMode,
            SurfaceMode,
            isInContent: true,
            hasItem ? hit.Kind : null,
            nearEdge,
            Keyboard.Modifiers);
        Cursor = intent switch
        {
            TimelinePointerIntent.Crosshair => Cursors.Cross,
            TimelinePointerIntent.Erase => Cursors.No,
            TimelinePointerIntent.Split => Cursors.IBeam,
            TimelinePointerIntent.Move => Cursors.SizeAll,
            TimelinePointerIntent.ResizeHorizontal => Cursors.SizeWE,
            _ => Cursors.Arrow
        };
    }

    private void RefreshHoverIntent()
    {
        if (_dragItem is null
            && _hoverPoint is Point point
            && TryCreateViewport(out TimelineViewport viewport))
        {
            UpdateHoverCursor(point, viewport);
        }
        InvalidateVisual();
    }

    private static bool IsAltKey(KeyEventArgs e) =>
        e.Key is Key.LeftAlt or Key.RightAlt
        || e.SystemKey is Key.LeftAlt or Key.RightAlt;

    private bool TryHitTimelineItem(
        Point point,
        TimelineViewport viewport,
        out TimelineRenderItem item)
    {
        double header = GetLaneHeaderWidth();
        double ruler = GetRulerHeight();
        if (point.X < header || point.Y < ruler || Snapshot is null)
        {
            item = default!;
            return false;
        }
        PopulateTimelineHitItems(
            point,
            viewport,
            preferDirectEditEdges: CanEdit
                && ToolMode == TimelineToolMode.Draw
                && TimelineToolPolicy.IsDirectEditingSurface(SurfaceMode));
        if (_hitItems.Count == 0)
        {
            item = default!;
            return false;
        }
        item = _hitItems[0];
        return true;
    }

    private void PopulateTimelineHitItems(
        Point point,
        TimelineViewport viewport,
        bool preferDirectEditEdges)
    {
        if (Snapshot is not TimelineRenderSnapshot snapshot)
        {
            _hitItems.Clear();
            return;
        }

        double header = GetLaneHeaderWidth();
        double ruler = GetRulerHeight();
        long tick = viewport.XToContainingTick(point.X - header);
        int lane = viewport.YToLane(point.Y - ruler);
        if (preferDirectEditEdges)
        {
            long toleranceTicks = Math.Max(
                1,
                checked((long)Math.Ceiling(
                    TimelineToolPolicy.DirectEditEdgeTolerancePixels / viewport.PixelsPerTick)));
            snapshot.Index.HitTestInto(tick, toleranceTicks, lane, _hitItems);
            int edgeIndex = TimelineToolPolicy.FindPreferredDirectEditEdgeCandidate(
                _hitItems,
                viewport,
                point.X - header,
                ToolMode,
                SurfaceMode,
                SelectionSnapshot);
            if (edgeIndex >= 0)
            {
                if (edgeIndex != 0)
                {
                    (_hitItems[0], _hitItems[edgeIndex]) = (_hitItems[edgeIndex], _hitItems[0]);
                }
                return;
            }
        }

        snapshot.Index.HitTestInto(tick, 0, lane, _hitItems);
    }

    private void RaiseBackgroundInvoked(
        Point point,
        TimelineViewport viewport,
        bool isDoubleClick)
    {
        double header = GetLaneHeaderWidth();
        double ruler = GetRulerHeight();
        long tick = viewport.XToTick(point.X - header);
        int lane = viewport.YToLane(point.Y - ruler);
        double laneOffset = Math.Clamp(
            point.Y - ruler - (lane - viewport.FirstLane) * LaneHeight,
            0,
            LaneHeight);
        double normalizedValue = SurfaceMode is TimelineSurfaceMode.EventLanes or TimelineSurfaceMode.Velocity
            ? ValueYToNormalized(point.Y, ruler)
            : 1 - laneOffset / Math.Max(1, LaneHeight);
        BackgroundInvoked?.Invoke(
            this,
            new TimelinePointEventArgs(
                tick,
                lane,
                normalizedValue,
                Keyboard.Modifiers,
                isDoubleClick));
    }

    private void ZoomValueAxis(double pointerY, int wheelDelta, double rulerHeight)
    {
        double oldRange = Math.Max(1d / 256, _valueViewMaximum - _valueViewMinimum);
        double factor = wheelDelta > 0 ? 0.8 : 1.25;
        double newRange = Math.Clamp(oldRange * factor, 1d / 64, 1);
        double contentHeight = Math.Max(1, ActualHeight - rulerHeight);
        double screenRatio = Math.Clamp((pointerY - rulerHeight) / contentHeight, 0, 1);
        double anchor = _valueViewMaximum - screenRatio * oldRange;
        double newMaximum = anchor + screenRatio * newRange;
        double newMinimum = newMaximum - newRange;
        if (newMinimum < 0)
        {
            newMaximum -= newMinimum;
            newMinimum = 0;
        }
        if (newMaximum > 1)
        {
            newMinimum -= newMaximum - 1;
            newMaximum = 1;
        }
        _valueViewMinimum = Math.Clamp(newMinimum, 0, 1 - newRange);
        _valueViewMaximum = Math.Clamp(newMaximum, _valueViewMinimum + newRange, 1);
        UpdateValueScrollMetrics();
        SetCurrentValue(ValueScrollOffsetProperty, Math.Clamp(1 - _valueViewMaximum, 0, ValueScrollMaximum));
        InvalidateVisual();
    }

    private double NormalizedToValueY(double normalized, double rulerHeight)
    {
        double range = Math.Max(1d / 256, _valueViewMaximum - _valueViewMinimum);
        double contentHeight = Math.Max(1, ActualHeight - rulerHeight);
        return rulerHeight
            + (_valueViewMaximum - Math.Clamp(normalized, _valueViewMinimum, _valueViewMaximum))
            / range
            * contentHeight;
    }

    private double ValueYToNormalized(double y, double rulerHeight)
    {
        double range = Math.Max(1d / 256, _valueViewMaximum - _valueViewMinimum);
        double contentHeight = Math.Max(1, ActualHeight - rulerHeight);
        return Math.Clamp(
            _valueViewMaximum - Math.Clamp((y - rulerHeight) / contentHeight, 0, 1) * range,
            0,
            1);
    }

    private long SnapAbsolute(long tick) => TimelineGridQuantization.SnapAbsolute(
        Math.Max(0, tick),
        Math.Max(1, OperationStepTicks),
        OperationUsesBars,
        TimeSignatureMap,
        0);

    private long SnapOperationDelta(long delta, long targetTick) =>
        TimelineGridQuantization.SnapDelta(
            delta,
            Math.Max(0, targetTick),
            Math.Max(1, OperationStepTicks),
            OperationUsesBars,
            TimeSignatureMap);

    private Brush Brush(string key, Color fallback)
    {
        if (TryFindResource(key) is SolidColorBrush resource)
        {
            return resource;
        }
        SolidColorBrush brush = new(fallback);
        brush.Freeze();
        return brush;
    }

    private FormattedText GetFormattedText(string value, Brush brush, double size, FontWeight weight)
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_cachedPixelsPerDip != pixelsPerDip || _textCache.Count > 512)
        {
            _cachedPixelsPerDip = pixelsPerDip;
            _textCache.Clear();
        }
        string key = $"{size:R}|{weight.ToOpenTypeWeight()}|{value}";
        if (_textCache.TryGetValue(key, out FormattedText? cached))
        {
            return cached;
        }
        FormattedText created = new(
            value,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
            size,
            brush,
            pixelsPerDip);
        _textCache.Add(key, created);
        return created;
    }

    private double GetLaneHeaderWidth() => SurfaceMode switch
    {
        TimelineSurfaceMode.Arrangement => 180,
        TimelineSurfaceMode.PianoRoll => 52,
        TimelineSurfaceMode.EventLanes => 52,
        TimelineSurfaceMode.Conductor => 130,
        TimelineSurfaceMode.Velocity => 52,
        _ => 0
    };

    private double GetRulerHeight() => SurfaceMode == TimelineSurfaceMode.General ? 0 : 24;

    private void EnsurePens(Brush border, Brush info, Brush text, Brush red, Brush segmentSelection)
    {
        if (ReferenceEquals(_penBorderBrush, border)
            && ReferenceEquals(_penInfoBrush, info)
            && ReferenceEquals(_penTextBrush, text)
            && ReferenceEquals(_penRedBrush, red)
            && ReferenceEquals(_penSegmentSelectionBrush, segmentSelection))
        {
            return;
        }

        _penBorderBrush = border;
        _penInfoBrush = info;
        _penTextBrush = text;
        _penRedBrush = red;
        _penSegmentSelectionBrush = segmentSelection;
        _borderPen = FrozenPen(border, 1);
        _infoPen = FrozenPen(info, 1);
        _textPen = FrozenPen(text, 2);
        _redPen = FrozenPen(red, 1);
        Brush selection = Brush("Brush.Red.Hover", Color.FromRgb(255, 96, 101));
        _selectionPen = FrozenPen(selection, 2);
        _segmentSelectionPen = FrozenPen(segmentSelection, 2);
        _editCursorPen = FrozenPen(info, 1, DashStyles.Dash);
        _marqueePen = FrozenPen(info, 1, DashStyles.Dash);
    }

    private static Pen FrozenPen(Brush brush, double thickness, DashStyle? dashStyle = null)
    {
        Pen pen = new(brush, thickness) { DashStyle = dashStyle ?? DashStyles.Solid };
        pen.Freeze();
        return pen;
    }

    private void ClearItemDrag()
    {
        _dragItem = null;
        _dragActivated = false;
        _dragCopyRequested = false;
        _dragModifiers = ModifierKeys.None;
        Cursor = Cursors.Arrow;
    }

    private readonly record struct PianoTileDrawEntry(
        TimelineRasterCacheKey Key,
        BitmapSource Bitmap);

    private readonly record struct VelocityTileDrawEntry(
        TimelineRasterCacheKey Key,
        BitmapSource Bitmap);

    private sealed class PianoRasterFrame(
        string projectionKey,
        TimelineRasterCacheKey[] keys)
    {
        public string ProjectionKey { get; } = projectionKey;
        public IReadOnlyList<TimelineRasterCacheKey> Keys { get; } = keys;

        public bool Matches(
            string currentProjectionKey,
            IReadOnlyList<TimelineRasterCacheKey> currentKeys)
        {
            if (!string.Equals(ProjectionKey, currentProjectionKey, StringComparison.Ordinal)
                || Keys.Count != currentKeys.Count)
            {
                return false;
            }
            for (int index = 0; index < Keys.Count; index++)
            {
                if (Keys[index] != currentKeys[index])
                {
                    return false;
                }
            }
            return true;
        }
    }

    private sealed class VelocityRasterFrame(
        string projectionKey,
        TimelineRasterCacheKey[] keys)
    {
        public string ProjectionKey { get; } = projectionKey;
        public IReadOnlyList<TimelineRasterCacheKey> Keys { get; } = keys;

        public bool Matches(
            string currentProjectionKey,
            IReadOnlyList<TimelineRasterCacheKey> currentKeys)
        {
            if (!string.Equals(ProjectionKey, currentProjectionKey, StringComparison.Ordinal)
                || Keys.Count != currentKeys.Count)
            {
                return false;
            }
            for (int index = 0; index < Keys.Count; index++)
            {
                if (Keys[index] != currentKeys[index])
                {
                    return false;
                }
            }
            return true;
        }
    }
}
