using Midora.Midi;

namespace Midora.Audio;

public sealed class MidiUnitRenderPlan
{
    private readonly ScheduledMidiMessage[] _events;

    public MidiUnitRenderPlan(
        byte canonicalZeroBasedPortNumber,
        byte canonicalZeroBasedChannelNumber,
        ReadOnlySpan<ScheduledMidiMessage> events)
    {
        if (canonicalZeroBasedPortNumber >= 16)
        {
            throw new ArgumentOutOfRangeException(nameof(canonicalZeroBasedPortNumber));
        }
        if (canonicalZeroBasedChannelNumber >= 16)
        {
            throw new ArgumentOutOfRangeException(nameof(canonicalZeroBasedChannelNumber));
        }

        CanonicalZeroBasedPortNumber = canonicalZeroBasedPortNumber;
        CanonicalZeroBasedChannelNumber = canonicalZeroBasedChannelNumber;
        _events = events.ToArray();
        ValidateEvents(_events);
    }

    public byte CanonicalZeroBasedPortNumber { get; }
    public byte CanonicalZeroBasedChannelNumber { get; }
    public int CanonicalUnitNumber =>
        (CanonicalZeroBasedPortNumber * 16) + CanonicalZeroBasedChannelNumber;
    public ReadOnlySpan<ScheduledMidiMessage> Events => _events;

    private static void ValidateEvents(ReadOnlySpan<ScheduledMidiMessage> events)
    {
        long previousFrame = 0;
        for (int i = 0; i < events.Length; i++)
        {
            ScheduledMidiMessage scheduled = events[i];
            if (scheduled.SampleFrame < 0 || i != 0 && scheduled.SampleFrame < previousFrame)
            {
                throw new ArgumentException(
                    "Unit MIDI events must be ordered by non-negative sample frame.",
                    nameof(events));
            }
            MidiMessage message = scheduled.Message;
            if (!message.IsChannelVoiceMessage || message.ChannelNumber != 0)
            {
                throw new ArgumentException(
                    "A Unit render event must be a channel-0 MIDI channel voice message.",
                    nameof(events));
            }
            if (message.Length is < 2 or > 3 || message.Byte1 > 127 || message.Byte2 > 127)
            {
                throw new ArgumentException("The Unit render plan contains malformed MIDI.", nameof(events));
            }
            if (message.MessageType == MidiMessageType.ControlChange && message.Byte1 is 91 or 93)
            {
                throw new ArgumentException(
                    "CC91 and CC93 are forbidden by the initial-release audio contract.",
                    nameof(events));
            }
            previousFrame = scheduled.SampleFrame;
        }
    }
}
