using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Midora.Application;
using Midora.Avalonia.Editing;
using Midora.Avalonia.Import;
using Midora.Avalonia.Presentation.Rendering;
using Midora.Avalonia.Views;
using Midora.Compiler;
using Midora.Domain;
using Midora.Persistence;
using Midora.Session;
using Midora.Session.Audio;

namespace Midora.Avalonia.Session;

/// <summary>
/// Shell-level session state for the Avalonia port: project presence and naming, workspace
/// tabs and navigation, transport/compile placeholders, and status-bar fields. This is the
/// binding surface of <c>MainWindow</c> and the seam where the real Domain/Application
/// wiring will land.
/// </summary>
public sealed class ShellSession : INotifyPropertyChanged
{
    private readonly List<WorkspaceTab> _history = [];
    private int _historyIndex = -1;
    private WorkspaceTab? _activeWorkspace;
    private bool _hasProject;
    private bool _isModified;
    private bool _isPlaying;
    private bool _isLoopEnabled;
    private bool _isFollowPlaybackEnabled;
    private bool _isSnapEnabled = true;
    private string _operationSubdivision = "1/8";
    private long _editCursorTick = -1;
    private bool _isGridVisible = true;
    private bool _isCompiling;
    private string _projectName = "Untitled Project";
    private string _compileState = "Not Compiled";
    private string _soundFontState = "No SoundFonts Enabled";
    private string? _statusMessage;
    private string? _statusMessageDetails;
    private string? _statusMessageDetailsTitle;
    private bool _statusMessageIsError;
    private string _issueSummary = "0 Errors, 0 Warnings";
    private string _notice = string.Empty;
    private int _errorCount;
    private int _warningCount;
    private string _positionText = "0001 : 01 : 0000";
    private string _tempoText = "120.00 BPM";
    private readonly DemoTimelineSource _demoSource = DemoTimelineSource.Create();
    private readonly Stopwatch _playbackClock = new();
    private DispatcherTimer? _playbackTimer;
    private long _playbackTick;
    private double _tempoBpm = 120;
    private MidiTimelineSource? _midiSource;
    private EditableMidiProject? _editableProject;
    private ArrangementView? _arrangementView;
    private MidiTrackView? _activeEditView;
    private readonly ProjectSessionHost? _projectHost;
    private readonly string? _projectHostError;

    public ShellSession(ApplicationPreferences? preferences = null)
    {
        Preferences = preferences;
        try
        {
            _projectHost = new ProjectSessionHost(
                SoftwareVersion,
                new ProjectSessionHostOptions(
                    EnableRealtimePlayback: preferences is not null,
                    Preferences: preferences));
        }
        catch (Exception exception)
        {
            // Avalonia port: the portable data root probe mirrors the WPF fail-closed
            // behaviour without aborting the whole shell; Project features report the error.
            _projectHostError = exception.Message;
        }

        Diagnostics.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDiagnostics));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<WorkspaceTab> Workspaces { get; } = [];

    /// <summary>Compiler diagnostics of the last Project compilation, for the Diagnostics view.</summary>
    public ObservableCollection<ShellDiagnostic> Diagnostics { get; } = [];

    public bool HasDiagnostics => Diagnostics.Count > 0;

    internal ProjectSessionHost? ProjectHost => _projectHost;

    internal bool HasProjectSession => _projectHost?.HasProject == true;

    internal string? ProjectHostError => _projectHostError;

    internal string? CurrentProjectPath => _projectHost?.Persistence?.CurrentProjectPath;

    internal MidoraProject? DomainProject => _projectHost?.Document?.Project;

    internal MidoraProjectFileInformationV1? ProjectFileInformation =>
        _projectHost?.Persistence?.FileInformation;

    internal const string SoftwareVersion = "0.1.0-avalonia-port";

    public WorkspaceTab? ActiveWorkspace
    {
        get => _activeWorkspace;
        private set => Set(ref _activeWorkspace, value);
    }

    public bool HasProject
    {
        get => _hasProject;
        private set
        {
            if (Set(ref _hasProject, value))
            {
                RaiseDerived();
            }
        }
    }

    public bool IsModified
    {
        get => _isModified;
        private set
        {
            if (Set(ref _isModified, value))
            {
                RaiseDerived();
            }
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (Set(ref _isPlaying, value))
            {
                RaiseDerived();
            }
        }
    }

    public bool IsLoopEnabled
    {
        get => _isLoopEnabled;
        set => Set(ref _isLoopEnabled, value);
    }

    public bool IsFollowPlaybackEnabled
    {
        get => _isFollowPlaybackEnabled;
        set => Set(ref _isFollowPlaybackEnabled, value);
    }

    public bool IsSnapEnabled
    {
        get => _isSnapEnabled;
        set
        {
            if (Set(ref _isSnapEnabled, value))
            {
                PushOperationStep();
            }
        }
    }

    public bool IsGridVisible
    {
        get => _isGridVisible;
        set => Set(ref _isGridVisible, value);
    }

    /// <summary>
    /// Arrangement operation subdivision as an <c>1/n</c> fraction (or <c>Bar</c>). It defines the
    /// effective operation step used by Snap (SRS 20.1.4).
    /// </summary>
    public string OperationSubdivision
    {
        get => _operationSubdivision;
        set
        {
            string normalized = string.IsNullOrWhiteSpace(value) ? "1/8" : value.Trim();
            if (Set(ref _operationSubdivision, normalized))
            {
                PushOperationStep();
            }
        }
    }

    /// <summary>Effective operation step in ticks; 1 while Snap is disabled (SRS 20.1.4).</summary>
    public long OperationStepTicks => ComputeOperationStepTicks(
        _operationSubdivision,
        Math.Max(1, _projectHost?.Document?.Project.TicksPerQuarterNote ?? 480),
        IsSnapEnabled);

    /// <summary>Session Edit Cursor position; -1 hides the blue dashed cursor (SRS 20.1.2).</summary>
    public long EditCursorTick
    {
        get => _editCursorTick;
        private set
        {
            if (Set(ref _editCursorTick, value))
            {
                _arrangementView?.SetEditCursorTick(value);
                _activeEditView?.SetEditCursorTick(value);
            }
        }
    }

    private static long ComputeOperationStepTicks(
        string subdivision,
        long ticksPerQuarterNote,
        bool snapEnabled)
    {
        if (!snapEnabled)
        {
            return 1;
        }

        string text = subdivision.Trim();
        if (text.Equals("Bar", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Max(1, ticksPerQuarterNote * 4);
        }

        if (text.StartsWith("1/", StringComparison.Ordinal)
            && long.TryParse(text.AsSpan(2), out long denominator)
            && denominator > 0)
        {
            long numerator = ticksPerQuarterNote * 4;
            return Math.Max(1, (numerator + denominator - 1) / denominator);
        }

        return Math.Max(1, ticksPerQuarterNote / 2);
    }

    private void PushOperationStep()
    {
        long step = OperationStepTicks;
        _arrangementView?.SetOperationStepTicks(step);
        _activeEditView?.SetOperationStepTicks(step);
        OnPropertyChanged(nameof(OperationStepTicks));
    }

    public bool IsCompiling
    {
        get => _isCompiling;
        private set
        {
            if (Set(ref _isCompiling, value))
            {
                RaiseDerived();
            }
        }
    }

    public string ProjectName
    {
        get => _projectName;
        private set
        {
            if (Set(ref _projectName, value))
            {
                RaiseDerived();
            }
        }
    }

    public string CompileState
    {
        get => _compileState;
        private set => Set(ref _compileState, value);
    }

    public string SoundFontState
    {
        get => _soundFontState;
        private set => Set(ref _soundFontState, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (Set(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatusMessage));
            }
        }
    }

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(_statusMessage);

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

    public string IssueSummary
    {
        get => _issueSummary;
        private set => Set(ref _issueSummary, value);
    }

    public string Notice
    {
        get => _notice;
        private set
        {
            if (Set(ref _notice, value))
            {
                OnPropertyChanged(nameof(HasNotice));
            }
        }
    }

    public bool HasNotice => !string.IsNullOrEmpty(_notice);

    /// <summary>WPF <c>ProjectState</c> wording, derived from the real document origin.</summary>
    public string ProjectState
    {
        get
        {
            if (!HasProject)
            {
                return "No Project";
            }

            bool persisted = _projectHost?.Document?.HasPersistentOrigin == true;
            if (IsModified)
            {
                return persisted ? "Modified" : "Modified · Unsaved";
            }

            return persisted ? "Saved" : "Unsaved";
        }
    }

    public string PlaybackState => IsPlaying ? "Playing" : "Stopped";

    public int ErrorCount
    {
        get => _errorCount;
        private set
        {
            if (Set(ref _errorCount, value))
            {
                RaiseDerived();
            }
        }
    }

    public int WarningCount
    {
        get => _warningCount;
        private set
        {
            if (Set(ref _warningCount, value))
            {
                RaiseDerived();
            }
        }
    }

    public string PositionText
    {
        get => _positionText;
        private set => Set(ref _positionText, value);
    }

    public long PlaybackTick
    {
        get => _playbackTick;
        private set
        {
            if (Set(ref _playbackTick, value))
            {
                _arrangementView?.SetPlaybackTick(value);
                _activeEditView?.SetPlaybackTick(value);
            }
        }
    }

    public string TempoText
    {
        get => _tempoText;
        private set => Set(ref _tempoText, value);
    }

    // Derived binding surface.

    public string WindowTitle => HasProject
        ? $"Midora — {ProjectName}{(IsModified ? " *" : string.Empty)}"
        : "Midora";

    public string TitleBarProjectDisplayName => HasProject
        ? $"{ProjectName}{(IsModified ? " *" : string.Empty)}"
        : "No Project";

    public bool CanEditProject => HasProject;

    public bool CanSaveProject =>
        HasProject && IsModified && (_projectHost?.Persistence?.CanSaveProject ?? false);

    public bool CanPlayback => HasProject && !IsPlaying;

    public bool CanRunProjectTask => HasProject && !IsCompiling && HasProjectSession;

    public bool CanNavigateBack => _historyIndex > 0;

    public bool CanNavigateForward => _historyIndex >= 0 && _historyIndex < _history.Count - 1;

    public bool HasErrors => ErrorCount > 0;

    public bool HasWarnings => WarningCount > 0;

    public IBrush IssueBrush => ErrorCount > 0
        ? Brush("Brush.Red", "#E5484D")
        : WarningCount > 0
            ? Brush("Brush.Warning", "#E8B34B")
            : Brush("Brush.Success", "#58C487");

    public IBrush PlaybackBrush => IsPlaying
        ? Brush("Brush.Success", "#58C487")
        : Brush("Brush.Text.Tertiary", "#747E8C");

    // Project lifecycle.

    /// <summary>
    /// Creates a real unsaved Domain Project through the Application layer and resets the shell
    /// surfaces. Used by the review hooks and the smoke probes.
    /// </summary>
    public void CreateProject(string name)
    {
        string normalized = string.IsNullOrWhiteSpace(name) ? "Untitled Project" : name.Trim();
        ProjectActivation? activation = null;
        if (_projectHost is null)
        {
            SetStatusMessage(
                $"New Project is unavailable: {_projectHostError ?? "portable storage is unavailable"}.",
                isError: true);
        }
        else
        {
            try
            {
                activation = _projectHost.CreateUnsaved(new NewProjectCreationRequest
                {
                    ProjectName = normalized
                });
            }
            catch (Exception exception)
            {
                SetStatusMessage($"New Project failed: {exception.Message}", isError: true);
            }
        }

        ResetShellForProject(activation, normalized);
        if (activation is not null)
        {
            AttachProjectDocument();
            SetStatusMessage($"New Project '{activation.ProjectName}' created.");
        }
    }

    /// <summary>Creates a Project from the New Project dialog, including its optional first save.</summary>
    public async Task<bool> CreateProjectAsync(NewProjectCreationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_projectHost is null)
        {
            SetStatusMessage(
                $"New Project is unavailable: {_projectHostError ?? "portable storage is unavailable"}.",
                isError: true);
            return false;
        }

        try
        {
            ProjectActivation activation = await _projectHost.CreateAsync(request);
            ResetShellForProject(activation, activation.ProjectName);
            AttachProjectDocument();
            SetStatusMessage(activation.IsPersisted
                ? $"New Project '{activation.ProjectName}' saved to {activation.CurrentPath}."
                : $"New Project '{activation.ProjectName}' created.");
            return true;
        }
        catch (Exception exception)
        {
            SetStatusMessage($"New Project failed: {exception.Message}", isError: true);
            return false;
        }
    }

    /// <summary>Opens a persisted .midora Project through the Application layer.</summary>
    public async Task<bool> OpenProjectAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (_projectHost is null)
        {
            SetStatusMessage(
                $"Open Project is unavailable: {_projectHostError ?? "portable storage is unavailable"}.",
                isError: true);
            return false;
        }

        try
        {
            ProjectActivation activation = await _projectHost.OpenAsync(path);
            ResetShellForProject(activation, Path.GetFileNameWithoutExtension(path));
            AttachProjectDocument();
            string note = activation.RequiresFormatUpgrade
                ? " Older file format: the first save will migrate it."
                : activation.HasDamagedObjects
                    ? " Some damaged objects are isolated; saving stays disabled."
                    : string.Empty;
            SetStatusMessage(
                $"Project opened: {activation.ProjectName}."
                + note
                + " Timeline lanes for Domain project data are still being ported.");
            return true;
        }
        catch (Exception exception)
        {
            SetStatusMessage($"Open Project failed: {exception.Message}", isError: true);
            return false;
        }
    }

    /// <summary>Persists the current Project (first save uses the supplied target path).</summary>
    public async Task<bool> SaveProjectAsync(
        string? firstTargetPath = null,
        bool overwriteAuthorized = false)
    {
        if (_projectHost?.Persistence is not { } persistence)
        {
            SetStatusMessage("Save requires a Project-backed session.", isError: true);
            return false;
        }

        try
        {
            MidoraProjectSaveResultV1 result = await persistence
                .SaveProjectAsync(firstTargetPath, overwriteAuthorized);
            IsModified = _projectHost.Document?.IsModified == true;
            RaiseDerived();
            SetStatusMessage($"Project saved to {result.TargetPath}.");
            return true;
        }
        catch (Exception exception)
        {
            SetStatusMessage($"Save failed: {exception.Message}", isError: true);
            return false;
        }
    }

    /// <summary>Writes a separate copy without clearing the current Project's modified state.</summary>
    public async Task<bool> SaveCopyAsync(string targetPath, bool overwriteAuthorized = false)
    {
        if (_projectHost?.Persistence is not { } persistence)
        {
            SetStatusMessage("Save Copy requires a Project-backed session.", isError: true);
            return false;
        }

        try
        {
            MidoraProjectSaveResultV1 result = await persistence
                .SaveCopyAsync(targetPath, overwriteAuthorized);
            SetStatusMessage($"Project copy saved to {result.TargetPath}.");
            return true;
        }
        catch (Exception exception)
        {
            SetStatusMessage($"Save Copy failed: {exception.Message}", isError: true);
            return false;
        }
    }

    private void ResetShellForProject(ProjectActivation? activation, string fallbackName)
    {
        _midiSource = null;
        _editableProject = null;
        _activeEditView = null;
        Workspaces.Clear();
        _history.Clear();
        _historyIndex = -1;

        ProjectName = activation?.ProjectName is { Length: > 0 } name ? name : fallbackName;
        HasProject = true;
        IsModified = _projectHost?.Document?.IsModified == true;
        IsPlaying = false;
        IsCompiling = false;
        ClearDiagnostics();
        PositionText = "0001 : 01 : 0000";
        TempoText = "120.00 BPM";
        Notice = string.Empty;
        SetStatusMessage(null);

        OpenWorkspace(WorkspaceKind.Arrangement);
        ApplyArrangementSource();
    }

    private void AttachProjectDocument()
    {
        if (_projectHost?.Document is not { } document)
        {
            return;
        }

        document.ContentChanged += (_, _) => MarkModified();
        document.HistoryChanged += (_, _) => RaiseDerived();
    }

    private void ClearDiagnostics()
    {
        Diagnostics.Clear();
        CompileState = "Not Compiled";
        ErrorCount = 0;
        WarningCount = 0;
        IssueSummary = "0 Errors, 0 Warnings";
        OnPropertyChanged(nameof(IssueBrush));
    }

    private WorkspaceTab CreateArrangementWorkspace()
    {
        _arrangementView = new ArrangementView { DataContext = this };
        _arrangementView.TrackActivated += (_, trackIndex) => OpenMidiTrackWorkspace(trackIndex);
        _arrangementView.SegmentActivated += (_, activation) =>
            OpenMidiSegmentWorkspace(activation.TrackIndex, activation.StartTick);
        _arrangementView.AllTracksRequested += (_, _) => OpenWorkspace(WorkspaceKind.AllTracks);
        _arrangementView.PlaybackCursorRequested += (_, tick) => SeekPlaybackCursor(tick);
        _arrangementView.EditCursorRequested += (_, tick) => SetEditCursorFromUi(tick);
        _arrangementView.TimeRangeSelected += (_, range) =>
            SetTimeRangeFromUi(range.StartTick, range.EndTick);
        _arrangementView.CreateTrackRequested += (_, kind) => SetStatus(kind switch
        {
            "Instrument" => "New Logical Track with Instrument requested (not wired yet).",
            "Midi" => "New Raw MIDI Track requested (not wired yet).",
            _ => "New Logical Track is not wired yet.",
        });
        return new WorkspaceTab(
            WorkspaceKind.Arrangement,
            "Arrangement",
            _arrangementView,
            Icon("Fluent.MoviesAndTv20Regular"),
            canClose: false);
    }

    /// <summary>Opens (or activates) a segment-scoped MIDI editor tab.</summary>
    public void OpenMidiSegmentWorkspace(int trackIndex, long startTick)
    {
        if (!HasProject ||
            _midiSource is null ||
            _editableProject is null ||
            trackIndex < 0 ||
            trackIndex >= _midiSource.Project.Tracks.Count)
        {
            return;
        }

        // Imported tracks carry exactly one Segment [0, source MTrk EOT), so navigation opens
        // that Segment instead of recomputing a display grid (SRS 23.5.3).
        long segmentStartTick = 0;
        long segmentLengthTicks = Math.Max(
            1,
            _midiSource.Project.Tracks[trackIndex].EndTick);
        var workspace = Workspaces.FirstOrDefault(
            tab => tab.Kind == WorkspaceKind.MidiTrack &&
                   tab.TrackIndex == trackIndex &&
                   tab.SegmentStartTick == segmentStartTick);
        if (workspace is null)
        {
            var view = new MidiTrackView();
            view.SetProject(_editableProject, trackIndex);
            view.SetSegmentRange(segmentStartTick, segmentLengthTicks);
            view.SetOperationStepTicks(OperationStepTicks);
            view.SetEditCursorTick(EditCursorTick);
            view.PlaybackCursorRequested += (_, tick) => SeekPlaybackCursor(tick);
            view.EditCursorRequested += (_, tick) => SetEditCursorFromUi(tick);
            view.TimeRangeSelected += (_, range) =>
                SetTimeRangeFromUi(range.StartTick, range.EndTick);
            view.Edited += (_, _) => MarkModified();
            string trackName = trackIndex + 1 < _midiSource.TrackNames.Count
                ? _midiSource.TrackNames[trackIndex + 1]
                : $"Track {trackIndex + 1}";
            workspace = new WorkspaceTab(
                WorkspaceKind.MidiTrack,
                $"MIDI Segment: {trackName}",
                view,
                Icon("Fluent.Midi20Regular"),
                canClose: true,
                trackIndex,
                segmentStartTick);
            Workspaces.Add(workspace);
        }

        ActivateWorkspace(workspace, pushHistory: true);
    }

    /// <summary>
    /// Opens (or activates) the per-track MIDI editor workspace for the imported project.
    /// </summary>
    public void OpenMidiTrackWorkspace(int trackIndex)
    {
        if (!HasProject ||
            _midiSource is null ||
            _editableProject is null ||
            trackIndex < 0 ||
            trackIndex >= _midiSource.Project.Tracks.Count)
        {
            return;
        }

        var workspace = Workspaces.FirstOrDefault(
            tab => tab.Kind == WorkspaceKind.MidiTrack && tab.TrackIndex == trackIndex);
        if (workspace is null)
        {
            var view = new MidiTrackView();
            view.SetProject(_editableProject, trackIndex);
            view.Edited += (_, _) => MarkModified();
            string name = trackIndex + 1 < _midiSource.TrackNames.Count
                ? _midiSource.TrackNames[trackIndex + 1]
                : $"Track {trackIndex + 1}";
            workspace = new WorkspaceTab(
                WorkspaceKind.MidiTrack,
                name,
                view,
                Icon("Fluent.Midi20Regular"),
                canClose: true,
                trackIndex);
            Workspaces.Add(workspace);
        }

        ActivateWorkspace(workspace, pushHistory: true);
    }

    /// <summary>Focus/undo surface used by the Edit menu for the active track editor.</summary>
    internal EditableMidiProject? EditableProject => _editableProject;

    internal MidiTimelineSource? MidiSource => _midiSource;

    public bool UndoActive() => _activeEditView?.Undo() == true;

    public bool RedoActive() => _activeEditView?.Redo() == true;

    /// <summary>Review helper: switches the active track editor's surface mode.</summary>
    internal void SetActiveEditMode(string mode) => _activeEditView?.ApplyModeByName(mode);

    /// <summary>
    /// Replaces the current project with a real imported SMF project and shows it in the
    /// Arrangement workspace.
    /// </summary>
    public void CreateProjectFromMidi(
        string name,
        ImportedMidiProject project,
        byte[]? sourceBytes = null)
    {
        ProjectActivation? activation = null;
        string? adoptionError = null;
        if (sourceBytes is not null && _projectHost is not null)
        {
            try
            {
                activation = _projectHost.ImportMidi(sourceBytes, name);
            }
            catch (Exception exception)
            {
                adoptionError = exception.Message;
            }
        }

        ResetShellForProject(activation, name);
        if (activation is not null)
        {
            AttachProjectDocument();
        }

        _editableProject = new EditableMidiProject(project);
        _editableProject.Changed += (_, _) =>
        {
            MarkModified();
            RefreshArrangementSource();
        };
        _midiSource = new MidiTimelineSource(project, liveProject: _editableProject);
        ApplyArrangementSource();

        if (project.Conductor.FirstOrDefault(
                conductorEvent => conductorEvent.Kind == ImportedConductorKind.Tempo) is { Value: > 0 } tempo)
        {
            _tempoBpm = tempo.Value;
            TempoText = $"{tempo.Value:0.00} BPM";
        }

        PlaybackTick = -1;
        EditCursorTick = -1;
        TimeRangeStartTick = -1;
        TimeRangeEndTick = -1;
        _arrangementView?.SetTimeRange(-1, -1);
        PushOperationStep();

        int warningCount = project.Diagnostics.Count(
            diagnostic => diagnostic.Severity == ImportedMidiDiagnosticSeverity.Warning);
        int informationCount = project.Diagnostics.Count(
            diagnostic => diagnostic.Severity == ImportedMidiDiagnosticSeverity.Info);
        string report = BuildMidiImportReport(project, warningCount, informationCount);
        if (adoptionError is not null)
        {
            report = "The Project could not be adopted from the MIDI import; "
                + "this session stays preview-only.\n\n"
                + adoptionError
                + "\n\n"
                + report;
        }
        else if (activation is null)
        {
            report = "The Project could not be adopted because portable storage is unavailable; "
                + "this session stays preview-only.\n\n"
                + report;
        }

        SetStatusMessage(
            $"MIDI import completed with {warningCount} warning(s) and {informationCount} information notice(s).",
            isError: adoptionError is not null,
            details: report,
            detailsTitle: "MIDI Import Report");
    }

    private static string BuildMidiImportReport(
        ImportedMidiProject project,
        int warningCount,
        int informationCount)
    {
        StringBuilder report = new();
        report.AppendLine(
            "The MIDI file was imported successfully after applying compatibility or preservation handling.");
        report.AppendLine();
        report.Append("Warnings: ").AppendLine(warningCount.ToString(CultureInfo.InvariantCulture));
        report.Append("Information: ").AppendLine(informationCount.ToString(CultureInfo.InvariantCulture));
        foreach (ImportedMidiDiagnostic diagnostic in project.Diagnostics)
        {
            report.AppendLine();
            report.Append('[').Append(diagnostic.Severity).Append("] ").AppendLine(diagnostic.Code);
            report.AppendLine(diagnostic.Message);
            if (diagnostic.SourceTrackIndex is int trackIndex)
            {
                report.Append("Source: MTrk ").Append(trackIndex);
                if (diagnostic.Tick is long tick)
                {
                    report.Append(", tick ").Append(tick);
                }

                report.AppendLine();
            }
        }

        return report.ToString().TrimEnd();
    }

    private void RefreshArrangementSource()
    {
        if (_arrangementView is null || _midiSource is null)
        {
            return;
        }

        _arrangementView.SetSource(
            _midiSource,
            _midiSource.Project.TicksPerQuarterNote,
            _midiSource.TrackNames,
            segment => _midiSource.GetPreviewSource(segment),
            preserveView: true,
            trackDetails: _midiSource.TrackDetails);
    }

    private void ApplyArrangementSource()
    {
        if (_arrangementView is null)
        {
            return;
        }

        if (_midiSource is { } midi)
        {
            _arrangementView.SetSource(
                midi,
                midi.Project.TicksPerQuarterNote,
                midi.TrackNames,
                segment => midi.GetPreviewSource(segment),
                trackDetails: midi.TrackDetails);
        }
        else
        {
            _arrangementView.SetSource(
                EmptyTimelineSource.Instance,
                480,
                ["Conductor"],
                previewProvider: null,
                trackDetails: ["Tempo & markers"]);
        }
    }

    public void CloseProject()
    {
        StopPlaybackClock();
        PlaybackTick = -1;
        EditCursorTick = -1;
        TimeRangeStartTick = -1;
        TimeRangeEndTick = -1;
        _arrangementView?.SetTimeRange(-1, -1);
        PushOperationStep();
        _projectHost?.Close();
        _midiSource = null;
        _editableProject = null;
        _activeEditView = null;
        _arrangementView = null;
        IsPlaying = false;
        IsCompiling = false;
        HasProject = false;
        IsModified = false;
        ProjectName = "Untitled Project";
        Workspaces.Clear();
        _history.Clear();
        _historyIndex = -1;
        foreach (WorkspaceTab tab in Workspaces)
        {
            tab.SetActive(false);
        }

        ActiveWorkspace = null;
        ClearDiagnostics();
        SetStatusMessage(null);
    }

    public void MarkModified()
    {
        if (HasProject)
        {
            IsModified = true;
        }
    }

    public void SetStatus(string text)
    {
        SetStatusMessage(text);
        if (Environment.GetEnvironmentVariable("MIDORA_PLAYBACK_TRACE") == "1")
        {
            Console.Out.WriteLine($"MIDORA-STATUS {text}");
            Console.Out.Flush();
        }
    }

    public void SetStatusMessage(
        string? text,
        bool isError = false,
        string? details = null,
        string? detailsTitle = null)
    {
        StatusMessageDetails = details;
        StatusMessageDetailsTitle = detailsTitle;
        StatusMessageIsError = isError;
        StatusMessage = text;
    }

    public void DismissStatusMessage()
    {
        StatusMessage = null;
        StatusMessageDetails = null;
        StatusMessageDetailsTitle = null;
        StatusMessageIsError = false;
    }

    public void SetNotice(string text) => Notice = text;

    public void DismissNotice() => Notice = string.Empty;

    public void SetSoundFontState(string text) => SoundFontState = text;

    // Workspace navigation.

    public void OpenWorkspace(WorkspaceKind kind)
    {
        if (!HasProject)
        {
            return;
        }

        var workspace = Workspaces.FirstOrDefault(tab => tab.Kind == kind);
        if (workspace is null)
        {
            workspace = kind switch
            {
                WorkspaceKind.Arrangement => CreateArrangementWorkspace(),
                WorkspaceKind.Diagnostics => new WorkspaceTab(
                    kind,
                    "Diagnostics",
                    new DiagnosticsView(),
                    Icon("Fluent.Pulse20Regular"),
                    canClose: true),
                WorkspaceKind.AllTracks => new WorkspaceTab(
                    kind,
                    "All Tracks",
                    new AllTracksPlaceholderView(),
                    Icon("Fluent.LayerDiagonal20Regular"),
                    canClose: true),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            Workspaces.Add(workspace);
        }

        ActivateWorkspace(workspace, pushHistory: true);
    }

    public void ActivateFromUi(WorkspaceTab? workspace)
    {
        if (workspace is null ||
            !Workspaces.Contains(workspace) ||
            ReferenceEquals(workspace, _activeWorkspace))
        {
            return;
        }

        ActivateWorkspace(workspace, pushHistory: true);
    }

    public void CloseWorkspace(WorkspaceTab workspace)
    {
        if (!workspace.CanClose)
        {
            return;
        }

        int index = Workspaces.IndexOf(workspace);
        if (index < 0)
        {
            return;
        }

        Workspaces.RemoveAt(index);
        _history.Remove(workspace);
        if (ReferenceEquals(_activeWorkspace, workspace))
        {
            var fallback = Workspaces.Count > 0
                ? Workspaces[Math.Min(index, Workspaces.Count - 1)]
                : null;
            if (fallback is not null)
            {
                ActivateWorkspace(fallback, pushHistory: false);
            }
            else
            {
                ActiveWorkspace = null;
            }
        }

        foreach (WorkspaceTab tab in Workspaces)
        {
            tab.SetActive(ReferenceEquals(tab, ActiveWorkspace));
        }

        RaiseDerived();
    }

    public void NavigateBack()
    {
        if (!CanNavigateBack)
        {
            return;
        }

        _historyIndex--;
        ActiveWorkspace = _history[_historyIndex];
        RaiseDerived();
    }

    public void NavigateForward()
    {
        if (!CanNavigateForward)
        {
            return;
        }

        _historyIndex++;
        ActiveWorkspace = _history[_historyIndex];
        RaiseDerived();
    }

    private void ActivateWorkspace(WorkspaceTab workspace, bool pushHistory)
    {
        if (_activeWorkspace is { } previous && !ReferenceEquals(previous, workspace))
        {
            previous.SetActive(false);
        }

        workspace.SetActive(true);
        ActiveWorkspace = workspace;
        _activeEditView = workspace.Content as MidiTrackView;
        if (pushHistory)
        {
            if (_historyIndex >= 0 && _historyIndex < _history.Count - 1)
            {
                _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
            }

            _history.Add(workspace);
            _historyIndex = _history.Count - 1;
        }

        RaiseDerived();
    }

    /// <summary>
    /// Moves the red Playback Cursor. The cursor is session state, so it also moves while stopped
    /// (SRS 20.1.3); a running engine additionally jumps through <c>Seek</c>.
    /// </summary>
    internal void SeekPlaybackCursor(long tick)
    {
        long snapped = SnapToOperationStep(tick);
        PlaybackTick = snapped;
        RealtimePlaybackSession? playback = PlaybackEngine;
        if (playback is not null)
        {
            try
            {
                playback.Seek(snapped);
            }
            catch (Exception exception)
            {
                SetStatus("Playback cursor could not move: " + exception.Message);
                return;
            }
        }

        SetStatus($"Playback cursor at tick {snapped}.");
    }

    /// <summary>Moves the blue dashed Edit Cursor without seeking or clearing the selection.</summary>
    internal void SetEditCursorFromUi(long tick) => EditCursorTick = SnapToOperationStep(tick);

    /// <summary>Stores a committed Time Range Selection as session state.</summary>
    internal void SetTimeRangeFromUi(long startTick, long endTick)
    {
        _arrangementView?.SetTimeRange(startTick, endTick);
        _activeEditView?.SetTimeRange(startTick, endTick);
        TimeRangeStartTick = startTick;
        TimeRangeEndTick = endTick;
        SetStatus($"Time range: {startTick} - {endTick} ticks.");
    }

    /// <summary>Session Time Range Selection start; -1 when no range is selected.</summary>
    public long TimeRangeStartTick { get; private set; } = -1;

    /// <summary>Session Time Range Selection end (exclusive); -1 when no range is selected.</summary>
    public long TimeRangeEndTick { get; private set; } = -1;

    private long SnapToOperationStep(long tick)
    {
        long step = Math.Max(1, OperationStepTicks);
        long clamped = Math.Max(0, tick);
        return step <= 1
            ? clamped
            : Math.Max(0, (long)Math.Round(clamped / (double)step, MidpointRounding.AwayFromZero) * step);
    }

    /// <summary>Persisted application preferences in effect for this shell, when loaded.</summary>
    internal ApplicationPreferences? Preferences { get; private set; }

    /// <summary>Formal realtime playback engine for the open Project, when available.</summary>
    internal RealtimePlaybackSession? PlaybackEngine => _projectHost?.Playback;

    /// <summary>Starts or stops realtime playback of the open Project.</summary>
    public void TogglePlayback()
    {
        if (!HasProject)
        {
            return;
        }

        if (IsPlaying)
        {
            StopPlayback();
            return;
        }

        RealtimePlaybackSession? playback = PlaybackEngine;
        if (playback is null)
        {
            SetStatus(
                _projectHost?.PlaybackFailure
                ?? "Realtime playback needs the formal audio worker for this platform.");
            return;
        }

        try
        {
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            playback.Start(_playbackTick > 0 ? _playbackTick : null);
            stopwatch.Stop();
            TracePlayback($"started in {stopwatch.ElapsedMilliseconds} ms");
        }
        catch (Exception exception)
        {
            SetStatus("Playback could not start: " + exception.Message);
            return;
        }

        IsPlaying = true;
        StartPlaybackClock();
        SetStatus("Playback started.");
        TracePlayback($"started tick={playback.CurrentTick} state={playback.State}");
    }

    public void StopPlayback()
    {
        PlaybackEngine?.Stop();
        if (IsPlaying)
        {
            IsPlaying = false;
            StopPlaybackClock();
            SetStatus("Playback stopped.");
            TracePlayback("stopped");
        }
    }

    /// <summary>Re-applies persisted preferences, rebuilding the audio worker when required.</summary>
    internal async Task ApplyPreferencesAsync(ApplicationPreferences preferences)
    {
        Preferences = preferences;
        if (_projectHost is null)
        {
            return;
        }

        try
        {
            await _projectHost.ApplyPreferencesAsync(preferences).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            SetStatus("Audio settings could not be applied: " + exception.Message);
        }
    }

    /// <summary>Review-only playback trace used by the MIDORA_PLAYBACK_TRACE hook.</summary>
    private static void TracePlayback(string message)
    {
        if (Environment.GetEnvironmentVariable("MIDORA_PLAYBACK_TRACE") == "1")
        {
            Console.Out.WriteLine($"MIDORA-PLAYBACK {message}");
            Console.Out.Flush();
        }
    }

    private void StartPlaybackClock()
    {
        _playbackTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _playbackTimer.Tick -= OnPlaybackTimerTick;
        _playbackTimer.Tick += OnPlaybackTimerTick;
        _playbackTimer.Start();
    }

    private void StopPlaybackClock()
    {
        _playbackTimer?.Stop();
    }

    private void OnPlaybackTimerTick(object? sender, EventArgs e)
    {
        RealtimePlaybackSession? playback = PlaybackEngine;
        if (playback is null)
        {
            StopPlayback();
            return;
        }

        playback.Update();
        if (playback.FailureMessage is { } failure)
        {
            SetStatus(failure);
            StopPlayback();
            return;
        }

        if (!playback.IsActive)
        {
            // The engine reached the end of the rendered range on its own.
            IsPlaying = false;
            StopPlaybackClock();
            SetStatus("Playback completed.");
            TracePlayback($"completed tick={playback.CurrentTick}");
            return;
        }

        long ticksPerQuarterNote = Math.Max(
            1,
            _projectHost?.Document?.Project.TicksPerQuarterNote ?? 480);
        PlaybackTick = playback.CurrentTick;
        UpdatePositionText(PlaybackTick, ticksPerQuarterNote);
        TracePlayback($"tick={PlaybackTick} state={playback.State}");
    }

    private void UpdatePositionText(long tick, long ticksPerQuarterNote)
    {
        long ticksPerBar = ticksPerQuarterNote * 4;
        long bar = tick / ticksPerBar + 1;
        long beat = tick % ticksPerBar / ticksPerQuarterNote + 1;
        long remainder = tick % ticksPerQuarterNote;
        PositionText = $"{bar:0000} : {beat:00} : {remainder:0000}";
    }

    /// <summary>
    /// Runs the real compiler through the Project compilation session and publishes its
    /// diagnostics. The compile runs on a worker thread; the result is applied on the caller.
    /// </summary>
    public async Task CompileAsync()
    {
        if (!CanRunProjectTask)
        {
            if (HasProject && !HasProjectSession)
            {
                SetStatusMessage(
                    "Compile needs a Project-backed session; imported MIDI is still preview data.",
                    isError: true);
            }

            return;
        }

        ProjectSessionHost host = _projectHost!;
        IsCompiling = true;
        CompileState = "Compiling";
        try
        {
            CanonicalCompiledResult result = await Task.Run(host.Compile).ConfigureAwait(true);
            ApplyCompilationResult(result);
        }
        catch (Exception exception)
        {
            CompileState = "Compile Failed";
            SetStatusMessage($"Compilation failed: {exception.Message}", isError: true);
        }
        finally
        {
            IsCompiling = false;
        }
    }

    private void ApplyCompilationResult(CanonicalCompiledResult result)
    {
        Diagnostics.Clear();
        int errors = 0;
        int warnings = 0;
        long count = result.Diagnostics.Count;
        for (long index = 0; index < count; index++)
        {
            CompilerDiagnostic diagnostic = result.Diagnostics[index];
            switch (diagnostic.Severity)
            {
                case DiagnosticSeverity.Error:
                    errors++;
                    break;
                case DiagnosticSeverity.Warning:
                    warnings++;
                    break;
                default:
                    break;
            }

            Diagnostics.Add(new ShellDiagnostic(
                diagnostic.Severity.ToString(),
                diagnostic.Code,
                diagnostic.Message,
                diagnostic.Source.Tick >= 0 ? $"tick {diagnostic.Source.Tick}" : string.Empty));
        }

        ErrorCount = errors;
        WarningCount = warnings;
        IssueSummary = $"{errors} Errors, {warnings} Warnings";
        OnPropertyChanged(nameof(IssueBrush));
        if (result.IsConsumable)
        {
            CompileState = "Compile Succeeded";
            SetStatusMessage($"Compilation completed with {errors} error(s) and {warnings} warning(s).");
        }
        else
        {
            CompileState = "Compile Failed";
            SetStatusMessage(
                $"Compilation failed{(result.FailureStage is { } stage ? $" at {stage}" : string.Empty)}"
                + $" with {errors} error(s) and {warnings} warning(s).",
                isError: true);
        }
    }

    // INotifyPropertyChanged plumbing.

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(TitleBarProjectDisplayName));
        OnPropertyChanged(nameof(CanEditProject));
        OnPropertyChanged(nameof(CanSaveProject));
        OnPropertyChanged(nameof(CanPlayback));
        OnPropertyChanged(nameof(CanRunProjectTask));
        OnPropertyChanged(nameof(CanNavigateBack));
        OnPropertyChanged(nameof(CanNavigateForward));
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(IssueBrush));
        OnPropertyChanged(nameof(PlaybackBrush));
        OnPropertyChanged(nameof(ProjectState));
        OnPropertyChanged(nameof(PlaybackState));
    }

    private static IBrush Brush(string resourceKey, string fallback)
    {
        if (global::Avalonia.Application.Current is { } app &&
            app.TryFindResource(resourceKey, out object? value) &&
            value is IBrush brush)
        {
            return brush;
        }

        return SolidColorBrush.Parse(fallback);
    }

    private static Geometry? Icon(string resourceKey)
    {
        return global::Avalonia.Application.Current is { } app &&
               app.TryFindResource(resourceKey, out object? value)
            ? value as Geometry
            : null;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>One row of the Diagnostics workspace, projected from the compiler result.</summary>
public sealed record ShellDiagnostic(
    string Severity,
    string Code,
    string Message,
    string SourceText);
