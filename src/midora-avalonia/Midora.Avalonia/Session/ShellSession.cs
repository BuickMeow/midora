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
using Midora.Avalonia.Editing;
using Midora.Avalonia.Import;
using Midora.Avalonia.Presentation.Rendering;
using Midora.Avalonia.Views;

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

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<WorkspaceTab> Workspaces { get; } = [];

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
        set => Set(ref _isSnapEnabled, value);
    }

    public bool IsGridVisible
    {
        get => _isGridVisible;
        set => Set(ref _isGridVisible, value);
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

    /// <summary>WPF <c>ProjectState</c>: a port Project always has an in-memory document.</summary>
    public string ProjectState => HasProject
        ? IsModified ? "Modified · Unsaved" : "Unsaved"
        : "No Project";

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

    public bool CanSaveProject => HasProject && IsModified;

    public bool CanPlayback => HasProject && !IsPlaying;

    public bool CanRunProjectTask => HasProject && !IsCompiling;

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

    public void CreateProject(string name)
    {
        _midiSource = null;
        _editableProject = null;
        _activeEditView = null;
        Workspaces.Clear();
        _history.Clear();
        _historyIndex = -1;

        ProjectName = string.IsNullOrWhiteSpace(name) ? "Untitled Project" : name.Trim();
        HasProject = true;
        IsModified = false;
        IsPlaying = false;
        IsCompiling = false;
        CompileState = "Not Compiled";
        ErrorCount = 0;
        WarningCount = 0;
        IssueSummary = "0 Errors, 0 Warnings";
        PositionText = "0001 : 01 : 0000";
        TempoText = "120.00 BPM";
        Notice = string.Empty;
        SetStatusMessage(null);

        OpenWorkspace(WorkspaceKind.Arrangement);
        ApplyArrangementSource();
    }

    private WorkspaceTab CreateArrangementWorkspace()
    {
        _arrangementView = new ArrangementView { DataContext = this };
        _arrangementView.TrackActivated += (_, trackIndex) => OpenMidiTrackWorkspace(trackIndex);
        _arrangementView.SegmentActivated += (_, activation) =>
            OpenMidiSegmentWorkspace(activation.TrackIndex, activation.StartTick);
        _arrangementView.AllTracksRequested += (_, _) => OpenWorkspace(WorkspaceKind.AllTracks);
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

        long ticksPerBar = Math.Max(1, 4L * _midiSource.Project.TicksPerQuarterNote);
        long chunkTicks = Math.Max(1, _midiSource.BarsPerSegment * ticksPerBar);
        long chunkStart = startTick / chunkTicks * chunkTicks;
        var workspace = Workspaces.FirstOrDefault(
            tab => tab.Kind == WorkspaceKind.MidiTrack &&
                   tab.TrackIndex == trackIndex &&
                   tab.SegmentStartTick == chunkStart);
        if (workspace is null)
        {
            var view = new MidiTrackView();
            view.SetProject(_editableProject, trackIndex);
            view.SetSegmentRange(chunkStart, chunkTicks);
            view.Edited += (_, _) => MarkModified();
            string trackName = trackIndex + 1 < _midiSource.TrackNames.Count
                ? _midiSource.TrackNames[trackIndex + 1]
                : $"Track {trackIndex + 1}";
            workspace = new WorkspaceTab(
                WorkspaceKind.MidiTrack,
                $"MIDI Segment: {trackName}@{chunkStart / ticksPerBar + 1}",
                view,
                Icon("Fluent.Midi20Regular"),
                canClose: true,
                trackIndex,
                chunkStart);
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
    public void CreateProjectFromMidi(string name, ImportedMidiProject project)
    {
        CreateProject(name);
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

        int warningCount = project.Diagnostics.Count(
            diagnostic => diagnostic.Severity == ImportedMidiDiagnosticSeverity.Warning);
        int informationCount = project.Diagnostics.Count(
            diagnostic => diagnostic.Severity == ImportedMidiDiagnosticSeverity.Info);
        SetStatusMessage(
            $"MIDI import completed with {warningCount} warning(s) and {informationCount} information notice(s).",
            isError: false,
            details: BuildMidiImportReport(project, warningCount, informationCount),
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
        CompileState = "Not Compiled";
        ErrorCount = 0;
        WarningCount = 0;
        IssueSummary = "0 Errors, 0 Warnings";
        SetStatusMessage(null);
    }

    public void MarkModified()
    {
        if (HasProject)
        {
            IsModified = true;
        }
    }

    public void SetStatus(string text) => SetStatusMessage(text);

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

    // Transport / compile placeholders.

    public void TogglePlayback()
    {
        if (!HasProject)
        {
            return;
        }

        IsPlaying = !IsPlaying;
        if (IsPlaying)
        {
            StartPlaybackClock();
        }
        else
        {
            StopPlaybackClock();
        }
    }

    public void StopPlayback()
    {
        if (IsPlaying)
        {
            IsPlaying = false;
            StopPlaybackClock();
        }
    }

    private void StartPlaybackClock()
    {
        _playbackTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _playbackTimer.Tick -= OnPlaybackTimerTick;
        _playbackTimer.Tick += OnPlaybackTimerTick;
        _playbackClock.Restart();
        _playbackTimer.Start();
    }

    private void StopPlaybackClock()
    {
        _playbackTimer?.Stop();
        _playbackClock.Reset();
    }

    private void OnPlaybackTimerTick(object? sender, EventArgs e)
    {
        double seconds = _playbackClock.Elapsed.TotalSeconds;
        _playbackClock.Restart();

        long ticksPerQuarterNote = Math.Max(1, _midiSource?.Project.TicksPerQuarterNote ?? 480);
        long advance = Math.Max(1, (long)(seconds * _tempoBpm / 60.0 * ticksPerQuarterNote));
        long maximum = Math.Max(1, _midiSource?.MaximumEndTick ?? _demoSource.MaximumEndTick);
        long next = _playbackTick + advance;
        if (next >= maximum)
        {
            next = IsLoopEnabled ? 0 : maximum - 1;
            if (!IsLoopEnabled)
            {
                StopPlayback();
            }
        }

        PlaybackTick = next;
        UpdatePositionText(next, ticksPerQuarterNote);
    }

    private void UpdatePositionText(long tick, long ticksPerQuarterNote)
    {
        long ticksPerBar = ticksPerQuarterNote * 4;
        long bar = tick / ticksPerBar + 1;
        long beat = tick % ticksPerBar / ticksPerQuarterNote + 1;
        long remainder = tick % ticksPerQuarterNote;
        PositionText = $"{bar:0000} : {beat:00} : {remainder:0000}";
    }

    public async Task CompileAsync()
    {
        if (!CanRunProjectTask)
        {
            return;
        }

        IsCompiling = true;
        CompileState = "Compiling";
        try
        {
            await Task.Delay(600);
            CompileState = "Compile Succeeded";
            SetStatusMessage("Compilation completed successfully.");
        }
        finally
        {
            IsCompiling = false;
        }
    }

    public void ResetDiagnostics(int errors, int warnings)
    {
        ErrorCount = errors;
        WarningCount = warnings;
        IssueSummary = $"{errors} Errors, {warnings} Warnings";
        OnPropertyChanged(nameof(IssueBrush));
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
        if (Application.Current is { } app &&
            app.TryFindResource(resourceKey, out object? value) &&
            value is IBrush brush)
        {
            return brush;
        }

        return SolidColorBrush.Parse(fallback);
    }

    private static Geometry? Icon(string resourceKey)
    {
        return Application.Current is { } app &&
               app.TryFindResource(resourceKey, out object? value)
            ? value as Geometry
            : null;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
