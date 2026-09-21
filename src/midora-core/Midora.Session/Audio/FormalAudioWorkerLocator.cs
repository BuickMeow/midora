using Midora.NativeInterops.Bass;

namespace Midora.Session.Audio;

/// <summary>
/// Locates the platform Native AOT audio worker executable and the operator-supplied native
/// directory it must load BASS from. The worker is published per release platform
/// (<c>Midora.Audio.Bass.Worker.exe</c> on Windows, <c>Midora.Audio.Bass.Worker</c> on macOS) and
/// always ships beside its pinned <c>libbass</c>/<c>bassmidi</c> binaries.
/// </summary>
public static class FormalAudioWorkerLocator
{
    /// <summary>Explicit worker executable path, for operations and development layouts.</summary>
    public const string WorkerPathEnvironmentVariable = "MIDORA_AUDIO_WORKER_PATH";

    /// <summary>Explicit directory that contains the worker executable.</summary>
    public const string WorkerDirectoryEnvironmentVariable = "MIDORA_AUDIO_WORKER_DIR";

    /// <summary>Operator-supplied native BASS directory when it is not beside the worker.</summary>
    public const string NativeDirectoryEnvironmentVariable = "MIDORA_BASS_NATIVE_DIR";

    private const string WorkerFileName = "Midora.Audio.Bass.Worker";
    private const string WorkerExecutableName = WorkerFileName + ".exe";

    public static bool TryLocate(
        out string? workerPath,
        out string? nativeDirectory,
        out string? failure)
    {
        foreach (string candidate in EnumerateWorkerCandidates())
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            if (!TryResolveNativeDirectory(candidate, out string? resolvedNative, out failure))
            {
                workerPath = null;
                nativeDirectory = null;
                return false;
            }

            workerPath = candidate;
            nativeDirectory = resolvedNative;
            failure = null;
            return true;
        }

        workerPath = null;
        nativeDirectory = null;
        failure =
            "The formal audio worker is not installed. Publish Midora.Audio.Bass.Worker for this "
            + "platform and place it beside Midora or in an audio-worker directory, or set "
            + WorkerPathEnvironmentVariable + ".";
        return false;
    }

    private static IEnumerable<string> EnumerateWorkerCandidates()
    {
        string? explicitPath = Environment.GetEnvironmentVariable(WorkerPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            yield return Path.GetFullPath(explicitPath);
        }

        string? explicitDirectory = Environment.GetEnvironmentVariable(
            WorkerDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
        {
            yield return Path.Combine(Path.GetFullPath(explicitDirectory), ExecutableName);
        }

        string baseDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(baseDirectory, "audio-worker", ExecutableName);
        yield return Path.Combine(baseDirectory, ExecutableName);
    }

    private static string ExecutableName =>
        OperatingSystem.IsWindows() ? WorkerExecutableName : WorkerFileName;

    private static bool TryResolveNativeDirectory(
        string workerPath,
        out string? nativeDirectory,
        out string? failure)
    {
        string? configured = Environment.GetEnvironmentVariable(NativeDirectoryEnvironmentVariable);
        string directory = string.IsNullOrWhiteSpace(configured)
            ? Path.GetDirectoryName(workerPath)!
            : Path.GetFullPath(configured);
        if (!Directory.Exists(directory))
        {
            nativeDirectory = null;
            failure = $"The BASS native directory does not exist: {directory}";
            return false;
        }

        string[] required = BassNativeFiles.RequiredFileNames;
        string? missing = required.FirstOrDefault(
            name => !File.Exists(Path.Combine(directory, name)));
        if (missing is not null)
        {
            nativeDirectory = null;
            failure =
                $"The audio worker native directory is incomplete: {missing} is missing from {directory}.";
            return false;
        }

        nativeDirectory = directory;
        failure = null;
        return true;
    }
}
