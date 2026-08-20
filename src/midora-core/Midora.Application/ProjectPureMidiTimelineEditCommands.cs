using Midora.Domain;

namespace Midora.Application;

public sealed record DirectMidiEventPointEdit(long Tick, int Data1, int Data2);

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand DeleteArrangementSegments(
        IReadOnlyCollection<MidoraId> segmentIds) =>
        Command("Delete Arrangement Segments", project =>
        {
            ArgumentNullException.ThrowIfNull(segmentIds);
            HashSet<MidoraId> requested = segmentIds.ToHashSet();
            if (requested.Count != segmentIds.Count || requested.Contains(default))
                throw new ArgumentException("Arrangement Segment IDs must be distinct and valid.", nameof(segmentIds));
            (LogicalTrack Track, Segment Segment, int Index)[] logical = project.Tracks
                .SelectMany(track => track.Segments.Select((segment, index) => (track, segment, index)))
                .Where(value => requested.Remove(value.segment.Id))
                .Select(value => (value.track, value.segment, value.index))
                .ToArray();
            (PureMidiTrack Track, MidiSegment Segment, int Index)[] midi = project.PureMidiTracks
                .SelectMany(track => track.Segments.Select((segment, index) => (track, segment, index)))
                .Where(value => requested.Remove(value.segment.Id))
                .Select(value => (value.track, value.segment, value.index))
                .ToArray();
            if (requested.Count != 0 || logical.Length + midi.Length == 0)
                throw new ArgumentOutOfRangeException(nameof(segmentIds));
            ProjectChangeSet changes = new();
            changes.TrackIds.UnionWith(logical.Select(value => value.Track.Id));
            changes.PureMidiTrackIds.UnionWith(midi.Select(value => value.Track.Id));
            return Prepared(
                true,
                changes,
                _ =>
                {
                    foreach (var value in logical) RemoveRequired(value.Track.Segments, value.Segment, "Segment");
                    foreach (var value in midi) RemoveRequired(value.Track.Segments, value.Segment, "MIDI Segment");
                },
                _ =>
                {
                    foreach (var value in logical.OrderBy(value => value.Index))
                        InsertAt(value.Track.Segments, value.Index, value.Segment, "Segment");
                    foreach (var value in midi.OrderBy(value => value.Index))
                        InsertAt(value.Track.Segments, value.Index, value.Segment, "MIDI Segment");
                });
        });

    public static IProjectEditCommand DeleteMidiSegments(
        IReadOnlyCollection<MidoraId> segmentIds) =>
        Command("Delete MIDI Segments", project =>
        {
            MidiSegmentSelection[] selected = SelectMidiSegments(project, segmentIds);
            return Prepared(
                true,
                PureMidiTrackChange(selected.Select(value => value.Track.Id).Distinct().ToArray()),
                _ =>
                {
                    foreach (MidiSegmentSelection value in selected)
                        RemoveRequired(value.Track.Segments, value.Segment, "MIDI Segment");
                },
                _ =>
                {
                    foreach (IGrouping<PureMidiTrack, MidiSegmentSelection> group in selected.GroupBy(value => value.Track))
                        foreach (MidiSegmentSelection value in group.OrderBy(value => value.Index))
                            InsertAt(value.Track.Segments, value.Index, value.Segment, "MIDI Segment");
                });
        });

    public static IProjectEditCommand MoveMidiSegments(
        IReadOnlyCollection<MidoraId> segmentIds,
        MidoraId primarySegmentId,
        MidoraId targetTrackId,
        long newPrimaryStartTick) =>
        TransformMidiSegments(
            "Move MIDI Segments",
            segmentIds,
            primarySegmentId,
            targetTrackId,
            newPrimaryStartTick,
            duplicate: false);

    public static IProjectEditCommand DuplicateMidiSegments(
        IReadOnlyCollection<MidoraId> segmentIds,
        MidoraId primarySegmentId,
        MidoraId targetTrackId,
        long newPrimaryStartTick) =>
        TransformMidiSegments(
            "Duplicate MIDI Segments",
            segmentIds,
            primarySegmentId,
            targetTrackId,
            newPrimaryStartTick,
            duplicate: true);

    private static IProjectEditCommand TransformMidiSegments(
        string name,
        IReadOnlyCollection<MidoraId> segmentIds,
        MidoraId primarySegmentId,
        MidoraId targetTrackId,
        long newPrimaryStartTick,
        bool duplicate) =>
        Command(name, project =>
        {
            if (newPrimaryStartTick < 0) throw new ArgumentOutOfRangeException(nameof(newPrimaryStartTick));
            MidiSegmentSelection[] selected = SelectMidiSegments(project, segmentIds);
            MidiSegmentSelection primary = selected.SingleOrDefault(value => value.Segment.Id == primarySegmentId);
            if (primary.Segment is null)
                throw new ArgumentException("The primary MIDI Segment must be selected.", nameof(primarySegmentId));
            int primaryTrackIndex = FindArrangementTrackIndex(
                project,
                ArrangementTrackKind.PureMidiTrack,
                primary.Track.Id);
            PureMidiTrack targetPrimaryTrack = FindPureMidiTrack(project, targetTrackId);
            int targetPrimaryTrackIndex = FindArrangementTrackIndex(
                project,
                ArrangementTrackKind.PureMidiTrack,
                targetPrimaryTrack.Id);
            long delta = checked(newPrimaryStartTick - primary.Segment.ProjectStartTick);
            MidiSegmentBatchPlacement[] placements = selected.Select(value =>
            {
                int sourceTrackIndex = FindArrangementTrackIndex(
                    project,
                    ArrangementTrackKind.PureMidiTrack,
                    value.Track.Id);
                int targetTrackIndex = checked(
                    targetPrimaryTrackIndex + sourceTrackIndex - primaryTrackIndex);
                if ((uint)targetTrackIndex >= (uint)project.ArrangementTracks.Count
                    || project.ArrangementTracks[targetTrackIndex] is not
                        { Kind: ArrangementTrackKind.PureMidiTrack } targetReference)
                {
                    throw new InvalidOperationException(
                        "The MIDI Segment batch cannot preserve its relative Arrangement lane offsets at the target.");
                }
                long start = checked(value.Segment.ProjectStartTick + delta);
                if (start < 0)
                    throw new InvalidOperationException("The MIDI Segment move would cross tick 0.");
                return new MidiSegmentBatchPlacement(
                    value,
                    FindPureMidiTrack(project, targetReference.TrackId),
                    start);
            }).ToArray();
            HashSet<MidiSegment> moving = duplicate
                ? []
                : selected.Select(value => value.Segment).ToHashSet();
            ValidateMidiSegmentPlacements(placements, moving);
            MidiSegment[]? copies = null;
            return Prepared(
                true,
                PureMidiTrackChange(placements
                    .SelectMany(value => new[] { value.Source.Track.Id, value.TargetTrack.Id })
                    .Distinct()
                    .ToArray()),
                owner =>
                {
                    if (duplicate)
                    {
                        copies ??= placements.Select(value =>
                            CloneMidiSegment(owner, value.Source.Segment, value.Start)).ToArray();
                        for (int index = 0; index < copies.Length; index++)
                            InsertMidiSegmentByTime(placements[index].TargetTrack.Segments, copies[index]);
                        return;
                    }
                    foreach (MidiSegmentSelection value in selected)
                        RemoveRequired(value.Track.Segments, value.Segment, "MIDI Segment");
                    foreach (MidiSegmentBatchPlacement placement in placements)
                    {
                        placement.Source.Segment.ProjectStartTick = placement.Start;
                        InsertMidiSegmentByTime(placement.TargetTrack.Segments, placement.Source.Segment);
                    }
                },
                _ =>
                {
                    if (duplicate)
                    {
                        for (int index = 0; index < (copies?.Length ?? 0); index++)
                            RemoveRequired(
                                placements[index].TargetTrack.Segments,
                                copies![index],
                                "MIDI Segment copy");
                        return;
                    }
                    foreach (MidiSegmentBatchPlacement placement in placements)
                        RemoveRequired(
                            placement.TargetTrack.Segments,
                            placement.Source.Segment,
                            "MIDI Segment");
                    foreach (MidiSegmentSelection value in selected)
                        value.Segment.ProjectStartTick = value.OriginalStartTick;
                    foreach (IGrouping<PureMidiTrack, MidiSegmentSelection> group in selected.GroupBy(value => value.Track))
                    {
                        foreach (MidiSegmentSelection value in group.OrderBy(value => value.Index))
                            InsertAt(value.Track.Segments, value.Index, value.Segment, "MIDI Segment");
                    }
                });
        });

    public static IProjectEditCommand AdjustMidiSegmentEdges(
        IReadOnlyCollection<MidoraId> segmentIds,
        long startDelta,
        long endDelta) =>
        Command("Adjust MIDI Segment edges", project =>
        {
            if (startDelta != 0 && endDelta != 0)
                throw new ArgumentException("Exactly one MIDI Segment edge may change.");
            MidiSegmentSelection[] selected = SelectMidiSegments(project, segmentIds);
            long boundedStartDelta = startDelta < 0
                ? Math.Max(startDelta, -selected.Min(value => value.Segment.ProjectStartTick))
                : startDelta;
            MidiSegmentEdgeEdit[] edits = selected.Select(value =>
            {
                SegmentWindow old = new(
                    value.Segment.ProjectStartTick,
                    value.Segment.LengthTicks,
                    value.Segment.ContentOffsetTick);
                SegmentEdgeAdjustment adjustment = AdjustSegmentEdgesSaturated(old, boundedStartDelta, endDelta);
                return new MidiSegmentEdgeEdit(value, old, adjustment.Window, adjustment.ContentShift);
            }).ToArray();
            ValidateMidiSegmentEdgeEdits(edits);
            return Prepared(
                edits.Any(value => value.Old != value.Replacement),
                PureMidiTrackChange(edits.Select(value => value.Selection.Track.Id).Distinct().ToArray()),
                _ =>
                {
                    foreach (MidiSegmentEdgeEdit edit in edits)
                    {
                        ShiftMidiSegmentContent(edit.Selection.Segment, edit.ContentShift);
                        SetMidiSegmentWindow(edit.Selection.Segment, edit.Replacement);
                    }
                },
                _ =>
                {
                    foreach (MidiSegmentEdgeEdit edit in edits)
                    {
                        SetMidiSegmentWindow(edit.Selection.Segment, edit.Old);
                        ShiftMidiSegmentContent(edit.Selection.Segment, -edit.ContentShift);
                    }
                });
        });

    public static IProjectEditCommand DeleteDirectMidiNotes(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds) =>
        Command("Delete Direct MIDI Notes", project =>
        {
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            DirectNoteSelection[] selected = SelectDirectNotes(location.Segment, noteIds);
            return Prepared(
                true,
                PureMidiTrackChange(location.Track.Id),
                _ =>
                {
                    foreach (DirectNoteSelection value in selected)
                        RemoveRequired(location.Segment.Notes, value.Note, "Direct MIDI Note");
                },
                _ =>
                {
                    foreach (DirectNoteSelection value in selected.OrderBy(value => value.Index))
                        InsertAt(location.Segment.Notes, value.Index, value.Note, "Direct MIDI Note");
                });
        });

    public static IProjectEditCommand MoveDirectMidiNotes(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        long tickDelta,
        int keyDelta) =>
        ChangeDirectMidiNotes(
            "Move Direct MIDI Notes",
            segmentId,
            noteIds,
            value => value with
            {
                StartTick = checked(value.StartTick + tickDelta),
                Key = checked(value.Key + keyDelta)
            });

    public static IProjectEditCommand AdjustDirectMidiNoteEdges(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        long startDelta,
        long endDelta) =>
        ChangeDirectMidiNotes(
            "Adjust Direct MIDI Note edges",
            segmentId,
            noteIds,
            value =>
            {
                long oldEnd = checked(value.StartTick + value.LengthTicks);
                long start = startDelta == 0
                    ? value.StartTick
                    : Math.Clamp(checked(value.StartTick + startDelta), 0, checked(oldEnd - 1));
                long end = endDelta == 0
                    ? oldEnd
                    : Math.Max(checked(start + 1), checked(oldEnd + endDelta));
                return value with { StartTick = start, LengthTicks = checked(end - start) };
            });

    public static IProjectEditCommand PaintDirectMidiNoteVelocities(
        MidoraId segmentId,
        IReadOnlyDictionary<MidoraId, int> velocities) =>
        Command("Paint Direct MIDI Note velocities", project =>
        {
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            DirectNoteSelection[] selected = SelectDirectNotes(location.Segment, velocities.Keys.ToArray());
            int[] old = selected.Select(value => value.Note.NoteOnVelocity).ToArray();
            int[] replacement = selected.Select(value => velocities[value.Note.Id]).ToArray();
            if (replacement.Any(value => value is < 1 or > 127))
                throw new ArgumentOutOfRangeException(nameof(velocities));
            return Prepared(
                old.Where((value, index) => value != replacement[index]).Any(),
                PureMidiTrackChange(location.Track.Id),
                _ =>
                {
                    for (int index = 0; index < selected.Length; index++)
                        selected[index].Note.NoteOnVelocity = replacement[index];
                },
                _ =>
                {
                    for (int index = 0; index < selected.Length; index++)
                        selected[index].Note.NoteOnVelocity = old[index];
                });
        });

    public static IProjectEditCommand DuplicateDirectMidiNotes(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        long tickDelta,
        int keyDelta) =>
        Command("Duplicate Direct MIDI Notes", project =>
        {
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            DirectNoteSelection[] selected = SelectDirectNotes(location.Segment, noteIds);
            DirectNoteValue[] values = selected.Select(value => SnapshotDirectNote(value.Note) with
            {
                StartTick = checked(value.Note.StartTick + tickDelta),
                Key = checked(value.Note.Key + keyDelta)
            }).ToArray();
            foreach (DirectNoteValue value in values)
                ValidateDirectMidiNote(value.StartTick, value.LengthTicks, value.Key, value.NoteOnVelocity, value.NoteOffVelocity);
            DirectMidiNote[]? copies = null;
            return Prepared(
                true,
                PureMidiTrackChange(location.Track.Id),
                owner =>
                {
                    copies ??= values.Select(value => CreateDirectNote(owner, value)).ToArray();
                    location.Segment.Notes.AddRange(copies);
                },
                _ =>
                {
                    foreach (DirectMidiNote copy in copies ?? []) location.Segment.Notes.Remove(copy);
                });
        });

    private static IProjectEditCommand ChangeDirectMidiNotes(
        string name,
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        Func<DirectNoteValue, DirectNoteValue> transform) =>
        ChangeDirectMidiNotes(
            name,
            segmentId,
            noteIds,
            values => values.Select(transform).ToArray());

    private static IProjectEditCommand ChangeDirectMidiNotes(
        string name,
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        Func<IReadOnlyList<DirectNoteValue>, DirectNoteValue[]> transform) =>
        Command(name, project =>
        {
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            DirectNoteSelection[] selected = SelectDirectNotes(location.Segment, noteIds);
            DirectNoteValue[] old = selected.Select(value => SnapshotDirectNote(value.Note)).ToArray();
            DirectNoteValue[] replacement = transform(old);
            if (replacement.Length != old.Length)
                throw new InvalidOperationException("A Direct MIDI Note transform returned the wrong result count.");
            bool[] discarded = new bool[selected.Length];
            for (int index = 0; index < replacement.Length; index++)
            {
                DirectNoteValue value = replacement[index];
                if (value.Key is < 0 or > 127)
                {
                    discarded[index] = true;
                    continue;
                }
                ValidateDirectMidiNote(value.StartTick, value.LengthTicks, value.Key, value.NoteOnVelocity, value.NoteOffVelocity);
            }
            return Prepared(
                old.Where((value, index) => value != replacement[index] || discarded[index]).Any(),
                PureMidiTrackChange(location.Track.Id),
                _ =>
                {
                    for (int index = 0; index < selected.Length; index++)
                    {
                        if (discarded[index]) location.Segment.Notes.Remove(selected[index].Note);
                        else ApplyDirectNote(selected[index].Note, replacement[index]);
                    }
                },
                _ =>
                {
                    for (int index = 0; index < selected.Length; index++)
                        ApplyDirectNote(selected[index].Note, old[index]);
                    foreach (DirectNoteSelection value in selected.Where((_, index) => discarded[index]).OrderBy(value => value.Index))
                        InsertAt(location.Segment.Notes, value.Index, value.Note, "Direct MIDI Note");
                });
        });

    public static IProjectEditCommand UpsertDirectMidiEventPoints(
        MidoraId segmentId,
        DirectMidiChannelEventKind kind,
        int laneData1,
        IReadOnlyCollection<DirectMidiEventPointEdit> points) =>
        Command("Draw Direct MIDI Event points", project =>
        {
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            DirectMidiEventPointEdit[] edits = points.OrderBy(value => value.Tick).ToArray();
            if (edits.Length == 0 || edits.Select(value => value.Tick).Distinct().Count() != edits.Length)
                throw new ArgumentException("Direct MIDI Event point ticks must be distinct.", nameof(points));
            DirectMidiChannelEvent[] existing = edits
                .Select(edit => FindDirectEventAtTick(location.Segment, kind, laneData1, edit.Tick))
                .Where(static value => value is not null)
                .Cast<DirectMidiChannelEvent>()
                .ToArray();
            DirectMidiEventValue[] old = existing.Select(SnapshotDirectEvent).ToArray();
            DirectMidiChannelEvent[]? created = null;
            return Prepared(
                true,
                PureMidiTrackChange(location.Track.Id),
                owner =>
                {
                    foreach (DirectMidiEventPointEdit edit in edits)
                    {
                        ValidateDirectMidiEvent(edit.Tick, kind, edit.Data1, edit.Data2);
                        DirectMidiChannelEvent? target = FindDirectEventAtTick(location.Segment, kind, laneData1, edit.Tick);
                        if (target is not null)
                        {
                            target.Data1 = edit.Data1;
                            target.Data2 = edit.Data2;
                        }
                    }
                    created ??= edits
                        .Where(edit => old.All(value => value.Tick != edit.Tick))
                        .Select(edit => new DirectMidiChannelEvent(owner)
                        {
                            Tick = edit.Tick,
                            Kind = kind,
                            Data1 = edit.Data1,
                            Data2 = edit.Data2,
                            Order = owner.NextStableId
                        })
                        .ToArray();
                    location.Segment.ChannelEvents.AddRange(created);
                },
                _ =>
                {
                    foreach (DirectMidiChannelEvent value in created ?? []) location.Segment.ChannelEvents.Remove(value);
                    for (int index = 0; index < existing.Length; index++) ApplyDirectEvent(existing[index], old[index]);
                });
        });

    public static IProjectEditCommand AdjustDirectMidiEventPoints(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> eventIds,
        long tickDelta,
        int data1Delta,
        int data2Delta,
        bool duplicate) =>
        Command(duplicate ? "Duplicate Direct MIDI Event points" : "Move Direct MIDI Event points", project =>
        {
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            DirectEventSelection[] selected = SelectDirectEvents(location.Segment, eventIds);
            DirectMidiEventValue[] values = selected.Select(value => SnapshotDirectEvent(value.Event) with
            {
                Tick = checked(value.Event.Tick + tickDelta),
                Data1 = checked(value.Event.Data1 + data1Delta),
                Data2 = checked(value.Event.Data2 + data2Delta)
            }).ToArray();
            foreach (DirectMidiEventValue value in values)
                ValidateDirectMidiEvent(value.Tick, value.Kind, value.Data1, value.Data2);
            DirectMidiChannelEvent[]? copies = null;
            return Prepared(
                true,
                PureMidiTrackChange(location.Track.Id),
                owner =>
                {
                    if (duplicate)
                    {
                        copies ??= values.Select(value => CreateDirectEvent(owner, value)).ToArray();
                        location.Segment.ChannelEvents.AddRange(copies);
                    }
                    else
                    {
                        for (int index = 0; index < selected.Length; index++)
                            ApplyDirectEvent(selected[index].Event, values[index]);
                    }
                },
                _ =>
                {
                    if (duplicate)
                    {
                        foreach (DirectMidiChannelEvent copy in copies ?? [])
                            location.Segment.ChannelEvents.Remove(copy);
                    }
                    else
                    {
                        for (int index = 0; index < selected.Length; index++) ApplyDirectEvent(selected[index].Event, selected[index].Original);
                    }
                });
        });

    public static IProjectEditCommand DeleteDirectMidiEvents(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> eventIds) =>
        Command("Delete Direct MIDI Events", project =>
        {
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            DirectEventSelection[] selected = SelectDirectEvents(location.Segment, eventIds);
            return Prepared(
                true,
                PureMidiTrackChange(location.Track.Id),
                _ =>
                {
                    foreach (DirectEventSelection value in selected)
                        RemoveRequired(location.Segment.ChannelEvents, value.Event, "Direct MIDI Event");
                },
                _ =>
                {
                    foreach (DirectEventSelection value in selected.OrderBy(value => value.Index))
                        InsertAt(location.Segment.ChannelEvents, value.Index, value.Event, "Direct MIDI Event");
                });
        });

    public static IProjectEditCommand AdjustOpaqueMidiEvents(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> eventIds,
        long tickDelta,
        bool duplicate) =>
        Command(duplicate ? "Duplicate imported MIDI events" : "Move imported MIDI events", project =>
        {
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            OpaqueEventSelection[] selected = SelectOpaqueEvents(location.Segment, eventIds);
            OpaqueMidiEventValue[] values = selected.Select(value => SnapshotOpaqueEvent(value.Event) with
            {
                Tick = checked(value.Event.Tick + tickDelta)
            }).ToArray();
            if (values.Any(value => value.Tick < 0))
                throw new InvalidOperationException("Imported MIDI events cannot move before tick 0.");
            OpaqueMidiEvent[]? copies = null;
            return Prepared(
                tickDelta != 0 || duplicate,
                PureMidiTrackChange(location.Track.Id),
                owner =>
                {
                    if (duplicate)
                    {
                        copies ??= values.Select(value => CreateOpaqueEvent(owner, value)).ToArray();
                        location.Segment.OpaqueEvents.AddRange(copies);
                    }
                    else
                    {
                        for (int index = 0; index < selected.Length; index++)
                            selected[index].Event.Tick = values[index].Tick;
                    }
                },
                _ =>
                {
                    if (duplicate)
                    {
                        foreach (OpaqueMidiEvent copy in copies ?? [])
                            location.Segment.OpaqueEvents.Remove(copy);
                    }
                    else
                    {
                        foreach (OpaqueEventSelection value in selected)
                            value.Event.Tick = value.Original.Tick;
                    }
                });
        });

    public static IProjectEditCommand DeleteOpaqueMidiEvents(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> eventIds) =>
        Command("Delete imported MIDI events", project =>
        {
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            OpaqueEventSelection[] selected = SelectOpaqueEvents(location.Segment, eventIds);
            return Prepared(
                true,
                PureMidiTrackChange(location.Track.Id),
                _ =>
                {
                    foreach (OpaqueEventSelection value in selected)
                        RemoveRequired(location.Segment.OpaqueEvents, value.Event, "imported MIDI event");
                },
                _ =>
                {
                    foreach (OpaqueEventSelection value in selected.OrderBy(value => value.Index))
                        InsertAt(location.Segment.OpaqueEvents, value.Index, value.Event, "imported MIDI event");
                });
        });

    private static MidiSegmentSelection[] SelectMidiSegments(
        MidoraProject project,
        IReadOnlyCollection<MidoraId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) throw new ArgumentException("At least one MIDI Segment is required.", nameof(ids));
        HashSet<MidoraId> distinct = [];
        return ids.Select(id =>
        {
            if (id == default || !distinct.Add(id)) throw new ArgumentException("MIDI Segment IDs must be distinct.", nameof(ids));
            MidiSegmentLocation value = FindMidiSegment(project, id);
            return new MidiSegmentSelection(value.Track, value.Segment, value.Index, value.Segment.ProjectStartTick);
        }).ToArray();
    }

    private static DirectNoteSelection[] SelectDirectNotes(MidiSegment segment, IReadOnlyCollection<MidoraId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) throw new ArgumentException("At least one Direct MIDI Note is required.", nameof(ids));
        HashSet<MidoraId> distinct = [];
        return ids.Select(id =>
        {
            if (id == default || !distinct.Add(id)) throw new ArgumentException("Direct MIDI Note IDs must be distinct.", nameof(ids));
            int index = segment.Notes.FindIndex(value => value.Id == id);
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(ids));
            return new DirectNoteSelection(segment.Notes[index], index);
        }).ToArray();
    }

    private static DirectEventSelection[] SelectDirectEvents(MidiSegment segment, IReadOnlyCollection<MidoraId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) throw new ArgumentException("At least one Direct MIDI Event is required.", nameof(ids));
        HashSet<MidoraId> distinct = [];
        return ids.Select(id =>
        {
            if (id == default || !distinct.Add(id)) throw new ArgumentException("Direct MIDI Event IDs must be distinct.", nameof(ids));
            int index = segment.ChannelEvents.FindIndex(value => value.Id == id);
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(ids));
            DirectMidiChannelEvent value = segment.ChannelEvents[index];
            return new DirectEventSelection(value, index, SnapshotDirectEvent(value));
        }).ToArray();
    }

    private static OpaqueEventSelection[] SelectOpaqueEvents(
        MidiSegment segment,
        IReadOnlyCollection<MidoraId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) throw new ArgumentException("At least one imported MIDI event is required.", nameof(ids));
        HashSet<MidoraId> distinct = [];
        return ids.Select(id =>
        {
            if (id == default || !distinct.Add(id))
                throw new ArgumentException("Imported MIDI event IDs must be distinct.", nameof(ids));
            int index = segment.OpaqueEvents.FindIndex(value => value.Id == id);
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(ids));
            OpaqueMidiEvent value = segment.OpaqueEvents[index];
            return new OpaqueEventSelection(value, index, SnapshotOpaqueEvent(value));
        }).ToArray();
    }

    private static void ValidateMidiSegmentPlacements(
        IReadOnlyCollection<MidiSegmentBatchPlacement> placements,
        HashSet<MidiSegment> moving)
    {
        foreach (IGrouping<PureMidiTrack, MidiSegmentBatchPlacement> group in placements.GroupBy(value => value.TargetTrack))
        {
            TickRange[] ranges = group.Select(value => new TickRange(
                value.Start,
                checked(value.Start + value.Source.Segment.LengthTicks)))
                .OrderBy(value => value.StartTick)
                .ToArray();
            if (ranges.Zip(ranges.Skip(1)).Any(value => value.First.EndTick > value.Second.StartTick)
                || group.Key.Segments
                    .Where(value => !moving.Contains(value))
                    .Any(existing => ranges.Any(range => range.Intersects(existing.ProjectRange))))
            {
                throw new InvalidOperationException("The MIDI Segment edit would create an overlap.");
            }
        }
    }

    private static void ValidateMidiSegmentEdgeEdits(MidiSegmentEdgeEdit[] edits)
    {
        HashSet<MidiSegment> selected = edits.Select(value => value.Selection.Segment).ToHashSet();
        foreach (IGrouping<PureMidiTrack, MidiSegmentEdgeEdit> group in edits.GroupBy(value => value.Selection.Track))
        {
            TickRange[] ranges = group.Select(value => new TickRange(
                value.Replacement.ProjectStartTick,
                checked(value.Replacement.ProjectStartTick + value.Replacement.LengthTicks)))
                .OrderBy(value => value.StartTick).ToArray();
            if (ranges.Zip(ranges.Skip(1)).Any(value => value.First.EndTick > value.Second.StartTick)
                || group.Key.Segments.Where(value => !selected.Contains(value)).Any(existing => ranges.Any(range => range.Intersects(existing.ProjectRange))))
                throw new InvalidOperationException("The MIDI Segment resize would create an overlap.");
        }
    }

    private static MidiSegment CloneMidiSegment(MidoraProject project, MidiSegment source, long start)
    {
        MidiSegment result = new(project)
        {
            ProjectStartTick = start,
            LengthTicks = source.LengthTicks,
            ContentOffsetTick = source.ContentOffsetTick
        };
        foreach (DirectMidiNote note in source.Notes)
        {
            result.Notes.Add(new DirectMidiNote(project)
            {
                StartTick = note.StartTick,
                LengthTicks = note.LengthTicks,
                Key = note.Key,
                NoteOnVelocity = note.NoteOnVelocity,
                NoteOffVelocity = note.NoteOffVelocity,
                NoteOnOrder = note.NoteOnOrder,
                NoteOffOrder = note.NoteOffOrder
            });
        }
        foreach (DirectMidiChannelEvent value in source.ChannelEvents)
        {
            result.ChannelEvents.Add(new DirectMidiChannelEvent(project)
            {
                Tick = value.Tick,
                Kind = value.Kind,
                Data1 = value.Data1,
                Data2 = value.Data2,
                Order = value.Order
            });
        }
        foreach (OpaqueMidiEvent value in source.OpaqueEvents)
        {
            result.OpaqueEvents.Add(new OpaqueMidiEvent(project)
            {
                Tick = value.Tick,
                Kind = value.Kind,
                MetaType = value.MetaType,
                Payload = value.Payload.ToArray(),
                Order = value.Order
            });
        }
        return result;
    }

    private static void ShiftMidiSegmentContent(MidiSegment segment, long delta)
    {
        if (delta == 0) return;
        foreach (DirectMidiNote note in segment.Notes) note.StartTick = checked(note.StartTick + delta);
        foreach (DirectMidiChannelEvent value in segment.ChannelEvents) value.Tick = checked(value.Tick + delta);
        foreach (OpaqueMidiEvent value in segment.OpaqueEvents) value.Tick = checked(value.Tick + delta);
    }

    private static void SetMidiSegmentWindow(MidiSegment segment, SegmentWindow value)
    {
        segment.ProjectStartTick = value.ProjectStartTick;
        segment.LengthTicks = value.LengthTicks;
        segment.ContentOffsetTick = value.ContentOffsetTick;
    }

    private static DirectNoteValue SnapshotDirectNote(DirectMidiNote value) => new(
        value.StartTick, value.LengthTicks, value.Key, value.NoteOnVelocity,
        value.NoteOffVelocity, value.NoteOnOrder, value.NoteOffOrder);

    private static DirectMidiNote CreateDirectNote(MidoraProject project, DirectNoteValue value)
    {
        DirectMidiNote result = new(project);
        ApplyDirectNote(result, value);
        return result;
    }

    private static void ApplyDirectNote(DirectMidiNote target, DirectNoteValue value)
    {
        target.StartTick = value.StartTick;
        target.LengthTicks = value.LengthTicks;
        target.Key = value.Key;
        target.NoteOnVelocity = value.NoteOnVelocity;
        target.NoteOffVelocity = value.NoteOffVelocity;
        target.NoteOnOrder = value.NoteOnOrder;
        target.NoteOffOrder = value.NoteOffOrder;
    }

    private static DirectMidiEventValue SnapshotDirectEvent(DirectMidiChannelEvent value) => new(
        value.Tick, value.Kind, value.Data1, value.Data2, value.Order);
    private static DirectMidiChannelEvent CreateDirectEvent(MidoraProject project, DirectMidiEventValue value)
    {
        DirectMidiChannelEvent result = new(project);
        ApplyDirectEvent(result, value);
        return result;
    }
    private static void ApplyDirectEvent(DirectMidiChannelEvent target, DirectMidiEventValue value)
    {
        target.Tick = value.Tick;
        target.Kind = value.Kind;
        target.Data1 = value.Data1;
        target.Data2 = value.Data2;
        target.Order = value.Order;
    }
    private static OpaqueMidiEventValue SnapshotOpaqueEvent(OpaqueMidiEvent value) => new(
        value.Tick,
        value.Kind,
        value.MetaType,
        value.Payload.ToArray(),
        value.Order);
    private static OpaqueMidiEvent CreateOpaqueEvent(MidoraProject project, OpaqueMidiEventValue value) => new(project)
    {
        Tick = value.Tick,
        Kind = value.Kind,
        MetaType = value.MetaType,
        Payload = value.Payload.ToArray(),
        Order = value.Order
    };
    private static DirectMidiChannelEvent? FindDirectEventAtTick(
        MidiSegment segment,
        DirectMidiChannelEventKind kind,
        int laneData1,
        long tick) => segment.ChannelEvents.FirstOrDefault(value =>
            value.Tick == tick
            && value.Kind == kind
            && (kind is DirectMidiChannelEventKind.ControlChange
                    or DirectMidiChannelEventKind.PolyphonicKeyPressure
                    or DirectMidiChannelEventKind.NoteOn
                    or DirectMidiChannelEventKind.NoteOff
                ? value.Data1 == laneData1
                : true));

    private readonly record struct MidiSegmentSelection(
        PureMidiTrack Track,
        MidiSegment Segment,
        int Index,
        long OriginalStartTick);
    private readonly record struct MidiSegmentBatchPlacement(
        MidiSegmentSelection Source,
        PureMidiTrack TargetTrack,
        long Start);
    private readonly record struct MidiSegmentEdgeEdit(
        MidiSegmentSelection Selection,
        SegmentWindow Old,
        SegmentWindow Replacement,
        long ContentShift);
    private readonly record struct DirectNoteSelection(DirectMidiNote Note, int Index);
    private readonly record struct DirectEventSelection(
        DirectMidiChannelEvent Event,
        int Index,
        DirectMidiEventValue Original);
    private readonly record struct OpaqueEventSelection(
        OpaqueMidiEvent Event,
        int Index,
        OpaqueMidiEventValue Original);
    private readonly record struct DirectNoteValue(
        long StartTick,
        long LengthTicks,
        int Key,
        int NoteOnVelocity,
        int NoteOffVelocity,
        long NoteOnOrder,
        long NoteOffOrder);
    private readonly record struct DirectMidiEventValue(
        long Tick,
        DirectMidiChannelEventKind Kind,
        int Data1,
        int Data2,
        long Order);
    private readonly record struct OpaqueMidiEventValue(
        long Tick,
        OpaqueMidiEventKind Kind,
        byte MetaType,
        byte[] Payload,
        long Order);
}
