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
    int Prepare();
    void Start(MidiRenderPlan plan, string soundFontPath, PlaybackMasterConfiguration master);
    void ApplyMonitoringCommands(ReadOnlySpan<MidiMonitoringCommand> commands);
    void Stop(bool flush);
    void Reset();
}

public readonly record struct PlaybackMasterConfiguration(float VolumeDecibels, bool LimiterEnabled);

public sealed class PlaybackController : IDisposable
{
    private readonly ProjectCompilationSession _session;
    private readonly IRealtimePlaybackBackend _backend;
    private readonly HashSet<MidoraId> _mutedTracks = [];
    private readonly HashSet<MidoraId> _soloTracks = [];
    private readonly HashSet<MidoraId> _audibleTracks = [];
    private CanonicalCompiledResult? _activeResult;
    private MidiRenderPlan? _activePlan;
    private TempoSampleMap? _activeTempoMap;
    private long _taskStartTick;
    private long _cursorTick;
    private long? _requestedEndTick;
    private TickRange? _loopRange;
    private bool _disposed;

    public PlaybackController(ProjectCompilationSession session, IRealtimePlaybackBackend backend)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        RebuildAudibleTracks();
    }

    public PlaybackState State { get; private set; } = PlaybackState.Stopped;
    public PlaybackTaskKind ActiveTaskKind { get; private set; }
    public Exception? LastError { get; private set; }
    public TickRange? LoopRange => _loopRange;
    public event EventHandler? StateChanged;

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
        _taskStartTick = effectiveCursorTick;
        _cursorTick = effectiveCursorTick;
        _requestedEndTick = endTick;
        ActiveTaskKind = PlaybackTaskKind.MainTimeline;
        StartPreparedRange(effectiveCursorTick, EffectiveEndTick(endTick), acquireEditLock: true);
    }

    public void StartEventInstrumentPreview(EventInstrumentPreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureCanStartTask();
        CanonicalCompiledResult compiled = new PreviewCompiler().CompileEventInstrument(_session.Project, request);
        StartPreview(compiled, request.SubVoiceId.HasValue
            ? PlaybackTaskKind.SubVoicePreview
            : PlaybackTaskKind.EventInstrumentPreview);
    }

    public void StartSegmentPreview(MidoraId trackId, MidoraId segmentId)
    {
        EnsureCanStartTask();
        CanonicalCompiledResult compiled = new PreviewCompiler().CompileSegment(_session.Project, trackId, segmentId);
        StartPreview(compiled, PlaybackTaskKind.SegmentPreview);
    }

    public void Stop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StopCore(applyCursorBehavior: true, releaseEditLock: true);
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
            RestartAt(tick, EffectiveEndTick(_requestedEndTick));
        }
        else if (State == PlaybackState.Stopped)
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
        _loopRange = range;
        if (range.HasValue && ActiveTaskKind == PlaybackTaskKind.MainTimeline
            && (State is PlaybackState.Playing or PlaybackState.Buffering))
        {
            long tick = CurrentTick;
            if (tick >= range.Value.EndTick)
            {
                tick = range.Value.StartTick;
            }
            RestartAt(tick, range.Value.EndTick);
        }
    }

    public void Update()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (State is not PlaybackState.Playing and not PlaybackState.Buffering)
        {
            return;
        }
        SetState(_backend.IsBuffering ? PlaybackState.Buffering : PlaybackState.Playing);
        if (_backend.IsFaulted)
        {
            EnterBackendError();
            return;
        }
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
        if (State != PlaybackState.Stopped)
        {
            StopCore(applyCursorBehavior: true, releaseEditLock: true);
        }
        _backend.Reset();
        _session.InvalidateSampleDomainCaches();
        LastError = null;
        ActiveTaskKind = PlaybackTaskKind.None;
        SetState(PlaybackState.Stopped);
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
        if (State != PlaybackState.Stopped)
        {
            try
            {
                StopCore(applyCursorBehavior: true, releaseEditLock: true);
            }
            catch
            {
                _session.SetEditsLocked(false);
            }
        }
        _backend.Dispose();
        _disposed = true;
    }

    private void StartPreparedRange(long cursorTick, long? endTick, bool acquireEditLock)
    {
        string soundFont = _session.EffectiveSoundFontPath
            ?? throw new InvalidOperationException("Playback requires an effective Project SoundFont.");
        if (!File.Exists(soundFont))
        {
            throw new FileNotFoundException("The effective Project SoundFont does not exist.", soundFont);
        }
        _cursorTick = cursorTick;
        SetState(PlaybackState.Preparing);
        if (acquireEditLock) _session.SetEditsLocked(true);
        try
        {
            int actualSampleRate = _backend.Prepare();
            _session.InvalidateSampleDomainCaches();
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
                _session.SetEditsLocked(false);
                ActiveTaskKind = PlaybackTaskKind.None;
                LastError = null;
                SetState(PlaybackState.Stopped);
                return;
            }
            MidiRenderPlan plan = MidiRenderPlanAdapter.CreateRealtime(compiled, actualSampleRate, _audibleTracks);
            PlaybackProjectSettings settings = _session.Project.Playback;
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
            _session.SetEditsLocked(false);
            ActiveTaskKind = PlaybackTaskKind.None;
            SetState(PlaybackState.Error);
            throw;
        }
    }

    private void StartPreview(CanonicalCompiledResult compiled, PlaybackTaskKind taskKind)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(compiled);
        if (State != PlaybackState.Stopped || ActiveTaskKind != PlaybackTaskKind.None)
        {
            throw new InvalidOperationException("A playback or preview task is already active.");
        }
        string soundFont = _session.EffectiveSoundFontPath
            ?? throw new InvalidOperationException("Preview requires an effective Project SoundFont.");
        if (!File.Exists(soundFont))
        {
            throw new FileNotFoundException("The effective Project SoundFont does not exist.", soundFont);
        }

        ActiveTaskKind = taskKind;
        SetState(PlaybackState.Preparing);
        _session.SetEditsLocked(true);
        try
        {
            int actualSampleRate = _backend.Prepare();
            _session.InvalidateSampleDomainCaches();
            if (!compiled.IsConsumable)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine,
                    compiled.Diagnostics.Select(value => $"{value.Code}: {value.Message}")));
            }
            if (compiled.EndTick <= compiled.StartTick)
            {
                _session.SetEditsLocked(false);
                ActiveTaskKind = PlaybackTaskKind.None;
                SetState(PlaybackState.Stopped);
                return;
            }
            MidiRenderPlan plan = MidiRenderPlanAdapter.CreateRealtime(compiled, actualSampleRate);
            PlaybackProjectSettings settings = _session.Project.Playback;
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
            _session.SetEditsLocked(false);
            ActiveTaskKind = PlaybackTaskKind.None;
            SetState(PlaybackState.Error);
            throw;
        }
    }

    private void RestartAt(long tick, long? endTick)
    {
        SetState(PlaybackState.Stopping);
        try
        {
            _backend.Stop(flush: true);
            _activeResult = null;
            _activePlan = null;
            _activeTempoMap = null;
            StartPreparedRange(tick, endTick, acquireEditLock: false);
        }
        catch
        {
            _session.SetEditsLocked(false);
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
            SetState(PlaybackState.Error);
            throw;
        }
        finally
        {
            if (releaseEditLock) _session.SetEditsLocked(false);
        }
    }

    private long? EffectiveEndTick(long? requested) => _loopRange?.EndTick ?? requested;

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
            _backend.RenderPositionFrames,
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
                    commands.Add(MidiMonitoringCommand.Send(value.ZeroBasedPort, value.Message));
                }
            }
        }
        if (commands.Count != 0)
        {
            _backend.ApplyMonitoringCommands(CollectionsMarshal.AsSpan(commands));
        }
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
        if (State == PlaybackState.Error)
        {
            _backend.Reset();
            _session.InvalidateSampleDomainCaches();
            ActiveTaskKind = PlaybackTaskKind.None;
            SetState(PlaybackState.Stopped);
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
        _activeResult = null;
        _activeTempoMap = null;
        if (failedTask == PlaybackTaskKind.MainTimeline)
        {
            _cursorTick = failedTick;
        }
        ActiveTaskKind = PlaybackTaskKind.None;
        _session.SetEditsLocked(false);
        LastError = cleanupError is null
            ? new InvalidOperationException(description)
            : new AggregateException(description, cleanupError);
        SetState(PlaybackState.Error);
    }

    private void SetState(PlaybackState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
