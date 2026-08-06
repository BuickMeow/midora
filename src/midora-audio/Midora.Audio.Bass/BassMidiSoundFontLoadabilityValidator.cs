using System.Runtime.ExceptionServices;
using Midora.Audio.Bass.Internals;
using NativeBass = Midora.NativeInterops.Bass.BASS;
using NativeBassMidi = Midora.NativeInterops.BassMidi.BASSMIDI;

namespace Midora.Audio.Bass;

public sealed unsafe class BassMidiSoundFontLoadabilityValidator
    : ISoundFontLoadabilityValidator
{
    public ValueTask ValidateAsync(
        string soundFontPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(soundFontPath);
        string path = Path.GetFullPath(soundFontPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
        {
            throw new SoundFontLoadabilityException(
                SoundFontLoadabilityFailure.Missing,
                "The selected SoundFont does not exist.");
        }

        try
        {
            using FileStream readable = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new SoundFontLoadabilityException(
                SoundFontLoadabilityFailure.Unreadable,
                "The selected SoundFont cannot be read.",
                innerException: exception);
        }

        BassNativeRuntime.Lease? runtimeLease = null;
        uint fontHandle = 0;
        Exception? failure = null;
        try
        {
            runtimeLease = BassNativeRuntime.Acquire();
            fixed (char* nativePath = path)
            {
                fontHandle = NativeBassMidi.FontInit(
                    nativePath,
                    NativeBass.BASS_UNICODE | NativeBassMidi.BASS_MIDI_FONT_MMAP);
            }
            if (fontHandle == 0)
            {
                ThrowLoadFailure("BASS_MIDI_FontInit", NativeBass.ErrorGetCode());
            }
            if (NativeBassMidi.FontLoad(fontHandle, -1, -1) == 0)
            {
                ThrowLoadFailure("BASS_MIDI_FontLoad", NativeBass.ErrorGetCode());
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        if (fontHandle != 0 && NativeBassMidi.FontFree(fontHandle) == 0)
        {
            int error = NativeBass.ErrorGetCode();
            failure = Combine(
                failure,
                new MidoraAudioException(
                    $"BASS_MIDI_FontFree failed after SoundFont validation with error {error}."));
        }
        if (runtimeLease is not null)
        {
            try
            {
                runtimeLease.Dispose();
            }
            catch (Exception exception)
            {
                failure = Combine(failure, exception);
            }
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        return ValueTask.CompletedTask;
    }

    private static void ThrowLoadFailure(string operation, int error)
    {
        if (error == NativeBass.BASS_ERROR_FILEOPEN)
        {
            throw new SoundFontLoadabilityException(
                SoundFontLoadabilityFailure.Unreadable,
                $"{operation} could not read the selected SoundFont.",
                error);
        }
        if (error is NativeBass.BASS_ERROR_FILEFORM or NativeBass.BASS_ERROR_NOTAVAIL)
        {
            throw new SoundFontLoadabilityException(
                SoundFontLoadabilityFailure.UnsupportedOrCorrupt,
                $"{operation} rejected the selected file as an unsupported or corrupt SoundFont.",
                error);
        }
        throw new MidoraAudioException($"{operation} failed with BASS error {error}.");
    }

    private static Exception Combine(Exception? first, Exception second) =>
        first is null
            ? second
            : new AggregateException(
                "SoundFont validation and native cleanup both failed.",
                first,
                second);
}
