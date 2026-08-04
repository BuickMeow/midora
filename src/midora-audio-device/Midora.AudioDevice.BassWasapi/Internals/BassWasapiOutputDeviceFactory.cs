using Midora.AudioDevice.BassWasapi.Settings;
using Midora.NativeInterops.BassWasapi;
using System.Runtime.InteropServices;
using System.Text;

namespace Midora.AudioDevice.BassWasapi.Internals;

internal sealed unsafe class BassWasapiOutputDeviceFactory(
    BassWasapiAudioOutputDeviceSettings settings
) : IAudioOutputDeviceFactory
{
    public IReadOnlyList<AudioOutputDeviceInfo> GetDevices()
    {
        List<AudioOutputDeviceInfo> result = [];

        BASSWASAPI.BASS_WASAPI_DEVICEINFO currentDeviceInfo;

        for (uint i = 0; 0 != BASSWASAPI.GetDeviceInfo(i, &currentDeviceInfo); i++)
        {
            if ((currentDeviceInfo.flags & BASSWASAPI.BASS_DEVICE_INPUT) != 0)
            {
                continue;
            }

            if ((currentDeviceInfo.flags & BASSWASAPI.BASS_DEVICE_ENABLED) == 0)
            {
                continue;
            }

            result.Add(new AudioOutputDeviceInfo(
                Marshal.PtrToStringAnsi((nint)currentDeviceInfo.id) ?? string.Empty,
                Marshal.PtrToStringAnsi((nint)currentDeviceInfo.name),
                new((int)currentDeviceInfo.mixfreq, (int)currentDeviceInfo.mixchans, AudioSampleFormat.Float32)
            ));
        }

        return result;
    }

    public IAudioOutputDevice Open(AudioOutputDeviceInfo deviceInfo, IAudioRenderSource audioRenderSource)
    {
        return new BassWasapiOutputDevice(deviceInfo, settings, audioRenderSource);
    }
}
