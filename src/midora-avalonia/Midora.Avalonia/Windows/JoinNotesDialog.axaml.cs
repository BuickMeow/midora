using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Midora.Avalonia.Windows;

/// <summary>
/// Placeholder for the Midora.Application note join options; MaximumGap is the only value
/// carried by the dialog.
/// </summary>
public sealed record NoteJoinOptions(long MaximumGap);

public partial class JoinNotesDialog : Window
{
    public JoinNotesDialog()
    {
        InitializeComponent();
        Opened += (_, _) => MaximumGapBox.Focus();
    }

    /// <summary>
    /// Result semantics: a non-null value means Apply was confirmed. Avalonia has no
    /// Window.DialogResult, so <c>Close()</c> plus this property carries the outcome.
    /// </summary>
    public NoteJoinOptions? Options { get; private set; }

    public static NoteJoinOptions ParseOptions(string? maximumGapText)
    {
        if (!long.TryParse(
                maximumGapText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long maximumGap)
            || maximumGap < 0)
        {
            throw new ArgumentException("Maximum Gap must be a non-negative Int64 Tick value.");
        }
        return new(maximumGap);
    }

    private void OnInputChanged(object? sender, TextChangedEventArgs e)
    {
        if (!IsInitialized || ValidationText is null) return;
        ValidationText.Text = string.Empty;
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Options = ParseOptions(MaximumGapBox.Text);
            Close();
        }
        catch (ArgumentException exception)
        {
            ValidationText.Text = exception.Message;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
