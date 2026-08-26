using Midora.Domain;

namespace Midora.Application;

public sealed record TemplateEventPointEdit(long Tick, int Value);

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand UpsertTemplateEventPoints(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidiValueTarget target,
        IReadOnlyCollection<TemplateEventPointEdit> points) =>
        Command("Draw template event points", project =>
        {
            ArgumentNullException.ThrowIfNull(points);
            if (points.Count == 0)
            {
                throw new ArgumentException("At least one Template Event point is required.", nameof(points));
            }
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            TemplateEventPointEdit[] edits = points
                .OrderBy(value => value.Tick)
                .ToArray();
            if (edits.Select(value => value.Tick).Distinct().Count() != edits.Length)
            {
                throw new ArgumentException("Template Event point ticks must be unique.", nameof(points));
            }
            foreach (TemplateEventPointEdit edit in edits)
            {
                ValidateTemplateEventPoint(target, edit);
            }

            List<ExistingTemplateEventPointEdit> existing = [];
            List<TemplateEventPointEdit> additions = [];
            foreach (TemplateEventPointEdit edit in edits)
            {
                TemplateEvent? current = FindTemplateEventForTarget(voice, target, edit.Tick);
                if (current is null)
                {
                    additions.Add(edit);
                }
                else
                {
                    existing.Add(new(current, CaptureTemplateEvent(current), edit.Value));
                }
            }

            long oldTemplateLength = instrument.TemplateLengthTicks;
            long requiredBoundary = edits.Max(value => checked(value.Tick + 1));
            long replacementTemplateLength = Math.Max(oldTemplateLength, requiredBoundary);
            TemplateEvent[]? created = null;
            bool existingChanges = existing.Any(value =>
                !TemplateEventMidiTargets.Enumerate(value.Event).Contains(target)
                || TemplateEventMidiTargets.GetValue(value.Event, target) != value.Value);
            return ResolveExactSubVoiceEventCollisions(Prepared(
                existingChanges || additions.Count != 0 || oldTemplateLength != replacementTemplateLength,
                EventInstrumentChange(eventInstrumentId),
                owner =>
                {
                    foreach (ExistingTemplateEventPointEdit edit in existing)
                    {
                        SetTemplateEventTarget(edit.Event, target, edit.Value);
                    }
                    if (created is null)
                    {
                        created = additions
                            .Select(value => CreateTemplateEventPoint(owner, target, value))
                            .ToArray();
                    }
                    foreach (TemplateEvent value in created)
                    {
                        if (voice.Events.Any(candidate => candidate.Id == value.Id))
                        {
                            throw new InvalidOperationException("A pasted Template Event point stable ID is already present.");
                        }
                        voice.Events.Add(value);
                    }
                    instrument.TemplateLengthTicks = replacementTemplateLength;
                },
                _ =>
                {
                    if (created is null)
                    {
                        throw new InvalidOperationException("Template Event points do not exist before the first Apply.");
                    }
                    int removed = voice.Events.RemoveRange(created);
                    if (removed != created.Length)
                        throw new InvalidOperationException("The drawn Template Event point set is no longer present.");
                    foreach (ExistingTemplateEventPointEdit edit in existing)
                    {
                        SetTemplateEvent(edit.Event, edit.OldValue);
                    }
                    instrument.TemplateLengthTicks = oldTemplateLength;
                }), voice);
        });

    private static TemplateEvent? FindTemplateEventForTarget(
        SubVoice voice,
        MidiValueTarget target,
        long tick)
    {
        IEnumerable<TemplateEvent> candidates = voice.Events.Where(value =>
            value.Kind != TemplateEventKind.Note && value.Tick == tick);
        return target.Kind switch
        {
            MidiValueKind.BankMsb or MidiValueKind.BankLsb =>
                candidates.SingleOrDefault(value => value.Kind == TemplateEventKind.Bank),
            MidiValueKind.PitchBendRangeSemitones or MidiValueKind.PitchBendRangeCents =>
                candidates.SingleOrDefault(value => value.Kind == TemplateEventKind.PitchBendRange),
            _ => candidates.SingleOrDefault(value =>
                TemplateEventMidiTargets.Enumerate(value).Contains(target))
        };
    }

    private static void ValidateTemplateEventPoint(MidiValueTarget target, TemplateEventPointEdit point)
    {
        if (point.Tick < 0 || point.Tick == long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(point.Tick));
        }
        bool valid = target.Kind switch
        {
            MidiValueKind.ControlChange => target.Number is >= 0 and <= 119 and not 91 and not 93
                && point.Value is >= 0 and <= 127,
            MidiValueKind.BankMsb or MidiValueKind.BankLsb or MidiValueKind.Program
                or MidiValueKind.PitchBendRangeSemitones => point.Value is >= 0 and <= 127,
            MidiValueKind.PitchBend => point.Value is >= -8192 and <= 8191,
            MidiValueKind.RegisteredParameter or MidiValueKind.NonRegisteredParameter =>
                target.Number is >= 0 and <= 16_383 && point.Value is >= 0 and <= 16_383,
            MidiValueKind.PitchBendRangeCents => point.Value is >= 0 and <= 99,
            _ => false
        };
        if (!valid)
        {
            throw new ArgumentOutOfRangeException(nameof(point));
        }
    }

    private static TemplateEvent CreateTemplateEventPoint(
        MidoraProject project,
        MidiValueTarget target,
        TemplateEventPointEdit point)
    {
        TemplateEvent result = new(project)
        {
            Tick = point.Tick,
            HasBankMsb = false,
            HasBankLsb = false
        };
        SetTemplateEventTarget(result, target, point.Value);
        return result;
    }

    private static void SetTemplateEventTarget(
        TemplateEvent targetEvent,
        MidiValueTarget target,
        int value)
    {
        switch (target.Kind)
        {
            case MidiValueKind.ControlChange:
                targetEvent.Kind = TemplateEventKind.ControlChange;
                targetEvent.Number = target.Number;
                targetEvent.Value = value;
                break;
            case MidiValueKind.BankMsb:
                targetEvent.Kind = TemplateEventKind.Bank;
                targetEvent.HasBankMsb = true;
                targetEvent.Value = value;
                break;
            case MidiValueKind.BankLsb:
                targetEvent.Kind = TemplateEventKind.Bank;
                targetEvent.HasBankLsb = true;
                targetEvent.SecondaryValue = value;
                break;
            case MidiValueKind.Program:
                targetEvent.Kind = TemplateEventKind.Program;
                targetEvent.Value = value;
                break;
            case MidiValueKind.PitchBend:
                targetEvent.Kind = TemplateEventKind.PitchBend;
                targetEvent.Value = value;
                break;
            case MidiValueKind.RegisteredParameter:
                targetEvent.Kind = TemplateEventKind.RegisteredParameter;
                targetEvent.Number = target.Number;
                targetEvent.Value = value;
                break;
            case MidiValueKind.NonRegisteredParameter:
                targetEvent.Kind = TemplateEventKind.NonRegisteredParameter;
                targetEvent.Number = target.Number;
                targetEvent.Value = value;
                break;
            case MidiValueKind.PitchBendRangeSemitones:
                targetEvent.Kind = TemplateEventKind.PitchBendRange;
                targetEvent.Value = value;
                break;
            case MidiValueKind.PitchBendRangeCents:
                targetEvent.Kind = TemplateEventKind.PitchBendRange;
                targetEvent.SecondaryValue = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target));
        }
        targetEvent.EnsureMappings();
    }

    private sealed record ExistingTemplateEventPointEdit(
        TemplateEvent Event,
        TemplateEventValue OldValue,
        int Value);
}
