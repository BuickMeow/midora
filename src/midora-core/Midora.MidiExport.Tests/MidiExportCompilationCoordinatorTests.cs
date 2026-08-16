using System.Buffers.Binary;
using Midora.Compiler;
using Midora.Domain;

namespace Midora.MidiExport.Tests;

public sealed class MidiExportCompilationCoordinatorTests
{
    [Fact]
    public void FreezesSelectedTracksLayoutsAndActuallyUsedPortsFromOneExportCompilation()
    {
        MidoraProject project = new(192);
        EventInstrument instrument = CreateInstrument(project);
        project.EventInstruments.Add(instrument);
        LogicalTrack selected = CreateTrack(project, instrument, "", note: 60);
        LogicalTrack notSelected = CreateTrack(project, instrument, "Other", note: 62);
        LogicalTrack unbound = new(project) { Name = "Unbound" };
        Segment unboundSegment = new(project) { LengthTicks = 192 };
        unboundSegment.Notes.Add(new(project) { LengthTicks = 96, Note = 64, Velocity = 100 });
        unbound.Segments.Add(unboundSegment);
        project.Tracks.Add(selected);
        project.Tracks.Add(notSelected);
        project.Tracks.Add(unbound);
        using MidoraCompiler compiler = new();
        MidiExportCompilationCoordinator coordinator = new(compiler);

        MidiExportCompilationResult result = coordinator.Compile(new()
        {
            Project = project,
            Mode = MidiExportMode.PerLogicalTrack,
            Routing = MidiExportRoutingStrategy.Compact,
            StartTick = 0,
            EndTick = 192,
            SelectedTrackIds = new HashSet<MidoraId> { selected.Id, unbound.Id }
        });

        Assert.True(result.Succeeded);
        Assert.Equal(CompilationPurpose.MidiExport, result.CompiledResult.Purpose);
        MidiExportLogicalTrackLayout layout = Assert.Single(result.Layouts);
        Assert.Equal(selected.Id, layout.TrackId);
        KeyValuePair<MidiExportChannelUnit, string> unitTrack = Assert.Single(layout.EventTrackNamesByUnit);
        Assert.Equal(new MidiExportChannelUnit(0, 0), unitTrack.Key);
        Assert.Equal("Port 1 / Channel 1", unitTrack.Value);
        Assert.Equal(new byte[] { 0 }, result.UsedZeroBasedPorts);
        Assert.Collection(
            result.Tracks,
            track => Assert.True(track.Participates),
            track => Assert.Equal("Not selected", track.ExclusionReason),
            track => Assert.Equal("No valid Event Instrument binding", track.ExclusionReason));
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "MIDORA1304" && diagnostic.Severity == DiagnosticSeverity.Info);
    }

    [Fact]
    public void InvalidSelectedTrackIdFailsCanonicalCompilationWithoutProducingLayoutsForIt()
    {
        MidoraProject project = new(192);
        using MidoraCompiler compiler = new();
        MidiExportCompilationResult result = new MidiExportCompilationCoordinator(compiler).Compile(new()
        {
            Project = project,
            Mode = MidiExportMode.WholeProject,
            Routing = MidiExportRoutingStrategy.Preserve,
            SelectedTrackIds = new HashSet<MidoraId> { MidoraId.FromSequence(999) }
        });

        Assert.False(result.Succeeded);
        Assert.Empty(result.Layouts);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "MIDORA1302");
    }

    [Fact]
    public void WholeProjectMergesAChannelUnitReusedBySequentialLogicalTracks()
    {
        MidoraProject project = new(192);
        EventInstrument instrument = CreateInstrument(project);
        project.EventInstruments.Add(instrument);
        LogicalTrack first = CreateTrack(project, instrument, "First", note: 60, segmentStartTick: 0);
        LogicalTrack second = CreateTrack(project, instrument, "Second", note: 62, segmentStartTick: 192);
        project.Tracks.Add(first);
        project.Tracks.Add(second);
        using MidoraCompiler compiler = new();
        MidiExportCompilationResult compilation = new MidiExportCompilationCoordinator(compiler).Compile(new()
        {
            Project = project,
            Mode = MidiExportMode.WholeProject,
            Routing = MidiExportRoutingStrategy.Compact,
            StartTick = 0,
            EndTick = 384
        });

        Assert.True(compilation.Succeeded);
        Assert.Equal(2, compilation.Layouts.Count);
        Assert.All(compilation.Layouts, layout =>
            Assert.Equal(new MidiExportChannelUnit(0, 0), Assert.Single(layout.EventTrackNamesByUnit).Key));

        MidiExportEncodingResult encoded = CanonicalMidiFileExporter.EncodeWholeProject(new()
        {
            CompiledResult = compilation.CompiledResult,
            ConductorTrackName = "Conductor",
            LogicalTracks = compilation.Layouts
        });

        Assert.True(encoded.Succeeded);
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16BigEndian(encoded.FileBytes.AsSpan(10, 2)));
    }

    private static EventInstrument CreateInstrument(MidoraProject project)
    {
        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            RootNote = 60,
            TemplateLengthTicks = 192,
            OverlapPolicy = OverlapPolicy.Warn
        };
        SubVoice voice = new(project) { Name = "Voice" };
        voice.Events.Add(TemplateEvent.Note(project, 0, 96, 60, 100));
        instrument.SubVoices.Add(voice);
        return instrument;
    }

    private static LogicalTrack CreateTrack(
        MidoraProject project,
        EventInstrument instrument,
        string name,
        byte note,
        long segmentStartTick = 0)
    {
        LogicalTrack track = new(project) { Name = name, EventInstrumentId = instrument.Id };
        Segment segment = new(project) { ProjectStartTick = segmentStartTick, LengthTicks = 192 };
        segment.Notes.Add(new(project)
        {
            LengthTicks = 192,
            Note = note,
            Velocity = 100
        });
        track.Segments.Add(segment);
        return track;
    }
}
