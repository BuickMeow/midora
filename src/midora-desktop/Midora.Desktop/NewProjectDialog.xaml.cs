using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Midora.Application;

namespace Midora.Desktop;

public partial class NewProjectDialog : Window
{
    private static readonly Regex IntegerPattern = new("^[0-9]+$", RegexOptions.CultureInvariant);
    private readonly string? _initialDirectory;
    private readonly string? _initialSoundFontDirectory;

    public NewProjectDialog(
        string? initialDirectory = null,
        string? initialSoundFontDirectory = null,
        string? defaultEmbeddedSoundFontPath = null)
    {
        _initialDirectory = initialDirectory;
        _initialSoundFontDirectory = initialSoundFontDirectory;
        InitializeComponent();
        if (defaultEmbeddedSoundFontPath is not null)
        {
            SoundFontPathBox.Text = Path.GetFullPath(defaultEmbeddedSoundFontPath);
            SoundFontModeBox.SelectedIndex = (int)NewProjectSoundFontMode.Embedded;
        }
        UpdateSaveControls();
        UpdateSoundFontControls();
        Loaded += (_, _) =>
        {
            ProjectNameBox.Focus();
            ProjectNameBox.SelectAll();
        };
    }

    public NewProjectCreationRequest? Request { get; private set; }

    private void OnTitleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnSaveNowChanged(object sender, RoutedEventArgs e) => UpdateSaveControls();

    private void UpdateSaveControls()
    {
        bool enabled = SaveNowCheck?.IsChecked == true;
        if (PathBox is not null) PathBox.IsEnabled = enabled;
        if (BrowseButton is not null) BrowseButton.IsEnabled = enabled;
        if (ExternalSoundFontItem is not null) ExternalSoundFontItem.IsEnabled = enabled;
        if (SoundFontModeBox is not null)
        {
            NewProjectSoundFontMode coerced = CoerceSoundFontMode(
                enabled,
                SelectedSoundFontMode);
            if (coerced != SelectedSoundFontMode)
            {
                SoundFontModeBox.SelectedIndex = (int)coerced;
            }
        }
        UpdateSoundFontControls();
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        SaveFileDialog dialog = new()
        {
            Title = "Create Midora Project",
            Filter = "Midora Project (*.midora)|*.midora",
            AddExtension = true,
            DefaultExt = ".midora",
            InitialDirectory = _initialDirectory,
            FileName = string.IsNullOrWhiteSpace(ProjectNameBox.Text)
                ? "Untitled Project.midora"
                : $"{ProjectNameBox.Text}.midora"
        };
        if (dialog.ShowDialog(this) == true) PathBox.Text = dialog.FileName;
    }

    private void OnIntegerTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !IntegerPattern.IsMatch(e.Text);

    private void OnSoundFontModeChanged(object sender, RoutedEventArgs e)
    {
        if (SoundFontModeBox is null) return;
        NewProjectSoundFontMode coerced = CoerceSoundFontMode(
            SaveNowCheck?.IsChecked == true,
            SelectedSoundFontMode);
        if (coerced != SelectedSoundFontMode)
        {
            SoundFontModeBox.SelectedIndex = (int)coerced;
            return;
        }
        UpdateSoundFontControls();
    }

    private void UpdateSoundFontControls()
    {
        if (SoundFontModeBox is null
            || SoundFontPathBox is null
            || SoundFontBrowseButton is null
            || SoundFontHelpText is null)
        {
            return;
        }

        NewProjectSoundFontMode mode = SelectedSoundFontMode;
        bool hasSelection = mode != NewProjectSoundFontMode.None;
        SoundFontPathBox.IsEnabled = hasSelection;
        SoundFontBrowseButton.IsEnabled = hasSelection;
        SoundFontHelpText.Text = mode switch
        {
            NewProjectSoundFontMode.None =>
                "The Project remains editable and can export MIDI, but playback, preview, and audio rendering remain unavailable.",
            NewProjectSoundFontMode.Embedded =>
                "The selected SF2 is copied into the Project. Confirm that you may redistribute it before sharing the Project.",
            NewProjectSoundFontMode.ExternalRelative =>
                "The SF2 must be beside the target .midora file or directly inside its soundfonts folder.",
            _ => throw new InvalidOperationException("Unknown new Project SoundFont mode.")
        };
    }

    private void OnBrowseSoundFontClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSoundFontMode == NewProjectSoundFontMode.None) return;
        OpenFileDialog dialog = new()
        {
            Title = SelectedSoundFontMode == NewProjectSoundFontMode.Embedded
                ? "Select SoundFont to Embed"
                : "Select External Project SoundFont",
            Filter = "SoundFont 2 (*.sf2)|*.sf2|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = File.Exists(SoundFontPathBox.Text)
                ? Path.GetDirectoryName(SoundFontPathBox.Text)
                : _initialSoundFontDirectory
        };
        if (dialog.ShowDialog(this) == true) SoundFontPathBox.Text = dialog.FileName;
    }

    internal NewProjectSoundFontMode SelectedSoundFontMode =>
        SoundFontModeBox?.SelectedIndex switch
        {
            (int)NewProjectSoundFontMode.Embedded => NewProjectSoundFontMode.Embedded,
            (int)NewProjectSoundFontMode.ExternalRelative => NewProjectSoundFontMode.ExternalRelative,
            _ => NewProjectSoundFontMode.None
        };

    internal static NewProjectSoundFontMode CoerceSoundFontMode(
        bool saveImmediately,
        NewProjectSoundFontMode requested) =>
        !saveImmediately && requested == NewProjectSoundFontMode.ExternalRelative
            ? NewProjectSoundFontMode.Embedded
            : requested;

    private void OnCreateClick(object sender, RoutedEventArgs e)
    {
        if (!TryBuildRequest(out NewProjectCreationRequest? request)) return;
        Request = request;
        DialogResult = true;
    }

    internal bool TryBuildRequest(out NewProjectCreationRequest? request)
    {
        request = null;
        ValidationText.Text = string.Empty;
        if (!int.TryParse(TpqBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int tpq)
            || tpq is < 1 or > 32767)
        {
            ValidationText.Text = "Ticks per quarter note must be an integer from 1 through 32,767.";
            TpqBox.Focus();
            return false;
        }
        bool saveNow = SaveNowCheck.IsChecked == true;
        if (saveNow && string.IsNullOrWhiteSpace(PathBox.Text))
        {
            ValidationText.Text = "Select the target .midora file.";
            return false;
        }

        NewProjectSoundFontMode soundFontMode = SelectedSoundFontMode;
        string? soundFontPath = null;
        if (soundFontMode != NewProjectSoundFontMode.None)
        {
            if (string.IsNullOrWhiteSpace(SoundFontPathBox.Text)
                || !File.Exists(SoundFontPathBox.Text))
            {
                ValidationText.Text = "Select an existing SoundFont file.";
                return false;
            }
            soundFontPath = Path.GetFullPath(SoundFontPathBox.Text);
        }
        if (soundFontMode == NewProjectSoundFontMode.ExternalRelative)
        {
            if (!saveNow)
            {
                ValidationText.Text = "An External SoundFont requires Save Project immediately.";
                return false;
            }
            if (!IsAllowedExternalSoundFontLocation(PathBox.Text, soundFontPath!))
            {
                ValidationText.Text =
                    "An External SoundFont must be beside the target .midora file or directly inside its soundfonts folder.";
                return false;
            }
        }

        request = new NewProjectCreationRequest
        {
            TicksPerQuarterNote = tpq,
            ProjectName = ProjectNameBox.Text,
            ProjectVersion = VersionBox.Text,
            AuthorOrTeam = AuthorBox.Text,
            PersistenceMode = saveNow
                ? NewProjectPersistenceMode.CreateAndSave
                : NewProjectPersistenceMode.CreateUnsaved,
            TargetPath = saveNow ? PathBox.Text : null,
            OverwriteAuthorized = saveNow && File.Exists(PathBox.Text),
            SoundFont = soundFontMode == NewProjectSoundFontMode.None
                ? NewProjectSoundFontSelection.NoSoundFont
                : new NewProjectSoundFontSelection(soundFontMode, soundFontPath)
        };
        return true;
    }

    internal static bool IsAllowedExternalSoundFontLocation(
        string projectPath,
        string soundFontPath)
    {
        string projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))
            ?? throw new ArgumentException("The target Project path has no directory.", nameof(projectPath));
        string soundFontDirectory = Path.GetDirectoryName(Path.GetFullPath(soundFontPath))
            ?? throw new ArgumentException("The SoundFont path has no directory.", nameof(soundFontPath));
        if (PathsEqual(projectDirectory, soundFontDirectory)) return true;
        DirectoryInfo? parent = Directory.GetParent(soundFontDirectory);
        return parent is not null
            && PathsEqual(projectDirectory, parent.FullName)
            && string.Equals(
                Path.GetFileName(soundFontDirectory),
                "soundfonts",
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
