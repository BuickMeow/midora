using System.Runtime.InteropServices;
using Midora.Audio;
using Xunit.Sdk;

namespace Midora.Audio.Bass.Tests;

internal static class NativeAudioIntegrationEnvironment
{
    private static readonly object Sync = new();
    private static bool _bassMidiLoaded;

    public static string RequireNativeDirectory()
    {
        string? configured = Environment.GetEnvironmentVariable("MIDORA_BASS_NATIVE_DIR");
        string directory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Midora",
                "Native",
                "BASS",
                NativeRuntimeIdentifier)
            : Path.GetFullPath(configured);
        string[] required = [BassLibraryFileName, BassMidiLibraryFileName];
        if (!Directory.Exists(directory)
            || required.Any(name => !File.Exists(Path.Combine(directory, name))))
        {
            throw SkipException.ForSkip(
                $"Native BASS integration requires MIDORA_BASS_NATIVE_DIR with {BassLibraryFileName} and {BassMidiLibraryFileName}.");
        }
        return directory;
    }

    internal static string NativeRuntimeIdentifier =>
        OperatingSystem.IsWindows() ? "win-x64"
        : OperatingSystem.IsMacOS() ? "osx-arm64"
        : "linux-x64";

    internal static string BassLibraryFileName =>
        OperatingSystem.IsWindows() ? "bass.dll"
        : OperatingSystem.IsMacOS() ? "libbass.dylib"
        : "libbass.so";

    internal static string BassMidiLibraryFileName =>
        OperatingSystem.IsWindows() ? "bassmidi.dll"
        : OperatingSystem.IsMacOS() ? "libbassmidi.dylib"
        : "libbassmidi.so";

    public static string RequireSoundFontPath()
    {
        string? configured = Environment.GetEnvironmentVariable("MIDORA_TEST_SOUNDFONT_PATH");
        string path = string.IsNullOrWhiteSpace(configured)
            ? string.Empty
            : Path.GetFullPath(configured);
        if (!File.Exists(path))
        {
            throw SkipException.ForSkip(
                "Native BASS integration requires MIDORA_TEST_SOUNDFONT_PATH naming an existing SF2 file.");
        }
        return path;
    }

    public static string RequireSecondSoundFontPath()
    {
        string? configured = Environment.GetEnvironmentVariable(
            "MIDORA_TEST_SECOND_SOUNDFONT_PATH");
        string path = string.IsNullOrWhiteSpace(configured)
            ? string.Empty
            : Path.GetFullPath(configured);
        if (!File.Exists(path))
        {
            throw SkipException.ForSkip(
                "Multi-SoundFont integration requires MIDORA_TEST_SECOND_SOUNDFONT_PATH naming an existing SF2 file.");
        }
        return path;
    }

    public static string RequireSoundFontSetCacheIdentity(string soundFontPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(soundFontPath);
        return SoundFontSetDefinition.Create([soundFontPath]).CacheIdentity;
    }

    public static void LoadBassMidi()
    {
        lock (Sync)
        {
            if (_bassMidiLoaded)
            {
                return;
            }
            string directory = RequireNativeDirectory();
            if (OperatingSystem.IsWindows())
            {
                _ = NativeLibrary.Load(Path.Combine(directory, BassLibraryFileName));
                _ = NativeLibrary.Load(Path.Combine(directory, BassMidiLibraryFileName));
            }
            else
            {
                // The platform resolver maps libbass/libbassmidi to the operator directory.
                Midora.NativeInterops.Bass.BassNativeLibrary.SetSearchDirectory(directory);
                Midora.NativeInterops.BassMidi.BassMidiNativeLibrary.SetSearchDirectory(directory);
                Midora.NativeInterops.Bass.BassNativeLibrary.EnsureRegistered();
                Midora.NativeInterops.BassMidi.BassMidiNativeLibrary.EnsureRegistered();
            }
            _bassMidiLoaded = true;
        }
    }

    public static string RequireManagedWorkerPath()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null
            && !File.Exists(Path.Combine(current.FullName, "Directory.Build.props")))
        {
            current = current.Parent;
        }
        string repositoryRoot = current?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        string targetDirectory = Path.Combine(
            repositoryRoot,
            "src",
            "midora-audio",
            "Midora.Audio.Bass.Worker",
            "bin",
            configuration,
            "net10.0");
        string[] candidates =
        [
            Path.Combine(targetDirectory, "Midora.Audio.Bass.Worker.dll"),
            Path.Combine(targetDirectory, NativeRuntimeIdentifier, "Midora.Audio.Bass.Worker.dll")
        ];
        string? path = candidates.FirstOrDefault(File.Exists);
        if (path is null)
        {
            throw new FileNotFoundException(
                "The managed integration-test Worker was not built.",
                candidates[0]);
        }
        return path;
    }
}
