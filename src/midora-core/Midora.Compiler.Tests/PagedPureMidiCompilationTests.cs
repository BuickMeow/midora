using Midora.Domain;

namespace Midora.Compiler.Tests;

public sealed class PagedPureMidiCompilationTests
{
    [Fact]
    public void FullCompileKeepsPagedDirectMidiDeferredAndRangeQueryable()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "midora-paged-compiler-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "track.mpk");
        try
        {
            MidoraProject project = new(192);
            MidiChannelRoot root = new(project)
            {
                Name = "Root",
                RoutingMode = MidiChannelRootRoutingMode.Fixed,
                FixedZeroBasedPort = 0,
                FixedZeroBasedChannel = 0,
                ChannelMode = MidiChannelMode.Melodic
            };
            PureMidiTrack track = new(project)
            {
                Name = "Track",
                MidiChannelRootId = root.Id
            };
            MidiSegment segment = new(project)
            {
                ProjectStartTick = 0,
                LengthTicks = 384,
                ContentOffsetTick = 0
            };
            track.Segments.Add(segment);
            project.MidiChannelRoots.Add(root);
            project.PureMidiTracks.Add(track);
            project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
            using PureMidiContentPackWriter writer = new(path);
            writer.AddNote(segment.Id, new(
                project.AllocateStableId(), 0, 96, 60, 100, 17, 1, 2));
            writer.AddNote(segment.Id, new(
                project.AllocateStableId(), 192, 96, 64, 110, 0, 3, 4));
            using PureMidiContentPack pack = writer.Complete();
            segment.AttachPagedContent(pack.GetSegmentSource(segment.Id));

            using MidoraCompiler compiler = new();
            CanonicalCompiledResult result = compiler.CompileFull(project);

            Assert.True(result.IsConsumable);
            Assert.True(result.HasPagedEvents);
            Assert.Equal(0, result.Events.Length);
            Assert.Equal(2, result.Statistics.NoteOnEventCount);
            Assert.Equal(result.Statistics.EventCount, result.TotalEventCount);
            CanonicalMidiEvent[] firstHalf = result.QueryEventPages(0, 192)
                .SelectMany(value => value.Items)
                .ToArray();
            Assert.Contains(firstHalf, value =>
                value.Message.MessageType == Midora.Midi.MidiMessageType.NoteOn
                && value.Message.Byte1 == 60);
            Assert.Contains(firstHalf, value =>
                value.Message.MessageType == Midora.Midi.MidiMessageType.NoteOff
                && value.Message.Byte1 == 60
                && value.Message.Byte2 == 17);
            Assert.DoesNotContain(firstHalf, value =>
                value.Message.MessageType == Midora.Midi.MidiMessageType.NoteOn
                && value.Message.Byte1 == 64);
            CanonicalMidiRenderEvent[] renderEvents = result
                .QueryMidiRenderEventPages(0, 192)
                .SelectMany(value => value.Items)
                .ToArray();
            Assert.Equal(firstHalf.Length, renderEvents.Length);
            for (int index = 0; index < firstHalf.Length; index++)
            {
                CanonicalMidiEvent canonical = firstHalf[index];
                CanonicalMidiRenderEvent render = renderEvents[index];
                Assert.Equal(canonical.Tick, render.Tick);
                Assert.Equal(canonical.ZeroBasedPort, render.ZeroBasedPort);
                Assert.Equal(canonical.Message, render.Message);
                Assert.Equal(canonical.Source.TrackId, render.TrackId);
                MidoraId expectedMonitoringSource =
                    canonical.Source.Origin == SourceOrigin.MidiChannelRootLifecycle
                        ? canonical.Source.MidiChannelRootId
                        : canonical.Source.TrackId;
                Assert.Equal(expectedMonitoringSource, render.MonitoringSourceId);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RangeStartChannelStateRestoresFromADeepEndpointCheckpoint()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "midora-paged-compiler-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "state-checkpoint.mpk");
        try
        {
            MidoraProject project = new(192);
            MidiChannelRoot root = new(project)
            {
                Name = "Root",
                RoutingMode = MidiChannelRootRoutingMode.Fixed,
                FixedZeroBasedPort = 0,
                FixedZeroBasedChannel = 0,
                ChannelMode = MidiChannelMode.Melodic
            };
            PureMidiTrack track = new(project)
            {
                Name = "Track",
                MidiChannelRootId = root.Id
            };
            MidiSegment segment = new(project)
            {
                ProjectStartTick = 0,
                LengthTicks = 21_000,
                ContentOffsetTick = 0
            };
            track.Segments.Add(segment);
            project.MidiChannelRoots.Add(root);
            project.PureMidiTracks.Add(track);
            project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
            using (PureMidiContentPackWriter writer = new(path))
            {
                for (int tick = 0; tick < 20_000; tick++)
                {
                    writer.AddChannelEvent(segment.Id, new(
                        project.AllocateStableId(),
                        tick,
                        DirectMidiChannelEventKind.ControlChange,
                        11,
                        tick % 128,
                        tick));
                }
                using PureMidiContentPack pack = writer.Complete();
                segment.AttachPagedContent(pack.GetSegmentSource(segment.Id));

                using MidoraCompiler compiler = new();
                CanonicalCompiledResult result = compiler.CompileFull(project);
                CanonicalMidiEvent restored = Assert.Single(
                    result.QueryEventPages(
                            19_500,
                            19_501,
                            includeStateAtStart: true)
                        .SelectMany(page => page.Items),
                    value =>
                        value.Role == CanonicalEventRole.RangeRestore
                        && value.Message.MessageType == Midora.Midi.MidiMessageType.ControlChange
                        && value.Message.Byte1 == 11
                        && value.Source.DirectMidiObjectId != default);

                Assert.Equal(19_500, restored.Tick);
                Assert.Equal(19_499 % 128, restored.Message.Byte2);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RangeStartRestoresPagedStateFromEndedSiblingTrackWithinActiveRoot()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "midora-paged-compiler-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "cross-track-state.mpk");
        try
        {
            MidoraProject project = new(192);
            MidiChannelRoot root = new(project)
            {
                Name = "Root",
                RoutingMode = MidiChannelRootRoutingMode.Auto,
                ChannelMode = MidiChannelMode.Melodic
            };
            PureMidiTrack notes = new(project)
            {
                Name = "Notes",
                MidiChannelRootId = root.Id
            };
            MidiSegment noteSegment = new(project)
            {
                ProjectStartTick = 0,
                LengthTicks = 300
            };
            notes.Segments.Add(noteSegment);
            PureMidiTrack state = new(project)
            {
                Name = "State",
                MidiChannelRootId = root.Id
            };
            MidiSegment stateSegment = new(project)
            {
                ProjectStartTick = 0,
                LengthTicks = 100
            };
            state.Segments.Add(stateSegment);
            project.MidiChannelRoots.Add(root);
            project.PureMidiTracks.Add(notes);
            project.PureMidiTracks.Add(state);
            project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, notes.Id));
            project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, state.Id));

            using (PureMidiContentPackWriter writer = new(path))
            {
                writer.AddNote(noteSegment.Id, new(
                    project.AllocateStableId(), 200, 30, 60, 100, 0, 1, 2));
                writer.AddChannelEvent(stateSegment.Id, new(
                    project.AllocateStableId(),
                    20,
                    DirectMidiChannelEventKind.ControlChange,
                    11,
                    77,
                    1));
                using PureMidiContentPack pack = writer.Complete();
                noteSegment.AttachPagedContent(pack.GetSegmentSource(noteSegment.Id));
                stateSegment.AttachPagedContent(pack.GetSegmentSource(stateSegment.Id));

                using MidoraCompiler compiler = new();
                CanonicalCompiledResult result = compiler.CompileFull(
                    project,
                    new CompilationRequest
                    {
                        Purpose = CompilationPurpose.Playback,
                        StartTick = 150,
                        EndTick = 250
                    });

                Assert.True(result.IsConsumable, string.Join(Environment.NewLine, result.Diagnostics));
                CanonicalMidiEvent restored = Assert.Single(
                    result.QueryEventPages(150, 151, includeStateAtStart: true)
                        .SelectMany(page => page.Items),
                    value => value.Tick == 150
                        && value.Role == CanonicalEventRole.RangeRestore
                        && value.Message.MessageType == Midora.Midi.MidiMessageType.ControlChange
                        && value.Message.Byte1 == 11
                        && value.Source.DirectMidiObjectId != default);
                Assert.Equal((byte)77, restored.Message.Byte2);
                Assert.Equal(state.Id, restored.Source.TrackId);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
