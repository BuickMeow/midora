using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectObjectClipboard
{
    public static ProjectObjectClipboardPayload CopyEventInstrument(
        ProjectDocumentSession document,
        MidoraId eventInstrumentId)
    {
        ArgumentNullException.ThrowIfNull(document);
        EventInstrument source = document.Project.EventInstruments
            .SingleOrDefault(value => value.Id == eventInstrumentId)
            ?? throw new ArgumentOutOfRangeException(nameof(eventInstrumentId));
        MidoraProject snapshotProject = new(document.Project.TicksPerQuarterNote);
        EventInstrument snapshot = EventInstrumentLibrary.CopyInto(
            snapshotProject,
            source,
            source.Name,
            folderId: null);
        Dictionary<MidoraId, LogicalTrack> tracks = document.Project.Tracks.ToDictionary(value => value.Id);
        LogicalTrackClipboardSnapshot[] childTracks = source.LogicalTrackIds
            .Where(tracks.ContainsKey)
            .Select(id => tracks[id])
            .Select(track => new LogicalTrackClipboardSnapshot(
                track.Name,
                track.EventInstrumentId,
                track.LastBoundEventInstrumentName,
                track.ColorOverride,
                track.Segments.Select(segment => SnapshotSegment(
                    segment,
                    trackOffset: 0,
                    startOffset: segment.ProjectStartTick)).ToArray()))
            .ToArray();
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.EventInstrument,
            1,
            $"Event Instrument: {source.Name}",
            new EventInstrumentClipboardData(snapshot, childTracks));
    }

    public static IProjectEditCommand CreatePasteEventInstrumentCommand(
        ProjectDocumentSession targetDocument,
        ProjectObjectClipboardPayload payload,
        MidoraId? targetFolderId = null,
        int? insertionIndex = null)
    {
        EventInstrumentClipboardData data = RequirePayload<EventInstrumentClipboardData>(
            targetDocument,
            payload,
            ProjectObjectClipboardKind.EventInstrument);
        return ProjectDomainEditCommands.PasteEventInstrumentClipboard(
            data.Snapshot,
            data.Tracks,
            insertionIndex);
    }
}

internal sealed record EventInstrumentClipboardData(
    EventInstrument Snapshot,
    LogicalTrackClipboardSnapshot[] Tracks) : ProjectObjectClipboardData;

public static partial class ProjectDomainEditCommands
{
    internal static IProjectEditCommand PasteEventInstrumentClipboard(
        EventInstrument snapshot,
        IReadOnlyList<LogicalTrackClipboardSnapshot> trackSnapshots,
        int? insertionIndex) =>
        Command("Paste event instrument", project =>
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            ArgumentNullException.ThrowIfNull(trackSnapshots);
            int index = insertionIndex ?? project.ArrangementParents.Count;
            ValidateInsertionIndex(index, project.ArrangementParents.Count, nameof(insertionIndex));
            EventInstrument? copy = null;
            LogicalTrack[]? trackCopies = null;
            return Prepared(
                hasChanges: true,
                EverythingChange(),
                owner =>
                {
                    if (copy is null)
                    {
                        copy = EventInstrumentLibrary.CopyInto(
                            owner,
                            snapshot,
                            requestedName: null,
                            folderId: null);
                        RemoveLaterExactTimelineCollisions(copy);
                        trackCopies = trackSnapshots.Select(trackSnapshot =>
                        {
                            LogicalTrack track = new(owner)
                            {
                                Name = trackSnapshot.Name,
                                EventInstrumentId = copy.Id,
                                LastBoundEventInstrumentName = copy.Name,
                                ColorOverride = trackSnapshot.ColorOverride
                            };
                            foreach (SegmentClipboardSnapshot segment in trackSnapshot.Segments)
                            {
                                InsertSegmentByTime(
                                    track.Segments,
                                    CreateSegmentFromClipboard(owner, segment, segment.StartOffset));
                            }
                            return track;
                        }).ToArray();
                    }
                    else
                    {
                        EnsureEventInstrumentIdAvailable(owner, copy.Id);
                        owner.EventInstruments.Add(copy);
                    }
                    foreach (LogicalTrack track in trackCopies ?? [])
                    {
                        EnsureLogicalTrackIdAvailable(owner, track.Id);
                        owner.Tracks.Add(track);
                        copy.LogicalTrackIds.Add(track.Id);
                    }
                    InsertAt(
                        owner.ArrangementParents,
                        index,
                        new ArrangementParentReference(ArrangementParentKind.EventInstrument, copy.Id),
                        "pasted Event Instrument parent");
                },
                owner =>
                {
                    EventInstrument value = copy ?? throw new InvalidOperationException(
                        "The pasted Event Instrument does not exist before Apply.");
                    RemoveRequired(
                        owner.ArrangementParents,
                        new ArrangementParentReference(ArrangementParentKind.EventInstrument, value.Id),
                        "pasted Event Instrument parent");
                    foreach (LogicalTrack track in trackCopies ?? [])
                    {
                        RemoveRequired(value.LogicalTrackIds, track.Id, "pasted Logical Track reference");
                        RemoveRequired(owner.Tracks, track, "pasted Logical Track");
                    }
                    RemoveRequired(owner.EventInstruments, value, "pasted Event Instrument");
                });
        });
}
