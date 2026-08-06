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
/// Formal initial-release realtime backend. The main process only coordinates a frozen render plan
/// and a fixed shared-memory control ABI; the worker owns synthesis, limiter, render-ahead, WASAPI,
/// and the native callback for the complete active session.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BassWasapiChildPlaybackBackend : IRealtimePlaybackBackend
{
    private readonly BassWasapiChildPlaybackOptions _options;
    private BassMidiAudioWorkerSession? _session;
    private int _actualSampleRate;
    private int _actualDeviceBufferFrameCount;
    private AudioWorkerStatus _lastStatus;
    private string? _lastStandardError;
    private int? _lastExitCode;
    private bool _disposed;

    public BassWasapiChildPlaybackBackend(BassWasapiChildPlaybackOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (_options.RenderAheadMilliseconds is < 20 or > 2_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Render-Ahead must be 20-2000 ms.");
        }
        if (_options.RendererSettings is null
            || _options.RendererSettings.MaximumWorkFrameCount
                != InitialReleaseAudioRuntimePolicy.WorkFrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                $"Initial-release child-process work blocks must be {InitialReleaseAudioRuntimePolicy.WorkFrameCount} frames.");
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

    public bool IsFaulted => ChildFaulted
        || IsUnexpectedWorkerTermination(CurrentStatus.State, CurrentExitCode);

    public string? FaultDescription => IsFaulted
        ? $"workerState={CurrentStatus.State}; fault={CurrentStatus.FaultCode}; childExitCode={CurrentExitCode}; stderr={_session?.StandardError ?? _lastStandardError}"
        : null;

    public int Prepare()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_session is not null)
        {
            throw new InvalidOperationException("Cannot prepare an active audio worker session.");
        }
        BassMidiAudioWorkerProbeResult result = BassMidiAudioWorkerSession.Probe(
            _options.WorkerPath,
            _options.BassNativeDirectory,
            _options.DeviceId,
            _options.DeviceBufferRequestMilliseconds,
            _options.PreparingTimeout);
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

        AudioMasterSettings masterSettings = new(
            master.VolumeDecibels,
            _options.MasterSettings.LimiterCeiling,
            _options.MasterSettings.LimiterReleaseMilliseconds,
            master.LimiterEnabled);
        _session = new BassMidiAudioWorkerSession(
            plan,
            soundFontPath,
            _options.RendererSettings,
            masterSettings,
            _options.RenderAheadMilliseconds,
            _options.DeviceBufferRequestMilliseconds,
            _options.DeviceId,
            _options.WorkerPath,
            _options.BassNativeDirectory,
            _options.PreparingTimeout);
    }

    public void ApplyMonitoringCommands(ReadOnlySpan<MidiMonitoringCommand> commands)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        BassMidiAudioWorkerSession session = _session
            ?? throw new InvalidOperationException("The audio worker is not active.");
        session.EnqueueMonitoringCommands(commands);
    }

    public void Stop(bool flush)
    {
        BassMidiAudioWorkerSession? session = _session;
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
            CaptureAndRelease(session);
        }
        if (failure is not null)
        {
            throw failure;
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
            _disposed = true;
        }
    }

    private void CaptureAndRelease(BassMidiAudioWorkerSession session)
    {
        _lastStatus = session.Status;
        _lastStandardError = session.StandardError;
        _lastExitCode = session.ExitCode;
        session.Dispose();
        _lastStandardError ??= session.StandardError;
        _lastExitCode ??= session.ExitCode;
    }

    internal static bool IsUnexpectedWorkerTermination(AudioWorkerState state, int? exitCode) =>
        exitCode.HasValue
        && (exitCode.Value != 0
            || state is not AudioWorkerState.Completed and not AudioWorkerState.Stopped);
}
