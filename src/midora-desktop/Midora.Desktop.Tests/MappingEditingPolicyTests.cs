using Midora.Domain;
using Midora.Mapping.Contract.V2;
using Xunit;

namespace Midora.Desktop.Tests;

public sealed class MappingEditingPolicyTests
{
    [Fact]
    public void NoIsolationOnlyAllowsTriggerVelocityForNoteVelocityTarget()
    {
        Fixture fixture = CreateFixture(requiresIsolation: false);

        IReadOnlyList<MappingSource> noteVelocity = MappingEditingPolicy.AllowedSources(
            fixture.Instrument,
            MappingEditingPolicy.Resolve(fixture.Instrument, fixture.NoteVelocity.Steps.Id));
        IReadOnlyList<MappingSource> controller = MappingEditingPolicy.AllowedSources(
            fixture.Instrument,
            MappingEditingPolicy.Resolve(fixture.Instrument, fixture.Controller.Steps.Id));
        IReadOnlyList<MappingSource> noteNumber = MappingEditingPolicy.AllowedSources(
            fixture.Instrument,
            MappingEditingPolicy.Resolve(fixture.Instrument, fixture.NoteNumber.Steps.Id));

        Assert.Contains(MappingSource.TriggerVelocity, noteVelocity);
        Assert.DoesNotContain(MappingSource.TriggerNote, noteVelocity);
        Assert.DoesNotContain(MappingSource.GateLength, noteVelocity);
        Assert.DoesNotContain(MappingSource.PitchDelta, noteVelocity);
        Assert.DoesNotContain(MappingSource.Envelope, noteVelocity);
        Assert.DoesNotContain(MappingSource.TriggerVelocity, controller);
        Assert.DoesNotContain(MappingSource.TemplateVelocity, controller);
        Assert.Empty(noteNumber);
    }

    [Fact]
    public void IsolationAndOwnerKindProduceContextLegalObjectSources()
    {
        Fixture fixture = CreateFixture(requiresIsolation: true);

        IReadOnlyList<MappingSource> noteVelocity = MappingEditingPolicy.AllowedSources(
            fixture.Instrument,
            MappingEditingPolicy.Resolve(fixture.Instrument, fixture.NoteVelocity.Steps.Id));
        IReadOnlyList<MappingSource> parameter = MappingEditingPolicy.AllowedSources(
            fixture.Instrument,
            MappingEditingPolicy.Resolve(fixture.Instrument, fixture.ParameterMapping.Steps.Id));

        Assert.Contains(MappingSource.TriggerVelocity, noteVelocity);
        Assert.Contains(MappingSource.TemplateNote, noteVelocity);
        Assert.Contains(MappingSource.TemplateVelocity, noteVelocity);
        Assert.Contains(MappingSource.LogicalParameter, noteVelocity);
        Assert.Contains(MappingSource.Envelope, noteVelocity);
        Assert.DoesNotContain(MappingSource.TemplateNote, parameter);
        Assert.DoesNotContain(MappingSource.TemplateVelocity, parameter);
    }

    [Fact]
    public void FunctionsDeclaringPerNoteContextRequireIsolation()
    {
        Fixture fixture = CreateFixture(requiresIsolation: false);
        CSharpMappingFunction contextFree = new(fixture.Project)
        {
            Name = "Context Free",
            Body = "return current;"
        };
        CSharpMappingFunction perNote = new(fixture.Project)
        {
            Name = "Per Note",
            Body = "return context.TriggerVelocity;"
        };
        perNote.DeclaredContextFields.Add(nameof(MappingContextV2.TriggerVelocity));
        fixture.Instrument.MappingFunctions.Add(contextFree);
        fixture.Instrument.MappingFunctions.Add(perNote);
        MappingChainEditingContext context = MappingEditingPolicy.Resolve(
            fixture.Instrument,
            fixture.NoteVelocity.Steps.Id);

        IReadOnlyList<CSharpMappingFunction> withoutIsolation =
            MappingEditingPolicy.AllowedFunctions(fixture.Instrument, context);
        Assert.Contains(contextFree, withoutIsolation);
        Assert.DoesNotContain(perNote, withoutIsolation);

        fixture.Instrument.RequiresChannelIsolation = true;
        IReadOnlyList<CSharpMappingFunction> withIsolation =
            MappingEditingPolicy.AllowedFunctions(fixture.Instrument, context);
        Assert.Contains(contextFree, withIsolation);
        Assert.Contains(perNote, withIsolation);
    }

    private static Fixture CreateFixture(bool requiresIsolation)
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            TemplateLengthTicks = 480,
            RequiresChannelIsolation = requiresIsolation
        };
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Expression",
            Type = LogicalParameterType.Integer,
            Minimum = 0,
            Maximum = 127
        };
        instrument.LogicalParameters.Add(parameter);
        instrument.Envelopes.Add(new InstrumentEnvelope(project) { Name = "Envelope" });
        SubVoice voice = new(project);
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);

        SubVoiceEventMapping noteVelocity = voice.GetOrCreateEventMapping(
            SubVoiceMappingConventions.NoteVelocityTarget);
        SubVoiceEventMapping noteNumber = voice.GetOrCreateEventMapping(
            TemplateEventMappingTarget.Create(
                TemplateEventKind.Note,
                0,
                TemplateEventMappingParameter.Number));
        SubVoiceEventMapping controller = voice.GetOrCreateEventMapping(
            TemplateEventMappingTarget.Create(
                TemplateEventKind.ControlChange,
                1,
                TemplateEventMappingParameter.Value));
        LogicalParameterMapping parameterMapping = new(project)
        {
            ParameterId = parameter.Id,
            SubVoiceId = voice.Id,
            Target = MidiValueTarget.ControlChange(1)
        };
        instrument.ParameterMappings.Add(parameterMapping);
        return new(project, instrument, noteVelocity, noteNumber, controller, parameterMapping);
    }

    private sealed record Fixture(
        MidoraProject Project,
        EventInstrument Instrument,
        SubVoiceEventMapping NoteVelocity,
        SubVoiceEventMapping NoteNumber,
        SubVoiceEventMapping Controller,
        LogicalParameterMapping ParameterMapping);
}
