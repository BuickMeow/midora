using Midora.Audio.Bass.Internals;
using NativeBass = Midora.NativeInterops.Bass.BASS;
using NativeBassMidi = Midora.NativeInterops.BassMidi.BASSMIDI;

namespace Midora.Audio.Bass;

internal sealed unsafe class PersistentBassMidiSoundFont : IDisposable
{
    private readonly BassNativeRuntime.Lease _runtimeLease;
    private readonly FontState[] _fonts;
    private bool _disposed;

    public PersistentBassMidiSoundFont(string soundFontPath)
        : this([soundFontPath])
    {
    }

    public PersistentBassMidiSoundFont(IReadOnlyList<string> soundFontPaths)
    {
        ArgumentNullException.ThrowIfNull(soundFontPaths);
        if (soundFontPaths.Count == 0)
        {
            throw new ArgumentException("At least one enabled SoundFont is required.", nameof(soundFontPaths));
        }

        string[] paths = soundFontPaths.Select(Path.GetFullPath).ToArray();
        foreach (string path in paths)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("An enabled application SoundFont does not exist.", path);
            }
        }

        _runtimeLease = BassNativeRuntime.Acquire();
        _fonts = new FontState[paths.Length];
        try
        {
            for (int index = 0; index < paths.Length; index++)
            {
                fixed (char* pointer = paths[index])
                {
                    _fonts[index] = new FontState(NativeBassMidi.FontInit(
                        pointer,
                        NativeBass.BASS_UNICODE | NativeBassMidi.BASS_MIDI_FONT_MMAP));
                }
                if (_fonts[index].Handle == 0)
                {
                    Throw("BASS_MIDI_FontInit");
                }
                EnsurePreset(_fonts[index], 0, 0);
            }
        }
        catch
        {
            FreeFonts();
            _runtimeLease.Dispose();
            throw;
        }
    }

    public uint[] Handles
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _fonts.Select(value => value.Handle).ToArray();
        }
    }

    public void EnsureReferencedPresets(MidiRenderPlan plan)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (int key in BassMidiRenderer.CollectReferencedPresetKeys(plan))
        {
            foreach (FontState font in _fonts)
            {
                EnsurePreset(font, key & 127, key >> 7);
            }
        }
    }

    private static void EnsurePreset(FontState font, int preset, int bank)
    {
        if (font.AllPresetsLoaded)
        {
            return;
        }
        int key = (bank << 7) | preset;
        if (font.LoadedPresetKeys.Contains(key))
        {
            return;
        }
        if (NativeBassMidi.FontLoad(font.Handle, preset, bank) != 0)
        {
            font.LoadedPresetKeys.Add(key);
            return;
        }

        int error = NativeBass.ErrorGetCode();
        if (error != NativeBass.BASS_ERROR_NOTAVAIL)
        {
            Throw("BASS_MIDI_FontLoad", error);
        }
        if (NativeBassMidi.FontLoad(font.Handle, -1, -1) == 0)
        {
            Throw("BASS_MIDI_FontLoad(all fallback presets)");
        }
        font.AllPresetsLoaded = true;
        font.LoadedPresetKeys.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Exception? failure = FreeFonts();
        try
        {
            _runtimeLease.Dispose();
        }
        catch (Exception exception)
        {
            failure = failure is null ? exception : new AggregateException(failure, exception);
        }
        if (failure is not null)
        {
            throw failure;
        }
    }

    private Exception? FreeFonts()
    {
        Exception? failure = null;
        foreach (FontState font in _fonts.Reverse())
        {
            if (font.Handle != 0 && NativeBassMidi.FontFree(font.Handle) == 0)
            {
                Exception next = new MidoraAudioException(
                    $"BASS_MIDI_FontFree failed with BASS error {NativeBass.ErrorGetCode()}.");
                failure = failure is null ? next : new AggregateException(failure, next);
            }
            font.Handle = 0;
        }
        return failure;
    }

    private static void Throw(string operation, int? error = null) =>
        throw new MidoraAudioException(
            $"{operation} failed with BASS error {error ?? NativeBass.ErrorGetCode()}.");

    private sealed class FontState(uint handle)
    {
        public uint Handle { get; set; } = handle;
        public HashSet<int> LoadedPresetKeys { get; } = [];
        public bool AllPresetsLoaded { get; set; }
    }
}
