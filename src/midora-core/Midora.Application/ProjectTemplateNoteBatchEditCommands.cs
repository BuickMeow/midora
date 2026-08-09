using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand MoveTemplateNotes(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> noteIds,
        long tickDelta,
        int pitchDelta) =>
        PrepareTemplateNoteBatch(
            "Move template notes",
            eventInstrumentId,
            subVoiceId,
            noteIds,
            value => value with
            {
                Tick = checked(value.Tick + tickDelta),
                Number = checked(value.Number + pitchDelta)
            });

    public static IProjectEditCommand AdjustTemplateNoteEdges(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> noteIds,
        long startDelta,
        long endDelta) =>
        PrepareTemplateNoteBatch(
            "Resize template notes",
            eventInstrumentId,
            subVoiceId,
            noteIds,
            value => value with
            {
                Tick = checked(value.Tick + startDelta),
                LengthTicks = checked(value.LengthTicks + endDelta - startDelta)
            });

    private static IProjectEditCommand PrepareTemplateNoteBatch(
        string commandName,
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> noteIds,
        Func<TemplateEventValue, TemplateEventValue> update) =>
        Command(commandName, project =>
        {
            ArgumentNullException.ThrowIfNull(noteIds);
            ArgumentNullException.ThrowIfNull(update);
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            HashSet<MidoraId> requested = ValidateBatchIds(noteIds, nameof(noteIds), "Template Note");
            TemplateEvent[] notes = voice.Events.Where(item => requested.Contains(item.Id)).ToArray();
            if (notes.Length != requested.Count || notes.Any(item => item.Kind != TemplateEventKind.Note))
            {
                throw new ArgumentException(
                    "Every selected ID must identify a Template Note in the target SubVoice.",
                    nameof(noteIds));
            }
            TemplateEventValue[] old = notes.Select(CaptureTemplateEvent).ToArray();
            TemplateEventValue[] replacement = old.Select(update).ToArray();
            for (int index = 0; index < notes.Length; index++)
            {
                ValidateTemplateEventEdit(notes[index], replacement[index]);
            }
            long oldTemplateLength = instrument.TemplateLengthTicks;
            long requiredBoundary = replacement.Max(value => checked(value.Tick + value.LengthTicks));
            long replacementTemplateLength = Math.Max(oldTemplateLength, requiredBoundary);
            return Prepared(
                old.Where((value, index) => value != replacement[index]).Any()
                    || oldTemplateLength != replacementTemplateLength,
                EventInstrumentChange(eventInstrumentId),
                _ =>
                {
                    for (int index = 0; index < notes.Length; index++) SetTemplateEvent(notes[index], replacement[index]);
                    instrument.TemplateLengthTicks = replacementTemplateLength;
                },
                _ =>
                {
                    for (int index = 0; index < notes.Length; index++) SetTemplateEvent(notes[index], old[index]);
                    instrument.TemplateLengthTicks = oldTemplateLength;
                });
        });
}
