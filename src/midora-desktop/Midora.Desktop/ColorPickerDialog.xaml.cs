using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Midora.Desktop;

public partial class ColorPickerDialog : Window
{
    private const int WheelSize = 220;
    private const int BrightnessWidth = 28;
    private const int BrightnessHeight = 218;
    private bool _updating;
    private bool _draggingWheel;
    private bool _draggingBrightness;
    private double _hue;
    private double _saturation;
    private double _value;
    private byte _red;
    private byte _green;
    private byte _blue;

    public ColorPickerDialog(byte red, byte green, byte blue)
    {
        InitializeComponent();
        _red = red;
        _green = green;
        _blue = blue;
        RgbToHsv(red, green, blue, out _hue, out _saturation, out _value);
        Loaded += (_, _) =>
        {
            RenderColorWheel();
            RenderBrightnessBar();
            UpdateInputsFromColor();
            UpdatePreviewAndPointers();
        };
    }

    public byte Red { get; private set; }
    public byte Green { get; private set; }
    public byte Blue { get; private set; }

    private void RenderColorWheel()
    {
        WriteableBitmap bitmap = new(WheelSize, WheelSize, 96, 96, PixelFormats.Bgra32, null);
        byte[] pixels = new byte[WheelSize * WheelSize * 4];
        double radius = (WheelSize - 2) / 2d;
        double center = WheelSize / 2d;
        for (int y = 0; y < WheelSize; y++)
        {
            for (int x = 0; x < WheelSize; x++)
            {
                double dx = x + 0.5 - center;
                double dy = y + 0.5 - center;
                double distance = Math.Sqrt(dx * dx + dy * dy);
                int index = (y * WheelSize + x) * 4;
                if (distance > radius) continue;
                double hue = (Math.Atan2(dy, dx) * 180d / Math.PI + 360d) % 360d;
                HsvToRgb(hue, Math.Clamp(distance / radius, 0, 1), 1, out byte r, out byte g, out byte b);
                pixels[index] = b;
                pixels[index + 1] = g;
                pixels[index + 2] = r;
                pixels[index + 3] = 255;
            }
        }
        bitmap.WritePixels(new Int32Rect(0, 0, WheelSize, WheelSize), pixels, WheelSize * 4, 0);
        bitmap.Freeze();
        ColorWheelImage.Source = bitmap;
    }

    private void RenderBrightnessBar()
    {
        WriteableBitmap bitmap = new(BrightnessWidth, BrightnessHeight, 96, 96, PixelFormats.Bgra32, null);
        byte[] pixels = new byte[BrightnessWidth * BrightnessHeight * 4];
        for (int y = 0; y < BrightnessHeight; y++)
        {
            HsvToRgb(_hue, _saturation, 1d - y / (double)(BrightnessHeight - 1), out byte r, out byte g, out byte b);
            for (int x = 0; x < BrightnessWidth; x++)
            {
                int index = (y * BrightnessWidth + x) * 4;
                pixels[index] = b;
                pixels[index + 1] = g;
                pixels[index + 2] = r;
                pixels[index + 3] = 255;
            }
        }
        bitmap.WritePixels(new Int32Rect(0, 0, BrightnessWidth, BrightnessHeight), pixels, BrightnessWidth * 4, 0);
        bitmap.Freeze();
        BrightnessImage.Source = bitmap;
    }

    private void OnWheelMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _draggingWheel = true;
        ((UIElement)sender).CaptureMouse();
        UpdateHueSaturation(e.GetPosition((IInputElement)sender));
    }

    private void OnWheelMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _draggingWheel = false;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private void OnWheelMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingWheel) UpdateHueSaturation(e.GetPosition((IInputElement)sender));
    }

    private void OnBrightnessMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _draggingBrightness = true;
        ((UIElement)sender).CaptureMouse();
        UpdateBrightness(e.GetPosition((IInputElement)sender));
    }

    private void OnBrightnessMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _draggingBrightness = false;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private void OnBrightnessMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingBrightness) UpdateBrightness(e.GetPosition((IInputElement)sender));
    }

    private void UpdateHueSaturation(Point point)
    {
        double radius = (WheelSize - 2) / 2d;
        double center = WheelSize / 2d;
        double dx = point.X - center;
        double dy = point.Y - center;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance > radius && distance > 0)
        {
            double scale = radius / distance;
            dx *= scale;
            dy *= scale;
            distance = radius;
        }
        _hue = (Math.Atan2(dy, dx) * 180d / Math.PI + 360d) % 360d;
        _saturation = Math.Clamp(distance / radius, 0, 1);
        UpdateColorFromHsv();
        RenderBrightnessBar();
        UpdateInputsFromColor();
        UpdatePreviewAndPointers();
    }

    private void UpdateBrightness(Point point)
    {
        double y = Math.Clamp(point.Y - 1, 0, BrightnessHeight - 1d);
        _value = 1d - y / (BrightnessHeight - 1d);
        UpdateColorFromHsv();
        UpdateInputsFromColor();
        UpdatePreviewAndPointers();
    }

    private void UpdateColorFromHsv() => HsvToRgb(_hue, _saturation, _value, out _red, out _green, out _blue);

    private void OnRgbTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;
        if (!byte.TryParse(RedTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte red)
            || !byte.TryParse(GreenTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte green)
            || !byte.TryParse(BlueTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte blue))
        {
            ErrorTextBlock.Text = "RGB values must be integers from 0 to 255.";
            return;
        }
        ErrorTextBlock.Text = string.Empty;
        _red = red;
        _green = green;
        _blue = blue;
        RgbToHsv(red, green, blue, out _hue, out _saturation, out _value);
        RenderBrightnessBar();
        UpdateInputsFromColor(updateRgb: false, updateHex: true);
        UpdatePreviewAndPointers();
    }

    private void OnHexTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;
        if (!TryParseHex(HexTextBox.Text, out byte red, out byte green, out byte blue))
        {
            ErrorTextBlock.Text = "Hex color must use #RRGGBB.";
            return;
        }
        ErrorTextBlock.Text = string.Empty;
        _red = red;
        _green = green;
        _blue = blue;
        RgbToHsv(red, green, blue, out _hue, out _saturation, out _value);
        RenderBrightnessBar();
        UpdateInputsFromColor(updateRgb: true, updateHex: false);
        UpdatePreviewAndPointers();
    }

    private void UpdateInputsFromColor(bool updateRgb = true, bool updateHex = true)
    {
        _updating = true;
        try
        {
            if (updateRgb)
            {
                RedTextBox.Text = _red.ToString(CultureInfo.InvariantCulture);
                GreenTextBox.Text = _green.ToString(CultureInfo.InvariantCulture);
                BlueTextBox.Text = _blue.ToString(CultureInfo.InvariantCulture);
            }
            if (updateHex) HexTextBox.Text = $"#{_red:X2}{_green:X2}{_blue:X2}";
        }
        finally
        {
            _updating = false;
        }
    }

    private void UpdatePreviewAndPointers()
    {
        SolidColorBrush brush = new(Color.FromRgb(_red, _green, _blue));
        brush.Freeze();
        PreviewFill.Fill = brush;
        double radius = (WheelSize - 2) / 2d;
        double center = WheelSize / 2d;
        double angle = _hue * Math.PI / 180d;
        Canvas.SetLeft(WheelSelector, center + Math.Cos(angle) * _saturation * radius - WheelSelector.Width / 2d);
        Canvas.SetTop(WheelSelector, center + Math.Sin(angle) * _saturation * radius - WheelSelector.Height / 2d);
        Canvas.SetTop(BrightnessSelector, 1 + (1 - _value) * (BrightnessHeight - 1d) - BrightnessSelector.Height / 2d);
    }

    private static bool TryParseHex(string text, out byte red, out byte green, out byte blue)
    {
        red = green = blue = 0;
        string value = text.Trim();
        if (value.StartsWith('#')) value = value[1..];
        if (value.Length != 6 || value.Any(character => !Uri.IsHexDigit(character))) return false;
        red = byte.Parse(value[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        green = byte.Parse(value.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        blue = byte.Parse(value.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return true;
    }

    private static void HsvToRgb(double hue, double saturation, double value, out byte red, out byte green, out byte blue)
    {
        hue = (hue % 360 + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);
        double chroma = value * saturation;
        double intermediate = chroma * (1 - Math.Abs((hue / 60) % 2 - 1));
        double offset = value - chroma;
        (double r, double g, double b) = hue switch
        {
            < 60 => (chroma, intermediate, 0d),
            < 120 => (intermediate, chroma, 0d),
            < 180 => (0d, chroma, intermediate),
            < 240 => (0d, intermediate, chroma),
            < 300 => (intermediate, 0d, chroma),
            _ => (chroma, 0d, intermediate)
        };
        red = ToByte((r + offset) * 255);
        green = ToByte((g + offset) * 255);
        blue = ToByte((b + offset) * 255);
    }

    private static void RgbToHsv(byte red, byte green, byte blue, out double hue, out double saturation, out double value)
    {
        double r = red / 255d;
        double g = green / 255d;
        double b = blue / 255d;
        double maximum = Math.Max(r, Math.Max(g, b));
        double minimum = Math.Min(r, Math.Min(g, b));
        double delta = maximum - minimum;
        if (delta == 0) hue = 0;
        else if (maximum == r) hue = 60 * (((g - b) / delta) % 6);
        else if (maximum == g) hue = 60 * (((b - r) / delta) + 2);
        else hue = 60 * (((r - g) / delta) + 4);
        if (hue < 0) hue += 360;
        saturation = maximum == 0 ? 0 : delta / maximum;
        value = maximum;
    }

    private static byte ToByte(double value) =>
        (byte)Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);

    private void OnAcceptClick(object sender, RoutedEventArgs e)
    {
        if (!TryParseHex(HexTextBox.Text, out byte red, out byte green, out byte blue))
        {
            ErrorTextBlock.Text = "Hex color must use #RRGGBB.";
            return;
        }
        Red = red;
        Green = green;
        Blue = blue;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
