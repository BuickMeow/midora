using Midora.Audio;
using Midora.Compiler;
using Midora.Domain;
using System.Security.Cryptography;
using System.Text;

namespace Midora.Playback;

public static class MidiRenderPlanAdapter
{
    public static MidiRenderPlan Create(
        CanonicalCompiledResult compiled,
        int sampleRate,
        IReadOnlySet<MidoraId>? audibleTrackIds = null)
        => CreateCore(compiled, sampleRate, audibleTrackIds,
            preserveFilteredTrackEvents: false, restrictToFileSampleRateRange: true);

    public static MidiRenderPlan CreateRealtime(
        CanonicalCompiledResult compiled,
        int sampleRate)
        => CreateCore(compiled, sampleRate, audibleTrackIds: null,
            preserveFilteredTrackEvents: false, restrictToFileSampleRateRange: false);

    public static MidiRenderPlan CreateRealtime(
        CanonicalCompiledResult compiled,
        int sampleRate,
        IReadOnlySet<MidoraId> audibleTrackIds)
    {
        ArgumentNullException.ThrowIfNull(audibleTrackIds);
        return CreateCore(compiled, sampleRate, audibleTrackIds,
            preserveFilteredTrackEvents: true, restrictToFileSampleRateRange: false);
    }

    private static MidiRenderPlan CreateCore(
        CanonicalCompiledResult compiled,
        int sampleRate,
        IReadOnlySet<MidoraId>? audibleTrackIds,
        bool preserveFilteredTrackEvents,
        bool restrictToFileSampleRateRange)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        if (!compiled.IsConsumable || compiled.IsPartial)
        {
            throw new ArgumentException("Only a consumable canonical result can produce an audio plan.", nameof(compiled));
        }
        if (sampleRate <= 0
            || restrictToFileSampleRateRange && sampleRate is < 8_000 or > 192_000)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        TempoSampleMap map = new(compiled.TicksPerQuarterNote, compiled.Tempos);
        long totalFrames = map.TickToSampleFrame(compiled.EndTick, compiled.StartTick, sampleRate);
        MidoraId[] sourceIds = compiled.Events.ToArray()
            .Select(value => value.Source.TrackId)
            .Concat(compiled.Allocations.ToArray().Select(value => value.TrackId))
            .Where(value => value != default)
            .Distinct()
            .OrderBy(value => value)
            .ToArray();
        Dictionary<MidoraId, int> sourceIndices = new(sourceIds.Length);
        for (int i = 0; i < sourceIds.Length; i++)
        {
            sourceIndices.Add(sourceIds[i], i);
        }
        int[] initiallyDisabled = preserveFilteredTrackEvents && audibleTrackIds is not null
            ? sourceIds.Select((value, index) => (value, index))
                .Where(value => !audibleTrackIds.Contains(value.value))
                .Select(value => value.index)
                .ToArray()
            : [];
        CanonicalAudioUnitFragment[] canonicalFragments =
            CanonicalAudioUnitProjection.Create(compiled).Fragments.ToArray()
                .Where(fragment => preserveFilteredTrackEvents
                    || audibleTrackIds is null
                    || audibleTrackIds.Contains(fragment.TrackId))
                .ToArray();
        MidiUnitFragmentRenderPlan[] unitFragments = canonicalFragments
            .Select(fragment => new MidiUnitFragmentRenderPlan(
                ResolveAllocation(compiled, fragment).ZeroBasedPort,
                ResolveAllocation(compiled, fragment).ZeroBasedChannel,
                fragment.TrackId.Value,
                fragment.SegmentId.Value,
                fragment.EventInstrumentId.Value,
                fragment.InstanceGroupId.Value,
                fragment.SubVoiceId.Value,
                sourceIndices[fragment.TrackId],
                map.TickToSampleFrame(fragment.EffectiveStartTick, compiled.StartTick, sampleRate),
                map.TickToSampleFrame(fragment.EffectiveEndTick, compiled.StartTick, sampleRate),
                fragment.SemanticFingerprint,
                fragment.Events.ToArray()
                    .Select(value => new ScheduledMidiMessage(
                        map.TickToSampleFrame(
                            fragment.GroupStartTick + value.RelativeTick,
                            compiled.StartTick,
                            sampleRate),
                        value.Message,
                        sourceIndices[fragment.TrackId]))
                    .ToArray()))
            .OrderBy(value => value.CanonicalUnitNumber)
            .ThenBy(value => value.StartFrame)
            .ThenBy(value => value.InstanceGroupId)
            .ThenBy(value => value.SubVoiceId)
            .ToArray();
        MidiSegmentRenderPlan[] segments = unitFragments
            .GroupBy(value => (value.TrackId, value.SegmentId, value.SourceIndex))
            .Select(group => CreateSegmentPlan(group.Key, group.ToArray()))
            .OrderBy(value => value.SourceIndex)
            .ThenBy(value => value.StartFrame)
            .ThenBy(value => value.SegmentId)
            .ToArray();
        List<MidiPortRenderPlan> ports = [];
        CanonicalMidiEvent[] events = compiled.Events.ToArray();
        for (byte port = 0; port < 16; port++)
        {
            List<ScheduledMidiMessage> scheduled = [];
            foreach (CanonicalMidiEvent value in events)
            {
                if (value.ZeroBasedPort != port)
                {
                    continue;
                }
                bool hasTrack = value.Source.TrackId != default;
                if (!preserveFilteredTrackEvents && audibleTrackIds is not null
                    && hasTrack && !audibleTrackIds.Contains(value.Source.TrackId))
                {
                    continue;
                }
                long frame = map.TickToSampleFrame(value.Tick, compiled.StartTick, sampleRate);
                int sourceIndex = hasTrack ? sourceIndices[value.Source.TrackId] : -1;
                scheduled.Add(new(frame, value.Message, sourceIndex));
            }
            if (scheduled.Count != 0)
            {
                ports.Add(new MidiPortRenderPlan(port, scheduled.ToArray()));
            }
        }
        return new MidiRenderPlan(
            sampleRate,
            totalFrames,
            ports.ToArray(),
            sourceIds.Select(value => value.Value).ToArray(),
            initiallyDisabled,
            unitFragments,
            segments);
    }

    private static MidiSegmentRenderPlan CreateSegmentPlan(
        (long TrackId, long SegmentId, int SourceIndex) identity,
        MidiUnitFragmentRenderPlan[] fragments)
    {
        long startFrame = fragments.Min(value => value.StartFrame);
        long endFrame = fragments.Max(value => value.EndFrame);
        using MemoryStream payload = new();
        using (BinaryWriter writer = new(payload, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("MIDORA_SAMPLE_DOMAIN_SEGMENT_PROJECTION_V1");
            writer.Write(identity.TrackId);
            writer.Write(identity.SegmentId);
            writer.Write(endFrame - startFrame);
            writer.Write(fragments.Length);
            foreach (MidiUnitFragmentRenderPlan fragment in fragments
                .OrderBy(value => value.StartFrame)
                .ThenBy(value => value.EndFrame)
                .ThenBy(value => value.EventInstrumentId)
                .ThenBy(value => value.InstanceGroupId)
                .ThenBy(value => value.SubVoiceId))
            {
                // Physical route is deliberately excluded: the Segment stem is the
                // deterministic sum of abstract 1-channel Unit projections.
                writer.Write(fragment.EventInstrumentId);
                writer.Write(fragment.InstanceGroupId);
                writer.Write(fragment.SubVoiceId);
                writer.Write(fragment.StartFrame - startFrame);
                writer.Write(fragment.EndFrame - startFrame);
                writer.Write(fragment.SemanticFingerprint);
                writer.Write(fragment.Events.Length);
                foreach (ScheduledMidiMessage value in fragment.Events)
                {
                    writer.Write(value.SampleFrame - startFrame);
                    writer.Write(value.Message.PackedValue);
                }
            }
        }
        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(
            payload.GetBuffer().AsSpan(0, checked((int)payload.Length))));
        return new(
            identity.TrackId,
            identity.SegmentId,
            identity.SourceIndex,
            startFrame,
            endFrame,
            fingerprint);
    }

    private static ChannelUnitAllocation ResolveAllocation(
        CanonicalCompiledResult compiled,
        CanonicalAudioUnitFragment fragment) => compiled.Allocations.ToArray()
            .First(value =>
                value.InstanceGroupId == fragment.InstanceGroupId
                && value.SubVoiceId == fragment.SubVoiceId);
}
