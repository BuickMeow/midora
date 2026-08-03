using Midora.AudioDevice.BassWasapi.Settings;
using Midora.NativeInterops.Bass;
using Midora.NativeInterops.BassWasapi;
using System.Runtime;
using System.Runtime.InteropServices;

namespace Midora.AudioDevice.BassWasapi.Internals;

internal sealed unsafe class BassWasapiOutputDevice : IAudioOutputDevice
{
    private readonly AudioOutputDeviceInfo _info;

    private readonly BassWasapiAudioOutputDeviceSettings _settings;

    private readonly IAudioRenderSource _audioRenderSource;

    public BassWasapiOutputDevice(AudioOutputDeviceInfo info, BassWasapiAudioOutputDeviceSettings settings, IAudioRenderSource audioRenderSource)
    {
        _info = info;
        _settings = settings;
        _audioRenderSource = audioRenderSource;
        Initialize();
    }

    public AudioOutputDeviceInfo Info => _info;

    public void Start()
    {
        FindDeviceOrThrow(Info.Id, out _, out int index);
        if (0 == BASSWASAPI.SetDevice((uint)index))
        {
            throw new BassException();
        }
        if (0 == BASSWASAPI.Start())
        {
            throw new BassException();
        }
    }

    public void Stop(bool flush)
    {
        FindDeviceOrThrow(Info.Id, out _, out int index);
        if (0 == BASSWASAPI.SetDevice((uint)index))
        {
            throw new BassException();
        }
        if (0 == BASSWASAPI.Stop(flush ? 1 : 0))
        {
            throw new BassException();
        }
    }

    public int Write(void* data, int length)
    {
        throw new NotImplementedException();
    }

    public void Dispose()
    {
        Stop(flush: true);
    }

    private void Initialize()
    {
        FindDeviceOrThrow(_info.Id, out BASSWASAPI.BASS_WASAPI_DEVICEINFO bassDeviceInfo, out int index);

        if (0 == BASSWASAPI.Init(
            device: index,
            freq: (uint)_info.AudioFormat.SampleRate,
            chans: (uint)_info.AudioFormat.ChannelCount,
            flags: BASSWASAPI.BASS_WASAPI_EVENT | BASSWASAPI.BASS_WASAPI_SAMPLES,
            buffer: _settings.DeviceBufferSamples,
            period: 0,
            proc: &BassWasapiProc,
            user: (void*)GCHandle.ToIntPtr(GCHandle.Alloc(_audioRenderSource, GCHandleType.Normal))
        ))
        {
            throw new BassException();
        }

        if (0 == BASSWASAPI.SetDevice((uint)index))
        {
            throw new BassException();
        }
    }

    private static void FindDeviceOrThrow(
        string id,
        out BASSWASAPI.BASS_WASAPI_DEVICEINFO bassDeviceInfo,
        out int index
    )
    {
        BASSWASAPI.BASS_WASAPI_DEVICEINFO currentDeviceInfo;

        for (uint i = 0; 0 != BASSWASAPI.GetDeviceInfo(i, &currentDeviceInfo); i++)
        {
            if (Marshal.PtrToStringAnsi((nint)currentDeviceInfo.id) == id)
            {
                bassDeviceInfo = currentDeviceInfo;
                index = (int)i;
                return;
            }
        }

        throw new MidoraAudioDeviceException($"Could not find the device [{id}]");
    }

    [UnmanagedCallersOnly]
    private static uint BassWasapiProc(void* buffer, uint length, void* user)
    {
        if (user == null)
        {
            return 0;
        }

        GCHandle handle = GCHandle.FromIntPtr((nint)user);
        if (handle.Target is not IAudioRenderSource source)
        {
            return 0;
        }

        return (uint)source.Render(buffer, (int)length);
    }
}
