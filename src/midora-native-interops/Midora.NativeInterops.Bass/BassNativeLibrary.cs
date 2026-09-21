using System.Reflection;
using System.Runtime.InteropServices;

namespace Midora.NativeInterops.Bass;

/// <summary>
/// Resolves the BASS native library by its platform file name. On macOS the operator-supplied
/// release ships <c>libbass.dylib</c>; on Unix <c>libbass.so</c>. Windows keeps the default
/// probing of <c>bass.dll</c> so the dormant Windows audio path stays valid.
/// </summary>
public static class BassNativeLibrary
{
    private static int _registered;

    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0)
        {
            return;
        }

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(BASS).Assembly, Resolve);
    }

    private static bool TryLoadFromOperatorDirectory(
        string fileName,
        Assembly assembly,
        DllImportSearchPath? searchPath,
        out IntPtr handle)
    {
        handle = IntPtr.Zero;
        string? directory = Environment.GetEnvironmentVariable("MIDORA_BASS_NATIVE_DIR");
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        return NativeLibrary.TryLoad(
            Path.Combine(directory, fileName),
            assembly,
            searchPath,
            out handle);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, BASS.LibraryName, StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        if (OperatingSystem.IsMacOS())
        {
            if (TryLoadFromOperatorDirectory("libbass.dylib", assembly, searchPath, out IntPtr operatorHandle))
            {
                return operatorHandle;
            }

            if (NativeLibrary.TryLoad("libbass.dylib", assembly, searchPath, out IntPtr macHandle))
            {
                return macHandle;
            }
        }

        return OperatingSystem.IsLinux()
            && NativeLibrary.TryLoad("libbass.so", assembly, searchPath, out IntPtr linuxHandle)
            ? linuxHandle
            : IntPtr.Zero;
    }
}
