using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Midora.Avalonia.Presentation.Rendering;

namespace Midora.Avalonia.Presentation.Controls;

/// <summary>
/// Left piano keyboard for the pitch-oriented surface modes. Mirrors the surface's lane math:
/// the highest visible pitch is at the top and lane == absolute MIDI pitch.
/// </summary>
public sealed class PianoKeyboardStrip : Control
{
    public static readonly StyledProperty<int> FirstLaneProperty =
        AvaloniaProperty.Register<PianoKeyboardStrip, int>(nameof(FirstLane), 36);

    public static readonly StyledProperty<double> LaneHeightProperty =
        AvaloniaProperty.Register<PianoKeyboardStrip, double>(nameof(LaneHeight), 12);

    public static readonly StyledProperty<double> RulerHeightProperty =
        AvaloniaProperty.Register<PianoKeyboardStrip, double>(nameof(RulerHeight), 24);

    private static readonly global::Avalonia.Media.Color BackgroundColor =
        global::Avalonia.Media.Color.FromRgb(0x0E, 0x11, 0x15);

    private static readonly global::Avalonia.Media.Color BorderColor =
        global::Avalonia.Media.Color.FromRgb(0x2A, 0x30, 0x3A);

    private static readonly global::Avalonia.Media.Color WhiteKeyColor =
        global::Avalonia.Media.Color.FromRgb(0xD4, 0xD8, 0xDD);

    private static readonly global::Avalonia.Media.Color WhiteKeyPressedColor =
        global::Avalonia.Media.Color.FromRgb(0xF2, 0x8B, 0x8F);

    private static readonly global::Avalonia.Media.Color BlackKeyColor =
        global::Avalonia.Media.Color.FromRgb(0x15, 0x18, 0x1D);

    private static readonly global::Avalonia.Media.Color BlackKeyPressedColor =
        global::Avalonia.Media.Color.FromRgb(0xC7, 0x37, 0x3C);

    private static readonly global::Avalonia.Media.Color LabelColor =
        global::Avalonia.Media.Color.FromRgb(0x25, 0x2B, 0x33);

    private static readonly SolidColorBrush BackgroundBrush = new(BackgroundColor);
    private static readonly SolidColorBrush WhiteKeyBrush = new(WhiteKeyColor);
    private static readonly SolidColorBrush WhiteKeyPressedBrush = new(WhiteKeyPressedColor);
    private static readonly SolidColorBrush BlackKeyBrush = new(BlackKeyColor);
    private static readonly SolidColorBrush BlackKeyPressedBrush = new(BlackKeyPressedColor);
    private static readonly SolidColorBrush LabelBrush = new(LabelColor);
    private static readonly Pen BorderPen = new(new SolidColorBrush(BorderColor), 1);
    private static readonly Pen WhiteKeyPen = new(new SolidColorBrush(BorderColor), 1);
    private static readonly Typeface LabelTypeface = new(
        FontFamily.Default,
        FontStyle.Normal,
        FontWeight.SemiBold,
        FontStretch.Normal);

    private int _pressedPitch = -1;

    static PianoKeyboardStrip()
    {
        AffectsRender<PianoKeyboardStrip>(
            FirstLaneProperty,
            LaneHeightProperty,
            RulerHeightProperty);
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

    public double RulerHeight
    {
        get => GetValue(RulerHeightProperty);
        set => SetValue(RulerHeightProperty, value);
    }

    public event EventHandler<int>? KeyPressed;

    public event EventHandler<int>? KeyReleased;

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        context.FillRectangle(BackgroundBrush, new Rect(0, 0, width, height), 1f);
        context.DrawLine(
            BorderPen,
            new Point(Math.Round(width) - 0.5, 0),
            new Point(Math.Round(width) - 0.5, height));

        double laneHeight = Math.Max(4, LaneHeight);
        double rulerHeight = Math.Min(RulerHeight, height);
        int rowCount = (int)Math.Floor((height - rulerHeight) / laneHeight);
        int firstLane = Math.Clamp(FirstLane, 0, 127);

        for (int row = 0; row < rowCount; row++)
        {
            int pitch = firstLane + rowCount - 1 - row;
            if (pitch is < 0 or > 127)
            {
                continue;
            }

            double top = rulerHeight + row * laneHeight;
            double keyHeight = Math.Max(1, laneHeight);
            bool black = PianoKeyPresentation.IsBlackKey(pitch);
            bool pressed = pitch == _pressedPitch;
            Rect whiteBounds = new(0, top, width, keyHeight);
            context.DrawRectangle(
                pressed ? WhiteKeyPressedBrush : WhiteKeyBrush,
                BorderPen,
                whiteBounds);
            if (black)
            {
                double blackKeyWidth = Math.Max(12, Math.Round(width * 0.68));
                Rect blackBounds = new(0, top + 1, blackKeyWidth, Math.Max(1, keyHeight - 2));
                context.DrawRectangle(
                    pressed ? BlackKeyPressedBrush : BlackKeyBrush,
                    BorderPen,
                    blackBounds,
                    1,
                    1);
                continue;
            }

            string? label = PianoKeyPresentation.GetOctaveCLabel(pitch);
            if (label is null)
            {
                continue;
            }

            double fontSize = Math.Clamp(laneHeight * 0.56, 8, 11);
            FormattedText formatted = new(
                label,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                LabelTypeface,
                fontSize,
                LabelBrush);
            try
            {
                double labelX = Math.Max(4, width - formatted.Width - 6);
                double labelY = top + (keyHeight - formatted.Height) / 2;
                context.DrawText(formatted, new Point(labelX, labelY));
            }
            finally
            {
                (formatted as IDisposable)?.Dispose();
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Point position = e.GetPosition(this);
        int pitch = PitchAt(position);
        if (pitch < 0)
        {
            return;
        }

        _pressedPitch = pitch;
        InvalidateVisual();
        KeyPressed?.Invoke(this, pitch);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressedPitch < 0)
        {
            return;
        }

        int pitch = _pressedPitch;
        _pressedPitch = -1;
        InvalidateVisual();
        KeyReleased?.Invoke(this, pitch);
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_pressedPitch < 0)
        {
            return;
        }

        _pressedPitch = -1;
        InvalidateVisual();
    }

    private int PitchAt(Point position)
    {
        double laneHeight = Math.Max(4, LaneHeight);
        double rulerHeight = Math.Min(RulerHeight, Bounds.Height);
        double contentY = position.Y - rulerHeight;
        if (contentY < 0 || contentY >= Bounds.Height - rulerHeight)
        {
            return -1;
        }

        int rowCount = (int)Math.Floor((Bounds.Height - rulerHeight) / laneHeight);
        int row = (int)Math.Floor(contentY / laneHeight);
        int pitch = Math.Clamp(FirstLane, 0, 127) + rowCount - 1 - row;
        if (pitch is < 0 or > 127)
        {
            return -1;
        }

        if (PianoKeyPresentation.IsBlackKey(pitch) && position.X > Bounds.Width * 0.62)
        {
            return -1;
        }

        return pitch;
    }

}
