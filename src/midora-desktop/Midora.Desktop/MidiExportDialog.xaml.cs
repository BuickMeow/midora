using System.Globalization;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Midora.MidiExport;
using Midora.Domain;

namespace Midora.Desktop;

public partial class MidiExportDialog : Window
{
    public MidiExportDialog(
        ExportProjectSettings settings,
        IEnumerable<LogicalTrack> tracks,
        string? initialDirectory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(tracks);
        InitializeComponent();
        ModeBox.ItemsSource = Enum.GetValues<MidiExportMode>();
        RoutingBox.ItemsSource = Enum.GetValues<MidiExportRoutingStrategy>();
        ModeBox.SelectedItem = settings.Mode switch
        {
            ProjectMidiExportMode.PerLogicalTrack => MidiExportMode.PerLogicalTrack,
            ProjectMidiExportMode.PerPort => MidiExportMode.PerPort,
            _ => MidiExportMode.WholeProject
        };
        RoutingBox.SelectedItem = settings.Routing == ProjectMidiExportRoutingStrategy.Compact
            ? MidiExportRoutingStrategy.Compact
            : MidiExportRoutingStrategy.Preserve;
        StartTickBox.Text = (settings.ManualStartTick ?? 0).ToString(CultureInfo.InvariantCulture);
        EndTickBox.Text = settings.ManualEndTick?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        ReadmeCheck.IsChecked = settings.IncludeReadme;
        WarningsCheck.IsChecked = settings.TreatWarningsAsErrors;
        SelectedTracksCheck.IsChecked = settings.TrackSelectionMode == ProjectMidiExportTrackSelectionMode.ExplicitAtTaskStart;
        LogicalTrack[] trackArray = tracks.ToArray();
        for (int index = 0; index < trackArray.Length; index++)
        {
            LogicalTrack track = trackArray[index];
            TrackRows.Add(new(
                track.Id,
                string.IsNullOrWhiteSpace(track.Name) ? $"Logical Track {index + 1}" : track.Name,
                isSelected: true));
        }
        OutputDirectoryBox.Text = initialDirectory ?? string.Empty;
        DataContext = this;
    }

    public DesktopMidiExportOptions? Options { get; private set; }
    public ObservableCollection<TrackSelectionRow> TrackRows { get; } = [];

    private void OnTitleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dialog = new()
        {
            Title = "Select MIDI Export Directory",
            Multiselect = false,
            InitialDirectory = Directory.Exists(OutputDirectoryBox.Text)
                ? OutputDirectoryBox.Text
                : null
        };
        if (dialog.ShowDialog(this) == true) OutputDirectoryBox.Text = dialog.FolderName;
    }

    private void OnModeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (RoutingBox is not null)
        {
            RoutingBox.IsEnabled = ModeBox.SelectedItem is not MidiExportMode.PerPort;
        }
    }

    private void OnReviewClick(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        if (!Directory.Exists(OutputDirectoryBox.Text))
        {
            ValidationText.Text = "Select an existing output directory.";
            return;
        }
        if (!long.TryParse(StartTickBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out long start)
            || start < 0)
        {
            ValidationText.Text = "Start tick must be a non-negative integer.";
            return;
        }
        long? end = null;
        if (!string.IsNullOrWhiteSpace(EndTickBox.Text))
        {
            if (!long.TryParse(EndTickBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed)
                || parsed <= start)
            {
                ValidationText.Text = "End tick must be an integer greater than the start tick.";
                return;
            }
            end = parsed;
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
            (MidiExportMode)ModeBox.SelectedItem,
            ModeBox.SelectedItem is MidiExportMode.PerPort
                ? MidiExportRoutingStrategy.Preserve
                : (MidiExportRoutingStrategy)RoutingBox.SelectedItem,
            OutputDirectoryBox.Text,
            start,
            end,
            ReadmeCheck.IsChecked == true,
            WarningsCheck.IsChecked == true,
            SelectedTrackIds: selectedTrackIds);
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
