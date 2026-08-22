using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Midora.Application;
using Midora.Domain;

namespace Midora.Desktop;

public partial class LogicalParameterDefinitionDialog : Window, INotifyPropertyChanged
{
    private const NumberStyles DecimalStyles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
    private const NumberStyles IntegerStyles = NumberStyles.AllowLeadingSign;
    private readonly bool _isCreation;

    public LogicalParameterDefinitionDialog(string suggestedName)
    {
        _isCreation = true;
        InitializeComponent();
        DataContext = this;
        TypeBox.ItemsSource = Enum.GetValues<LogicalParameterType>();
        MigrationModeBox.ItemsSource = Enum.GetValues<LogicalParameterLaneRebindMode>();
        MigrationModeBox.SelectedItem = LogicalParameterLaneRebindMode.Clamp;
        NameBox.Text = suggestedName;
        TypeBox.SelectedItem = LogicalParameterType.Double;
        MinimumBox.Text = "0";
        MaximumBox.Text = "1";
        DisplayMinimumBox.Text = "0";
        DisplayMaximumBox.Text = "1";
        DefaultValueBox.Text = "0";
        MigrationPanel.Visibility = Visibility.Collapsed;
        WindowTitleText.Text = "Create Logical Parameter";
        HeadingText.Text = "New Logical Parameter";
        DescriptionText.Text = "Define the complete Logical Parameter before adding it to the Event Instrument.";
        UpdateDefinitionShape();
    }

    public LogicalParameterDefinitionDialog(
        LogicalParameterDefinition parameter,
        int referencingLaneCount,
        int referencingPointCount)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        InitializeComponent();
        DataContext = this;
        TypeBox.ItemsSource = Enum.GetValues<LogicalParameterType>();
        MigrationModeBox.ItemsSource = Enum.GetValues<LogicalParameterLaneRebindMode>();
        MigrationModeBox.SelectedItem = LogicalParameterLaneRebindMode.Clamp;
        NameBox.Text = parameter.Name;
        TypeBox.SelectedItem = parameter.Type;
        MinimumBox.Text = Format(parameter.Minimum);
        MaximumBox.Text = Format(parameter.Maximum);
        DisplayMinimumBox.Text = Format(parameter.DisplayMinimum);
        DisplayMaximumBox.Text = Format(parameter.DisplayMaximum);
        DefaultValueBox.Text = Format(parameter.DefaultValue);
        ExplicitValuesBox.IsChecked = parameter.UsesExplicitEnumValues;
        foreach (LogicalParameterEnumItem item in parameter.EnumItems)
        {
            EnumItems.Add(new(item.Id, item.Name, item.Value));
        }
        AffectedDataText.Text = $"{referencingLaneCount} referencing Lane(s) · {referencingPointCount} point(s).";
        UpdateDefinitionShape();
        UpdateImplicitValues();
    }

    public ObservableCollection<LogicalParameterEnumItemEditRow> EnumItems { get; } = [];
    public LogicalParameterDefinitionEdit? DefinitionEdit { get; private set; }
    public string ParameterName { get; private set; } = string.Empty;
    public LogicalParameterLaneRebindMode MigrationMode { get; private set; }
    public bool EnumSemanticWarningAcknowledged { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnDefinitionShapeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        UpdateDefinitionShape();
    }

    private void OnExplicitValuesChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        UpdateImplicitValues();
    }

    private void OnAddEnumItemClick(object sender, RoutedEventArgs e)
    {
        HashSet<string> names = EnumItems.Select(item => item.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int suffix = 1;
        string name;
        do name = $"Item {suffix++}"; while (names.Contains(name));
        int value = EnumItems.Count == 0 ? 0 : EnumItems.Max(item => item.ParsedValueOrZero) + 1;
        LogicalParameterEnumItemEditRow row = new(null, name, value)
        {
            IsValueReadOnly = ExplicitValuesBox.IsChecked != true
        };
        EnumItems.Add(row);
        UpdateImplicitValues();
        EnumItemsList.SelectedItem = row;
        EnumItemsList.ScrollIntoView(row);
    }

    private void OnRemoveEnumItemClick(object sender, RoutedEventArgs e)
    {
        if (EnumItemsList.SelectedItem is not LogicalParameterEnumItemEditRow row) return;
        EnumItems.Remove(row);
        UpdateImplicitValues();
    }

    private void OnMoveEnumItemUpClick(object sender, RoutedEventArgs e) => MoveSelectedEnumItem(-1);
    private void OnMoveEnumItemDownClick(object sender, RoutedEventArgs e) => MoveSelectedEnumItem(1);

    private void MoveSelectedEnumItem(int delta)
    {
        if (EnumItemsList.SelectedItem is not LogicalParameterEnumItemEditRow row) return;
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
        EnumPanel.Visibility = isEnum ? Visibility.Visible : Visibility.Collapsed;
        EnumWarningBox.Visibility = isEnum ? Visibility.Visible : Visibility.Collapsed;
        if (isEnum && EnumItems.Count == 0)
        {
            EnumItems.Add(new(null, "Item 1", 0));
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

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            ParameterName = NameBox.Text.Trim();
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
            List<LogicalParameterEnumItemDefinitionEdit> items = [];
            if (type == LogicalParameterType.Enum)
            {
                if (EnumItems.Count == 0) throw new InvalidOperationException("An Enum requires at least one item.");
                HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
                HashSet<int> values = [];
                for (int index = 0; index < EnumItems.Count; index++)
                {
                    LogicalParameterEnumItemEditRow row = EnumItems[index];
                    string name = row.Name.Trim();
                    if (name.Length == 0) throw new InvalidOperationException($"Enum item {index + 1} requires a name.");
                    if (!names.Add(name)) throw new InvalidOperationException("Enum item names must be unique ignoring case.");
                    int value = usesExplicitValues ? ParseInt(row.ValueText, $"Enum item {index + 1} value") : index;
                    if (!values.Add(value)) throw new InvalidOperationException("Enum item values must be unique.");
                    items.Add(new(row.ExistingItemId, name, value));
                }
            }
            if (!_isCreation
                && MigrationModeBox.SelectedItem is not LogicalParameterLaneRebindMode)
            {
                throw new InvalidOperationException("Select a migration policy for invalid existing values.");
            }
            DefinitionEdit = new(
                type, minimum, maximum, displayMinimum, displayMaximum, defaultValue,
                usesExplicitValues, items);
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
            DialogResult = true;
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or OverflowException)
        {
            ErrorText.Text = exception.Message;
        }
    }

    private void OnTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private static double ParseDouble(string text, string label)
    {
        if (!double.TryParse(text, DecimalStyles, CultureInfo.InvariantCulture, out double value)
            || !double.IsFinite(value))
        {
            throw new FormatException($"{label} must be a finite decimal number using '.' and no exponent.");
        }
        return value;
    }

    private static int ParseInt(string text, string label)
    {
        if (!int.TryParse(text, IntegerStyles, CultureInfo.InvariantCulture, out int value))
        {
            throw new FormatException($"{label} must be a base-10 integer.");
        }
        return value;
    }

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class LogicalParameterEnumItemEditRow : INotifyPropertyChanged
{
    private string _name;
    private string _valueText;
    private bool _isValueReadOnly;

    public LogicalParameterEnumItemEditRow(MidoraId? existingItemId, string name, int value)
    {
        ExistingItemId = existingItemId;
        _name = name;
        _valueText = value.ToString(CultureInfo.InvariantCulture);
    }

    public MidoraId? ExistingItemId { get; }
    public string Name { get => _name; set => Set(ref _name, value ?? string.Empty); }
    public string ValueText { get => _valueText; set => Set(ref _valueText, value ?? string.Empty); }
    public bool IsValueReadOnly { get => _isValueReadOnly; set => Set(ref _isValueReadOnly, value); }
    public int ParsedValueOrZero => int.TryParse(ValueText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value) ? value : 0;
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
    }
}
