using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using Midora.Audio;
using Midora.Audio.Bass;
using Midora.AudioDevice;
using Midora.AudioDevice.BassWasapi.Internals;
using Midora.AudioDevice.BassWasapi.Settings;

namespace Midora.Playback.BassWasapi;

public sealed record BassWasapiPlaybackOptions(
    string? DeviceId,
    int RenderAheadMilliseconds,
    int DeviceBufferRequestMilliseconds,
    int WorkFrameCount,
    BassMidiRendererSettings RendererSettings,
    AudioMasterSettings MasterSettings)
{
    public static BassWasapiPlaybackOptions PrototypeCandidate { get; } = new(
        null,
        100,
        50,
        256,
        new BassMidiRendererSettings(
            BassMidiNoteOffPolicy.ReleaseAllMatchingNotes,
            BassMidiInterpolation.BassDefault,
            BassMidiSampleLoading.OnDemand,
            0,
            0,
            256),
        AudioMasterSettings.LimiterV1Candidate);
}

[SupportedOSPlatform("windows")]
public sealed class BassWasapiPlaybackBackend : IRealtimePlaybackBackend
{
    private readonly BassWasapiPlaybackOptions _options;
    private BassWasapiOutputDeviceFactory? _factory;
    private AudioOutputDeviceInfo? _device;
    private BassMidiRenderer? _renderer;
    private AudioFrameRingBuffer? _ring;
    private AudioRenderAheadWorker? _worker;
    private BassWasapiOutputDevice? _output;
    private int _actualSampleRate;
    private long _lastCallbackAllocatedBytes;
    private long _lastRenderingAllocatedBytes;
    private long _lastUnderrunCount;
    private bool _lastCallbackFaulted;
    private AudioRenderFault _lastRendererFault = AudioRenderFault.None;
    private bool _disposed;

    public BassWasapiPlaybackBackend(BassWasapiPlaybackOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (_options.RenderAheadMilliseconds is < 20 or > 2_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Render-Ahead must be 20–2000 ms.");
        }
        if (_options.WorkFrameCount is < 16 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Work frame count must be 16–65536.");
        }
    }

    public int ActualSampleRate => _actualSampleRate;
    public long PositionFrames => _output?.ConsumedFrameCount ?? 0;
    public long RenderPositionFrames => _renderer?.PositionFrames ?? PositionFrames;
    public bool IsBuffering => _ring is not null && !_ring.ProducerCompleted
        && _ring.AvailableFrameCount < _options.WorkFrameCount;
    public long CallbackAllocatedBytes => _output?.CallbackAllocatedBytes ?? _lastCallbackAllocatedBytes;
    public long RenderingThreadAllocatedBytes => _worker?.RenderingThreadAllocatedBytes ?? _lastRenderingAllocatedBytes;
    public long UnderrunCount => _ring?.UnderrunCount ?? _lastUnderrunCount;
    public bool CallbackFaulted => _output?.CallbackFaulted ?? _lastCallbackFaulted;
    public AudioRenderFault RendererFault => _renderer?.Fault ?? _lastRendererFault;
    public AudioOutputDeviceInfo? SelectedDevice => _output?.Info ?? _device;
    public bool IsCompleted => _ring is not null && _ring.ProducerCompleted && _ring.AvailableFrameCount == 0;
    public bool IsFaulted => CallbackFaulted || _ring?.ProducerFaulted == true
        || RendererFault.Code != AudioRenderFaultCode.None;
    public string? FaultDescription => IsFaulted
        ? $"callbackFault={CallbackFaulted}; producerFault={_ring?.ProducerFaulted == true}; rendererFault={RendererFault}"
        : null;

    public int Prepare()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_output is not null)
        {
            throw new InvalidOperationException("Cannot prepare an already active backend.");
        }
        _factory = new BassWasapiOutputDeviceFactory(
            new BassWasapiAudioOutputDeviceSettings(_options.DeviceBufferRequestMilliseconds));
        IReadOnlyList<AudioOutputDeviceInfo> devices = _factory.GetDevices();
        _device = _options.DeviceId is null
            ? devices.FirstOrDefault(value => value.IsSystemDefault) ?? devices.FirstOrDefault()
            : devices.FirstOrDefault(value => string.Equals(value.Id, _options.DeviceId, StringComparison.Ordinal));
        if (_device is null)
        {
            throw new MidoraAudioDeviceException("No enabled output device satisfies the selection.");
        }

        using SilentSource source = new(_device.AudioFormat);
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
        ArgumentNullException.ThrowIfNull(plan);
        if (_actualSampleRate == 0 || _factory is null || _device is null)
        {
            throw new InvalidOperationException("Prepare must succeed before Start.");
        }
        if (plan.SampleRate != _actualSampleRate)
        {
            throw new ArgumentException("The render plan sample rate differs from the prepared device rate.", nameof(plan));
        }
        if (_output is not null)
        {
            throw new InvalidOperationException("The backend is already active.");
        }

        try
        {
            AudioMasterSettings masterSettings = new(
                master.VolumeDecibels,
                _options.MasterSettings.LimiterCeiling,
                _options.MasterSettings.LimiterReleaseMilliseconds,
                master.LimiterEnabled);
            _renderer = new BassMidiRenderer(plan, soundFontPath, _options.RendererSettings, masterSettings);
            int ringCapacity = checked(plan.SampleRate * _options.RenderAheadMilliseconds / 1_000);
            if (ringCapacity < _options.WorkFrameCount)
            {
                throw new InvalidOperationException("Render-Ahead buffer is smaller than the work block.");
            }
            _ring = new AudioFrameRingBuffer(_renderer.Format, ringCapacity);
            _worker = new AudioRenderAheadWorker(_renderer, _ring, _options.WorkFrameCount);
            _worker.Start();
            int threshold = ringCapacity * 3 / 4;
            while (_ring.AvailableFrameCount < threshold && !_ring.ProducerCompleted && !_ring.ProducerFaulted)
            {
                Thread.Sleep(1);
            }
            if (_ring.ProducerFaulted)
            {
                throw new MidoraAudioException($"Render-ahead Preparing failed: {_renderer.Fault}");
            }
            _output = (BassWasapiOutputDevice)_factory.Open(_device, _ring);
            if (_output.Info.AudioFormat.SampleRate != _actualSampleRate)
            {
                throw new MidoraAudioDeviceException("Device actual sample rate changed during Preparing; rebuild the sample-domain cache.");
            }
            _output.Start();
        }
        catch (Exception preparationFailure)
        {
            Exception? cleanupFailure = ReleaseActiveResources();
            if (cleanupFailure is null)
            {
                throw;
            }
            throw new AggregateException(
                "Realtime playback preparation and cleanup both failed.",
                preparationFailure,
                cleanupFailure);
        }
    }

    public void Stop(bool flush)
    {
        if (_output is null && _worker is null && _renderer is null)
        {
            return;
        }
        Exception? failure = null;
        try
        {
            _output?.Stop(flush);
        }
        catch (Exception exception)
        {
            failure = CombineFailures(failure, exception);
        }
        try
        {
            _worker?.Stop();
        }
        catch (Exception exception)
        {
            failure = CombineFailures(failure, exception);
        }
        failure = CombineFailures(failure, ReleaseActiveResources());
        if (failure is not null)
        {
            throw failure;
        }
    }

    public void ApplyMonitoringCommands(ReadOnlySpan<MidiMonitoringCommand> commands)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        BassMidiRenderer renderer = _renderer
            ?? throw new InvalidOperationException("The realtime renderer is not active.");
        renderer.EnqueueMonitoringCommands(commands);
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
        if (_disposed)
        {
            return;
        }
        try
        {
            Stop(true);
        }
        finally
        {
            _disposed = true;
        }
    }

    private Exception? ReleaseActiveResources()
    {
        _lastCallbackAllocatedBytes = _output?.CallbackAllocatedBytes ?? _lastCallbackAllocatedBytes;
        _lastRenderingAllocatedBytes = _worker?.RenderingThreadAllocatedBytes ?? _lastRenderingAllocatedBytes;
        _lastUnderrunCount = _ring?.UnderrunCount ?? _lastUnderrunCount;
        _lastCallbackFaulted = _output?.CallbackFaulted ?? _lastCallbackFaulted;
        _lastRendererFault = _renderer?.Fault ?? _lastRendererFault;

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

        AudioRenderAheadWorker? worker = _worker;
        _worker = null;
        try
        {
            worker?.Dispose();
        }
        catch (Exception exception)
        {
            failure = CombineFailures(failure, exception);
        }

        AudioFrameRingBuffer? ring = _ring;
        _ring = null;
        try
        {
            ring?.Dispose();
        }
        catch (Exception exception)
        {
            failure = CombineFailures(failure, exception);
        }

        BassMidiRenderer? renderer = _renderer;
        _renderer = null;
        try
        {
            renderer?.Dispose();
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
            : new AggregateException("Multiple playback cleanup operations failed.", previous, next);
    }

    private sealed unsafe class SilentSource : IAudioRenderSource, IDisposable
    {
        public SilentSource(AudioFormat format) => Format = format;
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
