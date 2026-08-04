using System;
using System.Collections.Generic;
using System.Text;

namespace Midora.Audio;

public readonly record struct SoundfontRef(
    ISoundfont Soundfont,
    int Preset,
    int Bank
);
