#pragma warning disable CA1401

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Midora.NativeInterops.Bass;

public static unsafe partial class BASS
{
    public const string LibraryName = "bass";

    [LibraryImport(LibraryName, EntryPoint = "BASS_SetConfig")]
    public static partial int SetConfig(
        uint option,
        uint @value
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_GetConfig")]
    public static partial uint GetConfig(
        uint option
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_SetConfigPtr")]
    public static partial int SetConfigPtr(
        uint option,
        void* @value
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_GetConfigPtr")]
    public static partial void* GetConfigPtr(
        uint option
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_GetVersion")]
    public static partial uint GetVersion();

    [LibraryImport(LibraryName, EntryPoint = "BASS_ErrorGetCode")]
    public static partial int ErrorGetCode();

    [LibraryImport(LibraryName, EntryPoint = "BASS_GetDeviceInfo")]
    public static partial int GetDeviceInfo(
        uint device,
        BASS_DEVICEINFO* info
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_Init")]
    public static partial int Init(
        int device,
        uint freq,
        uint flags,
        void* win,
        void* dsguid
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_Free")]
    public static partial int Free();

    [LibraryImport(LibraryName, EntryPoint = "BASS_SetDevice")]
    public static partial int SetDevice(
        uint device
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_GetDevice")]
    public static partial uint GetDevice();

    [LibraryImport(LibraryName, EntryPoint = "BASS_GetInfo")]
    public static partial int GetInfo(
        BASS_INFO* info
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_Start")]
    public static partial int Start();

    [LibraryImport(LibraryName, EntryPoint = "BASS_Stop")]
    public static partial int Stop();

    [LibraryImport(LibraryName, EntryPoint = "BASS_Pause")]
    public static partial int Pause();

    [LibraryImport(LibraryName, EntryPoint = "BASS_IsStarted")]
    public static partial uint IsStarted();

    [LibraryImport(LibraryName, EntryPoint = "BASS_Update")]
    public static partial int Update(
        uint length
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_GetCPU")]
    public static partial float GetCPU();

    [LibraryImport(LibraryName, EntryPoint = "BASS_SetVolume")]
    public static partial int SetVolume(
        float volume
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_GetVolume")]
    public static partial float GetVolume();

    [LibraryImport(LibraryName, EntryPoint = "BASS_GetDSoundObject")]
    public static partial void* GetDSoundObject(
        uint @object
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_Set3DFactors")]
    public static partial int Set3DFactors(
        float distf,
        float rollf,
        float doppf
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_Get3DFactors")]
    public static partial int Get3DFactors(
        float* distf,
        float* rollf,
        float* doppf
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_Set3DPosition")]
    public static partial int Set3DPosition(
        BASS_3DVECTOR* pos,
        BASS_3DVECTOR* vel,
        BASS_3DVECTOR* front,
        BASS_3DVECTOR* top
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_Get3DPosition")]
    public static partial int Get3DPosition(
        BASS_3DVECTOR* pos,
        BASS_3DVECTOR* vel,
        BASS_3DVECTOR* front,
        BASS_3DVECTOR* top
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_Apply3D")]
    public static partial void Apply3D();

    [LibraryImport(LibraryName, EntryPoint = "BASS_PluginLoad")]
    public static partial uint PluginLoad(
        byte* @file,
        uint flags
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_PluginFree")]
    public static partial int PluginFree(
        uint handle
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_PluginEnable")]
    public static partial int PluginEnable(
        uint handle,
        int enable
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_PluginGetInfo")]
    public static partial BASS_PLUGININFO* PluginGetInfo(
        uint handle
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_SampleLoad")]
    public static partial uint SampleLoad(
        uint filetype,
        void* @file,
        ulong offset,
        uint length,
        uint max,
        uint flags
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_SampleCreate")]
    public static partial uint SampleCreate(
        uint length,
        uint freq,
        uint chans,
        uint max,
        uint flags
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_SampleFree")]
    public static partial int SampleFree(
        uint handle
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_SampleSetData")]
    public static partial int SampleSetData(
        uint handle,
        void* buffer
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_SampleGetData")]
    public static partial int SampleGetData(
        uint handle,
        void* buffer
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_SampleGetInfo")]
    public static partial int SampleGetInfo(
        uint handle,
        BASS_SAMPLE* info
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_SampleSetInfo")]
    public static partial int SampleSetInfo(
        uint handle,
        BASS_SAMPLE* info
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_SampleGetChannel")]
    public static partial uint SampleGetChannel(
        uint handle,
        uint flags
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_SampleGetChannels")]
    public static partial uint SampleGetChannels(
        uint handle,
        uint* channels
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_SampleStop")]
    public static partial int SampleStop(
        uint handle
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_StreamCreate")]
    public static partial uint StreamCreate(
        uint freq,
        uint chans,
        uint flags,
        delegate* unmanaged<uint, void*, uint, void*, uint> proc,
        void* user
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_StreamCreateFile")]
    public static partial uint StreamCreateFile(
        uint filetype,
        void* @file,
        ulong offset,
        ulong length,
        uint flags
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_StreamCreateURL")]
    public static partial uint StreamCreateURL(
        byte* url,
        uint offset,
        uint flags,
        delegate* unmanaged<void*, uint, void*, void> proc,
        void* user
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_StreamCreateFileUser")]
    public static partial uint StreamCreateFileUser(
        uint system,
        uint flags,
        BASS_FILEPROCS* proc,
        void* user
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_StreamCancel")]
    public static partial int StreamCancel(
        void* user
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_StreamFree")]
    public static partial int StreamFree(
        uint handle
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_StreamGetFilePosition")]
    public static partial ulong StreamGetFilePosition(
        uint handle,
        uint mode
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_StreamPutData")]
    public static partial uint StreamPutData(
        uint handle,
        void* buffer,
        uint length
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_StreamPutFileData")]
    public static partial uint StreamPutFileData(
        uint handle,
        void* buffer,
        uint length
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MusicLoad")]
    public static partial uint MusicLoad(
        uint filetype,
        void* @file,
        ulong offset,
        uint length,
        uint flags,
        uint freq
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MusicFree")]
    public static partial int MusicFree(
        uint handle
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_RecordGetDeviceInfo")]
    public static partial int RecordGetDeviceInfo(
        uint device,
        BASS_DEVICEINFO* info
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_RecordInit")]
    public static partial int RecordInit(
        int device
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_RecordFree")]
    public static partial int RecordFree();

    [LibraryImport(LibraryName, EntryPoint = "BASS_RecordSetDevice")]
    public static partial int RecordSetDevice(
        uint device
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_RecordGetDevice")]
    public static partial uint RecordGetDevice();

    [LibraryImport(LibraryName, EntryPoint = "BASS_RecordGetInfo")]
    public static partial int RecordGetInfo(
        BASS_RECORDINFO* info
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_RecordGetInputName")]
    public static partial byte* RecordGetInputName(
        int input
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_RecordSetInput")]
    public static partial int RecordSetInput(
        int input,
        uint flags,
        float volume
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_RecordGetInput")]
    public static partial uint RecordGetInput(
        int input,
        float* volume
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_RecordStart")]
    public static partial uint RecordStart(
        uint freq,
        uint chans,
        uint flags,
        delegate* unmanaged<uint, void*, uint, void*, int> proc,
        void* user
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelBytes2Seconds")]
    public static partial double ChannelBytes2Seconds(
        uint handle,
        ulong pos
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelSeconds2Bytes")]
    public static partial ulong ChannelSeconds2Bytes(
        uint handle,
        double pos
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelGetDevice")]
    public static partial uint ChannelGetDevice(
        uint handle
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelSetDevice")]
    public static partial int ChannelSetDevice(
        uint handle,
        uint device
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelIsActive")]
    public static partial uint ChannelIsActive(
        uint handle
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelGetInfo")]
    public static partial int ChannelGetInfo(
        uint handle,
        BASS_CHANNELINFO* info
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelGetTags")]
    public static partial byte* ChannelGetTags(
        uint handle,
        uint tags
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelFlags")]
    public static partial uint ChannelFlags(
        uint handle,
        uint flags,
        uint mask
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelLock")]
    public static partial int ChannelLock(
        uint handle,
        int @lock
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelRef")]
    public static partial int ChannelRef(
        uint handle,
        int inc
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelFree")]
    public static partial int ChannelFree(
        uint handle
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelPlay")]
    public static partial int ChannelPlay(
        uint handle,
        int restart
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelStart")]
    public static partial int ChannelStart(
        uint handle
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelStop")]
    public static partial int ChannelStop(
        uint handle
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelPause")]
    public static partial int ChannelPause(
        uint handle
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelUpdate")]
    public static partial int ChannelUpdate(
        uint handle,
        uint length
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelSetAttribute")]
    public static partial int ChannelSetAttribute(
        uint handle,
        uint attrib,
        float @value
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelGetAttribute")]
    public static partial int ChannelGetAttribute(
        uint handle,
        uint attrib,
        float* @value
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelSlideAttribute")]
    public static partial int ChannelSlideAttribute(
        uint handle,
        uint attrib,
        float @value,
        uint time
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelIsSliding")]
    public static partial int ChannelIsSliding(
        uint handle,
        uint attrib
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelSetAttributeEx")]
    public static partial int ChannelSetAttributeEx(
        uint handle,
        uint attrib,
        void* @value,
        uint typesize
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelGetAttributeEx")]
    public static partial uint ChannelGetAttributeEx(
        uint handle,
        uint attrib,
        void* @value,
        uint typesize
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelSet3DAttributes")]
    public static partial int ChannelSet3DAttributes(
        uint handle,
        int mode,
        float min,
        float max,
        int iangle,
        int oangle,
        float outvol
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelGet3DAttributes")]
    public static partial int ChannelGet3DAttributes(
        uint handle,
        uint* mode,
        float* min,
        float* max,
        uint* iangle,
        uint* oangle,
        float* outvol
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelSet3DPosition")]
    public static partial int ChannelSet3DPosition(
        uint handle,
        BASS_3DVECTOR* pos,
        BASS_3DVECTOR* orient,
        BASS_3DVECTOR* vel
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelGet3DPosition")]
    public static partial int ChannelGet3DPosition(
        uint handle,
        BASS_3DVECTOR* pos,
        BASS_3DVECTOR* orient,
        BASS_3DVECTOR* vel
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelGetLength")]
    public static partial ulong ChannelGetLength(
        uint handle,
        uint mode
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelSetPosition")]
    public static partial int ChannelSetPosition(
        uint handle,
        ulong pos,
        uint mode
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelGetPosition")]
    public static partial ulong ChannelGetPosition(
        uint handle,
        uint mode
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelGetLevel")]
    public static partial uint ChannelGetLevel(
        uint handle
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelGetLevelEx")]
    public static partial int ChannelGetLevelEx(
        uint handle,
        float* levels,
        float length,
        uint flags
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelGetData")]
    public static partial uint ChannelGetData(
        uint handle,
        void* buffer,
        uint length
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelSetSync")]
    public static partial uint ChannelSetSync(
        uint handle,
        uint type,
        ulong param,
        delegate* unmanaged<uint, uint, uint, void*, void> proc,
        void* user
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelRemoveSync")]
    public static partial int ChannelRemoveSync(
        uint handle,
        uint sync
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelSetLink")]
    public static partial int ChannelSetLink(
        uint handle,
        uint chan
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelRemoveLink")]
    public static partial int ChannelRemoveLink(
        uint handle,
        uint chan
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelSetDSP")]
    public static partial uint ChannelSetDSP(
        uint handle,
        delegate* unmanaged<uint, uint, void*, uint, void*, void> proc,
        void* user,
        int priority
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelSetDSPEx")]
    public static partial uint ChannelSetDSPEx(
        uint handle,
        delegate* unmanaged<uint, uint, void*, uint, void*, void> proc,
        void* user,
        int priority,
        uint flags
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelRemoveDSP")]
    public static partial int ChannelRemoveDSP(
        uint handle,
        uint dsp
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelSetFX")]
    public static partial uint ChannelSetFX(
        uint handle,
        uint type,
        int priority
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_ChannelRemoveFX")]
    public static partial int ChannelRemoveFX(
        uint handle,
        uint fx
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_FXSetParameters")]
    public static partial int FXSetParameters(
        uint handle,
        void* @params
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_FXGetParameters")]
    public static partial int FXGetParameters(
        uint handle,
        void* @params
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_FXSetPriority")]
    public static partial int FXSetPriority(
        uint handle,
        int priority
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_FXSetBypass")]
    public static partial int FXSetBypass(
        uint handle,
        int bypass
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_FXReset")]
    public static partial int FXReset(
        uint handle
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_FXFree")]
    public static partial int FXFree(
        uint handle
    );

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint BASS_SPEAKER_N(uint n) => n << 24;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint BASS_DSP_MONO_N(uint n) => n << 24;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint BASS_DSP_STEREO_N(uint n) => BASS_DSP_MONO_N(n) | 0x800000;

    [StructLayout(LayoutKind.Sequential)]
    public struct BASS_DEVICEINFO
    {
        public byte* name;
        public byte* driver;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BASS_INFO
    {
        public uint flags;
        public fixed uint reserved[7];
        public uint minbuf;
        public uint dsver;
        public uint latency;
        public uint initflags;
        public uint speakers;
        public uint freq;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BASS_RECORDINFO
    {
        public uint flags;
        public uint formats;
        public uint inputs;
        public int singlein;
        public uint freq;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BASS_SAMPLE
    {
        public uint freq;
        public float volume;
        public float pan;
        public uint flags;
        public uint length;
        public uint max;
        public uint origres;
        public uint chans;
        public uint mingap;
        public uint mode3d;
        public float mindist;
        public float maxdist;
        public uint iangle;
        public uint oangle;
        public float outvol;
        public fixed uint reserved[2];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BASS_CHANNELINFO
    {
        public uint freq;
        public uint chans;
        public uint flags;
        public uint ctype;
        public uint origres;
        public uint plugin;
        public uint sample;
        public byte* filename;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BASS_PLUGINFORM
    {
        public uint ctype;
        public byte* name;
        public byte* exts;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BASS_PLUGININFO
    {
        public uint version;
        public uint formatc;
        public BASS_PLUGINFORM* formats;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BASS_3DVECTOR
    {
        public float x;
        public float y;
        public float z;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BASS_FILEPROCS
    {
        public delegate* unmanaged<void*, void> close;
        public delegate* unmanaged<void*, ulong> length;
        public delegate* unmanaged<void*, uint, void*, uint> read;
        public delegate* unmanaged<ulong, void*, int> seek;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BASS_FILEOPENPROCS
    {
        public delegate* unmanaged<void*, void> close;
        public delegate* unmanaged<void*, ulong> length;
        public delegate* unmanaged<void*, uint, void*, uint> read;
        public delegate* unmanaged<ulong, void*, int> seek;
        public delegate* unmanaged<byte*, uint, void*> open;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TAG_ID3
    {
        public fixed byte id[3];
        public fixed byte title[30];
        public fixed byte artist[30];
        public fixed byte album[30];
        public fixed byte year[4];
        public fixed byte comment[30];
        public byte genre;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TAG_BINARY
    {
        public void* data;
        public uint length;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TAG_APE_BINARY
    {
        public byte* key;
        public void* data;
        public uint length;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct TAG_BEXT
    {
        public fixed byte Description[256];
        public fixed byte Originator[32];
        public fixed byte OriginatorReference[32];
        public fixed byte OriginationDate[10];
        public fixed byte OriginationTime[8];
        public ulong TimeReference;
        public ushort Version;
        public fixed byte UMID[64];
        public fixed byte Reserved[190];
        // Flexible array member CodingHistory[] starts immediately after this struct.
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TAG_CART_TIMER
    {
        public uint dwUsage;
        public uint dwValue;
    }

    [InlineArray(8)]
    public struct TAG_CART_TIMER_ARRAY_8
    {
        private TAG_CART_TIMER _element0;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TAG_CART
    {
        public fixed byte Version[4];
        public fixed byte Title[64];
        public fixed byte Artist[64];
        public fixed byte CutID[64];
        public fixed byte ClientID[64];
        public fixed byte Category[64];
        public fixed byte Classification[64];
        public fixed byte OutCue[64];
        public fixed byte StartDate[10];
        public fixed byte StartTime[8];
        public fixed byte EndDate[10];
        public fixed byte EndTime[8];
        public fixed byte ProducerAppID[64];
        public fixed byte ProducerAppVersion[64];
        public fixed byte UserDef[64];
        public uint dwLevelReference;
        public TAG_CART_TIMER_ARRAY_8 PostTimer;
        public fixed byte Reserved[276];
        public fixed byte URL[1024];
        // Flexible array member TagText[] starts immediately after this struct.
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TAG_CUE_POINT
    {
        public uint dwName;
        public uint dwPosition;
        public uint fccChunk;
        public uint dwChunkStart;
        public uint dwBlockStart;
        public uint dwSampleOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TAG_CUE
    {
        public uint dwCuePoints;
        // Flexible array member CuePoints[] starts immediately after this struct.
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TAG_SMPL_LOOP
    {
        public uint dwIdentifier;
        public uint dwType;
        public uint dwStart;
        public uint dwEnd;
        public uint dwFraction;
        public uint dwPlayCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TAG_SMPL
    {
        public uint dwManufacturer;
        public uint dwProduct;
        public uint dwSamplePeriod;
        public uint dwMIDIUnityNote;
        public uint dwMIDIPitchFraction;
        public uint dwSMPTEFormat;
        public uint dwSMPTEOffset;
        public uint cSampleLoops;
        public uint cbSamplerData;
        // Flexible array member SampleLoops[] starts immediately after this struct.
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TAG_CA_CODEC
    {
        public uint ftype;
        public uint atype;
        public byte* name;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BASS_DX8_CHORUS
    {
        public float fWetDryMix;
        public float fDepth;
        public float fFeedback;
        public float fFrequency;
        public uint lWaveform;
        public float fDelay;
        public uint lPhase;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BASS_DX8_COMPRESSOR
    {
        public float fGain;
        public float fAttack;
        public float fRelease;
        public float fThreshold;
        public float fRatio;
        public float fPredelay;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BASS_DX8_DISTORTION
    {
        public float fGain;
        public float fEdge;
        public float fPostEQCenterFrequency;
        public float fPostEQBandwidth;
        public float fPreLowpassCutoff;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BASS_DX8_ECHO
    {
        public float fWetDryMix;
        public float fFeedback;
        public float fLeftDelay;
        public float fRightDelay;
        public int lPanDelay;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BASS_DX8_FLANGER
    {
        public float fWetDryMix;
        public float fDepth;
        public float fFeedback;
        public float fFrequency;
        public uint lWaveform;
        public float fDelay;
        public uint lPhase;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BASS_DX8_GARGLE
    {
        public uint dwRateHz;
        public uint dwWaveShape;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BASS_DX8_I3DL2REVERB
    {
        public int lRoom;
        public int lRoomHF;
        public float flRoomRolloffFactor;
        public float flDecayTime;
        public float flDecayHFRatio;
        public int lReflections;
        public float flReflectionsDelay;
        public int lReverb;
        public float flReverbDelay;
        public float flDiffusion;
        public float flDensity;
        public float flHFReference;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BASS_DX8_PARAMEQ
    {
        public float fCenter;
        public float fBandwidth;
        public float fGain;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BASS_DX8_REVERB
    {
        public float fInGain;
        public float fReverbMix;
        public float fReverbTime;
        public float fHighFreqRTRatio;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BASS_FX_VOLUME_PARAM
    {
        public float fTarget;
        public float fCurrent;
        public float fTime;
        public uint lCurve;
    }

    public const uint BASSVERSION = 0x204;
    public const string BASSVERSIONTEXT = "2.4";
    public const int BASS_OK = 0;
    public const int BASS_ERROR_MEM = 1;
    public const int BASS_ERROR_FILEOPEN = 2;
    public const int BASS_ERROR_DRIVER = 3;
    public const int BASS_ERROR_BUFLOST = 4;
    public const int BASS_ERROR_HANDLE = 5;
    public const int BASS_ERROR_FORMAT = 6;
    public const int BASS_ERROR_POSITION = 7;
    public const int BASS_ERROR_INIT = 8;
    public const int BASS_ERROR_START = 9;
    public const int BASS_ERROR_SSL = 10;
    public const int BASS_ERROR_REINIT = 11;
    public const int BASS_ERROR_TRACK = 13;
    public const int BASS_ERROR_ALREADY = 14;
    public const int BASS_ERROR_NOTAUDIO = 17;
    public const int BASS_ERROR_NOCHAN = 18;
    public const int BASS_ERROR_ILLTYPE = 19;
    public const int BASS_ERROR_ILLPARAM = 20;
    public const int BASS_ERROR_NO3D = 21;
    public const int BASS_ERROR_NOEAX = 22;
    public const int BASS_ERROR_DEVICE = 23;
    public const int BASS_ERROR_NOPLAY = 24;
    public const int BASS_ERROR_FREQ = 25;
    public const int BASS_ERROR_NOTFILE = 27;
    public const int BASS_ERROR_NOHW = 29;
    public const int BASS_ERROR_EMPTY = 31;
    public const int BASS_ERROR_NONET = 32;
    public const int BASS_ERROR_CREATE = 33;
    public const int BASS_ERROR_NOFX = 34;
    public const int BASS_ERROR_NOTAVAIL = 37;
    public const int BASS_ERROR_DECODE = 38;
    public const int BASS_ERROR_DX = 39;
    public const int BASS_ERROR_TIMEOUT = 40;
    public const int BASS_ERROR_FILEFORM = 41;
    public const int BASS_ERROR_SPEAKER = 42;
    public const int BASS_ERROR_VERSION = 43;
    public const int BASS_ERROR_CODEC = 44;
    public const int BASS_ERROR_ENDED = 45;
    public const int BASS_ERROR_BUSY = 46;
    public const int BASS_ERROR_UNSTREAMABLE = 47;
    public const int BASS_ERROR_PROTOCOL = 48;
    public const int BASS_ERROR_DENIED = 49;
    public const int BASS_ERROR_FREEING = 50;
    public const int BASS_ERROR_CANCEL = 51;
    public const int BASS_ERROR_UNKNOWN = -1;
    public const uint BASS_CONFIG_BUFFER = 0;
    public const uint BASS_CONFIG_UPDATEPERIOD = 1;
    public const uint BASS_CONFIG_GVOL_SAMPLE = 4;
    public const uint BASS_CONFIG_GVOL_STREAM = 5;
    public const uint BASS_CONFIG_GVOL_MUSIC = 6;
    public const uint BASS_CONFIG_CURVE_VOL = 7;
    public const uint BASS_CONFIG_CURVE_PAN = 8;
    public const uint BASS_CONFIG_FLOATDSP = 9;
    public const uint BASS_CONFIG_3DALGORITHM = 10;
    public const uint BASS_CONFIG_NET_TIMEOUT = 11;
    public const uint BASS_CONFIG_NET_BUFFER = 12;
    public const uint BASS_CONFIG_PAUSE_NOPLAY = 13;
    public const uint BASS_CONFIG_NET_PREBUF = 15;
    public const uint BASS_CONFIG_NET_PASSIVE = 18;
    public const uint BASS_CONFIG_REC_BUFFER = 19;
    public const uint BASS_CONFIG_NET_PLAYLIST = 21;
    public const uint BASS_CONFIG_MUSIC_VIRTUAL = 22;
    public const uint BASS_CONFIG_VERIFY = 23;
    public const uint BASS_CONFIG_UPDATETHREADS = 24;
    public const uint BASS_CONFIG_DEV_BUFFER = 27;
    public const uint BASS_CONFIG_REC_LOOPBACK = 28;
    public const uint BASS_CONFIG_IOS_SESSION = 34;
    public const uint BASS_CONFIG_IOS_MIXAUDIO = 34;
    public const uint BASS_CONFIG_DEV_DEFAULT = 36;
    public const uint BASS_CONFIG_NET_READTIMEOUT = 37;
    public const uint BASS_CONFIG_VISTA_SPEAKERS = 38;
    public const uint BASS_CONFIG_IOS_SPEAKER = 39;
    public const uint BASS_CONFIG_MF_DISABLE = 40;
    public const uint BASS_CONFIG_HANDLES = 41;
    public const uint BASS_CONFIG_UNICODE = 42;
    public const uint BASS_CONFIG_SRC = 43;
    public const uint BASS_CONFIG_SRC_SAMPLE = 44;
    public const uint BASS_CONFIG_ASYNCFILE_BUFFER = 45;
    public const uint BASS_CONFIG_OGG_PRESCAN = 47;
    public const uint BASS_CONFIG_VIDEO = 48;
    public const uint BASS_CONFIG_MF_VIDEO = BASS_CONFIG_VIDEO;
    public const uint BASS_CONFIG_AIRPLAY = 49;
    public const uint BASS_CONFIG_DEV_NONSTOP = 50;
    public const uint BASS_CONFIG_IOS_NOCATEGORY = 51;
    public const uint BASS_CONFIG_VERIFY_NET = 52;
    public const uint BASS_CONFIG_DEV_PERIOD = 53;
    public const uint BASS_CONFIG_FLOAT = 54;
    public const uint BASS_CONFIG_NET_SEEK = 56;
    public const uint BASS_CONFIG_AM_DISABLE = 58;
    public const uint BASS_CONFIG_NET_PLAYLIST_DEPTH = 59;
    public const uint BASS_CONFIG_NET_PREBUF_WAIT = 60;
    public const uint BASS_CONFIG_ANDROID_SESSIONID = 62;
    public const uint BASS_CONFIG_WASAPI_PERSIST = 65;
    public const uint BASS_CONFIG_REC_WASAPI = 66;
    public const uint BASS_CONFIG_ANDROID_AAUDIO = 67;
    public const uint BASS_CONFIG_SAMPLE_ONEHANDLE = 69;
    public const uint BASS_CONFIG_NET_META = 71;
    public const uint BASS_CONFIG_NET_RESTRATE = 72;
    public const uint BASS_CONFIG_REC_DEFAULT = 73;
    public const uint BASS_CONFIG_NORAMP = 74;
    public const uint BASS_CONFIG_NOSOUND_MAXDELAY = 76;
    public const uint BASS_CONFIG_STACKALLOC = 79;
    public const uint BASS_CONFIG_DOWNMIX = 80;
    public const uint BASS_CONFIG_NET_AGENT = 16;
    public const uint BASS_CONFIG_NET_PROXY = 17;
    public const uint BASS_CONFIG_DEV_NOTIFY = 33;
    public const uint BASS_CONFIG_IOS_NOTIFY = 46;
    public const uint BASS_CONFIG_ANDROID_JAVAVM = 63;
    public const uint BASS_CONFIG_LIBSSL = 64;
    public const uint BASS_CONFIG_FILENAME = 75;
    public const uint BASS_CONFIG_FILEOPENPROCS = 77;
    public const uint BASS_CONFIG_THREAD = 0x40000000;
    public const uint BASS_IOS_SESSION_MIX = 1;
    public const uint BASS_IOS_SESSION_DUCK = 2;
    public const uint BASS_IOS_SESSION_AMBIENT = 4;
    public const uint BASS_IOS_SESSION_SPEAKER = 8;
    public const uint BASS_IOS_SESSION_DISABLE = 0x10;
    public const uint BASS_IOS_SESSION_DEACTIVATE = 0x20;
    public const uint BASS_IOS_SESSION_AIRPLAY = 0x40;
    public const uint BASS_IOS_SESSION_BTHFP = 0x80;
    public const uint BASS_IOS_SESSION_BTA2DP = 0x100;
    public const uint BASS_DEVICE_8BITS = 1;
    public const uint BASS_DEVICE_MONO = 2;
    public const uint BASS_DEVICE_3D = 4;
    public const uint BASS_DEVICE_16BITS = 8;
    public const uint BASS_DEVICE_REINIT = 0x80;
    public const uint BASS_DEVICE_LATENCY = 0x100;
    public const uint BASS_DEVICE_CPSPEAKERS = 0x400;
    public const uint BASS_DEVICE_SPEAKERS = 0x800;
    public const uint BASS_DEVICE_NOSPEAKER = 0x1000;
    public const uint BASS_DEVICE_DMIX = 0x2000;
    public const uint BASS_DEVICE_FREQ = 0x4000;
    public const uint BASS_DEVICE_STEREO = 0x8000;
    public const uint BASS_DEVICE_HOG = 0x10000;
    public const uint BASS_DEVICE_AUDIOTRACK = 0x20000;
    public const uint BASS_DEVICE_DSOUND = 0x40000;
    public const uint BASS_DEVICE_SOFTWARE = 0x80000;
    public const uint BASS_DEVICE_OPENSLES = 0x100000;
    public const uint BASS_DEVICE_APPLEVOICE = 0x200000;
    public const uint BASS_OBJECT_DS = 1;
    public const uint BASS_OBJECT_DS3DL = 2;
    public const uint BASS_DEVICE_ENABLED = 1;
    public const uint BASS_DEVICE_DEFAULT = 2;
    public const uint BASS_DEVICE_INIT = 4;
    public const uint BASS_DEVICE_LOOPBACK = 8;
    public const uint BASS_DEVICE_DEFAULTCOM = 0x80;
    public const uint BASS_DEVICE_TYPE_MASK = 0xff000000;
    public const uint BASS_DEVICE_TYPE_NETWORK = 0x01000000;
    public const uint BASS_DEVICE_TYPE_SPEAKERS = 0x02000000;
    public const uint BASS_DEVICE_TYPE_LINE = 0x03000000;
    public const uint BASS_DEVICE_TYPE_HEADPHONES = 0x04000000;
    public const uint BASS_DEVICE_TYPE_MICROPHONE = 0x05000000;
    public const uint BASS_DEVICE_TYPE_HEADSET = 0x06000000;
    public const uint BASS_DEVICE_TYPE_HANDSET = 0x07000000;
    public const uint BASS_DEVICE_TYPE_DIGITAL = 0x08000000;
    public const uint BASS_DEVICE_TYPE_SPDIF = 0x09000000;
    public const uint BASS_DEVICE_TYPE_HDMI = 0x0a000000;
    public const uint BASS_DEVICE_TYPE_DISPLAYPORT = 0x40000000;
    public const uint BASS_DEVICES_AIRPLAY = 0x1000000;
    public const uint DSCAPS_EMULDRIVER = 0x00000020;
    public const uint DSCAPS_CERTIFIED = 0x00000040;
    public const uint DSCAPS_HARDWARE = 0x80000000;
    public const uint DSCCAPS_EMULDRIVER = DSCAPS_EMULDRIVER;
    public const uint DSCCAPS_CERTIFIED = DSCAPS_CERTIFIED;
    public const uint BASS_FILE_NAME = 0;
    public const uint BASS_FILE_MEM = 1;
    public const uint BASS_FILE_MEMCOPY = 3;
    public const uint BASS_FILE_HANDLE = 4;
    public const uint BASS_SAMPLE_8BITS = 1;
    public const uint BASS_SAMPLE_MONO = 2;
    public const uint BASS_SAMPLE_LOOP = 4;
    public const uint BASS_SAMPLE_3D = 8;
    public const uint BASS_SAMPLE_SOFTWARE = 0x10;
    public const uint BASS_SAMPLE_MUTEMAX = 0x20;
    public const uint BASS_SAMPLE_NOREORDER = 0x40;
    public const uint BASS_SAMPLE_FX = 0x80;
    public const uint BASS_SAMPLE_FLOAT = 0x100;
    public const uint BASS_SAMPLE_OVER_VOL = 0x10000;
    public const uint BASS_SAMPLE_OVER_POS = 0x20000;
    public const uint BASS_SAMPLE_OVER_DIST = 0x30000;
    public const uint BASS_STREAM_PRESCAN = 0x20000;
    public const uint BASS_STREAM_AUTOFREE = 0x40000;
    public const uint BASS_STREAM_RESTRATE = 0x80000;
    public const uint BASS_STREAM_BLOCK = 0x100000;
    public const uint BASS_STREAM_DECODE = 0x200000;
    public const uint BASS_STREAM_STATUS = 0x800000;
    public const uint BASS_MP3_IGNOREDELAY = 0x200;
    public const uint BASS_MP3_SETPOS = BASS_STREAM_PRESCAN;
    public const uint BASS_MUSIC_FLOAT = BASS_SAMPLE_FLOAT;
    public const uint BASS_MUSIC_MONO = BASS_SAMPLE_MONO;
    public const uint BASS_MUSIC_LOOP = BASS_SAMPLE_LOOP;
    public const uint BASS_MUSIC_3D = BASS_SAMPLE_3D;
    public const uint BASS_MUSIC_FX = BASS_SAMPLE_FX;
    public const uint BASS_MUSIC_AUTOFREE = BASS_STREAM_AUTOFREE;
    public const uint BASS_MUSIC_DECODE = BASS_STREAM_DECODE;
    public const uint BASS_MUSIC_PRESCAN = BASS_STREAM_PRESCAN;
    public const uint BASS_MUSIC_CALCLEN = BASS_MUSIC_PRESCAN;
    public const uint BASS_MUSIC_RAMP = 0x200;
    public const uint BASS_MUSIC_RAMPS = 0x400;
    public const uint BASS_MUSIC_SURROUND = 0x800;
    public const uint BASS_MUSIC_SURROUND2 = 0x1000;
    public const uint BASS_MUSIC_FT2PAN = 0x2000;
    public const uint BASS_MUSIC_FT2MOD = 0x2000;
    public const uint BASS_MUSIC_PT1MOD = 0x4000;
    public const uint BASS_MUSIC_NONINTER = 0x10000;
    public const uint BASS_MUSIC_SINCINTER = 0x800000;
    public const uint BASS_MUSIC_POSRESET = 0x8000;
    public const uint BASS_MUSIC_POSRESETEX = 0x400000;
    public const uint BASS_MUSIC_STOPBACK = 0x80000;
    public const uint BASS_MUSIC_NOSAMPLE = 0x100000;
    public const uint BASS_SPEAKER_FRONT = 0x1000000;
    public const uint BASS_SPEAKER_REAR = 0x2000000;
    public const uint BASS_SPEAKER_CENLFE = 0x3000000;
    public const uint BASS_SPEAKER_SIDE = 0x4000000;
    public const uint BASS_SPEAKER_LEFT = 0x10000000;
    public const uint BASS_SPEAKER_RIGHT = 0x20000000;
    public const uint BASS_SPEAKER_FRONTLEFT = BASS_SPEAKER_FRONT | BASS_SPEAKER_LEFT;
    public const uint BASS_SPEAKER_FRONTRIGHT = BASS_SPEAKER_FRONT | BASS_SPEAKER_RIGHT;
    public const uint BASS_SPEAKER_REARLEFT = BASS_SPEAKER_REAR | BASS_SPEAKER_LEFT;
    public const uint BASS_SPEAKER_REARRIGHT = BASS_SPEAKER_REAR | BASS_SPEAKER_RIGHT;
    public const uint BASS_SPEAKER_CENTER = BASS_SPEAKER_CENLFE | BASS_SPEAKER_LEFT;
    public const uint BASS_SPEAKER_LFE = BASS_SPEAKER_CENLFE | BASS_SPEAKER_RIGHT;
    public const uint BASS_SPEAKER_SIDELEFT = BASS_SPEAKER_SIDE | BASS_SPEAKER_LEFT;
    public const uint BASS_SPEAKER_SIDERIGHT = BASS_SPEAKER_SIDE | BASS_SPEAKER_RIGHT;
    public const uint BASS_SPEAKER_REAR2 = BASS_SPEAKER_SIDE;
    public const uint BASS_SPEAKER_REAR2LEFT = BASS_SPEAKER_SIDELEFT;
    public const uint BASS_SPEAKER_REAR2RIGHT = BASS_SPEAKER_SIDERIGHT;
    public const uint BASS_ASYNCFILE = 0x40000000;
    public const uint BASS_UNICODE = 0x80000000;
    public const uint BASS_RECORD_OPENSLES = 0x1000;
    public const uint BASS_RECORD_PAUSE = 0x8000;
    public const uint BASS_ORIGRES_FLOAT = 0x10000;
    public const uint BASS_CTYPE_SAMPLE = 1;
    public const uint BASS_CTYPE_RECORD = 2;
    public const uint BASS_CTYPE_STREAM = 0x10000;
    public const uint BASS_CTYPE_STREAM_VORBIS = 0x10002;
    public const uint BASS_CTYPE_STREAM_OGG = 0x10002;
    public const uint BASS_CTYPE_STREAM_MP1 = 0x10003;
    public const uint BASS_CTYPE_STREAM_MP2 = 0x10004;
    public const uint BASS_CTYPE_STREAM_MP3 = 0x10005;
    public const uint BASS_CTYPE_STREAM_AIFF = 0x10006;
    public const uint BASS_CTYPE_STREAM_CA = 0x10007;
    public const uint BASS_CTYPE_STREAM_MF = 0x10008;
    public const uint BASS_CTYPE_STREAM_AM = 0x10009;
    public const uint BASS_CTYPE_STREAM_SAMPLE = 0x1000a;
    public const uint BASS_CTYPE_STREAM_DUMMY = 0x18000;
    public const uint BASS_CTYPE_STREAM_DEVICE = 0x18001;
    public const uint BASS_CTYPE_STREAM_WAV = 0x40000;
    public const uint BASS_CTYPE_STREAM_WAV_PCM = 0x50001;
    public const uint BASS_CTYPE_STREAM_WAV_FLOAT = 0x50003;
    public const uint BASS_CTYPE_MUSIC_MOD = 0x20000;
    public const uint BASS_CTYPE_MUSIC_MTM = 0x20001;
    public const uint BASS_CTYPE_MUSIC_S3M = 0x20002;
    public const uint BASS_CTYPE_MUSIC_XM = 0x20003;
    public const uint BASS_CTYPE_MUSIC_IT = 0x20004;
    public const uint BASS_CTYPE_MUSIC_MO3 = 0x00100;
    public const uint BASS_PLUGIN_PROC = 1;
    public const uint BASS_3DMODE_NORMAL = 0;
    public const uint BASS_3DMODE_RELATIVE = 1;
    public const uint BASS_3DMODE_OFF = 2;
    public const uint BASS_3DALG_DEFAULT = 0;
    public const uint BASS_3DALG_OFF = 1;
    public const uint BASS_SAMCHAN_NEW = 1;
    public const uint BASS_SAMCHAN_STREAM = 2;
    public const uint BASS_STREAMPROC_AGAIN = 0x40000000;
    public const uint BASS_STREAMPROC_END = 0x80000000;
    public static readonly delegate* unmanaged<uint, void*, uint, void*, uint> STREAMPROC_DUMMY = null;
    public static readonly delegate* unmanaged<uint, void*, uint, void*, uint> STREAMPROC_PUSH = (delegate* unmanaged<uint, void*, uint, void*, uint>)(void*)(nint)(-1);
    public static readonly delegate* unmanaged<uint, void*, uint, void*, uint> STREAMPROC_DEVICE = (delegate* unmanaged<uint, void*, uint, void*, uint>)(void*)(nint)(-2);
    public static readonly delegate* unmanaged<uint, void*, uint, void*, uint> STREAMPROC_DEVICE_3D = (delegate* unmanaged<uint, void*, uint, void*, uint>)(void*)(nint)(-3);
    public const uint STREAMFILE_NOBUFFER = 0;
    public const uint STREAMFILE_BUFFER = 1;
    public const uint STREAMFILE_BUFFERPUSH = 2;
    public const uint BASS_FILEDATA_END = 0;
    public const uint BASS_FILEPOS_CURRENT = 0;
    public const uint BASS_FILEPOS_DECODE = BASS_FILEPOS_CURRENT;
    public const uint BASS_FILEPOS_DOWNLOAD = 1;
    public const uint BASS_FILEPOS_END = 2;
    public const uint BASS_FILEPOS_START = 3;
    public const uint BASS_FILEPOS_CONNECTED = 4;
    public const uint BASS_FILEPOS_BUFFER = 5;
    public const uint BASS_FILEPOS_SOCKET = 6;
    public const uint BASS_FILEPOS_ASYNCBUF = 7;
    public const uint BASS_FILEPOS_SIZE = 8;
    public const uint BASS_FILEPOS_BUFFERING = 9;
    public const uint BASS_FILEPOS_AVAILABLE = 10;
    public const uint BASS_FILEPOS_ASYNCSIZE = 12;
    public const uint BASS_SYNC_POS = 0;
    public const uint BASS_SYNC_END = 2;
    public const uint BASS_SYNC_META = 4;
    public const uint BASS_SYNC_SLIDE = 5;
    public const uint BASS_SYNC_STALL = 6;
    public const uint BASS_SYNC_DOWNLOAD = 7;
    public const uint BASS_SYNC_FREE = 8;
    public const uint BASS_SYNC_SETPOS = 11;
    public const uint BASS_SYNC_MUSICPOS = 10;
    public const uint BASS_SYNC_MUSICINST = 1;
    public const uint BASS_SYNC_MUSICFX = 3;
    public const uint BASS_SYNC_OGG_CHANGE = 12;
    public const uint BASS_SYNC_ATTRIB = 13;
    public const uint BASS_SYNC_DEV_FAIL = 14;
    public const uint BASS_SYNC_DEV_FORMAT = 15;
    public const uint BASS_SYNC_POS_RAW = 16;
    public const uint BASS_SYNC_THREAD = 0x20000000;
    public const uint BASS_SYNC_MIXTIME = 0x40000000;
    public const uint BASS_SYNC_ONETIME = 0x80000000;
    public static readonly delegate* unmanaged<uint, void*, uint, void*, int> RECORDPROC_NONE = null;
    public static readonly delegate* unmanaged<uint, void*, uint, void*, int> RECORDPROC_TRUE = (delegate* unmanaged<uint, void*, uint, void*, int>)(void*)(nint)(-1);
    public const uint BASS_ACTIVE_STOPPED = 0;
    public const uint BASS_ACTIVE_PLAYING = 1;
    public const uint BASS_ACTIVE_STALLED = 2;
    public const uint BASS_ACTIVE_PAUSED = 3;
    public const uint BASS_ACTIVE_PAUSED_DEVICE = 4;
    public const uint BASS_ATTRIB_FREQ = 1;
    public const uint BASS_ATTRIB_VOL = 2;
    public const uint BASS_ATTRIB_PAN = 3;
    public const uint BASS_ATTRIB_EAXMIX = 4;
    public const uint BASS_ATTRIB_NOBUFFER = 5;
    public const uint BASS_ATTRIB_VBR = 6;
    public const uint BASS_ATTRIB_CPU = 7;
    public const uint BASS_ATTRIB_SRC = 8;
    public const uint BASS_ATTRIB_NET_RESUME = 9;
    public const uint BASS_ATTRIB_SCANINFO = 10;
    public const uint BASS_ATTRIB_NORAMP = 11;
    public const uint BASS_ATTRIB_BITRATE = 12;
    public const uint BASS_ATTRIB_BUFFER = 13;
    public const uint BASS_ATTRIB_GRANULE = 14;
    public const uint BASS_ATTRIB_USER = 15;
    public const uint BASS_ATTRIB_TAIL = 16;
    public const uint BASS_ATTRIB_PUSH_LIMIT = 17;
    public const uint BASS_ATTRIB_DOWNLOADPROC = 18;
    public const uint BASS_ATTRIB_VOLDSP = 19;
    public const uint BASS_ATTRIB_VOLDSP_PRIORITY = 20;
    public const uint BASS_ATTRIB_DOWNMIX = 21;
    public const uint BASS_ATTRIB_MUSIC_AMPLIFY = 0x100;
    public const uint BASS_ATTRIB_MUSIC_PANSEP = 0x101;
    public const uint BASS_ATTRIB_MUSIC_PSCALER = 0x102;
    public const uint BASS_ATTRIB_MUSIC_BPM = 0x103;
    public const uint BASS_ATTRIB_MUSIC_SPEED = 0x104;
    public const uint BASS_ATTRIB_MUSIC_VOL_GLOBAL = 0x105;
    public const uint BASS_ATTRIB_MUSIC_ACTIVE = 0x106;
    public const uint BASS_ATTRIB_MUSIC_VOL_CHAN = 0x200;
    public const uint BASS_ATTRIB_MUSIC_VOL_INST = 0x300;
    public const int BASS_ATTRIBTYPE_FLOAT = -1;
    public const int BASS_ATTRIBTYPE_INT = -2;
    public const uint BASS_SLIDE_LOG = 0x1000000;
    public const uint BASS_DATA_AVAILABLE = 0;
    public const uint BASS_DATA_NOREMOVE = 0x10000000;
    public const uint BASS_DATA_FIXED = 0x20000000;
    public const uint BASS_DATA_FLOAT = 0x40000000;
    public const uint BASS_DATA_FFT256 = 0x80000000;
    public const uint BASS_DATA_FFT512 = 0x80000001;
    public const uint BASS_DATA_FFT1024 = 0x80000002;
    public const uint BASS_DATA_FFT2048 = 0x80000003;
    public const uint BASS_DATA_FFT4096 = 0x80000004;
    public const uint BASS_DATA_FFT8192 = 0x80000005;
    public const uint BASS_DATA_FFT16384 = 0x80000006;
    public const uint BASS_DATA_FFT32768 = 0x80000007;
    public const uint BASS_DATA_FFT_INDIVIDUAL = 0x10;
    public const uint BASS_DATA_FFT_NOWINDOW = 0x20;
    public const uint BASS_DATA_FFT_REMOVEDC = 0x40;
    public const uint BASS_DATA_FFT_COMPLEX = 0x80;
    public const uint BASS_DATA_FFT_NYQUIST = 0x100;
    public const uint BASS_LEVEL_MONO = 1;
    public const uint BASS_LEVEL_STEREO = 2;
    public const uint BASS_LEVEL_RMS = 4;
    public const uint BASS_LEVEL_VOLPAN = 8;
    public const uint BASS_LEVEL_NOREMOVE = 0x10;
    public const uint BASS_TAG_ID3 = 0;
    public const uint BASS_TAG_ID3V2 = 1;
    public const uint BASS_TAG_OGG = 2;
    public const uint BASS_TAG_HTTP = 3;
    public const uint BASS_TAG_ICY = 4;
    public const uint BASS_TAG_META = 5;
    public const uint BASS_TAG_APE = 6;
    public const uint BASS_TAG_MP4 = 7;
    public const uint BASS_TAG_WMA = 8;
    public const uint BASS_TAG_VENDOR = 9;
    public const uint BASS_TAG_LYRICS3 = 10;
    public const uint BASS_TAG_CA_CODEC = 11;
    public const uint BASS_TAG_MF = 13;
    public const uint BASS_TAG_WAVEFORMAT = 14;
    public const uint BASS_TAG_AM_NAME = 16;
    public const uint BASS_TAG_ID3V2_2 = 17;
    public const uint BASS_TAG_AM_MIME = 18;
    public const uint BASS_TAG_LOCATION = 19;
    public const uint BASS_TAG_ID3V2_BINARY = 20;
    public const uint BASS_TAG_ID3V2_2_BINARY = 21;
    public const uint BASS_TAG_RIFF_INFO = 0x100;
    public const uint BASS_TAG_RIFF_BEXT = 0x101;
    public const uint BASS_TAG_RIFF_CART = 0x102;
    public const uint BASS_TAG_RIFF_DISP = 0x103;
    public const uint BASS_TAG_RIFF_CUE = 0x104;
    public const uint BASS_TAG_RIFF_SMPL = 0x105;
    public const uint BASS_TAG_APE_BINARY = 0x1000;
    public const uint BASS_TAG_MP4_COVERART = 0x1400;
    public const uint BASS_TAG_MUSIC_NAME = 0x10000;
    public const uint BASS_TAG_MUSIC_MESSAGE = 0x10001;
    public const uint BASS_TAG_MUSIC_ORDERS = 0x10002;
    public const uint BASS_TAG_MUSIC_AUTH = 0x10003;
    public const uint BASS_TAG_MUSIC_INST = 0x10100;
    public const uint BASS_TAG_MUSIC_CHAN = 0x10200;
    public const uint BASS_TAG_MUSIC_SAMPLE = 0x10300;
    public const uint BASS_TAG_INCREF = 0x20000000;
    public const uint BASS_POS_BYTE = 0;
    public const uint BASS_POS_MUSIC_ORDER = 1;
    public const uint BASS_POS_OGG = 3;
    public const uint BASS_POS_TRACK = 4;
    public const uint BASS_POS_RAW = 6;
    public const uint BASS_POS_END = 0x10;
    public const uint BASS_POS_LOOP = 0x11;
    public const uint BASS_POS_DSP = 0x800000;
    public const uint BASS_POS_FLUSH = 0x1000000;
    public const uint BASS_POS_RESET = 0x2000000;
    public const uint BASS_POS_RELATIVE = 0x4000000;
    public const uint BASS_POS_INEXACT = 0x8000000;
    public const uint BASS_POS_DECODE = 0x10000000;
    public const uint BASS_POS_DECODETO = 0x20000000;
    public const uint BASS_POS_SCAN = 0x40000000;
    public const uint BASS_NODEVICE = 0x20000;
    public const uint BASS_INPUT_OFF = 0x10000;
    public const uint BASS_INPUT_ON = 0x20000;
    public const uint BASS_INPUT_TYPE_MASK = 0xff000000;
    public const uint BASS_INPUT_TYPE_UNDEF = 0x00000000;
    public const uint BASS_INPUT_TYPE_DIGITAL = 0x01000000;
    public const uint BASS_INPUT_TYPE_LINE = 0x02000000;
    public const uint BASS_INPUT_TYPE_MIC = 0x03000000;
    public const uint BASS_INPUT_TYPE_SYNTH = 0x04000000;
    public const uint BASS_INPUT_TYPE_CD = 0x05000000;
    public const uint BASS_INPUT_TYPE_PHONE = 0x06000000;
    public const uint BASS_INPUT_TYPE_SPEAKER = 0x07000000;
    public const uint BASS_INPUT_TYPE_WAVE = 0x08000000;
    public const uint BASS_INPUT_TYPE_AUX = 0x09000000;
    public const uint BASS_INPUT_TYPE_ANALOG = 0x0a000000;
    public const uint BASS_DSP_READONLY = 1;
    public const uint BASS_DSP_FLOAT = 2;
    public const uint BASS_DSP_FREECALL = 4;
    public const uint BASS_DSP_BYPASS = 0x400000;
    public const uint BASS_FX_DX8_CHORUS = 0;
    public const uint BASS_FX_DX8_COMPRESSOR = 1;
    public const uint BASS_FX_DX8_DISTORTION = 2;
    public const uint BASS_FX_DX8_ECHO = 3;
    public const uint BASS_FX_DX8_FLANGER = 4;
    public const uint BASS_FX_DX8_GARGLE = 5;
    public const uint BASS_FX_DX8_I3DL2REVERB = 6;
    public const uint BASS_FX_DX8_PARAMEQ = 7;
    public const uint BASS_FX_DX8_REVERB = 8;
    public const uint BASS_FX_VOLUME = 9;
    public const uint BASS_DX8_PHASE_NEG_180 = 0;
    public const uint BASS_DX8_PHASE_NEG_90 = 1;
    public const uint BASS_DX8_PHASE_ZERO = 2;
    public const uint BASS_DX8_PHASE_90 = 3;
    public const uint BASS_DX8_PHASE_180 = 4;
    public const uint BASS_DEVICENOTIFY_ENABLED = 0;
    public const uint BASS_DEVICENOTIFY_DEFAULT = 1;
    public const uint BASS_DEVICENOTIFY_REC_DEFAULT = 2;
    public const uint BASS_DEVICENOTIFY_DEFAULTCOM = 3;
    public const uint BASS_DEVICENOTIFY_REC_DEFAULTCOM = 4;
    public const uint BASS_IOSNOTIFY_INTERRUPT = 1;
    public const uint BASS_IOSNOTIFY_INTERRUPT_END = 2;
}
