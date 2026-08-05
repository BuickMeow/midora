using NativeBass = Midora.NativeInterops.Bass.BASS;
using NativeBassMidi = Midora.NativeInterops.BassMidi.BASSMIDI;

namespace Midora.Audio.Bass.Internals;

internal static unsafe class BassNativeRuntime
{
    private const uint SupportedBassApiVersion = 0x0204;
    private static readonly object Gate = new();
    private static int _referenceCount;
    private static MidoraAudioException? _terminalFailure;

    public static Lease Acquire()
    {
        lock (Gate)
        {
            if (_terminalFailure is not null)
            {
                throw new MidoraAudioException(
                    "The process-wide BASS runtime is faulted after a failed cleanup and cannot be reused.",
                    _terminalFailure);
            }

            if (_referenceCount == 0)
            {
                ValidateApiVersion("BASS", NativeBass.GetVersion());
                ValidateApiVersion("BASSMIDI", NativeBassMidi.GetVersion());

                if (NativeBass.Init(0, 48_000, 0, null, null) == 0)
                {
                    int error = NativeBass.ErrorGetCode();
                    throw new MidoraAudioException($"BASS_Init failed with error {error}.");
                }
            }

            _referenceCount++;
            return new Lease();
        }
    }

    private static void ValidateApiVersion(string component, uint version)
    {
        uint apiVersion = version >> 16;
        if (apiVersion != SupportedBassApiVersion)
        {
            throw new MidoraAudioException(
                $"Unsupported {component} API version 0x{apiVersion:x4}; expected 0x{SupportedBassApiVersion:x4}.");
        }
    }

    internal sealed class Lease : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            lock (Gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _referenceCount--;
                if (_referenceCount == 0)
                {
                    if (NativeBass.Free() == 0)
                    {
                        int error = NativeBass.ErrorGetCode();
                        _terminalFailure = new MidoraAudioException($"BASS_Free failed with error {error}.");
                        throw _terminalFailure;
                    }
                }
            }
        }
    }
}
