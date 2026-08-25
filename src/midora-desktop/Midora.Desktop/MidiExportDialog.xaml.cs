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
    private readonly List<(TrackSelectionRow Row, bool IsPureMidi)> _allTrackRows = [];

    public MidiExportDialog(
        MidoraProject project,
        string? initialDirectory)
    {
        ArgumentNullException.ThrowIfNull(project);
        InitializeComponent();
        ModeBox.ItemsSource = Enum.GetValues<MidiExportMode>();
        RoutingBox.ItemsSource = Enum.GetValues<MidiExportRoutingStrategy>();
        ModeBox.SelectedItem = MidiExportMode.WholeProject;
        RoutingBox.SelectedItem = MidiExportRoutingStrategy.Compact;
        StartTickBox.Text = "0";
        EndTickBox.Text = string.Empty;
        ReadmeCheck.IsChecked = true;
        WarningsCheck.IsChecked = false;
        SelectedTracksCheck.IsChecked = false;
        LogicalTrack[] logicalTracks = project.LogicalTracksInArrangementOrder().ToArray();
        for (int index = 0; index < logicalTracks.Length; index++)
        {
            LogicalTrack track = logicalTracks[index];
            _allTrackRows.Add((new(
                track.Id,
                $"Logical · {(string.IsNullOrWhiteSpace(track.Name) ? $"Logical Track {index + 1}" : track.Name)}",
                isSelected: true), false));
        }
        PureMidiTrack[] pureMidiTracks = project.PureMidiTracksInArrangementOrder().ToArray();
        for (int index = 0; index < pureMidiTracks.Length; index++)
        {
            PureMidiTrack track = pureMidiTracks[index];
            _allTrackRows.Add((new(
                track.Id,
                $"MIDI · {(string.IsNullOrWhiteSpace(track.Name) ? $"MIDI Track {index + 1}" : track.Name)}",
                isSelected: true), true));
        }
        RefreshTrackRows();
        OutputDirectoryBox.Text = initialDirectory ?? string.Empty;
        DataContext = this;
    }

    public DesktopMidiExportOptions? Options { get; private set; }
    public ObservableCollection<TrackSelectionRow> TrackRows { get; } = [];

    private void OnTitleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnTrackListPreviewMouseWheel(object sender, MouseWheelEventArgs e) =>
        ListBoxWheelScroll.ScrollOneItemPerNotch(TrackListBox, e);

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
        RefreshTrackRows();
    }

    private void RefreshTrackRows()
    {
        if (ModeBox is null || SelectedTracksCheck is null)
        {
            return;
        }
        bool logicalOnly = ModeBox.SelectedItem is MidiExportMode.PerLogicalTrack;
        SelectedTracksCheck.Content = logicalOnly
            ? "Use only the checked Logical Tracks"
            : "Use only the checked Tracks";
        TrackRows.Clear();
        foreach ((TrackSelectionRow row, bool isPureMidi) in _allTrackRows)
        {
            if (!logicalOnly || !isPureMidi)
            {
                TrackRows.Add(row);
            }
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
            ValidationText.Text = ModeBox.SelectedItem is MidiExportMode.PerLogicalTrack
                ? "Check at least one Logical Track, or disable explicit Track selection."
                : "Check at least one Track, or disable explicit Track selection.";
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
