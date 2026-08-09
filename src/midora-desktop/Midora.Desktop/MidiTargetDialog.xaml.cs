using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Midora.Domain;

namespace Midora.Desktop;

public partial class MidiTargetDialog : Window
{
    public MidiTargetDialog(string title = "Select MIDI Target")
    {
        InitializeComponent();
        Title = title;
        KindBox.ItemsSource = Enum.GetValues<MidiValueKind>();
        KindBox.SelectedItem = MidiValueKind.ControlChange;
    }

    public MidiValueTarget? Result { get; private set; }

    private void OnKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NumberBox is null) return;
        NumberBox.IsEnabled = KindBox.SelectedItem is MidiValueKind.ControlChange
            or MidiValueKind.RegisteredParameter
            or MidiValueKind.NonRegisteredParameter;
        if (!NumberBox.IsEnabled) NumberBox.Text = "0";
    }

    private void OnSelectClick(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        if (KindBox.SelectedItem is not MidiValueKind kind
            || !int.TryParse(NumberBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
        {
            ValidationText.Text = "Select a target kind and enter a base-10 number.";
            return;
        }
        bool valid = kind switch
        {
            MidiValueKind.ControlChange => number is >= 0 and <= 119 and not 91 and not 93,
            MidiValueKind.RegisteredParameter or MidiValueKind.NonRegisteredParameter => number is >= 0 and <= 16_383,
            _ => number == 0
        };
        if (!valid)
        {
            ValidationText.Text = kind == MidiValueKind.ControlChange
                ? "Control Change number must be 0–119, excluding CC91 and CC93."
                : "RPN/NRPN number must be 0–16,383.";
            return;
        }
        Result = new(kind, number);
        DialogResult = true;
    }

    private void OnTitleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
