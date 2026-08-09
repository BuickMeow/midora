using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;

namespace Midora.Desktop;

public sealed record SelectionDialogItem(object Value, string Display, string Description = "");

public partial class SelectionDialog : Window
{
    public SelectionDialog(string title, string prompt, IEnumerable<SelectionDialogItem> options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(options);
        InitializeComponent();
        Title = title;
        Prompt = prompt ?? string.Empty;
        foreach (SelectionDialogItem option in options) Options.Add(option);
        DataContext = this;
        if (Options.Count != 0) OptionsList.SelectedIndex = 0;
    }

    public string Prompt { get; }
    public ObservableCollection<SelectionDialogItem> Options { get; } = [];
    public object? SelectedValue { get; private set; }

    private void OnSelectClick(object sender, RoutedEventArgs e)
    {
        if (OptionsList.SelectedItem is not SelectionDialogItem selected) return;
        SelectedValue = selected.Value;
        DialogResult = true;
    }

    private void OnOptionsDoubleClick(object sender, MouseButtonEventArgs e) => OnSelectClick(sender, e);
    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
