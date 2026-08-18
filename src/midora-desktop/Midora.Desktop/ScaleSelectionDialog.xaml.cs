using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Midora.Application;

namespace Midora.Desktop;

public partial class ScaleSelectionDialog : Window, INotifyPropertyChanged
{
    private bool _synchronizing;
    private string _adjustedLengthText;
    private string _factorText = "1.000";

    public ScaleSelectionDialog(long currentLength, bool showSegmentScope)
    {
        if (currentLength < 0) throw new ArgumentOutOfRangeException(nameof(currentLength));
        CurrentLength = currentLength;
        _adjustedLengthText = currentLength.ToString(CultureInfo.InvariantCulture);
        InitializeComponent();
        DataContext = this;
        ScopeLabel.Visibility = showSegmentScope ? Visibility.Visible : Visibility.Collapsed;
        ScopeBox.Visibility = showSegmentScope ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => AdjustedLengthBox.Focus();
    }

    public long CurrentLength { get; }
    public string AdjustedLengthText
    {
        get => _adjustedLengthText;
        set => Set(ref _adjustedLengthText, value ?? string.Empty);
    }
    public string FactorText
    {
        get => _factorText;
        set => Set(ref _factorText, value ?? string.Empty);
    }
    public double ScaleFactor { get; private set; } = 1;
    public SegmentSelectionTransformScope Scope => ScopeBox.SelectedIndex == 1
        ? SegmentSelectionTransformScope.ExposedContentAndSegments
        : SegmentSelectionTransformScope.ExposedContentOnly;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnAdjustedLengthTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_synchronizing || CurrentLength <= 0) return;
        if (!long.TryParse(
                AdjustedLengthText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long adjusted)
            || adjusted <= 0)
        {
            return;
        }
        _synchronizing = true;
        FactorModeBox.SelectedIndex = 0;
        FactorText = (adjusted / (double)CurrentLength).ToString("0.000", CultureInfo.InvariantCulture);
        _synchronizing = false;
    }

    private void OnFactorChanged(object sender, EventArgs e)
    {
        if (_synchronizing || CurrentLength <= 0) return;
        if (!double.TryParse(
                FactorText,
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
        AdjustedLengthText = Math.Round(
            adjustedValue,
            MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
        _synchronizing = false;
    }

    private void OnAcceptClick(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        if (CurrentLength <= 0)
        {
            ShowValidation("A zero-span selection cannot be scaled.");
            return;
        }
        if (!long.TryParse(
                AdjustedLengthText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long adjusted)
            || adjusted <= 0)
        {
            ShowValidation("Adjusted Length must be a positive integer.");
            return;
        }
        if (!double.TryParse(
                FactorText,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double factor)
            || !double.IsFinite(factor)
            || factor <= 0)
        {
            ShowValidation("Factor must be a finite positive number.");
            return;
        }
        ScaleFactor = FactorModeBox.SelectedIndex == 1 ? 1 / factor : factor;
        if (!double.IsFinite(ScaleFactor) || ScaleFactor <= 0)
        {
            ShowValidation("The effective scale factor is invalid.");
            return;
        }
        double expectedValue = CurrentLength * ScaleFactor;
        if (!double.IsFinite(expectedValue) || expectedValue < 1 || expectedValue > long.MaxValue)
        {
            ShowValidation("The scaled length is outside the supported Tick range.");
            return;
        }
        long expected = checked((long)Math.Round(
            expectedValue,
            MidpointRounding.AwayFromZero));
        if (expected != adjusted)
        {
            ScaleFactor = adjusted / (double)CurrentLength;
        }
        DialogResult = true;
    }

    private void ShowValidation(string message)
    {
        ValidationText.Text = message;
        _ = MessageDialog.Show(
            this,
            message,
            "Scale Selection",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Set(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (string.Equals(field, value, StringComparison.Ordinal)) return;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
    }
}
