using Midora.Domain;

namespace Midora.Avalonia.Presentation.Rendering;

public sealed class DemoTimelineSource :
    ITimelineRenderItemSource,
    INonBlockingTimelineFingerprintSource,
    ITimelineSegmentPreviewSource
{
    public const int DefaultTrackCount = 6;
    public const int DefaultTicksPerQuarterNote = 480;
    public const int TotalBars = 64;
    public const int BarsPerSegment = 8;
    public const int BeatsPerBar = 4;

    private const int SegmentCount = TotalBars / BarsPerSegment;
    private const int ContentTrackIndex = 0;
    private const int ContentBasePitch = 48;
    private const uint TimeSignatureAccentColor = 0xff62a6f6;
    private const uint MarkerAccentColor = 0xffe8b34b;
    private const uint EventAccentColor = 0xff9aa0a6;

    private static readonly int[] DiatonicOffsets = [0, 2, 4, 5, 7, 9, 11];
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
    private static readonly SegmentPreviewSource EmptyPreview = new(
        Array.Empty<TimelineSegmentPreviewNote>(),
        Array.Empty<TimelineSegmentPreviewEvent>());

    private readonly TimelineRenderItem[] _items;
    private readonly Dictionary<MidoraId, TimelineRenderItem> _itemsById;
    private readonly LaneBucket?[] _bucketsByLane;
    private readonly Dictionary<MidoraId, SegmentPreviewSource> _segmentPreviews;
    private readonly SegmentPreviewSource _combinedPreview;
    private readonly string[] _trackNames;
    private readonly ArrangementLaneDescriptor[] _laneDescriptors;

    private DemoTimelineSource(int trackCount, int ticksPerQuarterNote)
    {
        TrackCount = trackCount;
        TicksPerQuarterNote = ticksPerQuarterNote;

        long barTicks = checked(BeatsPerBar * (long)ticksPerQuarterNote);
        long segmentTicks = checked(BarsPerSegment * barTicks);
        long totalTicks = checked(TotalBars * barTicks);
        long stepTicks = Math.Max(1, ticksPerQuarterNote / 2);
        int stepsPerSegment = checked((int)(segmentTicks / stepTicks));
        long tempoStepTicks = checked((TotalBars / 4) * barTicks);
        TotalTicks = totalTicks;

        long nextIdValue = 1;
        MidoraId NextId() => new(nextIdValue++);

        MidoraId conductorId = NextId();
        MidoraId[] trackIds = new MidoraId[trackCount];
        for (int track = 0; track < trackCount; track++) trackIds[track] = NextId();

        List<TimelineRenderItem> items = [];
        Dictionary<MidoraId, TimelineRenderItem> itemsById = [];
        Dictionary<MidoraId, SegmentPreviewSource> segmentPreviews = [];
        List<TimelineSegmentPreviewNote> combinedNotes = [];
        List<TimelineSegmentPreviewEvent> combinedEvents = [];

        for (int step = 0; step < 4; step++)
        {
            long tick = step * tempoStepTicks;
            int beatsPerMinute = 100 + 12 * step;
            TimelineRenderItem tempo = new(
                NextId(), TimelineItemKind.TempoPoint, tick, tick + 1, 0,
                (beatsPerMinute - 100) / 36.0, 0, TimelineItemState.None)
            {
                SecondaryValue = beatsPerMinute,
                Label = beatsPerMinute + " BPM",
                AccentColor = ConductorRenderItemSource.TempoAccentColor
            };
            items.Add(tempo);
            itemsById[tempo.Id] = tempo;
        }

        long[] signatureTicks = [0, checked((TotalBars / 2) * barTicks)];
        int[] signatureNumerators = [4, 3];
        for (int index = 0; index < signatureTicks.Length; index++)
        {
            long tick = signatureTicks[index];
            int numerator = signatureNumerators[index];
            TimelineRenderItem signature = new(
                NextId(), TimelineItemKind.ConductorEvent, tick, tick + 1, 0, 0, 1,
                TimelineItemState.None)
            {
                SecondaryValue = numerator * 128 + BeatsPerBar,
                Label = numerator + "/" + BeatsPerBar,
                AccentColor = TimeSignatureAccentColor
            };
            items.Add(signature);
            itemsById[signature.Id] = signature;
        }

        for (int section = 0; section < SegmentCount; section++)
        {
            long tick = section * segmentTicks;
            TimelineRenderItem marker = new(
                NextId(), TimelineItemKind.Marker, tick, tick + 1, 0, 0, 3,
                TimelineItemState.None)
            {
                Label = "Section " + (char)('A' + section),
                AccentColor = MarkerAccentColor
            };
            items.Add(marker);
            itemsById[marker.Id] = marker;
        }

        int globalStep = 0;
        int globalBar = 0;
        for (int track = 0; track < trackCount; track++)
        {
            int lane = track + 1;
            uint accent = TrackAccentColors[track % TrackAccentColors.Length];
            for (int segment = 0; segment < SegmentCount; segment++)
            {
                long segmentStart = segment * segmentTicks;
                long segmentEnd = segmentStart + segmentTicks;
                TimelineRenderItem segmentItem = new(
                    NextId(), TimelineItemKind.Segment, segmentStart, segmentEnd, lane, 1, 0,
                    TimelineItemState.None)
                {
                    Label = "Track " + (track + 1) + ": Bars "
                        + (segment * BarsPerSegment + 1) + "-" + ((segment + 1) * BarsPerSegment),
                    AccentColor = accent
                };
                items.Add(segmentItem);
                itemsById[segmentItem.Id] = segmentItem;
                if (track != ContentTrackIndex)
                {
                    segmentPreviews[segmentItem.Id] = EmptyPreview;
                    continue;
                }

                List<TimelineSegmentPreviewNote> segmentNotes = [];
                List<TimelineSegmentPreviewEvent> segmentEvents = [];
                for (int step = 0; step < stepsPerSegment; step++)
                {
                    long tick = segmentStart + step * stepTicks;
                    int pitch = ContentBasePitch
                        + 12 * ((globalStep / 7) % 3)
                        + DiatonicOffsets[globalStep % 7];
                    int velocity = 72 + (globalStep % 4) * 12;
                    TimelineRenderItem note = new(
                        NextId(), TimelineItemKind.LogicalNote, tick, tick + stepTicks, pitch,
                        velocity, 1, TimelineItemState.None)
                    {
                        Label = "Note " + pitch,
                        AccentColor = accent
                    };
                    items.Add(note);
                    itemsById[note.Id] = note;
                    segmentNotes.Add(new(
                        (tick - segmentStart) / (double)segmentTicks,
                        (tick + stepTicks - segmentStart) / (double)segmentTicks,
                        pitch));
                    combinedNotes.Add(new(
                        tick / (double)totalTicks,
                        (tick + stepTicks) / (double)totalTicks,
                        pitch));
                    globalStep++;
                }

                for (int bar = 0; bar < BarsPerSegment; bar++)
                {
                    long tick = segmentStart + bar * barTicks;
                    double value = ((globalBar * 3) % 7) / 6.0;
                    TimelineRenderItem parameter = new(
                        NextId(), TimelineItemKind.LogicalParameterPoint, tick, tick + 1, 0,
                        value, 0, TimelineItemState.None)
                    {
                        Label = "Expression",
                        AccentColor = EventAccentColor
                    };
                    items.Add(parameter);
                    itemsById[parameter.Id] = parameter;
                    segmentEvents.Add(new(
                        (tick - segmentStart) / (double)segmentTicks,
                        value));
                    combinedEvents.Add(new(
                        tick / (double)totalTicks,
                        value));
                    globalBar++;
                }

                segmentPreviews[segmentItem.Id] = new SegmentPreviewSource(
                    segmentNotes,
                    segmentEvents);
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
        int maximumLane = -1;
        foreach (TimelineRenderItem item in materialized)
        {
            maximumEndTick = Math.Max(maximumEndTick, item.EndTick);
            maximumLane = Math.Max(maximumLane, item.Lane);
        }

        LaneBucket?[] buckets = new LaneBucket[Math.Max(0, maximumLane + 1)];
        int itemIndex = 0;
        while (itemIndex < materialized.Length)
        {
            int first = itemIndex;
            int lane = materialized[first].Lane;
            while (itemIndex < materialized.Length && materialized[itemIndex].Lane == lane)
                itemIndex++;
            buckets[lane] = new LaneBucket(materialized[first..itemIndex]);
        }

        string[] trackNames = new string[trackCount];
        ArrangementLaneDescriptor[] laneDescriptors = new ArrangementLaneDescriptor[trackCount + 1];
        laneDescriptors[0] = new(
            0, ArrangementLaneKind.Conductor, conductorId, null, 0,
            IsExpanded: true, HasChildren: false, CanContainSegments: false);
        for (int track = 0; track < trackCount; track++)
        {
            trackNames[track] = "Track " + (track + 1);
            laneDescriptors[track + 1] = new(
                track + 1, ArrangementLaneKind.LogicalTrack, trackIds[track], null, 0,
                IsExpanded: true, HasChildren: false, CanContainSegments: true);
        }

        _items = materialized;
        _itemsById = itemsById;
        _bucketsByLane = buckets;
        _segmentPreviews = segmentPreviews;
        _combinedPreview = new SegmentPreviewSource(combinedNotes, combinedEvents);
        _trackNames = trackNames;
        _laneDescriptors = laneDescriptors;
        MaximumEndTick = maximumEndTick;
        ContentFingerprint = TimelineContentFingerprint.ForRenderItems(materialized);
    }

    public static DemoTimelineSource Create(
        int trackCount = DefaultTrackCount,
        int ticksPerQuarterNote = DefaultTicksPerQuarterNote)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(trackCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(ticksPerQuarterNote, 1);
        return new DemoTimelineSource(trackCount, ticksPerQuarterNote);
    }

    public int TrackCount { get; }
    public int TicksPerQuarterNote { get; }
    public long TotalTicks { get; }
    public long Count => _items.Length;
    public long MaximumEndTick { get; }
    public ulong ContentFingerprint { get; }
    public IReadOnlyList<TimelineRenderItem> Items => _items;
    public IReadOnlyList<string> TrackNames => _trackNames;
    public IReadOnlyList<ArrangementLaneDescriptor> LaneDescriptors => _laneDescriptors;

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
        int first = Math.Max(0, firstLane);
        int last = Math.Min(lastLaneExclusive, _bucketsByLane.Length);
        for (int lane = first; lane < last; lane++)
        {
            LaneBucket? bucket = _bucketsByLane[lane];
            if (bucket is not null) bucket.QueryInto(effectiveStart, endTick, destination);
        }
    }

    public void VisitInto(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        Action<TimelineRenderItem> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
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

    public bool HasNoteContent => _combinedPreview.HasNoteContent;
    public bool HasEventContent => _combinedPreview.HasEventContent;
    public ulong NoteContentFingerprint => _combinedPreview.NoteContentFingerprint;
    public ulong EventContentFingerprint => _combinedPreview.EventContentFingerprint;

    public void QueryNotes(
        double normalizedStart,
        double normalizedEnd,
        List<TimelineSegmentPreviewNote> destination) =>
        _combinedPreview.QueryNotes(normalizedStart, normalizedEnd, destination);

    public void QueryEvents(
        double normalizedStart,
        double normalizedEnd,
        List<TimelineSegmentPreviewEvent> destination) =>
        _combinedPreview.QueryEvents(normalizedStart, normalizedEnd, destination);

    public ITimelineSegmentPreviewSource GetPreviewSource(TimelineRenderItem segment)
    {
        if (segment.Kind != TimelineItemKind.Segment)
        {
            throw new ArgumentException(
                "Only arrangement Segment items have a demo preview source.",
                nameof(segment));
        }
        if (!_segmentPreviews.TryGetValue(segment.Id, out SegmentPreviewSource? source))
        {
            throw new ArgumentException(
                "The Segment item does not belong to this demo source.",
                nameof(segment));
        }
        return source;
    }

    private sealed class SegmentPreviewSource : ITimelineSegmentPreviewSource
    {
        private readonly TimelineSegmentPreviewNote[] _notes;
        private readonly TimelineSegmentPreviewEvent[] _events;

        public SegmentPreviewSource(
            IEnumerable<TimelineSegmentPreviewNote> notes,
            IEnumerable<TimelineSegmentPreviewEvent> events)
        {
            ArgumentNullException.ThrowIfNull(notes);
            ArgumentNullException.ThrowIfNull(events);
            _notes = notes.ToArray();
            Array.Sort(_notes, static (left, right) =>
            {
                int value = left.NormalizedStart.CompareTo(right.NormalizedStart);
                if (value != 0) return value;
                value = left.NormalizedEnd.CompareTo(right.NormalizedEnd);
                return value != 0 ? value : left.Pitch.CompareTo(right.Pitch);
            });
            _events = events.ToArray();
            Array.Sort(_events, static (left, right) =>
            {
                int value = left.NormalizedTick.CompareTo(right.NormalizedTick);
                return value != 0 ? value : left.NormalizedValue.CompareTo(right.NormalizedValue);
            });
            HasNoteContent = _notes.Length != 0;
            HasEventContent = _events.Length != 0;
            NoteContentFingerprint = TimelineContentFingerprint.ForSegmentPreviewNotes(_notes);
            EventContentFingerprint = TimelineContentFingerprint.ForSegmentPreviewEvents(_events);
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
                    destination.Add(note);
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
                    destination.Add(value);
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

        public void QueryInto(
            long startTick,
            long endTick,
            List<TimelineRenderItem> destination)
        {
            int first = FirstPrefixEndGreaterThan(startTick);
            for (int index = first; index < _items.Length; index++)
            {
                TimelineRenderItem item = _items[index];
                if (item.StartTick >= endTick) break;
                if (item.EndTick > startTick) destination.Add(item);
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
