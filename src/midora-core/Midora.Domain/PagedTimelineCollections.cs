using System.Collections;
using System.Collections.ObjectModel;
using System.Numerics;

namespace Midora.Domain;

/// <summary>
/// Immutable render/compile value for a Logical Note page. This is an in-memory
/// paging boundary only; Format 1 persistence continues to use its frozen wire
/// representation.
/// </summary>
public readonly record struct LogicalNoteSnapshotValue(
    MidoraId Id,
    long StartTick,
    long LengthTicks,
    int Note,
    int Velocity);

/// <summary>
/// Immutable render/compile value for a SubVoice event page.
/// </summary>
public readonly record struct TemplateEventSnapshotValue(
    MidoraId Id,
    TemplateEventKind Kind,
    long Tick,
    long LengthTicks,
    int Number,
    int Value,
    int SecondaryValue,
    bool HasBankMsb,
    bool HasBankLsb,
    bool FollowPitchDelta);

public sealed class LogicalNoteQuerySnapshot
{
    private readonly PagedTimelineValueSnapshot<LogicalNoteSnapshotValue> _values;

    internal LogicalNoteQuerySnapshot(PagedTimelineValueSnapshot<LogicalNoteSnapshotValue> values) =>
        _values = values;

    public int Count => _values.Count;
    public long Generation => _values.Generation;
    public long MaximumEndTick => _values.MaximumEndTick;
    public ulong ContentFingerprint => _values.ContentFingerprint;

    public IEnumerable<LogicalNoteSnapshotValue> QueryValues(
        long startTick,
        long endTick,
        int minimumNote = 0,
        int maximumNote = 127) =>
        _values.Query(startTick, endTick, minimumNote, maximumNote);

    public IEnumerable<LogicalNoteSnapshotValue> EnumerateAll() => _values.EnumerateAll();

    public ulong GetRangeFingerprint(
        long startTick,
        long endTick,
        int minimumNote = 0,
        int maximumNote = 127) =>
        _values.GetRangeFingerprint(startTick, endTick, minimumNote, maximumNote);

    public void AccumulateStartColumns(long extent, Span<byte> destination) =>
        _values.AccumulateStartColumns(extent, destination);
}

public sealed class TemplateEventQuerySnapshot
{
    private const ulong NoteCategory = 1UL;
    private const ulong EventCategory = 2UL;
    private readonly PagedTimelineValueSnapshot<TemplateEventSnapshotValue> _values;

    internal TemplateEventQuerySnapshot(PagedTimelineValueSnapshot<TemplateEventSnapshotValue> values) =>
        _values = values;

    public int Count => _values.Count;
    public long Generation => _values.Generation;
    public long MaximumEndTick => _values.MaximumEndTick;
    public ulong ContentFingerprint => _values.ContentFingerprint;

    public IEnumerable<TemplateEventSnapshotValue> QueryNotes(
        long startTick,
        long endTick,
        int minimumNote = 0,
        int maximumNote = 127) =>
        _values.Query(startTick, endTick, minimumNote, maximumNote, NoteCategory)
            .Where(static value => value.Kind == TemplateEventKind.Note);

    public IEnumerable<TemplateEventSnapshotValue> QueryEvents(
        long startTick,
        long endTick) =>
        _values.Query(startTick, endTick, int.MinValue, int.MaxValue, EventCategory)
            .Where(static value => value.Kind != TemplateEventKind.Note);

    public IEnumerable<TemplateEventSnapshotValue> EnumerateAll() => _values.EnumerateAll();

    public ulong GetNoteRangeFingerprint(
        long startTick,
        long endTick,
        int minimumNote = 0,
        int maximumNote = 127) =>
        _values.GetRangeFingerprint(
            startTick,
            endTick,
            minimumNote,
            maximumNote,
            NoteCategory);

    public ulong GetEventRangeFingerprint(long startTick, long endTick) =>
        _values.GetRangeFingerprint(
            startTick,
            endTick,
            int.MinValue,
            int.MaxValue,
            EventCategory);

    public void AccumulateOverviewColumns(
        long extent,
        Span<byte> noteStartColumns,
        Span<byte> eventColumns)
    {
        if (extent <= 0) throw new ArgumentOutOfRangeException(nameof(extent));
        if (noteStartColumns.Length != eventColumns.Length)
            throw new ArgumentException("SubVoice overview channels must have equal widths.");
        foreach (TemplateEventSnapshotValue value in _values.EnumerateAll())
        {
            Span<byte> target = value.Kind == TemplateEventKind.Note
                ? noteStartColumns
                : eventColumns;
            PagedTimelineValueSnapshot<TemplateEventSnapshotValue>.MarkColumn(
                target,
                value.Tick,
                extent);
        }
    }

}

public sealed class LogicalNoteCollection : Collection<LogicalNote>
{
    private readonly PagedTimelineObjectList<LogicalNote, LogicalNoteSnapshotValue> _store;

    internal LogicalNoteCollection()
        : this(CreateStore())
    {
    }

    private LogicalNoteCollection(
        PagedTimelineObjectList<LogicalNote, LogicalNoteSnapshotValue> store)
        : base(store) => _store = store;

    public long Generation => _store.Generation;
    public int PageCount => _store.PageCount;

    public void AddRange(IEnumerable<LogicalNote> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        using IDisposable batch = _store.BeginBatchChange();
        foreach (LogicalNote value in values) Add(value);
    }

    public int RemoveRange(IReadOnlyCollection<LogicalNote> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return _store.RemoveRange(values);
    }

    internal Action RemoveRangeForExactCollision(IReadOnlyCollection<LogicalNote> values) =>
        _store.RemoveRangeForExactCollision(values);

    public IDisposable BeginBatchChange() => _store.BeginBatchChange();

    public LogicalNoteQuerySnapshot CreateQuerySnapshot() =>
        new(_store.CreateSnapshot());

    public bool TryGetById(MidoraId id, out LogicalNote? value) =>
        _store.TryGetById(id, out value);

    private static PagedTimelineObjectList<LogicalNote, LogicalNoteSnapshotValue> CreateStore() =>
        new(
            static value => new(
                value.Id,
                value.StartTick,
                value.LengthTicks,
                value.Note,
                value.Velocity),
            static value => value.Id,
            static value => value.StartTick,
            static value => SaturatingAdd(value.StartTick, Math.Max(1, value.LengthTicks)),
            static value => value.Note,
            static value => PagedTimelineFingerprint.ForLogicalNote(value),
            static (value, sink) => value.SetChangeSink(sink));

    private static long SaturatingAdd(long left, long right) =>
        right <= 0 || left > long.MaxValue - right ? long.MaxValue : left + right;
}

internal sealed class PagedTimelineObjectList<T, TValue> : IList<T>
    where T : class
{
    internal const int DefaultPageCapacity = 4096;

    private readonly Func<T, TValue> _toValue;
    private readonly Func<TValue, MidoraId> _getId;
    private readonly Func<TValue, long> _getStart;
    private readonly Func<TValue, long> _getEnd;
    private readonly Func<TValue, int> _getLane;
    private readonly Func<TValue, ulong> _getFingerprint;
    private readonly Func<TValue, ulong>? _getCategoryMask;
    private readonly Action<T, Action<T>?> _setChangeSink;
    private readonly List<Page> _pages = [];
    private readonly Dictionary<MidoraId, Entry> _entries = [];
    private Dictionary<T, int>? _duplicateReferenceCounts;
    private readonly HashSet<Page> _dirtyPages = [];
    private int _count;
    private int _batchDepth;
    private bool _batchChanged;
    private long _generation;

    public PagedTimelineObjectList(
        Func<T, TValue> toValue,
        Func<TValue, MidoraId> getId,
        Func<TValue, long> getStart,
        Func<TValue, long> getEnd,
        Func<TValue, int> getLane,
        Func<TValue, ulong> getFingerprint,
        Action<T, Action<T>?> setChangeSink,
        Func<TValue, ulong>? getCategoryMask = null)
    {
        _toValue = toValue ?? throw new ArgumentNullException(nameof(toValue));
        _getId = getId ?? throw new ArgumentNullException(nameof(getId));
        _getStart = getStart ?? throw new ArgumentNullException(nameof(getStart));
        _getEnd = getEnd ?? throw new ArgumentNullException(nameof(getEnd));
        _getLane = getLane ?? throw new ArgumentNullException(nameof(getLane));
        _getFingerprint = getFingerprint ?? throw new ArgumentNullException(nameof(getFingerprint));
        _setChangeSink = setChangeSink ?? throw new ArgumentNullException(nameof(setChangeSink));
        _getCategoryMask = getCategoryMask;
    }

    public int Count => _count;
    public bool IsReadOnly => false;
    public long Generation => _generation;
    public int PageCount => _pages.Count;

    public T this[int index]
    {
        get
        {
            (Page page, int localIndex) = Locate(index, allowEnd: false);
            return page.Items[localIndex];
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            (Page page, int localIndex) = Locate(index, allowEnd: false);
            T old = page.Items[localIndex];
            if (ReferenceEquals(old, value)) return;
            EnsureInsertable(value, old);
            page.Items[localIndex] = value;
            Detach(old);
            Attach(value, page);
            MarkChanged(page);
        }
    }

    public void Add(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Page page = _pages.Count == 0 || _pages[^1].Items.Count >= DefaultPageCapacity
            ? AddPage()
            : _pages[^1];
        EnsureInsertable(item);
        page.Items.Add(item);
        _count++;
        Attach(item, page);
        MarkChanged(page);
    }

    public void Clear()
    {
        if (_count == 0) return;
        foreach (Page page in _pages)
        {
            foreach (T item in page.Items) _setChangeSink(item, null);
        }
        _pages.Clear();
        _entries.Clear();
        _duplicateReferenceCounts?.Clear();
        _dirtyPages.Clear();
        _count = 0;
        Touch();
    }

    public bool Contains(T item) => item is not null
        && _entries.TryGetValue(_getId(_toValue(item)), out Entry? entry)
        && ReferenceEquals(entry.Item, item);

    public void CopyTo(T[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (arrayIndex < 0 || arrayIndex > array.Length - Count)
            throw new ArgumentOutOfRangeException(nameof(arrayIndex));
        foreach (T item in this) array[arrayIndex++] = item;
    }

    public IEnumerator<T> GetEnumerator()
    {
        foreach (Page page in _pages)
        {
            foreach (T item in page.Items) yield return item;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public int IndexOf(T item)
    {
        if (item is null
            || !_entries.TryGetValue(_getId(_toValue(item)), out Entry? entry)
            || !ReferenceEquals(entry.Item, item))
        {
            return -1;
        }
        int result = 0;
        foreach (Page page in _pages)
        {
            if (ReferenceEquals(page, entry.Page))
                return checked(result + page.Items.IndexOf(item));
            result = checked(result + page.Items.Count);
        }
        return -1;
    }

    public void Insert(int index, T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if ((uint)index > (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
        if (index == _count)
        {
            Add(item);
            return;
        }
        EnsureInsertable(item);
        (Page page, int localIndex) = Locate(index, allowEnd: false);
        page.Items.Insert(localIndex, item);
        _count++;
        Attach(item, page);
        if (page.Items.Count > DefaultPageCapacity) Split(page);
        else MarkChanged(page);
    }

    public bool Remove(T item)
    {
        if (item is null
            || !_entries.TryGetValue(_getId(_toValue(item)), out Entry? entry)
            || !ReferenceEquals(entry.Item, item))
        {
            return false;
        }
        int index = entry.Page.Items.IndexOf(item);
        if (index < 0) return false;
        entry.Page.Items.RemoveAt(index);
        _count--;
        Detach(item);
        if (entry.Page.Items.Count == 0)
        {
            _pages.Remove(entry.Page);
            _dirtyPages.Remove(entry.Page);
            Touch();
        }
        else
        {
            MarkChanged(entry.Page);
        }
        return true;
    }

    public int RemoveRange(IReadOnlyCollection<T> values)
    {
        if (values.Count == 0 || _count == 0) return 0;
        HashSet<T> requested = new(values, ReferenceEqualityComparer.Instance);
        int removed = 0;
        using IDisposable batch = BeginBatchChange();
        for (int pageIndex = _pages.Count - 1; pageIndex >= 0; pageIndex--)
        {
            Page page = _pages[pageIndex];
            List<T>? removedFromPage = null;
            int write = 0;
            for (int read = 0; read < page.Items.Count; read++)
            {
                T item = page.Items[read];
                if (requested.Contains(item))
                {
                    (removedFromPage ??= []).Add(item);
                    removed++;
                    continue;
                }
                if (write != read) page.Items[write] = item;
                write++;
            }
            if (write == page.Items.Count) continue;
            page.Items.RemoveRange(write, page.Items.Count - write);
            if (removedFromPage is not null)
            {
                foreach (T item in removedFromPage) Detach(item);
            }
            if (page.Items.Count == 0)
            {
                _pages.RemoveAt(pageIndex);
                _dirtyPages.Remove(page);
            }
            else
            {
                MarkChanged(page);
            }
        }
        if (removed != 0)
        {
            _count -= removed;
            Touch();
        }
        return removed;
    }

    public Action RemoveRangeForExactCollision(IReadOnlyCollection<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) return static () => { };
        HashSet<T> requested = new(values, ReferenceEqualityComparer.Instance);
        if (requested.Count != values.Count)
            throw new ArgumentException("Exact-collision values must be distinct.", nameof(values));

        List<CollisionPageRemoval> removals = [];
        for (int pageIndex = 0; pageIndex < _pages.Count; pageIndex++)
        {
            Page page = _pages[pageIndex];
            List<(int Index, T Value)> matches = [];
            for (int localIndex = 0; localIndex < page.Items.Count; localIndex++)
            {
                T value = page.Items[localIndex];
                if (requested.Remove(value)) matches.Add((localIndex, value));
            }
            if (matches.Count != 0) removals.Add(new(page, pageIndex, matches));
        }
        if (requested.Count != 0)
            throw new InvalidOperationException("A conflicting paged timeline value is no longer present.");

        int removedCount = 0;
        using (BeginBatchChange())
        {
            foreach (CollisionPageRemoval removal in removals)
            {
                HashSet<T> removed = new(
                    removal.Values.Select(static value => value.Value),
                    ReferenceEqualityComparer.Instance);
                List<T>? removedFromPage = null;
                int write = 0;
                for (int read = 0; read < removal.Page.Items.Count; read++)
                {
                    T value = removal.Page.Items[read];
                    if (removed.Contains(value))
                    {
                        (removedFromPage ??= []).Add(value);
                        removedCount++;
                        continue;
                    }
                    if (write != read) removal.Page.Items[write] = value;
                    write++;
                }
                removal.Page.Items.RemoveRange(write, removal.Page.Items.Count - write);
                if (removedFromPage is not null)
                {
                    foreach (T value in removedFromPage) Detach(value);
                }
                if (removal.Page.Items.Count == 0)
                {
                    _pages.Remove(removal.Page);
                    _dirtyPages.Remove(removal.Page);
                    Touch();
                }
                else
                {
                    MarkChanged(removal.Page);
                }
            }
            _count -= removedCount;
            Touch();
        }

        return () =>
        {
            foreach (CollisionPageRemoval removal in removals)
            {
                if (removal.Values.Any(value => _entries.ContainsKey(_getId(_toValue(value.Value)))))
                    throw new InvalidOperationException("A conflicting paged timeline value is already restored.");
            }
            using IDisposable batch = BeginBatchChange();
            foreach (CollisionPageRemoval removal in removals.OrderBy(static value => value.OriginalPageIndex))
            {
                if (!_pages.Contains(removal.Page))
                    _pages.Insert(Math.Min(removal.OriginalPageIndex, _pages.Count), removal.Page);
                int finalCount = checked(removal.Page.Items.Count + removal.Values.Count);
                List<T> restored = new(finalCount);
                int liveIndex = 0;
                int removedIndex = 0;
                for (int index = 0; index < finalCount; index++)
                {
                    if (removedIndex < removal.Values.Count
                        && removal.Values[removedIndex].Index == index)
                    {
                        restored.Add(removal.Values[removedIndex++].Value);
                    }
                    else
                    {
                        if ((uint)liveIndex >= (uint)removal.Page.Items.Count)
                            throw new InvalidOperationException("A paged timeline page changed before restoration.");
                        restored.Add(removal.Page.Items[liveIndex++]);
                    }
                }
                if (removedIndex != removal.Values.Count || liveIndex != removal.Page.Items.Count)
                    throw new InvalidOperationException("A paged timeline page cannot be restored exactly.");
                removal.Page.Items.Clear();
                removal.Page.Items.AddRange(restored);
                foreach ((int _, T value) in removal.Values) Attach(value, removal.Page);
                MarkChanged(removal.Page);
            }
            _count += removedCount;
            Touch();
        };
    }

    public void RemoveAt(int index)
    {
        (Page page, int localIndex) = Locate(index, allowEnd: false);
        _ = Remove(page.Items[localIndex]);
    }

    public IDisposable BeginBatchChange()
    {
        _batchDepth++;
        return new BatchScope(this);
    }

    public bool TryGetById(MidoraId id, out T? value)
    {
        if (_entries.TryGetValue(id, out Entry? entry))
        {
            value = entry.Item;
            return true;
        }
        value = null;
        return false;
    }

    public PagedTimelineValueSnapshot<TValue> CreateSnapshot()
    {
        FlushDirtyPages();
        return new(
            _pages.Select(static page => page.Snapshot!).ToArray(),
            _count,
            _generation,
            _getStart,
            _getEnd,
            _getLane,
            _getFingerprint);
    }

    private Page AddPage()
    {
        Page result = new();
        _pages.Add(result);
        return result;
    }

    private void Split(Page page)
    {
        int index = _pages.IndexOf(page);
        int splitAt = page.Items.Count / 2;
        Page right = new();
        right.Items.AddRange(page.Items.GetRange(splitAt, page.Items.Count - splitAt));
        page.Items.RemoveRange(splitAt, page.Items.Count - splitAt);
        _pages.Insert(index + 1, right);
        foreach (T item in right.Items)
        {
            if (_duplicateReferenceCounts?.ContainsKey(item) == true) continue;
            _entries[_getId(_toValue(item))].Page = right;
        }
        if (_duplicateReferenceCounts is not null)
        {
            HashSet<T> movedDuplicates = new(ReferenceEqualityComparer.Instance);
            foreach (T item in right.Items)
            {
                if (_duplicateReferenceCounts.ContainsKey(item)) movedDuplicates.Add(item);
            }
            foreach (T item in movedDuplicates) RefreshDuplicateReference(item);
        }
        MarkChanged(page);
        MarkChanged(right);
    }

    private void Attach(T item, Page page)
    {
        MidoraId id = _getId(_toValue(item));
        if (_entries.TryGetValue(id, out Entry? existing))
        {
            if (!ReferenceEquals(existing.Item, item))
                throw new InvalidOperationException("A paged timeline collection cannot contain distinct objects with duplicate Stable IDs.");
            _duplicateReferenceCounts ??= new(ReferenceEqualityComparer.Instance);
            _duplicateReferenceCounts[item] = _duplicateReferenceCounts.TryGetValue(item, out int count)
                ? checked(count + 1)
                : 2;
        }
        else
        {
            _entries.Add(id, new(item, page));
        }
        _setChangeSink(item, OnItemChanged);
    }

    private void Detach(T item)
    {
        if (_duplicateReferenceCounts?.ContainsKey(item) == true)
        {
            RefreshDuplicateReference(item);
            return;
        }
        _entries.Remove(_getId(_toValue(item)));
        _setChangeSink(item, null);
    }

    private void OnItemChanged(T item)
    {
        if (_duplicateReferenceCounts?.ContainsKey(item) == true)
        {
            foreach (Page page in _pages)
            {
                if (page.Items.Any(value => ReferenceEquals(value, item))) MarkChanged(page);
            }
            return;
        }
        MidoraId id = _getId(_toValue(item));
        if (_entries.TryGetValue(id, out Entry? entry)
            && ReferenceEquals(entry.Item, item))
        {
            MarkChanged(entry.Page);
        }
    }

    private void EnsureInsertable(T value, T? replacing = null)
    {
        MidoraId id = _getId(_toValue(value));
        if (id == default) throw new ArgumentOutOfRangeException(nameof(id));
        if (_entries.TryGetValue(id, out Entry? existing)
            && !ReferenceEquals(existing.Item, value)
            && (!ReferenceEquals(existing.Item, replacing)
                || replacing is not null
                && _duplicateReferenceCounts?.ContainsKey(replacing) == true))
        {
            throw new InvalidOperationException("A paged timeline collection cannot contain duplicate Stable IDs.");
        }
    }

    private void RefreshDuplicateReference(T item)
    {
        MidoraId id = _getId(_toValue(item));
        Page? firstPage = null;
        int count = 0;
        foreach (Page page in _pages)
        {
            foreach (T candidate in page.Items)
            {
                if (!ReferenceEquals(candidate, item)) continue;
                firstPage ??= page;
                count++;
            }
        }
        if (count == 0)
        {
            _entries.Remove(id);
            _duplicateReferenceCounts!.Remove(item);
            _setChangeSink(item, null);
            return;
        }
        _entries[id] = new(item, firstPage!);
        if (count == 1)
            _duplicateReferenceCounts!.Remove(item);
        else
            _duplicateReferenceCounts![item] = count;
    }

    private (Page Page, int LocalIndex) Locate(int index, bool allowEnd)
    {
        if (index < 0 || index > _count || !allowEnd && index == _count)
            throw new ArgumentOutOfRangeException(nameof(index));
        int remaining = index;
        foreach (Page page in _pages)
        {
            if (remaining < page.Items.Count) return (page, remaining);
            remaining -= page.Items.Count;
        }
        throw new ArgumentOutOfRangeException(nameof(index));
    }

    private void MarkChanged(Page page)
    {
        _dirtyPages.Add(page);
        Touch();
    }

    private void Touch()
    {
        if (_batchDepth != 0)
        {
            _batchChanged = true;
            return;
        }
        _generation++;
    }

    private void FlushDirtyPages()
    {
        foreach (Page page in _dirtyPages)
        {
            if (!_pages.Contains(page)) continue;
            TValue[] values = page.Items.Select(_toValue).ToArray();
            page.Snapshot = new(
                values,
                _getStart,
                _getEnd,
                _getLane,
                _getFingerprint,
                _getCategoryMask);
        }
        _dirtyPages.Clear();
    }

    private void EndBatch()
    {
        if (_batchDepth <= 0) throw new InvalidOperationException("Paged timeline batch scope is unbalanced.");
        _batchDepth--;
        if (_batchDepth != 0 || !_batchChanged) return;
        _batchChanged = false;
        _generation++;
    }

    private sealed class Page
    {
        public List<T> Items { get; } = new(DefaultPageCapacity);
        public PagedTimelineValuePage<TValue>? Snapshot { get; set; }
    }

    private sealed class Entry(T item, Page page)
    {
        public T Item { get; } = item;
        public Page Page { get; set; } = page;
    }

    private sealed record CollisionPageRemoval(
        Page Page,
        int OriginalPageIndex,
        IReadOnlyList<(int Index, T Value)> Values);

    private sealed class BatchScope(PagedTimelineObjectList<T, TValue> owner) : IDisposable
    {
        private PagedTimelineObjectList<T, TValue>? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndBatch();
    }
}

internal sealed class PagedTimelineValuePage<TValue>
{
    private const int FingerprintBlockSize = 128;
    private readonly Func<TValue, long> _getStart;
    private readonly Func<TValue, long> _getEnd;
    private readonly Func<TValue, int> _getLane;
    private readonly Func<TValue, ulong> _getFingerprint;
    private readonly Func<TValue, ulong>? _getCategoryMask;
    private readonly FingerprintBlock[] _fingerprintBlocks;

    public PagedTimelineValuePage(
        TValue[] values,
        Func<TValue, long> getStart,
        Func<TValue, long> getEnd,
        Func<TValue, int> getLane,
        Func<TValue, ulong> getFingerprint,
        Func<TValue, ulong>? getCategoryMask)
    {
        Values = values;
        _getStart = getStart;
        _getEnd = getEnd;
        _getLane = getLane;
        _getFingerprint = getFingerprint;
        _getCategoryMask = getCategoryMask;
        if (values.Length == 0)
        {
            MinimumStartTick = long.MaxValue;
            MaximumEndTick = 0;
            MinimumLane = int.MaxValue;
            MaximumLane = int.MinValue;
            ContentFingerprint = PagedTimelineFingerprint.Offset;
            CategoryMask = 0;
            _fingerprintBlocks = [];
            return;
        }
        long minimumStart = long.MaxValue;
        long maximumEnd = 0;
        int minimumLane = int.MaxValue;
        int maximumLane = int.MinValue;
        ulong fingerprint = PagedTimelineFingerprint.Offset;
        ulong categoryMask = getCategoryMask is null ? ulong.MaxValue : 0;
        foreach (TValue value in values)
        {
            minimumStart = Math.Min(minimumStart, getStart(value));
            maximumEnd = Math.Max(maximumEnd, getEnd(value));
            int lane = getLane(value);
            minimumLane = Math.Min(minimumLane, lane);
            maximumLane = Math.Max(maximumLane, lane);
            PagedTimelineFingerprint.Add(ref fingerprint, getFingerprint(value));
            if (getCategoryMask is not null) categoryMask |= getCategoryMask(value);
        }
        PagedTimelineFingerprint.Add(ref fingerprint, unchecked((ulong)values.Length));
        MinimumStartTick = minimumStart;
        MaximumEndTick = maximumEnd;
        MinimumLane = minimumLane;
        MaximumLane = maximumLane;
        ContentFingerprint = fingerprint;
        CategoryMask = categoryMask;
        _fingerprintBlocks = BuildFingerprintBlocks(values);
    }

    public TValue[] Values { get; }
    public long MinimumStartTick { get; }
    public long MaximumEndTick { get; }
    public int MinimumLane { get; }
    public int MaximumLane { get; }
    public ulong ContentFingerprint { get; }
    public ulong CategoryMask { get; }

    public void AccumulateRangeFingerprint(
        ref PagedTimelineRangeFingerprintAggregate aggregate,
        long startTick,
        long endTick,
        int minimumLane,
        int maximumLane,
        ulong requiredCategoryMask)
    {
        foreach (FingerprintBlock block in _fingerprintBlocks)
        {
            if (block.MaximumEndTick <= startTick
                || block.MinimumStartTick >= endTick
                || block.MaximumLane < minimumLane
                || block.MinimumLane > maximumLane
                || (block.CategoryMask & requiredCategoryMask) == 0)
            {
                continue;
            }
            if (startTick <= block.MinimumStartTick
                && endTick >= block.MaximumEndTick
                && minimumLane <= block.MinimumLane
                && maximumLane >= block.MaximumLane
                && (requiredCategoryMask == ulong.MaxValue
                    || (block.CategoryMask & ~requiredCategoryMask) == 0))
            {
                aggregate.Combine(block.Aggregate);
                continue;
            }
            int end = checked(block.First + block.Count);
            for (int index = block.First; index < end; index++)
            {
                TValue value = Values[index];
                int lane = _getLane(value);
                ulong categoryMask = _getCategoryMask?.Invoke(value) ?? ulong.MaxValue;
                if (_getStart(value) < endTick
                    && _getEnd(value) > startTick
                    && lane >= minimumLane
                    && lane <= maximumLane
                    && (categoryMask & requiredCategoryMask) != 0)
                {
                    aggregate.Add(_getFingerprint(value));
                }
            }
        }
    }

    private FingerprintBlock[] BuildFingerprintBlocks(TValue[] values)
    {
        FingerprintBlock[] result = new FingerprintBlock[
            (values.Length + FingerprintBlockSize - 1) / FingerprintBlockSize];
        for (int blockIndex = 0; blockIndex < result.Length; blockIndex++)
        {
            int first = checked(blockIndex * FingerprintBlockSize);
            int count = Math.Min(FingerprintBlockSize, values.Length - first);
            long minimumStartTick = long.MaxValue;
            long maximumEndTick = 0;
            int minimumLane = int.MaxValue;
            int maximumLane = int.MinValue;
            ulong categoryMask = _getCategoryMask is null ? ulong.MaxValue : 0;
            PagedTimelineRangeFingerprintAggregate aggregate = default;
            for (int index = first; index < first + count; index++)
            {
                TValue value = values[index];
                minimumStartTick = Math.Min(minimumStartTick, _getStart(value));
                maximumEndTick = Math.Max(maximumEndTick, _getEnd(value));
                int lane = _getLane(value);
                minimumLane = Math.Min(minimumLane, lane);
                maximumLane = Math.Max(maximumLane, lane);
                categoryMask |= _getCategoryMask?.Invoke(value) ?? ulong.MaxValue;
                aggregate.Add(_getFingerprint(value));
            }
            result[blockIndex] = new(
                first,
                count,
                minimumStartTick,
                maximumEndTick,
                minimumLane,
                maximumLane,
                categoryMask,
                aggregate);
        }
        return result;
    }

    private readonly record struct FingerprintBlock(
        int First,
        int Count,
        long MinimumStartTick,
        long MaximumEndTick,
        int MinimumLane,
        int MaximumLane,
        ulong CategoryMask,
        PagedTimelineRangeFingerprintAggregate Aggregate);
}

internal sealed class PagedTimelineValueSnapshot<TValue>
{
    private readonly PagedTimelineValuePage<TValue>[] _pages;
    private readonly Func<TValue, long> _getStart;
    private readonly Func<TValue, long> _getEnd;
    private readonly Func<TValue, int> _getLane;
    private readonly Func<TValue, ulong> _getFingerprint;

    public PagedTimelineValueSnapshot(
        PagedTimelineValuePage<TValue>[] pages,
        int count,
        long generation,
        Func<TValue, long> getStart,
        Func<TValue, long> getEnd,
        Func<TValue, int> getLane,
        Func<TValue, ulong> getFingerprint)
    {
        _pages = pages;
        _getStart = getStart;
        _getEnd = getEnd;
        _getLane = getLane;
        _getFingerprint = getFingerprint;
        Count = count;
        Generation = generation;
        MaximumEndTick = pages.Length == 0 ? 0 : pages.Max(static page => page.MaximumEndTick);
        ulong fingerprint = PagedTimelineFingerprint.Offset;
        foreach (PagedTimelineValuePage<TValue> page in pages)
            PagedTimelineFingerprint.Add(ref fingerprint, page.ContentFingerprint);
        PagedTimelineFingerprint.Add(ref fingerprint, unchecked((ulong)count));
        ContentFingerprint = fingerprint;
    }

    public int Count { get; }
    public long Generation { get; }
    public long MaximumEndTick { get; }
    public ulong ContentFingerprint { get; }

    public IEnumerable<TValue> Query(
        long startTick,
        long endTick,
        int minimumLane,
        int maximumLane,
        ulong requiredCategoryMask = ulong.MaxValue)
    {
        if (endTick <= startTick || maximumLane < minimumLane) yield break;
        foreach (PagedTimelineValuePage<TValue> page in _pages)
        {
            if (page.MaximumEndTick <= startTick
                || page.MinimumStartTick >= endTick
                || page.MaximumLane < minimumLane
                || page.MinimumLane > maximumLane
                || (page.CategoryMask & requiredCategoryMask) == 0)
            {
                continue;
            }
            foreach (TValue value in page.Values)
            {
                int lane = _getLane(value);
                if (_getStart(value) < endTick
                    && _getEnd(value) > startTick
                    && lane >= minimumLane
                    && lane <= maximumLane)
                {
                    yield return value;
                }
            }
        }
    }

    public IEnumerable<TValue> EnumerateAll()
    {
        foreach (PagedTimelineValuePage<TValue> page in _pages)
        {
            foreach (TValue value in page.Values) yield return value;
        }
    }

    public ulong GetRangeFingerprint(
        long startTick,
        long endTick,
        int minimumLane,
        int maximumLane,
        ulong requiredCategoryMask = ulong.MaxValue)
    {
        PagedTimelineRangeFingerprintAggregate aggregate = default;
        if (endTick <= startTick || maximumLane < minimumLane)
        {
            return aggregate.ToFingerprint();
        }
        foreach (PagedTimelineValuePage<TValue> page in _pages)
        {
            if (page.MaximumEndTick <= startTick
                || page.MinimumStartTick >= endTick
                || page.MaximumLane < minimumLane
                || page.MinimumLane > maximumLane
                || (page.CategoryMask & requiredCategoryMask) == 0)
            {
                continue;
            }
            page.AccumulateRangeFingerprint(
                ref aggregate,
                startTick,
                endTick,
                minimumLane,
                maximumLane,
                requiredCategoryMask);
        }
        return aggregate.ToFingerprint();
    }

    public void AccumulateStartColumns(long extent, Span<byte> destination)
    {
        if (extent <= 0) throw new ArgumentOutOfRangeException(nameof(extent));
        foreach (TValue value in EnumerateAll()) MarkColumn(destination, _getStart(value), extent);
    }

    internal static void MarkColumn(Span<byte> destination, long tick, long extent)
    {
        if (destination.IsEmpty) return;
        int column = Math.Clamp(
            (int)(Math.Max(0, tick) / (double)extent * destination.Length),
            0,
            destination.Length - 1);
        destination[column] = 1;
    }
}

internal struct PagedTimelineRangeFingerprintAggregate
{
    private ulong _xor;
    private ulong _sum;
    private ulong _rotatedSum;
    private ulong _count;

    public void Add(ulong value)
    {
        ulong mixed = Mix(value);
        _xor ^= mixed;
        _sum = unchecked(_sum + mixed);
        _rotatedSum = unchecked(_rotatedSum + BitOperations.RotateLeft(mixed, 23));
        _count++;
    }

    public void Combine(PagedTimelineRangeFingerprintAggregate other)
    {
        _xor ^= other._xor;
        _sum = unchecked(_sum + other._sum);
        _rotatedSum = unchecked(_rotatedSum + other._rotatedSum);
        _count = unchecked(_count + other._count);
    }

    public readonly ulong ToFingerprint()
    {
        ulong fingerprint = PagedTimelineFingerprint.Offset;
        PagedTimelineFingerprint.Add(ref fingerprint, _xor);
        PagedTimelineFingerprint.Add(ref fingerprint, _sum);
        PagedTimelineFingerprint.Add(ref fingerprint, _rotatedSum);
        PagedTimelineFingerprint.Add(ref fingerprint, _count);
        return fingerprint;
    }

    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value *= 0xbf58476d1ce4e5b9UL;
        value ^= value >> 27;
        value *= 0x94d049bb133111ebUL;
        return value ^ (value >> 31);
    }
}

internal static class PagedTimelineFingerprint
{
    internal const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    internal static void Add(ref ulong hash, ulong value)
    {
        hash ^= value;
        hash *= Prime;
    }

    internal static ulong ForLogicalNote(LogicalNoteSnapshotValue value)
    {
        ulong hash = Offset;
        Add(ref hash, unchecked((ulong)value.Id.Value));
        Add(ref hash, unchecked((ulong)value.StartTick));
        Add(ref hash, unchecked((ulong)value.LengthTicks));
        Add(ref hash, unchecked((ulong)value.Note));
        Add(ref hash, unchecked((ulong)value.Velocity));
        return hash;
    }

    internal static ulong ForTemplateEvent(TemplateEventSnapshotValue value)
    {
        ulong hash = Offset;
        Add(ref hash, unchecked((ulong)value.Id.Value));
        Add(ref hash, unchecked((ulong)value.Kind));
        Add(ref hash, unchecked((ulong)value.Tick));
        Add(ref hash, unchecked((ulong)value.LengthTicks));
        Add(ref hash, unchecked((ulong)value.Number));
        Add(ref hash, unchecked((ulong)value.Value));
        Add(ref hash, unchecked((ulong)value.SecondaryValue));
        Add(ref hash, value.HasBankMsb ? 1UL : 0UL);
        Add(ref hash, value.HasBankLsb ? 1UL : 0UL);
        Add(ref hash, value.FollowPitchDelta ? 1UL : 0UL);
        return hash;
    }
}
