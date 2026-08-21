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
    private IPureMidiSegmentContentSource? _source;
    private bool _clearSource;
    private long _generation;

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
                return Materialize(value);
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
                    : Materialize(value);
            }
        }
        foreach (DirectMidiNote value in _added) yield return value;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public int IndexOf(DirectMidiNote item)
    {
        if (item is null) return -1;
        int sourceOrdinal = SourceIndexOf(item.Id);
        if (sourceOrdinal >= 0 && !_removed.Contains(item.Id))
        {
            return VisibleIndexForSourceIndex(sourceOrdinal);
        }
        int added = _added.FindIndex(value => value.Id == item.Id);
        return added < 0 ? -1 : checked(LiveSourceCount + added);
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
                value = Materialize(_source.GetNote(index));
                return true;
            }
        }
        value = _added.FirstOrDefault(item => item.Id == id);
        return value is not null;
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
        int sourceIndex = SourceIndexOf(item.Id);
        if (!_clearSource && sourceIndex >= 0 && !_removed.Contains(item.Id))
        {
            _removed.Add(item.Id);
            _replacements.Remove(item.Id);
            Touch();
            return true;
        }
        int addedIndex = _added.FindIndex(value => value.Id == item.Id);
        if (addedIndex < 0) return false;
        _added.RemoveAt(addedIndex);
        Touch();
        return true;
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
        if (!_clearSource && SourceIndexOf(value.Id) >= 0 && !_removed.Contains(value.Id))
            _replacements[value.Id] = value;
        Touch();
    }

    private int LiveSourceCount => _source is null || _clearSource
        ? 0
        : checked(_source.NoteCount - _removed.Count);

    private int SourceIndexOf(MidoraId id) => _source is null || _clearSource
        ? -1
        : _source.FindNoteIndex(id);

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
        int removedBefore = 0;
        for (int index = 0; index < sourceIndex; index++)
            if (_removed.Contains(_source!.GetNote(index).Id)) removedBefore++;
        return sourceIndex - removedBefore;
    }

    private DirectMidiNote Materialize(DirectMidiNoteValue value)
    {
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
    private void Touch() => _generation++;

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
    private IPureMidiSegmentContentSource? _source;
    private bool _clearSource;
    private long _generation;

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
                    : Materialize(value);
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
                    : Materialize(value);
            }
        }
        foreach (DirectMidiChannelEvent value in _added) yield return value;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public int IndexOf(DirectMidiChannelEvent item)
    {
        if (item is null) return -1;
        int sourceOrdinal = SourceIndexOf(item.Id);
        if (sourceOrdinal >= 0 && !_removed.Contains(item.Id)) return VisibleIndexForSourceIndex(sourceOrdinal);
        int added = _added.FindIndex(value => value.Id == item.Id);
        return added < 0 ? -1 : checked(LiveSourceCount + added);
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
                value = Materialize(_source.GetChannelEvent(index));
                return true;
            }
        }
        value = _added.FirstOrDefault(item => item.Id == id);
        return value is not null;
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
        int sourceIndex = SourceIndexOf(item.Id);
        if (!_clearSource && sourceIndex >= 0 && !_removed.Contains(item.Id))
        {
            _removed.Add(item.Id);
            _replacements.Remove(item.Id);
            Touch();
            return true;
        }
        int addedIndex = _added.FindIndex(value => value.Id == item.Id);
        if (addedIndex < 0) return false;
        _added.RemoveAt(addedIndex);
        Touch();
        return true;
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
        if (!_clearSource && SourceIndexOf(value.Id) >= 0 && !_removed.Contains(value.Id))
            _replacements[value.Id] = value;
        Touch();
    }

    private int LiveSourceCount => _source is null || _clearSource
        ? 0
        : checked(_source.ChannelEventCount - _removed.Count);
    private int SourceIndexOf(MidoraId id) => _source is null || _clearSource
        ? -1
        : _source.FindChannelEventIndex(id);

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
        int removedBefore = 0;
        for (int index = 0; index < sourceIndex; index++)
            if (_removed.Contains(_source!.GetChannelEvent(index).Id)) removedBefore++;
        return sourceIndex - removedBefore;
    }

    private DirectMidiChannelEvent Materialize(DirectMidiChannelEventValue value)
    {
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
    private void Touch() => _generation++;

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
                    : Materialize(value);
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
                    : Materialize(value);
            }
        }
        foreach (OpaqueMidiEvent value in _added) yield return value;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public int IndexOf(OpaqueMidiEvent item)
    {
        if (item is null) return -1;
        int sourceOrdinal = SourceIndexOf(item.Id);
        if (sourceOrdinal >= 0 && !_removed.Contains(item.Id)) return VisibleIndexForSourceIndex(sourceOrdinal);
        int added = _added.FindIndex(value => value.Id == item.Id);
        return added < 0 ? -1 : checked(LiveSourceCount + added);
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
                value = Materialize(_source.GetOpaqueEvent(index));
                return true;
            }
        }
        value = _added.FirstOrDefault(item => item.Id == id);
        return value is not null;
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
        int sourceIndex = SourceIndexOf(item.Id);
        if (!_clearSource && sourceIndex >= 0 && !_removed.Contains(item.Id))
        {
            _removed.Add(item.Id);
            _replacements.Remove(item.Id);
            Touch();
            return true;
        }
        int addedIndex = _added.FindIndex(value => value.Id == item.Id);
        if (addedIndex < 0) return false;
        _added.RemoveAt(addedIndex);
        Touch();
        return true;
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
    private int SourceIndexOf(MidoraId id) => _source is null || _clearSource
        ? -1
        : _source.FindOpaqueEventIndex(id);

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
        int removedBefore = 0;
        for (int index = 0; index < sourceIndex; index++)
            if (_removed.Contains(_source!.GetOpaqueEvent(index).Id)) removedBefore++;
        return sourceIndex - removedBefore;
    }

    private OpaqueMidiEvent Materialize(OpaqueMidiEventValue value)
    {
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
