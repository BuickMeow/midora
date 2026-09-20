using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Midora.Avalonia.Windows;

public partial class NewProjectDialog : Window
{
    private static readonly Regex IntegerPattern = new("^[0-9]+$", RegexOptions.CultureInvariant);

    public NewProjectDialog()
    {
        InitializeComponent();
        UpdateSaveControls();
    }

    /// <summary>
    /// Result semantics: null means the dialog was cancelled; a non-null value means
    /// the user confirmed Create. Avalonia has no DialogResult, so Close() is the signal.
    /// </summary>
    public NewProjectDialogRequest? Result { get; private set; }

    public sealed record NewProjectDialogRequest(
        int TicksPerQuarterNote,
        string ProjectName,
        string ProjectVersion,
        string AuthorOrTeam,
        bool CreateAndSave,
        string? TargetPath,
        bool OverwriteAuthorized);

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _ = ProjectNameBox.Focus();
        ProjectNameBox.SelectAll();
    }

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnSaveNowChanged(object? sender, RoutedEventArgs e) => UpdateSaveControls();

    private void UpdateSaveControls()
    {
        bool enabled = SaveNowCheck.IsChecked == true;
        PathBox.IsEnabled = enabled;
        BrowseButton.IsEnabled = enabled;
    }

    private void OnIntegerTextInput(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || !IntegerPattern.IsMatch(e.Text))
        {
            e.Handled = true;
        }
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            string suggested = string.IsNullOrWhiteSpace(ProjectNameBox.Text)
                ? "Untitled Project.midora"
                : $"{ProjectNameBox.Text}.midora";
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Create Midora Project",
                SuggestedFileName = suggested,
                DefaultExtension = "midora",
                ShowOverwritePrompt = true,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Midora Project (*.midora)")
                    {
                        Patterns = new[] { "*.midora" }
                    }
                }
            });
            if (file is not null)
            {
                PathBox.Text = file.Path.LocalPath;
            }
        }
        catch (Exception)
        {
            // The storage provider is platform dependent; browsing must never break the dialog.
        }
    }

    private void OnCreateClick(object? sender, RoutedEventArgs e)
    {
        if (!TryBuildRequest(out NewProjectDialogRequest? request))
        {
            return;
        }

        Result = request;
        Close();
    }

    private bool TryBuildRequest(out NewProjectDialogRequest? request)
    {
        request = null;
        ValidationText.Text = string.Empty;
        if (!int.TryParse(TpqBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int tpq)
            || tpq is < 1 or > 32767)
        {
            ValidationText.Text = "Ticks per quarter note must be an integer from 1 through 32,767.";
            _ = TpqBox.Focus();
            return false;
        }

        bool saveNow = SaveNowCheck.IsChecked == true;
        if (saveNow && string.IsNullOrWhiteSpace(PathBox.Text))
        {
            ValidationText.Text = "Select the target .midora file.";
            return false;
        }

        request = new NewProjectDialogRequest(
            tpq,
            ProjectNameBox.Text ?? string.Empty,
            VersionBox.Text ?? string.Empty,
            AuthorBox.Text ?? string.Empty,
            saveNow,
            saveNow ? PathBox.Text : null,
            saveNow && File.Exists(PathBox.Text));
        return true;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
