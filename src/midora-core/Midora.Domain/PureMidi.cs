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
    private readonly DirectMidiNoteCollection _notes;
    private readonly DirectMidiChannelEventCollection _channelEvents;
    private readonly OpaqueMidiEventCollection _opaqueEvents;
    private IPureMidiSegmentContentSource? _pagedContentSource;

    public MidiSegment(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
        _notes = new(project);
        _channelEvents = new(project);
        _opaqueEvents = new(project);
    }

    internal MidiSegment(MidoraProject project, MidoraId preservedId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        Id = preservedId;
        _notes = new(project);
        _channelEvents = new(project);
        _opaqueEvents = new(project);
    }

    public MidoraId Id { get; init; }
    public long ProjectStartTick { get; set; }
    public long LengthTicks { get; set; }
    public long ContentOffsetTick { get; set; }
    public DirectMidiNoteCollection Notes => _notes;
    public DirectMidiChannelEventCollection ChannelEvents => _channelEvents;
    public OpaqueMidiEventCollection OpaqueEvents => _opaqueEvents;

    public TickRange ProjectRange => new(ProjectStartTick, checked(ProjectStartTick + LengthTicks));
    public long ContentEndTick => checked(ContentOffsetTick + LengthTicks);

    public bool UsesPagedContent => _notes.HasPagedSource
        || _channelEvents.HasPagedSource
        || _opaqueEvents.HasPagedSource;

    public void AttachPagedContent(IPureMidiSegmentContentSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_notes.Count != 0 || _channelEvents.Count != 0 || _opaqueEvents.Count != 0)
        {
            throw new InvalidOperationException(
                "Paged MIDI content can only be attached before editable records are added.");
        }

        _pagedContentSource = source;
        _notes.AttachSource(source);
        _channelEvents.AttachSource(source);
        _opaqueEvents.AttachSource(source);
    }

    public string? PagedContentFingerprint => _pagedContentSource?.ContentFingerprint;

    internal PureMidiContentPack? TryGetPristineContentPack()
    {
        if (!_notes.IsPristinePagedSource
            || !_channelEvents.IsPristinePagedSource
            || !_opaqueEvents.IsPristinePagedSource
            || !ReferenceEquals(_notes.PagedSource, _channelEvents.PagedSource)
            || !ReferenceEquals(_notes.PagedSource, _opaqueEvents.PagedSource))
        {
            return null;
        }

        return (_pagedContentSource as IPureMidiContentPackSegmentSource)?.Owner;
    }

    internal void CloneContentTo(MidiSegment target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        target._pagedContentSource = _pagedContentSource;
        _notes.CloneTo(target._notes, cancellationToken);
        _channelEvents.CloneTo(target._channelEvents, cancellationToken);
        _opaqueEvents.CloneTo(target._opaqueEvents, cancellationToken);
    }
}

public sealed class DirectMidiNote
{
    private IDirectMidiNoteChangeSink? _changeSink;
    private long _startTick;
    private long _lengthTicks;
    private int _key = 60;
    private int _noteOnVelocity = 100;
    private int _noteOffVelocity;
    private long _noteOnOrder;
    private long _noteOffOrder;

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
    public long StartTick { get => _startTick; set { _startTick = value; Changed(); } }
    public long LengthTicks { get => _lengthTicks; set { _lengthTicks = value; Changed(); } }
    public int Key { get => _key; set { _key = value; Changed(); } }
    public int NoteOnVelocity { get => _noteOnVelocity; set { _noteOnVelocity = value; Changed(); } }
    public int NoteOffVelocity { get => _noteOffVelocity; set { _noteOffVelocity = value; Changed(); } }
    public long NoteOnOrder { get => _noteOnOrder; set { _noteOnOrder = value; Changed(); } }
    public long NoteOffOrder { get => _noteOffOrder; set { _noteOffOrder = value; Changed(); } }

    internal void SetChangeSink(IDirectMidiNoteChangeSink? value) => _changeSink = value;

    private void Changed() => _changeSink?.OnChanged(this);
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
    private IDirectMidiChannelEventChangeSink? _changeSink;
    private long _tick;
    private DirectMidiChannelEventKind _kind;
    private int _data1;
    private int _data2;
    private long _order;

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
    public long Tick { get => _tick; set { _tick = value; Changed(); } }
    public DirectMidiChannelEventKind Kind { get => _kind; set { _kind = value; Changed(); } }
    public int Data1 { get => _data1; set { _data1 = value; Changed(); } }
    public int Data2 { get => _data2; set { _data2 = value; Changed(); } }
    public long Order { get => _order; set { _order = value; Changed(); } }

    internal void SetChangeSink(IDirectMidiChannelEventChangeSink? value) => _changeSink = value;

    private void Changed() => _changeSink?.OnChanged(this);
}

public enum OpaqueMidiEventKind
{
    Meta,
    SystemExclusive,
    SystemExclusiveContinuation
}

public sealed class OpaqueMidiEvent
{
    private IOpaqueMidiEventChangeSink? _changeSink;
    private long _tick;
    private OpaqueMidiEventKind _kind;
    private byte _metaType;
    private byte[] _payload = [];
    private long _order;

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
    public long Tick { get => _tick; set { _tick = value; Changed(); } }
    public OpaqueMidiEventKind Kind { get => _kind; set { _kind = value; Changed(); } }
    public byte MetaType { get => _metaType; set { _metaType = value; Changed(); } }
    public byte[] Payload { get => _payload; set { _payload = value ?? throw new ArgumentNullException(nameof(value)); Changed(); } }
    public long Order { get => _order; set { _order = value; Changed(); } }

    internal void SetChangeSink(IOpaqueMidiEventChangeSink? value) => _changeSink = value;

    private void Changed() => _changeSink?.OnChanged(this);
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
