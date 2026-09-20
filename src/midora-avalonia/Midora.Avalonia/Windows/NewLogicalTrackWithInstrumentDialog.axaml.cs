using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Midora.Avalonia.Windows;

public partial class NewLogicalTrackWithInstrumentDialog : Window
{
    private readonly InstrumentOption[] _options =
    [
        new("Grand Piano"),
        new("Warm Pad"),
        new("Bass Guitar"),
        new("Orchestral Strings")
    ];

    public NewLogicalTrackWithInstrumentDialog()
    {
        InitializeComponent();
        ExistingInstrumentList.ItemsSource = _options;
        if (_options.Length != 0)
        {
            ExistingInstrumentList.SelectedIndex = 0;
        }

        UpdateSourceState();
    }

    public bool CreatesInstrument { get; private set; }
    public string NewInstrumentName { get; private set; } = string.Empty;
    public string? ExistingInstrumentName { get; private set; }
    public bool Confirmed { get; private set; }

    private void OnSourceChanged(object? sender, RoutedEventArgs e)
    {
        if (NewInstrumentNameBox is not null)
        {
            UpdateSourceState();
        }
    }

    private void UpdateSourceState()
    {
        bool creates = NewInstrumentRadio.IsChecked == true;
        NewInstrumentNameBox.IsEnabled = creates;
        ExistingInstrumentList.IsEnabled = !creates;
        NewInstrumentNameBox.Opacity = creates ? 1 : 0.55;
        ExistingInstrumentList.Opacity = creates ? 0.55 : 1;
    }

    private void OnCreateClick(object? sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        bool creates = NewInstrumentRadio.IsChecked == true;
        string name = (NewInstrumentNameBox.Text ?? string.Empty).Trim();
        if (creates && name.Length == 0)
        {
            ErrorText.Text = "Event Instrument name must not be empty.";
            return;
        }

        if (!creates && ExistingInstrumentList.SelectedItem is not InstrumentOption)
        {
            ErrorText.Text = "Select an Event Instrument.";
            return;
        }

        CreatesInstrument = creates;
        NewInstrumentName = name;
        ExistingInstrumentName = creates
            ? null
            : ((InstrumentOption)ExistingInstrumentList.SelectedItem!).Name;
        // Avalonia has no DialogResult: callers read Confirmed after ShowDialog completes.
        Confirmed = true;
        Close();
    }

    private void OnOptionsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && ExistingInstrumentRadio.IsChecked == true)
        {
            OnCreateClick(sender, e);
        }
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

    private sealed record InstrumentOption(string Name)
    {
        public override string ToString() => Name;
    }
}
