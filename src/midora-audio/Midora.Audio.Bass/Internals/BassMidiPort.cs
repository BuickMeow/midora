using Midora.Audio.Bass.Internals;
using Midora.AudioDevice;
using Midora.Midi;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NativeBass = Midora.NativeInterops.Bass.BASS;
using NativeBassMidi = Midora.NativeInterops.BassMidi.BASSMIDI;

namespace Midora.Audio.Bass;

public sealed unsafe class BassMidiRenderer
    : IMidiRenderer,
      IPlaybackSpanFallbackSource,
      IMonitoringResettableRenderSource
{
    private const int MaximumMidiBatchEventCount = 1_024;
    private const int MaximumPackedMidiBatchByteCount = MaximumMidiBatchEventCount * 3;
    private const int MonitoringCommandQueueCapacity = 4_096;
    private const int DeterministicNativeDecodeFrameCount = InitialReleaseAudioRuntimePolicy.WorkFrameCount;
    private const float InitialReleaseInterpolationQuality = 1f;
    private const float InitialReleaseCpuLimit = 0f;
    private MidiRenderPlan _plan;
    private readonly BassMidiRendererSettings _settings;
    private readonly BassNativeRuntime.Lease? _runtimeLease;
    private readonly UnitState[] _units;
    private SegmentState[] _segments;
    private int[][] _segmentIndicesBySource;
    private int[] _segmentCursorBySource;
    private int[] _activeSegmentIndexBySource;
    private readonly int[] _unitIndexByCanonicalNumber;
    private readonly bool[] _sourceEnabled;
    private readonly bool[] _sourceCacheBypassed;
    private int[][] _cacheOwnerIndicesBySource;
    private readonly MidiMonitoringCommand[] _monitoringCommands = new MidiMonitoringCommand[MonitoringCommandQueueCapacity];
    private readonly object _monitoringProducerSync = new();
    private readonly float _masterGain;
    private readonly bool _limiterEnabled;
    private StereoPeakLimiter _limiter;
    private byte* _packedMidiBuffer;
    private float* _unitScratchBuffer;
    private float* _segmentScratchBuffer;
    private float* _outputStagingBuffer;
    private uint _soundFontHandle;
    private readonly PersistentBassMidiSoundFont? _persistentSoundFont;
    private SegmentPcmCacheIoBridge? _cacheIo;
    private ParallelBassMidiDecodeCoordinator? _parallelDecoder;
    private MidiRenderEventStreamReader? _eventStreamReader;
    private long _positionFrames;
    private long _renderPositionFrames;
    private long _nativeSynthesisFrameCount;
    private long _cacheReadWaitCount;
    private long _cacheWriteWaitCount;
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
        string? cacheStagingPath = null,
        int segmentProducerConcurrency = 1,
        string? cacheReadManifestPath = null)
        : this(
            plan,
            soundFontPath,
            settings,
            masterSettings,
            cacheStagingPath,
            segmentProducerConcurrency,
            cacheReadManifestPath,
            persistentSoundFont: null)
    {
    }

    internal BassMidiRenderer(
        MidiRenderPlan plan,
        string soundFontPath,
        BassMidiRendererSettings settings,
        AudioMasterSettings masterSettings,
        string? cacheStagingPath,
        int segmentProducerConcurrency,
        string? cacheReadManifestPath,
        PersistentBassMidiSoundFont? persistentSoundFont)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(soundFontPath);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(masterSettings);
        if (segmentProducerConcurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentProducerConcurrency));
        }

        if (!File.Exists(soundFontPath))
        {
            throw new FileNotFoundException("The Project SoundFont does not exist.", soundFontPath);
        }

        _plan = plan;
        _settings = settings;
        _persistentSoundFont = persistentSoundFont;
        _masterGain = MathF.Pow(10f, masterSettings.VolumeDecibels / 20f);
        _limiterEnabled = masterSettings.LimiterEnabled;
        _limiter = new StereoPeakLimiter(
            plan.SampleRate,
            masterSettings.LimiterCeiling,
            masterSettings.LimiterReleaseMilliseconds);
        _fault = AudioRenderFault.None;
        _units = new UnitState[plan.Units.Length];
        _segments = plan.Segments.ToArray().Select(value => new SegmentState(value)).ToArray();
        (_segmentIndicesBySource, _segmentCursorBySource, _activeSegmentIndexBySource) =
            CreateSegmentSchedule(plan.SourceIds.Length, _segments);
        _unitIndexByCanonicalNumber = new int[16 * 16];
        Array.Fill(_unitIndexByCanonicalNumber, -1);
        _sourceEnabled = new bool[plan.SourceIds.Length];
        _sourceCacheBypassed = new bool[plan.SourceIds.Length];
        _cacheOwnerIndicesBySource = CreateCacheOwnerMap(
            plan.SourceIds.Length,
            plan.UnitFragments,
            plan.CacheSourceBindings);
        Array.Fill(_sourceEnabled, true);
        foreach (int sourceIndex in plan.InitiallyDisabledSourceIndices)
        {
            _sourceEnabled[sourceIndex] = false;
            // A merged Pure MIDI Root cache can contain events from this child
            // source. Monitoring-dependent playback must synthesize the filtered
            // event set instead of replaying that unfiltered PCM.
            foreach (int cacheOwnerIndex in _cacheOwnerIndicesBySource[sourceIndex])
            {
                _sourceCacheBypassed[cacheOwnerIndex] = true;
            }
        }
        _runtimeLease = BassNativeRuntime.Acquire();

        try
        {
            _packedMidiBuffer = (byte*)NativeMemory.Alloc(MaximumPackedMidiBatchByteCount);
            nuint scratchByteCount = checked(
                (nuint)DeterministicNativeDecodeFrameCount * 2 * sizeof(float));
            _unitScratchBuffer = (float*)NativeMemory.Alloc(checked(
                scratchByteCount * (nuint)Math.Max(1, _units.Length)));
            _segmentScratchBuffer = (float*)NativeMemory.Alloc(checked(
                scratchByteCount * (nuint)Math.Max(1, plan.SourceIds.Length)));
            _outputStagingBuffer = (float*)NativeMemory.Alloc(scratchByteCount);
            OpenCacheStaging(cacheStagingPath, cacheReadManifestPath);
            if (plan.EventStreamDescriptor is MidiRenderEventStreamDescriptor eventStreamDescriptor)
            {
                _eventStreamReader = new(eventStreamDescriptor);
            }
            if (_persistentSoundFont is null)
            {
                CreateSoundFont(soundFontPath);
            }
            else
            {
                _soundFontHandle = _persistentSoundFont.Handle;
            }
            PreloadReferencedPresets();
            CreateUnits();
            WarmNativeHotPath();
            if (_units.Length != 0)
            {
                _parallelDecoder = new ParallelBassMidiDecodeCoordinator(
                    _units.Select(value => value.StreamHandle).ToArray(),
                    _unitScratchBuffer,
                    DeterministicNativeDecodeFrameCount,
                    segmentProducerConcurrency);
            }
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
    internal long CacheReadWaitCountForDiagnostics => _cacheReadWaitCount;
    internal long CacheWriteWaitCountForDiagnostics => _cacheWriteWaitCount;

    internal Exception? CacheReadFaultForDiagnostics =>
        _lastCacheReadFault ?? _cacheIo?.ReadFault;

    private Exception? _lastCacheReadFault;
    private string? _lastCacheReadFaultText;

    internal string? CacheReadFaultTextForDiagnostics =>
        _lastCacheReadFaultText ?? _cacheIo?.ReadFaultText;


    internal long ParallelDecodeAllocatedBytesForDiagnostics =>
        _parallelDecoder?.WorkerAllocatedBytes ?? 0;

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
        _segments = plan.Segments.ToArray().Select(value => new SegmentState(value)).ToArray();
        (_segmentIndicesBySource, _segmentCursorBySource, _activeSegmentIndexBySource) =
            CreateSegmentSchedule(plan.SourceIds.Length, _segments);
        _cacheOwnerIndicesBySource = CreateCacheOwnerMap(
            plan.SourceIds.Length,
            plan.UnitFragments,
            plan.CacheSourceBindings);
        _plan = plan;
    }

    internal void SeekForMonitoringColdStart(long producerFrontierFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Volatile.Read(ref _pullActive) != 0
            || producerFrontierFrame < 0
            || producerFrontierFrame > _plan.TotalFrameCount)
        {
            throw new InvalidOperationException(
                "A playback-span cache fallback requires an unused renderer and a valid producer frontier.");
        }

        _limiter.Reset();
        _cacheCaptureInvalidated |= _positionFrames != 0 || _renderPositionFrames != 0;
        _stagedFrameOffset = 0;
        _stagedFrameCount = 0;
        for (int i = 0; i < _units.Length; i++)
        {
            UnitState unit = _units[i];
            EstablishCanonicalInitialState(unit.StreamHandle, unit.IsPercussion);
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
        }
        _positionFrames = producerFrontierFrame;
        _renderPositionFrames = producerFrontierFrame;
        _eventStreamReader?.Seek(producerFrontierFrame);
        Array.Clear(_segmentCursorBySource);
        Array.Fill(_activeSegmentIndexBySource, -1);
        UpdateActiveSegmentsAtCurrentFrame();
    }

    void IPlaybackSpanFallbackSource.ResetForMonitoringColdStart(
        long producerFrontierFrame,
        ReadOnlySpan<MidiMonitoringCommand> commands)
    {
        SeekForMonitoringColdStart(producerFrontierFrame);
        EnqueueMonitoringCommands(commands);
    }

    void IMonitoringResettableRenderSource.ResetForMonitoringColdStart(
        long producerFrontierFrame,
        ReadOnlySpan<MidiMonitoringCommand> commands)
    {
        SeekForMonitoringColdStart(producerFrontierFrame);
        EnqueueMonitoringCommands(commands);
    }

    private FillOutputResult FillOutputStagingBuffer()
    {
        MidiRenderEventStreamReader? streamReader = _eventStreamReader;
        if (streamReader is not null)
        {
            long lookAheadFrames = RollingAudioPreparationPolicy.MillisecondsToFrames(
                _plan.SampleRate,
                RollingAudioPreparationPolicy.TargetHighWatermarkMilliseconds);
            long demand = _renderPositionFrames > _plan.TotalFrameCount - Math.Min(
                    _plan.TotalFrameCount,
                    lookAheadFrames)
                ? _plan.TotalFrameCount
                : _renderPositionFrames + lookAheadFrames;
            streamReader.RequestThrough(demand);
            if (streamReader.IsFaulted)
            {
                SetFault(AudioRenderFaultCode.InvalidPullRequest, 0, -1);
                return FillOutputResult.Fault;
            }
        }
        UpdateActiveSegmentsAtCurrentFrame();
        if (!ApplyPendingMonitoringCommands()
            || !PrepareFragmentStatesAtCurrentFrame())
        {
            return FillOutputResult.Fault;
        }

        if (streamReader is not null
            && !streamReader.IsCompleted
            && streamReader.SafeThroughFrame <= _renderPositionFrames)
        {
            return SubmitEventsAtCurrentFrame()
                ? FillOutputResult.Buffering
                : FillOutputResult.Fault;
        }

        // Streaming plans expose only the next unread event. Consume every event
        // at the current frame before asking for the next boundary; otherwise the
        // current event hides a later event inside the same native decode block.
        if (!SubmitEventsAtCurrentFrame())
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
        if (streamReader is not null && !streamReader.IsCompleted)
        {
            frameCount = (int)Math.Min(
                frameCount,
                Math.Max(0, streamReader.SafeThroughFrame - _renderPositionFrames));
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
        if (!RenderFrames(_outputStagingBuffer, frameCount))
        {
            return _fault.Code == AudioRenderFaultCode.None
                ? FillOutputResult.Buffering
                : FillOutputResult.Fault;
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

    private void OpenCacheStaging(
        string? cacheStagingPath,
        string? cacheReadManifestPath)
    {
        long requiredLength = 0;
        foreach (MidiSegmentRenderPlan segment in _plan.Segments)
        {
            if (segment.PcmCacheKey is null)
            {
                continue;
            }
            if (segment.PcmCacheHit && segment.PcmCachePayloadOffset < 0)
            {
                continue;
            }
            requiredLength = Math.Max(
                requiredLength,
                checked(segment.PcmCachePayloadOffset + segment.PcmPayloadByteCount));
        }
        if (requiredLength == 0)
        {
            if (string.IsNullOrWhiteSpace(cacheReadManifestPath))
            {
                return;
            }
        }
        if (string.IsNullOrWhiteSpace(cacheStagingPath))
        {
            throw new InvalidDataException(
                "The render plan contains Segment PCM cache bindings without a staging file.");
        }
        string path = Path.GetFullPath(cacheStagingPath);
        bool usesPackJournal = Directory.Exists(
            AudioCachePackJournal.GetDirectoryPath(path));
        long expectedStagingLength = usesPackJournal ? 0 : requiredLength;
        if (!File.Exists(path)
            || new FileInfo(path).Length != expectedStagingLength)
        {
            throw new InvalidDataException(
                "The Segment PCM cache staging file length does not match the render plan.");
        }

        _cacheIo = new SegmentPcmCacheIoBridge(path, cacheReadManifestPath, _plan);
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
        if (_persistentSoundFont is not null)
        {
            _persistentSoundFont.EnsureReferencedPresets(plan);
            return;
        }
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
        return plan.ReferencedPresetKeys.ToArray();
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

    private static (int[][] BySource, int[] Cursors, int[] Active) CreateSegmentSchedule(
        int sourceCount,
        SegmentState[] segments)
    {
        int[][] bySource = new int[sourceCount][];
        for (int sourceIndex = 0; sourceIndex < sourceCount; sourceIndex++)
        {
            int capturedSourceIndex = sourceIndex;
            bySource[sourceIndex] = segments
                .Select((value, index) => (value, index))
                .Where(value => value.value.Plan.SourceIndex == capturedSourceIndex)
                .OrderBy(value => value.value.Plan.StartFrame)
                .ThenBy(value => value.value.Plan.SegmentId)
                .Select(value => value.index)
                .ToArray();
        }
        int[] active = new int[sourceCount];
        Array.Fill(active, -1);
        return (bySource, new int[sourceCount], active);
    }

    private static int[][] CreateCacheOwnerMap(
        int sourceCount,
        ReadOnlySpan<MidiUnitFragmentRenderPlan> fragments,
        ReadOnlySpan<MidiRenderCacheSourceBinding> bindings)
    {
        MidiUnitFragmentRenderPlan[] frozenFragments = fragments.ToArray();
        int[][] result = new int[sourceCount][];
        for (int sourceIndex = 0; sourceIndex < sourceCount; sourceIndex++)
        {
            HashSet<int> owners = [];
            foreach (MidiRenderCacheSourceBinding binding in bindings)
                if (binding.SourceIndex == sourceIndex)
                    owners.Add(binding.CacheOwnerSourceIndex);
            foreach (MidiUnitFragmentRenderPlan fragment in frozenFragments)
            {
                if (fragment.SourceIndex == sourceIndex)
                {
                    owners.Add(fragment.SourceIndex);
                    continue;
                }

                foreach (ScheduledMidiMessage scheduled in fragment.Events)
                {
                    if (scheduled.SourceIndex == sourceIndex)
                    {
                        owners.Add(fragment.SourceIndex);
                        break;
                    }
                }
            }

            result[sourceIndex] = owners.Order().ToArray();
        }
        return result;
    }

    private void UpdateActiveSegmentsAtCurrentFrame()
    {
        for (int sourceIndex = 0; sourceIndex < _segmentIndicesBySource.Length; sourceIndex++)
        {
            int[] schedule = _segmentIndicesBySource[sourceIndex];
            int cursor = _segmentCursorBySource[sourceIndex];
            while (cursor < schedule.Length
                && _segments[schedule[cursor]].Plan.EndFrame <= _renderPositionFrames)
            {
                cursor++;
            }
            _segmentCursorBySource[sourceIndex] = cursor;
            _activeSegmentIndexBySource[sourceIndex] = cursor < schedule.Length
                && _segments[schedule[cursor]].Plan.StartFrame <= _renderPositionFrames
                ? schedule[cursor]
                : -1;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private SegmentState? GetActiveSegment(int sourceIndex)
    {
        if ((uint)sourceIndex >= (uint)_activeSegmentIndexBySource.Length)
        {
            return null;
        }
        int index = _activeSegmentIndexBySource[sourceIndex];
        return index >= 0 ? _segments[index] : null;
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

            bool isPercussion = fragments.Length != 0 && fragments[0].IsPercussion;
            if (fragments.Any(value => value.IsPercussion != isPercussion))
            {
                throw new InvalidDataException(
                    "One canonical Channel Unit cannot mix melodic and percussion fragments.");
            }
            EstablishCanonicalInitialState(streamHandle, isPercussion);

            return new UnitState(plan, streamHandle, fragments, isPercussion);
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

            uint available = NativeBass.ChannelGetData(unit.StreamHandle, _unitScratchBuffer, 0);
            if (available == uint.MaxValue)
            {
                ThrowBassPreparationFailure("BASS_ChannelGetData warm-up");
            }
        }
    }

    private static void EstablishCanonicalInitialState(uint streamHandle, bool isPercussion)
    {
        const uint channel = 0;
        // MIDI_EVENT_RESET is CC121: it resets controllers but deliberately does
        // not release keys. A monitoring cold start can rewind a stream after the
        // rolling producer has already submitted future NoteOns, so kill both the
        // sounding voices and pressed-key state before restoring canonical state.
        SubmitRequiredStreamEvent(
            streamHandle,
            channel,
            NativeBassMidi.MIDI_EVENT_NOTESOFF,
            "MIDI_EVENT_NOTESOFF");
        SubmitRequiredStreamEvent(
            streamHandle,
            channel,
            NativeBassMidi.MIDI_EVENT_SOUNDOFF,
            "MIDI_EVENT_SOUNDOFF");
        SubmitRequiredStreamEvent(
            streamHandle,
            channel,
            NativeBassMidi.MIDI_EVENT_RESET,
            "MIDI_EVENT_RESET");
        SubmitRequiredStreamEvent(
            streamHandle,
            channel,
            NativeBassMidi.MIDI_EVENT_DEFDRUMS,
            "MIDI_EVENT_DEFDRUMS",
            isPercussion ? 1u : 0u);
    }

    private static void SubmitRequiredStreamEvent(
        uint streamHandle,
        uint channel,
        uint midiEvent,
        string eventName,
        uint parameter = 0)
    {
        if (NativeBassMidi.StreamEvent(streamHandle, channel, midiEvent, parameter) == 0)
        {
            ThrowBassPreparationFailure($"BASS_MIDI_StreamEvent({eventName})");
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

                if (submitted == uint.MaxValue)
                {
                    int error = NativeBass.ErrorGetCode();
                    SetFault(
                        AudioRenderFaultCode.BassMidiEventSubmissionFailed,
                        error,
                        unit.Plan.CanonicalZeroBasedPortNumber);
                    return false;
                }

                // The return value is the count of events BASS processed, not a RAW-input
                // consumption offset. Controller/RPN sequences can therefore return fewer
                // processed events than the number of MIDI messages supplied. Any result
                // other than uint.MaxValue is success for this complete input buffer; never
                // split and resubmit a suffix based on the processed-event count.
                unit.EventIndex = scanIndex;
            }
        }

        return SubmitStreamingEventsAtCurrentFrame();
    }

    private bool SubmitStreamingEventsAtCurrentFrame()
    {
        MidiRenderEventStreamReader? reader = _eventStreamReader;
        if (reader is null) return true;
        while (reader.TryPeek(out ScheduledPortMidiMessage next))
        {
            if (next.Scheduled.SampleFrame < _renderPositionFrames)
            {
                SetFault(
                    AudioRenderFaultCode.BassMidiEventSubmissionFailed,
                    0,
                    next.ZeroBasedPortNumber);
                return false;
            }
            if (next.Scheduled.SampleFrame != _renderPositionFrames) return true;

            int canonicalUnitNumber = next.ZeroBasedPortNumber * 16
                + next.Scheduled.Message.ChannelNumber;
            int unitIndex = _unitIndexByCanonicalNumber[canonicalUnitNumber];
            if (unitIndex < 0)
            {
                SetFault(
                    AudioRenderFaultCode.BassMidiEventSubmissionFailed,
                    0,
                    next.ZeroBasedPortNumber);
                return false;
            }
            UnitState unit = _units[unitIndex];
            int batchCount = 0;
            int packedByteCount = 0;
            while (batchCount < MaximumMidiBatchEventCount
                && reader.TryPeek(out next)
                && next.Scheduled.SampleFrame == _renderPositionFrames
                && next.ZeroBasedPortNumber * 16 + next.Scheduled.Message.ChannelNumber
                    == canonicalUnitNumber)
            {
                _ = reader.TryDequeue(out ScheduledPortMidiMessage scheduledPort);
                ScheduledMidiMessage canonical = scheduledPort.Scheduled;
                MidiMessage unitMessage = MidiMessage.FromPackedValue(
                    canonical.Message.PackedValue & ~MidiMessage.ChannelNumberMask);
                ScheduledMidiMessage scheduled = canonical with { Message = unitMessage };
                if (!ShouldSubmitScheduledEvent(unit, scheduled)) continue;
                _packedMidiBuffer[packedByteCount++] = unitMessage.Byte0;
                _packedMidiBuffer[packedByteCount++] = unitMessage.Byte1;
                if (unitMessage.Length == 3)
                    _packedMidiBuffer[packedByteCount++] = unitMessage.Byte2;
                batchCount++;
            }
            if (batchCount == 0) continue;
            uint submitted = NativeBassMidi.StreamEvents(
                unit.StreamHandle,
                NativeBassMidi.BASS_MIDI_EVENTS_RAW | NativeBassMidi.BASS_MIDI_EVENTS_NORSTATUS,
                _packedMidiBuffer,
                (uint)packedByteCount);
            if (submitted == uint.MaxValue)
            {
                SetFault(
                    AudioRenderFaultCode.BassMidiEventSubmissionFailed,
                    NativeBass.ErrorGetCode(),
                    unit.Plan.CanonicalZeroBasedPortNumber);
                return false;
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
                foreach (int cacheOwnerIndex in _cacheOwnerIndicesBySource[command.SourceIndex])
                {
                    if (_sourceCacheBypassed[cacheOwnerIndex])
                    {
                        continue;
                    }

                    _sourceCacheBypassed[cacheOwnerIndex] = true;
                    _cacheCaptureInvalidated |= HasPcmCacheBindings();
                    ResetCachedHitStreamsForSource(cacheOwnerIndex);
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
        if (submitted != uint.MaxValue)
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
        if (_eventStreamReader?.TryPeek(out ScheduledPortMidiMessage streaming) == true
            && streaming.Scheduled.SampleFrame > _renderPositionFrames)
        {
            result = Math.Min(result, streaming.Scheduled.SampleFrame);
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
            SegmentState? segment = GetActiveSegment(fragment.SourceIndex);
            if (segment?.Plan.SegmentId == fragment.SegmentId
                && segment.Plan.PcmCacheHit
                && !_sourceCacheBypassed[fragment.SourceIndex])
            {
                unit.FragmentInitialized = true;
                continue;
            }
            try
            {
                EstablishCanonicalInitialState(unit.StreamHandle, unit.IsPercussion);
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
        SegmentState? segment = fragment is null
            ? null
            : GetActiveSegment(fragment.SourceIndex);
        if (fragment is not null
            && segment?.Plan.SegmentId == fragment.SegmentId
            && segment.Plan.PcmCacheKey is not null
            && !_sourceCacheBypassed[fragment.SourceIndex])
        {
            return !segment.Plan.PcmCacheHit;
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
        for (int i = 0; i < _segments.Length; i++)
        {
            if (_segments[i].Plan.PcmCacheKey is not null)
            {
                return true;
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
            SegmentState? segment = fragment is null
                ? null
                : GetActiveSegment(fragment.SourceIndex);
            if (fragment is not null
                && fragment.SourceIndex == sourceIndex
                && segment?.Plan.SegmentId == fragment.SegmentId
                && segment.Plan.PcmCacheHit)
            {
                EstablishCanonicalInitialState(unit.StreamHandle, unit.IsPercussion);
            }
        }
    }

    private bool RenderFrames(float* destination, int frameCount)
    {
        nuint sampleCount = checked((nuint)frameCount * 2);
        NativeMemory.Clear(destination, checked(sampleCount * sizeof(float)));
        uint requestedByteCount = checked((uint)(sampleCount * sizeof(float)));

        // A Segment stem is accumulated in its source-owned scratch slot. Logical
        // Track Segments cannot overlap, so one bounded slot per source is enough.
        for (int sourceIndex = 0; sourceIndex < _activeSegmentIndexBySource.Length; sourceIndex++)
        {
            SegmentState? segment = GetActiveSegment(sourceIndex);
            if (segment?.Plan.PcmCacheKey is null
                || _sourceCacheBypassed[sourceIndex]
                || _cacheCaptureInvalidated && !segment.Plan.PcmCacheHit)
            {
                continue;
            }
            float* segmentScratch = _segmentScratchBuffer
                + checked(sourceIndex * DeterministicNativeDecodeFrameCount * 2);
            if (segment.Plan.PcmCacheHit)
            {
                if (!CopyCachedPcmToScratch(segment.Plan, segmentScratch, frameCount))
                {
                    if (_cacheIo?.ReadFaulted == true)
                    {
                        _lastCacheReadFault = _cacheIo.ReadFault;
                        _lastCacheReadFaultText = _cacheIo.ReadFaultText;
                        SetFault(AudioRenderFaultCode.PcmCacheReadFailed, 0, -1);
                        return false;
                    }
                    return false;
                }
                if (_sourceEnabled[sourceIndex])
                {
                    MixScratchInto(destination, segmentScratch, sampleCount);
                }
            }
            else
            {
                NativeMemory.Clear(segmentScratch, checked(sampleCount * sizeof(float)));
            }
        }

        ParallelBassMidiDecodeCoordinator? decoder = _parallelDecoder;
        Span<bool> decodeRequired = decoder is null
            ? Span<bool>.Empty
            : decoder.Required;
        decodeRequired.Clear();
        for (int unitIndex = 0; unitIndex < _units.Length; unitIndex++)
        {
            UnitState unit = _units[unitIndex];
            MidiUnitFragmentRenderPlan? fragment = GetActiveFragment(unit);
            if (unit.Fragments.Length != 0 && fragment is null)
            {
                continue;
            }
            SegmentState? segment = fragment is null
                ? null
                : GetActiveSegment(fragment.SourceIndex);
            bool cacheActive = fragment is not null
                && segment?.Plan.SegmentId == fragment.SegmentId
                && segment.Plan.PcmCacheKey is not null
                && !_sourceCacheBypassed[fragment.SourceIndex]
                && (segment.Plan.PcmCacheHit || !_cacheCaptureInvalidated);
            if (cacheActive && segment!.Plan.PcmCacheHit)
            {
                // The cached Segment stem has already been mixed exactly once above.
                continue;
            }
            decodeRequired[unitIndex] = true;
        }
        decoder?.Decode(requestedByteCount);

        for (int unitIndex = 0; unitIndex < _units.Length; unitIndex++)
        {
            if (!decodeRequired[unitIndex])
            {
                continue;
            }
            UnitState unit = _units[unitIndex];
            uint receivedByteCount = decoder!.GetResult(unitIndex);
            if (receivedByteCount == uint.MaxValue)
            {
                SetFault(
                    AudioRenderFaultCode.BassMidiDecodeFailed,
                    decoder.GetError(unitIndex),
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

            float* unitScratch = _unitScratchBuffer
                + checked(unitIndex * DeterministicNativeDecodeFrameCount * 2);
            MidiUnitFragmentRenderPlan? fragment = GetActiveFragment(unit);
            SegmentState? segment = fragment is null
                ? null
                : GetActiveSegment(fragment.SourceIndex);
            bool cacheActive = fragment is not null
                && segment?.Plan.SegmentId == fragment.SegmentId
                && segment.Plan.PcmCacheKey is not null
                && !_sourceCacheBypassed[fragment.SourceIndex]
                && (segment.Plan.PcmCacheHit || !_cacheCaptureInvalidated);

            if (cacheActive)
            {
                float* segmentScratch = _segmentScratchBuffer
                    + checked(fragment!.SourceIndex * DeterministicNativeDecodeFrameCount * 2);
                MixScratchInto(segmentScratch, unitScratch, sampleCount);
            }
            else if (fragment is null
                || fragment.SourceIndex < 0
                || _sourceEnabled[fragment.SourceIndex])
            {
                MixScratchInto(destination, unitScratch, sampleCount);
            }
        }

        for (int sourceIndex = 0; sourceIndex < _activeSegmentIndexBySource.Length; sourceIndex++)
        {
            SegmentState? segment = GetActiveSegment(sourceIndex);
            if (segment?.Plan.PcmCacheKey is null
                || segment.Plan.PcmCacheHit
                || _sourceCacheBypassed[sourceIndex]
                || _cacheCaptureInvalidated)
            {
                continue;
            }
            float* segmentScratch = _segmentScratchBuffer
                + checked(sourceIndex * DeterministicNativeDecodeFrameCount * 2);
            if (!CopyScratchToCachedPcm(segment.Plan, segmentScratch, frameCount))
            {
                return false;
            }
            if (_sourceEnabled[sourceIndex])
            {
                MixScratchInto(destination, segmentScratch, sampleCount);
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MixScratchInto(float* destination, float* source, nuint sampleCount)
    {
        for (nuint sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            destination[sampleIndex] += source[sampleIndex];
        }
    }

    private bool PrepareCacheIoForBlock(int frameCount)
    {
        SegmentPcmCacheIoBridge? cacheIo = _cacheIo;
        if (cacheIo is null)
        {
            return true;
        }

        bool ioReady = true;
        bool readWaited = false;
        bool writeWaited = false;
        for (int sourceIndex = 0; sourceIndex < _activeSegmentIndexBySource.Length; sourceIndex++)
        {
            SegmentState? segment = GetActiveSegment(sourceIndex);
            if (segment?.Plan.PcmCacheKey is null || _sourceCacheBypassed[sourceIndex])
            {
                continue;
            }
            if (segment.Plan.PcmCacheHit)
            {
                bool ready = cacheIo.IsReadReady(
                    segment.Plan,
                    _renderPositionFrames,
                    frameCount);
                ioReady &= ready;
                readWaited |= !ready;
            }
            else
            {
                if (!_cacheCaptureInvalidated)
                {
                    bool ready = cacheIo.CanWriteFrames(
                        segment.Plan,
                        _renderPositionFrames,
                        frameCount);
                    ioReady &= ready;
                    writeWaited |= !ready;
                }
            }
        }
        if (cacheIo.ReadFaulted)
        {
            _lastCacheReadFault = cacheIo.ReadFault;
            _lastCacheReadFaultText = cacheIo.ReadFaultText;
            SetFault(AudioRenderFaultCode.PcmCacheReadFailed, 0, -1);
            return false;
        }
        if (cacheIo.WriteFaulted)
        {
            _cacheCaptureInvalidated = true;
        }
        if (readWaited)
        {
            _cacheReadWaitCount++;
        }
        if (writeWaited)
        {
            _cacheWriteWaitCount++;
        }
        // The rolling preparation ring absorbs normal writer jitter. Once the
        // bounded writer backlog is full, synthesis waits at the same frame so
        // completed Segment generations are not silently discarded and rebuilt
        // on every playback. The device side reaches controlled Buffering only if
        // the prepared high-water window is actually exhausted.
        return ioReady;
    }

    private bool CopyCachedPcmToScratch(
        MidiSegmentRenderPlan segment,
        float* destination,
        int frameCount)
    {
        return _cacheIo?.TryReadFrames(
            segment,
            _renderPositionFrames,
            destination,
            frameCount) == true;
    }

    private bool CopyScratchToCachedPcm(
        MidiSegmentRenderPlan segment,
        float* source,
        int frameCount)
    {
        return _cacheIo?.TryQueueWrite(
            segment,
            _renderPositionFrames,
            source,
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
        if (_eventStreamReader is not null)
        {
            try
            {
                _eventStreamReader.Dispose();
            }
            catch (Exception exception)
            {
                cleanupFailure = CombineFailures(cleanupFailure, exception);
            }
            _eventStreamReader = null;
        }
        if (_parallelDecoder is not null)
        {
            try
            {
                _parallelDecoder.Dispose();
            }
            catch (Exception exception)
            {
                cleanupFailure = CombineFailures(cleanupFailure, exception);
            }
            _parallelDecoder = null;
        }
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

        if (_soundFontHandle != 0 && _persistentSoundFont is null)
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

        if (_unitScratchBuffer != null)
        {
            NativeMemory.Free(_unitScratchBuffer);
            _unitScratchBuffer = null;
        }
        else
        {
            _soundFontHandle = 0;
        }

        if (_segmentScratchBuffer != null)
        {
            NativeMemory.Free(_segmentScratchBuffer);
            _segmentScratchBuffer = null;
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
        MidiUnitFragmentRenderPlan[] fragments,
        bool isPercussion)
    {
        public MidiUnitRenderPlan Plan { get; set; } = plan;

        public uint StreamHandle { get; set; } = streamHandle;

        public int EventIndex { get; set; }

        public MidiUnitFragmentRenderPlan[] Fragments { get; set; } = fragments;

        public int FragmentIndex { get; set; }

        public bool FragmentInitialized { get; set; }

        public bool IsPercussion { get; set; } = isPercussion;

    }

    private sealed class SegmentState(MidiSegmentRenderPlan plan)
    {
        public MidiSegmentRenderPlan Plan { get; } = plan;

    }

    private enum FillOutputResult : byte
    {
        Success,
        Buffering,
        Fault
    }
}
