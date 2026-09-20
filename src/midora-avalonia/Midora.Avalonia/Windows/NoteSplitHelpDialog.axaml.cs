using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Midora.Avalonia.Windows;

/// <summary>
/// Avalonia port of <c>Midora.Desktop.NoteSplitHelpDialog</c>. Static help content; the only
/// behavior is closing the dialog and dragging the custom title bar.
/// </summary>
public partial class NoteSplitHelpDialog : Window
{
    public NoteSplitHelpDialog() => InitializeComponent();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }
}
