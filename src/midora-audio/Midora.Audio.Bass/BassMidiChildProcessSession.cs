using Midora.AudioDevice;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;

namespace Midora.Audio.Bass;

[SupportedOSPlatform("windows")]
internal sealed unsafe class BassMidiChildProcessSession : IAudioRenderSource, IDisposable
{
    private const int MonitoringProtocolMagic = 0x4d43444d;
    private const int MonitoringProtocolVersion = 1;
    private readonly string _ownedTemporaryDirectory;
    private readonly SharedAudioFrameRingBuffer _ring;
    private readonly int _producerWorkFrameCount;
    private Process? _process;
    private Thread? _monitorThread;
    private readonly BassMidiChildConsumptionMode _consumptionMode;
    private readonly object _controlWriteSync = new();
    private NamedPipeServerStream? _controlPipe;
    private BinaryWriter? _controlWriter;
    private string? _standardError;
    private int _exitCode = int.MinValue;
    private bool _disposed;

    public BassMidiChildProcessSession(
        MidiRenderPlan plan,
        string soundFontPath,
        BassMidiRendererSettings rendererSettings,
        AudioMasterSettings masterSettings,
        int ipcAudioBufferMilliseconds,
        string workerPath,
        string bassNativeDirectory,
        BassMidiChildConsumptionMode consumptionMode,
        TimeSpan preparingTimeout)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(soundFontPath);
        ArgumentNullException.ThrowIfNull(rendererSettings);
        ArgumentNullException.ThrowIfNull(masterSettings);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(bassNativeDirectory);
        if (ipcAudioBufferMilliseconds is < 20 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(ipcAudioBufferMilliseconds));
        }

        if (!File.Exists(workerPath))
        {
            throw new FileNotFoundException("The Midora audio worker was not found.", workerPath);
        }

        if (preparingTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(preparingTimeout));
        }

        _ownedTemporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"midora-audio-ipc-{Guid.NewGuid():N}");
        _consumptionMode = consumptionMode;
        Directory.CreateDirectory(_ownedTemporaryDirectory);
        string planPath = Path.Combine(_ownedTemporaryDirectory, "compiled-audio-plan.mdap");
        MidiRenderPlanFile.Write(planPath, plan);

        string mapName = $"Midora.Audio.{Guid.NewGuid():N}";
        string controlPipeName = $"Midora.Audio.Control.{Guid.NewGuid():N}";
        AudioFormat format = new(plan.SampleRate, 2, AudioSampleFormat.Float32);
        int capacityFrames = InitialReleaseAudioRuntimePolicy.BufferMillisecondsToFrameCapacity(
            plan.SampleRate,
            ipcAudioBufferMilliseconds);
        _producerWorkFrameCount = Math.Min(rendererSettings.MaximumWorkFrameCount, capacityFrames);

        _ring = SharedAudioFrameRingBuffer.Create(mapName, format, capacityFrames);
        try
        {
            _controlPipe = new NamedPipeServerStream(
                controlPipeName,
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            ProcessStartInfo startInfo = CreateStartInfo(workerPath);
            AddWorkerArguments(
                startInfo,
                mapName,
                controlPipeName,
                planPath,
                soundFontPath,
                bassNativeDirectory,
                rendererSettings,
                masterSettings);
            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the Midora audio worker process.");
            _monitorThread = new Thread(MonitorProcess)
            {
                IsBackground = true,
                Name = "Midora Audio Worker Monitor"
            };
            _monitorThread.Start();
            using (CancellationTokenSource connectionTimeout = new(preparingTimeout))
            {
                _controlPipe.WaitForConnectionAsync(connectionTimeout.Token).GetAwaiter().GetResult();
            }
            _controlWriter = new BinaryWriter(_controlPipe, Encoding.UTF8, leaveOpen: true);
            WaitUntilReady(preparingTimeout);
        }
        catch
        {
            if (_process is not null)
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }

                if (_monitorThread is not null && _monitorThread.IsAlive)
                {
                    _monitorThread.Join();
                }

                _process.Dispose();
            }

            _ring.Dispose();
            ReleaseControlPipe();
            CleanupOwnedTemporaryDirectory();
            throw;
        }
    }

    public AudioFormat Format => _ring.Format;

    public bool ProducerReady => _ring.ProducerReady;

    public bool ProducerCompleted => _ring.ProducerCompleted;

    public bool ProducerFaulted => _ring.ProducerFaulted;

    public int AvailableFrameCount => _ring.AvailableFrameCount;

    public int ProducerWorkFrameCount => _producerWorkFrameCount;

    public long ProducedFrameCount => _ring.ProducedFrameCount;

    public long UnderrunCount => _ring.UnderrunCount;

    public long RenderingThreadAllocatedBytes => _ring.ProducerAllocatedBytes;

    public int? ExitCode => Volatile.Read(ref _exitCode) == int.MinValue
        ? null
        : Volatile.Read(ref _exitCode);

    public string? StandardError => _standardError;

    public AudioPullResult PullFrames(float* destination, int requestedFrameCount)
    {
        AudioPullResult result;
        do
        {
            result = _ring.PullFrames(destination, requestedFrameCount);
            if (result.Status == AudioPullStatus.Buffering
                && _consumptionMode == BassMidiChildConsumptionMode.OfflineBlocking)
            {
                Thread.Sleep(1);
            }
        }
        while (result.Status == AudioPullStatus.Buffering
            && _consumptionMode == BassMidiChildConsumptionMode.OfflineBlocking);

        return result;
    }

    public void EnqueueMonitoringCommands(ReadOnlySpan<MidiMonitoringCommand> commands)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (commands.IsEmpty)
        {
            return;
        }

        lock (_controlWriteSync)
        {
            try
            {
                BinaryWriter writer = _controlWriter
                    ?? throw new InvalidOperationException("The child monitoring control channel is unavailable.");
                writer.Write(MonitoringProtocolMagic);
                writer.Write(MonitoringProtocolVersion);
                writer.Write(commands.Length);
                foreach (MidiMonitoringCommand command in commands)
                {
                    writer.Write((byte)command.Kind);
                    writer.Write(command.ZeroBasedPortNumber);
                    writer.Write(command.SourceEnabled);
                    writer.Write((byte)0);
                    writer.Write(command.SourceIndex);
                    writer.Write(command.Message.PackedValue);
                }
                writer.Flush();
            }
            catch (IOException exception)
            {
                if (_process is not null)
                {
                    _ = SpinWait.SpinUntil(() => _process.HasExited, TimeSpan.FromSeconds(1));
                    if (_process.HasExited)
                    {
                        _monitorThread?.Join();
                    }
                }
                throw new MidoraAudioException(
                    $"The child monitoring control channel failed; exitCode={ExitCode}; stderr={StandardError}",
                    exception);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_process is not null && !_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        _monitorThread?.Join();
        _process?.Dispose();
        ReleaseControlPipe();
        _ring.Dispose();
        CleanupOwnedTemporaryDirectory();
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

    private static void AddWorkerArguments(
        ProcessStartInfo startInfo,
        string mapName,
        string controlPipeName,
        string planPath,
        string soundFontPath,
        string nativeDirectory,
        BassMidiRendererSettings rendererSettings,
        AudioMasterSettings masterSettings)
    {
        startInfo.ArgumentList.Add(mapName);
        startInfo.ArgumentList.Add(controlPipeName);
        startInfo.ArgumentList.Add(planPath);
        startInfo.ArgumentList.Add(soundFontPath);
        startInfo.ArgumentList.Add(nativeDirectory);
        startInfo.ArgumentList.Add(((int)rendererSettings.Interpolation).ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(((int)rendererSettings.SampleLoading).ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(rendererSettings.MaximumVoices.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(rendererSettings.CpuLimitPercent.ToString("R", CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(rendererSettings.MaximumWorkFrameCount.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(masterSettings.VolumeDecibels.ToString("R", CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(masterSettings.LimiterCeiling.ToString("R", CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(masterSettings.LimiterReleaseMilliseconds.ToString("R", CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(masterSettings.LimiterEnabled ? "1" : "0");
    }

    private void WaitUntilReady(TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + checked((long)timeout.TotalMilliseconds);
        while (!_ring.ProducerReady && !_ring.ProducerFaulted)
        {
            if (Volatile.Read(ref _exitCode) != int.MinValue)
            {
                throw new MidoraAudioException(
                    $"The audio worker exited during Preparing with code {_exitCode}: {_standardError}");
            }

            if (Environment.TickCount64 >= deadline)
            {
                throw new TimeoutException("The audio worker did not become ready within the Preparing timeout.");
            }

            Thread.Sleep(5);
        }

        if (_ring.ProducerFaulted)
        {
            throw new MidoraAudioException("The audio worker reported a Preparing fault.");
        }
    }

    private void MonitorProcess()
    {
        Process process = _process
            ?? throw new InvalidOperationException("The audio worker process is unavailable.");
        process.WaitForExit();
        _standardError = process.StandardError.ReadToEnd();
        _ = process.StandardOutput.ReadToEnd();
        Volatile.Write(ref _exitCode, process.ExitCode);
        if (!_ring.ProducerCompleted && process.ExitCode != 0)
        {
            _ring.FaultProducer();
        }
    }

    private void CleanupOwnedTemporaryDirectory()
    {
        try
        {
            string fullPath = Path.GetFullPath(_ownedTemporaryDirectory);
            string expectedPrefix = Path.GetFullPath(Path.GetTempPath()) + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(fullPath).StartsWith("midora-audio-ipc-", StringComparison.Ordinal))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
        catch
        {
            // The session result remains primary; startup cleanup can remove stale owned directories later.
        }
    }

    private void ReleaseControlPipe()
    {
        try
        {
            _controlWriter?.Dispose();
        }
        catch (IOException)
        {
        }
        finally
        {
            _controlWriter = null;
        }

        try
        {
            _controlPipe?.Dispose();
        }
        catch (IOException)
        {
        }
        finally
        {
            _controlPipe = null;
        }
    }
}

internal enum BassMidiChildConsumptionMode : byte
{
    RealtimeNonBlocking,
    OfflineBlocking
}
