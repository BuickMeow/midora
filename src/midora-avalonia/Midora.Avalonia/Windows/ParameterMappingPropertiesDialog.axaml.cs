using System;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Midora.Avalonia.Windows;

/// <summary>
/// Demo stand-ins for <c>Midora.Domain.MidiValueKind</c>, <c>MappingRounding</c> and
/// <c>MappingOverflow</c>. Declaration order mirrors the domain enums so the option order in the
/// combo boxes matches the WPF dialog.
/// </summary>
public enum MappingTargetKind
{
    ControlChange,
    BankMsb,
    BankLsb,
    Program,
    PitchBend,
    RegisteredParameter,
    NonRegisteredParameter,
    PitchBendRangeSemitones,
    PitchBendRangeCents
}

public enum MappingRounding
{
    Round,
    Floor,
    Ceiling
}

public enum MappingOverflow
{
    Fail,
    Clamp
}

public partial class ParameterMappingPropertiesDialog : Window
{
    private static readonly ControllerInfo[] ControllerChoices =
    [
        new(0, "Bank Select (MSB)"),
        new(1, "Modulation Wheel (MSB)"),
        new(5, "Portamento Time (MSB)"),
        new(6, "Data Entry (MSB)"),
        new(7, "Channel Volume (MSB)"),
        new(10, "Pan (MSB)"),
        new(11, "Expression (MSB)"),
        new(32, "Bank Select (LSB)"),
        new(38, "Data Entry (LSB)"),
        new(64, "Sustain Pedal"),
        new(65, "Portamento On/Off"),
        new(71, "Sound Controller 2 (Filter Resonance)"),
        new(74, "Sound Controller 5 (Filter Cutoff Frequency)"),
        new(91, "Effects 1 Depth (Reverb Send)"),
        new(93, "Effects 3 Depth (Chorus Send)")
    ];

    public ParameterMappingPropertiesDialog()
    {
        InitializeComponent();

        Choice[] parameters =
        [
            new("param.expression", "Expression"),
            new("param.brightness", "Brightness"),
            new("param.detune", "Detune")
        ];
        Choice[] subVoices =
        [
            new("subvoice.main", "Main SubVoice"),
            new("subvoice.harmony", "Harmony"),
            new("subvoice.noise", "Noise")
        ];

        SourceBox.ItemsSource = parameters;
        SubVoiceBox.ItemsSource = subVoices;
        KindBox.ItemsSource = Enum.GetValues<MappingTargetKind>();
        ControllerBox.ItemsSource = ControllerChoices;
        RoundingBox.ItemsSource = Enum.GetValues<MappingRounding>();
        OverflowBox.ItemsSource = Enum.GetValues<MappingOverflow>();

        SourceBox.SelectedItem = parameters[0];
        SubVoiceBox.SelectedItem = subVoices[0];
        KindBox.SelectedItem = MappingTargetKind.ControlChange;
        ControllerBox.SelectedItem = ControllerChoices.FirstOrDefault(value => value.Number == 11)
            ?? ControllerChoices[0];
        RpnBox.Text = "0";
        NrpnBox.Text = "0";
        RoundingBox.SelectedItem = MappingRounding.Round;
        OverflowBox.SelectedItem = MappingOverflow.Fail;
        UpdateTargetRows();
    }

    // Result surface mirroring the WPF dialog properties. Replaced by the formal Project command
    // when this dialog is wired into the real application.
    public string ParameterId { get; private set; } = string.Empty;
    public string SubVoiceId { get; private set; } = string.Empty;
    public MappingTargetKind TargetKind { get; private set; }
    public int TargetNumber { get; private set; }
    public MappingRounding Rounding { get; private set; }
    public MappingOverflow Overflow { get; private set; }

    private void OnKindChanged(object? sender, SelectionChangedEventArgs e) => UpdateTargetRows();

    private void UpdateTargetRows()
    {
        if (ControllerRow is null) return;
        bool hasKind = KindBox.SelectedItem is MappingTargetKind;
        MappingTargetKind kind = hasKind
            ? (MappingTargetKind)KindBox.SelectedItem!
            : MappingTargetKind.ControlChange;
        ControllerRow.IsVisible = hasKind && kind == MappingTargetKind.ControlChange;
        RpnRow.IsVisible = hasKind && kind == MappingTargetKind.RegisteredParameter;
        NrpnRow.IsVisible = hasKind && kind == MappingTargetKind.NonRegisteredParameter;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        if (SourceBox.SelectedItem is not Choice source
            || SubVoiceBox.SelectedItem is not Choice subVoice
            || KindBox.SelectedItem is not MappingTargetKind kind
            || RoundingBox.SelectedItem is not MappingRounding rounding
            || OverflowBox.SelectedItem is not MappingOverflow overflow)
        {
            ValidationText.Text = "Select a source, SubVoice, target kind, rounding mode, and overflow mode.";
            return;
        }
        int number = 0;
        if (kind == MappingTargetKind.ControlChange)
        {
            if (ControllerBox.SelectedItem is not ControllerInfo controller)
            {
                ValidationText.Text = "Select a supported Control Change.";
                return;
            }
            number = controller.Number;
        }
        else if (kind is MappingTargetKind.RegisteredParameter or MappingTargetKind.NonRegisteredParameter)
        {
            string text = kind == MappingTargetKind.RegisteredParameter
                ? RpnBox.Text ?? string.Empty
                : NrpnBox.Text ?? string.Empty;
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number)
                || number is < 0 or > 16_383)
            {
                ValidationText.Text = "RPN/NRPN number must be a base-10 integer from 0 through 16,383.";
                return;
            }
        }

        ParameterId = source.Value;
        SubVoiceId = subVoice.Value;
        TargetKind = kind;
        TargetNumber = number;
        Rounding = rounding;
        Overflow = overflow;
        // WPF set DialogResult = true; Avalonia closes and the caller reads the result properties.
        Close();
    }

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private sealed record Choice(string Value, string Label);

    private sealed record ControllerInfo(int Number, string DisplayName);
}
