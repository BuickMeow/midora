using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand MoveLogicalParameterPoints(
        MidoraId segmentId,
        MidoraId laneId,
        IReadOnlyCollection<MidoraId> pointIds,
        long tickDelta) =>
        Command("Move logical parameter points", project =>
        {
            SegmentLocation segment = FindSegment(project, segmentId);
            LogicalParameterLane lane = FindLogicalParameterLane(segment.Segment, laneId);
            SelectedCurvePoint[] selected = SelectCurvePoints(lane.Points, pointIds);
            LogicalParameterDefinition definition = FindBoundLogicalParameter(
                project,
                segment.Track,
                lane.ParameterId);
            CurvePoint[] replacement = selected.Select(value => new CurvePoint(
                project,
                value.Point.Id,
                checked(value.Point.Tick + tickDelta),
                value.Point.Value,
                value.Point.Interpolation)).ToArray();
            ValidateLogicalParameterPointBatch(
                definition,
                lane.Points,
                selected,
                replacement);
            return PrepareCurvePointReplacementBatch(
                segment.Track.Id,
                lane.Points,
                selected,
                replacement);
        });

    public static IProjectEditCommand AdjustLogicalParameterPoints(
        MidoraId segmentId,
        MidoraId laneId,
        IReadOnlyCollection<MidoraId> pointIds,
        long tickDelta,
        double valueDelta) =>
        Command("Adjust logical parameter points", project =>
        {
            if (!double.IsFinite(valueDelta))
            {
                throw new ArgumentOutOfRangeException(nameof(valueDelta));
            }
            SegmentLocation segment = FindSegment(project, segmentId);
            LogicalParameterLane lane = FindLogicalParameterLane(segment.Segment, laneId);
            SelectedCurvePoint[] selected = SelectCurvePoints(lane.Points, pointIds);
            LogicalParameterDefinition definition = FindBoundLogicalParameter(
                project,
                segment.Track,
                lane.ParameterId);
            CurvePoint[] replacement = selected.Select(value => new CurvePoint(
                project,
                value.Point.Id,
                checked(value.Point.Tick + tickDelta),
                value.Point.Value + valueDelta,
                value.Point.Interpolation)).ToArray();
            ValidateLogicalParameterPointBatch(
                definition,
                lane.Points,
                selected,
                replacement);
            return PrepareCurvePointReplacementBatch(
                segment.Track.Id,
                lane.Points,
                selected,
                replacement);
        });

    public static IProjectEditCommand SetLogicalParameterPointValues(
        MidoraId segmentId,
        MidoraId laneId,
        IReadOnlyCollection<MidoraId> pointIds,
        double value,
        ProjectBatchValueEditMode mode) =>
        Command("Change logical parameter point values", project =>
        {
            if (!Enum.IsDefined(mode) || !double.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(
                    !Enum.IsDefined(mode) ? nameof(mode) : nameof(value));
            }
            SegmentLocation segment = FindSegment(project, segmentId);
            LogicalParameterLane lane = FindLogicalParameterLane(segment.Segment, laneId);
            SelectedCurvePoint[] selected = SelectCurvePoints(lane.Points, pointIds);
            LogicalParameterDefinition definition = FindBoundLogicalParameter(
                project,
                segment.Track,
                lane.ParameterId);
            CurvePoint[] replacement = selected.Select(item => new CurvePoint(
                project,
                item.Point.Id,
                item.Point.Tick,
                mode == ProjectBatchValueEditMode.ExactSet
                    ? value
                    : item.Point.Value + value,
                item.Point.Interpolation)).ToArray();
            ValidateLogicalParameterPointBatch(
                definition,
                lane.Points,
                selected,
                replacement);
            return PrepareCurvePointReplacementBatch(
                segment.Track.Id,
                lane.Points,
                selected,
                replacement);
        });

    public static IProjectEditCommand SetLogicalParameterPoints(
        MidoraId segmentId,
        MidoraId laneId,
        IReadOnlyCollection<MidoraId> pointIds,
        long? tick = null,
        double? value = null,
        CurveInterpolation? interpolation = null) =>
        Command("Set logical parameter points", project =>
        {
            if (tick is null && value is null && interpolation is null)
            {
                throw new ArgumentException(
                    "At least one Logical Parameter point value must be provided.",
                    nameof(tick));
            }
            if (value is double pointValue && !double.IsFinite(pointValue))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            if (interpolation is CurveInterpolation interpolationValue
                && !Enum.IsDefined(interpolationValue))
            {
                throw new ArgumentOutOfRangeException(nameof(interpolation));
            }
            SegmentLocation segment = FindSegment(project, segmentId);
            LogicalParameterLane lane = FindLogicalParameterLane(segment.Segment, laneId);
            SelectedCurvePoint[] selected = SelectCurvePoints(lane.Points, pointIds);
            LogicalParameterDefinition definition = FindBoundLogicalParameter(
                project,
                segment.Track,
                lane.ParameterId);
            CurvePoint[] replacement = selected.Select(item => new CurvePoint(
                project,
                item.Point.Id,
                tick ?? item.Point.Tick,
                value ?? item.Point.Value,
                interpolation ?? item.Point.Interpolation)).ToArray();
            ValidateLogicalParameterPointBatch(
                definition,
                lane.Points,
                selected,
                replacement);
            return PrepareCurvePointReplacementBatch(
                segment.Track.Id,
                lane.Points,
                selected,
                replacement);
        });

    public static IProjectEditCommand DeleteLogicalParameterPoints(
        MidoraId segmentId,
        MidoraId laneId,
        IReadOnlyCollection<MidoraId> pointIds) =>
        Command("Delete logical parameter points", project =>
        {
            SegmentLocation segment = FindSegment(project, segmentId);
            LogicalParameterLane lane = FindLogicalParameterLane(segment.Segment, laneId);
            SelectedCurvePoint[] selected = SelectCurvePoints(lane.Points, pointIds);
            return PrepareCurvePointDeleteBatch(
                TrackChange(segment.Track.Id),
                lane.Points,
                selected,
                "Logical Parameter point");
        });

    public static IProjectEditCommand DeleteValueCurvePoints(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId curveId,
        IReadOnlyCollection<MidoraId> pointIds) =>
        Command("Delete value curve points", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            ValueCurve curve = FindValueCurve(voice, curveId);
            SelectedCurvePoint[] selected = SelectCurvePoints(curve.Points, pointIds);
            return PrepareCurvePointDeleteBatch(
                EventInstrumentChange(eventInstrumentId),
                curve.Points,
                selected,
                "Value Curve point");
        });

    public static IProjectEditCommand AdjustValueCurvePoints(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId curveId,
        IReadOnlyCollection<MidoraId> pointIds,
        long tickDelta,
        double valueDelta) =>
        Command("Adjust value curve points", project =>
        {
            if (!double.IsFinite(valueDelta))
            {
                throw new ArgumentOutOfRangeException(nameof(valueDelta));
            }
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            ValueCurve curve = FindValueCurve(voice, curveId);
            SelectedCurvePoint[] selected = SelectCurvePoints(curve.Points, pointIds);
            CurvePoint[] replacement = selected.Select(value => new CurvePoint(
                project,
                value.Point.Id,
                checked(value.Point.Tick + tickDelta),
                value.Point.Value + valueDelta,
                value.Point.Interpolation)).ToArray();
            ValidateValueCurvePointBatch(curve, selected, replacement);
            long oldTemplateLength = instrument.TemplateLengthTicks;
            long replacementTemplateLength = Math.Max(
                oldTemplateLength,
                checked(replacement.Max(value => value.Tick) + 1));
            CurvePoint[] old = selected.Select(value => value.Point).ToArray();
            return Prepared(
                old.Where((value, index) => value != replacement[index]).Any()
                    || oldTemplateLength != replacementTemplateLength,
                EventInstrumentChange(eventInstrumentId),
                _ =>
                {
                    ReplaceCurvePointBatch(curve.Points, old, replacement);
                    instrument.TemplateLengthTicks = replacementTemplateLength;
                },
                _ =>
                {
                    ReplaceCurvePointBatch(curve.Points, replacement, old);
                    instrument.TemplateLengthTicks = oldTemplateLength;
                });
        });

    public static IProjectEditCommand SetValueCurvePoints(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidoraId curveId,
        IReadOnlyCollection<MidoraId> pointIds,
        long? tick = null,
        double? value = null,
        CurveInterpolation? interpolation = null) =>
        Command("Set value curve points", project =>
        {
            if (tick is null && value is null && interpolation is null)
            {
                throw new ArgumentException(
                    "At least one Value Curve point value must be provided.",
                    nameof(tick));
            }
            if (value is double pointValue && !double.IsFinite(pointValue))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            if (interpolation is CurveInterpolation interpolationValue
                && !Enum.IsDefined(interpolationValue))
            {
                throw new ArgumentOutOfRangeException(nameof(interpolation));
            }
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            ValueCurve curve = FindValueCurve(voice, curveId);
            SelectedCurvePoint[] selected = SelectCurvePoints(curve.Points, pointIds);
            CurvePoint[] replacement = selected.Select(item => new CurvePoint(
                project,
                item.Point.Id,
                tick ?? item.Point.Tick,
                value ?? item.Point.Value,
                interpolation ?? item.Point.Interpolation)).ToArray();
            ValidateValueCurvePointBatch(curve, selected, replacement);
            long oldTemplateLength = instrument.TemplateLengthTicks;
            long replacementTemplateLength = Math.Max(
                oldTemplateLength,
                checked(replacement.Max(item => item.Tick) + 1));
            CurvePoint[] old = selected.Select(item => item.Point).ToArray();
            return Prepared(
                old.Where((item, index) => item != replacement[index]).Any()
                    || oldTemplateLength != replacementTemplateLength,
                EventInstrumentChange(eventInstrumentId),
                _ =>
                {
                    ReplaceCurvePointBatch(curve.Points, old, replacement);
                    instrument.TemplateLengthTicks = replacementTemplateLength;
                },
                _ =>
                {
                    ReplaceCurvePointBatch(curve.Points, replacement, old);
                    instrument.TemplateLengthTicks = oldTemplateLength;
                });
        });

    public static IProjectEditCommand DeleteTemplateEvents(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> templateEventIds) =>
        Command("Delete template events", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            ArgumentNullException.ThrowIfNull(templateEventIds);
            HashSet<MidoraId> requested = ValidateBatchIds(
                templateEventIds,
                nameof(templateEventIds),
                "Template Event");
            IndexedTemplateEvent[] selected = voice.Events
                .Select((value, index) => new IndexedTemplateEvent(value, index))
                .Where(value => requested.Contains(value.Event.Id))
                .ToArray();
            if (selected.Length != requested.Count)
            {
                throw new ArgumentException(
                    "Every selected Template Event must belong to the target SubVoice.",
                    nameof(templateEventIds));
            }
            return Prepared(
                hasChanges: true,
                EventInstrumentChange(eventInstrumentId),
                _ =>
                {
                    foreach (IndexedTemplateEvent value in selected)
                    {
                        RemoveRequired(voice.Events, value.Event, "Template Event");
                    }
                },
                _ =>
                {
                    foreach (IndexedTemplateEvent value in selected.OrderBy(value => value.Index))
                    {
                        InsertAt(voice.Events, value.Index, value.Event, "Template Event");
                    }
                });
        });

    public static IProjectEditCommand DeleteConductorEvents(
        IReadOnlyCollection<MidoraId> eventIds) =>
        Command("Delete conductor events", project =>
        {
            ArgumentNullException.ThrowIfNull(eventIds);
            HashSet<MidoraId> requested = ValidateBatchIds(
                eventIds,
                nameof(eventIds),
                "Conductor event");
            IndexedConductorEvent<TempoChange>[] tempos = SelectConductorEvents(
                project.Conductor.Tempos,
                requested,
                value => value.Id);
            IndexedConductorEvent<TimeSignatureChange>[] timeSignatures = SelectConductorEvents(
                project.Conductor.TimeSignatures,
                requested,
                value => value.Id);
            IndexedConductorEvent<KeySignatureChange>[] keySignatures = SelectConductorEvents(
                project.Conductor.KeySignatures,
                requested,
                value => value.Id);
            IndexedConductorEvent<ProjectMarker>[] markers = SelectConductorEvents(
                project.Conductor.Markers,
                requested,
                value => value.Id);
            int selectedCount = tempos.Length
                + timeSignatures.Length
                + keySignatures.Length
                + markers.Length;
            if (selectedCount != requested.Count)
            {
                throw new ArgumentException(
                    "Every selected ID must identify an ordinary Conductor event.",
                    nameof(eventIds));
            }
            if (tempos.Any(value => value.Value.Tick == 0)
                || timeSignatures.Any(value => value.Value.Tick == 0))
            {
                throw new InvalidOperationException(
                    "The required tick 0 Tempo and Time Signature cannot be deleted.");
            }
            return Prepared(
                hasChanges: true,
                ConductorChange(),
                _ =>
                {
                    RemoveConductorBatch(project.Conductor.Tempos, tempos, "Tempo");
                    RemoveConductorBatch(
                        project.Conductor.TimeSignatures,
                        timeSignatures,
                        "Time Signature");
                    RemoveConductorBatch(
                        project.Conductor.KeySignatures,
                        keySignatures,
                        "Key Signature");
                    RemoveConductorBatch(project.Conductor.Markers, markers, "Marker");
                },
                _ =>
                {
                    RestoreConductorBatch(project.Conductor.Tempos, tempos, "Tempo");
                    RestoreConductorBatch(
                        project.Conductor.TimeSignatures,
                        timeSignatures,
                        "Time Signature");
                    RestoreConductorBatch(
                        project.Conductor.KeySignatures,
                        keySignatures,
                        "Key Signature");
                    RestoreConductorBatch(project.Conductor.Markers, markers, "Marker");
                });
        });

    private static IPreparedProjectEdit PrepareCurvePointReplacementBatch(
        MidoraId trackId,
        List<CurvePoint> points,
        SelectedCurvePoint[] selected,
        CurvePoint[] replacement) =>
        Prepared(
            selected.Where((value, index) => value.Point != replacement[index]).Any(),
            TrackChange(trackId),
            _ => ReplaceCurvePointBatch(points, selected.Select(value => value.Point).ToArray(), replacement),
            _ => ReplaceCurvePointBatch(points, replacement, selected.Select(value => value.Point).ToArray()));

    private static void ReplaceCurvePointBatch(
        List<CurvePoint> points,
        CurvePoint[] expected,
        CurvePoint[] replacement)
    {
        for (int index = 0; index < expected.Length; index++)
        {
            ReplaceRequired(points, expected[index], replacement[index], "Curve Point");
        }
    }

    private static void ValidateLogicalParameterPointBatch(
        LogicalParameterDefinition definition,
        IReadOnlyCollection<CurvePoint> allPoints,
        IReadOnlyCollection<SelectedCurvePoint> selected,
        IReadOnlyCollection<CurvePoint> replacement)
    {
        foreach (CurvePoint point in replacement)
        {
            if (point.Tick < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(replacement));
            }
            ValidatePointValue(definition, point.Value, point.Interpolation);
        }
        HashSet<MidoraId> selectedIds = selected.Select(value => value.Point.Id).ToHashSet();
        long[] ticks = replacement.Select(value => value.Tick).ToArray();
        if (ticks.Distinct().Count() != ticks.Length
            || allPoints.Any(value => !selectedIds.Contains(value.Id) && ticks.Contains(value.Tick)))
        {
            throw new InvalidOperationException(
                "The Logical Parameter point batch would create duplicate point ticks.");
        }
    }

    private static void ValidateValueCurvePointBatch(
        ValueCurve curve,
        IReadOnlyCollection<SelectedCurvePoint> selected,
        IReadOnlyCollection<CurvePoint> replacement)
    {
        (double minimum, double maximum) = ValueCurveTargetRange(curve.Target);
        foreach (CurvePoint point in replacement)
        {
            if (point.Tick < 0 || point.Tick == long.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(replacement));
            }
            if (!double.IsFinite(point.Value) || !Enum.IsDefined(point.Interpolation))
            {
                throw new ArgumentOutOfRangeException(nameof(replacement));
            }
            if ((point.Value < minimum || point.Value > maximum)
                && curve.TargetSettings.Overflow == MappingOverflow.Fail)
            {
                throw new ArgumentOutOfRangeException(nameof(replacement));
            }
        }
        HashSet<MidoraId> selectedIds = selected.Select(value => value.Point.Id).ToHashSet();
        long[] ticks = replacement.Select(value => value.Tick).ToArray();
        if (ticks.Distinct().Count() != ticks.Length
            || curve.Points.Any(value => !selectedIds.Contains(value.Id) && ticks.Contains(value.Tick)))
        {
            throw new InvalidOperationException(
                "The Value Curve point batch would create duplicate point ticks.");
        }
    }

    private static SelectedCurvePoint[] SelectCurvePoints(
        List<CurvePoint> points,
        IReadOnlyCollection<MidoraId> pointIds)
    {
        ArgumentNullException.ThrowIfNull(pointIds);
        HashSet<MidoraId> requested = ValidateBatchIds(
            pointIds,
            nameof(pointIds),
            "Curve Point");
        SelectedCurvePoint[] selected = points
            .Select((value, index) => new SelectedCurvePoint(value, index))
            .Where(value => requested.Contains(value.Point.Id))
            .ToArray();
        if (selected.Length != requested.Count)
        {
            throw new ArgumentException(
                "Every selected Curve Point must belong to the target Curve.",
                nameof(pointIds));
        }
        return selected;
    }

    private static IPreparedProjectEdit PrepareCurvePointDeleteBatch(
        ProjectChangeSet changes,
        List<CurvePoint> points,
        SelectedCurvePoint[] selected,
        string objectName) =>
        Prepared(
            hasChanges: true,
            changes,
            _ =>
            {
                foreach (SelectedCurvePoint value in selected)
                {
                    RemoveRequired(points, value.Point, objectName);
                }
            },
            _ =>
            {
                foreach (SelectedCurvePoint value in selected.OrderBy(value => value.Index))
                {
                    InsertAt(points, value.Index, value.Point, objectName);
                }
            });

    private static HashSet<MidoraId> ValidateBatchIds(
        IReadOnlyCollection<MidoraId> values,
        string parameterName,
        string objectName)
    {
        if (values.Count == 0)
        {
            throw new ArgumentException(
                $"At least one {objectName} must be selected.",
                parameterName);
        }
        HashSet<MidoraId> result = [];
        foreach (MidoraId value in values)
        {
            if (value == default || !result.Add(value))
            {
                throw new ArgumentException(
                    $"{objectName} selections must contain distinct valid stable IDs.",
                    parameterName);
            }
        }
        return result;
    }

    private static IndexedConductorEvent<T>[] SelectConductorEvents<T>(
        List<T> values,
        IReadOnlySet<MidoraId> requested,
        Func<T, MidoraId> getId) =>
        values.Select((value, index) => new IndexedConductorEvent<T>(value, index))
            .Where(value => requested.Contains(getId(value.Value)))
            .ToArray();

    private static void RemoveConductorBatch<T>(
        List<T> target,
        IEnumerable<IndexedConductorEvent<T>> selected,
        string objectName)
        where T : class
    {
        foreach (IndexedConductorEvent<T> value in selected)
        {
            RemoveRequired(target, value.Value, objectName);
        }
    }

    private static void RestoreConductorBatch<T>(
        List<T> target,
        IEnumerable<IndexedConductorEvent<T>> selected,
        string objectName)
        where T : class
    {
        foreach (IndexedConductorEvent<T> value in selected.OrderBy(value => value.Index))
        {
            InsertAt(target, value.Index, value.Value, objectName);
        }
    }

    private readonly record struct SelectedCurvePoint(CurvePoint Point, int Index);
    private readonly record struct IndexedConductorEvent<T>(T Value, int Index);
}
