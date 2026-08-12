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

        ProjectBarInfo bar = timeSignatureMap.GetBarContaining(startTick);
        long nextEligibleTick = startTick;
        while (bar.StartTick < endTick)
        {
            if (bar.StartTick >= nextEligibleTick)
            {
                destination.Add(new(bar.StartTick, TimelineGridLineKind.Bar));
                nextEligibleTick = SaturatingAdd(bar.StartTick, minimumTickSpacing);
            }

            for (int beat = 1; beat < bar.Numerator; beat++)
            {
                long beatTick = checked(bar.StartTick + (long)beat * bar.TicksPerBeat);
                if (beatTick >= bar.EndTick || beatTick >= endTick)
                {
                    break;
                }
                if (beatTick >= nextEligibleTick)
                {
                    destination.Add(new(beatTick, TimelineGridLineKind.Beat));
                    nextEligibleTick = SaturatingAdd(beatTick, minimumTickSpacing);
                }
            }

            if (bar.EndTick <= bar.StartTick || bar.EndTick >= endTick)
            {
                break;
            }
            long nextBarTick = bar.EndTick;
            if (nextBarTick < nextEligibleTick)
            {
                ProjectBarInfo containing = timeSignatureMap.GetBarContaining(nextEligibleTick);
                nextBarTick = containing.StartTick >= nextEligibleTick
                    ? containing.StartTick
                    : containing.EndTick;
            }
            if (nextBarTick >= endTick)
            {
                break;
            }
            bar = timeSignatureMap.GetBarContaining(nextBarTick);
        }
    }

    private static long SaturatingAdd(long value, long increment) =>
        value > long.MaxValue - increment ? long.MaxValue : value + increment;
}
