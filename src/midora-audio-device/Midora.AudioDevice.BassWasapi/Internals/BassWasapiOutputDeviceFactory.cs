using Midora.AudioDevice.BassWasapi.Settings;
using Midora.NativeInterops.BassWasapi;
using System.Runtime.InteropServices;

namespace Midora.AudioDevice.BassWasapi.Internals;

public sealed unsafe class BassWasapiOutputDeviceFactory : IAudioOutputDeviceFactory
{
    internal const uint SupportedVersion = 0x02040401;
    private static readonly object ConfigurationGate = new();
    private readonly BassWasapiAudioOutputDeviceSettings _settings;

    public BassWasapiOutputDeviceFactory(BassWasapiAudioOutputDeviceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        EnsureUtf8DeviceInformation();
        ValidateExactVersion(BASSWASAPI.GetVersion());

        _settings = settings;
    }

    public int LastEnumerationErrorCode { get; private set; }

    public uint ApiVersion => BASSWASAPI.GetVersion();

    internal static void ValidateExactVersion(uint actualVersion)
    {
        if (actualVersion != SupportedVersion)
        {
            throw new MidoraAudioDeviceException(
                $"Unsupported BASSWASAPI version 0x{actualVersion:x8}; expected pinned version 0x{SupportedVersion:x8}.");
        }
    }

    public IReadOnlyList<AudioOutputDeviceInfo> GetDevices()
    {
        List<AudioOutputDeviceInfo> result = [];
        HashSet<string> deviceIds = new(StringComparer.Ordinal);
        LastEnumerationErrorCode = 0;

        BASSWASAPI.BASS_WASAPI_DEVICEINFO currentDeviceInfo;

        for (uint i = 0; 0 != BASSWASAPI.GetDeviceInfo(i, &currentDeviceInfo); i++)
        {
            if (!IsEligibleOutputDevice(currentDeviceInfo.flags))
            {
                continue;
            }

            string id = Marshal.PtrToStringUTF8((nint)currentDeviceInfo.id)
                ?? throw new MidoraAudioDeviceException(
                    "BASS_WASAPI_GetDeviceInfo returned an enabled output device without an ID.");
            if (id.Length == 0 || !deviceIds.Add(id))
            {
                throw new MidoraAudioDeviceException(
                    $"BASS_WASAPI_GetDeviceInfo returned an empty or duplicate output device ID [{id}].");
            }
            if (currentDeviceInfo.mixfreq is 0 or > int.MaxValue)
            {
                throw new MidoraAudioDeviceException(
                    $"The enabled output device [{id}] reported an invalid mix sample rate {currentDeviceInfo.mixfreq}.");
            }
            int sampleRate = (int)currentDeviceInfo.mixfreq;

            result.Add(new AudioOutputDeviceInfo(
                id,
                Marshal.PtrToStringUTF8((nint)currentDeviceInfo.name),
                new(sampleRate, 2, AudioSampleFormat.Float32),
                (currentDeviceInfo.flags & BASSWASAPI.BASS_DEVICE_DEFAULT) != 0
            ));
        }

        LastEnumerationErrorCode = Midora.NativeInterops.Bass.BASS.ErrorGetCode();
        ValidateEnumerationTerminalError(LastEnumerationErrorCode);

        return result;
    }

    public IAudioOutputDevice Open(AudioOutputDeviceInfo deviceInfo, IAudioRenderSource audioRenderSource)
    {
        return new BassWasapiOutputDevice(deviceInfo, _settings, audioRenderSource);
    }

    internal static bool IsEligibleOutputDevice(uint flags)
    {
        const uint rejectedFlags = BASSWASAPI.BASS_DEVICE_INPUT
            | BASSWASAPI.BASS_DEVICE_LOOPBACK
            | BASSWASAPI.BASS_DEVICE_UNPLUGGED
            | BASSWASAPI.BASS_DEVICE_DISABLED;
        return (flags & BASSWASAPI.BASS_DEVICE_ENABLED) != 0
            && (flags & rejectedFlags) == 0;
    }

    internal static void ValidateEnumerationTerminalError(int errorCode)
    {
        if (errorCode != Midora.NativeInterops.Bass.BASS.BASS_ERROR_DEVICE)
        {
            throw new MidoraAudioDeviceException(
                $"BASS_WASAPI_GetDeviceInfo failed with BASS error {errorCode} before enumeration completed.");
        }
    }

    internal static void ValidateUtf8DeviceInformationMode(uint configuredValue)
    {
        if (configuredValue is 0 or uint.MaxValue)
        {
            throw new MidoraAudioDeviceException(
                "BASS UTF-8 device information mode is required before WASAPI enumeration.");
        }
    }

    private static void EnsureUtf8DeviceInformation()
    {
        lock (ConfigurationGate)
        {
            uint configured = Midora.NativeInterops.Bass.BASS.GetConfig(
                Midora.NativeInterops.Bass.BASS.BASS_CONFIG_UNICODE);
            if (configured == uint.MaxValue)
            {
                int error = Midora.NativeInterops.Bass.BASS.ErrorGetCode();
                throw new MidoraAudioDeviceException(
                    $"BASS_GetConfig(BASS_CONFIG_UNICODE) failed with BASS error {error}.");
            }
            if (configured == 0)
            {
                if (Midora.NativeInterops.Bass.BASS.SetConfig(
                    Midora.NativeInterops.Bass.BASS.BASS_CONFIG_UNICODE,
                    1) == 0)
                {
                    int error = Midora.NativeInterops.Bass.BASS.ErrorGetCode();
                    throw new MidoraAudioDeviceException(
                        $"BASS_SetConfig(BASS_CONFIG_UNICODE) failed with BASS error {error}.");
                }
                configured = Midora.NativeInterops.Bass.BASS.GetConfig(
                    Midora.NativeInterops.Bass.BASS.BASS_CONFIG_UNICODE);
                if (configured == uint.MaxValue)
                {
                    int error = Midora.NativeInterops.Bass.BASS.ErrorGetCode();
                    throw new MidoraAudioDeviceException(
                        $"BASS_GetConfig(BASS_CONFIG_UNICODE) failed with BASS error {error}.");
                }
            }
            ValidateUtf8DeviceInformationMode(configured);
        }
    }
}
