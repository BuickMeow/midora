using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Midora.Compiler;

namespace Midora.Desktop;

public partial class BatchEditDialog : Window
{
    private readonly BatchEditPresetKind _presetKind;
    private readonly double _pointMinimum;
    private readonly double _pointMaximum;
    private readonly BatchEditPresetStore _presetStore = new();

    public BatchEditDialog(
        BatchEditPresetKind presetKind,
        double pointMinimum = 0,
        double pointMaximum = 127)
    {
        if (!double.IsFinite(pointMinimum)
            || !double.IsFinite(pointMaximum)
            || pointMaximum < pointMinimum)
        {
            throw new ArgumentOutOfRangeException(nameof(pointMinimum));
        }
        _presetKind = presetKind;
        _pointMinimum = pointMinimum;
        _pointMaximum = pointMaximum;
        InitializeComponent();
        bool note = presetKind == BatchEditPresetKind.Note;
        VelocityRow.Visibility = note ? Visibility.Visible : Visibility.Collapsed;
        KeyNumberRow.Visibility = note ? Visibility.Visible : Visibility.Collapsed;
        GateRow.Visibility = note ? Visibility.Visible : Visibility.Collapsed;
        PointValueRow.Visibility = note ? Visibility.Collapsed : Visibility.Visible;
        VelocityBox.ConfigureContext(presetKind, BatchEditField.Velocity);
        PointValueBox.ConfigureContext(presetKind, BatchEditField.PointValue);
        KeyNumberBox.ConfigureContext(presetKind, BatchEditField.KeyNumber);
        GateBox.ConfigureContext(presetKind, BatchEditField.Gate);
        TickBox.ConfigureContext(presetKind, BatchEditField.Tick);
        Loaded += (_, _) => (note ? VelocityBox : PointValueBox).FocusEditor();
    }

    public BatchEditExpressionProgram? Program { get; private set; }

    private IReadOnlyDictionary<BatchEditField, string?> CaptureExpressions() =>
        _presetKind == BatchEditPresetKind.Note
            ? new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.Velocity] = VelocityBox.Text,
                [BatchEditField.KeyNumber] = KeyNumberBox.Text,
                [BatchEditField.Gate] = GateBox.Text,
                [BatchEditField.Tick] = TickBox.Text
            }
            : new Dictionary<BatchEditField, string?>
            {
                [BatchEditField.PointValue] = PointValueBox.Text,
                [BatchEditField.Tick] = TickBox.Text
            };

    private BatchEditExpressionProgram CompileAndValidate()
    {
        BatchEditExpressionProgram program = BatchEditExpressionProgram.Compile(CaptureExpressions());
        try
        {
            if (_presetKind == BatchEditPresetKind.Note)
            {
                ValidateDirect(program, BatchEditField.Velocity, 1, 127, "Velocity");
                ValidateDirect(program, BatchEditField.KeyNumber, 0, 127, "Key Number");
                ValidateDirect(program, BatchEditField.Gate, 1, long.MaxValue, "Gate");
                ValidateDirect(program, BatchEditField.Tick, 0, long.MaxValue - 1d, "Tick");
            }
            else
            {
                ValidateDirect(program, BatchEditField.PointValue, _pointMinimum, _pointMaximum, "Point Value");
                ValidateDirect(program, BatchEditField.Tick, 0, long.MaxValue - 1d, "Tick");
            }
            return program;
        }
        catch
        {
            program.Dispose();
            throw;
        }
    }

    private static void ValidateDirect(
        BatchEditExpressionProgram program,
        BatchEditField field,
        double minimum,
        double maximum,
        string label)
    {
        if (program.GetFormulaKind(field) != BatchEditFormulaKind.DirectValue) return;
        double value = program.GetConstant(field)!.Value;
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                label,
                FormattableString.Invariant($"{label} must be within {minimum}–{maximum} when entered as a direct value."));
        }
    }

    private void OnValidateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            using BatchEditExpressionProgram program = CompileAndValidate();
            SetValidationStatus("Validation succeeded.", ValidationStatus.Success);
        }
        catch (Exception exception)
        {
            SetValidationStatus(exception.Message, ValidationStatus.Error);
        }
    }

    private void OnApplyClick(object sender, RoutedEventArgs e) => TryApply();

    private void OnExpressionCommitRequested(object? sender, EventArgs e) => TryApply();

    private void TryApply()
    {
        try
        {
            Program?.Dispose();
            Program = CompileAndValidate();
            DialogResult = true;
        }
        catch (Exception exception)
        {
            SetValidationStatus(exception.Message, ValidationStatus.Error);
            _ = MessageDialog.Show(
                this,
                exception.Message,
                "Batch Edit Selection",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        BatchEditHelpDialog dialog = new() { Owner = this };
        _ = dialog.ShowDialog();
    }

    private void OnPresetsClick(object sender, RoutedEventArgs e)
    {
        BatchEditPresetDialog dialog = new(_presetStore, _presetKind) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedPreset is not BatchEditPreset preset) return;
        ApplyPreset(preset);
        SetValidationStatus(
            $"Preset '{preset.Name}' loaded. Validate or apply it to the selection.",
            ValidationStatus.Neutral);
    }

    private void OnSavePresetClick(object sender, RoutedEventArgs e)
    {
        TextInputDialog nameDialog = new(
            "Save Batch Edit Preset",
            "Enter a unique preset name.",
            string.Empty)
        {
            Owner = this
        };
        if (nameDialog.ShowDialog() != true) return;
        try
        {
            BatchEditPresetInfo saved = _presetStore.Save(new(
                1,
                nameDialog.Value,
                _presetKind,
                VelocityBox.Text,
                PointValueBox.Text,
                KeyNumberBox.Text,
                GateBox.Text,
                TickBox.Text));
            SetValidationStatus($"Preset '{saved.Name}' saved.", ValidationStatus.Neutral);
        }
        catch (Exception exception)
        {
            _ = MessageDialog.Show(
                this,
                exception.Message,
                "Save Batch Edit Preset",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ApplyPreset(BatchEditPreset preset)
    {
        VelocityBox.Text = preset.Velocity;
        PointValueBox.Text = preset.PointValue;
        KeyNumberBox.Text = preset.KeyNumber;
        GateBox.Text = preset.Gate;
        TickBox.Text = preset.Tick;
    }

    private void OnFormulaTextChanged(object? sender, EventArgs e)
    {
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
        ValidationText.Foreground = (Brush)FindResource(brushKey);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Program?.Dispose();
        Program = null;
        DialogResult = false;
    }

    private void OnTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private enum ValidationStatus
    {
        Neutral,
        Success,
        Error
    }
}
