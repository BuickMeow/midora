using Midora.Domain;
using Midora.Mapping.Contract.V1;

namespace Midora.Compiler;

public static class SemanticValidator
{
    public static List<CompilerDiagnostic> Validate(MidoraProject project, CompilationRequest request)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(request);
        List<CompilerDiagnostic> diagnostics = [];
        SourceReference projectSource = new();

        if (project.TicksPerQuarterNote <= 0)
        {
            Error("MIDORA1001", "TicksPerQuarterNote 必须大于 0。", projectSource);
        }

        if (request.StartTick < 0 || request.EndTick is < 0 || request.EndTick < request.StartTick)
        {
            Error("MIDORA1002", "编译范围必须是非负且不反向的 [startTick, endTick)。", projectSource);
        }
        if (!Enum.IsDefined(request.Purpose))
        {
            Error("MIDORA1004", "编译请求的 Compilation Purpose 枚举值非法。", projectSource);
        }

        ValidateConductor(project, diagnostics);
        ValidateFolders(project, diagnostics);
        ValidateState(project.GlobalInitialState, projectSource, diagnostics);
        ValidateState(project.GlobalResetDefaults, projectSource, diagnostics);

        Dictionary<MidoraId, EventInstrument> instruments = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        HashSet<MidoraId> participatingInstrumentIds = GetParticipatingInstrumentIds(project, request);
        foreach (EventInstrument instrument in project.EventInstruments)
        {
            SourceReference source = projectSource with { EventInstrumentId = instrument.Id };
            if (!instruments.TryAdd(instrument.Id, instrument))
            {
                Error("MIDORA1201", "Event Instrument ID 重复。", source);
            }
            bool participates = participatingInstrumentIds.Contains(instrument.Id);
            if ((request.IncludedTrackIds is null || participates)
                && (string.IsNullOrWhiteSpace(instrument.Name)
                    || instrument.Name != instrument.Name.Trim()
                    || !names.Add(instrument.Name)))
            {
                Error("MIDORA1202", "Event Instrument 名称必须 trim 后非空且忽略大小写唯一。", source);
            }
            if (request.IncludedTrackIds is not null && !participates)
            {
                continue;
            }
            int internalDiagnosticStart = diagnostics.Count;
            Dictionary<MidoraId, LogicalParameterDefinition> parameters = ValidateParameterDefinitions(
                instrument.LogicalParameters, source, diagnostics);
            ValidateInstrument(instrument, parameters, source, diagnostics);
            if (!participates)
            {
                for (int i = internalDiagnosticStart; i < diagnostics.Count; i++)
                {
                    CompilerDiagnostic diagnostic = diagnostics[i];
                    if (diagnostic.Severity == DiagnosticSeverity.Error)
                    {
                        diagnostics[i] = diagnostic with { Severity = DiagnosticSeverity.Warning };
                    }
                }
            }
        }

        ValidateTracks(project, request, instruments, diagnostics);
        ValidateIncludedSubVoices(project, request, diagnostics);
        ValidateStableIds(project, request, diagnostics);
        return diagnostics;

        void Error(string code, string message, SourceReference source) =>
            diagnostics.Add(new(code, DiagnosticSeverity.Error, message, source));
    }

    private static void ValidateIncludedSubVoices(
        MidoraProject project,
        CompilationRequest request,
        List<CompilerDiagnostic> diagnostics)
    {
        if (request.IncludedSubVoiceIds is null)
        {
            return;
        }

        HashSet<MidoraId> participatingInstrumentIds = GetParticipatingInstrumentIds(project, request);
        HashSet<MidoraId> participatingSubVoiceIds = project.EventInstruments
            .Where(instrument => participatingInstrumentIds.Contains(instrument.Id))
            .SelectMany(instrument => instrument.SubVoices)
            .Select(subVoice => subVoice.Id)
            .ToHashSet();
        if (request.IncludedSubVoiceIds.Any(id => !participatingSubVoiceIds.Contains(id)))
        {
            AddError(
                "MIDORA1005",
                "编译请求引用了不属于参与编译 Event Instrument 的 SubVoice。",
                new(),
                diagnostics);
        }
    }

    internal static HashSet<MidoraId> GetParticipatingInstrumentIds(
        MidoraProject project,
        CompilationRequest request)
    {
        HashSet<MidoraId> result = [];
        foreach (LogicalTrack track in project.Tracks)
        {
            if (!track.EventInstrumentId.HasValue
                || request.IncludedTrackIds is not null && !request.IncludedTrackIds.Contains(track.Id))
            {
                continue;
            }
            result.Add(track.EventInstrumentId.Value);
        }
        return result;
    }

    private static void ValidateConductor(MidoraProject project, List<CompilerDiagnostic> diagnostics)
    {
        SourceReference source = new();
        if (project.Conductor.Tempos.Count(change => change.Tick == 0) != 1)
        {
            AddError("MIDORA1010", "Conductor 必须在 tick 0 恰好包含一个 Tempo 状态。", source, diagnostics);
        }
        if (project.Conductor.TimeSignatures.Count(change => change.Tick == 0) != 1)
        {
            AddError("MIDORA1011", "Conductor 必须在 tick 0 恰好包含一个 Time Signature 状态。", source, diagnostics);
        }
        HashSet<long> tempoTicks = [];
        foreach (TempoChange tempo in project.Conductor.Tempos)
        {
            if (tempo.Tick < 0
                || !IsRepresentableTempo(tempo.BeatsPerMinute)
                || !tempoTicks.Add(tempo.Tick))
            {
                AddError("MIDORA1012", "Tempo tick 必须非负、BPM 必须为正且同 tick 唯一。", source with { Tick = tempo.Tick }, diagnostics);
            }
        }
        HashSet<long> signatureTicks = [];
        foreach (TimeSignatureChange signature in project.Conductor.TimeSignatures)
        {
            bool supportedDenominator = signature.Denominator is 1 or 2 or 4 or 8 or 16 or 32 or 64;
            if (signature.Tick < 0 || signature.Numerator is < 1 or > 99
                || !supportedDenominator || !signatureTicks.Add(signature.Tick))
            {
                AddError("MIDORA1013", "Time Signature 非法或同 tick 重复。", source with { Tick = signature.Tick }, diagnostics);
            }
        }
        if (project.Conductor.EndMarkerTick is < 0)
        {
            AddError("MIDORA1014", "End Marker tick 不得为负。", source, diagnostics);
        }
        HashSet<long> keyTicks = [];
        foreach (KeySignatureChange key in project.Conductor.KeySignatures)
        {
            if (key.Tick < 0 || key.SharpsFlats is < -7 or > 7 || !keyTicks.Add(key.Tick))
            {
                AddError("MIDORA1015", "Key Signature 非法或同 tick 重复。", source with { Tick = key.Tick }, diagnostics);
            }
        }
        foreach (ProjectMarker marker in project.Conductor.Markers)
        {
            if (marker.Tick < 0)
            {
                AddError("MIDORA1016", "Marker tick 不得为负；名称允许为空和重复。", source with { Tick = marker.Tick }, diagnostics);
            }
        }
    }

    private static bool IsRepresentableTempo(decimal beatsPerMinute)
    {
        if (beatsPerMinute <= 0)
        {
            return false;
        }
        decimal exact;
        try
        {
            exact = 60_000_000m / beatsPerMinute;
        }
        catch (OverflowException)
        {
            return false;
        }
        decimal rounded = decimal.Round(exact, 0, MidpointRounding.AwayFromZero);
        return rounded is >= 1m and <= 16_777_215m;
    }

    private static void ValidateFolders(MidoraProject project, List<CompilerDiagnostic> diagnostics)
    {
        HashSet<MidoraId> ids = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (EventInstrumentLibraryFolder folder in project.EventInstrumentFolders)
        {
            if (!ids.Add(folder.Id) || string.IsNullOrWhiteSpace(folder.Name)
                || folder.Name != folder.Name.Trim()
                || string.Equals(folder.Name, "Unfiled", StringComparison.OrdinalIgnoreCase)
                || !names.Add(folder.Name))
            {
                AddError("MIDORA1020",
                    "Event Instrument Library Folder 必须为单层、名称 trim 后非空且忽略大小写唯一。",
                    new(), diagnostics);
            }
        }

        foreach (EventInstrument instrument in project.EventInstruments)
        {
            if (instrument.LibraryFolderId.HasValue && !ids.Contains(instrument.LibraryFolderId.Value))
            {
                diagnostics.Add(new("MIDORA1021", DiagnosticSeverity.Warning,
                    "Event Instrument 引用的 Library Folder 不存在；删除 Folder 时应把内容移至 Unfiled。",
                    new(EventInstrumentId: instrument.Id)));
            }
        }
    }

    private static void ValidateInstrument(
        EventInstrument instrument,
        IReadOnlyDictionary<MidoraId, LogicalParameterDefinition> parameters,
        SourceReference source,
        List<CompilerDiagnostic> diagnostics)
    {
        if (instrument.RootNote is < 0 or > 127 || instrument.TemplateLengthTicks <= 0)
        {
            AddError("MIDORA1210", "Event Instrument Root Note 或 Template Length 非法。", source, diagnostics);
        }
        if (instrument.SubVoices.Count is < 1 or > 256)
        {
            AddError("MIDORA1211", "Event Instrument 必须包含 1–256 个 SubVoice。", source, diagnostics);
        }
        if (!Enum.IsDefined(instrument.OverlapPolicy)
            || !Enum.IsDefined(instrument.OverlapScope)
            || !Enum.IsDefined(instrument.ShortLifecycle)
            || !Enum.IsDefined(instrument.LongLifecycle))
        {
            AddError("MIDORA1216", "Event Instrument Overlap 或生命周期枚举值非法。", source, diagnostics);
        }
        bool hasLoop = instrument.LoopStartTick.HasValue || instrument.LoopEndTick.HasValue;
        HashSet<MidoraId> envelopeIds = instrument.Envelopes.Select(value => value.Id).ToHashSet();
        Dictionary<MidoraId, CSharpMappingFunction> functions = [];
        HashSet<string> functionNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (CSharpMappingFunction function in instrument.MappingFunctions)
        {
            SourceReference functionSource = source with { MappingFunctionId = function.Id };
            string normalizedName = function.Name?.Trim() ?? string.Empty;
            if (!functions.TryAdd(function.Id, function) || normalizedName.Length == 0
                || !functionNames.Add(normalizedName))
            {
                AddError("MIDORA1273", "Mapping Function ID 与 trim 后名称必须在 Event Instrument 内唯一且非空。", functionSource, diagnostics);
            }
        }
        bool usesEnvelope = EnumerateMappingSteps(instrument)
            .Any(value => value.Source == MappingSource.Envelope);
        if (hasLoop && (!instrument.LoopStartTick.HasValue || !instrument.LoopEndTick.HasValue
            || instrument.LoopStartTick < 0 || instrument.LoopEndTick <= instrument.LoopStartTick
            || instrument.LoopEndTick > instrument.TemplateLengthTicks))
        {
            AddError("MIDORA1212", "Loop 必须是 Template 内非空的 [loopStart, loopEnd)。", source, diagnostics);
        }
        if ((hasLoop || usesEnvelope) && !instrument.RequiresChannelIsolation)
        {
            AddError("MIDORA1213", "Loop 和 Envelope 要求 Channel Isolation。", source, diagnostics);
        }
        if (instrument.OverlapPolicy == OverlapPolicy.LetOverlap && !instrument.RequiresChannelIsolation)
        {
            AddError("MIDORA1215", "Let Overlap 要求 Per-Note Instance Isolation。", source, diagnostics);
        }
        bool perNoteMapping = EnumerateMappingSteps(instrument).Any(step =>
            IsPerNoteStep(step) || step.Operation == MappingOperation.CustomCSharp
            && step.MappingFunctionId.HasValue
            && functions.TryGetValue(step.MappingFunctionId.Value, out CSharpMappingFunction? function)
            && function.DeclaredContextFields.Any(IsPerNoteContextField))
            || instrument.SubVoices.SelectMany(value => value.Events)
                .Any(value => value.Kind == TemplateEventKind.Note && HasActiveSteps(value.NumberMappings));
        if (perNoteMapping && !instrument.RequiresChannelIsolation)
        {
            AddError("MIDORA1214", "依赖单个 Logical Note 上下文的 Mapping 要求 Channel Isolation。", source, diagnostics);
        }
        ValidateState(instrument.InitialState, source, diagnostics);
        HashSet<MidoraId> subVoiceIds = [];
        foreach (SubVoice subVoice in instrument.SubVoices)
        {
            SourceReference subSource = source with { SubVoiceId = subVoice.Id };
            if (!subVoiceIds.Add(subVoice.Id) || subVoice.RootNoteOverride is < 0 or > 127)
            {
                AddError("MIDORA1220", "SubVoice ID 重复或 Root Note Override 非法。", subSource, diagnostics);
            }
            ValidateState(subVoice.InitialState, subSource, diagnostics);
            HashSet<MidiValueTarget> curveTargets = [];
            foreach (ValueCurve curve in subVoice.Curves)
            {
                SourceReference curveSource = subSource with { ValueCurveId = curve.Id };
                if (!curveTargets.Add(curve.Target) || curve.Points.Count == 0)
                {
                    AddError("MIDORA1221", "每个 SubVoice 的同一 MIDI target 最多一条非空 Curve。", curveSource, diagnostics);
                }
                ValidateTarget(curve.Target, curveSource, diagnostics);
                ValidateTargetSettings(curve.TargetSettings, curveSource, diagnostics);
                if (curve.Target.Kind is MidiValueKind.BankMsb or MidiValueKind.BankLsb or MidiValueKind.Program)
                {
                    AddError("MIDORA1223", "Bank 和 Program 不支持 Curve。", curveSource, diagnostics);
                }
                long previous = -1;
                foreach (CurvePoint point in curve.Points.OrderBy(point => point.Tick))
                {
                    if (point.Tick < 0 || point.Tick >= instrument.TemplateLengthTicks
                        || point.Tick == previous || !double.IsFinite(point.Value)
                        || !Enum.IsDefined(point.Interpolation))
                    {
                        AddError("MIDORA1222", "Curve point 的 tick/value/interpolation 非法或同 tick 重复。", curveSource with { Tick = point.Tick }, diagnostics);
                    }
                    double minimum = curve.Target.Kind == MidiValueKind.PitchBend ? -8192 : 0;
                    double maximum = curve.Target.Kind switch
                    {
                        MidiValueKind.PitchBend => 8191,
                        MidiValueKind.RegisteredParameter or MidiValueKind.NonRegisteredParameter => 16383,
                        MidiValueKind.PitchBendRangeCents => 99,
                        _ => 127
                    };
                    if ((point.Value < minimum || point.Value > maximum)
                        && curve.TargetSettings.Overflow == MappingOverflow.Fail)
                    {
                        AddError("MIDORA1224", "Curve point 超出目标 MIDI 值域。", curveSource with { Tick = point.Tick }, diagnostics);
                    }
                    previous = point.Tick;
                }
            }
            foreach (TemplateEvent templateEvent in subVoice.Events)
            {
                ValidateTemplateEvent(templateEvent, instrument.TemplateLengthTicks, subSource, diagnostics);
                IEnumerable<ValueMappingStep> eventSteps = ActiveSteps(templateEvent.NumberMappings)
                    .Concat(ActiveSteps(templateEvent.ValueMappings))
                    .Concat(ActiveSteps(templateEvent.SecondaryValueMappings));
                if (templateEvent.Kind != TemplateEventKind.Note && HasActiveSteps(templateEvent.NumberMappings))
                {
                    AddError("MIDORA1250", "只有 Note number 是可映射的事件 number；CC/RPN/NRPN number 属于事件身份。",
                        subSource with { SourceEventId = templateEvent.Id }, diagnostics);
                }
                if (templateEvent.Kind is not TemplateEventKind.Bank and not TemplateEventKind.PitchBendRange
                    && HasActiveSteps(templateEvent.SecondaryValueMappings))
                {
                    AddError("MIDORA1251", "该事件类型没有可映射的 secondary value。",
                        subSource with { SourceEventId = templateEvent.Id }, diagnostics);
                }
                if (templateEvent.Kind == TemplateEventKind.Bank
                    && (!templateEvent.HasBankMsb && HasActiveSteps(templateEvent.ValueMappings)
                        || !templateEvent.HasBankLsb && HasActiveSteps(templateEvent.SecondaryValueMappings)))
                {
                    AddError("MIDORA1253", "Bank Mapping 只能作用于该事件中实际存在的 MSB/LSB 值。",
                        subSource with { SourceEventId = templateEvent.Id }, diagnostics);
                }
                if (templateEvent.Kind != TemplateEventKind.Note
                    && eventSteps.Any(step => step.Source is MappingSource.TemplateNote or MappingSource.TemplateVelocity))
                {
                    AddError("MIDORA1252", "非 Note 事件不得使用 templateNote/templateVelocity Mapping Source。",
                        subSource with { SourceEventId = templateEvent.Id }, diagnostics);
                }
                if (templateEvent.Kind != TemplateEventKind.Note
                    && DeclaresNoteOnlyContext(eventSteps, functions))
                {
                    AddError("MIDORA1254", "非 Note 事件的 C# Mapping Function 不得声明 TemplateNote/TemplateVelocity。",
                        subSource with { SourceEventId = templateEvent.Id }, diagnostics);
                }
                ValidateMappingReferences(eventSteps,
                    parameters, envelopeIds, functions, subSource with { SourceEventId = templateEvent.Id }, diagnostics);
                if (hasLoop && templateEvent.Kind == TemplateEventKind.Note
                    && templateEvent.Tick >= instrument.LoopStartTick
                    && templateEvent.Tick < instrument.LoopEndTick
                    && templateEvent.Tick + templateEvent.LengthTicks > instrument.LoopEndTick)
                {
                    AddError("MIDORA1248", "Loop 内开始的 Note 不得把 NoteOff 延伸到 Loop End 之后。", subSource with { SourceEventId = templateEvent.Id }, diagnostics);
                }
            }
        }
        foreach (LogicalParameterMapping mapping in instrument.ParameterMappings)
        {
            SourceReference mappingSource = source with
            {
                SubVoiceId = mapping.SubVoiceId,
                LogicalParameterId = mapping.ParameterId,
                LogicalParameterMappingId = mapping.Id
            };
            if (!parameters.ContainsKey(mapping.ParameterId))
            {
                AddError("MIDORA1230", $"Parameter Mapping 引用了未知参数 '{mapping.ParameterId}'。", mappingSource, diagnostics);
            }
            if (!subVoiceIds.Contains(mapping.SubVoiceId))
            {
                AddError("MIDORA1235", $"Parameter Mapping 引用了未知 SubVoice '{mapping.SubVoiceId}'。", mappingSource, diagnostics);
            }
            ValidateTarget(mapping.Target, mappingSource, diagnostics);
            ValidateTargetSettings(mapping.TargetSettings, mappingSource, diagnostics);
            ValidateMappings(mapping.Steps, mappingSource, diagnostics);
            ValidateMappingReferences(ActiveSteps(mapping.Steps), parameters, envelopeIds, functions, mappingSource, diagnostics);
            if (ActiveSteps(mapping.Steps)
                .Any(step => step.Source is MappingSource.TemplateNote or MappingSource.TemplateVelocity))
            {
                AddError("MIDORA1252", "Logical Parameter Mapping 不得使用 TemplateNote/TemplateVelocity。",
                    mappingSource, diagnostics);
            }
            if (DeclaresNoteOnlyContext(ActiveSteps(mapping.Steps), functions))
            {
                AddError("MIDORA1254", "Logical Parameter C# Mapping Function 不得声明 TemplateNote/TemplateVelocity。",
                    mappingSource, diagnostics);
            }
        }
        foreach (IGrouping<(MidoraId SubVoiceId, MidiValueTarget Target), LogicalParameterMapping> group in
            instrument.ParameterMappings.Where(value => value.Steps.IsEnabled)
                .GroupBy(value => (value.SubVoiceId, value.Target)))
        {
            LogicalParameterMapping first = group.First();
            if (group.Skip(1).Any(value => value.TargetSettings.Rounding != first.TargetSettings.Rounding
                || value.TargetSettings.Overflow != first.TargetSettings.Overflow))
            {
                AddError("MIDORA1275", "同一 SubVoice/目标参数的 Logical Parameter Mappings 必须共享取整和最终越界策略。",
                    source with { SubVoiceId = group.Key.SubVoiceId }, diagnostics);
            }
        }
        foreach (InstrumentEnvelope envelope in instrument.Envelopes)
        {
            if (envelope.DelayTicks < 0 || envelope.AttackTicks < 0 || envelope.HoldTicks < 0
                || envelope.DecayTicks < 0 || envelope.ReleaseTicks < 0
                || !double.IsFinite(envelope.StartValue) || !double.IsFinite(envelope.PeakValue)
                || !double.IsFinite(envelope.SustainValue) || !double.IsFinite(envelope.EndValue)
                || envelope.StartValue is < 0 or > 1 || envelope.PeakValue is < 0 or > 1
                || envelope.SustainValue is < 0 or > 1 || envelope.EndValue is < 0 or > 1)
            {
                AddError("MIDORA1231", "Envelope 时间和值必须有效。",
                    source with { EnvelopeId = envelope.Id }, diagnostics);
            }
        }
    }

    private static void ValidateTemplateEvent(TemplateEvent value, long templateLength, SourceReference source, List<CompilerDiagnostic> diagnostics)
    {
        SourceReference eventSource = source with { SourceEventId = value.Id, Tick = value.Tick };
        if (!Enum.IsDefined(value.Kind))
        {
            AddError("MIDORA1249", "Template Event 类型枚举值非法。", eventSource, diagnostics);
        }
        ValidateTargetSettings(value.NumberTargetSettings, eventSource, diagnostics);
        ValidateTargetSettings(value.ValueTargetSettings, eventSource, diagnostics);
        ValidateTargetSettings(value.SecondaryValueTargetSettings, eventSource, diagnostics);
        if (value.Kind == TemplateEventKind.Note && value.NumberTargetSettings.Overflow != MappingOverflow.Fail)
        {
            AddError("MIDORA1276", "Note number 的最终越界策略必须为 Fail。", eventSource, diagnostics);
        }
        if (value.Tick < 0 || value.Tick >= templateLength)
        {
            AddError("MIDORA1240", "Template event 必须位于 Template 内。", eventSource, diagnostics);
        }
        switch (value.Kind)
        {
            case TemplateEventKind.Note:
                if (value.LengthTicks <= 0 || value.Number is < 0 or > 127 || value.Value is < 1 or > 127
                    || value.Tick > long.MaxValue - Math.Max(value.LengthTicks, 0)
                    || value.Tick + Math.Max(value.LengthTicks, 0) > templateLength)
                {
                    AddError("MIDORA1241", "Template Note 的 length/note/velocity 非法，或 NoteOff 超出 Template Length。", eventSource, diagnostics);
                }
                break;
            case TemplateEventKind.ControlChange:
                if (value.Number is < 0 or > 119 || value.Number is 91 or 93 || value.Value is < 0 or > 127)
                {
                    AddError("MIDORA1242", "普通 CC 只允许 0–119，且初版禁止 CC91/CC93。", eventSource, diagnostics);
                }
                break;
            case TemplateEventKind.Bank:
                if (!value.HasBankMsb && !value.HasBankLsb
                    || value.HasBankMsb && value.Value is < 0 or > 127
                    || value.HasBankLsb && value.SecondaryValue is < 0 or > 127)
                {
                    AddError("MIDORA1243", "Bank 必须至少包含 MSB/LSB 之一，存在的值必须在 0–127。", eventSource, diagnostics);
                }
                break;
            case TemplateEventKind.Program:
                if (value.Value is < 0 or > 127)
                {
                    AddError("MIDORA1244", "Program 必须在 0–127。", eventSource, diagnostics);
                }
                break;
            case TemplateEventKind.PitchBend:
                if (value.Value is < -8192 or > 8191)
                {
                    AddError("MIDORA1245", "Pitch Bend 必须在 -8192–8191。", eventSource, diagnostics);
                }
                break;
            case TemplateEventKind.RegisteredParameter:
            case TemplateEventKind.NonRegisteredParameter:
                if (value.Number is < 0 or > 16383 || value.Value is < 0 or > 16383)
                {
                    AddError("MIDORA1246", "RPN/NRPN parameter/data 必须是 14-bit。", eventSource, diagnostics);
                }
                break;
            case TemplateEventKind.PitchBendRange:
                if (value.Value is < 0 or > 127 || value.SecondaryValue is < 0 or > 99)
                {
                    AddError("MIDORA1247", "Pitch Bend Range 必须是 0–127 semitones、0–99 cents。", eventSource, diagnostics);
                }
                break;
        }
        ValidateMappings(value.NumberMappings, eventSource, diagnostics);
        ValidateMappings(value.ValueMappings, eventSource, diagnostics);
        ValidateMappings(value.SecondaryValueMappings, eventSource, diagnostics);
    }

    private static Dictionary<MidoraId, LogicalParameterDefinition> ValidateParameterDefinitions(
        IReadOnlyList<LogicalParameterDefinition> definitions,
        SourceReference source,
        List<CompilerDiagnostic> diagnostics)
    {
        Dictionary<MidoraId, LogicalParameterDefinition> result = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (LogicalParameterDefinition parameter in definitions)
        {
            SourceReference parameterSource = source with { LogicalParameterId = parameter.Id };
            if (!result.TryAdd(parameter.Id, parameter) || string.IsNullOrWhiteSpace(parameter.Name)
                || parameter.Name != parameter.Name.Trim() || !names.Add(parameter.Name))
            {
                AddError("MIDORA1101", "Logical Parameter ID 与 trim 后名称必须在 Event Instrument 内唯一且非空。", parameterSource, diagnostics);
            }
            if (!Enum.IsDefined(parameter.Type)
                || !double.IsFinite(parameter.Minimum) || !double.IsFinite(parameter.Maximum)
                || !double.IsFinite(parameter.DisplayMinimum) || !double.IsFinite(parameter.DisplayMaximum)
                || !double.IsFinite(parameter.DefaultValue) || parameter.Maximum < parameter.Minimum
                || parameter.DisplayMaximum < parameter.DisplayMinimum
                || parameter.DefaultValue < parameter.Minimum || parameter.DefaultValue > parameter.Maximum)
            {
                AddError("MIDORA1102", $"Logical Parameter '{parameter.Name}' 的类型、合法范围、显示范围或默认值无效。", parameterSource, diagnostics);
            }
            if (parameter.Type == LogicalParameterType.Integer
                && (parameter.Minimum != Math.Truncate(parameter.Minimum)
                    || parameter.Maximum != Math.Truncate(parameter.Maximum)
                    || parameter.DefaultValue != Math.Truncate(parameter.DefaultValue)))
            {
                AddError("MIDORA1103", $"Integer 参数 '{parameter.Name}' 的合法范围和默认值必须是整数。", parameterSource, diagnostics);
            }
            if (parameter.Type == LogicalParameterType.Enum && parameter.EnumItems.Count == 0)
            {
                AddError("MIDORA1104", $"枚举参数 '{parameter.Name}' 必须至少有一个枚举项。", parameterSource, diagnostics);
            }
            if (parameter.Type == LogicalParameterType.Enum)
            {
                HashSet<string> enumNames = new(StringComparer.OrdinalIgnoreCase);
                HashSet<int> enumValues = [];
                for (int i = 0; i < parameter.EnumItems.Count; i++)
                {
                    LogicalParameterEnumItem item = parameter.EnumItems[i];
                    int effectiveValue = parameter.UsesExplicitEnumValues ? item.Value : i;
                    if (string.IsNullOrWhiteSpace(item.Name) || item.Name != item.Name.Trim()
                        || !enumNames.Add(item.Name) || !enumValues.Add(effectiveValue)
                        || effectiveValue < parameter.Minimum || effectiveValue > parameter.Maximum)
                    {
                        AddError("MIDORA1105", $"Enum 参数 '{parameter.Name}' 的 item 名称/值非法、重复或超出合法范围。", parameterSource, diagnostics);
                    }
                }
                if (parameter.DefaultValue < int.MinValue || parameter.DefaultValue > int.MaxValue
                    || parameter.DefaultValue != Math.Truncate(parameter.DefaultValue)
                    || !enumValues.Contains((int)parameter.DefaultValue))
                {
                    AddError("MIDORA1106", $"Enum 参数 '{parameter.Name}' 的默认值不是有效枚举项。", parameterSource, diagnostics);
                }
            }
        }
        return result;
    }

    private static void ValidateTracks(
        MidoraProject project,
        CompilationRequest request,
        IReadOnlyDictionary<MidoraId, EventInstrument> instruments,
        List<CompilerDiagnostic> diagnostics)
    {
        HashSet<MidoraId> allTrackIds = [];
        HashSet<MidoraId> participatingTrackIds = [];
        foreach (LogicalTrack track in project.Tracks)
        {
            SourceReference trackSource = new(TrackId: track.Id);
            allTrackIds.Add(track.Id);
            if (request.IncludedTrackIds is not null
                && !request.IncludedTrackIds.Contains(track.Id))
            {
                continue;
            }
            if (!participatingTrackIds.Add(track.Id))
            {
                AddError("MIDORA1301", "Logical Track ID 必须唯一；名称允许为空和重复。", trackSource, diagnostics);
            }
            EventInstrument? boundInstrument = null;
            if (track.EventInstrumentId.HasValue
                && !instruments.TryGetValue(track.EventInstrumentId.Value, out boundInstrument))
            {
                diagnostics.Add(new("MIDORA1303", DiagnosticSeverity.Info,
                    "Logical Track 的 Event Instrument 引用已断裂；本次按未绑定 Track 处理。", trackSource));
            }
            if (boundInstrument is null
                && track.Segments.Any(segment => segment.Notes.Count != 0 || segment.ParameterLanes.Count != 0))
            {
                diagnostics.Add(new("MIDORA1304", DiagnosticSeverity.Info,
                    "未绑定 Event Instrument 的非空 Logical Track 不产生编译输出。", trackSource));
            }
            Dictionary<MidoraId, LogicalParameterDefinition> parameters = [];
            if (boundInstrument is not null)
            {
                foreach (LogicalParameterDefinition parameter in boundInstrument.LogicalParameters)
                {
                    parameters.TryAdd(parameter.Id, parameter);
                }
            }
            long? previousEndTick = null;
            foreach (Segment segment in track.Segments.OrderBy(segment => segment.ProjectStartTick).ThenBy(segment => segment.Id))
            {
                SourceReference segmentSource = trackSource with { SegmentId = segment.Id, Tick = segment.ProjectStartTick };
                bool projectRangeRepresentable = segment.LengthTicks > 0
                    && segment.ProjectStartTick >= 0
                    && segment.ProjectStartTick <= long.MaxValue - segment.LengthTicks;
                bool contentRangeRepresentable = segment.LengthTicks > 0
                    && segment.ContentOffsetTick >= 0
                    && segment.ContentOffsetTick <= long.MaxValue - segment.LengthTicks;
                if (!projectRangeRepresentable || !contentRangeRepresentable)
                {
                    AddError("MIDORA1310", "Segment 位置、长度、Content Offset 非法或时间范围超出 Int64。", segmentSource, diagnostics);
                }
                if (projectRangeRepresentable && previousEndTick > segment.ProjectStartTick)
                {
                    AddError("MIDORA1311", "同一 Logical Track 的 Segment 不得重叠。", segmentSource, diagnostics);
                }
                if (projectRangeRepresentable)
                {
                    previousEndTick = segment.ProjectStartTick + segment.LengthTicks;
                }
                HashSet<MidoraId> laneIds = [];
                foreach (LogicalParameterLane lane in segment.ParameterLanes)
                {
                    if (!laneIds.Add(lane.ParameterId))
                    {
                        AddError("MIDORA1312", $"Segment 参数 Lane '{lane.ParameterId}' 重复。", segmentSource, diagnostics);
                    }
                    else if (boundInstrument is not null && !parameters.ContainsKey(lane.ParameterId))
                    {
                        diagnostics.Add(new("MIDORA1314", DiagnosticSeverity.Warning,
                            $"Segment 参数 Lane '{lane.ParameterId}' 的引用已断裂；数据保留但不参与编译。", segmentSource));
                    }
                    long prior = -1;
                    parameters.TryGetValue(lane.ParameterId, out LogicalParameterDefinition? definition);
                    foreach (CurvePoint point in lane.Points.OrderBy(point => point.Tick))
                    {
                        if (point.Tick < 0 || point.Tick == prior || !double.IsFinite(point.Value)
                            || !Enum.IsDefined(point.Interpolation))
                        {
                            AddError("MIDORA1313", "参数 Lane point 的 tick/value/interpolation 非法或同 tick 重复。", segmentSource with { Tick = point.Tick }, diagnostics);
                        }
                        if (definition is not null)
                        {
                            if (point.Value < definition.Minimum || point.Value > definition.Maximum)
                            {
                                AddError("MIDORA1315", "参数 Lane point 超出 Logical Parameter 合法范围。", segmentSource with { Tick = point.Tick }, diagnostics);
                            }
                            if (definition.Type == LogicalParameterType.Enum
                                && point.Interpolation != CurveInterpolation.Step)
                            {
                                AddError("MIDORA1316", "Enum Logical Parameter 只允许阶梯变化。", segmentSource with { Tick = point.Tick }, diagnostics);
                            }
                            if (definition.Type == LogicalParameterType.Integer
                                && point.Value != Math.Truncate(point.Value))
                            {
                                AddError("MIDORA1317", "Integer Logical Parameter point 必须是整数。",
                                    segmentSource with { Tick = point.Tick }, diagnostics);
                            }
                            if (definition.Type == LogicalParameterType.Enum
                                && (point.Value < int.MinValue || point.Value > int.MaxValue
                                    || point.Value != Math.Truncate(point.Value)
                                    || !GetEnumValues(definition).Contains((int)point.Value)))
                            {
                                AddError("MIDORA1318", "Enum Logical Parameter point 必须引用有效枚举值。",
                                    segmentSource with { Tick = point.Tick }, diagnostics);
                            }
                        }
                        prior = point.Tick;
                    }
                }
                foreach (LogicalNote note in segment.Notes)
                {
                    SourceReference noteSource = segmentSource with { LogicalNoteId = note.Id, EventInstrumentId = track.EventInstrumentId ?? default, Tick = note.StartTick };
                    if (note.StartTick < 0 || note.LengthTicks <= 0
                        || note.Note is < 0 or > 127 || note.Velocity is < 1 or > 127
                        || note.StartTick > long.MaxValue - Math.Max(note.LengthTicks, 0))
                    {
                        AddError("MIDORA1320", "Logical Note 位置、长度、note、velocity 非法或时间范围超出 Int64。", noteSource, diagnostics);
                    }
                }
            }
        }
        if (request.IncludedTrackIds is not null && request.IncludedTrackIds.Any(id => !allTrackIds.Contains(id)))
        {
            AddError("MIDORA1302", "编译请求引用了不存在的 Logical Track。", new(), diagnostics);
        }
    }

    private static HashSet<int> GetEnumValues(LogicalParameterDefinition definition)
    {
        HashSet<int> values = [];
        for (int i = 0; i < definition.EnumItems.Count; i++)
        {
            values.Add(definition.UsesExplicitEnumValues ? definition.EnumItems[i].Value : i);
        }
        return values;
    }

    private static void ValidateStableIds(
        MidoraProject project,
        CompilationRequest request,
        List<CompilerDiagnostic> diagnostics)
    {
        HashSet<MidoraId> ids = [];
        HashSet<MidoraId> participatingInstrumentIds = GetParticipatingInstrumentIds(project, request);
        foreach (TempoChange value in project.Conductor.Tempos) Add(value.Id, new(Tick: value.Tick));
        foreach (TimeSignatureChange value in project.Conductor.TimeSignatures) Add(value.Id, new(Tick: value.Tick));
        foreach (KeySignatureChange value in project.Conductor.KeySignatures) Add(value.Id, new(Tick: value.Tick));
        foreach (ProjectMarker marker in project.Conductor.Markers) Add(marker.Id, new(Tick: marker.Tick));
        if (project.Conductor.EndMarker is not null)
        {
            Add(project.Conductor.EndMarker.Id, new(Tick: project.Conductor.EndMarker.Tick));
        }
        foreach (EventInstrumentLibraryFolder folder in project.EventInstrumentFolders)
        {
            Add(folder.Id, new());
        }
        foreach (EventInstrument instrument in project.EventInstruments)
        {
            if (request.IncludedTrackIds is not null
                && !participatingInstrumentIds.Contains(instrument.Id))
            {
                continue;
            }
            SourceReference instrumentSource = new(EventInstrumentId: instrument.Id);
            Add(instrument.Id, instrumentSource);
            foreach (LogicalParameterDefinition parameter in instrument.LogicalParameters)
            {
                Add(parameter.Id, instrumentSource);
                foreach (LogicalParameterEnumItem item in parameter.EnumItems) Add(item.Id, instrumentSource);
            }
            foreach (LogicalParameterMapping mapping in instrument.ParameterMappings)
            {
                Add(mapping.Id, instrumentSource);
                Add(mapping.Steps.Id, instrumentSource);
                foreach (ValueMappingStep step in mapping.Steps) Add(step.Id, instrumentSource);
            }
            foreach (InstrumentEnvelope envelope in instrument.Envelopes) Add(envelope.Id, instrumentSource);
            foreach (CSharpMappingFunction function in instrument.MappingFunctions) Add(function.Id, instrumentSource);
            foreach (SubVoice voice in instrument.SubVoices)
            {
                SourceReference voiceSource = instrumentSource with { SubVoiceId = voice.Id };
                Add(voice.Id, voiceSource);
                foreach (TemplateEvent value in voice.Events)
                {
                    SourceReference eventSource = voiceSource with { SourceEventId = value.Id, Tick = value.Tick };
                    Add(value.Id, eventSource);
                    Add(value.NumberMappings.Id, eventSource);
                    Add(value.ValueMappings.Id, eventSource);
                    Add(value.SecondaryValueMappings.Id, eventSource);
                    foreach (ValueMappingStep step in value.NumberMappings) Add(step.Id, eventSource);
                    foreach (ValueMappingStep step in value.ValueMappings) Add(step.Id, eventSource);
                    foreach (ValueMappingStep step in value.SecondaryValueMappings) Add(step.Id, eventSource);
                }
                foreach (ValueCurve curve in voice.Curves)
                {
                    Add(curve.Id, voiceSource);
                    foreach (CurvePoint point in curve.Points) Add(point.Id, voiceSource with { Tick = point.Tick });
                }
            }
        }
        foreach (LogicalTrack track in project.Tracks)
        {
            if (request.IncludedTrackIds is not null
                && !request.IncludedTrackIds.Contains(track.Id))
            {
                continue;
            }
            SourceReference trackSource = new(TrackId: track.Id);
            Add(track.Id, trackSource);
            foreach (Segment segment in track.Segments)
            {
                SourceReference segmentSource = trackSource with { SegmentId = segment.Id, Tick = segment.ProjectStartTick };
                Add(segment.Id, segmentSource);
                foreach (LogicalNote note in segment.Notes) Add(note.Id, segmentSource with { LogicalNoteId = note.Id });
                foreach (LogicalParameterLane lane in segment.ParameterLanes)
                {
                    Add(lane.Id, segmentSource);
                    foreach (CurvePoint point in lane.Points) Add(point.Id, segmentSource with { Tick = point.Tick });
                }
            }
        }

        void Add(MidoraId id, SourceReference source)
        {
            if (id == default || id.ToSequence() >= project.NextStableId || !ids.Add(id))
            {
                AddError("MIDORA1003",
                    "正式对象 Stable ID 为空、在 Project 内重复，或不属于当前 Project 的已分配计数器范围。",
                    source, diagnostics);
            }
        }
    }

    private static void ValidateState(MidiInitialState state, SourceReference source, List<CompilerDiagnostic> diagnostics)
    {
        if (state.BankMsb is < 0 or > 127 || state.BankLsb is < 0 or > 127 || state.Program is < 0 or > 127
            || state.PitchBend is < -8192 or > 8191 || state.PitchBendRangeSemitones is < 0 or > 127
            || state.PitchBendRangeCents is < 0 or > 99)
        {
            AddError("MIDORA1250", "Initial State 含非法 MIDI 值。", source, diagnostics);
        }
        foreach ((int controller, int value) in state.Controllers)
        {
            if (controller is < 0 or > 119 || controller is 91 or 93 || value is < 0 or > 127)
            {
                AddError("MIDORA1251", "Initial State CC 必须为 0–119，值为 0–127，且禁止 CC91/CC93。", source, diagnostics);
            }
        }
        foreach ((int parameter, int value) in state.RegisteredParameters.Concat(state.NonRegisteredParameters))
        {
            if (parameter is < 0 or > 16383 || value is < 0 or > 16383)
            {
                AddError("MIDORA1252", "Initial State RPN/NRPN parameter/data 必须是 14-bit。", source, diagnostics);
            }
        }
    }

    private static void ValidateTarget(MidiValueTarget target, SourceReference source, List<CompilerDiagnostic> diagnostics)
    {
        bool invalid = !Enum.IsDefined(target.Kind) || target.Kind switch
        {
            MidiValueKind.ControlChange => target.Number is < 0 or > 119 || target.Number is 91 or 93,
            MidiValueKind.RegisteredParameter or MidiValueKind.NonRegisteredParameter => target.Number is < 0 or > 16383,
            _ => target.Number != 0
        };
        if (invalid)
        {
            AddError("MIDORA1260", "Mapping/Curve target 非法或为被禁止的 CC91/CC93。", source, diagnostics);
        }
    }

    private static void ValidateTargetSettings(
        MidiIntegerTargetSettings settings,
        SourceReference source,
        List<CompilerDiagnostic> diagnostics)
    {
        if (!Enum.IsDefined(settings.Rounding) || !Enum.IsDefined(settings.Overflow))
        {
            AddError("MIDORA1277", "整数目标参数的取整或最终越界策略非法。", source, diagnostics);
        }
    }

    private static void ValidateMappings(MappingChain steps, SourceReference source, List<CompilerDiagnostic> diagnostics)
    {
        foreach (ValueMappingStep step in ActiveSteps(steps))
        {
            SourceReference stepSource = source with
            {
                MappingStepId = step.Id,
                MappingFunctionId = step.MappingFunctionId ?? default,
                LogicalParameterId = step.LogicalParameterId ?? source.LogicalParameterId,
                EnvelopeId = step.EnvelopeId ?? default
            };
            if (!Enum.IsDefined(step.Source) || !Enum.IsDefined(step.Operation)
                || !Enum.IsDefined(step.InputOverflow) || !Enum.IsDefined(step.DivideByZero)
                || !double.IsFinite(step.Constant) || !double.IsFinite(step.SourceMinimum)
                || !double.IsFinite(step.SourceMaximum) || !double.IsFinite(step.TargetMinimum)
                || !double.IsFinite(step.TargetMaximum) || step.SourceMaximum < step.SourceMinimum
                || step.TargetMaximum < step.TargetMinimum)
            {
                AddError("MIDORA1270", "Mapping step 的枚举配置或数值范围非法。", stepSource, diagnostics);
            }
            if (step.Operation == MappingOperation.CustomCSharp && !step.MappingFunctionId.HasValue)
            {
                AddError("MIDORA1271", "C# Mapping step 缺少 Mapping Function 引用。", stepSource, diagnostics);
            }
        }
    }

    private static IEnumerable<ValueMappingStep> EnumerateMappingSteps(EventInstrument instrument) =>
        instrument.ParameterMappings.SelectMany(value => ActiveSteps(value.Steps))
            .Concat(instrument.SubVoices.SelectMany(value => value.Events)
                .SelectMany(value => ActiveSteps(value.NumberMappings).Concat(ActiveSteps(value.ValueMappings))
                    .Concat(ActiveSteps(value.SecondaryValueMappings))));

    private static IEnumerable<ValueMappingStep> ActiveSteps(MappingChain chain) =>
        chain.IsEnabled ? chain.Where(value => value.IsEnabled) : [];

    private static bool HasActiveSteps(MappingChain chain) => chain.IsEnabled && chain.Any(value => value.IsEnabled);

    private static bool DeclaresNoteOnlyContext(
        IEnumerable<ValueMappingStep> steps,
        IReadOnlyDictionary<MidoraId, CSharpMappingFunction> functions) =>
        steps.Any(step => step.Operation == MappingOperation.CustomCSharp
            && step.MappingFunctionId.HasValue
            && functions.TryGetValue(step.MappingFunctionId.Value, out CSharpMappingFunction? function)
            && (function.DeclaredContextFields.Contains(nameof(MappingContextV1.TemplateNote))
                || function.DeclaredContextFields.Contains(nameof(MappingContextV1.TemplateVelocity))));

    private static void ValidateMappingReferences(
        IEnumerable<ValueMappingStep> steps,
        IReadOnlyDictionary<MidoraId, LogicalParameterDefinition> parameters,
        IReadOnlySet<MidoraId> envelopes,
        IReadOnlyDictionary<MidoraId, CSharpMappingFunction> functions,
        SourceReference source,
        List<CompilerDiagnostic> diagnostics)
    {
        foreach (ValueMappingStep step in steps)
        {
            SourceReference stepSource = source with
            {
                MappingStepId = step.Id,
                MappingFunctionId = step.MappingFunctionId ?? default,
                LogicalParameterId = step.LogicalParameterId ?? source.LogicalParameterId,
                EnvelopeId = step.EnvelopeId ?? default
            };
            if (step.Source == MappingSource.LogicalParameter
                && (!step.LogicalParameterId.HasValue || !parameters.ContainsKey(step.LogicalParameterId.Value)))
            {
                AddError("MIDORA1232", "Mapping step 引用了未知 Logical Parameter ID。", stepSource, diagnostics);
            }
            if (step.Source == MappingSource.Envelope
                && (!step.EnvelopeId.HasValue || !envelopes.Contains(step.EnvelopeId.Value)))
            {
                AddError("MIDORA1233", "Mapping step 引用了未知 Envelope Preset ID。", stepSource, diagnostics);
            }
            if (step.Operation == MappingOperation.CustomCSharp
                && (!step.MappingFunctionId.HasValue || !functions.ContainsKey(step.MappingFunctionId.Value)))
            {
                AddError("MIDORA1234", "Mapping step 引用了未知 Mapping Function ID。", stepSource, diagnostics);
            }
        }
    }

    private static void AddError(string code, string message, SourceReference source, List<CompilerDiagnostic> diagnostics) =>
        diagnostics.Add(new(code, DiagnosticSeverity.Error, message, source));

    private static bool IsPerNoteStep(ValueMappingStep step) => step.Source is
        MappingSource.TriggerNote or MappingSource.TriggerVelocity or MappingSource.GateLength or MappingSource.PitchDelta;

    private static bool IsPerNoteContextField(string field) => field is
        nameof(MappingContextV1.TriggerNote) or nameof(MappingContextV1.TriggerVelocity)
        or nameof(MappingContextV1.EffectiveRootNote) or nameof(MappingContextV1.PitchDelta)
        or nameof(MappingContextV1.GateLength) or nameof(MappingContextV1.SegmentLocalTick);
}
