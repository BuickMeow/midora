using Midora.Avalonia.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Avalonia.Presentation.Tests;

/// <summary>
/// The block aggregator turns notes into one segment per (lane, tick block). Every note must be
/// covered by its own block's segment, and a segment may only extend past its block by the length of
/// a note that actually starts inside it, which is what keeps a zoomed out view recognizable.
/// </summary>
public sealed class TimelinePianoRollBlockAggregatorTests
{
    private const int BlockTicks = 16;

    [Fact]
    public void CoversEveryNoteInItsOwnBlock()
    {
        TimelineRenderItem[] items = BuildRandomItems(seed: 4242, count: 5_000, lanes: 12);
        TimelinePianoRollBlockAggregator aggregator = Aggregate(items, firstLane: 0, lanes: 12, firstBlock: 0, blocks: 200);
        Assert.True(aggregator.TouchedCells.Count > 0);
        foreach (TimelineRenderItem item in items)
        {
            int cell = CellOf(aggregator, item);
            Assert.True(cell >= 0, $"note {item.Id.Value} has no segment");
            Assert.True(
                aggregator.MinimumStart(cell) <= item.StartTick
                && aggregator.MaximumEnd(cell) >= item.EndTick,
                $"note {item.Id.Value} [{item.StartTick},{item.EndTick}) is not covered");
            Assert.Equal(item.Lane, aggregator.LaneOf(cell));
        }
    }

    [Fact]
    public void SegmentOnlyExtendsPastItsBlockByANoteStartingInsideIt()
    {
        const int lanes = 8;
        const int blocks = 200;
        TimelineRenderItem[] items = BuildRandomItems(seed: 777, count: 5_000, lanes: lanes);
        TimelinePianoRollBlockAggregator aggregator = Aggregate(items, firstLane: 0, lanes: lanes, firstBlock: 0, blocks: blocks);

        // Longest note that starts in each block, which is the only way a segment may extend past it.
        Dictionary<int, long> longestPerCell = [];
        foreach (TimelineRenderItem item in items)
        {
            long block = item.StartTick / BlockTicks;
            if (item.Lane < 0 || item.Lane >= lanes || block < 0 || block >= blocks)
            {
                continue;
            }

            int cell = (int)(item.Lane * (long)blocks + block);
            long length = item.EndTick - item.StartTick;
            longestPerCell[cell] = longestPerCell.TryGetValue(cell, out long current)
                ? Math.Max(current, length)
                : length;
        }

        foreach (int cell in aggregator.TouchedCells)
        {
            long blockStart = BlockStartOf(aggregator, cell);
            long blockEnd = blockStart + BlockTicks;
            long span = aggregator.MaximumEnd(cell) - aggregator.MinimumStart(cell);
            Assert.True(
                aggregator.MinimumStart(cell) >= blockStart
                && aggregator.MinimumStart(cell) < blockEnd,
                $"segment starts outside its block: {aggregator.MinimumStart(cell)} vs [{blockStart},{blockEnd})");
            long longestNote = longestPerCell.TryGetValue(cell, out long value) ? value : 0;
            Assert.True(
                span <= BlockTicks + longestNote,
                $"segment span {span} exceeds block {BlockTicks} plus longest note {longestNote}");
        }
    }

    [Fact]
    public void WorksWhenDefaultInitialisedLikeAField()
    {
        // A struct stored as a field is default initialised, so Begin must create its buffers.
        TimelinePianoRollBlockAggregator aggregator = default;
        aggregator.Begin(0, 2, 0, 4, BlockTicks);
        aggregator.Add(Item(1, lane: 1, start: 0, end: 4));
        Assert.Single(aggregator.TouchedCells);
        Assert.Equal(1, aggregator.LaneOf(aggregator.TouchedCells[0]));

        aggregator.Begin(0, 2, 0, 4, BlockTicks);
        aggregator.Add(Item(2, lane: 0, start: BlockTicks, end: BlockTicks + 4));
        Assert.Single(aggregator.TouchedCells);
    }

    [Fact]
    public void IgnoresEventsAndItemsOutsideTheAggregatedRange()
    {
        TimelinePianoRollBlockAggregator aggregator = new();
        aggregator.Begin(firstLane: 4, laneCount: 4, firstBlock: 10, blockCount: 4, blockTicks: BlockTicks);
        aggregator.Add(Item(1, lane: 4, start: 10 * BlockTicks, end: 10 * BlockTicks + 4));
        aggregator.Add(Item(2, lane: 4, start: 10 * BlockTicks, end: 10 * BlockTicks + 4, kind: TimelineItemKind.DirectMidiEvent));
        aggregator.Add(Item(3, lane: 3, start: 10 * BlockTicks, end: 10 * BlockTicks + 4));
        aggregator.Add(Item(4, lane: 4, start: 9 * BlockTicks, end: 9 * BlockTicks + 4));
        aggregator.Add(Item(5, lane: 4, start: 14 * BlockTicks, end: 14 * BlockTicks + 4));
        Assert.Single(aggregator.TouchedCells);
        Assert.Equal(4, aggregator.LaneOf(aggregator.TouchedCells[0]));
    }

    [Fact]
    public void ReusesBuffersWithoutLeakingCellsBetweenPasses()
    {
        TimelinePianoRollBlockAggregator aggregator = new();
        aggregator.Begin(0, 2, 0, 4, BlockTicks);
        aggregator.Add(Item(1, lane: 0, start: 0, end: 4));
        aggregator.Add(Item(2, lane: 1, start: 0, end: 4));
        Assert.Equal(2, aggregator.TouchedCells.Count);

        aggregator.Begin(0, 2, 0, 4, BlockTicks);
        aggregator.Add(Item(3, lane: 1, start: BlockTicks * 2, end: BlockTicks * 2 + 4));
        Assert.Single(aggregator.TouchedCells);
        int cell = aggregator.TouchedCells[0];
        Assert.Equal(1, aggregator.LaneOf(cell));
        Assert.Equal(BlockTicks * 2, aggregator.MinimumStart(cell));
    }

    private static TimelinePianoRollBlockAggregator Aggregate(
        TimelineRenderItem[] items,
        int firstLane,
        int lanes,
        long firstBlock,
        int blocks)
    {
        TimelinePianoRollBlockAggregator aggregator = new();
        aggregator.Begin(firstLane, lanes, firstBlock, blocks, BlockTicks);
        foreach (TimelineRenderItem item in items)
        {
            aggregator.Add(item);
        }

        return aggregator;
    }

    private static int CellOf(in TimelinePianoRollBlockAggregator aggregator, in TimelineRenderItem item)
    {
        foreach (int cell in aggregator.TouchedCells)
        {
            if (aggregator.LaneOf(cell) == item.Lane
                && aggregator.MinimumStart(cell) <= item.StartTick
                && aggregator.MaximumEnd(cell) >= item.EndTick)
            {
                return cell;
            }
        }

        return -1;
    }

    private static long BlockStartOf(in TimelinePianoRollBlockAggregator aggregator, int cell)
    {
        long block = cell % aggregator.BlockCount;
        return block * BlockTicks;
    }

    private static TimelineRenderItem[] BuildRandomItems(int seed, int count, int lanes)
    {
        Random random = new(seed);
        List<TimelineRenderItem> items = [];
        for (int index = 0; index < count; index++)
        {
            long start = random.Next(0, 3_000);
            long end = start + (random.Next(0, 40) == 0
                ? random.Next(1, 500)
                : random.Next(1, 20));
            items.Add(Item(index + 1, random.Next(0, lanes), start, end));
        }

        return [.. items];
    }

    private static TimelineRenderItem Item(
        long id,
        int lane,
        long start,
        long end,
        TimelineItemKind kind = TimelineItemKind.DirectMidiNote) =>
        new(
            MidoraId.FromSequence(id),
            kind,
            start,
            end,
            lane,
            100,
            1,
            TimelineItemState.None);
}
