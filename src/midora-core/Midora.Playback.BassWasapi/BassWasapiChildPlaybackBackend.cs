using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using Midora.Audio;
using Midora.Audio.Bass;
using Midora.AudioDevice;
using Midora.AudioDevice.BassWasapi.Internals;
using Midora.AudioDevice.BassWasapi.Settings;

namespace Midora.Playback.BassWasapi;

public sealed record BassWasapiChildPlaybackOptions(
    string WorkerPath,
    string BassNativeDirectory,
    string? DeviceId,
    int IpcAudioBufferMilliseconds,
    int DeviceBufferRequestMilliseconds,
    BassMidiRendererSettings RendererSettings,
    AudioMasterSettings MasterSettings,
    TimeSpan PreparingTimeout);

[SupportedOSPlatform("windows")]
public sealed class BassWasapiChildPlaybackBackend : IRealtimePlaybackBackend
{
    private readonly BassWasapiChildPlaybackOptions _options;
    private BassWasapiOutputDeviceFactory? _factory;
    private AudioOutputDeviceInfo? _device;
    private BassMidiChildProcessSession? _session;
    private BassWasapiOutputDevice? _output;
    private int _actualSampleRate;
    private long _lastCallbackAllocatedBytes;
    private long _lastChildAllocatedBytes;
    private long _lastUnderrunCount;
    private bool _lastChildFaulted;
    private bool _disposed;

    public BassWasapiChildPlaybackBackend(BassWasapiChildPlaybackOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (_options.RendererSettings is null
            || _options.RendererSettings.MaximumWorkFrameCount != InitialReleaseAudioRuntimePolicy.WorkFrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                $"Initial-release child-process work blocks must be {InitialReleaseAudioRuntimePolicy.WorkFrameCount} frames.");
        }
    }

    public int ActualSampleRate => _actualSampleRate;
    public long PositionFrames => _output?.ConsumedFrameCount ?? 0;
    public long RenderPositionFrames => _session?.ProducedFrameCount ?? PositionFrames;
    public bool IsBuffering => _session is not null && !_session.ProducerCompleted
        && _session.AvailableFrameCount < _session.ProducerWorkFrameCount;
    public long CallbackAllocatedBytes => _output?.CallbackAllocatedBytes ?? _lastCallbackAllocatedBytes;
    public long ChildRenderingAllocatedBytes => _session?.RenderingThreadAllocatedBytes ?? _lastChildAllocatedBytes;
    public long UnderrunCount => _session?.UnderrunCount ?? _lastUnderrunCount;
    public bool ChildFaulted => _session?.ProducerFaulted ?? _lastChildFaulted;
    public bool IsCompleted => _session is not null && _session.ProducerCompleted && _session.AvailableFrameCount == 0;
    public bool IsFaulted => ChildFaulted || _output?.CallbackFaulted == true;
    public string? FaultDescription => IsFaulted
        ? $"callbackFault={_output?.CallbackFaulted == true}; childFault={ChildFaulted}; childExitCode={_session?.ExitCode}; stderr={_session?.StandardError}"
        : null;

    public int Prepare()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _factory = new BassWasapiOutputDeviceFactory(new(_options.DeviceBufferRequestMilliseconds));
        IReadOnlyList<AudioOutputDeviceInfo> devices = _factory.GetDevices();
        _device = _options.DeviceId is null
            ? devices.FirstOrDefault(value => value.IsSystemDefault) ?? devices.FirstOrDefault()
            : devices.FirstOrDefault(value => string.Equals(value.Id, _options.DeviceId, StringComparison.Ordinal));
        if (_device is null)
        {
            throw new MidoraAudioDeviceException("No enabled output device satisfies the selection.");
        }
        using ProbeSource source = new(_device.AudioFormat);
        BassWasapiOutputDevice probe = (BassWasapiOutputDevice)_factory.Open(_device, source);
        _actualSampleRate = probe.Info.AudioFormat.SampleRate;
        _device = probe.Info;
        probe.Dispose();
        if (probe.CleanupFaulted)
        {
            throw new MidoraAudioDeviceException(
                $"BASSWASAPI probe cleanup failed with BASS error {probe.CleanupErrorCode}.");
        }
        return _actualSampleRate;
    }

    public void Start(MidiRenderPlan plan, string soundFontPath, PlaybackMasterConfiguration master)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_factory is null || _device is null || plan.SampleRate != _actualSampleRate)
        {
            throw new InvalidOperationException("Prepare and a matching sample-domain plan are required.");
        }
        try
        {
            AudioMasterSettings masterSettings = new(
                master.VolumeDecibels,
                _options.MasterSettings.LimiterCeiling,
                _options.MasterSettings.LimiterReleaseMilliseconds,
                master.LimiterEnabled);
            _session = new BassMidiChildProcessSession(
                plan, soundFontPath, _options.RendererSettings, masterSettings,
                _options.IpcAudioBufferMilliseconds, _options.WorkerPath, _options.BassNativeDirectory,
                BassMidiChildConsumptionMode.RealtimeNonBlocking, _options.PreparingTimeout);
            int threshold = InitialReleaseAudioRuntimePolicy.BufferMillisecondsToFrameCapacity(
                plan.SampleRate,
                Math.Min(75, _options.IpcAudioBufferMilliseconds));
            while (_session.AvailableFrameCount < threshold && !_session.ProducerCompleted && !_session.ProducerFaulted)
            {
                Thread.Sleep(1);
            }
            if (_session.ProducerFaulted)
            {
                throw new MidoraAudioException("The child renderer faulted during Preparing.");
            }
            _output = (BassWasapiOutputDevice)_factory.Open(_device, _session);
            if (_output.Info.AudioFormat.SampleRate != _actualSampleRate)
            {
                throw new MidoraAudioDeviceException("Device actual sample rate changed during Preparing.");
            }
            _output.Start();
        }
        catch (Exception preparationFailure)
        {
            Exception? cleanupFailure = Release();
            if (cleanupFailure is null)
            {
                throw;
            }
            throw new AggregateException(
                "Child-process playback preparation and cleanup both failed.",
                preparationFailure,
                cleanupFailure);
        }
    }

    public void Stop(bool flush)
    {
        Exception? failure = null;
        try
        {
            _output?.Stop(flush);
        }
        catch (Exception exception)
        {
            failure = CombineFailures(failure, exception);
        }
        failure = CombineFailures(failure, Release());
        if (failure is not null)
        {
            throw failure;
        }
    }

    public void ApplyMonitoringCommands(ReadOnlySpan<MidiMonitoringCommand> commands)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        BassMidiChildProcessSession session = _session
            ?? throw new InvalidOperationException("The child renderer is not active.");
        session.EnqueueMonitoringCommands(commands);
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            Stop(true);
        }
        finally
        {
            _factory = null;
            _device = null;
            _actualSampleRate = 0;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            Stop(true);
        }
        finally
        {
            _disposed = true;
        }
    }

    private Exception? Release()
    {
        _lastCallbackAllocatedBytes = _output?.CallbackAllocatedBytes ?? _lastCallbackAllocatedBytes;
        _lastChildAllocatedBytes = _session?.RenderingThreadAllocatedBytes ?? _lastChildAllocatedBytes;
        _lastUnderrunCount = _session?.UnderrunCount ?? _lastUnderrunCount;
        _lastChildFaulted = _session?.ProducerFaulted ?? _lastChildFaulted;

        Exception? failure = null;
        BassWasapiOutputDevice? output = _output;
        _output = null;
        try
        {
            output?.Dispose();
            if (output?.CleanupFaulted == true)
            {
                failure = CombineFailures(failure, new MidoraAudioDeviceException(
                    $"BASSWASAPI cleanup failed with BASS error {output.CleanupErrorCode}."));
            }
        }
        catch (Exception exception)
        {
            failure = CombineFailures(failure, exception);
        }

        BassMidiChildProcessSession? session = _session;
        _session = null;
        try
        {
            session?.Dispose();
        }
        catch (Exception exception)
        {
            failure = CombineFailures(failure, exception);
        }

        return failure;
    }

    private static Exception? CombineFailures(Exception? previous, Exception? next)
    {
        if (next is null) return previous;
        return previous is null
            ? next
            : new AggregateException("Multiple child playback cleanup operations failed.", previous, next);
    }

    private sealed unsafe class ProbeSource : IAudioRenderSource, IDisposable
    {
        public ProbeSource(AudioFormat format) => Format = format;
        public AudioFormat Format { get; }
        public AudioPullResult PullFrames(float* destination, int requestedFrameCount)
        {
            NativeMemory.Clear(destination, checked((nuint)requestedFrameCount * (nuint)Format.BytesPerFrame));
            return AudioPullResult.Continue(requestedFrameCount);
        }
        public void Dispose()
        {
        }
    }
}
