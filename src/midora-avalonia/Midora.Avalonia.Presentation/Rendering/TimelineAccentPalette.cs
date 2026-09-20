using Avalonia.Media;

namespace Midora.Avalonia.Presentation.Rendering;

public readonly record struct TimelineAccentPalette(
    Color Segment,
    Color SelectedSegment,
    Color SelectionBorder,
    Color NotePreview)
{
    private const double FallbackHue = 208;

    public static TimelineAccentPalette FromArgb(uint argb)
    {
        byte red = (byte)(argb >> 16);
        byte green = (byte)(argb >> 8);
        byte blue = (byte)argb;
        RgbToHsl(red, green, blue, out double hue, out double saturation, out _);
        if (saturation < 0.01) hue = FallbackHue;
        return new(
            HslToColor(hue, 0.143, 0.302),
            HslToColor(hue, 0.181, 0.229),
            HslToColor(hue, 0.208, 0.645),
            HslToColor(hue, 0.143, 0.776));
    }

    private static void RgbToHsl(
        byte red,
        byte green,
        byte blue,
        out double hue,
        out double saturation,
        out double lightness)
    {
        double r = red / 255d;
        double g = green / 255d;
        double b = blue / 255d;
        double maximum = Math.Max(r, Math.Max(g, b));
        double minimum = Math.Min(r, Math.Min(g, b));
        double delta = maximum - minimum;
        lightness = (maximum + minimum) / 2;
        if (delta == 0)
        {
            hue = 0;
            saturation = 0;
            return;
        }
        saturation = delta / (1 - Math.Abs(2 * lightness - 1));
        if (maximum == r) hue = 60 * (((g - b) / delta) % 6);
        else if (maximum == g) hue = 60 * (((b - r) / delta) + 2);
        else hue = 60 * (((r - g) / delta) + 4);
        if (hue < 0) hue += 360;
    }

    private static Color HslToColor(double hue, double saturation, double lightness)
    {
        hue = (hue % 360 + 360) % 360;
        double chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double intermediate = chroma * (1 - Math.Abs((hue / 60) % 2 - 1));
        double offset = lightness - chroma / 2;
        (double red, double green, double blue) = hue switch
        {
            < 60 => (chroma, intermediate, 0d),
            < 120 => (intermediate, chroma, 0d),
            < 180 => (0d, chroma, intermediate),
            < 240 => (0d, intermediate, chroma),
            < 300 => (intermediate, 0d, chroma),
            _ => (chroma, 0d, intermediate)
        };
        return Color.FromRgb(ToByte(red + offset), ToByte(green + offset), ToByte(blue + offset));
    }

    private static byte ToByte(double value) =>
        (byte)Math.Clamp((int)Math.Round(value * 255, MidpointRounding.AwayFromZero), 0, 255);
}
