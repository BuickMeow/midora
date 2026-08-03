using System;
using System.Collections.Generic;
using System.Text;

namespace Midora.AudioDevice;

public unsafe interface IAudioRenderSource
{
    int Render(void* destination, int requiredBytes);
}
