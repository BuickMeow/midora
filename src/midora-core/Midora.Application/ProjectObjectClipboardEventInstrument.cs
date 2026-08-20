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
            source.Name);
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.EventInstrument,
            1,
            $"Event Instrument: {source.Name}",
            new EventInstrumentClipboardData(snapshot));
    }

    public static IProjectEditCommand CreatePasteEventInstrumentCommand(
        ProjectDocumentSession targetDocument,
        ProjectObjectClipboardPayload payload,
        int? insertionIndex = null)
    {
        EventInstrumentClipboardData data = RequirePayload<EventInstrumentClipboardData>(
            targetDocument,
            payload,
            ProjectObjectClipboardKind.EventInstrument);
        return ProjectDomainEditCommands.PasteEventInstrumentClipboard(
            data.Snapshot,
            insertionIndex);
    }
}

internal sealed record EventInstrumentClipboardData(
    EventInstrument Snapshot) : ProjectObjectClipboardData;

public static partial class ProjectDomainEditCommands
{
    internal static IProjectEditCommand PasteEventInstrumentClipboard(
        EventInstrument snapshot,
        int? insertionIndex) =>
        Command("Paste event instrument", project =>
        {
            ArgumentNullException.ThrowIfNull(snapshot);
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
                            requestedName: null);
                        RemoveLaterExactTimelineCollisions(copy);
                        // CopyInto registers the new definition immediately. Paste owns the
                        // final insertion position, so remove that provisional append before
                        // inserting the same object at the requested index.
                        RemoveRequired(
                            owner.EventInstruments,
                            copy,
                            "provisionally appended Event Instrument copy");
                    }
                    else
                    {
                        EnsureEventInstrumentIdAvailable(owner, copy.Id);
                    }
                    InsertAt(
                        owner.EventInstruments,
                        index,
                        copy,
                        "pasted Event Instrument");
                },
                owner =>
                {
                    EventInstrument value = copy ?? throw new InvalidOperationException(
                        "The pasted Event Instrument does not exist before Apply.");
                    RemoveRequired(owner.EventInstruments, value, "pasted Event Instrument");
                });
        });
}
