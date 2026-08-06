using Midora.Audio;
using Midora.Compiler;
using Midora.Domain;

namespace Midora.Playback;

public sealed class ProjectCompilationSession : IDisposable
{
    private readonly object _sync = new();
    private readonly MidoraCompiler _compiler = new();
    private readonly ProjectEditingTimeSession _editingTime;
    private readonly Dictionary<(long Fingerprint, int SampleRate), MidiRenderPlan> _samplePlans = [];
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
    public string? EffectiveSoundFontPath { get; private set; }
    public CanonicalCompiledResult LastAttempt { get; private set; }
    public CanonicalCompiledResult? LastSuccessfulResult { get; private set; }
    public CompilerRunTelemetry LastCompilationTelemetry => _compiler.LastTelemetry;
    public bool EditsLocked => Volatile.Read(ref _editLockCount) != 0;
    public event EventHandler? CompilationChanged;

    public CanonicalCompiledResult ApplyEdit(Action<MidoraProject> edit, ProjectChangeSet changes)
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
            if (LastAttempt.IsConsumable)
            {
                LastSuccessfulResult = LastAttempt;
            }
        }
        CompilationChanged?.Invoke(this, EventArgs.Empty);
        return LastAttempt;
    }

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
            if (LastAttempt.IsConsumable)
            {
                LastSuccessfulResult = LastAttempt;
            }
        }
        CompilationChanged?.Invoke(this, EventArgs.Empty);
        return LastAttempt;
    }

    public CanonicalCompiledResult CompileForPlayback(long startTick, long? endTick)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _compiler.CompileIncremental(Project, new ProjectChangeSet(), new CompilationRequest
            {
                Purpose = CompilationPurpose.Playback,
                StartTick = startTick,
                EndTick = endTick
            });
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
            try
            {
                _editingTime.Dispose();
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
