using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;

namespace Midora.Audio.Bass;

[SupportedOSPlatform("windows")]
public sealed class BassMidiAudioFileRenderWorker : IAudioFileRenderWorker
{
    private readonly string _workerPath;
    private readonly string _bassNativeDirectory;
    private readonly TimeSpan _preparingTimeout;

    public BassMidiAudioFileRenderWorker(
        string workerPath,
        string bassNativeDirectory,
        TimeSpan preparingTimeout)
        : this(workerPath, bassNativeDirectory, preparingTimeout, allowManagedTestWorker: false)
    {
    }

    internal BassMidiAudioFileRenderWorker(
        string workerPath,
        string bassNativeDirectory,
        TimeSpan preparingTimeout,
        bool allowManagedTestWorker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(bassNativeDirectory);
        if (!File.Exists(workerPath))
        {
            throw new FileNotFoundException("The Midora Native AOT audio worker was not found.", workerPath);
        }
        if (!allowManagedTestWorker
            && !string.Equals(Path.GetExtension(workerPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Formal audio file rendering requires the win-x64 Native AOT .exe worker; other launch forms are test-only.");
        }
        if (!Directory.Exists(bassNativeDirectory))
        {
            throw new DirectoryNotFoundException(bassNativeDirectory);
        }
        if (preparingTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(preparingTimeout));
        }

        _workerPath = Path.GetFullPath(workerPath);
        _bassNativeDirectory = Path.GetFullPath(bassNativeDirectory);
        _preparingTimeout = preparingTimeout;
    }

    public async Task PrepareAsync(
        AudioFileRenderWorkerPreparation preparation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ValidateCommon(
            preparation.SoundFontPath,
            preparation.SampleRate,
            preparation.MaximumSampleVoicesPerStream,
            preparation.MasterVolumeDecibels);
        using SharedAudioWorkerControl control = SharedAudioWorkerControl.Create(
            $"Midora.Audio.FileProbe.{Guid.NewGuid():N}");
        using Process process = Start(
            CreateProbeStartInfo(preparation, control.Name));
        await ObserveProcessAsync(
            process,
            control,
            totalFrameCount: 0,
            temporaryOutputPath: null,
            progress: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<AudioFileRenderWorkerResult> RenderAsync(
        AudioFileRenderWorkerRequest request,
        IProgress<AudioFileRenderWorkerProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Plan);
        ValidateCommon(
            request.SoundFontPath,
            request.Plan.SampleRate,
            request.MaximumSampleVoicesPerStream,
            request.MasterVolumeDecibels);
        string temporaryOutputPath = Path.GetFullPath(request.TemporaryOutputPath);
        string? outputDirectory = Path.GetDirectoryName(temporaryOutputPath);
        if (string.IsNullOrEmpty(outputDirectory) || !Directory.Exists(outputDirectory))
        {
            throw new DirectoryNotFoundException(outputDirectory);
        }
        if (File.Exists(temporaryOutputPath) || Directory.Exists(temporaryOutputPath))
        {
            throw new IOException("The authorized audio worker temporary target already exists.");
        }

        string ownedDirectory = Path.Combine(
            Path.GetTempPath(),
            $"midora-audio-file-worker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(ownedDirectory);
        string planPath = Path.Combine(ownedDirectory, "compiled-audio-plan.mdap");
        try
        {
            MidiRenderPlanFile.Write(planPath, request.Plan);
            using SharedAudioWorkerControl control = SharedAudioWorkerControl.Create(
                $"Midora.Audio.FileRender.{Guid.NewGuid():N}");
            using Process process = Start(
                CreateRenderStartInfo(request, control.Name, planPath, temporaryOutputPath));
            AudioWorkerStatus status = await ObserveProcessAsync(
                process,
                control,
                request.Plan.TotalFrameCount,
                temporaryOutputPath,
                progress,
                cancellationToken).ConfigureAwait(false);
            if (!File.Exists(temporaryOutputPath))
            {
                throw new AudioFileRenderWorkerException(
                    AudioFileRenderWorkerFailureStage.Finalizing,
                    "The audio worker completed without publishing its authorized temporary WAV target.");
            }
            return new(
                request.Plan.TotalFrameCount,
                new FileInfo(temporaryOutputPath).Length,
                status.RenderingAllocatedBytes);
        }
        finally
        {
            CleanupOwnedDirectory(ownedDirectory);
        }
    }

    private async Task<AudioWorkerStatus> ObserveProcessAsync(
        Process process,
        SharedAudioWorkerControl control,
        long totalFrameCount,
        string? temporaryOutputPath,
        IProgress<AudioFileRenderWorkerProgress>? progress,
        CancellationToken cancellationToken)
    {
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        long preparingDeadline = Environment.TickCount64
            + checked((long)_preparingTimeout.TotalMilliseconds);
        bool stopSent = false;
        AudioWorkerStatus last = default;
        bool hasLast = false;
        while (!process.HasExited)
        {
            AudioWorkerStatus current = control.ReadStatus();
            if (!hasLast
                || current.State != last.State
                || current.RenderPositionFrame != last.RenderPositionFrame)
            {
                progress?.Report(new(
                    current.State,
                    current.RenderPositionFrame,
                    totalFrameCount));
                last = current;
                hasLast = true;
            }
            if (current.State == AudioWorkerState.Faulted)
            {
                break;
            }
            if (current.State is AudioWorkerState.Created or AudioWorkerState.Preparing
                && Environment.TickCount64 >= preparingDeadline)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
                _ = await standardOutput.ConfigureAwait(false);
                string error = await standardError.ConfigureAwait(false);
                IReadOnlyList<string> residual = CleanupWorkerIntermediates(temporaryOutputPath);
                throw new AudioFileRenderWorkerException(
                    AudioFileRenderWorkerFailureStage.Preparing,
                    "The Native AOT audio worker exceeded the Preparing timeout. " + error,
                    residual);
            }
            if (cancellationToken.IsCancellationRequested && !stopSent)
            {
                if (!control.TryEnqueueStop())
                {
                    throw new AudioFileRenderWorkerException(
                        CurrentStage(current.State),
                        "The bounded audio worker control ring could not accept cancellation.");
                }
                stopSent = true;
            }
            await Task.Delay(2).ConfigureAwait(false);
        }

        await process.WaitForExitAsync().ConfigureAwait(false);
        _ = await standardOutput.ConfigureAwait(false);
        string stderr = await standardError.ConfigureAwait(false);
        AudioWorkerStatus final = control.ReadStatus();
        progress?.Report(new(final.State, final.RenderPositionFrame, totalFrameCount));
        IReadOnlyList<string> residualPaths = process.ExitCode == 0
            ? Array.Empty<string>()
            : CleanupWorkerIntermediates(temporaryOutputPath);
        if (cancellationToken.IsCancellationRequested)
        {
            residualPaths = residualPaths
                .Concat(CleanupWorkerIntermediates(temporaryOutputPath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (temporaryOutputPath is not null && File.Exists(temporaryOutputPath))
            {
                try
                {
                    File.Delete(temporaryOutputPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    residualPaths = residualPaths.Concat([temporaryOutputPath]).Distinct().ToArray();
                }
            }
            throw new AudioFileRenderCancelledException(cancellationToken, residualPaths);
        }
        if (process.ExitCode != 0 || final.State == AudioWorkerState.Faulted)
        {
            throw new AudioFileRenderWorkerException(
                CurrentStage(final.State),
                $"The Native AOT audio worker failed; exitCode={process.ExitCode}; fault={final.FaultCode}; stderr={stderr}",
                residualPaths);
        }
        if (final.State != AudioWorkerState.Completed)
        {
            throw new AudioFileRenderWorkerException(
                CurrentStage(final.State),
                $"The Native AOT audio worker exited without the required Completed state; state={final.State}.",
                CleanupWorkerIntermediates(temporaryOutputPath));
        }
        return final;
    }

    private ProcessStartInfo CreateProbeStartInfo(
        AudioFileRenderWorkerPreparation preparation,
        string controlName)
    {
        ProcessStartInfo result = CreateStartInfo();
        result.ArgumentList.Add("file-probe");
        result.ArgumentList.Add(controlName);
        result.ArgumentList.Add(Path.GetFullPath(preparation.SoundFontPath));
        result.ArgumentList.Add(_bassNativeDirectory);
        result.ArgumentList.Add(preparation.SampleRate.ToString(CultureInfo.InvariantCulture));
        result.ArgumentList.Add(preparation.MaximumSampleVoicesPerStream.ToString(CultureInfo.InvariantCulture));
        AddMasterSettings(result, preparation.MasterVolumeDecibels);
        return result;
    }

    private ProcessStartInfo CreateRenderStartInfo(
        AudioFileRenderWorkerRequest request,
        string controlName,
        string planPath,
        string temporaryOutputPath)
    {
        ProcessStartInfo result = CreateStartInfo();
        result.ArgumentList.Add("file-render");
        result.ArgumentList.Add(controlName);
        result.ArgumentList.Add(planPath);
        result.ArgumentList.Add(Path.GetFullPath(request.SoundFontPath));
        result.ArgumentList.Add(_bassNativeDirectory);
        result.ArgumentList.Add(temporaryOutputPath);
        result.ArgumentList.Add(request.MaximumSampleVoicesPerStream.ToString(CultureInfo.InvariantCulture));
        AddMasterSettings(result, request.MasterVolumeDecibels);
        return result;
    }

    private ProcessStartInfo CreateStartInfo()
    {
        ProcessStartInfo result = new()
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        if (string.Equals(Path.GetExtension(_workerPath), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            result.FileName = "dotnet";
            result.ArgumentList.Add(_workerPath);
        }
        else
        {
            result.FileName = _workerPath;
        }
        return result;
    }

    private static void AddMasterSettings(ProcessStartInfo startInfo, float volumeDecibels)
    {
        startInfo.ArgumentList.Add(volumeDecibels.ToString("R", CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("50");
        startInfo.ArgumentList.Add("1");
    }

    private static Process Start(ProcessStartInfo startInfo) =>
        Process.Start(startInfo)
        ?? throw new InvalidOperationException("Could not start the Midora Native AOT audio worker process.");

    private static void ValidateCommon(
        string soundFontPath,
        int sampleRate,
        int maximumSampleVoicesPerStream,
        float masterVolumeDecibels)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(soundFontPath);
        if (!File.Exists(soundFontPath))
        {
            throw new FileNotFoundException("The frozen Project SoundFont does not exist.", soundFontPath);
        }
        if (sampleRate is < 8_000 or > 192_000)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }
        if (maximumSampleVoicesPerStream is < 1 or > BassMidiPolyphonyConfiguration.MaximumSampleVoiceCount)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSampleVoicesPerStream));
        }
        if (!float.IsFinite(masterVolumeDecibels) || masterVolumeDecibels > 0)
        {
            throw new ArgumentOutOfRangeException(nameof(masterVolumeDecibels));
        }
    }

    private static AudioFileRenderWorkerFailureStage CurrentStage(AudioWorkerState state) =>
        state switch
        {
            AudioWorkerState.Created or AudioWorkerState.Preparing or AudioWorkerState.Prepared =>
                AudioFileRenderWorkerFailureStage.Preparing,
            AudioWorkerState.Finalizing or AudioWorkerState.Completed =>
                AudioFileRenderWorkerFailureStage.Finalizing,
            _ => AudioFileRenderWorkerFailureStage.Rendering
        };

    private static IReadOnlyList<string> CleanupWorkerIntermediates(string? temporaryOutputPath)
    {
        if (temporaryOutputPath is null)
        {
            return Array.Empty<string>();
        }
        string? directory = Path.GetDirectoryName(temporaryOutputPath);
        if (directory is null || !Directory.Exists(directory))
        {
            return Array.Empty<string>();
        }
        string prefix = "." + Path.GetFileName(temporaryOutputPath) + ".";
        List<string> residual = [];
        foreach (string candidate in Directory.EnumerateFiles(directory))
        {
            string name = Path.GetFileName(candidate);
            if (!name.StartsWith(prefix, StringComparison.Ordinal)
                || !name.EndsWith(".tmp", StringComparison.Ordinal))
            {
                continue;
            }
            try
            {
                File.Delete(candidate);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                residual.Add(candidate);
            }
        }
        return residual;
    }

    private static void CleanupOwnedDirectory(string directory)
    {
        try
        {
            string fullPath = Path.GetFullPath(directory);
            string expectedPrefix = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(fullPath).StartsWith("midora-audio-file-worker-", StringComparison.Ordinal))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The worker output result remains primary; stale owned plans can be removed on startup.
        }
    }
}
