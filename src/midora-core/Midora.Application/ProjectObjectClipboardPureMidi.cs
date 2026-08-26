using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectObjectClipboard
{
    public static ProjectObjectClipboardPayload CopyPureMidiTrack(
        ProjectDocumentSession document,
        MidoraId trackId)
    {
        ArgumentNullException.ThrowIfNull(document);
        PureMidiTrack track = document.Project.PureMidiTracks.SingleOrDefault(value => value.Id == trackId)
            ?? throw new ArgumentOutOfRangeException(nameof(trackId));
        MidiChannelRoot root = document.Project.MidiChannelRoots.SingleOrDefault(
            value => value.Id == track.MidiChannelRootId)
            ?? throw new InvalidOperationException(
                "The copied Pure MIDI Track references a missing MIDI Channel Root.");
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.PureMidiTrack,
            1,
            "1 MIDI Track",
            new PureMidiTrackClipboardData(
                SnapshotPureMidiTrack(track),
                SnapshotMidiRoute(root)));
    }

    public static ProjectObjectClipboardPayload CopyMidiSegments(
        ProjectDocumentSession document,
        IReadOnlyCollection<MidoraId> segmentIds,
        MidoraId primarySegmentId)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(segmentIds);
        if (segmentIds.Count == 0) throw new ArgumentException("At least one MIDI Segment must be copied.", nameof(segmentIds));
        HashSet<MidoraId> requested = [];
        List<(PureMidiTrack Track, MidiSegment Segment, int TrackIndex)> selected = [];
        foreach (MidoraId id in segmentIds)
        {
            if (id == default || !requested.Add(id))
                throw new ArgumentException("MIDI Segment selections require distinct stable IDs.", nameof(segmentIds));
            selected.Add(FindMidiSegmentForClipboard(document.Project, id));
        }
        var primary = selected.SingleOrDefault(value => value.Segment.Id == primarySegmentId);
        if (primary.Segment is null)
            throw new ArgumentException("The primary MIDI Segment must belong to the selection.", nameof(primarySegmentId));
        long earliest = selected.Min(value => value.Segment.ProjectStartTick);
        MidiSegmentClipboardSnapshot[] snapshots = selected
            .OrderBy(value => value.TrackIndex)
            .ThenBy(value => value.Segment.ProjectStartTick)
            .ThenBy(value => value.Segment.Id)
            .Select(value => SnapshotMidiSegment(
                value.Segment,
                checked(value.TrackIndex - primary.TrackIndex),
                checked(value.Segment.ProjectStartTick - earliest)))
            .ToArray();
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.MidiSegments,
            snapshots.Length,
            snapshots.Length == 1 ? "1 MIDI Segment" : $"{snapshots.Length} MIDI Segments",
            new MidiSegmentClipboardData(snapshots));
    }

    public static ProjectObjectClipboardPayload CopyDirectMidiNotes(
        ProjectDocumentSession document,
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(noteIds);
        MidiSegment source = FindMidiSegmentForClipboard(document.Project, segmentId).Segment;
        HashSet<MidoraId> requested = noteIds.ToHashSet();
        if (requested.Count == 0 || requested.Count != noteIds.Count)
            throw new ArgumentException("Direct MIDI Note selections require distinct stable IDs.", nameof(noteIds));
        DirectMidiNote[] notes = source.Notes.ResolveByIds(requested)
            .Select(static match => match.Value)
            .ToArray();
        if (notes.Length != requested.Count)
            throw new ArgumentException("Every copied Direct MIDI Note must belong to the source Segment.", nameof(noteIds));
        long earliest = notes.Min(value => value.StartTick);
        DirectMidiNoteClipboardSnapshot[] snapshots = notes.Select(value => new DirectMidiNoteClipboardSnapshot(
            checked(value.StartTick - earliest),
            value.LengthTicks,
            value.Key,
            value.NoteOnVelocity,
            value.NoteOffVelocity,
            value.NoteOnOrder,
            value.NoteOffOrder,
            PreserveOrders: true)).ToArray();
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.DirectMidiNotes,
            snapshots.Length,
            snapshots.Length == 1 ? "1 MIDI Note" : $"{snapshots.Length} MIDI Notes",
            new DirectMidiNoteClipboardData(snapshots));
    }

    public static ProjectObjectClipboardPayload CopyDirectMidiEvents(
        ProjectDocumentSession document,
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> eventIds)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(eventIds);
        MidiSegment source = FindMidiSegmentForClipboard(document.Project, segmentId).Segment;
        HashSet<MidoraId> requested = eventIds.ToHashSet();
        if (requested.Count == 0 || requested.Count != eventIds.Count)
            throw new ArgumentException("Direct MIDI Event selections require distinct stable IDs.", nameof(eventIds));
        DirectMidiChannelEvent[] events = source.ChannelEvents.ResolveByIds(requested)
            .Select(static match => match.Value)
            .ToArray();
        if (events.Length != requested.Count)
            throw new ArgumentException("Every copied Direct MIDI Event must belong to the source Segment.", nameof(eventIds));
        long earliest = events.Min(value => value.Tick);
        DirectMidiEventClipboardSnapshot[] snapshots = events.Select(value => new DirectMidiEventClipboardSnapshot(
            checked(value.Tick - earliest), value.Kind, value.Data1, value.Data2, value.Order)).ToArray();
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.DirectMidiEvents,
            snapshots.Length,
            snapshots.Length == 1 ? "1 MIDI Event" : $"{snapshots.Length} MIDI Events",
            new DirectMidiEventClipboardData(snapshots));
    }

    public static ProjectObjectClipboardPayload CopyOpaqueMidiEvents(
        ProjectDocumentSession document,
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> eventIds)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(eventIds);
        MidiSegment source = FindMidiSegmentForClipboard(document.Project, segmentId).Segment;
        HashSet<MidoraId> requested = eventIds.ToHashSet();
        if (requested.Count == 0 || requested.Count != eventIds.Count)
            throw new ArgumentException("Imported MIDI event selections require distinct stable IDs.", nameof(eventIds));
        OpaqueMidiEvent[] events = source.OpaqueEvents.ResolveByIds(requested)
            .Select(static match => match.Value)
            .ToArray();
        if (events.Length != requested.Count)
            throw new ArgumentException("Every copied imported MIDI event must belong to the source Segment.", nameof(eventIds));
        long earliest = events.Min(value => value.Tick);
        OpaqueMidiEventClipboardSnapshot[] snapshots = events.Select(value => new OpaqueMidiEventClipboardSnapshot(
            checked(value.Tick - earliest),
            value.Kind,
            value.MetaType,
            value.Payload.ToArray(),
            value.Order)).ToArray();
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.OpaqueMidiEvents,
            snapshots.Length,
            snapshots.Length == 1 ? "1 imported MIDI Event" : $"{snapshots.Length} imported MIDI Events",
            new OpaqueMidiEventClipboardData(snapshots));
    }

    public static IProjectEditCommand CreatePastePureMidiTrackCommand(
        ProjectDocumentSession document,
        ProjectObjectClipboardPayload payload,
        MidoraId rootId,
        int insertionIndex)
    {
        PureMidiTrackClipboardData data = RequirePayload<PureMidiTrackClipboardData>(
            document, payload, ProjectObjectClipboardKind.PureMidiTrack);
        return ProjectDomainEditCommands.PastePureMidiTrackClipboard(
            data.Track,
            data.Route,
            rootId,
            insertionIndex);
    }

    public static IProjectEditCommand CreatePastePureMidiTrackIndependentCommand(
        ProjectDocumentSession document,
        ProjectObjectClipboardPayload payload,
        int insertionIndex)
    {
        PureMidiTrackClipboardData data = RequirePayload<PureMidiTrackClipboardData>(
            document,
            payload,
            ProjectObjectClipboardKind.PureMidiTrack);
        return ProjectDomainEditCommands.PastePureMidiTrackClipboard(
            data.Track,
            data.Route,
            targetRootId: null,
            insertionIndex);
    }

    public static IProjectEditCommand CreatePasteMidiSegmentsCommand(
        ProjectDocumentSession document,
        ProjectObjectClipboardPayload payload,
        MidoraId targetTrackId,
        long editCursorTick)
    {
        MidiSegmentClipboardData data = RequirePayload<MidiSegmentClipboardData>(
            document, payload, ProjectObjectClipboardKind.MidiSegments);
        return ProjectDomainEditCommands.PasteMidiSegmentClipboard(data.Segments, targetTrackId, editCursorTick);
    }

    public static IProjectEditCommand CreatePasteNotesCommand(
        ProjectDocumentSession document,
        ProjectObjectClipboardPayload payload,
        MidoraId targetSegmentId,
        long editCursorTick,
        bool targetIsDirectMidi)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(payload);
        if (!ReferenceEquals(document.ClipboardSessionIdentity, payload.SourceSessionIdentity))
            throw new InvalidOperationException("Project object clipboard payloads are only valid in their source Project session.");
        if (targetIsDirectMidi)
        {
            IReadOnlyList<DirectMidiNoteClipboardSnapshot> values = payload.Kind switch
            {
                ProjectObjectClipboardKind.DirectMidiNotes when payload.Data is DirectMidiNoteClipboardData direct => direct.Notes,
                ProjectObjectClipboardKind.LogicalNotes when payload.Data is LogicalNoteClipboardData logical =>
                    logical.Notes.Select(value => new DirectMidiNoteClipboardSnapshot(
                        value.StartOffset,
                        value.LengthTicks,
                        value.Note,
                        value.Velocity,
                        0,
                        0,
                        0,
                        PreserveOrders: false)).ToArray(),
                _ => throw new ArgumentException("The clipboard payload does not contain MIDI-compatible Notes.", nameof(payload))
            };
            return ProjectDomainEditCommands.PasteDirectMidiNoteClipboard(values, targetSegmentId, editCursorTick);
        }
        IReadOnlyList<LogicalNoteClipboardSnapshot> logicalValues = payload.Kind switch
        {
            ProjectObjectClipboardKind.LogicalNotes when payload.Data is LogicalNoteClipboardData logical => logical.Notes,
            ProjectObjectClipboardKind.DirectMidiNotes when payload.Data is DirectMidiNoteClipboardData direct =>
                direct.Notes.Select(value => new LogicalNoteClipboardSnapshot(
                    value.StartOffset, value.LengthTicks, value.Key, value.NoteOnVelocity)).ToArray(),
            _ => throw new ArgumentException("The clipboard payload does not contain MIDI-compatible Notes.", nameof(payload))
        };
        return ProjectDomainEditCommands.PasteLogicalNoteClipboard(logicalValues, targetSegmentId, editCursorTick);
    }

    public static IProjectEditCommand CreatePasteDirectMidiEventsCommand(
        ProjectDocumentSession document,
        ProjectObjectClipboardPayload payload,
        MidoraId targetSegmentId,
        long editCursorTick)
    {
        DirectMidiEventClipboardData data = RequirePayload<DirectMidiEventClipboardData>(
            document, payload, ProjectObjectClipboardKind.DirectMidiEvents);
        return ProjectDomainEditCommands.PasteDirectMidiEventClipboard(
            data.Events, targetSegmentId, editCursorTick);
    }

    public static IProjectEditCommand CreatePasteOpaqueMidiEventsCommand(
        ProjectDocumentSession document,
        ProjectObjectClipboardPayload payload,
        MidoraId targetSegmentId,
        long editCursorTick)
    {
        OpaqueMidiEventClipboardData data = RequirePayload<OpaqueMidiEventClipboardData>(
            document, payload, ProjectObjectClipboardKind.OpaqueMidiEvents);
        return ProjectDomainEditCommands.PasteOpaqueMidiEventClipboard(
            data.Events, targetSegmentId, editCursorTick);
    }

    private static PureMidiTrackClipboardSnapshot SnapshotPureMidiTrack(PureMidiTrack track) => new(
        track.Name,
        track.Color,
        track.Segments.Select(segment => SnapshotMidiSegment(
            segment, 0, segment.ProjectStartTick)).ToArray());

    private static MidiSegmentClipboardSnapshot SnapshotMidiSegment(
        MidiSegment segment,
        int trackOffset,
        long startOffset) => new(
            trackOffset,
            startOffset,
            segment.LengthTicks,
            segment.ContentOffsetTick,
            segment.Notes.Select(note => new DirectMidiNoteClipboardSnapshot(
                note.StartTick, note.LengthTicks, note.Key, note.NoteOnVelocity,
                note.NoteOffVelocity, note.NoteOnOrder, note.NoteOffOrder,
                PreserveOrders: true)).ToArray(),
            segment.ChannelEvents.Select(value => new DirectMidiEventClipboardSnapshot(
                value.Tick, value.Kind, value.Data1, value.Data2, value.Order)).ToArray(),
            segment.OpaqueEvents.Select(value => new OpaqueMidiEventClipboardSnapshot(
                value.Tick, value.Kind, value.MetaType, value.Payload.ToArray(), value.Order)).ToArray());

    private static MidiRouteClipboardSnapshot SnapshotMidiRoute(MidiChannelRoot root) => new(
        root.Name,
        root.RoutingMode,
        root.FixedZeroBasedPort,
        root.FixedZeroBasedChannel,
        root.ChannelMode);

    private static (PureMidiTrack Track, MidiSegment Segment, int TrackIndex) FindMidiSegmentForClipboard(
        MidoraProject project,
        MidoraId segmentId)
    {
        (PureMidiTrack Track, MidiSegment Segment, int TrackIndex)? result = null;
        foreach (PureMidiTrack track in project.PureMidiTracks)
        {
            int trackIndex = ProjectDomainEditCommands.FindArrangementTrackIndex(
                project,
                ArrangementTrackKind.PureMidiTrack,
                track.Id);
            foreach (MidiSegment segment in track.Segments)
            {
                if (segment.Id != segmentId) continue;
                if (result.HasValue) throw new InvalidOperationException("The MIDI Segment stable ID is duplicated.");
                result = (track, segment, trackIndex);
            }
        }
        return result ?? throw new ArgumentOutOfRangeException(nameof(segmentId));
    }
}

internal sealed record PureMidiTrackClipboardData(
    PureMidiTrackClipboardSnapshot Track,
    MidiRouteClipboardSnapshot Route) : ProjectObjectClipboardData;
internal sealed record MidiRouteClipboardSnapshot(
    string Name,
    MidiChannelRootRoutingMode RoutingMode,
    byte FixedZeroBasedPort,
    byte FixedZeroBasedChannel,
    MidiChannelMode ChannelMode);
internal sealed record PureMidiTrackClipboardSnapshot(
    string Name,
    MidoraColor? Color,
    MidiSegmentClipboardSnapshot[] Segments);
internal sealed record MidiSegmentClipboardData(MidiSegmentClipboardSnapshot[] Segments) : ProjectObjectClipboardData;
internal sealed record MidiSegmentClipboardSnapshot(
    int TrackOffset,
    long StartOffset,
    long LengthTicks,
    long ContentOffsetTick,
    DirectMidiNoteClipboardSnapshot[] Notes,
    DirectMidiEventClipboardSnapshot[] Events,
    OpaqueMidiEventClipboardSnapshot[] OpaqueEvents);
internal sealed record DirectMidiNoteClipboardData(DirectMidiNoteClipboardSnapshot[] Notes) : ProjectObjectClipboardData;
internal sealed record DirectMidiEventClipboardData(DirectMidiEventClipboardSnapshot[] Events) : ProjectObjectClipboardData;
internal sealed record OpaqueMidiEventClipboardData(OpaqueMidiEventClipboardSnapshot[] Events) : ProjectObjectClipboardData;
internal sealed record DirectMidiNoteClipboardSnapshot(
    long StartOffset,
    long LengthTicks,
    int Key,
    int NoteOnVelocity,
    int NoteOffVelocity,
    long NoteOnOrder,
    long NoteOffOrder,
    bool PreserveOrders);
internal sealed record DirectMidiEventClipboardSnapshot(
    long Tick,
    DirectMidiChannelEventKind Kind,
    int Data1,
    int Data2,
    long Order);
internal sealed record OpaqueMidiEventClipboardSnapshot(
    long Tick,
    OpaqueMidiEventKind Kind,
    byte MetaType,
    byte[] Payload,
    long Order);

public static partial class ProjectDomainEditCommands
{
    internal static IProjectEditCommand PastePureMidiTrackClipboard(
        PureMidiTrackClipboardSnapshot snapshot,
        MidiRouteClipboardSnapshot route,
        MidoraId? targetRootId,
        int insertionIndex) =>
        Command("Paste Pure MIDI Track", project =>
        {
            MidiChannelRoot? targetRoot = targetRootId is MidoraId rootId
                ? FindMidiChannelRoot(project, rootId)
                : route.RoutingMode == MidiChannelRootRoutingMode.Fixed
                    ? project.MidiChannelRoots.SingleOrDefault(value =>
                        value.RoutingMode == MidiChannelRootRoutingMode.Fixed
                        && value.FixedZeroBasedPort == route.FixedZeroBasedPort
                        && value.FixedZeroBasedChannel == route.FixedZeroBasedChannel)
                    : null;
            if (targetRoot is not null
                && targetRoot.RoutingMode == MidiChannelRootRoutingMode.Fixed
                && targetRoot.ChannelMode != route.ChannelMode
                && targetRootId is null)
            {
                throw new InvalidOperationException(
                    "The copied Fixed route conflicts with the existing Channel Mode.");
            }
            ValidateInsertionIndex(
                insertionIndex,
                project.ArrangementTracks.Count,
                nameof(insertionIndex));
            int trackIndex = insertionIndex;
            if (targetRoot is { RoutingMode: MidiChannelRootRoutingMode.Auto })
            {
                int first = project.ArrangementTracks.FindIndex(reference =>
                    reference.Kind == ArrangementTrackKind.PureMidiTrack
                    && FindPureMidiTrack(project, reference.TrackId).MidiChannelRootId == targetRoot.Id);
                int last = project.ArrangementTracks.FindLastIndex(reference =>
                    reference.Kind == ArrangementTrackKind.PureMidiTrack
                    && FindPureMidiTrack(project, reference.TrackId).MidiChannelRootId == targetRoot.Id);
                if (first < 0)
                {
                    throw new InvalidOperationException(
                        "The target Auto MIDI Channel has no Arrangement member.");
                }
                if (trackIndex < first || trackIndex > last + 1)
                {
                    trackIndex = last + 1;
                }
            }
            PureMidiTrack? copy = null;
            MidiChannelRoot? createdRoot = null;
            return Prepared(
                true,
                EverythingChange(),
                owner =>
                {
                    if (targetRoot is null && createdRoot is null)
                    {
                        createdRoot = new(owner)
                        {
                            Name = ProjectTextRules.NormalizeShortText(
                                route.Name,
                                allowEmpty: false,
                                nameof(route)),
                            RoutingMode = route.RoutingMode,
                            FixedZeroBasedPort = route.FixedZeroBasedPort,
                            FixedZeroBasedChannel = route.FixedZeroBasedChannel,
                            ChannelMode = route.ChannelMode
                        };
                    }
                    MidiChannelRoot root = targetRoot ?? createdRoot!;
                    if (createdRoot is not null && !owner.MidiChannelRoots.Contains(createdRoot))
                    {
                        EnsureMidiChannelRootIdAvailable(owner, createdRoot.Id);
                        owner.MidiChannelRoots.Add(createdRoot);
                    }
                    copy ??= CreatePureMidiTrackFromClipboard(owner, snapshot, root.Id);
                    EnsurePureMidiTrackIdAvailable(owner, copy.Id);
                    copy.MidiChannelRootId = root.Id;
                    owner.PureMidiTracks.Add(copy);
                    InsertAt(
                        owner.ArrangementTracks,
                        trackIndex,
                        new ArrangementTrackReference(
                            ArrangementTrackKind.PureMidiTrack,
                            copy.Id),
                        "pasted Arrangement Track reference");
                },
                owner =>
                {
                    PureMidiTrack value = copy ?? throw new InvalidOperationException(
                        "The pasted Pure MIDI Track does not exist.");
                    RemoveRequired(
                        owner.ArrangementTracks,
                        new ArrangementTrackReference(
                            ArrangementTrackKind.PureMidiTrack,
                            value.Id),
                        "pasted Arrangement Track reference");
                    RemoveRequired(owner.PureMidiTracks, value, "pasted Pure MIDI Track");
                    if (createdRoot is not null)
                    {
                        RemoveRequired(owner.MidiChannelRoots, createdRoot, "MIDI Channel Root");
                    }
                });
        });

    internal static IProjectEditCommand PasteMidiSegmentClipboard(
        IReadOnlyList<MidiSegmentClipboardSnapshot> snapshots,
        MidoraId targetTrackId,
        long editCursorTick) =>
        Command("Paste MIDI Segments", project =>
        {
            if (snapshots.Count == 0 || editCursorTick < 0) throw new ArgumentOutOfRangeException(nameof(editCursorTick));
            PureMidiTrack primary = FindPureMidiTrack(project, targetTrackId);
            int primaryIndex = FindArrangementTrackIndex(
                project,
                ArrangementTrackKind.PureMidiTrack,
                primary.Id);
            var placements = snapshots.Select(snapshot =>
            {
                int index = checked(primaryIndex + snapshot.TrackOffset);
                if ((uint)index >= (uint)project.ArrangementTracks.Count
                    || project.ArrangementTracks[index] is not
                    { Kind: ArrangementTrackKind.PureMidiTrack } targetReference)
                {
                    throw new InvalidOperationException(
                        "The MIDI Segment clipboard cannot preserve its relative Arrangement lane offsets.");
                }
                PureMidiTrack track = FindPureMidiTrack(project, targetReference.TrackId);
                long start = checked(editCursorTick + snapshot.StartOffset);
                EnsureNoMidiSegmentOverlap(track, null, start, snapshot.LengthTicks);
                return (Snapshot: snapshot, Track: track, Start: start);
            }).ToArray();
            foreach (IGrouping<PureMidiTrack, (MidiSegmentClipboardSnapshot Snapshot, PureMidiTrack Track, long Start)> group in placements.GroupBy(value => value.Track))
            {
                var ordered = group.OrderBy(value => value.Start).ToArray();
                for (int i = 1; i < ordered.Length; i++)
                    if (checked(ordered[i - 1].Start + ordered[i - 1].Snapshot.LengthTicks) > ordered[i].Start)
                        throw new InvalidOperationException("Pasted MIDI Segments would overlap each other.");
            }
            MidiSegment[]? copies = null;
            return Prepared(true, EverythingChange(), owner =>
            {
                copies ??= placements.Select(value => CreateMidiSegmentFromClipboard(owner, value.Snapshot, value.Start)).ToArray();
                for (int i = 0; i < copies.Length; i++) InsertMidiSegmentByTime(placements[i].Track.Segments, copies[i]);
            }, _ =>
            {
                if (copies is null) throw new InvalidOperationException("Pasted MIDI Segments do not exist.");
                for (int i = 0; i < copies.Length; i++) RemoveRequired(placements[i].Track.Segments, copies[i], "pasted MIDI Segment");
            });
        });

    internal static IProjectEditCommand PasteDirectMidiNoteClipboard(
        IReadOnlyList<DirectMidiNoteClipboardSnapshot> snapshots,
        MidoraId segmentId,
        long editCursorTick) =>
        Command("Paste Direct MIDI Notes", project =>
        {
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            if (snapshots.Count == 0 || editCursorTick < 0) throw new ArgumentOutOfRangeException(nameof(editCursorTick));
            DirectMidiNote[]? created = null;
            return ResolveTargetedExactDirectMidiCollisions(Prepared(true, PureMidiTrackChange(location.Track.Id), owner =>
            {
                created ??= snapshots.Select(value =>
                {
                    long tick = checked(editCursorTick + value.StartOffset);
                    ValidateDirectMidiNote(tick, value.LengthTicks, value.Key, value.NoteOnVelocity, value.NoteOffVelocity);
                    DirectMidiNote note = new(owner)
                    {
                        StartTick = tick,
                        LengthTicks = value.LengthTicks,
                        Key = value.Key,
                        NoteOnVelocity = value.NoteOnVelocity,
                        NoteOffVelocity = value.NoteOffVelocity
                    };
                    note.NoteOnOrder = value.PreserveOrders
                        ? value.NoteOnOrder
                        : note.Id.Value * 2;
                    note.NoteOffOrder = value.PreserveOrders
                        ? value.NoteOffOrder
                        : checked(note.NoteOnOrder + 1);
                    return note;
                }).ToArray();
                location.Segment.Notes.AddRange(created);
            }, _ =>
            {
                location.Segment.Notes.RemoveRange(created ?? []);
            }), noteTargets: snapshots.Select(value => new DirectMidiNoteCollisionTarget(
                location.Segment,
                checked(editCursorTick + value.StartOffset),
                value.Key)));
        });

    internal static IProjectEditCommand PasteDirectMidiEventClipboard(
        IReadOnlyList<DirectMidiEventClipboardSnapshot> snapshots,
        MidoraId segmentId,
        long editCursorTick) =>
        Command("Paste Direct MIDI Events", project =>
        {
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            if (snapshots.Count == 0 || editCursorTick < 0) throw new ArgumentOutOfRangeException(nameof(editCursorTick));
            DirectMidiChannelEvent[]? created = null;
            return ResolveTargetedExactDirectMidiCollisions(Prepared(true, PureMidiTrackChange(location.Track.Id), owner =>
            {
                created ??= snapshots.Select(value =>
                {
                    long tick = checked(editCursorTick + value.Tick);
                    ValidateDirectMidiEvent(tick, value.Kind, value.Data1, value.Data2);
                    return new DirectMidiChannelEvent(owner)
                    {
                        Tick = tick,
                        Kind = value.Kind,
                        Data1 = value.Data1,
                        Data2 = value.Data2,
                        Order = value.Order
                    };
                }).ToArray();
                location.Segment.ChannelEvents.AddRange(created);
            }, _ =>
            {
                location.Segment.ChannelEvents.RemoveRange(created ?? []);
            }), eventTargets: snapshots.Select(value => new DirectMidiEventCollisionTarget(
                location.Segment,
                checked(editCursorTick + value.Tick),
                value.Kind,
                value.Data1)));
        });

    internal static IProjectEditCommand PasteOpaqueMidiEventClipboard(
        IReadOnlyList<OpaqueMidiEventClipboardSnapshot> snapshots,
        MidoraId targetSegmentId,
        long editCursorTick) =>
        Command("Paste imported MIDI events", project =>
        {
            ArgumentNullException.ThrowIfNull(snapshots);
            MidiSegmentLocation location = FindMidiSegment(project, targetSegmentId);
            if (snapshots.Count == 0) throw new ArgumentException("The imported MIDI event clipboard is empty.", nameof(snapshots));
            OpaqueMidiEvent[]? created = null;
            return Prepared(
                true,
                PureMidiTrackChange(location.Track.Id),
                owner =>
                {
                    created ??= snapshots.Select(value =>
                    {
                        long tick = checked(editCursorTick + value.Tick);
                        if (tick < 0) throw new InvalidOperationException("Imported MIDI events cannot be pasted before tick 0.");
                        if (!Enum.IsDefined(value.Kind))
                            throw new InvalidOperationException("The imported MIDI event clipboard contains an invalid event kind.");
                        return new OpaqueMidiEvent(owner)
                        {
                            Tick = tick,
                            Kind = value.Kind,
                            MetaType = value.MetaType,
                            Payload = value.Payload.ToArray(),
                            Order = value.Order
                        };
                    }).ToArray();
                    location.Segment.OpaqueEvents.AddRange(created);
                },
                _ =>
                {
                    location.Segment.OpaqueEvents.RemoveRange(created ?? []);
                });
        });

    private static PureMidiTrack CreatePureMidiTrackFromClipboard(
        MidoraProject project,
        PureMidiTrackClipboardSnapshot snapshot,
        MidoraId rootId)
    {
        PureMidiTrack track = new(project)
        {
            Name = ProjectTextRules.NormalizeShortText(snapshot.Name, true, nameof(snapshot)),
            MidiChannelRootId = rootId,
            Color = snapshot.Color
        };
        foreach (MidiSegmentClipboardSnapshot segment in snapshot.Segments)
            InsertMidiSegmentByTime(track.Segments, CreateMidiSegmentFromClipboard(project, segment, segment.StartOffset));
        return track;
    }

    private static MidiSegment CreateMidiSegmentFromClipboard(
        MidoraProject project,
        MidiSegmentClipboardSnapshot snapshot,
        long start)
    {
        MidiSegment segment = new(project)
        {
            ProjectStartTick = start,
            LengthTicks = snapshot.LengthTicks,
            ContentOffsetTick = snapshot.ContentOffsetTick
        };
        HashSet<(long Tick, int Key)> noteStarts = [];
        foreach (DirectMidiNoteClipboardSnapshot note in snapshot.Notes)
        {
            if (!noteStarts.Add((note.StartOffset, note.Key))) continue;
            DirectMidiNote copy = new(project)
            {
                StartTick = note.StartOffset,
                LengthTicks = note.LengthTicks,
                Key = note.Key,
                NoteOnVelocity = note.NoteOnVelocity,
                NoteOffVelocity = note.NoteOffVelocity
            };
            copy.NoteOnOrder = note.PreserveOrders
                ? note.NoteOnOrder
                : copy.Id.Value * 2;
            copy.NoteOffOrder = note.PreserveOrders
                ? note.NoteOffOrder
                : checked(copy.NoteOnOrder + 1);
            segment.Notes.Add(copy);
        }
        foreach (DirectMidiEventClipboardSnapshot value in snapshot.Events)
            segment.ChannelEvents.Add(new DirectMidiChannelEvent(project)
            {
                Tick = value.Tick,
                Kind = value.Kind,
                Data1 = value.Data1,
                Data2 = value.Data2,
                Order = value.Order
            });
        foreach (OpaqueMidiEventClipboardSnapshot value in snapshot.OpaqueEvents)
            segment.OpaqueEvents.Add(new OpaqueMidiEvent(project)
            {
                Tick = value.Tick,
                Kind = value.Kind,
                MetaType = value.MetaType,
                Payload = value.Payload.ToArray(),
                Order = value.Order
            });
        return segment;
    }
}
