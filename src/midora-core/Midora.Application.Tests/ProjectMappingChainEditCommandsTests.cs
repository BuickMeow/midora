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
    public void CustomCSharpStepUpdateNormalizesTheIrrelevantSourceAndUndoRestoresItExactly()
    {
        Fixture fixture = CreateFixture();
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateMappingStep(
            fixture.Instrument.Id,
            fixture.Chain.Id,
            fixture.FirstStep.Id,
            MappingSource.LogicalParameter,
            MappingOperation.CustomCSharp,
            logicalParameterId: null,
            envelopeId: null,
            mappingFunctionId: fixture.Function.Id,
            constant: 5,
            sourceMinimum: 0,
            sourceMaximum: 127,
            targetMinimum: 0,
            targetMaximum: 127,
            MappingInputOverflow.Clamp,
            DivideByZeroPolicy.TargetMaximum));

        Assert.Equal(MappingSource.CurrentValue, fixture.FirstStep.Source);
        Assert.Equal(MappingOperation.CustomCSharp, fixture.FirstStep.Operation);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();

        Assert.Equal(MappingSource.Constant, fixture.FirstStep.Source);
        Assert.Equal(MappingOperation.Add, fixture.FirstStep.Operation);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void CustomCSharpStepCreationNormalizesTheIrrelevantSource()
    {
        Fixture fixture = CreateFixture();
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        int oldCount = fixture.Chain.Count;

        document.Execute(ProjectDomainEditCommands.CreateMappingStep(
            fixture.Instrument.Id,
            fixture.Chain.Id,
            MappingSource.TriggerNote,
            MappingOperation.CustomCSharp,
            mappingFunctionId: fixture.Function.Id));

        ValueMappingStep created = Assert.Single(fixture.Chain.Skip(oldCount));
        Assert.Equal(MappingSource.CurrentValue, created.Source);
        Assert.Equal(MappingOperation.CustomCSharp, created.Operation);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();

        Assert.DoesNotContain(created, fixture.Chain);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void StepPropertiesCanMoveOwnerAtomicallyAndUndoRestoresOwnerIndexAndValues()
    {
        Fixture fixture = CreateFixture();
        SubVoice voice = Assert.Single(fixture.Instrument.SubVoices);
        SubVoiceEventMapping targetOwner = new(
            fixture.Project,
            TemplateEventMappingTarget.Create(
                TemplateEventKind.ControlChange,
                7,
                TemplateEventMappingParameter.Value));
        voice.EventMappings.Add(targetOwner);
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        long nextStableId = fixture.Project.NextStableId;

        document.Execute(ProjectDomainEditCommands.UpdateMappingStepAndOwner(
            fixture.Instrument.Id,
            fixture.Chain.Id,
            targetOwner.Steps.Id,
            fixture.FirstStep.Id,
            isEnabled: false,
            MappingSource.Constant,
            MappingOperation.Override,
            logicalParameterId: null,
            envelopeId: null,
            mappingFunctionId: null,
            constant: 73,
            sourceMinimum: 0,
            sourceMaximum: 127,
            targetMinimum: 0,
            targetMaximum: 127,
            MappingInputOverflow.Clamp,
            DivideByZeroPolicy.TargetMaximum));

        Assert.DoesNotContain(fixture.FirstStep, fixture.Chain);
        Assert.Same(fixture.FirstStep, Assert.Single(targetOwner.Steps));
        Assert.False(fixture.FirstStep.IsEnabled);
        Assert.Equal(73, fixture.FirstStep.Constant);
        Assert.Equal(nextStableId, fixture.Project.NextStableId);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();

        Assert.Same(fixture.FirstStep, fixture.Chain[0]);
        Assert.Empty(targetOwner.Steps);
        Assert.True(fixture.FirstStep.IsEnabled);
        Assert.Equal(5, fixture.FirstStep.Constant);
        Assert.Equal(nextStableId, fixture.Project.NextStableId);
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
    public void NonEmptyEventChainDeleteRemovesOwnerAndUndoRestoresSameOwnerAndSteps()
    {
        Fixture fixture = CreateFixture();
        long nextStableId = fixture.Project.NextStableId;
        SubVoice voice = Assert.Single(fixture.Instrument.SubVoices);
        SubVoiceEventMapping mapping = Assert.Single(
            voice.EventMappings,
            value => value.Steps.Id == fixture.Chain.Id);
        int originalIndex = voice.EventMappings.IndexOf(mapping);
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

        Assert.DoesNotContain(mapping, voice.EventMappings);
        Assert.Collection(
            fixture.Chain,
            value => Assert.Same(fixture.FirstStep, value),
            value => Assert.Same(fixture.SecondStep, value));
        Assert.Equal(nextStableId, fixture.Project.NextStableId);
        AssertController(compilation.LastAttempt, 20);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Same(mapping, voice.EventMappings[originalIndex]);
        Assert.Collection(
            fixture.Chain,
            value => Assert.Same(fixture.FirstStep, value),
            value => Assert.Same(fixture.SecondStep, value));
        AssertController(compilation.LastAttempt, 50);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Redo();
        TemplateEvent controller = Assert.Single(
            voice.Events,
            value => value.Kind == TemplateEventKind.ControlChange);
        document.Execute(ProjectDomainEditCommands.UpdateTemplateControlChange(
            fixture.Instrument.Id,
            voice.Id,
            controller.Id,
            tick: 0,
            controller: 11,
            value: 96));
        document.Execute(ProjectDomainEditCommands.CreateTemplateControlChange(
            fixture.Instrument.Id,
            voice.Id,
            tick: 1,
            controller: 11,
            value: 80));

        Assert.DoesNotContain(voice.EventMappings, value => value.Target == mapping.Target);
        Assert.Equal(2, voice.Events.Count(value => value.Kind == TemplateEventKind.ControlChange));
        AssertController(compilation.LastAttempt, 96);
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
    public void DeletingDisabledEventChainRemovesOwnerAndUndoRestoresDisabledState()
    {
        Fixture fixture = CreateFixture();
        SubVoice voice = Assert.Single(fixture.Instrument.SubVoices);
        SubVoiceEventMapping mapping = Assert.Single(
            voice.EventMappings,
            value => value.Steps.Id == fixture.Chain.Id);
        fixture.Chain.IsEnabled = false;
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.DeleteMappingChain(
            fixture.Instrument.Id,
            fixture.Chain.Id,
            nonEmptyDeletionConfirmed: true));

        Assert.DoesNotContain(mapping, voice.EventMappings);
        Assert.False(fixture.Chain.IsEnabled);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Contains(mapping, voice.EventMappings);
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

    [Fact]
    public void LogicalParameterMappingChainDeleteRemovesOwnerAndUndoRestoresSameIndex()
    {
        Fixture fixture = CreateFixture();
        LogicalParameterMapping mapping = new(fixture.Project)
        {
            ParameterId = fixture.Parameter.Id,
            SubVoiceId = fixture.Instrument.SubVoices[0].Id,
            Target = MidiValueTarget.ControlChange(1)
        };
        mapping.Steps.Add(new ValueMappingStep(fixture.Project)
        {
            Source = MappingSource.LogicalParameter,
            LogicalParameterId = fixture.Parameter.Id,
            Operation = MappingOperation.Override
        });
        fixture.Instrument.ParameterMappings.Add(mapping);
        int originalIndex = fixture.Instrument.ParameterMappings.IndexOf(mapping);
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.DeleteMappingChain(
            fixture.Instrument.Id,
            mapping.Steps.Id,
            nonEmptyDeletionConfirmed: true));

        Assert.DoesNotContain(mapping, fixture.Instrument.ParameterMappings);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();

        Assert.Same(mapping, fixture.Instrument.ParameterMappings[originalIndex]);
        Assert.Single(mapping.Steps);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void NoteMappingChainCannotBeDeleted()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        SubVoice voice = Assert.Single(instrument.SubVoices);
        voice.Events.Add(TemplateEvent.Note(project, 0, 120, 60, 100));
        SubVoiceEventMapping mapping = voice.FindEventMapping(
            SubVoiceMappingConventions.NoteVelocityTarget)!;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.DeleteMappingChain(
                instrument.Id,
                mapping.Steps.Id,
                nonEmptyDeletionConfirmed: true)));
        Assert.Contains(mapping, voice.EventMappings);
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void ChainPropertiesTargetSettingsUpdateTheOwningEventMappingAndUndo()
    {
        Fixture fixture = CreateFixture();
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateMappingChainTargetSettings(
            fixture.Instrument.Id,
            fixture.Chain.Id,
            MappingRounding.Floor,
            MappingOverflow.Clamp));

        SubVoiceEventMapping mapping = fixture.Instrument.SubVoices[0].EventMappings
            .Single(value => value.Steps.Id == fixture.Chain.Id);
        Assert.Equal(MappingRounding.Floor, mapping.TargetSettings.Rounding);
        Assert.Equal(MappingOverflow.Clamp, mapping.TargetSettings.Overflow);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Equal(MappingRounding.Round, mapping.TargetSettings.Rounding);
        Assert.Equal(MappingOverflow.Fail, mapping.TargetSettings.Overflow);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void ChainPropertiesTargetSettingsPropagateAcrossSharedParameterTarget()
    {
        Fixture fixture = CreateFixture();
        LogicalParameterMapping first = new(fixture.Project)
        {
            ParameterId = fixture.Parameter.Id,
            SubVoiceId = fixture.Instrument.SubVoices[0].Id,
            Target = MidiValueTarget.ControlChange(2)
        };
        LogicalParameterMapping second = new(fixture.Project)
        {
            ParameterId = fixture.Parameter.Id,
            SubVoiceId = first.SubVoiceId,
            Target = first.Target
        };
        fixture.Instrument.ParameterMappings.Add(first);
        fixture.Instrument.ParameterMappings.Add(second);
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateMappingChainTargetSettings(
            fixture.Instrument.Id,
            first.Steps.Id,
            MappingRounding.Ceiling,
            MappingOverflow.Clamp));

        Assert.All(
            new[] { first, second },
            mapping =>
            {
                Assert.Equal(MappingRounding.Ceiling, mapping.TargetSettings.Rounding);
                Assert.Equal(MappingOverflow.Clamp, mapping.TargetSettings.Overflow);
            });

        document.Undo();
        Assert.All(
            new[] { first, second },
            mapping =>
            {
                Assert.Equal(MappingRounding.Round, mapping.TargetSettings.Rounding);
                Assert.Equal(MappingOverflow.Fail, mapping.TargetSettings.Overflow);
            });
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void FollowInstanceVelocityPresetCanBeDisabledEnabledAndUndone()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        SubVoice voice = Assert.Single(instrument.SubVoices);
        _ = SubVoiceMappingConventions.AddDefaultInstanceVelocityMapping(project, voice);
        SubVoiceEventMapping mapping = voice.FindEventMapping(
            SubVoiceMappingConventions.NoteVelocityTarget)!;
        ValueMappingStep original = Assert.Single(mapping.Steps);
        ValueMappingStep custom = new(project)
        {
            Source = MappingSource.Constant,
            Operation = MappingOperation.Add,
            Constant = -10
        };
        mapping.Steps.Add(custom);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        Assert.True(SubVoiceMappingConventions.FollowsInstanceVelocity(voice));
        document.Execute(ProjectDomainEditCommands.SetSubVoiceFollowInstanceVelocity(
            instrument.Id,
            voice.Id,
            followsInstanceVelocity: false));
        Assert.False(SubVoiceMappingConventions.FollowsInstanceVelocity(voice));
        Assert.Same(custom, Assert.Single(mapping.Steps));

        document.Undo();
        Assert.True(SubVoiceMappingConventions.FollowsInstanceVelocity(voice));
        Assert.Collection(
            mapping.Steps,
            value => Assert.Same(original, value),
            value => Assert.Same(custom, value));

        document.Execute(ProjectDomainEditCommands.SetSubVoiceFollowInstanceVelocity(
            instrument.Id,
            voice.Id,
            followsInstanceVelocity: false));
        document.Execute(ProjectDomainEditCommands.SetSubVoiceFollowInstanceVelocity(
            instrument.Id,
            voice.Id,
            followsInstanceVelocity: true));
        Assert.True(SubVoiceMappingConventions.FollowsInstanceVelocity(voice));
        Assert.Equal(MappingSource.TriggerVelocity, mapping.Steps[0].Source);
        Assert.Same(custom, mapping.Steps[1]);
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
        Assert.True(
            result.IsConsumable,
            string.Join(Environment.NewLine, result.Diagnostics.Select(value =>
                $"{value.Code}: {value.Message}")));
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
        };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 960 };
        segment.Notes.Add(new LogicalNote(project)
        {
            LengthTicks = 240,
            Note = 60,
            Velocity = 100
        });
        track.Segments.Add(segment);
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
