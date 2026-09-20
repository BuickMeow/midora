using System;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Midora.Avalonia.Windows;

public partial class DirectMidiLaneTargetDialog : Window
{
    private static readonly KindOption[] Kinds =
    [
        new("ControlChange", "Control Change"),
        new("PolyphonicKeyPressure", "Polyphonic Key Pressure"),
        new("ProgramChange", "Program Change"),
        new("ChannelPressure", "Channel Pressure"),
        new("PitchBend", "Pitch Bend"),
        new("NoteOn", "Raw Note On"),
        new("NoteOff", "Raw Note Off")
    ];

    private static readonly ControllerOption[] Controllers = Enumerable.Range(0, 128)
        .Select(static number => new ControllerOption(number, FormatController(number)))
        .ToArray();

    private bool _updating;

    public DirectMidiLaneTargetDialog()
    {
        InitializeComponent();
        _updating = true;
        KindBox.ItemsSource = Kinds;
        ControllerBox.ItemsSource = Controllers;
        KindBox.SelectedIndex = 0;
        ControllerBox.SelectedIndex = 0;
        UpdateKindState();
        _updating = false;
    }

    public DirectMidiLaneSelection? Result { get; private set; }
    public bool Confirmed { get; private set; }

    public sealed record DirectMidiLaneSelection(string Kind, int SelectorNumber)
    {
        public override string ToString() => $"{Kind} {SelectorNumber}";
    }

    private void OnKindChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_updating)
        {
            UpdateKindState();
        }
    }

    private void UpdateKindState()
    {
        if (KindBox.SelectedItem is not KindOption option)
        {
            return;
        }

        bool controller = option.Kind == "ControlChange";
        bool keyNumber = option.Kind is "PolyphonicKeyPressure" or "NoteOn" or "NoteOff";
        SelectorLabel.IsVisible = controller || keyNumber;
        SelectorLabel.Text = controller ? "CONTROLLER" : "KEY NUMBER";
        ControllerBox.IsVisible = controller;
        KeyNumberBox.IsVisible = keyNumber;
    }

    private void OnSelectClick(object? sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        if (KindBox.SelectedItem is not KindOption option)
        {
            ValidationText.Text = "Select an event kind.";
            return;
        }

        int selector = 0;
        if (option.Kind == "ControlChange")
        {
            if (ControllerBox.SelectedItem is not ControllerOption controller)
            {
                ValidationText.Text = "Select a Control Change number.";
                return;
            }

            selector = controller.Number;
        }
        else if (option.Kind is "PolyphonicKeyPressure" or "NoteOn" or "NoteOff")
        {
            if (!int.TryParse(KeyNumberBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out selector)
                || selector is < 0 or > 127)
            {
                ValidationText.Text = "Key Number must be 0-127.";
                return;
            }
        }

        Result = new DirectMidiLaneSelection(option.Kind, selector);
        // Avalonia has no DialogResult: callers read Confirmed after ShowDialog completes.
        Confirmed = true;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private static string FormatController(int number)
    {
        string? name = number switch
        {
            0 => "Bank Select (MSB)",
            1 => "Modulation Wheel (MSB)",
            5 => "Portamento Time (MSB)",
            6 => "Data Entry (MSB)",
            7 => "Channel Volume (MSB)",
            10 => "Pan (MSB)",
            11 => "Expression (MSB)",
            32 => "Bank Select (LSB)",
            38 => "Data Entry (LSB)",
            42 => "Pan (LSB)",
            64 => "Sustain Pedal",
            65 => "Portamento On/Off",
            66 => "Sostenuto",
            67 => "Soft Pedal",
            71 => "Sound Controller 2 (Filter Resonance)",
            72 => "Sound Controller 3 (Release Time)",
            73 => "Sound Controller 4 (Attack Time)",
            74 => "Sound Controller 5 (Filter Cutoff Frequency)",
            75 => "Sound Controller 6 (Decay Time)",
            76 => "Sound Controller 7 (Vibrato Rate)",
            77 => "Sound Controller 8 (Vibrato Depth)",
            78 => "Sound Controller 9 (Vibrato Delay)",
            84 => "Portamento Control",
            91 => "Effects 1 Depth (Reverb Send)",
            93 => "Effects 3 Depth (Chorus Send)",
            94 => "Effects 4 Depth (User Effect Send)",
            98 => "Non-Registered Parameter Number (LSB)",
            99 => "Non-Registered Parameter Number (MSB)",
            100 => "Registered Parameter Number (LSB)",
            101 => "Registered Parameter Number (MSB)",
            120 => "All Sound Off",
            121 => "Reset All Controllers",
            123 => "All Notes Off",
            124 => "Omni Mode Off",
            125 => "Omni Mode On",
            126 => "Poly Mode Off",
            127 => "Poly Mode On",
            _ => null
        };
        return name is null ? $"CC {number}" : $"CC {number} - {name}";
    }

    private sealed record KindOption(string Kind, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    private sealed record ControllerOption(int Number, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }
}
