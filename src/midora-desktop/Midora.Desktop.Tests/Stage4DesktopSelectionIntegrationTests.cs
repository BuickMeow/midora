using Midora.Application;
using Midora.Desktop.Presentation.Interaction;
using Midora.Domain;
using Xunit;

namespace Midora.Desktop.Tests;

[Collection(DesktopSharedPresentationStateCollection.Name)]
public sealed class Stage4DesktopSelectionIntegrationTests
{
    [Fact]
    public async Task PreparedSplitPublishesResultSelectionAndUndoRedoRestoreBothSides()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Stage 4 selection publication",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
        EventInstrument instrument = Assert.Single(session.Project!.EventInstruments);
        session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Track", instrument.Id));
        LogicalTrack track = Assert.Single(session.Project.Tracks);
        session.Execute(ProjectDomainEditCommands.CreateSegment(track.Id, 0, 480));
        Segment segment = Assert.Single(track.Segments);
        session.Execute(ProjectDomainEditCommands.CreateLogicalNote(
            segment.Id,
            startTick: 24,
            lengthTicks: 120,
            note: 60,
            velocity: 100));
        LogicalNote original = Assert.Single(segment.Notes);
        TimelineWorkspaceViewModel workspace = session.OpenSegment(segment.Id);
        WorkspaceTimelineSelectionSource selectionSource = new(
            WorkspaceTimelineSelectionKind.LogicalNote,
            segment.Id);
        workspace.Selection.Replace(original.Id, selectionSource);
        session.RefreshWorkspaceSelection(workspace);
        CompressedMidoraIdSet originalSelectionRoot = workspace.Selection.SharedIds;

        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.SplitLogicalNotes(
                segment.Id,
                [original.Id],
                new NoteSplitOptions
                {
                    Mode = NoteSplitMode.FixedPieceLength,
                    FixedPieceLengthTicks = 40
                });
        using StagedProjectEdit staged = session.PrepareProjectEdit(command, workspace);
        PreparedTimelineSelection preparedTransition =
            Assert.IsType<PreparedTimelineSelection>(staged.PreparedSelection);
        Assert.Equal([original.Id], preparedTransition.OriginalSelectionIds);
        Assert.Equal(3, preparedTransition.ResultSelectionIds.Count);
        Assert.True(session.TryGetPreparedWorkspaceSelectionProjection(
            staged,
            out PreparedWorkspaceSelectionProjection? preparedProjection));
        Assert.NotNull(preparedProjection);
        CompressedMidoraIdSet preparedSelectionRoot = preparedProjection.Ids;
        ProjectEditExecution execution = session.ExecutePreparedPreservingWorkspaceSelection(
            staged,
            workspace,
            command);

        Assert.True(execution.Changed);
        Assert.Same(preparedSelectionRoot, workspace.Selection.SharedIds);
        MidoraId[] splitSelection = workspace.Selection.Ids.ToArray();
        Assert.Equal(3, splitSelection.Length);
        Assert.Contains(original.Id, splitSelection);
        Assert.Equal(original.Id, workspace.Selection.Primary);
        Assert.Equal(selectionSource, workspace.Selection.HomogeneousTimelineSource);
        Assert.Equal(splitSelection, ResolveCurrentNotes(session, segment.Id));

        session.Undo();
        Assert.Same(originalSelectionRoot, workspace.Selection.SharedIds);
        Assert.Equal([original.Id], workspace.Selection.Ids);
        Assert.Equal(original.Id, workspace.Selection.Primary);
        Assert.Equal(selectionSource, workspace.Selection.HomogeneousTimelineSource);
        Assert.Equal([original.Id], ResolveCurrentNotes(session, segment.Id));

        session.Redo();
        Assert.Same(preparedSelectionRoot, workspace.Selection.SharedIds);
        Assert.Equal(splitSelection, workspace.Selection.Ids);
        Assert.Equal(selectionSource, workspace.Selection.HomogeneousTimelineSource);
        Assert.Equal(splitSelection, ResolveCurrentNotes(session, segment.Id));
    }

    [Fact]
    public async Task CancelledPreparationNeverPublishesProjectOrWorkspaceSelection()
    {
        (DesktopSessionController session, TimelineWorkspaceViewModel workspace,
            Segment segment, LogicalNote note) = await CreateSessionAsync();
        await using (session)
        {
            CompressedMidoraIdSet beforeSelection = workspace.Selection.SharedIds;
            int beforeHistoryCount = session.Document!.History.Count;
            long beforeStateId = session.Document.CurrentStateId;
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();
            ITimelineSelectionResultEditCommand command =
                ProjectDomainEditCommands.QuantizeLogicalNotes(
                    segment.Id,
                    [note.Id],
                    new(TimelineQuantizeGrid.FromCustomTicks(10)));

            Assert.Throws<OperationCanceledException>(() =>
                session.PrepareProjectEdit(command, workspace, cancellation.Token));

            Assert.Same(beforeSelection, workspace.Selection.SharedIds);
            Assert.Equal(24, Assert.Single(segment.Notes).StartTick);
            Assert.Equal(beforeHistoryCount, session.Document.History.Count);
            Assert.Equal(beforeStateId, session.Document.CurrentStateId);
        }
    }

    [Fact]
    public async Task StalePreparedEditDoesNotAdoptItsFrozenSelection()
    {
        (DesktopSessionController session, TimelineWorkspaceViewModel workspace,
            Segment segment, LogicalNote note) = await CreateSessionAsync();
        await using (session)
        {
            ITimelineSelectionResultEditCommand stagedCommand =
                ProjectDomainEditCommands.SplitLogicalNotes(
                    segment.Id,
                    [note.Id],
                    new NoteSplitOptions
                    {
                        Mode = NoteSplitMode.FixedPieceLength,
                        FixedPieceLengthTicks = 40
                    });
            using StagedProjectEdit staged = session.PrepareProjectEdit(
                stagedCommand,
                workspace);
            CompressedMidoraIdSet beforeSelection = workspace.Selection.SharedIds;
            session.Execute(ProjectDomainEditCommands.SetSegmentWindow(
                segment.Id,
                projectStartTick: 1,
                lengthTicks: segment.LengthTicks,
                contentOffsetTick: segment.ContentOffsetTick));

            Assert.Throws<InvalidOperationException>(() =>
                session.ExecutePreparedPreservingWorkspaceSelection(
                    staged,
                    workspace,
                    stagedCommand));

            Assert.Same(beforeSelection, workspace.Selection.SharedIds);
            Assert.Single(session.Project!.Tracks.Single().Segments.Single().Notes);
        }
    }

    [Fact]
    public async Task PreparedNoOpCarriesProjectionButDoesNotReplaceSelectionOrHistory()
    {
        (DesktopSessionController session, TimelineWorkspaceViewModel workspace,
            Segment segment, LogicalNote note) = await CreateSessionAsync();
        await using (session)
        {
            CompressedMidoraIdSet beforeSelection = workspace.Selection.SharedIds;
            int beforeHistoryCount = session.Document!.History.Count;
            long beforeStateId = session.Document.CurrentStateId;
            ITimelineSelectionResultEditCommand command =
                ProjectDomainEditCommands.QuantizeLogicalNotes(
                    segment.Id,
                    [note.Id],
                    new(TimelineQuantizeGrid.FromCustomTicks(24)));

            using StagedProjectEdit staged = session.PrepareProjectEdit(command, workspace);
            Assert.NotNull(staged.PreparedSelection);
            Assert.True(session.TryGetPreparedWorkspaceSelectionProjection(
                staged,
                out PreparedWorkspaceSelectionProjection? preparedProjection));
            Assert.NotNull(preparedProjection);

            ProjectEditExecution execution =
                session.ExecutePreparedPreservingWorkspaceSelection(
                    staged,
                    workspace,
                    command);

            Assert.False(execution.Changed);
            Assert.Same(beforeSelection, workspace.Selection.SharedIds);
            Assert.Equal(
                new WorkspaceTimelineSelectionSource(
                    WorkspaceTimelineSelectionKind.LogicalNote,
                    segment.Id),
                workspace.Selection.HomogeneousTimelineSource);
            Assert.Equal(beforeHistoryCount, session.Document.History.Count);
            Assert.Equal(beforeStateId, session.Document.CurrentStateId);
            Assert.Equal(24, Assert.Single(segment.Notes).StartTick);
        }
    }

    [Fact]
    public async Task PreparedMultiOwnerEditAdoptsOnePrecompressedResultRoot()
    {
        (DesktopSessionController session, TimelineWorkspaceViewModel workspace,
            Segment firstSegment, LogicalNote firstNote) = await CreateSessionAsync();
        await using (session)
        {
            EventInstrument instrument = Assert.Single(session.Project!.EventInstruments);
            session.Execute(ProjectDomainEditCommands.CreateLogicalTrack(
                "Second Track",
                instrument.Id));
            LogicalTrack secondTrack = session.Project.Tracks.Single(
                value => value.Name == "Second Track");
            session.Execute(ProjectDomainEditCommands.CreateSegment(
                secondTrack.Id,
                projectStartTick: 600,
                lengthTicks: 480));
            Segment secondSegment = Assert.Single(secondTrack.Segments);
            session.Execute(ProjectDomainEditCommands.CreateLogicalNote(
                secondSegment.Id,
                startTick: 24,
                lengthTicks: 120,
                note: 64,
                velocity: 90));
            LogicalNote secondNote = Assert.Single(secondSegment.Notes);
            workspace.Selection.Replace(firstNote.Id);
            workspace.Selection.Add(secondNote.Id);
            session.RefreshWorkspaceSelection(workspace);
            ITimelineSelectionResultEditCommand command =
                ProjectDomainEditCommands.SplitLogicalNotes(
                [
                    new(firstSegment.Id, [firstNote.Id]),
                    new(secondSegment.Id, [secondNote.Id])
                ],
                new NoteSplitOptions
                {
                    Mode = NoteSplitMode.FixedPieceLength,
                    FixedPieceLengthTicks = 40
                });
            using StagedProjectEdit staged = session.PrepareProjectEdit(command, workspace);
            PreparedTimelineSelection transition = Assert.IsType<PreparedTimelineSelection>(
                staged.PreparedSelection);
            Assert.Equal(2, transition.OriginalSelectionIds.Count);
            Assert.Equal(6, transition.ResultSelectionIds.Count);
            Assert.True(session.TryGetPreparedWorkspaceSelectionProjection(
                staged,
                out PreparedWorkspaceSelectionProjection? preparedProjection));
            Assert.NotNull(preparedProjection);
            CompressedMidoraIdSet resultSelectionRoot = preparedProjection.Ids;

            ProjectEditExecution execution =
                session.ExecutePreparedPreservingWorkspaceSelection(
                    staged,
                    workspace,
                    command);

            Assert.True(execution.Changed);
            Assert.Same(resultSelectionRoot, workspace.Selection.SharedIds);
            Assert.Equal(6, workspace.Selection.Ids.Count);
            Assert.Equal(6, session.Project.Tracks
                .SelectMany(static value => value.Segments)
                .Where(value => value.Id == firstSegment.Id || value.Id == secondSegment.Id)
                .Sum(static value => value.Notes.Count));
        }
    }

    [Fact]
    public async Task MultiObjectCopiesPublishEveryStage4NoteAndEventSelectionSource()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Stage 4 copied selection routing",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
        EventInstrument instrument = Assert.Single(session.Project!.EventInstruments);
        SubVoice voice = Assert.Single(instrument.SubVoices);
        session.Execute(ProjectDomainEditCommands.CreateTemplateNote(
            instrument.Id, voice.Id, 12, 12, 60, 90));
        session.Execute(ProjectDomainEditCommands.CreateTemplateNote(
            instrument.Id, voice.Id, 36, 12, 64, 90));
        session.Execute(ProjectDomainEditCommands.CreateTemplateControlChange(
            instrument.Id, voice.Id, 12, 11, 40));
        session.Execute(ProjectDomainEditCommands.CreateTemplateControlChange(
            instrument.Id, voice.Id, 36, 11, 80));
        TemplateEvent[] templateNotes = voice.Events
            .Where(static value => value.Kind == TemplateEventKind.Note)
            .ToArray();
        TemplateEvent[] templateEvents = voice.Events
            .Where(static value => value.Kind == TemplateEventKind.ControlChange)
            .ToArray();

        session.Execute(ProjectDomainEditCommands.CreateLogicalParameter(
            instrument.Id,
            "Expression",
            LogicalParameterType.Integer,
            0,
            127,
            0,
            127,
            0));
        LogicalParameterDefinition parameter = Assert.Single(instrument.LogicalParameters);
        session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Logical", instrument.Id));
        LogicalTrack logicalTrack = Assert.Single(session.Project.Tracks);
        session.Execute(ProjectDomainEditCommands.CreateSegment(logicalTrack.Id, 0, 480));
        Segment logicalSegment = Assert.Single(logicalTrack.Segments);
        session.Execute(ProjectDomainEditCommands.CreateLogicalParameterLane(
            logicalSegment.Id,
            parameter.Id));
        LogicalParameterLane logicalLane = Assert.Single(logicalSegment.ParameterLanes);
        session.Execute(ProjectDomainEditCommands.CreateLogicalNote(
            logicalSegment.Id, 12, 12, 60, 90));
        session.Execute(ProjectDomainEditCommands.CreateLogicalNote(
            logicalSegment.Id, 36, 12, 64, 90));
        session.Execute(ProjectDomainEditCommands.CreateLogicalParameterPoint(
            logicalSegment.Id, logicalLane.Id, 12, 40, CurveInterpolation.Step));
        session.Execute(ProjectDomainEditCommands.CreateLogicalParameterPoint(
            logicalSegment.Id, logicalLane.Id, 36, 80, CurveInterpolation.Step));
        LogicalNote[] logicalNotes = logicalSegment.Notes.ToArray();
        CurvePoint[] logicalPoints = logicalLane.Points.ToArray();

        session.Execute(ProjectDomainEditCommands.CreatePureMidiTrackWithNewRoot("MIDI"));
        PureMidiTrack midiTrack = Assert.Single(session.Project.PureMidiTracks);
        session.Execute(ProjectDomainEditCommands.CreateMidiSegment(midiTrack.Id, 0, 480));
        MidiSegment midiSegment = Assert.Single(midiTrack.Segments);
        session.Execute(ProjectDomainEditCommands.CreateDirectMidiNote(
            midiSegment.Id, 12, 12, 60, 90));
        session.Execute(ProjectDomainEditCommands.CreateDirectMidiNote(
            midiSegment.Id, 36, 12, 64, 90));
        session.Execute(ProjectDomainEditCommands.CreateDirectMidiChannelEvent(
            midiSegment.Id, 12, DirectMidiChannelEventKind.ControlChange, 11, 40));
        session.Execute(ProjectDomainEditCommands.CreateDirectMidiChannelEvent(
            midiSegment.Id, 36, DirectMidiChannelEventKind.ControlChange, 11, 80));
        DirectMidiNote[] directNotes = midiSegment.Notes.ToArray();
        DirectMidiChannelEvent[] directEvents = midiSegment.ChannelEvents.ToArray();

        TimelineWorkspaceViewModel logicalWorkspace = session.OpenSegment(logicalSegment.Id);
        TimelineWorkspaceViewModel midiWorkspace = session.OpenSegment(midiSegment.Id);
        InstrumentWorkspaceViewModel instrumentWorkspace = session.OpenInstrument(instrument.Id);
        MidiValueTarget subVoiceTarget = MidiValueTarget.ControlChange(11);

        DuplicateAndAssert(
            logicalWorkspace,
            new(WorkspaceTimelineSelectionKind.LogicalNote, logicalSegment.Id),
            ProjectDomainEditCommands.DuplicateLogicalNotes(
                logicalSegment.Id,
                logicalNotes.Select(static value => value.Id).ToArray(),
                logicalSegment.Id,
                200));
        DuplicateAndAssert(
            logicalWorkspace,
            new(
                WorkspaceTimelineSelectionKind.LogicalParameterPoint,
                logicalSegment.Id,
                logicalLane.Id,
                PointMinimum: 0,
                PointMaximum: 127),
            ProjectDomainEditCommands.DuplicateLogicalParameterPoints(
                logicalSegment.Id,
                logicalLane.Id,
                logicalPoints.Select(static value => value.Id).ToArray(),
                tickDelta: 200,
                valueDelta: 0));
        DuplicateAndAssert(
            midiWorkspace,
            new(WorkspaceTimelineSelectionKind.DirectMidiNote, midiSegment.Id),
            ProjectDomainEditCommands.DuplicateDirectMidiNotes(
                midiSegment.Id,
                directNotes.Select(static value => value.Id).ToArray(),
                tickDelta: 200,
                keyDelta: 0));
        DuplicateAndAssert(
            midiWorkspace,
            new(
                WorkspaceTimelineSelectionKind.DirectMidiEventPoint,
                midiSegment.Id,
                DirectMidiEventKind: DirectMidiChannelEventKind.ControlChange,
                DirectMidiData1: 11,
                PointMinimum: 0,
                PointMaximum: 127),
            ProjectDomainEditCommands.AdjustDirectMidiEventPoints(
                midiSegment.Id,
                directEvents.Select(static value => value.Id).ToArray(),
                tickDelta: 200,
                data1Delta: 0,
                data2Delta: 0,
                duplicate: true));
        DuplicateAndAssert(
            instrumentWorkspace,
            new(
                WorkspaceTimelineSelectionKind.TemplateNote,
                instrument.Id,
                voice.Id),
            ProjectDomainEditCommands.DuplicateTemplateNotes(
                instrument.Id,
                voice.Id,
                templateNotes.Select(static value => value.Id).ToArray(),
                newEarliestTick: 200,
                pitchDelta: 0));
        DuplicateAndAssert(
            instrumentWorkspace,
            new(
                WorkspaceTimelineSelectionKind.SubVoiceEventPoint,
                instrument.Id,
                voice.Id,
                subVoiceTarget,
                PointMinimum: 0,
                PointMaximum: 127),
            ProjectDomainEditCommands.AdjustSubVoiceEventPoints(
                instrument.Id,
                voice.Id,
                templateEvents.Select(static value => value.Id).ToArray(),
                subVoiceTarget,
                tickDelta: 200,
                valueDelta: 0,
                duplicate: true));

        void DuplicateAndAssert(
            WorkspaceViewModel workspace,
            WorkspaceTimelineSelectionSource source,
            IProjectEditCommand command)
        {
            long firstNewStableId = session.Project!.NextStableId;
            session.Execute(command);
            MainWindow.SelectCreatedWorkspaceObjects(
                session,
                workspace,
                firstNewStableId,
                timelineSource: source);
            Assert.Equal(2, workspace.Selection.Ids.Count);
            Assert.Equal(source, workspace.Selection.HomogeneousTimelineSource);
            Assert.Equal(
                source.QuantizeScope,
                workspace.Selection.HomogeneousTimelineQuantizeScope);
        }
    }

    private static async Task<(DesktopSessionController Session,
        TimelineWorkspaceViewModel Workspace, Segment Segment, LogicalNote Note)>
        CreateSessionAsync()
    {
        DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Stage 4 prepared projection",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
        EventInstrument instrument = Assert.Single(session.Project!.EventInstruments);
        session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Track", instrument.Id));
        LogicalTrack track = Assert.Single(session.Project.Tracks);
        session.Execute(ProjectDomainEditCommands.CreateSegment(track.Id, 0, 480));
        Segment segment = Assert.Single(track.Segments);
        session.Execute(ProjectDomainEditCommands.CreateLogicalNote(
            segment.Id,
            startTick: 24,
            lengthTicks: 120,
            note: 60,
            velocity: 100));
        LogicalNote note = Assert.Single(segment.Notes);
        TimelineWorkspaceViewModel workspace = session.OpenSegment(segment.Id);
        workspace.Selection.Replace(
            note.Id,
            new(
                WorkspaceTimelineSelectionKind.LogicalNote,
                segment.Id));
        session.RefreshWorkspaceSelection(workspace);
        session.Document!.MarkSaveSucceeded();
        return (session, workspace, segment, note);
    }

    private static MidoraId[] ResolveCurrentNotes(
        DesktopSessionController session,
        MidoraId segmentId) =>
        session.Project!.Tracks
            .SelectMany(static value => value.Segments)
            .Single(value => value.Id == segmentId)
            .Notes.Select(static note => note.Id)
            .ToArray();
}
