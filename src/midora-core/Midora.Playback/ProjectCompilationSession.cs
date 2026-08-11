using Midora.Audio;
using Midora.Compiler;
using Midora.Domain;

namespace Midora.Playback;

public sealed class ProjectCompilationSession : IDisposable, IRealtimePlaybackCacheStore
{
    private readonly object _sync = new();
    private readonly MidoraCompiler _compiler = new();
    private readonly ProjectEditingTimeSession _editingTime;
    private readonly Dictionary<(long Fingerprint, int SampleRate), MidiRenderPlan> _samplePlans = [];
    private readonly Dictionary<(long StartTick, long? EndTick), CanonicalCompiledResult>
        _playbackRangeResults = [];
    private AudioCacheSessionStore? _audioCacheStore;
    private AudioCacheWarning _audioCacheWarning;
    private long _playbackRangeCacheHitCount;
    private long _playbackRangeCompilationCount;
    private int _editLockCount;
    private bool _disposed;

    public ProjectCompilationSession(
        MidoraProject project,
        string? effectiveSoundFontPath = null,
        TimeProvider? editingTimeProvider = null)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        string? normalizedSoundFontPath = effectiveSoundFontPath is null
            ? null
            : Path.GetFullPath(effectiveSoundFontPath);
        _editingTime = new ProjectEditingTimeSession(project, editingTimeProvider);
        EffectiveSoundFontPath = normalizedSoundFontPath;
        try
        {
            LastAttempt = _compiler.CompileFull(project);
            if (LastAttempt.IsConsumable)
            {
                LastSuccessfulResult = LastAttempt;
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
    public string? EffectiveSoundFontPath { get; private set; }
    public CanonicalCompiledResult LastAttempt { get; private set; }
    public CanonicalCompiledResult? LastSuccessfulResult { get; private set; }
    public CompilerRunTelemetry LastCompilationTelemetry => _compiler.LastTelemetry;
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

    internal CanonicalCompiledResult ApplyEdit(Action<MidoraProject> edit, ProjectChangeSet changes)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(changes);
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
            _samplePlans.Clear();
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
                _samplePlans.Clear();
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
                    _samplePlans.Clear();
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
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_editLockCount != 0)
            {
                throw new InvalidOperationException(
                    "Compilation after editing is forbidden while a Project edit lock is active.");
            }
            LastAttempt = _compiler.CompileIncremental(Project, changes);
            _samplePlans.Clear();
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

    public CanonicalCompiledResult CompileForPlayback(long startTick, long? endTick)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            (long StartTick, long? EndTick) key = (startTick, endTick);
            if (_playbackRangeResults.TryGetValue(key, out CanonicalCompiledResult? cached))
            {
                _playbackRangeCacheHitCount++;
                return cached;
            }

            CanonicalCompiledResult result = _compiler.CompileIncremental(
                Project,
                new ProjectChangeSet(),
                new CompilationRequest
                {
                    Purpose = CompilationPurpose.Playback,
                    StartTick = startTick,
                    EndTick = endTick
                });
            _playbackRangeCompilationCount++;
            if (result.IsConsumable && !result.IsPartial)
            {
                _playbackRangeResults.Add(key, result);
            }
            return result;
        }
    }

    public MidiRenderPlan GetOrCreateRenderPlan(int sampleRate)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
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

    public void InvalidateSampleDomainCaches()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _samplePlans.Clear();
        }
    }

    public AudioCacheWarning ResetAudioCacheGenerations()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _samplePlans.Clear();
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
                _samplePlans.Clear();
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

    public void SetEffectiveSoundFontPath(string? value)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_editLockCount != 0)
            {
                throw new InvalidOperationException(
                    "The effective SoundFont cannot change while a Project edit lock is active.");
            }
            EffectiveSoundFontPath = value is null ? null : Path.GetFullPath(value);
        }
    }

    public bool TryBeginSoundFontVerification(
        ProjectSoundFontReference? expectedReference)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_editLockCount != 0)
            {
                throw new InvalidOperationException(
                    "The effective SoundFont cannot be verified while a Project edit lock is active.");
            }
            if (Project.SoundFont.Reference != expectedReference)
            {
                return false;
            }
            EffectiveSoundFontPath = null;
            _samplePlans.Clear();
            return true;
        }
    }

    public bool TrySetVerifiedSoundFontPath(
        ProjectSoundFontReference expectedReference,
        string value)
    {
        ArgumentNullException.ThrowIfNull(expectedReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string path = Path.GetFullPath(value);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_editLockCount != 0)
            {
                throw new InvalidOperationException(
                    "The effective SoundFont cannot change while a Project edit lock is active.");
            }
            if (Project.SoundFont.Reference != expectedReference)
            {
                return false;
            }
            EffectiveSoundFontPath = path;
            _samplePlans.Clear();
            return true;
        }
    }

    public bool TryInvalidateSoundFontResource(
        ProjectSoundFontReference expectedReference)
    {
        ArgumentNullException.ThrowIfNull(expectedReference);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Project.SoundFont.Reference != expectedReference)
            {
                return false;
            }
            EffectiveSoundFontPath = null;
            _samplePlans.Clear();
            return true;
        }
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
            _editLockCount = checked(_editLockCount + 1);
            return new ProjectEditLockLease(this);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _samplePlans.Clear();
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
                _disposed = true;
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
        }
    }

    private sealed class ProjectEditLockLease(ProjectCompilationSession owner) : IDisposable
    {
        private ProjectCompilationSession? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseProjectEditLock();
    }
}
