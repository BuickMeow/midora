namespace Midora.Domain;

public sealed class LogicalNote
{
    public LogicalNote(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
    }

    internal LogicalNote(MidoraProject project, MidoraId preservedId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        Id = preservedId;
    }

    public MidoraId Id { get; init; }
    public long StartTick { get; set; }
    public long LengthTicks { get; set; }
    public int Note { get; set; } = 60;
    public int Velocity { get; set; } = 100;
}

public sealed class LogicalParameterLane
{
    public LogicalParameterLane(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
    }

    internal LogicalParameterLane(MidoraProject project, MidoraId preservedId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        Id = preservedId;
    }

    public MidoraId Id { get; init; }
    public MidoraId ParameterId { get; set; }
    public List<CurvePoint> Points { get; } = [];
}

public sealed class Segment
{
    public Segment(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
    }

    internal Segment(MidoraProject project, MidoraId preservedId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        Id = preservedId;
    }

    public MidoraId Id { get; init; }
    public long ProjectStartTick { get; set; }
    public long LengthTicks { get; set; }
    public long ContentOffsetTick { get; set; }
    public List<LogicalNote> Notes { get; } = [];
    public List<LogicalParameterLane> ParameterLanes { get; } = [];

    public TickRange ProjectRange => new(ProjectStartTick, checked(ProjectStartTick + LengthTicks));
    public long ContentEndTick => checked(ContentOffsetTick + LengthTicks);
}

public sealed class LogicalTrack
{
    public LogicalTrack(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
    }

    internal LogicalTrack(MidoraProject project, MidoraId preservedId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        Id = preservedId;
    }

    public MidoraId Id { get; init; }
    public required string Name { get; set; }
    public MidoraId? EventInstrumentId { get; set; }
    public string? LastBoundEventInstrumentName { get; set; }
    public MidoraColor? ColorOverride { get; set; }
    public List<Segment> Segments { get; } = [];
}

public sealed class PlaybackProjectSettings
{
    public double MasterVolumeDecibels { get; set; } = -0.1;
    public bool LimiterEnabled { get; set; } = true;
    public StopCursorBehavior StopCursorBehavior { get; set; } = StopCursorBehavior.ReturnToPlaybackStart;
}

public enum StopCursorBehavior
{
    ReturnToPlaybackStart,
    StayAtStoppedTick
}

public sealed class GlobalEventScopeDefaults
{
    public static bool IsChannelWide(TemplateEventKind kind) => kind != TemplateEventKind.Note;
}

public enum ProjectRangeMode
{
    ProjectDefaultRange,
    ManualRange
}

public enum ProjectTrackSelectionMode
{
    AllValidLogicalTracks,
    ExplicitLogicalTrackIds
}

public enum AudioRenderMode
{
    WholeMix,
    PerLogicalTrack
}

public enum ProjectMidiExportMode
{
    WholeProject,
    PerLogicalTrack,
    PerPort
}

public enum ProjectMidiExportTrackSelectionMode
{
    AllValidLogicalTracks,
    ExplicitAtTaskStart
}

public enum ProjectMidiExportRoutingStrategy
{
    Compact,
    Preserve
}

public sealed class ExportProjectSettings
{
    public ProjectMidiExportMode Mode { get; set; } = ProjectMidiExportMode.WholeProject;
    public ProjectRangeMode RangeMode { get; set; } = ProjectRangeMode.ProjectDefaultRange;
    public long? ManualStartTick { get; set; }
    public long? ManualEndTick { get; set; }
    public ProjectMidiExportTrackSelectionMode TrackSelectionMode { get; set; } =
        ProjectMidiExportTrackSelectionMode.AllValidLogicalTracks;
    public ProjectMidiExportRoutingStrategy Routing { get; set; } =
        ProjectMidiExportRoutingStrategy.Compact;
    public bool IncludeReadme { get; set; } = true;
    public bool TreatWarningsAsErrors { get; set; }
}

public sealed class AudioRenderProjectSettings
{
    public const int MinimumSampleRate = 8_000;
    public const int MaximumSampleRate = 192_000;
    public const int DefaultSampleRate = 48_000;
    public const int MinimumSampleVoicesPerUnitStream = 1;
    public const int MaximumSampleVoicesPerUnitStreamLimit = 16_777_216;
    public const int DefaultSampleVoicesPerUnitStream = 500;

    public AudioRenderMode Mode { get; set; } = AudioRenderMode.WholeMix;
    public ProjectRangeMode RangeMode { get; set; } = ProjectRangeMode.ProjectDefaultRange;
    public long? ManualStartTick { get; set; }
    public long? ManualEndTick { get; set; }
    public ProjectTrackSelectionMode TrackSelectionMode { get; set; } =
        ProjectTrackSelectionMode.AllValidLogicalTracks;
    public HashSet<MidoraId> ExplicitLogicalTrackIds { get; } = [];
    public int SampleRate { get; set; } = DefaultSampleRate;
    public int MaximumSampleVoicesPerUnitStream { get; set; } = DefaultSampleVoicesPerUnitStream;
}

public sealed class MidoraProject
{
    public const int MinimumTicksPerQuarterNote = 1;
    public const int MaximumTicksPerQuarterNote = 32_767;

    private long _nextStableId;

    public MidoraProject(int ticksPerQuarterNote)
        : this(ticksPerQuarterNote, TimeProvider.System.GetUtcNow())
    {
    }

    public MidoraProject(int ticksPerQuarterNote, DateTimeOffset createdAtUtc)
        : this(
            ticksPerQuarterNote,
            1,
            createInitialConductorState: true,
            createdAtUtc)
    {
    }

    internal MidoraProject(int ticksPerQuarterNote, long nextStableId)
        : this(
            ticksPerQuarterNote,
            nextStableId,
            createInitialConductorState: false,
            TimeProvider.System.GetUtcNow())
    {
    }

    internal MidoraProject(
        int ticksPerQuarterNote,
        long nextStableId,
        DateTimeOffset createdAtUtc)
        : this(
            ticksPerQuarterNote,
            nextStableId,
            createInitialConductorState: false,
            createdAtUtc)
    {
    }

    private MidoraProject(
        int ticksPerQuarterNote,
        long nextStableId,
        bool createInitialConductorState,
        DateTimeOffset createdAtUtc)
    {
        if (ticksPerQuarterNote is < MinimumTicksPerQuarterNote or > MaximumTicksPerQuarterNote)
        {
            throw new ArgumentOutOfRangeException(nameof(ticksPerQuarterNote));
        }
        if (nextStableId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nextStableId));
        }
        TicksPerQuarterNote = ticksPerQuarterNote;
        _nextStableId = nextStableId;
        Metadata = new ProjectMetadata(createdAtUtc);
        Conductor = new ConductorTrack(this, createInitialConductorState);
    }

    public int TicksPerQuarterNote { get; }
    public long NextStableId => _nextStableId;
    public ProjectMetadata Metadata { get; }
    public ConductorTrack Conductor { get; }
    public MidiInitialState GlobalInitialState { get; } = new();
    public MidiInitialState GlobalResetDefaults { get; } = new();
    public GlobalEventScopeDefaults GlobalEventScopeDefaults { get; } = new();
    public List<EventInstrument> EventInstruments { get; } = [];
    public List<DamagedProjectObject> DamagedEventInstruments { get; } = [];
    public List<EventInstrumentLibraryFolder> EventInstrumentFolders { get; } = [];
    public List<LogicalTrack> Tracks { get; } = [];
    public List<DamagedProjectObject> DamagedLogicalTracks { get; } = [];
    public PlaybackProjectSettings Playback { get; } = new();
    public ExportProjectSettings Export { get; } = new();
    public AudioRenderProjectSettings AudioRender { get; } = new();
    public ProjectSoundFontSettings SoundFont { get; } = new();

    public MidoraId AllocateStableId()
    {
        if (_nextStableId == long.MaxValue)
        {
            throw new InvalidOperationException("The Project stable ID counter is exhausted.");
        }
        MidoraId result = MidoraId.FromSequence(_nextStableId);
        _nextStableId++;
        return result;
    }

    internal void RestoreNextStableId(long nextStableId)
    {
        if (nextStableId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nextStableId));
        }
        _nextStableId = nextStableId;
    }

    public void SetEndMarker(long? tick)
    {
        if (tick is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tick));
        }
        if (!tick.HasValue)
        {
            Conductor.EndMarker = null;
        }
        else if (Conductor.EndMarker is null)
        {
            Conductor.EndMarker = new ProjectEndMarker(this, tick.Value);
        }
        else
        {
            Conductor.EndMarker.Tick = tick.Value;
        }
    }
}

public sealed record DamagedProjectObject(
    MidoraId Id,
    string NameSnapshot,
    string PackagePath,
    string Error,
    int OriginalIndex);

public sealed class EventInstrumentLibraryFolder
{
    public EventInstrumentLibraryFolder(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
    }

    internal EventInstrumentLibraryFolder(MidoraId preservedId)
    {
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        Id = preservedId;
    }

    public MidoraId Id { get; init; }
    public required string Name { get; set; }
}

public sealed class ProjectChangeSet
{
    public static ProjectChangeSet Everything { get; } = new() { AffectsEverything = true };

    public bool AffectsEverything { get; init; }
    public bool AffectsConductor { get; init; }
    public bool AffectsAudioPcmCacheGeneration { get; init; }
    public HashSet<MidoraId> TrackIds { get; } = [];
    public HashSet<MidoraId> EventInstrumentIds { get; } = [];
}
