using System.ComponentModel;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using Midora.Application;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using Midora.MidiExport;
using Midora.AudioRender;
using Midora.Compiler;

namespace Midora.Desktop;

public partial class MainWindow : Window
{
    private const double MinimumUiReorderDragDistance = 10;
    private const string ProjectTreeDragFormat = "Midora.ProjectTreeNode";
    private const string WorkspaceTabDragFormat = "Midora.WorkspaceTab";
    private const string EventInstrumentDragFormat = "Midora.EventInstrumentId";
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private readonly DesktopSessionController _session = new();
    private readonly ApplicationPreferencesStore _preferenceStore = new();
    private readonly RecentProjectsService _recentProjects = new(new RecentProjectsStore());
    private ApplicationPreferences _preferences = ApplicationPreferences.Default;
    private HwndSource? _windowSource;
    private bool _closeApproved;
    private bool _closeRequestInProgress;
    private bool _operationInProgress;
    private readonly DispatcherTimer _playbackTimer;
    private ProjectObjectClipboardPayload? _projectClipboard;
    private ProjectDocumentSession? _clipboardDocument;
    private (MidoraId SegmentId, long StartTick, int Pitch, int Velocity)? _notePlacement;
    private (MidoraId InstrumentId, MidoraId SubVoiceId, long StartTick, int Pitch, int Velocity)? _templateNotePlacement;
    private Point? _projectTreeDragStart;
    private ProjectTreeNode? _projectTreeDragNode;
    private Point? _workspaceTabDragStart;
    private WorkspaceViewModel? _workspaceTabDragWorkspace;
    private bool _spaceStartedPlayback;
    private Point? _instrumentListDragStart;
    private MidoraId? _instrumentListDragId;
    private ListBoxItem? _instrumentBrowserDropContainer;
    private bool _instrumentBrowserDropAfter;
    private int? _trackHeaderContextLane;
    private MidoraId? _arrangementSharedGroupContextId;
    private MidoraId? _logicalTrackShortcutTrackId;
    private (ArrangementLaneKind Kind, MidoraId Id)? _arrangementHeaderShortcut;
    private TimelineSurface? _pendingTimelineAltReleaseFocus;
    private bool _synchronizingInstrumentStructureSelection;
    private bool _followPlaybackViewportInteractionActive;
    private CancellationTokenSource? _instrumentLoopCommitDelay;
    private CancellationTokenSource? _timelineSelectionMaterialization;
    private long _nextProjectRuntimeInformationRefresh;
    private TimelineSelectionOperationContext? _timelineSelectionOperationContext;

    private enum TimelineSelectionObjectKind
    {
        Segments,
        MidiSegments,
        MixedSegments,
        LogicalNotes,
        LogicalParameterPoints,
        DirectMidiNotes,
        DirectMidiEventPoints,
        TemplateNotes,
        SubVoiceEventPoints
    }

    private sealed record TimelineSelectionOperationContext(
        TimelineSelectionObjectKind Kind,
        MidoraId[] Ids,
        MidoraId? OwnerId = null,
        MidoraId? SecondaryId = null,
        MidiValueTarget? MidiTarget = null,
        DirectMidiEventLaneTarget? DirectMidiTarget = null,
        double PointMinimum = 0,
        double PointMaximum = 127);

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _session;
        SourceInitialized += OnSourceInitialized;
        StateChanged += OnWindowStateChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        PreviewMouseDown += OnPreviewMouseDownForPlaybackShortcut;
        Deactivated += OnWindowDeactivated;
        ContextMenuOpening += OnEditingSurfaceContextMenuOpening;
        MainMenu.AddHandler(
            MenuItem.SubmenuOpenedEvent,
            new RoutedEventHandler(OnMainMenuSubmenuOpened),
            handledEventsToo: true);
        AddHandler(
            Selector.SelectionChangedEvent,
            new SelectionChangedEventHandler(OnAnyTabSelectionChangedForPreviewPriority),
            handledEventsToo: true);
        AddHandler(
            Keyboard.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(OnKeyboardFocusChangedForInputMethod),
            handledEventsToo: true);
        AddHandler(
            TimelineSurface.AltGestureConsumedEvent,
            new RoutedEventHandler(OnTimelineAltGestureConsumed));
        LoadDesktopPreferences();
        _playbackTimer = new(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _playbackTimer.Tick += OnPlaybackTimerTick;
        _playbackTimer.Start();
        if (_recentProjects.StartupNotice is not null)
        {
            _session.SetStatusMessage(_recentProjects.StartupNotice.Message, isError: true);
        }
    }

    public async Task HandleStartupRequestAsync(ApplicationStartupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? candidate = request.Arguments
            .Select(argument => Path.IsPathFullyQualified(argument)
                ? argument
                : Path.GetFullPath(Path.Combine(request.WorkingDirectory, argument)))
            .FirstOrDefault(path => string.Equals(
                Path.GetExtension(path), ".midora", StringComparison.OrdinalIgnoreCase));
        if (candidate is null) return;
        if (!File.Exists(candidate))
        {
            ShowError("Open Project", $"The requested Project does not exist.\n\n{candidate}");
            return;
        }
        if (!StopPlaybackForProjectCommand("Open Project") || !await ConfirmCloseCurrentProjectAsync()) return;
        Exception? audioInitializationFailure = null;
        if (await RunOperationAsync(
                "Open Project",
                async cancellationToken =>
                {
                    await _session.OpenProjectAsync(candidate, cancellationToken: cancellationToken);
                    audioInitializationFailure =
                        await InitializeAudioWorkerForActiveProjectAsync(cancellationToken);
                },
                canCancel: false))
        {
            RecordRecentDirectory(RecentDirectoryPurpose.OpenProject, Path.GetDirectoryName(candidate));
            RecordRecentProject(candidate);
            ReportAudioWorkerInitializationFailure(audioInitializationFailure);
        }
    }

    protected override async void OnClosed(EventArgs e)
    {
        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(OnWindowMessage);
            _windowSource = null;
        }
        _playbackTimer.Stop();
        Interlocked.Exchange(ref _instrumentLoopCommitDelay, null)?.Cancel();
        Interlocked.Exchange(ref _timelineSelectionMaterialization, null)?.Cancel();
        await _session.DisposeAsync();
        SaveDesktopPreferences();
        base.OnClosed(e);
    }

    private async void OnNewProjectClick(object sender, RoutedEventArgs e)
    {
        if (!StopPlaybackForProjectCommand("New Project") || !await ConfirmCloseCurrentProjectAsync()) return;
        NewProjectDialog dialog = new(
            ExistingRecentDirectory(RecentDirectoryPurpose.SaveAndSaveCopy))
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || dialog.Request is null) return;
        NewProjectCreationRequest request = dialog.Request;
        Exception? audioInitializationFailure = null;
        if (await RunOperationAsync(
                "Create Project",
                async cancellationToken =>
                {
                    await _session.CreateProjectAsync(request, cancellationToken);
                    audioInitializationFailure =
                        await InitializeAudioWorkerForActiveProjectAsync(cancellationToken);
                },
                canCancel: false))
        {
            if (request.TargetPath is string targetPath)
            {
                RecordRecentDirectory(RecentDirectoryPurpose.SaveAndSaveCopy, Path.GetDirectoryName(targetPath));
                RecordRecentProject(targetPath);
            }
            ReportAudioWorkerInitializationFailure(audioInitializationFailure);
        }
    }

    private async void OnOpenProjectClick(object sender, RoutedEventArgs e)
    {
        if (!StopPlaybackForProjectCommand("Open Project") || !await ConfirmCloseCurrentProjectAsync()) return;
        OpenFileDialog dialog = new()
        {
            Title = "Open Midora Project",
            Filter = "Midora Project (*.midora;*.zip)|*.midora;*.zip|Midora Package (*.midora)|*.midora|ZIP Package (*.zip)|*.zip|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = ExistingRecentDirectory(RecentDirectoryPurpose.OpenProject)
        };
        if (dialog.ShowDialog(this) != true) return;
        Exception? audioInitializationFailure = null;
        if (await RunOperationAsync(
                "Open Project",
                async cancellationToken =>
                {
                    await _session.OpenProjectAsync(
                        dialog.FileName,
                        cancellationToken: cancellationToken);
                    audioInitializationFailure =
                        await InitializeAudioWorkerForActiveProjectAsync(cancellationToken);
                },
                canCancel: false))
        {
            RecordRecentDirectory(RecentDirectoryPurpose.OpenProject, Path.GetDirectoryName(dialog.FileName));
            RecordRecentProject(dialog.FileName);
            ReportAudioWorkerInitializationFailure(audioInitializationFailure);
        }
    }

    private async void OnSaveProjectClick(object sender, RoutedEventArgs e) => await SaveProjectAsync();

    private async Task<bool> SaveProjectAsync()
    {
        if (!_session.HasProject) return true;
        if (_session.HasDamagedProjectObjects)
        {
            _session.Notice = "Saving is disabled while damaged Arrangement object placeholders remain. Delete every damaged placeholder first, or discard this Project session.";
            return false;
        }
        if (!StopPlaybackForProjectCommand("Save Project")) return false;
        string? firstPath = null;
        if (_session.Persistence?.CurrentProjectPath is null)
        {
            SaveFileDialog dialog = CreateProjectSaveDialog("Save Midora Project");
            if (dialog.ShowDialog(this) != true) return false;
            firstPath = dialog.FileName;
        }
        bool saved = await RunOperationAsync(
            "Save Project",
            () => _session.SaveProjectAsync(firstPath, overwriteAuthorized: firstPath is not null && File.Exists(firstPath)));
        if (saved && (firstPath ?? _session.Persistence?.CurrentProjectPath) is string path)
        {
            RecordRecentDirectory(RecentDirectoryPurpose.SaveAndSaveCopy, Path.GetDirectoryName(path));
            RecordRecentProject(path);
        }
        return saved;
    }

    private async void OnSaveCopyClick(object sender, RoutedEventArgs e)
    {
        if (!_session.HasProject) return;
        if (_session.HasDamagedProjectObjects)
        {
            _session.Notice = "Save Copy is disabled while damaged Arrangement object placeholders remain.";
            return;
        }
        if (!StopPlaybackForProjectCommand("Save Project Copy")) return;
        SaveFileDialog dialog = CreateProjectSaveDialog("Save Project Copy");
        if (dialog.ShowDialog(this) != true) return;
        if (await RunOperationAsync(
            "Save Project Copy",
            () => _session.SaveCopyAsync(dialog.FileName, overwriteAuthorized: File.Exists(dialog.FileName))))
        {
            RecordRecentDirectory(RecentDirectoryPurpose.SaveAndSaveCopy, Path.GetDirectoryName(dialog.FileName));
        }
    }

    private async void OnApplicationPreferencesClick(object sender, RoutedEventArgs e)
    {
        if (!PrepareForModalSurface()) return;
        if (!_session.CanStartForegroundTask)
        {
            _session.SetStatusMessage(
                "Stop playback and wait for the current foreground task before changing Application Preferences.",
                isError: true);
            return;
        }
        ApplicationPreferencesDialog dialog = new(_preferences) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;

        ApplicationPreferences preferences = dialog.Result;
        bool rebuildAudioWorker = _session.RequiresAudioWorkerRebuild(preferences);
        if (!rebuildAudioWorker)
        {
            ApplicationPreferencesSaveResult saved = _preferenceStore.Save(preferences);
            if (!saved.Succeeded)
            {
                ShowError(
                    "Application Preferences",
                    saved.Notice?.Message ?? "Application Preferences could not be saved.");
                return;
            }
            try
            {
                await _session.ApplyApplicationPreferencesAsync(preferences);
                _preferences = preferences;
                _session.SetStatusMessage("Application Preferences were saved and applied.");
            }
            catch (Exception exception)
            {
                _preferences = preferences;
                ShowError(
                    "Application Preferences",
                    $"Preferences were saved, but could not be applied: {exception.Message}");
            }
            return;
        }

        bool persisted = false;
        bool applied = await RunOperationAsync(
            "Saving Settings",
            async cancellationToken =>
            {
                ApplicationPreferencesSaveResult saved = await Task.Run(
                    () => _preferenceStore.Save(preferences),
                    cancellationToken);
                if (!saved.Succeeded)
                {
                    throw new IOException(
                        saved.Notice?.Message
                        ?? "Application Preferences could not be saved.");
                }
                persisted = true;
                _preferences = preferences;
                await _session.ApplyApplicationPreferencesAsync(
                    preferences,
                    cancellationToken);
            },
            canCancel: false,
            lockLevel: DesktopTaskLockLevel.FullApplication);
        if (applied)
        {
            _session.SetStatusMessage(
                "Application Preferences were saved; the audio Worker is ready.");
        }
        else if (persisted)
        {
            // The durable settings and in-memory preference snapshot must agree even when
            // operational BASS initialization fails. A later settings Apply or playback attempt
            // can retry initialization without silently reverting what was saved.
            _preferences = preferences;
        }
    }

    private async void OnCloseProjectClick(object sender, RoutedEventArgs e)
    {
        if (StopPlaybackForProjectCommand("Close Project") && await ConfirmCloseCurrentProjectAsync()) await _session.CloseProjectAsync();
    }

    private bool StopPlaybackForProjectCommand(string command)
    {
        if (!PrepareForModalSurface()) return false;
        if (_operationInProgress)
        {
            _session.SetStatusMessage(
                $"{command} cannot start while another foreground task is running. Midora does not queue tasks.",
                isError: true);
            return false;
        }
        if (!_session.IsPlaybackActive) return true;
        try
        {
            _session.StopPlayback();
            return !_session.IsPlaybackActive;
        }
        catch (Exception exception)
        {
            ShowError(command, $"Playback cleanup did not complete: {exception.Message}");
            return false;
        }
    }

    private async Task<bool> ConfirmCloseCurrentProjectAsync()
    {
        if (!_session.HasProject
            || (_session.Document?.IsModified != true
                && _session.Persistence?.CurrentProjectPath is not null))
        {
            return true;
        }
        if (_session.HasDamagedProjectObjects)
        {
            return MessageDialog.Show(
                this,
                "This Project has unsaved changes and damaged object placeholders. Saving is prohibited until every damaged placeholder is deleted. Close and discard the current session changes?",
                "Discard Unsavable Project Changes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }
        MessageBoxResult result = MessageDialog.Show(
            this,
            "Save changes to the current Project before closing it?",
            "Unsaved Project Changes",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);
        return result switch
        {
            MessageBoxResult.Yes => await SaveProjectAsync(),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    private void OnUndoClick(object sender, RoutedEventArgs e) => RunSynchronous("Undo", _session.Undo);
    private void OnRedoClick(object sender, RoutedEventArgs e) => RunSynchronous("Redo", _session.Redo);
    private void OnNavigateBackClick(object sender, RoutedEventArgs e) => _session.NavigateBack();
    private void OnNavigateForwardClick(object sender, RoutedEventArgs e) => _session.NavigateForward();

    private void OnWorkspaceTabListClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        ContextMenu menu = new() { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (WorkspaceViewModel workspace in _session.Workspaces)
        {
            MenuItem item = new()
            {
                Header = workspace.Header,
                IsCheckable = true,
                IsChecked = ReferenceEquals(workspace, _session.ActiveWorkspace),
                Tag = workspace
            };
            item.Click += (_, _) => _session.ActiveWorkspace = (WorkspaceViewModel)item.Tag;
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No open Workspaces", IsEnabled = false });
        }
        menu.IsOpen = true;
    }
    private void OnCutClick(object sender, RoutedEventArgs e) => CutOrCopyProjectSelection(cut: true);
    private void OnCopyClick(object sender, RoutedEventArgs e) => CutOrCopyProjectSelection(cut: false);
    private void OnPasteClick(object sender, RoutedEventArgs e) => PasteProjectSelection();
    private void OnSelectAllClick(object sender, RoutedEventArgs e) => SelectAllInFocusedScope();
    private void OnDuplicateClick(object sender, RoutedEventArgs e) => DuplicateFocusedSelection();

    private async void OnPlayClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _session.StartPlaybackAsync();
            _spaceStartedPlayback = true;
        }
        catch (Exception exception)
        {
            _spaceStartedPlayback = false;
            _session.SetStatusMessage($"Play: {exception.Message}", isError: true);
        }
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, RestorePlaybackShortcutFocus);
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        _spaceStartedPlayback = false;
        RunSynchronous("Stop", _session.StopPlayback);
    }

    private void OnPrimaryTransportClick(object sender, RoutedEventArgs e)
    {
        if (_session.IsPlaybackActive)
        {
            OnStopClick(sender, e);
        }
        else
        {
            OnPlayClick(sender, e);
        }
    }

    private void OnLoopClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle) return;
        if (toggle.IsChecked != true)
        {
            RunSynchronous("Disable Loop", () => _session.SetLoopRange(null));
            return;
        }
        if (_session.ActiveWorkspace is not TimelineWorkspaceViewModel
            {
                TimeRangeStartTick: long start,
                TimeRangeEndTick: long end
            } timeline
            || end <= start)
        {
            ShowUnavailable("Enable Loop", "Create a non-empty Time Range in the active timeline first.");
            toggle.IsChecked = false;
            return;
        }
        RunSynchronous("Enable Loop", () => _session.SetLoopRange(
            timeline.ToProjectRange(_session.Project!, start, end)));
    }

    private async void OnResetPlaybackClick(object sender, RoutedEventArgs e)
    {
        Exception? audioInitializationFailure = null;
        bool completed = await RunOperationAsync(
            "Reset Playback Engine",
            async cancellationToken =>
            {
                _session.ResetPlaybackEngine();
                audioInitializationFailure =
                    await InitializeAudioWorkerForActiveProjectAsync(cancellationToken);
            },
            canCancel: false,
            lockLevel: DesktopTaskLockLevel.FullApplication);
        if (completed)
        {
            ReportAudioWorkerInitializationFailure(audioInitializationFailure);
        }
    }

    private void OnPlaybackTimerTick(object? sender, EventArgs e)
    {
        long now = Environment.TickCount64;
        if (now >= _nextProjectRuntimeInformationRefresh)
        {
            _nextProjectRuntimeInformationRefresh = now + 1_000;
            _session.RefreshProjectRuntimeInformation();
        }
        if (!_session.IsPlaybackActive) return;
        try
        {
            _session.UpdatePlayback();
            FollowActivePlayback(force: false);
        }
        catch (Exception exception)
        {
            ShowError("Playback", exception.Message);
        }
    }

    private void FollowActivePlayback(bool force)
    {
        if (_session.ActiveWorkspace is not TimelineWorkspaceViewModel timeline)
        {
            return;
        }

        long? startTick = TimelinePlaybackFollowPolicy.ResolveStartTick(
            _preferences.DesktopUi.FollowPlayback,
            _session.IsPlaybackActive,
            _followPlaybackViewportInteractionActive,
            timeline.StartTick,
            timeline.TickSpan,
            timeline.PlaybackCursorTick,
            force);
        if (startTick is long resolvedStartTick)
        {
            timeline.StartTick = resolvedStartTick;
        }
    }

    private bool CanTemporarilySuspendPlaybackFollow() =>
        _preferences.DesktopUi.FollowPlayback
        && _session.IsPlaybackActive
        && _session.ActiveWorkspace is TimelineWorkspaceViewModel { PlaybackCursorTick: not null };

    private void OnFollowViewportPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        bool beginsExplicitViewportDrag = sender switch
        {
            TimelineOverviewSurface => e.ChangedButton == MouseButton.Left,
            TimelineSurface => e.ChangedButton == MouseButton.Middle,
            _ => false
        };
        if (beginsExplicitViewportDrag && CanTemporarilySuspendPlaybackFollow())
        {
            _followPlaybackViewportInteractionActive = true;
        }
    }

    private void OnFollowViewportPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        bool endsExplicitViewportDrag = sender switch
        {
            TimelineOverviewSurface => e.ChangedButton == MouseButton.Left,
            TimelineSurface => e.ChangedButton == MouseButton.Middle,
            _ => false
        };
        if (endsExplicitViewportDrag)
        {
            EndFollowPlaybackViewportInteraction();
        }
    }

    private void OnFollowViewportLostMouseCapture(object sender, MouseEventArgs e) =>
        EndFollowPlaybackViewportInteraction();

    private void EndFollowPlaybackViewportInteraction()
    {
        if (!_followPlaybackViewportInteractionActive)
        {
            return;
        }

        _followPlaybackViewportInteractionActive = false;
        FollowActivePlayback(force: true);
    }

    private void OnFollowOverviewPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (CanTemporarilySuspendPlaybackFollow())
        {
            e.Handled = true;
        }
    }

    private void OnNewTrackClick(object sender, RoutedEventArgs e)
    {
        QueueCreateLogicalTrack(sender, insertionIndex: null);
    }

    private void OnTrackHeaderNewTrackClick(object sender, RoutedEventArgs e)
    {
        int? insertionIndex = null;
        if (_session.Project is MidoraProject project
            && _session.ActiveWorkspace is TimelineWorkspaceViewModel arrangement
            && _trackHeaderContextLane is int lane
            && arrangement.GetArrangementLane(lane) is { ObjectId: MidoraId trackId })
        {
            int currentIndex = project.ArrangementTracks.FindIndex(value => value.TrackId == trackId);
            if (currentIndex >= 0) insertionIndex = currentIndex + 1;
        }
        QueueCreateLogicalTrack(sender, insertionIndex);
    }

    private void QueueCreateLogicalTrack(object sender, int? insertionIndex)
    {
        RunAfterMenuClosed(sender, () =>
        {
            if (!_session.HasProject) return;
            RunSynchronous("Create Logical Track", () =>
            {
                _session.Execute(ProjectDomainEditCommands.CreateLogicalTrack(
                    insertionIndex: insertionIndex));
                _session.OpenArrangement();
            });
        });
    }

    private void OnNewTrackWithInstrumentClick(object sender, RoutedEventArgs e)
    {
        RunAfterMenuClosed(sender, () =>
        {
            if (_session.Project is not MidoraProject project) return;
            NewLogicalTrackWithInstrumentDialog dialog = new(project.EventInstruments)
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true) return;
            if (dialog.CreatesInstrument)
            {
                RunSynchronous("Create Logical Track with Event Instrument", () =>
                {
                    _session.Execute(
                        ProjectDomainEditCommands.CreateLogicalTrackWithNewEventInstrument(
                            dialog.NewInstrumentName));
                    _session.OpenInstrument(project.EventInstruments[^1].Id);
                });
                return;
            }
            else if (dialog.ExistingInstrumentId is MidoraId instrumentId)
            {
                RunSynchronous("Create Logical Track", () =>
                    _session.Execute(ProjectDomainEditCommands.CreateLogicalTrack(
                        eventInstrumentId: instrumentId)));
            }
            _session.OpenArrangement();
        });
    }

    private void OnNewRawMidiTrackClick(object sender, RoutedEventArgs e)
    {
        RunAfterMenuClosed(sender, () =>
        {
            if (_session.Project is not MidoraProject project) return;
            NewRawMidiTrackDialog dialog = new(project.MidiChannelRoots) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            RunSynchronous("Create Raw MIDI Track", () =>
                _session.Execute(ProjectDomainEditCommands.CreatePureMidiTrackWithNewRoot(
                    dialog.TrackName,
                    dialog.RoutingMode,
                    dialog.OneBasedPort,
                    dialog.OneBasedChannel,
                    dialog.ChannelMode)));
            _session.OpenArrangement();
        });
    }

    private void OnNewInstrumentClick(object sender, RoutedEventArgs e)
    {
        RunAfterMenuClosed(sender, () =>
        {
            if (!_session.HasProject) return;
            RunSynchronous("Create Event Instrument", () =>
            {
                _session.Execute(ProjectDomainEditCommands.CreateEventInstrument());
                EventInstrument created = _session.Project!.EventInstruments[^1];
                _session.OpenInstrument(created.Id);
            });
        });
    }

    private void RunAfterMenuClosed(object sender, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!PrepareForModalSurface()) return;

        // A Popup owns a separate HWND. Close it and let the current routed input
        // event unwind before an edit rebuilds ItemsSource-backed WPF collections.
        // Mutating the visual tree synchronously from the Popup button's Click route
        // can leave mouse capture and layout processing in a re-entrant state.
        NewProjectItemPopup.IsOpen = false;
        NewProjectItemButton.IsChecked = false;
        _ = Dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }

    private void OnProjectTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ProjectTreeNode node) return;
        e.Handled = true;
        QueueOpenWorkspace(node);
    }

    private void QueueOpenWorkspace(ProjectTreeNode node) =>
        _ = Dispatcher.BeginInvoke(
            () =>
            {
                try { _session.OpenWorkspace(node); }
                catch (InvalidOperationException exception)
                {
                    _session.SetStatusMessage(exception.Message, isError: true);
                }
            },
            DispatcherPriority.Normal);

    private void OnProjectTreeRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current is not null && current is not TreeViewItem)
        {
            current = VisualTreeHelper.GetParent(current);
        }
        if (current is TreeViewItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void OnProjectTreeLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _projectTreeDragStart = null;
        _projectTreeDragNode = null;
        if (!_session.CanEditProject || _session.IsProjectTreeFiltered) return;
        TreeViewItem? item = FindVisualAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not ProjectTreeNode node
            || node.Kind is not (ProjectTreeNodeKind.LogicalTrack
                or ProjectTreeNodeKind.EventInstrument))
        {
            return;
        }
        _projectTreeDragStart = e.GetPosition(ProjectTree);
        _projectTreeDragNode = node;
    }

    private void OnProjectTreeMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || _projectTreeDragStart is not Point origin
            || _projectTreeDragNode is not ProjectTreeNode source
            || !_session.CanEditProject
            || _session.IsProjectTreeFiltered)
        {
            return;
        }
        Point current = e.GetPosition(ProjectTree);
        if (!HasReachedUiReorderDragThreshold(origin, current))
        {
            return;
        }
        _projectTreeDragStart = null;
        _projectTreeDragNode = null;
        DataObject data = new(ProjectTreeDragFormat, source);
        _ = DragDrop.DoDragDrop(ProjectTree, data, DragDropEffects.Move | DragDropEffects.Link);
    }

    private void OnProjectTreeDragOver(object sender, DragEventArgs e)
    {
        ProjectTreeNode? source = e.Data.GetDataPresent(ProjectTreeDragFormat)
            ? e.Data.GetData(ProjectTreeDragFormat) as ProjectTreeNode
            : null;
        ProjectTreeNode? target = FindVisualAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext
            as ProjectTreeNode;
        e.Effects = GetProjectTreeDropEffect(source, target);
        e.Handled = true;
    }

    private void OnProjectTreeDrop(object sender, DragEventArgs e)
    {
        ProjectTreeNode? source = e.Data.GetDataPresent(ProjectTreeDragFormat)
            ? e.Data.GetData(ProjectTreeDragFormat) as ProjectTreeNode
            : null;
        ProjectTreeNode? target = FindVisualAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext
            as ProjectTreeNode;
        DragDropEffects effect = GetProjectTreeDropEffect(source, target);
        e.Effects = effect;
        e.Handled = true;
        if (source is null || target is null || effect == DragDropEffects.None || _session.Project is null) return;
        if (effect == DragDropEffects.Link)
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                () => RunSynchronous(
                    "Project Tree Drag and Drop",
                    () => ApplyProjectTreeDrop(source, target)));
            return;
        }
        RunSynchronous("Project Tree Drag and Drop", () => ApplyProjectTreeDrop(source, target));
    }

    private DragDropEffects GetProjectTreeDropEffect(ProjectTreeNode? source, ProjectTreeNode? target)
    {
        if (source is null
            || target is null
            || ReferenceEquals(source, target)
            || !_session.CanEditProject
            || _session.IsProjectTreeFiltered)
        {
            return DragDropEffects.None;
        }
        return (source.Kind, target.Kind) switch
        {
            (ProjectTreeNodeKind.LogicalTrack, ProjectTreeNodeKind.LogicalTrack) => DragDropEffects.Move,
            (ProjectTreeNodeKind.EventInstrument, ProjectTreeNodeKind.EventInstrument) => DragDropEffects.Move,
            (ProjectTreeNodeKind.EventInstrument, ProjectTreeNodeKind.LogicalTrack
                or ProjectTreeNodeKind.LogicalTracks) => DragDropEffects.Link,
            _ => DragDropEffects.None
        };
    }

    private void ApplyProjectTreeDrop(ProjectTreeNode source, ProjectTreeNode target)
    {
        MidoraProject project = _session.Project
            ?? throw new InvalidOperationException("No Project is open.");
        if (source.ObjectId is not MidoraId sourceId)
        {
            throw new InvalidOperationException("The dragged Project object is unavailable.");
        }
        switch (source.Kind, target.Kind)
        {
            case (ProjectTreeNodeKind.LogicalTrack, ProjectTreeNodeKind.LogicalTrack)
                when target.ObjectId is MidoraId targetTrackId:
                _session.ReorderProjectTreeNode(
                    source,
                    project.Tracks.FindIndex(item => item.Id == targetTrackId));
                return;
            case (ProjectTreeNodeKind.EventInstrument, ProjectTreeNodeKind.EventInstrument)
                when target.ObjectId is MidoraId targetInstrumentId:
                _session.ReorderProjectTreeNode(
                    source,
                    project.EventInstruments.FindIndex(item => item.Id == targetInstrumentId));
                return;
            case (ProjectTreeNodeKind.EventInstrument, ProjectTreeNodeKind.LogicalTracks):
                EventInstrument instrument = project.EventInstruments.Single(item => item.Id == sourceId);
                _session.Execute(ProjectDomainEditCommands.CreateLogicalTrack(eventInstrumentId: sourceId));
                return;
            case (ProjectTreeNodeKind.EventInstrument, ProjectTreeNodeKind.LogicalTrack)
                when target.ObjectId is MidoraId targetTrackId:
                LogicalTrack track = project.Tracks.Single(item => item.Id == targetTrackId);
                if (project.ResolveEventInstrumentDefinitionId(track) is MidoraId currentId
                    && currentId != sourceId)
                {
                    EventInstrument? current = project.EventInstruments.FirstOrDefault(item => item.Id == currentId);
                    EventInstrument replacement = project.EventInstruments.Single(item => item.Id == sourceId);
                    if (MessageDialog.Show(
                            this,
                            $"Rebind Logical Track '{track.Name}' from '{current?.Name ?? track.LastBoundEventInstrumentName ?? "Unavailable Event Instrument"}' to '{replacement.Name}'? Existing Segments and Logical Parameter lanes are preserved; incompatible references will be diagnosed and are not repaired automatically.",
                            "Rebind Logical Track",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    {
                        return;
                    }
                }
                _session.BindLogicalTrack(targetTrackId, sourceId);
                return;
            default:
                throw new InvalidOperationException("This Project Tree drop target is not supported.");
        }
    }

    internal static T? FindVisualAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null && current is not T)
        {
            current = GetUiParent(current);
        }
        return current as T;
    }

    private static DependencyObject? GetUiParent(DependencyObject current)
    {
        if (current is ContentElement content)
        {
            return ContentOperations.GetParent(content)
                ?? (content as FrameworkContentElement)?.Parent;
        }
        if (current is Visual or Visual3D)
        {
            return VisualTreeHelper.GetParent(current)
                ?? (current as FrameworkElement)?.Parent
                ?? (current as FrameworkElement)?.TemplatedParent;
        }
        return LogicalTreeHelper.GetParent(current);
    }

    private static bool HasReachedUiReorderDragThreshold(Point origin, Point current)
    {
        double horizontal = current.X - origin.X;
        double vertical = current.Y - origin.Y;
        double systemDistance = Math.Sqrt(
            SystemParameters.MinimumHorizontalDragDistance
                * SystemParameters.MinimumHorizontalDragDistance
            + SystemParameters.MinimumVerticalDragDistance
                * SystemParameters.MinimumVerticalDragDistance);
        double threshold = Math.Max(MinimumUiReorderDragDistance, systemDistance);
        return horizontal * horizontal + vertical * vertical >= threshold * threshold;
    }

    private void ExecuteAndSelectCreated(
        IProjectEditCommand command,
        WorkspaceViewModel workspace)
    {
        MidoraProject project = _session.Project
            ?? throw new InvalidOperationException("No Project is open.");
        long firstNewStableId = project.NextStableId;
        _session.Execute(command);
        SelectCreatedWorkspaceObjects(workspace, firstNewStableId);
    }

    private void SelectCreatedWorkspaceObjects(
        WorkspaceViewModel workspace,
        long firstNewStableId,
        bool replaceSelectionWhenNoObjectSurvives = false)
    {
        if (_session.Project is not MidoraProject project) return;
        static MidoraId[] NewIds<T>(IEnumerable<T> items, Func<T, MidoraId> id, long first) =>
            items.Select(id).Where(value => value.Value >= first).Distinct().ToArray();

        MidoraId[] created = workspace switch
        {
            TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Arrangement } =>
                NewIds(project.Tracks.SelectMany(item => item.Segments), item => item.Id, firstNewStableId),
            TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Segment,
                ObjectId: MidoraId segmentId
            } => SegmentSelection(segmentId),
            TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Conductor } conductor =>
                conductor.ConductorEvents.Select(item => item.Id)
                    .Where(id => id.Value >= firstNewStableId).Distinct().ToArray(),
            InstrumentWorkspaceViewModel { ObjectId: MidoraId instrumentId } =>
                InstrumentSelection(instrumentId),
            _ => []
        };
        if (created.Length == 0)
        {
            if (replaceSelectionWhenNoObjectSurvives)
            {
                workspace.Selection.Clear();
                _session.RefreshWorkspaceSelection(workspace);
            }
            return;
        }
        workspace.Selection.Clear();
        foreach (MidoraId id in created) workspace.Selection.Add(id, makePrimary: false);
        _session.RefreshWorkspaceSelection(workspace);
        return;

        MidoraId[] SegmentSelection(MidoraId segmentId)
            => TimelineWorkspaceViewModel.FindCreatedSegmentObjectIds(
                project,
                segmentId,
                firstNewStableId);

        MidoraId[] InstrumentSelection(MidoraId instrumentId)
        {
            EventInstrument? instrument = project.EventInstruments.FirstOrDefault(item => item.Id == instrumentId);
            if (instrument is null) return [];
            MidoraId[] preferred = NewIds(instrument.SubVoices, item => item.Id, firstNewStableId);
            if (preferred.Length != 0) return preferred;
            preferred = NewIds(instrument.LogicalParameters, item => item.Id, firstNewStableId);
            if (preferred.Length != 0) return preferred;
            preferred = NewIds(instrument.ParameterMappings, item => item.Id, firstNewStableId);
            if (preferred.Length != 0) return preferred;
            preferred = NewIds(instrument.MappingFunctions, item => item.Id, firstNewStableId);
            if (preferred.Length != 0) return preferred;
            preferred = NewIds(instrument.Envelopes, item => item.Id, firstNewStableId);
            if (preferred.Length != 0) return preferred;
            preferred = NewIds(instrument.SubVoices.SelectMany(item => item.Events), item => item.Id, firstNewStableId);
            if (preferred.Length != 0) return preferred;
            preferred = NewIds(instrument.SubVoices.SelectMany(item => item.Curves), item => item.Id, firstNewStableId);
            if (preferred.Length != 0) return preferred;
            preferred = NewIds(
                instrument.SubVoices.SelectMany(item => item.Curves).SelectMany(item => item.Points),
                item => item.Id,
                firstNewStableId);
            if (preferred.Length != 0) return preferred;
            return workspace is InstrumentWorkspaceViewModel instrumentWorkspace
                ? instrumentWorkspace.MappingSteps.Select(item => item.Id)
                    .Where(id => id.Value >= firstNewStableId).Distinct().ToArray()
                : [];
        }
    }

    private void OnWorkspaceTabsLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _workspaceTabDragStart = null;
        _workspaceTabDragWorkspace = null;
        if (_session.IsMainWindowTaskLocked
            || FindVisualAncestor<ButtonBase>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }
        TabItem? item = FindVisualAncestor<TabItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not WorkspaceViewModel workspace) return;
        if (!workspace.CanReorder) return;
        _workspaceTabDragStart = e.GetPosition(WorkspaceTabs);
        _workspaceTabDragWorkspace = workspace;
    }

    private void OnWorkspaceTabsMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || _workspaceTabDragStart is not Point origin
            || _workspaceTabDragWorkspace is not WorkspaceViewModel workspace
            || _session.IsMainWindowTaskLocked)
        {
            return;
        }
        Point current = e.GetPosition(WorkspaceTabs);
        if (!HasReachedUiReorderDragThreshold(origin, current))
        {
            return;
        }
        _workspaceTabDragStart = null;
        _workspaceTabDragWorkspace = null;
        _ = DragDrop.DoDragDrop(
            WorkspaceTabs,
            new DataObject(WorkspaceTabDragFormat, workspace),
            DragDropEffects.Move);
    }

    private void OnWorkspaceTabsDragOver(object sender, DragEventArgs e)
    {
        WorkspaceViewModel? source = e.Data.GetDataPresent(WorkspaceTabDragFormat)
            ? e.Data.GetData(WorkspaceTabDragFormat) as WorkspaceViewModel
            : null;
        WorkspaceViewModel? target = FindVisualAncestor<TabItem>(e.OriginalSource as DependencyObject)?.DataContext
            as WorkspaceViewModel;
        e.Effects = source is not null
            && source.CanReorder
            && target is not null
            && !ReferenceEquals(source, target)
            && !_session.IsMainWindowTaskLocked
                ? DragDropEffects.Move
                : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWorkspaceTabsDrop(object sender, DragEventArgs e)
    {
        WorkspaceViewModel? source = e.Data.GetDataPresent(WorkspaceTabDragFormat)
            ? e.Data.GetData(WorkspaceTabDragFormat) as WorkspaceViewModel
            : null;
        WorkspaceViewModel? target = FindVisualAncestor<TabItem>(e.OriginalSource as DependencyObject)?.DataContext
            as WorkspaceViewModel;
        e.Handled = true;
        if (source is null || target is null || ReferenceEquals(source, target) || _session.IsMainWindowTaskLocked)
        {
            return;
        }
        int targetIndex = _session.Workspaces.IndexOf(target);
        if (targetIndex >= 0) _session.ReorderWorkspace(source, targetIndex);
    }

    private void OnWorkspaceTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, WorkspaceTabs)) return;
        _logicalTrackShortcutTrackId = null;
        _arrangementHeaderShortcut = null;
        SnapMenuItem.IsChecked = GetActiveEditorSettings().SnapEnabled;
        Dispatcher.BeginInvoke(() =>
        {
            if (WorkspaceTabs.ItemContainerGenerator.ContainerFromItem(WorkspaceTabs.SelectedItem)
                is TabItem selected)
            {
                selected.BringIntoView();
            }
            if (_session.ActiveWorkspace is DiagnosticsWorkspaceViewModel)
            {
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                {
                    if (_session.ActiveWorkspace is DiagnosticsWorkspaceViewModel
                        && FindWorkspaceElement<FrameworkElement>("DiagnosticsWorkspaceFocusTarget")
                            is { IsVisible: true, IsEnabled: true } focusTarget)
                    {
                        focusTarget.Focus();
                    }
                });
            }
        }, DispatcherPriority.Loaded);
    }

    private void OnProjectTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2)
        {
            BeginTreeRename();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            DeleteSelectedTreeNode();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && ProjectTree.SelectedItem is ProjectTreeNode node && !node.IsRenaming)
        {
            OnTreeOpenClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void OnTreeOpenClick(object sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ProjectTreeNode node) return;
        QueueOpenWorkspace(node);
    }

    private void OnProjectTreeContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || ProjectTree.SelectedItem is not ProjectTreeNode node) return;
        menu.Items.Clear();
        void Add(
            string header,
            RoutedEventHandler handler,
            string? gesture = null,
            bool enabled = true)
        {
            MenuItem item = new()
            {
                Header = header,
                InputGestureText = gesture ?? string.Empty,
                IsEnabled = enabled
            };
            item.Click += handler;
            menu.Items.Add(item);
        }
        void Separator() => menu.Items.Add(new Separator());

        switch (node.Kind)
        {
            case ProjectTreeNodeKind.InstrumentLibrary:
                Add("New Event Instrument", OnNewInstrumentClick);
                Separator();
                Add("Paste Event Instrument", OnPasteTreeInstrumentClick, "Ctrl+V");
                break;
            case ProjectTreeNodeKind.LogicalTracks:
                Add("New Logical Track", OnNewTrackClick);
                Separator();
                Add(
                    "Paste Logical Track",
                    OnPasteTreeLogicalTrackClick,
                    "Ctrl+V",
                    CanPasteLogicalTrack());
                break;
            case ProjectTreeNodeKind.LogicalTrack:
                Add("Rename", OnTreeRenameClick, "F2");
                Add("Bind Event Instrument…", OnTreeBindInstrumentClick);
                Separator();
                Add("Cut", OnCutTreeLogicalTrackClick, "Ctrl+X", _session.CanEditProject);
                Add("Copy", OnCopyTreeLogicalTrackClick, "Ctrl+C");
                Add("Paste", OnPasteTreeLogicalTrackClick, "Ctrl+V", CanPasteLogicalTrack());
                Add("Duplicate", OnDuplicateTreeLogicalTrackClick, "Ctrl+D", _session.CanEditProject);
                Separator();
                Add("Move Up", OnTreeMoveUpClick);
                Add("Move Down", OnTreeMoveDownClick);
                Separator();
                Add("Delete…", OnTreeDeleteClick);
                break;
            case ProjectTreeNodeKind.EventInstrument:
                Add("Open", OnTreeOpenClick);
                Add("Rename", OnTreeRenameClick, "F2");
                Separator();
                Add("Copy", OnCopyTreeInstrumentClick, "Ctrl+C");
                Add("Paste", OnPasteTreeInstrumentClick, "Ctrl+V");
                Add("Duplicate", OnDuplicateTreeInstrumentClick, "Ctrl+D");
                Separator();
                Add("Move Up", OnTreeMoveUpClick);
                Add("Move Down", OnTreeMoveDownClick);
                Separator();
                Add("Delete…", OnTreeDeleteClick);
                break;
            case ProjectTreeNodeKind.DamagedEventInstrument:
            case ProjectTreeNodeKind.DamagedLogicalTrack:
                Add("Delete damaged placeholder…", OnTreeDeleteClick);
                break;
            default:
                Add("Open", OnTreeOpenClick);
                break;
        }
    }

    private void OnTimelineContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu
            || menu.PlacementTarget is not TimelineSurface surface)
        {
            return;
        }

        menu.Items.Clear();
        MenuItem Add(string header, RoutedEventHandler handler, string? gesture = null, bool enabled = true)
        {
            MenuItem item = new()
            {
                Header = header,
                InputGestureText = gesture ?? string.Empty,
                IsEnabled = enabled
            };
            item.Click += handler;
            menu.Items.Add(item);
            return item;
        }
        void Separator() => menu.Items.Add(new Separator());

        Point contextPoint = Mouse.GetPosition(surface);
        double headerWidth = surface.LaneHeaderWidth;
        bool isLaneHeader = contextPoint.X < headerWidth;
        if (surface.SurfaceMode == TimelineSurfaceMode.Arrangement)
        {
            if (surface.IsArrangementEmptyBackground(contextPoint)
                && _session.ActiveWorkspace is TimelineWorkspaceViewModel
                {
                    Mode: TimelineWorkspaceMode.Arrangement
                } arrangementWorkspace)
            {
                ClearArrangementTrackSelection(arrangementWorkspace);
            }
            _trackHeaderContextLane = surface.TryGetArrangementLaneHeader(contextPoint, out int contextLane)
                ? contextLane
                : null;
            _arrangementSharedGroupContextId = surface.TryGetArrangementSharedGroupHeaderTarget(
                    contextPoint,
                    out MidoraId sharedGroupId)
                ? sharedGroupId
                : null;
            surface.SetArrangementSharedGroupContextHighlight(
                _arrangementSharedGroupContextId);
        }
        if (isLaneHeader)
        {
            switch (surface.SurfaceMode)
            {
                case TimelineSurfaceMode.Arrangement:
                    if (!TryGetArrangementHeaderContext(out ArrangementLaneDescriptor header))
                    {
                        Add("New Logical Track", OnNewTrackClick, enabled: _session.CanEditProject);
                        Add("New Logical Track with Instrument…", OnNewTrackWithInstrumentClick,
                            enabled: _session.CanEditProject);
                        Add("New MIDI Track…", OnNewRawMidiTrackClick, enabled: _session.CanEditProject);
                        return;
                    }
                    if (header.Kind is ArrangementLaneKind.DamagedEventInstrument
                        or ArrangementLaneKind.DamagedMidiChannelRoot
                        or ArrangementLaneKind.DamagedLogicalTrack
                        or ArrangementLaneKind.DamagedPureMidiTrack)
                    {
                        Add(
                            "Delete damaged placeholder…",
                            OnArrangementHeaderDeleteClick,
                            enabled: _session.CanEditProject);
                        return;
                    }
                    bool editable = _session.CanEditProject && header.Kind != ArrangementLaneKind.Conductor;
                    if (_arrangementSharedGroupContextId is MidoraId groupId)
                    {
                        if (header.SharedGroupMemberCount > 1)
                        {
                            MenuItem muteGroup = Add(
                                "Mute Shared Group",
                                OnArrangementSharedGroupMuteClick,
                                enabled: true);
                            muteGroup.IsCheckable = true;
                            muteGroup.IsChecked = _session.IsSharedGroupMuted(groupId);
                            MenuItem soloGroup = Add(
                                "Solo Shared Group",
                                OnArrangementSharedGroupSoloClick,
                                enabled: true);
                            soloGroup.IsCheckable = true;
                            soloGroup.IsChecked = _session.IsSharedGroupSolo(groupId);
                        }
                        if (header.Kind == ArrangementLaneKind.LogicalTrack)
                        {
                            Add(
                                "Change Event Instrument for Shared Group…",
                                OnArrangementSharedGroupInstrumentClick,
                                enabled: editable && _session.Project?.EventInstruments.Count > 0);
                        }
                        else if (header.Kind == ArrangementLaneKind.PureMidiTrack)
                        {
                            Add("MIDI Channel Settings…", OnArrangementSharedRootSettingsClick, enabled: editable);
                        }
                        if (header.IsSharedGroup)
                        {
                            (bool groupUp, bool groupDown) = ArrangementSharedGroupMoveAvailability(groupId);
                            Add("Move Shared Group Up", OnArrangementSharedGroupMoveUpClick,
                                enabled: editable && groupUp);
                            Add("Move Shared Group Down", OnArrangementSharedGroupMoveDownClick,
                                enabled: editable && groupDown);
                            Add(
                                "Make All Tracks Independent",
                                OnArrangementSharedGroupMakeIndependentClick,
                                enabled: editable);
                        }
                        Separator();
                        if (header.IsSharedGroup) return;
                    }
                    if (header.Kind == ArrangementLaneKind.Conductor)
                        Add("Open", OnArrangementHeaderOpenClick);
                    if (header.Kind != ArrangementLaneKind.Conductor)
                        Add("Rename…", OnArrangementHeaderRenameClick, "F2", editable);
                    if (header.Kind == ArrangementLaneKind.LogicalTrack)
                    {
                        Add("Edit Event Instrument…", OnArrangementHeaderEditEventInstrumentClick,
                            enabled: header.ParentId.HasValue);
                        Add("Change Event Instrument…", OnTrackHeaderBindClick,
                            enabled: editable && _session.Project?.EventInstruments.Count > 0);
                        Add("Share Instrument State With…", OnLogicalTrackShareStateClick,
                            enabled: editable && _session.Project?.Tracks.Count > 1);
                        Add("Make Independent", OnLogicalTrackMakeIndependentClick,
                            enabled: editable && header.IsSharedGroup);
                    }
                    if (header.Kind == ArrangementLaneKind.PureMidiTrack)
                    {
                        Add("MIDI Route Settings…", OnArrangementTrackRouteSettingsClick, enabled: editable);
                        Add("Share MIDI Channel With…", OnMidiTrackShareChannelClick,
                            enabled: editable && _session.Project?.PureMidiTracks.Count > 1);
                        Add("Make Independent", OnMidiTrackMakeIndependentClick,
                            enabled: editable && header.IsSharedGroup);
                    }
                    if (header.Kind != ArrangementLaneKind.Conductor) Separator();
                    if (header.Kind != ArrangementLaneKind.Conductor)
                    {
                        Add("Cut", OnArrangementHeaderCutClick, "Ctrl+X", editable);
                        Add("Copy", OnArrangementHeaderCopyClick, "Ctrl+C", header.ObjectId is not null);
                        Add("Paste", OnArrangementHeaderPasteClick, "Ctrl+V",
                            _session.CanEditProject && CanPasteArrangementHeader(header));
                        Add("Duplicate", OnArrangementHeaderDuplicateClick, "Ctrl+D", editable);
                        if (header.Kind == ArrangementLaneKind.LogicalTrack)
                        {
                            Add("Duplicate and Share State",
                                OnArrangementHeaderDuplicateAndShareStateClick,
                                enabled: editable && header.ParentId.HasValue);
                        }
                        Separator();
                    }
                    if (header.Kind is ArrangementLaneKind.LogicalTrack or ArrangementLaneKind.PureMidiTrack)
                    {
                        bool hasSegments = ArrangementHeaderSegmentCount(header) > 0;
                        Add("Select All Segments on Track", OnTrackHeaderSelectSegmentsClick, enabled: hasSegments);
                        Add("Add Track Segments to Selection", OnTrackHeaderAddSegmentsToSelectionClick, enabled: hasSegments);
                        Separator();
                    }
                    if (header.Kind != ArrangementLaneKind.Conductor)
                    {
                        (bool canMoveUp, bool canMoveDown) = ArrangementHeaderMoveAvailability(header);
                        Add("Move Up", OnArrangementHeaderMoveUpClick, enabled: editable && canMoveUp);
                        Add("Move Down", OnArrangementHeaderMoveDownClick, enabled: editable && canMoveDown);
                        Separator();
                        Add("Delete…", OnArrangementHeaderDeleteClick, enabled: editable);
                        Separator();
                    }
                    if (header.Kind == ArrangementLaneKind.Conductor)
                    {
                        Add("New Logical Track", OnNewTrackClick, enabled: _session.CanEditProject);
                        Add("New Logical Track with Instrument…", OnNewTrackWithInstrumentClick,
                            enabled: _session.CanEditProject);
                        Add("New MIDI Track…", OnNewRawMidiTrackClick, enabled: _session.CanEditProject);
                    }
                    return;
                case TimelineSurfaceMode.EventLanes
                    when _session.ActiveWorkspace is TimelineWorkspaceViewModel
                    {
                        Mode: TimelineWorkspaceMode.Segment
                    }:
                    bool isMidiEventLane = _session.ActiveWorkspace is TimelineWorkspaceViewModel
                    { ObjectId: MidoraId laneSegmentId }
                        && _session.Project is MidoraProject laneProject
                        && TimelineWorkspaceViewModel.FindMidiSegment(laneProject, laneSegmentId) is not null;
                    Add(
                        isMidiEventLane ? "Add MIDI Event Lane…" : "Add Logical Parameter Lane…",
                        OnAddParameterLaneClick,
                        enabled: _session.CanEditProject);
                    return;
                case TimelineSurfaceMode.EventLanes
                    when _session.ActiveWorkspace is InstrumentWorkspaceViewModel instrumentWorkspace:
                    {
                        Add("Add Event…", OnAddTemplateEventClick, enabled: _session.CanEditProject);
                        Add(
                            "Delete Event Lane…",
                            OnDeleteSubVoiceEventLaneClick,
                            enabled: _session.CanEditProject
                                && instrumentWorkspace.GetRenderLane(
                                    instrumentWorkspace.ActiveRenderLaneIndex)?.EventMappingTarget is not null);
                        return;
                    }
                default:
                    menu.IsOpen = false;
                    return;
            }
        }

        bool canEdit = _session.CanEditProject;
        if (surface.SurfaceMode == TimelineSurfaceMode.Arrangement)
        {
            Add("Open", OnOpenWorkspaceSelectionClick);
            Separator();
        }
        Add("Cut", OnCutClick, "Ctrl+X", canEdit);
        Add("Copy", OnCopyClick, "Ctrl+C");
        Add("Paste", OnPasteClick, "Ctrl+V", canEdit);
        Add("Duplicate", OnDuplicateClick, "Ctrl+D", canEdit);
        Separator();
        Add("Delete", OnDeleteWorkspaceSelectionClick, "Delete", canEdit);
        Separator();
        bool hasSelection = _session.ActiveWorkspace?.Selection.Ids.Count > 0;
        bool hasInvertibleItems = surface.Snapshot?.HasHitTestableItems == true;
        Add("Deselect All", OnDeselectAllTimelineObjectsClick, enabled: hasSelection);
        Add("Invert Selection", OnInvertTimelineSelectionClick, enabled: hasInvertibleItems);
        Separator();
        _timelineSelectionOperationContext = ResolveTimelineSelectionOperationContext(surface);
        TimelineSelectionOperationContext? operationContext = _timelineSelectionOperationContext;
        if (operationContext is not null)
        {
            bool hasOperationSelection = operationContext.Ids.Length != 0;
            if (operationContext.Kind is TimelineSelectionObjectKind.Segments
                or TimelineSelectionObjectKind.MidiSegments
                or TimelineSelectionObjectKind.MixedSegments)
            {
                MenuItem flipHorizontal = new()
                {
                    Header = "Flip Horizontal",
                    IsEnabled = canEdit && hasOperationSelection
                };
                MenuItem exposedOnly = new() { Header = "Exposed Content Only" };
                exposedOnly.Click += OnFlipSegmentsExposedContentHorizontalClick;
                flipHorizontal.Items.Add(exposedOnly);
                MenuItem contentAndSegments = new() { Header = "Exposed Content and Segments" };
                contentAndSegments.Click += OnFlipSegmentsAndContentHorizontalClick;
                flipHorizontal.Items.Add(contentAndSegments);
                menu.Items.Add(flipHorizontal);
            }
            else
            {
                Add(
                    "Flip Horizontal",
                    OnFlipSelectionHorizontalClick,
                    enabled: canEdit && hasOperationSelection);
            }
            if (operationContext.Kind is TimelineSelectionObjectKind.Segments
                or TimelineSelectionObjectKind.MidiSegments
                or TimelineSelectionObjectKind.MixedSegments
                or TimelineSelectionObjectKind.LogicalNotes
                or TimelineSelectionObjectKind.DirectMidiNotes
                or TimelineSelectionObjectKind.TemplateNotes)
            {
                Add(
                    "Flip Vertical",
                    OnFlipSelectionVerticalClick,
                    enabled: canEdit && hasOperationSelection);
            }
            Add(
                "Scale…",
                OnScaleSelectionClick,
                "Ctrl+Q",
                enabled: canEdit && hasOperationSelection);
            if (operationContext.Kind is TimelineSelectionObjectKind.Segments
                or TimelineSelectionObjectKind.MidiSegments
                or TimelineSelectionObjectKind.MixedSegments
                or TimelineSelectionObjectKind.LogicalNotes
                or TimelineSelectionObjectKind.DirectMidiNotes
                or TimelineSelectionObjectKind.TemplateNotes)
            {
                Add(
                    "Transpose…",
                    OnTransposeSelectionClick,
                    "Ctrl+T",
                    enabled: canEdit && hasOperationSelection);
            }
            Add(
                "Batch Edit…",
                OnBatchEditSelectionClick,
                "Ctrl+E",
                enabled: canEdit && hasOperationSelection);
            Separator();
        }
        if (_session.ActiveWorkspace is WorkspaceViewModel activeWorkspace)
        {
            ObjectPropertiesViewModel properties = _session.CreateObjectProperties(activeWorkspace);
            Add(
                "Properties…",
                OnEditTimelinePropertiesClick,
                "Ctrl+P",
                enabled: ObjectPropertiesProjection.CanEditInPropertiesDialog(
                    activeWorkspace,
                    properties));
            Separator();
        }
        Add("Set Time Range from Object Selection", OnSetTimeRangeFromObjectsClick);
        Add("Select Objects in Time Range", OnSelectObjectsInTimeRangeClick);
        Add("Clear Time Range", OnClearTimeRangeClick);
    }

    private void OnEditTimelinePropertiesClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not WorkspaceViewModel workspace) return;
        ObjectPropertiesViewModel properties = _session.CreateObjectProperties(workspace);
        if (!ObjectPropertiesProjection.CanEditInPropertiesDialog(workspace, properties))
        {
            ShowUnavailable("Properties", "The current object has no available properties.");
            return;
        }
        ObjectPropertiesDialog dialog = new(_session, workspace) { Owner = this };
        _ = dialog.ShowDialog();
        _session.RefreshWorkspaceSelection(workspace);
    }

    private void OnTreeRenameClick(object sender, RoutedEventArgs e) => BeginTreeRename();

    private void BeginTreeRename()
    {
        if (ProjectTree.SelectedItem is not ProjectTreeNode node
            || node.Kind is not (ProjectTreeNodeKind.LogicalTrack
                or ProjectTreeNodeKind.EventInstrument))
        {
            return;
        }
        string kind = node.Kind == ProjectTreeNodeKind.LogicalTrack
            ? "Logical Track"
            : "Event Instrument";
        TextInputDialog dialog = new(
            $"Rename {kind}",
            $"Enter the {kind} name.",
            node.Title)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            RunSynchronous($"Rename {kind}", () =>
                _session.RenameProjectTreeNode(node, dialog.Value));
        }
    }

    private void OnTreeRenameLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Visibility: Visibility.Visible } textBox)
        {
            textBox.Focus();
            textBox.SelectAll();
        }
    }

    private void OnTreeRenameLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: ProjectTreeNode { IsRenaming: true } } textBox)
        {
            CommitTreeRename(textBox);
        }
    }

    private void OnTreeRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ProjectTreeNode node } textBox) return;
        if (e.Key == Key.Enter)
        {
            CommitTreeRename(textBox);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            node.EditText = node.Title;
            node.IsRenaming = false;
            ProjectTree.Focus();
            e.Handled = true;
        }
    }

    private void CommitTreeRename(TextBox textBox)
    {
        if (textBox.DataContext is not ProjectTreeNode { IsRenaming: true } node) return;
        try
        {
            node.IsRenaming = false;
            _session.RenameProjectTreeNode(node, node.EditText);
            _session.SetStatusMessage(null);
        }
        catch (Exception exception)
        {
            node.IsRenaming = true;
            textBox.BorderBrush = (Brush)FindResource("Brush.Red");
            textBox.ToolTip = exception.Message;
            textBox.Focus();
            textBox.SelectAll();
        }
    }

    private void OnTreeMoveUpClick(object sender, RoutedEventArgs e) => MoveSelectedTreeNode(-1);
    private void OnTreeMoveDownClick(object sender, RoutedEventArgs e) => MoveSelectedTreeNode(1);

    private void MoveSelectedTreeNode(int direction)
    {
        if (ProjectTree.SelectedItem is not ProjectTreeNode node) return;
        RunSynchronous("Reorder Project Object", () => _session.MoveProjectTreeNode(node, direction));
    }

    private void OnTreeDeleteClick(object sender, RoutedEventArgs e) => DeleteSelectedTreeNode();

    private void OnTreeBindInstrumentClick(object sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ProjectTreeNode
            {
                Kind: ProjectTreeNodeKind.LogicalTrack,
                ObjectId: MidoraId trackId
            }
            || _session.Project is null)
        {
            return;
        }
        object unbound = new();
        List<SelectionDialogItem> options =
        [
            new(unbound, "Unbound", "Compilation reports the track as unbound until an Event Instrument is selected.")
        ];
        options.AddRange(_session.Project.EventInstruments.Select(instrument => new SelectionDialogItem(
            instrument.Id,
            string.IsNullOrWhiteSpace(instrument.Name) ? "Unnamed Event Instrument" : instrument.Name,
            $"{instrument.SubVoices.Count} SubVoice(s)")));
        SelectionDialog dialog = new(
            "Bind Logical Track",
            "Select the Event Instrument used by this Logical Track.",
            options)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true) return;
        MidoraId? instrumentId = ReferenceEquals(dialog.SelectedValue, unbound)
            ? null
            : (MidoraId?)dialog.SelectedValue;
        LogicalTrack track = _session.Project.Tracks.Single(item => item.Id == trackId);
        if (instrumentId is MidoraId selectedInstrumentId)
        {
            BindTrackToInstrument(track, selectedInstrumentId);
        }
        else
        {
            RunSynchronous(
                "Unbind Logical Track",
                () => _session.BindLogicalTrack(trackId, null));
        }
    }

    private void DeleteSelectedTreeNode()
    {
        if (ProjectTree.SelectedItem is not ProjectTreeNode node || _session.Project is null) return;
        string? detail = node.Kind switch
        {
            ProjectTreeNodeKind.LogicalTrack when node.ObjectId is MidoraId id =>
                $"Delete Logical Track '{node.Title}' and its {_session.Project.Tracks.Single(item => item.Id == id).Segments.Count} Segment(s)?",
            ProjectTreeNodeKind.EventInstrument when node.ObjectId is MidoraId id =>
                $"Delete Event Instrument '{node.Title}'? {_session.Project.EventInstrumentUsages.Count(item => item.EventInstrumentId == id)} usage(s) still reference it.",
            ProjectTreeNodeKind.DamagedEventInstrument =>
                $"Permanently remove damaged Event Instrument placeholder '{node.Title}' from the Project? Bound Logical Tracks will become unbound and retain the last known instrument name. This operation is undoable until the Project closes.",
            ProjectTreeNodeKind.DamagedLogicalTrack =>
                $"Permanently remove damaged Logical Track placeholder '{node.Title}' from the Project? This operation is undoable until the Project closes.",
            _ => null
        };
        if (detail is null) return;
        if (MessageDialog.Show(this, detail, "Delete Project Object", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        RunSynchronous("Delete Project Object", () => _session.DeleteProjectTreeNode(node, confirmed: true));
    }

    private void OnInstrumentListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox list && list.SelectedItem is InstrumentListItem instrument)
        {
            e.Handled = true;
            _ = Dispatcher.BeginInvoke(
                () => RunSynchronous("Open Event Instrument", () => _session.OpenInstrument(instrument.Id)),
                DispatcherPriority.Normal);
        }
    }

    private void OnInstrumentListMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _instrumentListDragStart = e.GetPosition((IInputElement)sender);
        _instrumentListDragId = FindListBoxItem(e.OriginalSource as DependencyObject)?.DataContext switch
        {
            InstrumentListItem item => item.Id,
            EventInstrumentBrowserRow row => row.Id,
            _ => null
        };
    }

    private static ListBoxItem? FindListBoxItem(DependencyObject? current)
    {
        while (current is not null && current is not ListBoxItem)
        {
            current = VisualTreeHelper.GetParent(current);
        }
        return current as ListBoxItem;
    }

    private void OnInstrumentListMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not ListBox list
            || e.LeftButton != MouseButtonState.Pressed
            || _instrumentListDragStart is not Point start
            || _instrumentListDragId is not MidoraId instrumentId)
        {
            return;
        }
        Point current = e.GetPosition(list);
        if (!HasReachedUiReorderDragThreshold(start, current))
        {
            return;
        }
        _instrumentListDragStart = null;
        _instrumentListDragId = null;
        DataObject data = new(EventInstrumentDragFormat, instrumentId.Value);
        DragDrop.DoDragDrop(list, data, DragDropEffects.Link | DragDropEffects.Move);
    }

    private void OnEventInstrumentBrowserDragOver(object sender, DragEventArgs e)
    {
        ListBoxItem? targetContainer = null;
        bool insertAfter = false;
        bool accepted = false;
        if (sender is ListBox list)
        {
            accepted = TryResolveEventInstrumentBrowserDrop(
                list,
                e,
                out _,
                out _,
                out targetContainer,
                out insertAfter);
        }
        if (accepted)
        {
            SetEventInstrumentBrowserDropPreview(targetContainer, insertAfter);
        }
        else
        {
            ClearEventInstrumentBrowserDropPreview();
        }
        e.Effects = accepted ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnEventInstrumentBrowserDragLeave(object sender, DragEventArgs e)
    {
        ClearEventInstrumentBrowserDropPreview();
        e.Handled = true;
    }

    private void OnEventInstrumentBrowserDrop(object sender, DragEventArgs e)
    {
        MidoraId instrumentId = default;
        int targetIndex = -1;
        bool accepted = false;
        if (sender is ListBox list)
        {
            accepted = TryResolveEventInstrumentBrowserDrop(
                list,
                e,
                out instrumentId,
                out targetIndex,
                out _,
                out _);
        }
        ClearEventInstrumentBrowserDropPreview();
        e.Effects = accepted ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
        if (!accepted) return;
        RunSynchronous("Move Event Instrument", () => _session.Execute(
            ProjectDomainEditCommands.ReorderEventInstrument(instrumentId, targetIndex)));
    }

    private bool TryResolveEventInstrumentBrowserDrop(
        ListBox list,
        DragEventArgs e,
        out MidoraId instrumentId,
        out int targetIndex,
        out ListBoxItem? targetContainer,
        out bool insertAfter)
    {
        instrumentId = default;
        targetIndex = -1;
        targetContainer = null;
        insertAfter = false;
        if (!_session.CanEditProject
            || _session.Project is not MidoraProject project
            || FindVisualAncestor<ScrollBar>(e.OriginalSource as DependencyObject) is not null
            || !e.Data.GetDataPresent(EventInstrumentDragFormat)
            || e.Data.GetData(EventInstrumentDragFormat) is not long rawId
            || rawId <= 0)
        {
            return false;
        }
        MidoraId candidate = new(rawId);
        int sourceIndex = project.EventInstruments.FindIndex(value => value.Id == candidate);
        if (sourceIndex < 0) return false;

        int boundaryIndex;
        targetContainer = FindListBoxItem(e.OriginalSource as DependencyObject);
        if (targetContainer?.DataContext is EventInstrumentBrowserRow targetRow)
        {
            int itemIndex = project.EventInstruments.FindIndex(value => value.Id == targetRow.Id);
            if (itemIndex < 0) return false;
            insertAfter = e.GetPosition(targetContainer).Y >= targetContainer.ActualHeight / 2;
            boundaryIndex = itemIndex + (insertAfter ? 1 : 0);
        }
        else
        {
            Point point = e.GetPosition(list);
            if (point.Y < 0 || point.Y >= list.ActualHeight) return false;
            boundaryIndex = project.EventInstruments.Count;
            if (project.EventInstruments.Count != 0
                && list.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem first)
            {
                double firstTop = first.TranslatePoint(new Point(0, 0), list).Y;
                if (point.Y < firstTop)
                {
                    boundaryIndex = 0;
                    targetContainer = first;
                    insertAfter = false;
                }
                else if (list.ItemContainerGenerator.ContainerFromIndex(
                             project.EventInstruments.Count - 1) is ListBoxItem last)
                {
                    targetContainer = last;
                    insertAfter = true;
                }
            }
        }

        if (boundaryIndex > sourceIndex) boundaryIndex--;
        instrumentId = candidate;
        targetIndex = Math.Clamp(boundaryIndex, 0, project.EventInstruments.Count - 1);
        return true;
    }

    private void SetEventInstrumentBrowserDropPreview(
        ListBoxItem? targetContainer,
        bool insertAfter)
    {
        if (ReferenceEquals(_instrumentBrowserDropContainer, targetContainer)
            && _instrumentBrowserDropAfter == insertAfter)
        {
            return;
        }
        ClearEventInstrumentBrowserDropPreview();
        _instrumentBrowserDropContainer = targetContainer;
        _instrumentBrowserDropAfter = insertAfter;
        if (targetContainer is null) return;
        targetContainer.SetResourceReference(Control.BorderBrushProperty, "Brush.Info");
        targetContainer.BorderThickness = insertAfter
            ? new Thickness(0, 0, 0, 2)
            : new Thickness(0, 2, 0, 0);
    }

    private void ClearEventInstrumentBrowserDropPreview()
    {
        if (_instrumentBrowserDropContainer is not null)
        {
            _instrumentBrowserDropContainer.ClearValue(Control.BorderBrushProperty);
            _instrumentBrowserDropContainer.ClearValue(Control.BorderThicknessProperty);
        }
        _instrumentBrowserDropContainer = null;
        _instrumentBrowserDropAfter = false;
    }

    private void OnTimelineInstrumentDragOver(object sender, DragEventArgs e)
    {
        bool accepted = TryResolveInstrumentDrop(
            sender,
            e,
            out LogicalTrack? track,
            out _,
            out int? insertionIndex);
        if (sender is TimelineSurface surface)
        {
            surface.SetExternalArrangementInsertionPreview(
                accepted && track is null ? insertionIndex : null);
        }
        e.Effects = accepted ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnTimelineInstrumentDragLeave(object sender, DragEventArgs e)
    {
        if (sender is TimelineSurface surface)
        {
            surface.SetExternalArrangementInsertionPreview(null);
        }
        e.Handled = true;
    }

    private void OnTimelineInstrumentDrop(object sender, DragEventArgs e)
    {
        if (sender is TimelineSurface surface)
        {
            surface.SetExternalArrangementInsertionPreview(null);
        }
        if (!TryResolveInstrumentDrop(
                sender,
                e,
                out LogicalTrack? track,
                out MidoraId instrumentId,
                out int? insertionIndex))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Link;
        e.Handled = true;
        MidoraId? trackId = track?.Id;
        int? trackInsertionIndex = insertionIndex;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () =>
            {
                if (trackId is MidoraId existingTrackId)
                {
                    LogicalTrack? current = _session.Project?.Tracks
                        .FirstOrDefault(value => value.Id == existingTrackId);
                    if (current is not null) BindTrackToInstrument(current, instrumentId);
                }
                else
                {
                    RunSynchronous("Create Logical Track", () => _session.Execute(
                        ProjectDomainEditCommands.CreateLogicalTrack(
                            eventInstrumentId: instrumentId,
                            insertionIndex: trackInsertionIndex)));
                }
            });
    }

    private bool TryResolveInstrumentDrop(
        object sender,
        DragEventArgs e,
        out LogicalTrack? track,
        out MidoraId instrumentId,
        out int? insertionIndex)
    {
        track = null;
        instrumentId = default;
        insertionIndex = null;
        if (sender is not TimelineSurface surface
            || surface.SurfaceMode != TimelineSurfaceMode.Arrangement
            || !_session.CanEditProject
            || _session.Project is not MidoraProject project
            || !e.Data.GetDataPresent(EventInstrumentDragFormat)
            || e.Data.GetData(EventInstrumentDragFormat) is not long rawId
            || rawId <= 0
            || _session.ActiveWorkspace is not TimelineWorkspaceViewModel arrangement)
        {
            return false;
        }
        MidoraId candidateInstrumentId = new(rawId);
        if (!project.EventInstruments.Any(item => item.Id == candidateInstrumentId)) return false;
        instrumentId = candidateInstrumentId;
        Point point = e.GetPosition(surface);
        if (surface.TryGetArrangementLaneHeader(point, out int lane)
            && arrangement.GetArrangementLane(lane) is
            { Kind: ArrangementLaneKind.LogicalTrack, ObjectId: MidoraId logicalTrackId })
        {
            track = project.Tracks.FirstOrDefault(value => value.Id == logicalTrackId);
            return track is not null;
        }
        if (!surface.TryGetArrangementTrackInsertionIndex(point, out int candidateInsertionIndex))
        {
            return false;
        }
        insertionIndex = Math.Clamp(candidateInsertionIndex, 0, project.ArrangementTracks.Count);
        return true;
    }

    private void BindTrackToInstrument(LogicalTrack track, MidoraId instrumentId)
    {
        if (_session.Project is not MidoraProject project) return;
        EventInstrument? instrument = project.EventInstruments.FirstOrDefault(item => item.Id == instrumentId);
        MidoraId? currentInstrumentId = project.ResolveEventInstrumentDefinitionId(track);
        if (instrument is null || currentInstrumentId == instrumentId) return;
        if (currentInstrumentId is not null
            && MessageDialog.Show(
                this,
                $"Rebind Logical Track '{TimelineWorkspaceViewModel.TrackDisplayName(project, track)}' to Event Instrument '{instrument.Name}'?",
                "Rebind Logical Track",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        RunSynchronous("Bind Logical Track", () =>
            _session.Execute(ProjectDomainEditCommands.BindLogicalTrack(track.Id, instrumentId)));
    }

    private void OnListBoxRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current is not null && current is not ListBoxItem)
        {
            current = VisualTreeHelper.GetParent(current);
        }
        if (current is ListBoxItem item)
        {
            item.IsSelected = true;
            item.Focus();
            if (ItemsControl.ItemsControlFromItemContainer(item) is ListBox list
                && list.DataContext is InstrumentWorkspaceViewModel workspace
                && TryGetInstrumentStructureItemId(item.DataContext, out _))
            {
                SelectInstrumentStructureItem(list, workspace, item.DataContext);
            }
        }
    }

    private void OnOpenLibraryInstrumentClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is LibraryWorkspaceViewModel
            {
                SelectedInstrument: InstrumentListItem selected
            })
        {
            _session.OpenInstrument(selected.Id);
        }
    }

    private void OnDeleteLibraryInstrumentClick(object sender, RoutedEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || _session.ActiveWorkspace is not LibraryWorkspaceViewModel
            {
                SelectedInstrument: InstrumentListItem selected
            })
        {
            return;
        }
        int bindings = project.EventInstrumentUsages.Count(
            usage => usage.EventInstrumentId == selected.Id);
        if (MessageDialog.Show(
                this,
                $"Delete Event Instrument '{selected.Name}'? {bindings} usage(s) still reference it.",
                "Delete Event Instrument",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        RunSynchronous("Delete Event Instrument", () => _session.Execute(
            ProjectDomainEditCommands.DeleteEventInstrument(selected.Id, referencedDeletionConfirmed: true)));
    }

    private void OnDuplicateLibraryInstrumentClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not LibraryWorkspaceViewModel
            {
                SelectedInstrument: InstrumentListItem selected
            })
        {
            return;
        }
        RunSynchronous("Duplicate Event Instrument", () =>
            _session.Execute(ProjectDomainEditCommands.DuplicateEventInstrument(selected.Id)));
    }

    private void OnCopyLibraryInstrumentClick(object sender, RoutedEventArgs e) =>
        CopySelectedEventInstrument();

    private void OnPasteLibraryInstrumentClick(object sender, RoutedEventArgs e) =>
        PasteEventInstrumentClipboard();

    private bool CopySelectedEventInstrument()
    {
        if (!TryGetSelectedEventInstrumentId(out MidoraId instrumentId))
        {
            return false;
        }
        return CopyEventInstrument(instrumentId);
    }

    private bool CopyEventInstrument(MidoraId instrumentId)
    {
        if (_session.Document is not ProjectDocumentSession document) return false;
        RunSynchronous("Copy Event Instrument", () =>
        {
            ProjectObjectClipboardPayload payload =
                ProjectObjectClipboard.CopyEventInstrument(document, instrumentId);
            Clipboard.SetDataObject(payload.PlainTextSummary, copy: true);
            _projectClipboard = payload;
            _clipboardDocument = document;
            _session.SetStatusMessage($"Copied {payload.PlainTextSummary}.");
        });
        return true;
    }

    private bool PasteEventInstrumentClipboard(bool allowSelectedTreeTarget = false)
    {
        if (!_session.CanEditProject
            || _session.Document is not ProjectDocumentSession document
            || _session.Project is not MidoraProject project
            || _projectClipboard is not ProjectObjectClipboardPayload
            { Kind: ProjectObjectClipboardKind.EventInstrument } payload
            || !ReferenceEquals(document, _clipboardDocument)
            || !IsEventInstrumentClipboardTarget(allowSelectedTreeTarget))
        {
            return false;
        }
        RunSynchronous("Paste Event Instrument", () =>
        {
            long firstNewStableId = project.NextStableId;
            _session.Execute(ProjectObjectClipboard.CreatePasteEventInstrumentCommand(
                document,
                payload));
            if (_session.ActiveWorkspace is LibraryWorkspaceViewModel library)
            {
                library.SelectedInstrument = library.Instruments
                    .FirstOrDefault(value => value.Id.Value >= firstNewStableId);
            }
            _session.SetStatusMessage($"Pasted {payload.PlainTextSummary}.");
        });
        return true;
    }

    private bool TryGetSelectedEventInstrumentId(out MidoraId instrumentId)
    {
        if (_session.ActiveWorkspace is LibraryWorkspaceViewModel
            {
                SelectedInstrument: InstrumentListItem selected
            })
        {
            instrumentId = selected.Id;
            return true;
        }
        if (ProjectTree.IsKeyboardFocusWithin
            && ProjectTree.SelectedItem is ProjectTreeNode
            { Kind: ProjectTreeNodeKind.EventInstrument, ObjectId: MidoraId selectedId })
        {
            instrumentId = selectedId;
            return true;
        }
        instrumentId = default;
        return false;
    }

    private bool IsEventInstrumentClipboardTarget(bool allowSelectedTreeTarget) =>
        _session.ActiveWorkspace is LibraryWorkspaceViewModel
        || (ProjectTree.IsKeyboardFocusWithin || allowSelectedTreeTarget)
            && ProjectTree.SelectedItem is ProjectTreeNode
            {
                Kind: ProjectTreeNodeKind.InstrumentLibrary
                    or ProjectTreeNodeKind.EventInstrument
            };

    private void OnCopyTreeInstrumentClick(object sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is ProjectTreeNode
            { Kind: ProjectTreeNodeKind.EventInstrument, ObjectId: MidoraId instrumentId })
        {
            _ = CopyEventInstrument(instrumentId);
        }
    }

    private void OnPasteTreeInstrumentClick(object sender, RoutedEventArgs e) =>
        _ = PasteEventInstrumentClipboard(allowSelectedTreeTarget: true);

    private void OnDuplicateTreeInstrumentClick(object sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ProjectTreeNode
            { Kind: ProjectTreeNodeKind.EventInstrument, ObjectId: MidoraId instrumentId })
        {
            return;
        }
        RunSynchronous("Duplicate Event Instrument", () => _session.Execute(
            ProjectDomainEditCommands.DuplicateEventInstrument(instrumentId)));
    }

    private void OnCutTreeLogicalTrackClick(object sender, RoutedEventArgs e) =>
        CutOrCopySelectedLogicalTrack(cut: true, requireTreeSelection: true);

    private void OnCopyTreeLogicalTrackClick(object sender, RoutedEventArgs e) =>
        CutOrCopySelectedLogicalTrack(cut: false, requireTreeSelection: true);

    private void OnPasteTreeLogicalTrackClick(object sender, RoutedEventArgs e) =>
        PasteLogicalTrackClipboard(ResolveLogicalTrackPasteIndex(preferTreeSelection: true));

    private void OnDuplicateTreeLogicalTrackClick(object sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ProjectTreeNode
            { Kind: ProjectTreeNodeKind.LogicalTrack, ObjectId: MidoraId trackId })
        {
            return;
        }
        RunSynchronous("Duplicate Logical Track", () => _session.Execute(
            ProjectDomainEditCommands.DuplicateLogicalTrack(trackId)));
    }

    private bool CutOrCopySelectedLogicalTrack(bool cut, bool requireTreeSelection = false)
    {
        if (!TryGetSelectedLogicalTrack(out LogicalTrack? track, out _, requireTreeSelection)
            || _session.Document is not ProjectDocumentSession document
            || cut && !_session.CanEditProject)
        {
            return false;
        }
        RunSynchronous(cut ? "Cut Logical Track" : "Copy Logical Track", () =>
        {
            ProjectObjectClipboardPayload payload;
            IProjectEditCommand? delete = null;
            if (cut)
            {
                ProjectObjectClipboardCutPreparation preparation =
                    ProjectObjectClipboard.PrepareCutLogicalTrack(document, track.Id);
                payload = preparation.Payload;
                delete = preparation.DeleteAfterSuccessfulClipboardWrite;
            }
            else
            {
                payload = ProjectObjectClipboard.CopyLogicalTrack(document, track.Id);
            }
            Clipboard.SetDataObject(payload.PlainTextSummary, copy: true);
            _projectClipboard = payload;
            _clipboardDocument = document;
            if (delete is not null)
            {
                _session.Execute(delete);
            }
            _session.SetStatusMessage($"{(cut ? "Cut" : "Copied")} {payload.PlainTextSummary}.");
        });
        return true;
    }

    private bool CanPasteLogicalTrack() =>
        _session.CanEditProject
        && _session.Document is ProjectDocumentSession document
        && _projectClipboard is { Kind: ProjectObjectClipboardKind.LogicalTrack }
        && ReferenceEquals(document, _clipboardDocument);

    private bool PasteLogicalTrackClipboard(int insertionIndex)
    {
        if (!CanPasteLogicalTrack()
            || _session.Document is not ProjectDocumentSession document
            || _projectClipboard is not ProjectObjectClipboardPayload payload
            || _session.Project is not MidoraProject project)
        {
            return false;
        }
        MidoraId? targetInstrumentId = ResolveLogicalTrackPasteTarget(project);
        if (targetInstrumentId is null) return false;
        int targetIndex = Math.Clamp(insertionIndex, 0, project.ArrangementTracks.Count);
        RunSynchronous("Paste Logical Track", () =>
        {
            _session.Execute(ProjectObjectClipboard.CreatePasteLogicalTrackCommand(
                document,
                payload,
                targetInstrumentId.Value,
                targetIndex));
            _session.SetStatusMessage($"Pasted {payload.PlainTextSummary}.");
        });
        return true;
    }

    private MidoraId? ResolveLogicalTrackPasteTarget(MidoraProject project)
    {
        if (_session.ActiveWorkspace is TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Arrangement
            } arrangement)
        {
            int? lane = _trackHeaderContextLane ?? arrangement.ActiveLane;
            if (lane is int laneIndex
                && arrangement.GetArrangementLane(laneIndex) is ArrangementLaneDescriptor descriptor)
            {
                if (descriptor.Kind == ArrangementLaneKind.LogicalTrack
                    && descriptor.ParentId is MidoraId id
                    && project.EventInstruments.Any(value => value.Id == id))
                {
                    return id;
                }
            }
        }
        if (project.EventInstruments.Count == 1)
            return project.EventInstruments[0].Id;
        if (project.EventInstruments.Count == 0)
        {
            ShowUnavailable("Paste Logical Track", "Create an Event Instrument first.");
            return null;
        }
        SelectionDialog dialog = new(
            "Paste Logical Track",
            "Select the Event Instrument Definition for the pasted independent usage.",
            project.EventInstruments.Select(value => new SelectionDialogItem(
                value.Id,
                string.IsNullOrWhiteSpace(value.Name) ? "Unnamed Event Instrument" : value.Name,
                $"{value.SubVoices.Count} SubVoice(s)")))
        {
            Owner = this
        };
        return dialog.ShowDialog() == true && dialog.SelectedValue is MidoraId selected
            ? selected
            : null;
    }

    private int ResolveLogicalTrackPasteIndex(
        bool preferTreeSelection = false,
        bool preferTrackHeaderContext = false)
    {
        if (_session.Project is not MidoraProject project)
        {
            return 0;
        }
        if ((preferTreeSelection || ProjectTree.IsKeyboardFocusWithin)
            && ProjectTree.SelectedItem is ProjectTreeNode treeNode)
        {
            if (treeNode is { Kind: ProjectTreeNodeKind.LogicalTrack, ObjectId: MidoraId trackId })
            {
                int index = project.ArrangementTracks.FindIndex(value => value.TrackId == trackId);
                if (index >= 0) return index + 1;
            }
            if (treeNode.Kind == ProjectTreeNodeKind.LogicalTracks)
            {
                return project.ArrangementTracks.Count;
            }
        }
        if (preferTrackHeaderContext
            && _trackHeaderContextLane is int contextLane
            && _session.ActiveWorkspace is TimelineWorkspaceViewModel arrangement
            && arrangement.GetArrangementLane(contextLane) is { ObjectId: MidoraId contextTrackId })
        {
            int index = project.ArrangementTracks.FindIndex(value => value.TrackId == contextTrackId);
            if (index >= 0) return index + 1;
        }
        if (_logicalTrackShortcutTrackId is MidoraId shortcutTrackId
            && GetFocusedTimelineSurface() is { SurfaceMode: TimelineSurfaceMode.Arrangement }
            && _session.ActiveWorkspace is TimelineWorkspaceViewModel
            { Mode: TimelineWorkspaceMode.Arrangement })
        {
            int shortcutIndex = project.ArrangementTracks.FindIndex(value => value.TrackId == shortcutTrackId);
            if (shortcutIndex >= 0) return shortcutIndex + 1;
        }
        if (_session.ActiveWorkspace is TimelineWorkspaceViewModel
            { Mode: TimelineWorkspaceMode.Arrangement, ActiveLane: int activeLane } timeline)
        {
            ArrangementLaneDescriptor? lane = timeline.GetArrangementLane(activeLane);
            int index = lane?.ObjectId is MidoraId activeTrackId
                ? project.ArrangementTracks.FindIndex(value => value.TrackId == activeTrackId)
                : -1;
            return index < 0 ? project.ArrangementTracks.Count : index + 1;
        }
        return project.ArrangementTracks.Count;
    }

    private bool TryGetSelectedLogicalTrack(
        out LogicalTrack track,
        out int index,
        bool requireTreeSelection = false)
    {
        track = null!;
        index = -1;
        if (_session.Project is not MidoraProject project)
        {
            return false;
        }
        if (ProjectTree.SelectedItem is ProjectTreeNode
            { Kind: ProjectTreeNodeKind.LogicalTrack, ObjectId: MidoraId trackId }
            && (ProjectTree.IsKeyboardFocusWithin || requireTreeSelection))
        {
            index = project.Tracks.FindIndex(value => value.Id == trackId);
        }
        else if (!requireTreeSelection
            && _logicalTrackShortcutTrackId is MidoraId shortcutTrackId
            && GetFocusedTimelineSurface() is { SurfaceMode: TimelineSurfaceMode.Arrangement }
            && _session.ActiveWorkspace is TimelineWorkspaceViewModel
            { Mode: TimelineWorkspaceMode.Arrangement })
        {
            index = project.Tracks.FindIndex(value => value.Id == shortcutTrackId);
        }
        if ((uint)index >= (uint)project.Tracks.Count)
        {
            return false;
        }
        track = project.Tracks[index];
        return true;
    }

    private void OnAddSubVoiceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: InstrumentWorkspaceViewModel { ObjectId: MidoraId instrumentId } workspace }) return;
        RunSynchronous("Create SubVoice", () => ExecuteAndSelectCreated(
            ProjectDomainEditCommands.CreateSubVoice(instrumentId), workspace));
    }

    private void OnDuplicateSubVoiceClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId,
                Selection.Primary: MidoraId subVoiceId
            }
            || _session.Project?.EventInstruments
                .FirstOrDefault(item => item.Id == instrumentId)?.SubVoices
                .Any(item => item.Id == subVoiceId) != true)
        {
            _session.SetStatusMessage("Select one SubVoice to duplicate.", isError: true);
            return;
        }
        InstrumentWorkspaceViewModel workspace = (InstrumentWorkspaceViewModel)_session.ActiveWorkspace;
        RunSynchronous("Duplicate SubVoice", () => ExecuteAndSelectCreated(
            ProjectDomainEditCommands.DuplicateSubVoice(instrumentId, subVoiceId), workspace));
    }

    private void OnInstrumentStructureCutClick(object sender, RoutedEventArgs e) =>
        CutOrCopyProjectSelection(cut: true);

    private void OnInstrumentStructureCopyClick(object sender, RoutedEventArgs e) =>
        CutOrCopyProjectSelection(cut: false);

    private void OnInstrumentStructurePasteClick(object sender, RoutedEventArgs e) =>
        PasteProjectSelection();

    private void OnInstrumentStructureDeleteClick(object sender, RoutedEventArgs e) =>
        DeleteWorkspaceSelection();

    private void OnMoveSubVoiceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string directionText }
            || !int.TryParse(directionText, out int direction)
            || _session.ActiveWorkspace is not InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId,
                Selection.Primary: MidoraId subVoiceId
            }
            || _session.Project?.EventInstruments.FirstOrDefault(item => item.Id == instrumentId)
                is not EventInstrument instrument)
        {
            _session.SetStatusMessage("Select one SubVoice to reorder.", isError: true);
            return;
        }
        int oldIndex = instrument.SubVoices.FindIndex(item => item.Id == subVoiceId);
        int newIndex = Math.Clamp(oldIndex + direction, 0, instrument.SubVoices.Count - 1);
        if (oldIndex < 0 || newIndex == oldIndex) return;
        RunSynchronous("Reorder SubVoice", () => _session.Execute(
            ProjectDomainEditCommands.ReorderSubVoice(instrumentId, subVoiceId, newIndex)));
    }

    private void OnInstrumentIsolationClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { IsChecked: bool enabled } checkBox
            || _session.ActiveWorkspace is not InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId
            } workspace
            || _session.Project?.EventInstruments.FirstOrDefault(item => item.Id == instrumentId)
                is not EventInstrument instrument)
        {
            return;
        }
        if (instrument.RequiresChannelIsolation == enabled) return;
        if (!_session.CanEditProject)
        {
            checkBox.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
            return;
        }
        if (!RunSynchronous("Change Event Instrument Isolation", () => _session.Execute(
                ProjectDomainEditCommands.UpdateEventInstrumentIsolation(instrumentId, enabled))))
        {
            _session.RefreshWorkspace(workspace);
            checkBox.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
        }
    }

    private void OnInstrumentConfigurationLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: string field }
            || _session.ActiveWorkspace is not InstrumentWorkspaceViewModel workspace
            || workspace.ObjectId is not MidoraId instrumentId)
        {
            return;
        }
        if (!_session.CanEditProject)
        {
            _session.RefreshWorkspace(workspace);
            return;
        }
        if (!RunSynchronous("Update Event Instrument configuration", () =>
        {
            IProjectEditCommand command = field switch
            {
                "Name" => ProjectDomainEditCommands.RenameEventInstrument(
                    instrumentId,
                    workspace.InstrumentNameText),
                "Description" => ProjectDomainEditCommands.UpdateEventInstrumentDescription(
                    instrumentId,
                    string.IsNullOrWhiteSpace(workspace.InstrumentDescriptionText)
                        ? null
                        : workspace.InstrumentDescriptionText),
                "RootNote" => ProjectDomainEditCommands.UpdateEventInstrumentRootNote(
                    instrumentId,
                    int.Parse(workspace.InstrumentRootNoteText, NumberStyles.Integer, CultureInfo.InvariantCulture)),
                "TemplateLength" => ProjectDomainEditCommands.UpdateEventInstrumentTemplateLength(
                    instrumentId,
                    long.Parse(workspace.InstrumentTemplateLengthText, NumberStyles.Integer, CultureInfo.InvariantCulture)),
                _ => throw new InvalidOperationException("Unknown Event Instrument configuration field.")
            };
            _session.Execute(command);
        }))
        {
            _session.RefreshWorkspace(workspace);
        }
    }

    private void OnSelectInstrumentColorClick(object sender, RoutedEventArgs e)
    {
        if (!_session.CanEditProject
            || _session.ActiveWorkspace is not InstrumentWorkspaceViewModel { ObjectId: MidoraId instrumentId }
            || _session.Project?.EventInstruments.FirstOrDefault(value => value.Id == instrumentId)
                is not EventInstrument instrument)
        {
            return;
        }
        ColorPickerDialog dialog = new(
            instrument.Color.Red,
            instrument.Color.Green,
            instrument.Color.Blue)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true) return;
        RunSynchronous("Change Event Instrument Color", () => _session.Execute(
            ProjectDomainEditCommands.UpdateEventInstrumentColor(
                instrumentId,
                new MidoraColor(dialog.Red, dialog.Green, dialog.Blue))));
    }

    private void OnInstrumentInitialStateFieldLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: PropertyField field }
            || _session.ActiveWorkspace is not InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId
            } workspace)
        {
            return;
        }
        if (!_session.CanEditProject)
        {
            _session.RefreshWorkspace(workspace);
            return;
        }
        RunSynchronous("Update Event Instrument Initial State", () =>
        {
            string text = field.Value.Trim();
            int? value = text.Length == 0
                ? null
                : int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            _session.Execute(ProjectDomainEditCommands.UpdateEventInstrumentInitialStateValue(
                instrumentId,
                ParseConfigurationMidiTarget(field.Key),
                value));
        });
        _session.RefreshWorkspace(workspace);
    }

    private void OnSubVoiceConfigurationLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: string field }
            || _session.ActiveWorkspace is not InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId,
                ActiveSubVoiceId: MidoraId subVoiceId
            } workspace
            || _session.Project?.EventInstruments.FirstOrDefault(item => item.Id == instrumentId)
                is not EventInstrument instrument
            || instrument.SubVoices.FirstOrDefault(item => item.Id == subVoiceId)
                is not SubVoice subVoice)
        {
            return;
        }
        if (!_session.CanEditProject)
        {
            _session.RefreshWorkspace(workspace);
            return;
        }
        bool succeeded = RunSynchronous("Update SubVoice configuration", () =>
        {
            IProjectEditCommand? command = field switch
            {
                "Name" when !string.Equals(subVoice.Name ?? string.Empty,
                    workspace.ActiveSubVoiceNameText, StringComparison.Ordinal) =>
                    ProjectDomainEditCommands.UpdateSubVoiceName(
                        instrumentId,
                        subVoiceId,
                        workspace.ActiveSubVoiceNameText),
                "RootNote" => CreateRootNoteCommand(),
                _ => null
            };
            if (command is not null) _session.Execute(command);
        });
        if (!succeeded) _session.RefreshWorkspace(workspace);

        IProjectEditCommand? CreateRootNoteCommand()
        {
            string text = workspace.ActiveSubVoiceRootNoteText.Trim();
            int? root = text.Length == 0
                ? null
                : int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            return root == subVoice.RootNoteOverride
                ? null
                : ProjectDomainEditCommands.UpdateSubVoiceRootNote(
                    instrumentId,
                    subVoiceId,
                    root);
        }
    }

    private void OnSubVoiceInitialStateFieldLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: PropertyField property }
            || _session.ActiveWorkspace is not InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId,
                ActiveSubVoiceId: MidoraId subVoiceId
            } workspace)
        {
            return;
        }
        if (!_session.CanEditProject)
        {
            _session.RefreshWorkspace(workspace);
            return;
        }
        bool succeeded = RunSynchronous("Update SubVoice Initial State", () =>
        {
            string text = property.Value.Trim();
            int? value = text.Length == 0
                ? null
                : int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            _session.Execute(ProjectDomainEditCommands.UpdateSubVoiceInitialStateValue(
                instrumentId,
                subVoiceId,
                ParseConfigurationMidiTarget(property.Key),
                value));
        });
        _session.RefreshWorkspace(workspace);
        if (!succeeded && sender is TextBox textBox)
        {
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        }
    }

    private static MidiValueTarget ParseConfigurationMidiTarget(string key)
    {
        if (key == "bankMsb") return MidiValueTarget.BankMsb;
        if (key == "bankLsb") return MidiValueTarget.BankLsb;
        if (key == "program") return MidiValueTarget.Program;
        if (key == "pitchBend") return MidiValueTarget.PitchBend;
        if (key == "pitchRangeSemitones") return MidiValueTarget.PitchBendRangeSemitones;
        if (key == "pitchRangeCents") return MidiValueTarget.PitchBendRangeCents;
        if (TryNumber("cc.", MidiValueKind.ControlChange, out MidiValueTarget target)
            || TryNumber("rpn.", MidiValueKind.RegisteredParameter, out target)
            || TryNumber("nrpn.", MidiValueKind.NonRegisteredParameter, out target))
        {
            return target;
        }
        throw new InvalidOperationException("Unknown Event Instrument Initial State target.");

        bool TryNumber(string prefix, MidiValueKind kind, out MidiValueTarget result)
        {
            result = default;
            if (!key.StartsWith(prefix, StringComparison.Ordinal)
                || !int.TryParse(key.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int number))
            {
                return false;
            }
            result = new(kind, number);
            return true;
        }
    }

    private void OnInstrumentLifecycleSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { Tag: string field } comboBox
            || _session.ActiveWorkspace is not InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId
            } workspace
            || _session.Project?.EventInstruments.FirstOrDefault(item => item.Id == instrumentId)
                is not EventInstrument instrument)
        {
            return;
        }
        ShortNoteLifecycle shortLifecycle = field == "Short" && comboBox.SelectedItem is ShortNoteLifecycle selectedShort
            ? selectedShort
            : instrument.ShortLifecycle;
        LongNoteLifecycle longLifecycle = field == "Long" && comboBox.SelectedItem is LongNoteLifecycle selectedLong
            ? selectedLong
            : instrument.LongLifecycle;
        if (shortLifecycle == instrument.ShortLifecycle && longLifecycle == instrument.LongLifecycle) return;
        if (!_session.CanEditProject)
        {
            comboBox.GetBindingExpression(Selector.SelectedItemProperty)?.UpdateTarget();
            return;
        }
        if (!RunSynchronous("Change Event Instrument Lifecycle", () => _session.Execute(
                ProjectDomainEditCommands.UpdateEventInstrumentLifecycle(
                    instrumentId,
                    shortLifecycle,
                    longLifecycle))))
        {
            _session.RefreshWorkspace(workspace);
            comboBox.GetBindingExpression(Selector.SelectedItemProperty)?.UpdateTarget();
        }
    }

    private void OnInstrumentOverlapSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { Tag: string field } comboBox
            || _session.ActiveWorkspace is not InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId
            } workspace
            || _session.Project?.EventInstruments.FirstOrDefault(item => item.Id == instrumentId)
                is not EventInstrument instrument)
        {
            return;
        }
        OverlapPolicy policy = field == "Policy" && comboBox.SelectedItem is OverlapPolicy selectedPolicy
            ? selectedPolicy
            : instrument.OverlapPolicy;
        OverlapScope scope = field == "Scope" && comboBox.SelectedItem is OverlapScope selectedScope
            ? selectedScope
            : instrument.OverlapScope;
        if (policy == instrument.OverlapPolicy && scope == instrument.OverlapScope) return;
        if (!_session.CanEditProject)
        {
            comboBox.GetBindingExpression(Selector.SelectedItemProperty)?.UpdateTarget();
            return;
        }
        if (!RunSynchronous("Change Event Instrument Overlap", () => _session.Execute(
                ProjectDomainEditCommands.UpdateEventInstrumentOverlap(instrumentId, policy, scope))))
        {
            _session.RefreshWorkspace(workspace);
            comboBox.GetBindingExpression(Selector.SelectedItemProperty)?.UpdateTarget();
        }
    }

    private void OnInstrumentSectionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender) || sender is not TabControl tabs) return;
        RestoreInstrumentConfigurationShortcutFocus(tabs);
    }

    private void OnInstrumentSectionsLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TabControl tabs
            || tabs.Items.OfType<TabItem>().FirstOrDefault(item =>
                string.Equals(item.Header as string, "Configurations", StringComparison.Ordinal))
                is not TabItem configurations)
        {
            return;
        }
        if (tabs.Items.IndexOf(configurations) != 0)
        {
            int requestedIndex = tabs.DataContext is InstrumentWorkspaceViewModel workspace
                ? workspace.ActiveSectionIndex
                : 0;
            tabs.Items.Remove(configurations);
            tabs.Items.Insert(0, configurations);
            tabs.SelectedIndex = Math.Clamp(requestedIndex, 0, tabs.Items.Count - 1);
        }
        RestoreInstrumentConfigurationShortcutFocus(tabs);
    }

    private void RestoreInstrumentConfigurationShortcutFocus(TabControl tabs)
    {
        if (tabs.SelectedItem is not TabItem { Header: "Configurations" }) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (tabs.IsVisible
                && tabs.IsEnabled
                && tabs.SelectedItem is TabItem { Header: "Configurations" })
            {
                tabs.Focus();
            }
        });
    }

    private void OnInstrumentNavigationScrollLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer
            {
                DataContext: InstrumentWorkspaceViewModel workspace
            } scrollViewer)
        {
            scrollViewer.ScrollToVerticalOffset(workspace.LeftPaneVerticalOffset);
        }
    }

    private void OnInstrumentNavigationScrollChanged(
        object sender,
        ScrollChangedEventArgs e)
    {
        if (sender is ScrollViewer
            {
                DataContext: InstrumentWorkspaceViewModel workspace
            } scrollViewer
            && e.VerticalChange != 0)
        {
            workspace.LeftPaneVerticalOffset = scrollViewer.VerticalOffset;
        }
    }

    private void OnInstrumentNavigationPreviewMouseWheel(
        object sender,
        MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer || e.Handled) return;
        double oldOffset = scrollViewer.VerticalOffset;
        scrollViewer.ScrollToVerticalOffset(
            Math.Clamp(
                oldOffset - e.Delta / 3d,
                0,
                scrollViewer.ScrollableHeight));
        e.Handled = scrollViewer.VerticalOffset != oldOffset;
    }

    private void OnInstrumentLoopLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        CommitInstrumentLoop();

    private async void OnInstrumentLoopTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_session.ActiveWorkspace is not InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId
            } workspace)
        {
            return;
        }

        CancellationTokenSource delay = new();
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref _instrumentLoopCommitDelay,
            delay);
        previous?.Cancel();
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(180), delay.Token);
            if (delay.IsCancellationRequested
                || !_session.CanEditProject
                || _session.Project?.EventInstruments.FirstOrDefault(value => value.Id == instrumentId)
                    is not EventInstrument instrument
                || !TryParseLoopDraft(workspace, instrument, out long? start, out long? end))
            {
                return;
            }
            ExecuteInstrumentLoopUpdate(workspace, instrumentId, instrument, start, end);
        }
        catch (OperationCanceledException) when (delay.IsCancellationRequested)
        {
        }
        finally
        {
            _ = Interlocked.CompareExchange(
                ref _instrumentLoopCommitDelay,
                null,
                delay);
            delay.Dispose();
        }
    }

    private void OnInstrumentLoopKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitInstrumentLoop();
        e.Handled = true;
    }

    private void CommitInstrumentLoop()
    {
        CancellationTokenSource? pending = Interlocked.Exchange(
            ref _instrumentLoopCommitDelay,
            null);
        pending?.Cancel();
        if (_session.ActiveWorkspace is not InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId
            } workspace
            || _session.Project?.EventInstruments.FirstOrDefault(value => value.Id == instrumentId)
                is not EventInstrument instrument)
        {
            return;
        }
        if (!_session.CanEditProject)
        {
            _session.RefreshWorkspace(workspace);
            return;
        }
        if (!RunSynchronous("Change Event Instrument Loop", () =>
        {
            long? start = ParseOptionalTick(workspace.LoopStartText, "Loop Start");
            long? end = ParseOptionalTick(workspace.LoopEndText, "Loop End");
            if (instrument.LoopStartTick == start && instrument.LoopEndTick == end)
            {
                return;
            }
            _session.Execute(ProjectDomainEditCommands.UpdateEventInstrumentLoop(
                instrumentId,
                start,
                end));
        }))
        {
            _session.RefreshWorkspace(workspace);
        }
    }

    private void ExecuteInstrumentLoopUpdate(
        InstrumentWorkspaceViewModel workspace,
        MidoraId instrumentId,
        EventInstrument instrument,
        long? start,
        long? end)
    {
        if (instrument.LoopStartTick == start && instrument.LoopEndTick == end) return;
        if (!_session.CanEditProject)
        {
            _session.RefreshWorkspace(workspace);
            return;
        }
        if (!RunSynchronous("Change Event Instrument Loop", () => _session.Execute(
                ProjectDomainEditCommands.UpdateEventInstrumentLoop(instrumentId, start, end))))
        {
            _session.RefreshWorkspace(workspace);
        }
    }

    private static bool TryParseLoopDraft(
        InstrumentWorkspaceViewModel workspace,
        EventInstrument instrument,
        out long? start,
        out long? end)
    {
        start = null;
        end = null;
        string startText = workspace.LoopStartText.Trim();
        string endText = workspace.LoopEndText.Trim();
        if (startText.Length == 0 && endText.Length == 0) return true;
        if (!instrument.RequiresChannelIsolation)
        {
            return false;
        }

        if (startText.Length > 0)
        {
            if (!long.TryParse(startText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedStart)
                || parsedStart < 0
                || parsedStart >= instrument.TemplateLengthTicks)
            {
                return false;
            }
            start = parsedStart;
        }
        if (endText.Length > 0)
        {
            if (!long.TryParse(endText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedEnd)
                || parsedEnd <= 0
                || parsedEnd > instrument.TemplateLengthTicks)
            {
                return false;
            }
            end = parsedEnd;
        }
        if (start.HasValue && end.HasValue && end.Value <= start.Value)
        {
            return false;
        }
        return true;
    }

    private T? FindWorkspaceElement<T>(object tag) where T : FrameworkElement
    {
        return FindDescendant<T>(WorkspaceTabs, element =>
            Equals(element.Tag, tag)
            && ReferenceEquals(element.DataContext, _session.ActiveWorkspace));
    }

    private static T? FindDescendant<T>(DependencyObject root, Predicate<T> predicate)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T candidate && predicate(candidate)) return candidate;
            T? nested = FindDescendant(child, predicate);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static IEnumerable<T> EnumerateDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T candidate) yield return candidate;
            foreach (T nested in EnumerateDescendants<T>(child)) yield return nested;
        }
    }

    private static long? ParseOptionalTick(string value, string label)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0) return null;
        return long.TryParse(
            trimmed,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out long tick)
            ? tick
            : throw new FormatException($"{label} must be blank or a base-10 integer.");
    }

    private void OnAddInstrumentStateClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement
            {
                DataContext: InstrumentWorkspaceViewModel
                {
                    ObjectId: MidoraId instrumentId
                } workspace
            })
        {
            return;
        }
        MidiStateEntryDialog dialog = new() { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Target is not MidiValueTarget target) return;
        RunSynchronous("Add Instrument MIDI State", () =>
        {
            MidoraId? selectedSubVoice = sender switch
            {
                FrameworkElement { Tag: "ActiveSubVoice" } => workspace.ActiveSubVoiceId,
                FrameworkElement { Tag: "Instrument" } => null,
                _ => workspace.Selection.Primary
            };
            if (selectedSubVoice is MidoraId selected
                && _session.Project!.EventInstruments.Single(item => item.Id == instrumentId)
                    .SubVoices.Any(item => item.Id == selected))
            {
                _session.Execute(ProjectDomainEditCommands.UpdateSubVoiceInitialStateValue(
                    instrumentId, selected, target, dialog.Value));
            }
            else
            {
                _session.Execute(ProjectDomainEditCommands.UpdateEventInstrumentInitialStateValue(
                    instrumentId, target, dialog.Value));
            }
        });
    }

    private void OnPreviewNotePressed(object? sender, PianoKeyEventArgs e)
    {
        if (sender is not FrameworkElement
            {
                DataContext: InstrumentWorkspaceViewModel
                {
                    ObjectId: MidoraId instrumentId
                } workspace
            }
            || _session.Project is null)
        {
            return;
        }
        if (workspace.IsPreviewMuted)
        {
            _session.SetStatusMessage("Preview is muted for this Event Instrument Workspace.", isError: true);
            return;
        }
        MidoraId? subVoiceId = workspace.PreviewMode == InstrumentPreviewMode.SelectedSubVoice
            || workspace.IsPreviewSoloSelected
            ? workspace.ActiveSubVoiceId
            : null;
        if ((workspace.PreviewMode == InstrumentPreviewMode.SelectedSubVoice
             || workspace.IsPreviewSoloSelected)
            && subVoiceId is null)
        {
            _session.SetStatusMessage(
                "Create or select a SubVoice before using Selected SubVoice Preview.",
                isError: true);
            return;
        }
        RunSynchronous("Start Held Preview", () => _session.StartHeldEventInstrumentPreview(
            new EventInstrumentPreviewRequest(
                instrumentId,
                subVoiceId,
                e.Note,
                e.Velocity,
                GateLengthTicks: null,
                CursorTick: _session.CurrentTick)));
    }

    private void OnPreviewNoteReleased(object? sender, PianoKeyEventArgs e) =>
        RunSynchronous("End Held Preview", _session.EndHeldPreviewGate);

    private void OnAddLogicalParameterClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement
            {
                DataContext: InstrumentWorkspaceViewModel
                {
                    ObjectId: MidoraId instrumentId
                } workspace
            }
            || _session.Project is null) return;
        EventInstrument instrument = _session.Project.EventInstruments.Single(item => item.Id == instrumentId);
        string name = UniqueName("Parameter", instrument.LogicalParameters.Select(item => item.Name));
        LogicalParameterDefinitionDialog dialog = new(name) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.DefinitionEdit is not { } edit) return;
        RunSynchronous("Create Logical Parameter", () => ExecuteAndSelectCreated(
            ProjectDomainEditCommands.CreateLogicalParameter(
                instrumentId,
                dialog.ParameterName,
                edit.Type,
                edit.Minimum,
                edit.Maximum,
                edit.DisplayMinimum,
                edit.DisplayMaximum,
                edit.DefaultValue,
                edit.UsesExplicitEnumValues,
                enumItems: edit.EnumItems),
            workspace));
    }

    private void OnAddEnumItemClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId,
                Selection: { Primary: MidoraId parameterId }
            }
            || _session.Project is null) return;
        EventInstrument instrument = _session.Project.EventInstruments.Single(item => item.Id == instrumentId);
        LogicalParameterDefinition? parameter = instrument.LogicalParameters.FirstOrDefault(item => item.Id == parameterId);
        if (parameter is null || parameter.Type != LogicalParameterType.Enum)
        {
            ShowUnavailable("Add Enum Item", "Select an Enum Logical Parameter first.");
            return;
        }
        string name = UniqueName("Item", parameter.EnumItems.Select(item => item.Name));
        int? explicitValue = null;
        if (parameter.UsesExplicitEnumValues)
        {
            int start = checked((int)Math.Ceiling(parameter.Minimum));
            int end = checked((int)Math.Floor(parameter.Maximum));
            HashSet<int> used = parameter.EnumItems.Select(item => item.Value).ToHashSet();
            for (int offset = 0; offset <= parameter.EnumItems.Count; offset++)
            {
                long candidate = (long)start + offset;
                if (candidate > end) break;
                if (!used.Contains((int)candidate))
                {
                    explicitValue = (int)candidate;
                    break;
                }
            }
            if (!explicitValue.HasValue)
            {
                ShowUnavailable("Add Enum Item", "The current explicit Enum legal range has no unused integer value.");
                return;
            }
        }
        RunSynchronous("Create Enum Item", () => _session.Execute(
            ProjectDomainEditCommands.CreateLogicalParameterEnumItem(
                instrumentId, parameterId, name, explicitValue)));
    }

    private void OnEditLogicalParameterDefinitionClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId,
                Selection: { Primary: MidoraId parameterId }
            }
            || _session.Project is null)
        {
            ShowUnavailable("Edit Logical Parameter Definition", "Select a Logical Parameter first.");
            return;
        }
        EventInstrument instrument = _session.Project.EventInstruments.Single(item => item.Id == instrumentId);
        LogicalParameterDefinition? parameter = instrument.LogicalParameters.FirstOrDefault(item => item.Id == parameterId);
        if (parameter is null)
        {
            ShowUnavailable("Edit Logical Parameter Definition", "The current selection is not a Logical Parameter.");
            return;
        }
        LogicalParameterLane[] lanes = _session.Project.Tracks
            .SelectMany(track => track.Segments)
            .SelectMany(segment => segment.ParameterLanes)
            .Where(lane => lane.ParameterId == parameterId)
            .ToArray();
        LogicalParameterDefinitionDialog dialog = new(parameter, lanes.Length, lanes.Sum(lane => lane.Points.Count))
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || dialog.DefinitionEdit is null) return;
        RunSynchronous("Migrate Logical Parameter Definition", () => _session.Execute(
            ProjectDomainEditCommands.MigrateLogicalParameterDefinition(
                instrumentId,
                parameterId,
                dialog.DefinitionEdit,
                dialog.MigrationMode,
                dialog.EnumSemanticWarningAcknowledged,
                dialog.ParameterName)));
    }

    private void OnAddMappingFunctionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: InstrumentWorkspaceViewModel { ObjectId: MidoraId instrumentId } }
            || _session.Project is null) return;
        EventInstrument instrument = _session.Project.EventInstruments.Single(item => item.Id == instrumentId);
        string name = UniqueName("Mapping", instrument.MappingFunctions.Select(item => item.Name));
        ShowMappingFunctionDialog(instrumentId, functionId: null, name, "value");
    }

    private void OnMappingFunctionDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox
            {
                DataContext: InstrumentWorkspaceViewModel { ObjectId: MidoraId instrumentId },
                SelectedItem: MappingFunctionListItem function
            })
        {
            e.Handled = true;
            _ = Dispatcher.BeginInvoke(
                () => ShowMappingFunctionDialog(instrumentId, function.Id),
                DispatcherPriority.Normal);
        }
    }

    private void OnEditMappingFunctionClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId,
                Selection.Primary: MidoraId functionId
            }
            || _session.Project?.EventInstruments.FirstOrDefault(value => value.Id == instrumentId)
                ?.MappingFunctions.Any(value => value.Id == functionId) != true)
        {
            ShowUnavailable("Mapping Function Properties", "Select a Mapping Function first.");
            return;
        }
        ShowMappingFunctionDialog(instrumentId, functionId);
        e.Handled = true;
    }

    private void ShowMappingFunctionDialog(
        MidoraId instrumentId,
        MidoraId? functionId,
        string? initialName = null,
        string? initialExpression = null)
    {
        if (_session.Project?.EventInstruments.FirstOrDefault(value => value.Id == instrumentId)
            is not EventInstrument instrument)
        {
            ShowUnavailable("Mapping Function", "The Event Instrument no longer exists.");
            return;
        }

        CSharpMappingFunction? function = functionId is MidoraId id
            ? instrument.MappingFunctions.FirstOrDefault(value => value.Id == id)
            : null;
        if (functionId.HasValue && function is null)
        {
            ShowUnavailable("Mapping Function", "The Mapping Function no longer exists.");
            return;
        }

        HashSet<MidoraId> before = instrument.MappingFunctions.Select(value => value.Id).ToHashSet();
        MappingFunctionDialog dialog = new(
            function is null ? "New Mapping Function" : "Mapping Function Properties",
            function?.Name ?? initialName ?? UniqueName("Mapping", instrument.MappingFunctions.Select(value => value.Name)),
            function?.Body ?? initialExpression ?? "value",
            submission =>
            {
                try
                {
                    if (function is null)
                    {
                        _session.Execute(ProjectDomainEditCommands.CreateMappingFunction(
                            instrumentId,
                            submission.Name,
                            submission.Expression,
                            submission.ReferencedContextFields));
                        CSharpMappingFunction created = instrument.MappingFunctions.Single(value => !before.Contains(value.Id));
                        if (_session.ActiveWorkspace is InstrumentWorkspaceViewModel workspace)
                        {
                            workspace.Selection.Replace(created.Id);
                            _session.RefreshWorkspace(workspace);
                        }
                    }
                    else
                    {
                        _session.Execute(ProjectDomainEditCommands.UpdateMappingFunction(
                            instrumentId,
                            function.Id,
                            submission.Name,
                            submission.Expression,
                            submission.ReferencedContextFields));
                    }
                    return null;
                }
                catch (Exception exception)
                {
                    return exception.Message;
                }
            })
        {
            Owner = this
        };
        _ = ShowModalDialog(dialog);
    }

    private void OnInstrumentStructureSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_synchronizingInstrumentStructureSelection
            || _session.ActiveWorkspace is not InstrumentWorkspaceViewModel workspace
            || sender is not ListBox list
            || list.SelectedItem is null)
        {
            return;
        }
        SelectInstrumentStructureItem(list, workspace, list.SelectedItem);
        if (list.SelectedItem is SubVoiceListItem selected)
        {
            _session.ActivateSubVoiceEditor(workspace, selected.Id);
            SetInstrumentStructureVisualSelection(workspace, selected.Id);
        }
    }

    private void SelectInstrumentStructureItem(
        ListBox list,
        InstrumentWorkspaceViewModel workspace,
        object item)
    {
        if (!TryGetInstrumentStructureItemId(item, out MidoraId selected)) return;
        workspace.SelectedMappingStepId = item is MappingStepListItem ? selected : null;
        _synchronizingInstrumentStructureSelection = true;
        try
        {
            foreach (ListBox candidate in EnumerateDescendants<ListBox>(WorkspaceTabs))
            {
                if (ReferenceEquals(candidate, list)
                    || !ReferenceEquals(candidate.DataContext, workspace)
                    || candidate.SelectedItem is null
                    || !TryGetInstrumentStructureItemId(candidate.SelectedItem, out _))
                {
                    continue;
                }
                candidate.UnselectAll();
            }
        }
        finally
        {
            _synchronizingInstrumentStructureSelection = false;
        }
        _session.SelectWorkspaceObject(workspace, selected);
    }

    private static bool TryGetInstrumentStructureItemId(object? item, out MidoraId id)
    {
        MidoraId? candidate = item switch
        {
            SubVoiceListItem value => value.Id,
            LogicalParameterListItem value => value.Id,
            MappingFunctionListItem value => value.Id,
            ParameterMappingListItem value => value.Id,
            MappingChainListItem value => value.Id,
            MappingStepListItem value => value.Id,
            EnvelopeListItem value => value.Id,
            _ => null
        };
        if (candidate is MidoraId resolved)
        {
            id = resolved;
            return true;
        }
        id = default;
        return false;
    }

    private void ClearInstrumentStructureVisualSelection(InstrumentWorkspaceViewModel workspace)
    {
        workspace.SelectedMappingStepId = null;
        _synchronizingInstrumentStructureSelection = true;
        try
        {
            foreach (ListBox candidate in EnumerateDescendants<ListBox>(WorkspaceTabs))
            {
                if (ReferenceEquals(candidate.DataContext, workspace)
                    && candidate.SelectedItem is not null
                    && TryGetInstrumentStructureItemId(candidate.SelectedItem, out _))
                {
                    candidate.UnselectAll();
                }
            }
        }
        finally
        {
            _synchronizingInstrumentStructureSelection = false;
        }
    }

    private void SetInstrumentStructureVisualSelection(
        InstrumentWorkspaceViewModel workspace,
        MidoraId selectedId)
    {
        workspace.SelectedMappingStepId = workspace.MappingSteps.Any(value => value.Id == selectedId)
            ? selectedId
            : null;
        _synchronizingInstrumentStructureSelection = true;
        try
        {
            bool selectedOne = false;
            foreach (ListBox candidate in EnumerateDescendants<ListBox>(WorkspaceTabs))
            {
                if (!ReferenceEquals(candidate.DataContext, workspace)) continue;
                object? matchingItem = candidate.Items.Cast<object>()
                    .FirstOrDefault(value =>
                        TryGetInstrumentStructureItemId(value, out MidoraId id)
                        && id == selectedId);
                if (!selectedOne && matchingItem is not null)
                {
                    candidate.SelectedItem = matchingItem;
                    selectedOne = true;
                }
                else if (candidate.SelectedItem is not null
                    && TryGetInstrumentStructureItemId(candidate.SelectedItem, out _))
                {
                    candidate.UnselectAll();
                }
            }
        }
        finally
        {
            _synchronizingInstrumentStructureSelection = false;
        }
    }

    private static string UniqueName(string basis, IEnumerable<string> existing)
    {
        HashSet<string> names = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int index = 1; ; index++)
        {
            string candidate = $"{basis} {index}";
            if (!names.Contains(candidate)) return candidate;
        }
    }

    private void OnOpenArrangementClick(object sender, RoutedEventArgs e)
    {
        if (_session.HasProject) _session.OpenArrangement();
    }

    private void OnOpenDiagnosticsClick(object sender, RoutedEventArgs e) => OpenTreeWorkspace(ProjectTreeNodeKind.Diagnostics);

    private void OnDiagnosticSummaryMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        OpenTreeWorkspace(ProjectTreeNodeKind.Diagnostics);
        e.Handled = true;
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        RunAfterMenuClosed(sender, () => OpenTreeWorkspace(ProjectTreeNodeKind.ProjectSettings));
    }

    private void OnAddProjectStateClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string scope }) return;
        MidiStateEntryDialog dialog = new() { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Target is not MidiValueTarget target) return;
        RunSynchronous("Add Project MIDI State", () => _session.Execute(
            scope == "reset"
                ? ProjectDomainEditCommands.UpdateProjectResetDefaultValue(target, dialog.Value)
                : ProjectDomainEditCommands.UpdateProjectInitialStateValue(target, dialog.Value)));
    }

    private void OnToggleProjectPanelClick(object sender, RoutedEventArgs e)
    {
        _preferences = _preferences with
        {
            DesktopUi = _preferences.DesktopUi with { ProjectPanelVisible = ProjectPanelMenuItem.IsChecked }
        };
        ApplyPanelVisibility();
        SaveDesktopPreferences();
    }

    private void OnToggleSnapClick(object sender, RoutedEventArgs e)
    {
        GetActiveEditorSettings().SnapEnabled = SnapMenuItem.IsChecked;
    }

    private void OnToggleFollowPlaybackClick(object sender, RoutedEventArgs e)
    {
        SetFollowPlaybackEnabled(FollowPlaybackMenuItem.IsChecked);
    }

    private void OnFollowPlaybackToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggle)
        {
            SetFollowPlaybackEnabled(toggle.IsChecked == true);
        }
    }

    private void SetFollowPlaybackEnabled(bool enabled)
    {
        _followPlaybackViewportInteractionActive = false;
        _preferences = _preferences with
        {
            DesktopUi = _preferences.DesktopUi with { FollowPlayback = enabled }
        };
        FollowPlaybackMenuItem.IsChecked = enabled;
        FollowPlaybackToggleButton.IsChecked = enabled;
        SaveDesktopPreferences();
        if (enabled)
        {
            FollowActivePlayback(force: true);
        }
    }

    private void OnResetLayoutClick(object sender, RoutedEventArgs e)
    {
        DesktopUiPreferences defaults = DesktopUiPreferences.Default;
        _preferences = _preferences with
        {
            DesktopUi = _preferences.DesktopUi with
            {
                ProjectPanelWidth = defaults.ProjectPanelWidth,
                ProjectPanelVisible = true
            }
        };
        ProjectPanelColumn.Width = new(defaults.ProjectPanelWidth);
        ApplyPanelVisibility();
        SaveDesktopPreferences();
    }

    private void OnResetEditorPreferencesClick(object sender, RoutedEventArgs e)
    {
        foreach (TimelineWorkspaceViewModel workspace in _session.Workspaces.OfType<TimelineWorkspaceViewModel>())
        {
            workspace.ResetViewport();
        }
        if (_session.Project is not null)
        {
            _session.ArrangementEditorSettings.Reset(true, _session.Project.TicksPerQuarterNote);
            _session.PianoRollEditorSettings.Reset(false, _session.Project.TicksPerQuarterNote);
        }
        _preferences = _preferences with
        {
            DesktopUi = _preferences.DesktopUi with
            {
                FollowPlayback = DesktopUiPreferences.Default.FollowPlayback
            }
        };
        ApplyPanelVisibility();
        SaveDesktopPreferences();
    }

    private void OnResetAllUiPreferencesClick(object sender, RoutedEventArgs e)
    {
        if (MessageDialog.Show(
                this,
                "Reset all local UI preferences, including panel layout, timeline Snap, and Follow Playback? Audio preferences are not affected.",
                "Reset All UI Preferences",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        _preferences = _preferences with { DesktopUi = DesktopUiPreferences.Default };
        OnResetLayoutClick(sender, e);
        OnResetEditorPreferencesClick(sender, e);
    }

    private void OpenTreeWorkspace(ProjectTreeNodeKind kind)
    {
        ProjectTreeNode? node = _session.ProjectTree.FirstOrDefault(item => item.Kind == kind);
        if (node is not null) _session.OpenWorkspace(node);
    }

    private void OnCloseWorkspaceClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WorkspaceViewModel workspace })
        {
            _session.CloseWorkspace(workspace);
            e.Handled = true;
        }
    }

    private void OnCloseOtherWorkspacesClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: WorkspaceViewModel keep }) return;
        foreach (WorkspaceViewModel workspace in _session.Workspaces.Where(item => !ReferenceEquals(item, keep)).ToArray())
        {
            _session.CloseWorkspace(workspace);
        }
        _session.ActiveWorkspace = keep;
    }

    private void OnTimelineItemInvoked(object? sender, TimelineItemEventArgs e)
    {
        if (_session.ActiveWorkspace is not WorkspaceViewModel workspace) return;
        workspace.ActiveLane = e.Item.Lane;
        bool replaceDrawSegmentSelection = ShouldReplaceDrawSegmentSelection(
            (sender as TimelineSurface)?.ToolMode,
            e.Item.Kind,
            e.Modifiers,
            e.PreserveSelectionForPotentialCopyDrag);
        if (e.PreserveExistingSelection)
        {
            long selectionRevision = workspace.Selection.Revision;
            workspace.Selection.Add(e.Item.Id);
            if (workspace.Selection.Revision != selectionRevision)
            {
                _session.RefreshWorkspaceSelection(workspace);
            }
        }
        else if (replaceDrawSegmentSelection)
        {
            long selectionRevision = workspace.Selection.Revision;
            workspace.Selection.Replace(e.Item.Id);
            if (workspace.Selection.Revision != selectionRevision)
            {
                _session.RefreshWorkspaceSelection(workspace);
            }
        }
        else if (!e.PreserveSelectionForPotentialCopyDrag)
        {
            long selectionRevision = workspace.Selection.Revision;
            if (e.IsCopyDragStart) workspace.Selection.Add(e.Item.Id);
            else if ((e.Modifiers & ModifierKeys.Control) != 0) workspace.Selection.Toggle(e.Item.Id);
            else if ((e.Modifiers & ModifierKeys.Shift) != 0) workspace.Selection.Add(e.Item.Id);
            else if (workspace.Selection.Ids.Contains(e.Item.Id)) workspace.Selection.Add(e.Item.Id);
            else workspace.Selection.Replace(e.Item.Id);
            if (workspace.Selection.Revision != selectionRevision)
            {
                _session.RefreshWorkspaceSelection(workspace);
            }
        }
        if (sender is TimelineSurface { ToolMode: TimelineToolMode.Draw }
            && e.Item.Kind is TimelineItemKind.LogicalNote
                or TimelineItemKind.DirectMidiNote
                or TimelineItemKind.TemplateNote)
        {
            GetActiveEditorSettings().DefaultLengthTicks = Math.Max(1, e.Item.Length);
        }
        if (sender is TimelineSurface { ToolMode: TimelineToolMode.Erase }
            && _session.CanEditProject)
        {
            DeleteWorkspaceSelection();
            return;
        }
        if (e.IsDoubleClick && e.Item.Kind == TimelineItemKind.Segment)
        {
            _session.OpenSegment(e.Item.Id);
        }
    }

    private void OnOpenWorkspaceSelectionClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Arrangement,
                Selection.Primary: MidoraId segmentId
            }
            && _session.Project?.Tracks.SelectMany(track => track.Segments)
                .Any(segment => segment.Id == segmentId) == true)
        {
            _session.OpenSegment(segmentId);
            return;
        }
        if (_session.ActiveWorkspace is InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId,
                Selection.Primary: MidoraId functionId
            }
            && _session.Project?.EventInstruments.FirstOrDefault(item => item.Id == instrumentId)
                ?.MappingFunctions.Any(function => function.Id == functionId) == true)
        {
            ShowMappingFunctionDialog(instrumentId, functionId);
        }
    }

    private void OnDeleteWorkspaceSelectionClick(object sender, RoutedEventArgs e)
    {
        if (_session.CanEditProject) DeleteWorkspaceSelection();
    }

    private void OnTimelineToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton
            {
                Tag: string value,
                DataContext: TimelineWorkspaceViewModel workspace
            } button
            || !Enum.TryParse(value, out TimelineToolMode mode))
        {
            return;
        }
        workspace.ToolMode = mode;
        button.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
    }

    private void OnInstrumentTimelineToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton
            {
                Tag: string value,
                DataContext: InstrumentWorkspaceViewModel workspace
            } button
            && Enum.TryParse(value, out TimelineToolMode mode))
        {
            workspace.ToolMode = mode;
            button.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        }
    }

    private void OnFollowInstanceVelocityClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox
            {
                IsChecked: bool follows,
                DataContext: InstrumentWorkspaceViewModel
                {
                    ObjectId: MidoraId instrumentId,
                    ActiveSubVoiceId: MidoraId subVoiceId
                } workspace
            } checkBox)
        {
            return;
        }
        if (!_session.CanEditProject)
        {
            checkBox.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
            return;
        }

        if (!RunSynchronous(
                "Change Follow Instance Velocity",
                () => _session.Execute(
                    ProjectDomainEditCommands.SetSubVoiceFollowInstanceVelocity(
                        instrumentId,
                        subVoiceId,
                        follows))))
        {
            _session.RefreshWorkspace(workspace);
            checkBox.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
        }
    }

    private void OnSelectInstrumentClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement
            {
                DataContext: InstrumentWorkspaceViewModel
                {
                    ObjectId: MidoraId instrumentId
                } workspace
            })
        {
            workspace.Selection.Replace(instrumentId);
            _session.RefreshWorkspace(workspace);
        }
    }

    private void OnTimelineSnapClick(object sender, RoutedEventArgs e)
    {
        SnapMenuItem.IsChecked = !GetActiveEditorSettings().SnapEnabled;
        OnToggleSnapClick(SnapMenuItem, e);
    }

    private void OnTimelineZoomClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string direction } source) return;
        double factor = direction == "In" ? 0.8 : 1.25;
        if (source.DataContext is InstrumentWorkspaceViewModel)
        {
            TimelineSurface? surface = FindWorkspaceElement<TimelineSurface>("SubVoiceNotes");
            if (surface is null) return;
            long nextSpan = Math.Clamp(
                checked((long)Math.Round(surface.TickSpan * factor, MidpointRounding.AwayFromZero)),
                16,
                1L << 50);
            long centerTick = surface.StartTick <= long.MaxValue - surface.TickSpan / 2
                ? surface.StartTick + surface.TickSpan / 2
                : long.MaxValue;
            surface.TickSpan = nextSpan;
            surface.StartTick = Math.Max(0, centerTick - nextSpan / 2);
            return;
        }
        if (source.DataContext is not TimelineWorkspaceViewModel workspace) return;
        long span = Math.Clamp(
            checked((long)Math.Round(workspace.TickSpan * factor, MidpointRounding.AwayFromZero)),
            16,
            1L << 50);
        long center = workspace.StartTick <= long.MaxValue - workspace.TickSpan / 2
            ? workspace.StartTick + workspace.TickSpan / 2
            : long.MaxValue;
        workspace.TickSpan = span;
        workspace.StartTick = Math.Max(0, center - span / 2);
    }

    private void OnTimelineVerticalZoomClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string direction } source) return;
        object timelineTag = source.DataContext is InstrumentWorkspaceViewModel
            ? "SubVoiceNotes"
            : "PrimaryTimeline";
        FindWorkspaceElement<TimelineSurface>(timelineTag)
            ?.AdjustVerticalZoom(string.Equals(direction, "In", StringComparison.Ordinal));
    }

    private void OnSubdivisionComboBoxLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not ComboBox { Tag: string role } comboBox) return;
        bool laneOperation = string.Equals(role, "LaneOperation", StringComparison.Ordinal);
        TimelineEditorSettings? settings = comboBox.DataContext switch
        {
            TimelineWorkspaceViewModel timeline => laneOperation
                ? timeline.LaneEditorSettings
                : timeline.EditorSettings,
            InstrumentWorkspaceViewModel instrument => laneOperation
                ? instrument.EventLaneEditorSettings
                : instrument.EditorSettings,
            _ => null
        };
        if (settings is null) return;
        if (TimelineSubdivision.TryParse(comboBox.Text, out TimelineSubdivision parsed))
        {
            TimelineSubdivision selection = settings.SubdivisionPresets.FirstOrDefault(candidate =>
                candidate.IsBar == parsed.IsBar
                && candidate.Numerator == parsed.Numerator
                && candidate.Denominator == parsed.Denominator);
            if (selection == default) selection = parsed;
            if (string.Equals(role, "Display", StringComparison.Ordinal))
            {
                settings.DisplaySubdivision = selection;
            }
            else
            {
                settings.OperationSubdivision = selection;
            }
        }
        comboBox.SelectedItem = string.Equals(role, "Display", StringComparison.Ordinal)
            ? settings.DisplaySubdivision
            : settings.OperationSubdivision;
        comboBox.Text = string.Equals(role, "Display", StringComparison.Ordinal)
            ? settings.DisplaySubdivisionText
            : settings.OperationSubdivisionText;
    }

    private void OnTimelineGridDivisionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string value }
            || _session.Project is null)
        {
            return;
        }
        TimelineSubdivision subdivision;
        if (TimelineSubdivision.TryParse(value, out TimelineSubdivision parsed))
        {
            subdivision = parsed;
        }
        else if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int legacyDivisions)
            && legacyDivisions > 0)
        {
            int denominator = checked(legacyDivisions * 4);
            subdivision = new(1, denominator, $"1/{denominator}");
        }
        else
        {
            return;
        }
        GetActiveEditorSettings().DisplaySubdivision = subdivision;
    }

    private void OnTimelineLaneHeaderCommandInvoked(
        object? sender,
        TimelineLaneHeaderCommandEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || _session.ActiveWorkspace is not TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Arrangement
            } workspace)
        {
            return;
        }
        ArrangementLaneDescriptor? lane = workspace.GetArrangementLane(e.Lane);
        if (lane is null) return;
        if (e.Command == TimelineLaneHeaderCommand.ToggleExpanded) return;
        if (lane.Value.ObjectId is not MidoraId objectId
            || lane.Value.Kind == ArrangementLaneKind.Conductor)
        {
            return;
        }
        RunSynchronous(
            e.Command == TimelineLaneHeaderCommand.ToggleMute ? "Toggle Mute" : "Toggle Solo",
            () =>
            {
                if (e.Command == TimelineLaneHeaderCommand.ToggleMute)
                {
                    _session.SetTrackMuted(objectId, !_session.IsTrackMuted(objectId));
                }
                else
                {
                    _session.SetTrackSolo(objectId, !_session.IsTrackSolo(objectId));
                }
            });
    }

    private void OnTimelineLaneHeaderInvoked(object? sender, TimelineLaneHeaderEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || _session.ActiveWorkspace is not WorkspaceViewModel workspace)
        {
            return;
        }
        if (workspace is TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Arrangement } arrangement
            && arrangement.GetArrangementLane(e.Lane) is ArrangementLaneDescriptor arrangementLane)
        {
            if (e.IsSharedGroupTarget) return;
            _trackHeaderContextLane = e.Lane;
            bool alreadySelected = arrangementLane.Kind == ArrangementLaneKind.Conductor
                ? arrangement.IsConductorTrackSelected
                : arrangementLane.ObjectId is MidoraId trackId
                    && arrangement.SelectedArrangementTrackId == trackId;
            if (alreadySelected)
            {
                ClearArrangementTrackSelection(arrangement);
            }
            else
            {
                SelectArrangementTrack(arrangement, arrangementLane);
            }
        }
        else
        {
            _trackHeaderContextLane = e.Lane;
            workspace.ActiveLane = e.Lane;
        }
        if (sender is TimelineSurface { Tag: "ParameterLanes" }
            && workspace is TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Segment,
                ObjectId: MidoraId segmentId
            } timelineWorkspace
            && TimelineWorkspaceViewModel.FindSegment(project, segmentId) is { } located
            && timelineWorkspace.GetActiveParameterLaneOption()?.LaneId is MidoraId activeLaneId)
        {
            workspace.Selection.Replace(activeLaneId);
            _session.RefreshWorkspace(workspace);
            return;
        }
        if (workspace is InstrumentWorkspaceViewModel instrumentWorkspace
            && instrumentWorkspace.GetRenderLane(e.Lane) is InstrumentRenderLane lane)
        {
            workspace.Selection.Replace(
                lane.ValueCurveId
                ?? lane.EventMappingChainId
                ?? lane.SubVoiceId);
            _session.RefreshWorkspace(workspace);
        }
    }

    private void OnArrangementAddClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: ContextMenu menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
            e.Handled = true;
        }
    }

    private void OnArrangementInstrumentInvoked(
        object? sender,
        TimelineArrangementInstrumentEventArgs e)
    {
        _session.OpenInstrument(e.InstrumentId);
    }

    private void OnArrangementMidiRouteInvoked(
        object? sender,
        TimelineArrangementMidiRouteEventArgs e) =>
        ShowMidiRouteSettings(e.TrackId, e.RootId);

    private void OnOpenInstrumentPropertiesClick(object sender, RoutedEventArgs e)
    {
        if (OpenInstrumentProperties()) e.Handled = true;
    }

    private bool OpenInstrumentProperties()
    {
        if (_session.ActiveWorkspace is not InstrumentWorkspaceViewModel workspace
            || workspace.Selection.Primary is null)
        {
            ShowUnavailable(
                "Properties",
                "Select an Event Instrument object first.");
            return false;
        }
        ObjectPropertiesViewModel properties = _session.CreateObjectProperties(workspace);
        if (!ObjectPropertiesProjection.CanEditInPropertiesDialog(workspace, properties))
        {
            ShowUnavailable(
                "Properties",
                "This object has no available properties.");
            return false;
        }
        ObjectPropertiesDialog dialog = new(_session, workspace) { Owner = this };
        _ = dialog.ShowDialog();
        _session.RefreshWorkspaceSelection(workspace);
        return true;
    }

    private void OnTimelineContextMenuClosed(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu { PlacementTarget: TimelineSurface surface })
        {
            surface.SetArrangementSharedGroupContextHighlight(null);
        }
    }

    private void OnResetAllTrackMonitoringClick(object sender, RoutedEventArgs e) =>
        RunSynchronous("Reset Track Monitoring", _session.ResetAllTrackMonitoringStates);

    private static EventInstrumentBrowserRow? EventInstrumentBrowserRowFrom(object sender) =>
        sender switch
        {
            ListBox { SelectedItem: EventInstrumentBrowserRow row } => row,
            FrameworkElement { DataContext: EventInstrumentBrowserRow row } => row,
            _ => null
        };

    private void OnEventInstrumentBrowserRightButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not ListBox list) return;
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current is not null && current is not ListBoxItem)
        {
            current = VisualTreeHelper.GetParent(current);
        }
        if (current is ListBoxItem item)
        {
            list.SelectedItem = item.DataContext;
        }
        else
        {
            list.SelectedItem = null;
        }
    }

    private void OnEventInstrumentBrowserDoubleClick(object sender, MouseButtonEventArgs e)
    {
        EventInstrumentBrowserRow? row = EventInstrumentBrowserRowFrom(sender);
        if (row is null) return;
        if (sender is ListBox { ContextMenu: { } menu }) menu.IsOpen = false;
        e.Handled = true;
        _session.OpenInstrument(row.Id);
    }

    internal static bool ShouldReplaceDrawSegmentSelection(
        TimelineToolMode? toolMode,
        TimelineItemKind itemKind,
        ModifierKeys modifiers,
        bool preserveSelectionForPotentialDrag) =>
        toolMode == TimelineToolMode.Draw
        && itemKind == TimelineItemKind.Segment
        && modifiers == ModifierKeys.None
        && !preserveSelectionForPotentialDrag;

    private void OnEventInstrumentBrowserEditClick(object sender, RoutedEventArgs e)
    {
        EventInstrumentBrowserRow? row = EventInstrumentBrowserRowFrom(sender);
        if (row is not null) _session.OpenInstrument(row.Id);
    }

    private void OnEventInstrumentBrowserAddTrackClick(object sender, RoutedEventArgs e)
    {
        EventInstrumentBrowserRow? row = EventInstrumentBrowserRowFrom(sender);
        if (row is null) return;
        RunSynchronous("Create Logical Track", () =>
            _session.Execute(ProjectDomainEditCommands.CreateLogicalTrack(
                eventInstrumentId: row.Id)));
    }

    private void OnEventInstrumentBrowserDuplicateClick(object sender, RoutedEventArgs e)
    {
        EventInstrumentBrowserRow? row = EventInstrumentBrowserRowFrom(sender);
        if (row is null) return;
        RunSynchronous("Duplicate Event Instrument", () =>
            _session.Execute(ProjectDomainEditCommands.DuplicateEventInstrumentOnly(row.Id)));
    }

    private void OnEventInstrumentBrowserCopyClick(object sender, RoutedEventArgs e) =>
        CopyEventInstrumentBrowserItem(sender, cut: false);

    private void OnEventInstrumentBrowserCutClick(object sender, RoutedEventArgs e) =>
        CopyEventInstrumentBrowserItem(sender, cut: true);

    private void CopyEventInstrumentBrowserItem(object sender, bool cut)
    {
        EventInstrumentBrowserRow? row = EventInstrumentBrowserRowFrom(sender);
        if (row is null
            || _session.Document is not ProjectDocumentSession document
            || cut && (!_session.CanEditProject || row.UsageCount != 0))
        {
            return;
        }
        RunSynchronous(cut ? "Cut Event Instrument" : "Copy Event Instrument", () =>
        {
            ProjectObjectClipboardPayload payload;
            IProjectEditCommand? delete = null;
            if (cut)
            {
                ProjectObjectClipboardCutPreparation prepared =
                    ProjectObjectClipboard.PrepareCutEventInstrument(document, row.Id);
                payload = prepared.Payload;
                delete = prepared.DeleteAfterSuccessfulClipboardWrite;
            }
            else
            {
                payload = ProjectObjectClipboard.CopyEventInstrument(document, row.Id);
            }
            Clipboard.SetDataObject(payload.PlainTextSummary, copy: true);
            _projectClipboard = payload;
            _clipboardDocument = document;
            if (delete is not null) _session.Execute(delete);
            _session.SetStatusMessage($"{(cut ? "Cut" : "Copied")} {payload.PlainTextSummary}.");
        });
    }

    private void OnEventInstrumentBrowserPasteClick(object sender, RoutedEventArgs e)
    {
        if (!_session.CanEditProject
            || _session.Project is not MidoraProject project
            || _session.Document is not ProjectDocumentSession document
            || _projectClipboard is not ProjectObjectClipboardPayload
            {
                Kind: ProjectObjectClipboardKind.EventInstrument
            } payload
            || !ReferenceEquals(document, _clipboardDocument))
        {
            return;
        }
        EventInstrumentBrowserRow? row = EventInstrumentBrowserRowFrom(sender);
        int insertionIndex = row is null
            ? project.EventInstruments.Count
            : project.EventInstruments.FindIndex(value => value.Id == row.Id) + 1;
        RunSynchronous("Paste Event Instrument", () => _session.Execute(
            ProjectObjectClipboard.CreatePasteEventInstrumentCommand(
                document,
                payload,
                insertionIndex: insertionIndex)));
    }

    private void OnEventInstrumentBrowserMoveUpClick(object sender, RoutedEventArgs e) =>
        MoveEventInstrumentBrowserItem(sender, -1);

    private void OnEventInstrumentBrowserMoveDownClick(object sender, RoutedEventArgs e) =>
        MoveEventInstrumentBrowserItem(sender, 1);

    private void MoveEventInstrumentBrowserItem(object sender, int direction)
    {
        EventInstrumentBrowserRow? row = EventInstrumentBrowserRowFrom(sender);
        if (row is null || _session.Project is not MidoraProject project) return;
        int index = project.EventInstruments.FindIndex(value => value.Id == row.Id);
        int target = index + direction;
        if (target < 0 || target >= project.EventInstruments.Count) return;
        RunSynchronous("Move Event Instrument", () => _session.Execute(
            ProjectDomainEditCommands.ReorderEventInstrument(row.Id, target)));
    }

    private void OnEventInstrumentBrowserRenameClick(object sender, RoutedEventArgs e)
    {
        EventInstrumentBrowserRow? row = EventInstrumentBrowserRowFrom(sender);
        if (row is null) return;
        TextInputDialog dialog = new(
            "Rename Event Instrument",
            "Enter the Event Instrument name.",
            row.Name)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true) return;
        RunSynchronous("Rename Event Instrument", () =>
            _session.Execute(ProjectDomainEditCommands.RenameEventInstrument(row.Id, dialog.Value)));
    }

    private void OnEventInstrumentBrowserDeleteClick(object sender, RoutedEventArgs e)
    {
        EventInstrumentBrowserRow? row = EventInstrumentBrowserRowFrom(sender);
        if (row is null) return;
        if (row.UsageCount != 0)
        {
            MessageDialog.Show(
                this,
                "This Event Instrument is still referenced by one or more usages. Remove or rebind those Logical Tracks first.",
                "Delete Event Instrument",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        if (MessageDialog.Show(
                this,
                $"Delete Event Instrument '{row.Name}'?",
                "Delete Event Instrument",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        RunSynchronous("Delete Event Instrument", () =>
            _session.Execute(ProjectDomainEditCommands.DeleteEventInstrument(row.Id, false)));
    }

    private void OnTimelineLaneHeaderDoubleInvoked(object? sender, TimelineLaneHeaderEventArgs e)
    {
        if (e.IsSharedGroupTarget) return;
        if (_session.ActiveWorkspace is not TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Arrangement
            } workspace
            || workspace.GetArrangementLane(e.Lane) is not ArrangementLaneDescriptor lane)
        {
            return;
        }
        if (lane.Kind == ArrangementLaneKind.Conductor)
        {
            _session.OpenWorkspace(new ProjectTreeNode(ProjectTreeNodeKind.Conductor, "Conductor Track"));
        }
        else if (lane.Kind == ArrangementLaneKind.EventInstrument
                 && lane.ObjectId is MidoraId instrumentId)
        {
            _session.OpenInstrument(instrumentId);
        }
    }

    private async void OnOpenMidiAsNewProjectClick(object sender, RoutedEventArgs e)
    {
        if (!StopPlaybackForProjectCommand("Open MIDI as New Project")
            || !await ConfirmCloseCurrentProjectAsync()) return;
        OpenFileDialog dialog = new()
        {
            Title = "Open MIDI as New Midora Project",
            Filter = "Standard MIDI File (*.mid;*.midi)|*.mid;*.midi|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = ExistingRecentDirectory(RecentDirectoryPurpose.OpenProject)
        };
        if (dialog.ShowDialog(this) != true) return;

        IReadOnlyDictionary<byte, byte>? portMap = null;
        IReadOnlyList<MidiProjectImportDiagnostic>? diagnostics = null;
        Exception? audioInitializationFailure = null;
        while (true)
        {
            MidiImportPortMappingRequiredException? mappingRequired = null;
            using DispatcherCoalescingProgress<MidiProjectImportProgress> progress = new(
                Dispatcher,
                TimeSpan.FromMilliseconds(100),
                value =>
                {
                    DesktopTaskViewModel? active = _session.ActiveForegroundTask;
                    if (active is not null)
                    {
                        _session.ReportTask(
                            active,
                            FormatMidiImportProgress(value),
                            value.Fraction);
                    }
                });
            bool succeeded = await RunOperationAsync(
                "Open MIDI as New Project",
                async cancellationToken =>
                {
                    diagnostics = await _session.ImportMidiAsNewProjectAsync(
                        dialog.FileName,
                        portMap,
                        cancellationToken,
                        progress);
                    _session.ActiveForegroundTask?.SetCancellationAvailable(false);
                    audioInitializationFailure =
                        await InitializeAudioWorkerForActiveProjectAsync(CancellationToken.None);
                },
                canCancel: true,
                handledException: exception =>
                {
                    mappingRequired = exception as MidiImportPortMappingRequiredException;
                    return mappingRequired is null
                        ? null
                        : "MIDI Port mapping review is required before import can continue.";
                });
            if (succeeded) break;
            if (mappingRequired is null) return;
            portMap = ReviewMidiImportPortMapping(mappingRequired.SourcePorts);
            if (portMap is null) return;
        }
        RecordRecentDirectory(RecentDirectoryPurpose.OpenProject, Path.GetDirectoryName(dialog.FileName));
        if (diagnostics is { Count: > 0 })
        {
            int warningCount = diagnostics.Count(value => value.Severity == DiagnosticSeverity.Warning);
            int informationCount = diagnostics.Count(value => value.Severity == DiagnosticSeverity.Info);
            string report = BuildMidiImportReport(diagnostics);
            _session.SetStatusMessage(
                $"MIDI import completed with {warningCount} warning(s) and {informationCount} information notice(s).",
                isError: false,
                details: report,
                detailsTitle: "MIDI Import Report");
            ShowMidiImportReport(report);
        }
        ReportAudioWorkerInitializationFailure(audioInitializationFailure);
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            RestorePlaybackShortcutFocus);
    }

    private static string FormatMidiImportProgress(MidiProjectImportProgress value) =>
        value.Phase switch
        {
            MidiProjectImportPhase.ScanningSource =>
                $"Scanning MIDI source: {value.ProcessedEventCount:N0} event(s) found, "
                + $"{value.ProcessedSourceBytes:N0}/{value.TotalSourceBytes:N0} bytes",
            MidiProjectImportPhase.ImportingEvents =>
                $"Importing MIDI events: {value.ProcessedEventCount:N0}/{value.TotalEventCount:N0}",
            MidiProjectImportPhase.ValidatingProject => "Validating imported Project",
            MidiProjectImportPhase.FinalizingProject => "Finalizing imported Project",
            MidiProjectImportPhase.Completed =>
                $"Imported {value.TotalEventCount:N0} MIDI event(s)",
            _ => "Importing MIDI"
        };

    private static string BuildMidiImportReport(IReadOnlyList<MidiProjectImportDiagnostic> diagnostics)
    {
        int warningCount = diagnostics.Count(value => value.Severity == DiagnosticSeverity.Warning);
        int informationCount = diagnostics.Count(value => value.Severity == DiagnosticSeverity.Info);
        StringBuilder report = new();
        report.AppendLine("The MIDI file was imported successfully after applying compatibility or preservation handling.");
        report.AppendLine();
        report.Append("Warnings: ").AppendLine(warningCount.ToString(CultureInfo.InvariantCulture));
        report.Append("Information: ").AppendLine(informationCount.ToString(CultureInfo.InvariantCulture));

        foreach (MidiProjectImportDiagnostic diagnostic in diagnostics)
        {
            report.AppendLine();
            report.Append('[').Append(diagnostic.Severity).Append("] ").AppendLine(diagnostic.Code);
            report.AppendLine(diagnostic.Message);
            List<string> location = [];
            if (diagnostic.SourceTrackIndex is int trackIndex)
            {
                location.Add($"MTrk {trackIndex}");
            }
            if (diagnostic.SourceByteOffset is int byteOffset)
            {
                location.Add($"byte {byteOffset}");
            }
            if (diagnostic.Tick is long tick)
            {
                location.Add($"tick {tick}");
            }
            if (diagnostic.ZeroBasedPort is byte port)
            {
                location.Add($"Port {port + 1}");
            }
            if (diagnostic.ZeroBasedChannel is byte channel)
            {
                location.Add($"Channel {channel + 1}");
            }
            if (location.Count != 0)
            {
                report.Append("Source: ").AppendLine(string.Join(", ", location));
            }
        }

        return report.ToString().TrimEnd();
    }

    private void ShowMidiImportReport(string report)
    {
        TextDetailsDialog dialog = new("MIDI Import Report", report)
        {
            Owner = this
        };
        _ = dialog.ShowDialog();
    }

    private IReadOnlyDictionary<byte, byte>? ReviewMidiImportPortMapping(
        IReadOnlyList<byte> sourcePorts)
    {
        if (sourcePorts.Count > 16)
        {
            ShowError(
                "MIDI Port Mapping",
                $"The source uses {sourcePorts.Count} distinct MIDI Ports; Midora supports at most 16.");
            return null;
        }
        Dictionary<byte, byte> result = [];
        HashSet<byte> used = [];
        foreach (byte sourcePort in sourcePorts.Order())
        {
            SelectionDialog selection = new(
                "Review MIDI Port Mapping",
                $"Map source Port {sourcePort + 1} to one unused Midora Port (1–16).",
                Enumerable.Range(0, 16)
                    .Select(value => checked((byte)value))
                    .Where(value => !used.Contains(value))
                    .Select(value => new SelectionDialogItem(
                        value,
                        $"Midora Port {value + 1}",
                        value == sourcePort && value < 16 ? "Same Port number" : string.Empty)))
            {
                Owner = this
            };
            if (selection.ShowDialog() != true || selection.SelectedValue is not byte targetPort)
                return null;
            result.Add(sourcePort, targetPort);
            used.Add(targetPort);
        }
        return result;
    }

    private void OnTimelineLaneHeaderContextRequested(object? sender, TimelineLaneHeaderEventArgs e)
    {
        _trackHeaderContextLane = e.Lane;
        if (_session.ActiveWorkspace is WorkspaceViewModel workspace)
        {
            if (workspace is TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Arrangement } arrangement
                && arrangement.GetArrangementLane(e.Lane) is ArrangementLaneDescriptor arrangementLane)
            {
                if (!e.IsSharedGroupTarget)
                {
                    SelectArrangementTrack(arrangement, arrangementLane);
                }
            }
            else
            {
                workspace.ActiveLane = e.Lane;
            }
        }
    }

    private void OnTimelineLaneHeaderReorderCompleted(
        object? sender,
        TimelineLaneHeaderReorderEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || _session.ActiveWorkspace is not TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Arrangement
            } workspace
            || workspace.GetArrangementLane(e.SourceLane) is not ArrangementLaneDescriptor source
            || workspace.GetArrangementLane(e.TargetLane) is not ArrangementLaneDescriptor target
            || source.Kind == ArrangementLaneKind.Conductor)
        {
            return;
        }
        if (source.ObjectId is not MidoraId sourceTrackId
            || source.Kind is not (ArrangementLaneKind.LogicalTrack
                or ArrangementLaneKind.PureMidiTrack))
        {
            return;
        }

        ArrangementTrackReference sourceReference = project.ArrangementTracks
            .Single(value => value.TrackId == sourceTrackId);
        if (e.MovesWholeGroup && source is { IsSharedGroup: true, SharedGroupId: MidoraId groupId })
        {
            ArrangementTrackReference? targetReference = target.ObjectId is MidoraId wholeGroupTargetTrackId
                ? project.ArrangementTracks.FirstOrDefault(value => value.TrackId == wholeGroupTargetTrackId)
                : project.ArrangementTracks.FirstOrDefault(value => value.TrackId != sourceTrackId
                    && (value.Kind != sourceReference.Kind
                        || ResolveDescriptorSharedGroup(workspace, value.TrackId) != groupId));
            if (targetReference is not ArrangementTrackReference targetValue || targetValue == default) return;
            RunSynchronous("Move Shared Track Group", () =>
                _session.Execute(ProjectDomainEditCommands.MoveArrangementSharedGroup(
                    groupId,
                    sourceReference.Kind,
                    targetValue.TrackId,
                    target.Kind == ArrangementLaneKind.Conductor ? false : e.InsertsAfterTarget)));
            return;
        }

        if (e.JoinsTargetGroup && target.ObjectId is MidoraId groupTargetTrackId)
        {
            if (source.Kind == ArrangementLaneKind.LogicalTrack
                && source.ParentId != target.ParentId)
            {
                LogicalTrack movingTrack = project.Tracks.Single(value => value.Id == sourceTrackId);
                EventInstrument targetInstrument = project.EventInstruments.Single(
                    value => value.Id == target.ParentId);
                if (MessageDialog.Show(
                        this,
                        $"Move Logical Track '{TimelineWorkspaceViewModel.TrackDisplayName(project, movingTrack)}' into the shared state group using Event Instrument '{targetInstrument.Name}' and change its binding?",
                        "Change Event Instrument",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    return;
                }
            }
            RunSynchronous("Join Shared Track Group", () =>
                _session.Execute(ProjectDomainEditCommands.MoveArrangementTrackIntoSharedGroup(
                    sourceTrackId,
                    groupTargetTrackId)));
            _logicalTrackShortcutTrackId = source.Kind == ArrangementLaneKind.LogicalTrack
                ? sourceTrackId
                : null;
            return;
        }

        int sourceIndex = project.ArrangementTracks.IndexOf(sourceReference);
        int finalIndex;
        if (target.Kind == ArrangementLaneKind.Conductor || target.ObjectId is not MidoraId targetTrackId)
        {
            finalIndex = 0;
        }
        else
        {
            int targetIndex = project.ArrangementTracks.FindIndex(value => value.TrackId == targetTrackId);
            if (targetIndex < 0) return;
            int targetAfterRemoval = targetIndex - (sourceIndex < targetIndex ? 1 : 0);
            finalIndex = targetAfterRemoval + (e.InsertsAfterTarget ? 1 : 0);
            finalIndex = Math.Clamp(finalIndex, 0, project.ArrangementTracks.Count - 1);
        }

        bool remainsInSameSharedGroup = !e.DetachesFromSourceGroup
            && source.IsSharedGroup
            && target.SharedGroupId == source.SharedGroupId;
        IProjectEditCommand command = remainsInSameSharedGroup
            || (!source.IsSharedGroup && !e.DetachesFromSourceGroup)
            ? ProjectDomainEditCommands.MoveArrangementTrack(sourceTrackId, finalIndex)
            : ProjectDomainEditCommands.MoveArrangementTrackOutsideSharedGroup(sourceTrackId, finalIndex);
        RunSynchronous("Move Arrangement Track", () => _session.Execute(command));
        _logicalTrackShortcutTrackId = source.Kind == ArrangementLaneKind.LogicalTrack
            ? sourceTrackId
            : null;
    }

    private static MidoraId? ResolveDescriptorSharedGroup(
        TimelineWorkspaceViewModel workspace,
        MidoraId trackId) => workspace.Snapshot?.ArrangementLanes
        .FirstOrDefault(value => value.ObjectId == trackId)
        .SharedGroupId;

    private bool TryGetTrackHeaderContext(out LogicalTrack track, out int index)
    {
        track = null!;
        index = -1;
        if (_session.Project is not MidoraProject project
            || !TryGetArrangementHeaderContext(out ArrangementLaneDescriptor lane)
            || lane.Kind != ArrangementLaneKind.LogicalTrack
            || lane.ObjectId is not MidoraId trackId)
        {
            return false;
        }
        track = project.Tracks.SingleOrDefault(value => value.Id == trackId)!;
        index = project.ArrangementTracks.FindIndex(value =>
            value.Kind == ArrangementTrackKind.LogicalTrack
            && value.TrackId == trackId);
        return track is not null && index >= 0;
    }

    private bool TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor)
    {
        descriptor = default;
        if (_session.ActiveWorkspace is not TimelineWorkspaceViewModel
            { Mode: TimelineWorkspaceMode.Arrangement } workspace)
        {
            return false;
        }
        int? lane = _trackHeaderContextLane;
        if (lane is not int laneIndex || workspace.GetArrangementLane(laneIndex) is not { } value)
            return false;
        descriptor = value;
        return true;
    }

    private int ArrangementHeaderSegmentCount(ArrangementLaneDescriptor descriptor)
    {
        if (_session.Project is not MidoraProject project || descriptor.ObjectId is not MidoraId id) return 0;
        return descriptor.Kind switch
        {
            ArrangementLaneKind.LogicalTrack => project.Tracks.FirstOrDefault(value => value.Id == id)?.Segments.Count ?? 0,
            ArrangementLaneKind.PureMidiTrack => project.PureMidiTracks.FirstOrDefault(value => value.Id == id)?.Segments.Count ?? 0,
            _ => 0
        };
    }

    private (bool Up, bool Down) ArrangementHeaderMoveAvailability(ArrangementLaneDescriptor descriptor)
    {
        if (_session.Project is not MidoraProject project || descriptor.ObjectId is not MidoraId id)
            return (false, false);
        ArrangementTrackKind kind = descriptor.Kind switch
        {
            ArrangementLaneKind.LogicalTrack => ArrangementTrackKind.LogicalTrack,
            ArrangementLaneKind.PureMidiTrack => ArrangementTrackKind.PureMidiTrack,
            _ => (ArrangementTrackKind)(-1)
        };
        int index = Enum.IsDefined(kind)
            ? project.ArrangementTracks.FindIndex(value => value.Kind == kind && value.TrackId == id)
            : -1;
        return (index > 0, index >= 0 && index < project.ArrangementTracks.Count - 1);
    }

    private (bool Up, bool Down) ArrangementSharedGroupMoveAvailability(MidoraId groupId)
    {
        if (_session.Project is not MidoraProject project) return (false, false);
        int first = project.ArrangementTracks.FindIndex(value =>
            ArrangementTrackBelongsToSharedGroup(project, value, groupId));
        int last = project.ArrangementTracks.FindLastIndex(value =>
            ArrangementTrackBelongsToSharedGroup(project, value, groupId));
        return (first > 0, last >= 0 && last < project.ArrangementTracks.Count - 1);
    }

    private static bool ArrangementTrackBelongsToSharedGroup(
        MidoraProject project,
        ArrangementTrackReference reference,
        MidoraId groupId) => reference.Kind switch
        {
            ArrangementTrackKind.LogicalTrack => project.Tracks.Single(
                value => value.Id == reference.TrackId).EventInstrumentUsageId == groupId,
            ArrangementTrackKind.PureMidiTrack => project.PureMidiTracks.Single(
                value => value.Id == reference.TrackId).MidiChannelRootId == groupId,
            _ => false
        };

    private bool CanPasteArrangementHeader(ArrangementLaneDescriptor descriptor)
    {
        if (_session.Document is not ProjectDocumentSession document
            || _projectClipboard is not ProjectObjectClipboardPayload payload
            || !ReferenceEquals(document, _clipboardDocument))
        {
            return false;
        }
        return payload.Kind switch
        {
            ProjectObjectClipboardKind.LogicalTrack => descriptor.Kind == ArrangementLaneKind.LogicalTrack,
            ProjectObjectClipboardKind.PureMidiTrack => descriptor.Kind == ArrangementLaneKind.PureMidiTrack,
            _ => false
        };
    }

    private void OnArrangementHeaderOpenClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor)) return;
        if (descriptor.Kind == ArrangementLaneKind.Conductor)
            _session.OpenWorkspace(new ProjectTreeNode(ProjectTreeNodeKind.Conductor, "Conductor Track"));
    }

    private void OnArrangementHeaderEditEventInstrumentClick(object sender, RoutedEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || !TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor)
            || descriptor.Kind != ArrangementLaneKind.LogicalTrack
            || descriptor.ParentId is not MidoraId instrumentId
            || !project.EventInstruments.Any(value => value.Id == instrumentId))
        {
            return;
        }
        RunAfterMenuClosed(sender, () =>
        {
            if (_session.Project?.EventInstruments.Any(value => value.Id == instrumentId) == true)
            {
                _session.OpenInstrument(instrumentId);
            }
        });
    }

    private void OnArrangementHeaderRenameClick(object sender, RoutedEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || !TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor)
            || descriptor.ObjectId is not MidoraId id)
        {
            return;
        }
        string current = descriptor.Kind switch
        {
            ArrangementLaneKind.LogicalTrack => project.Tracks.Single(value => value.Id == id).Name,
            ArrangementLaneKind.PureMidiTrack => project.PureMidiTracks.Single(value => value.Id == id).Name,
            _ => string.Empty
        };
        TextInputDialog dialog = new("Rename Arrangement Object", "Enter the new name.", current) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        RunSynchronous("Rename Arrangement Object", () =>
        {
            IProjectEditCommand command = descriptor.Kind switch
            {
                ArrangementLaneKind.LogicalTrack => ProjectDomainEditCommands.RenameLogicalTrack(id, dialog.Value),
                ArrangementLaneKind.PureMidiTrack => ProjectDomainEditCommands.RenamePureMidiTrack(id, dialog.Value),
                _ => throw new InvalidOperationException("The selected Arrangement row cannot be renamed.")
            };
            _session.Execute(command);
        });
    }

    private void OnArrangementTrackRouteSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || !TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor)
            || descriptor is not
            {
                Kind: ArrangementLaneKind.PureMidiTrack,
                ObjectId: MidoraId trackId,
                ParentId: MidoraId rootId
            })
        {
            return;
        }
        ShowMidiRouteSettings(trackId, rootId);
    }

    private void ShowMidiRouteSettings(MidoraId trackId, MidoraId rootId)
    {
        if (_session.Project is not MidoraProject project
            || project.PureMidiTracks.All(value => value.Id != trackId))
        {
            return;
        }
        MidiChannelRoot root = project.MidiChannelRoots.Single(value => value.Id == rootId);
        MidiChannelRootSettingsDialog dialog = new(root) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        int memberCount = project.PureMidiTracks.Count(
            value => value.MidiChannelRootId == root.Id);
        bool changesSharedFixedChannelMode = memberCount > 1
            && root.RoutingMode == MidiChannelRootRoutingMode.Fixed
            && dialog.RoutingMode == MidiChannelRootRoutingMode.Fixed
            && dialog.OneBasedPort == root.FixedZeroBasedPort + 1
            && dialog.OneBasedChannel == root.FixedZeroBasedChannel + 1
            && dialog.ChannelMode != root.ChannelMode;
        if (changesSharedFixedChannelMode
            && MessageDialog.Show(
                this,
                $"This Fixed MIDI channel is used by {memberCount} Tracks. "
                    + $"Changing its Channel Mode to {dialog.ChannelMode} will affect all of them. Continue?",
                "Change Shared MIDI Channel Mode",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        RunSynchronous("Configure MIDI Route", () => _session.Execute(
            ProjectDomainEditCommands.ConfigurePureMidiTrackRoute(
                trackId, dialog.RoutingMode,
                dialog.OneBasedPort, dialog.OneBasedChannel, dialog.ChannelMode)));
    }

    private void OnArrangementSharedRootSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || _arrangementSharedGroupContextId is not MidoraId rootId
            || project.MidiChannelRoots.SingleOrDefault(value => value.Id == rootId)
                is not MidiChannelRoot root)
        {
            return;
        }
        int members = project.PureMidiTracks.Count(value => value.MidiChannelRootId == root.Id);
        if (members > 1
            && MessageDialog.Show(
                this,
                $"This route is shared by {members} MIDI Tracks. Apply the settings to all members?",
                "Shared MIDI Route",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        MidiChannelRootSettingsDialog dialog = new(root) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        RunSynchronous("Configure Shared MIDI Route", () => _session.Execute(
            ProjectDomainEditCommands.ConfigureMidiChannelRoot(
                root.Id,
                root.Name,
                dialog.RoutingMode,
                dialog.OneBasedPort,
                dialog.OneBasedChannel,
                dialog.ChannelMode)));
    }

    private void OnArrangementSharedGroupMuteClick(object sender, RoutedEventArgs e)
    {
        if (_arrangementSharedGroupContextId is not MidoraId groupId) return;
        RunSynchronous("Toggle Shared Group Mute", () =>
            _session.SetSharedGroupMuted(groupId, !_session.IsSharedGroupMuted(groupId)));
    }

    private void OnArrangementSharedGroupSoloClick(object sender, RoutedEventArgs e)
    {
        if (_arrangementSharedGroupContextId is not MidoraId groupId) return;
        RunSynchronous("Toggle Shared Group Solo", () =>
            _session.SetSharedGroupSolo(groupId, !_session.IsSharedGroupSolo(groupId)));
    }

    private void OnArrangementSharedGroupInstrumentClick(object sender, RoutedEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || _arrangementSharedGroupContextId is not MidoraId usageId
            || project.EventInstrumentUsages.SingleOrDefault(value => value.Id == usageId)
                is not EventInstrumentUsage usage)
        {
            return;
        }
        SelectionDialog dialog = new(
            "Change Shared Event Instrument",
            "Select the Event Instrument Definition used by every Track in this shared state group.",
            project.EventInstruments.Select(instrument => new SelectionDialogItem(
                instrument.Id,
                string.IsNullOrWhiteSpace(instrument.Name)
                    ? "Unnamed Event Instrument"
                    : instrument.Name,
                instrument.Id == usage.EventInstrumentId
                    ? "Current definition"
                    : $"{instrument.SubVoices.Count} SubVoice(s)")))
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true
            || dialog.SelectedValue is not MidoraId eventInstrumentId
            || eventInstrumentId == usage.EventInstrumentId)
        {
            return;
        }
        RunSynchronous("Change Shared Event Instrument", () => _session.Execute(
            ProjectDomainEditCommands.RebindEventInstrumentUsage(usage.Id, eventInstrumentId)));
    }

    private void OnArrangementSharedGroupMoveUpClick(object sender, RoutedEventArgs e) =>
        MoveArrangementSharedGroup(-1);

    private void OnArrangementSharedGroupMoveDownClick(object sender, RoutedEventArgs e) =>
        MoveArrangementSharedGroup(1);

    private void MoveArrangementSharedGroup(int direction)
    {
        if (_session.Project is not MidoraProject project
            || _arrangementSharedGroupContextId is not MidoraId groupId
            || !TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor))
        {
            return;
        }
        ArrangementTrackKind kind = descriptor.Kind switch
        {
            ArrangementLaneKind.LogicalTrack => ArrangementTrackKind.LogicalTrack,
            ArrangementLaneKind.PureMidiTrack => ArrangementTrackKind.PureMidiTrack,
            _ => throw new InvalidOperationException("The selected row has no movable shared group.")
        };
        int first = project.ArrangementTracks.FindIndex(value =>
            value.Kind == kind && ArrangementTrackBelongsToSharedGroup(project, value, groupId));
        int last = project.ArrangementTracks.FindLastIndex(value =>
            value.Kind == kind && ArrangementTrackBelongsToSharedGroup(project, value, groupId));
        int targetIndex = direction < 0 ? first - 1 : last + 1;
        if (first < 0 || (uint)targetIndex >= (uint)project.ArrangementTracks.Count) return;
        ArrangementTrackReference target = project.ArrangementTracks[targetIndex];
        RunSynchronous("Move Shared Track Group", () => _session.Execute(
            ProjectDomainEditCommands.MoveArrangementSharedGroup(
                groupId,
                kind,
                target.TrackId,
                insertAfter: direction > 0)));
    }

    private void OnArrangementSharedGroupMakeIndependentClick(object sender, RoutedEventArgs e)
    {
        if (_arrangementSharedGroupContextId is not MidoraId groupId
            || !TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor))
        {
            return;
        }
        ArrangementTrackKind kind = descriptor.Kind switch
        {
            ArrangementLaneKind.LogicalTrack => ArrangementTrackKind.LogicalTrack,
            ArrangementLaneKind.PureMidiTrack => ArrangementTrackKind.PureMidiTrack,
            _ => throw new InvalidOperationException("The selected row has no shared Track group.")
        };
        RunSynchronous("Make Shared Tracks Independent", () => _session.Execute(
            ProjectDomainEditCommands.MakeArrangementSharedGroupIndependent(groupId, kind)));
    }

    private void OnLogicalTrackShareStateClick(object sender, RoutedEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || !TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor)
            || descriptor is not { Kind: ArrangementLaneKind.LogicalTrack, ObjectId: MidoraId trackId })
        {
            return;
        }
        LogicalTrack source = project.Tracks.Single(value => value.Id == trackId);
        SelectionDialog dialog = new(
            "Share Instrument State",
            "Select the Logical Track whose Event Instrument state should be shared.",
            project.LogicalTracksInArrangementOrder()
                .Where(value => value.Id != source.Id
                    && value.EventInstrumentUsageId.HasValue)
                .Select(value => new SelectionDialogItem(
                    value.Id,
                    TimelineWorkspaceViewModel.TrackDisplayName(project, value),
                    TimelineWorkspaceViewModel.BoundInstrumentDisplayName(project, value))))
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || dialog.SelectedValue is not MidoraId targetTrackId)
            return;
        LogicalTrack target = project.Tracks.Single(value => value.Id == targetTrackId);
        if (project.ResolveEventInstrumentDefinitionId(source)
            != project.ResolveEventInstrumentDefinitionId(target)
            && MessageDialog.Show(
                this,
                "The selected Track uses a different Event Instrument. Change the binding and join its shared state group?",
                "Change Event Instrument",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        RunSynchronous("Share Instrument State", () => _session.Execute(
            ProjectDomainEditCommands.MoveArrangementTrackIntoSharedGroup(
                source.Id,
                target.Id)));
    }

    private void OnLogicalTrackMakeIndependentClick(object sender, RoutedEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || !TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor)
            || descriptor is not { Kind: ArrangementLaneKind.LogicalTrack, ObjectId: MidoraId trackId })
        {
            return;
        }
        int index = project.ArrangementTracks.FindIndex(value => value.TrackId == trackId);
        RunSynchronous("Make Logical Track Independent", () => _session.Execute(
            ProjectDomainEditCommands.MoveArrangementTrackOutsideSharedGroup(trackId, index)));
    }

    private void OnMidiTrackShareChannelClick(object sender, RoutedEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || !TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor)
            || descriptor is not { Kind: ArrangementLaneKind.PureMidiTrack, ObjectId: MidoraId trackId })
        {
            return;
        }
        PureMidiTrack source = project.PureMidiTracks.Single(value => value.Id == trackId);
        SelectionDialog dialog = new(
            "Share MIDI Channel",
            "Select a MIDI Track whose Channel state and route should be shared.",
            project.PureMidiTracksInArrangementOrder()
                .Where(value => value.Id != source.Id)
                .Select(value =>
                {
                    MidiChannelRoot root = project.MidiChannelRoots.Single(
                        root => root.Id == value.MidiChannelRootId);
                    string route = root.RoutingMode == MidiChannelRootRoutingMode.Auto
                        ? $"Auto · {root.ChannelMode}"
                        : $"P{root.FixedZeroBasedPort + 1} Ch{root.FixedZeroBasedChannel + 1} · {root.ChannelMode}";
                    return new SelectionDialogItem(value.Id, value.Name, route);
                }))
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || dialog.SelectedValue is not MidoraId targetTrackId)
            return;
        PureMidiTrack target = project.PureMidiTracks.Single(value => value.Id == targetTrackId);
        MidiChannelRoot targetRoot = project.MidiChannelRoots.Single(
            value => value.Id == target.MidiChannelRootId);
        IProjectEditCommand command = targetRoot.RoutingMode == MidiChannelRootRoutingMode.Auto
            ? ProjectDomainEditCommands.MoveArrangementTrackIntoSharedGroup(source.Id, target.Id)
            : ProjectDomainEditCommands.MovePureMidiTrack(
                source.Id,
                targetRoot.Id,
                project.ArrangementTracks.FindIndex(value => value.TrackId == source.Id));
        RunSynchronous("Share MIDI Channel", () => _session.Execute(command));
    }

    private void OnMidiTrackMakeIndependentClick(object sender, RoutedEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || !TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor)
            || descriptor is not { Kind: ArrangementLaneKind.PureMidiTrack, ObjectId: MidoraId trackId })
        {
            return;
        }
        int index = project.ArrangementTracks.FindIndex(value => value.TrackId == trackId);
        RunSynchronous("Make MIDI Track Independent", () => _session.Execute(
            ProjectDomainEditCommands.MoveArrangementTrackOutsideSharedGroup(trackId, index)));
    }

    private void OnArrangementHeaderCutClick(object sender, RoutedEventArgs e) => CopyArrangementHeader(cut: true);
    private void OnArrangementHeaderCopyClick(object sender, RoutedEventArgs e) => CopyArrangementHeader(cut: false);

    private void CopyArrangementHeader(bool cut)
    {
        if (_session.Document is not ProjectDocumentSession document
            || !TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor)
            || descriptor.ObjectId is not MidoraId id
            || cut && !_session.CanEditProject)
        {
            return;
        }
        RunSynchronous(cut ? "Cut Arrangement Object" : "Copy Arrangement Object", () =>
        {
            ProjectObjectClipboardPayload payload;
            IProjectEditCommand? delete = null;
            if (cut)
            {
                ProjectObjectClipboardCutPreparation prepared = descriptor.Kind switch
                {
                    ArrangementLaneKind.LogicalTrack => ProjectObjectClipboard.PrepareCutLogicalTrack(document, id),
                    ArrangementLaneKind.PureMidiTrack => ProjectObjectClipboard.PrepareCutPureMidiTrack(document, id),
                    _ => throw new InvalidOperationException("The selected Arrangement row cannot be cut.")
                };
                payload = prepared.Payload;
                delete = prepared.DeleteAfterSuccessfulClipboardWrite;
            }
            else
            {
                payload = descriptor.Kind switch
                {
                    ArrangementLaneKind.LogicalTrack => ProjectObjectClipboard.CopyLogicalTrack(document, id),
                    ArrangementLaneKind.PureMidiTrack => ProjectObjectClipboard.CopyPureMidiTrack(document, id),
                    _ => throw new InvalidOperationException("The selected Arrangement row cannot be copied.")
                };
            }
            Clipboard.SetDataObject(payload.PlainTextSummary, copy: true);
            _projectClipboard = payload;
            _clipboardDocument = document;
            if (delete is not null) _session.Execute(delete);
            _session.SetStatusMessage($"{(cut ? "Cut" : "Copied")} {payload.PlainTextSummary}.");
        });
    }

    private void OnArrangementHeaderPasteClick(object sender, RoutedEventArgs e)
    {
        if (!_session.CanEditProject
            || _session.Document is not ProjectDocumentSession document
            || _session.Project is not MidoraProject project
            || _projectClipboard is not ProjectObjectClipboardPayload payload
            || !ReferenceEquals(document, _clipboardDocument)
            || !TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor))
        {
            return;
        }
        RunSynchronous("Paste Arrangement Object", () =>
        {
            IProjectEditCommand command = payload.Kind switch
            {
                ProjectObjectClipboardKind.LogicalTrack => CreateLogicalTrackHeaderPasteCommand(document, payload, project, descriptor),
                ProjectObjectClipboardKind.PureMidiTrack => CreatePureMidiTrackHeaderPasteCommand(document, payload, project, descriptor),
                _ => throw new InvalidOperationException("The clipboard object cannot be pasted at this Arrangement row.")
            };
            _session.Execute(command);
            _session.SetStatusMessage($"Pasted {payload.PlainTextSummary}.");
        });
    }

    private static IProjectEditCommand CreateLogicalTrackHeaderPasteCommand(
        ProjectDocumentSession document,
        ProjectObjectClipboardPayload payload,
        MidoraProject project,
        ArrangementLaneDescriptor descriptor)
    {
        LogicalTrack target = project.Tracks.Single(
            value => value.Id == descriptor.ObjectId!.Value);
        int index = ArrangementInsertionAfterTargetGroup(project, target.Id);
        return target.EventInstrumentUsageId is MidoraId usageId
            ? ProjectObjectClipboard.CreatePasteLogicalTrackIntoUsageCommand(
                document,
                payload,
                usageId,
                index)
            : ProjectObjectClipboard.CreatePasteLogicalTrackIndependentCommand(
                document,
                payload,
                index);
    }

    private static IProjectEditCommand CreatePureMidiTrackHeaderPasteCommand(
        ProjectDocumentSession document,
        ProjectObjectClipboardPayload payload,
        MidoraProject project,
        ArrangementLaneDescriptor descriptor)
    {
        PureMidiTrack target = project.PureMidiTracks.Single(
            value => value.Id == descriptor.ObjectId!.Value);
        MidiChannelRoot root = project.MidiChannelRoots.Single(
            value => value.Id == target.MidiChannelRootId);
        int index = root.RoutingMode == MidiChannelRootRoutingMode.Auto
            ? ArrangementInsertionAfterTargetGroup(project, target.Id)
            : project.ArrangementTracks.FindIndex(value => value.TrackId == target.Id) + 1;
        return ProjectObjectClipboard.CreatePastePureMidiTrackCommand(
            document,
            payload,
            root.Id,
            index);
    }

    private static int ArrangementInsertionAfterTargetGroup(
        MidoraProject project,
        MidoraId targetTrackId)
    {
        ArrangementTrackReference target = project.ArrangementTracks.Single(
            value => value.TrackId == targetTrackId);
        MidoraId? groupId = target.Kind switch
        {
            ArrangementTrackKind.LogicalTrack => project.Tracks.Single(
                value => value.Id == targetTrackId).EventInstrumentUsageId,
            ArrangementTrackKind.PureMidiTrack => project.PureMidiTracks.Single(
                value => value.Id == targetTrackId).MidiChannelRootId,
            _ => null
        };
        if (groupId is null)
        {
            return project.ArrangementTracks.IndexOf(target) + 1;
        }
        int last = project.ArrangementTracks.FindLastIndex(reference =>
            reference.Kind == target.Kind
            && (reference.Kind == ArrangementTrackKind.LogicalTrack
                ? project.Tracks.Single(track => track.Id == reference.TrackId)
                    .EventInstrumentUsageId == groupId
                : project.PureMidiTracks.Single(track => track.Id == reference.TrackId)
                    .MidiChannelRootId == groupId));
        return last + 1;
    }

    private void OnArrangementHeaderDuplicateClick(object sender, RoutedEventArgs e) =>
        DuplicateArrangementHeader(shareInstrumentState: false);

    private void OnArrangementHeaderDuplicateAndShareStateClick(object sender, RoutedEventArgs e) =>
        DuplicateArrangementHeader(shareInstrumentState: true);

    private void DuplicateArrangementHeader(bool shareInstrumentState)
    {
        if (!TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor)
            || descriptor.ObjectId is not MidoraId id) return;
        RunSynchronous("Duplicate Arrangement Object", () =>
        {
            IProjectEditCommand command = descriptor.Kind switch
            {
                ArrangementLaneKind.LogicalTrack when shareInstrumentState =>
                    ProjectDomainEditCommands.DuplicateLogicalTrackAndShareState(id),
                ArrangementLaneKind.LogicalTrack =>
                    ProjectDomainEditCommands.DuplicateLogicalTrack(id),
                ArrangementLaneKind.PureMidiTrack when !shareInstrumentState =>
                    ProjectDomainEditCommands.DuplicatePureMidiTrack(id),
                _ => throw new InvalidOperationException(
                    "The selected Arrangement row cannot be duplicated with the requested state sharing.")
            };
            _session.Execute(command);
        });
    }

    private void OnArrangementHeaderMoveUpClick(object sender, RoutedEventArgs e) => MoveArrangementHeader(-1);
    private void OnArrangementHeaderMoveDownClick(object sender, RoutedEventArgs e) => MoveArrangementHeader(1);

    private void MoveArrangementHeader(int direction)
    {
        if (_session.Project is not MidoraProject project
            || !TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor)
            || descriptor.ObjectId is not MidoraId id) return;
        int index = project.ArrangementTracks.FindIndex(value => value.TrackId == id);
        int targetIndex = index + direction;
        if (index < 0 || targetIndex < 0 || targetIndex >= project.ArrangementTracks.Count) return;
        ArrangementTrackReference target = project.ArrangementTracks[targetIndex];
        MidoraId? sourceGroup = descriptor.SharedGroupId;
        MidoraId? targetGroup = _session.ActiveWorkspace is TimelineWorkspaceViewModel workspace
            ? ResolveDescriptorSharedGroup(workspace, target.TrackId)
            : null;
        IProjectEditCommand command = descriptor.IsSharedGroup && sourceGroup != targetGroup
            ? ProjectDomainEditCommands.MoveArrangementTrackOutsideSharedGroup(id, targetIndex)
            : ProjectDomainEditCommands.MoveArrangementTrack(id, targetIndex);
        RunSynchronous("Move Arrangement Object", () => _session.Execute(command));
    }

    private void OnArrangementHeaderDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || !TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor)
            || descriptor.ObjectId is not MidoraId id) return;
        string message = descriptor.Kind switch
        {
            ArrangementLaneKind.LogicalTrack => $"Delete Logical Track '{project.Tracks.Single(value => value.Id == id).Name}' and all of its Segments?",
            ArrangementLaneKind.PureMidiTrack => $"Delete MIDI Track '{project.PureMidiTracks.Single(value => value.Id == id).Name}' and all of its Segments?",
            ArrangementLaneKind.DamagedEventInstrument => "Delete this damaged Event Instrument placeholder and its retained Logical Track subtree?",
            ArrangementLaneKind.DamagedMidiChannelRoot => "Delete this damaged MIDI Channel Root placeholder and its retained MIDI Track subtree?",
            ArrangementLaneKind.DamagedLogicalTrack => "Delete this damaged Logical Track placeholder?",
            ArrangementLaneKind.DamagedPureMidiTrack => "Delete this damaged Pure MIDI Track placeholder?",
            _ => string.Empty
        };
        if (message.Length == 0 || MessageDialog.Show(this, message, "Delete Arrangement Object",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        RunSynchronous("Delete Arrangement Object", () => _session.Execute(descriptor.Kind switch
        {
            ArrangementLaneKind.LogicalTrack => ProjectDomainEditCommands.DeleteLogicalTrack(id, true),
            ArrangementLaneKind.PureMidiTrack => ProjectDomainEditCommands.DeletePureMidiTrack(id, true),
            ArrangementLaneKind.DamagedEventInstrument => ProjectDomainEditCommands.DeleteDamagedEventInstrument(id),
            ArrangementLaneKind.DamagedMidiChannelRoot => ProjectDomainEditCommands.DeleteDamagedMidiChannelRoot(id),
            ArrangementLaneKind.DamagedLogicalTrack => ProjectDomainEditCommands.DeleteDamagedLogicalTrack(id),
            ArrangementLaneKind.DamagedPureMidiTrack => ProjectDomainEditCommands.DeleteDamagedPureMidiTrack(id),
            _ => throw new InvalidOperationException("The selected Arrangement row cannot be deleted.")
        }));
    }

    private void OnArrangementHeaderNewMidiTrackClick(object sender, RoutedEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || !TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor)) return;
        if (descriptor is not
            {
                Kind: ArrangementLaneKind.PureMidiTrack,
                ObjectId: MidoraId trackId,
                ParentId: MidoraId rootId
            }) return;
        MidiChannelRoot root = project.MidiChannelRoots.Single(value => value.Id == rootId);
        int index = root.RoutingMode == MidiChannelRootRoutingMode.Auto
            ? ArrangementInsertionAfterTargetGroup(project, trackId)
            : project.ArrangementTracks.FindIndex(value => value.TrackId == trackId) + 1;
        RunSynchronous("Create MIDI Track", () => _session.Execute(
            ProjectDomainEditCommands.CreatePureMidiTrack(rootId, insertionIndex: index)));
    }

    private void OnTrackHeaderRenameClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetTrackHeaderContext(out LogicalTrack track, out _)) return;
        TextInputDialog dialog = new(
            "Rename Logical Track",
            "Enter the Logical Track name.",
            track.Name)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true) return;
        RunSynchronous("Rename Logical Track", () =>
            _session.Execute(ProjectDomainEditCommands.RenameLogicalTrack(track.Id, dialog.Value)));
    }

    private void OnTrackHeaderBindClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetTrackHeaderContext(out LogicalTrack track, out _)
            || _session.Project is not MidoraProject project)
        {
            return;
        }
        SelectionDialog dialog = new(
            "Bind Logical Track",
            "Select the Event Instrument used by this Logical Track.",
            project.EventInstruments.Select(instrument => new SelectionDialogItem(
                instrument.Id,
                string.IsNullOrWhiteSpace(instrument.Name) ? "Unnamed Event Instrument" : instrument.Name,
                $"{instrument.SubVoices.Count} SubVoice(s)")))
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true && dialog.SelectedValue is MidoraId instrumentId)
        {
            BindTrackToInstrument(track, instrumentId);
        }
    }

    private void OnTrackHeaderUnbindClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetTrackHeaderContext(out LogicalTrack track, out _)) return;
        RunSynchronous("Unbind Logical Track", () =>
            _session.Execute(ProjectDomainEditCommands.BindLogicalTrack(track.Id, null)));
    }

    private void OnTrackHeaderCutClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetTrackHeaderContext(out LogicalTrack track, out _)) return;
        CutOrCopyLogicalTrack(track, cut: true);
    }

    private void OnTrackHeaderCopyClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetTrackHeaderContext(out LogicalTrack track, out _)) return;
        CutOrCopyLogicalTrack(track, cut: false);
    }

    private void OnTrackHeaderPasteClick(object sender, RoutedEventArgs e) =>
        PasteLogicalTrackClipboard(ResolveLogicalTrackPasteIndex(preferTrackHeaderContext: true));

    private void OnTrackHeaderDuplicateClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetTrackHeaderContext(out LogicalTrack track, out _)) return;
        RunSynchronous("Duplicate Logical Track", () => _session.Execute(
            ProjectDomainEditCommands.DuplicateLogicalTrack(track.Id)));
    }

    private void CutOrCopyLogicalTrack(LogicalTrack track, bool cut)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (_session.Document is not ProjectDocumentSession document
            || cut && !_session.CanEditProject)
        {
            return;
        }
        RunSynchronous(cut ? "Cut Logical Track" : "Copy Logical Track", () =>
        {
            ProjectObjectClipboardPayload payload;
            IProjectEditCommand? delete = null;
            if (cut)
            {
                ProjectObjectClipboardCutPreparation preparation =
                    ProjectObjectClipboard.PrepareCutLogicalTrack(document, track.Id);
                payload = preparation.Payload;
                delete = preparation.DeleteAfterSuccessfulClipboardWrite;
            }
            else
            {
                payload = ProjectObjectClipboard.CopyLogicalTrack(document, track.Id);
            }
            Clipboard.SetDataObject(payload.PlainTextSummary, copy: true);
            _projectClipboard = payload;
            _clipboardDocument = document;
            if (delete is not null) _session.Execute(delete);
            _session.SetStatusMessage($"{(cut ? "Cut" : "Copied")} {payload.PlainTextSummary}.");
        });
    }

    private void OnTrackHeaderSelectSegmentsClick(object sender, RoutedEventArgs e) =>
        SelectTrackHeaderSegments(WorkspaceSelectionRangeMode.Replace);

    private void OnTrackHeaderAddSegmentsToSelectionClick(object sender, RoutedEventArgs e) =>
        SelectTrackHeaderSegments(WorkspaceSelectionRangeMode.Add);

    private void SelectTrackHeaderSegments(WorkspaceSelectionRangeMode mode)
    {
        if (_session.Project is not MidoraProject project
            || !TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor)
            || descriptor.ObjectId is not MidoraId trackId
            || _session.ActiveWorkspace is not TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Arrangement
            } workspace)
        {
            return;
        }
        IEnumerable<MidoraId> segmentIds = descriptor.Kind switch
        {
            ArrangementLaneKind.LogicalTrack => project.Tracks.Single(value => value.Id == trackId)
                .Segments.Select(segment => segment.Id),
            ArrangementLaneKind.PureMidiTrack => project.PureMidiTracks.Single(value => value.Id == trackId)
                .Segments.Select(segment => segment.Id),
            _ => []
        };
        workspace.Selection.ApplyRange(segmentIds, mode);
        _session.RefreshWorkspaceSelection(workspace);
    }

    private void OnTrackHeaderMoveUpClick(object sender, RoutedEventArgs e) => MoveTrackHeader(-1);
    private void OnTrackHeaderMoveDownClick(object sender, RoutedEventArgs e) => MoveTrackHeader(1);

    private void MoveTrackHeader(int direction)
    {
        if (!TryGetTrackHeaderContext(out LogicalTrack track, out int index)
            || _session.Project is not MidoraProject project)
        {
            return;
        }
        int target = index + direction;
        if ((uint)target >= (uint)project.Tracks.Count) return;
        RunSynchronous("Reorder Logical Track", () =>
            _session.Execute(ProjectDomainEditCommands.ReorderLogicalTrack(track.Id, target)));
    }

    private void OnTrackHeaderDeleteClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetTrackHeaderContext(out LogicalTrack track, out _)) return;
        if (MessageDialog.Show(
                this,
                $"Delete Logical Track '{TimelineWorkspaceViewModel.TrackDisplayName(_session.Project!, track)}' and its {track.Segments.Count} Segment(s)?",
                "Delete Logical Track",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        RunSynchronous("Delete Logical Track", () =>
            _session.Execute(ProjectDomainEditCommands.DeleteLogicalTrack(track.Id, nonEmptyDeletionConfirmed: true)));
    }

    private void OnTimelineLanePreviewPressed(object? sender, TimelineLanePreviewEventArgs e)
    {
        RunSynchronous(
            "Start Pitch Audition",
            () => _session.BeginPitchAudition(e.Pitch, e.Velocity));
    }

    private void OnActiveEditorLaneDropDownClosed(object sender, EventArgs e)
    {
        ComboBox? comboBox = sender as ComboBox;
        if (_session.ActiveWorkspace is TimelineWorkspaceViewModel directTimeline
            && directTimeline.GetActiveParameterLaneOption()?.IsDirectMidiLane == true)
        {
            _session.RefreshWorkspace(directTimeline);
            RestoreEditorLaneTimelineFocus(comboBox, directTimeline);
            return;
        }
        if (_session.ActiveWorkspace is TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Segment,
                ObjectId: MidoraId segmentId
            } timeline
            && timeline.GetActiveParameterLaneOption() is ParameterLaneOption option
            && ShouldCreateLogicalParameterLane(option))
        {
            RunSynchronous("Create Logical Parameter Lane", () => ExecuteAndSelectCreated(
                ProjectDomainEditCommands.CreateLogicalParameterLane(segmentId, option.ParameterId),
                timeline));
            RestoreEditorLaneTimelineFocus(comboBox, timeline);
            return;
        }
        if (_session.ActiveWorkspace is WorkspaceViewModel workspace)
        {
            _session.RefreshWorkspace(workspace);
            RestoreEditorLaneTimelineFocus(comboBox, workspace);
        }
    }

    internal static bool ShouldCreateLogicalParameterLane(ParameterLaneOption option) =>
        !option.IsDirectMidiLane
        && option.LaneId is null
        && !option.IsBroken;

    private void RestoreEditorLaneTimelineFocus(
        ComboBox? comboBox,
        WorkspaceViewModel workspace)
    {
        object? timelineTag = workspace switch
        {
            InstrumentWorkspaceViewModel => "SubVoiceEvents",
            TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Segment } => "ParameterLanes",
            _ => null
        };
        if (comboBox is null || timelineTag is null) return;

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (!IsActive
                    || !ReferenceEquals(_session.ActiveWorkspace, workspace)
                    || !comboBox.IsKeyboardFocusWithin)
                {
                    return;
                }

                TimelineSurface? timeline = FindWorkspaceElement<TimelineSurface>(timelineTag);
                if (timeline is { IsVisible: true, IsEnabled: true, Focusable: true })
                {
                    timeline.Focus();
                }
            }));
    }

    private void OnTimelineVelocityEditCompleted(object? sender, TimelineVelocityEditEventArgs e)
    {
        if (e.Velocities.Count == 0) return;
        if (_session.ActiveWorkspace is TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Segment,
                ObjectId: MidoraId segmentId
            })
        {
            RunSynchronous("Paint Note Velocities", () => _session.Execute(
                _session.Project is not null
                && TimelineWorkspaceViewModel.FindMidiSegment(_session.Project, segmentId) is not null
                    ? ProjectDomainEditCommands.PaintDirectMidiNoteVelocities(segmentId, e.Velocities)
                    : ProjectDomainEditCommands.PaintLogicalNoteVelocities(segmentId, e.Velocities)));
            return;
        }
        if (_session.ActiveWorkspace is InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId,
                ActiveSubVoiceId: MidoraId subVoiceId
            })
        {
            RunSynchronous("Paint Template Note Velocities", () =>
                _session.Execute(ProjectDomainEditCommands.PaintTemplateNoteVelocities(
                    instrumentId,
                    subVoiceId,
                    e.Velocities)));
        }
    }

    private void OnTimelineLanePreviewReleased(object? sender, TimelineLanePreviewEventArgs e) =>
        RunSynchronous("End Pitch Audition", _session.EndPitchAudition);

    private void OnTimelinePitchPreviewRequested(object? sender, TimelinePitchPreviewEventArgs e) =>
        RunSynchronous(
            "Update Pitch Audition",
            () => _session.BeginPitchAudition(e.Pitch, e.Velocity));

    private void OnTimelinePitchPreviewReleased(object? sender, EventArgs e) =>
        RunSynchronous("End Pitch Audition", _session.EndPitchAudition);

    private void OnTimelineNotePlacementStarted(object? sender, TimelineNotePlacementEventArgs e)
    {
        if (sender is TimelineSurface { Tag: "SubVoiceNotes", DataContext: InstrumentWorkspaceViewModel instrumentWorkspace }
            && instrumentWorkspace.ObjectId is MidoraId instrumentId
            && instrumentWorkspace.ActiveSubVoiceId is MidoraId subVoiceId)
        {
            long templateStart = instrumentWorkspace.EditorSettings.SnapAbsolute(e.StartTick);
            _templateNotePlacement = (instrumentId, subVoiceId, templateStart, e.Pitch, e.Velocity);
            RunSynchronous(
                "Start Note Placement Audition",
                () => _session.BeginPitchAudition(e.Pitch, e.Velocity));
            return;
        }
        if (_session.Project is not MidoraProject project
            || _session.ActiveWorkspace is not TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Segment,
                ObjectId: MidoraId segmentId
            } workspace)
        {
            return;
        }
        if (TimelineWorkspaceViewModel.FindSegment(project, segmentId) is null
            && TimelineWorkspaceViewModel.FindMidiSegment(project, segmentId) is null) return;
        long start = workspace.EditorSettings.SnapAbsolute(e.StartTick);
        _notePlacement = (segmentId, start, e.Pitch, e.Velocity);
        RunSynchronous(
            "Start Note Placement Audition",
            () => _session.BeginPitchAudition(e.Pitch, e.Velocity));
    }

    private void OnTimelineNotePlacementCompleted(object? sender, TimelineNotePlacementEventArgs e)
    {
        RunSynchronous("End Note Placement Audition", _session.EndPitchAudition);
        if (_templateNotePlacement is { } templatePlacement
            && _session.ActiveWorkspace is InstrumentWorkspaceViewModel instrumentWorkspace)
        {
            _templateNotePlacement = null;
            long rawTemplateLength = Math.Max(1, checked(e.EndTick - templatePlacement.StartTick));
            long templateLength = Math.Max(
                1,
                instrumentWorkspace.EditorSettings.SnapDelta(rawTemplateLength, e.EndTick));
            RunSynchronous("Place Template Note", () => ExecuteAndSelectCreated(
                ProjectDomainEditCommands.CreateTemplateNote(
                    templatePlacement.InstrumentId,
                    templatePlacement.SubVoiceId,
                    templatePlacement.StartTick,
                    templateLength,
                    e.Pitch,
                    templatePlacement.Velocity),
                instrumentWorkspace));
            return;
        }
        if (_notePlacement is not { } placement
            || _session.ActiveWorkspace is not TimelineWorkspaceViewModel workspace)
        {
            return;
        }
        _notePlacement = null;
        long rawLength = Math.Max(1, checked(e.EndTick - placement.StartTick));
        long length = Math.Max(1, workspace.EditorSettings.SnapDelta(rawLength, e.EndTick));
        RunSynchronous("Place Note", () => ExecuteAndSelectCreated(
            _session.Project is not null
            && TimelineWorkspaceViewModel.FindMidiSegment(_session.Project, placement.SegmentId) is not null
                ? ProjectDomainEditCommands.CreateDirectMidiNote(
                    placement.SegmentId,
                    placement.StartTick,
                    length,
                    e.Pitch,
                    placement.Velocity)
                : ProjectDomainEditCommands.CreateLogicalNote(
                    placement.SegmentId,
                    placement.StartTick,
                    length,
                    e.Pitch,
                    placement.Velocity),
            workspace));
    }

    private void OnTimelineNotePlacementCancelled(object? sender, EventArgs e)
    {
        RunSynchronous("End Note Placement Audition", _session.EndPitchAudition);
        _notePlacement = null;
        _templateNotePlacement = null;
    }

    private void OnTimelineSegmentPlacementCompleted(
        object? sender,
        TimelineSegmentPlacementEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || _session.ActiveWorkspace is not TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Arrangement
            } workspace
            || workspace.GetArrangementLane(e.Lane) is not
            { CanContainSegments: true, ObjectId: MidoraId trackId } lane)
        {
            return;
        }

        long end = e.EndTick;
        long nextStart;
        bool occupied;
        if (lane.Kind == ArrangementLaneKind.LogicalTrack)
        {
            LogicalTrack track = project.Tracks.Single(value => value.Id == trackId);
            nextStart = track.Segments.Where(item => item.ProjectStartTick > e.StartTick)
                .Select(item => item.ProjectStartTick).DefaultIfEmpty(long.MaxValue).Min();
            occupied = track.Segments.Any(item => e.StartTick >= item.ProjectStartTick && e.StartTick < item.ProjectRange.EndTick);
        }
        else
        {
            PureMidiTrack track = project.PureMidiTracks.Single(value => value.Id == trackId);
            nextStart = track.Segments.Where(item => item.ProjectStartTick > e.StartTick)
                .Select(item => item.ProjectStartTick).DefaultIfEmpty(long.MaxValue).Min();
            occupied = track.Segments.Any(item => e.StartTick >= item.ProjectStartTick && e.StartTick < item.ProjectRange.EndTick);
        }
        if (occupied)
        {
            return;
        }
        if (nextStart != long.MaxValue)
        {
            end = Math.Min(end, nextStart);
        }
        if (end <= e.StartTick) return;

        RunSynchronous("Create Segment", () => ExecuteAndSelectCreated(
            lane.Kind == ArrangementLaneKind.LogicalTrack
                ? ProjectDomainEditCommands.CreateSegment(
                    trackId,
                    e.StartTick,
                    checked(end - e.StartTick))
                : ProjectDomainEditCommands.CreateMidiSegment(
                    trackId,
                    e.StartTick,
                    checked(end - e.StartTick)),
            workspace));
    }

    private void OnConductorEventSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: ConductorEventRow row }
            || _session.ActiveWorkspace is not TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Conductor
            } workspace)
        {
            return;
        }
        if (workspace.Selection.Primary == row.Id) return;
        workspace.Selection.Replace(row.Id);
        workspace.EditCursorTick = row.Tick;
        if (row.Tick < workspace.StartTick || row.Tick >= workspace.StartTick + workspace.TickSpan)
        {
            workspace.StartTick = Math.Max(0, row.Tick - workspace.TickSpan / 4);
        }
        _session.RefreshWorkspaceSelection(workspace);
    }

    private void OnProjectSettingLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox textBox) CommitProjectSetting(textBox, restoreOnFailure: true);
    }

    private void OnProjectSettingKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox) return;
        if (e.Key == Key.Enter)
        {
            CommitProjectSetting(textBox, restoreOnFailure: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            textBox.ClearValue(Control.BorderBrushProperty);
            textBox.ToolTip = null;
            e.Handled = true;
        }
    }

    private void OnProjectSettingChoiceDropDownClosed(object sender, EventArgs e)
    {
        if (sender is not ComboBox { Tag: PropertyField field }
            || !field.IsEditable
            || !IsCurrentProjectSettingField(field))
        {
            return;
        }

        try
        {
            _session.ApplyProjectSettingsField(field);
        }
        catch (Exception exception)
        {
            if (_session.Workspaces.OfType<SettingsWorkspaceViewModel>().FirstOrDefault()
                is SettingsWorkspaceViewModel settings)
            {
                _session.RefreshWorkspace(settings);
            }
            _session.SetStatusMessage(exception.Message, isError: true);
        }
    }

    private void CommitProjectSetting(TextBox textBox, bool restoreOnFailure)
    {
        if (textBox.IsReadOnly
            || textBox.Tag is not PropertyField field
            || !IsCurrentProjectSettingField(field))
        {
            return;
        }
        string originalValue = field.Value;
        if (string.Equals(textBox.Text, originalValue, StringComparison.Ordinal)) return;
        BindingExpression? binding = textBox.GetBindingExpression(TextBox.TextProperty);
        binding?.UpdateSource();
        try
        {
            _session.ApplyProjectSettingsField(field);
            textBox.ClearValue(Control.BorderBrushProperty);
            textBox.ToolTip = null;
        }
        catch (Exception exception)
        {
            if (restoreOnFailure)
            {
                field.Value = originalValue;
                binding?.UpdateTarget();
                textBox.ClearValue(Control.BorderBrushProperty);
                textBox.ToolTip = exception.Message;
                _session.SetStatusMessage(exception.Message, isError: true);
            }
            else
            {
                textBox.BorderBrush = (Brush)FindResource("Brush.Red");
                textBox.ToolTip = exception.Message;
                textBox.Focus();
                textBox.SelectAll();
            }
        }
    }

    private bool IsCurrentProjectSettingField(PropertyField field)
    {
        if (_session.Workspaces.OfType<SettingsWorkspaceViewModel>().FirstOrDefault() is not SettingsWorkspaceViewModel settings)
        {
            return false;
        }
        return settings.GeneralFields.Contains(field)
            || settings.InitialStateFields.Contains(field)
            || settings.ResetDefaultFields.Contains(field);
    }

    private void OnTimelineSelectionReplacementStarted(
        object? sender,
        TimelineSelectionReplacementEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not WorkspaceViewModel workspace)
            return;
        if (_session.Project is null || !_session.Workspaces.Contains(workspace)) return;
        e.BaseSelection = _session.BeginWorkspaceSelectionReplacement(workspace);
    }

    private void OnTimelineMarqueeCompleted(object? sender, TimelineMarqueeEventArgs e)
    {
        // A large out-of-core marquee finishes asynchronously.  Resolve its
        // owning workspace from the originating surface rather than whichever
        // tab happens to be active when the background scan completes.
        if ((sender as FrameworkElement)?.DataContext is not WorkspaceViewModel workspace)
            return;
        if (_session.Project is null || !_session.Workspaces.Contains(workspace)) return;
        _session.TryApplyMaterializedWorkspaceSelection(
            workspace,
            e.Materialization);
    }

    private void OnTimelineRulerClicked(object? sender, TimelineRulerEventArgs e)
    {
        if (_session.Project is not MidoraProject project)
        {
            return;
        }
        TimelineWorkspaceViewModel? timeline = (sender as FrameworkElement)?.DataContext
            as TimelineWorkspaceViewModel;
        RunSynchronous("Set Playback Cursor", () => _session.SetPlaybackCursor(
            timeline?.ToProjectTick(project, e.Tick) ?? e.Tick));
    }

    private void OnTimelineTimeRangeSelected(object? sender, TimelineTimeRangeEventArgs e)
    {
        if (_session.ActiveWorkspace is not TimelineWorkspaceViewModel workspace) return;
        workspace.SetTimeRange(e.StartTick, e.EndTick);
        if (_session.IsLoopEnabled)
        {
            RunSynchronous("Update Loop Range", () => _session.SetLoopRange(
                workspace.ToProjectRange(_session.Project!, e.StartTick, e.EndTick)));
        }
    }

    private void OnTimelineSegmentSplitRequested(object? sender, TimelineItemEventArgs e)
    {
        if (_session.ActiveWorkspace is not TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Arrangement
            } workspace
            || e.Item.Kind != TimelineItemKind.Segment)
        {
            return;
        }
        long splitTick = workspace.EditorSettings.SnapAbsolute(e.Tick);
        bool isMidiSegment = _session.Project!.PureMidiTracks.Any(track =>
            track.Segments.Any(segment => segment.Id == e.Item.Id));
        RunSynchronous("Split Segment", () => ExecuteAndSelectCreated(
            isMidiSegment
                ? ProjectDomainEditCommands.SplitMidiSegment(e.Item.Id, splitTick)
                : ProjectDomainEditCommands.SplitSegment(e.Item.Id, splitTick),
            workspace));
    }

    private void OnDeselectAllTimelineObjectsClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not WorkspaceViewModel workspace) return;
        workspace.Selection.Clear();
        _session.RefreshWorkspaceSelection(workspace);
    }

    private async void OnInvertTimelineSelectionClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not WorkspaceViewModel workspace
            || GetTimelineContextSurface(sender) is not TimelineSurface surface
            || surface.Snapshot is not TimelineRenderSnapshot snapshot)
        {
            return;
        }
        await MaterializeTimelineSelectionAsync(
            workspace,
            surface,
            snapshot,
            invert: true,
            lane: null);
    }

    private TimelineSelectionOperationContext? ResolveTimelineSelectionOperationContext(
        TimelineSurface surface)
    {
        if (_session.Project is not MidoraProject project
            || _session.ActiveWorkspace is not WorkspaceViewModel workspace)
        {
            return null;
        }
        IReadOnlySet<MidoraId> selected = workspace.Selection.IdSet;
        switch (workspace)
        {
            case TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Arrangement }:
                {
                    MidoraId[] logical = project.Tracks
                        .SelectMany(static track => track.Segments)
                        .Where(segment => selected.Contains(segment.Id))
                        .Select(static segment => segment.Id)
                        .ToArray();
                    MidoraId[] midi = project.PureMidiTracks
                        .SelectMany(static track => track.Segments)
                        .Where(segment => selected.Contains(segment.Id))
                        .Select(static segment => segment.Id)
                        .ToArray();
                    return new(
                        logical.Length != 0 && midi.Length != 0
                            ? TimelineSelectionObjectKind.MixedSegments
                            : midi.Length == 0
                                ? TimelineSelectionObjectKind.Segments
                                : TimelineSelectionObjectKind.MidiSegments,
                        logical.Concat(midi).ToArray());
                }
            case TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Segment,
                ObjectId: MidoraId segmentId
            } timeline:
                {
                    (LogicalTrack Track, Segment Segment)? location =
                        TimelineWorkspaceViewModel.FindSegment(project, segmentId);
                    if (location is not null)
                    {
                        if (string.Equals(surface.Tag as string, "ParameterLanes", StringComparison.Ordinal))
                        {
                            if (timeline.GetActiveParameterLaneOption()?.LaneId is not MidoraId laneId
                                || location.Value.Segment.ParameterLanes.FirstOrDefault(
                                    lane => lane.Id == laneId) is not LogicalParameterLane lane)
                            {
                                return null;
                            }
                            return new(
                                TimelineSelectionObjectKind.LogicalParameterPoints,
                                lane.Points.ResolveByIdsInCollectionOrder(selected)
                                    .Select(static point => point.Id)
                                    .ToArray(),
                                segmentId,
                                laneId,
                                PointMinimum: timeline.ActiveValueMinimum,
                                PointMaximum: timeline.ActiveValueMaximum);
                        }
                        return new(
                            TimelineSelectionObjectKind.LogicalNotes,
                            location.Value.Segment.Notes
                                .ResolveByIdsInCollectionOrder(selected)
                                .Select(static note => note.Id)
                                .ToArray(),
                            segmentId);
                    }
                    if (TimelineWorkspaceViewModel.FindMidiSegment(project, segmentId) is not { } midi)
                        return null;
                    if (string.Equals(surface.Tag as string, "ParameterLanes", StringComparison.Ordinal))
                    {
                        if (timeline.GetActiveParameterLaneOption()?.DirectMidiTarget
                            is not DirectMidiEventLaneTarget target)
                        {
                            return null;
                        }
                        return new(
                            TimelineSelectionObjectKind.DirectMidiEventPoints,
                            midi.Segment.ChannelEvents
                                .ResolveByIds(selected)
                                .Select(static match => match.Value)
                                .Where(value => TimelineWorkspaceViewModel.ToDirectMidiLaneTarget(value) == target)
                                .Select(static value => value.Id)
                                .ToArray(),
                            segmentId,
                            DirectMidiTarget: target,
                            PointMinimum: 0,
                            PointMaximum: target.Kind == DirectMidiChannelEventKind.PitchBend ? 16383 : 127);
                    }
                    return new(
                        TimelineSelectionObjectKind.DirectMidiNotes,
                        midi.Segment.Notes
                            .ResolveByIds(selected)
                            .Select(static match => match.Value.Id)
                            .ToArray(),
                        segmentId);
                }
            case InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId,
                ActiveSubVoiceId: MidoraId subVoiceId
            } instrumentWorkspace:
                {
                    EventInstrument? instrument = project.EventInstruments.FirstOrDefault(
                        value => value.Id == instrumentId);
                    SubVoice? voice = instrument?.SubVoices.FirstOrDefault(value => value.Id == subVoiceId);
                    if (voice is null) return null;
                    if (string.Equals(surface.Tag as string, "SubVoiceNotes", StringComparison.Ordinal))
                    {
                        return new(
                            TimelineSelectionObjectKind.TemplateNotes,
                            voice.Events
                                .ResolveByIdsInCollectionOrder(selected)
                                .Where(static value => value.Kind == TemplateEventKind.Note)
                                .Select(static value => value.Id)
                                .ToArray(),
                            instrumentId,
                            subVoiceId);
                    }
                    if (string.Equals(surface.Tag as string, "SubVoiceEvents", StringComparison.Ordinal)
                        && instrumentWorkspace.GetRenderLane(
                            instrumentWorkspace.ActiveRenderLaneIndex)?.Target is MidiValueTarget target)
                    {
                        return new(
                            TimelineSelectionObjectKind.SubVoiceEventPoints,
                            voice.Events
                                .ResolveByIdsInCollectionOrder(selected)
                                .Where(value => value.Kind != TemplateEventKind.Note
                                    && TemplateEventMidiTargets.Enumerate(value).Contains(target))
                                .Select(static value => value.Id)
                                .ToArray(),
                            instrumentId,
                            subVoiceId,
                            target,
                            PointMinimum: instrumentWorkspace.ActiveValueMinimum,
                            PointMaximum: instrumentWorkspace.ActiveValueMaximum);
                    }
                    return null;
                }
            default:
                return null;
        }
    }

    private void OnFlipSegmentsExposedContentHorizontalClick(object sender, RoutedEventArgs e) =>
        ExecuteHorizontalFlip(SegmentSelectionTransformScope.ExposedContentOnly);

    private void OnFlipSegmentsAndContentHorizontalClick(object sender, RoutedEventArgs e) =>
        ExecuteHorizontalFlip(SegmentSelectionTransformScope.ExposedContentAndSegments);

    private void OnFlipSelectionHorizontalClick(object sender, RoutedEventArgs e) =>
        ExecuteHorizontalFlip(SegmentSelectionTransformScope.ExposedContentOnly);

    private void ExecuteHorizontalFlip(SegmentSelectionTransformScope segmentScope)
    {
        if (_timelineSelectionOperationContext is not { Ids.Length: > 0 } context) return;
        RunSynchronous("Flip Selection Horizontally", () => ExecuteSelectionOperation(context.Kind switch
        {
            TimelineSelectionObjectKind.Segments => ProjectDomainEditCommands.FlipSegmentsHorizontal(
                context.Ids,
                segmentScope),
            TimelineSelectionObjectKind.MidiSegments => ProjectDomainEditCommands.FlipMidiSegmentsHorizontal(
                context.Ids,
                segmentScope),
            TimelineSelectionObjectKind.MixedSegments =>
                ProjectDomainEditCommands.FlipArrangementSegmentsHorizontal(
                    context.Ids,
                    segmentScope),
            TimelineSelectionObjectKind.LogicalNotes => ProjectDomainEditCommands.FlipLogicalNotesHorizontal(
                context.OwnerId!.Value,
                context.Ids),
            TimelineSelectionObjectKind.LogicalParameterPoints =>
                ProjectDomainEditCommands.FlipLogicalParameterPointsHorizontal(
                    context.OwnerId!.Value,
                    context.SecondaryId!.Value,
                    context.Ids),
            TimelineSelectionObjectKind.DirectMidiNotes =>
                ProjectDomainEditCommands.FlipDirectMidiNotesHorizontal(
                    context.OwnerId!.Value,
                    context.Ids),
            TimelineSelectionObjectKind.DirectMidiEventPoints =>
                ProjectDomainEditCommands.FlipDirectMidiEventPointsHorizontal(
                    context.OwnerId!.Value,
                    context.Ids),
            TimelineSelectionObjectKind.TemplateNotes => ProjectDomainEditCommands.FlipTemplateNotesHorizontal(
                context.OwnerId!.Value,
                context.SecondaryId!.Value,
                context.Ids),
            TimelineSelectionObjectKind.SubVoiceEventPoints =>
                ProjectDomainEditCommands.FlipSubVoiceEventPointsHorizontal(
                    context.OwnerId!.Value,
                    context.SecondaryId!.Value,
                    context.Ids,
                    context.MidiTarget!.Value),
            _ => throw new ArgumentOutOfRangeException()
        }));
    }

    private void OnFlipSelectionVerticalClick(object sender, RoutedEventArgs e)
    {
        if (_timelineSelectionOperationContext is not { Ids.Length: > 0 } context) return;
        RunSynchronous("Flip Selection Vertically", () => ExecuteSelectionOperation(context.Kind switch
        {
            TimelineSelectionObjectKind.Segments =>
                ProjectDomainEditCommands.FlipSegmentsVertical(context.Ids),
            TimelineSelectionObjectKind.MidiSegments =>
                ProjectDomainEditCommands.FlipMidiSegmentsVertical(context.Ids),
            TimelineSelectionObjectKind.MixedSegments =>
                ProjectDomainEditCommands.FlipArrangementSegmentsVertical(context.Ids),
            TimelineSelectionObjectKind.LogicalNotes => ProjectDomainEditCommands.FlipLogicalNotesVertical(
                context.OwnerId!.Value,
                context.Ids),
            TimelineSelectionObjectKind.DirectMidiNotes => ProjectDomainEditCommands.FlipDirectMidiNotesVertical(
                context.OwnerId!.Value,
                context.Ids),
            TimelineSelectionObjectKind.TemplateNotes => ProjectDomainEditCommands.FlipTemplateNotesVertical(
                context.OwnerId!.Value,
                context.SecondaryId!.Value,
                context.Ids),
            _ => throw new InvalidOperationException(
                "Vertical flip is unavailable for the current selection type.")
        }));
    }

    private void OnScaleSelectionClick(object sender, RoutedEventArgs e)
    {
        if (_timelineSelectionOperationContext is not { Ids.Length: > 0 } context
            || _session.Project is not MidoraProject project)
        {
            return;
        }
        long currentLength = GetTimelineSelectionSpan(project, context);
        if (currentLength <= 0)
        {
            ShowUnavailable(
                "Scale Selection",
                "The selection must span more than one Tick before it can be scaled.");
            return;
        }
        ScaleSelectionDialog dialog = new(
            currentLength,
            context.Kind is TimelineSelectionObjectKind.Segments
                or TimelineSelectionObjectKind.MidiSegments
                or TimelineSelectionObjectKind.MixedSegments)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true) return;
        RunSynchronous("Scale Selection", () => ExecuteSelectionOperation(context.Kind switch
        {
            TimelineSelectionObjectKind.Segments => ProjectDomainEditCommands.ScaleSegments(
                context.Ids,
                dialog.ScaleFactor,
                dialog.Scope),
            TimelineSelectionObjectKind.MidiSegments => ProjectDomainEditCommands.ScaleMidiSegments(
                context.Ids,
                dialog.ScaleFactor,
                dialog.Scope),
            TimelineSelectionObjectKind.MixedSegments =>
                ProjectDomainEditCommands.ScaleArrangementSegments(
                    context.Ids,
                    dialog.ScaleFactor,
                    dialog.Scope),
            TimelineSelectionObjectKind.LogicalNotes => ProjectDomainEditCommands.ScaleLogicalNotes(
                context.OwnerId!.Value,
                context.Ids,
                dialog.ScaleFactor),
            TimelineSelectionObjectKind.LogicalParameterPoints =>
                ProjectDomainEditCommands.ScaleLogicalParameterPoints(
                    context.OwnerId!.Value,
                    context.SecondaryId!.Value,
                    context.Ids,
                    dialog.ScaleFactor),
            TimelineSelectionObjectKind.DirectMidiNotes =>
                ProjectDomainEditCommands.ScaleDirectMidiNotes(
                    context.OwnerId!.Value,
                    context.Ids,
                    dialog.ScaleFactor),
            TimelineSelectionObjectKind.DirectMidiEventPoints =>
                ProjectDomainEditCommands.ScaleDirectMidiEventPoints(
                    context.OwnerId!.Value,
                    context.Ids,
                    dialog.ScaleFactor),
            TimelineSelectionObjectKind.TemplateNotes => ProjectDomainEditCommands.ScaleTemplateNotes(
                context.OwnerId!.Value,
                context.SecondaryId!.Value,
                context.Ids,
                dialog.ScaleFactor),
            TimelineSelectionObjectKind.SubVoiceEventPoints =>
                ProjectDomainEditCommands.ScaleSubVoiceEventPoints(
                    context.OwnerId!.Value,
                    context.SecondaryId!.Value,
                    context.Ids,
                    context.MidiTarget!.Value,
                    dialog.ScaleFactor),
            _ => throw new ArgumentOutOfRangeException()
        }));
    }

    private void OnTransposeSelectionClick(object sender, RoutedEventArgs e)
    {
        if (_timelineSelectionOperationContext is not { Ids.Length: > 0 } context) return;
        TransposeSelectionDialog dialog = new() { Owner = this };
        if (dialog.ShowDialog() != true) return;
        RunSynchronous("Transpose Selection", () => ExecuteSelectionOperation(context.Kind switch
        {
            TimelineSelectionObjectKind.Segments =>
                ProjectDomainEditCommands.TransposeSegments(context.Ids, dialog.Semitones),
            TimelineSelectionObjectKind.MidiSegments =>
                ProjectDomainEditCommands.TransposeMidiSegments(context.Ids, dialog.Semitones),
            TimelineSelectionObjectKind.MixedSegments =>
                ProjectDomainEditCommands.TransposeArrangementSegments(
                    context.Ids,
                    dialog.Semitones),
            TimelineSelectionObjectKind.LogicalNotes => ProjectDomainEditCommands.TransposeLogicalNotes(
                context.OwnerId!.Value,
                context.Ids,
                dialog.Semitones),
            TimelineSelectionObjectKind.DirectMidiNotes => ProjectDomainEditCommands.TransposeDirectMidiNotes(
                context.OwnerId!.Value,
                context.Ids,
                dialog.Semitones),
            TimelineSelectionObjectKind.TemplateNotes => ProjectDomainEditCommands.TransposeTemplateNotes(
                context.OwnerId!.Value,
                context.SecondaryId!.Value,
                context.Ids,
                dialog.Semitones),
            _ => throw new InvalidOperationException(
                "Transpose is unavailable for the current selection type.")
        }));
    }

    private void OnBatchEditSelectionClick(object sender, RoutedEventArgs e)
    {
        if (_timelineSelectionOperationContext is not { Ids.Length: > 0 } context) return;
        bool pointContext = context.Kind is TimelineSelectionObjectKind.LogicalParameterPoints
            or TimelineSelectionObjectKind.DirectMidiEventPoints
            or TimelineSelectionObjectKind.SubVoiceEventPoints;
        BatchEditDialog dialog = new(
            pointContext ? BatchEditPresetKind.Event : BatchEditPresetKind.Note,
            context.PointMinimum,
            context.PointMaximum)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || dialog.Program is not BatchEditExpressionProgram program)
        {
            return;
        }
        using (program)
        {
            RunSynchronous("Batch Edit Selection", () => ExecuteSelectionOperation(context.Kind switch
            {
                TimelineSelectionObjectKind.Segments =>
                    ProjectDomainEditCommands.BatchEditSegmentExposedNotes(context.Ids, program),
                TimelineSelectionObjectKind.MidiSegments =>
                    ProjectDomainEditCommands.BatchEditMidiSegmentExposedNotes(context.Ids, program),
                TimelineSelectionObjectKind.MixedSegments =>
                    ProjectDomainEditCommands.BatchEditArrangementSegmentExposedNotes(
                        context.Ids,
                        program),
                TimelineSelectionObjectKind.LogicalNotes => ProjectDomainEditCommands.BatchEditLogicalNotes(
                    context.OwnerId!.Value,
                    context.Ids,
                    program),
                TimelineSelectionObjectKind.LogicalParameterPoints =>
                    ProjectDomainEditCommands.BatchEditLogicalParameterPoints(
                        context.OwnerId!.Value,
                        context.SecondaryId!.Value,
                        context.Ids,
                        program),
                TimelineSelectionObjectKind.DirectMidiNotes =>
                    ProjectDomainEditCommands.BatchEditDirectMidiNotes(
                        context.OwnerId!.Value,
                        context.Ids,
                        program),
                TimelineSelectionObjectKind.DirectMidiEventPoints =>
                    ProjectDomainEditCommands.BatchEditDirectMidiEventPoints(
                        context.OwnerId!.Value,
                        context.Ids,
                        program),
                TimelineSelectionObjectKind.TemplateNotes => ProjectDomainEditCommands.BatchEditTemplateNotes(
                    context.OwnerId!.Value,
                    context.SecondaryId!.Value,
                    context.Ids,
                    program),
                TimelineSelectionObjectKind.SubVoiceEventPoints =>
                    ProjectDomainEditCommands.BatchEditSubVoiceEventPoints(
                        context.OwnerId!.Value,
                        context.SecondaryId!.Value,
                        context.Ids,
                        context.MidiTarget!.Value,
                        program),
                _ => throw new ArgumentOutOfRangeException()
            }));
        }
    }

    private void ExecuteSelectionOperation(IProjectEditCommand command)
    {
        WorkspaceViewModel workspace = _session.ActiveWorkspace
            ?? throw new InvalidOperationException(
                "A selection operation requires an active Workspace.");
        _session.ExecutePreservingWorkspaceSelection(command, workspace);
    }

    private static long GetTimelineSelectionSpan(
        MidoraProject project,
        TimelineSelectionOperationContext context)
    {
        HashSet<MidoraId> ids = context.Ids.ToHashSet();
        return context.Kind switch
        {
            TimelineSelectionObjectKind.Segments => RangeSpan(project.Tracks
                .SelectMany(static track => track.Segments)
                .Where(segment => ids.Contains(segment.Id))
                .Select(static segment => (
                    Start: segment.ProjectStartTick,
                    End: segment.ProjectRange.EndTick))),
            TimelineSelectionObjectKind.MidiSegments => RangeSpan(project.PureMidiTracks
                .SelectMany(static track => track.Segments)
                .Where(segment => ids.Contains(segment.Id))
                .Select(static segment => (
                    Start: segment.ProjectStartTick,
                    End: segment.ProjectRange.EndTick))),
            TimelineSelectionObjectKind.MixedSegments => RangeSpan(project.Tracks
                .SelectMany(static track => track.Segments)
                .Where(segment => ids.Contains(segment.Id))
                .Select(static segment => (
                    Start: segment.ProjectStartTick,
                    End: segment.ProjectRange.EndTick))
                .Concat(project.PureMidiTracks
                    .SelectMany(static track => track.Segments)
                    .Where(segment => ids.Contains(segment.Id))
                    .Select(static segment => (
                        Start: segment.ProjectStartTick,
                        End: segment.ProjectRange.EndTick)))),
            TimelineSelectionObjectKind.LogicalNotes => RangeSpan(
                TimelineWorkspaceViewModel.FindSegment(project, context.OwnerId)!.Value.Segment.Notes
                    .ResolveByIdsInCollectionOrder(ids)
                    .Select(static note => (
                        Start: note.StartTick,
                        End: checked(note.StartTick + note.LengthTicks)))),
            TimelineSelectionObjectKind.DirectMidiNotes => RangeSpan(
                TimelineWorkspaceViewModel.FindMidiSegment(project, context.OwnerId)!.Value.Segment.Notes
                    .ResolveByIds(ids)
                    .Select(static match => (
                        Start: match.Value.StartTick,
                        End: checked(match.Value.StartTick + match.Value.LengthTicks)))),
            TimelineSelectionObjectKind.TemplateNotes => RangeSpan(project.EventInstruments
                .Single(value => value.Id == context.OwnerId)
                .SubVoices.Single(value => value.Id == context.SecondaryId)
                .Events.ResolveByIdsInCollectionOrder(ids)
                .Where(static value => value.Kind == TemplateEventKind.Note)
                .Select(static value => (
                    Start: value.Tick,
                    End: checked(value.Tick + value.LengthTicks)))),
            TimelineSelectionObjectKind.LogicalParameterPoints => PointSpan(
                TimelineWorkspaceViewModel.FindSegment(project, context.OwnerId)!.Value.Segment
                    .ParameterLanes.Single(value => value.Id == context.SecondaryId)
                    .Points.ResolveByIdsInCollectionOrder(ids)
                    .Select(static value => value.Tick)),
            TimelineSelectionObjectKind.DirectMidiEventPoints => PointSpan(
                TimelineWorkspaceViewModel.FindMidiSegment(project, context.OwnerId)!.Value.Segment
                    .ChannelEvents.ResolveByIds(ids)
                    .Select(static match => match.Value.Tick)),
            TimelineSelectionObjectKind.SubVoiceEventPoints => PointSpan(project.EventInstruments
                .Single(value => value.Id == context.OwnerId)
                .SubVoices.Single(value => value.Id == context.SecondaryId)
                .Events.ResolveByIdsInCollectionOrder(ids)
                .Select(static value => value.Tick)),
            _ => throw new ArgumentOutOfRangeException()
        };

        static long RangeSpan(IEnumerable<(long Start, long End)> source)
        {
            bool any = false;
            long minimum = long.MaxValue;
            long maximum = long.MinValue;
            foreach ((long start, long end) in source)
            {
                any = true;
                minimum = Math.Min(minimum, start);
                maximum = Math.Max(maximum, end);
            }
            return any ? checked(maximum - minimum) : 0;
        }

        static long PointSpan(IEnumerable<long> source)
        {
            bool any = false;
            long minimum = long.MaxValue;
            long maximum = long.MinValue;
            foreach (long value in source)
            {
                any = true;
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
            }
            return any ? checked(maximum - minimum) : 0;
        }
    }

    private static TimelineSurface? GetTimelineContextSurface(object sender) =>
        sender is MenuItem menuItem
        && ItemsControl.ItemsControlFromItemContainer(menuItem) is ContextMenu contextMenu
            ? contextMenu.PlacementTarget as TimelineSurface
            : null;

    private void OnSetTimeRangeFromObjectsClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not TimelineWorkspaceViewModel workspace
            || !workspace.SetTimeRangeFromObjectSelection())
        {
            ShowUnavailable("Set Time Range", "Select at least one timeline object first.");
        }
    }

    private void OnSelectObjectsInTimeRangeClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not TimelineWorkspaceViewModel workspace
            || !workspace.SetObjectSelectionFromTimeRange())
        {
            ShowUnavailable("Select Objects in Time Range", "Create a non-empty Time Range that intersects timeline objects first.");
            return;
        }
        _session.RefreshWorkspaceSelection(workspace);
    }

    private void OnClearTimeRangeClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is TimelineWorkspaceViewModel workspace)
        {
            workspace.ClearTimeRange();
        }
    }

    private void OnTimelineBackgroundInvoked(object? sender, TimelinePointEventArgs e)
    {
        if (_session.ActiveWorkspace is WorkspaceViewModel activeWorkspace)
        {
            if (activeWorkspace is TimelineWorkspaceViewModel
                {
                    Mode: TimelineWorkspaceMode.Arrangement
                } arrangement
                && e.IsEmptyBackground)
            {
                ClearArrangementTrackSelection(arrangement);
            }
            else
            {
                activeWorkspace.ActiveLane = e.Lane;
            }
            if (sender is TimelineSurface { ToolMode: TimelineToolMode.Select }
                && !e.IsDoubleClick
                && e.Modifiers == ModifierKeys.None
                && activeWorkspace.Selection.Ids.Count != 0)
            {
                activeWorkspace.Selection.Clear();
                _session.RefreshWorkspaceSelection(activeWorkspace);
            }
        }
        if (_session.ActiveWorkspace is TimelineWorkspaceViewModel timeline)
        {
            bool parameterSurface = sender is FrameworkElement { Tag: "ParameterLanes" };
            TimelineEditorSettings activeSettings = parameterSurface
                ? timeline.LaneEditorSettings
                : timeline.EditorSettings;
            timeline.EditCursorTick = activeSettings.SnapAbsolute(e.Tick);
            if (!e.IsDoubleClick || _session.Project is null) return;
            RunSynchronous("Create timeline object", () =>
            {
                long snapped = activeSettings.SnapAbsolute(e.Tick);
                switch (timeline.Mode)
                {
                    case TimelineWorkspaceMode.Arrangement:
                        if (timeline.GetArrangementLane(e.Lane) is not
                            { CanContainSegments: true, ObjectId: MidoraId trackId } arrangementLane) return;
                        long requestedLength = timeline.EditorSettings.DefaultLengthTicks;
                        long nextStart;
                        bool occupied;
                        if (arrangementLane.Kind == ArrangementLaneKind.LogicalTrack)
                        {
                            LogicalTrack track = _session.Project.Tracks.Single(value => value.Id == trackId);
                            nextStart = track.Segments.Where(item => item.ProjectStartTick > snapped)
                                .Select(item => item.ProjectStartTick).DefaultIfEmpty(long.MaxValue).Min();
                            occupied = track.Segments.Any(item => snapped >= item.ProjectStartTick && snapped < item.ProjectRange.EndTick);
                        }
                        else
                        {
                            PureMidiTrack track = _session.Project.PureMidiTracks.Single(value => value.Id == trackId);
                            nextStart = track.Segments.Where(item => item.ProjectStartTick > snapped)
                                .Select(item => item.ProjectStartTick).DefaultIfEmpty(long.MaxValue).Min();
                            occupied = track.Segments.Any(item => snapped >= item.ProjectStartTick && snapped < item.ProjectRange.EndTick);
                        }
                        if (occupied) return;
                        long available = nextStart == long.MaxValue ? requestedLength : checked(nextStart - snapped);
                        if (available <= 0) return;
                        IProjectEditCommand createSegment = arrangementLane.Kind == ArrangementLaneKind.LogicalTrack
                            ? ProjectDomainEditCommands.CreateSegment(trackId, snapped, Math.Min(requestedLength, available))
                            : ProjectDomainEditCommands.CreateMidiSegment(trackId, snapped, Math.Min(requestedLength, available));
                        ExecuteAndSelectCreated(createSegment, timeline);
                        break;
                    case TimelineWorkspaceMode.Segment:
                        if (timeline.ObjectId is not MidoraId segmentId) return;
                        if (parameterSurface)
                        {
                            (LogicalTrack Track, Segment Segment)? location = TimelineWorkspaceViewModel.FindSegment(_session.Project, segmentId);
                            if (location is null)
                            {
                                if (TimelineWorkspaceViewModel.FindMidiSegment(_session.Project, segmentId) is not null
                                    && timeline.GetActiveParameterLaneOption()?.DirectMidiTarget is DirectMidiEventLaneTarget directTarget)
                                {
                                    (int data1, int data2) = DenormalizeDirectMidiEventValue(directTarget, e.NormalizedValue);
                                    ExecuteAndSelectCreated(ProjectDomainEditCommands.CreateDirectMidiChannelEvent(
                                        segmentId,
                                        snapped,
                                        directTarget.Kind,
                                        data1,
                                        data2), timeline);
                                }
                                return;
                            }
                            if (timeline.GetActiveParameterLaneOption()?.LaneId is not MidoraId laneId) return;
                            LogicalParameterLane lane = location.Value.Segment.ParameterLanes.Single(item => item.Id == laneId);
                            EventInstrument instrument = _session.Project.FindEventInstrumentDefinition(
                                location.Value.Track)
                                ?? throw new InvalidOperationException(
                                    "The Logical Track is not bound to an Event Instrument Usage.");
                            LogicalParameterDefinition definition = instrument.LogicalParameters.Single(
                                item => item.Id == lane.ParameterId);
                            ExecuteAndSelectCreated(ProjectDomainEditCommands.CreateLogicalParameterPoint(
                                segmentId,
                                lane.Id,
                                snapped,
                                TimelineWorkspaceViewModel.DenormalizeParameterValue(definition, e.NormalizedValue),
                                CurveInterpolation.Step), timeline);
                            break;
                        }
                        IProjectEditCommand createNote = TimelineWorkspaceViewModel.FindMidiSegment(_session.Project, segmentId) is not null
                            ? ProjectDomainEditCommands.CreateDirectMidiNote(
                                segmentId,
                                snapped,
                                timeline.EditorSettings.DefaultLengthTicks,
                                Math.Clamp(127 - e.Lane, 0, 127),
                                timeline.EditorSettings.DefaultVelocity)
                            : ProjectDomainEditCommands.CreateLogicalNote(
                                segmentId,
                                snapped,
                                timeline.EditorSettings.DefaultLengthTicks,
                                Math.Clamp(127 - e.Lane, 0, 127),
                                timeline.EditorSettings.DefaultVelocity);
                        ExecuteAndSelectCreated(createNote, timeline);
                        break;
                    case TimelineWorkspaceMode.Conductor:
                        switch (e.Lane)
                        {
                            case 0:
                                ExecuteAndSelectCreated(ProjectDomainEditCommands.CreateTempo(snapped, 120m), timeline);
                                break;
                            case 1:
                                ExecuteAndSelectCreated(ProjectDomainEditCommands.CreateTimeSignature(snapped, 4, 4), timeline);
                                break;
                            case 2:
                                ExecuteAndSelectCreated(ProjectDomainEditCommands.CreateKeySignature(snapped, 0, isMinor: false), timeline);
                                break;
                            case 3:
                                ExecuteAndSelectCreated(ProjectDomainEditCommands.CreateProjectMarker(snapped, string.Empty), timeline);
                                break;
                            case 4:
                                ExecuteAndSelectCreated(ProjectDomainEditCommands.CreateProjectEndMarker(snapped), timeline);
                                break;
                        }
                        break;
                }
            });
            return;
        }

        if (_session.ActiveWorkspace is InstrumentWorkspaceViewModel instrument)
        {
            bool eventSurface = sender is FrameworkElement { Tag: "SubVoiceEvents" };
            TimelineEditorSettings activeSettings = eventSurface
                ? instrument.EventLaneEditorSettings
                : instrument.EditorSettings;
            instrument.EditCursorTick = activeSettings.SnapAbsolute(e.Tick);
            if (!e.IsDoubleClick
                || _session.Project is null
                || instrument.ObjectId is not MidoraId instrumentId)
            {
                return;
            }
            EventInstrument source = _session.Project.EventInstruments.Single(item => item.Id == instrumentId);
            if (sender is FrameworkElement { Tag: "SubVoiceNotes" }
                && instrument.ActiveSubVoiceId is MidoraId noteSubVoiceId)
            {
                long noteTick = instrument.EditorSettings.SnapAbsolute(e.Tick);
                RunSynchronous("Create Template Note", () => ExecuteAndSelectCreated(
                    ProjectDomainEditCommands.CreateTemplateNote(
                        instrumentId,
                        noteSubVoiceId,
                        noteTick,
                        instrument.EditorSettings.DefaultLengthTicks,
                        Math.Clamp(127 - e.Lane, 0, 127),
                        instrument.EditorSettings.DefaultVelocity),
                    instrument));
                return;
            }
            InstrumentRenderLane? lane = instrument.GetRenderLane(instrument.ActiveRenderLaneIndex);
            if (lane is null) return;
            long tick = instrument.EventLaneEditorSettings.SnapAbsolute(e.Tick);
            if (lane.Target is MidiValueTarget target)
            {
                int value = checked((int)InstrumentWorkspaceViewModel.DenormalizeMidiValue(
                    target,
                    e.NormalizedValue));
                RunSynchronous($"Create {TemplateEventMidiTargets.Format(target)}", () => ExecuteAndSelectCreated(
                    CreateTemplateEventCommand(instrumentId, lane.SubVoiceId, target, tick, value), instrument));
            }
        }
    }

    private void OnTimelineItemEditCompleted(object? sender, TimelineItemEditEventArgs e)
    {
        if (_session.Project is null || _session.ActiveWorkspace is null) return;
        RunSynchronous("Edit timeline object", () =>
        {
            if (_session.ActiveWorkspace is TimelineWorkspaceViewModel timeline)
            {
                TimelineEditorSettings activeSettings = e.Item.Kind is TimelineItemKind.LogicalParameterPoint
                    or TimelineItemKind.DirectMidiEvent
                    or TimelineItemKind.OpaqueMidiEvent
                    ? timeline.LaneEditorSettings
                    : timeline.EditorSettings;
                long snappedDelta = activeSettings.SnapDelta(
                    e.TickDelta,
                    checked(e.Item.StartTick + e.TickDelta));
                long unclampedSnappedDelta = snappedDelta;
                long snappedTarget = Math.Max(0, checked(e.Item.StartTick + snappedDelta));
                long nonnegativeSnappedDelta = checked(snappedTarget - e.Item.StartTick);
                MidoraId[] selected = timeline.Selection.Ids.Count == 0
                    ? [e.Item.Id]
                    : timeline.Selection.Ids.ToArray();
                switch (timeline.Mode)
                {
                    case TimelineWorkspaceMode.Arrangement:
                        EditArrangementItem(
                            timeline,
                            e,
                            selected,
                            snappedTarget,
                            e.EditKind == TimelineItemEditKind.ResizeStart
                                ? unclampedSnappedDelta
                                : nonnegativeSnappedDelta);
                        break;
                    case TimelineWorkspaceMode.Segment when timeline.ObjectId is MidoraId segmentId:
                        if (e.Item.Kind == TimelineItemKind.LogicalParameterPoint)
                        {
                            EditLogicalParameterPoint(segmentId, e, snappedTarget, selected);
                        }
                        else if (e.Item.Kind == TimelineItemKind.DirectMidiEvent)
                        {
                            EditDirectMidiEventPoint(segmentId, e, snappedTarget, selected);
                        }
                        else if (e.Item.Kind == TimelineItemKind.OpaqueMidiEvent)
                        {
                            EditOpaqueMidiEventPoint(segmentId, e, snappedTarget, selected);
                        }
                        else if (e.Item.Kind == TimelineItemKind.DirectMidiNote)
                        {
                            EditDirectMidiNotes(
                                segmentId,
                                e,
                                selected,
                                e.EditKind == TimelineItemEditKind.ResizeStart
                                    ? unclampedSnappedDelta
                                    : nonnegativeSnappedDelta);
                        }
                        else
                        {
                            EditLogicalNotes(
                                segmentId,
                                e,
                                selected,
                                e.EditKind == TimelineItemEditKind.ResizeStart
                                    ? unclampedSnappedDelta
                                    : nonnegativeSnappedDelta);
                        }
                        break;
                    case TimelineWorkspaceMode.Conductor:
                        EditConductorEvent(e.Item.Id, snappedTarget);
                        break;
                }
                return;
            }
            if (_session.ActiveWorkspace is InstrumentWorkspaceViewModel instrument)
            {
                EditTemplateEvent(instrument, e);
            }
        });
    }

    private void OnTimelineEventPointEditCompleted(object? sender, TimelineEventPointEditEventArgs e)
    {
        if (_session.ActiveWorkspace is TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Segment,
                ObjectId: MidoraId directSegmentId
            } directTimeline
            && directTimeline.GetActiveParameterLaneOption()?.DirectMidiTarget is DirectMidiEventLaneTarget directTarget
            && _session.Project is MidoraProject directProject
            && TimelineWorkspaceViewModel.FindMidiSegment(directProject, directSegmentId) is not null
            && e.Points.Count != 0)
        {
            DirectMidiEventPointEdit[] directEdits = e.Points
                .OrderBy(value => value.Key)
                .Select(value =>
                {
                    (int data1, int data2) = DenormalizeDirectMidiEventValue(directTarget, value.Value);
                    return new DirectMidiEventPointEdit(value.Key, data1, data2);
                })
                .ToArray();
            RunSynchronous("Draw Direct MIDI Event points", () => _session.Execute(
                ProjectDomainEditCommands.UpsertDirectMidiEventPoints(
                    directSegmentId,
                    directTarget.Kind,
                    directTarget.Data1,
                    directEdits)));
            return;
        }
        if (_session.ActiveWorkspace is TimelineWorkspaceViewModel timeline
            && timeline.Mode == TimelineWorkspaceMode.Segment
            && timeline.ObjectId is MidoraId segmentId
            && timeline.GetActiveParameterLaneOption() is ParameterLaneOption option
            && option.LaneId is MidoraId laneId
            && _session.Project is MidoraProject project
            && TimelineWorkspaceViewModel.FindSegment(project, segmentId) is var location
            && location is not null
            && project.ResolveEventInstrumentDefinitionId(location.Value.Track) is MidoraId eventInstrumentId
            && project.EventInstruments.FirstOrDefault(value => value.Id == eventInstrumentId)
                is EventInstrument eventInstrument
            && eventInstrument.LogicalParameters.FirstOrDefault(value => value.Id == option.ParameterId)
                is LogicalParameterDefinition definition
            && e.Points.Count != 0)
        {
            LogicalParameterPointEdit[] pointEdits = e.Points
                .OrderBy(value => value.Key)
                .Select(value => new LogicalParameterPointEdit(
                    value.Key,
                    TimelineWorkspaceViewModel.DenormalizeParameterValue(definition, value.Value)))
                .ToArray();
            RunSynchronous("Draw Logical Parameter points", () => _session.Execute(
                ProjectDomainEditCommands.UpsertLogicalParameterPoints(
                    segmentId,
                    laneId,
                    pointEdits)));
            return;
        }

        if (_session.ActiveWorkspace is not InstrumentWorkspaceViewModel workspace
            || workspace.ObjectId is not MidoraId instrumentId
            || workspace.ActiveSubVoiceId is not MidoraId voiceId
            || workspace.GetRenderLane(workspace.ActiveRenderLaneIndex)?.Target is not MidiValueTarget target
            || e.Points.Count == 0)
        {
            return;
        }
        TemplateEventPointEdit[] edits = e.Points
            .OrderBy(value => value.Key)
            .Select(value => new TemplateEventPointEdit(
                value.Key,
                checked((int)InstrumentWorkspaceViewModel.DenormalizeMidiValue(target, value.Value))))
            .ToArray();
        RunSynchronous("Draw Event points", () => _session.Execute(
            ProjectDomainEditCommands.UpsertTemplateEventPoints(
                instrumentId,
                voiceId,
                target,
                edits)));
    }

    private void EditConductorEvent(MidoraId id, long tick)
    {
        if (_session.Project is null) return;
        if (_session.Project.Conductor.Tempos.FirstOrDefault(item => item.Id == id) is TempoChange tempo)
        {
            _session.Execute(ProjectDomainEditCommands.UpdateTempo(id, tick, tempo.BeatsPerMinute));
        }
        else if (_session.Project.Conductor.TimeSignatures.FirstOrDefault(item => item.Id == id) is TimeSignatureChange signature)
        {
            _session.Execute(ProjectDomainEditCommands.UpdateTimeSignature(
                id, tick, signature.Numerator, signature.Denominator));
        }
        else if (_session.Project.Conductor.KeySignatures.FirstOrDefault(item => item.Id == id) is KeySignatureChange key)
        {
            _session.Execute(ProjectDomainEditCommands.UpdateKeySignature(id, tick, key.SharpsFlats, key.IsMinor));
        }
        else if (_session.Project.Conductor.Markers.FirstOrDefault(item => item.Id == id) is ProjectMarker marker)
        {
            _session.Execute(ProjectDomainEditCommands.UpdateProjectMarker(id, tick, marker.Name));
        }
        else if (_session.Project.Conductor.EndMarker?.Id == id)
        {
            _session.Execute(ProjectDomainEditCommands.UpdateProjectEndMarker(tick));
        }
    }

    private void EditArrangementItem(
        TimelineWorkspaceViewModel workspace,
        TimelineItemEditEventArgs edit,
        MidoraId[] selected,
        long snappedTarget,
        long snappedDelta)
    {
        MidoraProject project = _session.Project!;
        MidoraId[] logicalSelected = project.Tracks
            .SelectMany(track => track.Segments)
            .Where(segment => selected.Contains(segment.Id))
            .Select(segment => segment.Id)
            .ToArray();
        MidoraId[] midiSelected = project.PureMidiTracks
            .SelectMany(track => track.Segments)
            .Where(segment => selected.Contains(segment.Id))
            .Select(segment => segment.Id)
            .ToArray();
        if (logicalSelected.Length != 0 && midiSelected.Length != 0)
        {
            MidoraId[] mixed = logicalSelected.Concat(midiSelected).ToArray();
            if (edit.EditKind == TimelineItemEditKind.Move)
            {
                if (edit.CopyRequested)
                {
                    throw new InvalidOperationException(
                        "Copy-drag is unavailable for a mixed Logical/MIDI Segment selection. Use Duplicate, then drag the copy.");
                }
                long minimumStart = logicalSelected
                    .Select(id => TimelineWorkspaceViewModel.FindSegment(project, id)!.Value.Segment.ProjectStartTick)
                    .Concat(midiSelected.Select(id =>
                        TimelineWorkspaceViewModel.FindMidiSegment(project, id)!.Value.Segment.ProjectStartTick))
                    .Min();
                long delta = Math.Max(snappedDelta, -minimumStart);
                _session.Execute(ProjectDomainEditCommands.MoveArrangementSegmentsHorizontal(
                    mixed,
                    delta));
            }
            else if (edit.EditKind == TimelineItemEditKind.ResizeStart)
            {
                _session.Execute(ProjectDomainEditCommands.AdjustArrangementSegmentEdges(
                    mixed,
                    snappedDelta,
                    0,
                    workspace.EditorSettings.EffectiveOperationStepTicks));
            }
            else
            {
                long endDelta = workspace.EditorSettings.SnapDelta(
                    edit.TickDelta,
                    checked(edit.Item.EndTick + edit.TickDelta));
                _session.Execute(ProjectDomainEditCommands.AdjustArrangementSegmentEdges(
                    mixed,
                    0,
                    endDelta,
                    workspace.EditorSettings.EffectiveOperationStepTicks));
            }
            return;
        }

        (LogicalTrack Track, Segment Segment)? location =
            TimelineWorkspaceViewModel.FindSegment(project, edit.Item.Id);
        if (location is null)
        {
            if (TimelineWorkspaceViewModel.FindMidiSegment(_session.Project!, edit.Item.Id) is not null)
            {
                EditMidiArrangementItem(workspace, edit, selected, snappedTarget, snappedDelta);
            }
            return;
        }
        Segment segment = location.Value.Segment;
        if (edit.EditKind == TimelineItemEditKind.Move)
        {
            Segment[] locatedSelection = _session.Project!.Tracks
                .SelectMany(track => track.Segments
                    .Where(item => selected.Contains(item.Id))
                    .Select(item => item))
                .ToArray();
            if (locatedSelection.Length == 0)
            {
                locatedSelection = [segment];
            }
            MidoraId[] movingSegmentIds = locatedSelection.Select(item => item.Id).ToArray();
            long minimumStart = locatedSelection.Min(item => item.ProjectStartTick);
            long clampedDelta = Math.Max(snappedDelta, -minimumStart);
            int targetLane = Math.Clamp(
                checked(edit.Item.Lane + edit.LaneDelta),
                0,
                Math.Max(0, workspace.Snapshot!.ArrangementLanes.Count - 1));
            ArrangementLaneDescriptor? target = workspace.GetArrangementLane(targetLane);
            if (target is not
                { Kind: ArrangementLaneKind.LogicalTrack, ObjectId: MidoraId targetTrackId })
            {
                return;
            }
            snappedTarget = checked(edit.Item.StartTick + clampedDelta);
            if (edit.CopyRequested)
            {
                long firstNewStableId = _session.Project.NextStableId;
                _session.Execute(ProjectDomainEditCommands.DuplicateSegments(
                    movingSegmentIds,
                    edit.Item.Id,
                    targetTrackId,
                    snappedTarget));
                SelectCreatedWorkspaceObjects(workspace, firstNewStableId);
            }
            else
            {
                _session.Execute(ProjectDomainEditCommands.MoveSegments(
                    movingSegmentIds,
                    edit.Item.Id,
                    targetTrackId,
                    snappedTarget));
            }
            return;
        }

        Segment[] selectedSegments = _session.Project!.Tracks
            .SelectMany(track => track.Segments)
            .Where(item => selected.Contains(item.Id))
            .ToArray();
        if (selectedSegments.Length == 0)
        {
            selectedSegments = [segment];
        }
        MidoraId[] selectedSegmentIds = selectedSegments.Select(item => item.Id).ToArray();
        if (edit.EditKind == TimelineItemEditKind.ResizeStart)
        {
            _session.Execute(ProjectDomainEditCommands.AdjustSegmentEdges(
                selectedSegmentIds,
                startDelta: snappedDelta,
                endDelta: 0,
                minimumLengthTicks: workspace.EditorSettings.EffectiveOperationStepTicks));
        }
        else
        {
            long endDelta = workspace.EditorSettings.SnapDelta(
                edit.TickDelta,
                checked(edit.Item.EndTick + edit.TickDelta));
            _session.Execute(ProjectDomainEditCommands.AdjustSegmentEdges(
                selectedSegmentIds,
                startDelta: 0,
                endDelta,
                minimumLengthTicks: workspace.EditorSettings.EffectiveOperationStepTicks));
        }
    }

    private void EditLogicalNotes(
        MidoraId segmentId,
        TimelineItemEditEventArgs edit,
        MidoraId[] selected,
        long snappedDelta)
    {
        Segment segment = TimelineWorkspaceViewModel.FindSegment(_session.Project!, segmentId)?.Segment
            ?? throw new InvalidOperationException("The Segment no longer exists.");
        TimelineWorkspaceViewModel workspace =
            (TimelineWorkspaceViewModel)_session.ActiveWorkspace!;
        MidoraId[] noteIds = selected;
        long minimumStart;
        int minimumPitch;
        int maximumPitch;
        if (workspace.SelectionSnapshot.TryGetMetrics(
                TimelineItemKind.LogicalNote,
                out TimelineSelectionMetrics metrics)
            && metrics.Count == selected.Length)
        {
            minimumStart = metrics.MinimumStartTick;
            minimumPitch = 127 - metrics.MaximumLane;
            maximumPitch = 127 - metrics.MinimumLane;
        }
        else
        {
            HashSet<MidoraId> requested = selected.ToHashSet();
            LogicalNote[] notes = segment.Notes
                .ResolveByIdsInCollectionOrder(requested)
                .ToArray();
            if (notes.Length == 0) return;
            noteIds = notes.Select(static item => item.Id).ToArray();
            minimumStart = notes.Min(static item => item.StartTick);
            minimumPitch = notes.Min(static item => item.Note);
            maximumPitch = notes.Max(static item => item.Note);
        }
        switch (edit.EditKind)
        {
            case TimelineItemEditKind.Move:
                long tickDelta = Math.Max(snappedDelta, -minimumStart);
                int requestedPitchDelta = -edit.LaneDelta;
                int pitchDelta = edit.CopyRequested
                    ? Math.Clamp(
                        requestedPitchDelta,
                        -minimumPitch,
                        127 - maximumPitch)
                    : requestedPitchDelta;
                if (edit.CopyRequested)
                {
                    long firstNewStableId = _session.Project!.NextStableId;
                    _session.Execute(ProjectDomainEditCommands.DuplicateLogicalNotes(
                        segmentId,
                        noteIds,
                        segmentId,
                        checked(minimumStart + tickDelta),
                        pitchDelta));
                    SelectCreatedWorkspaceObjects(
                        (TimelineWorkspaceViewModel)_session.ActiveWorkspace!,
                        firstNewStableId);
                }
                else
                {
                    _session.Execute(ProjectDomainEditCommands.MoveLogicalNotes(
                        segmentId,
                        noteIds,
                        tickDelta,
                        pitchDelta));
                }
                break;
            case TimelineItemEditKind.ResizeStart:
                long startDelta = Math.Max(
                    snappedDelta,
                    -minimumStart);
                _session.Execute(ProjectDomainEditCommands.AdjustLogicalNoteEdges(
                    segmentId,
                    noteIds,
                    startDelta,
                    endDelta: 0,
                    minimumLengthTicks: workspace.EditorSettings.EffectiveOperationStepTicks));
                break;
            case TimelineItemEditKind.ResizeEnd:
                long endDelta = ((TimelineWorkspaceViewModel)_session.ActiveWorkspace!).EditorSettings.SnapDelta(
                    edit.TickDelta,
                    checked(edit.Item.EndTick + edit.TickDelta));
                _session.Execute(ProjectDomainEditCommands.AdjustLogicalNoteEdges(
                    segmentId,
                    noteIds,
                    startDelta: 0,
                    endDelta,
                    minimumLengthTicks: workspace.EditorSettings.EffectiveOperationStepTicks));
                break;
        }
    }

    private void EditMidiArrangementItem(
        TimelineWorkspaceViewModel workspace,
        TimelineItemEditEventArgs edit,
        MidoraId[] selected,
        long snappedTarget,
        long snappedDelta)
    {
        MidoraProject project = _session.Project!;
        (PureMidiTrack Track, MidiSegment Segment)? location =
            TimelineWorkspaceViewModel.FindMidiSegment(project, edit.Item.Id);
        if (location is null) return;
        MidiSegment[] selectedSegments = project.PureMidiTracks
            .SelectMany(value => value.Segments)
            .Where(value => selected.Contains(value.Id))
            .ToArray();
        if (selectedSegments.Length == 0) selectedSegments = [location.Value.Segment];
        MidoraId[] selectedIds = selectedSegments.Select(value => value.Id).ToArray();
        if (edit.EditKind == TimelineItemEditKind.Move)
        {
            int targetLaneIndex = Math.Clamp(
                checked(edit.Item.Lane + edit.LaneDelta),
                0,
                Math.Max(0, workspace.Snapshot!.ArrangementLanes.Count - 1));
            ArrangementLaneDescriptor? targetLane = workspace.GetArrangementLane(targetLaneIndex);
            if (targetLane is not { Kind: ArrangementLaneKind.PureMidiTrack, ObjectId: MidoraId targetTrackId })
                return;
            long minimumStart = selectedSegments.Min(value => value.ProjectStartTick);
            long clampedDelta = Math.Max(snappedDelta, -minimumStart);
            snappedTarget = checked(edit.Item.StartTick + clampedDelta);
            long firstNewStableId = project.NextStableId;
            _session.Execute(edit.CopyRequested
                ? ProjectDomainEditCommands.DuplicateMidiSegments(
                    selectedIds,
                    edit.Item.Id,
                    targetTrackId,
                    snappedTarget)
                : ProjectDomainEditCommands.MoveMidiSegments(
                    selectedIds,
                    edit.Item.Id,
                    targetTrackId,
                    snappedTarget));
            if (edit.CopyRequested) SelectCreatedWorkspaceObjects(workspace, firstNewStableId);
            return;
        }
        if (edit.EditKind == TimelineItemEditKind.ResizeStart)
        {
            _session.Execute(ProjectDomainEditCommands.AdjustMidiSegmentEdges(
                selectedIds,
                snappedDelta,
                0,
                workspace.EditorSettings.EffectiveOperationStepTicks));
        }
        else
        {
            long endDelta = workspace.EditorSettings.SnapDelta(
                edit.TickDelta,
                checked(edit.Item.EndTick + edit.TickDelta));
            _session.Execute(ProjectDomainEditCommands.AdjustMidiSegmentEdges(
                selectedIds,
                0,
                endDelta,
                workspace.EditorSettings.EffectiveOperationStepTicks));
        }
    }

    private void EditDirectMidiNotes(
        MidoraId segmentId,
        TimelineItemEditEventArgs edit,
        MidoraId[] selected,
        long snappedDelta)
    {
        MidiSegment segment = TimelineWorkspaceViewModel.FindMidiSegment(_session.Project!, segmentId)?.Segment
            ?? throw new InvalidOperationException("The MIDI Segment no longer exists.");
        TimelineWorkspaceViewModel workspace =
            (TimelineWorkspaceViewModel)_session.ActiveWorkspace!;
        MidoraId[] noteIds = selected;
        long minimumStart;
        if (workspace.SelectionSnapshot.TryGetMetrics(
                TimelineItemKind.DirectMidiNote,
                out TimelineSelectionMetrics metrics)
            && metrics.Count == selected.Length)
        {
            minimumStart = metrics.MinimumStartTick;
        }
        else
        {
            DirectMidiNote[] notes = segment.Notes.ResolveByIds(selected)
                .Select(static match => match.Value)
                .ToArray();
            if (notes.Length == 0) return;
            noteIds = notes.Select(static value => value.Id).ToArray();
            minimumStart = notes.Min(static value => value.StartTick);
        }
        switch (edit.EditKind)
        {
            case TimelineItemEditKind.Move:
                long tickDelta = Math.Max(snappedDelta, -minimumStart);
                int keyDelta = -edit.LaneDelta;
                if (edit.CopyRequested)
                {
                    long firstNewStableId = _session.Project!.NextStableId;
                    _session.Execute(ProjectDomainEditCommands.DuplicateDirectMidiNotes(
                        segmentId,
                        noteIds,
                        tickDelta,
                        keyDelta));
                    SelectCreatedWorkspaceObjects(
                        (TimelineWorkspaceViewModel)_session.ActiveWorkspace!,
                        firstNewStableId,
                        replaceSelectionWhenNoObjectSurvives: true);
                }
                else
                {
                    _session.Execute(ProjectDomainEditCommands.MoveDirectMidiNotes(
                        segmentId,
                        noteIds,
                        tickDelta,
                        keyDelta));
                }
                break;
            case TimelineItemEditKind.ResizeStart:
                _session.Execute(ProjectDomainEditCommands.AdjustDirectMidiNoteEdges(
                    segmentId,
                    noteIds,
                    Math.Max(snappedDelta, -minimumStart),
                    0,
                    workspace.EditorSettings.EffectiveOperationStepTicks));
                break;
            case TimelineItemEditKind.ResizeEnd:
                long endDelta = workspace.EditorSettings.SnapDelta(
                    edit.TickDelta,
                    checked(edit.Item.EndTick + edit.TickDelta));
                _session.Execute(ProjectDomainEditCommands.AdjustDirectMidiNoteEdges(
                    segmentId,
                    noteIds,
                    0,
                    endDelta,
                    workspace.EditorSettings.EffectiveOperationStepTicks));
                break;
        }
    }

    private void EditDirectMidiEventPoint(
        MidoraId segmentId,
        TimelineItemEditEventArgs edit,
        long snappedTarget,
        IReadOnlyCollection<MidoraId> selectedIds)
    {
        if (_session.Project is not MidoraProject project
            || TimelineWorkspaceViewModel.FindMidiSegment(project, segmentId) is not { } location
            || !location.Segment.ChannelEvents.TryGetById(
                edit.Item.Id,
                out DirectMidiChannelEvent? point)
            || point is null)
        {
            return;
        }
        DirectMidiChannelEvent[] selected = location.Segment.ChannelEvents
            .ResolveByIds(selectedIds.ToHashSet())
            .Select(static match => match.Value)
            .Where(value => TimelineWorkspaceViewModel.ToDirectMidiLaneTarget(value)
                    == TimelineWorkspaceViewModel.ToDirectMidiLaneTarget(point))
            .ToArray();
        if (selected.Length == 0) selected = [point];
        MidoraId[] selectedEventIds = selected.Select(static value => value.Id).ToArray();
        long minimumTick = selected.Min(static value => value.Tick);
        long tickDelta = edit.EditKind == TimelineItemEditKind.Move
            ? Math.Max(checked(snappedTarget - point.Tick), -minimumTick)
            : 0;
        int valueDelta = checked((int)Math.Round(
            edit.ValueDelta * (point.Kind == DirectMidiChannelEventKind.PitchBend ? 16383 : 127),
            MidpointRounding.AwayFromZero));
        int data1Delta = point.Kind is DirectMidiChannelEventKind.ProgramChange
            or DirectMidiChannelEventKind.ChannelPressure
            or DirectMidiChannelEventKind.PitchBend
                ? valueDelta
                : 0;
        int data2Delta = point.Kind is DirectMidiChannelEventKind.ProgramChange
            or DirectMidiChannelEventKind.ChannelPressure
                ? 0
                : valueDelta;
        if (point.Kind == DirectMidiChannelEventKind.PitchBend)
        {
            int oldValue = (point.Data2 << 7) | point.Data1;
            int newValue = Math.Clamp(oldValue + valueDelta, 0, 16383);
            data1Delta = (newValue & 0x7f) - point.Data1;
            data2Delta = ((newValue >> 7) & 0x7f) - point.Data2;
        }
        else if (data1Delta != 0)
        {
            data1Delta = Math.Clamp(point.Data1 + data1Delta, 0, 127) - point.Data1;
        }
        else
        {
            data2Delta = Math.Clamp(point.Data2 + data2Delta, 0, 127) - point.Data2;
        }
        long firstNewStableId = project.NextStableId;
        _session.Execute(ProjectDomainEditCommands.AdjustDirectMidiEventPoints(
            segmentId,
            selectedEventIds,
            tickDelta,
            data1Delta,
            data2Delta,
            edit.CopyRequested));
        if (edit.CopyRequested)
        {
            SelectCreatedWorkspaceObjects(
                (TimelineWorkspaceViewModel)_session.ActiveWorkspace!,
                firstNewStableId);
        }
    }

    private static (int Data1, int Data2) DenormalizeDirectMidiEventValue(
        DirectMidiEventLaneTarget target,
        double normalized)
    {
        normalized = Math.Clamp(normalized, 0, 1);
        if (target.Kind == DirectMidiChannelEventKind.PitchBend)
        {
            int value = checked((int)Math.Round(normalized * 16383, MidpointRounding.AwayFromZero));
            return (value & 0x7f, (value >> 7) & 0x7f);
        }
        int scalar = checked((int)Math.Round(normalized * 127, MidpointRounding.AwayFromZero));
        return target.Kind switch
        {
            DirectMidiChannelEventKind.ControlChange
                or DirectMidiChannelEventKind.PolyphonicKeyPressure => (target.Data1, scalar),
            DirectMidiChannelEventKind.ProgramChange
                or DirectMidiChannelEventKind.ChannelPressure => (scalar, 0),
            _ => (target.Data1, scalar)
        };
    }

    private void EditOpaqueMidiEventPoint(
        MidoraId segmentId,
        TimelineItemEditEventArgs edit,
        long snappedTarget,
        IReadOnlyCollection<MidoraId> selectedIds)
    {
        if (_session.Project is not MidoraProject project
            || TimelineWorkspaceViewModel.FindMidiSegment(project, segmentId) is not { } location
            || !location.Segment.OpaqueEvents.TryGetById(
                edit.Item.Id,
                out OpaqueMidiEvent? point)
            || point is null)
        {
            return;
        }
        MidoraId[] selectedEventIds;
        long minimumTick;
        if (_session.ActiveWorkspace is TimelineWorkspaceViewModel timeline
            && timeline.SelectionSnapshot.TryGetMetrics(
                TimelineItemKind.OpaqueMidiEvent,
                out TimelineSelectionMetrics metrics)
            && metrics.Count == selectedIds.Count)
        {
            selectedEventIds = selectedIds.ToArray();
            minimumTick = metrics.MinimumStartTick;
        }
        else
        {
            OpaqueMidiEvent[] selected = location.Segment.OpaqueEvents
                .ResolveByIds(selectedIds.ToHashSet())
                .Select(static match => match.Value)
                .ToArray();
            if (selected.Length == 0) selected = [point];
            selectedEventIds = selected.Select(static value => value.Id).ToArray();
            minimumTick = selected.Min(static value => value.Tick);
        }
        long tickDelta = Math.Max(
            checked(snappedTarget - point.Tick),
            -minimumTick);
        long firstNewStableId = project.NextStableId;
        _session.Execute(ProjectDomainEditCommands.AdjustOpaqueMidiEvents(
            segmentId,
            selectedEventIds,
            tickDelta,
            edit.CopyRequested));
        if (edit.CopyRequested)
        {
            SelectCreatedWorkspaceObjects(
                (TimelineWorkspaceViewModel)_session.ActiveWorkspace!,
                firstNewStableId);
        }
    }

    private TimelineEditorSettings GetActiveEditorSettings() => _session.ActiveWorkspace switch
    {
        TimelineWorkspaceViewModel timeline => timeline.EditorSettings,
        InstrumentWorkspaceViewModel instrument => instrument.EditorSettings,
        _ => _session.ArrangementEditorSettings
    };

    private TimelineEditorSettings GetFocusedEditorSettings()
    {
        bool eventLaneFocused = Keyboard.FocusedElement is TimelineSurface
        { SurfaceMode: TimelineSurfaceMode.EventLanes };
        return _session.ActiveWorkspace switch
        {
            TimelineWorkspaceViewModel timeline when eventLaneFocused => timeline.LaneEditorSettings,
            InstrumentWorkspaceViewModel instrument when eventLaneFocused => instrument.EventLaneEditorSettings,
            _ => GetActiveEditorSettings()
        };
    }

    private void EditLogicalParameterPoint(
        MidoraId segmentId,
        TimelineItemEditEventArgs edit,
        long snappedTarget,
        IReadOnlyCollection<MidoraId> selectedIds)
    {
        if (_session.Project is null) return;
        (LogicalTrack Track, Segment Segment)? location =
            TimelineWorkspaceViewModel.FindSegment(_session.Project, segmentId);
        if (location is null) return;
        LogicalParameterLane? lane = null;
        CurvePoint? point = null;
        foreach (LogicalParameterLane candidate in location.Value.Segment.ParameterLanes)
        {
            if (!candidate.Points.TryGetById(edit.Item.Id, out CurvePoint? resolved)
                || resolved is null)
            {
                continue;
            }
            lane = candidate;
            point = resolved;
            break;
        }
        if (lane is null || point is null) return;
        EventInstrument instrument = _session.Project.FindEventInstrumentDefinition(location.Value.Track)
            ?? throw new InvalidOperationException(
                "The Logical Track is not bound to an Event Instrument Usage.");
        LogicalParameterDefinition definition = instrument.LogicalParameters.Single(item => item.Id == lane.ParameterId);
        double normalized = TimelineWorkspaceViewModel.NormalizeParameterValue(definition, point.Value);
        double value = TimelineWorkspaceViewModel.DenormalizeParameterValue(
            definition,
            Math.Clamp(normalized + edit.ValueDelta, 0, 1));
        HashSet<MidoraId> requested = selectedIds.ToHashSet();
        requested.Add(point.Id);
        IReadOnlyList<CurvePoint> selectedPoints =
            lane.Points.ResolveByIdsInCollectionOrder(requested);
        if (selectedPoints.Count == 0) selectedPoints = [point];
        MidoraId[] selected = selectedPoints.Select(static value => value.Id).ToArray();
        double requestedValueDelta = value - point.Value;
        double selectedMinimumValue = selectedPoints.Min(static value => value.Value);
        double selectedMaximumValue = selectedPoints.Max(static value => value.Value);
        double minimumValueDelta = definition.Minimum - selectedMinimumValue;
        double maximumValueDelta = definition.Maximum - selectedMaximumValue;
        double valueDelta = Math.Clamp(requestedValueDelta, minimumValueDelta, maximumValueDelta);
        long tickDelta = edit.EditKind == TimelineItemEditKind.Move
            ? Math.Max(
                checked(snappedTarget - point.Tick),
                -selectedPoints.Min(static value => value.Tick))
            : 0;
        if (edit.CopyRequested)
        {
            long firstNewStableId = _session.Project.NextStableId;
            _session.Execute(ProjectDomainEditCommands.DuplicateLogicalParameterPoints(
                segmentId,
                lane.Id,
                selected,
                tickDelta,
                valueDelta));
            SelectCreatedWorkspaceObjects(
                (TimelineWorkspaceViewModel)_session.ActiveWorkspace!,
                firstNewStableId);
        }
        else
        {
            _session.Execute(ProjectDomainEditCommands.AdjustLogicalParameterPoints(
                segmentId,
                lane.Id,
                selected,
                tickDelta,
                valueDelta));
        }
    }

    private void OnAddParameterLaneClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Segment,
                ObjectId: MidoraId segmentId
            } workspace
            || _session.Project is null) return;
        (LogicalTrack Track, Segment Segment)? location =
            TimelineWorkspaceViewModel.FindSegment(_session.Project, segmentId);
        if (location is null)
        {
            if (TimelineWorkspaceViewModel.FindMidiSegment(_session.Project, segmentId) is null)
            {
                return;
            }
            DirectMidiLaneTargetDialog midiDialog = new()
            {
                Owner = this
            };
            if (midiDialog.ShowDialog() == true
                && midiDialog.Result is DirectMidiEventLaneTarget target)
            {
                workspace.AddDirectMidiLaneTarget(target);
                _session.RefreshWorkspace(workspace);
                workspace.ActiveParameterLaneIndex = workspace.ParameterLaneOptions.ToList()
                    .FindIndex(value => value.DirectMidiTarget == target);
                _session.RefreshWorkspace(workspace);
            }
            return;
        }
        if (_session.Project.ResolveEventInstrumentDefinitionId(location.Value.Track)
            is not MidoraId instrumentId)
        {
            ShowUnavailable("Add Logical Parameter Lane", "Bind this Logical Track to an Event Instrument first.");
            return;
        }
        EventInstrument instrument = _session.Project.EventInstruments.Single(item => item.Id == instrumentId);
        HashSet<MidoraId> existing = location.Value.Segment.ParameterLanes.Select(item => item.ParameterId).ToHashSet();
        SelectionDialogItem[] options = instrument.LogicalParameters
            .Where(item => !existing.Contains(item.Id))
            .Select(item => new SelectionDialogItem(item.Id, item.Name, $"{item.Type} · {item.Minimum}–{item.Maximum}"))
            .ToArray();
        if (options.Length == 0)
        {
            ShowUnavailable("Add Logical Parameter Lane", instrument.LogicalParameters.Count == 0
                ? "The bound Event Instrument has no Logical Parameters."
                : "This Segment already has a Lane for every Logical Parameter.");
            return;
        }
        SelectionDialog dialog = new("Add Logical Parameter Lane", "Select a Logical Parameter from the bound Event Instrument.", options) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.SelectedValue is MidoraId parameterId)
        {
            RunSynchronous("Create Logical Parameter Lane", () => ExecuteAndSelectCreated(
                ProjectDomainEditCommands.CreateLogicalParameterLane(segmentId, parameterId), workspace));
        }
    }

    private void EditTemplateEvent(
        InstrumentWorkspaceViewModel workspace,
        TimelineItemEditEventArgs edit)
    {
        if (workspace.ObjectId is not MidoraId instrumentId) return;
        EventInstrument instrument = _session.Project!.EventInstruments.Single(item => item.Id == instrumentId);
        SubVoice? voice = null;
        TemplateEvent? template = null;
        foreach (SubVoice candidate in instrument.SubVoices)
        {
            if (!candidate.Events.TryGetById(edit.Item.Id, out TemplateEvent? resolved)
                || resolved is null)
            {
                continue;
            }
            voice = candidate;
            template = resolved;
            break;
        }
        if (voice is null || template is null) return;
        TimelineEditorSettings activeSettings = template.Kind == TemplateEventKind.Note
            ? workspace.EditorSettings
            : workspace.EventLaneEditorSettings;
        long snappedDelta = activeSettings.SnapDelta(
            edit.TickDelta,
            checked((edit.EditKind == TimelineItemEditKind.ResizeEnd
                ? checked(template.Tick + Math.Max(1, template.LengthTicks))
                : template.Tick) + edit.TickDelta));
        if (template.Kind == TemplateEventKind.Note)
        {
            HashSet<MidoraId> requested = workspace.Selection.Ids.ToHashSet();
            requested.Add(template.Id);
            List<TimelineRenderItem> resolvedNotes = new(requested.Count);
            workspace.SubVoiceNoteSnapshot?.QueryByIds(requested, resolvedNotes);
            MidoraId[] selectedIds = resolvedNotes
                .Select(static item => item.Id)
                .Distinct()
                .ToArray();
            if (selectedIds.Length == 0) selectedIds = [template.Id];
            TimelineSelectionSnapshot noteSelection = new(
                revision: 0,
                selectedIds,
                selectedIds.Contains(workspace.Selection.Primary ?? default)
                    ? workspace.Selection.Primary
                    : template.Id,
                resolvedNotes.Count == 0
                    ? [new TimelineRenderItem(
                        template.Id,
                        TimelineItemKind.TemplateNote,
                        template.Tick,
                        checked(template.Tick + template.LengthTicks),
                        127 - template.Number,
                        template.Value / 127d,
                        1,
                        TimelineItemState.None)]
                    : resolvedNotes);
            _ = noteSelection.TryGetMetrics(
                TimelineItemKind.TemplateNote,
                out TimelineSelectionMetrics noteMetrics);
            switch (edit.EditKind)
            {
                case TimelineItemEditKind.Move:
                    long tickDelta = Math.Max(snappedDelta, -noteMetrics.MinimumStartTick);
                    int requestedPitchDelta = -edit.LaneDelta;
                    int pitchDelta = edit.CopyRequested
                        ? Math.Clamp(
                            requestedPitchDelta,
                            -(127 - noteMetrics.MaximumLane),
                            127 - (127 - noteMetrics.MinimumLane))
                        : requestedPitchDelta;
                    if (edit.CopyRequested)
                    {
                        long firstNewStableId = _session.Project.NextStableId;
                        _session.Execute(ProjectDomainEditCommands.DuplicateTemplateNotes(
                            instrumentId,
                            voice.Id,
                            selectedIds,
                            checked(noteMetrics.MinimumStartTick + tickDelta),
                            pitchDelta));
                        SelectCreatedWorkspaceObjects(workspace, firstNewStableId);
                    }
                    else
                    {
                        _session.Execute(ProjectDomainEditCommands.MoveTemplateNotes(
                            instrumentId,
                            voice.Id,
                            selectedIds,
                            tickDelta,
                            pitchDelta));
                    }
                    return;
                case TimelineItemEditKind.ResizeStart:
                    long startDelta = Math.Max(
                        snappedDelta,
                        -noteMetrics.MinimumStartTick);
                    _session.Execute(ProjectDomainEditCommands.AdjustTemplateNoteEdges(
                        instrumentId,
                        voice.Id,
                        selectedIds,
                        startDelta,
                        endDelta: 0,
                        minimumLengthTicks: workspace.EditorSettings.EffectiveOperationStepTicks));
                    return;
                case TimelineItemEditKind.ResizeEnd:
                    _session.Execute(ProjectDomainEditCommands.AdjustTemplateNoteEdges(
                        instrumentId,
                        voice.Id,
                        selectedIds,
                        startDelta: 0,
                        endDelta: snappedDelta,
                        minimumLengthTicks: workspace.EditorSettings.EffectiveOperationStepTicks));
                    return;
            }
        }
        MidiValueTarget? activeTarget = workspace.GetRenderLane(workspace.ActiveRenderLaneIndex)?.Target;
        if (activeTarget is MidiValueTarget target
            && TemplateEventMidiTargets.Enumerate(template).Contains(target))
        {
            HashSet<MidoraId> requested = workspace.Selection.Ids.ToHashSet();
            requested.Add(template.Id);
            TemplateEvent[] selectedEvents = voice.Events
                .ResolveByIdsInCollectionOrder(requested)
                .Where(item => item.Kind != TemplateEventKind.Note
                    && TemplateEventMidiTargets.Enumerate(item).Contains(target))
                .ToArray();
            long requestedTickDelta = workspace.EventLaneEditorSettings.SnapDelta(
                edit.TickDelta,
                checked(template.Tick + edit.TickDelta));
            long tickDelta = Math.Max(
                requestedTickDelta,
                -selectedEvents.Min(item => item.Tick));
            (double minimum, double maximum) = InstrumentWorkspaceViewModel.MidiValueRange(target);
            int requestedValueDelta = checked((int)Math.Round(
                edit.ValueDelta * (maximum - minimum),
                MidpointRounding.AwayFromZero));
            int minimumValueDelta = checked((int)Math.Ceiling(
                minimum - selectedEvents.Min(item => TemplateEventMidiTargets.GetValue(item, target))));
            int maximumValueDelta = checked((int)Math.Floor(
                maximum - selectedEvents.Max(item => TemplateEventMidiTargets.GetValue(item, target))));
            int valueDelta = Math.Clamp(
                requestedValueDelta,
                minimumValueDelta,
                maximumValueDelta);
            long firstNewStableId = _session.Project.NextStableId;
            _session.Execute(ProjectDomainEditCommands.AdjustSubVoiceEventPoints(
                instrumentId,
                voice.Id,
                selectedEvents.Select(item => item.Id).ToArray(),
                target,
                tickDelta,
                valueDelta,
                edit.CopyRequested));
            if (edit.CopyRequested)
            {
                SelectCreatedWorkspaceObjects(workspace, firstNewStableId);
            }
            return;
        }
        long tick = Math.Max(0, checked(template.Tick + snappedDelta));
        long length = template.LengthTicks;
        IProjectEditCommand command = template.Kind switch
        {
            TemplateEventKind.Note => ProjectDomainEditCommands.UpdateTemplateNote(
                instrumentId, voice.Id, template.Id, tick, length,
                template.Number, template.Value, template.FollowPitchDelta),
            TemplateEventKind.ControlChange => ProjectDomainEditCommands.UpdateTemplateControlChange(
                instrumentId, voice.Id, template.Id, tick, template.Number, template.Value),
            TemplateEventKind.Bank => ProjectDomainEditCommands.UpdateTemplateBank(
                instrumentId, voice.Id, template.Id, tick,
                template.HasBankMsb ? template.Value : null,
                template.HasBankLsb ? template.SecondaryValue : null),
            TemplateEventKind.Program => ProjectDomainEditCommands.UpdateTemplateProgram(
                instrumentId, voice.Id, template.Id, tick, template.Value),
            TemplateEventKind.PitchBend => ProjectDomainEditCommands.UpdateTemplatePitchBend(
                instrumentId, voice.Id, template.Id, tick, template.Value),
            TemplateEventKind.RegisteredParameter => ProjectDomainEditCommands.UpdateTemplateRegisteredParameter(
                instrumentId, voice.Id, template.Id, tick, template.Number, template.Value),
            TemplateEventKind.NonRegisteredParameter => ProjectDomainEditCommands.UpdateTemplateNonRegisteredParameter(
                instrumentId, voice.Id, template.Id, tick, template.Number, template.Value),
            TemplateEventKind.PitchBendRange => ProjectDomainEditCommands.UpdateTemplatePitchBendRange(
                instrumentId, voice.Id, template.Id, tick, template.Value, template.SecondaryValue),
            _ => throw new InvalidOperationException("Unsupported Template Event kind.")
        };
        _session.Execute(command);
    }

    private void EditValueCurvePoint(InstrumentWorkspaceViewModel workspace, TimelineItemEditEventArgs edit)
    {
        if (workspace.ObjectId is not MidoraId instrumentId || _session.Project is null) return;
        EventInstrument instrument = _session.Project.EventInstruments.Single(item => item.Id == instrumentId);
        foreach (SubVoice voice in instrument.SubVoices)
        {
            ValueCurve? curve = null;
            CurvePoint? point = null;
            foreach (ValueCurve candidate in voice.Curves)
            {
                if (!candidate.Points.TryGetById(edit.Item.Id, out CurvePoint? resolved)
                    || resolved is null)
                {
                    continue;
                }
                curve = candidate;
                point = resolved;
                break;
            }
            if (curve is null || point is null) continue;
            (double minimum, double maximum) = InstrumentWorkspaceViewModel.MidiValueRange(curve.Target);
            HashSet<MidoraId> requested = workspace.Selection.Ids.ToHashSet();
            requested.Add(point.Id);
            IReadOnlyList<CurvePoint> selectedPoints =
                curve.Points.ResolveByIdsInCollectionOrder(requested);
            if (selectedPoints.Count == 0) selectedPoints = [point];
            MidoraId[] selected = selectedPoints.Select(static value => value.Id).ToArray();
            double requestedValue = Math.Round(
                Math.Clamp(point.Value + edit.ValueDelta * (maximum - minimum), minimum, maximum),
                MidpointRounding.AwayFromZero);
            double requestedValueDelta = requestedValue - point.Value;
            double minimumValueDelta = minimum
                - selectedPoints.Min(static value => value.Value);
            double maximumValueDelta = maximum
                - selectedPoints.Max(static value => value.Value);
            long requestedTickDelta = workspace.EditorSettings.SnapDelta(
                edit.TickDelta,
                checked(edit.Item.StartTick + edit.TickDelta));
            long tickDelta = Math.Max(
                requestedTickDelta,
                -selectedPoints.Min(static value => value.Tick));
            _session.Execute(ProjectDomainEditCommands.AdjustValueCurvePoints(
                instrumentId,
                voice.Id,
                curve.Id,
                selected,
                tickDelta,
                Math.Clamp(requestedValueDelta, minimumValueDelta, maximumValueDelta)));
            return;
        }
    }

    private void OnAddTemplateEventClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not InstrumentWorkspaceViewModel workspace
            || workspace.ObjectId is not MidoraId instrumentId
            || _session.Project is null) return;
        EventInstrument instrument = _session.Project.EventInstruments.Single(item => item.Id == instrumentId);
        MidoraId? voiceId = workspace.ActiveSubVoiceId;
        if (voiceId is null)
        {
            SelectionDialog voiceDialog = new(
                "Add Event",
                "Select the target SubVoice.",
                instrument.SubVoices.Select((voice, index) => new SelectionDialogItem(
                    voice.Id,
                    string.IsNullOrWhiteSpace(voice.Name) ? $"SubVoice {index + 1}" : voice.Name)))
            { Owner = this };
            if (voiceDialog.ShowDialog() != true || voiceDialog.SelectedValue is not MidoraId selectedVoiceId) return;
            voiceId = selectedVoiceId;
        }
        MidiTargetDialog targetDialog = new("Add Event") { Owner = this };
        if (targetDialog.ShowDialog() != true || targetDialog.Result is not MidiValueTarget target) return;
        SubVoice targetVoice = instrument.SubVoices.Single(item => item.Id == voiceId.Value);
        TemplateEventMappingTarget mappingTarget = TemplateEventMidiTargets.ToMappingTarget(target);
        if (targetVoice.EventMappings.Any(item => item.Target == mappingTarget))
        {
            ShowUnavailable(
                "Add Event",
                $"The {TemplateEventMidiTargets.Format(target)} event lane already exists in this SubVoice.");
            return;
        }
        RunSynchronous($"Create {TemplateEventMidiTargets.Format(target)} lane", () =>
            _session.Execute(ProjectDomainEditCommands.CreateSubVoiceEventLane(
                instrumentId,
                voiceId.Value,
                target)));
    }

    private void OnDeleteSubVoiceEventLaneClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not InstrumentWorkspaceViewModel workspace
            || workspace.ObjectId is not MidoraId instrumentId
            || workspace.GetRenderLane(workspace.ActiveRenderLaneIndex) is not InstrumentRenderLane
            {
                Target: MidiValueTarget target,
                EventMappingTarget: not null
            } lane
            || _session.Project?.EventInstruments
                .SingleOrDefault(value => value.Id == instrumentId)?
                .SubVoices.SingleOrDefault(value => value.Id == lane.SubVoiceId) is not SubVoice voice)
        {
            return;
        }

        int pointCount = voice.Events.Count(value =>
            TemplateEventMidiTargets.Enumerate(value).Contains(target));
        string label = TemplateEventMidiTargets.Format(target);
        if (MessageDialog.Show(
                this,
                pointCount == 0
                    ? $"Delete the '{label}' event lane?"
                    : $"Delete the '{label}' event lane and its {pointCount} event point(s)?",
                "Delete SubVoice Event Lane",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        RunSynchronous("Delete SubVoice Event Lane", () =>
        {
            _session.Execute(ProjectDomainEditCommands.DeleteSubVoiceEventLane(
                instrumentId,
                lane.SubVoiceId,
                target,
                nonEmptyDeletionConfirmed: pointCount != 0));
            workspace.Selection.Clear();
        });
    }

    private void OnAddParameterMappingClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not InstrumentWorkspaceViewModel workspace
            || workspace.ObjectId is not MidoraId instrumentId
            || _session.Project is null) return;
        EventInstrument instrument = _session.Project.EventInstruments.Single(item => item.Id == instrumentId);
        if (instrument.LogicalParameters.Count == 0 || instrument.SubVoices.Count == 0)
        {
            ShowUnavailable(
                "Add Logical Parameter Mapping",
                "Create at least one Logical Parameter and one SubVoice first.");
            return;
        }
        ParameterMappingPropertiesDialog dialog = new(instrument) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        RunSynchronous("Create Logical Parameter Mapping", () => ExecuteAndSelectCreated(
            ProjectDomainEditCommands.CreateLogicalParameterMapping(
                instrumentId,
                dialog.ParameterId,
                dialog.SubVoiceId,
                dialog.Target,
                dialog.Rounding,
                dialog.Overflow),
            workspace));
    }

    private void OnEditParameterMappingClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId,
                Selection.Primary: MidoraId mappingId
            }
            || _session.Project is not MidoraProject project)
        {
            return;
        }
        EventInstrument instrument = project.EventInstruments.Single(item => item.Id == instrumentId);
        LogicalParameterMapping? mapping = instrument.ParameterMappings
            .FirstOrDefault(item => item.Id == mappingId);
        if (mapping is null) return;

        ParameterMappingPropertiesDialog dialog = new(instrument, mapping) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        RunSynchronous(
            "Update Logical Parameter Mapping",
            () => _session.Execute(
                new SequentialProjectEditCommand(
                    "Update Logical Parameter Mapping",
                    [
                        _ => ProjectDomainEditCommands.UpdateLogicalParameterMappingRoute(
                            instrumentId,
                            mapping.Id,
                            dialog.ParameterId,
                            dialog.SubVoiceId,
                            dialog.Target),
                        _ => ProjectDomainEditCommands.UpdateLogicalParameterMappingTargetSettings(
                            instrumentId,
                            mapping.Id,
                            dialog.Rounding,
                            dialog.Overflow)
                    ])));
    }

    private void OnMoveParameterMappingClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement
            {
                Tag: string directionText,
                DataContext: InstrumentWorkspaceViewModel
                {
                    ObjectId: MidoraId instrumentId,
                    Selection.Primary: MidoraId mappingId
                }
            }
            || !int.TryParse(directionText, out int direction)
            || _session.Project is not MidoraProject project)
        {
            return;
        }
        EventInstrument instrument = project.EventInstruments.Single(item => item.Id == instrumentId);
        LogicalParameterMapping? mapping = instrument.ParameterMappings
            .FirstOrDefault(item => item.Id == mappingId);
        if (mapping is null) return;
        int current = instrument.ParameterMappings.IndexOf(mapping);
        int target = Math.Clamp(current + direction, 0, instrument.ParameterMappings.Count - 1);
        RunSynchronous(
            "Reorder Logical Parameter Mapping",
            () => _session.Execute(
                ProjectDomainEditCommands.ReorderLogicalParameterMapping(
                    instrumentId,
                    mapping.Id,
                    target)));
    }

    private void OnAddMappingStepClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement
            {
                DataContext: InstrumentWorkspaceViewModel
                {
                    ObjectId: MidoraId instrumentId
                } workspace
            }
            || workspace.MappingChains.Count == 0)
        {
            ShowUnavailable("Add Mapping Step", "Create a Logical Parameter Mapping or Template Event first.");
            return;
        }
        if (_session.Project is not MidoraProject project)
        {
            return;
        }
        EventInstrument instrument = project.EventInstruments.Single(item => item.Id == instrumentId);
        MidoraId? preferredChainId = workspace.Selection.Primary is MidoraId selectedId
            ? workspace.MappingChains.FirstOrDefault(chain => chain.Id == selectedId)?.Id
                ?? workspace.MappingSteps.FirstOrDefault(step => step.Id == selectedId)?.ChainId
            : null;
        MidoraId chainId = preferredChainId ?? workspace.MappingChains[0].Id;
        ObjectPropertiesViewModel properties =
            ObjectPropertiesProjection.CreateMappingStepCreationProperties(
                instrument,
                workspace,
                chainId);
        ObjectPropertiesDialog dialog = new(
            properties,
            values =>
            {
                ExecuteAndSelectCreated(
                    ObjectPropertiesProjection.CreateMappingStepCreationCommand(
                        instrument,
                        values),
                    workspace);
                return true;
            })
        { Owner = this };
        _ = dialog.ShowDialog();
    }

    private void OnMoveMappingStepClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement
            {
                Tag: string directionText,
                DataContext: InstrumentWorkspaceViewModel
                {
                    ObjectId: MidoraId instrumentId,
                    SelectedMappingStepId: MidoraId stepId
                } workspace
            }
            || !int.TryParse(directionText, out int direction)
            || workspace.MappingSteps.FirstOrDefault(item => item.Id == stepId) is not MappingStepListItem item)
        {
            return;
        }
        MappingChainListItem chain = workspace.MappingChains.Single(value => value.Id == item.ChainId);
        int target = Math.Clamp(item.Index + direction, 0, Math.Max(0, chain.StepCount - 1));
        RunSynchronous("Reorder Mapping Step", () => _session.Execute(
            ProjectDomainEditCommands.ReorderMappingStep(
                instrumentId,
                item.ChainId,
                item.Id,
                target)));
    }

    private void OnAddEnvelopeClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId
            } workspace
            || _session.Project?.EventInstruments.FirstOrDefault(
                value => value.Id == instrumentId) is not EventInstrument instrument) return;
        string name = UniqueName(
            "Envelope Preset",
            instrument.Envelopes.Select(value => value.Name ?? string.Empty));
        ObjectPropertiesViewModel properties =
            ObjectPropertiesProjection.CreateEnvelopeCreationProperties(name);
        ObjectPropertiesDialog dialog = new(
            properties,
            values =>
            {
                ExecuteAndSelectCreated(
                    ObjectPropertiesProjection.CreateEnvelopeCreationCommand(
                        instrumentId,
                        values),
                    workspace);
                return true;
            })
        { Owner = this };
        _ = dialog.ShowDialog();
    }

    private static IProjectEditCommand CreateTemplateEventCommand(
        MidoraId instrumentId,
        MidoraId voiceId,
        TemplateEventKind kind,
        long tick,
        int rootNote) => kind switch
        {
            TemplateEventKind.Note => ProjectDomainEditCommands.CreateTemplateNote(
                instrumentId, voiceId, tick, 48, rootNote, 100),
            TemplateEventKind.ControlChange => ProjectDomainEditCommands.CreateTemplateControlChange(
                instrumentId, voiceId, tick, 1, 0),
            TemplateEventKind.Bank => ProjectDomainEditCommands.CreateTemplateBank(
                instrumentId, voiceId, tick, 0, 0),
            TemplateEventKind.Program => ProjectDomainEditCommands.CreateTemplateProgram(
                instrumentId, voiceId, tick, 0),
            TemplateEventKind.PitchBend => ProjectDomainEditCommands.CreateTemplatePitchBend(
                instrumentId, voiceId, tick, 0),
            TemplateEventKind.RegisteredParameter => ProjectDomainEditCommands.CreateTemplateRegisteredParameter(
                instrumentId, voiceId, tick, 0, 0),
            TemplateEventKind.NonRegisteredParameter => ProjectDomainEditCommands.CreateTemplateNonRegisteredParameter(
                instrumentId, voiceId, tick, 0, 0),
            TemplateEventKind.PitchBendRange => ProjectDomainEditCommands.CreateTemplatePitchBendRange(
                instrumentId, voiceId, tick, 2, 0),
            _ => throw new InvalidOperationException("Unsupported Template Event kind.")
        };

    private static IProjectEditCommand CreateTemplateEventCommand(
        MidoraId instrumentId,
        MidoraId voiceId,
        MidiValueTarget target,
        long tick,
        int value) => target.Kind switch
        {
            MidiValueKind.ControlChange => ProjectDomainEditCommands.CreateTemplateControlChange(
                instrumentId, voiceId, tick, target.Number, Math.Clamp(value, 0, 127)),
            MidiValueKind.BankMsb => ProjectDomainEditCommands.CreateTemplateBank(
                instrumentId, voiceId, tick, Math.Clamp(value, 0, 127), null),
            MidiValueKind.BankLsb => ProjectDomainEditCommands.CreateTemplateBank(
                instrumentId, voiceId, tick, null, Math.Clamp(value, 0, 127)),
            MidiValueKind.Program => ProjectDomainEditCommands.CreateTemplateProgram(
                instrumentId, voiceId, tick, Math.Clamp(value, 0, 127)),
            MidiValueKind.PitchBend => ProjectDomainEditCommands.CreateTemplatePitchBend(
                instrumentId, voiceId, tick, Math.Clamp(value, -8192, 8191)),
            MidiValueKind.RegisteredParameter => ProjectDomainEditCommands.CreateTemplateRegisteredParameter(
                instrumentId, voiceId, tick, target.Number, Math.Clamp(value, 0, 16_383)),
            MidiValueKind.NonRegisteredParameter => ProjectDomainEditCommands.CreateTemplateNonRegisteredParameter(
                instrumentId, voiceId, tick, target.Number, Math.Clamp(value, 0, 16_383)),
            MidiValueKind.PitchBendRangeSemitones => ProjectDomainEditCommands.CreateTemplatePitchBendRange(
                instrumentId, voiceId, tick, Math.Clamp(value, 0, 127), 0),
            MidiValueKind.PitchBendRangeCents => ProjectDomainEditCommands.CreateTemplatePitchBendRange(
                instrumentId, voiceId, tick, 2, Math.Clamp(value, 0, 99)),
            _ => throw new InvalidOperationException("Unsupported MIDI event target.")
        };

    private static IProjectEditCommand UpdateTemplateEventTargetCommand(
        MidoraId instrumentId,
        MidoraId voiceId,
        TemplateEvent template,
        MidiValueTarget target,
        long tick,
        int value) => target.Kind switch
        {
            MidiValueKind.ControlChange => ProjectDomainEditCommands.UpdateTemplateControlChange(
                instrumentId, voiceId, template.Id, tick, target.Number, value),
            MidiValueKind.BankMsb => ProjectDomainEditCommands.UpdateTemplateBank(
                instrumentId, voiceId, template.Id, tick, value,
                template.HasBankLsb ? template.SecondaryValue : null),
            MidiValueKind.BankLsb => ProjectDomainEditCommands.UpdateTemplateBank(
                instrumentId, voiceId, template.Id, tick,
                template.HasBankMsb ? template.Value : null, value),
            MidiValueKind.Program => ProjectDomainEditCommands.UpdateTemplateProgram(
                instrumentId, voiceId, template.Id, tick, value),
            MidiValueKind.PitchBend => ProjectDomainEditCommands.UpdateTemplatePitchBend(
                instrumentId, voiceId, template.Id, tick, value),
            MidiValueKind.RegisteredParameter => ProjectDomainEditCommands.UpdateTemplateRegisteredParameter(
                instrumentId, voiceId, template.Id, tick, target.Number, value),
            MidiValueKind.NonRegisteredParameter => ProjectDomainEditCommands.UpdateTemplateNonRegisteredParameter(
                instrumentId, voiceId, template.Id, tick, target.Number, value),
            MidiValueKind.PitchBendRangeSemitones => ProjectDomainEditCommands.UpdateTemplatePitchBendRange(
                instrumentId, voiceId, template.Id, tick, value, template.SecondaryValue),
            MidiValueKind.PitchBendRangeCents => ProjectDomainEditCommands.UpdateTemplatePitchBendRange(
                instrumentId, voiceId, template.Id, tick, template.Value, value),
            _ => throw new InvalidOperationException("Unsupported MIDI event target.")
        };

    private async void OnCompileClick(object sender, RoutedEventArgs e)
    {
        if (!_session.HasProject) return;
        bool completed = await RunOperationAsync(
            "Compile Project",
            async () => _ = await _session.CompileProjectAsync(),
            DesktopTaskLockLevel.ProjectEdit);
        if (completed)
        {
            bool failed = _session.ErrorCount != 0;
            bool hasIssues = failed || _session.WarningCount != 0;
            if (hasIssues) OpenTreeWorkspace(ProjectTreeNodeKind.Diagnostics);
            _session.SetStatusMessage(
                failed
                    ? $"Compile completed with {_session.ErrorCount} error(s). Open Diagnostics for details."
                    : hasIssues
                        ? $"Compile succeeded with {_session.WarningCount} warning(s). Open Diagnostics for details."
                        : "Compile succeeded. The current canonical result is consumable.",
                isError: failed);
        }
    }

    private void OnDismissNoticeClick(object sender, RoutedEventArgs e) => _session.Notice = null;

    private void OnDismissStatusMessageClick(object sender, RoutedEventArgs e) =>
        _session.SetStatusMessage(null);

    private void OnStatusMessageDetailsClick(object sender, RoutedEventArgs e)
    {
        if (_session.StatusMessage is not string message) return;
        TextDetailsDialog dialog = new(
            _session.StatusMessageDetailsTitle ?? "Status Message",
            _session.StatusMessageDetails ?? message)
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private void OnDiagnosticDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox { SelectedItem: DiagnosticRow diagnostic })
        {
            e.Handled = true;
            _ = Dispatcher.BeginInvoke(
                () => NavigateToDiagnostic(diagnostic),
                DispatcherPriority.Normal);
        }
    }

    private void OnNavigateDiagnosticClick(object sender, RoutedEventArgs e)
    {
        if (GetDiagnosticCommandTarget(sender) is DiagnosticRow diagnostic)
        {
            NavigateToDiagnostic(diagnostic);
        }
    }

    private void NavigateToDiagnostic(DiagnosticRow diagnostic)
    {
        if (!RunSynchronous("Navigate to Diagnostic", () => _session.NavigateToDiagnostic(diagnostic))) return;
        SourceReference source = diagnostic.SourceReference;
        if (source.EventInstrumentId != default && source.MappingFunctionId != default)
        {
            ShowMappingFunctionDialog(source.EventInstrumentId, source.MappingFunctionId);
        }
    }

    private void OnCopyDiagnosticMessageClick(object sender, RoutedEventArgs e)
    {
        if (GetDiagnosticCommandTarget(sender) is DiagnosticRow diagnostic)
        {
            Clipboard.SetText(diagnostic.Message);
        }
    }

    private void OnCopyDiagnosticCodeClick(object sender, RoutedEventArgs e)
    {
        if (GetDiagnosticCommandTarget(sender) is DiagnosticRow diagnostic)
        {
            Clipboard.SetText(diagnostic.Code);
        }
    }

    private DiagnosticRow? GetDiagnosticCommandTarget(object sender)
    {
        if (sender is MenuItem menuItem
            && ItemsControl.ItemsControlFromItemContainer(menuItem) is ContextMenu contextMenu
            && contextMenu.PlacementTarget is ListBox listBox
            && listBox.SelectedItem is DiagnosticRow selected)
        {
            return selected;
        }
        return _session.SelectedDiagnostic;
    }

    private void OnCancelTaskClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DesktopTaskViewModel task }
            || !task.CanRequestCancel)
        {
            return;
        }
        if (task.LockLevel == DesktopTaskLockLevel.FullApplication
            && MessageDialog.Show(
                this,
                "Cancel audio rendering? Midora will stop at a safe boundary, finalize cleanup, and will not publish incomplete output files.",
                "Cancel Audio Rendering",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        task.RequestCancel();
    }

    private async void OnMidiExportClick(object sender, RoutedEventArgs e)
    {
        if (_session.Project is not MidoraProject project) return;
        if (!StopPlaybackForProjectCommand("MIDI Export")) return;
        string? initialDirectory = ExistingRecentDirectory(RecentDirectoryPurpose.MidiExport)
            ?? (_session.Persistence?.CurrentProjectPath is string currentPath
            ? Path.GetDirectoryName(currentPath)
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        MidiExportDialog dialog = new(
            project,
            initialDirectory)
        { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Options is null) return;
        RecordRecentDirectory(
            RecentDirectoryPurpose.MidiExport,
            dialog.Options.OutputDirectory);

        PreparedDesktopMidiExport? prepared = null;
        if (!await RunOperationAsync(
                "Prepare MIDI Export",
                () => Task.Run(() => prepared = _session.PrepareMidiExport(dialog.Options))))
        {
            return;
        }
        if (prepared is null) return;
        if (!prepared.Succeeded)
        {
            string compile = string.Join("\n", prepared.Compilation.Diagnostics.Take(12)
                .Select(item => $"{item.Severity} {item.Code}: {item.Message}"));
            string planning = string.Join("\n", prepared.OutputPlan.Diagnostics.Take(12)
                .Select(item => $"{item.Code}: {item.Message}"));
            ShowError("MIDI Export Plan Failed", string.Join("\n", new[] { compile, planning }.Where(value => value.Length != 0)));
            return;
        }

        string preview = string.Join("\n", prepared.OutputPlan.Targets.Take(16).Select(target =>
            $"• {target.FullPath}{(target.ExistedAtFreeze ? "  [EXISTS]" : string.Empty)}"));
        if (prepared.OutputPlan.Targets.Count > 16)
        {
            preview += $"\n… and {prepared.OutputPlan.Targets.Count - 16} more target(s)";
        }
        bool overwrite = prepared.OutputPlan.RequiresOverwriteAuthorization;
        MessageBoxResult confirmation = MessageDialog.Show(
            this,
            $"Frozen MIDI export paths:\n\n{preview}\n\n" +
            (overwrite
                ? "One or more targets already exist. Choose Yes to authorize overwriting exactly these frozen paths."
                : "Choose OK to start the export."),
            "Confirm MIDI Export",
            overwrite ? MessageBoxButton.YesNo : MessageBoxButton.OKCancel,
            overwrite ? MessageBoxImage.Warning : MessageBoxImage.Information);
        if (overwrite ? confirmation != MessageBoxResult.Yes : confirmation != MessageBoxResult.OK) return;

        MidiExportTaskResult? result = null;
        bool completed = await RunOperationAsync(
            "MIDI Export",
            async cancellationToken => result = await _session.ExecuteMidiExportAsync(prepared, overwrite, cancellationToken),
            canCancel: true);
        if (!completed || result is null) return;
        string resultMessage = result.Status switch
        {
            MidiExportTaskStatus.Succeeded =>
                $"MIDI export completed.\n\n{result.Output?.Items.Count ?? 0} artifact(s) were published.",
            MidiExportTaskStatus.Cancelled => "MIDI export was cancelled. No uncommitted target was published.",
            _ => result.OutputFailure?.Message
                ?? string.Join("\n", result.ArtifactDiagnostics.Select(item =>
                    $"{item.Diagnostic.Code}: {item.Diagnostic.Message}"))
                ?? "MIDI export failed."
        };
        MessageDialog.Show(
            this,
            resultMessage,
            "MIDI Export",
            MessageBoxButton.OK,
            result.Status == MidiExportTaskStatus.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private async void OnAudioRenderClick(object sender, RoutedEventArgs e)
    {
        if (_session.Project is null) return;
        if (!StopPlaybackForProjectCommand("Render Audio")) return;
        if (_preferences.GetEnabledSoundFontPaths().Length == 0)
        {
            ShowUnavailable(
                "Render Audio",
                "Audio rendering requires at least one enabled application SoundFont in Preferences.");
            return;
        }
        string initialDirectory = ExistingRecentDirectory(RecentDirectoryPurpose.AudioRender)
            ?? (_session.Persistence?.CurrentProjectPath is string currentPath
            ? Path.GetDirectoryName(currentPath)!
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        string currentStem = _session.Persistence?.CurrentProjectPath is string projectPath
            ? Path.GetFileNameWithoutExtension(projectPath)
            : string.Empty;
        string suggested = AudioRenderOutputPlanner.SuggestWholeMixFileName(
            _session.Project.Metadata.ProjectName,
            currentStem);
        AudioRenderDialog dialog = new(
            _session.Project,
            initialDirectory,
            suggested)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || dialog.Options is null) return;
        RecordRecentDirectory(
            RecentDirectoryPurpose.AudioRender,
            Directory.Exists(dialog.Options.OutputPath)
                ? dialog.Options.OutputPath
                : Path.GetDirectoryName(dialog.Options.OutputPath));

        PreparedDesktopAudioRender? prepared = null;
        DesktopAudioRenderOptions options = dialog.Options;
        while (true)
        {
            if (_operationInProgress)
            {
                _session.SetStatusMessage(
                    "Another foreground task is already running. Midora does not queue foreground tasks.",
                    isError: true);
                return;
            }
            _operationInProgress = true;
            DesktopTaskViewModel task = _session.BeginTask(
                "Prepare Audio Render",
                canCancel: true,
                DesktopTaskLockLevel.FullApplication);
            try
            {
                prepared = await _session.PrepareAudioRenderAsync(options, task.CancellationToken);
                _session.CompleteTask(task, "Succeeded");
                break;
            }
            catch (OperationCanceledException) when (task.CancellationToken.IsCancellationRequested)
            {
                _session.CompleteTask(task, "Cancelled", "Cancelled by user.");
                return;
            }
            catch (Exception exception)
            {
                _session.CompleteTask(task, "Failed", exception.Message);
                ShowError("Prepare Audio Render", exception.Message);
                return;
            }
            finally
            {
                _operationInProgress = false;
            }
        }
        if (prepared is null) return;
        await using (prepared)
        {
            if (!prepared.Succeeded)
            {
                string compilation = string.Join("\n", prepared.Compilation.Diagnostics.Take(12)
                    .Select(item => $"{item.Severity} {item.Code}: {item.Message}"));
                string planning = string.Join("\n", prepared.OutputPlan.Diagnostics.Take(12)
                    .Select(item => $"{item.Severity} {item.Code}: {item.Message}"));
                ShowError("Audio Render Plan Failed", string.Join("\n", new[] { compilation, planning }.Where(value => value.Length != 0)));
                return;
            }

            string preview = string.Join("\n", prepared.OutputPlan.Targets.Take(16).Select(target =>
                $"• {target.FullPath}{(target.ExistedAtFreeze ? "  [EXISTS]" : string.Empty)}"));
            if (prepared.OutputPlan.Targets.Count > 16)
            {
                preview += $"\n… and {prepared.OutputPlan.Targets.Count - 16} more target(s)";
            }
            bool overwrite = prepared.OutputPlan.RequiresOverwriteAuthorization;
            MessageBoxResult confirmation = MessageDialog.Show(
                this,
                $"Frozen audio render paths:\n\n{preview}\n\n" +
                (overwrite
                    ? "One or more targets already exist. Choose Yes to authorize overwriting exactly these frozen paths."
                    : "Choose OK to start rendering."),
                "Confirm Audio Render",
                overwrite ? MessageBoxButton.YesNo : MessageBoxButton.OKCancel,
                overwrite ? MessageBoxImage.Warning : MessageBoxImage.Information);
            if (overwrite ? confirmation != MessageBoxResult.Yes : confirmation != MessageBoxResult.OK) return;

            AudioRenderTaskResult? result = null;
            using DispatcherCoalescingProgress<AudioRenderTaskProgress> progress = new(
                Dispatcher,
                TimeSpan.FromMilliseconds(100),
                value =>
                {
                    string detail = $"{value.Status}: output {Math.Max(0, value.CurrentOutputIndex + 1)}/{value.OutputCount}, {value.ProcessedFrameCount:N0}/{value.TotalFrameCount:N0} frames";
                    DesktopTaskViewModel? active = _session.ActiveForegroundTask;
                    if (active is not null)
                    {
                        active.SetCancellationAvailable(value.Status is not AudioRenderTaskStatus.Finalizing
                            and not AudioRenderTaskStatus.Completed
                            and not AudioRenderTaskStatus.CompletedWithErrors
                            and not AudioRenderTaskStatus.Failed
                            and not AudioRenderTaskStatus.Cancelled);
                        double? fraction = value.TotalFrameCount > 0
                            ? value.ProcessedFrameCount / (double)value.TotalFrameCount
                            : null;
                        _session.ReportTask(active, detail, fraction);
                    }
                });
            bool completed = await RunOperationAsync(
                "Render Audio",
                async cancellationToken => result = await _session.ExecuteAudioRenderAsync(
                    prepared,
                    overwrite,
                    progress,
                    cancellationToken),
                canCancel: true,
                lockLevel: DesktopTaskLockLevel.FullApplication);
            _session.SetStatusMessage(null);
            if (!completed || result is null) return;
            string message = AudioRenderResultFormatter.Format(result);
            MessageDialog.Show(
                this,
                message,
                "Audio Render",
                MessageBoxButton.OK,
                result.Status == AudioRenderTaskStatus.Completed ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
    }

    private void OnAboutClick(object sender, RoutedEventArgs e) => OpenAboutDialog();

    private void OpenAboutDialog()
    {
        AboutDialog dialog = new();
        _ = ShowModalDialog(dialog);
    }

    private void ShowUnavailable(string title, string message) =>
        _session.SetStatusMessage($"{title}: {message}", isError: true);

    private async Task<Exception?> InitializeAudioWorkerForActiveProjectAsync(
        CancellationToken cancellationToken)
    {
        if (!_session.HasEnabledSoundFonts)
        {
            return null;
        }
        _session.ActiveForegroundTask?.Report("Preparing audio Worker");
        return await _session.TryInitializeAudioWorkerAsync(cancellationToken);
    }

    private void OnSegmentLowerEditorSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)
            || sender is not TabControl { SelectedItem: TabItem { Header: "Parameter Lane" } }
            || _session.ActiveWorkspace is not TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Segment
            } workspace)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (!ReferenceEquals(_session.ActiveWorkspace, workspace)) return;
                TimelineSurface? timeline = FindWorkspaceElement<TimelineSurface>("ParameterLanes");
                if (timeline is { IsVisible: true, IsEnabled: true, Focusable: true })
                {
                    timeline.Focus();
                }
            }));
    }

    private void OnSubVoiceLowerEditorSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)
            || sender is not TabControl { SelectedItem: TabItem { Header: "Event Lane" } }
            || _session.ActiveWorkspace is not InstrumentWorkspaceViewModel workspace)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (!ReferenceEquals(_session.ActiveWorkspace, workspace)) return;
                TimelineSurface? timeline = FindWorkspaceElement<TimelineSurface>("SubVoiceEvents");
                if (timeline is { IsVisible: true, IsEnabled: true, Focusable: true })
                {
                    timeline.Focus();
                }
            }));
    }

    private void ReportAudioWorkerInitializationFailure(Exception? failure)
    {
        if (failure is null)
        {
            return;
        }
        const string summary =
            "The Project is available, but the audio Worker could not be initialized. "
            + "Playback will retry initialization when started.";
        _session.SetStatusMessage(
            summary,
            isError: true,
            details: failure.ToString(),
            detailsTitle: "Audio Worker Initialization");
        ShowError(
            "Audio Worker Initialization",
            $"{summary}\n\n{failure.Message}");
    }

    private Task<bool> RunOperationAsync(
        string title,
        Func<Task> operation,
        DesktopTaskLockLevel lockLevel = DesktopTaskLockLevel.MainWindow) =>
        RunOperationAsync(title, _ => operation(), canCancel: false, lockLevel: lockLevel);

    private async Task<bool> RunOperationAsync(
        string title,
        Func<CancellationToken, Task> operation,
        bool canCancel,
        DesktopTaskLockLevel lockLevel = DesktopTaskLockLevel.MainWindow,
        Func<Exception, string?>? handledException = null)
    {
        if (_operationInProgress)
        {
            _session.SetStatusMessage("Another foreground task is already running. Midora does not queue foreground tasks.", isError: true);
            return false;
        }
        _operationInProgress = true;
        DesktopTaskViewModel task = _session.BeginTask(title, canCancel, lockLevel);
        if (lockLevel >= DesktopTaskLockLevel.MainWindow)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (task.CanRequestCancel) TaskLockCancelButton.Focus();
                else TaskLockOverlay.Focus();
            }, DispatcherPriority.Input);
        }
        try
        {
            await operation(task.CancellationToken);
            _session.CompleteTask(task, "Succeeded");
            return true;
        }
        catch (OperationCanceledException) when (task.CancellationToken.IsCancellationRequested)
        {
            _session.CompleteTask(task, "Cancelled", "Cancelled by user.");
            return false;
        }
        catch (Exception exception)
        {
            string? handledDetail = handledException?.Invoke(exception);
            if (handledDetail is not null)
            {
                _session.CompleteTask(task, "Needs input", handledDetail);
                return false;
            }
            _session.CompleteTask(task, "Failed", exception.Message);
            ShowError(title, exception.Message);
            return false;
        }
        finally
        {
            _operationInProgress = false;
        }
    }

    private bool RunSynchronous(string title, Action operation)
    {
        try
        {
            operation();
            return true;
        }
        catch (Exception exception)
        {
            _session.SetStatusMessage($"{title}: {exception.Message}", isError: true);
            return false;
        }
    }

    internal bool PrepareForModalSurface()
    {
        if (_session.ActiveWorkspace is not InstrumentWorkspaceViewModel) return true;
        return _session.StopEventInstrumentKeyboardPreviewForEditing();
    }

    private bool? ShowModalDialog(Window dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        if (!PrepareForModalSurface()) return false;
        if (dialog.Owner is null && IsVisible) dialog.Owner = this;
        return dialog.ShowDialog();
    }

    private void OnEditingSurfaceContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_session.ActiveWorkspace is InstrumentWorkspaceViewModel)
        {
            _ = PrepareForModalSurface();
        }
    }

    private void OnMainMenuSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is InstrumentWorkspaceViewModel)
        {
            _ = PrepareForModalSurface();
        }
    }

    private void OnAnyTabSelectionChangedForPreviewPriority(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource is TabControl
            && _session.ActiveWorkspace is InstrumentWorkspaceViewModel)
        {
            _ = PrepareForModalSurface();
        }
    }

    private void ShowError(string title, string message) => MessageDialog.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    private SaveFileDialog CreateProjectSaveDialog(string title) => new()
    {
        Title = title,
        Filter = "Midora Project (*.midora)|*.midora",
        AddExtension = true,
        DefaultExt = ".midora",
        InitialDirectory = ExistingRecentDirectory(RecentDirectoryPurpose.SaveAndSaveCopy),
        FileName = $"{_session.ProjectDisplayName}.midora"
    };

    private string? ExistingRecentDirectory(RecentDirectoryPurpose purpose)
    {
        string? directory = _preferences.RecentDirectories.Get(purpose);
        return directory is not null && Directory.Exists(directory) ? directory : null;
    }

    private void RecordRecentDirectory(RecentDirectoryPurpose purpose, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        string normalized;
        try
        {
            normalized = Path.GetFullPath(directory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _session.SetStatusMessage($"The recent directory was not saved: {exception.Message}", isError: true);
            return;
        }
        ApplicationRecentDirectories recent = _preferences.RecentDirectories;
        ApplicationRecentDirectories updated = purpose switch
        {
            RecentDirectoryPurpose.OpenProject => recent with { OpenProject = normalized },
            RecentDirectoryPurpose.SaveAndSaveCopy => recent with { SaveAndSaveCopy = normalized },
            RecentDirectoryPurpose.SoundFont => recent with { SoundFont = normalized },
            RecentDirectoryPurpose.MidiExport => recent with { MidiExport = normalized },
            RecentDirectoryPurpose.AudioRender => recent with { AudioRender = normalized },
            _ => throw new ArgumentOutOfRangeException(nameof(purpose))
        };
        ApplicationPreferences candidate = _preferences with { RecentDirectories = updated };
        ApplicationPreferencesSaveResult saved = _preferenceStore.Save(candidate);
        if (saved.Succeeded)
        {
            _preferences = candidate;
        }
        else
        {
            _session.SetStatusMessage(
                saved.Notice?.Message ?? "The recent directory could not be saved.",
                isError: true);
        }
    }

    private void RecordRecentProject(string path)
    {
        RecentProjectsUpdateResult result = _recentProjects.RecordSuccessfulProjectActivation(path);
        if (!result.Succeeded)
        {
            _session.SetStatusMessage(
                result.Notice?.Message ?? "The Recent Projects list could not be saved.",
                isError: true);
        }
    }

    private void LoadDesktopPreferences()
    {
        ApplicationPreferencesLoadResult loaded = _preferenceStore.Load();
        _preferences = loaded.Preferences;
        DesktopUiPreferences ui = _preferences.DesktopUi;
        Width = ui.MainWindowWidth;
        Height = ui.MainWindowHeight;
        ProjectPanelColumn.Width = new GridLength(0);
        ApplyPanelVisibility();
        if (ui.MainWindowLeft is double left && ui.MainWindowTop is double top
            && left + Width >= SystemParameters.VirtualScreenLeft
            && left <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
            && top + Height >= SystemParameters.VirtualScreenTop
            && top <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        if (ui.MainWindowMaximized)
        {
            Loaded += (_, _) => WindowState = WindowState.Maximized;
        }
        if (loaded.Notice is not null)
        {
            _session.SetStatusMessage(loaded.Notice.Message, isError: true);
        }
    }

    private void SaveDesktopPreferences()
    {
        Rect bounds = RestoreBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            bounds = new(Left, Top, ActualWidth, ActualHeight);
        }
        DesktopUiPreferences currentUi = _preferences.DesktopUi;
        DesktopUiPreferences ui = currentUi with
        {
            MainWindowWidth = Math.Clamp(bounds.Width, 1100, 32768),
            MainWindowHeight = Math.Clamp(bounds.Height, 680, 32768),
            MainWindowLeft = double.IsFinite(bounds.Left) ? bounds.Left : null,
            MainWindowTop = double.IsFinite(bounds.Top) ? bounds.Top : null,
            MainWindowMaximized = WindowState == WindowState.Maximized,
            ProjectPanelWidth = currentUi.ProjectPanelVisible
                ? Math.Clamp(ProjectPanelColumn.ActualWidth, 170, 360)
                : currentUi.ProjectPanelWidth
        };
        _preferences = _preferences with { DesktopUi = ui };
        ApplicationPreferencesSaveResult saved = _preferenceStore.Save(_preferences);
        if (!saved.Succeeded && saved.Notice is not null)
        {
            _session.SetStatusMessage(saved.Notice.Message, isError: true);
        }
    }

    private void ApplyPanelVisibility()
    {
        DesktopUiPreferences ui = _preferences.DesktopUi;
        ProjectPanelMenuItem.IsChecked = false;
        SnapMenuItem.IsChecked = GetActiveEditorSettings().SnapEnabled;
        FollowPlaybackMenuItem.IsChecked = ui.FollowPlayback;
        FollowPlaybackToggleButton.IsChecked = ui.FollowPlayback;

        ProjectPanelGrid.Visibility = Visibility.Collapsed;
        ProjectPanelSplitter.Visibility = Visibility.Collapsed;
        ProjectPanelColumn.MinWidth = 0;
        ProjectPanelColumn.Width = new(0);
        ProjectSplitterColumn.Width = new(0);

    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closeApproved) return;
        e.Cancel = true;
        if (_closeRequestInProgress || _operationInProgress) return;

        _closeRequestInProgress = true;
        try
        {
            if (!StopPlaybackForProjectCommand("Exit Midora")) return;
            if (!await ConfirmCloseCurrentProjectAsync()) return;

            _closeApproved = true;
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Normal,
                new Action(Close));
        }
        catch (Exception exception)
        {
            ShowError(
                "Exit Midora",
                $"Midora could not complete the exit request: {exception.Message}");
        }
        finally
        {
            _closeRequestInProgress = false;
        }
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private void OnTimelineAltGestureConsumed(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TimelineSurface surface)
        {
            _pendingTimelineAltReleaseFocus = surface;
        }
        e.Handled = true;
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!IsAltKey(e) || _pendingTimelineAltReleaseFocus is not TimelineSurface surface)
        {
            return;
        }

        _pendingTimelineAltReleaseFocus = null;
        e.Handled = true;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (IsActive && surface.IsVisible && surface.IsEnabled && surface.Focusable)
                {
                    surface.Focus();
                }
            }));
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        _pendingTimelineAltReleaseFocus = null;
    }

    private static bool IsAltKey(KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        return key is Key.LeftAlt or Key.RightAlt;
    }

    private static bool IsAltF4(KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        return key == Key.F4 && (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsAltF4(e))
        {
            _pendingTimelineAltReleaseFocus = null;
        }
        if (_session.IsMainWindowTaskLocked)
        {
            DependencyObject? focused = Keyboard.FocusedElement as DependencyObject;
            if (focused is null || !TaskLockOverlay.IsAncestorOf(focused))
            {
                e.Handled = true;
            }
            return;
        }
        if (_spaceStartedPlayback && e.Key == Key.Tab)
        {
            _spaceStartedPlayback = false;
        }
        if (e.Key == Key.F12 && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            if (!e.IsRepeat && !IsTransientInputSurfaceOpen())
            {
                OpenAboutDialog();
            }
            return;
        }
        if (e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            int count = _session.Workspaces.Count;
            if (count != 0)
            {
                int current = _session.ActiveWorkspace is null
                    ? -1
                    : _session.Workspaces.IndexOf(_session.ActiveWorkspace);
                int direction = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1;
                int target = (current + direction + count) % count;
                _session.ActiveWorkspace = _session.Workspaces[target];
            }
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F6)
        {
            WorkspaceTabs.Focus();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.P
            && Keyboard.Modifiers == ModifierKeys.Control
            && !IsTransientInputSurfaceOpen())
        {
            if (!PrepareForModalSurface())
            {
                e.Handled = true;
                return;
            }
            e.Handled = TryOpenActiveProperties();
            if (e.Handled) return;
        }
        if (Keyboard.Modifiers == ModifierKeys.None
            && !IsTextEditingFocus()
            && !IsTransientInputSurfaceOpen()
            && TryActivateTimelineTool(e.Key))
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Space
            && Keyboard.Modifiers == ModifierKeys.None
            && (!IsPlaybackShortcutInputFocus() || (_session.IsPlaybackActive && _spaceStartedPlayback))
            && !IsTransientInputSurfaceOpen())
        {
            e.Handled = true;
            if (!e.IsRepeat)
            {
                if (_session.IsPlaybackActive)
                {
                    OnStopClick(this, new RoutedEventArgs());
                }
                else
                {
                    OnPlayClick(this, new RoutedEventArgs());
                }
            }
            return;
        }
        if (e.Key == Key.Delete && !IsTextEditingFocus())
        {
            if (!_session.CanEditProject) return;
            if (IsArrangementHeaderShortcutContext())
                OnArrangementHeaderDeleteClick(this, new RoutedEventArgs());
            else if (ProjectTree.IsKeyboardFocusWithin) DeleteSelectedTreeNode();
            else DeleteWorkspaceSelection();
            e.Handled = true;
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (IsTextEditingFocus())
        {
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control
            && !IsTransientInputSurfaceOpen()
            && TryInvokeTimelineSelectionOperationShortcut(e.Key))
        {
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.N: OnNewProjectClick(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.O: OnOpenProjectClick(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.S when shift: OnSaveCopyClick(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.S:
                OnSaveProjectClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Z: OnUndoClick(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.Y: OnRedoClick(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.X: OnCutClick(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.C: OnCopyClick(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.V: OnPasteClick(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.A: OnSelectAllClick(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.D: OnDuplicateClick(this, new RoutedEventArgs()); e.Handled = true; break;
        }
    }

    private bool TryOpenActiveProperties()
    {
        if (_session.ActiveWorkspace is not WorkspaceViewModel workspace) return false;
        if (workspace is InstrumentWorkspaceViewModel instrumentWorkspace
            && _session.Project?.EventInstruments.FirstOrDefault(
                value => value.Id == instrumentWorkspace.ObjectId) is EventInstrument instrument
            && instrumentWorkspace.Selection.Primary is MidoraId selectedId)
        {
            if (instrument.LogicalParameters.Any(value => value.Id == selectedId))
            {
                OnEditLogicalParameterDefinitionClick(this, new RoutedEventArgs());
                return true;
            }
            if (instrument.ParameterMappings.Any(value => value.Id == selectedId))
            {
                OnEditParameterMappingClick(this, new RoutedEventArgs());
                return true;
            }
            if (instrument.MappingFunctions.Any(value => value.Id == selectedId))
            {
                ShowMappingFunctionDialog(instrument.Id, selectedId);
                return true;
            }
        }
        ObjectPropertiesViewModel properties = _session.CreateObjectProperties(workspace);
        if (!ObjectPropertiesProjection.CanEditInPropertiesDialog(workspace, properties)) return false;
        if (workspace is InstrumentWorkspaceViewModel)
        {
            return OpenInstrumentProperties();
        }
        OnEditTimelinePropertiesClick(this, new RoutedEventArgs());
        return true;
    }

    private bool TryInvokeTimelineSelectionOperationShortcut(Key key)
    {
        if (key is not (Key.Q or Key.T or Key.E)
            || GetFocusedTimelineSurface() is not TimelineSurface surface)
        {
            return false;
        }

        _timelineSelectionOperationContext = ResolveTimelineSelectionOperationContext(surface);
        if (!_session.CanEditProject
            || _timelineSelectionOperationContext is not { Ids.Length: > 0 } context)
        {
            return true;
        }

        if (!PrepareForModalSurface()) return true;

        switch (key)
        {
            case Key.Q:
                OnScaleSelectionClick(this, new RoutedEventArgs());
                break;
            case Key.T when context.Kind is TimelineSelectionObjectKind.Segments
                or TimelineSelectionObjectKind.MidiSegments
                or TimelineSelectionObjectKind.MixedSegments
                or TimelineSelectionObjectKind.LogicalNotes
                or TimelineSelectionObjectKind.DirectMidiNotes
                or TimelineSelectionObjectKind.TemplateNotes:
                OnTransposeSelectionClick(this, new RoutedEventArgs());
                break;
            case Key.E:
                OnBatchEditSelectionClick(this, new RoutedEventArgs());
                break;
        }
        return true;
    }

    private bool TryActivateTimelineTool(Key key)
    {
        if (key == Key.A)
        {
            if (_session.ActiveWorkspace is not (TimelineWorkspaceViewModel
                or InstrumentWorkspaceViewModel))
            {
                return false;
            }
            TimelineEditorSettings settings = GetFocusedEditorSettings();
            settings.SnapEnabled = !settings.SnapEnabled;
            SnapMenuItem.IsChecked = settings.SnapEnabled;
            return true;
        }
        TimelineToolMode? mode = key switch
        {
            Key.D => TimelineToolMode.Draw,
            Key.S => TimelineToolMode.Select,
            Key.E => TimelineToolMode.Erase,
            _ => null
        };
        if (mode is not TimelineToolMode resolved) return false;
        switch (_session.ActiveWorkspace)
        {
            case TimelineWorkspaceViewModel timeline:
                timeline.ToolMode = resolved;
                return true;
            case InstrumentWorkspaceViewModel instrument:
                instrument.ToolMode = resolved;
                return true;
            default:
                return false;
        }
    }

    private void CutOrCopyProjectSelection(bool cut)
    {
        if (IsArrangementHeaderShortcutContext())
        {
            CopyArrangementHeader(cut);
            return;
        }
        if (CutOrCopySelectedLogicalTrack(cut))
        {
            return;
        }
        if (!cut && TryGetSelectedEventInstrumentId(out _))
        {
            _ = CopySelectedEventInstrument();
            return;
        }
        if (_session.Document is not ProjectDocumentSession document
            || _session.Project is not MidoraProject project
            || _session.ActiveWorkspace is not WorkspaceViewModel workspace
            || workspace.Selection.Ids.Count == 0)
        {
            return;
        }
        if (cut && !_session.CanEditProject) return;

        RunSynchronous(cut ? "Cut Project Objects" : "Copy Project Objects", () =>
        {
            MidoraId[] ids = workspace.Selection.Ids.ToArray();
            ProjectObjectClipboardPayload payload;
            IProjectEditCommand? deleteAfterWrite = null;

            switch (workspace)
            {
                case TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Arrangement }:
                    {
                        MidoraId primary = workspace.Selection.Primary
                            ?? throw new InvalidOperationException("The Segment selection has no primary object.");
                        if (TimelineWorkspaceViewModel.FindSegment(project, primary) is not null)
                        {
                            if (ids.Any(id => TimelineWorkspaceViewModel.FindSegment(project, id) is null))
                                throw new InvalidOperationException("Logical and MIDI Segments cannot be copied in one clipboard operation.");
                            if (cut)
                            {
                                ProjectObjectClipboardCutPreparation prepared = ProjectObjectClipboard.PrepareCutSegments(
                                    document, ids, primary);
                                payload = prepared.Payload;
                                deleteAfterWrite = prepared.DeleteAfterSuccessfulClipboardWrite;
                            }
                            else payload = ProjectObjectClipboard.CopySegments(document, ids, primary);
                        }
                        else if (TimelineWorkspaceViewModel.FindMidiSegment(project, primary) is not null)
                        {
                            if (ids.Any(id => TimelineWorkspaceViewModel.FindMidiSegment(project, id) is null))
                                throw new InvalidOperationException("Logical and MIDI Segments cannot be copied in one clipboard operation.");
                            if (cut)
                            {
                                ProjectObjectClipboardCutPreparation prepared = ProjectObjectClipboard.PrepareCutMidiSegments(
                                    document, ids, primary);
                                payload = prepared.Payload;
                                deleteAfterWrite = prepared.DeleteAfterSuccessfulClipboardWrite;
                            }
                            else payload = ProjectObjectClipboard.CopyMidiSegments(document, ids, primary);
                        }
                        else throw new InvalidOperationException("The primary Segment no longer exists.");
                        break;
                    }
                case TimelineWorkspaceViewModel
                {
                    Mode: TimelineWorkspaceMode.Segment,
                    ObjectId: MidoraId segmentId
                }:
                    {
                        (LogicalTrack Track, Segment Segment)? location =
                            TimelineWorkspaceViewModel.FindSegment(project, segmentId);
                        IReadOnlySet<MidoraId> selected = workspace.Selection.IdSet;
                        if (location is null)
                        {
                            if (TimelineWorkspaceViewModel.FindMidiSegment(project, segmentId) is not { } midi)
                                throw new InvalidOperationException("The Segment no longer exists.");
                            MidoraId[] directNotes = midi.Segment.Notes.ResolveByIds(selected)
                                .Select(static match => match.Value.Id).ToArray();
                            if (directNotes.Length == ids.Length)
                            {
                                if (cut)
                                {
                                    ProjectObjectClipboardCutPreparation prepared = ProjectObjectClipboard.PrepareCutDirectMidiNotes(
                                        document, segmentId, directNotes);
                                    payload = prepared.Payload;
                                    deleteAfterWrite = prepared.DeleteAfterSuccessfulClipboardWrite;
                                }
                                else payload = ProjectObjectClipboard.CopyDirectMidiNotes(document, segmentId, directNotes);
                                break;
                            }
                            MidoraId[] directEvents = midi.Segment.ChannelEvents.ResolveByIds(selected)
                                .Select(static match => match.Value.Id).ToArray();
                            if (directEvents.Length == ids.Length)
                            {
                                if (cut)
                                {
                                    ProjectObjectClipboardCutPreparation prepared = ProjectObjectClipboard.PrepareCutDirectMidiEvents(
                                        document, segmentId, directEvents);
                                    payload = prepared.Payload;
                                    deleteAfterWrite = prepared.DeleteAfterSuccessfulClipboardWrite;
                                }
                                else payload = ProjectObjectClipboard.CopyDirectMidiEvents(document, segmentId, directEvents);
                                break;
                            }
                            MidoraId[] opaqueEvents = midi.Segment.OpaqueEvents.ResolveByIds(selected)
                                .Select(static match => match.Value.Id).ToArray();
                            if (opaqueEvents.Length == ids.Length)
                            {
                                if (cut)
                                {
                                    ProjectObjectClipboardCutPreparation prepared = ProjectObjectClipboard.PrepareCutOpaqueMidiEvents(
                                        document, segmentId, opaqueEvents);
                                    payload = prepared.Payload;
                                    deleteAfterWrite = prepared.DeleteAfterSuccessfulClipboardWrite;
                                }
                                else payload = ProjectObjectClipboard.CopyOpaqueMidiEvents(document, segmentId, opaqueEvents);
                                break;
                            }
                            throw new InvalidOperationException(
                                "Copy or Cut may target Direct MIDI Notes, Direct MIDI Events, or imported MIDI events, not a mixed selection.");
                        }
                        if (ids.Length == 1
                            && location.Value.Segment.ParameterLanes.Any(lane => lane.Id == ids[0]))
                        {
                            if (cut)
                            {
                                ProjectObjectClipboardCutPreparation prepared = ProjectObjectClipboard.PrepareCutLogicalParameterLane(
                                    document,
                                    segmentId,
                                    ids[0]);
                                payload = prepared.Payload;
                                deleteAfterWrite = prepared.DeleteAfterSuccessfulClipboardWrite;
                            }
                            else
                            {
                                payload = ProjectObjectClipboard.CopyLogicalParameterLane(document, segmentId, ids[0]);
                            }
                            break;
                        }
                        MidoraId[] notes = location.Value.Segment.Notes
                            .ResolveByIdsInCollectionOrder(selected)
                            .Select(static item => item.Id)
                            .ToArray();
                        if (notes.Length == ids.Length)
                        {
                            if (cut)
                            {
                                ProjectObjectClipboardCutPreparation prepared = ProjectObjectClipboard.PrepareCutLogicalNotes(
                                    document, segmentId, notes);
                                payload = prepared.Payload;
                                deleteAfterWrite = prepared.DeleteAfterSuccessfulClipboardWrite;
                            }
                            else payload = ProjectObjectClipboard.CopyLogicalNotes(document, segmentId, notes);
                            break;
                        }
                        LogicalParameterLane? selectedLane = null;
                        IReadOnlyList<CurvePoint>? selectedPoints = null;
                        foreach (LogicalParameterLane lane in location.Value.Segment.ParameterLanes)
                        {
                            IReadOnlyList<CurvePoint> matches =
                                lane.Points.ResolveByIdsInCollectionOrder(selected);
                            if (matches.Count == 0) continue;
                            if (selectedLane is not null)
                            {
                                selectedLane = null;
                                selectedPoints = null;
                                break;
                            }
                            selectedLane = lane;
                            selectedPoints = matches;
                        }
                        if (selectedLane is null || selectedPoints?.Count != ids.Length)
                        {
                            throw new InvalidOperationException(
                                "Copy or Cut may target Logical Notes or points from one Logical Parameter Lane, not a mixed selection.");
                        }
                        if (cut)
                        {
                            ProjectObjectClipboardCutPreparation prepared = ProjectObjectClipboard.PrepareCutLogicalParameterLaneContent(
                                document, segmentId, selectedLane.Id, ids);
                            payload = prepared.Payload;
                            deleteAfterWrite = prepared.DeleteAfterSuccessfulClipboardWrite;
                        }
                        else payload = ProjectObjectClipboard.CopyLogicalParameterLaneContent(
                            document, segmentId, selectedLane.Id, ids);
                        break;
                    }
                case TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Conductor }:
                    {
                        if (project.Conductor.EndMarker is ProjectEndMarker end && ids.Contains(end.Id))
                        {
                            throw new InvalidOperationException("The Project End Marker is excluded from Project Clipboard operations.");
                        }
                        if (cut)
                        {
                            ProjectObjectClipboardCutPreparation prepared = ProjectObjectClipboard.PrepareCutConductorEvents(document, ids);
                            payload = prepared.Payload;
                            deleteAfterWrite = prepared.DeleteAfterSuccessfulClipboardWrite;
                        }
                        else payload = ProjectObjectClipboard.CopyConductorEvents(document, ids);
                        break;
                    }
                case InstrumentWorkspaceViewModel instrumentWorkspace when instrumentWorkspace.ObjectId is MidoraId instrumentId:
                    {
                        EventInstrument instrument = project.EventInstruments.Single(item => item.Id == instrumentId);
                        IReadOnlySet<MidoraId> selected = workspace.Selection.IdSet;
                        if (ids.Length == 1 && instrument.SubVoices.Any(voice => voice.Id == ids[0]))
                        {
                            if (cut)
                            {
                                ProjectObjectClipboardCutPreparation prepared = ProjectObjectClipboard.PrepareCutSubVoice(
                                    document,
                                    instrumentId,
                                    ids[0]);
                                payload = prepared.Payload;
                                deleteAfterWrite = prepared.DeleteAfterSuccessfulClipboardWrite;
                            }
                            else
                            {
                                payload = ProjectObjectClipboard.CopySubVoice(document, instrumentId, ids[0]);
                            }
                            break;
                        }
                        if (ids.Length == 1 && instrument.LogicalParameters.Any(value => value.Id == ids[0]))
                        {
                            if (cut)
                            {
                                ProjectObjectClipboardCutPreparation prepared =
                                    ProjectObjectClipboard.PrepareCutLogicalParameterDefinition(
                                        document,
                                        instrumentId,
                                        ids[0]);
                                payload = prepared.Payload;
                                deleteAfterWrite = prepared.DeleteAfterSuccessfulClipboardWrite;
                            }
                            else
                            {
                                payload = ProjectObjectClipboard.CopyLogicalParameterDefinition(
                                    document,
                                    instrumentId,
                                    ids[0]);
                            }
                            break;
                        }
                        if (ids.Length == 1 && instrument.ParameterMappings.Any(value => value.Id == ids[0]))
                        {
                            if (cut)
                            {
                                ProjectObjectClipboardCutPreparation prepared =
                                    ProjectObjectClipboard.PrepareCutLogicalParameterMapping(
                                        document,
                                        instrumentId,
                                        ids[0]);
                                payload = prepared.Payload;
                                deleteAfterWrite = prepared.DeleteAfterSuccessfulClipboardWrite;
                            }
                            else
                            {
                                payload = ProjectObjectClipboard.CopyLogicalParameterMapping(
                                    document,
                                    instrumentId,
                                    ids[0]);
                            }
                            break;
                        }
                        if (ids.Length == 1 && instrumentWorkspace.MappingChains.Any(chain => chain.Id == ids[0]))
                        {
                            MappingChainListItem selectedChain = instrumentWorkspace.MappingChains
                                .Single(chain => chain.Id == ids[0]);
                            if (cut && !selectedChain.CanDelete)
                            {
                                throw new InvalidOperationException(
                                    "The Note Mapping Chain cannot be cut or deleted.");
                            }
                            if (cut)
                            {
                                ProjectObjectClipboardCutPreparation prepared = ProjectObjectClipboard.PrepareCutMappingChain(
                                    document,
                                    instrumentId,
                                    ids[0]);
                                payload = prepared.Payload;
                                deleteAfterWrite = prepared.DeleteAfterSuccessfulClipboardWrite;
                            }
                            else
                            {
                                payload = ProjectObjectClipboard.CopyMappingChain(document, instrumentId, ids[0]);
                            }
                            break;
                        }
                        if (ids.Length == 1
                            && instrumentWorkspace.MappingSteps.FirstOrDefault(value => value.Id == ids[0])
                                is MappingStepListItem selectedStep)
                        {
                            if (cut)
                            {
                                ProjectObjectClipboardCutPreparation prepared =
                                    ProjectObjectClipboard.PrepareCutMappingStep(
                                        document,
                                        instrumentId,
                                        selectedStep.ChainId,
                                        selectedStep.Id);
                                payload = prepared.Payload;
                                deleteAfterWrite = prepared.DeleteAfterSuccessfulClipboardWrite;
                            }
                            else
                            {
                                payload = ProjectObjectClipboard.CopyMappingStep(
                                    document,
                                    instrumentId,
                                    selectedStep.ChainId,
                                    selectedStep.Id);
                            }
                            break;
                        }
                        if (ids.Length == 1 && instrument.Envelopes.Any(value => value.Id == ids[0]))
                        {
                            if (cut)
                            {
                                ProjectObjectClipboardCutPreparation prepared =
                                    ProjectObjectClipboard.PrepareCutEnvelopePreset(
                                        document,
                                        instrumentId,
                                        ids[0]);
                                payload = prepared.Payload;
                                deleteAfterWrite = prepared.DeleteAfterSuccessfulClipboardWrite;
                            }
                            else
                            {
                                payload = ProjectObjectClipboard.CopyEnvelopePreset(
                                    document,
                                    instrumentId,
                                    ids[0]);
                            }
                            break;
                        }
                        if (ids.Length == 1 && instrument.MappingFunctions.Any(value => value.Id == ids[0]))
                        {
                            if (cut)
                            {
                                ProjectObjectClipboardCutPreparation prepared =
                                    ProjectObjectClipboard.PrepareCutMappingFunction(
                                        document,
                                        instrumentId,
                                        ids[0]);
                                payload = prepared.Payload;
                                deleteAfterWrite = prepared.DeleteAfterSuccessfulClipboardWrite;
                            }
                            else
                            {
                                payload = ProjectObjectClipboard.CopyMappingFunction(
                                    document,
                                    instrumentId,
                                    ids[0]);
                            }
                            break;
                        }
                        SubVoice? selectedVoice = null;
                        IReadOnlyList<TemplateEvent>? selectedEvents = null;
                        foreach (SubVoice voice in instrument.SubVoices)
                        {
                            IReadOnlyList<TemplateEvent> matches =
                                voice.Events.ResolveByIdsInCollectionOrder(selected);
                            if (matches.Count == 0) continue;
                            if (selectedVoice is not null)
                            {
                                selectedVoice = null;
                                selectedEvents = null;
                                break;
                            }
                            selectedVoice = voice;
                            selectedEvents = matches;
                        }
                        if (selectedVoice is not null && selectedEvents?.Count == ids.Length)
                        {
                            if (cut)
                            {
                                ProjectObjectClipboardCutPreparation prepared = ProjectObjectClipboard.PrepareCutSubVoiceTimelineEvents(
                                    document, instrumentId, selectedVoice.Id, ids);
                                payload = prepared.Payload;
                                deleteAfterWrite = prepared.DeleteAfterSuccessfulClipboardWrite;
                            }
                            else payload = ProjectObjectClipboard.CopySubVoiceTimelineEvents(
                                document, instrumentId, selectedVoice.Id, ids);
                            break;
                        }
                        foreach (SubVoice voice in instrument.SubVoices)
                        {
                            ValueCurve? curve = null;
                            foreach (ValueCurve candidate in voice.Curves)
                            {
                                IReadOnlyList<CurvePoint> matches =
                                    candidate.Points.ResolveByIdsInCollectionOrder(selected);
                                if (matches.Count == 0) continue;
                                if (curve is not null || matches.Count != ids.Length)
                                {
                                    curve = null;
                                    break;
                                }
                                curve = candidate;
                            }
                            if (curve is null) continue;
                            if (cut)
                            {
                                ProjectObjectClipboardCutPreparation prepared = ProjectObjectClipboard.PrepareCutValueCurveContent(
                                    document, instrumentId, voice.Id, curve.Id, ids);
                                payload = prepared.Payload;
                                deleteAfterWrite = prepared.DeleteAfterSuccessfulClipboardWrite;
                            }
                            else payload = ProjectObjectClipboard.CopyValueCurveContent(
                                document, instrumentId, voice.Id, curve.Id, ids);
                            goto ClipboardPayloadReady;
                        }
                        throw new InvalidOperationException(
                            "Copy or Cut requires one Event Instrument structure item, Template Events from one SubVoice, or points from one Value Curve.");
                    }
                default:
                    return;
            }

        ClipboardPayloadReady:
            Clipboard.SetDataObject(payload.PlainTextSummary, copy: true);
            _projectClipboard = payload;
            _clipboardDocument = document;
            if (deleteAfterWrite is not null)
            {
                _session.Execute(deleteAfterWrite);
                workspace.Selection.Clear();
                if (workspace is InstrumentWorkspaceViewModel visualWorkspace)
                {
                    ClearInstrumentStructureVisualSelection(visualWorkspace);
                }
            }
            _session.SetStatusMessage($"{(cut ? "Cut" : "Copied")} {payload.PlainTextSummary}.");
        });
    }

    private void PasteProjectSelection()
    {
        if (IsArrangementHeaderShortcutContext()
            && TryGetArrangementHeaderContext(out ArrangementLaneDescriptor header)
            && CanPasteArrangementHeader(header))
        {
            OnArrangementHeaderPasteClick(this, new RoutedEventArgs());
            return;
        }
        if (_projectClipboard?.Kind == ProjectObjectClipboardKind.LogicalTrack
            && IsLogicalTrackShortcutContext()
            && PasteLogicalTrackClipboard(ResolveLogicalTrackPasteIndex()))
        {
            return;
        }
        if (_projectClipboard?.Kind == ProjectObjectClipboardKind.EventInstrument
            && PasteEventInstrumentClipboard())
        {
            return;
        }
        if (!_session.CanEditProject
            || _session.Document is not ProjectDocumentSession document
            || _session.Project is not MidoraProject project
            || _session.ActiveWorkspace is not WorkspaceViewModel workspace
            || _projectClipboard is not ProjectObjectClipboardPayload payload
            || !ReferenceEquals(document, _clipboardDocument))
        {
            return;
        }

        RunSynchronous("Paste Project Objects", () =>
        {
            long cursor = workspace is TimelineWorkspaceViewModel timeline
                ? timeline.EditCursorTick ?? 0
                : 0;
            IProjectEditCommand command;
            MidoraId? retainedStructureSelection = null;
            if (workspace is InstrumentWorkspaceViewModel chainWorkspace
                && chainWorkspace.ObjectId is MidoraId chainInstrumentId
                && payload.Kind == ProjectObjectClipboardKind.MappingChain)
            {
                MidoraId targetChainId = ResolveMappingChainTarget(chainWorkspace);
                MappingChainListItem target = chainWorkspace.MappingChains.Single(item => item.Id == targetChainId);
                if (target.StepCount != 0
                    && MessageDialog.Show(
                        this,
                        $"Replace all {target.StepCount} step(s) in '{target.Owner}' with the copied Mapping Chain?",
                        "Replace Mapping Chain",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    return;
                }
                command = ProjectObjectClipboard.CreatePasteMappingChainCommand(
                    document,
                    payload,
                    chainInstrumentId,
                    targetChainId,
                    nonEmptyReplacementConfirmed: target.StepCount != 0);
            }
            else if (workspace is InstrumentWorkspaceViewModel mappingWorkspace
                && mappingWorkspace.ObjectId is MidoraId mappingInstrumentId
                && payload.Kind == ProjectObjectClipboardKind.LogicalParameterMapping)
            {
                MidoraId targetMappingId = mappingWorkspace.Selection.Primary is MidoraId selected
                    && mappingWorkspace.ParameterMappings.Any(value => value.Id == selected)
                    ? selected
                    : throw new InvalidOperationException(
                        "Select a target Logical Parameter Mapping before pasting its configuration.");
                ParameterMappingListItem target = mappingWorkspace.ParameterMappings
                    .Single(value => value.Id == targetMappingId);
                if (target.StepCount != 0
                    && MessageDialog.Show(
                        this,
                        $"Replace all {target.StepCount} step(s) in the selected Logical Parameter Mapping?",
                        "Replace Logical Parameter Mapping",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    return;
                }
                command = ProjectObjectClipboard.CreatePasteLogicalParameterMappingCommand(
                    document,
                    payload,
                    mappingInstrumentId,
                    targetMappingId,
                    nonEmptyReplacementConfirmed: target.StepCount != 0);
                retainedStructureSelection = targetMappingId;
            }
            else if (workspace is InstrumentWorkspaceViewModel stepWorkspace
                && stepWorkspace.ObjectId is MidoraId stepInstrumentId
                && payload.Kind == ProjectObjectClipboardKind.MappingStep)
            {
                (MidoraId ChainId, int InsertionIndex) target =
                    ResolveMappingStepPasteTarget(stepWorkspace);
                command = ProjectObjectClipboard.CreatePasteMappingStepCommand(
                    document,
                    payload,
                    stepInstrumentId,
                    target.ChainId,
                    target.InsertionIndex);
            }
            else command = workspace switch
            {
                TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Arrangement } arrangement
                    when payload.Kind == ProjectObjectClipboardKind.Segments =>
                    ProjectObjectClipboard.CreatePasteSegmentsCommand(
                        document,
                        payload,
                        ResolveArrangementTargetTrack(project, arrangement),
                        cursor),
                TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Arrangement } arrangement
                    when payload.Kind == ProjectObjectClipboardKind.MidiSegments =>
                    ProjectObjectClipboard.CreatePasteMidiSegmentsCommand(
                        document,
                        payload,
                        ResolveArrangementTargetMidiTrack(project, arrangement),
                        cursor),
                TimelineWorkspaceViewModel
                {
                    Mode: TimelineWorkspaceMode.Segment,
                    ObjectId: MidoraId segmentId
                } when payload.Kind is ProjectObjectClipboardKind.LogicalNotes
                    or ProjectObjectClipboardKind.DirectMidiNotes =>
                    ProjectObjectClipboard.CreatePasteNotesCommand(
                        document,
                        payload,
                        segmentId,
                        cursor,
                        TimelineWorkspaceViewModel.FindMidiSegment(project, segmentId) is not null),
                TimelineWorkspaceViewModel
                {
                    Mode: TimelineWorkspaceMode.Segment,
                    ObjectId: MidoraId segmentId
                } when payload.Kind == ProjectObjectClipboardKind.DirectMidiEvents
                    && TimelineWorkspaceViewModel.FindMidiSegment(project, segmentId) is not null =>
                    ProjectObjectClipboard.CreatePasteDirectMidiEventsCommand(
                        document, payload, segmentId, cursor),
                TimelineWorkspaceViewModel
                {
                    Mode: TimelineWorkspaceMode.Segment,
                    ObjectId: MidoraId segmentId
                } when payload.Kind == ProjectObjectClipboardKind.OpaqueMidiEvents
                    && TimelineWorkspaceViewModel.FindMidiSegment(project, segmentId) is not null =>
                    ProjectObjectClipboard.CreatePasteOpaqueMidiEventsCommand(
                        document, payload, segmentId, cursor),
                TimelineWorkspaceViewModel
                {
                    Mode: TimelineWorkspaceMode.Segment,
                    ObjectId: MidoraId segmentId
                } segmentWorkspace when payload.Kind == ProjectObjectClipboardKind.LogicalParameterLaneContent =>
                    ProjectObjectClipboard.CreatePasteLogicalParameterLaneContentCommand(
                        document,
                        payload,
                        segmentId,
                        ResolveSegmentTargetLane(project, segmentWorkspace, segmentId),
                        cursor),
                TimelineWorkspaceViewModel
                {
                    Mode: TimelineWorkspaceMode.Segment,
                    ObjectId: MidoraId segmentId
                } when payload.Kind == ProjectObjectClipboardKind.LogicalParameterLane =>
                    ProjectObjectClipboard.CreatePasteLogicalParameterLaneCommand(document, payload, segmentId, cursor),
                TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Conductor }
                    when payload.Kind == ProjectObjectClipboardKind.ConductorEvents =>
                    ProjectObjectClipboard.CreatePasteConductorEventsCommand(document, payload, cursor),
                InstrumentWorkspaceViewModel instrumentWorkspace
                    when instrumentWorkspace.ObjectId is MidoraId instrumentId
                    && payload.Kind == ProjectObjectClipboardKind.SubVoice =>
                    ProjectObjectClipboard.CreatePasteSubVoiceCommand(
                        document,
                        payload,
                        instrumentId,
                        ResolveSubVoicePasteIndex(project, instrumentWorkspace, instrumentId)),
                InstrumentWorkspaceViewModel instrumentWorkspace
                    when instrumentWorkspace.ObjectId is MidoraId instrumentId
                    && payload.Kind == ProjectObjectClipboardKind.LogicalParameterDefinition =>
                    ProjectObjectClipboard.CreatePasteLogicalParameterDefinitionCommand(
                        document,
                        payload,
                        instrumentId,
                        ResolveStructurePasteIndex(
                            project.EventInstruments.Single(value => value.Id == instrumentId).LogicalParameters,
                            instrumentWorkspace.Selection.Primary,
                            value => value.Id)),
                InstrumentWorkspaceViewModel instrumentWorkspace
                    when instrumentWorkspace.ObjectId is MidoraId instrumentId
                    && payload.Kind == ProjectObjectClipboardKind.EnvelopePreset =>
                    ProjectObjectClipboard.CreatePasteEnvelopePresetCommand(
                        document,
                        payload,
                        instrumentId,
                        ResolveStructurePasteIndex(
                            project.EventInstruments.Single(value => value.Id == instrumentId).Envelopes,
                            instrumentWorkspace.Selection.Primary,
                            value => value.Id)),
                InstrumentWorkspaceViewModel instrumentWorkspace
                    when instrumentWorkspace.ObjectId is MidoraId instrumentId
                    && payload.Kind == ProjectObjectClipboardKind.MappingFunction =>
                    ProjectObjectClipboard.CreatePasteMappingFunctionCommand(
                        document,
                        payload,
                        instrumentId,
                        ResolveStructurePasteIndex(
                            project.EventInstruments.Single(value => value.Id == instrumentId).MappingFunctions,
                            instrumentWorkspace.Selection.Primary,
                            value => value.Id)),
                InstrumentWorkspaceViewModel instrumentWorkspace
                    when instrumentWorkspace.ObjectId is MidoraId instrumentId
                    && payload.Kind == ProjectObjectClipboardKind.SubVoiceTimelineEvents =>
                    ProjectObjectClipboard.CreatePasteSubVoiceTimelineEventsCommand(
                        document,
                        payload,
                        instrumentId,
                        ResolveInstrumentTargetLane(instrumentWorkspace).SubVoiceId,
                        cursor),
                InstrumentWorkspaceViewModel instrumentWorkspace
                    when instrumentWorkspace.ObjectId is MidoraId instrumentId
                    && payload.Kind == ProjectObjectClipboardKind.ValueCurveContent =>
                    CreatePasteValueCurveCommand(document, payload, instrumentWorkspace, instrumentId, cursor),
                _ => throw new InvalidOperationException(
                    $"{payload.Kind} cannot be pasted into the active Workspace selection scope.")
            };
            long firstNewStableId = project.NextStableId;
            _session.Execute(command);
            if (workspace is InstrumentWorkspaceViewModel structureWorkspace
                && IsInstrumentStructureClipboardKind(payload.Kind))
            {
                SelectInstrumentStructurePasteResult(
                    structureWorkspace,
                    payload.Kind,
                    firstNewStableId,
                    retainedStructureSelection);
            }
            else
            {
                SelectCreatedWorkspaceObjects(workspace, firstNewStableId);
            }
            _session.SetStatusMessage($"Pasted {payload.PlainTextSummary}.");
        });
    }

    private static MidoraId ResolveArrangementTargetTrack(
        MidoraProject project,
        TimelineWorkspaceViewModel workspace)
    {
        if (project.Tracks.Count == 0)
        {
            throw new InvalidOperationException("Create a Logical Track before pasting Segments.");
        }
        if (workspace.Selection.Primary is MidoraId segmentId
            && TimelineWorkspaceViewModel.FindSegment(project, segmentId) is { } located)
        {
            return located.Track.Id;
        }
        if (workspace.GetArrangementLane(workspace.ActiveLane ?? -1) is
            { Kind: ArrangementLaneKind.LogicalTrack, ObjectId: MidoraId activeTrackId })
        {
            return activeTrackId;
        }
        return project.TracksInArrangementOrder()
            .FirstOrDefault(value => value.Kind == ArrangementTrackKind.LogicalTrack) is
        { TrackId: var first } && first != default
                    ? first
                    : project.Tracks[0].Id;
    }

    private static MidoraId ResolveArrangementTargetMidiTrack(
        MidoraProject project,
        TimelineWorkspaceViewModel workspace)
    {
        if (project.PureMidiTracks.Count == 0)
            throw new InvalidOperationException("Create a MIDI Track before pasting MIDI Segments.");
        if (workspace.Selection.Primary is MidoraId segmentId
            && TimelineWorkspaceViewModel.FindMidiSegment(project, segmentId) is { } located)
        {
            return located.Track.Id;
        }
        if (workspace.GetArrangementLane(workspace.ActiveLane ?? -1) is
            { Kind: ArrangementLaneKind.PureMidiTrack, ObjectId: MidoraId activeTrackId })
        {
            return activeTrackId;
        }
        return project.TracksInArrangementOrder()
            .FirstOrDefault(value => value.Kind == ArrangementTrackKind.PureMidiTrack) is
        { TrackId: var first } && first != default
                    ? first
                    : project.PureMidiTracks[0].Id;
    }

    private static int ResolveSubVoicePasteIndex(
        MidoraProject project,
        InstrumentWorkspaceViewModel workspace,
        MidoraId instrumentId)
    {
        EventInstrument instrument = project.EventInstruments.Single(value => value.Id == instrumentId);
        if (workspace.Selection.Primary is MidoraId selected)
        {
            int index = instrument.SubVoices.FindIndex(value => value.Id == selected);
            if (index >= 0) return index + 1;
        }
        return instrument.SubVoices.Count;
    }

    private static int ResolveStructurePasteIndex<T>(
        IReadOnlyList<T> values,
        MidoraId? selectedId,
        Func<T, MidoraId> getId)
    {
        if (selectedId is MidoraId selected)
        {
            for (int index = 0; index < values.Count; index++)
            {
                if (getId(values[index]) == selected) return index + 1;
            }
        }
        return values.Count;
    }

    private static (MidoraId ChainId, int InsertionIndex) ResolveMappingStepPasteTarget(
        InstrumentWorkspaceViewModel workspace)
    {
        MidoraId selected = workspace.Selection.Primary
            ?? throw new InvalidOperationException(
                "Select a target Mapping Chain or Mapping Step before pasting.");
        MappingStepListItem? step = workspace.MappingSteps
            .FirstOrDefault(value => value.Id == selected);
        if (step is not null) return (step.ChainId, step.Index + 1);
        MappingChainListItem? chain = workspace.MappingChains
            .FirstOrDefault(value => value.Id == selected);
        return chain is not null
            ? (chain.Id, chain.StepCount)
            : throw new InvalidOperationException(
                "The current selection is not a Mapping Chain target.");
    }

    private static bool IsInstrumentStructureClipboardKind(ProjectObjectClipboardKind kind) =>
        kind is ProjectObjectClipboardKind.SubVoice
            or ProjectObjectClipboardKind.LogicalParameterDefinition
            or ProjectObjectClipboardKind.LogicalParameterMapping
            or ProjectObjectClipboardKind.MappingChain
            or ProjectObjectClipboardKind.MappingStep
            or ProjectObjectClipboardKind.EnvelopePreset
            or ProjectObjectClipboardKind.MappingFunction;

    private void SelectInstrumentStructurePasteResult(
        InstrumentWorkspaceViewModel workspace,
        ProjectObjectClipboardKind kind,
        long firstNewStableId,
        MidoraId? retainedSelection)
    {
        MidoraId? selected = retainedSelection ?? kind switch
        {
            ProjectObjectClipboardKind.SubVoice => workspace.SubVoices
                .Select(value => value.Id).FirstOrDefault(value => value.Value >= firstNewStableId),
            ProjectObjectClipboardKind.LogicalParameterDefinition => workspace.Parameters
                .Select(value => value.Id).FirstOrDefault(value => value.Value >= firstNewStableId),
            ProjectObjectClipboardKind.MappingChain => workspace.MappingChains
                .Select(value => value.Id).FirstOrDefault(value => value.Value >= firstNewStableId),
            ProjectObjectClipboardKind.MappingStep => workspace.MappingSteps
                .Select(value => value.Id).FirstOrDefault(value => value.Value >= firstNewStableId),
            ProjectObjectClipboardKind.EnvelopePreset => workspace.Envelopes
                .Select(value => value.Id).FirstOrDefault(value => value.Value >= firstNewStableId),
            ProjectObjectClipboardKind.MappingFunction => workspace.MappingFunctions
                .Select(value => value.Id).FirstOrDefault(value => value.Value >= firstNewStableId),
            _ => null
        };
        if (selected is not MidoraId id || id == default)
        {
            workspace.Selection.Clear();
        }
        else
        {
            workspace.Selection.Replace(id);
        }
        _session.RefreshWorkspaceSelection(workspace);
        if (selected is MidoraId selectedId && selectedId != default)
        {
            SetInstrumentStructureVisualSelection(workspace, selectedId);
        }
        else
        {
            ClearInstrumentStructureVisualSelection(workspace);
        }
    }

    private static MidoraId ResolveSegmentTargetLane(
        MidoraProject project,
        TimelineWorkspaceViewModel workspace,
        MidoraId segmentId)
    {
        (LogicalTrack Track, Segment Segment)? located = TimelineWorkspaceViewModel.FindSegment(project, segmentId);
        if (located is null || located.Value.Segment.ParameterLanes.Count == 0)
        {
            throw new InvalidOperationException("Create a Logical Parameter Lane before pasting points.");
        }
        return workspace.GetActiveParameterLaneOption()?.LaneId
            ?? throw new InvalidOperationException("Select an existing Logical Parameter Lane before pasting points.");
    }

    private static InstrumentRenderLane ResolveInstrumentTargetLane(InstrumentWorkspaceViewModel workspace) =>
        workspace.GetRenderLane(workspace.ActiveRenderLaneIndex) is InstrumentRenderLane target
            ? target
            : throw new InvalidOperationException("Select the target SubVoice Event Lane before pasting.");

    private static MidoraId ResolveMappingChainTarget(InstrumentWorkspaceViewModel workspace)
    {
        MidoraId selected = workspace.Selection.Primary
            ?? throw new InvalidOperationException("Select a target Mapping Chain or Mapping Step before pasting.");
        if (workspace.MappingChains.Any(chain => chain.Id == selected)) return selected;
        return workspace.MappingSteps.FirstOrDefault(step => step.Id == selected)?.ChainId
            ?? throw new InvalidOperationException("The current selection is not a Mapping Chain target.");
    }

    private static IProjectEditCommand CreatePasteValueCurveCommand(
        ProjectDocumentSession document,
        ProjectObjectClipboardPayload payload,
        InstrumentWorkspaceViewModel workspace,
        MidoraId instrumentId,
        long cursor)
    {
        InstrumentRenderLane lane = ResolveInstrumentTargetLane(workspace);
        MidoraId curveId = lane.ValueCurveId
            ?? throw new InvalidOperationException("Select a Value Curve lane before pasting curve points.");
        return ProjectObjectClipboard.CreatePasteValueCurveContentCommand(
            document, payload, instrumentId, lane.SubVoiceId, curveId, cursor);
    }

    private async void SelectAllInFocusedScope()
    {
        if (_session.ActiveWorkspace is not WorkspaceViewModel workspace
            || Keyboard.FocusedElement is not TimelineSurface surface
            || surface.Snapshot is null)
        {
            return;
        }
        int? lane = workspace.ActiveLane is int activeLane
            && (surface.Tag as string) == "ParameterLanes"
                ? activeLane
                : null;
        await MaterializeTimelineSelectionAsync(
            workspace,
            surface,
            surface.Snapshot,
            invert: false,
            lane);
    }

    private async Task MaterializeTimelineSelectionAsync(
        WorkspaceViewModel workspace,
        TimelineSurface surface,
        TimelineRenderSnapshot snapshot,
        bool invert,
        int? lane)
    {
        long selectionRevision = workspace.Selection.Revision;
        TimelineSelectionSnapshot current = workspace.SelectionSnapshot;
        MidoraId? currentAnchor = workspace.Selection.Anchor;
        if (current.Revision != selectionRevision)
        {
            // Selection presentation is frame-coalesced. Do not synchronously
            // resolve a potentially paged million-item selection merely to run
            // this command; allow the pending frame to publish it first.
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Render);
            current = workspace.SelectionSnapshot;
            selectionRevision = workspace.Selection.Revision;
            currentAnchor = workspace.Selection.Anchor;
            if (current.Revision != selectionRevision) return;
        }

        CancellationTokenSource cancellation = new();
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref _timelineSelectionMaterialization,
            cancellation);
        previous?.Cancel();
        previous?.Dispose();
        try
        {
            MaterializedTimelineSelection result = await Task.Run(
                () => BuildTimelineSelection(
                    snapshot,
                    current,
                    currentAnchor,
                    invert,
                    lane,
                    cancellation.Token),
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_session.ActiveWorkspace, workspace)
                || !ReferenceEquals(surface.Snapshot, snapshot)
                || !_session.IsWorkspaceSelectionMaterializationCurrent(
                    workspace,
                    selectionRevision))
            {
                return;
            }
            if (result.IsUnchanged) return;
            workspace.Selection.AdoptMaterialized(
                result.Ids,
                result.Primary,
                result.Anchor);
            _session.RefreshWorkspaceSelection(workspace);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _session.SetStatusMessage(
                $"Selection operation failed: {ex.Message}",
                isError: true);
        }
        finally
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _timelineSelectionMaterialization,
                        null,
                        cancellation),
                    cancellation))
            {
                cancellation.Dispose();
            }
        }
    }

    private static MaterializedTimelineSelection BuildTimelineSelection(
        TimelineRenderSnapshot snapshot,
        TimelineSelectionSnapshot current,
        MidoraId? currentAnchor,
        bool invert,
        int? lane,
        CancellationToken cancellationToken)
    {
        ImmutableHashSet<MidoraId>.Builder result = invert
            ? current.Ids.ToImmutableHashSet().ToBuilder()
            : ImmutableHashSet.CreateBuilder<MidoraId>();
        MidoraId? first = null;
        int visited = 0;
        foreach (TimelineRenderItem item in snapshot.EnumerateAllItems())
        {
            if ((visited++ & 4095) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            if (item.State.HasFlag(TimelineItemState.HitTestDisabled)
                || lane is int requiredLane && item.Lane != requiredLane)
            {
                continue;
            }
            first ??= item.Id;
            if (invert)
            {
                if (!result.Remove(item.Id)) result.Add(item.Id);
            }
            else
            {
                result.Add(item.Id);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();

        MidoraId? primary;
        MidoraId? anchor;
        if (!invert)
        {
            primary = first;
            anchor = first;
        }
        else
        {
            primary = current.Primary is MidoraId currentPrimary
                && result.Contains(currentPrimary)
                    ? currentPrimary
                    : result.Count == 0 ? null : result.Min();
            anchor = primary;
        }
        ImmutableHashSet<MidoraId> materialized = result.ToImmutable();
        bool unchanged = current.Count == materialized.Count
            && materialized.SetEquals(current.Ids)
            && primary == current.Primary
            && anchor == currentAnchor;
        return new(materialized, primary, anchor, unchanged);
    }

    private sealed record MaterializedTimelineSelection(
        ImmutableHashSet<MidoraId> Ids,
        MidoraId? Primary,
        MidoraId? Anchor,
        bool IsUnchanged);

    private void DuplicateFocusedSelection()
    {
        if (_session.CanEditProject && IsArrangementHeaderShortcutContext())
        {
            DuplicateArrangementHeader(shareInstrumentState: false);
            return;
        }
        if (_session.CanEditProject
            && TryGetSelectedLogicalTrack(out LogicalTrack logicalTrack, out _))
        {
            RunSynchronous("Duplicate Logical Track", () => _session.Execute(
                ProjectDomainEditCommands.DuplicateLogicalTrack(logicalTrack.Id)));
            return;
        }
        if (_session.CanEditProject && TryGetSelectedEventInstrumentId(out MidoraId eventInstrumentId))
        {
            RunSynchronous("Duplicate Event Instrument", () => _session.Execute(
                ProjectDomainEditCommands.DuplicateEventInstrument(eventInstrumentId)));
            return;
        }
        if (!_session.CanEditProject
            || _session.Project is not MidoraProject project
            || _session.ActiveWorkspace is not WorkspaceViewModel workspace
            || workspace.Selection.Ids.Count == 0)
        {
            return;
        }
        RunSynchronous("Duplicate Selection", () =>
        {
            MidoraId[] ids = workspace.Selection.Ids.ToArray();
            long cursor = (workspace as TimelineWorkspaceViewModel)?.EditCursorTick ?? 0;
            long firstNewStableId = project.NextStableId;
            switch (workspace)
            {
                case TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Arrangement } arrangement:
                    {
                        MidoraId primary = workspace.Selection.Primary!.Value;
                        if (TimelineWorkspaceViewModel.FindSegment(project, primary) is { } located)
                        {
                            long target = cursor == 0
                                ? checked(located.Segment.ProjectStartTick + located.Segment.LengthTicks)
                                : cursor;
                            _session.Execute(ProjectDomainEditCommands.DuplicateSegments(
                                ids, primary, ResolveArrangementTargetTrack(project, arrangement), target));
                        }
                        else if (TimelineWorkspaceViewModel.FindMidiSegment(project, primary) is { } midi)
                        {
                            long target = cursor == 0
                                ? checked(midi.Segment.ProjectStartTick + midi.Segment.LengthTicks)
                                : cursor;
                            MidoraId targetTrackId = arrangement.GetArrangementLane(arrangement.ActiveLane ?? -1) is
                            { Kind: ArrangementLaneKind.PureMidiTrack, ObjectId: MidoraId activeTrackId }
                                    ? activeTrackId
                                    : midi.Track.Id;
                            _session.Execute(ProjectDomainEditCommands.DuplicateMidiSegments(
                                ids, primary, targetTrackId, target));
                        }
                        else throw new InvalidOperationException("The primary Segment no longer exists.");
                        break;
                    }
                case TimelineWorkspaceViewModel
                {
                    Mode: TimelineWorkspaceMode.Segment,
                    ObjectId: MidoraId segmentId
                }:
                    {
                        HashSet<MidoraId> selected = ids.ToHashSet();
                        if (TimelineWorkspaceViewModel.FindSegment(project, segmentId) is { } located)
                        {
                            LogicalNote[] notes = located.Segment.Notes
                                .ResolveByIdsInCollectionOrder(selected)
                                .ToArray();
                            if (notes.Length != ids.Length)
                                throw new InvalidOperationException("Ctrl+D currently duplicates Logical Notes in the Segment note scope.");
                            long target = cursor == 0
                                ? checked(notes.Min(item => item.StartTick) + Math.Max(1, notes.Max(item => item.LengthTicks)))
                                : cursor;
                            _session.Execute(ProjectDomainEditCommands.DuplicateLogicalNotes(segmentId, ids, segmentId, target));
                        }
                        else if (TimelineWorkspaceViewModel.FindMidiSegment(project, segmentId) is { } midi)
                        {
                            DirectMidiNote[] notes = midi.Segment.Notes.ResolveByIds(selected)
                                .Select(static match => match.Value)
                                .ToArray();
                            if (notes.Length != ids.Length)
                                throw new InvalidOperationException("Ctrl+D currently duplicates Direct MIDI Notes in the piano-roll scope.");
                            long target = cursor == 0
                                ? checked(notes.Min(item => item.StartTick) + Math.Max(1, notes.Max(item => item.LengthTicks)))
                                : cursor;
                            _session.Execute(ProjectDomainEditCommands.DuplicateDirectMidiNotes(
                                segmentId,
                                ids,
                                checked(target - notes.Min(item => item.StartTick)),
                                0));
                        }
                        else throw new InvalidOperationException("The Segment no longer exists.");
                        break;
                    }
                case InstrumentWorkspaceViewModel
                {
                    ObjectId: MidoraId instrumentId
                } instrumentWorkspace:
                    {
                        InstrumentRenderLane lane = ResolveInstrumentTargetLane(instrumentWorkspace);
                        MidiValueTarget target = lane.Target
                            ?? throw new InvalidOperationException(
                                "Select a SubVoice MIDI Event lane before duplicating event points.");
                        EventInstrument instrument = project.EventInstruments.Single(value => value.Id == instrumentId);
                        SubVoice voice = instrument.SubVoices.Single(value => value.Id == lane.SubVoiceId);
                        HashSet<MidoraId> requested = ids.ToHashSet();
                        TemplateEvent[] events = voice.Events
                            .ResolveByIdsInCollectionOrder(requested)
                            .ToArray();
                        if (events.Length != ids.Length
                            || events.Any(value => value.Kind == TemplateEventKind.Note
                                || !TemplateEventMidiTargets.Enumerate(value).Contains(target)))
                        {
                            throw new InvalidOperationException(
                                "Ctrl+D may duplicate event points from one SubVoice MIDI Event lane only.");
                        }
                        long earliest = events.Min(value => value.Tick);
                        long destination = instrumentWorkspace.EditCursorTick is > 0
                            ? instrumentWorkspace.EditCursorTick.Value
                            : checked(events.Max(value => value.Tick)
                                + Math.Max(1, instrumentWorkspace.EditorSettings.EffectiveOperationStepTicks));
                        _session.Execute(ProjectDomainEditCommands.AdjustSubVoiceEventPoints(
                            instrumentId,
                            voice.Id,
                            ids,
                            target,
                            checked(destination - earliest),
                            valueDelta: 0,
                            duplicate: true));
                        break;
                    }
                default:
                    throw new InvalidOperationException("The active selection scope does not define Duplicate.");
            }
            SelectCreatedWorkspaceObjects(workspace, firstNewStableId);
        });
    }

    private void DeleteWorkspaceSelection()
    {
        if (_session.Project is null || _session.ActiveWorkspace is not WorkspaceViewModel workspace
            || workspace.Selection.Ids.Count == 0)
        {
            return;
        }
        MidoraId[] ids = workspace.Selection.Ids.ToArray();
        RunSynchronous("Delete Selection", () =>
        {
            switch (workspace)
            {
                case TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Arrangement }:
                    _session.Execute(ProjectDomainEditCommands.DeleteArrangementSegments(ids));
                    break;
                case TimelineWorkspaceViewModel
                {
                    Mode: TimelineWorkspaceMode.Segment,
                    ObjectId: MidoraId segmentId
                }:
                    DeleteSegmentSelection(segmentId, ids);
                    break;
                case TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Conductor }:
                    if (_session.Project.Conductor.EndMarker is ProjectEndMarker end
                        && ids.Contains(end.Id))
                    {
                        if (ids.Length != 1)
                        {
                            throw new InvalidOperationException(
                                "Delete the Project End Marker separately from ordinary Conductor events.");
                        }
                        _session.Execute(ProjectDomainEditCommands.DeleteProjectEndMarker());
                    }
                    else
                    {
                        _session.Execute(ProjectDomainEditCommands.DeleteConductorEvents(ids));
                    }
                    break;
                case InstrumentWorkspaceViewModel instrumentWorkspace:
                    DeleteInstrumentSelection(instrumentWorkspace, ids);
                    break;
            }
            workspace.Selection.Clear();
            if (workspace is InstrumentWorkspaceViewModel visualWorkspace)
            {
                ClearInstrumentStructureVisualSelection(visualWorkspace);
            }
        });
    }

    private void DeleteSegmentSelection(MidoraId segmentId, MidoraId[] ids)
    {
        (LogicalTrack Track, Segment Segment)? location =
            TimelineWorkspaceViewModel.FindSegment(_session.Project!, segmentId);
        if (location is null)
        {
            if (TimelineWorkspaceViewModel.FindMidiSegment(_session.Project!, segmentId) is not { } midi)
                return;
            HashSet<MidoraId> midiSelected = ids.ToHashSet();
            if (_session.ActiveWorkspace is TimelineWorkspaceViewModel timeline
                && timeline.SelectionSnapshot.TryGetMetrics(
                    TimelineItemKind.DirectMidiNote,
                    out TimelineSelectionMetrics noteMetrics)
                && noteMetrics.Count == ids.Length)
            {
                _session.Execute(ProjectDomainEditCommands.DeleteDirectMidiNotes(segmentId, ids));
                return;
            }
            if (_session.ActiveWorkspace is TimelineWorkspaceViewModel eventTimeline
                && eventTimeline.SelectionSnapshot.TryGetMetrics(
                    TimelineItemKind.DirectMidiEvent,
                    out TimelineSelectionMetrics eventMetrics)
                && eventMetrics.Count == ids.Length)
            {
                _session.Execute(ProjectDomainEditCommands.DeleteDirectMidiEvents(segmentId, ids));
                return;
            }
            if (_session.ActiveWorkspace is TimelineWorkspaceViewModel opaqueTimeline
                && opaqueTimeline.SelectionSnapshot.TryGetMetrics(
                    TimelineItemKind.OpaqueMidiEvent,
                    out TimelineSelectionMetrics opaqueMetrics)
                && opaqueMetrics.Count == ids.Length)
            {
                _session.Execute(ProjectDomainEditCommands.DeleteOpaqueMidiEvents(segmentId, ids));
                return;
            }
            MidoraId[] directNotes = midi.Segment.Notes.ResolveByIds(midiSelected)
                .Select(static match => match.Value.Id).ToArray();
            if (directNotes.Length == ids.Length)
            {
                _session.Execute(ProjectDomainEditCommands.DeleteDirectMidiNotes(segmentId, directNotes));
                return;
            }
            MidoraId[] events = midi.Segment.ChannelEvents.ResolveByIds(midiSelected)
                .Select(static match => match.Value.Id).ToArray();
            if (events.Length == ids.Length)
            {
                _session.Execute(ProjectDomainEditCommands.DeleteDirectMidiEvents(segmentId, events));
                return;
            }
            MidoraId[] opaqueEvents = midi.Segment.OpaqueEvents.ResolveByIds(midiSelected)
                .Select(static match => match.Value.Id).ToArray();
            if (opaqueEvents.Length == ids.Length)
            {
                _session.Execute(ProjectDomainEditCommands.DeleteOpaqueMidiEvents(segmentId, opaqueEvents));
                return;
            }
            throw new InvalidOperationException(
                "A single delete gesture may target Direct MIDI Notes, Direct MIDI Events, or imported MIDI events, not a mixed selection.");
        }
        HashSet<MidoraId> selected = ids.ToHashSet();
        if (ids.Length == 1
            && location.Value.Segment.ParameterLanes.Any(lane => lane.Id == ids[0]))
        {
            if (MessageDialog.Show(
                    this,
                    "Delete the selected Logical Parameter Lane and all of its points?",
                    "Delete Logical Parameter Lane",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                _session.Execute(ProjectDomainEditCommands.DeleteLogicalParameterLane(
                    segmentId,
                    ids[0],
                    deletionConfirmed: true));
            }
            return;
        }
        MidoraId[] notes = location.Value.Segment.Notes
            .ResolveByIdsInCollectionOrder(selected)
            .Select(static item => item.Id)
            .ToArray();
        if (notes.Length == ids.Length)
        {
            _session.Execute(ProjectDomainEditCommands.DeleteLogicalNotes(segmentId, notes));
            return;
        }
        LogicalParameterLane? selectedLane = null;
        IReadOnlyList<CurvePoint>? selectedPoints = null;
        foreach (LogicalParameterLane lane in location.Value.Segment.ParameterLanes)
        {
            IReadOnlyList<CurvePoint> matches =
                lane.Points.ResolveByIdsInCollectionOrder(selected);
            if (matches.Count == 0) continue;
            if (selectedLane is not null)
            {
                selectedLane = null;
                selectedPoints = null;
                break;
            }
            selectedLane = lane;
            selectedPoints = matches;
        }
        if (selectedLane is not null && selectedPoints?.Count == ids.Length)
        {
            _session.Execute(ProjectDomainEditCommands.DeleteLogicalParameterPoints(
                segmentId,
                selectedLane.Id,
                ids));
            return;
        }
        throw new InvalidOperationException(
            "A single delete gesture may target Logical Notes or points from one Logical Parameter Lane, not a mixed selection.");
    }

    private void DeleteInstrumentSelection(InstrumentWorkspaceViewModel workspace, MidoraId[] ids)
    {
        if (workspace.ObjectId is not MidoraId instrumentId) return;
        EventInstrument instrument = _session.Project!.EventInstruments.Single(item => item.Id == instrumentId);
        if (ids.Length == 1)
        {
            MidoraId id = ids[0];
            if (instrument.SubVoices.Any(item => item.Id == id))
            {
                if (MessageDialog.Show(
                        this,
                        "Delete the selected SubVoice, all of its Template Events and Value Curves, and its Logical Parameter Mappings?",
                        "Delete SubVoice",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    _session.Execute(ProjectDomainEditCommands.DeleteSubVoice(instrumentId, id, nonEmptyDeletionConfirmed: true));
                }
                return;
            }
            if (instrument.LogicalParameters.Any(item => item.Id == id))
            {
                if (MessageDialog.Show(
                        this,
                        "Delete the selected Logical Parameter and all references to it?",
                        "Delete Logical Parameter",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    _session.Execute(ProjectDomainEditCommands.DeleteLogicalParameter(instrumentId, id, referencedDeletionConfirmed: true));
                }
                return;
            }
            if (instrument.MappingFunctions.Any(item => item.Id == id))
            {
                if (MessageDialog.Show(
                        this,
                        "Delete the selected Mapping Function? Existing Mapping Steps that reference it may also be affected.",
                        "Delete Mapping Function",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    _session.Execute(ProjectDomainEditCommands.DeleteMappingFunction(instrumentId, id, referencedDeletionConfirmed: true));
                }
                return;
            }
            if (instrument.ParameterMappings.Any(item => item.Id == id))
            {
                if (MessageDialog.Show(
                        this,
                        "Delete the selected Logical Parameter Mapping and its ordered Mapping Chain?",
                        "Delete Logical Parameter Mapping",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    _session.Execute(ProjectDomainEditCommands.DeleteLogicalParameterMapping(
                        instrumentId, id, deletionConfirmed: true));
                }
                return;
            }
            if (instrument.Envelopes.Any(item => item.Id == id))
            {
                if (MessageDialog.Show(
                        this,
                        "Delete the selected Envelope Preset? Mapping Steps that reference it will retain an unavailable reference.",
                        "Delete Envelope Preset",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    _session.Execute(ProjectDomainEditCommands.DeleteInstrumentEnvelope(
                        instrumentId, id, referencedDeletionConfirmed: true));
                }
                return;
            }
            MappingChainListItem? selectedChain = workspace.MappingChains
                .FirstOrDefault(item => item.Id == id);
            if (selectedChain is not null)
            {
                if (!selectedChain.CanDelete) return;
                bool nonEmpty = selectedChain.StepCount != 0;
                if (nonEmpty
                    && MessageDialog.Show(
                        this,
                        $"Delete Mapping Chain '{selectedChain.Owner}' and its {selectedChain.StepCount} step(s)?",
                        "Delete Mapping Chain",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    return;
                }
                _session.Execute(ProjectDomainEditCommands.DeleteMappingChain(
                    instrumentId,
                    id,
                    nonEmptyDeletionConfirmed: nonEmpty));
                return;
            }
            foreach (MappingChain chain in EnumerateInstrumentMappingChains(instrument))
            {
                if (chain.FirstOrDefault(item => item.Id == id) is not ValueMappingStep step) continue;
                _session.Execute(ProjectDomainEditCommands.DeleteMappingStep(
                    instrumentId,
                    chain.Id,
                    step.Id));
                return;
            }
        }
        HashSet<MidoraId> selected = ids.ToHashSet();
        foreach (SubVoice candidateVoice in instrument.SubVoices)
        {
            ValueCurve? curve = null;
            foreach (ValueCurve candidate in candidateVoice.Curves)
            {
                IReadOnlyList<CurvePoint> matches =
                    candidate.Points.ResolveByIdsInCollectionOrder(selected);
                if (matches.Count == 0) continue;
                if (curve is not null || matches.Count != ids.Length)
                {
                    curve = null;
                    break;
                }
                curve = candidate;
            }
            if (curve is not null)
            {
                _session.Execute(ProjectDomainEditCommands.DeleteValueCurvePoints(
                    instrumentId,
                    candidateVoice.Id,
                    curve.Id,
                    ids));
                return;
            }
        }
        SubVoice? selectedVoice = null;
        IReadOnlyList<TemplateEvent>? selectedEvents = null;
        foreach (SubVoice voice in instrument.SubVoices)
        {
            IReadOnlyList<TemplateEvent> matches =
                voice.Events.ResolveByIdsInCollectionOrder(selected);
            if (matches.Count == 0) continue;
            if (selectedVoice is not null)
            {
                selectedVoice = null;
                selectedEvents = null;
                break;
            }
            selectedVoice = voice;
            selectedEvents = matches;
        }
        if (selectedVoice is null || selectedEvents?.Count != ids.Length)
        {
            throw new InvalidOperationException(
                "A single delete gesture may target Template Events from one SubVoice only.");
        }
        _session.Execute(ProjectDomainEditCommands.DeleteTemplateEvents(
            instrumentId,
            selectedVoice.Id,
            ids));
    }

    private static IEnumerable<MappingChain> EnumerateInstrumentMappingChains(EventInstrument instrument) =>
        instrument.ParameterMappings.Select(item => item.Steps)
            .Concat(instrument.SubVoices
                .SelectMany(voice => voice.EventMappings)
                .Select(item => item.Steps));

    private static bool IsTextEditingFocus()
    {
        DependencyObject? focused = Keyboard.FocusedElement as DependencyObject;
        return focused is TextBoxBase or PasswordBox or ComboBox
               || FindVisualAncestor<MappingFunctionCodeEditor>(focused) is not null;
    }

    private void OnKeyboardFocusChangedForInputMethod(
        object sender,
        KeyboardFocusChangedEventArgs e) =>
        ApplyInputMethodPolicy(e.NewFocus as DependencyObject);

    internal static void ApplyInputMethodPolicy(DependencyObject? focused)
    {
        if (focused is null) return;
        InputMethod.SetIsInputMethodEnabled(focused, IsInputMethodTextTarget(focused));
    }

    internal static bool IsInputMethodTextTarget(DependencyObject? focused)
    {
        for (DependencyObject? current = focused; current is not null; current = GetUiParent(current))
        {
            if (current is TextBoxBase or PasswordBox)
            {
                return true;
            }
            if (current is MappingFunctionCodeEditor)
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

    private static bool IsPlaybackShortcutInputFocus()
    {
        DependencyObject? focused = Keyboard.FocusedElement as DependencyObject;
        if (focused is TextBoxBase or PasswordBox)
        {
            return true;
        }
        if (FindVisualAncestor<MappingFunctionCodeEditor>(focused) is not null)
        {
            return true;
        }

        if (focused is ComboBox { IsDropDownOpen: true })
        {
            return true;
        }

        ComboBoxItem? item = focused as ComboBoxItem;
        if (item is null && focused is Visual or Visual3D)
        {
            item = FindVisualAncestor<ComboBoxItem>(focused);
        }
        return item is not null
            && ItemsControl.ItemsControlFromItemContainer(item) is ComboBox { IsDropDownOpen: true };
    }

    private void OnPreviewMouseDownForPlaybackShortcut(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;
        if (_session.ActiveWorkspace is InstrumentWorkspaceViewModel
            && (e.ChangedButton == MouseButton.Right
                || e.ClickCount > 1
                || IsPreviewPriorityPointerTarget(source, PrimaryTransportButton)))
        {
            _ = PrepareForModalSurface();
        }
        TimelineSurface? surface = FindVisualAncestor<TimelineSurface>(source);
        if (surface is { SurfaceMode: TimelineSurfaceMode.Arrangement }
            && _session.ActiveWorkspace is TimelineWorkspaceViewModel
            { Mode: TimelineWorkspaceMode.Arrangement } arrangementWorkspace)
        {
            Point point = e.GetPosition(surface);
            if (surface.IsArrangementEmptyBackground(point))
            {
                ClearArrangementTrackSelection(arrangementWorkspace);
            }
        }
        if (_spaceStartedPlayback
            && (FindVisualAncestor<TextBoxBase>(source) is not null
                || FindVisualAncestor<PasswordBox>(source) is not null
                || FindVisualAncestor<ComboBox>(source) is not null
                || FindVisualAncestor<MappingFunctionCodeEditor>(source) is not null))
        {
            _spaceStartedPlayback = false;
        }
    }

    internal static bool IsPreviewPriorityPointerTarget(
        DependencyObject? source,
        ButtonBase? primaryTransportButton)
    {
        ButtonBase? button = FindVisualAncestor<ButtonBase>(source);
        return (button is not null && !ReferenceEquals(button, primaryTransportButton))
            || FindVisualAncestor<TabItem>(source) is not null
            || FindVisualAncestor<ComboBox>(source) is not null
            || FindVisualAncestor<MenuItem>(source) is not null;
    }

    private bool IsLogicalTrackShortcutContext() =>
        ProjectTree.IsKeyboardFocusWithin
        || _logicalTrackShortcutTrackId is MidoraId trackId
        && _session.Project?.Tracks.Any(value => value.Id == trackId) == true
        && GetFocusedTimelineSurface() is { SurfaceMode: TimelineSurfaceMode.Arrangement }
        && _session.ActiveWorkspace is TimelineWorkspaceViewModel
        { Mode: TimelineWorkspaceMode.Arrangement };

    private bool IsArrangementHeaderShortcutContext() =>
        _arrangementHeaderShortcut is { } shortcut
        && ArrangementShortcutTargetExists(shortcut)
        && GetFocusedTimelineSurface() is { SurfaceMode: TimelineSurfaceMode.Arrangement }
        && _session.ActiveWorkspace is TimelineWorkspaceViewModel
        { Mode: TimelineWorkspaceMode.Arrangement };

    private bool ArrangementShortcutTargetExists((ArrangementLaneKind Kind, MidoraId Id) shortcut) =>
        _session.Project is MidoraProject project
        && (shortcut.Kind switch
        {
            ArrangementLaneKind.LogicalTrack => project.Tracks.Any(value => value.Id == shortcut.Id),
            ArrangementLaneKind.PureMidiTrack => project.PureMidiTracks.Any(value => value.Id == shortcut.Id),
            _ => false
        });

    private void SelectArrangementTrack(
        TimelineWorkspaceViewModel workspace,
        ArrangementLaneDescriptor descriptor)
    {
        bool conductor = descriptor.Kind == ArrangementLaneKind.Conductor;
        bool selectableObject = descriptor.Kind is ArrangementLaneKind.LogicalTrack
            or ArrangementLaneKind.PureMidiTrack
            or ArrangementLaneKind.DamagedLogicalTrack
            or ArrangementLaneKind.DamagedPureMidiTrack;
        if (!conductor && (!selectableObject || descriptor.ObjectId is not MidoraId)) return;

        workspace.ActiveLane = descriptor.Lane;
        workspace.IsConductorTrackSelected = conductor;
        workspace.SelectedArrangementTrackId = conductor ? null : descriptor.ObjectId;
        if (descriptor.ObjectId is MidoraId objectId
            && descriptor.Kind is ArrangementLaneKind.LogicalTrack or ArrangementLaneKind.PureMidiTrack)
        {
            _arrangementHeaderShortcut = (descriptor.Kind, objectId);
            _logicalTrackShortcutTrackId = descriptor.Kind == ArrangementLaneKind.LogicalTrack
                ? objectId
                : null;
        }
        else
        {
            _arrangementHeaderShortcut = null;
            _logicalTrackShortcutTrackId = null;
        }
    }

    private void ClearArrangementTrackSelection(TimelineWorkspaceViewModel workspace)
    {
        workspace.SelectedArrangementTrackId = null;
        workspace.IsConductorTrackSelected = false;
        workspace.ActiveLane = null;
        _trackHeaderContextLane = null;
        _arrangementSharedGroupContextId = null;
        _logicalTrackShortcutTrackId = null;
        _arrangementHeaderShortcut = null;
    }

    private static TimelineSurface? GetFocusedTimelineSurface() =>
        FindVisualAncestor<TimelineSurface>(Keyboard.FocusedElement as DependencyObject);

    private void RestorePlaybackShortcutFocus()
    {
        TimelineSurface? timeline = FindDescendant<TimelineSurface>(
            WorkspaceTabs,
            candidate => candidate.IsVisible && candidate.IsEnabled);
        if (timeline?.Focus() != true)
        {
            WorkspaceTabs.Focus();
        }
    }

    private bool IsTransientInputSurfaceOpen() =>
        NewProjectItemPopup.IsOpen
        || MainMenu.Items.OfType<MenuItem>().Any(IsOpenMenuBranch);

    private static bool IsOpenMenuBranch(MenuItem item) =>
        item.IsSubmenuOpen
        || item.Items.OfType<MenuItem>().Any(IsOpenMenuBranch);

    private static bool IsTimelineInteractionFocus()
    {
        DependencyObject? current = Keyboard.FocusedElement as DependencyObject;
        while (current is not null)
        {
            if (current is TimelineSurface)
            {
                return true;
            }
            current = GetUiParent(current);
        }
        return false;
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;
        if (IsInteractiveTitleBarSource(source))
        {
            return;
        }
        if (e.ClickCount == 2) { ToggleMaximize(); return; }
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnTitleBarMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveTitleBarSource(e.OriginalSource as DependencyObject)) return;
        SystemCommands.ShowSystemMenu(this, PointToScreen(e.GetPosition(this)));
    }

    private static bool IsInteractiveTitleBarSource(DependencyObject? source) =>
        FindVisualAncestor<Menu>(source) is not null
        || FindVisualAncestor<ButtonBase>(source) is not null;

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void OnMaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        WindowChrome? chrome = WindowChrome.GetWindowChrome(this);
        if (chrome is not null) chrome.ResizeBorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(6);
        MaximizeGlyph.Data = (Geometry)FindResource(WindowState == WindowState.Maximized ? "WindowControl.Restore" : "WindowControl.Maximize");
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowSource = (HwndSource?)PresentationSource.FromVisual(this);
        _windowSource?.AddHook(OnWindowMessage);
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmGetMinMaxInfo && ApplyMonitorWorkArea(hwnd, lParam, MinWidth, MinHeight)) handled = true;
        return IntPtr.Zero;
    }

    private static bool ApplyMonitorWorkArea(IntPtr hwnd, IntPtr pointer, double minWidth, double minHeight)
    {
        IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return false;
        MonitorInfo info = new() { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;
        MinMaxInfo limits = Marshal.PtrToStructure<MinMaxInfo>(pointer);
        limits.MaxPosition.X = info.WorkArea.Left - info.MonitorBounds.Left;
        limits.MaxPosition.Y = info.WorkArea.Top - info.MonitorBounds.Top;
        limits.MaxSize.X = info.WorkArea.Right - info.WorkArea.Left;
        limits.MaxSize.Y = info.WorkArea.Bottom - info.WorkArea.Top;
        limits.MaxTrackSize = limits.MaxSize;
        double scale = Math.Max(1, GetDpiForWindow(hwnd)) / 96d;
        limits.MinTrackSize.X = Math.Max(limits.MinTrackSize.X, (int)Math.Ceiling(minWidth * scale));
        limits.MinTrackSize.Y = Math.Max(limits.MinTrackSize.Y, (int)Math.Ceiling(minHeight * scale));
        Marshal.StructureToPtr(limits, pointer, false);
        return true;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public NativePoint Reserved; public NativePoint MaxSize; public NativePoint MaxPosition; public NativePoint MinTrackSize; public NativePoint MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRectangle { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)] private struct MonitorInfo { public int Size; public NativeRectangle MonitorBounds; public NativeRectangle WorkArea; public uint Flags; }
}
