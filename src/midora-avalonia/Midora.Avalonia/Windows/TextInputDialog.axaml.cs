using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Midora.Avalonia.Windows;

public partial class TextInputDialog : Window, INotifyPropertyChanged
{
    private readonly TaskCompletionSource _closed = new();
    private readonly Func<string, string?>? _submit;
    private PropertyChangedEventHandler? _propertyChanged;
    private string _value;
    private string? _validationError;
    private bool _accepted;

    public TextInputDialog()
        : this("Rename Track", "Enter a new name for the selected track:", "Piano Roll")
    {
    }

    public TextInputDialog(string title, string prompt, string value, Func<string, string?>? submit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        InitializeComponent();
        Title = title;
        Prompt = prompt ?? string.Empty;
        _value = value ?? string.Empty;
        _submit = submit;
        DataContext = this;
        Opened += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
        Closed += (_, _) => _closed.TrySetResult();
    }

    public string Prompt { get; }
    public string? ValidationError => _validationError;
    public bool HasValidationError => !string.IsNullOrEmpty(_validationError);
    public bool Accepted => _accepted;

    public string Value
    {
        get => _value;
        set
        {
            if (string.Equals(_value, value, StringComparison.Ordinal)) return;
            _value = value ?? string.Empty;
            _propertyChanged?.Invoke(this, new(nameof(Value)));
        }
    }

    event PropertyChangedEventHandler? INotifyPropertyChanged.PropertyChanged
    {
        add => _propertyChanged += value;
        remove => _propertyChanged -= value;
    }

    // Avalonia has no Window.DialogResult; a null return means the dialog was cancelled.
    public static async Task<string?> ShowAsync(
        Window? owner,
        string title,
        string prompt,
        string value,
        Func<string, string?>? submit = null)
    {
        TextInputDialog dialog = new(title, prompt, value, submit);
        if (owner is { IsVisible: true })
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
            await dialog._closed.Task;
        }
        return dialog._accepted ? dialog._value : null;
    }

    internal bool TrySubmit()
    {
        try { _validationError = _submit?.Invoke(Value); }
        catch (Exception error) { _validationError = error.Message; }
        _propertyChanged?.Invoke(this, new(nameof(ValidationError)));
        _propertyChanged?.Invoke(this, new(nameof(HasValidationError)));
        return string.IsNullOrEmpty(_validationError);
    }

    private void OnAcceptClick(object? sender, RoutedEventArgs e)
    {
        if (TrySubmit())
        {
            _accepted = true;
            Close();
        }
        else
        {
            ValueBox.Focus();
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
