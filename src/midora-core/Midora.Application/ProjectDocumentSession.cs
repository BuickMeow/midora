using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

namespace Midora.Application;

public enum ProjectDocumentOrigin
{
    Unsaved,
    Persisted
}

public sealed record ProjectHistoryEntryInfo(
    string Name,
    long BeforeStateId,
    long AfterStateId);

public sealed record ProjectEditExecution(
    bool Changed,
    CanonicalCompiledResult CompilationResult);

public sealed class ProjectContentChangedEventArgs : EventArgs
{
    public ProjectContentChangedEventArgs(ProjectChangeSet changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        AffectsEverything = changes.AffectsEverything;
        AffectsConductor = changes.AffectsConductor;
        AffectsAudioPcmCacheGeneration = changes.AffectsAudioPcmCacheGeneration;
        TrackIds = Array.AsReadOnly(changes.TrackIds.Order().ToArray());
        EventInstrumentIds = Array.AsReadOnly(changes.EventInstrumentIds.Order().ToArray());
        EventInstrumentUsageIds = Array.AsReadOnly(changes.EventInstrumentUsageIds.Order().ToArray());
        MidiChannelRootIds = Array.AsReadOnly(changes.MidiChannelRootIds.Order().ToArray());
        PureMidiTrackIds = Array.AsReadOnly(changes.PureMidiTrackIds.Order().ToArray());
        PresentationTrackIds = Array.AsReadOnly(changes.PresentationTrackIds.Order().ToArray());
        PresentationEventInstrumentIds = Array.AsReadOnly(
            changes.PresentationEventInstrumentIds.Order().ToArray());
    }

    public bool AffectsEverything { get; }
    public bool AffectsConductor { get; }
    public bool AffectsAudioPcmCacheGeneration { get; }
    public IReadOnlyList<MidoraId> TrackIds { get; }
    public IReadOnlyList<MidoraId> EventInstrumentIds { get; }
    public IReadOnlyList<MidoraId> EventInstrumentUsageIds { get; }
    public IReadOnlyList<MidoraId> MidiChannelRootIds { get; }
    public IReadOnlyList<MidoraId> PureMidiTrackIds { get; }
    public IReadOnlyList<MidoraId> PresentationTrackIds { get; }
    public IReadOnlyList<MidoraId> PresentationEventInstrumentIds { get; }
    public bool IsEmpty => !AffectsEverything
        && !AffectsConductor
        && !AffectsAudioPcmCacheGeneration
        && TrackIds.Count == 0
        && EventInstrumentIds.Count == 0
        && EventInstrumentUsageIds.Count == 0
        && MidiChannelRootIds.Count == 0
        && PureMidiTrackIds.Count == 0
        && PresentationTrackIds.Count == 0
        && PresentationEventInstrumentIds.Count == 0;
}

public interface IProjectEditCommand
{
    string Name { get; }
    IPreparedProjectEdit Prepare(MidoraProject project);
}

public interface IPreparedProjectEdit
{
    bool HasChanges { get; }
    ProjectChangeSet Changes { get; }
    void Apply(MidoraProject project);
    void Undo(MidoraProject project);
}

public sealed class ProjectPropertyEditCommand<T> : IProjectEditCommand
{
    private readonly Func<MidoraProject, T> _read;
    private readonly Action<MidoraProject, T> _write;
    private readonly T _newValue;
    private readonly ProjectChangeSet _changes;
    private readonly IEqualityComparer<T> _comparer;

    public ProjectPropertyEditCommand(
        string name,
        Func<MidoraProject, T> read,
        Action<MidoraProject, T> write,
        T newValue,
        ProjectChangeSet changes,
        IEqualityComparer<T>? comparer = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A Project edit command name is required.", nameof(name));
        }
        Name = name.Trim();
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _newValue = newValue;
        _changes = CloneChanges(changes ?? throw new ArgumentNullException(nameof(changes)));
        _comparer = comparer ?? EqualityComparer<T>.Default;
    }

    public string Name { get; }

    public IPreparedProjectEdit Prepare(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        T oldValue = _read(project);
        return new Prepared(
            _write,
            oldValue,
            _newValue,
            !_comparer.Equals(oldValue, _newValue),
            _changes);
    }

    private sealed class Prepared(
        Action<MidoraProject, T> write,
        T oldValue,
        T newValue,
        bool hasChanges,
        ProjectChangeSet changes) : IPreparedProjectEdit
    {
        public bool HasChanges { get; } = hasChanges;
        public ProjectChangeSet Changes { get; } = changes;
        public void Apply(MidoraProject project) => write(project, newValue);
        public void Undo(MidoraProject project) => write(project, oldValue);
    }

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
        result.EventInstrumentUsageIds.UnionWith(source.EventInstrumentUsageIds);
        result.MidiChannelRootIds.UnionWith(source.MidiChannelRootIds);
        result.PureMidiTrackIds.UnionWith(source.PureMidiTrackIds);
        result.PresentationTrackIds.UnionWith(source.PresentationTrackIds);
        result.PresentationEventInstrumentIds.UnionWith(
            source.PresentationEventInstrumentIds);
        return result;
    }
}

public sealed class ProjectDocumentSession
{
    private readonly object _clipboardSessionIdentity = new();
    private readonly object _sync = new();
    private readonly ProjectCompilationSession _compilation;
    private readonly List<HistoryEntry> _entries = [];
    private readonly HashSet<string> _externalDirtyReasons = new(StringComparer.Ordinal);
    private int _cursor;
    private long _currentStateId;
    private long _nextStateId = 1;
    private long _baselineStateId;
    private bool _hasPersistentOrigin;
    private bool _notifying;

    public ProjectDocumentSession(
        ProjectCompilationSession compilation,
        ProjectDocumentOrigin origin = ProjectDocumentOrigin.Unsaved)
    {
        _compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
        _hasPersistentOrigin = origin switch
        {
            ProjectDocumentOrigin.Unsaved => false,
            ProjectDocumentOrigin.Persisted => true,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        _baselineStateId = _currentStateId;
    }

    public MidoraProject Project => _compilation.Project;
    public ProjectCompilationSession Compilation => _compilation;
    internal object ClipboardSessionIdentity => _clipboardSessionIdentity;

    public bool HasPersistentOrigin
    {
        get
        {
            lock (_sync)
            {
                return _hasPersistentOrigin;
            }
        }
    }

    public bool IsModified
    {
        get
        {
            lock (_sync)
            {
                return IsModifiedCore;
            }
        }
    }

    public bool NeedsSaveBeforeClose
    {
        get
        {
            lock (_sync)
            {
                return !_hasPersistentOrigin || IsModifiedCore;
            }
        }
    }

    public bool CanUndo
    {
        get
        {
            lock (_sync)
            {
                return _cursor != 0;
            }
        }
    }

    public bool CanRedo
    {
        get
        {
            lock (_sync)
            {
                return _cursor != _entries.Count;
            }
        }
    }

    public string? UndoName
    {
        get
        {
            lock (_sync)
            {
                return _cursor == 0 ? null : _entries[_cursor - 1].Name;
            }
        }
    }

    public string? RedoName
    {
        get
        {
            lock (_sync)
            {
                return _cursor == _entries.Count ? null : _entries[_cursor].Name;
            }
        }
    }

    public IReadOnlyList<ProjectHistoryEntryInfo> History
    {
        get
        {
            lock (_sync)
            {
                return _entries
                    .Select(entry => new ProjectHistoryEntryInfo(
                        entry.Name,
                        entry.BeforeStateId,
                        entry.AfterStateId))
                    .ToArray();
            }
        }
    }

    public IReadOnlyList<string> ExternalDirtyReasons
    {
        get
        {
            lock (_sync)
            {
                return _externalDirtyReasons.Order(StringComparer.Ordinal).ToArray();
            }
        }
    }

    public event EventHandler? HistoryChanged;
    public event EventHandler<ProjectContentChangedEventArgs>? ContentChanged;

    public ProjectEditExecution Execute(IProjectEditCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_sync)
        {
            ThrowIfNotifying();
            string commandName = command.Name;
            if (string.IsNullOrWhiteSpace(commandName))
            {
                throw new ArgumentException(
                    "A Project edit command name is required.",
                    nameof(command));
            }
            commandName = commandName.Trim();
            IPreparedProjectEdit sourcePrepared = command.Prepare(Project)
                ?? throw new InvalidOperationException(
                    "A Project edit command returned no prepared edit.");
            FrozenPreparedProjectEdit prepared = FreezePreparedEdit(Project, sourcePrepared);
            if (!prepared.HasChanges)
            {
                return new(false, _compilation.LastAttempt);
            }
            if (_nextStateId == long.MaxValue)
            {
                throw new InvalidOperationException("The Project history state counter is exhausted.");
            }

            CanonicalCompiledResult result = _compilation.ApplyReversibleEdit(
                prepared.Apply,
                prepared.Undo,
                prepared.Changes);
            if (_cursor != _entries.Count)
            {
                _entries.RemoveRange(_cursor, _entries.Count - _cursor);
            }
            long nextStateId = _nextStateId++;
            _entries.Add(new(
                commandName,
                _currentStateId,
                nextStateId,
                prepared));
            _cursor++;
            _currentStateId = nextStateId;
            NotifyEditChanged(prepared.Changes);
            return new(true, result);
        }
    }

    public CanonicalCompiledResult Undo()
    {
        lock (_sync)
        {
            ThrowIfNotifying();
            if (_cursor == 0)
            {
                throw new InvalidOperationException("There is no Project edit to undo.");
            }
            HistoryEntry entry = _entries[_cursor - 1];
            CanonicalCompiledResult result = _compilation.ApplyReversibleEdit(
                entry.Prepared.Undo,
                entry.Prepared.Apply,
                entry.Prepared.Changes);
            _cursor--;
            _currentStateId = entry.BeforeStateId;
            NotifyEditChanged(entry.Prepared.Changes);
            return result;
        }
    }

    public CanonicalCompiledResult Redo()
    {
        lock (_sync)
        {
            ThrowIfNotifying();
            if (_cursor == _entries.Count)
            {
                throw new InvalidOperationException("There is no Project edit to redo.");
            }
            HistoryEntry entry = _entries[_cursor];
            CanonicalCompiledResult result = _compilation.ApplyReversibleEdit(
                entry.Prepared.Apply,
                entry.Prepared.Undo,
                entry.Prepared.Changes);
            _cursor++;
            _currentStateId = entry.AfterStateId;
            NotifyEditChanged(entry.Prepared.Changes);
            return result;
        }
    }

    public void MarkSaveSucceeded()
    {
        lock (_sync)
        {
            ThrowIfNotifying();
            bool changed = !_hasPersistentOrigin
                || _baselineStateId != _currentStateId
                || _externalDirtyReasons.Count != 0;
            _hasPersistentOrigin = true;
            _baselineStateId = _currentStateId;
            _externalDirtyReasons.Clear();
            if (changed)
            {
                NotifyHistoryChanged(compilationChanged: false);
            }
        }
    }

    public void MarkExternallyModified(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("An external modification reason is required.", nameof(reason));
        }
        lock (_sync)
        {
            ThrowIfNotifying();
            if (_externalDirtyReasons.Add(reason.Trim()))
            {
                NotifyHistoryChanged(compilationChanged: false);
            }
        }
    }

    private bool IsModifiedCore =>
        _externalDirtyReasons.Count != 0
        || _baselineStateId != _currentStateId;

    private void NotifyEditChanged(ProjectChangeSet changes)
    {
        NotifyHistoryChanged(compilationChanged: true, changes);
    }

    public long CurrentStateId
    {
        get
        {
            lock (_sync)
            {
                return _currentStateId;
            }
        }
    }

    private void NotifyHistoryChanged(
        bool compilationChanged,
        ProjectChangeSet? changes = null)
    {
        _notifying = true;
        try
        {
            if (compilationChanged)
            {
                _compilation.NotifyCompilationChanged();
                ContentChanged?.Invoke(this, new ProjectContentChangedEventArgs(
                    changes ?? ProjectChangeSet.Everything));
            }
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _notifying = false;
        }
    }

    private void ThrowIfNotifying()
    {
        if (_notifying)
        {
            throw new InvalidOperationException(
                "Project History cannot be mutated reentrantly from a change notification.");
        }
    }

    private static FrozenPreparedProjectEdit FreezePreparedEdit(
        MidoraProject project,
        IPreparedProjectEdit prepared)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(prepared.Changes);
        ProjectChangeSet changes = new()
        {
            AffectsEverything = prepared.Changes.AffectsEverything,
            AffectsConductor = prepared.Changes.AffectsConductor,
            AffectsAudioPcmCacheGeneration =
                prepared.Changes.AffectsAudioPcmCacheGeneration
        };
        changes.TrackIds.UnionWith(prepared.Changes.TrackIds);
        changes.EventInstrumentIds.UnionWith(prepared.Changes.EventInstrumentIds);
        changes.EventInstrumentUsageIds.UnionWith(prepared.Changes.EventInstrumentUsageIds);
        changes.MidiChannelRootIds.UnionWith(prepared.Changes.MidiChannelRootIds);
        changes.PureMidiTrackIds.UnionWith(prepared.Changes.PureMidiTrackIds);
        changes.PresentationTrackIds.UnionWith(prepared.Changes.PresentationTrackIds);
        changes.PresentationEventInstrumentIds.UnionWith(
            prepared.Changes.PresentationEventInstrumentIds);
        return new(
            ExactTimelineCollisionPolicy.Wrap(project, prepared),
            changes);
    }

    private sealed record HistoryEntry(
        string Name,
        long BeforeStateId,
        long AfterStateId,
        IPreparedProjectEdit Prepared);

    private sealed class FrozenPreparedProjectEdit(
        IPreparedProjectEdit source,
        ProjectChangeSet changes) : IPreparedProjectEdit
    {
        public bool HasChanges { get; } = source.HasChanges;
        public ProjectChangeSet Changes { get; } = changes;
        public void Apply(MidoraProject project) => source.Apply(project);
        public void Undo(MidoraProject project) => source.Undo(project);
    }
}
