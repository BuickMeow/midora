using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;

namespace Midora.Audio.Bass;

public readonly record struct BassMidiAudioWorkerProbeResult(
    int ActualSampleRate,
    int ActualDeviceBufferFrameCount);

[SupportedOSPlatform("windows")]
public sealed class BassMidiAudioWorkerSession : IDisposable
{
    private readonly string _ownedTemporaryDirectory;
    private readonly SharedAudioWorkerControl _control;
    private readonly Process _process;
    private readonly Thread _monitorThread;
    private string? _standardError;
    private int _exitCode = int.MinValue;
    private int _monitorFaulted;
    private bool _disposed;

    public BassMidiAudioWorkerSession(
        MidiRenderPlan plan,
        string soundFontPath,
        BassMidiRendererSettings rendererSettings,
        AudioMasterSettings masterSettings,
        int renderAheadMilliseconds,
        int deviceBufferRequestMilliseconds,
        string? deviceId,
        string workerPath,
        string bassNativeDirectory,
        TimeSpan preparingTimeout)
        : this(
            plan,
            soundFontPath,
            rendererSettings,
            masterSettings,
            renderAheadMilliseconds,
            deviceBufferRequestMilliseconds,
            deviceId,
            workerPath,
            bassNativeDirectory,
            preparingTimeout,
            allowManagedTestWorker: false)
    {
    }

    internal BassMidiAudioWorkerSession(
        MidiRenderPlan plan,
        string soundFontPath,
        BassMidiRendererSettings rendererSettings,
        AudioMasterSettings masterSettings,
        int renderAheadMilliseconds,
        int deviceBufferRequestMilliseconds,
        string? deviceId,
        string workerPath,
        string bassNativeDirectory,
        TimeSpan preparingTimeout,
        bool allowManagedTestWorker)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(soundFontPath);
        ArgumentNullException.ThrowIfNull(rendererSettings);
        ArgumentNullException.ThrowIfNull(masterSettings);
        ValidateCommon(
            workerPath,
            bassNativeDirectory,
            deviceBufferRequestMilliseconds,
            preparingTimeout,
            allowManagedTestWorker);
        if (renderAheadMilliseconds is < 20 or > 2_000)
        {
            throw new ArgumentOutOfRangeException(nameof(renderAheadMilliseconds));
        }
        InitialReleaseAudioWorkerProtocolPolicy.ValidateRealtimeSettings(
            rendererSettings,
            masterSettings,
            renderAheadMilliseconds,
            deviceBufferRequestMilliseconds);
        if (!File.Exists(soundFontPath))
        {
            throw new FileNotFoundException(
                "The frozen Project SoundFont does not exist.",
                soundFontPath);
        }
        workerPath = Path.GetFullPath(workerPath);
        bassNativeDirectory = Path.GetFullPath(bassNativeDirectory);
        soundFontPath = Path.GetFullPath(soundFontPath);

        _ownedTemporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"midora-audio-worker-{Guid.NewGuid():N}");
        SharedAudioWorkerControl? createdControl = null;
        Process? startedProcess = null;
        Thread? startedMonitorThread = null;
        try
        {
            Directory.CreateDirectory(_ownedTemporaryDirectory);
            string planPath = Path.Combine(_ownedTemporaryDirectory, "compiled-audio-plan.mdap");
            MidiRenderPlanFile.Write(planPath, plan);
            createdControl = SharedAudioWorkerControl.Create(
                $"Midora.Audio.Control.{Guid.NewGuid():N}");
            _control = createdControl;
            ProcessStartInfo startInfo = CreateStartInfo(workerPath);
            AddPlaybackArguments(
                startInfo,
                _control.Name,
                planPath,
                soundFontPath,
                bassNativeDirectory,
                deviceId,
                renderAheadMilliseconds,
                deviceBufferRequestMilliseconds,
                rendererSettings,
                masterSettings,
                plan.SampleRate);
            startedProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the Midora audio worker process.");
            _process = startedProcess;
            Task<string> standardError = startedProcess.StandardError.ReadToEndAsync();
            Task<string> standardOutput = startedProcess.StandardOutput.ReadToEndAsync();
            startedMonitorThread = new Thread(
                () => MonitorProcess(standardError, standardOutput))
            {
                IsBackground = true,
                Name = "Midora Audio Worker Monitor"
            };
            _monitorThread = startedMonitorThread;
            _monitorThread.Start();
            WaitUntilPlaybackStarted(preparingTimeout);
        }
        catch
        {
            if (startedProcess is not null)
            {
                TerminateProcess(startedProcess);
                if (startedMonitorThread is { IsAlive: true })
                {
                    startedMonitorThread.Join();
                }
                startedProcess.Dispose();
            }
            createdControl?.Dispose();
            CleanupOwnedTemporaryDirectory(_ownedTemporaryDirectory);
            throw;
        }
    }

    public AudioWorkerStatus Status => _control.ReadStatus();

    public int? ExitCode => Volatile.Read(ref _exitCode) == int.MinValue
        ? null
        : Volatile.Read(ref _exitCode);

    public string? StandardError => _standardError;

    public static BassMidiAudioWorkerProbeResult Probe(
        string workerPath,
        string bassNativeDirectory,
        string? deviceId,
        int deviceBufferRequestMilliseconds,
        TimeSpan timeout)
    {
        ValidateCommon(
            workerPath,
            bassNativeDirectory,
            deviceBufferRequestMilliseconds,
            timeout,
            allowManagedTestWorker: false);
        workerPath = Path.GetFullPath(workerPath);
        bassNativeDirectory = Path.GetFullPath(bassNativeDirectory);
        using SharedAudioWorkerControl control = SharedAudioWorkerControl.Create(
            $"Midora.Audio.Probe.{Guid.NewGuid():N}");
        using Process process = StartProbe(
            workerPath,
            control.Name,
            bassNativeDirectory,
            deviceId,
            deviceBufferRequestMilliseconds);
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        long deadline = Environment.TickCount64 + checked((long)timeout.TotalMilliseconds);
        while (true)
        {
            AudioWorkerStatus status = control.ReadStatus();
            if (status.State == AudioWorkerState.Prepared)
            {
                if (!process.WaitForExit(RemainingMilliseconds(deadline)))
                {
                    TerminateProcess(process);
                    _ = standardError.GetAwaiter().GetResult();
                    _ = standardOutput.GetAwaiter().GetResult();
                    throw new TimeoutException("The audio worker probe did not exit within the Preparing timeout.");
                }
                string error = standardError.GetAwaiter().GetResult();
                _ = standardOutput.GetAwaiter().GetResult();
                if (process.ExitCode != 0)
                {
                    throw new MidoraAudioException(
                        $"The audio worker probe exited with code {process.ExitCode}: {error}");
                }
                return new(status.ActualSampleRate, status.ActualDeviceBufferFrameCount);
            }
            if (status.State == AudioWorkerState.Faulted || process.HasExited)
            {
                if (!process.WaitForExit(RemainingMilliseconds(deadline)))
                {
                    TerminateProcess(process);
                }
                string error = standardError.GetAwaiter().GetResult();
                _ = standardOutput.GetAwaiter().GetResult();
                throw new MidoraAudioException(
                    $"The audio worker probe failed; fault={status.FaultCode}; exitCode={process.ExitCode}; stderr={error}");
            }
            if (Environment.TickCount64 >= deadline)
            {
                TerminateProcess(process);
                _ = standardError.GetAwaiter().GetResult();
                _ = standardOutput.GetAwaiter().GetResult();
                throw new TimeoutException("The audio worker probe did not complete within the Preparing timeout.");
            }
            Thread.Sleep(1);
        }
    }

    public void EnqueueMonitoringCommands(ReadOnlySpan<MidiMonitoringCommand> commands)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_control.TryEnqueueMonitoringCommands(commands))
        {
            throw new InvalidOperationException("The bounded audio worker command ring is full.");
        }
    }

    public void Stop(bool flush, TimeSpan timeout)
    {
        if (_disposed)
        {
            return;
        }
        if (!_process.HasExited)
        {
            if (!_control.TryEnqueueStop(flush))
            {
                throw new InvalidOperationException("The bounded audio worker command ring is full.");
            }
            if (!_process.WaitForExit(checked((int)timeout.TotalMilliseconds)))
            {
                TerminateProcess(_process);
                _monitorThread.Join();
                throw new TimeoutException("The audio worker did not stop within the requested timeout.");
            }
        }
        _monitorThread.Join();
        AudioWorkerStatus status = Status;
        if (!IsSuccessfulTerminalExit(status.State, ExitCode))
        {
            throw new MidoraAudioException(
                $"The audio worker did not stop cleanly; state={status.State}; fault={status.FaultCode}; exitCode={ExitCode}; stderr={StandardError}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (!_process.HasExited)
        {
            if (!_control.TryEnqueueStop() || !_process.WaitForExit(5_000))
            {
                TerminateProcess(_process);
            }
        }
        _monitorThread.Join();
        _process.Dispose();
        _control.Dispose();
        CleanupOwnedTemporaryDirectory(_ownedTemporaryDirectory);
    }

    private static Process StartProbe(
        string workerPath,
        string controlName,
        string nativeDirectory,
        string? deviceId,
        int deviceBufferRequestMilliseconds)
    {
        ProcessStartInfo startInfo = CreateStartInfo(workerPath);
        startInfo.ArgumentList.Add("probe");
        startInfo.ArgumentList.Add(controlName);
        startInfo.ArgumentList.Add(nativeDirectory);
        startInfo.ArgumentList.Add(deviceId ?? string.Empty);
        startInfo.ArgumentList.Add(deviceBufferRequestMilliseconds.ToString(CultureInfo.InvariantCulture));
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the Midora audio worker probe.");
    }

    private static ProcessStartInfo CreateStartInfo(string workerPath)
    {
        ProcessStartInfo result = new()
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        if (string.Equals(Path.GetExtension(workerPath), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            result.FileName = "dotnet";
            result.ArgumentList.Add(workerPath);
        }
        else
        {
            result.FileName = workerPath;
        }
        return result;
    }

    private static void AddPlaybackArguments(
        ProcessStartInfo startInfo,
        string controlName,
        string planPath,
        string soundFontPath,
        string nativeDirectory,
        string? deviceId,
        int renderAheadMilliseconds,
        int deviceBufferRequestMilliseconds,
        BassMidiRendererSettings rendererSettings,
        AudioMasterSettings masterSettings,
        int expectedSampleRate)
    {
        startInfo.ArgumentList.Add("play");
        startInfo.ArgumentList.Add(controlName);
        startInfo.ArgumentList.Add(planPath);
        startInfo.ArgumentList.Add(soundFontPath);
        startInfo.ArgumentList.Add(nativeDirectory);
        startInfo.ArgumentList.Add(deviceId ?? string.Empty);
        startInfo.ArgumentList.Add(renderAheadMilliseconds.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(deviceBufferRequestMilliseconds.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(rendererSettings.MaximumSampleVoiceCount.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(rendererSettings.MaximumWorkFrameCount.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(masterSettings.VolumeDecibels.ToString("R", CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(masterSettings.LimiterCeiling.ToString("R", CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(masterSettings.LimiterReleaseMilliseconds.ToString("R", CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(masterSettings.LimiterEnabled ? "1" : "0");
        startInfo.ArgumentList.Add(expectedSampleRate.ToString(CultureInfo.InvariantCulture));
    }

    private void WaitUntilPlaybackStarted(TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + checked((long)timeout.TotalMilliseconds);
        while (true)
        {
            AudioWorkerStatus status = Status;
            if (status.State is AudioWorkerState.Playing
                or AudioWorkerState.Buffering
                or AudioWorkerState.Completed)
            {
                return;
            }
            if (status.State == AudioWorkerState.Faulted || ExitCode is not null)
            {
                if (!_process.WaitForExit(RemainingMilliseconds(deadline)))
                {
                    TerminateProcess(_process);
                }
                _monitorThread.Join();
                throw new MidoraAudioException(
                    $"The audio worker failed during Preparing; fault={status.FaultCode}; exitCode={ExitCode}; stderr={StandardError}");
            }
            if (Volatile.Read(ref _monitorFaulted) != 0)
            {
                throw new MidoraAudioException(
                    $"The audio worker monitor failed during Preparing: {StandardError}");
            }
            if (Environment.TickCount64 >= deadline)
            {
                TerminateProcess(_process);
                _monitorThread.Join();
                throw new TimeoutException("The audio worker did not start within the Preparing timeout.");
            }
            Thread.Sleep(1);
        }
    }

    private void MonitorProcess(
        Task<string> standardError,
        Task<string> standardOutput)
    {
        try
        {
            _process.WaitForExit();
            _standardError = standardError.GetAwaiter().GetResult();
            _ = standardOutput.GetAwaiter().GetResult();
            Volatile.Write(ref _exitCode, _process.ExitCode);
        }
        catch (Exception exception)
        {
            _standardError = $"The audio worker monitor failed: {exception}";
            Volatile.Write(ref _monitorFaulted, 1);
        }
    }

    private static int RemainingMilliseconds(long deadline)
    {
        long remaining = deadline - Environment.TickCount64;
        return remaining <= 0 ? 0 : checked((int)Math.Min(remaining, int.MaxValue));
    }

    private static void TerminateProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            // The child exited between HasExited and Kill.
        }
        process.WaitForExit();
    }

    private static void ValidateCommon(
        string workerPath,
        string nativeDirectory,
        int deviceBufferRequestMilliseconds,
        TimeSpan timeout,
        bool allowManagedTestWorker)
    {
        ValidateWorkerLaunchPath(workerPath, allowManagedTestWorker);
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeDirectory);
        if (!Directory.Exists(nativeDirectory))
        {
            throw new DirectoryNotFoundException(nativeDirectory);
        }
        if (deviceBufferRequestMilliseconds is < 5 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(deviceBufferRequestMilliseconds));
        }
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }

    internal static void ValidateWorkerLaunchPath(string workerPath, bool allowManagedTestWorker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerPath);
        if (!File.Exists(workerPath))
        {
            throw new FileNotFoundException("The Midora Native AOT audio worker was not found.", workerPath);
        }

        string extension = Path.GetExtension(workerPath);
        if (!string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)
            && !(allowManagedTestWorker
                && string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "Formal realtime playback requires the win-x64 Native AOT .exe worker; managed .dll launch is test-only.");
        }
    }

    internal static bool IsSuccessfulTerminalExit(AudioWorkerState state, int? exitCode) =>
        exitCode == 0
        && state is AudioWorkerState.Stopped or AudioWorkerState.Completed;

    private static void CleanupOwnedTemporaryDirectory(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            string expectedPrefix = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(fullPath).StartsWith("midora-audio-worker-", StringComparison.Ordinal))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
        catch
        {
            // The playback result remains primary; startup cleanup can remove stale owned directories later.
        }
    }
}
