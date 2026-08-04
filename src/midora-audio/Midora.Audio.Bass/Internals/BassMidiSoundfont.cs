using Midora.NativeInterops.Bass;
using Midora.NativeInterops.BassMidi;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Midora.Audio.Bass.Internals;

internal unsafe sealed class BassMidiSoundfont : ISoundfont
{
    private readonly string _sourceFilePath;
    private readonly uint _soundfontHandle;

    private bool _isDisposed;

    public BassMidiSoundfont(string sourceFilePath)
    {
        _sourceFilePath = sourceFilePath;

        nint sondfontNamePtr = 0;

        try
        {
            sondfontNamePtr = Marshal.StringToHGlobalUni(_sourceFilePath);
            _soundfontHandle = BASSMIDI.FontInit(
                (void*)sondfontNamePtr,
                BASS.BASS_UNICODE | BASSMIDI.BASS_MIDI_FONT_MMAP
            );
            if (0 == _soundfontHandle)
            {
                throw new BassException();
            }
        }
        finally
        {
            if (sondfontNamePtr != 0)
            {
                try { Marshal.FreeHGlobal(sondfontNamePtr); } catch { /* 随它去吧~ */ }
            }
        }
    }

    public string SourceFilePath => _sourceFilePath;

    public uint SoundfontHandle => _soundfontHandle;

    private void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            if (disposing)
            {
                
            }

            if (0 != _soundfontHandle)
            {
                if (0 == BASSMIDI.FontFree(_soundfontHandle))
                {
                    throw new BassException();
                }
            }

            _isDisposed = true;
        }
    }

    ~BassMidiSoundfont()
    {
        Dispose(disposing: false);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
