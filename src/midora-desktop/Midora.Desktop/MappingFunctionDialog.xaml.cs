using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Midora.Compiler;
using Midora.Mapping.Contract.V2;

namespace Midora.Desktop;

public sealed record MappingFunctionDialogSubmission(
    string Name,
    string Expression,
    IReadOnlyList<string> ReferencedContextFields);

public partial class MappingFunctionDialog : Window
{
    private readonly Func<MappingFunctionDialogSubmission, string?> _submit;
    private readonly CSharpMappingDraftCompiler _compiler = new();
    private CSharpMappingDraftCompilationResult? _lastValidation;
    private string? _lastValidatedExpression;

    public MappingFunctionDialog(
        string title,
        string name,
        string expression,
        Func<MappingFunctionDialogSubmission, string?> submit)
    {
        ArgumentNullException.ThrowIfNull(submit);
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        NameBox.Text = name ?? string.Empty;
        ExpressionEditor.Text = expression ?? string.Empty;
        _submit = submit;
        Loaded += (_, _) => ExpressionEditor.FocusEditor();
        Closed += (_, _) => _compiler.Dispose();
    }

    private void OnValidateClick(object sender, RoutedEventArgs e) => ValidateExpression();

    private void OnOkClick(object sender, RoutedEventArgs e) => TrySubmit();

    private void OnExpressionCommitRequested(object? sender, EventArgs e) => TrySubmit();

    private void TrySubmit()
    {
        string name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            SetValidation("Name must not be empty.", "Brush.Red.Hover");
            _ = NameBox.Focus();
            return;
        }

        CSharpMappingDraftCompilationResult validation = ValidateExpression();
        if (!validation.Succeeded)
        {
            ExpressionEditor.FocusEditor();
            return;
        }

        string? error;
        try
        {
            error = _submit(new(
                name,
                ExpressionEditor.Text,
                validation.ReferencedContextFields));
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }
        if (!string.IsNullOrWhiteSpace(error))
        {
            SetValidation(error, "Brush.Red.Hover");
            return;
        }

        DialogResult = true;
    }

    private CSharpMappingDraftCompilationResult ValidateExpression()
    {
        string expression = ExpressionEditor.Text;
        if (_lastValidation is not null
            && string.Equals(_lastValidatedExpression, expression, StringComparison.Ordinal))
        {
            return _lastValidation;
        }

        CSharpMappingDraftCompilationResult result = _compiler.Compile(
            MappingExpressionAbiV3.Version,
            expression);
        _lastValidation = result;
        _lastValidatedExpression = expression;
        SetValidation(
            result.Succeeded
                ? result.ReferencedContextFields.Count == 0
                    ? "Expression is valid. No context fields are referenced."
                    : $"Expression is valid. Context: {string.Join(", ", result.ReferencedContextFields.Select(value => $"context.{value}"))}."
                : result.ErrorMessage ?? "Expression validation failed.",
            result.Succeeded ? "Brush.Success" : "Brush.Red.Hover");
        return result;
    }

    private void OnInputChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ResetValidation();

    private void OnExpressionChanged(object? sender, EventArgs e) => ResetValidation();

    private void ResetValidation()
    {
        _lastValidation = null;
        _lastValidatedExpression = null;
        SetValidation("Not validated.", "Brush.Text.Tertiary");
    }

    private void SetValidation(string message, string brushKey)
    {
        ValidationText.Text = message;
        ValidationText.Foreground = TryFindResource(brushKey) as Brush
            ?? TryFindResource("Brush.Text.Tertiary") as Brush;
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        MappingFunctionHelpDialog dialog = new() { Owner = this };
        _ = dialog.ShowDialog();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
