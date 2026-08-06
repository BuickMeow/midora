using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand UpdateInstrumentEnvelope(
        MidoraId eventInstrumentId,
        MidoraId envelopeId,
        string? name,
        long delayTicks,
        long attackTicks,
        long holdTicks,
        long decayTicks,
        double startValue,
        double peakValue,
        double sustainValue,
        long releaseTicks,
        double endValue) =>
        Command("Change envelope preset", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            InstrumentEnvelope envelope = FindEnvelope(instrument, envelopeId);
            if (!instrument.RequiresChannelIsolation)
            {
                throw new InvalidOperationException(
                    "Envelope Presets cannot be edited while Per-Note Instance Isolation is disabled.");
            }
            string? normalizedName = name is null
                ? null
                : ProjectTextRules.NormalizeShortText(name, allowEmpty: true, nameof(name));
            EnvelopeValue replacement = new(
                normalizedName,
                delayTicks,
                attackTicks,
                holdTicks,
                decayTicks,
                startValue,
                peakValue,
                sustainValue,
                releaseTicks,
                endValue);
            ValidateEnvelopeValue(replacement);
            EnvelopeValue old = CaptureEnvelope(envelope);
            return Prepared(
                old != replacement,
                EventInstrumentChange(eventInstrumentId),
                _ => SetEnvelope(envelope, replacement),
                _ => SetEnvelope(envelope, old));
        });

    public static IProjectEditCommand DeleteInstrumentEnvelope(
        MidoraId eventInstrumentId,
        MidoraId envelopeId,
        bool referencedDeletionConfirmed) =>
        Command("Delete envelope preset", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            InstrumentEnvelope envelope = FindEnvelope(instrument, envelopeId);
            bool isReferenced = EnumerateMappingSteps(instrument)
                .Any(value => value.Source == MappingSource.Envelope
                    && value.EnvelopeId == envelopeId);
            if (isReferenced && !referencedDeletionConfirmed)
            {
                throw new InvalidOperationException(
                    "Deleting a referenced Envelope Preset requires explicit confirmation.");
            }
            int originalIndex = instrument.Envelopes.IndexOf(envelope);
            return Prepared(
                hasChanges: true,
                EventInstrumentChange(eventInstrumentId),
                _ => RemoveRequired(instrument.Envelopes, envelope, "Envelope Preset"),
                _ => InsertAt(
                    instrument.Envelopes,
                    originalIndex,
                    envelope,
                    "Envelope Preset"));
        });

    private static InstrumentEnvelope FindEnvelope(
        EventInstrument instrument,
        MidoraId envelopeId) =>
        instrument.Envelopes.SingleOrDefault(value => value.Id == envelopeId)
        ?? throw new ArgumentOutOfRangeException(nameof(envelopeId));

    private static IEnumerable<ValueMappingStep> EnumerateMappingSteps(
        EventInstrument instrument) =>
        instrument.SubVoices
            .SelectMany(value => value.Events)
            .SelectMany(value => value.NumberMappings
                .Concat(value.ValueMappings)
                .Concat(value.SecondaryValueMappings))
            .Concat(instrument.ParameterMappings.SelectMany(value => value.Steps));

    private static EnvelopeValue CaptureEnvelope(InstrumentEnvelope value) =>
        new(
            value.Name,
            value.DelayTicks,
            value.AttackTicks,
            value.HoldTicks,
            value.DecayTicks,
            value.StartValue,
            value.PeakValue,
            value.SustainValue,
            value.ReleaseTicks,
            value.EndValue);

    private static void ValidateEnvelopeValue(EnvelopeValue value)
    {
        if (value.DelayTicks < 0
            || value.AttackTicks < 0
            || value.HoldTicks < 0
            || value.DecayTicks < 0
            || value.ReleaseTicks < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Envelope stage durations must be non-negative.");
        }
        if (!IsUnitValue(value.StartValue)
            || !IsUnitValue(value.PeakValue)
            || !IsUnitValue(value.SustainValue)
            || !IsUnitValue(value.EndValue))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Envelope values must be finite and in the inclusive range 0 through 1.");
        }
    }

    private static bool IsUnitValue(double value) =>
        double.IsFinite(value) && value is >= 0 and <= 1;

    private static void SetEnvelope(InstrumentEnvelope target, EnvelopeValue value)
    {
        target.Name = value.Name;
        target.DelayTicks = value.DelayTicks;
        target.AttackTicks = value.AttackTicks;
        target.HoldTicks = value.HoldTicks;
        target.DecayTicks = value.DecayTicks;
        target.StartValue = value.StartValue;
        target.PeakValue = value.PeakValue;
        target.SustainValue = value.SustainValue;
        target.ReleaseTicks = value.ReleaseTicks;
        target.EndValue = value.EndValue;
    }

    private sealed record EnvelopeValue(
        string? Name,
        long DelayTicks,
        long AttackTicks,
        long HoldTicks,
        long DecayTicks,
        double StartValue,
        double PeakValue,
        double SustainValue,
        long ReleaseTicks,
        double EndValue);
}
