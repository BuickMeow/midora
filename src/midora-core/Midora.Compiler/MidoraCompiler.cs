using Midora.Domain;
using Midora.Mapping.Contract.V1;
using Midora.Midi;

namespace Midora.Compiler;

public sealed class MidoraCompiler : IDisposable
{
    private readonly Dictionary<MidoraId, TrackCacheEntry> _trackCache = [];
    private readonly MappingEngine _mapping = new();
    private MidoraProject? _cacheOwner;
    private bool _disposed;

    public CompilerRunTelemetry LastTelemetry { get; private set; }

    public CanonicalCompiledResult CompileFull(MidoraProject project, CompilationRequest? request = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        request ??= new CompilationRequest();
        return CompileCore(project, request, false, ProjectChangeSet.Everything);
    }

    public CanonicalCompiledResult CompileIncremental(
        MidoraProject project,
        ProjectChangeSet changes,
        CompilationRequest? request = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(changes);
        request ??= new CompilationRequest();
        return CompileCore(project, request, true, changes);
    }

    public void ClearCache()
    {
        _trackCache.Clear();
        _mapping.ClearCache();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        ClearCache();
        _mapping.Dispose();
        _cacheOwner = null;
        _disposed = true;
    }

    private CanonicalCompiledResult CompileCore(
        MidoraProject project,
        CompilationRequest request,
        bool incremental,
        ProjectChangeSet changes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ReferenceEquals(_cacheOwner, project))
        {
            ClearCache();
            _cacheOwner = project;
        }
        _mapping.SynchronizeFunctions(project.EventInstruments.SelectMany(instrument => instrument.MappingFunctions));
        LastTelemetry = default;
        List<CompilerDiagnostic> diagnostics = SemanticValidator.Validate(project, request);
        ValidateMappingFunctions(project, request, diagnostics);
        long naturalEnd = GetNaturalEnd(project, request.IncludedTrackIds);
        long endTick = request.EndTick ?? project.Conductor.EndMarkerTick ?? naturalEnd;
        if (endTick < request.StartTick)
        {
            diagnostics.Add(new("MIDORA2001", DiagnosticSeverity.Error,
                "有效编译结束 tick 早于范围起点。", new(Tick: endTick)));
            endTick = request.StartTick;
        }

        CanonicalConductor conductor = FreezeConductor(project.Conductor, request.StartTick, endTick);
        if (diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            return Failure(project, request, endTick, conductor, diagnostics);
        }

        Dictionary<MidoraId, EventInstrument> instruments = project.EventInstruments.ToDictionary(value => value.Id);
        List<RawInstance> instances = [];
        int recompiledTracks = 0;
        int reusedTracks = 0;
        IReadOnlyList<LogicalTrack> selectedTracks = project.Tracks
            .Where(track => request.IncludedTrackIds is null || request.IncludedTrackIds.Contains(track.Id))
            .ToArray();

        HashSet<MidoraId> liveTrackIds = project.Tracks.Select(track => track.Id).ToHashSet();
        foreach (MidoraId cachedId in _trackCache.Keys.Where(id => !liveTrackIds.Contains(id)).ToArray())
        {
            _trackCache.Remove(cachedId);
        }

        foreach (LogicalTrack track in selectedTracks)
        {
            long fingerprint = SourceFingerprint.ForTrack(track, instruments, project);
            bool explicitlyDirty = changes.AffectsEverything || changes.TrackIds.Contains(track.Id)
                || changes.EventInstrumentIds.Any(id => TrackReferences(track, id));
            if (incremental && !explicitlyDirty && _trackCache.TryGetValue(track.Id, out TrackCacheEntry? cached)
                && cached.Fingerprint == fingerprint)
            {
                instances.AddRange(cached.Instances);
                diagnostics.AddRange(cached.Diagnostics);
                reusedTracks++;
                continue;
            }

            int diagnosticStart = diagnostics.Count;
            RawInstance[] compiledTrack = ExpandTrack(project, track, instruments, diagnostics);
            CompilerDiagnostic[] trackDiagnostics = diagnostics.Skip(diagnosticStart).ToArray();
            _trackCache[track.Id] = new TrackCacheEntry(fingerprint, compiledTrack, trackDiagnostics);
            instances.AddRange(compiledTrack);
            recompiledTracks++;
        }

        if (!request.EndTick.HasValue && !project.Conductor.EndMarkerTick.HasValue)
        {
            endTick = instances
                .Select(value => value.EndTick)
                .DefaultIfEmpty(0)
                .Max();
            if (endTick < request.StartTick)
            {
                diagnostics.Add(new(
                    "MIDORA2001",
                    DiagnosticSeverity.Error,
                    "有效编译结束 tick 早于范围起点。",
                    new(Tick: endTick)));
                endTick = request.StartTick;
            }
            conductor = FreezeConductor(project.Conductor, request.StartTick, endTick);
        }

        if (request.IncludedSubVoiceIds is not null)
        {
            instances = instances.Select(instance => instance with
            {
                Voices = instance.Voices
                        .Where(voice => request.IncludedSubVoiceIds.Contains(voice.SubVoiceId))
                        .ToArray()
            })
                .Where(instance => instance.Voices.Length != 0)
                .ToList();
        }

        ValidateOverlap(project, instances, diagnostics);
        AllocationResult allocation = Allocate(project, instances, diagnostics);
        bool warningsFail = request.TreatWarningsAsErrors
            && diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning);
        bool errors = diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if (errors || warningsFail)
        {
            LastTelemetry = new(recompiledTracks, reusedTracks);
            return new CanonicalCompiledResult(
                project.TicksPerQuarterNote, request.StartTick, endTick,
                [], conductor, [], diagnostics.ToArray(), request.Purpose,
                false, false, 0,
                new(selectedTracks.Count, instances.Count, 0, allocation.PeakUnits));
        }

        List<CanonicalMidiEvent> allEvents = MaterializeEvents(instances, allocation.UnitBySubVoice);
        CanonicalMidiEvent[] ranged = ApplyRange(
            allEvents, allocation.Allocations, request.StartTick, endTick, project.GlobalResetDefaults);
        ChannelUnitAllocation[] rangedAllocations = allocation.Allocations
            .Where(value => value.StartTick < endTick && value.EndTick > request.StartTick)
            .ToArray();
        if (allocation.PeakUnits >= 248)
        {
            diagnostics.Add(new("MIDORA2250", DiagnosticSeverity.Info,
                $"Channel Unit 峰值为 {allocation.PeakUnits}/256。", new()));
        }

        long resultFingerprint = SourceFingerprint.ForResult(
            request.StartTick, endTick, ranged, conductor);
        LastTelemetry = new(recompiledTracks, reusedTracks);
        return new CanonicalCompiledResult(
            project.TicksPerQuarterNote, request.StartTick, endTick,
            ranged, conductor, rangedAllocations, diagnostics.ToArray(), request.Purpose,
            false, true, resultFingerprint,
            new(selectedTracks.Count, instances.Count, ranged.Length, allocation.PeakUnits));
    }

    private static CanonicalCompiledResult Failure(
        MidoraProject project,
        CompilationRequest request,
        long endTick,
        CanonicalConductor conductor,
        List<CompilerDiagnostic> diagnostics) => new(
            project.TicksPerQuarterNote, request.StartTick, endTick,
            [], conductor, [], diagnostics.ToArray(), request.Purpose,
            false, false, 0,
            new(project.Tracks.Count, 0, 0, 0));

    private void ValidateMappingFunctions(
        MidoraProject project,
        CompilationRequest request,
        List<CompilerDiagnostic> diagnostics)
    {
        HashSet<MidoraId> selectedInstrumentIds = SemanticValidator.GetParticipatingInstrumentIds(project, request);
        foreach (EventInstrument instrument in project.EventInstruments)
        {
            if (request.IncludedTrackIds is not null
                && !selectedInstrumentIds.Contains(instrument.Id))
            {
                continue;
            }
            HashSet<MidoraId> activeReferences = GetActiveMappingSteps(instrument)
                .Where(step => step.Operation == MappingOperation.CustomCSharp && step.MappingFunctionId.HasValue)
                .Select(step => step.MappingFunctionId!.Value)
                .ToHashSet();
            foreach (CSharpMappingFunction function in instrument.MappingFunctions)
            {
                string? error = _mapping.ValidateFunction(function);
                if (error is null)
                {
                    continue;
                }
                bool participates = selectedInstrumentIds.Contains(instrument.Id)
                    && activeReferences.Contains(function.Id);
                diagnostics.Add(new(
                    participates ? "MIDORA2103" : "MIDORA2104",
                    participates ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                    error,
                    new(EventInstrumentId: instrument.Id)));
            }
        }
    }

    private static IEnumerable<ValueMappingStep> GetActiveMappingSteps(EventInstrument instrument) =>
        instrument.ParameterMappings.SelectMany(value => ActiveSteps(value.Steps))
            .Concat(instrument.SubVoices.SelectMany(value => value.Events)
                .SelectMany(value => ActiveSteps(value.NumberMappings)
                    .Concat(ActiveSteps(value.ValueMappings))
                    .Concat(ActiveSteps(value.SecondaryValueMappings))));

    private RawInstance[] ExpandTrack(
        MidoraProject project,
        LogicalTrack track,
        IReadOnlyDictionary<MidoraId, EventInstrument> instruments,
        List<CompilerDiagnostic> diagnostics)
    {
        List<RawInstance> result = [];
        if (!track.EventInstrumentId.HasValue
            || !instruments.TryGetValue(track.EventInstrumentId.Value, out EventInstrument? instrument))
        {
            return [];
        }
        Dictionary<MidoraId, LogicalParameterDefinition> definitions = instrument.LogicalParameters
            .ToDictionary(value => value.Id);
        Dictionary<MidoraId, CSharpMappingFunction> functions = instrument.MappingFunctions
            .ToDictionary(value => value.Id);
        int sourceOrder = 0;
        List<AcceptedInstance> activePolicyInstances = [];
        foreach (Segment segment in track.Segments.OrderBy(value => value.ProjectStartTick))
        {
            Dictionary<MidoraId, LogicalParameterLane> lanes = segment.ParameterLanes
                .Where(value => definitions.ContainsKey(value.ParameterId))
                .ToDictionary(value => value.ParameterId);
            foreach (LogicalNote note in segment.Notes.OrderBy(value => value.StartTick))
            {
                if (note.StartTick < segment.ContentOffsetTick || note.StartTick >= segment.ContentEndTick)
                {
                    continue;
                }

                long projectStart = checked(segment.ProjectStartTick + note.StartTick - segment.ContentOffsetTick);
                long segmentEnd = checked(segment.ProjectStartTick + segment.LengthTicks);
                long gateEnd = Math.Min(checked(projectStart + note.LengthTicks), segmentEnd);
                Dictionary<MidoraId, double> parameterValues = EvaluateParameters(definitions, lanes, note.StartTick);
                int instanceSourceOrder = sourceOrder++;
                activePolicyInstances.RemoveAll(value => value.Instance.EndTick <= projectStart);
                AcceptedInstance[] conflicts = activePolicyInstances.Where(value =>
                    value.Instance.EndTick > projectStart
                    && (instrument.OverlapScope == OverlapScope.AnyPitch || value.Note.Note == note.Note)).ToArray();
                if (conflicts.Length != 0 && instrument.OverlapPolicy == OverlapPolicy.CutNewRejectNew)
                {
                    diagnostics.Add(new("MIDORA2203", DiagnosticSeverity.Warning,
                        "新实例与活动实例重叠，按 Cut New / Reject New 策略未生成。",
                        new(track.Id, segment.Id, note.Id, instrument.Id, Tick: projectStart)));
                    continue;
                }
                if (conflicts.Length != 0 && instrument.OverlapPolicy == OverlapPolicy.CutPrevious)
                {
                    if (conflicts.Any(value => value.ProjectStartTick == projectStart))
                    {
                        diagnostics.Add(new("MIDORA2204", DiagnosticSeverity.Error,
                            "Cut Previous 下同 tick 的多个实例没有确定的先后截断语义。",
                            new(track.Id, segment.Id, note.Id, instrument.Id, Tick: projectStart)));
                    }
                    else
                    {
                        foreach (AcceptedInstance previous in conflicts)
                        {
                            Dictionary<MidoraId, double> previousParameters = EvaluateParameters(
                                definitions, lanes, previous.Note.StartTick);
                            RawInstance shortened = ExpandInstance(
                                project, track, previous.Segment, previous.Note, instrument,
                                previous.ProjectStartTick, projectStart, previous.SegmentEndTick,
                                previousParameters, definitions, lanes, functions,
                                previous.SourceOrder, diagnostics);
                            result[previous.ResultIndex] = shortened;
                            previous.Instance = shortened;
                            activePolicyInstances.Remove(previous);
                        }
                    }
                }
                RawInstance instance = ExpandInstance(
                    project, track, segment, note, instrument, projectStart, gateEnd, segmentEnd,
                    parameterValues, definitions, lanes, functions, instanceSourceOrder, diagnostics);
                result.Add(instance);
                activePolicyInstances.Add(new(
                    result.Count - 1, segment, note, projectStart, segmentEnd,
                    instanceSourceOrder, instance));
            }
        }
        if (result.Count != 0)
        {
            foreach (SubVoice emptyVoice in instrument.SubVoices.Where(voice =>
                voice.Events.Count == 0
                && voice.Curves.Count == 0
                && !instrument.ParameterMappings.Any(mapping => mapping.Steps.IsEnabled)))
            {
                diagnostics.Add(new(
                    "MIDORA1225",
                    DiagnosticSeverity.Info,
                    "实际参与编译的 SubVoice 没有普通 MIDI 输出内容。",
                    new(track.Id, EventInstrumentId: instrument.Id, SubVoiceId: emptyVoice.Id)));
            }
        }
        return result.ToArray();
    }

    private RawInstance ExpandInstance(
        MidoraProject project,
        LogicalTrack track,
        Segment segment,
        LogicalNote note,
        EventInstrument instrument,
        long projectStart,
        long gateEnd,
        long segmentEnd,
        Dictionary<MidoraId, double> initialParameters,
        IReadOnlyDictionary<MidoraId, LogicalParameterDefinition> definitions,
        IReadOnlyDictionary<MidoraId, LogicalParameterLane> lanes,
        IReadOnlyDictionary<MidoraId, CSharpMappingFunction> functions,
        int sourceOrder,
        List<CompilerDiagnostic> diagnostics)
    {
        long gateLength = gateEnd - projectStart;
        bool shortNote = gateLength < instrument.TemplateLengthTicks;
        HashSet<MidoraId> usedEnvelopeIds = GetUsedEnvelopeIds(instrument);
        long release = instrument.Envelopes.Where(value => usedEnvelopeIds.Contains(value.Id))
            .Select(value => value.ReleaseTicks).DefaultIfEmpty().Max();
        bool releaseTriggered = shortNote
            ? instrument.ShortLifecycle != ShortNoteLifecycle.OneShot
            : instrument.LongLifecycle != LongNoteLifecycle.EndAtTemplate;
        long? releaseStartLocalTick = releaseTriggered ? gateLength : null;
        long naturalDuration;
        if (shortNote)
        {
            naturalDuration = instrument.ShortLifecycle switch
            {
                ShortNoteLifecycle.CutAtNoteOff => checked(gateLength + release),
                ShortNoteLifecycle.OneShot => instrument.TemplateLengthTicks,
                ShortNoteLifecycle.Tail => Math.Max(instrument.TemplateLengthTicks, checked(gateLength + release)),
                _ => gateLength
            };
        }
        else if (instrument.LongLifecycle == LongNoteLifecycle.EndAtTemplate)
        {
            naturalDuration = instrument.TemplateLengthTicks;
        }
        else
        {
            long loopTailLength = instrument.LoopEndTick.HasValue
                ? instrument.TemplateLengthTicks - instrument.LoopEndTick.Value
                : 0;
            naturalDuration = checked(gateLength + Math.Max(release, loopTailLength));
        }
        long actualEnd = Math.Min(checked(projectStart + naturalDuration), segmentEnd);
        if (actualEnd < projectStart)
        {
            actualEnd = projectStart;
        }

        RawSubVoice[] voices = new RawSubVoice[instrument.SubVoices.Count];
        long sequence = ((long)sourceOrder) << 32;
        for (int voiceIndex = 0; voiceIndex < instrument.SubVoices.Count; voiceIndex++)
        {
            SubVoice voice = instrument.SubVoices[voiceIndex];
            SourceReference source = new(track.Id, segment.Id, note.Id, instrument.Id, voice.Id, Tick: projectStart);
            List<RawMidiEvent> events = [];
            MidiInitialState state = MergeState(project.GlobalInitialState, instrument.InitialState, voice.InitialState);
            HashSet<MidiValueTarget> tickZeroTargets = GetTickZeroTargets(voice);
            EmitInitialState(events, projectStart, state, tickZeroTargets, source, ref sequence);
            int root = voice.RootNoteOverride ?? instrument.RootNote;
            int pitchDelta = note.Note - root;
            EmitVoiceCurves(events, instrument, voice, projectStart, gateLength, actualEnd, source, ref sequence);
            foreach (TemplateEvent templateEvent in voice.Events.OrderBy(value => value.Tick))
            {
                foreach (EventOccurrence occurrence in EnumerateOccurrences(instrument, templateEvent.Tick, gateLength, shortNote))
                {
                    long tick = checked(projectStart + occurrence.LocalTick);
                    bool afterGate = tick >= gateEnd;
                    if (tick >= actualEnd
                        || (shortNote && instrument.ShortLifecycle == ShortNoteLifecycle.CutAtNoteOff && afterGate)
                        || (releaseTriggered && afterGate && templateEvent.Kind == TemplateEventKind.Note))
                    {
                        continue;
                    }
                    Dictionary<MidoraId, double> parametersAtTick = EvaluateParameters(
                        definitions, lanes, checked(segment.ContentOffsetTick + tick - segment.ProjectStartTick));
                    Dictionary<MidoraId, double> envelopesAtTick = EvaluateEnvelopes(
                        instrument, occurrence.LocalTick, releaseStartLocalTick, usedEnvelopeIds);
                    MappingContextV1 context = new(
                        templateEvent.Value, note.Note, note.Velocity, gateLength, pitchDelta,
                        occurrence.TemplateTick, tick,
                        templateEvent.Kind == TemplateEventKind.Note ? templateEvent.Number : 0,
                        templateEvent.Kind == TemplateEventKind.Note ? templateEvent.Value : 0)
                    {
                        EffectiveRootNote = root,
                        CurrentEventId = ToMappingId(templateEvent.Id),
                        CurrentEventKind = ToMappingEventKind(templateEvent.Kind),
                        SegmentLocalTick = checked(segment.ContentOffsetTick + tick - segment.ProjectStartTick),
                        TrackId = ToMappingId(track.Id),
                        SegmentId = ToMappingId(segment.Id),
                        SubVoiceId = ToMappingId(voice.Id),
                        SubVoiceName = voice.Name,
                        SubVoiceIndex = voiceIndex,
                        SubVoiceEffectiveRootNote = root,
                        EventInstrumentId = ToMappingId(instrument.Id),
                        EventInstrumentName = instrument.Name,
                        EventInstrumentRootNote = instrument.RootNote
                    };
                    try
                    {
                        EmitTemplateEvent(events, templateEvent, tick, gateEnd, actualEnd,
                            releaseTriggered, pitchDelta, note.Velocity,
                            context, parametersAtTick, envelopesAtTick, functions,
                            source with { SourceEventId = templateEvent.Id, Tick = tick }, ref sequence);
                    }
                    catch (Exception exception) when (exception is MappingException or OverflowException)
                    {
                        diagnostics.Add(new("MIDORA2101", DiagnosticSeverity.Error,
                            exception.Message, source with { SourceEventId = templateEvent.Id, Tick = tick }));
                    }
                }
            }

            EmitParameterMappings(events, instrument, segment, projectStart, actualEnd, note, pitchDelta,
                gateLength, releaseStartLocalTick, usedEnvelopeIds, definitions, lanes, initialParameters,
                functions, source, ref sequence, diagnostics);
            HashSet<MidiValueTarget> usedTargets = CollectUsedTargets(instrument, voice, state);
            EmitReset(events, actualEnd, usedTargets, project.GlobalResetDefaults,
                source with { Tick = actualEnd }, ref sequence);
            voices[voiceIndex] = new RawSubVoice(voice.Id, events.ToArray());
        }

        return new RawInstance(
            note.Id, track.Id, segment.Id, instrument.Id, note.Note, projectStart, actualEnd,
            instrument.RequiresChannelIsolation,
            instrument.OverlapPolicy, instrument.OverlapScope,
            sourceOrder, voices);
    }

    private void EmitTemplateEvent(
        List<RawMidiEvent> output,
        TemplateEvent value,
        long tick,
        long gateEnd,
        long actualEnd,
        bool releaseTriggered,
        int pitchDelta,
        int triggerVelocity,
        MappingContextV1 context,
        IReadOnlyDictionary<MidoraId, double> parameters,
        IReadOnlyDictionary<MidoraId, double> envelopes,
        IReadOnlyDictionary<MidoraId, CSharpMappingFunction> functions,
        SourceReference source,
        ref long sequence)
    {
        int number = ApplyMappedInt(value.Number, value.NumberMappings, value.NumberTargetSettings,
            context with { CurrentParameter = MappingTargetParameterV1.Number, TargetOriginalValue = value.Number },
            parameters, envelopes, functions, 0, 127, value.Number, false);
        int eventValue = ApplyMappedInt(value.Value, value.ValueMappings, value.ValueTargetSettings,
            context with { CurrentParameter = MappingTargetParameterV1.Value, TargetOriginalValue = value.Value },
            parameters, envelopes, functions,
            value.Kind switch
            {
                TemplateEventKind.PitchBend => -8192,
                TemplateEventKind.Note => 1,
                _ => 0
            },
            value.Kind is TemplateEventKind.RegisteredParameter or TemplateEventKind.NonRegisteredParameter ? 16383 :
            value.Kind == TemplateEventKind.PitchBend ? 8191 : 127,
            DefaultTemplateValue(value.Kind), true);
        int secondary = ApplyMappedInt(value.SecondaryValue, value.SecondaryValueMappings, value.SecondaryValueTargetSettings,
            context with { CurrentParameter = MappingTargetParameterV1.SecondaryValue, TargetOriginalValue = value.SecondaryValue },
            parameters, envelopes, functions, 0,
            value.Kind == TemplateEventKind.PitchBendRange ? 99 : 127,
            value.Kind == TemplateEventKind.PitchBendRange ? 0 : value.SecondaryValue, true);
        switch (value.Kind)
        {
            case TemplateEventKind.Note:
                number = value.FollowPitchDelta ? checked(number + pitchDelta) : number;
                if (number is < 0 or > 127)
                {
                    throw new MappingException("Transposed Note number is outside 0–127; Note number cannot clamp.");
                }
                output.Add(RawMidiEvent.NoteOn(tick, number, eventValue, sequence++, source));
                long naturalOffTick = checked(tick + value.LengthTicks);
                long offTick = releaseTriggered && naturalOffTick > gateEnd && actualEnd > gateEnd
                    ? actualEnd
                    : Math.Min(naturalOffTick, actualEnd);
                output.Add(RawMidiEvent.NoteOff(offTick, number, sequence++, source with { Tick = offTick }));
                break;
            case TemplateEventKind.ControlChange:
                output.Add(RawMidiEvent.Control(tick, number, eventValue, CanonicalEventRole.ControlChange, sequence++, source));
                break;
            case TemplateEventKind.Bank:
                if (value.HasBankMsb)
                {
                    output.Add(RawMidiEvent.Control(tick, 0, eventValue, CanonicalEventRole.Bank, sequence++, source));
                }
                if (value.HasBankLsb)
                {
                    output.Add(RawMidiEvent.Control(tick, 32, secondary, CanonicalEventRole.Bank, sequence++, source));
                }
                break;
            case TemplateEventKind.Program:
                output.Add(RawMidiEvent.Program(tick, eventValue, sequence++, source));
                break;
            case TemplateEventKind.PitchBend:
                output.Add(RawMidiEvent.PitchBend(tick, eventValue, sequence++, source));
                break;
            case TemplateEventKind.RegisteredParameter:
                EmitParameter(output, tick, true, number, eventValue, source, ref sequence);
                break;
            case TemplateEventKind.NonRegisteredParameter:
                EmitParameter(output, tick, false, number, eventValue, source, ref sequence);
                break;
            case TemplateEventKind.PitchBendRange:
                EmitPitchBendRange(output, tick, eventValue, Math.Clamp(secondary, 0, 99), source, ref sequence);
                break;
        }
    }

    private int ApplyMappedInt(
        int value,
        MappingChain steps,
        MidiIntegerTargetSettings targetSettings,
        in MappingContextV1 context,
        IReadOnlyDictionary<MidoraId, double> parameters,
        IReadOnlyDictionary<MidoraId, double> envelopes,
        IReadOnlyDictionary<MidoraId, CSharpMappingFunction> functions,
        int minimum,
        int maximum,
        int targetDefault,
        bool allowClamp)
    {
        if (!steps.IsEnabled || steps.Count == 0 || !steps.Any(item => item.IsEnabled))
        {
            return value;
        }
        double result = _mapping.Apply(
            value, steps, context, parameters, envelopes, functions,
            minimum, maximum, targetDefault, targetSettings.Overflow, allowClamp);
        return MappingEngine.Round(result, targetSettings.Rounding);
    }

    private static int DefaultTemplateValue(TemplateEventKind kind) => kind switch
    {
        TemplateEventKind.PitchBendRange => 2,
        _ => 0
    };

    private static IEnumerable<EventOccurrence> EnumerateOccurrences(
        EventInstrument instrument,
        long eventTick,
        long gateLength,
        bool shortNote)
    {
        if (shortNote || !instrument.LoopStartTick.HasValue || !instrument.LoopEndTick.HasValue
            || gateLength <= instrument.TemplateLengthTicks)
        {
            yield return new(eventTick, eventTick);
            yield break;
        }
        long loopStart = instrument.LoopStartTick.Value;
        long loopEnd = instrument.LoopEndTick.Value;
        if (eventTick < loopStart)
        {
            yield return new(eventTick, eventTick);
            yield break;
        }
        if (eventTick >= loopEnd)
        {
            yield return new(checked(gateLength + eventTick - loopEnd), eventTick);
            yield break;
        }
        long loopLength = loopEnd - loopStart;
        long relative = eventTick - loopStart;
        for (long iterationStart = loopStart; iterationStart + relative < gateLength; iterationStart = checked(iterationStart + loopLength))
        {
            yield return new(checked(iterationStart + relative), eventTick);
        }
    }

    private static void EmitInitialState(
        List<RawMidiEvent> output,
        long tick,
        MidiInitialState state,
        IReadOnlySet<MidiValueTarget> suppressedTargets,
        SourceReference source,
        ref long sequence)
    {
        if (!suppressedTargets.Contains(MidiValueTarget.BankMsb))
        {
            output.Add(RawMidiEvent.Control(tick, 0, state.BankMsb ?? 0, CanonicalEventRole.InitialState, sequence++, source));
        }
        if (!suppressedTargets.Contains(MidiValueTarget.BankLsb))
        {
            output.Add(RawMidiEvent.Control(tick, 32, state.BankLsb ?? 0, CanonicalEventRole.InitialState, sequence++, source));
        }
        if (!suppressedTargets.Contains(MidiValueTarget.Program))
        {
            output.Add(RawMidiEvent.Program(tick, state.Program ?? 0, sequence++, source, CanonicalEventRole.InitialState));
        }
        bool suppressSemitones = suppressedTargets.Contains(MidiValueTarget.PitchBendRangeSemitones);
        bool suppressCents = suppressedTargets.Contains(MidiValueTarget.PitchBendRangeCents);
        if (!suppressSemitones && !suppressCents)
        {
            EmitPitchBendRange(output, tick, state.PitchBendRangeSemitones ?? 2, state.PitchBendRangeCents ?? 0, source, ref sequence, CanonicalEventRole.InitialState);
        }
        else
        {
            if (!suppressSemitones)
            {
                EmitPitchBendRangeComponent(output, tick, true, state.PitchBendRangeSemitones ?? 2,
                    source, ref sequence, CanonicalEventRole.InitialState);
            }
            if (!suppressCents)
            {
                EmitPitchBendRangeComponent(output, tick, false, state.PitchBendRangeCents ?? 0,
                    source, ref sequence, CanonicalEventRole.InitialState);
            }
        }
        foreach ((int controller, int value) in state.Controllers.OrderBy(value => value.Key))
        {
            if (!suppressedTargets.Contains(MidiValueTarget.ControlChange(controller)))
            {
                output.Add(RawMidiEvent.Control(tick, controller, value, CanonicalEventRole.InitialState, sequence++, source));
            }
        }
        foreach ((int parameter, int value) in state.RegisteredParameters.OrderBy(value => value.Key))
        {
            if (!suppressedTargets.Contains(MidiValueTarget.Rpn(parameter)))
            {
                EmitParameter(output, tick, true, parameter, value, source, ref sequence, CanonicalEventRole.InitialState);
            }
        }
        foreach ((int parameter, int value) in state.NonRegisteredParameters.OrderBy(value => value.Key))
        {
            if (!suppressedTargets.Contains(MidiValueTarget.Nrpn(parameter)))
            {
                EmitParameter(output, tick, false, parameter, value, source, ref sequence, CanonicalEventRole.InitialState);
            }
        }
        if (!suppressedTargets.Contains(MidiValueTarget.PitchBend))
        {
            output.Add(RawMidiEvent.PitchBend(tick, state.PitchBend ?? 0, sequence++, source, CanonicalEventRole.InitialState));
        }
    }

    private static MidiInitialState MergeState(params MidiInitialState[] states)
    {
        MidiInitialState result = new();
        foreach (MidiInitialState state in states)
        {
            result.BankMsb = state.BankMsb ?? result.BankMsb;
            result.BankLsb = state.BankLsb ?? result.BankLsb;
            result.Program = state.Program ?? result.Program;
            result.PitchBend = state.PitchBend ?? result.PitchBend;
            result.PitchBendRangeSemitones = state.PitchBendRangeSemitones ?? result.PitchBendRangeSemitones;
            result.PitchBendRangeCents = state.PitchBendRangeCents ?? result.PitchBendRangeCents;
            foreach ((int controller, int value) in state.Controllers)
            {
                result.Controllers[controller] = value;
            }
            foreach ((int parameter, int value) in state.RegisteredParameters)
            {
                result.RegisteredParameters[parameter] = value;
            }
            foreach ((int parameter, int value) in state.NonRegisteredParameters)
            {
                result.NonRegisteredParameters[parameter] = value;
            }
        }
        return result;
    }

    private static void EmitVoiceCurves(
        List<RawMidiEvent> output,
        EventInstrument instrument,
        SubVoice voice,
        long projectStart,
        long gateLength,
        long actualEnd,
        SourceReference source,
        ref long sequence)
    {
        foreach (ValueCurve curve in voice.Curves.OrderBy(value => value.Target.Kind).ThenBy(value => value.Target.Number))
        {
            int? previousOutputValue = null;
            for (long localTick = 0; projectStart + localTick < actualEnd; localTick++)
            {
                long templateTick = MapLongTickToTemplate(instrument, localTick, gateLength);
                if (templateTick < 0 || templateTick >= instrument.TemplateLengthTicks)
                {
                    continue;
                }
                double value = EvaluateCurve(curve.Points, templateTick, 0);
                int normalized = NormalizeTargetValue(curve.Target, value, curve.TargetSettings);
                if (previousOutputValue == normalized)
                {
                    continue;
                }
                previousOutputValue = normalized;
                EmitTarget(output, projectStart + localTick, curve.Target, normalized,
                    source with { Tick = projectStart + localTick }, ref sequence);
            }
        }
    }

    private void EmitParameterMappings(
        List<RawMidiEvent> output,
        EventInstrument instrument,
        Segment segment,
        long projectStart,
        long actualEnd,
        LogicalNote note,
        int pitchDelta,
        long gateLength,
        long? releaseStartLocalTick,
        IReadOnlySet<MidoraId> usedEnvelopeIds,
        IReadOnlyDictionary<MidoraId, LogicalParameterDefinition> definitions,
        IReadOnlyDictionary<MidoraId, LogicalParameterLane> lanes,
        IReadOnlyDictionary<MidoraId, double> initialParameters,
        IReadOnlyDictionary<MidoraId, CSharpMappingFunction> functions,
        SourceReference source,
        ref long sequence,
        List<CompilerDiagnostic> diagnostics)
    {
        _ = initialParameters;
        int targetVoiceIndex = instrument.SubVoices.FindIndex(voice => voice.Id == source.SubVoiceId);
        SubVoice targetVoice = instrument.SubVoices[targetVoiceIndex];
        int targetVoiceRoot = targetVoice.RootNoteOverride ?? instrument.RootNote;
        foreach (IGrouping<MidiValueTarget, LogicalParameterMapping> group in instrument.ParameterMappings
            .Where(value => value.Steps.IsEnabled && value.SubVoiceId == source.SubVoiceId)
            .GroupBy(value => value.Target)
            .OrderBy(value => value.Key.Kind).ThenBy(value => value.Key.Number))
        {
            LogicalParameterMapping[] mappings = group.ToArray();
            MidiIntegerTargetSettings targetSettings = mappings[0].TargetSettings;
            double baseValue = DefaultTargetValue(group.Key);
            TargetStatePoint[] rawState = BuildTargetStateTimeline(output, group.Key);
            int rawStateIndex = 0;
            double currentRawValue = baseValue;
            int? previousOutputValue = null;
            for (long tick = projectStart; tick < actualEnd; tick++)
            {
                while (rawStateIndex < rawState.Length && rawState[rawStateIndex].Tick <= tick)
                {
                    currentRawValue = rawState[rawStateIndex++].Value;
                }
                long contentTick = checked(segment.ContentOffsetTick + tick - segment.ProjectStartTick);
                Dictionary<MidoraId, double> parameters = EvaluateParameters(definitions, lanes, contentTick);
                Dictionary<MidoraId, double> envelopes = EvaluateEnvelopes(
                    instrument, tick - projectStart, releaseStartLocalTick, usedEnvelopeIds);
                double current = currentRawValue;
                try
                {
                    foreach (LogicalParameterMapping mapping in mappings)
                    {
                        double logical = parameters[mapping.ParameterId];
                        long instanceTick = tick - projectStart;
                        long templateTick = MapLongTickToTemplate(instrument, instanceTick, gateLength);
                        MappingContextV1 context = new(current, note.Note, note.Velocity, gateLength, pitchDelta,
                            templateTick, tick, 0, 0)
                        {
                            CurrentParameter = MappingTargetParameterV1.LogicalParameterOutput,
                            CurrentEventKind = ToMappingEventKind(group.Key.Kind),
                            LogicalParameterId = ToMappingId(mapping.ParameterId),
                            LogicalParameterName = definitions[mapping.ParameterId].Name,
                            LogicalParameterValue = logical,
                            TargetOriginalValue = currentRawValue,
                            SegmentLocalTick = contentTick,
                            TrackId = ToMappingId(source.TrackId),
                            SegmentId = ToMappingId(source.SegmentId),
                            SubVoiceId = ToMappingId(source.SubVoiceId),
                            SubVoiceName = targetVoice.Name,
                            SubVoiceIndex = targetVoiceIndex,
                            SubVoiceEffectiveRootNote = targetVoiceRoot,
                            EventInstrumentId = ToMappingId(instrument.Id),
                            EventInstrumentName = instrument.Name,
                            EventInstrumentRootNote = instrument.RootNote
                        };
                        IReadOnlyList<ValueMappingStep> steps = mapping.Steps;
                        current = steps.Count == 0 || !steps.Any(item => item.IsEnabled)
                            ? logical
                            : _mapping.Apply(current, steps, context, parameters, envelopes, functions,
                                TargetMinimum(group.Key), TargetMaximum(group.Key),
                                DefaultTargetValue(group.Key), mapping.TargetSettings.Overflow, true);
                    }
                    int normalized = NormalizeTargetValue(group.Key, current, targetSettings);
                    if (previousOutputValue != normalized)
                    {
                        previousOutputValue = normalized;
                        EmitTarget(output, tick, group.Key, normalized, source with { Tick = tick }, ref sequence,
                            CanonicalEventRole.LogicalParameter);
                    }
                }
                catch (Exception exception) when (exception is MappingException or OverflowException)
                {
                    diagnostics.Add(new("MIDORA2102", DiagnosticSeverity.Error, exception.Message, source with { Tick = tick }));
                    break;
                }
            }
        }
    }

    private static TargetStatePoint[] BuildTargetStateTimeline(
        IReadOnlyList<RawMidiEvent> events,
        MidiValueTarget target)
    {
        long targetKey = SemanticTargetForTarget(target);
        return events.Where(value => value.SemanticTargetKey == targetKey)
            .GroupBy(value => value.SemanticGroup)
            .Select(group => new
            {
                Tick = group.First().Tick,
                Role = group.Max(value => value.Role),
                Sequence = group.Max(value => value.Sequence),
                Value = DecodeTargetGroup(target, group)
            })
            .OrderBy(value => value.Tick)
            .ThenBy(value => value.Role)
            .ThenBy(value => value.Sequence)
            .Select(value => new TargetStatePoint(value.Tick, value.Value))
            .ToArray();
    }

    private static double DecodeTargetGroup(MidiValueTarget target, IEnumerable<RawMidiEvent> group) =>
        target.Kind switch
        {
            MidiValueKind.ControlChange or MidiValueKind.BankMsb or MidiValueKind.BankLsb =>
                group.Last(value => value.Kind == RawMessageKind.ControlChange).Data2,
            MidiValueKind.Program => group.Last(value => value.Kind == RawMessageKind.ProgramChange).Data1,
            MidiValueKind.PitchBend => group.Last(value => value.Kind == RawMessageKind.PitchBend).Data1,
            MidiValueKind.RegisteredParameter or MidiValueKind.NonRegisteredParameter =>
                (group.Last(value => value.Data1 == 6).Data2 << 7)
                | group.Last(value => value.Data1 == 38).Data2,
            MidiValueKind.PitchBendRangeSemitones => group.Last(value => value.Data1 == 6).Data2,
            MidiValueKind.PitchBendRangeCents => group.Last(value => value.Data1 == 38).Data2,
            _ => throw new InvalidOperationException($"Unsupported target {target}.")
        };

    private static HashSet<MidoraId> GetUsedEnvelopeIds(EventInstrument instrument)
    {
        HashSet<MidoraId> result = [];
        IEnumerable<ValueMappingStep> steps = instrument.ParameterMappings
            .Where(value => value.Steps.IsEnabled)
            .SelectMany(value => value.Steps.Where(step => step.IsEnabled))
            .Concat(instrument.SubVoices.SelectMany(value => value.Events)
                .SelectMany(value => ActiveSteps(value.NumberMappings).Concat(ActiveSteps(value.ValueMappings))
                    .Concat(ActiveSteps(value.SecondaryValueMappings))));
        foreach (ValueMappingStep step in steps)
        {
            if (step.Source == MappingSource.Envelope && step.EnvelopeId.HasValue)
            {
                result.Add(step.EnvelopeId.Value);
            }
        }
        return result;
    }

    private static IEnumerable<ValueMappingStep> ActiveSteps(MappingChain chain) =>
        chain.IsEnabled ? chain.Where(value => value.IsEnabled) : [];

    private static Dictionary<MidoraId, double> EvaluateEnvelopes(
        EventInstrument instrument,
        long localTick,
        long? releaseStartLocalTick,
        IReadOnlySet<MidoraId> usedEnvelopeIds)
    {
        Dictionary<MidoraId, double> result = new(usedEnvelopeIds.Count);
        foreach (InstrumentEnvelope envelope in instrument.Envelopes)
        {
            if (!usedEnvelopeIds.Contains(envelope.Id))
            {
                continue;
            }
            double value = !releaseStartLocalTick.HasValue || localTick < releaseStartLocalTick.Value
                ? EnvelopePreReleaseValue(envelope, localTick)
                : Interpolate(
                    EnvelopePreReleaseValue(envelope, releaseStartLocalTick.Value),
                    envelope.EndValue,
                    localTick - releaseStartLocalTick.Value,
                    envelope.ReleaseTicks);
            result.Add(envelope.Id, value);
        }
        return result;
    }

    private static double EnvelopePreReleaseValue(InstrumentEnvelope envelope, long tick)
    {
        if (tick < envelope.DelayTicks)
        {
            return envelope.StartValue;
        }
        tick -= envelope.DelayTicks;
        if (tick < envelope.AttackTicks)
        {
            return Interpolate(envelope.StartValue, envelope.PeakValue, tick, envelope.AttackTicks);
        }
        tick -= envelope.AttackTicks;
        if (tick < envelope.HoldTicks)
        {
            return envelope.PeakValue;
        }
        tick -= envelope.HoldTicks;
        if (tick < envelope.DecayTicks)
        {
            return Interpolate(envelope.PeakValue, envelope.SustainValue, tick, envelope.DecayTicks);
        }
        return envelope.SustainValue;
    }

    private static double Interpolate(double start, double end, long elapsed, long duration) =>
        duration <= 0 ? end : start + ((end - start) * Math.Clamp(elapsed / (double)duration, 0, 1));

    private static long MapLongTickToTemplate(EventInstrument instrument, long localTick, long gateLength)
    {
        if (!instrument.LoopStartTick.HasValue || !instrument.LoopEndTick.HasValue
            || gateLength <= instrument.TemplateLengthTicks || localTick < instrument.LoopStartTick.Value)
        {
            return localTick;
        }
        if (localTick >= gateLength)
        {
            return checked(instrument.LoopEndTick.Value + localTick - gateLength);
        }
        long loopLength = instrument.LoopEndTick.Value - instrument.LoopStartTick.Value;
        return instrument.LoopStartTick.Value + ((localTick - instrument.LoopStartTick.Value) % loopLength);
    }

    private static Dictionary<MidoraId, double> EvaluateParameters(
        IReadOnlyDictionary<MidoraId, LogicalParameterDefinition> definitions,
        IReadOnlyDictionary<MidoraId, LogicalParameterLane> lanes,
        long contentTick)
    {
        Dictionary<MidoraId, double> result = new(definitions.Count);
        foreach ((MidoraId id, LogicalParameterDefinition definition) in definitions)
        {
            double value = lanes.TryGetValue(id, out LogicalParameterLane? lane)
                ? EvaluateCurve(lane.Points, contentTick, definition.DefaultValue)
                : definition.DefaultValue;
            value = definition.Type switch
            {
                LogicalParameterType.Integer or LogicalParameterType.Enum =>
                    MappingEngine.Round(value, MappingRounding.Round),
                _ => value
            };
            result.Add(id, Math.Clamp(value, definition.Minimum, definition.Maximum));
        }
        return result;
    }

    private static double EvaluateCurve(IReadOnlyList<CurvePoint> unsorted, long tick, double defaultValue)
    {
        if (unsorted.Count == 0)
        {
            return defaultValue;
        }
        IReadOnlyList<CurvePoint> points = unsorted;
        for (int i = 1; i < unsorted.Count; i++)
        {
            if (unsorted[i].Tick < unsorted[i - 1].Tick)
            {
                points = unsorted.OrderBy(value => value.Tick).ToArray();
                break;
            }
        }
        if (tick < points[0].Tick)
        {
            return defaultValue;
        }
        int low = 0;
        int high = points.Count - 1;
        while (low < high)
        {
            int middle = (low + high + 1) >> 1;
            if (points[middle].Tick <= tick)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }
        CurvePoint left = points[low];
        if (low == points.Count - 1 || left.Interpolation == CurveInterpolation.Step)
        {
            return left.Value;
        }
        CurvePoint right = points[low + 1];
        return Interpolate(left.Value, right.Value, tick - left.Tick, right.Tick - left.Tick);
    }

    private static void EmitTarget(
        List<RawMidiEvent> output,
        long tick,
        MidiValueTarget target,
        int value,
        SourceReference source,
        ref long sequence,
        CanonicalEventRole? roleOverride = null)
    {
        switch (target.Kind)
        {
            case MidiValueKind.ControlChange:
                output.Add(RawMidiEvent.Control(tick, target.Number, value, roleOverride ?? CanonicalEventRole.ControlChange, sequence++, source));
                break;
            case MidiValueKind.BankMsb:
                output.Add(RawMidiEvent.Control(tick, 0, value, roleOverride ?? CanonicalEventRole.Bank, sequence++, source));
                break;
            case MidiValueKind.BankLsb:
                output.Add(RawMidiEvent.Control(tick, 32, value, roleOverride ?? CanonicalEventRole.Bank, sequence++, source));
                break;
            case MidiValueKind.Program:
                output.Add(RawMidiEvent.Program(tick, value, sequence++, source, roleOverride ?? CanonicalEventRole.Program));
                break;
            case MidiValueKind.PitchBend:
                output.Add(RawMidiEvent.PitchBend(tick, value, sequence++, source, roleOverride ?? CanonicalEventRole.PitchBend));
                break;
            case MidiValueKind.RegisteredParameter:
                EmitParameter(output, tick, true, target.Number, value, source, ref sequence, roleOverride ?? CanonicalEventRole.Parameter);
                break;
            case MidiValueKind.NonRegisteredParameter:
                EmitParameter(output, tick, false, target.Number, value, source, ref sequence, roleOverride ?? CanonicalEventRole.Parameter);
                break;
            case MidiValueKind.PitchBendRangeSemitones:
                EmitPitchBendRangeComponent(output, tick, true, value, source, ref sequence, roleOverride ?? CanonicalEventRole.Parameter);
                break;
            case MidiValueKind.PitchBendRangeCents:
                EmitPitchBendRangeComponent(output, tick, false, value, source, ref sequence, roleOverride ?? CanonicalEventRole.Parameter);
                break;
        }
    }

    private static int NormalizeTargetValue(
        MidiValueTarget target,
        double value,
        MidiIntegerTargetSettings settings)
    {
        double minimum = TargetMinimum(target);
        double maximum = TargetMaximum(target);
        if (!double.IsFinite(value))
        {
            throw new MappingException("Target value is NaN or Infinity.");
        }
        if (value < minimum || value > maximum)
        {
            if (settings.Overflow == MappingOverflow.Clamp)
            {
                value = Math.Clamp(value, minimum, maximum);
            }
            else
            {
                throw new MappingException($"Target value {value} is outside [{minimum}, {maximum}].");
            }
        }
        return MappingEngine.Round(value, settings.Rounding);
    }

    private static double DefaultTargetValue(MidiValueTarget target) => target.Kind switch
    {
        MidiValueKind.ControlChange => BuiltInControllerReset(target.Number),
        MidiValueKind.PitchBend => 0,
        MidiValueKind.PitchBendRangeSemitones => 2,
        MidiValueKind.PitchBendRangeCents => 0,
        _ => 0
    };

    private static double TargetMinimum(MidiValueTarget target) => target.Kind == MidiValueKind.PitchBend ? -8192 : 0;
    private static double TargetMaximum(MidiValueTarget target) => target.Kind switch
    {
        MidiValueKind.PitchBend => 8191,
        MidiValueKind.RegisteredParameter or MidiValueKind.NonRegisteredParameter => 16383,
        MidiValueKind.PitchBendRangeCents => 99,
        _ => 127
    };

    private static void EmitParameter(
        List<RawMidiEvent> output,
        long tick,
        bool registered,
        int parameter,
        int value,
        SourceReference source,
        ref long sequence,
        CanonicalEventRole role = CanonicalEventRole.Parameter)
    {
        long group = sequence;
        long targetKey = (registered ? RpnTargetKeyBase : NrpnTargetKeyBase) + parameter;
        output.Add(RawMidiEvent.Control(tick, registered ? 101 : 99, (parameter >> 7) & 127, role, sequence++, source, targetKey, group));
        output.Add(RawMidiEvent.Control(tick, registered ? 100 : 98, parameter & 127, role, sequence++, source, targetKey, group));
        output.Add(RawMidiEvent.Control(tick, 6, (value >> 7) & 127, role, sequence++, source, targetKey, group));
        output.Add(RawMidiEvent.Control(tick, 38, value & 127, role, sequence++, source, targetKey, group));
        output.Add(RawMidiEvent.Control(tick, registered ? 101 : 99, 127, role, sequence++, source, targetKey, group));
        output.Add(RawMidiEvent.Control(tick, registered ? 100 : 98, 127, role, sequence++, source, targetKey, group));
    }

    private static void EmitPitchBendRange(
        List<RawMidiEvent> output,
        long tick,
        int semitones,
        int cents,
        SourceReference source,
        ref long sequence,
        CanonicalEventRole role = CanonicalEventRole.Parameter)
    {
        long group = sequence;
        output.Add(RawMidiEvent.Control(tick, 101, 0, role, sequence++, source, PitchBendRangeTargetKey, group));
        output.Add(RawMidiEvent.Control(tick, 100, 0, role, sequence++, source, PitchBendRangeTargetKey, group));
        output.Add(RawMidiEvent.Control(tick, 6, semitones, role, sequence++, source, PitchBendRangeTargetKey, group));
        output.Add(RawMidiEvent.Control(tick, 38, cents, role, sequence++, source, PitchBendRangeTargetKey, group));
        output.Add(RawMidiEvent.Control(tick, 101, 127, role, sequence++, source, PitchBendRangeTargetKey, group));
        output.Add(RawMidiEvent.Control(tick, 100, 127, role, sequence++, source, PitchBendRangeTargetKey, group));
    }

    private static void EmitPitchBendRangeComponent(
        List<RawMidiEvent> output,
        long tick,
        bool semitones,
        int value,
        SourceReference source,
        ref long sequence,
        CanonicalEventRole role = CanonicalEventRole.Parameter)
    {
        long group = sequence;
        long targetKey = semitones ? PitchBendRangeSemitoneTargetKey : PitchBendRangeCentsTargetKey;
        output.Add(RawMidiEvent.Control(tick, 101, 0, role, sequence++, source, targetKey, group));
        output.Add(RawMidiEvent.Control(tick, 100, 0, role, sequence++, source, targetKey, group));
        output.Add(RawMidiEvent.Control(tick, semitones ? 6 : 38, value, role, sequence++, source, targetKey, group));
        output.Add(RawMidiEvent.Control(tick, 101, 127, role, sequence++, source, targetKey, group));
        output.Add(RawMidiEvent.Control(tick, 100, 127, role, sequence++, source, targetKey, group));
    }

    private const long ControlTargetKeyBase = 0x1_0000;
    private const long ProgramTargetKey = 0x2_0000;
    private const long PitchBendTargetKey = 0x3_0000;
    private const long RpnTargetKeyBase = 0x4_0000;
    private const long NrpnTargetKeyBase = 0x5_0000;
    private const long PitchBendRangeSemitoneTargetKey = 0x6_0000;
    private const long PitchBendRangeCentsTargetKey = 0x6_0001;
    private const long PitchBendRangeTargetKey = 0x6_0002;

    private static long ControlTargetKey(int controller) => ControlTargetKeyBase + controller;

    private static long SemanticTargetForTarget(MidiValueTarget target) => target.Kind switch
    {
        MidiValueKind.ControlChange => ControlTargetKey(target.Number),
        MidiValueKind.BankMsb => ControlTargetKey(0),
        MidiValueKind.BankLsb => ControlTargetKey(32),
        MidiValueKind.Program => ProgramTargetKey,
        MidiValueKind.PitchBend => PitchBendTargetKey,
        MidiValueKind.RegisteredParameter => RpnTargetKeyBase + target.Number,
        MidiValueKind.NonRegisteredParameter => NrpnTargetKeyBase + target.Number,
        MidiValueKind.PitchBendRangeSemitones => PitchBendRangeSemitoneTargetKey,
        MidiValueKind.PitchBendRangeCents => PitchBendRangeCentsTargetKey,
        _ => long.MinValue
    };

    private static long SemanticTargetForMessage(MidiMessage message) => message.MessageType switch
    {
        MidiMessageType.ControlChange => ControlTargetKey(message.Byte1),
        MidiMessageType.ProgramChange => ProgramTargetKey,
        MidiMessageType.PitchWheelChange => PitchBendTargetKey,
        _ => long.MinValue
    };

    private static HashSet<MidiValueTarget> GetTickZeroTargets(SubVoice voice)
    {
        HashSet<MidiValueTarget> result = [];
        foreach (TemplateEvent value in voice.Events.Where(value => value.Tick == 0))
        {
            AddTemplateTargets(value, result);
        }
        foreach (ValueCurve curve in voice.Curves)
        {
            if (curve.Points.Any(value => value.Tick == 0))
            {
                result.Add(curve.Target);
            }
        }
        return result;
    }

    private static HashSet<MidiValueTarget> CollectUsedTargets(
        EventInstrument instrument,
        SubVoice voice,
        MidiInitialState explicitState)
    {
        HashSet<MidiValueTarget> result = [];
        if (explicitState.BankMsb.HasValue) result.Add(MidiValueTarget.BankMsb);
        if (explicitState.BankLsb.HasValue) result.Add(MidiValueTarget.BankLsb);
        if (explicitState.Program.HasValue) result.Add(MidiValueTarget.Program);
        if (explicitState.PitchBend.HasValue) result.Add(MidiValueTarget.PitchBend);
        if (explicitState.PitchBendRangeSemitones.HasValue) result.Add(MidiValueTarget.PitchBendRangeSemitones);
        if (explicitState.PitchBendRangeCents.HasValue) result.Add(MidiValueTarget.PitchBendRangeCents);
        foreach (int controller in explicitState.Controllers.Keys) result.Add(MidiValueTarget.ControlChange(controller));
        foreach (int parameter in explicitState.RegisteredParameters.Keys) result.Add(MidiValueTarget.Rpn(parameter));
        foreach (int parameter in explicitState.NonRegisteredParameters.Keys) result.Add(MidiValueTarget.Nrpn(parameter));
        foreach (TemplateEvent value in voice.Events) AddTemplateTargets(value, result);
        foreach (ValueCurve curve in voice.Curves) result.Add(curve.Target);
        foreach (LogicalParameterMapping mapping in instrument.ParameterMappings
            .Where(value => value.Steps.IsEnabled && value.SubVoiceId == voice.Id))
        {
            result.Add(mapping.Target);
        }
        return result;
    }

    private static void AddTemplateTargets(TemplateEvent value, HashSet<MidiValueTarget> targets)
    {
        switch (value.Kind)
        {
            case TemplateEventKind.Note:
                break;
            case TemplateEventKind.ControlChange:
                targets.Add(MidiValueTarget.ControlChange(value.Number));
                break;
            case TemplateEventKind.Bank:
                if (value.HasBankMsb) targets.Add(MidiValueTarget.BankMsb);
                if (value.HasBankLsb) targets.Add(MidiValueTarget.BankLsb);
                break;
            case TemplateEventKind.Program:
                targets.Add(MidiValueTarget.Program);
                break;
            case TemplateEventKind.PitchBend:
                targets.Add(MidiValueTarget.PitchBend);
                break;
            case TemplateEventKind.RegisteredParameter:
                targets.Add(MidiValueTarget.Rpn(value.Number));
                break;
            case TemplateEventKind.NonRegisteredParameter:
                targets.Add(MidiValueTarget.Nrpn(value.Number));
                break;
            case TemplateEventKind.PitchBendRange:
                targets.Add(MidiValueTarget.PitchBendRangeSemitones);
                targets.Add(MidiValueTarget.PitchBendRangeCents);
                break;
        }
    }

    private static void EmitReset(
        List<RawMidiEvent> output,
        long tick,
        IEnumerable<MidiValueTarget> targets,
        MidiInitialState resetDefaults,
        SourceReference source,
        ref long sequence)
    {
        foreach (MidiValueTarget target in targets.OrderBy(value => value.Kind).ThenBy(value => value.Number))
        {
            switch (target.Kind)
            {
                case MidiValueKind.ControlChange:
                    int controllerValue = resetDefaults.Controllers.GetValueOrDefault(
                        target.Number, BuiltInControllerReset(target.Number));
                    output.Add(RawMidiEvent.Control(tick, target.Number, controllerValue,
                        CanonicalEventRole.Reset, sequence++, source));
                    break;
                case MidiValueKind.BankMsb:
                    output.Add(RawMidiEvent.Control(tick, 0, resetDefaults.BankMsb ?? 0,
                        CanonicalEventRole.Reset, sequence++, source));
                    break;
                case MidiValueKind.BankLsb:
                    output.Add(RawMidiEvent.Control(tick, 32, resetDefaults.BankLsb ?? 0,
                        CanonicalEventRole.Reset, sequence++, source));
                    break;
                case MidiValueKind.Program:
                    output.Add(RawMidiEvent.Program(tick, resetDefaults.Program ?? 0, sequence++, source, CanonicalEventRole.Reset));
                    break;
                case MidiValueKind.PitchBend:
                    output.Add(RawMidiEvent.PitchBend(tick, resetDefaults.PitchBend ?? 0, sequence++, source, CanonicalEventRole.Reset));
                    break;
                case MidiValueKind.RegisteredParameter:
                    EmitParameter(output, tick, true, target.Number,
                        resetDefaults.RegisteredParameters.GetValueOrDefault(target.Number), source, ref sequence, CanonicalEventRole.Reset);
                    break;
                case MidiValueKind.NonRegisteredParameter:
                    EmitParameter(output, tick, false, target.Number,
                        resetDefaults.NonRegisteredParameters.GetValueOrDefault(target.Number), source, ref sequence, CanonicalEventRole.Reset);
                    break;
                case MidiValueKind.PitchBendRangeSemitones:
                    EmitPitchBendRangeComponent(output, tick, true,
                        resetDefaults.PitchBendRangeSemitones ?? 2, source, ref sequence, CanonicalEventRole.Reset);
                    break;
                case MidiValueKind.PitchBendRangeCents:
                    EmitPitchBendRangeComponent(output, tick, false,
                        resetDefaults.PitchBendRangeCents ?? 0, source, ref sequence, CanonicalEventRole.Reset);
                    break;
            }
        }
    }

    private static int BuiltInControllerReset(int controller) => controller switch
    {
        7 => 100,
        10 => 64,
        11 => 127,
        _ => 0
    };

    private static void ValidateOverlap(MidoraProject project, List<RawInstance> instances, List<CompilerDiagnostic> diagnostics)
    {
        foreach (IGrouping<(MidoraId TrackId, MidoraId InstrumentId), RawInstance> binding in instances
            .GroupBy(value => (value.TrackId, value.InstrumentId)))
        {
            RawInstance[] ordered = binding.OrderBy(value => value.StartTick).ThenBy(value => value.SourceOrder).ToArray();
            for (int i = 0; i < ordered.Length; i++)
            {
                for (int j = i + 1; j < ordered.Length && ordered[j].StartTick < ordered[i].EndTick; j++)
                {
                    bool inScope = ordered[i].OverlapScope == OverlapScope.AnyPitch || ordered[i].Pitch == ordered[j].Pitch;
                    if (!inScope || ordered[i].OverlapPolicy is OverlapPolicy.LetOverlap
                        or OverlapPolicy.CutPrevious or OverlapPolicy.CutNewRejectNew)
                    {
                        continue;
                    }
                    DiagnosticSeverity severity = ordered[i].OverlapPolicy == OverlapPolicy.Reject
                        ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning;
                    diagnostics.Add(new("MIDORA2201", severity,
                        "Event Instrument Instance 发生受策略约束的重叠。",
                        new(ordered[j].TrackId, ordered[j].SegmentId, ordered[j].InstanceId,
                            ordered[j].InstrumentId, Tick: ordered[j].StartTick)));
                }
            }
        }
    }

    private static AllocationResult Allocate(
        MidoraProject project,
        List<RawInstance> instances,
        List<CompilerDiagnostic> diagnostics)
    {
        List<AllocationGroup> groups = [];
        foreach (IGrouping<(MidoraId TrackId, MidoraId InstrumentId), RawInstance> binding in instances
            .GroupBy(value => (value.TrackId, value.InstrumentId)))
        {
            RawInstance[] ordered = binding.OrderBy(value => value.StartTick).ThenBy(value => value.SourceOrder).ToArray();
            AllocationGroup? current = null;
            foreach (RawInstance instance in ordered)
            {
                if (instance.Isolated || current is null || instance.StartTick >= current.EndTick)
                {
                    current = new AllocationGroup(instance);
                    groups.Add(current);
                }
                else
                {
                    current.Add(instance);
                }
            }
        }
        Dictionary<MidoraId, int> trackOrder = project.Tracks
            .Select((track, index) => (track.Id, index)).ToDictionary(value => value.Id, value => value.index);
        groups.Sort((left, right) =>
        {
            int byStart = left.StartTick.CompareTo(right.StartTick);
            if (byStart != 0) return byStart;
            int byTrack = trackOrder[left.TrackId].CompareTo(trackOrder[right.TrackId]);
            return byTrack != 0 ? byTrack : left.SourceOrder.CompareTo(right.SourceOrder);
        });

        bool[] used = new bool[256];
        List<ActiveAllocation> active = [];
        Dictionary<(MidoraId InstanceId, MidoraId SubVoiceId), int> unitByVoice = [];
        List<ChannelUnitAllocation> allocations = [];
        int peak = 0;
        foreach (AllocationGroup group in groups)
        {
            for (int i = active.Count - 1; i >= 0; i--)
            {
                if (active[i].EndTick <= group.StartTick)
                {
                    foreach (int unit in active[i].Units)
                    {
                        used[unit] = false;
                    }
                    active.RemoveAt(i);
                }
            }
            int count = group.Instances[0].Voices.Length;
            int[] units = new int[count];
            int found = 0;
            for (int unit = 0; unit < used.Length && found < count; unit++)
            {
                if (!used[unit])
                {
                    units[found++] = unit;
                }
            }
            if (found != count)
            {
                diagnostics.Add(new("MIDORA2202", DiagnosticSeverity.Error,
                    $"无法为 {count} 个 SubVoice 原子分配 Channel Group；全局上限为 256 Channel Units。",
                    new(group.Instances[0].TrackId, group.Instances[0].SegmentId,
                        group.Instances[0].InstanceId, group.Instances[0].InstrumentId, Tick: group.StartTick)));
                continue;
            }
            foreach (int unit in units)
            {
                used[unit] = true;
            }
            active.Add(new(group.EndTick, units));
            peak = Math.Max(peak, used.Count(value => value));
            foreach (RawInstance instance in group.Instances)
            {
                for (int i = 0; i < instance.Voices.Length; i++)
                {
                    RawSubVoice voice = instance.Voices[i];
                    int unit = units[i];
                    unitByVoice[(instance.InstanceId, voice.SubVoiceId)] = unit;
                    allocations.Add(new(instance.TrackId, instance.InstrumentId, group.GroupId, voice.SubVoiceId,
                        group.StartTick, group.EndTick, (byte)(unit >> 4), (byte)(unit & 15)));
                }
            }
        }
        return new(unitByVoice, allocations.ToArray(), peak);
    }

    private static List<CanonicalMidiEvent> MaterializeEvents(
        List<RawInstance> instances,
        IReadOnlyDictionary<(MidoraId InstanceId, MidoraId SubVoiceId), int> unitByVoice)
    {
        List<CanonicalMidiEvent> result = [];
        foreach (RawInstance instance in instances)
        {
            foreach (RawSubVoice voice in instance.Voices)
            {
                if (!unitByVoice.TryGetValue((instance.InstanceId, voice.SubVoiceId), out int unit))
                {
                    continue;
                }
                byte channel = (byte)(unit & 15);
                foreach (RawMidiEvent value in voice.Events)
                {
                    result.Add(new(value.Tick, (byte)(unit >> 4), channel, value.ToMidiMessage(channel),
                        value.Role, value.Sequence, value.SemanticTargetKey, value.SemanticGroup, value.Source));
                }
            }
        }
        result.Sort(CanonicalComparer.Instance);
        return FoldSameTickStates(result);
    }

    private static List<CanonicalMidiEvent> FoldSameTickStates(List<CanonicalMidiEvent> sorted)
    {
        Dictionary<(long Tick, byte Port, byte Channel, long Target), long> selectedGroups = [];
        List<CanonicalMidiEvent> reversed = new(sorted.Count);
        for (int i = sorted.Count - 1; i >= 0; i--)
        {
            CanonicalMidiEvent value = sorted[i];
            if (value.SemanticTargetKey == long.MinValue)
            {
                reversed.Add(value);
                continue;
            }
            (long, byte, byte, long) key = (value.Tick, value.ZeroBasedPort, value.ZeroBasedChannel, value.SemanticTargetKey);
            if (!selectedGroups.TryGetValue(key, out long selectedGroup))
            {
                selectedGroups.Add(key, value.SemanticGroup);
                reversed.Add(value);
            }
            else if (selectedGroup == value.SemanticGroup)
            {
                reversed.Add(value);
            }
        }
        reversed.Reverse();
        reversed.Sort(CanonicalComparer.Instance);
        return reversed;
    }

    private static CanonicalMidiEvent[] ApplyRange(
        List<CanonicalMidiEvent> source,
        ReadOnlySpan<ChannelUnitAllocation> allocations,
        long startTick,
        long endTick,
        MidiInitialState resetDefaults)
    {
        List<CanonicalMidiEvent> result = [];
        Dictionary<(byte Port, byte Channel, long Target), CanonicalStateGroup> state = [];
        Dictionary<(byte Port, byte Channel, byte Note), int> activeNotes = [];
        HashSet<(byte Port, byte Channel, long Target)> pollutedTargets = [];
        HashSet<(byte Port, byte Channel)> activeAtStart = [];
        foreach (ChannelUnitAllocation allocation in allocations)
        {
            if (allocation.StartTick < startTick && allocation.EndTick > startTick)
            {
                activeAtStart.Add((allocation.ZeroBasedPort, allocation.ZeroBasedChannel));
            }
        }
        foreach (CanonicalMidiEvent value in source)
        {
            MidiMessage message = value.Message;
            if (value.Tick < startTick)
            {
                if (message.MessageType == MidiMessageType.NoteOn && message.Byte2 != 0)
                {
                    (byte, byte, byte) key = (value.ZeroBasedPort, value.ZeroBasedChannel, message.Byte1);
                    activeNotes[key] = activeNotes.GetValueOrDefault(key) + 1;
                }
                else if (message.MessageType == MidiMessageType.NoteOff
                    || (message.MessageType == MidiMessageType.NoteOn && message.Byte2 == 0))
                {
                    (byte, byte, byte) key = (value.ZeroBasedPort, value.ZeroBasedChannel, message.Byte1);
                    if (activeNotes.TryGetValue(key, out int count) && count > 0)
                    {
                        activeNotes[key] = count - 1;
                    }
                }
                else if (value.SemanticTargetKey != long.MinValue)
                {
                    (byte, byte, long) key = (
                        value.ZeroBasedPort, value.ZeroBasedChannel, value.SemanticTargetKey);
                    if (!state.TryGetValue(key, out CanonicalStateGroup? group)
                        || group.SemanticGroup != value.SemanticGroup)
                    {
                        group = new CanonicalStateGroup(
                            value.Tick, value.Role, value.StableOrder, value.SemanticGroup);
                        state[key] = group;
                    }
                    group.Events.Add(value);
                }
                continue;
            }
            if (value.Tick >= endTick)
            {
                continue;
            }
            result.Add(value);
            if (message.MessageType is not MidiMessageType.NoteOn and not MidiMessageType.NoteOff
                && value.Role != CanonicalEventRole.Reset && value.SemanticTargetKey != long.MinValue)
            {
                pollutedTargets.Add((value.ZeroBasedPort, value.ZeroBasedChannel, value.SemanticTargetKey));
            }
            if (message.MessageType == MidiMessageType.NoteOn && message.Byte2 != 0)
            {
                (byte, byte, byte) key = (value.ZeroBasedPort, value.ZeroBasedChannel, message.Byte1);
                activeNotes[key] = activeNotes.GetValueOrDefault(key) + 1;
            }
            else if (message.MessageType == MidiMessageType.NoteOff)
            {
                (byte, byte, byte) key = (value.ZeroBasedPort, value.ZeroBasedChannel, message.Byte1);
                if (activeNotes.TryGetValue(key, out int count) && count > 0)
                {
                    activeNotes[key] = count - 1;
                }
            }
        }

        long restoreOrder = long.MinValue;
        foreach (((byte port, byte channel, long target), CanonicalStateGroup group) in state
            .Where(value => activeAtStart.Contains((value.Key.Port, value.Key.Channel)))
            .OrderBy(value => value.Key.Port).ThenBy(value => value.Key.Channel)
            .ThenBy(value => value.Value.Role).ThenBy(value => value.Value.Tick)
            .ThenBy(value => value.Value.StableOrder))
        {
            pollutedTargets.Add((port, channel, target));
            foreach (CanonicalMidiEvent previous in group.Events.OrderBy(value => value.StableOrder))
            {
                result.Add(previous with
                {
                    Tick = startTick,
                    Role = CanonicalEventRole.RangeRestore,
                    StableOrder = restoreOrder++
                });
            }
        }

        HashSet<(byte Port, byte Channel)> cleanupChannels = [];
        foreach (((byte port, byte channel, byte note), int count) in activeNotes)
        {
            for (int i = 0; i < count; i++)
            {
                result.Add(new(endTick, port, channel, MidiMessage.NoteOff(channel, note, 0),
                    CanonicalEventRole.NoteOff, long.MaxValue - 4, long.MinValue, long.MinValue, default));
            }
            if (count > 0)
            {
                cleanupChannels.Add((port, channel));
            }
        }
        foreach (ChannelUnitAllocation allocation in allocations)
        {
            // Events at endTick are excluded by the half-open range. An allocation
            // ending exactly there therefore still needs the synthetic boundary
            // cleanup that replaces its filtered source Reset group.
            if (allocation.StartTick < endTick && allocation.EndTick >= endTick)
            {
                cleanupChannels.Add((allocation.ZeroBasedPort, allocation.ZeroBasedChannel));
            }
        }
        long resetOrder = long.MaxValue / 2;
        foreach ((byte port, byte channel) in cleanupChannels)
        {
            foreach ((byte statePort, byte stateChannel, long target) in pollutedTargets
                .OrderBy(value => value.Target))
            {
                if (statePort != port || stateChannel != channel)
                {
                    continue;
                }
                AppendCanonicalReset(
                    result, endTick, port, channel, target, resetDefaults, ref resetOrder);
            }
        }
        result.Sort(CanonicalComparer.Instance);
        return FoldSameTickStates(result).ToArray();
    }

    private static void AppendCanonicalReset(
        List<CanonicalMidiEvent> output,
        long tick,
        byte port,
        byte channel,
        long target,
        MidiInitialState defaults,
        ref long order)
    {
        long currentOrder = order;
        long group = currentOrder;
        if (target >= ControlTargetKeyBase && target < ControlTargetKeyBase + 128)
        {
            int controller = checked((int)(target - ControlTargetKeyBase));
            int value = controller switch
            {
                0 => defaults.BankMsb ?? 0,
                32 => defaults.BankLsb ?? 0,
                _ => defaults.Controllers.GetValueOrDefault(controller, BuiltInControllerReset(controller))
            };
            Add(MidiMessage.ControlChange(channel, checked((byte)controller), checked((byte)value)));
        }
        else if (target == ProgramTargetKey)
        {
            Add(MidiMessage.ProgramChange(channel, checked((byte)(defaults.Program ?? 0))));
        }
        else if (target == PitchBendTargetKey)
        {
            Add(MidiMessage.PitchWheelChange(channel, checked((ushort)((defaults.PitchBend ?? 0) + 8192))));
        }
        else if (target >= RpnTargetKeyBase && target < RpnTargetKeyBase + 16_384)
        {
            int parameter = checked((int)(target - RpnTargetKeyBase));
            AddParameter(true, parameter, defaults.RegisteredParameters.GetValueOrDefault(parameter));
        }
        else if (target >= NrpnTargetKeyBase && target < NrpnTargetKeyBase + 16_384)
        {
            int parameter = checked((int)(target - NrpnTargetKeyBase));
            AddParameter(false, parameter, defaults.NonRegisteredParameters.GetValueOrDefault(parameter));
        }
        else if (target == PitchBendRangeTargetKey)
        {
            Add(MidiMessage.ControlChange(channel, 101, 0));
            Add(MidiMessage.ControlChange(channel, 100, 0));
            Add(MidiMessage.ControlChange(channel, 6, checked((byte)(defaults.PitchBendRangeSemitones ?? 2))));
            Add(MidiMessage.ControlChange(channel, 38, checked((byte)(defaults.PitchBendRangeCents ?? 0))));
            Add(MidiMessage.ControlChange(channel, 101, 127));
            Add(MidiMessage.ControlChange(channel, 100, 127));
        }
        else if (target is PitchBendRangeSemitoneTargetKey or PitchBendRangeCentsTargetKey)
        {
            bool semitones = target == PitchBendRangeSemitoneTargetKey;
            Add(MidiMessage.ControlChange(channel, 101, 0));
            Add(MidiMessage.ControlChange(channel, 100, 0));
            Add(MidiMessage.ControlChange(channel, semitones ? (byte)6 : (byte)38,
                checked((byte)(semitones
                    ? defaults.PitchBendRangeSemitones ?? 2
                    : defaults.PitchBendRangeCents ?? 0))));
            Add(MidiMessage.ControlChange(channel, 101, 127));
            Add(MidiMessage.ControlChange(channel, 100, 127));
        }

        void AddParameter(bool registered, int parameter, int value)
        {
            Add(MidiMessage.ControlChange(channel, registered ? (byte)101 : (byte)99, checked((byte)(parameter >> 7))));
            Add(MidiMessage.ControlChange(channel, registered ? (byte)100 : (byte)98, checked((byte)(parameter & 127))));
            Add(MidiMessage.ControlChange(channel, 6, checked((byte)(value >> 7))));
            Add(MidiMessage.ControlChange(channel, 38, checked((byte)(value & 127))));
            Add(MidiMessage.ControlChange(channel, registered ? (byte)101 : (byte)99, 127));
            Add(MidiMessage.ControlChange(channel, registered ? (byte)100 : (byte)98, 127));
        }

        void Add(MidiMessage message) => output.Add(new(
            tick, port, channel, message, CanonicalEventRole.Reset,
            currentOrder++, target, group, default));

        order = currentOrder;
    }

    private static long GetNaturalEnd(
        MidoraProject project,
        IReadOnlySet<MidoraId>? includedTrackIds)
    {
        long end = 0;
        foreach (LogicalTrack track in project.Tracks)
        {
            if (includedTrackIds is not null && !includedTrackIds.Contains(track.Id))
            {
                continue;
            }
            foreach (Segment segment in track.Segments)
            {
                if (segment.Notes.Count != 0
                    && segment.ProjectStartTick >= 0 && segment.LengthTicks > 0
                    && segment.ProjectStartTick <= long.MaxValue - segment.LengthTicks)
                {
                    end = Math.Max(end, segment.ProjectStartTick + segment.LengthTicks);
                }
            }
        }
        return end;
    }

    private static CanonicalConductor FreezeConductor(ConductorTrack source, long startTick, long endTick) => new(
        RangeStateful(
            source.Tempos.OrderBy(value => value.Tick).ToArray(), startTick, endTick,
            value => value.Tick,
            (value, tick, restored) => new CanonicalTempo(value.Id, tick, value.BeatsPerMinute, restored)),
        RangeStateful(
            source.TimeSignatures.OrderBy(value => value.Tick).ToArray(), startTick, endTick,
            value => value.Tick,
            (value, tick, restored) => new CanonicalTimeSignature(
                value.Id, tick, value.Numerator, value.Denominator, restored)),
        RangeStateful(
            source.KeySignatures.OrderBy(value => value.Tick).ToArray(), startTick, endTick,
            value => value.Tick,
            (value, tick, restored) => new CanonicalKeySignature(
                value.Id, tick, value.SharpsFlats, value.IsMinor, restored)),
        source.Markers.Where(value => value.Tick >= startTick && value.Tick < endTick)
            .OrderBy(value => value.Tick)
            .ThenBy(value => value.Id)
            .Select(value => new CanonicalMarker(value.Id, value.Tick, value.Name)).ToArray(),
        source.EndMarker is null ? null : new CanonicalEndMarker(source.EndMarker.Id, source.EndMarker.Tick));

    private static TResult[] RangeStateful<TSource, TResult>(
        TSource[] ordered,
        long startTick,
        long endTick,
        Func<TSource, long> getTick,
        Func<TSource, long, bool, TResult> convert)
        where TSource : class
    {
        List<TResult> result = [];
        bool hasAtStart = ordered.Any(value => getTick(value) == startTick);
        if (startTick > 0 && !hasAtStart)
        {
            TSource? previous = ordered.LastOrDefault(value => getTick(value) < startTick);
            if (previous is not null)
            {
                result.Add(convert(previous, startTick, true));
            }
        }
        foreach (TSource value in ordered)
        {
            long tick = getTick(value);
            if (tick >= startTick && (tick < endTick || endTick == startTick && tick == startTick))
            {
                result.Add(convert(value, tick, false));
            }
        }
        return result.ToArray();
    }

    private static bool TrackReferences(LogicalTrack track, MidoraId instrumentId) =>
        track.EventInstrumentId == instrumentId;

    private sealed record TrackCacheEntry(
        long Fingerprint,
        RawInstance[] Instances,
        CompilerDiagnostic[] Diagnostics);

    private sealed class CanonicalStateGroup(
        long tick,
        CanonicalEventRole role,
        long stableOrder,
        long semanticGroup)
    {
        public long Tick { get; } = tick;
        public CanonicalEventRole Role { get; } = role;
        public long StableOrder { get; } = stableOrder;
        public long SemanticGroup { get; } = semanticGroup;
        public List<CanonicalMidiEvent> Events { get; } = [];
    }

    private sealed record RawInstance(
        MidoraId InstanceId,
        MidoraId TrackId,
        MidoraId SegmentId,
        MidoraId InstrumentId,
        int Pitch,
        long StartTick,
        long EndTick,
        bool Isolated,
        OverlapPolicy OverlapPolicy,
        OverlapScope OverlapScope,
        int SourceOrder,
        RawSubVoice[] Voices);

    private sealed record RawSubVoice(MidoraId SubVoiceId, RawMidiEvent[] Events);

    private sealed class AcceptedInstance(
        int resultIndex,
        Segment segment,
        LogicalNote note,
        long projectStartTick,
        long segmentEndTick,
        int sourceOrder,
        RawInstance instance)
    {
        public int ResultIndex { get; } = resultIndex;
        public Segment Segment { get; } = segment;
        public LogicalNote Note { get; } = note;
        public long ProjectStartTick { get; } = projectStartTick;
        public long SegmentEndTick { get; } = segmentEndTick;
        public int SourceOrder { get; } = sourceOrder;
        public RawInstance Instance { get; set; } = instance;
    }

    private readonly record struct EventOccurrence(long LocalTick, long TemplateTick);
    private readonly record struct TargetStatePoint(long Tick, double Value);

    private enum RawMessageKind : byte
    {
        NoteOff,
        NoteOn,
        ControlChange,
        ProgramChange,
        PitchBend
    }

    private readonly record struct RawMidiEvent(
        long Tick,
        RawMessageKind Kind,
        int Data1,
        int Data2,
        CanonicalEventRole Role,
        long Sequence,
        long SemanticTargetKey,
        long SemanticGroup,
        SourceReference Source)
    {
        public static RawMidiEvent NoteOff(long tick, int note, long sequence, SourceReference source) =>
            new(tick, RawMessageKind.NoteOff, note, 0, CanonicalEventRole.NoteOff,
                sequence, long.MinValue, long.MinValue, source);
        public static RawMidiEvent NoteOn(long tick, int note, int velocity, long sequence, SourceReference source) =>
            new(tick, RawMessageKind.NoteOn, note, velocity, CanonicalEventRole.NoteOn,
                sequence, long.MinValue, long.MinValue, source);
        public static RawMidiEvent Control(
            long tick, int controller, int value, CanonicalEventRole role, long sequence,
            SourceReference source, long semanticTargetKey = long.MinValue, long semanticGroup = long.MinValue) =>
            new(tick, RawMessageKind.ControlChange, controller, value, role, sequence,
                semanticTargetKey == long.MinValue ? ControlTargetKey(controller) : semanticTargetKey,
                semanticGroup == long.MinValue ? sequence : semanticGroup,
                source);
        public static RawMidiEvent Program(long tick, int program, long sequence, SourceReference source, CanonicalEventRole role = CanonicalEventRole.Program) =>
            new(tick, RawMessageKind.ProgramChange, program, 0, role, sequence,
                ProgramTargetKey, sequence, source);
        public static RawMidiEvent PitchBend(long tick, int value, long sequence, SourceReference source, CanonicalEventRole role = CanonicalEventRole.PitchBend) =>
            new(tick, RawMessageKind.PitchBend, value, 0, role, sequence,
                PitchBendTargetKey, sequence, source);

        public MidiMessage ToMidiMessage(byte channel) => Kind switch
        {
            RawMessageKind.NoteOff => MidiMessage.NoteOff(channel, checked((byte)Data1), 0),
            RawMessageKind.NoteOn => MidiMessage.NoteOn(channel, checked((byte)Data1), checked((byte)Data2)),
            RawMessageKind.ControlChange => MidiMessage.ControlChange(channel, checked((byte)Data1), checked((byte)Data2)),
            RawMessageKind.ProgramChange => MidiMessage.ProgramChange(channel, checked((byte)Data1)),
            RawMessageKind.PitchBend => MidiMessage.PitchWheelChange(channel, checked((ushort)(Data1 + 8192))),
            _ => throw new InvalidOperationException()
        };
    }

    private sealed class AllocationGroup
    {
        public AllocationGroup(RawInstance instance)
        {
            GroupId = instance.InstanceId;
            StartTick = instance.StartTick;
            EndTick = instance.EndTick;
            SourceOrder = instance.SourceOrder;
            Instances.Add(instance);
        }
        public MidoraId GroupId { get; }
        public MidoraId TrackId => Instances[0].TrackId;
        public long StartTick { get; }
        public long EndTick { get; private set; }
        public int SourceOrder { get; }
        public List<RawInstance> Instances { get; } = [];
        public void Add(RawInstance instance)
        {
            Instances.Add(instance);
            EndTick = Math.Max(EndTick, instance.EndTick);
        }
    }

    private sealed record ActiveAllocation(long EndTick, int[] Units);
    private sealed record AllocationResult(
        Dictionary<(MidoraId InstanceId, MidoraId SubVoiceId), int> UnitBySubVoice,
        ChannelUnitAllocation[] Allocations,
        int PeakUnits);

    private static MappingStableIdV1 ToMappingId(MidoraId id) => new(id.High, id.Low);

    private static MappingEventKindV1 ToMappingEventKind(TemplateEventKind kind) => kind switch
    {
        TemplateEventKind.Note => MappingEventKindV1.Note,
        TemplateEventKind.ControlChange => MappingEventKindV1.ControlChange,
        TemplateEventKind.Bank => MappingEventKindV1.BankSelect,
        TemplateEventKind.Program => MappingEventKindV1.ProgramChange,
        TemplateEventKind.PitchBend => MappingEventKindV1.PitchBend,
        TemplateEventKind.RegisteredParameter => MappingEventKindV1.Rpn,
        TemplateEventKind.NonRegisteredParameter => MappingEventKindV1.Nrpn,
        TemplateEventKind.PitchBendRange => MappingEventKindV1.PitchBendRange,
        _ => MappingEventKindV1.Unknown
    };

    private static MappingEventKindV1 ToMappingEventKind(MidiValueKind kind) => kind switch
    {
        MidiValueKind.ControlChange => MappingEventKindV1.ControlChange,
        MidiValueKind.BankMsb or MidiValueKind.BankLsb => MappingEventKindV1.BankSelect,
        MidiValueKind.Program => MappingEventKindV1.ProgramChange,
        MidiValueKind.PitchBend => MappingEventKindV1.PitchBend,
        MidiValueKind.RegisteredParameter => MappingEventKindV1.Rpn,
        MidiValueKind.NonRegisteredParameter => MappingEventKindV1.Nrpn,
        MidiValueKind.PitchBendRangeSemitones or MidiValueKind.PitchBendRangeCents => MappingEventKindV1.PitchBendRange,
        _ => MappingEventKindV1.Unknown
    };

    private sealed class CanonicalComparer : IComparer<CanonicalMidiEvent>
    {
        public static CanonicalComparer Instance { get; } = new();
        public int Compare(CanonicalMidiEvent x, CanonicalMidiEvent y)
        {
            int value = x.Tick.CompareTo(y.Tick);
            if (value != 0) return value;
            value = x.Role.CompareTo(y.Role);
            if (value != 0) return value;
            value = x.ZeroBasedPort.CompareTo(y.ZeroBasedPort);
            if (value != 0) return value;
            value = x.ZeroBasedChannel.CompareTo(y.ZeroBasedChannel);
            if (value != 0) return value;
            return x.StableOrder.CompareTo(y.StableOrder);
        }
    }
}

internal static class SourceFingerprint
{
    private const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static long ForTrack(
        LogicalTrack track,
        IReadOnlyDictionary<MidoraId, EventInstrument> instruments,
        MidoraProject project)
    {
        ulong hash = Offset;
        Add(ref hash, project.TicksPerQuarterNote);
        AddState(ref hash, project.GlobalInitialState);
        AddState(ref hash, project.GlobalResetDefaults);
        Add(ref hash, track.Id);
        Add(ref hash, track.EventInstrumentId ?? default);
        HashSet<MidoraId> referencedInstrumentIds = [];
        if (track.EventInstrumentId.HasValue) referencedInstrumentIds.Add(track.EventInstrumentId.Value);
        foreach (Segment segment in track.Segments)
        {
            Add(ref hash, segment.Id);
            Add(ref hash, segment.ProjectStartTick);
            Add(ref hash, segment.LengthTicks);
            Add(ref hash, segment.ContentOffsetTick);
            foreach (LogicalNote note in segment.Notes)
            {
                Add(ref hash, note.Id);
                Add(ref hash, note.StartTick);
                Add(ref hash, note.LengthTicks);
                Add(ref hash, note.Note);
                Add(ref hash, note.Velocity);
            }
            foreach (LogicalParameterLane lane in segment.ParameterLanes)
            {
                Add(ref hash, lane.Id);
                Add(ref hash, lane.ParameterId);
                foreach (CurvePoint point in lane.Points)
                {
                    Add(ref hash, point.Id);
                    Add(ref hash, point.Tick);
                    Add(ref hash, BitConverter.DoubleToInt64Bits(point.Value));
                    Add(ref hash, (int)point.Interpolation);
                }
            }
        }
        foreach (MidoraId instrumentId in referencedInstrumentIds.OrderBy(value => value))
        {
            if (instruments.TryGetValue(instrumentId, out EventInstrument? instrument))
            {
                AddInstrument(ref hash, instrument);
            }
        }
        return unchecked((long)hash);
    }

    public static long ForResult(
        long start,
        long end,
        ReadOnlySpan<CanonicalMidiEvent> events,
        CanonicalConductor conductor)
    {
        ulong hash = Offset;
        Add(ref hash, start);
        Add(ref hash, end);
        Add(ref hash, conductor.EndMarkerTick ?? -1);
        Add(ref hash, conductor.EndMarker?.Id ?? default);
        foreach (CanonicalTempo value in conductor.Tempos)
        {
            Add(ref hash, value.SourceId);
            Add(ref hash, value.Tick);
            Add(ref hash, value.BeatsPerMinute);
            Add(ref hash, value.IsRangeRestore ? 1 : 0);
        }
        foreach (CanonicalTimeSignature value in conductor.TimeSignatures)
        {
            Add(ref hash, value.SourceId);
            Add(ref hash, value.Tick);
            Add(ref hash, value.Numerator);
            Add(ref hash, value.Denominator);
            Add(ref hash, value.IsRangeRestore ? 1 : 0);
        }
        foreach (CanonicalKeySignature value in conductor.KeySignatures)
        {
            Add(ref hash, value.SourceId);
            Add(ref hash, value.Tick);
            Add(ref hash, value.SharpsFlats);
            Add(ref hash, value.IsMinor ? 1 : 0);
            Add(ref hash, value.IsRangeRestore ? 1 : 0);
        }
        foreach (CanonicalMarker value in conductor.Markers)
        {
            Add(ref hash, value.Id);
            Add(ref hash, value.Tick);
            Add(ref hash, value.Name);
        }
        foreach (CanonicalMidiEvent value in events)
        {
            Add(ref hash, value.Tick);
            Add(ref hash, value.ZeroBasedPort);
            Add(ref hash, value.ZeroBasedChannel);
            Add(ref hash, unchecked((long)value.Message.PackedValue));
            Add(ref hash, (int)value.Role);
            Add(ref hash, value.StableOrder);
            Add(ref hash, value.SemanticTargetKey);
            Add(ref hash, value.SemanticGroup);
            Add(ref hash, value.Source.TrackId);
            Add(ref hash, value.Source.SegmentId);
            Add(ref hash, value.Source.LogicalNoteId);
            Add(ref hash, value.Source.EventInstrumentId);
            Add(ref hash, value.Source.SubVoiceId);
            Add(ref hash, value.Source.SourceEventId);
            Add(ref hash, value.Source.Tick);
        }
        return unchecked((long)hash);
    }

    private static void AddInstrument(ref ulong hash, EventInstrument instrument)
    {
        Add(ref hash, instrument.Id);
        // These display names are part of MappingContextV1 and can therefore affect
        // a free C# Mapping Function's returned value.
        Add(ref hash, instrument.Name);
        Add(ref hash, instrument.RootNote);
        Add(ref hash, instrument.TemplateLengthTicks);
        Add(ref hash, instrument.RequiresChannelIsolation ? 1 : 0);
        Add(ref hash, (int)instrument.OverlapPolicy);
        Add(ref hash, (int)instrument.OverlapScope);
        Add(ref hash, (int)instrument.ShortLifecycle);
        Add(ref hash, (int)instrument.LongLifecycle);
        Add(ref hash, instrument.LoopStartTick ?? -1);
        Add(ref hash, instrument.LoopEndTick ?? -1);
        AddState(ref hash, instrument.InitialState);
        foreach (LogicalParameterDefinition definition in instrument.LogicalParameters.OrderBy(value => value.Id))
        {
            Add(ref hash, definition.Id);
            Add(ref hash, definition.Name);
            Add(ref hash, (int)definition.Type);
            Add(ref hash, definition.Minimum);
            Add(ref hash, definition.Maximum);
            Add(ref hash, definition.DisplayMinimum);
            Add(ref hash, definition.DisplayMaximum);
            Add(ref hash, definition.DefaultValue);
            Add(ref hash, definition.UsesExplicitEnumValues ? 1 : 0);
            foreach (LogicalParameterEnumItem value in definition.EnumItems)
            {
                Add(ref hash, value.Id);
                Add(ref hash, value.Name);
                Add(ref hash, value.Value);
            }
        }
        foreach (SubVoice voice in instrument.SubVoices)
        {
            Add(ref hash, voice.Id);
            Add(ref hash, voice.Name ?? string.Empty);
            Add(ref hash, voice.RootNoteOverride ?? -1);
            AddState(ref hash, voice.InitialState);
            foreach (TemplateEvent value in voice.Events)
            {
                Add(ref hash, value.Id);
                Add(ref hash, (int)value.Kind);
                Add(ref hash, value.Tick);
                Add(ref hash, value.LengthTicks);
                Add(ref hash, value.Number);
                Add(ref hash, value.Value);
                Add(ref hash, value.SecondaryValue);
                if (value.Kind == TemplateEventKind.Bank)
                {
                    Add(ref hash, value.HasBankMsb ? 1 : 0);
                    Add(ref hash, value.HasBankLsb ? 1 : 0);
                }
                Add(ref hash, value.FollowPitchDelta ? 1 : 0);
                AddChain(ref hash, value.NumberMappings);
                AddTargetSettings(ref hash, value.NumberTargetSettings);
                AddChain(ref hash, value.ValueMappings);
                AddTargetSettings(ref hash, value.ValueTargetSettings);
                AddChain(ref hash, value.SecondaryValueMappings);
                AddTargetSettings(ref hash, value.SecondaryValueTargetSettings);
            }
            foreach (ValueCurve curve in voice.Curves)
            {
                Add(ref hash, curve.Id);
                Add(ref hash, (int)curve.Target.Kind);
                Add(ref hash, curve.Target.Number);
                AddTargetSettings(ref hash, curve.TargetSettings);
                foreach (CurvePoint point in curve.Points)
                {
                    Add(ref hash, point.Id);
                    Add(ref hash, point.Tick);
                    Add(ref hash, point.Value);
                    Add(ref hash, (int)point.Interpolation);
                }
            }
        }
        foreach (LogicalParameterMapping mapping in instrument.ParameterMappings)
        {
            Add(ref hash, mapping.Id);
            Add(ref hash, mapping.ParameterId);
            Add(ref hash, mapping.SubVoiceId);
            Add(ref hash, (int)mapping.Target.Kind);
            Add(ref hash, mapping.Target.Number);
            AddChain(ref hash, mapping.Steps);
            AddTargetSettings(ref hash, mapping.TargetSettings);
        }
        foreach (CSharpMappingFunction function in instrument.MappingFunctions.OrderBy(value => value.Id))
        {
            Add(ref hash, function.Id);
            Add(ref hash, function.Name);
            Add(ref hash, function.AbiVersion);
            Add(ref hash, function.Body);
            foreach (string field in function.DeclaredContextFields.Order(StringComparer.Ordinal)) Add(ref hash, field);
        }
        foreach (InstrumentEnvelope envelope in instrument.Envelopes)
        {
            Add(ref hash, envelope.Id);
            Add(ref hash, envelope.Name ?? string.Empty);
            Add(ref hash, envelope.DelayTicks);
            Add(ref hash, envelope.AttackTicks);
            Add(ref hash, envelope.HoldTicks);
            Add(ref hash, envelope.DecayTicks);
            Add(ref hash, envelope.ReleaseTicks);
            Add(ref hash, envelope.StartValue);
            Add(ref hash, envelope.PeakValue);
            Add(ref hash, envelope.SustainValue);
            Add(ref hash, envelope.EndValue);
        }
    }

    private static void AddState(ref ulong hash, MidiInitialState state)
    {
        Add(ref hash, state.BankMsb ?? -1);
        Add(ref hash, state.BankLsb ?? -1);
        Add(ref hash, state.Program ?? -1);
        Add(ref hash, state.PitchBend ?? int.MinValue);
        Add(ref hash, state.PitchBendRangeSemitones ?? -1);
        Add(ref hash, state.PitchBendRangeCents ?? -1);
        foreach ((int key, int value) in state.Controllers.OrderBy(value => value.Key))
        {
            Add(ref hash, key); Add(ref hash, value);
        }
        foreach ((int key, int value) in state.RegisteredParameters.OrderBy(value => value.Key))
        {
            Add(ref hash, key); Add(ref hash, value);
        }
        foreach ((int key, int value) in state.NonRegisteredParameters.OrderBy(value => value.Key))
        {
            Add(ref hash, key); Add(ref hash, value);
        }
    }

    private static void AddMapping(ref ulong hash, ValueMappingStep step)
    {
        Add(ref hash, step.Id);
        Add(ref hash, step.IsEnabled ? 1 : 0);
        Add(ref hash, (int)step.Source);
        Add(ref hash, (int)step.Operation);
        Add(ref hash, step.LogicalParameterId ?? default);
        Add(ref hash, step.EnvelopeId ?? default);
        Add(ref hash, step.MappingFunctionId ?? default);
        Add(ref hash, step.Constant);
        Add(ref hash, step.SourceMinimum);
        Add(ref hash, step.SourceMaximum);
        Add(ref hash, step.TargetMinimum);
        Add(ref hash, step.TargetMaximum);
        Add(ref hash, (int)step.InputOverflow);
        Add(ref hash, (int)step.DivideByZero);
    }

    private static void AddTargetSettings(ref ulong hash, MidiIntegerTargetSettings settings)
    {
        Add(ref hash, (int)settings.Rounding);
        Add(ref hash, (int)settings.Overflow);
    }

    private static void AddChain(ref ulong hash, MappingChain chain)
    {
        Add(ref hash, chain.Id);
        Add(ref hash, chain.IsEnabled ? 1 : 0);
        foreach (ValueMappingStep step in chain) AddMapping(ref hash, step);
    }

    private static void Add(ref ulong hash, MidoraId value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.Value.TryWriteBytes(bytes);
        foreach (byte item in bytes) Add(ref hash, item);
    }
    private static void Add(ref ulong hash, string value)
    {
        foreach (char item in value)
        {
            Add(ref hash, item);
        }
        Add(ref hash, 0);
    }
    private static void Add(ref ulong hash, long value)
    {
        ulong bits = unchecked((ulong)value);
        for (int i = 0; i < 8; i++)
        {
            Add(ref hash, (byte)(bits >> (i * 8)));
        }
    }
    private static void Add(ref ulong hash, int value) => Add(ref hash, (long)value);
    private static void Add(ref ulong hash, double value) => Add(ref hash, BitConverter.DoubleToInt64Bits(value));
    private static void Add(ref ulong hash, decimal value)
    {
        foreach (int bits in decimal.GetBits(value))
        {
            Add(ref hash, bits);
        }
    }
    private static void Add(ref ulong hash, byte value)
    {
        hash ^= value;
        hash *= Prime;
    }
}
