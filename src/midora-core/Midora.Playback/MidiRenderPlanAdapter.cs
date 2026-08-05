using Midora.Audio;
using Midora.Compiler;
using Midora.Domain;

namespace Midora.Playback;

public static class MidiRenderPlanAdapter
{
    public static MidiRenderPlan Create(
        CanonicalCompiledResult compiled,
        int sampleRate,
        IReadOnlySet<MidoraId>? audibleTrackIds = null)
        => CreateCore(compiled, sampleRate, audibleTrackIds, preserveFilteredTrackEvents: false);

    public static MidiRenderPlan CreateRealtime(
        CanonicalCompiledResult compiled,
        int sampleRate,
        IReadOnlySet<MidoraId> audibleTrackIds)
    {
        ArgumentNullException.ThrowIfNull(audibleTrackIds);
        return CreateCore(compiled, sampleRate, audibleTrackIds, preserveFilteredTrackEvents: true);
    }

    private static MidiRenderPlan CreateCore(
        CanonicalCompiledResult compiled,
        int sampleRate,
        IReadOnlySet<MidoraId>? audibleTrackIds,
        bool preserveFilteredTrackEvents)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        if (!compiled.IsConsumable || compiled.IsPartial && compiled.Purpose is not CompilationPurpose.Playback
            and not CompilationPurpose.Range and not CompilationPurpose.AudioRender
            and not CompilationPurpose.SegmentPreview and not CompilationPurpose.EventInstrumentPreview)
        {
            throw new ArgumentException("Only a consumable canonical result can produce an audio plan.", nameof(compiled));
        }
        if (sampleRate is < 8_000 or > 192_000)
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
            initiallyDisabled);
    }
}
