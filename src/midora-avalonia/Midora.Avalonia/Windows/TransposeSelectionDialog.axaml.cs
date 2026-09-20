using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Midora.Avalonia.Windows;

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
                $"{(value > 0 ? $"+{value}" : value.ToString(CultureInfo.InvariantCulture))}  {PositiveNames[Math.Abs(value)]}"))
            .ToArray();
        InitializeComponent();
        IntervalList.ItemsSource = Items;
        Opened += (_, _) =>
        {
            IntervalList.SelectedItem = Items.First(item => item.Semitones == 0);
            if (IntervalList.SelectedItem is { } selected)
            {
                IntervalList.ScrollIntoView(selected);
            }
            IntervalList.Focus();
        };
    }

    public IReadOnlyList<TransposeIntervalItem> Items { get; }

    /// <summary>
    /// Result semantics: a non-null value means Apply was confirmed. Avalonia has no
    /// Window.DialogResult, so <c>Close()</c> plus this property carries the outcome.
    /// </summary>
    public int? Semitones { get; private set; }

    private void OnAcceptClick(object? sender, RoutedEventArgs e)
    {
        if (IntervalList.SelectedItem is not TransposeIntervalItem item) return;
        Semitones = item.Semitones;
        Close();
    }

    private void OnListDoubleTapped(object? sender, TappedEventArgs e) => OnAcceptClick(sender, e);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
