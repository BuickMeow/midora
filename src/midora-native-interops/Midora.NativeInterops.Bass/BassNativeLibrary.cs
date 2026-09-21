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
    private static string? _searchDirectory;

    /// <summary>
    /// Registers the operator-supplied native directory explicitly (worker native-directory
    /// argument). The environment variable <c>MIDORA_BASS_NATIVE_DIR</c> stays as a fallback.
    /// </summary>
    public static void SetSearchDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            Volatile.Write(ref _searchDirectory, null);
            return;
        }

        string fullPath = Path.GetFullPath(directory);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException(
                $"The BASS native directory does not exist: {fullPath}");
        }

        Volatile.Write(ref _searchDirectory, fullPath);
    }

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
        string? directory = Volatile.Read(ref _searchDirectory)
            ?? Environment.GetEnvironmentVariable("MIDORA_BASS_NATIVE_DIR");
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
