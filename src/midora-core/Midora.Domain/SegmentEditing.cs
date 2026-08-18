namespace Midora.Domain;

public readonly record struct SegmentSplitResult(Segment Left, Segment Right);

public static class SegmentEditing
{
    public static Segment Duplicate(MidoraProject project, Segment source)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(source);
        Segment result = new(project)
        {
            ProjectStartTick = source.ProjectStartTick,
            LengthTicks = source.LengthTicks,
            ContentOffsetTick = source.ContentOffsetTick
        };
        HashSet<(long Tick, int Key)> noteStarts = [];
        foreach (LogicalNote note in source.Notes)
        {
            if (!noteStarts.Add((note.StartTick, note.Note)))
            {
                continue;
            }
            result.Notes.Add(CloneNote(project, note, note.LengthTicks, preserveId: false));
        }
        foreach (LogicalParameterLane lane in source.ParameterLanes)
        {
            LogicalParameterLane copy = new(project) { ParameterId = lane.ParameterId };
            HashSet<long> pointTicks = [];
            foreach (CurvePoint point in lane.Points)
            {
                if (!pointTicks.Add(point.Tick))
                {
                    continue;
                }
                copy.Points.Add(new(project, point.Tick, point.Value, point.Interpolation));
            }
            result.ParameterLanes.Add(copy);
        }
        return result;
    }

    public static void Move(Segment segment, long newProjectStartTick)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (newProjectStartTick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(newProjectStartTick));
        }
        _ = checked(newProjectStartTick + segment.LengthTicks);
        segment.ProjectStartTick = newProjectStartTick;
    }

    public static Segment Join(MidoraProject project, Segment first, Segment second)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (first.Id == second.Id)
        {
            throw new ArgumentException("A Segment cannot be joined with itself.", nameof(second));
        }
        Segment left = first.ProjectStartTick <= second.ProjectStartTick ? first : second;
        Segment right = ReferenceEquals(left, first) ? second : first;
        if (left.ProjectRange.EndTick > right.ProjectStartTick)
        {
            throw new ArgumentException("Only non-overlapping Segments can be joined.", nameof(second));
        }

        long leftContentOrigin = checked(left.ProjectStartTick - left.ContentOffsetTick);
        long rightContentOrigin = checked(right.ProjectStartTick - right.ContentOffsetTick);
        long joinedContentOrigin = Math.Min(leftContentOrigin, rightContentOrigin);
        long joinedStart = left.ProjectStartTick;
        long joinedEnd = right.ProjectRange.EndTick;
        Segment result = new(project, left.Id)
        {
            ProjectStartTick = joinedStart,
            LengthTicks = checked(joinedEnd - joinedStart),
            ContentOffsetTick = checked(joinedStart - joinedContentOrigin)
        };

        HashSet<(long Tick, int Key)> noteStarts = [];
        AppendNotes(left, leftContentOrigin);
        AppendNotes(right, rightContentOrigin);
        Dictionary<MidoraId, (MidoraId LaneId, Dictionary<long, CurvePoint> Points)> lanes = [];
        AppendLanes(left, leftContentOrigin, preferIncomingAtSameTick: false);
        AppendLanes(right, rightContentOrigin, preferIncomingAtSameTick: false);
        foreach ((MidoraId parameterId, (MidoraId laneId, Dictionary<long, CurvePoint> points)) in lanes
            .OrderBy(value => value.Key))
        {
            LogicalParameterLane lane = new(project, laneId) { ParameterId = parameterId };
            lane.Points.AddRange(points.OrderBy(value => value.Key).Select(value => value.Value));
            result.ParameterLanes.Add(lane);
        }
        return result;

        void AppendNotes(Segment source, long sourceOrigin)
        {
            foreach (LogicalNote note in source.Notes)
            {
                long absoluteTick = checked(sourceOrigin + note.StartTick);
                long joinedTick = checked(absoluteTick - joinedContentOrigin);
                if (!noteStarts.Add((joinedTick, note.Note)))
                {
                    continue;
                }
                LogicalNote copy = CloneNote(project, note, note.LengthTicks);
                copy.StartTick = joinedTick;
                result.Notes.Add(copy);
            }
        }

        void AppendLanes(Segment source, long sourceOrigin, bool preferIncomingAtSameTick)
        {
            foreach (LogicalParameterLane sourceLane in source.ParameterLanes)
            {
                if (!lanes.TryGetValue(sourceLane.ParameterId, out var target))
                {
                    target = (sourceLane.Id, []);
                    lanes.Add(sourceLane.ParameterId, target);
                }
                foreach (CurvePoint point in sourceLane.Points)
                {
                    long absoluteTick = checked(sourceOrigin + point.Tick);
                    long joinedTick = checked(absoluteTick - joinedContentOrigin);
                    CurvePoint moved = point with { Tick = joinedTick };
                    if (preferIncomingAtSameTick || !target.Points.ContainsKey(joinedTick))
                    {
                        target.Points[joinedTick] = moved;
                    }
                }
            }
        }
    }

    public static SegmentSplitResult Split(MidoraProject project, Segment source, long projectSplitTick)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(source);
        if (projectSplitTick <= source.ProjectStartTick || projectSplitTick >= source.ProjectRange.EndTick)
        {
            throw new ArgumentOutOfRangeException(nameof(projectSplitTick));
        }
        long leftLength = projectSplitTick - source.ProjectStartTick;
        long splitContentTick = checked(source.ContentOffsetTick + leftLength);
        Segment left = new(project, source.Id)
        {
            ProjectStartTick = source.ProjectStartTick,
            LengthTicks = leftLength,
            ContentOffsetTick = source.ContentOffsetTick
        };
        Segment right = new(project)
        {
            ProjectStartTick = projectSplitTick,
            LengthTicks = source.LengthTicks - leftLength,
            ContentOffsetTick = splitContentTick
        };
        foreach (LogicalNote note in source.Notes)
        {
            if (note.StartTick < splitContentTick)
            {
                long available = splitContentTick - note.StartTick;
                left.Notes.Add(CloneNote(project, note, Math.Min(note.LengthTicks, available)));
            }
            else
            {
                right.Notes.Add(CloneNote(project, note, note.LengthTicks));
            }
        }
        foreach (LogicalParameterLane lane in source.ParameterLanes)
        {
            LogicalParameterLane leftLane = new(project, lane.Id) { ParameterId = lane.ParameterId };
            LogicalParameterLane rightLane = new(project) { ParameterId = lane.ParameterId };
            CurvePoint[] points = lane.Points.OrderBy(value => value.Tick).ToArray();
            foreach (CurvePoint point in points)
            {
                if (point.Tick < splitContentTick)
                {
                    leftLane.Points.Add(point);
                }
                else
                {
                    rightLane.Points.Add(point);
                }
            }
            if (points.Any(value => value.Tick < splitContentTick)
                && rightLane.Points.All(value => value.Tick != splitContentTick))
            {
                rightLane.Points.Insert(0, new CurvePoint(
                    project,
                    splitContentTick,
                    Evaluate(points, splitContentTick),
                    CurveInterpolation.Step));
            }
            left.ParameterLanes.Add(leftLane);
            right.ParameterLanes.Add(rightLane);
        }
        return new(left, right);
    }

    private static LogicalNote CloneNote(
        MidoraProject project,
        LogicalNote source,
        long length,
        bool preserveId = true)
    {
        LogicalNote result = preserveId
            ? new LogicalNote(project, source.Id)
            : new LogicalNote(project);
        result.StartTick = source.StartTick;
        result.LengthTicks = length;
        result.Note = source.Note;
        result.Velocity = source.Velocity;
        return result;
    }

    private static double Evaluate(ReadOnlySpan<CurvePoint> points, long tick)
    {
        if (points.IsEmpty)
        {
            throw new InvalidOperationException("Cannot split an empty parameter lane.");
        }
        if (tick <= points[0].Tick)
        {
            return points[0].Value;
        }
        for (int i = 0; i < points.Length - 1; i++)
        {
            CurvePoint left = points[i];
            CurvePoint right = points[i + 1];
            if (tick <= right.Tick)
            {
                if (left.Interpolation == CurveInterpolation.Step)
                {
                    return left.Value;
                }
                double ratio = (tick - left.Tick) / (double)(right.Tick - left.Tick);
                return left.Value + ((right.Value - left.Value) * ratio);
            }
        }
        return points[^1].Value;
    }
}
