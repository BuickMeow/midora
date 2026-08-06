namespace Midora.Audio.Bass;

internal static class InitialReleaseAudioWorkerProtocolPolicy
{
    public static bool ParseBoolean(string value, string argumentName) => value switch
    {
        "0" => false,
        "1" => true,
        _ => throw new InvalidDataException(
            $"The audio worker {argumentName} argument must be exactly 0 or 1.")
    };

    public static void ValidateRealtimeSettings(
        BassMidiRendererSettings rendererSettings,
        AudioMasterSettings masterSettings,
        int renderAheadMilliseconds,
        int deviceBufferRequestMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(rendererSettings);
        ArgumentNullException.ThrowIfNull(masterSettings);
        if (rendererSettings.MaximumWorkFrameCount != InitialReleaseAudioRuntimePolicy.WorkFrameCount)
        {
            throw new InvalidDataException(
                $"Initial-release realtime worker blocks must be {InitialReleaseAudioRuntimePolicy.WorkFrameCount} frames.");
        }
        if (renderAheadMilliseconds is < 20 or > 2_000)
        {
            throw new InvalidDataException("Realtime Render-Ahead must be 20-2000 ms.");
        }
        if (deviceBufferRequestMilliseconds is < 5 or > 200)
        {
            throw new InvalidDataException("Realtime Device Buffer Request must be 5-200 ms.");
        }
        ValidateLimiterV1(masterSettings, requireEnabled: false);
    }

    public static void ValidateFileSettings(
        BassMidiRendererSettings rendererSettings,
        AudioMasterSettings masterSettings,
        int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(rendererSettings);
        ArgumentNullException.ThrowIfNull(masterSettings);
        if (rendererSettings.MaximumWorkFrameCount != InitialReleaseAudioRuntimePolicy.WorkFrameCount)
        {
            throw new InvalidDataException(
                $"Initial-release file-render worker blocks must be {InitialReleaseAudioRuntimePolicy.WorkFrameCount} frames.");
        }
        if (sampleRate is < 8_000 or > 192_000)
        {
            throw new InvalidDataException("Audio file sample rate must be 8000-192000 Hz.");
        }
        ValidateLimiterV1(masterSettings, requireEnabled: true);
    }

    public static string RequireExistingFile(string path, string argumentName)
    {
        RequireFullyQualified(path, argumentName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"The audio worker {argumentName} file does not exist.",
                path);
        }
        return path;
    }

    public static string RequireExistingDirectory(string path, string argumentName)
    {
        RequireFullyQualified(path, argumentName);
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException(
                $"The audio worker {argumentName} directory does not exist: {path}");
        }
        return path;
    }

    public static string RequireNewFileTarget(string path, string argumentName)
    {
        RequireFullyQualified(path, argumentName);
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"The audio worker {argumentName} parent directory does not exist: {directory}");
        }
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException($"The audio worker {argumentName} target already exists.");
        }
        return path;
    }

    private static void ValidateLimiterV1(
        AudioMasterSettings masterSettings,
        bool requireEnabled)
    {
        if (masterSettings.LimiterCeiling != 1f
            || masterSettings.LimiterReleaseMilliseconds != 50f
            || requireEnabled && !masterSettings.LimiterEnabled)
        {
            throw new InvalidDataException(
                "The audio worker settings do not match the fixed initial-release Limiter v1 chain.");
        }
    }

    private static void RequireFullyQualified(string path, string argumentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException(
                $"The audio worker {argumentName} path must be fully qualified.");
        }
    }
}
