using Midora.AudioDevice.BassWasapi.Settings;
using Midora.NativeInterops.BassWasapi;
using System.Runtime.InteropServices;

namespace Midora.AudioDevice.BassWasapi.Internals;

public sealed unsafe class BassWasapiOutputDeviceFactory : IAudioOutputDeviceFactory
{
    internal const uint SupportedVersion = 0x02040401;
    private readonly BassWasapiAudioOutputDeviceSettings _settings;

    public BassWasapiOutputDeviceFactory(BassWasapiAudioOutputDeviceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
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
        LastEnumerationErrorCode = 0;

        BASSWASAPI.BASS_WASAPI_DEVICEINFO currentDeviceInfo;

        for (uint i = 0; 0 != BASSWASAPI.GetDeviceInfo(i, &currentDeviceInfo); i++)
        {
            const uint rejectedFlags = BASSWASAPI.BASS_DEVICE_INPUT
                | BASSWASAPI.BASS_DEVICE_LOOPBACK
                | BASSWASAPI.BASS_DEVICE_UNPLUGGED
                | BASSWASAPI.BASS_DEVICE_DISABLED;

            if ((currentDeviceInfo.flags & rejectedFlags) != 0)
            {
                continue;
            }

            if ((currentDeviceInfo.flags & BASSWASAPI.BASS_DEVICE_ENABLED) == 0)
            {
                continue;
            }

            result.Add(new AudioOutputDeviceInfo(
                Marshal.PtrToStringUTF8((nint)currentDeviceInfo.id) ?? string.Empty,
                Marshal.PtrToStringUTF8((nint)currentDeviceInfo.name),
                new((int)currentDeviceInfo.mixfreq, 2, AudioSampleFormat.Float32),
                (currentDeviceInfo.flags & BASSWASAPI.BASS_DEVICE_DEFAULT) != 0
            ));
        }

        LastEnumerationErrorCode = Midora.NativeInterops.Bass.BASS.ErrorGetCode();

        return result;
    }

    public IAudioOutputDevice Open(AudioOutputDeviceInfo deviceInfo, IAudioRenderSource audioRenderSource)
    {
        return new BassWasapiOutputDevice(deviceInfo, _settings, audioRenderSource);
    }
}
