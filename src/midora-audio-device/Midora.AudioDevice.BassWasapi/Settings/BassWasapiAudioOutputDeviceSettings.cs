using System;
using System.Collections.Generic;
using System.Text;

namespace Midora.AudioDevice.BassWasapi.Settings;

public sealed record class BassWasapiAudioOutputDeviceSettings(
    uint DeviceBufferSamples = 1024
);
