using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Midora.Avalonia.Windows;

/// <summary>
/// Placeholders for the Midora.Application humanize field model; the dialog only needs the
/// mode and bounds to validate and echo the user's input.
/// </summary>
public enum TimelineHumanizeMode
{
    Disabled,
    Add,
    Multiply,
    Override
}

public sealed record TimelineHumanizeField(TimelineHumanizeMode Mode, double Minimum, double Maximum)
{
    public static TimelineHumanizeField Disabled { get; } = new(TimelineHumanizeMode.Disabled, 0, 0);

    public static TimelineHumanizeField Add(double minimum, double maximum) =>
        new(TimelineHumanizeMode.Add, minimum, maximum);

    public static TimelineHumanizeField Multiply(double minimum, double maximum) =>
        new(TimelineHumanizeMode.Multiply, minimum, maximum);

    public static TimelineHumanizeField Override(double minimum, double maximum) =>
        new(TimelineHumanizeMode.Override, minimum, maximum);
}

public sealed record TimelineHumanizeOptions(
    TimelineHumanizeField Tick,
    TimelineHumanizeField Gate,
    TimelineHumanizeField Velocity,
    ulong? Seed);

public partial class HumanizeSelectionDialog : Window
{
    private static readonly TimelineHumanizeMode[] Modes =
    [
        TimelineHumanizeMode.Disabled,
        TimelineHumanizeMode.Add,
        TimelineHumanizeMode.Multiply,
        TimelineHumanizeMode.Override
    ];

    public HumanizeSelectionDialog()
    {
        InitializeComponent();
        PopulateModes(TickModeBox);
        PopulateModes(GateModeBox);
        PopulateModes(VelocityModeBox);
        UpdateEnabledState();
    }

    /// <summary>
    /// Result semantics: a non-null value means Apply was confirmed. Avalonia has no
    /// Window.DialogResult, so <c>Close()</c> plus this property carries the outcome.
    /// </summary>
    public TimelineHumanizeOptions? Options { get; private set; }

    public static TimelineHumanizeField ParseField(
        TimelineHumanizeMode mode,
        string? minimumText,
        string? maximumText,
        string fieldName)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == TimelineHumanizeMode.Disabled) return TimelineHumanizeField.Disabled;
        if (mode == TimelineHumanizeMode.Multiply)
        {
            if (!double.TryParse(
                    minimumText,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double minimum)
                || !double.TryParse(
                    maximumText,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double maximum)
                || !double.IsFinite(minimum)
                || !double.IsFinite(maximum))
            {
                throw new ArgumentException($"{fieldName} Minimum and Maximum must be finite numbers.");
            }
            if (minimum > maximum)
                throw new ArgumentException($"{fieldName} Minimum cannot exceed Maximum.");
            if (minimum < 0)
                throw new ArgumentException($"{fieldName} Multiply factors cannot be negative.");
            return TimelineHumanizeField.Multiply(minimum, maximum);
        }

        if (!decimal.TryParse(
                minimumText,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out decimal integerMinimum)
            || !decimal.TryParse(
                maximumText,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out decimal integerMaximum)
            || integerMinimum != decimal.Truncate(integerMinimum)
            || integerMaximum != decimal.Truncate(integerMaximum)
            || integerMinimum < long.MinValue
            || integerMaximum > long.MaxValue)
        {
            throw new ArgumentException($"{fieldName} {mode} bounds must be Int64 integers.");
        }
        if (integerMinimum > integerMaximum)
            throw new ArgumentException($"{fieldName} Minimum cannot exceed Maximum.");

        long minimumValue = decimal.ToInt64(integerMinimum);
        long maximumValue = decimal.ToInt64(integerMaximum);
        return mode switch
        {
            TimelineHumanizeMode.Add => TimelineHumanizeField.Add(minimumValue, maximumValue),
            TimelineHumanizeMode.Override => TimelineHumanizeField.Override(minimumValue, maximumValue),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    private static void PopulateModes(ComboBox box)
    {
        box.ItemsSource = Modes.Select(static value => value.ToString()).ToArray();
        box.SelectedIndex = 0;
    }

    private void OnSeedModeChanged(object? sender, RoutedEventArgs e)
    {
        UpdateEnabledState();
        ClearValidation();
    }

    private void OnFieldModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateEnabledState();
        ClearValidation();
    }

    private void OnInputChanged(object? sender, TextChangedEventArgs e) => ClearValidation();

    private void ClearValidation()
    {
        if (!IsInitialized || ValidationText is null) return;
        ValidationText.Text = string.Empty;
    }

    private void UpdateEnabledState()
    {
        // Avalonia 12 raises SelectionChanged/IsCheckedChanged while the compiled XAML populator
        // is still running, before every named field has been assigned. Only touch the controls
        // once the whole tree exists.
        if (!IsInitialized
            || TickModeBox is null || TickMinimumBox is null || TickMaximumBox is null
            || GateModeBox is null || GateMinimumBox is null || GateMaximumBox is null
            || VelocityModeBox is null || VelocityMinimumBox is null || VelocityMaximumBox is null
            || SeedBox is null || ExplicitSeedButton is null)
        {
            return;
        }

        SeedBox.IsEnabled = ExplicitSeedButton.IsChecked == true;
        SetRangeEnabled(TickModeBox, TickMinimumBox, TickMaximumBox);
        SetRangeEnabled(GateModeBox, GateMinimumBox, GateMaximumBox);
        SetRangeEnabled(VelocityModeBox, VelocityMinimumBox, VelocityMaximumBox);
    }

    private static void SetRangeEnabled(ComboBox modeBox, TextBox minimumBox, TextBox maximumBox)
    {
        bool enabled = GetMode(modeBox) != TimelineHumanizeMode.Disabled;
        minimumBox.IsEnabled = enabled;
        maximumBox.IsEnabled = enabled;
    }

    private static TimelineHumanizeMode GetMode(ComboBox box) =>
        box.SelectedIndex is >= 0 and < 4
            ? Modes[box.SelectedIndex]
            : TimelineHumanizeMode.Disabled;

    private void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            ulong? seed = null;
            if (ExplicitSeedButton.IsChecked == true)
            {
                if (!ulong.TryParse(SeedBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong parsed))
                    throw new ArgumentException("Explicit Seed must be an unsigned 64-bit integer.");
                seed = parsed;
            }
            Options = new(
                ParseField(GetMode(TickModeBox), TickMinimumBox.Text, TickMaximumBox.Text, "Tick"),
                ParseField(GetMode(GateModeBox), GateMinimumBox.Text, GateMaximumBox.Text, "Gate"),
                ParseField(GetMode(VelocityModeBox), VelocityMinimumBox.Text, VelocityMaximumBox.Text, "Velocity"),
                seed);
            Close();
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
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
