using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectObjectClipboard
{
    public static ProjectObjectClipboardPayload CopyLogicalParameterLane(
        ProjectDocumentSession document,
        MidoraId sourceSegmentId,
        MidoraId laneId)
    {
        using ClipboardCaptureScope capture = ClipboardCaptureScope.Enter();
        ArgumentNullException.ThrowIfNull(document);
        Segment segment = FindSegment(document.Project, sourceSegmentId).Segment;
        LogicalParameterLane lane = segment.ParameterLanes
            .SingleOrDefault(value => value.Id == laneId)
            ?? throw new ArgumentOutOfRangeException(nameof(laneId));
        IReadOnlyList<CurvePointClipboardSnapshot> points = SnapshotTimelinePoints(lane.Points);
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.LogicalParameterLane,
            1,
            "1 Logical Parameter Lane",
            new LogicalParameterLaneClipboardData(lane.ParameterId, points));
    }

    public static ProjectObjectClipboardPayload CopyLogicalParameterLaneContent(
        ProjectDocumentSession document,
        MidoraId sourceSegmentId,
        MidoraId laneId,
        IReadOnlyCollection<MidoraId> pointIds)
    {
        using ClipboardCaptureScope capture = ClipboardCaptureScope.Enter();
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pointIds);
        if (pointIds.Count == 0)
        {
            throw new ArgumentException(
                "At least one Logical Parameter point must be copied.",
                nameof(pointIds));
        }
        Segment segment = FindSegment(document.Project, sourceSegmentId).Segment;
        LogicalParameterLane lane = segment.ParameterLanes
            .SingleOrDefault(value => value.Id == laneId)
            ?? throw new ArgumentOutOfRangeException(nameof(laneId));
        IReadOnlySet<MidoraId> requested = ValidateDistinctIds(pointIds, nameof(pointIds));
        IReadOnlyList<CurvePointClipboardSnapshot> points = SnapshotTimelinePointValues(
            EnumerateClipboardSelection(document.Project, lane.Points.CreateQuerySnapshot(), requested));
        if (points.Count != requested.Count)
        {
            throw new ArgumentException(
                "Every copied Logical Parameter point must belong to the source Lane.",
                nameof(pointIds));
        }
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.LogicalParameterLaneContent,
            points.Count,
            points.Count == 1
                ? "1 Logical Parameter Point"
                : $"{points.Count} Logical Parameter Points",
            new LogicalParameterLaneContentClipboardData(lane.ParameterId, points));
    }

    public static ProjectObjectClipboardPayload CopyConductorEvents(
        ProjectDocumentSession document,
        IReadOnlyCollection<MidoraId> eventIds)
    {
        using ClipboardCaptureScope capture = ClipboardCaptureScope.Enter();
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(eventIds);
        if (eventIds.Count == 0)
        {
            throw new ArgumentException(
                "At least one ordinary Conductor event must be copied.",
                nameof(eventIds));
        }
        IReadOnlySet<MidoraId> requested = ValidateDistinctIds(eventIds, nameof(eventIds));
        ConductorClipboardList snapshots = ConductorClipboardList.Capture(
            document.Project.Conductor.Tempos.Where(value => requested.Contains(value.Id))
                .Select(value => new ConductorEventClipboardSnapshot(ConductorClipboardEventKind.Tempo,
                    value.Tick, value.BeatsPerMinute, 0, 0, false, null))
            .Concat(document.Project.Conductor.TimeSignatures.Where(value => requested.Contains(value.Id))
                .Select(value => new ConductorEventClipboardSnapshot(ConductorClipboardEventKind.TimeSignature,
                    value.Tick, 0, value.Numerator, value.Denominator, false, null)))
            .Concat(document.Project.Conductor.KeySignatures.Where(value => requested.Contains(value.Id))
                .Select(value => new ConductorEventClipboardSnapshot(ConductorClipboardEventKind.KeySignature,
                    value.Tick, 0, value.SharpsFlats, 0, value.IsMinor, null)))
            .Concat(document.Project.Conductor.Markers.Where(value => requested.Contains(value.Id))
                .Select(value => new ConductorEventClipboardSnapshot(ConductorClipboardEventKind.Marker,
                    value.Tick, 0, 0, 0, false, value.Name))));
        if (snapshots.Count != requested.Count)
            throw new ArgumentException(
                "Every copied ID must identify an ordinary Conductor event; the Project End Marker is excluded.", nameof(eventIds));
        return new(document.ClipboardSessionIdentity, ProjectObjectClipboardKind.ConductorEvents,
            snapshots.Count, snapshots.Count == 1 ? "1 Conductor Event" : $"{snapshots.Count} Conductor Events",
            new ConductorEventsClipboardData(snapshots));
    }

    public static IProjectEditCommand CreatePasteLogicalParameterLaneCommand(
        ProjectDocumentSession targetDocument,
        ProjectObjectClipboardPayload payload,
        MidoraId targetSegmentId,
        long editCursorTick)
    {
        LogicalParameterLaneClipboardData data =
            RequirePayload<LogicalParameterLaneClipboardData>(
                targetDocument,
                payload,
                ProjectObjectClipboardKind.LogicalParameterLane);
        return KeepClipboardAlive(payload, ProjectDomainEditCommands.PasteLogicalParameterLaneClipboard(
            data,
            targetSegmentId,
            editCursorTick), new(payload.Kind, targetSegmentId));
    }

    public static IProjectEditCommand CreatePasteLogicalParameterLaneContentCommand(
        ProjectDocumentSession targetDocument,
        ProjectObjectClipboardPayload payload,
        MidoraId targetSegmentId,
        MidoraId targetLaneId,
        long editCursorTick)
    {
        LogicalParameterLaneContentClipboardData data =
            RequirePayload<LogicalParameterLaneContentClipboardData>(
                targetDocument,
                payload,
                ProjectObjectClipboardKind.LogicalParameterLaneContent);
        return KeepClipboardAlive(payload, ProjectDomainEditCommands.PasteLogicalParameterLaneContentClipboard(
            data,
            targetSegmentId,
            targetLaneId,
            editCursorTick), new(payload.Kind, targetSegmentId, targetLaneId));
    }

    public static IProjectEditCommand CreatePasteConductorEventsCommand(
        ProjectDocumentSession targetDocument,
        ProjectObjectClipboardPayload payload,
        long editCursorTick)
    {
        ConductorEventsClipboardData data = RequirePayload<ConductorEventsClipboardData>(
            targetDocument,
            payload,
            ProjectObjectClipboardKind.ConductorEvents);
        return KeepClipboardAlive(payload, ProjectDomainEditCommands.PasteConductorEventsClipboard(
            data.Events,
            editCursorTick));
    }

    private static IReadOnlyList<CurvePointClipboardSnapshot> SnapshotTimelinePoints(
        IEnumerable<CurvePoint> source) => SnapshotTimelinePointValues(source.Select(value =>
            new CurvePointSnapshotValue(value.Id, value.Tick, value.Value, value.Interpolation)));

    private static IReadOnlyList<CurvePointClipboardSnapshot> SnapshotTimelinePointValues(
        IEnumerable<CurvePointSnapshotValue> source)
    {
        BoundedEditRecordStore<CurvePointSnapshotValue> selected = ClipboardCaptureScope.Sort(source,
            Comparer<CurvePointSnapshotValue>.Create((left, right) =>
            {
                int order = left.Tick.CompareTo(right.Tick);
                return order != 0 ? order : left.Id.CompareTo(right.Id);
            }), reportSelectionProgress: true);
        long earliest = selected.Count == 0 ? 0 : selected[0].Tick;
        return new ProjectedClipboardList<CurvePointSnapshotValue, CurvePointClipboardSnapshot>(selected,
            value => new(checked(value.Tick - earliest), value.Value, CurveInterpolation.Step));
    }

    private static IReadOnlySet<MidoraId> ValidateDistinctIds(
        IEnumerable<MidoraId> values,
        string parameterName)
    {
        if (values is IReadOnlySet<MidoraId> existing)
        {
            if (existing.Contains(default)) throw new ArgumentException(
                "Clipboard selections must contain distinct valid stable IDs.", parameterName);
            return existing;
        }
        BoundedEditRecordStore<MidoraId> result = ClipboardCaptureScope.Sort(values, Comparer<MidoraId>.Default);
        MidoraId? previous = null;
        foreach (MidoraId value in result)
        {
            if (value == default || previous == value)
            {
                throw new ArgumentException(
                    "Clipboard selections must contain distinct valid stable IDs.",
                    parameterName);
            }
            previous = value;
        }
        return new ClipboardIdSet(result);
    }

    private readonly record struct ConductorEventAbsoluteSnapshot(
        ConductorClipboardEventKind Kind,
        long Tick,
        decimal BeatsPerMinute,
        int Primary,
        int Secondary,
        bool Flag,
        string? Text);
}

internal sealed record LogicalParameterLaneClipboardData(
    MidoraId ParameterId,
    IReadOnlyList<CurvePointClipboardSnapshot> Points) : ProjectObjectClipboardData;

internal sealed record LogicalParameterLaneContentClipboardData(
    MidoraId ParameterId,
    IReadOnlyList<CurvePointClipboardSnapshot> Points) : ProjectObjectClipboardData;

internal enum ConductorClipboardEventKind
{
    Tempo,
    TimeSignature,
    KeySignature,
    Marker
}

internal sealed record ConductorEventsClipboardData(
    IReadOnlyList<ConductorEventClipboardSnapshot> Events) : ProjectObjectClipboardData;

internal sealed record ConductorEventClipboardSnapshot(
    ConductorClipboardEventKind Kind,
    long TickOffset,
    decimal BeatsPerMinute,
    int Primary,
    int Secondary,
    bool Flag,
    string? Text);

public static partial class ProjectDomainEditCommands
{
    internal static IProjectEditCommand PasteLogicalParameterLaneClipboard(
        LogicalParameterLaneClipboardData snapshot,
        MidoraId targetSegmentId,
        long editCursorTick) =>
        Command("Paste logical parameter lane", project =>
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (editCursorTick < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(editCursorTick));
            }
            SegmentLocation target = FindSegment(project, targetSegmentId);
            LogicalParameterDefinition definition = FindBoundLogicalParameter(
                project,
                target.Track,
                snapshot.ParameterId);
            if (target.Segment.ParameterLanes.Any(
                value => value.ParameterId == snapshot.ParameterId))
            {
                throw new InvalidOperationException(
                    "The target Segment already has a Lane for the exact Logical Parameter.");
            }
            return PrepareBoundedLogicalLaneClipboard(project, target, definition, snapshot.Points, editCursorTick);
        });

    internal static IProjectEditCommand PasteLogicalParameterLaneContentClipboard(
        LogicalParameterLaneContentClipboardData snapshot,
        MidoraId targetSegmentId,
        MidoraId targetLaneId,
        long editCursorTick) =>
        Command("Paste logical parameter points", project =>
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (editCursorTick < 0 || snapshot.Points.Count == 0)
            {
                throw new ArgumentOutOfRangeException(
                    editCursorTick < 0 ? nameof(editCursorTick) : nameof(snapshot));
            }
            SegmentLocation target = FindSegment(project, targetSegmentId);
            LogicalParameterLane lane = FindLogicalParameterLane(target.Segment, targetLaneId);
            if (lane.ParameterId != snapshot.ParameterId)
            {
                throw new InvalidOperationException(
                    "Logical Parameter content requires an exact target Parameter ID.");
            }
            return AppendBoundedLogicalParameterPoints(targetSegmentId, targetLaneId,
                snapshot.Points.Select(value => new CurvePointSnapshotValue(default,
                    checked(editCursorTick + value.Tick), value.Value, CurveInterpolation.Step))).Prepare(project);
        });

    internal static IProjectEditCommand PasteConductorEventsClipboard(
        IReadOnlyList<ConductorEventClipboardSnapshot> snapshots,
        long editCursorTick) =>
        Command("Paste conductor events", project =>
        {
            ArgumentNullException.ThrowIfNull(snapshots);
            if (snapshots.Count == 0 || editCursorTick < 0)
            {
                throw new ArgumentOutOfRangeException(
                    snapshots.Count == 0 ? nameof(snapshots) : nameof(editCursorTick));
            }
            using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
            using var collisions = BoundedEditSort.Sort(CollisionRows(),
                Comparer<ConductorClipboardCollision>.Create((a, b) =>
                {
                    int order = a.Kind.CompareTo(b.Kind);
                    if (order == 0) order = a.Tick.CompareTo(b.Tick);
                    return order == 0 ? a.Incoming.CompareTo(b.Incoming) : order;
                }), scope.Resources, scope.Token);
            ConductorClipboardCollision? previous = null;
            foreach (var row in collisions)
            {
                scope.Token.ThrowIfCancellationRequested();
                if (previous is { } old && old.Kind == row.Kind && old.Tick == row.Tick && (old.Incoming || row.Incoming))
                    throw new InvalidOperationException($"Pasted {row.Kind} events would create duplicate ticks.");
                previous = row;
            }
            long firstId = 0, nextId = 0;
            return Prepared(true, ConductorChange(), owner =>
            {
                firstId = owner.NextStableId;
                long processed = 0;
                foreach (ConductorEventClipboardSnapshot snapshot in snapshots)
                {
                    BulkEditPreparationContext.Current?.Checkpoint(processed++, snapshots.Count, TimelineEditPreparationPhase.BuildingResult);
                    ConductorClipboardValue value = ValidateConductorClipboardValue(snapshot, editCursorTick);
                    switch (value.Kind)
                    {
                        case ConductorClipboardEventKind.Tempo:
                            owner.Conductor.Tempos.Add(new(owner, value.Tick, value.BeatsPerMinute)); break;
                        case ConductorClipboardEventKind.TimeSignature:
                            owner.Conductor.TimeSignatures.Add(new(owner, value.Tick, value.Primary, value.Secondary)); break;
                        case ConductorClipboardEventKind.KeySignature:
                            owner.Conductor.KeySignatures.Add(new(owner, value.Tick, value.Primary, value.Flag)); break;
                        case ConductorClipboardEventKind.Marker:
                            owner.Conductor.Markers.Add(new(owner, value.Tick, value.Text ?? string.Empty)); break;
                    }
                }
                nextId = owner.NextStableId;
            }, owner =>
            {
                owner.Conductor.Tempos.RemoveAll(value => value.Id.Value >= firstId && value.Id.Value < nextId);
                owner.Conductor.TimeSignatures.RemoveAll(value => value.Id.Value >= firstId && value.Id.Value < nextId);
                owner.Conductor.KeySignatures.RemoveAll(value => value.Id.Value >= firstId && value.Id.Value < nextId);
                owner.Conductor.Markers.RemoveAll(value => value.Id.Value >= firstId && value.Id.Value < nextId);
            });

            IEnumerable<ConductorClipboardCollision> CollisionRows()
            {
                foreach (var value in project.Conductor.Tempos) yield return new(ConductorClipboardEventKind.Tempo, value.Tick, false);
                foreach (var value in project.Conductor.TimeSignatures) yield return new(ConductorClipboardEventKind.TimeSignature, value.Tick, false);
                foreach (var value in project.Conductor.KeySignatures) yield return new(ConductorClipboardEventKind.KeySignature, value.Tick, false);
                foreach (var snapshot in snapshots)
                {
                    scope.Token.ThrowIfCancellationRequested();
                    ConductorClipboardValue value = ValidateConductorClipboardValue(snapshot, editCursorTick);
                    if (value.Kind != ConductorClipboardEventKind.Marker) yield return new(value.Kind, value.Tick, true);
                }
            }
        });

    private static ConductorClipboardValue ValidateConductorClipboardValue(
        ConductorEventClipboardSnapshot snapshot,
        long editCursorTick)
    {
        long tick = checked(editCursorTick + snapshot.TickOffset);
        ValidateConductorTick(tick, nameof(editCursorTick));
        switch (snapshot.Kind)
        {
            case ConductorClipboardEventKind.Tempo:
                ValidateTempo(snapshot.BeatsPerMinute);
                break;
            case ConductorClipboardEventKind.TimeSignature:
                if (snapshot.Primary is < 1 or > 99
                    || snapshot.Secondary is not (1 or 2 or 4 or 8 or 16 or 32 or 64))
                {
                    throw new InvalidOperationException(
                        "The Time Signature clipboard snapshot is invalid.");
                }
                break;
            case ConductorClipboardEventKind.KeySignature:
                if (snapshot.Primary is < -7 or > 7)
                {
                    throw new InvalidOperationException(
                        "The Key Signature clipboard snapshot is invalid.");
                }
                break;
            case ConductorClipboardEventKind.Marker:
                _ = ProjectTextRules.NormalizeShortText(
                    snapshot.Text ?? string.Empty,
                    allowEmpty: true,
                    nameof(snapshot));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(snapshot));
        }
        return new(
            snapshot.Kind,
            tick,
            snapshot.BeatsPerMinute,
            snapshot.Primary,
            snapshot.Secondary,
            snapshot.Flag,
            snapshot.Text);
    }

    private readonly record struct ConductorClipboardCollision(ConductorClipboardEventKind Kind, long Tick, bool Incoming);

    private readonly record struct ConductorClipboardValue(
        ConductorClipboardEventKind Kind,
        long Tick,
        decimal BeatsPerMinute,
        int Primary,
        int Secondary,
        bool Flag,
        string? Text);

}
