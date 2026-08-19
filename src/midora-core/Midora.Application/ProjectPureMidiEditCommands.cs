using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand CreateMidiChannelRoot(
        string? name = null,
        int? insertionIndex = null) =>
        Command("Create MIDI Channel Root", project =>
        {
            string normalized = ProjectTextRules.NormalizeShortText(
                name ?? "MIDI Channel Root",
                allowEmpty: false,
                nameof(name));
            int parentIndex = insertionIndex ?? project.ArrangementParents.Count;
            ValidateInsertionIndex(parentIndex, project.ArrangementParents.Count, nameof(insertionIndex));
            return DeferredCreate(
                EverythingChange(),
                value =>
                {
                    MidiChannelRoot root = new(value)
                    {
                        Name = normalized,
                        RoutingMode = MidiChannelRootRoutingMode.Auto,
                        ChannelMode = MidiChannelMode.Melodic
                    };
                    value.MidiChannelRoots.Add(root);
                    value.ArrangementParents.Insert(
                        parentIndex,
                        new(ArrangementParentKind.MidiChannelRoot, root.Id));
                    return root;
                },
                (value, root) =>
                {
                    EnsureMidiChannelRootIdAvailable(value, root.Id);
                    value.MidiChannelRoots.Add(root);
                    InsertAt(
                        value.ArrangementParents,
                        parentIndex,
                        new ArrangementParentReference(ArrangementParentKind.MidiChannelRoot, root.Id),
                        "Arrangement parent");
                },
                (value, root) =>
                {
                    RemoveRequired(
                        value.ArrangementParents,
                        new ArrangementParentReference(ArrangementParentKind.MidiChannelRoot, root.Id),
                        "Arrangement parent");
                    RemoveRequired(value.MidiChannelRoots, root, "MIDI Channel Root");
                });
        });

    public static IProjectEditCommand ConfigureMidiChannelRoot(
        MidoraId rootId,
        string name,
        MidiChannelRootRoutingMode routingMode,
        int oneBasedPort,
        int oneBasedChannel,
        MidiChannelMode channelMode) =>
        Command("Configure MIDI Channel Root", project =>
        {
            MidiChannelRoot root = FindMidiChannelRoot(project, rootId);
            string normalized = ProjectTextRules.NormalizeShortText(
                name,
                allowEmpty: false,
                nameof(name));
            if (!Enum.IsDefined(routingMode)) throw new ArgumentOutOfRangeException(nameof(routingMode));
            if (!Enum.IsDefined(channelMode)) throw new ArgumentOutOfRangeException(nameof(channelMode));
            if (oneBasedPort is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(oneBasedPort));
            if (oneBasedChannel is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(oneBasedChannel));
            if (routingMode == MidiChannelRootRoutingMode.Fixed
                && project.MidiChannelRoots.Any(value => value.Id != rootId
                    && value.RoutingMode == MidiChannelRootRoutingMode.Fixed
                    && value.FixedZeroBasedPort == oneBasedPort - 1
                    && value.FixedZeroBasedChannel == oneBasedChannel - 1))
            {
                throw new InvalidOperationException(
                    "Another Fixed MIDI Channel Root already owns the selected Port.Channel.");
            }
            RootConfiguration before = new(
                root.Name,
                root.RoutingMode,
                root.FixedZeroBasedPort,
                root.FixedZeroBasedChannel,
                root.ChannelMode);
            RootConfiguration after = new(
                normalized,
                routingMode,
                checked((byte)(oneBasedPort - 1)),
                checked((byte)(oneBasedChannel - 1)),
                channelMode);
            return Prepared(
                before != after,
                RootChange(rootId),
                _ => ApplyRootConfiguration(root, after),
                _ => ApplyRootConfiguration(root, before));
        });

    public static IProjectEditCommand ReorderArrangementParent(MidoraId parentId, int newIndex) =>
        Command("Reorder Arrangement parent", project =>
        {
            int oldIndex = project.ArrangementParents.FindIndex(value => value.ParentId == parentId);
            if (oldIndex < 0) throw new ArgumentOutOfRangeException(nameof(parentId));
            ValidateExistingIndex(newIndex, project.ArrangementParents.Count, nameof(newIndex));
            ArrangementParentReference parent = project.ArrangementParents[oldIndex];
            return Prepared(
                oldIndex != newIndex,
                EverythingChange(),
                value => Move(value.ArrangementParents, parent, newIndex),
                value => Move(value.ArrangementParents, parent, oldIndex));
        });

    public static IProjectEditCommand DuplicateMidiChannelRoot(MidoraId rootId, string? name = null) =>
        Command("Duplicate MIDI Channel Root", project =>
        {
            MidiChannelRoot source = FindMidiChannelRoot(project, rootId);
            string normalized = ProjectTextRules.NormalizeShortText(
                name ?? $"{source.Name} Copy",
                allowEmpty: false,
                nameof(name));
            ArrangementParentReference sourceReference = new(
                ArrangementParentKind.MidiChannelRoot,
                source.Id);
            int parentIndex = project.ArrangementParents.IndexOf(sourceReference) + 1;
            if (parentIndex == 0)
            {
                throw new InvalidOperationException(
                    "The MIDI Channel Root is missing from the Arrangement parent order.");
            }
            List<PureMidiTrack> createdTracks = [];
            return DeferredCreate(
                EverythingChange(),
                value =>
                {
                    MidiChannelRoot copy = new(value)
                    {
                        Name = normalized,
                        RoutingMode = MidiChannelRootRoutingMode.Auto,
                        FixedZeroBasedPort = source.FixedZeroBasedPort,
                        FixedZeroBasedChannel = source.FixedZeroBasedChannel,
                        ChannelMode = source.ChannelMode
                    };
                    value.MidiChannelRoots.Add(copy);
                    foreach (MidoraId trackId in source.MidiTrackIds)
                    {
                        PureMidiTrack trackCopy = ClonePureMidiTrack(
                            value,
                            FindPureMidiTrack(value, trackId),
                            copy.Id);
                        value.PureMidiTracks.Add(trackCopy);
                        copy.MidiTrackIds.Add(trackCopy.Id);
                        createdTracks.Add(trackCopy);
                    }
                    value.ArrangementParents.Insert(
                        parentIndex,
                        new(ArrangementParentKind.MidiChannelRoot, copy.Id));
                    return copy;
                },
                (value, copy) =>
                {
                    EnsureMidiChannelRootIdAvailable(value, copy.Id);
                    value.MidiChannelRoots.Add(copy);
                    foreach (PureMidiTrack track in createdTracks)
                    {
                        EnsurePureMidiTrackIdAvailable(value, track.Id);
                        value.PureMidiTracks.Add(track);
                    }
                    InsertAt(
                        value.ArrangementParents,
                        parentIndex,
                        new ArrangementParentReference(ArrangementParentKind.MidiChannelRoot, copy.Id),
                        "Arrangement parent");
                },
                (value, copy) =>
                {
                    RemoveRequired(
                        value.ArrangementParents,
                        new ArrangementParentReference(ArrangementParentKind.MidiChannelRoot, copy.Id),
                        "Arrangement parent");
                    foreach (PureMidiTrack track in createdTracks)
                    {
                        RemoveRequired(value.PureMidiTracks, track, "Pure MIDI Track");
                    }
                    RemoveRequired(value.MidiChannelRoots, copy, "MIDI Channel Root");
                });
        });

    public static IProjectEditCommand DeleteMidiChannelRoot(
        MidoraId rootId,
        bool nonEmptyDeletionConfirmed) =>
        Command("Delete MIDI Channel Root", project =>
        {
            MidiChannelRoot root = FindMidiChannelRoot(project, rootId);
            PureMidiTrack[] children = root.MidiTrackIds
                .Select(id => FindPureMidiTrack(project, id))
                .ToArray();
            if (children.Length != 0 && !nonEmptyDeletionConfirmed)
            {
                throw new InvalidOperationException(
                    "Deleting a non-empty MIDI Channel Root subtree requires explicit confirmation.");
            }
            int rootRepositoryIndex = project.MidiChannelRoots.IndexOf(root);
            ArrangementParentReference parent = new(ArrangementParentKind.MidiChannelRoot, root.Id);
            int parentIndex = project.ArrangementParents.IndexOf(parent);
            Dictionary<MidoraId, int> trackIndices = children.ToDictionary(
                value => value.Id,
                value => project.PureMidiTracks.IndexOf(value));
            return Prepared(
                hasChanges: true,
                EverythingChange(),
                value =>
                {
                    RemoveRequired(value.ArrangementParents, parent, "Arrangement parent");
                    foreach (PureMidiTrack child in children)
                    {
                        RemoveRequired(value.PureMidiTracks, child, "Pure MIDI Track");
                    }
                    RemoveRequired(value.MidiChannelRoots, root, "MIDI Channel Root");
                },
                value =>
                {
                    EnsureMidiChannelRootIdAvailable(value, root.Id);
                    InsertAt(value.MidiChannelRoots, rootRepositoryIndex, root, "MIDI Channel Root");
                    InsertAt(value.ArrangementParents, parentIndex, parent, "Arrangement parent");
                    foreach (PureMidiTrack child in children.OrderBy(value => trackIndices[value.Id]))
                    {
                        InsertAt(
                            value.PureMidiTracks,
                            Math.Clamp(trackIndices[child.Id], 0, value.PureMidiTracks.Count),
                            child,
                            "Pure MIDI Track");
                    }
                });
        });

    public static IProjectEditCommand DeleteDamagedMidiChannelRoot(MidoraId placeholderId) =>
        Command("Delete damaged MIDI Channel Root", project =>
        {
            _ = project.DamagedMidiChannelRoots.SingleOrDefault(value => value.Id == placeholderId)
                ?? throw new ArgumentOutOfRangeException(nameof(placeholderId));
            DamagedMidiChannelRootDeletion? deletion = null;
            return Prepared(
                hasChanges: true,
                EverythingChange(),
                value =>
                {
                    DamagedMidiChannelRootDeletion applied =
                        DamagedProjectObjectEditing.DeleteMidiChannelRoot(value, placeholderId);
                    deletion ??= applied;
                },
                value => DamagedProjectObjectEditing.UndoDeleteMidiChannelRoot(
                    value,
                    deletion ?? throw new InvalidOperationException(
                        "The damaged MIDI Channel Root deletion was not applied.")));
        });

    public static IProjectEditCommand CreatePureMidiTrack(
        MidoraId rootId,
        string? name = null,
        int? insertionIndex = null) =>
        Command("Create Pure MIDI Track", project =>
        {
            MidiChannelRoot root = FindMidiChannelRoot(project, rootId);
            string normalized = ProjectTextRules.NormalizeShortText(
                name ?? "MIDI Track",
                allowEmpty: true,
                nameof(name));
            int childIndex = insertionIndex ?? root.MidiTrackIds.Count;
            ValidateInsertionIndex(childIndex, root.MidiTrackIds.Count, nameof(insertionIndex));
            return DeferredCreate(
                RootChange(root.Id),
                value =>
                {
                    PureMidiTrack track = new(value)
                    {
                        Name = normalized,
                        MidiChannelRootId = root.Id
                    };
                    value.PureMidiTracks.Add(track);
                    root.MidiTrackIds.Insert(childIndex, track.Id);
                    return track;
                },
                (value, track) =>
                {
                    EnsurePureMidiTrackIdAvailable(value, track.Id);
                    value.PureMidiTracks.Add(track);
                    InsertAt(root.MidiTrackIds, childIndex, track.Id, "Pure MIDI Track reference");
                },
                (value, track) =>
                {
                    RemoveRequired(root.MidiTrackIds, track.Id, "Pure MIDI Track reference");
                    RemoveRequired(value.PureMidiTracks, track, "Pure MIDI Track");
                });
        });

    public static IProjectEditCommand RenamePureMidiTrack(MidoraId trackId, string name) =>
        Command("Rename Pure MIDI Track", project =>
        {
            PureMidiTrack track = FindPureMidiTrack(project, trackId);
            string normalized = ProjectTextRules.NormalizeShortText(name, allowEmpty: true, nameof(name));
            string before = track.Name;
            return Prepared(
                !string.Equals(before, normalized, StringComparison.Ordinal),
                PureMidiTrackChange(trackId),
                _ => track.Name = normalized,
                _ => track.Name = before);
        });

    public static IProjectEditCommand MovePureMidiTrack(
        MidoraId trackId,
        MidoraId targetRootId,
        int targetIndex) =>
        Command("Move Pure MIDI Track", project =>
        {
            PureMidiTrack track = FindPureMidiTrack(project, trackId);
            MidiChannelRoot source = FindMidiChannelRoot(project, track.MidiChannelRootId);
            MidiChannelRoot target = FindMidiChannelRoot(project, targetRootId);
            int sourceIndex = source.MidiTrackIds.IndexOf(track.Id);
            if (sourceIndex < 0)
            {
                throw new InvalidOperationException(
                    "The Pure MIDI Track is missing from its Root child order.");
            }
            int targetCount = ReferenceEquals(source, target)
                ? target.MidiTrackIds.Count - 1
                : target.MidiTrackIds.Count;
            ValidateInsertionIndex(targetIndex, targetCount, nameof(targetIndex));
            MidoraId sourceRootId = source.Id;
            return Prepared(
                !ReferenceEquals(source, target) || sourceIndex != targetIndex,
                RootChange(source.Id, target.Id),
                _ =>
                {
                    RemoveRequired(source.MidiTrackIds, track.Id, "Pure MIDI Track reference");
                    target.MidiTrackIds.Insert(targetIndex, track.Id);
                    track.MidiChannelRootId = target.Id;
                },
                _ =>
                {
                    RemoveRequired(target.MidiTrackIds, track.Id, "Pure MIDI Track reference");
                    source.MidiTrackIds.Insert(sourceIndex, track.Id);
                    track.MidiChannelRootId = sourceRootId;
                });
        });

    public static IProjectEditCommand DuplicatePureMidiTrack(MidoraId trackId, string? name = null) =>
        Command("Duplicate Pure MIDI Track", project =>
        {
            PureMidiTrack source = FindPureMidiTrack(project, trackId);
            MidiChannelRoot root = FindMidiChannelRoot(project, source.MidiChannelRootId);
            int childIndex = root.MidiTrackIds.IndexOf(source.Id) + 1;
            if (childIndex == 0)
            {
                throw new InvalidOperationException(
                    "The Pure MIDI Track is missing from its Root child order.");
            }
            string normalized = ProjectTextRules.NormalizeShortText(
                name ?? $"{source.Name} Copy",
                allowEmpty: true,
                nameof(name));
            return DeferredCreate(
                RootChange(root.Id),
                value =>
                {
                    PureMidiTrack copy = ClonePureMidiTrack(value, source, root.Id);
                    copy.Name = normalized;
                    value.PureMidiTracks.Add(copy);
                    root.MidiTrackIds.Insert(childIndex, copy.Id);
                    return copy;
                },
                (value, copy) =>
                {
                    EnsurePureMidiTrackIdAvailable(value, copy.Id);
                    value.PureMidiTracks.Add(copy);
                    InsertAt(root.MidiTrackIds, childIndex, copy.Id, "Pure MIDI Track reference");
                },
                (value, copy) =>
                {
                    RemoveRequired(root.MidiTrackIds, copy.Id, "Pure MIDI Track reference");
                    RemoveRequired(value.PureMidiTracks, copy, "Pure MIDI Track");
                });
        });

    public static IProjectEditCommand DeletePureMidiTrack(
        MidoraId trackId,
        bool nonEmptyDeletionConfirmed) =>
        Command("Delete Pure MIDI Track", project =>
        {
            PureMidiTrack track = FindPureMidiTrack(project, trackId);
            if (track.Segments.Count != 0 && !nonEmptyDeletionConfirmed)
            {
                throw new InvalidOperationException(
                    "Deleting a non-empty Pure MIDI Track requires explicit confirmation.");
            }
            MidiChannelRoot root = FindMidiChannelRoot(project, track.MidiChannelRootId);
            int repositoryIndex = project.PureMidiTracks.IndexOf(track);
            int childIndex = root.MidiTrackIds.IndexOf(track.Id);
            return Prepared(
                hasChanges: true,
                RootChange(root.Id),
                value =>
                {
                    RemoveRequired(root.MidiTrackIds, track.Id, "Pure MIDI Track reference");
                    RemoveRequired(value.PureMidiTracks, track, "Pure MIDI Track");
                },
                value =>
                {
                    EnsurePureMidiTrackIdAvailable(value, track.Id);
                    InsertAt(value.PureMidiTracks, repositoryIndex, track, "Pure MIDI Track");
                    InsertAt(root.MidiTrackIds, childIndex, track.Id, "Pure MIDI Track reference");
                });
        });

    public static IProjectEditCommand DeleteDamagedPureMidiTrack(MidoraId placeholderId) =>
        Command("Delete damaged Pure MIDI Track", project =>
        {
            _ = project.DamagedPureMidiTracks.SingleOrDefault(value => value.Id == placeholderId)
                ?? throw new ArgumentOutOfRangeException(nameof(placeholderId));
            DamagedPureMidiTrackDeletion? deletion = null;
            return Prepared(
                hasChanges: true,
                EverythingChange(),
                value =>
                {
                    DamagedPureMidiTrackDeletion applied =
                        DamagedProjectObjectEditing.DeletePureMidiTrack(value, placeholderId);
                    deletion ??= applied;
                },
                value => DamagedProjectObjectEditing.UndoDeletePureMidiTrack(
                    value,
                    deletion ?? throw new InvalidOperationException(
                        "The damaged Pure MIDI Track deletion was not applied.")));
        });

    public static IProjectEditCommand CreateMidiSegment(
        MidoraId trackId,
        long projectStartTick,
        long lengthTicks,
        long contentOffsetTick = 0) =>
        Command("Create MIDI Segment", project =>
        {
            PureMidiTrack track = FindPureMidiTrack(project, trackId);
            ValidateSegmentRange(projectStartTick, lengthTicks, contentOffsetTick);
            EnsureNoMidiSegmentOverlap(track, null, projectStartTick, lengthTicks);
            return DeferredCreate(
                PureMidiTrackChange(track.Id),
                value =>
                {
                    MidiSegment segment = new(value)
                    {
                        ProjectStartTick = projectStartTick,
                        LengthTicks = lengthTicks,
                        ContentOffsetTick = contentOffsetTick
                    };
                    InsertMidiSegmentByTime(track.Segments, segment);
                    return segment;
                },
                (_, segment) => InsertMidiSegmentByTime(track.Segments, segment),
                (_, segment) => RemoveRequired(track.Segments, segment, "MIDI Segment"));
        });

    public static IProjectEditCommand CreateDirectMidiNote(
        MidoraId segmentId,
        long startTick,
        long lengthTicks,
        int key,
        int noteOnVelocity,
        int noteOffVelocity = 0) =>
        Command("Create Direct MIDI Note", project =>
        {
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            ValidateDirectMidiNote(startTick, lengthTicks, key, noteOnVelocity, noteOffVelocity);
            return DeferredCreate(
                PureMidiTrackChange(location.Track.Id),
                value =>
                {
                    DirectMidiNote note = new(value)
                    {
                        StartTick = startTick,
                        LengthTicks = lengthTicks,
                        Key = key,
                        NoteOnVelocity = noteOnVelocity,
                        NoteOffVelocity = noteOffVelocity
                    };
                    note.NoteOnOrder = note.Id.Value * 2;
                    note.NoteOffOrder = checked(note.NoteOnOrder + 1);
                    location.Segment.Notes.Add(note);
                    return note;
                },
                (_, note) => location.Segment.Notes.Add(note),
                (_, note) => location.Segment.Notes.Remove(note));
        });

    public static IProjectEditCommand CreateDirectMidiChannelEvent(
        MidoraId segmentId,
        long tick,
        DirectMidiChannelEventKind kind,
        int data1,
        int data2 = 0,
        long? order = null) =>
        Command("Create Direct MIDI Event", project =>
        {
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            ValidateDirectMidiEvent(tick, kind, data1, data2);
            return DeferredCreate(
                PureMidiTrackChange(location.Track.Id),
                value =>
                {
                    DirectMidiChannelEvent directEvent = new(value)
                    {
                        Tick = tick,
                        Kind = kind,
                        Data1 = data1,
                        Data2 = data2,
                        Order = order ?? value.NextStableId
                    };
                    location.Segment.ChannelEvents.Add(directEvent);
                    return directEvent;
                },
                (_, directEvent) => location.Segment.ChannelEvents.Add(directEvent),
                (_, directEvent) => RemoveRequired(
                    location.Segment.ChannelEvents,
                    directEvent,
                    "Direct MIDI Event"));
        });

    private static MidiChannelRoot FindMidiChannelRoot(MidoraProject project, MidoraId rootId) =>
        project.MidiChannelRoots.SingleOrDefault(value => value.Id == rootId)
        ?? throw new ArgumentOutOfRangeException(nameof(rootId));

    private static PureMidiTrack FindPureMidiTrack(MidoraProject project, MidoraId trackId) =>
        project.PureMidiTracks.SingleOrDefault(value => value.Id == trackId)
        ?? throw new ArgumentOutOfRangeException(nameof(trackId));

    private static MidiSegmentLocation FindMidiSegment(MidoraProject project, MidoraId segmentId)
    {
        MidiSegmentLocation? result = null;
        foreach (PureMidiTrack track in project.PureMidiTracks)
        {
            for (int index = 0; index < track.Segments.Count; index++)
            {
                MidiSegment segment = track.Segments[index];
                if (segment.Id != segmentId) continue;
                if (result.HasValue)
                {
                    throw new InvalidOperationException("The MIDI Segment stable ID is duplicated.");
                }
                result = new(track, segment, index);
            }
        }
        return result ?? throw new ArgumentOutOfRangeException(nameof(segmentId));
    }

    private static void EnsureNoMidiSegmentOverlap(
        PureMidiTrack track,
        MidiSegment? excluded,
        long projectStartTick,
        long lengthTicks)
    {
        TickRange candidate = new(projectStartTick, checked(projectStartTick + lengthTicks));
        if (track.Segments.Any(value => !ReferenceEquals(value, excluded)
            && candidate.Intersects(value.ProjectRange)))
        {
            throw new InvalidOperationException(
                "MIDI Segments on the same Pure MIDI Track cannot overlap.");
        }
    }

    private static void ValidateDirectMidiNote(
        long startTick,
        long lengthTicks,
        int key,
        int noteOnVelocity,
        int noteOffVelocity)
    {
        if (startTick < 0) throw new ArgumentOutOfRangeException(nameof(startTick));
        if (lengthTicks <= 0) throw new ArgumentOutOfRangeException(nameof(lengthTicks));
        _ = checked(startTick + lengthTicks);
        if (key is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(key));
        if (noteOnVelocity is < 1 or > 127) throw new ArgumentOutOfRangeException(nameof(noteOnVelocity));
        if (noteOffVelocity is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(noteOffVelocity));
    }

    private static void ValidateDirectMidiEvent(
        long tick,
        DirectMidiChannelEventKind kind,
        int data1,
        int data2)
    {
        if (tick < 0) throw new ArgumentOutOfRangeException(nameof(tick));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (data1 is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(data1));
        bool oneDataByte = kind is DirectMidiChannelEventKind.ProgramChange
            or DirectMidiChannelEventKind.ChannelPressure;
        if (data2 is < 0 or > 127 || oneDataByte && data2 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(data2));
        }
    }

    private static PureMidiTrack ClonePureMidiTrack(
        MidoraProject project,
        PureMidiTrack source,
        MidoraId targetRootId)
    {
        PureMidiTrack result = new(project)
        {
            Name = source.Name,
            MidiChannelRootId = targetRootId,
            Color = source.Color
        };
        foreach (MidiSegment segment in source.Segments)
        {
            MidiSegment segmentCopy = new(project)
            {
                ProjectStartTick = segment.ProjectStartTick,
                LengthTicks = segment.LengthTicks,
                ContentOffsetTick = segment.ContentOffsetTick
            };
            foreach (DirectMidiNote note in segment.Notes)
            {
                segmentCopy.Notes.Add(new DirectMidiNote(project)
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
            foreach (DirectMidiChannelEvent directEvent in segment.ChannelEvents)
            {
                segmentCopy.ChannelEvents.Add(new DirectMidiChannelEvent(project)
                {
                    Tick = directEvent.Tick,
                    Kind = directEvent.Kind,
                    Data1 = directEvent.Data1,
                    Data2 = directEvent.Data2,
                    Order = directEvent.Order
                });
            }
            foreach (OpaqueMidiEvent opaque in segment.OpaqueEvents)
            {
                segmentCopy.OpaqueEvents.Add(new OpaqueMidiEvent(project)
                {
                    Tick = opaque.Tick,
                    Kind = opaque.Kind,
                    MetaType = opaque.MetaType,
                    Payload = opaque.Payload.ToArray(),
                    Order = opaque.Order
                });
            }
            result.Segments.Add(segmentCopy);
        }
        return result;
    }

    private static void InsertMidiSegmentByTime(List<MidiSegment> segments, MidiSegment segment)
    {
        int index = 0;
        while (index < segments.Count
            && (segments[index].ProjectStartTick < segment.ProjectStartTick
                || segments[index].ProjectStartTick == segment.ProjectStartTick
                && segments[index].Id.CompareTo(segment.Id) < 0))
        {
            index++;
        }
        segments.Insert(index, segment);
    }

    private static void EnsureMidiChannelRootIdAvailable(MidoraProject project, MidoraId id)
    {
        if (project.MidiChannelRoots.Any(value => value.Id == id)
            || project.DamagedMidiChannelRoots.Any(value => value.Id == id))
        {
            throw new InvalidOperationException("The MIDI Channel Root stable ID is already present.");
        }
    }

    private static void EnsurePureMidiTrackIdAvailable(MidoraProject project, MidoraId id)
    {
        if (project.PureMidiTracks.Any(value => value.Id == id)
            || project.DamagedPureMidiTracks.Any(value => value.Id == id))
        {
            throw new InvalidOperationException("The Pure MIDI Track stable ID is already present.");
        }
    }

    private static ProjectChangeSet RootChange(params MidoraId[] rootIds)
    {
        ProjectChangeSet result = new();
        result.MidiChannelRootIds.UnionWith(rootIds);
        return result;
    }

    private static ProjectChangeSet PureMidiTrackChange(params MidoraId[] trackIds)
    {
        ProjectChangeSet result = new();
        result.PureMidiTrackIds.UnionWith(trackIds);
        return result;
    }

    private static void ApplyRootConfiguration(MidiChannelRoot root, RootConfiguration value)
    {
        root.Name = value.Name;
        root.RoutingMode = value.RoutingMode;
        root.FixedZeroBasedPort = value.Port;
        root.FixedZeroBasedChannel = value.Channel;
        root.ChannelMode = value.ChannelMode;
    }

    private readonly record struct RootConfiguration(
        string Name,
        MidiChannelRootRoutingMode RoutingMode,
        byte Port,
        byte Channel,
        MidiChannelMode ChannelMode);

    private readonly record struct MidiSegmentLocation(
        PureMidiTrack Track,
        MidiSegment Segment,
        int Index);
}
