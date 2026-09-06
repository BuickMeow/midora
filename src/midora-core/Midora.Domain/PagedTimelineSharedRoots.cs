using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Midora.Domain;

internal interface ITimelineOrdinalLookup
{
    int Find(MidoraId id);
    int StructuralDepth => 0;
    void Prepare(CancellationToken token = default) => token.ThrowIfCancellationRequested();
    void Prepare(IImmutableTimelineOrdinalIndexBuilder builder, CancellationToken token = default) => Prepare(token);
}

internal sealed partial class PagedTimelineObjectList<T, TValue> where T : class
{
    private SharedStore? _shared;
    private ITimelineOrdinalLookup? _sharedOrdinalDirectory;

    // The dictionaries below are immutable too: capturing a root never copies
    // the object graph, value pages, spatial indexes, or leaf directory.
    private sealed record EditableRoot(
        PersistentTimelineSequence<TValue> Sequence,
        ImmutableDictionary<PersistentTimelineSequence<TValue>.Leaf, PagedTimelineValuePage<TValue>> LeafPages,
        IReadOnlyDictionary<MidoraId, TValue> BaseById,
        PersistentTimelineIdDeltaMap<TValue> IdDelta,
        PersistentTimelineIdDeltaMap<TValue> SpatialBaseDelta,
        PersistentTimelineIdDeltaMap<TValue> SpatialValueOverlay,
        ImmutableDictionary<PersistentTimelineIdDeltaMap<TValue>.Bucket, PagedTimelineValuePage<TValue>> OverlayPages,
        PagedTimelineSpatialBlockIndex<TValue> SpatialIndex,
        PagedTimelineSpatialBlockIndex<TValue> OverlayIndex,
        ImmutableDictionary<long, int> DiscoveryCounts,
        ITimelineOrdinalLookup Ordinals) : ITimelineOrdinalLookup
    {
        public int Find(MidoraId id) => Ordinals.Find(id);
        public int StructuralDepth => Ordinals.StructuralDepth;
        public void Prepare(CancellationToken token = default) => Ordinals.Prepare(token);
        public void Prepare(IImmutableTimelineOrdinalIndexBuilder builder, CancellationToken token = default) => Ordinals.Prepare(builder, token);
    }

    private EditableRoot CaptureEditableRoot() => new(
        _publishedSequence!, _publishedLeafPages, _publishedBaseById!,
        _publishedIdDelta, _spatialBaseDelta, _spatialValueOverlay,
        _spatialOverlayPages, _spatialIndex, _spatialOverlayIndex,
        _discoveryKeyPageCounts,
        _sharedOrdinalDirectory ??= new OrdinalDirectory(_publishedSequence!, _getId));

    internal void AdoptSnapshot(PagedTimelineValueSnapshot<TValue> snapshot, Func<TValue, T> materialize)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(materialize);
        if (_count != 0 || _batchDepth != 0)
            throw new InvalidOperationException("Only an empty timeline collection can adopt an immutable root.");
        if (snapshot.EditableRoot is not EditableRoot root)
            throw new InvalidOperationException("The timeline snapshot has no compatible editable root.");
        _publishedSequence = root.Sequence;
        _publishedLeafPages = root.LeafPages;
        _publishedBaseById = root.BaseById;
        _publishedIdDelta = root.IdDelta;
        _spatialBaseDelta = root.SpatialBaseDelta;
        _spatialValueOverlay = root.SpatialValueOverlay;
        _spatialOverlayPages = root.OverlayPages;
        _spatialIndex = root.SpatialIndex;
        _spatialOverlayIndex = root.OverlayIndex;
        _discoveryKeyPageCounts = root.DiscoveryCounts;
        _sharedOrdinalDirectory = root.Ordinals;
        _count = snapshot.Count;
        _generation = snapshot.Generation;
        _publishedSnapshot = snapshot;
        _publishedSnapshotGeneration = _generation;
        _shared = new(this, materialize);
    }

    internal void AdoptSource(IImmutableTimelineValueSource<TValue> source, Func<TValue, T> materialize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(materialize);
        if (_count != 0 || _batchDepth != 0)
            throw new InvalidOperationException("Only an empty timeline collection can adopt an immutable source.");
        PersistentTimelineSequence<TValue> sequence = PersistentTimelineSequence<TValue>
            .CreateSourceBacked(source, _getFingerprint, cancellationToken);
        var pages = ImmutableDictionary.CreateBuilder<PersistentTimelineSequence<TValue>.Leaf, PagedTimelineValuePage<TValue>>();
        List<PagedTimelineValuePage<TValue>> spatialPages = [];
        ImmutableDictionary<long, int>.Builder discovery = ImmutableDictionary.CreateBuilder<long, int>();
        foreach (var leaf in sequence.EnumerateLeaves())
        {
            cancellationToken.ThrowIfCancellationRequested();
            PagedTimelineValuePage<TValue> page = CreatePublishedPage(leaf);
            pages.Add(leaf, page);
            spatialPages.Add(page);
            foreach (long key in page.DiscoveryKeys)
                discovery[key] = discovery.TryGetValue(key, out int count) ? checked(count + 1) : 1;
        }
        PagedTimelineSpatialBlockIndex<TValue> spatial = PagedTimelineSpatialBlockIndex<TValue>.Create(spatialPages);
        ITimelineOrdinalLookup ordinals = source is IIndexedImmutableTimelineValueSource<TValue> indexed
            ? new IndexedSourceOrdinals(indexed)
            : new OrdinalDirectory(sequence, _getId);
        // Build this compact page directory while detached, not on first UI hit.
        if (ordinals is OrdinalDirectory directory) directory.Prepare(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _publishedSequence = sequence;
        _publishedLeafPages = pages.ToImmutable();
        _publishedBaseById = new SourceValueDictionary(sequence, ordinals, _getId);
        _sharedOrdinalDirectory = ordinals;
        _spatialIndex = spatial;
        _discoveryKeyPageCounts = discovery.ToImmutable();
        _count = source.Count;
        _generation++;
        _publishedSnapshot = null;
        _shared = new(this, materialize);
    }

    private sealed class SourceValueDictionary(PersistentTimelineSequence<TValue> sequence,
        ITimelineOrdinalLookup ordinals, Func<TValue, MidoraId> getId) : IReadOnlyDictionary<MidoraId, TValue>
    {
        public int Count => sequence.Count;
        public IEnumerable<MidoraId> Keys => sequence.Enumerate().Select(getId);
        public IEnumerable<TValue> Values => sequence.Enumerate();
        public TValue this[MidoraId key] => TryGetValue(key, out TValue? value)
            ? value : throw new KeyNotFoundException();
        public bool ContainsKey(MidoraId key) => ordinals.Find(key) >= 0;
        public bool TryGetValue(MidoraId key, out TValue value)
        {
            int index = ordinals.Find(key);
            value = index >= 0 ? sequence[index] : default!;
            return index >= 0;
        }
        public IEnumerator<KeyValuePair<MidoraId, TValue>> GetEnumerator()
        {
            foreach (TValue value in sequence.Enumerate()) yield return new(getId(value), value);
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class LeafValueSource(PersistentTimelineSequence<TValue>.Leaf leaf)
        : IImmutableTimelineValueSource<TValue>
    {
        public int Count => leaf.Count;
        public int PageCapacity => PersistentTimelineSequence<TValue>.LeafCapacity;
        public TValue this[int index] => leaf.GetValue(index);
        public bool TryReadCachedValue(int index, out TValue value)
        {
            using var scope = TimelineValueReadScope.EnterCacheOnly();
            try { value = leaf.GetValue(index); return true; }
            catch (TimelineValueReadPendingException) { value = default!; return false; }
        }
        public ReadOnlyMemory<TValue> ReadPage(int pageIndex) => pageIndex == 0
            ? leaf.Values : throw new ArgumentOutOfRangeException(nameof(pageIndex));
        public bool TryReadCachedPage(int pageIndex, out ReadOnlyMemory<TValue> values)
        {
            using var scope = TimelineValueReadScope.EnterCacheOnly();
            try { values = ReadPage(pageIndex); return true; }
            catch (TimelineValueReadPendingException) { values = default; return false; }
        }
        public IEnumerator<TValue> GetEnumerator() => leaf.EnumerateValues().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class EditedLeafValueSource(PersistentTimelineSequence<TValue>.Leaf? original,
        IImmutableTimelineValueSource<TimelineValueEdit<TValue>> changes, int[] mapping)
        : IImmutableTimelineValueSource<TValue>
    {
        public int Count => mapping.Length;
        public int PageCapacity => PersistentTimelineSequence<TValue>.LeafCapacity;
        public TValue this[int index] => mapping[index] < 0
            ? original!.GetValue(~mapping[index]) : changes[mapping[index]].Replacement;
        public bool TryReadCachedValue(int index, out TValue value)
        {
            if (mapping[index] >= 0)
            {
                if (changes.TryReadCachedValue(mapping[index], out var change))
                { value = change.Replacement; return true; }
                value = default!; return false;
            }
            using var scope = TimelineValueReadScope.EnterCacheOnly();
            try { value = original!.GetValue(~mapping[index]); return true; }
            catch (TimelineValueReadPendingException) { value = default!; return false; }
        }
        public ReadOnlyMemory<TValue> ReadPage(int pageIndex)
        {
            if (pageIndex != 0) throw new ArgumentOutOfRangeException(nameof(pageIndex));
            TValue[] values = new TValue[Count];
            for (int i = 0; i < values.Length; i++) values[i] = this[i];
            return values;
        }
        public bool TryReadCachedPage(int pageIndex, out ReadOnlyMemory<TValue> values)
        {
            using var scope = TimelineValueReadScope.EnterCacheOnly();
            try { values = ReadPage(pageIndex); return true; }
            catch (TimelineValueReadPendingException) { values = default; return false; }
        }
        public IEnumerator<TValue> GetEnumerator() { for (int i = 0; i < Count; i++) yield return this[i]; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal void AdoptEditedSnapshot(PagedTimelineValueSnapshot<TValue> snapshot,
        IImmutableTimelineValueSource<TimelineValueEdit<TValue>> changes,
        IImmutableTimelineValueSource<TValue>? appended, Func<TValue, T> materialize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.PageCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(changes));
        if (_count != 0 || _batchDepth != 0)
            throw new InvalidOperationException("Only an empty timeline collection can adopt an edited root.");
        if (snapshot.EditableRoot is not EditableRoot source)
            throw new InvalidOperationException("The timeline snapshot has no compatible editable root.");
        List<PersistentTimelineSequence<TValue>.Leaf> leaves = [];
        var pages = source.LeafPages.ToBuilder();
        HashSet<PagedTimelineValuePage<TValue>> removedPages = [];
        List<PagedTimelineValuePage<TValue>> addedPages = [];
        var discovery = source.DiscoveryCounts.ToBuilder();
        int changeIndex = 0, start = 0, removedCount = 0;
        int[] removedByPage = new int[(changes.Count + Math.Max(1, changes.PageCapacity) - 1) / Math.Max(1, changes.PageCapacity) + 1];
        foreach (var leaf in source.Sequence.EnumerateLeaves())
        {
            cancellationToken.ThrowIfCancellationRequested();
            PersistentTimelineSequence<TValue>.Leaf? replacement = leaf;
            bool touched = changeIndex < changes.Count && changes[changeIndex].Ordinal < start + leaf.Count;
            if (touched)
            {
                List<int> mapping = new(leaf.Count);
                for (int local = 0; local < leaf.Count; local++)
                {
                    int ordinal = checked(start + local);
                    if (changeIndex < changes.Count && changes[changeIndex].Ordinal < ordinal)
                        throw new InvalidOperationException("Timeline root edits must be unique and sorted by source ordinal.");
                    if (changeIndex < changes.Count && changes[changeIndex].Ordinal == ordinal)
                    {
                        if (changeIndex % changes.PageCapacity == 0)
                            removedByPage[changeIndex / changes.PageCapacity] = removedCount;
                        var change = changes[changeIndex];
                        if (change.IsDeleted) removedCount++;
                        else
                        {
                            if (_getId(change.Replacement) != _getId(leaf.GetValue(local)))
                                throw new InvalidOperationException("A timeline root replacement must preserve Stable ID.");
                            mapping.Add(changeIndex);
                        }
                        changeIndex++;
                    }
                    else mapping.Add(~local);
                }
                replacement = mapping.Count == 0 ? null : new(
                    new TimelineValueBuffer<TValue>(new EditedLeafValueSource(mapping.Any(static i => i < 0) ? leaf : null,
                        changes, mapping.ToArray()),
                        0, mapping.Count), _getFingerprint);
            }
            // Existing value overlays are consolidated into leaf-backed views.
            // Their scalar values remain in the original immutable leaves.
            if (touched || leaf.Replacements.Count != 0)
            {
                PagedTimelineValuePage<TValue> oldPage = pages[leaf];
                pages.Remove(leaf);
                if (removedPages.Add(oldPage))
                    foreach (long key in oldPage.DiscoveryKeys)
                    {
                        int count = discovery[key];
                        if (count == 1) discovery.Remove(key); else discovery[key] = count - 1;
                    }
                if (replacement is not null) AddPage(replacement);
            }
            if (replacement is not null) leaves.Add(replacement);
            start += leaf.Count;
        }
        if (changeIndex != changes.Count)
            throw new InvalidOperationException("A timeline root edit references an out-of-range source ordinal.");
        removedByPage[^1] = removedCount;
        PersistentTimelineSequence<TValue>? appendSequence = null;
        if (appended is not null)
        {
            appendSequence = PersistentTimelineSequence<TValue>.CreateSourceBacked(appended, _getFingerprint, cancellationToken);
            foreach (var leaf in appendSequence.EnumerateLeaves())
            {
                cancellationToken.ThrowIfCancellationRequested();
                leaves.Add(leaf);
                AddPage(leaf);
            }
        }
        var sequence = PersistentTimelineSequence<TValue>.CreateFromLeaves(leaves, _getFingerprint);
        var spatial = source.SpatialIndex.ReplacePages(removedPages.ToArray(), addedPages);
        ITimelineOrdinalLookup ordinals;
        IDisposable? createdAddress = null;
        if (removedCount == 0 && (appended?.Count ?? 0) == 0) ordinals = source.Ordinals;
        else if (changes is IImmutableTimelineEditAddressSource addresses)
        {
            if (source.Ordinals.StructuralDepth >= 8)
            {
                var index = addresses.CreateOrdinalIndex(sequence.Enumerate().Select((v, i) => new TimelineIdOrdinal(_getId(v), i)), cancellationToken);
                createdAddress = index as IDisposable;
                ordinals = new ExternalOrdinals(index);
            }
            else
            {
                var deleted = removedCount == 0 ? null : addresses.CreateDeletedOrdinals(cancellationToken);
                createdAddress = deleted as IDisposable;
                ITimelineOrdinalLookup? additions = appended is IIndexedImmutableTimelineValueSource<TValue> indexed
                    ? new IndexedSourceOrdinals(indexed)
                    : appendSequence is not null ? new OrdinalDirectory(appendSequence, _getId) : null;
                try { additions?.Prepare(cancellationToken); }
                catch { createdAddress?.Dispose(); throw; }
                ordinals = new BoundedEditedOrdinals(source.Ordinals, deleted, source.Sequence.Count - removedCount, additions);
            }
        }
        else ordinals = new EditedOrdinals(source.Ordinals, changes, removedByPage,
            source.Sequence.Count - removedCount, appended, _getId);
        try { cancellationToken.ThrowIfCancellationRequested(); }
        catch { createdAddress?.Dispose(); throw; }
        _publishedSequence = sequence;
        _publishedLeafPages = pages.ToImmutable();
        _publishedBaseById = new SourceValueDictionary(sequence, ordinals, _getId);
        _spatialIndex = spatial;
        _discoveryKeyPageCounts = discovery.ToImmutable();
        _sharedOrdinalDirectory = ordinals;
        _count = sequence.Count;
        _generation = checked(snapshot.Generation + 1);
        _publishedSnapshot = null;
        _shared = new(this, materialize);
        return;

        void AddPage(PersistentTimelineSequence<TValue>.Leaf leaf)
        {
            PagedTimelineValuePage<TValue> page = CreatePublishedPage(leaf);
            pages.Add(leaf, page);
            addedPages.Add(page);
            foreach (long key in page.DiscoveryKeys)
                discovery[key] = discovery.TryGetValue(key, out int count) ? checked(count + 1) : 1;
        }
    }

    private sealed class EditedOrdinals(ITimelineOrdinalLookup original,
        IImmutableTimelineValueSource<TimelineValueEdit<TValue>> changes,
        int[] removedByPage, int retainedCount, IImmutableTimelineValueSource<TValue>? appended,
        Func<TValue, MidoraId> getId) : ITimelineOrdinalLookup
    {
        public int StructuralDepth => original.StructuralDepth + 1;
        public void Prepare(CancellationToken token = default) => original.Prepare(token);
        public void Prepare(IImmutableTimelineOrdinalIndexBuilder builder, CancellationToken token = default) => original.Prepare(builder, token);
        public int Find(MidoraId id)
        {
            int ordinal = original.Find(id);
            if (ordinal >= 0)
            {
                int low = 0, high = changes.Count;
                while (low < high)
                {
                    int middle = low + ((high - low) >> 1);
                    if (changes[middle].Ordinal < ordinal) low = middle + 1; else high = middle;
                }
                if (low < changes.Count && changes[low].Ordinal == ordinal && changes[low].IsDeleted) return -1;
                int page = low / changes.PageCapacity;
                int removed = removedByPage[page];
                for (int i = page * changes.PageCapacity; i < low; i++)
                    if (changes[i].IsDeleted) removed++;
                return ordinal - removed;
            }
            if (appended is IIndexedImmutableTimelineValueSource<TValue> indexed)
                return indexed.TryFindOrdinalById(id, out int addedOrdinal) ? retainedCount + addedOrdinal : -1;
            if (appended is null) return -1;
            for (int i = 0; i < appended.Count; i++)
                if (getId(appended[i]) == id) return retainedCount + i;
            return -1;
        }
    }

    private sealed class ExternalOrdinals(IImmutableTimelineIdIndex index) : ITimelineOrdinalLookup
    {
        public int Find(MidoraId id) => index.TryFindOrdinalById(id, out int ordinal) ? ordinal : -1;
    }

    private sealed class BoundedEditedOrdinals(ITimelineOrdinalLookup original,
        IImmutableTimelineValueSource<int>? deleted, int retainedCount, ITimelineOrdinalLookup? additions) : ITimelineOrdinalLookup
    {
        public int StructuralDepth => original.StructuralDepth + 1;
        public void Prepare(CancellationToken token = default) => original.Prepare(token);
        public void Prepare(IImmutableTimelineOrdinalIndexBuilder builder, CancellationToken token = default) => original.Prepare(builder, token);
        public int Find(MidoraId id)
        {
            int ordinal = original.Find(id);
            if (ordinal < 0)
            {
                int added = additions?.Find(id) ?? -1;
                return added < 0 ? -1 : checked(retainedCount + added);
            }
            if (deleted is null) return ordinal;
            int low = 0, high = deleted.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (deleted[middle] < ordinal) low = middle + 1; else high = middle;
            }
            return low < deleted.Count && deleted[low] == ordinal ? -1 : ordinal - low;
        }
    }

    private sealed class SharedStore
    {
        private readonly PagedTimelineObjectList<T, TValue> owner;
        private readonly Func<TValue, T> materialize;
        private readonly Action<T> _changeSink;
        public SharedStore(PagedTimelineObjectList<T, TValue> owner, Func<TValue, T> materialize)
        {
            this.owner = owner;
            this.materialize = materialize;
            _changeSink = OnChanged;
        }
        // Facades preserve reference identity while held by an editor. The root
        // owns only immutable values; a read does not retain the facade or page.
        private Dictionary<MidoraId, FacadeReference> _facades = [];
        private readonly Dictionary<int, TValue> _pending = [];
        private int _nextPrune = DefaultPageCapacity * 2;
        private long _structuralRevision;
        private readonly record struct FacadeReference(WeakReference<T> Reference, int Ordinal, long StructuralRevision);

        public T Get(int index)
        {
            if ((uint)index >= (uint)owner._count) throw new ArgumentOutOfRangeException(nameof(index));
            TValue value = _pending.TryGetValue(index, out TValue? pending)
                ? pending : owner._publishedSequence![index];
            return GetFacade(value, index);
        }

        private T GetFacade(TValue value, int ordinal = -1)
        {
            MidoraId id = owner._getId(value);
            if (_facades.TryGetValue(id, out FacadeReference reference) && reference.Reference.TryGetTarget(out T? existing))
            {
                if (ordinal >= 0) _facades[id] = reference with { Ordinal = ordinal, StructuralRevision = _structuralRevision };
                return existing;
            }
            T result = materialize(value);
            Register(result, ordinal);
            return result;
        }

        private void Register(T value, int ordinal = -1)
        {
            _facades[owner._getId(owner._toValue(value))] = new(new(value), ordinal, _structuralRevision);
            owner._setChangeSink(value, _changeSink);
            if (_facades.Count < _nextPrune) return;
            Dictionary<MidoraId, FacadeReference> retained = [];
            foreach (var pair in _facades)
                if (pair.Value.Reference.TryGetTarget(out _)) retained.Add(pair.Key, pair.Value);
            _facades = retained;
            _nextPrune = checked(retained.Count + DefaultPageCapacity);
        }

        private bool TryGetValue(MidoraId id, out TValue value)
        {
            if (owner._publishedIdDelta.TryGetValue(id, out PagedTimelineIdDelta<TValue> delta))
            {
                value = delta.Value;
                return delta.Exists;
            }
            return owner._publishedBaseById!.TryGetValue(id, out value!);
        }

        public bool TryGetById(MidoraId id, out T? value)
        {
            Flush();
            if (TryGetValue(id, out TValue? scalar))
            {
                value = GetFacade(scalar);
                return true;
            }
            value = null;
            return false;
        }

        private int FindOrdinal(MidoraId id) =>
            (owner._sharedOrdinalDirectory ??= new OrdinalDirectory(owner._publishedSequence!, owner._getId)).Find(id);

        public int IndexOf(T? value)
        {
            if (value is null) return -1;
            MidoraId id = owner._getId(owner._toValue(value));
            if (!_facades.TryGetValue(id, out FacadeReference reference)
                || !reference.Reference.TryGetTarget(out T? registered) || !ReferenceEquals(value, registered)) return -1;
            if (reference.Ordinal >= 0 && reference.StructuralRevision == _structuralRevision)
                return reference.Ordinal;
            int ordinal = FindOrdinal(id);
            _facades[id] = reference with { Ordinal = ordinal, StructuralRevision = _structuralRevision };
            return ordinal;
        }

        private void OnChanged(T value)
        {
            int index = IndexOf(value);
            if (index < 0) return;
            _pending[index] = owner._toValue(value);
            // Legacy batch callers may hold a scope for millions of mutations.
            // The unpublished mutable working set remains one bounded chunk.
            if (_pending.Count >= DefaultPageCapacity) Flush();
            owner.Touch();
        }

        public void Flush()
        {
            if (_pending.Count == 0) return;
            owner.ApplyPublishedMutation(owner._publishedSequence!.ReplaceBatch(_pending));
            _pending.Clear();
        }

        public IEnumerable<T> Enumerate()
        {
            Flush();
            int ordinal = 0;
            foreach (TValue value in owner._publishedSequence!.Enumerate()) yield return GetFacade(value, ordinal++);
        }

        public IReadOnlyList<(int Index, T Value)> Resolve(IReadOnlyCollection<MidoraId> ids)
        {
            ArgumentNullException.ThrowIfNull(ids);
            List<(int Index, T Value)> result = new(ids.Count);
            foreach (MidoraId id in ids.Distinct())
            {
                if (!TryGetById(id, out T? value)) continue;
                int index = FindOrdinal(id);
                if (index >= 0) result.Add((index, value!));
            }
            result.Sort(static (left, right) => left.Index.CompareTo(right.Index));
            return result;
        }

        public void ValidateInsertRange(IReadOnlyList<T> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            HashSet<MidoraId> seen = [];
            foreach (T item in values)
            {
                ArgumentNullException.ThrowIfNull(item);
                MidoraId id = owner._getId(owner._toValue(item));
                if (id == default) throw new ArgumentOutOfRangeException(nameof(values));
                if (!seen.Add(id) || TryGetValue(id, out _))
                    throw new InvalidOperationException("A paged timeline collection cannot contain duplicate Stable IDs.");
            }
        }

        public void InsertRange(int index, IReadOnlyList<T> values)
        {
            if ((uint)index > (uint)owner._count) throw new ArgumentOutOfRangeException(nameof(index));
            ValidateInsertRange(values);
            if (values.Count == 0) return;
            Flush();
            // The sequence insertion accepts an indexed projection; no second
            // full array of scalar values is built beside the caller's data.
            owner.ApplyPublishedMutation(owner._publishedSequence!.InsertRange(index, new ValueProjection(values, owner._toValue)));
            owner._count = checked(owner._count + values.Count);
            owner._sharedOrdinalDirectory = null;
            _structuralRevision++;
            for (int offset = 0; offset < values.Count; offset++) Register(values[offset], index + offset);
            owner.Touch();
        }

        public void Set(int index, T value)
        {
            ArgumentNullException.ThrowIfNull(value);
            T old = Get(index);
            if (ReferenceEquals(old, value)) return;
            MidoraId oldId = owner._getId(owner._toValue(old));
            MidoraId newId = owner._getId(owner._toValue(value));
            if (newId != oldId && TryGetValue(newId, out _))
                throw new InvalidOperationException("A paged timeline collection cannot contain duplicate Stable IDs.");
            Flush();
            owner.ApplyPublishedMutation(owner._publishedSequence!.ReplaceBatch(new Dictionary<int, TValue> { [index] = owner._toValue(value) }));
            owner._setChangeSink(old, null);
            _facades.Remove(oldId);
            Register(value, index);
            if (oldId != newId) owner._sharedOrdinalDirectory = null;
            owner.Touch();
        }

        public void ReplaceRange(IReadOnlyList<T> expected, IReadOnlyList<T> replacement)
        {
            ArgumentNullException.ThrowIfNull(expected);
            ArgumentNullException.ThrowIfNull(replacement);
            if (expected.Count != replacement.Count) throw new ArgumentException("Paged timeline replacement lengths must match.");
            int[] indices = new int[expected.Count];
            HashSet<MidoraId> ids = [];
            for (int i = 0; i < expected.Count; i++)
            {
                MidoraId id = owner._getId(owner._toValue(expected[i]));
                if (!ids.Add(id) || id != owner._getId(owner._toValue(replacement[i])) || (indices[i] = IndexOf(expected[i])) < 0)
                    throw new InvalidOperationException("A paged timeline replacement is no longer fully present.");
            }
            using IDisposable batch = owner.BeginBatchChange();
            for (int i = 0; i < expected.Count; i++)
            {
                if (ReferenceEquals(expected[i], replacement[i])) continue;
                owner._setChangeSink(expected[i], null);
                Register(replacement[i], indices[i]);
                _pending[indices[i]] = owner._toValue(replacement[i]);
                if (_pending.Count >= DefaultPageCapacity) Flush();
                owner.Touch();
            }
        }

        public int RemoveRange(IReadOnlyCollection<T> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            int[] indices = values.Select(IndexOf).Where(static index => index >= 0).Distinct().Order().ToArray();
            if (indices.Length == 0) return 0;
            Flush();
            foreach (T value in values)
            {
                if (IndexOf(value) < 0) continue;
                owner._setChangeSink(value, null);
                _facades.Remove(owner._getId(owner._toValue(value)));
            }
            owner.ApplyPublishedMutation(owner._publishedSequence!.RemoveIndices(indices));
            owner._count -= indices.Length;
            owner._sharedOrdinalDirectory = null;
            _structuralRevision++;
            owner.Touch();
            return indices.Length;
        }

        public Action RemoveRangeWithUndo(IReadOnlyCollection<T> values)
        {
            (int Index, T Value)[] removed = values.Select(value => (Index: IndexOf(value), Value: value)).OrderBy(static value => value.Index).ToArray();
            if (removed.Any(static value => value.Index < 0) || removed.Select(static value => value.Index).Distinct().Count() != removed.Length)
                throw new InvalidOperationException("A conflicting paged timeline value is no longer present.");
            RemoveRange(values);
            return () =>
            {
                using IDisposable batch = owner.BeginBatchChange();
                foreach (var value in removed) InsertRange(value.Index, [value.Value]);
            };
        }

        public void Clear()
        {
            if (owner._count == 0) return;
            foreach (FacadeReference reference in _facades.Values)
                if (reference.Reference.TryGetTarget(out T? value)) owner._setChangeSink(value, null);
            _facades.Clear();
            _pending.Clear();
            owner.ResetPublishedStateToEmpty();
            owner._sharedOrdinalDirectory = null;
            owner._count = 0;
            owner.Touch();
        }
    }

    private sealed class ValueProjection(IReadOnlyList<T> values, Func<T, TValue> project) : IReadOnlyList<TValue>
    {
        public int Count => values.Count;
        public TValue this[int index] => project(values[index]);
        public IEnumerator<TValue> GetEnumerator() { foreach (T value in values) yield return project(value); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // A page-sized ID directory, shared by all value-only revisions. It avoids
    // an additional dictionary entry per note and still prunes monotonic and
    // locally reordered ID ranges. Ordinals remain revision-local addresses.
    private sealed class IndexedSourceOrdinals : ITimelineOrdinalLookup
    {
        private readonly Func<MidoraId, int> _find;
        public IndexedSourceOrdinals(IIndexedImmutableTimelineValueSource<TValue> source)
        {
            if (source.DetachedIdIndex is { } index)
                _find = id => index.TryFindOrdinalById(id, out int ordinal) ? ordinal : -1;
            else _find = id => source.TryFindOrdinalById(id, out int ordinal) ? ordinal : -1;
        }
        public int Find(MidoraId id) => _find(id);
    }

    private sealed class OrdinalDirectory(PersistentTimelineSequence<TValue> sequence, Func<TValue, MidoraId> getId) : ITimelineOrdinalLookup
    {
        private readonly object _gate = new();
        private PersistentTimelineSequence<TValue>? _source = sequence;
        private volatile PreparedIndex? _prepared;

        public int Find(MidoraId id)
        {
            Prepare();
            PreparedIndex index = _prepared!;
            return index.External is { } external
                ? external.TryFindOrdinalById(id, out int ordinal) ? ordinal : -1
                : Find(index.Root, id);
        }
        public void Prepare(CancellationToken token = default)
        {
            if (_prepared is not null) return;
            TimelineValueReadScope.ThrowIfReadWouldBlock();
            lock (_gate)
            {
                if (_prepared is not null) return;
                Node? root = Build(_source!, getId, token);
                token.ThrowIfCancellationRequested();
                _prepared = new(root, null);
                _source = null;
            }
        }

        public void Prepare(IImmutableTimelineOrdinalIndexBuilder builder, CancellationToken token = default)
        {
            if (_prepared?.External is not null) return;
            TimelineValueReadScope.ThrowIfReadWouldBlock();
            lock (_gate)
            {
                if (_prepared?.External is not null) return;
                // Formal order and Stable ID order may be arbitrarily different
                // after a saved Split/quantize/reorder. Min/max pruning of formal
                // pages is not an ID index: build a bounded, independently owned
                // sorted address index before resolving a large selection.
                IImmutableTimelineIdIndex external = builder.CreateOrdinalIndex(Addresses(), token);
                token.ThrowIfCancellationRequested();
                external.RetainForSourceLifetime();
                _prepared = new(null, external);
                _source = null;
            }
        }

        private IEnumerable<TimelineIdOrdinal> Addresses()
        {
            if (_source is { } sequence)
            {
                int ordinal = 0;
                foreach (TValue value in sequence.Enumerate()) yield return new(getId(value), ordinal++);
                yield break;
            }
            var stack = new Stack<Node>();
            if (_prepared?.Root is { } root) stack.Push(root);
            while (stack.TryPop(out Node? node))
            {
                if (node.Ids is { } ids)
                {
                    for (int i = 0; i < ids.Length; i++) yield return new(ids[i], node.Start + i);
                }
                else
                {
                    if (node.Right is { } right) stack.Push(right);
                    if (node.Left is { } left) stack.Push(left);
                }
            }
        }

        private sealed record PreparedIndex(Node? Root, IImmutableTimelineIdIndex? External);

        private int Find(Node? node, MidoraId id)
        {
            if (node is null || id.CompareTo(node.Minimum) < 0 || id.CompareTo(node.Maximum) > 0) return -1;
            if (node.Ids is not null)
            {
                for (int offset = 0; offset < node.Ids.Length; offset++)
                    if (node.Ids[offset] == id) return node.Start + offset;
                return -1;
            }
            int left = Find(node.Left, id);
            return left >= 0 ? left : Find(node.Right, id);
        }

        private static Node? Build(PersistentTimelineSequence<TValue> values, Func<TValue, MidoraId> getId,
            CancellationToken token)
        {
            List<Node> nodes = [];
            int start = 0;
            foreach (var leaf in values.EnumerateLeaves())
            {
                token.ThrowIfCancellationRequested();
                MidoraId minimum = getId(leaf.GetValue(0)), maximum = minimum;
                MidoraId[] ids = new MidoraId[leaf.Count];
                int offset = 0;
                foreach (TValue value in leaf.EnumerateValues())
                {
                    MidoraId id = getId(value);
                    ids[offset++] = id;
                    if (id.CompareTo(minimum) < 0) minimum = id;
                    if (id.CompareTo(maximum) > 0) maximum = id;
                }
                nodes.Add(new(minimum, maximum, start, ids, null, null));
                start += leaf.Count;
            }
            return Build(nodes, 0, nodes.Count);
        }

        private static Node? Build(List<Node> nodes, int first, int count)
        {
            if (count == 0) return null;
            if (count == 1) return nodes[first];
            int half = count / 2;
            Node left = Build(nodes, first, half)!, right = Build(nodes, first + half, count - half)!;
            return new(left.Minimum.CompareTo(right.Minimum) < 0 ? left.Minimum : right.Minimum,
                left.Maximum.CompareTo(right.Maximum) > 0 ? left.Maximum : right.Maximum,
                left.Start, null, left, right);
        }

        private sealed record Node(MidoraId Minimum, MidoraId Maximum, int Start,
            MidoraId[]? Ids, Node? Left, Node? Right);
    }
}
