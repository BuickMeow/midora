using Midora.AudioDevice;
using Midora.AudioDevice.Bass.Internals;
using Midora.AudioDevice.Bass.Settings;
using Midora.AudioDevice.BassWasapi.Internals;
using Midora.AudioDevice.BassWasapi.Settings;

namespace Midora.Audio.Bass.Worker;

/// <summary>
/// Platform-neutral worker facade over the realtime output device. Windows keeps
/// BASSWASAPI Shared/event-driven; macOS uses BASS CoreAudio output. The worker body only
/// sees this type so device selection stays the single platform seam.
/// </summary>
internal sealed class WorkerAudioOutputDevice : IAudioOutputDevice, IDisposable
{
    private readonly IAudioOutputDevice _device;
    private readonly IAudioOutputDeviceDiagnostics _diagnostics;
    private readonly IAudioOutputDeviceFlushController _flushController;

    private WorkerAudioOutputDevice(
        IAudioOutputDevice device,
        IAudioOutputDeviceDiagnostics diagnostics,
        IAudioOutputDeviceFlushController flushController)
    {
        _device = device;
        _diagnostics = diagnostics;
        _flushController = flushController;
    }

    public static WorkerAudioOutputDevice Open(
        IAudioOutputDeviceFactory factory,
        AudioOutputDeviceInfo deviceInfo,
        IAudioRenderSource audioRenderSource)
    {
        IAudioOutputDevice device = factory.Open(deviceInfo, audioRenderSource);
        if (device is not IAudioOutputDeviceDiagnostics diagnostics
            || device is not IAudioOutputDeviceFlushController flushController)
        {
            device.Dispose();
            throw new MidoraAudioDeviceException(
                $"Output device [{deviceInfo.Id}] does not expose the required worker diagnostics.");
        }

        return new(device, diagnostics, flushController);
    }

    public AudioOutputDeviceInfo Info => _device.Info;

    public void Start() => _device.Start();

    public void Stop(bool flush) => _device.Stop(flush);

    public void Dispose() => _device.Dispose();

    public long StopAndResetBufferedOutput() => _flushController.StopAndResetBufferedOutput();

    public bool IsProcessingStarted => _diagnostics.IsProcessingStarted;

    public long CallbackCount => _diagnostics.CallbackCount;

    public long CallbackAllocatedBytes => _diagnostics.CallbackAllocatedBytes;

    public bool CallbackFaulted => _diagnostics.CallbackFaulted;

    public long ConsumedFrameCount => _diagnostics.ConsumedFrameCount;

    public uint ActualBufferFrameCount => _diagnostics.ActualBufferFrameCount;

    public bool DeviceLost => _diagnostics.DeviceLost;

    public bool DefaultDeviceChanged => _diagnostics.DefaultDeviceChanged;

    public bool CleanupFaulted => _diagnostics.CleanupFaulted;

    public int CleanupErrorCode => _diagnostics.CleanupErrorCode;

    public static bool IsOutputSelectionInvalidated(
        bool followsSystemDefault,
        bool defaultDeviceChanged,
        bool deviceLost) =>
        AudioOutputDeviceSelection.IsInvalidated(followsSystemDefault, defaultDeviceChanged, deviceLost);
}

/// <summary>Selects the platform output factory; the worker never branches per call site.</summary>
internal sealed class WorkerAudioOutputDeviceFactory
{
    private readonly IAudioOutputDeviceFactory _factory;

    public WorkerAudioOutputDeviceFactory(int deviceBufferRequestMilliseconds) =>
        _factory = OperatingSystem.IsWindows()
            ? new BassWasapiOutputDeviceFactory(
                new BassWasapiAudioOutputDeviceSettings(deviceBufferRequestMilliseconds))
            : new BassAudioOutputDeviceFactory(
                new BassAudioOutputDeviceSettings(deviceBufferRequestMilliseconds));

    public IReadOnlyList<AudioOutputDeviceInfo> GetDevices() => _factory.GetDevices();

    public WorkerAudioOutputDevice Open(
        AudioOutputDeviceInfo deviceInfo,
        IAudioRenderSource audioRenderSource) =>
        WorkerAudioOutputDevice.Open(_factory, deviceInfo, audioRenderSource);
}
