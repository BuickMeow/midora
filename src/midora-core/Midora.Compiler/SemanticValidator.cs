using Midora.Domain;

namespace Midora.Compiler;

public static class SemanticValidator
{
    public static List<CompilerDiagnostic> Validate(MidoraProject project, CompilationRequest request)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(request);
        List<CompilerDiagnostic> diagnostics = [];
        SourceReference projectSource = new(project.Id);

        if (project.TicksPerQuarterNote <= 0)
        {
            Error("MIDORA1001", "TicksPerQuarterNote 必须大于 0。", projectSource);
        }

        if (request.StartTick < 0 || request.EndTick is < 0 || request.EndTick < request.StartTick)
        {
            Error("MIDORA1002", "编译范围必须是非负且不反向的 [startTick, endTick)。", projectSource);
        }

        ValidateConductor(project, diagnostics);
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
            if (string.IsNullOrWhiteSpace(instrument.Name) || instrument.Name != instrument.Name.Trim() || !names.Add(instrument.Name))
            {
                Error("MIDORA1202", "Event Instrument 名称必须 trim 后非空且忽略大小写唯一。", source);
            }
            int internalDiagnosticStart = diagnostics.Count;
            Dictionary<MidoraId, LogicalParameterDefinition> parameters = ValidateParameterDefinitions(
                instrument.LogicalParameters, source, diagnostics);
            ValidateInstrument(instrument, parameters, source, diagnostics);
            if (!participatingInstrumentIds.Contains(instrument.Id))
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
        ValidateStableIds(project, diagnostics);
        return diagnostics;

        void Error(string code, string message, SourceReference source) =>
            diagnostics.Add(new(code, DiagnosticSeverity.Error, message, source));
    }

    internal static HashSet<MidoraId> GetParticipatingInstrumentIds(
        MidoraProject project,
        CompilationRequest request)
    {
        long rangeEnd = request.EndTick ?? project.Conductor.EndMarkerTick ?? long.MaxValue;
        HashSet<MidoraId> result = [];
        foreach (LogicalTrack track in project.Tracks)
        {
            if (!track.EventInstrumentId.HasValue
                || request.IncludedTrackIds is not null && !request.IncludedTrackIds.Contains(track.Id))
            {
                continue;
            }
            bool participates = track.Segments.Any(segment =>
                segment.ProjectStartTick < rangeEnd
                && segment.LengthTicks > 0
                && segment.ProjectStartTick <= long.MaxValue - segment.LengthTicks
                && segment.ProjectStartTick + segment.LengthTicks > request.StartTick
                && segment.Notes.Count != 0);
            if (participates)
            {
                result.Add(track.EventInstrumentId.Value);
            }
        }
        return result;
    }

    private static void ValidateConductor(MidoraProject project, List<CompilerDiagnostic> diagnostics)
    {
        SourceReference source = new(project.Id);
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
            if (tempo.Tick < 0 || tempo.BeatsPerMinute <= 0 || !tempoTicks.Add(tempo.Tick))
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
            if (marker.Tick < 0 || string.IsNullOrWhiteSpace(marker.Name))
            {
                AddError("MIDORA1016", "Marker 必须有非空名称和非负 tick。", source with { Tick = marker.Tick }, diagnostics);
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
        bool hasLoop = instrument.LoopStartTick.HasValue || instrument.LoopEndTick.HasValue;
        HashSet<MidoraId> envelopeIds = instrument.Envelopes.Select(value => value.Id).ToHashSet();
        Dictionary<MidoraId, CSharpMappingFunction> functions = [];
        HashSet<string> functionNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (CSharpMappingFunction function in instrument.MappingFunctions)
        {
            if (!functions.TryAdd(function.Id, function) || string.IsNullOrWhiteSpace(function.Name)
                || function.Name != function.Name.Trim() || !functionNames.Add(function.Name))
            {
                AddError("MIDORA1273", "Mapping Function ID 与 trim 后名称必须在 Event Instrument 内唯一且非空。", source, diagnostics);
            }
            if (string.IsNullOrWhiteSpace(function.Body)
                || function.DeclaredContextFields.Any(field => !ValidMappingContextFields.Contains(field)))
            {
                AddError("MIDORA1274", "Mapping Function 函数体为空或声明了未知 Context 字段。", source, diagnostics);
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
                if (!curveTargets.Add(curve.Target) || curve.Points.Count == 0)
                {
                    AddError("MIDORA1221", "每个 SubVoice 的同一 MIDI target 最多一条非空 Curve。", subSource, diagnostics);
                }
                ValidateTarget(curve.Target, subSource, diagnostics);
                if (curve.Target.Kind is MidiValueKind.BankMsb or MidiValueKind.BankLsb or MidiValueKind.Program)
                {
                    AddError("MIDORA1223", "Bank 和 Program 不支持 Curve。", subSource, diagnostics);
                }
                long previous = -1;
                foreach (CurvePoint point in curve.Points.OrderBy(point => point.Tick))
                {
                    if (point.Tick < 0 || point.Tick >= instrument.TemplateLengthTicks || point.Tick == previous || !double.IsFinite(point.Value))
                    {
                        AddError("MIDORA1222", "Curve point 的 tick/value 非法或同 tick 重复。", subSource with { Tick = point.Tick }, diagnostics);
                    }
                    double minimum = curve.Target.Kind == MidiValueKind.PitchBend ? -8192 : 0;
                    double maximum = curve.Target.Kind switch
                    {
                        MidiValueKind.PitchBend => 8191,
                        MidiValueKind.RegisteredParameter or MidiValueKind.NonRegisteredParameter => 16383,
                        MidiValueKind.PitchBendRangeCents => 99,
                        _ => 127
                    };
                    if (point.Value < minimum || point.Value > maximum)
                    {
                        AddError("MIDORA1224", "Curve point 超出目标 MIDI 值域。", subSource with { Tick = point.Tick }, diagnostics);
                    }
                    previous = point.Tick;
                }
            }
            foreach (TemplateEvent templateEvent in subVoice.Events)
            {
                ValidateTemplateEvent(templateEvent, instrument.TemplateLengthTicks, subSource, diagnostics);
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
                if (templateEvent.Kind != TemplateEventKind.Note
                    && ActiveSteps(templateEvent.NumberMappings).Concat(ActiveSteps(templateEvent.ValueMappings))
                        .Concat(ActiveSteps(templateEvent.SecondaryValueMappings))
                        .Any(step => step.Source is MappingSource.TemplateNote or MappingSource.TemplateVelocity))
                {
                    AddError("MIDORA1252", "非 Note 事件不得使用 templateNote/templateVelocity Mapping Source。",
                        subSource with { SourceEventId = templateEvent.Id }, diagnostics);
                }
                ValidateMappingReferences(
                    ActiveSteps(templateEvent.NumberMappings).Concat(ActiveSteps(templateEvent.ValueMappings))
                        .Concat(ActiveSteps(templateEvent.SecondaryValueMappings)),
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
            if (!parameters.ContainsKey(mapping.ParameterId))
            {
                AddError("MIDORA1230", $"Parameter Mapping 引用了未知参数 '{mapping.ParameterId}'。", source, diagnostics);
            }
            if (!subVoiceIds.Contains(mapping.SubVoiceId))
            {
                AddError("MIDORA1235", $"Parameter Mapping 引用了未知 SubVoice '{mapping.SubVoiceId}'。", source, diagnostics);
            }
            ValidateTarget(mapping.Target, source, diagnostics);
            ValidateMappings(mapping.Steps, source, diagnostics);
            ValidateMappingReferences(ActiveSteps(mapping.Steps), parameters, envelopeIds, functions, source, diagnostics);
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
                AddError("MIDORA1231", "Envelope 时间和值必须有效。", source, diagnostics);
            }
        }
    }

    private static void ValidateTemplateEvent(TemplateEvent value, long templateLength, SourceReference source, List<CompilerDiagnostic> diagnostics)
    {
        SourceReference eventSource = source with { SourceEventId = value.Id, Tick = value.Tick };
        if (value.Tick < 0 || value.Tick >= templateLength)
        {
            AddError("MIDORA1240", "Template event 必须位于 Template 内。", eventSource, diagnostics);
        }
        switch (value.Kind)
        {
            case TemplateEventKind.Note:
                if (value.LengthTicks <= 0 || value.Number is < 0 or > 127 || value.Value is < 1 or > 127)
                {
                    AddError("MIDORA1241", "Template Note 的 length/note/velocity 非法。", eventSource, diagnostics);
                }
                break;
            case TemplateEventKind.ControlChange:
                if (value.Number is < 0 or > 119 || value.Number is 91 or 93 || value.Value is < 0 or > 127)
                {
                    AddError("MIDORA1242", "普通 CC 只允许 0–119，且初版禁止 CC91/CC93。", eventSource, diagnostics);
                }
                break;
            case TemplateEventKind.Bank:
                if (value.Value is < 0 or > 127 || value.SecondaryValue is < 0 or > 127)
                {
                    AddError("MIDORA1243", "Bank MSB/LSB 必须在 0–127。", eventSource, diagnostics);
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
            if (!result.TryAdd(parameter.Id, parameter) || string.IsNullOrWhiteSpace(parameter.Name)
                || parameter.Name != parameter.Name.Trim() || !names.Add(parameter.Name))
            {
                AddError("MIDORA1101", "Logical Parameter ID 与 trim 后名称必须在 Event Instrument 内唯一且非空。", source, diagnostics);
            }
            if (!double.IsFinite(parameter.Minimum) || !double.IsFinite(parameter.Maximum)
                || !double.IsFinite(parameter.DisplayMinimum) || !double.IsFinite(parameter.DisplayMaximum)
                || !double.IsFinite(parameter.DefaultValue) || parameter.Maximum < parameter.Minimum
                || parameter.DisplayMaximum < parameter.DisplayMinimum
                || parameter.DefaultValue < parameter.Minimum || parameter.DefaultValue > parameter.Maximum)
            {
                AddError("MIDORA1102", $"Logical Parameter '{parameter.Name}' 的合法范围、显示范围或默认值无效。", source, diagnostics);
            }
            if (parameter.Type == LogicalParameterType.Integer
                && (parameter.Minimum != Math.Truncate(parameter.Minimum)
                    || parameter.Maximum != Math.Truncate(parameter.Maximum)
                    || parameter.DefaultValue != Math.Truncate(parameter.DefaultValue)))
            {
                AddError("MIDORA1103", $"Integer 参数 '{parameter.Name}' 的合法范围和默认值必须是整数。", source, diagnostics);
            }
            if (parameter.Type == LogicalParameterType.Enum && parameter.EnumItems.Count == 0)
            {
                AddError("MIDORA1104", $"枚举参数 '{parameter.Name}' 必须至少有一个枚举项。", source, diagnostics);
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
                        || !enumNames.Add(item.Name) || !enumValues.Add(effectiveValue))
                    {
                        AddError("MIDORA1105", $"Enum 参数 '{parameter.Name}' 的 item 名称/值非法或重复。", source, diagnostics);
                    }
                }
                if (parameter.DefaultValue < int.MinValue || parameter.DefaultValue > int.MaxValue
                    || parameter.DefaultValue != Math.Truncate(parameter.DefaultValue)
                    || !enumValues.Contains((int)parameter.DefaultValue))
                {
                    AddError("MIDORA1106", $"Enum 参数 '{parameter.Name}' 的默认值不是有效枚举项。", source, diagnostics);
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
        HashSet<MidoraId> trackIds = [];
        foreach (LogicalTrack track in project.Tracks)
        {
            SourceReference trackSource = new(project.Id, TrackId: track.Id);
            if (!trackIds.Add(track.Id) || string.IsNullOrWhiteSpace(track.Name) || track.Name != track.Name.Trim())
            {
                AddError("MIDORA1301", "Logical Track ID 必须唯一且名称非空。", trackSource, diagnostics);
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
                        if (point.Tick < 0 || point.Tick == prior || !double.IsFinite(point.Value))
                        {
                            AddError("MIDORA1313", "参数 Lane point 非法或同 tick 重复。", segmentSource with { Tick = point.Tick }, diagnostics);
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
        if (request.IncludedTrackIds is not null && request.IncludedTrackIds.Any(id => !trackIds.Contains(id)))
        {
            AddError("MIDORA1302", "编译请求引用了不存在的 Logical Track。", new(project.Id), diagnostics);
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

    private static void ValidateStableIds(MidoraProject project, List<CompilerDiagnostic> diagnostics)
    {
        HashSet<MidoraId> ids = [];
        Add(project.Id, new(project.Id));
        foreach (TempoChange value in project.Conductor.Tempos) Add(value.Id, new(project.Id, Tick: value.Tick));
        foreach (TimeSignatureChange value in project.Conductor.TimeSignatures) Add(value.Id, new(project.Id, Tick: value.Tick));
        foreach (KeySignatureChange value in project.Conductor.KeySignatures) Add(value.Id, new(project.Id, Tick: value.Tick));
        foreach (ProjectMarker marker in project.Conductor.Markers) Add(marker.Id, new(project.Id, Tick: marker.Tick));
        foreach (EventInstrumentLibraryFolder folder in project.EventInstrumentFolders)
        {
            Add(folder.Id, new(project.Id));
        }
        foreach (EventInstrument instrument in project.EventInstruments)
        {
            SourceReference instrumentSource = new(project.Id, EventInstrumentId: instrument.Id);
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
            SourceReference trackSource = new(project.Id, TrackId: track.Id);
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
            if (id == default || !ids.Add(id))
            {
                AddError("MIDORA1003", "正式对象 Stable ID 为空或在 Project 内重复。", source, diagnostics);
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
        bool invalid = target.Kind switch
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

    private static void ValidateMappings(MappingChain steps, SourceReference source, List<CompilerDiagnostic> diagnostics)
    {
        foreach (ValueMappingStep step in ActiveSteps(steps))
        {
            if (!double.IsFinite(step.Constant) || !double.IsFinite(step.SourceMinimum)
                || !double.IsFinite(step.SourceMaximum) || !double.IsFinite(step.TargetMinimum)
                || !double.IsFinite(step.TargetMaximum) || step.SourceMaximum < step.SourceMinimum
                || step.TargetMaximum < step.TargetMinimum)
            {
                AddError("MIDORA1270", "Mapping step 的数值范围非法。", source, diagnostics);
            }
            if (step.Operation == MappingOperation.CustomCSharp && !step.MappingFunctionId.HasValue)
            {
                AddError("MIDORA1271", "C# Mapping step 缺少 Mapping Function 引用。", source, diagnostics);
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
            if (step.Source == MappingSource.LogicalParameter
                && (!step.LogicalParameterId.HasValue || !parameters.ContainsKey(step.LogicalParameterId.Value)))
            {
                AddError("MIDORA1232", "Mapping step 引用了未知 Logical Parameter ID。", source, diagnostics);
            }
            if (step.Source == MappingSource.Envelope
                && (!step.EnvelopeId.HasValue || !envelopes.Contains(step.EnvelopeId.Value)))
            {
                AddError("MIDORA1233", "Mapping step 引用了未知 Envelope Preset ID。", source, diagnostics);
            }
            if (step.Operation == MappingOperation.CustomCSharp
                && (!step.MappingFunctionId.HasValue || !functions.ContainsKey(step.MappingFunctionId.Value)))
            {
                AddError("MIDORA1234", "Mapping step 引用了未知 Mapping Function ID。", source, diagnostics);
            }
        }
    }

    private static void AddError(string code, string message, SourceReference source, List<CompilerDiagnostic> diagnostics) =>
        diagnostics.Add(new(code, DiagnosticSeverity.Error, message, source));

    private static bool IsPerNoteStep(ValueMappingStep step) => step.Source is
        MappingSource.TriggerNote or MappingSource.TriggerVelocity or MappingSource.GateLength or MappingSource.PitchDelta;

    private static bool IsPerNoteContextField(string field) => field is
        nameof(MappingContext.TriggerNote) or nameof(MappingContext.TriggerVelocity)
        or nameof(MappingContext.EffectiveRootNote) or nameof(MappingContext.PitchDelta)
        or nameof(MappingContext.GateLength) or nameof(MappingContext.SegmentLocalTick);

    private static readonly HashSet<string> ValidMappingContextFields = new(StringComparer.Ordinal)
    {
        nameof(MappingContext.CurrentValue),
        nameof(MappingContext.TriggerNote),
        nameof(MappingContext.TriggerVelocity),
        nameof(MappingContext.GateLength),
        nameof(MappingContext.PitchDelta),
        nameof(MappingContext.TemplateTick),
        nameof(MappingContext.ProjectTick),
        nameof(MappingContext.TemplateNote),
        nameof(MappingContext.TemplateVelocity),
        nameof(MappingContext.EffectiveRootNote),
        nameof(MappingContext.CurrentEventId),
        nameof(MappingContext.CurrentParameter),
        nameof(MappingContext.CurrentEventKind),
        nameof(MappingContext.LogicalParameterId),
        nameof(MappingContext.LogicalParameterName),
        nameof(MappingContext.LogicalParameterValue),
        nameof(MappingContext.TargetOriginalValue),
        nameof(MappingContext.SegmentLocalTick),
        nameof(MappingContext.TrackId),
        nameof(MappingContext.SegmentId),
        nameof(MappingContext.SubVoiceId),
        nameof(MappingContext.SubVoiceName),
        nameof(MappingContext.SubVoiceIndex),
        nameof(MappingContext.SubVoiceEffectiveRootNote),
        nameof(MappingContext.EventInstrumentId),
        nameof(MappingContext.EventInstrumentName),
        nameof(MappingContext.EventInstrumentRootNote)
    };
}
