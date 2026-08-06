using NativeBass = Midora.NativeInterops.Bass.BASS;
using NativeBassMidi = Midora.NativeInterops.BassMidi.BASSMIDI;

namespace Midora.Audio.Bass.Internals;

internal static unsafe class BassNativeRuntime
{
    internal const uint SupportedBassVersion = 0x02041203;
    internal const uint SupportedBassMidiVersion = 0x02041000;
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
                ValidateExactVersion("BASS", NativeBass.GetVersion(), SupportedBassVersion);
                ValidateExactVersion("BASSMIDI", NativeBassMidi.GetVersion(), SupportedBassMidiVersion);
                EnsureUtf8DeviceInformation();

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

    internal static void ValidateUtf8DeviceInformationMode(uint configuredValue)
    {
        if (configuredValue is 0 or uint.MaxValue)
        {
            throw new MidoraAudioException(
                "BASS UTF-8 device information mode is required before process-wide initialization.");
        }
    }

    private static void EnsureUtf8DeviceInformation()
    {
        uint configured = NativeBass.GetConfig(NativeBass.BASS_CONFIG_UNICODE);
        if (configured == uint.MaxValue)
        {
            int error = NativeBass.ErrorGetCode();
            throw new MidoraAudioException(
                $"BASS_GetConfig(BASS_CONFIG_UNICODE) failed with error {error}.");
        }
        if (configured == 0)
        {
            if (NativeBass.SetConfig(NativeBass.BASS_CONFIG_UNICODE, 1) == 0)
            {
                int error = NativeBass.ErrorGetCode();
                throw new MidoraAudioException(
                    $"BASS_SetConfig(BASS_CONFIG_UNICODE) failed with error {error}.");
            }
            configured = NativeBass.GetConfig(NativeBass.BASS_CONFIG_UNICODE);
            if (configured == uint.MaxValue)
            {
                int error = NativeBass.ErrorGetCode();
                throw new MidoraAudioException(
                    $"BASS_GetConfig(BASS_CONFIG_UNICODE) failed with error {error}.");
            }
        }
        ValidateUtf8DeviceInformationMode(configured);
    }

    internal static void ValidateExactVersion(string component, uint actualVersion, uint expectedVersion)
    {
        if (actualVersion != expectedVersion)
        {
            throw new MidoraAudioException(
                $"Unsupported {component} version 0x{actualVersion:x8}; expected pinned version 0x{expectedVersion:x8}.");
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
