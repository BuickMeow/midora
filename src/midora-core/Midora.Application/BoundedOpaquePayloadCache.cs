namespace Midora.Application;

/// <summary>A small, process-wide cache of exact payload byte arrays. Keys do
/// not retain an old Project/root; both byte and entry counts have hard limits.</summary>
internal static class BoundedOpaquePayloadCache
{
    private const long MaximumBytes = 8L * 1024 * 1024;
    private const int MaximumEntries = 8192;
    private static readonly object Sync = new();
    private static readonly Dictionary<(long Source, int Ordinal), LinkedListNode<Entry>> Entries = [];
    private static readonly LinkedList<Entry> Recent = [];
    private static long _identity, _bytes;
    public static long AllocateIdentity() => Interlocked.Increment(ref _identity);
    public static bool TryGet(long source, int ordinal, out ReadOnlyMemory<byte> bytes)
    {
        lock (Sync)
        {
            if (Entries.TryGetValue((source, ordinal), out var entry))
            { Recent.Remove(entry); Recent.AddFirst(entry); bytes = entry.Value.Bytes; return true; }
        }
        bytes = default; return false;
    }
    public static void Add(long source, int ordinal, ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length + 128L > MaximumBytes) return;
        lock (Sync)
        {
            if (Entries.ContainsKey((source, ordinal))) return;
            while (Entries.Count >= MaximumEntries || _bytes + bytes.Length + 128 > MaximumBytes)
            {
                var last = Recent.Last!; Recent.RemoveLast(); Entries.Remove((last.Value.Source, last.Value.Ordinal));
                _bytes -= last.Value.Bytes.Length + 128;
            }
            var entry = Recent.AddFirst(new Entry(source, ordinal, bytes.ToArray()));
            Entries.Add((source, ordinal), entry); _bytes += entry.Value.Bytes.Length + 128;
        }
    }
    private sealed record Entry(long Source, int Ordinal, ReadOnlyMemory<byte> Bytes);
}
