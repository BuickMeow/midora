using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand UpdateTemplateNote(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId templateEventId,
        long tick,
        long lengthTicks,
        int note,
        int velocity,
        bool followPitchDelta) =>
        UpdateTemplateEvent(
            "Change template note",
            eventInstrumentId,
            subVoiceId,
            templateEventId,
            TemplateEventKind.Note,
            current => current with
            {
                Tick = tick,
                LengthTicks = lengthTicks,
                Number = note,
                Value = velocity,
                FollowPitchDelta = followPitchDelta
            });

    public static IProjectEditCommand UpdateTemplateControlChange(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId templateEventId,
        long tick,
        int controller,
        int value) =>
        UpdateTemplateEvent(
            "Change template control change",
            eventInstrumentId,
            subVoiceId,
            templateEventId,
            TemplateEventKind.ControlChange,
            current => current with { Tick = tick, Number = controller, Value = value });

    public static IProjectEditCommand UpdateTemplateBank(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId templateEventId,
        long tick,
        int? bankMsb,
        int? bankLsb) =>
        UpdateTemplateEvent(
            "Change template bank",
            eventInstrumentId,
            subVoiceId,
            templateEventId,
            TemplateEventKind.Bank,
            current => current with
            {
                Tick = tick,
                Value = bankMsb ?? 0,
                SecondaryValue = bankLsb ?? 0,
                HasBankMsb = bankMsb.HasValue,
                HasBankLsb = bankLsb.HasValue
            });

    public static IProjectEditCommand UpdateTemplateProgram(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId templateEventId,
        long tick,
        int program) =>
        UpdateTemplateEvent(
            "Change template program",
            eventInstrumentId,
            subVoiceId,
            templateEventId,
            TemplateEventKind.Program,
            current => current with { Tick = tick, Value = program });

    public static IProjectEditCommand UpdateTemplatePitchBend(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId templateEventId,
        long tick,
        int value) =>
        UpdateTemplateEvent(
            "Change template pitch bend",
            eventInstrumentId,
            subVoiceId,
            templateEventId,
            TemplateEventKind.PitchBend,
            current => current with { Tick = tick, Value = value });

    public static IProjectEditCommand UpdateTemplateRegisteredParameter(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId templateEventId,
        long tick,
        int parameter,
        int value) =>
        UpdateTemplateParameter(
            "Change template RPN",
            eventInstrumentId,
            subVoiceId,
            templateEventId,
            TemplateEventKind.RegisteredParameter,
            tick,
            parameter,
            value);

    public static IProjectEditCommand UpdateTemplateNonRegisteredParameter(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId templateEventId,
        long tick,
        int parameter,
        int value) =>
        UpdateTemplateParameter(
            "Change template NRPN",
            eventInstrumentId,
            subVoiceId,
            templateEventId,
            TemplateEventKind.NonRegisteredParameter,
            tick,
            parameter,
            value);

    public static IProjectEditCommand UpdateTemplatePitchBendRange(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId templateEventId,
        long tick,
        int semitones,
        int cents) =>
        UpdateTemplateEvent(
            "Change template pitch bend range",
            eventInstrumentId,
            subVoiceId,
            templateEventId,
            TemplateEventKind.PitchBendRange,
            current => current with
            {
                Tick = tick,
                Value = semitones,
                SecondaryValue = cents
            });

    public static IProjectEditCommand DeleteTemplateEvent(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId templateEventId) =>
        Command("Delete template event", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            TemplateEvent templateEvent = FindTemplateEvent(voice, templateEventId);
            int originalIndex = voice.Events.IndexOf(templateEvent);
            return Prepared(
                hasChanges: true,
                EventInstrumentChange(eventInstrumentId),
                _ => RemoveRequired(voice.Events, templateEvent, "Template Event"),
                _ => InsertAt(voice.Events, originalIndex, templateEvent, "Template Event"));
        });

    private static IProjectEditCommand UpdateTemplateParameter(
        string commandName,
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId templateEventId,
        TemplateEventKind expectedKind,
        long tick,
        int parameter,
        int value) =>
        UpdateTemplateEvent(
            commandName,
            eventInstrumentId,
            subVoiceId,
            templateEventId,
            expectedKind,
            current => current with { Tick = tick, Number = parameter, Value = value });

    private static IProjectEditCommand UpdateTemplateEvent(
        string commandName,
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId templateEventId,
        TemplateEventKind expectedKind,
        Func<TemplateEventValue, TemplateEventValue> update) =>
        Command(commandName, project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            TemplateEvent templateEvent = FindTemplateEvent(voice, templateEventId);
            if (templateEvent.Kind != expectedKind)
            {
                throw new InvalidOperationException(
                    $"The Template Event is not a {expectedKind} event.");
            }
            TemplateEventValue old = CaptureTemplateEvent(templateEvent);
            TemplateEventValue replacement = update(old);
            ValidateTemplateEventEdit(templateEvent, replacement);
            long requiredBoundary = replacement.Kind == TemplateEventKind.Note
                ? checked(replacement.Tick + replacement.LengthTicks)
                : checked(replacement.Tick + 1);
            long oldTemplateLength = instrument.TemplateLengthTicks;
            long replacementTemplateLength = Math.Max(oldTemplateLength, requiredBoundary);
            IndexedTemplateEvent[] conflicts = voice.Events
                .Select((value, index) => new IndexedTemplateEvent(value, index))
                .Where(value => !ReferenceEquals(value.Event, templateEvent)
                    && TemplateEventsConflict(value.Event, replacement))
                .ToArray();
            if (conflicts.Select(value => value.Event.Id).Distinct().Count() != conflicts.Length)
            {
                throw new InvalidOperationException(
                    "Conflicting Template Event stable IDs must be unique.");
            }
            return Prepared(
                old != replacement
                    || oldTemplateLength != replacementTemplateLength
                    || conflicts.Length != 0,
                EventInstrumentChange(eventInstrumentId),
                _ =>
                {
                    RequireContains(voice.Events, templateEvent, "Template Event");
                    foreach (IndexedTemplateEvent conflict in conflicts)
                    {
                        RequireContains(voice.Events, conflict.Event, "conflicting Template Event");
                    }
                    SetTemplateEvent(templateEvent, replacement);
                    for (int index = conflicts.Length - 1; index >= 0; index--)
                    {
                        RemoveRequired(
                            voice.Events,
                            conflicts[index].Event,
                            "conflicting Template Event");
                    }
                    instrument.TemplateLengthTicks = replacementTemplateLength;
                },
                _ =>
                {
                    if (conflicts.Any(conflict => voice.Events.Any(
                        value => value.Id == conflict.Event.Id)))
                    {
                        throw new InvalidOperationException(
                            "A replaced Template Event stable ID is already present.");
                    }
                    int restoredCount = 0;
                    foreach (IndexedTemplateEvent conflict in conflicts)
                    {
                        if ((uint)conflict.Index > (uint)(voice.Events.Count + restoredCount))
                        {
                            throw new InvalidOperationException(
                                "A replaced Template Event index can no longer be restored.");
                        }
                        restoredCount++;
                    }
                    RequireContains(voice.Events, templateEvent, "Template Event");
                    SetTemplateEvent(templateEvent, old);
                    instrument.TemplateLengthTicks = oldTemplateLength;
                    foreach (IndexedTemplateEvent conflict in conflicts)
                    {
                        InsertAt(
                            voice.Events,
                            conflict.Index,
                            conflict.Event,
                            "conflicting Template Event");
                    }
                });
        });

    private static TemplateEvent FindTemplateEvent(SubVoice voice, MidoraId templateEventId) =>
        voice.Events.SingleOrDefault(value => value.Id == templateEventId)
        ?? throw new ArgumentOutOfRangeException(nameof(templateEventId));

    private static void ValidateTemplateEventEdit(
        TemplateEvent templateEvent,
        TemplateEventValue value)
    {
        if (!Enum.IsDefined(value.Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(value.Kind));
        }
        if (value.Tick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value.Tick));
        }
        switch (value.Kind)
        {
            case TemplateEventKind.Note:
                if (value.LengthTicks <= 0
                    || value.Tick > long.MaxValue - value.LengthTicks)
                {
                    throw new ArgumentOutOfRangeException(nameof(value.LengthTicks));
                }
                if (value.Number is < 0 or > 127)
                {
                    throw new ArgumentOutOfRangeException(nameof(value.Number));
                }
                if (value.Value is < 1 or > 127)
                {
                    throw new ArgumentOutOfRangeException(nameof(value.Value));
                }
                break;
            case TemplateEventKind.ControlChange:
                if (value.Number is < 0 or > 119 or 91 or 93)
                {
                    throw new ArgumentOutOfRangeException(nameof(value.Number));
                }
                ValidateSevenBit(value.Value, nameof(value.Value));
                break;
            case TemplateEventKind.Bank:
                if (!value.HasBankMsb && !value.HasBankLsb)
                {
                    throw new ArgumentException("Bank must contain an MSB, an LSB, or both.");
                }
                if (value.HasBankMsb)
                {
                    ValidateSevenBit(value.Value, nameof(value.Value));
                }
                if (value.HasBankLsb)
                {
                    ValidateSevenBit(value.SecondaryValue, nameof(value.SecondaryValue));
                }
                if (!value.HasBankMsb && HasActiveSteps(templateEvent.ValueMappings)
                    || !value.HasBankLsb && HasActiveSteps(templateEvent.SecondaryValueMappings))
                {
                    throw new InvalidOperationException(
                        "A mapped Bank component cannot be removed while its Mapping Chain is active.");
                }
                break;
            case TemplateEventKind.Program:
                ValidateSevenBit(value.Value, nameof(value.Value));
                break;
            case TemplateEventKind.PitchBend:
                if (value.Value is < -8192 or > 8191)
                {
                    throw new ArgumentOutOfRangeException(nameof(value.Value));
                }
                break;
            case TemplateEventKind.RegisteredParameter:
            case TemplateEventKind.NonRegisteredParameter:
                ValidateFourteenBit(value.Number, nameof(value.Number));
                ValidateFourteenBit(value.Value, nameof(value.Value));
                break;
            case TemplateEventKind.PitchBendRange:
                ValidateSevenBit(value.Value, nameof(value.Value));
                if (value.SecondaryValue is < 0 or > 99)
                {
                    throw new ArgumentOutOfRangeException(nameof(value.SecondaryValue));
                }
                break;
        }
        if (value.Kind != TemplateEventKind.Note && value.Tick == long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value.Tick),
                "An instantaneous Template Event requires a representable exclusive end boundary.");
        }
    }

    private static void ValidateSevenBit(int value, string parameterName)
    {
        if (value is < 0 or > 127)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateFourteenBit(int value, string parameterName)
    {
        if (value is < 0 or > 16_383)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static bool HasActiveSteps(MappingChain chain) =>
        chain.IsEnabled && chain.Any(value => value.IsEnabled);

    private static bool TemplateEventsConflict(
        TemplateEvent candidate,
        TemplateEventValue replacement)
    {
        if (candidate.Tick != replacement.Tick || candidate.Kind == TemplateEventKind.Note)
        {
            return false;
        }
        if (replacement.Kind == TemplateEventKind.Note)
        {
            return false;
        }
        bool candidatePitchBendRange = candidate.Kind == TemplateEventKind.PitchBendRange
            || candidate.Kind == TemplateEventKind.RegisteredParameter && candidate.Number == 0;
        bool replacementPitchBendRange = replacement.Kind == TemplateEventKind.PitchBendRange
            || replacement.Kind == TemplateEventKind.RegisteredParameter && replacement.Number == 0;
        if (candidatePitchBendRange && replacementPitchBendRange)
        {
            return true;
        }
        if (candidate.Kind != replacement.Kind)
        {
            return false;
        }
        return replacement.Kind switch
        {
            TemplateEventKind.ControlChange => candidate.Number == replacement.Number,
            TemplateEventKind.RegisteredParameter or TemplateEventKind.NonRegisteredParameter =>
                candidate.Number == replacement.Number,
            _ => true
        };
    }

    private static TemplateEventValue CaptureTemplateEvent(TemplateEvent value) =>
        new(
            value.Kind,
            value.Tick,
            value.LengthTicks,
            value.Number,
            value.Value,
            value.SecondaryValue,
            value.HasBankMsb,
            value.HasBankLsb,
            value.FollowPitchDelta);

    private static void SetTemplateEvent(TemplateEvent target, TemplateEventValue value)
    {
        target.Kind = value.Kind;
        target.Tick = value.Tick;
        target.LengthTicks = value.LengthTicks;
        target.Number = value.Number;
        target.Value = value.Value;
        target.SecondaryValue = value.SecondaryValue;
        target.HasBankMsb = value.HasBankMsb;
        target.HasBankLsb = value.HasBankLsb;
        target.FollowPitchDelta = value.FollowPitchDelta;
    }

    private sealed record TemplateEventValue(
        TemplateEventKind Kind,
        long Tick,
        long LengthTicks,
        int Number,
        int Value,
        int SecondaryValue,
        bool HasBankMsb,
        bool HasBankLsb,
        bool FollowPitchDelta);

    private readonly record struct IndexedTemplateEvent(TemplateEvent Event, int Index);
}
