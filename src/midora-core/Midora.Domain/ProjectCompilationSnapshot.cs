namespace Midora.Domain;

/// <summary>
/// Creates and incrementally synchronizes the source-model mirror owned by a
/// background compiler. Stable IDs are preserved because compiler checkpoints
/// and diagnostics use them as identity.
/// </summary>
internal static class ProjectCompilationSnapshot
{
    public static MidoraProject Create(
        MidoraProject source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        ProjectMetadataSnapshot metadata = source.Metadata.Snapshot();
        MidoraProject result = new(
            source.TicksPerQuarterNote,
            source.NextStableId,
            metadata.CreatedAtUtc);
        result.Metadata.Restore(metadata);
        CopyConductor(source.Conductor, result.Conductor, cancellationToken);
        CopyState(source.GlobalInitialState, result.GlobalInitialState, cancellationToken);
        CopyState(source.GlobalResetDefaults, result.GlobalResetDefaults, cancellationToken);

        foreach (EventInstrumentLibraryFolder folder in source.EventInstrumentFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.EventInstrumentFolders.Add(new EventInstrumentLibraryFolder(folder.Id)
            {
                Name = folder.Name
            });
        }
        foreach (DamagedProjectObject damaged in source.DamagedEventInstruments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.DamagedEventInstruments.Add(damaged);
        }
        foreach (DamagedProjectObject damaged in source.DamagedLogicalTracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.DamagedLogicalTracks.Add(damaged);
        }
        foreach (DamagedProjectObject damaged in source.DamagedMidiChannelRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.DamagedMidiChannelRoots.Add(damaged);
        }
        foreach (DamagedProjectObject damaged in source.DamagedPureMidiTracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.DamagedPureMidiTracks.Add(damaged);
        }
        result.ArrangementParents.AddRange(source.ArrangementParents);
        foreach (EventInstrument instrument in source.EventInstruments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.EventInstruments.Add(CloneInstrument(result, instrument, cancellationToken));
        }
        foreach (LogicalTrack track in source.Tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Tracks.Add(CloneTrack(result, track, cancellationToken));
        }
        foreach (MidiChannelRoot root in source.MidiChannelRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.MidiChannelRoots.Add(CloneMidiChannelRoot(result, root));
        }
        foreach (PureMidiTrack track in source.PureMidiTracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.PureMidiTracks.Add(ClonePureMidiTrack(result, track, cancellationToken));
        }

        result.RestoreNextStableId(source.NextStableId);
        return result;
    }

    public static MidoraProject Synchronize(
        MidoraProject snapshot,
        MidoraProject source,
        ProjectChangeSet changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(changes);

        if (changes.AffectsEverything
            || snapshot.TicksPerQuarterNote != source.TicksPerQuarterNote)
        {
            return Create(source, cancellationToken);
        }

        if (changes.AffectsConductor)
        {
            CopyConductor(source.Conductor, snapshot.Conductor, cancellationToken);
        }
        if (changes.EventInstrumentIds.Count != 0)
        {
            SynchronizeByStableId(
                snapshot.EventInstruments,
                source.EventInstruments,
                changes.EventInstrumentIds,
                value => CloneInstrument(snapshot, value, cancellationToken),
                cancellationToken);
        }
        if (changes.TrackIds.Count != 0)
        {
            SynchronizeByStableId(
                snapshot.Tracks,
                source.Tracks,
                changes.TrackIds,
                value => CloneTrack(snapshot, value, cancellationToken),
                cancellationToken);
        }
        if (changes.MidiChannelRootIds.Count != 0)
        {
            SynchronizeByStableId(
                snapshot.MidiChannelRoots,
                source.MidiChannelRoots,
                changes.MidiChannelRootIds,
                value => CloneMidiChannelRoot(snapshot, value),
                cancellationToken);
        }
        HashSet<MidoraId> changedPureMidiTrackIds = [.. changes.PureMidiTrackIds];
        if (changes.MidiChannelRootIds.Count != 0)
        {
            changedPureMidiTrackIds.UnionWith(source.PureMidiTracks
                .Where(value => changes.MidiChannelRootIds.Contains(value.MidiChannelRootId))
                .Select(value => value.Id));
            changedPureMidiTrackIds.UnionWith(snapshot.PureMidiTracks
                .Where(value => changes.MidiChannelRootIds.Contains(value.MidiChannelRootId))
                .Select(value => value.Id));
        }
        if (changedPureMidiTrackIds.Count != 0)
        {
            SynchronizeByStableId(
                snapshot.PureMidiTracks,
                source.PureMidiTracks,
                changedPureMidiTrackIds,
                value => ClonePureMidiTrack(snapshot, value, cancellationToken),
                cancellationToken);
        }

        snapshot.RestoreNextStableId(source.NextStableId);
        return snapshot;
    }

    private static void SynchronizeByStableId<T>(
        List<T> snapshot,
        IReadOnlyList<T> source,
        IReadOnlySet<MidoraId> changedIds,
        Func<T, T> clone,
        CancellationToken cancellationToken)
        where T : class
    {
        static MidoraId IdOf(T value) => value switch
        {
            EventInstrument instrument => instrument.Id,
            LogicalTrack track => track.Id,
            MidiChannelRoot root => root.Id,
            PureMidiTrack track => track.Id,
            _ => throw new InvalidOperationException("Unsupported compilation snapshot object.")
        };

        List<T> reusable = [.. snapshot];
        List<T> synchronized = new(source.Count);
        foreach (T sourceValue in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MidoraId sourceId = IdOf(sourceValue);
            int reusableIndex = changedIds.Contains(sourceId)
                ? -1
                : reusable.FindIndex(value => IdOf(value) == sourceId);
            if (reusableIndex < 0)
            {
                synchronized.Add(clone(sourceValue));
                continue;
            }

            synchronized.Add(reusable[reusableIndex]);
            reusable.RemoveAt(reusableIndex);
        }
        snapshot.Clear();
        snapshot.AddRange(synchronized);
    }

    private static void CopyConductor(
        ConductorTrack source,
        ConductorTrack target,
        CancellationToken cancellationToken)
    {
        List<TempoChange> tempos = new(source.Tempos.Count);
        List<TimeSignatureChange> timeSignatures = new(source.TimeSignatures.Count);
        List<KeySignatureChange> keySignatures = new(source.KeySignatures.Count);
        List<ProjectMarker> markers = new(source.Markers.Count);
        foreach (TempoChange value in source.Tempos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tempos.Add(new TempoChange(value.Id, value.Tick, value.BeatsPerMinute));
        }
        foreach (TimeSignatureChange value in source.TimeSignatures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            timeSignatures.Add(new TimeSignatureChange(
                value.Id,
                value.Tick,
                value.Numerator,
                value.Denominator));
        }
        foreach (KeySignatureChange value in source.KeySignatures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            keySignatures.Add(new KeySignatureChange(
                value.Id,
                value.Tick,
                value.SharpsFlats,
                value.IsMinor));
        }
        foreach (ProjectMarker marker in source.Markers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            markers.Add(new ProjectMarker(marker.Id, marker.Tick, marker.Name));
        }
        ProjectEndMarker? endMarker = source.EndMarker is null
            ? null
            : new ProjectEndMarker(source.EndMarker.Id, source.EndMarker.Tick);
        target.Tempos.Clear();
        target.Tempos.AddRange(tempos);
        target.TimeSignatures.Clear();
        target.TimeSignatures.AddRange(timeSignatures);
        target.KeySignatures.Clear();
        target.KeySignatures.AddRange(keySignatures);
        target.Markers.Clear();
        target.Markers.AddRange(markers);
        target.EndMarker = endMarker;
    }

    private static LogicalTrack CloneTrack(
        MidoraProject project,
        LogicalTrack source,
        CancellationToken cancellationToken)
    {
        LogicalTrack result = new(project, source.Id)
        {
            Name = source.Name,
            EventInstrumentId = source.EventInstrumentId,
            LastBoundEventInstrumentName = source.LastBoundEventInstrumentName,
            ColorOverride = source.ColorOverride
        };
        foreach (Segment segment in source.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Segment segmentCopy = new(project, segment.Id)
            {
                ProjectStartTick = segment.ProjectStartTick,
                LengthTicks = segment.LengthTicks,
                ContentOffsetTick = segment.ContentOffsetTick
            };
            for (int index = 0; index < segment.Notes.Count; index++)
            {
                if ((index & 0xff) == 0) cancellationToken.ThrowIfCancellationRequested();
                LogicalNote note = segment.Notes[index];
                segmentCopy.Notes.Add(new LogicalNote(project, note.Id)
                {
                    StartTick = note.StartTick,
                    LengthTicks = note.LengthTicks,
                    Note = note.Note,
                    Velocity = note.Velocity
                });
            }
            foreach (LogicalParameterLane lane in segment.ParameterLanes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LogicalParameterLane laneCopy = new(project, lane.Id)
                {
                    ParameterId = lane.ParameterId
                };
                for (int index = 0; index < lane.Points.Count; index++)
                {
                    if ((index & 0xff) == 0) cancellationToken.ThrowIfCancellationRequested();
                    CurvePoint point = lane.Points[index];
                    laneCopy.Points.Add(ClonePoint(project, point));
                }
                segmentCopy.ParameterLanes.Add(laneCopy);
            }
            result.Segments.Add(segmentCopy);
        }
        return result;
    }

    private static MidiChannelRoot CloneMidiChannelRoot(
        MidoraProject project,
        MidiChannelRoot source)
    {
        MidiChannelRoot result = new(project, source.Id)
        {
            Name = source.Name,
            RoutingMode = source.RoutingMode,
            FixedZeroBasedPort = source.FixedZeroBasedPort,
            FixedZeroBasedChannel = source.FixedZeroBasedChannel,
            ChannelMode = source.ChannelMode
        };
        result.MidiTrackIds.AddRange(source.MidiTrackIds);
        return result;
    }

    private static PureMidiTrack ClonePureMidiTrack(
        MidoraProject project,
        PureMidiTrack source,
        CancellationToken cancellationToken)
    {
        PureMidiTrack result = new(project, source.Id)
        {
            Name = source.Name,
            MidiChannelRootId = source.MidiChannelRootId,
            Color = source.Color
        };
        foreach (MidiSegment segment in source.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MidiSegment segmentCopy = new(project, segment.Id)
            {
                ProjectStartTick = segment.ProjectStartTick,
                LengthTicks = segment.LengthTicks,
                ContentOffsetTick = segment.ContentOffsetTick
            };
            for (int index = 0; index < segment.Notes.Count; index++)
            {
                if ((index & 0xff) == 0) cancellationToken.ThrowIfCancellationRequested();
                DirectMidiNote note = segment.Notes[index];
                segmentCopy.Notes.Add(new DirectMidiNote(project, note.Id)
                {
                    StartTick = note.StartTick,
                    LengthTicks = note.LengthTicks,
                    Key = note.Key,
                    NoteOnVelocity = note.NoteOnVelocity,
                    NoteOffVelocity = note.NoteOffVelocity,
                    NoteOnOrder = note.NoteOnOrder,
                    NoteOffOrder = note.NoteOffOrder
                });
            }
            for (int index = 0; index < segment.ChannelEvents.Count; index++)
            {
                if ((index & 0xff) == 0) cancellationToken.ThrowIfCancellationRequested();
                DirectMidiChannelEvent value = segment.ChannelEvents[index];
                segmentCopy.ChannelEvents.Add(new DirectMidiChannelEvent(project, value.Id)
                {
                    Tick = value.Tick,
                    Kind = value.Kind,
                    Data1 = value.Data1,
                    Data2 = value.Data2,
                    Order = value.Order
                });
            }
            for (int index = 0; index < segment.OpaqueEvents.Count; index++)
            {
                if ((index & 0xff) == 0) cancellationToken.ThrowIfCancellationRequested();
                OpaqueMidiEvent value = segment.OpaqueEvents[index];
                segmentCopy.OpaqueEvents.Add(new OpaqueMidiEvent(project, value.Id)
                {
                    Tick = value.Tick,
                    Kind = value.Kind,
                    MetaType = value.MetaType,
                    Payload = [.. value.Payload],
                    Order = value.Order
                });
            }
            result.Segments.Add(segmentCopy);
        }
        return result;
    }

    private static EventInstrument CloneInstrument(
        MidoraProject project,
        EventInstrument source,
        CancellationToken cancellationToken)
    {
        EventInstrument result = new(project, source.Id)
        {
            Name = source.Name,
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
        result.LogicalTrackIds.AddRange(source.LogicalTrackIds);
        CopyState(source.InitialState, result.InitialState, cancellationToken);

        foreach (LogicalParameterDefinition definition in source.LogicalParameters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogicalParameterDefinition definitionCopy = new(project, definition.Id)
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
            for (int index = 0; index < definition.EnumItems.Count; index++)
            {
                if ((index & 0xff) == 0) cancellationToken.ThrowIfCancellationRequested();
                LogicalParameterEnumItem item = definition.EnumItems[index];
                definitionCopy.EnumItems.Add(new LogicalParameterEnumItem(project, item.Id)
                {
                    Name = item.Name,
                    Value = item.Value
                });
            }
            result.LogicalParameters.Add(definitionCopy);
        }
        foreach (CSharpMappingFunction function in source.MappingFunctions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CSharpMappingFunction functionCopy = new(project, function.Id)
            {
                Name = function.Name,
                Body = function.Body,
                AbiVersion = function.AbiVersion
            };
            functionCopy.DeclaredContextFields.UnionWith(function.DeclaredContextFields);
            result.MappingFunctions.Add(functionCopy);
        }
        foreach (InstrumentEnvelope envelope in source.Envelopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Envelopes.Add(new InstrumentEnvelope(project, envelope.Id)
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
            });
        }
        foreach (SubVoice voice in source.SubVoices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.SubVoices.Add(CloneSubVoice(project, voice, cancellationToken));
        }
        foreach (LogicalParameterMapping mapping in source.ParameterMappings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogicalParameterMapping mappingCopy = new(
                project,
                mapping.Id,
                mapping.Steps.Id)
            {
                ParameterId = mapping.ParameterId,
                SubVoiceId = mapping.SubVoiceId,
                Target = mapping.Target
            };
            CopyTargetSettings(mapping.TargetSettings, mappingCopy.TargetSettings);
            CopyChain(project, mapping.Steps, mappingCopy.Steps, cancellationToken);
            result.ParameterMappings.Add(mappingCopy);
        }
        return result;
    }

    private static SubVoice CloneSubVoice(
        MidoraProject project,
        SubVoice source,
        CancellationToken cancellationToken)
    {
        SubVoice result = new(project, source.Id)
        {
            Name = source.Name,
            RootNoteOverride = source.RootNoteOverride
        };
        CopyState(source.InitialState, result.InitialState, cancellationToken);
        foreach (SubVoiceEventMapping mapping in source.EventMappings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubVoiceEventMapping mappingCopy = new(
                project,
                mapping.Target,
                mapping.Steps.Id);
            CopyTargetSettings(mapping.TargetSettings, mappingCopy.TargetSettings);
            CopyChain(project, mapping.Steps, mappingCopy.Steps, cancellationToken);
            result.EventMappings.Add(mappingCopy);
        }
        foreach (TemplateEvent value in source.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TemplateEvent eventCopy = new(project, value.Id)
            {
                Kind = value.Kind,
                Tick = value.Tick,
                LengthTicks = value.LengthTicks,
                Number = value.Number,
                Value = value.Value,
                SecondaryValue = value.SecondaryValue,
                HasBankMsb = value.HasBankMsb,
                HasBankLsb = value.HasBankLsb,
                FollowPitchDelta = value.FollowPitchDelta
            };
            result.Events.Add(eventCopy);
        }
        foreach (ValueCurve curve in source.Curves)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValueCurve curveCopy = new(project, curve.Id) { Target = curve.Target };
            CopyTargetSettings(curve.TargetSettings, curveCopy.TargetSettings);
            for (int index = 0; index < curve.Points.Count; index++)
            {
                if ((index & 0xff) == 0) cancellationToken.ThrowIfCancellationRequested();
                CurvePoint point = curve.Points[index];
                curveCopy.Points.Add(ClonePoint(project, point));
            }
            result.Curves.Add(curveCopy);
        }
        return result;
    }

    private static CurvePoint ClonePoint(MidoraProject project, CurvePoint source) =>
        new(project, source.Id, source.Tick, source.Value, source.Interpolation);

    private static void CopyChain(
        MidoraProject project,
        MappingChain source,
        MappingChain target,
        CancellationToken cancellationToken)
    {
        target.IsEnabled = source.IsEnabled;
        target.Clear();
        foreach (ValueMappingStep step in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            target.Add(new ValueMappingStep(project, step.Id)
            {
                IsEnabled = step.IsEnabled,
                Source = step.Source,
                Operation = step.Operation,
                LogicalParameterId = step.LogicalParameterId,
                EnvelopeId = step.EnvelopeId,
                MappingFunctionId = step.MappingFunctionId,
                Constant = step.Constant,
                SourceMinimum = step.SourceMinimum,
                SourceMaximum = step.SourceMaximum,
                TargetMinimum = step.TargetMinimum,
                TargetMaximum = step.TargetMaximum,
                InputOverflow = step.InputOverflow,
                DivideByZero = step.DivideByZero
            });
        }
    }

    private static void CopyTargetSettings(
        MidiIntegerTargetSettings source,
        MidiIntegerTargetSettings target)
    {
        target.Rounding = source.Rounding;
        target.Overflow = source.Overflow;
    }

    private static void CopyState(
        MidiInitialState source,
        MidiInitialState target,
        CancellationToken cancellationToken)
    {
        target.BankMsb = source.BankMsb;
        target.BankLsb = source.BankLsb;
        target.Program = source.Program;
        target.PitchBend = source.PitchBend;
        target.PitchBendRangeSemitones = source.PitchBendRangeSemitones;
        target.PitchBendRangeCents = source.PitchBendRangeCents;
        target.Controllers.Clear();
        target.RegisteredParameters.Clear();
        target.NonRegisteredParameters.Clear();
        foreach ((int key, int value) in source.Controllers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            target.Controllers.Add(key, value);
        }
        foreach ((int key, int value) in source.RegisteredParameters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            target.RegisteredParameters.Add(key, value);
        }
        foreach ((int key, int value) in source.NonRegisteredParameters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            target.NonRegisteredParameters.Add(key, value);
        }
    }
}
