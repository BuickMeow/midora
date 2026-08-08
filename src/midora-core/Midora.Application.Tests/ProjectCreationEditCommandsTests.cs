using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectCreationEditCommandsTests
{
    [Fact]
    public void UndoKeepsAllocatorHighWaterRedoKeepsIdentityAndBranchUsesHigherId()
    {
        MidoraProject project = new(480);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        long before = project.NextStableId;

        document.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Created"));
        LogicalTrack created = Assert.Single(project.Tracks);
        long afterCreate = project.NextStableId;

        Assert.Equal(before, created.Id.Value);
        Assert.Equal(before + 1, afterCreate);
        Assert.True(document.IsModified);
        AssertMatchesFull(compilation);

        document.Undo();

        Assert.Empty(project.Tracks);
        Assert.Equal(afterCreate, project.NextStableId);
        Assert.False(document.IsModified);
        Assert.True(document.CanRedo);
        AssertMatchesFull(compilation);

        document.Redo();

        Assert.Same(created, Assert.Single(project.Tracks));
        Assert.Equal(afterCreate, project.NextStableId);
        AssertMatchesFull(compilation);

        document.Undo();
        document.Execute(ProjectDomainEditCommands.CreateEventInstrumentFolder("Folder"));
        EventInstrumentLibraryFolder branch = Assert.Single(project.EventInstrumentFolders);

        Assert.False(document.CanRedo);
        Assert.True(branch.Id.Value > created.Id.Value);
        Assert.Equal(afterCreate + 1, project.NextStableId);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void InstrumentAndTrackDuplicationAreDeepStableAndUndoable()
    {
        MidoraProject project = new(480);
        EventInstrument source = EventInstrumentLibrary.Create(project, "Source");
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Amount",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1,
            DefaultValue = 0.5
        };
        source.LogicalParameters.Add(parameter);
        source.SubVoices[0].Events.Add(TemplateEvent.Note(project, 0, 120, 60, 100));
        LogicalTrack track = new(project)
        {
            Name = "Track",
            EventInstrumentId = source.Id,
            LastBoundEventInstrumentName = source.Name
        };
        Segment segment = new(project) { ProjectStartTick = 0, LengthTicks = 480 };
        segment.Notes.Add(new LogicalNote(project)
        {
            StartTick = 0,
            LengthTicks = 240,
            Note = 64,
            Velocity = 90
        });
        track.Segments.Add(segment);
        project.Tracks.Add(track);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.DuplicateEventInstrument(source.Id, "Copy"));
        EventInstrument instrumentCopy = project.EventInstruments[1];
        Assert.NotEqual(source.Id, instrumentCopy.Id);
        Assert.NotEqual(source.SubVoices[0].Id, instrumentCopy.SubVoices[0].Id);
        Assert.NotEqual(source.SubVoices[0].Events[0].Id, instrumentCopy.SubVoices[0].Events[0].Id);
        Assert.NotEqual(parameter.Id, instrumentCopy.LogicalParameters[0].Id);
        Assert.Equal(source.Id, track.EventInstrumentId);

        document.Execute(ProjectDomainEditCommands.DuplicateLogicalTrack(track.Id));
        LogicalTrack trackCopy = project.Tracks[1];
        Assert.NotEqual(track.Id, trackCopy.Id);
        Assert.Equal(track.EventInstrumentId, trackCopy.EventInstrumentId);
        Assert.NotEqual(segment.Id, trackCopy.Segments[0].Id);
        Assert.NotEqual(segment.Notes[0].Id, trackCopy.Segments[0].Notes[0].Id);
        trackCopy.Segments[0].Notes[0].Note = 72;
        Assert.Equal(64, segment.Notes[0].Note);
        trackCopy.Segments[0].Notes[0].Note = 64;
        AssertMatchesFull(compilation);

        MidoraId copyTrackId = trackCopy.Id;
        MidoraId copyInstrumentId = instrumentCopy.Id;
        long highWater = project.NextStableId;
        document.Undo();
        document.Undo();
        Assert.Single(project.Tracks);
        Assert.Single(project.EventInstruments);
        Assert.Equal(highWater, project.NextStableId);
        Assert.False(document.IsModified);

        document.Redo();
        document.Redo();
        Assert.Equal(copyInstrumentId, project.EventInstruments[1].Id);
        Assert.Equal(copyTrackId, project.Tracks[1].Id);
        Assert.Equal(highWater, project.NextStableId);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void SegmentContentCreationAndSplitPreserveIdsAndExactUndo()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Value",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1,
            DefaultValue = 0
        };
        instrument.LogicalParameters.Add(parameter);
        LogicalTrack track = new(project)
        {
            Name = "Track",
            EventInstrumentId = instrument.Id,
            LastBoundEventInstrumentName = instrument.Name
        };
        project.Tracks.Add(track);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.CreateSegment(track.Id, 0, 960));
        Segment original = Assert.Single(track.Segments);
        document.Execute(ProjectDomainEditCommands.CreateLogicalNote(
            original.Id,
            120,
            600,
            64,
            100));
        LogicalNote note = Assert.Single(original.Notes);
        document.Execute(ProjectDomainEditCommands.CreateLogicalParameterLane(
            original.Id,
            parameter.Id));
        LogicalParameterLane lane = Assert.Single(original.ParameterLanes);
        document.Execute(ProjectDomainEditCommands.CreateLogicalParameterPoint(
            original.Id,
            lane.Id,
            0,
            0.25,
            CurveInterpolation.Linear));
        document.Execute(ProjectDomainEditCommands.CreateLogicalParameterPoint(
            original.Id,
            lane.Id,
            720,
            0.75,
            CurveInterpolation.Linear));
        long beforeSplit = project.NextStableId;

        document.Execute(ProjectDomainEditCommands.SplitSegment(original.Id, 480));

        Assert.Equal(2, track.Segments.Count);
        Segment left = track.Segments[0];
        Segment right = track.Segments[1];
        Assert.Equal(original.Id, left.Id);
        Assert.NotEqual(original.Id, right.Id);
        Assert.Equal(480, left.LengthTicks);
        Assert.Equal(480, right.LengthTicks);
        Assert.Equal(note.Id, Assert.Single(left.Notes).Id);
        Assert.Equal(360, left.Notes[0].LengthTicks);
        Assert.Empty(right.Notes);
        Assert.Equal(lane.Id, Assert.Single(left.ParameterLanes).Id);
        Assert.NotEqual(lane.Id, Assert.Single(right.ParameterLanes).Id);
        Assert.Contains(right.ParameterLanes[0].Points, point => point.Tick == 480);
        Assert.True(project.NextStableId > beforeSplit);
        long afterSplit = project.NextStableId;
        MidoraId rightId = right.Id;
        AssertMatchesFull(compilation);

        document.Undo();

        Assert.Same(original, Assert.Single(track.Segments));
        Assert.Same(note, Assert.Single(original.Notes));
        Assert.Same(lane, Assert.Single(original.ParameterLanes));
        Assert.Equal(afterSplit, project.NextStableId);
        AssertMatchesFull(compilation);

        document.Redo();

        Assert.Equal(rightId, track.Segments[1].Id);
        Assert.Equal(afterSplit, project.NextStableId);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void ConductorCreationCommandsValidateConflictsAndRedoOriginalIds()
    {
        MidoraProject project = new(480);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.CreateTempo(480, 90m));
        document.Execute(ProjectDomainEditCommands.CreateTimeSignature(960, 3, 4));
        document.Execute(ProjectDomainEditCommands.CreateKeySignature(0, -2, true));
        document.Execute(ProjectDomainEditCommands.CreateProjectMarker(0, string.Empty));
        document.Execute(ProjectDomainEditCommands.CreateProjectMarker(0, "Intro"));
        document.Execute(ProjectDomainEditCommands.CreateProjectEndMarker(1_920));

        Assert.Equal(2, project.Conductor.Tempos.Count);
        Assert.Equal(2, project.Conductor.TimeSignatures.Count);
        Assert.Single(project.Conductor.KeySignatures);
        Assert.Equal(2, project.Conductor.Markers.Count);
        ProjectEndMarker endMarker = Assert.IsType<ProjectEndMarker>(project.Conductor.EndMarker);
        long highWater = project.NextStableId;
        AssertMatchesFull(compilation);

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.CreateTempo(480, 100m)));
        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.CreateProjectEndMarker(2_400)));
        Assert.Equal(highWater, project.NextStableId);

        document.Undo();
        Assert.Null(project.Conductor.EndMarker);
        Assert.Equal(highWater, project.NextStableId);
        document.Redo();
        Assert.Same(endMarker, project.Conductor.EndMarker);
        Assert.Equal(highWater, project.NextStableId);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void InvalidCreationIsRejectedBeforeAllocatingStableId()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalTrack track = new(project)
        {
            Name = "Track",
            EventInstrumentId = instrument.Id
        };
        project.Tracks.Add(track);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        long highWater = project.NextStableId;

        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.CreateSegment(track.Id, -1, 480)));
        Assert.Throws<ArgumentException>(() => document.Execute(
            ProjectDomainEditCommands.CreateEventInstrumentFolder("Unfiled")));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.CreateLogicalTrack("Track", insertionIndex: 2)));

        Assert.Equal(highWater, project.NextStableId);
        Assert.Empty(document.History);
        Assert.False(document.IsModified);
    }

    [Fact]
    public void SubVoiceTemplateAndCurveCreationAreDeepUndoableAndDeterministic()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        instrument.RequiresChannelIsolation = true;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.CreateSubVoice(
            instrument.Id,
            "Layer",
            rootNoteOverride: 64));
        SubVoice source = instrument.SubVoices[1];
        document.Execute(ProjectDomainEditCommands.CreateTemplateNote(
            instrument.Id,
            source.Id,
            0,
            120,
            60,
            100));
        TemplateEvent note = Assert.Single(source.Events);
        document.Execute(ProjectDomainEditCommands.CreateMappingStep(
            instrument.Id,
            note.ValueMappings.Id,
            constant: 0.5,
            targetMaximum: 127));
        document.Execute(ProjectDomainEditCommands.CreateValueCurve(
            instrument.Id,
            source.Id,
            MidiValueTarget.ControlChange(1)));
        ValueCurve curve = Assert.Single(source.Curves);
        document.Execute(ProjectDomainEditCommands.CreateValueCurvePoint(
            instrument.Id,
            source.Id,
            curve.Id,
            0,
            64));
        source.InitialState.Program = 12;

        document.Execute(ProjectDomainEditCommands.DuplicateSubVoice(
            instrument.Id,
            source.Id,
            "Layer Copy"));

        SubVoice copy = instrument.SubVoices[2];
        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal(12, copy.InitialState.Program);
        Assert.NotEqual(note.Id, Assert.Single(copy.Events).Id);
        Assert.NotEqual(note.ValueMappings.Id, copy.Events[0].ValueMappings.Id);
        Assert.NotEqual(
            Assert.Single(note.ValueMappings).Id,
            Assert.Single(copy.Events[0].ValueMappings).Id);
        Assert.NotEqual(curve.Id, Assert.Single(copy.Curves).Id);
        Assert.NotEqual(
            Assert.Single(curve.Points).Id,
            Assert.Single(copy.Curves[0].Points).Id);
        long highWater = project.NextStableId;
        MidoraId copyId = copy.Id;
        AssertMatchesFull(compilation);

        document.Undo();
        Assert.Equal(2, instrument.SubVoices.Count);
        Assert.Equal(highWater, project.NextStableId);
        document.Redo();
        Assert.Equal(copyId, instrument.SubVoices[2].Id);
        Assert.Equal(highWater, project.NextStableId);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void TemplateCreationReplacesSameTargetAndUndoRestoresExactEvent()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        SubVoice voice = instrument.SubVoices[0];
        TemplateEvent existing = TemplateEvent.ControlChange(project, 0, 7, 20);
        voice.Events.Add(existing);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.CreateTemplateControlChange(
            instrument.Id,
            voice.Id,
            0,
            7,
            100));

        TemplateEvent replacement = Assert.Single(voice.Events);
        Assert.NotSame(existing, replacement);
        Assert.Equal(100, replacement.Value);
        long highWater = project.NextStableId;
        AssertMatchesFull(compilation);

        document.Undo();
        Assert.Same(existing, Assert.Single(voice.Events));
        Assert.Equal(highWater, project.NextStableId);
        document.Redo();
        Assert.Same(replacement, Assert.Single(voice.Events));
        Assert.Equal(highWater, project.NextStableId);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void ParameterMappingEnvelopeAndFunctionCreationUseOneHistoryProtocol()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        instrument.RequiresChannelIsolation = true;
        SubVoice voice = instrument.SubVoices[0];
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.CreateLogicalParameter(
            instrument.Id,
            "Mode",
            LogicalParameterType.Enum,
            0,
            10,
            0,
            10,
            0));
        LogicalParameterDefinition parameter = Assert.Single(instrument.LogicalParameters);
        document.Execute(ProjectDomainEditCommands.CreateLogicalParameterEnumItem(
            instrument.Id,
            parameter.Id,
            "Off"));
        document.Execute(ProjectDomainEditCommands.CreateInstrumentEnvelope(
            instrument.Id,
            "Shape",
            attackTicks: 10,
            releaseTicks: 20));
        document.Execute(ProjectDomainEditCommands.CreateMappingFunction(
            instrument.Id,
            "Identity",
            "return value;",
            []));
        document.Execute(ProjectDomainEditCommands.DuplicateMappingFunction(
            instrument.Id,
            instrument.MappingFunctions[0].Id));
        document.Execute(ProjectDomainEditCommands.CreateLogicalParameterMapping(
            instrument.Id,
            parameter.Id,
            voice.Id,
            MidiValueTarget.ControlChange(7)));
        LogicalParameterMapping mapping = Assert.Single(instrument.ParameterMappings);
        document.Execute(ProjectDomainEditCommands.CreateMappingStep(
            instrument.Id,
            mapping.Steps.Id,
            MappingSource.LogicalParameter,
            MappingOperation.Override,
            logicalParameterId: parameter.Id,
            targetMinimum: 0,
            targetMaximum: 127));

        Assert.Equal("Off", Assert.Single(parameter.EnumItems).Name);
        Assert.Single(instrument.Envelopes);
        Assert.Equal(2, instrument.MappingFunctions.Count);
        Assert.Equal("Identity Copy", instrument.MappingFunctions[1].Name);
        ValueMappingStep step = Assert.Single(mapping.Steps);
        long highWater = project.NextStableId;
        AssertMatchesFull(compilation);

        document.Undo();
        Assert.Empty(mapping.Steps);
        Assert.Equal(highWater, project.NextStableId);
        document.Redo();
        Assert.Same(step, Assert.Single(mapping.Steps));
        Assert.Equal(highWater, project.NextStableId);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void RepeatedLaneCreationIsNoOpAndDoesNotAllocateIdentity()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Value",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1,
            DefaultValue = 0
        };
        instrument.LogicalParameters.Add(parameter);
        LogicalTrack track = new(project)
        {
            Name = "Track",
            EventInstrumentId = instrument.Id
        };
        Segment segment = new(project) { ProjectStartTick = 0, LengthTicks = 480 };
        track.Segments.Add(segment);
        project.Tracks.Add(track);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.CreateLogicalParameterLane(
            segment.Id,
            parameter.Id));
        long highWater = project.NextStableId;
        int historyCount = document.History.Count;

        document.Execute(ProjectDomainEditCommands.CreateLogicalParameterLane(
            segment.Id,
            parameter.Id));

        Assert.Single(segment.ParameterLanes);
        Assert.Equal(highWater, project.NextStableId);
        Assert.Equal(historyCount, document.History.Count);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void MappingChainPasteAllocatesNewChainAndStepsAndUndoRestoresTarget()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        SubVoice voice = instrument.SubVoices[0];
        TemplateEvent sourceEvent = TemplateEvent.Note(project, 0, 120, 60, 100);
        TemplateEvent targetEvent = TemplateEvent.Note(project, 240, 120, 62, 100);
        ValueMappingStep sourceStep = new(project)
        {
            Source = MappingSource.Constant,
            Operation = MappingOperation.Add,
            Constant = 5
        };
        ValueMappingStep targetStep = new(project)
        {
            Source = MappingSource.CurrentValue,
            Operation = MappingOperation.Multiply,
            Constant = 2
        };
        sourceEvent.ValueMappings.Add(sourceStep);
        targetEvent.ValueMappings.Add(targetStep);
        voice.Events.Add(sourceEvent);
        voice.Events.Add(targetEvent);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        MappingChain originalTarget = targetEvent.ValueMappings;
        long before = project.NextStableId;

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.PasteMappingChain(
                instrument.Id,
                sourceEvent.ValueMappings.Id,
                originalTarget.Id,
                nonEmptyReplacementConfirmed: false)));
        Assert.Equal(before, project.NextStableId);

        document.Execute(ProjectDomainEditCommands.PasteMappingChain(
            instrument.Id,
            sourceEvent.ValueMappings.Id,
            originalTarget.Id,
            nonEmptyReplacementConfirmed: true));

        MappingChain pasted = targetEvent.ValueMappings;
        ValueMappingStep pastedStep = Assert.Single(pasted);
        Assert.NotSame(originalTarget, pasted);
        Assert.NotEqual(originalTarget.Id, pasted.Id);
        Assert.NotEqual(sourceEvent.ValueMappings.Id, pasted.Id);
        Assert.NotEqual(sourceStep.Id, pastedStep.Id);
        Assert.Equal(sourceStep.Constant, pastedStep.Constant);
        long highWater = project.NextStableId;
        AssertMatchesFull(compilation);

        document.Undo();
        Assert.Same(originalTarget, targetEvent.ValueMappings);
        Assert.Same(targetStep, Assert.Single(originalTarget));
        Assert.Equal(highWater, project.NextStableId);
        document.Redo();
        Assert.Same(pasted, targetEvent.ValueMappings);
        Assert.Same(pastedStep, Assert.Single(pasted));
        Assert.Equal(highWater, project.NextStableId);
        AssertMatchesFull(compilation);
    }

    private static void AssertMatchesFull(ProjectCompilationSession compilation)
    {
        CanonicalCompiledResult full = new MidoraCompiler().CompileFull(
            compilation.Project,
            compilation.LastAttempt.Context.RequestedEndTick.HasValue
                ? new CompilationRequest
                {
                    Purpose = compilation.LastAttempt.Context.Purpose,
                    StartTick = compilation.LastAttempt.Context.StartTick,
                    EndTick = compilation.LastAttempt.Context.RequestedEndTick,
                    TreatWarningsAsErrors = compilation.LastAttempt.Context.TreatWarningsAsErrors
                }
                : new CompilationRequest
                {
                    Purpose = compilation.LastAttempt.Context.Purpose,
                    StartTick = compilation.LastAttempt.Context.StartTick,
                    TreatWarningsAsErrors = compilation.LastAttempt.Context.TreatWarningsAsErrors
                });
        Assert.Equal(full.Fingerprint, compilation.LastAttempt.Fingerprint);
        Assert.Equal(full.IsConsumable, compilation.LastAttempt.IsConsumable);
        Assert.Equal(full.Events, compilation.LastAttempt.Events);
        Assert.Equal(full.Diagnostics, compilation.LastAttempt.Diagnostics);
    }
}
