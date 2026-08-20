using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectObjectClipboardPureMidiTests
{
    [Fact]
    public void IndependentFixedPureMidiTrackPasteDeepCopiesContentAndJoinsExistingRoute()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = AddRoot(
            project,
            "Fixed Root",
            MidiChannelRootRoutingMode.Fixed,
            MidiChannelMode.Percussion);
        root.FixedZeroBasedPort = 4;
        root.FixedZeroBasedChannel = 9;
        PureMidiTrack track = AddTrack(project, root, "Drums");
        MidiSegment segment = AddSegment(project, track, 120, 480);
        DirectMidiNote note = new(project)
        {
            StartTick = 12,
            LengthTicks = 48,
            Key = 36,
            NoteOnVelocity = 110,
            NoteOffVelocity = 23,
            NoteOnOrder = 0,
            NoteOffOrder = 7
        };
        DirectMidiChannelEvent directEvent = new(project)
        {
            Tick = 20,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 91,
            Data2 = 64,
            Order = 9
        };
        OpaqueMidiEvent opaque = new(project)
        {
            Tick = 30,
            Kind = OpaqueMidiEventKind.Meta,
            MetaType = 0x7f,
            Payload = [1, 2, 3],
            Order = 10
        };
        segment.Notes.Add(note);
        segment.ChannelEvents.Add(directEvent);
        segment.OpaqueEvents.Add(opaque);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        ProjectObjectClipboardPayload payload = ProjectObjectClipboard.CopyPureMidiTrack(
            document,
            track.Id);
        note.Key = 40;
        directEvent.Data2 = 1;
        opaque.Payload[0] = 99;
        document.Execute(ProjectObjectClipboard.CreatePastePureMidiTrackIndependentCommand(
            document,
            payload,
            insertionIndex: project.ArrangementTracks.Count));

        Assert.Same(root, Assert.Single(project.MidiChannelRoots));
        PureMidiTrack copiedTrack = Assert.Single(
            project.PureMidiTracks,
            value => value.Id != track.Id);
        Assert.Equal(root.Id, copiedTrack.MidiChannelRootId);
        Assert.NotEqual(track.Id, copiedTrack.Id);
        MidiSegment copiedSegment = Assert.Single(copiedTrack.Segments);
        Assert.NotEqual(segment.Id, copiedSegment.Id);
        DirectMidiNote copiedNote = Assert.Single(copiedSegment.Notes);
        Assert.Equal(36, copiedNote.Key);
        Assert.Equal(23, copiedNote.NoteOffVelocity);
        Assert.Equal(0, copiedNote.NoteOnOrder);
        Assert.Equal(7, copiedNote.NoteOffOrder);
        Assert.Equal(64, Assert.Single(copiedSegment.ChannelEvents).Data2);
        Assert.Equal([1, 2, 3], Assert.Single(copiedSegment.OpaqueEvents).Payload);

        document.Undo();
        Assert.Same(root, Assert.Single(project.MidiChannelRoots));
        Assert.DoesNotContain(copiedTrack, project.PureMidiTracks);
        document.Redo();
        Assert.Same(root, Assert.Single(project.MidiChannelRoots));
        Assert.Contains(copiedTrack, project.PureMidiTracks);
    }

    [Fact]
    public void LogicalAndDirectNotesShareOnlyCommonFieldsAcrossClipboardBoundary()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalTrack logicalTrack = new(project) {
            Name = "Logical",
            LastBoundEventInstrumentName = instrument.Name
        };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, logicalTrack, instrument.Id);
        Segment logicalSegment = new(project) { LengthTicks = 960 };
        LogicalNote logical = new(project)
        {
            StartTick = 10,
            LengthTicks = 60,
            Note = 64,
            Velocity = 91
        };
        logicalSegment.Notes.Add(logical);
        logicalTrack.Segments.Add(logicalSegment);

        MidiChannelRoot root = AddRoot(project, "Root");
        PureMidiTrack midiTrack = AddTrack(project, root, "MIDI");
        MidiSegment midiSegment = AddSegment(project, midiTrack, 0, 960);
        DirectMidiNote direct = new(project)
        {
            StartTick = 20,
            LengthTicks = 70,
            Key = 72,
            NoteOnVelocity = 87,
            NoteOffVelocity = 55,
            NoteOnOrder = 0,
            NoteOffOrder = 1
        };
        midiSegment.Notes.Add(direct);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        ProjectObjectClipboardPayload logicalPayload = ProjectObjectClipboard.CopyLogicalNotes(
            document,
            logicalSegment.Id,
            [logical.Id]);
        document.Execute(ProjectObjectClipboard.CreatePasteNotesCommand(
            document,
            logicalPayload,
            midiSegment.Id,
            editCursorTick: 100,
            targetIsDirectMidi: true));

        DirectMidiNote logicalCopy = Assert.Single(
            midiSegment.Notes,
            value => value.StartTick == 100);
        Assert.Equal(64, logicalCopy.Key);
        Assert.Equal(91, logicalCopy.NoteOnVelocity);
        Assert.Equal(0, logicalCopy.NoteOffVelocity);
        Assert.NotEqual(logicalCopy.NoteOnOrder, logicalCopy.NoteOffOrder);

        ProjectObjectClipboardPayload directPayload = ProjectObjectClipboard.CopyDirectMidiNotes(
            document,
            midiSegment.Id,
            [direct.Id]);
        document.Execute(ProjectObjectClipboard.CreatePasteNotesCommand(
            document,
            directPayload,
            logicalSegment.Id,
            editCursorTick: 200,
            targetIsDirectMidi: false));

        LogicalNote directCopy = Assert.Single(
            logicalSegment.Notes,
            value => value.StartTick == 200);
        Assert.Equal(72, directCopy.Note);
        Assert.Equal(87, directCopy.Velocity);
        Assert.Equal(70, directCopy.LengthTicks);
    }

    [Fact]
    public void DirectMidiEventPastePreservesSameTickDuplicatesAndUndoRestoresExactOriginalOrder()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = AddRoot(project, "Root");
        PureMidiTrack track = AddTrack(project, root, "Track");
        MidiSegment source = AddSegment(project, track, 0, 100);
        MidiSegment target = AddSegment(project, track, 200, 100);
        DirectMidiChannelEvent first = new(project)
        {
            Tick = 0,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 1,
            Data2 = 80,
            Order = 1
        };
        DirectMidiChannelEvent later = new(project)
        {
            Tick = 0,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 1,
            Data2 = 99,
            Order = 2
        };
        source.ChannelEvents.AddRange([first, later]);
        DirectMidiChannelEvent existing = new(project)
        {
            Tick = 20,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 1,
            Data2 = 12,
            Order = 10
        };
        DirectMidiChannelEvent unaffected = new(project)
        {
            Tick = 21,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 1,
            Data2 = 13,
            Order = 11
        };
        target.ChannelEvents.AddRange([existing, unaffected]);
        DirectMidiChannelEvent[] original = target.ChannelEvents.ToArray();
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        ProjectObjectClipboardPayload payload = ProjectObjectClipboard.CopyDirectMidiEvents(
            document,
            source.Id,
            [first.Id, later.Id]);
        document.Execute(ProjectObjectClipboard.CreatePasteDirectMidiEventsCommand(
            document,
            payload,
            target.Id,
            editCursorTick: 20));

        Assert.Equal(
            [12, 80, 99],
            target.ChannelEvents
                .Where(value => value.Tick == 20 && value.Data1 == 1)
                .Select(value => value.Data2));
        Assert.Equal([existing, unaffected], target.ChannelEvents.Take(2));

        document.Undo();
        Assert.Equal(original, target.ChannelEvents);
        document.Redo();
        Assert.Equal(
            [12, 80, 99],
            target.ChannelEvents
                .Where(value => value.Tick == 20 && value.Data1 == 1)
                .Select(value => value.Data2));
    }

    [Fact]
    public void DirectMidiEventMoveAndDuplicatePreserveSameTickDuplicates()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = AddRoot(project, "Root");
        PureMidiTrack track = AddTrack(project, root, "Track");
        MidiSegment segment = AddSegment(project, track, 0, 100);
        DirectMidiChannelEvent moved = new(project)
        {
            Tick = 10,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 7,
            Data2 = 40,
            Order = 1
        };
        DirectMidiChannelEvent existing = new(project)
        {
            Tick = 20,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 7,
            Data2 = 80,
            Order = 2
        };
        segment.ChannelEvents.AddRange([moved, existing]);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.AdjustDirectMidiEventPoints(
            segment.Id,
            [moved.Id],
            tickDelta: 10,
            data1Delta: 0,
            data2Delta: 0,
            duplicate: false));

        Assert.Equal([moved, existing], segment.ChannelEvents);
        Assert.All(segment.ChannelEvents, value => Assert.Equal(20, value.Tick));

        document.Undo();
        Assert.Equal(10, moved.Tick);
        Assert.Equal([moved, existing], segment.ChannelEvents);

        document.Redo();
        document.Execute(ProjectDomainEditCommands.AdjustDirectMidiEventPoints(
            segment.Id,
            [moved.Id],
            tickDelta: 0,
            data1Delta: 0,
            data2Delta: 0,
            duplicate: true));

        Assert.Equal(3, segment.ChannelEvents.Count);
        Assert.All(segment.ChannelEvents, value => Assert.Equal(20, value.Tick));
    }

    [Fact]
    public void DirectMidiNotesPreserveExactSameTickKeyDuplicatesAcrossEditingCommands()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = AddRoot(project, "Root");
        PureMidiTrack track = AddTrack(project, root, "Track");
        MidiSegment segment = AddSegment(project, track, 0, 480);
        DirectMidiNote existing = new(project)
        {
            StartTick = 24,
            LengthTicks = 96,
            Key = 60,
            NoteOnVelocity = 80,
            NoteOffVelocity = 12,
            NoteOnOrder = 1,
            NoteOffOrder = 2
        };
        DirectMidiNote moving = new(project)
        {
            StartTick = 48,
            LengthTicks = 48,
            Key = 60,
            NoteOnVelocity = 70,
            NoteOffVelocity = 13,
            NoteOnOrder = 3,
            NoteOffOrder = 4
        };
        segment.Notes.AddRange([existing, moving]);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.CreateDirectMidiNote(
            segment.Id,
            startTick: 24,
            lengthTicks: 72,
            key: 60,
            noteOnVelocity: 90,
            noteOffVelocity: 14));
        Assert.Equal(2, segment.Notes.Count(value => value.StartTick == 24 && value.Key == 60));

        document.Execute(ProjectDomainEditCommands.MoveDirectMidiNotes(
            segment.Id,
            [moving.Id],
            tickDelta: -24,
            keyDelta: 0));
        Assert.Equal(3, segment.Notes.Count(value => value.StartTick == 24 && value.Key == 60));

        document.Execute(ProjectDomainEditCommands.DuplicateDirectMidiNotes(
            segment.Id,
            [existing.Id],
            tickDelta: 0,
            keyDelta: 0));
        Assert.Equal(4, segment.Notes.Count(value => value.StartTick == 24 && value.Key == 60));

        ProjectObjectClipboardPayload payload = ProjectObjectClipboard.CopyDirectMidiNotes(
            document,
            segment.Id,
            [existing.Id]);
        document.Execute(ProjectObjectClipboard.CreatePasteNotesCommand(
            document,
            payload,
            segment.Id,
            editCursorTick: 24,
            targetIsDirectMidi: true));
        Assert.Equal(5, segment.Notes.Count(value => value.StartTick == 24 && value.Key == 60));

        CanonicalCompiledResult compiled = compilation.LastAttempt;
        Assert.True(compiled.IsConsumable, string.Join(Environment.NewLine, compiled.Diagnostics));
        Assert.Equal(5, compiled.Events.ToArray().Count(value =>
            value.Tick == 24
            && value.Role == CanonicalEventRole.DirectMidi
            && value.Message.MessageType == Midora.Midi.MidiMessageType.NoteOn
            && value.Message.Byte1 == 60));
    }

    [Fact]
    public void MidiSegmentClipboardUsesArrangementOrderInsteadOfRepositoryOrder()
    {
        MidoraProject project = new(480);
        MidiChannelRoot firstRoot = AddRoot(project, "First");
        MidiChannelRoot secondRoot = AddRoot(project, "Second");
        PureMidiTrack firstTrack = new(project)
        {
            Name = "First Track",
            MidiChannelRootId = firstRoot.Id
        };
        PureMidiTrack secondTrack = new(project)
        {
            Name = "Second Track",
            MidiChannelRootId = secondRoot.Id
        };
        project.PureMidiTracks.AddRange([secondTrack, firstTrack]);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, secondTrack.Id));
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, firstTrack.Id));
        MidiSegment first = AddSegment(project, firstTrack, 0, 100);
        MidiSegment second = AddSegment(project, secondTrack, 0, 100);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        ProjectObjectClipboardPayload payload = ProjectObjectClipboard.CopyMidiSegments(
            document,
            [first.Id, second.Id],
            first.Id);
        document.Execute(ProjectObjectClipboard.CreatePasteMidiSegmentsCommand(
            document,
            payload,
            firstTrack.Id,
            editCursorTick: 200));

        Assert.Contains(firstTrack.Segments, value => value.ProjectStartTick == 200);
        Assert.Contains(secondTrack.Segments, value => value.ProjectStartTick == 200);
    }

    [Fact]
    public void ImportedMidiEventsCanMoveCopyDeleteAndRoundTripThroughClipboard()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = AddRoot(project, "Root");
        PureMidiTrack track = AddTrack(project, root, "Track");
        MidiSegment segment = AddSegment(project, track, 0, 960);
        OpaqueMidiEvent opaque = new(project)
        {
            Tick = 24,
            Kind = OpaqueMidiEventKind.Meta,
            MetaType = 0x05,
            Payload = [0x41, 0x42],
            Order = 7
        };
        segment.OpaqueEvents.Add(opaque);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.AdjustOpaqueMidiEvents(
            segment.Id, [opaque.Id], tickDelta: 12, duplicate: false));
        Assert.Equal(36, opaque.Tick);
        document.Undo();
        Assert.Equal(24, opaque.Tick);

        document.Execute(ProjectDomainEditCommands.AdjustOpaqueMidiEvents(
            segment.Id, [opaque.Id], tickDelta: 48, duplicate: true));
        OpaqueMidiEvent duplicate = Assert.Single(segment.OpaqueEvents, value => value.Id != opaque.Id);
        Assert.Equal(72, duplicate.Tick);
        Assert.Equal([0x41, 0x42], duplicate.Payload);
        Assert.NotSame(opaque.Payload, duplicate.Payload);

        ProjectObjectClipboardPayload payload = ProjectObjectClipboard.CopyOpaqueMidiEvents(
            document, segment.Id, [opaque.Id]);
        opaque.Payload[0] = 0x7f;
        document.Execute(ProjectObjectClipboard.CreatePasteOpaqueMidiEventsCommand(
            document, payload, segment.Id, editCursorTick: 200));
        OpaqueMidiEvent pasted = Assert.Single(segment.OpaqueEvents, value => value.Tick == 200);
        Assert.Equal([0x41, 0x42], pasted.Payload);

        document.Execute(ProjectDomainEditCommands.DeleteOpaqueMidiEvents(segment.Id, [pasted.Id]));
        Assert.DoesNotContain(pasted, segment.OpaqueEvents);
        document.Undo();
        Assert.Contains(pasted, segment.OpaqueEvents);
    }

    private static MidiChannelRoot AddRoot(
        MidoraProject project,
        string name,
        MidiChannelRootRoutingMode routing = MidiChannelRootRoutingMode.Auto,
        MidiChannelMode channelMode = MidiChannelMode.Melodic)
    {
        MidiChannelRoot root = new(project)
        {
            Name = name,
            RoutingMode = routing,
            ChannelMode = channelMode
        };
        project.MidiChannelRoots.Add(root);
        return root;
    }

    private static PureMidiTrack AddTrack(
        MidoraProject project,
        MidiChannelRoot root,
        string name)
    {
        PureMidiTrack track = new(project)
        {
            Name = name,
            MidiChannelRootId = root.Id
        };
        project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        return track;
    }

    private static MidiSegment AddSegment(
        MidoraProject project,
        PureMidiTrack track,
        long start,
        long length)
    {
        MidiSegment segment = new(project)
        {
            ProjectStartTick = start,
            LengthTicks = length
        };
        track.Segments.Add(segment);
        return segment;
    }
}
