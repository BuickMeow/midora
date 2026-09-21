using System.Runtime.Versioning;
using Midora.Audio;
using Midora.Audio.Bass;
using Midora.AudioDevice;

namespace Midora.Playback.BassWasapi;

public sealed record BassWasapiChildPlaybackOptions(
    string WorkerPath,
    string BassNativeDirectory,
    string? DeviceId,
    int RenderAheadMilliseconds,
    int DeviceBufferRequestMilliseconds,
    BassMidiRendererSettings RendererSettings,
    AudioMasterSettings MasterSettings,
    TimeSpan PreparingTimeout);

/// <summary>
/// Formal realtime backend shared by every release platform. The main process only coordinates a
/// frozen render plan and a fixed shared-memory control ABI; the worker owns synthesis, limiter,
/// render-ahead and the platform output backend (BASSWASAPI on Windows, CoreAudio on macOS). The
/// worker executable path and native directory are injected through the options.
/// </summary>
public sealed class BassWasapiChildPlaybackBackend
    : IRealtimePlaybackBackend,
      IHeldPreviewRealtimePlaybackBackend,
      IRealtimePlaybackCacheBackend,
      IRealtimePlaybackSoundFontBackend,
      ISimplePitchAuditionRealtimePlaybackBackend,
      IBufferingRecoveryRealtimePlaybackBackend,
      ICancellableRealtimePlaybackPreparationBackend
{
    private readonly BassWasapiChildPlaybackOptions _options;
    private string? _selectedDeviceId;
    private IBassMidiAudioWorkerSession? _session;
    private PersistentBassMidiAudioWorkerHost? _host;
    private SoundFontConfiguration[] _soundFonts = [];
    private string[] _soundFontPaths = [];
    private string? _soundFontSetCacheIdentity;
    private IRealtimePlaybackCacheStore? _audioCache;
    private RealtimePlaybackCacheMode _nextPlaybackCacheMode;
    private AudioCacheSessionStore.AudioRecoverySpool? _nextRecoverySpool;
    private AudioCacheSessionStore.AudioRecoverySpool? _activeRecoverySpool;
    private long _nextRecoveryMemoryFrameCapacity;
    private long _activeRecoveryMemoryFrameCapacity;
    private int _actualSampleRate;
    private int _actualDeviceBufferFrameCount;
    private AudioWorkerStatus _lastStatus;
    private string? _lastStandardError;
    private int? _lastExitCode;
    private bool _disposed;

    public BassWasapiChildPlaybackBackend(BassWasapiChildPlaybackOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _selectedDeviceId = _options.DeviceId;
        if (_options.RenderAheadMilliseconds is < 20 or > 2_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Render-Ahead must be 20-2000 ms.");
        }
        if (_options.DeviceBufferRequestMilliseconds is < 5 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                "Device Buffer Request must be 5-200 ms.");
        }
        if (_options.PreparingTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                "Preparing timeout must be positive.");
        }
        if (_options.RendererSettings is null
            || _options.RendererSettings.MaximumWorkFrameCount
                != InitialReleaseAudioRuntimePolicy.WorkFrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                $"Initial-release child-process work blocks must be {InitialReleaseAudioRuntimePolicy.WorkFrameCount} frames.");
        }
        if (_options.MasterSettings is null
            || _options.MasterSettings.LimiterCeiling
                != AudioMasterSettings.LimiterCeilingV2
            || _options.MasterSettings.LimiterReleaseMilliseconds
                != AudioMasterSettings.LimiterReleaseMillisecondsV2)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                "Initial-release realtime playback requires the fixed limiter algorithm.");
        }
    }

    private AudioWorkerStatus CurrentStatus => _session?.Status ?? _lastStatus;

    private int? CurrentExitCode => _session?.ExitCode ?? _lastExitCode;

    public int ActualSampleRate => _actualSampleRate;

    public int ActualDeviceBufferFrameCount => _actualDeviceBufferFrameCount;

    public long PositionFrames => CurrentStatus.PositionFrame;

    public long RenderPositionFrames => CurrentStatus.RenderPositionFrame;

    public bool IsBuffering => CurrentStatus.State == AudioWorkerState.Buffering;

    public long CallbackAllocatedBytes => CurrentStatus.CallbackAllocatedBytes;

    public long ChildRenderingAllocatedBytes => CurrentStatus.RenderingAllocatedBytes;

    public long UnderrunCount => CurrentStatus.UnderrunCount;

    public bool ChildFaulted => CurrentStatus.State == AudioWorkerState.Faulted;

    public bool IsCompleted => CurrentStatus.State == AudioWorkerState.Completed;

    public bool OutputDeviceSelectionRequired =>
        CurrentStatus.State == AudioWorkerState.OutputDeviceUnavailable;

    public string? OutputDeviceSelectionReason => OutputDeviceSelectionRequired
        ? "The active output device was removed or disabled; the audio output was disconnected."
        : null;

    public bool HasBufferingRecoveryStorage => _activeRecoverySpool is not null
        || _activeRecoveryMemoryFrameCapacity > 0;

    public bool IsFaulted
    {
        get
        {
            (AudioWorkerStatus status, int? exitCode) = ReadCurrentProcessSnapshot();
            return status.State == AudioWorkerState.Faulted
                || IsUnexpectedWorkerTermination(status.State, exitCode);
        }
    }

    public string? FaultDescription
    {
        get
        {
            (AudioWorkerStatus status, int? exitCode) = ReadCurrentProcessSnapshot();
            return status.State == AudioWorkerState.Faulted
                || IsUnexpectedWorkerTermination(status.State, exitCode)
                ? $"workerState={status.State}; fault={status.FaultCode}; childExitCode={exitCode}; stderr={_session?.StandardError ?? _lastStandardError}"
                : null;
        }
    }

    public int Prepare()
        => Prepare(CancellationToken.None);

    public int Prepare(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_session is not null)
        {
            throw new InvalidOperationException("Cannot prepare an active audio worker session.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        BassMidiAudioWorkerProbeResult result;
        try
        {
            result = _soundFontPaths.Length == 0
                ? BassMidiAudioWorkerSession.Probe(
                    _options.WorkerPath,
                    _options.BassNativeDirectory,
                    _selectedDeviceId,
                    _options.DeviceBufferRequestMilliseconds,
                    _options.PreparingTimeout,
                    cancellationToken)
                : EnsurePersistentHost().Probe(
                    _selectedDeviceId,
                    _options.DeviceBufferRequestMilliseconds);
        }
        catch (Exception preparationFailure)
        {
            Exception? cleanupFailure = DiscardPersistentHost();
            if (cleanupFailure is not null)
            {
                throw new AggregateException(
                    "Realtime preparation and persistent Worker cleanup both failed.",
                    preparationFailure,
                    cleanupFailure);
            }
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();
        _actualSampleRate = result.ActualSampleRate;
        _actualDeviceBufferFrameCount = result.ActualDeviceBufferFrameCount;
        return _actualSampleRate;
    }

    public void Start(MidiRenderPlan plan, string soundFontPath, PlaybackMasterConfiguration master)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(plan);
        if (_actualSampleRate == 0 || plan.SampleRate != _actualSampleRate)
        {
            throw new InvalidOperationException("Prepare and a matching sample-domain plan are required.");
        }
        if (_session is not null)
        {
            throw new InvalidOperationException("The audio worker is already active.");
        }
        string normalizedSoundFontPath = Path.GetFullPath(soundFontPath);
        if (_soundFontPaths.Length == 0
            || _soundFontSetCacheIdentity is null
            || !string.Equals(
                normalizedSoundFontPath,
                _soundFontPaths[0],
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Playback requires the current application SoundFont-set cache identity.");
        }

        AudioMasterSettings masterSettings = new(
            master.VolumeDecibels,
            _options.MasterSettings.LimiterCeiling,
            _options.MasterSettings.LimiterReleaseMilliseconds,
            master.LimiterEnabled);
        RealtimePlaybackCacheMode cacheMode = _nextPlaybackCacheMode;
        IRealtimePlaybackCacheStore? cache = cacheMode == RealtimePlaybackCacheMode.Disabled
            ? null
            : _audioCache;
        _nextPlaybackCacheMode = RealtimePlaybackCacheMode.Disabled;
        _activeRecoverySpool = _nextRecoverySpool;
        _nextRecoverySpool = null;
        _activeRecoveryMemoryFrameCapacity = _nextRecoveryMemoryFrameCapacity;
        _nextRecoveryMemoryFrameCapacity = 0;
        try
        {
            // The worker owns the memory-mapped recovery payload while playback is active.
            // Keeping the reservation handle open in this process prevents
            // MemoryMappedFile.CreateFromFile from opening the same path on Windows.
            _activeRecoverySpool?.ReleaseFileHandleForExternalUse();
            _session = new PersistentBassMidiAudioWorkerSession(
                EnsurePersistentHost(),
                plan,
                _soundFontSetCacheIdentity,
                _options.RendererSettings,
                masterSettings,
                _options.RenderAheadMilliseconds,
                _options.DeviceBufferRequestMilliseconds,
                _selectedDeviceId,
                _options.PreparingTimeout,
                cache,
                _activeRecoverySpool?.Path,
                _activeRecoveryMemoryFrameCapacity,
                cacheMode == RealtimePlaybackCacheMode.UnitPcmAndPlaybackSpan);
        }
        catch (Exception startFailure)
        {
            Exception? cleanupFailure = null;
            try
            {
                _host?.Dispose();
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }
            _host = null;
            try
            {
                _activeRecoverySpool?.Dispose();
            }
            catch (Exception exception)
            {
                cleanupFailure = CombineFailures(cleanupFailure, exception);
            }
            _activeRecoverySpool = null;
            _activeRecoveryMemoryFrameCapacity = 0;
            if (cleanupFailure is not null)
            {
                throw new AggregateException(
                    "Realtime playback startup and persistent Worker cleanup both failed.",
                    startFailure,
                    cleanupFailure);
            }
            throw;
        }
    }

    public void SetAudioCacheStore(IRealtimePlaybackCacheStore cacheStore)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(cacheStore);
        if (_session is not null)
        {
            throw new InvalidOperationException(
                "The audio cache store cannot change while the Worker is active.");
        }
        _audioCache = cacheStore;
    }

    public void SetSoundFontSet(
        IReadOnlyList<SoundFontConfiguration> soundFonts,
        string? cacheIdentity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(soundFonts);
        if ((soundFonts.Count == 0) != (cacheIdentity is null))
        {
            throw new ArgumentException(
                "The enabled SoundFont list and cache identity must both be present or both be absent.");
        }
        SoundFontConfiguration[] normalized = soundFonts
            .Select(value => value.Normalize())
            .ToArray();
        string[] normalizedPaths = normalized.Select(value => value.Path).ToArray();
        if (cacheIdentity is not null
            && (cacheIdentity.Length != 64 || cacheIdentity.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))))
        {
            throw new ArgumentException(
                "The SoundFont set cache identity is invalid.",
                nameof(cacheIdentity));
        }
        if (SoundFontConfigurationsEqual(_soundFonts, normalized)
            && string.Equals(_soundFontSetCacheIdentity, cacheIdentity, StringComparison.Ordinal))
        {
            return;
        }
        if (_session is not null)
        {
            throw new InvalidOperationException(
                "The application SoundFont list cannot change while realtime audio is active.");
        }

        _host?.Dispose();
        _host = null;
        _soundFonts = normalized;
        _soundFontPaths = normalizedPaths;
        _soundFontSetCacheIdentity = cacheIdentity;
        _actualSampleRate = 0;
        _actualDeviceBufferFrameCount = 0;
        // SetSoundFontSet only changes the frozen configuration. Application Preferences
        // explicitly follows this with Prepare so the replacement persistent host opens the
        // original files immediately; other callers may still prepare lazily at task startup.
    }

    public void BeginPitchAudition(int pitch, int velocity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_session is not null)
        {
            throw new InvalidOperationException(
                "Pitch audition is unavailable while realtime playback is active.");
        }
        try
        {
            EnsurePersistentHost().BeginPitchAudition(
                _selectedDeviceId,
                _options.DeviceBufferRequestMilliseconds,
                pitch,
                velocity);
        }
        catch (Exception auditionFailure)
        {
            Exception? cleanupFailure = DiscardPersistentHost();
            if (cleanupFailure is not null)
            {
                throw new AggregateException(
                    "Pitch audition startup and persistent Worker cleanup both failed.",
                    auditionFailure,
                    cleanupFailure);
            }
            throw;
        }
    }

    public void EndPitchAudition()
    {
        if (_disposed || _host is null || _session is not null)
        {
            return;
        }
        try
        {
            _host.EndPitchAudition();
        }
        catch (Exception auditionFailure)
        {
            Exception? cleanupFailure = DiscardPersistentHost();
            if (cleanupFailure is not null)
            {
                throw new AggregateException(
                    "Pitch audition cleanup and persistent Worker cleanup both failed.",
                    auditionFailure,
                    cleanupFailure);
            }
            throw;
        }
    }

    public void SetNextPlaybackCacheMode(RealtimePlaybackCacheMode mode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_session is not null)
        {
            throw new InvalidOperationException(
                "The next playback cache policy cannot change while the Worker is active.");
        }
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        _nextPlaybackCacheMode = mode;
    }

    public void SetNextBufferingRecoveryStorage(
        AudioCacheSessionStore.AudioRecoverySpool? recoverySpool,
        long memoryFallbackFrameCapacity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_session is not null)
        {
            throw new InvalidOperationException(
                "Buffering recovery storage cannot change while the Worker is active.");
        }
        if (memoryFallbackFrameCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(memoryFallbackFrameCapacity));
        }
        _nextRecoverySpool?.Dispose();
        _nextRecoverySpool = recoverySpool;
        _nextRecoveryMemoryFrameCapacity = memoryFallbackFrameCapacity;
    }

    public void BeginBufferingRecovery(long recoveryEndFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_activeRecoverySpool is null && _activeRecoveryMemoryFrameCapacity <= 0)
        {
            throw new AudioRecoveryStorageUnavailableException(
                "The complete Buffering recovery interval has no reserved spool.",
                new IOException("No Buffering recovery spool is active."));
        }
        IBassMidiAudioWorkerSession session = _session
            ?? throw new InvalidOperationException("The audio worker is not active.");
        session.BeginBufferingRecovery(recoveryEndFrame);
    }

    public void ApplyMonitoringCommands(ReadOnlySpan<MidiMonitoringCommand> commands)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IBassMidiAudioWorkerSession session = _session
            ?? throw new InvalidOperationException("The audio worker is not active.");
        session.EnqueueMonitoringCommands(commands);
    }

    public long PauseHeldPreviewAtProducerFrontier(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IBassMidiAudioWorkerSession session = _session
            ?? throw new InvalidOperationException("The audio worker is not active.");
        return session.PauseHeldPreviewAtProducerFrontier(timeout);
    }

    public void ReplaceHeldPreviewFutureAndResume(
        MidiRenderPlan plan,
        long producerFrontierFrame,
        TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IBassMidiAudioWorkerSession session = _session
            ?? throw new InvalidOperationException("The audio worker is not active.");
        session.ReplaceHeldPreviewFutureAndResume(plan, producerFrontierFrame, timeout);
    }

    public void ResumeHeldPreviewFromProducerFrontier()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IBassMidiAudioWorkerSession session = _session
            ?? throw new InvalidOperationException("The audio worker is not active.");
        session.ResumeHeldPreviewFromProducerFrontier(_options.PreparingTimeout);
    }

    public void Stop(bool flush)
    {
        IBassMidiAudioWorkerSession? session = _session;
        if (session is null)
        {
            return;
        }

        Exception? failure = null;
        try
        {
            session.Stop(flush, _options.PreparingTimeout);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            _session = null;
            failure = CombineFailures(failure, CaptureAndRelease(session));
            try
            {
                _activeRecoverySpool?.Dispose();
            }
            catch (Exception exception)
            {
                failure = CombineFailures(failure, exception);
            }
            _activeRecoverySpool = null;
            _activeRecoveryMemoryFrameCapacity = 0;
        }
        if (failure is not null)
        {
            failure = CombineFailures(failure, DiscardPersistentHost());
            throw failure!;
        }
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            Stop(flush: true);
        }
        finally
        {
            _actualSampleRate = 0;
            _actualDeviceBufferFrameCount = 0;
        }
    }

    public void SelectOutputDevice(string? deviceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (deviceId is { Length: 0 })
        {
            throw new ArgumentException(
                "The output device ID must be null for System Default or non-empty.",
                nameof(deviceId));
        }
        if (_session is not null)
        {
            throw new InvalidOperationException(
                "The output device cannot be selected while an audio worker session is active.");
        }

        _selectedDeviceId = deviceId;
        _actualSampleRate = 0;
        _actualDeviceBufferFrameCount = 0;
        _lastStatus = default;
        _lastStandardError = null;
        _lastExitCode = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        try
        {
            Stop(flush: true);
        }
        finally
        {
            _nextRecoverySpool?.Dispose();
            _nextRecoverySpool = null;
            _nextRecoveryMemoryFrameCapacity = 0;
            _host?.Dispose();
            _host = null;
            _disposed = true;
        }
    }

    private Exception? CaptureAndRelease(IBassMidiAudioWorkerSession session) =>
        ExecuteGuaranteedRelease(
            () =>
            {
                _lastStandardError = session.StandardError;
                _lastExitCode = session.ExitCode;
                _lastStatus = session.Status;
            },
            () =>
            {
                try
                {
                    session.Dispose();
                }
                finally
                {
                    _lastStandardError ??= session.StandardError;
                    _lastExitCode ??= session.ExitCode;
                }
            });

    private (AudioWorkerStatus Status, int? ExitCode) ReadCurrentProcessSnapshot()
    {
        IBassMidiAudioWorkerSession? session = _session;
        if (session is null)
        {
            return (_lastStatus, _lastExitCode);
        }

        // Observe process termination before reading the shared status. Once a zero exit code is
        // visible, the Worker can no longer publish another state, so the following status read is
        // its terminal snapshot. Reading in the opposite order can combine an earlier Playing state
        // with a newly visible zero exit code and falsely classify fast cached playback as a crash.
        int? exitCode = session.ExitCode;
        AudioWorkerStatus status = session.Status;
        return (status, exitCode);
    }

    private PersistentBassMidiAudioWorkerHost EnsurePersistentHost()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_soundFontPaths.Length == 0)
        {
            throw new InvalidOperationException("At least one enabled application SoundFont is required.");
        }
        return _host ??= new(
            _options.WorkerPath,
            _options.BassNativeDirectory,
            _soundFonts,
            _options.PreparingTimeout);
    }

    private static bool SoundFontConfigurationsEqual(
        IReadOnlyList<SoundFontConfiguration> left,
        IReadOnlyList<SoundFontConfiguration> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }
        for (int index = 0; index < left.Count; index++)
        {
            if (!string.Equals(
                    left[index].Path,
                    right[index].Path,
                    StringComparison.OrdinalIgnoreCase)
                || left[index].Target != right[index].Target)
            {
                return false;
            }
        }
        return true;
    }

    private Exception? DiscardPersistentHost()
    {
        PersistentBassMidiAudioWorkerHost? host = _host;
        _host = null;
        if (host is null)
        {
            return null;
        }
        try
        {
            host.Dispose();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    internal static Exception? ExecuteGuaranteedRelease(
        Action capture,
        Action release)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(release);
        Exception? failure = null;
        try
        {
            capture();
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        try
        {
            release();
        }
        catch (Exception exception)
        {
            failure = CombineFailures(failure, exception);
        }
        return failure;
    }

    private static Exception? CombineFailures(Exception? previous, Exception? next)
    {
        if (next is null)
        {
            return previous;
        }
        return previous is null
            ? next
            : new AggregateException(
                "Multiple realtime audio worker cleanup operations failed.",
                previous,
                next);
    }

    internal static bool IsUnexpectedWorkerTermination(AudioWorkerState state, int? exitCode) =>
        exitCode.HasValue
        && (exitCode.Value != 0
            || state is not AudioWorkerState.Completed
                and not AudioWorkerState.Stopped
                and not AudioWorkerState.OutputDeviceUnavailable);
}
