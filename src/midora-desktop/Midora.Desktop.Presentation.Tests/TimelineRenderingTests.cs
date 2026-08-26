using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Midora.Desktop.Presentation.Tests;

public sealed class TimelineRenderingTests
{
    [Fact]
    public void TimelineTextCacheSeparatesTransparentMeasurementFromVisibleText()
    {
        RunOnSta(() =>
        {
            TimelineSurface surface = new();
            MethodInfo? method = typeof(TimelineSurface).GetMethod(
                "GetFormattedText",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            object? transparent = method!.Invoke(
                surface,
                ["Instrument", Brushes.Transparent, 9d, FontWeights.Normal]);
            object? visible = method.Invoke(
                surface,
                ["Instrument", Brushes.White, 9d, FontWeights.Normal]);

            Assert.NotSame(transparent, visible);
        });
    }

    [Fact]
    public void PlaybackCursorInvalidatesOnlyItsLightweightOverlay()
    {
        FrameworkPropertyMetadata surfaceMetadata = Assert.IsType<FrameworkPropertyMetadata>(
            TimelineSurface.PlaybackCursorTickProperty.GetMetadata(typeof(TimelineSurface)));
        FrameworkPropertyMetadata overlayMetadata = Assert.IsType<FrameworkPropertyMetadata>(
            TimelinePlaybackCursorOverlay.PlaybackCursorTickProperty.GetMetadata(
                typeof(TimelinePlaybackCursorOverlay)));

        Assert.False(surfaceMetadata.AffectsRender);
        Assert.True(overlayMetadata.AffectsRender);
    }

    [Fact]
    public void ArrangementTrackSelectionInvalidatesTheSurface()
    {
        FrameworkPropertyMetadata trackMetadata = Assert.IsType<FrameworkPropertyMetadata>(
            TimelineSurface.SelectedArrangementTrackIdProperty.GetMetadata(typeof(TimelineSurface)));
        FrameworkPropertyMetadata conductorMetadata = Assert.IsType<FrameworkPropertyMetadata>(
            TimelineSurface.IsConductorTrackSelectedProperty.GetMetadata(typeof(TimelineSurface)));

        Assert.True(trackMetadata.AffectsRender);
        Assert.True(conductorMetadata.AffectsRender);
    }

    [Fact]
    public void OverviewPlaybackAndEditCursorsInvalidateTheOverview()
    {
        FrameworkPropertyMetadata playbackMetadata = Assert.IsType<FrameworkPropertyMetadata>(
            TimelineOverviewSurface.PlaybackCursorTickProperty.GetMetadata(
                typeof(TimelineOverviewSurface)));
        FrameworkPropertyMetadata editMetadata = Assert.IsType<FrameworkPropertyMetadata>(
            TimelineOverviewSurface.EditCursorTickProperty.GetMetadata(
                typeof(TimelineOverviewSurface)));

        Assert.True(playbackMetadata.AffectsRender);
        Assert.True(editMetadata.AffectsRender);
    }

    [Fact]
    public void MaterializedOverviewEventsContributeToTimelineExtent()
    {
        TimelineRenderSnapshot snapshot = new(
            1,
            "logical-parameter-extent",
            [],
            overviewSource: new MaterializedTimelineOverviewSource([], [2_400]));

        Assert.Equal(2_401, snapshot.MaximumEndTick);
    }

    [Fact]
    public void PianoRollVerticalZoomMinimumIsThreeDevicePixels()
    {
        Assert.Equal(3, TimelineSurface.MinimumPianoLaneHeight);
        Assert.Equal(128, TimelineSurface.MaximumPianoLaneHeight);
    }

    [Fact]
    public void TimelinePointerReadoutUsesArrangementAndPianoRollCoordinates()
    {
        RunOnSta(() =>
        {
            MethodInfo? updatePointer = typeof(TimelineSurface).GetMethod(
                "UpdatePointerPositionText",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(updatePointer);
            TimelineViewport viewport = new(
                StartTick: 100,
                EndTick: 1_100,
                FirstLane: 20,
                LaneCount: 30,
                Width: 600,
                Height: 300,
                LaneHeight: 10);

            TimelineSurface arrangement = ArrangeSurface(TimelineSurfaceMode.Arrangement);
            updatePointer!.Invoke(
                arrangement,
                [new Point(TimelineSurface.ArrangementLaneHeaderWidth + 120, 42), viewport]);
            Assert.Equal("(300)", arrangement.PointerPositionText);
            updatePointer.Invoke(
                arrangement,
                [new Point(TimelineSurface.ArrangementLaneHeaderWidth - 1, 42), viewport]);
            Assert.Empty(arrangement.PointerPositionText);

            TimelineSurface pianoRoll = ArrangeSurface(TimelineSurfaceMode.PianoRoll);
            updatePointer.Invoke(pianoRoll, [new Point(52 + 120, 24 + 35), viewport]);
            Assert.Equal("(300, 104)", pianoRoll.PointerPositionText);
            updatePointer.Invoke(pianoRoll, [new Point(51, 24 + 35), viewport]);
            Assert.Empty(pianoRoll.PointerPositionText);
        });

        static TimelineSurface ArrangeSurface(TimelineSurfaceMode mode)
        {
            TimelineSurface surface = new()
            {
                SurfaceMode = mode,
                OperationStepTicks = 1,
                TickSpan = 1_000,
                LaneHeight = 10
            };
            surface.Measure(new Size(1_000, 400));
            surface.Arrange(new Rect(0, 0, 1_000, 400));
            return surface;
        }
    }

    [Fact]
    public void PianoRollVerticalZoomButtonsAdjustOneDevicePixelAndKeepTheViewportBounded()
    {
        RunOnSta(() =>
        {
            TimelineSurface surface = new()
            {
                SurfaceMode = TimelineSurfaceMode.PianoRoll,
                LaneHeight = 15,
                FirstLane = 48,
                TickSpan = 1_920
            };
            surface.Measure(new Size(800, 400));
            surface.Arrange(new Rect(0, 0, 800, 400));

            surface.AdjustVerticalZoom(zoomIn: true);

            Assert.Equal(16, surface.LaneHeight);
            Assert.InRange(surface.FirstLane, 0, surface.MaximumFirstLane);

            surface.AdjustVerticalZoom(zoomIn: false);

            Assert.Equal(15, surface.LaneHeight);
            Assert.InRange(surface.FirstLane, 0, surface.MaximumFirstLane);
        });
    }

    [Fact]
    public void ArrangementVerticalZoomButtonsRemainWithinTheArrangementBounds()
    {
        RunOnSta(() =>
        {
            TimelineSurface surface = new()
            {
                SurfaceMode = TimelineSurfaceMode.Arrangement,
                LaneHeight = 56,
                TickSpan = 1_920
            };
            surface.Measure(new Size(800, 400));
            surface.Arrange(new Rect(0, 0, 800, 400));

            surface.AdjustVerticalZoom(zoomIn: true);

            Assert.InRange(surface.LaneHeight, 56.01, 112);

            for (int index = 0; index < 100; index++)
            {
                surface.AdjustVerticalZoom(zoomIn: false);
            }

            Assert.Equal(28, surface.LaneHeight);
        });
    }

    [Theory]
    [InlineData(TimelineSurfaceMode.Velocity)]
    [InlineData(TimelineSurfaceMode.EventLanes)]
    public void ContinuousValueSurfacesDoNotRenderHeightDependentLaneZebra(
        TimelineSurfaceMode mode)
    {
        RunOnSta(() =>
        {
            const int width = 800;
            const int height = 400;
            TimelineSurface surface = new()
            {
                SurfaceMode = mode,
                LaneHeight = 160,
                TickSpan = 1_920,
                GridVisible = false
            };
            surface.Measure(new Size(width, height));
            surface.Arrange(new Rect(0, 0, width, height));

            RenderTargetBitmap target = new(width, height, 96, 96, PixelFormats.Pbgra32);
            target.Render(surface);
            byte[] upper = new byte[4];
            byte[] lower = new byte[4];
            target.CopyPixels(new Int32Rect(700, 100, 1, 1), upper, 4, 0);
            target.CopyPixels(new Int32Rect(700, 250, 1, 1), lower, 4, 0);

            Assert.Equal(upper, lower);
        });
    }

    [Fact]
    public void ArrangementScrollMaximumKeepsTheLastVariableHeightRowVisible()
    {
        RunOnSta(() =>
        {
            MidoraId populatedParent = new(5);
            TimelineRenderSnapshot snapshot = new(
                1,
                "arrangement:variable-row-scroll",
                [],
                Enumerable.Range(0, 8).Select(index => $"Lane {index}").ToArray(),
                arrangementLanes:
                [
                    new(0, ArrangementLaneKind.Conductor, null, null, 0, true, false, false),
                    new(1, ArrangementLaneKind.EventInstrument, new MidoraId(2), null, 0, false, false, false),
                    new(2, ArrangementLaneKind.MidiChannelRoot, new MidoraId(3), null, 0, false, false, false),
                    new(3, ArrangementLaneKind.EventInstrument, new MidoraId(4), null, 0, false, false, false),
                    new(4, ArrangementLaneKind.EventInstrument, populatedParent, null, 0, true, true, false),
                    new(5, ArrangementLaneKind.LogicalTrack, new MidoraId(6), populatedParent, 1, false, false, true),
                    new(6, ArrangementLaneKind.LogicalTrack, new MidoraId(7), populatedParent, 1, false, false, true),
                    new(7, ArrangementLaneKind.LogicalTrack, new MidoraId(8), populatedParent, 1, false, false, true)
                ]);
            TimelineSurface surface = new()
            {
                Snapshot = snapshot,
                SurfaceMode = TimelineSurfaceMode.Arrangement,
                LaneHeight = 60,
                TickSpan = 1_920
            };

            Grid host = new();
            host.Children.Add(surface);
            host.Measure(new Size(800, 200));
            host.Arrange(new Rect(0, 0, 800, 200));

            Assert.Equal(200, surface.ActualHeight);
            surface.LaneHeight = 61;
            Assert.Equal(6, surface.MaximumFirstLane);
            surface.FirstLane = surface.MaximumFirstLane;
            Assert.Equal(6, surface.FirstLane);
        });
    }

    [Fact]
    public void ArrangementInstrumentDropTargetsOnlyGlobalTrackGaps()
    {
        RunOnSta(() =>
        {
            MidoraId sharedUsageId = new(100);
            TimelineRenderSnapshot snapshot = new(
                1,
                "arrangement:instrument-drop-gaps",
                [],
                ["Conductor", "Shared A", "Shared B", "Independent"],
                arrangementLanes:
                [
                    new(0, ArrangementLaneKind.Conductor, null, null, 0, true, false, false),
                    new(1, ArrangementLaneKind.LogicalTrack, new MidoraId(11), null, 0, true, false, true)
                    {
                        SharedGroupId = sharedUsageId,
                        IsSharedGroup = true,
                        IsSharedGroupStart = true,
                        SharedGroupMemberCount = 2
                    },
                    new(2, ArrangementLaneKind.LogicalTrack, new MidoraId(12), null, 0, true, false, true)
                    {
                        SharedGroupId = sharedUsageId,
                        IsSharedGroup = true,
                        IsSharedGroupEnd = true,
                        SharedGroupMemberCount = 2
                    },
                    new(3, ArrangementLaneKind.PureMidiTrack, new MidoraId(13), null, 0, true, false, true)
                ]);
            TimelineSurface surface = new()
            {
                Snapshot = snapshot,
                SurfaceMode = TimelineSurfaceMode.Arrangement,
                LaneHeight = 60,
                TickSpan = 1_920
            };
            Grid host = new();
            host.Children.Add(surface);
            host.Measure(new Size(800, 320));
            host.Arrange(new Rect(0, 0, 800, 320));

            Assert.True(surface.TryGetArrangementTrackInsertionIndex(new Point(400, 84), out int first));
            Assert.Equal(0, first);
            Assert.False(surface.TryGetArrangementTrackInsertionIndex(new Point(400, 144), out _));
            Assert.True(surface.TryGetArrangementTrackInsertionIndex(new Point(400, 204), out int afterGroup));
            Assert.Equal(2, afterGroup);
            Assert.False(surface.TryGetArrangementTrackInsertionIndex(new Point(400, 234), out _));
            Assert.True(surface.TryGetArrangementTrackInsertionIndex(new Point(400, 280), out int last));
            Assert.Equal(3, last);
        });
    }

    [Fact]
    public void ArrangementBraceAndEmptyBackgroundHaveIndependentHitTargets()
    {
        RunOnSta(() =>
        {
            MidoraId sharedUsageId = new(100);
            TimelineRenderSnapshot snapshot = new(
                1,
                "arrangement:brace-hit-target",
                [],
                ["Conductor", "Shared A", "Shared B"],
                arrangementLanes:
                [
                    new(0, ArrangementLaneKind.Conductor, null, null, 0, true, false, false),
                    new(1, ArrangementLaneKind.LogicalTrack, new MidoraId(11), null, 0, true, false, true)
                    {
                        SharedGroupId = sharedUsageId,
                        IsSharedGroup = true,
                        IsSharedGroupStart = true,
                        SharedGroupMemberCount = 2
                    },
                    new(2, ArrangementLaneKind.LogicalTrack, new MidoraId(12), null, 0, true, false, true)
                    {
                        SharedGroupId = sharedUsageId,
                        IsSharedGroup = true,
                        IsSharedGroupEnd = true,
                        SharedGroupMemberCount = 2
                    }
                ]);
            TimelineSurface surface = new()
            {
                Snapshot = snapshot,
                SurfaceMode = TimelineSurfaceMode.Arrangement,
                LaneHeight = 60,
                TickSpan = 1_920
            };
            Grid host = new();
            host.Children.Add(surface);
            host.Measure(new Size(800, 260));
            host.Arrange(new Rect(0, 0, 800, 260));

            Assert.True(surface.TryGetArrangementSharedGroupBraceTarget(
                new Point(5, 100),
                out MidoraId groupId));
            Assert.Equal(sharedUsageId, groupId);
            Assert.False(surface.TryGetArrangementSharedGroupBraceTarget(new Point(20, 100), out _));
            Assert.False(surface.IsArrangementEmptyBackground(new Point(5, 100)));
            Assert.True(surface.IsArrangementEmptyBackground(new Point(400, 100)));
            Assert.True(surface.IsArrangementEmptyBackground(new Point(400, 240)));
        });
    }

    [Fact]
    public void ArrangementSharedGroupContextHighlightPersistsUntilExplicitlyCleared()
    {
        RunOnSta(() =>
        {
            MidoraId sharedUsageId = new(100);
            TimelineRenderSnapshot snapshot = new(
                1,
                "arrangement:brace-context-highlight",
                [],
                ["Conductor", "Shared A", "Shared B"],
                arrangementLanes:
                [
                    new(0, ArrangementLaneKind.Conductor, null, null, 0, true, false, false),
                    new(1, ArrangementLaneKind.LogicalTrack, new MidoraId(11), null, 0, true, false, true)
                    {
                        SharedGroupId = sharedUsageId,
                        IsSharedGroup = true,
                        IsSharedGroupStart = true,
                        SharedGroupMemberCount = 2
                    },
                    new(2, ArrangementLaneKind.LogicalTrack, new MidoraId(12), null, 0, true, false, true)
                    {
                        SharedGroupId = sharedUsageId,
                        IsSharedGroup = true,
                        IsSharedGroupEnd = true,
                        SharedGroupMemberCount = 2
                    }
                ]);
            TimelineSurface surface = new()
            {
                Snapshot = snapshot,
                SurfaceMode = TimelineSurfaceMode.Arrangement,
                LaneHeight = 60,
                TickSpan = 1_920
            };
            Grid host = new();
            host.Children.Add(surface);
            host.Measure(new Size(800, 260));
            host.Arrange(new Rect(0, 0, 800, 260));

            byte[] initial = RenderVisual(surface);
            surface.SetArrangementSharedGroupContextHighlight(sharedUsageId);
            byte[] highlighted = RenderVisual(surface);
            surface.SetArrangementSharedGroupContextHighlight(null);
            byte[] cleared = RenderVisual(surface);

            Assert.False(initial.SequenceEqual(highlighted));
            Assert.Equal(initial, cleared);
        });
    }

    [Theory]
    [InlineData(111.999, true, false, ArrangementSharedGroupDropZone.Before)]
    [InlineData(112, true, false, ArrangementSharedGroupDropZone.Body)]
    [InlineData(208, true, false, ArrangementSharedGroupDropZone.Body)]
    [InlineData(208.001, true, false, ArrangementSharedGroupDropZone.After)]
    [InlineData(103.999, true, true, ArrangementSharedGroupDropZone.Before)]
    [InlineData(104, true, true, ArrangementSharedGroupDropZone.Body)]
    [InlineData(107.999, false, false, ArrangementSharedGroupDropZone.Before)]
    [InlineData(108, false, false, ArrangementSharedGroupDropZone.Body)]
    [InlineData(212, false, false, ArrangementSharedGroupDropZone.Body)]
    [InlineData(212.001, false, false, ArrangementSharedGroupDropZone.After)]
    public void ArrangementSharedGroupDropZoneSeparatesExteriorAndBodyTargets(
        double pointerY,
        bool differentGroup,
        bool retainedJoin,
        ArrangementSharedGroupDropZone expected)
    {
        Assert.Equal(
            expected,
            TimelineToolPolicy.ResolveArrangementSharedGroupDropZone(
                pointerY,
                groupTop: 100,
                groupBottom: 220,
                differentGroup,
                retainedJoin));
    }

    [Fact]
    public void ArrangementExteriorPreviewUsesTheSharedGroupOuterBoundary()
    {
        Assert.Equal(
            100,
            TimelineToolPolicy.ResolveArrangementSharedGroupBoundaryY(
                100,
                220,
                ArrangementSharedGroupDropZone.Before));
        Assert.Equal(
            220,
            TimelineToolPolicy.ResolveArrangementSharedGroupBoundaryY(
                100,
                220,
                ArrangementSharedGroupDropZone.After));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TimelineToolPolicy.ResolveArrangementSharedGroupBoundaryY(
                100,
                220,
                ArrangementSharedGroupDropZone.Body));
    }

    [Fact]
    public void PianoNoteBoundsOccupyTheWholePixelAlignedKeyRow()
    {
        TimelineViewport viewport = new(0, 100, 4, 8, 400, 24, 3);
        TimelineRenderItem note = Item(
            1,
            10,
            20,
            5,
            kind: TimelineItemKind.DirectMidiNote);

        Rect bounds = TimelineRasterPlacement.GetUnclippedItemBounds(
            viewport,
            note,
            laneHeaderWidth: 52,
            rulerHeight: 24,
            laneHeight: 3);

        Assert.Equal(27, bounds.Top);
        Assert.Equal(3, bounds.Height);
    }

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
    public void ContainmentMappingDoesNotRoundTheLastHalfTickIntoTheNextObject()
    {
        TimelineViewport viewport = new(0, 40, 0, 1, 400, 18, 18);

        Assert.Equal(20, viewport.XToTick(196));
        Assert.Equal(19, viewport.XToContainingTick(196));
        Assert.Equal(20, viewport.XToContainingTick(200));
        Assert.Equal(39, viewport.XToContainingTick(400));
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
    public void SnapshotCarriesLaneColorsAndSegmentAccentChangesRenderFingerprint()
    {
        TimelineRenderItem plainItem = Item(
            1,
            0,
            480,
            0,
            kind: TimelineItemKind.Segment);
        TimelineRenderItem accentedItem = plainItem with { AccentColor = 0xff336699 };
        TimelineRenderSnapshot plain = new(12, "arrangement", [plainItem]);
        TimelineRenderSnapshot accented = new(
            12,
            "arrangement",
            [accentedItem],
            ["Track"],
            laneColors: [0xff336699]);

        Assert.Equal(0xff336699u, accented.LaneColors[0]);
        Assert.NotEqual(plain.ContentFingerprint, accented.ContentFingerprint);
    }

    [Fact]
    public void SegmentAccentPaletteNormalizesSourceIntensityWhileRetainingHue()
    {
        TimelineAccentPalette brightRed = TimelineAccentPalette.FromArgb(0xffff0000);
        TimelineAccentPalette darkRed = TimelineAccentPalette.FromArgb(0xff800000);
        TimelineAccentPalette green = TimelineAccentPalette.FromArgb(0xff00ff00);

        Assert.Equal(brightRed, darkRed);
        Assert.NotEqual(brightRed, green);
        Assert.All(
            new[]
            {
                brightRed.Segment,
                brightRed.SelectedSegment,
                brightRed.SelectionBorder,
                brightRed.NotePreview
            },
            value => Assert.Equal(byte.MaxValue, value.A));
        Assert.NotEqual(brightRed.Segment, brightRed.NotePreview);
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
        Assert.True(TimelineToolPolicy.SupportsCopyDrag(
            TimelineToolMode.Draw,
            TimelineSurfaceMode.EventLanes,
            TimelineItemKind.LogicalParameterPoint,
            TimelineItemEditKind.Move));
        Assert.True(TimelineToolPolicy.CanBeginItemEdit(
            TimelineToolMode.Draw,
            TimelineSurfaceMode.EventLanes,
            TimelineItemKind.DirectMidiEvent));
        Assert.True(TimelineToolPolicy.SupportsCopyDrag(
            TimelineToolMode.Draw,
            TimelineSurfaceMode.EventLanes,
            TimelineItemKind.OpaqueMidiEvent,
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

    [Fact]
    public void ConductorUsesTheSameDirectDrawAndSelectInteractionPolicy()
    {
        Assert.True(TimelineToolPolicy.IsDirectEditingSurface(TimelineSurfaceMode.Conductor));
        Assert.True(TimelineToolPolicy.RequestsBackgroundCreation(
            TimelineToolMode.Draw,
            TimelineSurfaceMode.Conductor,
            clickCount: 1));
        Assert.True(TimelineToolPolicy.StartsMarqueeBeforeItemHit(
            TimelineToolMode.Select,
            TimelineSurfaceMode.Conductor,
            clickCount: 1));
        Assert.True(TimelineToolPolicy.CanBeginItemEdit(
            TimelineToolMode.Draw,
            TimelineSurfaceMode.Conductor,
            TimelineItemKind.ConductorEvent));
        Assert.False(TimelineToolPolicy.CanBeginItemEdit(
            TimelineToolMode.Select,
            TimelineSurfaceMode.Conductor,
            TimelineItemKind.ConductorEvent));
        Assert.Equal(
            TimelineItemEditKind.Move,
            TimelineToolPolicy.ResolveItemEditKind(
                TimelineToolMode.Draw,
                TimelineSurfaceMode.Conductor,
                TimelineItemKind.ConductorEvent,
                ModifierKeys.None,
                isNearStart: true,
                isNearEnd: true));
        Assert.Equal(
            TimelinePointerIntent.Move,
            TimelineToolPolicy.GetPointerIntent(
                TimelineToolMode.Draw,
                TimelineSurfaceMode.Conductor,
                isInContent: true,
                TimelineItemKind.ConductorEvent,
                isNearHorizontalEdge: true));
    }

    [Fact]
    public void DrawEventPointUsesVerticalEditPointerUnlessAltForcesTrace()
    {
        Assert.Equal(
            TimelinePointerIntent.ResizeVertical,
            TimelineToolPolicy.GetPointerIntent(
                TimelineToolMode.Draw,
                TimelineSurfaceMode.EventLanes,
                isInContent: true,
                TimelineItemKind.LogicalParameterPoint,
                isNearHorizontalEdge: true));
        Assert.Equal(
            TimelinePointerIntent.Crosshair,
            TimelineToolPolicy.GetPointerIntent(
                TimelineToolMode.Draw,
                TimelineSurfaceMode.EventLanes,
                isInContent: true,
                TimelineItemKind.LogicalParameterPoint,
                isNearHorizontalEdge: true,
                ModifierKeys.Alt));
    }

    [Theory]
    [InlineData(TimelineToolMode.Select, TimelineSurfaceMode.Arrangement, 1, true)]
    [InlineData(TimelineToolMode.Select, TimelineSurfaceMode.PianoRoll, 1, true)]
    [InlineData(TimelineToolMode.Select, TimelineSurfaceMode.PianoRoll, 2, false)]
    [InlineData(TimelineToolMode.Draw, TimelineSurfaceMode.Arrangement, 1, false)]
    [InlineData(TimelineToolMode.Select, TimelineSurfaceMode.EventLanes, 1, true)]
    public void DirectTimelineSelectStartsMarqueeBeforeItemHit(
        TimelineToolMode toolMode,
        TimelineSurfaceMode surfaceMode,
        int clickCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            TimelineToolPolicy.StartsMarqueeBeforeItemHit(toolMode, surfaceMode, clickCount));
    }

    [Theory]
    [InlineData(ModifierKeys.None, WorkspaceSelectionRangeMode.Replace)]
    [InlineData(ModifierKeys.Control, WorkspaceSelectionRangeMode.Add)]
    [InlineData(ModifierKeys.Alt, WorkspaceSelectionRangeMode.Remove)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt, WorkspaceSelectionRangeMode.Toggle)]
    [InlineData(ModifierKeys.Shift, WorkspaceSelectionRangeMode.Add)]
    public void MarqueeModifiersResolveToExplicitSetOperations(
        ModifierKeys modifiers,
        WorkspaceSelectionRangeMode expected)
    {
        Assert.Equal(expected, TimelineToolPolicy.ResolveMarqueeSelectionMode(modifiers));
    }

    [Fact]
    public void AltLeftForcesValueTraceWithoutDirectPointManipulation()
    {
        Assert.True(TimelineToolPolicy.ForcesValueTrace(
            TimelineToolMode.Select,
            TimelineSurfaceMode.Velocity,
            MouseButton.Left,
            ModifierKeys.Alt));
        Assert.True(TimelineToolPolicy.ForcesValueTrace(
            TimelineToolMode.Draw,
            TimelineSurfaceMode.EventLanes,
            MouseButton.Left,
            ModifierKeys.Alt | ModifierKeys.Control));
        Assert.False(TimelineToolPolicy.ForcesValueTrace(
            TimelineToolMode.Select,
            TimelineSurfaceMode.EventLanes,
            MouseButton.Left,
            ModifierKeys.Alt));
        Assert.False(TimelineToolPolicy.ForcesValueTrace(
            TimelineToolMode.Draw,
            TimelineSurfaceMode.EventLanes,
            MouseButton.Left,
            ModifierKeys.None));
        Assert.False(TimelineToolPolicy.ForcesValueTrace(
            TimelineToolMode.Draw,
            TimelineSurfaceMode.EventLanes,
            MouseButton.Left,
            ModifierKeys.Shift));
        Assert.False(TimelineToolPolicy.ForcesValueTrace(
            TimelineToolMode.Draw,
            TimelineSurfaceMode.EventLanes,
            MouseButton.Right,
            ModifierKeys.Alt));
        Assert.False(TimelineToolPolicy.ForcesValueTrace(
            TimelineToolMode.Draw,
            TimelineSurfaceMode.PianoRoll,
            MouseButton.Left,
            ModifierKeys.Alt));
    }

    [Fact]
    public void ShiftRightRequestsHorizontalTraceOnlyInDrawEventLanes()
    {
        Assert.True(TimelineToolPolicy.RequestsHorizontalValueTrace(
            TimelineToolMode.Draw,
            TimelineSurfaceMode.EventLanes,
            MouseButton.Right,
            ModifierKeys.Shift));
        Assert.False(TimelineToolPolicy.RequestsHorizontalValueTrace(
            TimelineToolMode.Draw,
            TimelineSurfaceMode.EventLanes,
            MouseButton.Right,
            ModifierKeys.None));
        Assert.False(TimelineToolPolicy.RequestsHorizontalValueTrace(
            TimelineToolMode.Select,
            TimelineSurfaceMode.EventLanes,
            MouseButton.Right,
            ModifierKeys.Shift));
        Assert.False(TimelineToolPolicy.RequestsHorizontalValueTrace(
            TimelineToolMode.Draw,
            TimelineSurfaceMode.Velocity,
            MouseButton.Right,
            ModifierKeys.Shift));
    }

    [Fact]
    public void ShiftLocksTimeOnlyForPointCreationAndNoteOrPointMoves()
    {
        Assert.True(TimelineToolPolicy.RequestsTimeLockedPointCreation(
            TimelineToolMode.Draw,
            TimelineSurfaceMode.EventLanes,
            MouseButton.Left,
            ModifierKeys.Shift));
        Assert.False(TimelineToolPolicy.RequestsTimeLockedPointCreation(
            TimelineToolMode.Draw,
            TimelineSurfaceMode.EventLanes,
            MouseButton.Right,
            ModifierKeys.Shift));
        Assert.True(TimelineToolPolicy.RequestsTimeLockedNotePlacement(
            TimelineToolMode.Draw,
            TimelineSurfaceMode.PianoRoll,
            MouseButton.Left,
            ModifierKeys.Shift | ModifierKeys.Control));
        Assert.True(TimelineToolPolicy.RequestsTimeLockedItemMove(
            TimelineToolMode.Draw,
            TimelineSurfaceMode.PianoRoll,
            TimelineItemKind.LogicalNote,
            TimelineItemEditKind.Move,
            ModifierKeys.Shift));
        Assert.True(TimelineToolPolicy.RequestsTimeLockedItemMove(
            TimelineToolMode.Draw,
            TimelineSurfaceMode.EventLanes,
            TimelineItemKind.LogicalParameterPoint,
            TimelineItemEditKind.Move,
            ModifierKeys.Shift | ModifierKeys.Control));
        Assert.False(TimelineToolPolicy.RequestsTimeLockedItemMove(
            TimelineToolMode.Draw,
            TimelineSurfaceMode.PianoRoll,
            TimelineItemKind.TemplateNote,
            TimelineItemEditKind.ResizeEnd,
            ModifierKeys.Shift));
        Assert.False(TimelineToolPolicy.RequestsTimeLockedItemMove(
            TimelineToolMode.Draw,
            TimelineSurfaceMode.Arrangement,
            TimelineItemKind.Segment,
            TimelineItemEditKind.Move,
            ModifierKeys.Shift));
    }

    [Theory]
    [InlineData(TimelineSurfaceMode.Arrangement, TimelineItemKind.Segment)]
    [InlineData(TimelineSurfaceMode.PianoRoll, TimelineItemKind.LogicalNote)]
    [InlineData(TimelineSurfaceMode.PianoRoll, TimelineItemKind.TemplateNote)]
    public void AltForcesDirectItemMoveFromEitherResizeEdge(
        TimelineSurfaceMode surfaceMode,
        TimelineItemKind itemKind)
    {
        Assert.True(TimelineToolPolicy.ForcesItemMove(
            TimelineToolMode.Draw,
            surfaceMode,
            itemKind,
            ModifierKeys.Alt));
        Assert.Equal(
            TimelineItemEditKind.Move,
            TimelineToolPolicy.ResolveItemEditKind(
                TimelineToolMode.Draw,
                surfaceMode,
                itemKind,
                ModifierKeys.Alt,
                isNearStart: true,
                isNearEnd: false));
        Assert.Equal(
            TimelineItemEditKind.Move,
            TimelineToolPolicy.ResolveItemEditKind(
                TimelineToolMode.Draw,
                surfaceMode,
                itemKind,
                ModifierKeys.Alt | ModifierKeys.Control,
                isNearStart: false,
                isNearEnd: true));
        Assert.True(TimelineToolPolicy.SupportsCopyDrag(
            TimelineToolMode.Draw,
            surfaceMode,
            itemKind,
            TimelineItemEditKind.Move));
        Assert.Equal(
            TimelinePointerIntent.Move,
            TimelineToolPolicy.GetPointerIntent(
                TimelineToolMode.Draw,
                surfaceMode,
                isInContent: true,
                itemKind,
                isNearHorizontalEdge: true,
                ModifierKeys.Alt));
    }

    [Fact]
    public void AltDoesNotForceMoveOutsideDirectDrawManipulation()
    {
        Assert.False(TimelineToolPolicy.ForcesItemMove(
            TimelineToolMode.Select,
            TimelineSurfaceMode.Arrangement,
            TimelineItemKind.Segment,
            ModifierKeys.Alt));
        Assert.False(TimelineToolPolicy.ForcesItemMove(
            TimelineToolMode.Draw,
            TimelineSurfaceMode.EventLanes,
            TimelineItemKind.TemplateEvent,
            ModifierKeys.Alt));
        Assert.False(TimelineToolPolicy.ForcesItemMove(
            TimelineToolMode.Draw,
            TimelineSurfaceMode.EventLanes,
            TimelineItemKind.LogicalParameterPoint,
            ModifierKeys.Alt));
        Assert.Equal(
            TimelinePointerIntent.Crosshair,
            TimelineToolPolicy.GetPointerIntent(
                TimelineToolMode.Draw,
                TimelineSurfaceMode.EventLanes,
                isInContent: true,
                TimelineItemKind.LogicalParameterPoint,
                isNearHorizontalEdge: true,
                ModifierKeys.Alt));
        Assert.Equal(
            TimelineItemEditKind.ResizeStart,
            TimelineToolPolicy.ResolveItemEditKind(
                TimelineToolMode.Draw,
                TimelineSurfaceMode.Arrangement,
                TimelineItemKind.Segment,
                ModifierKeys.None,
                isNearStart: true,
                isNearEnd: false));
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
    public void SegmentPreviewTilesInvalidateOnlyTheEditedLayerAndAffectedTile()
    {
        TimelineSegmentPreview original = new(
            new MidoraId(132),
            [new TimelineSegmentPreviewNote(0.1, 0.2, 60)],
            [new TimelineSegmentPreviewEvent(0.1, 0.5)]);
        TimelineSegmentPreview noteEdited = new(
            new MidoraId(132),
            [new TimelineSegmentPreviewNote(0.1, 0.2, 61)],
            [new TimelineSegmentPreviewEvent(0.1, 0.5)]);
        TimelineSegmentPreview eventEdited = new(
            new MidoraId(132),
            [new TimelineSegmentPreviewNote(0.1, 0.2, 60)],
            [new TimelineSegmentPreviewEvent(0.1, 0.75)]);
        const double deviceWidth = 1_024;

        Assert.NotEqual(
            TimelineSegmentPreviewRasterizer.ComputeNoteTileContentFingerprint(
                original, deviceWidth, tileX: 0),
            TimelineSegmentPreviewRasterizer.ComputeNoteTileContentFingerprint(
                noteEdited, deviceWidth, tileX: 0));
        Assert.Equal(
            TimelineSegmentPreviewRasterizer.ComputeNoteTileContentFingerprint(
                original, deviceWidth, tileX: 3),
            TimelineSegmentPreviewRasterizer.ComputeNoteTileContentFingerprint(
                noteEdited, deviceWidth, tileX: 3));
        Assert.Equal(
            TimelineSegmentPreviewRasterizer.ComputeEventTileContentFingerprint(
                original, deviceWidth, tileX: 0),
            TimelineSegmentPreviewRasterizer.ComputeEventTileContentFingerprint(
                noteEdited, deviceWidth, tileX: 0));
        Assert.Equal(
            TimelineSegmentPreviewRasterizer.ComputeNoteTileContentFingerprint(
                original, deviceWidth, tileX: 0),
            TimelineSegmentPreviewRasterizer.ComputeNoteTileContentFingerprint(
                eventEdited, deviceWidth, tileX: 0));
        Assert.NotEqual(
            TimelineSegmentPreviewRasterizer.ComputeEventTileContentFingerprint(
                original, deviceWidth, tileX: 0),
            TimelineSegmentPreviewRasterizer.ComputeEventTileContentFingerprint(
                eventEdited, deviceWidth, tileX: 0));
    }

    [Fact]
    public void SegmentEventTileAggregatesOneLinePerDeviceColumnAtMaximumHeight()
    {
        TimelineSegmentPreview preview = new(
            new MidoraId(132),
            [],
            [
                new TimelineSegmentPreviewEvent(0.1, 0.25),
                new TimelineSegmentPreviewEvent(0.1001, 0.75)
            ]);

        TimelineRasterBuffer raster = TimelineSegmentPreviewRasterizer.RasterizeEventTile(
            preview,
            deviceSegmentWidth: 100,
            deviceHeight: 100,
            tileX: 0,
            Color.FromRgb(229, 61, 68));

        Assert.Equal(1, raster.CandidateCount);
        Assert.Equal(0, Alpha(raster, 10, 10));
        Assert.True(Alpha(raster, 10, 25) > 0);
        Assert.Equal(0, Alpha(raster, 9, 80));
        Assert.Equal(0, Alpha(raster, 11, 80));
    }

    [Fact]
    public void ConductorPreviewUsesLocalTileFingerprintsAndAggregatesSameTypeColumns()
    {
        TimelineRenderItem near = Item(
            1, 10, 11, 0, kind: TimelineItemKind.ConductorEvent) with
        { ZIndex = 0, AccentColor = 0xffe5484d };
        TimelineRenderItem duplicate = Item(
            2, 10, 11, 0, kind: TimelineItemKind.ConductorEvent) with
        { ZIndex = 0, AccentColor = 0xffe5484d };
        TimelineRenderItem far = Item(
            3, 600, 601, 0, kind: TimelineItemKind.Marker) with
        { ZIndex = 3, AccentColor = 0xffe8b34b };
        TimelineRenderSnapshot original = new(
            1,
            "arrangement",
            [near, duplicate, far]);
        TimelineRenderSnapshot editedFar = new(
            2,
            "arrangement",
            [near, duplicate, far with { StartTick = 610, EndTick = 611 }]);

        Assert.Equal(
            TimelineConductorTileRasterizer.ComputeContentFingerprint(
                original, 1, tileX: 0, dpiScaleX: 1),
            TimelineConductorTileRasterizer.ComputeContentFingerprint(
                editedFar, 1, tileX: 0, dpiScaleX: 1));
        Assert.NotEqual(
            TimelineConductorTileRasterizer.ComputeContentFingerprint(
                original, 1, tileX: 2, dpiScaleX: 1),
            TimelineConductorTileRasterizer.ComputeContentFingerprint(
                editedFar, 1, tileX: 2, dpiScaleX: 1));

        TimelineRasterBuffer tile = TimelineConductorTileRasterizer.Rasterize(
            original,
            devicePixelsPerTick: 1,
            deviceLaneHeight: 50,
            tileX: 0,
            dpiScaleX: 1,
            dpiScaleY: 1,
            Color.FromRgb(98, 166, 246),
            Color.FromRgb(42, 48, 58));
        Assert.Equal(1, tile.CandidateCount);
    }

    [Fact]
    public void ConductorStableTileExcludesProjectEndMarker()
    {
        TimelineRenderSnapshot withEnd = new(
            1,
            "arrangement",
            [Item(1, 10, 11, 0, kind: TimelineItemKind.ProjectEndMarker)]);
        TimelineRenderSnapshot empty = new(1, "arrangement", []);

        Assert.Equal(
            TimelineConductorTileRasterizer.ComputeContentFingerprint(
                empty, 1, tileX: 0, dpiScaleX: 1),
            TimelineConductorTileRasterizer.ComputeContentFingerprint(
                withEnd, 1, tileX: 0, dpiScaleX: 1));
        Assert.Equal(
            0,
            TimelineConductorTileRasterizer.Rasterize(
                withEnd,
                devicePixelsPerTick: 1,
                deviceLaneHeight: 50,
                tileX: 0,
                dpiScaleX: 1,
                dpiScaleY: 1,
                Color.FromRgb(98, 166, 246),
                Color.FromRgb(42, 48, 58)).CandidateCount);
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
    public void PianoTileCanUseABrighterNormalOutlineThanItsFill()
    {
        TimelineRenderSnapshot snapshot = new(
            1,
            "segment:swapped-note-colors",
            [Item(1, 10, 30, 2)]);
        Color darkFill = Color.FromRgb(68, 75, 80);
        Color brightOutline = Color.FromRgb(163, 178, 190);

        TimelineRasterBuffer raster = TimelinePianoTileRasterizer.Rasterize(
            snapshot,
            devicePixelsPerTick: 4,
            devicePixelsPerLane: 16,
            tileX: 0,
            tileY: 0,
            darkFill,
            Color.FromRgb(232, 179, 75),
            normalOutlineColor: brightOutline);

        Color outline = PixelColor(raster, 41, 40);
        Color fill = PixelColor(raster, 60, 40);
        Assert.True(outline.R > fill.R);
        Assert.True(outline.G > fill.G);
        Assert.True(outline.B > fill.B);
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
    public void SourceBackedSegmentPreviewReportsAndRasterizesContentWithoutMaterializingArrays()
    {
        TimelineSegmentPreview preview = new(
            new MidoraId(1),
            new TestSegmentPreviewSource());

        Assert.Empty(preview.Notes);
        Assert.Empty(preview.Events);
        Assert.True(preview.HasNoteContent);
        Assert.True(preview.HasEventContent);

        TimelineRasterBuffer notes = TimelineSegmentPreviewRasterizer.RasterizeNoteTile(
            preview,
            deviceSegmentWidth: 512,
            deviceHeight: 64,
            tileX: 0,
            Color.FromRgb(189, 199, 207));
        TimelineRasterBuffer events = TimelineSegmentPreviewRasterizer.RasterizeEventTile(
            preview,
            deviceSegmentWidth: 512,
            deviceHeight: 64,
            tileX: 0,
            Color.FromRgb(229, 61, 68));

        Assert.Equal(1, notes.CandidateCount);
        Assert.Equal(1, events.CandidateCount);
    }

    [Fact]
    public void FixedArrangementPreviewTileCombinesSourceBackedNotesAndEvents()
    {
        TimelineSegmentPreview preview = new(
            new MidoraId(1),
            new TestSegmentPreviewSource());

        TimelineRasterBuffer raster =
            TimelineSegmentPreviewRasterizer.RasterizeFixedPreviewTile(
                preview,
                segmentLengthTicks: 3_072,
                ticksPerQuarterNote: 768,
                lod: 0,
                tileX: 0,
                Color.FromRgb(189, 199, 207),
                Color.FromRgb(105, 47, 52));

        Assert.Equal(TimelineSegmentPreviewRasterizer.FixedPreviewTileSize, raster.Width);
        Assert.Equal(TimelineSegmentPreviewRasterizer.Height, raster.Height);
        Assert.Equal(2, raster.CandidateCount);
        Assert.Contains(raster.Pixels.Where((_, index) => index % 4 == 3), alpha => alpha > 0);
        Assert.Equal(
            384,
            TimelineSegmentPreviewRasterizer.GetFixedPreviewContentWidth(3_072, 768));
    }

    [Theory]
    [InlineData(100, 48, 48)]
    [InlineData(20, 48, 20)]
    [InlineData(100, 1, 1)]
    public void ResizePreviewUsesSnapStepAsMinimumWithoutExpandingShortExistingItems(
        long currentLengthTicks,
        long operationStepTicks,
        long expected)
    {
        Assert.Equal(
            expected,
            TimelineToolPolicy.ResolveResizeMinimumLength(
                currentLengthTicks,
                operationStepTicks));
    }

    [Fact]
    public void FixedArrangementPreviewUsesBoundedLodWhenZoomedOut()
    {
        const long segmentLengthTicks = 18_000_000;
        const int ticksPerQuarterNote = 768;
        int displayLod = TimelineSegmentPreviewRasterizer.SelectDisplayLod(
            currentPixelsPerTick: 560d / segmentLengthTicks,
            ticksPerQuarterNote);
        long displayWidth = TimelineSegmentPreviewRasterizer.GetFixedPreviewContentWidth(
            segmentLengthTicks,
            ticksPerQuarterNote,
            displayLod);
        int warmupLod = TimelineSegmentPreviewRasterizer.SelectWarmupLod(
            segmentLengthTicks,
            ticksPerQuarterNote);
        long warmupWidth = TimelineSegmentPreviewRasterizer.GetFixedPreviewContentWidth(
            segmentLengthTicks,
            ticksPerQuarterNote,
            warmupLod);

        Assert.InRange(displayWidth, 280, 1_120);
        Assert.InRange(
            1 + ((displayWidth - 1) / TimelineSegmentPreviewRasterizer.FixedPreviewTileSize),
            1,
            5);
        Assert.InRange(
            1 + ((warmupWidth - 1) / TimelineSegmentPreviewRasterizer.FixedPreviewTileSize),
            1,
            TimelineSegmentPreviewRasterizer.MaximumWarmupTilesPerSegment);
    }

    [Fact]
    public void ZoomingOutKeepsTheCompletedFallbackVisibleWhileTargetLodLoads()
    {
        RunOnSta(() =>
        {
            const long segmentLengthTicks = 36_000;
            const long zoomedOutTickSpan = 360_000;
            TimelineRasterCacheSession.Clear();
            TimelineRenderItem segment = Item(
                1,
                0,
                segmentLengthTicks,
                0,
                kind: TimelineItemKind.Segment);
            TimelineSegmentPreview preview = new(
                segment.Id,
                [new TimelineSegmentPreviewNote(0, 1, 60)]);
            ArrangementLaneDescriptor[] lanes =
            [
                new(
                    0,
                    ArrangementLaneKind.LogicalTrack,
                    new MidoraId(2),
                    null,
                    0,
                    true,
                    false,
                    true)
            ];
            TimelineRenderSnapshot withPreview = new(
                1,
                "arrangement:zoom-fallback",
                [segment],
                ["Track"],
                segmentPreviews: new Dictionary<MidoraId, TimelineSegmentPreview>
                {
                    [segment.Id] = preview
                },
                arrangementLanes: lanes);
            TimelineRenderSnapshot withoutPreview = new(
                1,
                "arrangement:zoom-fallback-baseline",
                [segment],
                ["Track"],
                arrangementLanes: lanes);
            TimelineSurface surface = CreateArrangementSurface(withPreview, tickSpan: 3_072);
            byte[] initialBaseline = RenderVisual(
                CreateArrangementSurface(withoutPreview, tickSpan: 3_072));
            bool fallbackVisible = false;
            try
            {
                for (int attempt = 0; attempt < 200 && !fallbackVisible; attempt++)
                {
                    fallbackVisible = !initialBaseline.SequenceEqual(RenderVisual(surface));
                    if (fallbackVisible) break;
                    Thread.Sleep(10);
                    PumpDispatcher();
                }
                Assert.True(fallbackVisible, "The viewport-independent fallback never became visible.");

                surface.TickSpan = zoomedOutTickSpan;
                byte[] zoomedBaseline = RenderVisual(
                    CreateArrangementSurface(withoutPreview, zoomedOutTickSpan));
                byte[] firstZoomedFrame = RenderVisual(surface);

                Assert.False(
                    zoomedBaseline.SequenceEqual(firstZoomedFrame),
                    "Zooming out cleared the completed fallback while a new target LOD was loading.");
            }
            finally
            {
                TimelineRasterCacheSession.Clear();
            }
        });
    }

    [Fact]
    public void ReadyDetailTilesExcludeFallbackFromTheirHorizontalRanges()
    {
        Rect visible = new(10, 20, 100, 40);
        Rect[] detailTiles =
        [
            new(0, 20, 30, 40),
            new(30, 20, 25, 40),
            new(85, 20, 40, 40)
        ];
        List<Rect> gaps = [];

        TimelineRasterPlacement.BuildUncoveredHorizontalGaps(
            visible,
            detailTiles,
            gaps);

        Assert.Equal(
            [new Rect(55, 20, 30, 40)],
            gaps);
    }

    [Fact]
    public void SelectedArrangementPreviewLodKeepsOnePixelNoteVisibleAcrossPanPhases()
    {
        const long segmentLengthTicks = 3_072;
        const int ticksPerQuarterNote = 768;
        const double currentPixelsPerTick = 0.05;
        TimelineSegmentPreview preview = new(
            new MidoraId(1),
            [new TimelineSegmentPreviewNote(0.25, 0.250_001, 60)]);
        int lod = TimelineSegmentPreviewRasterizer.SelectDisplayLod(
            currentPixelsPerTick,
            ticksPerQuarterNote);
        Assert.True(
            TimelineSegmentPreviewRasterizer.GetFixedPreviewPixelsPerTick(
                ticksPerQuarterNote,
                lod) <= currentPixelsPerTick);
        TimelineRasterBuffer raster = TimelineSegmentPreviewRasterizer.RasterizeFixedPreviewTile(
            preview,
            segmentLengthTicks,
            ticksPerQuarterNote,
            lod,
            tileX: 0,
            Color.FromRgb(189, 199, 207),
            Color.FromRgb(105, 47, 52));
        BitmapSource bitmap = BitmapSource.Create(
            raster.Width,
            raster.Height,
            96,
            96,
            PixelFormats.Pbgra32,
            null,
            raster.Pixels,
            raster.Stride);
        double destinationWidth = segmentLengthTicks * currentPixelsPerTick;

        for (int phase = 0; phase < 16; phase++)
        {
            DrawingVisual visual = new();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
            using (DrawingContext context = visual.RenderOpen())
            {
                context.DrawImage(
                    bitmap,
                    new Rect(50 + phase / 16d, 0, destinationWidth, 60));
            }
            RenderTargetBitmap target = new(300, 60, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            byte[] pixels = new byte[300 * 60 * 4];
            target.CopyPixels(pixels, 300 * 4, 0);

            Assert.Contains(
                pixels.Where((_, index) => index % 4 == 3),
                alpha => alpha > 0);
        }
    }

    [Fact]
    public void ArrangementPreviewTilePlacementKeepsOneDevicePixelPhaseWhilePanning()
    {
        DpiScale dpi = new(1.25, 1.25);
        double? expectedWidth = null;
        for (int phase = 0; phase < 16; phase++)
        {
            Rect destination = TimelineRasterPlacement.GetSegmentPreviewTileDestination(
                new Rect(100 + phase / 16d, 20.25, 600.3, 60),
                contentWidth: 700,
                tileLeft: 256,
                tileWidth: 256,
                dpi);
            double leftDevice = destination.Left * dpi.DpiScaleX;
            double widthDevice = destination.Width * dpi.DpiScaleX;

            Assert.Equal(Math.Round(leftDevice), leftDevice, 9);
            expectedWidth ??= widthDevice;
            Assert.Equal(expectedWidth.Value, widthDevice, 9);
        }
    }

    [Theory]
    [InlineData(3_072L)]
    [InlineData(18_000_000L)]
    public void ArrangementSurfaceEventuallyComposesCachedSegmentPreview(
        long segmentLengthTicks)
    {
        RunOnSta(() =>
        {
            TimelineRasterCacheSession.Clear();
            TimelineRenderItem segment = Item(
                1,
                0,
                segmentLengthTicks,
                0,
                kind: TimelineItemKind.Segment);
            TimelineSegmentPreview preview = new(
                segment.Id,
                [new TimelineSegmentPreviewNote(0.25, 0.5, 60)],
                [new TimelineSegmentPreviewEvent(0.75, 0.6)]);
            TimelineRenderSnapshot withoutPreview = new(
                1,
                $"arrangement:preview-baseline:{segmentLengthTicks}",
                [segment],
                ["Track"],
                arrangementLanes:
                [
                    new(
                        0,
                        ArrangementLaneKind.LogicalTrack,
                        new MidoraId(2),
                        null,
                        0,
                        true,
                        false,
                        true)
                ]);
            TimelineRenderSnapshot withPreview = new(
                1,
                $"arrangement:preview-composed:{segmentLengthTicks}",
                [segment],
                ["Track"],
                segmentPreviews: new Dictionary<MidoraId, TimelineSegmentPreview>
                {
                    [segment.Id] = preview
                },
                arrangementLanes:
                [
                    new(
                        0,
                        ArrangementLaneKind.LogicalTrack,
                        new MidoraId(2),
                        null,
                        0,
                        true,
                        false,
                        true)
                ]);
            TimelineSurface baselineSurface = CreateArrangementSurface(
                withoutPreview,
                segmentLengthTicks);
            TimelineSurface previewSurface = CreateArrangementSurface(
                withPreview,
                segmentLengthTicks);
            byte[] baseline = RenderVisual(baselineSurface);
            TimelineRenderSnapshot empty = new(
                1,
                $"arrangement:preview-empty:{segmentLengthTicks}",
                [],
                ["Track"],
                arrangementLanes:
                [
                    new(
                        0,
                        ArrangementLaneKind.LogicalTrack,
                        new MidoraId(2),
                        null,
                        0,
                        true,
                        false,
                        true)
                ]);
            byte[] emptyPixels = RenderVisual(CreateArrangementSurface(
                empty,
                segmentLengthTicks));
            Assert.False(emptyPixels.SequenceEqual(baseline));
            bool composed = false;
            for (int attempt = 0; attempt < 200 && !composed; attempt++)
            {
                byte[] rendered = RenderVisual(previewSurface);
                composed = !baseline.SequenceEqual(rendered);
                if (composed) break;
                Thread.Sleep(10);
                PumpDispatcher();
            }

            int completedCount = TimelineRasterCacheSession.CompletedCount;
            TimelineRasterCacheSession.Clear();
            Assert.True(
                composed,
                $"The Segment preview never reached the composed surface; completed cache entries: {completedCount}.");
        });
    }

    [Fact]
    public void VisibleFallbackBarrierDefersDetailUntilEverySegmentHasACoarseFrame()
    {
        RunOnSta(() =>
        {
            const long segmentLengthTicks = 36_000;
            ManualResetEventSlim releaseSlowFallback = new(false);
            RecordingSegmentPreviewSource fastSource = new(fingerprint: 11);
            RecordingSegmentPreviewSource slowSource = new(
                fingerprint: 12,
                firstQueryGate: releaseSlowFallback);
            TimelineRenderItem fastSegment = Item(
                1,
                0,
                segmentLengthTicks,
                0,
                kind: TimelineItemKind.Segment);
            TimelineRenderItem slowSegment = Item(
                2,
                0,
                segmentLengthTicks,
                1,
                kind: TimelineItemKind.Segment);
            TimelineRenderSnapshot snapshot = new(
                1,
                "arrangement:progressive-fallback-barrier",
                [fastSegment, slowSegment],
                ["Fast", "Slow"],
                segmentPreviews: new Dictionary<MidoraId, TimelineSegmentPreview>
                {
                    [fastSegment.Id] = new(fastSegment.Id, fastSource),
                    [slowSegment.Id] = new(slowSegment.Id, slowSource)
                },
                arrangementLanes:
                [
                    new(
                        0,
                        ArrangementLaneKind.LogicalTrack,
                        new MidoraId(101),
                        null,
                        0,
                        true,
                        false,
                        true),
                    new(
                        1,
                        ArrangementLaneKind.LogicalTrack,
                        new MidoraId(102),
                        null,
                        0,
                        true,
                        false,
                        true)
                ]);
            TimelineSurface surface = CreateArrangementSurface(snapshot, tickSpan: 3_072);
            TimelineRasterCacheSession.Clear();
            try
            {
                for (int attempt = 0;
                    attempt < 200
                    && fastSource.CoarseQueryCount
                        < TimelineSegmentPreviewRasterizer.MaximumWarmupTilesPerSegment;
                    attempt++)
                {
                    _ = RenderVisual(surface);
                    Thread.Sleep(5);
                    PumpDispatcher();
                }

                Assert.True(
                    fastSource.CoarseQueryCount
                        >= TimelineSegmentPreviewRasterizer.MaximumWarmupTilesPerSegment,
                    "The fast Segment never completed its bounded fallback frame.");
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    _ = RenderVisual(surface);
                    Thread.Sleep(5);
                    PumpDispatcher();
                }
                Assert.False(fastSource.HasDetailedQuery);

                releaseSlowFallback.Set();
                for (int attempt = 0; attempt < 200 && !fastSource.HasDetailedQuery; attempt++)
                {
                    _ = RenderVisual(surface);
                    Thread.Sleep(5);
                    PumpDispatcher();
                }
                Assert.True(
                    fastSource.HasDetailedQuery,
                    "Visible detail was not scheduled after every coarse fallback became complete.");
            }
            finally
            {
                releaseSlowFallback.Set();
                TimelineRasterCacheSession.Clear();
            }
        });
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
            with
        { Value = 0.5 };
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
    public void VelocityTileUsesFixedStemWidthInsteadOfNoteLength()
    {
        TimelineRenderItem velocity = Item(1, 10, 220, 0, kind: TimelineItemKind.Velocity)
            with
        { Value = 0.5, ZIndex = 60 };
        TimelineRenderSnapshot snapshot = new(1, "velocity:fixed-width", [velocity]);
        int lod = TimelineRasterLod.Quantize(1);

        TimelineRasterBuffer raster = TimelineVelocityTileRasterizer.Rasterize(
            snapshot,
            selection: null,
            lod,
            tileX: 0,
            Color.FromRgb(163, 178, 190),
            Color.FromRgb(229, 61, 68),
            Color.FromRgb(49, 58, 69));

        int startX = TimelineVelocityTileRasterizer.Gutter + 10;
        Assert.True(Alpha(raster, startX, 180) > 0);
        Assert.Equal(0, Alpha(raster, TimelineVelocityTileRasterizer.Gutter + 100, 180));
    }

    [Fact]
    public void VelocityMarkerIsWiderThanItsStem()
    {
        TimelineRenderItem velocity = Item(1, 10, 11, 0, kind: TimelineItemKind.Velocity)
            with
        { Value = 0.5, ZIndex = 60 };
        TimelineRenderSnapshot snapshot = new(1, "velocity:marker", [velocity]);
        TimelineRasterBuffer raster = TimelineVelocityTileRasterizer.Rasterize(
            snapshot,
            selection: null,
            TimelineRasterLod.Quantize(1),
            tileX: 0,
            Color.FromRgb(163, 178, 190),
            Color.FromRgb(229, 61, 68),
            Color.FromRgb(49, 58, 69));
        int centerX = TimelineVelocityTileRasterizer.Gutter + 10;
        int top = (int)Math.Round(0.5 * (TimelineVelocityTileRasterizer.RasterHeight - 1), MidpointRounding.AwayFromZero);
        int markerWidth = Enumerable.Range(
                centerX - TimelineVelocityTileRasterizer.MarkerSize,
                TimelineVelocityTileRasterizer.MarkerSize * 2 + 1)
            .Count(x => Alpha(raster, x, top + 2) > 0);
        int stemWidth = Enumerable.Range(
                centerX - TimelineVelocityTileRasterizer.MarkerSize,
                TimelineVelocityTileRasterizer.MarkerSize * 2 + 1)
            .Count(x => Alpha(raster, x, top + TimelineVelocityTileRasterizer.MarkerSize + 3) > 0);

        Assert.Equal(TimelineVelocityTileRasterizer.MarkerSize, markerWidth);
        Assert.Equal(TimelineVelocityTileRasterizer.StemWidth, stemWidth);
        Assert.True(markerWidth > stemWidth);
    }

    [Fact]
    public void VelocityTileDrawsHigherPitchOnTopAtTheSameTick()
    {
        TimelineRenderItem low = Item(1, 10, 11, 0, kind: TimelineItemKind.Velocity)
            with
        { Value = 0.5, ZIndex = 48 };
        TimelineRenderItem high = Item(2, 10, 11, 0, kind: TimelineItemKind.Velocity)
            with
        { Value = 0.5, ZIndex = 84 };
        TimelineRenderSnapshot snapshot = new(1, "velocity:stacking", [high, low]);
        TimelineSelectionSnapshot selection = new(1, [low.Id], low.Id);
        Color normal = Color.FromRgb(10, 80, 160);
        Color selected = Color.FromRgb(220, 20, 30);
        TimelineRasterBuffer raster = TimelineVelocityTileRasterizer.Rasterize(
            snapshot,
            selection,
            TimelineRasterLod.Quantize(1),
            tileX: 0,
            normal,
            selected,
            Color.FromRgb(49, 58, 69));
        int centerX = TimelineVelocityTileRasterizer.Gutter + 10;
        int top = (int)Math.Round(0.5 * (TimelineVelocityTileRasterizer.RasterHeight - 1), MidpointRounding.AwayFromZero);

        Assert.Equal(normal, PixelColor(raster, centerX, top + 2));
    }

    [Fact]
    public void VelocityTileFingerprintIgnoresUnrelatedSelectionRevision()
    {
        TimelineRenderItem velocity = Item(1, 10, 11, 0, kind: TimelineItemKind.Velocity)
            with
        { Value = 0.5, ZIndex = 60 };
        TimelineRenderSnapshot snapshot = new(1, "velocity:selection", [velocity]);
        TimelineSelectionSnapshot first = new(1, [], null);
        TimelineSelectionSnapshot later = new(99, [], null);
        int lod = TimelineRasterLod.Quantize(1);

        Assert.Equal(
            TimelineVelocityTileRasterizer.ComputeContentFingerprint(snapshot, first, lod, 0),
            TimelineVelocityTileRasterizer.ComputeContentFingerprint(snapshot, later, lod, 0));
    }

    [Fact]
    public void EventPointTileKeepsPointSizeAcrossTimeAndValueZoom()
    {
        TimelineRenderItem point = Item(
            1, 10, 11, 0, kind: TimelineItemKind.LogicalParameterPoint) with
        { Value = 0.5 };
        TimelineRenderSnapshot snapshot = new(1, "event-point:fixed-size", [point]);
        Color normal = Color.FromRgb(98, 166, 246);
        Color primary = Color.FromRgb(241, 243, 245);
        Color border = Color.FromRgb(42, 48, 58);

        TimelineRasterBuffer initial = TimelineEventPointTileRasterizer.Rasterize(
            snapshot, null, 1, 256, 0, 0, 1, 1, normal, primary, border);
        TimelineRasterBuffer zoomed = TimelineEventPointTileRasterizer.Rasterize(
            snapshot, null, 8, 768, 0, 1, 1, 1, normal, primary, border);

        Assert.Equal(OpaqueSize(initial), OpaqueSize(zoomed));
        Assert.Equal((8, 8), OpaqueSize(initial));
    }

    [Fact]
    public void EventPointTilePlacementKeepsOneToOneDeviceSizeAcrossViewZoom()
    {
        TimelineViewport initial = new(0, 256, 0, 1, 256, 256, 20);
        TimelineViewport zoomed = new(0, 32, 0, 1, 256, 256, 20);

        Rect initialDestination = TimelineRasterPlacement.GetEventPointTileDestination(
            initial, 1, 256, 0, 0, 52, 20, 0, 1, 1, 1);
        Rect zoomedDestination = TimelineRasterPlacement.GetEventPointTileDestination(
            zoomed, 8, 1_024, 0, 1, 52, 20, 0.25, 0.5, 1, 1);

        Assert.Equal(
            TimelineEventPointTileRasterizer.GetRasterSize(1),
            initialDestination.Width);
        Assert.Equal(initialDestination.Width, zoomedDestination.Width);
        Assert.Equal(initialDestination.Height, zoomedDestination.Height);
    }

    [Fact]
    public void EventPointTileRepeatsPointGutterAcrossHorizontalBoundary()
    {
        TimelineRenderItem point = Item(
            1, 256, 257, 0, kind: TimelineItemKind.LogicalParameterPoint) with
        { Value = 0.5 };
        TimelineRenderSnapshot snapshot = new(1, "event-point:gutter", [point]);
        Color normal = Color.FromRgb(98, 166, 246);
        Color primary = Color.FromRgb(241, 243, 245);
        Color border = Color.FromRgb(42, 48, 58);
        int gutter = TimelineEventPointTileRasterizer.GetGutter(1);

        TimelineRasterBuffer left = TimelineEventPointTileRasterizer.Rasterize(
            snapshot, null, 1, 256, 0, 0, 1, 1, normal, primary, border);
        TimelineRasterBuffer right = TimelineEventPointTileRasterizer.Rasterize(
            snapshot, null, 1, 256, 1, 0, 1, 1, normal, primary, border);

        Assert.True(Alpha(left, gutter + 255, gutter + 128) > 0);
        Assert.True(Alpha(right, gutter, gutter + 128) > 0);
    }

    [Fact]
    public void EventPointTileBatchesSelectionAndPrimaryIntoRaster()
    {
        TimelineRenderItem point = Item(
            1, 10, 11, 0, kind: TimelineItemKind.LogicalParameterPoint) with
        { Value = 0.5 };
        TimelineRenderSnapshot snapshot = new(1, "event-point:selection", [point]);
        TimelineSelectionSnapshot unselected = new(0, [], null);
        TimelineSelectionSnapshot selected = new(1, [point.Id], point.Id);
        Color normal = Color.FromRgb(10, 80, 160);
        Color primary = Color.FromRgb(230, 240, 250);
        Color border = Color.FromRgb(20, 25, 30);

        TimelineRasterBuffer normalRaster = TimelineEventPointTileRasterizer.Rasterize(
            snapshot, unselected, 1, 256, 0, 0, 1, 1, normal, primary, border);
        TimelineRasterBuffer selectedRaster = TimelineEventPointTileRasterizer.Rasterize(
            snapshot, selected, 1, 256, 0, 0, 1, 1, normal, primary, border);

        Assert.False(normalRaster.Pixels.SequenceEqual(selectedRaster.Pixels));
        Assert.Equal((8, 8), OpaqueSize(normalRaster));
        Assert.Equal((12, 12), OpaqueSize(selectedRaster));
    }

    [Fact]
    public void EventPointSelectionTileContainsOnlySelectedPoints()
    {
        TimelineRenderItem selectedPoint = Item(
            1, 20, 21, 0, kind: TimelineItemKind.LogicalParameterPoint) with
        { Value = 0.5 };
        TimelineRenderItem unselectedPoint = Item(
            2, 80, 81, 0, kind: TimelineItemKind.LogicalParameterPoint) with
        { Value = 0.5 };
        TimelineRenderSnapshot snapshot = new(
            1,
            "event-point:selection-only",
            [selectedPoint, unselectedPoint]);
        TimelineSelectionSnapshot selection = new(1, [selectedPoint.Id], selectedPoint.Id);
        int gutter = TimelineEventPointTileRasterizer.GetGutter(1);

        TimelineRasterBuffer raster = TimelineEventPointTileRasterizer.Rasterize(
            snapshot,
            selection,
            1,
            256,
            0,
            0,
            1,
            1,
            Color.FromRgb(98, 166, 246),
            Color.FromRgb(241, 243, 245),
            Color.FromRgb(42, 48, 58),
            selectionOnly: true);

        Assert.Equal(1, raster.CandidateCount);
        Assert.True(Alpha(raster, gutter + 20, gutter + 128) > 0);
        Assert.Equal(0, Alpha(raster, gutter + 80, gutter + 128));
    }

    [Fact]
    public void PianoDragPreviewTileUsesOnlyTheBlueOutline()
    {
        TimelineRenderItem note = Item(1, 20, 30, 2, kind: TimelineItemKind.LogicalNote);
        TimelineRenderSnapshot snapshot = new(1, "piano:drag-outline", [note]);
        TimelineSelectionSnapshot selection = new(1, [note.Id], note.Id);
        Color blue = Color.FromRgb(98, 166, 246);

        TimelineRasterBuffer raster = TimelinePianoTileRasterizer.Rasterize(
            snapshot,
            devicePixelsPerTick: 1,
            devicePixelsPerLane: 16,
            tileX: 0,
            tileY: 0,
            Colors.Transparent,
            Colors.Transparent,
            selection,
            selectionOnly: true,
            outlineColor: blue);

        Assert.Equal(1, raster.CandidateCount);
        Assert.True(Alpha(raster, 20 + TimelinePianoTileRasterizer.Gutter, 35 + TimelinePianoTileRasterizer.Gutter) > 0);
        Assert.Equal(0, Alpha(raster, 25 + TimelinePianoTileRasterizer.Gutter, 40 + TimelinePianoTileRasterizer.Gutter));
    }

    [Fact]
    public void SemanticMarqueeAnchorDoesNotMoveWhenTheViewportScrolls()
    {
        TimelineViewport scrolledViewport = new(
            StartTick: 0,
            EndTick: 1_000,
            FirstLane: 40,
            LaneCount: 20,
            Width: 1_000,
            Height: 400,
            LaneHeight: 20);

        (int firstLane, int lastLaneExclusive) =
            TimelineToolPolicy.ResolveSemanticMarqueeLaneRange(
                anchorLane: 12,
                scrolledViewport,
                currentContentY: 110);

        Assert.Equal(12, firstLane);
        Assert.Equal(46, lastLaneExclusive);
        Assert.Equal(
            (0.2, 0.8),
            TimelineToolPolicy.ResolveSemanticMarqueeValueRange(0.8, 0.2));
    }

    [Fact]
    public void SelectedControlDragDefersSelectionToggleUntilTheGestureIsKnown()
    {
        Assert.True(TimelineToolPolicy.DefersControlSelectionToggleForPotentialDrag(
            TimelineToolMode.Draw,
            TimelineSurfaceMode.EventLanes,
            TimelineItemKind.LogicalParameterPoint,
            ModifierKeys.Control,
            isSelected: true));
        Assert.False(TimelineToolPolicy.DefersControlSelectionToggleForPotentialDrag(
            TimelineToolMode.Draw,
            TimelineSurfaceMode.EventLanes,
            TimelineItemKind.LogicalParameterPoint,
            ModifierKeys.Control,
            isSelected: false));
        Assert.False(TimelineToolPolicy.DefersControlSelectionToggleForPotentialDrag(
            TimelineToolMode.Select,
            TimelineSurfaceMode.EventLanes,
            TimelineItemKind.LogicalParameterPoint,
            ModifierKeys.Control,
            isSelected: true));
    }

    [Fact]
    public void EventPointTileCullsDenseOffscreenPopulationBeforeRasterizing()
    {
        TimelineRenderItem[] points = Enumerable.Range(0, 100_000)
            .Select(index => Item(
                index + 1L,
                index * 4L,
                index * 4L + 1,
                0,
                kind: TimelineItemKind.LogicalParameterPoint) with
            {
                Value = (index % 128) / 127d
            })
            .ToArray();
        TimelineRenderSnapshot snapshot = new(1, "event-point:dense", points);

        TimelineRasterBuffer raster = TimelineEventPointTileRasterizer.Rasterize(
            snapshot,
            null,
            1,
            256,
            tileX: 100,
            tileY: 0,
            dpiScaleX: 1,
            dpiScaleY: 1,
            Color.FromRgb(98, 166, 246),
            Color.FromRgb(241, 243, 245),
            Color.FromRgb(42, 48, 58));

        Assert.InRange(raster.CandidateCount, 60, 70);
        Assert.Contains(raster.Pixels.Where((_, index) => index % 4 == 3), alpha => alpha > 0);
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
    public void PreviousExactScaleTileCanRemainWorldAnchoredDuringZoomTransition()
    {
        const double previousPixelsPerTickDevice = 1.25;
        const double previousPixelsPerLaneDevice = 16;
        TimelineViewport zoomed = new(120, 620, 8, 12, 1_000, 192, 48);

        Rect destination = TimelineRasterPlacement.GetPianoTileCoreDestination(
            zoomed,
            previousPixelsPerTickDevice,
            previousPixelsPerLaneDevice,
            tileX: 1,
            tileY: 1,
            laneHeaderWidth: 52,
            rulerHeight: 24,
            laneHeight: 48);

        double tileStartTick = TimelinePianoTileRasterizer.TileSize / previousPixelsPerTickDevice;
        double tileStartLane = TimelinePianoTileRasterizer.TileSize / previousPixelsPerLaneDevice;
        Assert.Equal(52 + (tileStartTick - zoomed.StartTick) * zoomed.PixelsPerTick, destination.Left, 8);
        Assert.Equal(24 + (tileStartLane - zoomed.FirstLane) * zoomed.LaneHeight, destination.Top, 8);
        Assert.Equal(
            TimelinePianoTileRasterizer.TileSize / previousPixelsPerTickDevice * zoomed.PixelsPerTick,
            destination.Width,
            8);
        Assert.Equal(
            TimelinePianoTileRasterizer.TileSize / previousPixelsPerLaneDevice * zoomed.LaneHeight,
            destination.Height,
            8);
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

    [Theory]
    [InlineData(300, 280, 240, 288)]
    [InlineData(300, 270, 240, 288)]
    [InlineData(300, 260, 240, 288)]
    [InlineData(300, 230, 240, 288)]
    [InlineData(300, 210, 192, 288)]
    public void ReverseMarqueeKeepsSnappedAnchorEdgeStable(
        long rawAnchor,
        long rawMoving,
        long expectedStart,
        long expectedEnd)
    {
        TimelineGridQuantization.SnappedRange range = TimelineGridQuantization.SnapRangeFromAnchor(
            rawAnchor,
            rawMoving,
            fixedStepTicks: 48,
            useBars: false,
            timeSignatureMap: null);

        Assert.Equal(expectedStart, range.StartTick);
        Assert.Equal(expectedEnd, range.EndTick);
    }

    [Theory]
    [InlineData(100, 110, 96, 144)]
    [InlineData(100, 120, 96, 144)]
    [InlineData(100, 130, 96, 144)]
    [InlineData(100, 170, 96, 192)]
    public void ForwardMarqueeKeepsSnappedAnchorEdgeStable(
        long rawAnchor,
        long rawMoving,
        long expectedStart,
        long expectedEnd)
    {
        TimelineGridQuantization.SnappedRange range = TimelineGridQuantization.SnapRangeFromAnchor(
            rawAnchor,
            rawMoving,
            fixedStepTicks: 48,
            useBars: false,
            timeSignatureMap: null);

        Assert.Equal(expectedStart, range.StartTick);
        Assert.Equal(expectedEnd, range.EndTick);
    }

    [Fact]
    public void ReverseMarqueeAtTickZeroStillProducesAPositiveRange()
    {
        TimelineGridQuantization.SnappedRange range = TimelineGridQuantization.SnapRangeFromAnchor(
            rawAnchorTick: 10,
            rawMovingTick: 0,
            fixedStepTicks: 48,
            useBars: false,
            timeSignatureMap: null);

        Assert.Equal(0, range.StartTick);
        Assert.Equal(48, range.EndTick);
    }

    [Fact]
    public void ReverseBarMarqueeKeepsTheSnappedAnchorBoundaryStable()
    {
        MidoraProject project = new(480);
        ProjectTimeSignatureMap map = new(project);

        TimelineGridQuantization.SnappedRange nearAnchor = TimelineGridQuantization.SnapRangeFromAnchor(
            rawAnchorTick: 2_000,
            rawMovingTick: 1_800,
            fixedStepTicks: 1,
            useBars: true,
            map);
        TimelineGridQuantization.SnappedRange fartherLeft = TimelineGridQuantization.SnapRangeFromAnchor(
            rawAnchorTick: 2_000,
            rawMovingTick: 700,
            fixedStepTicks: 1,
            useBars: true,
            map);

        Assert.Equal(new TimelineGridQuantization.SnappedRange(0, 1_920), nearAnchor);
        Assert.Equal(new TimelineGridQuantization.SnappedRange(0, 1_920), fartherLeft);
    }

    [Fact]
    public void DirectEditHitUsesPointerSideAtAHighlyZoomedSharedBoundary()
    {
        TimelineViewport viewport = new(0, 40, 0, 1, 400, 18, 18);
        TimelineRenderItem left = Item(1, 10, 20, 0);
        TimelineRenderItem right = Item(2, 20, 30, 0);
        TimelineIntervalIndex index = new([left, right]);
        List<TimelineRenderItem> candidates = [];
        index.HitTestInto(viewport.XToTick(199), 1, 0, candidates);

        int fromLeft = TimelineToolPolicy.FindPreferredDirectEditEdgeCandidate(
            candidates,
            viewport,
            contentX: 199,
            TimelineToolMode.Draw,
            TimelineSurfaceMode.PianoRoll);
        int fromRight = TimelineToolPolicy.FindPreferredDirectEditEdgeCandidate(
            candidates,
            viewport,
            contentX: 201,
            TimelineToolMode.Draw,
            TimelineSurfaceMode.PianoRoll);

        Assert.Equal(left.Id, candidates[fromLeft].Id);
        Assert.Equal(right.Id, candidates[fromRight].Id);
    }

    [Fact]
    public void DirectEditHitIncludesTheHalfOpenRightBoundary()
    {
        TimelineViewport viewport = new(0, 40, 0, 1, 400, 18, 18);
        TimelineRenderItem item = Item(1, 10, 20, 0);
        TimelineIntervalIndex index = new([item]);
        List<TimelineRenderItem> candidates = [];
        index.HitTestInto(tick: 20, toleranceTicks: 1, lane: 0, candidates);

        int hit = TimelineToolPolicy.FindPreferredDirectEditEdgeCandidate(
            candidates,
            viewport,
            contentX: 200,
            TimelineToolMode.Draw,
            TimelineSurfaceMode.PianoRoll);

        Assert.Equal(item.Id, candidates[hit].Id);
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
    public void ArrangementBarGridIncludesDenominatorBeatsAcrossMeterChanges()
    {
        MidoraProject project = new(480);
        project.Conductor.TimeSignatures.Add(new TimeSignatureChange(project, 1_920, 3, 4));
        project.Conductor.TimeSignatures.Add(new TimeSignatureChange(project, 3_360, 6, 8));
        ProjectTimeSignatureMap map = new(project);
        List<TimelineGridLine> lines = [];

        TimelineGridPresentation.BuildArrangementBarGridLines(1_920, 4_800, map, lines);

        Assert.Equal(
            [
                new(1_920, TimelineGridLineKind.Bar),
                new(2_400, TimelineGridLineKind.Beat),
                new(2_880, TimelineGridLineKind.Beat),
                new(3_360, TimelineGridLineKind.Bar),
                new(3_600, TimelineGridLineKind.Beat),
                new(3_840, TimelineGridLineKind.Beat),
                new(4_080, TimelineGridLineKind.Beat),
                new(4_320, TimelineGridLineKind.Beat),
                new(4_560, TimelineGridLineKind.Beat)
            ],
            lines);
    }

    [Fact]
    public void ArrangementBarGridMakesTruncatedMeterChangeANewBarBoundary()
    {
        MidoraProject project = new(480);
        project.Conductor.TimeSignatures.Add(new TimeSignatureChange(project, 1_000, 3, 4));
        ProjectTimeSignatureMap map = new(project);
        List<TimelineGridLine> lines = [];

        TimelineGridPresentation.BuildArrangementBarGridLines(0, 2_440, map, lines);

        Assert.Equal(
            [
                new(0, TimelineGridLineKind.Bar),
                new(480, TimelineGridLineKind.Beat),
                new(960, TimelineGridLineKind.Beat),
                new(1_000, TimelineGridLineKind.Bar),
                new(1_480, TimelineGridLineKind.Beat),
                new(1_960, TimelineGridLineKind.Beat)
            ],
            lines);
    }

    [Fact]
    public void SegmentBarGridMapsLocalTicksBackToProjectBars()
    {
        MidoraProject project = new(480);
        project.Conductor.TimeSignatures.Add(new TimeSignatureChange(project, 1_000, 3, 4));
        ProjectTimeSignatureMap map = new(project);
        List<TimelineGridLine> lines = [];

        TimelineGridPresentation.BuildBarGridLines(
            startTick: 100,
            endTick: 1_540,
            map,
            lines,
            projectTickOffset: 900);

        Assert.Equal(
            [
                new(100, TimelineGridLineKind.Bar),
                new(580, TimelineGridLineKind.Beat),
                new(1_060, TimelineGridLineKind.Beat)
            ],
            lines);
    }

    [Fact]
    public void ArrangementBarGridSkipsLinesBelowTheVisibleTickResolution()
    {
        MidoraProject project = new(480);
        ProjectTimeSignatureMap map = new(project);
        List<TimelineGridLine> lines = [];

        TimelineGridPresentation.BuildArrangementBarGridLines(
            0,
            1_920_000,
            map,
            lines,
            minimumTickSpacing: 3_000);

        Assert.Equal(500, lines.Count);
        Assert.All(lines, line => Assert.Equal(TimelineGridLineKind.Bar, line.Kind));
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

    [Fact]
    public void WorkspaceSelectionReplaceAllSkipsEquivalentRestore()
    {
        WorkspaceSelection selection = new();
        MidoraId[] ids = Enumerable.Range(1, 10_000)
            .Select(value => new MidoraId(value))
            .ToArray();
        selection.ReplaceAll(ids, ids[123]);
        long revision = selection.Revision;

        bool changed = selection.ReplaceAll(ids.Reverse(), ids[123]);

        Assert.False(changed);
        Assert.Equal(revision, selection.Revision);
        Assert.Equal(ids[123], selection.Primary);
        Assert.Equal(ids.Length, selection.Ids.Count);
    }

    [Fact]
    public void WorkspaceSelectionRangeOperationsHaveSetSemantics()
    {
        WorkspaceSelection selection = new();
        selection.ApplyRange([new MidoraId(1), new MidoraId(2)], WorkspaceSelectionRangeMode.Replace);

        selection.ApplyRange([new MidoraId(2), new MidoraId(3)], WorkspaceSelectionRangeMode.Add);
        Assert.Equal([new MidoraId(1), new MidoraId(2), new MidoraId(3)], selection.Ids.Order());

        selection.ApplyRange([new MidoraId(2), new MidoraId(4)], WorkspaceSelectionRangeMode.Remove);
        Assert.Equal([new MidoraId(1), new MidoraId(3)], selection.Ids.Order());

        selection.ApplyRange([new MidoraId(1), new MidoraId(4)], WorkspaceSelectionRangeMode.Toggle);
        Assert.Equal([new MidoraId(3), new MidoraId(4)], selection.Ids.Order());

        selection.ApplyRange([], WorkspaceSelectionRangeMode.Replace);
        Assert.Empty(selection.Ids);
    }

    [Fact]
    public void CanceledQueuedRasterRequestSkipsItsFactoryAndDoesNotPopulateCache()
    {
        RunOnSta(() =>
        {
            TimelineRasterCache cache = new();
            using CountdownEvent workersStarted = new(1);
            using ManualResetEventSlim releaseWorkers = new(false);
            using CancellationTokenSource cancellation = new();
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            TimelineRasterCacheKey first = RasterKey(1);
            TimelineRasterCacheKey canceled = RasterKey(3);
            int canceledFactoryCalls = 0;

            Assert.True(cache.Request(first, BlockingFactory, dispatcher, static () => { }));
            Assert.True(workersStarted.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(cache.Request(
                canceled,
                () =>
                {
                    Interlocked.Increment(ref canceledFactoryCalls);
                    return EmptyRaster();
                },
                dispatcher,
                static () => { },
                cancellation.Token));

            cancellation.Cancel();
            releaseWorkers.Set();
            for (int attempt = 0; attempt < 1_000 && cache.InFlightCount != 0; attempt++)
            {
                Thread.Sleep(2);
                PumpDispatcher();
            }

            Assert.Equal(0, cache.InFlightCount);
            Assert.Equal(0, Volatile.Read(ref canceledFactoryCalls));
            Assert.False(cache.TryGet(canceled, out _));

            TimelineRasterBuffer BlockingFactory()
            {
                workersStarted.Signal();
                Assert.True(releaseWorkers.Wait(TimeSpan.FromSeconds(10)));
                return EmptyRaster();
            }

            static TimelineRasterBuffer EmptyRaster() => new(1, 1, new byte[4]);
            static TimelineRasterCacheKey RasterKey(long tileX) => new(
                TimelineRasterLayer.PianoNotes,
                "cancellation-test",
                1,
                1,
                1,
                tileX,
                0,
                0,
                0,
                0,
                96,
                96);
        });
    }

    [Fact]
    public void VisibleRasterRequestIsNotBlockedByBackgroundWarmup()
    {
        RunOnSta(() =>
        {
            TimelineRasterCache cache = new();
            using ManualResetEventSlim backgroundStarted = new(false);
            using ManualResetEventSlim releaseBackground = new(false);
            using ManualResetEventSlim visibleStarted = new(false);
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;

            Assert.True(cache.Request(
                RasterKey(1),
                () =>
                {
                    backgroundStarted.Set();
                    Assert.True(releaseBackground.Wait(TimeSpan.FromSeconds(10)));
                    return EmptyRaster();
                },
                dispatcher,
                static () => { },
                priority: TimelineRasterRequestPriority.Background));
            Assert.True(backgroundStarted.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(cache.Request(
                RasterKey(2),
                EmptyRaster,
                dispatcher,
                static () => { },
                priority: TimelineRasterRequestPriority.Background));
            Assert.True(cache.Request(
                RasterKey(3),
                () =>
                {
                    visibleStarted.Set();
                    return EmptyRaster();
                },
                dispatcher,
                static () => { },
                priority: TimelineRasterRequestPriority.Visible));

            Assert.True(
                visibleStarted.Wait(TimeSpan.FromSeconds(2)),
                "A visible raster waited behind the queued background warmup.");
            releaseBackground.Set();
            for (int attempt = 0; attempt < 1_000 && cache.InFlightCount != 0; attempt++)
            {
                Thread.Sleep(2);
                PumpDispatcher();
            }
            Assert.Equal(0, cache.InFlightCount);

            static TimelineRasterBuffer EmptyRaster() => new(1, 1, new byte[4]);
            static TimelineRasterCacheKey RasterKey(long tileX) => new(
                TimelineRasterLayer.PianoNotes,
                "priority-test",
                1,
                1,
                1,
                tileX,
                0,
                0,
                0,
                0,
                96,
                96);
        });
    }

    [Fact]
    public void SpeculativeRasterQueueReservesCapacityForVisibleRequests()
    {
        RunOnSta(() =>
        {
            TimelineRasterCache cache = new();
            using ManualResetEventSlim backgroundStarted = new(false);
            using ManualResetEventSlim releaseBackground = new(false);
            using ManualResetEventSlim visibleStarted = new(false);
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;

            for (int index = 0; index < TimelineRasterCache.MaximumInFlight / 2; index++)
            {
                int requestIndex = index;
                Assert.True(cache.Request(
                    RasterKey(requestIndex),
                    requestIndex == 0
                        ? () =>
                        {
                            backgroundStarted.Set();
                            Assert.True(releaseBackground.Wait(TimeSpan.FromSeconds(10)));
                            return EmptyRaster();
                        }
                        : EmptyRaster,
                    dispatcher,
                    static () => { },
                    priority: TimelineRasterRequestPriority.Background));
            }
            Assert.True(backgroundStarted.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(cache.Request(
                RasterKey(1_000),
                EmptyRaster,
                dispatcher,
                static () => { },
                priority: TimelineRasterRequestPriority.Background));
            Assert.True(cache.Request(
                RasterKey(2_000),
                () =>
                {
                    visibleStarted.Set();
                    return EmptyRaster();
                },
                dispatcher,
                static () => { },
                priority: TimelineRasterRequestPriority.Visible));
            Assert.True(
                visibleStarted.Wait(TimeSpan.FromSeconds(2)),
                "Speculative raster requests consumed capacity reserved for visible work.");

            releaseBackground.Set();
            for (int attempt = 0; attempt < 1_000 && cache.InFlightCount != 0; attempt++)
            {
                Thread.Sleep(2);
                PumpDispatcher();
            }
            Assert.Equal(0, cache.InFlightCount);

            static TimelineRasterBuffer EmptyRaster() => new(1, 1, new byte[4]);
            static TimelineRasterCacheKey RasterKey(long tileX) => new(
                TimelineRasterLayer.PianoNotes,
                "capacity-test",
                1,
                1,
                1,
                tileX,
                0,
                0,
                0,
                0,
                96,
                96);
        });
    }

    [Fact]
    public void PagedPianoFingerprintUsesRangeMetadataWithoutQueryingItems()
    {
        MetadataOnlyRenderSource source = new();
        TimelineRenderSnapshot snapshot = new(
            1,
            "metadata-fingerprint",
            [],
            itemSource: source);

        ulong fingerprint = TimelinePianoTileRasterizer.ComputeContentFingerprint(
            snapshot,
            devicePixelsPerTick: 0.25,
            devicePixelsPerLane: 6,
            tileX: 0,
            tileY: 0);

        Assert.NotEqual(0UL, fingerprint);
        Assert.Equal(1, source.RangeFingerprintCalls);
        Assert.Equal(0, source.QueryCalls);
    }

    private static byte Alpha(TimelineRasterBuffer buffer, int x, int y) =>
        buffer.Pixels[(y * buffer.Width + x) * 4 + 3];

    private sealed class MetadataOnlyRenderSource : ITimelineRenderItemSource
    {
        public int QueryCalls { get; private set; }
        public int RangeFingerprintCalls { get; private set; }
        public long Count => 1_000_000;
        public long MaximumEndTick => 1_000_000;
        public ulong ContentFingerprint => 11;

        public ulong GetRangeFingerprint(
            long startTick,
            long endTick,
            int firstLane,
            int lastLaneExclusive)
        {
            RangeFingerprintCalls++;
            return 17;
        }

        public void QueryInto(
            long startTick,
            long endTick,
            int firstLane,
            int lastLaneExclusive,
            List<TimelineRenderItem> destination)
        {
            QueryCalls++;
            throw new InvalidOperationException("A foreground fingerprint decoded paged content.");
        }

        public bool TryGetById(MidoraId id, out TimelineRenderItem item)
        {
            item = default;
            return false;
        }

        public IEnumerable<TimelineRenderItem> EnumerateAll() => [];
    }

    private static Color PixelColor(TimelineRasterBuffer buffer, int x, int y)
    {
        int offset = (y * buffer.Width + x) * 4;
        return Color.FromArgb(
            buffer.Pixels[offset + 3],
            buffer.Pixels[offset + 2],
            buffer.Pixels[offset + 1],
            buffer.Pixels[offset]);
    }

    private static (int Width, int Height) OpaqueSize(TimelineRasterBuffer buffer)
    {
        int left = buffer.Width;
        int top = buffer.Height;
        int right = -1;
        int bottom = -1;
        for (int y = 0; y < buffer.Height; y++)
        {
            for (int x = 0; x < buffer.Width; x++)
            {
                if (Alpha(buffer, x, y) == 0) continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }
        return right < left || bottom < top
            ? (0, 0)
            : (right - left + 1, bottom - top + 1);
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

    private sealed class TestSegmentPreviewSource : ITimelineSegmentPreviewSource
    {
        public bool HasNoteContent => true;
        public bool HasEventContent => true;
        public ulong NoteContentFingerprint => 1;
        public ulong EventContentFingerprint => 2;

        public void QueryNotes(
            double normalizedStart,
            double normalizedEnd,
            List<TimelineSegmentPreviewNote> destination)
        {
            if (normalizedStart <= 0.25 && normalizedEnd > 0.25)
                destination.Add(new(0.25, 0.5, 60));
        }

        public void QueryEvents(
            double normalizedStart,
            double normalizedEnd,
            List<TimelineSegmentPreviewEvent> destination)
        {
            if (normalizedStart <= 0.25 && normalizedEnd > 0.25)
                destination.Add(new(0.25, 0.75));
        }
    }

    private sealed class RecordingSegmentPreviewSource(
        ulong fingerprint,
        ManualResetEventSlim? firstQueryGate = null) : ITimelineSegmentPreviewSource
    {
        private readonly object _gate = new();
        private readonly List<(double Start, double End)> _queries = [];
        private int _claimedGate;

        public bool HasNoteContent => true;
        public bool HasEventContent => false;
        public ulong NoteContentFingerprint => fingerprint;
        public ulong EventContentFingerprint => 0;

        public int CoarseQueryCount
        {
            get
            {
                lock (_gate)
                    return _queries.Count(static query =>
                    {
                        double span = query.End - query.Start;
                        return span >= 0.2;
                    });
            }
        }

        public bool HasDetailedQuery
        {
            get
            {
                lock (_gate)
                    return _queries.Any(static query =>
                        query.Start < 0.1
                        && query.End - query.Start < 0.1);
            }
        }

        public void QueryNotes(
            double normalizedStart,
            double normalizedEnd,
            List<TimelineSegmentPreviewNote> destination)
        {
            if (firstQueryGate is not null
                && Interlocked.CompareExchange(ref _claimedGate, 1, 0) == 0)
            {
                _ = firstQueryGate.Wait(TimeSpan.FromSeconds(10));
            }
            lock (_gate)
                _queries.Add((normalizedStart, normalizedEnd));
            if (normalizedStart <= 0.05 && normalizedEnd > 0.05)
                destination.Add(new(0.05, 0.06, 60));
        }

        public void QueryEvents(
            double normalizedStart,
            double normalizedEnd,
            List<TimelineSegmentPreviewEvent> destination)
        {
        }
    }

    private static TimelineSurface CreateArrangementSurface(
        TimelineRenderSnapshot snapshot,
        long tickSpan)
    {
        TimelineSurface surface = new()
        {
            SurfaceMode = TimelineSurfaceMode.Arrangement,
            PreviewTicksPerQuarterNote = 768,
            LaneHeight = 60,
            TickSpan = tickSpan,
            GridVisible = false,
            Snapshot = snapshot
        };
        surface.Measure(new Size(800, 260));
        surface.Arrange(new Rect(0, 0, 800, 260));
        return surface;
    }

    private static void PumpDispatcher()
    {
        DispatcherFrame frame = new();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("The WPF timeline test did not complete.");
        }
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static byte[] RenderVisual(Visual visual)
    {
        visual.Dispatcher.Invoke(
            DispatcherPriority.Render,
            new Action(() => { }));
        RenderTargetBitmap target = new(800, 260, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        byte[] pixels = new byte[800 * 260 * 4];
        target.CopyPixels(pixels, 800 * 4, 0);
        return pixels;
    }
}
