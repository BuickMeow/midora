using Midora.Audio.Bass.Internals;
using NativeBass = Midora.NativeInterops.Bass.BASS;
using NativeBassMidi = Midora.NativeInterops.BassMidi.BASSMIDI;

namespace Midora.Audio.Bass;

internal sealed unsafe class PersistentBassMidiSoundFont : IDisposable
{
    private readonly BassNativeRuntime.Lease _runtimeLease;
    private readonly HashSet<int> _loadedPresetKeys = [];
    private uint _handle;
    private bool _allPresetsLoaded;
    private bool _disposed;

    public PersistentBassMidiSoundFont(string soundFontPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(soundFontPath);
        string path = Path.GetFullPath(soundFontPath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The Project SoundFont does not exist.", path);
        }

        _runtimeLease = BassNativeRuntime.Acquire();
        try
        {
            fixed (char* pointer = path)
            {
                _handle = NativeBassMidi.FontInit(
                    pointer,
                    NativeBass.BASS_UNICODE | NativeBassMidi.BASS_MIDI_FONT_MMAP);
            }
            if (_handle == 0)
            {
                Throw("BASS_MIDI_FontInit");
            }
            EnsurePreset(0, 0);
        }
        catch
        {
            if (_handle != 0)
            {
                _ = NativeBassMidi.FontFree(_handle);
                _handle = 0;
            }
            _runtimeLease.Dispose();
            throw;
        }
    }

    public uint Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _handle;
        }
    }

    public void EnsureReferencedPresets(MidiRenderPlan plan)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (int key in BassMidiRenderer.CollectReferencedPresetKeys(plan))
        {
            EnsurePreset(key & 127, key >> 7);
        }
    }

    private void EnsurePreset(int preset, int bank)
    {
        if (_allPresetsLoaded)
        {
            return;
        }
        int key = (bank << 7) | preset;
        if (_loadedPresetKeys.Contains(key))
        {
            return;
        }
        if (NativeBassMidi.FontLoad(_handle, preset, bank) != 0)
        {
            _loadedPresetKeys.Add(key);
            return;
        }

        int error = NativeBass.ErrorGetCode();
        if (error != NativeBass.BASS_ERROR_NOTAVAIL)
        {
            Throw("BASS_MIDI_FontLoad", error);
        }
        if (NativeBassMidi.FontLoad(_handle, -1, -1) == 0)
        {
            Throw("BASS_MIDI_FontLoad(all fallback presets)");
        }
        _allPresetsLoaded = true;
        _loadedPresetKeys.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Exception? failure = null;
        if (_handle != 0 && NativeBassMidi.FontFree(_handle) == 0)
        {
            failure = new MidoraAudioException(
                $"BASS_MIDI_FontFree failed with BASS error {NativeBass.ErrorGetCode()}.");
        }
        _handle = 0;
        try
        {
            _runtimeLease.Dispose();
        }
        catch (Exception exception)
        {
            failure = failure is null
                ? exception
                : new AggregateException(failure, exception);
        }
        if (failure is not null)
        {
            throw failure;
        }
    }

    private static void Throw(string operation, int? error = null) =>
        throw new MidoraAudioException(
            $"{operation} failed with BASS error {error ?? NativeBass.ErrorGetCode()}.");
}
