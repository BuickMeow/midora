using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Midora.Avalonia.Windows;

/// <summary>Demo stand-in for <c>Midora.Domain.LogicalParameterType</c>.</summary>
public enum LogicalParameterType
{
    Integer,
    Double,
    Enum
}

/// <summary>Demo stand-in for <c>Midora.Application.LogicalParameterLaneRebindMode</c>.</summary>
public enum LogicalParameterLaneRebindMode
{
    Clamp,
    DiscardInvalidValues
}

public partial class LogicalParameterDefinitionDialog : Window
{
    private const NumberStyles DecimalStyles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
    private const NumberStyles IntegerStyles = NumberStyles.AllowLeadingSign;
    private readonly bool _isCreation;

    public LogicalParameterDefinitionDialog()
        : this("New Logical Parameter", isCreation: true, referencingLaneCount: 0, referencingPointCount: 0)
    {
    }

    public LogicalParameterDefinitionDialog(string suggestedName)
        : this(suggestedName, isCreation: true, referencingLaneCount: 0, referencingPointCount: 0)
    {
    }

    public LogicalParameterDefinitionDialog(
        string name,
        bool isCreation,
        int referencingLaneCount,
        int referencingPointCount)
    {
        _isCreation = isCreation;
        InitializeComponent();
        DataContext = this;

        TypeBox.ItemsSource = Enum.GetValues<LogicalParameterType>();
        MigrationModeBox.ItemsSource = Enum.GetValues<LogicalParameterLaneRebindMode>();
        MigrationModeBox.SelectedItem = LogicalParameterLaneRebindMode.Clamp;

        NameBox.Text = name;
        // The WPF edit constructor binds the definition's own type; the demo opens edit review on
        // Enum so the Enum panel and migration panel are visible without a Project.
        TypeBox.SelectedItem = isCreation ? LogicalParameterType.Double : LogicalParameterType.Enum;
        MinimumBox.Text = "0";
        MaximumBox.Text = "1";
        DisplayMinimumBox.Text = "0";
        DisplayMaximumBox.Text = "1";
        DefaultValueBox.Text = "0";

        if (isCreation)
        {
            MigrationPanel.IsVisible = false;
            WindowTitleText.Text = "Create Logical Parameter";
            HeadingText.Text = "New Logical Parameter";
            DescriptionText.Text = "Define the complete Logical Parameter before adding it to the Event Instrument.";
        }
        else
        {
            AffectedDataText.Text = $"{referencingLaneCount} referencing Lane(s) · {referencingPointCount} point(s).";
            EnumItems.Add(new LogicalParameterEnumItemRow(null, "Item 1", 0));
            EnumItems.Add(new LogicalParameterEnumItemRow(null, "Item 2", 1));
        }

        UpdateDefinitionShape();
    }

    public ObservableCollection<LogicalParameterEnumItemRow> EnumItems { get; } = [];
    public string ParameterName { get; private set; } = string.Empty;
    public LogicalParameterType InputType { get; private set; }
    public double Minimum { get; private set; }
    public double Maximum { get; private set; }
    public double DisplayMinimum { get; private set; }
    public double DisplayMaximum { get; private set; }
    public double DefaultValue { get; private set; }
    public LogicalParameterLaneRebindMode MigrationMode { get; private set; }
    public bool EnumSemanticWarningAcknowledged { get; private set; }

    private void OnDefinitionShapeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        UpdateDefinitionShape();
    }

    private void OnExplicitValuesChanged(object? sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        UpdateImplicitValues();
    }

    private void OnAddEnumItemClick(object? sender, RoutedEventArgs e)
    {
        HashSet<string> names = EnumItems
            .Select(item => item.Name.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int suffix = 1;
        string name;
        do name = $"Item {suffix++}"; while (names.Contains(name));
        int value = EnumItems.Count == 0 ? 0 : EnumItems.Max(item => item.ParsedValueOrZero) + 1;
        LogicalParameterEnumItemRow row = new(null, name, value)
        {
            IsValueReadOnly = ExplicitValuesBox.IsChecked != true
        };
        EnumItems.Add(row);
        UpdateImplicitValues();
        EnumItemsList.SelectedItem = row;
        EnumItemsList.ScrollIntoView(row);
    }

    private void OnRemoveEnumItemClick(object? sender, RoutedEventArgs e)
    {
        if (EnumItemsList.SelectedItem is not LogicalParameterEnumItemRow row) return;
        EnumItems.Remove(row);
        UpdateImplicitValues();
    }

    private void OnMoveEnumItemUpClick(object? sender, RoutedEventArgs e) => MoveSelectedEnumItem(-1);

    private void OnMoveEnumItemDownClick(object? sender, RoutedEventArgs e) => MoveSelectedEnumItem(1);

    private void MoveSelectedEnumItem(int delta)
    {
        if (EnumItemsList.SelectedItem is not LogicalParameterEnumItemRow row) return;
        int index = EnumItems.IndexOf(row);
        int target = index + delta;
        if (target < 0 || target >= EnumItems.Count) return;
        EnumItems.Move(index, target);
        UpdateImplicitValues();
        EnumItemsList.SelectedItem = row;
    }

    private void UpdateDefinitionShape()
    {
        bool isEnum = TypeBox.SelectedItem is LogicalParameterType.Enum;
        EnumPanel.IsVisible = isEnum;
        EnumWarningBox.IsVisible = isEnum;
        if (isEnum && EnumItems.Count == 0)
        {
            EnumItems.Add(new LogicalParameterEnumItemRow(null, "Item 1", 0));
        }
        UpdateImplicitValues();
    }

    private void UpdateImplicitValues()
    {
        bool explicitValues = ExplicitValuesBox.IsChecked == true;
        for (int index = 0; index < EnumItems.Count; index++)
        {
            EnumItems[index].IsValueReadOnly = !explicitValues;
            if (!explicitValues) EnumItems[index].ValueText = index.ToString(CultureInfo.InvariantCulture);
        }
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            ParameterName = (NameBox.Text ?? string.Empty).Trim();
            if (ParameterName.Length == 0)
            {
                throw new InvalidOperationException("Name must not be empty.");
            }
            if (TypeBox.SelectedItem is not LogicalParameterType type)
            {
                throw new InvalidOperationException("Select an input type.");
            }

            double minimum = ParseDouble(MinimumBox.Text, "Legal Minimum");
            double maximum = ParseDouble(MaximumBox.Text, "Legal Maximum");
            double displayMinimum = ParseDouble(DisplayMinimumBox.Text, "Display Minimum");
            double displayMaximum = ParseDouble(DisplayMaximumBox.Text, "Display Maximum");
            double defaultValue = ParseDouble(DefaultValueBox.Text, "Default Value");

            bool usesExplicitValues = type == LogicalParameterType.Enum && ExplicitValuesBox.IsChecked == true;
            if (type == LogicalParameterType.Enum)
            {
                if (EnumItems.Count == 0) throw new InvalidOperationException("An Enum requires at least one item.");
                HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
                HashSet<int> values = [];
                for (int index = 0; index < EnumItems.Count; index++)
                {
                    LogicalParameterEnumItemRow row = EnumItems[index];
                    string name = row.Name.Trim();
                    if (name.Length == 0) throw new InvalidOperationException($"Enum item {index + 1} requires a name.");
                    if (!names.Add(name)) throw new InvalidOperationException("Enum item names must be unique ignoring case.");
                    int value = usesExplicitValues
                        ? ParseInt(row.ValueText, $"Enum item {index + 1} value")
                        : index;
                    if (!values.Add(value)) throw new InvalidOperationException("Enum item values must be unique.");
                }
            }

            if (!_isCreation && MigrationModeBox.SelectedItem is not LogicalParameterLaneRebindMode)
            {
                throw new InvalidOperationException("Select a migration policy for invalid existing values.");
            }
            InputType = type;
            Minimum = minimum;
            Maximum = maximum;
            DisplayMinimum = displayMinimum;
            DisplayMaximum = displayMaximum;
            DefaultValue = defaultValue;
            MigrationMode = MigrationModeBox.SelectedItem is LogicalParameterLaneRebindMode migrationMode
                ? migrationMode
                : LogicalParameterLaneRebindMode.Clamp;
            EnumSemanticWarningAcknowledged = _isCreation || EnumWarningBox.IsChecked == true;
            if (!_isCreation
                && type == LogicalParameterType.Enum
                && !EnumSemanticWarningAcknowledged)
            {
                throw new InvalidOperationException("Acknowledge the Enum semantic warning before applying the migration.");
            }

            // WPF set DialogResult = true; Avalonia closes and the caller reads the result properties.
            Close();
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or OverflowException)
        {
            ErrorText.Text = exception.Message;
        }
    }

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private static double ParseDouble(string? text, string label)
    {
        if (!double.TryParse(text, DecimalStyles, CultureInfo.InvariantCulture, out double value)
            || !double.IsFinite(value))
        {
            throw new FormatException($"{label} must be a finite decimal number using '.' and no exponent.");
        }
        return value;
    }

    private static int ParseInt(string? text, string label)
    {
        if (!int.TryParse(text, IntegerStyles, CultureInfo.InvariantCulture, out int value))
        {
            throw new FormatException($"{label} must be a base-10 integer.");
        }
        return value;
    }
}

public sealed class LogicalParameterEnumItemRow : INotifyPropertyChanged
{
    private string _name;
    private string _valueText;
    private bool _isValueReadOnly;

    public LogicalParameterEnumItemRow(string? existingItemId, string name, int value)
    {
        ExistingItemId = existingItemId;
        _name = name;
        _valueText = value.ToString(CultureInfo.InvariantCulture);
    }

    public string? ExistingItemId { get; }

    public string Name
    {
        get => _name;
        set => Set(ref _name, value ?? string.Empty);
    }

    public string ValueText
    {
        get => _valueText;
        set => Set(ref _valueText, value ?? string.Empty);
    }

    public bool IsValueReadOnly
    {
        get => _isValueReadOnly;
        set => Set(ref _isValueReadOnly, value);
    }

    public int ParsedValueOrZero =>
        int.TryParse(ValueText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value)
            ? value
            : 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
