using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand DuplicateTemplateNotes(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> noteIds,
        long newEarliestTick,
        int pitchDelta) =>
        Command("Duplicate template notes", project =>
        {
            if (newEarliestTick < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(newEarliestTick));
            }
            ArgumentNullException.ThrowIfNull(noteIds);
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            HashSet<MidoraId> requested = ValidateBatchIds(noteIds, nameof(noteIds), "Template Note");
            TemplateEvent[] notes = voice.Events
                .Where(item => requested.Contains(item.Id))
                .ToArray();
            if (notes.Length != requested.Count || notes.Any(item => item.Kind != TemplateEventKind.Note))
            {
                throw new ArgumentException(
                    "Every selected ID must identify a Template Note in the target SubVoice.",
                    nameof(noteIds));
            }

            long earliest = notes.Min(item => item.Tick);
            long tickDelta = checked(newEarliestTick - earliest);
            TemplateEventValue[] replacements = notes
                .Select(item => CaptureTemplateEvent(item) with
                {
                    Tick = checked(item.Tick + tickDelta),
                    Number = checked(item.Number + pitchDelta)
                })
                .ToArray();
            for (int index = 0; index < notes.Length; index++)
            {
                ValidateTemplateEventEdit(notes[index], replacements[index]);
            }

            long oldTemplateLength = instrument.TemplateLengthTicks;
            long requiredBoundary = replacements.Max(value => checked(value.Tick + value.LengthTicks));
            long replacementTemplateLength = Math.Max(oldTemplateLength, requiredBoundary);
            int insertionIndex = voice.Events.Count;
            TemplateEvent[]? copies = null;
            return Prepared(
                hasChanges: true,
                EventInstrumentChange(eventInstrumentId),
                owner =>
                {
                    if (copies is null)
                    {
                        copies = new TemplateEvent[notes.Length];
                        for (int index = 0; index < notes.Length; index++)
                        {
                            TemplateEvent copy = new(owner);
                            SetTemplateEvent(copy, replacements[index]);
                            CopyMappingChain(owner, notes[index].NumberMappings, copy.NumberMappings);
                            CopyMappingChain(owner, notes[index].ValueMappings, copy.ValueMappings);
                            CopyMappingChain(owner, notes[index].SecondaryValueMappings, copy.SecondaryValueMappings);
                            SetTargetSettings(
                                copy.NumberTargetSettings,
                                new(notes[index].NumberTargetSettings.Rounding, notes[index].NumberTargetSettings.Overflow));
                            SetTargetSettings(
                                copy.ValueTargetSettings,
                                new(notes[index].ValueTargetSettings.Rounding, notes[index].ValueTargetSettings.Overflow));
                            SetTargetSettings(
                                copy.SecondaryValueTargetSettings,
                                new(
                                    notes[index].SecondaryValueTargetSettings.Rounding,
                                    notes[index].SecondaryValueTargetSettings.Overflow));
                            copies[index] = copy;
                        }
                    }
                    for (int index = 0; index < copies.Length; index++)
                    {
                        InsertAt(
                            voice.Events,
                            insertionIndex + index,
                            copies[index],
                            "Template Note copy");
                    }
                    instrument.TemplateLengthTicks = replacementTemplateLength;
                },
                _ =>
                {
                    if (copies is null)
                    {
                        throw new InvalidOperationException(
                            "Template Note copies do not exist before the first Apply.");
                    }
                    foreach (TemplateEvent copy in copies)
                    {
                        RemoveRequired(voice.Events, copy, "Template Note copy");
                    }
                    instrument.TemplateLengthTicks = oldTemplateLength;
                });
        });

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
