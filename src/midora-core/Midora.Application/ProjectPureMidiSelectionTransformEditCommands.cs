using System.Diagnostics;
using Midora.Compiler;
using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand FlipDirectMidiNotesHorizontal(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds) =>
        ChangeDirectMidiNotes(
            "Flip Direct MIDI Notes horizontally",
            segmentId,
            noteIds,
            values =>
            {
                long left = values.Min(value => value.StartTick);
                long right = values.Max(value => checked(value.StartTick + value.LengthTicks));
                return values.Select(value => value with
                {
                    StartTick = checked(left + right - checked(value.StartTick + value.LengthTicks))
                }).ToArray();
            });

    public static IProjectEditCommand FlipDirectMidiNotesVertical(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds) =>
        ChangeDirectMidiNotes(
            "Flip Direct MIDI Notes vertically",
            segmentId,
            noteIds,
            values =>
            {
                int minimum = values.Min(value => value.Key);
                int maximum = values.Max(value => value.Key);
                return values.Select(value => value with
                {
                    Key = checked(minimum + maximum - value.Key)
                }).ToArray();
            });

    public static IProjectEditCommand ScaleDirectMidiNotes(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        double factor) =>
        ChangeDirectMidiNotes(
            "Scale Direct MIDI Notes",
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

    public static IProjectEditCommand TransposeDirectMidiNotes(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        int semitones) =>
        MoveDirectMidiNotes(segmentId, noteIds, tickDelta: 0, keyDelta: semitones);

    public static IProjectEditCommand FlipDirectMidiEventPointsHorizontal(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> eventIds) =>
        TransformDirectMidiEventPoints(
            "Flip Direct MIDI Event points horizontally",
            segmentId,
            eventIds,
            values =>
            {
                long left = values.Min(value => value.Tick);
                long right = values.Max(value => value.Tick);
                return values.Select(value => value with
                {
                    Tick = checked(left + right - value.Tick)
                }).ToArray();
            });

    public static IProjectEditCommand ScaleDirectMidiEventPoints(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> eventIds,
        double factor) =>
        TransformDirectMidiEventPoints(
            "Scale Direct MIDI Event points",
            segmentId,
            eventIds,
            values =>
            {
                ValidateScaleFactor(factor);
                long origin = values.Min(value => value.Tick);
                return values.Select(value => value with
                {
                    Tick = ScaleTick(origin, value.Tick, factor)
                }).ToArray();
            });

    public static IProjectEditCommand BatchEditDirectMidiNotes(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        BatchEditExpressionProgram program) =>
        Command("Batch edit Direct MIDI Notes", project =>
        {
            ArgumentNullException.ThrowIfNull(program);
            ValidateNoteBatchProgram(program);
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            DirectNoteSelection[] selected = SelectDirectNotes(location.Segment, noteIds);
            DirectNoteValue[] old = selected.Select(value => SnapshotDirectNote(value.Note)).ToArray();
            long relativeOrigin = old.Min(value => value.StartTick);
            Stopwatch clock = Stopwatch.StartNew();
            DirectNoteBatchResult[] replacement = old.Select(value =>
            {
                BatchEditValues calculated = program.Evaluate(
                    new(
                        Velocity: value.NoteOnVelocity,
                        PointValue: 0,
                        KeyNumber: value.Key,
                        Gate: value.LengthTicks,
                        Tick: value.StartTick,
                        RelativeTick: checked(value.StartTick - relativeOrigin)),
                    clock,
                    BatchExpressionTimeout);
                long? tick = RoundTickOrDiscard(calculated.Tick);
                double roundedKey = Math.Round(calculated.KeyNumber, MidpointRounding.AwayFromZero);
                bool keyOutOfRange = roundedKey is < 0 or > 127;
                DirectNoteValue result = value with
                {
                    StartTick = tick ?? 0,
                    LengthTicks = RoundAndClamp(calculated.Gate, 1, long.MaxValue),
                    Key = keyOutOfRange ? 0 : checked((int)roundedKey),
                    NoteOnVelocity = checked((int)RoundAndClamp(calculated.Velocity, 1, 127))
                };
                bool discard = tick is null || keyOutOfRange;
                if (!discard)
                {
                    ValidateDirectMidiNote(
                        result.StartTick,
                        result.LengthTicks,
                        result.Key,
                        result.NoteOnVelocity,
                        result.NoteOffVelocity);
                }
                return new DirectNoteBatchResult(result, discard);
            }).ToArray();
            bool[] discarded = replacement.Select(value => value.Discard).ToArray();
            Dictionary<(long Tick, int Key), (long Tick, int Key)> occupied = location.Segment.Notes
                .Where(value => selected.All(selectedValue => !ReferenceEquals(selectedValue.Note, value)))
                .GroupBy(value => (value.StartTick, value.Key))
                .ToDictionary(group => group.Key, group => group.Key);
            for (int index = 0; index < replacement.Length; index++)
            {
                if (!discarded[index])
                {
                    (long Tick, int Key) source = (old[index].StartTick, old[index].Key);
                    (long Tick, int Key) target = (
                        replacement[index].Value.StartTick,
                        replacement[index].Value.Key);
                    if (occupied.TryGetValue(target, out var incumbentSource)
                        && incumbentSource != source)
                    {
                        discarded[index] = true;
                    }
                    else
                    {
                        occupied[target] = source;
                    }
                }
            }
            return Prepared(
                old.Where((value, index) => value != replacement[index].Value || discarded[index]).Any(),
                PureMidiTrackChange(location.Track.Id),
                _ =>
                {
                    for (int index = 0; index < selected.Length; index++)
                    {
                        if (discarded[index]) location.Segment.Notes.Remove(selected[index].Note);
                        else ApplyDirectNote(selected[index].Note, replacement[index].Value);
                    }
                },
                _ =>
                {
                    for (int index = 0; index < selected.Length; index++)
                        ApplyDirectNote(selected[index].Note, old[index]);
                    foreach (DirectNoteSelection value in selected
                        .Where((_, index) => discarded[index])
                        .OrderBy(value => value.Index))
                    {
                        InsertAt(location.Segment.Notes, value.Index, value.Note, "Direct MIDI Note");
                    }
                });
        });

    public static IProjectEditCommand BatchEditDirectMidiEventPoints(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> eventIds,
        BatchEditExpressionProgram program) =>
        Command("Batch edit Direct MIDI Event points", project =>
        {
            ArgumentNullException.ThrowIfNull(program);
            ValidatePointBatchProgram(program);
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            DirectEventSelection[] selected = SelectDirectEvents(location.Segment, eventIds);
            EnsureSameDirectMidiEventLane(selected);
            int maximum = selected[0].Event.Kind == DirectMidiChannelEventKind.PitchBend ? 16383 : 127;
            ValidateDirectRange(program, BatchEditField.PointValue, 0, maximum);
            DirectMidiEventValue[] old = selected.Select(value => value.Original).ToArray();
            long relativeOrigin = old.Min(value => value.Tick);
            Stopwatch clock = Stopwatch.StartNew();
            DirectEventBatchResult[] replacement = old.Select(value =>
            {
                int pointValue = DirectMidiEventPointValue(value);
                BatchEditValues calculated = program.Evaluate(
                    new(
                        Velocity: 0,
                        PointValue: pointValue,
                        KeyNumber: 0,
                        Gate: 0,
                        Tick: value.Tick,
                        RelativeTick: checked(value.Tick - relativeOrigin)),
                    clock,
                    BatchExpressionTimeout);
                long? tick = RoundTickOrDiscard(calculated.Tick);
                int scalar = checked((int)RoundAndClamp(calculated.PointValue, 0, maximum));
                DirectMidiEventValue result = WithDirectMidiEventPointValue(
                    value with { Tick = tick ?? 0 },
                    scalar);
                bool discard = tick is null;
                if (!discard)
                    ValidateDirectMidiEvent(result.Tick, result.Kind, result.Data1, result.Data2);
                return new DirectEventBatchResult(result, discard);
            }).ToArray();
            return Prepared(
                old.Where((value, index) => value != replacement[index].Value || replacement[index].Discard).Any(),
                PureMidiTrackChange(location.Track.Id),
                _ =>
                {
                    for (int index = 0; index < selected.Length; index++)
                    {
                        if (replacement[index].Discard)
                            location.Segment.ChannelEvents.Remove(selected[index].Event);
                        else
                            ApplyDirectEvent(selected[index].Event, replacement[index].Value);
                    }
                },
                _ =>
                {
                    for (int index = 0; index < selected.Length; index++)
                        ApplyDirectEvent(selected[index].Event, old[index]);
                    foreach (DirectEventSelection value in selected
                        .Where((_, index) => replacement[index].Discard)
                        .OrderBy(value => value.Index))
                    {
                        InsertAt(location.Segment.ChannelEvents, value.Index, value.Event, "Direct MIDI Event");
                    }
                });
        });

    public static IProjectEditCommand FlipMidiSegmentsHorizontal(
        IReadOnlyCollection<MidoraId> segmentIds,
        SegmentSelectionTransformScope scope) =>
        TransformMidiSegmentSelection(
            "Flip MIDI Segments horizontally",
            segmentIds,
            scope,
            MidiSegmentContentTransformKind.FlipHorizontal,
            factor: 1,
            semitones: 0);

    public static IProjectEditCommand FlipMidiSegmentsVertical(
        IReadOnlyCollection<MidoraId> segmentIds) =>
        TransformMidiSegmentSelection(
            "Flip exposed MIDI Segment Notes vertically",
            segmentIds,
            SegmentSelectionTransformScope.ExposedContentOnly,
            MidiSegmentContentTransformKind.FlipVertical,
            factor: 1,
            semitones: 0);

    public static IProjectEditCommand ScaleMidiSegments(
        IReadOnlyCollection<MidoraId> segmentIds,
        double factor,
        SegmentSelectionTransformScope scope) =>
        TransformMidiSegmentSelection(
            "Scale MIDI Segments",
            segmentIds,
            scope,
            MidiSegmentContentTransformKind.Scale,
            factor,
            semitones: 0);

    public static IProjectEditCommand TransposeMidiSegments(
        IReadOnlyCollection<MidoraId> segmentIds,
        int semitones) =>
        TransformMidiSegmentSelection(
            "Transpose exposed MIDI Segment Notes",
            segmentIds,
            SegmentSelectionTransformScope.ExposedContentOnly,
            MidiSegmentContentTransformKind.Transpose,
            factor: 1,
            semitones);

    public static IProjectEditCommand BatchEditMidiSegmentExposedNotes(
        IReadOnlyCollection<MidoraId> segmentIds,
        BatchEditExpressionProgram program) =>
        Command("Batch edit exposed MIDI Segment Notes", project =>
        {
            ArgumentNullException.ThrowIfNull(program);
            ValidateNoteBatchProgram(program);
            MidiSegmentSelection[] segments = SelectMidiSegments(project, segmentIds);
            List<MidiSegmentDirectNoteSelection> selected = [];
            foreach (MidiSegmentSelection segment in segments)
            {
                long contentLeft = segment.Segment.ContentOffsetTick;
                long contentRight = segment.Segment.ContentEndTick;
                selected.AddRange(segment.Segment.Notes
                    .Select((note, index) => new MidiSegmentDirectNoteSelection(
                        segment.Segment,
                        note,
                        index,
                        SnapshotDirectNote(note)))
                    .Where(value => value.Old.StartTick < contentRight
                        && checked(value.Old.StartTick + value.Old.LengthTicks) > contentLeft));
            }
            if (selected.Count == 0)
                throw new InvalidOperationException("The selected MIDI Segments expose no Direct MIDI Notes to batch edit.");

            long relativeOrigin = selected.Min(value => value.Old.StartTick);
            Stopwatch clock = Stopwatch.StartNew();
            MidiSegmentDirectNoteTransform[] notes = selected.Select(value =>
            {
                BatchEditValues calculated = program.Evaluate(
                    new(
                        Velocity: value.Old.NoteOnVelocity,
                        PointValue: 0,
                        KeyNumber: value.Old.Key,
                        Gate: value.Old.LengthTicks,
                        Tick: value.Old.StartTick,
                        RelativeTick: checked(value.Old.StartTick - relativeOrigin)),
                    clock,
                    BatchExpressionTimeout);
                long? tick = RoundTickOrDiscard(calculated.Tick);
                double roundedKey = Math.Round(calculated.KeyNumber, MidpointRounding.AwayFromZero);
                bool keyOutOfRange = roundedKey is < 0 or > 127;
                DirectNoteValue replacement = value.Old with
                {
                    StartTick = tick ?? 0,
                    LengthTicks = RoundAndClamp(calculated.Gate, 1, long.MaxValue),
                    Key = keyOutOfRange ? 0 : checked((int)roundedKey),
                    NoteOnVelocity = checked((int)RoundAndClamp(calculated.Velocity, 1, 127))
                };
                bool discard = tick is null || keyOutOfRange;
                if (!discard)
                {
                    ValidateDirectMidiNote(
                        replacement.StartTick,
                        replacement.LengthTicks,
                        replacement.Key,
                        replacement.NoteOnVelocity,
                        replacement.NoteOffVelocity);
                }
                return new MidiSegmentDirectNoteTransform(
                    value.Segment,
                    value.Note,
                    value.Index,
                    value.Old,
                    replacement,
                    discard);
            }).ToArray();
            return PrepareMidiSegmentContentEdit(segments, [], notes, [], []);
        });

    private static IProjectEditCommand TransformMidiSegmentSelection(
        string name,
        IReadOnlyCollection<MidoraId> segmentIds,
        SegmentSelectionTransformScope scope,
        MidiSegmentContentTransformKind kind,
        double factor,
        int semitones) =>
        Command(name, project =>
        {
            if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
            if (kind == MidiSegmentContentTransformKind.Scale) ValidateScaleFactor(factor);
            MidiSegmentSelection[] segments = SelectMidiSegments(project, segmentIds);
            long selectionLeft = segments.Min(value => value.Segment.ProjectStartTick);
            long selectionRight = segments.Max(value => value.Segment.ProjectRange.EndTick);
            List<MidiSegmentWindowTransform> windows = [];
            List<MidiSegmentDirectNoteTransform> notes = [];
            List<MidiSegmentDirectEventTransform> events = [];
            List<MidiSegmentOpaqueEventTransform> opaque = [];

            foreach (MidiSegmentSelection entry in segments)
            {
                MidiSegment segment = entry.Segment;
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
                        MidiSegmentContentTransformKind.FlipHorizontal => oldWindow with
                        {
                            ProjectStartTick = checked(selectionLeft + selectionRight
                                - checked(oldWindow.ProjectStartTick + oldWindow.LengthTicks))
                        },
                        MidiSegmentContentTransformKind.Scale => oldWindow with
                        {
                            ProjectStartTick = ScaleTick(selectionLeft, oldWindow.ProjectStartTick, factor),
                            LengthTicks = ScaleLength(oldWindow.LengthTicks, factor)
                        },
                        _ => oldWindow
                    };
                }
                windows.Add(new(entry, oldWindow, newWindow));

                foreach ((DirectMidiNote note, int index) in segment.Notes.Select((value, index) => (value, index)))
                {
                    DirectNoteValue old = SnapshotDirectNote(note);
                    long noteEnd = checked(old.StartTick + old.LengthTicks);
                    if (old.StartTick >= contentRight || noteEnd <= contentLeft) continue;
                    DirectNoteValue replacement = kind switch
                    {
                        MidiSegmentContentTransformKind.FlipHorizontal => old with
                        {
                            StartTick = checked(contentLeft + contentRight
                                - checked(old.StartTick + old.LengthTicks))
                        },
                        MidiSegmentContentTransformKind.FlipVertical => old with { Key = 127 - old.Key },
                        MidiSegmentContentTransformKind.Scale => old with
                        {
                            StartTick = ScaleTick(contentLeft, old.StartTick, factor),
                            LengthTicks = ScaleLength(old.LengthTicks, factor)
                        },
                        MidiSegmentContentTransformKind.Transpose => old with
                        {
                            Key = checked(old.Key + semitones)
                        },
                        _ => old
                    };
                    bool discard = replacement.Key is < 0 or > 127;
                    if (!discard)
                    {
                        ValidateDirectMidiNote(
                            replacement.StartTick,
                            replacement.LengthTicks,
                            replacement.Key,
                            replacement.NoteOnVelocity,
                            replacement.NoteOffVelocity);
                    }
                    notes.Add(new(segment, note, index, old, replacement, discard));
                }

                if (kind is MidiSegmentContentTransformKind.FlipHorizontal
                    or MidiSegmentContentTransformKind.Scale)
                {
                    foreach (DirectMidiChannelEvent value in segment.ChannelEvents.Where(value =>
                        value.Tick >= contentLeft && value.Tick < contentRight))
                    {
                        DirectMidiEventValue old = SnapshotDirectEvent(value);
                        DirectMidiEventValue replacement = old with
                        {
                            Tick = kind == MidiSegmentContentTransformKind.FlipHorizontal
                                ? checked(contentLeft + checked(contentRight - 1) - old.Tick)
                                : ScaleTick(contentLeft, old.Tick, factor)
                        };
                        ValidateDirectMidiEvent(
                            replacement.Tick,
                            replacement.Kind,
                            replacement.Data1,
                            replacement.Data2);
                        events.Add(new(value, old, replacement));
                    }
                    foreach (OpaqueMidiEvent value in segment.OpaqueEvents.Where(value =>
                        value.Tick >= contentLeft && value.Tick < contentRight))
                    {
                        long replacement = kind == MidiSegmentContentTransformKind.FlipHorizontal
                            ? checked(contentLeft + checked(contentRight - 1) - value.Tick)
                            : ScaleTick(contentLeft, value.Tick, factor);
                        if (replacement < 0) throw new ArgumentOutOfRangeException(nameof(segmentIds));
                        opaque.Add(new(value, value.Tick, replacement));
                    }
                }
            }

            ValidateMidiSegmentTransformWindows(windows);
            return PrepareMidiSegmentContentEdit(segments, windows, notes, events, opaque);
        });

    private static IPreparedProjectEdit PrepareMidiSegmentContentEdit(
        IReadOnlyCollection<MidiSegmentSelection> segments,
        IReadOnlyCollection<MidiSegmentWindowTransform> windows,
        IReadOnlyCollection<MidiSegmentDirectNoteTransform> notes,
        IReadOnlyCollection<MidiSegmentDirectEventTransform> events,
        IReadOnlyCollection<MidiSegmentOpaqueEventTransform> opaque) =>
        Prepared(
            windows.Any(value => value.Old != value.Replacement)
                || notes.Any(value => value.Discard || value.Old != value.Replacement)
                || events.Any(value => value.Old != value.Replacement)
                || opaque.Any(value => value.OldTick != value.ReplacementTick),
            PureMidiTrackChange(segments.Select(value => value.Track.Id).Distinct().ToArray()),
            _ =>
            {
                foreach (MidiSegmentDirectEventTransform value in events)
                    ApplyDirectEvent(value.Event, value.Replacement);
                foreach (MidiSegmentOpaqueEventTransform value in opaque)
                    value.Event.Tick = value.ReplacementTick;
                foreach (MidiSegmentDirectNoteTransform value in notes)
                {
                    if (value.Discard) value.Segment.Notes.Remove(value.Note);
                    else ApplyDirectNote(value.Note, value.Replacement);
                }
                foreach (MidiSegmentWindowTransform value in windows)
                    SetMidiSegmentWindow(value.Entry.Segment, value.Replacement);
                SortTransformedMidiSegments(windows);
            },
            _ =>
            {
                foreach (MidiSegmentWindowTransform value in windows)
                    SetMidiSegmentWindow(value.Entry.Segment, value.Old);
                SortTransformedMidiSegments(windows);
                foreach (MidiSegmentDirectNoteTransform value in notes.Where(value => !value.Discard))
                    ApplyDirectNote(value.Note, value.Old);
                foreach (IGrouping<MidiSegment, MidiSegmentDirectNoteTransform> group in notes
                    .Where(value => value.Discard)
                    .GroupBy(value => value.Segment))
                {
                    foreach (MidiSegmentDirectNoteTransform value in group.OrderBy(value => value.Index))
                    {
                        ApplyDirectNote(value.Note, value.Old);
                        InsertAt(group.Key.Notes, value.Index, value.Note, "Direct MIDI Note");
                    }
                }
                foreach (MidiSegmentOpaqueEventTransform value in opaque)
                    value.Event.Tick = value.OldTick;
                foreach (MidiSegmentDirectEventTransform value in events)
                    ApplyDirectEvent(value.Event, value.Old);
            });

    private static void ValidateMidiSegmentTransformWindows(
        IReadOnlyCollection<MidiSegmentWindowTransform> windows)
    {
        HashSet<MidiSegment> selected = windows.Select(value => value.Entry.Segment).ToHashSet();
        foreach (IGrouping<PureMidiTrack, MidiSegmentWindowTransform> group in windows
            .GroupBy(value => value.Entry.Track))
        {
            MidiSegmentWindowTransform[] ordered = group
                .OrderBy(value => value.Replacement.ProjectStartTick)
                .ThenBy(value => value.Entry.Segment.Id)
                .ToArray();
            for (int index = 0; index < ordered.Length; index++)
            {
                SegmentWindow value = ordered[index].Replacement;
                ValidateSegmentRange(value.ProjectStartTick, value.LengthTicks, value.ContentOffsetTick);
                TickRange range = new(
                    value.ProjectStartTick,
                    checked(value.ProjectStartTick + value.LengthTicks));
                if (group.Key.Segments.Any(segment =>
                    !selected.Contains(segment) && range.Intersects(segment.ProjectRange)))
                {
                    throw new InvalidOperationException(
                        "The transformation would overlap an existing MIDI Segment.");
                }
                if (index > 0)
                {
                    SegmentWindow previous = ordered[index - 1].Replacement;
                    if (checked(previous.ProjectStartTick + previous.LengthTicks) > value.ProjectStartTick)
                    {
                        throw new InvalidOperationException(
                            "The transformation would overlap selected MIDI Segments.");
                    }
                }
            }
        }
    }

    private static void SortTransformedMidiSegments(
        IReadOnlyCollection<MidiSegmentWindowTransform> windows)
    {
        foreach (PureMidiTrack track in windows.Select(value => value.Entry.Track).Distinct())
        {
            track.Segments.Sort(static (left, right) =>
            {
                int tick = left.ProjectStartTick.CompareTo(right.ProjectStartTick);
                return tick != 0 ? tick : left.Id.CompareTo(right.Id);
            });
        }
    }

    private static IProjectEditCommand TransformDirectMidiEventPoints(
        string name,
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> eventIds,
        Func<IReadOnlyList<DirectMidiEventValue>, DirectMidiEventValue[]> transform) =>
        Command(name, project =>
        {
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            DirectEventSelection[] selected = SelectDirectEvents(location.Segment, eventIds);
            EnsureSameDirectMidiEventLane(selected);
            DirectMidiEventValue[] old = selected.Select(value => value.Original).ToArray();
            DirectMidiEventValue[] replacement = transform(old);
            if (replacement.Length != old.Length)
                throw new InvalidOperationException("A Direct MIDI Event transform returned the wrong result count.");
            foreach (DirectMidiEventValue value in replacement)
                ValidateDirectMidiEvent(value.Tick, value.Kind, value.Data1, value.Data2);
            return Prepared(
                old.Where((value, index) => value != replacement[index]).Any(),
                PureMidiTrackChange(location.Track.Id),
                _ =>
                {
                    for (int index = 0; index < selected.Length; index++)
                        ApplyDirectEvent(selected[index].Event, replacement[index]);
                },
                _ =>
                {
                    for (int index = 0; index < selected.Length; index++)
                        ApplyDirectEvent(selected[index].Event, old[index]);
                });
        });

    private static void EnsureSameDirectMidiEventLane(IReadOnlyList<DirectEventSelection> selected)
    {
        DirectMidiChannelEvent first = selected[0].Event;
        if (selected.Skip(1).Any(value => !SameDirectMidiEventLane(first, value.Event)))
        {
            throw new ArgumentException(
                "Direct MIDI Event point transforms require one event lane.",
                nameof(selected));
        }
    }

    private static bool SameDirectMidiEventLane(
        DirectMidiChannelEvent left,
        DirectMidiChannelEvent right) =>
        left.Kind == right.Kind
        && (!DirectMidiEventUsesData1Selector(left.Kind) || left.Data1 == right.Data1);

    private static bool DirectMidiEventUsesData1Selector(DirectMidiChannelEventKind kind) =>
        kind is DirectMidiChannelEventKind.ControlChange
            or DirectMidiChannelEventKind.PolyphonicKeyPressure
            or DirectMidiChannelEventKind.NoteOn
            or DirectMidiChannelEventKind.NoteOff;

    private static int DirectMidiEventPointValue(DirectMidiEventValue value) => value.Kind switch
    {
        DirectMidiChannelEventKind.PitchBend => (value.Data2 << 7) | value.Data1,
        DirectMidiChannelEventKind.ProgramChange or DirectMidiChannelEventKind.ChannelPressure => value.Data1,
        _ => value.Data2
    };

    private static DirectMidiEventValue WithDirectMidiEventPointValue(
        DirectMidiEventValue value,
        int pointValue) => value.Kind switch
        {
            DirectMidiChannelEventKind.PitchBend => value with
            {
                Data1 = pointValue & 0x7f,
                Data2 = (pointValue >> 7) & 0x7f
            },
            DirectMidiChannelEventKind.ProgramChange or DirectMidiChannelEventKind.ChannelPressure =>
                value with { Data1 = pointValue },
            _ => value with { Data2 = pointValue }
        };

    private readonly record struct DirectNoteBatchResult(DirectNoteValue Value, bool Discard);
    private readonly record struct DirectEventBatchResult(DirectMidiEventValue Value, bool Discard);
    private enum MidiSegmentContentTransformKind
    {
        FlipHorizontal,
        FlipVertical,
        Scale,
        Transpose
    }
    private readonly record struct MidiSegmentDirectNoteSelection(
        MidiSegment Segment,
        DirectMidiNote Note,
        int Index,
        DirectNoteValue Old);
    private readonly record struct MidiSegmentWindowTransform(
        MidiSegmentSelection Entry,
        SegmentWindow Old,
        SegmentWindow Replacement);
    private readonly record struct MidiSegmentDirectNoteTransform(
        MidiSegment Segment,
        DirectMidiNote Note,
        int Index,
        DirectNoteValue Old,
        DirectNoteValue Replacement,
        bool Discard);
    private readonly record struct MidiSegmentDirectEventTransform(
        DirectMidiChannelEvent Event,
        DirectMidiEventValue Old,
        DirectMidiEventValue Replacement);
    private readonly record struct MidiSegmentOpaqueEventTransform(
        OpaqueMidiEvent Event,
        long OldTick,
        long ReplacementTick);
}
