using System;
using System.Collections.Generic;
using System.Text;

namespace Midora.AudioDevice;

public sealed class MidoraAudioDeviceException(string message) : Exception(message);
