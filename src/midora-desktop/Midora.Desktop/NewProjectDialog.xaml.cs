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

    public NewProjectDialog(string? initialDirectory = null)
    {
        _initialDirectory = initialDirectory;
        InitializeComponent();
        UpdateSaveControls();
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
            OverwriteAuthorized = saveNow && File.Exists(PathBox.Text)
        };
        return true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
