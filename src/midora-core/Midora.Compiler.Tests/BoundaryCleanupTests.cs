using Midora.Domain;
using Midora.Midi;

namespace Midora.Compiler.Tests;

public sealed class BoundaryCleanupTests
{
    [Fact]
    public void BoundaryNoteOffsRetainFifoTemplateAndLogicalNoteSources()
    {
        var fixture = CompilerTestProject.Create();
        TemplateEvent first = TemplateEvent.Note(fixture.Project, 0, 300, 60, 100);
        TemplateEvent second = TemplateEvent.Note(fixture.Project, 10, 300, 60, 100);
        fixture.Voice.Events.Add(first);
        fixture.Voice.Events.Add(second);
        LogicalNote logicalNote = CompilerTestProject.AddNote(
            fixture.Segment,
            fixture.Instrument,
            0,
            480);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest
            {
                Purpose = CompilationPurpose.Range,
                EndTick = 150
            });

        CanonicalMidiEvent[] boundary = result.Events.ToArray()
            .Where(value => value.Tick == 150
                && value.Message.MessageType == MidiMessageType.NoteOff
                && value.Source.Origin == SourceOrigin.CompilerBoundaryCleanup)
            .ToArray();
        Assert.Equal(2, boundary.Length);
        Assert.All(boundary, value => Assert.Equal(logicalNote.Id, value.Source.LogicalNoteId));
        Assert.Equal([first.Id, second.Id], boundary.Select(value => value.Source.SourceEventId).ToArray());
        Assert.All(boundary, value => Assert.Equal(150, value.Source.Tick));
    }

    [Fact]
    public void SourceNoteOffDequeuesOldestOverlappingPitchBeforeBoundary()
    {
        var fixture = CompilerTestProject.Create();
        TemplateEvent first = TemplateEvent.Note(fixture.Project, 0, 100, 60, 100);
        TemplateEvent second = TemplateEvent.Note(fixture.Project, 10, 300, 60, 100);
        fixture.Voice.Events.Add(first);
        fixture.Voice.Events.Add(second);
        _ = CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 480);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest
            {
                Purpose = CompilationPurpose.Range,
                EndTick = 150
            });

        CanonicalMidiEvent boundary = Assert.Single(result.Events.ToArray(), value =>
            value.Tick == 150
            && value.Message.MessageType == MidiMessageType.NoteOff
            && value.Source.Origin == SourceOrigin.CompilerBoundaryCleanup);
        Assert.Equal(second.Id, boundary.Source.SourceEventId);
    }

    [Fact]
    public void BoundaryNoteOffsUseDeterministicPortChannelPitchOrder()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 300, 61, 100));
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 300, 60, 100));
        _ = CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 480);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest
            {
                Purpose = CompilationPurpose.Range,
                EndTick = 150
            });

        byte[] pitches = result.Events.ToArray()
            .Where(value => value.Tick == 150
                && value.Message.MessageType == MidiMessageType.NoteOff
                && value.Source.Origin == SourceOrigin.CompilerBoundaryCleanup)
            .Select(value => value.Message.Byte1)
            .ToArray();
        Assert.Equal([60, 61], pitches);
    }

    [Fact]
    public void HardBoundaryOrdersExactNoteOffThenAllSoundOffThenStateReset()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 480);
        fixture.Voice.Events.Add(TemplateEvent.ControlChange(fixture.Project, 0, 11, 80));
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 300, 60, 100));
        _ = CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 480);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest
            {
                Purpose = CompilationPurpose.Range,
                EndTick = 150
            });

        CanonicalMidiEvent[] boundary = result.Events.ToArray()
            .Where(value => value.Tick == 150)
            .ToArray();
        int noteOff = Array.FindIndex(boundary, value =>
            value.Message.MessageType == MidiMessageType.NoteOff);
        int soundOff = Array.FindIndex(boundary, value =>
            value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 120);
        int expressionReset = Array.FindIndex(boundary, value =>
            value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 11
            && value.Role == CanonicalEventRole.Reset);

        Assert.True(noteOff >= 0 && noteOff < soundOff && soundOff < expressionReset);
        Assert.Equal(SourceOrigin.CompilerBoundaryCleanup, boundary[soundOff].Source.Origin);
    }

    [Fact]
    public void SharedAllocationGroupCleansOnlyAfterTheLastOverlappingInstance()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 960);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        _ = CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);
        _ = CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 120, 240);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);
        CanonicalMidiEvent[] soundOffs = result.Events.ToArray()
            .Where(value => value.Message.MessageType == MidiMessageType.ControlChange
                && value.Message.Byte1 == 120)
            .ToArray();

        CanonicalMidiEvent soundOff = Assert.Single(soundOffs);
        Assert.Equal(360, soundOff.Tick);
        Assert.DoesNotContain(result.Events.ToArray(), value =>
            value.Tick == 240 && value.Role == CanonicalEventRole.Reset);
        Assert.Single(result.Allocations.ToArray().Select(value => value.InstanceGroupId).Distinct());
    }

    [Fact]
    public void AdjacentAllocationGroupsCleanBeforeTheReplacementStartsOnTheReusedChannel()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 960);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        _ = CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);
        _ = CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 240, 240);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);
        CanonicalMidiEvent[] atReuse = result.Events.ToArray()
            .Where(value => value.Tick == 240)
            .ToArray();
        int soundOff = Array.FindIndex(atReuse, value =>
            value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 120);
        int noteOn = Array.FindIndex(atReuse, value =>
            value.Message.MessageType == MidiMessageType.NoteOn
            && value.Message.Byte2 != 0);

        Assert.True(soundOff >= 0 && soundOff < noteOn);
        Assert.Equal(atReuse[soundOff].ZeroBasedPort, atReuse[noteOn].ZeroBasedPort);
        Assert.Equal(atReuse[soundOff].ZeroBasedChannel, atReuse[noteOn].ZeroBasedChannel);
    }

    [Fact]
    public void EmptySubVoiceDoesNotEmitAllSoundOff()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 480);
        _ = CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.DoesNotContain(result.Events.ToArray(), value =>
            value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 120);
    }

    [Fact]
    public void ZeroLengthRangeDoesNotReportRangeExternalEmptySubVoice()
    {
        var fixture = CompilerTestProject.Create();
        _ = CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 120, 240);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest
            {
                Purpose = CompilationPurpose.Range,
                StartTick = 0,
                EndTick = 0
            });

        Assert.True(result.IsConsumable);
        Assert.DoesNotContain(result.Diagnostics, value => value.Code == "MIDORA1225");
    }

    [Fact]
    public void EmptySubVoiceInfoHonorsExplicitSubVoiceSelection()
    {
        var fixture = CompilerTestProject.Create(subVoiceCount: 2);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        SubVoice selectedEmptyVoice = fixture.Instrument.SubVoices[1];
        _ = CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest
            {
                Purpose = CompilationPurpose.EventInstrumentPreview,
                EndTick = 240,
                IncludedSubVoiceIds = [selectedEmptyVoice.Id]
            });

        CompilerDiagnostic diagnostic = Assert.Single(
            result.Diagnostics,
            value => value.Code == "MIDORA1225");
        Assert.Equal(selectedEmptyVoice.Id, diagnostic.Source.SubVoiceId);
        Assert.DoesNotContain(result.Events.ToArray(), value =>
            value.Source.SubVoiceId == fixture.Voice.Id);
    }
}
