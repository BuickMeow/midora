using Midora.AudioDevice.BassWasapi.Settings;
using Midora.NativeInterops.BassWasapi;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NativeBass = Midora.NativeInterops.Bass.BASS;

namespace Midora.AudioDevice.BassWasapi.Internals;

public sealed unsafe class BassWasapiOutputDevice : IAudioOutputDevice
{
    private const int SubmissionHistoryCapacity = 4_096;
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
    private readonly SubmissionRecord[] _submissionHistory =
        new SubmissionRecord[SubmissionHistoryCapacity];
    private long _submissionSequence;
    private long _submittedDeviceFrameCount;
    private long _contentOriginFrame;
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

    public bool IsProcessingStarted
    {
        get
        {
            ThrowIfDisposed();
            SetCurrentDeviceOrThrow();
            return BASSWASAPI.IsStarted() != 0;
        }
    }

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

    /// <summary>
    /// Stops callbacks, discards samples already submitted to WASAPI but not yet
    /// presented by the endpoint, and rewinds the consumer counter to the best
    /// observable audible frontier. The caller must rebuild its prepared PCM at
    /// the returned frame before restarting this output.
    /// </summary>
    public long StopAndResetBufferedOutput()
    {
        ThrowIfDisposed();
        if (!_isStarted)
        {
            return ConsumedFrameCount;
        }

        SetCurrentDeviceOrThrow();
        if (0 == BASSWASAPI.Lock(1))
        {
            ThrowBassWasapiError("BASS_WASAPI_Lock");
        }

        Exception? failure = null;
        long audibleFrameCount = ConsumedFrameCount;
        try
        {
            // The callback cannot advance _consumedFrameCount between these
            // observations while the device lock is held.
            uint bufferedByteCount = BASSWASAPI.GetData(null, NativeBass.BASS_DATA_AVAILABLE);
            if (bufferedByteCount == uint.MaxValue)
            {
                ThrowBassWasapiError("BASS_WASAPI_GetData(BASS_DATA_AVAILABLE)");
            }
            long submittedDeviceFrameCount = _submittedDeviceFrameCount;
            if (0 == BASSWASAPI.Stop(1))
            {
                ThrowBassWasapiError("BASS_WASAPI_Stop(reset)");
            }

            _isStarted = false;
            long audibleDeviceFrameCount = CalculateAudibleFrameCount(
                submittedDeviceFrameCount,
                bufferedByteCount);
            audibleFrameCount = ResolveContentFrameAtDeviceFrontier(
                audibleDeviceFrameCount);
            ResetSubmissionHistory(audibleFrameCount);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            if (0 == BASSWASAPI.Lock(0))
            {
                int error = BassErrorCode();
                MidoraAudioDeviceException unlockFailure = new(
                    $"BASS_WASAPI_Lock(unlock) failed with BASS error {error}.");
                failure = failure is null
                    ? unlockFailure
                    : new AggregateException(failure, unlockFailure);
            }
        }
        if (failure is not null)
        {
            throw failure;
        }
        return audibleFrameCount;
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

            device.RecordSubmission(requestedFrames, result.FrameCount);

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
        result.IsValidForRequest(requestedFrames);

    internal static long CalculateAudibleFrameCount(
        long submittedFrameCount,
        uint bufferedByteCount)
    {
        if (submittedFrameCount < 0
            || bufferedByteCount % BassWasapiInitializationPolicy.BytesPerFrame != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferedByteCount));
        }

        long bufferedFrameCount = bufferedByteCount
            / BassWasapiInitializationPolicy.BytesPerFrame;
        return Math.Max(0, submittedFrameCount - bufferedFrameCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordSubmission(int requestedFrameCount, int contentFrameCount)
    {
        long sequence = _submissionSequence;
        int index = (int)(sequence % SubmissionHistoryCapacity);
        _submissionHistory[index] = new SubmissionRecord(
            sequence,
            _submittedDeviceFrameCount,
            _consumedFrameCount,
            requestedFrameCount,
            contentFrameCount);
        _submittedDeviceFrameCount += requestedFrameCount;
        _consumedFrameCount += contentFrameCount;
        _submissionSequence = sequence + 1;
    }

    private long ResolveContentFrameAtDeviceFrontier(long deviceFrame)
    {
        if (deviceFrame <= 0)
        {
            return _contentOriginFrame;
        }
        if (deviceFrame >= _submittedDeviceFrameCount)
        {
            return _consumedFrameCount;
        }

        long firstSequence = Math.Max(0, _submissionSequence - SubmissionHistoryCapacity);
        for (long sequence = _submissionSequence - 1; sequence >= firstSequence; sequence--)
        {
            SubmissionRecord record =
                _submissionHistory[(int)(sequence % SubmissionHistoryCapacity)];
            if (record.Sequence != sequence || deviceFrame < record.DeviceStartFrame)
            {
                continue;
            }
            long relativeDeviceFrame = deviceFrame - record.DeviceStartFrame;
            return record.ContentStartFrame + Math.Min(
                relativeDeviceFrame,
                record.ContentFrameCount);
        }

        // This would require more than 4096 callbacks of endpoint latency. A
        // conservative rewind is safer than replaying content that may not yet
        // have reached the endpoint.
        return _contentOriginFrame;
    }

    private void ResetSubmissionHistory(long contentOriginFrame)
    {
        Array.Clear(_submissionHistory);
        _submissionSequence = 0;
        _submittedDeviceFrameCount = 0;
        _contentOriginFrame = contentOriginFrame;
        Volatile.Write(ref _consumedFrameCount, contentOriginFrame);
    }

    private readonly record struct SubmissionRecord(
        long Sequence,
        long DeviceStartFrame,
        long ContentStartFrame,
        int RequestedFrameCount,
        int ContentFrameCount);

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
