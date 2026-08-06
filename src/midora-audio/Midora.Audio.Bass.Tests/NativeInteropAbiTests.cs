using System.Reflection;
using System.Runtime.InteropServices;
using Midora.NativeInterops.Bass;
using Midora.NativeInterops.BassMidi;
using Midora.NativeInterops.BassWasapi;

namespace Midora.Audio.Bass.Tests;

public sealed unsafe class NativeInteropAbiTests
{
    [Fact]
    public void FormalRuntimeUsesPinnedWindowsX64PrimitiveWidths()
    {
        Assert.True(OperatingSystem.IsWindows());
        Assert.True(Environment.Is64BitProcess);
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(4, sizeof(int));
        Assert.Equal(4, sizeof(uint));
        Assert.Equal(8, sizeof(ulong));
        Assert.Equal(8, sizeof(void*));
        Assert.Equal(8, sizeof(delegate* unmanaged<void>));
    }

    [Fact]
    public void BassStructLayoutsMatchPinnedX64HeaderAbi()
    {
        AssertLayout<BASS.BASS_DEVICEINFO>(24,
            (nameof(BASS.BASS_DEVICEINFO.name), 0),
            (nameof(BASS.BASS_DEVICEINFO.driver), 8),
            (nameof(BASS.BASS_DEVICEINFO.flags), 16));
        AssertLayout<BASS.BASS_INFO>(56,
            (nameof(BASS.BASS_INFO.flags), 0),
            (nameof(BASS.BASS_INFO.reserved), 4),
            (nameof(BASS.BASS_INFO.minbuf), 32),
            (nameof(BASS.BASS_INFO.freq), 52));
        AssertLayout<BASS.BASS_CHANNELINFO>(40,
            (nameof(BASS.BASS_CHANNELINFO.freq), 0),
            (nameof(BASS.BASS_CHANNELINFO.sample), 24),
            (nameof(BASS.BASS_CHANNELINFO.filename), 32));
        AssertLayout<BASS.BASS_FILEPROCS>(32,
            (nameof(BASS.BASS_FILEPROCS.close), 0),
            (nameof(BASS.BASS_FILEPROCS.length), 8),
            (nameof(BASS.BASS_FILEPROCS.read), 16),
            (nameof(BASS.BASS_FILEPROCS.seek), 24));
        AssertLayout<BASS.WAVEFORMATEX>(18,
            (nameof(BASS.WAVEFORMATEX.wFormatTag), 0),
            (nameof(BASS.WAVEFORMATEX.nSamplesPerSec), 4),
            (nameof(BASS.WAVEFORMATEX.cbSize), 16));
    }

    [Fact]
    public void BassMidiStructLayoutsMatchPinnedX64HeaderAbi()
    {
        AssertLayout<BASSMIDI.BASS_MIDI_FONT>(12,
            (nameof(BASSMIDI.BASS_MIDI_FONT.font), 0),
            (nameof(BASSMIDI.BASS_MIDI_FONT.preset), 4),
            (nameof(BASSMIDI.BASS_MIDI_FONT.bank), 8));
        AssertLayout<BASSMIDI.BASS_MIDI_FONTEX>(24,
            (nameof(BASSMIDI.BASS_MIDI_FONTEX.font), 0),
            (nameof(BASSMIDI.BASS_MIDI_FONTEX.dbanklsb), 20));
        AssertLayout<BASSMIDI.BASS_MIDI_FONTEX2>(32,
            (nameof(BASSMIDI.BASS_MIDI_FONTEX2.font), 0),
            (nameof(BASSMIDI.BASS_MIDI_FONTEX2.minchan), 24),
            (nameof(BASSMIDI.BASS_MIDI_FONTEX2.numchan), 28));
        AssertLayout<BASSMIDI.BASS_MIDI_FONTINFO>(40,
            (nameof(BASSMIDI.BASS_MIDI_FONTINFO.name), 0),
            (nameof(BASSMIDI.BASS_MIDI_FONTINFO.comment), 16),
            (nameof(BASSMIDI.BASS_MIDI_FONTINFO.presets), 24),
            (nameof(BASSMIDI.BASS_MIDI_FONTINFO.samtype), 36));
        AssertLayout<BASSMIDI.BASS_MIDI_MARK>(16,
            (nameof(BASSMIDI.BASS_MIDI_MARK.track), 0),
            (nameof(BASSMIDI.BASS_MIDI_MARK.pos), 4),
            (nameof(BASSMIDI.BASS_MIDI_MARK.text), 8));
        AssertLayout<BASSMIDI.BASS_MIDI_EVENT>(20,
            (nameof(BASSMIDI.BASS_MIDI_EVENT.@event), 0),
            (nameof(BASSMIDI.BASS_MIDI_EVENT.param), 4),
            (nameof(BASSMIDI.BASS_MIDI_EVENT.chan), 8),
            (nameof(BASSMIDI.BASS_MIDI_EVENT.tick), 12),
            (nameof(BASSMIDI.BASS_MIDI_EVENT.pos), 16));
        AssertLayout<BASSMIDI.BASS_MIDI_DEVICEINFO>(16,
            (nameof(BASSMIDI.BASS_MIDI_DEVICEINFO.name), 0),
            (nameof(BASSMIDI.BASS_MIDI_DEVICEINFO.id), 8),
            (nameof(BASSMIDI.BASS_MIDI_DEVICEINFO.flags), 12));
    }

    [Fact]
    public void BassWasapiStructLayoutsMatchPinnedX64HeaderAbi()
    {
        AssertLayout<BASSWASAPI.BASS_WASAPI_DEVICEINFO>(40,
            (nameof(BASSWASAPI.BASS_WASAPI_DEVICEINFO.name), 0),
            (nameof(BASSWASAPI.BASS_WASAPI_DEVICEINFO.id), 8),
            (nameof(BASSWASAPI.BASS_WASAPI_DEVICEINFO.type), 16),
            (nameof(BASSWASAPI.BASS_WASAPI_DEVICEINFO.flags), 20),
            (nameof(BASSWASAPI.BASS_WASAPI_DEVICEINFO.minperiod), 24),
            (nameof(BASSWASAPI.BASS_WASAPI_DEVICEINFO.defperiod), 28),
            (nameof(BASSWASAPI.BASS_WASAPI_DEVICEINFO.mixfreq), 32),
            (nameof(BASSWASAPI.BASS_WASAPI_DEVICEINFO.mixchans), 36));
        AssertLayout<BASSWASAPI.BASS_WASAPI_INFO>(32,
            (nameof(BASSWASAPI.BASS_WASAPI_INFO.initflags), 0),
            (nameof(BASSWASAPI.BASS_WASAPI_INFO.buflen), 16),
            (nameof(BASSWASAPI.BASS_WASAPI_INFO.volmax), 20),
            (nameof(BASSWASAPI.BASS_WASAPI_INFO.volstep), 28));
    }

    [Fact]
    public void CriticalImportsUseExactEntryPointsAndWindowsX64DefaultAbi()
    {
        AssertImport(typeof(BASS), nameof(BASS.GetVersion), "bass", "BASS_GetVersion", typeof(uint));
        AssertImport(typeof(BASS), nameof(BASS.ErrorGetCode), "bass", "BASS_ErrorGetCode", typeof(int));
        AssertImport(typeof(BASS), nameof(BASS.Init), "bass", "BASS_Init", typeof(int));
        AssertImport(typeof(BASS), nameof(BASS.Free), "bass", "BASS_Free", typeof(int));
        AssertImport(typeof(BASS), nameof(BASS.StreamFree), "bass", "BASS_StreamFree", typeof(int));
        AssertImport(typeof(BASSMIDI), nameof(BASSMIDI.GetVersion), "bassmidi", "BASS_MIDI_GetVersion", typeof(uint));
        AssertImport(typeof(BASSMIDI), nameof(BASSMIDI.StreamCreate), "bassmidi", "BASS_MIDI_StreamCreate", typeof(uint));
        AssertImport(typeof(BASSMIDI), nameof(BASSMIDI.StreamSetFonts), "bassmidi", "BASS_MIDI_StreamSetFonts", typeof(int));
        AssertImport(typeof(BASSMIDI), nameof(BASSMIDI.FontInit), "bassmidi", "BASS_MIDI_FontInit", typeof(uint));
        AssertImport(typeof(BASSMIDI), nameof(BASSMIDI.FontFree), "bassmidi", "BASS_MIDI_FontFree", typeof(int));
        AssertImport(typeof(BASSMIDI), nameof(BASSMIDI.FontLoad), "bassmidi", "BASS_MIDI_FontLoad", typeof(int));
        AssertImport(typeof(BASSWASAPI), nameof(BASSWASAPI.GetVersion), "basswasapi", "BASS_WASAPI_GetVersion", typeof(uint));
        AssertImport(typeof(BASSWASAPI), nameof(BASSWASAPI.SetNotify), "basswasapi", "BASS_WASAPI_SetNotify", typeof(int));
        AssertImport(typeof(BASSWASAPI), nameof(BASSWASAPI.GetDeviceInfo), "basswasapi", "BASS_WASAPI_GetDeviceInfo", typeof(int));
        AssertImport(typeof(BASSWASAPI), nameof(BASSWASAPI.Init), "basswasapi", "BASS_WASAPI_Init", typeof(int));
        AssertImport(typeof(BASSWASAPI), nameof(BASSWASAPI.Free), "basswasapi", "BASS_WASAPI_Free", typeof(int));
    }

    private static void AssertLayout<T>(
        int expectedSize,
        params (string FieldName, int Offset)[] fields) where T : struct
    {
        Assert.Equal(expectedSize, Marshal.SizeOf<T>());
        foreach ((string fieldName, int offset) in fields)
        {
            Assert.Equal(new IntPtr(offset), Marshal.OffsetOf<T>(fieldName));
        }
    }

    private static void AssertImport(
        Type declaringType,
        string methodName,
        string libraryName,
        string entryPoint,
        Type returnType)
    {
        MethodInfo method = declaringType.GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Missing native import {declaringType.Name}.{methodName}.");
        LibraryImportAttribute import = method.GetCustomAttribute<LibraryImportAttribute>()
            ?? throw new InvalidOperationException(
                $"{declaringType.Name}.{methodName} is not declared with LibraryImport.");

        Assert.Equal(libraryName, import.LibraryName);
        Assert.Equal(entryPoint, import.EntryPoint);
        Assert.Equal(returnType, method.ReturnType);
        Assert.Null(method.GetCustomAttribute<UnmanagedCallConvAttribute>());
    }
}
