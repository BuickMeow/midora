using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand UpdateTemplateEventNumberTargetSettings(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId templateEventId,
        MappingRounding rounding,
        MappingOverflow overflow) =>
        UpdateTemplateEventTargetSettings(
            "Change template event number target settings",
            eventInstrumentId,
            subVoiceId,
            templateEventId,
            TemplateEventTargetSlot.Number,
            rounding,
            overflow);

    public static IProjectEditCommand UpdateTemplateEventValueTargetSettings(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId templateEventId,
        MappingRounding rounding,
        MappingOverflow overflow) =>
        UpdateTemplateEventTargetSettings(
            "Change template event value target settings",
            eventInstrumentId,
            subVoiceId,
            templateEventId,
            TemplateEventTargetSlot.Value,
            rounding,
            overflow);

    public static IProjectEditCommand UpdateTemplateEventSecondaryValueTargetSettings(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId templateEventId,
        MappingRounding rounding,
        MappingOverflow overflow) =>
        UpdateTemplateEventTargetSettings(
            "Change template event secondary value target settings",
            eventInstrumentId,
            subVoiceId,
            templateEventId,
            TemplateEventTargetSlot.SecondaryValue,
            rounding,
            overflow);

    private static IProjectEditCommand UpdateTemplateEventTargetSettings(
        string commandName,
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId templateEventId,
        TemplateEventTargetSlot slot,
        MappingRounding rounding,
        MappingOverflow overflow) =>
        Command(commandName, project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            TemplateEvent templateEvent = FindTemplateEvent(voice, templateEventId);
            ValidateTemplateEventTargetSlot(templateEvent, slot);
            ValidateIntegerTargetSettings(rounding, overflow);
            if (slot == TemplateEventTargetSlot.Number
                && overflow != MappingOverflow.Fail)
            {
                throw new ArgumentException(
                    "Note number cannot use the Clamp final overflow policy.",
                    nameof(overflow));
            }
            MidiIntegerTargetSettings settings = slot switch
            {
                TemplateEventTargetSlot.Number => templateEvent.NumberTargetSettings,
                TemplateEventTargetSlot.Value => templateEvent.ValueTargetSettings,
                TemplateEventTargetSlot.SecondaryValue => templateEvent.SecondaryValueTargetSettings,
                _ => throw new ArgumentOutOfRangeException(nameof(slot))
            };
            IntegerTargetSettingsValue old = new(settings.Rounding, settings.Overflow);
            IntegerTargetSettingsValue replacement = new(rounding, overflow);
            return Prepared(
                old != replacement,
                EventInstrumentChange(eventInstrumentId),
                _ => SetTargetSettings(settings, replacement),
                _ => SetTargetSettings(settings, old));
        });

    private static void ValidateTemplateEventTargetSlot(
        TemplateEvent templateEvent,
        TemplateEventTargetSlot slot)
    {
        if (!Enum.IsDefined(templateEvent.Kind))
        {
            throw new InvalidOperationException(
                "The Template Event kind must be repaired before editing target settings.");
        }
        bool supported = slot switch
        {
            TemplateEventTargetSlot.Number => templateEvent.Kind == TemplateEventKind.Note,
            TemplateEventTargetSlot.Value => templateEvent.Kind != TemplateEventKind.Bank
                || templateEvent.HasBankMsb,
            TemplateEventTargetSlot.SecondaryValue =>
                templateEvent.Kind == TemplateEventKind.PitchBendRange
                || templateEvent.Kind == TemplateEventKind.Bank && templateEvent.HasBankLsb,
            _ => false
        };
        if (!supported)
        {
            throw new InvalidOperationException(
                "The Template Event does not expose the selected mappable value target.");
        }
    }

    private static void ValidateIntegerTargetSettings(
        MappingRounding rounding,
        MappingOverflow overflow)
    {
        if (!Enum.IsDefined(rounding))
        {
            throw new ArgumentOutOfRangeException(nameof(rounding));
        }
        if (!Enum.IsDefined(overflow))
        {
            throw new ArgumentOutOfRangeException(nameof(overflow));
        }
    }

    private enum TemplateEventTargetSlot
    {
        Number,
        Value,
        SecondaryValue
    }
}
