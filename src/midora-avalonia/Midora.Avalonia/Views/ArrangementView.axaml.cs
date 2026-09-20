using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Midora.Avalonia.Views;

public partial class ArrangementView : UserControl
{
    private int _zoomPercent = 100;

    public ArrangementView() => InitializeComponent();

    private void OnZoomInClick(object? sender, RoutedEventArgs e) =>
        SetZoom(_zoomPercent + 25);

    private void OnZoomOutClick(object? sender, RoutedEventArgs e) =>
        SetZoom(_zoomPercent - 25);

    private void SetZoom(int percent)
    {
        _zoomPercent = Math.Clamp(percent, 25, 400);
        ZoomText.Text = $"{_zoomPercent}%";
    }
}
