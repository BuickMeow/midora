using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Midora.Avalonia.Views;

/// <summary>
/// Arrangement lane header column. Mirrors the approved WPF raster lane headers:
/// a 3 px accent stripe, a 20×20 track type icon at x=5, the primary label (11 px
/// Brush.Text.Primary) with the secondary route chip (9 px Brush.Text.Tertiary inside a
/// 2 px rounded border) vertically centered as a group, and 16×16 M/S command chips at
/// <c>width-39</c> / <c>width-20</c> with Brush.Red / Brush.Success active states.
/// </summary>
public sealed class LaneHeaderStrip : Control
{
    private const double IconLeft = 5d;
    private const double IconSize = 20d;
    private const double TextIndent = 23d;
    private const double LabelRightInset = 52d;
    private const double CommandSize = 16d;

    private static readonly global::Avalonia.Media.Color BackgroundColor =
        global::Avalonia.Media.Color.FromRgb(0x0E, 0x11, 0x15);
    private static readonly global::Avalonia.Media.Color BorderColor =
        global::Avalonia.Media.Color.FromRgb(0x2A, 0x30, 0x3A);
    private static readonly global::Avalonia.Media.Color PrimaryColor =
        global::Avalonia.Media.Color.FromRgb(0xF1, 0xF3, 0xF5);
    private static readonly global::Avalonia.Media.Color SecondaryColor =
        global::Avalonia.Media.Color.FromRgb(0xA7, 0xAF, 0xBB);
    private static readonly global::Avalonia.Media.Color TertiaryColor =
        global::Avalonia.Media.Color.FromRgb(0x74, 0x7E, 0x8C);
    private static readonly global::Avalonia.Media.Color RedColor =
        global::Avalonia.Media.Color.FromRgb(0xE5, 0x48, 0x4D);
    private static readonly global::Avalonia.Media.Color RedHoverColor =
        global::Avalonia.Media.Color.FromRgb(0xF2, 0x55, 0x5A);
    private static readonly global::Avalonia.Media.Color RedDarkColor =
        global::Avalonia.Media.Color.FromRgb(0x8F, 0x24, 0x29);
    private static readonly global::Avalonia.Media.Color RedSubtleColor =
        global::Avalonia.Media.Color.FromRgb(0x2A, 0x12, 0x15);
    private static readonly global::Avalonia.Media.Color SuccessColor =
        global::Avalonia.Media.Color.FromRgb(0x58, 0xC4, 0x87);
    private static readonly global::Avalonia.Media.Color SuccessSubtleColor =
        global::Avalonia.Media.Color.FromRgb(0x10, 0x25, 0x1B);

    private static readonly uint[] TrackAccentColors =
    [
        0xff4a6fa5, 0xff5f9e6e, 0xffb0b46b, 0xff8f6fa5,
        0xffa56f6f, 0xff6fa5a5, 0xff9e7bb5, 0xffb58a5f,
        0xff7b9ec4, 0xff6f9e8f, 0xffb0a05f, 0xffa05f8f,
        0xff5f7fa5, 0xff7fa55f, 0xffa5955f, 0xff8f5f7f,
        0xff6f8fb5, 0xff8fb56f, 0xffb58f6f, 0xff9f6f8f,
        0xff5f6fa5, 0xff6fa55f, 0xffa56f5f, 0xff8f6f9f,
        0xff6f9ec4, 0xff9ec46f, 0xffc49e6f, 0xffb06f9e,
        0xff5f9ea5, 0xff9ea55f, 0xffa55f9e, 0xff6fa58f,
    ];

    private static readonly SolidColorBrush BackgroundBrush = new(BackgroundColor);
    private static readonly SolidColorBrush PrimaryBrush = new(PrimaryColor);
    private static readonly SolidColorBrush SecondaryBrush = new(SecondaryColor);
    private static readonly SolidColorBrush TertiaryBrush = new(TertiaryColor);
    private static readonly Pen BorderPen = new(new SolidColorBrush(BorderColor), 1);
    private static readonly Pen ChipPen = new(new SolidColorBrush(BorderColor), 1);
    private static readonly Pen RedPen = new(new SolidColorBrush(RedColor), 1);
    private static readonly Pen SuccessPen = new(new SolidColorBrush(SuccessColor), 1);
    private static readonly SolidColorBrush RedSubtleBrush = new(RedSubtleColor);
    private static readonly SolidColorBrush SuccessSubtleBrush = new(SuccessSubtleColor);

    private readonly List<(Rect Rect, int Lane, bool IsMute)> _chipHitRects = [];
    private readonly HashSet<int> _mutedLanes = [];
    private readonly HashSet<int> _soloedLanes = [];
    private readonly Dictionary<string, Geometry?> _iconCache = [];
    private Typeface? _regularTypeface;
    private Typeface? _semiBoldTypeface;

    public static readonly StyledProperty<double> LaneHeightProperty =
        AvaloniaProperty.Register<LaneHeaderStrip, double>(nameof(LaneHeight), 56);

    public static readonly StyledProperty<int> FirstLaneProperty =
        AvaloniaProperty.Register<LaneHeaderStrip, int>(nameof(FirstLane));

    public static readonly StyledProperty<double> RulerHeightProperty =
        AvaloniaProperty.Register<LaneHeaderStrip, double>(nameof(RulerHeight));

    public static readonly StyledProperty<IReadOnlyList<string>?> TrackNamesProperty =
        AvaloniaProperty.Register<LaneHeaderStrip, IReadOnlyList<string>?>(nameof(TrackNames));

    public static readonly StyledProperty<IReadOnlyList<string>?> SecondaryLabelsProperty =
        AvaloniaProperty.Register<LaneHeaderStrip, IReadOnlyList<string>?>(nameof(SecondaryLabels));

    static LaneHeaderStrip()
    {
        AffectsRender<LaneHeaderStrip>(
            LaneHeightProperty,
            FirstLaneProperty,
            RulerHeightProperty,
            TrackNamesProperty,
            SecondaryLabelsProperty);
    }

    public double LaneHeight
    {
        get => GetValue(LaneHeightProperty);
        set => SetValue(LaneHeightProperty, value);
    }

    public int FirstLane
    {
        get => GetValue(FirstLaneProperty);
        set => SetValue(FirstLaneProperty, value);
    }

    public double RulerHeight
    {
        get => GetValue(RulerHeightProperty);
        set => SetValue(RulerHeightProperty, value);
    }

    public IReadOnlyList<string>? TrackNames
    {
        get => GetValue(TrackNamesProperty);
        set => SetValue(TrackNamesProperty, value);
    }

    public IReadOnlyList<string>? SecondaryLabels
    {
        get => GetValue(SecondaryLabelsProperty);
        set => SetValue(SecondaryLabelsProperty, value);
    }

    public event EventHandler<LaneToggleEventArgs>? MuteToggled;

    public event EventHandler<LaneToggleEventArgs>? SoloToggled;

    public sealed record LaneToggleEventArgs(int Lane, bool Active);

    /// <summary>Clears every runtime mute/solo state and repaints.</summary>
    public void ClearStates()
    {
        if (_mutedLanes.Count == 0 && _soloedLanes.Count == 0)
        {
            return;
        }

        _mutedLanes.Clear();
        _soloedLanes.Clear();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        _chipHitRects.Clear();
        context.FillRectangle(BackgroundBrush, new Rect(0, 0, width, height), 1f);
        context.DrawLine(
            BorderPen,
            new Point(Math.Round(width) - 0.5, 0),
            new Point(Math.Round(width) - 0.5, height));

        double laneHeight = Math.Max(8, LaneHeight);
        double rulerHeight = Math.Min(RulerHeight, height);
        if (rulerHeight > 0)
        {
            context.DrawLine(
                BorderPen,
                new Point(0, Math.Round(rulerHeight) + 0.5),
                new Point(width, Math.Round(rulerHeight) + 0.5));
        }

        int lane = Math.Max(0, FirstLane);
        int laneLimit = TrackNames is { Count: > 0 } named ? named.Count : int.MaxValue;
        for (double laneTop = rulerHeight;
             laneTop < height && lane < laneLimit;
             laneTop += laneHeight, lane++)
        {
            double laneBottom = Math.Min(height, laneTop + laneHeight);
            double visualHeight = laneBottom - laneTop;
            if (visualHeight < 2)
            {
                break;
            }

            global::Avalonia.Media.Color stripeColor = lane == 0
                ? TertiaryColor
                : AccentColor(lane - 1);
            context.FillRectangle(
                new ImmutableSolidColorBrush(stripeColor),
                new Rect(0, laneTop, 3, visualHeight),
                1f);

            if (lane > 0)
            {
                context.DrawLine(
                    BorderPen,
                    new Point(0, Math.Round(laneTop) + 0.5),
                    new Point(width, Math.Round(laneTop) + 0.5));
            }

            DrawLaneContent(context, lane, laneTop, visualHeight, width);
        }
    }

    private void DrawLaneContent(
        DrawingContext context,
        int lane,
        double laneTop,
        double visualHeight,
        double width)
    {
        string name = LaneName(lane);
        string secondary = LaneSecondaryLabel(lane);
        Geometry? icon = LoadIcon(lane == 0
            ? "Fluent.Wrench20Regular"
            : "Fluent.Midi20Regular");

        double iconTop = Math.Round(laneTop + (visualHeight - IconSize) / 2);
        if (icon is not null)
        {
            using (context.PushTransform(Matrix.CreateTranslation(IconLeft, iconTop)))
            {
                context.DrawGeometry(TertiaryBrush, null, icon);
            }
        }

        FormattedText primary = CreateText(name, 11, FontWeight.Normal, PrimaryBrush);
        FormattedText? detail = secondary.Length == 0
            ? null
            : CreateText(secondary, 9, FontWeight.Normal, TertiaryBrush);
        double combinedHeight = primary.Height
            + (detail is null ? 0 : detail.Height + 1);
        if (detail is not null && combinedHeight > visualHeight - 2)
        {
            detail = null;
            combinedHeight = primary.Height;
        }

        double primaryY = laneTop + Math.Max(0, (visualHeight - combinedHeight) / 2);
        double textX = 8 + TextIndent;
        double maximumRight = width - LabelRightInset;
        if (detail is not null)
        {
            double chipWidth = Math.Min(detail.Width + 6, maximumRight - textX + 3);
            if (chipWidth > 4)
            {
                Rect chip = new(textX - 3, primaryY + primary.Height, chipWidth, detail.Height + 3);
                context.DrawRectangle(null, ChipPen, chip, 2, 2);
            }
        }

        using (context.PushClip(new RoundedRect(new Rect(6, laneTop, Math.Max(0, maximumRight - 6), visualHeight))))
        {
            context.DrawText(primary, new Point(textX, primaryY));
            if (detail is not null)
            {
                context.DrawText(detail, new Point(textX, primaryY + primary.Height + 2));
            }
        }

        (primary as IDisposable)?.Dispose();
        (detail as IDisposable)?.Dispose();

        bool muted = _mutedLanes.Contains(lane);
        bool soloed = _soloedLanes.Contains(lane);
        double commandTop = laneTop + Math.Max(0, (visualHeight - CommandSize) / 2);
        Rect muteRect = new(width - 39, commandTop, CommandSize, CommandSize);
        Rect soloRect = new(width - 20, commandTop, CommandSize, CommandSize);
        _chipHitRects.Add((new Rect(width - 41, commandTop, 20, CommandSize), lane, true));
        _chipHitRects.Add((new Rect(width - 21, commandTop, 20, CommandSize), lane, false));
        DrawCommand(context, "M", muteRect, muted, solo: false);
        DrawCommand(context, "S", soloRect, soloed, solo: true);
    }

    private string LaneName(int lane)
    {
        if (lane == 0)
        {
            return "Conductor";
        }

        return TrackNames is { } names && lane < names.Count && !string.IsNullOrWhiteSpace(names[lane])
            ? names[lane]
            : $"Track {lane}";
    }

    private string LaneSecondaryLabel(int lane)
    {
        if (lane == 0)
        {
            return "Tempo & markers";
        }

        return SecondaryLabels is { } labels && lane < labels.Count
            ? labels[lane]
            : string.Empty;
    }

    private static global::Avalonia.Media.Color AccentColor(int trackIndex)
    {
        uint argb = TrackAccentColors[trackIndex % TrackAccentColors.Length];
        return global::Avalonia.Media.Color.FromArgb(
            (byte)(argb >> 24),
            (byte)(argb >> 16),
            (byte)(argb >> 8),
            (byte)argb);
    }

    /// <summary>WPF M/S commands are 16×16 squares with a 1 px border and no corner radius.</summary>
    private void DrawCommand(
        DrawingContext context,
        string glyph,
        Rect bounds,
        bool active,
        bool solo)
    {
        IBrush? fill = active
            ? solo ? SuccessSubtleBrush : RedSubtleBrush
            : null;
        Pen pen = active
            ? solo ? SuccessPen : RedPen
            : ChipPen;
        IBrush text = active
            ? solo
                ? new ImmutableSolidColorBrush(SuccessColor)
                : new ImmutableSolidColorBrush(RedHoverColor)
            : SecondaryBrush;
        context.DrawRectangle(fill, pen, bounds);
        FormattedText formatted = CreateText(glyph, 9, FontWeight.SemiBold, text);
        try
        {
            context.DrawText(
                formatted,
                new Point(
                    bounds.X + Math.Max(0, (bounds.Width - formatted.Width) / 2),
                    bounds.Y + Math.Max(0, (bounds.Height - formatted.Height) / 2)));
        }
        finally
        {
            (formatted as IDisposable)?.Dispose();
        }
    }

    private FormattedText CreateText(string text, double size, FontWeight weight, IBrush brush) =>
        new(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            weight == FontWeight.Normal
                ? _regularTypeface ??= new Typeface(FontFamily.Default)
                : _semiBoldTypeface ??= new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold),
            size,
            brush);

    private Geometry? LoadIcon(string resourceKey)
    {
        if (_iconCache.TryGetValue(resourceKey, out Geometry? cached))
        {
            return cached;
        }

        Geometry? geometry = null;
        if (Application.Current is { } app && app.TryFindResource(resourceKey, out object? value))
        {
            geometry = value as Geometry;
        }

        _iconCache[resourceKey] = geometry;
        return geometry;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Point position = e.GetPosition(this);
        foreach ((Rect rect, int lane, bool isMute) in _chipHitRects)
        {
            if (!rect.Contains(position))
            {
                continue;
            }

            HashSet<int> states = isMute ? _mutedLanes : _soloedLanes;
            bool active = !states.Remove(lane);
            if (active)
            {
                states.Add(lane);
            }

            InvalidateVisual();
            if (isMute)
            {
                MuteToggled?.Invoke(this, new LaneToggleEventArgs(lane, active));
            }
            else
            {
                SoloToggled?.Invoke(this, new LaneToggleEventArgs(lane, active));
            }

            e.Handled = true;
            return;
        }
    }
}
