using Midora.Midi;

namespace Midora.Audio;

public enum MidiMonitoringCommandKind : byte
{
    SetSourceEnabled,
    SendMessage
}

public readonly record struct MidiMonitoringCommand(
    MidiMonitoringCommandKind Kind,
    int SourceIndex,
    byte ZeroBasedPortNumber,
    MidiMessage Message,
    bool SourceEnabled)
{
    public static MidiMonitoringCommand EnableSource(int sourceIndex) =>
        new(MidiMonitoringCommandKind.SetSourceEnabled, sourceIndex, 0, default, true);

    public static MidiMonitoringCommand DisableSource(int sourceIndex) =>
        new(MidiMonitoringCommandKind.SetSourceEnabled, sourceIndex, 0, default, false);

    public static MidiMonitoringCommand Send(byte zeroBasedPortNumber, MidiMessage message) =>
        new(MidiMonitoringCommandKind.SendMessage, -1, zeroBasedPortNumber, message, false);
}
