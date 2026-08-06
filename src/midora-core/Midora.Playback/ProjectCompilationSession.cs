using Midora.Audio;
using Midora.Compiler;
using Midora.Domain;

namespace Midora.Playback;

public sealed class ProjectCompilationSession : IDisposable
{
    private readonly object _sync = new();
    private readonly MidoraCompiler _compiler = new();
    private readonly Dictionary<(long Fingerprint, int SampleRate), MidiRenderPlan> _samplePlans = [];
    private bool _editsLocked;
    private bool _disposed;

    public ProjectCompilationSession(MidoraProject project)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        LastAttempt = _compiler.CompileFull(project);
        if (LastAttempt.IsConsumable)
        {
            LastSuccessfulResult = LastAttempt;
        }
    }

    public MidoraProject Project { get; }
    public CanonicalCompiledResult LastAttempt { get; private set; }
    public CanonicalCompiledResult? LastSuccessfulResult { get; private set; }
    public CompilerRunTelemetry LastCompilationTelemetry => _compiler.LastTelemetry;
    public bool EditsLocked => Volatile.Read(ref _editsLocked);
    public event EventHandler? CompilationChanged;

    public CanonicalCompiledResult ApplyEdit(Action<MidoraProject> edit, ProjectChangeSet changes)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(changes);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_editsLocked)
            {
                throw new InvalidOperationException("Project edits are forbidden while audio is Preparing, Playing or Buffering.");
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
            if (_editsLocked)
            {
                throw new InvalidOperationException("Compilation after editing is forbidden while audio is active.");
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

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _samplePlans.Clear();
            _compiler.Dispose();
            _disposed = true;
        }
    }

    internal void SetEditsLocked(bool value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Volatile.Write(ref _editsLocked, value);
    }
}
