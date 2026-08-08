using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectTemplateEventEditCommandsTests
{
    [Fact]
    public void TemplateNoteUpdateAutoExtendsLengthPreservesIdentityAndUndoIsExact()
    {
        MidoraProject project = CreateProject();
        EventInstrument instrument = project.EventInstruments[0];
        SubVoice voice = instrument.SubVoices[0];
        TemplateEvent note = voice.Events[0];
        ValueMappingStep mapping = new(project)
        {
            Source = MappingSource.Constant,
            Constant = 1
        };
        note.ValueMappings.Add(mapping);
        MappingChain valueMappings = note.ValueMappings;
        long nextStableId = project.NextStableId;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateTemplateNote(
            instrument.Id,
            voice.Id,
            note.Id,
            tick: 600,
            lengthTicks: 240,
            note: 72,
            velocity: 90,
            followPitchDelta: false));

        Assert.Same(note, voice.Events.Single());
        Assert.Same(valueMappings, note.ValueMappings);
        Assert.Same(mapping, note.ValueMappings.Single());
        Assert.Equal((600, 240, 72, 90, false),
            (note.Tick, note.LengthTicks, note.Number, note.Value, note.FollowPitchDelta));
        Assert.Equal(840, instrument.TemplateLengthTicks);
        Assert.Equal(nextStableId, project.NextStableId);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();

        Assert.Same(note, voice.Events.Single());
        Assert.Equal((0, 480, 60, 100, true),
            (note.Tick, note.LengthTicks, note.Number, note.Value, note.FollowPitchDelta));
        Assert.Equal(480, instrument.TemplateLengthTicks);
        Assert.Equal(nextStableId, project.NextStableId);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void TemplateEventEditsRejectInvalidRawMidiValuesWithoutHistoryChanges()
    {
        MidoraProject project = CreateProject();
        EventInstrument instrument = project.EventInstruments[0];
        SubVoice voice = instrument.SubVoices[0];
        TemplateEvent note = voice.Events[0];
        TemplateEvent controller = TemplateEvent.ControlChange(project, 10, 1, 64);
        TemplateEvent bank = TemplateEvent.Bank(project, 20, 0, 0);
        TemplateEvent program = TemplateEvent.Program(project, 30, 0);
        TemplateEvent pitchBend = Event(project, TemplateEventKind.PitchBend, 40, value: 0);
        TemplateEvent rpn = Event(project, TemplateEventKind.RegisteredParameter, 50, 1, 2);
        TemplateEvent nrpn = Event(project, TemplateEventKind.NonRegisteredParameter, 60, 1, 2);
        TemplateEvent range = Event(project, TemplateEventKind.PitchBendRange, 70, value: 2);
        voice.Events.AddRange([controller, bank, program, pitchBend, rpn, nrpn, range]);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTemplateNote(
                instrument.Id, voice.Id, note.Id, 0, 0, 60, 100, true)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTemplateNote(
                instrument.Id, voice.Id, note.Id, 0, 480, 128, 100, true)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTemplateNote(
                instrument.Id, voice.Id, note.Id, 0, 480, 60, 0, true)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTemplateControlChange(
                instrument.Id, voice.Id, controller.Id, 10, 91, 64)));
        Assert.Throws<ArgumentException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTemplateBank(
                instrument.Id, voice.Id, bank.Id, 20, null, null)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTemplateProgram(
                instrument.Id, voice.Id, program.Id, 30, 128)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTemplatePitchBend(
                instrument.Id, voice.Id, pitchBend.Id, 40, 8_192)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTemplateRegisteredParameter(
                instrument.Id, voice.Id, rpn.Id, 50, 16_384, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTemplateNonRegisteredParameter(
                instrument.Id, voice.Id, nrpn.Id, 60, 0, 16_384)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTemplatePitchBendRange(
                instrument.Id, voice.Id, range.Id, 70, 12, 100)));

        Assert.False(document.IsModified);
        Assert.False(document.CanUndo);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void SameTickStateConflictsAreReplacedAndUndoRestoresExactOrder()
    {
        MidoraProject project = CreateProject();
        EventInstrument instrument = project.EventInstruments[0];
        SubVoice voice = instrument.SubVoices[0];
        TemplateEvent controllerConflict = TemplateEvent.ControlChange(project, 120, 1, 10);
        TemplateEvent controllerTarget = TemplateEvent.ControlChange(project, 240, 1, 20);
        TemplateEvent rpnZero = Event(
            project,
            TemplateEventKind.RegisteredParameter,
            300,
            number: 0,
            value: 256);
        TemplateEvent pitchBendRange = Event(
            project,
            TemplateEventKind.PitchBendRange,
            360,
            value: 2,
            secondaryValue: 0);
        voice.Events.AddRange([controllerConflict, rpnZero, controllerTarget, pitchBendRange]);
        TemplateEvent[] originalOrder = voice.Events.ToArray();
        long nextStableId = project.NextStableId;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateTemplateControlChange(
            instrument.Id,
            voice.Id,
            controllerTarget.Id,
            tick: 120,
            controller: 1,
            value: 99));

        Assert.DoesNotContain(controllerConflict, voice.Events);
        Assert.Contains(controllerTarget, voice.Events);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Equal(originalOrder, voice.Events);
        Assert.Same(controllerConflict, voice.Events[1]);

        document.Execute(ProjectDomainEditCommands.UpdateTemplatePitchBendRange(
            instrument.Id,
            voice.Id,
            pitchBendRange.Id,
            tick: 300,
            semitones: 12,
            cents: 50));

        Assert.DoesNotContain(rpnZero, voice.Events);
        Assert.Contains(pitchBendRange, voice.Events);
        Assert.Equal(nextStableId, project.NextStableId);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Equal(originalOrder, voice.Events);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void BankCannotDropAnActivelyMappedComponent()
    {
        MidoraProject project = CreateProject();
        EventInstrument instrument = project.EventInstruments[0];
        SubVoice voice = instrument.SubVoices[0];
        TemplateEvent mappedBank = TemplateEvent.Bank(project, 120, 1, 2);
        mappedBank.ValueMappings.Add(new ValueMappingStep(project));
        TemplateEvent editableBank = TemplateEvent.Bank(project, 240, 3, 4);
        editableBank.ValueMappings.Add(new ValueMappingStep(project));
        editableBank.ValueMappings.IsEnabled = false;
        voice.Events.AddRange([mappedBank, editableBank]);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateTemplateBank(
                instrument.Id,
                voice.Id,
                mappedBank.Id,
                tick: 120,
                bankMsb: null,
                bankLsb: 2)));
        Assert.False(document.IsModified);

        document.Execute(ProjectDomainEditCommands.UpdateTemplateBank(
            instrument.Id,
            voice.Id,
            editableBank.Id,
            tick: 600,
            bankMsb: null,
            bankLsb: 127));

        Assert.False(editableBank.HasBankMsb);
        Assert.True(editableBank.HasBankLsb);
        Assert.Equal(127, editableBank.SecondaryValue);
        Assert.Equal(601, instrument.TemplateLengthTicks);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.True(editableBank.HasBankMsb);
        Assert.True(editableBank.HasBankLsb);
        Assert.Equal((3, 4), (editableBank.Value, editableBank.SecondaryValue));
        Assert.Equal(480, instrument.TemplateLengthTicks);
        Assert.False(document.IsModified);
    }

    [Fact]
    public void TemplateEventDeleteRestoresTheExactObjectAndListPosition()
    {
        MidoraProject project = CreateProject();
        EventInstrument instrument = project.EventInstruments[0];
        SubVoice voice = instrument.SubVoices[0];
        TemplateEvent controller = TemplateEvent.ControlChange(project, 120, 1, 64);
        TemplateEvent program = TemplateEvent.Program(project, 240, 10);
        voice.Events.AddRange([controller, program]);
        TemplateEvent[] originalOrder = voice.Events.ToArray();
        long nextStableId = project.NextStableId;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.DeleteTemplateEvent(
            instrument.Id,
            voice.Id,
            controller.Id));

        Assert.DoesNotContain(controller, voice.Events);
        Assert.Equal(nextStableId, project.NextStableId);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Equal(originalOrder, voice.Events);
        Assert.Same(controller, voice.Events[1]);
        Assert.Equal(nextStableId, project.NextStableId);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    private static TemplateEvent Event(
        MidoraProject project,
        TemplateEventKind kind,
        long tick,
        int number = 0,
        int value = 0,
        int secondaryValue = 0) =>
        new(project)
        {
            Kind = kind,
            Tick = tick,
            Number = number,
            Value = value,
            SecondaryValue = secondaryValue
        };

    private static ProjectDocumentSession PersistedDocument(ProjectCompilationSession compilation) =>
        new(compilation, ProjectDocumentOrigin.Persisted);

    private static MidoraProject CreateProject()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project)
        {
            Name = "Piano",
            TemplateLengthTicks = 480,
            OverlapPolicy = OverlapPolicy.Warn
        };
        SubVoice voice = new(project);
        voice.Events.Add(TemplateEvent.Note(project, 0, 480, 60, 100));
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
            LengthTicks = 480,
            Note = 60,
            Velocity = 100
        });
        track.Segments.Add(segment);
        project.Tracks.Add(track);
        return project;
    }

    private static void AssertCurrentCompilationMatchesFull(ProjectCompilationSession compilation)
    {
        using MidoraCompiler fullCompiler = new();
        CanonicalCompiledResult expected = fullCompiler.CompileFull(compilation.Project);
        CanonicalCompiledResult actual = compilation.LastAttempt;
        Assert.Equal(expected.IsConsumable, actual.IsConsumable);
        Assert.Equal(expected.IsPartial, actual.IsPartial);
        Assert.Equal(expected.FailureStage, actual.FailureStage);
        Assert.Equal(expected.StartTick, actual.StartTick);
        Assert.Equal(expected.EndTick, actual.EndTick);
        Assert.Equal(expected.Fingerprint, actual.Fingerprint);
        Assert.Equal(expected.Statistics, actual.Statistics);
        Assert.Equal(expected.Events.ToArray(), actual.Events.ToArray());
        Assert.Equal(expected.Allocations.ToArray(), actual.Allocations.ToArray());
        Assert.Equal(expected.Diagnostics, actual.Diagnostics);
    }
}
