using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Midora.Compiler;

namespace Midora.Desktop;

public partial class BatchEditDialog : Window
{
    private const string HelpText = """
        Leave a field blank to keep its original value.

        Direct value
          Enter one value in the legal range, for example: 96

        Single-step operations
          n%   set the result to n percent of the original value
          *n   multiply the original value by n
          /n   divide the original value by n; n cannot be zero
          +n   add n to the original value
          -n   subtract n from the original value
        Here n is any non-negative finite number.
        Because -n means subtraction, enter a direct negative target as an expression,
        for example: =-100

        C# expression
          Start the field with = and enter one C# numeric expression without a semicolon.
          The expression result is double. System.Math methods are available both as Math.X(...)
          and directly, for example =Clamp(v0 * 1.25, 1, 127).

        Variables
          v0 / v1   Velocity before / after calculation
          p0 / p1   Point Value before / after calculation
          k0 / k1   Key Number before / after calculation
          g0 / g1   Gate before / after calculation
          t0 / t1   Tick before / after calculation
          tr        Tick relative to the earliest selected object before calculation

        A result variable may depend on another available result variable. Circular dependencies,
        including a field depending on itself, are rejected during validation. Expressions allow
        bounded numeric operations, conditionals, comparisons, and System.Math only.

        Result handling
          Values are rounded away from zero where the target is integral.
          Velocity and Point Value results are clamped to their target ranges.
          Gate is clamped to at least 1 tick.
          A note whose Key Number is outside 0–127 is removed.
          A Segment Note or parameter point before the exposed left edge expands the Segment left
          while preserving every hidden object's Project position. If that would cross Project
          Tick 0, the calculated object is removed. A SubVoice object below Tick 0 is removed.
          Evaluation of the entire batch is limited to 10 seconds.
        """;

    private readonly BatchEditPresetKind _presetKind;
    private readonly double _pointMinimum;
    private readonly double _pointMaximum;
    private readonly BatchEditPresetStore _presetStore = new();
    private bool _sanitizing;

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
        Loaded += (_, _) => (note ? VelocityBox : PointValueBox).Focus();
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
            ValidationText.Text = "Validation succeeded.";
        }
        catch (Exception exception)
        {
            ValidationText.Text = exception.Message;
        }
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Program?.Dispose();
            Program = CompileAndValidate();
            DialogResult = true;
        }
        catch (Exception exception)
        {
            ValidationText.Text = exception.Message;
            _ = MessageDialog.Show(
                this,
                exception.Message,
                "Batch Edit Selection",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnHelpClick(object sender, RoutedEventArgs e) =>
        _ = MessageDialog.Show(
            this,
            HelpText,
            "Batch Edit Help",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

    private void OnPresetsClick(object sender, RoutedEventArgs e)
    {
        BatchEditPresetDialog dialog = new(_presetStore, _presetKind) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedPreset is not BatchEditPreset preset) return;
        ApplyPreset(preset);
        ValidationText.Text = $"Preset '{preset.Name}' loaded. Validate or apply it to the selection.";
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
            ValidationText.Text = $"Preset '{saved.Name}' saved.";
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

    private void OnFormulaTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_sanitizing || sender is not TextBox textBox) return;
        string sanitized = textBox.Text.Replace("\r", " ").Replace("\n", " ");
        if (!string.Equals(sanitized, textBox.Text, StringComparison.Ordinal))
        {
            int caret = Math.Min(textBox.CaretIndex, sanitized.Length);
            _sanitizing = true;
            textBox.Text = sanitized;
            textBox.CaretIndex = caret;
            _sanitizing = false;
        }
        ValidationText.Text = "Not validated.";
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
}
