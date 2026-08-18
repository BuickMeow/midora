using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Midora.Desktop;

public partial class BatchEditPresetDialog : Window
{
    private readonly BatchEditPresetStore _store;
    private readonly BatchEditPresetKind _kind;

    public BatchEditPresetDialog(BatchEditPresetStore store, BatchEditPresetKind kind)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _kind = kind;
        InitializeComponent();
        DataContext = this;
        Reload();
    }

    public ObservableCollection<BatchEditPresetInfo> Presets { get; } = [];
    public BatchEditPreset? SelectedPreset { get; private set; }

    private void Reload()
    {
        Presets.Clear();
        foreach (BatchEditPresetInfo preset in _store.Load(_kind)) Presets.Add(preset);
        PreviewText.Text = Presets.Count == 0
            ? "No presets are stored in this category."
            : "Select a preset to preview it.";
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PreviewText.Text = PresetList.SelectedItem is BatchEditPresetInfo preset
            ? preset.Preview
            : "Select a preset to preview it.";
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (PresetList.SelectedItem is not BatchEditPresetInfo preset) return;
        SelectedPreset = preset.Preset;
        DialogResult = true;
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (PresetList.SelectedItem is not BatchEditPresetInfo preset) return;
        if (MessageDialog.Show(
                this,
                $"Delete batch edit preset '{preset.Name}'?",
                "Delete Preset",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        try
        {
            _store.Delete(preset);
            Reload();
        }
        catch (Exception exception)
        {
            _ = MessageDialog.Show(this, exception.Message, "Delete Preset", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
