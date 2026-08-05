using System;
using System.Collections.Generic;
using System.Text;

namespace Midora.AudioDevice.BassWasapi.Settings;

public sealed record class BassWasapiAudioOutputDeviceSettings
{
    public BassWasapiAudioOutputDeviceSettings(int deviceBufferRequestMilliseconds = 50)
    {
        if (deviceBufferRequestMilliseconds is < 5 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(deviceBufferRequestMilliseconds));
        }

        DeviceBufferRequestMilliseconds = deviceBufferRequestMilliseconds;
    }

    public int DeviceBufferRequestMilliseconds { get; }
}
