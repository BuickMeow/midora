using System.Collections.ObjectModel;
using Midora.Domain;

namespace Midora.Application;

internal readonly record struct DirectMidiNoteCollisionTarget(
    MidiSegment Segment,
    long Tick,
    int Key);

internal readonly record struct LogicalNoteCollisionTarget(
    Segment Segment,
    long Tick,
    int Key);

internal readonly record struct TemplateNoteCollisionTarget(
    SubVoice SubVoice,
    long Tick,
    int Key);

internal readonly record struct DirectMidiEventCollisionTarget(
    MidiSegment Segment,
    long Tick,
    DirectMidiChannelEventKind Kind,
    int Data1);

/// <summary>
/// Resolves exact timeline-key collisions at the Project edit transaction boundary.
/// Logical/Template/Direct MIDI Notes keep the existing occupant at an exact
/// start-tick/key. Logical Parameter, Template MIDI, and Direct MIDI event points
/// instead keep the last newcomer from the current edit, so a moved or newly drawn
/// point replaces the former value.
/// The policy deliberately does not treat duration overlap at different start ticks
/// as a collision and does not clean unrelated pre-existing damage.
/// </summary>
internal static class ExactTimelineCollisionPolicy
{
    public static IPreparedProjectEdit Scope(
        IPreparedProjectEdit source,
        IEnumerable<Segment>? logicalNoteSegments = null,
        IEnumerable<LogicalParameterLane>? logicalParameterLanes = null,
        IEnumerable<SubVoice>? subVoices = null,
        IEnumerable<ValueCurve>? valueCurves = null,
        IEnumerable<MidiSegment>? directMidiSegments = null,
        IEnumerable<LogicalNoteCollisionTarget>? logicalNoteTargets = null,
        IEnumerable<TemplateNoteCollisionTarget>? templateNoteTargets = null,
        IEnumerable<DirectMidiNoteCollisionTarget>? directMidiNoteTargets = null,
        IEnumerable<DirectMidiEventCollisionTarget>? directMidiEventTargets = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        CollisionScopeSet additions = new(
            (logicalNoteSegments ?? []).Distinct().ToArray(),
            (logicalParameterLanes ?? []).Distinct().ToArray(),
            (subVoices ?? []).Distinct().ToArray(),
            (valueCurves ?? []).Distinct().ToArray(),
            (directMidiSegments ?? []).Distinct().ToArray(),
            (logicalNoteTargets ?? []).Distinct().ToArray(),
            (templateNoteTargets ?? []).Distinct().ToArray(),
            (directMidiNoteTargets ?? []).Distinct().ToArray(),
            (directMidiEventTargets ?? []).Distinct().ToArray());
        if (additions.IsEmpty) return source;
        if (source is CollisionScopedPreparedEdit existing)
        {
            return new CollisionScopedPreparedEdit(
                existing.Source,
                CollisionScopeSet.Merge(existing.Scopes, additions));
        }
        return new CollisionScopedPreparedEdit(source, additions);
    }

    public static IPreparedProjectEdit Wrap(
        MidoraProject project,
        IPreparedProjectEdit source)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(source);
        if (!source.HasChanges || source is not CollisionScopedPreparedEdit scoped)
        {
            return source;
        }

        CollisionBaseline baseline = CaptureBaseline(scoped.Scopes);
        return new CollisionResolvingPreparedEdit(source, baseline, scoped.Scopes);
    }

    public static IPreparedProjectEdit CombineScopes(
        IPreparedProjectEdit source,
        IEnumerable<IPreparedProjectEdit> children)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(children);
        CollisionScopeSet? combined = null;
        foreach (IPreparedProjectEdit child in children)
        {
            if (child is not CollisionScopedPreparedEdit scoped) continue;
            combined = combined is null
                ? scoped.Scopes
                : CollisionScopeSet.Merge(combined, scoped.Scopes);
        }
        return combined is null ? source : new CollisionScopedPreparedEdit(source, combined);
    }

    private static CollisionBaseline CaptureBaseline(CollisionScopeSet scopes)
    {
        Dictionary<MidoraId, HashSet<CollisionKey>> keysById = [];
        foreach (CollisionCandidate candidate in EnumerateCandidates(scopes))
        {
            if (!keysById.TryGetValue(candidate.Id, out HashSet<CollisionKey>? keys))
            {
                keys = [];
                keysById.Add(candidate.Id, keys);
            }
            keys.UnionWith(candidate.Keys);
        }
        return new(keysById);
    }

    private static IReadOnlyList<CollisionRemoval> Resolve(
        CollisionScopeSet scopes,
        CollisionBaseline baseline)
    {
        CollisionCandidate[] candidates = EnumerateCandidates(scopes).ToArray();
        HashSet<CollisionCandidate> discarded = [];
        foreach (IGrouping<CollisionKey, CollisionCandidate> group in candidates
            .SelectMany(candidate => candidate.Keys.Select(key => (key, candidate)))
            .GroupBy(value => value.key, value => value.candidate))
        {
            CollisionCandidate[] occupants = group
                .Where(candidate => !discarded.Contains(candidate))
                .OrderBy(candidate => candidate.Order)
                .ToArray();
            if (occupants.Length < 2)
            {
                continue;
            }

            HashSet<CollisionCandidate> incumbents = occupants
                .Where(candidate => baseline.Contains(candidate.Id, group.Key))
                .ToHashSet();
            if (UsesLaterPointEditWins(group.Key.Scope))
            {
                CollisionCandidate[] newcomers = occupants
                    .Where(candidate => !baseline.Contains(candidate.Id, group.Key))
                    .ToArray();
                if (newcomers.Length == 0)
                {
                    // Do not turn an unrelated pre-existing collision into an
                    // implicit cleanup edit.
                    continue;
                }

                CollisionCandidate pointWinner = newcomers[^1];
                foreach (CollisionCandidate candidate in occupants)
                {
                    if (!ReferenceEquals(candidate, pointWinner))
                    {
                        discarded.Add(candidate);
                    }
                }
                continue;
            }

            CollisionCandidate winner = incumbents.Count != 0
                ? occupants.First(incumbents.Contains)
                : occupants[0];
            foreach (CollisionCandidate candidate in occupants)
            {
                if (!ReferenceEquals(candidate, winner)
                    && !incumbents.Contains(candidate))
                {
                    discarded.Add(candidate);
                }
            }
        }

        CollisionRemoval[] removals = discarded
            .OrderByDescending(candidate => candidate.Order)
            .Select(candidate => candidate.Remove())
            .ToArray();
        return removals;
    }

    private static bool UsesLaterPointEditWins(CollisionScope scope) =>
        scope is CollisionScope.LogicalParameterPoint
            or CollisionScope.TemplateEventPoint
            or CollisionScope.DirectMidiEventPoint;

    private static IEnumerable<CollisionCandidate> EnumerateCandidates(CollisionScopeSet scopes)
    {
        long order = 0;
        foreach (Segment segment in scopes.LogicalNoteSegments.OrderBy(static value => value.Id))
        {
            for (int index = 0; index < segment.Notes.Count; index++)
            {
                LogicalNote note = segment.Notes[index];
                yield return CollisionCandidate.ForList(
                    note.Id,
                    [new(CollisionScope.LogicalNote, segment.Id, note.StartTick, note.Note)],
                    order++,
                    segment.Notes,
                    note,
                    "Logical Note");
            }
        }

        foreach (LogicalParameterLane lane in scopes.LogicalParameterLanes
            .OrderBy(static value => value.Id))
        {
            for (int index = 0; index < lane.Points.Count; index++)
            {
                CurvePoint point = lane.Points[index];
                yield return CollisionCandidate.ForList(
                    point.Id,
                    [new(CollisionScope.LogicalParameterPoint, lane.Id, point.Tick, 0)],
                    order++,
                    lane.Points,
                    point,
                    "Logical Parameter point");
            }
        }

        foreach (SubVoice voice in scopes.SubVoices.OrderBy(static value => value.Id))
        {
            for (int index = 0; index < voice.Events.Count; index++)
            {
                TemplateEvent value = voice.Events[index];
                CollisionKey[] keys = value.Kind == TemplateEventKind.Note
                    ? [new(CollisionScope.TemplateNote, voice.Id, value.Tick, value.Number)]
                    : TemplateEventExactCollision.GetNonNoteDetails(
                            value.Kind,
                            value.Number,
                            value.HasBankMsb,
                            value.HasBankLsb)
                        .Select(detail => new CollisionKey(
                            CollisionScope.TemplateEventPoint,
                            voice.Id,
                            value.Tick,
                            detail))
                        .ToArray();
                yield return CollisionCandidate.ForCollection(
                    value.Id,
                    keys,
                    order++,
                    voice.Events,
                    value,
                    "Template Event");
            }
        }

        HashSet<Segment> fullLogicalNoteSegments = scopes.LogicalNoteSegments.ToHashSet();
        foreach (IGrouping<Segment, LogicalNoteCollisionTarget> group in
            scopes.LogicalNoteTargets
                .Where(target => !fullLogicalNoteSegments.Contains(target.Segment))
                .GroupBy(static target => target.Segment)
                .OrderBy(static group => group.Key.Id))
        {
            HashSet<(long Tick, int Key)> keys = group
                .Select(static target => (target.Tick, target.Key))
                .ToHashSet();
            foreach (LogicalNote note in group.Key.Notes)
            {
                if (!keys.Contains((note.StartTick, note.Note))) continue;
                yield return CollisionCandidate.ForList(
                    note.Id,
                    [new(CollisionScope.LogicalNote, group.Key.Id, note.StartTick, note.Note)],
                    order++,
                    group.Key.Notes,
                    note,
                    "Logical Note");
            }
        }

        HashSet<SubVoice> fullSubVoices = scopes.SubVoices.ToHashSet();
        foreach (IGrouping<SubVoice, TemplateNoteCollisionTarget> group in
            scopes.TemplateNoteTargets
                .Where(target => !fullSubVoices.Contains(target.SubVoice))
                .GroupBy(static target => target.SubVoice)
                .OrderBy(static group => group.Key.Id))
        {
            HashSet<(long Tick, int Key)> keys = group
                .Select(static target => (target.Tick, target.Key))
                .ToHashSet();
            foreach (TemplateEvent value in group.Key.Events)
            {
                if (value.Kind != TemplateEventKind.Note
                    || !keys.Contains((value.Tick, value.Number)))
                {
                    continue;
                }
                yield return CollisionCandidate.ForCollection(
                    value.Id,
                    [new(CollisionScope.TemplateNote, group.Key.Id, value.Tick, value.Number)],
                    order++,
                    group.Key.Events,
                    value,
                    "Template Note");
            }
        }

        foreach (ValueCurve curve in scopes.ValueCurves.OrderBy(static value => value.Id))
        {
            for (int index = 0; index < curve.Points.Count; index++)
            {
                CurvePoint point = curve.Points[index];
                yield return CollisionCandidate.ForList(
                    point.Id,
                    [new(CollisionScope.ValueCurvePoint, curve.Id, point.Tick, 0)],
                    order++,
                    curve.Points,
                    point,
                    "Value Curve point");
            }
        }

        foreach (MidiSegment segment in scopes.DirectMidiSegments.OrderBy(static value => value.Id))
        {
            for (int index = 0; index < segment.Notes.Count; index++)
            {
                DirectMidiNote note = segment.Notes[index];
                yield return CollisionCandidate.ForList(
                    note.Id,
                    [new(CollisionScope.DirectMidiNote, segment.Id, note.StartTick, note.Key)],
                    order++,
                    segment.Notes,
                    note,
                    "Direct MIDI Note");
            }
            for (int index = 0; index < segment.ChannelEvents.Count; index++)
            {
                DirectMidiChannelEvent value = segment.ChannelEvents[index];
                int selector = DirectMidiEventUsesData1Selector(value.Kind)
                    ? value.Data1 + 1
                    : 0;
                int detail = ((int)value.Kind << 16) | selector;
                yield return CollisionCandidate.ForList(
                    value.Id,
                    [new(CollisionScope.DirectMidiEventPoint, segment.Id, value.Tick, detail)],
                    order++,
                    segment.ChannelEvents,
                    value,
                    "Direct MIDI Event");
            }
        }

        HashSet<MidiSegment> fullDirectMidiSegments = scopes.DirectMidiSegments.ToHashSet();
        foreach (IGrouping<MidiSegment, DirectMidiNoteCollisionTarget> group in
            scopes.DirectMidiNoteTargets
                .Where(target => !fullDirectMidiSegments.Contains(target.Segment))
                .GroupBy(static target => target.Segment)
                .OrderBy(static group => group.Key.Id))
        {
            HashSet<DirectMidiNoteStartKey> keys = group
                .Select(static target => new DirectMidiNoteStartKey(target.Tick, target.Key))
                .ToHashSet();
            foreach (DirectMidiNote note in group.Key.Notes.QueryStartKeys(keys))
            {
                yield return CollisionCandidate.ForDirectMidiNote(
                    note.Id,
                    [new(CollisionScope.DirectMidiNote, group.Key.Id, note.StartTick, note.Key)],
                    order++,
                    group.Key.Notes,
                    note);
            }
        }

        foreach (IGrouping<MidiSegment, DirectMidiEventCollisionTarget> group in
            scopes.DirectMidiEventTargets
                .Where(target => !fullDirectMidiSegments.Contains(target.Segment))
                .GroupBy(static target => target.Segment)
                .OrderBy(static group => group.Key.Id))
        {
            HashSet<DirectMidiEventStartKey> keys = group.Select(target => new DirectMidiEventStartKey(
                    target.Tick,
                    target.Kind,
                    DirectMidiEventUsesData1Selector(target.Kind) ? target.Data1 : 0))
                .ToHashSet();
            foreach (DirectMidiChannelEvent value in group.Key.ChannelEvents.QueryStartKeys(keys))
            {
                int selector = DirectMidiEventUsesData1Selector(value.Kind)
                    ? value.Data1 + 1
                    : 0;
                int detail = ((int)value.Kind << 16) | selector;
                yield return CollisionCandidate.ForDirectMidiEvent(
                    value.Id,
                    [new(CollisionScope.DirectMidiEventPoint, group.Key.Id, value.Tick, detail)],
                    order++,
                    group.Key.ChannelEvents,
                    value);
            }
        }
    }

    private static bool DirectMidiEventUsesData1Selector(
        DirectMidiChannelEventKind kind) =>
        kind is DirectMidiChannelEventKind.ControlChange
            or DirectMidiChannelEventKind.PolyphonicKeyPressure
            or DirectMidiChannelEventKind.NoteOn
            or DirectMidiChannelEventKind.NoteOff;

    private sealed class CollisionResolvingPreparedEdit(
        IPreparedProjectEdit source,
        CollisionBaseline baseline,
        CollisionScopeSet scopes) : IPreparedProjectEdit
    {
        private IReadOnlyList<CollisionRemoval>? _lastRemovals;
        private bool _applyStarted;

        public bool HasChanges => source.HasChanges;
        public ProjectChangeSet Changes => source.Changes;

        public void Apply(MidoraProject project)
        {
            _applyStarted = true;
            _lastRemovals = [];
            source.Apply(project);
            _lastRemovals = Resolve(scopes, baseline);
        }

        public void Undo(MidoraProject project)
        {
            if (!_applyStarted || _lastRemovals is null)
            {
                throw new InvalidOperationException(
                    "An exact-collision edit cannot be undone before Apply.");
            }
            foreach (CollisionRemoval removal in _lastRemovals.Reverse())
            {
                removal.Restore();
            }
            source.Undo(project);
            _lastRemovals = null;
            _applyStarted = false;
        }
    }

    private sealed record CollisionScopedPreparedEdit(
        IPreparedProjectEdit Source,
        CollisionScopeSet Scopes) : IPreparedProjectEdit
    {
        public bool HasChanges => Source.HasChanges;
        public ProjectChangeSet Changes => Source.Changes;
        public void Apply(MidoraProject project) => Source.Apply(project);
        public void Undo(MidoraProject project) => Source.Undo(project);
    }

    private sealed record CollisionScopeSet(
        IReadOnlyCollection<Segment> LogicalNoteSegments,
        IReadOnlyCollection<LogicalParameterLane> LogicalParameterLanes,
        IReadOnlyCollection<SubVoice> SubVoices,
        IReadOnlyCollection<ValueCurve> ValueCurves,
        IReadOnlyCollection<MidiSegment> DirectMidiSegments,
        IReadOnlyCollection<LogicalNoteCollisionTarget> LogicalNoteTargets,
        IReadOnlyCollection<TemplateNoteCollisionTarget> TemplateNoteTargets,
        IReadOnlyCollection<DirectMidiNoteCollisionTarget> DirectMidiNoteTargets,
        IReadOnlyCollection<DirectMidiEventCollisionTarget> DirectMidiEventTargets)
    {
        public bool IsEmpty => LogicalNoteSegments.Count == 0
            && LogicalParameterLanes.Count == 0
            && SubVoices.Count == 0
            && ValueCurves.Count == 0
            && DirectMidiSegments.Count == 0
            && LogicalNoteTargets.Count == 0
            && TemplateNoteTargets.Count == 0
            && DirectMidiNoteTargets.Count == 0
            && DirectMidiEventTargets.Count == 0;

        public static CollisionScopeSet Merge(CollisionScopeSet left, CollisionScopeSet right) =>
            new(
                left.LogicalNoteSegments.Concat(right.LogicalNoteSegments).Distinct().ToArray(),
                left.LogicalParameterLanes.Concat(right.LogicalParameterLanes).Distinct().ToArray(),
                left.SubVoices.Concat(right.SubVoices).Distinct().ToArray(),
                left.ValueCurves.Concat(right.ValueCurves).Distinct().ToArray(),
                left.DirectMidiSegments.Concat(right.DirectMidiSegments).Distinct().ToArray(),
                left.LogicalNoteTargets.Concat(right.LogicalNoteTargets).Distinct().ToArray(),
                left.TemplateNoteTargets.Concat(right.TemplateNoteTargets).Distinct().ToArray(),
                left.DirectMidiNoteTargets.Concat(right.DirectMidiNoteTargets).Distinct().ToArray(),
                left.DirectMidiEventTargets.Concat(right.DirectMidiEventTargets).Distinct().ToArray());
    }

    private sealed record CollisionBaseline(
        IReadOnlyDictionary<MidoraId, HashSet<CollisionKey>> KeysById)
    {
        public bool Contains(MidoraId id, CollisionKey key) =>
            KeysById.TryGetValue(id, out HashSet<CollisionKey>? keys)
            && keys.Contains(key);
    }

    private sealed class CollisionCandidate(
        MidoraId id,
        IReadOnlyList<CollisionKey> keys,
        long order,
        Func<CollisionRemoval> remove)
    {
        public MidoraId Id { get; } = id;
        public IReadOnlyList<CollisionKey> Keys { get; } = keys;
        public long Order { get; } = order;
        public CollisionRemoval Remove() => remove();

        public static CollisionCandidate ForList<T>(
            MidoraId id,
            IReadOnlyList<CollisionKey> keys,
            long order,
            IList<T> values,
            T value,
            string name)
            where T : class =>
            new(id, keys, order, () =>
            {
                int index = values.IndexOf(value);
                if (index < 0)
                {
                    throw new InvalidOperationException($"The conflicting {name} is no longer present.");
                }
                values.RemoveAt(index);
                return new(
                    () =>
                    {
                        if (values.Contains(value))
                        {
                            throw new InvalidOperationException($"The conflicting {name} is already restored.");
                        }
                        values.Insert(Math.Clamp(index, 0, values.Count), value);
                    });
            });

        public static CollisionCandidate ForCollection<T>(
            MidoraId id,
            IReadOnlyList<CollisionKey> keys,
            long order,
            Collection<T> values,
            T value,
            string name)
            where T : class =>
            new(id, keys, order, () =>
            {
                int index = values.IndexOf(value);
                if (index < 0)
                {
                    throw new InvalidOperationException($"The conflicting {name} is no longer present.");
                }
                values.RemoveAt(index);
                return new(
                    () =>
                    {
                        if (values.Contains(value))
                        {
                            throw new InvalidOperationException($"The conflicting {name} is already restored.");
                        }
                        values.Insert(Math.Clamp(index, 0, values.Count), value);
                    });
            });

        public static CollisionCandidate ForDirectMidiNote(
            MidoraId id,
            IReadOnlyList<CollisionKey> keys,
            long order,
            DirectMidiNoteCollection values,
            DirectMidiNote value) =>
            new(id, keys, order, () => new(values.RemoveForExactCollision(value)));

        public static CollisionCandidate ForDirectMidiEvent(
            MidoraId id,
            IReadOnlyList<CollisionKey> keys,
            long order,
            DirectMidiChannelEventCollection values,
            DirectMidiChannelEvent value) =>
            new(id, keys, order, () => new(values.RemoveForExactCollision(value)));
    }

    private sealed record CollisionRemoval(Action Restore);

    private readonly record struct CollisionKey(
        CollisionScope Scope,
        MidoraId OwnerId,
        long Tick,
        int Detail);

    private enum CollisionScope
    {
        LogicalNote,
        LogicalParameterPoint,
        TemplateNote,
        TemplateEventPoint,
        ValueCurvePoint,
        DirectMidiNote,
        DirectMidiEventPoint
    }

}
