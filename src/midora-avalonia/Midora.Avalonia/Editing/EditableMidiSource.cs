using Midora.Avalonia.Import;
using Midora.Avalonia.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Avalonia.Editing;

public sealed class EditableMidiSource :
    ITimelineRenderItemSource,
    INonBlockingTimelineFingerprintSource
{
    private readonly EditableMidiProject _project;
    private readonly int _trackIndex;

    public EditableMidiSource(EditableMidiProject project, int trackIndex)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (trackIndex < 0 || trackIndex >= project.TrackCount)
        {
            throw new ArgumentOutOfRangeException(nameof(trackIndex));
        }
        _project = project;
        _trackIndex = trackIndex;
    }

    public EditableMidiProject Project => _project;

    public int TrackIndex => _trackIndex;

    public bool CanComputeRangeFingerprintWithoutBlocking => true;

    public long Count
    {
        get
        {
            EditableMidiTrack track = _project.Tracks[_trackIndex];
            return track.Notes.Count + track.Events.Count;
        }
    }

    public long MaximumEndTick => ComputeMaximumEndTick();

    public ulong ContentFingerprint => ComputeContentFingerprint();

    public void QueryInto(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        List<TimelineRenderItem> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        long effectiveStart = Math.Max(0, startTick);
        if (endTick <= effectiveStart || firstLane >= lastLaneExclusive) return;
        foreach (TimelineRenderItem item in BuildItems())
        {
            if (item.Lane < firstLane || item.Lane >= lastLaneExclusive) continue;
            if (item.StartTick < endTick && item.EndTick > effectiveStart) destination.Add(item);
        }
    }

    public ulong GetRangeFingerprint(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive)
    {
        ulong range = TimelineContentFingerprint.Combine(
            unchecked((ulong)startTick),
            unchecked((ulong)endTick));
        range = TimelineContentFingerprint.Combine(
            range,
            TimelineContentFingerprint.Combine(
                unchecked((ulong)firstLane),
                unchecked((ulong)lastLaneExclusive)));
        return TimelineContentFingerprint.Combine(ContentFingerprint, range);
    }

    public bool TryGetById(MidoraId id, out TimelineRenderItem item)
    {
        foreach (TimelineRenderItem candidate in BuildItems())
        {
            if (candidate.Id == id)
            {
                item = candidate;
                return true;
            }
        }
        item = default;
        return false;
    }

    public IEnumerable<TimelineRenderItem> EnumerateAll() => BuildItems();

    public void AccumulateOverviewDensity(long extent, Span<int> destination)
    {
        if (extent <= 0 || destination.IsEmpty) return;
        foreach (TimelineRenderItem item in BuildItems())
        {
            int column = (int)Math.Clamp(
                Math.Floor(item.StartTick / (double)extent * destination.Length),
                0,
                destination.Length - 1);
            if (destination[column] != int.MaxValue) destination[column]++;
        }
    }

    private ulong ComputeContentFingerprint()
    {
        return TimelineContentFingerprint.Combine(
            TimelineContentFingerprint.ForRenderItems(BuildItems()),
            unchecked((ulong)_project.Version));
    }

    private long ComputeMaximumEndTick()
    {
        long maximum = 0;
        foreach (EditableMidiTrack track in _project.Tracks)
        {
            foreach (EditableMidiNote note in track.Notes)
            {
                if (note.EndTick > maximum) maximum = note.EndTick;
            }
            foreach (EditableMidiEvent value in track.Events)
            {
                long tick = Math.Max(0, value.Tick);
                long endTick = tick == long.MaxValue ? long.MaxValue : tick + 1;
                if (endTick > maximum) maximum = endTick;
            }
        }
        return Math.Max(1, maximum);
    }

    private TimelineRenderItem[] BuildItems()
    {
        EditableMidiTrack track = _project.Tracks[_trackIndex];
        List<TimelineRenderItem> items = new(track.Notes.Count + track.Events.Count);
        foreach (EditableMidiNote note in track.Notes)
        {
            long endTick = note.EndTick > note.StartTick
                ? note.EndTick
                : note.StartTick == long.MaxValue
                    ? long.MaxValue
                    : note.StartTick + 1;
            bool invalid = note.Key is < 0 or > 127;
            items.Add(new TimelineRenderItem(
                note.Id,
                TimelineItemKind.DirectMidiNote,
                note.StartTick,
                endTick,
                Math.Clamp(note.Key, 0, 127),
                note.Velocity,
                1,
                invalid ? TimelineItemState.Invalid : TimelineItemState.None)
            {
                SecondaryValue = note.NoteOffVelocity,
                Label = string.Empty
            });
        }
        foreach (EditableMidiEvent value in track.Events)
        {
            (double primary, double secondary) = MapEventData(value);
            long startTick = Math.Max(0, value.Tick);
            long eventEndTick = startTick == long.MaxValue ? long.MaxValue : startTick + 1;
            items.Add(new TimelineRenderItem(
                value.Id,
                TimelineItemKind.DirectMidiEvent,
                startTick,
                eventEndTick,
                Math.Clamp(value.Channel, 0, 15),
                primary,
                1,
                TimelineItemState.None)
            {
                SecondaryValue = secondary,
                Label = string.Empty
            });
        }

        TimelineRenderItem[] materialized = [.. items];
        Array.Sort(materialized, static (left, right) =>
        {
            int value = left.Lane.CompareTo(right.Lane);
            if (value != 0) return value;
            value = left.StartTick.CompareTo(right.StartTick);
            if (value != 0) return value;
            value = left.EndTick.CompareTo(right.EndTick);
            if (value != 0) return value;
            value = left.Kind.CompareTo(right.Kind);
            return value != 0 ? value : left.Id.CompareTo(right.Id);
        });
        return materialized;
    }

    private static (double Primary, double Secondary) MapEventData(EditableMidiEvent value)
    {
        return value.Kind switch
        {
            ImportedMidiEventKind.ProgramChange => (value.Data1, value.Data2),
            ImportedMidiEventKind.AfterTouch => (value.Data1, value.Data2),
            ImportedMidiEventKind.PitchBend => (value.Data1 | (value.Data2 << 7), value.Data1),
            _ => (value.Data2, value.Data1)
        };
    }
}
