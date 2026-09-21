using System.Reflection;
using System.Runtime.InteropServices;

namespace Midora.NativeInterops.BassMidi;

/// <summary>
/// Resolves the BASSMIDI native library by its platform file name. On macOS the
/// operator-supplied release ships <c>libbassmidi.dylib</c>; on Unix <c>libbassmidi.so</c>.
/// Windows keeps the default probing of <c>bassmidi.dll</c>.
/// </summary>
public static class BassMidiNativeLibrary
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

        NativeLibrary.SetDllImportResolver(typeof(BASSMIDI).Assembly, Resolve);
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
        if (!string.Equals(libraryName, BASSMIDI.LibraryName, StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        if (OperatingSystem.IsMacOS())
        {
            if (TryLoadFromOperatorDirectory("libbassmidi.dylib", assembly, searchPath, out IntPtr operatorHandle))
            {
                return operatorHandle;
            }

            if (NativeLibrary.TryLoad("libbassmidi.dylib", assembly, searchPath, out IntPtr macHandle))
            {
                return macHandle;
            }
        }

        return OperatingSystem.IsLinux()
            && NativeLibrary.TryLoad("libbassmidi.so", assembly, searchPath, out IntPtr linuxHandle)
            ? linuxHandle
            : IntPtr.Zero;
    }
}
