using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using System.Windows.Media;
using Xunit;

namespace Midora.Desktop.Tests;

public sealed class PagedLogicalPresentationTests
{
    [Fact]
    public void LogicalPianoSourceQueriesOnlyVisibleNotePages()
    {
        using MidoraProject project = new(192);
        Segment segment = new(project)
        {
            LengthTicks = 100_000,
            ContentOffsetTick = 0
        };
        segment.Notes.AddRange(Enumerable.Range(0, 20_000).Select(index => new LogicalNote(project)
        {
            StartTick = index * 4L,
            LengthTicks = 2,
            Note = index % 128,
            Velocity = 100
        }));
        TimelineRenderSnapshot snapshot = new(
            1,
            "logical-paged-query",
            [],
            itemSource: new PagedLogicalNoteTimelineItemSource(
                segment,
                LogicalNoteTimelineProjection.Notes));
        List<TimelineRenderItem> values = [];

        snapshot.QueryInto(10_000, 10_100, 0, 128, values);

        Assert.All(values, value =>
        {
            Assert.True(value.StartTick < 10_100);
            Assert.True(value.EndTick > 10_000);
        });
        Assert.InRange(values.Count, 20, 30);
    }

    [Fact]
    public void LogicalArrangementPreviewFingerprintIsLocalToTheTile()
    {
        using MidoraProject project = new(192);
        Segment segment = new(project)
        {
            LengthTicks = 20_000,
            ContentOffsetTick = 0
        };
        LogicalNote near = new(project)
        {
            StartTick = 100,
            LengthTicks = 100,
            Note = 60,
            Velocity = 100
        };
        LogicalNote far = new(project)
        {
            StartTick = 12_000,
            LengthTicks = 100,
            Note = 64,
            Velocity = 100
        };
        segment.Notes.AddRange([near, far]);
        const double width = 2_000;
        LogicalSegmentPreviewSource first = Preview(segment);
        ulong tile0 = first.GetTileContentFingerprint(false, width, 0);

        far.Note = 72;
        ulong afterFarEdit = Preview(segment).GetTileContentFingerprint(false, width, 0);
        near.Note = 61;
        ulong afterNearEdit = Preview(segment).GetTileContentFingerprint(false, width, 0);

        Assert.Equal(tile0, afterFarEdit);
        Assert.NotEqual(tile0, afterNearEdit);

        static LogicalSegmentPreviewSource Preview(Segment value) => new(
            value,
            value.Notes.CreateQuerySnapshot(),
            []);
    }

    [Fact]
    public void PianoSelectionFingerprintIgnoresSelectionsOutsideTheTile()
    {
        using MidoraProject project = new(192);
        Segment segment = new(project) { LengthTicks = 20_000 };
        LogicalNote near = new(project)
        {
            StartTick = 100,
            LengthTicks = 100,
            Note = 60,
            Velocity = 100
        };
        LogicalNote far = new(project)
        {
            StartTick = 12_000,
            LengthTicks = 100,
            Note = 64,
            Velocity = 100
        };
        segment.Notes.AddRange([near, far]);
        TimelineRenderSnapshot snapshot = new(
            1,
            "logical-local-selection",
            [],
            itemSource: new PagedLogicalNoteTimelineItemSource(
                segment,
                LogicalNoteTimelineProjection.Notes));
        TimelineSelectionSnapshot none = new(1, [], null, []);
        TimelineSelectionSnapshot farOnly = new(
            2,
            [far.Id],
            far.Id,
            [new TimelineRenderItem(
                far.Id,
                TimelineItemKind.LogicalNote,
                far.StartTick,
                far.StartTick + far.LengthTicks,
                127 - far.Note,
                far.Velocity,
                1,
                TimelineItemState.Selected)]);

        ulong first = TimelinePianoTileRasterizer.ComputeSelectionFingerprint(
            snapshot,
            none,
            devicePixelsPerTick: 0.1,
            devicePixelsPerLane: 10,
            tileX: 0,
            tileY: 2);
        ulong second = TimelinePianoTileRasterizer.ComputeSelectionFingerprint(
            snapshot,
            farOnly,
            devicePixelsPerTick: 0.1,
            devicePixelsPerLane: 10,
            tileX: 0,
            tileY: 2);

        Assert.Equal(first, second);
    }

    [Fact]
    public void SubVoiceNoteAndVelocitySourcesShareOneImmutablePagedSnapshot()
    {
        using MidoraProject project = new(192);
        SubVoice voice = new(project);
        voice.Events.AddRange(Enumerable.Range(0, 10_000).Select(index => new TemplateEvent(project)
        {
            Kind = TemplateEventKind.Note,
            Tick = index * 2L,
            LengthTicks = 2,
            Number = index % 128,
            Value = 100
        }));
        TemplateEventQuerySnapshot values = voice.Events.CreateQuerySnapshot();
        TimelineRenderSnapshot notes = new(
            1,
            "subvoice-notes",
            [],
            itemSource: new PagedTemplateNoteTimelineItemSource(
                voice,
                TemplateNoteTimelineProjection.Notes,
                snapshot: values));
        TimelineRenderSnapshot velocities = new(
            1,
            "subvoice-velocities",
            [],
            itemSource: new PagedTemplateNoteTimelineItemSource(
                voice,
                TemplateNoteTimelineProjection.Velocities,
                snapshot: values));
        List<TimelineRenderItem> noteItems = [];
        List<TimelineRenderItem> velocityItems = [];

        notes.QueryInto(5_000, 5_100, 0, 128, noteItems);
        velocities.QueryInto(5_000, 5_100, 0, 1, velocityItems);

        Assert.Equal(50, noteItems.Count);
        Assert.Equal(50, velocityItems.Count);
        Assert.All(noteItems, value => Assert.Equal(TimelineItemKind.TemplateNote, value.Kind));
        Assert.All(velocityItems, value => Assert.Equal(TimelineItemKind.Velocity, value.Kind));
    }
}
