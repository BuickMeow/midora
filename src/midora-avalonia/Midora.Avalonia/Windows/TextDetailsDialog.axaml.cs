using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Midora.Avalonia.Windows;

public partial class TextDetailsDialog : Window
{
    public TextDetailsDialog()
        : this(
            "Compile Details",
            string.Join(
                Environment.NewLine,
                "Project:        Untitled Project",
                "Compile mode:   Full",
                "Source:         placeholder rows",
                "Logical tracks: 3",
                "Pure MIDI:      1",
                "Segments:       7",
                "Notes:          1,284",
                "CC events:      96",
                "Diagnostics:    0 errors, 0 warnings",
                "Canonical:      pending",
                "SoundFonts:     not configured"))
    {
    }

    public TextDetailsDialog(string title, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(message);
        InitializeComponent();
        Title = title;
        MessageTextBox.Text = message;
    }

    public string Message => MessageTextBox.Text ?? string.Empty;

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                CopyButton.Content = "Copy failed";
                return;
            }
            await clipboard.SetTextAsync(Message);
            CopyButton.Content = "Copied";
        }
        catch (Exception)
        {
            CopyButton.Content = "Copy failed";
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
