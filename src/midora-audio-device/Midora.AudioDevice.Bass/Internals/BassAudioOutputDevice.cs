using System.Globalization;
using System.Runtime.InteropServices;
using Midora.AudioDevice.Bass.Settings;
using NativeBass = Midora.NativeInterops.Bass.BASS;

namespace Midora.AudioDevice.Bass.Internals;

/// <summary>
/// macOS-first BASS native output device. A pull stream drives the canonical render source
/// from BASS's output thread; the Windows path remains reserved through the same
/// <see cref="IAudioOutputDevice"/> contract and the WASAPI project is left dormant.
/// </summary>
public sealed unsafe class BassAudioOutputDevice : IAudioOutputDevice
{
    private readonly IAudioRenderSource _audioRenderSource;
    private GCHandle _sourceHandle;
    private AudioOutputDeviceInfo _info;
    private uint _streamHandle;
    private bool _isDeviceInitialized;
    private bool _isStarted;
    private bool _isDisposed;
    private int _callbackFaulted;
    private long _callbackCount;
    private long _callbackAllocatedBytes;
    private long _consumedFrameCount;
    private int _lastCallbackFrameCount;
    private long _callbackPullFailureCount;
    private long _lastCallbackRequestedBytes;
    private long _zeroLengthCallbackCount;
    private uint _updatePeriodMilliseconds;
    private uint _bufferMilliseconds;
    private uint _actualBufferFrameCount;

    public BassAudioOutputDevice(
        AudioOutputDeviceInfo info,
        BassAudioOutputDeviceSettings settings,
        IAudioRenderSource audioRenderSource)
    {
        _info = info ?? throw new ArgumentNullException(nameof(info));
        _audioRenderSource = audioRenderSource ?? throw new ArgumentNullException(nameof(audioRenderSource));
        ArgumentNullException.ThrowIfNull(settings);
        Initialize(settings);
    }

    public AudioOutputDeviceInfo Info => _info;

    public bool CallbackFaulted => Volatile.Read(ref _callbackFaulted) != 0;

    public long CallbackCount => Volatile.Read(ref _callbackCount);

    public long CallbackAllocatedBytes => Volatile.Read(ref _callbackAllocatedBytes);

    public long ConsumedFrameCount => Volatile.Read(ref _consumedFrameCount);

    public long CallbackPullFailureCount => Volatile.Read(ref _callbackPullFailureCount);

    public int LastCallbackFrameCount => Volatile.Read(ref _lastCallbackFrameCount);

    public long LastCallbackRequestedBytes => Volatile.Read(ref _lastCallbackRequestedBytes);

    public long ZeroLengthCallbackCount => Volatile.Read(ref _zeroLengthCallbackCount);

    public uint UpdatePeriodMilliseconds => _updatePeriodMilliseconds;

    public uint BufferMilliseconds => _bufferMilliseconds;

    /// <summary>Device minimum buffer reported by BASS after initialization, in frames.</summary>
    public uint ActualBufferFrameCount => _actualBufferFrameCount;

    public void Start()
    {
        ThrowIfDisposed();
        if (_isStarted)
        {
            return;
        }

        if (NativeBass.ChannelPlay(_streamHandle, 0) == 0)
        {
            ThrowBassError("BASS_ChannelPlay");
        }

        _isStarted = true;
    }

    public void Stop(bool flush)
    {
        if (_isDisposed)
        {
            return;
        }

        if (_isStarted)
        {
            if (NativeBass.ChannelStop(_streamHandle) == 0)
            {
                ThrowBassError("BASS_ChannelStop");
            }

            _isStarted = false;
        }

        if (flush && NativeBass.ChannelSetPosition(
                _streamHandle, 0, BassAudioInitializationPolicy.PositionModeByte) == 0)
        {
            ThrowBassError("BASS_ChannelSetPosition(0)");
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        if (_streamHandle != 0)
        {
            _ = NativeBass.ChannelStop(_streamHandle);
            _ = NativeBass.StreamFree(_streamHandle);
            _streamHandle = 0;
        }

        if (_isDeviceInitialized)
        {
            _ = NativeBass.Free();
            _isDeviceInitialized = false;
        }

        if (_sourceHandle.IsAllocated)
        {
            _sourceHandle.Free();
        }
    }

    private void Initialize(BassAudioOutputDeviceSettings settings)
    {
        if (!int.TryParse(_info.Id, NumberStyles.None, CultureInfo.InvariantCulture, out int deviceIndex)
            || deviceIndex < 1)
        {
            throw new MidoraAudioDeviceException(
                $"The BASS output device id [{_info.Id}] is not a valid physical device index.");
        }

        (uint updatePeriod, uint buffer) =
            BassAudioInitializationPolicy.ResolveBufferConfiguration(settings.DeviceBufferRequestMilliseconds);
        if (NativeBass.SetConfig(NativeBass.BASS_CONFIG_UPDATEPERIOD, updatePeriod) == 0)
        {
            ThrowBassError("BASS_SetConfig(BASS_CONFIG_UPDATEPERIOD)");
        }

        if (NativeBass.SetConfig(NativeBass.BASS_CONFIG_BUFFER, buffer) == 0)
        {
            ThrowBassError("BASS_SetConfig(BASS_CONFIG_BUFFER)");
        }

        _updatePeriodMilliseconds = updatePeriod;
        _bufferMilliseconds = buffer;

        if (NativeBass.Init(deviceIndex, BassAudioInitializationPolicy.RequestedFrequency,
                BassAudioInitializationPolicy.InitializationFlags, null, null) == 0)
        {
            ThrowBassError($"BASS_Init(device {deviceIndex})");
        }

        _isDeviceInitialized = true;

        NativeBass.BASS_INFO info;
        if (NativeBass.GetInfo(&info) == 0 || info.freq is 0 or > int.MaxValue)
        {
            ThrowBassError("BASS_GetInfo");
        }

        AudioFormat format = new(
            (int)info.freq,
            BassAudioInitializationPolicy.RequestedChannelCount,
            AudioSampleFormat.Float32);
        if (!BassAudioInitializationPolicy.IsSupportedRuntimeFormat(format))
        {
            throw new MidoraAudioDeviceException(
                $"BASS initialized device [{_info.Id}] with unsupported format {format.SampleRate} Hz/{format.ChannelCount}ch/{format.SampleFormat}.");
        }

        _info = _info with { AudioFormat = format };
        _actualBufferFrameCount = checked((uint)((long)info.minbuf * format.SampleRate / 1_000));
        _sourceHandle = GCHandle.Alloc(this);
        _streamHandle = NativeBass.StreamCreate(
            (uint)format.SampleRate,
            BassAudioInitializationPolicy.RequestedChannelCount,
            NativeBass.BASS_SAMPLE_FLOAT,
            &StreamProcedure,
            (void*)GCHandle.ToIntPtr(_sourceHandle));
        if (_streamHandle == 0)
        {
            ThrowBassError("BASS_StreamCreate");
        }
    }

    [UnmanagedCallersOnly]
    private static uint StreamProcedure(uint handle, void* buffer, uint length, void* user)
    {
        try
        {
            if (GCHandle.FromIntPtr((nint)user).Target is not BassAudioOutputDevice device)
            {
                return NativeBass.BASS_STREAMPROC_END;
            }

            return device.Pull(buffer, length);
        }
        catch
        {
            return NativeBass.BASS_STREAMPROC_END;
        }
    }

    private uint Pull(void* buffer, uint length)
    {
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Interlocked.Increment(ref _callbackCount);

        int bytesPerFrame = _info.AudioFormat.BytesPerFrame;
        Volatile.Write(ref _lastCallbackRequestedBytes, length);
        if (length == 0)
        {
            Interlocked.Increment(ref _zeroLengthCallbackCount);
        }

        int requestedBytes = checked((int)length);

        // BASS may request a byte count that is not a whole number of frames. Round down to
        // whole frames, render those, and silence the (sub-frame) tail. Treating a non-aligned
        // request as a failure would end the stream and stall the render source.
        int frames = requestedBytes / bytesPerFrame;
        int usedBytes = frames * bytesPerFrame;
        AudioPullStatus status = AudioPullStatus.Continue;

        if (frames > 0)
        {
            AudioPullResult result = _audioRenderSource.PullFrames((float*)buffer, frames);
            if (!result.IsValidForRequest(frames) || result.Status == AudioPullStatus.Fault)
            {
                Interlocked.Increment(ref _callbackPullFailureCount);
                FailCallback();
                return NativeBass.BASS_STREAMPROC_END;
            }

            status = result.Status;
            if (result.FrameCount < frames)
            {
                new Span<byte>(
                    (byte*)buffer + result.FrameCount * bytesPerFrame,
                    (frames - result.FrameCount) * bytesPerFrame).Clear();
            }

            Interlocked.Add(ref _consumedFrameCount, result.FrameCount);
            Volatile.Write(ref _lastCallbackFrameCount, result.FrameCount);
        }

        if (usedBytes < requestedBytes)
        {
            new Span<byte>((byte*)buffer + usedBytes, requestedBytes - usedBytes).Clear();
        }

        Volatile.Write(
            ref _callbackAllocatedBytes,
            Volatile.Read(ref _callbackAllocatedBytes)
                + (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore));

        return status == AudioPullStatus.EndOfStream
            ? NativeBass.BASS_STREAMPROC_END
            : (uint)requestedBytes;
    }

    private void FailCallback() => Volatile.Write(ref _callbackFaulted, 1);

    private static void ThrowBassError(string operation)
    {
        int error = NativeBass.ErrorGetCode();
        throw new MidoraAudioDeviceException($"{operation} failed with BASS error {error}.");
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_isDisposed, this);
}
