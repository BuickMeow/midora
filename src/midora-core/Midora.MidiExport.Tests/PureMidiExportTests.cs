using System.Text;
using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;

namespace Midora.MidiExport.Tests;

public sealed class PureMidiExportTests
{
    [Fact]
    public void PagedPureMidiExportStreamsChannelAndOpaqueEventsWithoutDroppingThem()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "midora-paged-midi-export-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            MidoraProject project = new(192);
            MidiChannelRoot root = new(project)
            {
                Name = "Paged Root",
                RoutingMode = MidiChannelRootRoutingMode.Fixed,
                FixedZeroBasedPort = 1,
                FixedZeroBasedChannel = 2
            };
            PureMidiTrack track = AddTrack(project, root, "Paged Track", 384);
            project.MidiChannelRoots.Add(root);
            project.ArrangementParents.Add(new(ArrangementParentKind.MidiChannelRoot, root.Id));
            MidiSegment segment = track.Segments[0];
            string packPath = Path.Combine(directory, "track.mpk");
            using PureMidiContentPackWriter writer = new(packPath);
            writer.AddNote(segment.Id, new(
                project.AllocateStableId(), 0, 96, 60, 100, 37, 10, 11));
            writer.AddChannelEvent(segment.Id, new(
                project.AllocateStableId(), 192, DirectMidiChannelEventKind.ControlChange,
                93, 81, 20));
            writer.AddOpaqueEvent(segment.Id, new(
                project.AllocateStableId(), 240, OpaqueMidiEventKind.Meta,
                StandardMidiFile.TextMetaType, "paged"u8.ToArray(), 30));
            using PureMidiContentPack pack = writer.Complete();
            segment.AttachPagedContent(pack.GetSegmentSource(segment.Id));

            using MidoraCompiler compiler = new();
            CanonicalCompiledResult compiled = compiler.CompileFull(
                project,
                new CompilationRequest { Purpose = CompilationPurpose.MidiExport });
            MidiExportEncodingResult encoded = CanonicalMidiFileExporter.EncodeWholeProject(new()
            {
                CompiledResult = compiled,
                ConductorTrackName = "Paged",
                LogicalTracks = []
            });

            Assert.True(encoded.Succeeded, string.Join(Environment.NewLine, encoded.Diagnostics));
            ParsedStandardMidiFile parsed = StandardMidiFile.ParseType0Or1(encoded.FileBytes);
            ParsedStandardMidiFileTrack midiTrack = parsed.Tracks[1];
            Assert.Contains(midiTrack.Events, value =>
                value.Kind == StandardMidiFileEventKind.ChannelVoice
                && value.Message.MessageType == MidiMessageType.NoteOn
                && value.Message.Byte1 == 60);
            Assert.Contains(midiTrack.Events, value =>
                value.Kind == StandardMidiFileEventKind.ChannelVoice
                && value.Message.MessageType == MidiMessageType.NoteOff
                && value.Message.Byte2 == 37);
            Assert.Contains(midiTrack.Events, value =>
                value.Kind == StandardMidiFileEventKind.ChannelVoice
                && value.Message.MessageType == MidiMessageType.ControlChange
                && value.Message.Byte1 == 93
                && value.Message.Byte2 == 81);
            Assert.Contains(midiTrack.Events, value =>
                value.Kind == StandardMidiFileEventKind.Meta
                && value.Type == StandardMidiFile.TextMetaType
                && value.Data.Span.SequenceEqual("paged"u8));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WholeProjectPreservesPureTrackTopologyNamesEndsAndFullChannelVoiceData()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = new(project)
        {
            Name = "Shared Root",
            RoutingMode = MidiChannelRootRoutingMode.Fixed,
            FixedZeroBasedPort = 2,
            FixedZeroBasedChannel = 4,
            ChannelMode = MidiChannelMode.Melodic
        };
        project.MidiChannelRoots.Add(root);
        project.ArrangementParents.Add(new(ArrangementParentKind.MidiChannelRoot, root.Id));
        PureMidiTrack shortTrack = AddTrack(project, root, "Short", 240);
        PureMidiTrack longTrack = AddTrack(project, root, "Long", 960);
        MidiSegment shortSegment = shortTrack.Segments[0];
        shortSegment.Notes.Add(new DirectMidiNote(project)
        {
            StartTick = 0,
            LengthTicks = 120,
            Key = 60,
            NoteOnVelocity = 100,
            NoteOffVelocity = 44,
            NoteOnOrder = 1,
            NoteOffOrder = 2
        });
        longTrack.Segments[0].ChannelEvents.Add(new DirectMidiChannelEvent(project)
        {
            Tick = 100,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 93,
            Data2 = 80,
            Order = 1
        });

        CanonicalCompiledResult compiled = new MidoraCompiler().CompileFull(
            project,
            new CompilationRequest { Purpose = CompilationPurpose.MidiExport });
        MidiExportEncodingResult encoded = CanonicalMidiFileExporter.EncodeWholeProject(new()
        {
            CompiledResult = compiled,
            ConductorTrackName = "Project",
            LogicalTracks = []
        });

        Assert.True(encoded.Succeeded, string.Join(Environment.NewLine, encoded.Diagnostics));
        ParsedStandardMidiFile parsed = StandardMidiFile.ParseType0Or1(encoded.FileBytes);
        Assert.Equal(3, parsed.Tracks.Count);
        Assert.Equal([960L, 240L, 960L], parsed.Tracks.Select(value => value.EndTick).ToArray());
        Assert.Equal("Short", TrackName(parsed.Tracks[1]));
        Assert.Equal("Long", TrackName(parsed.Tracks[2]));
        ParsedStandardMidiFileEvent noteOff = Assert.Single(parsed.Tracks[1].Events,
            value => value.Kind == StandardMidiFileEventKind.ChannelVoice
                && value.Message.MessageType == MidiMessageType.NoteOff);
        Assert.Equal((byte)44, noteOff.Message.Byte2);
        Assert.Contains(parsed.Tracks[2].Events, value =>
            value.Kind == StandardMidiFileEventKind.ChannelVoice
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 93
            && value.Message.Byte2 == 80);
        Assert.All(parsed.Tracks.Skip(1), track => Assert.Contains(track.Events, value =>
            value.Kind == StandardMidiFileEventKind.Meta
            && value.Type == 0x7f
            && value.Data.Span.StartsWith("MIDORA"u8)));
    }

    [Fact]
    public void DirectNoteEndingAtProjectBoundaryPreservesNoteOffVelocity()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = new(project)
        {
            Name = "Boundary Root",
            RoutingMode = MidiChannelRootRoutingMode.Fixed,
            FixedZeroBasedPort = 0,
            FixedZeroBasedChannel = 0
        };
        project.MidiChannelRoots.Add(root);
        project.ArrangementParents.Add(new(ArrangementParentKind.MidiChannelRoot, root.Id));
        PureMidiTrack track = AddTrack(project, root, "Boundary", 120);
        track.Segments[0].Notes.Add(new(project)
        {
            StartTick = 0,
            LengthTicks = 120,
            Key = 60,
            NoteOnVelocity = 100,
            NoteOffVelocity = 47
        });

        CanonicalCompiledResult compiled = new MidoraCompiler().CompileFull(
            project,
            new CompilationRequest { Purpose = CompilationPurpose.MidiExport });
        MidiExportEncodingResult encoded = CanonicalMidiFileExporter.EncodeWholeProject(new()
        {
            CompiledResult = compiled,
            ConductorTrackName = "Boundary",
            LogicalTracks = []
        });

        Assert.True(encoded.Succeeded, string.Join(Environment.NewLine, encoded.Diagnostics));
        ParsedStandardMidiFile parsed = StandardMidiFile.ParseType0Or1(encoded.FileBytes);
        ParsedStandardMidiFileEvent noteOff = Assert.Single(parsed.Tracks[1].Events,
            value => value.Kind == StandardMidiFileEventKind.ChannelVoice
                && value.Message.MessageType == MidiMessageType.NoteOff);
        Assert.Equal((byte)47, noteOff.Message.Byte2);
    }

    private static PureMidiTrack AddTrack(
        MidoraProject project,
        MidiChannelRoot root,
        string name,
        long length)
    {
        PureMidiTrack track = new(project)
        {
            Name = name,
            MidiChannelRootId = root.Id
        };
        track.Segments.Add(new(project) { LengthTicks = length });
        project.PureMidiTracks.Add(track);
        root.MidiTrackIds.Add(track.Id);
        return track;
    }

    private static string TrackName(ParsedStandardMidiFileTrack track) =>
        Encoding.UTF8.GetString(Assert.Single(track.Events, value =>
            value.Kind == StandardMidiFileEventKind.Meta
            && value.Type == StandardMidiFile.TrackNameMetaType).Data.Span);
}
