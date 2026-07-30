#pragma warning disable CA1401

using System.Runtime.InteropServices;

namespace Midora.NativeInterops.BassMidi;

public static unsafe partial class BASSMIDI
{
    public const string LibraryName = "bassmidi";

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_GetVersion")]
    public static partial uint GetVersion();

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_StreamCreate")]
    public static partial uint StreamCreate(
        uint channels,
        uint flags,
        uint freq
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_StreamCreateFile")]
    public static partial uint StreamCreateFile(
        uint filetype,
        void* @file,
        ulong offset,
        ulong length,
        uint flags,
        uint freq
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_StreamCreateURL")]
    public static partial uint StreamCreateURL(
        byte* url,
        uint offset,
        uint flags,
        delegate* unmanaged<void*, uint, void*, void> proc,
        void* user,
        uint freq
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_StreamCreateFileUser")]
    public static partial uint StreamCreateFileUser(
        uint system,
        uint flags,
        global::Midora.NativeInterops.Bass.BASS.BASS_FILEPROCS* procs,
        void* user,
        uint freq
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_StreamCreateEvents")]
    public static partial uint StreamCreateEvents(
        BASS_MIDI_EVENT* events,
        uint ppqn,
        uint flags,
        uint freq
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_StreamGetMark")]
    public static partial int StreamGetMark(
        uint handle,
        uint type,
        uint index,
        BASS_MIDI_MARK* mark
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_StreamGetMarks")]
    public static partial uint StreamGetMarks(
        uint handle,
        int track,
        uint type,
        BASS_MIDI_MARK* marks
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_StreamSetFonts")]
    public static partial int StreamSetFonts(
        uint handle,
        void* fonts,
        uint count
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_StreamGetFonts")]
    public static partial uint StreamGetFonts(
        uint handle,
        void* fonts,
        uint count
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_StreamLoadSamples")]
    public static partial int StreamLoadSamples(
        uint handle
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_StreamEvent")]
    public static partial int StreamEvent(
        uint handle,
        uint chan,
        uint @event,
        uint param
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_StreamEvents")]
    public static partial uint StreamEvents(
        uint handle,
        uint mode,
        void* events,
        uint length
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_StreamGetEvent")]
    public static partial uint StreamGetEvent(
        uint handle,
        uint chan,
        uint @event
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_StreamGetEvents")]
    public static partial uint StreamGetEvents(
        uint handle,
        int track,
        uint filter,
        BASS_MIDI_EVENT* events
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_StreamGetEventsEx")]
    public static partial uint StreamGetEventsEx(
        uint handle,
        int track,
        uint filter,
        BASS_MIDI_EVENT* events,
        uint start,
        uint count
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_StreamGetPreset")]
    public static partial int StreamGetPreset(
        uint handle,
        uint chan,
        BASS_MIDI_FONT* font
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_StreamGetChannel")]
    public static partial uint StreamGetChannel(
        uint handle,
        uint chan
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_StreamSetFilter")]
    public static partial int StreamSetFilter(
        uint handle,
        int seeking,
        delegate* unmanaged<uint, int, BASS_MIDI_EVENT*, int, void*, int> proc,
        void* user
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_FontInit")]
    public static partial uint FontInit(
        void* @file,
        uint flags
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_FontInitUser")]
    public static partial uint FontInitUser(
        global::Midora.NativeInterops.Bass.BASS.BASS_FILEPROCS* procs,
        void* user,
        uint flags
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_FontFree")]
    public static partial int FontFree(
        uint handle
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_FontGetInfo")]
    public static partial int FontGetInfo(
        uint handle,
        BASS_MIDI_FONTINFO* info
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_FontGetPresets")]
    public static partial int FontGetPresets(
        uint handle,
        uint* presets
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_FontGetPreset")]
    public static partial byte* FontGetPreset(
        uint handle,
        int preset,
        int bank
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_FontLoad")]
    public static partial int FontLoad(
        uint handle,
        int preset,
        int bank
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_FontLoadEx")]
    public static partial int FontLoadEx(
        uint handle,
        int preset,
        int bank,
        uint length,
        uint flags
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_FontUnload")]
    public static partial int FontUnload(
        uint handle,
        int preset,
        int bank
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_FontCompact")]
    public static partial int FontCompact(
        uint handle
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_FontPack")]
    public static partial int FontPack(
        uint handle,
        void* outfile,
        void* encoder,
        uint flags
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_FontUnpack")]
    public static partial int FontUnpack(
        uint handle,
        void* outfile,
        uint flags
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_FontFlags")]
    public static partial uint FontFlags(
        uint handle,
        uint flags,
        uint mask
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_FontSetVolume")]
    public static partial int FontSetVolume(
        uint handle,
        float volume
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_FontGetVolume")]
    public static partial float FontGetVolume(
        uint handle
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_ConvertEvents")]
    public static partial uint ConvertEvents(
        byte* data,
        uint length,
        BASS_MIDI_EVENT* events,
        uint count,
        uint flags
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_InGetDeviceInfo")]
    public static partial int InGetDeviceInfo(
        uint device,
        BASS_MIDI_DEVICEINFO* info
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_InInit")]
    public static partial int InInit(
        uint device,
        delegate* unmanaged<uint, double, byte*, uint, void*, void> proc,
        void* user
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_InFree")]
    public static partial int InFree(
        uint device
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_InStart")]
    public static partial int InStart(
        uint device
    );

    [LibraryImport(LibraryName, EntryPoint = "BASS_MIDI_InStop")]
    public static partial int InStop(
        uint device
    );

    [StructLayout(LayoutKind.Sequential)]
    public struct BASS_MIDI_FONT
    {
        public uint font;
        public int preset;
        public int bank;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BASS_MIDI_FONTEX
    {
        public uint font;
        public int spreset;
        public int sbank;
        public int dpreset;
        public int dbank;
        public int dbanklsb;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BASS_MIDI_FONTEX2
    {
        public uint font;
        public int spreset;
        public int sbank;
        public int dpreset;
        public int dbank;
        public int dbanklsb;
        public uint minchan;
        public uint numchan;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BASS_MIDI_FONTINFO
    {
        public byte* name;
        public byte* copyright;
        public byte* comment;
        public uint presets;
        public uint samsize;
        public uint samload;
        public uint samtype;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BASS_MIDI_MARK
    {
        public uint track;
        public uint pos;
        public byte* text;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BASS_MIDI_EVENT
    {
        public uint @event;
        public uint param;
        public uint chan;
        public uint tick;
        public uint pos;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BASS_MIDI_DEVICEINFO
    {
        public byte* name;
        public uint id;
        public uint flags;
    }

    public const int BASS_ERROR_MIDI_INCLUDE = 7000;
    public const uint BASS_CONFIG_MIDI_COMPACT = 0x10400;
    public const uint BASS_CONFIG_MIDI_VOICES = 0x10401;
    public const uint BASS_CONFIG_MIDI_AUTOFONT = 0x10402;
    public const uint BASS_CONFIG_MIDI_IN_PORTS = 0x10404;
    public const uint BASS_CONFIG_MIDI_SAMPLETHREADS = 0x10406;
    public const uint BASS_CONFIG_MIDI_SAMPLEMEM = 0x10407;
    public const uint BASS_CONFIG_MIDI_SAMPLEREAD = 0x10408;
    public const uint BASS_CONFIG_MIDI_SAMPLELOADING = 0x1040a;
    public const uint BASS_CONFIG_MIDI_DEFFONT = 0x10403;
    public const uint BASS_CONFIG_MIDI_SFZHEAD = 0x10409;
    public const uint BASS_SYNC_MIDI_MARK = 0x10000;
    public const uint BASS_SYNC_MIDI_MARKER = 0x10000;
    public const uint BASS_SYNC_MIDI_CUE = 0x10001;
    public const uint BASS_SYNC_MIDI_LYRIC = 0x10002;
    public const uint BASS_SYNC_MIDI_TEXT = 0x10003;
    public const uint BASS_SYNC_MIDI_EVENT = 0x10004;
    public const uint BASS_SYNC_MIDI_TICK = 0x10005;
    public const uint BASS_SYNC_MIDI_TIMESIG = 0x10006;
    public const uint BASS_SYNC_MIDI_KEYSIG = 0x10007;
    public const uint BASS_MIDI_NODRUMPARAMUSER = 0x200;
    public const uint BASS_MIDI_NODRUMPARAM = 0x400;
    public const uint BASS_MIDI_NOSYSRESET = 0x800;
    public const uint BASS_MIDI_DECAYEND = 0x1000;
    public const uint BASS_MIDI_NOFX = 0x2000;
    public const uint BASS_MIDI_DECAYSEEK = 0x4000;
    public const uint BASS_MIDI_NOCROP = 0x8000;
    public const uint BASS_MIDI_NOTEOFF1 = 0x10000;
    public const uint BASS_MIDI_ASYNC = 0x400000;
    public const uint BASS_MIDI_SINCINTER = 0x800000;
    public const uint BASS_MIDI_FONT_MEM = 0x10000;
    public const uint BASS_MIDI_FONT_MMAP = 0x20000;
    public const uint BASS_MIDI_FONT_XGDRUMS = 0x40000;
    public const uint BASS_MIDI_FONT_NOFX = 0x80000;
    public const uint BASS_MIDI_FONT_LINATTMOD = 0x100000;
    public const uint BASS_MIDI_FONT_LINDECVOL = 0x200000;
    public const uint BASS_MIDI_FONT_NORAMPIN = 0x400000;
    public const uint BASS_MIDI_FONT_NOSBLIMITS = 0x800000;
    public const uint BASS_MIDI_FONT_NOLIMITS = BASS_MIDI_FONT_NOSBLIMITS;
    public const uint BASS_MIDI_FONT_MINFX = 0x1000000;
    public const uint BASS_MIDI_FONT_SBLIMITS = 0x2000000;
    public const uint BASS_MIDI_FONT_STEREO = 0x4000000;
    public const uint BASS_MIDI_FONT_EX = 0x1000000;
    public const uint BASS_MIDI_FONT_EX2 = 0x2000000;
    public const uint BASS_MIDI_MARK_MARKER = 0;
    public const uint BASS_MIDI_MARK_CUE = 1;
    public const uint BASS_MIDI_MARK_LYRIC = 2;
    public const uint BASS_MIDI_MARK_TEXT = 3;
    public const uint BASS_MIDI_MARK_TIMESIG = 4;
    public const uint BASS_MIDI_MARK_KEYSIG = 5;
    public const uint BASS_MIDI_MARK_COPY = 6;
    public const uint BASS_MIDI_MARK_TRACK = 7;
    public const uint BASS_MIDI_MARK_INST = 8;
    public const uint BASS_MIDI_MARK_TRACKSTART = 9;
    public const uint BASS_MIDI_MARK_SEQSPEC = 10;
    public const uint BASS_MIDI_MARK_TICK = 0x10000;
    public const uint MIDI_EVENT_NOTE = 1;
    public const uint MIDI_EVENT_PROGRAM = 2;
    public const uint MIDI_EVENT_CHANPRES = 3;
    public const uint MIDI_EVENT_PITCH = 4;
    public const uint MIDI_EVENT_PITCHRANGE = 5;
    public const uint MIDI_EVENT_DRUMS = 6;
    public const uint MIDI_EVENT_FINETUNE = 7;
    public const uint MIDI_EVENT_COARSETUNE = 8;
    public const uint MIDI_EVENT_MASTERVOL = 9;
    public const uint MIDI_EVENT_BANK = 10;
    public const uint MIDI_EVENT_MODULATION = 11;
    public const uint MIDI_EVENT_VOLUME = 12;
    public const uint MIDI_EVENT_PAN = 13;
    public const uint MIDI_EVENT_EXPRESSION = 14;
    public const uint MIDI_EVENT_SUSTAIN = 15;
    public const uint MIDI_EVENT_SOUNDOFF = 16;
    public const uint MIDI_EVENT_RESET = 17;
    public const uint MIDI_EVENT_NOTESOFF = 18;
    public const uint MIDI_EVENT_PORTAMENTO = 19;
    public const uint MIDI_EVENT_PORTATIME = 20;
    public const uint MIDI_EVENT_PORTANOTE = 21;
    public const uint MIDI_EVENT_MODE = 22;
    public const uint MIDI_EVENT_REVERB = 23;
    public const uint MIDI_EVENT_CHORUS = 24;
    public const uint MIDI_EVENT_CUTOFF = 25;
    public const uint MIDI_EVENT_RESONANCE = 26;
    public const uint MIDI_EVENT_RELEASE = 27;
    public const uint MIDI_EVENT_ATTACK = 28;
    public const uint MIDI_EVENT_DECAY = 29;
    public const uint MIDI_EVENT_REVERB_MACRO = 30;
    public const uint MIDI_EVENT_CHORUS_MACRO = 31;
    public const uint MIDI_EVENT_REVERB_TIME = 32;
    public const uint MIDI_EVENT_REVERB_DELAY = 33;
    public const uint MIDI_EVENT_REVERB_LOCUTOFF = 34;
    public const uint MIDI_EVENT_REVERB_HICUTOFF = 35;
    public const uint MIDI_EVENT_REVERB_LEVEL = 36;
    public const uint MIDI_EVENT_CHORUS_DELAY = 37;
    public const uint MIDI_EVENT_CHORUS_DEPTH = 38;
    public const uint MIDI_EVENT_CHORUS_RATE = 39;
    public const uint MIDI_EVENT_CHORUS_FEEDBACK = 40;
    public const uint MIDI_EVENT_CHORUS_LEVEL = 41;
    public const uint MIDI_EVENT_CHORUS_REVERB = 42;
    public const uint MIDI_EVENT_USERFX = 43;
    public const uint MIDI_EVENT_USERFX_LEVEL = 44;
    public const uint MIDI_EVENT_USERFX_REVERB = 45;
    public const uint MIDI_EVENT_USERFX_CHORUS = 46;
    public const uint MIDI_EVENT_DRUM_FINETUNE = 50;
    public const uint MIDI_EVENT_DRUM_COARSETUNE = 51;
    public const uint MIDI_EVENT_DRUM_PAN = 52;
    public const uint MIDI_EVENT_DRUM_REVERB = 53;
    public const uint MIDI_EVENT_DRUM_CHORUS = 54;
    public const uint MIDI_EVENT_DRUM_CUTOFF = 55;
    public const uint MIDI_EVENT_DRUM_RESONANCE = 56;
    public const uint MIDI_EVENT_DRUM_LEVEL = 57;
    public const uint MIDI_EVENT_DRUM_USERFX = 58;
    public const uint MIDI_EVENT_SOFT = 60;
    public const uint MIDI_EVENT_SYSTEM = 61;
    public const uint MIDI_EVENT_TEMPO = 62;
    public const uint MIDI_EVENT_SCALETUNING = 63;
    public const uint MIDI_EVENT_CONTROL = 64;
    public const uint MIDI_EVENT_CHANPRES_VIBRATO = 65;
    public const uint MIDI_EVENT_CHANPRES_PITCH = 66;
    public const uint MIDI_EVENT_CHANPRES_FILTER = 67;
    public const uint MIDI_EVENT_CHANPRES_VOLUME = 68;
    public const uint MIDI_EVENT_MOD_VIBRATO = 69;
    public const uint MIDI_EVENT_MODRANGE = 69;
    public const uint MIDI_EVENT_BANK_LSB = 70;
    public const uint MIDI_EVENT_KEYPRES = 71;
    public const uint MIDI_EVENT_KEYPRES_VIBRATO = 72;
    public const uint MIDI_EVENT_KEYPRES_PITCH = 73;
    public const uint MIDI_EVENT_KEYPRES_FILTER = 74;
    public const uint MIDI_EVENT_KEYPRES_VOLUME = 75;
    public const uint MIDI_EVENT_SOSTENUTO = 76;
    public const uint MIDI_EVENT_MOD_PITCH = 77;
    public const uint MIDI_EVENT_MOD_FILTER = 78;
    public const uint MIDI_EVENT_MOD_VOLUME = 79;
    public const uint MIDI_EVENT_VIBRATO_RATE = 80;
    public const uint MIDI_EVENT_VIBRATO_DEPTH = 81;
    public const uint MIDI_EVENT_VIBRATO_DELAY = 82;
    public const uint MIDI_EVENT_MASTER_FINETUNE = 83;
    public const uint MIDI_EVENT_MASTER_COARSETUNE = 84;
    public const uint MIDI_EVENT_PAN_LSB = 85;
    public const uint MIDI_EVENT_MIXLEVEL = 0x10000;
    public const uint MIDI_EVENT_TRANSPOSE = 0x10001;
    public const uint MIDI_EVENT_SYSTEMEX = 0x10002;
    public const uint MIDI_EVENT_SPEED = 0x10004;
    public const uint MIDI_EVENT_DEFDRUMS = 0x10006;
    public const uint MIDI_EVENT_END = 0;
    public const uint MIDI_EVENT_END_TRACK = 0x10003;
    public const uint MIDI_EVENT_NOTES = 0x20000;
    public const uint MIDI_EVENT_VOICES = 0x20001;
    public const uint MIDI_SYSTEM_DEFAULT = 0;
    public const uint MIDI_SYSTEM_GM1 = 1;
    public const uint MIDI_SYSTEM_GM2 = 2;
    public const uint MIDI_SYSTEM_XG = 3;
    public const uint MIDI_SYSTEM_GS = 4;
    public const uint MIDI_SYSTEM_GS_88 = 5;
    public const uint BASS_MIDI_EVENTS_STRUCT = 0;
    public const uint BASS_MIDI_EVENTS_RAW = 0x10000;
    public const uint BASS_MIDI_EVENTS_SYNC = 0x1000000;
    public const uint BASS_MIDI_EVENTS_NORSTATUS = 0x2000000;
    public const uint BASS_MIDI_EVENTS_CANCEL = 0x4000000;
    public const uint BASS_MIDI_EVENTS_TIME = 0x8000000;
    public const uint BASS_MIDI_EVENTS_ABSTIME = 0x10000000;
    public const uint BASS_MIDI_EVENTS_ASYNC = 0x20000000;
    public const uint BASS_MIDI_EVENTS_FILTER = 0x40000000;
    public const uint BASS_MIDI_EVENTS_FLUSH = 0x80000000;
    public const uint BASS_MIDI_CHAN_CHORUS = 0xFFFFFFFF;
    public const uint BASS_MIDI_CHAN_REVERB = 0xFFFFFFFE;
    public const uint BASS_MIDI_CHAN_USERFX = 0xFFFFFFFD;
    public const uint BASS_CTYPE_STREAM_MIDI = 0x10d00;
    public const uint BASS_ATTRIB_MIDI_PPQN = 0x12000;
    public const uint BASS_ATTRIB_MIDI_CPU = 0x12001;
    public const uint BASS_ATTRIB_MIDI_CHANS = 0x12002;
    public const uint BASS_ATTRIB_MIDI_VOICES = 0x12003;
    public const uint BASS_ATTRIB_MIDI_VOICES_ACTIVE = 0x12004;
    public const uint BASS_ATTRIB_MIDI_STATE = 0x12005;
    public const uint BASS_ATTRIB_MIDI_SRC = 0x12006;
    public const uint BASS_ATTRIB_MIDI_KILL = 0x12007;
    public const uint BASS_ATTRIB_MIDI_SPEED = 0x12008;
    public const uint BASS_ATTRIB_MIDI_REVERB = 0x12009;
    public const uint BASS_ATTRIB_MIDI_VOL = 0x1200a;
    public const uint BASS_ATTRIB_MIDI_QUEUE_TICK = 0x1200b;
    public const uint BASS_ATTRIB_MIDI_QUEUE_BYTE = 0x1200c;
    public const uint BASS_ATTRIB_MIDI_QUEUE_ASYNC = 0x1200d;
    public const uint BASS_ATTRIB_MIDI_QUEUED_TICK = 0x1200e;
    public const uint BASS_ATTRIB_MIDI_QUEUED_BYTE = 0x1200f;
    public const uint BASS_ATTRIB_MIDI_QUEUED_ASYNC = 0x12010;
    public const uint BASS_ATTRIB_MIDI_EXCKEYS = 0x12011;
    public const uint BASS_ATTRIB_MIDI_TRACK_VOL = 0x12100;
    public const uint BASS_TAG_MIDI_TRACK = 0x11000;
    public const uint BASS_POS_MIDI_TICK = 2;
    public const uint BASS_MIDI_FONTLOAD_NOWAIT = 1;
    public const uint BASS_MIDI_FONTLOAD_COMPACT = 2;
    public const uint BASS_MIDI_FONTLOAD_NOLOAD = 4;
    public const uint BASS_MIDI_FONTLOAD_TIME = 8;
    public const uint BASS_MIDI_FONTLOAD_KEEPDEC = 16;
    public const uint BASS_MIDI_PACK_NOHEAD = 1;
    public const uint BASS_MIDI_PACK_16BIT = 2;
    public const uint BASS_MIDI_PACK_48KHZ = 4;
}
