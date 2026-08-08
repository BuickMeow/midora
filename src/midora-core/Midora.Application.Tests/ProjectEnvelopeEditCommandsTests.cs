using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectEnvelopeEditCommandsTests
{
    [Fact]
    public void EnvelopeUpdateValidatesNormalizesPreservesIdentityAndUndoIsExact()
    {
        Fixture fixture = CreateFixture();
        long nextStableId = fixture.Project.NextStableId;
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateInstrumentEnvelope(
            fixture.Instrument.Id,
            fixture.Envelope.Id,
            "  Swell  ",
            delayTicks: 10,
            attackTicks: 20,
            holdTicks: 30,
            decayTicks: 40,
            startValue: 0.2,
            peakValue: 0.8,
            sustainValue: 0.5,
            releaseTicks: 300,
            endValue: 0.1));

        Assert.Same(fixture.Envelope, fixture.Instrument.Envelopes.Single());
        Assert.Equal("Swell", fixture.Envelope.Name);
        Assert.Equal((10, 20, 30, 40, 300),
            (fixture.Envelope.DelayTicks,
                fixture.Envelope.AttackTicks,
                fixture.Envelope.HoldTicks,
                fixture.Envelope.DecayTicks,
                fixture.Envelope.ReleaseTicks));
        Assert.Equal((0.2, 0.8, 0.5, 0.1),
            (fixture.Envelope.StartValue,
                fixture.Envelope.PeakValue,
                fixture.Envelope.SustainValue,
                fixture.Envelope.EndValue));
        Assert.Equal(480, fixture.Instrument.TemplateLengthTicks);
        Assert.Equal(nextStableId, fixture.Project.NextStableId);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Same(fixture.Envelope, fixture.Instrument.Envelopes.Single());
        Assert.Null(fixture.Envelope.Name);
        Assert.Equal((0, 0, 0, 0, 0),
            (fixture.Envelope.DelayTicks,
                fixture.Envelope.AttackTicks,
                fixture.Envelope.HoldTicks,
                fixture.Envelope.DecayTicks,
                fixture.Envelope.ReleaseTicks));
        Assert.Equal((0d, 1d, 1d, 0d),
            (fixture.Envelope.StartValue,
                fixture.Envelope.PeakValue,
                fixture.Envelope.SustainValue,
                fixture.Envelope.EndValue));
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void EnvelopeUpdateRejectsInvalidValuesAndIsolationRestrictedEditing()
    {
        Fixture fixture = CreateFixture();
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            Update(fixture, delayTicks: -1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            Update(fixture, peakValue: 1.01)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            Update(fixture, sustainValue: double.NaN)));
        Assert.Throws<ArgumentException>(() => document.Execute(
            Update(fixture, name: "bad\nname")));
        Assert.False(document.CanUndo);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateEventInstrumentIsolation(
            fixture.Instrument.Id,
            requiresChannelIsolation: false));
        Assert.Throws<InvalidOperationException>(() => document.Execute(Update(fixture)));
        Assert.True(document.CanUndo);
        Assert.Equal("Change event instrument isolation", document.UndoName);
        document.Undo();
        Assert.True(fixture.Instrument.RequiresChannelIsolation);
        Assert.False(document.IsModified);
    }

    [Fact]
    public void ReferencedDeleteRequiresConfirmationKeepsBrokenReferenceAndUndoRestoresObject()
    {
        Fixture fixture = CreateFixture();
        ValueMappingStep step = fixture.Controller.ValueMappings.Single();
        long nextStableId = fixture.Project.NextStableId;
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.DeleteInstrumentEnvelope(
                fixture.Instrument.Id,
                fixture.Envelope.Id,
                referencedDeletionConfirmed: false)));
        document.Execute(ProjectDomainEditCommands.DeleteInstrumentEnvelope(
            fixture.Instrument.Id,
            fixture.Envelope.Id,
            referencedDeletionConfirmed: true));

        Assert.DoesNotContain(fixture.Envelope, fixture.Instrument.Envelopes);
        Assert.Equal(fixture.Envelope.Id, step.EnvelopeId);
        Assert.False(compilation.LastAttempt.IsConsumable);
        Assert.Contains(compilation.LastAttempt.Diagnostics, value => value.Code == "MIDORA1233");
        Assert.Equal(nextStableId, fixture.Project.NextStableId);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Same(fixture.Envelope, fixture.Instrument.Envelopes[0]);
        Assert.Equal(fixture.Envelope.Id, step.EnvelopeId);
        Assert.True(compilation.LastAttempt.IsConsumable);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void UnreferencedEnvelopeCanBeDeletedWhileIsolationIsDisabled()
    {
        Fixture fixture = CreateFixture();
        InstrumentEnvelope unreferenced = new(fixture.Project) { Name = "Unused" };
        fixture.Instrument.Envelopes.Add(unreferenced);
        fixture.Instrument.RequiresChannelIsolation = false;
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.DeleteInstrumentEnvelope(
            fixture.Instrument.Id,
            unreferenced.Id,
            referencedDeletionConfirmed: false));

        Assert.DoesNotContain(unreferenced, fixture.Instrument.Envelopes);
        document.Undo();
        Assert.Same(unreferenced, fixture.Instrument.Envelopes[1]);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    private static IProjectEditCommand Update(
        Fixture fixture,
        string? name = null,
        long delayTicks = 0,
        double peakValue = 1,
        double sustainValue = 1) =>
        ProjectDomainEditCommands.UpdateInstrumentEnvelope(
            fixture.Instrument.Id,
            fixture.Envelope.Id,
            name,
            delayTicks,
            attackTicks: 0,
            holdTicks: 0,
            decayTicks: 0,
            startValue: 0,
            peakValue,
            sustainValue,
            releaseTicks: 0,
            endValue: 0);

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
        InstrumentEnvelope envelope = new(project);
        instrument.Envelopes.Add(envelope);
        SubVoice voice = new(project);
        TemplateEvent controller = TemplateEvent.ControlChange(project, 0, 11, 20);
        controller.ValueMappings.Add(new ValueMappingStep(project)
        {
            Source = MappingSource.Envelope,
            EnvelopeId = envelope.Id
        });
        voice.Events.Add(controller);
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
            LengthTicks = 240,
            Note = 60,
            Velocity = 100
        });
        track.Segments.Add(segment);
        project.Tracks.Add(track);
        return new(project, instrument, envelope, controller);
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
        Assert.Equal(expected.FailureStage, actual.FailureStage);
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
        InstrumentEnvelope Envelope,
        TemplateEvent Controller);
}
