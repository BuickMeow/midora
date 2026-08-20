using Midora.Domain;

namespace Midora.Desktop.Presentation.Rendering;

public enum TimelineGridLineKind
{
    Bar,
    Beat
}

public readonly record struct TimelineGridLine(long Tick, TimelineGridLineKind Kind);

public static class TimelineGridPresentation
{
    public static void BuildArrangementBarGridLines(
        long startTick,
        long endTick,
        ProjectTimeSignatureMap timeSignatureMap,
        List<TimelineGridLine> destination,
        long minimumTickSpacing = 1)
        => BuildBarGridLines(
            startTick,
            endTick,
            timeSignatureMap,
            destination,
            minimumTickSpacing,
            projectTickOffset: 0);

    public static void BuildBarGridLines(
        long startTick,
        long endTick,
        ProjectTimeSignatureMap timeSignatureMap,
        List<TimelineGridLine> destination,
        long minimumTickSpacing = 1,
        long projectTickOffset = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startTick);
        ArgumentNullException.ThrowIfNull(timeSignatureMap);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumTickSpacing, 1);
        destination.Clear();
        if (endTick <= startTick)
        {
            return;
        }

        long localStart = startTick;
        if (projectTickOffset < 0)
        {
            long firstNonNegativeProjectTick = projectTickOffset == long.MinValue
                ? long.MaxValue
                : -projectTickOffset;
            localStart = Math.Max(localStart, firstNonNegativeProjectTick);
        }
        if (localStart >= endTick)
        {
            return;
        }
        long projectStart = SaturatingAddSigned(localStart, projectTickOffset);
        long projectEnd = SaturatingAddSigned(endTick, projectTickOffset);
        if (projectEnd <= projectStart)
        {
            return;
        }

        ProjectBarInfo bar = timeSignatureMap.GetBarContaining(projectStart);
        long nextEligibleTick = localStart;
        while (bar.StartTick < projectEnd)
        {
            long localBarTick = checked(bar.StartTick - projectTickOffset);
            if (localBarTick >= nextEligibleTick)
            {
                destination.Add(new(localBarTick, TimelineGridLineKind.Bar));
                nextEligibleTick = SaturatingAdd(localBarTick, minimumTickSpacing);
            }

            for (int beat = 1; beat < bar.Numerator; beat++)
            {
                long projectBeatTick = checked(bar.StartTick + (long)beat * bar.TicksPerBeat);
                if (projectBeatTick >= bar.EndTick || projectBeatTick >= projectEnd)
                {
                    break;
                }
                long localBeatTick = checked(projectBeatTick - projectTickOffset);
                if (localBeatTick >= nextEligibleTick)
                {
                    destination.Add(new(localBeatTick, TimelineGridLineKind.Beat));
                    nextEligibleTick = SaturatingAdd(localBeatTick, minimumTickSpacing);
                }
            }

            if (bar.EndTick <= bar.StartTick || bar.EndTick >= projectEnd)
            {
                break;
            }
            long nextBarTick = bar.EndTick;
            long nextEligibleProjectTick = SaturatingAddSigned(
                nextEligibleTick,
                projectTickOffset);
            if (nextBarTick < nextEligibleProjectTick)
            {
                ProjectBarInfo containing = timeSignatureMap.GetBarContaining(
                    nextEligibleProjectTick);
                nextBarTick = containing.StartTick >= nextEligibleProjectTick
                    ? containing.StartTick
                    : containing.EndTick;
            }
            if (nextBarTick >= projectEnd)
            {
                break;
            }
            bar = timeSignatureMap.GetBarContaining(nextBarTick);
        }
    }

    private static long SaturatingAdd(long value, long increment) =>
        value > long.MaxValue - increment ? long.MaxValue : value + increment;

    private static long SaturatingAddSigned(long value, long increment)
    {
        if (increment > 0 && value > long.MaxValue - increment) return long.MaxValue;
        if (increment < 0 && value < long.MinValue - increment) return long.MinValue;
        return value + increment;
    }
}
