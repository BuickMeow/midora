using Midora.Domain;

namespace Midora.Application;

public enum SegmentSelectionTransformScope
{
    ExposedContentOnly,
    ExposedContentAndSegments
}

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand FlipLogicalNotesHorizontal(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds) =>
        TransformLogicalNotes(
            "Flip logical notes horizontally",
            segmentId,
            noteIds,
            values =>
            {
                long left = values.Min(value => value.StartTick);
                long right = values.Max(value => checked(value.StartTick + value.LengthTicks));
                return values.Select(value => value with
                {
                    StartTick = checked(left + (right
                        - checked(value.StartTick + value.LengthTicks)))
                }).ToArray();
            });

    public static IProjectEditCommand FlipLogicalNotesVertical(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds) =>
        TransformLogicalNotes(
            "Flip logical notes vertically",
            segmentId,
            noteIds,
            values =>
            {
                int minimum = values.Min(value => value.Note);
                int maximum = values.Max(value => value.Note);
                return values.Select(value => value with
                {
                    Note = checked(minimum + maximum - value.Note)
                }).ToArray();
            });

    public static IProjectEditCommand ScaleLogicalNotes(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        double factor) =>
        TransformLogicalNotes(
            "Scale logical notes",
            segmentId,
            noteIds,
            values =>
            {
                ValidateScaleFactor(factor);
                long origin = values.Min(value => value.StartTick);
                return values.Select(value => value with
                {
                    StartTick = ScaleTick(origin, value.StartTick, factor),
                    LengthTicks = ScaleLength(value.LengthTicks, factor)
                }).ToArray();
            });

    public static IProjectEditCommand TransposeLogicalNotes(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        int semitones) =>
        MoveLogicalNotes(segmentId, noteIds, tickDelta: 0, pitchDelta: semitones);

    public static IProjectEditCommand FlipTemplateNotesHorizontal(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> noteIds) =>
        TransformTemplateNotes(
            "Flip template notes horizontally",
            eventInstrumentId,
            subVoiceId,
            noteIds,
            values =>
            {
                long left = values.Min(value => value.Tick);
                long right = values.Max(value => checked(value.Tick + value.LengthTicks));
                return values.Select(value => value with
                {
                    Tick = checked(left + (right
                        - checked(value.Tick + value.LengthTicks)))
                }).ToArray();
            });

    public static IProjectEditCommand FlipTemplateNotesVertical(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> noteIds) =>
        TransformTemplateNotes(
            "Flip template notes vertically",
            eventInstrumentId,
            subVoiceId,
            noteIds,
            values =>
            {
                int minimum = values.Min(value => value.Number);
                int maximum = values.Max(value => value.Number);
                return values.Select(value => value with
                {
                    Number = checked(minimum + maximum - value.Number)
                }).ToArray();
            });

    public static IProjectEditCommand ScaleTemplateNotes(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> noteIds,
        double factor) =>
        TransformTemplateNotes(
            "Scale template notes",
            eventInstrumentId,
            subVoiceId,
            noteIds,
            values =>
            {
                ValidateScaleFactor(factor);
                long origin = values.Min(value => value.Tick);
                return values.Select(value => value with
                {
                    Tick = ScaleTick(origin, value.Tick, factor),
                    LengthTicks = ScaleLength(value.LengthTicks, factor)
                }).ToArray();
            });

    public static IProjectEditCommand TransposeTemplateNotes(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> noteIds,
        int semitones) =>
        MoveTemplateNotes(
            eventInstrumentId,
            subVoiceId,
            noteIds,
            tickDelta: 0,
            pitchDelta: semitones);

    public static IProjectEditCommand FlipLogicalParameterPointsHorizontal(
        MidoraId segmentId,
        MidoraId laneId,
        IReadOnlyCollection<MidoraId> pointIds) =>
        TransformLogicalParameterPoints(
            "Flip logical parameter points horizontally",
            segmentId,
            laneId,
            pointIds,
            points =>
            {
                long left = points.Min(value => value.Tick);
                long right = points.Max(value => value.Tick);
                return points.Select(value => checked(left + (right - value.Tick))).ToArray();
            });

    public static IProjectEditCommand ScaleLogicalParameterPoints(
        MidoraId segmentId,
        MidoraId laneId,
        IReadOnlyCollection<MidoraId> pointIds,
        double factor) =>
        TransformLogicalParameterPoints(
            "Scale logical parameter points",
            segmentId,
            laneId,
            pointIds,
            points =>
            {
                ValidateScaleFactor(factor);
                long origin = points.Min(value => value.Tick);
                return points.Select(value => ScaleTick(origin, value.Tick, factor)).ToArray();
            });

    public static IProjectEditCommand FlipSubVoiceEventPointsHorizontal(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> eventIds,
        MidiValueTarget target) =>
        TransformSubVoiceEventPoints(
            "Flip SubVoice event points horizontally",
            eventInstrumentId,
            subVoiceId,
            eventIds,
            target,
            values =>
            {
                long left = values.Min(value => value.Tick);
                long right = values.Max(value => value.Tick);
                return values.Select(value => checked(left + (right - value.Tick))).ToArray();
            });

    public static IProjectEditCommand ScaleSubVoiceEventPoints(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> eventIds,
        MidiValueTarget target,
        double factor) =>
        TransformSubVoiceEventPoints(
            "Scale SubVoice event points",
            eventInstrumentId,
            subVoiceId,
            eventIds,
            target,
            values =>
            {
                ValidateScaleFactor(factor);
                long origin = values.Min(value => value.Tick);
                return values.Select(value => ScaleTick(origin, value.Tick, factor)).ToArray();
            });

    public static IProjectEditCommand FlipSegmentsHorizontal(
        IReadOnlyCollection<MidoraId> segmentIds,
        SegmentSelectionTransformScope scope) =>
        TransformSegments(
            "Flip segments horizontally",
            segmentIds,
            scope,
            SegmentContentTransformKind.FlipHorizontal,
            factor: 1,
            semitones: 0);

    public static IProjectEditCommand FlipSegmentsVertical(
        IReadOnlyCollection<MidoraId> segmentIds) =>
        TransformSegments(
            "Flip exposed Segment notes vertically",
            segmentIds,
            SegmentSelectionTransformScope.ExposedContentOnly,
            SegmentContentTransformKind.FlipVertical,
            factor: 1,
            semitones: 0);

    public static IProjectEditCommand ScaleSegments(
        IReadOnlyCollection<MidoraId> segmentIds,
        double factor,
        SegmentSelectionTransformScope scope) =>
        TransformSegments(
            "Scale segments",
            segmentIds,
            scope,
            SegmentContentTransformKind.Scale,
            factor,
            semitones: 0);

    public static IProjectEditCommand TransposeSegments(
        IReadOnlyCollection<MidoraId> segmentIds,
        int semitones) =>
        TransformSegments(
            "Transpose exposed Segment notes",
            segmentIds,
            SegmentSelectionTransformScope.ExposedContentOnly,
            SegmentContentTransformKind.Transpose,
            factor: 1,
            semitones);

    private static IProjectEditCommand TransformLogicalNotes(
        string name,
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        Func<IReadOnlyList<LogicalNoteValue>, LogicalNoteValue[]> transform) =>
        Command(name, project =>
        {
            SegmentLocation location = FindSegment(project, segmentId);
            SelectedLogicalNote[] selected = SelectLogicalNotes(location.Segment, noteIds);
            LogicalNoteValue[] old = selected.Select(value => Snapshot(value.Note)).ToArray();
            LogicalNoteValue[] replacement = transform(old);
            if (replacement.Length != old.Length)
            {
                throw new InvalidOperationException("A Logical Note transform returned the wrong result count.");
            }
            ValidateLogicalNoteBatch(replacement);
            return ResolveExactLogicalNoteCollisions(
                PrepareLogicalNoteBatch(
                    location.Track.Id,
                    selected,
                    old,
                    replacement),
                location.Segment);
        });

    private static IProjectEditCommand TransformTemplateNotes(
        string name,
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> noteIds,
        Func<IReadOnlyList<TemplateEventValue>, TemplateEventValue[]> transform) =>
        Command(name, project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            HashSet<MidoraId> requested = ValidateBatchIds(noteIds, nameof(noteIds), "Template Note");
            TemplateEventTransformEntry[] selected = voice.Events
                .Select((value, index) => new TemplateEventTransformEntry(
                    value,
                    index,
                    CaptureTemplateEvent(value)))
                .Where(value => requested.Contains(value.Event.Id))
                .ToArray();
            if (selected.Length != requested.Count
                || selected.Any(value => value.Event.Kind != TemplateEventKind.Note))
            {
                throw new ArgumentException(
                    "Every selected ID must identify a Template Note in the target SubVoice.",
                    nameof(noteIds));
            }
            TemplateEventValue[] old = selected.Select(value => value.Old).ToArray();
            TemplateEventValue[] replacement = transform(old);
            if (replacement.Length != old.Length)
            {
                throw new InvalidOperationException("A Template Note transform returned the wrong result count.");
            }
            for (int index = 0; index < replacement.Length; index++)
            {
                ValidateTemplateEventEdit(selected[index].Event, replacement[index]);
            }
            long oldLength = instrument.TemplateLengthTicks;
            long newLength = Math.Max(
                oldLength,
                replacement.Max(value => checked(value.Tick + value.LengthTicks)));
            return ResolveExactSubVoiceEventCollisions(Prepared(
                old.Where((value, index) => value != replacement[index]).Any()
                    || oldLength != newLength,
                EventInstrumentChange(eventInstrumentId),
                _ =>
                {
                    for (int index = 0; index < selected.Length; index++)
                    {
                        SetTemplateEvent(selected[index].Event, replacement[index]);
                    }
                    instrument.TemplateLengthTicks = newLength;
                },
                _ =>
                {
                    for (int index = 0; index < selected.Length; index++)
                    {
                        SetTemplateEvent(selected[index].Event, old[index]);
                    }
                    instrument.TemplateLengthTicks = oldLength;
                }), voice);
        });

    private static IProjectEditCommand TransformLogicalParameterPoints(
        string name,
        MidoraId segmentId,
        MidoraId laneId,
        IReadOnlyCollection<MidoraId> pointIds,
        Func<IReadOnlyList<CurvePoint>, long[]> transform) =>
        Command(name, project =>
        {
            SegmentLocation location = FindSegment(project, segmentId);
            LogicalParameterLane lane = FindLogicalParameterLane(location.Segment, laneId);
            SelectedCurvePoint[] selected = SelectCurvePoints(lane.Points, pointIds);
            long[] ticks = transform(selected.Select(value => value.Point).ToArray());
            if (ticks.Length != selected.Length || ticks.Any(value => value < 0 || value == long.MaxValue))
            {
                throw new ArgumentOutOfRangeException(nameof(pointIds));
            }
            CurvePoint[] replacement = selected.Select((value, index) => new CurvePoint(
                project,
                value.Point.Id,
                ticks[index],
                value.Point.Value,
                value.Point.Interpolation)).ToArray();
            ValidateNoCurvePointConflicts(lane.Points, selected, replacement);
            return PrepareCurvePointReplacementBatch(
                location.Track.Id,
                lane.Points,
                selected,
                replacement);
        });

    private static IProjectEditCommand TransformSubVoiceEventPoints(
        string name,
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> eventIds,
        MidiValueTarget target,
        Func<IReadOnlyList<TemplateEventValue>, long[]> transform) =>
        Command(name, project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            HashSet<MidoraId> requested = ValidateBatchIds(eventIds, nameof(eventIds), "Template Event");
            TemplateEventTransformEntry[] selected = voice.Events
                .Select((value, index) => new TemplateEventTransformEntry(
                    value,
                    index,
                    CaptureTemplateEvent(value)))
                .Where(value => requested.Contains(value.Event.Id))
                .ToArray();
            if (selected.Length != requested.Count
                || selected.Any(value => value.Event.Kind == TemplateEventKind.Note
                    || !TemplateEventMidiTargets.Enumerate(value.Event).Contains(target)))
            {
                throw new ArgumentException(
                    "Every selected ID must identify a point in the requested SubVoice event lane.",
                    nameof(eventIds));
            }
            long[] ticks = transform(selected.Select(value => value.Old).ToArray());
            if (ticks.Length != selected.Length)
            {
                throw new InvalidOperationException("A SubVoice event transform returned the wrong result count.");
            }
            TemplateEventValue[] replacement = selected.Select((value, index) =>
                value.Old with { Tick = ticks[index] }).ToArray();
            for (int index = 0; index < replacement.Length; index++)
            {
                ValidateTemplateEventEdit(selected[index].Event, replacement[index]);
            }
            ValidateNoTemplateEventConflicts(voice, selected, replacement);
            long oldLength = instrument.TemplateLengthTicks;
            long newLength = Math.Max(oldLength, checked(replacement.Max(value => value.Tick) + 1));
            return Prepared(
                selected.Where((value, index) => value.Old != replacement[index]).Any()
                    || oldLength != newLength,
                EventInstrumentChange(eventInstrumentId),
                _ =>
                {
                    for (int index = 0; index < selected.Length; index++)
                    {
                        SetTemplateEvent(selected[index].Event, replacement[index]);
                    }
                    instrument.TemplateLengthTicks = newLength;
                },
                _ =>
                {
                    foreach (TemplateEventTransformEntry value in selected)
                    {
                        SetTemplateEvent(value.Event, value.Old);
                    }
                    instrument.TemplateLengthTicks = oldLength;
                });
        });

    private static IProjectEditCommand TransformSegments(
        string name,
        IReadOnlyCollection<MidoraId> segmentIds,
        SegmentSelectionTransformScope scope,
        SegmentContentTransformKind kind,
        double factor,
        int semitones) =>
        Command(name, project =>
        {
            if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
            if (kind == SegmentContentTransformKind.Scale) ValidateScaleFactor(factor);
            HashSet<MidoraId> requested = ValidateBatchIds(segmentIds, nameof(segmentIds), "Segment");
            SegmentTransformEntry[] segments = project.Tracks
                .SelectMany(track => track.Segments.Select((segment, index) =>
                    new SegmentTransformEntry(track, segment, index)))
                .Where(value => requested.Contains(value.Segment.Id))
                .ToArray();
            if (segments.Length != requested.Count)
            {
                throw new ArgumentException(
                    "Every selected ID must identify a Segment.",
                    nameof(segmentIds));
            }

            long selectionLeft = segments.Min(value => value.Segment.ProjectStartTick);
            long selectionRight = segments.Max(value => value.Segment.ProjectRange.EndTick);
            List<SegmentWindowTransform> windows = [];
            List<LogicalNoteTransform> notes = [];
            List<CurvePointTransform> points = [];
            foreach (SegmentTransformEntry entry in segments)
            {
                Segment segment = entry.Segment;
                long contentLeft = segment.ContentOffsetTick;
                long contentRight = segment.ContentEndTick;
                SegmentWindow oldWindow = new(
                    segment.ProjectStartTick,
                    segment.LengthTicks,
                    segment.ContentOffsetTick);
                SegmentWindow newWindow = oldWindow;
                if (scope == SegmentSelectionTransformScope.ExposedContentAndSegments)
                {
                    newWindow = kind switch
                    {
                        SegmentContentTransformKind.FlipHorizontal => oldWindow with
                        {
                            ProjectStartTick = checked(selectionLeft + (selectionRight
                                - checked(oldWindow.ProjectStartTick + oldWindow.LengthTicks)))
                        },
                        SegmentContentTransformKind.Scale => oldWindow with
                        {
                            ProjectStartTick = ScaleTick(
                                selectionLeft,
                                oldWindow.ProjectStartTick,
                                factor),
                            LengthTicks = ScaleLength(oldWindow.LengthTicks, factor)
                        },
                        _ => oldWindow
                    };
                }
                windows.Add(new(entry, oldWindow, newWindow));

                foreach ((LogicalNote note, int index) in segment.Notes
                    .Select((value, index) => (value, index)))
                {
                    long noteEnd = checked(note.StartTick + note.LengthTicks);
                    if (note.StartTick >= contentRight || noteEnd <= contentLeft) continue;
                    LogicalNoteValue old = Snapshot(note);
                    LogicalNoteValue replacement = kind switch
                    {
                        SegmentContentTransformKind.FlipHorizontal => old with
                        {
                            StartTick = checked(contentLeft + (contentRight
                                - checked(old.StartTick + old.LengthTicks)))
                        },
                        SegmentContentTransformKind.FlipVertical => old with
                        {
                            Note = 127 - old.Note
                        },
                        SegmentContentTransformKind.Scale => old with
                        {
                            StartTick = ScaleTick(contentLeft, old.StartTick, factor),
                            LengthTicks = ScaleLength(old.LengthTicks, factor)
                        },
                        SegmentContentTransformKind.Transpose => old with
                        {
                            Note = checked(old.Note + semitones)
                        },
                        _ => old
                    };
                    bool discard = replacement.Note is < 0 or > 127;
                    if (!discard) ValidateLogicalNote(
                        replacement.StartTick,
                        replacement.LengthTicks,
                        replacement.Note,
                        replacement.Velocity);
                    notes.Add(new(segment, note, index, old, replacement, discard));
                }
                if (kind is SegmentContentTransformKind.FlipHorizontal
                    or SegmentContentTransformKind.Scale)
                {
                    foreach (LogicalParameterLane lane in segment.ParameterLanes)
                    {
                        foreach (CurvePoint point in lane.Points.Where(value =>
                            value.Tick >= contentLeft && value.Tick < contentRight))
                        {
                            long tick = kind == SegmentContentTransformKind.FlipHorizontal
                                ? checked(contentLeft + (checked(contentRight - 1) - point.Tick))
                                : ScaleTick(contentLeft, point.Tick, factor);
                            CurvePoint replacement = new(
                                project,
                                point.Id,
                                tick,
                                point.Value,
                                point.Interpolation);
                            points.Add(new(lane, point, replacement));
                        }
                    }
                }
            }

            ValidateSegmentTransformWindows(project, windows);
            foreach (IGrouping<LogicalParameterLane, CurvePointTransform> laneEdits in points
                .GroupBy(value => value.Lane))
            {
                SelectedCurvePoint[] selected = laneEdits
                    .Select(value => new SelectedCurvePoint(
                        value.Old,
                        value.Lane.Points.IndexOf(value.Old)))
                    .ToArray();
                ValidateNoCurvePointConflicts(
                    laneEdits.Key.Points,
                    selected,
                    laneEdits.Select(value => value.Replacement).ToArray());
            }

            bool changed = windows.Any(value => value.Old != value.Replacement)
                || notes.Any(value => value.Discard || value.Old != value.Replacement)
                || points.Any(value => value.Old != value.Replacement);
            return ResolveExactLogicalNoteCollisions(Prepared(
                changed,
                TrackChange(segments.Select(value => value.Track.Id).Distinct().ToArray()),
                _ =>
                {
                    foreach (CurvePointTransform point in points)
                    {
                        ReplaceRequired(
                            point.Lane.Points,
                            point.Old,
                            point.Replacement,
                            "Logical Parameter point");
                    }
                    foreach (LogicalNoteTransform note in notes)
                    {
                        if (note.Discard)
                        {
                            RemoveRequired(note.Segment.Notes, note.Note, "out-of-range Logical Note");
                        }
                        else
                        {
                            SetLogicalNote(note.Note, note.Replacement);
                        }
                    }
                    foreach (SegmentWindowTransform window in windows)
                    {
                        SetWindow(window.Entry.Segment, window.Replacement);
                    }
                    SortTransformedSegments(windows);
                },
                _ =>
                {
                    foreach (SegmentWindowTransform window in windows)
                    {
                        SetWindow(window.Entry.Segment, window.Old);
                    }
                    SortTransformedSegments(windows);
                    foreach (LogicalNoteTransform note in notes.Where(value => !value.Discard))
                    {
                        SetLogicalNote(note.Note, note.Old);
                    }
                    foreach (IGrouping<Segment, LogicalNoteTransform> group in notes
                        .Where(value => value.Discard)
                        .GroupBy(value => value.Segment))
                    {
                        foreach (LogicalNoteTransform note in group.OrderBy(value => value.Index))
                        {
                            SetLogicalNote(note.Note, note.Old);
                            InsertAt(group.Key.Notes, note.Index, note.Note, "Logical Note");
                        }
                    }
                    foreach (CurvePointTransform point in points)
                    {
                        ReplaceRequired(
                            point.Lane.Points,
                            point.Replacement,
                            point.Old,
                            "Logical Parameter point");
                    }
                }), segments.Select(value => value.Segment));
        });

    private static void ValidateNoCurvePointConflicts(
        IReadOnlyCollection<CurvePoint> all,
        IReadOnlyCollection<SelectedCurvePoint> selected,
        IReadOnlyCollection<CurvePoint> replacement)
    {
        HashSet<MidoraId> selectedIds = selected.Select(value => value.Point.Id).ToHashSet();
        long[] ticks = replacement.Select(value => value.Tick).ToArray();
        if (ticks.Distinct().Count() != ticks.Length
            || all.Any(value => !selectedIds.Contains(value.Id) && ticks.Contains(value.Tick)))
        {
            throw new InvalidOperationException(
                "The transformation would overlap event points at the same tick.");
        }
    }

    private static void ValidateNoTemplateEventConflicts(
        SubVoice voice,
        IReadOnlyCollection<TemplateEventTransformEntry> selected,
        IReadOnlyList<TemplateEventValue> replacement)
    {
        HashSet<MidoraId> selectedIds = selected.Select(value => value.Event.Id).ToHashSet();
        if (replacement.Any(value => voice.Events.Any(candidate =>
                !selectedIds.Contains(candidate.Id) && TemplateEventsConflict(candidate, value)))
            || replacement.Select((left, index) => (left, index)).Any(value =>
                replacement.Skip(value.index + 1).Any(right =>
                    TemplateEventValuesConflict(value.left, right))))
        {
            throw new InvalidOperationException(
                "The transformation would overlap SubVoice event points at the same tick.");
        }
    }

    private static void ValidateSegmentTransformWindows(
        MidoraProject project,
        IReadOnlyCollection<SegmentWindowTransform> windows)
    {
        HashSet<Segment> selected = windows.Select(value => value.Entry.Segment).ToHashSet();
        foreach (IGrouping<LogicalTrack, SegmentWindowTransform> group in windows
            .GroupBy(value => value.Entry.Track))
        {
            SegmentWindowTransform[] ordered = group
                .OrderBy(value => value.Replacement.ProjectStartTick)
                .ThenBy(value => value.Entry.Segment.Id)
                .ToArray();
            for (int index = 0; index < ordered.Length; index++)
            {
                SegmentWindow value = ordered[index].Replacement;
                ValidateSegmentRange(
                    value.ProjectStartTick,
                    value.LengthTicks,
                    value.ContentOffsetTick);
                TickRange range = new(
                    value.ProjectStartTick,
                    checked(value.ProjectStartTick + value.LengthTicks));
                if (group.Key.Segments.Any(segment =>
                    !selected.Contains(segment) && range.Intersects(segment.ProjectRange)))
                {
                    throw new InvalidOperationException(
                        "The transformation would overlap an existing Segment.");
                }
                if (index != 0)
                {
                    SegmentWindow previous = ordered[index - 1].Replacement;
                    if (checked(previous.ProjectStartTick + previous.LengthTicks)
                        > value.ProjectStartTick)
                    {
                        throw new InvalidOperationException(
                            "The transformation would overlap selected Segments.");
                    }
                }
            }
        }
    }

    private static void SortTransformedSegments(
        IReadOnlyCollection<SegmentWindowTransform> windows)
    {
        foreach (LogicalTrack track in windows.Select(value => value.Entry.Track).Distinct())
        {
            track.Segments.Sort(static (left, right) =>
            {
                int tick = left.ProjectStartTick.CompareTo(right.ProjectStartTick);
                return tick != 0 ? tick : left.Id.CompareTo(right.Id);
            });
        }
    }

    private static void ValidateScaleFactor(double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(factor),
                "The scale factor must be a finite positive number.");
        }
    }

    private static long ScaleTick(long origin, long tick, double factor)
    {
        long delta = checked(tick - origin);
        double scaled = delta * factor;
        if (!double.IsFinite(scaled)
            || scaled < long.MinValue
            || scaled > long.MaxValue)
        {
            throw new OverflowException("The scaled tick is outside the Int64 range.");
        }
        return checked(origin + (long)Math.Round(scaled, MidpointRounding.AwayFromZero));
    }

    private static long ScaleLength(long length, double factor)
    {
        double scaled = length * factor;
        if (!double.IsFinite(scaled) || scaled > long.MaxValue)
        {
            throw new OverflowException("The scaled length is outside the Int64 range.");
        }
        return Math.Max(1, (long)Math.Round(scaled, MidpointRounding.AwayFromZero));
    }

    private enum SegmentContentTransformKind
    {
        FlipHorizontal,
        FlipVertical,
        Scale,
        Transpose
    }

    private readonly record struct TemplateEventTransformEntry(
        TemplateEvent Event,
        int Index,
        TemplateEventValue Old);
    private readonly record struct SegmentTransformEntry(
        LogicalTrack Track,
        Segment Segment,
        int Index);
    private readonly record struct SegmentWindowTransform(
        SegmentTransformEntry Entry,
        SegmentWindow Old,
        SegmentWindow Replacement);
    private readonly record struct LogicalNoteTransform(
        Segment Segment,
        LogicalNote Note,
        int Index,
        LogicalNoteValue Old,
        LogicalNoteValue Replacement,
        bool Discard);
    private readonly record struct CurvePointTransform(
        LogicalParameterLane Lane,
        CurvePoint Old,
        CurvePoint Replacement);
}
