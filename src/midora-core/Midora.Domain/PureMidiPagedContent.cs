using System.Collections;

namespace Midora.Domain;

public readonly record struct DirectMidiNoteValue(
    MidoraId Id,
    long StartTick,
    long LengthTicks,
    int Key,
    int NoteOnVelocity,
    int NoteOffVelocity,
    long NoteOnOrder,
    long NoteOffOrder);

public readonly record struct DirectMidiChannelEventValue(
    MidoraId Id,
    long Tick,
    DirectMidiChannelEventKind Kind,
    int Data1,
    int Data2,
    long Order);

public readonly record struct OpaqueMidiEventValue(
    MidoraId Id,
    long Tick,
    OpaqueMidiEventKind Kind,
    byte MetaType,
    ReadOnlyMemory<byte> Payload,
    long Order);

public readonly record struct DirectMidiNoteSourceMatch(
    int Index,
    DirectMidiNoteValue Value);

public readonly record struct DirectMidiChannelEventSourceMatch(
    int Index,
    DirectMidiChannelEventValue Value);

public readonly record struct OpaqueMidiEventSourceMatch(
    int Index,
    OpaqueMidiEventValue Value);

public readonly record struct DirectMidiNoteMatch(
    int Index,
    DirectMidiNote Value);

public readonly record struct DirectMidiNoteStartKey(long Tick, int Key);

public readonly record struct DirectMidiEventStartKey(
    long Tick,
    DirectMidiChannelEventKind Kind,
    int Data1);

public readonly record struct DirectMidiChannelEventMatch(
    int Index,
    DirectMidiChannelEvent Value);

public readonly record struct OpaqueMidiEventMatch(
    int Index,
    OpaqueMidiEvent Value);

public readonly record struct PureMidiContentRangeSummary(
    long MinimumTick,
    long MaximumTick,
    int RecordCount);

public interface IPureMidiContentOverviewSource
{
    IEnumerable<PureMidiContentRangeSummary> GetNoteRangeSummaries();

    IEnumerable<PureMidiContentRangeSummary> GetChannelEventRangeSummaries() => [];

    IEnumerable<PureMidiContentRangeSummary> GetOpaqueEventRangeSummaries() => [];

    bool TryAccumulateNoteStartColumns(
        long extent,
        Span<byte> destination,
        IReadOnlySet<MidoraId>? excludedIds) => false;

    bool TryAccumulateChannelEventColumns(
        long extent,
        Span<byte> noteStartColumns,
        Span<byte> eventColumns,
        IReadOnlySet<MidoraId>? excludedIds) => false;

    bool TryAccumulateOpaqueEventColumns(
        long extent,
        Span<byte> destination,
        IReadOnlySet<MidoraId>? excludedIds) => false;
}

internal static class PureMidiOverviewProjection
{
    public static void Validate(long extent, Span<byte> destination)
    {
        if (extent <= 0) throw new ArgumentOutOfRangeException(nameof(extent));
        if (destination.IsEmpty) return;
    }

    public static void Validate(
        long extent,
        Span<byte> noteStartColumns,
        Span<byte> eventColumns)
    {
        Validate(extent, noteStartColumns);
        if (noteStartColumns.Length != eventColumns.Length)
        {
            throw new ArgumentException(
                "Pure MIDI overview channels must have equal widths.");
        }
    }

    public static int Column(long tick, long extent, int width)
    {
        if (extent <= 0) throw new ArgumentOutOfRangeException(nameof(extent));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (tick <= 0) return 0;
        double projected = tick / (double)extent * width;
        return projected >= width ? width - 1 : (int)projected;
    }

    public static void Mark(Span<byte> destination, long tick, long extent)
    {
        if (destination.IsEmpty) return;
        destination[Column(tick, extent, destination.Length)] = 1;
    }

    public static void Mark(
        DirectMidiChannelEventKind kind,
        int data2,
        long tick,
        long extent,
        Span<byte> noteStartColumns,
        Span<byte> eventColumns)
    {
        if (kind == DirectMidiChannelEventKind.NoteOn && data2 > 0)
        {
            Mark(noteStartColumns, tick, extent);
        }
        else if (kind is not (DirectMidiChannelEventKind.NoteOn
            or DirectMidiChannelEventKind.NoteOff))
        {
            Mark(eventColumns, tick, extent);
        }
    }
}

public interface IPureMidiSegmentContentSource
{
    int NoteCount { get; }
    int ChannelEventCount { get; }
    int OpaqueEventCount { get; }
    string ContentFingerprint { get; }

    DirectMidiNoteValue GetNote(int index);
    DirectMidiChannelEventValue GetChannelEvent(int index);
    OpaqueMidiEventValue GetOpaqueEvent(int index);
    int FindNoteIndex(MidoraId id);
    int FindChannelEventIndex(MidoraId id);
    int FindOpaqueEventIndex(MidoraId id);

    IEnumerable<DirectMidiNoteSourceMatch> QueryNotesByIds(
        IReadOnlySet<MidoraId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        foreach (MidoraId id in ids)
        {
            int index = FindNoteIndex(id);
            if (index >= 0) yield return new(index, GetNote(index));
        }
    }

    IEnumerable<DirectMidiNoteSourceMatch> QueryNotesAtStarts(
        IReadOnlySet<DirectMidiNoteStartKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (DirectMidiNoteStartKey key in keys)
        {
            long endTick = key.Tick == long.MaxValue ? long.MaxValue : key.Tick + 1;
            foreach (DirectMidiNoteValue value in QueryNotes(
                key.Tick,
                endTick,
                key.Key,
                key.Key))
            {
                if (value.StartTick == key.Tick)
                {
                    int index = FindNoteIndex(value.Id);
                    if (index >= 0) yield return new(index, value);
                }
            }
        }
    }

    IEnumerable<DirectMidiChannelEventSourceMatch> QueryChannelEventsByIds(
        IReadOnlySet<MidoraId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        foreach (MidoraId id in ids)
        {
            int index = FindChannelEventIndex(id);
            if (index >= 0) yield return new(index, GetChannelEvent(index));
        }
    }

    IEnumerable<DirectMidiChannelEventSourceMatch> QueryChannelEventsAtStarts(
        IReadOnlySet<DirectMidiEventStartKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (DirectMidiEventStartKey key in keys)
        {
            IEnumerable<DirectMidiChannelEventValue> candidates = key.Tick == long.MaxValue
                ? Enumerable.Range(0, ChannelEventCount).Select(GetChannelEvent)
                : QueryChannelEvents(key.Tick, key.Tick + 1);
            foreach (DirectMidiChannelEventValue value in candidates)
            {
                int selector = value.Kind is DirectMidiChannelEventKind.ControlChange
                    or DirectMidiChannelEventKind.PolyphonicKeyPressure
                    or DirectMidiChannelEventKind.NoteOn
                    or DirectMidiChannelEventKind.NoteOff
                        ? value.Data1
                        : 0;
                if (value.Tick == key.Tick && value.Kind == key.Kind && selector == key.Data1)
                {
                    int index = FindChannelEventIndex(value.Id);
                    if (index >= 0) yield return new(index, value);
                }
            }
        }
    }

    IEnumerable<OpaqueMidiEventSourceMatch> QueryOpaqueEventsByIds(
        IReadOnlySet<MidoraId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        foreach (MidoraId id in ids)
        {
            int index = FindOpaqueEventIndex(id);
            if (index >= 0) yield return new(index, GetOpaqueEvent(index));
        }
    }

    IEnumerable<DirectMidiNoteValue> QueryNotes(
        long startTick,
        long endTick,
        int minimumKey = 0,
        int maximumKey = 127);

    IEnumerable<DirectMidiChannelEventValue> QueryChannelEvents(
        long startTick,
        long endTick);

    IEnumerable<OpaqueMidiEventValue> QueryOpaqueEvents(
        long startTick,
        long endTick);
}

public interface IPureMidiPlaybackEndpointSource
{
    IEnumerable<DirectMidiNoteValue> QueryNoteStarts(
        long startTick,
        long endTick);

    IEnumerable<DirectMidiNoteValue> QueryNoteEnds(
        long startTick,
        long endTick);

    IEnumerable<DirectMidiNoteValue> QueryActiveNotes(long tick);

    IEnumerable<DirectMidiChannelEventValue> QueryOrderedChannelEvents(
        long startTick,
        long endTick);
}

internal interface IDirectMidiNoteChangeSink
{
    void OnChanged(DirectMidiNote value);
}

internal interface IDirectMidiChannelEventChangeSink
{
    void OnChanged(DirectMidiChannelEvent value);
}

internal interface IOpaqueMidiEventChangeSink
{
    void OnChanged(OpaqueMidiEvent value);
}

internal interface IPureMidiContentPackSegmentSource
{
    PureMidiContentPack Owner { get; }
}

public sealed class DirectMidiNoteCollection : IList<DirectMidiNote>, IReadOnlyList<DirectMidiNote>, IDirectMidiNoteChangeSink
{
    private readonly MidoraProject _project;
    private readonly List<DirectMidiNote> _added = [];
    private readonly Dictionary<MidoraId, DirectMidiNote> _replacements = [];
    private readonly HashSet<MidoraId> _removed = [];
    private readonly HashSet<MidoraId> _materializedSourceIds = [];
    private readonly Dictionary<MidoraId, int> _sourceIndices = [];
    private IPureMidiSegmentContentSource? _source;
    private bool _clearSource;
    private long _generation;
    private int _batchChangeDepth;
    private bool _batchChanged;
    private HashSet<MidoraId>? _batchSourceIds;

    internal DirectMidiNoteCollection(MidoraProject project) => _project = project;

    public int Count => checked(LiveSourceCount + _added.Count);
    public bool IsReadOnly => false;
    public bool HasPagedSource => _source is not null;
    public long Generation => _generation;
    internal IEnumerable<DirectMidiNote> EditedItems => _replacements.Values.Concat(_added);
    internal IReadOnlyCollection<MidoraId> RemovedSourceIds => _removed;
    internal bool ClearsPagedSource => _clearSource;
    internal IPureMidiSegmentContentSource? PagedSource => _source;
    internal bool IsPristinePagedSource => _source is not null
        && !_clearSource
        && _removed.Count == 0
        && _replacements.Count == 0
        && _added.Count == 0;

    public DirectMidiNote this[int index]
    {
        get
        {
            int sourceIndex = FindSourceIndexForVisibleIndex(index);
            if (sourceIndex >= 0)
            {
                DirectMidiNoteValue value = _source!.GetNote(sourceIndex);
                if (_replacements.TryGetValue(value.Id, out DirectMidiNote? replacement))
                {
                    return replacement;
                }
                return Materialize(value, sourceIndex);
            }
            int addedIndex = checked(index - LiveSourceCount);
            return _added[addedIndex];
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            int sourceIndex = FindSourceIndexForVisibleIndex(index);
            if (sourceIndex >= 0)
            {
                DirectMidiNoteValue original = _source!.GetNote(sourceIndex);
                if (value.Id == original.Id)
                {
                    Track(value);
                    _sourceIndices[original.Id] = sourceIndex;
                    _replacements[original.Id] = value;
                }
                else
                {
                    _removed.Add(original.Id);
                    Track(value);
                    _added.Add(value);
                }
                Touch();
                return;
            }
            int addedIndex = checked(index - LiveSourceCount);
            Track(value);
            _added[addedIndex] = value;
            Touch();
        }
    }

    public void Add(DirectMidiNote item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Track(item);
        _added.Add(item);
        Touch();
    }

    public void AddRange(IEnumerable<DirectMidiNote> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (DirectMidiNote value in values) Add(value);
    }

    public void Clear()
    {
        _clearSource = _source is not null;
        _removed.Clear();
        _replacements.Clear();
        _materializedSourceIds.Clear();
        _sourceIndices.Clear();
        _added.Clear();
        Touch();
    }

    public bool Contains(DirectMidiNote item) => item is not null && IndexOf(item) >= 0;

    public void CopyTo(DirectMidiNote[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        foreach (DirectMidiNote value in this) array[arrayIndex++] = value;
    }

    public IEnumerator<DirectMidiNote> GetEnumerator()
    {
        if (!_clearSource && _source is not null)
        {
            for (int index = 0; index < _source.NoteCount; index++)
            {
                DirectMidiNoteValue value = _source.GetNote(index);
                if (_removed.Contains(value.Id)) continue;
                yield return _replacements.TryGetValue(value.Id, out DirectMidiNote? replacement)
                    ? replacement
                    : Materialize(value, index);
            }
        }
        foreach (DirectMidiNote value in _added) yield return value;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public int IndexOf(DirectMidiNote item)
    {
        if (item is null) return -1;
        int added = _added.FindIndex(value => value.Id == item.Id);
        if (added >= 0) return checked(LiveSourceCount + added);
        int sourceOrdinal = SourceIndexOf(item.Id);
        if (sourceOrdinal >= 0 && !_removed.Contains(item.Id))
        {
            return VisibleIndexForSourceIndex(sourceOrdinal);
        }
        return -1;
    }

    public int FindIndex(Predicate<DirectMidiNote> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        int index = 0;
        foreach (DirectMidiNote value in this)
        {
            if (match(value)) return index;
            index++;
        }
        return -1;
    }

    public bool TryGetById(MidoraId id, out DirectMidiNote? value)
    {
        if (_replacements.TryGetValue(id, out value)) return true;
        if (!_clearSource && !_removed.Contains(id) && _source is not null)
        {
            int index = _source.FindNoteIndex(id);
            if (index >= 0)
            {
                value = Materialize(_source.GetNote(index), index);
                return true;
            }
        }
        value = _added.FirstOrDefault(item => item.Id == id);
        return value is not null;
    }

    public IReadOnlyList<DirectMidiNoteMatch> ResolveByIds(
        IReadOnlyCollection<MidoraId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) return [];
        HashSet<MidoraId> requested = [.. ids];
        Dictionary<MidoraId, DirectMidiNoteMatch> matches = [];
        if (!_clearSource && _source is not null)
        {
            HashSet<MidoraId> sourceIds = [.. requested];
            sourceIds.ExceptWith(_removed);
            int[] removedIndices = _removed.Count == 0
                ? []
                : _removed
                    .Select(SourceIndexOf)
                    .Where(static value => value >= 0)
                    .Order()
                    .ToArray();
            foreach (DirectMidiNoteSourceMatch match in _source.QueryNotesByIds(sourceIds))
            {
                DirectMidiNote value = _replacements.TryGetValue(match.Value.Id, out DirectMidiNote? replacement)
                    ? replacement
                    : Materialize(match.Value, match.Index);
                matches[match.Value.Id] = new(
                    VisibleIndex(match.Index, removedIndices),
                    value);
            }
        }
        int addedBase = LiveSourceCount;
        for (int index = 0; index < _added.Count; index++)
        {
            DirectMidiNote value = _added[index];
            if (requested.Contains(value.Id))
                matches[value.Id] = new(checked(addedBase + index), value);
        }
        List<DirectMidiNoteMatch> result = new(matches.Count);
        HashSet<MidoraId> emitted = [];
        foreach (MidoraId id in ids)
        {
            if (emitted.Add(id) && matches.TryGetValue(id, out DirectMidiNoteMatch match))
                result.Add(match);
        }
        return result;
    }

    public IDisposable BeginBatchChange(IReadOnlyCollection<DirectMidiNote> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (_batchChangeDepth != 0)
            throw new InvalidOperationException("Direct MIDI Note batch changes cannot be nested.");
        _batchChangeDepth = 1;
        _batchChanged = false;
        if (!_clearSource && _source is not null && values.Count != 0)
        {
            _batchSourceIds = values
                .Select(static value => value.Id)
                .Where(id => _materializedSourceIds.Contains(id) && !_removed.Contains(id))
                .ToHashSet();
        }
        else
        {
            _batchSourceIds = [];
        }
        return new BatchChangeScope(this);
    }

    public void Insert(int index, DirectMidiNote item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if ((uint)index > (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        Track(item);
        int addedIndex = Math.Clamp(index - LiveSourceCount, 0, _added.Count);
        _added.Insert(addedIndex, item);
        Touch();
    }

    public bool Remove(DirectMidiNote item)
    {
        if (item is null) return false;
        int addedIndex = _added.FindIndex(value => value.Id == item.Id);
        if (addedIndex >= 0)
        {
            _added.RemoveAt(addedIndex);
            Touch();
            return true;
        }
        int sourceIndex = SourceIndexOf(item.Id);
        if (!_clearSource && sourceIndex >= 0 && !_removed.Contains(item.Id))
        {
            _removed.Add(item.Id);
            _replacements.Remove(item.Id);
            Touch();
            return true;
        }
        return false;
    }

    internal Action RemoveForExactCollision(DirectMidiNote item)
    {
        ArgumentNullException.ThrowIfNull(item);
        int addedIndex = _added.FindIndex(value => value.Id == item.Id);
        if (addedIndex >= 0)
        {
            _added.RemoveAt(addedIndex);
            Touch();
            return () =>
            {
                Track(item);
                _added.Insert(Math.Clamp(addedIndex, 0, _added.Count), item);
                Touch();
            };
        }

        int sourceIndex = SourceIndexOf(item.Id);
        if (_clearSource || sourceIndex < 0 || _removed.Contains(item.Id))
            throw new InvalidOperationException("The conflicting Direct MIDI Note is no longer present.");
        _replacements.TryGetValue(item.Id, out DirectMidiNote? replacement);
        _removed.Add(item.Id);
        _replacements.Remove(item.Id);
        Touch();
        return () =>
        {
            if (!_removed.Remove(item.Id))
                throw new InvalidOperationException("The conflicting Direct MIDI Note is already restored.");
            if (replacement is not null) _replacements[item.Id] = replacement;
            Touch();
        };
    }

    public void RemoveAt(int index) => Remove(this[index]);

    public IEnumerable<DirectMidiNote> Query(
        long startTick,
        long endTick,
        int minimumKey = 0,
        int maximumKey = 127)
    {
        if (endTick <= startTick || maximumKey < minimumKey) yield break;
        if (!_clearSource && _source is not null)
        {
            foreach (DirectMidiNoteValue sourceValue in _source.QueryNotes(
                startTick, endTick, minimumKey, maximumKey))
            {
                if (_removed.Contains(sourceValue.Id)) continue;
                if (_replacements.ContainsKey(sourceValue.Id)) continue;
                yield return Materialize(sourceValue);
            }
        }
        foreach (DirectMidiNote replacement in _replacements.Values)
        {
            if (Intersects(replacement, startTick, endTick, minimumKey, maximumKey))
                yield return replacement;
        }
        foreach (DirectMidiNote value in _added)
        {
            if (Intersects(value, startTick, endTick, minimumKey, maximumKey)) yield return value;
        }
    }

    public IEnumerable<DirectMidiNoteValue> QueryValues(
        long startTick,
        long endTick,
        int minimumKey = 0,
        int maximumKey = 127)
    {
        if (endTick <= startTick || maximumKey < minimumKey) yield break;
        if (!_clearSource && _source is not null)
        {
            foreach (DirectMidiNoteValue value in _source.QueryNotes(
                startTick, endTick, minimumKey, maximumKey))
            {
                if (_removed.Contains(value.Id) || _replacements.ContainsKey(value.Id)) continue;
                yield return value;
            }
        }
        foreach (DirectMidiNote value in _replacements.Values)
        {
            if (Intersects(value, startTick, endTick, minimumKey, maximumKey))
                yield return ToValue(value);
        }
        foreach (DirectMidiNote value in _added)
        {
            if (Intersects(value, startTick, endTick, minimumKey, maximumKey))
                yield return ToValue(value);
        }
    }

    public IEnumerable<PureMidiContentRangeSummary> GetOverviewRangeSummaries()
    {
        if (!_clearSource && _source is IPureMidiContentOverviewSource overviewSource)
        {
            foreach (PureMidiContentRangeSummary summary in overviewSource.GetNoteRangeSummaries())
                yield return summary;
        }
        else if (!_clearSource && _source is not null)
        {
            foreach (DirectMidiNoteValue value in _source.QueryNotes(0, long.MaxValue))
            {
                yield return new(value.StartTick, value.StartTick, 1);
            }
        }
        foreach (DirectMidiNote value in _replacements.Values.Concat(_added))
        {
            yield return new(value.StartTick, value.StartTick, 1);
        }
    }

    public void AccumulateOverviewColumns(long extent, Span<byte> destination)
    {
        PureMidiOverviewProjection.Validate(extent, destination);
        if (destination.IsEmpty) return;

        HashSet<MidoraId>? excludedIds = SourceExclusions();
        if (!_clearSource && _source is not null)
        {
            bool accumulated = _source is IPureMidiContentOverviewSource overviewSource
                && overviewSource.TryAccumulateNoteStartColumns(
                    extent,
                    destination,
                    excludedIds);
            if (!accumulated)
            {
                IEnumerable<DirectMidiNoteValue> sourceValues =
                    _source is IPureMidiPlaybackEndpointSource endpoints
                        ? endpoints.QueryNoteStarts(0, long.MaxValue)
                        : _source.QueryNotes(0, long.MaxValue);
                foreach (DirectMidiNoteValue value in sourceValues)
                {
                    if (excludedIds?.Contains(value.Id) == true) continue;
                    PureMidiOverviewProjection.Mark(destination, value.StartTick, extent);
                }
            }
        }

        foreach (DirectMidiNote value in _replacements.Values.Concat(_added))
            PureMidiOverviewProjection.Mark(destination, value.StartTick, extent);
    }

    public IEnumerable<DirectMidiNoteValue> QueryStartValues(
        long startTick,
        long endTick) => QueryEndpointValues(startTick, endTick, noteOn: true);

    public IEnumerable<DirectMidiNoteValue> QueryEndValues(
        long startTick,
        long endTick) => QueryEndpointValues(startTick, endTick, noteOn: false);

    public IEnumerable<DirectMidiNoteValue> QueryActiveValues(long tick)
    {
        if (tick < 0) yield break;
        IEnumerable<DirectMidiNoteValue> source = [];
        if (!_clearSource && _source is not null)
        {
            source = _source is IPureMidiPlaybackEndpointSource endpoints
                ? endpoints.QueryActiveNotes(tick)
                : _source.QueryNotes(tick, tick == long.MaxValue ? tick : tick + 1);
        }
        foreach (DirectMidiNoteValue value in source)
        {
            if (_removed.Contains(value.Id) || _replacements.ContainsKey(value.Id)) continue;
            if (IsActive(value, tick)) yield return value;
        }
        foreach (DirectMidiNote replacement in _replacements.Values)
        {
            DirectMidiNoteValue value = ToValue(replacement);
            if (IsActive(value, tick)) yield return value;
        }
        foreach (DirectMidiNote note in _added)
        {
            DirectMidiNoteValue value = ToValue(note);
            if (IsActive(value, tick)) yield return value;
        }
    }

    private IEnumerable<DirectMidiNoteValue> QueryEndpointValues(
        long startTick,
        long endTick,
        bool noteOn)
    {
        if (endTick <= startTick) yield break;
        IEnumerable<DirectMidiNoteValue> source = [];
        if (!_clearSource && _source is not null)
        {
            if (_source is IPureMidiPlaybackEndpointSource endpoints)
            {
                source = noteOn
                    ? endpoints.QueryNoteStarts(startTick, endTick)
                    : endpoints.QueryNoteEnds(startTick, endTick);
            }
            else
            {
                source = noteOn
                    ? _source.QueryNotes(startTick, endTick)
                    : _source.QueryNotes(0, endTick);
            }
        }

        IEnumerable<DirectMidiNoteValue> filteredSource = source.Where(value =>
            !_removed.Contains(value.Id)
            && !_replacements.ContainsKey(value.Id)
            && EndpointTick(value, noteOn) >= startTick
            && EndpointTick(value, noteOn) < endTick);
        IEnumerable<DirectMidiNoteValue> edited = _replacements.Values
            .Concat(_added)
            .Select(ToValue)
            .Where(value => EndpointTick(value, noteOn) >= startTick
                && EndpointTick(value, noteOn) < endTick)
            .OrderBy(value => EndpointTick(value, noteOn))
            .ThenBy(value => noteOn ? value.NoteOnOrder : value.NoteOffOrder)
            .ThenBy(value => value.Id);
        foreach (DirectMidiNoteValue value in MergeEndpoints(
            filteredSource,
            edited,
            noteOn))
        {
            yield return value;
        }
    }

    private static IEnumerable<DirectMidiNoteValue> MergeEndpoints(
        IEnumerable<DirectMidiNoteValue> left,
        IEnumerable<DirectMidiNoteValue> right,
        bool noteOn)
    {
        using IEnumerator<DirectMidiNoteValue> leftEnumerator = left.GetEnumerator();
        using IEnumerator<DirectMidiNoteValue> rightEnumerator = right.GetEnumerator();
        bool hasLeft = leftEnumerator.MoveNext();
        bool hasRight = rightEnumerator.MoveNext();
        while (hasLeft || hasRight)
        {
            bool takeLeft = !hasRight || hasLeft
                && CompareEndpoint(leftEnumerator.Current, rightEnumerator.Current, noteOn) <= 0;
            if (takeLeft)
            {
                yield return leftEnumerator.Current;
                hasLeft = leftEnumerator.MoveNext();
            }
            else
            {
                yield return rightEnumerator.Current;
                hasRight = rightEnumerator.MoveNext();
            }
        }
    }

    private static int CompareEndpoint(
        DirectMidiNoteValue left,
        DirectMidiNoteValue right,
        bool noteOn)
    {
        int result = EndpointTick(left, noteOn).CompareTo(EndpointTick(right, noteOn));
        if (result != 0) return result;
        result = (noteOn ? left.NoteOnOrder : left.NoteOffOrder)
            .CompareTo(noteOn ? right.NoteOnOrder : right.NoteOffOrder);
        return result != 0 ? result : left.Id.CompareTo(right.Id);
    }

    private static long EndpointTick(DirectMidiNoteValue value, bool noteOn) =>
        noteOn ? value.StartTick : SaturatingAdd(value.StartTick, value.LengthTicks);

    private static bool IsActive(DirectMidiNoteValue value, long tick) =>
        value.StartTick < tick && SaturatingAdd(value.StartTick, value.LengthTicks) > tick;

    private static long SaturatingAdd(long left, long right) =>
        right <= 0 || left > long.MaxValue - right ? long.MaxValue : left + right;

    internal void AttachSource(IPureMidiSegmentContentSource source)
    {
        if (_source is not null || _added.Count != 0)
            throw new InvalidOperationException("A paged source is already attached or records were added.");
        _source = source;
    }

    internal void CloneTo(DirectMidiNoteCollection target, CancellationToken cancellationToken)
    {
        if (_source is not null) target._source = _source;
        target._clearSource = _clearSource;
        target._removed.UnionWith(_removed);
        target._materializedSourceIds.UnionWith(_materializedSourceIds);
        foreach ((MidoraId id, int sourceIndex) in _sourceIndices)
            target._sourceIndices.Add(id, sourceIndex);
        foreach ((MidoraId id, DirectMidiNote value) in _replacements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            target._replacements.Add(id, Clone(target._project, value, target));
        }
        foreach (DirectMidiNote value in _added)
        {
            cancellationToken.ThrowIfCancellationRequested();
            target._added.Add(Clone(target._project, value, target));
        }
        target._generation = _generation;
    }

    void IDirectMidiNoteChangeSink.OnChanged(DirectMidiNote value)
    {
        bool sourceBacked = _batchChangeDepth != 0
            ? _batchSourceIds?.Contains(value.Id) == true && !_removed.Contains(value.Id)
            : !_clearSource
                && _materializedSourceIds.Contains(value.Id)
                && !_removed.Contains(value.Id);
        if (sourceBacked)
            _replacements[value.Id] = value;
        Touch();
    }

    private int LiveSourceCount => _source is null || _clearSource
        ? 0
        : checked(_source.NoteCount - _removed.Count);

    private int SourceIndexOf(MidoraId id)
    {
        if (_source is null || _clearSource) return -1;
        if (_sourceIndices.TryGetValue(id, out int cached)) return cached;
        int index = _source.FindNoteIndex(id);
        if (index >= 0) _sourceIndices[id] = index;
        return index;
    }

    private HashSet<MidoraId>? SourceExclusions()
    {
        if (_removed.Count == 0 && _replacements.Count == 0) return null;
        HashSet<MidoraId> result = [.. _removed];
        result.UnionWith(_replacements.Keys);
        return result;
    }

    private int FindSourceIndexForVisibleIndex(int visibleIndex)
    {
        if ((uint)visibleIndex >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(visibleIndex));
        int liveSourceCount = LiveSourceCount;
        if (visibleIndex >= liveSourceCount) return -1;
        if (_removed.Count == 0) return visibleIndex;
        int seen = 0;
        for (int sourceIndex = 0; sourceIndex < _source!.NoteCount; sourceIndex++)
        {
            if (_removed.Contains(_source.GetNote(sourceIndex).Id)) continue;
            if (seen++ == visibleIndex) return sourceIndex;
        }
        throw new InvalidOperationException("The paged Note index is inconsistent.");
    }

    private int VisibleIndexForSourceIndex(int sourceIndex)
    {
        if (_removed.Count == 0) return sourceIndex;
        int removedBefore = _removed.Count(id =>
        {
            int removedIndex = SourceIndexOf(id);
            return removedIndex >= 0 && removedIndex < sourceIndex;
        });
        return sourceIndex - removedBefore;
    }

    public IEnumerable<DirectMidiNote> QueryStartKeys(
        IReadOnlySet<DirectMidiNoteStartKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0) yield break;
        if (!_clearSource && _source is not null)
        {
            foreach (DirectMidiNoteSourceMatch match in _source.QueryNotesAtStarts(keys))
            {
                DirectMidiNoteValue sourceValue = match.Value;
                if (_removed.Contains(sourceValue.Id) || _replacements.ContainsKey(sourceValue.Id))
                    continue;
                yield return Materialize(sourceValue, match.Index);
            }
        }
        foreach (DirectMidiNote replacement in _replacements.Values)
        {
            if (keys.Contains(new(replacement.StartTick, replacement.Key)))
                yield return replacement;
        }
        foreach (DirectMidiNote value in _added)
        {
            if (keys.Contains(new(value.StartTick, value.Key))) yield return value;
        }
    }

    private static int VisibleIndex(int sourceIndex, int[] sortedRemovedIndices)
    {
        int low = 0;
        int high = sortedRemovedIndices.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (sortedRemovedIndices[middle] < sourceIndex) low = middle + 1;
            else high = middle;
        }
        return checked(sourceIndex - low);
    }

    private DirectMidiNote Materialize(DirectMidiNoteValue value, int sourceIndex = -1)
    {
        _materializedSourceIds.Add(value.Id);
        if (sourceIndex >= 0) _sourceIndices[value.Id] = sourceIndex;
        DirectMidiNote result = new(_project, value.Id)
        {
            StartTick = value.StartTick,
            LengthTicks = value.LengthTicks,
            Key = value.Key,
            NoteOnVelocity = value.NoteOnVelocity,
            NoteOffVelocity = value.NoteOffVelocity,
            NoteOnOrder = value.NoteOnOrder,
            NoteOffOrder = value.NoteOffOrder
        };
        Track(result);
        return result;
    }

    private void Track(DirectMidiNote value) => value.SetChangeSink(this);
    private void Touch()
    {
        if (_batchChangeDepth != 0)
        {
            _batchChanged = true;
            return;
        }
        _generation++;
    }

    private void EndBatchChange()
    {
        if (_batchChangeDepth != 1)
            throw new InvalidOperationException("No Direct MIDI Note batch change is active.");
        _batchChangeDepth = 0;
        _batchSourceIds = null;
        if (_batchChanged) _generation++;
        _batchChanged = false;
    }

    private sealed class BatchChangeScope(DirectMidiNoteCollection owner) : IDisposable
    {
        private DirectMidiNoteCollection? _owner = owner;

        public void Dispose()
        {
            DirectMidiNoteCollection? value = Interlocked.Exchange(ref _owner, null);
            value?.EndBatchChange();
        }
    }

    private static bool Intersects(
        DirectMidiNote value,
        long startTick,
        long endTick,
        int minimumKey,
        int maximumKey) => value.StartTick < endTick
        && value.StartTick <= long.MaxValue - Math.Max(1, value.LengthTicks)
        && value.StartTick + Math.Max(1, value.LengthTicks) > startTick
        && value.Key >= minimumKey
        && value.Key <= maximumKey;

    private static DirectMidiNoteValue ToValue(DirectMidiNote value) => new(
        value.Id,
        value.StartTick,
        value.LengthTicks,
        value.Key,
        value.NoteOnVelocity,
        value.NoteOffVelocity,
        value.NoteOnOrder,
        value.NoteOffOrder);

    private static DirectMidiNote Clone(
        MidoraProject project,
        DirectMidiNote source,
        IDirectMidiNoteChangeSink sink)
    {
        DirectMidiNote result = new(project, source.Id)
        {
            StartTick = source.StartTick,
            LengthTicks = source.LengthTicks,
            Key = source.Key,
            NoteOnVelocity = source.NoteOnVelocity,
            NoteOffVelocity = source.NoteOffVelocity,
            NoteOnOrder = source.NoteOnOrder,
            NoteOffOrder = source.NoteOffOrder
        };
        result.SetChangeSink(sink);
        return result;
    }
}

public sealed class DirectMidiChannelEventCollection : IList<DirectMidiChannelEvent>, IReadOnlyList<DirectMidiChannelEvent>, IDirectMidiChannelEventChangeSink
{
    private readonly MidoraProject _project;
    private readonly List<DirectMidiChannelEvent> _added = [];
    private readonly Dictionary<MidoraId, DirectMidiChannelEvent> _replacements = [];
    private readonly HashSet<MidoraId> _removed = [];
    private readonly HashSet<MidoraId> _materializedSourceIds = [];
    private readonly Dictionary<MidoraId, int> _sourceIndices = [];
    private IPureMidiSegmentContentSource? _source;
    private bool _clearSource;
    private long _generation;
    private int _batchChangeDepth;
    private bool _batchChanged;
    private HashSet<MidoraId>? _batchSourceIds;

    internal DirectMidiChannelEventCollection(MidoraProject project) => _project = project;

    public int Count => checked(LiveSourceCount + _added.Count);
    public bool IsReadOnly => false;
    public bool HasPagedSource => _source is not null;
    public long Generation => _generation;
    internal IEnumerable<DirectMidiChannelEvent> EditedItems => _replacements.Values.Concat(_added);
    internal IReadOnlyCollection<MidoraId> RemovedSourceIds => _removed;
    internal bool ClearsPagedSource => _clearSource;
    internal IPureMidiSegmentContentSource? PagedSource => _source;
    internal bool IsPristinePagedSource => _source is not null
        && !_clearSource
        && _removed.Count == 0
        && _replacements.Count == 0
        && _added.Count == 0;

    public DirectMidiChannelEvent this[int index]
    {
        get
        {
            int sourceIndex = FindSourceIndexForVisibleIndex(index);
            if (sourceIndex >= 0)
            {
                DirectMidiChannelEventValue value = _source!.GetChannelEvent(sourceIndex);
                return _replacements.TryGetValue(value.Id, out DirectMidiChannelEvent? replacement)
                    ? replacement
                    : Materialize(value, sourceIndex);
            }
            return _added[checked(index - LiveSourceCount)];
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            int sourceIndex = FindSourceIndexForVisibleIndex(index);
            if (sourceIndex >= 0)
            {
                DirectMidiChannelEventValue original = _source!.GetChannelEvent(sourceIndex);
                if (value.Id == original.Id)
                {
                    Track(value);
                    _sourceIndices[original.Id] = sourceIndex;
                    _replacements[original.Id] = value;
                }
                else
                {
                    _removed.Add(original.Id);
                    Track(value);
                    _added.Add(value);
                }
                Touch();
                return;
            }
            int addedIndex = checked(index - LiveSourceCount);
            Track(value);
            _added[addedIndex] = value;
            Touch();
        }
    }

    public void Add(DirectMidiChannelEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Track(item);
        _added.Add(item);
        Touch();
    }

    public void AddRange(IEnumerable<DirectMidiChannelEvent> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (DirectMidiChannelEvent value in values) Add(value);
    }

    public void Clear()
    {
        _clearSource = _source is not null;
        _removed.Clear();
        _replacements.Clear();
        _materializedSourceIds.Clear();
        _sourceIndices.Clear();
        _added.Clear();
        Touch();
    }

    public bool Contains(DirectMidiChannelEvent item) => item is not null && IndexOf(item) >= 0;
    public void CopyTo(DirectMidiChannelEvent[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        foreach (DirectMidiChannelEvent value in this) array[arrayIndex++] = value;
    }

    public IEnumerator<DirectMidiChannelEvent> GetEnumerator()
    {
        if (!_clearSource && _source is not null)
        {
            for (int index = 0; index < _source.ChannelEventCount; index++)
            {
                DirectMidiChannelEventValue value = _source.GetChannelEvent(index);
                if (_removed.Contains(value.Id)) continue;
                yield return _replacements.TryGetValue(value.Id, out DirectMidiChannelEvent? replacement)
                    ? replacement
                    : Materialize(value, index);
            }
        }
        foreach (DirectMidiChannelEvent value in _added) yield return value;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public int IndexOf(DirectMidiChannelEvent item)
    {
        if (item is null) return -1;
        int added = _added.FindIndex(value => value.Id == item.Id);
        if (added >= 0) return checked(LiveSourceCount + added);
        int sourceOrdinal = SourceIndexOf(item.Id);
        if (sourceOrdinal >= 0 && !_removed.Contains(item.Id)) return VisibleIndexForSourceIndex(sourceOrdinal);
        return -1;
    }

    public int FindIndex(Predicate<DirectMidiChannelEvent> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        int index = 0;
        foreach (DirectMidiChannelEvent value in this)
        {
            if (match(value)) return index;
            index++;
        }
        return -1;
    }

    public bool TryGetById(MidoraId id, out DirectMidiChannelEvent? value)
    {
        if (_replacements.TryGetValue(id, out value)) return true;
        if (!_clearSource && !_removed.Contains(id) && _source is not null)
        {
            int index = _source.FindChannelEventIndex(id);
            if (index >= 0)
            {
                value = Materialize(_source.GetChannelEvent(index), index);
                return true;
            }
        }
        value = _added.FirstOrDefault(item => item.Id == id);
        return value is not null;
    }

    public IReadOnlyList<DirectMidiChannelEventMatch> ResolveByIds(
        IReadOnlyCollection<MidoraId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) return [];
        HashSet<MidoraId> requested = [.. ids];
        Dictionary<MidoraId, DirectMidiChannelEventMatch> matches = [];
        if (!_clearSource && _source is not null)
        {
            HashSet<MidoraId> sourceIds = [.. requested];
            sourceIds.ExceptWith(_removed);
            int[] removedIndices = _removed.Count == 0
                ? []
                : _removed
                    .Select(SourceIndexOf)
                    .Where(static value => value >= 0)
                    .Order()
                    .ToArray();
            foreach (DirectMidiChannelEventSourceMatch match in
                _source.QueryChannelEventsByIds(sourceIds))
            {
                DirectMidiChannelEvent value = _replacements.TryGetValue(
                    match.Value.Id,
                    out DirectMidiChannelEvent? replacement)
                        ? replacement
                        : Materialize(match.Value, match.Index);
                matches[match.Value.Id] = new(
                    VisibleIndex(match.Index, removedIndices),
                    value);
            }
        }
        int addedBase = LiveSourceCount;
        for (int index = 0; index < _added.Count; index++)
        {
            DirectMidiChannelEvent value = _added[index];
            if (requested.Contains(value.Id))
                matches[value.Id] = new(checked(addedBase + index), value);
        }
        List<DirectMidiChannelEventMatch> result = new(matches.Count);
        HashSet<MidoraId> emitted = [];
        foreach (MidoraId id in ids)
        {
            if (emitted.Add(id)
                && matches.TryGetValue(id, out DirectMidiChannelEventMatch match))
            {
                result.Add(match);
            }
        }
        return result;
    }

    public IDisposable BeginBatchChange(IReadOnlyCollection<DirectMidiChannelEvent> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (_batchChangeDepth != 0)
            throw new InvalidOperationException("Direct MIDI Event batch changes cannot be nested.");
        _batchChangeDepth = 1;
        _batchChanged = false;
        _batchSourceIds = values
            .Select(static value => value.Id)
            .Where(id => _materializedSourceIds.Contains(id) && !_removed.Contains(id))
            .ToHashSet();
        return new BatchChangeScope(this);
    }

    public void Insert(int index, DirectMidiChannelEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if ((uint)index > (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        Track(item);
        _added.Insert(Math.Clamp(index - LiveSourceCount, 0, _added.Count), item);
        Touch();
    }

    public bool Remove(DirectMidiChannelEvent item)
    {
        if (item is null) return false;
        int addedIndex = _added.FindIndex(value => value.Id == item.Id);
        if (addedIndex >= 0)
        {
            _added.RemoveAt(addedIndex);
            Touch();
            return true;
        }
        int sourceIndex = SourceIndexOf(item.Id);
        if (!_clearSource && sourceIndex >= 0 && !_removed.Contains(item.Id))
        {
            _removed.Add(item.Id);
            _replacements.Remove(item.Id);
            Touch();
            return true;
        }
        return false;
    }

    internal Action RemoveForExactCollision(DirectMidiChannelEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        int addedIndex = _added.FindIndex(value => value.Id == item.Id);
        if (addedIndex >= 0)
        {
            _added.RemoveAt(addedIndex);
            Touch();
            return () =>
            {
                Track(item);
                _added.Insert(Math.Clamp(addedIndex, 0, _added.Count), item);
                Touch();
            };
        }

        int sourceIndex = SourceIndexOf(item.Id);
        if (_clearSource || sourceIndex < 0 || _removed.Contains(item.Id))
            throw new InvalidOperationException("The conflicting Direct MIDI Event is no longer present.");
        _replacements.TryGetValue(item.Id, out DirectMidiChannelEvent? replacement);
        _removed.Add(item.Id);
        _replacements.Remove(item.Id);
        Touch();
        return () =>
        {
            if (!_removed.Remove(item.Id))
                throw new InvalidOperationException("The conflicting Direct MIDI Event is already restored.");
            if (replacement is not null) _replacements[item.Id] = replacement;
            Touch();
        };
    }

    public void RemoveAt(int index) => Remove(this[index]);

    public IEnumerable<DirectMidiChannelEvent> Query(long startTick, long endTick)
    {
        if (endTick <= startTick) yield break;
        if (!_clearSource && _source is not null)
        {
            foreach (DirectMidiChannelEventValue sourceValue in _source.QueryChannelEvents(startTick, endTick))
            {
                if (_removed.Contains(sourceValue.Id) || _replacements.ContainsKey(sourceValue.Id)) continue;
                yield return Materialize(sourceValue);
            }
        }
        foreach (DirectMidiChannelEvent value in _replacements.Values)
            if (value.Tick >= startTick && value.Tick < endTick) yield return value;
        foreach (DirectMidiChannelEvent value in _added)
            if (value.Tick >= startTick && value.Tick < endTick) yield return value;
    }

    public IEnumerable<DirectMidiChannelEvent> QueryStartKeys(
        IReadOnlySet<DirectMidiEventStartKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0) yield break;
        if (!_clearSource && _source is not null)
        {
            foreach (DirectMidiChannelEventSourceMatch match in
                _source.QueryChannelEventsAtStarts(keys))
            {
                DirectMidiChannelEventValue sourceValue = match.Value;
                if (_removed.Contains(sourceValue.Id) || _replacements.ContainsKey(sourceValue.Id))
                    continue;
                yield return Materialize(sourceValue, match.Index);
            }
        }
        foreach (DirectMidiChannelEvent replacement in _replacements.Values)
        {
            if (keys.Contains(StartKey(replacement))) yield return replacement;
        }
        foreach (DirectMidiChannelEvent value in _added)
        {
            if (keys.Contains(StartKey(value))) yield return value;
        }
    }

    public IEnumerable<DirectMidiChannelEventValue> QueryValues(long startTick, long endTick)
    {
        if (endTick <= startTick) yield break;
        if (!_clearSource && _source is not null)
        {
            foreach (DirectMidiChannelEventValue value in _source.QueryChannelEvents(startTick, endTick))
            {
                if (_removed.Contains(value.Id) || _replacements.ContainsKey(value.Id)) continue;
                yield return value;
            }
        }
        foreach (DirectMidiChannelEvent value in _replacements.Values)
        {
            if (value.Tick >= startTick && value.Tick < endTick) yield return ToValue(value);
        }
        foreach (DirectMidiChannelEvent value in _added)
        {
            if (value.Tick >= startTick && value.Tick < endTick) yield return ToValue(value);
        }
    }

    public IEnumerable<PureMidiContentRangeSummary> GetOverviewRangeSummaries()
    {
        if (!_clearSource && _source is IPureMidiContentOverviewSource overviewSource)
        {
            foreach (PureMidiContentRangeSummary summary in overviewSource.GetChannelEventRangeSummaries())
                yield return summary;
        }
        else if (!_clearSource && _source is not null)
        {
            foreach (DirectMidiChannelEventValue value in _source.QueryChannelEvents(0, long.MaxValue))
                yield return new(value.Tick, value.Tick, 1);
        }
        foreach (DirectMidiChannelEvent value in _replacements.Values.Concat(_added))
            yield return new(value.Tick, value.Tick, 1);
    }

    public void AccumulateOverviewColumns(
        long extent,
        Span<byte> noteStartColumns,
        Span<byte> eventColumns)
    {
        PureMidiOverviewProjection.Validate(extent, noteStartColumns, eventColumns);
        if (noteStartColumns.IsEmpty) return;

        HashSet<MidoraId>? excludedIds = SourceExclusions();
        if (!_clearSource && _source is not null)
        {
            bool accumulated = _source is IPureMidiContentOverviewSource overviewSource
                && overviewSource.TryAccumulateChannelEventColumns(
                    extent,
                    noteStartColumns,
                    eventColumns,
                    excludedIds);
            if (!accumulated)
            {
                foreach (DirectMidiChannelEventValue value in
                    _source.QueryChannelEvents(0, long.MaxValue))
                {
                    if (excludedIds?.Contains(value.Id) == true) continue;
                    PureMidiOverviewProjection.Mark(
                        value.Kind,
                        value.Data2,
                        value.Tick,
                        extent,
                        noteStartColumns,
                        eventColumns);
                }
            }
        }

        foreach (DirectMidiChannelEvent value in _replacements.Values.Concat(_added))
        {
            PureMidiOverviewProjection.Mark(
                value.Kind,
                value.Data2,
                value.Tick,
                extent,
                noteStartColumns,
                eventColumns);
        }
    }

    public IEnumerable<DirectMidiChannelEventValue> QueryOrderedValues(
        long startTick,
        long endTick)
    {
        if (endTick <= startTick) yield break;
        IEnumerable<DirectMidiChannelEventValue> source = [];
        if (!_clearSource && _source is not null)
        {
            source = _source is IPureMidiPlaybackEndpointSource endpoints
                ? endpoints.QueryOrderedChannelEvents(startTick, endTick)
                : _source.QueryChannelEvents(startTick, endTick)
                    .OrderBy(value => value.Tick)
                    .ThenBy(value => value.Order)
                    .ThenBy(value => value.Id);
        }
        source = source.Where(value =>
            !_removed.Contains(value.Id) && !_replacements.ContainsKey(value.Id));
        IEnumerable<DirectMidiChannelEventValue> edited = _replacements.Values
            .Concat(_added)
            .Select(ToValue)
            .Where(value => value.Tick >= startTick && value.Tick < endTick)
            .OrderBy(value => value.Tick)
            .ThenBy(value => value.Order)
            .ThenBy(value => value.Id);

        using IEnumerator<DirectMidiChannelEventValue> left = source.GetEnumerator();
        using IEnumerator<DirectMidiChannelEventValue> right = edited.GetEnumerator();
        bool hasLeft = left.MoveNext();
        bool hasRight = right.MoveNext();
        while (hasLeft || hasRight)
        {
            bool takeLeft = !hasRight || hasLeft
                && Compare(left.Current, right.Current) <= 0;
            if (takeLeft)
            {
                yield return left.Current;
                hasLeft = left.MoveNext();
            }
            else
            {
                yield return right.Current;
                hasRight = right.MoveNext();
            }
        }

        static int Compare(
            DirectMidiChannelEventValue left,
            DirectMidiChannelEventValue right)
        {
            int result = left.Tick.CompareTo(right.Tick);
            if (result != 0) return result;
            result = left.Order.CompareTo(right.Order);
            return result != 0 ? result : left.Id.CompareTo(right.Id);
        }
    }

    internal void AttachSource(IPureMidiSegmentContentSource source)
    {
        if (_source is not null || _added.Count != 0)
            throw new InvalidOperationException("A paged source is already attached or records were added.");
        _source = source;
    }

    internal void CloneTo(DirectMidiChannelEventCollection target, CancellationToken cancellationToken)
    {
        if (_source is not null) target._source = _source;
        target._clearSource = _clearSource;
        target._removed.UnionWith(_removed);
        target._materializedSourceIds.UnionWith(_materializedSourceIds);
        foreach ((MidoraId id, int sourceIndex) in _sourceIndices)
            target._sourceIndices.Add(id, sourceIndex);
        foreach ((MidoraId id, DirectMidiChannelEvent value) in _replacements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            target._replacements.Add(id, Clone(target._project, value, target));
        }
        foreach (DirectMidiChannelEvent value in _added)
        {
            cancellationToken.ThrowIfCancellationRequested();
            target._added.Add(Clone(target._project, value, target));
        }
        target._generation = _generation;
    }

    void IDirectMidiChannelEventChangeSink.OnChanged(DirectMidiChannelEvent value)
    {
        bool sourceBacked = _batchChangeDepth != 0
            ? _batchSourceIds?.Contains(value.Id) == true && !_removed.Contains(value.Id)
            : !_clearSource
                && _materializedSourceIds.Contains(value.Id)
                && !_removed.Contains(value.Id);
        if (sourceBacked)
            _replacements[value.Id] = value;
        Touch();
    }

    private int LiveSourceCount => _source is null || _clearSource
        ? 0
        : checked(_source.ChannelEventCount - _removed.Count);
    private int SourceIndexOf(MidoraId id)
    {
        if (_source is null || _clearSource) return -1;
        if (_sourceIndices.TryGetValue(id, out int cached)) return cached;
        int index = _source.FindChannelEventIndex(id);
        if (index >= 0) _sourceIndices[id] = index;
        return index;
    }

    private HashSet<MidoraId>? SourceExclusions()
    {
        if (_removed.Count == 0 && _replacements.Count == 0) return null;
        HashSet<MidoraId> result = [.. _removed];
        result.UnionWith(_replacements.Keys);
        return result;
    }

    private int FindSourceIndexForVisibleIndex(int visibleIndex)
    {
        if ((uint)visibleIndex >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(visibleIndex));
        if (visibleIndex >= LiveSourceCount) return -1;
        if (_removed.Count == 0) return visibleIndex;
        int seen = 0;
        for (int sourceIndex = 0; sourceIndex < _source!.ChannelEventCount; sourceIndex++)
        {
            if (_removed.Contains(_source.GetChannelEvent(sourceIndex).Id)) continue;
            if (seen++ == visibleIndex) return sourceIndex;
        }
        throw new InvalidOperationException("The paged event index is inconsistent.");
    }

    private int VisibleIndexForSourceIndex(int sourceIndex)
    {
        if (_removed.Count == 0) return sourceIndex;
        int removedBefore = _removed.Count(id =>
        {
            int removedIndex = SourceIndexOf(id);
            return removedIndex >= 0 && removedIndex < sourceIndex;
        });
        return sourceIndex - removedBefore;
    }

    private static int VisibleIndex(int sourceIndex, int[] sortedRemovedIndices)
    {
        int low = 0;
        int high = sortedRemovedIndices.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (sortedRemovedIndices[middle] < sourceIndex) low = middle + 1;
            else high = middle;
        }
        return checked(sourceIndex - low);
    }

    private static DirectMidiEventStartKey StartKey(DirectMidiChannelEvent value) => new(
        value.Tick,
        value.Kind,
        value.Kind is DirectMidiChannelEventKind.ControlChange
            or DirectMidiChannelEventKind.PolyphonicKeyPressure
            or DirectMidiChannelEventKind.NoteOn
            or DirectMidiChannelEventKind.NoteOff
                ? value.Data1
                : 0);

    private DirectMidiChannelEvent Materialize(
        DirectMidiChannelEventValue value,
        int sourceIndex = -1)
    {
        _materializedSourceIds.Add(value.Id);
        if (sourceIndex >= 0) _sourceIndices[value.Id] = sourceIndex;
        DirectMidiChannelEvent result = new(_project, value.Id)
        {
            Tick = value.Tick,
            Kind = value.Kind,
            Data1 = value.Data1,
            Data2 = value.Data2,
            Order = value.Order
        };
        Track(result);
        return result;
    }

    private void Track(DirectMidiChannelEvent value) => value.SetChangeSink(this);
    private void Touch()
    {
        if (_batchChangeDepth != 0)
        {
            _batchChanged = true;
            return;
        }
        _generation++;
    }

    private void EndBatchChange()
    {
        if (_batchChangeDepth != 1)
            throw new InvalidOperationException("No Direct MIDI Event batch change is active.");
        _batchChangeDepth = 0;
        _batchSourceIds = null;
        if (_batchChanged) _generation++;
        _batchChanged = false;
    }

    private sealed class BatchChangeScope(DirectMidiChannelEventCollection owner) : IDisposable
    {
        private DirectMidiChannelEventCollection? _owner = owner;

        public void Dispose()
        {
            DirectMidiChannelEventCollection? value = Interlocked.Exchange(ref _owner, null);
            value?.EndBatchChange();
        }
    }

    private static DirectMidiChannelEventValue ToValue(DirectMidiChannelEvent value) => new(
        value.Id,
        value.Tick,
        value.Kind,
        value.Data1,
        value.Data2,
        value.Order);

    private static DirectMidiChannelEvent Clone(
        MidoraProject project,
        DirectMidiChannelEvent source,
        IDirectMidiChannelEventChangeSink sink)
    {
        DirectMidiChannelEvent result = new(project, source.Id)
        {
            Tick = source.Tick,
            Kind = source.Kind,
            Data1 = source.Data1,
            Data2 = source.Data2,
            Order = source.Order
        };
        result.SetChangeSink(sink);
        return result;
    }
}

public sealed class OpaqueMidiEventCollection : IList<OpaqueMidiEvent>, IReadOnlyList<OpaqueMidiEvent>, IOpaqueMidiEventChangeSink
{
    private readonly MidoraProject _project;
    private readonly List<OpaqueMidiEvent> _added = [];
    private readonly Dictionary<MidoraId, OpaqueMidiEvent> _replacements = [];
    private readonly HashSet<MidoraId> _removed = [];
    private readonly Dictionary<MidoraId, int> _sourceIndices = [];
    private IPureMidiSegmentContentSource? _source;
    private bool _clearSource;
    private long _generation;

    internal OpaqueMidiEventCollection(MidoraProject project) => _project = project;

    public int Count => checked(LiveSourceCount + _added.Count);
    public bool IsReadOnly => false;
    public bool HasPagedSource => _source is not null;
    public long Generation => _generation;
    internal IEnumerable<OpaqueMidiEvent> EditedItems => _replacements.Values.Concat(_added);
    internal IReadOnlyCollection<MidoraId> RemovedSourceIds => _removed;
    internal bool ClearsPagedSource => _clearSource;
    internal IPureMidiSegmentContentSource? PagedSource => _source;
    internal bool IsPristinePagedSource => _source is not null
        && !_clearSource
        && _removed.Count == 0
        && _replacements.Count == 0
        && _added.Count == 0;

    public OpaqueMidiEvent this[int index]
    {
        get
        {
            int sourceIndex = FindSourceIndexForVisibleIndex(index);
            if (sourceIndex >= 0)
            {
                OpaqueMidiEventValue value = _source!.GetOpaqueEvent(sourceIndex);
                return _replacements.TryGetValue(value.Id, out OpaqueMidiEvent? replacement)
                    ? replacement
                    : Materialize(value, sourceIndex);
            }
            return _added[checked(index - LiveSourceCount)];
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            int sourceIndex = FindSourceIndexForVisibleIndex(index);
            if (sourceIndex >= 0)
            {
                OpaqueMidiEventValue original = _source!.GetOpaqueEvent(sourceIndex);
                if (value.Id == original.Id)
                {
                    Track(value);
                    _sourceIndices[original.Id] = sourceIndex;
                    _replacements[original.Id] = value;
                }
                else
                {
                    _removed.Add(original.Id);
                    Track(value);
                    _added.Add(value);
                }
                Touch();
                return;
            }
            int addedIndex = checked(index - LiveSourceCount);
            Track(value);
            _added[addedIndex] = value;
            Touch();
        }
    }

    public void Add(OpaqueMidiEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Track(item);
        _added.Add(item);
        Touch();
    }

    public void AddRange(IEnumerable<OpaqueMidiEvent> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (OpaqueMidiEvent value in values) Add(value);
    }

    public void Clear()
    {
        _clearSource = _source is not null;
        _removed.Clear();
        _replacements.Clear();
        _sourceIndices.Clear();
        _added.Clear();
        Touch();
    }

    public bool Contains(OpaqueMidiEvent item) => item is not null && IndexOf(item) >= 0;
    public void CopyTo(OpaqueMidiEvent[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        foreach (OpaqueMidiEvent value in this) array[arrayIndex++] = value;
    }

    public IEnumerator<OpaqueMidiEvent> GetEnumerator()
    {
        if (!_clearSource && _source is not null)
        {
            for (int index = 0; index < _source.OpaqueEventCount; index++)
            {
                OpaqueMidiEventValue value = _source.GetOpaqueEvent(index);
                if (_removed.Contains(value.Id)) continue;
                yield return _replacements.TryGetValue(value.Id, out OpaqueMidiEvent? replacement)
                    ? replacement
                    : Materialize(value, index);
            }
        }
        foreach (OpaqueMidiEvent value in _added) yield return value;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public int IndexOf(OpaqueMidiEvent item)
    {
        if (item is null) return -1;
        int added = _added.FindIndex(value => value.Id == item.Id);
        if (added >= 0) return checked(LiveSourceCount + added);
        int sourceOrdinal = SourceIndexOf(item.Id);
        if (sourceOrdinal >= 0 && !_removed.Contains(item.Id)) return VisibleIndexForSourceIndex(sourceOrdinal);
        return -1;
    }

    public int FindIndex(Predicate<OpaqueMidiEvent> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        int index = 0;
        foreach (OpaqueMidiEvent value in this)
        {
            if (match(value)) return index;
            index++;
        }
        return -1;
    }

    public bool TryGetById(MidoraId id, out OpaqueMidiEvent? value)
    {
        if (_replacements.TryGetValue(id, out value)) return true;
        if (!_clearSource && !_removed.Contains(id) && _source is not null)
        {
            int index = _source.FindOpaqueEventIndex(id);
            if (index >= 0)
            {
                value = Materialize(_source.GetOpaqueEvent(index), index);
                return true;
            }
        }
        value = _added.FirstOrDefault(item => item.Id == id);
        return value is not null;
    }

    public IReadOnlyList<OpaqueMidiEventMatch> ResolveByIds(
        IReadOnlyCollection<MidoraId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) return [];
        HashSet<MidoraId> requested = [.. ids];
        Dictionary<MidoraId, OpaqueMidiEventMatch> matches = [];
        if (!_clearSource && _source is not null)
        {
            HashSet<MidoraId> sourceIds = [.. requested];
            sourceIds.ExceptWith(_removed);
            int[] removedIndices = _removed.Count == 0
                ? []
                : _removed
                    .Select(SourceIndexOf)
                    .Where(static value => value >= 0)
                    .Order()
                    .ToArray();
            foreach (OpaqueMidiEventSourceMatch match in _source.QueryOpaqueEventsByIds(sourceIds))
            {
                OpaqueMidiEvent value = _replacements.TryGetValue(match.Value.Id, out OpaqueMidiEvent? replacement)
                    ? replacement
                    : Materialize(match.Value, match.Index);
                matches[match.Value.Id] = new(
                    VisibleIndex(match.Index, removedIndices),
                    value);
            }
        }
        int addedBase = LiveSourceCount;
        for (int index = 0; index < _added.Count; index++)
        {
            OpaqueMidiEvent value = _added[index];
            if (requested.Contains(value.Id))
                matches[value.Id] = new(checked(addedBase + index), value);
        }
        List<OpaqueMidiEventMatch> result = new(matches.Count);
        HashSet<MidoraId> emitted = [];
        foreach (MidoraId id in ids)
        {
            if (emitted.Add(id) && matches.TryGetValue(id, out OpaqueMidiEventMatch match))
                result.Add(match);
        }
        return result;
    }

    public void Insert(int index, OpaqueMidiEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if ((uint)index > (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        Track(item);
        _added.Insert(Math.Clamp(index - LiveSourceCount, 0, _added.Count), item);
        Touch();
    }

    public bool Remove(OpaqueMidiEvent item)
    {
        if (item is null) return false;
        int addedIndex = _added.FindIndex(value => value.Id == item.Id);
        if (addedIndex >= 0)
        {
            _added.RemoveAt(addedIndex);
            Touch();
            return true;
        }
        int sourceIndex = SourceIndexOf(item.Id);
        if (!_clearSource && sourceIndex >= 0 && !_removed.Contains(item.Id))
        {
            _removed.Add(item.Id);
            _replacements.Remove(item.Id);
            Touch();
            return true;
        }
        return false;
    }

    public void RemoveAt(int index) => Remove(this[index]);

    public IEnumerable<OpaqueMidiEvent> Query(long startTick, long endTick)
    {
        if (endTick <= startTick) yield break;
        if (!_clearSource && _source is not null)
        {
            foreach (OpaqueMidiEventValue sourceValue in _source.QueryOpaqueEvents(startTick, endTick))
            {
                if (_removed.Contains(sourceValue.Id) || _replacements.ContainsKey(sourceValue.Id)) continue;
                yield return Materialize(sourceValue);
            }
        }
        foreach (OpaqueMidiEvent value in _replacements.Values)
            if (value.Tick >= startTick && value.Tick < endTick) yield return value;
        foreach (OpaqueMidiEvent value in _added)
            if (value.Tick >= startTick && value.Tick < endTick) yield return value;
    }

    public IEnumerable<OpaqueMidiEventValue> QueryValues(long startTick, long endTick)
    {
        if (endTick <= startTick) yield break;
        if (!_clearSource && _source is not null)
        {
            foreach (OpaqueMidiEventValue value in _source.QueryOpaqueEvents(startTick, endTick))
            {
                if (_removed.Contains(value.Id) || _replacements.ContainsKey(value.Id)) continue;
                yield return value;
            }
        }
        foreach (OpaqueMidiEvent value in _replacements.Values)
        {
            if (value.Tick >= startTick && value.Tick < endTick) yield return ToValue(value);
        }
        foreach (OpaqueMidiEvent value in _added)
        {
            if (value.Tick >= startTick && value.Tick < endTick) yield return ToValue(value);
        }
    }

    public IEnumerable<PureMidiContentRangeSummary> GetOverviewRangeSummaries()
    {
        if (!_clearSource && _source is IPureMidiContentOverviewSource overviewSource)
        {
            foreach (PureMidiContentRangeSummary summary in overviewSource.GetOpaqueEventRangeSummaries())
                yield return summary;
        }
        else if (!_clearSource && _source is not null)
        {
            foreach (OpaqueMidiEventValue value in _source.QueryOpaqueEvents(0, long.MaxValue))
                yield return new(value.Tick, value.Tick, 1);
        }
        foreach (OpaqueMidiEvent value in _replacements.Values.Concat(_added))
            yield return new(value.Tick, value.Tick, 1);
    }

    public void AccumulateOverviewColumns(long extent, Span<byte> destination)
    {
        PureMidiOverviewProjection.Validate(extent, destination);
        if (destination.IsEmpty) return;

        HashSet<MidoraId>? excludedIds = SourceExclusions();
        if (!_clearSource && _source is not null)
        {
            bool accumulated = _source is IPureMidiContentOverviewSource overviewSource
                && overviewSource.TryAccumulateOpaqueEventColumns(
                    extent,
                    destination,
                    excludedIds);
            if (!accumulated)
            {
                foreach (OpaqueMidiEventValue value in
                    _source.QueryOpaqueEvents(0, long.MaxValue))
                {
                    if (excludedIds?.Contains(value.Id) == true) continue;
                    PureMidiOverviewProjection.Mark(destination, value.Tick, extent);
                }
            }
        }

        foreach (OpaqueMidiEvent value in _replacements.Values.Concat(_added))
            PureMidiOverviewProjection.Mark(destination, value.Tick, extent);
    }

    internal void AttachSource(IPureMidiSegmentContentSource source)
    {
        if (_source is not null || _added.Count != 0)
            throw new InvalidOperationException("A paged source is already attached or records were added.");
        _source = source;
    }

    internal void CloneTo(OpaqueMidiEventCollection target, CancellationToken cancellationToken)
    {
        if (_source is not null) target._source = _source;
        target._clearSource = _clearSource;
        target._removed.UnionWith(_removed);
        foreach ((MidoraId id, int sourceIndex) in _sourceIndices)
            target._sourceIndices.Add(id, sourceIndex);
        foreach ((MidoraId id, OpaqueMidiEvent value) in _replacements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            target._replacements.Add(id, Clone(target._project, value, target));
        }
        foreach (OpaqueMidiEvent value in _added)
        {
            cancellationToken.ThrowIfCancellationRequested();
            target._added.Add(Clone(target._project, value, target));
        }
        target._generation = _generation;
    }

    void IOpaqueMidiEventChangeSink.OnChanged(OpaqueMidiEvent value)
    {
        if (!_clearSource && SourceIndexOf(value.Id) >= 0 && !_removed.Contains(value.Id))
            _replacements[value.Id] = value;
        Touch();
    }

    private int LiveSourceCount => _source is null || _clearSource
        ? 0
        : checked(_source.OpaqueEventCount - _removed.Count);
    private int SourceIndexOf(MidoraId id)
    {
        if (_source is null || _clearSource) return -1;
        if (_sourceIndices.TryGetValue(id, out int cached)) return cached;
        int index = _source.FindOpaqueEventIndex(id);
        if (index >= 0) _sourceIndices[id] = index;
        return index;
    }

    private HashSet<MidoraId>? SourceExclusions()
    {
        if (_removed.Count == 0 && _replacements.Count == 0) return null;
        HashSet<MidoraId> result = [.. _removed];
        result.UnionWith(_replacements.Keys);
        return result;
    }

    private int FindSourceIndexForVisibleIndex(int visibleIndex)
    {
        if ((uint)visibleIndex >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(visibleIndex));
        if (visibleIndex >= LiveSourceCount) return -1;
        if (_removed.Count == 0) return visibleIndex;
        int seen = 0;
        for (int sourceIndex = 0; sourceIndex < _source!.OpaqueEventCount; sourceIndex++)
        {
            if (_removed.Contains(_source.GetOpaqueEvent(sourceIndex).Id)) continue;
            if (seen++ == visibleIndex) return sourceIndex;
        }
        throw new InvalidOperationException("The paged opaque-event index is inconsistent.");
    }

    private int VisibleIndexForSourceIndex(int sourceIndex)
    {
        if (_removed.Count == 0) return sourceIndex;
        int removedBefore = _removed.Count(id =>
        {
            int removedIndex = SourceIndexOf(id);
            return removedIndex >= 0 && removedIndex < sourceIndex;
        });
        return sourceIndex - removedBefore;
    }

    private static int VisibleIndex(int sourceIndex, int[] sortedRemovedIndices)
    {
        int low = 0;
        int high = sortedRemovedIndices.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (sortedRemovedIndices[middle] < sourceIndex) low = middle + 1;
            else high = middle;
        }
        return checked(sourceIndex - low);
    }

    private OpaqueMidiEvent Materialize(OpaqueMidiEventValue value, int sourceIndex = -1)
    {
        if (sourceIndex >= 0) _sourceIndices[value.Id] = sourceIndex;
        OpaqueMidiEvent result = new(_project, value.Id)
        {
            Tick = value.Tick,
            Kind = value.Kind,
            MetaType = value.MetaType,
            Payload = value.Payload.ToArray(),
            Order = value.Order
        };
        Track(result);
        return result;
    }

    private void Track(OpaqueMidiEvent value) => value.SetChangeSink(this);
    private void Touch() => _generation++;

    private static OpaqueMidiEventValue ToValue(OpaqueMidiEvent value) => new(
        value.Id,
        value.Tick,
        value.Kind,
        value.MetaType,
        value.Payload,
        value.Order);

    private static OpaqueMidiEvent Clone(
        MidoraProject project,
        OpaqueMidiEvent source,
        IOpaqueMidiEventChangeSink sink)
    {
        OpaqueMidiEvent result = new(project, source.Id)
        {
            Tick = source.Tick,
            Kind = source.Kind,
            MetaType = source.MetaType,
            Payload = [.. source.Payload],
            Order = source.Order
        };
        result.SetChangeSink(sink);
        return result;
    }
}
