using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand UpdateEventInstrumentTemplateLength(
        MidoraId eventInstrumentId,
        long templateLengthTicks) =>
        Command("Change event instrument template length", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            if (templateLengthTicks <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(templateLengthTicks));
            }
            long minimum = GetMinimumTemplateLength(instrument);
            if (templateLengthTicks < minimum)
            {
                throw new InvalidOperationException(
                    $"Template Length cannot be shorter than the required content boundary {minimum}.");
            }
            long oldLength = instrument.TemplateLengthTicks;
            return Prepared(
                oldLength != templateLengthTicks,
                EventInstrumentChange(eventInstrumentId),
                _ => instrument.TemplateLengthTicks = templateLengthTicks,
                _ => instrument.TemplateLengthTicks = oldLength);
        });

    public static IProjectEditCommand UpdateEventInstrumentIsolation(
        MidoraId eventInstrumentId,
        bool requiresChannelIsolation) =>
        Command("Change event instrument isolation", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            bool oldValue = instrument.RequiresChannelIsolation;
            return Prepared(
                oldValue != requiresChannelIsolation,
                EventInstrumentChange(eventInstrumentId),
                _ => instrument.RequiresChannelIsolation = requiresChannelIsolation,
                _ => instrument.RequiresChannelIsolation = oldValue);
        });

    public static IProjectEditCommand UpdateEventInstrumentOverlap(
        MidoraId eventInstrumentId,
        OverlapPolicy policy,
        OverlapScope scope) =>
        Command("Change event instrument overlap", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            if (!Enum.IsDefined(policy))
            {
                throw new ArgumentOutOfRangeException(nameof(policy));
            }
            if (!Enum.IsDefined(scope))
            {
                throw new ArgumentOutOfRangeException(nameof(scope));
            }
            if (policy == OverlapPolicy.LetOverlap && !instrument.RequiresChannelIsolation)
            {
                throw new InvalidOperationException(
                    "Let Overlap can only be selected while Per-Note Instance Isolation is enabled.");
            }
            OverlapValue old = new(instrument.OverlapPolicy, instrument.OverlapScope);
            OverlapValue replacement = new(policy, scope);
            return Prepared(
                old != replacement,
                EventInstrumentChange(eventInstrumentId),
                _ => SetOverlap(instrument, replacement),
                _ => SetOverlap(instrument, old));
        });

    public static IProjectEditCommand UpdateEventInstrumentLifecycle(
        MidoraId eventInstrumentId,
        ShortNoteLifecycle shortLifecycle,
        LongNoteLifecycle longLifecycle) =>
        Command("Change event instrument lifecycle", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            if (!Enum.IsDefined(shortLifecycle))
            {
                throw new ArgumentOutOfRangeException(nameof(shortLifecycle));
            }
            if (!Enum.IsDefined(longLifecycle))
            {
                throw new ArgumentOutOfRangeException(nameof(longLifecycle));
            }
            LifecycleValue old = new(instrument.ShortLifecycle, instrument.LongLifecycle);
            LifecycleValue replacement = new(shortLifecycle, longLifecycle);
            return Prepared(
                old != replacement,
                EventInstrumentChange(eventInstrumentId),
                _ => SetLifecycle(instrument, replacement),
                _ => SetLifecycle(instrument, old));
        });

    public static IProjectEditCommand UpdateEventInstrumentLoop(
        MidoraId eventInstrumentId,
        long? loopStartTick,
        long? loopEndTick) =>
        Command("Change event instrument loop", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            bool disabling = !loopStartTick.HasValue && !loopEndTick.HasValue;
            if (!disabling)
            {
                if (!loopStartTick.HasValue || !loopEndTick.HasValue)
                {
                    throw new ArgumentException(
                        "Loop Start and Loop End must either both be present or both be absent.");
                }
                if (!instrument.RequiresChannelIsolation)
                {
                    throw new InvalidOperationException(
                        "A Loop can only be enabled or edited while Per-Note Instance Isolation is enabled.");
                }
                if (loopStartTick.Value < 0
                    || loopEndTick.Value <= loopStartTick.Value
                    || loopEndTick.Value > instrument.TemplateLengthTicks)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(loopEndTick),
                        "Loop must be a non-empty range inside Template Length.");
                }
            }
            LoopValue old = new(instrument.LoopStartTick, instrument.LoopEndTick);
            LoopValue replacement = new(loopStartTick, loopEndTick);
            return Prepared(
                old != replacement,
                EventInstrumentChange(eventInstrumentId),
                _ => SetLoop(instrument, replacement),
                _ => SetLoop(instrument, old));
        });

    public static IProjectEditCommand UpdateSubVoiceName(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        string? name) =>
        Command("Rename subvoice", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            string? normalized = name is null
                ? null
                : ProjectTextRules.NormalizeShortText(name, allowEmpty: true, nameof(name));
            string? oldName = voice.Name;
            return Prepared(
                !string.Equals(oldName, normalized, StringComparison.Ordinal),
                EventInstrumentChange(eventInstrumentId),
                _ => voice.Name = normalized,
                _ => voice.Name = oldName);
        });

    public static IProjectEditCommand UpdateSubVoiceRootNote(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        int? rootNoteOverride) =>
        Command("Change subvoice root note", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            if (rootNoteOverride is < 0 or > 127)
            {
                throw new ArgumentOutOfRangeException(nameof(rootNoteOverride));
            }
            int? oldRootNote = voice.RootNoteOverride;
            return Prepared(
                oldRootNote != rootNoteOverride,
                EventInstrumentChange(eventInstrumentId),
                _ => voice.RootNoteOverride = rootNoteOverride,
                _ => voice.RootNoteOverride = oldRootNote);
        });

    public static IProjectEditCommand ReorderSubVoice(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        int newIndex) =>
        Command("Reorder subvoice", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            int oldIndex = instrument.SubVoices.IndexOf(voice);
            ValidateExistingIndex(newIndex, instrument.SubVoices.Count, nameof(newIndex));
            return Prepared(
                oldIndex != newIndex,
                EventInstrumentChange(eventInstrumentId),
                _ => Move(instrument.SubVoices, voice, newIndex),
                _ => Move(instrument.SubVoices, voice, oldIndex));
        });

    public static IProjectEditCommand DeleteSubVoice(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        bool nonEmptyDeletionConfirmed) =>
        Command("Delete subvoice", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            if (instrument.SubVoices.Count == 1)
            {
                throw new InvalidOperationException(
                    "The last SubVoice in an Event Instrument cannot be deleted.");
            }
            IndexedParameterMapping[] mappings = instrument.ParameterMappings
                .Select((value, index) => new IndexedParameterMapping(value, index))
                .Where(value => value.Mapping.SubVoiceId == subVoiceId)
                .ToArray();
            if (mappings.Select(value => value.Mapping.Id).Distinct().Count() != mappings.Length)
            {
                throw new InvalidOperationException(
                    "Logical Parameter Mapping stable IDs must be unique before deleting a SubVoice.");
            }
            bool nonEmpty = !string.IsNullOrEmpty(voice.Name)
                || voice.RootNoteOverride.HasValue
                || voice.Events.Count != 0
                || voice.Curves.Count != 0
                || !IsEmpty(voice.InitialState)
                || mappings.Length != 0;
            if (nonEmpty && !nonEmptyDeletionConfirmed)
            {
                throw new InvalidOperationException(
                    "Deleting a non-empty SubVoice requires explicit confirmation.");
            }
            int originalIndex = instrument.SubVoices.IndexOf(voice);
            return Prepared(
                hasChanges: true,
                EventInstrumentChange(eventInstrumentId),
                _ =>
                {
                    RequireContains(instrument.SubVoices, voice, "SubVoice");
                    foreach (IndexedParameterMapping mapping in mappings)
                    {
                        RequireContains(
                            instrument.ParameterMappings,
                            mapping.Mapping,
                            "Logical Parameter Mapping");
                    }
                    RemoveRequired(instrument.SubVoices, voice, "SubVoice");
                    for (int index = mappings.Length - 1; index >= 0; index--)
                    {
                        RemoveRequired(
                            instrument.ParameterMappings,
                            mappings[index].Mapping,
                            "Logical Parameter Mapping");
                    }
                },
                _ =>
                {
                    if (instrument.SubVoices.Any(value => value.Id == subVoiceId))
                    {
                        throw new InvalidOperationException(
                            "The SubVoice stable ID is already present.");
                    }
                    if ((uint)originalIndex > (uint)instrument.SubVoices.Count)
                    {
                        throw new InvalidOperationException(
                            "The original SubVoice index can no longer be restored.");
                    }
                    int restoredMappingCount = 0;
                    foreach (IndexedParameterMapping mapping in mappings)
                    {
                        if ((uint)mapping.Index
                            > (uint)(instrument.ParameterMappings.Count + restoredMappingCount))
                        {
                            throw new InvalidOperationException(
                                "An original Logical Parameter Mapping index can no longer be restored.");
                        }
                        restoredMappingCount++;
                    }
                    if (mappings.Any(mapping => instrument.ParameterMappings.Any(
                        value => value.Id == mapping.Mapping.Id)))
                    {
                        throw new InvalidOperationException(
                            "A deleted Logical Parameter Mapping stable ID is already present.");
                    }
                    InsertAt(instrument.SubVoices, originalIndex, voice, "SubVoice");
                    foreach (IndexedParameterMapping mapping in mappings)
                    {
                        InsertAt(
                            instrument.ParameterMappings,
                            mapping.Index,
                            mapping.Mapping,
                            "Logical Parameter Mapping");
                    }
                });
        });

    private static SubVoice FindSubVoice(EventInstrument instrument, MidoraId subVoiceId) =>
        instrument.SubVoices.SingleOrDefault(value => value.Id == subVoiceId)
        ?? throw new ArgumentOutOfRangeException(nameof(subVoiceId));

    private static long GetMinimumTemplateLength(EventInstrument instrument)
    {
        long minimum = 1;
        if (instrument.LoopEndTick.HasValue)
        {
            minimum = Math.Max(minimum, instrument.LoopEndTick.Value);
        }
        foreach (SubVoice voice in instrument.SubVoices)
        {
            foreach (TemplateEvent templateEvent in voice.Events)
            {
                minimum = Math.Max(minimum, GetRequiredTemplateBoundary(templateEvent));
            }
            foreach (CurvePoint point in voice.Curves.SelectMany(value => value.Points))
            {
                minimum = Math.Max(minimum, GetRequiredInstantBoundary(point.Tick, "Curve Point"));
            }
        }
        return minimum;
    }

    private static long GetRequiredTemplateBoundary(TemplateEvent templateEvent)
    {
        if (templateEvent.Tick < 0)
        {
            return 1;
        }
        if (templateEvent.Kind == TemplateEventKind.Note && templateEvent.LengthTicks > 0)
        {
            if (templateEvent.Tick > long.MaxValue - templateEvent.LengthTicks)
            {
                throw new InvalidOperationException(
                    "A Template Note end cannot be represented by Int64.");
            }
            return templateEvent.Tick + templateEvent.LengthTicks;
        }
        return GetRequiredInstantBoundary(templateEvent.Tick, "Template Event");
    }

    private static long GetRequiredInstantBoundary(long tick, string objectName)
    {
        if (tick < 0)
        {
            return 1;
        }
        if (tick == long.MaxValue)
        {
            throw new InvalidOperationException(
                $"The {objectName} tick leaves no representable Template Length boundary.");
        }
        return tick + 1;
    }

    private static bool IsEmpty(MidiInitialState state) =>
        !state.BankMsb.HasValue
        && !state.BankLsb.HasValue
        && !state.Program.HasValue
        && !state.PitchBend.HasValue
        && !state.PitchBendRangeSemitones.HasValue
        && !state.PitchBendRangeCents.HasValue
        && state.Controllers.Count == 0
        && state.RegisteredParameters.Count == 0
        && state.NonRegisteredParameters.Count == 0;

    private static void SetOverlap(EventInstrument instrument, OverlapValue value)
    {
        instrument.OverlapPolicy = value.Policy;
        instrument.OverlapScope = value.Scope;
    }

    private static void SetLifecycle(EventInstrument instrument, LifecycleValue value)
    {
        instrument.ShortLifecycle = value.Short;
        instrument.LongLifecycle = value.Long;
    }

    private static void SetLoop(EventInstrument instrument, LoopValue value)
    {
        instrument.LoopStartTick = value.StartTick;
        instrument.LoopEndTick = value.EndTick;
    }

    private readonly record struct OverlapValue(OverlapPolicy Policy, OverlapScope Scope);
    private readonly record struct LifecycleValue(ShortNoteLifecycle Short, LongNoteLifecycle Long);
    private readonly record struct LoopValue(long? StartTick, long? EndTick);
    private readonly record struct IndexedParameterMapping(
        LogicalParameterMapping Mapping,
        int Index);
}
