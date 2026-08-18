using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Midora.Compiler;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Desktop;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public enum ProjectTreeNodeKind
{
    Conductor,
    InstrumentLibrary,
    InstrumentFolder,
    EventInstrument,
    DamagedEventInstrument,
    LogicalTracks,
    LogicalTrack,
    DamagedLogicalTrack,
    ProjectSettings,
    Diagnostics
}

public sealed class ProjectTreeNode(
    ProjectTreeNodeKind kind,
    string title,
    MidoraId? objectId = null,
    string subtitle = "") : ObservableObject
{
    private string _title = title;
    private string _editText = title;
    private bool _isRenaming;

    public ProjectTreeNodeKind Kind { get; } = kind;
    public MidoraId? ObjectId { get; } = objectId;
    public string Subtitle { get; } = subtitle;
    public bool IsDamaged => Kind is ProjectTreeNodeKind.DamagedEventInstrument
        or ProjectTreeNodeKind.DamagedLogicalTrack;
    public bool IsInstrumentFolder => Kind == ProjectTreeNodeKind.InstrumentFolder;
    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }
    public string EditText { get => _editText; set => Set(ref _editText, value); }
    public bool IsRenaming { get => _isRenaming; set => Set(ref _isRenaming, value); }
    public ObservableCollection<ProjectTreeNode> Children { get; } = [];
}

public sealed record DiagnosticRow(
    string Severity,
    string Category,
    string Code,
    string Message,
    string Source,
    bool IsCurrent,
    SourceReference SourceReference)
{
    public string Status => IsCurrent
        ? "Active"
        : string.Equals(Category, "Runtime", StringComparison.OrdinalIgnoreCase)
            ? "Runtime History"
            : string.Equals(Category, "Compile", StringComparison.OrdinalIgnoreCase)
                ? "Prior Result"
                : "Resolved";
}

public enum DesktopTaskLockLevel
{
    ProjectEdit = 2,
    MainWindow = 3,
    FullApplication = 4
}

public sealed class DesktopTaskViewModel : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private string _status = "Running";
    private string _detail = string.Empty;
    private double? _progress;
    private bool _isRunning = true;
    private bool _isCancellationAvailable = true;
    private DateTimeOffset? _completedAt;

    public DesktopTaskViewModel(string name, bool canCancel, DesktopTaskLockLevel lockLevel)
    {
        Name = name;
        CanCancel = canCancel;
        LockLevel = lockLevel;
        StartedAt = DateTimeOffset.Now;
    }

    public string Name { get; }
    public DesktopTaskLockLevel LockLevel { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? CompletedAt { get => _completedAt; private set => Set(ref _completedAt, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public double? Progress { get => _progress; private set => Set(ref _progress, value); }
    public bool IsRunning { get => _isRunning; private set { if (Set(ref _isRunning, value)) Raise(nameof(CanRequestCancel)); } }
    public bool CanCancel { get; }
    public bool CanRequestCancel => CanCancel && IsCancellationAvailable && IsRunning && !_cancellation.IsCancellationRequested;
    public bool IsCancellationAvailable
    {
        get => _isCancellationAvailable;
        private set
        {
            if (Set(ref _isCancellationAvailable, value)) Raise(nameof(CanRequestCancel));
        }
    }
    public CancellationToken CancellationToken => _cancellation.Token;

    public void Report(string detail, double? progress = null)
    {
        if (!IsRunning) return;
        Detail = detail ?? string.Empty;
        Progress = progress is null ? null : Math.Clamp(progress.Value, 0, 1);
    }

    public void RequestCancel()
    {
        if (!CanRequestCancel) return;
        _cancellation.Cancel();
        Status = "Cancelling";
        Raise(nameof(CanRequestCancel));
    }

    public void SetCancellationAvailable(bool available) => IsCancellationAvailable = available;

    public void Complete(string status, string detail = "")
    {
        Status = status;
        Detail = detail;
        Progress = status == "Succeeded" ? 1 : Progress;
        IsRunning = false;
        CompletedAt = DateTimeOffset.Now;
    }

    public void Dispose() => _cancellation.Dispose();
}

public enum InspectorFieldValueState
{
    SameValue,
    Mixed,
    Unavailable
}

public sealed record InspectorChoiceOption(string Value, string Label);

public sealed class InspectorField(
    string key,
    string label,
    string value,
    bool isEditable = true,
    InspectorFieldValueState valueState = InspectorFieldValueState.SameValue,
    IReadOnlyList<string>? options = null,
    bool isBoolean = false,
    IReadOnlyList<InspectorChoiceOption>? choices = null) : ObservableObject
{
    private string _value = value;
    private bool _booleanValue = bool.TryParse(value, out bool parsed) && parsed;

    public string Key { get; } = key;
    public string Label { get; } = label;
    public bool IsEditable { get; } = isEditable;
    public InspectorFieldValueState ValueState { get; } = valueState;
    public bool IsMixed => ValueState == InspectorFieldValueState.Mixed;
    public bool IsUnavailable => ValueState == InspectorFieldValueState.Unavailable;
    public IReadOnlyList<string> Options { get; } = options ?? [];
    public IReadOnlyList<InspectorChoiceOption> Choices { get; } = choices
        ?? (options ?? []).Select(option => new InspectorChoiceOption(option, option)).ToArray();
    public bool IsChoice => Choices.Count > 0;
    public bool IsBoolean { get; } = isBoolean;
    public string Value
    {
        get => _value;
        set => Set(ref _value, value);
    }
    public bool BooleanValue
    {
        get => _booleanValue;
        set
        {
            if (!Set(ref _booleanValue, value)) return;
            Value = value ? bool.TrueString : bool.FalseString;
        }
    }
}

public sealed class InspectorViewModel : ObservableObject
{
    private string _title = "No selection";
    private string _context = "Select an object in the active Workspace.";
    private string? _errorText;

    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Context { get => _context; private set => Set(ref _context, value); }
    public string? ErrorText
    {
        get => _errorText;
        set
        {
            if (Set(ref _errorText, value)) Raise(nameof(HasError));
        }
    }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);
    public ObservableCollection<InspectorField> Fields { get; } = [];

    public void Replace(string title, string context, IEnumerable<InspectorField> fields)
    {
        Title = title;
        Context = context;
        ErrorText = null;
        Fields.Clear();
        foreach (InspectorField field in fields) Fields.Add(field);
    }
}

public abstract class WorkspaceViewModel(
    WorkspaceKey key,
    string header) : ObservableObject
{
    private string _header = header;
    private int? _activeLane;
    private TimelineSelectionSnapshot _selectionSnapshot = new(0, [], null);

    public WorkspaceKey Key { get; } = key;
    public WorkspaceKind Kind => Key.Kind;
    public MidoraId? ObjectId => Key.ObjectId;
    public bool CanClose => Kind != WorkspaceKind.Arrangement;
    public bool CanReorder => Kind != WorkspaceKind.Arrangement;
    public string Header
    {
        get => _header;
        protected set => Set(ref _header, value);
    }
    public WorkspaceSelection Selection { get; } = new();
    public TimelineSelectionSnapshot SelectionSnapshot
    {
        get => _selectionSnapshot;
        private set => Set(ref _selectionSnapshot, value);
    }
    public int? ActiveLane
    {
        get => _activeLane;
        set => Set(ref _activeLane, value is null ? null : Math.Max(0, value.Value));
    }

    public abstract void Rebuild(MidoraProject project, long revision);

    public void RefreshSelectionPresentation() =>
        SelectionSnapshot = new(Selection.Revision, Selection.Ids, Selection.Primary);
}

public enum TimelineWorkspaceMode
{
    Arrangement,
    Segment,
    Conductor
}

public readonly record struct TimelineSubdivision(
    int Numerator,
    int Denominator,
    string Label,
    bool IsBar = false)
{
    public string ShortLabel => IsBar ? "Bar" : $"{Numerator}/{Denominator}";

    public long ToTicks(int ticksPerQuarterNote, int barNumerator = 4, int barDenominator = 4)
    {
        if (ticksPerQuarterNote <= 0) throw new ArgumentOutOfRangeException(nameof(ticksPerQuarterNote));
        if (IsBar)
        {
            if (barNumerator <= 0 || barDenominator <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(barNumerator));
            }
            return Math.Max(1, checked((long)Math.Ceiling(
                ticksPerQuarterNote * 4d * barNumerator / barDenominator)));
        }
        if (Numerator <= 0 || Denominator <= 0)
        {
            throw new InvalidOperationException("Timeline subdivisions must be positive.");
        }
        return Math.Max(1, checked((long)Math.Ceiling(
            ticksPerQuarterNote * 4d * Numerator / Denominator)));
    }

    public override string ToString() => ShortLabel;

    public static bool TryParse(string? text, out TimelineSubdivision value)
    {
        string normalized = text?.Trim() ?? string.Empty;
        if (string.Equals(normalized, "Bar", StringComparison.OrdinalIgnoreCase))
        {
            value = Presets[0];
            return true;
        }
        int slash = normalized.IndexOf('/');
        int denominatorEnd = slash + 1;
        while (denominatorEnd < normalized.Length && char.IsAsciiDigit(normalized[denominatorEnd]))
        {
            denominatorEnd++;
        }
        if (slash > 0
            && int.TryParse(normalized[..slash], out int numerator)
            && denominatorEnd > slash + 1
            && int.TryParse(normalized[(slash + 1)..denominatorEnd], out int denominator)
            && numerator > 0
            && denominator > 0)
        {
            value = new(numerator, denominator, $"{numerator}/{denominator}");
            return true;
        }
        if (int.TryParse(normalized, out int customDenominator) && customDenominator > 0)
        {
            value = new(1, customDenominator, $"1/{customDenominator}");
            return true;
        }
        value = default;
        return false;
    }

    public static IReadOnlyList<TimelineSubdivision> Presets { get; } =
    [
        new(1, 1, "Bar", IsBar: true),
        new(1, 1, "1/1 · Whole"),
        new(1, 2, "1/2 · Half"),
        new(1, 3, "1/3 · Half triplet"),
        new(1, 4, "1/4 · Quarter"),
        new(1, 6, "1/6 · Quarter triplet"),
        new(3, 16, "3/16 · Dotted eighth"),
        new(1, 8, "1/8 · Eighth"),
        new(1, 12, "1/12 · Eighth triplet"),
        new(3, 32, "3/32 · Dotted sixteenth"),
        new(1, 16, "1/16 · Sixteenth"),
        new(1, 24, "1/24 · Sixteenth triplet"),
        new(3, 64, "3/64 · Dotted thirty-second"),
        new(1, 32, "1/32 · Thirty-second"),
        new(1, 48, "1/48 · Thirty-second triplet"),
        new(1, 64, "1/64 · Sixty-fourth"),
        new(1, 128, "1/128"),
        new(1, 256, "1/256")
    ];
}

public sealed class TimelineEditorSettings : ObservableObject
{
    private TimelineSubdivision _displaySubdivision = new(1, 4, "1/4 · Quarter");
    private TimelineSubdivision _operationSubdivision = new(1, 16, "1/16 · Sixteenth");
    private bool _snapEnabled = true;
    private bool _gridVisible = true;
    private long _defaultLengthTicks = 768;
    private int _defaultVelocity = 100;
    private int _ticksPerQuarterNote = 768;
    private int _barNumerator = 4;
    private int _barDenominator = 4;
    private ProjectTimeSignatureMap? _timeSignatureMap;

    public IReadOnlyList<TimelineSubdivision> SubdivisionPresets => TimelineSubdivision.Presets;
    public TimelineSubdivision DisplaySubdivision
    {
        get => _displaySubdivision;
        set
        {
            if (!Set(ref _displaySubdivision, value)) return;
            Raise(nameof(DisplayGridStepTicks));
            Raise(nameof(DisplayGridUsesBars));
            Raise(nameof(DisplayGridLabel));
            Raise(nameof(DisplaySubdivisionText));
        }
    }
    public TimelineSubdivision OperationSubdivision
    {
        get => _operationSubdivision;
        set
        {
            if (!Set(ref _operationSubdivision, value)) return;
            Raise(nameof(OperationStepTicks));
            Raise(nameof(EffectiveOperationStepTicks));
            Raise(nameof(EffectiveOperationUsesBars));
            Raise(nameof(OperationGridLabel));
            Raise(nameof(OperationSubdivisionText));
        }
    }
    public bool SnapEnabled
    {
        get => _snapEnabled;
        set
        {
            if (!Set(ref _snapEnabled, value)) return;
            Raise(nameof(EffectiveOperationStepTicks));
            Raise(nameof(EffectiveOperationUsesBars));
        }
    }
    public bool GridVisible { get => _gridVisible; set => Set(ref _gridVisible, value); }
    public long DefaultLengthTicks
    {
        get => _defaultLengthTicks;
        set => Set(ref _defaultLengthTicks, Math.Max(1, value));
    }
    public int DefaultVelocity
    {
        get => _defaultVelocity;
        set => Set(ref _defaultVelocity, Math.Clamp(value, 1, 127));
    }
    public long DisplayGridStepTicks => DisplaySubdivision.ToTicks(
        _ticksPerQuarterNote, _barNumerator, _barDenominator);
    public long OperationStepTicks => OperationSubdivision.ToTicks(
        _ticksPerQuarterNote, _barNumerator, _barDenominator);
    public long EffectiveOperationStepTicks => SnapEnabled ? OperationStepTicks : 1;
    public bool DisplayGridUsesBars => DisplaySubdivision.IsBar;
    public bool EffectiveOperationUsesBars => SnapEnabled && OperationSubdivision.IsBar;
    public ProjectTimeSignatureMap? TimeSignatureMap => _timeSignatureMap;
    public string DisplayGridLabel => $"Grid {DisplaySubdivision.Label}";
    public string OperationGridLabel => $"Step {OperationSubdivision.Label}";
    public string DisplaySubdivisionText
    {
        get => DisplaySubdivision.IsBar
            ? "Bar"
            : $"{DisplaySubdivision.Numerator}/{DisplaySubdivision.Denominator}";
        set
        {
            if (TimelineSubdivision.TryParse(value, out TimelineSubdivision parsed))
            {
                DisplaySubdivision = parsed;
            }
        }
    }
    public string OperationSubdivisionText
    {
        get => OperationSubdivision.IsBar
            ? "Bar"
            : $"{OperationSubdivision.Numerator}/{OperationSubdivision.Denominator}";
        set
        {
            if (TimelineSubdivision.TryParse(value, out TimelineSubdivision parsed))
            {
                OperationSubdivision = parsed;
            }
        }
    }

    public void ConfigureProject(MidoraProject project, long referenceTick)
    {
        ArgumentNullException.ThrowIfNull(project);
        _ticksPerQuarterNote = project.TicksPerQuarterNote;
        _timeSignatureMap = new ProjectTimeSignatureMap(project);
        TimeSignatureChange? signature = project.Conductor.TimeSignatures
            .Where(item => item.Tick <= Math.Max(0, referenceTick))
            .OrderByDescending(item => item.Tick)
            .FirstOrDefault();
        _barNumerator = signature?.Numerator ?? 4;
        _barDenominator = signature?.Denominator ?? 4;
        Raise(nameof(DisplayGridStepTicks));
        Raise(nameof(OperationStepTicks));
        Raise(nameof(EffectiveOperationStepTicks));
        Raise(nameof(TimeSignatureMap));
    }

    public long SnapAbsolute(long tick, int movementDirection = 0) =>
        TimelineGridQuantization.SnapAbsolute(
            Math.Max(0, tick),
            EffectiveOperationStepTicks,
            EffectiveOperationUsesBars,
            TimeSignatureMap,
            movementDirection);

    public long SnapDelta(long delta, long targetTick) =>
        TimelineGridQuantization.SnapDelta(
            delta,
            Math.Max(0, targetTick),
            EffectiveOperationStepTicks,
            EffectiveOperationUsesBars,
            TimeSignatureMap);

    public void Reset(bool arrangement, int ticksPerQuarterNote = 768)
    {
        _ticksPerQuarterNote = Math.Max(1, ticksPerQuarterNote);
        _timeSignatureMap = null;
        DisplaySubdivision = arrangement
            ? TimelineSubdivision.Presets.Single(item => item.IsBar)
            : TimelineSubdivision.Presets.Single(item => !item.IsBar && item.Numerator == 1 && item.Denominator == 4);
        OperationSubdivision = TimelineSubdivision.Presets.Single(item =>
            !item.IsBar
            && item.Numerator == 1
            && item.Denominator == (arrangement ? 8 : 16));
        SnapEnabled = true;
        GridVisible = true;
        DefaultLengthTicks = _ticksPerQuarterNote;
        DefaultVelocity = 100;
        ConfigureBarDefaults();
        Raise(nameof(TimeSignatureMap));
    }

    private void ConfigureBarDefaults()
    {
        _barNumerator = 4;
        _barDenominator = 4;
        Raise(nameof(DisplayGridStepTicks));
        Raise(nameof(OperationStepTicks));
        Raise(nameof(EffectiveOperationStepTicks));
    }
}

public sealed record ConductorEventRow(
    MidoraId Id,
    long Tick,
    string Type,
    string Value);

public sealed record ParameterLaneOption(
    MidoraId ParameterId,
    MidoraId? LaneId,
    string Label,
    bool IsBroken = false);

public sealed class TrackSelectionRow(MidoraId id, string name, bool isSelected) : ObservableObject
{
    private bool _isSelected = isSelected;
    public MidoraId Id { get; } = id;
    public string Name { get; } = name;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

public sealed class TimelineWorkspaceViewModel : WorkspaceViewModel
{
    private readonly Dictionary<MidoraId, SegmentPreviewCacheEntry> _segmentPreviewCache = [];
    private readonly HashSet<MidoraId> _mutedTrackIds = [];
    private readonly HashSet<MidoraId> _soloTrackIds = [];
    private TimelineRenderSnapshot? _snapshot;
    private TimelineRenderSnapshot? _rulerSnapshot;
    private TimelineRenderSnapshot? _parameterSnapshot;
    private TimelineRenderSnapshot? _velocitySnapshot;
    private int _activeParameterLaneIndex;
    private string _context = string.Empty;
    private long _startTick;
    private long _tickSpan;
    private int _firstLane;
    private double _laneHeight;
    private long? _rangeStartTick;
    private long? _rangeEndTick;
    private long? _timeRangeStartTick;
    private long? _timeRangeEndTick;
    private long _timelineExtentEndTick = 3072;
    private long? _editCursorTick;
    private long? _playbackCursorTick;
    private bool _viewportInitialized;
    private TimelineToolMode _toolMode = TimelineToolMode.Select;
    private double _activeValueMinimum;
    private double _activeValueMaximum = 127;
    private bool _activeValueIntegral = true;
    private bool _isLowerEditorVisible = true;
    private double _lowerEditorHeight = 190;

    public TimelineWorkspaceViewModel(
        WorkspaceKey key,
        string header,
        TimelineWorkspaceMode mode,
        TimelineEditorSettings? editorSettings = null)
        : base(key, header)
    {
        Mode = mode;
        EditorSettings = editorSettings ?? new TimelineEditorSettings();
        EditorSettings.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(TimelineEditorSettings.DisplayGridStepTicks)
                or nameof(TimelineEditorSettings.DisplayGridLabel)
                or nameof(TimelineEditorSettings.EffectiveOperationStepTicks)
                or nameof(TimelineEditorSettings.GridVisible))
            {
                Raise(nameof(GridStepTicks));
                Raise(nameof(OperationStepTicks));
                Raise(nameof(GridLabel));
                Raise(nameof(GridVisible));
            }
        };
        _laneHeight = mode switch
        {
            TimelineWorkspaceMode.Arrangement => 56,
            TimelineWorkspaceMode.Conductor => 27,
            _ => 18
        };
        _firstLane = mode == TimelineWorkspaceMode.Segment ? 48 : 0;
        _tickSpan = 3072;
    }

    public TimelineWorkspaceMode Mode { get; }
    public TimelineEditorSettings EditorSettings { get; }
    public TimelineEditorSettings LaneEditorSettings { get; } = new();
    public bool IsSegment => Mode == TimelineWorkspaceMode.Segment;
    public bool IsConductor => Mode == TimelineWorkspaceMode.Conductor;
    public bool IsArrangement => Mode == TimelineWorkspaceMode.Arrangement;
    public bool IsLowerEditorVisible
    {
        get => IsSegment && _isLowerEditorVisible;
        set
        {
            if (!IsSegment || !Set(ref _isLowerEditorVisible, value)) return;
            Raise(nameof(BottomEditorRowHeight));
        }
    }
    public GridLength BottomEditorRowHeight
    {
        get => IsConductor
            ? new GridLength(170)
            : IsLowerEditorVisible
                ? new GridLength(_lowerEditorHeight)
                : new GridLength(0);
        set
        {
            if (!IsSegment || value.GridUnitType != GridUnitType.Pixel) return;
            double height = Math.Clamp(value.Value, 110, 520);
            if (!Set(ref _lowerEditorHeight, height, nameof(BottomEditorRowHeight))) return;
        }
    }
    public ObservableCollection<ConductorEventRow> ConductorEvents { get; } = [];
    public TimelineSurfaceMode SurfaceMode => Mode switch
    {
        TimelineWorkspaceMode.Arrangement => TimelineSurfaceMode.Arrangement,
        TimelineWorkspaceMode.Segment => TimelineSurfaceMode.PianoRoll,
        TimelineWorkspaceMode.Conductor => TimelineSurfaceMode.Conductor,
        _ => TimelineSurfaceMode.General
    };
    public TimelineToolMode ToolMode
    {
        get => _toolMode;
        set
        {
            if (!Set(ref _toolMode, value)) return;
            Raise(nameof(IsSelectTool));
            Raise(nameof(IsDrawTool));
            Raise(nameof(IsEraseTool));
            Raise(nameof(IsSplitTool));
        }
    }
    public bool IsSelectTool => ToolMode == TimelineToolMode.Select;
    public bool IsDrawTool => ToolMode == TimelineToolMode.Draw;
    public bool IsEraseTool => ToolMode == TimelineToolMode.Erase;
    public bool IsSplitTool => ToolMode == TimelineToolMode.Split;
    public bool GridVisible
    {
        get => EditorSettings.GridVisible;
        set => EditorSettings.GridVisible = value;
    }
    public string GridLabel => EditorSettings.DisplayGridLabel;
    public TimelineRenderSnapshot? Snapshot
    {
        get => _snapshot;
        private set => Set(ref _snapshot, value);
    }
    public TimelineRenderSnapshot? RulerSnapshot
    {
        get => _rulerSnapshot;
        private set => Set(ref _rulerSnapshot, value);
    }
    public TimelineRenderSnapshot? ParameterSnapshot
    {
        get => _parameterSnapshot;
        private set => Set(ref _parameterSnapshot, value);
    }
    public TimelineRenderSnapshot? VelocitySnapshot
    {
        get => _velocitySnapshot;
        private set => Set(ref _velocitySnapshot, value);
    }
    public ObservableCollection<ParameterLaneOption> ParameterLaneOptions { get; } = [];
    public int ActiveParameterLaneIndex
    {
        get => _activeParameterLaneIndex;
        set => Set(ref _activeParameterLaneIndex, Math.Max(-1, value));
    }
    public ParameterLaneOption? GetActiveParameterLaneOption() =>
        ActiveParameterLaneIndex >= 0 && ActiveParameterLaneIndex < ParameterLaneOptions.Count
            ? ParameterLaneOptions[ActiveParameterLaneIndex]
            : null;
    public double ActiveValueMinimum
    {
        get => _activeValueMinimum;
        private set => Set(ref _activeValueMinimum, value);
    }
    public double ActiveValueMaximum
    {
        get => _activeValueMaximum;
        private set => Set(ref _activeValueMaximum, value);
    }
    public bool ActiveValueIntegral
    {
        get => _activeValueIntegral;
        private set => Set(ref _activeValueIntegral, value);
    }
    public string Context
    {
        get => _context;
        private set => Set(ref _context, value);
    }
    public long StartTick
    {
        get => _startTick;
        set => Set(ref _startTick, Math.Max(0, value));
    }
    public long TickSpan
    {
        get => _tickSpan;
        set => Set(ref _tickSpan, Math.Max(16, value));
    }
    public int FirstLane
    {
        get => _firstLane;
        set => Set(ref _firstLane, Math.Max(0, value));
    }
    public double LaneHeight
    {
        get => _laneHeight;
        set => Set(ref _laneHeight, Math.Clamp(value, 8, 128));
    }
    public long GridStepTicks => EditorSettings.DisplayGridStepTicks;
    public long OperationStepTicks => EditorSettings.EffectiveOperationStepTicks;
    public long? RangeStartTick
    {
        get => _rangeStartTick;
        private set => Set(ref _rangeStartTick, value);
    }
    public long? RangeEndTick
    {
        get => _rangeEndTick;
        private set => Set(ref _rangeEndTick, value);
    }
    public long? TimeRangeStartTick
    {
        get => _timeRangeStartTick;
        private set => Set(ref _timeRangeStartTick, value);
    }
    public long? TimeRangeEndTick
    {
        get => _timeRangeEndTick;
        private set => Set(ref _timeRangeEndTick, value);
    }
    public bool HasTimeRange => TimeRangeStartTick.HasValue && TimeRangeEndTick > TimeRangeStartTick;
    public long TimelineExtentEndTick
    {
        get => _timelineExtentEndTick;
        private set => Set(ref _timelineExtentEndTick, Math.Max(1, value));
    }
    public long? EditCursorTick
    {
        get => _editCursorTick;
        set => Set(ref _editCursorTick, value is null ? null : Math.Max(0, value.Value));
    }
    public long? PlaybackCursorTick
    {
        get => _playbackCursorTick;
        private set => Set(ref _playbackCursorTick, value);
    }

    public void SetTrackMonitoringStates(
        IEnumerable<MidoraId> mutedTrackIds,
        IEnumerable<MidoraId> soloTrackIds)
    {
        ArgumentNullException.ThrowIfNull(mutedTrackIds);
        ArgumentNullException.ThrowIfNull(soloTrackIds);
        _mutedTrackIds.Clear();
        _mutedTrackIds.UnionWith(mutedTrackIds);
        _soloTrackIds.Clear();
        _soloTrackIds.UnionWith(soloTrackIds);
    }

    public void ResetViewport()
    {
        StartTick = 0;
        TickSpan = 3072;
        FirstLane = Mode == TimelineWorkspaceMode.Segment ? 48 : 0;
        LaneHeight = Mode == TimelineWorkspaceMode.Arrangement ? 56 : 18;
    }

    public long ToProjectTick(MidoraProject project, long timelineTick)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (timelineTick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timelineTick));
        }
        if (Mode != TimelineWorkspaceMode.Segment)
        {
            return timelineTick;
        }
        (LogicalTrack Track, Segment Segment)? located = FindSegment(project, ObjectId);
        if (located is null)
        {
            throw new InvalidOperationException("The Segment no longer exists.");
        }
        Segment segment = located.Value.Segment;
        long localTick = Math.Clamp(
            timelineTick,
            segment.ContentOffsetTick,
            segment.ContentEndTick);
        return checked(segment.ProjectStartTick + (localTick - segment.ContentOffsetTick));
    }

    public TickRange ToProjectRange(MidoraProject project, long timelineStartTick, long timelineEndTick)
    {
        if (timelineEndTick <= timelineStartTick)
        {
            throw new ArgumentOutOfRangeException(nameof(timelineEndTick));
        }
        long startTick = ToProjectTick(project, timelineStartTick);
        long endTick = ToProjectTick(project, timelineEndTick);
        if (endTick <= startTick)
        {
            throw new InvalidOperationException(
                "The selected time range does not intersect the active Segment range.");
        }
        return new(startTick, endTick);
    }

    public void UpdatePlaybackCursor(MidoraProject project, long projectTick)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (Mode != TimelineWorkspaceMode.Segment)
        {
            PlaybackCursorTick = Math.Max(0, projectTick);
            return;
        }
        (LogicalTrack Track, Segment Segment)? located = FindSegment(project, ObjectId);
        if (located is null)
        {
            PlaybackCursorTick = null;
            return;
        }
        Segment segment = located.Value.Segment;
        long projectEndTick = checked(segment.ProjectStartTick + segment.LengthTicks);
        PlaybackCursorTick = projectTick < segment.ProjectStartTick || projectTick > projectEndTick
            ? null
            : checked(segment.ContentOffsetTick + (projectTick - segment.ProjectStartTick));
    }

    public void SetTimeRange(long startTick, long endTick)
    {
        if (startTick < 0 || endTick <= startTick)
        {
            throw new ArgumentOutOfRangeException(nameof(endTick));
        }
        TimeRangeStartTick = startTick;
        TimeRangeEndTick = endTick;
        Raise(nameof(HasTimeRange));
    }

    public void ClearTimeRange()
    {
        TimeRangeStartTick = null;
        TimeRangeEndTick = null;
        Raise(nameof(HasTimeRange));
    }

    public bool SetTimeRangeFromObjectSelection()
    {
        if (Snapshot is null || Selection.Ids.Count == 0) return false;
        TimelineRenderItem[] selected = Snapshot.Items
            .Where(item => Selection.Ids.Contains(item.Id)
                && (item.State & TimelineItemState.HitTestDisabled) == 0)
            .ToArray();
        if (selected.Length == 0) return false;
        SetTimeRange(selected.Min(item => item.StartTick), selected.Max(item => item.EndTick));
        return true;
    }

    public bool SetObjectSelectionFromTimeRange()
    {
        if (Snapshot is null || TimeRangeStartTick is not long start || TimeRangeEndTick is not long end)
        {
            return false;
        }
        MidoraId[] ids = Snapshot.Items
            .Where(item => item.StartTick < end && item.EndTick > start
                && (item.State & TimelineItemState.HitTestDisabled) == 0)
            .Select(item => item.Id)
            .Distinct()
            .ToArray();
        Selection.Clear();
        foreach (MidoraId id in ids) Selection.Add(id, makePrimary: false);
        return ids.Length != 0;
    }

    public override void Rebuild(MidoraProject project, long revision)
    {
        EditorSettings.ConfigureProject(project, StartTick);
        LaneEditorSettings.ConfigureProject(project, StartTick);
        if (!_viewportInitialized)
        {
            TickSpan = Math.Max(TickSpan, checked((long)project.TicksPerQuarterNote * 16));
            _viewportInitialized = true;
        }
        switch (Mode)
        {
            case TimelineWorkspaceMode.Arrangement:
                RebuildArrangement(project, revision);
                break;
            case TimelineWorkspaceMode.Segment:
                RebuildSegment(project, revision);
                break;
            case TimelineWorkspaceMode.Conductor:
                RebuildConductor(project, revision);
                break;
            default:
                throw new InvalidOperationException("Unknown timeline workspace mode.");
        }
        long contentEnd = Snapshot?.Items.Count > 0
            ? Snapshot.Items.Max(item => item.EndTick)
            : 0;
        long minimumExtent = StartTick <= long.MaxValue - TickSpan
            ? StartTick + TickSpan
            : long.MaxValue;
        long padding = Math.Max(1, checked((long)project.TicksPerQuarterNote * 4));
        long paddedContent = contentEnd <= long.MaxValue - padding ? contentEnd + padding : long.MaxValue;
        TimelineExtentEndTick = Math.Max(minimumExtent, paddedContent);
        Raise(nameof(GridStepTicks));
        Raise(nameof(OperationStepTicks));
        Raise(nameof(GridLabel));
    }

    private void RebuildArrangement(MidoraProject project, long revision)
    {
        PruneSelection(project.Tracks.SelectMany(track => track.Segments).Select(segment => segment.Id));
        RangeStartTick = null;
        RangeEndTick = null;
        List<TimelineRenderItem> items = [];
        Dictionary<MidoraId, TimelineSegmentPreview> previews = [];
        HashSet<MidoraId> liveSegmentIds = [];
        for (int trackIndex = 0; trackIndex < project.Tracks.Count; trackIndex++)
        {
            LogicalTrack track = project.Tracks[trackIndex];
            EventInstrument? boundInstrument = track.EventInstrumentId is MidoraId instrumentId
                ? project.EventInstruments.FirstOrDefault(value => value.Id == instrumentId)
                : null;
            MidoraColor? effectiveColor = track.ColorOverride ?? boundInstrument?.Color;
            uint accentColor = effectiveColor is MidoraColor color
                ? ToOpaqueArgb(color)
                : 0;
            foreach (Segment segment in track.Segments)
            {
                liveSegmentIds.Add(segment.Id);
                items.Add(Item(
                    segment.Id,
                    TimelineItemKind.Segment,
                    segment.ProjectStartTick,
                    checked(segment.ProjectStartTick + segment.LengthTicks),
                    trackIndex,
                    z: 0) with { AccentColor = accentColor });
                previews.Add(segment.Id, GetOrCreateSegmentPreview(segment));
            }
        }
        foreach (MidoraId staleId in _segmentPreviewCache.Keys.Where(id => !liveSegmentIds.Contains(id)).ToArray())
        {
            _segmentPreviewCache.Remove(staleId);
        }
        Context = $"{project.Tracks.Count} logical tracks · {items.Count(item => item.Kind == TimelineItemKind.Segment)} segments";
        Snapshot = new(
            revision,
            "arrangement",
            items,
            project.Tracks.Select(track => TrackDisplayName(project, track)).ToArray(),
            project.Tracks.Select(track =>
                (_mutedTrackIds.Contains(track.Id) ? TimelineLaneState.Muted : TimelineLaneState.None)
                | (_soloTrackIds.Contains(track.Id) ? TimelineLaneState.Solo : TimelineLaneState.None)).ToArray(),
            previews,
            project.Tracks.Select(track => BoundInstrumentDisplayName(project, track)).ToArray(),
            project.Tracks.Select(track =>
            {
                EventInstrument? instrument = track.EventInstrumentId is MidoraId instrumentId
                    ? project.EventInstruments.FirstOrDefault(value => value.Id == instrumentId)
                    : null;
                MidoraColor? color = track.ColorOverride ?? instrument?.Color;
                return color is MidoraColor value ? ToOpaqueArgb(value) : 0;
            }).ToArray());
        RulerSnapshot = BuildConductorOverview(project, revision);
    }

    private TimelineSegmentPreview GetOrCreateSegmentPreview(Segment segment)
    {
        SegmentPreviewNoteSource[] source = segment.Notes
            .Select(note => new SegmentPreviewNoteSource(note.StartTick, note.LengthTicks, note.Note))
            .ToArray();
        if (_segmentPreviewCache.TryGetValue(segment.Id, out SegmentPreviewCacheEntry? cached)
            && cached.ContentOffsetTick == segment.ContentOffsetTick
            && cached.LengthTicks == segment.LengthTicks
            && cached.Source.AsSpan().SequenceEqual(source))
        {
            return cached.Preview;
        }

        long visibleStart = segment.ContentOffsetTick;
        long visibleEnd = segment.ContentEndTick;
        List<TimelineSegmentPreviewNote> notes = new(source.Length);
        foreach (SegmentPreviewNoteSource note in source)
        {
            long noteEnd = checked(note.StartTick + note.LengthTicks);
            long clippedStart = Math.Max(visibleStart, note.StartTick);
            long clippedEnd = Math.Min(visibleEnd, noteEnd);
            if (clippedEnd <= clippedStart) continue;
            notes.Add(new(
                (clippedStart - visibleStart) / (double)segment.LengthTicks,
                (clippedEnd - visibleStart) / (double)segment.LengthTicks,
                Math.Clamp(note.Pitch, 0, 127)));
        }
        TimelineSegmentPreview preview = new(segment.Id, notes);
        _segmentPreviewCache[segment.Id] = new(
            segment.ContentOffsetTick,
            segment.LengthTicks,
            source,
            preview);
        return preview;
    }

    private readonly record struct SegmentPreviewNoteSource(long StartTick, long LengthTicks, int Pitch);

    private static uint ToOpaqueArgb(MidoraColor color) =>
        0xff000000u | ((uint)color.Red << 16) | ((uint)color.Green << 8) | color.Blue;

    private sealed record SegmentPreviewCacheEntry(
        long ContentOffsetTick,
        long LengthTicks,
        SegmentPreviewNoteSource[] Source,
        TimelineSegmentPreview Preview);

    private void RebuildSegment(MidoraProject project, long revision)
    {
        RulerSnapshot = null;
        (LogicalTrack Track, Segment Segment)? located = FindSegment(project, ObjectId);
        if (located is null)
        {
            Context = "The Segment no longer exists.";
            Snapshot = new(revision, $"segment:{ObjectId}", Array.Empty<TimelineRenderItem>());
            ParameterSnapshot = new(revision, $"segment-parameters:{ObjectId}", Array.Empty<TimelineRenderItem>());
            VelocitySnapshot = new(revision, $"segment-velocities:{ObjectId}", Array.Empty<TimelineRenderItem>());
            return;
        }
        LogicalTrack track = located.Value.Track;
        Segment segment = located.Value.Segment;
        PruneSelection(
            segment.Notes.Select(note => note.Id)
                .Concat(segment.ParameterLanes.Select(lane => lane.Id))
                .Concat(segment.ParameterLanes.SelectMany(lane => lane.Points).Select(point => point.Id)));
        RangeStartTick = segment.ContentOffsetTick;
        RangeEndTick = segment.ContentEndTick;
        Header = $"Segment: {TrackDisplayName(project, track)} @ {segment.ProjectStartTick}";
        Context = $"Segment local ticks · Project start {segment.ProjectStartTick} · active {segment.ContentOffsetTick}–{segment.ContentEndTick}";
        TimelineRenderItem[] items = segment.Notes
            .Select(note => Item(
                note.Id,
                TimelineItemKind.LogicalNote,
                note.StartTick,
                checked(note.StartTick + note.LengthTicks),
                127 - Math.Clamp(note.Note, 0, 127),
                z: 1,
                value: note.Velocity,
                extra: (note.StartTick < segment.ContentOffsetTick
                    || note.StartTick >= segment.ContentEndTick
                    ? TimelineItemState.OutsideActiveRange
                    : TimelineItemState.None)
                    | (note.Note is < 0 or > 127
                        ? TimelineItemState.Invalid
                        : TimelineItemState.None)))
            .ToArray();
        Snapshot = new(
            revision,
            $"segment:{segment.Id.Value}",
            items,
            Enumerable.Range(0, 128).Select(lane => MidiNoteName(127 - lane)).ToArray());
        VelocitySnapshot = new(
            revision,
            $"segment-velocities:{segment.Id.Value}",
            segment.Notes.Select(note => new TimelineRenderItem(
                note.Id,
                TimelineItemKind.Velocity,
                note.StartTick,
                checked(note.StartTick + 1),
                0,
                note.Velocity / 127d,
                note.Note,
                Selection.Ids.Contains(note.Id)
                    ? TimelineItemState.Selected
                      | (Selection.Primary == note.Id ? TimelineItemState.Primary : TimelineItemState.None)
                    : TimelineItemState.None)),
            ["Velocity"]);

        EventInstrument? instrument = track.EventInstrumentId is MidoraId instrumentId
            ? project.EventInstruments.FirstOrDefault(item => item.Id == instrumentId)
            : null;
        List<TimelineRenderItem> parameterItems = [];
        List<string> parameterLabels = [];
        MidoraId? previousParameterId = GetActiveParameterLaneOption()?.ParameterId;
        ParameterLaneOptions.Clear();
        if (instrument is not null)
        {
            foreach (LogicalParameterDefinition definition in instrument.LogicalParameters)
            {
                LogicalParameterLane? lane = segment.ParameterLanes.FirstOrDefault(
                    item => item.ParameterId == definition.Id);
                ParameterLaneOptions.Add(new(
                    definition.Id,
                    lane?.Id,
                    lane is null ? $"{definition.Name} · Empty" : definition.Name));
            }
        }
        foreach (LogicalParameterLane lane in segment.ParameterLanes.Where(lane =>
            instrument?.LogicalParameters.Any(definition => definition.Id == lane.ParameterId) != true))
        {
            ParameterLaneOptions.Add(new(
                lane.ParameterId,
                lane.Id,
                $"Broken parameter {lane.ParameterId.Value}",
                IsBroken: true));
        }
        MidoraId? selectedParameterId = Selection.Primary is MidoraId selected
            ? segment.ParameterLanes.FirstOrDefault(lane =>
                lane.Id == selected || lane.Points.Any(point => point.Id == selected))?.ParameterId
            : null;
        MidoraId? preferredParameterId = selectedParameterId ?? previousParameterId;
        int preferredIndex = preferredParameterId is MidoraId parameterId
            ? ParameterLaneOptions.ToList().FindIndex(option => option.ParameterId == parameterId)
            : -1;
        ActiveParameterLaneIndex = ParameterLaneOptions.Count == 0
            ? -1
            : preferredIndex >= 0
                ? preferredIndex
                : Math.Clamp(ActiveParameterLaneIndex, 0, ParameterLaneOptions.Count - 1);
        ActiveValueMinimum = 0;
        ActiveValueMaximum = 127;
        ActiveValueIntegral = true;
        ParameterLaneOption? activeOption = GetActiveParameterLaneOption();
        if (activeOption is not null)
        {
            LogicalParameterDefinition? definition = instrument?.LogicalParameters
                .FirstOrDefault(item => item.Id == activeOption.ParameterId);
            if (definition is not null)
            {
                double minimum = definition.DisplayMinimum;
                double maximum = definition.DisplayMaximum;
                if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum)
                {
                    minimum = definition.Minimum;
                    maximum = definition.Maximum;
                }
                ActiveValueMinimum = minimum;
                ActiveValueMaximum = maximum;
                ActiveValueIntegral = definition.Type is LogicalParameterType.Integer or LogicalParameterType.Enum;
            }
            parameterLabels.Add(activeOption.Label);
            LogicalParameterLane? lane = activeOption.LaneId is MidoraId laneId
                ? segment.ParameterLanes.FirstOrDefault(item => item.Id == laneId)
                : null;
            CurvePoint[] points = lane?.Points.OrderBy(item => item.Tick).ThenBy(item => item.Id).ToArray() ?? [];
            foreach (CurvePoint point in points)
            {
                double normalized = NormalizeParameterValue(definition, point.Value);
                TimelineItemState state = point.Tick < segment.ContentOffsetTick || point.Tick >= segment.ContentEndTick
                    ? TimelineItemState.OutsideActiveRange
                    : TimelineItemState.None;
                if (definition is null) state |= TimelineItemState.Broken;
                if (Selection.Ids.Contains(point.Id)) state |= TimelineItemState.Selected;
                if (Selection.Primary == point.Id) state |= TimelineItemState.Primary;
                parameterItems.Add(new(
                    point.Id,
                    TimelineItemKind.LogicalParameterPoint,
                    point.Tick,
                    checked(point.Tick + 1),
                    0,
                    normalized,
                    1,
                    state));
            }
        }
        ParameterSnapshot = new(
            revision,
            $"segment-parameters:{segment.Id.Value}",
            parameterItems,
            parameterLabels);
    }

    private void RebuildConductor(MidoraProject project, long revision)
    {
        PruneSelection(
            project.Conductor.Tempos.Select(item => item.Id)
                .Concat(project.Conductor.TimeSignatures.Select(item => item.Id))
                .Concat(project.Conductor.KeySignatures.Select(item => item.Id))
                .Concat(project.Conductor.Markers.Select(item => item.Id))
                .Concat(project.Conductor.EndMarker is ProjectEndMarker selectionEndMarker
                    ? [selectionEndMarker.Id]
                    : Array.Empty<MidoraId>()));
        RulerSnapshot = null;
        RangeStartTick = null;
        RangeEndTick = null;
        ConductorEvents.Clear();
        List<TimelineRenderItem> items = [];
        foreach (TempoChange item in project.Conductor.Tempos)
        {
            items.Add(Item(item.Id, TimelineItemKind.ConductorEvent, item.Tick, checked(item.Tick + 1), 0));
            ConductorEvents.Add(new(item.Id, item.Tick, "Tempo", $"{item.BeatsPerMinute:0.######} BPM"));
        }
        foreach (TimeSignatureChange item in project.Conductor.TimeSignatures)
        {
            items.Add(Item(item.Id, TimelineItemKind.ConductorEvent, item.Tick, checked(item.Tick + 1), 1));
            ConductorEvents.Add(new(item.Id, item.Tick, "Time Signature", $"{item.Numerator}/{item.Denominator}"));
        }
        foreach (KeySignatureChange item in project.Conductor.KeySignatures)
        {
            items.Add(Item(item.Id, TimelineItemKind.ConductorEvent, item.Tick, checked(item.Tick + 1), 2));
            ConductorEvents.Add(new(item.Id, item.Tick, "Key Signature", $"{item.SharpsFlats:+0;-0;0} · {(item.IsMinor ? "Minor" : "Major")}"));
        }
        foreach (ProjectMarker item in project.Conductor.Markers)
        {
            items.Add(Item(item.Id, TimelineItemKind.Marker, item.Tick, checked(item.Tick + 1), 3));
            ConductorEvents.Add(new(item.Id, item.Tick, "Marker", string.IsNullOrWhiteSpace(item.Name) ? "(unnamed)" : item.Name));
        }
        if (project.Conductor.EndMarker is ProjectEndMarker endMarker)
        {
            items.Add(Item(
                endMarker.Id,
                TimelineItemKind.ProjectEndMarker,
                endMarker.Tick,
                checked(endMarker.Tick + 1),
                4,
                z: 2));
            ConductorEvents.Add(new(endMarker.Id, endMarker.Tick, "Project End", "Hard end boundary"));
        }
        ConductorEventRow[] orderedRows = ConductorEvents
            .OrderBy(item => item.Tick).ThenBy(item => item.Type, StringComparer.Ordinal).ThenBy(item => item.Id)
            .ToArray();
        ConductorEvents.Clear();
        foreach (ConductorEventRow row in orderedRows) ConductorEvents.Add(row);
        Context = "Tempo · Time Signature · Key Signature · Markers · Project End Marker";
        Snapshot = new(
            revision,
            "conductor",
            items,
            ["Tempo", "Time Signature", "Key Signature", "Marker", "Project End"]);
    }

    private static TimelineRenderSnapshot BuildConductorOverview(MidoraProject project, long revision)
    {
        List<(MidoraId Id, TimelineItemKind Kind, long Tick, string Label, int Priority)> events = [];
        foreach (TempoChange item in project.Conductor.Tempos)
        {
            events.Add((item.Id, TimelineItemKind.ConductorEvent, item.Tick,
                $"{item.BeatsPerMinute:0.##} BPM", 0));
        }
        foreach (TimeSignatureChange item in project.Conductor.TimeSignatures)
        {
            events.Add((item.Id, TimelineItemKind.ConductorEvent, item.Tick,
                $"{item.Numerator}/{item.Denominator}", 1));
        }
        foreach (KeySignatureChange item in project.Conductor.KeySignatures)
        {
            events.Add((item.Id, TimelineItemKind.ConductorEvent, item.Tick, "Key", 2));
        }
        foreach (ProjectMarker item in project.Conductor.Markers)
        {
            events.Add((item.Id, TimelineItemKind.Marker, item.Tick,
                string.IsNullOrWhiteSpace(item.Name) ? "Marker" : item.Name, 3));
        }
        if (project.Conductor.EndMarker is ProjectEndMarker end)
        {
            events.Add((end.Id, TimelineItemKind.ProjectEndMarker, end.Tick, "END", 4));
        }
        TimelineRenderItem[] items = events
            .GroupBy(item => item.Tick)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                (MidoraId Id, TimelineItemKind Kind, long Tick, string Label, int Priority) primary = group
                    .OrderByDescending(item => item.Priority)
                    .ThenBy(item => item.Id.Value)
                    .First();
                string label = string.Join(
                    " · ",
                    group.OrderBy(item => item.Priority)
                        .ThenBy(item => item.Id.Value)
                        .Select(item => item.Label)
                        .Distinct(StringComparer.Ordinal));
                return new TimelineRenderItem(
                    primary.Id,
                    primary.Kind,
                    primary.Tick,
                    checked(primary.Tick + 1),
                    0,
                    0,
                    primary.Priority,
                    TimelineItemState.HitTestDisabled)
                {
                    Label = label
                };
            })
            .ToArray();
        return new(revision, "arrangement-conductor-overview", items);
    }

    private TimelineRenderItem Item(
        MidoraId id,
        TimelineItemKind kind,
        long start,
        long end,
        int lane,
        int z = 0,
        double value = 0,
        TimelineItemState extra = TimelineItemState.None)
    {
        TimelineItemState state = extra;
        if (Selection.Ids.Contains(id))
        {
            state |= TimelineItemState.Selected;
        }
        if (Selection.Primary == id)
        {
            state |= TimelineItemState.Primary;
        }
        return new(id, kind, start, end, lane, value, z, state);
    }

    private void PruneSelection(IEnumerable<MidoraId> validIds)
    {
        HashSet<MidoraId> valid = validIds.ToHashSet();
        foreach (MidoraId selectedId in Selection.Ids.Where(id => !valid.Contains(id)).ToArray())
        {
            Selection.Remove(selectedId);
        }
    }

    internal static (LogicalTrack Track, Segment Segment)? FindSegment(
        MidoraProject project,
        MidoraId? segmentId)
    {
        if (segmentId is null)
        {
            return null;
        }
        foreach (LogicalTrack track in project.Tracks)
        {
            Segment? segment = track.Segments.FirstOrDefault(candidate => candidate.Id == segmentId);
            if (segment is not null)
            {
                return (track, segment);
            }
        }
        return null;
    }

    internal static string TrackDisplayName(MidoraProject project, LogicalTrack track)
    {
        if (!string.IsNullOrWhiteSpace(track.Name))
        {
            return track.Name;
        }
        int index = project.Tracks.IndexOf(track) + 1;
        return $"Logical Track {index}";
    }

    internal static string BoundInstrumentDisplayName(MidoraProject project, LogicalTrack track)
    {
        if (track.EventInstrumentId is not MidoraId instrumentId)
        {
            return "Unbound";
        }
        EventInstrument? instrument = project.EventInstruments.FirstOrDefault(item => item.Id == instrumentId);
        if (instrument is not null)
        {
            return string.IsNullOrWhiteSpace(instrument.Name) ? "Unnamed Event Instrument" : instrument.Name;
        }
        return string.IsNullOrWhiteSpace(track.LastBoundEventInstrumentName)
            ? "Missing Event Instrument"
            : $"Missing: {track.LastBoundEventInstrumentName}";
    }

    internal static string MidiNoteName(int note)
    {
        note = Math.Clamp(note, 0, 127);
        string[] names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
        return $"{names[note % 12]}{note / 12 - 1}";
    }

    internal static double NormalizeParameterValue(LogicalParameterDefinition? definition, double value)
    {
        if (definition is null) return 0.5;
        double minimum = definition.DisplayMinimum;
        double maximum = definition.DisplayMaximum;
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum)
        {
            minimum = definition.Minimum;
            maximum = definition.Maximum;
        }
        if (maximum <= minimum) return 0.5;
        return Math.Clamp((value - minimum) / (maximum - minimum), 0, 1);
    }

    internal static double DenormalizeParameterValue(LogicalParameterDefinition definition, double normalized)
    {
        double minimum = definition.DisplayMinimum;
        double maximum = definition.DisplayMaximum;
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum)
        {
            minimum = definition.Minimum;
            maximum = definition.Maximum;
        }
        double value = minimum + Math.Clamp(normalized, 0, 1) * (maximum - minimum);
        if (definition.Type == LogicalParameterType.Double)
        {
            return value;
        }

        double integral = Math.Round(value, MidpointRounding.AwayFromZero);
        if (definition.Type != LogicalParameterType.Enum
            || !definition.UsesExplicitEnumValues
            || definition.EnumItems.Count == 0)
        {
            return integral;
        }
        return definition.EnumItems
            .OrderBy(item => Math.Abs(item.Value - integral))
            .ThenBy(item => item.Value)
            .First()
            .Value;
    }
}

public sealed record InstrumentListItem(
    MidoraId Id,
    string Name,
    string Folder,
    int UsageCount,
    string Status);

public enum InstrumentLibrarySortMode
{
    Manual,
    Name,
    Folder,
    Usage
}

public sealed record SubVoiceListItem(MidoraId Id, string Name, int EventCount);
public sealed record LogicalParameterListItem(MidoraId Id, string Name, string Type, string Range);
public sealed record MappingFunctionListItem(MidoraId Id, string Name, int AbiVersion);
public sealed record ParameterMappingListItem(MidoraId Id, string Source, string Target, int StepCount);
public sealed record EnvelopeListItem(MidoraId Id, string Name, string Summary);
public sealed record MappingChainListItem(
    MidoraId Id,
    string Owner,
    int StepCount,
    bool CanDelete);
public sealed record MappingStepListItem(MidoraId Id, MidoraId ChainId, string Owner, int Index, string Summary);

public sealed record InstrumentRenderLane(
    MidoraId SubVoiceId,
    string Label,
    MidiValueTarget? Target = null,
    MidoraId? ValueCurveId = null,
    MidoraId? EventMappingChainId = null,
    TemplateEventMappingTarget? EventMappingTarget = null);

public sealed record InitialStateListItem(string Target, string Value, string Scope);

public enum InstrumentPreviewMode
{
    FullInstrument,
    SelectedSubVoice
}

public sealed class LibraryWorkspaceViewModel()
    : WorkspaceViewModel(
        WorkspaceKey.ForType(WorkspaceKind.EventInstrumentLibrary),
        "Event Instrument Library")
{
    private readonly List<InstrumentListItem> _allInstruments = [];
    private string _searchText = string.Empty;
    private InstrumentLibrarySortMode _sortMode;
    private InstrumentListItem? _selectedInstrument;

    public ObservableCollection<InstrumentListItem> Instruments { get; } = [];
    public Array SortModes { get; } = Enum.GetValues<InstrumentLibrarySortMode>();
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? string.Empty)) ApplyView();
        }
    }
    public InstrumentLibrarySortMode SortMode
    {
        get => _sortMode;
        set
        {
            if (Set(ref _sortMode, value)) ApplyView();
        }
    }
    public InstrumentListItem? SelectedInstrument
    {
        get => _selectedInstrument;
        set => Set(ref _selectedInstrument, value);
    }

    public override void Rebuild(MidoraProject project, long revision)
    {
        MidoraId? selectedId = SelectedInstrument?.Id;
        _allInstruments.Clear();
        foreach (EventInstrument instrument in project.EventInstruments)
        {
            string folder = instrument.LibraryFolderId is MidoraId folderId
                ? project.EventInstrumentFolders.FirstOrDefault(folder => folder.Id == folderId)?.Name
                    ?? "Broken Folder"
                : "Unfiled";
            int usage = project.Tracks.Count(track => track.EventInstrumentId == instrument.Id);
            _allInstruments.Add(new(instrument.Id, instrument.Name, folder, usage, "Current"));
        }
        ApplyView();
        SelectedInstrument = selectedId is MidoraId id
            ? Instruments.FirstOrDefault(item => item.Id == id)
            : null;
    }

    private void ApplyView()
    {
        IEnumerable<InstrumentListItem> query = _allInstruments;
        string search = SearchText.Trim();
        if (search.Length != 0)
        {
            query = query.Where(item => item.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || item.Folder.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || item.Status.Contains(search, StringComparison.CurrentCultureIgnoreCase));
        }
        query = SortMode switch
        {
            InstrumentLibrarySortMode.Name => query.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            InstrumentLibrarySortMode.Folder => query.OrderBy(item => item.Folder, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            InstrumentLibrarySortMode.Usage => query.OrderByDescending(item => item.UsageCount)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => query
        };
        Instruments.Clear();
        foreach (InstrumentListItem item in query) Instruments.Add(item);
    }
}

public sealed class InstrumentWorkspaceViewModel(
    MidoraId instrumentId,
    string header)
    : WorkspaceViewModel(
        WorkspaceKey.ForObject(WorkspaceKind.EventInstrumentEditor, instrumentId),
        header)
{
    private readonly Dictionary<MidoraId, SubVoiceEditorSettings> _subVoiceEditorSettings = [];
    private string _summary = string.Empty;
    private TimelineRenderSnapshot? _subVoiceSnapshot;
    private TimelineRenderSnapshot? _subVoiceNoteSnapshot;
    private TimelineRenderSnapshot? _subVoiceEventSnapshot;
    private TimelineRenderSnapshot? _subVoiceVelocitySnapshot;
    private int _activeRenderLaneIndex;
    private MidoraId? _activeSubVoiceId;
    private string _activeSubVoiceName = "No SubVoice";
    private string _activeSubVoiceContext = "Create or select a SubVoice to edit its timeline.";
    private bool _activeSubVoiceFollowsInstanceVelocity;
    private bool _requiresChannelIsolation;
    private ShortNoteLifecycle _shortLifecycle;
    private LongNoteLifecycle _longLifecycle;
    private OverlapPolicy _overlapPolicy;
    private OverlapScope _overlapScope;
    private string _loopStartText = string.Empty;
    private string _loopEndText = string.Empty;
    private string _instrumentNameText = string.Empty;
    private string _instrumentDescriptionText = string.Empty;
    private string _instrumentColorText = "#6B7280";
    private string _instrumentRootNoteText = "60";
    private string _instrumentTemplateLengthText = "1";
    private string _instrumentStableIdText = string.Empty;
    private long? _loopStartTick;
    private long? _loopEndTick;
    private long _templateLengthTicks = 1;
    private int _activeRootPitch = 60;
    private long _scenarioGateLengthTicks = 192;
    private int _scenarioPitch = 60;
    private int _scenarioVelocity = 100;
    private bool _scenarioHasHardBoundary;
    private long _scenarioHardBoundaryTick = 384;
    private TimelineToolMode _toolMode = TimelineToolMode.Select;
    private double _activeValueMinimum;
    private double _activeValueMaximum = 127;
    private bool _activeValueIntegral = true;
    private bool _isPreviewExpanded = true;
    private bool _isPreviewMuted;
    private bool _isPreviewSoloSelected;
    private InstrumentPreviewMode _previewMode;
    private long? _editCursorTick;
    private MidoraId? _selectedMappingStepId;
    private int _activeSectionIndex;
    private long _timelineStartTick;
    private long _timelineTickSpan = 3072;
    private int _timelineFirstLane = 59;
    private double _timelineLaneHeight = 18;
    private GridLength _noteEditorRowHeight = new(5, GridUnitType.Star);
    private GridLength _eventEditorRowHeight = new(3, GridUnitType.Star);
    private int _activeLowerEditorIndex;
    private double _velocityValueScrollOffset;
    private double _eventValueScrollOffset;

    private TimelineEditorSettings _editorSettings = new();
    private TimelineEditorSettings _eventLaneEditorSettings = new();

    public TimelineEditorSettings EditorSettings
    {
        get => _editorSettings;
        private set => Set(ref _editorSettings, value);
    }

    public TimelineEditorSettings EventLaneEditorSettings
    {
        get => _eventLaneEditorSettings;
        private set => Set(ref _eventLaneEditorSettings, value);
    }

    public int ActiveSectionIndex
    {
        get => _activeSectionIndex;
        set => Set(ref _activeSectionIndex, Math.Clamp(value, 0, 2));
    }

    public long TimelineStartTick
    {
        get => _timelineStartTick;
        set => Set(ref _timelineStartTick, Math.Max(0, value));
    }

    public long TimelineTickSpan
    {
        get => _timelineTickSpan;
        set => Set(ref _timelineTickSpan, Math.Max(1, value));
    }

    public int TimelineFirstLane
    {
        get => _timelineFirstLane;
        set => Set(ref _timelineFirstLane, Math.Clamp(value, 0, 127));
    }

    public double TimelineLaneHeight
    {
        get => _timelineLaneHeight;
        set => Set(ref _timelineLaneHeight, Math.Clamp(value, 4, 128));
    }

    public GridLength NoteEditorRowHeight
    {
        get => _noteEditorRowHeight;
        set
        {
            if (value.Value <= 0 || !double.IsFinite(value.Value)) return;
            Set(ref _noteEditorRowHeight, value);
        }
    }

    public GridLength EventEditorRowHeight
    {
        get => _eventEditorRowHeight;
        set
        {
            if (value.Value <= 0 || !double.IsFinite(value.Value)) return;
            Set(ref _eventEditorRowHeight, value);
        }
    }

    public int ActiveLowerEditorIndex
    {
        get => _activeLowerEditorIndex;
        set => Set(ref _activeLowerEditorIndex, Math.Clamp(value, 0, 1));
    }

    public double VelocityValueScrollOffset
    {
        get => _velocityValueScrollOffset;
        set => Set(ref _velocityValueScrollOffset, Math.Max(0, value));
    }

    public double EventValueScrollOffset
    {
        get => _eventValueScrollOffset;
        set => Set(ref _eventValueScrollOffset, Math.Max(0, value));
    }

    public long? EditCursorTick
    {
        get => _editCursorTick;
        set => Set(ref _editCursorTick, value is null ? null : Math.Max(0, value.Value));
    }

    public MidoraId? SelectedMappingStepId
    {
        get => _selectedMappingStepId;
        set => Set(ref _selectedMappingStepId, value);
    }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }
    public TimelineRenderSnapshot? SubVoiceSnapshot
    {
        get => _subVoiceSnapshot;
        private set => Set(ref _subVoiceSnapshot, value);
    }
    public TimelineRenderSnapshot? SubVoiceNoteSnapshot
    {
        get => _subVoiceNoteSnapshot;
        private set => Set(ref _subVoiceNoteSnapshot, value);
    }
    public TimelineRenderSnapshot? SubVoiceEventSnapshot
    {
        get => _subVoiceEventSnapshot;
        private set => Set(ref _subVoiceEventSnapshot, value);
    }
    public TimelineRenderSnapshot? SubVoiceVelocitySnapshot
    {
        get => _subVoiceVelocitySnapshot;
        private set => Set(ref _subVoiceVelocitySnapshot, value);
    }
    public int ActiveRenderLaneIndex
    {
        get => _activeRenderLaneIndex;
        set => Set(ref _activeRenderLaneIndex, Math.Max(-1, value));
    }
    public double ActiveValueMinimum
    {
        get => _activeValueMinimum;
        private set => Set(ref _activeValueMinimum, value);
    }
    public double ActiveValueMaximum
    {
        get => _activeValueMaximum;
        private set => Set(ref _activeValueMaximum, value);
    }
    public bool ActiveValueIntegral
    {
        get => _activeValueIntegral;
        private set => Set(ref _activeValueIntegral, value);
    }
    public MidoraId? ActiveSubVoiceId
    {
        get => _activeSubVoiceId;
        private set => Set(ref _activeSubVoiceId, value);
    }
    public string ActiveSubVoiceName
    {
        get => _activeSubVoiceName;
        private set => Set(ref _activeSubVoiceName, value);
    }
    public string ActiveSubVoiceContext
    {
        get => _activeSubVoiceContext;
        private set => Set(ref _activeSubVoiceContext, value);
    }
    public bool ActiveSubVoiceFollowsInstanceVelocity
    {
        get => _activeSubVoiceFollowsInstanceVelocity;
        private set => Set(ref _activeSubVoiceFollowsInstanceVelocity, value);
    }
    public Array ShortLifecycleValues { get; } = Enum.GetValues<ShortNoteLifecycle>();
    public Array LongLifecycleValues { get; } = Enum.GetValues<LongNoteLifecycle>();
    public Array OverlapPolicyValues { get; } = Enum.GetValues<OverlapPolicy>();
    public Array OverlapScopeValues { get; } = Enum.GetValues<OverlapScope>();
    public bool RequiresChannelIsolation { get => _requiresChannelIsolation; private set => Set(ref _requiresChannelIsolation, value); }
    public ShortNoteLifecycle ShortLifecycle { get => _shortLifecycle; private set => Set(ref _shortLifecycle, value); }
    public LongNoteLifecycle LongLifecycle { get => _longLifecycle; private set => Set(ref _longLifecycle, value); }
    public OverlapPolicy OverlapPolicy { get => _overlapPolicy; private set => Set(ref _overlapPolicy, value); }
    public OverlapScope OverlapScope { get => _overlapScope; private set => Set(ref _overlapScope, value); }
    public string LoopStartText { get => _loopStartText; set => Set(ref _loopStartText, value ?? string.Empty); }
    public string LoopEndText { get => _loopEndText; set => Set(ref _loopEndText, value ?? string.Empty); }
    public string InstrumentNameText { get => _instrumentNameText; set => Set(ref _instrumentNameText, value ?? string.Empty); }
    public string InstrumentDescriptionText { get => _instrumentDescriptionText; set => Set(ref _instrumentDescriptionText, value ?? string.Empty); }
    public string InstrumentColorText { get => _instrumentColorText; set => Set(ref _instrumentColorText, value ?? string.Empty); }
    public string InstrumentRootNoteText { get => _instrumentRootNoteText; set => Set(ref _instrumentRootNoteText, value ?? string.Empty); }
    public string InstrumentTemplateLengthText { get => _instrumentTemplateLengthText; set => Set(ref _instrumentTemplateLengthText, value ?? string.Empty); }
    public string InstrumentStableIdText { get => _instrumentStableIdText; private set => Set(ref _instrumentStableIdText, value); }
    public long? LoopStartTick { get => _loopStartTick; private set => Set(ref _loopStartTick, value); }
    public long? LoopEndTick { get => _loopEndTick; private set => Set(ref _loopEndTick, value); }
    public long TemplateLengthTicks { get => _templateLengthTicks; private set => Set(ref _templateLengthTicks, Math.Max(1, value)); }
    public int ActiveRootPitch { get => _activeRootPitch; private set => Set(ref _activeRootPitch, Math.Clamp(value, 0, 127)); }
    public long ScenarioGateLengthTicks { get => _scenarioGateLengthTicks; set => Set(ref _scenarioGateLengthTicks, Math.Max(1, value)); }
    public int ScenarioPitch { get => _scenarioPitch; set => Set(ref _scenarioPitch, Math.Clamp(value, 0, 127)); }
    public int ScenarioVelocity { get => _scenarioVelocity; set => Set(ref _scenarioVelocity, Math.Clamp(value, 1, 127)); }
    public bool ScenarioHasHardBoundary { get => _scenarioHasHardBoundary; set => Set(ref _scenarioHasHardBoundary, value); }
    public long ScenarioHardBoundaryTick { get => _scenarioHardBoundaryTick; set => Set(ref _scenarioHardBoundaryTick, Math.Max(1, value)); }
    public TimelineToolMode ToolMode
    {
        get => _toolMode;
        set
        {
            if (!Set(ref _toolMode, value)) return;
            Raise(nameof(IsSelectTool));
            Raise(nameof(IsDrawTool));
            Raise(nameof(IsEraseTool));
        }
    }
    public bool IsSelectTool => ToolMode == TimelineToolMode.Select;
    public bool IsDrawTool => ToolMode == TimelineToolMode.Draw;
    public bool IsEraseTool => ToolMode == TimelineToolMode.Erase;
    public bool IsPreviewExpanded { get => _isPreviewExpanded; set => Set(ref _isPreviewExpanded, value); }
    public bool IsPreviewMuted { get => _isPreviewMuted; set => Set(ref _isPreviewMuted, value); }
    public bool IsPreviewSoloSelected { get => _isPreviewSoloSelected; set => Set(ref _isPreviewSoloSelected, value); }
    public InstrumentPreviewMode PreviewMode
    {
        get => _previewMode;
        set
        {
            if (Set(ref _previewMode, value)) Raise(nameof(PreviewModeIndex));
        }
    }
    public int PreviewModeIndex
    {
        get => (int)PreviewMode;
        set => PreviewMode = value == (int)InstrumentPreviewMode.SelectedSubVoice
            ? InstrumentPreviewMode.SelectedSubVoice
            : InstrumentPreviewMode.FullInstrument;
    }
    public IReadOnlyList<string> PreviewModeLabels { get; } = ["Full Instrument", "Selected SubVoice"];
    public ObservableCollection<SubVoiceListItem> SubVoices { get; } = [];
    public ObservableCollection<LogicalParameterListItem> Parameters { get; } = [];
    public ObservableCollection<MappingFunctionListItem> MappingFunctions { get; } = [];
    public ObservableCollection<ParameterMappingListItem> ParameterMappings { get; } = [];
    public ObservableCollection<EnvelopeListItem> Envelopes { get; } = [];
    public ObservableCollection<MappingChainListItem> MappingChains { get; } = [];
    public ObservableCollection<MappingStepListItem> MappingSteps { get; } = [];
    public ObservableCollection<InitialStateListItem> InitialStateEntries { get; } = [];
    public ObservableCollection<InspectorField> InstrumentInitialStateFields { get; } = [];
    public ObservableCollection<InstrumentRenderLane> RenderLanes { get; } = [];

    public override void Rebuild(MidoraProject project, long revision)
    {
        EventInstrument? instrument = project.EventInstruments.FirstOrDefault(item => item.Id == ObjectId);
        SubVoices.Clear();
        Parameters.Clear();
        MappingFunctions.Clear();
        ParameterMappings.Clear();
        Envelopes.Clear();
        MappingChains.Clear();
        MappingSteps.Clear();
        InitialStateEntries.Clear();
        InstrumentInitialStateFields.Clear();
        MidiValueTarget? previousTarget = GetRenderLane(ActiveRenderLaneIndex)?.Target;
        RenderLanes.Clear();
        if (instrument is null)
        {
            SelectedMappingStepId = null;
            Summary = "The Event Instrument no longer exists.";
            SubVoiceSnapshot = new(revision, $"instrument:{ObjectId}", Array.Empty<TimelineRenderItem>());
            SubVoiceNoteSnapshot = new(revision, $"instrument-notes:{ObjectId}", Array.Empty<TimelineRenderItem>());
            SubVoiceEventSnapshot = new(revision, $"instrument-events:{ObjectId}", Array.Empty<TimelineRenderItem>());
            SubVoiceVelocitySnapshot = new(revision, $"instrument-velocities:{ObjectId}", Array.Empty<TimelineRenderItem>());
            ActiveSubVoiceId = null;
            ActiveSubVoiceName = "Missing SubVoice";
            ActiveSubVoiceContext = "The Event Instrument no longer exists.";
            ActiveSubVoiceFollowsInstanceVelocity = false;
            return;
        }

        Header = instrument.Name;
        InstrumentNameText = instrument.Name;
        InstrumentDescriptionText = instrument.Description ?? string.Empty;
        InstrumentColorText = $"#{instrument.Color.Red:X2}{instrument.Color.Green:X2}{instrument.Color.Blue:X2}";
        InstrumentRootNoteText = instrument.RootNote.ToString(System.Globalization.CultureInfo.InvariantCulture);
        InstrumentTemplateLengthText = instrument.TemplateLengthTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        InstrumentStableIdText = instrument.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        AddInstrumentInitialStateFields(instrument.InitialState);
        Summary = $"Root {MidiNoteName(instrument.RootNote)} · Template {instrument.TemplateLengthTicks} ticks · {instrument.SubVoices.Count} SubVoices";
        RequiresChannelIsolation = instrument.RequiresChannelIsolation;
        ShortLifecycle = instrument.ShortLifecycle;
        LongLifecycle = instrument.LongLifecycle;
        OverlapPolicy = instrument.OverlapPolicy;
        OverlapScope = instrument.OverlapScope;
        LoopStartText = instrument.LoopStartTick?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        LoopEndText = instrument.LoopEndTick?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        LoopStartTick = instrument.LoopStartTick;
        LoopEndTick = instrument.LoopEndTick;
        TemplateLengthTicks = instrument.TemplateLengthTicks;
        for (int index = 0; index < instrument.SubVoices.Count; index++)
        {
            SubVoice voice = instrument.SubVoices[index];
            SubVoices.Add(new(
                voice.Id,
                string.IsNullOrWhiteSpace(voice.Name) ? $"SubVoice {index + 1}" : voice.Name,
                voice.Events.Count));
        }
        foreach (LogicalParameterDefinition parameter in instrument.LogicalParameters)
        {
            Parameters.Add(new(
                parameter.Id,
                parameter.Name,
                parameter.Type.ToString(),
                $"{parameter.Minimum}–{parameter.Maximum}"));
        }
        foreach (CSharpMappingFunction function in instrument.MappingFunctions)
        {
            MappingFunctions.Add(new(function.Id, function.Name, function.AbiVersion));
        }
        foreach (LogicalParameterMapping mapping in instrument.ParameterMappings)
        {
            string source = instrument.LogicalParameters.FirstOrDefault(item => item.Id == mapping.ParameterId)?.Name
                ?? $"Broken {mapping.ParameterId}";
            SubVoice? voice = instrument.SubVoices.FirstOrDefault(item => item.Id == mapping.SubVoiceId);
            string targetVoice = voice is null
                ? $"Broken {mapping.SubVoiceId}"
                : string.IsNullOrWhiteSpace(voice.Name) ? $"SubVoice {instrument.SubVoices.IndexOf(voice) + 1}" : voice.Name;
            ParameterMappings.Add(new(
                mapping.Id,
                source,
                $"{targetVoice} · {FormatTarget(mapping.Target)}",
                mapping.Steps.Count));
            AddMappingChain(
                mapping.Steps,
                $"Parameter · {source} → {targetVoice}",
                canDelete: true);
        }
        foreach (SubVoice voice in instrument.SubVoices)
        {
            string voiceName = string.IsNullOrWhiteSpace(voice.Name)
                ? $"SubVoice {instrument.SubVoices.IndexOf(voice) + 1}"
                : voice.Name;
            foreach (SubVoiceEventMapping mapping in voice.EventMappings
                .OrderBy(value => value.Target.EventKind)
                .ThenBy(value => value.Target.EventNumber)
                .ThenBy(value => value.Target.Parameter))
            {
                AddMappingChain(
                    mapping.Steps,
                    $"{voiceName} · {FormatEventMappingTarget(mapping.Target)}",
                    canDelete: mapping.Target.EventKind != TemplateEventKind.Note);
            }
        }

        if (SelectedMappingStepId is MidoraId selectedStepId
            && !MappingSteps.Any(value => value.Id == selectedStepId))
        {
            SelectedMappingStepId = null;
        }
        foreach (InstrumentEnvelope envelope in instrument.Envelopes)
        {
            Envelopes.Add(new(
                envelope.Id,
                string.IsNullOrWhiteSpace(envelope.Name) ? "Envelope Preset" : envelope.Name,
                $"D {envelope.DelayTicks} · A {envelope.AttackTicks} · H {envelope.HoldTicks} · D {envelope.DecayTicks} · R {envelope.ReleaseTicks}"));
        }

        SubVoice? activeVoice = ResolveActiveSubVoice(instrument, Selection.Primary, ActiveSubVoiceId);
        HashSet<MidoraId> liveSubVoiceIds = instrument.SubVoices
            .Select(value => value.Id)
            .ToHashSet();
        foreach (MidoraId staleId in _subVoiceEditorSettings.Keys
            .Where(value => !liveSubVoiceIds.Contains(value))
            .ToArray())
        {
            _subVoiceEditorSettings.Remove(staleId);
        }
        if (activeVoice is not null)
        {
            if (!_subVoiceEditorSettings.TryGetValue(
                    activeVoice.Id,
                    out SubVoiceEditorSettings? settings))
            {
                TimelineEditorSettings piano = new();
                TimelineEditorSettings eventLane = new();
                piano.Reset(arrangement: false, project.TicksPerQuarterNote);
                eventLane.Reset(arrangement: false, project.TicksPerQuarterNote);
                settings = new(piano, eventLane);
                _subVoiceEditorSettings.Add(activeVoice.Id, settings);
            }
            settings.Piano.ConfigureProject(project, referenceTick: 0);
            settings.EventLane.ConfigureProject(project, referenceTick: 0);
            EditorSettings = settings.Piano;
            EventLaneEditorSettings = settings.EventLane;
        }
        else
        {
            EditorSettings.ConfigureProject(project, referenceTick: 0);
            EventLaneEditorSettings.ConfigureProject(project, referenceTick: 0);
        }
        ActiveSubVoiceId = activeVoice?.Id;
        ActiveRootPitch = activeVoice?.RootNoteOverride ?? instrument.RootNote;
        ActiveSubVoiceName = activeVoice is null
            ? "No SubVoice"
            : string.IsNullOrWhiteSpace(activeVoice.Name)
                ? $"SubVoice {instrument.SubVoices.IndexOf(activeVoice) + 1}"
                : activeVoice.Name;
        ActiveSubVoiceContext = activeVoice is null
            ? "Create or select a SubVoice to edit its timeline."
            : $"Root Note {(activeVoice.RootNoteOverride.HasValue ? "override" : "inherited")} · "
              + $"effective {MidiNoteName(activeVoice.RootNoteOverride ?? instrument.RootNote)} · "
              + $"Template {instrument.TemplateLengthTicks} ticks";
        ActiveSubVoiceFollowsInstanceVelocity = activeVoice is not null
            && SubVoiceMappingConventions.FollowsInstanceVelocity(activeVoice);

        List<TimelineRenderItem> notes = [];
        List<TimelineRenderItem> events = [];
        List<InstrumentRenderLane> lanes = [];
        if (activeVoice is not null)
        {
            foreach (TemplateEvent item in activeVoice.Events.Where(item => item.Kind == TemplateEventKind.Note))
            {
                notes.Add(new(
                    item.Id,
                    TimelineItemKind.TemplateNote,
                    item.Tick,
                    checked(item.Tick + item.LengthTicks),
                    127 - Math.Clamp(item.Number, 0, 127),
                    item.Value / 127d,
                    1,
                    SelectionState(item.Id)
                    | (item.Number is < 0 or > 127
                        ? TimelineItemState.Invalid
                        : TimelineItemState.None)));
            }

            Dictionary<MidiValueTarget, SubVoiceEventMapping?> eventLaneMappings = [];
            foreach (SubVoiceEventMapping mapping in activeVoice.EventMappings)
            {
                if (TemplateEventMidiTargets.TryFromMappingTarget(
                        mapping.Target,
                        out MidiValueTarget laneMidiTarget))
                {
                    eventLaneMappings[laneMidiTarget] = mapping;
                }
            }
            foreach (MidiValueTarget eventTarget in activeVoice.Events
                .Where(static item => item.Kind != TemplateEventKind.Note)
                .SelectMany(TemplateEventMidiTargets.Enumerate))
            {
                eventLaneMappings.TryAdd(eventTarget, null);
            }
            foreach ((MidiValueTarget laneMidiTarget, SubVoiceEventMapping? mapping) in eventLaneMappings
                .OrderBy(value => value.Key.Kind)
                .ThenBy(value => value.Key.Number))
            {
                int eventLane = lanes.Count;
                lanes.Add(new(
                    activeVoice.Id,
                    TemplateEventMidiTargets.Format(laneMidiTarget),
                    laneMidiTarget,
                    EventMappingChainId: mapping?.Steps.Id,
                    EventMappingTarget: TemplateEventMidiTargets.ToMappingTarget(laneMidiTarget)));
                foreach (TemplateEvent item in activeVoice.Events.Where(item =>
                    item.Kind != TemplateEventKind.Note
                    && TemplateEventMidiTargets.Enumerate(item).Contains(laneMidiTarget)))
                {
                    events.Add(new(
                        item.Id,
                        TimelineItemKind.LogicalParameterPoint,
                        item.Tick,
                        checked(item.Tick + 1),
                        eventLane,
                        NormalizeMidiValue(
                            laneMidiTarget,
                            TemplateEventMidiTargets.GetValue(item, laneMidiTarget)),
                        2,
                        SelectionState(item.Id)));
                }
            }

            AddInitialStateEntries(activeVoice.InitialState);
        }

        foreach (InstrumentRenderLane lane in lanes) RenderLanes.Add(lane);
        MidiValueTarget? selectedTarget = null;
        if (Selection.Primary is MidoraId selectedEventId
            && activeVoice?.Events.FirstOrDefault(item => item.Id == selectedEventId) is TemplateEvent selectedEvent)
        {
            MidiValueTarget[] selectedTargets = TemplateEventMidiTargets.Enumerate(selectedEvent).ToArray();
            if (selectedTargets.Length != 0)
            {
                selectedTarget = previousTarget is MidiValueTarget previous
                    && selectedTargets.Contains(previous)
                        ? previous
                        : selectedTargets[0];
            }
        }
        MidiValueTarget? preferredTarget = selectedTarget ?? previousTarget;
        int preferredLane = preferredTarget is MidiValueTarget target
            ? lanes.FindIndex(item => item.Target == target)
            : -1;
        ActiveRenderLaneIndex = lanes.Count == 0
            ? -1
            : preferredLane >= 0
                ? preferredLane
                : Math.Clamp(ActiveRenderLaneIndex, 0, lanes.Count - 1);
        ActiveValueMinimum = 0;
        ActiveValueMaximum = 127;
        ActiveValueIntegral = true;
        if (lanes.Count != 0)
        {
            InstrumentRenderLane activeLane = lanes[ActiveRenderLaneIndex];
            if (activeLane.Target is MidiValueTarget activeTarget)
            {
                (ActiveValueMinimum, ActiveValueMaximum) = MidiValueRange(activeTarget);
            }
        }
        TimelineRenderItem[] activeEvents = lanes.Count == 0
            ? []
            : events.Where(item => item.Lane == ActiveRenderLaneIndex)
                .Select(item => item with { Lane = 0 })
                .ToArray();
        string projectionSuffix = activeVoice?.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none";
        SubVoiceNoteSnapshot = new(
            revision,
            $"instrument-notes:{instrument.Id.Value}:{projectionSuffix}",
            notes,
            Enumerable.Range(0, 128).Select(lane => MidiNoteName(127 - lane)).ToArray());
        SubVoiceEventSnapshot = new(
            revision,
            $"instrument-events:{instrument.Id.Value}:{projectionSuffix}",
            activeEvents,
            lanes.Count == 0 ? [] : [lanes[ActiveRenderLaneIndex].Label]);
        SubVoiceVelocitySnapshot = new(
            revision,
            $"instrument-velocities:{instrument.Id.Value}:{projectionSuffix}",
            activeVoice?.Events
                .Where(item => item.Kind == TemplateEventKind.Note)
                .Select(item => new TimelineRenderItem(
                    item.Id,
                    TimelineItemKind.Velocity,
                    item.Tick,
                    checked(item.Tick + 1),
                    0,
                    item.Value / 127d,
                    item.Number,
                    SelectionState(item.Id)))
                .ToArray() ?? [],
            ["Velocity"]);
        SubVoiceSnapshot = new(
            revision,
            $"instrument-overview:{instrument.Id.Value}:{projectionSuffix}",
            notes.Concat(events));

        TimelineItemState SelectionState(MidoraId id)
        {
            TimelineItemState state = TimelineItemState.None;
            if (Selection.Ids.Contains(id)) state |= TimelineItemState.Selected;
            if (Selection.Primary == id) state |= TimelineItemState.Primary;
            return state;
        }

        void AddInitialStateEntries(MidiInitialState state)
        {
            Add("Bank MSB", state.BankMsb);
            Add("Bank LSB", state.BankLsb);
            Add("Program", state.Program is int program ? program + 1 : null);
            Add("Pitch Bend", state.PitchBend);
            Add("Pitch Bend Range Semitones", state.PitchBendRangeSemitones);
            Add("Pitch Bend Range Cents", state.PitchBendRangeCents);
            foreach ((int number, int value) in state.Controllers.OrderBy(item => item.Key))
                InitialStateEntries.Add(new(MidiControlChangeCatalog.Format(number), value.ToString(System.Globalization.CultureInfo.InvariantCulture), "SubVoice"));
            foreach ((int number, int value) in state.RegisteredParameters.OrderBy(item => item.Key))
                InitialStateEntries.Add(new($"RPN {number}", value.ToString(System.Globalization.CultureInfo.InvariantCulture), "SubVoice"));
            foreach ((int number, int value) in state.NonRegisteredParameters.OrderBy(item => item.Key))
                InitialStateEntries.Add(new($"NRPN {number}", value.ToString(System.Globalization.CultureInfo.InvariantCulture), "SubVoice"));

            void Add(string target, int? value)
            {
                InitialStateEntries.Add(new(
                    target,
                    value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "Not set",
                    "SubVoice"));
            }
        }

        void AddInstrumentInitialStateFields(MidiInitialState state)
        {
            Add("bankMsb", "BANK MSB", state.BankMsb);
            Add("bankLsb", "BANK LSB", state.BankLsb);
            Add("program", "PROGRAM (0–127)", state.Program);
            Add("pitchBend", "PITCH BEND", state.PitchBend);
            Add("pitchRangeSemitones", "PITCH RANGE SEMITONES", state.PitchBendRangeSemitones);
            Add("pitchRangeCents", "PITCH RANGE CENTS", state.PitchBendRangeCents);
            foreach ((int number, int value) in state.Controllers.OrderBy(item => item.Key))
                Add($"cc.{number}", MidiControlChangeCatalog.Format(number), value);
            foreach ((int number, int value) in state.RegisteredParameters.OrderBy(item => item.Key))
                Add($"rpn.{number}", $"RPN {number}", value);
            foreach ((int number, int value) in state.NonRegisteredParameters.OrderBy(item => item.Key))
                Add($"nrpn.{number}", $"NRPN {number}", value);

            void Add(string key, string label, int? value) =>
                InstrumentInitialStateFields.Add(new(
                    key,
                    label,
                    value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty));
        }

        void AddMappingChain(MappingChain chain, string owner, bool canDelete)
        {
            MappingChains.Add(new(chain.Id, owner, chain.Count, canDelete));
            for (int index = 0; index < chain.Count; index++)
            {
                ValueMappingStep step = chain[index];
                MappingSteps.Add(new(
                    step.Id,
                    chain.Id,
                    owner,
                    index,
                    $"{step.Source} · {step.Operation}{(step.IsEnabled ? string.Empty : " · Disabled")}"));
            }
        }
    }

    private sealed record SubVoiceEditorSettings(
        TimelineEditorSettings Piano,
        TimelineEditorSettings EventLane);

    public InstrumentRenderLane? GetRenderLane(int lane) =>
        lane >= 0 && lane < RenderLanes.Count ? RenderLanes[lane] : null;

    private static SubVoice? ResolveActiveSubVoice(
        EventInstrument instrument,
        MidoraId? primary,
        MidoraId? previous)
    {
        if (primary is MidoraId selected)
        {
            SubVoice? direct = instrument.SubVoices.FirstOrDefault(item => item.Id == selected);
            if (direct is not null) return direct;
            SubVoice? owner = instrument.SubVoices.FirstOrDefault(voice =>
                voice.Events.Any(item => item.Id == selected)
                || voice.Curves.Any(curve => curve.Id == selected || curve.Points.Any(point => point.Id == selected)));
            if (owner is not null) return owner;
        }
        return previous is MidoraId previousId
            ? instrument.SubVoices.FirstOrDefault(item => item.Id == previousId) ?? instrument.SubVoices.FirstOrDefault()
            : instrument.SubVoices.FirstOrDefault();
    }

    internal static (double Minimum, double Maximum) MidiValueRange(MidiValueTarget target) => target.Kind switch
    {
        MidiValueKind.PitchBend => (-8192, 8191),
        MidiValueKind.RegisteredParameter or MidiValueKind.NonRegisteredParameter => (0, 16383),
        MidiValueKind.PitchBendRangeCents => (0, 99),
        _ => (0, 127)
    };

    internal static double DenormalizeMidiValue(MidiValueTarget target, double normalized)
    {
        (double minimum, double maximum) = MidiValueRange(target);
        return Math.Round(minimum + Math.Clamp(normalized, 0, 1) * (maximum - minimum), MidpointRounding.AwayFromZero);
    }

    private static double NormalizeMidiValue(MidiValueTarget target, double value)
    {
        (double minimum, double maximum) = MidiValueRange(target);
        return Math.Clamp((value - minimum) / (maximum - minimum), 0, 1);
    }

    private static string FormatTarget(MidiValueTarget target) => TemplateEventMidiTargets.Format(target);

    private static string FormatEventMappingTarget(TemplateEventMappingTarget target)
    {
        string eventName = target.EventKind switch
        {
            TemplateEventKind.Note => "Note",
            TemplateEventKind.ControlChange => MidiControlChangeCatalog.Format(target.EventNumber),
            TemplateEventKind.Bank => "Bank",
            TemplateEventKind.Program => "Program",
            TemplateEventKind.PitchBend => "Pitch Bend",
            TemplateEventKind.RegisteredParameter => $"RPN {target.EventNumber}",
            TemplateEventKind.NonRegisteredParameter => $"NRPN {target.EventNumber}",
            TemplateEventKind.PitchBendRange => "Pitch Bend Range",
            _ => target.EventKind.ToString()
        };
        string parameter = (target.EventKind, target.Parameter) switch
        {
            (TemplateEventKind.Note, TemplateEventMappingParameter.Number) => "Number",
            (TemplateEventKind.Note, TemplateEventMappingParameter.Value) => "Velocity",
            (TemplateEventKind.Bank, TemplateEventMappingParameter.Value) => "MSB",
            (TemplateEventKind.Bank, TemplateEventMappingParameter.SecondaryValue) => "LSB",
            (TemplateEventKind.PitchBendRange, TemplateEventMappingParameter.Value) => "Semitones",
            (TemplateEventKind.PitchBendRange, TemplateEventMappingParameter.SecondaryValue) => "Cents",
            _ => "Value"
        };
        return $"{eventName} · {parameter}";
    }

    private static string MidiNoteName(int note) => TimelineWorkspaceViewModel.MidiNoteName(note);
}

public sealed class MappingFunctionWorkspaceViewModel(
    MidoraId instrumentId,
    MidoraId functionId,
    string header)
    : WorkspaceViewModel(
        WorkspaceKey.ForObject(WorkspaceKind.MappingFunctionEditor, functionId),
        header)
{
    private string _draftName = string.Empty;
    private string _draftBody = string.Empty;
    private string _declaredContextFields = string.Empty;
    private string _status = "Applied";
    private string _findText = string.Empty;
    private string _findStatus = string.Empty;
    private string _caretStatus = "Ln 1, Col 1";
    private bool _initialized;
    private bool _synchronizing;
    private bool _isDirty;

    public MidoraId InstrumentId { get; } = instrumentId;
    public string DraftName { get => _draftName; set { if (Set(ref _draftName, value)) MarkDirty(); } }
    public string DraftBody { get => _draftBody; set { if (Set(ref _draftBody, value)) MarkDirty(); } }
    public string DeclaredContextFields { get => _declaredContextFields; set { if (Set(ref _declaredContextFields, value)) MarkDirty(); } }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string FindText { get => _findText; set => Set(ref _findText, value ?? string.Empty); }
    public string FindStatus { get => _findStatus; set => Set(ref _findStatus, value ?? string.Empty); }
    public string CaretStatus { get => _caretStatus; set => Set(ref _caretStatus, value ?? string.Empty); }
    public bool IsDirty { get => _isDirty; private set => Set(ref _isDirty, value); }

    public override void Rebuild(MidoraProject project, long revision)
    {
        EventInstrument? instrument = project.EventInstruments.FirstOrDefault(item => item.Id == InstrumentId);
        CSharpMappingFunction? function = instrument?.MappingFunctions.FirstOrDefault(item => item.Id == ObjectId);
        if (function is null)
        {
            Status = "The Mapping Function no longer exists.";
            return;
        }
        Header = function.Name;
        if (_initialized && IsDirty) return;
        _synchronizing = true;
        DraftName = function.Name;
        DraftBody = function.Body;
        DeclaredContextFields = string.Join(", ", function.DeclaredContextFields.Order(StringComparer.Ordinal));
        _synchronizing = false;
        _initialized = true;
        IsDirty = false;
        Status = $"Applied · ABI v{function.AbiVersion}";
    }

    public IReadOnlyList<string> ParseDeclaredContextFields() => DeclaredContextFields
        .Split([',', ';', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public void MarkApplied()
    {
        IsDirty = false;
        Status = "Applied";
    }

    public void SetValidationStatus(string status) => Status = status;

    private void MarkDirty()
    {
        if (_synchronizing || !_initialized) return;
        IsDirty = true;
        Status = "Draft differs from applied version";
    }
}

public sealed class SettingsWorkspaceViewModel()
    : WorkspaceViewModel(
        WorkspaceKey.ForType(WorkspaceKind.ProjectSettings),
        "Project Settings")
{
    private string _projectName = string.Empty;
    private string _projectVersion = string.Empty;
    private string _author = string.Empty;
    private string _soundFont = string.Empty;
    private string _playback = string.Empty;
    private string _audioRender = string.Empty;
    private int _compiledNoteOnCount;
    private int _compiledMidiEventCount;
    private long _totalEditingTimeMilliseconds;

    public string ProjectName { get => _projectName; private set => Set(ref _projectName, value); }
    public string ProjectVersion { get => _projectVersion; private set => Set(ref _projectVersion, value); }
    public string Author { get => _author; private set => Set(ref _author, value); }
    public string SoundFont
    {
        get => _soundFont;
        private set
        {
            if (Set(ref _soundFont, value)) Raise(nameof(HasSoundFont));
        }
    }
    public bool HasSoundFont => !string.Equals(SoundFont, "No SoundFont Selected", StringComparison.Ordinal);
    public bool HasEmbeddedSoundFont { get; private set; }
    public string Playback { get => _playback; private set => Set(ref _playback, value); }
    public string AudioRender { get => _audioRender; private set => Set(ref _audioRender, value); }
    public ObservableCollection<InspectorField> GeneralFields { get; } = [];
    public ObservableCollection<InspectorField> PlaybackFields { get; } = [];
    public ObservableCollection<InspectorField> MidiExportFields { get; } = [];
    public ObservableCollection<InspectorField> AudioRenderFields { get; } = [];
    public ObservableCollection<TrackSelectionRow> AudioRenderTracks { get; } = [];
    public ObservableCollection<InspectorField> InitialStateFields { get; } = [];
    public ObservableCollection<InspectorField> ResetDefaultFields { get; } = [];

    public override void Rebuild(MidoraProject project, long revision)
    {
        ProjectName = project.Metadata.ProjectName;
        ProjectVersion = project.Metadata.ProjectVersion;
        Author = project.Metadata.AuthorOrTeam;
        SoundFont = project.SoundFont.Reference is null
            ? "No SoundFont Selected"
            : $"{project.SoundFont.Reference.Mode} · {project.SoundFont.Reference.OriginalFileName}";
        HasEmbeddedSoundFont = project.SoundFont.Reference is EmbeddedProjectSoundFontReference;
        Raise(nameof(HasEmbeddedSoundFont));
        Playback = $"Master {project.Playback.MasterVolumeDecibels:0.###} dB · Limiter {(project.Playback.LimiterEnabled ? "On" : "Off")}";
        AudioRender = $"{project.AudioRender.Mode} · {project.AudioRender.SampleRate:N0} Hz · {project.AudioRender.MaximumSampleVoicesPerUnitStream:N0} voices / Unit";
        Replace(GeneralFields,
            new("settings.project.name", "PROJECT NAME", project.Metadata.ProjectName),
            new("settings.project.version", "PROJECT VERSION", project.Metadata.ProjectVersion),
            new("settings.project.author", "AUTHOR / TEAM", project.Metadata.AuthorOrTeam),
            new("settings.project.originalWork", "ORIGINAL WORK", project.Metadata.OriginalWork),
            new("settings.project.copyright", "COPYRIGHT", project.Metadata.Copyright),
            new("settings.project.noteCount", "NOTES", _compiledNoteOnCount.ToString("N0"), false),
            new("settings.project.eventCount", "EVENTS", _compiledMidiEventCount.ToString("N0"), false),
            new("settings.project.totalWorkTime", "PROJECT WORK TIME", FormatDuration(_totalEditingTimeMilliseconds), false),
            new("settings.project.tpq", "TICKS PER QUARTER NOTE", project.TicksPerQuarterNote.ToString(), false));
        Replace(PlaybackFields,
            new("settings.playback.master", "MASTER VOLUME (DB)", project.Playback.MasterVolumeDecibels.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Choice("settings.playback.limiter", "LIMITER ENABLED", project.Playback.LimiterEnabled, ["False", "True"]),
            Choice("settings.playback.stopCursor", "STOP CURSOR BEHAVIOR", project.Playback.StopCursorBehavior));
        Replace(MidiExportFields,
            Choice("settings.midi.mode", "MODE", project.Export.Mode),
            Choice("settings.midi.rangeMode", "RANGE MODE", project.Export.RangeMode),
            new("settings.midi.start", "MANUAL START TICK", project.Export.ManualStartTick?.ToString() ?? string.Empty),
            new("settings.midi.end", "MANUAL END TICK", project.Export.ManualEndTick?.ToString() ?? string.Empty),
            Choice("settings.midi.trackSelection", "TRACK SELECTION", project.Export.TrackSelectionMode),
            Choice("settings.midi.routing", "ROUTING", project.Export.Routing),
            Choice("settings.midi.readme", "INCLUDE README", project.Export.IncludeReadme, ["False", "True"]),
            Choice("settings.midi.warnings", "WARNINGS AS ERRORS", project.Export.TreatWarningsAsErrors, ["False", "True"]));
        Replace(AudioRenderFields,
            Choice("settings.audio.mode", "MODE", project.AudioRender.Mode),
            Choice("settings.audio.rangeMode", "RANGE MODE", project.AudioRender.RangeMode),
            new("settings.audio.start", "MANUAL START TICK", project.AudioRender.ManualStartTick?.ToString() ?? string.Empty),
            new("settings.audio.end", "MANUAL END TICK", project.AudioRender.ManualEndTick?.ToString() ?? string.Empty),
            Choice("settings.audio.trackSelection", "TRACK SELECTION", project.AudioRender.TrackSelectionMode),
            new("settings.audio.sampleRate", "SAMPLE RATE", project.AudioRender.SampleRate.ToString()),
            new("settings.audio.voices", "MAXIMUM SAMPLE VOICES / UNIT", project.AudioRender.MaximumSampleVoicesPerUnitStream.ToString()));
        AudioRenderTracks.Clear();
        foreach (LogicalTrack track in project.Tracks)
        {
            AudioRenderTracks.Add(new(
                track.Id,
                string.IsNullOrWhiteSpace(track.Name) ? $"Logical Track {project.Tracks.IndexOf(track) + 1}" : track.Name,
                project.AudioRender.ExplicitLogicalTrackIds.Contains(track.Id)));
        }
        Replace(InitialStateFields, StateFields("settings.initial", project.GlobalInitialState));
        Replace(ResetDefaultFields, StateFields("settings.reset", project.GlobalResetDefaults));
    }

    public void UpdateRuntimeInformation(
        CompilationStatistics statistics,
        long totalEditingTimeMilliseconds)
    {
        if (totalEditingTimeMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalEditingTimeMilliseconds));
        }
        _compiledNoteOnCount = statistics.NoteOnEventCount;
        _compiledMidiEventCount = statistics.EventCount;
        _totalEditingTimeMilliseconds = totalEditingTimeMilliseconds;
        UpdateReadOnlyField("settings.project.noteCount", _compiledNoteOnCount.ToString("N0"));
        UpdateReadOnlyField("settings.project.eventCount", _compiledMidiEventCount.ToString("N0"));
        UpdateReadOnlyField(
            "settings.project.totalWorkTime",
            FormatDuration(_totalEditingTimeMilliseconds));
    }

    private void UpdateReadOnlyField(string key, string value)
    {
        InspectorField? field = GeneralFields.FirstOrDefault(item => item.Key == key);
        if (field is not null)
        {
            field.Value = value;
        }
    }

    private static string FormatDuration(long milliseconds)
    {
        long totalSeconds = milliseconds / 1_000;
        long days = totalSeconds / 86_400;
        long hours = totalSeconds / 3_600 % 24;
        long minutes = totalSeconds / 60 % 60;
        long seconds = totalSeconds % 60;
        return days == 0
            ? $"{hours:00}:{minutes:00}:{seconds:00}"
            : $"{days:N0}d {hours:00}:{minutes:00}:{seconds:00}";
    }

    private static InspectorField[] StateFields(string prefix, MidiInitialState state)
    {
        List<InspectorField> fields =
        [
            new($"{prefix}.bankMsb", "BANK MSB", state.BankMsb?.ToString() ?? string.Empty),
            new($"{prefix}.bankLsb", "BANK LSB", state.BankLsb?.ToString() ?? string.Empty),
            new($"{prefix}.program", "PROGRAM (0–127)", state.Program?.ToString() ?? string.Empty),
            new($"{prefix}.pitchBend", "PITCH BEND", state.PitchBend?.ToString() ?? string.Empty),
            new($"{prefix}.pitchRangeSemitones", "PITCH RANGE SEMITONES", state.PitchBendRangeSemitones?.ToString() ?? string.Empty),
            new($"{prefix}.pitchRangeCents", "PITCH RANGE CENTS", state.PitchBendRangeCents?.ToString() ?? string.Empty)
        ];
        fields.AddRange(state.Controllers.OrderBy(item => item.Key)
            .Select(item => new InspectorField(
                $"{prefix}.cc.{item.Key}",
                MidiControlChangeCatalog.Format(item.Key),
                item.Value.ToString())));
        fields.AddRange(state.RegisteredParameters.OrderBy(item => item.Key)
            .Select(item => new InspectorField($"{prefix}.rpn.{item.Key}", $"RPN {item.Key}", item.Value.ToString())));
        fields.AddRange(state.NonRegisteredParameters.OrderBy(item => item.Key)
            .Select(item => new InspectorField($"{prefix}.nrpn.{item.Key}", $"NRPN {item.Key}", item.Value.ToString())));
        return fields.ToArray();
    }

    private static InspectorField Choice<T>(
        string key,
        string label,
        T value,
        IReadOnlyList<string>? options = null)
        where T : notnull => new(
            key,
            label,
            value.ToString() ?? string.Empty,
            options: options ?? (typeof(T).IsEnum ? Enum.GetNames(typeof(T)) : []));

    private static void Replace(ObservableCollection<InspectorField> target, params InspectorField[] fields)
    {
        target.Clear();
        foreach (InspectorField field in fields) target.Add(field);
    }
}

public sealed class DiagnosticsWorkspaceViewModel()
    : WorkspaceViewModel(
        WorkspaceKey.ForType(WorkspaceKind.Diagnostics),
        "Diagnostics")
{
    private readonly List<DiagnosticRow> _allDiagnostics = [];
    private readonly HashSet<MidoraId> _workspaceScopeIds = [];
    private readonly HashSet<MidoraId> _selectionScopeIds = [];
    private string _searchText = string.Empty;
    private string _severityFilter = "All severities";
    private string _statusFilter = "Active";
    private string _scopeFilter = "Whole Project";
    public ObservableCollection<DiagnosticRow> Diagnostics { get; } = [];
    public IReadOnlyList<string> SeverityFilters { get; } = ["All severities", "Error", "Warning", "Information"];
    public IReadOnlyList<string> StatusFilters { get; } =
        ["All statuses", "Active", "Prior Result", "Resolved", "Runtime History"];
    public IReadOnlyList<string> ScopeFilters { get; } = ["Whole Project", "Current Workspace", "Current Selection", "Current Task"];
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? string.Empty)) ApplyFilter();
        }
    }
    public string SeverityFilter
    {
        get => _severityFilter;
        set { if (Set(ref _severityFilter, value ?? "All severities")) ApplyFilter(); }
    }
    public string StatusFilter
    {
        get => _statusFilter;
        set { if (Set(ref _statusFilter, value ?? "Active")) ApplyFilter(); }
    }
    public string ScopeFilter
    {
        get => _scopeFilter;
        set { if (Set(ref _scopeFilter, value ?? "Whole Project")) ApplyFilter(); }
    }
    public string Summary => $"Showing {Diagnostics.Count} of {_allDiagnostics.Count} diagnostic(s)";

    public override void Rebuild(MidoraProject project, long revision)
    {
    }

    public void Replace(IEnumerable<DiagnosticRow> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        _allDiagnostics.Clear();
        _allDiagnostics.AddRange(diagnostics);
        ApplyFilter();
    }

    public void SetScope(WorkspaceViewModel? workspace)
    {
        _workspaceScopeIds.Clear();
        _selectionScopeIds.Clear();
        if (workspace?.ObjectId is MidoraId objectId) _workspaceScopeIds.Add(objectId);
        if (workspace is not null)
        {
            _selectionScopeIds.UnionWith(workspace.Selection.Ids);
            _workspaceScopeIds.UnionWith(workspace.Selection.Ids);
        }
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string search = SearchText.Trim();
        IEnumerable<DiagnosticRow> query = _allDiagnostics;
        if (!string.Equals(SeverityFilter, "All severities", StringComparison.Ordinal))
        {
            string expected = SeverityFilter == "Information" ? "Info" : SeverityFilter;
            query = query.Where(item => string.Equals(item.Severity, expected, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.Equals(StatusFilter, "All statuses", StringComparison.Ordinal))
        {
            query = query.Where(item => string.Equals(item.Status, StatusFilter, StringComparison.Ordinal));
        }
        query = ScopeFilter switch
        {
            "Current Workspace" => query.Where(item => MatchesAny(item.SourceReference, _workspaceScopeIds)),
            "Current Selection" => query.Where(item => MatchesAny(item.SourceReference, _selectionScopeIds)),
            "Current Task" => Enumerable.Empty<DiagnosticRow>(),
            _ => query
        };
        if (search.Length != 0)
        {
            query = query.Where(item => item.Severity.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || item.Category.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || item.Code.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || item.Message.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || item.Source.Contains(search, StringComparison.CurrentCultureIgnoreCase));
        }
        Diagnostics.Clear();
        foreach (DiagnosticRow diagnostic in query)
        {
            Diagnostics.Add(diagnostic);
        }
        Raise(nameof(Summary));
    }

    private static bool MatchesAny(SourceReference source, HashSet<MidoraId> ids)
    {
        if (ids.Count == 0) return false;
        return ids.Contains(source.TrackId)
            || ids.Contains(source.SegmentId)
            || ids.Contains(source.LogicalNoteId)
            || ids.Contains(source.EventInstrumentId)
            || ids.Contains(source.SubVoiceId)
            || ids.Contains(source.SourceEventId)
            || ids.Contains(source.LogicalParameterId)
            || ids.Contains(source.LogicalParameterMappingId)
            || ids.Contains(source.MappingStepId)
            || ids.Contains(source.MappingFunctionId)
            || ids.Contains(source.ValueCurveId)
            || ids.Contains(source.EnvelopeId);
    }
}

public static class DiagnosticProjection
{
    public static DiagnosticRow FromCompiler(
        CompilerDiagnostic diagnostic,
        bool isCurrent = true) => new(
        diagnostic.Severity.ToString(),
        "Compile",
        diagnostic.Code,
        diagnostic.Message,
        SourceText(diagnostic.Source),
        isCurrent,
        diagnostic.Source);

    private static string SourceText(SourceReference source)
    {
        if (source.LogicalNoteId != default) return $"Logical Note {source.LogicalNoteId}";
        if (source.SegmentId != default) return $"Segment {source.SegmentId}";
        if (source.TrackId != default) return $"Logical Track {source.TrackId}";
        if (source.SubVoiceId != default) return $"SubVoice {source.SubVoiceId}";
        if (source.EventInstrumentId != default) return $"Event Instrument {source.EventInstrumentId}";
        if (source.MappingFunctionId != default) return $"Mapping Function {source.MappingFunctionId}";
        return source.Tick >= 0 ? $"Tick {source.Tick}" : "Project";
    }
}
