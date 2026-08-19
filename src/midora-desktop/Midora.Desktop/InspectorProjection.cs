using System.Globalization;
using Midora.Application;
using Midora.Domain;

namespace Midora.Desktop;

internal static class InspectorProjection
{
    public static void Rebuild(
        InspectorViewModel inspector,
        MidoraProject? project,
        WorkspaceViewModel? workspace)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        if (project is null || workspace is null)
        {
            inspector.Replace("No selection", "Select an object in the active Workspace.", []);
            return;
        }

        if (workspace.Selection.Ids.Count > 1)
        {
            RebuildMultiSelection(inspector, project, workspace);
            return;
        }

        MidoraId? selectedId = workspace.Selection.Primary;
        if (workspace is TimelineWorkspaceViewModel timeline)
        {
            RebuildTimeline(inspector, project, timeline, selectedId);
            return;
        }
        if (workspace is InstrumentWorkspaceViewModel instrumentWorkspace)
        {
            RebuildInstrument(inspector, project, instrumentWorkspace, selectedId);
            return;
        }

        inspector.Replace(
            workspace.Header,
            workspace.Kind.ToString(),
            [Field("workspace.kind", "WORKSPACE TYPE", workspace.Kind, false)]);
    }

    public static IProjectEditCommand CreateEditCommand(
        MidoraProject project,
        WorkspaceViewModel workspace,
        string key,
        string value)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        value ??= string.Empty;

        if (workspace.Selection.Ids.Count > 1)
        {
            return CreateMultiSelectionEditCommand(project, workspace, key, value);
        }

        if (key.StartsWith("segment.", StringComparison.Ordinal))
        {
            (LogicalTrack Track, Segment Segment) location = FindSegmentContext(project, workspace);
            Segment segment = location.Segment;
            long start = key == "segment.start" ? Long(value, "Project Start Tick") : segment.ProjectStartTick;
            long length = key == "segment.length" ? Long(value, "Length") : segment.LengthTicks;
            long offset = key == "segment.offset" ? Long(value, "Content Offset") : segment.ContentOffsetTick;
            return ProjectDomainEditCommands.SetSegmentWindow(segment.Id, start, length, offset);
        }

        if (key.StartsWith("note.", StringComparison.Ordinal))
        {
            (Segment segment, LogicalNote note) = FindLogicalNoteContext(project, workspace);
            return ProjectDomainEditCommands.UpdateLogicalNote(
                segment.Id,
                note.Id,
                key == "note.start" ? Long(value, "Start Tick") : note.StartTick,
                key == "note.length" ? Long(value, "Length") : note.LengthTicks,
                key == "note.number" ? Int(value, "MIDI Note") : note.Note,
                key == "note.velocity" ? Int(value, "Velocity") : note.Velocity);
        }

        if (key.StartsWith("parameterPoint.", StringComparison.Ordinal))
        {
            (Segment segment, LogicalParameterLane lane, CurvePoint point) =
                FindLogicalParameterPointContext(project, workspace);
            CurveInterpolation interpolation = key == "parameterPoint.interpolation"
                ? EnumValue<CurveInterpolation>(value, "Interpolation")
                : point.Interpolation;
            return ProjectDomainEditCommands.UpdateLogicalParameterPoint(
                segment.Id,
                lane.Id,
                point.Id,
                key == "parameterPoint.tick" ? Long(value, "Tick") : point.Tick,
                key == "parameterPoint.value" ? Double(value, "Value") : point.Value,
                interpolation);
        }

        if (workspace is InstrumentWorkspaceViewModel instrumentWorkspace
            && instrumentWorkspace.ObjectId is MidoraId instrumentId)
        {
            EventInstrument instrument = project.EventInstruments.Single(item => item.Id == instrumentId);
            return key switch
            {
                "instrument.name" => ProjectDomainEditCommands.RenameEventInstrument(instrumentId, value),
                "instrument.description" => ProjectDomainEditCommands.UpdateEventInstrumentDescription(instrumentId, value),
                "instrument.root" => ProjectDomainEditCommands.UpdateEventInstrumentRootNote(instrumentId, Int(value, "Root Note")),
                "instrument.templateLength" => ProjectDomainEditCommands.UpdateEventInstrumentTemplateLength(instrumentId, Long(value, "Template Length")),
                "instrument.isolation" => ProjectDomainEditCommands.UpdateEventInstrumentIsolation(instrumentId, Bool(value, "Channel Isolation")),
                "instrument.overlapPolicy" or "instrument.overlapScope" =>
                    ProjectDomainEditCommands.UpdateEventInstrumentOverlap(
                        instrumentId,
                        key == "instrument.overlapPolicy" ? EnumValue<OverlapPolicy>(value, "Overlap Policy") : instrument.OverlapPolicy,
                        key == "instrument.overlapScope" ? EnumValue<OverlapScope>(value, "Overlap Scope") : instrument.OverlapScope),
                "instrument.shortLifecycle" or "instrument.longLifecycle" =>
                    ProjectDomainEditCommands.UpdateEventInstrumentLifecycle(
                        instrumentId,
                        key == "instrument.shortLifecycle" ? EnumValue<ShortNoteLifecycle>(value, "Short Note Lifecycle") : instrument.ShortLifecycle,
                        key == "instrument.longLifecycle" ? EnumValue<LongNoteLifecycle>(value, "Long Note Lifecycle") : instrument.LongLifecycle),
                "instrument.loopStart" or "instrument.loopEnd" =>
                    CreateLoopEdit(instrument, key, value),
                _ when key.StartsWith("instrument.initial.", StringComparison.Ordinal) =>
                    ProjectDomainEditCommands.UpdateEventInstrumentInitialStateValue(
                        instrument.Id,
                        ParseInitialStateTarget(key["instrument.initial.".Length..]),
                        NullableInt(value, "Initial State Value")),
                _ when key.StartsWith("subvoice.", StringComparison.Ordinal) =>
                    CreateSubVoiceEdit(instrument, instrumentWorkspace.Selection.Primary, key, value),
                _ when key.StartsWith("parameter.", StringComparison.Ordinal) =>
                    CreateLogicalParameterEdit(instrument, instrumentWorkspace.Selection.Primary, key, value),
                _ when key.StartsWith("valueCurvePoint.", StringComparison.Ordinal) =>
                    CreateValueCurvePointEdit(instrument, instrumentWorkspace.Selection.Primary, key, value),
                _ when key.StartsWith("parameterMapping.", StringComparison.Ordinal) =>
                    CreateParameterMappingEdit(instrument, instrumentWorkspace.Selection.Primary, key, value),
                _ when key.StartsWith("mappingChain.", StringComparison.Ordinal) =>
                    CreateMappingChainEdit(instrument, instrumentWorkspace.Selection.Primary, key, value),
                _ when key.StartsWith("mappingStep.", StringComparison.Ordinal) =>
                    CreateMappingStepEdit(instrument, instrumentWorkspace.Selection.Primary, key, value),
                _ when key.StartsWith("envelope.", StringComparison.Ordinal) =>
                    CreateEnvelopeEdit(instrument, instrumentWorkspace.Selection.Primary, key, value),
                _ when key.StartsWith("template.", StringComparison.Ordinal) =>
                    CreateTemplateEventEdit(project, instrument, instrumentWorkspace, key, value),
                _ => throw new InvalidOperationException("This Inspector property is read-only.")
            };
        }

        if (workspace is TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Conductor })
        {
            return CreateConductorEdit(project, workspace.Selection.Primary, key, value);
        }

        throw new InvalidOperationException("This Inspector property is read-only.");
    }

    private static void RebuildMultiSelection(
        InspectorViewModel inspector,
        MidoraProject project,
        WorkspaceViewModel workspace)
    {
        MidoraId[] ids = workspace.Selection.Ids.ToArray();
        if (workspace is TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Segment } timeline
            && TimelineWorkspaceViewModel.FindSegment(project, timeline.ObjectId) is { } location)
        {
            LogicalNote[] notes = location.Segment.Notes
                .Where(item => workspace.Selection.Ids.Contains(item.Id))
                .ToArray();
            if (notes.Length == ids.Length)
            {
                inspector.Replace(
                    $"{notes.Length} Logical Notes",
                    "Common fields use one atomic Exact Set operation. Mixed is distinct from Unavailable.",
                    [BatchField("batch.note.start", "START TICK", notes, item => item.StartTick),
                     BatchField("batch.note.length", "LENGTH TICKS", notes, item => item.LengthTicks),
                     BatchField("batch.note.number", "MIDI NOTE", notes, item => item.Note),
                     BatchField("batch.note.velocity", "VELOCITY", notes, item => item.Velocity)]);
                return;
            }

            if (TryFindLogicalParameterPointBatch(location.Segment, workspace.Selection.Ids)
                is { } pointBatch)
            {
                inspector.Replace(
                    $"{pointBatch.Points.Length} Logical Parameter Points",
                    "All selected points share one Definition, value domain, and lane.",
                    [BatchField("batch.parameterPoint.tick", "TICK", pointBatch.Points, item => item.Tick),
                     BatchField("batch.parameterPoint.value", "VALUE", pointBatch.Points, item => item.Value),
                     BatchField("batch.parameterPoint.interpolation", "INTERPOLATION", pointBatch.Points, item => item.Interpolation)]);
                return;
            }
        }

        if (workspace is InstrumentWorkspaceViewModel instrumentWorkspace
            && instrumentWorkspace.ObjectId is MidoraId instrumentId
            && project.EventInstruments.FirstOrDefault(item => item.Id == instrumentId) is EventInstrument instrument
            && TryFindValueCurvePointBatch(instrument, workspace.Selection.Ids) is { } curveBatch)
        {
            inspector.Replace(
                $"{curveBatch.Points.Length} Value Curve Points",
                "All selected points share one SubVoice, target, value domain, and curve.",
                [BatchField("batch.valueCurvePoint.tick", "TICK", curveBatch.Points, item => item.Tick),
                 BatchField("batch.valueCurvePoint.value", "VALUE", curveBatch.Points, item => item.Value),
                 BatchField("batch.valueCurvePoint.interpolation", "INTERPOLATION", curveBatch.Points, item => item.Interpolation)]);
            return;
        }

        inspector.Replace(
            $"{ids.Length} objects selected",
            "The selected objects do not expose a field with identical semantics and a safe atomic batch edit.",
            [new InspectorField(
                "selection.unavailable",
                "COMMON EDITABLE FIELDS",
                "Unavailable",
                false,
                InspectorFieldValueState.Unavailable)]);
    }

    private static IProjectEditCommand CreateMultiSelectionEditCommand(
        MidoraProject project,
        WorkspaceViewModel workspace,
        string key,
        string value)
    {
        MidoraId[] ids = workspace.Selection.Ids.ToArray();
        if (workspace is TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Segment } timeline
            && TimelineWorkspaceViewModel.FindSegment(project, timeline.ObjectId) is { } location)
        {
            if (location.Segment.Notes.Count(item => workspace.Selection.Ids.Contains(item.Id)) == ids.Length)
            {
                return key switch
                {
                    "batch.note.start" => ProjectDomainEditCommands.SetLogicalNoteValues(
                        location.Segment.Id, ids, startTick: Long(value, "Start Tick")),
                    "batch.note.length" => ProjectDomainEditCommands.SetLogicalNoteValues(
                        location.Segment.Id, ids, lengthTicks: Long(value, "Length")),
                    "batch.note.number" => ProjectDomainEditCommands.SetLogicalNoteValues(
                        location.Segment.Id, ids, note: Int(value, "MIDI Note")),
                    "batch.note.velocity" => ProjectDomainEditCommands.SetLogicalNoteValues(
                        location.Segment.Id, ids, velocity: Int(value, "Velocity")),
                    _ => throw new InvalidOperationException("This batch field is read-only.")
                };
            }

            if (TryFindLogicalParameterPointBatch(location.Segment, workspace.Selection.Ids)
                is { } pointBatch)
            {
                return key switch
                {
                    "batch.parameterPoint.tick" => ProjectDomainEditCommands.SetLogicalParameterPoints(
                        location.Segment.Id, pointBatch.Lane.Id, ids, tick: Long(value, "Tick")),
                    "batch.parameterPoint.value" => ProjectDomainEditCommands.SetLogicalParameterPoints(
                        location.Segment.Id, pointBatch.Lane.Id, ids, value: Double(value, "Value")),
                    "batch.parameterPoint.interpolation" => ProjectDomainEditCommands.SetLogicalParameterPoints(
                        location.Segment.Id, pointBatch.Lane.Id, ids,
                        interpolation: EnumValue<CurveInterpolation>(value, "Interpolation")),
                    _ => throw new InvalidOperationException("This batch field is read-only.")
                };
            }
        }

        if (workspace is InstrumentWorkspaceViewModel instrumentWorkspace
            && instrumentWorkspace.ObjectId is MidoraId instrumentId
            && project.EventInstruments.FirstOrDefault(item => item.Id == instrumentId) is EventInstrument instrument
            && TryFindValueCurvePointBatch(instrument, workspace.Selection.Ids) is { } curveBatch)
        {
            return key switch
            {
                "batch.valueCurvePoint.tick" => ProjectDomainEditCommands.SetValueCurvePoints(
                    instrument.Id, curveBatch.Voice.Id, curveBatch.Curve.Id, ids,
                    tick: Long(value, "Tick")),
                "batch.valueCurvePoint.value" => ProjectDomainEditCommands.SetValueCurvePoints(
                    instrument.Id, curveBatch.Voice.Id, curveBatch.Curve.Id, ids,
                    value: Double(value, "Value")),
                "batch.valueCurvePoint.interpolation" => ProjectDomainEditCommands.SetValueCurvePoints(
                    instrument.Id, curveBatch.Voice.Id, curveBatch.Curve.Id, ids,
                    interpolation: EnumValue<CurveInterpolation>(value, "Interpolation")),
                _ => throw new InvalidOperationException("This batch field is read-only.")
            };
        }

        throw new InvalidOperationException(
            "The current selection has no common field that can be edited atomically.");
    }

    private static LogicalParameterPointBatch? TryFindLogicalParameterPointBatch(
        Segment segment,
        IReadOnlyCollection<MidoraId> ids)
    {
        foreach (LogicalParameterLane lane in segment.ParameterLanes)
        {
            CurvePoint[] points = lane.Points.Where(item => ids.Contains(item.Id)).ToArray();
            if (points.Length == ids.Count) return new(lane, points);
        }
        return null;
    }

    private static ValueCurvePointBatch? TryFindValueCurvePointBatch(
        EventInstrument instrument,
        IReadOnlyCollection<MidoraId> ids)
    {
        foreach (SubVoice voice in instrument.SubVoices)
        {
            foreach (ValueCurve curve in voice.Curves)
            {
                CurvePoint[] points = curve.Points.Where(item => ids.Contains(item.Id)).ToArray();
                if (points.Length == ids.Count) return new(voice, curve, points);
            }
        }
        return null;
    }

    private static InspectorField BatchField<TItem, TValue>(
        string key,
        string label,
        IReadOnlyList<TItem> items,
        Func<TItem, TValue> selector)
    {
        TValue first = selector(items[0]);
        bool same = items.Skip(1).All(item => EqualityComparer<TValue>.Default.Equals(first, selector(item)));
        IReadOnlyList<string>? options = typeof(TValue).IsEnum
            ? Enum.GetNames(typeof(TValue))
            : null;
        return new(
            key,
            label,
            same
                ? Convert.ToString(first, CultureInfo.InvariantCulture) ?? string.Empty
                : "Mixed",
            true,
            same ? InspectorFieldValueState.SameValue : InspectorFieldValueState.Mixed,
            options,
            typeof(TValue) == typeof(bool));
    }

    private sealed record LogicalParameterPointBatch(
        LogicalParameterLane Lane,
        CurvePoint[] Points);

    private sealed record ValueCurvePointBatch(
        SubVoice Voice,
        ValueCurve Curve,
        CurvePoint[] Points);

    private static void RebuildTimeline(
        InspectorViewModel inspector,
        MidoraProject project,
        TimelineWorkspaceViewModel workspace,
        MidoraId? selectedId)
    {
        if (workspace.Mode == TimelineWorkspaceMode.Arrangement && selectedId is MidoraId segmentId)
        {
            (LogicalTrack Track, Segment Segment)? location = TimelineWorkspaceViewModel.FindSegment(project, segmentId);
            if (location is not null)
            {
                Segment segment = location.Value.Segment;
                inspector.Replace(
                    "Segment",
                    TimelineWorkspaceViewModel.TrackDisplayName(project, location.Value.Track),
                    [Field("segment.start", "PROJECT START TICK", segment.ProjectStartTick),
                     Field("segment.length", "LENGTH TICKS", segment.LengthTicks),
                     Field("segment.offset", "CONTENT OFFSET TICK", segment.ContentOffsetTick),
                     Field("object.id", "STABLE ID", segment.Id.Value, false)]);
                return;
            }
            if (TimelineWorkspaceViewModel.FindMidiSegment(project, segmentId) is { } midiLocation)
            {
                MidiSegment segment = midiLocation.Segment;
                inspector.Replace(
                    "MIDI Segment",
                    string.IsNullOrWhiteSpace(midiLocation.Track.Name)
                        ? "Unnamed MIDI Track"
                        : midiLocation.Track.Name,
                    [Field("midiSegment.start", "PROJECT START TICK", segment.ProjectStartTick, false),
                     Field("midiSegment.length", "LENGTH TICKS", segment.LengthTicks, false),
                     Field("midiSegment.offset", "CONTENT OFFSET TICK", segment.ContentOffsetTick, false),
                     Field("object.id", "STABLE ID", segment.Id.Value, false)]);
                return;
            }
        }

        if (workspace.Mode == TimelineWorkspaceMode.Segment)
        {
            (LogicalTrack Track, Segment Segment)? location =
                TimelineWorkspaceViewModel.FindSegment(project, workspace.ObjectId);
            if (location is not null && selectedId is MidoraId noteId)
            {
                LogicalNote? note = location.Value.Segment.Notes.FirstOrDefault(item => item.Id == noteId);
                if (note is not null)
                {
                    inspector.Replace(
                        "Logical Note",
                        $"{TimelineWorkspaceViewModel.MidiNoteName(note.Note)} in {workspace.Header}",
                        [Field("note.start", "START TICK", note.StartTick),
                         Field("note.length", "LENGTH TICKS", note.LengthTicks),
                         Field("note.number", "MIDI NOTE", note.Note),
                         Field("note.velocity", "VELOCITY", note.Velocity),
                         Field("object.id", "STABLE ID", note.Id.Value, false)]);
                    return;
                }
                LogicalParameterLane? lane = location.Value.Segment.ParameterLanes
                    .FirstOrDefault(candidate => candidate.Points.Any(point => point.Id == noteId));
                CurvePoint? point = lane?.Points.FirstOrDefault(candidate => candidate.Id == noteId);
                if (lane is not null && point is not null)
                {
                    EventInstrument? instrument = location.Value.Track.EventInstrumentId is MidoraId instrumentId
                        ? project.EventInstruments.FirstOrDefault(item => item.Id == instrumentId)
                        : null;
                    LogicalParameterDefinition? definition = instrument?.LogicalParameters
                        .FirstOrDefault(item => item.Id == lane.ParameterId);
                    inspector.Replace(
                        "Logical Parameter Point",
                        definition is null ? "Missing parameter definition" : definition.Name,
                        [Field("parameterPoint.tick", "TICK", point.Tick),
                         Field("parameterPoint.value", "VALUE", point.Value),
                         Field("parameterPoint.interpolation", "INTERPOLATION", point.Interpolation),
                         Field("object.id", "STABLE ID", point.Id.Value, false)]);
                    return;
                }
            }
            if (location is not null)
            {
                Segment segment = location.Value.Segment;
                inspector.Replace(
                    "Segment Window",
                    TimelineWorkspaceViewModel.TrackDisplayName(project, location.Value.Track),
                    [Field("segment.start", "PROJECT START TICK", segment.ProjectStartTick),
                     Field("segment.length", "LENGTH TICKS", segment.LengthTicks),
                     Field("segment.offset", "CONTENT OFFSET TICK", segment.ContentOffsetTick)]);
                return;
            }

            if (TimelineWorkspaceViewModel.FindMidiSegment(project, workspace.ObjectId) is { } midiLocation)
            {
                MidiSegment segment = midiLocation.Segment;
                if (selectedId is MidoraId midiObjectId)
                {
                    if (segment.Notes.FirstOrDefault(item => item.Id == midiObjectId)
                        is DirectMidiNote note)
                    {
                        inspector.Replace(
                            "Direct MIDI Note",
                            $"{TimelineWorkspaceViewModel.MidiNoteName(note.Key)} in {workspace.Header}",
                            [Field("midiNote.start", "START TICK", note.StartTick, false),
                             Field("midiNote.length", "LENGTH TICKS", note.LengthTicks, false),
                             Field("midiNote.key", "KEY NUMBER", note.Key, false),
                             Field("midiNote.onVelocity", "NOTE ON VELOCITY", note.NoteOnVelocity, false),
                             Field("midiNote.offVelocity", "NOTE OFF VELOCITY", note.NoteOffVelocity, false),
                             Field("midiNote.onOrder", "NOTE ON ORDER", note.NoteOnOrder, false),
                             Field("midiNote.offOrder", "NOTE OFF ORDER", note.NoteOffOrder, false),
                             Field("object.id", "STABLE ID", note.Id.Value, false)]);
                        return;
                    }
                    if (segment.ChannelEvents.FirstOrDefault(item => item.Id == midiObjectId)
                        is DirectMidiChannelEvent channelEvent)
                    {
                        inspector.Replace(
                            "Direct MIDI Event",
                            TimelineWorkspaceViewModel.DirectMidiLaneLabel(
                                TimelineWorkspaceViewModel.ToDirectMidiLaneTarget(channelEvent)),
                            [Field("midiEvent.tick", "TICK", channelEvent.Tick, false),
                             Field("midiEvent.kind", "EVENT KIND", channelEvent.Kind, false),
                             Field("midiEvent.data1", "DATA 1", channelEvent.Data1, false),
                             Field("midiEvent.data2", "DATA 2", channelEvent.Data2, false),
                             Field("midiEvent.order", "TRACK EVENT ORDER", channelEvent.Order, false),
                             Field("object.id", "STABLE ID", channelEvent.Id.Value, false)]);
                        return;
                    }
                    if (segment.OpaqueEvents.FirstOrDefault(item => item.Id == midiObjectId)
                        is OpaqueMidiEvent opaque)
                    {
                        string payloadPreview = Convert.ToHexString(
                            opaque.Payload.AsSpan(0, Math.Min(opaque.Payload.Length, 256)));
                        if (opaque.Payload.Length > 256) payloadPreview += "…";
                        inspector.Replace(
                            "Imported MIDI Event",
                            TimelineWorkspaceViewModel.OpaqueMidiEventLabel(opaque),
                            [Field("opaqueMidi.tick", "TICK", opaque.Tick, false),
                             Field("opaqueMidi.kind", "EVENT KIND", opaque.Kind, false),
                             Field("opaqueMidi.metaType", "META TYPE", $"0x{opaque.MetaType:X2}", false),
                             Field("opaqueMidi.payloadLength", "PAYLOAD BYTES", opaque.Payload.Length, false),
                             Field("opaqueMidi.payload", "PAYLOAD HEX PREVIEW", payloadPreview, false),
                             Field("opaqueMidi.order", "TRACK EVENT ORDER", opaque.Order, false),
                             Field("object.id", "STABLE ID", opaque.Id.Value, false)]);
                        return;
                    }
                }
                inspector.Replace(
                    "MIDI Segment Window",
                    string.IsNullOrWhiteSpace(midiLocation.Track.Name)
                        ? "Unnamed MIDI Track"
                        : midiLocation.Track.Name,
                    [Field("midiSegment.start", "PROJECT START TICK", segment.ProjectStartTick, false),
                     Field("midiSegment.length", "LENGTH TICKS", segment.LengthTicks, false),
                     Field("midiSegment.offset", "CONTENT OFFSET TICK", segment.ContentOffsetTick, false)]);
                return;
            }
        }

        if (workspace.Mode == TimelineWorkspaceMode.Conductor && selectedId is MidoraId conductorId)
        {
            if (project.Conductor.Tempos.FirstOrDefault(item => item.Id == conductorId) is TempoChange tempo)
            {
                inspector.Replace("Tempo", "Conductor event", [
                    Field("conductor.tick", "TICK", tempo.Tick),
                    Field("conductor.bpm", "BEATS PER MINUTE", tempo.BeatsPerMinute)]);
                return;
            }
            if (project.Conductor.TimeSignatures.FirstOrDefault(item => item.Id == conductorId) is TimeSignatureChange signature)
            {
                inspector.Replace("Time Signature", "Conductor event", [
                    Field("conductor.tick", "TICK", signature.Tick),
                    Field("conductor.numerator", "NUMERATOR", signature.Numerator),
                    Field("conductor.denominator", "DENOMINATOR", signature.Denominator)]);
                return;
            }
            if (project.Conductor.KeySignatures.FirstOrDefault(item => item.Id == conductorId) is KeySignatureChange key)
            {
                inspector.Replace("Key Signature", "Conductor event", [
                    Field("conductor.tick", "TICK", key.Tick),
                    Field("conductor.sharpsFlats", "SHARPS / FLATS", key.SharpsFlats),
                    Field("conductor.isMinor", "IS MINOR", key.IsMinor)]);
                return;
            }
            if (project.Conductor.Markers.FirstOrDefault(item => item.Id == conductorId) is ProjectMarker marker)
            {
                inspector.Replace("Project Marker", "Conductor event", [
                    Field("conductor.tick", "TICK", marker.Tick),
                    Field("conductor.name", "NAME", marker.Name)]);
                return;
            }
            if (project.Conductor.EndMarker is ProjectEndMarker end && end.Id == conductorId)
            {
                inspector.Replace("Project End Marker", "Hard Project boundary", [
                    Field("conductor.tick", "TICK", end.Tick)]);
                return;
            }
        }

        inspector.Replace(workspace.Header, workspace.Context, [
            Field("viewport.start", "VIEW START TICK", workspace.StartTick, false),
            Field("viewport.span", "VISIBLE TICK SPAN", workspace.TickSpan, false)]);
    }

    private static void RebuildInstrument(
        InspectorViewModel inspector,
        MidoraProject project,
        InstrumentWorkspaceViewModel workspace,
        MidoraId? selectedId)
    {
        EventInstrument? instrument = project.EventInstruments.FirstOrDefault(item => item.Id == workspace.ObjectId);
        if (instrument is null)
        {
            inspector.Replace("Missing Event Instrument", "The referenced object no longer exists.", []);
            return;
        }
        if (selectedId is MidoraId eventId)
        {
            if (instrument.SubVoices.FirstOrDefault(item => item.Id == eventId) is SubVoice selectedVoice)
            {
                inspector.Replace(
                    string.IsNullOrWhiteSpace(selectedVoice.Name) ? "SubVoice" : selectedVoice.Name,
                    "SubVoice definition",
                    [Field("subvoice.name", "NAME", selectedVoice.Name ?? string.Empty),
                     Field("subvoice.root", "ROOT NOTE OVERRIDE", selectedVoice.RootNoteOverride?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                     StateField("subvoice.initial.bankMsb", "INITIAL BANK MSB", selectedVoice.InitialState.BankMsb),
                     StateField("subvoice.initial.bankLsb", "INITIAL BANK LSB", selectedVoice.InitialState.BankLsb),
                     StateField("subvoice.initial.program", "INITIAL PROGRAM (0–127)", selectedVoice.InitialState.Program),
                     StateField("subvoice.initial.pitchBend", "INITIAL PITCH BEND", selectedVoice.InitialState.PitchBend),
                     StateField("subvoice.initial.pitchRangeSemitones", "INITIAL PITCH RANGE SEMITONES", selectedVoice.InitialState.PitchBendRangeSemitones),
                     StateField("subvoice.initial.pitchRangeCents", "INITIAL PITCH RANGE CENTS", selectedVoice.InitialState.PitchBendRangeCents),
                     Field("subvoice.events", "TEMPLATE EVENT COUNT", selectedVoice.Events.Count, false),
                     Field("subvoice.curves", "VALUE CURVE COUNT", selectedVoice.Curves.Count, false),
                     .. ExtraStateFields("subvoice.initial", selectedVoice.InitialState),
                     Field("object.id", "STABLE ID", selectedVoice.Id.Value, false)]);
                return;
            }
            if (instrument.LogicalParameters.FirstOrDefault(item => item.Id == eventId) is LogicalParameterDefinition parameter)
            {
                inspector.Replace(
                    parameter.Name,
                    $"Logical Parameter · {parameter.Type}",
                    [Field("parameter.name", "NAME", parameter.Name),
                     Field("parameter.type", "TYPE", parameter.Type, false),
                     Field("parameter.minimum", "LEGAL MINIMUM", parameter.Minimum),
                     Field("parameter.maximum", "LEGAL MAXIMUM", parameter.Maximum),
                     Field("parameter.default", "DEFAULT VALUE", parameter.DefaultValue),
                     Field("parameter.displayMinimum", "DISPLAY MINIMUM", parameter.DisplayMinimum),
                     Field("parameter.displayMaximum", "DISPLAY MAXIMUM", parameter.DisplayMaximum),
                     Field("parameter.enumCount", "ENUM ITEM COUNT", parameter.EnumItems.Count, false),
                     Field("object.id", "STABLE ID", parameter.Id.Value, false)]);
                return;
            }
            if (instrument.MappingFunctions.FirstOrDefault(item => item.Id == eventId) is CSharpMappingFunction function)
            {
                inspector.Replace(
                    function.Name,
                    "C# Mapping Function",
                    [Field("mapping.abi", "ABI VERSION", function.AbiVersion, false),
                     Field("mapping.context", "DECLARED CONTEXT FIELDS", string.Join(", ", function.DeclaredContextFields), false),
                     Field("mapping.open", "EDITING", "Double-click the function to open its Draft Workspace.", false),
                     Field("object.id", "STABLE ID", function.Id.Value, false)]);
                return;
            }
            if (instrument.ParameterMappings.FirstOrDefault(item => item.Id == eventId) is LogicalParameterMapping mapping)
            {
                string sourceName = instrument.LogicalParameters.FirstOrDefault(item => item.Id == mapping.ParameterId)?.Name
                    ?? $"Broken reference {mapping.ParameterId}";
                SubVoice? targetVoice = instrument.SubVoices.FirstOrDefault(item => item.Id == mapping.SubVoiceId);
                inspector.Replace(
                    "Logical Parameter Mapping",
                    $"{sourceName} → {(targetVoice?.Name ?? mapping.SubVoiceId.ToString())} · {FormatMidiTarget(mapping.Target)}",
                    [ChoiceField(
                         "parameterMapping.source",
                         "SOURCE PARAMETER",
                         mapping.ParameterId.Value.ToString(CultureInfo.InvariantCulture),
                         IdChoices(
                             instrument.LogicalParameters.Select(item => (item.Id, item.Name)),
                             mapping.ParameterId,
                             "Missing Logical Parameter")),
                     ChoiceField(
                         "parameterMapping.subVoice",
                         "TARGET SUBVOICE",
                         mapping.SubVoiceId.Value.ToString(CultureInfo.InvariantCulture),
                         IdChoices(
                             instrument.SubVoices.Select((item, index) => (
                                 item.Id,
                                 string.IsNullOrWhiteSpace(item.Name)
                                     ? $"SubVoice {index + 1}"
                                     : item.Name)),
                             mapping.SubVoiceId,
                             "Missing SubVoice")),
                     Field("parameterMapping.target", "MIDI TARGET · use Edit… to change", FormatMidiTarget(mapping.Target), false),
                     Field("parameterMapping.rounding", "FINAL ROUNDING", mapping.TargetSettings.Rounding),
                     Field("parameterMapping.overflow", "FINAL OVERFLOW", mapping.TargetSettings.Overflow),
                     Field("parameterMapping.steps", "MAPPING CHAIN STEPS", mapping.Steps.Count, false),
                     Field("object.id", "STABLE ID", mapping.Id.Value, false)]);
                return;
            }
            if (workspace.MappingChains.Any(item => item.Id == eventId))
            {
                MappingChainEditingContext chainContext = MappingEditingPolicy.Resolve(
                    instrument,
                    eventId);
                string owner = workspace.MappingChains.Single(item => item.Id == eventId).Owner;
                MidiIntegerTargetSettings settings = chainContext.TargetSettings;
                inspector.Replace(
                    "Mapping Chain",
                    owner,
                    [Field("mappingChain.enabled", "ENABLED", chainContext.Chain.IsEnabled),
                     Field(
                         "mappingChain.ownerKind",
                         "OWNER KIND",
                         chainContext.IsLogicalParameterMapping
                             ? "Logical Parameter Mapping"
                             : "SubVoice Event Mapping",
                         false),
                     Field(
                         "mappingChain.target",
                         "TARGET",
                         chainContext.ParameterMapping is LogicalParameterMapping parameterOwner
                             ? FormatMidiTarget(parameterOwner.Target)
                             : FormatEventMappingTarget(chainContext.EventMapping!.Target),
                         false),
                     Field("mappingChain.rounding", "FINAL ROUNDING", settings.Rounding),
                     Field("mappingChain.overflow", "FINAL OVERFLOW", settings.Overflow),
                     Field("mappingChain.steps", "STEP COUNT", chainContext.Chain.Count, false),
                     Field("object.id", "STABLE ID", chainContext.Chain.Id.Value, false)]);
                return;
            }
            if (FindMappingStep(instrument, eventId) is MappingStepContext mappingStep)
            {
                ValueMappingStep step = mappingStep.Step;
                MappingChainEditingContext chainContext = MappingEditingPolicy.Resolve(
                    instrument,
                    mappingStep.Chain.Id);
                MappingSource[] sources = MappingEditingPolicy.AllowedSources(instrument, chainContext)
                    .Append(step.Source)
                    .Distinct()
                    .ToArray();
                MappingOperation[] operations = Enum.GetValues<MappingOperation>()
                    .Where(value => value != MappingOperation.CustomCSharp
                        || MappingEditingPolicy.AllowedFunctions(instrument, chainContext).Count != 0
                        || value == step.Operation)
                    .ToArray();
                List<InspectorField> fields =
                [
                    Field("mappingStep.enabled", "ENABLED", step.IsEnabled),
                    ChoiceField(
                        "mappingStep.source",
                        "SOURCE",
                        step.Source.ToString(),
                        sources.Select(source => new InspectorChoiceOption(
                            source.ToString(),
                            source.ToString()))),
                    ChoiceField(
                        "mappingStep.operation",
                        "OPERATION",
                        step.Operation.ToString(),
                        operations.Select(operation => new InspectorChoiceOption(
                            operation.ToString(),
                            operation.ToString())))
                ];
                if (step.Source == MappingSource.LogicalParameter)
                {
                    fields.Add(ChoiceField(
                        "mappingStep.logicalParameter",
                        "LOGICAL PARAMETER",
                        step.LogicalParameterId?.Value.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                        OptionalIdChoices(
                            instrument.LogicalParameters.Select(item => (item.Id, item.Name)),
                            step.LogicalParameterId,
                            "Missing Logical Parameter")));
                }
                if (step.Source == MappingSource.Envelope)
                {
                    fields.Add(ChoiceField(
                        "mappingStep.envelope",
                        "ENVELOPE PRESET",
                        step.EnvelopeId?.Value.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                        OptionalIdChoices(
                            instrument.Envelopes.Select(item => (
                                item.Id,
                                string.IsNullOrWhiteSpace(item.Name) ? "Envelope Preset" : item.Name)),
                            step.EnvelopeId,
                            "Missing Envelope")));
                }
                if (step.Operation == MappingOperation.CustomCSharp)
                {
                    fields.Add(ChoiceField(
                        "mappingStep.function",
                        "C# MAPPING FUNCTION",
                        step.MappingFunctionId?.Value.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                        OptionalIdChoices(
                            MappingEditingPolicy.AllowedFunctions(instrument, chainContext)
                                .Select(item => (item.Id, item.Name)),
                            step.MappingFunctionId,
                            "Missing or illegal Mapping Function")));
                }
                fields.AddRange(
                [
                    Field("mappingStep.constant", "CONSTANT", step.Constant),
                    Field("mappingStep.sourceMinimum", "SOURCE MINIMUM", step.SourceMinimum),
                    Field("mappingStep.sourceMaximum", "SOURCE MAXIMUM", step.SourceMaximum),
                    Field("mappingStep.targetMinimum", "TARGET MINIMUM", step.TargetMinimum),
                    Field("mappingStep.targetMaximum", "TARGET MAXIMUM", step.TargetMaximum),
                    Field("mappingStep.inputOverflow", "INPUT OVERFLOW", step.InputOverflow),
                    Field("mappingStep.divideByZero", "DIVIDE BY ZERO", step.DivideByZero),
                    Field("mappingStep.chain", "CHAIN STABLE ID", mappingStep.Chain.Id.Value, false),
                    Field("object.id", "STABLE ID", step.Id.Value, false)
                ]);
                inspector.Replace(
                    $"Mapping Step {mappingStep.Index + 1}",
                    $"Ordered Mapping Chain {mappingStep.Chain.Id.Value}",
                    fields);
                return;
            }
            if (instrument.Envelopes.FirstOrDefault(item => item.Id == eventId) is InstrumentEnvelope envelope)
            {
                bool canEditEnvelope = instrument.RequiresChannelIsolation;
                inspector.Replace(
                    string.IsNullOrWhiteSpace(envelope.Name) ? "Envelope Preset" : envelope.Name,
                    instrument.RequiresChannelIsolation
                        ? "Fixed ADSR-like lifecycle envelope"
                        : "Incompatible while Per-Note Instance Isolation is disabled",
                    [Field("envelope.name", "NAME", envelope.Name ?? string.Empty, canEditEnvelope),
                     Field("envelope.delay", "DELAY TICKS", envelope.DelayTicks, canEditEnvelope),
                     Field("envelope.attack", "ATTACK TICKS", envelope.AttackTicks, canEditEnvelope),
                     Field("envelope.hold", "HOLD TICKS", envelope.HoldTicks, canEditEnvelope),
                     Field("envelope.decay", "DECAY TICKS", envelope.DecayTicks, canEditEnvelope),
                     Field("envelope.release", "RELEASE TICKS", envelope.ReleaseTicks, canEditEnvelope),
                     Field("envelope.start", "START VALUE", envelope.StartValue, canEditEnvelope),
                     Field("envelope.peak", "PEAK VALUE", envelope.PeakValue, canEditEnvelope),
                     Field("envelope.sustain", "SUSTAIN VALUE", envelope.SustainValue, canEditEnvelope),
                     Field("envelope.end", "END VALUE", envelope.EndValue, canEditEnvelope),
                     Field("object.id", "STABLE ID", envelope.Id.Value, false)]);
                return;
            }
            foreach (SubVoice curveVoice in instrument.SubVoices)
            {
                ValueCurve? curve = curveVoice.Curves.FirstOrDefault(
                    candidate => candidate.Points.Any(point => point.Id == eventId));
                CurvePoint? point = curve?.Points.FirstOrDefault(candidate => candidate.Id == eventId);
                if (curve is null || point is null) continue;
                inspector.Replace(
                    "Value Curve Point",
                    $"{curveVoice.Name ?? "SubVoice"} · {FormatMidiTarget(curve.Target)}",
                    [Field("valueCurvePoint.tick", "TICK", point.Tick),
                     Field("valueCurvePoint.value", "VALUE", point.Value),
                     Field("valueCurvePoint.interpolation", "INTERPOLATION", point.Interpolation),
                     Field("object.id", "STABLE ID", point.Id.Value, false)]);
                return;
            }
            SubVoice? voice = instrument.SubVoices.FirstOrDefault(item => item.Events.Any(value => value.Id == eventId));
            TemplateEvent? template = voice?.Events.FirstOrDefault(item => item.Id == eventId);
            if (voice is not null && template is not null)
            {
                List<InspectorField> fields = [
                    Field("template.tick", "TICK", template.Tick),
                    Field("template.kind", "EVENT KIND", template.Kind, false)];
                if (template.Kind == TemplateEventKind.Note)
                {
                    fields.Add(Field("template.length", "LENGTH TICKS", template.LengthTicks));
                }
                if (template.Kind is TemplateEventKind.Note or TemplateEventKind.ControlChange
                    or TemplateEventKind.RegisteredParameter or TemplateEventKind.NonRegisteredParameter)
                {
                    fields.Add(Field(
                        "template.number",
                        template.Kind == TemplateEventKind.ControlChange
                            ? $"CONTROLLER · {MidiControlChangeCatalog.Format(template.Number)}"
                            : "NUMBER",
                        template.Number));
                }
                if (template.Kind != TemplateEventKind.Bank || template.HasBankMsb)
                {
                    fields.Add(Field(
                        "template.value",
                        template.Kind == TemplateEventKind.Program ? "PROGRAM (1–128)" : "VALUE",
                        template.Kind == TemplateEventKind.Program ? template.Value + 1 : template.Value));
                }
                if (template.Kind is TemplateEventKind.Bank or TemplateEventKind.PitchBendRange)
                {
                    fields.Add(Field("template.secondary", "SECONDARY VALUE", template.SecondaryValue));
                }
                if (template.Kind == TemplateEventKind.Note)
                {
                    fields.Add(Field("template.followPitch", "FOLLOW PITCH DELTA", template.FollowPitchDelta));
                }
                inspector.Replace(template.Kind.ToString(), string.IsNullOrWhiteSpace(voice.Name) ? "SubVoice event" : voice.Name, fields);
                return;
            }
        }

        inspector.Replace(
            instrument.Name,
            "Event Instrument",
            [Field("instrument.name", "NAME", instrument.Name),
             Field("instrument.description", "DESCRIPTION", instrument.Description ?? string.Empty),
             Field("instrument.root", "ROOT MIDI NOTE", instrument.RootNote),
             Field("instrument.templateLength", "TEMPLATE LENGTH TICKS", instrument.TemplateLengthTicks),
             Field("instrument.isolation", "REQUIRES CHANNEL ISOLATION", instrument.RequiresChannelIsolation),
             Field("instrument.overlapPolicy", "OVERLAP POLICY", instrument.OverlapPolicy),
             Field("instrument.overlapScope", "OVERLAP SCOPE", instrument.OverlapScope),
             Field("instrument.shortLifecycle", "SHORT NOTE LIFECYCLE", instrument.ShortLifecycle),
             Field("instrument.longLifecycle", "LONG NOTE LIFECYCLE", instrument.LongLifecycle),
             Field("instrument.loopStart", "LOOP START (blank = disabled)", instrument.LoopStartTick?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
             Field("instrument.loopEnd", "LOOP END (blank = disabled)", instrument.LoopEndTick?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
             StateField("instrument.initial.bankMsb", "INITIAL BANK MSB", instrument.InitialState.BankMsb),
             StateField("instrument.initial.bankLsb", "INITIAL BANK LSB", instrument.InitialState.BankLsb),
             StateField("instrument.initial.program", "INITIAL PROGRAM (0–127)", instrument.InitialState.Program),
             StateField("instrument.initial.pitchBend", "INITIAL PITCH BEND", instrument.InitialState.PitchBend),
             StateField("instrument.initial.pitchRangeSemitones", "INITIAL PITCH RANGE SEMITONES", instrument.InitialState.PitchBendRangeSemitones),
             StateField("instrument.initial.pitchRangeCents", "INITIAL PITCH RANGE CENTS", instrument.InitialState.PitchBendRangeCents),
             .. ExtraStateFields("instrument.initial", instrument.InitialState),
             Field("object.id", "STABLE ID", instrument.Id.Value, false)]);
    }

    private static IProjectEditCommand CreateSubVoiceEdit(
        EventInstrument instrument,
        MidoraId? selectedId,
        string key,
        string value)
    {
        MidoraId id = selectedId ?? throw new InvalidOperationException("Select one SubVoice first.");
        _ = instrument.SubVoices.SingleOrDefault(item => item.Id == id)
            ?? throw new InvalidOperationException("The selected SubVoice no longer exists.");
        return key switch
        {
            "subvoice.name" => ProjectDomainEditCommands.UpdateSubVoiceName(instrument.Id, id, value),
            "subvoice.root" => ProjectDomainEditCommands.UpdateSubVoiceRootNote(
                instrument.Id,
                id,
                NullableInt(value, "Root Note Override")),
            _ when key.StartsWith("subvoice.initial.", StringComparison.Ordinal) =>
                ProjectDomainEditCommands.UpdateSubVoiceInitialStateValue(
                    instrument.Id,
                    id,
                    ParseInitialStateTarget(key["subvoice.initial.".Length..]),
                    NullableInt(value, "Initial State Value")),
            _ => throw new InvalidOperationException("This SubVoice property is read-only.")
        };
    }

    private static IProjectEditCommand CreateLoopEdit(
        EventInstrument instrument,
        string key,
        string value)
    {
        long? edited = NullableLong(value, key == "instrument.loopStart" ? "Loop Start" : "Loop End");
        if (!edited.HasValue)
        {
            return ProjectDomainEditCommands.UpdateEventInstrumentLoop(instrument.Id, null, null);
        }
        return ProjectDomainEditCommands.UpdateEventInstrumentLoop(
            instrument.Id,
            key == "instrument.loopStart" ? edited : instrument.LoopStartTick,
            key == "instrument.loopEnd" ? edited : instrument.LoopEndTick);
    }

    private static IProjectEditCommand CreateLogicalParameterEdit(
        EventInstrument instrument,
        MidoraId? selectedId,
        string key,
        string value)
    {
        MidoraId id = selectedId ?? throw new InvalidOperationException("Select one Logical Parameter first.");
        LogicalParameterDefinition parameter = instrument.LogicalParameters.SingleOrDefault(item => item.Id == id)
            ?? throw new InvalidOperationException("The selected Logical Parameter no longer exists.");
        return key switch
        {
            "parameter.name" => ProjectDomainEditCommands.RenameLogicalParameter(instrument.Id, id, value),
            "parameter.default" => ProjectDomainEditCommands.UpdateLogicalParameterDefaultValue(
                instrument.Id, id, Double(value, "Default Value")),
            "parameter.minimum" or "parameter.maximum" => ProjectDomainEditCommands.UpdateLogicalParameterLegalRange(
                instrument.Id,
                id,
                key == "parameter.minimum" ? Double(value, "Legal Minimum") : parameter.Minimum,
                key == "parameter.maximum" ? Double(value, "Legal Maximum") : parameter.Maximum),
            "parameter.displayMinimum" or "parameter.displayMaximum" => ProjectDomainEditCommands.UpdateLogicalParameterDisplayRange(
                instrument.Id,
                id,
                key == "parameter.displayMinimum" ? Double(value, "Display Minimum") : parameter.DisplayMinimum,
                key == "parameter.displayMaximum" ? Double(value, "Display Maximum") : parameter.DisplayMaximum),
            _ => throw new InvalidOperationException("This Logical Parameter property is read-only.")
        };
    }

    private static IProjectEditCommand CreateValueCurvePointEdit(
        EventInstrument instrument,
        MidoraId? selectedId,
        string key,
        string value)
    {
        MidoraId id = selectedId ?? throw new InvalidOperationException("Select one Value Curve point first.");
        foreach (SubVoice voice in instrument.SubVoices)
        {
            ValueCurve? curve = voice.Curves.FirstOrDefault(candidate => candidate.Points.Any(point => point.Id == id));
            CurvePoint? point = curve?.Points.FirstOrDefault(candidate => candidate.Id == id);
            if (curve is null || point is null) continue;
            return ProjectDomainEditCommands.UpdateValueCurvePoint(
                instrument.Id,
                voice.Id,
                curve.Id,
                point.Id,
                key == "valueCurvePoint.tick" ? Long(value, "Tick") : point.Tick,
                key == "valueCurvePoint.value" ? Double(value, "Value") : point.Value,
                key == "valueCurvePoint.interpolation"
                    ? EnumValue<CurveInterpolation>(value, "Interpolation")
                    : point.Interpolation);
        }
        throw new InvalidOperationException("The selected Value Curve point no longer exists.");
    }

    private static IProjectEditCommand CreateParameterMappingEdit(
        EventInstrument instrument,
        MidoraId? selectedId,
        string key,
        string value)
    {
        MidoraId id = selectedId ?? throw new InvalidOperationException("Select one Logical Parameter Mapping first.");
        LogicalParameterMapping mapping = instrument.ParameterMappings.SingleOrDefault(item => item.Id == id)
            ?? throw new InvalidOperationException("The selected Logical Parameter Mapping no longer exists.");
        return key switch
        {
            "parameterMapping.source" =>
                ProjectDomainEditCommands.UpdateLogicalParameterMappingSource(
                    instrument.Id,
                    mapping.Id,
                    NullableId(value, "Logical Parameter")
                        ?? throw new FormatException("Select a Logical Parameter.")),
            "parameterMapping.subVoice" =>
                ProjectDomainEditCommands.UpdateLogicalParameterMappingTarget(
                    instrument.Id,
                    mapping.Id,
                    NullableId(value, "SubVoice")
                        ?? throw new FormatException("Select a SubVoice."),
                    mapping.Target),
            "parameterMapping.rounding" or "parameterMapping.overflow" =>
                ProjectDomainEditCommands.UpdateLogicalParameterMappingTargetSettings(
                    instrument.Id,
                    mapping.Id,
                    key == "parameterMapping.rounding"
                        ? EnumValue<MappingRounding>(value, "Final Rounding")
                        : mapping.TargetSettings.Rounding,
                    key == "parameterMapping.overflow"
                        ? EnumValue<MappingOverflow>(value, "Final Overflow")
                        : mapping.TargetSettings.Overflow),
            _ => throw new InvalidOperationException("This Logical Parameter Mapping property is read-only.")
        };
    }

    private static IProjectEditCommand CreateMappingChainEdit(
        EventInstrument instrument,
        MidoraId? selectedId,
        string key,
        string value)
    {
        MidoraId id = selectedId
            ?? throw new InvalidOperationException("Select one Mapping Chain first.");
        MappingChainEditingContext context = MappingEditingPolicy.Resolve(instrument, id);
        return key switch
        {
            "mappingChain.enabled" => ProjectDomainEditCommands.UpdateMappingChainEnabled(
                instrument.Id,
                context.Chain.Id,
                Bool(value, "Enabled")),
            "mappingChain.rounding" or "mappingChain.overflow" =>
                ProjectDomainEditCommands.UpdateMappingChainTargetSettings(
                    instrument.Id,
                    context.Chain.Id,
                    key == "mappingChain.rounding"
                        ? EnumValue<MappingRounding>(value, "Final Rounding")
                        : context.TargetSettings.Rounding,
                    key == "mappingChain.overflow"
                        ? EnumValue<MappingOverflow>(value, "Final Overflow")
                        : context.TargetSettings.Overflow),
            _ => throw new InvalidOperationException("This Mapping Chain property is read-only.")
        };
    }

    private static IProjectEditCommand CreateMappingStepEdit(
        EventInstrument instrument,
        MidoraId? selectedId,
        string key,
        string value)
    {
        MidoraId id = selectedId ?? throw new InvalidOperationException("Select one Mapping Step first.");
        MappingStepContext context = FindMappingStep(instrument, id)
            ?? throw new InvalidOperationException("The selected Mapping Step no longer exists.");
        ValueMappingStep step = context.Step;
        if (key == "mappingStep.enabled")
        {
            return ProjectDomainEditCommands.UpdateMappingStepEnabled(
                instrument.Id,
                context.Chain.Id,
                step.Id,
                Bool(value, "Enabled"));
        }
        return ProjectDomainEditCommands.UpdateMappingStep(
            instrument.Id,
            context.Chain.Id,
            step.Id,
            key == "mappingStep.source" ? EnumValue<MappingSource>(value, "Source") : step.Source,
            key == "mappingStep.operation" ? EnumValue<MappingOperation>(value, "Operation") : step.Operation,
            key == "mappingStep.logicalParameter" ? NullableId(value, "Logical Parameter") : step.LogicalParameterId,
            key == "mappingStep.envelope" ? NullableId(value, "Envelope") : step.EnvelopeId,
            key == "mappingStep.function" ? NullableId(value, "C# Mapping Function") : step.MappingFunctionId,
            key == "mappingStep.constant" ? Double(value, "Constant") : step.Constant,
            key == "mappingStep.sourceMinimum" ? Double(value, "Source Minimum") : step.SourceMinimum,
            key == "mappingStep.sourceMaximum" ? Double(value, "Source Maximum") : step.SourceMaximum,
            key == "mappingStep.targetMinimum" ? Double(value, "Target Minimum") : step.TargetMinimum,
            key == "mappingStep.targetMaximum" ? Double(value, "Target Maximum") : step.TargetMaximum,
            key == "mappingStep.inputOverflow" ? EnumValue<MappingInputOverflow>(value, "Input Overflow") : step.InputOverflow,
            key == "mappingStep.divideByZero" ? EnumValue<DivideByZeroPolicy>(value, "Divide By Zero") : step.DivideByZero);
    }

    private static MappingStepContext? FindMappingStep(EventInstrument instrument, MidoraId id)
    {
        foreach (MappingChain chain in EnumerateMappingChains(instrument))
        {
            for (int index = 0; index < chain.Count; index++)
            {
                if (chain[index].Id == id) return new(chain, chain[index], index);
            }
        }
        return null;
    }

    private static IEnumerable<MappingChain> EnumerateMappingChains(EventInstrument instrument) =>
        instrument.ParameterMappings.Select(item => item.Steps)
            .Concat(instrument.SubVoices
                .SelectMany(voice => voice.EventMappings)
                .Select(item => item.Steps));

    private readonly record struct MappingStepContext(
        MappingChain Chain,
        ValueMappingStep Step,
        int Index);

    private static IProjectEditCommand CreateEnvelopeEdit(
        EventInstrument instrument,
        MidoraId? selectedId,
        string key,
        string value)
    {
        MidoraId id = selectedId ?? throw new InvalidOperationException("Select one Envelope Preset first.");
        InstrumentEnvelope envelope = instrument.Envelopes.SingleOrDefault(item => item.Id == id)
            ?? throw new InvalidOperationException("The selected Envelope Preset no longer exists.");
        return ProjectDomainEditCommands.UpdateInstrumentEnvelope(
            instrument.Id,
            envelope.Id,
            key == "envelope.name" ? value : envelope.Name,
            key == "envelope.delay" ? Long(value, "Delay") : envelope.DelayTicks,
            key == "envelope.attack" ? Long(value, "Attack") : envelope.AttackTicks,
            key == "envelope.hold" ? Long(value, "Hold") : envelope.HoldTicks,
            key == "envelope.decay" ? Long(value, "Decay") : envelope.DecayTicks,
            key == "envelope.start" ? Double(value, "Start Value") : envelope.StartValue,
            key == "envelope.peak" ? Double(value, "Peak Value") : envelope.PeakValue,
            key == "envelope.sustain" ? Double(value, "Sustain Value") : envelope.SustainValue,
            key == "envelope.release" ? Long(value, "Release") : envelope.ReleaseTicks,
            key == "envelope.end" ? Double(value, "End Value") : envelope.EndValue);
    }

    private static string FormatMidiTarget(MidiValueTarget target) => target.Kind switch
    {
        MidiValueKind.ControlChange => MidiControlChangeCatalog.Format(target.Number),
        MidiValueKind.RegisteredParameter => $"RPN {target.Number}",
        MidiValueKind.NonRegisteredParameter => $"NRPN {target.Number}",
        _ => target.Kind.ToString()
    };

    private static string FormatEventMappingTarget(TemplateEventMappingTarget target)
    {
        if (target.EventKind == TemplateEventKind.Note)
        {
            return target.Parameter == TemplateEventMappingParameter.Number
                ? "Note · Number"
                : "Note · Velocity";
        }
        return TemplateEventMidiTargets.TryFromMappingTarget(target, out MidiValueTarget midiTarget)
            ? TemplateEventMidiTargets.Format(midiTarget)
            : $"{target.EventKind} · {target.Parameter}";
    }

    private static IProjectEditCommand CreateTemplateEventEdit(
        MidoraProject project,
        EventInstrument instrument,
        InstrumentWorkspaceViewModel workspace,
        string key,
        string value)
    {
        MidoraId id = workspace.Selection.Primary
            ?? throw new InvalidOperationException("Select one SubVoice event first.");
        SubVoice voice = instrument.SubVoices.Single(item => item.Events.Any(candidate => candidate.Id == id));
        TemplateEvent item = voice.Events.Single(candidate => candidate.Id == id);
        long tick = key == "template.tick" ? Long(value, "Tick") : item.Tick;
        int number = key == "template.number" ? Int(value, "Number") : item.Number;
        int eventValue = key == "template.value"
            ? item.Kind == TemplateEventKind.Program
                ? checked(Int(value, "Program") - 1)
                : Int(value, "Value")
            : item.Value;
        int secondary = key == "template.secondary" ? Int(value, "Secondary Value") : item.SecondaryValue;
        return item.Kind switch
        {
            TemplateEventKind.Note => ProjectDomainEditCommands.UpdateTemplateNote(
                instrument.Id, voice.Id, item.Id, tick,
                key == "template.length" ? Long(value, "Length") : item.LengthTicks,
                number, eventValue,
                key == "template.followPitch" ? Bool(value, "Follow Pitch Delta") : item.FollowPitchDelta),
            TemplateEventKind.ControlChange => ProjectDomainEditCommands.UpdateTemplateControlChange(
                instrument.Id, voice.Id, item.Id, tick, number, eventValue),
            TemplateEventKind.Bank => ProjectDomainEditCommands.UpdateTemplateBank(
                instrument.Id, voice.Id, item.Id, tick,
                item.HasBankMsb ? eventValue : null,
                item.HasBankLsb ? secondary : null),
            TemplateEventKind.Program => ProjectDomainEditCommands.UpdateTemplateProgram(
                instrument.Id, voice.Id, item.Id, tick, eventValue),
            TemplateEventKind.PitchBend => ProjectDomainEditCommands.UpdateTemplatePitchBend(
                instrument.Id, voice.Id, item.Id, tick, eventValue),
            TemplateEventKind.RegisteredParameter => ProjectDomainEditCommands.UpdateTemplateRegisteredParameter(
                instrument.Id, voice.Id, item.Id, tick, number, eventValue),
            TemplateEventKind.NonRegisteredParameter => ProjectDomainEditCommands.UpdateTemplateNonRegisteredParameter(
                instrument.Id, voice.Id, item.Id, tick, number, eventValue),
            TemplateEventKind.PitchBendRange => ProjectDomainEditCommands.UpdateTemplatePitchBendRange(
                instrument.Id, voice.Id, item.Id, tick, eventValue, secondary),
            _ => throw new InvalidOperationException("Unsupported Template Event kind.")
        };
    }

    private static IProjectEditCommand CreateConductorEdit(
        MidoraProject project,
        MidoraId? selectedId,
        string key,
        string value)
    {
        MidoraId id = selectedId ?? throw new InvalidOperationException("Select one Conductor event first.");
        if (project.Conductor.Tempos.FirstOrDefault(item => item.Id == id) is TempoChange tempo)
        {
            return ProjectDomainEditCommands.UpdateTempo(
                id,
                key == "conductor.tick" ? Long(value, "Tick") : tempo.Tick,
                key == "conductor.bpm" ? Decimal(value, "Beats Per Minute") : tempo.BeatsPerMinute);
        }
        if (project.Conductor.TimeSignatures.FirstOrDefault(item => item.Id == id) is TimeSignatureChange signature)
        {
            return ProjectDomainEditCommands.UpdateTimeSignature(
                id,
                key == "conductor.tick" ? Long(value, "Tick") : signature.Tick,
                key == "conductor.numerator" ? Int(value, "Numerator") : signature.Numerator,
                key == "conductor.denominator" ? Int(value, "Denominator") : signature.Denominator);
        }
        if (project.Conductor.KeySignatures.FirstOrDefault(item => item.Id == id) is KeySignatureChange keySignature)
        {
            return ProjectDomainEditCommands.UpdateKeySignature(
                id,
                key == "conductor.tick" ? Long(value, "Tick") : keySignature.Tick,
                key == "conductor.sharpsFlats" ? Int(value, "Sharps / Flats") : keySignature.SharpsFlats,
                key == "conductor.isMinor" ? Bool(value, "Is Minor") : keySignature.IsMinor);
        }
        if (project.Conductor.Markers.FirstOrDefault(item => item.Id == id) is ProjectMarker marker)
        {
            return ProjectDomainEditCommands.UpdateProjectMarker(
                id,
                key == "conductor.tick" ? Long(value, "Tick") : marker.Tick,
                key == "conductor.name" ? value : marker.Name);
        }
        if (project.Conductor.EndMarker is ProjectEndMarker end && end.Id == id)
        {
            return ProjectDomainEditCommands.UpdateProjectEndMarker(Long(value, "Tick"));
        }
        throw new InvalidOperationException("The selected Conductor event no longer exists.");
    }

    private static (LogicalTrack Track, Segment Segment) FindSegmentContext(
        MidoraProject project,
        WorkspaceViewModel workspace)
    {
        MidoraId? id = workspace is TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Segment }
            ? workspace.ObjectId
            : workspace.Selection.Primary;
        return TimelineWorkspaceViewModel.FindSegment(project, id)
            ?? throw new InvalidOperationException("Select one Segment first.");
    }

    private static (Segment Segment, LogicalNote Note) FindLogicalNoteContext(
        MidoraProject project,
        WorkspaceViewModel workspace)
    {
        if (workspace is not TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Segment } timeline)
            throw new InvalidOperationException("A Logical Note must be edited in its Segment Workspace.");
        Segment segment = TimelineWorkspaceViewModel.FindSegment(project, timeline.ObjectId)?.Segment
            ?? throw new InvalidOperationException("The Segment no longer exists.");
        MidoraId id = workspace.Selection.Primary
            ?? throw new InvalidOperationException("Select one Logical Note first.");
        LogicalNote note = segment.Notes.FirstOrDefault(item => item.Id == id)
            ?? throw new InvalidOperationException("The Logical Note no longer exists.");
        return (segment, note);
    }

    private static (Segment Segment, LogicalParameterLane Lane, CurvePoint Point)
        FindLogicalParameterPointContext(MidoraProject project, WorkspaceViewModel workspace)
    {
        if (workspace is not TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Segment } timeline)
            throw new InvalidOperationException("A Logical Parameter point must be edited in its Segment Workspace.");
        Segment segment = TimelineWorkspaceViewModel.FindSegment(project, timeline.ObjectId)?.Segment
            ?? throw new InvalidOperationException("The Segment no longer exists.");
        MidoraId id = workspace.Selection.Primary
            ?? throw new InvalidOperationException("Select one Logical Parameter point first.");
        LogicalParameterLane lane = segment.ParameterLanes.FirstOrDefault(
                candidate => candidate.Points.Any(point => point.Id == id))
            ?? throw new InvalidOperationException("The Logical Parameter point no longer exists.");
        CurvePoint point = lane.Points.Single(item => item.Id == id);
        return (segment, lane, point);
    }

    private static InspectorField Field(string key, string label, object value, bool editable = true) =>
        new(
            key,
            label,
            Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
            editable,
            InspectorFieldValueState.SameValue,
            value.GetType().IsEnum ? Enum.GetNames(value.GetType()) : null,
            value is bool);

    private static InspectorField ChoiceField(
        string key,
        string label,
        string value,
        IEnumerable<InspectorChoiceOption> choices,
        bool editable = true) =>
        new(
            key,
            label,
            value,
            editable,
            InspectorFieldValueState.SameValue,
            options: null,
            isBoolean: false,
            choices: choices.ToArray());

    private static IReadOnlyList<InspectorChoiceOption> IdChoices(
        IEnumerable<(MidoraId Id, string Label)> values,
        MidoraId current,
        string missingLabel)
    {
        List<InspectorChoiceOption> result = values
            .Select(value => new InspectorChoiceOption(
                value.Id.Value.ToString(CultureInfo.InvariantCulture),
                value.Label))
            .ToList();
        string currentValue = current.Value.ToString(CultureInfo.InvariantCulture);
        if (result.All(value => value.Value != currentValue))
        {
            result.Add(new(currentValue, $"{missingLabel} · {currentValue}"));
        }
        return result;
    }

    private static IReadOnlyList<InspectorChoiceOption> OptionalIdChoices(
        IEnumerable<(MidoraId Id, string Label)> values,
        MidoraId? current,
        string missingLabel)
    {
        List<InspectorChoiceOption> result =
        [
            new(string.Empty, "Select an object…")
        ];
        result.AddRange(values.Select(value => new InspectorChoiceOption(
            value.Id.Value.ToString(CultureInfo.InvariantCulture),
            value.Label)));
        if (current is MidoraId currentId)
        {
            string currentValue = currentId.Value.ToString(CultureInfo.InvariantCulture);
            if (result.All(value => value.Value != currentValue))
            {
                result.Add(new(currentValue, $"{missingLabel} · {currentValue}"));
            }
        }
        return result;
    }

    private static InspectorField StateField(string key, string label, int? value) =>
        new(key, label, value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);

    private static IEnumerable<InspectorField> ExtraStateFields(string prefix, MidiInitialState state) =>
        state.Controllers.OrderBy(item => item.Key)
            .Select(item => StateField(
                $"{prefix}.cc.{item.Key}",
                $"INITIAL {MidiControlChangeCatalog.Format(item.Key)}",
                item.Value))
            .Concat(state.RegisteredParameters.OrderBy(item => item.Key)
                .Select(item => StateField($"{prefix}.rpn.{item.Key}", $"INITIAL RPN {item.Key}", item.Value)))
            .Concat(state.NonRegisteredParameters.OrderBy(item => item.Key)
                .Select(item => StateField($"{prefix}.nrpn.{item.Key}", $"INITIAL NRPN {item.Key}", item.Value)));

    private static MidiValueTarget ParseInitialStateTarget(string key)
    {
        if (TryInitialTarget(key, "cc.", MidiValueKind.ControlChange, out MidiValueTarget target)
            || TryInitialTarget(key, "rpn.", MidiValueKind.RegisteredParameter, out target)
            || TryInitialTarget(key, "nrpn.", MidiValueKind.NonRegisteredParameter, out target))
        {
            return target;
        }
        return key switch
        {
            "bankMsb" => new(MidiValueKind.BankMsb),
            "bankLsb" => new(MidiValueKind.BankLsb),
            "program" => new(MidiValueKind.Program),
            "pitchBend" => new(MidiValueKind.PitchBend),
            "pitchRangeSemitones" => new(MidiValueKind.PitchBendRangeSemitones),
            "pitchRangeCents" => new(MidiValueKind.PitchBendRangeCents),
            _ => throw new InvalidOperationException("This Initial State target is unsupported.")
        };
    }

    private static bool TryInitialTarget(
        string key,
        string prefix,
        MidiValueKind kind,
        out MidiValueTarget target)
    {
        target = default;
        if (!key.StartsWith(prefix, StringComparison.Ordinal)
            || !int.TryParse(key[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int number))
        {
            return false;
        }
        target = new(kind, number);
        return true;
    }

    private static long Long(string value, string label) => long.TryParse(
        value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long result)
        ? result : throw new FormatException($"{label} must be a base-10 integer.");

    private static int Int(string value, string label) => int.TryParse(
        value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
        ? result : throw new FormatException($"{label} must be a base-10 integer.");

    private static int? NullableInt(string value, string label) => string.IsNullOrWhiteSpace(value)
        ? null
        : Int(value, label);

    private static long? NullableLong(string value, string label) => string.IsNullOrWhiteSpace(value)
        ? null
        : Long(value, label);

    private static MidoraId? NullableId(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        long parsed = Long(value, label);
        return parsed > 0
            ? new MidoraId(parsed)
            : throw new FormatException($"{label} stable ID must be a positive integer.");
    }

    private static decimal Decimal(string value, string label) => decimal.TryParse(
        value.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal result)
        ? result : throw new FormatException($"{label} must be a decimal number using '.'.");

    private static double Double(string value, string label) => double.TryParse(
        value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double result)
        && double.IsFinite(result)
        ? result : throw new FormatException($"{label} must be a finite number using '.'.");

    private static T EnumValue<T>(string value, string label) where T : struct, Enum =>
        Enum.TryParse(value.Trim(), ignoreCase: true, out T result) && Enum.IsDefined(result)
            ? result
            : throw new FormatException($"{label} is not a supported {typeof(T).Name} value.");

    private static bool Bool(string value, string label) => bool.TryParse(value.Trim(), out bool result)
        ? result : throw new FormatException($"{label} must be True or False.");
}
