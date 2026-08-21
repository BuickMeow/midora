using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectFlatArrangementEditCommandsTests
{
    [Fact]
    public void JoiningLogicalSharedGroupDeletesEmptyUsageAndUndoRestoresIdentityAndOrder()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalTrack first = AddLogicalTrack(project, instrument, "First");
        LogicalTrack separator = AddLogicalTrack(project, instrument, "Separator");
        LogicalTrack moving = AddLogicalTrack(project, instrument, "Moving");
        MidoraId movingUsageId = moving.EventInstrumentUsageId!.Value;
        EventInstrumentUsage movingUsage = project.EventInstrumentUsages.Single(
            value => value.Id == movingUsageId);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.MoveArrangementTrackIntoSharedGroup(
            moving.Id,
            first.Id));

        Assert.Equal(first.EventInstrumentUsageId, moving.EventInstrumentUsageId);
        Assert.DoesNotContain(movingUsage, project.EventInstrumentUsages);
        Assert.Equal(
            [first.Id, moving.Id, separator.Id],
            project.TracksInArrangementOrder().Select(value => value.TrackId));

        document.Undo();

        Assert.Equal(movingUsageId, moving.EventInstrumentUsageId);
        Assert.Contains(movingUsage, project.EventInstrumentUsages);
        Assert.Equal(
            [first.Id, separator.Id, moving.Id],
            project.TracksInArrangementOrder().Select(value => value.TrackId));

        document.Redo();

        Assert.Equal(first.EventInstrumentUsageId, moving.EventInstrumentUsageId);
        Assert.DoesNotContain(movingUsage, project.EventInstrumentUsages);
    }

    [Fact]
    public void MovingLogicalTrackOutsideSharedGroupCreatesIndependentUsageAndUndoRestoresGroup()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalTrack first = AddLogicalTrack(project, instrument, "First");
        LogicalTrack moving = new(project)
        {
            Name = "Moving",
            EventInstrumentUsageId = first.EventInstrumentUsageId,
            LastBoundEventInstrumentName = instrument.Name
        };
        project.Tracks.Add(moving);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, moving.Id));
        LogicalTrack after = AddLogicalTrack(project, instrument, "After");
        MidoraId sharedUsageId = first.EventInstrumentUsageId!.Value;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.MoveArrangementTrackOutsideSharedGroup(
            moving.Id,
            2));

        Assert.NotEqual(sharedUsageId, moving.EventInstrumentUsageId);
        Assert.Equal(
            instrument.Id,
            project.EventInstrumentUsages.Single(
                value => value.Id == moving.EventInstrumentUsageId).EventInstrumentId);
        Assert.Equal(
            [first.Id, after.Id, moving.Id],
            project.TracksInArrangementOrder().Select(value => value.TrackId));

        document.Undo();

        Assert.Equal(sharedUsageId, moving.EventInstrumentUsageId);
        Assert.Equal(
            [first.Id, moving.Id, after.Id],
            project.TracksInArrangementOrder().Select(value => value.TrackId));
    }

    [Fact]
    public void MovingAutoMidiTrackOutsideGroupCreatesRootAndUndoRestoresOriginalRoot()
    {
        MidoraProject project = new(480);
        MidiChannelRoot sharedRoot = new(project)
        {
            Name = "Shared",
            RoutingMode = MidiChannelRootRoutingMode.Auto,
            ChannelMode = MidiChannelMode.Melodic
        };
        project.MidiChannelRoots.Add(sharedRoot);
        PureMidiTrack first = AddPureMidiTrack(project, sharedRoot, "First");
        PureMidiTrack moving = AddPureMidiTrack(project, sharedRoot, "Moving");
        MidiChannelRoot otherRoot = new(project)
        {
            Name = "Other",
            RoutingMode = MidiChannelRootRoutingMode.Auto,
            ChannelMode = MidiChannelMode.Percussion
        };
        project.MidiChannelRoots.Add(otherRoot);
        PureMidiTrack other = AddPureMidiTrack(project, otherRoot, "Other");
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.MoveArrangementTrackOutsideSharedGroup(
            moving.Id,
            2));

        MidiChannelRoot independent = project.MidiChannelRoots.Single(
            value => value.Id == moving.MidiChannelRootId);
        Assert.NotEqual(sharedRoot.Id, independent.Id);
        Assert.Equal(MidiChannelRootRoutingMode.Auto, independent.RoutingMode);
        Assert.Equal(sharedRoot.ChannelMode, independent.ChannelMode);
        Assert.Equal(
            [first.Id, other.Id, moving.Id],
            project.TracksInArrangementOrder().Select(value => value.TrackId));

        document.Undo();

        Assert.Equal(sharedRoot.Id, moving.MidiChannelRootId);
        Assert.DoesNotContain(independent, project.MidiChannelRoots);
        Assert.Equal(
            [first.Id, moving.Id, other.Id],
            project.TracksInArrangementOrder().Select(value => value.TrackId));
    }

    [Fact]
    public void FixedRouteCreationReusesRootAndAllowsDispersedMembers()
    {
        MidoraProject project = new(480);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.CreatePureMidiTrackWithNewRoot(
            "Fixed A",
            MidiChannelRootRoutingMode.Fixed,
            oneBasedPort: 2,
            oneBasedChannel: 10,
            MidiChannelMode.Percussion));
        document.Execute(ProjectDomainEditCommands.CreatePureMidiTrackWithNewRoot("Auto"));
        document.Execute(ProjectDomainEditCommands.CreatePureMidiTrackWithNewRoot(
            "Fixed B",
            MidiChannelRootRoutingMode.Fixed,
            oneBasedPort: 2,
            oneBasedChannel: 10,
            MidiChannelMode.Percussion));
        PureMidiTrack first = project.PureMidiTracks.Single(value => value.Name == "Fixed A");
        PureMidiTrack second = project.PureMidiTracks.Single(value => value.Name == "Fixed B");
        PureMidiTrack auto = project.PureMidiTracks.Single(value => value.Name == "Auto");

        Assert.Equal(first.MidiChannelRootId, second.MidiChannelRootId);
        Assert.Equal(2, project.MidiChannelRoots.Count);

        document.Execute(ProjectDomainEditCommands.MoveArrangementTrack(second.Id, 0));

        Assert.Equal(
            [second.Id, first.Id, auto.Id],
            project.TracksInArrangementOrder().Select(value => value.TrackId));
        document.Execute(ProjectDomainEditCommands.MoveArrangementTrack(auto.Id, 1));
        Assert.Equal(
            [second.Id, auto.Id, first.Id],
            project.TracksInArrangementOrder().Select(value => value.TrackId));
    }

    [Fact]
    public void JoiningFixedRouteUsesTheTargetChipPositionAndDeletesTheEmptySourceRoot()
    {
        MidoraProject project = new(480);
        MidiChannelRoot fixedRoot = new(project)
        {
            Name = "Fixed",
            RoutingMode = MidiChannelRootRoutingMode.Fixed,
            FixedZeroBasedPort = 1,
            FixedZeroBasedChannel = 2,
            ChannelMode = MidiChannelMode.Melodic
        };
        project.MidiChannelRoots.Add(fixedRoot);
        PureMidiTrack fixedFirst = AddPureMidiTrack(project, fixedRoot, "Fixed First");
        MidiChannelRoot separatorRoot = new(project)
        {
            Name = "Separator",
            RoutingMode = MidiChannelRootRoutingMode.Auto,
            ChannelMode = MidiChannelMode.Melodic
        };
        project.MidiChannelRoots.Add(separatorRoot);
        PureMidiTrack separator = AddPureMidiTrack(project, separatorRoot, "Separator");
        PureMidiTrack fixedSecond = AddPureMidiTrack(project, fixedRoot, "Fixed Second");
        MidiChannelRoot movingRoot = new(project)
        {
            Name = "Moving",
            RoutingMode = MidiChannelRootRoutingMode.Auto,
            ChannelMode = MidiChannelMode.Percussion
        };
        project.MidiChannelRoots.Add(movingRoot);
        PureMidiTrack moving = AddPureMidiTrack(project, movingRoot, "Moving");
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.MoveArrangementTrackIntoSharedGroup(
            moving.Id,
            fixedFirst.Id));

        Assert.Equal(fixedRoot.Id, moving.MidiChannelRootId);
        Assert.DoesNotContain(movingRoot, project.MidiChannelRoots);
        Assert.Equal(
            [fixedFirst.Id, moving.Id, separator.Id, fixedSecond.Id],
            project.TracksInArrangementOrder().Select(value => value.TrackId));

        document.Undo();

        Assert.Equal(movingRoot.Id, moving.MidiChannelRootId);
        Assert.Contains(movingRoot, project.MidiChannelRoots);
        Assert.Equal(
            [fixedFirst.Id, separator.Id, fixedSecond.Id, moving.Id],
            project.TracksInArrangementOrder().Select(value => value.TrackId));
    }

    [Fact]
    public void ConfiguringOneTrackOfSharedFixedRouteMovesOnlyThatTrack()
    {
        MidoraProject project = new(480);
        MidiChannelRoot shared = new(project)
        {
            Name = "Shared Fixed",
            RoutingMode = MidiChannelRootRoutingMode.Fixed,
            FixedZeroBasedPort = 0,
            FixedZeroBasedChannel = 0,
            ChannelMode = MidiChannelMode.Melodic
        };
        project.MidiChannelRoots.Add(shared);
        PureMidiTrack first = AddPureMidiTrack(project, shared, "First");
        PureMidiTrack second = AddPureMidiTrack(project, shared, "Second");
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.ConfigurePureMidiTrackRoute(
            second.Id,
            MidiChannelRootRoutingMode.Fixed,
            oneBasedPort: 2,
            oneBasedChannel: 3,
            MidiChannelMode.Percussion));

        MidiChannelRoot moved = project.MidiChannelRoots.Single(value => value.Id == second.MidiChannelRootId);
        Assert.Equal(shared.Id, first.MidiChannelRootId);
        Assert.NotEqual(shared.Id, second.MidiChannelRootId);
        Assert.Equal(MidiChannelRootRoutingMode.Fixed, moved.RoutingMode);
        Assert.Equal(1, moved.FixedZeroBasedPort);
        Assert.Equal(2, moved.FixedZeroBasedChannel);
        Assert.Equal(MidiChannelMode.Percussion, moved.ChannelMode);

        document.Undo();

        Assert.Equal(shared.Id, first.MidiChannelRootId);
        Assert.Equal(shared.Id, second.MidiChannelRootId);
        Assert.Single(project.MidiChannelRoots);
    }

    [Fact]
    public void ConfiguringSharedFixedAddressChannelModeUpdatesAllMembersAtomically()
    {
        MidoraProject project = new(480);
        MidiChannelRoot shared = new(project)
        {
            Name = "Shared Fixed",
            RoutingMode = MidiChannelRootRoutingMode.Fixed,
            FixedZeroBasedPort = 2,
            FixedZeroBasedChannel = 4,
            ChannelMode = MidiChannelMode.Melodic
        };
        project.MidiChannelRoots.Add(shared);
        PureMidiTrack first = AddPureMidiTrack(project, shared, "First");
        PureMidiTrack second = AddPureMidiTrack(project, shared, "Second");
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.ConfigurePureMidiTrackRoute(
            first.Id,
            MidiChannelRootRoutingMode.Fixed,
            oneBasedPort: 3,
            oneBasedChannel: 5,
            MidiChannelMode.Percussion));

        Assert.Equal(MidiChannelMode.Percussion, shared.ChannelMode);
        Assert.Equal(shared.Id, first.MidiChannelRootId);
        Assert.Equal(shared.Id, second.MidiChannelRootId);
        Assert.Single(project.MidiChannelRoots);

        document.Undo();

        Assert.Equal(MidiChannelMode.Melodic, shared.ChannelMode);
        Assert.Equal(shared.Id, first.MidiChannelRootId);
        Assert.Equal(shared.Id, second.MidiChannelRootId);

        document.Redo();

        Assert.Equal(MidiChannelMode.Percussion, shared.ChannelMode);
        Assert.Single(project.MidiChannelRoots);
    }

    [Fact]
    public void ConfiguringMiddleAutoMemberMovesItOutsideTheRemainingBlock()
    {
        MidoraProject project = new(480);
        MidiChannelRoot shared = new(project)
        {
            Name = "Shared Auto",
            RoutingMode = MidiChannelRootRoutingMode.Auto,
            ChannelMode = MidiChannelMode.Melodic
        };
        project.MidiChannelRoots.Add(shared);
        PureMidiTrack first = AddPureMidiTrack(project, shared, "First");
        PureMidiTrack middle = AddPureMidiTrack(project, shared, "Middle");
        PureMidiTrack last = AddPureMidiTrack(project, shared, "Last");
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.ConfigurePureMidiTrackRoute(
            middle.Id,
            MidiChannelRootRoutingMode.Fixed,
            oneBasedPort: 4,
            oneBasedChannel: 5,
            MidiChannelMode.Melodic));

        Assert.Equal(
            [first.Id, last.Id, middle.Id],
            project.TracksInArrangementOrder().Select(value => value.TrackId));
        Assert.Equal(shared.Id, first.MidiChannelRootId);
        Assert.Equal(shared.Id, last.MidiChannelRootId);
        Assert.NotEqual(shared.Id, middle.MidiChannelRootId);

        document.Undo();

        Assert.Equal(
            [first.Id, middle.Id, last.Id],
            project.TracksInArrangementOrder().Select(value => value.TrackId));
        Assert.All(new[] { first, middle, last }, value => Assert.Equal(shared.Id, value.MidiChannelRootId));
        Assert.Single(project.MidiChannelRoots);
    }

    [Fact]
    public void ConfiguringSingletonToExistingFixedRouteDeletesEmptySourceRoot()
    {
        MidoraProject project = new(480);
        MidiChannelRoot target = new(project)
        {
            Name = "Target",
            RoutingMode = MidiChannelRootRoutingMode.Fixed,
            FixedZeroBasedPort = 2,
            FixedZeroBasedChannel = 3,
            ChannelMode = MidiChannelMode.Percussion
        };
        project.MidiChannelRoots.Add(target);
        PureMidiTrack targetTrack = AddPureMidiTrack(project, target, "Target");
        MidiChannelRoot source = new(project)
        {
            Name = "Source",
            RoutingMode = MidiChannelRootRoutingMode.Auto,
            ChannelMode = MidiChannelMode.Melodic
        };
        project.MidiChannelRoots.Add(source);
        PureMidiTrack moving = AddPureMidiTrack(project, source, "Moving");
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.ConfigurePureMidiTrackRoute(
            moving.Id,
            MidiChannelRootRoutingMode.Fixed,
            oneBasedPort: 3,
            oneBasedChannel: 4,
            MidiChannelMode.Percussion));

        Assert.Equal(target.Id, targetTrack.MidiChannelRootId);
        Assert.Equal(target.Id, moving.MidiChannelRootId);
        Assert.DoesNotContain(source, project.MidiChannelRoots);

        document.Undo();

        Assert.Equal(source.Id, moving.MidiChannelRootId);
        Assert.Contains(source, project.MidiChannelRoots);
    }

    [Fact]
    public void PlainReorderCannotSplitLogicalSharedGroup()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalTrack first = AddLogicalTrack(project, instrument, "First");
        LogicalTrack second = new(project)
        {
            Name = "Second",
            EventInstrumentUsageId = first.EventInstrumentUsageId,
            LastBoundEventInstrumentName = instrument.Name
        };
        project.Tracks.Add(second);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, second.Id));
        LogicalTrack other = AddLogicalTrack(project, instrument, "Other");
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            document.Execute(ProjectDomainEditCommands.ReorderLogicalTrack(first.Id, 2)));

        Assert.Contains("split", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            [first.Id, second.Id, other.Id],
            project.TracksInArrangementOrder().Select(value => value.TrackId));
        Assert.Empty(document.History);
    }

    [Fact]
    public void RebindingSharedUsageUpdatesEveryMemberAndUndoRestoresDefinition()
    {
        MidoraProject project = new(480);
        EventInstrument firstInstrument = EventInstrumentLibrary.Create(project, "First Instrument");
        EventInstrument secondInstrument = EventInstrumentLibrary.Create(project, "Second Instrument");
        LogicalTrack first = AddLogicalTrack(project, firstInstrument, "First");
        LogicalTrack second = new(project)
        {
            Name = "Second",
            EventInstrumentUsageId = first.EventInstrumentUsageId,
            LastBoundEventInstrumentName = firstInstrument.Name
        };
        project.Tracks.Add(second);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, second.Id));
        MidoraId usageId = first.EventInstrumentUsageId!.Value;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.RebindEventInstrumentUsage(
            usageId,
            secondInstrument.Id));

        Assert.Equal(
            secondInstrument.Id,
            project.EventInstrumentUsages.Single(value => value.Id == usageId).EventInstrumentId);
        Assert.All(
            new[] { first, second },
            value => Assert.Equal(secondInstrument.Name, value.LastBoundEventInstrumentName));

        document.Undo();

        Assert.Equal(
            firstInstrument.Id,
            project.EventInstrumentUsages.Single(value => value.Id == usageId).EventInstrumentId);
        Assert.All(
            new[] { first, second },
            value => Assert.Equal(firstInstrument.Name, value.LastBoundEventInstrumentName));
    }

    [Fact]
    public void MakingLogicalGroupIndependentCreatesOneUsagePerTrackAndUndoRestoresGroup()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalTrack first = AddLogicalTrack(project, instrument, "First");
        LogicalTrack second = new(project)
        {
            Name = "Second",
            EventInstrumentUsageId = first.EventInstrumentUsageId,
            LastBoundEventInstrumentName = instrument.Name
        };
        LogicalTrack third = new(project)
        {
            Name = "Third",
            EventInstrumentUsageId = first.EventInstrumentUsageId,
            LastBoundEventInstrumentName = instrument.Name
        };
        project.Tracks.Add(second);
        project.Tracks.Add(third);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, second.Id));
        project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, third.Id));
        MidoraId sharedUsageId = first.EventInstrumentUsageId!.Value;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.MakeArrangementSharedGroupIndependent(
            sharedUsageId,
            ArrangementTrackKind.LogicalTrack));

        Assert.Equal(3, new[] { first, second, third }
            .Select(value => value.EventInstrumentUsageId)
            .Distinct()
            .Count());
        Assert.All(
            project.EventInstrumentUsages,
            value => Assert.Equal(instrument.Id, value.EventInstrumentId));

        document.Undo();

        Assert.All(
            new[] { first, second, third },
            value => Assert.Equal(sharedUsageId, value.EventInstrumentUsageId));
        Assert.Single(project.EventInstrumentUsages);
    }

    [Fact]
    public void MakingAutoMidiGroupIndependentCreatesOneRootPerTrackAndUndoRestoresGroup()
    {
        MidoraProject project = new(480);
        MidiChannelRoot sharedRoot = new(project)
        {
            Name = "Shared",
            RoutingMode = MidiChannelRootRoutingMode.Auto,
            ChannelMode = MidiChannelMode.Percussion
        };
        project.MidiChannelRoots.Add(sharedRoot);
        PureMidiTrack first = AddPureMidiTrack(project, sharedRoot, "First");
        PureMidiTrack second = AddPureMidiTrack(project, sharedRoot, "Second");
        PureMidiTrack third = AddPureMidiTrack(project, sharedRoot, "Third");
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.MakeArrangementSharedGroupIndependent(
            sharedRoot.Id,
            ArrangementTrackKind.PureMidiTrack));

        Assert.Equal(3, new[] { first, second, third }
            .Select(value => value.MidiChannelRootId)
            .Distinct()
            .Count());
        Assert.All(project.MidiChannelRoots, value =>
        {
            Assert.Equal(MidiChannelRootRoutingMode.Auto, value.RoutingMode);
            Assert.Equal(MidiChannelMode.Percussion, value.ChannelMode);
        });

        document.Undo();

        Assert.All(
            new[] { first, second, third },
            value => Assert.Equal(sharedRoot.Id, value.MidiChannelRootId));
        Assert.Single(project.MidiChannelRoots);
    }

    [Fact]
    public void LogicalSegmentBatchUsesMixedArrangementLaneOffsetsAtomically()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalTrack targetFirst = AddLogicalTrack(project, instrument, "Target First");
        LogicalTrack targetSecond = AddLogicalTrack(project, instrument, "Target Second");
        MidiChannelRoot firstRoot = new(project)
        {
            Name = "First MIDI",
            RoutingMode = MidiChannelRootRoutingMode.Auto,
            ChannelMode = MidiChannelMode.Melodic
        };
        project.MidiChannelRoots.Add(firstRoot);
        PureMidiTrack incompatibleTarget = AddPureMidiTrack(project, firstRoot, "MIDI Target");
        LogicalTrack sourceFirst = AddLogicalTrack(project, instrument, "Source First");
        MidiChannelRoot secondRoot = new(project)
        {
            Name = "Second MIDI",
            RoutingMode = MidiChannelRootRoutingMode.Auto,
            ChannelMode = MidiChannelMode.Melodic
        };
        project.MidiChannelRoots.Add(secondRoot);
        _ = AddPureMidiTrack(project, secondRoot, "MIDI Separator");
        LogicalTrack sourceSecond = AddLogicalTrack(project, instrument, "Source Second");
        Segment first = new(project) { ProjectStartTick = 100, LengthTicks = 60 };
        Segment second = new(project) { ProjectStartTick = 200, LengthTicks = 60 };
        sourceFirst.Segments.Add(first);
        sourceSecond.Segments.Add(second);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            document.Execute(ProjectDomainEditCommands.MoveSegments(
                [first.Id, second.Id],
                first.Id,
                targetFirst.Id,
                newPrimaryStartTick: 300)));

        Assert.Contains("Arrangement lane offsets", error.Message, StringComparison.Ordinal);
        Assert.Equal([first], sourceFirst.Segments);
        Assert.Equal([second], sourceSecond.Segments);
        Assert.Empty(targetFirst.Segments);
        Assert.Empty(targetSecond.Segments);
        Assert.Empty(incompatibleTarget.Segments);
        Assert.Empty(document.History);
    }

    [Fact]
    public void LogicalSegmentClipboardUsesMixedArrangementLaneOffsetsAtomically()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalTrack targetFirst = AddLogicalTrack(project, instrument, "Target First");
        LogicalTrack targetSecond = AddLogicalTrack(project, instrument, "Target Second");
        MidiChannelRoot targetRoot = new(project)
        {
            Name = "MIDI Target",
            RoutingMode = MidiChannelRootRoutingMode.Auto,
            ChannelMode = MidiChannelMode.Melodic
        };
        project.MidiChannelRoots.Add(targetRoot);
        PureMidiTrack incompatibleTarget = AddPureMidiTrack(project, targetRoot, "MIDI Target");
        LogicalTrack sourceFirst = AddLogicalTrack(project, instrument, "Source First");
        MidiChannelRoot separatorRoot = new(project)
        {
            Name = "MIDI Separator",
            RoutingMode = MidiChannelRootRoutingMode.Auto,
            ChannelMode = MidiChannelMode.Melodic
        };
        project.MidiChannelRoots.Add(separatorRoot);
        _ = AddPureMidiTrack(project, separatorRoot, "MIDI Separator");
        LogicalTrack sourceSecond = AddLogicalTrack(project, instrument, "Source Second");
        Segment first = new(project) { ProjectStartTick = 100, LengthTicks = 60 };
        Segment second = new(project) { ProjectStartTick = 200, LengthTicks = 60 };
        sourceFirst.Segments.Add(first);
        sourceSecond.Segments.Add(second);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        ProjectObjectClipboardPayload payload = ProjectObjectClipboard.CopySegments(
            document,
            [first.Id, second.Id],
            first.Id);
        long nextStableId = project.NextStableId;

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            document.Execute(ProjectObjectClipboard.CreatePasteSegmentsCommand(
                document,
                payload,
                targetFirst.Id,
                editCursorTick: 300)));

        Assert.Contains("Arrangement lane offsets", error.Message, StringComparison.Ordinal);
        Assert.Empty(targetFirst.Segments);
        Assert.Empty(targetSecond.Segments);
        Assert.Empty(incompatibleTarget.Segments);
        Assert.Equal(nextStableId, project.NextStableId);
        Assert.Empty(document.History);
    }

    [Fact]
    public void MidiSegmentBatchUsesMixedArrangementLaneOffsetsAtomically()
    {
        MidoraProject project = new(480);
        MidiChannelRoot targetRoot = new(project)
        {
            Name = "Target",
            RoutingMode = MidiChannelRootRoutingMode.Fixed,
            ChannelMode = MidiChannelMode.Melodic
        };
        project.MidiChannelRoots.Add(targetRoot);
        PureMidiTrack target = AddPureMidiTrack(project, targetRoot, "Target");
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        _ = AddLogicalTrack(project, instrument, "Logical Separator");
        LogicalTrack incompatibleTarget = AddLogicalTrack(project, instrument, "Logical Target");
        MidiChannelRoot sourceRoot = new(project)
        {
            Name = "Source",
            RoutingMode = MidiChannelRootRoutingMode.Fixed,
            FixedZeroBasedChannel = 1,
            ChannelMode = MidiChannelMode.Melodic
        };
        project.MidiChannelRoots.Add(sourceRoot);
        PureMidiTrack sourceFirst = AddPureMidiTrack(project, sourceRoot, "Source First");
        _ = AddLogicalTrack(project, instrument, "Logical Separator 2");
        PureMidiTrack sourceSecond = AddPureMidiTrack(project, sourceRoot, "Source Second");
        MidiSegment first = new(project) { ProjectStartTick = 100, LengthTicks = 60 };
        MidiSegment second = new(project) { ProjectStartTick = 200, LengthTicks = 60 };
        sourceFirst.Segments.Add(first);
        sourceSecond.Segments.Add(second);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            document.Execute(ProjectDomainEditCommands.MoveMidiSegments(
                [first.Id, second.Id],
                first.Id,
                target.Id,
                newPrimaryStartTick: 300)));

        Assert.Contains("Arrangement lane offsets", error.Message, StringComparison.Ordinal);
        Assert.Empty(target.Segments);
        Assert.Empty(incompatibleTarget.Segments);
        Assert.Equal([first], sourceFirst.Segments);
        Assert.Equal([second], sourceSecond.Segments);
        Assert.Empty(document.History);
    }

    [Fact]
    public void UnboundLogicalTrackRejectsMusicalContentUntilBound()
    {
        MidoraProject project = new(480);
        LogicalTrack track = new(project) { Name = "Unbound" };
        ProjectGraphConstruction.AddUnboundLogicalTrack(project, track);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            document.Execute(ProjectDomainEditCommands.CreateSegment(
                track.Id,
                projectStartTick: 0,
                lengthTicks: 480)));

        Assert.Contains("Bind an Event Instrument", error.Message, StringComparison.Ordinal);
        Assert.Empty(track.Segments);
        Assert.Empty(document.History);
    }

    private static LogicalTrack AddLogicalTrack(
        MidoraProject project,
        EventInstrument instrument,
        string name)
    {
        LogicalTrack track = new(project) { Name = name };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        return track;
    }

    private static PureMidiTrack AddPureMidiTrack(
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
}
