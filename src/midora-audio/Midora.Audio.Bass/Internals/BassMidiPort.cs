using Midora.Audio.Bass.Internals;
using Midora.AudioDevice;
using Midora.Midi;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NativeBass = Midora.NativeInterops.Bass.BASS;
using NativeBassMidi = Midora.NativeInterops.BassMidi.BASSMIDI;

namespace Midora.Audio.Bass;

public sealed unsafe class BassMidiRenderer : IMidiRenderer, IPlaybackSpanFallbackSource
{
    private const int MaximumMidiBatchEventCount = 1_024;
    private const int MaximumPackedMidiBatchByteCount = MaximumMidiBatchEventCount * 3;
    private const int MonitoringCommandQueueCapacity = 4_096;
    private const int DeterministicNativeDecodeFrameCount = InitialReleaseAudioRuntimePolicy.WorkFrameCount;
    private const int CachedMonitoringFadeMilliseconds = 4;
    private const float InitialReleaseInterpolationQuality = 1f;
    private const float InitialReleaseCpuLimit = 0f;
    private MidiRenderPlan _plan;
    private readonly BassMidiRendererSettings _settings;
    private readonly BassNativeRuntime.Lease? _runtimeLease;
    private readonly UnitState[] _units;
    private readonly int[] _unitIndexByCanonicalNumber;
    private readonly bool[] _sourceEnabled;
    private readonly bool[] _sourceCacheBypassed;
    private readonly int _cachedMonitoringFadeFrameCount;
    private readonly MidiMonitoringCommand[] _monitoringCommands = new MidiMonitoringCommand[MonitoringCommandQueueCapacity];
    private readonly object _monitoringProducerSync = new();
    private readonly float _masterGain;
    private readonly bool _limiterEnabled;
    private StereoPeakLimiter _limiter;
    private byte* _packedMidiBuffer;
    private float* _portScratchBuffer;
    private float* _outputStagingBuffer;
    private uint _soundFontHandle;
    private UnitPcmCacheIoBridge? _cacheIo;
    private long _positionFrames;
    private long _renderPositionFrames;
    private long _nativeSynthesisFrameCount;
    private AudioRenderFault _fault;
    private int _stagedFrameOffset;
    private int _stagedFrameCount;
    private int _pullActive;
    private long _monitoringCommandReadPosition;
    private long _monitoringCommandWritePosition;
    private bool _disposed;
    private bool _cacheCaptureInvalidated;

    public BassMidiRenderer(
        MidiRenderPlan plan,
        string soundFontPath,
        BassMidiRendererSettings settings,
        AudioMasterSettings masterSettings,
        string? cacheStagingPath = null)
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
        _units = new UnitState[plan.Units.Length];
        _unitIndexByCanonicalNumber = new int[16 * 16];
        Array.Fill(_unitIndexByCanonicalNumber, -1);
        _sourceEnabled = new bool[plan.SourceIds.Length];
        _sourceCacheBypassed = new bool[plan.SourceIds.Length];
        _cachedMonitoringFadeFrameCount = Math.Max(
            1,
            checked((plan.SampleRate * CachedMonitoringFadeMilliseconds + 999) / 1000));
        Array.Fill(_sourceEnabled, true);
        foreach (int sourceIndex in plan.InitiallyDisabledSourceIndices)
        {
            _sourceEnabled[sourceIndex] = false;
        }
        _runtimeLease = BassNativeRuntime.Acquire();

        try
        {
            _packedMidiBuffer = (byte*)NativeMemory.Alloc(MaximumPackedMidiBatchByteCount);
            nuint scratchByteCount = checked(
                (nuint)DeterministicNativeDecodeFrameCount * 2 * sizeof(float));
            _portScratchBuffer = (float*)NativeMemory.Alloc(scratchByteCount);
            _outputStagingBuffer = (float*)NativeMemory.Alloc(scratchByteCount);
            OpenCacheStaging(cacheStagingPath);
            CreateSoundFont(soundFontPath);
            PreloadReferencedPresets();
            CreateUnits();
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

    public long RenderPositionFrames => _renderPositionFrames;

    public long TotalFrameCount => _plan.TotalFrameCount;

    public AudioRenderFault Fault => _fault;

    public bool CacheCaptureInvalidated => _cacheCaptureInvalidated;

    internal long NativeSynthesisFrameCountForDiagnostics => _nativeSynthesisFrameCount;

    internal int UnitStreamCountForDiagnostics => _units.Length;

    internal void FinalizeCacheCapture()
    {
        _cacheIo?.CompleteWritesAndWait();
        _cacheCaptureInvalidated |= _cacheIo?.WriteFaulted == true;
    }

    internal uint GetPressedKeyCountForDiagnostics(int zeroBasedPortNumber, int zeroBasedChannel)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (zeroBasedPortNumber is < 0 or >= 16)
        {
            throw new ArgumentOutOfRangeException(nameof(zeroBasedPortNumber));
        }
        if (zeroBasedChannel is < 0 or >= 16)
        {
            throw new ArgumentOutOfRangeException(nameof(zeroBasedChannel));
        }

        int unitIndex = _unitIndexByCanonicalNumber[(zeroBasedPortNumber * 16) + zeroBasedChannel];
        if (unitIndex < 0)
        {
            throw new ArgumentException(
                "The render plan does not use the requested Port/Channel Unit.",
                nameof(zeroBasedChannel));
        }

        uint count = NativeBassMidi.StreamGetEvent(
            _units[unitIndex].StreamHandle,
            0,
            NativeBassMidi.MIDI_EVENT_NOTES);
        if (count == uint.MaxValue)
        {
            int error = NativeBass.ErrorGetCode();
            throw new MidoraAudioException(
                $"BASS_MIDI_StreamGetEvent(MIDI_EVENT_NOTES) failed with BASS error {error}.");
        }

        return count;
    }

    internal float GetStreamAttributeForDiagnostics(int zeroBasedPortNumber, uint attribute)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (zeroBasedPortNumber is < 0 or >= 16)
        {
            throw new ArgumentOutOfRangeException(nameof(zeroBasedPortNumber));
        }

        int unitIndex = FindFirstUnitIndexForPort(zeroBasedPortNumber);
        if (unitIndex < 0)
        {
            throw new ArgumentException("The render plan does not use the requested Port.", nameof(zeroBasedPortNumber));
        }

        float value;
        if (NativeBass.ChannelGetAttribute(_units[unitIndex].StreamHandle, attribute, &value) == 0)
        {
            ThrowBassPreparationFailure("BASS_ChannelGetAttribute");
        }

        return value;
    }

    public void EnqueueMonitoringCommands(ReadOnlySpan<MidiMonitoringCommand> commands)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (commands.IsEmpty)
        {
            return;
        }

        ValidateMonitoringCommands(commands);
        lock (_monitoringProducerSync)
        {
            long write = _monitoringCommandWritePosition;
            long read = Volatile.Read(ref _monitoringCommandReadPosition);
            if (commands.Length > MonitoringCommandQueueCapacity - (write - read))
            {
                throw new InvalidOperationException("The bounded MIDI monitoring command queue is full.");
            }

            for (int i = 0; i < commands.Length; i++)
            {
                _monitoringCommands[(int)((write + i) % MonitoringCommandQueueCapacity)] = commands[i];
            }
            Volatile.Write(ref _monitoringCommandWritePosition, write + commands.Length);
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
                if (_stagedFrameCount == 0)
                {
                    FillOutputResult fill = FillOutputStagingBuffer();
                    if (fill == FillOutputResult.Fault)
                    {
                        return AudioPullResult.Fault(completedFrames);
                    }
                    if (fill == FillOutputResult.Buffering)
                    {
                        return completedFrames == 0
                            ? AudioPullResult.Buffering()
                            : AudioPullResult.Continue(completedFrames);
                    }
                }

                int copyFrameCount = Math.Min(
                    _stagedFrameCount,
                    targetFrameCount - completedFrames);
                nuint copyByteCount = checked((nuint)copyFrameCount * 2 * sizeof(float));
                Buffer.MemoryCopy(
                    _outputStagingBuffer + (_stagedFrameOffset * 2),
                    destination + (completedFrames * 2),
                    copyByteCount,
                    copyByteCount);

                _stagedFrameOffset += copyFrameCount;
                _stagedFrameCount -= copyFrameCount;
                if (_stagedFrameCount == 0)
                {
                    _stagedFrameOffset = 0;
                }
                completedFrames += copyFrameCount;
                _positionFrames += copyFrameCount;
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

    public void ReplaceFuturePlan(MidiRenderPlan plan, long producerFrontierFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(plan);
        if (Volatile.Read(ref _pullActive) != 0
            || _renderPositionFrames != producerFrontierFrame
            || _positionFrames > producerFrontierFrame
            || producerFrontierFrame - _positionFrames != _stagedFrameCount)
        {
            throw new InvalidOperationException(
                "The MIDI render plan can only be replaced at a paused producer frontier.");
        }
        if (plan.SampleRate != _plan.SampleRate
            || plan.TotalFrameCount < producerFrontierFrame
            || !plan.SourceIds.SequenceEqual(_plan.SourceIds)
            || !plan.InitiallyDisabledSourceIndices.SequenceEqual(_plan.InitiallyDisabledSourceIndices))
        {
            throw new ArgumentException(
                "The replacement plan is incompatible with the active held-preview renderer.",
                nameof(plan));
        }
        ReadOnlySpan<MidiUnitRenderPlan> plans = plan.Units;
        if (plans.Length != _units.Length)
        {
            throw new ArgumentException(
                "The replacement plan must preserve the active Unit stream set.",
                nameof(plan));
        }
        for (int i = 0; i < plans.Length; i++)
        {
            if (plans[i].CanonicalUnitNumber != _units[i].Plan.CanonicalUnitNumber)
            {
                throw new ArgumentException(
                    "The replacement plan must preserve the active ordered Unit stream set.",
                    nameof(plan));
            }
        }

        PreloadReferencedPresets(plan);
        for (int i = 0; i < plans.Length; i++)
        {
            MidiUnitRenderPlan replacementPlan = plans[i];
            _units[i].Plan = replacementPlan;
            _units[i].EventIndex = MidiRenderPlanSplicer.FindFirstEventAtOrAfter(
                replacementPlan.Events,
                producerFrontierFrame);
            _units[i].Fragments = plan.UnitFragments.ToArray()
                .Where(value => value.CanonicalUnitNumber == replacementPlan.CanonicalUnitNumber)
                .OrderBy(value => value.StartFrame)
                .ThenBy(value => value.InstanceGroupId)
                .ThenBy(value => value.SubVoiceId)
                .ToArray();
            _units[i].FragmentIndex = 0;
            while (_units[i].FragmentIndex < _units[i].Fragments.Length
                && _units[i].Fragments[_units[i].FragmentIndex].EndFrame
                    <= producerFrontierFrame)
            {
                _units[i].FragmentIndex++;
            }
            _units[i].FragmentInitialized = _units[i].FragmentIndex
                < _units[i].Fragments.Length
                && _units[i].Fragments[_units[i].FragmentIndex].StartFrame
                    < producerFrontierFrame;
        }
        _plan = plan;
    }

    internal void SeekForMonitoringColdStart(long producerFrontierFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Volatile.Read(ref _pullActive) != 0
            || producerFrontierFrame < 0
            || producerFrontierFrame > _plan.TotalFrameCount
            || _positionFrames != 0
            || _renderPositionFrames != 0
            || _stagedFrameCount != 0)
        {
            throw new InvalidOperationException(
                "A playback-span cache fallback requires an unused renderer and a valid producer frontier.");
        }

        _limiter.Reset();
        for (int i = 0; i < _units.Length; i++)
        {
            UnitState unit = _units[i];
            EstablishCanonicalInitialState(unit.StreamHandle);
            unit.EventIndex = MidiRenderPlanSplicer.FindFirstEventAtOrAfter(
                unit.Plan.Events,
                producerFrontierFrame);
            unit.FragmentIndex = 0;
            while (unit.FragmentIndex < unit.Fragments.Length
                && unit.Fragments[unit.FragmentIndex].EndFrame <= producerFrontierFrame)
            {
                unit.FragmentIndex++;
            }
            unit.FragmentInitialized = false;
            unit.CachedMuteFadeRemainingFrames = 0;
        }
        _positionFrames = producerFrontierFrame;
        _renderPositionFrames = producerFrontierFrame;
    }

    void IPlaybackSpanFallbackSource.SeekForMonitoringColdStart(
        long producerFrontierFrame) =>
        SeekForMonitoringColdStart(producerFrontierFrame);

    private FillOutputResult FillOutputStagingBuffer()
    {
        if (!ApplyPendingMonitoringCommands()
            || !PrepareFragmentStatesAtCurrentFrame())
        {
            return FillOutputResult.Fault;
        }

        long nextEventFrame = Math.Min(
            FindNextEventFrameAfterCurrent(),
            FindNextFragmentBoundaryFrame());
        int frameCount = (int)Math.Min(
            DeterministicNativeDecodeFrameCount,
            _plan.TotalFrameCount - _renderPositionFrames);
        if (nextEventFrame != long.MaxValue)
        {
            frameCount = (int)Math.Min(frameCount, nextEventFrame - _renderPositionFrames);
        }

        if (frameCount <= 0)
        {
            SetFault(AudioRenderFaultCode.BassMidiEventSubmissionFailed, 0, -1);
            return FillOutputResult.Fault;
        }
        if (!PrepareCacheIoForBlock(frameCount))
        {
            return _fault.Code == AudioRenderFaultCode.None
                ? FillOutputResult.Buffering
                : FillOutputResult.Fault;
        }
        if (!SubmitEventsAtCurrentFrame())
        {
            return FillOutputResult.Fault;
        }
        if (!RenderFrames(_outputStagingBuffer, frameCount))
        {
            return FillOutputResult.Fault;
        }

        _stagedFrameOffset = 0;
        _stagedFrameCount = frameCount;
        _renderPositionFrames += frameCount;
        return FillOutputResult.Success;
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

    private void OpenCacheStaging(string? cacheStagingPath)
    {
        long requiredLength = 0;
        foreach (MidiUnitFragmentRenderPlan fragment in _plan.UnitFragments)
        {
            if (fragment.PcmCacheKey is null)
            {
                continue;
            }
            requiredLength = Math.Max(
                requiredLength,
                checked(fragment.PcmCachePayloadOffset + fragment.PcmPayloadByteCount));
        }
        if (requiredLength == 0)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(cacheStagingPath))
        {
            throw new InvalidDataException(
                "The render plan contains PCM cache bindings without a staging file.");
        }
        string path = Path.GetFullPath(cacheStagingPath);
        if (!File.Exists(path) || new FileInfo(path).Length != requiredLength)
        {
            throw new InvalidDataException(
                "The Unit PCM cache staging file length does not match the render plan.");
        }

        _cacheIo = new UnitPcmCacheIoBridge(path, _plan);
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

    private void PreloadReferencedPresets() => PreloadReferencedPresets(_plan);

    private void PreloadReferencedPresets(MidiRenderPlan plan)
    {
        int[] referencedPresets = CollectReferencedPresetKeys(plan);
        for (int i = 0; i < referencedPresets.Length; i++)
        {
            int key = referencedPresets[i];
            int preset = key & 127;
            int bank = key >> 7;
            if (NativeBassMidi.FontLoad(_soundFontHandle, preset, bank) != 0)
            {
                continue;
            }

            int error = NativeBass.ErrorGetCode();
            if (error != NativeBass.BASS_ERROR_NOTAVAIL)
            {
                ThrowBassPreparationFailure("BASS_MIDI_FontLoad", error);
            }

            // Initial release deliberately does not validate whether a Program/Bank exists.
            // BASSMIDI may fall back to another bank/preset, so preload the whole SF2 in this
            // exceptional case to keep fallback rendering free of runtime sample loading.
            if (NativeBassMidi.FontLoad(_soundFontHandle, -1, -1) == 0)
            {
                ThrowBassPreparationFailure("BASS_MIDI_FontLoad(all fallback presets)");
            }
            return;
        }
    }

    internal static int[] CollectReferencedPresetKeys(MidiRenderPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        bool[] referenced = new bool[128 * 128];
        int count = 0;
        Span<byte> banks = stackalloc byte[16];
        Span<byte> programs = stackalloc byte[16];
        foreach (MidiPortRenderPlan port in plan.Ports)
        {
            banks.Clear();
            programs.Clear();
            foreach (ScheduledMidiMessage scheduled in port.Events)
            {
                MidiMessage message = scheduled.Message;
                int channel = message.ChannelNumber;
                if (message.MessageType == MidiMessageType.ControlChange && message.Byte1 == 0)
                {
                    banks[channel] = message.Byte2;
                    continue;
                }
                if (message.MessageType == MidiMessageType.ProgramChange)
                {
                    programs[channel] = message.Byte1;
                    continue;
                }
                if (message.MessageType != MidiMessageType.NoteOn || message.Byte2 == 0)
                {
                    continue;
                }

                int key = (banks[channel] << 7) | programs[channel];
                if (!referenced[key])
                {
                    referenced[key] = true;
                    count++;
                }
            }
        }

        int[] result = new int[count];
        int resultIndex = 0;
        for (int key = 0; key < referenced.Length; key++)
        {
            if (referenced[key])
            {
                result[resultIndex++] = key;
            }
        }
        return result;
    }

    private void CreateUnits()
    {
        ReadOnlySpan<MidiUnitRenderPlan> plans = _plan.Units;
        for (int i = 0; i < plans.Length; i++)
        {
            MidiUnitRenderPlan unitPlan = plans[i];
            MidiUnitFragmentRenderPlan[] fragments = _plan.UnitFragments.ToArray()
                .Where(value => value.CanonicalUnitNumber == unitPlan.CanonicalUnitNumber)
                .OrderBy(value => value.StartFrame)
                .ThenBy(value => value.InstanceGroupId)
                .ThenBy(value => value.SubVoiceId)
                .ToArray();
            _units[i] = CreateUnit(unitPlan, fragments);
            _unitIndexByCanonicalNumber[unitPlan.CanonicalUnitNumber] = i;
        }
    }

    private UnitState CreateUnit(
        MidiUnitRenderPlan plan,
        MidiUnitFragmentRenderPlan[] fragments)
    {
        uint flags = BuildStreamFlags(_settings);

        uint streamHandle = NativeBassMidi.StreamCreate(1, flags, (uint)_plan.SampleRate);
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

            if (NativeBass.ChannelSetAttribute(
                streamHandle,
                NativeBassMidi.BASS_ATTRIB_MIDI_SRC,
                InitialReleaseInterpolationQuality) == 0)
            {
                ThrowBassPreparationFailure("BASS_ChannelSetAttribute(BASS_ATTRIB_MIDI_SRC)");
            }

            if (NativeBass.ChannelSetAttribute(
                    streamHandle,
                    NativeBassMidi.BASS_ATTRIB_MIDI_VOICES,
                    _settings.MaximumSampleVoicesPerUnitStream) == 0)
            {
                ThrowBassPreparationFailure("BASS_ChannelSetAttribute(BASS_ATTRIB_MIDI_VOICES)");
            }

            if (NativeBass.ChannelSetAttribute(
                streamHandle,
                NativeBassMidi.BASS_ATTRIB_MIDI_CPU,
                InitialReleaseCpuLimit) == 0)
            {
                ThrowBassPreparationFailure("BASS_ChannelSetAttribute(BASS_ATTRIB_MIDI_CPU)");
            }

            EstablishCanonicalInitialState(streamHandle);

            return new UnitState(plan, streamHandle, fragments);
        }
        catch (Exception creationFailure)
        {
            if (NativeBass.StreamFree(streamHandle) == 0)
            {
                int error = NativeBass.ErrorGetCode();
                throw new AggregateException(
                    "BASS MIDI Unit preparation and stream cleanup both failed.",
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
            | NativeBassMidi.BASS_MIDI_NOFX
            | NativeBassMidi.BASS_MIDI_NOTEOFF1;

        return flags;
    }

    private void WarmNativeHotPath()
    {
        for (int i = 0; i < _units.Length; i++)
        {
            UnitState unit = _units[i];
            uint submitted = NativeBassMidi.StreamEvents(
                unit.StreamHandle,
                NativeBassMidi.BASS_MIDI_EVENTS_RAW | NativeBassMidi.BASS_MIDI_EVENTS_NORSTATUS,
                _packedMidiBuffer,
                0);
            if (submitted == uint.MaxValue)
            {
                ThrowBassPreparationFailure("BASS_MIDI_StreamEvents warm-up");
            }

            uint available = NativeBass.ChannelGetData(unit.StreamHandle, _portScratchBuffer, 0);
            if (available == uint.MaxValue)
            {
                ThrowBassPreparationFailure("BASS_ChannelGetData warm-up");
            }
        }
    }

    private static void EstablishCanonicalInitialState(uint streamHandle)
    {
        const uint channel = 0;
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

    private bool SubmitEventsAtCurrentFrame()
    {
        for (int unitIndex = 0; unitIndex < _units.Length; unitIndex++)
        {
            UnitState unit = _units[unitIndex];
            ReadOnlySpan<ScheduledMidiMessage> events = unit.Plan.Events;

            while (unit.EventIndex < events.Length
                && events[unit.EventIndex].SampleFrame == _renderPositionFrames)
            {
                int scanIndex = unit.EventIndex;
                int batchCount = 0;
                int packedByteCount = 0;

                while (scanIndex < events.Length
                    && batchCount < MaximumMidiBatchEventCount
                    && events[scanIndex].SampleFrame == _renderPositionFrames)
                {
                    ScheduledMidiMessage scheduled = events[scanIndex++];
                    if (!ShouldSubmitScheduledEvent(unit, scheduled))
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
                    unit.EventIndex = scanIndex;
                    continue;
                }

                uint submitted = NativeBassMidi.StreamEvents(
                    unit.StreamHandle,
                    NativeBassMidi.BASS_MIDI_EVENTS_RAW | NativeBassMidi.BASS_MIDI_EVENTS_NORSTATUS,
                    _packedMidiBuffer,
                    (uint)packedByteCount);

                if (submitted == uint.MaxValue || submitted == 0 || submitted > (uint)batchCount)
                {
                    int error = NativeBass.ErrorGetCode();
                    SetFault(
                        AudioRenderFaultCode.BassMidiEventSubmissionFailed,
                        error,
                        unit.Plan.CanonicalZeroBasedPortNumber);
                    return false;
                }

                if (submitted == (uint)batchCount)
                {
                    unit.EventIndex = scanIndex;
                    continue;
                }

                int acceptedEnabledEvents = 0;
                while (unit.EventIndex < scanIndex)
                {
                    ScheduledMidiMessage accepted = events[unit.EventIndex++];
                    if (ShouldSubmitScheduledEvent(unit, accepted))
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
        long read = _monitoringCommandReadPosition;
        long write = Volatile.Read(ref _monitoringCommandWritePosition);
        while (read < write)
        {
            MidiMonitoringCommand command = _monitoringCommands[
                (int)(read % MonitoringCommandQueueCapacity)];
            if (command.Kind == MidiMonitoringCommandKind.SetSourceEnabled)
            {
                if (_sourceEnabled[command.SourceIndex] == command.SourceEnabled)
                {
                    read++;
                    Volatile.Write(ref _monitoringCommandReadPosition, read);
                    continue;
                }
                _sourceEnabled[command.SourceIndex] = command.SourceEnabled;
                ConfigureCachedHitFadeForSource(
                    command.SourceIndex,
                    command.SourceEnabled);
                if (!_sourceCacheBypassed[command.SourceIndex])
                {
                    _sourceCacheBypassed[command.SourceIndex] = true;
                    _cacheCaptureInvalidated |= HasPcmCacheBindings();
                    ResetCachedHitStreamsForSource(command.SourceIndex);
                }
            }
            else
            {
                MidiMessage canonicalMessage = command.Message;
                int canonicalUnitNumber =
                    (command.ZeroBasedPortNumber * 16) + canonicalMessage.ChannelNumber;
                int unitIndex = _unitIndexByCanonicalNumber[canonicalUnitNumber];
                MidiMessage unitMessage = MidiMessage.FromPackedValue(
                    canonicalMessage.PackedValue & ~MidiMessage.ChannelNumberMask);
                if (unitIndex < 0 || !SubmitImmediateMessage(_units[unitIndex], unitMessage))
                {
                    return false;
                }
            }

            read++;
            Volatile.Write(ref _monitoringCommandReadPosition, read);
        }
        return true;
    }

    private bool SubmitImmediateMessage(UnitState unit, MidiMessage message)
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
            unit.StreamHandle,
            NativeBassMidi.BASS_MIDI_EVENTS_RAW | NativeBassMidi.BASS_MIDI_EVENTS_NORSTATUS,
            _packedMidiBuffer,
            (uint)byteCount);
        if (submitted == 1)
        {
            return true;
        }

        int error = NativeBass.ErrorGetCode();
        SetFault(
            AudioRenderFaultCode.BassMidiEventSubmissionFailed,
            error,
            unit.Plan.CanonicalZeroBasedPortNumber);
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
                || _unitIndexByCanonicalNumber[
                    (command.ZeroBasedPortNumber * 16) + message.ChannelNumber] < 0
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
    private long FindNextEventFrameAfterCurrent()
    {
        long result = long.MaxValue;
        for (int i = 0; i < _units.Length; i++)
        {
            UnitState unit = _units[i];
            ReadOnlySpan<ScheduledMidiMessage> events = unit.Plan.Events;
            int index = unit.EventIndex;
            while (index < events.Length
                && events[index].SampleFrame <= _renderPositionFrames)
            {
                index++;
            }
            if (index < events.Length)
            {
                result = Math.Min(result, events[index].SampleFrame);
            }
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long FindNextFragmentBoundaryFrame()
    {
        long result = long.MaxValue;
        for (int i = 0; i < _units.Length; i++)
        {
            UnitState unit = _units[i];
            if (unit.Fragments.Length == 0 || unit.FragmentIndex >= unit.Fragments.Length)
            {
                continue;
            }
            MidiUnitFragmentRenderPlan fragment = unit.Fragments[unit.FragmentIndex];
            result = Math.Min(
                result,
                _renderPositionFrames < fragment.StartFrame
                    ? fragment.StartFrame
                    : fragment.EndFrame);
        }
        return result;
    }

    private bool PrepareFragmentStatesAtCurrentFrame()
    {
        for (int i = 0; i < _units.Length; i++)
        {
            UnitState unit = _units[i];
            if (unit.Fragments.Length == 0)
            {
                continue;
            }
            while (unit.FragmentIndex < unit.Fragments.Length
                && unit.Fragments[unit.FragmentIndex].EndFrame <= _renderPositionFrames)
            {
                unit.FragmentIndex++;
                unit.FragmentInitialized = false;
                unit.CachedMuteFadeRemainingFrames = 0;
            }
            if (unit.FragmentIndex >= unit.Fragments.Length)
            {
                continue;
            }
            MidiUnitFragmentRenderPlan fragment = unit.Fragments[unit.FragmentIndex];
            if (_renderPositionFrames < fragment.StartFrame || unit.FragmentInitialized)
            {
                continue;
            }
            try
            {
                EstablishCanonicalInitialState(unit.StreamHandle);
                unit.FragmentInitialized = true;
            }
            catch (MidoraAudioException)
            {
                int error = NativeBass.ErrorGetCode();
                SetFault(
                    AudioRenderFaultCode.BassMidiEventSubmissionFailed,
                    error,
                    unit.Plan.CanonicalZeroBasedPortNumber);
                return false;
            }
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool ShouldSubmitScheduledEvent(UnitState unit, ScheduledMidiMessage scheduled)
    {
        MidiUnitFragmentRenderPlan? fragment = GetActiveFragment(unit);
        if (unit.Fragments.Length != 0 && fragment is null)
        {
            return false;
        }
        if (fragment is not null
            && fragment.PcmCacheKey is not null
            && !_sourceCacheBypassed[fragment.SourceIndex])
        {
            return !fragment.PcmCacheHit;
        }
        return scheduled.SourceIndex < 0 || _sourceEnabled[scheduled.SourceIndex];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private MidiUnitFragmentRenderPlan? GetActiveFragment(UnitState unit)
    {
        if (unit.FragmentIndex >= unit.Fragments.Length)
        {
            return null;
        }
        MidiUnitFragmentRenderPlan fragment = unit.Fragments[unit.FragmentIndex];
        return _renderPositionFrames >= fragment.StartFrame
            && _renderPositionFrames < fragment.EndFrame
            ? fragment
            : null;
    }

    private bool HasPcmCacheBindings()
    {
        for (int i = 0; i < _units.Length; i++)
        {
            foreach (MidiUnitFragmentRenderPlan fragment in _units[i].Fragments)
            {
                if (fragment.PcmCacheKey is not null)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private void ResetCachedHitStreamsForSource(int sourceIndex)
    {
        for (int i = 0; i < _units.Length; i++)
        {
            UnitState unit = _units[i];
            MidiUnitFragmentRenderPlan? fragment = GetActiveFragment(unit);
            if (fragment is not null
                && fragment.SourceIndex == sourceIndex
                && fragment.PcmCacheHit)
            {
                EstablishCanonicalInitialState(unit.StreamHandle);
            }
        }
    }

    private void ConfigureCachedHitFadeForSource(int sourceIndex, bool enabled)
    {
        for (int i = 0; i < _units.Length; i++)
        {
            UnitState unit = _units[i];
            MidiUnitFragmentRenderPlan? fragment = GetActiveFragment(unit);
            if (fragment is not null
                && fragment.SourceIndex == sourceIndex
                && fragment.PcmCacheHit)
            {
                unit.CachedMuteFadeRemainingFrames = enabled
                    ? 0
                    : _cachedMonitoringFadeFrameCount;
            }
        }
    }

    private bool RenderFrames(float* destination, int frameCount)
    {
        nuint sampleCount = checked((nuint)frameCount * 2);
        NativeMemory.Clear(destination, checked(sampleCount * sizeof(float)));
        uint requestedByteCount = checked((uint)(sampleCount * sizeof(float)));

        for (int unitIndex = 0; unitIndex < _units.Length; unitIndex++)
        {
            UnitState unit = _units[unitIndex];
            MidiUnitFragmentRenderPlan? fragment = GetActiveFragment(unit);
            if (unit.Fragments.Length != 0 && fragment is null)
            {
                continue;
            }
            bool cacheActive = fragment?.PcmCacheKey is not null
                && (!_sourceCacheBypassed[fragment.SourceIndex]
                    || fragment.PcmCacheHit && unit.CachedMuteFadeRemainingFrames > 0);
            bool shouldMix;
            if (cacheActive && fragment!.PcmCacheHit)
            {
                if (!CopyCachedPcmToScratch(fragment, frameCount))
                {
                    SetFault(AudioRenderFaultCode.PcmCacheReadFailed, 0,
                        unit.Plan.CanonicalZeroBasedPortNumber);
                    return false;
                }
                if (unit.CachedMuteFadeRemainingFrames > 0)
                {
                    unit.CachedMuteFadeRemainingFrames = ApplyCachedMuteFade(
                        new Span<float>(_portScratchBuffer, checked(frameCount * 2)),
                        unit.CachedMuteFadeRemainingFrames,
                        _cachedMonitoringFadeFrameCount);
                    shouldMix = true;
                }
                else
                {
                    shouldMix = _sourceEnabled[fragment.SourceIndex];
                }
            }
            else
            {
                uint receivedByteCount = NativeBass.ChannelGetData(
                    unit.StreamHandle,
                    _portScratchBuffer,
                    requestedByteCount);

                if (receivedByteCount == uint.MaxValue)
                {
                    int error = NativeBass.ErrorGetCode();
                    SetFault(
                        AudioRenderFaultCode.BassMidiDecodeFailed,
                        error,
                        unit.Plan.CanonicalZeroBasedPortNumber);
                    return false;
                }

                if (receivedByteCount != requestedByteCount)
                {
                    SetFault(
                        AudioRenderFaultCode.BassMidiShortRead,
                        0,
                        unit.Plan.CanonicalZeroBasedPortNumber);
                    return false;
                }
                _nativeSynthesisFrameCount += frameCount;
                if (cacheActive)
                {
                    if (!CopyScratchToCachedPcm(fragment!, frameCount))
                    {
                        _cacheCaptureInvalidated = true;
                    }
                    shouldMix = _sourceEnabled[fragment!.SourceIndex];
                }
                else
                {
                    shouldMix = true;
                }
            }

            if (shouldMix)
            {
                for (nuint sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
                {
                    destination[sampleIndex] += _portScratchBuffer[sampleIndex];
                }
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

    private bool PrepareCacheIoForBlock(int frameCount)
    {
        UnitPcmCacheIoBridge? cacheIo = _cacheIo;
        if (cacheIo is null)
        {
            return true;
        }

        bool readsReady = true;
        bool writesReady = true;
        for (int unitIndex = 0; unitIndex < _units.Length; unitIndex++)
        {
            UnitState unit = _units[unitIndex];
            MidiUnitFragmentRenderPlan? fragment = GetActiveFragment(unit);
            if (fragment?.PcmCacheKey is null)
            {
                continue;
            }
            bool cacheActive = !_sourceCacheBypassed[fragment.SourceIndex]
                || fragment.PcmCacheHit && unit.CachedMuteFadeRemainingFrames > 0;
            if (!cacheActive)
            {
                continue;
            }
            if (fragment.PcmCacheHit)
            {
                readsReady &= cacheIo.IsReadReady(
                    fragment,
                    _renderPositionFrames,
                    frameCount);
            }
            else
            {
                writesReady &= cacheIo.CanWriteFrames(
                    fragment,
                    _renderPositionFrames,
                    frameCount);
            }
        }
        if (cacheIo.ReadFaulted)
        {
            SetFault(AudioRenderFaultCode.PcmCacheReadFailed, 0, -1);
            return false;
        }
        if (cacheIo.WriteFaulted)
        {
            _cacheCaptureInvalidated = true;
        }
        return readsReady && writesReady;
    }

    internal static int ApplyCachedMuteFade(
        Span<float> interleavedStereo,
        int remainingFrames,
        int totalFrames)
    {
        if ((interleavedStereo.Length & 1) != 0)
        {
            throw new ArgumentException(
                "Cached monitoring PCM must contain complete stereo frames.",
                nameof(interleavedStereo));
        }
        if (remainingFrames < 0 || totalFrames <= 0 || remainingFrames > totalFrames)
        {
            throw new ArgumentOutOfRangeException(nameof(remainingFrames));
        }

        int frameCount = interleavedStereo.Length / 2;
        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            float gain = remainingFrames > 0
                ? remainingFrames / (float)totalFrames
                : 0f;
            int sampleIndex = frameIndex * 2;
            interleavedStereo[sampleIndex] *= gain;
            interleavedStereo[sampleIndex + 1] *= gain;
            if (remainingFrames > 0)
            {
                remainingFrames--;
            }
        }
        return remainingFrames;
    }

    private bool CopyCachedPcmToScratch(
        MidiUnitFragmentRenderPlan fragment,
        int frameCount)
    {
        return _cacheIo?.TryReadFrames(
            fragment,
            _renderPositionFrames,
            _portScratchBuffer,
            frameCount) == true;
    }

    private bool CopyScratchToCachedPcm(
        MidiUnitFragmentRenderPlan fragment,
        int frameCount)
    {
        return _cacheIo?.TryQueueWrite(
            fragment,
            _renderPositionFrames,
            _portScratchBuffer,
            frameCount) == true;
    }

    private void SetFault(AudioRenderFaultCode code, int nativeErrorCode, int zeroBasedPortNumber)
    {
        if (_fault.Code == AudioRenderFaultCode.None)
        {
            _fault = new AudioRenderFault(code, nativeErrorCode, zeroBasedPortNumber, _renderPositionFrames);
        }
    }

    private Exception? ReleaseNativeResources()
    {
        Exception? cleanupFailure = null;
        for (int i = _units.Length - 1; i >= 0; i--)
        {
            UnitState? unit = _units[i];
            if (unit is not null && unit.StreamHandle != 0)
            {
                if (NativeBass.StreamFree(unit.StreamHandle) == 0)
                {
                    int error = NativeBass.ErrorGetCode();
                    cleanupFailure = CombineFailures(
                        cleanupFailure,
                        new MidoraAudioException(
                            $"BASS_StreamFree for Port {unit.Plan.CanonicalZeroBasedPortNumber}, Channel {unit.Plan.CanonicalZeroBasedChannelNumber} failed with BASS error {error}."));
                }
                unit.StreamHandle = 0;
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

        if (_outputStagingBuffer != null)
        {
            NativeMemory.Free(_outputStagingBuffer);
            _outputStagingBuffer = null;
        }

        if (_packedMidiBuffer != null)
        {
            NativeMemory.Free(_packedMidiBuffer);
            _packedMidiBuffer = null;
        }

        if (_cacheIo is not null)
        {
            try
            {
                _cacheIo.Dispose();
            }
            catch (Exception exception)
            {
                cleanupFailure = CombineFailures(cleanupFailure, exception);
            }
            _cacheCaptureInvalidated |= _cacheIo.WriteFaulted;
            _cacheIo = null;
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowBassPreparationFailure(string operation, int error)
    {
        throw new MidoraAudioException($"{operation} failed with BASS error {error}.");
    }

    private int FindFirstUnitIndexForPort(int zeroBasedPortNumber)
    {
        int firstCanonicalUnitNumber = zeroBasedPortNumber * 16;
        for (int channel = 0; channel < 16; channel++)
        {
            int unitIndex = _unitIndexByCanonicalNumber[firstCanonicalUnitNumber + channel];
            if (unitIndex >= 0)
            {
                return unitIndex;
            }
        }

        return -1;
    }

    private sealed class UnitState(
        MidiUnitRenderPlan plan,
        uint streamHandle,
        MidiUnitFragmentRenderPlan[] fragments)
    {
        public MidiUnitRenderPlan Plan { get; set; } = plan;

        public uint StreamHandle { get; set; } = streamHandle;

        public int EventIndex { get; set; }

        public MidiUnitFragmentRenderPlan[] Fragments { get; set; } = fragments;

        public int FragmentIndex { get; set; }

        public bool FragmentInitialized { get; set; }

        public int CachedMuteFadeRemainingFrames { get; set; }
    }

    private enum FillOutputResult : byte
    {
        Success,
        Buffering,
        Fault
    }
}
