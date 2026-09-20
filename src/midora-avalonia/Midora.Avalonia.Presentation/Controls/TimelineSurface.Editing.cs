using Avalonia;
using Avalonia.Input;
using Midora.Avalonia.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Avalonia.Presentation.Controls;

public sealed partial class TimelineSurface
{
    private const double EditEdgeToleranceDevicePixels = 4;
    private const double EditBarToleranceDevicePixels = 5;
    private const long EditMaximumTick = long.MaxValue - 1;

    private enum EditGestureKind
    {
        None,
        DrawNote,
        Move,
        ResizeStart,
        ResizeEnd,
        Erase,
        Split,
        Velocity,
        EventValue
    }

    private readonly List<TimelineRenderItem> _gestureItems = [];
    private readonly HashSet<MidoraId> _gestureItemIds = [];
    private readonly HashSet<MidoraId> _erasedItemIds = [];
    private readonly List<TimelineRenderItem> _singleItemScratch = [];

    private ITimelineEditHost? _gestureHost;
    private IPointer? _gesturePointer;
    private EditGestureKind _gesture;
    private TimelineToolMode _gestureToolMode;
    private TimelineSurfaceMode _gestureSurfaceMode;
    private bool _editTransactionOpen;
    private bool _gestureChanged;
    private bool _captureLostHandlerAttached;
    private long _gestureOriginTick;
    private int _gestureOriginLane;
    private TimelineRenderItem _gestureItem;
    private long _gestureAppliedTickDelta;
    private int _gestureAppliedLaneDelta;
    private long _gestureResizeStartTick;
    private long _gestureResizeEndTick;
    private long _gestureDrawBaseEndTick;
    private double _gestureAppliedValue;
    private bool _previewActive;
    private int _previewLane;
    private long _previewStartTick;
    private long _previewEndTick;

    public static readonly StyledProperty<ITimelineEditHost?> EditHostProperty =
        AvaloniaProperty.Register<TimelineSurface, ITimelineEditHost?>(nameof(EditHost));

    public static readonly StyledProperty<TimelineToolMode> ToolModeProperty =
        AvaloniaProperty.Register<TimelineSurface, TimelineToolMode>(
            nameof(ToolMode),
            defaultValue: TimelineToolMode.Select);

    public ITimelineEditHost? EditHost
    {
        get => GetValue(EditHostProperty);
        set => SetValue(EditHostProperty, value);
    }

    public TimelineToolMode ToolMode
    {
        get => GetValue(ToolModeProperty);
        set => SetValue(ToolModeProperty, value);
    }

    public event EventHandler? EditCommitted;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!_captureLostHandlerAttached)
        {
            _captureLostHandlerAttached = true;
            PointerCaptureLost += OnEditingCaptureLost;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_captureLostHandlerAttached)
        {
            _captureLostHandlerAttached = false;
            PointerCaptureLost -= OnEditingCaptureLost;
        }

        CancelEditingGesture();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _gesture != EditGestureKind.None)
        {
            CancelEditingGesture();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void OnEditingCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_gesture != EditGestureKind.None)
        {
            CancelEditingGesture();
        }
    }

    private bool OnEditingPointerPressed(PointerPressedEventArgs e, PointerPoint point)
    {
        if (_gesture != EditGestureKind.None)
        {
            return point.Properties.IsLeftButtonPressed;
        }

        if (!point.Properties.IsLeftButtonPressed
            || Source is null
            || EditHost is not { CanEdit: true } host
            || !TryCreateViewport(out _))
        {
            return false;
        }

        return SurfaceMode switch
        {
            TimelineSurfaceMode.PianoRoll => TryBeginPianoRollPressed(e, point, host),
            TimelineSurfaceMode.Velocity => TryBeginVelocityPressed(e, point, host),
            TimelineSurfaceMode.EventLanes => TryBeginEventLanePressed(e, point, host),
            _ => false
        };
    }

    private bool OnEditingPointerMoved(PointerEventArgs e, Point position)
    {
        if (_gesture == EditGestureKind.None)
        {
            return false;
        }

        if (!IsEditingGestureCurrent(out ITimelineEditHost host)
            || Source is null
            || !TryCreateViewport(out TimelineViewport viewport))
        {
            CancelEditingGesture();
            return true;
        }

        switch (_gesture)
        {
            case EditGestureKind.DrawNote:
                ApplyDraw(position, viewport, host);
                break;
            case EditGestureKind.Move:
                ApplyMove(position, viewport, host);
                break;
            case EditGestureKind.ResizeStart:
            case EditGestureKind.ResizeEnd:
                ApplyResize(position, viewport, host);
                break;
            case EditGestureKind.Erase:
                EraseAt(position, host);
                break;
            case EditGestureKind.Velocity:
                ApplyVelocity(position, viewport, host);
                break;
            case EditGestureKind.EventValue:
                ApplyEventValue(position, viewport, host);
                break;
        }

        RaisePointerTickChanged(position);
        return true;
    }

    private bool OnEditingPointerReleased(PointerReleasedEventArgs e, Point position)
    {
        if (_gesture == EditGestureKind.None)
        {
            return false;
        }

        if (e.InitialPressMouseButton != MouseButton.Left)
        {
            return false;
        }

        if (!IsEditingGestureCurrent(out ITimelineEditHost host)
            || Source is null
            || !TryCreateViewport(out TimelineViewport viewport))
        {
            CancelEditingGesture();
            return true;
        }

        switch (_gesture)
        {
            case EditGestureKind.DrawNote:
                ApplyDraw(position, viewport, host);
                break;
            case EditGestureKind.Move:
                ApplyMove(position, viewport, host);
                break;
            case EditGestureKind.ResizeStart:
            case EditGestureKind.ResizeEnd:
                ApplyResize(position, viewport, host);
                break;
            case EditGestureKind.Erase:
                EraseAt(position, host);
                break;
            case EditGestureKind.Velocity:
                ApplyVelocity(position, viewport, host);
                break;
            case EditGestureKind.EventValue:
                ApplyEventValue(position, viewport, host);
                break;
        }

        FinishEditingGesture(raiseCommit: true);
        return true;
    }

    private bool TryBeginPianoRollPressed(
        PointerPressedEventArgs e,
        PointerPoint point,
        ITimelineEditHost host)
    {
        return ToolMode switch
        {
            TimelineToolMode.Erase => TryBeginErase(e, point.Position, host),
            TimelineToolMode.Split => TryBeginSplit(e, point.Position, host),
            TimelineToolMode.Draw => TryBeginNoteEdit(e, point, host, allowDraw: true),
            _ => TryBeginNoteEdit(e, point, host, allowDraw: false)
        };
    }

    private bool TryBeginNoteEdit(
        PointerPressedEventArgs e,
        PointerPoint point,
        ITimelineEditHost host,
        bool allowDraw)
    {
        Point position = point.Position;
        if (TryHitNoteAt(position, out TimelineViewport viewport, out int lane, out TimelineRenderItem item))
        {
            BeginEditingGesture(host, e);
            _gestureOriginLane = lane;
            _gestureOriginTick = viewport.XToContainingTick(position.X);
            _gestureItem = item;
            bool alreadySelected = IsItemSelected(item);
            if (!alreadySelected)
            {
                SelectItem(item);
            }

            double edgeTolerance = EditEdgeToleranceDevicePixels * GetDevicePixelWidth();
            double startDistance = Math.Abs(position.X - viewport.TickToX(item.StartTick));
            double endDistance = Math.Abs(position.X - viewport.TickToX(item.EndTick));
            if (Math.Min(startDistance, endDistance) <= edgeTolerance)
            {
                _gesture = startDistance <= endDistance
                    ? EditGestureKind.ResizeStart
                    : EditGestureKind.ResizeEnd;
                _gestureResizeStartTick = item.StartTick;
                _gestureResizeEndTick = item.EndTick;
                SetSingleGestureItem(item);
            }
            else
            {
                _gesture = EditGestureKind.Move;
                if (alreadySelected)
                {
                    CollectSelectedNoteItems(item);
                }
                else
                {
                    SetSingleGestureItem(item);
                }
            }

            return true;
        }

        if (!allowDraw || e.ClickCount > 1)
        {
            return false;
        }

        if (!TryResolveEditingPointer(position, out viewport, out lane, out long rawTick))
        {
            return false;
        }

        long start = SnapEditingTick(rawTick);
        long length = Math.Max(1, host.DefaultNoteLengthTicks);
        BeginEditingGesture(host, e);
        _gesture = EditGestureKind.DrawNote;
        _gestureOriginLane = lane;
        _gestureOriginTick = rawTick;
        TimelineRenderItem created = host.CreateNote(lane, start, length);
        _gestureItem = created;
        _previewActive = true;
        _previewLane = lane;
        _previewStartTick = created.StartTick;
        _previewEndTick = created.EndTick;
        _gestureDrawBaseEndTick = created.EndTick;
        _gestureChanged = true;
        UpdateDrawPreview(viewport);
        InvalidateVisual();
        return true;
    }

    private bool TryBeginErase(PointerPressedEventArgs e, Point position, ITimelineEditHost host)
    {
        if (!TryResolveEditingPointer(position, out _, out int lane, out long tick))
        {
            return false;
        }

        BeginEditingGesture(host, e);
        _gesture = EditGestureKind.Erase;
        _gestureOriginLane = lane;
        _gestureOriginTick = tick;
        EraseAt(position, host);
        return true;
    }

    private bool TryBeginSplit(PointerPressedEventArgs e, Point position, ITimelineEditHost host)
    {
        if (!TryHitNoteAt(position, out TimelineViewport viewport, out int lane, out TimelineRenderItem item))
        {
            return false;
        }

        BeginEditingGesture(host, e);
        _gesture = EditGestureKind.Split;
        _gestureOriginLane = lane;
        _gestureOriginTick = viewport.XToContainingTick(position.X);
        _gestureItem = item;
        SelectItem(item);
        if (host.SplitItem(item, viewport.XToTick(position.X)))
        {
            _gestureChanged = true;
        }

        return true;
    }

    private bool TryBeginVelocityPressed(
        PointerPressedEventArgs e,
        PointerPoint point,
        ITimelineEditHost host)
    {
        if (!TryResolveEditingPointer(point.Position, out TimelineViewport viewport, out int lane, out long tick)
            || !TryHitVelocityBar(point.Position, viewport, lane, out TimelineRenderItem item))
        {
            return false;
        }

        BeginEditingGesture(host, e);
        _gesture = EditGestureKind.Velocity;
        _gestureOriginLane = lane;
        _gestureOriginTick = tick;
        _gestureItem = item;
        if (IsItemSelected(item))
        {
            CollectSelectedNoteItems(item);
        }
        else
        {
            SelectItem(item);
            SetSingleGestureItem(item);
        }

        _gestureAppliedValue = ResolveVelocityValue(viewport, lane, point.Position.Y);
        host.SetVelocity(_gestureItems, _gestureAppliedValue);
        _gestureChanged = true;
        InvalidateVisual();
        return true;
    }

    private bool TryBeginEventLanePressed(
        PointerPressedEventArgs e,
        PointerPoint point,
        ITimelineEditHost host)
    {
        if (!TryResolveEditingPointer(point.Position, out TimelineViewport viewport, out int lane, out long tick)
            || !TryHitEventPoint(point.Position, viewport, lane, out TimelineRenderItem item))
        {
            return false;
        }

        BeginEditingGesture(host, e);
        _gesture = EditGestureKind.EventValue;
        _gestureOriginLane = lane;
        _gestureOriginTick = tick;
        _gestureItem = item;
        SelectItem(item);
        _gestureAppliedValue = ResolveEventValue(viewport, lane, point.Position.Y);
        host.SetEventValue(item, _gestureAppliedValue);
        _gestureChanged = true;
        InvalidateVisual();
        return true;
    }

    private void ApplyDraw(Point position, TimelineViewport viewport, ITimelineEditHost host)
    {
        long pointerTick = viewport.XToTick(position.X);
        long rawDelta = pointerTick - _gestureOriginTick;
        long extra = ResolveForwardStepDelta(rawDelta, ResolveEditingSnapStepTicks());
        long end = SaturatedAdd(_gestureDrawBaseEndTick, extra);
        if (end == _previewEndTick)
        {
            return;
        }

        _previewEndTick = end;
        host.ResizeItem(_gestureItem, _previewStartTick, end);
        _gestureChanged = true;
        UpdateDrawPreview(viewport);
        InvalidateVisual();
    }

    private void ApplyMove(Point position, TimelineViewport viewport, ITimelineEditHost host)
    {
        if (_gestureItems.Count == 0)
        {
            return;
        }

        long pointerTick = viewport.XToTick(position.X);
        int lane = ResolveEditingLane(viewport, position.Y);
        long rawTickDelta = pointerTick - _gestureOriginTick;
        long tickDelta = rawTickDelta == 0
            ? 0
            : ProjectTimelineGrid.SnapDelta(
                rawTickDelta,
                Math.Max(0, _gestureOriginTick),
                ResolveEditingSnapStepTicks(),
                useBars: false,
                GetTimeSignatureMap());
        int laneDelta = lane - _gestureOriginLane;
        if (tickDelta == _gestureAppliedTickDelta && laneDelta == _gestureAppliedLaneDelta)
        {
            return;
        }

        host.MoveItems(
            _gestureItems,
            tickDelta - _gestureAppliedTickDelta,
            laneDelta - _gestureAppliedLaneDelta);
        _gestureAppliedTickDelta = tickDelta;
        _gestureAppliedLaneDelta = laneDelta;
        _gestureChanged = true;
        InvalidateVisual();
    }

    private void ApplyResize(Point position, TimelineViewport viewport, ITimelineEditHost host)
    {
        long pointerTick = viewport.XToTick(position.X);
        long snapped = SnapEditingTick(pointerTick);
        if (_gesture == EditGestureKind.ResizeStart)
        {
            long newStart = Math.Min(snapped, _gestureResizeEndTick - 1);
            if (newStart == _gestureResizeStartTick)
            {
                return;
            }

            host.ResizeItem(_gestureItem, newStart, _gestureResizeEndTick);
            _gestureResizeStartTick = newStart;
        }
        else
        {
            long newEnd = Math.Max(snapped, SaturatedAdd(_gestureResizeStartTick, 1));
            if (newEnd == _gestureResizeEndTick)
            {
                return;
            }

            host.ResizeItem(_gestureItem, _gestureResizeStartTick, newEnd);
            _gestureResizeEndTick = newEnd;
        }

        _gestureChanged = true;
        InvalidateVisual();
    }

    private void EraseAt(Point position, ITimelineEditHost host)
    {
        if (!TryHitNoteAt(position, out _, out _, out TimelineRenderItem item))
        {
            return;
        }

        if (!_erasedItemIds.Add(item.Id))
        {
            return;
        }

        _singleItemScratch.Clear();
        _singleItemScratch.Add(item);
        host.EraseItems(_singleItemScratch);
        _gestureChanged = true;
        InvalidateVisual();
    }

    private void ApplyVelocity(Point position, TimelineViewport viewport, ITimelineEditHost host)
    {
        if (_gestureItems.Count == 0)
        {
            return;
        }

        double value = ResolveVelocityValue(viewport, _gestureOriginLane, position.Y);
        if (value == _gestureAppliedValue)
        {
            return;
        }

        _gestureAppliedValue = value;
        host.SetVelocity(_gestureItems, value);
        _gestureChanged = true;
        InvalidateVisual();
    }

    private void ApplyEventValue(Point position, TimelineViewport viewport, ITimelineEditHost host)
    {
        double value = ResolveEventValue(viewport, _gestureOriginLane, position.Y);
        if (value == _gestureAppliedValue)
        {
            return;
        }

        _gestureAppliedValue = value;
        host.SetEventValue(_gestureItem, value);
        _gestureChanged = true;
        InvalidateVisual();
    }

    private void UpdateDrawPreview(TimelineViewport viewport)
    {
        if (!_previewActive)
        {
            return;
        }

        double left = viewport.TickToX(_previewStartTick);
        double right = viewport.TickToX(_previewEndTick);
        double laneTop = RulerHeight + (_previewLane - viewport.FirstLane) * viewport.LaneHeight;
        _marqueeRect = new Rect(
            Math.Min(left, right),
            laneTop,
            Math.Max(1, Math.Abs(right - left)),
            viewport.LaneHeight);
        _isMarqueeVisible = true;
    }

    private void ClearDrawPreview()
    {
        if (!_previewActive)
        {
            return;
        }

        _previewActive = false;
        _isMarqueeVisible = false;
        _marqueeRect = default;
    }

    private void BeginEditingGesture(ITimelineEditHost host, PointerPressedEventArgs e)
    {
        _gestureHost = host;
        _gesturePointer = e.Pointer;
        _gestureToolMode = ToolMode;
        _gestureSurfaceMode = SurfaceMode;
        _gestureChanged = false;
        _gestureAppliedTickDelta = 0;
        _gestureAppliedLaneDelta = 0;
        _gestureAppliedValue = 0;
        _gestureItem = default;
        _gestureItems.Clear();
        _gestureItemIds.Clear();
        _erasedItemIds.Clear();
        _editTransactionOpen = true;
        e.Pointer.Capture(this);
        host.BeginEditTransaction();
    }

    private void FinishEditingGesture(bool raiseCommit)
    {
        ITimelineEditHost? host = _gestureHost;
        IPointer? pointer = _gesturePointer;
        bool changed = _gestureChanged;
        _gesture = EditGestureKind.None;
        _gestureHost = null;
        _gesturePointer = null;
        _gestureItem = default;
        _gestureItems.Clear();
        _gestureItemIds.Clear();
        _erasedItemIds.Clear();
        _gestureChanged = false;
        _gestureAppliedTickDelta = 0;
        _gestureAppliedLaneDelta = 0;
        _gestureAppliedValue = 0;
        ClearDrawPreview();
        if (_editTransactionOpen)
        {
            _editTransactionOpen = false;
            host?.EndEditTransaction();
        }

        if (pointer is not null)
        {
            pointer.Capture(null);
        }

        InvalidateVisual();
        if (raiseCommit && changed)
        {
            EditCommitted?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CancelEditingGesture()
    {
        if (_gesture == EditGestureKind.None && !_editTransactionOpen)
        {
            return;
        }

        FinishEditingGesture(raiseCommit: false);
    }

    private bool IsEditingGestureCurrent(out ITimelineEditHost host)
    {
        host = _gestureHost!;
        return _gestureHost is { CanEdit: true }
            && ReferenceEquals(_gestureHost, EditHost)
            && _gestureToolMode == ToolMode
            && _gestureSurfaceMode == SurfaceMode;
    }

    private void SetSingleGestureItem(TimelineRenderItem item)
    {
        _gestureItems.Clear();
        _gestureItemIds.Clear();
        _gestureItems.Add(item);
        _gestureItemIds.Add(item.Id);
    }

    private void CollectSelectedNoteItems(TimelineRenderItem pressed)
    {
        _gestureItems.Clear();
        _gestureItemIds.Clear();
        _gestureItems.Add(pressed);
        _gestureItemIds.Add(pressed.Id);
        if (Source is null || !TryCreateViewport(out TimelineViewport viewport))
        {
            return;
        }

        _queryScratch.Clear();
        Source.QueryInto(
            viewport.StartTick,
            viewport.EndTick,
            viewport.FirstLane,
            viewport.LastLaneExclusive,
            _queryScratch);
        foreach (TimelineRenderItem candidate in _queryScratch)
        {
            if (!IsEditingNoteItem(candidate)
                || !IsItemSelected(candidate)
                || !_gestureItemIds.Add(candidate.Id))
            {
                continue;
            }

            _gestureItems.Add(candidate);
        }
    }

    private bool TryHitNoteAt(
        Point position,
        out TimelineViewport viewport,
        out int lane,
        out TimelineRenderItem item)
    {
        item = default;
        viewport = default;
        lane = 0;
        if (Source is null || !TryResolveEditingPointer(position, out viewport, out lane, out long tick))
        {
            return false;
        }

        _queryScratch.Clear();
        long endTick = tick < long.MaxValue ? tick + 1 : long.MaxValue;
        Source.QueryInto(tick, endTick, lane, lane + 1, _queryScratch);
        return TryChooseTopmost(IsEditingNoteItem, out item);
    }

    private bool TryHitVelocityBar(
        Point position,
        TimelineViewport viewport,
        int lane,
        out TimelineRenderItem item) =>
        TryHitPointNear(
            position,
            viewport,
            lane,
            IsEditingNoteItem,
            out item);

    private bool TryHitEventPoint(
        Point position,
        TimelineViewport viewport,
        int lane,
        out TimelineRenderItem item) =>
        TryHitPointNear(
            position,
            viewport,
            lane,
            static candidate => !IsNoteKind(candidate.Kind)
                && !candidate.State.HasFlag(TimelineItemState.HitTestDisabled),
            out item);

    private bool TryHitPointNear(
        Point position,
        TimelineViewport viewport,
        int lane,
        Func<TimelineRenderItem, bool> predicate,
        out TimelineRenderItem item)
    {
        item = default;
        if (Source is null)
        {
            return false;
        }

        double tolerance = EditBarToleranceDevicePixels * GetDevicePixelWidth();
        long tick = viewport.XToContainingTick(position.X);
        GetToleranceQueryRange(viewport, tick, tolerance, out long queryStart, out long queryEnd);
        _queryScratch.Clear();
        Source.QueryInto(queryStart, queryEnd, lane, lane + 1, _queryScratch);
        double bestDistance = double.PositiveInfinity;
        bool found = false;
        foreach (TimelineRenderItem candidate in _queryScratch)
        {
            if (!predicate(candidate))
            {
                continue;
            }

            double distance = Math.Abs(position.X - viewport.TickToX(candidate.StartTick));
            if (distance > tolerance)
            {
                continue;
            }

            if (!found
                || distance < bestDistance
                || distance == bestDistance && candidate.Id.CompareTo(item.Id) < 0)
            {
                item = candidate;
                bestDistance = distance;
                found = true;
            }
        }

        return found;
    }

    private bool TryResolveEditingPointer(
        Point position,
        out TimelineViewport viewport,
        out int lane,
        out long tick)
    {
        lane = 0;
        tick = 0;
        if (!TryCreateViewport(out viewport))
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

        lane = LaneFromContentY(viewport, contentY);
        tick = viewport.XToContainingTick(position.X);
        return true;
    }

    private int ResolveEditingLane(TimelineViewport viewport, double y)
    {
        double contentY = y - RulerHeight;
        double maximum = viewport.LaneCount * viewport.LaneHeight;
        double clamped = double.IsFinite(contentY)
            ? Math.Clamp(contentY, 0, maximum - double.Epsilon)
            : 0;
        return LaneFromContentY(viewport, clamped);
    }

    private double ResolveVelocityValue(TimelineViewport viewport, int lane, double y)
    {
        double laneTop = RulerHeight + (lane - viewport.FirstLane) * viewport.LaneHeight;
        double barSpan = Math.Max(1, viewport.LaneHeight - 4);
        double fraction = 1 - (y - laneTop) / barSpan;
        if (!double.IsFinite(fraction))
        {
            fraction = 0;
        }

        fraction = Math.Clamp(fraction, 0, 1);
        double maximum = double.IsFinite(ValueMaximum) ? Math.Max(1, ValueMaximum) : 127;
        return fraction * maximum;
    }

    private double ResolveEventValue(TimelineViewport viewport, int lane, double y)
    {
        double laneTop = RulerHeight + (lane - viewport.FirstLane) * viewport.LaneHeight;
        double fraction = 1 - (y - laneTop) / Math.Max(1, viewport.LaneHeight);
        if (!double.IsFinite(fraction))
        {
            fraction = 0;
        }

        fraction = Math.Clamp(fraction, 0, 1);
        double minimum = double.IsFinite(ValueMinimum) ? ValueMinimum : 0;
        double maximum = double.IsFinite(ValueMaximum) ? ValueMaximum : 127;
        return minimum + fraction * (maximum - minimum);
    }

    private long SnapEditingTick(long tick)
    {
        if (tick <= 0)
        {
            return 0;
        }

        long step = ResolveEditingSnapStepTicks();
        return ProjectTimelineGrid.TrySnapAbsolute(
            tick,
            step,
            useBars: false,
            GetTimeSignatureMap(),
            0,
            out long snapped)
            ? snapped
            : tick;
    }

    private long ResolveEditingSnapStepTicks()
    {
        long step = TicksPerQuarterNote / 4;
        return step > 0 ? step : 1;
    }

    private static long ResolveForwardStepDelta(long pointerDeltaTicks, long stepTicks)
    {
        if (pointerDeltaTicks <= 0)
        {
            return 0;
        }

        long step = stepTicks > 0 ? stepTicks : 1;
        long units = ((pointerDeltaTicks - 1) / step) + 1;
        return units > long.MaxValue / step ? long.MaxValue : units * step;
    }

    private static long SaturatedAdd(long value, long delta)
    {
        if (delta <= 0)
        {
            return value;
        }

        return value > EditMaximumTick - delta ? EditMaximumTick : value + delta;
    }

    private static void GetToleranceQueryRange(
        TimelineViewport viewport,
        long tick,
        double tolerancePixels,
        out long start,
        out long end)
    {
        long toleranceTicks = 1;
        double pixelsPerTick = viewport.PixelsPerTick;
        if (pixelsPerTick > 0)
        {
            double candidate = tolerancePixels / pixelsPerTick;
            if (double.IsFinite(candidate) && candidate > 1)
            {
                toleranceTicks = candidate >= long.MaxValue
                    ? long.MaxValue
                    : (long)Math.Ceiling(candidate);
            }
        }

        start = tick > toleranceTicks ? tick - toleranceTicks : 0;
        long margin = toleranceTicks >= long.MaxValue ? long.MaxValue : toleranceTicks + 1;
        end = tick > long.MaxValue - margin ? long.MaxValue : tick + margin;
        if (end <= start)
        {
            end = start < long.MaxValue ? start + 1 : long.MaxValue;
        }
    }

    private bool IsItemSelected(TimelineRenderItem item) =>
        SelectedId == item.Id || item.State.HasFlag(TimelineItemState.Selected);

    private static bool IsEditingNoteItem(TimelineRenderItem item) =>
        IsNoteKind(item.Kind) && !item.State.HasFlag(TimelineItemState.HitTestDisabled);
}
