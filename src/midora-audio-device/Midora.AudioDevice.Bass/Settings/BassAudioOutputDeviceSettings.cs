namespace Midora.AudioDevice.Bass.Settings;

/// <summary>
/// macOS-first BASS native output settings. The device request maps onto BASS buffer
/// configuration; the exact latency/stability mapping is pinned by the macOS audio spike.
/// </summary>
public sealed record class BassAudioOutputDeviceSettings
{
    public BassAudioOutputDeviceSettings(int deviceBufferRequestMilliseconds = 50)
    {
        if (deviceBufferRequestMilliseconds is < 5 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(deviceBufferRequestMilliseconds));
        }

        DeviceBufferRequestMilliseconds = deviceBufferRequestMilliseconds;
    }

    public int DeviceBufferRequestMilliseconds { get; }
}
