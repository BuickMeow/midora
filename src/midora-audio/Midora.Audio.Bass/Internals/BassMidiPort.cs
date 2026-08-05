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
    private const int MonitoringBatchQueueCapacity = 64;
    private readonly MidiRenderPlan _plan;
    private readonly BassMidiRendererSettings _settings;
    private readonly BassNativeRuntime.Lease? _runtimeLease;
    private readonly PortState[] _ports;
    private readonly int[] _portIndexByNumber;
    private readonly bool[] _sourceEnabled;
    private readonly MidiMonitoringCommand[]?[] _monitoringBatches = new MidiMonitoringCommand[MonitoringBatchQueueCapacity][];
    private readonly object _monitoringProducerSync = new();
    private readonly float _masterGain;
    private readonly bool _limiterEnabled;
    private StereoPeakLimiter _limiter;
    private byte* _packedMidiBuffer;
    private float* _portScratchBuffer;
    private uint _soundFontHandle;
    private long _positionFrames;
    private AudioRenderFault _fault;
    private int _pullActive;
    private long _monitoringBatchReadPosition;
    private long _monitoringBatchWritePosition;
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
        _limiterEnabled = masterSettings.LimiterEnabled;
        _limiter = new StereoPeakLimiter(
            plan.SampleRate,
            masterSettings.LimiterCeiling,
            masterSettings.LimiterReleaseMilliseconds);
        _fault = AudioRenderFault.None;
        _ports = new PortState[plan.Ports.Length];
        _portIndexByNumber = new int[16];
        Array.Fill(_portIndexByNumber, -1);
        _sourceEnabled = new bool[plan.SourceIds.Length];
        Array.Fill(_sourceEnabled, true);
        foreach (int sourceIndex in plan.InitiallyDisabledSourceIndices)
        {
            _sourceEnabled[sourceIndex] = false;
        }
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
        catch (Exception preparationFailure)
        {
            Exception? cleanupFailure = ReleaseNativeResources();
            try
            {
                _runtimeLease?.Dispose();
            }
            catch (Exception runtimeFailure)
            {
                cleanupFailure = CombineFailures(cleanupFailure, runtimeFailure);
            }

            if (cleanupFailure is null)
            {
                throw;
            }

            throw new AggregateException(
                "BASS MIDI renderer preparation and native cleanup both failed.",
                preparationFailure,
                cleanupFailure);
        }
    }

    public AudioFormat Format => new(_plan.SampleRate, 2, AudioSampleFormat.Float32);

    public long PositionFrames => _positionFrames;

    public long TotalFrameCount => _plan.TotalFrameCount;

    public AudioRenderFault Fault => _fault;

    public void EnqueueMonitoringCommands(ReadOnlySpan<MidiMonitoringCommand> commands)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (commands.IsEmpty)
        {
            return;
        }

        MidiMonitoringCommand[] batch = commands.ToArray();
        ValidateMonitoringCommands(batch);
        lock (_monitoringProducerSync)
        {
            long write = _monitoringBatchWritePosition;
            long read = Volatile.Read(ref _monitoringBatchReadPosition);
            if (write - read >= MonitoringBatchQueueCapacity)
            {
                throw new InvalidOperationException("The bounded MIDI monitoring command queue is full.");
            }
            int slot = (int)(write % MonitoringBatchQueueCapacity);
            Volatile.Write(ref _monitoringBatches[slot], batch);
            Volatile.Write(ref _monitoringBatchWritePosition, write + 1);
        }
    }

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
                if (!ApplyPendingMonitoringCommands())
                {
                    return AudioPullResult.Fault(completedFrames);
                }
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
        Exception? cleanupFailure = ReleaseNativeResources();
        try
        {
            _runtimeLease?.Dispose();
        }
        catch (Exception runtimeFailure)
        {
            cleanupFailure = CombineFailures(cleanupFailure, runtimeFailure);
        }
        GC.SuppressFinalize(this);

        if (cleanupFailure is not null)
        {
            throw cleanupFailure;
        }
    }

    ~BassMidiRenderer()
    {
        if (!_disposed)
        {
            _disposed = true;
            try
            {
                _ = ReleaseNativeResources();
            }
            catch
            {
                // Finalizers cannot surface cleanup failures. Every failed native call is still
                // checked and its thread-local error code is captured by ReleaseNativeResources.
            }

            try
            {
                _runtimeLease?.Dispose();
            }
            catch
            {
                // Dispose is the reporting path; the finalizer remains non-throwing.
            }
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
            _portIndexByNumber[plans[i].ZeroBasedPortNumber] = i;
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
        catch (Exception creationFailure)
        {
            if (NativeBass.StreamFree(streamHandle) == 0)
            {
                int error = NativeBass.ErrorGetCode();
                throw new AggregateException(
                    "BASS MIDI Port preparation and stream cleanup both failed.",
                    creationFailure,
                    new MidoraAudioException($"BASS_StreamFree failed with BASS error {error}."));
            }

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
                NativeBassMidi.MIDI_EVENT_RESET,
                0) == 0)
            {
                ThrowBassPreparationFailure("BASS_MIDI_StreamEvent(MIDI_EVENT_RESET)");
            }

            if (NativeBassMidi.StreamEvent(
                streamHandle,
                channel,
                NativeBassMidi.MIDI_EVENT_DEFDRUMS,
                0) == 0)
            {
                ThrowBassPreparationFailure("BASS_MIDI_StreamEvent(MIDI_EVENT_DEFDRUMS)");
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
                int scanIndex = port.EventIndex;
                int batchCount = 0;
                int packedByteCount = 0;

                while (scanIndex < events.Length
                    && batchCount < MaximumMidiBatchEventCount
                    && events[scanIndex].SampleFrame == _positionFrames)
                {
                    ScheduledMidiMessage scheduled = events[scanIndex++];
                    if (scheduled.SourceIndex >= 0 && !_sourceEnabled[scheduled.SourceIndex])
                    {
                        continue;
                    }

                    MidiMessage message = scheduled.Message;
                    _packedMidiBuffer[packedByteCount++] = message.Byte0;
                    _packedMidiBuffer[packedByteCount++] = message.Byte1;
                    if (message.Length == 3)
                    {
                        _packedMidiBuffer[packedByteCount++] = message.Byte2;
                    }

                    batchCount++;
                }

                if (batchCount == 0)
                {
                    port.EventIndex = scanIndex;
                    continue;
                }

                uint submitted = NativeBassMidi.StreamEvents(
                    port.StreamHandle,
                    NativeBassMidi.BASS_MIDI_EVENTS_RAW | NativeBassMidi.BASS_MIDI_EVENTS_NORSTATUS,
                    _packedMidiBuffer,
                    (uint)packedByteCount);

                if (submitted == uint.MaxValue || submitted == 0 || submitted > (uint)batchCount)
                {
                    int error = NativeBass.ErrorGetCode();
                    SetFault(
                        AudioRenderFaultCode.BassMidiEventSubmissionFailed,
                        error,
                        port.Plan.ZeroBasedPortNumber);
                    return false;
                }

                if (submitted == (uint)batchCount)
                {
                    port.EventIndex = scanIndex;
                    continue;
                }

                int acceptedEnabledEvents = 0;
                while (port.EventIndex < scanIndex)
                {
                    ScheduledMidiMessage accepted = events[port.EventIndex++];
                    if (accepted.SourceIndex < 0 || _sourceEnabled[accepted.SourceIndex])
                    {
                        acceptedEnabledEvents++;
                        if (acceptedEnabledEvents == submitted)
                        {
                            break;
                        }
                    }
                }
            }
        }

        return true;
    }

    private bool ApplyPendingMonitoringCommands()
    {
        long read = _monitoringBatchReadPosition;
        long write = Volatile.Read(ref _monitoringBatchWritePosition);
        while (read < write)
        {
            int slot = (int)(read % MonitoringBatchQueueCapacity);
            MidiMonitoringCommand[]? batch = Volatile.Read(ref _monitoringBatches[slot]);
            if (batch is null)
            {
                SetFault(AudioRenderFaultCode.InvalidPullRequest, 0, -1);
                return false;
            }

            for (int i = 0; i < batch.Length; i++)
            {
                MidiMonitoringCommand command = batch[i];
                if (command.Kind == MidiMonitoringCommandKind.SetSourceEnabled)
                {
                    _sourceEnabled[command.SourceIndex] = command.SourceEnabled;
                    continue;
                }

                int portIndex = _portIndexByNumber[command.ZeroBasedPortNumber];
                if (portIndex < 0 || !SubmitImmediateMessage(_ports[portIndex], command.Message))
                {
                    return false;
                }
            }

            Volatile.Write(ref _monitoringBatches[slot], null);
            read++;
            Volatile.Write(ref _monitoringBatchReadPosition, read);
        }
        return true;
    }

    private bool SubmitImmediateMessage(PortState port, MidiMessage message)
    {
        _packedMidiBuffer[0] = message.Byte0;
        _packedMidiBuffer[1] = message.Byte1;
        int byteCount = 2;
        if (message.Length == 3)
        {
            _packedMidiBuffer[2] = message.Byte2;
            byteCount = 3;
        }

        uint submitted = NativeBassMidi.StreamEvents(
            port.StreamHandle,
            NativeBassMidi.BASS_MIDI_EVENTS_RAW | NativeBassMidi.BASS_MIDI_EVENTS_NORSTATUS,
            _packedMidiBuffer,
            (uint)byteCount);
        if (submitted == 1)
        {
            return true;
        }

        int error = NativeBass.ErrorGetCode();
        SetFault(AudioRenderFaultCode.BassMidiEventSubmissionFailed, error, port.Plan.ZeroBasedPortNumber);
        return false;
    }

    private void ValidateMonitoringCommands(ReadOnlySpan<MidiMonitoringCommand> commands)
    {
        foreach (MidiMonitoringCommand command in commands)
        {
            if (command.Kind == MidiMonitoringCommandKind.SetSourceEnabled)
            {
                if ((uint)command.SourceIndex >= (uint)_sourceEnabled.Length)
                {
                    throw new ArgumentException("A monitoring command references an invalid source.", nameof(commands));
                }
                continue;
            }

            MidiMessage message = command.Message;
            if (command.Kind != MidiMonitoringCommandKind.SendMessage
                || command.ZeroBasedPortNumber >= 16
                || _portIndexByNumber[command.ZeroBasedPortNumber] < 0
                || !message.IsChannelVoiceMessage
                || message.Length is < 2 or > 3
                || message.Byte1 > 127
                || message.Byte2 > 127
                || message.MessageType == MidiMessageType.ControlChange && message.Byte1 is 91 or 93)
            {
                throw new ArgumentException("A monitoring command contains an invalid MIDI message or Port.", nameof(commands));
            }
        }
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
            if (_limiterEnabled)
            {
                if (!_limiter.Process(left, right, out destination[sampleIndex], out destination[sampleIndex + 1]))
                {
                    SetFault(AudioRenderFaultCode.NonFiniteSample, 0, -1);
                    return false;
                }
            }
            else
            {
                if (!float.IsFinite(left) || !float.IsFinite(right))
                {
                    SetFault(AudioRenderFaultCode.NonFiniteSample, 0, -1);
                    return false;
                }
                destination[sampleIndex] = left;
                destination[sampleIndex + 1] = right;
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

    private Exception? ReleaseNativeResources()
    {
        Exception? cleanupFailure = null;
        for (int i = _ports.Length - 1; i >= 0; i--)
        {
            PortState? port = _ports[i];
            if (port is not null && port.StreamHandle != 0)
            {
                if (NativeBass.StreamFree(port.StreamHandle) == 0)
                {
                    int error = NativeBass.ErrorGetCode();
                    cleanupFailure = CombineFailures(
                        cleanupFailure,
                        new MidoraAudioException(
                            $"BASS_StreamFree for Port {port.Plan.ZeroBasedPortNumber} failed with BASS error {error}."));
                }
                port.StreamHandle = 0;
            }
        }

        if (_soundFontHandle != 0)
        {
            if (NativeBassMidi.FontFree(_soundFontHandle) == 0)
            {
                int error = NativeBass.ErrorGetCode();
                cleanupFailure = CombineFailures(
                    cleanupFailure,
                    new MidoraAudioException($"BASS_MIDI_FontFree failed with BASS error {error}."));
            }
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

        return cleanupFailure;
    }

    private static Exception CombineFailures(Exception? previous, Exception next)
    {
        return previous is null
            ? next
            : new AggregateException("Multiple native cleanup operations failed.", previous, next);
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
