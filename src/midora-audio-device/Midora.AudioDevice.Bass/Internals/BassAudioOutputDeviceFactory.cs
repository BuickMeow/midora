using System.Globalization;
using System.Runtime.InteropServices;
using Midora.AudioDevice.Bass.Settings;
using Midora.NativeInterops.Bass;
using NativeBass = Midora.NativeInterops.Bass.BASS;

namespace Midora.AudioDevice.Bass.Internals;

/// <summary>
/// macOS-first BASS native output device enumeration. Device 0 ("No sound") is excluded,
/// and only enabled devices are reported. Enumeration reads each device's actual sample rate
/// by a scoped BASS_Init/BASS_Free, so it must run before realtime playback initializes BASS.
/// </summary>
public sealed unsafe class BassAudioOutputDeviceFactory : IAudioOutputDeviceFactory
{
    internal const uint SupportedBassVersion = 0x02041203;
    private const uint FirstPhysicalDevice = 1;

    private readonly BassAudioOutputDeviceSettings _settings;

    public BassAudioOutputDeviceFactory(BassAudioOutputDeviceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        BassNativeLibrary.EnsureRegistered();
        ValidateExactVersion(NativeBass.GetVersion());
        _settings = settings;
    }

    public int LastEnumerationErrorCode { get; private set; }

    public int LastUnreadableFormatDeviceCount { get; private set; }

    public uint ApiVersion => NativeBass.GetVersion();

    internal static void ValidateExactVersion(uint actualVersion)
    {
        if (actualVersion != SupportedBassVersion)
        {
            throw new MidoraAudioDeviceException(
                $"Unsupported BASS version 0x{actualVersion:x8}; expected pinned version 0x{SupportedBassVersion:x8}.");
        }
    }

    public IReadOnlyList<AudioOutputDeviceInfo> GetDevices()
    {
        List<AudioOutputDeviceInfo> result = [];
        HashSet<string> deviceIds = new(StringComparer.Ordinal);
        LastEnumerationErrorCode = 0;
        LastUnreadableFormatDeviceCount = 0;

        for (uint index = FirstPhysicalDevice; ; index++)
        {
            NativeBass.BASS_DEVICEINFO deviceInfo;
            if (NativeBass.GetDeviceInfo(index, &deviceInfo) == 0)
            {
                break;
            }

            if ((deviceInfo.flags & NativeBass.BASS_DEVICE_ENABLED) == 0)
            {
                continue;
            }

            string id = index.ToString(CultureInfo.InvariantCulture);
            if (!deviceIds.Add(id))
            {
                throw new MidoraAudioDeviceException(
                    $"BASS_GetDeviceInfo returned a duplicate output device index [{id}].");
            }

            string name = Marshal.PtrToStringUTF8((nint)deviceInfo.name) ?? $"Output Device {id}";
            if (TryReadDeviceSampleRate(index) is not int sampleRate)
            {
                // Never invent a sample rate: an enabled device whose actual rate cannot be read
                // is reported as unreadable and left out of the output list.
                LastUnreadableFormatDeviceCount++;
                continue;
            }

            result.Add(new AudioOutputDeviceInfo(
                id,
                name,
                new(sampleRate, BassAudioInitializationPolicy.RequestedChannelCount, AudioSampleFormat.Float32),
                (deviceInfo.flags & NativeBass.BASS_DEVICE_DEFAULT) != 0));
        }

        LastEnumerationErrorCode = NativeBass.ErrorGetCode();
        if (LastEnumerationErrorCode != NativeBass.BASS_ERROR_DEVICE)
        {
            throw new MidoraAudioDeviceException(
                $"BASS_GetDeviceInfo failed with BASS error {LastEnumerationErrorCode} before enumeration completed.");
        }

        return result;
    }

    public IAudioOutputDevice Open(AudioOutputDeviceInfo deviceInfo, IAudioRenderSource audioRenderSource)
    {
        ArgumentNullException.ThrowIfNull(deviceInfo);
        ArgumentNullException.ThrowIfNull(audioRenderSource);
        return new BassAudioOutputDevice(deviceInfo, _settings, audioRenderSource);
    }

    internal static int? TryReadDeviceSampleRate(uint index)
    {
        if (NativeBass.Init((int)index, BassAudioInitializationPolicy.RequestedFrequency,
                BassAudioInitializationPolicy.InitializationFlags, null, null) == 0)
        {
            return null;
        }

        try
        {
            NativeBass.BASS_INFO info;
            if (NativeBass.GetInfo(&info) == 0 || info.freq is 0 or > int.MaxValue)
            {
                return null;
            }

            return (int)info.freq;
        }
        finally
        {
            _ = NativeBass.Free();
        }
    }
}
