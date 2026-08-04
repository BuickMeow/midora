using Midora.NativeInterops.Bass;
using Midora.NativeInterops.BassMidi;

namespace Midora.Audio.Bass.Internals;

internal unsafe class BassMidiPort : IMidiPort
{
    private readonly IReadOnlyList<SoundfontRef> _soundfontRefs;
    private readonly uint _bassStreamHandle;

    private bool _isDisposed;

    public BassMidiPort(IReadOnlyList<SoundfontRef> soundfontRefs)
    {
        _soundfontRefs = soundfontRefs;

        try
        {
            BASSMIDI.BASS_MIDI_FONT* bassFonts = stackalloc BASSMIDI.BASS_MIDI_FONT[soundfontRefs.Count];
            for (int i = 0; i < soundfontRefs.Count; i++)
            {
                SoundfontRef sfRef = soundfontRefs[i];

                if (sfRef.Soundfont is not BassMidiSoundfont bassMidiSoundfont)
                {
                    throw new ArgumentException(
                        $"Soundfont type mismatched: expected: {typeof(BassMidiSoundfont)}, actual: {sfRef.Soundfont.GetType()}",
                        nameof(soundfontRefs)
                    );
                }

                bassFonts[i] = new BASSMIDI.BASS_MIDI_FONT
                {
                    font = bassMidiSoundfont.SoundfontHandle,
                    preset = sfRef.Preset,
                    bank = sfRef.Bank
                };
            }

            _bassStreamHandle = BASSMIDI.StreamCreate(
                16,
                BASS.BASS_SAMPLE_FLOAT | BASS.BASS_STREAM_DECODE | BASSMIDI.BASS_MIDI_NOFX | BASSMIDI.BASS_MIDI_NOTEOFF1,
                1
            );

            if (0 == _bassStreamHandle)
            {
                throw new BassException();
            }

            if (0 == BASSMIDI.StreamSetFonts(_bassStreamHandle, bassFonts, (uint)soundfontRefs.Count))
            {
                throw new BassException();
            }
        }
        catch
        {
            if (0 != _bassStreamHandle)
            {
                _ = BASS.StreamFree(_bassStreamHandle);
            }

            throw;
        }
    }

    IReadOnlyList<SoundfontRef> IMidiPort.SoundfontRefs => _soundfontRefs;

    public int Render(void* destination, int requiredBytes)
    {
        return (int)BASS.ChannelGetData(_bassStreamHandle, destination, (uint)requiredBytes);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            if (disposing)
            {
                
            }

            if (0 != _bassStreamHandle)
            {
                if (0 == BASS.StreamFree(_bassStreamHandle))
                {
                    throw new BassException();
                }
            }

            _isDisposed = true;
        }
    }

    ~BassMidiPort()
    {
        Dispose(disposing: false);
    }

    public void Dispose()
    {      
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
