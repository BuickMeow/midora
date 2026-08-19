namespace Midora.Domain;

public enum ArrangementParentKind
{
    EventInstrument,
    MidiChannelRoot
}

public readonly record struct ArrangementParentReference
{
    public ArrangementParentReference(ArrangementParentKind kind, MidoraId parentId)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        if (parentId == default)
        {
            throw new ArgumentOutOfRangeException(nameof(parentId));
        }
        Kind = kind;
        ParentId = parentId;
    }

    public ArrangementParentKind Kind { get; }
    public MidoraId ParentId { get; }
}

public enum MidiChannelRootRoutingMode
{
    Auto,
    Fixed
}

public enum MidiChannelMode
{
    Melodic,
    Percussion
}

public sealed class MidiChannelRoot
{
    public MidiChannelRoot(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
    }

    internal MidiChannelRoot(MidoraProject project, MidoraId preservedId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        Id = preservedId;
    }

    public MidoraId Id { get; init; }
    public required string Name { get; set; }
    public MidiChannelRootRoutingMode RoutingMode { get; set; }
    public byte FixedZeroBasedPort { get; set; }
    public byte FixedZeroBasedChannel { get; set; }
    public MidiChannelMode ChannelMode { get; set; }
    public List<MidoraId> MidiTrackIds { get; } = [];
}

public sealed class PureMidiTrack
{
    public PureMidiTrack(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
    }

    internal PureMidiTrack(MidoraProject project, MidoraId preservedId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        Id = preservedId;
    }

    public MidoraId Id { get; init; }
    public required string Name { get; set; }
    public MidoraId MidiChannelRootId { get; set; }
    public MidoraColor? Color { get; set; }
    public List<MidiSegment> Segments { get; } = [];
}

public sealed class MidiSegment
{
    public MidiSegment(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
    }

    internal MidiSegment(MidoraProject project, MidoraId preservedId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        Id = preservedId;
    }

    public MidoraId Id { get; init; }
    public long ProjectStartTick { get; set; }
    public long LengthTicks { get; set; }
    public long ContentOffsetTick { get; set; }
    public List<DirectMidiNote> Notes { get; } = [];
    public List<DirectMidiChannelEvent> ChannelEvents { get; } = [];
    public List<OpaqueMidiEvent> OpaqueEvents { get; } = [];

    public TickRange ProjectRange => new(ProjectStartTick, checked(ProjectStartTick + LengthTicks));
    public long ContentEndTick => checked(ContentOffsetTick + LengthTicks);
}

public sealed class DirectMidiNote
{
    public DirectMidiNote(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
    }

    internal DirectMidiNote(MidoraProject project, MidoraId preservedId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        Id = preservedId;
    }

    public MidoraId Id { get; init; }
    public long StartTick { get; set; }
    public long LengthTicks { get; set; }
    public int Key { get; set; } = 60;
    public int NoteOnVelocity { get; set; } = 100;
    public int NoteOffVelocity { get; set; }
    public long NoteOnOrder { get; set; }
    public long NoteOffOrder { get; set; }
}

public enum DirectMidiChannelEventKind
{
    NoteOff,
    NoteOn,
    PolyphonicKeyPressure,
    ControlChange,
    ProgramChange,
    ChannelPressure,
    PitchBend
}

public sealed class DirectMidiChannelEvent
{
    public DirectMidiChannelEvent(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
    }

    internal DirectMidiChannelEvent(MidoraProject project, MidoraId preservedId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        Id = preservedId;
    }

    public MidoraId Id { get; init; }
    public long Tick { get; set; }
    public DirectMidiChannelEventKind Kind { get; set; }
    public int Data1 { get; set; }
    public int Data2 { get; set; }
    public long Order { get; set; }
}

public enum OpaqueMidiEventKind
{
    Meta,
    SystemExclusive,
    SystemExclusiveContinuation
}

public sealed class OpaqueMidiEvent
{
    public OpaqueMidiEvent(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
    }

    internal OpaqueMidiEvent(MidoraProject project, MidoraId preservedId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        Id = preservedId;
    }

    public MidoraId Id { get; init; }
    public long Tick { get; set; }
    public OpaqueMidiEventKind Kind { get; set; }
    public byte MetaType { get; set; }
    public byte[] Payload { get; set; } = [];
    public long Order { get; set; }
}

public static class ArrangementHierarchy
{
    public static IEnumerable<EventInstrument> EventInstrumentsInOrder(this MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Dictionary<MidoraId, EventInstrument> byId = project.EventInstruments
            .GroupBy(value => value.Id)
            .ToDictionary(value => value.Key, value => value.First());
        foreach (ArrangementParentReference parent in project.ArrangementParents)
        {
            if (parent.Kind == ArrangementParentKind.EventInstrument
                && byId.TryGetValue(parent.ParentId, out EventInstrument? instrument))
            {
                yield return instrument;
            }
        }
    }

    public static IEnumerable<MidiChannelRoot> MidiChannelRootsInOrder(this MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Dictionary<MidoraId, MidiChannelRoot> byId = project.MidiChannelRoots
            .GroupBy(value => value.Id)
            .ToDictionary(value => value.Key, value => value.First());
        foreach (ArrangementParentReference parent in project.ArrangementParents)
        {
            if (parent.Kind == ArrangementParentKind.MidiChannelRoot
                && byId.TryGetValue(parent.ParentId, out MidiChannelRoot? root))
            {
                yield return root;
            }
        }
    }

    public static IEnumerable<LogicalTrack> LogicalTracksInArrangementOrder(this MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Dictionary<MidoraId, LogicalTrack> tracks = project.Tracks.ToDictionary(value => value.Id);
        foreach (EventInstrument instrument in project.EventInstrumentsInOrder())
        {
            foreach (MidoraId trackId in instrument.LogicalTrackIds)
            {
                if (tracks.TryGetValue(trackId, out LogicalTrack? track))
                {
                    yield return track;
                }
            }
        }
    }

    public static IEnumerable<PureMidiTrack> PureMidiTracksInArrangementOrder(this MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Dictionary<MidoraId, PureMidiTrack> tracks = project.PureMidiTracks.ToDictionary(value => value.Id);
        foreach (MidiChannelRoot root in project.MidiChannelRootsInOrder())
        {
            foreach (MidoraId trackId in root.MidiTrackIds)
            {
                if (tracks.TryGetValue(trackId, out PureMidiTrack? track))
                {
                    yield return track;
                }
            }
        }
    }
}
