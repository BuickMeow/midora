using System;
using System.Collections.Generic;
using System.Text;

namespace Midora.AudioDevice;

public interface IAudioOutputDevice : IDisposable
{
    AudioOutputDeviceInfo Info { get; }

    void Start();

    void Stop(bool flush);
}
