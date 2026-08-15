using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Midora.AudioDevice;

namespace Midora.Audio.Bass;

[SupportedOSPlatform("windows")]
internal sealed class PersistentBassMidiAudioWorkerHost : IDisposable
{
    private readonly object _sync = new();
    private readonly string _ownedDirectory;
    private readonly SharedAudioWorkerControl _control;
    private readonly Process _process;
    private readonly Task<string> _standardError;
    private readonly Task<string> _standardOutput;
    private readonly TimeSpan _defaultTimeout;
    private long _nextGeneration;
    private long _activePlaybackGeneration;
    private bool _disposed;

    public PersistentBassMidiAudioWorkerHost(
        string workerPath,
        string bassNativeDirectory,
        string soundFontPath,
        TimeSpan preparingTimeout,
        bool allowManagedTestWorker = false)
    {
        BassMidiAudioWorkerSession.ValidateWorkerLaunchPath(workerPath, allowManagedTestWorker);
        ArgumentException.ThrowIfNullOrWhiteSpace(bassNativeDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(soundFontPath);
        if (preparingTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(preparingTimeout));
        }
        string nativeDirectory = Path.GetFullPath(bassNativeDirectory);
        string fontPath = Path.GetFullPath(soundFontPath);
        if (!Directory.Exists(nativeDirectory))
        {
            throw new DirectoryNotFoundException(nativeDirectory);
        }
        if (!File.Exists(fontPath))
        {
            throw new FileNotFoundException("The verified Project SoundFont does not exist.", fontPath);
        }

        WorkerPath = Path.GetFullPath(workerPath);
        NativeDirectory = nativeDirectory;
        SoundFontPath = fontPath;
        _defaultTimeout = preparingTimeout;
        _ownedDirectory = Path.Combine(
            Path.GetTempPath(),
            $"midora-audio-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_ownedDirectory);
        _control = SharedAudioWorkerControl.Create($"Midora.Audio.Host.{Guid.NewGuid():N}");
        try
        {
            ProcessStartInfo startInfo = CreateStartInfo(WorkerPath);
            startInfo.ArgumentList.Add("realtime-host");
            startInfo.ArgumentList.Add(_control.Name);
            startInfo.ArgumentList.Add(_ownedDirectory);
            startInfo.ArgumentList.Add(SoundFontPath);
            startInfo.ArgumentList.Add(NativeDirectory);
            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the persistent Midora audio worker.");
            _standardError = _process.StandardError.ReadToEndAsync();
            _standardOutput = _process.StandardOutput.ReadToEndAsync();
            PersistentAudioWorkerResponse ready = WaitForResponse(0, preparingTimeout);
            if (!ready.Succeeded)
            {
                throw new MidoraAudioException(
                    "The persistent audio worker failed to initialize: " + ready.Error);
            }
        }
        catch
        {
            DisposeAfterConstructionFailure();
            throw;
        }
    }

    public string WorkerPath { get; }
    public string NativeDirectory { get; }
    public string SoundFontPath { get; }
    public SharedAudioWorkerControl Control => _control;
    public AudioWorkerStatus Status => _control.ReadStatus();
    public bool HasExited => _process.HasExited;
    internal int ProcessId => _process.Id;

    public string StandardError
    {
        get
        {
            if (!_process.HasExited)
            {
                return string.Empty;
            }
            return _standardError.IsCompletedSuccessfully ? _standardError.Result : string.Empty;
        }
    }

    public BassMidiAudioWorkerProbeResult Probe(
        string? deviceId,
        int deviceBufferRequestMilliseconds)
    {
        lock (_sync)
        {
            RequireIdle();
            long generation = BeginRequest(
                [deviceId ?? string.Empty, deviceBufferRequestMilliseconds.ToString(CultureInfo.InvariantCulture)],
                _control.TryEnqueuePersistentProbe);
            PersistentAudioWorkerResponse response = WaitForResponse(generation, _defaultTimeout);
            CompleteExchange(generation);
            if (!response.Succeeded)
            {
                throw new MidoraAudioDeviceException(response.Error);
            }
            return new(response.ActualSampleRate, response.ActualDeviceBufferFrameCount);
        }
    }

    public long StartPlayback(IReadOnlyList<string> arguments)
    {
        lock (_sync)
        {
            RequireIdle();
            long generation = BeginRequest(
                arguments,
                _control.TryEnqueuePersistentStartPlayback);
            _activePlaybackGeneration = generation;
            WaitForPlaybackAcceptance(generation, _defaultTimeout);
            return generation;
        }
    }

    public PersistentAudioWorkerResponse CompletePlayback(long generation, TimeSpan timeout)
    {
        lock (_sync)
        {
            if (_activePlaybackGeneration != generation)
            {
                throw new InvalidOperationException("The persistent audio worker playback generation is not active.");
            }
            PersistentAudioWorkerResponse response = WaitForResponse(generation, timeout);
            _activePlaybackGeneration = 0;
            CompleteExchange(generation);
            return response;
        }
    }

    public void CancelActivePlayback(TimeSpan timeout)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activePlaybackGeneration == 0)
            {
                return;
            }
            long generation = _activePlaybackGeneration;
            if (!PersistentAudioWorkerExchange.TryReadResponse(
                    _ownedDirectory,
                    generation,
                    out _)
                && !_control.TryEnqueueStop())
            {
                throw new InvalidOperationException(
                    "The persistent audio worker command ring is full while cancelling playback.");
            }
            _ = WaitForResponse(generation, timeout);
            _activePlaybackGeneration = 0;
            CompleteExchange(generation);
        }
    }

    public void BeginPitchAudition(
        string? deviceId,
        int deviceBufferRequestMilliseconds,
        int pitch,
        int velocity)
    {
        if (pitch is < 0 or > 127 || velocity is < 1 or > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(pitch));
        }
        ExecuteIdleCommand(
            [
                deviceId ?? string.Empty,
                deviceBufferRequestMilliseconds.ToString(CultureInfo.InvariantCulture),
                pitch.ToString(CultureInfo.InvariantCulture),
                velocity.ToString(CultureInfo.InvariantCulture)
            ],
            _control.TryEnqueuePitchAuditionNoteOn);
    }

    public void EndPitchAudition() => ExecuteIdleCommand(
        [],
        _control.TryEnqueuePitchAuditionNoteOff);

    private void ExecuteIdleCommand(
        IReadOnlyList<string> arguments,
        Func<long, bool> enqueue)
    {
        lock (_sync)
        {
            RequireIdle();
            long generation = BeginRequest(arguments, enqueue);
            PersistentAudioWorkerResponse response = WaitForResponse(generation, _defaultTimeout);
            CompleteExchange(generation);
            if (!response.Succeeded)
            {
                throw new MidoraAudioException(response.Error);
            }
        }
    }

    private long BeginRequest(IReadOnlyList<string> arguments, Func<long, bool> enqueue)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process.HasExited)
        {
            throw WorkerExitedException();
        }
        long generation = checked(++_nextGeneration);
        PersistentAudioWorkerExchange.WriteRequest(_ownedDirectory, generation, arguments);
        if (!enqueue(generation))
        {
            PersistentAudioWorkerExchange.DeleteExchange(_ownedDirectory, generation);
            throw new InvalidOperationException("The persistent audio worker command ring is full.");
        }
        return generation;
    }

    private PersistentAudioWorkerResponse WaitForResponse(long generation, TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + checked((long)Math.Ceiling(timeout.TotalMilliseconds));
        while (true)
        {
            if (PersistentAudioWorkerExchange.TryReadResponse(
                _ownedDirectory,
                generation,
                out PersistentAudioWorkerResponse response))
            {
                return response;
            }
            if (_process.HasExited)
            {
                throw WorkerExitedException();
            }
            if (Environment.TickCount64 >= deadline)
            {
                throw new TimeoutException("The persistent audio worker did not respond within the requested timeout.");
            }
            Thread.Sleep(1);
        }
    }

    private void WaitForPlaybackAcceptance(long generation, TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + checked((long)Math.Ceiling(timeout.TotalMilliseconds));
        while (true)
        {
            if (PersistentAudioWorkerExchange.HasAcceptance(_ownedDirectory, generation))
            {
                return;
            }
            if (PersistentAudioWorkerExchange.TryReadResponse(
                    _ownedDirectory,
                    generation,
                    out PersistentAudioWorkerResponse response))
            {
                _activePlaybackGeneration = 0;
                CompleteExchange(generation);
                throw new MidoraAudioException(
                    response.Succeeded
                        ? "The persistent audio worker completed without accepting the playback task."
                        : response.Error);
            }
            if (_process.HasExited)
            {
                throw WorkerExitedException();
            }
            if (Environment.TickCount64 >= deadline)
            {
                throw new TimeoutException(
                    "The persistent audio worker did not accept playback within the Preparing timeout.");
            }
            Thread.Sleep(1);
        }
    }

    private Exception WorkerExitedException()
    {
        _process.WaitForExit();
        string error = _standardError.GetAwaiter().GetResult();
        _ = _standardOutput.GetAwaiter().GetResult();
        return new MidoraAudioException(
            $"The persistent audio worker exited unexpectedly with code {_process.ExitCode}: {error}");
    }

    private void RequireIdle()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_activePlaybackGeneration != 0)
        {
            throw new InvalidOperationException("A persistent audio worker playback task is already active.");
        }
    }

    private void CompleteExchange(long generation) =>
        PersistentAudioWorkerExchange.DeleteExchange(_ownedDirectory, generation);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            try
            {
                if (!_process.HasExited)
                {
                    if (_activePlaybackGeneration != 0)
                    {
                        _ = _control.TryEnqueueStop();
                        try
                        {
                            _ = WaitForResponse(_activePlaybackGeneration, TimeSpan.FromSeconds(5));
                        }
                        catch
                        {
                            // Forced process termination below is the final cleanup boundary.
                        }
                        _activePlaybackGeneration = 0;
                    }
                    long generation = checked(++_nextGeneration);
                    PersistentAudioWorkerExchange.WriteRequest(_ownedDirectory, generation, []);
                    if (_control.TryEnqueuePersistentShutdown(generation))
                    {
                        try
                        {
                            _ = WaitForResponse(generation, TimeSpan.FromSeconds(5));
                        }
                        catch
                        {
                            // Forced process termination below is the final cleanup boundary.
                        }
                    }
                }
            }
            finally
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
                _process.WaitForExit();
                _process.Dispose();
                _control.Dispose();
                TryDeleteOwnedDirectory();
            }
        }
    }

    private void DisposeAfterConstructionFailure()
    {
        try
        {
            if (_process is not null && !_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit();
            }
            _process?.Dispose();
        }
        catch
        {
        }
        _control.Dispose();
        TryDeleteOwnedDirectory();
    }

    private void TryDeleteOwnedDirectory()
    {
        try
        {
            string fullPath = Path.GetFullPath(_ownedDirectory);
            string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))
                + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(fullPath).StartsWith("midora-audio-host-", StringComparison.Ordinal))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
        catch
        {
        }
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
}
