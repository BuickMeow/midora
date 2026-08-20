using Midora.Compiler;
using Midora.Domain;

namespace Midora.AudioRender.Tests;

public sealed class AudioRenderCompilationCoordinatorTests
{
    [Fact]
    public void WholeMixUsesOneDedicatedCanonicalContextAndIgnoresMuteSoloState()
    {
        MidoraProject project = AudioRenderTestProject.Create(
            ("Piano", 192, 60),
            ("Strings", 384, 64));
        LogicalTrack unbound = new(project) { Name = "Unbound" };
        ProjectGraphConstruction.AddUnboundLogicalTrack(project, unbound);
        using MidoraCompiler compiler = new();

        AudioRenderCompilationResult result = new AudioRenderCompilationCoordinator(compiler).Compile(new()
        {
            Project = project,
            Mode = AudioRenderMode.WholeMix,
            StartTick = 24,
            EndTick = 480,
            SelectedTrackIds = new HashSet<MidoraId>
            {
                project.Tracks[0].Id,
                project.Tracks[1].Id,
                unbound.Id
            }
        });

        AudioRenderCompilationItem item = Assert.Single(result.Items);
        Assert.True(item.Succeeded);
        Assert.Equal(CompilationPurpose.AudioRender, item.CompiledResult.Purpose);
        Assert.Equal(24, item.CompiledResult.StartTick);
        Assert.Equal(480, item.CompiledResult.EndTick);
        Assert.Equal(480, result.EndTick);
        Assert.Contains(result.Diagnostics, value =>
            value.Code == "MIDORA-AUDIO-RENDER-TRACK-UNBOUND"
            && value.Severity == AudioRenderDiagnosticSeverity.Info);
    }

    [Fact]
    public void PerTrackCompilesIndependentlyAndFreezesOneNaturalRange()
    {
        MidoraProject project = AudioRenderTestProject.Create(
            ("Short", 192, 60),
            ("Long", 768, 64));
        using MidoraCompiler compiler = new();

        AudioRenderCompilationResult result = new AudioRenderCompilationCoordinator(compiler).Compile(new()
        {
            Project = project,
            Mode = AudioRenderMode.PerLogicalTrack
        });

        Assert.True(result.HasRenderableOutput);
        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, item =>
        {
            Assert.True(item.Succeeded);
            Assert.Equal(CompilationPurpose.LogicalTrackAudioRender, item.CompiledResult.Purpose);
            Assert.Equal(result.EndTick, item.CompiledResult.EndTick);
            Assert.All(item.CompiledResult.Allocations.ToArray(), allocation =>
            {
                Assert.Equal(0, allocation.ZeroBasedPort);
                Assert.Equal(0, allocation.ZeroBasedChannel);
            });
        });
        Assert.True(result.EndTick >= 768);
    }

    [Fact]
    public void WholeMixRendersPureMidiOnlyProjectAndUsesItsNaturalRange()
    {
        MidoraProject project = new(192);
        MidiChannelRoot root = new(project)
        {
            Name = "Piano",
            RoutingMode = MidiChannelRootRoutingMode.Auto,
            ChannelMode = MidiChannelMode.Melodic
        };
        PureMidiTrack track = new(project)
        {
            Name = "Imported Track",
            MidiChannelRootId = root.Id
        };
        MidiSegment segment = new(project)
        {
            ProjectStartTick = 384,
            LengthTicks = 768
        };
        segment.Notes.Add(new(project)
        {
            StartTick = 0,
            LengthTicks = 384,
            Key = 60,
            NoteOnVelocity = 100
        });
        track.Segments.Add(segment);
        project.MidiChannelRoots.Add(root);
        project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        using MidoraCompiler compiler = new();

        AudioRenderCompilationResult result = new AudioRenderCompilationCoordinator(compiler).Compile(new()
        {
            Project = project,
            Mode = AudioRenderMode.WholeMix
        });

        AudioRenderCompilationItem item = Assert.Single(result.Items);
        Assert.True(item.Succeeded, string.Join(Environment.NewLine, item.Diagnostics));
        Assert.True(result.HasRenderableOutput);
        Assert.Equal(1152, result.EndTick);
        Assert.Contains(item.CompiledResult.Events.ToArray(), value =>
            value.Role == CanonicalEventRole.DirectMidi
            && value.Source.PureMidiTrackId == track.Id);
    }

    [Fact]
    public void WholeMixExplicitSelectionCanSelectOnlyPureMidiTrack()
    {
        MidoraProject project = AudioRenderTestProject.Create(("Logical", 192, 60));
        EventInstrument instrument = Assert.Single(project.EventInstruments);
        MidiChannelRoot root = new(project)
        {
            Name = "Root",
            RoutingMode = MidiChannelRootRoutingMode.Auto,
            ChannelMode = MidiChannelMode.Melodic
        };
        PureMidiTrack track = new(project) { Name = "MIDI", MidiChannelRootId = root.Id };
        MidiSegment segment = new(project) { LengthTicks = 384 };
        segment.Notes.Add(new(project)
        {
            LengthTicks = 192,
            Key = 72,
            NoteOnVelocity = 90
        });
        track.Segments.Add(segment);
        project.MidiChannelRoots.Add(root);
        project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        using MidoraCompiler compiler = new();

        AudioRenderCompilationResult result = new AudioRenderCompilationCoordinator(compiler).Compile(new()
        {
            Project = project,
            Mode = AudioRenderMode.WholeMix,
            SelectedTrackIds = new HashSet<MidoraId> { track.Id }
        });

        AudioRenderCompilationItem item = Assert.Single(result.Items);
        Assert.True(item.Succeeded, string.Join(Environment.NewLine, item.Diagnostics));
        Assert.Contains(item.CompiledResult.Events.ToArray(), value =>
            value.Role == CanonicalEventRole.DirectMidi
            && value.Source.PureMidiTrackId == track.Id);
        Assert.DoesNotContain(item.CompiledResult.Events.ToArray(), value =>
            value.Role == CanonicalEventRole.NoteOn
            && value.Source.TrackId == project.Tracks[0].Id);
    }

    [Fact]
    public void BoundEmptyInstrumentRemainsSilentTargetWithCommonRange()
    {
        MidoraProject project = AudioRenderTestProject.Create(("Audible", 192, 60));
        EventInstrument emptyInstrument = new(project)
        {
            Name = "Empty",
            RootNote = 60,
            TemplateLengthTicks = 192
        };
        SubVoice emptyVoice = new(project) { Name = "Empty Voice" };
        emptyInstrument.SubVoices.Add(emptyVoice);
        project.EventInstruments.Add(emptyInstrument);
        LogicalTrack silentTrack = new(project) {
            Name = "Silent",
        };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, silentTrack, emptyInstrument.Id);
        Segment silentSegment = new(project) { LengthTicks = 384 };
        silentSegment.Notes.Add(new(project)
        {
            LengthTicks = 384,
            Note = 64,
            Velocity = 100
        });
        silentTrack.Segments.Add(silentSegment);
        using MidoraCompiler compiler = new();

        AudioRenderCompilationResult result = new AudioRenderCompilationCoordinator(compiler).Compile(new()
        {
            Project = project,
            Mode = AudioRenderMode.PerLogicalTrack
        });

        Assert.True(result.HasRenderableOutput);
        Assert.Equal(2, result.Items.Count);
        AudioRenderCompilationItem silent = Assert.Single(result.Items, value =>
            value.Track?.TrackId == silentTrack.Id);
        Assert.True(
            silent.Succeeded,
            string.Join(" | ", silent.Diagnostics.Select(value =>
                $"{value.Code}:{value.Severity}:{value.Message}")));
        Assert.Equal(result.EndTick, silent.CompiledResult.EndTick);
        Assert.DoesNotContain(silent.CompiledResult.Events.ToArray(), value =>
            value.Role is CanonicalEventRole.NoteOn or CanonicalEventRole.NoteOff);
        Assert.Single(silent.CompiledResult.Allocations.ToArray());
        Assert.Contains(silent.Diagnostics, value =>
            value.Code == "MIDORA1225"
            && value.Severity == DiagnosticSeverity.Info
            && value.Source.SubVoiceId == emptyVoice.Id);
    }

    [Fact]
    public void NoBoundSelectedTrackBlocksBeforeRendering()
    {
        MidoraProject project = new(192);
        LogicalTrack track = new(project) { Name = "Unbound" };
        project.Tracks.Add(track);
        using MidoraCompiler compiler = new();

        AudioRenderCompilationResult result = new AudioRenderCompilationCoordinator(compiler).Compile(new()
        {
            Project = project,
            Mode = AudioRenderMode.PerLogicalTrack,
            SelectedTrackIds = new HashSet<MidoraId> { track.Id }
        });

        Assert.False(result.HasRenderableOutput);
        Assert.Empty(result.Items);
        Assert.Contains(result.Diagnostics, value => value.Code == "MIDORA-AUDIO-RENDER-NO-TARGETS");
    }

    [Fact]
    public void WholeMixPreservesDamagedInstrumentBindingAsCanonicalError()
    {
        MidoraProject project = AudioRenderTestProject.Create(("Healthy", 192, 60));
        MidoraId damagedInstrumentId = project.AllocateStableId();
        project.DamagedEventInstruments.Add(new(
            damagedInstrumentId,
            "Damaged",
            "event-instruments/damaged.pb",
            "Invalid protobuf",
            1));
        LogicalTrack damagedTrack = CreateDamagedTrack(project, damagedInstrumentId);
        project.Tracks.Add(damagedTrack);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, damagedTrack.Id));
        using MidoraCompiler compiler = new();

        AudioRenderCompilationResult result = new AudioRenderCompilationCoordinator(compiler).Compile(new()
        {
            Project = project,
            Mode = AudioRenderMode.WholeMix,
            SelectedTrackIds = new HashSet<MidoraId>
            {
                project.Tracks[0].Id,
                damagedTrack.Id
            }
        });

        AudioRenderCompilationItem item = Assert.Single(result.Items);
        Assert.False(item.Succeeded);
        Assert.False(result.HasRenderableOutput);
        Assert.Contains(result.Tracks, value => value.TrackId == damagedTrack.Id && value.Participates);
        Assert.Contains(item.Diagnostics, value =>
            value.Code == "MIDORA1305"
            && value.Severity == DiagnosticSeverity.Error
            && value.Source.TrackId == damagedTrack.Id
            && value.Source.EventInstrumentId == damagedInstrumentId);
        Assert.DoesNotContain(result.Diagnostics, value =>
            value.Code == "MIDORA-AUDIO-RENDER-TRACK-UNBOUND"
            && value.TrackId == damagedTrack.Id);
    }

    [Fact]
    public void PerTrackRecordsDamagedInstrumentBindingFailureAndKeepsHealthyOutput()
    {
        MidoraProject project = AudioRenderTestProject.Create(("Healthy", 192, 60));
        MidoraId damagedInstrumentId = project.AllocateStableId();
        project.DamagedEventInstruments.Add(new(
            damagedInstrumentId,
            "Damaged",
            "event-instruments/damaged.pb",
            "Invalid protobuf",
            1));
        LogicalTrack damagedTrack = CreateDamagedTrack(project, damagedInstrumentId);
        project.Tracks.Add(damagedTrack);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, damagedTrack.Id));
        using MidoraCompiler compiler = new();

        AudioRenderCompilationResult result = new AudioRenderCompilationCoordinator(compiler).Compile(new()
        {
            Project = project,
            Mode = AudioRenderMode.PerLogicalTrack,
            SelectedTrackIds = new HashSet<MidoraId>
            {
                project.Tracks[0].Id,
                damagedTrack.Id
            }
        });

        Assert.True(result.HasRenderableOutput);
        Assert.Equal(2, result.Items.Count);
        Assert.True(Assert.Single(result.Items, value =>
            value.Track?.TrackId == project.Tracks[0].Id).Succeeded);
        AudioRenderCompilationItem damaged = Assert.Single(result.Items, value =>
            value.Track?.TrackId == damagedTrack.Id);
        Assert.False(damaged.Succeeded);
        Assert.Contains(damaged.Diagnostics, value =>
            value.Code == "MIDORA1305"
            && value.Severity == DiagnosticSeverity.Error
            && value.Source.TrackId == damagedTrack.Id);
    }

    [Fact]
    public void RejectsZeroLengthManualRange()
    {
        MidoraProject project = AudioRenderTestProject.Create(("Track", 192, 60));
        using MidoraCompiler compiler = new();
        AudioRenderCompilationCoordinator coordinator = new(compiler);

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => coordinator.Compile(new()
        {
            Project = project,
            Mode = AudioRenderMode.WholeMix,
            StartTick = 100,
            EndTick = 100
        }));
    }

    private static LogicalTrack CreateDamagedTrack(
        MidoraProject project,
        MidoraId damagedInstrumentId)
    {
        EventInstrumentUsage usage = new(project)
        {
            EventInstrumentId = damagedInstrumentId
        };
        project.EventInstrumentUsages.Add(usage);
        LogicalTrack track = new(project)
        {
            Name = "Damaged Track",
            EventInstrumentUsageId = usage.Id
        };
        Segment segment = new(project) { LengthTicks = 192 };
        segment.Notes.Add(new(project)
        {
            LengthTicks = 96,
            Note = 64,
            Velocity = 100
        });
        track.Segments.Add(segment);
        return track;
    }
}
