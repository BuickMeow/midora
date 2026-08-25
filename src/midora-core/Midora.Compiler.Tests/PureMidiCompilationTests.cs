using Midora.Domain;
using Midora.Midi;

namespace Midora.Compiler.Tests;

public sealed class PureMidiCompilationTests
{
    [Fact]
    public void RootMergesChildTracksInExplicitOrderAndKeepsDirectDuplicates()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = AddRoot(project, fixedChannel: 9, MidiChannelMode.Percussion);
        PureMidiTrack first = AddTrack(project, root, "First");
        PureMidiTrack second = AddTrack(project, root, "Second");
        MidiSegment firstSegment = AddSegment(project, first, 0, 480);
        MidiSegment secondSegment = AddSegment(project, second, 0, 480);
        firstSegment.ChannelEvents.Add(new DirectMidiChannelEvent(project)
        {
            Tick = 10,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 7,
            Data2 = 20,
            Order = 1
        });
        secondSegment.ChannelEvents.Add(new DirectMidiChannelEvent(project)
        {
            Tick = 10,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 7,
            Data2 = 90,
            Order = 1
        });

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(project);

        Assert.True(result.IsConsumable, string.Join(Environment.NewLine, result.Diagnostics));
        CanonicalMidiEvent[] direct = result.Events.ToArray()
            .Where(value => value.Tick == 10 && value.Role == CanonicalEventRole.DirectMidi)
            .ToArray();
        Assert.Equal(2, direct.Length);
        Assert.Equal((byte)20, direct[0].Message.Byte2);
        Assert.Equal((byte)90, direct[1].Message.Byte2);
        Assert.Equal(first.Id, direct[0].ExportTrackId);
        Assert.Equal(second.Id, direct[1].ExportTrackId);
        Assert.Single(result.Allocations.ToArray(), value => value.MidiChannelRootId == root.Id);
        Assert.Equal(MidiChannelMode.Percussion, result.Allocations[0].ChannelMode);
    }

    [Fact]
    public void AdjacentChildSegmentsShareOneRootLifecycle()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = AddRoot(project, fixedChannel: 0, MidiChannelMode.Melodic);
        PureMidiTrack first = AddTrack(project, root, "First");
        PureMidiTrack second = AddTrack(project, root, "Second");
        MidiSegment left = AddSegment(project, first, 0, 240);
        MidiSegment right = AddSegment(project, second, 240, 240);
        left.Notes.Add(NewNote(project, 0, 400, 60));
        right.Notes.Add(NewNote(project, 0, 120, 62));

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(project);

        Assert.True(result.IsConsumable, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.DoesNotContain(result.Events.ToArray(), value => value.Tick == 240
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 120);
        Assert.Contains(result.Events.ToArray(), value => value.Tick == 240
            && value.Message.MessageType == MidiMessageType.NoteOff
            && value.Message.Byte1 == 60);
        Assert.Contains(result.Events.ToArray(), value => value.Tick == 480
            && value.Role == CanonicalEventRole.RootBoundaryCleanup
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 120);
    }

    [Fact]
    public void FixedRootsAreReservedBeforeAutoRoots()
    {
        MidoraProject project = new(480);
        MidiChannelRoot fixedRoot = AddRoot(project, fixedChannel: 0, MidiChannelMode.Melodic);
        _ = AddTrack(project, fixedRoot, "Fixed Track");
        MidiChannelRoot autoRoot = new(project)
        {
            Name = "Auto",
            RoutingMode = MidiChannelRootRoutingMode.Auto,
            ChannelMode = MidiChannelMode.Melodic
        };
        project.MidiChannelRoots.Add(autoRoot);
        PureMidiTrack track = AddTrack(project, autoRoot, "Auto Track");
        AddSegment(project, track, 0, 120).Notes.Add(NewNote(project, 0, 60, 64));

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(project);

        Assert.True(result.IsConsumable, string.Join(Environment.NewLine, result.Diagnostics));
        ChannelUnitAllocation allocation = Assert.Single(
            result.Allocations.ToArray(), value => value.MidiChannelRootId == autoRoot.Id);
        Assert.Equal((byte)0, allocation.ZeroBasedPort);
        Assert.Equal((byte)1, allocation.ZeroBasedChannel);
        Assert.Equal(1, result.Statistics.ReservedMidiRootUnitCount);
        Assert.Equal(2, result.Statistics.AllocatedMidiRootUnitCount);
        Assert.DoesNotContain(result.Allocations.ToArray(), value => value.MidiChannelRootId == fixedRoot.Id);
    }

    [Fact]
    public void LogicalAllocationAlsoBypassesFixedAndParticipatingAutoRoots()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 240);
        fixture.Voice.Events.Add(TemplateEvent.Note(
            fixture.Project,
            0,
            120,
            60,
            100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 120);

        MidiChannelRoot fixedRoot = AddRoot(
            fixture.Project,
            fixedChannel: 0,
            MidiChannelMode.Melodic);
        _ = AddTrack(fixture.Project, fixedRoot, "Fixed Track");
        MidiChannelRoot autoRoot = new(fixture.Project)
        {
            Name = "Auto",
            RoutingMode = MidiChannelRootRoutingMode.Auto,
            ChannelMode = MidiChannelMode.Melodic
        };
        fixture.Project.MidiChannelRoots.Add(autoRoot);
        PureMidiTrack midiTrack = AddTrack(fixture.Project, autoRoot, "Auto Track");
        AddSegment(fixture.Project, midiTrack, 0, 120).Notes.Add(
            NewNote(fixture.Project, 0, 120, 64));

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable, string.Join(Environment.NewLine, result.Diagnostics));
        ChannelUnitAllocation rootAllocation = Assert.Single(
            result.Allocations.ToArray(),
            value => value.MidiChannelRootId == autoRoot.Id);
        ChannelUnitAllocation logicalAllocation = Assert.Single(
            result.Allocations.ToArray(),
            value => value.MidiChannelRootId == default);
        Assert.Equal((byte)0, rootAllocation.ZeroBasedPort);
        Assert.Equal((byte)1, rootAllocation.ZeroBasedChannel);
        Assert.Equal((byte)0, logicalAllocation.ZeroBasedPort);
        Assert.Equal((byte)2, logicalAllocation.ZeroBasedChannel);
        Assert.Equal(1, result.Statistics.ReservedMidiRootUnitCount);
        Assert.DoesNotContain(
            result.Allocations.ToArray(),
            value => value.MidiChannelRootId == fixedRoot.Id);
    }

    [Fact]
    public void ExplicitLogicalTrackScopeDoesNotReserveAnExcludedFixedRoot()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 240);
        fixture.Voice.Events.Add(TemplateEvent.Note(
            fixture.Project,
            0,
            120,
            60,
            100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 120);
        MidiChannelRoot fixedRoot = AddRoot(
            fixture.Project,
            fixedChannel: 0,
            MidiChannelMode.Melodic);
        PureMidiTrack midiTrack = AddTrack(fixture.Project, fixedRoot, "Excluded");
        AddSegment(fixture.Project, midiTrack, 0, 120).Notes.Add(
            NewNote(fixture.Project, 0, 120, 64));

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest
            {
                IncludedTrackIds = [fixture.Track.Id]
            });

        Assert.True(result.IsConsumable, string.Join(Environment.NewLine, result.Diagnostics));
        ChannelUnitAllocation allocation = Assert.Single(result.Allocations.ToArray());
        Assert.Equal(default, allocation.MidiChannelRootId);
        Assert.Equal((byte)0, allocation.ZeroBasedPort);
        Assert.Equal((byte)0, allocation.ZeroBasedChannel);
        Assert.Equal(0, result.Statistics.ReservedMidiRootUnitCount);
        Assert.Empty(result.SmfTracks.ToArray());
    }

    [Fact]
    public void RootLifecycleResetsOnlyTargetsUsedByItsOwnConnectedInterval()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = AddRoot(project, fixedChannel: 0, MidiChannelMode.Melodic);
        PureMidiTrack track = AddTrack(project, root, "State");
        MidiSegment first = AddSegment(project, track, 0, 100);
        MidiSegment second = AddSegment(project, track, 200, 100);
        first.ChannelEvents.Add(new DirectMidiChannelEvent(project)
        {
            Tick = 0,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 10,
            Data2 = 32,
            Order = 1
        });
        second.ChannelEvents.Add(new DirectMidiChannelEvent(project)
        {
            Tick = 0,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 11,
            Data2 = 64,
            Order = 1
        });

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(project);

        Assert.True(result.IsConsumable, string.Join(Environment.NewLine, result.Diagnostics));
        CanonicalMidiEvent[] firstBoundary = result.Events.ToArray()
            .Where(value => value.Tick == 100
                && value.Source.Origin == SourceOrigin.MidiChannelRootLifecycle)
            .ToArray();
        CanonicalMidiEvent[] secondStart = result.Events.ToArray()
            .Where(value => value.Tick == 200
                && value.Source.Origin == SourceOrigin.MidiChannelRootLifecycle)
            .ToArray();
        Assert.Contains(firstBoundary, value => value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 10);
        Assert.DoesNotContain(firstBoundary, value => value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 11);
        Assert.Contains(secondStart, value => value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 11);
        Assert.DoesNotContain(secondStart, value => value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 10);
    }

    [Fact]
    public void MidiExportAggregatesCrossTrackSameTickCompatibilityWarningsPerRoot()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = AddRoot(project, fixedChannel: 0, MidiChannelMode.Melodic);
        PureMidiTrack first = AddTrack(project, root, "First");
        PureMidiTrack second = AddTrack(project, root, "Second");
        AddSegment(project, first, 0, 480).ChannelEvents.Add(new DirectMidiChannelEvent(project)
        {
            Tick = 24,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 7,
            Data2 = 20,
            Order = 1
        });
        AddSegment(project, second, 0, 480).ChannelEvents.Add(new DirectMidiChannelEvent(project)
        {
            Tick = 24,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 7,
            Data2 = 90,
            Order = 1
        });

        using MidoraCompiler compiler = new();
        CanonicalCompiledResult playback = compiler.CompileFull(project);
        CanonicalCompiledResult export = compiler.CompileFull(
            project,
            new CompilationRequest { Purpose = CompilationPurpose.MidiExport });
        CanonicalCompiledResult strictExport = compiler.CompileFull(
            project,
            new CompilationRequest
            {
                Purpose = CompilationPurpose.MidiExport,
                TreatWarningsAsErrors = true
            });

        Assert.DoesNotContain(playback.Diagnostics, value => value.Code == "MIDORA2251");
        CompilerDiagnostic warning = Assert.Single(export.Diagnostics, value => value.Code == "MIDORA2251");
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal(root.Id, warning.Source.MidiChannelRootId);
        Assert.Equal(24, warning.Source.Tick);
        Assert.True(export.IsConsumable);
        Assert.False(strictExport.IsConsumable);
        Assert.Equal(CompilationFailureStage.WarningPolicy, strictExport.FailureStage);
    }

    [Fact]
    public void RecognizedChannelModeSystemExclusiveIsMaterializedForAudio()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = AddRoot(project, fixedChannel: 3, MidiChannelMode.Melodic);
        PureMidiTrack track = AddTrack(project, root, "Mode");
        MidiSegment segment = AddSegment(project, track, 0, 480);
        OpaqueMidiEvent source = new(project)
        {
            Tick = 120,
            Kind = OpaqueMidiEventKind.SystemExclusive,
            Payload = [0x43, 0x10, 0x4c, 0x08, 0x03, 0x07, 0x01, 0xf7],
            Order = 7
        };
        segment.OpaqueEvents.Add(source);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(project);

        Assert.True(result.IsConsumable, string.Join(Environment.NewLine, result.Diagnostics));
        CanonicalMidiChannelModeSystemExclusiveEvent value = Assert.Single(
            result.ChannelModeSystemExclusiveEvents.ToArray());
        Assert.Equal(120, value.Tick);
        Assert.Equal((byte)0, value.ZeroBasedPort);
        Assert.Equal((byte)3, value.ZeroBasedChannel);
        Assert.Equal(MidiChannelModeSystemExclusiveKind.YamahaXgPartMode, value.Value.Kind);
        Assert.Equal((byte)3, value.Value.TargetChannel);
        Assert.Equal((byte)1, value.Value.ModeValue);
        Assert.Equal(CanonicalEventRole.DirectMidi, value.Role);
        Assert.Equal(track.Id, value.Source.TrackId);
        Assert.Equal(source.Id, value.Source.DirectMidiObjectId);
    }

    [Fact]
    public void RangeStartRestoresLatestRecognizedChannelModeWithinActiveRootInterval()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = AddRoot(project, fixedChannel: 5, MidiChannelMode.Melodic);
        PureMidiTrack track = AddTrack(project, root, "Mode");
        MidiSegment segment = AddSegment(project, track, 0, 480);
        segment.OpaqueEvents.Add(new(project)
        {
            Tick = 40,
            Kind = OpaqueMidiEventKind.SystemExclusive,
            Payload = [0x43, 0x10, 0x4c, 0x08, 0x05, 0x07, 0x01, 0xf7],
            Order = 4
        });
        segment.OpaqueEvents.Add(new(project)
        {
            Tick = 80,
            Kind = OpaqueMidiEventKind.SystemExclusive,
            Payload = [0x43, 0x10, 0x4c, 0x08, 0x05, 0x07, 0x00, 0xf7],
            Order = 8
        });

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(
            project,
            new CompilationRequest
            {
                Purpose = CompilationPurpose.Playback,
                StartTick = 120,
                EndTick = 240
            });

        Assert.True(result.IsConsumable, string.Join(Environment.NewLine, result.Diagnostics));
        CanonicalMidiChannelModeSystemExclusiveEvent value = Assert.Single(
            result.ChannelModeSystemExclusiveEvents.ToArray());
        Assert.Equal(120, value.Tick);
        Assert.Equal(CanonicalEventRole.RangeRestore, value.Role);
        Assert.Equal((byte)0, value.Value.ModeValue);
    }

    [Fact]
    public void ArbitrarySystemExclusiveRemainsOpaqueAndIsNotMaterializedForAudio()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = AddRoot(project, fixedChannel: 0, MidiChannelMode.Melodic);
        PureMidiTrack track = AddTrack(project, root, "Opaque");
        MidiSegment segment = AddSegment(project, track, 0, 480);
        segment.OpaqueEvents.Add(new(project)
        {
            Tick = 12,
            Kind = OpaqueMidiEventKind.SystemExclusive,
            Payload = [0x7d, 0x01, 0x02, 0xf7],
            Order = 1
        });

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(project);

        Assert.True(result.IsConsumable, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Empty(result.ChannelModeSystemExclusiveEvents.ToArray());
        Assert.Single(result.OpaqueMidiEvents.ToArray());
    }

    [Fact]
    public void ChannelModeSystemExclusiveIsFullIncrementalEquivalentAfterEdit()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = AddRoot(project, fixedChannel: 2, MidiChannelMode.Melodic);
        PureMidiTrack track = AddTrack(project, root, "Mode");
        MidiSegment segment = AddSegment(project, track, 0, 480);
        OpaqueMidiEvent source = new(project)
        {
            Tick = 24,
            Kind = OpaqueMidiEventKind.SystemExclusive,
            Payload = [0x43, 0x10, 0x4c, 0x08, 0x02, 0x07, 0x00, 0xf7],
            Order = 1
        };
        segment.OpaqueEvents.Add(source);
        using MidoraCompiler incrementalCompiler = new();
        _ = incrementalCompiler.CompileFull(project);
        source.Payload = [0x43, 0x10, 0x4c, 0x08, 0x02, 0x07, 0x01, 0xf7];
        ProjectChangeSet changes = new();
        changes.TrackIds.Add(track.Id);

        CanonicalCompiledResult incremental = incrementalCompiler.CompileIncremental(
            project,
            changes);
        using MidoraCompiler fullCompiler = new();
        CanonicalCompiledResult full = fullCompiler.CompileFull(project);

        Assert.Equal(full.Fingerprint, incremental.Fingerprint);
        Assert.Equal(
            full.ChannelModeSystemExclusiveEvents.ToArray(),
            incremental.ChannelModeSystemExclusiveEvents.ToArray());
    }

    private static MidiChannelRoot AddRoot(
        MidoraProject project,
        byte fixedChannel,
        MidiChannelMode mode)
    {
        MidiChannelRoot root = new(project)
        {
            Name = "Root",
            RoutingMode = MidiChannelRootRoutingMode.Fixed,
            FixedZeroBasedPort = 0,
            FixedZeroBasedChannel = fixedChannel,
            ChannelMode = mode
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

    private static DirectMidiNote NewNote(
        MidoraProject project,
        long start,
        long length,
        int key) => new(project)
        {
            StartTick = start,
            LengthTicks = length,
            Key = key,
            NoteOnVelocity = 100,
            NoteOffVelocity = 23,
            NoteOnOrder = 1,
            NoteOffOrder = 2
        };
}
