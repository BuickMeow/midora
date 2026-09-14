using Midora.Domain;

namespace Midora.Application;

/// <summary>Disposable, spillable display index. All methods that read it run off the UI thread.</summary>
public sealed class InstrumentChangeProjection : IDisposable
{
    private readonly BoundedImmutableValueSource<InstrumentChangeValue> _values;
    private InstrumentChangeProjection(BoundedImmutableValueSource<InstrumentChangeValue> values) => _values = values;
    private static readonly IComparer<InstrumentChangeValue> Order = Comparer<InstrumentChangeValue>.Create((a, b) =>
    { int time = a.Tick.CompareTo(b.Tick); return time != 0 ? time : a.Id.CompareTo(b.Id); });

    public static InstrumentChangeProjection Create(InstrumentChangeSet groups,
        Func<InstrumentChange, InstrumentChangeValue?> read, CancellationToken token)
    {
        var resources = new BoundedEditResources(new PagedEditResourceBudget(
            maximumWorkingBytes: 4L * 1024 * 1024, maximumResidentBytes: 4L * 1024 * 1024));
        using var context = BulkEditPreparationContext.Enter(token, resources: resources);
        var store = BoundedEditSort.Sort(Read(), Order, resources, token);
        try { return new(new BoundedImmutableValueSource<InstrumentChangeValue>(store)); }
        catch { store.Dispose(); throw; }
        IEnumerable<InstrumentChangeValue> Read()
        {
            foreach (var group in groups.Values)
            { token.ThrowIfCancellationRequested(); if (read(group) is { } value) yield return value; }
        }
    }

    private int LowerBound(long tick)
    {
        int lo = 0, hi = _values.Count;
        while (lo < hi) { int mid = lo + (hi - lo) / 2; if (_values[mid].Tick < tick) lo = mid + 1; else hi = mid; }
        return lo;
    }

    public InstrumentChangeValue? ReadAtTick(long tick, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int at = LowerBound(tick);
        return at < _values.Count && _values[at].Tick == tick ? _values[at] : null;
    }

    public InstrumentChangeValue[] ReadVisible(long start, long end, int pixels, CancellationToken token)
    {
        if (end <= start) return [];
        pixels = Math.Clamp(pixels, 1, 16_384);
        var values = new List<InstrumentChangeValue>(Math.Min(pixels, _values.Count));
        int at = LowerBound(start);
        double span = (double)(end - start);
        while (at < _values.Count)
        {
            token.ThrowIfCancellationRequested();
            var value = _values[at]; if (value.Tick >= end) break;
            values.Add(value);
            int column = Math.Clamp((int)((value.Tick - start) / span * pixels), 0, pixels - 1);
            // Jump to the next physical pixel through the sorted index, rather
            // than scanning every event at a dense Tick on every viewport change.
            long offset = (long)decimal.Ceiling((column + 1m) * (end - start) / pixels);
            long next = start + offset;
            if (next <= value.Tick) { if (value.Tick == long.MaxValue) break; next = value.Tick + 1; }
            at = Math.Max(at + 1, LowerBound(next));
        }
        return values.ToArray();
    }
    public void Dispose() => _values.Dispose();
}
