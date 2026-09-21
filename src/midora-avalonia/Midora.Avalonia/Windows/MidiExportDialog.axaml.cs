using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Midora.Domain;

namespace Midora.Avalonia.Windows;

public partial class MidiExportDialog : Window
{
    private readonly List<ExportTrackRow> _allTrackRows = [];

    public MidiExportDialog()
        : this(null)
    {
    }

    public MidiExportDialog(string? initialDirectory = null, IReadOnlyList<ExportTrackRow>? tracks = null)
    {
        InitializeComponent();
        ModeBox.ItemsSource = Enum.GetValues<ExportMode>();
        RoutingBox.ItemsSource = Enum.GetValues<ExportRouting>();
        ModeBox.SelectedItem = ExportMode.WholeProject;
        RoutingBox.SelectedItem = ExportRouting.Compact;
        StartTickBox.Text = "0";
        EndTickBox.Text = string.Empty;
        ReadmeCheck.IsChecked = true;
        WarningsCheck.IsChecked = false;
        SelectedTracksCheck.IsChecked = false;
        if (tracks is null)
        {
            _allTrackRows.Add(new("Logical · Piano", isPureMidi: false));
            _allTrackRows.Add(new("Logical · Strings", isPureMidi: false));
            _allTrackRows.Add(new("Logical · Drums", isPureMidi: false));
            _allTrackRows.Add(new("MIDI · Conductor Sketch", isPureMidi: true));
            _allTrackRows.Add(new("MIDI · External Hardware", isPureMidi: true));
        }
        else
        {
            _allTrackRows.AddRange(tracks);
        }

        RefreshTrackRows();
        OutputDirectoryBox.Text = initialDirectory ?? string.Empty;
        DataContext = this;
    }

    public ObservableCollection<ExportTrackRow> TrackRows { get; } = [];

    /// <summary>
    /// Result semantics: null means the dialog was cancelled; a non-null value means
    /// the user confirmed Review Plan. Avalonia has no DialogResult, so Close() is the signal.
    /// </summary>
    public MidiExportDialogOptions? Options { get; private set; }

    public enum ExportMode
    {
        WholeProject,
        PerLogicalTrack,
        PerPort
    }

    public enum ExportRouting
    {
        Compact,
        Preserve
    }

    public sealed record MidiExportDialogOptions(
        ExportMode Mode,
        ExportRouting Routing,
        string OutputDirectory,
        long StartTick,
        long? EndTick,
        bool IncludeReadme,
        bool TreatWarningsAsErrors,
        IReadOnlyList<MidoraId>? SelectedTrackIds);

    public sealed class ExportTrackRow
    {
        public ExportTrackRow(string name, bool isPureMidi, MidoraId stableId = default)
        {
            Name = name;
            IsPureMidi = isPureMidi;
            StableId = stableId;
        }

        public MidoraId StableId { get; }

        public string Name { get; }

        public bool IsPureMidi { get; }

        public bool IsSelected { get; set; } = true;
    }

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (RoutingBox is not null)
        {
            RoutingBox.IsEnabled = ModeBox.SelectedItem is not ExportMode.PerPort;
        }

        RefreshTrackRows();
    }

    private void RefreshTrackRows()
    {
        if (ModeBox is null || SelectedTracksCheck is null)
        {
            return;
        }

        bool logicalOnly = ModeBox.SelectedItem is ExportMode.PerLogicalTrack;
        SelectedTracksCheck.Content = logicalOnly
            ? "Use only the checked Logical Tracks"
            : "Use only the checked Tracks";
        TrackRows.Clear();
        foreach (ExportTrackRow row in _allTrackRows)
        {
            if (!logicalOnly || !row.IsPureMidi)
            {
                TrackRows.Add(row);
            }
        }
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select MIDI Export Directory",
                AllowMultiple = false
            });
            if (folders.Count > 0)
            {
                OutputDirectoryBox.Text = folders[0].Path.LocalPath;
            }
        }
        catch (Exception)
        {
            // The storage provider is platform dependent; browsing must never break the dialog.
        }
    }

    private void OnReviewClick(object? sender, RoutedEventArgs e)
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

        List<MidoraId>? selectedTrackIds = SelectedTracksCheck.IsChecked == true
            ? TrackRows.Where(item => item.IsSelected).Select(item => item.StableId).ToList()
            : null;
        if (selectedTrackIds is { Count: 0 })
        {
            ValidationText.Text = ModeBox.SelectedItem is ExportMode.PerLogicalTrack
                ? "Check at least one Logical Track, or disable explicit Track selection."
                : "Check at least one Track, or disable explicit Track selection.";
            return;
        }

        ExportMode mode = ModeBox.SelectedItem is ExportMode selectedMode
            ? selectedMode
            : ExportMode.WholeProject;
        ExportRouting routing = mode == ExportMode.PerPort
            ? ExportRouting.Preserve
            : RoutingBox.SelectedItem is ExportRouting selectedRouting
                ? selectedRouting
                : ExportRouting.Compact;
        Options = new MidiExportDialogOptions(
            mode,
            routing,
            OutputDirectoryBox.Text ?? string.Empty,
            start,
            end,
            ReadmeCheck.IsChecked == true,
            WarningsCheck.IsChecked == true,
            selectedTrackIds);
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
