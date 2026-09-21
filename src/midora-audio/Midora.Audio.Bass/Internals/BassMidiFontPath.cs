using System.Text;
using NativeBass = Midora.NativeInterops.Bass.BASS;
using NativeBassMidi = Midora.NativeInterops.BassMidi.BASSMIDI;

namespace Midora.Audio.Bass.Internals;

/// <summary>
/// Initializes a BASSMIDI font from a file path with platform-correct encoding. Windows keeps
/// the historical UTF-16 + <c>BASS_UNICODE</c> form; macOS/Linux pass UTF-8 bytes without
/// <c>BASS_UNICODE</c> because BASS uses UTF-8 file APIs there (spike 2026-09-21).
/// </summary>
internal static unsafe class BassMidiFontPath
{
    public static uint Init(string path, bool memoryMap)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (OperatingSystem.IsWindows())
        {
            fixed (char* pointer = path)
            {
                return NativeBassMidi.FontInit(pointer, WindowsFlags(memoryMap));
            }
        }

        byte[] utf8 = Encoding.UTF8.GetBytes(path);
        fixed (byte* pointer = utf8)
        {
            return NativeBassMidi.FontInit(
                pointer,
                memoryMap ? NativeBassMidi.BASS_MIDI_FONT_MMAP : 0u);
        }
    }

    private static uint WindowsFlags(bool memoryMap) =>
        NativeBass.BASS_UNICODE | (memoryMap ? NativeBassMidi.BASS_MIDI_FONT_MMAP : 0u);
}
