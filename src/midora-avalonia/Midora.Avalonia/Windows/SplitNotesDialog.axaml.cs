using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Midora.Avalonia.Windows;

/// <summary>
/// Avalonia port of <c>Midora.Desktop.SplitNotesDialog</c>. Numeric parsing, mode rows, the
/// validation result bar, the preset list and OK/Cancel semantics are ported; the expression
/// compiler (and the Avalonia BatchExpressionEditor, which is a Window here) is replaced by a
/// structural check over a monospace TextBox.
/// </summary>
public partial class SplitNotesDialog : Window
{
    // Mirrors NoteSplitPresetStore.MaximumAllowedCuts.
    public const int MaximumAllowedCuts = 16_777_216;

    private static readonly string[] ForbiddenExpressionTokens =
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

    private readonly ObservableCollection<NoteSplitPresetEntry> _presets;

    private bool _initialized;
    private bool _closing;

    public SplitNotesDialog()
    {
        InitializeComponent();
        _presets = NoteSplitPresetDialog.CreateDemoEntries();
        UpdateModeRows();
        _initialized = true;
        Opened += (_, _) => FixedLengthBox.Focus();
    }

    /// <summary>Accepted options; the caller reads them after the dialog closes.</summary>
    public SplitOptions? Options { get; private set; }

    public bool Confirmed { get; private set; }

    public sealed record SplitOptions(
        NoteSplitMode Mode,
        long FixedLengthTicks,
        int MaximumPieceCount,
        string Expression,
        int MaximumCuts);

    private NoteSplitMode SelectedMode => ModeBox.SelectedIndex switch
    {
        1 => NoteSplitMode.MaximumPieceCount,
        2 => NoteSplitMode.Expression,
        _ => NoteSplitMode.FixedPieceLength
    };

    private void OnModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        UpdateModeRows();
        ResetValidationStatus();
    }

    private void UpdateModeRows()
    {
        NoteSplitMode mode = SelectedMode;
        FixedLengthRow.IsVisible = mode == NoteSplitMode.FixedPieceLength;
        PieceCountRow.IsVisible = mode == NoteSplitMode.MaximumPieceCount;
        ExpressionRow.IsVisible = mode == NoteSplitMode.Expression;
        MaximumCutsRow.IsVisible = ExpressionRow.IsVisible;
    }

    private SplitOptions CompileAndValidate()
    {
        NoteSplitMode mode = SelectedMode;

        long fixedLength = 1;
        if (mode == NoteSplitMode.FixedPieceLength
            && (!long.TryParse(
                    FixedLengthBox.Text,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out fixedLength)
                || fixedLength < 1))
        {
            throw new ArgumentException("Piece Length must be a positive Int64 Tick value.");
        }

        int pieceCount = 2;
        if (mode == NoteSplitMode.MaximumPieceCount
            && (!int.TryParse(
                    PieceCountBox.Text,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out pieceCount)
                || pieceCount < 1))
        {
            throw new ArgumentException("Maximum Piece Count must be a positive Int32 value.");
        }

        int maximumCuts = 65_535;
        if (mode == NoteSplitMode.Expression)
        {
            if (!int.TryParse(
                    MaximumCutsBox.Text,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out maximumCuts)
                || maximumCuts is < 1 or > MaximumAllowedCuts)
            {
                throw new ArgumentException($"Maximum Cuts must be within 1–{MaximumAllowedCuts:N0}.");
            }

            // The WPF dialog compiles the expression through NoteSplitExpressionProgram.Compile.
            // The Avalonia demo keeps the same result bar and only runs a structural check.
            if (ValidateExpression(ExpressionBox.Text) is { } expressionError)
            {
                throw new ArgumentException(expressionError);
            }
        }

        return new SplitOptions(
            mode,
            fixedLength,
            pieceCount,
            ExpressionBox.Text ?? "=192",
            maximumCuts);
    }

    private static string? ValidateExpression(string? text)
    {
        string expression = (text ?? string.Empty).Trim();
        if (expression.Length == 0) return "Expression must not be empty.";
        if (!expression.StartsWith('=')) return "Expression must start with '='.";
        foreach (string token in ForbiddenExpressionTokens)
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

    private void OnValidateClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            _ = CompileAndValidate();
            SetValidationStatus("Validation succeeded.", ValidationStatus.Success);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            SetValidationStatus(exception.Message, ValidationStatus.Error);
        }
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e) => TryApply();

    private void OnExpressionKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        e.Handled = true;
        TryApply();
    }

    private void TryApply()
    {
        try
        {
            Options = CompileAndValidate();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            SetValidationStatus(exception.Message, ValidationStatus.Error);
            _ = MessageDialog.ShowAsync(
                this,
                exception.Message,
                "Split Notes",
                MessageDialogButtons.Ok,
                MessageDialogIcon.Error);
            return;
        }

        Confirmed = true;
        _closing = true;
        Close();
    }

    private async void OnHelpClick(object? sender, RoutedEventArgs e)
    {
        NoteSplitHelpDialog dialog = new();
        await dialog.ShowDialog(this);
    }

    private async void OnPresetsClick(object? sender, RoutedEventArgs e)
    {
        NoteSplitPresetDialog dialog = new(_presets);
        await dialog.ShowDialog(this);
        if (dialog.SelectedPreset is not NoteSplitPresetEntry preset) return;
        ApplyPreset(preset);
        SetValidationStatus(
            $"Preset '{preset.Name}' loaded. Validate or apply it to the selection.",
            ValidationStatus.Neutral);
    }

    private async void OnSavePresetClick(object? sender, RoutedEventArgs e)
    {
        SplitOptions options;
        try
        {
            options = CompileAndValidate();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            SetValidationStatus(exception.Message, ValidationStatus.Error);
            return;
        }

        string? name = await TextInputDialog.ShowAsync(
            this,
            "Save Note Split Preset",
            "Enter a unique preset name.",
            string.Empty,
            ValidatePresetName);
        if (name is null) return;

        name = name.Trim();
        _presets.Add(new NoteSplitPresetEntry(
            name,
            options.Mode,
            options.FixedLengthTicks,
            options.MaximumPieceCount,
            // WPF CapturePreset stored the default expression for non-expression modes.
            options.Mode == NoteSplitMode.Expression ? options.Expression : "=192",
            options.MaximumCuts));
        SetValidationStatus($"Preset '{name}' saved.", ValidationStatus.Neutral);
    }

    private string? ValidatePresetName(string value)
    {
        string name = (value ?? string.Empty).Trim();
        if (name.Length is < 1 or > 100 || name.EndsWith('.'))
        {
            return "Preset names must contain 1–100 filename-safe characters and cannot end with a period.";
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return "Preset names must contain 1–100 filename-safe characters and cannot end with a period.";
        }

        if (_presets.Any(preset => string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return $"A Note Split preset named '{name}' already exists.";
        }

        return null;
    }

    private void ApplyPreset(NoteSplitPresetEntry preset)
    {
        ModeBox.SelectedIndex = preset.Mode switch
        {
            NoteSplitMode.FixedPieceLength => 0,
            NoteSplitMode.MaximumPieceCount => 1,
            NoteSplitMode.Expression => 2,
            _ => 0
        };
        FixedLengthBox.Text = preset.FixedLengthTicks.ToString(CultureInfo.InvariantCulture);
        PieceCountBox.Text = preset.MaximumPieceCount.ToString(CultureInfo.InvariantCulture);
        ExpressionBox.Text = preset.Expression;
        MaximumCutsBox.Text = preset.MaximumCuts.ToString(CultureInfo.InvariantCulture);
        UpdateModeRows();
    }

    private void OnInputChanged(object? sender, TextChangedEventArgs e) => ResetValidationStatus();

    private void ResetValidationStatus()
    {
        if (!_initialized || ValidationText is null) return;
        SetValidationStatus("Not validated.", ValidationStatus.Neutral);
    }

    private void SetValidationStatus(string message, ValidationStatus status)
    {
        ValidationText.Text = message;
        string brushKey = status switch
        {
            ValidationStatus.Success => "Brush.Success",
            ValidationStatus.Error => "Brush.Red.Hover",
            _ => "Brush.Text.Tertiary"
        };
        if (this.TryFindResource(brushKey, out object? value) && value is IBrush brush)
        {
            ValidationText.Foreground = brush;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (_closing) return;
        _closing = true;
        Options = null;
        Confirmed = false;
        Close();
    }

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        OnCancelClick(sender, e);
    }

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private enum ValidationStatus
    {
        Neutral,
        Success,
        Error
    }
}
