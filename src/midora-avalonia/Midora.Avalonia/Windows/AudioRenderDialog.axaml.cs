using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Midora.Avalonia.Windows;

public partial class AudioRenderDialog : Window
{
    private const int MinimumSampleRate = 8_000;
    private const int MaximumSampleRate = 192_000;
    private const int MinimumSampleVoices = 1;
    private const int MaximumSampleVoices = 16_777_216;
    private readonly string _suggestedWholeMixName;
    private readonly string _initialDirectory;
    private readonly List<RenderTrackRow> _allTrackRows = [];

    public AudioRenderDialog()
        : this(null)
    {
    }

    public AudioRenderDialog(string? initialDirectory = null, string suggestedWholeMixName = "Whole Mix.wav")
    {
        InitializeComponent();
        _initialDirectory = initialDirectory ?? string.Empty;
        _suggestedWholeMixName = suggestedWholeMixName;
        _allTrackRows.Add(new("Logical · Piano", isPureMidi: false));
        _allTrackRows.Add(new("Logical · Strings", isPureMidi: false));
        _allTrackRows.Add(new("Logical · Drums", isPureMidi: false));
        _allTrackRows.Add(new("MIDI · Conductor Sketch", isPureMidi: true));
        _allTrackRows.Add(new("MIDI · External Hardware", isPureMidi: true));
        ModeBox.ItemsSource = Enum.GetValues<RenderMode>();
        ModeBox.SelectedItem = RenderMode.WholeMix;
        SampleRateBox.Text = 48_000.ToString(CultureInfo.InvariantCulture);
        VoicesBox.Text = 500.ToString(CultureInfo.InvariantCulture);
        StartTickBox.Text = "0";
        EndTickBox.Text = string.Empty;
        SelectedTracksCheck.IsChecked = false;
        RefreshTrackRows(RenderMode.WholeMix);
        OutputPathBox.Text = Path.Combine(_initialDirectory, _suggestedWholeMixName);
        DataContext = this;
    }

    public ObservableCollection<RenderTrackRow> TrackRows { get; } = [];

    /// <summary>
    /// Result semantics: null means the dialog was cancelled; a non-null value means
    /// the user confirmed Review Plan. Avalonia has no DialogResult, so Close() is the signal.
    /// </summary>
    public AudioRenderDialogOptions? Options { get; private set; }

    public enum RenderMode
    {
        WholeMix,
        PerLogicalTrack
    }

    public sealed record AudioRenderDialogOptions(
        RenderMode Mode,
        string OutputPath,
        long StartTick,
        long? EndTick,
        int SampleRate,
        int SampleVoicesPerUnitStream,
        bool TreatWarningsAsErrors,
        IReadOnlyList<string>? SelectedTrackNames);

    public sealed class RenderTrackRow
    {
        public RenderTrackRow(string name, bool isPureMidi)
        {
            Name = name;
            IsPureMidi = isPureMidi;
        }

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
        if (OutputPathBox is null || ModeBox.SelectedItem is not RenderMode mode)
        {
            return;
        }

        bool wholeMix = mode == RenderMode.WholeMix;
        string current = OutputPathBox.Text ?? string.Empty;
        string directory = wholeMix
            ? Path.GetDirectoryName(current) ?? string.Empty
            : File.Exists(current)
                ? Path.GetDirectoryName(current) ?? string.Empty
                : current;
        OutputPathBox.Text = wholeMix
            ? Path.Combine(directory, _suggestedWholeMixName)
            : directory;
        RefreshTrackRows(mode);
    }

    private void RefreshTrackRows(RenderMode mode)
    {
        TrackRows.Clear();
        foreach (RenderTrackRow row in _allTrackRows)
        {
            if (mode == RenderMode.WholeMix || !row.IsPureMidi)
            {
                TrackRows.Add(row);
            }
        }
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (ModeBox.SelectedItem is RenderMode.WholeMix)
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Select Whole Mix WAVE Target",
                    SuggestedFileName = _suggestedWholeMixName,
                    DefaultExtension = "wav",
                    ShowOverwritePrompt = true,
                    FileTypeChoices = new[]
                    {
                        new FilePickerFileType("WAVE audio (*.wav)")
                        {
                            Patterns = new[] { "*.wav" }
                        }
                    }
                });
                if (file is not null)
                {
                    OutputPathBox.Text = file.Path.LocalPath;
                }
            }
            else
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Select Per Logical Track Audio Directory",
                    AllowMultiple = false
                });
                if (folders.Count > 0)
                {
                    OutputPathBox.Text = folders[0].Path.LocalPath;
                }
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
        if (string.IsNullOrWhiteSpace(OutputPathBox.Text))
        {
            ValidationText.Text = "Select an output target.";
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
            if (!long.TryParse(EndTickBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out long value)
                || value <= start)
            {
                ValidationText.Text = "End tick must be an integer greater than start tick.";
                return;
            }

            end = value;
        }

        if (!int.TryParse(SampleRateBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int sampleRate)
            || sampleRate is < MinimumSampleRate or > MaximumSampleRate)
        {
            ValidationText.Text = "Sample rate must be an integer from 8,000 through 192,000 Hz.";
            return;
        }

        if (!int.TryParse(VoicesBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int voices)
            || voices is < MinimumSampleVoices or > MaximumSampleVoices)
        {
            ValidationText.Text = "Maximum sample voices must be an integer from 1 through 16,777,216.";
            return;
        }

        RenderMode mode = ModeBox.SelectedItem is RenderMode selectedMode
            ? selectedMode
            : RenderMode.WholeMix;
        List<string>? selectedTrackNames = SelectedTracksCheck.IsChecked == true
            ? TrackRows
                .Where(item => item.IsSelected && (mode == RenderMode.WholeMix || !item.IsPureMidi))
                .Select(item => item.Name)
                .ToList()
            : null;
        if (selectedTrackNames is { Count: 0 })
        {
            ValidationText.Text = mode == RenderMode.WholeMix
                ? "Check at least one Logical or Pure MIDI Track, or disable explicit Track selection."
                : "Check at least one Logical Track, or disable explicit Track selection.";
            return;
        }

        Options = new AudioRenderDialogOptions(
            mode,
            OutputPathBox.Text ?? string.Empty,
            start,
            end,
            sampleRate,
            voices,
            WarningsCheck.IsChecked == true,
            selectedTrackNames);
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
