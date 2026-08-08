using Midora.Midi;

namespace Midora.Audio;

public sealed class MidiRenderPlan
{
    private readonly MidiPortRenderPlan[] _ports;
    private readonly MidiUnitRenderPlan[] _units;
    private readonly MidiUnitFragmentRenderPlan[] _unitFragments;
    private readonly long[] _sourceIds;
    private readonly int[] _initiallyDisabledSourceIndices;

    public MidiRenderPlan(
        int sampleRate,
        long totalFrameCount,
        ReadOnlySpan<MidiPortRenderPlan> ports,
        ReadOnlySpan<long> sourceIds = default,
        ReadOnlySpan<int> initiallyDisabledSourceIndices = default,
        ReadOnlySpan<MidiUnitFragmentRenderPlan> unitFragments = default)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        if (totalFrameCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalFrameCount));
        }

        if (ports.Length > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(ports));
        }

        SampleRate = sampleRate;
        TotalFrameCount = totalFrameCount;
        _ports = ports.ToArray();
        _units = CreateUnitStreamSlots(_ports);
        _unitFragments = unitFragments.ToArray();
        _sourceIds = sourceIds.ToArray();
        _initiallyDisabledSourceIndices = initiallyDisabledSourceIndices.ToArray();
        ValidateSources(_sourceIds, _initiallyDisabledSourceIndices);
        ValidatePorts(_ports, totalFrameCount, _sourceIds.Length);
        ValidateUnitFragments(_unitFragments, totalFrameCount, _sourceIds.Length);
    }

    public int SampleRate { get; }

    public long TotalFrameCount { get; }

    public ReadOnlySpan<MidiPortRenderPlan> Ports => _ports;

    public ReadOnlySpan<MidiUnitRenderPlan> Units => _units;

    public ReadOnlySpan<MidiUnitFragmentRenderPlan> UnitFragments => _unitFragments;

    public ReadOnlySpan<long> SourceIds => _sourceIds;

    public ReadOnlySpan<int> InitiallyDisabledSourceIndices => _initiallyDisabledSourceIndices;

    public int FindSourceIndex(long sourceId) => Array.IndexOf(_sourceIds, sourceId);

    private static void ValidateSources(ReadOnlySpan<long> sourceIds, ReadOnlySpan<int> disabledIndices)
    {
        HashSet<long> seen = [];
        foreach (long sourceId in sourceIds)
        {
            if (sourceId <= 0 || !seen.Add(sourceId))
            {
                throw new ArgumentException("Render source IDs must be non-empty and unique.", nameof(sourceIds));
            }
        }

        HashSet<int> disabled = [];
        foreach (int sourceIndex in disabledIndices)
        {
            if ((uint)sourceIndex >= (uint)sourceIds.Length || !disabled.Add(sourceIndex))
            {
                throw new ArgumentException(
                    "Initially disabled source indices must be unique and reference the source table.",
                    nameof(disabledIndices));
            }
        }
    }

    private static void ValidatePorts(
        ReadOnlySpan<MidiPortRenderPlan> ports,
        long totalFrameCount,
        int sourceCount)
    {
        int previousPort = -1;
        for (int i = 0; i < ports.Length; i++)
        {
            MidiPortRenderPlan port = ports[i] ?? throw new ArgumentException("A render port cannot be null.", nameof(ports));
            if (port.ZeroBasedPortNumber <= previousPort)
            {
                throw new ArgumentException("Render ports must be unique and sorted by zero-based Port number.", nameof(ports));
            }

            ReadOnlySpan<ScheduledMidiMessage> events = port.Events;
            if (!events.IsEmpty && events[^1].SampleFrame > totalFrameCount)
            {
                throw new ArgumentException("A MIDI event is beyond the render range.", nameof(ports));
            }

            foreach (ScheduledMidiMessage scheduled in events)
            {
                if (scheduled.SourceIndex < -1 || scheduled.SourceIndex >= sourceCount)
                {
                    throw new ArgumentException("A MIDI event references an invalid render source.", nameof(ports));
                }
            }

            previousPort = port.ZeroBasedPortNumber;
        }
    }

    private static MidiUnitRenderPlan[] CreateUnitStreamSlots(ReadOnlySpan<MidiPortRenderPlan> ports)
    {
        List<MidiUnitRenderPlan> result = [];
        foreach (MidiPortRenderPlan port in ports)
        {
            for (byte channel = 0; channel < 16; channel++)
            {
                List<ScheduledMidiMessage> events = [];
                foreach (ScheduledMidiMessage scheduled in port.Events)
                {
                    if (scheduled.Message.ChannelNumber != channel)
                    {
                        continue;
                    }
                    uint packed = (scheduled.Message.PackedValue & ~MidiMessage.ChannelNumberMask);
                    events.Add(scheduled with { Message = MidiMessage.FromPackedValue(packed) });
                }
                if (events.Count != 0)
                {
                    result.Add(new(port.ZeroBasedPortNumber, channel, events.ToArray()));
                }
            }
        }
        return result.ToArray();
    }

    private static void ValidateUnitFragments(
        ReadOnlySpan<MidiUnitFragmentRenderPlan> fragments,
        long totalFrameCount,
        int sourceCount)
    {
        Dictionary<int, long> endByUnit = [];
        HashSet<(long InstanceGroupId, long SubVoiceId)> identities = [];
        foreach (MidiUnitFragmentRenderPlan fragment in fragments)
        {
            if (fragment.EndFrame > totalFrameCount
                || fragment.SourceIndex >= sourceCount
                || !identities.Add((fragment.InstanceGroupId, fragment.SubVoiceId)))
            {
                throw new ArgumentException(
                    "The Unit fragment set contains an invalid boundary, source, or duplicate identity.",
                    nameof(fragments));
            }
            if (endByUnit.TryGetValue(fragment.CanonicalUnitNumber, out long previousEnd)
                && fragment.StartFrame < previousEnd)
            {
                throw new ArgumentException(
                    "Unit fragments assigned to one canonical stream slot must not overlap.",
                    nameof(fragments));
            }
            endByUnit[fragment.CanonicalUnitNumber] = fragment.EndFrame;
        }
    }
}
