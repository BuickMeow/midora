namespace Midora.Avalonia.Presentation.Rendering;

/// <summary>
/// Port shim for <c>System.Windows.DpiScale</c>; keeps ported signatures identical while
/// avoiding any WPF dependency.
/// </summary>
public readonly record struct DpiScale(double DpiScaleX, double DpiScaleY)
{
    public double PixelsPerDip => DpiScaleX;
}
