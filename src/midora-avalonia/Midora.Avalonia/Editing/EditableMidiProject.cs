using Midora.Avalonia.Import;
using Midora.Domain;

namespace Midora.Avalonia.Editing;

public sealed class EditableMidiNote
{
    public MidoraId Id { get; init; }

    public long StartTick { get; set; }

    public long EndTick { get; set; }

    public int Key { get; set; }

    public int Velocity { get; set; }

    public int NoteOffVelocity { get; set; }

    public int Channel { get; init; }
}

public sealed class EditableMidiEvent
{
    public MidoraId Id { get; init; }

    public long Tick { get; set; }

    public int Channel { get; init; }

    public ImportedMidiEventKind Kind { get; init; }

    public int Data1 { get; set; }

    public int Data2 { get; set; }
}

public sealed class EditableMidiTrack
{
    internal EditableMidiTrack(
        int trackIndex,
        string name,
        List<EditableMidiNote> notes,
        List<EditableMidiEvent> events)
    {
        TrackIndex = trackIndex;
        Name = name ?? string.Empty;
        Notes = notes ?? throw new ArgumentNullException(nameof(notes));
        Events = events ?? throw new ArgumentNullException(nameof(events));
    }

    public int TrackIndex { get; }

    public string Name { get; }

    public List<EditableMidiNote> Notes { get; }

    public List<EditableMidiEvent> Events { get; }

    public long Version { get; internal set; }
}

public sealed class EditableMidiProject
{
    public const int MaximumHistoryEntries = 512;

    private static readonly Comparison<EditableMidiNote> NoteOrder = static (left, right) =>
    {
        int value = left.StartTick.CompareTo(right.StartTick);
        if (value != 0) return value;
        value = left.Key.CompareTo(right.Key);
        return value != 0 ? value : left.Id.CompareTo(right.Id);
    };

    private static readonly Comparison<EditableMidiEvent> EventOrder = static (left, right) =>
    {
        int value = left.Tick.CompareTo(right.Tick);
        return value != 0 ? value : left.Id.CompareTo(right.Id);
    };

    private readonly List<EditableMidiTrack> _tracks;
    private readonly List<IEditCommand> _undoStack = [];
    private readonly List<IEditCommand> _redoStack = [];
    private long _nextId = 1;

    public EditableMidiProject(ImportedMidiProject source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
        _tracks = new List<EditableMidiTrack>(source.Tracks.Count);
        for (int trackIndex = 0; trackIndex < source.Tracks.Count; trackIndex++)
        {
            ImportedMidiTrack imported = source.Tracks[trackIndex];

            List<EditableMidiNote> notes = new(imported.Notes.Count);
            foreach (ImportedMidiNote note in imported.Notes)
            {
                notes.Add(new EditableMidiNote
                {
                    Id = AllocateId(),
                    StartTick = note.StartTick,
                    EndTick = note.EndTick,
                    Key = note.Key,
                    Velocity = note.Velocity,
                    NoteOffVelocity = note.NoteOffVelocity,
                    Channel = note.Channel
                });
            }
            notes.Sort(NoteOrder);

            List<EditableMidiEvent> events = new(imported.Events.Count);
            foreach (ImportedMidiEvent value in imported.Events)
            {
                events.Add(new EditableMidiEvent
                {
                    Id = AllocateId(),
                    Tick = value.Tick,
                    Channel = value.Channel,
                    Kind = value.Kind,
                    Data1 = value.Data1,
                    Data2 = value.Data2
                });
            }
            events.Sort(EventOrder);

            _tracks.Add(new EditableMidiTrack(trackIndex, imported.Name, notes, events));
        }

        Tracks = _tracks.AsReadOnly();
    }

    public ImportedMidiProject Source { get; }

    public IReadOnlyList<EditableMidiTrack> Tracks { get; }

    public int TrackCount => _tracks.Count;

    public long Version { get; private set; }

    public bool CanUndo => _undoStack.Count > 0;

    public bool CanRedo => _redoStack.Count > 0;

    public event EventHandler? Changed;

    public EditableMidiNote AddNote(
        int trackIndex,
        long startTick,
        int key,
        long lengthTicks,
        int velocity,
        int channel = 0)
    {
        EditableMidiTrack track = GetTrack(trackIndex);
        long start = Math.Clamp(startTick, 0, long.MaxValue - 1);
        long length = Math.Max(1, lengthTicks);
        EditableMidiNote note = new()
        {
            Id = AllocateId(),
            StartTick = start,
            EndTick = SafeAdd(start, length),
            Key = Math.Clamp(key, 0, 127),
            Velocity = Math.Clamp(velocity, 1, 127),
            NoteOffVelocity = 0,
            Channel = Math.Clamp(channel, 0, 15)
        };
        Execute(track, new AddNoteCommand(track, note));
        return note;
    }

    public void RemoveNotes(int trackIndex, IReadOnlyCollection<MidoraId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        EditableMidiTrack track = GetTrack(trackIndex);
        if (ids.Count == 0) return;
        HashSet<MidoraId> targets = new(ids);
        List<EditableMidiNote> removed = [];
        foreach (EditableMidiNote note in track.Notes)
        {
            if (targets.Contains(note.Id)) removed.Add(note);
        }
        if (removed.Count == 0) return;
        Execute(track, new RemoveNotesCommand(track, removed));
    }

    public void TransformNotes(
        int trackIndex,
        IReadOnlyCollection<MidoraId> ids,
        long tickDelta,
        int keyDelta)
    {
        ArgumentNullException.ThrowIfNull(ids);
        EditableMidiTrack track = GetTrack(trackIndex);
        if (ids.Count == 0 || tickDelta == 0 && keyDelta == 0) return;
        HashSet<MidoraId> targets = new(ids);
        List<NoteMutation> mutations = [];
        foreach (EditableMidiNote note in track.Notes)
        {
            if (!targets.Contains(note.Id)) continue;
            long length = note.EndTick > note.StartTick ? note.EndTick - note.StartTick : 1;
            long start = Math.Min(ClampTickAdd(note.StartTick, tickDelta), long.MaxValue - 1);
            mutations.Add(new NoteMutation(
                note,
                note.StartTick,
                note.EndTick,
                note.Key,
                start,
                SafeAdd(start, length),
                ClampKey(note.Key, keyDelta)));
        }
        if (mutations.Count == 0) return;
        Execute(track, new NoteMutationCommand(track, mutations));
    }

    public void ResizeNote(int trackIndex, MidoraId id, long newStartTick, long newEndTick)
    {
        EditableMidiTrack track = GetTrack(trackIndex);
        EditableMidiNote? note = FindNote(track, id);
        if (note is null) return;
        long start = Math.Clamp(newStartTick, 0, long.MaxValue - 1);
        long end = Math.Max(newEndTick, start + 1);
        if (note.StartTick == start && note.EndTick == end) return;
        Execute(track, new NoteMutationCommand(
            track,
            [new NoteMutation(note, note.StartTick, note.EndTick, note.Key, start, end, note.Key)]));
    }

    public void SetVelocity(int trackIndex, IReadOnlyCollection<MidoraId> ids, int velocity)
    {
        ArgumentNullException.ThrowIfNull(ids);
        EditableMidiTrack track = GetTrack(trackIndex);
        if (ids.Count == 0) return;
        int clamped = Math.Clamp(velocity, 1, 127);
        HashSet<MidoraId> targets = new(ids);
        List<NoteVelocityMutation> mutations = [];
        foreach (EditableMidiNote note in track.Notes)
        {
            if (targets.Contains(note.Id) && note.Velocity != clamped)
            {
                mutations.Add(new NoteVelocityMutation(note, note.Velocity, clamped));
            }
        }
        if (mutations.Count == 0) return;
        Execute(track, new SetVelocityCommand(track, mutations));
    }

    public bool SplitNote(int trackIndex, MidoraId id, long tick)
    {
        EditableMidiTrack track = GetTrack(trackIndex);
        EditableMidiNote? note = FindNote(track, id);
        if (note is null || tick <= note.StartTick || tick >= note.EndTick) return false;
        EditableMidiNote tail = new()
        {
            Id = AllocateId(),
            StartTick = tick,
            EndTick = note.EndTick,
            Key = note.Key,
            Velocity = note.Velocity,
            NoteOffVelocity = note.NoteOffVelocity,
            Channel = note.Channel
        };
        Execute(track, new SplitNoteCommand(track, note, note.EndTick, tick, tail));
        return true;
    }

    public void SetEventValue(int trackIndex, MidoraId id, int data2)
    {
        EditableMidiTrack track = GetTrack(trackIndex);
        EditableMidiEvent? value = FindEvent(track, id);
        if (value is null) return;
        int clamped = Math.Clamp(data2, 0, 127);
        if (value.Data2 == clamped) return;
        Execute(track, new SetEventValueCommand(track, value, value.Data2, clamped));
    }

    public void RemoveEvents(int trackIndex, IReadOnlyCollection<MidoraId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        EditableMidiTrack track = GetTrack(trackIndex);
        if (ids.Count == 0) return;
        HashSet<MidoraId> targets = new(ids);
        List<EditableMidiEvent> removed = [];
        foreach (EditableMidiEvent value in track.Events)
        {
            if (targets.Contains(value.Id)) removed.Add(value);
        }
        if (removed.Count == 0) return;
        Execute(track, new RemoveEventsCommand(track, removed));
    }

    public void Undo()
    {
        if (_undoStack.Count == 0) return;
        IEditCommand command = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        command.Undo();
        _redoStack.Add(command);
        NotifyChanged(command.Track);
    }

    public void Redo()
    {
        if (_redoStack.Count == 0) return;
        IEditCommand command = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        command.Redo();
        _undoStack.Add(command);
        NotifyChanged(command.Track);
    }

    private void Execute(EditableMidiTrack track, IEditCommand command)
    {
        command.Redo();
        _undoStack.Add(command);
        if (_undoStack.Count > MaximumHistoryEntries) _undoStack.RemoveAt(0);
        _redoStack.Clear();
        NotifyChanged(track);
    }

    private void NotifyChanged(EditableMidiTrack track)
    {
        track.Version++;
        Version++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private MidoraId AllocateId()
    {
        long value = _nextId;
        _nextId = checked(value + 1);
        return new MidoraId(value);
    }

    private EditableMidiTrack GetTrack(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= _tracks.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(trackIndex));
        }
        return _tracks[trackIndex];
    }

    private static void SortNotes(EditableMidiTrack track) => track.Notes.Sort(NoteOrder);

    private static void SortEvents(EditableMidiTrack track) => track.Events.Sort(EventOrder);

    private static EditableMidiNote? FindNote(EditableMidiTrack track, MidoraId id)
    {
        foreach (EditableMidiNote note in track.Notes)
        {
            if (note.Id == id) return note;
        }
        return null;
    }

    private static EditableMidiEvent? FindEvent(EditableMidiTrack track, MidoraId id)
    {
        foreach (EditableMidiEvent value in track.Events)
        {
            if (value.Id == id) return value;
        }
        return null;
    }

    private static long SafeAdd(long start, long length)
    {
        if (length <= 0) return start;
        return start > long.MaxValue - length ? long.MaxValue : start + length;
    }

    private static long ClampTickAdd(long tick, long delta)
    {
        Int128 value = (Int128)tick + delta;
        if (value < 0) return 0;
        return value > long.MaxValue ? long.MaxValue : (long)value;
    }

    private static int ClampKey(long key, int keyDelta)
    {
        Int128 value = key + (Int128)keyDelta;
        if (value < 0) return 0;
        return value > 127 ? 127 : (int)value;
    }

    private interface IEditCommand
    {
        EditableMidiTrack Track { get; }

        void Undo();

        void Redo();
    }

    private readonly record struct NoteMutation(
        EditableMidiNote Note,
        long OldStartTick,
        long OldEndTick,
        int OldKey,
        long NewStartTick,
        long NewEndTick,
        int NewKey);

    private readonly record struct NoteVelocityMutation(
        EditableMidiNote Note,
        int OldVelocity,
        int NewVelocity);

    private sealed class AddNoteCommand : IEditCommand
    {
        private readonly EditableMidiNote _note;

        public AddNoteCommand(EditableMidiTrack track, EditableMidiNote note)
        {
            Track = track;
            _note = note;
        }

        public EditableMidiTrack Track { get; }

        public void Redo()
        {
            Track.Notes.Add(_note);
            SortNotes(Track);
        }

        public void Undo() => Track.Notes.Remove(_note);
    }

    private sealed class RemoveNotesCommand : IEditCommand
    {
        private readonly EditableMidiNote[] _notes;

        public RemoveNotesCommand(EditableMidiTrack track, List<EditableMidiNote> notes)
        {
            Track = track;
            _notes = [.. notes];
        }

        public EditableMidiTrack Track { get; }

        public void Redo()
        {
            foreach (EditableMidiNote note in _notes) Track.Notes.Remove(note);
        }

        public void Undo()
        {
            Track.Notes.AddRange(_notes);
            SortNotes(Track);
        }
    }

    private sealed class NoteMutationCommand : IEditCommand
    {
        private readonly NoteMutation[] _mutations;

        public NoteMutationCommand(EditableMidiTrack track, List<NoteMutation> mutations)
        {
            Track = track;
            _mutations = [.. mutations];
        }

        public EditableMidiTrack Track { get; }

        public void Redo()
        {
            foreach (NoteMutation mutation in _mutations)
            {
                mutation.Note.StartTick = mutation.NewStartTick;
                mutation.Note.EndTick = mutation.NewEndTick;
                mutation.Note.Key = mutation.NewKey;
            }
            SortNotes(Track);
        }

        public void Undo()
        {
            foreach (NoteMutation mutation in _mutations)
            {
                mutation.Note.StartTick = mutation.OldStartTick;
                mutation.Note.EndTick = mutation.OldEndTick;
                mutation.Note.Key = mutation.OldKey;
            }
            SortNotes(Track);
        }
    }

    private sealed class SetVelocityCommand : IEditCommand
    {
        private readonly NoteVelocityMutation[] _mutations;

        public SetVelocityCommand(EditableMidiTrack track, List<NoteVelocityMutation> mutations)
        {
            Track = track;
            _mutations = [.. mutations];
        }

        public EditableMidiTrack Track { get; }

        public void Redo()
        {
            foreach (NoteVelocityMutation mutation in _mutations)
            {
                mutation.Note.Velocity = mutation.NewVelocity;
            }
        }

        public void Undo()
        {
            foreach (NoteVelocityMutation mutation in _mutations)
            {
                mutation.Note.Velocity = mutation.OldVelocity;
            }
        }
    }

    private sealed class SplitNoteCommand : IEditCommand
    {
        private readonly EditableMidiNote _note;
        private readonly EditableMidiNote _tail;
        private readonly long _originalEndTick;
        private readonly long _splitTick;

        public SplitNoteCommand(
            EditableMidiTrack track,
            EditableMidiNote note,
            long originalEndTick,
            long splitTick,
            EditableMidiNote tail)
        {
            Track = track;
            _note = note;
            _tail = tail;
            _originalEndTick = originalEndTick;
            _splitTick = splitTick;
        }

        public EditableMidiTrack Track { get; }

        public void Redo()
        {
            _note.EndTick = _splitTick;
            Track.Notes.Add(_tail);
            SortNotes(Track);
        }

        public void Undo()
        {
            _note.EndTick = _originalEndTick;
            Track.Notes.Remove(_tail);
            SortNotes(Track);
        }
    }

    private sealed class SetEventValueCommand : IEditCommand
    {
        private readonly EditableMidiEvent _event;
        private readonly int _oldData2;
        private readonly int _newData2;

        public SetEventValueCommand(
            EditableMidiTrack track,
            EditableMidiEvent value,
            int oldData2,
            int newData2)
        {
            Track = track;
            _event = value;
            _oldData2 = oldData2;
            _newData2 = newData2;
        }

        public EditableMidiTrack Track { get; }

        public void Redo() => _event.Data2 = _newData2;

        public void Undo() => _event.Data2 = _oldData2;
    }

    private sealed class RemoveEventsCommand : IEditCommand
    {
        private readonly EditableMidiEvent[] _events;

        public RemoveEventsCommand(EditableMidiTrack track, List<EditableMidiEvent> events)
        {
            Track = track;
            _events = [.. events];
        }

        public EditableMidiTrack Track { get; }

        public void Redo()
        {
            foreach (EditableMidiEvent value in _events) Track.Events.Remove(value);
        }

        public void Undo()
        {
            Track.Events.AddRange(_events);
            SortEvents(Track);
        }
    }
}
