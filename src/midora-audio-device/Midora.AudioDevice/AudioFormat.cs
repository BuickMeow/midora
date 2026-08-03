using System;
using System.Collections.Generic;
using System.Text;

namespace Midora.AudioDevice;

public readonly record struct AudioFormat(
    int SampleRate,
    int ChannelCount,
    AudioSampleFormat SampleFormat)
{
    public int BytesPerFrame =>
        ChannelCount * SampleFormat switch
        {
            AudioSampleFormat.Int16 => 2,
            AudioSampleFormat.Int24 => 3,
            AudioSampleFormat.Int32 => 4,
            AudioSampleFormat.Float32 => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(SampleFormat))
        };
}

public enum AudioSampleFormat
{
    Int16,
    Int24,
    Int32,
    Float32
}
