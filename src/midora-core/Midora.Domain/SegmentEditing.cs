namespace Midora.Domain;

public readonly record struct SegmentSplitResult(Segment Left, Segment Right);

public static class SegmentEditing
{
    public static Segment Duplicate(Segment source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Segment result = new()
        {
            ProjectStartTick = source.ProjectStartTick,
            LengthTicks = source.LengthTicks,
            ContentOffsetTick = source.ContentOffsetTick
        };
        foreach (LogicalNote note in source.Notes)
        {
            result.Notes.Add(CloneNote(note, note.LengthTicks, preserveId: false));
        }
        foreach (LogicalParameterLane lane in source.ParameterLanes)
        {
            LogicalParameterLane copy = new() { ParameterId = lane.ParameterId };
            foreach (CurvePoint point in lane.Points)
            {
                copy.Points.Add(new(point.Tick, point.Value, point.Interpolation));
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

    public static Segment Join(Segment first, Segment second)
    {
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
        Segment result = new()
        {
            Id = left.Id,
            ProjectStartTick = joinedStart,
            LengthTicks = checked(joinedEnd - joinedStart),
            ContentOffsetTick = checked(joinedStart - joinedContentOrigin)
        };

        AppendNotes(left, leftContentOrigin);
        AppendNotes(right, rightContentOrigin);
        Dictionary<MidoraId, (MidoraId LaneId, Dictionary<long, CurvePoint> Points)> lanes = [];
        AppendLanes(left, leftContentOrigin, preferIncomingAtSameTick: false);
        AppendLanes(right, rightContentOrigin, preferIncomingAtSameTick: true);
        foreach ((MidoraId parameterId, (MidoraId laneId, Dictionary<long, CurvePoint> points)) in lanes
            .OrderBy(value => value.Key))
        {
            LogicalParameterLane lane = new() { Id = laneId, ParameterId = parameterId };
            lane.Points.AddRange(points.OrderBy(value => value.Key).Select(value => value.Value));
            result.ParameterLanes.Add(lane);
        }
        return result;

        void AppendNotes(Segment source, long sourceOrigin)
        {
            foreach (LogicalNote note in source.Notes)
            {
                long absoluteTick = checked(sourceOrigin + note.StartTick);
                LogicalNote copy = CloneNote(note, note.LengthTicks);
                copy.StartTick = checked(absoluteTick - joinedContentOrigin);
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

    public static SegmentSplitResult Split(Segment source, long projectSplitTick)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (projectSplitTick <= source.ProjectStartTick || projectSplitTick >= source.ProjectRange.EndTick)
        {
            throw new ArgumentOutOfRangeException(nameof(projectSplitTick));
        }
        long leftLength = projectSplitTick - source.ProjectStartTick;
        long splitContentTick = checked(source.ContentOffsetTick + leftLength);
        Segment left = new()
        {
            Id = source.Id,
            ProjectStartTick = source.ProjectStartTick,
            LengthTicks = leftLength,
            ContentOffsetTick = source.ContentOffsetTick
        };
        Segment right = new()
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
                left.Notes.Add(CloneNote(note, Math.Min(note.LengthTicks, available)));
            }
            else
            {
                right.Notes.Add(CloneNote(note, note.LengthTicks));
            }
        }
        foreach (LogicalParameterLane lane in source.ParameterLanes)
        {
            LogicalParameterLane leftLane = new() { Id = lane.Id, ParameterId = lane.ParameterId };
            LogicalParameterLane rightLane = new() { ParameterId = lane.ParameterId };
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
                    splitContentTick,
                    Evaluate(points, splitContentTick),
                    CurveInterpolation.Step));
            }
            left.ParameterLanes.Add(leftLane);
            right.ParameterLanes.Add(rightLane);
        }
        return new(left, right);
    }

    private static LogicalNote CloneNote(LogicalNote source, long length, bool preserveId = true) => new()
    {
        Id = preserveId ? source.Id : MidoraId.New(),
        StartTick = source.StartTick,
        LengthTicks = length,
        Note = source.Note,
        Velocity = source.Velocity
    };

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
