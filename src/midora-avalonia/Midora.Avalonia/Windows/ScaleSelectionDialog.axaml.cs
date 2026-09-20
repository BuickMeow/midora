using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Midora.Avalonia.Windows;

/// <summary>
/// Placeholder for the Midora.Application segmentation scope enum; only the value is
/// consumed by the dialog result.
/// </summary>
public enum SegmentSelectionTransformScope
{
    ExposedContentOnly,
    ExposedContentAndSegments
}

public partial class ScaleSelectionDialog : Window
{
    private bool _synchronizing;

    public ScaleSelectionDialog()
        : this(1920, false)
    {
    }

    public ScaleSelectionDialog(long currentLength, bool showSegmentScope)
    {
        if (currentLength < 0) throw new ArgumentOutOfRangeException(nameof(currentLength));
        CurrentLength = currentLength;
        // XAML Text defaults raise TextChanged while later controls are still unassigned.
        _synchronizing = true;
        InitializeComponent();
        CurrentLengthBox.Text = currentLength.ToString(CultureInfo.InvariantCulture);
        AdjustedLengthBox.Text = currentLength.ToString(CultureInfo.InvariantCulture);
        _synchronizing = false;
        ScopeLabel.IsVisible = showSegmentScope;
        ScopeBox.IsVisible = showSegmentScope;
        Opened += (_, _) => AdjustedLengthBox.Focus();
    }

    public long CurrentLength { get; }

    /// <summary>
    /// Result semantics: <see cref="Accepted"/> true means Apply was confirmed. Avalonia
    /// has no Window.DialogResult, so <c>Close()</c> plus <see cref="Accepted"/> and
    /// <see cref="ScaleFactor"/> carry the outcome.
    /// </summary>
    public double ScaleFactor { get; private set; } = 1;

    public bool Accepted { get; private set; }

    public SegmentSelectionTransformScope Scope => ScopeBox.SelectedIndex == 1
        ? SegmentSelectionTransformScope.ExposedContentAndSegments
        : SegmentSelectionTransformScope.ExposedContentOnly;

    private void OnAdjustedLengthTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_synchronizing || CurrentLength <= 0) return;
        if (!long.TryParse(
                AdjustedLengthBox.Text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long adjusted)
            || adjusted <= 0)
        {
            return;
        }
        _synchronizing = true;
        FactorModeBox.SelectedIndex = 0;
        FactorBox.Text = (adjusted / (double)CurrentLength).ToString("0.000", CultureInfo.InvariantCulture);
        _synchronizing = false;
    }

    private void OnFactorChanged(object? sender, TextChangedEventArgs e) => RecomputeFactor();

    private void OnFactorModeChanged(object? sender, SelectionChangedEventArgs e) => RecomputeFactor();

    private void RecomputeFactor()
    {
        if (_synchronizing || CurrentLength <= 0) return;
        if (!double.TryParse(
                FactorBox.Text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double factor)
            || !double.IsFinite(factor)
            || factor <= 0)
        {
            return;
        }
        double effective = FactorModeBox.SelectedIndex == 1 ? 1 / factor : factor;
        double adjustedValue = CurrentLength * effective;
        if (!double.IsFinite(adjustedValue) || adjustedValue < 1 || adjustedValue > long.MaxValue) return;
        _synchronizing = true;
        AdjustedLengthBox.Text = Math.Round(
            adjustedValue,
            MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
        _synchronizing = false;
    }

    private void OnAcceptClick(object? sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        if (CurrentLength <= 0)
        {
            ShowValidation("A zero-span selection cannot be scaled.");
            return;
        }
        if (!long.TryParse(
                AdjustedLengthBox.Text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long adjusted)
            || adjusted <= 0)
        {
            ShowValidation("Adjusted Length must be a positive integer.");
            return;
        }
        if (!double.TryParse(
                FactorBox.Text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double factor)
            || !double.IsFinite(factor)
            || factor <= 0)
        {
            ShowValidation("Factor must be a finite positive number.");
            return;
        }
        double scaleFactor = FactorModeBox.SelectedIndex == 1 ? 1 / factor : factor;
        if (!double.IsFinite(scaleFactor) || scaleFactor <= 0)
        {
            ShowValidation("The effective scale factor is invalid.");
            return;
        }
        double expectedValue = CurrentLength * scaleFactor;
        if (!double.IsFinite(expectedValue) || expectedValue < 1 || expectedValue > long.MaxValue)
        {
            ShowValidation("The scaled length is outside the supported Tick range.");
            return;
        }
        long expected;
        try
        {
            expected = checked((long)Math.Round(expectedValue, MidpointRounding.AwayFromZero));
        }
        catch (OverflowException)
        {
            ShowValidation("The scaled length is outside the supported Tick range.");
            return;
        }
        if (expected != adjusted)
        {
            scaleFactor = adjusted / (double)CurrentLength;
        }
        ScaleFactor = scaleFactor;
        Accepted = true;
        Close();
    }

    private void ShowValidation(string message) => ValidationText.Text = message;

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
