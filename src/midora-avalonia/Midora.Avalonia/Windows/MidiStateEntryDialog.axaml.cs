using System;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Midora.Avalonia.Windows;

public partial class MidiStateEntryDialog : Window
{
    private static readonly string[] SupportedKinds =
    [
        "ControlChange",
        "RegisteredParameter",
        "NonRegisteredParameter"
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

    public MidiStateEntryDialog()
    {
        InitializeComponent();
        _updating = true;
        KindBox.ItemsSource = SupportedKinds;
        ControllerBox.ItemsSource = Controllers;
        ControllerBox.SelectionChanged += OnControllerChanged;
        ControllerBox.SelectedIndex = 0;
        KindBox.SelectedIndex = 0;
        UpdateKindState();
        _updating = false;
    }

    public MidiStateTarget? Target { get; private set; }
    public int Value { get; private set; }
    public bool Confirmed { get; private set; }

    public sealed record MidiStateTarget(string Kind, int Number)
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
        ControllerBox.IsVisible = controller;
        NumberBox.IsVisible = !controller;
        NumberLabel.Text = controller ? "CONTROLLER" : "PARAMETER NUMBER";
        UpdateValueHint();
    }

    private void OnControllerChanged(object? sender, SelectionChangedEventArgs e) => UpdateValueHint();

    private void UpdateValueHint()
    {
        if (ValueBox is null)
        {
            return;
        }

        bool offsetController = KindBox.SelectedItem is "ControlChange"
            && ControllerBox.SelectedItem is ControllerOption cc
            && IsOffsetController(cc.Number);
        string hint = KindBox.SelectedItem is "ControlChange"
            ? offsetController ? "Value: -64 to 63; 0 is the center." : "Value: 0 to 127."
            : "Value: 0 to 16383.";
        ToolTip.SetTip(ValueBox, hint);
    }

    private void OnAddClick(object? sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        if (KindBox.SelectedItem is not string kind
            || !int.TryParse(ValueBox.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            ValidationText.Text = "Value must be a base-10 integer.";
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
        else if (!int.TryParse(
                     NumberBox.Text?.Trim(),
                     NumberStyles.Integer,
                     CultureInfo.InvariantCulture,
                     out number))
        {
            ValidationText.Text = "Parameter Number must be a base-10 integer.";
            return;
        }

        bool valid = kind switch
        {
            "ControlChange" => number is >= 0 and <= 119 and not 91 and not 93,
            "RegisteredParameter" or "NonRegisteredParameter" => number is >= 0 and <= 16_383,
            _ => false
        };
        if (!valid)
        {
            ValidationText.Text = kind == "ControlChange"
                ? "CC number must be 0-119 except 91/93."
                : "RPN/NRPN Number must be 0-16,383.";
            return;
        }

        Target = new MidiStateTarget(kind, number);
        Value = ClampInitialState(kind, number, value);
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

    private static bool IsOffsetController(int number) => number is 10 or (>= 71 and <= 78);

    private static int ClampInitialState(string kind, int number, int value)
    {
        if (kind != "ControlChange")
        {
            return Math.Clamp(value, 0, 16_383);
        }

        return IsOffsetController(number)
            ? Math.Clamp(value, -64, 63) + 64
            : Math.Clamp(value, 0, 127);
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
