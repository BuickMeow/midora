using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Midora.Avalonia.Import;
using Midora.Avalonia.Platform;
using Midora.Avalonia.Session;
using Midora.Avalonia.Windows;

namespace Midora.Avalonia;

public partial class MainWindow : Window
{
    private const double TitleBarHeight = 36;

    public MainWindow()
    {
        InitializeComponent();
        Session = new ShellSession();
        DataContext = Session;
        ApplyPlatformChrome();
        PopulateWindowsMenu();
        WorkspaceTabs.SelectionChanged += (_, _) =>
            Session.ActivateFromUi(WorkspaceTabs.SelectedItem as WorkspaceTab);
    }

    internal ShellSession Session { get; }

    /// <summary>Review-only entry point used by the <c>MIDORA_MIDI_OPEN</c> env var.</summary>
    internal void OpenMidiForReview(string path)
    {
        try
        {
            var project = ImportedMidiProject.Parse(path);
            Session.CreateProjectFromMidi(
                System.IO.Path.GetFileNameWithoutExtension(path),
                project);
        }
        catch (Exception ex)
        {
            Session.SetStatus($"MIDI open failed: {ex.Message}");
        }
    }

    /// <summary>Review-only entry point used by the <c>MIDORA_OPEN_TRACK</c> env var.</summary>
    internal void OpenTrackForReview(int trackIndex) =>
        Session.OpenMidiTrackWorkspace(trackIndex);

    /// <summary>Review-only entry point used by the <c>MIDORA_OPEN_SEGMENT</c> env var.</summary>
    internal void OpenSegmentForReview(int trackIndex, long startTick) =>
        Session.OpenMidiSegmentWorkspace(trackIndex, startTick);

    /// <summary>Review-only entry point used by the <c>MIDORA_AUTOPLAY</c> env var.</summary>
    internal void StartPlaybackForReview() => Session.TogglePlayback();

    /// <summary>Review-only entry point used by the <c>MIDORA_TRACK_MODE</c> env var.</summary>
    internal void SetTrackModeForReview(string mode) => Session.SetActiveEditMode(mode);

    /// <summary>Review-only entry point used by the <c>MIDORA_NEW_PROJECT</c> env var.</summary>
    internal void NewProjectForReview() => Session.CreateProject("Untitled Project");

    // ---- Platform chrome -------------------------------------------------

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        RecenterTrafficLights();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            RecenterTrafficLights();
        }
    }

    private void ApplyPlatformChrome()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        ExtendClientAreaChromeHints = global::Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome;
        ExtendClientAreaTitleBarHeightHint = TitleBarHeight;
        CaptionButtons.IsVisible = false;
        TitleBarContent.Margin = new Thickness(72, 0, 0, 0);
    }

    private void RecenterTrafficLights()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var handle = TryGetPlatformHandle();
        if (handle is not null)
        {
            MacWindowChrome.CenterTrafficLights(handle.Handle, TitleBarHeight);
        }
    }

    // ---- Title bar / window commands ------------------------------------

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    private void OnDismissNoticeClick(object? sender, RoutedEventArgs e) => Session.DismissNotice();

    private async void OnViewStatusMessageClick(object? sender, RoutedEventArgs e)
    {
        if (Session.StatusMessageDetails is { Length: > 0 } details)
        {
            await new TextDetailsDialog(
                Session.StatusMessageDetailsTitle ?? "Details",
                details).ShowDialog(this);
            return;
        }

        if (!string.IsNullOrEmpty(Session.StatusMessage))
        {
            await MessageDialog.ShowAsync(
                this,
                Session.StatusMessage,
                Session.StatusMessageDetailsTitle ?? "Details");
        }
    }

    private void OnDismissStatusMessageClick(object? sender, RoutedEventArgs e) =>
        Session.DismissStatusMessage();

    private void OnOpenDiagnosticsPointerReleased(object? sender, PointerReleasedEventArgs e) =>
        Session.OpenWorkspace(WorkspaceKind.Diagnostics);

    private void OnSoundFontStatusPointerReleased(object? sender, PointerReleasedEventArgs e) =>
        OnApplicationPreferencesClick(sender, e);

    // ---- File menu / command bar ----------------------------------------

    private async void OnNewProjectClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new NewProjectDialog();
        await dialog.ShowDialog(this);
        if (dialog.Result is { } request)
        {
            Session.CreateProject(request.ProjectName);
        }
    }

    private async void OnOpenProjectClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Project",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Midora Project") { Patterns = ["*.midora"] }]
        });

        if (files.Count > 0)
        {
            Session.CreateProject(System.IO.Path.GetFileNameWithoutExtension(files[0].Name));
            Session.SetStatus("Project opened (port placeholder; persistence is not wired yet).");
        }
    }

    private async void OnOpenMidiAsNewProjectClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open MIDI as New Project",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Standard MIDI File") { Patterns = ["*.mid", "*.midi"] }]
        });

        if (files.Count == 0)
        {
            return;
        }

        string? path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            await MessageDialog.ShowAsync(
                this,
                "The selected MIDI file is not a local file.",
                "Open MIDI as New Project",
                MessageDialogButtons.Ok,
                MessageDialogIcon.Warning);
            return;
        }

        try
        {
            var project = ImportedMidiProject.Parse(path);
            Session.CreateProjectFromMidi(
                System.IO.Path.GetFileNameWithoutExtension(files[0].Name),
                project);
        }
        catch (Exception ex)
        {
            await MessageDialog.ShowAsync(
                this,
                $"Could not import the MIDI file.\n\n{ex.Message}",
                "Open MIDI as New Project",
                MessageDialogButtons.Ok,
                MessageDialogIcon.Error);
        }
    }

    private async void OnSaveProjectClick(object? sender, RoutedEventArgs e)
    {
        await MessageDialog.ShowAsync(
            this,
            "Saving .midora files is not wired in the Avalonia port yet.",
            "Save Project");
        Session.SetStatus("Save requested (persistence is not wired yet).");
    }

    private async void OnSaveCopyClick(object? sender, RoutedEventArgs e)
    {
        await MessageDialog.ShowAsync(
            this,
            "Save Copy is not wired in the Avalonia port yet.",
            "Save Copy");
        Session.SetStatus("Save Copy requested (persistence is not wired yet).");
    }

    private async void OnCloseProjectClick(object? sender, RoutedEventArgs e)
    {
        if (!Session.HasProject)
        {
            return;
        }

        if (Session.IsModified)
        {
            var result = await MessageDialog.ShowAsync(
                this,
                "Close the Project and discard unsaved changes?",
                "Close Project",
                MessageDialogButtons.YesNo,
                MessageDialogIcon.Warning);
            if (result != MessageDialogResult.Yes)
            {
                return;
            }
        }

        Session.CloseProject();
    }

    // ---- Edit menu -------------------------------------------------------

    private void OnUndoClick(object? sender, RoutedEventArgs e) =>
        Session.SetStatus(Session.UndoActive()
            ? "Undo."
            : "Nothing to undo in the active editor.");

    private void OnRedoClick(object? sender, RoutedEventArgs e) =>
        Session.SetStatus(Session.RedoActive()
            ? "Redo."
            : "Nothing to redo in the active editor.");

    private void OnCutClick(object? sender, RoutedEventArgs e) =>
        Session.SetStatus("Cut is not wired yet (Selection/Clipboard arrive with the presentation core).");

    private void OnCopyClick(object? sender, RoutedEventArgs e) =>
        Session.SetStatus("Copy is not wired yet (Selection/Clipboard arrive with the presentation core).");

    private void OnPasteClick(object? sender, RoutedEventArgs e) =>
        Session.SetStatus("Paste is not wired yet (Selection/Clipboard arrive with the presentation core).");

    private void OnSelectAllClick(object? sender, RoutedEventArgs e) =>
        Session.SetStatus("Select All is not wired yet (Selection arrives with the presentation core).");

    private void OnDuplicateClick(object? sender, RoutedEventArgs e) =>
        Session.SetStatus("Duplicate is not wired yet (Selection arrives with the presentation core).");

    // ---- View menu / navigation -----------------------------------------

    private void OnOpenArrangementClick(object? sender, RoutedEventArgs e) =>
        Session.OpenWorkspace(WorkspaceKind.Arrangement);

    private void OnOpenAllTracksClick(object? sender, RoutedEventArgs e) =>
        Session.OpenWorkspace(WorkspaceKind.AllTracks);

    private void OnOpenDiagnosticsClick(object? sender, RoutedEventArgs e) =>
        Session.OpenWorkspace(WorkspaceKind.Diagnostics);

    private void OnNavigateBackClick(object? sender, RoutedEventArgs e) => Session.NavigateBack();

    private void OnNavigateForwardClick(object? sender, RoutedEventArgs e) => Session.NavigateForward();

    private void OnCloseWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: WorkspaceTab tab })
        {
            Session.CloseWorkspace(tab);
        }
    }

    // ---- Project menu ----------------------------------------------------

    private void OnNewInstrumentClick(object? sender, RoutedEventArgs e) =>
        Session.SetStatus("New Event Instrument is not wired yet.");

    private void OnNewTrackClick(object? sender, RoutedEventArgs e) =>
        Session.SetStatus("New Logical Track is not wired yet.");

    private async void OnNewTrackWithInstrumentClick(object? sender, RoutedEventArgs e)
    {
        await new NewLogicalTrackWithInstrumentDialog().ShowDialog(this);
        Session.SetStatus("New Logical Track with Instrument requested (not wired yet).");
    }

    private async void OnNewRawMidiTrackClick(object? sender, RoutedEventArgs e)
    {
        await new NewRawMidiTrackDialog().ShowDialog(this);
        Session.SetStatus("New Raw MIDI Track requested (not wired yet).");
    }

    private void OnProjectSettingsClick(object? sender, RoutedEventArgs e) =>
        Session.SetStatus("Project Settings is not wired yet.");

    // ---- Playback / Compile / Export ------------------------------------

    private void OnPlayClick(object? sender, RoutedEventArgs e) => Session.TogglePlayback();

    private void OnStopClick(object? sender, RoutedEventArgs e) => Session.StopPlayback();

    private void OnPrimaryTransportClick(object? sender, RoutedEventArgs e) => Session.TogglePlayback();

    private async void OnResetPlaybackClick(object? sender, RoutedEventArgs e)
    {
        await MessageDialog.ShowAsync(
            this,
            "Resetting the playback engine is not wired in the Avalonia port yet.",
            "Reset Playback Engine");
        Session.SetStatus("Reset Playback Engine requested (not wired yet).");
    }

    private async void OnCompileClick(object? sender, RoutedEventArgs e) =>
        await Session.CompileAsync();

    private async void OnMidiExportClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new MidiExportDialog();
        await dialog.ShowDialog(this);
        if (dialog.Options is not null)
        {
            Session.SetStatus("MIDI export confirmed (encoding is not wired yet).");
        }
    }

    private async void OnAudioRenderClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new AudioRenderDialog();
        await dialog.ShowDialog(this);
        if (dialog.Options is not null)
        {
            Session.SetStatus("Audio render confirmed (rendering is not wired yet).");
        }
    }

    // ---- Application menu ------------------------------------------------

    private async void OnApplicationPreferencesClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new ApplicationPreferencesDialog();
        await dialog.ShowDialog(this);
        if (dialog.Result is not null)
        {
            Session.SetSoundFontState("1 SoundFont Enabled (placeholder)");
            Session.SetStatus("Application Preferences saved (in-memory placeholder).");
        }
    }

    private async void OnInstrumentCatalogsClick(object? sender, RoutedEventArgs e) =>
        await new InstrumentCatalogDialog().ShowDialog(this);

    private async void OnAboutClick(object? sender, RoutedEventArgs e) =>
        await new AboutDialog().ShowDialog(this);

    // ---- Temporary Windows catalog menu ---------------------------------

    private void PopulateWindowsMenu()
    {
        foreach (var group in WindowCatalog.Entries.GroupBy(entry => entry.Group))
        {
            var groupItem = new MenuItem { Header = group.Key };
            foreach (var entry in group)
            {
                var item = new MenuItem { Header = entry.Name };
                item.Click += async (_, _) => await ShowCatalogWindowAsync(entry);
                groupItem.Items.Add(item);
            }

            WindowsMenu.Items.Add(groupItem);
        }
    }

    private async Task ShowCatalogWindowAsync(WindowCatalog.Entry entry)
    {
        try
        {
            await entry.Factory().ShowDialog(this);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Window '{entry.Name}' failed: {ex}");
        }
    }

    // ---- Startup probes ---------------------------------------------------

    internal async Task<int> RunWindowSmokeAsync()
    {
        var failures = new List<string>();
        foreach (var entry in WindowCatalog.Entries)
        {
            Window? window = null;
            try
            {
                window = entry.Factory();
                window.Show();
                await Task.Delay(40);
                window.Close();
            }
            catch (Exception ex)
            {
                failures.Add($"{entry.Name}: {ex.GetType().Name}: {ex.Message}");
                try
                {
                    window?.Close();
                }
                catch
                {
                    // The window never became usable; nothing else to release.
                }
            }
        }

        Console.Error.WriteLine(
            $"WINDOW-SMOKE total={WindowCatalog.Entries.Count} failures={failures.Count}");
        foreach (var failure in failures)
        {
            Console.Error.WriteLine("FAIL " + failure);
        }

        return failures.Count == 0 ? 0 : 2;
    }

    internal async Task<int> RunShellSmokeAsync()
    {
        var failures = new List<string>();

        void Step(string name, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Step("create-project", () => Session.CreateProject("Smoke Project"));

        var midiPath = Environment.GetEnvironmentVariable("MIDORA_MIDI_SMOKE");
        if (!string.IsNullOrEmpty(midiPath) && File.Exists(midiPath))
        {
            try
            {
                var imported = ImportedMidiProject.Parse(midiPath);
                Session.CreateProjectFromMidi(
                    System.IO.Path.GetFileNameWithoutExtension(midiPath),
                    imported);
                if (!Session.HasProject || Session.Workspaces.Count == 0)
                {
                    failures.Add("midi-import: imported project did not open a workspace");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"midi-import: {ex.GetType().Name}: {ex.Message}");
            }
        }
        Step("open-arrangement", () => Session.OpenWorkspace(WorkspaceKind.Arrangement));
        Step("segment-preview-content", () =>
        {
            if (Session.MidiSource is not { } source)
            {
                return;
            }

            var scratch = new List<Presentation.Rendering.TimelineRenderItem>();
            source.QueryInto(0, long.MaxValue / 2, 2, 3, scratch);
            var segment = scratch.FirstOrDefault(
                item => item.Kind == Presentation.Rendering.TimelineItemKind.Segment);
            if (segment.Id.Value == 0)
            {
                throw new InvalidOperationException("No arrangement segment found on lane 2.");
            }

            var preview = source.GetPreviewSource(segment);
            if (!preview.HasNoteContent)
            {
                throw new InvalidOperationException(
                    $"Segment preview on lane 2 has no notes (segment {segment.StartTick}..{segment.EndTick}).");
            }
        });
        Step("open-midi-track", () => Session.OpenMidiTrackWorkspace(0));
        Step("edit-note", () =>
        {
            if (Session.EditableProject is not { } editable)
            {
                return;
            }

            var note = editable.AddNote(0, 0, 64, 120, 100);
            editable.TransformNotes(0, [note.Id], 240, 1);
            if (!editable.CanUndo)
            {
                throw new InvalidOperationException("Undo stack is empty after an edit.");
            }

            editable.Undo();
            editable.Redo();
            editable.SetVelocity(0, [note.Id], 90);
        });
        Step("undo-redo-active", () =>
        {
            if (Session.EditableProject is null)
            {
                return;
            }

            if (!Session.UndoActive())
            {
                throw new InvalidOperationException("Active editor undo failed.");
            }

            Session.RedoActive();
        });
        Step("coalesced-drag-undo", () =>
        {
            if (Session.EditableProject is not { } editable)
            {
                return;
            }

            var note = editable.AddNote(0, 480, 62, 240, 100);
            editable.BeginTransaction();
            editable.TransformNotes(0, [note.Id], 120, 1);
            editable.TransformNotes(0, [note.Id], 120, 1);
            editable.EndTransaction();
            editable.Undo();
            var restored = editable.Tracks[0].Notes.FirstOrDefault(candidate => candidate.Id == note.Id);
            if (restored is null || restored.StartTick != 480 || restored.Key != 62)
            {
                throw new InvalidOperationException(
                    $"Coalesced undo mismatch: {restored?.StartTick}/{restored?.Key}.");
            }
        });
        Step("open-diagnostics", () => Session.OpenWorkspace(WorkspaceKind.Diagnostics));
        Step("open-all-tracks", () => Session.OpenWorkspace(WorkspaceKind.AllTracks));
        Step("navigate-back", Session.NavigateBack);
        Step("navigate-forward", Session.NavigateForward);
        Step("play", Session.TogglePlayback);
        Step("stop", Session.StopPlayback);
        Step("mark-modified", Session.MarkModified);

        try
        {
            await Session.CompileAsync();
        }
        catch (Exception ex)
        {
            failures.Add($"compile: {ex.GetType().Name}: {ex.Message}");
        }

        Step("close-workspace", () =>
        {
            if (Session.Workspaces.Count > 0)
            {
                Session.CloseWorkspace(Session.Workspaces[^1]);
            }
        });
        Step("close-project", Session.CloseProject);

        Console.Error.WriteLine($"SHELL-SMOKE failures={failures.Count}");
        foreach (var failure in failures)
        {
            Console.Error.WriteLine("FAIL " + failure);
        }

        return failures.Count == 0 ? 0 : 3;
    }
}
