using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using Midora.Application;
using Midora.Audio;
using Midora.Compiler;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using Midora.Persistence;
using Midora.Playback;
using Midora.MidiExport;
using Midora.AudioRender;
using Midora.Audio.Bass;
using Midora.Playback.BassWasapi;

namespace Midora.Desktop;

public sealed class DesktopSessionController : ObservableObject, IAsyncDisposable
{
    private readonly object _modelRefreshGate = new();
    private int _compilerErrorCount;
    private int _compilerWarningCount;
    private const string SoftwareVersion = "0.1.0-dev";
    private readonly MidoraProjectPackageV1 _packages = new(SoftwareVersion);
    private readonly ProjectCreationCoordinator _creation;
    private readonly ProjectOpenCoordinator _opening;
    private readonly CSharpMappingDraftCompiler _mappingDraftCompiler = new();
    private readonly HashSet<MidoraId> _mutedTrackIds = [];
    private readonly HashSet<MidoraId> _soloTrackIds = [];
    private readonly List<WorkspaceKey> _backNavigation = [];
    private readonly List<WorkspaceKey> _forwardNavigation = [];
    private readonly SynchronizationContext? _uiContext =
        SynchronizationContext.Current is DispatcherSynchronizationContext dispatcherContext
            ? dispatcherContext
            : null;
    private ProjectContext? _context;
    private WorkspaceViewModel? _activeWorkspace;
    private WorkspaceViewModel? _diagnosticScopeWorkspace;
    private long _revision;
    private string? _notice;
    private DiagnosticRow? _selectedDiagnostic;
    private DiagnosticRow? _selectedBottomDiagnostic;
    private ProjectTimeSignatureMap? _timeSignatureMap;
    private string _projectTreeSearchText = string.Empty;
    private bool _isNavigatingHistory;
    private string? _statusMessage;
    private bool _statusMessageIsError;
    private bool _isPlaybackStartPending;

    public DesktopSessionController()
    {
        _creation = new(_packages, new WorkerSoundFontLoadabilityValidator());
        _opening = new(_packages);
    }

    public bool HasProject => _context is not null;
    public bool IsForegroundTaskRunning => TaskHistory.Any(item => item.IsRunning);
    public DesktopTaskViewModel? ActiveForegroundTask => TaskHistory.LastOrDefault(item => item.IsRunning);
    public bool IsMainWindowTaskLocked => ActiveForegroundTask?.LockLevel >= DesktopTaskLockLevel.MainWindow;
    public bool IsFullApplicationTaskLocked => ActiveForegroundTask?.LockLevel >= DesktopTaskLockLevel.FullApplication;
    public bool CanEditProject => HasProject && !IsForegroundTaskRunning && !IsPlaybackActive;
    public bool CanStartForegroundTask => !IsForegroundTaskRunning && !IsPlaybackActive;
    public bool CanRunProjectTask => HasProject && CanStartForegroundTask;
    public bool HasDamagedProjectObjects => Project is not null
        && (Project.DamagedEventInstruments.Count != 0 || Project.DamagedLogicalTracks.Count != 0);
    public bool CanSaveProject => HasProject && !IsForegroundTaskRunning && !HasDamagedProjectObjects;
    public bool CanUseContextMenus => !IsMainWindowTaskLocked;
    public bool CanNavigateBack => _backNavigation.Any(key => Workspaces.Any(item => item.Key == key));
    public bool CanNavigateForward => _forwardNavigation.Any(key => Workspaces.Any(item => item.Key == key));
    public bool HasUnsavedDrafts => Workspaces.OfType<MappingFunctionWorkspaceViewModel>().Any(item => item.IsDirty);
    public ProjectDocumentSession? Document => _context?.Document;
    public MidoraProject? Project => Document?.Project;
    public ProjectPersistenceCoordinator? Persistence => _context?.Persistence;
    public string WindowTitle => _context is null
        ? "Midora"
        : $"Midora — {ProjectDisplayName}{(Document!.IsModified ? " *" : string.Empty)}";
    public string ProjectDisplayName
    {
        get
        {
            if (Project is null) return "No Project";
            if (!string.IsNullOrWhiteSpace(Project.Metadata.ProjectName))
            {
                return Project.Metadata.ProjectName;
            }
            return Persistence?.CurrentProjectPath is string path
                ? Path.GetFileNameWithoutExtension(path)
                : "Untitled Project";
        }
    }
    public string TitleBarProjectDisplayName => _context is null
        ? "No Project"
        : $"{ProjectDisplayName}{(Document!.IsModified ? " *" : string.Empty)}";
    public string ProjectState => _context is null
        ? "No Project"
        : Persistence?.CurrentProjectPath is null
            ? Document!.IsModified ? "Modified · Unsaved" : "Unsaved"
            : Document!.IsModified ? "Modified" : "Saved";
    public string CompileState => _context is null
        ? "Not Compiled"
        : _context.Compilation.CompilationState switch
        {
            ProjectCompilationState.NotCompiled => "Not Compiled",
            ProjectCompilationState.Outdated => "Compile Result Outdated",
            ProjectCompilationState.Compiling => "Compiling",
            ProjectCompilationState.Succeeded => "Compile Succeeded",
            ProjectCompilationState.Failed => "Compile Failed",
            _ => "Not Compiled"
        };
    public string SoundFontState => Project?.SoundFont.Reference is null
        ? "No SoundFont"
        : "SoundFont Configured";
    public bool IsTrackMuted(MidoraId trackId) => _mutedTrackIds.Contains(trackId);
    public bool IsTrackSolo(MidoraId trackId) => _soloTrackIds.Contains(trackId);
    public long CurrentTick => _context?.Playback?.CurrentTick ?? 0;
    public string TempoText
    {
        get
        {
            TempoChange? tempo = Project?.Conductor.Tempos
                .Where(item => item.Tick <= Math.Max(0, CurrentTick))
                .OrderByDescending(item => item.Tick)
                .FirstOrDefault();
            return tempo is null
                ? "— BPM"
                : $"{tempo.BeatsPerMinute:0.00} BPM";
        }
    }
    public string PositionText
    {
        get
        {
            if (_timeSignatureMap is null) return "—";
            ProjectMusicalPosition position = _timeSignatureMap.GetPosition(Math.Max(0, CurrentTick));
            return $"{position.Bar:D4} : {position.Beat:D2} : {position.TickOffset:D3}";
        }
    }
    public PlaybackState PlaybackState => _context?.Playback?.State ?? PlaybackState.Stopped;
    public bool IsPlaybackActive => PlaybackState is PlaybackState.Preparing
        or PlaybackState.Playing
        or PlaybackState.Buffering
        or PlaybackState.Stopping;
    public bool IsLoopEnabled => _context?.Playback?.LoopRange is not null;
    public bool CanPlayback => _context?.Playback is not null
        && _context.Compilation.EffectiveSoundFontPath is not null
        && _context.Compilation.CompilationState is not ProjectCompilationState.Failed
        && !_isPlaybackStartPending
        && !IsPlaybackActive;
    public bool CanPreview => _context?.Tasks is not null
        && _context.Compilation.EffectiveSoundFontPath is not null
        && !IsPlaybackActive
        && !IsForegroundTaskRunning;
    public string? PlaybackUnavailableReason => _context?.PlaybackUnavailableReason
        ?? (_context?.Compilation.EffectiveSoundFontPath is null
            ? "A verified Project SoundFont is required."
            : _context.Compilation.CompilationState == ProjectCompilationState.Failed
                ? "The current canonical compilation is not consumable."
                : null);
    public int ErrorCount => _compilerErrorCount;
    public int WarningCount => _compilerWarningCount;
    public string IssueSummary => _context is null
        ? "No diagnostics"
        : $"{ErrorCount} Errors, {WarningCount} Warnings";
    public string? Notice
    {
        get => _notice;
        set
        {
            if (Set(ref _notice, value))
            {
                Raise(nameof(HasNotice));
            }
        }
    }
    public bool HasNotice => !string.IsNullOrWhiteSpace(Notice);
    public string ProjectTreeSearchText
    {
        get => _projectTreeSearchText;
        set
        {
            string normalized = value ?? string.Empty;
            if (!Set(ref _projectTreeSearchText, normalized)) return;
            Raise(nameof(IsProjectTreeFiltered));
            RefreshProjectTree();
        }
    }
    public bool IsProjectTreeFiltered => !string.IsNullOrWhiteSpace(ProjectTreeSearchText);
    public DiagnosticRow? SelectedDiagnostic
    {
        get => _selectedDiagnostic;
        set => Set(ref _selectedDiagnostic, value);
    }
    public DiagnosticRow? SelectedBottomDiagnostic
    {
        get => _selectedBottomDiagnostic;
        set => Set(ref _selectedBottomDiagnostic, value);
    }
    public string ActivityText => ActiveForegroundTask?.Status
        ?? PlaybackState.ToString();

    public ObservableCollection<ProjectTreeNode> ProjectTree { get; } = [];
    public ObservableCollection<WorkspaceViewModel> Workspaces { get; } = [];
    public ObservableCollection<DiagnosticRow> CompilerDiagnostics { get; } = [];
    public DiagnosticsWorkspaceViewModel BottomDiagnosticsViewModel { get; } = new();
    public ObservableCollection<DesktopTaskViewModel> TaskHistory { get; } = [];
    public InspectorViewModel Inspector { get; } = new();
    public TimelineEditorSettings ArrangementEditorSettings { get; } = new();
    public TimelineEditorSettings PianoRollEditorSettings { get; } = new();
    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (Set(ref _statusMessage, value)) Raise(nameof(HasStatusMessage));
        }
    }
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool StatusMessageIsError
    {
        get => _statusMessageIsError;
        private set => Set(ref _statusMessageIsError, value);
    }

    public void SetStatusMessage(string? message, bool isError = false)
    {
        StatusMessageIsError = isError;
        StatusMessage = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
    }

    public WorkspaceViewModel? ActiveWorkspace
    {
        get => _activeWorkspace;
        set
        {
            WorkspaceViewModel? previous = _activeWorkspace;
            if (Set(ref _activeWorkspace, value))
            {
                if (!_isNavigatingHistory
                    && previous is not null
                    && value is not null
                    && previous.Key != value.Key)
                {
                    _backNavigation.Add(previous.Key);
                    if (_backNavigation.Count > 100) _backNavigation.RemoveAt(0);
                    _forwardNavigation.Clear();
                }
                RefreshInspector();
                if (value is not DiagnosticsWorkspaceViewModel)
                {
                    _diagnosticScopeWorkspace = value;
                }
                Workspaces.OfType<DiagnosticsWorkspaceViewModel>()
                    .FirstOrDefault()?.SetScope(_diagnosticScopeWorkspace);
                BottomDiagnosticsViewModel.SetScope(_diagnosticScopeWorkspace);
                Raise(nameof(CanNavigateBack));
                Raise(nameof(CanNavigateForward));
            }
        }
    }

    public void NavigateBack() => NavigateWorkspaceHistory(_backNavigation, _forwardNavigation);

    public void NavigateForward() => NavigateWorkspaceHistory(_forwardNavigation, _backNavigation);

    private void NavigateWorkspaceHistory(List<WorkspaceKey> source, List<WorkspaceKey> destination)
    {
        while (source.Count != 0)
        {
            WorkspaceKey key = source[^1];
            source.RemoveAt(source.Count - 1);
            WorkspaceViewModel? target = Workspaces.FirstOrDefault(item => item.Key == key);
            if (target is null || ReferenceEquals(target, ActiveWorkspace)) continue;
            if (ActiveWorkspace is not null)
            {
                destination.Add(ActiveWorkspace.Key);
                if (destination.Count > 100) destination.RemoveAt(0);
            }
            _isNavigatingHistory = true;
            try { ActiveWorkspace = target; }
            finally { _isNavigatingHistory = false; }
            Raise(nameof(CanNavigateBack));
            Raise(nameof(CanNavigateForward));
            return;
        }
        Raise(nameof(CanNavigateBack));
        Raise(nameof(CanNavigateForward));
    }

    public async Task CreateProjectAsync(
        NewProjectCreationRequest request,
        CancellationToken cancellationToken = default)
    {
        NewProjectCreationResult result = await _creation.CreateAsync(request, cancellationToken);
        ProjectContext? next = null;
        try
        {
            next = ProjectContext.FromCreation(_packages, result);
            result = null!;
            await next.RefreshSoundFontAsync(cancellationToken);
            await ActivateAsync(next);
            next = null;
        }
        finally
        {
            if (next is not null)
            {
                await next.DisposeAsync();
            }
            if (result is not null)
            {
                await result.DisposeAsync();
            }
        }
    }

    public async Task OpenProjectAsync(
        string path,
        IProgress<ProjectOpenCandidateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ProjectOpenCandidate candidate = await _opening.OpenAsync(path, progress, cancellationToken);
        ProjectContext? next = null;
        try
        {
            next = ProjectContext.FromOpenCandidate(candidate);
            candidate = null!;
            await next.RefreshSoundFontAsync(cancellationToken);
            await ActivateAsync(next);
            next = null;
        }
        finally
        {
            if (next is not null)
            {
                await next.DisposeAsync();
            }
            if (candidate is not null)
            {
                await candidate.DisposeAsync();
            }
        }
    }

    public async Task SaveProjectAsync(
        string? firstSavePath = null,
        bool overwriteAuthorized = false,
        CancellationToken cancellationToken = default)
    {
        if (Persistence is null)
        {
            throw new InvalidOperationException("No Project is open.");
        }
        await Persistence.SaveProjectAsync(
            firstSavePath,
            overwriteAuthorized,
            cancellationToken);
        RefreshAll();
    }

    public async Task SaveCopyAsync(
        string path,
        bool overwriteAuthorized,
        CancellationToken cancellationToken = default)
    {
        if (Persistence is null)
        {
            throw new InvalidOperationException("No Project is open.");
        }
        await Persistence.SaveCopyAsync(path, overwriteAuthorized, cancellationToken);
    }

    public PreparedDesktopMidiExport PrepareMidiExport(DesktopMidiExportOptions options)
    {
        if (_context is null || Project is null)
        {
            throw new InvalidOperationException("No Project is open.");
        }
        _ = _context.Compilation.EnsureCurrentCompilationAsync().GetAwaiter().GetResult();
        using IDisposable editLock = _context.Compilation.AcquireProjectEditLock();
        MidoraProjectFileInformationV1? information = Persistence?.FileInformation;
        return DesktopMidiExportService.Prepare(
            Project,
            Persistence?.CurrentProjectPath,
            information?.CreatedWithSoftwareVersion,
            information?.LastSavedWithSoftwareVersion,
            options);
    }

    public async Task<MidiExportTaskResult> ExecuteMidiExportAsync(
        PreparedDesktopMidiExport prepared,
        bool overwriteAuthorized,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (_context is null)
        {
            throw new InvalidOperationException("No Project is open.");
        }
        using IDisposable editLock = _context.Compilation.AcquireProjectEditLock();
        return await new MidiExportTaskRunner().ExecuteAsync(new()
        {
            Compilation = prepared.Compilation,
            OutputPlan = prepared.OutputPlan,
            Readme = prepared.Readme,
            OverwriteAuthorized = overwriteAuthorized
        }, cancellationToken);
    }

    public async Task SelectEmbeddedSoundFontAsync(
        string selectedPath,
        CancellationToken cancellationToken = default)
    {
        if (_context is null) throw new InvalidOperationException("No Project is open.");
        await _context.SoundFontEditing.SelectEmbeddedAsync(selectedPath, cancellationToken);
        await _context.RefreshSoundFontAsync(cancellationToken);
        RefreshAll();
    }

    public async Task SelectExternalSoundFontAsync(
        string selectedPath,
        CancellationToken cancellationToken = default)
    {
        if (_context is null) throw new InvalidOperationException("No Project is open.");
        string projectPath = Persistence?.CurrentProjectPath
            ?? throw new InvalidOperationException(
                "Save the Project before selecting an external relative SoundFont.");
        await _context.SoundFontEditing.SelectExternalAsync(
            projectPath,
            selectedPath,
            cancellationToken);
        await _context.RefreshSoundFontAsync(cancellationToken);
        RefreshAll();
    }

    public void ClearSoundFont()
    {
        if (_context is null) throw new InvalidOperationException("No Project is open.");
        _context.SoundFontEditing.Clear();
        RefreshAll();
    }

    public void StartPlayback(long? cursorTick = null)
    {
        if (_context?.Tasks is null)
        {
            throw new InvalidOperationException(
                PlaybackUnavailableReason ?? "The formal realtime playback backend is unavailable.");
        }
        CanonicalCompiledResult result = _context.Compilation
            .EnsureCurrentCompilationAsync()
            .GetAwaiter()
            .GetResult();
        if (!result.IsConsumable)
        {
            throw new InvalidOperationException(
                "The current canonical compilation is not consumable.");
        }
        _context.Tasks.StartMainPlayback(cursorTick);
        RefreshProperties();
    }

    public async Task StartPlaybackAsync(
        long? cursorTick = null,
        CancellationToken cancellationToken = default)
    {
        ProjectContext context = _context
            ?? throw new InvalidOperationException("No Project is open.");
        if (context.Tasks is null)
        {
            throw new InvalidOperationException(
                PlaybackUnavailableReason ?? "The formal realtime playback backend is unavailable.");
        }
        if (_isPlaybackStartPending)
        {
            throw new InvalidOperationException("Playback preparation is already waiting for compilation.");
        }
        _isPlaybackStartPending = true;
        RefreshProperties();
        try
        {
            CanonicalCompiledResult result = await context.Compilation
                .EnsureCurrentCompilationAsync(cancellationToken);
            if (!ReferenceEquals(_context, context))
            {
                throw new OperationCanceledException("The Project changed while playback was preparing.");
            }
            if (!result.IsConsumable)
            {
                throw new InvalidOperationException(
                    "The current canonical compilation is not consumable.");
            }
            context.Tasks.StartMainPlayback(cursorTick);
        }
        finally
        {
            _isPlaybackStartPending = false;
            RefreshProperties();
        }
    }

    public void StopPlayback()
    {
        _context?.Tasks?.StopPlayback();
        RefreshProperties();
    }

    public void SetPlaybackCursor(long tick)
    {
        if (tick < 0) throw new ArgumentOutOfRangeException(nameof(tick));
        if (_context?.Playback is null)
        {
            throw new InvalidOperationException("No Project playback session is open.");
        }
        _context.Playback.Seek(tick);
        RefreshProperties();
    }

    public void SetLoopRange(TickRange? range)
    {
        if (_context?.Playback is null)
        {
            throw new InvalidOperationException("No Project playback session is open.");
        }
        _context.Playback.SetLoop(range);
        RefreshProperties();
    }

    public void SetTrackMuted(MidoraId trackId, bool muted)
    {
        SetTrackMonitoringState(trackId, muted, isSolo: false);
    }

    public void SetTrackSolo(MidoraId trackId, bool solo)
    {
        SetTrackMonitoringState(trackId, solo, isSolo: true);
    }

    private void SetTrackMonitoringState(MidoraId trackId, bool enabled, bool isSolo)
    {
        if (Project?.Tracks.Any(track => track.Id == trackId) != true)
        {
            throw new InvalidOperationException("The Logical Track no longer exists.");
        }
        HashSet<MidoraId> states = isSolo ? _soloTrackIds : _mutedTrackIds;
        bool wasEnabled = states.Contains(trackId);
        if (wasEnabled == enabled) return;
        if (enabled) states.Add(trackId);
        else states.Remove(trackId);
        try
        {
            if (isSolo) _context?.Playback?.SetTrackSolo(trackId, enabled);
            else _context?.Playback?.SetTrackMuted(trackId, enabled);
        }
        catch
        {
            if (wasEnabled) states.Add(trackId);
            else states.Remove(trackId);
            throw;
        }
        foreach (TimelineWorkspaceViewModel workspace in Workspaces
                     .OfType<TimelineWorkspaceViewModel>()
                     .Where(item => item.Mode == TimelineWorkspaceMode.Arrangement))
        {
            RefreshWorkspace(workspace);
        }
    }

    public void StartHeldEventInstrumentPreview(EventInstrumentPreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_context?.Tasks is null)
        {
            throw new InvalidOperationException(
                _context?.PlaybackUnavailableReason ?? "Realtime Preview is unavailable.");
        }
        if (!CanPreview)
        {
            throw new InvalidOperationException(
                PlaybackUnavailableReason ?? "Realtime Preview is currently locked.");
        }
        _context.Tasks.StartHeldEventInstrumentPreview(request);
        RefreshProperties();
    }

    public void StartHeldSegmentPitchRulerPreview(
        MidoraId trackId,
        MidoraId segmentId,
        int pitch,
        int velocity,
        decimal tempo)
    {
        if (!CanPreview)
        {
            throw new InvalidOperationException(
                PlaybackUnavailableReason ?? "Realtime Preview is currently locked.");
        }
        (_context?.Tasks ?? throw new InvalidOperationException("Realtime Preview is unavailable."))
            .StartHeldSegmentPitchRulerPreview(trackId, segmentId, pitch, velocity, tempo);
        RefreshProperties();
    }

    public Exception? TryStartHeldSegmentNotePreview(SegmentNotePreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_context?.Tasks is null)
        {
            return new InvalidOperationException(
                _context?.PlaybackUnavailableReason ?? "Realtime Preview is unavailable.");
        }
        Exception? failure = _context.Tasks.TryStartHeldSegmentNotePreview(request);
        RefreshProperties();
        return failure;
    }

    public SegmentNotePlacementPreviewCompletion CompleteSegmentNotePlacement(
        long finalGateLengthTicks,
        IProjectEditCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (Document is null)
        {
            throw new InvalidOperationException("No Project note-placement session is available.");
        }

        if (_context?.Tasks is null)
        {
            Document.Execute(command);
            RefreshProperties();
            return new SegmentNotePlacementPreviewCompletion(null, null);
        }

        SegmentNotePlacementPreviewCompletion result = _context.Tasks.CompleteSegmentNotePlacement(
            finalGateLengthTicks,
            () => Document.Execute(command));
        RefreshProperties();
        return result;
    }

    public void EndHeldPreviewGate()
    {
        if (_context?.Playback?.IsHeldPreviewGateOpen == true)
        {
            _context.Tasks!.EndHeldPreviewGate();
            RefreshProperties();
        }
    }

    public void CancelHeldPreview()
    {
        _context?.Tasks?.CancelHeldPreview();
        RefreshProperties();
    }

    public void ResetPlaybackEngine()
    {
        if (_context?.Playback is null) return;
        _context.Playback.ResetPlaybackEngine();
        RefreshProperties();
    }

    public void UpdatePlayback()
    {
        if (_context?.Playback is null) return;
        _context.Playback.Update();
        RefreshTimelinePlaybackCursors();
        Raise(nameof(CurrentTick));
        Raise(nameof(TempoText));
        Raise(nameof(PositionText));
        Raise(nameof(PlaybackState));
        Raise(nameof(IsPlaybackActive));
        Raise(nameof(IsLoopEnabled));
        Raise(nameof(CanPlayback));
        Raise(nameof(CanPreview));
    }

    public async Task<PreparedDesktopAudioRender> PrepareAudioRenderAsync(
        DesktopAudioRenderOptions options,
        CancellationToken cancellationToken = default)
    {
        if (_context is null || Project is null)
        {
            throw new InvalidOperationException("No Project is open.");
        }
        CanonicalCompiledResult current = await _context.Compilation
            .EnsureCurrentCompilationAsync(cancellationToken);
        if (!current.IsConsumable)
        {
            throw new InvalidOperationException(
                "Audio rendering requires a consumable current canonical compilation.");
        }
        using IDisposable editLock = _context.Compilation.AcquireProjectEditLock();
        return await DesktopAudioRenderService.PrepareAsync(
            Project,
            Persistence?.CurrentProjectPath,
            _context.SoundFontResources.CurrentEmbeddedResource,
            options,
            cancellationToken);
    }

    public async Task<AudioRenderTaskResult> ExecuteAudioRenderAsync(
        PreparedDesktopAudioRender prepared,
        bool overwriteAuthorized,
        IProgress<AudioRenderTaskProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (_context is null)
        {
            throw new InvalidOperationException("No Project is open.");
        }
        using IDisposable editLock = _context.Compilation.AcquireProjectEditLock();
        AudioRenderTaskRunner runner = new(prepared.Worker);
        return await runner.ExecuteAsync(
            new()
            {
                Compilation = prepared.Compilation,
                OutputPlan = prepared.OutputPlan,
                SoundFont = prepared.SoundFont,
                SampleRate = prepared.SampleRate,
                MaximumSampleVoicesPerUnitStream = prepared.MaximumSampleVoicesPerUnitStream,
                MasterVolumeDecibels = prepared.MasterVolumeDecibels,
                OverwriteAuthorized = overwriteAuthorized
            },
            _context.Compilation,
            progress,
            cancellationToken);
    }

    public async Task<CanonicalCompiledResult> CompileProjectAsync(CancellationToken cancellationToken = default)
    {
        if (_context is null) throw new InvalidOperationException("No Project is open.");
        return await _context.Compilation.RecompileAsync(
            new ProjectChangeSet { AffectsEverything = true },
            cancellationToken);
    }

    public ProjectEditExecution Execute(IProjectEditCommand command)
    {
        if (Document is null)
        {
            throw new InvalidOperationException("No Project is open.");
        }
        if (!CanEditProject)
        {
            throw new InvalidOperationException(
                "Project editing is locked while playback or a foreground task is active.");
        }
        ProjectEditExecution result = Document.Execute(command);
        // ProjectDocumentSession raises HistoryChanged synchronously for a changed edit.
        // That event is the single refresh/revision path; refreshing here as well would
        // rebuild every rendered workspace twice for one atomic edit.
        return result;
    }

    public void ApplyInspectorField(InspectorField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (!field.IsEditable || Project is null || ActiveWorkspace is null)
        {
            throw new InvalidOperationException("This Inspector property is read-only.");
        }
        Execute(InspectorProjection.CreateEditCommand(Project, ActiveWorkspace, field.Key, field.Value));
    }

    public void ApplyProjectSettingsField(InspectorField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (!field.IsEditable || Project is null)
        {
            throw new InvalidOperationException("This Project Settings field is read-only.");
        }
        Execute(ProjectSettingsProjection.CreateEditCommand(Project, field));
    }

    public DesktopTaskViewModel BeginTask(
        string name,
        bool canCancel,
        DesktopTaskLockLevel lockLevel = DesktopTaskLockLevel.ProjectEdit)
    {
        if (IsForegroundTaskRunning)
        {
            throw new InvalidOperationException("Another foreground task is already running.");
        }
        DesktopTaskViewModel task = new(name, canCancel, lockLevel);
        TaskHistory.Add(task);
        while (TaskHistory.Count > 100)
        {
            DesktopTaskViewModel? removable = TaskHistory.FirstOrDefault(item => !item.IsRunning);
            if (removable is null) break;
            TaskHistory.Remove(removable);
            removable.Dispose();
        }
        Raise(nameof(ActivityText));
        Raise(nameof(ActiveForegroundTask));
        Raise(nameof(IsMainWindowTaskLocked));
        Raise(nameof(IsFullApplicationTaskLocked));
        Raise(nameof(IsForegroundTaskRunning));
        Raise(nameof(CanEditProject));
        Raise(nameof(CanStartForegroundTask));
        Raise(nameof(CanRunProjectTask));
        Raise(nameof(CanSaveProject));
        Raise(nameof(CanUseContextMenus));
        Raise(nameof(CanNavigateBack));
        Raise(nameof(CanNavigateForward));
        return task;
    }

    public void ReportTask(DesktopTaskViewModel task, string detail, double? progress = null)
    {
        if (!TaskHistory.Contains(task)) return;
        task.Report(detail, progress);
        SetStatusMessage(detail);
        Raise(nameof(ActivityText));
        Raise(nameof(ActiveForegroundTask));
        Raise(nameof(IsMainWindowTaskLocked));
        Raise(nameof(IsFullApplicationTaskLocked));
    }

    public void CompleteTask(DesktopTaskViewModel task, string status, string detail = "")
    {
        if (!TaskHistory.Contains(task)) return;
        task.Complete(status, detail);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            SetStatusMessage(detail, status == "Failed");
        }
        Raise(nameof(ActivityText));
        Raise(nameof(ActiveForegroundTask));
        Raise(nameof(IsMainWindowTaskLocked));
        Raise(nameof(IsFullApplicationTaskLocked));
        Raise(nameof(IsForegroundTaskRunning));
        Raise(nameof(CanEditProject));
        Raise(nameof(CanStartForegroundTask));
        Raise(nameof(CanRunProjectTask));
        Raise(nameof(CanSaveProject));
        Raise(nameof(CanUseContextMenus));
    }

    public void ApplyApplicationPreferences(ApplicationPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        preferences.Validate();
        if (_context is null)
        {
            return;
        }
        if (IsForegroundTaskRunning || IsPlaybackActive)
        {
            throw new InvalidOperationException(
                "Application Preferences can only change while playback is stopped and no foreground task is running.");
        }

        PlaybackController? previousPlayback = _context.Playback;
        if (previousPlayback is not null)
        {
            previousPlayback.StateChanged -= OnPlaybackStateChanged;
        }
        _context.ReconfigurePlaybackServices(preferences);
        if (_context.Playback is not null)
        {
            foreach (MidoraId trackId in _mutedTrackIds) _context.Playback.SetTrackMuted(trackId, true);
            foreach (MidoraId trackId in _soloTrackIds) _context.Playback.SetTrackSolo(trackId, true);
            _context.Playback.StateChanged += OnPlaybackStateChanged;
        }
        RefreshProperties();
    }

    public void NavigateToDiagnostic(DiagnosticRow diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        if (Project is null) return;
        SourceReference source = diagnostic.SourceReference;
        if (source.SegmentId != default)
        {
            (LogicalTrack Track, Segment Segment)? located = TimelineWorkspaceViewModel.FindSegment(Project, source.SegmentId);
            if (located is null)
            {
                SetStatusMessage("The diagnostic references a Segment that no longer exists.", isError: true);
                return;
            }
            TimelineWorkspaceViewModel workspace = OpenSegment(source.SegmentId);
            MidoraId target = source.LogicalNoteId != default
                && located.Value.Segment.Notes.Any(item => item.Id == source.LogicalNoteId)
                    ? source.LogicalNoteId
                    : source.SegmentId;
            workspace.Selection.Replace(target);
            workspace.EditCursorTick = Math.Max(0, source.Tick);
            RefreshWorkspace(workspace);
            return;
        }
        if (source.EventInstrumentId != default)
        {
            if (source.MappingFunctionId != default)
            {
                OpenMappingFunction(source.EventInstrumentId, source.MappingFunctionId);
                return;
            }
            InstrumentWorkspaceViewModel workspace = OpenInstrument(source.EventInstrumentId);
            MidoraId target = source.SourceEventId != default ? source.SourceEventId : source.EventInstrumentId;
            workspace.Selection.Replace(target);
            RefreshWorkspace(workspace);
            return;
        }
        if (source.TrackId != default)
        {
            OpenArrangement();
            return;
        }
        if (source.SourceEventId == default && source.Tick < 0)
        {
            SetStatusMessage("This diagnostic has no navigable Project source.", isError: true);
            return;
        }
        TimelineWorkspaceViewModel conductor = (TimelineWorkspaceViewModel)OpenWorkspace(
            ProjectTree.First(item => item.Kind == ProjectTreeNodeKind.Conductor));
        MidoraId conductorId = source.SourceEventId;
        if (conductorId != default)
        {
            conductor.Selection.Replace(conductorId);
            RefreshWorkspace(conductor);
        }
        conductor.EditCursorTick = source.Tick >= 0 ? source.Tick : conductor.EditCursorTick;
    }

    public void RenameProjectTreeNode(ProjectTreeNode node, string name)
    {
        ArgumentNullException.ThrowIfNull(node);
        MidoraId id = node.ObjectId
            ?? throw new InvalidOperationException("This Project node cannot be renamed.");
        IProjectEditCommand command = node.Kind switch
        {
            ProjectTreeNodeKind.LogicalTrack => ProjectDomainEditCommands.RenameLogicalTrack(id, name),
            ProjectTreeNodeKind.EventInstrument => ProjectDomainEditCommands.RenameEventInstrument(id, name),
            ProjectTreeNodeKind.InstrumentFolder => ProjectDomainEditCommands.RenameEventInstrumentFolder(id, name),
            _ => throw new InvalidOperationException("This Project node cannot be renamed.")
        };
        Execute(command);
    }

    public void MoveProjectTreeNode(ProjectTreeNode node, int direction)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (Project is null || direction == 0) return;
        MidoraId id = node.ObjectId
            ?? throw new InvalidOperationException("This Project node cannot be reordered.");
        IProjectEditCommand command = node.Kind switch
        {
            ProjectTreeNodeKind.LogicalTrack => ProjectDomainEditCommands.ReorderLogicalTrack(
                id,
                Math.Clamp(Project.Tracks.FindIndex(item => item.Id == id) + Math.Sign(direction), 0, Project.Tracks.Count - 1)),
            ProjectTreeNodeKind.EventInstrument => ProjectDomainEditCommands.ReorderEventInstrument(
                id,
                Math.Clamp(Project.EventInstruments.FindIndex(item => item.Id == id) + Math.Sign(direction), 0, Project.EventInstruments.Count - 1)),
            ProjectTreeNodeKind.InstrumentFolder => ProjectDomainEditCommands.ReorderEventInstrumentFolder(
                id,
                Math.Clamp(Project.EventInstrumentFolders.FindIndex(item => item.Id == id) + Math.Sign(direction), 0, Project.EventInstrumentFolders.Count - 1)),
            _ => throw new InvalidOperationException("This Project node cannot be reordered.")
        };
        Execute(command);
    }

    public void ReorderProjectTreeNode(ProjectTreeNode node, int newIndex)
    {
        ArgumentNullException.ThrowIfNull(node);
        MidoraId id = node.ObjectId
            ?? throw new InvalidOperationException("This Project node cannot be reordered.");
        IProjectEditCommand command = node.Kind switch
        {
            ProjectTreeNodeKind.LogicalTrack => ProjectDomainEditCommands.ReorderLogicalTrack(id, newIndex),
            ProjectTreeNodeKind.EventInstrument => ProjectDomainEditCommands.ReorderEventInstrument(id, newIndex),
            ProjectTreeNodeKind.InstrumentFolder => ProjectDomainEditCommands.ReorderEventInstrumentFolder(id, newIndex),
            _ => throw new InvalidOperationException("This Project node cannot be reordered.")
        };
        Execute(command);
    }

    public void DeleteProjectTreeNode(ProjectTreeNode node, bool confirmed)
    {
        ArgumentNullException.ThrowIfNull(node);
        MidoraId id = node.ObjectId
            ?? throw new InvalidOperationException("This Project node cannot be deleted.");
        IProjectEditCommand command = node.Kind switch
        {
            ProjectTreeNodeKind.LogicalTrack => ProjectDomainEditCommands.DeleteLogicalTrack(id, confirmed),
            ProjectTreeNodeKind.EventInstrument => ProjectDomainEditCommands.DeleteEventInstrument(id, confirmed),
            ProjectTreeNodeKind.InstrumentFolder => ProjectDomainEditCommands.DeleteEventInstrumentFolder(id),
            ProjectTreeNodeKind.DamagedEventInstrument => ProjectDomainEditCommands.DeleteDamagedEventInstrument(id),
            ProjectTreeNodeKind.DamagedLogicalTrack => ProjectDomainEditCommands.DeleteDamagedLogicalTrack(id),
            _ => throw new InvalidOperationException("This Project node cannot be deleted.")
        };
        Execute(command);
    }

    public void BindLogicalTrack(MidoraId trackId, MidoraId? eventInstrumentId) =>
        Execute(ProjectDomainEditCommands.BindLogicalTrack(trackId, eventInstrumentId));

    public void Undo()
    {
        if (!CanEditProject) return;
        if (Document?.CanUndo != true) return;
        Document.Undo();
    }

    public void Redo()
    {
        if (!CanEditProject) return;
        if (Document?.CanRedo != true) return;
        Document.Redo();
    }

    public WorkspaceViewModel OpenWorkspace(ProjectTreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        WorkspaceViewModel workspace = node.Kind switch
        {
            ProjectTreeNodeKind.Conductor => GetOrCreate(
                WorkspaceKey.ForType(WorkspaceKind.ConductorTrack),
                () => new TimelineWorkspaceViewModel(
                    WorkspaceKey.ForType(WorkspaceKind.ConductorTrack),
                    "Conductor Track",
                    TimelineWorkspaceMode.Conductor,
                    ArrangementEditorSettings)),
            ProjectTreeNodeKind.InstrumentLibrary => GetOrCreate(
                WorkspaceKey.ForType(WorkspaceKind.EventInstrumentLibrary),
                static () => new LibraryWorkspaceViewModel()),
            ProjectTreeNodeKind.EventInstrument when node.ObjectId is MidoraId id => GetOrCreate(
                WorkspaceKey.ForObject(WorkspaceKind.EventInstrumentEditor, id),
                () => new InstrumentWorkspaceViewModel(id, node.Title, PianoRollEditorSettings)),
            ProjectTreeNodeKind.LogicalTracks or ProjectTreeNodeKind.LogicalTrack => OpenArrangement(),
            ProjectTreeNodeKind.ProjectSettings => GetOrCreate(
                WorkspaceKey.ForType(WorkspaceKind.ProjectSettings),
                static () => new SettingsWorkspaceViewModel()),
            ProjectTreeNodeKind.Diagnostics => GetOrCreate(
                WorkspaceKey.ForType(WorkspaceKind.Diagnostics),
                static () => new DiagnosticsWorkspaceViewModel()),
            ProjectTreeNodeKind.DamagedEventInstrument or ProjectTreeNodeKind.DamagedLogicalTrack =>
                throw new InvalidOperationException(
                    "Damaged placeholders cannot be opened or edited. Review the error tooltip, then explicitly delete the placeholder or discard this Project session."),
            _ => throw new InvalidOperationException("This Project tree node has no Workspace.")
        };
        ActiveWorkspace = workspace;
        return workspace;
    }

    public TimelineWorkspaceViewModel OpenArrangement()
    {
        TimelineWorkspaceViewModel workspace = (TimelineWorkspaceViewModel)GetOrCreate(
            WorkspaceKey.ForType(WorkspaceKind.Arrangement),
            () => new TimelineWorkspaceViewModel(
                WorkspaceKey.ForType(WorkspaceKind.Arrangement),
                "Arrangement",
                TimelineWorkspaceMode.Arrangement,
                ArrangementEditorSettings));
        ActiveWorkspace = workspace;
        return workspace;
    }

    public TimelineWorkspaceViewModel OpenSegment(MidoraId segmentId)
    {
        TimelineWorkspaceViewModel workspace = (TimelineWorkspaceViewModel)GetOrCreate(
            WorkspaceKey.ForObject(WorkspaceKind.SegmentEditor, segmentId),
            () => new TimelineWorkspaceViewModel(
                WorkspaceKey.ForObject(WorkspaceKind.SegmentEditor, segmentId),
                "Segment",
                TimelineWorkspaceMode.Segment,
                PianoRollEditorSettings));
        ActiveWorkspace = workspace;
        return workspace;
    }

    public InstrumentWorkspaceViewModel OpenInstrument(MidoraId instrumentId)
    {
        EventInstrument instrument = Project?.EventInstruments.FirstOrDefault(item => item.Id == instrumentId)
            ?? throw new InvalidOperationException("The Event Instrument no longer exists.");
        InstrumentWorkspaceViewModel workspace = (InstrumentWorkspaceViewModel)GetOrCreate(
            WorkspaceKey.ForObject(WorkspaceKind.EventInstrumentEditor, instrumentId),
            () => new InstrumentWorkspaceViewModel(instrumentId, instrument.Name, PianoRollEditorSettings));
        ActiveWorkspace = workspace;
        return workspace;
    }

    public MappingFunctionWorkspaceViewModel OpenMappingFunction(MidoraId instrumentId, MidoraId functionId)
    {
        EventInstrument instrument = Project?.EventInstruments.FirstOrDefault(item => item.Id == instrumentId)
            ?? throw new InvalidOperationException("The Event Instrument no longer exists.");
        CSharpMappingFunction function = instrument.MappingFunctions.FirstOrDefault(item => item.Id == functionId)
            ?? throw new InvalidOperationException("The Mapping Function no longer exists.");
        MappingFunctionWorkspaceViewModel workspace = (MappingFunctionWorkspaceViewModel)GetOrCreate(
            WorkspaceKey.ForObject(WorkspaceKind.MappingFunctionEditor, functionId),
            () => new MappingFunctionWorkspaceViewModel(instrumentId, functionId, function.Name));
        ActiveWorkspace = workspace;
        return workspace;
    }

    public CSharpMappingDraftCompilationResult CompileMappingDraft(MappingFunctionWorkspaceViewModel workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        EventInstrument instrument = Project?.EventInstruments.FirstOrDefault(item => item.Id == workspace.InstrumentId)
            ?? throw new InvalidOperationException("The Event Instrument no longer exists.");
        CSharpMappingFunction function = instrument.MappingFunctions.FirstOrDefault(item => item.Id == workspace.ObjectId)
            ?? throw new InvalidOperationException("The Mapping Function no longer exists.");
        CSharpMappingDraftCompilationResult result = _mappingDraftCompiler.Compile(
            function.AbiVersion,
            workspace.DraftBody,
            workspace.ParseDeclaredContextFields());
        workspace.SetValidationStatus(result.Succeeded
            ? "Draft compiled successfully"
            : result.ErrorMessage ?? "Draft compilation failed");
        return result;
    }

    public bool ApplyMappingDraft(MappingFunctionWorkspaceViewModel workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        CSharpMappingDraftCompilationResult validation = CompileMappingDraft(workspace);
        if (!validation.Succeeded) return false;
        Execute(ProjectDomainEditCommands.UpdateMappingFunction(
            workspace.InstrumentId,
            workspace.ObjectId!.Value,
            workspace.DraftName,
            workspace.DraftBody,
            workspace.ParseDeclaredContextFields()));
        workspace.MarkApplied();
        RefreshWorkspace(workspace);
        return true;
    }

    public void CloseWorkspace(WorkspaceViewModel workspace)
    {
        int index = Workspaces.IndexOf(workspace);
        if (index < 0) return;
        Workspaces.RemoveAt(index);
        _backNavigation.RemoveAll(key => key == workspace.Key);
        _forwardNavigation.RemoveAll(key => key == workspace.Key);
        if (ReferenceEquals(ActiveWorkspace, workspace))
        {
            ActiveWorkspace = Workspaces.Count == 0
                ? null
                : Workspaces[Math.Clamp(index - 1, 0, Workspaces.Count - 1)];
        }
        Raise(nameof(CanNavigateBack));
        Raise(nameof(CanNavigateForward));
    }

    public void ReorderWorkspace(WorkspaceViewModel workspace, int newIndex)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        int oldIndex = Workspaces.IndexOf(workspace);
        if (oldIndex < 0)
        {
            throw new InvalidOperationException("The Workspace is no longer open.");
        }
        if (newIndex < 0 || newIndex >= Workspaces.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(newIndex));
        }
        if (oldIndex != newIndex) Workspaces.Move(oldIndex, newIndex);
        ActiveWorkspace = workspace;
    }

    public void RefreshWorkspace(WorkspaceViewModel workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (Project is null || !Workspaces.Contains(workspace))
        {
            return;
        }
        PrepareWorkspaceRuntimeState(workspace);
        workspace.Rebuild(Project, _revision);
        workspace.RefreshSelectionPresentation();
        if (workspace is TimelineWorkspaceViewModel timeline)
        {
            timeline.UpdatePlaybackCursor(Project, CurrentTick);
        }
        if (workspace is not DiagnosticsWorkspaceViewModel)
        {
            if (ReferenceEquals(workspace, ActiveWorkspace)) _diagnosticScopeWorkspace = workspace;
            Workspaces.OfType<DiagnosticsWorkspaceViewModel>()
                .FirstOrDefault()?.SetScope(_diagnosticScopeWorkspace);
            BottomDiagnosticsViewModel.SetScope(_diagnosticScopeWorkspace);
        }
        if (ReferenceEquals(workspace, ActiveWorkspace)) RefreshInspector();
    }

    public void RefreshWorkspaceSelection(WorkspaceViewModel workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (Project is null || !Workspaces.Contains(workspace))
        {
            return;
        }
        workspace.RefreshSelectionPresentation();
        if (workspace is not DiagnosticsWorkspaceViewModel)
        {
            if (ReferenceEquals(workspace, ActiveWorkspace)) _diagnosticScopeWorkspace = workspace;
            Workspaces.OfType<DiagnosticsWorkspaceViewModel>()
                .FirstOrDefault()?.SetScope(_diagnosticScopeWorkspace);
            BottomDiagnosticsViewModel.SetScope(_diagnosticScopeWorkspace);
        }
        if (ReferenceEquals(workspace, ActiveWorkspace)) RefreshInspector();
    }

    public void SelectWorkspaceObject(WorkspaceViewModel workspace, MidoraId id)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (!Workspaces.Contains(workspace)) return;
        workspace.Selection.Replace(id);
        workspace.RefreshSelectionPresentation();
        if (ReferenceEquals(workspace, _diagnosticScopeWorkspace))
        {
            Workspaces.OfType<DiagnosticsWorkspaceViewModel>()
                .FirstOrDefault()?.SetScope(workspace);
            BottomDiagnosticsViewModel.SetScope(workspace);
        }
        if (ReferenceEquals(workspace, ActiveWorkspace)) RefreshInspector();
    }

    public void ActivateSubVoiceEditor(InstrumentWorkspaceViewModel workspace, MidoraId subVoiceId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        EventInstrument? instrument = Project?.EventInstruments
            .FirstOrDefault(value => value.Id == workspace.ObjectId);
        if (!Workspaces.Contains(workspace)
            || instrument?.SubVoices.Any(value => value.Id == subVoiceId) != true)
        {
            return;
        }
        SelectWorkspaceObject(workspace, subVoiceId);
        RefreshWorkspace(workspace);
        workspace.ActiveSectionIndex = 1;
    }

    public async Task CloseProjectAsync()
    {
        ProjectContext? previous = _context;
        if (previous is null) return;
        Unsubscribe(previous);
        _context = null;
        TimelineRasterCacheSession.Clear();
        Workspaces.Clear();
        _backNavigation.Clear();
        _forwardNavigation.Clear();
        _diagnosticScopeWorkspace = null;
        ProjectTree.Clear();
        CompilerDiagnostics.Clear();
        SelectedDiagnostic = null;
        SelectedBottomDiagnostic = null;
        BottomDiagnosticsViewModel.Replace(Array.Empty<DiagnosticRow>());
        BottomDiagnosticsViewModel.SetScope(null);
        _mappingDraftCompiler.Clear();
        _mutedTrackIds.Clear();
        _soloTrackIds.Clear();
        foreach (DesktopTaskViewModel task in TaskHistory) task.Dispose();
        TaskHistory.Clear();
        ActiveWorkspace = null;
        _revision = 0;
        _timeSignatureMap = null;
        SetStatusMessage(null);
        RefreshProperties();
        await previous.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await CloseProjectAsync();
        _mappingDraftCompiler.Dispose();
    }

    private async Task ActivateAsync(ProjectContext next)
    {
        _mappingDraftCompiler.Clear();
        _mutedTrackIds.Clear();
        _soloTrackIds.Clear();
        ProjectContext? previous = _context;
        if (previous is not null)
        {
            Unsubscribe(previous);
        }
        _context = next;
        Subscribe(next);
        TimelineRasterCacheSession.Clear();
        Workspaces.Clear();
        _backNavigation.Clear();
        _forwardNavigation.Clear();
        ActiveWorkspace = null;
        _revision = 1;
        _timeSignatureMap = new(Project!);
        ArrangementEditorSettings.Reset(arrangement: true, Project!.TicksPerQuarterNote);
        PianoRollEditorSettings.Reset(arrangement: false, Project.TicksPerQuarterNote);
        ArrangementEditorSettings.ConfigureProject(Project, referenceTick: 0);
        PianoRollEditorSettings.ConfigureProject(Project, referenceTick: 0);
        SetStatusMessage(null);
        RefreshAll();
        OpenArrangement();
        AudioCacheWarning cacheWarning = next.Compilation.AudioCacheWarning;
        if (cacheWarning.Code != AudioCacheWarningCode.None)
        {
            SetStatusMessage("Audio cache warning: " + cacheWarning.Message);
        }
        if (previous is not null)
        {
            await previous.DisposeAsync();
        }
    }

    private WorkspaceViewModel GetOrCreate(
        WorkspaceKey key,
        Func<WorkspaceViewModel> factory)
    {
        WorkspaceViewModel? existing = Workspaces.FirstOrDefault(item => item.Key == key);
        if (existing is not null)
        {
            return existing;
        }
        WorkspaceViewModel created = factory();
        if (Project is not null)
        {
            PrepareWorkspaceRuntimeState(created);
            created.Rebuild(Project, _revision);
            if (created is TimelineWorkspaceViewModel timeline)
            {
                timeline.UpdatePlaybackCursor(Project, CurrentTick);
            }
            if (created is DiagnosticsWorkspaceViewModel diagnostics)
            {
                diagnostics.Replace(CompilerDiagnostics);
                diagnostics.SetScope(_diagnosticScopeWorkspace);
            }
        }
        Workspaces.Add(created);
        return created;
    }

    private void RefreshAll()
    {
        _timeSignatureMap = Project is null ? null : new ProjectTimeSignatureMap(Project);
        if (Project is not null)
        {
            ArrangementEditorSettings.ConfigureProject(Project, CurrentTick);
            PianoRollEditorSettings.ConfigureProject(Project, CurrentTick);
        }
        RefreshDiagnostics();
        RefreshProjectTree();
        if (Project is not null)
        {
            foreach (WorkspaceViewModel workspace in Workspaces.ToArray())
            {
                if (!ObjectStillExists(workspace))
                {
                    CloseWorkspace(workspace);
                    continue;
                }
                PrepareWorkspaceRuntimeState(workspace);
                workspace.Rebuild(Project, _revision);
                if (workspace is TimelineWorkspaceViewModel timeline)
                {
                    timeline.UpdatePlaybackCursor(Project, CurrentTick);
                }
                if (workspace is DiagnosticsWorkspaceViewModel diagnostics)
                {
                    diagnostics.Replace(CompilerDiagnostics);
                }
            }
        }
        RefreshProperties();
        RefreshInspector();
    }

    private void RefreshChanged(ProjectContentChangedEventArgs changes)
    {
        if (Project is null) return;
        if (changes.IsEmpty)
        {
            RefreshAll();
            return;
        }
        if (changes.AffectsEverything || changes.AffectsConductor)
        {
            _timeSignatureMap = new ProjectTimeSignatureMap(Project);
        }
        ArrangementEditorSettings.ConfigureProject(Project, CurrentTick);
        PianoRollEditorSettings.ConfigureProject(Project, CurrentTick);
        RefreshDiagnostics();
        RefreshProjectTree();
        foreach (WorkspaceViewModel workspace in Workspaces.ToArray())
        {
            if (!ObjectStillExists(workspace))
            {
                CloseWorkspace(workspace);
                continue;
            }
            if (workspace is DiagnosticsWorkspaceViewModel diagnostics)
            {
                diagnostics.Replace(CompilerDiagnostics);
                continue;
            }
            if (!WorkspaceAffected(workspace, changes)) continue;
            PrepareWorkspaceRuntimeState(workspace);
            workspace.Rebuild(Project, _revision);
            if (workspace is TimelineWorkspaceViewModel timeline)
            {
                timeline.UpdatePlaybackCursor(Project, CurrentTick);
            }
        }
        RefreshProperties();
        RefreshInspector();
    }

    private bool WorkspaceAffected(
        WorkspaceViewModel workspace,
        ProjectContentChangedEventArgs changes)
    {
        if (changes.AffectsEverything) return true;
        HashSet<MidoraId> trackIds = changes.TrackIds.ToHashSet();
        HashSet<MidoraId> instrumentIds = changes.EventInstrumentIds.ToHashSet();
        return workspace.Kind switch
        {
            WorkspaceKind.Arrangement => changes.AffectsConductor
                || trackIds.Count != 0
                || instrumentIds.Count != 0,
            WorkspaceKind.ConductorTrack => changes.AffectsConductor,
            WorkspaceKind.SegmentEditor => workspace.ObjectId is MidoraId segmentId
                && TimelineWorkspaceViewModel.FindSegment(Project!, segmentId) is { } located
                && (trackIds.Contains(located.Track.Id)
                    || located.Track.EventInstrumentId is MidoraId instrumentId
                       && instrumentIds.Contains(instrumentId)),
            WorkspaceKind.EventInstrumentLibrary => instrumentIds.Count != 0,
            WorkspaceKind.EventInstrumentEditor => workspace.ObjectId is MidoraId eventInstrumentId
                && instrumentIds.Contains(eventInstrumentId),
            WorkspaceKind.MappingFunctionEditor => workspace.ObjectId is MidoraId mappingId
                && Project!.EventInstruments.Any(instrument =>
                    instrumentIds.Contains(instrument.Id)
                    && instrument.MappingFunctions.Any(mapping => mapping.Id == mappingId)),
            WorkspaceKind.ProjectSettings => true,
            WorkspaceKind.Diagnostics => false,
            _ => false
        };
    }

    private void RefreshInspector() => InspectorProjection.Rebuild(Inspector, Project, ActiveWorkspace);

    private void PrepareWorkspaceRuntimeState(WorkspaceViewModel workspace)
    {
        if (workspace is TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Arrangement } timeline)
        {
            timeline.SetTrackMonitoringStates(_mutedTrackIds, _soloTrackIds);
        }
    }

    private void RefreshProjectTree()
    {
        ProjectTree.Clear();
        if (Project is null) return;

        string query = ProjectTreeSearchText.Trim();
        bool filtered = query.Length != 0;
        if (!filtered)
        {
            ProjectTree.Add(new(ProjectTreeNodeKind.Conductor, "Conductor Track"));
        }
        ProjectTreeNode library = new(ProjectTreeNodeKind.InstrumentLibrary, "Event Instrument Library");
        foreach (EventInstrumentLibraryFolder folder in Project.EventInstrumentFolders)
        {
            EventInstrument[] instruments = Project.EventInstruments
                .Where(item => item.LibraryFolderId == folder.Id)
                .ToArray();
            bool folderMatches = MatchesProjectTreeFilter(folder.Name, query);
            ProjectTreeNode folderNode = new(ProjectTreeNodeKind.InstrumentFolder, folder.Name, folder.Id);
            foreach (EventInstrument instrument in instruments.Where(item =>
                         folderMatches || MatchesProjectTreeFilter(item.Name, query)))
            {
                folderNode.Children.Add(new(ProjectTreeNodeKind.EventInstrument, instrument.Name, instrument.Id));
            }
            if (!filtered || folderMatches || folderNode.Children.Count != 0) library.Children.Add(folderNode);
        }
        foreach (EventInstrument instrument in Project.EventInstruments.Where(item =>
                     item.LibraryFolderId is null && MatchesProjectTreeFilter(item.Name, query)))
        {
            library.Children.Add(new(ProjectTreeNodeKind.EventInstrument, instrument.Name, instrument.Id));
        }
        foreach (DamagedProjectObject damaged in Project.DamagedEventInstruments
                     .Where(item => MatchesProjectTreeFilter(item.NameSnapshot, query))
                     .OrderBy(item => item.OriginalIndex)
                     .ThenBy(item => item.Id))
        {
            ProjectTreeNode node = CreateDamagedNode(ProjectTreeNodeKind.DamagedEventInstrument, damaged);
            library.Children.Insert(Math.Clamp(damaged.OriginalIndex, 0, library.Children.Count), node);
        }
        if (!filtered || library.Children.Count != 0) ProjectTree.Add(library);

        ProjectTreeNode tracks = new(ProjectTreeNodeKind.LogicalTracks, "Logical Tracks");
        foreach (LogicalTrack track in Project.Tracks)
        {
            EventInstrument? bound = track.EventInstrumentId is MidoraId instrumentId
                ? Project.EventInstruments.FirstOrDefault(item => item.Id == instrumentId)
                : null;
            string boundName = bound is null
                ? track.LastBoundEventInstrumentName ?? string.Empty
                : bound.Name;
            if (!MatchesProjectTreeFilter(track.Name, query)
                && !MatchesProjectTreeFilter(boundName, query))
            {
                continue;
            }
            tracks.Children.Add(new(
                ProjectTreeNodeKind.LogicalTrack,
                TimelineWorkspaceViewModel.TrackDisplayName(Project, track),
                track.Id,
                bound is null
                    ? "Unbound"
                    : string.IsNullOrWhiteSpace(bound.Name) ? "Unnamed instrument" : bound.Name));
        }
        foreach (DamagedProjectObject damaged in Project.DamagedLogicalTracks
                     .Where(item => MatchesProjectTreeFilter(item.NameSnapshot, query))
                     .OrderBy(item => item.OriginalIndex)
                     .ThenBy(item => item.Id))
        {
            ProjectTreeNode node = CreateDamagedNode(ProjectTreeNodeKind.DamagedLogicalTrack, damaged);
            tracks.Children.Insert(Math.Clamp(damaged.OriginalIndex, 0, tracks.Children.Count), node);
        }
        if (!filtered || tracks.Children.Count != 0) ProjectTree.Add(tracks);
        if (!filtered)
        {
            ProjectTree.Add(new(ProjectTreeNodeKind.ProjectSettings, "Project Settings"));
            ProjectTree.Add(new(ProjectTreeNodeKind.Diagnostics, $"Diagnostics ({IssueSummary})"));
        }
    }

    private static bool MatchesProjectTreeFilter(string? value, string query) =>
        query.Length == 0 || (value?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);

    private void RefreshDiagnosticProjectTreeNode()
    {
        ProjectTreeNode? node = ProjectTree.FirstOrDefault(item => item.Kind == ProjectTreeNodeKind.Diagnostics);
        if (node is not null)
        {
            node.Title = $"Diagnostics ({IssueSummary})";
        }
    }

    private static ProjectTreeNode CreateDamagedNode(
        ProjectTreeNodeKind kind,
        DamagedProjectObject damaged)
    {
        string name = string.IsNullOrWhiteSpace(damaged.NameSnapshot)
            ? Path.GetFileNameWithoutExtension(damaged.PackagePath)
            : damaged.NameSnapshot;
        return new(kind, $"[Damaged] {name}", damaged.Id, damaged.Error);
    }

    private void RefreshDiagnostics()
    {
        lock (_modelRefreshGate)
        {
            DiagnosticRow[] diagnostics = _context is null
                ? []
                : _context.Compilation.LastAttempt.Diagnostics
                    .Select(diagnostic => DiagnosticProjection.FromCompiler(
                        diagnostic,
                        _context.Compilation.IsCompilationCurrent))
                    .ToArray();
            CompilerDiagnostics.Clear();
            foreach (DiagnosticRow diagnostic in diagnostics)
            {
                CompilerDiagnostics.Add(diagnostic);
            }
            _compilerErrorCount = diagnostics.Count(item => item.Severity == "Error");
            _compilerWarningCount = diagnostics.Count(item => item.Severity == "Warning");
            BottomDiagnosticsViewModel.Replace(diagnostics);
            BottomDiagnosticsViewModel.SetScope(_diagnosticScopeWorkspace);
        }
    }

    private bool ObjectStillExists(WorkspaceViewModel workspace)
    {
        if (Project is null || workspace.ObjectId is not MidoraId id) return true;
        return workspace.Kind switch
        {
            WorkspaceKind.SegmentEditor => TimelineWorkspaceViewModel.FindSegment(Project, id) is not null,
            WorkspaceKind.EventInstrumentEditor => Project.EventInstruments.Any(item => item.Id == id),
            WorkspaceKind.MappingFunctionEditor => Project.EventInstruments.Any(
                instrument => instrument.MappingFunctions.Any(function => function.Id == id)),
            _ => true
        };
    }

    private void RefreshProperties()
    {
        RefreshTimelinePlaybackCursors();
        Raise(nameof(HasProject));
        Raise(nameof(Document));
        Raise(nameof(Project));
        Raise(nameof(Persistence));
        Raise(nameof(WindowTitle));
        Raise(nameof(ProjectDisplayName));
        Raise(nameof(TitleBarProjectDisplayName));
        Raise(nameof(ProjectState));
        Raise(nameof(CompileState));
        Raise(nameof(SoundFontState));
        Raise(nameof(PositionText));
        Raise(nameof(CurrentTick));
        Raise(nameof(TempoText));
        Raise(nameof(PlaybackState));
        Raise(nameof(IsPlaybackActive));
        Raise(nameof(CanPlayback));
        Raise(nameof(CanPreview));
        Raise(nameof(PlaybackUnavailableReason));
        Raise(nameof(ErrorCount));
        Raise(nameof(WarningCount));
        Raise(nameof(IssueSummary));
        Raise(nameof(ActivityText));
        Raise(nameof(ActiveForegroundTask));
        Raise(nameof(IsMainWindowTaskLocked));
        Raise(nameof(IsFullApplicationTaskLocked));
        Raise(nameof(IsForegroundTaskRunning));
        Raise(nameof(CanEditProject));
        Raise(nameof(CanStartForegroundTask));
        Raise(nameof(CanRunProjectTask));
        Raise(nameof(HasDamagedProjectObjects));
        Raise(nameof(CanSaveProject));
        Raise(nameof(CanUseContextMenus));
    }

    private void RefreshTimelinePlaybackCursors()
    {
        if (Project is null)
        {
            return;
        }
        long currentTick = CurrentTick;
        foreach (TimelineWorkspaceViewModel timeline in Workspaces.OfType<TimelineWorkspaceViewModel>())
        {
            timeline.UpdatePlaybackCursor(Project, currentTick);
        }
    }

    private void Subscribe(ProjectContext context)
    {
        context.Document.HistoryChanged += OnDocumentHistoryChanged;
        context.Document.ContentChanged += OnDocumentContentChanged;
        context.Compilation.CompilationChanged += OnCompilationChanged;
        if (context.Playback is not null)
        {
            context.Playback.StateChanged += OnPlaybackStateChanged;
        }
    }

    private void Unsubscribe(ProjectContext context)
    {
        context.Document.HistoryChanged -= OnDocumentHistoryChanged;
        context.Document.ContentChanged -= OnDocumentContentChanged;
        context.Compilation.CompilationChanged -= OnCompilationChanged;
        if (context.Playback is not null)
        {
            context.Playback.StateChanged -= OnPlaybackStateChanged;
        }
    }

    private void OnDocumentHistoryChanged(object? sender, EventArgs e) => DispatchModelRefresh(() =>
    {
        RefreshProperties();
    });

    private void OnDocumentContentChanged(
        object? sender,
        ProjectContentChangedEventArgs e) => DispatchModelRefresh(() =>
    {
        _revision++;
        RefreshChanged(e);
    });

    private void OnCompilationChanged(object? sender, EventArgs e) => DispatchModelRefresh(() =>
    {
        RefreshDiagnostics();
        RefreshDiagnosticProjectTreeNode();
        RefreshProperties();
    });

    private void OnPlaybackStateChanged(object? sender, EventArgs e) =>
        DispatchModelRefresh(() =>
        {
            if (sender is PlaybackController
                {
                    State: PlaybackState.Error,
                    LastError: Exception failure
                })
            {
                SetStatusMessage($"Playback failed: {failure.Message}", isError: true);
            }
            else if (_context?.Compilation.AudioCacheWarning is
                { Code: not AudioCacheWarningCode.None } warning)
            {
                SetStatusMessage("Audio cache warning: " + warning.Message);
            }
            RefreshProperties();
        });

    private void DispatchModelRefresh(Action refresh)
    {
        ArgumentNullException.ThrowIfNull(refresh);
        if (_uiContext is null)
        {
            lock (_modelRefreshGate)
            {
                refresh();
            }
            return;
        }

        // ProjectDocumentSession publishes notifications synchronously while its edit
        // transaction is still unwinding. Always queue the WPF projection refresh,
        // even when the edit originated on the Dispatcher thread. This keeps bound
        // ObservableCollections on their owning UI thread and prevents layout/input
        // re-entrancy from Click, Popup, and double-click routes.
        _uiContext.Post(static state => ((Action)state!).Invoke(), refresh);
    }

    private sealed class ProjectContext : IAsyncDisposable
    {
        private readonly IAsyncDisposable _owner;
        private readonly ProjectSoundFontResourceSession _soundFontResources;
        private readonly ProjectSoundFontRuntimeSession _soundFontRuntime;

        private ProjectContext(
            IAsyncDisposable owner,
            ProjectCompilationSession compilation,
            ProjectDocumentSession document,
            ProjectPersistenceCoordinator persistence,
            ProjectSoundFontResourceSession soundFontResources,
            ProjectSoundFontEditing soundFontEditing,
            ProjectSoundFontRuntimeSession soundFontRuntime,
            PlaybackController? playback,
            ApplicationTaskCoordinator? tasks,
            string? playbackUnavailableReason)
        {
            _owner = owner;
            Compilation = compilation;
            Document = document;
            Persistence = persistence;
            _soundFontResources = soundFontResources;
            SoundFontEditing = soundFontEditing;
            _soundFontRuntime = soundFontRuntime;
            Playback = playback;
            Tasks = tasks;
            PlaybackUnavailableReason = playbackUnavailableReason;
        }

        public ProjectCompilationSession Compilation { get; }
        public ProjectDocumentSession Document { get; }
        public ProjectPersistenceCoordinator Persistence { get; }
        public ProjectSoundFontEditing SoundFontEditing { get; }
        public ProjectSoundFontResourceSession SoundFontResources => _soundFontResources;
        public PlaybackController? Playback { get; private set; }
        public ApplicationTaskCoordinator? Tasks { get; private set; }
        public string? PlaybackUnavailableReason { get; private set; }

        public static ProjectContext FromCreation(
            MidoraProjectPackageV1 packages,
            NewProjectCreationResult result)
        {
            ProjectCompilationSession compilation = new(
                result.Project,
                result.EffectiveSoundFontPath,
                executionMode: ProjectCompilationExecutionMode.Background);
            ProjectSoundFontResourceSession? resources = null;
            ProjectSoundFontEditing? editing = null;
            ProjectSoundFontRuntimeSession? runtime = null;
            try
            {
                ProjectDocumentSession document = new(compilation, result.Origin);
                resources = new(result.EmbeddedSoundFontResource);
                editing = new(
                    document,
                    new WorkerSoundFontLoadabilityValidator(),
                    resources);
                runtime = new(
                    compilation,
                    new WorkerSoundFontLoadabilityValidator());
                ProjectPersistenceCoordinator persistence = new(
                    document,
                    packages,
                    result.CurrentProjectPath,
                    result.FileInformation,
                    () => resources.CurrentEmbeddedResource);
                CreatePlaybackServices(
                    compilation,
                    null,
                    out PlaybackController? playback,
                    out ApplicationTaskCoordinator? tasks,
                    out string? playbackFailure);
                return new(
                    result,
                    compilation,
                    document,
                    persistence,
                    resources,
                    editing,
                    runtime,
                    playback,
                    tasks,
                    playbackFailure);
            }
            catch
            {
                runtime?.Dispose();
                editing?.Dispose();
                resources?.Dispose();
                compilation.Dispose();
                throw;
            }
        }

        public static ProjectContext FromOpenCandidate(ProjectOpenCandidate candidate)
        {
            ProjectCompilationSession compilation = new(
                candidate.Project,
                candidate.InitialSoundFontState.ResolvedAbsolutePath,
                executionMode: ProjectCompilationExecutionMode.Background);
            ProjectSoundFontResourceSession? resources = null;
            ProjectSoundFontEditing? editing = null;
            ProjectSoundFontRuntimeSession? runtime = null;
            try
            {
                ProjectDocumentSession document = candidate.CreateDocumentSession(compilation);
                resources = new(candidate.EmbeddedSoundFontResource);
                editing = new(
                    document,
                    new WorkerSoundFontLoadabilityValidator(),
                    resources);
                runtime = new(
                    compilation,
                    new WorkerSoundFontLoadabilityValidator());
                ProjectPersistenceCoordinator persistence = new(
                    document,
                    new MidoraProjectPackageV1(SoftwareVersion),
                    candidate.CurrentProjectPath,
                    candidate.FileInformation,
                    () => resources.CurrentEmbeddedResource);
                CreatePlaybackServices(
                    compilation,
                    null,
                    out PlaybackController? playback,
                    out ApplicationTaskCoordinator? tasks,
                    out string? playbackFailure);
                return new(
                    candidate,
                    compilation,
                    document,
                    persistence,
                    resources,
                    editing,
                    runtime,
                    playback,
                    tasks,
                    playbackFailure);
            }
            catch
            {
                runtime?.Dispose();
                editing?.Dispose();
                resources?.Dispose();
                compilation.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Tasks?.Dispose();
            Playback?.Dispose();
            _soundFontRuntime.Dispose();
            await SoundFontEditing.DisposeAsync();
            await _soundFontResources.DisposeAsync();
            Compilation.Dispose();
            await _owner.DisposeAsync();
        }

        public Task<ProjectSoundFontRuntimeSnapshot> RefreshSoundFontAsync(
            CancellationToken cancellationToken = default) =>
            _soundFontRuntime.RefreshAsync(
                Persistence.CurrentProjectPath,
                _soundFontResources.CurrentEmbeddedResource,
                cancellationToken: cancellationToken);

        public void ReconfigurePlaybackServices(ApplicationPreferences preferences)
        {
            ArgumentNullException.ThrowIfNull(preferences);
            Tasks?.Dispose();
            Playback?.Dispose();
            Tasks = null;
            Playback = null;
            CreatePlaybackServices(
                Compilation,
                preferences,
                out PlaybackController? playback,
                out ApplicationTaskCoordinator? tasks,
                out string? failure);
            Playback = playback;
            Tasks = tasks;
            PlaybackUnavailableReason = failure;
        }

        private static void CreatePlaybackServices(
            ProjectCompilationSession compilation,
            ApplicationPreferences? suppliedPreferences,
            out PlaybackController? playback,
            out ApplicationTaskCoordinator? tasks,
            out string? failure)
        {
            playback = null;
            tasks = null;
            ApplicationPreferences preferences = suppliedPreferences
                ?? new ApplicationPreferencesStore().Load().Preferences;
            compilation.ConfigureAudioCache(
                preferences.AudioCache.RootPath,
                preferences.AudioCache.MaximumReusableBytes);
            if (!FormalAudioWorkerLocator.TryLocate(
                    out string? workerPath,
                    out string? nativeDirectory,
                    out failure))
            {
                return;
            }
            try
            {
                RealtimeAudioPreferences audio = preferences.RealtimeAudio;
                BassWasapiChildPlaybackBackend backend = new(new(
                    workerPath!,
                    nativeDirectory!,
                    audio.PlaybackOutputDeviceId,
                    audio.RenderAheadMilliseconds,
                    audio.DeviceBufferRequestMilliseconds,
                    new BassMidiRendererSettings(
                        audio.MaximumSampleVoicesPerUnitStream,
                        Midora.Audio.InitialReleaseAudioRuntimePolicy.WorkFrameCount),
                    new AudioMasterSettings(-0.1f, 1f, 50f),
                    TimeSpan.FromSeconds(30)));
                playback = new(compilation, backend);
                playback.BeginDefaultPlaybackPreparation();
                tasks = new(compilation, playback);
                failure = null;
            }
            catch (Exception exception)
            {
                playback?.Dispose();
                playback = null;
                tasks = null;
                failure = exception.Message;
            }
        }
    }
}
