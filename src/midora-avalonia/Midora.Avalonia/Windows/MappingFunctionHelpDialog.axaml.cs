using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Midora.Avalonia.Windows;

/// <summary>
/// Avalonia port of <c>Midora.Desktop.MappingFunctionHelpDialog</c>. Static help content; OK used
/// <c>DialogResult = true</c> in WPF, which has no Avalonia equivalent — <see cref="Window.Close"/>
/// carries the confirm result.
/// </summary>
public partial class MappingFunctionHelpDialog : Window
{
    public MappingFunctionHelpDialog() => InitializeComponent();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }
}
