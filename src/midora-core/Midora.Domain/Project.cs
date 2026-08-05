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

public sealed class MidoraProject
{
    private UInt128 _nextStableId;

    public MidoraProject(int ticksPerQuarterNote)
        : this(ticksPerQuarterNote, (UInt128)1, createInitialConductorState: true)
    {
    }

    internal MidoraProject(int ticksPerQuarterNote, UInt128 nextStableId)
        : this(ticksPerQuarterNote, nextStableId, createInitialConductorState: false)
    {
    }

    private MidoraProject(
        int ticksPerQuarterNote,
        UInt128 nextStableId,
        bool createInitialConductorState)
    {
        if (ticksPerQuarterNote <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ticksPerQuarterNote));
        }
        if (nextStableId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nextStableId));
        }
        TicksPerQuarterNote = ticksPerQuarterNote;
        _nextStableId = nextStableId;
        Conductor = new ConductorTrack(this, createInitialConductorState);
    }

    public int TicksPerQuarterNote { get; }
    public UInt128 NextStableId => _nextStableId;
    public ConductorTrack Conductor { get; }
    public MidiInitialState GlobalInitialState { get; } = new();
    public MidiInitialState GlobalResetDefaults { get; } = new();
    public GlobalEventScopeDefaults GlobalEventScopeDefaults { get; } = new();
    public List<EventInstrument> EventInstruments { get; } = [];
    public List<EventInstrumentLibraryFolder> EventInstrumentFolders { get; } = [];
    public List<LogicalTrack> Tracks { get; } = [];
    public PlaybackProjectSettings Playback { get; } = new();
    public string? SoundFontPath { get; set; }

    public MidoraId AllocateStableId()
    {
        if (_nextStableId == UInt128.MaxValue)
        {
            throw new InvalidOperationException("The Project stable ID counter is exhausted.");
        }
        MidoraId result = MidoraId.FromSequence(_nextStableId);
        _nextStableId++;
        return result;
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

public sealed class EventInstrumentLibraryFolder
{
    public EventInstrumentLibraryFolder(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
    }

    public MidoraId Id { get; init; }
    public required string Name { get; set; }
}

public sealed class ProjectChangeSet
{
    public static ProjectChangeSet Everything { get; } = new() { AffectsEverything = true };

    public bool AffectsEverything { get; init; }
    public bool AffectsConductor { get; init; }
    public HashSet<MidoraId> TrackIds { get; } = [];
    public HashSet<MidoraId> EventInstrumentIds { get; } = [];
}
