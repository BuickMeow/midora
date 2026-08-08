using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectMappingChainEditCommandsTests
{
    [Fact]
    public void ChainAndStepEnabledStatesChangeCompilationAndUndoExactly()
    {
        Fixture fixture = CreateFixture();
        long nextStableId = fixture.Project.NextStableId;
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        AssertController(compilation.LastAttempt, 50);

        ProjectEditExecution noChange = document.Execute(
            ProjectDomainEditCommands.UpdateMappingChainEnabled(
                fixture.Instrument.Id,
                fixture.Chain.Id,
                isEnabled: true));
        Assert.False(noChange.Changed);
        Assert.False(document.CanUndo);

        document.Execute(ProjectDomainEditCommands.UpdateMappingStepEnabled(
            fixture.Instrument.Id,
            fixture.Chain.Id,
            fixture.FirstStep.Id,
            isEnabled: false));
        AssertController(compilation.LastAttempt, 40);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.True(fixture.FirstStep.IsEnabled);
        AssertController(compilation.LastAttempt, 50);

        document.Execute(ProjectDomainEditCommands.UpdateMappingChainEnabled(
            fixture.Instrument.Id,
            fixture.Chain.Id,
            isEnabled: false));
        AssertController(compilation.LastAttempt, 20);
        Assert.Equal(nextStableId, fixture.Project.NextStableId);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.True(fixture.Chain.IsEnabled);
        AssertController(compilation.LastAttempt, 50);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void StepUpdatePreservesIdentityAndReferencesAndUndoIsExact()
    {
        Fixture fixture = CreateFixture();
        long nextStableId = fixture.Project.NextStableId;
        MidoraId id = fixture.FirstStep.Id;
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateMappingStep(
            fixture.Instrument.Id,
            fixture.Chain.Id,
            fixture.FirstStep.Id,
            MappingSource.Constant,
            MappingOperation.Add,
            fixture.Parameter.Id,
            fixture.Envelope.Id,
            fixture.Function.Id,
            constant: 15,
            sourceMinimum: -2,
            sourceMaximum: 2,
            targetMinimum: -10,
            targetMaximum: 200,
            MappingInputOverflow.Extrapolate,
            DivideByZeroPolicy.TargetDefault));

        Assert.Same(fixture.FirstStep, fixture.Chain[0]);
        Assert.Equal(id, fixture.FirstStep.Id);
        Assert.Equal(fixture.Parameter.Id, fixture.FirstStep.LogicalParameterId);
        Assert.Equal(fixture.Envelope.Id, fixture.FirstStep.EnvelopeId);
        Assert.Equal(fixture.Function.Id, fixture.FirstStep.MappingFunctionId);
        Assert.Equal(MappingInputOverflow.Extrapolate, fixture.FirstStep.InputOverflow);
        Assert.Equal(DivideByZeroPolicy.TargetDefault, fixture.FirstStep.DivideByZero);
        Assert.Equal(nextStableId, fixture.Project.NextStableId);
        AssertController(compilation.LastAttempt, 70);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Same(fixture.FirstStep, fixture.Chain[0]);
        Assert.Equal(5, fixture.FirstStep.Constant);
        Assert.Null(fixture.FirstStep.LogicalParameterId);
        Assert.Null(fixture.FirstStep.EnvelopeId);
        Assert.Null(fixture.FirstStep.MappingFunctionId);
        Assert.Equal(MappingInputOverflow.Clamp, fixture.FirstStep.InputOverflow);
        Assert.Equal(DivideByZeroPolicy.TargetMaximum, fixture.FirstStep.DivideByZero);
        AssertController(compilation.LastAttempt, 50);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void ReorderChangesExecutionOrderAndUndoRestoresObjectOrder()
    {
        Fixture fixture = CreateFixture();
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.ReorderMappingStep(
            fixture.Instrument.Id,
            fixture.Chain.Id,
            fixture.SecondStep.Id,
            newIndex: 0));

        Assert.Same(fixture.SecondStep, fixture.Chain[0]);
        Assert.Same(fixture.FirstStep, fixture.Chain[1]);
        AssertController(compilation.LastAttempt, 45);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Same(fixture.FirstStep, fixture.Chain[0]);
        Assert.Same(fixture.SecondStep, fixture.Chain[1]);
        AssertController(compilation.LastAttempt, 50);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void StepDeletePreservesReferencedResourcesAndUndoRestoresSameObjectAndIndex()
    {
        Fixture fixture = CreateFixture();
        fixture.FirstStep.LogicalParameterId = fixture.Parameter.Id;
        fixture.FirstStep.EnvelopeId = fixture.Envelope.Id;
        fixture.FirstStep.MappingFunctionId = fixture.Function.Id;
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.DeleteMappingStep(
            fixture.Instrument.Id,
            fixture.Chain.Id,
            fixture.FirstStep.Id));

        Assert.DoesNotContain(fixture.FirstStep, fixture.Chain);
        Assert.Contains(fixture.Parameter, fixture.Instrument.LogicalParameters);
        Assert.Contains(fixture.Envelope, fixture.Instrument.Envelopes);
        Assert.Contains(fixture.Function, fixture.Instrument.MappingFunctions);
        AssertController(compilation.LastAttempt, 40);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Same(fixture.FirstStep, fixture.Chain[0]);
        Assert.Equal(fixture.Parameter.Id, fixture.FirstStep.LogicalParameterId);
        Assert.Equal(fixture.Envelope.Id, fixture.FirstStep.EnvelopeId);
        Assert.Equal(fixture.Function.Id, fixture.FirstStep.MappingFunctionId);
        AssertController(compilation.LastAttempt, 50);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void NonEmptyChainDeleteRequiresConfirmationAndUndoRestoresAllSteps()
    {
        Fixture fixture = CreateFixture();
        long nextStableId = fixture.Project.NextStableId;
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.DeleteMappingChain(
                fixture.Instrument.Id,
                fixture.Chain.Id,
                nonEmptyDeletionConfirmed: false)));
        Assert.False(document.CanUndo);

        document.Execute(ProjectDomainEditCommands.DeleteMappingChain(
            fixture.Instrument.Id,
            fixture.Chain.Id,
            nonEmptyDeletionConfirmed: true));

        Assert.Empty(fixture.Chain);
        Assert.Equal(nextStableId, fixture.Project.NextStableId);
        AssertController(compilation.LastAttempt, 20);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Collection(
            fixture.Chain,
            value => Assert.Same(fixture.FirstStep, value),
            value => Assert.Same(fixture.SecondStep, value));
        AssertController(compilation.LastAttempt, 50);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void IncompleteStepCanBeSavedAndDisabledWithoutDiagnostics()
    {
        Fixture fixture = CreateFixture();
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateMappingStep(
            fixture.Instrument.Id,
            fixture.Chain.Id,
            fixture.FirstStep.Id,
            MappingSource.CurrentValue,
            MappingOperation.CustomCSharp,
            logicalParameterId: null,
            envelopeId: null,
            mappingFunctionId: null,
            constant: 0,
            sourceMinimum: 10,
            sourceMaximum: -10,
            targetMinimum: 20,
            targetMaximum: -20,
            MappingInputOverflow.Clamp,
            DivideByZeroPolicy.TargetMaximum));

        Assert.False(compilation.LastAttempt.IsConsumable);
        Assert.Contains(compilation.LastAttempt.Diagnostics, value => value.Code == "MIDORA1270");
        Assert.Contains(compilation.LastAttempt.Diagnostics, value => value.Code == "MIDORA1271");
        AssertCurrentCompilationMatchesFull(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateMappingStepEnabled(
            fixture.Instrument.Id,
            fixture.Chain.Id,
            fixture.FirstStep.Id,
            isEnabled: false));

        Assert.True(compilation.LastAttempt.IsConsumable);
        Assert.DoesNotContain(compilation.LastAttempt.Diagnostics,
            value => value.Code is "MIDORA1270" or "MIDORA1271" or "MIDORA1234");
        AssertController(compilation.LastAttempt, 40);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void StepUpdateRejectsUnpersistableValuesWithoutHistory()
    {
        Fixture fixture = CreateFixture();
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            UpdateFirstStep(fixture, source: (MappingSource)999)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            UpdateFirstStep(fixture, operation: (MappingOperation)999)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            UpdateFirstStep(fixture, inputOverflow: (MappingInputOverflow)999)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            UpdateFirstStep(fixture, divideByZero: (DivideByZeroPolicy)999)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            UpdateFirstStep(fixture, constant: double.NaN)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            UpdateFirstStep(fixture, mappingFunctionId: default(MidoraId))));

        Assert.False(document.CanUndo);
        Assert.False(document.IsModified);
        Assert.Equal(5, fixture.FirstStep.Constant);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void DeletingDisabledChainResetsEmptySentinelAndUndoRestoresDisabledState()
    {
        Fixture fixture = CreateFixture();
        fixture.Chain.IsEnabled = false;
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.DeleteMappingChain(
            fixture.Instrument.Id,
            fixture.Chain.Id,
            nonEmptyDeletionConfirmed: true));

        Assert.Empty(fixture.Chain);
        Assert.True(fixture.Chain.IsEnabled);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.False(fixture.Chain.IsEnabled);
        Assert.Collection(
            fixture.Chain,
            value => Assert.Same(fixture.FirstStep, value),
            value => Assert.Same(fixture.SecondStep, value));
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void LogicalParameterMappingChainIsLocatedByStableId()
    {
        Fixture fixture = CreateFixture();
        LogicalParameterMapping mapping = new(fixture.Project)
        {
            ParameterId = fixture.Parameter.Id,
            SubVoiceId = fixture.Instrument.SubVoices[0].Id,
            Target = MidiValueTarget.ControlChange(1)
        };
        mapping.TargetSettings.Overflow = MappingOverflow.Clamp;
        mapping.Steps.Add(new ValueMappingStep(fixture.Project)
        {
            Source = MappingSource.LogicalParameter,
            LogicalParameterId = fixture.Parameter.Id,
            Operation = MappingOperation.Override
        });
        fixture.Instrument.ParameterMappings.Add(mapping);
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateMappingChainEnabled(
            fixture.Instrument.Id,
            mapping.Steps.Id,
            isEnabled: false));

        Assert.False(mapping.Steps.IsEnabled);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.True(mapping.Steps.IsEnabled);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    private static IProjectEditCommand UpdateFirstStep(
        Fixture fixture,
        MappingSource source = MappingSource.Constant,
        MappingOperation operation = MappingOperation.Add,
        MidoraId? mappingFunctionId = null,
        double constant = 5,
        MappingInputOverflow inputOverflow = MappingInputOverflow.Clamp,
        DivideByZeroPolicy divideByZero = DivideByZeroPolicy.TargetMaximum) =>
        ProjectDomainEditCommands.UpdateMappingStep(
            fixture.Instrument.Id,
            fixture.Chain.Id,
            fixture.FirstStep.Id,
            source,
            operation,
            logicalParameterId: null,
            envelopeId: null,
            mappingFunctionId,
            constant,
            sourceMinimum: 0,
            sourceMaximum: 1,
            targetMinimum: 0,
            targetMaximum: 127,
            inputOverflow,
            divideByZero);

    private static void AssertController(CanonicalCompiledResult result, byte expected)
    {
        Assert.True(result.IsConsumable);
        CanonicalMidiEvent value = Assert.Single(result.Events.ToArray(), item =>
            item.Tick == 0
            && item.Role == CanonicalEventRole.ControlChange
            && item.Message.MessageType == MidiMessageType.ControlChange
            && item.Message.Byte1 == 11);
        Assert.Equal(expected, value.Message.Byte2);
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
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Expression",
            Type = LogicalParameterType.Integer,
            Minimum = 0,
            Maximum = 127,
            DisplayMinimum = 0,
            DisplayMaximum = 127,
            DefaultValue = 0
        };
        InstrumentEnvelope envelope = new(project) { Name = "Envelope" };
        CSharpMappingFunction function = new(project)
        {
            Name = "Identity",
            Body = "return value;"
        };
        instrument.LogicalParameters.Add(parameter);
        instrument.Envelopes.Add(envelope);
        instrument.MappingFunctions.Add(function);
        SubVoice voice = new(project);
        TemplateEvent controller = TemplateEvent.ControlChange(project, 0, 11, 20);
        ValueMappingStep firstStep = new(project)
        {
            Source = MappingSource.Constant,
            Operation = MappingOperation.Add,
            Constant = 5
        };
        ValueMappingStep secondStep = new(project)
        {
            Source = MappingSource.Constant,
            Operation = MappingOperation.Multiply,
            Constant = 2
        };
        controller.ValueMappings.Add(firstStep);
        controller.ValueMappings.Add(secondStep);
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
        return new(
            project,
            instrument,
            parameter,
            envelope,
            function,
            controller.ValueMappings,
            firstStep,
            secondStep);
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
        LogicalParameterDefinition Parameter,
        InstrumentEnvelope Envelope,
        CSharpMappingFunction Function,
        MappingChain Chain,
        ValueMappingStep FirstStep,
        ValueMappingStep SecondStep);
}
