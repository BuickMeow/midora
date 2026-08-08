using Midora.Compiler;
using Midora.Domain;

namespace Midora.Playback.Tests;

public sealed class CanonicalAudioUnitProjectionTests
{
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
}
