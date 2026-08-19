using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectObjectClipboardTests
{
    [Fact]
    public void LogicalTrackClipboardDeepCopiesContentAndPreservesValidBinding()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument" };
        project.EventInstruments.Add(instrument);
        project.ArrangementParents.Add(new(ArrangementParentKind.EventInstrument, instrument.Id));
        LogicalTrack source = new(project)
        {
            Name = "Source",
            EventInstrumentId = instrument.Id,
            LastBoundEventInstrumentName = instrument.Name,
            ColorOverride = new MidoraColor(40, 80, 120)
        };
        Segment segment = new(project)
        {
            ProjectStartTick = 120,
            LengthTicks = 240,
            ContentOffsetTick = 20
        };
        LogicalNote note = new(project)
        {
            StartTick = 30,
            LengthTicks = 60,
            Note = 65,
            Velocity = 95
        };
        segment.Notes.Add(note);
        source.Segments.Add(segment);
        LogicalTrack peer = new(project)
        {
            Name = "Peer",
            EventInstrumentId = instrument.Id,
            LastBoundEventInstrumentName = instrument.Name
        };
        project.Tracks.AddRange([source, peer]);
        instrument.LogicalTrackIds.AddRange([source.Id, peer.Id]);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        ProjectObjectClipboardPayload payload = ProjectObjectClipboard.CopyLogicalTrack(
            document,
            source.Id);
        source.Name = "Changed after copy";
        note.Note = 12;

        document.Execute(ProjectObjectClipboard.CreatePasteLogicalTrackCommand(
            document,
            payload,
            targetEventInstrumentId: instrument.Id,
            insertionIndex: 1));

        LogicalTrack copy = project.Tracks.Single(value => value.Id != source.Id && value.Id != peer.Id);
        Assert.Equal("Source", copy.Name);
        Assert.Equal(instrument.Id, copy.EventInstrumentId);
        Assert.Equal(source.ColorOverride, copy.ColorOverride);
        Assert.NotEqual(source.Id, copy.Id);
        Segment segmentCopy = Assert.Single(copy.Segments);
        LogicalNote noteCopy = Assert.Single(segmentCopy.Notes);
        Assert.NotEqual(segment.Id, segmentCopy.Id);
        Assert.NotEqual(note.Id, noteCopy.Id);
        Assert.Equal((120L, 240L, 20L), (
            segmentCopy.ProjectStartTick,
            segmentCopy.LengthTicks,
            segmentCopy.ContentOffsetTick));
        Assert.Equal((30L, 60L, 65, 95), (
            noteCopy.StartTick,
            noteCopy.LengthTicks,
            noteCopy.Note,
            noteCopy.Velocity));

        document.Undo();
        Assert.Equal([source, peer], project.Tracks);
        document.Redo();
        Assert.Same(copy, project.Tracks[^1]);
        Assert.Equal([source.Id, copy.Id, peer.Id], instrument.LogicalTrackIds);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void EventInstrumentClipboardIsDeepSnapshotAndPasteRemapsOwnedReferences()
    {
        MidoraProject project = new(480);
        EventInstrumentLibraryFolder folder = EventInstrumentLibrary.CreateFolder(project, "Leads");
        EventInstrument source = EventInstrumentLibrary.Create(project, "Lead");
        project.ArrangementParents.Add(new(
            ArrangementParentKind.EventInstrument,
            source.Id));
        source.LibraryFolderId = folder.Id;
        source.Description = "Snapshot description";
        source.Color = new MidoraColor(21, 91, 173);
        source.RequiresChannelIsolation = true;
        SubVoice voice = Assert.Single(source.SubVoices);
        voice.Name = "Main";
        voice.Events.Add(TemplateEvent.Note(project, 0, 240, 60, 100));
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Amount",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1,
            DisplayMinimum = 0,
            DisplayMaximum = 1,
            DefaultValue = 0.5
        };
        InstrumentEnvelope envelope = new(project) { Name = "Shape", PeakValue = 0.75 };
        CSharpMappingFunction function = new(project)
        {
            Name = "Identity",
            Body = "return value;"
        };
        source.LogicalParameters.Add(parameter);
        source.Envelopes.Add(envelope);
        source.MappingFunctions.Add(function);
        LogicalParameterMapping mapping = new(project)
        {
            ParameterId = parameter.Id,
            SubVoiceId = voice.Id,
            Target = MidiValueTarget.ControlChange(1)
        };
        mapping.Steps.Add(new ValueMappingStep(project)
        {
            Source = MappingSource.LogicalParameter,
            Operation = MappingOperation.Override,
            LogicalParameterId = parameter.Id
        });
        mapping.Steps.Add(new ValueMappingStep(project)
        {
            Source = MappingSource.Envelope,
            Operation = MappingOperation.Multiply,
            EnvelopeId = envelope.Id
        });
        mapping.Steps.Add(new ValueMappingStep(project)
        {
            Source = MappingSource.CurrentValue,
            Operation = MappingOperation.CustomCSharp,
            MappingFunctionId = function.Id
        });
        source.ParameterMappings.Add(mapping);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        ProjectObjectClipboardPayload payload = ProjectObjectClipboard.CopyEventInstrument(
            document,
            source.Id);
        parameter.Name = "Mutated after copy";
        function.Body = "return 0;";
        long firstPastedId = project.NextStableId;

        document.Execute(ProjectObjectClipboard.CreatePasteEventInstrumentCommand(
            document,
            payload));

        EventInstrument copy = project.EventInstruments.Single(value => value.Id != source.Id);
        Assert.True(copy.Id.Value >= firstPastedId);
        Assert.Equal("Lead Copy 1", copy.Name);
        Assert.Equal("Snapshot description", copy.Description);
        Assert.Equal(source.Color, copy.Color);
        Assert.Null(copy.LibraryFolderId);
        LogicalParameterDefinition parameterCopy = Assert.Single(copy.LogicalParameters);
        InstrumentEnvelope envelopeCopy = Assert.Single(copy.Envelopes);
        CSharpMappingFunction functionCopy = Assert.Single(copy.MappingFunctions);
        SubVoice voiceCopy = Assert.Single(copy.SubVoices);
        LogicalParameterMapping mappingCopy = Assert.Single(copy.ParameterMappings);
        Assert.Equal("Amount", parameterCopy.Name);
        Assert.Equal("return value;", functionCopy.Body);
        Assert.NotEqual(parameter.Id, parameterCopy.Id);
        Assert.NotEqual(envelope.Id, envelopeCopy.Id);
        Assert.NotEqual(function.Id, functionCopy.Id);
        Assert.NotEqual(voice.Id, voiceCopy.Id);
        Assert.Equal(parameterCopy.Id, mappingCopy.ParameterId);
        Assert.Equal(voiceCopy.Id, mappingCopy.SubVoiceId);
        Assert.Equal(parameterCopy.Id, mappingCopy.Steps[0].LogicalParameterId);
        Assert.Equal(envelopeCopy.Id, mappingCopy.Steps[1].EnvelopeId);
        Assert.Equal(functionCopy.Id, mappingCopy.Steps[2].MappingFunctionId);
        Assert.Single(document.History);

        document.Undo();
        Assert.DoesNotContain(copy, project.EventInstruments);
        document.Redo();
        Assert.Same(copy, project.EventInstruments.Single(value => value.Id == copy.Id));
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void SegmentPayloadIsImmutableDeepSnapshotWithFreshOwnedIds()
    {
        MidoraProject project = new(480);
        LogicalTrack sourceTrack = new(project) { Name = "Source" };
        LogicalTrack targetTrack = new(project) { Name = "Target" };
        project.Tracks.AddRange([sourceTrack, targetTrack]);
        MidoraId externalParameterId = MidoraId.FromSequence(900_000);
        Segment source = new(project)
        {
            ProjectStartTick = 10,
            LengthTicks = 100,
            ContentOffsetTick = 25
        };
        LogicalNote note = new(project)
        {
            StartTick = 4,
            LengthTicks = 20,
            Note = 61,
            Velocity = 99
        };
        LogicalParameterLane lane = new(project) { ParameterId = externalParameterId };
        CurvePoint point = new(project, 7, 0.25, CurveInterpolation.Step);
        lane.Points.Add(point);
        source.Notes.Add(note);
        source.ParameterLanes.Add(lane);
        sourceTrack.Segments.Add(source);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        ProjectObjectClipboardPayload payload = ProjectObjectClipboard.CopySegments(
            document,
            [source.Id],
            source.Id);
        document.Execute(ProjectDomainEditCommands.DeleteSegments([source.Id]));
        long beforePasteId = project.NextStableId;

        document.Execute(ProjectObjectClipboard.CreatePasteSegmentsCommand(
            document,
            payload,
            targetTrack.Id,
            editCursorTick: 500));

        Segment copy = Assert.Single(targetTrack.Segments);
        LogicalNote noteCopy = Assert.Single(copy.Notes);
        LogicalParameterLane laneCopy = Assert.Single(copy.ParameterLanes);
        CurvePoint pointCopy = Assert.Single(laneCopy.Points);
        Assert.Equal((500L, 100L, 25L),
            (copy.ProjectStartTick, copy.LengthTicks, copy.ContentOffsetTick));
        Assert.Equal((4L, 20L, 61, 99),
            (noteCopy.StartTick, noteCopy.LengthTicks, noteCopy.Note, noteCopy.Velocity));
        Assert.Equal((7L, 0.25, CurveInterpolation.Step),
            (pointCopy.Tick, pointCopy.Value, pointCopy.Interpolation));
        Assert.Equal(externalParameterId, laneCopy.ParameterId);
        Assert.All(
            new[] { copy.Id, noteCopy.Id, laneCopy.Id, pointCopy.Id },
            id => Assert.True(id.Value >= beforePasteId));
        Assert.Equal(2, document.History.Count);
        AssertMatchesFull(compilation);

        document.Undo();
        Assert.Empty(targetTrack.Segments);
        document.Redo();
        Assert.Same(copy, Assert.Single(targetTrack.Segments));
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void SegmentPastePreservesRelativeTrackAndTimeOffsets()
    {
        MidoraProject project = new(480);
        LogicalTrack targetPrimary = new(project) { Name = "Target 1" };
        LogicalTrack targetSecondary = new(project) { Name = "Target 2" };
        LogicalTrack sourcePrimary = new(project) { Name = "Source 1" };
        LogicalTrack sourceSecondary = new(project) { Name = "Source 2" };
        project.Tracks.AddRange(
            [targetPrimary, targetSecondary, sourcePrimary, sourceSecondary]);
        Segment first = new(project) { ProjectStartTick = 100, LengthTicks = 60 };
        Segment second = new(project) { ProjectStartTick = 220, LengthTicks = 60 };
        sourcePrimary.Segments.Add(first);
        sourceSecondary.Segments.Add(second);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        ProjectObjectClipboardPayload payload = ProjectObjectClipboard.CopySegments(
            document,
            [second.Id, first.Id],
            first.Id);

        document.Execute(ProjectObjectClipboard.CreatePasteSegmentsCommand(
            document,
            payload,
            targetPrimary.Id,
            editCursorTick: 500));

        Assert.Equal(500, Assert.Single(targetPrimary.Segments).ProjectStartTick);
        Assert.Equal(620, Assert.Single(targetSecondary.Segments).ProjectStartTick);
        Assert.Single(document.History);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void InvalidSegmentTargetRejectsBeforeIdsHistoryOrMutation()
    {
        MidoraProject project = new(480);
        LogicalTrack target = new(project) { Name = "Target" };
        LogicalTrack sourcePrimary = new(project) { Name = "Source 1" };
        LogicalTrack sourceSecondary = new(project) { Name = "Source 2" };
        project.Tracks.AddRange([sourcePrimary, sourceSecondary, target]);
        Segment first = new(project) { ProjectStartTick = 100, LengthTicks = 60 };
        Segment second = new(project) { ProjectStartTick = 220, LengthTicks = 60 };
        sourcePrimary.Segments.Add(first);
        sourceSecondary.Segments.Add(second);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        ProjectObjectClipboardPayload payload = ProjectObjectClipboard.CopySegments(
            document,
            [first.Id, second.Id],
            first.Id);
        long nextStableId = project.NextStableId;

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectObjectClipboard.CreatePasteSegmentsCommand(
                document,
                payload,
                target.Id,
                editCursorTick: 500)));

        Assert.Empty(target.Segments);
        Assert.Equal(nextStableId, project.NextStableId);
        Assert.Empty(document.History);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void LogicalNotePayloadSurvivesCutAndEachPasteAllocatesNewIds()
    {
        MidoraProject project = new(480);
        LogicalTrack track = new(project) { Name = "Track" };
        Segment source = new(project) { LengthTicks = 480 };
        Segment target = new(project) { ProjectStartTick = 480, LengthTicks = 480 };
        LogicalNote first = new(project)
        {
            StartTick = 20,
            LengthTicks = 40,
            Note = 60,
            Velocity = 80
        };
        LogicalNote second = new(project)
        {
            StartTick = 140,
            LengthTicks = 60,
            Note = 64,
            Velocity = 100
        };
        source.Notes.AddRange([first, second]);
        track.Segments.AddRange([source, target]);
        project.Tracks.Add(track);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        ProjectObjectClipboardPayload payload = ProjectObjectClipboard.CopyLogicalNotes(
            document,
            source.Id,
            [second.Id, first.Id]);
        document.Execute(ProjectDomainEditCommands.DeleteLogicalNotes(
            source.Id,
            [first.Id, second.Id]));

        document.Execute(ProjectObjectClipboard.CreatePasteLogicalNotesCommand(
            document,
            payload,
            target.Id,
            editCursorTick: 30));
        LogicalNote[] firstPaste = target.Notes.ToArray();
        document.Execute(ProjectObjectClipboard.CreatePasteLogicalNotesCommand(
            document,
            payload,
            target.Id,
            editCursorTick: 300));
        LogicalNote[] secondPaste = target.Notes.Skip(2).ToArray();

        Assert.Equal([30L, 150L], firstPaste.Select(value => value.StartTick));
        Assert.Equal([300L, 420L], secondPaste.Select(value => value.StartTick));
        Assert.Equal([60, 64], firstPaste.Select(value => value.Note));
        Assert.Empty(firstPaste.Select(value => value.Id)
            .Intersect(secondPaste.Select(value => value.Id)));
        Assert.Equal(3, document.History.Count);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void PayloadKindAndProjectSessionAreStrictlyValidated()
    {
        MidoraProject sourceProject = new(480);
        LogicalTrack sourceTrack = new(sourceProject) { Name = "Source" };
        Segment sourceSegment = new(sourceProject) { LengthTicks = 100 };
        sourceTrack.Segments.Add(sourceSegment);
        sourceProject.Tracks.Add(sourceTrack);
        using ProjectCompilationSession sourceCompilation = new(sourceProject);
        ProjectDocumentSession sourceDocument = PersistedDocument(sourceCompilation);
        ProjectObjectClipboardPayload payload = ProjectObjectClipboard.CopySegments(
            sourceDocument,
            [sourceSegment.Id],
            sourceSegment.Id);

        Assert.Throws<ArgumentException>(() =>
            ProjectObjectClipboard.CreatePasteLogicalNotesCommand(
                sourceDocument,
                payload,
                sourceSegment.Id,
                editCursorTick: 0));

        MidoraProject otherProject = new(480);
        LogicalTrack otherTrack = new(otherProject) { Name = "Other" };
        otherProject.Tracks.Add(otherTrack);
        using ProjectCompilationSession otherCompilation = new(otherProject);
        ProjectDocumentSession otherDocument = PersistedDocument(otherCompilation);
        Assert.Throws<InvalidOperationException>(() =>
            ProjectObjectClipboard.CreatePasteSegmentsCommand(
                otherDocument,
                payload,
                otherTrack.Id,
                editCursorTick: 0));
    }

    [Fact]
    public void LogicalParameterLaneAndContentRequireExactTargetAndAlignEarliestPoint()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument" };
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Amount",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1
        };
        instrument.LogicalParameters.Add(parameter);
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project)
        {
            Name = "Track",
            EventInstrumentId = instrument.Id
        };
        Segment source = new(project) { LengthTicks = 480 };
        Segment target = new(project) { ProjectStartTick = 480, LengthTicks = 480 };
        LogicalParameterLane sourceLane = new(project) { ParameterId = parameter.Id };
        CurvePoint first = new(project, 10, 0.25, CurveInterpolation.Step);
        CurvePoint second = new(project, 30, 0.75);
        sourceLane.Points.AddRange([first, second]);
        source.ParameterLanes.Add(sourceLane);
        track.Segments.AddRange([source, target]);
        project.Tracks.Add(track);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        ProjectObjectClipboardPayload lanePayload =
            ProjectObjectClipboard.CopyLogicalParameterLane(
                document,
                source.Id,
                sourceLane.Id);
        ProjectObjectClipboardPayload contentPayload =
            ProjectObjectClipboard.CopyLogicalParameterLaneContent(
                document,
                source.Id,
                sourceLane.Id,
                [second.Id, first.Id]);
        document.Execute(ProjectObjectClipboard.CreatePasteLogicalParameterLaneCommand(
            document,
            lanePayload,
            target.Id,
            editCursorTick: 100));

        LogicalParameterLane targetLane = Assert.Single(target.ParameterLanes);
        Assert.NotEqual(sourceLane.Id, targetLane.Id);
        Assert.Equal(parameter.Id, targetLane.ParameterId);
        Assert.Equal([100L, 120L], targetLane.Points.Select(value => value.Tick));
        Assert.Equal([0.25, 0.75], targetLane.Points.Select(value => value.Value));

        document.Execute(
            ProjectObjectClipboard.CreatePasteLogicalParameterLaneContentCommand(
                document,
                contentPayload,
                target.Id,
                targetLane.Id,
                editCursorTick: 200));

        Assert.Equal([100L, 120L, 200L, 220L],
            targetLane.Points.Select(value => value.Tick));
        Assert.Equal(2, document.History.Count);
        AssertMatchesFull(compilation);

        long nextStableId = project.NextStableId;
        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectObjectClipboard.CreatePasteLogicalParameterLaneCommand(
                document,
                lanePayload,
                target.Id,
                editCursorTick: 300)));
        Assert.Equal(nextStableId, project.NextStableId);
        Assert.Equal(2, document.History.Count);
    }

    [Fact]
    public void ConductorPayloadCopiesOrdinaryEventsAtomicallyAndExcludesEndMarker()
    {
        MidoraProject project = new(480);
        TempoChange tempo = new(project, 100, 90m);
        TimeSignatureChange timeSignature = new(project, 200, 3, 4);
        KeySignatureChange keySignature = new(project, 220, -2, true);
        ProjectMarker firstMarker = new(project, 250, "A");
        ProjectMarker secondMarker = new(project, 250, "B");
        project.Conductor.Tempos.Add(tempo);
        project.Conductor.TimeSignatures.Add(timeSignature);
        project.Conductor.KeySignatures.Add(keySignature);
        project.Conductor.Markers.AddRange([firstMarker, secondMarker]);
        project.SetEndMarker(1_000);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        ProjectObjectClipboardPayload payload = ProjectObjectClipboard.CopyConductorEvents(
            document,
            [secondMarker.Id, keySignature.Id, tempo.Id, timeSignature.Id, firstMarker.Id]);

        document.Execute(ProjectObjectClipboard.CreatePasteConductorEventsCommand(
            document,
            payload,
            editCursorTick: 500));

        Assert.Contains(project.Conductor.Tempos,
            value => value.Tick == 500 && value.BeatsPerMinute == 90m);
        Assert.Contains(project.Conductor.TimeSignatures,
            value => value.Tick == 600 && value.Numerator == 3 && value.Denominator == 4);
        Assert.Contains(project.Conductor.KeySignatures,
            value => value.Tick == 620 && value.SharpsFlats == -2 && value.IsMinor);
        Assert.Equal(["A", "B"], project.Conductor.Markers
            .Where(value => value.Tick == 650)
            .Select(value => value.Name)
            .Order(StringComparer.Ordinal));
        Assert.Single(document.History);
        AssertMatchesFull(compilation);

        long nextStableId = project.NextStableId;
        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectObjectClipboard.CreatePasteConductorEventsCommand(
                document,
                payload,
                editCursorTick: 500)));
        Assert.Equal(nextStableId, project.NextStableId);
        Assert.Single(document.History);

        Assert.Throws<ArgumentException>(() => ProjectObjectClipboard.CopyConductorEvents(
            document,
            [project.Conductor.EndMarker!.Id]));
    }

    [Fact]
    public void SubVoiceTimelinePasteUsesTheTargetSubVoiceSharedMappingDefinition()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            TemplateLengthTicks = 100
        };
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Amount",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1
        };
        InstrumentEnvelope envelope = new(project) { Name = "Envelope" };
        CSharpMappingFunction function = new(project)
        {
            Name = "Function",
            Body = "return value;"
        };
        SubVoice source = new(project) { Name = "Source" };
        SubVoice target = new(project) { Name = "Target" };
        TemplateEvent templateEvent = TemplateEvent.ControlChange(project, 10, 1, 64);
        templateEvent.ValueMappings.IsEnabled = false;
        ValueMappingStep sourceStep = new(project)
        {
            Source = MappingSource.LogicalParameter,
            Operation = MappingOperation.CustomCSharp,
            LogicalParameterId = parameter.Id,
            EnvelopeId = envelope.Id,
            MappingFunctionId = function.Id,
            Constant = 0.25,
            SourceMinimum = -1,
            SourceMaximum = 1,
            TargetMinimum = 0,
            TargetMaximum = 127,
            InputOverflow = MappingInputOverflow.Extrapolate,
            DivideByZero = DivideByZeroPolicy.Zero
        };
        templateEvent.ValueMappings.Add(sourceStep);
        templateEvent.ValueTargetSettings.Rounding = MappingRounding.Floor;
        templateEvent.ValueTargetSettings.Overflow = MappingOverflow.Clamp;
        source.Events.Add(templateEvent);
        instrument.LogicalParameters.Add(parameter);
        instrument.Envelopes.Add(envelope);
        instrument.MappingFunctions.Add(function);
        instrument.SubVoices.AddRange([source, target]);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        ProjectObjectClipboardPayload payload = ProjectObjectClipboard.CopySubVoiceTimelineEvents(
            document,
            instrument.Id,
            source.Id,
            [templateEvent.Id]);
        long beforePasteId = project.NextStableId;

        document.Execute(ProjectObjectClipboard.CreatePasteSubVoiceTimelineEventsCommand(
            document,
            payload,
            instrument.Id,
            target.Id,
            editCursorTick: 200));

        TemplateEvent copy = Assert.Single(target.Events);
        Assert.Equal(200, copy.Tick);
        Assert.NotEqual(templateEvent.Id, copy.Id);
        Assert.NotEqual(templateEvent.ValueMappings.Id, copy.ValueMappings.Id);
        Assert.True(copy.Id.Value >= beforePasteId);
        Assert.Empty(copy.ValueMappings);
        Assert.True(copy.ValueMappings.IsEnabled);
        Assert.Equal((MappingRounding.Round, MappingOverflow.Fail),
            (copy.ValueTargetSettings.Rounding, copy.ValueTargetSettings.Overflow));
        Assert.Equal(201, instrument.TemplateLengthTicks);
        Assert.Single(document.History);
        AssertMatchesFull(compilation);

        document.Undo();
        Assert.Empty(target.Events);
        Assert.Equal(100, instrument.TemplateLengthTicks);
        document.Redo();
        Assert.Same(copy, Assert.Single(target.Events));
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void WholeSubVoicePasteAcrossInstrumentsCopiesAndRemapsMappingDependencies()
    {
        MidoraProject project = new(480);
        EventInstrument sourceInstrument = new(project) { Name = "Source", TemplateLengthTicks = 480 };
        EventInstrument targetInstrument = new(project) { Name = "Target", TemplateLengthTicks = 480 };
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Amount",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1,
            DisplayMinimum = 0,
            DisplayMaximum = 1,
            DefaultValue = 0.5
        };
        InstrumentEnvelope envelope = new(project) { Name = "Envelope", AttackTicks = 12 };
        CSharpMappingFunction function = new(project) { Name = "Function", Body = "return value;" };
        SubVoice source = new(project) { Name = "Layer", RootNoteOverride = 72 };
        source.InitialState.Controllers.Add(11, 90);
        TemplateEvent controller = TemplateEvent.ControlChange(project, 20, 11, 64);
        ValueMappingStep step = new(project)
        {
            Source = MappingSource.LogicalParameter,
            Operation = MappingOperation.CustomCSharp,
            LogicalParameterId = parameter.Id,
            EnvelopeId = envelope.Id,
            MappingFunctionId = function.Id
        };
        controller.ValueMappings.Add(step);
        source.Events.Add(controller);
        sourceInstrument.LogicalParameters.Add(parameter);
        sourceInstrument.Envelopes.Add(envelope);
        sourceInstrument.MappingFunctions.Add(function);
        sourceInstrument.SubVoices.Add(source);
        targetInstrument.SubVoices.Add(new(project) { Name = "Existing" });
        project.EventInstruments.AddRange([sourceInstrument, targetInstrument]);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        ProjectObjectClipboardPayload payload = ProjectObjectClipboard.CopySubVoice(
            document,
            sourceInstrument.Id,
            source.Id);
        long firstCopiedId = project.NextStableId;

        document.Execute(ProjectObjectClipboard.CreatePasteSubVoiceCommand(
            document,
            payload,
            targetInstrument.Id,
            insertionIndex: 1));

        SubVoice copy = targetInstrument.SubVoices[1];
        TemplateEvent eventCopy = Assert.Single(copy.Events);
        ValueMappingStep stepCopy = Assert.Single(eventCopy.ValueMappings);
        LogicalParameterDefinition parameterCopy = Assert.Single(targetInstrument.LogicalParameters);
        InstrumentEnvelope envelopeCopy = Assert.Single(targetInstrument.Envelopes);
        CSharpMappingFunction functionCopy = Assert.Single(targetInstrument.MappingFunctions);
        Assert.Equal(("Layer", 72), (copy.Name, copy.RootNoteOverride));
        Assert.Equal(90, copy.InitialState.Controllers[11]);
        Assert.All(
            new[] { copy.Id, eventCopy.Id, stepCopy.Id, parameterCopy.Id, envelopeCopy.Id, functionCopy.Id },
            id => Assert.True(id.Value >= firstCopiedId));
        Assert.Equal(parameterCopy.Id, stepCopy.LogicalParameterId);
        Assert.Equal(envelopeCopy.Id, stepCopy.EnvelopeId);
        Assert.Equal(functionCopy.Id, stepCopy.MappingFunctionId);
        Assert.NotEqual(parameter.Id, parameterCopy.Id);
        Assert.Single(document.History);
        AssertMatchesFull(compilation);

        document.Undo();
        Assert.Single(targetInstrument.SubVoices);
        Assert.Empty(targetInstrument.LogicalParameters);
        Assert.Empty(targetInstrument.Envelopes);
        Assert.Empty(targetInstrument.MappingFunctions);
        AssertMatchesFull(compilation);

        document.Redo();
        Assert.Same(copy, targetInstrument.SubVoices[1]);
        Assert.Same(parameterCopy, Assert.Single(targetInstrument.LogicalParameters));
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void SubVoiceAndValueCurvePasteRejectIncompatibleTargetsBeforeMutation()
    {
        MidoraProject project = new(480);
        EventInstrument sourceInstrument = new(project) { Name = "Source Instrument" };
        EventInstrument otherInstrument = new(project) { Name = "Other Instrument" };
        SubVoice sourceVoice = new(project) { Name = "Source" };
        SubVoice targetVoice = new(project) { Name = "Target" };
        SubVoice otherVoice = new(project) { Name = "Other" };
        TemplateEvent templateEvent = TemplateEvent.Program(project, 10, 5);
        sourceVoice.Events.Add(templateEvent);
        ValueCurve sourceCurve = new(project) { Target = MidiValueTarget.ControlChange(1) };
        CurvePoint sourcePoint = new(project, 20, 32);
        sourceCurve.Points.Add(sourcePoint);
        sourceVoice.Curves.Add(sourceCurve);
        ValueCurve targetCurve = new(project) { Target = MidiValueTarget.ControlChange(1) };
        ValueCurve incompatibleCurve = new(project) { Target = MidiValueTarget.ControlChange(2) };
        targetVoice.Curves.AddRange([targetCurve, incompatibleCurve]);
        sourceInstrument.SubVoices.AddRange([sourceVoice, targetVoice]);
        otherInstrument.SubVoices.Add(otherVoice);
        project.EventInstruments.AddRange([sourceInstrument, otherInstrument]);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        ProjectObjectClipboardPayload events =
            ProjectObjectClipboard.CopySubVoiceTimelineEvents(
                document,
                sourceInstrument.Id,
                sourceVoice.Id,
                [templateEvent.Id]);
        ProjectObjectClipboardPayload curveContent =
            ProjectObjectClipboard.CopyValueCurveContent(
                document,
                sourceInstrument.Id,
                sourceVoice.Id,
                sourceCurve.Id,
                [sourcePoint.Id]);
        long beforeInvalidPasteId = project.NextStableId;

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectObjectClipboard.CreatePasteSubVoiceTimelineEventsCommand(
                document,
                events,
                otherInstrument.Id,
                otherVoice.Id,
                editCursorTick: 100)));
        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectObjectClipboard.CreatePasteValueCurveContentCommand(
                document,
                curveContent,
                sourceInstrument.Id,
                targetVoice.Id,
                incompatibleCurve.Id,
                editCursorTick: 100)));
        Assert.Equal(beforeInvalidPasteId, project.NextStableId);
        Assert.Empty(document.History);

        document.Execute(ProjectObjectClipboard.CreatePasteValueCurveContentCommand(
            document,
            curveContent,
            sourceInstrument.Id,
            targetVoice.Id,
            targetCurve.Id,
            editCursorTick: 100));
        CurvePoint pasted = Assert.Single(targetCurve.Points);
        Assert.Equal((100L, 32d), (pasted.Tick, pasted.Value));
        Assert.NotEqual(sourcePoint.Id, pasted.Id);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void CutPreparationDoesNotDeleteUntilClipboardWriteSucceeds()
    {
        MidoraProject project = new(480);
        LogicalTrack track = new(project) { Name = "Track" };
        Segment source = new(project) { LengthTicks = 480 };
        Segment target = new(project) { ProjectStartTick = 480, LengthTicks = 480 };
        LogicalNote note = new(project)
        {
            StartTick = 20,
            LengthTicks = 40,
            Note = 60,
            Velocity = 100
        };
        source.Notes.Add(note);
        track.Segments.AddRange([source, target]);
        project.Tracks.Add(track);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        ProjectObjectClipboardCutPreparation cut =
            ProjectObjectClipboard.PrepareCutLogicalNotes(
                document,
                source.Id,
                [note.Id]);

        Assert.Same(note, Assert.Single(source.Notes));
        Assert.Empty(document.History);

        document.Execute(cut.DeleteAfterSuccessfulClipboardWrite);
        Assert.Empty(source.Notes);
        Assert.Single(document.History);

        document.Execute(ProjectObjectClipboard.CreatePasteLogicalNotesCommand(
            document,
            cut.Payload,
            target.Id,
            editCursorTick: 100));
        LogicalNote pasted = Assert.Single(target.Notes);
        Assert.NotEqual(note.Id, pasted.Id);
        Assert.Equal(2, document.History.Count);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void CompatibleOrderedMappingChainUsesSnapshotAndFreshOwnedIds()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument" };
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Amount",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1
        };
        SubVoice voice = new(project) { Name = "Voice" };
        TemplateEvent source = TemplateEvent.ControlChange(project, 10, 1, 64);
        TemplateEvent target = TemplateEvent.ControlChange(project, 20, 2, 64);
        ValueMappingStep step = new(project)
        {
            Source = MappingSource.LogicalParameter,
            Operation = MappingOperation.Remap,
            LogicalParameterId = parameter.Id,
            SourceMinimum = 0,
            SourceMaximum = 1,
            TargetMinimum = 0,
            TargetMaximum = 127
        };
        source.ValueMappings.Add(step);
        voice.Events.AddRange([source, target]);
        instrument.LogicalParameters.Add(parameter);
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        MappingChain originalTarget = target.ValueMappings;

        ProjectObjectClipboardCutPreparation cut =
            ProjectObjectClipboard.PrepareCutMappingChain(
                document,
                instrument.Id,
                source.ValueMappings.Id);
        Assert.Single(source.ValueMappings);
        document.Execute(cut.DeleteAfterSuccessfulClipboardWrite);
        Assert.Empty(source.ValueMappings);

        document.Execute(ProjectObjectClipboard.CreatePasteMappingChainCommand(
            document,
            cut.Payload,
            instrument.Id,
            originalTarget.Id,
            nonEmptyReplacementConfirmed: false));

        MappingChain copy = target.ValueMappings;
        ValueMappingStep stepCopy = Assert.Single(copy);
        Assert.NotSame(originalTarget, copy);
        Assert.NotEqual(originalTarget.Id, copy.Id);
        Assert.NotEqual(source.ValueMappings.Id, copy.Id);
        Assert.NotEqual(step.Id, stepCopy.Id);
        Assert.Equal(parameter.Id, stepCopy.LogicalParameterId);
        Assert.Equal(2, document.History.Count);
        AssertMatchesFull(compilation);

        document.Undo();
        Assert.Same(originalTarget, target.ValueMappings);
        document.Redo();
        Assert.Same(copy, target.ValueMappings);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void EveryRemainingCutKindIsTwoPhaseAndUndoRestoresTheExactSourceObject()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument" };
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Amount",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1,
            DefaultValue = 0.5
        };
        SubVoice voice = new(project) { Name = "Voice" };
        TemplateEvent templateEvent = TemplateEvent.ControlChange(project, 10, 1, 64);
        ValueCurve curve = new(project)
        {
            Target = MidiValueTarget.ControlChange(2)
        };
        CurvePoint curvePoint = new(project, 20, 32);
        curve.Points.Add(curvePoint);
        voice.Events.Add(templateEvent);
        voice.Curves.Add(curve);
        instrument.LogicalParameters.Add(parameter);
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);

        LogicalTrack track = new(project)
        {
            Name = "Track",
            EventInstrumentId = instrument.Id,
            LastBoundEventInstrumentName = instrument.Name
        };
        Segment segment = new(project) { LengthTicks = 480 };
        LogicalParameterLane lane = new(project) { ParameterId = parameter.Id };
        CurvePoint lanePoint = new(project, 30, 0.75);
        lane.Points.Add(lanePoint);
        segment.ParameterLanes.Add(lane);
        track.Segments.Add(segment);
        project.Tracks.Add(track);

        ProjectMarker marker = new(project, 120, "Marker");
        project.Conductor.Markers.Add(marker);

        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        ProjectObjectClipboardCutPreparation laneContent =
            ProjectObjectClipboard.PrepareCutLogicalParameterLaneContent(
                document,
                segment.Id,
                lane.Id,
                [lanePoint.Id]);
        Assert.Equal(ProjectObjectClipboardKind.LogicalParameterLaneContent, laneContent.Payload.Kind);
        Assert.Same(lanePoint, Assert.Single(lane.Points));
        document.Execute(laneContent.DeleteAfterSuccessfulClipboardWrite);
        Assert.Empty(lane.Points);
        document.Undo();
        Assert.Same(lanePoint, Assert.Single(lane.Points));

        ProjectObjectClipboardCutPreparation wholeLane =
            ProjectObjectClipboard.PrepareCutLogicalParameterLane(
                document,
                segment.Id,
                lane.Id);
        Assert.Equal(ProjectObjectClipboardKind.LogicalParameterLane, wholeLane.Payload.Kind);
        Assert.Same(lane, Assert.Single(segment.ParameterLanes));
        document.Execute(wholeLane.DeleteAfterSuccessfulClipboardWrite);
        Assert.Empty(segment.ParameterLanes);
        document.Undo();
        Assert.Same(lane, Assert.Single(segment.ParameterLanes));

        ProjectObjectClipboardCutPreparation timeline =
            ProjectObjectClipboard.PrepareCutSubVoiceTimelineEvents(
                document,
                instrument.Id,
                voice.Id,
                [templateEvent.Id]);
        Assert.Equal(ProjectObjectClipboardKind.SubVoiceTimelineEvents, timeline.Payload.Kind);
        Assert.Same(templateEvent, Assert.Single(voice.Events));
        document.Execute(timeline.DeleteAfterSuccessfulClipboardWrite);
        Assert.Empty(voice.Events);
        document.Undo();
        Assert.Same(templateEvent, Assert.Single(voice.Events));

        ProjectObjectClipboardCutPreparation curveContent =
            ProjectObjectClipboard.PrepareCutValueCurveContent(
                document,
                instrument.Id,
                voice.Id,
                curve.Id,
                [curvePoint.Id]);
        Assert.Equal(ProjectObjectClipboardKind.ValueCurveContent, curveContent.Payload.Kind);
        Assert.Same(curvePoint, Assert.Single(curve.Points));
        document.Execute(curveContent.DeleteAfterSuccessfulClipboardWrite);
        Assert.Empty(curve.Points);
        document.Undo();
        Assert.Same(curvePoint, Assert.Single(curve.Points));

        ProjectObjectClipboardCutPreparation conductor =
            ProjectObjectClipboard.PrepareCutConductorEvents(document, [marker.Id]);
        Assert.Equal(ProjectObjectClipboardKind.ConductorEvents, conductor.Payload.Kind);
        Assert.Same(marker, Assert.Single(project.Conductor.Markers));
        document.Execute(conductor.DeleteAfterSuccessfulClipboardWrite);
        Assert.Empty(project.Conductor.Markers);
        document.Undo();
        Assert.Same(marker, Assert.Single(project.Conductor.Markers));

        ProjectObjectClipboardCutPreparation segments =
            ProjectObjectClipboard.PrepareCutSegments(document, [segment.Id], segment.Id);
        Assert.Equal(ProjectObjectClipboardKind.Segments, segments.Payload.Kind);
        Assert.Same(segment, Assert.Single(track.Segments));
        document.Execute(segments.DeleteAfterSuccessfulClipboardWrite);
        Assert.Empty(track.Segments);
        document.Undo();
        Assert.Same(segment, Assert.Single(track.Segments));

        Assert.False(document.IsModified);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void InstrumentDefinitionClipboardCreatesIndependentAtomicCopies()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            RequiresChannelIsolation = true
        };
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Mode",
            Type = LogicalParameterType.Enum,
            Minimum = 0,
            Maximum = 1,
            DisplayMinimum = 0,
            DisplayMaximum = 1,
            DefaultValue = 0
        };
        parameter.EnumItems.AddRange(
        [
            new(project) { Name = "Off", Value = 0 },
            new(project) { Name = "On", Value = 1 }
        ]);
        InstrumentEnvelope envelope = new(project)
        {
            Name = "Shape",
            AttackTicks = 12,
            PeakValue = 0.8,
            SustainValue = 0.5,
            ReleaseTicks = 24
        };
        CSharpMappingFunction function = new(project)
        {
            Name = "Scale",
            Body = "return value * 2;"
        };
        function.DeclaredContextFields.Add("GateLength");
        instrument.LogicalParameters.Add(parameter);
        instrument.Envelopes.Add(envelope);
        instrument.MappingFunctions.Add(function);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        ProjectObjectClipboardPayload parameterPayload =
            ProjectObjectClipboard.CopyLogicalParameterDefinition(
                document,
                instrument.Id,
                parameter.Id);
        ProjectObjectClipboardPayload envelopePayload = ProjectObjectClipboard.CopyEnvelopePreset(
            document,
            instrument.Id,
            envelope.Id);
        ProjectObjectClipboardPayload functionPayload = ProjectObjectClipboard.CopyMappingFunction(
            document,
            instrument.Id,
            function.Id);

        document.Execute(ProjectObjectClipboard.CreatePasteLogicalParameterDefinitionCommand(
            document,
            parameterPayload,
            instrument.Id));
        document.Execute(ProjectObjectClipboard.CreatePasteEnvelopePresetCommand(
            document,
            envelopePayload,
            instrument.Id));
        document.Execute(ProjectObjectClipboard.CreatePasteMappingFunctionCommand(
            document,
            functionPayload,
            instrument.Id));

        LogicalParameterDefinition parameterCopy = instrument.LogicalParameters[1];
        InstrumentEnvelope envelopeCopy = instrument.Envelopes[1];
        CSharpMappingFunction functionCopy = instrument.MappingFunctions[1];
        Assert.Equal("Mode Copy 2", parameterCopy.Name);
        Assert.Equal(["Off", "On"], parameterCopy.EnumItems.Select(value => value.Name));
        Assert.NotEqual(parameter.Id, parameterCopy.Id);
        Assert.All(parameterCopy.EnumItems, value =>
            Assert.DoesNotContain(parameter.EnumItems, source => source.Id == value.Id));
        Assert.Equal(("Shape", 12L, 0.8, 0.5, 24L),
            (envelopeCopy.Name, envelopeCopy.AttackTicks, envelopeCopy.PeakValue,
                envelopeCopy.SustainValue, envelopeCopy.ReleaseTicks));
        Assert.NotEqual(envelope.Id, envelopeCopy.Id);
        Assert.Equal("Scale Copy 2", functionCopy.Name);
        Assert.Equal(function.Body, functionCopy.Body);
        Assert.Equal(["GateLength"], functionCopy.DeclaredContextFields);
        Assert.NotEqual(function.Id, functionCopy.Id);
        Assert.Equal(3, document.History.Count);

        document.Undo();
        Assert.Single(instrument.MappingFunctions);
        document.Redo();
        Assert.Same(functionCopy, instrument.MappingFunctions[1]);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void MappingItemClipboardReplacesConfigurationAndInsertsOneStep()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument" };
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Amount",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1,
            DisplayMinimum = 0,
            DisplayMaximum = 1
        };
        SubVoice voice = new(project) { Name = "Voice" };
        LogicalParameterMapping source = new(project)
        {
            ParameterId = parameter.Id,
            SubVoiceId = voice.Id,
            Target = MidiValueTarget.ControlChange(1)
        };
        source.TargetSettings.Rounding = MappingRounding.Floor;
        source.TargetSettings.Overflow = MappingOverflow.Clamp;
        ValueMappingStep sourceStep = new(project)
        {
            Source = MappingSource.LogicalParameter,
            Operation = MappingOperation.Remap,
            LogicalParameterId = parameter.Id,
            SourceMinimum = 0,
            SourceMaximum = 1,
            TargetMinimum = 0,
            TargetMaximum = 127
        };
        source.Steps.Add(sourceStep);
        LogicalParameterMapping target = new(project)
        {
            ParameterId = parameter.Id,
            SubVoiceId = voice.Id,
            Target = MidiValueTarget.ControlChange(7)
        };
        ValueMappingStep oldTargetStep = new(project);
        target.Steps.Add(oldTargetStep);
        instrument.LogicalParameters.Add(parameter);
        instrument.SubVoices.Add(voice);
        instrument.ParameterMappings.AddRange([source, target]);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        MappingChain oldTargetChain = target.Steps;

        ProjectObjectClipboardPayload mappingPayload =
            ProjectObjectClipboard.CopyLogicalParameterMapping(
                document,
                instrument.Id,
                source.Id);
        document.Execute(ProjectObjectClipboard.CreatePasteLogicalParameterMappingCommand(
            document,
            mappingPayload,
            instrument.Id,
            target.Id,
            nonEmptyReplacementConfirmed: true));

        Assert.Equal(MidiValueTarget.ControlChange(7), target.Target);
        Assert.Equal(MappingRounding.Floor, target.TargetSettings.Rounding);
        Assert.Equal(MappingOverflow.Clamp, target.TargetSettings.Overflow);
        ValueMappingStep replacedStep = Assert.Single(target.Steps);
        Assert.NotSame(sourceStep, replacedStep);
        Assert.Equal(parameter.Id, replacedStep.LogicalParameterId);
        Assert.NotSame(oldTargetChain, target.Steps);

        ProjectObjectClipboardPayload stepPayload = ProjectObjectClipboard.CopyMappingStep(
            document,
            instrument.Id,
            source.Steps.Id,
            sourceStep.Id);
        document.Execute(ProjectObjectClipboard.CreatePasteMappingStepCommand(
            document,
            stepPayload,
            instrument.Id,
            target.Steps.Id,
            insertionIndex: 1));

        ValueMappingStep inserted = target.Steps[1];
        Assert.NotEqual(sourceStep.Id, inserted.Id);
        Assert.Equal(sourceStep.Operation, inserted.Operation);
        Assert.Equal(2, target.Steps.Count);

        ProjectObjectClipboardCutPreparation stepCut = ProjectObjectClipboard.PrepareCutMappingStep(
            document,
            instrument.Id,
            target.Steps.Id,
            inserted.Id);
        Assert.Same(inserted, target.Steps[1]);
        document.Execute(stepCut.DeleteAfterSuccessfulClipboardWrite);
        Assert.Single(target.Steps);
        document.Undo();
        Assert.Same(inserted, target.Steps[1]);
        document.Undo();
        Assert.Single(target.Steps);
        document.Undo();
        Assert.Same(oldTargetChain, target.Steps);
        document.Redo();
        Assert.NotSame(oldTargetChain, target.Steps);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void InstrumentDefinitionCutsAreTwoPhaseAndUndoRestoreExactObjects()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            RequiresChannelIsolation = true
        };
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Amount",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1,
            DisplayMinimum = 0,
            DisplayMaximum = 1
        };
        InstrumentEnvelope envelope = new(project) { Name = "Shape" };
        CSharpMappingFunction function = new(project)
        {
            Name = "Identity",
            Body = "return value;"
        };
        instrument.LogicalParameters.Add(parameter);
        instrument.Envelopes.Add(envelope);
        instrument.MappingFunctions.Add(function);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        ProjectObjectClipboardCutPreparation parameterCut =
            ProjectObjectClipboard.PrepareCutLogicalParameterDefinition(
                document,
                instrument.Id,
                parameter.Id);
        Assert.Same(parameter, Assert.Single(instrument.LogicalParameters));
        document.Execute(parameterCut.DeleteAfterSuccessfulClipboardWrite);
        Assert.Empty(instrument.LogicalParameters);
        document.Undo();
        Assert.Same(parameter, Assert.Single(instrument.LogicalParameters));

        ProjectObjectClipboardCutPreparation envelopeCut =
            ProjectObjectClipboard.PrepareCutEnvelopePreset(
                document,
                instrument.Id,
                envelope.Id);
        Assert.Same(envelope, Assert.Single(instrument.Envelopes));
        document.Execute(envelopeCut.DeleteAfterSuccessfulClipboardWrite);
        Assert.Empty(instrument.Envelopes);
        document.Undo();
        Assert.Same(envelope, Assert.Single(instrument.Envelopes));

        ProjectObjectClipboardCutPreparation functionCut =
            ProjectObjectClipboard.PrepareCutMappingFunction(
                document,
                instrument.Id,
                function.Id);
        Assert.Same(function, Assert.Single(instrument.MappingFunctions));
        document.Execute(functionCut.DeleteAfterSuccessfulClipboardWrite);
        Assert.Empty(instrument.MappingFunctions);
        document.Undo();
        Assert.Same(function, Assert.Single(instrument.MappingFunctions));
        Assert.False(document.IsModified);
        AssertMatchesFull(compilation);
    }

    private static ProjectDocumentSession PersistedDocument(ProjectCompilationSession compilation)
    {
        ProjectDocumentSession result = new(compilation, ProjectDocumentOrigin.Persisted);
        result.MarkSaveSucceeded();
        return result;
    }

    private static void AssertMatchesFull(ProjectCompilationSession compilation)
    {
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult expected = compiler.CompileFull(compilation.Project);
        Assert.Equal(expected.Fingerprint, compilation.LastAttempt.Fingerprint);
        Assert.Equal(expected.IsConsumable, compilation.LastAttempt.IsConsumable);
    }
}
