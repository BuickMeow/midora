using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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

    [Fact]
    public void SnapshotCarriesNormalizedSegmentPreviewIncludingLeftBoundary()
    {
        MidoraId segmentId = new(8);
        TimelineSegmentPreview preview = new(segmentId,
        [
            new TimelineSegmentPreviewNote(0, 0.25, 60),
            new TimelineSegmentPreviewNote(0.75, 1, 127)
        ]);
        TimelineRenderSnapshot snapshot = new(
            12,
            "arrangement",
            [Item(8, 0, 480, 0, kind: TimelineItemKind.Segment)],
            ["Track"],
            segmentPreviews: new Dictionary<MidoraId, TimelineSegmentPreview> { [segmentId] = preview });

        Assert.Same(preview, snapshot.SegmentPreviews[segmentId]);
        Assert.Equal(0, preview.Notes[0].NormalizedStart);
        Assert.Equal(127, preview.Notes[1].Pitch);
    }

    [Theory]
    [InlineData(TimelineToolMode.Draw, false, null, false, TimelinePointerIntent.Default)]
    [InlineData(TimelineToolMode.Select, false, null, false, TimelinePointerIntent.Crosshair)]
    [InlineData(TimelineToolMode.Draw, true, TimelineItemKind.Segment, false, TimelinePointerIntent.Move)]
    [InlineData(TimelineToolMode.Draw, true, TimelineItemKind.Segment, true, TimelinePointerIntent.ResizeHorizontal)]
    [InlineData(TimelineToolMode.Split, true, TimelineItemKind.Segment, true, TimelinePointerIntent.Split)]
    public void DirectTimelinePointerIntentFollowsSelectedTool(
        TimelineToolMode toolMode,
        bool hasItem,
        TimelineItemKind? itemKind,
        bool nearEdge,
        TimelinePointerIntent expected)
    {
        Assert.Equal(
            expected,
            TimelineToolPolicy.GetPointerIntent(
                toolMode,
                TimelineSurfaceMode.Arrangement,
                isInContent: true,
                hasItem ? itemKind : null,
                nearEdge));
    }

    [Fact]
    public void DirectTimelineEditsAndCreationBelongToDrawToolOnly()
    {
        Assert.True(TimelineToolPolicy.CanBeginItemEdit(
            TimelineToolMode.Draw,
            TimelineSurfaceMode.PianoRoll,
            TimelineItemKind.LogicalNote));
        Assert.False(TimelineToolPolicy.CanBeginItemEdit(
            TimelineToolMode.Select,
            TimelineSurfaceMode.PianoRoll,
            TimelineItemKind.LogicalNote));
        Assert.True(TimelineToolPolicy.RequestsBackgroundCreation(
            TimelineToolMode.Draw,
            TimelineSurfaceMode.PianoRoll,
            clickCount: 1));
        Assert.False(TimelineToolPolicy.RequestsBackgroundCreation(
            TimelineToolMode.Select,
            TimelineSurfaceMode.PianoRoll,
            clickCount: 2));
        Assert.True(TimelineToolPolicy.SupportsCopyDrag(
            TimelineToolMode.Draw,
            TimelineSurfaceMode.PianoRoll,
            TimelineItemKind.LogicalNote,
            TimelineItemEditKind.Move));
        Assert.True(TimelineToolPolicy.SupportsCopyDrag(
            TimelineToolMode.Draw,
            TimelineSurfaceMode.Arrangement,
            TimelineItemKind.Segment,
            TimelineItemEditKind.Move));
        Assert.False(TimelineToolPolicy.SupportsCopyDrag(
            TimelineToolMode.Draw,
            TimelineSurfaceMode.PianoRoll,
            TimelineItemKind.LogicalNote,
            TimelineItemEditKind.ResizeEnd));
        Assert.False(TimelineToolPolicy.SupportsCopyDrag(
            TimelineToolMode.Select,
            TimelineSurfaceMode.PianoRoll,
            TimelineItemKind.TemplateNote,
            TimelineItemEditKind.Move));
    }

    [Theory]
    [InlineData(0, false, "C-1")]
    [InlineData(60, false, "C4")]
    [InlineData(61, true, null)]
    [InlineData(72, false, "C5")]
    [InlineData(127, false, null)]
    public void PianoKeyPresentationUsesBlackKeysAndLabelsOctaveCOnly(
        int midiNote,
        bool expectedBlack,
        string? expectedLabel)
    {
        Assert.Equal(expectedBlack, PianoKeyPresentation.IsBlackKey(midiNote));
        Assert.Equal(expectedLabel, PianoKeyPresentation.GetOctaveCLabel(midiNote));
    }

    [Fact]
    public void SegmentPreviewRejectsOutOfRangeProjection()
    {
        Assert.Throws<ArgumentException>(() => new TimelineSegmentPreview(
            new MidoraId(1),
            [new TimelineSegmentPreviewNote(-0.1, 0.5, 60)]));
        Assert.Throws<ArgumentException>(() => new TimelineSegmentPreview(
            new MidoraId(1),
            [new TimelineSegmentPreviewNote(0, 0.5, 128)]));
    }

    [Fact]
    public void RenderContentFingerprintIgnoresSelectionOnlyState()
    {
        TimelineRenderItem item = Item(1, 0, 10, 4);
        TimelineRenderSnapshot unselected = new(1, "segment:1", [item]);
        TimelineRenderSnapshot selected = new(
            1,
            "segment:1",
            [item with { State = TimelineItemState.Selected | TimelineItemState.Primary }]);

        Assert.Equal(unselected.ContentFingerprint, selected.ContentFingerprint);
        Assert.Equal(unselected.ItemsById[item.Id], unselected.Items[0]);
    }

    [Fact]
    public void ArrangementRasterCollapsesExtremeSegmentIntoOneBoundedBitmap()
    {
        TimelineSegmentPreviewNote[] notes = Enumerable.Range(1, 112)
            .SelectMany(pitch => Enumerable.Range(0, 192)
                .Select(tick => new TimelineSegmentPreviewNote(
                    tick / 192d,
                    (tick + 1) / 192d,
                    pitch)))
            .ToArray();
        TimelineSegmentPreview preview = new(new MidoraId(132), notes);

        TimelineRasterBuffer raster = TimelineSegmentPreviewRasterizer.Rasterize(
            preview,
            Color.FromRgb(189, 199, 207));

        Assert.Equal(21_504, raster.CandidateCount);
        Assert.Equal(TimelineSegmentPreviewRasterizer.Width * TimelineSegmentPreviewRasterizer.Height * 4, raster.Pixels.Length);
        Assert.Contains(raster.Pixels.Where((_, index) => index % 4 == 3), alpha => alpha > 0);
    }

    [Fact]
    public void PianoTileRasterizesOnlyItsTimeAndPitchBlock()
    {
        TimelineRenderItem[] notes = Enumerable.Range(1, 112)
            .SelectMany(pitch => Enumerable.Range(0, 192)
                .Select(tick => Item(
                    (pitch - 1) * 192L + tick + 1,
                    tick,
                    tick + 1,
                    127 - pitch)))
            .ToArray();
        TimelineRenderSnapshot snapshot = new(1, "segment:132", notes);
        int horizontalLod = TimelineRasterLod.Quantize(4);
        int verticalLod = TimelineRasterLod.Quantize(16);

        TimelineRasterBuffer raster = TimelinePianoTileRasterizer.Rasterize(
            snapshot,
            horizontalLod,
            verticalLod,
            tileX: 0,
            tileY: 1,
            Color.FromRgb(163, 178, 190),
            Color.FromRgb(232, 179, 75));

        Assert.InRange(raster.CandidateCount, 1, 1_300);
        Assert.True(raster.CandidateCount < notes.Length / 10);
        Assert.Contains(raster.Pixels.Where((_, index) => index % 4 == 3), alpha => alpha > 0);
    }

    [Fact]
    public void PianoTileFingerprintInvalidatesOnlyIntersectingTileAndIgnoresSelection()
    {
        TimelineRenderItem first = Item(1, 10, 20, 2);
        TimelineRenderItem second = Item(2, 300, 310, 2);
        TimelineRenderSnapshot original = new(1, "segment:1", [first, second]);
        TimelineRenderSnapshot edited = new(2, "segment:1", [first with { StartTick = 11, EndTick = 21 }, second]);
        TimelineRenderSnapshot selected = new(
            2,
            "segment:1",
            [first with { StartTick = 11, EndTick = 21 }, second with { State = TimelineItemState.Selected | TimelineItemState.Primary }]);
        int horizontalLod = TimelineRasterLod.Quantize(1);
        int verticalLod = TimelineRasterLod.Quantize(16);

        ulong originalFirstTile = TimelinePianoTileRasterizer.ComputeContentFingerprint(
            original, horizontalLod, verticalLod, tileX: 0, tileY: 0);
        ulong editedFirstTile = TimelinePianoTileRasterizer.ComputeContentFingerprint(
            edited, horizontalLod, verticalLod, tileX: 0, tileY: 0);
        ulong originalSecondTile = TimelinePianoTileRasterizer.ComputeContentFingerprint(
            original, horizontalLod, verticalLod, tileX: 1, tileY: 0);
        ulong editedSecondTile = TimelinePianoTileRasterizer.ComputeContentFingerprint(
            edited, horizontalLod, verticalLod, tileX: 1, tileY: 0);
        ulong selectedSecondTile = TimelinePianoTileRasterizer.ComputeContentFingerprint(
            selected, horizontalLod, verticalLod, tileX: 1, tileY: 0);

        Assert.NotEqual(originalFirstTile, editedFirstTile);
        Assert.Equal(originalSecondTile, editedSecondTile);
        Assert.Equal(editedSecondTile, selectedSecondTile);
    }

    [Fact]
    public void SegmentPreviewDestinationRemainsAnchoredToFullSegmentWhilePanning()
    {
        TimelineRenderItem segment = Item(
            1,
            0,
            1_000,
            0,
            kind: TimelineItemKind.Segment);
        TimelineViewport initial = new(0, 1_000, 0, 1, 1_000, 50, 50);
        TimelineViewport panned = new(500, 1_500, 0, 1, 1_000, 50, 50);

        Rect initialBounds = TimelineRasterPlacement.GetUnclippedItemBounds(
            initial, segment, laneHeaderWidth: 52, rulerHeight: 20, laneHeight: 50);
        Rect pannedBounds = TimelineRasterPlacement.GetUnclippedItemBounds(
            panned, segment, laneHeaderWidth: 52, rulerHeight: 20, laneHeight: 50);

        Assert.Equal(1_000, initialBounds.Width);
        Assert.Equal(initialBounds.Width, pannedBounds.Width);
        Assert.Equal(initialBounds.Left - 500, pannedBounds.Left);
        Assert.Equal(initialBounds.Top, pannedBounds.Top);
    }

    [Fact]
    public void DeferredPianoTileRequestsKeepTheirOwnCoordinates()
    {
        TimelineRenderSnapshot snapshot = new(
            1,
            "segment:1",
            [Item(1, 10, 20, 2), Item(2, 300, 310, 2)]);
        int horizontalLod = TimelineRasterLod.Quantize(1);
        int verticalLod = TimelineRasterLod.Quantize(16);
        TimelinePianoTileRasterRequest[] requests = new TimelinePianoTileRasterRequest[2];
        for (int tileX = 0; tileX < requests.Length; tileX++)
        {
            requests[tileX] = new(
                snapshot,
                horizontalLod,
                verticalLod,
                tileX,
                tileY: 0,
                Color.FromRgb(163, 178, 190),
                Color.FromRgb(232, 179, 75));
        }

        TimelineRasterBuffer first = requests[0].Rasterize();
        TimelineRasterBuffer second = requests[1].Rasterize();

        Assert.Equal(0, requests[0].TileX);
        Assert.Equal(1, requests[1].TileX);
        Assert.Equal(1, first.CandidateCount);
        Assert.Equal(1, second.CandidateCount);
        Assert.False(first.Pixels.SequenceEqual(second.Pixels));
    }

    [Fact]
    public void PianoNoteCrossingTileBoundaryIsRasterizedInBothTiles()
    {
        TimelineRenderSnapshot snapshot = new(1, "segment:1", [Item(1, 250, 270, 2)]);
        int horizontalLod = TimelineRasterLod.Quantize(1);
        int verticalLod = TimelineRasterLod.Quantize(16);

        TimelineRasterBuffer left = TimelinePianoTileRasterizer.Rasterize(
            snapshot,
            horizontalLod,
            verticalLod,
            tileX: 0,
            tileY: 0,
            Color.FromRgb(163, 178, 190),
            Color.FromRgb(232, 179, 75));
        TimelineRasterBuffer right = TimelinePianoTileRasterizer.Rasterize(
            snapshot,
            horizontalLod,
            verticalLod,
            tileX: 1,
            tileY: 0,
            Color.FromRgb(163, 178, 190),
            Color.FromRgb(232, 179, 75));

        Assert.Equal(1, left.CandidateCount);
        Assert.Equal(1, right.CandidateCount);
        Assert.Contains(left.Pixels.Where((_, index) => index % 4 == 3), alpha => alpha > 0);
        Assert.Contains(right.Pixels.Where((_, index) => index % 4 == 3), alpha => alpha > 0);
    }

    [Fact]
    public void PianoTileGuttersRenderTheSameContentAcrossCoreBoundary()
    {
        TimelineRenderSnapshot snapshot = new(1, "segment:1", [Item(1, 0, 600, 2)]);
        int horizontalLod = TimelineRasterLod.Quantize(1);
        int verticalLod = TimelineRasterLod.Quantize(16);
        Color normal = Color.FromRgb(163, 178, 190);
        Color warning = Color.FromRgb(232, 179, 75);

        TimelineRasterBuffer left = TimelinePianoTileRasterizer.Rasterize(
            snapshot, horizontalLod, verticalLod, 0, 0, normal, warning);
        TimelineRasterBuffer right = TimelinePianoTileRasterizer.Rasterize(
            snapshot, horizontalLod, verticalLod, 1, 0, normal, warning);

        Assert.Equal(TimelinePianoTileRasterizer.RasterSize, left.Width);
        Assert.Equal(Alpha(left, 257, 36), Alpha(right, 1, 36));
        Assert.True(Alpha(left, 257, 36) > 0);
    }

    [Fact]
    public void PianoTileDrawsAVisibleBoundaryBetweenAdjacentNotes()
    {
        TimelineRenderSnapshot snapshot = new(
            1,
            "segment:1",
            [Item(1, 10, 20, 2), Item(2, 20, 30, 2)]);
        int horizontalLod = TimelineRasterLod.Quantize(1);
        int verticalLod = TimelineRasterLod.Quantize(16);
        TimelineRasterBuffer raster = TimelinePianoTileRasterizer.Rasterize(
            snapshot,
            horizontalLod,
            verticalLod,
            0,
            0,
            Color.FromRgb(163, 178, 190),
            Color.FromRgb(232, 179, 75));

        Assert.True(Alpha(raster, 21, 40) > Alpha(raster, 16, 40));
    }

    [Fact]
    public void PianoTileUsesTheSameRoundedBoundaryForAdjacentNotesAtExactScale()
    {
        TimelineRenderSnapshot snapshot = new(
            1,
            "segment:1",
            [Item(1, 10, 20, 2), Item(2, 20, 30, 2)]);
        TimelineRasterBuffer raster = TimelinePianoTileRasterizer.Rasterize(
            snapshot,
            devicePixelsPerTick: 1.35,
            devicePixelsPerLane: 17.25,
            tileX: 0,
            tileY: 0,
            Color.FromRgb(163, 178, 190),
            Color.FromRgb(232, 179, 75));

        Assert.True(Alpha(raster, 27, 42) > Alpha(raster, 20, 42));
        Assert.True(Alpha(raster, 28, 42) > Alpha(raster, 20, 42));
    }

    [Fact]
    public void PianoTileKeepsVerticalPixelPhaseAcrossHorizontalTilesAtExactScale()
    {
        TimelineRenderSnapshot snapshot = new(1, "segment:1", [Item(1, 0, 600, 2)]);
        TimelineRasterBuffer left = TimelinePianoTileRasterizer.Rasterize(
            snapshot,
            devicePixelsPerTick: 1.37,
            devicePixelsPerLane: 17.25,
            tileX: 0,
            tileY: 0,
            Color.FromRgb(163, 178, 190),
            Color.FromRgb(232, 179, 75));
        TimelineRasterBuffer right = TimelinePianoTileRasterizer.Rasterize(
            snapshot,
            devicePixelsPerTick: 1.37,
            devicePixelsPerLane: 17.25,
            tileX: 1,
            tileY: 0,
            Color.FromRgb(163, 178, 190),
            Color.FromRgb(232, 179, 75));

        for (int y = 0; y < TimelinePianoTileRasterizer.RasterSize; y++)
        {
            Assert.Equal(Alpha(left, 257, y), Alpha(right, 1, y));
        }
    }

    [Fact]
    public void SegmentPreviewKeepsTickZeroInItsFirstPixel()
    {
        TimelineSegmentPreview preview = new(
            new MidoraId(1),
            [new TimelineSegmentPreviewNote(0, 0.01, 60)]);

        TimelineRasterBuffer raster = TimelineSegmentPreviewRasterizer.Rasterize(
            preview,
            Color.FromRgb(189, 199, 207));

        Assert.True(Alpha(raster, 0, 33) > 0);
    }

    [Fact]
    public void SegmentPreviewOnePixelPitchSurvivesNormalLaneDownsampling()
    {
        TimelineSegmentPreview preview = new(
            new MidoraId(1),
            [new TimelineSegmentPreviewNote(0, 192 / 3456d, 77)]);
        TimelineRasterBuffer raster = TimelineSegmentPreviewRasterizer.Rasterize(
            preview,
            Color.FromRgb(189, 199, 207));
        DrawingVisual visual = new();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        BitmapSource bitmap = BitmapSource.Create(
            raster.Width,
            raster.Height,
            96,
            96,
            PixelFormats.Pbgra32,
            null,
            raster.Pixels,
            raster.Stride);
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawImage(bitmap, new Rect(0, 0, 1_000, 44));
        }
        RenderTargetBitmap target = new(1_000, 44, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        byte[] pixels = new byte[1_000 * 44 * 4];
        target.CopyPixels(pixels, 1_000 * 4, 0);

        Assert.Contains(
            Enumerable.Range(0, 56).SelectMany(x => Enumerable.Range(0, 44)
                .Select(y => pixels[(y * 1_000 + x) * 4 + 3])),
            alpha => alpha > 0);
    }

    [Fact]
    public void VelocityTileBatchesSelectionIntoTheRaster()
    {
        TimelineRenderItem velocity = Item(1, 10, 20, 0, kind: TimelineItemKind.Velocity)
            with { Value = 0.5 };
        TimelineRenderSnapshot snapshot = new(1, "velocity:1", [velocity]);
        TimelineSelectionSnapshot unselected = new(0, [], null);
        TimelineSelectionSnapshot selected = new(1, [velocity.Id], velocity.Id);
        int lod = TimelineRasterLod.Quantize(1);

        TimelineRasterBuffer normal = TimelineVelocityTileRasterizer.Rasterize(
            snapshot, unselected, lod, 0,
            Color.FromRgb(163, 178, 190), Color.FromRgb(229, 61, 68), Color.FromRgb(49, 58, 69));
        TimelineRasterBuffer highlighted = TimelineVelocityTileRasterizer.Rasterize(
            snapshot, selected, lod, 0,
            Color.FromRgb(163, 178, 190), Color.FromRgb(229, 61, 68), Color.FromRgb(49, 58, 69));

        Assert.False(normal.Pixels.SequenceEqual(highlighted.Pixels));
        Assert.NotEqual(
            TimelineVelocityTileRasterizer.ComputeContentFingerprint(snapshot, unselected, lod, 0),
            TimelineVelocityTileRasterizer.ComputeContentFingerprint(snapshot, selected, lod, 0));
    }

    [Fact]
    public void PianoTileDestinationRemainsWorldAnchoredWhilePanning()
    {
        TimelineViewport initial = new(0, 1_000, 0, 8, 1_000, 160, 20);
        TimelineViewport panned = new(250, 1_250, 0, 8, 1_000, 160, 20);
        int horizontalLod = TimelineRasterLod.Quantize(initial.PixelsPerTick);
        int verticalLod = TimelineRasterLod.Quantize(initial.LaneHeight);

        Rect initialDestination = TimelineRasterPlacement.GetPianoTileDestination(
            initial, horizontalLod, verticalLod, 2, 1, 52, 20, 20);
        Rect pannedDestination = TimelineRasterPlacement.GetPianoTileDestination(
            panned, horizontalLod, verticalLod, 2, 1, 52, 20, 20);

        Assert.Equal(initialDestination.Width, pannedDestination.Width);
        Assert.Equal(initialDestination.Height, pannedDestination.Height);
        Assert.Equal(initialDestination.Left - 250, pannedDestination.Left);
        Assert.Equal(initialDestination.Top, pannedDestination.Top);
    }

    [Fact]
    public void PianoNoteUsesCorrespondingWorldTileAtEveryZoomLevel()
    {
        TimelineRenderSnapshot snapshot = new(1, "segment:1", [Item(1, 300, 310, 2)]);
        int verticalLod = TimelineRasterLod.Quantize(16);
        Color normal = Color.FromRgb(163, 178, 190);
        Color warning = Color.FromRgb(232, 179, 75);

        int normalLod = TimelineRasterLod.Quantize(1);
        TimelineRasterBuffer normalZoom = TimelinePianoTileRasterizer.Rasterize(
            snapshot, normalLod, verticalLod, tileX: 1, tileY: 0, normal, warning);
        int closeLod = TimelineRasterLod.Quantize(4);
        TimelineRasterBuffer closeZoom = TimelinePianoTileRasterizer.Rasterize(
            snapshot, closeLod, verticalLod, tileX: 4, tileY: 0, normal, warning);

        Assert.Equal(1, normalZoom.CandidateCount);
        Assert.Equal(1, closeZoom.CandidateCount);
        Assert.Contains(normalZoom.Pixels.Where((_, index) => index % 4 == 3), alpha => alpha > 0);
        Assert.Contains(closeZoom.Pixels.Where((_, index) => index % 4 == 3), alpha => alpha > 0);
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

    [Theory]
    [InlineData(100, 110, 48, 96, 144)]
    [InlineData(100, 300, 48, 96, 288)]
    public void MarqueePositiveRangeUsesOperationGridAndNeverFallsBackToOneTick(
        long rawStart,
        long rawEnd,
        long step,
        long expectedStart,
        long expectedEnd)
    {
        TimelineGridQuantization.SnappedRange range = TimelineGridQuantization.SnapPositiveRange(
            rawStart,
            rawEnd,
            step,
            useBars: false,
            timeSignatureMap: null);

        Assert.Equal(expectedStart, range.StartTick);
        Assert.Equal(expectedEnd, range.EndTick);
    }

    [Fact]
    public void BarGridFollowsEffectiveTimeSignatureAndTruncatedBoundary()
    {
        MidoraProject project = new(480);
        project.Conductor.TimeSignatures.Add(new TimeSignatureChange(project, 1_000, 3, 4));
        ProjectTimeSignatureMap map = new(project);

        Assert.Equal(1_000, TimelineGridQuantization.GetGridTickAtOrAfter(900, 1_920, true, map));
        Assert.Equal(2_440, TimelineGridQuantization.GetNextGridTick(1_000, 1_920, true, map));
        Assert.Equal(1_000, TimelineGridQuantization.SnapAbsolute(970, 1_920, true, map, 0));
    }

    [Fact]
    public void BarDeltaUsesTheBarLengthAtTheTargetTick()
    {
        MidoraProject project = new(480);
        project.Conductor.TimeSignatures.Add(new TimeSignatureChange(project, 1_000, 3, 4));
        ProjectTimeSignatureMap map = new(project);

        Assert.Equal(1_000, TimelineGridQuantization.SnapDelta(800, 900, 1_920, true, map));
        Assert.Equal(1_440, TimelineGridQuantization.SnapDelta(800, 1_100, 1_920, true, map));
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
        Assert.True(selection.Revision >= 4);
    }

    [Fact]
    public void WorkspaceSelectionAppliesLargeRangeAsOneRevision()
    {
        WorkspaceSelection selection = new();
        MidoraId[] ids = Enumerable.Range(1, 10_000).Select(value => new MidoraId(value)).ToArray();

        selection.ApplyRange(ids, WorkspaceSelectionRangeMode.Replace);

        Assert.Equal(1, selection.Revision);
        Assert.Equal(ids.Length, selection.Ids.Count);
        Assert.Equal(ids[0], selection.Primary);
    }

    private static byte Alpha(TimelineRasterBuffer buffer, int x, int y) =>
        buffer.Pixels[(y * buffer.Width + x) * 4 + 3];

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
