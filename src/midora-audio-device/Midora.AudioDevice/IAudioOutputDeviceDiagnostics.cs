namespace Midora.AudioDevice;

/// <summary>
/// Runtime counters of a realtime output device. Values are read by the audio worker and
/// by the playback owners; they never enter Project, canonical or cache identity.
/// </summary>
public interface IAudioOutputDeviceDiagnostics
{
    long CallbackCount { get; }

    long CallbackAllocatedBytes { get; }

    bool CallbackFaulted { get; }

    long ConsumedFrameCount { get; }

    uint ActualBufferFrameCount { get; }

    bool IsProcessingStarted { get; }

    bool DeviceLost { get; }

    bool DefaultDeviceChanged { get; }

    bool CleanupFaulted { get; }

    int CleanupErrorCode { get; }
}

/// <summary>
/// Platform-specific flush of audio already submitted to the endpoint. Windows locks and
/// resets the WASAPI endpoint buffer; macOS stops the stream with a position reset because
/// CoreAudio exposes no equivalent endpoint reset.
/// </summary>
public interface IAudioOutputDeviceFlushController
{
    /// <summary>Returns the consumer frame frontier after the flush.</summary>
    long StopAndResetBufferedOutput();
}

public static class AudioOutputDeviceSelection
{
    /// <summary>
    /// A selected output is invalidated when its device is lost, or when the session follows
    /// the system default device and that default changed.
    /// </summary>
    public static bool IsInvalidated(
        bool followsSystemDefault,
        bool defaultDeviceChanged,
        bool deviceLost) =>
        deviceLost || (followsSystemDefault && defaultDeviceChanged);
}
