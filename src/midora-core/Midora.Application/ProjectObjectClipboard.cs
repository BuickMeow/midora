using Midora.Domain;

namespace Midora.Application;

public enum ProjectObjectClipboardKind
{
    EventInstrument,
    Segments,
    LogicalNotes,
    LogicalParameterLane,
    LogicalParameterLaneContent,
    SubVoiceTimelineEvents,
    SubVoice,
    ValueCurveContent,
    MappingChain,
    LogicalParameterDefinition,
    LogicalParameterMapping,
    MappingStep,
    EnvelopePreset,
    MappingFunction,
    ConductorEvents
}

public sealed class ProjectObjectClipboardPayload
{
    internal ProjectObjectClipboardPayload(
        object sourceSessionIdentity,
        ProjectObjectClipboardKind kind,
        int objectCount,
        string plainTextSummary,
        ProjectObjectClipboardData data)
    {
        SourceSessionIdentity = sourceSessionIdentity;
        Kind = kind;
        ObjectCount = objectCount;
        PlainTextSummary = plainTextSummary;
        Data = data;
    }

    public ProjectObjectClipboardKind Kind { get; }
    public int ObjectCount { get; }
    public string PlainTextSummary { get; }
    internal object SourceSessionIdentity { get; }
    internal ProjectObjectClipboardData Data { get; }
}

public static partial class ProjectObjectClipboard
{
    public static ProjectObjectClipboardPayload CopySegments(
        ProjectDocumentSession document,
        IReadOnlyCollection<MidoraId> segmentIds,
        MidoraId primarySegmentId)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(segmentIds);
        if (segmentIds.Count == 0)
        {
            throw new ArgumentException(
                "At least one Segment must be copied.",
                nameof(segmentIds));
        }
        HashSet<MidoraId> requested = [];
        List<(LogicalTrack Track, Segment Segment, int TrackIndex)> selected = [];
        foreach (MidoraId id in segmentIds)
        {
            if (id == default || !requested.Add(id))
            {
                throw new ArgumentException(
                    "Segment clipboard selections must contain distinct valid stable IDs.",
                    nameof(segmentIds));
            }
            selected.Add(FindSegment(document.Project, id));
        }
        var primary = selected.SingleOrDefault(value => value.Segment.Id == primarySegmentId);
        if (primary.Segment is null)
        {
            throw new ArgumentException(
                "The primary Segment must belong to the clipboard selection.",
                nameof(primarySegmentId));
        }
        long earliest = selected.Min(value => value.Segment.ProjectStartTick);
        SegmentClipboardSnapshot[] snapshots = selected
            .OrderBy(value => value.TrackIndex)
            .ThenBy(value => value.Segment.ProjectStartTick)
            .ThenBy(value => value.Segment.Id)
            .Select(value => SnapshotSegment(
                value.Segment,
                checked(value.TrackIndex - primary.TrackIndex),
                checked(value.Segment.ProjectStartTick - earliest)))
            .ToArray();
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.Segments,
            snapshots.Length,
            snapshots.Length == 1 ? "1 Segment" : $"{snapshots.Length} Segments",
            new SegmentClipboardData(snapshots));
    }

    public static ProjectObjectClipboardPayload CopyLogicalNotes(
        ProjectDocumentSession document,
        MidoraId sourceSegmentId,
        IReadOnlyCollection<MidoraId> logicalNoteIds)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(logicalNoteIds);
        Segment source = FindSegment(document.Project, sourceSegmentId).Segment;
        if (logicalNoteIds.Count == 0)
        {
            throw new ArgumentException(
                "At least one Logical Note must be copied.",
                nameof(logicalNoteIds));
        }
        HashSet<MidoraId> requested = [];
        foreach (MidoraId id in logicalNoteIds)
        {
            if (id == default || !requested.Add(id))
            {
                throw new ArgumentException(
                    "Logical Note clipboard selections must contain distinct valid stable IDs.",
                    nameof(logicalNoteIds));
            }
        }
        LogicalNote[] selected = source.Notes
            .Where(value => requested.Contains(value.Id))
            .ToArray();
        if (selected.Length != requested.Count)
        {
            throw new ArgumentException(
                "Every copied Logical Note must belong to the source Segment.",
                nameof(logicalNoteIds));
        }
        long earliest = selected.Min(value => value.StartTick);
        LogicalNoteClipboardSnapshot[] snapshots = selected.Select(value => new LogicalNoteClipboardSnapshot(
            checked(value.StartTick - earliest),
            value.LengthTicks,
            value.Note,
            value.Velocity)).ToArray();
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.LogicalNotes,
            snapshots.Length,
            snapshots.Length == 1 ? "1 Logical Note" : $"{snapshots.Length} Logical Notes",
            new LogicalNoteClipboardData(snapshots));
    }

    public static IProjectEditCommand CreatePasteSegmentsCommand(
        ProjectDocumentSession targetDocument,
        ProjectObjectClipboardPayload payload,
        MidoraId activeTargetTrackId,
        long editCursorTick)
    {
        SegmentClipboardData data = RequirePayload<SegmentClipboardData>(
            targetDocument,
            payload,
            ProjectObjectClipboardKind.Segments);
        return ProjectDomainEditCommands.PasteSegmentClipboard(
            data.Segments,
            activeTargetTrackId,
            editCursorTick);
    }

    public static IProjectEditCommand CreatePasteLogicalNotesCommand(
        ProjectDocumentSession targetDocument,
        ProjectObjectClipboardPayload payload,
        MidoraId targetSegmentId,
        long editCursorTick)
    {
        LogicalNoteClipboardData data = RequirePayload<LogicalNoteClipboardData>(
            targetDocument,
            payload,
            ProjectObjectClipboardKind.LogicalNotes);
        return ProjectDomainEditCommands.PasteLogicalNoteClipboard(
            data.Notes,
            targetSegmentId,
            editCursorTick);
    }

    private static T RequirePayload<T>(
        ProjectDocumentSession targetDocument,
        ProjectObjectClipboardPayload payload,
        ProjectObjectClipboardKind expectedKind)
        where T : ProjectObjectClipboardData
    {
        ArgumentNullException.ThrowIfNull(targetDocument);
        ArgumentNullException.ThrowIfNull(payload);
        if (!ReferenceEquals(
            targetDocument.ClipboardSessionIdentity,
            payload.SourceSessionIdentity))
        {
            throw new InvalidOperationException(
                "Project object clipboard payloads are only valid in their source Project session.");
        }
        if (payload.Kind != expectedKind || payload.Data is not T data)
        {
            throw new ArgumentException(
                $"The clipboard payload does not contain {expectedKind}.",
                nameof(payload));
        }
        return data;
    }

    private static (
        LogicalTrack Track,
        Segment Segment,
        int TrackIndex) FindSegment(MidoraProject project, MidoraId segmentId)
    {
        (LogicalTrack Track, Segment Segment, int TrackIndex)? result = null;
        for (int trackIndex = 0; trackIndex < project.Tracks.Count; trackIndex++)
        {
            LogicalTrack track = project.Tracks[trackIndex];
            foreach (Segment segment in track.Segments)
            {
                if (segment.Id != segmentId)
                {
                    continue;
                }
                if (result.HasValue)
                {
                    throw new InvalidOperationException("The Segment stable ID is duplicated.");
                }
                result = (track, segment, trackIndex);
            }
        }
        return result ?? throw new ArgumentOutOfRangeException(nameof(segmentId));
    }

    private static SegmentClipboardSnapshot SnapshotSegment(
        Segment segment,
        int trackOffset,
        long startOffset) =>
        new(
            trackOffset,
            startOffset,
            segment.LengthTicks,
            segment.ContentOffsetTick,
            segment.Notes.Select(value => new LogicalNoteClipboardSnapshot(
                value.StartTick,
                value.LengthTicks,
                value.Note,
                value.Velocity)).ToArray(),
            segment.ParameterLanes.Select(value => new LogicalParameterLaneClipboardSnapshot(
                value.ParameterId,
                value.Points.Select(point => new CurvePointClipboardSnapshot(
                    point.Tick,
                    point.Value,
                    point.Interpolation)).ToArray())).ToArray());
}

internal abstract record ProjectObjectClipboardData;
internal sealed record SegmentClipboardData(
    SegmentClipboardSnapshot[] Segments) : ProjectObjectClipboardData;
internal sealed record LogicalNoteClipboardData(
    LogicalNoteClipboardSnapshot[] Notes) : ProjectObjectClipboardData;
internal sealed record SegmentClipboardSnapshot(
    int TrackOffset,
    long StartOffset,
    long LengthTicks,
    long ContentOffsetTick,
    LogicalNoteClipboardSnapshot[] Notes,
    LogicalParameterLaneClipboardSnapshot[] ParameterLanes);
internal sealed record LogicalNoteClipboardSnapshot(
    long StartOffset,
    long LengthTicks,
    int Note,
    int Velocity);
internal sealed record LogicalParameterLaneClipboardSnapshot(
    MidoraId ParameterId,
    CurvePointClipboardSnapshot[] Points);
internal sealed record CurvePointClipboardSnapshot(
    long Tick,
    double Value,
    CurveInterpolation Interpolation);

public static partial class ProjectDomainEditCommands
{
    internal static IProjectEditCommand PasteSegmentClipboard(
        IReadOnlyList<SegmentClipboardSnapshot> snapshots,
        MidoraId activeTargetTrackId,
        long editCursorTick) =>
        Command("Paste segments", project =>
        {
            ArgumentNullException.ThrowIfNull(snapshots);
            if (snapshots.Count == 0 || editCursorTick < 0)
            {
                throw new ArgumentOutOfRangeException(
                    snapshots.Count == 0 ? nameof(snapshots) : nameof(editCursorTick));
            }
            LogicalTrack primaryTrack = FindTrack(project, activeTargetTrackId);
            int primaryTrackIndex = project.Tracks.IndexOf(primaryTrack);
            SegmentClipboardPlacement[] placements = snapshots.Select(snapshot =>
            {
                int targetIndex = checked(primaryTrackIndex + snapshot.TrackOffset);
                if ((uint)targetIndex >= (uint)project.Tracks.Count)
                {
                    throw new InvalidOperationException(
                        "The Segment clipboard payload cannot preserve its relative Track offsets at the target.");
                }
                long start = checked(editCursorTick + snapshot.StartOffset);
                ValidateSegmentRange(start, snapshot.LengthTicks, snapshot.ContentOffsetTick);
                return new SegmentClipboardPlacement(
                    snapshot,
                    project.Tracks[targetIndex],
                    start);
            }).ToArray();
            ValidateClipboardSegmentPlacements(placements);
            Segment[]? copies = null;
            return Prepared(
                hasChanges: true,
                TrackChange(placements.Select(value => value.TargetTrack.Id).Distinct().ToArray()),
                owner =>
                {
                    if (copies is null)
                    {
                        Segment[] created = placements
                            .Select(value => CreateSegmentFromClipboard(
                                owner,
                                value.Snapshot,
                                value.ProjectStartTick))
                            .ToArray();
                        copies = created;
                    }
                    for (int index = 0; index < copies.Length; index++)
                    {
                        InsertSegmentByTime(placements[index].TargetTrack.Segments, copies[index]);
                    }
                },
                _ =>
                {
                    if (copies is null)
                    {
                        throw new InvalidOperationException(
                            "Segment clipboard copies do not exist before the first Apply.");
                    }
                    for (int index = 0; index < copies.Length; index++)
                    {
                        RemoveRequired(
                            placements[index].TargetTrack.Segments,
                            copies[index],
                            "pasted Segment");
                    }
                });
        });

    internal static IProjectEditCommand PasteLogicalNoteClipboard(
        IReadOnlyList<LogicalNoteClipboardSnapshot> snapshots,
        MidoraId targetSegmentId,
        long editCursorTick) =>
        Command("Paste logical notes", project =>
        {
            ArgumentNullException.ThrowIfNull(snapshots);
            if (snapshots.Count == 0 || editCursorTick < 0)
            {
                throw new ArgumentOutOfRangeException(
                    snapshots.Count == 0 ? nameof(snapshots) : nameof(editCursorTick));
            }
            SegmentLocation target = FindSegment(project, targetSegmentId);
            LogicalNoteValue[] values = snapshots.Select(value => new LogicalNoteValue(
                checked(editCursorTick + value.StartOffset),
                value.LengthTicks,
                value.Note,
                value.Velocity)).ToArray();
            ValidateLogicalNoteBatch(values);
            LogicalNote[]? copies = null;
            int insertionIndex = target.Segment.Notes.Count;
            return Prepared(
                hasChanges: true,
                TrackChange(target.Track.Id),
                owner =>
                {
                    if (copies is null)
                    {
                        copies = values.Select(value => new LogicalNote(owner)
                        {
                            StartTick = value.StartTick,
                            LengthTicks = value.LengthTicks,
                            Note = value.Note,
                            Velocity = value.Velocity
                        }).ToArray();
                    }
                    for (int index = 0; index < copies.Length; index++)
                    {
                        InsertAt(
                            target.Segment.Notes,
                            insertionIndex + index,
                            copies[index],
                            "pasted Logical Note");
                    }
                },
                _ =>
                {
                    if (copies is null)
                    {
                        throw new InvalidOperationException(
                            "Logical Note clipboard copies do not exist before the first Apply.");
                    }
                    foreach (LogicalNote copy in copies)
                    {
                        RemoveRequired(target.Segment.Notes, copy, "pasted Logical Note");
                    }
                });
        });

    private static Segment CreateSegmentFromClipboard(
        MidoraProject project,
        SegmentClipboardSnapshot snapshot,
        long projectStartTick)
    {
        Segment result = new(project)
        {
            ProjectStartTick = projectStartTick,
            LengthTicks = snapshot.LengthTicks,
            ContentOffsetTick = snapshot.ContentOffsetTick
        };
        foreach (LogicalNoteClipboardSnapshot value in snapshot.Notes)
        {
            ValidateLogicalNote(value.StartOffset, value.LengthTicks, value.Note, value.Velocity);
            result.Notes.Add(new LogicalNote(project)
            {
                StartTick = value.StartOffset,
                LengthTicks = value.LengthTicks,
                Note = value.Note,
                Velocity = value.Velocity
            });
        }
        foreach (LogicalParameterLaneClipboardSnapshot value in snapshot.ParameterLanes)
        {
            LogicalParameterLane lane = new(project) { ParameterId = value.ParameterId };
            foreach (CurvePointClipboardSnapshot point in value.Points)
            {
                lane.Points.Add(new CurvePoint(
                    project,
                    point.Tick,
                    point.Value,
                    point.Interpolation));
            }
            result.ParameterLanes.Add(lane);
        }
        return result;
    }

    private static void ValidateClipboardSegmentPlacements(
        SegmentClipboardPlacement[] placements)
    {
        foreach (IGrouping<LogicalTrack, SegmentClipboardPlacement> group in placements
            .GroupBy(value => value.TargetTrack))
        {
            SegmentClipboardPlacement[] ordered = group
                .OrderBy(value => value.ProjectStartTick)
                .ToArray();
            for (int index = 0; index < ordered.Length; index++)
            {
                TickRange range = new(
                    ordered[index].ProjectStartTick,
                    checked(ordered[index].ProjectStartTick
                        + ordered[index].Snapshot.LengthTicks));
                if (group.Key.Segments.Any(value => range.Intersects(value.ProjectRange)))
                {
                    throw new InvalidOperationException(
                        "Pasted Segments would overlap an existing Segment.");
                }
                if (index != 0)
                {
                    long previousEnd = checked(ordered[index - 1].ProjectStartTick
                        + ordered[index - 1].Snapshot.LengthTicks);
                    if (previousEnd > ordered[index].ProjectStartTick)
                    {
                        throw new InvalidOperationException(
                            "Pasted Segments would overlap each other.");
                    }
                }
            }
        }
    }

    private readonly record struct SegmentClipboardPlacement(
        SegmentClipboardSnapshot Snapshot,
        LogicalTrack TargetTrack,
        long ProjectStartTick);
}
