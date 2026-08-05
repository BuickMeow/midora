namespace Midora.Audio;

public sealed class MidiRenderPlan
{
    private readonly MidiPortRenderPlan[] _ports;

    public MidiRenderPlan(int sampleRate, long totalFrameCount, ReadOnlySpan<MidiPortRenderPlan> ports)
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
        ValidatePorts(_ports, totalFrameCount);
    }

    public int SampleRate { get; }

    public long TotalFrameCount { get; }

    public ReadOnlySpan<MidiPortRenderPlan> Ports => _ports;

    private static void ValidatePorts(ReadOnlySpan<MidiPortRenderPlan> ports, long totalFrameCount)
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

            previousPort = port.ZeroBasedPortNumber;
        }
    }
}
