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
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(soundFontPath);
        ArgumentNullException.ThrowIfNull(rendererSettings);
        ArgumentNullException.ThrowIfNull(masterSettings);
        ValidateCommon(workerPath, bassNativeDirectory, deviceBufferRequestMilliseconds, preparingTimeout);
        if (renderAheadMilliseconds is < 20 or > 2_000)
        {
            throw new ArgumentOutOfRangeException(nameof(renderAheadMilliseconds));
        }

        _ownedTemporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"midora-audio-worker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_ownedTemporaryDirectory);
        string planPath = Path.Combine(_ownedTemporaryDirectory, "compiled-audio-plan.mdap");
        MidiRenderPlanFile.Write(planPath, plan);
        _control = SharedAudioWorkerControl.Create($"Midora.Audio.Control.{Guid.NewGuid():N}");
        Process? startedProcess = null;
        Thread? startedMonitorThread = null;
        try
        {
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
            startedMonitorThread = new Thread(MonitorProcess)
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
                if (!startedProcess.HasExited)
                {
                    startedProcess.Kill(entireProcessTree: true);
                    startedProcess.WaitForExit();
                }
                startedMonitorThread?.Join();
                startedProcess.Dispose();
            }
            _control.Dispose();
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
        ValidateCommon(workerPath, bassNativeDirectory, deviceBufferRequestMilliseconds, timeout);
        using SharedAudioWorkerControl control = SharedAudioWorkerControl.Create(
            $"Midora.Audio.Probe.{Guid.NewGuid():N}");
        using Process process = StartProbe(
            workerPath,
            control.Name,
            bassNativeDirectory,
            deviceId,
            deviceBufferRequestMilliseconds);
        long deadline = Environment.TickCount64 + checked((long)timeout.TotalMilliseconds);
        while (true)
        {
            AudioWorkerStatus status = control.ReadStatus();
            if (status.State == AudioWorkerState.Prepared)
            {
                if (!process.WaitForExit(RemainingMilliseconds(deadline)))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                    throw new TimeoutException("The audio worker probe did not exit within the Preparing timeout.");
                }
                string error = process.StandardError.ReadToEnd();
                _ = process.StandardOutput.ReadToEnd();
                if (process.ExitCode != 0)
                {
                    throw new MidoraAudioException(
                        $"The audio worker probe exited with code {process.ExitCode}: {error}");
                }
                return new(status.ActualSampleRate, status.ActualDeviceBufferFrameCount);
            }
            if (status.State == AudioWorkerState.Faulted || process.HasExited)
            {
                string error = process.StandardError.ReadToEnd();
                _ = process.StandardOutput.ReadToEnd();
                throw new MidoraAudioException(
                    $"The audio worker probe failed; fault={status.FaultCode}; exitCode={process.ExitCode}; stderr={error}");
            }
            if (Environment.TickCount64 >= deadline)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
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
        if (_disposed || _process.HasExited)
        {
            return;
        }
        if (!_control.TryEnqueueStop(flush))
        {
            throw new InvalidOperationException("The bounded audio worker command ring is full.");
        }
        if (!_process.WaitForExit(checked((int)timeout.TotalMilliseconds)))
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit();
            throw new TimeoutException("The audio worker did not stop within the requested timeout.");
        }
        _monitorThread.Join();
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
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit();
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
                _monitorThread.Join();
                throw new MidoraAudioException(
                    $"The audio worker failed during Preparing; fault={status.FaultCode}; exitCode={ExitCode}; stderr={StandardError}");
            }
            if (Environment.TickCount64 >= deadline)
            {
                _process.Kill(entireProcessTree: true);
                _monitorThread.Join();
                throw new TimeoutException("The audio worker did not start within the Preparing timeout.");
            }
            Thread.Sleep(1);
        }
    }

    private void MonitorProcess()
    {
        _process.WaitForExit();
        _standardError = _process.StandardError.ReadToEnd();
        _ = _process.StandardOutput.ReadToEnd();
        Volatile.Write(ref _exitCode, _process.ExitCode);
    }

    private static int RemainingMilliseconds(long deadline)
    {
        long remaining = deadline - Environment.TickCount64;
        return remaining <= 0 ? 0 : checked((int)Math.Min(remaining, int.MaxValue));
    }

    private static void ValidateCommon(
        string workerPath,
        string nativeDirectory,
        int deviceBufferRequestMilliseconds,
        TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeDirectory);
        if (!File.Exists(workerPath))
        {
            throw new FileNotFoundException("The Midora audio worker was not found.", workerPath);
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
