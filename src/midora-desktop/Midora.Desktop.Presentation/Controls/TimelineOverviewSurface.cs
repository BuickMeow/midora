using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Midora.Desktop.Presentation.Rendering;

namespace Midora.Desktop.Presentation.Controls;

/// <summary>
/// A rendered, allocation-bounded timeline overview and horizontal navigator.
/// It deliberately exposes one WPF control rather than one element per musical object.
/// </summary>
public sealed class TimelineOverviewSurface : Control
{
    public static readonly DependencyProperty SnapshotProperty = DependencyProperty.Register(
        nameof(Snapshot), typeof(TimelineRenderSnapshot), typeof(TimelineOverviewSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StartTickProperty = DependencyProperty.Register(
        nameof(StartTick), typeof(long), typeof(TimelineOverviewSurface),
        new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TickSpanProperty = DependencyProperty.Register(
        nameof(TickSpan), typeof(long), typeof(TimelineOverviewSurface),
        new FrameworkPropertyMetadata(3072L, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ExtentEndTickProperty = DependencyProperty.Register(
        nameof(ExtentEndTick), typeof(long), typeof(TimelineOverviewSurface),
        new FrameworkPropertyMetadata(3072L, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PlaybackCursorTickProperty = DependencyProperty.Register(
        nameof(PlaybackCursorTick), typeof(long?), typeof(TimelineOverviewSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty EditCursorTickProperty = DependencyProperty.Register(
        nameof(EditCursorTick), typeof(long?), typeof(TimelineOverviewSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly int[] _density = new int[2048];
    private TimelineRenderSnapshot? _cachedSnapshot;
    private long _cachedExtent;
    private int _cachedWidth;
    private int _maximumDensity;
    private bool _draggingViewport;
    private double _dragOffset;
    private Brush? _cachedBorderBrush;
    private Brush? _cachedInfoBrush;
    private Brush? _cachedRedBrush;
    private Pen? _borderPen;
    private Pen? _infoPen;
    private Pen? _playbackCursorPen;
    private Pen? _editCursorPen;

    public TimelineOverviewSurface()
    {
        Height = 22;
        MinHeight = 22;
        Focusable = true;
        Cursor = Cursors.Arrow;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new RenderedSurfaceAutomationPeer(this, "TimelineOverviewSurface");

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

    public long StartTick
    {
        get => (long)GetValue(StartTickProperty);
        set => SetValue(StartTickProperty, Math.Max(0, value));
    }

    public long TickSpan
    {
        get => (long)GetValue(TickSpanProperty);
        set => SetValue(TickSpanProperty, Math.Max(16, value));
    }

    public long ExtentEndTick
    {
        get => (long)GetValue(ExtentEndTickProperty);
        set => SetValue(ExtentEndTickProperty, Math.Max(1, value));
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

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        Brush surface = ResourceBrush("Brush.Surface.1", Color.FromRgb(14, 17, 21));
        Brush border = ResourceBrush("Brush.Border", Color.FromRgb(42, 48, 58));
        Brush info = ResourceBrush("Brush.Info", Color.FromRgb(98, 166, 246));
        Brush red = ResourceBrush("Brush.Red", Color.FromRgb(229, 72, 77));
        EnsurePens(border, info, red);
        drawingContext.DrawRectangle(surface, _borderPen, new Rect(0, 0, ActualWidth, ActualHeight));
        if (ActualWidth <= 2 || ActualHeight <= 2) return;

        long extent = EffectiveExtent();
        BuildDensityIfNeeded(extent);
        if (_maximumDensity > 0)
        {
            double plotHeight = Math.Max(1, ActualHeight - 6);
            for (int x = 0; x < _cachedWidth; x++)
            {
                int count = _density[x];
                if (count == 0) continue;
                double height = Math.Max(1, plotHeight * Math.Log2(count + 1) / Math.Log2(_maximumDensity + 1));
                drawingContext.DrawRectangle(border, null, new Rect(x, ActualHeight - 3 - height, 1, height));
            }
        }

        Rect thumb = ViewportThumb(extent);
        drawingContext.PushOpacity(IsMouseOver ? 0.28 : 0.20);
        drawingContext.DrawRoundedRectangle(info, null, thumb, 2, 2);
        drawingContext.Pop();
        drawingContext.DrawRoundedRectangle(null, _infoPen, thumb, 2, 2);
        DrawCursor(drawingContext, PlaybackCursorTick, extent, _playbackCursorPen);
        DrawCursor(drawingContext, EditCursorTick, extent, _editCursorPen);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        long extent = EffectiveExtent();
        Point point = e.GetPosition(this);
        Rect thumb = ViewportThumb(extent);
        if (thumb.Contains(point))
        {
            _dragOffset = point.X - thumb.Left;
        }
        else
        {
            _dragOffset = thumb.Width / 2;
            MoveViewport(point.X, extent);
        }
        _draggingViewport = true;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_draggingViewport || e.LeftButton != MouseButtonState.Pressed) return;
        MoveViewport(e.GetPosition(this).X, EffectiveExtent());
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_draggingViewport) return;
        _draggingViewport = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _draggingViewport = false;
        base.OnLostMouseCapture(e);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        long delta = Math.Max(1, TickSpan / 8);
        StartTick = e.Delta > 0
            ? Math.Max(0, StartTick - delta)
            : SafeAdd(StartTick, delta);
        e.Handled = true;
    }

    private void MoveViewport(double pointerX, long extent)
    {
        double width = Math.Max(1, ActualWidth);
        Rect thumb = ViewportThumb(extent);
        double desiredLeft = Math.Clamp(pointerX - _dragOffset, 0, Math.Max(0, width - thumb.Width));
        long maximumStart = Math.Max(0, extent - Math.Min(TickSpan, extent));
        StartTick = maximumStart == 0
            ? 0
            : (long)Math.Round(desiredLeft / Math.Max(1, width - thumb.Width) * maximumStart,
                MidpointRounding.AwayFromZero);
    }

    private Rect ViewportThumb(long extent)
    {
        double width = Math.Max(1, ActualWidth);
        long span = Math.Min(Math.Max(1, TickSpan), extent);
        double thumbWidth = Math.Clamp(width * span / extent, 18, width);
        long maximumStart = Math.Max(0, extent - span);
        double left = maximumStart == 0
            ? 0
            : Math.Clamp(StartTick, 0, maximumStart) / (double)maximumStart * Math.Max(0, width - thumbWidth);
        return new Rect(left, 2, thumbWidth, Math.Max(1, ActualHeight - 4));
    }

    private long EffectiveExtent()
    {
        long viewportEnd = SafeAdd(Math.Max(0, StartTick), Math.Max(1, TickSpan));
        return Math.Max(1, Math.Max(ExtentEndTick, viewportEnd));
    }

    private void BuildDensityIfNeeded(long extent)
    {
        int width = Math.Clamp((int)Math.Ceiling(ActualWidth), 0, _density.Length);
        if (ReferenceEquals(Snapshot, _cachedSnapshot) && extent == _cachedExtent && width == _cachedWidth) return;
        Array.Clear(_density);
        _maximumDensity = 0;
        _cachedSnapshot = Snapshot;
        _cachedExtent = extent;
        _cachedWidth = width;
        if (Snapshot is null || width == 0) return;
        Snapshot.AccumulateOverviewDensity(extent, _density.AsSpan(0, width));
        for (int x = 0; x < width; x++)
        {
            int count = _density[x];
            if (count > _maximumDensity) _maximumDensity = count;
        }
    }

    private static long SafeAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private void DrawCursor(DrawingContext context, long? tick, long extent, Pen? pen)
    {
        if (tick is not long value || value < 0 || value > extent || pen is null) return;
        double contentWidth = Math.Max(0, ActualWidth - 2);
        if (contentWidth <= 0) return;
        double rawX = 1 + value / (double)extent * contentWidth;
        double x = Math.Clamp(Math.Floor(rawX) + 0.5, 1.5, Math.Max(1.5, ActualWidth - 1.5));
        context.DrawLine(pen, new Point(x, 1), new Point(x, Math.Max(1, ActualHeight - 1)));
    }

    private void EnsurePens(Brush border, Brush info, Brush red)
    {
        if (ReferenceEquals(border, _cachedBorderBrush)
            && ReferenceEquals(info, _cachedInfoBrush)
            && ReferenceEquals(red, _cachedRedBrush)) return;
        _cachedBorderBrush = border;
        _cachedInfoBrush = info;
        _cachedRedBrush = red;
        _borderPen = new Pen(border, 1);
        _infoPen = new Pen(info, 1);
        _playbackCursorPen = new Pen(red, 1);
        _editCursorPen = new Pen(info, 1)
        {
            DashStyle = DashStyles.Dash
        };
        if (_borderPen.CanFreeze) _borderPen.Freeze();
        if (_infoPen.CanFreeze) _infoPen.Freeze();
        if (_playbackCursorPen.CanFreeze) _playbackCursorPen.Freeze();
        if (_editCursorPen.CanFreeze) _editCursorPen.Freeze();
    }

    private static Brush ResourceBrush(string key, Color fallback)
    {
        Brush value = Application.Current?.TryFindResource(key) as Brush
            ?? new SolidColorBrush(fallback);
        if (value.CanFreeze && !value.IsFrozen)
        {
            value = value.Clone();
            value.Freeze();
        }
        return value;
    }
}
