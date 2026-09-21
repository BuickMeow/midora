using Midora.Audio.Bass.Internals;
using NativeBass = Midora.NativeInterops.Bass.BASS;
using NativeBassMidi = Midora.NativeInterops.BassMidi.BASSMIDI;

namespace Midora.Audio.Bass;

internal sealed unsafe class PersistentBassMidiSoundFont : IDisposable
{
    private readonly BassNativeRuntime.Lease _runtimeLease;
    private readonly SoundFontConfiguration[] _configurations;
    private readonly IReadOnlyList<SoundFontConfiguration> _readOnlyConfigurations;
    private readonly FontState[] _fonts;
    private bool _disposed;

    public PersistentBassMidiSoundFont(string soundFontPath)
        : this([new SoundFontConfiguration(soundFontPath, null)])
    {
    }

    public PersistentBassMidiSoundFont(IReadOnlyList<string> soundFontPaths)
        : this(soundFontPaths
            .Select(path => new SoundFontConfiguration(path, null))
            .ToArray())
    {
    }

    public PersistentBassMidiSoundFont(
        IReadOnlyList<SoundFontConfiguration> soundFonts)
    {
        ArgumentNullException.ThrowIfNull(soundFonts);
        if (soundFonts.Count == 0)
        {
            throw new ArgumentException("At least one enabled SoundFont is required.", nameof(soundFonts));
        }

        _configurations = soundFonts.Select(value => value.Normalize()).ToArray();
        _readOnlyConfigurations = Array.AsReadOnly(_configurations);
        foreach (SoundFontConfiguration configuration in _configurations)
        {
            string path = configuration.Path;
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("An enabled application SoundFont does not exist.", path);
            }
        }

        _runtimeLease = BassNativeRuntime.Acquire();
        _fonts = new FontState[_configurations.Length];
        try
        {
            for (int index = 0; index < _configurations.Length; index++)
            {
                SoundFontConfiguration configuration = _configurations[index];
                _fonts[index] = new FontState(BassMidiFontPath.Init(
                    configuration.Path,
                    memoryMap: !configuration.IsSfz));
                if (_fonts[index].Handle == 0)
                {
                    Throw("BASS_MIDI_FontInit");
                }
                SoundFontTarget? target = configuration.Target;
                EnsurePreset(
                    _fonts[index],
                    configuration.IsSfz ? 0 : target?.Program ?? 0,
                    configuration.IsSfz ? 0 : target?.BankMsb ?? 0);
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

    public IReadOnlyList<SoundFontConfiguration> Configurations
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _readOnlyConfigurations;
        }
    }

    public void ApplyToStream(uint streamHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (streamHandle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(streamHandle));
        }
        NativeBassMidi.BASS_MIDI_FONTEX2* fonts = stackalloc NativeBassMidi.BASS_MIDI_FONTEX2[
            _fonts.Length];
        for (int index = 0; index < _fonts.Length; index++)
        {
            SoundFontConfiguration configuration = _configurations[index];
            fonts[index] = BassMidiSoundFontMapping.Create(
                _fonts[index].Handle,
                configuration);
        }
        uint count = BassMidiSoundFontMapping.AddExtendedConfigurationFlag(_fonts.Length);
        if (NativeBassMidi.StreamSetFonts(streamHandle, fonts, count) == 0)
        {
            Throw("BASS_MIDI_StreamSetFonts");
        }
    }

    public void EnsureReferencedPresets(MidiRenderPlan plan)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (int key in BassMidiRenderer.CollectReferencedPresetKeys(plan))
        {
            for (int index = 0; index < _fonts.Length; index++)
            {
                if (_configurations[index].Target is not null)
                {
                    continue;
                }
                EnsurePreset(_fonts[index], key & 127, key >> 7);
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
