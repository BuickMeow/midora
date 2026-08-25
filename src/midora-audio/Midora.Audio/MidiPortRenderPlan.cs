using Midora.Midi;

namespace Midora.Audio;

public sealed class MidiPortRenderPlan
{
    private readonly ScheduledMidiMessage[] _events;

    public MidiPortRenderPlan(byte zeroBasedPortNumber, ReadOnlySpan<ScheduledMidiMessage> events)
    {
        if (zeroBasedPortNumber >= 16)
        {
            throw new ArgumentOutOfRangeException(nameof(zeroBasedPortNumber));
        }

        ZeroBasedPortNumber = zeroBasedPortNumber;
        _events = events.ToArray();
        ValidateEvents(_events);
    }

    public byte ZeroBasedPortNumber { get; }

    public ReadOnlySpan<ScheduledMidiMessage> Events => _events;

    private static void ValidateEvents(ReadOnlySpan<ScheduledMidiMessage> events)
    {
        long previousFrame = 0;

        for (int i = 0; i < events.Length; i++)
        {
            ScheduledMidiMessage scheduled = events[i];
            if (scheduled.SampleFrame < 0 || (i != 0 && scheduled.SampleFrame < previousFrame))
            {
                throw new ArgumentException("MIDI events must be ordered by non-negative sample frame.", nameof(events));
            }

            MidiMessage message = scheduled.Message;
            scheduled.ValidatePayload();
            if (!message.IsChannelVoiceMessage)
            {
                throw new ArgumentException("Initial-release audio rendering only accepts MIDI channel voice messages.", nameof(events));
            }

            if (message.Length is < 2 or > 3 || message.Byte1 > 127 || message.Byte2 > 127)
            {
                throw new ArgumentException("The render plan contains a malformed MIDI channel message.", nameof(events));
            }

            if (!scheduled.IsChannelModeSystemExclusive
                && message.MessageType == MidiMessageType.ControlChange
                && message.Byte1 is 91 or 93)
            {
                throw new ArgumentException("CC91 and CC93 are forbidden by the initial-release audio contract.", nameof(events));
            }

            previousFrame = scheduled.SampleFrame;
        }
    }
}
