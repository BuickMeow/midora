using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectObjectClipboard
{
    public static ProjectObjectClipboardPayload CopyLogicalParameterLane(
        ProjectDocumentSession document,
        MidoraId sourceSegmentId,
        MidoraId laneId)
    {
        ArgumentNullException.ThrowIfNull(document);
        Segment segment = FindSegment(document.Project, sourceSegmentId).Segment;
        LogicalParameterLane lane = segment.ParameterLanes
            .SingleOrDefault(value => value.Id == laneId)
            ?? throw new ArgumentOutOfRangeException(nameof(laneId));
        CurvePointClipboardSnapshot[] points = SnapshotTimelinePoints(lane.Points);
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
        HashSet<MidoraId> requested = ValidateDistinctIds(pointIds, nameof(pointIds));
        CurvePoint[] selected = lane.Points
            .Where(value => requested.Contains(value.Id))
            .ToArray();
        if (selected.Length != requested.Count)
        {
            throw new ArgumentException(
                "Every copied Logical Parameter point must belong to the source Lane.",
                nameof(pointIds));
        }
        CurvePointClipboardSnapshot[] points = SnapshotTimelinePoints(selected);
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.LogicalParameterLaneContent,
            points.Length,
            points.Length == 1
                ? "1 Logical Parameter Point"
                : $"{points.Length} Logical Parameter Points",
            new LogicalParameterLaneContentClipboardData(lane.ParameterId, points));
    }

    public static ProjectObjectClipboardPayload CopyConductorEvents(
        ProjectDocumentSession document,
        IReadOnlyCollection<MidoraId> eventIds)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(eventIds);
        if (eventIds.Count == 0)
        {
            throw new ArgumentException(
                "At least one ordinary Conductor event must be copied.",
                nameof(eventIds));
        }
        HashSet<MidoraId> requested = ValidateDistinctIds(eventIds, nameof(eventIds));
        List<ConductorEventAbsoluteSnapshot> selected = [];
        selected.AddRange(document.Project.Conductor.Tempos
            .Where(value => requested.Contains(value.Id))
            .Select(value => new ConductorEventAbsoluteSnapshot(
                ConductorClipboardEventKind.Tempo,
                value.Tick,
                value.BeatsPerMinute,
                0,
                0,
                false,
                null)));
        selected.AddRange(document.Project.Conductor.TimeSignatures
            .Where(value => requested.Contains(value.Id))
            .Select(value => new ConductorEventAbsoluteSnapshot(
                ConductorClipboardEventKind.TimeSignature,
                value.Tick,
                0,
                value.Numerator,
                value.Denominator,
                false,
                null)));
        selected.AddRange(document.Project.Conductor.KeySignatures
            .Where(value => requested.Contains(value.Id))
            .Select(value => new ConductorEventAbsoluteSnapshot(
                ConductorClipboardEventKind.KeySignature,
                value.Tick,
                0,
                value.SharpsFlats,
                0,
                value.IsMinor,
                null)));
        selected.AddRange(document.Project.Conductor.Markers
            .Where(value => requested.Contains(value.Id))
            .Select(value => new ConductorEventAbsoluteSnapshot(
                ConductorClipboardEventKind.Marker,
                value.Tick,
                0,
                0,
                0,
                false,
                value.Name)));
        if (selected.Count != requested.Count)
        {
            throw new ArgumentException(
                "Every copied ID must identify an ordinary Conductor event; the Project End Marker is excluded.",
                nameof(eventIds));
        }
        long earliest = selected.Min(value => value.Tick);
        ConductorEventClipboardSnapshot[] snapshots = selected
            .OrderBy(value => value.Tick)
            .ThenBy(value => value.Kind)
            .Select(value => new ConductorEventClipboardSnapshot(
                value.Kind,
                checked(value.Tick - earliest),
                value.BeatsPerMinute,
                value.Primary,
                value.Secondary,
                value.Flag,
                value.Text))
            .ToArray();
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.ConductorEvents,
            snapshots.Length,
            snapshots.Length == 1 ? "1 Conductor Event" : $"{snapshots.Length} Conductor Events",
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
        return ProjectDomainEditCommands.PasteLogicalParameterLaneClipboard(
            data,
            targetSegmentId,
            editCursorTick);
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
        return ProjectDomainEditCommands.PasteLogicalParameterLaneContentClipboard(
            data,
            targetSegmentId,
            targetLaneId,
            editCursorTick);
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
        return ProjectDomainEditCommands.PasteConductorEventsClipboard(
            data.Events,
            editCursorTick);
    }

    private static CurvePointClipboardSnapshot[] SnapshotTimelinePoints(
        IEnumerable<CurvePoint> source)
    {
        CurvePoint[] selected = source
            .OrderBy(value => value.Tick)
            .ThenBy(value => value.Id)
            .ToArray();
        long earliest = selected.Length == 0 ? 0 : selected[0].Tick;
        return selected.Select(value => new CurvePointClipboardSnapshot(
            checked(value.Tick - earliest),
            value.Value,
            value.Interpolation)).ToArray();
    }

    private static HashSet<MidoraId> ValidateDistinctIds(
        IEnumerable<MidoraId> values,
        string parameterName)
    {
        HashSet<MidoraId> result = [];
        foreach (MidoraId value in values)
        {
            if (value == default || !result.Add(value))
            {
                throw new ArgumentException(
                    "Clipboard selections must contain distinct valid stable IDs.",
                    parameterName);
            }
        }
        return result;
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
    CurvePointClipboardSnapshot[] Points) : ProjectObjectClipboardData;

internal sealed record LogicalParameterLaneContentClipboardData(
    MidoraId ParameterId,
    CurvePointClipboardSnapshot[] Points) : ProjectObjectClipboardData;

internal enum ConductorClipboardEventKind
{
    Tempo,
    TimeSignature,
    KeySignature,
    Marker
}

internal sealed record ConductorEventsClipboardData(
    ConductorEventClipboardSnapshot[] Events) : ProjectObjectClipboardData;

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
            CurvePointClipboardValue[] values = PrepareLogicalParameterPoints(
                definition,
                snapshot.Points,
                editCursorTick);
            LogicalParameterLane? copy = null;
            int insertionIndex = target.Segment.ParameterLanes.Count;
            return Prepared(
                hasChanges: true,
                TrackChange(target.Track.Id),
                owner =>
                {
                    if (copy is null)
                    {
                        copy = new LogicalParameterLane(owner)
                        {
                            ParameterId = snapshot.ParameterId
                        };
                        copy.Points.AddRange(values.Select(value => new CurvePoint(
                            owner,
                            value.Tick,
                            value.Value,
                            value.Interpolation)));
                    }
                    InsertAt(
                        target.Segment.ParameterLanes,
                        insertionIndex,
                        copy,
                        "pasted Logical Parameter Lane");
                },
                _ => RemoveRequired(
                    target.Segment.ParameterLanes,
                    copy ?? throw new InvalidOperationException(
                        "The pasted Logical Parameter Lane does not exist before Apply."),
                    "pasted Logical Parameter Lane"));
        });

    internal static IProjectEditCommand PasteLogicalParameterLaneContentClipboard(
        LogicalParameterLaneContentClipboardData snapshot,
        MidoraId targetSegmentId,
        MidoraId targetLaneId,
        long editCursorTick) =>
        Command("Paste logical parameter points", project =>
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (editCursorTick < 0 || snapshot.Points.Length == 0)
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
            LogicalParameterDefinition definition = FindBoundLogicalParameter(
                project,
                target.Track,
                snapshot.ParameterId);
            CurvePointClipboardValue[] values = PrepareLogicalParameterPoints(
                definition,
                snapshot.Points,
                editCursorTick);
            CurvePoint[]? copies = null;
            return ResolveExactLogicalParameterPointCollisions(Prepared(
                hasChanges: true,
                TrackChange(target.Track.Id),
                owner =>
                {
                    copies ??= values.Select(value => new CurvePoint(
                        owner,
                        value.Tick,
                        value.Value,
                        value.Interpolation)).ToArray();
                    lane.Points.AddRange(copies);
                },
                _ =>
                {
                    if (copies is null)
                    {
                        throw new InvalidOperationException(
                            "Pasted Logical Parameter points do not exist before Apply.");
                    }
                    foreach (CurvePoint point in copies)
                    {
                        RemoveRequired(lane.Points, point, "pasted Logical Parameter point");
                    }
                }), lane);
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
            ConductorClipboardValue[] values = snapshots.Select(value =>
                ValidateConductorClipboardValue(value, editCursorTick)).ToArray();
            ValidateConductorClipboardConflicts(project.Conductor, values);
            PastedConductorEvents? copies = null;
            return Prepared(
                hasChanges: true,
                ConductorChange(),
                owner =>
                {
                    copies ??= CreateConductorClipboardCopies(owner, values);
                    owner.Conductor.Tempos.AddRange(copies.Tempos);
                    owner.Conductor.TimeSignatures.AddRange(copies.TimeSignatures);
                    owner.Conductor.KeySignatures.AddRange(copies.KeySignatures);
                    owner.Conductor.Markers.AddRange(copies.Markers);
                },
                owner =>
                {
                    if (copies is null)
                    {
                        throw new InvalidOperationException(
                            "Pasted Conductor events do not exist before Apply.");
                    }
                    foreach (TempoChange value in copies.Tempos)
                    {
                        RemoveRequired(owner.Conductor.Tempos, value, "pasted Tempo");
                    }
                    foreach (TimeSignatureChange value in copies.TimeSignatures)
                    {
                        RemoveRequired(
                            owner.Conductor.TimeSignatures,
                            value,
                            "pasted Time Signature");
                    }
                    foreach (KeySignatureChange value in copies.KeySignatures)
                    {
                        RemoveRequired(
                            owner.Conductor.KeySignatures,
                            value,
                            "pasted Key Signature");
                    }
                    foreach (ProjectMarker value in copies.Markers)
                    {
                        RemoveRequired(owner.Conductor.Markers, value, "pasted Marker");
                    }
                });
        });

    private static CurvePointClipboardValue[] PrepareLogicalParameterPoints(
        LogicalParameterDefinition definition,
        IEnumerable<CurvePointClipboardSnapshot> snapshots,
        long editCursorTick)
    {
        HashSet<long> pointTicks = [];
        CurvePointClipboardValue[] values = snapshots.Select(value =>
        {
            long tick = checked(editCursorTick + value.Tick);
            ValidatePointValue(definition, value.Value, value.Interpolation);
            return new CurvePointClipboardValue(tick, value.Value, value.Interpolation);
        }).Where(value => pointTicks.Add(value.Tick)).ToArray();
        return values;
    }

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

    private static void ValidateConductorClipboardConflicts(
        ConductorTrack conductor,
        IReadOnlyCollection<ConductorClipboardValue> values)
    {
        ValidateUniqueConductorTicks(
            values.Where(value => value.Kind == ConductorClipboardEventKind.Tempo),
            conductor.Tempos.Select(value => value.Tick),
            "Tempo");
        ValidateUniqueConductorTicks(
            values.Where(value => value.Kind == ConductorClipboardEventKind.TimeSignature),
            conductor.TimeSignatures.Select(value => value.Tick),
            "Time Signature");
        ValidateUniqueConductorTicks(
            values.Where(value => value.Kind == ConductorClipboardEventKind.KeySignature),
            conductor.KeySignatures.Select(value => value.Tick),
            "Key Signature");
    }

    private static void ValidateUniqueConductorTicks(
        IEnumerable<ConductorClipboardValue> selected,
        IEnumerable<long> existingTicks,
        string eventName)
    {
        long[] ticks = selected.Select(value => value.Tick).ToArray();
        HashSet<long> existing = existingTicks.ToHashSet();
        if (ticks.Distinct().Count() != ticks.Length || ticks.Any(existing.Contains))
        {
            throw new InvalidOperationException(
                $"Pasted {eventName} events would create duplicate ticks.");
        }
    }

    private static PastedConductorEvents CreateConductorClipboardCopies(
        MidoraProject project,
        IEnumerable<ConductorClipboardValue> values)
    {
        List<TempoChange> tempos = [];
        List<TimeSignatureChange> timeSignatures = [];
        List<KeySignatureChange> keySignatures = [];
        List<ProjectMarker> markers = [];
        foreach (ConductorClipboardValue value in values)
        {
            switch (value.Kind)
            {
                case ConductorClipboardEventKind.Tempo:
                    tempos.Add(new TempoChange(project, value.Tick, value.BeatsPerMinute));
                    break;
                case ConductorClipboardEventKind.TimeSignature:
                    timeSignatures.Add(new TimeSignatureChange(
                        project,
                        value.Tick,
                        value.Primary,
                        value.Secondary));
                    break;
                case ConductorClipboardEventKind.KeySignature:
                    keySignatures.Add(new KeySignatureChange(
                        project,
                        value.Tick,
                        value.Primary,
                        value.Flag));
                    break;
                case ConductorClipboardEventKind.Marker:
                    markers.Add(new ProjectMarker(
                        project,
                        value.Tick,
                        value.Text ?? string.Empty));
                    break;
            }
        }
        return new(tempos, timeSignatures, keySignatures, markers);
    }

    private readonly record struct CurvePointClipboardValue(
        long Tick,
        double Value,
        CurveInterpolation Interpolation);

    private readonly record struct ConductorClipboardValue(
        ConductorClipboardEventKind Kind,
        long Tick,
        decimal BeatsPerMinute,
        int Primary,
        int Secondary,
        bool Flag,
        string? Text);

    private sealed record PastedConductorEvents(
        TempoChange[] Tempos,
        TimeSignatureChange[] TimeSignatures,
        KeySignatureChange[] KeySignatures,
        ProjectMarker[] Markers)
    {
        public PastedConductorEvents(
            IEnumerable<TempoChange> tempos,
            IEnumerable<TimeSignatureChange> timeSignatures,
            IEnumerable<KeySignatureChange> keySignatures,
            IEnumerable<ProjectMarker> markers)
            : this(
                tempos.ToArray(),
                timeSignatures.ToArray(),
                keySignatures.ToArray(),
                markers.ToArray())
        {
        }
    }
}
