using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace Midora.Desktop;

public partial class TextInputDialog : Window, INotifyPropertyChanged
{
    private string _value;

    public TextInputDialog(string title, string prompt, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        InitializeComponent();
        Title = title;
        Prompt = prompt ?? string.Empty;
        _value = value ?? string.Empty;
        DataContext = this;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    public string Prompt { get; }
    public string Value
    {
        get => _value;
        set
        {
            if (string.Equals(_value, value, StringComparison.Ordinal)) return;
            _value = value ?? string.Empty;
            PropertyChanged?.Invoke(this, new(nameof(Value)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnAcceptClick(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
