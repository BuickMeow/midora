using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Midora.Audio;
using Midora.Audio.Bass;

namespace Midora.Desktop;

public static class FormalAudioWorkerLocator
{
    public static bool TryCreateFileRenderWorker(
        out BassMidiAudioFileRenderWorker? worker,
        out string? failure)
    {
        if (!TryLocate(out string? workerPath, out string? nativeDirectory, out failure))
        {
            worker = null;
            return false;
        }
        try
        {
            worker = new(workerPath!, nativeDirectory!, TimeSpan.FromSeconds(30));
            failure = null;
            return true;
        }
        catch (Exception exception)
        {
            worker = null;
            failure = exception.Message;
            return false;
        }
    }

    public static bool TryLocate(
        out string? workerPath,
        out string? nativeDirectory,
        out string? failure)
    {
        string root = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(root, "Midora.Audio.Bass.Worker.exe"),
            Path.Combine(root, "audio-worker", "Midora.Audio.Bass.Worker.exe")
        ];
        workerPath = candidates.FirstOrDefault(File.Exists);
        if (workerPath is null)
        {
            nativeDirectory = null;
            failure = "The formal win-x64 Native AOT audio worker is not installed beside Midora.";
            return false;
        }
        string locatedNativeDirectory = Path.GetDirectoryName(workerPath)!;
        nativeDirectory = locatedNativeDirectory;
        string[] required = ["bass.dll", "bassmidi.dll", "basswasapi.dll", "native-manifest.json"];
        string? missing = required.FirstOrDefault(file => !File.Exists(Path.Combine(locatedNativeDirectory, file)));
        if (missing is not null)
        {
            workerPath = null;
            nativeDirectory = null;
            failure = $"The formal audio worker directory is incomplete: {missing} is missing.";
            return false;
        }
        failure = null;
        return true;
    }
}

public sealed record FormalAudioOutputDevice(
    string Id,
    string Name,
    int SampleRate,
    int ChannelCount,
    bool IsSystemDefault);

/// <summary>
/// Enumerates formal WASAPI outputs through the Native AOT audio worker. This deliberately keeps
/// BASS and BASSWASAPI out of the WPF process.
/// </summary>
public static class FormalAudioOutputDeviceEnumerator
{
    private const string ProtocolHeader = "MIDORA-AUDIO-DEVICES-V1";

    public static async Task<IReadOnlyList<FormalAudioOutputDevice>> EnumerateAsync(
        CancellationToken cancellationToken = default)
    {
        if (!FormalAudioWorkerLocator.TryLocate(
                out string? workerPath,
                out string? nativeDirectory,
                out string? failure))
        {
            throw new InvalidOperationException(failure);
        }

        ProcessStartInfo start = new(workerPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("list-output-devices");
        start.ArgumentList.Add(nativeDirectory!);
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("The formal audio worker could not be started.");
        try
        {
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"The formal audio worker could not enumerate output devices. {stderr.Trim()}");
            }
            return Parse(stdout);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw;
        }
    }

    internal static IReadOnlyList<FormalAudioOutputDevice> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || !string.Equals(lines[0], ProtocolHeader, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The audio worker returned an unsupported device-list protocol.");
        }
        if (lines.Length > 1025)
        {
            throw new InvalidDataException("The audio worker returned too many output devices.");
        }

        List<FormalAudioOutputDevice> result = new(lines.Length - 1);
        HashSet<string> ids = new(StringComparer.Ordinal);
        for (int index = 1; index < lines.Length; index++)
        {
            string[] fields = lines[index].Split('\t');
            if (fields.Length != 5
                || fields[0] is not ("0" or "1")
                || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int sampleRate)
                || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out int channels)
                || sampleRate <= 0
                || channels <= 0)
            {
                throw new InvalidDataException("The audio worker returned a malformed output-device row.");
            }
            string id;
            string name;
            try
            {
                id = Encoding.UTF8.GetString(Convert.FromBase64String(fields[3]));
                name = Encoding.UTF8.GetString(Convert.FromBase64String(fields[4]));
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException("The audio worker returned malformed device text.", exception);
            }
            if (string.IsNullOrWhiteSpace(id) || !ids.Add(id))
            {
                throw new InvalidDataException("The audio worker returned an empty or duplicate output-device reference.");
            }
            result.Add(new(
                id,
                string.IsNullOrWhiteSpace(name) ? id : name,
                sampleRate,
                channels,
                fields[0] == "1"));
        }
        return result;
    }
}

public sealed class WorkerSoundFontLoadabilityValidator : ISoundFontLoadabilityValidator
{
    public async ValueTask ValidateAsync(
        string soundFontPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(soundFontPath);
        if (!File.Exists(soundFontPath))
        {
            throw new SoundFontLoadabilityException(
                SoundFontLoadabilityFailure.Missing,
                "The selected SoundFont file does not exist.");
        }
        if (!FormalAudioWorkerLocator.TryCreateFileRenderWorker(out BassMidiAudioFileRenderWorker? worker, out string? failure))
        {
            throw new MidoraAudioException(failure ?? "The formal audio worker is unavailable.");
        }
        try
        {
            await worker!.PrepareAsync(
                new(soundFontPath, 48_000, 1, 0),
                cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new SoundFontLoadabilityException(
                SoundFontLoadabilityFailure.Unreadable,
                "The selected SoundFont cannot be read.",
                innerException: exception);
        }
        catch (AudioFileRenderWorkerException exception)
        {
            // A worker/native-baseline failure is deliberately not mislabeled as a corrupt SF2.
            throw new MidoraAudioException(
                $"The formal audio worker could not validate the SoundFont: {exception.Message}",
                exception);
        }
    }
}
