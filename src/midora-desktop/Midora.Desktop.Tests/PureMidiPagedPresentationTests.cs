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
    public void DirectMidiOverviewDensityDoesNotFillGapsInsidePagedRangeSummary()
    {
        using MidoraProject project = new(480);
        MidiSegment segment = new(project) { LengthTicks = 1_000 };
        SparsePagedNoteSource source = new();
        segment.AttachPagedContent(source);
        TimelineRenderSnapshot snapshot = new(
            1,
            "direct-midi-sparse-paged-overview",
            [],
            itemSource: new PagedDirectMidiTimelineItemSource(
                segment,
                DirectMidiTimelineProjection.Notes));
        int[] density = new int[10];

        snapshot.AccumulateOverviewDensity(1_000, density);

        Assert.True(density[1] > 0);
        Assert.True(density[8] > 0);
        Assert.All(
            density.Where((_, index) => index is not 1 and not 8),
            value => Assert.Equal(0, value));
        Assert.Equal(1, source.NoteStartQueryCount);
    }

    [Fact]
    public void DirectMidiOverviewChannelsSeparateNoteStartsFromEvents()
    {
        using MidoraProject project = new(480);
        MidiSegment segment = new(project) { LengthTicks = 1_000 };
        segment.Notes.Add(new DirectMidiNote(project)
        {
            StartTick = 100,
            LengthTicks = 40,
            Key = 60
        });
        segment.ChannelEvents.Add(new DirectMidiChannelEvent(project)
        {
            Tick = 300,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 1,
            Data2 = 64
        });
        segment.OpaqueEvents.Add(new OpaqueMidiEvent(project)
        {
            Tick = 700,
            Kind = OpaqueMidiEventKind.SystemExclusive,
            Payload = [0x7d]
        });
        segment.ChannelEvents.Add(new DirectMidiChannelEvent(project)
        {
            Tick = 400,
            Kind = DirectMidiChannelEventKind.NoteOn,
            Data1 = 67,
            Data2 = 90
        });
        segment.ChannelEvents.Add(new DirectMidiChannelEvent(project)
        {
            Tick = 500,
            Kind = DirectMidiChannelEventKind.NoteOff,
            Data1 = 67,
            Data2 = 0
        });
        TimelineRenderSnapshot snapshot = new(
            1,
            "direct-midi-two-channel-overview",
            [],
            overviewSource: new PureMidiSegmentOverviewSource(segment));
        byte[] notes = new byte[10];
        byte[] events = new byte[10];

        snapshot.AccumulateOverviewChannels(1_000, notes, events);

        Assert.Equal(1, notes[1]);
        Assert.Equal(1, notes[4]);
        Assert.Equal(1, events[3]);
        Assert.Equal(1, events[7]);
        Assert.Equal(0, events[4]);
        Assert.Equal(0, events[5]);
        Assert.Equal(0, notes[3]);
        Assert.Equal(0, events[1]);
    }

    [Fact]
    public void DirectMidiDedicatedOverviewUsesRealTicksInsteadOfFillingPagedGaps()
    {
        using MidoraProject project = new(480);
        MidiSegment segment = new(project) { LengthTicks = 1_000 };
        SparsePagedNoteSource source = new();
        segment.AttachPagedContent(source);
        TimelineRenderSnapshot snapshot = new(
            1,
            "direct-midi-exact-paged-overview",
            [],
            overviewSource: new PureMidiSegmentOverviewSource(segment));
        byte[] notes = new byte[10];
        byte[] events = new byte[10];

        snapshot.AccumulateOverviewChannels(1_000, notes, events);

        Assert.Equal(1, notes[1]);
        Assert.Equal(1, notes[8]);
        Assert.All(
            notes.Where((_, index) => index is not 1 and not 8),
            value => Assert.Equal(0, value));
        Assert.All(events, value => Assert.Equal(0, value));
        Assert.Equal(1, source.NoteStartQueryCount);

        DirectMidiNote moved = segment.Notes[0];
        moved.StartTick = 600;
        Array.Clear(notes);
        TimelineRenderSnapshot editedSnapshot = new(
            2,
            "direct-midi-exact-paged-overview-edited",
            [],
            overviewSource: new PureMidiSegmentOverviewSource(segment));

        editedSnapshot.AccumulateOverviewChannels(1_000, notes, events);

        Assert.Equal(0, notes[1]);
        Assert.Equal(1, notes[6]);
        Assert.Equal(1, notes[8]);
        Assert.Equal(2, source.NoteStartQueryCount);
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

    private sealed class SparsePagedNoteSource :
        IPureMidiSegmentContentSource,
        IPureMidiPlaybackEndpointSource,
        IPureMidiContentOverviewSource
    {
        private readonly DirectMidiNoteValue[] _notes =
        [
            new(new MidoraId(1), 100, 40, 60, 100, 0, 0, 1),
            new(new MidoraId(2), 800, 40, 64, 100, 0, 2, 3)
        ];

        public int NoteCount => _notes.Length;
        public int ChannelEventCount => 0;
        public int OpaqueEventCount => 0;
        public string ContentFingerprint => "sparse-overview-test";
        public int NoteStartQueryCount { get; private set; }
        public DirectMidiNoteValue GetNote(int index) => _notes[index];
        public DirectMidiChannelEventValue GetChannelEvent(int index) =>
            throw new ArgumentOutOfRangeException(nameof(index));
        public OpaqueMidiEventValue GetOpaqueEvent(int index) =>
            throw new ArgumentOutOfRangeException(nameof(index));
        public int FindNoteIndex(MidoraId id) => Array.FindIndex(_notes, value => value.Id == id);
        public int FindChannelEventIndex(MidoraId id) => -1;
        public int FindOpaqueEventIndex(MidoraId id) => -1;

        public IEnumerable<DirectMidiNoteValue> QueryNotes(
            long startTick,
            long endTick,
            int minimumKey = 0,
            int maximumKey = 127) => _notes.Where(value =>
                value.StartTick < endTick
                && value.StartTick + value.LengthTicks > startTick
                && value.Key >= minimumKey
                && value.Key <= maximumKey);

        public IEnumerable<DirectMidiChannelEventValue> QueryChannelEvents(long startTick, long endTick) => [];
        public IEnumerable<OpaqueMidiEventValue> QueryOpaqueEvents(long startTick, long endTick) => [];
        public IEnumerable<DirectMidiNoteValue> QueryNoteStarts(long startTick, long endTick)
        {
            NoteStartQueryCount++;
            return _notes.Where(value => value.StartTick >= startTick && value.StartTick < endTick);
        }
        public IEnumerable<DirectMidiNoteValue> QueryNoteEnds(long startTick, long endTick) =>
            _notes.Where(value => value.StartTick + value.LengthTicks >= startTick
                && value.StartTick + value.LengthTicks < endTick);
        public IEnumerable<DirectMidiNoteValue> QueryActiveNotes(long tick) =>
            _notes.Where(value => value.StartTick < tick && value.StartTick + value.LengthTicks > tick);
        public IEnumerable<DirectMidiChannelEventValue> QueryOrderedChannelEvents(
            long startTick,
            long endTick) => [];
        public IEnumerable<PureMidiContentRangeSummary> GetNoteRangeSummaries() =>
            [new(100, 800, _notes.Length)];
    }
}
