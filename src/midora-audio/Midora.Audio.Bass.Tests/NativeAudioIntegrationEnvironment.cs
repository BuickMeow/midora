using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Xunit.Sdk;

namespace Midora.Audio.Bass.Tests;

internal static class NativeAudioIntegrationEnvironment
{
    private static readonly object Sync = new();
    private static bool _bassMidiLoaded;
    private static string? _verifiedSoundFontPath;
    private static long _verifiedSoundFontLength;
    private static DateTime _verifiedSoundFontLastWriteTimeUtc;
    private static string? _verifiedSoundFontSha256;

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

    public static string RequireVerifiedSoundFontSha256(string soundFontPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(soundFontPath);
        string path = Path.GetFullPath(soundFontPath);
        FileInfo file = new(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException(
                "The integration-test SoundFont does not exist.",
                path);
        }

        lock (Sync)
        {
            if (string.Equals(_verifiedSoundFontPath, path, StringComparison.OrdinalIgnoreCase)
                && _verifiedSoundFontLength == file.Length
                && _verifiedSoundFontLastWriteTimeUtc == file.LastWriteTimeUtc
                && _verifiedSoundFontSha256 is not null)
            {
                return _verifiedSoundFontSha256;
            }

            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 1024,
                FileOptions.SequentialScan);
            string sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            _verifiedSoundFontPath = path;
            _verifiedSoundFontLength = file.Length;
            _verifiedSoundFontLastWriteTimeUtc = file.LastWriteTimeUtc;
            _verifiedSoundFontSha256 = sha256;
            return sha256;
        }
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
