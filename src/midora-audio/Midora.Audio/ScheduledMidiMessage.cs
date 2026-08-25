using Midora.Midi;

namespace Midora.Audio;

public readonly record struct ScheduledMidiMessage(
    long SampleFrame,
    MidiMessage Message,
    int SourceIndex = -1,
    MidiChannelModeSystemExclusive? ChannelModeSystemExclusive = null)
{
    public static ScheduledMidiMessage CreateChannelModeSystemExclusive(
        long sampleFrame,
        byte channel,
        MidiChannelModeSystemExclusive value,
        int sourceIndex = -1) => new(
            sampleFrame,
            MidiMessage.ProgramChange(channel, 0),
            sourceIndex,
            value with { TargetChannel = channel });

    public bool IsChannelModeSystemExclusive => ChannelModeSystemExclusive.HasValue;

    internal void ValidatePayload()
    {
        if (ChannelModeSystemExclusive is not { } systemExclusive) return;
        if (!Enum.IsDefined(systemExclusive.Kind)
            || systemExclusive.DeviceId is < 0x10 or > 0x1f
            || systemExclusive.ModeValue > 2
            || systemExclusive.TargetChannel != Message.ChannelNumber
            || Message.MessageType != MidiMessageType.ProgramChange
            || Message.Byte1 != 0)
        {
            throw new ArgumentException("The scheduled channel-mode SysEx event is malformed.");
        }
    }
}
