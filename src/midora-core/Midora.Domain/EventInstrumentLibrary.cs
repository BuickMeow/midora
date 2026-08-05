namespace Midora.Domain;

public static class EventInstrumentLibrary
{
    public static EventInstrument Create(MidoraProject project, string? requestedName = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        string name = requestedName is null
            ? GenerateUniqueName(project, "Event Instrument")
            : ValidateUniqueName(project, requestedName, default);
        EventInstrument result = new()
        {
            Name = name,
            TemplateLengthTicks = project.TicksPerQuarterNote
        };
        result.SubVoices.Add(new SubVoice());
        project.EventInstruments.Add(result);
        return result;
    }

    public static void Rename(MidoraProject project, MidoraId instrumentId, string name)
    {
        EventInstrument instrument = Find(project, instrumentId);
        instrument.Name = ValidateUniqueName(project, name, instrumentId);
    }

    public static EventInstrument Duplicate(MidoraProject project, MidoraId instrumentId, string? requestedName = null)
    {
        EventInstrument source = Find(project, instrumentId);
        string name = requestedName is null
            ? GenerateUniqueName(project, $"{source.Name} Copy")
            : ValidateUniqueName(project, requestedName, default);
        Dictionary<MidoraId, MidoraId> parameters = [];
        Dictionary<MidoraId, MidoraId> voices = [];
        Dictionary<MidoraId, MidoraId> functions = [];
        Dictionary<MidoraId, MidoraId> envelopes = [];
        EventInstrument result = new()
        {
            Name = name,
            Description = source.Description,
            Color = source.Color,
            LibraryFolderId = source.LibraryFolderId,
            RootNote = source.RootNote,
            TemplateLengthTicks = source.TemplateLengthTicks,
            RequiresChannelIsolation = source.RequiresChannelIsolation,
            OverlapPolicy = source.OverlapPolicy,
            OverlapScope = source.OverlapScope,
            ShortLifecycle = source.ShortLifecycle,
            LongLifecycle = source.LongLifecycle,
            LoopStartTick = source.LoopStartTick,
            LoopEndTick = source.LoopEndTick
        };
        CopyState(source.InitialState, result.InitialState);

        foreach (LogicalParameterDefinition definition in source.LogicalParameters)
        {
            LogicalParameterDefinition copy = new()
            {
                Name = definition.Name,
                Type = definition.Type,
                Minimum = definition.Minimum,
                Maximum = definition.Maximum,
                DisplayMinimum = definition.DisplayMinimum,
                DisplayMaximum = definition.DisplayMaximum,
                DefaultValue = definition.DefaultValue,
                UsesExplicitEnumValues = definition.UsesExplicitEnumValues
            };
            foreach (LogicalParameterEnumItem item in definition.EnumItems)
            {
                copy.EnumItems.Add(new() { Name = item.Name, Value = item.Value });
            }
            parameters.Add(definition.Id, copy.Id);
            result.LogicalParameters.Add(copy);
        }
        foreach (CSharpMappingFunction function in source.MappingFunctions)
        {
            CSharpMappingFunction copy = new() { Name = function.Name, Body = function.Body };
            copy.DeclaredContextFields.UnionWith(function.DeclaredContextFields);
            functions.Add(function.Id, copy.Id);
            result.MappingFunctions.Add(copy);
        }
        foreach (InstrumentEnvelope envelope in source.Envelopes)
        {
            InstrumentEnvelope copy = new()
            {
                Name = envelope.Name,
                DelayTicks = envelope.DelayTicks,
                AttackTicks = envelope.AttackTicks,
                HoldTicks = envelope.HoldTicks,
                DecayTicks = envelope.DecayTicks,
                StartValue = envelope.StartValue,
                PeakValue = envelope.PeakValue,
                SustainValue = envelope.SustainValue,
                ReleaseTicks = envelope.ReleaseTicks,
                EndValue = envelope.EndValue
            };
            envelopes.Add(envelope.Id, copy.Id);
            result.Envelopes.Add(copy);
        }
        foreach (SubVoice voice in source.SubVoices)
        {
            SubVoice copy = new() { Name = voice.Name, RootNoteOverride = voice.RootNoteOverride };
            CopyState(voice.InitialState, copy.InitialState);
            voices.Add(voice.Id, copy.Id);
            foreach (TemplateEvent value in voice.Events)
            {
                TemplateEvent eventCopy = new()
                {
                    Kind = value.Kind,
                    Tick = value.Tick,
                    LengthTicks = value.LengthTicks,
                    Number = value.Number,
                    Value = value.Value,
                    SecondaryValue = value.SecondaryValue,
                    FollowPitchDelta = value.FollowPitchDelta
                };
                CopyChain(value.NumberMappings, eventCopy.NumberMappings, parameters, functions, envelopes);
                CopyChain(value.ValueMappings, eventCopy.ValueMappings, parameters, functions, envelopes);
                CopyChain(value.SecondaryValueMappings, eventCopy.SecondaryValueMappings, parameters, functions, envelopes);
                copy.Events.Add(eventCopy);
            }
            foreach (ValueCurve curve in voice.Curves)
            {
                ValueCurve curveCopy = new() { Target = curve.Target };
                foreach (CurvePoint point in curve.Points)
                {
                    curveCopy.Points.Add(new(point.Tick, point.Value, point.Interpolation));
                }
                copy.Curves.Add(curveCopy);
            }
            result.SubVoices.Add(copy);
        }
        foreach (LogicalParameterMapping mapping in source.ParameterMappings)
        {
            LogicalParameterMapping copy = new()
            {
                ParameterId = Remap(parameters, mapping.ParameterId),
                SubVoiceId = Remap(voices, mapping.SubVoiceId),
                Target = mapping.Target
            };
            CopyChain(mapping.Steps, copy.Steps, parameters, functions, envelopes);
            result.ParameterMappings.Add(copy);
        }

        project.EventInstruments.Add(result);
        return result;
    }

    public static IReadOnlyList<LogicalTrack> Delete(
        MidoraProject project,
        MidoraId instrumentId,
        bool referencedDeletionConfirmed)
    {
        EventInstrument instrument = Find(project, instrumentId);
        LogicalTrack[] affected = project.Tracks.Where(value => value.EventInstrumentId == instrumentId).ToArray();
        if (affected.Length != 0 && !referencedDeletionConfirmed)
        {
            throw new InvalidOperationException("Deleting a referenced Event Instrument requires explicit confirmation.");
        }
        foreach (LogicalTrack track in affected)
        {
            track.LastBoundEventInstrumentName = instrument.Name;
            track.EventInstrumentId = null;
        }
        _ = project.EventInstruments.Remove(instrument);
        return affected;
    }

    private static EventInstrument Find(MidoraProject project, MidoraId id) =>
        project.EventInstruments.FirstOrDefault(value => value.Id == id)
        ?? throw new ArgumentOutOfRangeException(nameof(id));

    private static string ValidateUniqueName(MidoraProject project, string name, MidoraId excludedId)
    {
        string normalized = name.Trim();
        if (normalized.Length == 0
            || project.EventInstruments.Any(value => value.Id != excludedId
                && string.Equals(value.Name.Trim(), normalized, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Event Instrument names must be non-empty and unique ignoring case.", nameof(name));
        }
        return normalized;
    }

    private static string GenerateUniqueName(MidoraProject project, string stem)
    {
        for (int index = 1; ; index++)
        {
            string candidate = $"{stem} {index}";
            if (!project.EventInstruments.Any(value =>
                string.Equals(value.Name.Trim(), candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }
    }

    private static void CopyChain(
        MappingChain source,
        MappingChain target,
        IReadOnlyDictionary<MidoraId, MidoraId> parameters,
        IReadOnlyDictionary<MidoraId, MidoraId> functions,
        IReadOnlyDictionary<MidoraId, MidoraId> envelopes)
    {
        target.IsEnabled = source.IsEnabled;
        foreach (ValueMappingStep step in source)
        {
            target.Add(new ValueMappingStep
            {
                IsEnabled = step.IsEnabled,
                Source = step.Source,
                Operation = step.Operation,
                LogicalParameterId = Remap(parameters, step.LogicalParameterId),
                EnvelopeId = Remap(envelopes, step.EnvelopeId),
                MappingFunctionId = Remap(functions, step.MappingFunctionId),
                Constant = step.Constant,
                SourceMinimum = step.SourceMinimum,
                SourceMaximum = step.SourceMaximum,
                TargetMinimum = step.TargetMinimum,
                TargetMaximum = step.TargetMaximum,
                Rounding = step.Rounding,
                InputOverflow = step.InputOverflow,
                Overflow = step.Overflow,
                DivideByZero = step.DivideByZero
            });
        }
    }

    private static MidoraId Remap(IReadOnlyDictionary<MidoraId, MidoraId> map, MidoraId id) =>
        map.TryGetValue(id, out MidoraId result) ? result : id;

    private static MidoraId? Remap(IReadOnlyDictionary<MidoraId, MidoraId> map, MidoraId? id) =>
        id.HasValue ? Remap(map, id.Value) : null;

    private static void CopyState(MidiInitialState source, MidiInitialState target)
    {
        target.BankMsb = source.BankMsb;
        target.BankLsb = source.BankLsb;
        target.Program = source.Program;
        target.PitchBend = source.PitchBend;
        target.PitchBendRangeSemitones = source.PitchBendRangeSemitones;
        target.PitchBendRangeCents = source.PitchBendRangeCents;
        foreach ((int key, int value) in source.Controllers) target.Controllers.Add(key, value);
        foreach ((int key, int value) in source.RegisteredParameters) target.RegisteredParameters.Add(key, value);
        foreach ((int key, int value) in source.NonRegisteredParameters) target.NonRegisteredParameters.Add(key, value);
    }
}
