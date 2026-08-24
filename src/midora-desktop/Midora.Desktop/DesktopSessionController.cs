using System.Collections.ObjectModel;
using System.Globalization;
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
using Midora.Midi;

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
    private ApplicationPreferences _applicationPreferences =
        new ApplicationPreferencesStore().Load().Preferences;
    private readonly CSharpMappingDraftCompiler _mappingDraftCompiler = new();
    private readonly HashSet<MidoraId> _mutedTrackIds = [];
    private readonly HashSet<MidoraId> _soloTrackIds = [];
    private readonly HashSet<MidoraId> _mutedSharedGroupIds = [];
    private readonly HashSet<MidoraId> _soloSharedGroupIds = [];
    private readonly List<WorkspaceKey> _backNavigation = [];
    private readonly List<WorkspaceKey> _forwardNavigation = [];
    private readonly Dictionary<long, Dictionary<WorkspaceKey, WorkspaceSelectionBookmark>>
        _workspaceSelectionHistory = [];
    private readonly SynchronizationContext? _uiContext =
        SynchronizationContext.Current is DispatcherSynchronizationContext dispatcherContext
            ? dispatcherContext
            : null;
    private ProjectContext? _context;
    private BassWasapiChildPlaybackBackend? _preparedPlaybackBackend;
    private WorkspaceViewModel? _activeWorkspace;
    private WorkspaceViewModel? _diagnosticScopeWorkspace;
    private long _revision;
    private string? _notice;
    private DiagnosticRow? _selectedDiagnostic;
    private ProjectTimeSignatureMap? _timeSignatureMap;
    private string _projectTreeSearchText = string.Empty;
    private bool _isNavigatingHistory;
    private string? _statusMessage;
    private string? _statusMessageDetails;
    private string? _statusMessageDetailsTitle;
    private bool _statusMessageIsError;
    private bool _isPlaybackStartPending;
    private long _displayCurrentTick;
    private TempoChange[] _orderedTempoChanges = [];
    private int _activeTempoIndex = -1;
    private long _tempoLookupTick = -1;
    private string _tempoText = "— BPM";
    private DesktopTaskViewModel? _foregroundTask;

    public DesktopSessionController()
    {
        _creation = new(_packages);
        _opening = new(_packages);
    }

    public bool HasProject => _context is not null;
    public bool IsForegroundTaskRunning => _foregroundTask?.IsRunning == true;
    public DesktopTaskViewModel? ActiveForegroundTask => IsForegroundTaskRunning
        ? _foregroundTask
        : null;
    public bool IsMainWindowTaskLocked => ActiveForegroundTask?.LockLevel >= DesktopTaskLockLevel.MainWindow;
    public bool IsFullApplicationTaskLocked => ActiveForegroundTask?.LockLevel >= DesktopTaskLockLevel.FullApplication;
    public bool CanEditProject => HasProject && !IsForegroundTaskRunning && !IsPlaybackActive;
    public bool CanStartForegroundTask => !IsForegroundTaskRunning && !IsPlaybackActive;
    public bool CanRunProjectTask => HasProject && CanStartForegroundTask;
    public bool HasDamagedProjectObjects => Project is not null
        && (Project.DamagedEventInstruments.Count != 0
            || Project.DamagedLogicalTracks.Count != 0
            || Project.DamagedMidiChannelRoots.Count != 0
            || Project.DamagedPureMidiTracks.Count != 0);
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
    public string SoundFontState => _applicationPreferences.GetEnabledSoundFontPaths().Length switch
    {
        0 => "No SoundFonts Enabled",
        1 => "1 SoundFont Enabled",
        int count => $"{count} SoundFonts Enabled"
    };
    public bool HasEnabledSoundFonts =>
        _applicationPreferences.GetEnabledSoundFontPaths().Length != 0;
    public bool IsTrackMuted(MidoraId trackId) => _mutedTrackIds.Contains(trackId);
    public bool IsTrackSolo(MidoraId trackId) => _soloTrackIds.Contains(trackId);
    public bool IsSharedGroupMuted(MidoraId sharedGroupId) =>
        _mutedSharedGroupIds.Contains(sharedGroupId);
    public bool IsSharedGroupSolo(MidoraId sharedGroupId) =>
        _soloSharedGroupIds.Contains(sharedGroupId);
    public long CurrentTick => _displayCurrentTick;
    public string TempoText => _tempoText;
    public string PositionText
    {
        get
        {
            if (_timeSignatureMap is null) return "—";
            ProjectMusicalPosition position = _timeSignatureMap.GetPosition(Math.Max(0, CurrentTick));
            int tickDigits = Math.Max(
                1,
                (Project?.TicksPerQuarterNote ?? 1)
                    .ToString(CultureInfo.InvariantCulture)
                    .Length);
            string tickOffset = position.TickOffset.ToString(
                $"D{tickDigits}",
                CultureInfo.InvariantCulture);
            return $"{position.Bar:D4} : {position.Beat:D2} : {tickOffset}";
        }
    }
    public PlaybackState PlaybackState => _context?.Playback?.State ?? PlaybackState.Stopped;
    public bool IsBuffering => PlaybackState == PlaybackState.Buffering;
    public string PlaybackStatusText
    {
        get
        {
            if (!IsBuffering)
            {
                return PlaybackState.ToString();
            }
            double? progress = _context?.Playback?.BufferingProgress;
            return progress.HasValue
                ? $"Buffering ({Math.Clamp((int)Math.Floor(progress.Value * 100d), 0, 100)}%)"
                : "Buffering";
        }
    }
    public bool IsPlaybackActive => PlaybackState is PlaybackState.Preparing
        or PlaybackState.Playing
        or PlaybackState.Buffering
        or PlaybackState.Stopping;
    public bool IsLoopEnabled => _context?.Playback?.LoopRange is not null;
    public bool CanPlayback => _context?.Playback is not null
        && _context.Compilation.EffectiveSoundFontPaths.Count != 0
        && _context.Compilation.CompilationState is not ProjectCompilationState.Failed
        && !_isPlaybackStartPending
        && !IsPlaybackActive;
    public bool CanTogglePlayback => IsPlaybackActive || CanPlayback;
    public string PrimaryTransportAction => IsPlaybackActive ? "Stop" : "Play";
    public string PrimaryTransportToolTip => IsPlaybackActive
        ? "Stop"
        : PlaybackUnavailableReason ?? "Play";
    public bool CanPreview => _context?.Tasks is not null
        && _context.Compilation.EffectiveSoundFontPaths.Count != 0
        && !IsPlaybackActive
        && !IsForegroundTaskRunning;
    public string? PlaybackUnavailableReason
    {
        get
        {
            if (_context is null) return null;
            return _context.PlaybackUnavailableReason
                ?? (_context.Compilation.EffectiveSoundFontPaths.Count == 0
                    ? "Enable at least one application SoundFont in Preferences."
                    : _context.Compilation.CompilationState == ProjectCompilationState.Failed
                        ? "The current canonical compilation is not consumable."
                        : null);
        }
    }
    public int ErrorCount => _compilerErrorCount;
    public int WarningCount => _compilerWarningCount;
    public bool HasErrors => ErrorCount > 0;
    public bool HasWarnings => WarningCount > 0;
    public string IssueSummary => $"{ErrorCount} Errors, {WarningCount} Warnings";
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
    public string ActivityText => ActiveForegroundTask?.Status
        ?? PlaybackState.ToString();

    public ObservableCollection<ProjectTreeNode> ProjectTree { get; } = [];
    public ObservableCollection<WorkspaceViewModel> Workspaces { get; } = [];
    public ObservableCollection<DiagnosticRow> CompilerDiagnostics { get; } = [];
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
    public string? StatusMessageDetails
    {
        get => _statusMessageDetails;
        private set => Set(ref _statusMessageDetails, value);
    }
    public string? StatusMessageDetailsTitle
    {
        get => _statusMessageDetailsTitle;
        private set => Set(ref _statusMessageDetailsTitle, value);
    }
    public bool StatusMessageIsError
    {
        get => _statusMessageIsError;
        private set => Set(ref _statusMessageIsError, value);
    }

    public void SetStatusMessage(
        string? message,
        bool isError = false,
        string? details = null,
        string? detailsTitle = null)
    {
        StatusMessageIsError = isError;
        string? normalized = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        StatusMessageDetails = normalized is null || string.IsNullOrWhiteSpace(details)
            ? null
            : details.Trim();
        StatusMessageDetailsTitle = normalized is null || string.IsNullOrWhiteSpace(detailsTitle)
            ? null
            : detailsTitle.Trim();
        StatusMessage = normalized;
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
                if (value is not DiagnosticsWorkspaceViewModel)
                {
                    _diagnosticScopeWorkspace = value;
                }
                Workspaces.OfType<DiagnosticsWorkspaceViewModel>()
                    .FirstOrDefault()?.SetScope(_diagnosticScopeWorkspace);
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
            next = ProjectContext.FromCreation(
                _packages,
                result,
                _applicationPreferences,
                TakePreparedPlaybackBackend());
            result = null!;
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
            next = ProjectContext.FromOpenCandidate(
                candidate,
                _applicationPreferences,
                TakePreparedPlaybackBackend());
            candidate = null!;
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

    public async Task<IReadOnlyList<MidiProjectImportDiagnostic>> ImportMidiAsNewProjectAsync(
        string path,
        IReadOnlyDictionary<byte, byte>? zeroBasedPortMapping = null,
        CancellationToken cancellationToken = default,
        IProgress<MidiProjectImportProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        MidiProjectImportResult imported = await Task.Run(
            () => MidiProjectImportService.ImportFile(
                fullPath,
                Path.GetFileNameWithoutExtension(fullPath),
                zeroBasedPortMapping,
                cancellationToken,
                progress),
            cancellationToken);
        IReadOnlyList<MidiProjectImportDiagnostic> diagnostics =
            await AdoptMidiImportAsNewProjectAsync(
            imported,
            cancellationToken);
        progress?.Report(new(
            MidiProjectImportPhase.Completed,
            imported.Metrics?.ScannedEventCount ?? 0,
            imported.Metrics?.ScannedEventCount ?? 0,
            imported.Metrics?.SourceFileBytes ?? 0,
            imported.Metrics?.SourceFileBytes ?? 0,
            1));
        return diagnostics;
    }

    internal async Task<IReadOnlyList<MidiProjectImportDiagnostic>> ImportMidiBytesAsNewProjectAsync(
        byte[] file,
        string projectName,
        IReadOnlyDictionary<byte, byte>? zeroBasedPortMapping = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(projectName);
        MidiProjectImportResult imported = await Task.Run(
            () => MidiProjectImportService.Import(
                file,
                projectName,
                zeroBasedPortMapping,
                cancellationToken),
            cancellationToken);
        return await AdoptMidiImportAsNewProjectAsync(imported, cancellationToken);
    }

    internal async Task<IReadOnlyList<MidiProjectImportDiagnostic>> AdoptMidiImportAsNewProjectAsync(
        MidiProjectImportResult imported,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imported);
        cancellationToken.ThrowIfCancellationRequested();
        NewProjectCreationResult adopted;
        try
        {
            adopted = _creation.AdoptImportedProject(imported.Project);
        }
        catch
        {
            imported.Project.Dispose();
            throw;
        }
        ProjectContext? next = null;
        try
        {
            next = ProjectContext.FromCreation(
                _packages,
                adopted,
                _applicationPreferences,
                TakePreparedPlaybackBackend());
            adopted = null!;
            await ActivateAsync(next);
            next = null;
            return imported.Diagnostics;
        }
        finally
        {
            if (next is not null) await next.DisposeAsync();
            if (adopted is not null) await adopted.DisposeAsync();
        }
    }

    internal static async Task<byte[]> ReadMidiImportFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        await using FileStream stream = new(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        long length = stream.Length;
        if (length > StandardMidiFile.MaximumImportFileByteCount)
        {
            throw new MidoraMidiException(
                $"SMF input exceeds the bounded {StandardMidiFile.MaximumImportFileByteCount}-byte admission limit.");
        }
        byte[] result = GC.AllocateUninitializedArray<byte>((int)length);
        await stream.ReadExactlyAsync(result, cancellationToken);
        byte[] growthProbe = new byte[1];
        if (await stream.ReadAsync(growthProbe, cancellationToken) != 0)
        {
            throw new IOException(
                "The MIDI file changed while it was being read; retry the import after the writer has finished.");
        }
        return result;
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
        await Task.Run(
            () => Persistence.SaveProjectAsync(
                firstSavePath,
                overwriteAuthorized,
                cancellationToken),
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
        await Task.Run(
            () => Persistence.SaveCopyAsync(path, overwriteAuthorized, cancellationToken),
            cancellationToken);
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

    public void SetSharedGroupMuted(MidoraId sharedGroupId, bool muted)
    {
        SetSharedGroupMonitoringState(sharedGroupId, muted, isSolo: false);
    }

    public void SetSharedGroupSolo(MidoraId sharedGroupId, bool solo)
    {
        SetSharedGroupMonitoringState(sharedGroupId, solo, isSolo: true);
    }

    public void ResetAllTrackMonitoringStates()
    {
        _context?.Playback?.ResetMonitoringStates();
        _mutedTrackIds.Clear();
        _soloTrackIds.Clear();
        _mutedSharedGroupIds.Clear();
        _soloSharedGroupIds.Clear();
        foreach (TimelineWorkspaceViewModel workspace in Workspaces
                     .OfType<TimelineWorkspaceViewModel>()
                     .Where(item => item.Mode == TimelineWorkspaceMode.Arrangement))
        {
            RefreshWorkspace(workspace);
        }
    }

    private void SetTrackMonitoringState(MidoraId trackId, bool enabled, bool isSolo)
    {
        if (Project is not MidoraProject project
            || (!project.Tracks.Any(track => track.Id == trackId)
                && !project.PureMidiTracks.Any(track => track.Id == trackId)))
        {
            throw new InvalidOperationException("The Arrangement Track no longer exists.");
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

    private void SetSharedGroupMonitoringState(
        MidoraId sharedGroupId,
        bool enabled,
        bool isSolo)
    {
        if (Project is not MidoraProject project
            || (!project.EventInstrumentUsages.Any(value => value.Id == sharedGroupId)
                && !project.MidiChannelRoots.Any(value => value.Id == sharedGroupId)))
        {
            throw new InvalidOperationException("The shared Track group no longer exists.");
        }
        HashSet<MidoraId> states = isSolo
            ? _soloSharedGroupIds
            : _mutedSharedGroupIds;
        bool wasEnabled = states.Contains(sharedGroupId);
        if (wasEnabled == enabled) return;
        if (enabled) states.Add(sharedGroupId);
        else states.Remove(sharedGroupId);
        try
        {
            if (isSolo) _context?.Playback?.SetSharedGroupSolo(sharedGroupId, enabled);
            else _context?.Playback?.SetSharedGroupMuted(sharedGroupId, enabled);
        }
        catch
        {
            if (wasEnabled) states.Add(sharedGroupId);
            else states.Remove(sharedGroupId);
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
        if (!CanPreview
            && !_context.Tasks.CanReplaceReleasedHeldEventInstrumentKeyboardPreview)
        {
            throw new InvalidOperationException(
                PlaybackUnavailableReason ?? "Realtime Preview is currently locked.");
        }
        _context.Tasks.StartHeldEventInstrumentPreview(request);
        RefreshProperties();
    }

    public void BeginPitchAudition(int pitch, int velocity)
    {
        if (!CanPreview)
        {
            throw new InvalidOperationException(
                PlaybackUnavailableReason ?? "Realtime Preview is currently locked.");
        }
        (_context?.Playback ?? throw new InvalidOperationException("Realtime Preview is unavailable."))
            .BeginPitchAudition(pitch, velocity);
    }

    public void EndPitchAudition() => _context?.Playback?.EndPitchAudition();

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
        if (_context?.Tasks is null) return;
        _context.Tasks.ResetPlaybackEngine();
        RefreshProperties();
    }

    public void UpdatePlayback()
    {
        if (_context?.Playback is null) return;
        _context.Playback.Update();
        bool tickChanged = CapturePlaybackTick();
        bool tempoChanged = UpdateTempoForTick(_displayCurrentTick);
        RefreshTimelinePlaybackCursors(_displayCurrentTick);
        if (tickChanged)
        {
            Raise(nameof(CurrentTick));
            Raise(nameof(PositionText));
        }
        if (tempoChanged)
        {
            Raise(nameof(TempoText));
        }
        Raise(nameof(PlaybackState));
        Raise(nameof(IsBuffering));
        Raise(nameof(PlaybackStatusText));
        Raise(nameof(IsPlaybackActive));
        Raise(nameof(IsLoopEnabled));
        Raise(nameof(CanPlayback));
        Raise(nameof(CanTogglePlayback));
        Raise(nameof(PrimaryTransportAction));
        Raise(nameof(PrimaryTransportToolTip));
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
            _applicationPreferences.GetEnabledSoundFontConfigurations(),
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

    public ProjectEditExecution ExecutePreservingWorkspaceSelection(
        IProjectEditCommand command,
        WorkspaceViewModel workspace)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(workspace);
        ProjectDocumentSession document = Document
            ?? throw new InvalidOperationException("No Project is open.");
        if (!Workspaces.Contains(workspace))
        {
            throw new InvalidOperationException(
                "The selection-preserving edit target is not an open Workspace.");
        }

        long beforeStateId = document.CurrentStateId;
        WorkspaceSelectionBookmark before = CaptureSelection(workspace.Selection);
        ProjectEditExecution result = Execute(command);
        if (!result.Changed)
        {
            return result;
        }

        StoreSelection(beforeStateId, workspace.Key, before);
        StoreSelection(
            document.CurrentStateId,
            workspace.Key,
            CaptureSelection(workspace.Selection));
        return result;
    }

    public void ApplyObjectProperties(
        WorkspaceViewModel workspace,
        IReadOnlyCollection<PropertyField> fields)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(fields);
        if (Project is null || !Workspaces.Contains(workspace))
        {
            throw new InvalidOperationException(
                "The Properties target is not an open Project Workspace.");
        }

        PropertyField[] pending = fields
            .Where(field => field.HasPendingChange)
            .ToArray();
        if (pending.Length == 0) return;
        if (pending.Any(field => !ObjectPropertiesProjection.CanApplyFromOwnedEditor(workspace, field)))
        {
            throw new InvalidOperationException("One or more object properties are read-only.");
        }

        Dictionary<string, string> edits = pending.ToDictionary(
            field => field.Key,
            field => field.Value,
            StringComparer.Ordinal);
        IProjectEditCommand command = ObjectPropertiesProjection.CreateEditCommand(
            Project,
            workspace,
            edits);
        ExecutePreservingWorkspaceSelection(command, workspace);
    }

    public ObjectPropertiesViewModel CreateObjectProperties(WorkspaceViewModel workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ObjectPropertiesViewModel result = new();
        ObjectPropertiesProjection.Rebuild(result, Project, workspace);
        return result;
    }

    public void ApplyProjectSettingsField(PropertyField field)
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
        _foregroundTask?.Dispose();
        DesktopTaskViewModel task = new(name, canCancel, lockLevel);
        _foregroundTask = task;
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
        if (!ReferenceEquals(_foregroundTask, task) || !task.IsRunning) return;
        task.Report(detail, progress);
        SetStatusMessage(detail);
        Raise(nameof(ActivityText));
        Raise(nameof(ActiveForegroundTask));
        Raise(nameof(IsMainWindowTaskLocked));
        Raise(nameof(IsFullApplicationTaskLocked));
    }

    public void CompleteTask(DesktopTaskViewModel task, string status, string detail = "")
    {
        if (!ReferenceEquals(_foregroundTask, task) || !task.IsRunning) return;
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

    public bool RequiresAudioWorkerRebuild(ApplicationPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        preferences.Validate();
        return !_applicationPreferences.RealtimeAudio.Equals(preferences.RealtimeAudio)
            || !_applicationPreferences.AudioCache.Equals(preferences.AudioCache)
            || !SoundFontPreferencesEqual(
                _applicationPreferences.SoundFonts,
                preferences.SoundFonts);
    }

    public async Task ApplyApplicationPreferencesAsync(
        ApplicationPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        preferences.Validate();
        bool rebuildAudioWorker = RequiresAudioWorkerRebuild(preferences);
        if (IsPlaybackActive)
        {
            throw new InvalidOperationException(
                "Application Preferences can only change while playback is stopped.");
        }

        _applicationPreferences = preferences;
        if (!rebuildAudioWorker)
        {
            Raise(nameof(SoundFontState));
            Raise(nameof(HasEnabledSoundFonts));
            return;
        }

        BassWasapiChildPlaybackBackend? stalePreparedBackend = _preparedPlaybackBackend;
        _preparedPlaybackBackend = null;
        stalePreparedBackend?.Dispose();

        if (_context is null)
        {
            SoundFontConfiguration[] enabledSoundFonts =
                preferences.GetEnabledSoundFontConfigurations();
            if (enabledSoundFonts.Length != 0)
            {
                BassWasapiChildPlaybackBackend? preparedBackend =
                    ProjectContext.CreatePlaybackBackend(preferences);
                try
                {
                    SoundFontSetDefinition soundFontSet =
                        SoundFontSetDefinition.Create(enabledSoundFonts);
                    preparedBackend.SetSoundFontSet(
                        enabledSoundFonts,
                        soundFontSet.CacheIdentity);
                    await Task.Run(
                        () => preparedBackend.Prepare(cancellationToken),
                        cancellationToken);
                    _preparedPlaybackBackend = preparedBackend;
                    preparedBackend = null;
                }
                finally
                {
                    preparedBackend?.Dispose();
                }
            }
            Raise(nameof(SoundFontState));
            Raise(nameof(HasEnabledSoundFonts));
            return;
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
            foreach (MidoraId sharedGroupId in _mutedSharedGroupIds)
                _context.Playback.SetSharedGroupMuted(sharedGroupId, true);
            foreach (MidoraId sharedGroupId in _soloSharedGroupIds)
                _context.Playback.SetSharedGroupSolo(sharedGroupId, true);
            _context.Playback.StateChanged += OnPlaybackStateChanged;
        }
        try
        {
            if (preferences.GetEnabledSoundFontPaths().Length != 0)
            {
                PlaybackController playback = _context.Playback
                    ?? throw new InvalidOperationException(
                        _context.PlaybackUnavailableReason
                        ?? "The formal audio Worker is unavailable.");
                await Task.Run(
                    () => playback.WarmUpAudioBackend(cancellationToken),
                    cancellationToken);
            }
        }
        finally
        {
            RefreshProperties();
            Raise(nameof(SoundFontState));
            Raise(nameof(HasEnabledSoundFonts));
        }
    }

    public async Task<Exception?> TryInitializeAudioWorkerAsync(
        CancellationToken cancellationToken = default)
    {
        if (_context is null || _applicationPreferences.GetEnabledSoundFontPaths().Length == 0)
        {
            return null;
        }
        PlaybackController? playback = _context.Playback;
        if (playback is null)
        {
            return new InvalidOperationException(
                _context.PlaybackUnavailableReason
                ?? "The formal audio Worker is unavailable.");
        }
        try
        {
            await Task.Run(
                () => playback.WarmUpAudioBackend(cancellationToken),
                cancellationToken);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return exception;
        }
        finally
        {
            RefreshProperties();
        }
    }

    private BassWasapiChildPlaybackBackend? TakePreparedPlaybackBackend()
    {
        BassWasapiChildPlaybackBackend? result = _preparedPlaybackBackend;
        _preparedPlaybackBackend = null;
        return result;
    }

    private static bool SoundFontPreferencesEqual(
        IReadOnlyList<ApplicationSoundFontPreference> left,
        IReadOnlyList<ApplicationSoundFontPreference> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }
        for (int index = 0; index < left.Count; index++)
        {
            if (left[index].Enabled != right[index].Enabled
                || left[index].Target != right[index].Target
                || !string.Equals(
                    left[index].Path,
                    right[index].Path,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
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
        RestoreWorkspaceSelections(Document.CurrentStateId);
    }

    public void Redo()
    {
        if (!CanEditProject) return;
        if (Document?.CanRedo != true) return;
        Document.Redo();
        RestoreWorkspaceSelections(Document.CurrentStateId);
    }

    private static WorkspaceSelectionBookmark CaptureSelection(WorkspaceSelection selection) =>
        new(selection.Ids.ToArray(), selection.Primary);

    private void StoreSelection(
        long stateId,
        WorkspaceKey workspaceKey,
        WorkspaceSelectionBookmark bookmark)
    {
        if (!_workspaceSelectionHistory.TryGetValue(
                stateId,
                out Dictionary<WorkspaceKey, WorkspaceSelectionBookmark>? byWorkspace))
        {
            byWorkspace = [];
            _workspaceSelectionHistory.Add(stateId, byWorkspace);
        }
        byWorkspace[workspaceKey] = bookmark;
    }

    private void RestoreWorkspaceSelections(long stateId)
    {
        if (!_workspaceSelectionHistory.TryGetValue(
                stateId,
                out Dictionary<WorkspaceKey, WorkspaceSelectionBookmark>? byWorkspace))
        {
            return;
        }
        foreach ((WorkspaceKey key, WorkspaceSelectionBookmark bookmark) in byWorkspace)
        {
            WorkspaceViewModel? workspace = Workspaces.FirstOrDefault(value => value.Key == key);
            if (workspace is null) continue;
            workspace.Selection.Clear();
            foreach (MidoraId id in bookmark.Ids.Where(id => id != bookmark.Primary))
            {
                workspace.Selection.Add(id, makePrimary: false);
            }
            if (bookmark.Primary is MidoraId primary)
            {
                workspace.Selection.Add(primary, makePrimary: true);
            }
            RefreshWorkspaceSelection(workspace);
        }
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
                () => new InstrumentWorkspaceViewModel(id, node.Title)),
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
        CenterSegmentEditorOnArrangementCursor(workspace, segmentId);
        ActiveWorkspace = workspace;
        return workspace;
    }

    private void CenterSegmentEditorOnArrangementCursor(
        TimelineWorkspaceViewModel workspace,
        MidoraId segmentId)
    {
        if (Project is null
            || ActiveWorkspace is not TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Arrangement
            } arrangement)
        {
            return;
        }
        if (arrangement.EditCursorTick is not long projectTick)
        {
            return;
        }

        (long ProjectStartTick, long LengthTicks, long ContentOffsetTick)? segment =
            TimelineWorkspaceViewModel.FindSegment(Project, segmentId) is { } logical
                ? (logical.Segment.ProjectStartTick, logical.Segment.LengthTicks, logical.Segment.ContentOffsetTick)
                : TimelineWorkspaceViewModel.FindMidiSegment(Project, segmentId) is { } midi
                    ? (midi.Segment.ProjectStartTick, midi.Segment.LengthTicks, midi.Segment.ContentOffsetTick)
                    : null;
        if (segment is null)
        {
            return;
        }
        long projectEndTick = checked(segment.Value.ProjectStartTick + segment.Value.LengthTicks);
        if (projectTick < segment.Value.ProjectStartTick || projectTick >= projectEndTick)
        {
            return;
        }

        long localTick = checked(
            segment.Value.ContentOffsetTick + (projectTick - segment.Value.ProjectStartTick));
        workspace.EditCursorTick = localTick;
        workspace.CenterViewportOnTick(localTick);
    }

    public InstrumentWorkspaceViewModel OpenInstrument(MidoraId instrumentId)
    {
        EventInstrument instrument = Project?.EventInstruments.FirstOrDefault(item => item.Id == instrumentId)
            ?? throw new InvalidOperationException("The Event Instrument no longer exists.");
        InstrumentWorkspaceViewModel workspace = (InstrumentWorkspaceViewModel)GetOrCreate(
            WorkspaceKey.ForObject(WorkspaceKind.EventInstrumentEditor, instrumentId),
            () => new InstrumentWorkspaceViewModel(instrumentId, instrument.Name));
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
        ArgumentNullException.ThrowIfNull(workspace);
        if (!workspace.CanClose) return;
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
        if (!workspace.CanReorder) return;
        newIndex = Math.Max(1, newIndex);
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
        }
    }

    public void RefreshProjectRuntimeInformation()
    {
        if (_context is null)
        {
            return;
        }
        CompilationStatistics statistics = _context.Compilation.LastAttempt.Statistics;
        long totalEditingTimeMilliseconds =
            _context.Compilation.SnapshotTotalEditingTimeMilliseconds();
        foreach (SettingsWorkspaceViewModel workspace in
            Workspaces.OfType<SettingsWorkspaceViewModel>())
        {
            workspace.UpdateRuntimeInformation(statistics, totalEditingTimeMilliseconds);
        }
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
        }
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
        }
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
        _workspaceSelectionHistory.Clear();
        _diagnosticScopeWorkspace = null;
        ProjectTree.Clear();
        CompilerDiagnostics.Clear();
        SelectedDiagnostic = null;
        _mappingDraftCompiler.Clear();
        _mutedTrackIds.Clear();
        _soloTrackIds.Clear();
        _mutedSharedGroupIds.Clear();
        _soloSharedGroupIds.Clear();
        _foregroundTask?.Dispose();
        _foregroundTask = null;
        ActiveWorkspace = null;
        _revision = 0;
        _timeSignatureMap = null;
        _displayCurrentTick = 0;
        _orderedTempoChanges = [];
        _activeTempoIndex = -1;
        _tempoLookupTick = -1;
        _tempoText = "— BPM";
        SetStatusMessage(null);
        RefreshProperties();
        await previous.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await CloseProjectAsync();
        _preparedPlaybackBackend?.Dispose();
        _preparedPlaybackBackend = null;
        _mappingDraftCompiler.Dispose();
    }

    private async Task ActivateAsync(ProjectContext next)
    {
        _mappingDraftCompiler.Clear();
        _mutedTrackIds.Clear();
        _soloTrackIds.Clear();
        _mutedSharedGroupIds.Clear();
        _soloSharedGroupIds.Clear();
        ProjectContext? previous = _context;
        if (previous is not null)
        {
            Unsubscribe(previous);
        }
        _context = next;
        _displayCurrentTick = next.Playback?.CurrentTick ?? 0;
        Subscribe(next);
        TimelineRasterCacheSession.Clear();
        Workspaces.Clear();
        _backNavigation.Clear();
        _forwardNavigation.Clear();
        _workspaceSelectionHistory.Clear();
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
        if (created.Kind == WorkspaceKind.Arrangement)
            Workspaces.Insert(0, created);
        else
            Workspaces.Add(created);
        return created;
    }

    private void RefreshAll()
    {
        CapturePlaybackTick();
        _timeSignatureMap = Project is null ? null : new ProjectTimeSignatureMap(Project);
        RebuildTempoLookup();
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
                workspace.RefreshSelectionPresentation();
                if (workspace is TimelineWorkspaceViewModel timeline)
                {
                    timeline.UpdatePlaybackCursor(Project, CurrentTick);
                }
            }
        }
        RefreshProperties();
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
            RebuildTempoLookup();
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
            if (workspace is DiagnosticsWorkspaceViewModel)
            {
                continue;
            }
            if (!WorkspaceAffected(workspace, changes)) continue;
            PrepareWorkspaceRuntimeState(workspace);
            workspace.Rebuild(Project, _revision);
            workspace.RefreshSelectionPresentation();
            if (workspace is TimelineWorkspaceViewModel timeline)
            {
                timeline.UpdatePlaybackCursor(Project, CurrentTick);
            }
        }
        RefreshProperties();
    }

    private bool WorkspaceAffected(
        WorkspaceViewModel workspace,
        ProjectContentChangedEventArgs changes)
    {
        if (changes.AffectsEverything) return true;
        HashSet<MidoraId> trackIds = changes.TrackIds.ToHashSet();
        HashSet<MidoraId> instrumentIds = changes.EventInstrumentIds.ToHashSet();
        HashSet<MidoraId> rootIds = changes.MidiChannelRootIds.ToHashSet();
        HashSet<MidoraId> pureTrackIds = changes.PureMidiTrackIds.ToHashSet();
        return workspace.Kind switch
        {
            WorkspaceKind.Arrangement => changes.AffectsConductor
                || trackIds.Count != 0
                || instrumentIds.Count != 0
                || rootIds.Count != 0
                || pureTrackIds.Count != 0,
            WorkspaceKind.ConductorTrack => changes.AffectsConductor,
            WorkspaceKind.SegmentEditor => workspace.ObjectId is MidoraId segmentId
                && (TimelineWorkspaceViewModel.FindSegment(Project!, segmentId) is { } located
                    && (trackIds.Contains(located.Track.Id)
                        || Project!.ResolveEventInstrumentDefinitionId(located.Track) is MidoraId instrumentId
                           && instrumentIds.Contains(instrumentId))
                    || TimelineWorkspaceViewModel.FindMidiSegment(Project!, segmentId) is { } midi
                    && (pureTrackIds.Contains(midi.Track.Id)
                        || rootIds.Contains(midi.Track.MidiChannelRootId))),
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

    private void PrepareWorkspaceRuntimeState(WorkspaceViewModel workspace)
    {
        if (workspace is TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Arrangement } timeline)
        {
            timeline.SetTrackMonitoringStates(
                _mutedTrackIds,
                _soloTrackIds,
                _mutedSharedGroupIds,
                _soloSharedGroupIds);
        }
        if (workspace is SettingsWorkspaceViewModel settings && _context is not null)
        {
            settings.UpdateRuntimeInformation(
                _context.Compilation.LastAttempt.Statistics,
                _context.Compilation.SnapshotTotalEditingTimeMilliseconds());
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
        foreach (EventInstrument instrument in Project.EventInstruments.Where(item =>
                     MatchesProjectTreeFilter(item.Name, query)))
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
            EventInstrument? bound = Project.FindEventInstrumentDefinition(track);
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
            foreach (DiagnosticsWorkspaceViewModel workspace in
                Workspaces.OfType<DiagnosticsWorkspaceViewModel>())
            {
                workspace.Replace(diagnostics);
            }
            _compilerErrorCount = diagnostics.Count(item => item.Severity == "Error");
            _compilerWarningCount = diagnostics.Count(item => item.Severity == "Warning");
        }
    }

    private bool ObjectStillExists(WorkspaceViewModel workspace)
    {
        if (Project is null || workspace.ObjectId is not MidoraId id) return true;
        return workspace.Kind switch
        {
            WorkspaceKind.SegmentEditor =>
                TimelineWorkspaceViewModel.FindSegment(Project, id) is not null
                || TimelineWorkspaceViewModel.FindMidiSegment(Project, id) is not null,
            WorkspaceKind.EventInstrumentEditor => Project.EventInstruments.Any(item => item.Id == id),
            WorkspaceKind.MappingFunctionEditor => Project.EventInstruments.Any(
                instrument => instrument.MappingFunctions.Any(function => function.Id == id)),
            _ => true
        };
    }

    private void RefreshProperties()
    {
        CapturePlaybackTick();
        UpdateTempoForTick(_displayCurrentTick);
        RefreshTimelinePlaybackCursors(_displayCurrentTick);
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
        Raise(nameof(HasEnabledSoundFonts));
        Raise(nameof(PositionText));
        Raise(nameof(CurrentTick));
        Raise(nameof(TempoText));
        Raise(nameof(PlaybackState));
        Raise(nameof(IsBuffering));
        Raise(nameof(PlaybackStatusText));
        Raise(nameof(IsPlaybackActive));
        Raise(nameof(CanPlayback));
        Raise(nameof(CanTogglePlayback));
        Raise(nameof(PrimaryTransportAction));
        Raise(nameof(PrimaryTransportToolTip));
        Raise(nameof(CanPreview));
        Raise(nameof(PlaybackUnavailableReason));
        Raise(nameof(ErrorCount));
        Raise(nameof(WarningCount));
        Raise(nameof(HasErrors));
        Raise(nameof(HasWarnings));
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

    private void RefreshTimelinePlaybackCursors() =>
        RefreshTimelinePlaybackCursors(_displayCurrentTick);

    private void RefreshTimelinePlaybackCursors(long currentTick)
    {
        if (Project is null)
        {
            return;
        }
        foreach (TimelineWorkspaceViewModel timeline in Workspaces.OfType<TimelineWorkspaceViewModel>())
        {
            timeline.UpdatePlaybackCursor(Project, currentTick);
        }
    }

    private bool CapturePlaybackTick()
    {
        long next = Math.Max(0, _context?.Playback?.CurrentTick ?? 0);
        if (_displayCurrentTick == next)
        {
            return false;
        }
        _displayCurrentTick = next;
        return true;
    }

    private void RebuildTempoLookup()
    {
        _orderedTempoChanges = Project?.Conductor.Tempos
            .OrderBy(static value => value.Tick)
            .ToArray()
            ?? [];
        _activeTempoIndex = -1;
        _tempoLookupTick = -1;
        UpdateTempoForTick(_displayCurrentTick, force: true);
    }

    private bool UpdateTempoForTick(long tick, bool force = false)
    {
        tick = Math.Max(0, tick);
        int previousTempoIndex = _activeTempoIndex;
        if (_orderedTempoChanges.Length == 0)
        {
            _activeTempoIndex = -1;
        }
        else if (tick >= _tempoLookupTick && _activeTempoIndex >= -1)
        {
            while (_activeTempoIndex + 1 < _orderedTempoChanges.Length
                && _orderedTempoChanges[_activeTempoIndex + 1].Tick <= tick)
            {
                _activeTempoIndex++;
            }
        }
        else
        {
            int low = 0;
            int high = _orderedTempoChanges.Length;
            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                if (_orderedTempoChanges[middle].Tick <= tick)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            _activeTempoIndex = low - 1;
        }
        _tempoLookupTick = tick;
        if (!force && previousTempoIndex == _activeTempoIndex)
        {
            return false;
        }
        string next = _activeTempoIndex < 0
            ? "— BPM"
            : $"{_orderedTempoChanges[_activeTempoIndex].BeatsPerMinute:0.00} BPM";
        if (string.Equals(_tempoText, next, StringComparison.Ordinal))
        {
            return false;
        }
        _tempoText = next;
        return true;
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
        RefreshProjectRuntimeInformation();
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

    private sealed record WorkspaceSelectionBookmark(
        IReadOnlyList<MidoraId> Ids,
        MidoraId? Primary);

    private sealed class ProjectContext : IAsyncDisposable
    {
        private readonly IAsyncDisposable _owner;

        private ProjectContext(
            IAsyncDisposable owner,
            ProjectCompilationSession compilation,
            ProjectDocumentSession document,
            ProjectPersistenceCoordinator persistence,
            PlaybackController? playback,
            ApplicationTaskCoordinator? tasks,
            string? playbackUnavailableReason)
        {
            _owner = owner;
            Compilation = compilation;
            Document = document;
            Persistence = persistence;
            Playback = playback;
            Tasks = tasks;
            PlaybackUnavailableReason = playbackUnavailableReason;
        }

        public ProjectCompilationSession Compilation { get; }
        public ProjectDocumentSession Document { get; }
        public ProjectPersistenceCoordinator Persistence { get; }
        public PlaybackController? Playback { get; private set; }
        public ApplicationTaskCoordinator? Tasks { get; private set; }
        public string? PlaybackUnavailableReason { get; private set; }

        public static ProjectContext FromCreation(
            MidoraProjectPackageV1 packages,
            NewProjectCreationResult result,
            ApplicationPreferences preferences,
            BassWasapiChildPlaybackBackend? preparedPlaybackBackend = null)
        {
            ProjectCompilationSession compilation = new(
                result.Project,
                executionMode: ProjectCompilationExecutionMode.Background);
            try
            {
                compilation.SetEffectiveSoundFontConfigurations(
                    preferences.GetEnabledSoundFontConfigurations());
                ProjectDocumentSession document = new(compilation, result.Origin);
                ProjectPersistenceCoordinator persistence = new(
                    document,
                    packages,
                    result.CurrentProjectPath,
                    result.FileInformation);
                CreatePlaybackServices(
                    compilation,
                    preferences,
                    preparedPlaybackBackend,
                    out PlaybackController? playback,
                    out ApplicationTaskCoordinator? tasks,
                    out string? playbackFailure);
                preparedPlaybackBackend = null;
                return new(
                    result,
                    compilation,
                    document,
                    persistence,
                    playback,
                    tasks,
                    playbackFailure);
            }
            catch
            {
                preparedPlaybackBackend?.Dispose();
                compilation.Dispose();
                throw;
            }
        }

        public static ProjectContext FromOpenCandidate(
            ProjectOpenCandidate candidate,
            ApplicationPreferences preferences,
            BassWasapiChildPlaybackBackend? preparedPlaybackBackend = null)
        {
            ProjectCompilationSession compilation = new(
                candidate.Project,
                executionMode: ProjectCompilationExecutionMode.Background);
            try
            {
                compilation.SetEffectiveSoundFontConfigurations(
                    preferences.GetEnabledSoundFontConfigurations());
                ProjectDocumentSession document = candidate.CreateDocumentSession(compilation);
                ProjectPersistenceCoordinator persistence = new(
                    document,
                    new MidoraProjectPackageV1(SoftwareVersion),
                    candidate.CurrentProjectPath,
                    candidate.FileInformation);
                CreatePlaybackServices(
                    compilation,
                    preferences,
                    preparedPlaybackBackend,
                    out PlaybackController? playback,
                    out ApplicationTaskCoordinator? tasks,
                    out string? playbackFailure);
                preparedPlaybackBackend = null;
                return new(
                    candidate,
                    compilation,
                    document,
                    persistence,
                    playback,
                    tasks,
                    playbackFailure);
            }
            catch
            {
                preparedPlaybackBackend?.Dispose();
                compilation.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Tasks?.Dispose();
            Playback?.Dispose();
            Compilation.Dispose();
            await _owner.DisposeAsync();
        }

        public void ReconfigurePlaybackServices(ApplicationPreferences preferences)
        {
            ArgumentNullException.ThrowIfNull(preferences);
            Tasks?.Dispose();
            Playback?.Dispose();
            Tasks = null;
            Playback = null;
            Compilation.SetEffectiveSoundFontConfigurations(
                preferences.GetEnabledSoundFontConfigurations());
            CreatePlaybackServices(
                Compilation,
                preferences,
                preparedPlaybackBackend: null,
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
            BassWasapiChildPlaybackBackend? preparedPlaybackBackend,
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
            BassWasapiChildPlaybackBackend? backend = preparedPlaybackBackend;
            try
            {
                if (backend is null)
                {
                    backend = CreatePlaybackBackend(preferences);
                }
                else
                {
                    // The detached backend was prepared with the metadata identity. Align the
                    // Project session before PlaybackController attaches so it does not discard
                    // the already-loaded persistent Worker as a stale SoundFont host.
                    compilation.RefreshEffectiveSoundFontCacheIdentity();
                }
                playback = new(compilation, backend);
                backend = null;
                tasks = new(compilation, playback);
                failure = null;
            }
            catch (Exception exception)
            {
                backend?.Dispose();
                playback?.Dispose();
                playback = null;
                tasks = null;
                failure = exception.Message;
            }
        }

        public static BassWasapiChildPlaybackBackend CreatePlaybackBackend(
            ApplicationPreferences preferences)
        {
            ArgumentNullException.ThrowIfNull(preferences);
            if (!FormalAudioWorkerLocator.TryLocate(
                    out string? workerPath,
                    out string? nativeDirectory,
                    out string? failure))
            {
                throw new InvalidOperationException(
                    failure ?? "The formal audio Worker is unavailable.");
            }
            RealtimeAudioPreferences audio = preferences.RealtimeAudio;
            return new(new(
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
        }
    }
}
