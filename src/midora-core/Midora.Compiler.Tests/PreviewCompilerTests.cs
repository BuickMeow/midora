using Midora.Domain;
using Midora.Midi;

namespace Midora.Compiler.Tests;

public sealed class PreviewCompilerTests
{
    [Fact]
    public void EventInstrumentPreviewUsesTemporaryContextAndCursorTempo()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 960);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 240, 60, 100));
        fixture.Project.Conductor.Tempos.Add(new TempoChange(fixture.Project, 480, 90m));
        int trackCount = fixture.Project.Tracks.Count;
        int segmentCount = fixture.Track.Segments.Count;

        CanonicalCompiledResult result = new PreviewCompiler().CompileEventInstrument(
            fixture.Project,
            new EventInstrumentPreviewRequest(
                fixture.Instrument.Id,
                Pitch: 67,
                Velocity: 111,
                GateLengthTicks: 360,
                CursorTick: 600));

        Assert.True(result.IsConsumable);
        Assert.False(result.IsPartial);
        Assert.Equal(CompilationPurpose.EventInstrumentPreview, result.Purpose);
        Assert.Equal(90m, result.Conductor.Tempos[0].BeatsPerMinute);
        CanonicalMidiEvent noteOn = Assert.Single(result.Events.ToArray(), value => value.Role == CanonicalEventRole.NoteOn);
        Assert.Equal(67, noteOn.Message.Byte1);
        Assert.Equal(100, noteOn.Message.Byte2); // Template velocity remains the mapping target value.
        Assert.Equal(trackCount, fixture.Project.Tracks.Count);
        Assert.Equal(segmentCount, fixture.Track.Segments.Count);
        Assert.Empty(fixture.Segment.Notes);
    }

    [Fact]
    public void SubVoicePreviewKeepsInstrumentContextButFiltersOutput()
    {
        var fixture = CompilerTestProject.Create(subVoiceCount: 2);
        fixture.Instrument.SubVoices[0].Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        fixture.Instrument.SubVoices[1].Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 72, 90));
        SubVoice selected = fixture.Instrument.SubVoices[1];

        CanonicalCompiledResult result = new PreviewCompiler().CompileEventInstrument(
            fixture.Project,
            new EventInstrumentPreviewRequest(fixture.Instrument.Id, selected.Id));

        Assert.True(result.IsConsumable);
        CanonicalMidiEvent note = Assert.Single(result.Events.ToArray(), value => value.Role == CanonicalEventRole.NoteOn);
        Assert.Equal(72, note.Message.Byte1);
        Assert.Single(result.Allocations.ToArray());
        Assert.Equal(selected.Id, result.Allocations[0].SubVoiceId);
    }

    [Fact]
    public void SegmentPreviewUsesOnlySelectedSegmentAndProjectConductorRange()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 480);
        fixture.Segment.ProjectStartTick = 480;
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 120, 64);
        Segment other = new(fixture.Project) { ProjectStartTick = 1_200, LengthTicks = 480 };
        CompilerTestProject.RegisterSegment(fixture.Project, other);
        CompilerTestProject.AddNote(other, fixture.Instrument, 0, 120, 72);
        fixture.Track.Segments.Add(other);
        fixture.Project.Conductor.Tempos.Add(new TempoChange(fixture.Project, 240, 90m));
        fixture.Project.Conductor.Tempos.Add(new TempoChange(fixture.Project, 720, 140m));

        CanonicalCompiledResult result = new PreviewCompiler().CompileSegment(
            fixture.Project, fixture.Track.Id, fixture.Segment.Id);

        Assert.True(result.IsConsumable);
        Assert.Equal(CompilationPurpose.SegmentPreview, result.Purpose);
        Assert.Equal(480, result.StartTick);
        Assert.Equal(960, result.EndTick);
        Assert.Equal(90m, result.Conductor.Tempos[0].BeatsPerMinute);
        Assert.True(result.Conductor.Tempos[0].IsRangeRestore);
        Assert.Contains(result.Conductor.Tempos.ToArray(), value => value.Tick == 720 && value.BeatsPerMinute == 140m);
        CanonicalMidiEvent note = Assert.Single(result.Events.ToArray(), value => value.Message.MessageType == MidiMessageType.NoteOn);
        Assert.Equal(64, note.Message.Byte1);
        Assert.DoesNotContain(result.Events.ToArray(), value => value.Message.MessageType == MidiMessageType.NoteOn && value.Message.Byte1 == 72);
    }

    [Fact]
    public void SegmentPreviewPreservesDamagedInstrumentBindingError()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 480);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 120, 64);
        _ = fixture.Project.EventInstruments.Remove(fixture.Instrument);
        fixture.Project.DamagedEventInstruments.Add(new(
            fixture.Instrument.Id,
            fixture.Instrument.Name,
            "event-instruments/damaged.pb",
            "Invalid protobuf",
            0));

        CanonicalCompiledResult result = new PreviewCompiler().CompileSegment(
            fixture.Project,
            fixture.Track.Id,
            fixture.Segment.Id);

        Assert.False(result.IsConsumable);
        Assert.True(result.IsPartial);
        CompilerDiagnostic diagnostic = Assert.Single(result.Diagnostics, value =>
            value.Code == "MIDORA1305");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(fixture.Track.Id, diagnostic.Source.TrackId);
        Assert.Equal(fixture.Instrument.Id, diagnostic.Source.EventInstrumentId);
        Assert.DoesNotContain(result.Diagnostics, value => value.Code == "MIDORA1303");
    }

    [Fact]
    public void PreviewContextsPreserveSelectedInstrumentLibraryFolderIdentity()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 480);
        EventInstrumentLibraryFolder folder = new(fixture.Project) { Name = "Folder" };
        fixture.Project.EventInstrumentFolders.Add(folder);
        fixture.Instrument.LibraryFolderId = folder.Id;
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 120, 64);

        CanonicalCompiledResult instrumentPreview = new PreviewCompiler().CompileEventInstrument(
            fixture.Project,
            new EventInstrumentPreviewRequest(fixture.Instrument.Id));
        CanonicalCompiledResult segmentPreview = new PreviewCompiler().CompileSegment(
            fixture.Project,
            fixture.Track.Id,
            fixture.Segment.Id);

        Assert.True(instrumentPreview.IsConsumable);
        Assert.True(segmentPreview.IsConsumable);
        Assert.DoesNotContain(instrumentPreview.Diagnostics, value => value.Code == "MIDORA1021");
        Assert.DoesNotContain(segmentPreview.Diagnostics, value => value.Code == "MIDORA1021");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SegmentPreviewRejectsUnboundAndBrokenInstrumentBindings(bool brokenReference)
    {
        var fixture = CompilerTestProject.Create(segmentLength: 480);
        fixture.Track.EventInstrumentId = brokenReference
            ? fixture.Project.AllocateStableId()
            : null;

        CanonicalCompiledResult result = new PreviewCompiler().CompileSegment(
            fixture.Project,
            fixture.Track.Id,
            fixture.Segment.Id);

        Assert.False(result.IsConsumable);
        Assert.True(result.IsPartial);
        CompilerDiagnostic diagnostic = Assert.Single(result.Diagnostics, value =>
            value.Code == "MIDORA1306");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(fixture.Track.Id, diagnostic.Source.TrackId);
        Assert.Equal(
            brokenReference,
            result.Diagnostics.Any(value => value.Code == "MIDORA1303"));
    }
}
