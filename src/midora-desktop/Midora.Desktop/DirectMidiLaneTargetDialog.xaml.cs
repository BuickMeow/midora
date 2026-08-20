using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Midora.Domain;

namespace Midora.Desktop;

public partial class DirectMidiLaneTargetDialog : Window
{
    private static readonly DirectMidiLaneKindOption[] Kinds =
    [
        new(DirectMidiChannelEventKind.ControlChange, "Control Change"),
        new(DirectMidiChannelEventKind.PolyphonicKeyPressure, "Polyphonic Key Pressure"),
        new(DirectMidiChannelEventKind.ProgramChange, "Program Change"),
        new(DirectMidiChannelEventKind.ChannelPressure, "Channel Pressure"),
        new(DirectMidiChannelEventKind.PitchBend, "Pitch Bend"),
        new(DirectMidiChannelEventKind.NoteOn, "Raw Note On"),
        new(DirectMidiChannelEventKind.NoteOff, "Raw Note Off")
    ];

    public DirectMidiLaneTargetDialog()
    {
        InitializeComponent();
        KindBox.ItemsSource = Kinds;
        ControllerBox.ItemsSource = Enumerable.Range(0, 128)
            .Select(static number => new DirectMidiControllerOption(
                number,
                MidiControlChangeCatalog.Format(number)))
            .ToArray();
        KindBox.SelectedIndex = 0;
        ControllerBox.SelectedIndex = 0;
    }

    public DirectMidiEventLaneTarget? Result { get; private set; }

    private void OnKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectorLabel is null
            || KindBox.SelectedItem is not DirectMidiLaneKindOption option)
        {
            return;
        }
        bool controller = option.Kind == DirectMidiChannelEventKind.ControlChange;
        bool keyNumber = option.Kind is DirectMidiChannelEventKind.PolyphonicKeyPressure
            or DirectMidiChannelEventKind.NoteOn
            or DirectMidiChannelEventKind.NoteOff;
        SelectorLabel.Visibility = controller || keyNumber ? Visibility.Visible : Visibility.Collapsed;
        SelectorLabel.Text = controller ? "CONTROLLER" : "KEY NUMBER";
        ControllerBox.Visibility = controller ? Visibility.Visible : Visibility.Collapsed;
        KeyNumberBox.Visibility = keyNumber ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSelectClick(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        if (KindBox.SelectedItem is not DirectMidiLaneKindOption option)
        {
            ValidationText.Text = "Select an event kind.";
            return;
        }
        int selector = 0;
        if (option.Kind == DirectMidiChannelEventKind.ControlChange)
        {
            if (ControllerBox.SelectedItem is not DirectMidiControllerOption controller)
            {
                ValidationText.Text = "Select a Control Change number.";
                return;
            }
            selector = controller.Number;
        }
        else if (option.Kind is DirectMidiChannelEventKind.PolyphonicKeyPressure
                 or DirectMidiChannelEventKind.NoteOn
                 or DirectMidiChannelEventKind.NoteOff)
        {
            if (!int.TryParse(
                    KeyNumberBox.Text,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out selector)
                || selector is < 0 or > 127)
            {
                ValidationText.Text = "Key Number must be 0–127.";
                return;
            }
        }
        Result = new(option.Kind, selector);
        DialogResult = true;
    }

    private void OnTitleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private sealed record DirectMidiLaneKindOption(
        DirectMidiChannelEventKind Kind,
        string DisplayName);

    private sealed record DirectMidiControllerOption(int Number, string DisplayName);
}
