using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    // Insert three explicit MIDI messages before the old same-tick stream.
    // Order is a source value, never an inference from Stable ID allocation.
    // All patches use bounded stores; even a dense tick does not materialize
    // an array of notes, messages or payloads.
    private static IProjectEditCommand ReserveInstrumentChangeOrder(MidoraId segmentId, long tick) =>
        Command("Reserve instrument change order", original =>
        {
        using var orderScope = BulkEditPreparationContext.Enter(project: original);
        var originalOwner = FindMidiSegment(original, segmentId).Segment;
        // Only the exceptional Int64 order boundary needs an order-rank map.
        // Ranking equal orders equally also preserves every existing tie-break.
        bool rebase = Orders().Any(value => value > long.MaxValue - 3);
        using var sorted = rebase ? BoundedEditSort.Sort(Orders(), Comparer<long>.Default, orderScope.Resources, orderScope.Token) : null;
        using var ranks = sorted is null ? null : new BoundedImmutableValueSource<long>(sorted);
        long Shift(long value)
        {
            if (ranks is null) return checked(value + 3);
            int lo = 0, hi = ranks.Count;
            while (lo < hi) { int mid = lo + (hi - lo) / 2; if (ranks[mid] < value) lo = mid + 1; else hi = mid; }
            return lo + 3L;
        }
        IEnumerable<long> Orders()
        {
            var notes = originalOwner.Notes.CreateQuerySnapshot();
            foreach (var value in notes.QueryStartValues(tick, tick + 1)) { orderScope.Token.ThrowIfCancellationRequested(); yield return value.NoteOnOrder; }
            foreach (var value in notes.QueryEndValues(tick, tick + 1)) { orderScope.Token.ThrowIfCancellationRequested(); yield return value.NoteOffOrder; }
            foreach (var value in originalOwner.ChannelEvents.CreateQuerySnapshot().QueryValues(tick, tick + 1))
            { orderScope.Token.ThrowIfCancellationRequested(); yield return value.Order; }
            foreach (var value in originalOwner.OpaqueEvents.CreateQuerySnapshot().QueryValues(tick, tick + 1))
            { orderScope.Token.ThrowIfCancellationRequested(); yield return value.Order; }
        }
        return new SequentialProjectEditCommand("Reserve instrument change order",
        [
            _ => Command("Rebase note order", project =>
            {
                using var scope = BulkEditPreparationContext.Enter(project: project);
                var location = FindMidiSegment(project, segmentId);
                var stamp = ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
                var source = BoundedDirectMidiNoteSource.Capture(location.Segment.Notes);
                using var store = BoundedEditSort.Sort(Read(), BoundedDirectMidiNoteSource.OrdinalComparer,
                    scope.Resources, scope.Token);
                if (store.Count == 0) return Prepared(false, PureMidiTrackChange(location.Track.Id), _ => { }, _ => { });
                using var plan = new BoundedImmutableValueSource<BoundedDirectNoteDelta>(store);
                return PrepareBoundedDirectMidiNotes(project, location, source, plan, scope,
                    resolveCollisions: false, expectedSourceStamp: stamp);
                IEnumerable<BoundedDirectNoteDelta> Read()
                {
                    var endpoints = source.QueryNoteStarts(tick, checked(tick + 1))
                        .Concat(source.QueryNoteEnds(tick, checked(tick + 1)));
                    foreach (var item in source.ResolveValues(endpoints, scope.Token))
                    {
                        scope.Token.ThrowIfCancellationRequested();
                        var value = item.Value;
                        yield return item with { Value = value with
                        {
                            NoteOnOrder = value.StartTick == tick ? Shift(value.NoteOnOrder) : value.NoteOnOrder,
                            NoteOffOrder = checked(value.StartTick + value.LengthTicks) == tick
                                ? Shift(value.NoteOffOrder) : value.NoteOffOrder
                        }};
                    }
                }
            }),
            _ => Command("Rebase channel event order", project =>
            {
                using var scope = BulkEditPreparationContext.Enter(project: project);
                var location = FindMidiSegment(project, segmentId);
                var stamp = ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
                var source = BoundedDirectMidiEventSource.Capture(location.Segment.ChannelEvents);
                using var store = BoundedEditSort.Sort(Read(), BoundedDirectMidiEventSource.OrdinalComparer,
                    scope.Resources, scope.Token);
                if (store.Count == 0) return Prepared(false, PureMidiTrackChange(location.Track.Id), _ => { }, _ => { });
                using var plan = new BoundedImmutableValueSource<BoundedDirectEventDelta>(store);
                return PublishBoundedDirectMidiEvents(project, location, source, plan, scope,
                    BoundedEventCollisionMode.None, expectedSourceStamp: stamp);
                IEnumerable<BoundedDirectEventDelta> Read()
                {
                    foreach (var item in source.ResolveValues(source.QueryChannelEvents(tick, checked(tick + 1)), scope.Token))
                        yield return item with { Value = item.Value with { Order = Shift(item.Value.Order) } };
                }
            }),
            _ => Command("Rebase preserved event order", project =>
            {
                using var scope = BulkEditPreparationContext.Enter(project: project);
                var location = FindMidiSegment(project, segmentId);
                if (location.Segment.OpaqueEvents.Count == 0 || !location.Segment.OpaqueEvents.CreateQuerySnapshot().QueryValues(tick, tick + 1).Any())
                    return Prepared(false, PureMidiTrackChange(location.Track.Id), _ => { }, _ => { });
                var stamp = ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
                var source = BoundedOpaqueMidiSource.Capture(project, location.Segment.OpaqueEvents);
                using var store = BoundedEditSort.Sort(Read(), BoundedDirectMidiEventSource.OrdinalComparer,
                    scope.Resources, scope.Token);
                using var plan = new BoundedImmutableValueSource<BoundedDirectEventDelta>(store);
                return PublishBoundedOpaqueRoot(project, location, source, plan, scope, null, source.Metadata.FormalExtent,
                    null, stamp);
                IEnumerable<BoundedDirectEventDelta> Read()
                {
                    foreach (var item in source.Metadata.ResolveValues(
                        source.Metadata.QueryChannelEvents(tick, checked(tick + 1)), scope.Token))
                        yield return item with { Value = item.Value with { Order = Shift(item.Value.Order) } };
                }
            })
        ]).Prepare(original);
        });
}
