using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace Midora.Desktop.Presentation.Controls;

/// <summary>
/// Renders an explanatory lifecycle scenario without creating per-event WPF elements.
/// It is a session-only projection and never mutates Project content.
/// </summary>
public sealed class LifecyclePreviewSurface : Control
{
    protected override AutomationPeer OnCreateAutomationPeer() =>
        new RenderedSurfaceAutomationPeer(this, "LifecyclePreviewSurface");

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        _ = UIElementAutomationPeer.CreatePeerForElement(this);
    }

    public static readonly DependencyProperty TemplateLengthTicksProperty = Register<long>(
        nameof(TemplateLengthTicks), 1L);
    public static readonly DependencyProperty GateLengthTicksProperty = Register<long>(
        nameof(GateLengthTicks), 192L);
    public static readonly DependencyProperty LoopStartTickProperty = Register<long?>(
        nameof(LoopStartTick), null);
    public static readonly DependencyProperty LoopEndTickProperty = Register<long?>(
        nameof(LoopEndTick), null);
    public static readonly DependencyProperty HasHardBoundaryProperty = Register<bool>(
        nameof(HasHardBoundary), false);
    public static readonly DependencyProperty HardBoundaryTickProperty = Register<long>(
        nameof(HardBoundaryTick), 384L);
    public static readonly DependencyProperty RequiresIsolationProperty = Register<bool>(
        nameof(RequiresIsolation), false);
    public static readonly DependencyProperty ScenarioPitchProperty = Register<int>(
        nameof(ScenarioPitch), 60);
    public static readonly DependencyProperty ScenarioVelocityProperty = Register<int>(
        nameof(ScenarioVelocity), 100);
    private readonly Dictionary<TextCacheKey, FormattedText> _textCache = [];
    private readonly Dictionary<PenCacheKey, Pen> _penCache = [];
    private double _cachedPixelsPerDip = -1;

    public LifecyclePreviewSurface()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        MinHeight = 116;
    }

    public long TemplateLengthTicks
    {
        get => (long)GetValue(TemplateLengthTicksProperty);
        set => SetValue(TemplateLengthTicksProperty, value);
    }
    public long GateLengthTicks
    {
        get => (long)GetValue(GateLengthTicksProperty);
        set => SetValue(GateLengthTicksProperty, value);
    }
    public long? LoopStartTick
    {
        get => (long?)GetValue(LoopStartTickProperty);
        set => SetValue(LoopStartTickProperty, value);
    }
    public long? LoopEndTick
    {
        get => (long?)GetValue(LoopEndTickProperty);
        set => SetValue(LoopEndTickProperty, value);
    }
    public bool HasHardBoundary
    {
        get => (bool)GetValue(HasHardBoundaryProperty);
        set => SetValue(HasHardBoundaryProperty, value);
    }
    public long HardBoundaryTick
    {
        get => (long)GetValue(HardBoundaryTickProperty);
        set => SetValue(HardBoundaryTickProperty, value);
    }
    public bool RequiresIsolation
    {
        get => (bool)GetValue(RequiresIsolationProperty);
        set => SetValue(RequiresIsolationProperty, value);
    }
    public int ScenarioPitch
    {
        get => (int)GetValue(ScenarioPitchProperty);
        set => SetValue(ScenarioPitchProperty, value);
    }
    public int ScenarioVelocity
    {
        get => (int)GetValue(ScenarioVelocityProperty);
        set => SetValue(ScenarioVelocityProperty, value);
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        Brush surface = FindBrush("Brush.Surface.0", Color.FromRgb(9, 11, 14));
        Brush surface2 = FindBrush("Brush.Surface.2", Color.FromRgb(21, 25, 31));
        Brush border = FindBrush("Brush.Border", Color.FromRgb(42, 48, 58));
        Brush red = FindBrush("Brush.Red", Color.FromRgb(229, 72, 77));
        Brush info = FindBrush("Brush.Info", Color.FromRgb(98, 166, 246));
        Brush text = FindBrush("Brush.Text.Primary", Color.FromRgb(241, 243, 245));
        Brush secondary = FindBrush("Brush.Text.Secondary", Color.FromRgb(142, 153, 168));
        Pen borderPen = GetPen(border, 1);
        Pen redPen = GetPen(red, 1);
        Pen infoPen = GetPen(info, 1);

        context.DrawRectangle(surface, null, new Rect(0, 0, ActualWidth, ActualHeight));
        const double left = 76;
        const double rightPadding = 12;
        double width = Math.Max(1, ActualWidth - left - rightPadding);
        long visibleEnd = Math.Max(
            Math.Max(1, TemplateLengthTicks),
            Math.Max(GateLengthTicks, HasHardBoundary ? HardBoundaryTick : 0));
        long padding = Math.Max(1, visibleEnd / 8);
        visibleEnd = visibleEnd > long.MaxValue - padding ? long.MaxValue : visibleEnd + padding;
        double X(long tick) => left + Math.Clamp(tick, 0, visibleEnd) / (double)visibleEnd * width;

        DrawLabel(context, "TEMPLATE", 8, 26, secondary, 10);
        DrawLabel(context, "SCENARIO", 8, 67, secondary, 10);
        context.DrawRectangle(surface2, borderPen, new Rect(left, 21, Math.Max(1, X(TemplateLengthTicks) - left), 20));
        context.DrawRectangle(red, null, new Rect(left, 62, Math.Max(1, X(GateLengthTicks) - left), 20));

        if (LoopStartTick is long loopStart && LoopEndTick is long loopEnd && loopEnd > loopStart)
        {
            Rect loop = new(X(loopStart), 23, Math.Max(1, X(loopEnd) - X(loopStart)), 16);
            context.PushOpacity(RequiresIsolation ? 0.65 : 0.22);
            context.DrawRectangle(info, infoPen, loop);
            context.Pop();
            DrawLabel(context, "LOOP", loop.X + 4, 24, text, 9);
        }

        context.DrawLine(redPen, new Point(X(GateLengthTicks), 56), new Point(X(GateLengthTicks), 88));
        DrawLabel(context, "Gate end", Math.Min(ActualWidth - 58, X(GateLengthTicks) + 4), 86, secondary, 9);
        if (HasHardBoundary)
        {
            context.DrawLine(infoPen, new Point(X(HardBoundaryTick), 14), new Point(X(HardBoundaryTick), 91));
            DrawLabel(context, "Hard boundary", Math.Min(ActualWidth - 78, X(HardBoundaryTick) + 4), 4, info, 9);
        }
        DrawLabel(
            context,
            $"Pitch {Math.Clamp(ScenarioPitch, 0, 127)} · Velocity {Math.Clamp(ScenarioVelocity, 1, 127)} · "
            + (RequiresIsolation ? "Per-note instance isolation enabled" : "Shared-instance compatible data only"),
            left,
            Math.Max(92, ActualHeight - 20),
            secondary,
            10);
    }

    private void DrawLabel(
        DrawingContext context,
        string value,
        double x,
        double y,
        Brush brush,
        double size)
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_cachedPixelsPerDip != pixelsPerDip || _textCache.Count > 64)
        {
            _cachedPixelsPerDip = pixelsPerDip;
            _textCache.Clear();
        }
        TextCacheKey key = new(value, size, brush);
        if (!_textCache.TryGetValue(key, out FormattedText? formatted))
        {
            formatted = new(
                value,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                size,
                brush,
                pixelsPerDip);
            _textCache[key] = formatted;
        }
        context.DrawText(formatted, new Point(x, y));
    }

    private Brush FindBrush(string key, Color fallback)
    {
        if (TryFindResource(key) is Brush brush) return brush;
        SolidColorBrush created = new(fallback);
        created.Freeze();
        return created;
    }

    private Pen GetPen(Brush brush, double thickness)
    {
        PenCacheKey key = new(brush, thickness);
        if (_penCache.TryGetValue(key, out Pen? cached)) return cached;
        Pen pen = new(brush, thickness);
        if (pen.CanFreeze) pen.Freeze();
        _penCache[key] = pen;
        return pen;
    }

    private static DependencyProperty Register<T>(string name, T defaultValue) =>
        DependencyProperty.Register(
            name,
            typeof(T),
            typeof(LifecyclePreviewSurface),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly record struct TextCacheKey(string Value, double Size, Brush Brush);
    private readonly record struct PenCacheKey(Brush Brush, double Thickness);
}
