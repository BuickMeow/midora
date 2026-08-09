using Midora.Domain;

namespace Midora.Application;

public enum ProjectBatchValueEditMode
{
    ExactSet,
    RelativeAdjust
}

public enum LogicalNoteAlignment
{
    Start,
    End,
    Length
}

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand MoveLogicalNotes(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> logicalNoteIds,
        long tickDelta,
        int pitchDelta) =>
        Command("Move logical notes", project =>
        {
            SegmentLocation segment = FindSegment(project, segmentId);
            SelectedLogicalNote[] selected = SelectLogicalNotes(
                segment.Segment,
                logicalNoteIds);
            LogicalNoteValue[] old = selected.Select(value => Snapshot(value.Note)).ToArray();
            LogicalNoteValue[] replacement = old.Select(value => new LogicalNoteValue(
                checked(value.StartTick + tickDelta),
                value.LengthTicks,
                checked(value.Note + pitchDelta),
                value.Velocity)).ToArray();
            ValidateLogicalNoteBatch(replacement);
            return PrepareLogicalNoteBatch(
                segment.Track.Id,
                selected,
                old,
                replacement);
        });

    public static IProjectEditCommand AdjustLogicalNoteEdges(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> logicalNoteIds,
        long startDelta,
        long endDelta) =>
        Command("Adjust logical note edges", project =>
        {
            SegmentLocation segment = FindSegment(project, segmentId);
            SelectedLogicalNote[] selected = SelectLogicalNotes(
                segment.Segment,
                logicalNoteIds);
            LogicalNoteValue[] old = selected.Select(value => Snapshot(value.Note)).ToArray();
            LogicalNoteValue[] replacement = old.Select(value =>
            {
                long start = checked(value.StartTick + startDelta);
                long end = checked(checked(value.StartTick + value.LengthTicks) + endDelta);
                return new LogicalNoteValue(
                    start,
                    checked(end - start),
                    value.Note,
                    value.Velocity);
            }).ToArray();
            ValidateLogicalNoteBatch(replacement);
            return PrepareLogicalNoteBatch(
                segment.Track.Id,
                selected,
                old,
                replacement);
        });

    public static IProjectEditCommand SetLogicalNoteVelocities(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> logicalNoteIds,
        int value,
        ProjectBatchValueEditMode mode) =>
        Command("Change logical note velocities", project =>
        {
            if (!Enum.IsDefined(mode))
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }
            SegmentLocation segment = FindSegment(project, segmentId);
            SelectedLogicalNote[] selected = SelectLogicalNotes(
                segment.Segment,
                logicalNoteIds);
            LogicalNoteValue[] old = selected.Select(item => Snapshot(item.Note)).ToArray();
            LogicalNoteValue[] replacement = old.Select(item => new LogicalNoteValue(
                item.StartTick,
                item.LengthTicks,
                item.Note,
                mode == ProjectBatchValueEditMode.ExactSet
                    ? value
                    : checked(item.Velocity + value))).ToArray();
            ValidateLogicalNoteBatch(replacement);
            return PrepareLogicalNoteBatch(
                segment.Track.Id,
                selected,
                old,
                replacement);
        });

    public static IProjectEditCommand SetLogicalNoteValues(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> logicalNoteIds,
        long? startTick = null,
        long? lengthTicks = null,
        int? note = null,
        int? velocity = null) =>
        Command("Set logical note values", project =>
        {
            if (startTick is null
                && lengthTicks is null
                && note is null
                && velocity is null)
            {
                throw new ArgumentException(
                    "At least one Logical Note value must be provided.",
                    nameof(startTick));
            }
            SegmentLocation segment = FindSegment(project, segmentId);
            SelectedLogicalNote[] selected = SelectLogicalNotes(
                segment.Segment,
                logicalNoteIds);
            LogicalNoteValue[] old = selected.Select(item => Snapshot(item.Note)).ToArray();
            LogicalNoteValue[] replacement = old.Select(item => new LogicalNoteValue(
                startTick ?? item.StartTick,
                lengthTicks ?? item.LengthTicks,
                note ?? item.Note,
                velocity ?? item.Velocity)).ToArray();
            ValidateLogicalNoteBatch(replacement);
            return PrepareLogicalNoteBatch(
                segment.Track.Id,
                selected,
                old,
                replacement);
        });

    public static IProjectEditCommand AlignLogicalNotes(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> logicalNoteIds,
        MidoraId primaryLogicalNoteId,
        LogicalNoteAlignment alignment) =>
        Command("Align logical notes", project =>
        {
            if (!Enum.IsDefined(alignment))
            {
                throw new ArgumentOutOfRangeException(nameof(alignment));
            }
            SegmentLocation segment = FindSegment(project, segmentId);
            SelectedLogicalNote[] selected = SelectLogicalNotes(
                segment.Segment,
                logicalNoteIds);
            LogicalNote primary = selected
                .SingleOrDefault(value => value.Note.Id == primaryLogicalNoteId).Note
                ?? throw new ArgumentException(
                    "The primary Logical Note must belong to the batch selection.",
                    nameof(primaryLogicalNoteId));
            LogicalNoteValue primaryValue = Snapshot(primary);
            long primaryEnd = checked(primaryValue.StartTick + primaryValue.LengthTicks);
            LogicalNoteValue[] old = selected.Select(value => Snapshot(value.Note)).ToArray();
            LogicalNoteValue[] replacement = old.Select(value => alignment switch
            {
                LogicalNoteAlignment.Start => value with
                {
                    StartTick = primaryValue.StartTick
                },
                LogicalNoteAlignment.End => value with
                {
                    StartTick = checked(primaryEnd - value.LengthTicks)
                },
                LogicalNoteAlignment.Length => value with
                {
                    LengthTicks = primaryValue.LengthTicks
                },
                _ => throw new ArgumentOutOfRangeException(nameof(alignment))
            }).ToArray();
            ValidateLogicalNoteBatch(replacement);
            return PrepareLogicalNoteBatch(
                segment.Track.Id,
                selected,
                old,
                replacement);
        });

    public static IProjectEditCommand DeleteLogicalNotes(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> logicalNoteIds) =>
        Command("Delete logical notes", project =>
        {
            SegmentLocation segment = FindSegment(project, segmentId);
            SelectedLogicalNote[] selected = SelectLogicalNotes(
                segment.Segment,
                logicalNoteIds);
            return Prepared(
                hasChanges: true,
                TrackChange(segment.Track.Id),
                _ =>
                {
                    foreach (SelectedLogicalNote value in selected)
                    {
                        RemoveRequired(segment.Segment.Notes, value.Note, "Logical Note");
                    }
                },
                _ =>
                {
                    foreach (SelectedLogicalNote value in selected.OrderBy(value => value.Index))
                    {
                        InsertAt(
                            segment.Segment.Notes,
                            value.Index,
                            value.Note,
                            "Logical Note");
                    }
                });
        });

    public static IProjectEditCommand DuplicateLogicalNotes(
        MidoraId sourceSegmentId,
        IReadOnlyCollection<MidoraId> logicalNoteIds,
        MidoraId targetSegmentId,
        long newEarliestStartTick) =>
        Command("Duplicate logical notes", project =>
        {
            if (newEarliestStartTick < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(newEarliestStartTick));
            }
            SegmentLocation source = FindSegment(project, sourceSegmentId);
            SegmentLocation target = FindSegment(project, targetSegmentId);
            SelectedLogicalNote[] selected = SelectLogicalNotes(
                source.Segment,
                logicalNoteIds);
            long earliest = selected.Min(value => value.Note.StartTick);
            long delta = checked(newEarliestStartTick - earliest);
            LogicalNoteValue[] snapshots = selected
                .Select(value => Snapshot(value.Note) with
                {
                    StartTick = checked(value.Note.StartTick + delta)
                })
                .ToArray();
            ValidateLogicalNoteBatch(snapshots);
            LogicalNote[]? copies = null;
            int insertionIndex = target.Segment.Notes.Count;
            return Prepared(
                hasChanges: true,
                TrackChange(source.Track.Id, target.Track.Id),
                owner =>
                {
                    if (copies is null)
                    {
                        copies = snapshots.Select(value => new LogicalNote(owner)
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
                            "Logical Note copy");
                    }
                },
                _ =>
                {
                    if (copies is null)
                    {
                        throw new InvalidOperationException(
                            "Logical Note copies do not exist before the first Apply.");
                    }
                    foreach (LogicalNote copy in copies)
                    {
                        RemoveRequired(target.Segment.Notes, copy, "Logical Note copy");
                    }
                });
        });

    public static IProjectEditCommand MoveSegments(
        IReadOnlyCollection<MidoraId> segmentIds,
        MidoraId primarySegmentId,
        MidoraId targetPrimaryTrackId,
        long newPrimaryStartTick) =>
        Command("Move segments", project =>
        {
            SegmentBatchPlacement[] placements = PlanSegmentBatch(
                project,
                segmentIds,
                primarySegmentId,
                targetPrimaryTrackId,
                newPrimaryStartTick,
                isCopy: false);
            bool changed = placements.Any(value =>
                !ReferenceEquals(value.Source.Track, value.TargetTrack)
                || value.Source.Segment.ProjectStartTick != value.NewProjectStartTick);
            SegmentOriginalPlacement[] original = placements.Select(value =>
                new SegmentOriginalPlacement(
                    value.Source.Track,
                    value.Source.Segment,
                    value.Source.Index,
                    value.Source.Segment.ProjectStartTick)).ToArray();
            ProjectChangeSet changes = TrackChange(placements
                .SelectMany(value => new[] { value.Source.Track.Id, value.TargetTrack.Id })
                .Distinct()
                .ToArray());
            return Prepared(
                changed,
                changes,
                _ =>
                {
                    foreach (SegmentOriginalPlacement value in original)
                    {
                        RemoveRequired(value.Track.Segments, value.Segment, "Segment");
                    }
                    foreach (SegmentBatchPlacement value in placements)
                    {
                        value.Source.Segment.ProjectStartTick = value.NewProjectStartTick;
                        InsertSegmentByTime(value.TargetTrack.Segments, value.Source.Segment);
                    }
                },
                _ =>
                {
                    foreach (SegmentBatchPlacement value in placements)
                    {
                        RemoveRequired(value.TargetTrack.Segments, value.Source.Segment, "Segment");
                    }
                    foreach (SegmentOriginalPlacement value in original)
                    {
                        value.Segment.ProjectStartTick = value.ProjectStartTick;
                    }
                    foreach (IGrouping<LogicalTrack, SegmentOriginalPlacement> group in original
                        .GroupBy(value => value.Track))
                    {
                        foreach (SegmentOriginalPlacement value in group.OrderBy(value => value.Index))
                        {
                            InsertAt(value.Track.Segments, value.Index, value.Segment, "Segment");
                        }
                    }
                });
        });

    public static IProjectEditCommand DuplicateSegments(
        IReadOnlyCollection<MidoraId> segmentIds,
        MidoraId primarySegmentId,
        MidoraId targetPrimaryTrackId,
        long newPrimaryStartTick) =>
        Command("Duplicate segments", project =>
        {
            SegmentBatchPlacement[] placements = PlanSegmentBatch(
                project,
                segmentIds,
                primarySegmentId,
                targetPrimaryTrackId,
                newPrimaryStartTick,
                isCopy: true);
            ProjectChangeSet changes = TrackChange(placements
                .Select(value => value.TargetTrack.Id)
                .Distinct()
                .ToArray());
            Segment[]? copies = null;
            return Prepared(
                hasChanges: true,
                changes,
                owner =>
                {
                    if (copies is null)
                    {
                        Segment[] created = new Segment[placements.Length];
                        for (int index = 0; index < placements.Length; index++)
                        {
                            created[index] = SegmentEditing.Duplicate(
                                owner,
                                placements[index].Source.Segment);
                            created[index].ProjectStartTick = placements[index].NewProjectStartTick;
                        }
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
                            "Segment copies do not exist before the first Apply.");
                    }
                    for (int index = 0; index < copies.Length; index++)
                    {
                        RemoveRequired(
                            placements[index].TargetTrack.Segments,
                            copies[index],
                            "Segment copy");
                    }
                });
        });

    public static IProjectEditCommand DeleteSegments(
        IReadOnlyCollection<MidoraId> segmentIds) =>
        Command("Delete segments", project =>
        {
            ArgumentNullException.ThrowIfNull(segmentIds);
            if (segmentIds.Count == 0)
            {
                throw new ArgumentException(
                    "At least one Segment must be selected.",
                    nameof(segmentIds));
            }
            HashSet<MidoraId> requested = [];
            SegmentOriginalPlacement[] selected = segmentIds.Select(id =>
            {
                if (id == default || !requested.Add(id))
                {
                    throw new ArgumentException(
                        "Segment selections must contain distinct valid stable IDs.",
                        nameof(segmentIds));
                }
                SegmentLocation location = FindSegment(project, id);
                return new SegmentOriginalPlacement(
                    location.Track,
                    location.Segment,
                    location.Index,
                    location.Segment.ProjectStartTick);
            }).ToArray();
            return Prepared(
                hasChanges: true,
                TrackChange(selected.Select(value => value.Track.Id).Distinct().ToArray()),
                _ =>
                {
                    foreach (SegmentOriginalPlacement value in selected)
                    {
                        RemoveRequired(value.Track.Segments, value.Segment, "Segment");
                    }
                },
                _ =>
                {
                    foreach (IGrouping<LogicalTrack, SegmentOriginalPlacement> group in selected
                        .GroupBy(value => value.Track))
                    {
                        foreach (SegmentOriginalPlacement value in group.OrderBy(value => value.Index))
                        {
                            InsertAt(value.Track.Segments, value.Index, value.Segment, "Segment");
                        }
                    }
                });
        });

    private static SegmentBatchPlacement[] PlanSegmentBatch(
        MidoraProject project,
        IReadOnlyCollection<MidoraId> segmentIds,
        MidoraId primarySegmentId,
        MidoraId targetPrimaryTrackId,
        long newPrimaryStartTick,
        bool isCopy)
    {
        ArgumentNullException.ThrowIfNull(segmentIds);
        if (segmentIds.Count == 0)
        {
            throw new ArgumentException(
                "At least one Segment must be selected.",
                nameof(segmentIds));
        }
        if (newPrimaryStartTick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(newPrimaryStartTick));
        }
        HashSet<MidoraId> requested = [];
        foreach (MidoraId id in segmentIds)
        {
            if (id == default || !requested.Add(id))
            {
                throw new ArgumentException(
                    "Segment selections must contain distinct valid stable IDs.",
                    nameof(segmentIds));
            }
        }
        SelectedSegment[] selected = requested
            .Select(id =>
            {
                SegmentLocation location = FindSegment(project, id);
                return new SelectedSegment(
                    location,
                    project.Tracks.IndexOf(location.Track));
            })
            .OrderBy(value => value.TrackIndex)
            .ThenBy(value => value.Location.Segment.ProjectStartTick)
            .ThenBy(value => value.Location.Segment.Id)
            .ToArray();
        SelectedSegment primary = selected
            .SingleOrDefault(value => value.Location.Segment.Id == primarySegmentId);
        if (primary.Location.Segment is null)
        {
            throw new ArgumentException(
                "The primary Segment must belong to the batch selection.",
                nameof(primarySegmentId));
        }
        LogicalTrack targetPrimaryTrack = FindTrack(project, targetPrimaryTrackId);
        int targetPrimaryTrackIndex = project.Tracks.IndexOf(targetPrimaryTrack);
        long tickDelta = checked(
            newPrimaryStartTick - primary.Location.Segment.ProjectStartTick);
        SegmentBatchPlacement[] placements = selected.Select(value =>
        {
            int targetTrackIndex = checked(
                targetPrimaryTrackIndex + value.TrackIndex - primary.TrackIndex);
            if ((uint)targetTrackIndex >= (uint)project.Tracks.Count)
            {
                throw new InvalidOperationException(
                    "The Segment batch cannot preserve its relative Track offsets at the target.");
            }
            long start = checked(value.Location.Segment.ProjectStartTick + tickDelta);
            ValidateSegmentRange(
                start,
                value.Location.Segment.LengthTicks,
                value.Location.Segment.ContentOffsetTick);
            return new SegmentBatchPlacement(
                value.Location,
                project.Tracks[targetTrackIndex],
                start);
        }).ToArray();

        HashSet<Segment> selectedObjects = selected
            .Select(value => value.Location.Segment)
            .ToHashSet<Segment>(ReferenceEqualityComparer.Instance);
        foreach (IGrouping<LogicalTrack, SegmentBatchPlacement> group in placements
            .GroupBy(value => value.TargetTrack))
        {
            SegmentBatchPlacement[] candidates = group
                .OrderBy(value => value.NewProjectStartTick)
                .ThenBy(value => value.Source.Segment.Id)
                .ToArray();
            for (int index = 0; index < candidates.Length; index++)
            {
                TickRange range = new(
                    candidates[index].NewProjectStartTick,
                    checked(candidates[index].NewProjectStartTick
                        + candidates[index].Source.Segment.LengthTicks));
                if (group.Key.Segments.Any(existing =>
                    (isCopy || !selectedObjects.Contains(existing))
                    && range.Intersects(existing.ProjectRange)))
                {
                    throw new InvalidOperationException(
                        "The Segment batch would overlap an existing Segment.");
                }
                if (index != 0)
                {
                    SegmentBatchPlacement previous = candidates[index - 1];
                    long previousEnd = checked(previous.NewProjectStartTick
                        + previous.Source.Segment.LengthTicks);
                    if (previousEnd > candidates[index].NewProjectStartTick)
                    {
                        throw new InvalidOperationException(
                            "Segments in the batch would overlap each other.");
                    }
                }
            }
        }
        return placements;
    }

    private static IPreparedProjectEdit PrepareLogicalNoteBatch(
        MidoraId trackId,
        SelectedLogicalNote[] selected,
        LogicalNoteValue[] old,
        LogicalNoteValue[] replacement) =>
        Prepared(
            old.Where((value, index) => value != replacement[index]).Any(),
            TrackChange(trackId),
            _ => SetLogicalNoteBatch(selected, replacement),
            _ => SetLogicalNoteBatch(selected, old));

    private static void SetLogicalNoteBatch(
        SelectedLogicalNote[] selected,
        LogicalNoteValue[] values)
    {
        for (int index = 0; index < selected.Length; index++)
        {
            SetLogicalNote(selected[index].Note, values[index]);
        }
    }

    private static SelectedLogicalNote[] SelectLogicalNotes(
        Segment segment,
        IReadOnlyCollection<MidoraId> logicalNoteIds)
    {
        ArgumentNullException.ThrowIfNull(logicalNoteIds);
        if (logicalNoteIds.Count == 0)
        {
            throw new ArgumentException(
                "At least one Logical Note must be selected.",
                nameof(logicalNoteIds));
        }
        HashSet<MidoraId> requested = [];
        foreach (MidoraId id in logicalNoteIds)
        {
            if (id == default || !requested.Add(id))
            {
                throw new ArgumentException(
                    "Logical Note selections must contain distinct valid stable IDs.",
                    nameof(logicalNoteIds));
            }
        }
        SelectedLogicalNote[] selected = segment.Notes
            .Select((value, index) => new SelectedLogicalNote(value, index))
            .Where(value => requested.Contains(value.Note.Id))
            .ToArray();
        if (selected.Length != requested.Count)
        {
            throw new ArgumentException(
                "Every selected Logical Note must belong to the target Segment.",
                nameof(logicalNoteIds));
        }
        return selected;
    }

    private static LogicalNoteValue Snapshot(LogicalNote value) =>
        new(value.StartTick, value.LengthTicks, value.Note, value.Velocity);

    private static void ValidateLogicalNoteBatch(IEnumerable<LogicalNoteValue> values)
    {
        foreach (LogicalNoteValue value in values)
        {
            ValidateLogicalNote(
                value.StartTick,
                value.LengthTicks,
                value.Note,
                value.Velocity);
        }
    }

    private readonly record struct SelectedLogicalNote(LogicalNote Note, int Index);
    private readonly record struct SelectedSegment(SegmentLocation Location, int TrackIndex);
    private readonly record struct SegmentBatchPlacement(
        SegmentLocation Source,
        LogicalTrack TargetTrack,
        long NewProjectStartTick);
    private readonly record struct SegmentOriginalPlacement(
        LogicalTrack Track,
        Segment Segment,
        int Index,
        long ProjectStartTick);
}
