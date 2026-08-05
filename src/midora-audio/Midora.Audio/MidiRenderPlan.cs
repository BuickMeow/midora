namespace Midora.Audio;

public sealed class MidiRenderPlan
{
    private readonly MidiPortRenderPlan[] _ports;
    private readonly Guid[] _sourceIds;
    private readonly int[] _initiallyDisabledSourceIndices;

    public MidiRenderPlan(
        int sampleRate,
        long totalFrameCount,
        ReadOnlySpan<MidiPortRenderPlan> ports,
        ReadOnlySpan<Guid> sourceIds = default,
        ReadOnlySpan<int> initiallyDisabledSourceIndices = default)
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
        _sourceIds = sourceIds.ToArray();
        _initiallyDisabledSourceIndices = initiallyDisabledSourceIndices.ToArray();
        ValidateSources(_sourceIds, _initiallyDisabledSourceIndices);
        ValidatePorts(_ports, totalFrameCount, _sourceIds.Length);
    }

    public int SampleRate { get; }

    public long TotalFrameCount { get; }

    public ReadOnlySpan<MidiPortRenderPlan> Ports => _ports;

    public ReadOnlySpan<Guid> SourceIds => _sourceIds;

    public ReadOnlySpan<int> InitiallyDisabledSourceIndices => _initiallyDisabledSourceIndices;

    public int FindSourceIndex(Guid sourceId) => Array.IndexOf(_sourceIds, sourceId);

    private static void ValidateSources(ReadOnlySpan<Guid> sourceIds, ReadOnlySpan<int> disabledIndices)
    {
        HashSet<Guid> seen = [];
        foreach (Guid sourceId in sourceIds)
        {
            if (sourceId == Guid.Empty || !seen.Add(sourceId))
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
}
