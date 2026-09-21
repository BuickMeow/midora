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

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, BASS.LibraryName, StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        if (OperatingSystem.IsMacOS()
            && NativeLibrary.TryLoad("libbass.dylib", assembly, searchPath, out IntPtr macHandle))
        {
            return macHandle;
        }

        return OperatingSystem.IsLinux()
            && NativeLibrary.TryLoad("libbass.so", assembly, searchPath, out IntPtr linuxHandle)
            ? linuxHandle
            : IntPtr.Zero;
    }
}
