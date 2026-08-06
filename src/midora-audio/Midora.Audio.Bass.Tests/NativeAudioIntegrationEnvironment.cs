using System.Runtime.InteropServices;
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
                "win-x64")
            : Path.GetFullPath(configured);
        string[] required = ["bass.dll", "bassmidi.dll"];
        if (!Directory.Exists(directory)
            || required.Any(name => !File.Exists(Path.Combine(directory, name))))
        {
            throw SkipException.ForSkip(
                "Native BASS integration requires MIDORA_BASS_NATIVE_DIR with bass.dll and bassmidi.dll.");
        }
        return directory;
    }

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

    public static void LoadBassMidi()
    {
        lock (Sync)
        {
            if (_bassMidiLoaded)
            {
                return;
            }
            string directory = RequireNativeDirectory();
            _ = NativeLibrary.Load(Path.Combine(directory, "bass.dll"));
            _ = NativeLibrary.Load(Path.Combine(directory, "bassmidi.dll"));
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
            Path.Combine(targetDirectory, "win-x64", "Midora.Audio.Bass.Worker.dll")
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
