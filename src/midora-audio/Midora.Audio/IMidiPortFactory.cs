namespace Midora.Audio;

public interface IMidiPortFactory
{
    IMidiPort Create(params IEnumerable<SoundfontRef> soundfontRefs);
}
