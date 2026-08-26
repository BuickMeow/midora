namespace Midora.Domain;

/// <summary>
/// Immutable revision view over Direct MIDI channel events.  Raster workers retain
/// this object instead of the live collection, so a concurrent edit can only appear
/// in a later presentation revision.
/// </summary>
public sealed class DirectMidiChannelEventQuerySnapshot
{
    private readonly IPureMidiSegmentContentSource? _source;
    private readonly IReadOnlySet<MidoraId>? _sourceExclusions;
    private readonly PureMidiPointOverlayIndex<DirectMidiChannelEventValue> _overlayIndex;
    private readonly PureMidiSourceIdResolutionCache<DirectMidiChannelEventSourceMatch> _sourceIdCache;
    private readonly DirectMidiChannelEventValue[] _excluded;

    internal DirectMidiChannelEventQuerySnapshot(
        IPureMidiSegmentContentSource? source,
        bool clearSource,
        IReadOnlySet<MidoraId>? sourceExclusions,
        IReadOnlyDictionary<MidoraId, DirectMidiChannelEventValue> sourceValues,
        PureMidiPointOverlayIndex<DirectMidiChannelEventValue> overlayIndex,
        PureMidiSourceIdResolutionCache<DirectMidiChannelEventSourceMatch> sourceIdCache,
        int count,
        long generation)
    {
        _source = clearSource ? null : source;
        _overlayIndex = overlayIndex;
        _sourceIdCache = sourceIdCache;
        _sourceExclusions = _source is not null && sourceExclusions?.Count != 0
            ? sourceExclusions
            : null;
        _excluded = _sourceExclusions is null
            ? []
            : _sourceExclusions
                .Where(sourceValues.ContainsKey)
                .Select(id => sourceValues[id])
                .OrderBy(static value => value.Tick)
                .ThenBy(static value => value.Order)
                .ThenBy(static value => value.Id)
                .ToArray();
        Count = count;
        Generation = generation;
    }

    public int Count { get; }
    public long Generation { get; }

    public ulong GetRangeFingerprint(long startTick, long endTick)
    {
        ulong result = 14695981039346656037UL;
        if (_source is IPureMidiContentRangeFingerprintSource ranged)
            Add(ref result, ranged.GetChannelEventRangeFingerprint(startTick, endTick));
        else if (_source is not null)
            Add(ref result, HashText(_source.ContentFingerprint));
        AddRange(ref result, _excluded, startTick, endTick, exclusion: true);
        AddRange(ref result, _overlayIndex.Query(startTick, endTick), exclusion: false);
        return result;
    }

    public IEnumerable<DirectMidiChannelEventValue> QueryValues(long startTick, long endTick)
    {
        if (endTick <= startTick) yield break;
        if (_source is not null)
        {
            foreach (DirectMidiChannelEventValue value in _source.QueryChannelEvents(startTick, endTick))
            {
                if (_sourceExclusions?.Contains(value.Id) != true) yield return value;
            }
        }
        foreach (DirectMidiChannelEventValue value in _overlayIndex.Query(startTick, endTick))
            yield return value;
    }

    public bool TryQueryValuesCached(
        long startTick,
        long endTick,
        List<DirectMidiChannelEventValue> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (endTick <= startTick) return true;
        List<DirectMidiChannelEventValue>? sourceValues = null;
        if (_source is not null)
        {
            sourceValues = [];
            if (_source is IPureMidiCachedContentSource cached)
            {
                if (!cached.TryQueryCachedChannelEvents(startTick, endTick, sourceValues)) return false;
            }
            else
            {
                sourceValues.AddRange(_source.QueryChannelEvents(startTick, endTick));
            }
        }
        if (sourceValues is not null)
        {
            foreach (DirectMidiChannelEventValue value in sourceValues)
                if (_sourceExclusions?.Contains(value.Id) != true) destination.Add(value);
        }
        destination.AddRange(_overlayIndex.Query(startTick, endTick));
        return true;
    }

    public void PrefetchRange(long startTick, long endTick, CancellationToken cancellationToken)
    {
        if (endTick > startTick && _source is IPureMidiCachedContentSource cached)
            cached.PrefetchChannelEvents(startTick, endTick, cancellationToken);
    }

    public bool TryQueryByIdsCached(
        IReadOnlySet<MidoraId> ids,
        List<DirectMidiChannelEventValue> destination)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(destination);
        List<DirectMidiChannelEventSourceMatch>? sourceMatches = null;
        if (_source is not null)
        {
            HashSet<MidoraId> sourceIds = [.. ids];
            if (_sourceExclusions is not null) sourceIds.ExceptWith(_sourceExclusions);
            sourceMatches = [];
            if (_source is IPureMidiCachedContentSource cached)
            {
                if (!cached.TryQueryCachedChannelEventsByIds(sourceIds, sourceMatches)) return false;
            }
            else
            {
                sourceMatches.AddRange(_source.QueryChannelEventsByIds(sourceIds));
            }
        }
        HashSet<MidoraId> emitted = [];
        if (sourceMatches is not null)
        {
            foreach (DirectMidiChannelEventSourceMatch match in sourceMatches)
                if (emitted.Add(match.Value.Id)) destination.Add(match.Value);
        }
        foreach (DirectMidiChannelEventValue value in _overlayIndex.ResolveByIds(ids))
            if (emitted.Add(value.Id)) destination.Add(value);
        return true;
    }

    public IEnumerable<DirectMidiChannelEventValue> ResolveByIds(
        IReadOnlySet<MidoraId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) yield break;
        HashSet<MidoraId> emitted = [];
        if (_source is not null)
        {
            HashSet<MidoraId> sourceIds = [.. ids];
            if (_sourceExclusions is not null) sourceIds.ExceptWith(_sourceExclusions);
            foreach (DirectMidiChannelEventSourceMatch match in _sourceIdCache.Resolve(
                sourceIds,
                _source.QueryChannelEventsByIds))
            {
                if (emitted.Add(match.Value.Id)) yield return match.Value;
            }
        }
        foreach (DirectMidiChannelEventValue value in _overlayIndex.ResolveByIds(ids))
            if (emitted.Add(value.Id)) yield return value;
    }

    public void PrefetchIds(IReadOnlySet<MidoraId> ids, CancellationToken cancellationToken)
    {
        if (_source is not IPureMidiCachedContentSource cached || ids.Count == 0) return;
        HashSet<MidoraId> sourceIds = [.. ids];
        if (_sourceExclusions is not null) sourceIds.ExceptWith(_sourceExclusions);
        cached.PrefetchChannelEventsByIds(sourceIds, cancellationToken);
    }

    private static int FirstAtOrAfter(DirectMidiChannelEventValue[] values, long tick)
    {
        int low = 0;
        int high = values.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (values[middle].Tick < tick) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static DirectMidiChannelEventValue ToValue(DirectMidiChannelEvent value) => new(
        value.Id, value.Tick, value.Kind, value.Data1, value.Data2, value.Order);

    private static void AddRange(
        ref ulong fingerprint,
        IEnumerable<DirectMidiChannelEventValue> values,
        bool exclusion)
    {
        foreach (DirectMidiChannelEventValue value in values)
        {
            Add(ref fingerprint, exclusion ? 0x6578636c75646564UL : 0x656469746564UL);
            Add(ref fingerprint, unchecked((ulong)value.Id.Value));
            Add(ref fingerprint, unchecked((ulong)value.Tick));
            Add(ref fingerprint, unchecked((ulong)value.Kind));
            Add(ref fingerprint, unchecked((ulong)value.Data1));
            Add(ref fingerprint, unchecked((ulong)value.Data2));
            Add(ref fingerprint, unchecked((ulong)value.Order));
        }
    }

    private static void AddRange(
        ref ulong fingerprint,
        DirectMidiChannelEventValue[] values,
        long startTick,
        long endTick,
        bool exclusion)
    {
        int first = FirstAtOrAfter(values, startTick);
        int last = FirstAtOrAfter(values, endTick);
        for (int index = first; index < last; index++)
        {
            DirectMidiChannelEventValue value = values[index];
            Add(ref fingerprint, exclusion ? 0x6578636c75646564UL : 0x656469746564UL);
            Add(ref fingerprint, unchecked((ulong)value.Id.Value));
            Add(ref fingerprint, unchecked((ulong)value.Tick));
            Add(ref fingerprint, unchecked((ulong)value.Kind));
            Add(ref fingerprint, unchecked((ulong)value.Data1));
            Add(ref fingerprint, unchecked((ulong)value.Data2));
            Add(ref fingerprint, unchecked((ulong)value.Order));
        }
    }

    private static ulong HashText(string value)
    {
        ulong result = 14695981039346656037UL;
        foreach (char character in value) Add(ref result, character);
        return result;
    }

    private static void Add(ref ulong value, ulong part)
    {
        value ^= part;
        value *= 1099511628211UL;
    }
}

/// <summary>Immutable revision view over imported Meta/SysEx events.</summary>
public sealed class OpaqueMidiEventQuerySnapshot
{
    private readonly IPureMidiSegmentContentSource? _source;
    private readonly IReadOnlySet<MidoraId>? _sourceExclusions;
    private readonly PureMidiPointOverlayIndex<OpaqueMidiEventValue> _overlayIndex;
    private readonly PureMidiSourceIdResolutionCache<OpaqueMidiEventSourceMatch> _sourceIdCache;
    private readonly OpaqueMidiEventValue[] _excluded;

    internal OpaqueMidiEventQuerySnapshot(
        IPureMidiSegmentContentSource? source,
        bool clearSource,
        IReadOnlySet<MidoraId>? sourceExclusions,
        IReadOnlyDictionary<MidoraId, OpaqueMidiEventValue> sourceValues,
        PureMidiPointOverlayIndex<OpaqueMidiEventValue> overlayIndex,
        PureMidiSourceIdResolutionCache<OpaqueMidiEventSourceMatch> sourceIdCache,
        int count,
        long generation)
    {
        _source = clearSource ? null : source;
        _overlayIndex = overlayIndex;
        _sourceIdCache = sourceIdCache;
        _sourceExclusions = _source is not null && sourceExclusions?.Count != 0
            ? sourceExclusions
            : null;
        _excluded = _sourceExclusions is null
            ? []
            : _sourceExclusions.Where(sourceValues.ContainsKey).Select(id => sourceValues[id])
                .OrderBy(static value => value.Tick).ThenBy(static value => value.Order)
                .ThenBy(static value => value.Id).ToArray();
        Count = count;
        Generation = generation;
    }

    public int Count { get; }
    public long Generation { get; }

    public ulong GetRangeFingerprint(long startTick, long endTick)
    {
        ulong result = 14695981039346656037UL;
        if (_source is IPureMidiContentRangeFingerprintSource ranged)
            Add(ref result, ranged.GetOpaqueEventRangeFingerprint(startTick, endTick));
        else if (_source is not null)
            Add(ref result, HashText(_source.ContentFingerprint));
        AddRange(ref result, _excluded, startTick, endTick, exclusion: true);
        AddRange(ref result, _overlayIndex.Query(startTick, endTick), exclusion: false);
        return result;
    }

    public IEnumerable<OpaqueMidiEventValue> QueryValues(long startTick, long endTick)
    {
        if (endTick <= startTick) yield break;
        if (_source is not null)
        {
            foreach (OpaqueMidiEventValue value in _source.QueryOpaqueEvents(startTick, endTick))
                if (_sourceExclusions?.Contains(value.Id) != true) yield return value;
        }
        foreach (OpaqueMidiEventValue value in _overlayIndex.Query(startTick, endTick))
            yield return value;
    }

    public bool TryQueryValuesCached(
        long startTick,
        long endTick,
        List<OpaqueMidiEventValue> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (endTick <= startTick) return true;
        List<OpaqueMidiEventValue>? sourceValues = null;
        if (_source is not null)
        {
            sourceValues = [];
            if (_source is IPureMidiCachedContentSource cached)
            {
                if (!cached.TryQueryCachedOpaqueEvents(startTick, endTick, sourceValues)) return false;
            }
            else sourceValues.AddRange(_source.QueryOpaqueEvents(startTick, endTick));
        }
        if (sourceValues is not null)
        {
            foreach (OpaqueMidiEventValue value in sourceValues)
                if (_sourceExclusions?.Contains(value.Id) != true) destination.Add(value);
        }
        destination.AddRange(_overlayIndex.Query(startTick, endTick));
        return true;
    }

    public void PrefetchRange(long startTick, long endTick, CancellationToken cancellationToken)
    {
        if (endTick > startTick && _source is IPureMidiCachedContentSource cached)
            cached.PrefetchOpaqueEvents(startTick, endTick, cancellationToken);
    }

    public bool TryQueryByIdsCached(
        IReadOnlySet<MidoraId> ids,
        List<OpaqueMidiEventValue> destination)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(destination);
        List<OpaqueMidiEventSourceMatch>? sourceMatches = null;
        if (_source is not null)
        {
            HashSet<MidoraId> sourceIds = [.. ids];
            if (_sourceExclusions is not null) sourceIds.ExceptWith(_sourceExclusions);
            sourceMatches = [];
            if (_source is IPureMidiCachedContentSource cached)
            {
                if (!cached.TryQueryCachedOpaqueEventsByIds(sourceIds, sourceMatches)) return false;
            }
            else sourceMatches.AddRange(_source.QueryOpaqueEventsByIds(sourceIds));
        }
        HashSet<MidoraId> emitted = [];
        if (sourceMatches is not null)
        {
            foreach (OpaqueMidiEventSourceMatch match in sourceMatches)
                if (emitted.Add(match.Value.Id)) destination.Add(match.Value);
        }
        foreach (OpaqueMidiEventValue value in _overlayIndex.ResolveByIds(ids))
            if (emitted.Add(value.Id)) destination.Add(value);
        return true;
    }

    public IEnumerable<OpaqueMidiEventValue> ResolveByIds(IReadOnlySet<MidoraId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) yield break;
        HashSet<MidoraId> emitted = [];
        if (_source is not null)
        {
            HashSet<MidoraId> sourceIds = [.. ids];
            if (_sourceExclusions is not null) sourceIds.ExceptWith(_sourceExclusions);
            foreach (OpaqueMidiEventSourceMatch match in _sourceIdCache.Resolve(
                sourceIds,
                _source.QueryOpaqueEventsByIds))
            {
                if (emitted.Add(match.Value.Id)) yield return match.Value;
            }
        }
        foreach (OpaqueMidiEventValue value in _overlayIndex.ResolveByIds(ids))
            if (emitted.Add(value.Id)) yield return value;
    }

    public void PrefetchIds(IReadOnlySet<MidoraId> ids, CancellationToken cancellationToken)
    {
        if (_source is not IPureMidiCachedContentSource cached || ids.Count == 0) return;
        HashSet<MidoraId> sourceIds = [.. ids];
        if (_sourceExclusions is not null) sourceIds.ExceptWith(_sourceExclusions);
        cached.PrefetchOpaqueEventsByIds(sourceIds, cancellationToken);
    }

    private static int FirstAtOrAfter(OpaqueMidiEventValue[] values, long tick)
    {
        int low = 0;
        int high = values.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (values[middle].Tick < tick) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static OpaqueMidiEventValue ToValue(OpaqueMidiEvent value) => new(
        value.Id,
        value.Tick,
        value.Kind,
        value.MetaType,
        value.Payload.ToArray(),
        value.Order);

    private static void AddRange(
        ref ulong fingerprint,
        IEnumerable<OpaqueMidiEventValue> values,
        bool exclusion)
    {
        foreach (OpaqueMidiEventValue value in values)
        {
            Add(ref fingerprint, exclusion ? 0x6578636c75646564UL : 0x656469746564UL);
            Add(ref fingerprint, unchecked((ulong)value.Id.Value));
            Add(ref fingerprint, unchecked((ulong)value.Tick));
            Add(ref fingerprint, unchecked((ulong)value.Kind));
            Add(ref fingerprint, value.MetaType);
            foreach (byte part in value.Payload.Span) Add(ref fingerprint, part);
            Add(ref fingerprint, unchecked((ulong)value.Order));
        }
    }

    private static void AddRange(
        ref ulong fingerprint,
        OpaqueMidiEventValue[] values,
        long startTick,
        long endTick,
        bool exclusion)
    {
        int first = FirstAtOrAfter(values, startTick);
        int last = FirstAtOrAfter(values, endTick);
        for (int index = first; index < last; index++)
        {
            OpaqueMidiEventValue value = values[index];
            Add(ref fingerprint, exclusion ? 0x6578636c75646564UL : 0x656469746564UL);
            Add(ref fingerprint, unchecked((ulong)value.Id.Value));
            Add(ref fingerprint, unchecked((ulong)value.Tick));
            Add(ref fingerprint, unchecked((ulong)value.Kind));
            Add(ref fingerprint, value.MetaType);
            foreach (byte part in value.Payload.Span) Add(ref fingerprint, part);
            Add(ref fingerprint, unchecked((ulong)value.Order));
        }
    }

    private static ulong HashText(string value)
    {
        ulong result = 14695981039346656037UL;
        foreach (char character in value) Add(ref result, character);
        return result;
    }

    private static void Add(ref ulong value, ulong part)
    {
        value ^= part;
        value *= 1099511628211UL;
    }
}
