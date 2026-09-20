using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Midora.Avalonia.Windows;

/// <summary>Completion rows shown by the demo popup; mirrors the WPF completion item shape.</summary>
public sealed record MappingFunctionCompletionItem(
    string Text,
    string Kind,
    string Description,
    string InsertText,
    int CaretBacktrack);

/// <summary>
/// Avalonia port of <c>Midora.Desktop.MappingFunctionCodeEditor</c>. The AvalonEdit host is replaced
/// by an ordinary monospace <see cref="TextBox"/> inside the same chrome; semantic colorization,
/// bracket matching and caret-anchored popup positioning are not reproduced. The completion popup
/// keeps the WPF list/signature structure and the WPF <c>CaretStatus</c> property is surfaced as the
/// "Ln x, Col y" readout next to the result bar.
/// </summary>
public partial class MappingFunctionCodeEditor : Window
{
    public MappingFunctionCodeEditor()
    {
        InitializeComponent();
        CompletionList.ItemsSource = CompletionItems;
        CompletionList.SelectedIndex = 0;
        CompletionPopup.PlacementTarget = CodeBox;
        CodeBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.CaretIndexProperty) UpdateCaretStatus();
        };
        UpdateCaretStatus();
        UpdateSignaturePanel();
    }

    public IReadOnlyList<MappingFunctionCompletionItem> CompletionItems { get; } =
    [
        new("value", "Parameter", "The data-domain value entering this Mapping Step.", "value", 0),
        new("context", "Parameter", "The bounded MappingContextV2 snapshot.", "context", 0),
        new("Math.Sin", "Method", "Returns the sine of the specified angle.", "Math.Sin", 0),
        new("Math.Round", "Method", "Rounds a number to the nearest integral value.", "Math.Round", 0),
        new("Clamp(value, min, max)", "Method", "Clamps a value into an inclusive range.", "Clamp(value, min, max)", 26),
        new("context.TriggerVelocity", "Property", "Velocity of the note that triggered this step.", "context.TriggerVelocity", 0),
        new("context.TemplateTick", "Property", "Source Template Event tick.", "context.TemplateTick", 0),
        new("PI", "Constant", "Ratio of a circle's circumference to its diameter.", "PI", 0),
        new("E", "Constant", "Base of the natural logarithm.", "E", 0)
    ];

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void OnShowCompletionClick(object? sender, RoutedEventArgs e) =>
        CompletionPopup.IsOpen = !CompletionPopup.IsOpen;

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
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
        if (CompletionList.SelectedItem is not MappingFunctionCompletionItem item) return;

        string text = CodeBox.Text ?? string.Empty;
        int caret = Math.Clamp(CodeBox.CaretIndex, 0, text.Length);
        int start = caret;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;

        CodeBox.Text = text[..start] + item.InsertText + text[caret..];
        CodeBox.CaretIndex = Math.Max(
            start,
            start + item.InsertText.Length - Math.Min(item.CaretBacktrack, item.InsertText.Length));
        CompletionPopup.IsOpen = false;
        UpdateCaretStatus();
    }

    private void UpdateCaretStatus()
    {
        string text = CodeBox.Text ?? string.Empty;
        int caret = Math.Clamp(CodeBox.CaretIndex, 0, text.Length);
        int line = 1;
        int column = 1;
        for (int index = 0; index < caret; index++)
        {
            if (text[index] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }

        CaretStatusText.Text = $"Ln {line}, Col {column}";
    }

    private void UpdateSignaturePanel()
    {
        if (CompletionList.SelectedItem is not MappingFunctionCompletionItem item)
        {
            SignatureText.Text = string.Empty;
            SignaturePanel.IsVisible = false;
            return;
        }

        SignatureText.Text = item.Description;
        SignaturePanel.IsVisible = true;
    }
}
