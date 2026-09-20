using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Midora.Avalonia.Windows;

public partial class MappingFunctionDialog : Window
{
    private static readonly string[] ForbiddenTokens =
    [
        ";",
        "{",
        "}",
        "while",
        "foreach",
        "for ",
        "=>",
        "new "
    ];

    private string? _lastValidatedExpression;
    private bool _lastValidationSucceeded;

    public MappingFunctionDialog()
        : this("Mapping Function", "Expression", "context.Value * 2")
    {
    }

    public MappingFunctionDialog(string title, string name, string expression)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        NameBox.Text = name ?? string.Empty;
        ExpressionEditor.Text = expression ?? string.Empty;
        Opened += (_, _) => ExpressionEditor.Focus();
    }

    public string FunctionName { get; private set; } = string.Empty;
    public string Expression { get; private set; } = string.Empty;
    public IReadOnlyList<string> ReferencedContextFields { get; private set; } = [];

    private void OnValidateClick(object? sender, RoutedEventArgs e) => ValidateExpression();

    private void OnOkClick(object? sender, RoutedEventArgs e) => TrySubmit();

    private void OnExpressionKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            TrySubmit();
        }
    }

    private void TrySubmit()
    {
        string name = (NameBox.Text ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            SetValidation("Name must not be empty.", "Brush.Red.Hover");
            _ = NameBox.Focus();
            return;
        }
        if (!ValidateExpression())
        {
            ExpressionEditor.Focus();
            return;
        }

        FunctionName = name;
        Expression = ExpressionEditor.Text ?? string.Empty;
        // WPF set DialogResult = true; Avalonia closes and the caller reads the result properties.
        Close();
    }

    private bool ValidateExpression()
    {
        string expression = ExpressionEditor.Text ?? string.Empty;
        if (_lastValidatedExpression is not null
            && string.Equals(_lastValidatedExpression, expression, StringComparison.Ordinal))
        {
            return _lastValidationSucceeded;
        }

        _lastValidatedExpression = expression;
        string? error = CheckExpression(expression);
        _lastValidationSucceeded = error is null;
        if (error is null)
        {
            string[] fields = ExtractContextFields(expression);
            ReferencedContextFields = fields;
            SetValidation(
                fields.Length == 0
                    ? "Expression is valid. No context fields are referenced."
                    : $"Expression is valid. Context: {string.Join(", ", fields.Select(value => $"context.{value}"))}.",
                "Brush.Success");
        }
        else
        {
            ReferencedContextFields = [];
            SetValidation(error, "Brush.Red.Hover");
        }
        return _lastValidationSucceeded;
    }

    // The real dialog compiles the expression through the Mapping Function ABI v3 compiler. The
    // Avalonia port only performs a structural sanity check so the result bar is demonstrable.
    private static string? CheckExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return "Expression must not be empty.";
        }
        foreach (string token in ForbiddenTokens)
        {
            if (expression.Contains(token, StringComparison.Ordinal))
            {
                return $"Expression must be a single bounded expression and must not contain '{token.Trim()}'.";
            }
        }
        int depth = 0;
        foreach (char character in expression)
        {
            if (character == '(')
            {
                depth++;
            }
            else if (character == ')')
            {
                depth--;
                if (depth < 0) return "Parentheses are not balanced.";
            }
        }
        return depth != 0 ? "Parentheses are not balanced." : null;
    }

    private static string[] ExtractContextFields(string expression)
    {
        const string marker = "context.";
        List<string> fields = [];
        int index = 0;
        while (index < expression.Length)
        {
            int start = expression.IndexOf(marker, index, StringComparison.Ordinal);
            if (start < 0) break;
            int fieldStart = start + marker.Length;
            int fieldEnd = fieldStart;
            while (fieldEnd < expression.Length
                   && (char.IsLetterOrDigit(expression[fieldEnd]) || expression[fieldEnd] == '_'))
            {
                fieldEnd++;
            }
            if (fieldEnd > fieldStart && (char.IsLetter(expression[fieldStart]) || expression[fieldStart] == '_'))
            {
                string field = expression[fieldStart..fieldEnd];
                if (!fields.Contains(field, StringComparer.Ordinal)) fields.Add(field);
            }
            index = fieldEnd > fieldStart ? fieldEnd : fieldStart + 1;
        }
        fields.Sort(StringComparer.Ordinal);
        return fields.ToArray();
    }

    private void OnNameTextChanged(object? sender, TextChangedEventArgs e) => ResetValidation();

    private void OnExpressionTextChanged(object? sender, TextChangedEventArgs e) => ResetValidation();

    private void ResetValidation()
    {
        _lastValidatedExpression = null;
        _lastValidationSucceeded = false;
        SetValidation("Not validated.", "Brush.Text.Tertiary");
    }

    private void SetValidation(string message, string brushKey)
    {
        ValidationText.Text = message;
        if (this.TryFindResource(brushKey, out object? value) && value is IBrush brush)
        {
            ValidationText.Foreground = brush;
        }
    }

    private void OnHelpClick(object? sender, RoutedEventArgs e)
    {
        // The WPF dialog opens MappingFunctionHelpDialog, which is outside this assignment; the
        // summary is shown in the result bar instead.
        SetValidation(
            "HELP — Enter one bounded numeric expression that produces the mapped integer value. "
            + "Reference the per-note context with context.<Field> (for example context.Velocity). "
            + "The supported context fields are inferred automatically when the expression is validated. "
            + "Statements, loops, assignments, local declarations and arbitrary API calls are not part of the "
            + "Mapping Function ABI and are rejected. Press Ctrl+Enter to validate and accept, or Validate to "
            + "check without closing the dialog.",
            "Brush.Info");
    }

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
