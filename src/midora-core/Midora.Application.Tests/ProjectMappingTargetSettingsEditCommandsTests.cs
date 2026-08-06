using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectMappingTargetSettingsEditCommandsTests
{
    [Fact]
    public void TemplateEventTargetSettingsUpdateSupportedSlotsAndUndoExactly()
    {
        Fixture fixture = CreateFixture();
        UInt128 nextStableId = fixture.Project.NextStableId;
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateTemplateEventNumberTargetSettings(
            fixture.Instrument.Id,
            fixture.Voice.Id,
            fixture.Note.Id,
            MappingRounding.Floor,
            MappingOverflow.Fail));
        document.Execute(ProjectDomainEditCommands.UpdateTemplateEventValueTargetSettings(
            fixture.Instrument.Id,
            fixture.Voice.Id,
            fixture.Controller.Id,
            MappingRounding.Ceiling,
            MappingOverflow.Clamp));
        document.Execute(ProjectDomainEditCommands.UpdateTemplateEventSecondaryValueTargetSettings(
            fixture.Instrument.Id,
            fixture.Voice.Id,
            fixture.PitchBendRange.Id,
            MappingRounding.Floor,
            MappingOverflow.Clamp));

        Assert.Equal(MappingRounding.Floor, fixture.Note.NumberTargetSettings.Rounding);
        Assert.Equal(MappingOverflow.Fail, fixture.Note.NumberTargetSettings.Overflow);
        Assert.Equal(MappingRounding.Ceiling, fixture.Controller.ValueTargetSettings.Rounding);
        Assert.Equal(MappingOverflow.Clamp, fixture.Controller.ValueTargetSettings.Overflow);
        Assert.Equal(MappingRounding.Floor, fixture.PitchBendRange.SecondaryValueTargetSettings.Rounding);
        Assert.Equal(MappingOverflow.Clamp, fixture.PitchBendRange.SecondaryValueTargetSettings.Overflow);
        Assert.Equal(nextStableId, fixture.Project.NextStableId);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        document.Undo();
        document.Undo();
        Assert.Equal(MappingRounding.Round, fixture.Note.NumberTargetSettings.Rounding);
        Assert.Equal(MappingOverflow.Fail, fixture.Note.NumberTargetSettings.Overflow);
        Assert.Equal(MappingRounding.Round, fixture.Controller.ValueTargetSettings.Rounding);
        Assert.Equal(MappingOverflow.Fail, fixture.Controller.ValueTargetSettings.Overflow);
        Assert.Equal(MappingRounding.Round, fixture.PitchBendRange.SecondaryValueTargetSettings.Rounding);
        Assert.Equal(MappingOverflow.Fail, fixture.PitchBendRange.SecondaryValueTargetSettings.Overflow);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void TargetSettingsRejectIdentityMissingAndNoteClampSlotsWithoutHistory()
    {
        Fixture fixture = CreateFixture();
        TemplateEvent bankWithoutLsb = TemplateEvent.Bank(
            fixture.Project,
            tick: 20,
            msb: 1,
            lsb: null);
        fixture.Voice.Events.Add(bankWithoutLsb);
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTemplateEventNumberTargetSettings(
                fixture.Instrument.Id,
                fixture.Voice.Id,
                fixture.Controller.Id,
                MappingRounding.Round,
                MappingOverflow.Fail)));
        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTemplateEventSecondaryValueTargetSettings(
                fixture.Instrument.Id,
                fixture.Voice.Id,
                bankWithoutLsb.Id,
                MappingRounding.Round,
                MappingOverflow.Fail)));
        Assert.Throws<ArgumentException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTemplateEventNumberTargetSettings(
                fixture.Instrument.Id,
                fixture.Voice.Id,
                fixture.Note.Id,
                MappingRounding.Round,
                MappingOverflow.Clamp)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTemplateEventValueTargetSettings(
                fixture.Instrument.Id,
                fixture.Voice.Id,
                fixture.Controller.Id,
                (MappingRounding)999,
                MappingOverflow.Fail)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTemplateEventValueTargetSettings(
                fixture.Instrument.Id,
                fixture.Voice.Id,
                fixture.Controller.Id,
                MappingRounding.Round,
                (MappingOverflow)999)));

        Assert.False(document.CanUndo);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void NumberRoundingSettingChangesMappedNoteOnlyAtFinalOutput()
    {
        Fixture fixture = CreateFixture();
        fixture.Note.NumberMappings.Add(new ValueMappingStep(fixture.Project)
        {
            Source = MappingSource.Constant,
            Operation = MappingOperation.Override,
            Constant = 62.5
        });
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        Assert.Contains(compilation.LastAttempt.Events.ToArray(), value =>
            value.Role == CanonicalEventRole.NoteOn && value.Message.Byte1 == 63);

        document.Execute(ProjectDomainEditCommands.UpdateTemplateEventNumberTargetSettings(
            fixture.Instrument.Id,
            fixture.Voice.Id,
            fixture.Note.Id,
            MappingRounding.Floor,
            MappingOverflow.Fail));

        Assert.Contains(compilation.LastAttempt.Events.ToArray(), value =>
            value.Role == CanonicalEventRole.NoteOn && value.Message.Byte1 == 62);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Contains(compilation.LastAttempt.Events.ToArray(), value =>
            value.Role == CanonicalEventRole.NoteOn && value.Message.Byte1 == 63);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    private static Fixture CreateFixture()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project)
        {
            Name = "Piano",
            TemplateLengthTicks = 480,
            RequiresChannelIsolation = true,
            OverlapPolicy = OverlapPolicy.Warn
        };
        SubVoice voice = new(project);
        TemplateEvent note = TemplateEvent.Note(project, 0, 240, 60, 100);
        TemplateEvent controller = TemplateEvent.ControlChange(project, 10, 11, 20);
        TemplateEvent pitchBendRange = new(project)
        {
            Kind = TemplateEventKind.PitchBendRange,
            Tick = 15,
            Value = 2,
            SecondaryValue = 50
        };
        voice.Events.Add(note);
        voice.Events.Add(controller);
        voice.Events.Add(pitchBendRange);
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project)
        {
            Name = "Track",
            EventInstrumentId = instrument.Id
        };
        Segment segment = new(project) { LengthTicks = 960 };
        segment.Notes.Add(new LogicalNote(project)
        {
            LengthTicks = 240,
            Note = 60,
            Velocity = 100
        });
        track.Segments.Add(segment);
        project.Tracks.Add(track);
        return new(project, instrument, voice, note, controller, pitchBendRange);
    }

    private static ProjectDocumentSession PersistedDocument(ProjectCompilationSession compilation) =>
        new(compilation, ProjectDocumentOrigin.Persisted);

    private static void AssertCurrentCompilationMatchesFull(ProjectCompilationSession compilation)
    {
        using MidoraCompiler fullCompiler = new();
        CanonicalCompiledResult expected = fullCompiler.CompileFull(compilation.Project);
        CanonicalCompiledResult actual = compilation.LastAttempt;
        Assert.Equal(expected.IsConsumable, actual.IsConsumable);
        Assert.Equal(expected.IsPartial, actual.IsPartial);
        Assert.Equal(expected.StartTick, actual.StartTick);
        Assert.Equal(expected.EndTick, actual.EndTick);
        Assert.Equal(expected.Fingerprint, actual.Fingerprint);
        Assert.Equal(expected.Statistics, actual.Statistics);
        Assert.Equal(expected.Events.ToArray(), actual.Events.ToArray());
        Assert.Equal(expected.Allocations.ToArray(), actual.Allocations.ToArray());
        Assert.Equal(expected.Diagnostics, actual.Diagnostics);
    }

    private sealed record Fixture(
        MidoraProject Project,
        EventInstrument Instrument,
        SubVoice Voice,
        TemplateEvent Note,
        TemplateEvent Controller,
        TemplateEvent PitchBendRange);
}
