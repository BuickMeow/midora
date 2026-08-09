using System.Globalization;
using System.Windows;
using System.Windows.Input;
using Midora.Domain;

namespace Midora.Desktop;

public partial class MidiStateEntryDialog : Window
{
    private static readonly MidiValueKind[] SupportedKinds =
    [
        MidiValueKind.ControlChange,
        MidiValueKind.RegisteredParameter,
        MidiValueKind.NonRegisteredParameter
    ];

    public MidiStateEntryDialog()
    {
        InitializeComponent();
        KindBox.ItemsSource = SupportedKinds;
        KindBox.SelectedIndex = 0;
    }

    public MidiValueTarget? Target { get; private set; }
    public int Value { get; private set; }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        if (KindBox.SelectedItem is not MidiValueKind kind
            || !int.TryParse(NumberBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
            || !int.TryParse(ValueBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            ValidationText.Text = "Number and Value must be base-10 integers.";
            return;
        }
        bool valid = kind switch
        {
            MidiValueKind.ControlChange => number is >= 0 and <= 119 and not 91 and not 93 && value is >= 0 and <= 127,
            MidiValueKind.RegisteredParameter or MidiValueKind.NonRegisteredParameter =>
                number is >= 0 and <= 16_383 && value is >= 0 and <= 16_383,
            _ => false
        };
        if (!valid)
        {
            ValidationText.Text = kind == MidiValueKind.ControlChange
                ? "CC number must be 0–119 except 91/93; Value must be 0–127."
                : "RPN/NRPN Number and Value must each be 0–16,383.";
            return;
        }
        Target = new MidiValueTarget(kind, number);
        Value = value;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnTitleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}
