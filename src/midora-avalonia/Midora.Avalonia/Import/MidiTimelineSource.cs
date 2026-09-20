using System.Collections.Concurrent;
using System.Globalization;
using Midora.Avalonia.Editing;
using Midora.Avalonia.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Avalonia.Import;

public sealed class MidiTimelineSource :
    ITimelineRenderItemSource,
    INonBlockingTimelineFingerprintSource,
    ITimelineSegmentPreviewSource
{
    public const int DefaultBarsPerSegment = 8;
    public const int BeatsPerBar = 4;

    private const long LaneObjectIdBase = 1L << 48;
    private const long TrackNoteIdBase = 1L;
    private const long TrackEventIdBase = 1L << 40;
    private const double MinimumTempo = 20d;
    private const double MaximumTempo = 300d;
    private const uint TimeSignatureAccentColor = 0xff62a6f6;
    private const uint KeySignatureAccentColor = 0xffaf7ac5;
    private const uint MarkerAccentColor = 0xffe8b34b;

    private static readonly uint[] TrackAccentColors =
    [
        0xff62a6f6,
        0xff58c487,
        0xffe8b34b,
        0xffaf7ac5,
        0xffe57373,
        0xff4dd0e1,
        0xffba68c8,
        0xffffb74d
    ];

    private readonly ImportedMidiProject _project;
    private readonly EditableMidiProject? _liveProject;
    private readonly long _segmentTicks;
    private readonly long _timelineTicks;
    private readonly int _segmentCount;
    private readonly long _segmentIdBase;
    private readonly TimelineRenderItem[] _items;
    private readonly TimelineRenderItem[] _conductorItems;
    private readonly Dictionary<MidoraId, TimelineRenderItem> _itemsById;
    private readonly LaneBucket?[] _bucketsByLane;
    private readonly ArrangementLaneDescriptor[] _laneDescriptors;
    private readonly string[] _trackNames;
    private readonly SegmentPreviewSource _overviewPreview;
    private readonly ulong _contentFingerprint;
    private ConcurrentDictionary<MidoraId, SegmentPreviewSource> _segmentPreviews = [];
    private long _segmentPreviewsVersion = long.MinValue;
    private SegmentPreviewSource? _liveOverviewPreview;
    private long _liveOverviewVersion = long.MinValue;
    private readonly ConcurrentDictionary<int, TrackTimelineSource> _trackSources = [];

    public MidiTimelineSource(
        ImportedMidiProject project,
        int? previewTrackIndex = null,
        int barsPerSegment = DefaultBarsPerSegment,
        EditableMidiProject? liveProject = null)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        ArgumentOutOfRangeException.ThrowIfLessThan(project.TicksPerQuarterNote, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(barsPerSegment, 1);
        if (previewTrackIndex is { } requested
            && (requested < 0 || requested >= project.Tracks.Count))
        {
            throw new ArgumentOutOfRangeException(nameof(previewTrackIndex));
        }
        if (liveProject is { } live && live.TrackCount != project.Tracks.Count)
        {
            throw new ArgumentException(
                "A live project must expose the same tracks as its imported source.",
                nameof(liveProject));
        }
        _liveProject = liveProject;

        BarsPerSegment = barsPerSegment;
        _segmentTicks = checked((long)barsPerSegment * BeatsPerBar * project.TicksPerQuarterNote);
        long contentEndTick = Math.Max(0L, project.MaximumEndTick);
        long segmentCount = contentEndTick <= 0
            ? 1
            : ((contentEndTick - 1) / _segmentTicks) + 1;
        _segmentCount = checked((int)segmentCount);
        _timelineTicks = checked((long)_segmentCount * _segmentTicks);
        PreviewTrackIndex = previewTrackIndex ?? FindFirstContentTrack(project);

        ImportedMidiConductorEvent[] orderedConductor = project.Conductor
            .Select(static (value, index) => (Value: value, Index: index))
            .OrderBy(static pair => pair.Value.Tick)
            .ThenBy(static pair => pair.Index)
            .Select(static pair => pair.Value)
            .ToArray();

        List<TimelineRenderItem> items = [];
        Dictionary<MidoraId, TimelineRenderItem> itemsById = [];
        _conductorItems = new TimelineRenderItem[orderedConductor.Length];
        for (int index = 0; index < orderedConductor.Length; index++)
        {
            TimelineRenderItem item = CreateConductorItem(orderedConductor[index], new MidoraId(index + 1));
            _conductorItems[index] = item;
            items.Add(item);
            itemsById.Add(item.Id, item);
        }

        _segmentIdBase = orderedConductor.Length + 1L;
        long nextSegmentId = _segmentIdBase;
        for (int trackIndex = 0; trackIndex < project.Tracks.Count; trackIndex++)
        {
            string trackName = TrackDisplayName(project.Tracks[trackIndex], trackIndex);
            uint accentColor = TrackAccentColors[trackIndex % TrackAccentColors.Length];
            for (int chunk = 0; chunk < _segmentCount; chunk++)
            {
                (long startTick, long endTick) = SegmentRange(chunk);
                TimelineRenderItem item = new(
                    new MidoraId(nextSegmentId++),
                    TimelineItemKind.Segment,
                    startTick,
                    endTick,
                    trackIndex + 1,
                    1,
                    0,
                    TimelineItemState.None)
                {
                    Label = trackName,
                    AccentColor = accentColor
                };
                items.Add(item);
                itemsById.Add(item.Id, item);
            }
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

        long maximumEndTick = 0;
        int maximumLane = 0;
        foreach (TimelineRenderItem item in materialized)
        {
            maximumEndTick = Math.Max(maximumEndTick, item.EndTick);
            maximumLane = Math.Max(maximumLane, item.Lane);
        }

        LaneBucket?[] buckets = new LaneBucket?[maximumLane + 1];
        int itemIndex = 0;
        while (itemIndex < materialized.Length)
        {
            int first = itemIndex;
            int lane = materialized[first].Lane;
            while (itemIndex < materialized.Length && materialized[itemIndex].Lane == lane)
            {
                itemIndex++;
            }
            buckets[lane] = new LaneBucket(materialized[first..itemIndex]);
        }

        _trackNames = new string[project.Tracks.Count + 1];
        _trackNames[0] = "Conductor";
        for (int trackIndex = 0; trackIndex < project.Tracks.Count; trackIndex++)
        {
            _trackNames[trackIndex + 1] = TrackDisplayName(project.Tracks[trackIndex], trackIndex);
        }

        _laneDescriptors = new ArrangementLaneDescriptor[project.Tracks.Count + 1];
        _laneDescriptors[0] = new(
            0,
            ArrangementLaneKind.Conductor,
            new MidoraId(LaneObjectIdBase),
            null,
            0,
            IsExpanded: true,
            HasChildren: false,
            CanContainSegments: false);
        for (int trackIndex = 0; trackIndex < project.Tracks.Count; trackIndex++)
        {
            _laneDescriptors[trackIndex + 1] = new(
                trackIndex + 1,
                ArrangementLaneKind.PureMidiTrack,
                new MidoraId(LaneObjectIdBase + trackIndex + 1),
                null,
                0,
                IsExpanded: true,
                HasChildren: false,
                CanContainSegments: true);
        }

        if (PreviewTrackIndex >= 0)
        {
            ImportedMidiTrack previewTrack = project.Tracks[PreviewTrackIndex];
            _overviewPreview = new SegmentPreviewSource(
                0,
                _timelineTicks,
                previewTrack.Notes,
                previewTrack.Events);
        }
        else
        {
            _overviewPreview = SegmentPreviewSource.Empty;
        }

        _items = materialized;
        _itemsById = itemsById;
        _bucketsByLane = buckets;
        MaximumEndTick = Math.Max(1, maximumEndTick);
        _contentFingerprint = TimelineContentFingerprint.ForRenderItems(materialized);
    }

    public ImportedMidiProject Project => _project;
    public EditableMidiProject? LiveProject => _liveProject;
    public int TrackCount => _project.Tracks.Count;
    public int PreviewTrackIndex { get; }
    public int BarsPerSegment { get; }
    public long Count => _items.Length;
    public long MaximumEndTick { get; }
    public ulong ContentFingerprint => _liveProject is { } live
        ? TimelineContentFingerprint.Combine(_contentFingerprint, unchecked((ulong)live.Version))
        : _contentFingerprint;
    public IReadOnlyList<ArrangementLaneDescriptor> LaneDescriptors => _laneDescriptors;
    public IReadOnlyList<string> TrackNames => _trackNames;

    public IReadOnlyList<ImportedMidiNote> GetTrackNotes(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= TrackCount)
        {
            throw new ArgumentOutOfRangeException(nameof(trackIndex));
        }
        return _project.Tracks[trackIndex].Notes;
    }

    public ITimelineRenderItemSource CreateTrackSource(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= TrackCount)
        {
            throw new ArgumentOutOfRangeException(nameof(trackIndex));
        }
        return _trackSources.GetOrAdd(
            trackIndex,
            index => new TrackTimelineSource(_project.Tracks[index], _conductorItems));
    }

    public void QueryInto(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        List<TimelineRenderItem> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        VisitRange(startTick, endTick, firstLane, lastLaneExclusive, destination.Add);
    }

    public void VisitInto(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        Action<TimelineRenderItem> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        VisitRange(startTick, endTick, firstLane, lastLaneExclusive, visitor);
    }

    public bool TryGetById(MidoraId id, out TimelineRenderItem item) =>
        _itemsById.TryGetValue(id, out item);

    public IEnumerable<TimelineRenderItem> EnumerateAll() => _items;

    public void AccumulateOverviewDensity(long extent, Span<int> destination)
    {
        if (extent <= 0 || destination.IsEmpty) return;
        foreach (TimelineRenderItem item in _items)
        {
            int column = (int)Math.Clamp(
                Math.Floor(item.StartTick / (double)extent * destination.Length),
                0,
                destination.Length - 1);
            if (destination[column] != int.MaxValue) destination[column]++;
        }
    }

    public bool HasNoteContent => OverviewPreview.HasNoteContent;
    public bool HasEventContent => OverviewPreview.HasEventContent;
    public ulong NoteContentFingerprint => OverviewPreview.NoteContentFingerprint;
    public ulong EventContentFingerprint => OverviewPreview.EventContentFingerprint;

    public void QueryNotes(
        double normalizedStart,
        double normalizedEnd,
        List<TimelineSegmentPreviewNote> destination) =>
        OverviewPreview.QueryNotes(normalizedStart, normalizedEnd, destination);

    public void QueryEvents(
        double normalizedStart,
        double normalizedEnd,
        List<TimelineSegmentPreviewEvent> destination) =>
        OverviewPreview.QueryEvents(normalizedStart, normalizedEnd, destination);

    public ITimelineSegmentPreviewSource GetPreviewSource(TimelineRenderItem segment)
    {
        if (segment.Kind != TimelineItemKind.Segment)
        {
            throw new ArgumentException(
                "Only arrangement Segment items have a MIDI preview source.",
                nameof(segment));
        }
        long offset = segment.Id.Value - _segmentIdBase;
        long trackIndex = offset < 0 ? -1 : offset / _segmentCount;
        if (trackIndex < 0 || trackIndex >= _project.Tracks.Count)
        {
            throw new ArgumentException(
                "The Segment item does not belong to this MIDI source.",
                nameof(segment));
        }
        int chunk = (int)(offset % _segmentCount);
        (long startTick, long endTick) = SegmentRange(chunk);
        if (segment.Lane != trackIndex + 1 || segment.StartTick != startTick)
        {
            throw new ArgumentException(
                "The Segment item does not belong to this MIDI source.",
                nameof(segment));
        }
        if (_liveProject is { } live)
        {
            return GetLivePreview(live, segment.Id, (int)trackIndex, startTick, endTick);
        }
        return _segmentPreviews.GetOrAdd(
            segment.Id,
            _ => new SegmentPreviewSource(
                startTick,
                endTick,
                _project.Tracks[(int)trackIndex].Notes,
                _project.Tracks[(int)trackIndex].Events));
    }

    private SegmentPreviewSource GetLivePreview(
        EditableMidiProject live,
        MidoraId segmentId,
        int trackIndex,
        long startTick,
        long endTick)
    {
        long version = live.Version;
        if (_segmentPreviewsVersion != version)
        {
            _segmentPreviews = [];
            _segmentPreviewsVersion = version;
        }
        return _segmentPreviews.GetOrAdd(
            segmentId,
            _ => SegmentPreviewSource.ForLiveTrack(
                startTick,
                endTick,
                live.Tracks[trackIndex],
                version));
    }

    private SegmentPreviewSource OverviewPreview
    {
        get
        {
            if (_liveProject is not { } live) return _overviewPreview;
            long version = live.Version;
            if (_liveOverviewPreview is { } cached && _liveOverviewVersion == version)
            {
                return cached;
            }
            int trackIndex = PreviewTrackIndex >= 0
                ? PreviewTrackIndex
                : FindFirstContentTrack(live);
            SegmentPreviewSource preview = trackIndex >= 0
                ? SegmentPreviewSource.ForLiveTrack(
                    0,
                    _timelineTicks,
                    live.Tracks[trackIndex],
                    version)
                : SegmentPreviewSource.Empty;
            _liveOverviewPreview = preview;
            _liveOverviewVersion = version;
            return preview;
        }
    }

    private void VisitRange(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        Action<TimelineRenderItem> visitor)
    {
        long effectiveStart = Math.Max(0, startTick);
        if (endTick <= effectiveStart || firstLane >= lastLaneExclusive) return;
        int first = Math.Max(0, firstLane);
        int last = Math.Min(lastLaneExclusive, _bucketsByLane.Length);
        for (int lane = first; lane < last; lane++)
        {
            LaneBucket? bucket = _bucketsByLane[lane];
            if (bucket is not null) bucket.VisitInto(effectiveStart, endTick, visitor);
        }
    }

    private (long StartTick, long EndTick) SegmentRange(int chunk)
    {
        long startTick = chunk <= long.MaxValue / _segmentTicks
            ? chunk * _segmentTicks
            : long.MaxValue;
        long endTick = startTick > long.MaxValue - _segmentTicks
            ? long.MaxValue
            : startTick + _segmentTicks;
        return (startTick, endTick);
    }

    private static int FindFirstContentTrack(ImportedMidiProject project)
    {
        for (int index = 0; index < project.Tracks.Count; index++)
        {
            ImportedMidiTrack track = project.Tracks[index];
            if (track.Notes.Count != 0 || track.Events.Count != 0) return index;
        }
        return -1;
    }

    private static int FindFirstContentTrack(EditableMidiProject project)
    {
        for (int index = 0; index < project.TrackCount; index++)
        {
            EditableMidiTrack track = project.Tracks[index];
            if (track.Notes.Count != 0 || track.Events.Count != 0) return index;
        }
        return -1;
    }

    private static string TrackDisplayName(ImportedMidiTrack track, int trackIndex)
    {
        return string.IsNullOrWhiteSpace(track.Name)
            ? "Track " + (trackIndex + 1).ToString(CultureInfo.InvariantCulture)
            : track.Name;
    }

    private static TimelineRenderItem CreateConductorItem(
        ImportedMidiConductorEvent value,
        MidoraId id)
    {
        long tick = Math.Max(0, value.Tick);
        long endTick = tick == long.MaxValue ? long.MaxValue : tick + 1;
        switch (value.Kind)
        {
            case ImportedConductorKind.Tempo:
                {
                    double beatsPerMinute = double.IsFinite(value.Value) ? value.Value : 0d;
                    double normalized = Math.Clamp(
                        (beatsPerMinute - MinimumTempo) / (MaximumTempo - MinimumTempo),
                        0d,
                        1d);
                    return new TimelineRenderItem(
                        id,
                        TimelineItemKind.TempoPoint,
                        tick,
                        endTick,
                        0,
                        normalized,
                        0,
                        TimelineItemState.None)
                    {
                        SecondaryValue = beatsPerMinute,
                        Label = beatsPerMinute.ToString("0.00", CultureInfo.InvariantCulture) + " BPM",
                        AccentColor = ConductorRenderItemSource.TempoAccentColor
                    };
                }
            case ImportedConductorKind.TimeSignature:
                return new TimelineRenderItem(
                    id,
                    TimelineItemKind.ConductorEvent,
                    tick,
                    endTick,
                    0,
                    0,
                    1,
                    TimelineItemState.None)
                {
                    SecondaryValue = value.Numerator * 128 + value.Denominator,
                    Label = value.Numerator.ToString(CultureInfo.InvariantCulture)
                        + "/" + value.Denominator.ToString(CultureInfo.InvariantCulture),
                    AccentColor = TimeSignatureAccentColor
                };
            case ImportedConductorKind.KeySignature:
                {
                    bool isMinor = value.Denominator != 0;
                    return new TimelineRenderItem(
                        id,
                        TimelineItemKind.ConductorEvent,
                        tick,
                        endTick,
                        0,
                        0,
                        2,
                        TimelineItemState.None)
                    {
                        SecondaryValue = value.Numerator + 7 + (isMinor ? 16 : 0),
                        Label = value.Numerator.ToString("+0;-0;0", CultureInfo.InvariantCulture)
                            + (isMinor ? " minor" : " major"),
                        AccentColor = KeySignatureAccentColor
                    };
                }
            default:
                return new TimelineRenderItem(
                    id,
                    TimelineItemKind.Marker,
                    tick,
                    endTick,
                    0,
                    0,
                    3,
                    TimelineItemState.None)
                {
                    Label = value.Text ?? string.Empty,
                    AccentColor = MarkerAccentColor
                };
        }
    }

    private static (double Primary, double Secondary) MapEventData(ImportedMidiEvent value)
    {
        return value.Kind switch
        {
            ImportedMidiEventKind.ProgramChange => (value.Data1, value.Data2),
            ImportedMidiEventKind.AfterTouch => (value.Data1, value.Data2),
            ImportedMidiEventKind.PitchBend => (value.PitchBendValue, value.Data1),
            _ => (value.Data2, value.Data1)
        };
    }

    private static double NormalizeEventPreviewValue(ImportedMidiEvent value)
    {
        double normalized = value.Kind switch
        {
            ImportedMidiEventKind.ProgramChange => value.Data1 / 127d,
            ImportedMidiEventKind.AfterTouch => value.Data1 / 127d,
            ImportedMidiEventKind.PitchBend => value.PitchBendValue / 16383d,
            _ => value.Data2 / 127d
        };
        return double.IsFinite(normalized) ? Math.Clamp(normalized, 0d, 1d) : 0d;
    }

    private sealed class TrackTimelineSource :
        ITimelineRenderItemSource,
        INonBlockingTimelineFingerprintSource
    {
        private readonly TimelineRenderItem[] _items;
        private readonly Dictionary<MidoraId, TimelineRenderItem> _itemsById;
        private readonly LaneBucket?[] _bucketsByLane;

        public TrackTimelineSource(
            ImportedMidiTrack track,
            IReadOnlyList<TimelineRenderItem> conductorItems)
        {
            ArgumentNullException.ThrowIfNull(track);
            ArgumentNullException.ThrowIfNull(conductorItems);
            List<TimelineRenderItem> items = new(track.Notes.Count + track.Events.Count);
            for (int index = 0; index < track.Notes.Count; index++)
            {
                ImportedMidiNote note = track.Notes[index];
                long endTick = note.EndTick > note.StartTick
                    ? note.EndTick
                    : note.StartTick == long.MaxValue
                        ? long.MaxValue
                        : note.StartTick + 1;
                bool invalid = note.Key is < 0 or > 127;
                items.Add(new(
                    new MidoraId(TrackNoteIdBase + index),
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
            for (int index = 0; index < track.Events.Count; index++)
            {
                ImportedMidiEvent value = track.Events[index];
                (double primary, double secondary) = MapEventData(value);
                long startTick = Math.Max(0, value.Tick);
                long eventEndTick = startTick == long.MaxValue ? long.MaxValue : startTick + 1;
                items.Add(new(
                    new MidoraId(TrackEventIdBase + index),
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

            Dictionary<MidoraId, TimelineRenderItem> itemsById = [];
            long maximumEndTick = 0;
            int maximumLane = 0;
            foreach (TimelineRenderItem item in materialized)
            {
                itemsById[item.Id] = item;
                maximumEndTick = Math.Max(maximumEndTick, item.EndTick);
                maximumLane = Math.Max(maximumLane, item.Lane);
            }
            foreach (TimelineRenderItem item in conductorItems)
            {
                maximumEndTick = Math.Max(maximumEndTick, item.EndTick);
            }

            LaneBucket?[] buckets = new LaneBucket?[maximumLane + 1];
            int itemIndex = 0;
            while (itemIndex < materialized.Length)
            {
                int first = itemIndex;
                int lane = materialized[first].Lane;
                while (itemIndex < materialized.Length && materialized[itemIndex].Lane == lane)
                {
                    itemIndex++;
                }
                buckets[lane] = new LaneBucket(materialized[first..itemIndex]);
            }

            _items = materialized;
            _itemsById = itemsById;
            _bucketsByLane = buckets;
            MaximumEndTick = Math.Max(1, maximumEndTick);
            ContentFingerprint = TimelineContentFingerprint.Combine(
                TimelineContentFingerprint.ForRenderItems(materialized),
                TimelineContentFingerprint.ForRenderItems(conductorItems));
        }

        public long Count => _items.Length;
        public long MaximumEndTick { get; }
        public ulong ContentFingerprint { get; }

        public void QueryInto(
            long startTick,
            long endTick,
            int firstLane,
            int lastLaneExclusive,
            List<TimelineRenderItem> destination)
        {
            ArgumentNullException.ThrowIfNull(destination);
            VisitRange(startTick, endTick, firstLane, lastLaneExclusive, destination.Add);
        }

        public void VisitInto(
            long startTick,
            long endTick,
            int firstLane,
            int lastLaneExclusive,
            Action<TimelineRenderItem> visitor)
        {
            ArgumentNullException.ThrowIfNull(visitor);
            VisitRange(startTick, endTick, firstLane, lastLaneExclusive, visitor);
        }

        public bool TryGetById(MidoraId id, out TimelineRenderItem item) =>
            _itemsById.TryGetValue(id, out item);

        public IEnumerable<TimelineRenderItem> EnumerateAll() => _items;

        public void AccumulateOverviewDensity(long extent, Span<int> destination)
        {
            if (extent <= 0 || destination.IsEmpty) return;
            foreach (TimelineRenderItem item in _items)
            {
                int column = (int)Math.Clamp(
                    Math.Floor(item.StartTick / (double)extent * destination.Length),
                    0,
                    destination.Length - 1);
                if (destination[column] != int.MaxValue) destination[column]++;
            }
        }

        private void VisitRange(
            long startTick,
            long endTick,
            int firstLane,
            int lastLaneExclusive,
            Action<TimelineRenderItem> visitor)
        {
            long effectiveStart = Math.Max(0, startTick);
            if (endTick <= effectiveStart || firstLane >= lastLaneExclusive) return;
            int first = Math.Max(0, firstLane);
            int last = Math.Min(lastLaneExclusive, _bucketsByLane.Length);
            for (int lane = first; lane < last; lane++)
            {
                LaneBucket? bucket = _bucketsByLane[lane];
                if (bucket is not null) bucket.VisitInto(effectiveStart, endTick, visitor);
            }
        }
    }

    private sealed class SegmentPreviewSource : ITimelineSegmentPreviewSource
    {
        public static SegmentPreviewSource Empty { get; } = new(
            0,
            1,
            Array.Empty<ImportedMidiNote>(),
            Array.Empty<ImportedMidiEvent>());

        private readonly TimelineSegmentPreviewNote[] _notes;
        private readonly TimelineSegmentPreviewEvent[] _events;

        public SegmentPreviewSource(
            long startTick,
            long endTick,
            IEnumerable<ImportedMidiNote> notes,
            IEnumerable<ImportedMidiEvent> events,
            ulong? fingerprintSalt = null)
        {
            ArgumentNullException.ThrowIfNull(notes);
            ArgumentNullException.ThrowIfNull(events);
            if (endTick <= startTick)
            {
                throw new ArgumentOutOfRangeException(nameof(endTick));
            }

            double length = endTick - startTick;
            List<TimelineSegmentPreviewNote> mappedNotes = [];
            foreach (ImportedMidiNote note in notes)
            {
                long clippedStart = Math.Max(startTick, note.StartTick);
                long clippedEnd = Math.Min(endTick, note.EndTick);
                if (clippedEnd <= clippedStart) continue;
                mappedNotes.Add(new(
                    (clippedStart - startTick) / length,
                    (clippedEnd - startTick) / length,
                    Math.Clamp(note.Key, 0, 127)));
            }
            _notes = [.. mappedNotes];
            Array.Sort(_notes, static (left, right) =>
            {
                int value = left.NormalizedStart.CompareTo(right.NormalizedStart);
                if (value != 0) return value;
                value = left.NormalizedEnd.CompareTo(right.NormalizedEnd);
                return value != 0 ? value : left.Pitch.CompareTo(right.Pitch);
            });

            List<TimelineSegmentPreviewEvent> mappedEvents = [];
            foreach (ImportedMidiEvent value in events)
            {
                if (value.Tick < startTick || value.Tick >= endTick) continue;
                mappedEvents.Add(new(
                    (value.Tick - startTick) / length,
                    NormalizeEventPreviewValue(value)));
            }
            _events = [.. mappedEvents];
            Array.Sort(_events, static (left, right) =>
            {
                int value = left.NormalizedTick.CompareTo(right.NormalizedTick);
                return value != 0 ? value : left.NormalizedValue.CompareTo(right.NormalizedValue);
            });

            HasNoteContent = _notes.Length != 0;
            HasEventContent = _events.Length != 0;
            ulong noteFingerprint = TimelineContentFingerprint.ForSegmentPreviewNotes(_notes);
            ulong eventFingerprint = TimelineContentFingerprint.ForSegmentPreviewEvents(_events);
            if (fingerprintSalt is { } salt)
            {
                noteFingerprint = TimelineContentFingerprint.Combine(noteFingerprint, salt);
                eventFingerprint = TimelineContentFingerprint.Combine(eventFingerprint, salt);
            }
            NoteContentFingerprint = noteFingerprint;
            EventContentFingerprint = eventFingerprint;
        }

        public static SegmentPreviewSource ForLiveTrack(
            long startTick,
            long endTick,
            EditableMidiTrack track,
            long version)
        {
            ArgumentNullException.ThrowIfNull(track);
            return new SegmentPreviewSource(
                startTick,
                endTick,
                EnumerateNotes(track.Notes),
                EnumerateEvents(track.Events),
                unchecked((ulong)version));
        }

        private static IEnumerable<ImportedMidiNote> EnumerateNotes(
            IReadOnlyList<EditableMidiNote> notes)
        {
            for (int index = 0; index < notes.Count; index++)
            {
                EditableMidiNote note = notes[index];
                yield return new ImportedMidiNote(
                    note.StartTick,
                    note.EndTick,
                    note.Key,
                    note.Velocity,
                    note.NoteOffVelocity,
                    note.Channel);
            }
        }

        private static IEnumerable<ImportedMidiEvent> EnumerateEvents(
            IReadOnlyList<EditableMidiEvent> events)
        {
            for (int index = 0; index < events.Count; index++)
            {
                EditableMidiEvent value = events[index];
                yield return new ImportedMidiEvent(
                    value.Tick,
                    value.Channel,
                    value.Kind,
                    value.Data1,
                    value.Data2);
            }
        }

        public bool HasNoteContent { get; }
        public bool HasEventContent { get; }
        public ulong NoteContentFingerprint { get; }
        public ulong EventContentFingerprint { get; }

        public void QueryNotes(
            double normalizedStart,
            double normalizedEnd,
            List<TimelineSegmentPreviewNote> destination)
        {
            ArgumentNullException.ThrowIfNull(destination);
            if (!double.IsFinite(normalizedStart)
                || !double.IsFinite(normalizedEnd)
                || normalizedEnd <= normalizedStart)
            {
                return;
            }
            foreach (TimelineSegmentPreviewNote note in _notes)
            {
                if (note.NormalizedStart < normalizedEnd && note.NormalizedEnd > normalizedStart)
                {
                    destination.Add(note);
                }
            }
        }

        public void QueryEvents(
            double normalizedStart,
            double normalizedEnd,
            List<TimelineSegmentPreviewEvent> destination)
        {
            ArgumentNullException.ThrowIfNull(destination);
            if (!double.IsFinite(normalizedStart)
                || !double.IsFinite(normalizedEnd)
                || normalizedEnd <= normalizedStart)
            {
                return;
            }
            foreach (TimelineSegmentPreviewEvent value in _events)
            {
                if (value.NormalizedTick >= normalizedStart && value.NormalizedTick < normalizedEnd)
                {
                    destination.Add(value);
                }
            }
        }
    }

    private sealed class LaneBucket
    {
        private readonly TimelineRenderItem[] _items;
        private readonly long[] _maximumEndPrefix;

        public LaneBucket(TimelineRenderItem[] items)
        {
            _items = items;
            _maximumEndPrefix = new long[items.Length];
            long maximumEnd = long.MinValue;
            for (int index = 0; index < items.Length; index++)
            {
                maximumEnd = Math.Max(maximumEnd, items[index].EndTick);
                _maximumEndPrefix[index] = maximumEnd;
            }
        }

        public void VisitInto(
            long startTick,
            long endTick,
            Action<TimelineRenderItem> visitor)
        {
            int first = FirstPrefixEndGreaterThan(startTick);
            for (int index = first; index < _items.Length; index++)
            {
                TimelineRenderItem item = _items[index];
                if (item.StartTick >= endTick) break;
                if (item.EndTick > startTick) visitor(item);
            }
        }

        private int FirstPrefixEndGreaterThan(long value)
        {
            int low = 0;
            int high = _maximumEndPrefix.Length;
            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                if (_maximumEndPrefix[middle] <= value) low = middle + 1;
                else high = middle;
            }
            return low;
        }
    }
}
