namespace Midora.Avalonia.Presentation.Rendering;

/// <summary>
/// Chunked per-lane tick index over render items that are sorted by
/// (lane, start tick, end tick, kind, id).
///
/// Items of one lane are a contiguous range of the shared array, and every lane is split into
/// units of <see cref="UnitSize"/> items (unit c covers items [c*UnitSize, ...)), so a unit's
/// input range is computed from the unit id without any lookup table. A unit intersects the tick
/// range [start, end) iff unitStart &lt;= end AND unitMaxEnd &gt; start. Building the same index with
/// a larger unit size yields a coarser level of detail over the identical items.
///
/// chunkMaxEnd is not monotonic (one long note inflates its chunk), so each block of
/// <see cref="BlockChunks"/> chunks stores the block prefix max (non-decreasing) and the block
/// suffix max. The first intersecting chunk is found by binary searching the block prefix max and
/// scanning at most one block of chunks; the last chunk whose first note starts before the range end
/// is found by binary searching the monotonic chunkStart array. Long notes therefore only cost their
/// own block, and the visited range is O(visible items) instead of O(lane prefix).
/// </summary>
public sealed class TimelineLaneChunkIndex
{
    /// <summary>Default items per unit.</summary>
    public const int UnitSize = 256;

    /// <summary>Items per unit in the query index; kept as the historical name.</summary>
    public const int ChunkSize = UnitSize;

    /// <summary>Chunks per index block; bounds the scan after the block binary search.</summary>
    public const int BlockChunks = 64;

    private readonly TimelineRenderItem[] _items;
    private readonly int _unitSize;
    private readonly int[] _laneFirstItem;
    private readonly int[] _laneItemCount;
    private readonly long[][] _laneChunkStart;
    private readonly long[][] _laneChunkMaxEnd;
    private readonly long[][] _laneBlockPrefixMax;

    private TimelineLaneChunkIndex(
        TimelineRenderItem[] items,
        int unitSize,
        int[] laneFirstItem,
        int[] laneItemCount,
        long[][] laneChunkStart,
        long[][] laneChunkMaxEnd,
        long[][] laneBlockPrefixMax,
        int chunkCount,
        int blockEntryCount)
    {
        _items = items;
        _unitSize = unitSize;
        _laneFirstItem = laneFirstItem;
        _laneItemCount = laneItemCount;
        _laneChunkStart = laneChunkStart;
        _laneChunkMaxEnd = laneChunkMaxEnd;
        _laneBlockPrefixMax = laneBlockPrefixMax;
        ChunkCount = chunkCount;
        BlockEntryCount = blockEntryCount;
    }

    public int LaneCount => _laneFirstItem.Length;

    /// <summary>Total number of chunks, one per <see cref="ChunkSize"/> items.</summary>
    public int ChunkCount { get; }

    /// <summary>Total number of block prefix entries, one per <see cref="BlockChunks"/> chunks.</summary>
    public int BlockEntryCount { get; }

    /// <summary>Approximate index bytes excluding the shared item array, used by review traces.</summary>
    public long IndexBytes =>
        (long)ChunkCount * sizeof(long) * 2 + (long)BlockEntryCount * sizeof(long);

    /// <summary>
    /// Builds the index. The item array must already be sorted by
    /// (lane, start tick, end tick, kind, id), which is what <see cref="BuildSorted"/> verifies.
    /// </summary>
    public static TimelineLaneChunkIndex BuildSorted(TimelineRenderItem[] items) =>
        BuildSorted(items, UnitSize);

    /// <summary>
    /// Builds the index with an explicit unit size. A larger unit merges more items per envelope,
    /// which is how the level of detail ladder is produced from the same sorted item array.
    /// </summary>
    public static TimelineLaneChunkIndex BuildSorted(TimelineRenderItem[] items, int unitSize)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfLessThan(unitSize, 1);
        int laneCount = 1;
        for (int index = 0; index < items.Length; index++)
        {
            if (index > 0 && !InOrder(items[index - 1], items[index]))
            {
                throw new ArgumentException(
                    "The render items must be sorted by lane, start tick, end tick, kind and id.",
                    nameof(items));
            }

            int lane = Math.Clamp(items[index].Lane, 0, int.MaxValue);
            if (lane + 1 > laneCount)
            {
                laneCount = lane + 1;
            }
        }

        int[] counts = new int[laneCount];
        foreach (TimelineRenderItem item in items)
        {
            counts[Math.Clamp(item.Lane, 0, laneCount - 1)]++;
        }

        int[] laneFirst = new int[laneCount];
        int running = 0;
        for (int lane = 0; lane < laneCount; lane++)
        {
            laneFirst[lane] = running;
            running += counts[lane];
        }

        long[][] chunkStart = new long[laneCount][];
        long[][] chunkMaxEnd = new long[laneCount][];
        long[][] blockPrefixMax = new long[laneCount][];
        int chunkTotal = 0;
        int blockTotal = 0;
        for (int lane = 0; lane < laneCount; lane++)
        {
            int count = counts[lane];
            if (count == 0)
            {
                chunkStart[lane] = [];
                chunkMaxEnd[lane] = [];
                blockPrefixMax[lane] = [];
                continue;
            }

            int chunks = (count + unitSize - 1) / unitSize;
            long[] starts = new long[chunks];
            long[] maxEnds = new long[chunks];
            long[] blocks = new long[(chunks + BlockChunks - 1) / BlockChunks];
            int first = laneFirst[lane];
            long blockPrefix = long.MinValue;
            for (int chunk = 0; chunk < chunks; chunk++)
            {
                int from = first + chunk * unitSize;
                int to = Math.Min(first + count, from + unitSize);
                starts[chunk] = items[from].StartTick;
                long maximumEnd = long.MinValue;
                for (int index = from; index < to; index++)
                {
                    if (items[index].EndTick > maximumEnd)
                    {
                        maximumEnd = items[index].EndTick;
                    }
                }

                maxEnds[chunk] = maximumEnd;
                blockPrefix = Math.Max(blockPrefix, maximumEnd);
                if (chunk % BlockChunks == BlockChunks - 1 || chunk == chunks - 1)
                {
                    // One entry per block holding the prefix max up to the end of that block, so the
                    // array stays non-decreasing and the block binary search is exact.
                    blocks[chunk / BlockChunks] = blockPrefix;
                }
            }
            chunkStart[lane] = starts;
            chunkMaxEnd[lane] = maxEnds;
            blockPrefixMax[lane] = blocks;
            chunkTotal += chunks;
            blockTotal += blocks.Length;
        }

        return new TimelineLaneChunkIndex(
            items,
            unitSize,
            laneFirst,
            counts,
            chunkStart,
            chunkMaxEnd,
            blockPrefixMax,
            chunkTotal,
            blockTotal);
    }

    /// <summary>Collects intersecting items into a list without a delegate per item.</summary>
    public void QueryInto(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        List<TimelineRenderItem> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ListSink sink = new(destination);
        VisitRange(startTick, endTick, firstLane, lastLaneExclusive, ref sink);
    }

    public void VisitInto(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        Action<TimelineRenderItem> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        ActionSink sink = new(visitor);
        VisitRange(startTick, endTick, firstLane, lastLaneExclusive, ref sink);
    }

    /// <summary>Counts intersecting items without materializing them.</summary>
    public int Count(long startTick, long endTick, int firstLane, int lastLaneExclusive)
    {
        CountingSink sink = new();
        VisitRange(startTick, endTick, firstLane, lastLaneExclusive, ref sink);
        return sink.Count;
    }

    /// <summary>
    /// Sink for chunk level aggregation (LOD). A chunk is emitted once with its envelope
    /// [startTick, endTick] and item count; the caller unions envelopes instead of visiting items.
    /// </summary>
    public interface IChunkSink
    {
        void Chunk(int lane, long startTick, long endTick, int itemCount);
    }

    /// <summary>
    /// Visits every chunk that can intersect the tick range, with the chunk's first note start and
    /// maximum end tick. Envelopes are conservative: a chunk is emitted whenever any of its notes
    /// can intersect, so the union of emitted envelopes covers every intersecting note.
    /// </summary>
    public void VisitChunks<TChunkSink>(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        ref TChunkSink sink)
        where TChunkSink : struct, IChunkSink
    {
        long effectiveStart = Math.Max(0, startTick);
        if (endTick <= effectiveStart || firstLane >= lastLaneExclusive)
        {
            return;
        }

        int first = Math.Max(0, firstLane);
        int last = Math.Min(lastLaneExclusive, _laneFirstItem.Length);
        for (int lane = first; lane < last; lane++)
        {
            int count = _laneItemCount[lane];
            if (count == 0)
            {
                continue;
            }

            long[] starts = _laneChunkStart[lane];
            long[] maxEnds = _laneChunkMaxEnd[lane];
            int firstChunk = FirstChunkReachingInto(lane, effectiveStart);
            if (firstChunk >= starts.Length)
            {
                continue;
            }

            int lastChunk = LastChunkStartingBefore(starts, endTick);
            if (lastChunk < firstChunk)
            {
                continue;
            }

            int laneFirst = _laneFirstItem[lane];
            for (int chunk = firstChunk; chunk <= lastChunk; chunk++)
            {
                int from = laneFirst + chunk * _unitSize;
                int to = Math.Min(laneFirst + count, from + _unitSize);
                sink.Chunk(lane, starts[chunk], maxEnds[chunk], to - from);
            }
        }
    }

    /// <summary>
    /// Sink used by the traversal. A struct sink keeps the hot loop free of delegates and closures,
    /// which dominated the query cost when a large range was collected.
    /// </summary>
    public interface IItemSink
    {
        void Add(in TimelineRenderItem item);
    }

    private struct ListSink(List<TimelineRenderItem> destination) : IItemSink
    {
        public void Add(in TimelineRenderItem item) => destination.Add(item);
    }

    private struct ActionSink(Action<TimelineRenderItem> visitor) : IItemSink
    {
        public void Add(in TimelineRenderItem item) => visitor(item);
    }

    private struct CountingSink : IItemSink
    {
        public int Count;

        public void Add(in TimelineRenderItem item) => Count++;
    }

    private void VisitRange<TItemSink>(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        ref TItemSink sink)
        where TItemSink : struct, IItemSink
    {
        long effectiveStart = Math.Max(0, startTick);
        if (endTick <= effectiveStart || firstLane >= lastLaneExclusive)
        {
            return;
        }

        int first = Math.Max(0, firstLane);
        int last = Math.Min(lastLaneExclusive, _laneFirstItem.Length);
        for (int lane = first; lane < last; lane++)
        {
            int count = _laneItemCount[lane];
            if (count == 0)
            {
                continue;
            }

            long[] starts = _laneChunkStart[lane];
            long[] maxEnds = _laneChunkMaxEnd[lane];
            int firstChunk = FirstChunkReachingInto(lane, effectiveStart);
            if (firstChunk >= starts.Length)
            {
                continue;
            }

            int lastChunk = LastChunkStartingBefore(starts, endTick);
            if (lastChunk < firstChunk)
            {
                continue;
            }

            int laneFirst = _laneFirstItem[lane];
            for (int chunk = firstChunk; chunk <= lastChunk; chunk++)
            {
                int from = laneFirst + chunk * _unitSize;
                int to = Math.Min(laneFirst + count, from + _unitSize);
                for (int index = from; index < to; index++)
                {
                    ref readonly TimelineRenderItem item = ref _items[index];
                    if (item.StartTick >= endTick)
                    {
                        break;
                    }

                    if (item.EndTick > effectiveStart)
                    {
                        sink.Add(in item);
                    }
                }
            }
        }
    }

    /// <summary>
    /// First chunk whose maximum end reaches into the range. The block prefix max is non-decreasing,
    /// so everything before the found block ends before the range start and is skipped in O(1).
    /// </summary>
    private int FirstChunkReachingInto(int lane, long effectiveStart)
    {
        long[] maxEnds = _laneChunkMaxEnd[lane];
        long[] blocks = _laneBlockPrefixMax[lane];
        int block = LowerBound(blocks, effectiveStart);
        int chunk = block * BlockChunks;
        if (chunk >= maxEnds.Length)
        {
            return maxEnds.Length;
        }

        int blockEnd = Math.Min(maxEnds.Length, chunk + BlockChunks);
        for (; chunk < blockEnd; chunk++)
        {
            if (maxEnds[chunk] >= effectiveStart)
            {
                return chunk;
            }
        }

        return maxEnds.Length;
    }

    /// <summary>Last chunk whose first note starts before the range end (chunkStart is monotonic).</summary>
    private static int LastChunkStartingBefore(long[] starts, long endTick)
    {
        int low = 0;
        int high = starts.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (starts[middle] <= endTick)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low - 1;
    }

    private static int LowerBound(long[] values, long target)
    {
        int low = 0;
        int high = values.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (values[middle] >= target)
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

    private static bool InOrder(in TimelineRenderItem left, in TimelineRenderItem right)
    {
        int value = left.Lane.CompareTo(right.Lane);
        if (value != 0) return value < 0;
        value = left.StartTick.CompareTo(right.StartTick);
        if (value != 0) return value < 0;
        value = left.EndTick.CompareTo(right.EndTick);
        if (value != 0) return value < 0;
        value = left.Kind.CompareTo(right.Kind);
        return value != 0 ? value < 0 : left.Id.CompareTo(right.Id) <= 0;
    }
}
