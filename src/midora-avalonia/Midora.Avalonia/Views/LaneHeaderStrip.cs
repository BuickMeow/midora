using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.Media;

namespace Midora.Avalonia.Views;

/// <summary>
/// Left track-header column for the Arrangement timeline: conductor row plus one header per
/// imported track with its accent stripe, name, channel summary and M/S chips.
/// </summary>
public sealed class LaneHeaderStrip : Control
{
    public static readonly StyledProperty<double> LaneHeightProperty =
        AvaloniaProperty.Register<LaneHeaderStrip, double>(nameof(LaneHeight), 28);

    public static readonly StyledProperty<int> FirstLaneProperty =
        AvaloniaProperty.Register<LaneHeaderStrip, int>(nameof(FirstLane));

    public static readonly StyledProperty<double> RulerHeightProperty =
        AvaloniaProperty.Register<LaneHeaderStrip, double>(nameof(RulerHeight), 24);

    public static readonly StyledProperty<IReadOnlyList<string>?> TrackNamesProperty =
        AvaloniaProperty.Register<LaneHeaderStrip, IReadOnlyList<string>?>(nameof(TrackNames));

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

    private static readonly global::Avalonia.Media.Color ChipColor =
        global::Avalonia.Media.Color.FromRgb(0x1A, 0x1F, 0x27);

    private static readonly global::Avalonia.Media.Color ChipBorderColor =
        global::Avalonia.Media.Color.FromRgb(0x3A, 0x42, 0x4F);

    private static readonly global::Avalonia.Media.Color ConductorColor =
        global::Avalonia.Media.Color.FromRgb(0xE5, 0x48, 0x4D);

    private static readonly uint[] TrackAccentColors =
    [
        0xff62a6f6,
        0xff58c487,
        0xffe8b34b,
        0xffaf7ac5,
        0xffe0776b,
        0xff4fb3b3,
        0xff9a8cff,
        0xffb0b46b
    ];

    private static readonly SolidColorBrush BackgroundBrush = new(BackgroundColor);
    private static readonly SolidColorBrush ChipBrush = new(ChipColor);
    private static readonly SolidColorBrush PrimaryBrush = new(PrimaryColor);
    private static readonly SolidColorBrush SecondaryBrush = new(SecondaryColor);
    private static readonly SolidColorBrush TertiaryBrush = new(TertiaryColor);
    private static readonly Pen BorderPen = new(new SolidColorBrush(BorderColor), 1);
    private static readonly Pen ChipPen = new(new SolidColorBrush(ChipBorderColor), 1);

    private readonly List<(Rect Rect, int Lane, bool IsMute)> _chipHitRects = [];
    private readonly HashSet<int> _mutedLanes = [];
    private readonly HashSet<int> _soloedLanes = [];
    private Typeface? _typeface;

    static LaneHeaderStrip()
    {
        AffectsRender<LaneHeaderStrip>(
            LaneHeightProperty,
            FirstLaneProperty,
            RulerHeightProperty,
            TrackNamesProperty);
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

    public event EventHandler<LaneToggleEventArgs>? MuteToggled;

    public event EventHandler<LaneToggleEventArgs>? SoloToggled;

    public sealed record LaneToggleEventArgs(int Lane, bool Active);

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
        context.DrawLine(
            BorderPen,
            new Point(0, Math.Round(rulerHeight) + 0.5),
            new Point(width, Math.Round(rulerHeight) + 0.5));

        int lane = Math.Max(0, FirstLane);
        int laneLimit = TrackNames is { Count: > 0 } named ? named.Count : int.MaxValue;
        for (double laneTop = rulerHeight;
             laneTop < height && lane < laneLimit;
             laneTop += laneHeight, lane++)
        {
            double laneBottom = Math.Min(height, laneTop + laneHeight);
            if (laneBottom - laneTop < 2)
            {
                break;
            }

            global::Avalonia.Media.Color stripeColor = lane == 0
                ? ConductorColor
                : AccentColor(lane - 1);
            context.FillRectangle(
                new SolidColorBrush(stripeColor),
                new Rect(0, laneTop, 3, laneBottom - laneTop),
                1f);

            if (lane > 0)
            {
                context.DrawLine(
                    BorderPen,
                    new Point(0, Math.Round(laneTop) + 0.5),
                    new Point(width, Math.Round(laneTop) + 0.5));
            }

            string name = lane == 0
                ? "Conductor"
                : TrackNames is { } names && lane < names.Count && !string.IsNullOrWhiteSpace(names[lane])
                    ? names[lane]
                    : $"Track {lane}";
            string summary = lane == 0 ? "Tempo & markers" : $"P1 Ch{lane} Melodic";

            DrawText(context, name, 10, laneTop + 3, PrimaryBrush, 12, 600);
            DrawText(context, summary, 10, laneTop + 17, TertiaryBrush, 10, 400);

            bool muted = _mutedLanes.Contains(lane);
            bool soloed = _soloedLanes.Contains(lane);
            Rect muteRect = new(width - 47, laneTop + 7, 18, 14);
            Rect soloRect = new(width - 25, laneTop + 7, 18, 14);
            _chipHitRects.Add((muteRect, lane, true));
            _chipHitRects.Add((soloRect, lane, false));
            DrawChip(context, "M", muteRect, muted);
            DrawChip(context, "S", soloRect, soloed, solo: true);
        }
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

    private void DrawChip(DrawingContext context, string glyph, Rect chip, bool active, bool solo = false)
    {
        IBrush fill = active
            ? new SolidColorBrush(solo
                ? global::Avalonia.Media.Color.FromRgb(0x2A, 0x21, 0x11)
                : global::Avalonia.Media.Color.FromRgb(0x2A, 0x12, 0x15))
            : ChipBrush;
        Pen pen = active
            ? new Pen(new SolidColorBrush(solo
                ? global::Avalonia.Media.Color.FromRgb(0xE8, 0xB3, 0x4B)
                : global::Avalonia.Media.Color.FromRgb(0x8F, 0x24, 0x29)), 1)
            : ChipPen;
        IBrush textBrush = active
            ? new SolidColorBrush(solo
                ? global::Avalonia.Media.Color.FromRgb(0xE8, 0xB3, 0x4B)
                : global::Avalonia.Media.Color.FromRgb(0xF2, 0x55, 0x5A))
            : SecondaryBrush;
        context.DrawRectangle(fill, pen, chip, 3, 3);
        DrawText(context, glyph, chip.X + 5.5, chip.Y + 0.5, textBrush, 10, 600);
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

    private void DrawText(
        DrawingContext context,
        string text,
        double x,
        double y,
        IBrush brush,
        double fontSize,
        int weight)
    {
        _typeface ??= ResolveTypeface(weight);
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(_typeface?.FontFamily ?? FontFamily.Default, FontStyle.Normal, FontWeight.Normal),
            fontSize,
            brush);
        context.DrawText(formatted, new Point(x, y));
    }

    private static Typeface ResolveTypeface(int weight)
    {
        FontFamily family = FontFamily.Default;
        if (Application.Current is { } app &&
            app.TryFindResource("Font.UI", out object? value) &&
            value is FontFamily resolved)
        {
            family = resolved;
        }

        return new Typeface(family, FontStyle.Normal, FontWeight.Normal);
    }
}
