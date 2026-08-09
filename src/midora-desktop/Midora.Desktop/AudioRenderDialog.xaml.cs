using System.Globalization;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Midora.Domain;

namespace Midora.Desktop;

public partial class AudioRenderDialog : Window
{
    private readonly string _suggestedWholeMixName;

    public AudioRenderDialog(
        AudioRenderProjectSettings settings,
        IEnumerable<LogicalTrack> tracks,
        string initialDirectory,
        string suggestedWholeMixName)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(tracks);
        InitializeComponent();
        _suggestedWholeMixName = suggestedWholeMixName;
        ModeBox.ItemsSource = Enum.GetValues<AudioRenderMode>();
        ModeBox.SelectedItem = settings.Mode;
        SampleRateBox.Text = settings.SampleRate.ToString(CultureInfo.InvariantCulture);
        VoicesBox.Text = settings.MaximumSampleVoicesPerUnitStream.ToString(CultureInfo.InvariantCulture);
        StartTickBox.Text = (settings.ManualStartTick ?? 0).ToString(CultureInfo.InvariantCulture);
        EndTickBox.Text = settings.ManualEndTick?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        SelectedTracksCheck.IsChecked = settings.TrackSelectionMode == ProjectTrackSelectionMode.ExplicitLogicalTrackIds;
        LogicalTrack[] trackArray = tracks.ToArray();
        for (int index = 0; index < trackArray.Length; index++)
        {
            LogicalTrack track = trackArray[index];
            TrackRows.Add(new(
                track.Id,
                string.IsNullOrWhiteSpace(track.Name) ? $"Logical Track {index + 1}" : track.Name,
                settings.TrackSelectionMode != ProjectTrackSelectionMode.ExplicitLogicalTrackIds
                    || settings.ExplicitLogicalTrackIds.Contains(track.Id)));
        }
        OutputPathBox.Text = settings.Mode == AudioRenderMode.WholeMix
            ? Path.Combine(initialDirectory, suggestedWholeMixName)
            : initialDirectory;
        DataContext = this;
    }

    public DesktopAudioRenderOptions? Options { get; private set; }
    public ObservableCollection<TrackSelectionRow> TrackRows { get; } = [];

    private void OnTitleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnModeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (OutputPathBox is null || ModeBox.SelectedItem is not AudioRenderMode mode) return;
        string directory = mode == AudioRenderMode.WholeMix
            ? Path.GetDirectoryName(OutputPathBox.Text) ?? string.Empty
            : File.Exists(OutputPathBox.Text)
                ? Path.GetDirectoryName(OutputPathBox.Text) ?? string.Empty
                : OutputPathBox.Text;
        OutputPathBox.Text = mode == AudioRenderMode.WholeMix
            ? Path.Combine(directory, _suggestedWholeMixName)
            : directory;
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        if (ModeBox.SelectedItem is AudioRenderMode.WholeMix)
        {
            SaveFileDialog dialog = new()
            {
                Title = "Select Whole Mix WAVE Target",
                Filter = "WAVE audio (*.wav)|*.wav",
                AddExtension = true,
                DefaultExt = ".wav",
                FileName = _suggestedWholeMixName,
                InitialDirectory = Path.GetDirectoryName(OutputPathBox.Text)
            };
            if (dialog.ShowDialog(this) == true) OutputPathBox.Text = dialog.FileName;
        }
        else
        {
            OpenFolderDialog dialog = new()
            {
                Title = "Select Per Logical Track Audio Directory",
                Multiselect = false,
                InitialDirectory = Directory.Exists(OutputPathBox.Text) ? OutputPathBox.Text : null
            };
            if (dialog.ShowDialog(this) == true) OutputPathBox.Text = dialog.FolderName;
        }
    }

    private void OnReviewClick(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        if (string.IsNullOrWhiteSpace(OutputPathBox.Text))
        {
            ValidationText.Text = "Select an output target.";
            return;
        }
        if (!long.TryParse(StartTickBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out long start) || start < 0)
        {
            ValidationText.Text = "Start tick must be a non-negative integer.";
            return;
        }
        long? end = null;
        if (!string.IsNullOrWhiteSpace(EndTickBox.Text))
        {
            if (!long.TryParse(EndTickBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out long value) || value <= start)
            {
                ValidationText.Text = "End tick must be an integer greater than start tick.";
                return;
            }
            end = value;
        }
        if (!int.TryParse(SampleRateBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int sampleRate)
            || sampleRate is < AudioRenderProjectSettings.MinimumSampleRate or > AudioRenderProjectSettings.MaximumSampleRate)
        {
            ValidationText.Text = "Sample rate must be an integer from 8,000 through 192,000 Hz.";
            return;
        }
        if (!int.TryParse(VoicesBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int voices)
            || voices is < AudioRenderProjectSettings.MinimumSampleVoicesPerUnitStream or > AudioRenderProjectSettings.MaximumSampleVoicesPerUnitStreamLimit)
        {
            ValidationText.Text = "Maximum sample voices must be an integer from 1 through 16,777,216.";
            return;
        }
        HashSet<MidoraId>? selectedTrackIds = SelectedTracksCheck.IsChecked == true
            ? TrackRows.Where(item => item.IsSelected).Select(item => item.Id).ToHashSet()
            : null;
        if (selectedTrackIds is { Count: 0 })
        {
            ValidationText.Text = "Check at least one Logical Track, or disable explicit Track selection.";
            return;
        }
        Options = new(
            (AudioRenderMode)ModeBox.SelectedItem,
            OutputPathBox.Text,
            start,
            end,
            sampleRate,
            voices,
            WarningsCheck.IsChecked == true,
            AcceptExternalSoundFontHashChange: false,
            SelectedTrackIds: selectedTrackIds);
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
