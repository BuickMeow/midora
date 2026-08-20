using Midora.Application;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using System.Windows.Media;
using Xunit;

namespace Midora.Desktop.Tests;

public sealed class PureMidiPagedPresentationTests
{
    [Fact]
    public void DirectMidiOverviewDensityIncludesExternalTimelineSource()
    {
        using MidoraProject project = new(480);
        MidiSegment segment = new(project)
        {
            LengthTicks = 1_000
        };
        segment.Notes.Add(new DirectMidiNote(project)
        {
            StartTick = 100,
            LengthTicks = 40,
            Key = 60
        });
        segment.Notes.Add(new DirectMidiNote(project)
        {
            StartTick = 800,
            LengthTicks = 40,
            Key = 64
        });
        TimelineRenderSnapshot snapshot = new(
            1,
            "direct-midi-overview",
            [],
            itemSource: new PagedDirectMidiTimelineItemSource(
                segment,
                DirectMidiTimelineProjection.Notes));
        int[] density = new int[10];

        snapshot.AccumulateOverviewDensity(1_000, density);

        Assert.True(density[1] > 0);
        Assert.True(density[8] > 0);
    }

    [Fact]
    public void DirectMidiOverviewDensityMarksNoteStartsWithoutFillingLongGates()
    {
        using MidoraProject project = new(480);
        MidiSegment segment = new(project)
        {
            LengthTicks = 1_000
        };
        segment.Notes.Add(new DirectMidiNote(project)
        {
            StartTick = 100,
            LengthTicks = 700,
            Key = 60
        });
        TimelineRenderSnapshot snapshot = new(
            1,
            "direct-midi-long-gate-overview",
            [],
            itemSource: new PagedDirectMidiTimelineItemSource(
                segment,
                DirectMidiTimelineProjection.Notes));
        int[] density = new int[10];

        snapshot.AccumulateOverviewDensity(1_000, density);

        Assert.True(density[1] > 0);
        Assert.All(density.Skip(2), value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task OptInImportedSampleProvidesArrangementAndEditorItemsFromPagedContent()
    {
        string? path = Environment.GetEnvironmentVariable("MIDORA_UI_SAMPLE_MIDI_PATH");
        if (string.IsNullOrWhiteSpace(path)) return;

        MidiProjectImportResult imported = MidiProjectImportService.ImportFile(
            Path.GetFullPath(path),
            Path.GetFileNameWithoutExtension(path));
        try
        {
            MidiSegment segment = imported.Project.PureMidiTracks
                .SelectMany(track => track.Segments)
                .First(candidate => candidate.Notes.Count != 0);
            DirectMidiNoteValue first = segment.Notes.QueryValues(
                    segment.ContentOffsetTick,
                    segment.ContentEndTick)
                .First();

            PagedMidiSegmentPreviewSource previewSource = new(segment);
            Assert.True(previewSource.HasNoteContent);
            List<TimelineSegmentPreviewNote> previews = [];
            previewSource.QueryNotes(0, 1, previews);
            Assert.NotEmpty(previews);

            TimelineSegmentPreview arrangementPreview = new(segment.Id, previewSource);
            Assert.True(arrangementPreview.HasNoteContent);
            const double arrangementWidth = 512;
            long arrangementTileX = (long)Math.Floor(
                previews[0].NormalizedStart * arrangementWidth
                / TimelineSegmentPreviewRasterizer.TileSize);
            TimelineRasterBuffer arrangementRaster = TimelineSegmentPreviewRasterizer.RasterizeNoteTile(
                arrangementPreview,
                deviceSegmentWidth: arrangementWidth,
                deviceHeight: 64,
                tileX: arrangementTileX,
                Colors.LightGray);
            Assert.True(arrangementRaster.CandidateCount > 0);
            Assert.Contains(arrangementRaster.Pixels, static value => value != 0);

            TimelineWorkspaceViewModel workspace = new(
                WorkspaceKey.ForObject(WorkspaceKind.SegmentEditor, segment.Id),
                "MIDI Segment",
                TimelineWorkspaceMode.Segment);
            workspace.Rebuild(imported.Project, revision: 1);
            List<TimelineRenderItem> items = [];
            int lane = 127 - first.Key;
            workspace.Snapshot!.QueryInto(
                first.StartTick,
                checked(first.StartTick + Math.Max(1, first.LengthTicks)),
                lane,
                lane + 1,
                items);

            Assert.Contains(items, value => value.Id == first.Id);

            const double pixelsPerTick = 1;
            const double pixelsPerLane = 18;
            long tileX = (long)Math.Floor(
                first.StartTick * pixelsPerTick / TimelinePianoTileRasterizer.TileSize);
            long tileY = (long)Math.Floor(
                lane * pixelsPerLane / TimelinePianoTileRasterizer.TileSize);
            TimelineRasterBuffer pianoRaster = await Task.Run(() =>
                TimelinePianoTileRasterizer.Rasterize(
                    workspace.Snapshot,
                    pixelsPerTick,
                    pixelsPerLane,
                    tileX,
                    tileY,
                    Colors.LightGray,
                    Colors.Orange));
            Assert.True(pianoRaster.CandidateCount > 0);
            Assert.Contains(pianoRaster.Pixels, static value => value != 0);
        }
        finally
        {
            imported.Project.Dispose();
        }
    }
}
