using System;
using System.Collections.Generic;
using System.Text;

namespace Midora.Audio.Bass.Internals;

internal class BassMidiPortFactory : IMidiPortFactory
{
    public IMidiPort Create(params IEnumerable<SoundfontRef> soundfontRefs)
    {
        return new BassMidiPort([.. soundfontRefs]);
    }
}
