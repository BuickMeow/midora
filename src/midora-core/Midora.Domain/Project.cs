namespace Midora.Domain;

public sealed class LogicalNote
{
    public MidoraId Id { get; init; } = MidoraId.New();
    public long StartTick { get; set; }
    public long LengthTicks { get; set; }
    public int Note { get; set; } = 60;
    public int Velocity { get; set; } = 100;
}

public sealed class LogicalParameterLane
{
    public MidoraId Id { get; init; } = MidoraId.New();
    public MidoraId ParameterId { get; set; }
    public List<CurvePoint> Points { get; } = [];
}

public sealed class Segment
{
    public MidoraId Id { get; init; } = MidoraId.New();
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
    public MidoraId Id { get; init; } = MidoraId.New();
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
    public MidoraProject(int ticksPerQuarterNote)
    {
        TicksPerQuarterNote = ticksPerQuarterNote;
    }

    public MidoraId Id { get; init; } = MidoraId.New();
    public int TicksPerQuarterNote { get; }
    public ConductorTrack Conductor { get; } = new();
    public MidiInitialState GlobalInitialState { get; } = new();
    public MidiInitialState GlobalResetDefaults { get; } = new();
    public GlobalEventScopeDefaults GlobalEventScopeDefaults { get; } = new();
    public List<EventInstrument> EventInstruments { get; } = [];
    public List<EventInstrumentLibraryFolder> EventInstrumentFolders { get; } = [];
    public List<LogicalTrack> Tracks { get; } = [];
    public PlaybackProjectSettings Playback { get; } = new();
    public string? SoundFontPath { get; set; }
}

public sealed class EventInstrumentLibraryFolder
{
    public MidoraId Id { get; init; } = MidoraId.New();
    public required string Name { get; set; }
    public MidoraId? ParentFolderId { get; set; }
}

public sealed class ProjectChangeSet
{
    public static ProjectChangeSet Everything { get; } = new() { AffectsEverything = true };

    public bool AffectsEverything { get; init; }
    public bool AffectsConductor { get; init; }
    public HashSet<MidoraId> TrackIds { get; } = [];
    public HashSet<MidoraId> EventInstrumentIds { get; } = [];
}
