using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand UpdateMappingChainEnabled(
        MidoraId eventInstrumentId,
        MidoraId mappingChainId,
        bool isEnabled) =>
        Command("Change mapping chain enabled state", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            MappingChain chain = FindMappingChain(instrument, mappingChainId);
            bool oldValue = chain.IsEnabled;
            return Prepared(
                oldValue != isEnabled,
                EventInstrumentChange(eventInstrumentId),
                _ => chain.IsEnabled = isEnabled,
                _ => chain.IsEnabled = oldValue);
        });

    public static IProjectEditCommand UpdateMappingStepEnabled(
        MidoraId eventInstrumentId,
        MidoraId mappingChainId,
        MidoraId mappingStepId,
        bool isEnabled) =>
        Command("Change mapping step enabled state", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            MappingChain chain = FindMappingChain(instrument, mappingChainId);
            ValueMappingStep step = FindMappingStep(chain, mappingStepId);
            bool oldValue = step.IsEnabled;
            return Prepared(
                oldValue != isEnabled,
                EventInstrumentChange(eventInstrumentId),
                _ => step.IsEnabled = isEnabled,
                _ => step.IsEnabled = oldValue);
        });

    public static IProjectEditCommand UpdateMappingStep(
        MidoraId eventInstrumentId,
        MidoraId mappingChainId,
        MidoraId mappingStepId,
        MappingSource source,
        MappingOperation operation,
        MidoraId? logicalParameterId,
        MidoraId? envelopeId,
        MidoraId? mappingFunctionId,
        double constant,
        double sourceMinimum,
        double sourceMaximum,
        double targetMinimum,
        double targetMaximum,
        MappingInputOverflow inputOverflow,
        DivideByZeroPolicy divideByZero) =>
        Command("Change mapping step", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            MappingChain chain = FindMappingChain(instrument, mappingChainId);
            ValueMappingStep step = FindMappingStep(chain, mappingStepId);
            MappingStepValue old = CaptureMappingStep(step);
            MappingStepValue replacement = new(
                source,
                operation,
                logicalParameterId,
                envelopeId,
                mappingFunctionId,
                constant,
                sourceMinimum,
                sourceMaximum,
                targetMinimum,
                targetMaximum,
                inputOverflow,
                divideByZero);
            ValidateMappingStepValue(replacement);
            return Prepared(
                old != replacement,
                EventInstrumentChange(eventInstrumentId),
                _ => SetMappingStep(step, replacement),
                _ => SetMappingStep(step, old));
        });

    public static IProjectEditCommand ReorderMappingStep(
        MidoraId eventInstrumentId,
        MidoraId mappingChainId,
        MidoraId mappingStepId,
        int newIndex) =>
        Command("Reorder mapping step", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            MappingChain chain = FindMappingChain(instrument, mappingChainId);
            ValueMappingStep step = FindMappingStep(chain, mappingStepId);
            int oldIndex = chain.IndexOf(step);
            ValidateExistingIndex(newIndex, chain.Count, nameof(newIndex));
            return Prepared(
                oldIndex != newIndex,
                EventInstrumentChange(eventInstrumentId),
                _ => MoveMappingStep(chain, step, newIndex),
                _ => MoveMappingStep(chain, step, oldIndex));
        });

    public static IProjectEditCommand DeleteMappingStep(
        MidoraId eventInstrumentId,
        MidoraId mappingChainId,
        MidoraId mappingStepId) =>
        Command("Delete mapping step", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            MappingChain chain = FindMappingChain(instrument, mappingChainId);
            ValueMappingStep step = FindMappingStep(chain, mappingStepId);
            int originalIndex = chain.IndexOf(step);
            return Prepared(
                hasChanges: true,
                EventInstrumentChange(eventInstrumentId),
                _ => RemoveMappingStepRequired(chain, step),
                _ => InsertMappingStepAt(chain, originalIndex, step));
        });

    public static IProjectEditCommand DeleteMappingChain(
        MidoraId eventInstrumentId,
        MidoraId mappingChainId,
        bool nonEmptyDeletionConfirmed) =>
        Command("Delete mapping chain", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            MappingChain chain = FindMappingChain(instrument, mappingChainId);
            if (chain.Count != 0 && !nonEmptyDeletionConfirmed)
            {
                throw new InvalidOperationException(
                    "Deleting a non-empty Mapping Chain requires explicit confirmation.");
            }
            bool oldIsEnabled = chain.IsEnabled;
            ValueMappingStep[] oldSteps = chain.ToArray();
            return Prepared(
                oldSteps.Length != 0 || !oldIsEnabled,
                EventInstrumentChange(eventInstrumentId),
                _ => ClearMappingChainRequired(chain, oldIsEnabled, oldSteps),
                _ => RestoreMappingChain(chain, oldIsEnabled, oldSteps));
        });

    private static MappingChain FindMappingChain(
        EventInstrument instrument,
        MidoraId mappingChainId)
    {
        MappingChain[] matches = EnumerateMappingChains(instrument)
            .Where(value => value.Id == mappingChainId)
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ArgumentOutOfRangeException(nameof(mappingChainId)),
            _ => throw new InvalidOperationException(
                "The Mapping Chain stable ID is duplicated in this Event Instrument.")
        };
    }

    private static IEnumerable<MappingChain> EnumerateMappingChains(
        EventInstrument instrument) =>
        instrument.SubVoices
            .SelectMany(value => value.EventMappings)
            .Select(value => value.Steps)
            .Concat(instrument.ParameterMappings.Select(value => value.Steps));

    private static ValueMappingStep FindMappingStep(
        MappingChain chain,
        MidoraId mappingStepId) =>
        chain.SingleOrDefault(value => value.Id == mappingStepId)
        ?? throw new ArgumentOutOfRangeException(nameof(mappingStepId));

    private static MappingStepValue CaptureMappingStep(ValueMappingStep value) =>
        new(
            value.Source,
            value.Operation,
            value.LogicalParameterId,
            value.EnvelopeId,
            value.MappingFunctionId,
            value.Constant,
            value.SourceMinimum,
            value.SourceMaximum,
            value.TargetMinimum,
            value.TargetMaximum,
            value.InputOverflow,
            value.DivideByZero);

    private static void ValidateMappingStepValue(MappingStepValue value)
    {
        if (!Enum.IsDefined(value.Source))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Mapping Source is invalid.");
        }
        if (!Enum.IsDefined(value.Operation))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Mapping Operation is invalid.");
        }
        if (!Enum.IsDefined(value.InputOverflow))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Mapping input overflow policy is invalid.");
        }
        if (!Enum.IsDefined(value.DivideByZero))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Mapping divide-by-zero policy is invalid.");
        }
        if (!double.IsFinite(value.Constant)
            || !double.IsFinite(value.SourceMinimum)
            || !double.IsFinite(value.SourceMaximum)
            || !double.IsFinite(value.TargetMinimum)
            || !double.IsFinite(value.TargetMaximum))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Mapping Step numeric values must be finite.");
        }
        ValidateOptionalMappingReference(value.LogicalParameterId, "Logical Parameter");
        ValidateOptionalMappingReference(value.EnvelopeId, "Envelope Preset");
        ValidateOptionalMappingReference(value.MappingFunctionId, "Mapping Function");
    }

    private static void ValidateOptionalMappingReference(MidoraId? value, string referenceName)
    {
        if (value.HasValue && value.Value == default)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"A {referenceName} reference cannot use the empty stable ID.");
        }
    }

    private static void SetMappingStep(ValueMappingStep target, MappingStepValue value)
    {
        target.Source = value.Source;
        target.Operation = value.Operation;
        target.LogicalParameterId = value.LogicalParameterId;
        target.EnvelopeId = value.EnvelopeId;
        target.MappingFunctionId = value.MappingFunctionId;
        target.Constant = value.Constant;
        target.SourceMinimum = value.SourceMinimum;
        target.SourceMaximum = value.SourceMaximum;
        target.TargetMinimum = value.TargetMinimum;
        target.TargetMaximum = value.TargetMaximum;
        target.InputOverflow = value.InputOverflow;
        target.DivideByZero = value.DivideByZero;
    }

    private static void MoveMappingStep(
        MappingChain chain,
        ValueMappingStep step,
        int targetIndex)
    {
        int currentIndex = chain.IndexOf(step);
        if (currentIndex < 0)
        {
            throw new InvalidOperationException("The Mapping Step is no longer present.");
        }
        chain.RemoveAt(currentIndex);
        chain.Insert(targetIndex, step);
    }

    private static void RemoveMappingStepRequired(
        MappingChain chain,
        ValueMappingStep step)
    {
        if (!chain.Remove(step))
        {
            throw new InvalidOperationException("The Mapping Step is no longer present.");
        }
    }

    private static void InsertMappingStepAt(
        MappingChain chain,
        int index,
        ValueMappingStep step)
    {
        if ((uint)index > (uint)chain.Count)
        {
            throw new InvalidOperationException(
                "The original Mapping Step index can no longer be restored.");
        }
        if (chain.Any(value => value.Id == step.Id))
        {
            throw new InvalidOperationException(
                "The Mapping Step stable ID is already present in the chain.");
        }
        chain.Insert(index, step);
    }

    private static void ClearMappingChainRequired(
        MappingChain chain,
        bool expectedIsEnabled,
        IReadOnlyCollection<ValueMappingStep> expectedSteps)
    {
        if (chain.IsEnabled != expectedIsEnabled
            || chain.Count != expectedSteps.Count
            || !chain.SequenceEqual(expectedSteps))
        {
            throw new InvalidOperationException(
                "The Mapping Chain changed after the delete command was prepared.");
        }
        chain.Clear();
        chain.IsEnabled = true;
    }

    private static void RestoreMappingChain(
        MappingChain chain,
        bool oldIsEnabled,
        IEnumerable<ValueMappingStep> oldSteps)
    {
        if (!chain.IsEnabled || chain.Count != 0)
        {
            throw new InvalidOperationException(
                "The deleted Mapping Chain sentinel changed before Undo.");
        }
        chain.IsEnabled = oldIsEnabled;
        foreach (ValueMappingStep step in oldSteps)
        {
            chain.Add(step);
        }
    }

    private readonly record struct MappingStepValue(
        MappingSource Source,
        MappingOperation Operation,
        MidoraId? LogicalParameterId,
        MidoraId? EnvelopeId,
        MidoraId? MappingFunctionId,
        double Constant,
        double SourceMinimum,
        double SourceMaximum,
        double TargetMinimum,
        double TargetMaximum,
        MappingInputOverflow InputOverflow,
        DivideByZeroPolicy DivideByZero);
}
