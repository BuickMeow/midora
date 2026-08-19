using Midora.Audio;
using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;

namespace Midora.Playback.Tests;

public sealed class CanonicalAudioUnitProjectionTests
{
    [Fact]
    public void SharedMidiRootProducesOneChannelStateFragmentAcrossChildTracks()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = new(project)
        {
            Name = "Shared",
            RoutingMode = MidiChannelRootRoutingMode.Fixed,
            FixedZeroBasedPort = 2,
            FixedZeroBasedChannel = 9,
            ChannelMode = MidiChannelMode.Percussion
        };
        project.MidiChannelRoots.Add(root);
        project.ArrangementParents.Add(new(
            ArrangementParentKind.MidiChannelRoot,
            root.Id));
        PureMidiTrack first = AddMidiTrack(project, root, "First", startTick: 0, key: 36);
        PureMidiTrack second = AddMidiTrack(project, root, "Second", startTick: 240, key: 38);

        using MidoraCompiler compiler = new();
        CanonicalCompiledResult compiled = compiler.CompileFull(project);

        Assert.True(compiled.IsConsumable, string.Join(Environment.NewLine, compiled.Diagnostics));
        CanonicalAudioUnitFragment fragment = Assert.Single(
            CanonicalAudioUnitProjection.Create(compiled).Fragments.ToArray());
        Assert.Equal(root.Id, fragment.MidiChannelRootId);
        Assert.Equal(MidiChannelMode.Percussion, fragment.ChannelMode);
        Assert.Equal(0, fragment.GroupStartTick);
        Assert.Equal(720, fragment.GroupEndTick);
        Assert.Equal(first.Id, fragment.TrackId);
        Assert.Contains(fragment.Events.ToArray(), value =>
            value.Message.MessageType == MidiMessageType.NoteOn && value.Message.Byte1 == 36);
        Assert.Contains(fragment.Events.ToArray(), value =>
            value.Message.MessageType == MidiMessageType.NoteOn && value.Message.Byte1 == 38);
        Assert.All(fragment.Events.ToArray(), value => Assert.Equal(0, value.Message.ChannelNumber));
    }

    [Fact]
    public void RootLifecycleMonitoringEventsRemainEnabledWhenTheirOwnerTrackIsMuted()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = new(project)
        {
            Name = "Shared",
            RoutingMode = MidiChannelRootRoutingMode.Fixed,
            FixedZeroBasedPort = 0,
            FixedZeroBasedChannel = 0,
            ChannelMode = MidiChannelMode.Melodic
        };
        project.MidiChannelRoots.Add(root);
        project.ArrangementParents.Add(new(
            ArrangementParentKind.MidiChannelRoot,
            root.Id));
        PureMidiTrack owner = AddMidiTrack(project, root, "Owner", startTick: 0, key: 60);
        PureMidiTrack audible = AddMidiTrack(project, root, "Audible", startTick: 0, key: 64);
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult compiled = compiler.CompileFull(project);

        MidiRenderPlan plan = MidiRenderPlanAdapter.CreateRealtime(
            compiled,
            48_000,
            new HashSet<MidoraId> { audible.Id });

        int rootSourceIndex = plan.FindSourceIndex(root.Id.Value);
        int ownerSourceIndex = plan.FindSourceIndex(owner.Id.Value);
        Assert.True(rootSourceIndex >= 0);
        Assert.True(ownerSourceIndex >= 0);
        Assert.Contains(ownerSourceIndex, plan.InitiallyDisabledSourceIndices.ToArray());
        Assert.DoesNotContain(rootSourceIndex, plan.InitiallyDisabledSourceIndices.ToArray());
        Assert.Contains(plan.Ports.ToArray().SelectMany(value => value.Events.ToArray()), value =>
            value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 121
            && value.SourceIndex == rootSourceIndex);
        Assert.Contains(plan.UnitFragments.ToArray().SelectMany(value => value.Events.ToArray()), value =>
            value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 121
            && value.SourceIndex == rootSourceIndex);
    }

    [Fact]
    public void RealtimeSessionKeepsPureMidiRootMixSourceEnabled()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = new(project)
        {
            Name = "Shared",
            RoutingMode = MidiChannelRootRoutingMode.Auto,
            ChannelMode = MidiChannelMode.Melodic
        };
        project.MidiChannelRoots.Add(root);
        project.ArrangementParents.Add(new(
            ArrangementParentKind.MidiChannelRoot,
            root.Id));
        PureMidiTrack track = AddMidiTrack(project, root, "Track", startTick: 0, key: 60);
        using ProjectCompilationSession session = new(project);
        CanonicalCompiledResult compiled = session.CompileForPlayback(0, null);

        MidiRenderPlan audible = session.GetOrCreateRealtimeRenderPlan(
            compiled,
            48_000,
            new HashSet<MidoraId> { track.Id });
        MidiRenderPlan muted = session.GetOrCreateRealtimeRenderPlan(
            compiled,
            48_000,
            new HashSet<MidoraId>());

        int rootSourceIndex = audible.FindSourceIndex(root.Id.Value);
        int trackSourceIndex = audible.FindSourceIndex(track.Id.Value);
        Assert.True(rootSourceIndex >= 0);
        Assert.True(trackSourceIndex >= 0);
        Assert.Equal(rootSourceIndex, Assert.Single(audible.UnitFragments.ToArray()).SourceIndex);
        Assert.DoesNotContain(rootSourceIndex, audible.InitiallyDisabledSourceIndices.ToArray());
        Assert.DoesNotContain(trackSourceIndex, audible.InitiallyDisabledSourceIndices.ToArray());
        Assert.DoesNotContain(rootSourceIndex, muted.InitiallyDisabledSourceIndices.ToArray());
        Assert.Contains(trackSourceIndex, muted.InitiallyDisabledSourceIndices.ToArray());
    }

    [Fact]
    public void PureMidiEffectsControllersRemainCanonicalButAreIgnoredByNoFxAudioProjection()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = new(project)
        {
            Name = "No FX",
            RoutingMode = MidiChannelRootRoutingMode.Fixed,
            FixedZeroBasedPort = 0,
            FixedZeroBasedChannel = 0,
            ChannelMode = MidiChannelMode.Melodic
        };
        project.MidiChannelRoots.Add(root);
        project.ArrangementParents.Add(new(
            ArrangementParentKind.MidiChannelRoot,
            root.Id));
        PureMidiTrack track = AddMidiTrack(project, root, "Track", startTick: 0, key: 60);
        track.Segments[0].ChannelEvents.Add(new DirectMidiChannelEvent(project)
        {
            Tick = 12,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 91,
            Data2 = 96,
            Order = 5
        });

        using MidoraCompiler compiler = new();
        CanonicalCompiledResult compiled = compiler.CompileFull(project);
        MidiRenderPlan plan = MidiRenderPlanAdapter.CreateRealtime(compiled, 48_000);

        Assert.True(compiled.IsConsumable, string.Join(Environment.NewLine, compiled.Diagnostics));
        Assert.Contains(compiled.Events.ToArray(), value =>
            value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 91
            && value.Message.Byte2 == 96);
        Assert.DoesNotContain(
            plan.Ports.ToArray().SelectMany(value => value.Events.ToArray()),
            value => value.Message.MessageType == MidiMessageType.ControlChange
                && value.Message.Byte1 == 91);
        Assert.DoesNotContain(
            plan.UnitFragments.ToArray().SelectMany(value => value.Events.ToArray()),
            value => value.Message.MessageType == MidiMessageType.ControlChange
                && value.Message.Byte1 == 91);
    }

    [Fact]
    public void ProjectsCanonicalAllocationsToRouteIndependentChannelZeroFragments()
    {
        MidoraProject project = new(480);
        (EventInstrument instrument, LogicalTrack track, Segment segment) = AddVoice(
            project,
            pitch: 60);

        using MidoraCompiler compiler = new();
        CanonicalCompiledResult before = compiler.CompileFull(project);
        CanonicalAudioUnitFragment original = Assert.Single(
            CanonicalAudioUnitProjection.Create(before).Fragments.ToArray());
        ChannelUnitAllocation originalAllocation = before.Allocations[0];
        AudioSynthesisCacheEnvironment environment = new(
            48_000,
            new string('a', 64),
            "bass-2.4.18.3+bassmidi-2.4.16.0",
            500);
        string originalCacheKey = AudioUnitPcmCacheKey.Create(original, before, environment);

        (EventInstrument blockerInstrument, LogicalTrack blockerTrack, _) = AddVoice(
            project,
            pitch: 72);
        project.EventInstruments.Remove(blockerInstrument);
        project.EventInstruments.Insert(0, blockerInstrument);
        project.Tracks.Remove(blockerTrack);
        project.Tracks.Insert(0, blockerTrack);

        CanonicalCompiledResult after = compiler.CompileFull(project);
        CanonicalAudioUnitFragment moved = CanonicalAudioUnitProjection.Create(after).Fragments
            .ToArray()
            .Single(value => value.SegmentId == segment.Id);
        ChannelUnitAllocation movedAllocation = after.Allocations.ToArray()
            .Single(value => value.SegmentId == segment.Id);

        Assert.Equal(instrument.Id, moved.EventInstrumentId);
        Assert.Equal(track.Id, moved.TrackId);
        Assert.Equal(original.InstanceGroupId, moved.InstanceGroupId);
        Assert.Equal(original.SubVoiceId, moved.SubVoiceId);
        Assert.Equal(original.SemanticFingerprint, moved.SemanticFingerprint);
        Assert.Equal(originalCacheKey, AudioUnitPcmCacheKey.Create(moved, after, environment));
        Assert.NotEqual(
            (originalAllocation.ZeroBasedPort, originalAllocation.ZeroBasedChannel),
            (movedAllocation.ZeroBasedPort, movedAllocation.ZeroBasedChannel));
        Assert.All(
            moved.Events.ToArray(),
            value => Assert.Equal(0, value.Message.ChannelNumber));
    }

    [Fact]
    public void UnitPcmKeyChangesForEverySoundAffectingEnvironmentDimension()
    {
        MidoraProject project = new(480);
        _ = AddVoice(project, pitch: 60);
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult compiled = compiler.CompileFull(project);
        CanonicalAudioUnitFragment fragment = Assert.Single(
            CanonicalAudioUnitProjection.Create(compiled).Fragments.ToArray());
        AudioSynthesisCacheEnvironment baseline = new(
            48_000,
            new string('a', 64),
            "native-baseline-a",
            500);

        string key = AudioUnitPcmCacheKey.Create(fragment, compiled, baseline);

        Assert.NotEqual(key, AudioUnitPcmCacheKey.Create(fragment, compiled, baseline with { SampleRate = 44_100 }));
        Assert.NotEqual(key, AudioUnitPcmCacheKey.Create(
            fragment,
            compiled,
            baseline with { SoundFontSha256 = new string('b', 64) }));
        Assert.NotEqual(key, AudioUnitPcmCacheKey.Create(
            fragment,
            compiled,
            baseline with { NativeBaselineIdentity = "native-baseline-b" }));
        Assert.NotEqual(key, AudioUnitPcmCacheKey.Create(
            fragment,
            compiled,
            baseline with { MaximumSampleVoicesPerUnitStream = 501 }));
    }

    private static (EventInstrument Instrument, LogicalTrack Track, Segment Segment) AddVoice(
        MidoraProject project,
        byte pitch)
    {
        EventInstrument instrument = new(project)
        {
            Name = $"Instrument {pitch}",
            TemplateLengthTicks = 480,
            OverlapPolicy = OverlapPolicy.Warn
        };
        SubVoice voice = new(project);
        voice.Events.Add(TemplateEvent.Note(project, 0, 480, pitch, 100));
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project)
        {
            Name = $"Track {pitch}",
            EventInstrumentId = instrument.Id
        };
        Segment segment = new(project) { LengthTicks = 480 };
        segment.Notes.Add(new LogicalNote(project)
        {
            LengthTicks = 480,
            Note = pitch,
            Velocity = 100
        });
        track.Segments.Add(segment);
        project.Tracks.Add(track);
        return (instrument, track, segment);
    }

    private static PureMidiTrack AddMidiTrack(
        MidoraProject project,
        MidiChannelRoot root,
        string name,
        long startTick,
        int key)
    {
        PureMidiTrack track = new(project)
        {
            Name = name,
            MidiChannelRootId = root.Id
        };
        MidiSegment segment = new(project)
        {
            ProjectStartTick = startTick,
            LengthTicks = 480
        };
        segment.Notes.Add(new DirectMidiNote(project)
        {
            StartTick = 0,
            LengthTicks = 240,
            Key = key,
            NoteOnVelocity = 100,
            NoteOffVelocity = 32,
            NoteOnOrder = 0,
            NoteOffOrder = 1
        });
        track.Segments.Add(segment);
        project.PureMidiTracks.Add(track);
        root.MidiTrackIds.Add(track.Id);
        return track;
    }
}
