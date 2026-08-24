using Midora.Audio;
using Midora.Compiler;
using Midora.Domain;

namespace Midora.Playback;

public enum ProjectCompilationExecutionMode
{
    Synchronous,
    Background
}

public enum ProjectCompilationState
{
    NotCompiled,
    Outdated,
    Compiling,
    Succeeded,
    Failed
}

public sealed class ProjectCompilationSession : IDisposable, IRealtimePlaybackCacheStore
{
    private readonly object _sync = new();
    private readonly object _projectGate = new();
    private readonly MidoraCompiler _compiler = new();
    private readonly ProjectEditingTimeSession _editingTime;
    private readonly ProjectCompilationExecutionMode _executionMode;
    private readonly TimeSpan _backgroundDebounce;
    private readonly SemaphoreSlim _compileSignal = new(0, 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly Dictionary<(long Fingerprint, int SampleRate), MidiRenderPlan> _samplePlans = [];
    private readonly Dictionary<
        (long Fingerprint, long StartTick, long EndTick, int SampleRate),
        MidiRenderPlan> _realtimePlans = [];
    private readonly Dictionary<(long StartTick, long? EndTick), CanonicalCompiledResult>
        _playbackRangeResults = [];
    private MidoraProject _compilationProject;
    private AudioCacheSessionStore? _audioCacheStore;
    private AudioCacheWarning _audioCacheWarning;
    private long _playbackRangeCacheHitCount;
    private long _playbackRangeCompilationCount;
    private Task? _compileWorker;
    private CancellationTokenSource? _activeCompilationCancellation;
    private TaskCompletionSource<bool> _compilationStateChanged = CreateStatePulse();
    private ProjectChangeSet _pendingChanges = new();
    private ProjectCompilationState _compilationState = ProjectCompilationState.NotCompiled;
    private Exception? _backgroundCompilationFailure;
    private long _sourceRevision;
    private long _compiledRevision = -1;
    private long _requestedCompilationGeneration;
    private long _publishedCompilationGeneration = -1;
    private long _sampleDomainGeneration;
    private bool _compileSignalPending;
    private bool _forceImmediateCompilation;
    private int _editLockCount;
    private bool _disposed;

    public ProjectCompilationSession(
        MidoraProject project,
        string? effectiveSoundFontPath = null,
        TimeProvider? editingTimeProvider = null,
        ProjectCompilationExecutionMode executionMode = ProjectCompilationExecutionMode.Synchronous,
        TimeSpan? backgroundDebounce = null)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        _executionMode = executionMode;
        _backgroundDebounce = backgroundDebounce ?? TimeSpan.FromMilliseconds(75);
        if (_backgroundDebounce < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(backgroundDebounce));
        }
        _editingTime = new ProjectEditingTimeSession(project, editingTimeProvider);
        EffectiveSoundFontPaths = effectiveSoundFontPath is null
            ? Array.Empty<string>()
            : [Path.GetFullPath(effectiveSoundFontPath)];
        EffectiveSoundFontSetCacheIdentity = effectiveSoundFontPath is null
            ? null
            : SoundFontSetDefinition.CreateConfiguration(EffectiveSoundFontPaths).CacheIdentity;
        try
        {
            _compilationProject = executionMode == ProjectCompilationExecutionMode.Background
                ? ProjectCompilationSnapshot.Create(project)
                : project;
            LastAttempt = _compiler.CompileFull(_compilationProject);
            _compiledRevision = _sourceRevision;
            _publishedCompilationGeneration = _requestedCompilationGeneration;
            _compilationState = LastAttempt.IsConsumable
                ? ProjectCompilationState.Succeeded
                : ProjectCompilationState.Failed;
            if (LastAttempt.IsConsumable)
            {
                LastSuccessfulResult = LastAttempt;
            }
            if (_executionMode == ProjectCompilationExecutionMode.Background)
            {
                _compileWorker = Task.Run(CompilationWorkerAsync);
            }
        }
        catch
        {
            _editingTime.Dispose();
            _compiler.Dispose();
            throw;
        }
    }

    public MidoraProject Project { get; }
    internal ProjectEditingTimeSession EditingTimeSession => _editingTime;
    public IReadOnlyList<string> EffectiveSoundFontPaths { get; private set; }
    public string? EffectiveSoundFontPath => EffectiveSoundFontPaths.FirstOrDefault();
    public string? EffectiveSoundFontSetCacheIdentity { get; private set; }
    public CanonicalCompiledResult LastAttempt { get; private set; }
    public CanonicalCompiledResult? LastSuccessfulResult { get; private set; }
    public CompilerRunTelemetry LastCompilationTelemetry => _compiler.LastTelemetry;
    public ProjectCompilationExecutionMode ExecutionMode => _executionMode;
    public ProjectCompilationState CompilationState
    {
        get
        {
            lock (_sync)
            {
                return _compilationState;
            }
        }
    }
    public long SourceRevision
    {
        get
        {
            lock (_sync)
            {
                return _sourceRevision;
            }
        }
    }
    public long CompiledRevision
    {
        get
        {
            lock (_sync)
            {
                return _compiledRevision;
            }
        }
    }
    public bool IsCompilationCurrent
    {
        get
        {
            lock (_sync)
            {
                return IsCompilationCurrentCore();
            }
        }
    }
    public long PlaybackRangeCacheHitCount => Volatile.Read(ref _playbackRangeCacheHitCount);
    public long PlaybackRangeCompilationCount => Volatile.Read(ref _playbackRangeCompilationCount);
    public bool EditsLocked => Volatile.Read(ref _editLockCount) != 0;
    public AudioCacheWarning AudioCacheWarning
    {
        get
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _audioCacheWarning;
            }
        }
    }
    public AudioCacheSessionSnapshot? AudioCacheSnapshot
    {
        get
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _audioCacheStore?.GetSnapshot();
            }
        }
    }
    public event EventHandler? CompilationChanged;
    public event EventHandler? EffectiveSoundFontChanged;

    internal CanonicalCompiledResult ApplyEdit(Action<MidoraProject> edit, ProjectChangeSet changes)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(changes);
        if (_executionMode == ProjectCompilationExecutionMode.Background)
        {
            CanonicalCompiledResult result = ApplyBackgroundEdit(edit, null, changes);
            CompilationChanged?.Invoke(this, EventArgs.Empty);
            return result;
        }
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_editLockCount != 0)
            {
                throw new InvalidOperationException(
                    "Project edits are forbidden while a Project edit lock is active.");
            }
            edit(Project);
            LastAttempt = _compiler.CompileIncremental(Project, changes);
            RecordSynchronousCompilationLocked(changes, sourceChanged: AffectsCompilation(changes));
            ClearSampleDomainCachesCore();
            _playbackRangeResults.Clear();
            if (LastAttempt.IsConsumable)
            {
                LastSuccessfulResult = LastAttempt;
            }
        }
        if (changes.AffectsAudioPcmCacheGeneration)
        {
            _ = ResetAudioCacheGenerations();
        }
        CompilationChanged?.Invoke(this, EventArgs.Empty);
        return LastAttempt;
    }

    internal CanonicalCompiledResult ApplyReversibleEdit(
        Action<MidoraProject> edit,
        Action<MidoraProject> rollback,
        ProjectChangeSet changes)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(rollback);
        ArgumentNullException.ThrowIfNull(changes);
        if (_executionMode == ProjectCompilationExecutionMode.Background)
        {
            return ApplyBackgroundEdit(edit, rollback, changes);
        }
        CanonicalCompiledResult result;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_editLockCount != 0)
            {
                throw new InvalidOperationException(
                    "Project edits are forbidden while a Project edit lock is active.");
            }

            CanonicalCompiledResult? previousSuccessful = LastSuccessfulResult;
            try
            {
                edit(Project);
                LastAttempt = _compiler.CompileIncremental(Project, changes);
                RecordSynchronousCompilationLocked(changes, sourceChanged: AffectsCompilation(changes));
                ClearSampleDomainCachesCore();
                _playbackRangeResults.Clear();
                if (LastAttempt.IsConsumable)
                {
                    LastSuccessfulResult = LastAttempt;
                }
                result = LastAttempt;
            }
            catch (Exception editError)
            {
                try
                {
                    rollback(Project);
                    LastAttempt = _compiler.CompileFull(Project);
                    RecordSynchronousCompilationLocked(
                        ProjectChangeSet.Everything,
                        sourceChanged: false);
                    ClearSampleDomainCachesCore();
                    _playbackRangeResults.Clear();
                    LastSuccessfulResult = LastAttempt.IsConsumable
                        ? LastAttempt
                        : previousSuccessful;
                }
                catch (Exception rollbackError)
                {
                    throw new AggregateException(
                        "The Project edit failed and its rollback could not restore a verified compiler state.",
                        editError,
                        rollbackError);
                }
                throw;
            }
        }
        if (changes.AffectsAudioPcmCacheGeneration)
        {
            _ = ResetAudioCacheGenerations();
        }
        return result;
    }

    internal void NotifyCompilationChanged() =>
        CompilationChanged?.Invoke(this, EventArgs.Empty);

    public CanonicalCompiledResult Recompile(ProjectChangeSet changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (_executionMode == ProjectCompilationExecutionMode.Background)
        {
            return RecompileAsync(changes).GetAwaiter().GetResult();
        }
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_editLockCount != 0)
            {
                throw new InvalidOperationException(
                    "Compilation after editing is forbidden while a Project edit lock is active.");
            }
            LastAttempt = _compiler.CompileIncremental(Project, changes);
            RecordSynchronousCompilationLocked(changes, sourceChanged: false);
            ClearSampleDomainCachesCore();
            _playbackRangeResults.Clear();
            if (LastAttempt.IsConsumable)
            {
                LastSuccessfulResult = LastAttempt;
            }
        }
        if (changes.AffectsAudioPcmCacheGeneration)
        {
            _ = ResetAudioCacheGenerations();
        }
        CompilationChanged?.Invoke(this, EventArgs.Empty);
        return LastAttempt;
    }

    public async Task<CanonicalCompiledResult> RecompileAsync(
        ProjectChangeSet changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (_executionMode == ProjectCompilationExecutionMode.Synchronous)
        {
            return await Task.Run(() => Recompile(changes), cancellationToken).ConfigureAwait(false);
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_editLockCount != 0)
            {
                throw new InvalidOperationException(
                    "Compilation after editing is forbidden while a Project edit lock is active.");
            }
            _pendingChanges = MergeChanges(_pendingChanges, changes);
            _requestedCompilationGeneration = checked(_requestedCompilationGeneration + 1);
            _activeCompilationCancellation?.Cancel();
            ClearSampleDomainCachesCore();
            _playbackRangeResults.Clear();
            _backgroundCompilationFailure = null;
            SetCompilationStateLocked(ProjectCompilationState.Outdated);
            ScheduleCompilationLocked(immediate: true);
        }
        if (changes.AffectsAudioPcmCacheGeneration)
        {
            _ = ResetAudioCacheGenerations();
        }
        CompilationChanged?.Invoke(this, EventArgs.Empty);
        return await EnsureCurrentCompilationAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CanonicalCompiledResult> EnsureCurrentCompilationAsync(
        CancellationToken cancellationToken = default)
    {
        if (_executionMode == ProjectCompilationExecutionMode.Synchronous)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return LastAttempt;
            }
        }

        while (true)
        {
            Task stateChanged;
            Exception? failure;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (IsCompilationAttemptCurrentCore())
                {
                    failure = _backgroundCompilationFailure;
                    if (failure is null)
                    {
                        return LastAttempt;
                    }
                }
                else
                {
                    failure = null;
                    ScheduleCompilationLocked(immediate: true);
                }
                stateChanged = _compilationStateChanged.Task;
            }

            if (failure is not null)
            {
                throw new InvalidOperationException(
                    "The current Project revision could not be compiled.",
                    failure);
            }
            await stateChanged.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private CanonicalCompiledResult ApplyBackgroundEdit(
        Action<MidoraProject> edit,
        Action<MidoraProject>? rollback,
        ProjectChangeSet changes)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_editLockCount != 0)
            {
                throw new InvalidOperationException(
                    "Project edits are forbidden while a Project edit lock is active.");
            }
            _activeCompilationCancellation?.Cancel();
        }

        lock (_projectGate)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_editLockCount != 0)
                {
                    throw new InvalidOperationException(
                        "Project edits are forbidden while a Project edit lock is active.");
                }
            }

            try
            {
                edit(Project);
            }
            catch (Exception editError) when (rollback is not null)
            {
                try
                {
                    rollback(Project);
                }
                catch (Exception rollbackError)
                {
                    throw new AggregateException(
                        "The Project edit failed and its rollback could not restore the source model.",
                        editError,
                        rollbackError);
                }
                throw;
            }

            lock (_sync)
            {
                bool affectsCompilation = AffectsCompilation(changes);
                if (affectsCompilation)
                {
                    _sourceRevision = checked(_sourceRevision + 1);
                    _requestedCompilationGeneration = checked(_requestedCompilationGeneration + 1);
                    _pendingChanges = MergeChanges(_pendingChanges, changes);
                    _backgroundCompilationFailure = null;
                    SetCompilationStateLocked(ProjectCompilationState.Outdated);
                    ScheduleCompilationLocked(immediate: false);
                }
                ClearSampleDomainCachesCore();
                _playbackRangeResults.Clear();
            }
        }

        if (changes.AffectsAudioPcmCacheGeneration)
        {
            _ = ResetAudioCacheGenerations();
        }
        return LastAttempt;
    }

    private async Task CompilationWorkerAsync()
    {
        CancellationToken disposeToken = _disposeCancellation.Token;
        try
        {
            while (true)
            {
                await _compileSignal.WaitAsync(disposeToken).ConfigureAwait(false);
                bool immediate;
                lock (_sync)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    _compileSignalPending = false;
                    immediate = _forceImmediateCompilation;
                    _forceImmediateCompilation = false;
                }

                if (!immediate && _backgroundDebounce > TimeSpan.Zero)
                {
                    await Task.Delay(_backgroundDebounce, disposeToken).ConfigureAwait(false);
                }

                long targetRevision;
                long targetGeneration;
                ProjectChangeSet changes;
                CancellationTokenSource compilationCancellation;
                lock (_sync)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    if (_editLockCount != 0)
                    {
                        SetCompilationStateLocked(ProjectCompilationState.Outdated);
                        continue;
                    }
                    if (IsCompilationAttemptCurrentCore())
                    {
                        continue;
                    }

                    targetRevision = _sourceRevision;
                    targetGeneration = _requestedCompilationGeneration;
                    changes = CloneChanges(_pendingChanges);
                    compilationCancellation = CancellationTokenSource.CreateLinkedTokenSource(disposeToken);
                    _activeCompilationCancellation = compilationCancellation;
                    SetCompilationStateLocked(ProjectCompilationState.Compiling);
                }
                CompilationChanged?.Invoke(this, EventArgs.Empty);

                CanonicalCompiledResult? result = null;
                Exception? failure = null;
                bool canceled = false;
                MidoraProject compilationProject;
                try
                {
                    lock (_projectGate)
                    {
                        compilationCancellation.Token.ThrowIfCancellationRequested();
                        _compilationProject = ProjectCompilationSnapshot.Synchronize(
                            _compilationProject,
                            Project,
                            changes,
                            compilationCancellation.Token);
                        compilationProject = _compilationProject;
                    }
                    compilationCancellation.Token.ThrowIfCancellationRequested();
                    result = _compiler.CompileIncremental(
                        compilationProject,
                        changes,
                        cancellationToken: compilationCancellation.Token);
                }
                catch (OperationCanceledException) when (compilationCancellation.IsCancellationRequested)
                {
                    canceled = true;
                }
                catch (Exception exception)
                {
                    failure = exception;
                }

                bool publishNotification;
                lock (_sync)
                {
                    if (ReferenceEquals(_activeCompilationCancellation, compilationCancellation))
                    {
                        _activeCompilationCancellation = null;
                    }
                    compilationCancellation.Dispose();
                    if (_disposed)
                    {
                        return;
                    }

                    bool targetIsCurrent = targetRevision == _sourceRevision
                        && targetGeneration == _requestedCompilationGeneration;
                    if (!canceled && targetIsCurrent && result is not null)
                    {
                        LastAttempt = result;
                        _compiledRevision = targetRevision;
                        _publishedCompilationGeneration = targetGeneration;
                        _pendingChanges = new ProjectChangeSet();
                        _backgroundCompilationFailure = null;
                        if (result.IsConsumable)
                        {
                            LastSuccessfulResult = result;
                        }
                        SetCompilationStateLocked(result.IsConsumable
                            ? ProjectCompilationState.Succeeded
                            : ProjectCompilationState.Failed);
                    }
                    else if (!canceled && targetIsCurrent && failure is not null)
                    {
                        _compiledRevision = targetRevision;
                        _publishedCompilationGeneration = targetGeneration;
                        _pendingChanges = new ProjectChangeSet();
                        _backgroundCompilationFailure = failure;
                        SetCompilationStateLocked(ProjectCompilationState.Failed);
                    }
                    else
                    {
                        SetCompilationStateLocked(ProjectCompilationState.Outdated);
                        ScheduleCompilationLocked(immediate: true);
                    }
                    publishNotification = true;
                }
                if (publishNotification)
                {
                    CompilationChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        catch (OperationCanceledException) when (disposeToken.IsCancellationRequested)
        {
        }
    }

    private void ScheduleCompilationLocked(bool immediate)
    {
        if (_executionMode != ProjectCompilationExecutionMode.Background || _disposed)
        {
            return;
        }
        _forceImmediateCompilation |= immediate;
        if (_compileSignalPending)
        {
            return;
        }
        _compileSignalPending = true;
        _compileSignal.Release();
    }

    private void SetCompilationStateLocked(ProjectCompilationState state)
    {
        _compilationState = state;
        TaskCompletionSource<bool> previous = _compilationStateChanged;
        _compilationStateChanged = CreateStatePulse();
        previous.TrySetResult(true);
    }

    private bool IsCompilationCurrentCore() =>
        IsCompilationAttemptCurrentCore() && _backgroundCompilationFailure is null;

    private bool IsCompilationAttemptCurrentCore() =>
        _compiledRevision == _sourceRevision
        && _publishedCompilationGeneration == _requestedCompilationGeneration
        && _compilationState is ProjectCompilationState.Succeeded or ProjectCompilationState.Failed;

    private static TaskCompletionSource<bool> CreateStatePulse() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void RecordSynchronousCompilationLocked(
        ProjectChangeSet changes,
        bool sourceChanged)
    {
        if (sourceChanged)
        {
            _sourceRevision = checked(_sourceRevision + 1);
        }
        _requestedCompilationGeneration = checked(_requestedCompilationGeneration + 1);
        _compiledRevision = _sourceRevision;
        _publishedCompilationGeneration = _requestedCompilationGeneration;
        _pendingChanges = new ProjectChangeSet();
        _backgroundCompilationFailure = null;
        SetCompilationStateLocked(LastAttempt.IsConsumable
            ? ProjectCompilationState.Succeeded
            : ProjectCompilationState.Failed);
    }

    private static bool AffectsCompilation(ProjectChangeSet changes) =>
        changes.AffectsEverything
        || changes.AffectsConductor
        || changes.TrackIds.Count != 0
        || changes.EventInstrumentIds.Count != 0
        || changes.MidiChannelRootIds.Count != 0
        || changes.PureMidiTrackIds.Count != 0;

    private static ProjectChangeSet CloneChanges(ProjectChangeSet source)
    {
        ProjectChangeSet result = new()
        {
            AffectsEverything = source.AffectsEverything,
            AffectsConductor = source.AffectsConductor,
            AffectsAudioPcmCacheGeneration = source.AffectsAudioPcmCacheGeneration
        };
        result.TrackIds.UnionWith(source.TrackIds);
        result.EventInstrumentIds.UnionWith(source.EventInstrumentIds);
        result.MidiChannelRootIds.UnionWith(source.MidiChannelRootIds);
        result.PureMidiTrackIds.UnionWith(source.PureMidiTrackIds);
        return result;
    }

    private static ProjectChangeSet MergeChanges(
        ProjectChangeSet left,
        ProjectChangeSet right)
    {
        ProjectChangeSet result = new()
        {
            AffectsEverything = left.AffectsEverything || right.AffectsEverything,
            AffectsConductor = left.AffectsConductor || right.AffectsConductor,
            AffectsAudioPcmCacheGeneration = left.AffectsAudioPcmCacheGeneration
                || right.AffectsAudioPcmCacheGeneration
        };
        result.TrackIds.UnionWith(left.TrackIds);
        result.TrackIds.UnionWith(right.TrackIds);
        result.EventInstrumentIds.UnionWith(left.EventInstrumentIds);
        result.EventInstrumentIds.UnionWith(right.EventInstrumentIds);
        result.MidiChannelRootIds.UnionWith(left.MidiChannelRootIds);
        result.MidiChannelRootIds.UnionWith(right.MidiChannelRootIds);
        result.PureMidiTrackIds.UnionWith(left.PureMidiTrackIds);
        result.PureMidiTrackIds.UnionWith(right.PureMidiTrackIds);
        return result;
    }

    public CanonicalCompiledResult CompileForPlayback(long startTick, long? endTick)
    {
        lock (_projectGate)
        {
            (long StartTick, long? EndTick) key = (startTick, endTick);
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_executionMode == ProjectCompilationExecutionMode.Background
                    && !IsCompilationCurrentCore())
                {
                    throw new InvalidOperationException(
                        "Playback compilation requires the current Project source revision.");
                }
                if (_playbackRangeResults.TryGetValue(key, out CanonicalCompiledResult? cached))
                {
                    _playbackRangeCacheHitCount++;
                    return cached;
                }
                if (startTick == 0
                    && endTick is null
                    && LastAttempt.Context.IsFullProject
                    && LastAttempt.IsConsumable
                    && !LastAttempt.IsPartial)
                {
                    CanonicalCompiledResult playbackView =
                        MidoraCompiler.CreateDefaultPlaybackView(LastAttempt);
                    _playbackRangeResults.Add(key, playbackView);
                    _playbackRangeCacheHitCount++;
                    return playbackView;
                }
            }

            CanonicalCompiledResult result = _compiler.CompileIncremental(
                _executionMode == ProjectCompilationExecutionMode.Background
                    ? _compilationProject
                    : Project,
                new ProjectChangeSet(),
                new CompilationRequest
                {
                    Purpose = CompilationPurpose.Playback,
                    StartTick = startTick,
                    EndTick = endTick
                });
            lock (_sync)
            {
                _playbackRangeCompilationCount++;
                if (result.IsConsumable && !result.IsPartial)
                {
                    _playbackRangeResults.TryAdd(key, result);
                }
            }
            return result;
        }
    }

    public MidiRenderPlan GetOrCreateRenderPlan(int sampleRate)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_executionMode == ProjectCompilationExecutionMode.Background
                && !IsCompilationCurrentCore())
            {
                throw new InvalidOperationException(
                    "The current Project source revision has not finished compiling.");
            }
            CanonicalCompiledResult result = LastAttempt;
            if (!result.IsConsumable)
            {
                throw new InvalidOperationException("The current Project source has no consumable canonical result.");
            }
            (long Fingerprint, int SampleRate) key = (result.Fingerprint, sampleRate);
            if (!_samplePlans.TryGetValue(key, out MidiRenderPlan? plan))
            {
                plan = MidiRenderPlanAdapter.Create(result, sampleRate);
                _samplePlans.Add(key, plan);
            }
            return plan;
        }
    }

    public MidiRenderPlan GetOrCreateRealtimeRenderPlan(
        CanonicalCompiledResult result,
        int sampleRate,
        IReadOnlySet<MidoraId> audibleTrackIds)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(audibleTrackIds);
        if (!result.IsConsumable || result.IsPartial)
        {
            throw new ArgumentException(
                "Only a complete consumable canonical result can produce a realtime plan.",
                nameof(result));
        }

        MidiRenderPlan basePlan = GetOrCreateRealtimeBasePlan(result, sampleRate);

        ReadOnlySpan<long> sourceIds = basePlan.SourceIds;
        // A Root source owns the shared lifecycle and final Unit mix; child Track
        // sources carry the actual monitoring filters. Disabling the Root would
        // discard the complete Pure MIDI Unit after synthesis.
        HashSet<long> rootSourceIds = basePlan.UnitFragments
            .ToArray()
            .Where(fragment => fragment.MidiChannelRootId > 0)
            .Select(fragment => fragment.MidiChannelRootId)
            .ToHashSet();
        int[] disabled = new int[sourceIds.Length];
        int disabledCount = 0;
        for (int sourceIndex = 0; sourceIndex < sourceIds.Length; sourceIndex++)
        {
            long sourceId = sourceIds[sourceIndex];
            if (!rootSourceIds.Contains(sourceId)
                && !audibleTrackIds.Contains(new MidoraId(sourceId)))
            {
                disabled[disabledCount++] = sourceIndex;
            }
        }
        return basePlan.WithInitiallyDisabledSourceIndices(
            disabled.AsSpan(0, disabledCount));
    }

    public void PrewarmDefaultRealtimeRenderPlan(int sampleRate)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }
        CanonicalCompiledResult result = CompileForPlayback(0, null);
        _ = GetOrCreateRealtimeBasePlan(result, sampleRate);
    }

    private MidiRenderPlan GetOrCreateRealtimeBasePlan(
        CanonicalCompiledResult result,
        int sampleRate)
    {
        (long Fingerprint, long StartTick, long EndTick, int SampleRate) key =
            (result.Fingerprint, result.StartTick, result.EndTick, sampleRate);
        long generation;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_executionMode == ProjectCompilationExecutionMode.Background
                && !IsCompilationCurrentCore())
            {
                throw new InvalidOperationException(
                    "The current Project source revision has not finished compiling.");
            }
            if (_realtimePlans.TryGetValue(key, out MidiRenderPlan? cached))
            {
                return cached;
            }
            generation = _sampleDomainGeneration;
        }

        // Projection is deliberately outside _sync. A very dense Project can
        // take noticeable CPU time here; background prewarming must never make
        // an edit wait on this session lock.
        MidiRenderPlan created = MidiRenderPlanAdapter.CreateRealtime(result, sampleRate);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (generation == _sampleDomainGeneration)
            {
                if (_realtimePlans.TryGetValue(key, out MidiRenderPlan? concurrent))
                {
                    return concurrent;
                }
                _realtimePlans.Add(key, created);
            }
            return created;
        }
    }

    public void InvalidateSampleDomainCaches()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ClearSampleDomainCachesCore();
        }
    }

    private void ClearSampleDomainCachesCore()
    {
        _samplePlans.Clear();
        _realtimePlans.Clear();
        _sampleDomainGeneration = checked(_sampleDomainGeneration + 1);
    }

    public AudioCacheWarning ResetAudioCacheGenerations()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ClearSampleDomainCachesCore();
            AudioCacheSessionSnapshot? snapshot = _audioCacheStore?.GetSnapshot();
            if (snapshot is null)
            {
                return _audioCacheWarning;
            }
            _audioCacheWarning = snapshot.Value.Warning;
            return _audioCacheWarning;
        }
    }

    public AudioCacheWarning ConfigureAudioCache(string rootPath, long maximumReusableBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        string normalizedRootPath;
        try
        {
            normalizedRootPath = Path.GetFullPath(rootPath);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            normalizedRootPath = rootPath;
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            AudioCacheSessionSnapshot? current = _audioCacheStore?.GetSnapshot();
            if (current.HasValue
                && current.Value.MaximumReusableBytes == maximumReusableBytes
                && string.Equals(
                    Path.TrimEndingDirectorySeparator(current.Value.RootPath),
                    Path.TrimEndingDirectorySeparator(normalizedRootPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                _audioCacheWarning = current.Value.Warning;
                return _audioCacheWarning;
            }
        }

        AudioCacheSessionStore? replacement = null;
        AudioCacheWarning warning = default;
        try
        {
            replacement = new AudioCacheSessionStore(normalizedRootPath, maximumReusableBytes);
            warning = replacement.GetSnapshot().Warning;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            warning = new(
                AudioCacheWarningCode.AudioCacheRetentionDisabled,
                "The configured audio cache is unavailable; playback will render without reusable retention. "
                    + exception.Message);
        }

        AudioCacheSessionStore? previous;
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                previous = _audioCacheStore;
                _audioCacheStore = replacement;
                _audioCacheWarning = warning;
                ClearSampleDomainCachesCore();
            }
        }
        catch
        {
            replacement?.Dispose();
            throw;
        }
        previous?.Dispose();
        return warning;
    }

    public bool TryReadReusableAudio(string key, out byte[] payload)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_audioCacheStore is not null
                && _audioCacheStore.TryReadReusable(key, out payload))
            {
                _audioCacheWarning = _audioCacheStore.GetSnapshot().Warning;
                return true;
            }
            payload = [];
            return false;
        }
    }

    public AudioCachePublishResult PublishReusableAudio(string key, ReadOnlySpan<byte> payload)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_audioCacheStore is null)
            {
                return new(
                    false,
                    false,
                    AudioCacheRetentionState.DisabledByWriteFailure,
                    _audioCacheWarning);
            }
            AudioCachePublishResult result = _audioCacheStore.PublishReusable(key, payload);
            _audioCacheWarning = result.Warning;
            return result;
        }
    }

    public bool TryCopyReusableAudio(
        string key,
        Stream destination,
        out long payloadLength)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_audioCacheStore is not null
                && _audioCacheStore.TryCopyReusable(key, destination, out payloadLength))
            {
                _audioCacheWarning = _audioCacheStore.GetSnapshot().Warning;
                return true;
            }
            payloadLength = 0;
            return false;
        }
    }

    public AudioCachePublishResult PublishReusableAudio(
        string key,
        Stream source,
        long payloadLength)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_audioCacheStore is null)
            {
                return new(
                    false,
                    false,
                    AudioCacheRetentionState.DisabledByWriteFailure,
                    _audioCacheWarning);
            }
            AudioCachePublishResult result = _audioCacheStore.PublishReusable(
                key,
                source,
                payloadLength);
            _audioCacheWarning = result.Warning;
            return result;
        }
    }

    public ReusableAudioReadLease? AcquireReusableAudioReadLease(
        IReadOnlyList<string> keys)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _audioCacheStore?.AcquireReusableReadLease(keys);
        }
    }

    public bool SupportsReusableAudioPackJournals
    {
        get
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _audioCacheStore?.SupportsReusableAudioPackJournals == true;
            }
        }
    }

    public void AdoptReusableAudioPackJournals(
        AudioCacheSessionStore.AudioRecoverySpool spool,
        string journalDirectory,
        IReadOnlyCollection<string> completedKeys)
    {
        ArgumentNullException.ThrowIfNull(spool);
        ArgumentException.ThrowIfNullOrWhiteSpace(journalDirectory);
        ArgumentNullException.ThrowIfNull(completedKeys);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_audioCacheStore is null)
            {
                spool.Dispose();
                return;
            }
            _audioCacheStore.AdoptReusableAudioPackJournals(
                spool,
                journalDirectory,
                completedKeys);
            _audioCacheWarning = _audioCacheStore.GetSnapshot().Warning;
        }
    }

    public void QueueReusableAudioBatch(
        AudioCacheSessionStore.AudioRecoverySpool spool,
        IReadOnlyList<AudioCachePublishSlice> slices)
    {
        ArgumentNullException.ThrowIfNull(spool);
        ArgumentNullException.ThrowIfNull(slices);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_audioCacheStore is null)
            {
                spool.Dispose();
                return;
            }
            _audioCacheStore.QueueReusableBatch(spool, slices);
            _audioCacheWarning = _audioCacheStore.GetSnapshot().Warning;
        }
    }

    public void InvalidateReusableAudio(string key)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _audioCacheStore?.InvalidateReusable(key);
            if (_audioCacheStore is not null)
            {
                _audioCacheWarning = _audioCacheStore.GetSnapshot().Warning;
            }
        }
    }

    public void RegisterReusableAudioGeneration(string owner, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _audioCacheStore?.RegisterReusableGeneration(owner, key);
            if (_audioCacheStore is not null)
            {
                _audioCacheWarning = _audioCacheStore.GetSnapshot().Warning;
            }
        }
    }

    public void RegisterReusableAudioGenerations(
        IReadOnlyList<AudioCacheGenerationBinding> bindings)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _audioCacheStore?.RegisterReusableGenerations(bindings);
        }
    }

    public bool TryCompactReusableAudio(bool isStopped, TimeSpan idleDuration)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _audioCacheStore?.TryCompactReusable(isStopped, idleDuration) == true;
        }
    }

    public int ClearInactiveAudioCacheSessions()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _audioCacheStore?.ClearInactiveSessions() ?? 0;
        }
    }

    public AudioCacheSessionStore.AudioRecoverySpool CreateBufferingRecoverySpool(
        long lengthBytes)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_audioCacheStore is null)
            {
                throw new AudioRecoveryStorageUnavailableException(
                    "The complete Buffering recovery interval cannot be reserved because the configured cache root is unavailable.",
                    new IOException(_audioCacheWarning.Message));
            }
            return _audioCacheStore.CreateRecoverySpool(lengthBytes);
        }
    }

    public AudioCacheSessionStore.AudioRecoverySpool CreateTransientAudioSpool(
        long lengthBytes) => CreateBufferingRecoverySpool(lengthBytes);

    public AudioCacheSessionStore.AudioRecoverySpool CreateSparseTransientAudioSpool(
        long lengthBytes)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_audioCacheStore is null)
            {
                throw new AudioRecoveryStorageUnavailableException(
                    "The Segment cache staging file cannot be created because the configured cache root is unavailable.",
                    new IOException(_audioCacheWarning.Message));
            }
            return _audioCacheStore.CreateRecoverySpool(lengthBytes, sparse: true);
        }
    }

    public void DisableReusableAudioRetention(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_audioCacheStore is null)
            {
                _audioCacheWarning = new(
                    AudioCacheWarningCode.AudioCacheRetentionDisabled,
                    reason);
                return;
            }
            _audioCacheStore.DisableReusableRetention(reason);
            _audioCacheWarning = _audioCacheStore.GetSnapshot().Warning;
        }
    }

    public void SetEffectiveSoundFontPath(string? value) =>
        SetEffectiveSoundFontPaths(value is null ? [] : [value]);

    public void SetEffectiveSoundFontPaths(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        string[] normalized = values.Select(Path.GetFullPath).ToArray();
        string? identity = normalized.Length == 0
            ? null
            : SoundFontSetDefinition.CreateConfiguration(normalized).CacheIdentity;
        bool changed;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_editLockCount != 0)
            {
                throw new InvalidOperationException(
                    "The effective SoundFont cannot change while a Project edit lock is active.");
            }
            changed = !EffectiveSoundFontPaths.SequenceEqual(
                    normalized,
                    StringComparer.OrdinalIgnoreCase)
                || !string.Equals(EffectiveSoundFontSetCacheIdentity, identity, StringComparison.Ordinal);
            EffectiveSoundFontPaths = normalized;
            EffectiveSoundFontSetCacheIdentity = identity;
            if (changed) ClearSampleDomainCachesCore();
        }
        if (changed) EffectiveSoundFontChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshEffectiveSoundFontCacheIdentity()
    {
        IReadOnlyList<string> paths = EffectiveSoundFontPaths;
        if (paths.Count == 0)
        {
            return;
        }
        string identity = SoundFontSetDefinition.Create(paths).CacheIdentity;
        bool changed;
        lock (_sync)
        {
            changed = !string.Equals(EffectiveSoundFontSetCacheIdentity, identity, StringComparison.Ordinal);
            EffectiveSoundFontSetCacheIdentity = identity;
        }
        if (changed) EffectiveSoundFontChanged?.Invoke(this, EventArgs.Empty);
    }

    public long SnapshotTotalEditingTimeMilliseconds()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _editingTime.SnapshotTotalEditingTimeMilliseconds();
        }
    }

    public void NotifySystemSuspending()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _editingTime.NotifySystemSuspending();
        }
    }

    public void NotifySystemResumed()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _editingTime.NotifySystemResumed();
        }
    }

    public void BeginProjectClosing()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _editingTime.BeginClosing();
        }
    }

    public void CancelProjectClosing()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _editingTime.CancelClosing();
        }
    }

    public IDisposable AcquireProjectEditLock()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeCompilationCancellation?.Cancel();
        }
        lock (_projectGate)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _editLockCount = checked(_editLockCount + 1);
                return new ProjectEditLockLease(this);
            }
        }
    }

    public void Dispose()
    {
        Task? worker;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _activeCompilationCancellation?.Cancel();
            _disposeCancellation.Cancel();
            _compilationStateChanged.TrySetResult(true);
            worker = _compileWorker;
        }

        if (worker is not null && Task.CurrentId != worker.Id)
        {
            try
            {
                worker.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }

        lock (_sync)
        {
            ClearSampleDomainCachesCore();
            _playbackRangeResults.Clear();
            try
            {
                try
                {
                    _audioCacheStore?.Dispose();
                    _audioCacheStore = null;
                }
                finally
                {
                    _editingTime.Dispose();
                }
            }
            finally
            {
                _compiler.Dispose();
                _editLockCount = 0;
                _activeCompilationCancellation?.Dispose();
                _activeCompilationCancellation = null;
                _compileSignal.Dispose();
                _disposeCancellation.Dispose();
            }
        }
    }

    private void ReleaseProjectEditLock()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            if (_editLockCount <= 0)
            {
                throw new InvalidOperationException("The Project edit lock lease has already been released.");
            }
            _editLockCount--;
            if (_editLockCount == 0 && !IsCompilationAttemptCurrentCore())
            {
                ScheduleCompilationLocked(immediate: true);
            }
        }
    }

    private sealed class ProjectEditLockLease(ProjectCompilationSession owner) : IDisposable
    {
        private ProjectCompilationSession? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseProjectEditLock();
    }
}
