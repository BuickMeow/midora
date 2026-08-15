using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Midora.Domain;

namespace Midora.Desktop;

public partial class MidiTargetDialog : Window
{
    public MidiTargetDialog(
        string title = "Select MIDI Target",
        MidiValueTarget? selectedTarget = null)
    {
        InitializeComponent();
        Title = title;
        DialogTitleText.Text = title;
        KindBox.ItemsSource = Enum.GetValues<MidiValueKind>();
        ControllerBox.ItemsSource = MidiControlChangeCatalog.EditableControllers;
        MidiValueTarget initial = selectedTarget ?? MidiValueTarget.ControlChange(0);
        KindBox.SelectedItem = initial.Kind;
        if (initial.Kind == MidiValueKind.ControlChange)
        {
            ControllerBox.SelectedItem = MidiControlChangeCatalog.EditableControllers
                .FirstOrDefault(value => value.Number == initial.Number);
            if (ControllerBox.SelectedItem is null) ControllerBox.SelectedIndex = 0;
        }
        else
        {
            ControllerBox.SelectedIndex = 0;
        }
        NumberBox.Text = initial.Number.ToString(CultureInfo.InvariantCulture);
    }

    public MidiValueTarget? Result { get; private set; }

    private void OnKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NumberBox is null) return;
        bool controller = KindBox.SelectedItem is MidiValueKind.ControlChange;
        bool numberedParameter = KindBox.SelectedItem is MidiValueKind.RegisteredParameter
            or MidiValueKind.NonRegisteredParameter;
        ControllerBox.Visibility = controller ? Visibility.Visible : Visibility.Collapsed;
        NumberBox.Visibility = numberedParameter ? Visibility.Visible : Visibility.Collapsed;
        NumberLabel.Visibility = controller || numberedParameter ? Visibility.Visible : Visibility.Collapsed;
        NumberLabel.Text = controller ? "CONTROLLER" : "PARAMETER NUMBER";
        if (!numberedParameter) NumberBox.Text = "0";
    }

    private void OnSelectClick(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        if (KindBox.SelectedItem is not MidiValueKind kind)
        {
            ValidationText.Text = "Select a target kind.";
            return;
        }
        int number;
        if (kind == MidiValueKind.ControlChange)
        {
            if (ControllerBox.SelectedItem is not MidiControlChangeInfo controller)
            {
                ValidationText.Text = "Select a supported Control Change.";
                return;
            }
            number = controller.Number;
        }
        else if (kind is MidiValueKind.RegisteredParameter or MidiValueKind.NonRegisteredParameter)
        {
            if (!int.TryParse(NumberBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out number))
            {
                ValidationText.Text = "Enter a base-10 parameter number.";
                return;
            }
        }
        else
        {
            number = 0;
        }
        bool valid = kind switch
        {
            MidiValueKind.ControlChange => MidiControlChangeCatalog.IsEditable(number),
            MidiValueKind.RegisteredParameter or MidiValueKind.NonRegisteredParameter => number is >= 0 and <= 16_383,
            _ => number == 0
        };
        if (!valid)
        {
            ValidationText.Text = kind == MidiValueKind.ControlChange
                ? "Select a Control Change supported by Midora's BASSMIDI profile."
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
