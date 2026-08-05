using Midora.Audio.Bass.Internals;
using Midora.AudioDevice;
using Midora.Midi;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NativeBass = Midora.NativeInterops.Bass.BASS;
using NativeBassMidi = Midora.NativeInterops.BassMidi.BASSMIDI;

namespace Midora.Audio.Bass;

public sealed unsafe class BassMidiRenderer : IMidiRenderer
{
    private const int MaximumMidiBatchEventCount = 1_024;
    private const int MaximumPackedMidiBatchByteCount = MaximumMidiBatchEventCount * 3;
    private readonly MidiRenderPlan _plan;
    private readonly BassMidiRendererSettings _settings;
    private readonly BassNativeRuntime.Lease? _runtimeLease;
    private readonly PortState[] _ports;
    private readonly float _masterGain;
    private StereoPeakLimiter _limiter;
    private byte* _packedMidiBuffer;
    private float* _portScratchBuffer;
    private uint _soundFontHandle;
    private long _positionFrames;
    private AudioRenderFault _fault;
    private int _pullActive;
    private bool _disposed;

    public BassMidiRenderer(
        MidiRenderPlan plan,
        string soundFontPath,
        BassMidiRendererSettings settings,
        AudioMasterSettings masterSettings)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(soundFontPath);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(masterSettings);

        if (!File.Exists(soundFontPath))
        {
            throw new FileNotFoundException("The Project SoundFont does not exist.", soundFontPath);
        }

        _plan = plan;
        _settings = settings;
        _masterGain = MathF.Pow(10f, masterSettings.VolumeDecibels / 20f);
        _limiter = new StereoPeakLimiter(
            plan.SampleRate,
            masterSettings.LimiterCeiling,
            masterSettings.LimiterReleaseMilliseconds);
        _fault = AudioRenderFault.None;
        _ports = new PortState[plan.Ports.Length];
        _runtimeLease = BassNativeRuntime.Acquire();

        try
        {
            _packedMidiBuffer = (byte*)NativeMemory.Alloc(MaximumPackedMidiBatchByteCount);
            nuint scratchByteCount = checked((nuint)settings.MaximumWorkFrameCount * 2 * sizeof(float));
            _portScratchBuffer = (float*)NativeMemory.Alloc(scratchByteCount);
            CreateSoundFont(soundFontPath);
            CreatePorts();
            WarmNativeHotPath();
        }
        catch
        {
            ReleaseNativeResources();
            _runtimeLease?.Dispose();
            throw;
        }
    }

    public AudioFormat Format => new(_plan.SampleRate, 2, AudioSampleFormat.Float32);

    public long PositionFrames => _positionFrames;

    public long TotalFrameCount => _plan.TotalFrameCount;

    public AudioRenderFault Fault => _fault;

    public AudioPullResult PullFrames(float* destination, int requestedFrameCount)
    {
        if (_disposed || destination == null || requestedFrameCount < 0 || Interlocked.Exchange(ref _pullActive, 1) != 0)
        {
            SetFault(AudioRenderFaultCode.InvalidPullRequest, 0, -1);
            return AudioPullResult.Fault();
        }

        int completedFrames = 0;
        try
        {
            if (_fault.Code != AudioRenderFaultCode.None)
            {
                return AudioPullResult.Fault();
            }

            if (requestedFrameCount == 0)
            {
                return _positionFrames == _plan.TotalFrameCount
                    ? AudioPullResult.EndOfStream()
                    : AudioPullResult.Continue(0);
            }

            long availableFrames = _plan.TotalFrameCount - _positionFrames;
            int targetFrameCount = (int)Math.Min(requestedFrameCount, availableFrames);

            while (completedFrames < targetFrameCount)
            {
                if (!SubmitEventsAtCurrentFrame())
                {
                    return AudioPullResult.Fault(completedFrames);
                }

                long nextEventFrame = FindNextEventFrame();
                int workFrameCount = Math.Min(
                    _settings.MaximumWorkFrameCount,
                    targetFrameCount - completedFrames);

                if (nextEventFrame != long.MaxValue)
                {
                    workFrameCount = (int)Math.Min(workFrameCount, nextEventFrame - _positionFrames);
                }

                if (workFrameCount <= 0)
                {
                    SetFault(AudioRenderFaultCode.BassMidiEventSubmissionFailed, 0, -1);
                    return AudioPullResult.Fault(completedFrames);
                }

                if (!RenderFrames(destination + (completedFrames * 2), workFrameCount))
                {
                    return AudioPullResult.Fault(completedFrames);
                }

                completedFrames += workFrameCount;
                _positionFrames += workFrameCount;
            }

            return _positionFrames == _plan.TotalFrameCount
                ? AudioPullResult.EndOfStream(completedFrames)
                : AudioPullResult.Continue(completedFrames);
        }
        finally
        {
            Volatile.Write(ref _pullActive, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseNativeResources();
        _runtimeLease?.Dispose();
        GC.SuppressFinalize(this);
    }

    ~BassMidiRenderer()
    {
        if (!_disposed)
        {
            _disposed = true;
            ReleaseNativeResources();
            _runtimeLease?.Dispose();
        }
    }

    private void CreateSoundFont(string soundFontPath)
    {
        fixed (char* path = soundFontPath)
        {
            _soundFontHandle = NativeBassMidi.FontInit(
                path,
                NativeBass.BASS_UNICODE | NativeBassMidi.BASS_MIDI_FONT_MMAP);
        }

        if (_soundFontHandle == 0)
        {
            ThrowBassPreparationFailure("BASS_MIDI_FontInit");
        }
    }

    private void CreatePorts()
    {
        ReadOnlySpan<MidiPortRenderPlan> plans = _plan.Ports;
        for (int i = 0; i < plans.Length; i++)
        {
            _ports[i] = CreatePort(plans[i]);
        }
    }

    private PortState CreatePort(MidiPortRenderPlan plan)
    {
        uint flags = BuildStreamFlags(_settings);

        uint streamHandle = NativeBassMidi.StreamCreate(16, flags, (uint)_plan.SampleRate);
        if (streamHandle == 0)
        {
            ThrowBassPreparationFailure("BASS_MIDI_StreamCreate");
        }

        try
        {
            NativeBassMidi.BASS_MIDI_FONT font = new()
            {
                font = _soundFontHandle,
                preset = -1,
                bank = 0
            };

            if (NativeBassMidi.StreamSetFonts(streamHandle, &font, 1) == 0)
            {
                ThrowBassPreparationFailure("BASS_MIDI_StreamSetFonts");
            }

            if (_settings.MaximumVoices != 0
                && NativeBass.ChannelSetAttribute(
                    streamHandle,
                    NativeBassMidi.BASS_ATTRIB_MIDI_VOICES,
                    _settings.MaximumVoices) == 0)
            {
                ThrowBassPreparationFailure("BASS_ChannelSetAttribute(BASS_ATTRIB_MIDI_VOICES)");
            }

            if (NativeBass.ChannelSetAttribute(
                streamHandle,
                NativeBassMidi.BASS_ATTRIB_MIDI_CPU,
                _settings.CpuLimitPercent) == 0)
            {
                ThrowBassPreparationFailure("BASS_ChannelSetAttribute(BASS_ATTRIB_MIDI_CPU)");
            }

            EstablishCanonicalInitialState(streamHandle);

            if (_settings.SampleLoading == BassMidiSampleLoading.PreloadAllReferencedSamples
                && NativeBassMidi.StreamLoadSamples(streamHandle) == 0)
            {
                ThrowBassPreparationFailure("BASS_MIDI_StreamLoadSamples");
            }

            return new PortState(plan, streamHandle);
        }
        catch
        {
            _ = NativeBass.StreamFree(streamHandle);
            throw;
        }
    }

    internal static uint BuildStreamFlags(BassMidiRendererSettings settings)
    {
        uint flags = NativeBass.BASS_SAMPLE_FLOAT
            | NativeBass.BASS_STREAM_DECODE
            | NativeBassMidi.BASS_MIDI_NOFX;

        if (settings.NoteOffPolicy == BassMidiNoteOffPolicy.ReleaseOldestMatchingNote)
        {
            flags |= NativeBassMidi.BASS_MIDI_NOTEOFF1;
        }

        if (settings.Interpolation == BassMidiInterpolation.Sinc)
        {
            flags |= NativeBassMidi.BASS_MIDI_SINCINTER;
        }

        return flags;
    }

    private void WarmNativeHotPath()
    {
        for (int i = 0; i < _ports.Length; i++)
        {
            PortState port = _ports[i];
            uint submitted = NativeBassMidi.StreamEvents(
                port.StreamHandle,
                NativeBassMidi.BASS_MIDI_EVENTS_RAW | NativeBassMidi.BASS_MIDI_EVENTS_NORSTATUS,
                _packedMidiBuffer,
                0);
            if (submitted == uint.MaxValue)
            {
                ThrowBassPreparationFailure("BASS_MIDI_StreamEvents warm-up");
            }

            uint available = NativeBass.ChannelGetData(port.StreamHandle, _portScratchBuffer, 0);
            if (available == uint.MaxValue)
            {
                ThrowBassPreparationFailure("BASS_ChannelGetData warm-up");
            }
        }
    }

    private static void EstablishCanonicalInitialState(uint streamHandle)
    {
        for (uint channel = 0; channel < 16; channel++)
        {
            if (NativeBassMidi.StreamEvent(
                streamHandle,
                channel,
                NativeBassMidi.MIDI_EVENT_DEFDRUMS,
                0) == 0)
            {
                ThrowBassPreparationFailure("BASS_MIDI_StreamEvent(MIDI_EVENT_DEFDRUMS)");
            }

            if (NativeBassMidi.StreamEvent(
                streamHandle,
                channel,
                NativeBassMidi.MIDI_EVENT_RESET,
                0) == 0)
            {
                ThrowBassPreparationFailure("BASS_MIDI_StreamEvent(MIDI_EVENT_RESET)");
            }
        }
    }

    private bool SubmitEventsAtCurrentFrame()
    {
        for (int portIndex = 0; portIndex < _ports.Length; portIndex++)
        {
            PortState port = _ports[portIndex];
            ReadOnlySpan<ScheduledMidiMessage> events = port.Plan.Events;

            while (port.EventIndex < events.Length
                && events[port.EventIndex].SampleFrame == _positionFrames)
            {
                int batchStart = port.EventIndex;
                int batchCount = 0;
                int packedByteCount = 0;

                while (batchStart + batchCount < events.Length
                    && batchCount < MaximumMidiBatchEventCount
                    && events[batchStart + batchCount].SampleFrame == _positionFrames)
                {
                    MidiMessage message = events[batchStart + batchCount].Message;
                    _packedMidiBuffer[packedByteCount++] = message.Byte0;
                    _packedMidiBuffer[packedByteCount++] = message.Byte1;
                    if (message.Length == 3)
                    {
                        _packedMidiBuffer[packedByteCount++] = message.Byte2;
                    }

                    batchCount++;
                }

                uint submitted = NativeBassMidi.StreamEvents(
                    port.StreamHandle,
                    NativeBassMidi.BASS_MIDI_EVENTS_RAW | NativeBassMidi.BASS_MIDI_EVENTS_NORSTATUS,
                    _packedMidiBuffer,
                    (uint)packedByteCount);

                if (submitted == uint.MaxValue || submitted != (uint)batchCount)
                {
                    int error = NativeBass.ErrorGetCode();
                    SetFault(
                        AudioRenderFaultCode.BassMidiEventSubmissionFailed,
                        error,
                        port.Plan.ZeroBasedPortNumber);
                    return false;
                }

                port.EventIndex += batchCount;
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long FindNextEventFrame()
    {
        long result = long.MaxValue;
        for (int i = 0; i < _ports.Length; i++)
        {
            PortState port = _ports[i];
            ReadOnlySpan<ScheduledMidiMessage> events = port.Plan.Events;
            if (port.EventIndex < events.Length)
            {
                result = Math.Min(result, events[port.EventIndex].SampleFrame);
            }
        }

        return result;
    }

    private bool RenderFrames(float* destination, int frameCount)
    {
        nuint sampleCount = checked((nuint)frameCount * 2);
        NativeMemory.Clear(destination, checked(sampleCount * sizeof(float)));
        uint requestedByteCount = checked((uint)(sampleCount * sizeof(float)));

        for (int portIndex = 0; portIndex < _ports.Length; portIndex++)
        {
            PortState port = _ports[portIndex];
            uint receivedByteCount = NativeBass.ChannelGetData(
                port.StreamHandle,
                _portScratchBuffer,
                requestedByteCount);

            if (receivedByteCount == uint.MaxValue)
            {
                int error = NativeBass.ErrorGetCode();
                SetFault(
                    AudioRenderFaultCode.BassMidiDecodeFailed,
                    error,
                    port.Plan.ZeroBasedPortNumber);
                return false;
            }

            if (receivedByteCount != requestedByteCount)
            {
                SetFault(
                    AudioRenderFaultCode.BassMidiShortRead,
                    0,
                    port.Plan.ZeroBasedPortNumber);
                return false;
            }

            for (nuint sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
            {
                destination[sampleIndex] += _portScratchBuffer[sampleIndex];
            }
        }

        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            int sampleIndex = frameIndex * 2;
            float left = destination[sampleIndex] * _masterGain;
            float right = destination[sampleIndex + 1] * _masterGain;
            if (!_limiter.Process(left, right, out destination[sampleIndex], out destination[sampleIndex + 1]))
            {
                SetFault(AudioRenderFaultCode.NonFiniteSample, 0, -1);
                return false;
            }
        }

        return true;
    }

    private void SetFault(AudioRenderFaultCode code, int nativeErrorCode, int zeroBasedPortNumber)
    {
        if (_fault.Code == AudioRenderFaultCode.None)
        {
            _fault = new AudioRenderFault(code, nativeErrorCode, zeroBasedPortNumber, _positionFrames);
        }
    }

    private void ReleaseNativeResources()
    {
        for (int i = _ports.Length - 1; i >= 0; i--)
        {
            PortState? port = _ports[i];
            if (port is not null && port.StreamHandle != 0)
            {
                _ = NativeBass.StreamFree(port.StreamHandle);
                port.StreamHandle = 0;
            }
        }

        if (_soundFontHandle != 0)
        {
            _ = NativeBassMidi.FontFree(_soundFontHandle);
            _soundFontHandle = 0;
        }

        if (_portScratchBuffer != null)
        {
            NativeMemory.Free(_portScratchBuffer);
            _portScratchBuffer = null;
        }

        if (_packedMidiBuffer != null)
        {
            NativeMemory.Free(_packedMidiBuffer);
            _packedMidiBuffer = null;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowBassPreparationFailure(string operation)
    {
        int error = NativeBass.ErrorGetCode();
        throw new MidoraAudioException($"{operation} failed with BASS error {error}.");
    }

    private sealed class PortState(MidiPortRenderPlan plan, uint streamHandle)
    {
        public MidiPortRenderPlan Plan { get; } = plan;

        public uint StreamHandle { get; set; } = streamHandle;

        public int EventIndex { get; set; }
    }
}
