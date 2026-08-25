using System.Windows;
using System.Windows.Input;

namespace Midora.Desktop;

public partial class MappingFunctionHelpDialog : Window
{
    public MappingFunctionHelpDialog() => InitializeComponent();

    private void OnCloseClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
