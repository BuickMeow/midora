using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Midora.Avalonia.Windows;

/// <summary>Completion rows shown by the demo popup; mirrors the WPF completion item shape.</summary>
public sealed record BatchExpressionCompletionItem(
    string Text,
    string Kind,
    string Description,
    string InsertText,
    int CaretBacktrack);

/// <summary>
/// Avalonia port of <c>Midora.Desktop.BatchExpressionEditor</c>. The WPF control switched between a
/// plain <c>TextBox</c> and an AvalonEdit expression editor; the Avalonia window shows both branches
/// (plain branch on the "Gate" row, expression branch on the "Velocity" row) with an ordinary
/// monospace <see cref="TextBox"/> in place of AvalonEdit. Syntax colorization, bracket matching and
/// caret-anchored positioning of the completion popup are not reproduced; the popup itself keeps the
/// WPF list/signature structure and can be opened with Ctrl+Space or the demo button.
/// </summary>
public partial class BatchExpressionEditor : Window
{
    public BatchExpressionEditor()
    {
        InitializeComponent();
        CompletionList.ItemsSource = CompletionItems;
        CompletionList.SelectedIndex = 0;
        CompletionPopup.PlacementTarget = VelocityBox;
        UpdateSignaturePanel();
    }

    public IReadOnlyList<BatchExpressionCompletionItem> CompletionItems { get; } =
    [
        new("Clamp(value, min, max)", "Method", "Clamps a value into an inclusive range.", "Clamp(value, min, max)", 0),
        new("v0", "Variable", "Original Velocity before the edit.", "v0", 0),
        new("v1", "Variable", "Calculated Velocity result.", "v1", 0),
        new("k0", "Variable", "Original Key Number before the edit.", "k0", 0),
        new("g0", "Variable", "Original Gate before the edit.", "g0", 0),
        new("t0", "Variable", "Original Tick before the edit.", "t0", 0),
        new("tr", "Variable", "Tick relative to the earliest selected object.", "tr", 0),
        new("Math.Round", "Method", "Rounds a number to the nearest integral value.", "Math.Round", 0),
        new("Math.Sin", "Method", "Returns the sine of the specified angle.", "Math.Sin", 0),
        new("PI", "Constant", "Ratio of a circle's circumference to its diameter.", "PI", 0)
    ];

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void OnShowCompletionClick(object? sender, RoutedEventArgs e) =>
        CompletionPopup.IsOpen = !CompletionPopup.IsOpen;

    private void OnExpressionKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            CompletionPopup.IsOpen = true;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && CompletionPopup.IsOpen)
        {
            CompletionPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void OnCompletionSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        UpdateSignaturePanel();

    private void OnCompletionDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (CompletionList.SelectedItem is not BatchExpressionCompletionItem item) return;

        string text = VelocityBox.Text ?? string.Empty;
        int caret = Math.Clamp(VelocityBox.CaretIndex, 0, text.Length);
        int start = caret;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;

        VelocityBox.Text = text[..start] + item.InsertText + text[caret..];
        VelocityBox.CaretIndex = Math.Max(
            start,
            start + item.InsertText.Length - Math.Min(item.CaretBacktrack, item.InsertText.Length));
        CompletionPopup.IsOpen = false;
    }

    private void UpdateSignaturePanel()
    {
        if (CompletionList.SelectedItem is not BatchExpressionCompletionItem item)
        {
            SignatureText.Text = string.Empty;
            SignaturePanel.IsVisible = false;
            return;
        }

        SignatureText.Text = item.Description;
        SignaturePanel.IsVisible = true;
    }
}
