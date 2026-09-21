using Midora.Avalonia.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Avalonia.Presentation.Tests;

/// <summary>
/// The chunked lane index replaces a per-item end-tick prefix array, so its range results must stay
/// exactly equal to a naive filter over every item, including long notes that span many chunks.
/// </summary>
public sealed class TimelineLaneChunkIndexTests
{
    [Fact]
    public void QueryMatchesNaiveFilterAcrossRandomRanges()
    {
        TimelineRenderItem[] items = BuildRandomItems(seed: 1234, count: 4_000, lanes: 16);
        TimelineLaneChunkIndex chunkIndex = TimelineLaneChunkIndex.BuildSorted(items);
        Random random = new(5678);
        for (int iteration = 0; iteration < 400; iteration++)
        {
            long start = random.Next(0, 12_000);
            long end = start + random.Next(1, 4_000);
            int firstLane = random.Next(0, 18);
            int lastLane = firstLane + random.Next(0, 6);
            AssertSameItems(items, chunkIndex, start, end, firstLane, lastLane);
        }
    }

    [Fact]
    public void LongNotesSpanningManyChunksAreAlwaysFound()
    {
        List<TimelineRenderItem> items = [];
        long id = 1;
        items.Add(Item(id++, lane: 3, start: 0, end: 5_000_000));
        for (int index = 0; index < 3_000; index++)
        {
            items.Add(Item(id++, lane: 3, start: 1_000 + index * 2L, end: 1_001 + index * 2L));
        }

        TimelineRenderItem[] sorted = Sort(items);
        TimelineLaneChunkIndex chunkIndex = TimelineLaneChunkIndex.BuildSorted(sorted);
        Assert.True(chunkIndex.ChunkCount > 10);
        for (long start = 0; start < 7_000; start += 250)
        {
            AssertSameItems(sorted, chunkIndex, start, start + 100, 0, 8);
        }
    }

    [Fact]
    public void ChunkBoundariesReturnExactResults()
    {
        List<TimelineRenderItem> items = [];
        for (int index = 0; index < TimelineLaneChunkIndex.ChunkSize * 3 + 5; index++)
        {
            items.Add(Item(index + 1, lane: 1, start: index * 10L, end: index * 10L + 5));
        }

        TimelineRenderItem[] sorted = Sort(items);
        TimelineLaneChunkIndex chunkIndex = TimelineLaneChunkIndex.BuildSorted(sorted);
        long[] probes =
        [
            0,
            5,
            TimelineLaneChunkIndex.ChunkSize * 10L - 1,
            TimelineLaneChunkIndex.ChunkSize * 10L,
            TimelineLaneChunkIndex.ChunkSize * 10L + 1,
            (TimelineLaneChunkIndex.ChunkSize * 2) * 10L - 1,
            (TimelineLaneChunkIndex.ChunkSize * 2) * 10L,
        ];
        foreach (long probe in probes)
        {
            AssertSameItems(sorted, chunkIndex, probe, probe + 40, 0, 4);
        }
    }

    [Fact]
    public void EmptyAndOutOfRangeLanesReturnNothing()
    {
        TimelineRenderItem[] items = Sort(
        [
            Item(1, lane: 2, start: 0, end: 100),
            Item(2, lane: 5, start: 50, end: 150),
        ]);
        TimelineLaneChunkIndex chunkIndex = TimelineLaneChunkIndex.BuildSorted(items);
        Assert.Empty(Collect(chunkIndex, 0, 1_000, 3, 4));
        Assert.Empty(Collect(chunkIndex, 0, 1_000, 20, 24));
        Assert.Empty(Collect(chunkIndex, 500, 600, 0, 8));
        Assert.Empty(Collect(chunkIndex, 100, 100, 0, 8));
        Assert.Empty(Collect(chunkIndex, 200, 100, 0, 8));
        Assert.Equal(2, Collect(chunkIndex, 0, 1_000, 0, 8).Count);
    }

    [Fact]
    public void OpenEndedNotesAndHugeRangesStayCorrect()
    {
        TimelineRenderItem[] items = Sort(
        [
            Item(1, lane: 0, start: 10, end: long.MaxValue),
            Item(2, lane: 0, start: 20, end: 30),
            Item(3, lane: 1, start: 0, end: 5),
        ]);
        TimelineLaneChunkIndex chunkIndex = TimelineLaneChunkIndex.BuildSorted(items);
        Assert.Equal(2, Collect(chunkIndex, 25, 26, 0, 1).Count);
        Assert.Equal(3, Collect(chunkIndex, 0, long.MaxValue, 0, 2).Count);
        Assert.Single(Collect(chunkIndex, 0, 6, 1, 2));
    }

    [Fact]
    public void ChunkEnvelopesCoverEveryIntersectingNote()
    {
        TimelineRenderItem[] items = BuildRandomItems(seed: 4242, count: 6_000, lanes: 12);
        TimelineLaneChunkIndex chunkIndex = TimelineLaneChunkIndex.BuildSorted(items);
        Random random = new(31337);
        for (int iteration = 0; iteration < 200; iteration++)
        {
            long start = random.Next(0, 10_000);
            long end = start + random.Next(1, 2_000);
            int firstLane = random.Next(0, 13);
            int lastLane = firstLane + random.Next(0, 5);
            CoverageSink sink = new();
            chunkIndex.VisitChunks(start, end, firstLane, lastLane, ref sink);
            long effectiveStart = Math.Max(0, start);
            foreach (TimelineRenderItem item in items)
            {
                if (item.Lane < firstLane || item.Lane >= lastLane) continue;
                if (item.StartTick >= end || item.EndTick <= effectiveStart) continue;
                bool covered = false;
                foreach ((int lane, long chunkStart, long chunkEnd, int count) in sink.Chunks)
                {
                    if (lane == item.Lane && chunkStart <= item.StartTick && chunkEnd >= item.EndTick)
                    {
                        covered = true;
                        break;
                    }
                }

                Assert.True(
                    covered,
                    $"note {item.Id.Value} [{item.StartTick},{item.EndTick}) lane {item.Lane} "
                    + $"was not covered for range [{start},{end}) lanes [{firstLane},{lastLane})");
            }
        }
    }

    [Fact]
    public void ChunkEnvelopesReportItemCountsAndLaneBounds()
    {
        List<TimelineRenderItem> items = [];
        for (int index = 0; index < TimelineLaneChunkIndex.ChunkSize * 2 + 3; index++)
        {
            items.Add(Item(index + 1, lane: 2, start: index * 4L, end: index * 4L + 2));
        }

        TimelineRenderItem[] sorted = Sort(items);
        TimelineLaneChunkIndex chunkIndex = TimelineLaneChunkIndex.BuildSorted(sorted);
        CoverageSink sink = new();
        chunkIndex.VisitChunks(0, long.MaxValue / 2, 0, 8, ref sink);
        Assert.Equal(3, sink.Chunks.Count);
        Assert.Equal(TimelineLaneChunkIndex.ChunkSize, sink.Chunks[0].Count);
        Assert.Equal(TimelineLaneChunkIndex.ChunkSize, sink.Chunks[1].Count);
        Assert.Equal(3, sink.Chunks[2].Count);
        Assert.All(sink.Chunks, chunk => Assert.Equal(2, chunk.Lane));
    }

    private struct CoverageSink : TimelineLaneChunkIndex.IChunkSink
    {
        public readonly List<(int Lane, long StartTick, long EndTick, int Count)> Chunks { get; }

        public CoverageSink() => Chunks = [];

        public void Chunk(int lane, long startTick, long endTick, int itemCount) =>
            Chunks.Add((lane, startTick, endTick, itemCount));
    }

    [Fact]
    public void BuildRejectsUnsortedItems()
    {
        TimelineRenderItem[] items =
        [
            Item(1, lane: 1, start: 0, end: 10),
            Item(2, lane: 0, start: 0, end: 10),
        ];
        Assert.Throws<ArgumentException>(() => TimelineLaneChunkIndex.BuildSorted(items));
    }

    [Fact]
    public void IndexStaysFarSmallerThanAPerItemPrefixArray()
    {
        TimelineRenderItem[] items = BuildRandomItems(seed: 99, count: 20_000, lanes: 8);
        TimelineLaneChunkIndex chunkIndex = TimelineLaneChunkIndex.BuildSorted(items);
        Assert.True(
            chunkIndex.IndexBytes * 8 <= (long)items.Length * sizeof(long),
            $"index bytes {chunkIndex.IndexBytes} for {items.Length} items");
    }

    private static void AssertSameItems(
        TimelineRenderItem[] items,
        TimelineLaneChunkIndex chunkIndex,
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive)
    {
        List<TimelineRenderItem> expected = [];
        long effectiveStart = Math.Max(0, startTick);
        foreach (TimelineRenderItem item in items)
        {
            if (item.Lane < firstLane || item.Lane >= lastLaneExclusive) continue;
            if (item.StartTick < endTick && item.EndTick > effectiveStart) expected.Add(item);
        }

        List<TimelineRenderItem> actual = Collect(chunkIndex, startTick, endTick, firstLane, lastLaneExclusive);
        Assert.Equal(expected.Count, actual.Count);
        for (int position = 0; position < expected.Count; position++)
        {
            Assert.Equal(expected[position].Id, actual[position].Id);
        }
    }

    private static List<TimelineRenderItem> Collect(
        TimelineLaneChunkIndex chunkIndex,
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive)
    {
        List<TimelineRenderItem> destination = [];
        chunkIndex.QueryInto(startTick, endTick, firstLane, lastLaneExclusive, destination);
        return destination;
    }

    private static TimelineRenderItem[] BuildRandomItems(int seed, int count, int lanes)
    {
        Random random = new(seed);
        List<TimelineRenderItem> items = [];
        for (int index = 0; index < count; index++)
        {
            long start = random.Next(0, 10_000);
            long end = start + (random.Next(0, 50) == 0
                ? random.Next(1, 9_000)
                : random.Next(1, 40));
            items.Add(Item(index + 1, random.Next(0, lanes), start, end));
        }

        return Sort(items);
    }

    private static TimelineRenderItem[] Sort(List<TimelineRenderItem> items)
    {
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
        return materialized;
    }

    private static TimelineRenderItem Item(long id, int lane, long start, long end) =>
        new(
            MidoraId.FromSequence(id),
            TimelineItemKind.DirectMidiNote,
            start,
            end,
            lane,
            100,
            1,
            TimelineItemState.None);
}
