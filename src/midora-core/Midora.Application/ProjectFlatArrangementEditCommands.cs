using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand RebindEventInstrumentUsage(
        MidoraId eventInstrumentUsageId,
        MidoraId eventInstrumentId) =>
        Command("Change shared Event Instrument binding", project =>
        {
            EventInstrumentUsage usage = project.EventInstrumentUsages.SingleOrDefault(
                    value => value.Id == eventInstrumentUsageId)
                ?? throw new ArgumentOutOfRangeException(nameof(eventInstrumentUsageId));
            EventInstrument target = FindEventInstrument(project, eventInstrumentId);
            MidoraId oldInstrumentId = usage.EventInstrumentId;
            LogicalTrack[] members = project.Tracks
                .Where(value => value.EventInstrumentUsageId == usage.Id)
                .ToArray();
            if (members.Length == 0)
            {
                throw new InvalidOperationException(
                    "The Event Instrument Usage must have at least one Logical Track member.");
            }
            string?[] oldBoundNames = members
                .Select(value => value.LastBoundEventInstrumentName)
                .ToArray();
            return Prepared(
                oldInstrumentId != target.Id,
                EverythingChange(),
                _ =>
                {
                    usage.EventInstrumentId = target.Id;
                    foreach (LogicalTrack member in members)
                        member.LastBoundEventInstrumentName = target.Name;
                },
                _ =>
                {
                    usage.EventInstrumentId = oldInstrumentId;
                    for (int index = 0; index < members.Length; index++)
                        members[index].LastBoundEventInstrumentName = oldBoundNames[index];
                });
        });

    public static IProjectEditCommand MoveArrangementTrackOutsideSharedGroup(
        MidoraId trackId,
        int newIndex) =>
        Command("Move arrangement track outside shared group", project =>
        {
            ArrangementTrackReference reference = project.ArrangementTracks
                .SingleOrDefault(value => value.TrackId == trackId);
            if (reference == default) throw new ArgumentOutOfRangeException(nameof(trackId));
            ValidateExistingIndex(newIndex, project.ArrangementTracks.Count, nameof(newIndex));
            ArrangementTrackReference[] beforeOrder = project.ArrangementTracks.ToArray();

            LogicalTrack? logicalTrack = reference.Kind == ArrangementTrackKind.LogicalTrack
                ? FindTrack(project, trackId)
                : null;
            EventInstrumentUsage? oldUsage = logicalTrack is not null
                ? RequireUsage(project, logicalTrack)
                : null;
            bool splitLogicalUsage = oldUsage is not null
                && project.Tracks.Count(value => value.EventInstrumentUsageId == oldUsage.Id) > 1;
            EventInstrumentUsage? independentUsage = null;

            PureMidiTrack? midiTrack = reference.Kind == ArrangementTrackKind.PureMidiTrack
                ? FindPureMidiTrack(project, trackId)
                : null;
            MidiChannelRoot? oldRoot = midiTrack is not null
                ? FindMidiChannelRoot(project, midiTrack.MidiChannelRootId)
                : null;
            bool splitAutoRoot = oldRoot is { RoutingMode: MidiChannelRootRoutingMode.Auto }
                && project.PureMidiTracks.Count(value => value.MidiChannelRootId == oldRoot.Id) > 1;
            MidiChannelRoot? independentRoot = null;
            MidoraId? sharedIdentity = splitLogicalUsage
                ? oldUsage!.Id
                : splitAutoRoot
                    ? oldRoot!.Id
                    : null;
            ArrangementTrackReference[] afterOrder = sharedIdentity.HasValue
                ? MoveReferenceOutsideGroup(
                    project,
                    beforeOrder,
                    reference,
                    newIndex,
                    sharedIdentity.Value)
                : MoveReference(beforeOrder, reference, newIndex);
            EnsureFormalGroupContiguity(project, afterOrder, reference, null);

            return Prepared(
                !beforeOrder.SequenceEqual(afterOrder) || splitLogicalUsage || splitAutoRoot,
                EverythingChange(),
                value =>
                {
                    if (splitLogicalUsage)
                    {
                        if (independentUsage is null)
                        {
                            independentUsage = new(value)
                            {
                                EventInstrumentId = oldUsage!.EventInstrumentId
                            };
                        }
                        else
                        {
                            EnsureEventInstrumentUsageIdAvailable(value, independentUsage.Id);
                        }
                        value.EventInstrumentUsages.Add(independentUsage);
                        logicalTrack!.EventInstrumentUsageId = independentUsage.Id;
                    }
                    if (splitAutoRoot)
                    {
                        if (independentRoot is null)
                        {
                            independentRoot = new(value)
                            {
                                Name = oldRoot!.Name,
                                RoutingMode = MidiChannelRootRoutingMode.Auto,
                                ChannelMode = oldRoot.ChannelMode
                            };
                        }
                        else
                        {
                            EnsureMidiChannelRootIdAvailable(value, independentRoot.Id);
                        }
                        value.MidiChannelRoots.Add(independentRoot);
                        midiTrack!.MidiChannelRootId = independentRoot.Id;
                    }
                    ReplaceArrangementOrder(value, beforeOrder, afterOrder);
                },
                value =>
                {
                    ReplaceArrangementOrder(value, afterOrder, beforeOrder);
                    if (splitLogicalUsage)
                    {
                        logicalTrack!.EventInstrumentUsageId = oldUsage!.Id;
                        RemoveRequired(
                            value.EventInstrumentUsages,
                            independentUsage!,
                            "Event Instrument Usage");
                    }
                    if (splitAutoRoot)
                    {
                        midiTrack!.MidiChannelRootId = oldRoot!.Id;
                        RemoveRequired(value.MidiChannelRoots, independentRoot!, "MIDI Channel Root");
                    }
                });
        });

    public static IProjectEditCommand MoveArrangementTrackIntoSharedGroup(
        MidoraId trackId,
        MidoraId targetTrackId) =>
        Command("Move arrangement track into shared group", project =>
        {
            ArrangementTrackReference sourceReference = project.ArrangementTracks
                .SingleOrDefault(value => value.TrackId == trackId);
            ArrangementTrackReference targetReference = project.ArrangementTracks
                .SingleOrDefault(value => value.TrackId == targetTrackId);
            if (sourceReference == default || targetReference == default || trackId == targetTrackId)
                throw new ArgumentOutOfRangeException(nameof(trackId));
            if (sourceReference.Kind != targetReference.Kind)
                throw new InvalidOperationException("Only Tracks of the same type can share state.");

            ArrangementTrackReference[] beforeOrder = project.ArrangementTracks.ToArray();
            MidoraId targetGroupId;
            LogicalTrack? logicalTrack = null;
            EventInstrumentUsage? oldUsage = null;
            EventInstrumentUsage? targetUsage = null;
            EventInstrument? targetDefinition = null;
            PureMidiTrack? midiTrack = null;
            MidiChannelRoot? oldRoot = null;
            MidiChannelRoot? targetRoot = null;
            if (sourceReference.Kind == ArrangementTrackKind.LogicalTrack)
            {
                logicalTrack = FindTrack(project, trackId);
                LogicalTrack targetTrack = FindTrack(project, targetTrackId);
                oldUsage = logicalTrack.EventInstrumentUsageId is MidoraId sourceUsageId
                    ? project.EventInstrumentUsages.SingleOrDefault(value => value.Id == sourceUsageId)
                        ?? throw new InvalidOperationException(
                            "The source Logical Track references a missing Event Instrument Usage.")
                    : null;
                targetUsage = RequireUsage(project, targetTrack);
                targetDefinition = FindEventInstrument(project, targetUsage.EventInstrumentId);
                targetGroupId = targetUsage.Id;
            }
            else
            {
                midiTrack = FindPureMidiTrack(project, trackId);
                PureMidiTrack targetTrack = FindPureMidiTrack(project, targetTrackId);
                oldRoot = FindMidiChannelRoot(project, midiTrack.MidiChannelRootId);
                targetRoot = FindMidiChannelRoot(project, targetTrack.MidiChannelRootId);
                targetGroupId = targetRoot.Id;
            }

            List<ArrangementTrackReference> after = beforeOrder.Where(value => value != sourceReference).ToList();
            bool targetRequiresContiguity = sourceReference.Kind == ArrangementTrackKind.LogicalTrack
                || targetRoot?.RoutingMode == MidiChannelRootRoutingMode.Auto;
            int targetInsertionIndex = targetRequiresContiguity
                ? after.FindLastIndex(value => value.Kind == sourceReference.Kind
                    && ResolveSharedIdentity(project, value) == targetGroupId) + 1
                : after.IndexOf(targetReference) + 1;
            if (targetInsertionIndex <= 0)
                throw new InvalidOperationException("The target shared group has no Arrangement member.");
            after.Insert(targetInsertionIndex, sourceReference);
            ArrangementTrackReference[] afterOrder = after.ToArray();
            EnsureFormalGroupContiguity(
                project,
                afterOrder,
                sourceReference,
                targetGroupId);
            bool removeOldUsage = oldUsage is not null && oldUsage.Id != targetUsage!.Id
                && project.Tracks.Count(value => value.EventInstrumentUsageId == oldUsage.Id) == 1;
            int oldUsageIndex = removeOldUsage ? project.EventInstrumentUsages.IndexOf(oldUsage!) : -1;
            bool removeOldRoot = oldRoot is not null && oldRoot.Id != targetRoot!.Id
                && project.PureMidiTracks.Count(value => value.MidiChannelRootId == oldRoot.Id) == 1;
            int oldRootIndex = removeOldRoot ? project.MidiChannelRoots.IndexOf(oldRoot!) : -1;
            MidoraId? oldUsageId = logicalTrack?.EventInstrumentUsageId;
            string? oldBoundName = logicalTrack?.LastBoundEventInstrumentName;

            return Prepared(
                true,
                EverythingChange(),
                value =>
                {
                    if (logicalTrack is not null)
                    {
                        if (removeOldUsage)
                            RemoveRequired(value.EventInstrumentUsages, oldUsage!, "Event Instrument Usage");
                        logicalTrack.EventInstrumentUsageId = targetUsage!.Id;
                        logicalTrack.LastBoundEventInstrumentName = targetDefinition!.Name;
                    }
                    else
                    {
                        midiTrack!.MidiChannelRootId = targetRoot!.Id;
                        if (removeOldRoot)
                            RemoveRequired(value.MidiChannelRoots, oldRoot!, "MIDI Channel Root");
                    }
                    ReplaceArrangementOrder(value, beforeOrder, afterOrder);
                },
                value =>
                {
                    ReplaceArrangementOrder(value, afterOrder, beforeOrder);
                    if (logicalTrack is not null)
                    {
                        logicalTrack.EventInstrumentUsageId = oldUsageId;
                        logicalTrack.LastBoundEventInstrumentName = oldBoundName;
                        if (removeOldUsage)
                            InsertAt(value.EventInstrumentUsages, oldUsageIndex, oldUsage!, "Event Instrument Usage");
                    }
                    else
                    {
                        if (removeOldRoot)
                            InsertAt(value.MidiChannelRoots, oldRootIndex, oldRoot!, "MIDI Channel Root");
                        midiTrack!.MidiChannelRootId = oldRoot!.Id;
                    }
                });
        });

    public static IProjectEditCommand MoveArrangementSharedGroup(
        MidoraId sharedGroupId,
        ArrangementTrackKind kind,
        MidoraId targetTrackId,
        bool insertAfter) =>
        Command("Move shared arrangement group", project =>
        {
            ArrangementTrackReference[] beforeOrder = project.ArrangementTracks.ToArray();
            ArrangementTrackReference[] members = beforeOrder
                .Where(value => value.Kind == kind
                    && ResolveSharedIdentity(project, value) == sharedGroupId)
                .ToArray();
            if (members.Length < 2)
                throw new InvalidOperationException("The selected shared group no longer exists.");
            ArrangementTrackReference target = beforeOrder.SingleOrDefault(value => value.TrackId == targetTrackId);
            if (target == default) throw new ArgumentOutOfRangeException(nameof(targetTrackId));
            if (members.Contains(target))
                return Prepared(false, NoCompilationChange(), _ => { }, _ => { });
            List<ArrangementTrackReference> after = beforeOrder.Except(members).ToList();
            MidoraId? targetGroupId = ResolveSharedIdentity(project, target);
            ArrangementTrackReference[] targetMembers = targetGroupId.HasValue
                && IsContiguousGroup(project, target, targetGroupId.Value)
                ? after.Where(value => value.Kind == target.Kind
                    && ResolveSharedIdentity(project, value) == targetGroupId.Value).ToArray()
                : [target];
            int insertionIndex = insertAfter
                ? after.IndexOf(targetMembers[^1]) + 1
                : after.IndexOf(targetMembers[0]);
            after.InsertRange(insertionIndex, members);
            ArrangementTrackReference[] afterOrder = after.ToArray();
            EnsureFormalGroupContiguity(project, afterOrder);
            return Prepared(
                !beforeOrder.SequenceEqual(afterOrder),
                EverythingChange(),
                value => ReplaceArrangementOrder(value, beforeOrder, afterOrder),
                value => ReplaceArrangementOrder(value, afterOrder, beforeOrder));
        });

    public static IProjectEditCommand MakeArrangementSharedGroupIndependent(
        MidoraId sharedGroupId,
        ArrangementTrackKind kind) =>
        Command("Make shared arrangement group independent", project =>
        {
            if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            if (kind == ArrangementTrackKind.LogicalTrack)
            {
                EventInstrumentUsage usage = project.EventInstrumentUsages.SingleOrDefault(
                        value => value.Id == sharedGroupId)
                    ?? throw new ArgumentOutOfRangeException(nameof(sharedGroupId));
                LogicalTrack[] members = project.LogicalTracksInArrangementOrder()
                    .Where(value => value.EventInstrumentUsageId == usage.Id)
                    .ToArray();
                if (members.Length < 2)
                    throw new InvalidOperationException("The selected Logical Track group is not shared.");
                List<EventInstrumentUsage> createdUsages = [];
                return Prepared(
                    true,
                    EverythingChange(),
                    value =>
                    {
                        for (int index = 1; index < members.Length; index++)
                        {
                            EventInstrumentUsage independent;
                            if (createdUsages.Count < index)
                            {
                                independent = new(value)
                                {
                                    EventInstrumentId = usage.EventInstrumentId
                                };
                                createdUsages.Add(independent);
                            }
                            else
                            {
                                independent = createdUsages[index - 1];
                                EnsureEventInstrumentUsageIdAvailable(value, independent.Id);
                            }
                            value.EventInstrumentUsages.Add(independent);
                            members[index].EventInstrumentUsageId = independent.Id;
                        }
                    },
                    value =>
                    {
                        for (int index = members.Length - 1; index >= 1; index--)
                        {
                            members[index].EventInstrumentUsageId = usage.Id;
                            RemoveRequired(
                                value.EventInstrumentUsages,
                                createdUsages[index - 1],
                                "Event Instrument Usage");
                        }
                    });
            }

            MidiChannelRoot root = project.MidiChannelRoots.SingleOrDefault(
                    value => value.Id == sharedGroupId)
                ?? throw new ArgumentOutOfRangeException(nameof(sharedGroupId));
            if (root.RoutingMode != MidiChannelRootRoutingMode.Auto)
            {
                throw new InvalidOperationException(
                    "Only an Auto MIDI Channel Root can be dissolved into independent routes.");
            }
            PureMidiTrack[] midiMembers = project.PureMidiTracksInArrangementOrder()
                .Where(value => value.MidiChannelRootId == root.Id)
                .ToArray();
            if (midiMembers.Length < 2)
                throw new InvalidOperationException("The selected MIDI Track group is not shared.");
            List<MidiChannelRoot> createdRoots = [];
            return Prepared(
                true,
                EverythingChange(),
                value =>
                {
                    for (int index = 1; index < midiMembers.Length; index++)
                    {
                        MidiChannelRoot independent;
                        if (createdRoots.Count < index)
                        {
                            independent = new(value)
                            {
                                Name = root.Name,
                                RoutingMode = MidiChannelRootRoutingMode.Auto,
                                ChannelMode = root.ChannelMode
                            };
                            createdRoots.Add(independent);
                        }
                        else
                        {
                            independent = createdRoots[index - 1];
                            EnsureMidiChannelRootIdAvailable(value, independent.Id);
                        }
                        value.MidiChannelRoots.Add(independent);
                        midiMembers[index].MidiChannelRootId = independent.Id;
                    }
                },
                value =>
                {
                    for (int index = midiMembers.Length - 1; index >= 1; index--)
                    {
                        midiMembers[index].MidiChannelRootId = root.Id;
                        RemoveRequired(
                            value.MidiChannelRoots,
                            createdRoots[index - 1],
                            "MIDI Channel Root");
                    }
                });
        });

    /// <summary>
    /// Moves any Arrangement Track in the one global mixed Track order.
    /// </summary>
    public static IProjectEditCommand MoveArrangementTrack(MidoraId trackId, int newIndex) =>
        Command("Move arrangement track", project =>
        {
            ArrangementTrackReference reference = project.ArrangementTracks
                .SingleOrDefault(value => value.TrackId == trackId);
            if (reference == default)
            {
                throw new ArgumentOutOfRangeException(nameof(trackId));
            }
            int oldIndex = project.ArrangementTracks.IndexOf(reference);
            ValidateExistingIndex(newIndex, project.ArrangementTracks.Count, nameof(newIndex));
            ArrangementTrackReference[] beforeOrder = project.ArrangementTracks.ToArray();
            ArrangementTrackReference[] afterOrder = MoveReference(beforeOrder, reference, newIndex);
            EnsureFormalGroupContiguity(project, afterOrder);
            return Prepared(
                oldIndex != newIndex,
                EverythingChange(),
                value => ReplaceArrangementOrder(value, beforeOrder, afterOrder),
                value => ReplaceArrangementOrder(value, afterOrder, beforeOrder));
        });

    /// <summary>
    /// Makes a Logical Track join the same stateful Usage as another Track.
    /// If the Tracks resolve to different Definitions, the moving Track is rebound
    /// to the target Usage's Definition in the same atomic edit.
    /// </summary>
    public static IProjectEditCommand ShareLogicalTrackUsageWith(
        MidoraId trackId,
        MidoraId targetTrackId) =>
        MoveArrangementTrackIntoSharedGroup(trackId, targetTrackId);

    public static IProjectEditCommand MakeLogicalTrackUsageIndependent(MidoraId trackId) =>
        Command("Make event instrument state independent", project =>
        {
            LogicalTrack track = FindTrack(project, trackId);
            EventInstrumentUsage sourceUsage = RequireUsage(project, track);
            int memberCount = project.Tracks.Count(
                value => value.EventInstrumentUsageId == sourceUsage.Id);
            if (memberCount == 1)
            {
                return Prepared(false, NoCompilationChange(), _ => { }, _ => { });
            }
            ArrangementTrackReference reference = new(
                ArrangementTrackKind.LogicalTrack,
                track.Id);
            ArrangementTrackReference[] beforeOrder = project.ArrangementTracks.ToArray();
            int currentIndex = project.ArrangementTracks.IndexOf(reference);
            if (currentIndex < 0)
            {
                throw new InvalidOperationException(
                    "The Logical Track is missing from the Arrangement Track order.");
            }
            ArrangementTrackReference[] afterOrder = MoveReferenceOutsideGroup(
                project,
                beforeOrder,
                reference,
                currentIndex,
                sourceUsage.Id);
            EnsureFormalGroupContiguity(
                project,
                afterOrder,
                reference,
                null);
            EventInstrumentUsage? independent = null;
            return Prepared(
                true,
                EverythingChange(),
                value =>
                {
                    if (independent is null)
                    {
                        independent = new(value)
                        {
                            EventInstrumentId = sourceUsage.EventInstrumentId
                        };
                    }
                    else
                    {
                        EnsureEventInstrumentUsageIdAvailable(value, independent.Id);
                    }
                    value.EventInstrumentUsages.Add(independent);
                    track.EventInstrumentUsageId = independent.Id;
                    ReplaceArrangementOrder(value, beforeOrder, afterOrder);
                },
                value =>
                {
                    ReplaceArrangementOrder(value, afterOrder, beforeOrder);
                    track.EventInstrumentUsageId = sourceUsage.Id;
                    RemoveRequired(
                        value.EventInstrumentUsages,
                        independent!,
                        "Event Instrument Usage");
                });
        });

    private static EventInstrumentUsage RequireUsage(
        MidoraProject project,
        LogicalTrack track) =>
        track.EventInstrumentUsageId is MidoraId usageId
            ? project.EventInstrumentUsages.SingleOrDefault(value => value.Id == usageId)
                ?? throw new InvalidOperationException(
                    "The Logical Track references a missing Event Instrument Usage.")
            : throw new InvalidOperationException(
                "The Logical Track is not bound to an Event Instrument Usage.");

    private static ArrangementTrackReference[] MoveReference(
        IReadOnlyList<ArrangementTrackReference> source,
        ArrangementTrackReference reference,
        int targetIndex)
    {
        List<ArrangementTrackReference> result = source.ToList();
        result.Remove(reference);
        result.Insert(targetIndex, reference);
        return result.ToArray();
    }

    private static ArrangementTrackReference[] MoveReferenceOutsideGroup(
        MidoraProject project,
        IReadOnlyList<ArrangementTrackReference> source,
        ArrangementTrackReference reference,
        int requestedFinalIndex,
        MidoraId oldGroupId)
    {
        List<ArrangementTrackReference> result = source.ToList();
        int oldIndex = result.IndexOf(reference);
        if (oldIndex < 0)
        {
            throw new InvalidOperationException(
                "The Arrangement Track is no longer present.");
        }
        result.RemoveAt(oldIndex);
        int insertionIndex = Math.Clamp(requestedFinalIndex, 0, result.Count);
        int first = result.FindIndex(value => value.Kind == reference.Kind
            && ResolveSharedIdentity(project, value) == oldGroupId);
        int last = result.FindLastIndex(value => value.Kind == reference.Kind
            && ResolveSharedIdentity(project, value) == oldGroupId);
        if (first >= 0 && insertionIndex > first && insertionIndex <= last)
        {
            insertionIndex = last + 1;
        }
        result.Insert(insertionIndex, reference);
        return result.ToArray();
    }

    private static void EnsureFormalGroupContiguity(
        MidoraProject project,
        IReadOnlyList<ArrangementTrackReference> order,
        ArrangementTrackReference? reassignedReference = null,
        MidoraId? reassignedGroupId = null)
    {
        Dictionary<MidoraId, List<int>> logicalPositions = [];
        Dictionary<MidoraId, List<int>> autoMidiPositions = [];
        for (int index = 0; index < order.Count; index++)
        {
            ArrangementTrackReference reference = order[index];
            MidoraId? identity = reassignedReference.HasValue
                && reference == reassignedReference.Value
                    ? reassignedGroupId
                    : ResolveSharedIdentity(project, reference);
            if (!identity.HasValue)
            {
                continue;
            }
            if (reference.Kind == ArrangementTrackKind.LogicalTrack)
            {
                logicalPositions.TryAdd(identity.Value, []);
                logicalPositions[identity.Value].Add(index);
                continue;
            }
            MidiChannelRoot root = FindMidiChannelRoot(project, identity.Value);
            if (root.RoutingMode == MidiChannelRootRoutingMode.Auto)
            {
                autoMidiPositions.TryAdd(identity.Value, []);
                autoMidiPositions[identity.Value].Add(index);
            }
        }
        if (logicalPositions.Values.Concat(autoMidiPositions.Values).Any(
            positions => positions.Count > 1
                && positions[^1] - positions[0] + 1 != positions.Count))
        {
            throw new InvalidOperationException(
                "The Arrangement edit would split a shared Track group.");
        }
    }

    private static MidoraId? ResolveSharedIdentity(
        MidoraProject project,
        ArrangementTrackReference reference) => reference.Kind switch
        {
            ArrangementTrackKind.LogicalTrack => FindTrack(project, reference.TrackId)
                .EventInstrumentUsageId,
            ArrangementTrackKind.PureMidiTrack => FindPureMidiTrack(project, reference.TrackId)
                .MidiChannelRootId,
            _ => throw new InvalidOperationException("Unknown Arrangement Track kind.")
        };

    private static bool IsContiguousGroup(
        MidoraProject project,
        ArrangementTrackReference reference,
        MidoraId groupId) => reference.Kind switch
        {
            ArrangementTrackKind.LogicalTrack => true,
            ArrangementTrackKind.PureMidiTrack => FindMidiChannelRoot(project, groupId)
                .RoutingMode == MidiChannelRootRoutingMode.Auto,
            _ => false
        };

    private static void ReplaceArrangementOrder(
        MidoraProject project,
        IReadOnlyList<ArrangementTrackReference> expected,
        IReadOnlyList<ArrangementTrackReference> replacement)
    {
        if (!project.ArrangementTracks.SequenceEqual(expected))
        {
            throw new InvalidOperationException(
                "Arrangement Track membership changed while applying a reorder operation.");
        }
        project.ArrangementTracks.Clear();
        project.ArrangementTracks.AddRange(replacement);
    }
}
