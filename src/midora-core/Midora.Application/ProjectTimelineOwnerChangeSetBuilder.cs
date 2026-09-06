using System.Collections.Immutable;
using Midora.Domain;

namespace Midora.Application;

/// <summary>
/// Builds Stage 4 timeline receipts from the already planned object set.  It
/// resolves only candidate Stable IDs through the old/new source indexes; it
/// never enumerates the complete owner merely to discover an invalidation
/// footprint.
/// </summary>
internal static class ProjectTimelineOwnerChangeSetBuilder
{
    public static void AddLogicalNotes(
        ProjectChangeSet changes,
        Segment previous,
        Segment current,
        IEnumerable<MidoraId> candidateIds) => Add(
            changes,
            previous.Id,
            ProjectTimelineOwnerKind.LogicalSegment,
            ProjectTimelineSourceKind.LogicalNotes,
            laneOrCurveId: null,
            previous.Notes.Generation,
            current.Notes.Generation,
            ResolveLogicalNotes(previous.Notes, candidateIds),
            ResolveLogicalNotes(current.Notes, candidateIds));

    public static void AddDirectNotes(
        ProjectChangeSet changes,
        MidiSegment previous,
        MidiSegment current,
        IEnumerable<MidoraId> candidateIds) => Add(
            changes,
            previous.Id,
            ProjectTimelineOwnerKind.DirectMidiSegment,
            ProjectTimelineSourceKind.DirectMidiNotes,
            laneOrCurveId: null,
            previous.Notes.Generation,
            current.Notes.Generation,
            ResolveDirectNotes(previous.Notes, candidateIds),
            ResolveDirectNotes(current.Notes, candidateIds));

    public static void AddSubVoiceEvents(
        ProjectChangeSet changes,
        SubVoice previous,
        SubVoice current,
        IEnumerable<MidoraId> candidateIds) => Add(
            changes,
            previous.Id,
            ProjectTimelineOwnerKind.SubVoice,
            ProjectTimelineSourceKind.SubVoiceEvents,
            laneOrCurveId: null,
            previous.Events.Generation,
            current.Events.Generation,
            ResolveTemplateEvents(previous.Events, candidateIds),
            ResolveTemplateEvents(current.Events, candidateIds));

    public static void AddLogicalParameterPoints(
        ProjectChangeSet changes,
        MidoraId ownerId,
        LogicalParameterLane previous,
        LogicalParameterLane current,
        IEnumerable<MidoraId> candidateIds) => Add(
            changes,
            ownerId,
            ProjectTimelineOwnerKind.LogicalSegment,
            ProjectTimelineSourceKind.LogicalParameterPoints,
            previous.Id,
            previous.Points.Generation,
            current.Points.Generation,
            ResolveCurvePoints(previous.Points, candidateIds),
            ResolveCurvePoints(current.Points, candidateIds));

    public static void AddDirectEvents(
        ProjectChangeSet changes,
        MidiSegment previous,
        MidiSegment current,
        IEnumerable<MidoraId> candidateIds) => Add(
            changes,
            previous.Id,
            ProjectTimelineOwnerKind.DirectMidiSegment,
            ProjectTimelineSourceKind.DirectMidiChannelEvents,
            laneOrCurveId: null,
            previous.ChannelEvents.Generation,
            current.ChannelEvents.Generation,
            ResolveDirectEvents(previous.ChannelEvents, candidateIds),
            ResolveDirectEvents(current.ChannelEvents, candidateIds));

    private static IReadOnlyList<ResolvedValue<LogicalNoteSnapshotValue>> ResolveLogicalNotes(
        LogicalNoteCollection source,
        IEnumerable<MidoraId> candidateIds)
    {
        MidoraId[] ids = FreezeIds(candidateIds);
        return source.ResolveByIdsWithIndicesInCollectionOrder(ids)
            .Select(static match => new ResolvedValue<LogicalNoteSnapshotValue>(
                match.Value.Id,
                match.Index,
                new(
                    match.Value.Id,
                    match.Value.StartTick,
                    match.Value.LengthTicks,
                    match.Value.Note,
                    match.Value.Velocity),
                NoteRange(match.Value.StartTick, match.Value.LengthTicks, match.Value.Note)))
            .ToArray();
    }

    private static IReadOnlyList<ResolvedValue<DirectMidiNoteValue>> ResolveDirectNotes(
        DirectMidiNoteCollection source,
        IEnumerable<MidoraId> candidateIds)
    {
        MidoraId[] ids = FreezeIds(candidateIds);
        return source.ResolveByIds(ids)
            .Select(static match => new ResolvedValue<DirectMidiNoteValue>(
                match.Value.Id,
                match.Index,
                new(
                    match.Value.Id,
                    match.Value.StartTick,
                    match.Value.LengthTicks,
                    match.Value.Key,
                    match.Value.NoteOnVelocity,
                    match.Value.NoteOffVelocity,
                    match.Value.NoteOnOrder,
                    match.Value.NoteOffOrder),
                NoteRange(match.Value.StartTick, match.Value.LengthTicks, match.Value.Key)))
            .ToArray();
    }

    private static IReadOnlyList<ResolvedValue<TemplateEventSnapshotValue>> ResolveTemplateEvents(
        TemplateEventCollection source,
        IEnumerable<MidoraId> candidateIds)
    {
        MidoraId[] ids = FreezeIds(candidateIds);
        return source.ResolveByIdsWithIndicesInCollectionOrder(ids)
            .Select(static match => new ResolvedValue<TemplateEventSnapshotValue>(
                match.Value.Id,
                match.Index,
                Snapshot(match.Value),
                match.Value.Kind == TemplateEventKind.Note
                    ? NoteRange(match.Value.Tick, match.Value.LengthTicks, match.Value.Number)
                    : PointRange(match.Value.Tick, TemplateEventLane(match.Value))))
            .ToArray();
    }

    private static IReadOnlyList<ResolvedValue<CurvePointSnapshotValue>> ResolveCurvePoints(
        CurvePointCollection source,
        IEnumerable<MidoraId> candidateIds)
    {
        MidoraId[] ids = FreezeIds(candidateIds);
        return source.ResolveByIdsWithIndicesInCollectionOrder(ids)
            .Select(static match => new ResolvedValue<CurvePointSnapshotValue>(
                match.Value.Id,
                match.Index,
                new(
                    match.Value.Id,
                    match.Value.Tick,
                    match.Value.Value,
                    match.Value.Interpolation),
                PointRange(match.Value.Tick, 0)))
            .ToArray();
    }

    private static IReadOnlyList<ResolvedValue<DirectMidiChannelEventValue>> ResolveDirectEvents(
        DirectMidiChannelEventCollection source,
        IEnumerable<MidoraId> candidateIds)
    {
        MidoraId[] ids = FreezeIds(candidateIds);
        return source.ResolveByIds(ids)
            .Select(static match => new ResolvedValue<DirectMidiChannelEventValue>(
                match.Value.Id,
                match.Index,
                new(
                    match.Value.Id,
                    match.Value.Tick,
                    match.Value.Kind,
                    match.Value.Data1,
                    match.Value.Data2,
                    match.Value.Order),
                PointRange(match.Value.Tick, DirectEventLane(match.Value))))
            .ToArray();
    }

    private static int DirectEventLane(DirectMidiChannelEvent value)
    {
        int selector = value.Kind is DirectMidiChannelEventKind.PolyphonicKeyPressure
            or DirectMidiChannelEventKind.ControlChange
                ? value.Data1
                : 0;
        return checked(((int)value.Kind * 256) + selector);
    }

    private static int TemplateEventLane(TemplateEvent value)
    {
        int selector = value.Kind is TemplateEventKind.ControlChange
            or TemplateEventKind.RegisteredParameter
            or TemplateEventKind.NonRegisteredParameter
                ? value.Number
                : 0;
        return checked(((int)value.Kind * 256) + selector);
    }

    private static TemplateEventSnapshotValue Snapshot(TemplateEvent value) => new(
        value.Id,
        value.Kind,
        value.Tick,
        value.LengthTicks,
        value.Number,
        value.Value,
        value.SecondaryValue,
        value.HasBankMsb,
        value.HasBankLsb,
        value.FollowPitchDelta);

    private static void Add<TValue>(
        ProjectChangeSet changes,
        MidoraId ownerId,
        ProjectTimelineOwnerKind ownerKind,
        ProjectTimelineSourceKind sourceKind,
        MidoraId? laneOrCurveId,
        long previousRevision,
        long currentRevision,
        IReadOnlyList<ResolvedValue<TValue>> previous,
        IReadOnlyList<ResolvedValue<TValue>> current)
        where TValue : struct
    {
        Dictionary<MidoraId, ResolvedValue<TValue>> previousById =
            previous.ToDictionary(static value => value.Id);
        Dictionary<MidoraId, ResolvedValue<TValue>> currentById =
            current.ToDictionary(static value => value.Id);
        HashSet<MidoraId> changedIds = [.. previousById.Keys];
        changedIds.UnionWith(currentById.Keys);
        changedIds.RemoveWhere(id =>
            previousById.TryGetValue(id, out ResolvedValue<TValue> oldValue)
            && currentById.TryGetValue(id, out ResolvedValue<TValue> newValue)
            && EqualityComparer<TValue>.Default.Equals(oldValue.Value, newValue.Value));
        if (changedIds.Count == 0) return;

        ImmutableArray<TimelineOrdinalRange> previousOrdinals = CompressOrdinals(
            changedIds.Where(previousById.ContainsKey).Select(id => previousById[id].Ordinal));
        ImmutableArray<TimelineOrdinalRange> currentOrdinals = CompressOrdinals(
            changedIds.Where(currentById.ContainsKey).Select(id => currentById[id].Ordinal));
        ImmutableArray<ProjectTimelineContentChangeRange> previousRanges = MergeRanges(
            changedIds.Where(previousById.ContainsKey).Select(id => previousById[id].Range));
        ImmutableArray<ProjectTimelineContentChangeRange> currentRanges = MergeRanges(
            changedIds.Where(currentById.ContainsKey).Select(id => currentById[id].Range));
        ImmutableArray<ProjectTimelineContentChangeRange> invalidationRanges = MergeRanges(
            previousRanges.Concat(currentRanges));

        ProjectTimelineSourceChange source = new(
            sourceKind,
            laneOrCurveId,
            previousRevision,
            currentRevision,
            previousOrdinals,
            PageIndices: [],
            invalidationRanges)
        {
            PreviousContentRanges = previousRanges,
            CurrentContentRanges = currentRanges,
            CurrentOrdinalRanges = currentOrdinals,
            CurrentPageIndices = []
        };
        Append(changes, ownerId, ownerKind, source);
    }

    private static void Append(
        ProjectChangeSet changes,
        MidoraId ownerId,
        ProjectTimelineOwnerKind ownerKind,
        ProjectTimelineSourceChange source)
    {
        for (int index = 0; index < changes.TimelineOwnerChanges.Count; index++)
        {
            ProjectTimelineOwnerChangeSet existing = changes.TimelineOwnerChanges[index];
            if (existing.OwnerId != ownerId || existing.OwnerKind != ownerKind) continue;
            changes.TimelineOwnerChanges[index] = existing with
            {
                Sources = existing.Sources.Add(source)
            };
            return;
        }
        changes.TimelineOwnerChanges.Add(new(ownerId, ownerKind, [source]));
    }

    private static MidoraId[] FreezeIds(IEnumerable<MidoraId> ids) => ids
        .Where(static id => id != default)
        .Distinct()
        .ToArray();

    private static ImmutableArray<TimelineOrdinalRange> CompressOrdinals(
        IEnumerable<int> ordinals)
    {
        int[] ordered = ordinals.Distinct().Order().ToArray();
        if (ordered.Length == 0) return [];
        ImmutableArray<TimelineOrdinalRange>.Builder result = ImmutableArray.CreateBuilder<TimelineOrdinalRange>();
        int first = ordered[0];
        int end = checked(first + 1);
        for (int index = 1; index < ordered.Length; index++)
        {
            int ordinal = ordered[index];
            if (ordinal == end)
            {
                end++;
                continue;
            }
            result.Add(new(first, end - first));
            first = ordinal;
            end = checked(ordinal + 1);
        }
        result.Add(new(first, end - first));
        return result.ToImmutable();
    }

    private static ImmutableArray<ProjectTimelineContentChangeRange> MergeRanges(
        IEnumerable<ProjectTimelineContentChangeRange> ranges)
    {
        ProjectTimelineContentChangeRange[] ordered = ranges
            .Distinct()
            .OrderBy(static value => value.MinimumLane)
            .ThenBy(static value => value.MaximumLane)
            .ThenBy(static value => value.StartTick)
            .ThenBy(static value => value.EndTick)
            .ToArray();
        if (ordered.Length == 0) return [];
        ImmutableArray<ProjectTimelineContentChangeRange>.Builder result =
            ImmutableArray.CreateBuilder<ProjectTimelineContentChangeRange>();
        ProjectTimelineContentChangeRange current = ordered[0];
        for (int index = 1; index < ordered.Length; index++)
        {
            ProjectTimelineContentChangeRange next = ordered[index];
            if (next.MinimumLane == current.MinimumLane
                && next.MaximumLane == current.MaximumLane
                && next.StartTick <= current.EndTick)
            {
                current = new(
                    current.StartTick,
                    Math.Max(current.EndTick, next.EndTick),
                    current.MinimumLane,
                    current.MaximumLane);
                continue;
            }
            result.Add(current);
            current = next;
        }
        result.Add(current);
        return result.ToImmutable();
    }

    private static ProjectTimelineContentChangeRange NoteRange(
        long startTick,
        long lengthTicks,
        int lane) => new(
            startTick,
            SaturatingEnd(startTick, Math.Max(1, lengthTicks)),
            lane,
            lane);

    private static ProjectTimelineContentChangeRange PointRange(long tick, int lane)
    {
        if (tick == long.MaxValue)
            throw new InvalidOperationException("A timeline point at Int64.MaxValue has no representable half-open footprint.");
        return new(tick, tick + 1, lane, lane);
    }

    private static long SaturatingEnd(long startTick, long lengthTicks) =>
        lengthTicks > 0 && startTick <= long.MaxValue - lengthTicks
            ? startTick + lengthTicks
            : long.MaxValue;

    private readonly record struct ResolvedValue<TValue>(
        MidoraId Id,
        int Ordinal,
        TValue Value,
        ProjectTimelineContentChangeRange Range)
        where TValue : struct;
}
