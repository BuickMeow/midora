using System;
using System.Collections.Generic;
using System.Text;

namespace Midora.AudioDevice;

public interface IAudioOutputDeviceFactory
{
    IReadOnlyList<AudioOutputDeviceInfo> GetDevices();

    IAudioOutputDevice Open(AudioOutputDeviceInfo deviceInfo, IAudioRenderSource audioRenderSource);
}
