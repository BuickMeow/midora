using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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
    private const string ProjectTreeDragFormat = "Midora.ProjectTreeNode";
    private const string WorkspaceTabDragFormat = "Midora.WorkspaceTab";
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

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _session;
        SourceInitialized += OnSourceInitialized;
        StateChanged += OnWindowStateChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += OnPreviewMouseDownForPlaybackShortcut;
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
        if (await RunOperationAsync("Open Project", () => _session.OpenProjectAsync(candidate)))
        {
            RecordRecentDirectory(RecentDirectoryPurpose.OpenProject, Path.GetDirectoryName(candidate));
            RecordRecentProject(candidate);
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
        await _session.DisposeAsync();
        SaveDesktopPreferences();
        base.OnClosed(e);
    }

    private async void OnNewProjectClick(object sender, RoutedEventArgs e)
    {
        if (!StopPlaybackForProjectCommand("New Project") || !await ConfirmCloseCurrentProjectAsync()) return;
        NewProjectDialog dialog = new(ExistingRecentDirectory(RecentDirectoryPurpose.SaveAndSaveCopy)) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Request is null) return;
        if (await RunOperationAsync("Create Project", () => _session.CreateProjectAsync(dialog.Request))
            && dialog.Request.TargetPath is string targetPath)
        {
            RecordRecentDirectory(RecentDirectoryPurpose.SaveAndSaveCopy, Path.GetDirectoryName(targetPath));
            RecordRecentProject(targetPath);
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
        if (await RunOperationAsync("Open Project", () => _session.OpenProjectAsync(dialog.FileName)))
        {
            RecordRecentDirectory(RecentDirectoryPurpose.OpenProject, Path.GetDirectoryName(dialog.FileName));
            RecordRecentProject(dialog.FileName);
        }
    }

    private async void OnSaveProjectClick(object sender, RoutedEventArgs e) => await SaveProjectAsync();

    private async Task<bool> SaveProjectAsync()
    {
        if (!_session.HasProject) return true;
        if (_session.HasDamagedProjectObjects)
        {
            _session.Notice = "Saving is disabled while damaged Event Instrument or Logical Track placeholders remain. Delete every damaged placeholder first, or discard this Project session.";
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
            _session.Notice = "Save Copy is disabled while damaged Event Instrument or Logical Track placeholders remain.";
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

    private void OnApplicationPreferencesClick(object sender, RoutedEventArgs e)
    {
        if (!_session.CanStartForegroundTask)
        {
            _session.SetStatusMessage(
                "Stop playback and wait for the current foreground task before changing Application Preferences.",
                isError: true);
            return;
        }
        ApplicationPreferencesDialog dialog = new(_preferences) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;

        ApplicationPreferencesSaveResult saved = _preferenceStore.Save(dialog.Result);
        if (!saved.Succeeded)
        {
            ShowError(
                "Application Preferences",
                saved.Notice?.Message ?? "Application Preferences could not be saved.");
            return;
        }
        try
        {
            _session.ApplyApplicationPreferences(dialog.Result);
            _preferences = dialog.Result;
            _session.SetStatusMessage("Application Preferences were saved and applied.");
        }
        catch (Exception exception)
        {
            // Persistence already succeeded. The next Project session will still use these values.
            _preferences = dialog.Result;
            ShowError(
                "Application Preferences",
                $"Preferences were saved, but the current Project session could not apply them: {exception.Message}");
        }
    }

    private async void OnCloseProjectClick(object sender, RoutedEventArgs e)
    {
        if (StopPlaybackForProjectCommand("Close Project") && await ConfirmCloseCurrentProjectAsync()) await _session.CloseProjectAsync();
    }

    private bool StopPlaybackForProjectCommand(string command)
    {
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
        if (_session.HasUnsavedDrafts)
        {
            MessageBoxResult drafts = MessageBox.Show(
                this,
                "Apply all C# Mapping drafts before closing the current Project? Drafts are session UI state and are discarded when the Project closes.",
                "Unapplied C# Mapping Drafts",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);
            if (drafts == MessageBoxResult.Cancel) return false;
            if (drafts == MessageBoxResult.Yes)
            {
                foreach (MappingFunctionWorkspaceViewModel workspace in _session.Workspaces
                             .OfType<MappingFunctionWorkspaceViewModel>()
                             .Where(item => item.IsDirty)
                             .ToArray())
                {
                    _session.ActiveWorkspace = workspace;
                    if (!_session.ApplyMappingDraft(workspace)) return false;
                }
            }
        }
        if (!_session.HasProject
            || (_session.Document?.IsModified != true
                && _session.Persistence?.CurrentProjectPath is not null))
        {
            return true;
        }
        if (_session.HasDamagedProjectObjects)
        {
            return MessageBox.Show(
                this,
                "This Project has unsaved changes and damaged object placeholders. Saving is prohibited until every damaged placeholder is deleted. Close and discard the current session changes?",
                "Discard Unsavable Project Changes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }
        MessageBoxResult result = MessageBox.Show(
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

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        RunSynchronous("Play", () => _session.StartPlayback());
        _spaceStartedPlayback = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, RestorePlaybackShortcutFocus);
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        _spaceStartedPlayback = false;
        RunSynchronous("Stop", _session.StopPlayback);
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

    private void OnResetPlaybackClick(object sender, RoutedEventArgs e) =>
        RunSynchronous("Reset Playback Engine", _session.ResetPlaybackEngine);

    private void OnPlaybackTimerTick(object? sender, EventArgs e)
    {
        if (!_session.IsPlaybackActive) return;
        try
        {
            _session.UpdatePlayback();
            if (_preferences.DesktopUi.FollowPlayback
                && _session.ActiveWorkspace is TimelineWorkspaceViewModel timeline)
            {
                if (timeline.PlaybackCursorTick is not long tick)
                {
                    return;
                }
                long followStart = checked(timeline.StartTick + timeline.TickSpan / 10);
                long followEnd = checked(timeline.StartTick + timeline.TickSpan * 9 / 10);
                if (tick < followStart || tick > followEnd)
                {
                    timeline.StartTick = Math.Max(0, checked(tick - timeline.TickSpan / 5));
                }
            }
        }
        catch (Exception exception)
        {
            ShowError("Playback", exception.Message);
        }
    }

    private void OnNewTrackClick(object sender, RoutedEventArgs e)
    {
        RunAfterMenuClosed(sender, () =>
        {
            if (!_session.HasProject) return;
            RunSynchronous("Create Logical Track", () =>
            {
                _session.Execute(ProjectDomainEditCommands.CreateLogicalTrack());
                _session.OpenArrangement();
            });
        });
    }

    private void OnNewInstrumentFolderClick(object sender, RoutedEventArgs e)
    {
        RunAfterMenuClosed(sender, () =>
        {
            if (!_session.HasProject) return;
            string name = UniqueFolderName();
            RunSynchronous("Create Event Instrument Folder", () =>
                _session.Execute(ProjectDomainEditCommands.CreateEventInstrumentFolder(name)));
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

        // A Popup owns a separate HWND. Close it and let the current routed input
        // event unwind before an edit rebuilds ItemsSource-backed WPF collections.
        // Mutating the visual tree synchronously from the Popup button's Click route
        // can leave mouse capture and layout processing in a re-entrant state.
        NewProjectItemPopup.IsOpen = false;
        NewProjectItemButton.IsChecked = false;
        _ = Dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }

    private string UniqueFolderName()
    {
        const string basis = "New Folder";
        HashSet<string> names = _session.Project!.EventInstrumentFolders
            .Select(folder => folder.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(basis)) return basis;
        for (int suffix = 2; ; suffix++)
        {
            string candidate = $"{basis} {suffix}";
            if (!names.Contains(candidate)) return candidate;
        }
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
                or ProjectTreeNodeKind.EventInstrument
                or ProjectTreeNodeKind.InstrumentFolder))
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
        if (Math.Abs(current.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance)
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
        e.Handled = true;
        if (source is null || target is null || effect == DragDropEffects.None || _session.Project is null) return;

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
            (ProjectTreeNodeKind.InstrumentFolder, ProjectTreeNodeKind.InstrumentFolder) => DragDropEffects.Move,
            (ProjectTreeNodeKind.EventInstrument, ProjectTreeNodeKind.EventInstrument) => DragDropEffects.Move,
            (ProjectTreeNodeKind.EventInstrument, ProjectTreeNodeKind.InstrumentFolder
                or ProjectTreeNodeKind.InstrumentLibrary) => DragDropEffects.Move,
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
            throw new InvalidOperationException("The dragged Project node has no stable identity.");
        }
        switch (source.Kind, target.Kind)
        {
            case (ProjectTreeNodeKind.LogicalTrack, ProjectTreeNodeKind.LogicalTrack)
                when target.ObjectId is MidoraId targetTrackId:
                _session.ReorderProjectTreeNode(
                    source,
                    project.Tracks.FindIndex(item => item.Id == targetTrackId));
                return;
            case (ProjectTreeNodeKind.InstrumentFolder, ProjectTreeNodeKind.InstrumentFolder)
                when target.ObjectId is MidoraId targetFolderId:
                _session.ReorderProjectTreeNode(
                    source,
                    project.EventInstrumentFolders.FindIndex(item => item.Id == targetFolderId));
                return;
            case (ProjectTreeNodeKind.EventInstrument, ProjectTreeNodeKind.EventInstrument)
                when target.ObjectId is MidoraId targetInstrumentId:
                _session.ReorderProjectTreeNode(
                    source,
                    project.EventInstruments.FindIndex(item => item.Id == targetInstrumentId));
                return;
            case (ProjectTreeNodeKind.EventInstrument, ProjectTreeNodeKind.InstrumentFolder)
                when target.ObjectId is MidoraId folderId:
                _session.Execute(ProjectDomainEditCommands.MoveEventInstrumentToFolder(sourceId, folderId));
                return;
            case (ProjectTreeNodeKind.EventInstrument, ProjectTreeNodeKind.InstrumentLibrary):
                _session.Execute(ProjectDomainEditCommands.MoveEventInstrumentToFolder(sourceId, null));
                return;
            case (ProjectTreeNodeKind.EventInstrument, ProjectTreeNodeKind.LogicalTracks):
                EventInstrument instrument = project.EventInstruments.Single(item => item.Id == sourceId);
                _session.Execute(ProjectDomainEditCommands.CreateLogicalTrack(instrument.Name, sourceId));
                return;
            case (ProjectTreeNodeKind.EventInstrument, ProjectTreeNodeKind.LogicalTrack)
                when target.ObjectId is MidoraId targetTrackId:
                LogicalTrack track = project.Tracks.Single(item => item.Id == targetTrackId);
                if (track.EventInstrumentId is MidoraId currentId && currentId != sourceId)
                {
                    EventInstrument? current = project.EventInstruments.FirstOrDefault(item => item.Id == currentId);
                    EventInstrument replacement = project.EventInstruments.Single(item => item.Id == sourceId);
                    if (MessageBox.Show(
                            this,
                            $"Rebind Logical Track '{track.Name}' from '{current?.Name ?? track.LastBoundEventInstrumentName ?? currentId.ToString()}' to '{replacement.Name}'? Existing Segments and Logical Parameter lanes are preserved; incompatible references will be diagnosed and are not repaired automatically.",
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

    private static T? FindVisualAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null && current is not T)
        {
            current = VisualTreeHelper.GetParent(current);
        }
        return current as T;
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
        long firstNewStableId)
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
        if (created.Length == 0) return;
        workspace.Selection.Clear();
        foreach (MidoraId id in created) workspace.Selection.Add(id, makePrimary: false);
        _session.RefreshWorkspaceSelection(workspace);
        return;

        MidoraId[] SegmentSelection(MidoraId segmentId)
        {
            Segment? segment = TimelineWorkspaceViewModel.FindSegment(project, segmentId)?.Segment;
            if (segment is null) return [];
            MidoraId[] lanes = NewIds(segment.ParameterLanes, item => item.Id, firstNewStableId);
            if (lanes.Length != 0) return lanes;
            return NewIds(
                segment.Notes.Select(item => item.Id)
                    .Concat(segment.ParameterLanes.SelectMany(item => item.Points).Select(item => item.Id)),
                item => item,
                firstNewStableId);
        }

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
        if (Math.Abs(current.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance)
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
        SnapMenuItem.IsChecked = GetActiveEditorSettings().SnapEnabled;
        Dispatcher.BeginInvoke(() =>
        {
            if (WorkspaceTabs.ItemContainerGenerator.ContainerFromItem(WorkspaceTabs.SelectedItem)
                is TabItem selected)
            {
                selected.BringIntoView();
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
        void Add(string header, RoutedEventHandler handler, string? gesture = null)
        {
            MenuItem item = new() { Header = header, InputGestureText = gesture ?? string.Empty };
            item.Click += handler;
            menu.Items.Add(item);
        }
        void Separator() => menu.Items.Add(new Separator());

        switch (node.Kind)
        {
            case ProjectTreeNodeKind.InstrumentLibrary:
                Add("New Event Instrument", OnNewInstrumentClick);
                Add("New Event Instrument Folder", OnNewInstrumentFolderClick);
                break;
            case ProjectTreeNodeKind.LogicalTracks:
                Add("New Logical Track", OnNewTrackClick);
                break;
            case ProjectTreeNodeKind.LogicalTrack:
                Add("Open", OnTreeOpenClick);
                Add("Rename", OnTreeRenameClick, "F2");
                Add("Bind Event Instrument…", OnTreeBindInstrumentClick);
                Separator();
                Add("Move Up", OnTreeMoveUpClick);
                Add("Move Down", OnTreeMoveDownClick);
                Separator();
                Add("Delete…", OnTreeDeleteClick);
                break;
            case ProjectTreeNodeKind.EventInstrument:
            case ProjectTreeNodeKind.InstrumentFolder:
                Add("Open", OnTreeOpenClick);
                Add("Rename", OnTreeRenameClick, "F2");
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
        void Add(string header, RoutedEventHandler handler, string? gesture = null, bool enabled = true)
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

        double headerWidth = surface.SurfaceMode switch
        {
            TimelineSurfaceMode.Arrangement => 180,
            TimelineSurfaceMode.PianoRoll or TimelineSurfaceMode.EventLanes or TimelineSurfaceMode.Velocity => 52,
            TimelineSurfaceMode.Conductor => 130,
            _ => 0
        };
        bool isLaneHeader = Mouse.GetPosition(surface).X < headerWidth;
        if (isLaneHeader)
        {
            switch (surface.SurfaceMode)
            {
                case TimelineSurfaceMode.Arrangement:
                    Add("New Logical Track", OnNewTrackClick, enabled: _session.CanEditProject);
                    return;
                case TimelineSurfaceMode.EventLanes
                    when _session.ActiveWorkspace is TimelineWorkspaceViewModel
                    {
                        Mode: TimelineWorkspaceMode.Segment
                    }:
                    Add("Add Logical Parameter Lane…", OnAddParameterLaneClick, enabled: _session.CanEditProject);
                    return;
                case TimelineSurfaceMode.EventLanes
                    when _session.ActiveWorkspace is InstrumentWorkspaceViewModel:
                    Add("Add Template Event…", OnAddTemplateEventClick, enabled: _session.CanEditProject);
                    Add("Add Value Curve…", OnAddValueCurveClick, enabled: _session.CanEditProject);
                    return;
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
        Add("Set Time Range from Object Selection", OnSetTimeRangeFromObjectsClick);
        Add("Select Objects in Time Range", OnSelectObjectsInTimeRangeClick);
        Add("Clear Time Range", OnClearTimeRangeClick);
    }

    private void OnTreeRenameClick(object sender, RoutedEventArgs e) => BeginTreeRename();

    private void BeginTreeRename()
    {
        if (ProjectTree.SelectedItem is not ProjectTreeNode node
            || node.Kind is not (ProjectTreeNodeKind.LogicalTrack
                or ProjectTreeNodeKind.EventInstrument
                or ProjectTreeNodeKind.InstrumentFolder))
        {
            return;
        }
        node.EditText = node.Title;
        node.IsRenaming = true;
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
            $"Stable ID {instrument.Id}")));
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
        RunSynchronous(
            "Bind Logical Track",
            () => _session.BindLogicalTrack(trackId, instrumentId));
    }

    private void DeleteSelectedTreeNode()
    {
        if (ProjectTree.SelectedItem is not ProjectTreeNode node || _session.Project is null) return;
        string? detail = node.Kind switch
        {
            ProjectTreeNodeKind.LogicalTrack when node.ObjectId is MidoraId id =>
                $"Delete Logical Track '{node.Title}' and its {_session.Project.Tracks.Single(item => item.Id == id).Segments.Count} Segment(s)?",
            ProjectTreeNodeKind.EventInstrument when node.ObjectId is MidoraId id =>
                $"Delete Event Instrument '{node.Title}'? {_session.Project.Tracks.Count(item => item.EventInstrumentId == id)} bound Logical Track(s) will become unbound.",
            ProjectTreeNodeKind.InstrumentFolder when node.ObjectId is MidoraId id =>
                $"Delete folder '{node.Title}'? {_session.Project.EventInstruments.Count(item => item.LibraryFolderId == id)} contained Event Instrument(s) will be moved to Unfiled.",
            ProjectTreeNodeKind.DamagedEventInstrument =>
                $"Permanently remove damaged Event Instrument placeholder '{node.Title}' from the Project? Bound Logical Tracks will become unbound and retain the last known instrument name. This operation is undoable until the Project closes.",
            ProjectTreeNodeKind.DamagedLogicalTrack =>
                $"Permanently remove damaged Logical Track placeholder '{node.Title}' from the Project? This operation is undoable until the Project closes.",
            _ => null
        };
        if (detail is null) return;
        if (MessageBox.Show(this, detail, "Delete Project Object", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
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
        int bindings = project.Tracks.Count(track => track.EventInstrumentId == selected.Id);
        if (MessageBox.Show(
                this,
                $"Delete Event Instrument '{selected.Name}'? {bindings} bound Logical Track(s) will become unbound.",
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

    private void OnMoveLibraryInstrumentClick(object sender, RoutedEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || _session.ActiveWorkspace is not LibraryWorkspaceViewModel
            {
                SelectedInstrument: InstrumentListItem selected
            })
        {
            return;
        }
        List<SelectionDialogItem> options = [new("unfiled", "Unfiled", "Top-level library")];
        options.AddRange(project.EventInstrumentFolders.Select(folder =>
            new SelectionDialogItem(folder.Id, folder.Name, "Event Instrument folder")));
        SelectionDialog dialog = new(
            "Move Event Instrument",
            $"Choose a folder for '{selected.Name}'. This changes manual library organization only.",
            options) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        MidoraId? folderId = dialog.SelectedValue is MidoraId id ? id : null;
        RunSynchronous("Move Event Instrument", () => _session.Execute(
            ProjectDomainEditCommands.MoveEventInstrumentToFolder(selected.Id, folderId)));
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
        if (sender is not CheckBox { IsChecked: bool enabled }
            || _session.ActiveWorkspace is not InstrumentWorkspaceViewModel { ObjectId: MidoraId instrumentId }
            || _session.Project?.EventInstruments.FirstOrDefault(item => item.Id == instrumentId)
                is not EventInstrument instrument
            || instrument.RequiresChannelIsolation == enabled)
        {
            return;
        }
        RunSynchronous("Change Event Instrument Isolation", () => _session.Execute(
            ProjectDomainEditCommands.UpdateEventInstrumentIsolation(instrumentId, enabled)));
    }

    private void OnInstrumentLifecycleSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { Tag: string field }
            || _session.ActiveWorkspace is not InstrumentWorkspaceViewModel { ObjectId: MidoraId instrumentId }
            || _session.Project?.EventInstruments.FirstOrDefault(item => item.Id == instrumentId)
                is not EventInstrument instrument)
        {
            return;
        }
        ShortNoteLifecycle shortLifecycle = field == "Short" && ((ComboBox)sender).SelectedItem is ShortNoteLifecycle selectedShort
            ? selectedShort
            : instrument.ShortLifecycle;
        LongNoteLifecycle longLifecycle = field == "Long" && ((ComboBox)sender).SelectedItem is LongNoteLifecycle selectedLong
            ? selectedLong
            : instrument.LongLifecycle;
        if (shortLifecycle == instrument.ShortLifecycle && longLifecycle == instrument.LongLifecycle) return;
        RunSynchronous("Change Event Instrument Lifecycle", () => _session.Execute(
            ProjectDomainEditCommands.UpdateEventInstrumentLifecycle(
                instrumentId,
                shortLifecycle,
                longLifecycle)));
    }

    private void OnInstrumentOverlapSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { Tag: string field }
            || _session.ActiveWorkspace is not InstrumentWorkspaceViewModel { ObjectId: MidoraId instrumentId }
            || _session.Project?.EventInstruments.FirstOrDefault(item => item.Id == instrumentId)
                is not EventInstrument instrument)
        {
            return;
        }
        OverlapPolicy policy = field == "Policy" && ((ComboBox)sender).SelectedItem is OverlapPolicy selectedPolicy
            ? selectedPolicy
            : instrument.OverlapPolicy;
        OverlapScope scope = field == "Scope" && ((ComboBox)sender).SelectedItem is OverlapScope selectedScope
            ? selectedScope
            : instrument.OverlapScope;
        if (policy == instrument.OverlapPolicy && scope == instrument.OverlapScope) return;
        RunSynchronous("Change Event Instrument Overlap", () => _session.Execute(
            ProjectDomainEditCommands.UpdateEventInstrumentOverlap(instrumentId, policy, scope)));
    }

    private void OnApplyInstrumentLoopClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId
            } workspace)
        {
            return;
        }
        RunSynchronous("Change Event Instrument Loop", () =>
        {
            long? start = ParseOptionalTick(workspace.LoopStartText, "Loop Start");
            long? end = ParseOptionalTick(workspace.LoopEndText, "Loop End");
            _session.Execute(ProjectDomainEditCommands.UpdateEventInstrumentLoop(instrumentId, start, end));
        });
    }

    private void OnFindMappingClick(object sender, RoutedEventArgs e) => FindNextInMappingEditor();

    private void OnMappingEditorSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: MappingFunctionWorkspaceViewModel workspace } editor)
        {
            return;
        }
        int lineIndex = editor.GetLineIndexFromCharacterIndex(editor.CaretIndex);
        int lineStart = lineIndex >= 0 ? editor.GetCharacterIndexFromLineIndex(lineIndex) : 0;
        workspace.CaretStatus = $"Ln {Math.Max(0, lineIndex) + 1}, Col {editor.CaretIndex - Math.Max(0, lineStart) + 1}";
    }

    private void FocusMappingFind()
    {
        if (FindWorkspaceElement<TextBox>("MappingFind") is TextBox find)
        {
            find.Focus();
            find.SelectAll();
        }
    }

    private void FindNextInMappingEditor()
    {
        if (_session.ActiveWorkspace is not MappingFunctionWorkspaceViewModel workspace
            || FindWorkspaceElement<TextBox>("MappingEditor") is not TextBox editor)
        {
            return;
        }
        string needle = workspace.FindText;
        if (needle.Length == 0)
        {
            workspace.FindStatus = "Enter text to find.";
            FocusMappingFind();
            return;
        }
        int start = Math.Clamp(editor.SelectionStart + editor.SelectionLength, 0, editor.Text.Length);
        int index = editor.Text.IndexOf(needle, start, StringComparison.OrdinalIgnoreCase);
        bool wrapped = false;
        if (index < 0 && start > 0)
        {
            index = editor.Text.IndexOf(needle, 0, start, StringComparison.OrdinalIgnoreCase);
            wrapped = index >= 0;
        }
        if (index < 0)
        {
            workspace.FindStatus = "No match.";
            return;
        }
        editor.Focus();
        editor.Select(index, needle.Length);
        editor.ScrollToLine(Math.Max(0, editor.GetLineIndexFromCharacterIndex(index)));
        workspace.FindStatus = wrapped ? "Wrapped to the first match." : "Match selected.";
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
            MidoraId? selectedSubVoice = sender is FrameworkElement { Tag: "ActiveSubVoice" }
                ? workspace.ActiveSubVoiceId
                : workspace.Selection.Primary;
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
        if (sender is not FrameworkElement { DataContext: InstrumentWorkspaceViewModel { ObjectId: MidoraId instrumentId } }
            || _session.Project is null) return;
        EventInstrument instrument = _session.Project.EventInstruments.Single(item => item.Id == instrumentId);
        SelectionDialog typeDialog = new(
            "Create Logical Parameter",
            "Select the formal input type. Initial legal/display ranges use safe defaults and remain editable in Inspector.",
            Enum.GetValues<LogicalParameterType>().Select(type => new SelectionDialogItem(type, type.ToString()))) { Owner = this };
        if (typeDialog.ShowDialog() != true || typeDialog.SelectedValue is not LogicalParameterType type) return;
        string name = UniqueName("Parameter", instrument.LogicalParameters.Select(item => item.Name));
        double maximum = type == LogicalParameterType.Double ? 1 : 127;
        InstrumentWorkspaceViewModel workspace = (InstrumentWorkspaceViewModel)_session.ActiveWorkspace!;
        RunSynchronous("Create Logical Parameter", () => ExecuteAndSelectCreated(
            ProjectDomainEditCommands.CreateLogicalParameter(
                instrumentId, name, type,
                minimum: 0, maximum: maximum, displayMinimum: 0, displayMaximum: maximum, defaultValue: 0), workspace));
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
                dialog.EnumSemanticWarningAcknowledged)));
    }

    private void OnAddMappingFunctionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: InstrumentWorkspaceViewModel { ObjectId: MidoraId instrumentId } }
            || _session.Project is null) return;
        EventInstrument instrument = _session.Project.EventInstruments.Single(item => item.Id == instrumentId);
        HashSet<MidoraId> before = instrument.MappingFunctions.Select(item => item.Id).ToHashSet();
        string name = UniqueName("Mapping", instrument.MappingFunctions.Select(item => item.Name));
        RunSynchronous("Create C# Mapping Function", () =>
        {
            _session.Execute(ProjectDomainEditCommands.CreateMappingFunction(
                instrumentId, name, "return value;", Array.Empty<string>()));
            CSharpMappingFunction created = instrument.MappingFunctions.Single(item => !before.Contains(item.Id));
            _session.OpenMappingFunction(instrumentId, created.Id);
        });
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
                () => RunSynchronous(
                    "Open C# Mapping Function",
                    () => _session.OpenMappingFunction(instrumentId, function.Id)),
                DispatcherPriority.Normal);
        }
    }

    private void OnInstrumentStructureSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_session.ActiveWorkspace is not InstrumentWorkspaceViewModel workspace
            || sender is not ListBox list
            || list.SelectedItem is null)
        {
            return;
        }
        MidoraId? id = list.SelectedItem switch
        {
            SubVoiceListItem item => item.Id,
            LogicalParameterListItem item => item.Id,
            MappingFunctionListItem item => item.Id,
            ParameterMappingListItem item => item.Id,
            MappingChainListItem item => item.Id,
            MappingStepListItem item => item.Id,
            EnvelopeListItem item => item.Id,
            _ => null
        };
        if (id is MidoraId selected)
        {
            _session.SelectWorkspaceObject(workspace, selected);
        }
    }

    private void OnCompileMappingDraftClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MappingFunctionWorkspaceViewModel workspace }) return;
        RunSynchronous("Compile C# Mapping Draft", () => _session.CompileMappingDraft(workspace));
    }

    private void OnApplyMappingDraftClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MappingFunctionWorkspaceViewModel workspace }) return;
        RunSynchronous("Apply C# Mapping Draft", () => _session.ApplyMappingDraft(workspace));
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

    private void OnToggleInspectorClick(object sender, RoutedEventArgs e)
    {
        _preferences = _preferences with
        {
            DesktopUi = _preferences.DesktopUi with { InspectorVisible = InspectorMenuItem.IsChecked }
        };
        ApplyPanelVisibility();
        SaveDesktopPreferences();
    }

    private void OnToggleBottomPanelClick(object sender, RoutedEventArgs e)
    {
        _preferences = _preferences with
        {
            DesktopUi = _preferences.DesktopUi with { BottomPanelVisible = BottomPanelMenuItem.IsChecked }
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
        _preferences = _preferences with
        {
            DesktopUi = _preferences.DesktopUi with { FollowPlayback = FollowPlaybackMenuItem.IsChecked }
        };
        SaveDesktopPreferences();
    }

    private void OnResetLayoutClick(object sender, RoutedEventArgs e)
    {
        DesktopUiPreferences defaults = DesktopUiPreferences.Default;
        _preferences = _preferences with
        {
            DesktopUi = _preferences.DesktopUi with
            {
                ProjectPanelWidth = defaults.ProjectPanelWidth,
                InspectorWidth = defaults.InspectorWidth,
                BottomPanelHeight = defaults.BottomPanelHeight,
                ProjectPanelVisible = true,
                InspectorVisible = true,
                BottomPanelVisible = true
            }
        };
        ProjectPanelColumn.Width = new(defaults.ProjectPanelWidth);
        InspectorColumn.Width = new(defaults.InspectorWidth);
        BottomPanelRow.Height = new(defaults.BottomPanelHeight);
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
        if (MessageBox.Show(
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
            if (workspace is MappingFunctionWorkspaceViewModel { IsDirty: true } mapping)
            {
                MessageBoxResult result = MessageBox.Show(
                    this,
                    "Apply this C# Mapping draft before closing the Workspace?",
                    "Unapplied C# Mapping Draft",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning);
                if (result == MessageBoxResult.Cancel) return;
                if (result == MessageBoxResult.Yes && !_session.ApplyMappingDraft(mapping)) return;
            }
            _session.CloseWorkspace(workspace);
            e.Handled = true;
        }
    }

    private void OnCloseOtherWorkspacesClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: WorkspaceViewModel keep }) return;
        foreach (WorkspaceViewModel workspace in _session.Workspaces.Where(item => !ReferenceEquals(item, keep)).ToArray())
        {
            if (workspace is MappingFunctionWorkspaceViewModel { IsDirty: true })
            {
                _session.Notice = "Close each dirty C# Mapping draft individually so its Apply decision is explicit.";
                continue;
            }
            _session.CloseWorkspace(workspace);
        }
        _session.ActiveWorkspace = keep;
    }

    private void OnTimelineItemInvoked(object? sender, TimelineItemEventArgs e)
    {
        if (_session.ActiveWorkspace is not WorkspaceViewModel workspace) return;
        workspace.ActiveLane = e.Item.Lane;
        if (e.IsCopyDragStart) workspace.Selection.Add(e.Item.Id);
        else if ((e.Modifiers & ModifierKeys.Control) != 0) workspace.Selection.Toggle(e.Item.Id);
        else if ((e.Modifiers & ModifierKeys.Shift) != 0) workspace.Selection.Add(e.Item.Id);
        else if (workspace.Selection.Ids.Contains(e.Item.Id)) workspace.Selection.Add(e.Item.Id);
        else workspace.Selection.Replace(e.Item.Id);
        _session.RefreshWorkspaceSelection(workspace);
        if (sender is TimelineSurface { ToolMode: TimelineToolMode.Draw }
            && e.Item.Kind is TimelineItemKind.LogicalNote or TimelineItemKind.TemplateNote)
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
            _session.OpenMappingFunction(instrumentId, functionId);
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

    private void OnSelectActiveSubVoiceClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement
            {
                DataContext: InstrumentWorkspaceViewModel
                {
                    ActiveSubVoiceId: MidoraId subVoiceId
                } workspace
            })
        {
            workspace.Selection.Replace(subVoiceId);
            _session.RefreshWorkspace(workspace);
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

    private void OnSubdivisionComboBoxLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not ComboBox { Tag: string role } comboBox) return;
        TimelineEditorSettings? settings = comboBox.DataContext switch
        {
            TimelineWorkspaceViewModel timeline => timeline.EditorSettings,
            InstrumentWorkspaceViewModel instrument => instrument.EditorSettings,
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
            }
            || (uint)e.Lane >= (uint)project.Tracks.Count)
        {
            return;
        }
        MidoraId trackId = project.Tracks[e.Lane].Id;
        RunSynchronous(
            e.Command == TimelineLaneHeaderCommand.ToggleMute ? "Toggle Track Mute" : "Toggle Track Solo",
            () =>
            {
                if (e.Command == TimelineLaneHeaderCommand.ToggleMute)
                {
                    _session.SetTrackMuted(trackId, !_session.IsTrackMuted(trackId));
                }
                else
                {
                    _session.SetTrackSolo(trackId, !_session.IsTrackSolo(trackId));
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
        workspace.ActiveLane = e.Lane;
        if (sender is TimelineSurface { Tag: "ParameterLanes" }
            && workspace is TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Segment,
                ObjectId: MidoraId segmentId
            } timelineWorkspace
            && TimelineWorkspaceViewModel.FindSegment(project, segmentId) is { } located
            && (uint)timelineWorkspace.ActiveParameterLaneIndex < (uint)located.Segment.ParameterLanes.Count)
        {
            workspace.Selection.Replace(located.Segment.ParameterLanes[timelineWorkspace.ActiveParameterLaneIndex].Id);
            _session.RefreshWorkspace(workspace);
            return;
        }
        if (workspace is InstrumentWorkspaceViewModel instrumentWorkspace
            && instrumentWorkspace.GetRenderLane(e.Lane) is InstrumentRenderLane lane)
        {
            workspace.Selection.Replace(lane.ValueCurveId ?? lane.SubVoiceId);
            _session.RefreshWorkspace(workspace);
        }
    }

    private void OnTimelineLanePreviewPressed(object? sender, TimelineLanePreviewEventArgs e)
    {
        if (sender is TimelineSurface { Tag: "SubVoiceNotes", DataContext: InstrumentWorkspaceViewModel workspace }
            && workspace.ObjectId is MidoraId instrumentId
            && workspace.ActiveSubVoiceId is MidoraId subVoiceId)
        {
            RunSynchronous("Start SubVoice Pitch Preview", () => _session.StartHeldEventInstrumentPreview(
                new EventInstrumentPreviewRequest(
                    instrumentId,
                    subVoiceId,
                    e.Pitch,
                    e.Velocity,
                    GateLengthTicks: null,
                    CursorTick: _session.CurrentTick)
                {
                    DirectSubVoicePitchPreview = true
                }));
            return;
        }
        if (_session.Project is not MidoraProject project
            || _session.ActiveWorkspace is not TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Segment,
                ObjectId: MidoraId segmentId
            })
        {
            return;
        }
        (LogicalTrack Track, Segment Segment)? located = TimelineWorkspaceViewModel.FindSegment(project, segmentId);
        if (located is null) return;
        decimal tempo = project.Conductor.Tempos
            .Where(item => item.Tick <= _session.CurrentTick)
            .OrderByDescending(item => item.Tick)
            .Select(item => item.BeatsPerMinute)
            .FirstOrDefault(120m);
        RunSynchronous("Start Pitch Ruler Preview", () => _session.StartHeldSegmentPitchRulerPreview(
            located.Value.Track.Id,
            segmentId,
            e.Pitch,
            e.Velocity,
            tempo));
    }

    private void OnActiveEditorLaneDropDownClosed(object sender, EventArgs e)
    {
        if (_session.ActiveWorkspace is WorkspaceViewModel workspace)
        {
            _session.RefreshWorkspace(workspace);
        }
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
            RunSynchronous("Paint Note Velocities", () =>
                _session.Execute(ProjectDomainEditCommands.PaintLogicalNoteVelocities(segmentId, e.Velocities)));
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
        RunSynchronous("End Pitch Ruler Preview", _session.EndHeldPreviewGate);

    private void OnTimelineNotePlacementStarted(object? sender, TimelineNotePlacementEventArgs e)
    {
        if (sender is TimelineSurface { Tag: "SubVoiceNotes", DataContext: InstrumentWorkspaceViewModel instrumentWorkspace }
            && instrumentWorkspace.ObjectId is MidoraId instrumentId
            && instrumentWorkspace.ActiveSubVoiceId is MidoraId subVoiceId)
        {
            long templateStart = instrumentWorkspace.EditorSettings.SnapAbsolute(e.StartTick);
            _templateNotePlacement = (instrumentId, subVoiceId, templateStart, e.Pitch, e.Velocity);
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
        (LogicalTrack Track, Segment Segment)? located = TimelineWorkspaceViewModel.FindSegment(project, segmentId);
        if (located is null) return;
        long start = workspace.EditorSettings.SnapAbsolute(e.StartTick);
        _notePlacement = (segmentId, start, e.Pitch, e.Velocity);
    }

    private void OnTimelineNotePlacementCompleted(object? sender, TimelineNotePlacementEventArgs e)
    {
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
                    templatePlacement.Pitch,
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
        RunSynchronous("Place Logical Note", () => ExecuteAndSelectCreated(
            ProjectDomainEditCommands.CreateLogicalNote(
                placement.SegmentId,
                placement.StartTick,
                length,
                placement.Pitch,
                placement.Velocity),
            workspace));
    }

    private void OnTimelineNotePlacementCancelled(object? sender, EventArgs e)
    {
        _notePlacement = null;
        _templateNotePlacement = null;
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
        workspace.Selection.Replace(row.Id);
        workspace.EditCursorTick = row.Tick;
        if (row.Tick < workspace.StartTick || row.Tick >= workspace.StartTick + workspace.TickSpan)
        {
            workspace.StartTick = Math.Max(0, row.Tick - workspace.TickSpan / 4);
        }
        _session.RefreshWorkspaceSelection(workspace);
    }

    private void OnInspectorFieldLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox textBox) CommitInspectorField(textBox, restoreOnFailure: true);
    }

    private void OnInspectorFieldKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox) return;
        if (e.Key == Key.Enter)
        {
            CommitInspectorField(textBox, restoreOnFailure: false);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            _session.Inspector.ErrorText = null;
            e.Handled = true;
        }
    }

    private void OnInspectorChoiceDropDownClosed(object sender, EventArgs e)
    {
        if (sender is not ComboBox { Tag: InspectorField field } || !field.IsEditable) return;
        try
        {
            _session.ApplyInspectorField(field);
            _session.Inspector.ErrorText = null;
        }
        catch (Exception exception)
        {
            _session.Inspector.ErrorText = exception.Message;
        }
    }

    private void CommitInspectorField(TextBox textBox, bool restoreOnFailure)
    {
        if (textBox.IsReadOnly
            || textBox.Tag is not InspectorField field
            || !_session.Inspector.Fields.Contains(field))
        {
            return;
        }
        string originalValue = field.Value;
        if (string.Equals(textBox.Text, originalValue, StringComparison.Ordinal))
        {
            _session.Inspector.ErrorText = null;
            return;
        }
        BindingExpression? binding = textBox.GetBindingExpression(TextBox.TextProperty);
        binding?.UpdateSource();
        try
        {
            _session.ApplyInspectorField(field);
            _session.Inspector.ErrorText = null;
        }
        catch (Exception exception)
        {
            _session.Inspector.ErrorText = exception.Message;
            if (restoreOnFailure)
            {
                field.Value = originalValue;
                binding?.UpdateTarget();
            }
            else
            {
                textBox.Focus();
                textBox.SelectAll();
            }
        }
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
            CommitProjectSetting(textBox, restoreOnFailure: false);
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
        if (sender is not ComboBox { Tag: InspectorField field }
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
            _session.SetStatusMessage(exception.Message, isError: true);
        }
    }

    private void OnApplyAudioTrackSelectionClick(object sender, RoutedEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || _session.ActiveWorkspace is not SettingsWorkspaceViewModel settings)
        {
            return;
        }
        MidoraId[] selected = settings.AudioRenderTracks
            .Where(track => track.IsSelected)
            .Select(track => track.Id)
            .ToArray();
        RunSynchronous("Update Audio Render Track Selection", () => _session.Execute(
            ProjectDomainEditCommands.UpdateAudioRenderSettings(
                project.AudioRender.Mode,
                project.AudioRender.RangeMode,
                project.AudioRender.ManualStartTick,
                project.AudioRender.ManualEndTick,
                project.AudioRender.TrackSelectionMode,
                selected,
                project.AudioRender.SampleRate,
                project.AudioRender.MaximumSampleVoicesPerUnitStream)));
    }

    private void CommitProjectSetting(TextBox textBox, bool restoreOnFailure)
    {
        if (textBox.IsReadOnly
            || textBox.Tag is not InspectorField field
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

    private bool IsCurrentProjectSettingField(InspectorField field)
    {
        if (_session.Workspaces.OfType<SettingsWorkspaceViewModel>().FirstOrDefault() is not SettingsWorkspaceViewModel settings)
        {
            return false;
        }
        return settings.GeneralFields.Contains(field)
            || settings.PlaybackFields.Contains(field)
            || settings.MidiExportFields.Contains(field)
            || settings.AudioRenderFields.Contains(field)
            || settings.InitialStateFields.Contains(field)
            || settings.ResetDefaultFields.Contains(field);
    }

    private void OnTimelineMarqueeCompleted(object? sender, TimelineMarqueeEventArgs e)
    {
        if (_session.ActiveWorkspace is not WorkspaceViewModel workspace) return;
        bool remove = (e.Modifiers & ModifierKeys.Alt) != 0;
        bool toggle = !remove && (e.Modifiers & ModifierKeys.Control) != 0;
        bool add = !remove && !toggle && (e.Modifiers & ModifierKeys.Shift) != 0;
        workspace.Selection.ApplyRange(
            e.ItemIds,
            remove
                ? WorkspaceSelectionRangeMode.Remove
                : toggle
                    ? WorkspaceSelectionRangeMode.Toggle
                    : add
                        ? WorkspaceSelectionRangeMode.Add
                        : WorkspaceSelectionRangeMode.Replace);
        _session.RefreshWorkspaceSelection(workspace);
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
        RunSynchronous("Split Segment", () => ExecuteAndSelectCreated(
            ProjectDomainEditCommands.SplitSegment(e.Item.Id, splitTick), workspace));
    }

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
            activeWorkspace.ActiveLane = e.Lane;
        }
        if (_session.ActiveWorkspace is TimelineWorkspaceViewModel timeline)
        {
            timeline.EditCursorTick = timeline.EditorSettings.SnapAbsolute(e.Tick);
            if (!e.IsDoubleClick || _session.Project is null) return;
            bool parameterSurface = sender is FrameworkElement { Tag: "ParameterLanes" };
            RunSynchronous("Create timeline object", () =>
            {
                long snapped = timeline.EditorSettings.SnapAbsolute(e.Tick);
                switch (timeline.Mode)
                {
                    case TimelineWorkspaceMode.Arrangement:
                        if (e.Lane >= _session.Project.Tracks.Count) return;
                        LogicalTrack track = _session.Project.Tracks[e.Lane];
                        long requestedLength = timeline.EditorSettings.DefaultLengthTicks;
                        long nextStart = track.Segments
                            .Where(item => item.ProjectStartTick > snapped)
                            .Select(item => item.ProjectStartTick)
                            .DefaultIfEmpty(long.MaxValue)
                            .Min();
                        if (track.Segments.Any(item => snapped >= item.ProjectStartTick && snapped < item.ProjectRange.EndTick)) return;
                        long available = nextStart == long.MaxValue ? requestedLength : checked(nextStart - snapped);
                        if (available <= 0) return;
                        ExecuteAndSelectCreated(ProjectDomainEditCommands.CreateSegment(
                            track.Id,
                            snapped,
                            Math.Min(requestedLength, available)), timeline);
                        break;
                    case TimelineWorkspaceMode.Segment:
                        if (timeline.ObjectId is not MidoraId segmentId) return;
                        if (parameterSurface)
                        {
                            (LogicalTrack Track, Segment Segment)? location =
                                TimelineWorkspaceViewModel.FindSegment(_session.Project, segmentId);
                            if (location is null || timeline.ActiveParameterLaneIndex >= location.Value.Segment.ParameterLanes.Count) return;
                            LogicalParameterLane lane = location.Value.Segment.ParameterLanes[timeline.ActiveParameterLaneIndex];
                            EventInstrument instrument = _session.Project.EventInstruments.Single(
                                item => item.Id == location.Value.Track.EventInstrumentId);
                            LogicalParameterDefinition definition = instrument.LogicalParameters.Single(
                                item => item.Id == lane.ParameterId);
                            ExecuteAndSelectCreated(ProjectDomainEditCommands.CreateLogicalParameterPoint(
                                segmentId,
                                lane.Id,
                                snapped,
                                TimelineWorkspaceViewModel.DenormalizeParameterValue(definition, e.NormalizedValue),
                                CurveInterpolation.Linear), timeline);
                            break;
                        }
                        ExecuteAndSelectCreated(ProjectDomainEditCommands.CreateLogicalNote(
                            segmentId,
                            snapped,
                            timeline.EditorSettings.DefaultLengthTicks,
                            Math.Clamp(127 - e.Lane, 0, 127),
                            timeline.EditorSettings.DefaultVelocity), timeline);
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

        if (_session.ActiveWorkspace is InstrumentWorkspaceViewModel instrument
            && e.IsDoubleClick
            && _session.Project is not null
            && instrument.ObjectId is MidoraId instrumentId)
        {
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
            InstrumentRenderLane? lane = instrument.GetRenderLane(e.Lane);
            if (lane is null) return;
            long tick = instrument.EditorSettings.SnapAbsolute(e.Tick);
            if (lane.ValueCurveId is MidoraId curveId)
            {
                SubVoice voice = source.SubVoices.Single(item => item.Id == lane.SubVoiceId);
                ValueCurve curve = voice.Curves.Single(item => item.Id == curveId);
                RunSynchronous("Create Value Curve point", () => ExecuteAndSelectCreated(
                    ProjectDomainEditCommands.CreateValueCurvePoint(
                        instrumentId,
                        voice.Id,
                        curve.Id,
                        tick,
                        InstrumentWorkspaceViewModel.DenormalizeMidiValue(curve.Target, e.NormalizedValue),
                        CurveInterpolation.Linear), instrument));
            }
            else if (lane.EventKind is TemplateEventKind kind)
            {
                RunSynchronous($"Create {kind}", () => ExecuteAndSelectCreated(
                    CreateTemplateEventCommand(instrumentId, lane.SubVoiceId, kind, tick, source.RootNote), instrument));
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
                long snappedDelta = (e.Modifiers & ModifierKeys.Alt) != 0
                    ? e.TickDelta
                    : timeline.EditorSettings.SnapDelta(
                        e.TickDelta,
                        checked(e.Item.StartTick + e.TickDelta));
                long snappedTarget = Math.Max(0, checked(e.Item.StartTick + snappedDelta));
                snappedDelta = checked(snappedTarget - e.Item.StartTick);
                MidoraId[] selected = timeline.Selection.Ids.Count == 0
                    ? [e.Item.Id]
                    : timeline.Selection.Ids.ToArray();
                switch (timeline.Mode)
                {
                    case TimelineWorkspaceMode.Arrangement:
                        EditArrangementItem(timeline, e, selected, snappedTarget, snappedDelta);
                        break;
                    case TimelineWorkspaceMode.Segment when timeline.ObjectId is MidoraId segmentId:
                        if (e.Item.Kind == TimelineItemKind.LogicalParameterPoint)
                        {
                            EditLogicalParameterPoint(segmentId, e, snappedTarget, selected);
                        }
                        else
                        {
                            EditLogicalNotes(segmentId, e, selected, snappedDelta);
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
        (LogicalTrack Track, Segment Segment)? location =
            TimelineWorkspaceViewModel.FindSegment(_session.Project!, edit.Item.Id);
        if (location is null) return;
        Segment segment = location.Value.Segment;
        if (edit.EditKind == TimelineItemEditKind.Move)
        {
            var locatedSelection = _session.Project!.Tracks
                .SelectMany((track, lane) => track.Segments
                    .Where(item => selected.Contains(item.Id))
                    .Select(item => (Segment: item, Lane: lane)))
                .ToArray();
            long minimumStart = locatedSelection.Min(item => item.Segment.ProjectStartTick);
            long clampedDelta = Math.Max(snappedDelta, -minimumStart);
            int minimumLane = locatedSelection.Min(item => item.Lane);
            int maximumLane = locatedSelection.Max(item => item.Lane);
            int laneDelta = Math.Clamp(
                edit.LaneDelta,
                -minimumLane,
                _session.Project.Tracks.Count - 1 - maximumLane);
            int targetLane = checked(edit.Item.Lane + laneDelta);
            snappedTarget = checked(edit.Item.StartTick + clampedDelta);
            MidoraId targetTrackId = _session.Project.Tracks[targetLane].Id;
            if (edit.CopyRequested)
            {
                long firstNewStableId = _session.Project.NextStableId;
                _session.Execute(ProjectDomainEditCommands.DuplicateSegments(
                    selected,
                    edit.Item.Id,
                    targetTrackId,
                    snappedTarget));
                SelectCreatedWorkspaceObjects(workspace, firstNewStableId);
            }
            else
            {
                _session.Execute(ProjectDomainEditCommands.MoveSegments(
                    selected,
                    edit.Item.Id,
                    targetTrackId,
                    snappedTarget));
            }
            return;
        }

        long oldStart = segment.ProjectStartTick;
        long oldEnd = segment.ProjectRange.EndTick;
        if (edit.EditKind == TimelineItemEditKind.ResizeStart)
        {
            long minimumStart = checked(oldStart - segment.ContentOffsetTick);
            long newStart = Math.Clamp(snappedTarget, minimumStart, oldEnd - 1);
            long delta = checked(newStart - oldStart);
            _session.Execute(ProjectDomainEditCommands.SetSegmentWindow(
                segment.Id,
                newStart,
                checked(oldEnd - newStart),
                checked(segment.ContentOffsetTick + delta)));
        }
        else
        {
            long endDelta = (edit.Modifiers & ModifierKeys.Alt) != 0
                ? edit.TickDelta
                : workspace.EditorSettings.SnapDelta(
                    edit.TickDelta,
                    checked(edit.Item.EndTick + edit.TickDelta));
            long newEnd = Math.Max(oldStart + 1, checked(edit.Item.EndTick + endDelta));
            _session.Execute(ProjectDomainEditCommands.SetSegmentWindow(
                segment.Id,
                oldStart,
                checked(newEnd - oldStart),
                segment.ContentOffsetTick));
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
        LogicalNote[] notes = segment.Notes.Where(item => selected.Contains(item.Id)).ToArray();
        if (notes.Length == 0) return;
        switch (edit.EditKind)
        {
            case TimelineItemEditKind.Move:
                long tickDelta = Math.Max(snappedDelta, -notes.Min(item => item.StartTick));
                int requestedPitchDelta = -edit.LaneDelta;
                int pitchDelta = Math.Clamp(
                    requestedPitchDelta,
                    -notes.Min(item => item.Note),
                    127 - notes.Max(item => item.Note));
                MidoraId[] noteIds = notes.Select(item => item.Id).ToArray();
                if (edit.CopyRequested)
                {
                    long firstNewStableId = _session.Project!.NextStableId;
                    _session.Execute(ProjectDomainEditCommands.DuplicateLogicalNotes(
                        segmentId,
                        noteIds,
                        segmentId,
                        checked(notes.Min(item => item.StartTick) + tickDelta),
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
                long startDelta = Math.Clamp(
                    snappedDelta,
                    -notes.Min(item => item.StartTick),
                    notes.Min(item => item.LengthTicks - 1));
                _session.Execute(ProjectDomainEditCommands.AdjustLogicalNoteEdges(
                    segmentId,
                    selected,
                    startDelta,
                    endDelta: 0));
                break;
            case TimelineItemEditKind.ResizeEnd:
                long endDelta = (edit.Modifiers & ModifierKeys.Alt) != 0
                    ? edit.TickDelta
                    : ((TimelineWorkspaceViewModel)_session.ActiveWorkspace!).EditorSettings.SnapDelta(
                        edit.TickDelta,
                        checked(edit.Item.EndTick + edit.TickDelta));
                endDelta = Math.Max(endDelta, notes.Max(item => 1 - item.LengthTicks));
                _session.Execute(ProjectDomainEditCommands.AdjustLogicalNoteEdges(
                    segmentId,
                    selected,
                    startDelta: 0,
                    endDelta));
                break;
        }
    }

    private TimelineEditorSettings GetActiveEditorSettings() => _session.ActiveWorkspace switch
    {
        TimelineWorkspaceViewModel timeline => timeline.EditorSettings,
        InstrumentWorkspaceViewModel instrument => instrument.EditorSettings,
        _ => _session.ArrangementEditorSettings
    };

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
        LogicalParameterLane? lane = location.Value.Segment.ParameterLanes
            .FirstOrDefault(item => item.Points.Any(point => point.Id == edit.Item.Id));
        CurvePoint? point = lane?.Points.FirstOrDefault(item => item.Id == edit.Item.Id);
        if (lane is null || point is null) return;
        EventInstrument instrument = _session.Project.EventInstruments.Single(
            item => item.Id == location.Value.Track.EventInstrumentId);
        LogicalParameterDefinition definition = instrument.LogicalParameters.Single(item => item.Id == lane.ParameterId);
        double normalized = TimelineWorkspaceViewModel.NormalizeParameterValue(definition, point.Value);
        double value = TimelineWorkspaceViewModel.DenormalizeParameterValue(
            definition,
            Math.Clamp(normalized + edit.ValueDelta, 0, 1));
        MidoraId[] selected = lane.Points
            .Where(candidate => candidate.Id == point.Id || selectedIds.Contains(candidate.Id))
            .Select(candidate => candidate.Id)
            .ToArray();
        double requestedValueDelta = value - point.Value;
        double minimumValueDelta = selected.Max(id =>
            definition.Minimum - lane.Points.Single(candidate => candidate.Id == id).Value);
        double maximumValueDelta = selected.Min(id =>
            definition.Maximum - lane.Points.Single(candidate => candidate.Id == id).Value);
        double valueDelta = Math.Clamp(requestedValueDelta, minimumValueDelta, maximumValueDelta);
        long tickDelta = edit.EditKind == TimelineItemEditKind.Move
            ? Math.Max(
                checked(snappedTarget - point.Tick),
                -selected.Min(id => lane.Points.Single(candidate => candidate.Id == id).Tick))
            : 0;
        _session.Execute(ProjectDomainEditCommands.AdjustLogicalParameterPoints(
            segmentId,
            lane.Id,
            selected,
            tickDelta,
            valueDelta));
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
        if (location is null || location.Value.Track.EventInstrumentId is not MidoraId instrumentId)
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
        SubVoice? voice = instrument.SubVoices.FirstOrDefault(item => item.Events.Any(value => value.Id == edit.Item.Id));
        TemplateEvent? template = voice?.Events.FirstOrDefault(item => item.Id == edit.Item.Id);
        if (voice is null || template is null)
        {
            EditValueCurvePoint(workspace, edit);
            return;
        }
        long snappedDelta = (edit.Modifiers & ModifierKeys.Alt) != 0
            ? edit.TickDelta
            : workspace.EditorSettings.SnapDelta(
                edit.TickDelta,
                checked((edit.EditKind == TimelineItemEditKind.ResizeEnd
                    ? checked(template.Tick + Math.Max(1, template.LengthTicks))
                    : template.Tick) + edit.TickDelta));
        if (template.Kind == TemplateEventKind.Note)
        {
            TemplateEvent[] selectedNotes = voice.Events
                .Where(item => item.Kind == TemplateEventKind.Note
                    && (item.Id == template.Id || workspace.Selection.Ids.Contains(item.Id)))
                .ToArray();
            MidoraId[] selectedIds = selectedNotes.Select(item => item.Id).ToArray();
            switch (edit.EditKind)
            {
                case TimelineItemEditKind.Move:
                    long tickDelta = Math.Max(snappedDelta, -selectedNotes.Min(item => item.Tick));
                    int requestedPitchDelta = -edit.LaneDelta;
                    int pitchDelta = Math.Clamp(
                        requestedPitchDelta,
                        -selectedNotes.Min(item => item.Number),
                        127 - selectedNotes.Max(item => item.Number));
                    if (edit.CopyRequested)
                    {
                        long firstNewStableId = _session.Project.NextStableId;
                        _session.Execute(ProjectDomainEditCommands.DuplicateTemplateNotes(
                            instrumentId,
                            voice.Id,
                            selectedIds,
                            checked(selectedNotes.Min(item => item.Tick) + tickDelta),
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
                    long startDelta = Math.Clamp(
                        snappedDelta,
                        -selectedNotes.Min(item => item.Tick),
                        selectedNotes.Min(item => item.LengthTicks - 1));
                    _session.Execute(ProjectDomainEditCommands.AdjustTemplateNoteEdges(
                        instrumentId,
                        voice.Id,
                        selectedIds,
                        startDelta,
                        endDelta: 0));
                    return;
                case TimelineItemEditKind.ResizeEnd:
                    long endDelta = Math.Max(
                        snappedDelta,
                        selectedNotes.Max(item => 1 - item.LengthTicks));
                    _session.Execute(ProjectDomainEditCommands.AdjustTemplateNoteEdges(
                        instrumentId,
                        voice.Id,
                        selectedIds,
                        startDelta: 0,
                        endDelta));
                    return;
            }
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
            ValueCurve? curve = voice.Curves.FirstOrDefault(item => item.Points.Any(point => point.Id == edit.Item.Id));
            CurvePoint? point = curve?.Points.FirstOrDefault(item => item.Id == edit.Item.Id);
            if (curve is null || point is null) continue;
            (double minimum, double maximum) = InstrumentWorkspaceViewModel.MidiValueRange(curve.Target);
            MidoraId[] selected = curve.Points
                .Where(candidate => candidate.Id == point.Id || workspace.Selection.Ids.Contains(candidate.Id))
                .Select(candidate => candidate.Id)
                .ToArray();
            double requestedValue = Math.Round(
                Math.Clamp(point.Value + edit.ValueDelta * (maximum - minimum), minimum, maximum),
                MidpointRounding.AwayFromZero);
            double requestedValueDelta = requestedValue - point.Value;
            double minimumValueDelta = selected.Max(id =>
                minimum - curve.Points.Single(candidate => candidate.Id == id).Value);
            double maximumValueDelta = selected.Min(id =>
                maximum - curve.Points.Single(candidate => candidate.Id == id).Value);
            long requestedTickDelta = (edit.Modifiers & ModifierKeys.Alt) != 0
                ? edit.TickDelta
                : workspace.EditorSettings.SnapDelta(
                    edit.TickDelta,
                    checked(edit.Item.StartTick + edit.TickDelta));
            long tickDelta = Math.Max(
                requestedTickDelta,
                -selected.Min(id => curve.Points.Single(candidate => candidate.Id == id).Tick));
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
        SelectionDialog voiceDialog = new(
            "Add Template Event",
            "Select the target SubVoice.",
            instrument.SubVoices.Select((voice, index) => new SelectionDialogItem(
                voice.Id,
                string.IsNullOrWhiteSpace(voice.Name) ? $"SubVoice {index + 1}" : voice.Name))) { Owner = this };
        if (voiceDialog.ShowDialog() != true || voiceDialog.SelectedValue is not MidoraId voiceId) return;
        SelectionDialog kindDialog = new(
            "Add Template Event",
            "Select the formal Template Event kind. Exact fields can be edited in Inspector after creation.",
            Enum.GetValues<TemplateEventKind>().Select(kind => new SelectionDialogItem(kind, kind.ToString()))) { Owner = this };
        if (kindDialog.ShowDialog() != true || kindDialog.SelectedValue is not TemplateEventKind kind) return;
        RunSynchronous($"Create {kind}", () => ExecuteAndSelectCreated(
            CreateTemplateEventCommand(instrumentId, voiceId, kind, 0, instrument.RootNote), workspace));
    }

    private void OnAddValueCurveClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not InstrumentWorkspaceViewModel workspace
            || workspace.ObjectId is not MidoraId instrumentId
            || _session.Project is null) return;
        EventInstrument instrument = _session.Project.EventInstruments.Single(item => item.Id == instrumentId);
        SelectionDialog voiceDialog = new(
            "Add Value Curve",
            "Select the target SubVoice.",
            instrument.SubVoices.Select((voice, index) => new SelectionDialogItem(
                voice.Id,
                string.IsNullOrWhiteSpace(voice.Name) ? $"SubVoice {index + 1}" : voice.Name))) { Owner = this };
        if (voiceDialog.ShowDialog() != true || voiceDialog.SelectedValue is not MidoraId voiceId) return;
        MidiTargetDialog targetDialog = new("Add Value Curve") { Owner = this };
        if (targetDialog.ShowDialog() != true || targetDialog.Result is not MidiValueTarget target) return;
        RunSynchronous("Create Value Curve", () => ExecuteAndSelectCreated(
            ProjectDomainEditCommands.CreateValueCurve(instrumentId, voiceId, target), workspace));
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
        SelectionDialog parameterDialog = new(
            "Add Logical Parameter Mapping",
            "Select the source Logical Parameter.",
            instrument.LogicalParameters.Select(item => new SelectionDialogItem(item.Id, item.Name, item.Type.ToString()))) { Owner = this };
        if (parameterDialog.ShowDialog() != true || parameterDialog.SelectedValue is not MidoraId parameterId) return;
        SelectionDialog voiceDialog = new(
            "Add Logical Parameter Mapping",
            "Select the target SubVoice.",
            instrument.SubVoices.Select((voice, index) => new SelectionDialogItem(
                voice.Id,
                string.IsNullOrWhiteSpace(voice.Name) ? $"SubVoice {index + 1}" : voice.Name))) { Owner = this };
        if (voiceDialog.ShowDialog() != true || voiceDialog.SelectedValue is not MidoraId voiceId) return;
        MidiTargetDialog targetDialog = new("Add Logical Parameter Mapping") { Owner = this };
        if (targetDialog.ShowDialog() != true || targetDialog.Result is not MidiValueTarget target) return;
        RunSynchronous("Create Logical Parameter Mapping", () => ExecuteAndSelectCreated(
            ProjectDomainEditCommands.CreateLogicalParameterMapping(
                instrumentId, parameterId, voiceId, target), workspace));
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
        SelectionDialog chainDialog = new(
            "Add Mapping Step",
            "Select the ordered Mapping Chain that will own the new step.",
            workspace.MappingChains.Select(chain => new SelectionDialogItem(
                chain.Id,
                chain.Owner,
                $"{chain.StepCount} existing step(s)"))) { Owner = this };
        if (chainDialog.ShowDialog() != true || chainDialog.SelectedValue is not MidoraId chainId) return;
        SelectionDialog sourceDialog = new(
            "Mapping Source",
            "Select the source read by this step.",
            Enum.GetValues<MappingSource>().Select(value => new SelectionDialogItem(value, value.ToString()))) { Owner = this };
        if (sourceDialog.ShowDialog() != true || sourceDialog.SelectedValue is not MappingSource source) return;
        SelectionDialog operationDialog = new(
            "Mapping Operation",
            "Select the operation. Numeric ranges and references remain editable in Inspector.",
            Enum.GetValues<MappingOperation>().Select(value => new SelectionDialogItem(value, value.ToString()))) { Owner = this };
        if (operationDialog.ShowDialog() != true || operationDialog.SelectedValue is not MappingOperation operation) return;
        RunSynchronous("Create Mapping Step", () => ExecuteAndSelectCreated(
            ProjectDomainEditCommands.CreateMappingStep(
                instrumentId,
                chainId,
                source,
                operation), workspace));
    }

    private void OnMoveMappingStepClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement
            {
                Tag: string directionText,
                DataContext: InstrumentWorkspaceViewModel
                {
                    ObjectId: MidoraId instrumentId,
                    Selection: { Primary: MidoraId stepId }
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
            } workspace) return;
        RunSynchronous("Create Envelope Preset", () => ExecuteAndSelectCreated(
            ProjectDomainEditCommands.CreateInstrumentEnvelope(
                instrumentId,
                name: "Envelope Preset",
                attackTicks: 48,
                decayTicks: 48,
                sustainValue: 0.75,
                releaseTicks: 96), workspace));
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

    private async void OnCompileClick(object sender, RoutedEventArgs e)
    {
        if (!_session.HasProject) return;
        OpenTreeWorkspace(ProjectTreeNodeKind.Diagnostics);
        bool completed = await RunOperationAsync(
            "Compile Project",
            async () => _ = await _session.CompileProjectAsync(),
            DesktopTaskLockLevel.ProjectEdit);
        if (completed)
        {
            bool failed = _session.ErrorCount != 0;
            _session.SetStatusMessage(
                failed
                    ? $"Compile completed with {_session.ErrorCount} error(s). Open Diagnostics for details."
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
        TextDetailsDialog dialog = new("Status Message Details", message)
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
                () => RunSynchronous("Navigate to Diagnostic", () => _session.NavigateToDiagnostic(diagnostic)),
                DispatcherPriority.Normal);
        }
    }

    private void OnNavigateDiagnosticClick(object sender, RoutedEventArgs e)
    {
        if (GetDiagnosticCommandTarget(sender) is DiagnosticRow diagnostic)
        {
            RunSynchronous("Navigate to Diagnostic", () => _session.NavigateToDiagnostic(diagnostic));
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
        return _session.SelectedDiagnostic ?? _session.SelectedBottomDiagnostic;
    }

    private void OnCancelTaskClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DesktopTaskViewModel task }
            || !task.CanRequestCancel)
        {
            return;
        }
        if (task.LockLevel == DesktopTaskLockLevel.FullApplication
            && MessageBox.Show(
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
        if (!_session.HasProject) return;
        if (!StopPlaybackForProjectCommand("MIDI Export")) return;
        string? initialDirectory = ExistingRecentDirectory(RecentDirectoryPurpose.MidiExport)
            ?? (_session.Persistence?.CurrentProjectPath is string currentPath
            ? Path.GetDirectoryName(currentPath)
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        MidiExportDialog dialog = new(
            _session.Project!.Export,
            _session.Project.Tracks,
            initialDirectory) { Owner = this };
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
        MessageBoxResult confirmation = MessageBox.Show(
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
        MessageBox.Show(
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
        if (_session.Project.SoundFont.Reference is null)
        {
            ShowUnavailable("Render Audio", "Audio rendering is unavailable because the Project has no SoundFont.");
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
            _session.Project.AudioRender,
            _session.Project.Tracks,
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
            catch (AudioRenderSoundFontException exception)
                when (exception.Failure == AudioRenderSoundFontFailure.ExternalHashChangeRequiresConfirmation)
            {
                _session.CompleteTask(task, "Attention", "External SoundFont identity changed; explicit task-only acceptance is required.");
                MessageBoxResult accept = MessageBox.Show(
                    this,
                    $"The external Project SoundFont content differs from its stored identity.\n\nCurrent SHA-256: {exception.CurrentSha256}\nCurrent size: {exception.CurrentFileSizeBytes:N0} bytes\n\nUse this changed file for this render only? The Project reference will not be modified.",
                    "External SoundFont Changed",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (accept != MessageBoxResult.Yes) return;
                options = options with { AcceptExternalSoundFontHashChange = true };
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
            MessageBoxResult confirmation = MessageBox.Show(
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
            Progress<AudioRenderTaskProgress> progress = new(value =>
            {
                string detail = $"{value.Status}: output {Math.Max(0, value.CurrentOutputIndex + 1)}/{value.OutputCount}, {value.ProcessedFrameCount:N0}/{value.TotalFrameCount:N0} frames";
                DesktopTaskViewModel? active = _session.TaskHistory.LastOrDefault(item => item.IsRunning);
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
            MessageBox.Show(
                this,
                message,
                "Audio Render",
                MessageBoxButton.OK,
                result.Status == AudioRenderTaskStatus.Completed ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
    }

    private async void OnSelectEmbeddedSoundFontClick(object sender, RoutedEventArgs e)
    {
        if (!_session.HasProject) return;
        OpenFileDialog dialog = CreateSoundFontDialog("Embed Project SoundFont");
        if (dialog.ShowDialog(this) != true) return;
        if (await RunOperationAsync(
            "Embed Project SoundFont",
            cancellationToken => _session.SelectEmbeddedSoundFontAsync(dialog.FileName, cancellationToken),
            canCancel: true))
        {
            RecordRecentDirectory(RecentDirectoryPurpose.SoundFont, Path.GetDirectoryName(dialog.FileName));
        }
    }

    private async void OnSelectExternalSoundFontClick(object sender, RoutedEventArgs e)
    {
        if (!_session.HasProject) return;
        if (_session.Persistence?.CurrentProjectPath is null)
        {
            MessageBox.Show(
                this,
                "Save the Project first. External SoundFonts must resolve relative to the Project root or its direct soundfonts directory.",
                "Use External Project SoundFont",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        OpenFileDialog dialog = CreateSoundFontDialog("Select External Project SoundFont");
        if (dialog.ShowDialog(this) != true) return;
        if (await RunOperationAsync(
            "Select External Project SoundFont",
            cancellationToken => _session.SelectExternalSoundFontAsync(dialog.FileName, cancellationToken),
            canCancel: true))
        {
            RecordRecentDirectory(RecentDirectoryPurpose.SoundFont, Path.GetDirectoryName(dialog.FileName));
        }
    }

    private void OnClearSoundFontClick(object sender, RoutedEventArgs e)
    {
        if (!_session.HasProject) return;
        if (MessageBox.Show(
                this,
                "Clear the Project SoundFont reference? Playback, preview, and audio rendering will become unavailable; editing and MIDI export remain available.",
                "Clear Project SoundFont",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            RunSynchronous("Clear Project SoundFont", _session.ClearSoundFont);
        }
    }

    private OpenFileDialog CreateSoundFontDialog(string title) => new()
    {
        Title = title,
        Filter = "SoundFont 2 (*.sf2)|*.sf2|All files (*.*)|*.*",
        CheckFileExists = true,
        Multiselect = false,
        InitialDirectory = ExistingRecentDirectory(RecentDirectoryPurpose.SoundFont)
    };

    private void OnAboutClick(object sender, RoutedEventArgs e) => MessageBox.Show(
        this,
        "Midora 0.1 development build\nWindows Desktop · .NET 10 · win-x64\n\nCopyright (c) 2026 Midora contributors",
        "About Midora",
        MessageBoxButton.OK,
        MessageBoxImage.Information);

    private void ShowUnavailable(string title, string message) =>
        _session.SetStatusMessage($"{title}: {message}", isError: true);

    private Task<bool> RunOperationAsync(
        string title,
        Func<Task> operation,
        DesktopTaskLockLevel lockLevel = DesktopTaskLockLevel.MainWindow) =>
        RunOperationAsync(title, _ => operation(), canCancel: false, lockLevel: lockLevel);

    private async Task<bool> RunOperationAsync(
        string title,
        Func<CancellationToken, Task> operation,
        bool canCancel,
        DesktopTaskLockLevel lockLevel = DesktopTaskLockLevel.MainWindow)
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
            _session.CompleteTask(task, "Failed", exception.Message);
            ShowError(title, exception.Message);
            return false;
        }
        finally
        {
            _operationInProgress = false;
        }
    }

    private void RunSynchronous(string title, Action operation)
    {
        try { operation(); }
        catch (Exception exception) { _session.SetStatusMessage($"{title}: {exception.Message}", isError: true); }
    }

    private void ShowError(string title, string message) => MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

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

    private void OnRecentProjectsSubmenuOpened(object sender, RoutedEventArgs e)
    {
        RecentProjectsMenu.Items.Clear();
        IReadOnlyList<RecentProjectEntry> entries = _recentProjects.Current;
        if (entries.Count == 0)
        {
            RecentProjectsMenu.Items.Add(new MenuItem { Header = "No recent Projects", IsEnabled = false });
            return;
        }
        foreach (RecentProjectEntry entry in entries)
        {
            MenuItem item = new()
            {
                Header = entry.IsCurrentlyAvailable ? entry.Path : $"{entry.Path}  [Missing]",
                Tag = entry.Path
            };
            item.Click += OnOpenRecentProjectClick;
            RecentProjectsMenu.Items.Add(item);
        }
        RecentProjectsMenu.Items.Add(new Separator());
        MenuItem clear = new() { Header = "Clear Recent Projects" };
        clear.Click += OnClearRecentProjectsClick;
        RecentProjectsMenu.Items.Add(clear);
    }

    private async void OnOpenRecentProjectClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string path }) return;
        if (!File.Exists(path))
        {
            if (MessageBox.Show(
                    this,
                    $"This recent Project no longer exists:\n\n{path}\n\nRemove it from the recent list?",
                    "Recent Project Missing",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                RecentProjectsUpdateResult removed = _recentProjects.Remove(path);
                if (!removed.Succeeded) _session.SetStatusMessage(removed.Notice?.Message, isError: true);
            }
            return;
        }
        if (!StopPlaybackForProjectCommand("Open Recent Project") || !await ConfirmCloseCurrentProjectAsync()) return;
        if (await RunOperationAsync("Open Project", () => _session.OpenProjectAsync(path)))
        {
            RecordRecentDirectory(RecentDirectoryPurpose.OpenProject, Path.GetDirectoryName(path));
            RecordRecentProject(path);
        }
    }

    private void OnClearRecentProjectsClick(object sender, RoutedEventArgs e)
    {
        RecentProjectsUpdateResult result = _recentProjects.Clear();
        if (!result.Succeeded) _session.SetStatusMessage(result.Notice?.Message, isError: true);
    }

    private void LoadDesktopPreferences()
    {
        ApplicationPreferencesLoadResult loaded = _preferenceStore.Load();
        _preferences = loaded.Preferences;
        DesktopUiPreferences ui = _preferences.DesktopUi;
        Width = ui.MainWindowWidth;
        Height = ui.MainWindowHeight;
        ProjectPanelColumn.Width = new GridLength(ui.ProjectPanelWidth);
        InspectorColumn.Width = new GridLength(ui.InspectorWidth);
        BottomPanelRow.Height = new GridLength(ui.BottomPanelHeight);
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
                : currentUi.ProjectPanelWidth,
            InspectorWidth = currentUi.InspectorVisible
                ? Math.Clamp(InspectorColumn.ActualWidth, 220, 420)
                : currentUi.InspectorWidth,
            BottomPanelHeight = currentUi.BottomPanelVisible
                ? Math.Clamp(BottomPanelRow.ActualHeight, 80, 500)
                : currentUi.BottomPanelHeight
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
        ProjectPanelMenuItem.IsChecked = ui.ProjectPanelVisible;
        InspectorMenuItem.IsChecked = ui.InspectorVisible;
        BottomPanelMenuItem.IsChecked = ui.BottomPanelVisible;
        SnapMenuItem.IsChecked = GetActiveEditorSettings().SnapEnabled;
        FollowPlaybackMenuItem.IsChecked = ui.FollowPlayback;

        ProjectPanelGrid.Visibility = ui.ProjectPanelVisible ? Visibility.Visible : Visibility.Collapsed;
        ProjectPanelSplitter.Visibility = ui.ProjectPanelVisible ? Visibility.Visible : Visibility.Collapsed;
        ProjectPanelColumn.MinWidth = ui.ProjectPanelVisible ? 170 : 0;
        ProjectPanelColumn.Width = ui.ProjectPanelVisible ? new(ui.ProjectPanelWidth) : new(0);
        ProjectSplitterColumn.Width = ui.ProjectPanelVisible ? new(4) : new(0);

        InspectorGrid.Visibility = ui.InspectorVisible ? Visibility.Visible : Visibility.Collapsed;
        InspectorSplitter.Visibility = ui.InspectorVisible ? Visibility.Visible : Visibility.Collapsed;
        InspectorColumn.MinWidth = ui.InspectorVisible ? 220 : 0;
        InspectorColumn.Width = ui.InspectorVisible ? new(ui.InspectorWidth) : new(0);
        InspectorSplitterColumn.Width = ui.InspectorVisible ? new(4) : new(0);

        BottomTabs.Visibility = ui.BottomPanelVisible ? Visibility.Visible : Visibility.Collapsed;
        BottomPanelSplitter.Visibility = ui.BottomPanelVisible ? Visibility.Visible : Visibility.Collapsed;
        BottomPanelSplitterRow.Height = ui.BottomPanelVisible ? new(4) : new(0);
        BottomPanelRow.Height = ui.BottomPanelVisible ? new(ui.BottomPanelHeight) : new(0);
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

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
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
        if (e.Key == Key.F3 && _session.ActiveWorkspace is MappingFunctionWorkspaceViewModel)
        {
            FindNextInMappingEditor();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F6)
        {
            if (ProjectTree.IsKeyboardFocusWithin) WorkspaceTabs.Focus();
            else if (WorkspaceTabs.IsKeyboardFocusWithin) ProjectTree.Focus();
            else ProjectTree.Focus();
            e.Handled = true;
            return;
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
            && (!IsTextEditingFocus() || (_session.IsPlaybackActive && _spaceStartedPlayback))
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
            if (ProjectTree.IsKeyboardFocusWithin) DeleteSelectedTreeNode();
            else DeleteWorkspaceSelection();
            e.Handled = true;
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (IsTextEditingFocus())
        {
            if (e.Key == Key.F
                && !shift
                && _session.ActiveWorkspace is MappingFunctionWorkspaceViewModel)
            {
                FocusMappingFind();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.S
                && !shift
                && _session.ActiveWorkspace is MappingFunctionWorkspaceViewModel mapping)
            {
                RunSynchronous("Apply C# Mapping Draft", () => _session.ApplyMappingDraft(mapping));
                e.Handled = true;
            }
            return;
        }
        switch (e.Key)
        {
            case Key.F when !shift && _session.ActiveWorkspace is MappingFunctionWorkspaceViewModel:
                FocusMappingFind(); e.Handled = true; break;
            case Key.N: OnNewProjectClick(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.O: OnOpenProjectClick(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.S when shift: OnSaveCopyClick(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.S:
                if (_session.ActiveWorkspace is MappingFunctionWorkspaceViewModel mapping)
                {
                    RunSynchronous("Apply C# Mapping Draft", () => _session.ApplyMappingDraft(mapping));
                }
                else OnSaveProjectClick(this, new RoutedEventArgs());
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

    private bool TryActivateTimelineTool(Key key)
    {
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
                    if (cut)
                    {
                        ProjectObjectClipboardCutPreparation prepared = ProjectObjectClipboard.PrepareCutSegments(
                            document, ids, primary);
                        payload = prepared.Payload;
                        deleteAfterWrite = prepared.DeleteAfterSuccessfulClipboardWrite;
                    }
                    else payload = ProjectObjectClipboard.CopySegments(document, ids, primary);
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
                    if (location is null) throw new InvalidOperationException("The Segment no longer exists.");
                    HashSet<MidoraId> selected = ids.ToHashSet();
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
                        .Where(item => selected.Contains(item.Id)).Select(item => item.Id).ToArray();
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
                    LogicalParameterLane[] lanes = location.Value.Segment.ParameterLanes
                        .Where(lane => lane.Points.Any(point => selected.Contains(point.Id))).ToArray();
                    if (lanes.Length != 1
                        || lanes[0].Points.Count(point => selected.Contains(point.Id)) != ids.Length)
                    {
                        throw new InvalidOperationException(
                            "Copy or Cut may target Logical Notes or points from one Logical Parameter Lane, not a mixed selection.");
                    }
                    if (cut)
                    {
                        ProjectObjectClipboardCutPreparation prepared = ProjectObjectClipboard.PrepareCutLogicalParameterLaneContent(
                            document, segmentId, lanes[0].Id, ids);
                        payload = prepared.Payload;
                        deleteAfterWrite = prepared.DeleteAfterSuccessfulClipboardWrite;
                    }
                    else payload = ProjectObjectClipboard.CopyLogicalParameterLaneContent(
                        document, segmentId, lanes[0].Id, ids);
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
                    HashSet<MidoraId> selected = ids.ToHashSet();
                    if (ids.Length == 1 && instrumentWorkspace.MappingChains.Any(chain => chain.Id == ids[0]))
                    {
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
                    SubVoice[] voices = instrument.SubVoices
                        .Where(voice => voice.Events.Any(item => selected.Contains(item.Id))).ToArray();
                    if (voices.Length == 1
                        && voices[0].Events.Count(item => selected.Contains(item.Id)) == ids.Length)
                    {
                        if (cut)
                        {
                            ProjectObjectClipboardCutPreparation prepared = ProjectObjectClipboard.PrepareCutSubVoiceTimelineEvents(
                                document, instrumentId, voices[0].Id, ids);
                            payload = prepared.Payload;
                            deleteAfterWrite = prepared.DeleteAfterSuccessfulClipboardWrite;
                        }
                        else payload = ProjectObjectClipboard.CopySubVoiceTimelineEvents(
                            document, instrumentId, voices[0].Id, ids);
                        break;
                    }
                    foreach (SubVoice voice in instrument.SubVoices)
                    {
                        ValueCurve? curve = voice.Curves.FirstOrDefault(
                            candidate => candidate.Points.Any(point => selected.Contains(point.Id)));
                        if (curve is null || curve.Points.Count(point => selected.Contains(point.Id)) != ids.Length) continue;
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
                        "Copy or Cut may target Template Events from one SubVoice or points from one Value Curve.");
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
            }
            _session.SetStatusMessage($"{(cut ? "Cut" : "Copied")} {payload.PlainTextSummary}.");
        });
    }

    private void PasteProjectSelection()
    {
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
            if (workspace is InstrumentWorkspaceViewModel chainWorkspace
                && chainWorkspace.ObjectId is MidoraId chainInstrumentId
                && payload.Kind == ProjectObjectClipboardKind.MappingChain)
            {
                MidoraId targetChainId = ResolveMappingChainTarget(chainWorkspace);
                MappingChainListItem target = chainWorkspace.MappingChains.Single(item => item.Id == targetChainId);
                if (target.StepCount != 0
                    && MessageBox.Show(
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
            else command = workspace switch
            {
                TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Arrangement } arrangement
                    when payload.Kind == ProjectObjectClipboardKind.Segments =>
                    ProjectObjectClipboard.CreatePasteSegmentsCommand(
                        document,
                        payload,
                        ResolveArrangementTargetTrack(project, arrangement),
                        cursor),
                TimelineWorkspaceViewModel
                {
                    Mode: TimelineWorkspaceMode.Segment,
                    ObjectId: MidoraId segmentId
                } segmentWorkspace when payload.Kind == ProjectObjectClipboardKind.LogicalNotes =>
                    ProjectObjectClipboard.CreatePasteLogicalNotesCommand(document, payload, segmentId, cursor),
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
            SelectCreatedWorkspaceObjects(workspace, firstNewStableId);
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
        int lane = Math.Clamp(workspace.ActiveLane ?? 0, 0, project.Tracks.Count - 1);
        return project.Tracks[lane].Id;
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
        int index = workspace.ActiveLane
            ?? throw new InvalidOperationException("Select the target Logical Parameter Lane before pasting points.");
        if ((uint)index >= (uint)located.Value.Segment.ParameterLanes.Count)
        {
            throw new InvalidOperationException("The active target is not a Logical Parameter Lane.");
        }
        return located.Value.Segment.ParameterLanes[index].Id;
    }

    private static InstrumentRenderLane ResolveInstrumentTargetLane(InstrumentWorkspaceViewModel workspace) =>
        workspace.ActiveLane is int lane && workspace.GetRenderLane(lane) is InstrumentRenderLane target
            ? target
            : throw new InvalidOperationException("Select the target SubVoice event or Value Curve lane before pasting.");

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

    private void SelectAllInFocusedScope()
    {
        if (_session.ActiveWorkspace is not WorkspaceViewModel workspace
            || Keyboard.FocusedElement is not TimelineSurface surface
            || surface.Snapshot is null)
        {
            return;
        }
        IEnumerable<TimelineRenderItem> candidates = surface.Snapshot.Items
            .Where(item => (item.State & TimelineItemState.HitTestDisabled) == 0);
        if (workspace.ActiveLane is int lane
            && (surface.Tag as string) == "ParameterLanes")
        {
            candidates = candidates.Where(item => item.Lane == lane);
        }
        MidoraId[] ids = candidates.Select(item => item.Id).Distinct().ToArray();
        workspace.Selection.Clear();
        foreach (MidoraId id in ids) workspace.Selection.Add(id, makePrimary: false);
        _session.RefreshWorkspaceSelection(workspace);
    }

    private void DuplicateFocusedSelection()
    {
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
                    (LogicalTrack Track, Segment Segment) located = TimelineWorkspaceViewModel.FindSegment(project, primary)
                        ?? throw new InvalidOperationException("The primary Segment no longer exists.");
                    long target = cursor == 0
                        ? checked(located.Segment.ProjectStartTick + located.Segment.LengthTicks)
                        : cursor;
                    _session.Execute(ProjectDomainEditCommands.DuplicateSegments(
                        ids, primary, ResolveArrangementTargetTrack(project, arrangement), target));
                    break;
                }
                case TimelineWorkspaceViewModel
                {
                    Mode: TimelineWorkspaceMode.Segment,
                    ObjectId: MidoraId segmentId
                }:
                {
                    (LogicalTrack Track, Segment Segment) located = TimelineWorkspaceViewModel.FindSegment(project, segmentId)
                        ?? throw new InvalidOperationException("The Segment no longer exists.");
                    HashSet<MidoraId> selected = ids.ToHashSet();
                    LogicalNote[] notes = located.Segment.Notes.Where(item => selected.Contains(item.Id)).ToArray();
                    if (notes.Length != ids.Length)
                    {
                        throw new InvalidOperationException("Ctrl+D currently duplicates Logical Notes in the Segment note scope.");
                    }
                    long target = cursor == 0
                        ? checked(notes.Min(item => item.StartTick) + Math.Max(1, notes.Max(item => item.LengthTicks)))
                        : cursor;
                    _session.Execute(ProjectDomainEditCommands.DuplicateLogicalNotes(segmentId, ids, segmentId, target));
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
                    _session.Execute(ProjectDomainEditCommands.DeleteSegments(ids));
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
        });
    }

    private void DeleteSegmentSelection(MidoraId segmentId, MidoraId[] ids)
    {
        (LogicalTrack Track, Segment Segment)? location =
            TimelineWorkspaceViewModel.FindSegment(_session.Project!, segmentId);
        if (location is null) return;
        HashSet<MidoraId> selected = ids.ToHashSet();
        if (ids.Length == 1
            && location.Value.Segment.ParameterLanes.Any(lane => lane.Id == ids[0]))
        {
            if (MessageBox.Show(
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
            .Where(item => selected.Contains(item.Id)).Select(item => item.Id).ToArray();
        if (notes.Length == ids.Length)
        {
            _session.Execute(ProjectDomainEditCommands.DeleteLogicalNotes(segmentId, notes));
            return;
        }
        LogicalParameterLane[] lanes = location.Value.Segment.ParameterLanes
            .Where(lane => lane.Points.Any(point => selected.Contains(point.Id))).ToArray();
        if (lanes.Length == 1
            && lanes[0].Points.Count(point => selected.Contains(point.Id)) == ids.Length)
        {
            _session.Execute(ProjectDomainEditCommands.DeleteLogicalParameterPoints(
                segmentId,
                lanes[0].Id,
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
                if (MessageBox.Show(
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
                if (MessageBox.Show(
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
                if (MessageBox.Show(
                        this,
                        "Delete the selected C# Mapping Function? Existing Mapping Steps that reference it may also be affected.",
                        "Delete C# Mapping Function",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    _session.Execute(ProjectDomainEditCommands.DeleteMappingFunction(instrumentId, id, referencedDeletionConfirmed: true));
                }
                return;
            }
            if (instrument.ParameterMappings.Any(item => item.Id == id))
            {
                if (MessageBox.Show(
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
                if (MessageBox.Show(
                        this,
                        "Delete the selected Envelope Preset? Mapping Steps that reference it will retain a broken stable-ID reference.",
                        "Delete Envelope Preset",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    _session.Execute(ProjectDomainEditCommands.DeleteInstrumentEnvelope(
                        instrumentId, id, referencedDeletionConfirmed: true));
                }
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
            ValueCurve? curve = candidateVoice.Curves.FirstOrDefault(
                item => item.Points.Any(point => selected.Contains(point.Id)));
            if (curve is not null && curve.Points.Count(point => selected.Contains(point.Id)) == ids.Length)
            {
                _session.Execute(ProjectDomainEditCommands.DeleteValueCurvePoints(
                    instrumentId,
                    candidateVoice.Id,
                    curve.Id,
                    ids));
                return;
            }
        }
        SubVoice[] voices = instrument.SubVoices
            .Where(voice => voice.Events.Any(item => selected.Contains(item.Id))).ToArray();
        if (voices.Length != 1
            || voices[0].Events.Count(item => selected.Contains(item.Id)) != ids.Length)
        {
            throw new InvalidOperationException(
                "A single delete gesture may target Template Events from one SubVoice only.");
        }
        _session.Execute(ProjectDomainEditCommands.DeleteTemplateEvents(
            instrumentId,
            voices[0].Id,
            ids));
    }

    private static IEnumerable<MappingChain> EnumerateInstrumentMappingChains(EventInstrument instrument) =>
        instrument.ParameterMappings.Select(item => item.Steps)
            .Concat(instrument.SubVoices.SelectMany(voice => voice.Events).SelectMany(item => new[]
            {
                item.NumberMappings,
                item.ValueMappings,
                item.SecondaryValueMappings
            }));

    private static bool IsTextEditingFocus() => Keyboard.FocusedElement is TextBoxBase
        or PasswordBox
        or ComboBox;

    private void OnPreviewMouseDownForPlaybackShortcut(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;
        if (_spaceStartedPlayback
            && (FindVisualAncestor<TextBoxBase>(source) is not null
                || FindVisualAncestor<PasswordBox>(source) is not null
                || FindVisualAncestor<ComboBox>(source) is not null))
        {
            _spaceStartedPlayback = false;
        }
    }

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
            current = current is Visual or Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleMaximize(); return; }
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnTitleBarMouseRightButtonUp(object sender, MouseButtonEventArgs e) =>
        SystemCommands.ShowSystemMenu(this, PointToScreen(e.GetPosition(this)));

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
