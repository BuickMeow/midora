using Midora.AudioDevice.BassWasapi.Settings;
using Midora.NativeInterops.BassWasapi;
using System.Runtime.InteropServices;

namespace Midora.AudioDevice.BassWasapi.Internals;

public sealed unsafe class BassWasapiOutputDevice : IAudioOutputDevice
{
    private AudioOutputDeviceInfo _info;
    private readonly IAudioRenderSource _audioRenderSource;
    private GCHandle _sourceHandle;
    private int _deviceIndex;
    private bool _isInitialized;
    private bool _isStarted;
    private bool _isDisposed;
    private int _callbackFaulted;
    private long _callbackCount;
    private long _callbackAllocatedBytes;
    private long _consumedFrameCount;
    private uint _actualBufferFrameCount;
    private int _cleanupErrorCode;
    private bool _cleanupFaulted;
    private int _deviceLost;
    private int _defaultDeviceChanged;
    private int _deviceListChanged;
    private int _lastCallbackFrameCount;
    private bool _notifyInstalled;

    public BassWasapiOutputDevice(AudioOutputDeviceInfo info, BassWasapiAudioOutputDeviceSettings settings, IAudioRenderSource audioRenderSource)
    {
        _info = info;
        _audioRenderSource = audioRenderSource;
        Initialize(settings);
    }

    public AudioOutputDeviceInfo Info => _info;

    public bool CallbackFaulted => Volatile.Read(ref _callbackFaulted) != 0;

    public long CallbackCount => Volatile.Read(ref _callbackCount);

    public long CallbackAllocatedBytes => Volatile.Read(ref _callbackAllocatedBytes);

    public long ConsumedFrameCount => Volatile.Read(ref _consumedFrameCount);

    public uint ActualBufferFrameCount => _actualBufferFrameCount;

    public int CleanupErrorCode => _cleanupErrorCode;

    public bool CleanupFaulted => _cleanupFaulted;

    public bool DeviceLost => Volatile.Read(ref _deviceLost) != 0;

    public bool DefaultDeviceChanged => Volatile.Read(ref _defaultDeviceChanged) != 0;

    public bool DeviceListChanged => Volatile.Read(ref _deviceListChanged) != 0;

    public int LastCallbackFrameCount => Volatile.Read(ref _lastCallbackFrameCount);

    public double LastCallbackPeriodMilliseconds =>
        LastCallbackFrameCount * 1_000d / Info.AudioFormat.SampleRate;

    public void Start()
    {
        ThrowIfDisposed();
        if (_isStarted)
        {
            return;
        }

        SetCurrentDeviceOrThrow();
        if (0 == BASSWASAPI.Start())
        {
            ThrowBassWasapiError("BASS_WASAPI_Start");
        }

        _isStarted = true;
    }

    public void Stop(bool flush)
    {
        ThrowIfDisposed();
        if (!_isStarted)
        {
            return;
        }

        SetCurrentDeviceOrThrow();
        if (0 == BASSWASAPI.Stop(flush ? 1 : 0))
        {
            ThrowBassWasapiError("BASS_WASAPI_Stop");
        }

        _isStarted = false;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        bool deviceSelected = true;
        if (_isStarted || _notifyInstalled || _isInitialized)
        {
            deviceSelected = BASSWASAPI.SetDevice((uint)_deviceIndex) != 0;
            if (!deviceSelected)
            {
                CaptureCleanupError();
            }
        }

        if (_isStarted)
        {
            if (deviceSelected && BASSWASAPI.Stop(1) == 0)
            {
                CaptureCleanupError();
            }

            _isStarted = false;
        }

        if (_notifyInstalled)
        {
            if (deviceSelected && BASSWASAPI.SetNotify(null, null) == 0)
            {
                CaptureCleanupError();
            }

            _notifyInstalled = false;
        }

        if (_isInitialized)
        {
            if (deviceSelected && BASSWASAPI.Free() == 0)
            {
                CaptureCleanupError();
            }

            _isInitialized = false;
        }

        if (_sourceHandle.IsAllocated)
        {
            _sourceHandle.Free();
        }

        _isDisposed = true;
    }

    private void Initialize(BassWasapiAudioOutputDeviceSettings settings)
    {
        FindDeviceOrThrow(_info.Id, out _, out _deviceIndex);
        _sourceHandle = GCHandle.Alloc(this, GCHandleType.Normal);

        if (0 == BASSWASAPI.Init(
            device: _deviceIndex,
            freq: BassWasapiInitializationPolicy.RequestedFrequency,
            chans: BassWasapiInitializationPolicy.RequestedChannelCount,
            flags: BassWasapiInitializationPolicy.InitializationFlags,
            buffer: settings.DeviceBufferRequestMilliseconds / 1_000f,
            period: BassWasapiInitializationPolicy.RequestedPeriodSeconds,
            proc: &BassWasapiProc,
            user: (void*)GCHandle.ToIntPtr(_sourceHandle)
        ))
        {
            int error = BassErrorCode();
            _sourceHandle.Free();
            throw new MidoraAudioDeviceException($"BASS_WASAPI_Init failed with BASS error {error}.");
        }

        _isInitialized = true;
        SetCurrentDeviceOrThrow();

        if (BASSWASAPI.SetNotify(
            &BassWasapiNotify,
            (void*)GCHandle.ToIntPtr(_sourceHandle)) == 0)
        {
            int error = BassErrorCode();
            throw FinalizeInitializationFailure(new MidoraAudioDeviceException(
                $"BASS_WASAPI_SetNotify failed with BASS error {error}."));
        }

        _notifyInstalled = true;

        BASSWASAPI.BASS_WASAPI_INFO actualInfo;
        if (BASSWASAPI.GetInfo(&actualInfo) == 0)
        {
            int error = BassErrorCode();
            throw FinalizeInitializationFailure(new MidoraAudioDeviceException(
                $"BASS_WASAPI_GetInfo failed with BASS error {error}."));
        }

        if (!BassWasapiInitializationPolicy.IsSupportedRuntimeFormat(
            actualInfo.initflags,
            actualInfo.freq,
            actualInfo.chans,
            actualInfo.format))
        {
            throw FinalizeInitializationFailure(new MidoraAudioDeviceException(
                $"Unsupported WASAPI runtime policy: flags=0x{actualInfo.initflags:x8}, "
                + $"sampleRate={actualInfo.freq}, format={actualInfo.format}, channels={actualInfo.chans}."));
        }

        AudioFormat actualFormat = new(
            checked((int)actualInfo.freq),
            BassWasapiInitializationPolicy.RequestedChannelCount,
            AudioSampleFormat.Float32);
        if (!BassWasapiInitializationPolicy.TryGetBufferFrameCount(
            actualInfo.buflen,
            out _actualBufferFrameCount))
        {
            throw FinalizeInitializationFailure(new MidoraAudioDeviceException(
                $"Invalid WASAPI runtime buffer byte count {actualInfo.buflen}."));
        }
        _info = _info with { AudioFormat = actualFormat };
        if (_audioRenderSource.Format != actualFormat)
        {
            throw FinalizeInitializationFailure(new MidoraAudioDeviceException(
                $"The audio source format {_audioRenderSource.Format} does not match the initialized WASAPI format {actualFormat}; rebuild sample-domain state."));
        }
    }

    private Exception FinalizeInitializationFailure(Exception primaryFailure)
    {
        Dispose();
        return CleanupFaulted
            ? new AggregateException(
                "BASSWASAPI initialization and cleanup both failed.",
                primaryFailure,
                new MidoraAudioDeviceException(
                    $"BASSWASAPI cleanup failed with BASS error {CleanupErrorCode}."))
            : primaryFailure;
    }

    private static void FindDeviceOrThrow(
        string id,
        out BASSWASAPI.BASS_WASAPI_DEVICEINFO bassDeviceInfo,
        out int index
    )
    {
        BASSWASAPI.BASS_WASAPI_DEVICEINFO currentDeviceInfo;

        for (uint i = 0; 0 != BASSWASAPI.GetDeviceInfo(i, &currentDeviceInfo); i++)
        {
            if (Marshal.PtrToStringUTF8((nint)currentDeviceInfo.id) == id)
            {
                if (!BassWasapiOutputDeviceFactory.IsEligibleOutputDevice(currentDeviceInfo.flags))
                {
                    throw new MidoraAudioDeviceException(
                        $"The selected output device [{id}] is no longer enabled and present.");
                }
                bassDeviceInfo = currentDeviceInfo;
                index = (int)i;
                return;
            }
        }

        int error = BassErrorCode();
        BassWasapiOutputDeviceFactory.ValidateEnumerationTerminalError(error);
        throw new MidoraAudioDeviceException($"Could not find the device [{id}]");
    }

    private void SetCurrentDeviceOrThrow()
    {
        if (BASSWASAPI.SetDevice((uint)_deviceIndex) == 0)
        {
            ThrowBassWasapiError("BASS_WASAPI_SetDevice");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    private static int BassErrorCode()
    {
        return Midora.NativeInterops.Bass.BASS.ErrorGetCode();
    }

    private void CaptureCleanupError()
    {
        int error = BassErrorCode();
        _cleanupFaulted = true;
        if (_cleanupErrorCode == 0)
        {
            _cleanupErrorCode = error;
        }
    }

    private static void ThrowBassWasapiError(string operation)
    {
        int error = BassErrorCode();
        throw new MidoraAudioDeviceException($"{operation} failed with BASS error {error}.");
    }

    [UnmanagedCallersOnly]
    private static uint BassWasapiProc(void* buffer, uint length, void* user)
    {
        if (user == null)
        {
            return 0;
        }

        BassWasapiOutputDevice? device = null;
        long allocatedBeforeCallback = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            GCHandle handle = GCHandle.FromIntPtr((nint)user);
            device = handle.Target as BassWasapiOutputDevice;
            if (device is null)
            {
                NativeMemory.Clear(buffer, length);
                return length;
            }

            IAudioRenderSource source = device._audioRenderSource;
            if (source.Format.BytesPerFrame != BassWasapiInitializationPolicy.BytesPerFrame
                || length % BassWasapiInitializationPolicy.BytesPerFrame != 0)
            {
                Interlocked.Exchange(ref device._callbackFaulted, 1);
                NativeMemory.Clear(buffer, length);
                return length;
            }

            int requestedFrames = checked((int)(length / BassWasapiInitializationPolicy.BytesPerFrame));
            Volatile.Write(ref device._lastCallbackFrameCount, requestedFrames);
            AudioPullResult result = source.PullFrames((float*)buffer, requestedFrames);
            if (!IsValidCallbackPullResult(result, requestedFrames))
            {
                Interlocked.Exchange(ref device._callbackFaulted, 1);
                NativeMemory.Clear(buffer, length);
                return length;
            }

            if (result.Status == AudioPullStatus.Fault)
            {
                Interlocked.Exchange(ref device._callbackFaulted, 1);
            }

            Interlocked.Add(ref device._consumedFrameCount, result.FrameCount);

            if (result.FrameCount < requestedFrames)
            {
                uint writtenBytes = (uint)result.FrameCount * BassWasapiInitializationPolicy.BytesPerFrame;
                NativeMemory.Clear((byte*)buffer + writtenBytes, length - writtenBytes);
            }

            return length;
        }
        catch
        {
            if (device is not null)
            {
                Interlocked.Exchange(ref device._callbackFaulted, 1);
            }

            NativeMemory.Clear(buffer, length);
            return length;
        }
        finally
        {
            if (device is not null)
            {
                Interlocked.Increment(ref device._callbackCount);
                Interlocked.Add(
                    ref device._callbackAllocatedBytes,
                    GC.GetAllocatedBytesForCurrentThread() - allocatedBeforeCallback);
            }
        }
    }

    internal static bool IsValidCallbackPullResult(AudioPullResult result, int requestedFrames) =>
        result.FrameCount >= 0
        && result.FrameCount <= requestedFrames
        && (result.Status != AudioPullStatus.Buffering || result.FrameCount == 0);

    [UnmanagedCallersOnly]
    private static void BassWasapiNotify(uint notify, uint deviceIndex, void* user)
    {
        if (user == null)
        {
            return;
        }

        try
        {
            GCHandle handle = GCHandle.FromIntPtr((nint)user);
            if (handle.Target is not BassWasapiOutputDevice device)
            {
                return;
            }

            Volatile.Write(ref device._deviceListChanged, 1);
            if (notify == BASSWASAPI.BASS_WASAPI_NOTIFY_DEFOUTPUT)
            {
                Volatile.Write(ref device._defaultDeviceChanged, 1);
            }

            if (IsDeviceLossNotification(notify, deviceIndex, device._deviceIndex))
            {
                Volatile.Write(ref device._deviceLost, 1);
            }
        }
        catch
        {
            // Native notification boundary: no exception may escape.
        }
    }

    internal static bool IsDeviceLossNotification(uint notify, uint deviceIndex, int selectedDeviceIndex) =>
        deviceIndex == (uint)selectedDeviceIndex
        && notify is BASSWASAPI.BASS_WASAPI_NOTIFY_DISABLED or BASSWASAPI.BASS_WASAPI_NOTIFY_FAIL;

    public static bool IsOutputSelectionInvalidated(
        bool followsSystemDefault,
        bool defaultDeviceChanged,
        bool deviceLost) =>
        deviceLost || (followsSystemDefault && defaultDeviceChanged);
}
