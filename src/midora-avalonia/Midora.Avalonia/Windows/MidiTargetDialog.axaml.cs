using System;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Midora.Avalonia.Windows;

public partial class MidiTargetDialog : Window
{
    private static readonly string[] Kinds =
    [
        "ControlChange",
        "BankMsb",
        "BankLsb",
        "Program",
        "PitchBend",
        "RegisteredParameter",
        "NonRegisteredParameter",
        "PitchBendRangeSemitones",
        "PitchBendRangeCents"
    ];

    private static readonly int[] EditableControllerNumbers =
    [
        0, 1, 5, 6, 7, 10, 11, 32, 38, 42, 64, 65, 66, 67,
        71, 72, 73, 74, 75, 76, 77, 78, 84, 94, 98, 99, 100, 101
    ];

    private static readonly ControllerOption[] Controllers = EditableControllerNumbers
        .Select(number => new ControllerOption(number, FormatController(number)))
        .ToArray();

    private bool _updating;

    public MidiTargetDialog()
        : this("Select MIDI Target")
    {
    }

    public MidiTargetDialog(string title = "Select MIDI Target")
    {
        InitializeComponent();
        Title = title;
        DialogTitleText.Text = title;
        _updating = true;
        KindBox.ItemsSource = Kinds;
        ControllerBox.ItemsSource = Controllers;
        KindBox.SelectedItem = "ControlChange";
        ControllerBox.SelectedIndex = 0;
        NumberBox.Text = "0";
        UpdateKindState();
        _updating = false;
    }

    public MidiTargetSelection? Result { get; private set; }
    public bool Confirmed { get; private set; }

    public sealed record MidiTargetSelection(string Kind, int Number)
    {
        public override string ToString() => $"{Kind} {Number}";
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
        bool controller = KindBox.SelectedItem is "ControlChange";
        bool numberedParameter = KindBox.SelectedItem is "RegisteredParameter" or "NonRegisteredParameter";
        ControllerBox.IsVisible = controller;
        NumberBox.IsVisible = numberedParameter;
        NumberLabel.IsVisible = controller || numberedParameter;
        NumberLabel.Text = controller ? "CONTROLLER" : "PARAMETER NUMBER";
        if (!numberedParameter)
        {
            NumberBox.Text = "0";
        }
    }

    private void OnSelectClick(object? sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        if (KindBox.SelectedItem is not string kind)
        {
            ValidationText.Text = "Select a target kind.";
            return;
        }

        int number;
        if (kind == "ControlChange")
        {
            if (ControllerBox.SelectedItem is not ControllerOption controller)
            {
                ValidationText.Text = "Select a supported Control Change.";
                return;
            }

            number = controller.Number;
        }
        else if (kind is "RegisteredParameter" or "NonRegisteredParameter")
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
            "ControlChange" => EditableControllerNumbers.Contains(number),
            "RegisteredParameter" or "NonRegisteredParameter" => number is >= 0 and <= 16_383,
            _ => number == 0
        };
        if (!valid)
        {
            ValidationText.Text = kind == "ControlChange"
                ? "Select a Control Change supported by Midora's BASSMIDI profile."
                : "RPN/NRPN number must be 0-16,383.";
            return;
        }

        Result = new MidiTargetSelection(kind, number);
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
            94 => "Effects 4 Depth (User Effect Send)",
            98 => "Non-Registered Parameter Number (LSB)",
            99 => "Non-Registered Parameter Number (MSB)",
            100 => "Registered Parameter Number (LSB)",
            101 => "Registered Parameter Number (MSB)",
            _ => null
        };
        return name is null ? $"CC {number}" : $"{number} - {name}";
    }

    private sealed record ControllerOption(int Number, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }
}
