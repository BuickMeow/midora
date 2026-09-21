using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Midora.Avalonia.Import;
using Midora.Avalonia.Session;
using Midora.Avalonia.Windows;
using Midora.Application;
using Midora.Domain;
using Midora.Midi;
using Midora.MidiExport;
using Midora.Session;

namespace Midora.Avalonia;

public partial class MainWindow : Window
{
    /// <summary>
    /// Title bar height: macOS keeps the native 28 pt bar so AppKit centers the traffic
    /// lights itself (no interop, no layout races); Windows keeps the WPF baseline 36.
    /// </summary>
    private double TitleBarHeight => OperatingSystem.IsMacOS() ? 28d : 36d;

    public MainWindow()
        : this(preferences: null)
    {
    }

    public MainWindow(ApplicationPreferences? preferences)
    {
        InitializeComponent();
        Session = new ShellSession(preferences);
        DataContext = Session;
        ApplyPlatformChrome();
        PopulateWindowsMenu();
        RefreshSoundFontState();
        // Space is reserved for Play/Stop everywhere except while editing text, so it must be
        // handled during tunneling before a focused button can consume it (SRS 20.1.5).
        AddHandler(KeyDownEvent, OnWindowPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnWindowPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || e.KeyModifiers != KeyModifiers.None || IsTextEditingFocused())
        {
            return;
        }

        e.Handled = true;
        Session.TogglePlayback();
    }

    /// <summary>True when the keyboard focus is inside a text-editing control.</summary>
    private bool IsTextEditingFocused()
    {
        if (FocusManager?.GetFocusedElement() is not Control focused)
        {
            return false;
        }

        for (Control? current = focused; current is not null; current = current.Parent as Control)
        {
            if (current is TextBox or NumericUpDown)
            {
                return true;
            }

            if (current is ComboBox { IsEditable: true })
            {
                return true;
            }
        }

        return false;
    }

    private void RefreshSoundFontState()
    {
        if (Session.Preferences is not { } preferences)
        {
            return;
        }

        int enabled = preferences.SoundFonts.Count(soundFont => soundFont.Enabled);
        Session.SetSoundFontState(enabled == 1
            ? "1 SoundFont Enabled"
            : $"{enabled} SoundFonts Enabled");
    }

    private void OnWorkspaceTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: WorkspaceTab tab }
            && e.GetCurrentPoint(sender as Control).Properties.IsLeftButtonPressed)
        {
            Session.ActivateFromUi(tab);
        }
    }

    internal ShellSession Session { get; }

    /// <summary>Review-only entry point used by the <c>MIDORA_MIDI_OPEN</c> env var.</summary>
    internal void OpenMidiForReview(string path)
    {
        try
        {
            bool trace = Environment.GetEnvironmentVariable("MIDORA_IMPORT_TRACE") == "1";
            long started = Environment.TickCount64;
            byte[] bytes = File.ReadAllBytes(path);
            if (trace)
            {
                Console.Out.WriteLine(
                    $"MIDORA-IMPORT read={Environment.TickCount64 - started} ms bytes={bytes.Length}");
                Console.Out.Flush();
            }

            var project = ImportedMidiProject.Parse(bytes, System.IO.Path.GetFileName(path));
            if (trace)
            {
                Console.Out.WriteLine(
                    $"MIDORA-IMPORT parse={Environment.TickCount64 - started} ms");
                Console.Out.Flush();
            }

            Session.CreateProjectFromMidi(
                System.IO.Path.GetFileNameWithoutExtension(path),
                project,
                bytes);
            if (trace)
            {
                Console.Out.WriteLine(
                    $"MIDORA-IMPORT session={Environment.TickCount64 - started} ms");
                Console.Out.Flush();
            }
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

    /// <summary>
    /// Review-only entry point used by the <c>MIDORA_DIAGNOSTICS</c> env var: compiles the
    /// current Project and opens the Diagnostics workspace.
    /// </summary>
    internal async Task ShowDiagnosticsForReviewAsync()
    {
        await Session.CompileAsync();
        Session.OpenWorkspace(WorkspaceKind.Diagnostics);
    }

    // ---- Platform chrome -------------------------------------------------

    private void ApplyPlatformChrome()
    {
        // The custom title bar row must match the extended title bar hint exactly;
        // otherwise the native traffic lights sit off-center in the custom bar.
        RootLayout.RowDefinitions[0].Height = new GridLength(TitleBarHeight);
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        // Avalonia 12 replaced ExtendClientAreaChromeHints with WindowDecorations. Full keeps
        // the native title bar and traffic lights, and is also what makes the platform report
        // the extended title bar margin the custom 28 pt row relies on.
        WindowDecorations = WindowDecorations.Full;
        ExtendClientAreaTitleBarHeightHint = TitleBarHeight;
        CaptionButtons.IsVisible = false;
        TitleBarContent.Margin = new Thickness(72, 0, 0, 0);
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
            await Session.CreateProjectAsync(new Midora.Application.NewProjectCreationRequest
            {
                TicksPerQuarterNote = request.TicksPerQuarterNote,
                ProjectName = request.ProjectName,
                ProjectVersion = request.ProjectVersion,
                AuthorOrTeam = request.AuthorOrTeam,
                PersistenceMode = request.CreateAndSave
                    ? Midora.Application.NewProjectPersistenceMode.CreateAndSave
                    : Midora.Application.NewProjectPersistenceMode.CreateUnsaved,
                TargetPath = request.TargetPath,
                OverwriteAuthorized = request.OverwriteAuthorized
            });
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

        if (files.Count > 0 && files[0].TryGetLocalPath() is { Length: > 0 } path)
        {
            await Session.OpenProjectAsync(path);
        }
    }

    /// <summary>Picks the first-save target and confirms an existing file before overwriting.</summary>
    private async Task<string?> PickSaveTargetAsync(string title, string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = "midora",
            FileTypeChoices = [new FilePickerFileType("Midora Project") { Patterns = ["*.midora"] }]
        });

        string? path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        if (File.Exists(path))
        {
            var confirm = await MessageDialog.ShowAsync(
                this,
                $"{System.IO.Path.GetFileName(path)} already exists. Overwrite it?",
                title,
                MessageDialogButtons.YesNo,
                MessageDialogIcon.Warning);
            if (confirm != MessageDialogResult.Yes)
            {
                return null;
            }
        }

        return path;
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
            byte[] bytes = await File.ReadAllBytesAsync(path);
            var project = ImportedMidiProject.Parse(bytes, System.IO.Path.GetFileName(path));
            Session.CreateProjectFromMidi(
                System.IO.Path.GetFileNameWithoutExtension(files[0].Name),
                project,
                bytes);
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
        if (Session.CurrentProjectPath is { } current)
        {
            await Session.SaveProjectAsync(overwriteAuthorized: true);
            return;
        }

        string? path = await PickSaveTargetAsync("Save Project", $"{Session.ProjectName}.midora");
        if (path is not null)
        {
            await Session.SaveProjectAsync(path, overwriteAuthorized: true);
        }
    }

    private async void OnSaveCopyClick(object? sender, RoutedEventArgs e)
    {
        string suggested = Session.CurrentProjectPath is { } current
            ? $"{System.IO.Path.GetFileNameWithoutExtension(current)} copy.midora"
            : $"{Session.ProjectName}.midora";
        string? path = await PickSaveTargetAsync("Save Copy", suggested);
        if (path is not null)
        {
            await Session.SaveCopyAsync(path, overwriteAuthorized: true);
        }
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
        if (!Session.HasProjectSession || Session.DomainProject is not { } project)
        {
            Session.SetStatus("MIDI Export needs a Project-backed session.");
            return;
        }

        var trackRows = new List<MidiExportDialog.ExportTrackRow>();
        foreach (LogicalTrack track in project.Tracks)
        {
            trackRows.Add(new(track.Name, isPureMidi: false, track.Id));
        }

        foreach (PureMidiTrack track in project.PureMidiTracks)
        {
            trackRows.Add(new(track.Name, isPureMidi: true, track.Id));
        }

        var dialog = new MidiExportDialog(initialDirectory: null, trackRows);
        await dialog.ShowDialog(this);
        if (dialog.Options is not { } options)
        {
            return;
        }

        try
        {
            MidiExportSessionOptions exportOptions = new(
                options.Mode switch
                {
                    MidiExportDialog.ExportMode.PerLogicalTrack => MidiExportMode.PerLogicalTrack,
                    MidiExportDialog.ExportMode.PerPort => MidiExportMode.PerPort,
                    _ => MidiExportMode.WholeProject
                },
                options.Routing == MidiExportDialog.ExportRouting.Preserve
                    ? MidiExportRoutingStrategy.Preserve
                    : MidiExportRoutingStrategy.Compact,
                options.OutputDirectory,
                options.StartTick,
                options.EndTick,
                options.IncludeReadme,
                options.TreatWarningsAsErrors,
                options.SelectedTrackIds is { Count: > 0 } ids ? ids.ToHashSet() : null);

            PreparedMidiExport prepared = await Task.Run(() => MidiExportSessionService.Prepare(
                project,
                Session.CurrentProjectPath,
                Session.ProjectFileInformation,
                ShellSession.SoftwareVersion,
                exportOptions));

            if (!prepared.Succeeded)
            {
                await MessageDialog.ShowAsync(
                    this,
                    "The MIDI export plan is not valid:\n\n"
                    + string.Join("\n", prepared.Problems.Take(12)),
                    "MIDI Export",
                    MessageDialogButtons.Ok,
                    MessageDialogIcon.Error);
                Session.SetStatus("MIDI export plan failed.");
                return;
            }

            bool overwriteAuthorized = false;
            if (prepared.RequiresOverwriteAuthorization)
            {
                string targets = string.Join("\n", prepared.PlannedPaths.Take(12));
                if (prepared.PlannedPaths.Count > 12)
                {
                    targets += $"\n… and {prepared.PlannedPaths.Count - 12} more";
                }

                var confirm = await MessageDialog.ShowAsync(
                    this,
                    "These files already exist and will be overwritten:\n\n" + targets,
                    "MIDI Export",
                    MessageDialogButtons.YesNo,
                    MessageDialogIcon.Warning);
                if (confirm != MessageDialogResult.Yes)
                {
                    return;
                }

                overwriteAuthorized = true;
            }

            MidiExportSessionOutcome outcome = await Task.Run(
                () => MidiExportSessionService.ExecuteAsync(prepared, overwriteAuthorized));
            if (!outcome.Succeeded)
            {
                await MessageDialog.ShowAsync(
                    this,
                    outcome.FailureMessage ?? "MIDI export failed.",
                    "MIDI Export",
                    MessageDialogButtons.Ok,
                    MessageDialogIcon.Error);
                Session.SetStatus("MIDI export failed: " + (outcome.FailureMessage ?? "unknown"));
                return;
            }

            string details = string.Join("\n", outcome.WrittenPaths);
            if (outcome.PaddingSummary.HasPadding)
            {
                details += "\n\n" + outcome.PaddingSummary.Message;
            }

            if (outcome.Messages.Count > 0)
            {
                details += "\n\n" + string.Join("\n", outcome.Messages.Take(20));
            }

            Session.SetStatusMessage(
                $"MIDI export wrote {outcome.WrittenPaths.Count} file(s) to {options.OutputDirectory}.",
                isError: false,
                details: details,
                detailsTitle: "MIDI Export Report");
        }
        catch (Exception exception)
        {
            await MessageDialog.ShowAsync(
                this,
                $"MIDI export failed.\n\n{exception.Message}",
                "MIDI Export",
                MessageDialogButtons.Ok,
                MessageDialogIcon.Error);
            Session.SetStatus("MIDI export failed: " + exception.Message);
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
        var dialog = new ApplicationPreferencesDialog(Session.Preferences);
        await dialog.ShowDialog(this);
        if (dialog.Preferences is not { } updated)
        {
            return;
        }

        ApplicationPreferencesSaveResult saved = new ApplicationPreferencesStore().Save(updated);
        if (!saved.Succeeded)
        {
            Session.SetStatus(
                "Application Preferences could not be saved: "
                + (saved.Notice?.Message ?? "unknown error"));
            return;
        }

        await Session.ApplyPreferencesAsync(updated);
        RefreshSoundFontState();
        Session.SetStatus(
            saved.Notice is null
                ? "Application Preferences saved."
                : "Application Preferences saved. " + saved.Notice.Message);
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
                failures.Add($"{entry.Name}: {ex}");
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

        Step("create-project", () =>
        {
            Session.CreateProject("Smoke Project");
            if (!Session.HasProject)
            {
                throw new InvalidOperationException("New Project did not open a Project.");
            }
        });

        try
        {
            string smokeDirectory = Path.Combine(Path.GetTempPath(), "midora-avalonia-smoke");
            Directory.CreateDirectory(smokeDirectory);
            string savePath = Path.Combine(smokeDirectory, "smoke-project.midora");
            if (File.Exists(savePath))
            {
                File.Delete(savePath);
            }

            await Session.SaveProjectAsync(savePath, overwriteAuthorized: true);
            if (!File.Exists(savePath))
            {
                throw new InvalidOperationException("Save Project did not create the package.");
            }

            await Session.OpenProjectAsync(savePath);
            if (!Session.HasProjectSession)
            {
                throw new InvalidOperationException("Re-opened Project has no Project session.");
            }

            await Session.CompileAsync();
            if (Session.CompileState != "Compile Succeeded")
            {
                throw new InvalidOperationException($"Compile state is '{Session.CompileState}'.");
            }
        }
        catch (Exception ex)
        {
            failures.Add($"project-lifecycle: {ex.GetType().Name}: {ex.Message}");
        }

        var midiPath = Environment.GetEnvironmentVariable("MIDORA_MIDI_SMOKE");
        if (!string.IsNullOrEmpty(midiPath) && File.Exists(midiPath))
        {
            try
            {
                byte[] midiBytes = File.ReadAllBytes(midiPath);
                var imported = ImportedMidiProject.Parse(midiBytes, Path.GetFileName(midiPath));
                Session.CreateProjectFromMidi(
                    System.IO.Path.GetFileNameWithoutExtension(midiPath),
                    imported,
                    midiBytes);
                if (!Session.HasProject || Session.Workspaces.Count == 0)
                {
                    failures.Add("midi-import: imported project did not open a workspace");
                }

                if (!Session.HasProjectSession)
                {
                    failures.Add("midi-import: imported project was not adopted into a Project session");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"midi-import: {ex.GetType().Name}: {ex.Message}");
            }
        }
        string exportSummary = string.Empty;
        if (Session.HasProjectSession && Session.DomainProject is { } exportProject)
        {
            try
            {
                string exportDirectory = Path.Combine(
                    Path.GetTempPath(), "midora-avalonia-smoke", "midi-export");
                Directory.CreateDirectory(exportDirectory);
                PreparedMidiExport prepared = MidiExportSessionService.Prepare(
                    exportProject,
                    Session.CurrentProjectPath,
                    Session.ProjectFileInformation,
                    ShellSession.SoftwareVersion,
                    new MidiExportSessionOptions(
                        MidiExportMode.WholeProject,
                        MidiExportRoutingStrategy.Compact,
                        exportDirectory,
                        0,
                        null,
                        IncludeReadme: true,
                        TreatWarningsAsErrors: false));
                if (!prepared.Succeeded)
                {
                    throw new InvalidOperationException(
                        "MIDI export plan failed: " + string.Join("; ", prepared.Problems.Take(5)));
                }

                MidiExportSessionOutcome outcome = await MidiExportSessionService.ExecuteAsync(
                    prepared,
                    overwriteAuthorized: true);
                if (!outcome.Succeeded || outcome.WrittenPaths.Count == 0)
                {
                    throw new InvalidOperationException(
                        outcome.FailureMessage ?? "MIDI export wrote no files.");
                }

                string exportedMidiPath = outcome.WrittenPaths.First(
                    path => path.EndsWith(".mid", StringComparison.OrdinalIgnoreCase));
                StandardMidiFile.ParseType0Or1(File.ReadAllBytes(exportedMidiPath));
                exportSummary = $" midi-export={outcome.WrittenPaths.Count}";
            }
            catch (Exception ex)
            {
                failures.Add($"midi-export: {ex.GetType().Name}: {ex.Message}");
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

        string compileSummary = Session.CompileState;
        try
        {
            await Session.CompileAsync();
            compileSummary = $"{Session.CompileState}"
                + $" diagnostics={Session.Diagnostics.Count}"
                + $" errors={Session.ErrorCount} warnings={Session.WarningCount}";
            if (Session.HasProjectSession && Session.CompileState != "Compile Succeeded")
            {
                failures.Add($"compile: state is '{Session.CompileState}'.");
            }
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

        Console.Error.WriteLine($"SHELL-SMOKE failures={failures.Count} {compileSummary}{exportSummary}");
        foreach (var failure in failures)
        {
            Console.Error.WriteLine("FAIL " + failure);
        }

        return failures.Count == 0 ? 0 : 3;
    }
}
