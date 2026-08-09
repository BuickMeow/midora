using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Desktop.Presentation.Tests;

public sealed class TimelineRenderingTests
{
    [Fact]
    public void ViewportMapsTicksAndLanesDeterministically()
    {
        TimelineViewport viewport = new(100, 500, 10, 8, 800, 160, 20);

        Assert.Equal(0, viewport.TickToX(100));
        Assert.Equal(400, viewport.TickToX(300));
        Assert.Equal(500, viewport.XToTick(800));
        Assert.Equal(10, viewport.YToLane(0));
        Assert.Equal(17, viewport.YToLane(159.99));
    }

    [Fact]
    public void IntervalIndexFindsLongItemBeginningBeforeViewport()
    {
        TimelineRenderItem[] items =
        [
            Item(1, 0, 10_000, 4),
            Item(2, 2_000, 2_100, 4),
            Item(3, 4_000, 4_100, 5)
        ];
        TimelineIntervalIndex index = new(items);
        List<TimelineRenderItem> result = [];

        index.QueryInto(5_000, 6_000, 4, 5, result);

        Assert.Collection(result, item => Assert.Equal(1, item.Id.Value));
    }

    [Fact]
    public void IntervalIndexCullsLargeOffscreenPopulation()
    {
        TimelineRenderItem[] items = Enumerable.Range(0, 100_000)
            .Select(index => Item(index + 1, index * 16L, index * 16L + 8, index % 128))
            .ToArray();
        TimelineIntervalIndex intervalIndex = new(items);
        List<TimelineRenderItem> result = new(capacity: 64);

        intervalIndex.QueryInto(320_000, 320_160, 0, 128, result);

        Assert.Equal(10, result.Count);
        Assert.All(result, item => Assert.True(item.StartTick < 320_160 && item.EndTick > 320_000));
    }

    [Fact]
    public void IntervalIndexCullsHundredThousandNotesEventsAndTenThousandSegments()
    {
        IEnumerable<TimelineRenderItem> notes = Enumerable.Range(0, 100_000)
            .Select(index => Item(index + 1L, index * 24L, index * 24L + 12, index % 128));
        IEnumerable<TimelineRenderItem> events = Enumerable.Range(0, 100_000)
            .Select(index => Item(
                100_001L + index,
                index * 24L + 6,
                index * 24L + 7,
                index % 128,
                kind: TimelineItemKind.TemplateEvent));
        IEnumerable<TimelineRenderItem> segments = Enumerable.Range(0, 10_000)
            .Select(index => Item(
                200_001L + index,
                index * 240L,
                index * 240L + 120,
                index % 64,
                kind: TimelineItemKind.Segment));
        TimelineIntervalIndex index = new(notes.Concat(events).Concat(segments));
        List<TimelineRenderItem> result = new(capacity: 128);

        index.QueryInto(1_200_000, 1_200_240, 0, 128, result);

        Assert.InRange(result.Count, 1, 128);
        Assert.All(result, item => Assert.True(item.StartTick < 1_200_240 && item.EndTick > 1_200_000));
    }

    [Fact]
    public void HierarchicalMaximumEndDoesNotScanHalfMillionItemsBehindOneLongInterval()
    {
        TimelineRenderItem[] items = Enumerable.Range(0, 500_000)
            .Select(index => Item(index + 2L, index * 8L, index * 8L + 4, 0))
            .Prepend(Item(1, 0, 4_000_000, 0))
            .ToArray();
        TimelineIntervalIndex index = new(items);
        List<TimelineRenderItem> result = new(capacity: 32);

        index.QueryInto(3_900_000, 3_900_080, 0, 1, result);

        Assert.Equal(11, result.Count);
        Assert.Contains(result, item => item.Id == new MidoraId(1));
    }

    [Fact]
    public void StableViewportQueryReusesDestinationWithoutManagedAllocation()
    {
        TimelineIntervalIndex index = new(Enumerable.Range(0, 100_000)
            .Select(item => Item(item + 1L, item * 8L, item * 8L + 4, item % 32)));
        List<TimelineRenderItem> result = new(capacity: 128);
        for (int iteration = 0; iteration < 10_000; iteration++)
        {
            index.QueryInto(200_000, 200_080, 0, 32, result);
        }
        long minimumAllocation = long.MaxValue;
        for (int sample = 0; sample < 5; sample++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int iteration = 0; iteration < 1_000; iteration++)
            {
                index.QueryInto(200_000, 200_080, 0, 32, result);
            }
            minimumAllocation = Math.Min(
                minimumAllocation,
                GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Equal(0, minimumAllocation);
    }

    [Fact]
    public void HitTestUsesZThenSmallestSpanThenStableId()
    {
        TimelineIntervalIndex index = new(
        [
            Item(3, 0, 100, 2, z: 2),
            Item(2, 10, 20, 2, z: 2),
            Item(1, 10, 20, 2, z: 3)
        ]);
        List<TimelineRenderItem> result = [];

        index.HitTestInto(15, 0, 2, result);

        Assert.Equal([1L, 2L, 3L], result.Select(item => item.Id.Value));
    }

    [Fact]
    public void SnapshotRejectsStaleProjection()
    {
        TimelineRenderSnapshot snapshot = new(12, "segment:42", [Item(1, 0, 10, 0)]);

        Assert.True(snapshot.Matches(12, "segment:42"));
        Assert.False(snapshot.Matches(13, "segment:42"));
        Assert.False(snapshot.Matches(12, "segment:43"));
    }

    [Fact]
    public void SnapshotCarriesRuntimeLaneMuteAndSoloWithoutChangingItems()
    {
        TimelineRenderItem item = Item(1, 0, 10, 0);
        TimelineRenderSnapshot snapshot = new(
            12,
            "arrangement",
            [item],
            ["Track"],
            [TimelineLaneState.Muted | TimelineLaneState.Solo]);

        Assert.Equal(TimelineLaneState.Muted | TimelineLaneState.Solo, snapshot.LaneStates[0]);
        Assert.Equal(item, snapshot.Items[0]);
    }

    [Theory]
    [InlineData(12, 10, 0, 10)]
    [InlineData(15, 10, -1, 10)]
    [InlineData(15, 10, 0, 10)]
    [InlineData(15, 10, 1, 20)]
    [InlineData(18, 10, 0, 20)]
    public void SnapUsesMovementDirectionOnlyForExactTie(
        long tick,
        long grid,
        int direction,
        long expected)
    {
        Assert.Equal(expected, TimelineSnap.Snap(tick, grid, direction));
    }

    [Fact]
    public void WorkspaceSelectionMaintainsValidPrimary()
    {
        WorkspaceSelection selection = new();
        selection.Replace(new MidoraId(3));
        selection.Add(new MidoraId(2));
        selection.Toggle(new MidoraId(2));

        Assert.Equal(new MidoraId(3), selection.Primary);
        Assert.Equal([new MidoraId(3)], selection.Ids);

        selection.Remove(new MidoraId(3));

        Assert.Null(selection.Primary);
        Assert.Empty(selection.Ids);
    }

    [Fact]
    public void WorkspaceIdentitySeparatesTypeAndObjectWorkspaces()
    {
        WorkspaceKey arrangement = WorkspaceKey.ForType(WorkspaceKind.Arrangement);
        WorkspaceKey first = WorkspaceKey.ForObject(
            WorkspaceKind.SegmentEditor,
            new MidoraId(9));
        WorkspaceKey second = WorkspaceKey.ForObject(
            WorkspaceKind.SegmentEditor,
            new MidoraId(10));

        Assert.NotEqual(arrangement, first);
        Assert.NotEqual(first, second);
        Assert.Throws<ArgumentException>(() => WorkspaceKey.ForType(WorkspaceKind.SegmentEditor));
    }

    private static TimelineRenderItem Item(
        long id,
        long start,
        long end,
        int lane,
        int z = 0,
        TimelineItemKind kind = TimelineItemKind.LogicalNote) => new(
            new MidoraId(id),
            kind,
            start,
            end,
            lane,
            0,
            z,
            TimelineItemState.None);
}
