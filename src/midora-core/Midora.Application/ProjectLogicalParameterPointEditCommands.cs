using Midora.Domain;

namespace Midora.Application;

public sealed record LogicalParameterPointEdit(long Tick, double Value);

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand UpsertLogicalParameterPoints(
        MidoraId segmentId,
        MidoraId laneId,
        IReadOnlyCollection<LogicalParameterPointEdit> points) =>
        Command("Draw logical parameter points", project =>
        {
            ArgumentNullException.ThrowIfNull(points);
            if (points.Count == 0)
            {
                throw new ArgumentException(
                    "At least one Logical Parameter point is required.",
                    nameof(points));
            }

            SegmentLocation segment = FindSegment(project, segmentId);
            LogicalParameterLane lane = FindLogicalParameterLane(segment.Segment, laneId);
            LogicalParameterDefinition definition = FindBoundLogicalParameter(
                project,
                segment.Track,
                lane.ParameterId);
            LogicalParameterPointEdit[] edits = points
                .OrderBy(value => value.Tick)
                .ToArray();
            if (edits.Select(value => value.Tick).Distinct().Count() != edits.Length)
            {
                throw new ArgumentException(
                    "Logical Parameter point ticks must be unique.",
                    nameof(points));
            }

            List<ExistingLogicalParameterPointEdit> replacements = [];
            List<LogicalParameterPointEdit> additions = [];
            const CurveInterpolation additionInterpolation = CurveInterpolation.Step;
            foreach (LogicalParameterPointEdit edit in edits)
            {
                if (edit.Tick < 0 || edit.Tick == long.MaxValue || !double.IsFinite(edit.Value))
                {
                    throw new ArgumentOutOfRangeException(nameof(points));
                }
                ValidatePointValue(definition, edit.Value, additionInterpolation);
                CurvePoint? existing = lane.Points.SingleOrDefault(value => value.Tick == edit.Tick);
                if (existing is null)
                {
                    additions.Add(edit);
                }
                else
                {
                    ValidatePointValue(definition, edit.Value, CurveInterpolation.Step);
                    replacements.Add(new(existing, edit.Value));
                }
            }

            CurvePoint[]? created = null;
            CurvePoint[] replacementPoints = replacements
                .Select(value => new CurvePoint(
                    project,
                    value.Point.Id,
                    value.Point.Tick,
                    value.Value,
                    CurveInterpolation.Step))
                .ToArray();
            bool changesExisting = replacements
                .Select((value, index) => value.Point.Value != replacementPoints[index].Value)
                .Any(value => value);

            return Prepared(
                changesExisting || additions.Count != 0,
                TrackChange(segment.Track.Id),
                owner =>
                {
                    for (int index = 0; index < replacements.Count; index++)
                    {
                        ReplaceRequired(
                            lane.Points,
                            replacements[index].Point,
                            replacementPoints[index],
                            "Logical Parameter point");
                    }
                    created ??= additions
                        .Select(value => new CurvePoint(
                            owner,
                            value.Tick,
                            value.Value,
                            CurveInterpolation.Step))
                        .ToArray();
                    foreach (CurvePoint point in created)
                    {
                        InsertCurvePoint(lane.Points, point);
                    }
                },
                _ =>
                {
                    if (created is null)
                    {
                        throw new InvalidOperationException(
                            "Logical Parameter points do not exist before the first Apply.");
                    }
                    foreach (CurvePoint point in created)
                    {
                        RemoveRequired(lane.Points, point, "Logical Parameter point");
                    }
                    for (int index = 0; index < replacements.Count; index++)
                    {
                        ReplaceRequired(
                            lane.Points,
                            replacementPoints[index],
                            replacements[index].Point,
                            "Logical Parameter point");
                    }
                });
        });

    private sealed record ExistingLogicalParameterPointEdit(CurvePoint Point, double Value);
}
