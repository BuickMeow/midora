using Midora.Audio;
using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;
using System.Runtime.InteropServices;

namespace Midora.Playback;

public enum PlaybackState
{
    Stopped,
    Preparing,
    Playing,
    Buffering,
    Stopping,
    Error
}

public enum PlaybackTaskKind
{
    None,
    MainTimeline,
    SegmentPreview,
    EventInstrumentPreview,
    SubVoicePreview
}

public interface IRealtimePlaybackBackend : IDisposable
{
    int ActualSampleRate { get; }
    long PositionFrames { get; }
    long RenderPositionFrames { get; }
    bool IsBuffering { get; }
    bool IsCompleted { get; }
    bool IsFaulted { get; }
    string? FaultDescription { get; }
    bool OutputDeviceSelectionRequired { get; }
    string? OutputDeviceSelectionReason { get; }
    int Prepare();
    void Start(MidiRenderPlan plan, string soundFontPath, PlaybackMasterConfiguration master);
    void ApplyMonitoringCommands(ReadOnlySpan<MidiMonitoringCommand> commands);
    void Stop(bool flush);
    void Reset();
    void SelectOutputDevice(string? deviceId);
}

public interface ICancellableRealtimePlaybackPreparationBackend
{
    int Prepare(CancellationToken cancellationToken);
}

public interface IHeldPreviewRealtimePlaybackBackend
{
    long PauseHeldPreviewAtProducerFrontier(TimeSpan timeout);
    void ReplaceHeldPreviewFutureAndResume(
        MidiRenderPlan plan,
        long producerFrontierFrame,
        TimeSpan timeout);
    void ResumeHeldPreviewFromProducerFrontier();
}

public interface IRealtimePlaybackCacheStore : IAudioPcmCacheSessionAccess
{
    bool TryReadReusableAudio(string key, out byte[] payload);
    AudioCachePublishResult PublishReusableAudio(string key, ReadOnlySpan<byte> payload);
}

public interface IRealtimePlaybackCacheBackend
{
    void SetAudioCacheStore(IRealtimePlaybackCacheStore cacheStore);
    void SetNextPlaybackCacheMode(RealtimePlaybackCacheMode mode);
}

public interface IRealtimePlaybackSoundFontBackend
{
    void SetSoundFontIdentity(string? soundFontPath, string? verifiedSha256);
}

public interface ISimplePitchAuditionRealtimePlaybackBackend
{
    void BeginPitchAudition(int pitch, int velocity);
    void EndPitchAudition();
}

public enum RealtimePlaybackCacheMode
{
    Disabled,
    UnitPcm,
    UnitPcmAndPlaybackSpan
}

public interface IBufferingRecoveryRealtimePlaybackBackend
{
    void SetNextBufferingRecoveryStorage(
        AudioCacheSessionStore.AudioRecoverySpool? recoverySpool,
        long memoryFallbackFrameCapacity);

    bool HasBufferingRecoveryStorage { get; }

    void BeginBufferingRecovery(long recoveryEndFrame);
}

public sealed class OutputDeviceSelectionRequiredException : InvalidOperationException
{
    public OutputDeviceSelectionRequiredException(string message)
        : base(message)
    {
    }
}

public readonly record struct PlaybackMasterConfiguration(float VolumeDecibels, bool LimiterEnabled);

public readonly record struct HeldPreviewGateEndReport(
    long FinalGateLengthTicks,
    long ConsumedFrameAtGateEnd,
    long ProducerFrontierFrame,
    long QueuedLatencyFrameCount,
    double QueuedLatencyMilliseconds);

public sealed class PlaybackController : IDisposable
{
    private const int HeldPreviewWindowSeconds = 8;
    private const int HeldPreviewRenewalThresholdSeconds = 4;
    private static readonly TimeSpan HeldPreviewBackendTimeout = TimeSpan.FromSeconds(5);
    private readonly ProjectCompilationSession _session;
    private readonly IRealtimePlaybackBackend _backend;
    private readonly HashSet<MidoraId> _mutedTracks = [];
    private readonly HashSet<MidoraId> _soloTracks = [];
    private readonly HashSet<MidoraId> _audibleTracks = [];
    private readonly object _backendPreparationSync = new();
    private readonly object _prewarmSync = new();
    private readonly CancellationTokenSource _prewarmCancellation = new();
    private Task _prewarmTask = Task.CompletedTask;
    private long _prewarmRequestGeneration;
    private int _knownSampleRate;
    private CanonicalCompiledResult? _activeResult;
    private MidiRenderPlan? _activePlan;
    private TempoSampleMap? _activeTempoMap;
    private long _taskStartTick;
    private long _cursorTick;
    private long? _requestedEndTick;
    private TickRange? _loopRange;
    private IDisposable? _editLockLease;
    private Func<long, CanonicalCompiledResult>? _heldPreviewOpenCompiler;
    private Func<long, long, CanonicalCompiledResult>? _heldPreviewEndCompiler;
    private decimal _heldPreviewTempo;
    private long _heldPreviewWindowEndTick;
    private bool _heldPreviewGateOpen;
    private bool _releaseEditLockAtHeldGateEnd;
    private AudioRecoveryStorageUnavailableException? _recoveryStorageFailure;
    private bool _bufferingRecoveryRequested;
    private bool _disposed;

    public PlaybackController(ProjectCompilationSession session, IRealtimePlaybackBackend backend)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        if (backend is IRealtimePlaybackCacheBackend cacheBackend)
        {
            cacheBackend.SetAudioCacheStore(session);
        }
        RebuildAudibleTracks();
        _session.CompilationChanged += HandleCompilationChangedForPrewarm;
        _session.EffectiveSoundFontChanged += HandleEffectiveSoundFontChanged;
        RefreshBackendSoundFontIdentity();
    }

    public PlaybackState State { get; private set; } = PlaybackState.Stopped;
    public PlaybackTaskKind ActiveTaskKind { get; private set; }
    public Exception? LastError { get; private set; }
    public bool OutputDeviceSelectionRequired { get; private set; }
    public bool IsHeldPreviewGateOpen => _heldPreviewGateOpen;
    public HeldPreviewGateEndReport? LastHeldPreviewGateEndReport { get; private set; }
    public TickRange? LoopRange => _loopRange;
    public event EventHandler? StateChanged;

    public void BeginDefaultPlaybackPreparation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_prewarmSync)
        {
            _prewarmRequestGeneration = checked(_prewarmRequestGeneration + 1);
            if (_prewarmTask.IsCompleted)
            {
                _prewarmTask = Task.Run(
                    RunDefaultPlaybackPreparationLoop,
                    _prewarmCancellation.Token);
            }
        }
    }

    public long CurrentTick
    {
        get
        {
            if (ActiveTaskKind is not PlaybackTaskKind.MainTimeline)
            {
                return _cursorTick;
            }
            return CurrentTaskTick;
        }
    }

    public long CurrentTaskTick
    {
        get
        {
            CanonicalCompiledResult? active = _activeResult;
            TempoSampleMap? map = _activeTempoMap;
            if (active is null || map is null || State is PlaybackState.Stopped or PlaybackState.Error)
            {
                return _cursorTick;
            }
            return map.SampleFrameToTick(
                _backend.PositionFrames, active.StartTick, _backend.ActualSampleRate, active.EndTick);
        }
    }

    public void Start(long? cursorTick = null, long? endTick = null)
    {
        EnsureCanStartTask();
        long effectiveCursorTick = cursorTick ?? _cursorTick;
        if (effectiveCursorTick < 0 || endTick.HasValue && endTick < effectiveCursorTick)
        {
            throw new ArgumentOutOfRangeException(nameof(cursorTick), "Playback requires a non-negative, non-reversed range.");
        }
        long? effectiveEndTick = EffectiveEndTick(endTick);
        if (effectiveEndTick < effectiveCursorTick)
        {
            throw new ArgumentOutOfRangeException(nameof(cursorTick),
                "The effective playback or loop end must not precede the playback cursor.");
        }
        _ = RequireEffectiveSoundFont("Playback");
        _taskStartTick = effectiveCursorTick;
        _cursorTick = effectiveCursorTick;
        _requestedEndTick = endTick;
        StartPreparedRange(effectiveCursorTick, effectiveEndTick, acquireEditLock: true);
    }

    public void StartEventInstrumentPreview(EventInstrumentPreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureCanStartTask();
        StartPreview(
            () => new PreviewCompiler().CompileEventInstrument(_session.Project, request),
            request.SubVoiceId.HasValue
                ? PlaybackTaskKind.SubVoicePreview
                : PlaybackTaskKind.EventInstrumentPreview);
    }

    public void BeginPitchAudition(int pitch, int velocity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (pitch is < 0 or > 127 || velocity is < 1 or > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(pitch));
        }
        if (State != PlaybackState.Stopped || ActiveTaskKind != PlaybackTaskKind.None)
        {
            throw new InvalidOperationException(
                "Pitch audition is unavailable while a formal audio task is active.");
        }
        _ = RequireEffectiveSoundFont("Pitch audition");
        if (_backend is not ISimplePitchAuditionRealtimePlaybackBackend auditionBackend)
        {
            throw new NotSupportedException(
                "The selected realtime backend does not support simple pitch audition.");
        }
        auditionBackend.BeginPitchAudition(pitch, velocity);
    }

    public void EndPitchAudition()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_backend is ISimplePitchAuditionRealtimePlaybackBackend auditionBackend)
        {
            auditionBackend.EndPitchAudition();
        }
    }

    public void StartHeldEventInstrumentPreview(EventInstrumentPreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.GateLengthTicks.HasValue)
        {
            throw new ArgumentException(
                "A held-preview Gate Start must not carry a final Gate Length.",
                nameof(request));
        }
        decimal previewTempo = ResolvePreviewTempo(_session.Project, request);
        StartHeldPreviewCore(
            windowEndTick => new PreviewCompiler().CompileHeldEventInstrumentGateOpen(
                _session.Project,
                request,
                windowEndTick),
            (finalGateLength, effectiveGateEndTick) =>
                new PreviewCompiler().CompileHeldEventInstrumentGateEnd(
                    _session.Project,
                    request,
                    finalGateLength,
                    effectiveGateEndTick),
            previewTempo,
            request.SubVoiceId.HasValue
                ? PlaybackTaskKind.SubVoicePreview
                : PlaybackTaskKind.EventInstrumentPreview,
            releaseEditLockAtGateEnd: false);
    }

    public void StartHeldSegmentPitchRulerPreview(
        MidoraId trackId,
        MidoraId segmentId,
        int pitch,
        int velocity,
        decimal previewTempo)
    {
        LogicalTrack track = _session.Project.Tracks.FirstOrDefault(value => value.Id == trackId)
            ?? throw new ArgumentOutOfRangeException(nameof(trackId));
        Segment segment = track.Segments.FirstOrDefault(value => value.Id == segmentId)
            ?? throw new ArgumentOutOfRangeException(nameof(segmentId));
        MidoraId instrumentId = track.EventInstrumentId
            ?? throw new InvalidOperationException(
                "Pitch Ruler preview requires a bound Event Instrument.");
        if (!_session.Project.EventInstruments.Any(value => value.Id == instrumentId))
        {
            throw new InvalidOperationException(
                "The Pitch Ruler preview Event Instrument binding is missing or damaged.");
        }
        StartHeldEventInstrumentPreview(new EventInstrumentPreviewRequest(
            instrumentId,
            Pitch: pitch,
            Velocity: velocity,
            Tempo: previewTempo,
            CursorTick: segment.ProjectStartTick));
    }

    public void StartHeldSegmentNotePreview(SegmentNotePreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        LogicalTrack track = _session.Project.Tracks.FirstOrDefault(
            value => value.Id == request.TrackId)
            ?? throw new ArgumentOutOfRangeException(nameof(request));
        Segment segment = track.Segments.FirstOrDefault(value => value.Id == request.SegmentId)
            ?? throw new ArgumentOutOfRangeException(nameof(request));
        long projectStartTick = checked(
            segment.ProjectStartTick + (request.StartTick - segment.ContentOffsetTick));
        decimal previewTempo = ResolvePreviewTempo(_session.Project, projectStartTick);
        StartHeldPreviewCore(
            windowLengthTicks => new PreviewCompiler().CompileHeldSegmentNoteGateOpen(
                _session.Project,
                request,
                windowLengthTicks),
            (finalGateLength, effectiveGateEndTick) =>
                new PreviewCompiler().CompileHeldSegmentNoteGateEnd(
                    _session.Project,
                    request,
                    finalGateLength,
                    effectiveGateEndTick),
            previewTempo,
            PlaybackTaskKind.EventInstrumentPreview,
            releaseEditLockAtGateEnd: true);
    }

    private void StartHeldPreviewCore(
        Func<long, CanonicalCompiledResult> compileOpen,
        Func<long, long, CanonicalCompiledResult> compileEnd,
        decimal previewTempo,
        PlaybackTaskKind taskKind,
        bool releaseEditLockAtGateEnd)
    {
        ArgumentNullException.ThrowIfNull(compileOpen);
        ArgumentNullException.ThrowIfNull(compileEnd);
        EnsureCanStartTask();
        if (_backend is not IHeldPreviewRealtimePlaybackBackend)
        {
            throw new NotSupportedException(
                "The selected realtime backend does not support causal held Preview.");
        }
        _ = RequireEffectiveSoundFont("Held Preview");

        try
        {
            _editLockLease = _session.AcquireProjectEditLock();
            ActiveTaskKind = taskKind;
            SetState(PlaybackState.Preparing);
            string soundFont = RequireEffectiveSoundFont("Held Preview");
            long initialWindowEndTick = CalculateHeldPreviewWindowTicks(
                _session.Project.TicksPerQuarterNote,
                previewTempo,
                HeldPreviewWindowSeconds);
            CanonicalCompiledResult compiled = compileOpen(initialWindowEndTick);
            RequireConsumablePreview(compiled);
            int actualSampleRate = PrepareBackend();
            _session.InvalidateSampleDomainCaches();
            MidiRenderPlan plan = MidiRenderPlanAdapter.CreateRealtime(
                compiled,
                actualSampleRate);
            PlaybackProjectSettings settings = _session.Project.Playback;
            ConfigureNextBufferingRecovery(compiled, plan);
            SetNextPlaybackCacheMode(RealtimePlaybackCacheMode.Disabled);
            _backend.Start(plan, soundFont, new(
                checked((float)settings.MasterVolumeDecibels),
                settings.LimiterEnabled));
            _activeResult = compiled;
            _activePlan = plan;
            _activeTempoMap = new(compiled.TicksPerQuarterNote, compiled.Tempos);
            _heldPreviewOpenCompiler = compileOpen;
            _heldPreviewEndCompiler = compileEnd;
            _heldPreviewTempo = previewTempo;
            _heldPreviewWindowEndTick = initialWindowEndTick;
            _heldPreviewGateOpen = true;
            _releaseEditLockAtHeldGateEnd = releaseEditLockAtGateEnd;
            LastHeldPreviewGateEndReport = null;
            LastError = null;
            SetState(_backend.IsBuffering ? PlaybackState.Buffering : PlaybackState.Playing);
        }
        catch (Exception exception)
        {
            LastError = exception;
            _activeResult = null;
            _activePlan = null;
            _activeTempoMap = null;
            ClearHeldPreviewState();
            ReleaseEditLock();
            ActiveTaskKind = PlaybackTaskKind.None;
            SetState(PlaybackState.Error);
            throw;
        }
    }

    public HeldPreviewGateEndReport EndHeldPreviewGate(long? finalGateLengthTicks = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_heldPreviewGateOpen
            || _heldPreviewEndCompiler is null
            || _activeResult is null
            || _activePlan is null
            || _activeTempoMap is null
            || _backend is not IHeldPreviewRealtimePlaybackBackend heldBackend
            || State is not PlaybackState.Playing and not PlaybackState.Buffering)
        {
            throw new InvalidOperationException("No held-preview Gate is active.");
        }
        if (finalGateLengthTicks.HasValue && finalGateLengthTicks.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(finalGateLengthTicks));
        }

        long consumedFrame = _backend.PositionFrames;
        try
        {
            long producerFrontier = heldBackend.PauseHeldPreviewAtProducerFrontier(
                HeldPreviewBackendTimeout);
            long frozenGateLength = finalGateLengthTicks
                ?? Math.Max(
                    1,
                    _activeTempoMap.SampleFrameToTick(
                        consumedFrame,
                        _activeResult.StartTick,
                        _backend.ActualSampleRate,
                        _activeResult.EndTick) - _activeResult.StartTick);
            long effectiveGateEndTick = Math.Max(
                1,
                FirstTickAtOrAfterFrame(
                    _activeTempoMap,
                    producerFrontier,
                    _activeResult.StartTick,
                    _backend.ActualSampleRate,
                    _activeResult.EndTick) - _activeResult.StartTick);
            CanonicalCompiledResult continuation = _heldPreviewEndCompiler(
                frozenGateLength,
                effectiveGateEndTick);
            RequireConsumablePreview(continuation);
            MidiRenderPlan continuationPlan = MidiRenderPlanAdapter.CreateRealtime(
                continuation,
                _backend.ActualSampleRate);
            MidiRenderPlan replacement = MidiRenderPlanSplicer
                .SpliceHeldGateEndAtProducerFrontier(
                    _activePlan,
                    continuationPlan,
                    producerFrontier);
            heldBackend.ReplaceHeldPreviewFutureAndResume(
                replacement,
                producerFrontier,
                HeldPreviewBackendTimeout);
            _activeResult = continuation;
            _activePlan = replacement;
            _activeTempoMap = new(
                continuation.TicksPerQuarterNote,
                continuation.Tempos);
            _heldPreviewGateOpen = false;
            _heldPreviewOpenCompiler = null;
            _heldPreviewEndCompiler = null;
            _heldPreviewTempo = 0;
            _heldPreviewWindowEndTick = 0;
            if (_releaseEditLockAtHeldGateEnd)
            {
                ReleaseEditLock();
            }
            _releaseEditLockAtHeldGateEnd = false;
            long latencyFrames = Math.Max(0, producerFrontier - consumedFrame);
            HeldPreviewGateEndReport report = new(
                frozenGateLength,
                consumedFrame,
                producerFrontier,
                latencyFrames,
                latencyFrames * 1_000d / _backend.ActualSampleRate);
            LastHeldPreviewGateEndReport = report;
            return report;
        }
        catch (Exception exception)
        {
            FailHeldPreview(exception);
            throw;
        }
    }

    public void CancelHeldPreview()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_heldPreviewGateOpen)
        {
            return;
        }
        StopCore(applyCursorBehavior: false, releaseEditLock: true);
    }

    public void StartSegmentPreview(MidoraId trackId, MidoraId segmentId)
    {
        EnsureCanStartTask();
        StartPreview(
            () => new PreviewCompiler().CompileSegment(_session.Project, trackId, segmentId),
            PlaybackTaskKind.SegmentPreview);
    }

    public void Stop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StopCore(applyCursorBehavior: true, releaseEditLock: true);
    }

    public void SelectOutputDevice(string? deviceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (deviceId is { Length: 0 })
        {
            throw new ArgumentException(
                "The output device ID must be null for System Default or non-empty.",
                nameof(deviceId));
        }
        if (State != PlaybackState.Stopped || ActiveTaskKind != PlaybackTaskKind.None)
        {
            throw new InvalidOperationException(
                "The playback output device can only be selected while playback is stopped.");
        }

        _backend.SelectOutputDevice(deviceId);
        Volatile.Write(ref _knownSampleRate, 0);
        _session.InvalidateSampleDomainCaches();
        OutputDeviceSelectionRequired = false;
        LastError = null;
        BeginDefaultPlaybackPreparation();
    }

    public void Seek(long tick)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (tick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tick));
        }
        if ((State is PlaybackState.Playing or PlaybackState.Buffering)
            && ActiveTaskKind == PlaybackTaskKind.MainTimeline)
        {
            long? effectiveEndTick = EffectiveEndTick(_requestedEndTick);
            if (effectiveEndTick < tick)
            {
                throw new ArgumentOutOfRangeException(nameof(tick),
                    "The seek target must not follow the effective playback or loop end.");
            }
            RestartAt(tick, effectiveEndTick);
        }
        else if ((State is PlaybackState.Stopped or PlaybackState.Error)
            && ActiveTaskKind == PlaybackTaskKind.None)
        {
            _cursorTick = tick;
        }
        else
        {
            throw new InvalidOperationException("Seek is unavailable in the current playback state.");
        }
    }

    public void SetLoop(TickRange? range)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (range.HasValue && (!range.Value.IsValid || range.Value.Length <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(range));
        }
        if (_loopRange == range)
        {
            return;
        }
        _loopRange = range;
        if (ActiveTaskKind != PlaybackTaskKind.MainTimeline
            || State is not PlaybackState.Playing and not PlaybackState.Buffering)
        {
            return;
        }

        long tick = CurrentTick;
        if (range.HasValue)
        {
            if (tick >= range.Value.EndTick)
            {
                tick = range.Value.StartTick;
            }
            RestartAt(tick, range.Value.EndTick);
            return;
        }

        if (_requestedEndTick.HasValue && tick >= _requestedEndTick.Value)
        {
            StopCore(applyCursorBehavior: true, releaseEditLock: true);
            return;
        }
        RestartAt(tick, _requestedEndTick);
    }

    public void Update()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (State is not PlaybackState.Playing and not PlaybackState.Buffering)
        {
            return;
        }
        if (_backend.OutputDeviceSelectionRequired)
        {
            EnterOutputDeviceSelectionRequired();
            return;
        }
        if (_backend.IsFaulted)
        {
            EnterBackendError();
            return;
        }
        if (_backend.IsBuffering)
        {
            if (!_bufferingRecoveryRequested && !TryBeginBufferingRecovery())
            {
                return;
            }
            SetState(PlaybackState.Buffering);
            return;
        }
        _bufferingRecoveryRequested = false;
        if (_heldPreviewGateOpen)
        {
            ExtendHeldPreviewWindowIfNeeded();
        }
        SetState(_backend.IsBuffering ? PlaybackState.Buffering : PlaybackState.Playing);
        if (!_backend.IsCompleted)
        {
            return;
        }
        if (_loopRange.HasValue && ActiveTaskKind == PlaybackTaskKind.MainTimeline)
        {
            RestartAt(_loopRange.Value.StartTick, _loopRange.Value.EndTick);
        }
        else
        {
            StopCore(applyCursorBehavior: true, releaseEditLock: true);
        }
    }

    public void SetTrackMuted(MidoraId trackId, bool muted)
    {
        EnsureTrackExists(trackId);
        bool changed = muted ? _mutedTracks.Add(trackId) : _mutedTracks.Remove(trackId);
        if (!changed) return;
        try
        {
            ApplyMonitoringChange();
        }
        catch
        {
            if (muted) _mutedTracks.Remove(trackId);
            else _mutedTracks.Add(trackId);
            RebuildAudibleTracks();
            throw;
        }
    }

    public void SetTrackSolo(MidoraId trackId, bool solo)
    {
        EnsureTrackExists(trackId);
        bool changed = solo ? _soloTracks.Add(trackId) : _soloTracks.Remove(trackId);
        if (!changed) return;
        try
        {
            ApplyMonitoringChange();
        }
        catch
        {
            if (solo) _soloTracks.Remove(trackId);
            else _soloTracks.Add(trackId);
            RebuildAudibleTracks();
            throw;
        }
    }

    public void SetTrackAudible(MidoraId trackId, bool audible) => SetTrackMuted(trackId, !audible);

    public void ResetPlaybackEngine()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Exception? stopFailure = null;
        if (State != PlaybackState.Stopped)
        {
            try
            {
                StopCore(applyCursorBehavior: true, releaseEditLock: true);
            }
            catch (Exception exception)
            {
                stopFailure = exception;
            }
        }

        Exception? resetFailure = null;
        try
        {
            _backend.Reset();
        }
        catch (Exception exception)
        {
            resetFailure = exception;
        }
        finally
        {
            Volatile.Write(ref _knownSampleRate, 0);
            _session.InvalidateSampleDomainCaches();
            _activeResult = null;
            _activePlan = null;
            _activeTempoMap = null;
            ClearHeldPreviewState();
            ActiveTaskKind = PlaybackTaskKind.None;
            ReleaseEditLock();
        }

        if (resetFailure is null)
        {
            if (!OutputDeviceSelectionRequired)
            {
                LastError = null;
            }
            SetState(PlaybackState.Stopped);
            return;
        }

        LastError = stopFailure is null
            ? resetFailure
            : new AggregateException(
                "Playback stop and backend reset both failed.",
                stopFailure,
                resetFailure);
        SetState(PlaybackState.Error);
        throw LastError;
    }

    public void RecoverFromError()
    {
        if (State == PlaybackState.Error)
        {
            ResetPlaybackEngine();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _session.CompilationChanged -= HandleCompilationChangedForPrewarm;
        _session.EffectiveSoundFontChanged -= HandleEffectiveSoundFontChanged;
        _prewarmCancellation.Cancel();
        Task prewarmTask;
        lock (_prewarmSync)
        {
            prewarmTask = _prewarmTask;
        }
        try
        {
            prewarmTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        if (State != PlaybackState.Stopped)
        {
            try
            {
                StopCore(applyCursorBehavior: true, releaseEditLock: true);
            }
            catch
            {
                ReleaseEditLock();
            }
        }
        _backend.Dispose();
        _prewarmCancellation.Dispose();
        _disposed = true;
    }

    private void HandleEffectiveSoundFontChanged(object? sender, EventArgs e) =>
        RefreshBackendSoundFontIdentity();

    private void RefreshBackendSoundFontIdentity()
    {
        if (_backend is not IRealtimePlaybackSoundFontBackend soundFontBackend)
        {
            return;
        }
        string? path = _session.EffectiveSoundFontPath;
        string? sha256 = path is null ? null : _session.EffectiveSoundFontSha256;
        lock (_backendPreparationSync)
        {
            soundFontBackend.SetSoundFontIdentity(path, sha256);
            Volatile.Write(ref _knownSampleRate, 0);
        }
    }

    private void StartPreparedRange(long cursorTick, long? endTick, bool acquireEditLock)
    {
        _cursorTick = cursorTick;
        try
        {
            if (acquireEditLock)
            {
                _editLockLease = _session.AcquireProjectEditLock();
            }
            ActiveTaskKind = PlaybackTaskKind.MainTimeline;
            SetState(PlaybackState.Preparing);
            string soundFont = RequireEffectiveSoundFont("Playback");
            WaitForDefaultPlaybackPreparation();
            int actualSampleRate = PrepareBackend();
            CanonicalCompiledResult compiled = _session.CompileForPlayback(cursorTick, endTick);
            if (!compiled.IsConsumable)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine,
                    compiled.Diagnostics.Select(value => $"{value.Code}: {value.Message}")));
            }
            if (compiled.EndTick < compiled.StartTick)
            {
                throw new InvalidOperationException("Playback range must not be reversed.");
            }
            if (compiled.EndTick == compiled.StartTick)
            {
                _activeResult = null;
                _activePlan = null;
                _activeTempoMap = null;
                ClearHeldPreviewState();
                ReleaseEditLock();
                ActiveTaskKind = PlaybackTaskKind.None;
                LastError = null;
                SetState(PlaybackState.Stopped);
                return;
            }
            MidiRenderPlan plan = _session.GetOrCreateRealtimeRenderPlan(
                compiled,
                actualSampleRate,
                _audibleTracks);
            PlaybackProjectSettings settings = _session.Project.Playback;
            ConfigureNextBufferingRecovery(compiled, plan);
            SetNextPlaybackCacheMode(RealtimePlaybackCacheMode.UnitPcmAndPlaybackSpan);
            _backend.Start(plan, soundFont, new(
                checked((float)settings.MasterVolumeDecibels), settings.LimiterEnabled));
            _activeResult = compiled;
            _activePlan = plan;
            _activeTempoMap = new(compiled.TicksPerQuarterNote, compiled.Tempos);
            LastError = null;
            SetState(_backend.IsBuffering ? PlaybackState.Buffering : PlaybackState.Playing);
        }
        catch (Exception exception)
        {
            LastError = exception;
            _activeResult = null;
            _activePlan = null;
            _activeTempoMap = null;
            ClearHeldPreviewState();
            ReleaseEditLock();
            ActiveTaskKind = PlaybackTaskKind.None;
            SetState(PlaybackState.Error);
            throw;
        }
    }

    private void StartPreview(Func<CanonicalCompiledResult> compile, PlaybackTaskKind taskKind)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(compile);
        if (State != PlaybackState.Stopped || ActiveTaskKind != PlaybackTaskKind.None)
        {
            throw new InvalidOperationException("A playback or preview task is already active.");
        }
        _ = RequireEffectiveSoundFont("Preview");

        try
        {
            _editLockLease = _session.AcquireProjectEditLock();
            ActiveTaskKind = taskKind;
            SetState(PlaybackState.Preparing);
            string soundFont = RequireEffectiveSoundFont("Preview");
            CanonicalCompiledResult compiled = compile();
            if (!compiled.IsConsumable)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine,
                    compiled.Diagnostics.Select(value => $"{value.Code}: {value.Message}")));
            }
            int actualSampleRate = PrepareBackend();
            _session.InvalidateSampleDomainCaches();
            if (compiled.EndTick <= compiled.StartTick)
            {
                ReleaseEditLock();
                ActiveTaskKind = PlaybackTaskKind.None;
                SetState(PlaybackState.Stopped);
                return;
            }
            MidiRenderPlan plan = MidiRenderPlanAdapter.CreateRealtime(compiled, actualSampleRate);
            PlaybackProjectSettings settings = _session.Project.Playback;
            ConfigureNextBufferingRecovery(compiled, plan);
            SetNextPlaybackCacheMode(taskKind == PlaybackTaskKind.SegmentPreview
                ? RealtimePlaybackCacheMode.UnitPcm
                : RealtimePlaybackCacheMode.Disabled);
            _backend.Start(plan, soundFont, new(
                checked((float)settings.MasterVolumeDecibels), settings.LimiterEnabled));
            _activeResult = compiled;
            _activePlan = plan;
            _activeTempoMap = new(compiled.TicksPerQuarterNote, compiled.Tempos);
            LastError = null;
            SetState(_backend.IsBuffering ? PlaybackState.Buffering : PlaybackState.Playing);
        }
        catch (Exception exception)
        {
            LastError = exception;
            _activeResult = null;
            _activePlan = null;
            _activeTempoMap = null;
            ClearHeldPreviewState();
            ReleaseEditLock();
            ActiveTaskKind = PlaybackTaskKind.None;
            SetState(PlaybackState.Error);
            throw;
        }
    }

    private void ExtendHeldPreviewWindowIfNeeded()
    {
        if (!_heldPreviewGateOpen
            || _heldPreviewOpenCompiler is null
            || _activePlan is null
            || _backend is not IHeldPreviewRealtimePlaybackBackend heldBackend)
        {
            return;
        }
        long thresholdFrames = checked(
            (long)_backend.ActualSampleRate * HeldPreviewRenewalThresholdSeconds);
        if (_activePlan.TotalFrameCount - _backend.RenderPositionFrames > thresholdFrames)
        {
            return;
        }

        try
        {
            long producerFrontier = heldBackend.PauseHeldPreviewAtProducerFrontier(
                HeldPreviewBackendTimeout);
            long extensionTicks = CalculateHeldPreviewWindowTicks(
                _session.Project.TicksPerQuarterNote,
                _heldPreviewTempo,
                HeldPreviewWindowSeconds);
            long replacementWindowEndTick = _heldPreviewWindowEndTick >= long.MaxValue - extensionTicks
                ? long.MaxValue
                : _heldPreviewWindowEndTick + extensionTicks;
            if (replacementWindowEndTick == _heldPreviewWindowEndTick)
            {
                heldBackend.ResumeHeldPreviewFromProducerFrontier();
                return;
            }
            CanonicalCompiledResult expanded = _heldPreviewOpenCompiler(
                replacementWindowEndTick);
            RequireConsumablePreview(expanded);
            MidiRenderPlan expandedPlan = MidiRenderPlanAdapter.CreateRealtime(
                expanded,
                _backend.ActualSampleRate);
            MidiRenderPlan replacement = MidiRenderPlanSplicer.SpliceAtProducerFrontier(
                _activePlan,
                expandedPlan,
                producerFrontier);
            heldBackend.ReplaceHeldPreviewFutureAndResume(
                replacement,
                producerFrontier,
                HeldPreviewBackendTimeout);
            _activeResult = expanded;
            _activePlan = replacement;
            _activeTempoMap = new(expanded.TicksPerQuarterNote, expanded.Tempos);
            _heldPreviewWindowEndTick = replacementWindowEndTick;
        }
        catch (Exception exception)
        {
            FailHeldPreview(exception);
            throw;
        }
    }

    private static long CalculateHeldPreviewWindowTicks(
        int ticksPerQuarterNote,
        decimal tempo,
        int seconds)
    {
        if (tempo <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tempo), "Preview Tempo must be positive.");
        }
        try
        {
            decimal ticks = decimal.Ceiling(
                checked(tempo * ticksPerQuarterNote * seconds / 60m));
            return ticks >= long.MaxValue ? long.MaxValue : Math.Max(1, checked((long)ticks));
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    private static decimal ResolvePreviewTempo(
        MidoraProject project,
        EventInstrumentPreviewRequest request) => request.Tempo
        ?? ResolvePreviewTempo(project, request.CursorTick);

    private static decimal ResolvePreviewTempo(
        MidoraProject project,
        long tick) => project.Conductor.Tempos
            .Where(value => value.Tick <= tick)
            .OrderBy(value => value.Tick)
            .LastOrDefault()?.BeatsPerMinute
        ?? project.Conductor.Tempos
            .OrderBy(value => value.Tick)
            .FirstOrDefault()?.BeatsPerMinute
        ?? 120m;

    private static long FirstTickAtOrAfterFrame(
        TempoSampleMap map,
        long frame,
        long originTick,
        int sampleRate,
        long maximumTick)
    {
        long tick = map.SampleFrameToTick(frame, originTick, sampleRate, maximumTick);
        if (tick < maximumTick
            && map.TickToSampleFrame(tick, originTick, sampleRate) < frame)
        {
            tick++;
        }
        return tick;
    }

    private static void RequireConsumablePreview(CanonicalCompiledResult compiled)
    {
        if (!compiled.IsConsumable)
        {
            throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                compiled.Diagnostics.Select(value => $"{value.Code}: {value.Message}")));
        }
    }

    private void FailHeldPreview(Exception failure)
    {
        Exception? cleanupFailure = null;
        try
        {
            _backend.Stop(flush: true);
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }
        _session.InvalidateSampleDomainCaches();
        _activeResult = null;
        _activePlan = null;
        _activeTempoMap = null;
        ClearHeldPreviewState();
        ActiveTaskKind = PlaybackTaskKind.None;
        ReleaseEditLock();
        LastError = cleanupFailure is null
            ? failure
            : new AggregateException(
                "Held Preview failed and backend cleanup also failed.",
                failure,
                cleanupFailure);
        SetState(PlaybackState.Error);
    }

    private void ClearHeldPreviewState()
    {
        _heldPreviewOpenCompiler = null;
        _heldPreviewEndCompiler = null;
        _heldPreviewTempo = 0;
        _heldPreviewWindowEndTick = 0;
        _heldPreviewGateOpen = false;
        _releaseEditLockAtHeldGateEnd = false;
    }

    private void RestartAt(long tick, long? endTick)
    {
        long stoppedTick = CurrentTaskTick;
        bool backendStopped = false;
        SetState(PlaybackState.Stopping);
        try
        {
            _backend.Stop(flush: true);
            backendStopped = true;
            _activeResult = null;
            _activePlan = null;
            _activeTempoMap = null;
            ClearHeldPreviewState();
            StartPreparedRange(tick, endTick, acquireEditLock: false);
        }
        catch (Exception exception)
        {
            LastError = exception;
            _activeResult = null;
            _activePlan = null;
            _activeTempoMap = null;
            ClearHeldPreviewState();
            _cursorTick = backendStopped ? tick : stoppedTick;
            ActiveTaskKind = PlaybackTaskKind.None;
            ReleaseEditLock();
            SetState(PlaybackState.Error);
            throw;
        }
    }

    private void StopCore(bool applyCursorBehavior, bool releaseEditLock)
    {
        if (State == PlaybackState.Stopped) return;
        PlaybackTaskKind stoppedTask = ActiveTaskKind;
        long stoppedTick = CurrentTaskTick;
        SetState(PlaybackState.Stopping);
        try
        {
            _backend.Stop(flush: true);
            _activeResult = null;
            _activePlan = null;
            _activeTempoMap = null;
            ClearHeldPreviewState();
            if (stoppedTask == PlaybackTaskKind.MainTimeline)
            {
                _cursorTick = applyCursorBehavior
                    && _session.Project.Playback.StopCursorBehavior == StopCursorBehavior.ReturnToPlaybackStart
                    ? _taskStartTick
                    : stoppedTick;
            }
            LastError = null;
            ActiveTaskKind = PlaybackTaskKind.None;
            SetState(PlaybackState.Stopped);
        }
        catch (Exception exception)
        {
            LastError = exception;
            _activeResult = null;
            _activePlan = null;
            _activeTempoMap = null;
            ClearHeldPreviewState();
            if (stoppedTask == PlaybackTaskKind.MainTimeline)
            {
                _cursorTick = stoppedTick;
            }
            ActiveTaskKind = PlaybackTaskKind.None;
            SetState(PlaybackState.Error);
            throw;
        }
        finally
        {
            if (releaseEditLock) ReleaseEditLock();
        }
    }

    private long? EffectiveEndTick(long? requested) => _loopRange?.EndTick ?? requested;

    private void SetNextPlaybackCacheMode(RealtimePlaybackCacheMode mode)
    {
        if (_backend is IRealtimePlaybackCacheBackend cacheBackend)
        {
            cacheBackend.SetNextPlaybackCacheMode(mode);
        }
    }

    private void ConfigureNextBufferingRecovery(
        CanonicalCompiledResult compiled,
        MidiRenderPlan plan)
    {
        _bufferingRecoveryRequested = false;
        _recoveryStorageFailure = null;
        if (_backend is not IBufferingRecoveryRealtimePlaybackBackend recoveryBackend)
        {
            return;
        }

        long maximumFrames = CalculateMaximumRecoveryFrameCount(compiled, plan);
        try
        {
            AudioCacheSessionStore.AudioRecoverySpool spool =
                _session.CreateBufferingRecoverySpool(checked(
                    maximumFrames * 2L * sizeof(float)));
            recoveryBackend.SetNextBufferingRecoveryStorage(spool, maximumFrames);
        }
        catch (AudioRecoveryStorageUnavailableException exception)
        {
            _recoveryStorageFailure = exception;
            recoveryBackend.SetNextBufferingRecoveryStorage(null, maximumFrames);
        }
    }

    private static long CalculateMaximumRecoveryFrameCount(
        CanonicalCompiledResult compiled,
        MidiRenderPlan plan)
    {
        decimal minimumTempo = compiled.Tempos.ToArray()
            .Select(value => value.BeatsPerMinute)
            .DefaultIfEmpty(120m)
            .Min();
        decimal upperBound = decimal.Ceiling(
            BufferingRecoveryPlanner.MaximumQuarterNoteCount
            * 60m
            * plan.SampleRate
            / minimumTempo) + 2m;
        long frames = upperBound >= long.MaxValue
            ? long.MaxValue
            : decimal.ToInt64(upperBound);
        return Math.Max(1, Math.Min(frames, plan.TotalFrameCount));
    }

    private bool TryBeginBufferingRecovery()
    {
        CanonicalCompiledResult compiled = _activeResult
            ?? throw new InvalidOperationException(
                "The active canonical playback result is unavailable during Buffering.");
        TempoSampleMap map = _activeTempoMap
            ?? throw new InvalidOperationException(
                "The active Tempo map is unavailable during Buffering.");
        if (_backend is not IBufferingRecoveryRealtimePlaybackBackend recoveryBackend)
        {
            EnterBufferingRecoveryError(new NotSupportedException(
                "The selected realtime backend cannot prepare a complete natural recovery interval."));
            return false;
        }
        if (!recoveryBackend.HasBufferingRecoveryStorage)
        {
            EnterBufferingRecoveryError(_recoveryStorageFailure
                ?? new AudioRecoveryStorageUnavailableException(
                    "The complete Buffering recovery interval has no reserved storage.",
                    new IOException("No recovery spool is active.")));
            return false;
        }

        try
        {
            long failureFrame = _backend.PositionFrames;
            long failureTick = map.SampleFrameToTick(
                failureFrame,
                compiled.StartTick,
                _backend.ActualSampleRate,
                compiled.EndTick);
            if (failureTick >= compiled.EndTick)
            {
                return true;
            }
            BufferingRecoveryInterval interval = BufferingRecoveryPlanner.Plan(
                compiled,
                failureTick,
                compiled.EndTick);
            long recoveryEndFrame = map.TickToSampleFrame(
                interval.EndTick,
                compiled.StartTick,
                _backend.ActualSampleRate);
            if (recoveryEndFrame <= failureFrame)
            {
                throw new InvalidDataException(
                    "The natural Buffering recovery interval did not advance a sample frame.");
            }
            recoveryBackend.BeginBufferingRecovery(recoveryEndFrame);
            _bufferingRecoveryRequested = true;
            return true;
        }
        catch (Exception exception)
        {
            EnterBufferingRecoveryError(exception);
            return false;
        }
    }

    private void EnterBufferingRecoveryError(Exception failure)
    {
        long failedTick = CurrentTaskTick;
        PlaybackTaskKind failedTask = ActiveTaskKind;
        Exception? cleanupError = null;
        try
        {
            _backend.Stop(flush: true);
        }
        catch (Exception exception)
        {
            cleanupError = exception;
        }
        _session.InvalidateSampleDomainCaches();
        _activeResult = null;
        _activePlan = null;
        _activeTempoMap = null;
        ClearHeldPreviewState();
        if (failedTask == PlaybackTaskKind.MainTimeline)
        {
            _cursorTick = failedTick;
        }
        ActiveTaskKind = PlaybackTaskKind.None;
        ReleaseEditLock();
        _bufferingRecoveryRequested = false;
        LastError = cleanupError is null
            ? failure
            : new AggregateException(
                "Buffering recovery failed and backend cleanup also failed.",
                failure,
                cleanupError);
        SetState(PlaybackState.Error);
    }

    private string RequireEffectiveSoundFont(string operation)
    {
        string soundFont = _session.EffectiveSoundFontPath
            ?? throw new InvalidOperationException(
                $"{operation} requires an effective Project SoundFont.");
        if (!File.Exists(soundFont))
        {
            throw new FileNotFoundException(
                "The effective Project SoundFont does not exist.",
                soundFont);
        }
        return soundFont;
    }

    private void ApplyMonitoringChange()
    {
        bool active = ActiveTaskKind == PlaybackTaskKind.MainTimeline
            && (State is PlaybackState.Playing or PlaybackState.Buffering);
        HashSet<MidoraId> previouslyAudible = new(_audibleTracks);
        RebuildAudibleTracks();
        if (!active)
        {
            return;
        }

        CanonicalCompiledResult compiled = _activeResult
            ?? throw new InvalidOperationException("The active canonical playback result is unavailable.");
        MidiRenderPlan plan = _activePlan
            ?? throw new InvalidOperationException("The active sample-domain playback plan is unavailable.");
        long renderTick = _activeTempoMap!.SampleFrameToTick(
            _backend.PositionFrames,
            compiled.StartTick,
            _backend.ActualSampleRate,
            compiled.EndTick);
        MidoraId[] newlyDisabled = previouslyAudible.Except(_audibleTracks).OrderBy(value => value).ToArray();
        MidoraId[] newlyEnabled = _audibleTracks.Except(previouslyAudible).OrderBy(value => value).ToArray();
        if (newlyDisabled.Length == 0 && newlyEnabled.Length == 0)
        {
            return;
        }

        CanonicalCompiledResult? restoreResult = newlyEnabled.Length == 0
            ? null
            : _session.CompileForPlayback(renderTick, EffectiveEndTick(_requestedEndTick));
        if (restoreResult is not null && !restoreResult.IsConsumable)
        {
            throw new InvalidOperationException("A monitoring cold-start state could not be compiled.");
        }
        Dictionary<(MidoraId TrackId, MidoraId InstanceId, MidoraId SubVoiceId), ChannelUnitAllocation>
            activeRouting = compiled.Allocations.ToArray()
                .Where(value => value.StartTick <= renderTick && value.EndTick > renderTick)
                .ToDictionary(
                    value => (value.TrackId, value.InstanceId, value.SubVoiceId),
                    value => value);

        List<MidiMonitoringCommand> commands = [];
        foreach (MidoraId trackId in newlyDisabled)
        {
            int sourceIndex = plan.FindSourceIndex(trackId.Value);
            if (sourceIndex < 0) continue;
            commands.Add(MidiMonitoringCommand.DisableSource(sourceIndex));
            AppendTrackCleanup(commands, compiled, trackId, renderTick);
        }
        foreach (MidoraId trackId in newlyEnabled)
        {
            int sourceIndex = plan.FindSourceIndex(trackId.Value);
            if (sourceIndex < 0) continue;
            commands.Add(MidiMonitoringCommand.EnableSource(sourceIndex));
            foreach (CanonicalMidiEvent value in restoreResult!.Events)
            {
                if (value.Tick == restoreResult.StartTick
                    && value.Role == CanonicalEventRole.RangeRestore
                    && value.Source.TrackId == trackId)
                {
                    var routingKey = (
                        value.Source.TrackId,
                        value.Source.LogicalNoteId,
                        value.Source.SubVoiceId);
                    if (!activeRouting.TryGetValue(routingKey, out ChannelUnitAllocation activeAllocation))
                    {
                        throw new InvalidOperationException(
                            "A monitoring restore event could not be routed to its active canonical Channel Unit.");
                    }
                    commands.Add(MidiMonitoringCommand.Send(
                        activeAllocation.ZeroBasedPort,
                        WithChannel(value.Message, activeAllocation.ZeroBasedChannel)));
                }
            }
        }
        if (commands.Count != 0)
        {
            _backend.ApplyMonitoringCommands(CollectionsMarshal.AsSpan(commands));
        }
    }

    private static MidiMessage WithChannel(MidiMessage message, byte channel)
    {
        if (!message.IsChannelVoiceMessage)
        {
            throw new InvalidOperationException(
                "Monitoring state restore accepts only canonical MIDI channel messages.");
        }
        uint packed = message.PackedValue & ~MidiMessage.ChannelNumberMask | channel;
        return MidiMessage.FromPackedValue(packed);
    }

    private static void AppendTrackCleanup(
        List<MidiMonitoringCommand> commands,
        CanonicalCompiledResult compiled,
        MidoraId trackId,
        long tick)
    {
        HashSet<(byte Port, byte Channel)> channels = compiled.Allocations.ToArray()
            .Where(value => value.TrackId == trackId && value.StartTick <= tick && value.EndTick > tick)
            .Select(value => (value.ZeroBasedPort, value.ZeroBasedChannel))
            .ToHashSet();
        foreach ((byte port, byte channel) in channels.OrderBy(value => value.Port).ThenBy(value => value.Channel))
        {
            commands.Add(MidiMonitoringCommand.Send(port, MidiMessage.ControlChange(channel, 123, 0)));
            commands.Add(MidiMonitoringCommand.Send(port, MidiMessage.ControlChange(channel, 120, 0)));
            commands.Add(MidiMonitoringCommand.Send(port, MidiMessage.ControlChange(channel, 121, 0)));

            ChannelUnitAllocation allocation = compiled.Allocations.ToArray()
                .Where(value => value.TrackId == trackId
                    && value.ZeroBasedPort == port
                    && value.ZeroBasedChannel == channel
                    && value.StartTick <= tick
                    && value.EndTick > tick)
                .OrderBy(value => value.EndTick)
                .First();
            long cleanupTick = Math.Min(allocation.EndTick, compiled.EndTick);
            foreach (CanonicalMidiEvent reset in compiled.Events.ToArray()
                .Where(value => value.Tick == cleanupTick
                    && value.ZeroBasedPort == port
                    && value.ZeroBasedChannel == channel
                    && value.Role == CanonicalEventRole.Reset)
                .OrderBy(value => value.StableOrder))
            {
                commands.Add(MidiMonitoringCommand.Send(port, reset.Message));
            }
        }
    }

    private void RebuildAudibleTracks()
    {
        _audibleTracks.Clear();
        bool hasSolo = _soloTracks.Count != 0;
        foreach (LogicalTrack track in _session.Project.Tracks)
        {
            if (!_mutedTracks.Contains(track.Id) && (!hasSolo || _soloTracks.Contains(track.Id)))
            {
                _audibleTracks.Add(track.Id);
            }
        }
    }

    private void EnsureTrackExists(MidoraId trackId)
    {
        if (!_session.Project.Tracks.Any(value => value.Id == trackId))
        {
            throw new ArgumentOutOfRangeException(nameof(trackId));
        }
    }

    private void EnsureCanStartTask()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (OutputDeviceSelectionRequired)
        {
            throw new OutputDeviceSelectionRequiredException(
                "The active output device became unavailable. Select an output device explicitly before starting playback or preview again.");
        }
        if (State == PlaybackState.Error)
        {
            ResetPlaybackEngine();
        }
        if (State != PlaybackState.Stopped || ActiveTaskKind != PlaybackTaskKind.None)
        {
            throw new InvalidOperationException("A playback or preview task is already active.");
        }
    }

    private void EnterBackendError()
    {
        long failedTick = CurrentTaskTick;
        PlaybackTaskKind failedTask = ActiveTaskKind;
        string description = _backend.FaultDescription ?? "The realtime playback backend reported an unrecoverable fault.";
        Exception? cleanupError = null;
        try
        {
            _backend.Stop(flush: true);
        }
        catch (Exception exception)
        {
            cleanupError = exception;
        }
        _session.InvalidateSampleDomainCaches();
        _activeResult = null;
        _activePlan = null;
        _activeTempoMap = null;
        ClearHeldPreviewState();
        if (failedTask == PlaybackTaskKind.MainTimeline)
        {
            _cursorTick = failedTick;
        }
        ActiveTaskKind = PlaybackTaskKind.None;
        ReleaseEditLock();
        LastError = cleanupError is null
            ? new InvalidOperationException(description)
            : new AggregateException(description, cleanupError);
        SetState(PlaybackState.Error);
    }

    private void HandleCompilationChangedForPrewarm(object? sender, EventArgs eventArgs)
    {
        if (_disposed
            || State != PlaybackState.Stopped
            || !_session.IsCompilationCurrent
            || _session.CompilationState != ProjectCompilationState.Succeeded)
        {
            return;
        }
        BeginDefaultPlaybackPreparation();
    }

    private void RunDefaultPlaybackPreparationLoop()
    {
        CancellationToken cancellationToken = _prewarmCancellation.Token;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long requestGeneration;
            lock (_prewarmSync)
            {
                requestGeneration = _prewarmRequestGeneration;
            }
            try
            {
                int sampleRate = Volatile.Read(ref _knownSampleRate);
                if (sampleRate == 0)
                {
                    sampleRate = PrepareBackend(cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                _session.PrewarmDefaultRealtimeRenderPlan(sampleRate);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Best effort only. Formal preparation and diagnostics still run
                // synchronously when playback is explicitly requested.
            }
            lock (_prewarmSync)
            {
                if (requestGeneration == _prewarmRequestGeneration)
                {
                    return;
                }
            }
        }
    }

    private void WaitForDefaultPlaybackPreparation()
    {
        Task task;
        lock (_prewarmSync)
        {
            task = _prewarmTask;
        }
        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (_prewarmCancellation.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(PlaybackController));
        }
    }

    private int PrepareBackend()
        => PrepareBackend(CancellationToken.None);

    private int PrepareBackend(CancellationToken cancellationToken)
    {
        lock (_backendPreparationSync)
        {
            int sampleRate = _backend is ICancellableRealtimePlaybackPreparationBackend cancellable
                ? cancellable.Prepare(cancellationToken)
                : _backend.Prepare();
            Volatile.Write(ref _knownSampleRate, sampleRate);
            return sampleRate;
        }
    }

    private void EnterOutputDeviceSelectionRequired()
    {
        long failedTick = CurrentTaskTick;
        PlaybackTaskKind failedTask = ActiveTaskKind;
        string description = _backend.OutputDeviceSelectionReason
            ?? "The active output device became unavailable.";
        Exception? cleanupError = null;
        try
        {
            _backend.Stop(flush: false);
        }
        catch (Exception exception)
        {
            cleanupError = exception;
        }
        _session.InvalidateSampleDomainCaches();
        _activeResult = null;
        _activePlan = null;
        _activeTempoMap = null;
        ClearHeldPreviewState();
        if (failedTask == PlaybackTaskKind.MainTimeline)
        {
            _cursorTick = failedTick;
        }
        ActiveTaskKind = PlaybackTaskKind.None;
        ReleaseEditLock();
        OutputDeviceSelectionRequired = true;
        OutputDeviceSelectionRequiredException selectionRequired = new(
            $"{description} Select an output device explicitly before playback or preview can start again.");
        LastError = cleanupError is null
            ? selectionRequired
            : new AggregateException(selectionRequired.Message, selectionRequired, cleanupError);
        SetState(cleanupError is null ? PlaybackState.Stopped : PlaybackState.Error);
    }

    private void SetState(PlaybackState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReleaseEditLock()
    {
        _editLockLease?.Dispose();
        _editLockLease = null;
    }
}
