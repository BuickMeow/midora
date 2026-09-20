using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Midora.Avalonia.Windows;

/// <summary>Demo stand-in for <c>Midora.Domain.MidiValueKind</c> (declaration order preserved).</summary>
public enum MidiValueKind
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

/// <summary>Demo stand-in for <c>Midora.Application.LogicalParameterEventBindingOperation</c>.</summary>
public enum LogicalParameterEventBindingOperation
{
    Override,
    Add,
    Multiply
}

/// <summary>Demo stand-in for <c>Midora.Application.LogicalParameterEventBindingConflictPolicy</c>.</summary>
public enum LogicalParameterEventBindingConflictPolicy
{
    Append,
    Replace
}

public sealed class EventBindingSubVoiceChoice : INotifyPropertyChanged
{
    private bool _isSelected;

    public EventBindingSubVoiceChoice(string id, string name, bool isSelected)
    {
        Id = id;
        Name = name;
        _isSelected = isSelected;
    }

    public string Id { get; }

    public string Name { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Result surface mirroring the WPF request handed to the formal Project command.</summary>
public sealed record LogicalParameterEventBindingResult(
    string Name,
    MidiValueKind TargetKind,
    int TargetNumber,
    LogicalParameterEventBindingOperation Operation,
    int SourceMinimum,
    int SourceMaximum,
    double? FactorMinimum,
    double? FactorMaximum,
    LogicalParameterEventBindingConflictPolicy ConflictPolicy,
    bool AppliesToAllSubVoices,
    IReadOnlyList<string> SubVoiceIds);

public partial class LogicalParameterEventBindingDialog : Window
{
    private static readonly ControllerChoice[] ControllerChoices =
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
        new(71, "Sound Controller 2 (Filter Resonance)"),
        new(74, "Sound Controller 5 (Filter Cutoff Frequency)"),
        new(91, "Effects 1 Depth (Reverb Send)"),
        new(93, "Effects 3 Depth (Chorus Send)")
    ];

    private readonly EventBindingSubVoiceChoice[] _subVoiceChoices;
    private readonly string? _currentSubVoiceId;
    private readonly List<DemoMapping> _existingMappings;
    private bool _initializationComplete;
    private bool _updatingDefaults;
    private bool _nameWasEdited;
    private bool _revertingConflict;

    public LogicalParameterEventBindingDialog()
        : this(["Main SubVoice", "Harmony", "Noise"], "Main SubVoice")
    {
    }

    public LogicalParameterEventBindingDialog(
        IReadOnlyList<string> subVoiceNames,
        string? currentSubVoiceName)
    {
        _currentSubVoiceId = currentSubVoiceName;
        _subVoiceChoices = subVoiceNames
            .Select((name, index) => new EventBindingSubVoiceChoice(
                $"subvoice.{index}",
                string.IsNullOrWhiteSpace(name) ? $"SubVoice {index + 1}" : name,
                string.Equals(name, currentSubVoiceName, StringComparison.Ordinal)))
            .ToArray();
        _existingMappings =
        [
            new("Main SubVoice", "Expression", MappingRounding.Round, MappingOverflow.Clamp),
            new("Main SubVoice", "Expression Depth", MappingRounding.Round, MappingOverflow.Clamp),
            new("Harmony", "Expression", MappingRounding.Round, MappingOverflow.Clamp)
        ];

        InitializeComponent();

        KindBox.ItemsSource = Enum.GetValues<MidiValueKind>();
        ControllerBox.ItemsSource = ControllerChoices;
        ScopeBox.ItemsSource = new[]
        {
            new ScopeChoice(EventBindingScopeChoice.Current, "Current SubVoice"),
            new ScopeChoice(EventBindingScopeChoice.Selected, "Selected SubVoices"),
            new ScopeChoice(EventBindingScopeChoice.All, "All SubVoices")
        };
        RefreshConflictChoices(appendAllowed: true);
        OperationBox.ItemsSource = Enum.GetValues<LogicalParameterEventBindingOperation>();
        foreach (EventBindingSubVoiceChoice choice in _subVoiceChoices)
        {
            choice.PropertyChanged += OnSubVoiceChoiceChanged;
        }
        SubVoiceList.ItemsSource = _subVoiceChoices;

        ScopeBox.SelectedIndex = _currentSubVoiceId is not null ? 0 : 2;
        ConflictBox.SelectedIndex = 0;
        OperationBox.SelectedItem = LogicalParameterEventBindingOperation.Override;
        KindBox.SelectedItem = MidiValueKind.ControlChange;
        ControllerBox.SelectedItem = ControllerChoices.FirstOrDefault(value => value.Number == 11)
            ?? ControllerChoices[0];
        _initializationComplete = true;
        UpdateTargetRowsAndDefaults(forceName: true, forceRange: true);
        UpdateScopePresentation();
    }

    public LogicalParameterEventBindingResult? Request { get; private set; }

    private void OnTargetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializationComplete && !_updatingDefaults)
        {
            UpdateTargetRowsAndDefaults(forceName: false, forceRange: false);
        }
    }

    private void OnTargetTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_initializationComplete && !_updatingDefaults)
        {
            UpdateTargetRowsAndDefaults(forceName: false, forceRange: false);
        }
    }

    private void OnScopeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializationComplete) UpdateScopePresentation();
    }

    private void OnOperationChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializationComplete && !_updatingDefaults)
        {
            UpdateTargetRowsAndDefaults(forceName: false, forceRange: true);
        }
    }

    private void OnNameChanged(object? sender, TextChangedEventArgs e)
    {
        if (_initializationComplete && !_updatingDefaults)
        {
            _nameWasEdited = true;
        }
    }

    private void OnConflictChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_revertingConflict || !_initializationComplete) return;
        if (ConflictBox.SelectedItem is ConflictChoice { IsEnabled: false })
        {
            _revertingConflict = true;
            try
            {
                ConflictBox.SelectedIndex = 0;
            }
            finally
            {
                _revertingConflict = false;
            }
        }
    }

    private void OnSubVoiceChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_initializationComplete
            && string.Equals(e.PropertyName, nameof(EventBindingSubVoiceChoice.IsSelected), StringComparison.Ordinal)
            && TryGetTarget(out MidiValueTarget target))
        {
            UpdateExistingMappingSummary(target);
        }
    }

    private void UpdateTargetRowsAndDefaults(bool forceName, bool forceRange)
    {
        if (!_initializationComplete || ControllerRow is null) return;

        _updatingDefaults = true;
        try
        {
            bool hasKind = KindBox.SelectedItem is MidiValueKind;
            MidiValueKind kind = hasKind
                ? (MidiValueKind)KindBox.SelectedItem!
                : MidiValueKind.ControlChange;
            ControllerRow.IsVisible = hasKind && kind == MidiValueKind.ControlChange;
            RpnRow.IsVisible = hasKind && kind == MidiValueKind.RegisteredParameter;
            NrpnRow.IsVisible = hasKind && kind == MidiValueKind.NonRegisteredParameter;

            LogicalParameterEventBindingOperation operation =
                OperationBox.SelectedItem is LogicalParameterEventBindingOperation value
                    ? value
                    : LogicalParameterEventBindingOperation.Override;
            FactorRangePanel.IsVisible = operation == LogicalParameterEventBindingOperation.Multiply;
            OperationHelpText.Text = operation switch
            {
                LogicalParameterEventBindingOperation.Override =>
                    "Absolute overwrite: the parameter value replaces the current target value. Its neutral default is the formal MIDI reset/default, not a user-authored Initial State.",
                LogicalParameterEventBindingOperation.Add =>
                    "Relative add: the parameter value is added to the current accumulated target value. The neutral default is 0.",
                LogicalParameterEventBindingOperation.Multiply =>
                    "Relative multiply: the source range is mapped to the factor range, then multiplied by the current accumulated target value. Factor 1 must map to an exact Integer source value.",
                _ => string.Empty
            };

            if (TryGetTarget(out MidiValueTarget target))
            {
                if (forceName || !_nameWasEdited)
                {
                    NameBox.Text = DefaultParameterName(target);
                    _nameWasEdited = false;
                }
                if (forceRange)
                {
                    SetDefaultRange(target, operation);
                }
                UpdateExistingMappingSummary(target);
            }
            else
            {
                SummaryText.Text = "Complete the MIDI target to inspect existing mappings.";
                ExistingMappingOrderList.ItemsSource = null;
            }
        }
        finally
        {
            _updatingDefaults = false;
        }
    }

    private void UpdateScopePresentation()
    {
        if (!_initializationComplete || SubVoiceSelectionPanel is null) return;
        EventBindingScopeChoice scope = (ScopeBox.SelectedItem as ScopeChoice)?.Value
            ?? EventBindingScopeChoice.All;
        SubVoiceList.IsEnabled = scope == EventBindingScopeChoice.Selected;
        SubVoiceSelectionHint.Text = scope switch
        {
            EventBindingScopeChoice.Current when _currentSubVoiceId is not null =>
                "The currently active SubVoice is frozen when OK is pressed.",
            EventBindingScopeChoice.Current => "No current SubVoice is available.",
            EventBindingScopeChoice.Selected => "Check one or more current SubVoices.",
            _ => "All current SubVoices are frozen now; later SubVoices are not added automatically."
        };
        UpdateTargetRowsAndDefaults(forceName: false, forceRange: false);
    }

    private void SetDefaultRange(MidiValueTarget target, LogicalParameterEventBindingOperation operation)
    {
        (int minimum, int maximum) = GetRange(target);
        switch (operation)
        {
            case LogicalParameterEventBindingOperation.Override:
                SourceMinimumBox.Text = minimum.ToString(CultureInfo.InvariantCulture);
                SourceMaximumBox.Text = maximum.ToString(CultureInfo.InvariantCulture);
                break;
            case LogicalParameterEventBindingOperation.Add:
                int magnitude = Math.Max(Math.Abs(minimum), Math.Abs(maximum));
                SourceMinimumBox.Text = (-magnitude).ToString(CultureInfo.InvariantCulture);
                SourceMaximumBox.Text = magnitude.ToString(CultureInfo.InvariantCulture);
                break;
            case LogicalParameterEventBindingOperation.Multiply:
                SourceMinimumBox.Text = "0";
                SourceMaximumBox.Text = "100";
                FactorMinimumBox.Text = "0";
                FactorMaximumBox.Text = "2";
                break;
        }
    }

    private void UpdateExistingMappingSummary(MidiValueTarget target)
    {
        string[] selectedNames = ResolveSelectedSubVoiceNames();
        HashSet<string> selected = selectedNames.ToHashSet(StringComparer.Ordinal);
        bool targetSupported = target.Kind == MidiValueKind.ControlChange && target.Number == 11;
        List<DemoMapping> mappings = targetSupported
            ? _existingMappings.Where(value => selected.Contains(value.SubVoiceName)).ToList()
            : [];
        int count = mappings.Count;
        bool appendAllowed = mappings.All(value =>
            value.SettingsRounding == MappingRounding.Round
            && value.SettingsOverflow == MappingOverflow.Clamp);
        SummaryText.Text = count == 0
            ? "No exact-target Mapping currently exists for the chosen SubVoices."
            : appendAllowed
                ? $"{count:N0} exact-target Mapping(s) currently exist. Append preserves them; Replace removes only these exact-target mappings and preserves unrelated order."
                : $"{count:N0} exact-target Mapping(s) currently exist. Append is unavailable because their shared target settings are not Round + Clamp; Replace remains available.";
        RefreshConflictChoices(appendAllowed);
        ExistingMappingOrderList.ItemsSource = BuildExistingMappingOrder(selectedNames, mappings);
    }

    private static string[] BuildExistingMappingOrder(
        IReadOnlyList<string> selectedSubVoiceNames,
        IReadOnlyList<DemoMapping> mappings)
    {
        List<string> rows = [];
        foreach (string subVoiceName in selectedSubVoiceNames)
        {
            List<DemoMapping> subVoiceMappings = mappings
                .Where(value => string.Equals(value.SubVoiceName, subVoiceName, StringComparison.Ordinal))
                .ToList();
            if (subVoiceMappings.Count == 0)
            {
                rows.Add($"{subVoiceName}: no existing Mapping");
                continue;
            }
            for (int index = 0; index < subVoiceMappings.Count; index++)
            {
                DemoMapping mapping = subVoiceMappings[index];
                rows.Add(
                    $"{subVoiceName}: {index + 1}. {mapping.ParameterName} · "
                    + $"{mapping.SettingsRounding} / {mapping.SettingsOverflow}");
            }
        }
        return rows.ToArray();
    }

    private void RefreshConflictChoices(bool appendAllowed)
    {
        ConflictChoice? previous = ConflictBox.SelectedItem as ConflictChoice;
        ConflictChoice[] choices =
        [
            new(
                LogicalParameterEventBindingConflictPolicy.Append,
                appendAllowed
                    ? "Append after existing mappings"
                    : "Append unavailable — shared settings are not Round + Clamp",
                appendAllowed),
            new(
                LogicalParameterEventBindingConflictPolicy.Replace,
                "Replace exact-target mappings",
                IsEnabled: true),
            new(null, "Cancel creation", IsEnabled: true)
        ];
        ConflictBox.ItemsSource = choices;
        if (previous is null) return;
        ConflictBox.SelectedItem = choices.FirstOrDefault(value =>
            value.Value == previous.Value && value.IsEnabled);
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        string name = (NameBox.Text ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            ShowValidation("Parameter Name cannot be empty.", NameBox);
            return;
        }
        if (!TryGetTarget(out MidiValueTarget target))
        {
            ShowValidation("Complete the MIDI target before creating the binding.", KindBox);
            return;
        }
        if (OperationBox.SelectedItem is not LogicalParameterEventBindingOperation operation
            || ScopeBox.SelectedItem is not ScopeChoice scopeChoice
            || ConflictBox.SelectedItem is not ConflictChoice conflictChoice)
        {
            ShowValidation("Select an operation, target scope, and existing-mapping action.");
            return;
        }
        if (conflictChoice.Value is null)
        {
            // WPF set DialogResult = false.
            Close();
            return;
        }
        if (!TryParseInt(SourceMinimumBox, "Source Minimum", out int sourceMinimum)
            || !TryParseInt(SourceMaximumBox, "Source Maximum", out int sourceMaximum)
            || sourceMaximum < sourceMinimum)
        {
            if (ValidationText.Text.Length == 0)
            {
                ShowValidation("Source Minimum must not exceed Source Maximum.", SourceMinimumBox);
            }
            return;
        }

        double? factorMinimum = null;
        double? factorMaximum = null;
        if (operation == LogicalParameterEventBindingOperation.Multiply)
        {
            if (!TryParseDouble(FactorMinimumBox, "Factor Minimum", out double parsedMinimum)
                || !TryParseDouble(FactorMaximumBox, "Factor Maximum", out double parsedMaximum)
                || parsedMaximum <= parsedMinimum)
            {
                if (ValidationText.Text.Length == 0)
                {
                    ShowValidation("Factor Minimum must be less than Factor Maximum.", FactorMinimumBox);
                }
                return;
            }
            factorMinimum = parsedMinimum;
            factorMaximum = parsedMaximum;
        }

        string? semanticError = ValidateOperationRange(
            target,
            operation,
            sourceMinimum,
            sourceMaximum,
            factorMinimum,
            factorMaximum);
        if (semanticError is not null)
        {
            ShowValidation(semanticError, SourceMinimumBox);
            return;
        }

        string[] selectedIds = ResolveSelectedSubVoiceIds();
        if (_subVoiceChoices.Length == 0)
        {
            ShowValidation("The Event Instrument has no SubVoices to bind.", ScopeBox);
            return;
        }
        if (scopeChoice.Value != EventBindingScopeChoice.All && selectedIds.Length == 0)
        {
            ShowValidation("Select at least one SubVoice.", SubVoiceList);
            return;
        }
        if (scopeChoice.Value == EventBindingScopeChoice.Current
            && (_currentSubVoiceId is null || selectedIds.Length != 1))
        {
            ShowValidation("There is no current SubVoice to bind.", ScopeBox);
            return;
        }

        Request = new LogicalParameterEventBindingResult(
            name,
            target.Kind,
            target.Number,
            operation,
            sourceMinimum,
            sourceMaximum,
            factorMinimum,
            factorMaximum,
            conflictChoice.Value.Value,
            scopeChoice.Value == EventBindingScopeChoice.All,
            scopeChoice.Value == EventBindingScopeChoice.All
                ? _subVoiceChoices.Select(value => value.Id).ToArray()
                : selectedIds);
        // WPF set DialogResult = true; Avalonia closes and the caller reads Request.
        Close();
    }

    private string[] ResolveSelectedSubVoiceNames()
    {
        EventBindingScopeChoice scope = (ScopeBox.SelectedItem as ScopeChoice)?.Value
            ?? EventBindingScopeChoice.All;
        return scope switch
        {
            EventBindingScopeChoice.Current when _currentSubVoiceId is not null =>
                [CurrentSubVoiceName()],
            EventBindingScopeChoice.Selected => _subVoiceChoices
                .Where(value => value.IsSelected)
                .Select(value => value.Name)
                .ToArray(),
            _ => _subVoiceChoices.Select(value => value.Name).ToArray()
        };
    }

    private string[] ResolveSelectedSubVoiceIds()
    {
        EventBindingScopeChoice scope = (ScopeBox.SelectedItem as ScopeChoice)?.Value
            ?? EventBindingScopeChoice.All;
        return scope switch
        {
            EventBindingScopeChoice.Current when _currentSubVoiceId is not null => [_currentSubVoiceId!],
            EventBindingScopeChoice.Selected => _subVoiceChoices
                .Where(value => value.IsSelected)
                .Select(value => value.Id)
                .ToArray(),
            EventBindingScopeChoice.All => _subVoiceChoices.Select(value => value.Id).ToArray(),
            _ => []
        };
    }

    private string CurrentSubVoiceName() =>
        _subVoiceChoices
            .FirstOrDefault(value => string.Equals(value.Id, _currentSubVoiceId, StringComparison.Ordinal))
            ?.Name
        ?? _currentSubVoiceId
        ?? string.Empty;

    private bool TryGetTarget(out MidiValueTarget target)
    {
        target = default;
        if (KindBox.SelectedItem is not MidiValueKind kind) return false;
        int number = 0;
        if (kind == MidiValueKind.ControlChange)
        {
            if (ControllerBox.SelectedItem is not ControllerChoice controller) return false;
            number = controller.Number;
        }
        else if (kind is MidiValueKind.RegisteredParameter or MidiValueKind.NonRegisteredParameter)
        {
            TextBox box = kind == MidiValueKind.RegisteredParameter ? RpnBox : NrpnBox;
            if (!int.TryParse(box.Text, NumberStyles.None, CultureInfo.InvariantCulture, out number)
                || number is < 0 or > 16_383)
            {
                return false;
            }
        }
        target = new MidiValueTarget(kind, number);
        return true;
    }

    private static string DefaultParameterName(MidiValueTarget target)
    {
        if (target.Kind == MidiValueKind.ControlChange)
        {
            ControllerChoice? controller = ControllerChoices
                .FirstOrDefault(value => value.Number == target.Number);
            if (controller is not null) return controller.DisplayName;
        }
        return $"MIDI {target.Kind} {target.Number}";
    }

    private static string? ValidateOperationRange(
        MidiValueTarget target,
        LogicalParameterEventBindingOperation operation,
        int sourceMinimum,
        int sourceMaximum,
        double? factorMinimum,
        double? factorMaximum)
    {
        switch (operation)
        {
            case LogicalParameterEventBindingOperation.Override:
                int targetDefault = GetTargetDefaultValue(target);
                return targetDefault < sourceMinimum || targetDefault > sourceMaximum
                    ? $"Override source range must contain the formal target default ({targetDefault})."
                    : null;
            case LogicalParameterEventBindingOperation.Add:
                return sourceMinimum > 0 || sourceMaximum < 0
                    ? "Add source range must contain the neutral offset 0."
                    : null;
            case LogicalParameterEventBindingOperation.Multiply:
                if (!factorMinimum.HasValue || !factorMaximum.HasValue)
                {
                    return "Multiply requires a Factor Minimum and Factor Maximum.";
                }
                if (factorMinimum.Value > 1 || factorMaximum.Value < 1)
                {
                    return "Multiply factor range must contain the neutral factor 1.";
                }
                if (sourceMinimum == sourceMaximum)
                {
                    return "Multiply source range must contain more than one value.";
                }
                double sourceDefault = sourceMinimum
                    + ((1 - factorMinimum.Value) / (factorMaximum.Value - factorMinimum.Value)
                       * (sourceMaximum - (double)sourceMinimum));
                return !double.IsFinite(sourceDefault)
                       || sourceDefault != Math.Truncate(sourceDefault)
                       || sourceDefault < sourceMinimum
                       || sourceDefault > sourceMaximum
                    ? "Factor 1 must map back to an exact Integer source value inside the source range."
                    : null;
            default:
                return "Select a supported operation.";
        }
    }

    private bool TryParseInt(TextBox box, string label, out int value)
    {
        if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }
        ShowValidation($"{label} must be a base-10 integer.", box);
        return false;
    }

    private bool TryParseDouble(TextBox box, string label, out double value)
    {
        if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value))
        {
            return true;
        }
        ShowValidation($"{label} must be a finite number.", box);
        return false;
    }

    private void ShowValidation(string message, Control? control = null)
    {
        ValidationText.Text = message;
        control?.Focus();
    }

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private static (int Minimum, int Maximum) GetRange(MidiValueTarget target) => target.Kind switch
    {
        MidiValueKind.ControlChange or MidiValueKind.BankMsb or MidiValueKind.BankLsb
            or MidiValueKind.Program => (0, 127),
        MidiValueKind.PitchBend => (-8192, 8191),
        MidiValueKind.RegisteredParameter or MidiValueKind.NonRegisteredParameter => (0, 16_383),
        MidiValueKind.PitchBendRangeSemitones => (0, 24),
        _ => (0, 127)
    };

    private static int GetTargetDefaultValue(MidiValueTarget target)
    {
        if (target.Kind != MidiValueKind.ControlChange) return 0;
        return target.Number switch
        {
            7 => 100,
            10 => 64,
            11 => 127,
            _ => 0
        };
    }

    private enum EventBindingScopeChoice
    {
        Current,
        Selected,
        All
    }

    private readonly record struct MidiValueTarget(MidiValueKind Kind, int Number);

    private sealed record ScopeChoice(EventBindingScopeChoice Value, string Label);

    private sealed record ConflictChoice(
        LogicalParameterEventBindingConflictPolicy? Value,
        string Label,
        bool IsEnabled);

    private sealed record ControllerChoice(int Number, string DisplayName);

    private sealed record DemoMapping(
        string SubVoiceName,
        string ParameterName,
        MappingRounding SettingsRounding,
        MappingOverflow SettingsOverflow);
}
