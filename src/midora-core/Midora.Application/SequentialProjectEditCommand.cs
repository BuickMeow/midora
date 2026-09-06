using Midora.Domain;

namespace Midora.Application;

/// <summary>
/// Prepares a bounded sequence of edits against the result of the preceding edit,
/// then exposes the whole sequence as one Project-history transaction.
/// </summary>
/// <remarks>
/// This command is intended for transactional property editors that update existing
/// objects. The supplied factories must not allocate new stable IDs while preparing.
/// </remarks>
public sealed class SequentialProjectEditCommand : IProjectEditCommand
{
    private readonly IReadOnlyList<Func<MidoraProject, IProjectEditCommand>> _factories;

    public SequentialProjectEditCommand(
        string name,
        IEnumerable<Func<MidoraProject, IProjectEditCommand>> factories)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A Project edit command name is required.", nameof(name));
        }
        ArgumentNullException.ThrowIfNull(factories);
        Name = name.Trim();
        _factories = factories.ToArray();
        if (_factories.Count == 0 || _factories.Any(static value => value is null))
        {
            throw new ArgumentException(
                "At least one valid Project edit factory is required.",
                nameof(factories));
        }
    }

    public string Name { get; }

    public IPreparedProjectEdit Prepare(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        List<IPreparedProjectEdit> prepared = [];
        try
        {
            foreach (Func<MidoraProject, IProjectEditCommand> factory in _factories)
            {
                IProjectEditCommand command = factory(project)
                    ?? throw new InvalidOperationException(
                        "A sequential Project edit factory returned no command.");
                IPreparedProjectEdit edit = command.Prepare(project)
                    ?? throw new InvalidOperationException(
                        "A sequential Project edit command returned no prepared edit.");
                edit.Apply(project);
                prepared.Add(edit);
            }
        }
        catch
        {
            try
            {
                UndoPrepared(project, prepared);
            }
            finally
            {
                DisposePrepared(prepared);
            }
            throw;
        }

        try
        {
            UndoPrepared(project, prepared);
        }
        catch
        {
            DisposePrepared(prepared);
            throw;
        }
        ProjectChangeSet changes = MergeChanges(prepared.Select(value => value.Changes));
        IPreparedProjectEdit result = new Prepared(prepared.ToArray(), changes);
        return ExactTimelineCollisionPolicy.CombineScopes(result, prepared);
    }

    private static void UndoPrepared(
        MidoraProject project,
        IReadOnlyList<IPreparedProjectEdit> prepared)
    {
        for (int index = prepared.Count - 1; index >= 0; index--)
        {
            prepared[index].Undo(project);
        }
    }

    private static void DisposePrepared(IEnumerable<IPreparedProjectEdit> prepared)
    {
        foreach (IPreparedProjectEdit edit in prepared)
            if (edit is IDisposable disposable) disposable.Dispose();
    }

    private static ProjectChangeSet MergeChanges(IEnumerable<ProjectChangeSet> values)
    {
        ProjectChangeSet[] source = values.ToArray();
        ProjectChangeSet result = new()
        {
            AffectsEverything = source.Any(value => value.AffectsEverything),
            AffectsConductor = source.Any(value => value.AffectsConductor),
            AffectsAudioPcmCacheGeneration =
                source.Any(value => value.AffectsAudioPcmCacheGeneration)
        };
        foreach (ProjectChangeSet value in source)
        {
            result.TrackIds.UnionWith(value.TrackIds);
            result.EventInstrumentIds.UnionWith(value.EventInstrumentIds);
            result.EventInstrumentUsageIds.UnionWith(value.EventInstrumentUsageIds);
            result.MidiChannelRootIds.UnionWith(value.MidiChannelRootIds);
            result.PureMidiTrackIds.UnionWith(value.PureMidiTrackIds);
            result.PresentationTrackIds.UnionWith(value.PresentationTrackIds);
            result.PresentationEventInstrumentIds.UnionWith(
                value.PresentationEventInstrumentIds);
            result.TimelineOwnerChanges.AddRange(value.TimelineOwnerChanges);
        }
        return result;
    }

    private sealed class Prepared(
        IReadOnlyList<IPreparedProjectEdit> edits,
        ProjectChangeSet changes) : IPreparedProjectEdit, IDisposable
    {
        public bool HasChanges => edits.Any(value => value.HasChanges);
        public ProjectChangeSet Changes { get; } = changes;

        public void Apply(MidoraProject project)
        {
            int applied = 0;
            try
            {
                for (; applied < edits.Count; applied++) edits[applied].Apply(project);
            }
            catch
            {
                for (int index = applied - 1; index >= 0; index--) edits[index].Undo(project);
                throw;
            }
        }

        public void Undo(MidoraProject project)
        {
            for (int index = edits.Count - 1; index >= 0; index--) edits[index].Undo(project);
        }

        public void Dispose() => DisposePrepared(edits);
    }
}
