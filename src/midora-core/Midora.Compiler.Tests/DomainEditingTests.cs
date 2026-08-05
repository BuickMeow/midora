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
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "parameter",
            Minimum = 0,
            Maximum = 1
        };
        source.LogicalParameters.Add(parameter);
        InstrumentEnvelope envelope = new(project) { Name = "envelope", ReleaseTicks = 20 };
        source.Envelopes.Add(envelope);
        CSharpMappingFunction function = new(project) { Name = "mapping", Body = "return value;" };
        source.MappingFunctions.Add(function);
        TemplateEvent value = TemplateEvent.ControlChange(project, 0, 1, 10);
        value.ValueMappings.Add(new ValueMappingStep(project)
        {
            Operation = MappingOperation.CustomCSharp,
            MappingFunctionId = function.Id,
            EnvelopeId = envelope.Id,
            LogicalParameterId = parameter.Id
        });
        source.SubVoices[0].Events.Add(value);
        LogicalParameterMapping parameterMapping = new(project)
        {
            ParameterId = parameter.Id,
            SubVoiceId = source.SubVoices[0].Id,
            Target = MidiValueTarget.ControlChange(11)
        };
        parameterMapping.Steps.Add(new ValueMappingStep(project)
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
        LogicalTrack track = new(project) { Name = "Track", EventInstrumentId = instrument.Id };
        Segment segment = new(project) { LengthTicks = 480 };
        segment.Notes.Add(new LogicalNote(project) { LengthTicks = 120 });
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
        MidoraProject project = new(480);
        MidoraId parameterId = project.AllocateStableId();
        Segment left = new(project) { ProjectStartTick = 100, LengthTicks = 100, ContentOffsetTick = 20 };
        LogicalNote leftNote = new(project) { StartTick = 20, LengthTicks = 50 };
        left.Notes.Add(leftNote);
        LogicalParameterLane leftLane = new(project) { ParameterId = parameterId };
        leftLane.Points.Add(new(project, 220, 1)); // hidden point at absolute tick 300
        left.ParameterLanes.Add(leftLane);
        Segment right = new(project) { ProjectStartTick = 300, LengthTicks = 100 };
        LogicalNote rightNote = new(project) { StartTick = 0, LengthTicks = 50 };
        right.Notes.Add(rightNote);
        LogicalParameterLane rightLane = new(project) { ParameterId = parameterId };
        rightLane.Points.Add(new(project, 0, 2));
        right.ParameterLanes.Add(rightLane);

        Segment duplicate = SegmentEditing.Duplicate(project, left);
        Segment joined = SegmentEditing.Join(project, left, right);

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
        MidoraProject project = new(480);
        MappingChain chain = new(project);
        ValueMappingStep first = new(project) { Operation = MappingOperation.Add };
        ValueMappingStep second = new(project) { Operation = MappingOperation.Multiply };
        ValueMappingStep replacement = new(project) { Operation = MappingOperation.Clamp };

        chain.Add(second);
        chain.Insert(0, first);
        chain[1] = replacement;

        Assert.Equal([first, replacement], chain.ToArray());
        Assert.True(chain.Remove(first));
        Assert.Equal([replacement], chain.ToArray());
        chain.Clear();
        Assert.Empty(chain);
    }

    [Fact]
    public void ProjectStableIdsUseMonotonicCounterAndEndMarkerKeepsIdentityWhenMoved()
    {
        MidoraProject project = new(480);
        UInt128 before = project.NextStableId;
        EventInstrument first = EventInstrumentLibrary.Create(project, "First");
        UInt128 afterFirst = project.NextStableId;
        _ = EventInstrumentLibrary.Delete(project, first.Id, referencedDeletionConfirmed: true);
        EventInstrument second = EventInstrumentLibrary.Create(project, "Second");

        Assert.True(afterFirst > before);
        Assert.True(second.Id.ToSequence() >= afterFirst);
        Assert.True(project.NextStableId > second.Id.ToSequence());

        project.SetEndMarker(960);
        MidoraId endMarkerId = project.Conductor.EndMarker!.Id;
        project.SetEndMarker(1_920);

        Assert.Equal(endMarkerId, project.Conductor.EndMarker!.Id);
        Assert.Equal(1_920, project.Conductor.EndMarkerTick);
    }

    [Fact]
    public void StableIdUsesCanonicalPersistenceTextAndProtobufParts()
    {
        const ulong high = 0x0123456789abcdef;
        const ulong low = 0xfedcba9876543210;
        MidoraId id = MidoraId.FromParts(high, low);

        Assert.Equal("0123456789abcdeffedcba9876543210", id.ToString());
        Assert.Equal(high, id.High);
        Assert.Equal(low, id.Low);
        Assert.Equal(((UInt128)high << 64) | low, id.ToSequence());
        Assert.True(MidoraId.TryParseCanonical(id.ToString(), out MidoraId parsed));
        Assert.Equal(id, parsed);
        Assert.False(MidoraId.TryParseCanonical("0123456789ABCDEFFEDCBA9876543210", out _));
        Assert.False(MidoraId.TryParseCanonical("00000000000000000000000000000000", out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => MidoraId.FromParts(0, 0));
    }

    [Fact]
    public void DeletingFolderMovesContainedInstrumentsToUnfiled()
    {
        MidoraProject project = new(480);
        EventInstrumentLibraryFolder folder = EventInstrumentLibrary.CreateFolder(project, "  Keys  ");
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Piano");
        instrument.LibraryFolderId = folder.Id;

        IReadOnlyList<EventInstrument> moved = EventInstrumentLibrary.DeleteFolder(project, folder.Id);

        Assert.Equal("Keys", folder.Name);
        Assert.Equal([instrument], moved);
        Assert.Null(instrument.LibraryFolderId);
        Assert.Empty(project.EventInstrumentFolders);
        Assert.Throws<ArgumentException>(() => EventInstrumentLibrary.CreateFolder(project, "Unfiled"));
    }
}
