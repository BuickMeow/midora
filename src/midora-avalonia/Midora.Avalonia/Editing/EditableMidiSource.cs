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
    private TimelineRenderItem[]? _items;
    private int[][]? _laneItems;
    private long[][]? _laneMaximumEndPrefix;
    private ulong _fingerprint;
    private long _maximumEndTick;
    private long _builtProjectVersion = -1;
    private long _builtTrackVersion = -1;

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

    public long MaximumEndTick
    {
        get
        {
            EnsureBuilt();
            return _maximumEndTick;
        }
    }

    public ulong ContentFingerprint
    {
        get
        {
            EnsureBuilt();
            return _fingerprint;
        }
    }

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
        EnsureBuilt();
        int[][] lanes = _laneItems!;
        long[][] prefixes = _laneMaximumEndPrefix!;
        int first = Math.Max(0, firstLane);
        int last = Math.Min(lastLaneExclusive, lanes.Length);
        for (int lane = first; lane < last; lane++)
        {
            int[] laneItems = lanes[lane];
            if (laneItems.Length == 0)
            {
                continue;
            }

            long[] prefix = prefixes[lane];
            int index = FirstPrefixEndGreaterThan(prefix, effectiveStart);
            for (; index < laneItems.Length; index++)
            {
                TimelineRenderItem item = _items![laneItems[index]];
                if (item.StartTick >= endTick)
                {
                    break;
                }

                if (item.EndTick > effectiveStart)
                {
                    destination.Add(item);
                }
            }
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
        EnsureBuilt();
        foreach (TimelineRenderItem candidate in _items!)
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

    public IEnumerable<TimelineRenderItem> EnumerateAll()
    {
        EnsureBuilt();
        return _items!;
    }

    public void AccumulateOverviewDensity(long extent, Span<int> destination)
    {
        if (extent <= 0 || destination.IsEmpty) return;
        EnsureBuilt();
        foreach (TimelineRenderItem item in _items!)
        {
            int column = (int)Math.Clamp(
                Math.Floor(item.StartTick / (double)extent * destination.Length),
                0,
                destination.Length - 1);
            if (destination[column] != int.MaxValue) destination[column]++;
        }
    }

    /// <summary>
    /// Rebuilds the render items, the per-lane index and the fingerprint only when the track or the
    /// project changed. Every frame used to rebuild and sort the whole track (200k items for a large
    /// Pure MIDI track) on each query, which dominated both the ruler and the piano roll frame.
    /// </summary>
    private void EnsureBuilt()
    {
        EditableMidiTrack track = _project.Tracks[_trackIndex];
        long projectVersion = _project.Version;
        long trackVersion = track.Version;
        if (_items is not null
            && _builtProjectVersion == projectVersion
            && _builtTrackVersion == trackVersion)
        {
            return;
        }

        TimelineRenderItem[] items = BuildItems(track);
        _items = items;
        _fingerprint = TimelineContentFingerprint.Combine(
            TimelineContentFingerprint.ForRenderItems(items),
            unchecked((ulong)projectVersion));
        _maximumEndTick = ComputeMaximumEndTick();
        BuildLaneIndex(items);
        _builtProjectVersion = projectVersion;
        _builtTrackVersion = trackVersion;
    }

    private void BuildLaneIndex(TimelineRenderItem[] items)
    {
        int laneCount = 0;
        foreach (TimelineRenderItem item in items)
        {
            if (item.Lane >= laneCount)
            {
                laneCount = item.Lane + 1;
            }
        }

        laneCount = Math.Max(1, laneCount);
        int[] counts = new int[laneCount];
        foreach (TimelineRenderItem item in items)
        {
            int lane = item.Lane < 0 ? 0 : item.Lane;
            counts[lane < laneCount ? lane : laneCount - 1]++;
        }

        int[][] lanes = new int[laneCount][];
        long[][] prefixes = new long[laneCount][];
        for (int lane = 0; lane < laneCount; lane++)
        {
            lanes[lane] = new int[counts[lane]];
            prefixes[lane] = new long[counts[lane]];
        }

        int[] fill = new int[laneCount];
        for (int index = 0; index < items.Length; index++)
        {
            int lane = items[index].Lane < 0 ? 0 : items[index].Lane;
            lane = lane < laneCount ? lane : laneCount - 1;
            lanes[lane][fill[lane]++] = index;
        }

        for (int lane = 0; lane < laneCount; lane++)
        {
            int[] laneItems = lanes[lane];
            long maximumEnd = long.MinValue;
            for (int index = 0; index < laneItems.Length; index++)
            {
                maximumEnd = Math.Max(maximumEnd, items[laneItems[index]].EndTick);
                prefixes[lane][index] = maximumEnd;
            }
        }

        _laneItems = lanes;
        _laneMaximumEndPrefix = prefixes;
    }

    private static int FirstPrefixEndGreaterThan(long[] prefix, long value)
    {
        int low = 0;
        int high = prefix.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (prefix[middle] > value)
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        return low;
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

    private TimelineRenderItem[] BuildItems(EditableMidiTrack track)
    {
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
