namespace Midora.AudioDevice;

public sealed record class AudioOutputDeviceInfo(
    string Id,
    string? Name,
    AudioFormat AudioFormat,
    bool IsSystemDefault = false
);
