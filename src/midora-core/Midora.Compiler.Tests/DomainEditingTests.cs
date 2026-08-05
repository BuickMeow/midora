using Midora.Domain;

namespace Midora.Compiler.Tests;

public sealed class DomainEditingTests
{
    [Fact]
    public void LibraryCreationProducesMinimalValidUniqueInstrument()
    {
        MidoraProject project = new(960);

        EventInstrument first = EventInstrumentLibrary.Create(project);
        EventInstrument second = EventInstrumentLibrary.Create(project);

        Assert.Equal("Event Instrument 1", first.Name);
        Assert.Equal("Event Instrument 2", second.Name);
        Assert.Equal(960, first.TemplateLengthTicks);
        Assert.Single(first.SubVoices);
        Assert.Empty(first.SubVoices[0].Events);
        Assert.True(new MidoraCompiler().CompileFull(project).IsConsumable);
    }

    [Fact]
    public void LibraryDuplicateDeepCopiesAndRemapsEveryInternalReference()
    {
        MidoraProject project = new(480);
        EventInstrument source = EventInstrumentLibrary.Create(project, "Source");
        source.Description = "description";
        LogicalParameterDefinition parameter = new()
        {
            Name = "parameter",
            Minimum = 0,
            Maximum = 1
        };
        source.LogicalParameters.Add(parameter);
        InstrumentEnvelope envelope = new() { Name = "envelope", ReleaseTicks = 20 };
        source.Envelopes.Add(envelope);
        CSharpMappingFunction function = new() { Name = "mapping", Body = "return value;" };
        source.MappingFunctions.Add(function);
        TemplateEvent value = TemplateEvent.ControlChange(0, 1, 10);
        value.ValueMappings.Add(new ValueMappingStep
        {
            Operation = MappingOperation.CustomCSharp,
            MappingFunctionId = function.Id,
            EnvelopeId = envelope.Id,
            LogicalParameterId = parameter.Id
        });
        source.SubVoices[0].Events.Add(value);
        LogicalParameterMapping parameterMapping = new()
        {
            ParameterId = parameter.Id,
            SubVoiceId = source.SubVoices[0].Id,
            Target = MidiValueTarget.ControlChange(11)
        };
        parameterMapping.Steps.Add(new ValueMappingStep
        {
            Source = MappingSource.LogicalParameter,
            LogicalParameterId = parameter.Id
        });
        source.ParameterMappings.Add(parameterMapping);

        EventInstrument copy = EventInstrumentLibrary.Duplicate(project, source.Id);

        Assert.NotEqual(source.Id, copy.Id);
        Assert.NotEqual(source.SubVoices[0].Id, copy.SubVoices[0].Id);
        Assert.NotEqual(parameter.Id, copy.LogicalParameters[0].Id);
        Assert.NotEqual(function.Id, copy.MappingFunctions[0].Id);
        Assert.NotEqual(envelope.Id, copy.Envelopes[0].Id);
        Assert.Equal(copy.MappingFunctions[0].Id,
            copy.SubVoices[0].Events[0].ValueMappings[0].MappingFunctionId);
        Assert.Equal(copy.LogicalParameters[0].Id, copy.ParameterMappings[0].ParameterId);
        Assert.Equal(copy.SubVoices[0].Id, copy.ParameterMappings[0].SubVoiceId);
        copy.SubVoices[0].Events[0].Value = 99;
        Assert.Equal(10, source.SubVoices[0].Events[0].Value);
    }

    [Fact]
    public void DeletingReferencedInstrumentRequiresConfirmationAndPreservesTrackData()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Bound");
        LogicalTrack track = new() { Name = "Track", EventInstrumentId = instrument.Id };
        Segment segment = new() { LengthTicks = 480 };
        segment.Notes.Add(new LogicalNote { LengthTicks = 120 });
        track.Segments.Add(segment);
        project.Tracks.Add(track);

        _ = Assert.Throws<InvalidOperationException>(() =>
            EventInstrumentLibrary.Delete(project, instrument.Id, referencedDeletionConfirmed: false));
        IReadOnlyList<LogicalTrack> affected = EventInstrumentLibrary.Delete(
            project,
            instrument.Id,
            referencedDeletionConfirmed: true);

        Assert.Equal([track], affected);
        Assert.Null(track.EventInstrumentId);
        Assert.Equal("Bound", track.LastBoundEventInstrumentName);
        Assert.Single(track.Segments[0].Notes);
    }

    [Fact]
    public void SegmentDuplicateAndJoinPreserveContentCoordinatesAndRightPointWins()
    {
        MidoraId parameterId = MidoraId.New();
        Segment left = new() { ProjectStartTick = 100, LengthTicks = 100, ContentOffsetTick = 20 };
        LogicalNote leftNote = new() { StartTick = 20, LengthTicks = 50 };
        left.Notes.Add(leftNote);
        LogicalParameterLane leftLane = new() { ParameterId = parameterId };
        leftLane.Points.Add(new(220, 1)); // hidden point at absolute tick 300
        left.ParameterLanes.Add(leftLane);
        Segment right = new() { ProjectStartTick = 300, LengthTicks = 100 };
        LogicalNote rightNote = new() { StartTick = 0, LengthTicks = 50 };
        right.Notes.Add(rightNote);
        LogicalParameterLane rightLane = new() { ParameterId = parameterId };
        rightLane.Points.Add(new(0, 2));
        right.ParameterLanes.Add(rightLane);

        Segment duplicate = SegmentEditing.Duplicate(left);
        Segment joined = SegmentEditing.Join(left, right);

        Assert.NotEqual(left.Id, duplicate.Id);
        Assert.NotEqual(leftNote.Id, duplicate.Notes[0].Id);
        Assert.Equal(100, joined.ProjectStartTick);
        Assert.Equal(300, joined.LengthTicks);
        Assert.Equal(20, joined.ContentOffsetTick);
        Assert.Equal([20L, 220L], joined.Notes.Select(value => value.StartTick).ToArray());
        Assert.Equal([leftNote.Id, rightNote.Id], joined.Notes.Select(value => value.Id).ToArray());
        CurvePoint merged = Assert.Single(joined.ParameterLanes[0].Points);
        Assert.Equal(220, merged.Tick);
        Assert.Equal(2, merged.Value);
    }

    [Fact]
    public void MappingChainSupportsOrderedEditing()
    {
        MappingChain chain = new();
        ValueMappingStep first = new() { Operation = MappingOperation.Add };
        ValueMappingStep second = new() { Operation = MappingOperation.Multiply };
        ValueMappingStep replacement = new() { Operation = MappingOperation.Clamp };

        chain.Add(second);
        chain.Insert(0, first);
        chain[1] = replacement;

        Assert.Equal([first, replacement], chain.ToArray());
        Assert.True(chain.Remove(first));
        Assert.Equal([replacement], chain.ToArray());
        chain.Clear();
        Assert.Empty(chain);
    }
}
