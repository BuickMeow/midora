using Midora.AudioDevice;

namespace Midora.Audio;

public interface IMidiPort : IAudioRenderSource, IDisposable
{
    IReadOnlyList<SoundfontRef> SoundfontRefs { get; }
}
