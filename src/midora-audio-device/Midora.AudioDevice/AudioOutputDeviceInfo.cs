using System;
using System.Collections.Generic;
using System.Text;

namespace Midora.AudioDevice;

public sealed record class AudioOutputDeviceInfo(
    string Id,
    string? Name,
    AudioFormat AudioFormat
);
