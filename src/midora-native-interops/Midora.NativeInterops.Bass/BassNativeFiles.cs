namespace Midora.NativeInterops.Bass;

/// <summary>
/// Operator-supplied native file names for the current release platform. The audio runtime and
/// the PCM cache identity must agree on these names, so they live in one place instead of being
/// repeated as Windows-only literals.
/// </summary>
public static class BassNativeFiles
{
    public static string RuntimeIdentifier =>
        OperatingSystem.IsWindows() ? "win-x64"
        : OperatingSystem.IsMacOS() ? "osx-arm64"
        : "linux-x64";

    public static string BassFileName =>
        OperatingSystem.IsWindows() ? "bass.dll"
        : OperatingSystem.IsMacOS() ? "libbass.dylib"
        : "libbass.so";

    public static string BassMidiFileName =>
        OperatingSystem.IsWindows() ? "bassmidi.dll"
        : OperatingSystem.IsMacOS() ? "libbassmidi.dylib"
        : "libbassmidi.so";

    /// <summary>BASSWASAPI exists only on Windows; other platforms use the native CoreAudio path.</summary>
    public static string BassWasapiFileName => "basswasapi.dll";

    public static string[] RequiredFileNames =>
        OperatingSystem.IsWindows()
            ? [BassFileName, BassMidiFileName, BassWasapiFileName]
            : [BassFileName, BassMidiFileName];
}
