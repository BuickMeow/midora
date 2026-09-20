using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
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
    private string _compileState = "Not compiled";
    private string _soundFontState = "SoundFonts: not configured";
    private string _statusText = "Ready";
    private string _issueSummary = "No diagnostics";
    private int _errorCount;
    private int _warningCount;
    private string _positionText = "1.1.000";
    private string _tempoText = "120.00 BPM";
    private readonly DemoTimelineSource _demoSource = DemoTimelineSource.Create();
    private MidiTimelineSource? _midiSource;
    private ArrangementView? _arrangementView;

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

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string IssueSummary
    {
        get => _issueSummary;
        private set => Set(ref _issueSummary, value);
    }

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
        ? $"{ProjectName}{(IsModified ? "  ●" : string.Empty)}"
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

    // Project lifecycle.

    public void CreateProject(string name)
    {
        _midiSource = null;
        Workspaces.Clear();
        _history.Clear();
        _historyIndex = -1;

        ProjectName = string.IsNullOrWhiteSpace(name) ? "Untitled Project" : name.Trim();
        HasProject = true;
        IsModified = false;
        IsPlaying = false;
        IsCompiling = false;
        CompileState = "Not compiled";
        ErrorCount = 0;
        WarningCount = 0;
        IssueSummary = "No diagnostics";
        PositionText = "1.1.000";
        TempoText = "120.00 BPM";
        StatusText = $"Project '{ProjectName}' created (in-memory port placeholder).";

        OpenWorkspace(WorkspaceKind.Arrangement);
        ApplyArrangementSource();
    }

    private WorkspaceTab CreateArrangementWorkspace()
    {
        _arrangementView = new ArrangementView();
        _arrangementView.TrackActivated += (_, trackIndex) => OpenMidiTrackWorkspace(trackIndex);
        return new WorkspaceTab(
            WorkspaceKind.Arrangement,
            "Arrangement",
            _arrangementView,
            Icon("Fluent.MusicNote120Regular"),
            canClose: false);
    }

    /// <summary>
    /// Opens (or activates) the per-track MIDI editor workspace for the imported project.
    /// </summary>
    public void OpenMidiTrackWorkspace(int trackIndex)
    {
        if (!HasProject ||
            _midiSource is null ||
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
            view.SetProject(_midiSource, trackIndex);
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

    /// <summary>
    /// Replaces the current project with a real imported SMF project and shows it in the
    /// Arrangement workspace.
    /// </summary>
    public void CreateProjectFromMidi(string name, MidiTimelineSource source)
    {
        CreateProject(name);
        _midiSource = source;
        ApplyArrangementSource();

        if (source.Project.Conductor.FirstOrDefault(
                conductorEvent => conductorEvent.Kind == ImportedConductorKind.Tempo) is { Value: > 0 } tempo)
        {
            TempoText = $"{tempo.Value:0.00} BPM";
        }

        StatusText =
            $"Imported '{source.Project.SourceFileName}' · {source.Project.Tracks.Count} track(s) · " +
            $"{source.Project.TicksPerQuarterNote} TPQN · {source.Project.MaximumEndTick} ticks.";
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
                segment => midi.GetPreviewSource(segment));
        }
        else
        {
            _arrangementView.SetSource(
                _demoSource,
                480,
                _demoSource.TrackNames,
                segment => _demoSource.GetPreviewSource(segment));
        }
    }

    public void CloseProject()
    {
        _midiSource = null;
        _arrangementView = null;
        IsPlaying = false;
        IsCompiling = false;
        HasProject = false;
        IsModified = false;
        ProjectName = "Untitled Project";
        Workspaces.Clear();
        _history.Clear();
        _historyIndex = -1;
        ActiveWorkspace = null;
        CompileState = "Not compiled";
        ErrorCount = 0;
        WarningCount = 0;
        IssueSummary = "No diagnostics";
        StatusText = "Project closed.";
    }

    public void MarkModified()
    {
        if (HasProject)
        {
            IsModified = true;
        }
    }

    public void SetStatus(string text) => StatusText = text;

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
        ActiveWorkspace = workspace;
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
        StatusText = IsPlaying ? "Playing (placeholder transport)." : "Stopped.";
    }

    public void StopPlayback()
    {
        if (IsPlaying)
        {
            IsPlaying = false;
            StatusText = "Stopped.";
        }
    }

    public async Task CompileAsync()
    {
        if (!CanRunProjectTask)
        {
            return;
        }

        IsCompiling = true;
        CompileState = "Compiling…";
        StatusText = "Compiling Project…";
        try
        {
            await Task.Delay(600);
            CompileState = "Compiled (placeholder)";
            StatusText = "Compilation finished (port placeholder, no canonical result yet).";
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
        IssueSummary = errors == 0 && warnings == 0
            ? "No diagnostics"
            : $"{errors} error(s), {warnings} warning(s)";
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
