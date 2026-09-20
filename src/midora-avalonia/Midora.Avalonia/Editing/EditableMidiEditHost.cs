using Midora.Avalonia.Presentation.Controls;
using Midora.Avalonia.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Avalonia.Editing;

public sealed class EditableMidiEditHost : ITimelineEditHost
{
    private readonly EditableMidiProject _project;
    private readonly EditableMidiSource _source;
    private readonly int _trackIndex;
    private readonly long _ticksPerQuarterNote;
    private Dictionary<MidoraId, EditableMidiNote>? _notesById;
    private Dictionary<MidoraId, EditableMidiEvent>? _eventsById;
    private long _lookupProjectVersion = -1;
    private long _lookupTrackVersion = -1;

    public EditableMidiEditHost(
        EditableMidiProject project,
        int trackIndex,
        long ticksPerQuarterNote)
        : this(project, new EditableMidiSource(project, trackIndex), ticksPerQuarterNote)
    {
    }

    public EditableMidiEditHost(
        EditableMidiProject project,
        EditableMidiSource source,
        long ticksPerQuarterNote)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(source);
        if (!ReferenceEquals(project, source.Project))
        {
            throw new ArgumentException(
                "The edit source must belong to the edited project.",
                nameof(source));
        }

        _project = project;
        _source = source;
        _trackIndex = source.TrackIndex;
        _ticksPerQuarterNote = Math.Max(1, ticksPerQuarterNote);
    }

    public EditableMidiProject Project => _project;

    public EditableMidiSource Source => _source;

    public int TrackIndex => _trackIndex;

    public bool CanEdit => true;

    public long DefaultNoteLengthTicks => Math.Max(1, _ticksPerQuarterNote / 4);

    public TimelineRenderItem CreateNote(int lane, long startTick, long lengthTicks)
    {
        EditableMidiNote note = _project.AddNote(
            _trackIndex,
            startTick,
            lane,
            lengthTicks,
            velocity: 100,
            channel: 0);
        if (_source.TryGetById(note.Id, out TimelineRenderItem item))
        {
            return item;
        }

        return new TimelineRenderItem(
            note.Id,
            TimelineItemKind.DirectMidiNote,
            note.StartTick,
            note.EndTick,
            Math.Clamp(note.Key, 0, 127),
            note.Velocity,
            1,
            TimelineItemState.None)
        {
            SecondaryValue = note.NoteOffVelocity
        };
    }

    public void MoveItems(IReadOnlyList<TimelineRenderItem> items, long tickDelta, int laneDelta)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0 || tickDelta == 0 && laneDelta == 0)
        {
            return;
        }

        EnsureLookups();
        List<MidoraId>? noteIds = null;
        List<MidoraId>? eventIds = null;
        foreach (TimelineRenderItem item in items)
        {
            if (_notesById!.ContainsKey(item.Id))
            {
                (noteIds ??= []).Add(item.Id);
            }
            else if (_eventsById!.ContainsKey(item.Id))
            {
                (eventIds ??= []).Add(item.Id);
            }
        }

        if (noteIds is not null)
        {
            _project.TransformNotes(_trackIndex, noteIds, tickDelta, laneDelta);
        }

        if (eventIds is not null)
        {
            foreach (MidoraId id in eventIds)
            {
                EditableMidiEvent value = _eventsById![id];
                value.Tick = ClampTickAdd(value.Tick, tickDelta);
            }
        }
    }

    public void ResizeItem(TimelineRenderItem item, long newStartTick, long newEndTick) =>
        _project.ResizeNote(_trackIndex, item.Id, newStartTick, newEndTick);

    public void EraseItems(IReadOnlyList<TimelineRenderItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return;
        }

        EnsureLookups();
        List<MidoraId>? noteIds = null;
        List<MidoraId>? eventIds = null;
        foreach (TimelineRenderItem item in items)
        {
            if (_notesById!.ContainsKey(item.Id))
            {
                (noteIds ??= []).Add(item.Id);
            }
            else if (_eventsById!.ContainsKey(item.Id))
            {
                (eventIds ??= []).Add(item.Id);
            }
        }

        if (noteIds is not null)
        {
            _project.RemoveNotes(_trackIndex, noteIds);
        }

        if (eventIds is not null)
        {
            _project.RemoveEvents(_trackIndex, eventIds);
        }
    }

    public bool SplitItem(TimelineRenderItem item, long tick) =>
        _project.SplitNote(_trackIndex, item.Id, tick);

    public void SetVelocity(IReadOnlyList<TimelineRenderItem> items, double velocity)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0 || !double.IsFinite(velocity))
        {
            return;
        }

        EnsureLookups();
        List<MidoraId>? noteIds = null;
        foreach (TimelineRenderItem item in items)
        {
            if (_notesById!.ContainsKey(item.Id))
            {
                (noteIds ??= []).Add(item.Id);
            }
        }

        if (noteIds is not null)
        {
            _project.SetVelocity(_trackIndex, noteIds, RoundToInt(velocity));
        }
    }

    public void SetEventValue(TimelineRenderItem item, double value)
    {
        if (!double.IsFinite(value))
        {
            return;
        }

        EnsureLookups();
        if (_eventsById!.ContainsKey(item.Id))
        {
            _project.SetEventValue(_trackIndex, item.Id, RoundToInt(value));
        }
    }

    public void BeginEditTransaction()
    {
    }

    public void EndEditTransaction()
    {
    }

    private void EnsureLookups()
    {
        EditableMidiTrack track = _project.Tracks[_trackIndex];
        if (_notesById is not null
            && _eventsById is not null
            && _lookupProjectVersion == _project.Version
            && _lookupTrackVersion == track.Version)
        {
            return;
        }

        Dictionary<MidoraId, EditableMidiNote> notes = new(track.Notes.Count);
        foreach (EditableMidiNote note in track.Notes)
        {
            notes[note.Id] = note;
        }

        Dictionary<MidoraId, EditableMidiEvent> events = new(track.Events.Count);
        foreach (EditableMidiEvent value in track.Events)
        {
            events[value.Id] = value;
        }

        _notesById = notes;
        _eventsById = events;
        _lookupProjectVersion = _project.Version;
        _lookupTrackVersion = track.Version;
    }

    private static int RoundToInt(double value) =>
        (int)Math.Round(Math.Clamp(value, 0d, 127d), MidpointRounding.AwayFromZero);

    private static long ClampTickAdd(long tick, long delta)
    {
        Int128 value = (Int128)tick + delta;
        if (value < 0) return 0;
        return value > long.MaxValue ? long.MaxValue : (long)value;
    }
}
