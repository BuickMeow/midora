using System;
using System.Collections.Generic;
using System.Text;

namespace Midora.Audio.Bass.Internals;

internal class BassMidiSoundfontFactory : ISoundfontFactory
{
    public ISoundfont Create(string filePath)
    {
        return new BassMidiSoundfont(filePath);
    }
}
