using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Midora.Domain;

namespace Midora.Desktop;

public partial class ParameterMappingPropertiesDialog : Window
{
    public ParameterMappingPropertiesDialog(
        EventInstrument instrument,
        LogicalParameterMapping? mapping = null)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        InitializeComponent();
        bool creating = mapping is null;
        Title = creating ? "Create Parameter Mapping" : "Parameter Mapping Properties";
        DialogTitleText.Text = Title;
        SourceBox.ItemsSource = instrument.LogicalParameters
            .Select(value => new Choice(value.Id, value.Name))
            .ToArray();
        SubVoiceBox.ItemsSource = instrument.SubVoices
            .Select((value, index) => new Choice(
                value.Id,
                string.IsNullOrWhiteSpace(value.Name) ? $"SubVoice {index + 1}" : value.Name))
            .ToArray();
        KindBox.ItemsSource = Enum.GetValues<MidiValueKind>();
        ControllerBox.ItemsSource = MidiControlChangeCatalog.EditableControllers;
        RoundingBox.ItemsSource = Enum.GetValues<MappingRounding>();
        OverflowBox.ItemsSource = Enum.GetValues<MappingOverflow>();

        SourceBox.SelectedValuePath = nameof(Choice.Value);
        SubVoiceBox.SelectedValuePath = nameof(Choice.Value);
        SourceBox.SelectedValue = mapping?.ParameterId
            ?? instrument.LogicalParameters.FirstOrDefault()?.Id;
        SubVoiceBox.SelectedValue = mapping?.SubVoiceId
            ?? instrument.SubVoices.FirstOrDefault()?.Id;
        MidiValueTarget target = mapping?.Target ?? MidiValueTarget.ControlChange(0);
        KindBox.SelectedItem = target.Kind;
        ControllerBox.SelectedItem = MidiControlChangeCatalog.EditableControllers
            .FirstOrDefault(value => value.Number == target.Number)
            ?? MidiControlChangeCatalog.EditableControllers.FirstOrDefault();
        string number = target.Number.ToString(CultureInfo.InvariantCulture);
        RpnBox.Text = number;
        NrpnBox.Text = number;
        RoundingBox.SelectedItem = mapping?.TargetSettings.Rounding ?? MappingRounding.Round;
        OverflowBox.SelectedItem = mapping?.TargetSettings.Overflow ?? MappingOverflow.Fail;
        UpdateTargetRows();
    }

    public MidoraId ParameterId { get; private set; }
    public MidoraId SubVoiceId { get; private set; }
    public MidiValueTarget Target { get; private set; }
    public MappingRounding Rounding { get; private set; }
    public MappingOverflow Overflow { get; private set; }

    private void OnKindChanged(object sender, SelectionChangedEventArgs e) => UpdateTargetRows();

    private void UpdateTargetRows()
    {
        if (ControllerRow is null) return;
        MidiValueKind? kind = KindBox.SelectedItem as MidiValueKind?;
        ControllerRow.Visibility = kind == MidiValueKind.ControlChange
            ? Visibility.Visible
            : Visibility.Collapsed;
        RpnRow.Visibility = kind == MidiValueKind.RegisteredParameter
            ? Visibility.Visible
            : Visibility.Collapsed;
        NrpnRow.Visibility = kind == MidiValueKind.NonRegisteredParameter
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        if (SourceBox.SelectedValue is not MidoraId parameterId
            || SubVoiceBox.SelectedValue is not MidoraId subVoiceId
            || KindBox.SelectedItem is not MidiValueKind kind
            || RoundingBox.SelectedItem is not MappingRounding rounding
            || OverflowBox.SelectedItem is not MappingOverflow overflow)
        {
            ValidationText.Text = "Select a source, SubVoice, target kind, rounding mode, and overflow mode.";
            return;
        }
        int number = 0;
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
            string text = kind == MidiValueKind.RegisteredParameter ? RpnBox.Text : NrpnBox.Text;
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number)
                || number is < 0 or > 16_383)
            {
                ValidationText.Text = "RPN/NRPN number must be a base-10 integer from 0 through 16,383.";
                return;
            }
        }
        ParameterId = parameterId;
        SubVoiceId = subVoiceId;
        Target = new(kind, number);
        Rounding = rounding;
        Overflow = overflow;
        DialogResult = true;
    }

    private void OnTitleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private sealed record Choice(MidoraId Value, string Label);
}
