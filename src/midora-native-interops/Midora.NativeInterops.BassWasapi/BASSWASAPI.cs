#pragma warning disable CA1401

using System.Runtime.InteropServices;

namespace Midora.NativeInterops.BassWasapi;

public static unsafe partial class BASSWASAPI
{
    public const string LibraryName = "basswasapi";

    [LibraryImport(LibraryName, EntryPoint = "BASS_WASAPI_GetVersion")]
    public static partial uint GetVersion();

    [LibraryImport(LibraryName, EntryPoint = "BASS_WASAPI_SetNotify")]
    public static partial int SetNotify(
        delegate* unmanaged<uint, uint, void*, void> proc,
        void* user
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_WASAPI_GetDeviceInfo")]
    public static partial int GetDeviceInfo(
        uint device,
        BASS_WASAPI_DEVICEINFO* info
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_WASAPI_GetDeviceLevel")]
    public static partial float GetDeviceLevel(
        uint device,
        int chan
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_WASAPI_SetDevice")]
    public static partial int SetDevice(
        uint device
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_WASAPI_GetDevice")]
    public static partial uint GetDevice();

    [LibraryImport(LibraryName, EntryPoint = "BASS_WASAPI_CheckFormat")]
    public static partial uint CheckFormat(
        uint device,
        uint freq,
        uint chans,
        uint flags
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_WASAPI_Init")]
    public static partial int Init(
        int device,
        uint freq,
        uint chans,
        uint flags,
        float buffer,
        float period,
        delegate* unmanaged<void*, uint, void*, uint> proc,
        void* user
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_WASAPI_Free")]
    public static partial int Free();

    [LibraryImport(LibraryName, EntryPoint = "BASS_WASAPI_GetInfo")]
    public static partial int GetInfo(
        BASS_WASAPI_INFO* info
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_WASAPI_GetCPU")]
    public static partial float GetCPU();

    [LibraryImport(LibraryName, EntryPoint = "BASS_WASAPI_Lock")]
    public static partial int Lock(
        int @lock
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_WASAPI_Start")]
    public static partial int Start();

    [LibraryImport(LibraryName, EntryPoint = "BASS_WASAPI_Stop")]
    public static partial int Stop(
        int reset
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_WASAPI_IsStarted")]
    public static partial int IsStarted();

    [LibraryImport(LibraryName, EntryPoint = "BASS_WASAPI_SetVolume")]
    public static partial int SetVolume(
        uint mode,
        float volume
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_WASAPI_GetVolume")]
    public static partial float GetVolume(
        uint mode
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_WASAPI_SetMute")]
    public static partial int SetMute(
        uint mode,
        int mute
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_WASAPI_GetMute")]
    public static partial int GetMute(
        uint mode
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_WASAPI_PutData")]
    public static partial uint PutData(
        void* buffer,
        uint length
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_WASAPI_GetData")]
    public static partial uint GetData(
        void* buffer,
        uint length
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_WASAPI_GetLevel")]
    public static partial uint GetLevel();

    [LibraryImport(LibraryName, EntryPoint = "BASS_WASAPI_GetLevelEx")]
    public static partial int GetLevelEx(
        float* levels,
        float length,
        uint flags
    );

    [StructLayout(LayoutKind.Sequential)]
    public struct BASS_WASAPI_DEVICEINFO
    {
        public byte* name;
        public byte* id;
        public uint type;
        public uint flags;
        public float minperiod;
        public float defperiod;
        public uint mixfreq;
        public uint mixchans;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BASS_WASAPI_INFO
    {
        public uint initflags;
        public uint freq;
        public uint chans;
        public uint format;
        public uint buflen;
        public float volmax;
        public float volmin;
        public float volstep;
    }

    public const int BASS_ERROR_WASAPI = 5000;
    public const int BASS_ERROR_WASAPI_BUFFER = 5001;
    public const int BASS_ERROR_WASAPI_CATEGORY = 5002;
    public const int BASS_ERROR_WASAPI_DENIED = 5003;
    public const uint BASS_WASAPI_TYPE_NETWORKDEVICE = 0;
    public const uint BASS_WASAPI_TYPE_SPEAKERS = 1;
    public const uint BASS_WASAPI_TYPE_LINELEVEL = 2;
    public const uint BASS_WASAPI_TYPE_HEADPHONES = 3;
    public const uint BASS_WASAPI_TYPE_MICROPHONE = 4;
    public const uint BASS_WASAPI_TYPE_HEADSET = 5;
    public const uint BASS_WASAPI_TYPE_HANDSET = 6;
    public const uint BASS_WASAPI_TYPE_DIGITAL = 7;
    public const uint BASS_WASAPI_TYPE_SPDIF = 8;
    public const uint BASS_WASAPI_TYPE_HDMI = 9;
    public const uint BASS_WASAPI_TYPE_UNKNOWN = 10;
    public const uint BASS_DEVICE_ENABLED = 1;
    public const uint BASS_DEVICE_DEFAULT = 2;
    public const uint BASS_DEVICE_INIT = 4;
    public const uint BASS_DEVICE_LOOPBACK = 8;
    public const uint BASS_DEVICE_INPUT = 16;
    public const uint BASS_DEVICE_UNPLUGGED = 32;
    public const uint BASS_DEVICE_DISABLED = 64;
    public const uint BASS_WASAPI_EXCLUSIVE = 1;
    public const uint BASS_WASAPI_AUTOFORMAT = 2;
    public const uint BASS_WASAPI_BUFFER = 4;
    public const uint BASS_WASAPI_EVENT = 16;
    public const uint BASS_WASAPI_SAMPLES = 32;
    public const uint BASS_WASAPI_DITHER = 64;
    public const uint BASS_WASAPI_RAW = 128;
    public const uint BASS_WASAPI_ASYNC = 0x100;
    public const uint BASS_WASAPI_CATEGORY_MASK = 0xf000;
    public const uint BASS_WASAPI_CATEGORY_OTHER = 0x0000;
    public const uint BASS_WASAPI_CATEGORY_FOREGROUNDONLYMEDIA = 0x1000;
    public const uint BASS_WASAPI_CATEGORY_BACKGROUNDCAPABLEMEDIA = 0x2000;
    public const uint BASS_WASAPI_CATEGORY_COMMUNICATIONS = 0x3000;
    public const uint BASS_WASAPI_CATEGORY_ALERTS = 0x4000;
    public const uint BASS_WASAPI_CATEGORY_SOUNDEFFECTS = 0x5000;
    public const uint BASS_WASAPI_CATEGORY_GAMEEFFECTS = 0x6000;
    public const uint BASS_WASAPI_CATEGORY_GAMEMEDIA = 0x7000;
    public const uint BASS_WASAPI_CATEGORY_GAMECHAT = 0x8000;
    public const uint BASS_WASAPI_CATEGORY_SPEECH = 0x9000;
    public const uint BASS_WASAPI_CATEGORY_MOVIE = 0xa000;
    public const uint BASS_WASAPI_CATEGORY_MEDIA = 0xb000;
    public const uint BASS_WASAPI_FORMAT_FLOAT = 0;
    public const uint BASS_WASAPI_FORMAT_8BIT = 1;
    public const uint BASS_WASAPI_FORMAT_16BIT = 2;
    public const uint BASS_WASAPI_FORMAT_24BIT = 3;
    public const uint BASS_WASAPI_FORMAT_32BIT = 4;
    public const uint BASS_WASAPI_CURVE_DB = 0;
    public const uint BASS_WASAPI_CURVE_LINEAR = 1;
    public const uint BASS_WASAPI_CURVE_WINDOWS = 2;
    public const uint BASS_WASAPI_VOL_SESSION = 8;
    public static readonly delegate* unmanaged<void*, uint, void*, uint> WASAPIPROC_PUSH = null;
    public static readonly delegate* unmanaged<void*, uint, void*, uint> WASAPIPROC_BASS = (delegate* unmanaged<void*, uint, void*, uint>)(void*)(nint)(-1);
    public const uint BASS_WASAPI_NOTIFY_ENABLED = 0;
    public const uint BASS_WASAPI_NOTIFY_DISABLED = 1;
    public const uint BASS_WASAPI_NOTIFY_DEFOUTPUT = 2;
    public const uint BASS_WASAPI_NOTIFY_DEFINPUT = 3;
    public const uint BASS_WASAPI_NOTIFY_FAIL = 0x100;
}
