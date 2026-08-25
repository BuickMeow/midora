using Midora.Compiler;
using Midora.Domain;
using Midora.Mapping.Contract.V2;
using Midora.Midi;

namespace Midora.Playback.Tests;

public sealed class ProjectCompilationSessionBackgroundTests
{
    [Fact]
    public async Task BackgroundSnapshotIncludesAndSynchronizesPureMidiBranch()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = new(project)
        {
            Name = "Root",
            RoutingMode = MidiChannelRootRoutingMode.Auto,
            ChannelMode = MidiChannelMode.Melodic
        };
        PureMidiTrack track = new(project)
        {
            Name = "MIDI Track",
            MidiChannelRootId = root.Id
        };
        MidiSegment segment = new(project)
        {
            LengthTicks = 480
        };
        DirectMidiNote note = new(project)
        {
            StartTick = 0,
            LengthTicks = 120,
            Key = 60,
            NoteOnVelocity = 100,
            NoteOffVelocity = 31,
            NoteOnOrder = 10,
            NoteOffOrder = 20
        };
        segment.Notes.Add(note);
        track.Segments.Add(segment);
        project.MidiChannelRoots.Add(root);
        project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        using ProjectCompilationSession session = new(
            project,
            executionMode: ProjectCompilationExecutionMode.Background,
            backgroundDebounce: TimeSpan.Zero);

        Assert.Contains(
            session.LastAttempt.Events.ToArray(),
            value => value.Role == CanonicalEventRole.DirectMidi
                && value.Message.MessageType == MidiMessageType.NoteOn
                && value.Message.Byte1 == 60);

        ProjectChangeSet changes = new();
        changes.PureMidiTrackIds.Add(track.Id);
        _ = session.ApplyEdit(_ => note.Key = 65, changes);

        CanonicalCompiledResult current = await session.EnsureCurrentCompilationAsync();
        CanonicalCompiledResult full = new MidoraCompiler().CompileFull(project);

        Assert.Equal(full.Events.ToArray(), current.Events.ToArray());
        Assert.Equal(full.SmfTracks.ToArray(), current.SmfTracks.ToArray());
        Assert.Equal(full.OpaqueMidiEvents.ToArray(), current.OpaqueMidiEvents.ToArray());
        Assert.Equal(full.Fingerprint, current.Fingerprint);
        Assert.Contains(
            current.Events.ToArray(),
            value => value.Role == CanonicalEventRole.DirectMidi
                && value.Message.MessageType == MidiMessageType.NoteOn
                && value.Message.Byte1 == 65);

        ProjectChangeSet rootChanges = new();
        rootChanges.MidiChannelRootIds.Add(root.Id);
        _ = session.ApplyEdit(_ =>
        {
            root.RoutingMode = MidiChannelRootRoutingMode.Fixed;
            root.FixedZeroBasedPort = 2;
            root.FixedZeroBasedChannel = 3;
        }, rootChanges);

        current = await session.EnsureCurrentCompilationAsync();
        full = new MidoraCompiler().CompileFull(project);

        Assert.Equal(full.Events.ToArray(), current.Events.ToArray());
        Assert.Equal(full.Fingerprint, current.Fingerprint);
        Assert.All(current.Events.ToArray(), value =>
        {
            Assert.Equal((byte)2, value.ZeroBasedPort);
            Assert.Equal((byte)3, value.ZeroBasedChannel);
        });

        ProjectChangeSet hierarchyChanges = new();
        hierarchyChanges.MidiChannelRootIds.Add(root.Id);
        _ = session.ApplyEdit(value =>
        {
            PureMidiTrack addedTrack = new(value)
            {
                Name = "Added MIDI Track",
                MidiChannelRootId = root.Id
            };
            MidiSegment addedSegment = new(value)
            {
                ProjectStartTick = 480,
                LengthTicks = 240
            };
            addedSegment.ChannelEvents.Add(new DirectMidiChannelEvent(value)
            {
                Tick = 0,
                Kind = DirectMidiChannelEventKind.ControlChange,
                Data1 = 11,
                Data2 = 80,
                Order = 100
            });
            addedTrack.Segments.Add(addedSegment);
            value.PureMidiTracks.Add(addedTrack);
            value.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, addedTrack.Id));
        }, hierarchyChanges);

        current = await session.EnsureCurrentCompilationAsync();
        full = new MidoraCompiler().CompileFull(project);

        Assert.Equal(full.Events.ToArray(), current.Events.ToArray());
        Assert.Equal(full.SmfTracks.ToArray(), current.SmfTracks.ToArray());
        Assert.Equal(full.Fingerprint, current.Fingerprint);
    }

    [Fact]
    public async Task BackgroundEditPublishesOnlyTheCurrentSourceRevision()
    {
        MidoraProject project = new(480);
        project.Conductor.EndMarker = new ProjectEndMarker(project, 960);
        using ProjectCompilationSession session = new(
            project,
            executionMode: ProjectCompilationExecutionMode.Background,
            backgroundDebounce: TimeSpan.FromMilliseconds(10));
        CanonicalCompiledResult previous = session.LastAttempt;

        CanonicalCompiledResult editReturn = session.ApplyEdit(
            value => value.Conductor.Markers.Add(new ProjectMarker(value, 240, "A")),
            new ProjectChangeSet { AffectsConductor = true });

        Assert.Same(previous, editReturn);
        Assert.Equal(1, session.SourceRevision);
        Assert.False(session.IsCompilationCurrent);
        Assert.Contains(
            session.CompilationState,
            new[] { ProjectCompilationState.Outdated, ProjectCompilationState.Compiling });

        CanonicalCompiledResult current = await session.EnsureCurrentCompilationAsync();
        CanonicalCompiledResult full = new MidoraCompiler().CompileFull(project);

        Assert.True(current.IsConsumable);
        Assert.True(session.IsCompilationCurrent);
        Assert.Equal(session.SourceRevision, session.CompiledRevision);
        Assert.Equal(full.Fingerprint, current.Fingerprint);
        Assert.Equal(full.Conductor.Markers.ToArray(), current.Conductor.Markers.ToArray());
        Assert.Equal(full.Events.ToArray(), current.Events.ToArray());
    }

    [Fact]
    public async Task ConsecutiveEditsConvergeToTheLatestRevision()
    {
        MidoraProject project = new(480);
        project.Conductor.EndMarker = new ProjectEndMarker(project, 960);
        using ProjectCompilationSession session = new(
            project,
            executionMode: ProjectCompilationExecutionMode.Background,
            backgroundDebounce: TimeSpan.FromMilliseconds(25));

        _ = session.ApplyEdit(
            value => value.Conductor.Markers.Add(new ProjectMarker(value, 120, "First")),
            new ProjectChangeSet { AffectsConductor = true });
        _ = session.ApplyEdit(
            value => value.Conductor.Markers.Add(new ProjectMarker(value, 360, "Second")),
            new ProjectChangeSet { AffectsConductor = true });

        CanonicalCompiledResult current = await session.EnsureCurrentCompilationAsync();

        Assert.Equal(2, session.SourceRevision);
        Assert.Equal(2, session.CompiledRevision);
        Assert.True(session.IsCompilationCurrent);
        CanonicalMarker[] markers = current.Conductor.Markers.ToArray();
        Assert.Equal(2, markers.Length);
        Assert.Contains(markers, value => value.Name == "First");
        Assert.Contains(markers, value => value.Name == "Second");
    }

    [Fact]
    public async Task WaitingConsumerCanCancelWithoutCancelingSharedCompilation()
    {
        MidoraProject project = new(480);
        using ProjectCompilationSession session = new(
            project,
            executionMode: ProjectCompilationExecutionMode.Background,
            backgroundDebounce: TimeSpan.FromMilliseconds(30));
        _ = session.ApplyEdit(
            value => value.Conductor.Markers.Add(new ProjectMarker(value, 120, "Marker")),
            new ProjectChangeSet { AffectsConductor = true });
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.EnsureCurrentCompilationAsync(cancellation.Token));

        CanonicalCompiledResult current = await session.EnsureCurrentCompilationAsync();
        Assert.True(current.IsConsumable);
        Assert.True(session.IsCompilationCurrent);
    }

    [Fact]
    public async Task EditArrivingAfterWorkerStartsSupersedesTheOlderRevision()
    {
        (MidoraProject project, LogicalTrack track, LogicalNote first, LogicalNote second) =
            CreateNoteProject(2_000);
        using ProjectCompilationSession session = new(
            project,
            executionMode: ProjectCompilationExecutionMode.Background,
            backgroundDebounce: TimeSpan.Zero);
        TaskCompletionSource<bool> compiling = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.CompilationChanged += (_, _) =>
        {
            if (session.CompilationState == ProjectCompilationState.Compiling)
            {
                compiling.TrySetResult(true);
            }
        };
        ProjectChangeSet changes = new();
        changes.TrackIds.Add(track.Id);

        _ = session.ApplyEdit(_ => first.Note = 61, changes);
        await compiling.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _ = session.ApplyEdit(_ => second.Note = 62, changes);

        CanonicalCompiledResult current = await session.EnsureCurrentCompilationAsync();
        CanonicalCompiledResult full = new MidoraCompiler().CompileFull(project);

        Assert.Equal(2, session.SourceRevision);
        Assert.Equal(session.SourceRevision, session.CompiledRevision);
        Assert.Equal(full.Fingerprint, current.Fingerprint);
        Assert.Equal(full.Events.ToArray(), current.Events.ToArray());
    }

    [Fact]
    public async Task BackgroundCompilationNeverExecutesLegacyFreeCSharpAndCanRecover()
    {
        string markerPath = Path.Combine(
            Path.GetTempPath(),
            $"midora-background-compile-{Guid.NewGuid():N}.marker");
        try
        {
            (MidoraProject project, LogicalTrack _, LogicalNote _, LogicalNote _) =
                CreateNoteProject(2);
            EventInstrument instrument = Assert.Single(project.EventInstruments);
            TemplateEvent templateEvent = Assert.Single(Assert.Single(instrument.SubVoices).Events);
            CSharpMappingFunction function = new(project)
            {
                Name = "blocking-test",
                AbiVersion = MappingAbiV2.Version,
                Body = $"System.IO.File.WriteAllText(@\"{markerPath.Replace("\"", "\"\"")}\", \"started\")"
            };
            instrument.MappingFunctions.Add(function);
            templateEvent.ValueMappings.Add(new ValueMappingStep(project)
            {
                Operation = MappingOperation.CustomCSharp,
                MappingFunctionId = function.Id
            });
            using ProjectCompilationSession session = new(
                project,
                executionMode: ProjectCompilationExecutionMode.Background,
                backgroundDebounce: TimeSpan.Zero);
            ProjectChangeSet changes = new();
            changes.EventInstrumentIds.Add(instrument.Id);
            CanonicalCompiledResult rejected = await session.EnsureCurrentCompilationAsync();
            Assert.False(rejected.IsConsumable);
            Assert.False(File.Exists(markerPath));
            Assert.Contains(rejected.Diagnostics, diagnostic =>
                diagnostic.Message.Contains("Free C# Mapping Functions are not executed", StringComparison.Ordinal));

            _ = session.ApplyEdit(_ =>
            {
                function.AbiVersion = MappingExpressionAbiV3.Version;
                function.Body = "value";
            }, changes);
            CanonicalCompiledResult current = await session.EnsureCurrentCompilationAsync();
            CanonicalCompiledResult full = new MidoraCompiler().CompileFull(project);
            Assert.True(current.IsConsumable);
            Assert.Equal(full.Fingerprint, current.Fingerprint);
            Assert.Equal(full.Events.ToArray(), current.Events.ToArray());
        }
        finally
        {
            if (File.Exists(markerPath))
            {
                File.Delete(markerPath);
            }
        }
    }

    private static (MidoraProject Project, LogicalTrack Track, LogicalNote First, LogicalNote Second)
        CreateNoteProject(int noteCount)
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            TemplateLengthTicks = 120,
            RequiresChannelIsolation = true,
            OverlapPolicy = OverlapPolicy.LetOverlap
        };
        SubVoice voice = new(project);
        voice.Events.Add(TemplateEvent.Note(project, 0, 120, 60, 100));
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project) {
            Name = "Track",
        };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project)
        {
            LengthTicks = noteCount * 120L
        };
        for (int index = 0; index < noteCount; index++)
        {
            segment.Notes.Add(new LogicalNote(project)
            {
                StartTick = index * 120L,
                LengthTicks = 120,
                Note = 60,
                Velocity = 100
            });
        }
        track.Segments.Add(segment);
        return (project, track, segment.Notes[0], segment.Notes[1]);
    }
}
