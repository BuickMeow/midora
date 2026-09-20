using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Midora.Avalonia.Windows;

/// <summary>
/// Avalonia port of <c>Midora.Desktop.BatchEditDialog</c>. The WPF window hosted five
/// <c>BatchExpressionEditor</c> controls backed by Midora.Compiler's batch expression ABI v3; this
/// port keeps the same field layout, note/logical row switching, validation ranges, preset and help
/// flows, and replaces AvalonEdit with monospace <see cref="TextBox"/> editors inside the WPF
/// editor chrome. Expression compilation is not reproduced (see the validation rules below).
/// </summary>
public partial class BatchEditDialog : Window
{
    private readonly bool _noteKind;
    private readonly double _pointMinimum;
    private readonly double _pointMaximum;
    private readonly ObservableCollection<BatchEditPresetEntry> _presets;

    public BatchEditDialog()
        : this(noteKind: true, pointMinimum: 0, pointMaximum: 127)
    {
    }

    public BatchEditDialog(bool noteKind, double pointMinimum = 0, double pointMaximum = 127)
    {
        if (!double.IsFinite(pointMinimum)
            || !double.IsFinite(pointMaximum)
            || pointMaximum < pointMinimum)
        {
            throw new ArgumentOutOfRangeException(nameof(pointMinimum));
        }

        _noteKind = noteKind;
        _pointMinimum = pointMinimum;
        _pointMaximum = pointMaximum;
        _presets = BatchEditPresetDialog.CreateDemoEntries();
        InitializeComponent();

        VelocityRow.IsVisible = noteKind;
        KeyNumberRow.IsVisible = noteKind;
        GateRow.IsVisible = noteKind;
        PointValueRow.IsVisible = !noteKind;
        SetValidationStatus("Not validated.", ValidationStatus.Neutral);
        Opened += (_, _) => (noteKind ? VelocityBox : PointValueBox).Focus();
    }

    /// <summary>Avalonia has no DialogResult: callers read <see cref="Confirmed"/> after the dialog closes.</summary>
    public bool Confirmed { get; private set; }

    /// <summary>The captured formula text per field, available after a confirmed apply.</summary>
    public IReadOnlyDictionary<string, string>? Expressions { get; private set; }

    private void OnFormulaTextChanged(object? sender, TextChangedEventArgs e) =>
        SetValidationStatus("Not validated.", ValidationStatus.Neutral);

    private void OnFormulaKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            _ = TryApplyAsync();
        }
    }

    private void OnValidateClick(object? sender, RoutedEventArgs e)
    {
        string? error = ValidateAll();
        SetValidationStatus(
            error ?? "Validation succeeded.",
            error is null ? ValidationStatus.Success : ValidationStatus.Error);
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e) => _ = TryApplyAsync();

    private async Task TryApplyAsync()
    {
        string? error = ValidateAll();
        if (error is not null)
        {
            SetValidationStatus(error, ValidationStatus.Error);
            await MessageDialog.ShowAsync(
                this,
                error,
                "Batch Edit Selection",
                MessageDialogButtons.Ok,
                MessageDialogIcon.Error);
            return;
        }

        Expressions = CaptureExpressions();
        SetValidationStatus("Validation succeeded.", ValidationStatus.Success);
        Confirmed = true;
        Close();
    }

    private void OnHelpClick(object? sender, RoutedEventArgs e) =>
        _ = new BatchEditHelpDialog().ShowDialog(this);

    private async void OnPresetsClick(object? sender, RoutedEventArgs e)
    {
        BatchEditPresetDialog dialog = new(_presets, _noteKind);
        await dialog.ShowDialog(this);
        if (!dialog.Confirmed || dialog.SelectedPreset is not { } preset)
        {
            return;
        }

        ApplyPreset(preset);
        SetValidationStatus(
            $"Preset '{preset.Name}' loaded. Validate or apply it to the selection.",
            ValidationStatus.Neutral);
    }

    private async void OnSavePresetClick(object? sender, RoutedEventArgs e)
    {
        string? name = await TextInputDialog.ShowAsync(
            this,
            "Save Batch Edit Preset",
            "Enter a unique preset name.",
            string.Empty);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        string trimmed = name.Trim();
        if (_presets.Any(entry => string.Equals(entry.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            await MessageDialog.ShowAsync(
                this,
                $"A batch edit preset named '{trimmed}' already exists.",
                "Save Batch Edit Preset",
                MessageDialogButtons.Ok,
                MessageDialogIcon.Error);
            return;
        }

        BatchEditPresetEntry saved = new(
            trimmed,
            _noteKind,
            VelocityBox.Text ?? string.Empty,
            PointValueBox.Text ?? string.Empty,
            KeyNumberBox.Text ?? string.Empty,
            GateBox.Text ?? string.Empty,
            TickBox.Text ?? string.Empty);
        _presets.Add(saved);
        SetValidationStatus($"Preset '{saved.Name}' saved.", ValidationStatus.Neutral);
    }

    private void ApplyPreset(BatchEditPresetEntry preset)
    {
        VelocityBox.Text = preset.Velocity;
        PointValueBox.Text = preset.PointValue;
        KeyNumberBox.Text = preset.KeyNumber;
        GateBox.Text = preset.Gate;
        TickBox.Text = preset.Tick;
    }

    private IReadOnlyDictionary<string, string> CaptureExpressions() => _noteKind
        ? new Dictionary<string, string>
        {
            ["Velocity"] = VelocityBox.Text ?? string.Empty,
            ["KeyNumber"] = KeyNumberBox.Text ?? string.Empty,
            ["Gate"] = GateBox.Text ?? string.Empty,
            ["Tick"] = TickBox.Text ?? string.Empty
        }
        : new Dictionary<string, string>
        {
            ["PointValue"] = PointValueBox.Text ?? string.Empty,
            ["Tick"] = TickBox.Text ?? string.Empty
        };

    private string? ValidateAll()
    {
        string? error = _noteKind
            ? ValidateField(VelocityBox, "Velocity", 1, 127)
                ?? ValidateField(KeyNumberBox, "Key Number", 0, 127)
                ?? ValidateField(GateBox, "Gate", 1, long.MaxValue)
            : ValidateField(PointValueBox, "Point Value", _pointMinimum, _pointMaximum);
        return error ?? ValidateField(TickBox, "Tick", 0, long.MaxValue - 1d);
    }

    private static string? ValidateField(TextBox box, string label, double minimum, double maximum)
    {
        string text = (box.Text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text.StartsWith('='))
        {
            return text.Length > 1
                ? null
                : $"{label}: enter an expression after '='.";
        }

        bool operation = text[0] is '*' or '/' or '+' or '-' || text[^1] == '%';
        string operand = operation
            ? text[^1] == '%' ? text[..^1].Trim() : text[1..].Trim()
            : text;
        if (!double.TryParse(operand, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || !double.IsFinite(value))
        {
            return $"{label}: '{text}' is not a direct value, operation, or = expression.";
        }

        if (operation)
        {
            return null;
        }

        return value < minimum || value > maximum
            ? $"{label} must be within {minimum}–{maximum} when entered as a direct value."
            : null;
    }

    private void SetValidationStatus(string message, ValidationStatus status)
    {
        ValidationText.Text = message;
        ValidationText.Foreground = status switch
        {
            ValidationStatus.Success => ResolveBrush("Brush.Success", "#58C487"),
            ValidationStatus.Error => ResolveBrush("Brush.Red.Hover", "#F2555A"),
            _ => ResolveBrush("Brush.Text.Tertiary", "#747E8C")
        };
    }

    private IBrush ResolveBrush(string key, string fallback) =>
        this.TryFindResource(key, out object? value) && value is IBrush brush
            ? brush
            : Brush.Parse(fallback);

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private enum ValidationStatus
    {
        Neutral,
        Success,
        Error
    }
}
