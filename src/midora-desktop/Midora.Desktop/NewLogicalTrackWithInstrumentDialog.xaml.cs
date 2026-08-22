using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Midora.Domain;

namespace Midora.Desktop;

public partial class NewLogicalTrackWithInstrumentDialog : Window
{
    public NewLogicalTrackWithInstrumentDialog(IEnumerable<EventInstrument> instruments)
    {
        ArgumentNullException.ThrowIfNull(instruments);
        InitializeComponent();
        foreach (EventInstrument instrument in instruments)
        {
            Options.Add(new(
                instrument.Id,
                string.IsNullOrWhiteSpace(instrument.Name) ? "Unnamed Event Instrument" : instrument.Name,
                $"{instrument.SubVoices.Count} SubVoice(s)"));
        }
        DataContext = this;
        if (Options.Count != 0) ExistingInstrumentList.SelectedIndex = 0;
        UpdateSourceState();
    }

    public ObservableCollection<SelectionDialogItem> Options { get; } = [];
    public bool CreatesInstrument { get; private set; }
    public string NewInstrumentName { get; private set; } = string.Empty;
    public MidoraId? ExistingInstrumentId { get; private set; }

    private void OnSourceChanged(object sender, RoutedEventArgs e)
    {
        if (IsInitialized) UpdateSourceState();
    }

    private void UpdateSourceState()
    {
        bool creates = NewInstrumentRadio.IsChecked == true;
        NewInstrumentNameBox.IsEnabled = creates;
        ExistingInstrumentList.IsEnabled = !creates;
        NewInstrumentNameBox.Opacity = creates ? 1 : 0.55;
        ExistingInstrumentList.Opacity = creates ? 0.55 : 1;
    }

    private void OnCreateClick(object sender, RoutedEventArgs e)
    {
        bool creates = NewInstrumentRadio.IsChecked == true;
        string name = NewInstrumentNameBox.Text.Trim();
        if (creates && name.Length == 0)
        {
            ErrorText.Text = "Event Instrument name must not be empty.";
            return;
        }
        if (!creates && ExistingInstrumentList.SelectedItem is not SelectionDialogItem)
        {
            ErrorText.Text = "Select an Event Instrument.";
            return;
        }
        CreatesInstrument = creates;
        NewInstrumentName = name;
        ExistingInstrumentId = creates
            ? null
            : (MidoraId)((SelectionDialogItem)ExistingInstrumentList.SelectedItem).Value;
        DialogResult = true;
    }

    private void OnOptionsDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ExistingInstrumentRadio.IsChecked == true) OnCreateClick(sender, e);
    }

    private void OnTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
