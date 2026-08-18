using System.Windows;
using System.Windows.Input;

namespace Midora.Desktop;

public sealed record TransposeIntervalItem(int Semitones, string Display);

public partial class TransposeSelectionDialog : Window
{
    private static readonly string[] PositiveNames =
    [
        "Perfect Unison", "Minor Second", "Major Second", "Minor Third",
        "Major Third", "Perfect Fourth", "Tritone", "Perfect Fifth",
        "Minor Sixth", "Major Sixth", "Minor Seventh", "Major Seventh", "Perfect Octave"
    ];

    public TransposeSelectionDialog()
    {
        Items = Enumerable.Range(-12, 25)
            .Reverse()
            .Select(value => new TransposeIntervalItem(
                value,
                $"{(value > 0 ? $"+{value}" : value.ToString())}  {PositiveNames[Math.Abs(value)]}"))
            .ToArray();
        InitializeComponent();
        DataContext = this;
        Loaded += (_, _) =>
        {
            IntervalList.SelectedValue = 0;
            IntervalList.ScrollIntoView(IntervalList.SelectedItem);
            IntervalList.Focus();
        };
    }

    public IReadOnlyList<TransposeIntervalItem> Items { get; }
    public int Semitones { get; private set; }

    private void OnAcceptClick(object sender, RoutedEventArgs e)
    {
        if (IntervalList.SelectedValue is not int value) return;
        Semitones = value;
        DialogResult = true;
    }
    private void OnListDoubleClick(object sender, MouseButtonEventArgs e) => OnAcceptClick(sender, e);
    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
