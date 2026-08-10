using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace Midora.Desktop;

public partial class TextDetailsDialog : Window
{
    public TextDetailsDialog(string title, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(message);
        InitializeComponent();
        Title = title;
        MessageTextBox.Text = message;
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetDataObject(MessageTextBox.Text, copy: true);
            CopyButton.Content = "Copied";
        }
        catch (ExternalException)
        {
            CopyButton.Content = "Copy failed";
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
