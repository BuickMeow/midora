using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Midora.Avalonia.Windows;

/// <summary>
/// Avalonia port of <c>Midora.Desktop.ObjectPropertiesDialog</c>. The WPF dialog binds an
/// <c>ObjectPropertiesViewModel</c> whose <c>PropertyField</c> rows exchange mixed/editable state
/// with the session controller; this port reproduces the same row structure, the Mixed-field
/// activation and restore-original controls, and the task overlay with local placeholder rows.
/// </summary>
public partial class ObjectPropertiesDialog : Window
{
    private readonly ObjectPropertiesSurface _surface = new();
    private DispatcherTimer? _submitTimer;
    private bool _submitting;

    public ObjectPropertiesDialog()
        : this(canEdit: true)
    {
    }

    public ObjectPropertiesDialog(bool canEdit)
    {
        InitializeComponent();
        CanEdit = canEdit;
        SeedPlaceholderFields(canEdit);
        DataContext = _surface;
    }

    public bool CanEdit { get; }

    /// <summary>Avalonia has no DialogResult: callers read <see cref="Confirmed"/> after the dialog closes.</summary>
    public bool Confirmed { get; private set; }

    public ObservableCollection<ObjectPropertyFieldRow> Fields => _surface.Fields;

    private void SeedPlaceholderFields(bool canEdit)
    {
        Fields.Add(new ObjectPropertyFieldRow("name", "Name", "Lead Piano", canEdit));
        Fields.Add(new ObjectPropertyFieldRow("startTick", "Start Tick", "1,920", canEdit));
        Fields.Add(new ObjectPropertyFieldRow("endTick", "End Tick", "3,840", canEdit));
        Fields.Add(new ObjectPropertyFieldRow("key", "Key Number", "60", canEdit));
        Fields.Add(new ObjectPropertyFieldRow(
            "velocity",
            "Velocity",
            "96",
            canEdit,
            ObjectPropertyValueState.Mixed));
        Fields.Add(new ObjectPropertyFieldRow("gate", "Gate", "480", canEdit));
        Fields.Add(new ObjectPropertyFieldRow(
            "channel",
            "Channel",
            "1",
            canEdit,
            choices:
            [
                new ObjectPropertyChoiceRow("1", "Channel 1"),
                new ObjectPropertyChoiceRow("2", "Channel 2"),
                new ObjectPropertyChoiceRow("3", "Channel 3"),
                new ObjectPropertyChoiceRow("4", "Channel 4")
            ]));
        Fields.Add(new ObjectPropertyFieldRow(
            "segment",
            "Segment",
            "A",
            canEdit,
            choices:
            [
                new ObjectPropertyChoiceRow("A", "Segment A"),
                new ObjectPropertyChoiceRow("B", "Segment B")
            ]));
        Fields.Add(new ObjectPropertyFieldRow("muted", "Muted", "False", canEdit, isBoolean: true));
        Fields.Add(new ObjectPropertyFieldRow("instrument", "Event Instrument", "Grand Piano · Definition", canEdit, isEditable: false));
    }

    private void OnActivateMixedClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ObjectPropertyFieldRow field })
        {
            field.ActivateMixedEdit();
            _surface.ErrorText = null;
        }
    }

    private void OnResetFieldClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ObjectPropertyFieldRow field })
        {
            field.Reset();
            _surface.ErrorText = null;
        }
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (_submitting)
        {
            return;
        }

        if (!CanEdit)
        {
            _surface.ErrorText = "The Project is read-only while playback or a rendering task owns the session.";
            return;
        }

        ObjectPropertyFieldRow? pendingMixed = Fields.FirstOrDefault(field => field.CanActivateMixed);
        if (pendingMixed is not null)
        {
            _surface.ErrorText =
                $"The selected objects have different '{pendingMixed.Label}' values. Use the + button to edit all of them as one value, or cancel.";
            return;
        }

        if (!Fields.Any(field => field.HasPendingChange))
        {
            // WPF closed without a command when nothing changed; keep the same short-circuit.
            Confirmed = true;
            Close();
            return;
        }

        _surface.ErrorText = null;
        BeginPlaceholderSubmit();
    }

    /// <summary>
    /// The WPF dialog awaits a real staged Project edit behind this overlay. The Avalonia port has no
    /// session controller, so the overlay demonstrates the indeterminate task pipeline and completes
    /// after a short interval.
    /// </summary>
    private void BeginPlaceholderSubmit()
    {
        _submitting = true;
        _surface.TaskName = "Apply Properties";
        _surface.TaskDetail = "Preparing property changes...";
        _surface.IsTaskCancelEnabled = true;
        PropertyTaskOverlay.IsVisible = true;
        _submitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _submitTimer.Tick += OnPlaceholderSubmitTick;
        _submitTimer.Start();
    }

    private void OnPlaceholderSubmitTick(object? sender, EventArgs e)
    {
        _submitTimer?.Stop();
        _submitTimer = null;
        _submitting = false;
        PropertyTaskOverlay.IsVisible = false;
        Confirmed = true;
        Close();
    }

    private void OnCancelTaskClick(object? sender, RoutedEventArgs e)
    {
        if (!_submitting)
        {
            return;
        }

        _submitTimer?.Stop();
        _submitTimer = null;
        _submitting = false;
        PropertyTaskOverlay.IsVisible = false;
        _surface.ErrorText = "Applying properties was cancelled.";
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (_submitting)
        {
            OnCancelTaskClick(sender, e);
            return;
        }

        Confirmed = false;
        Close();
    }

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}

/// <summary>Local stand-in for <c>Midora.Desktop.ObjectPropertiesViewModel</c>.</summary>
public sealed class ObjectPropertiesSurface : INotifyPropertyChanged
{
    private string _title = "Lead Piano · Note";
    private string _context = "2 objects selected in the Arrangement Workspace.";
    private string? _errorText;
    private string _taskName = "Apply Properties";
    private string _taskDetail = "Preparing property changes...";
    private bool _isTaskCancelEnabled = true;
    private PropertyChangedEventHandler? _propertyChanged;

    public ObservableCollection<ObjectPropertyFieldRow> Fields { get; } = [];

    public string Title
    {
        get => _title;
        set
        {
            _title = value;
            Raise(nameof(Title));
        }
    }

    public string Context
    {
        get => _context;
        set
        {
            _context = value;
            Raise(nameof(Context));
        }
    }

    public string? ErrorText
    {
        get => _errorText;
        set
        {
            _errorText = value;
            Raise(nameof(ErrorText));
            Raise(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public string TaskName
    {
        get => _taskName;
        set
        {
            _taskName = value;
            Raise(nameof(TaskName));
        }
    }

    public string TaskDetail
    {
        get => _taskDetail;
        set
        {
            _taskDetail = value;
            Raise(nameof(TaskDetail));
        }
    }

    public bool IsTaskCancelEnabled
    {
        get => _isTaskCancelEnabled;
        set
        {
            _isTaskCancelEnabled = value;
            Raise(nameof(IsTaskCancelEnabled));
        }
    }

    event PropertyChangedEventHandler? INotifyPropertyChanged.PropertyChanged
    {
        add => _propertyChanged += value;
        remove => _propertyChanged -= value;
    }

    private void Raise(string propertyName) =>
        _propertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public enum ObjectPropertyValueState
{
    SameValue,
    Mixed,
    Unavailable
}

public sealed record ObjectPropertyChoiceRow(string Value, string Label);

/// <summary>
/// Local stand-in for <c>Midora.Desktop.PropertyField</c>: same row state machine (mixed value,
/// enable-as-one-value, restore original) without the Project edit pipeline behind it.
/// </summary>
public sealed class ObjectPropertyFieldRow : INotifyPropertyChanged
{
    private readonly bool _canEdit;
    private string _value;
    private bool _booleanValue;
    private ObjectPropertyValueState _valueState;
    private ObjectPropertyChoiceRow? _selectedChoice;
    private PropertyChangedEventHandler? _propertyChanged;

    public ObjectPropertyFieldRow(
        string key,
        string label,
        string value,
        bool canEdit = true,
        ObjectPropertyValueState valueState = ObjectPropertyValueState.SameValue,
        bool isBoolean = false,
        bool isEditable = true,
        IReadOnlyList<ObjectPropertyChoiceRow>? choices = null)
    {
        Key = key;
        Label = label;
        _value = value;
        _canEdit = canEdit;
        _valueState = valueState;
        IsEditable = isEditable;
        IsBoolean = isBoolean;
        Choices = choices ?? [];
        OriginalValue = value;
        OriginalValueState = valueState;
        _booleanValue = bool.TryParse(value, out bool parsed) && parsed;
        _selectedChoice = Choices.FirstOrDefault(choice => string.Equals(choice.Value, value, StringComparison.Ordinal));
    }

    public string Key { get; }

    public string Label { get; }

    public bool IsEditable { get; }

    public bool IsBoolean { get; }

    public IReadOnlyList<ObjectPropertyChoiceRow> Choices { get; }

    public string OriginalValue { get; }

    public ObjectPropertyValueState OriginalValueState { get; }

    public ObjectPropertyValueState ValueState
    {
        get => _valueState;
        private set
        {
            if (_valueState == value)
            {
                return;
            }

            _valueState = value;
            Raise(nameof(IsMixed));
            Raise(nameof(IsInputEnabled));
            Raise(nameof(IsEditorEnabled));
            Raise(nameof(CanActivateMixed));
            Raise(nameof(HasPendingChange));
            Raise(nameof(CanReset));
        }
    }

    public bool IsMixed => ValueState == ObjectPropertyValueState.Mixed;

    public bool IsChoice => Choices.Count > 0;

    public bool IsInputEnabled => IsEditable && ValueState == ObjectPropertyValueState.SameValue;

    public bool IsEditorEnabled => _canEdit && IsInputEnabled;

    public bool IsReadOnly => !IsEditorEnabled;

    public bool IsTextVisible => !IsChoice && !IsBoolean;

    public bool CanActivateMixed => IsEditable && IsMixed;

    public bool HasPendingChange => IsEditable
        && (ValueState != OriginalValueState || !string.Equals(Value, OriginalValue, StringComparison.Ordinal));

    public bool CanReset => HasPendingChange;

    public string Value
    {
        get => _value;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_value, value, StringComparison.Ordinal))
            {
                return;
            }

            _value = value;
            bool parsed = bool.TryParse(value, out bool booleanValue) && booleanValue;
            if (_booleanValue != parsed)
            {
                _booleanValue = parsed;
                Raise(nameof(BooleanValue));
            }

            Raise(nameof(HasPendingChange));
            Raise(nameof(CanReset));
        }
    }

    public bool BooleanValue
    {
        get => _booleanValue;
        set
        {
            if (_booleanValue == value)
            {
                return;
            }

            _booleanValue = value;
            Value = value ? bool.TrueString : bool.FalseString;
            Raise(nameof(BooleanValue));
        }
    }

    public ObjectPropertyChoiceRow? SelectedChoice
    {
        get => _selectedChoice;
        set
        {
            if (ReferenceEquals(_selectedChoice, value))
            {
                return;
            }

            _selectedChoice = value;
            if (value is not null)
            {
                Value = value.Value;
            }

            Raise(nameof(SelectedChoice));
        }
    }

    event PropertyChangedEventHandler? INotifyPropertyChanged.PropertyChanged
    {
        add => _propertyChanged += value;
        remove => _propertyChanged -= value;
    }

    public void ActivateMixedEdit()
    {
        if (!CanActivateMixed)
        {
            return;
        }

        ValueState = ObjectPropertyValueState.SameValue;
        if (IsChoice)
        {
            SelectedChoice = Choices[0];
        }
        else if (IsBoolean)
        {
            BooleanValue = false;
        }
        else
        {
            Value = string.Empty;
        }
    }

    public void Reset()
    {
        if (!IsEditable)
        {
            return;
        }

        ValueState = OriginalValueState;
        Value = OriginalValue;
        SelectedChoice = Choices.FirstOrDefault(choice => string.Equals(choice.Value, OriginalValue, StringComparison.Ordinal));
        Raise(nameof(HasPendingChange));
        Raise(nameof(CanReset));
    }

    private void Raise(string propertyName) =>
        _propertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
