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
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.EventInstrument,
            1,
            $"Event Instrument: {source.Name}",
            new EventInstrumentClipboardData(snapshot, source.LibraryFolderId));
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
        MidoraId? folderId = targetFolderId
            ?? (data.SourceFolderId is MidoraId sourceFolderId
                && targetDocument.Project.EventInstrumentFolders.Any(value => value.Id == sourceFolderId)
                    ? sourceFolderId
                    : null);
        return ProjectDomainEditCommands.PasteEventInstrumentClipboard(
            data.Snapshot,
            folderId,
            insertionIndex);
    }
}

internal sealed record EventInstrumentClipboardData(
    EventInstrument Snapshot,
    MidoraId? SourceFolderId) : ProjectObjectClipboardData;

public static partial class ProjectDomainEditCommands
{
    internal static IProjectEditCommand PasteEventInstrumentClipboard(
        EventInstrument snapshot,
        MidoraId? folderId,
        int? insertionIndex) =>
        Command("Paste event instrument", project =>
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (folderId.HasValue)
            {
                _ = FindFolder(project, folderId.Value);
            }
            int index = insertionIndex ?? project.EventInstruments.Count;
            ValidateInsertionIndex(index, project.EventInstruments.Count, nameof(insertionIndex));
            EventInstrument? copy = null;
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
                            folderId);
                        RemoveLaterExactTimelineCollisions(copy);
                        Move(owner.EventInstruments, copy, index);
                        return;
                    }
                    EnsureEventInstrumentIdAvailable(owner, copy.Id);
                    InsertAt(owner.EventInstruments, index, copy, "pasted Event Instrument");
                },
                owner => RemoveRequired(
                    owner.EventInstruments,
                    copy ?? throw new InvalidOperationException(
                        "The pasted Event Instrument does not exist before Apply."),
                    "pasted Event Instrument"));
        });
}
