namespace Midora.Domain;

/// <summary>
/// Shared integer-grid arithmetic for timeline presentation and Project edits.
/// Fixed musical subdivisions are anchored at Project tick zero; only the Bar
/// mode follows the effective Time Signature map.
/// </summary>
public static class ProjectTimelineGrid
{
    public readonly record struct SnappedRange(long StartTick, long EndTick);

    public static long ResolveWholeNoteFractionStep(
        int ticksPerQuarterNote,
        int numerator,
        int denominator)
    {
        if (ticksPerQuarterNote < 1)
            throw new ArgumentOutOfRangeException(nameof(ticksPerQuarterNote));
        if (numerator < 1)
            throw new ArgumentOutOfRangeException(nameof(numerator));
        if (denominator < 1)
            throw new ArgumentOutOfRangeException(nameof(denominator));

        Int128 scaled = (Int128)4 * ticksPerQuarterNote * numerator;
        Int128 step = (scaled + denominator - 1) / denominator;
        if (step < 1 || step > long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(numerator));
        return (long)step;
    }

    public static long Snap(long tick, long gridStep, int tieDirection)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        ArgumentOutOfRangeException.ThrowIfLessThan(gridStep, 1);
        if (tieDirection is < -1 or > 1)
            throw new ArgumentOutOfRangeException(nameof(tieDirection));

        long lower = tick / gridStep * gridStep;
        if (lower == tick)
            return lower;

        // The mathematical upper grid point can lie above Int64. Compare in a
        // wider domain first, so an unselected upper candidate cannot fail a
        // valid lower result and a selected unrepresentable result still fails.
        Int128 upperWide = (Int128)lower + gridStep;
        Int128 lowerDistance = (Int128)tick - lower;
        Int128 upperDistance = upperWide - tick;
        if (lowerDistance < upperDistance)
            return lower;
        if (upperDistance < lowerDistance)
            return checked((long)upperWide);
        return tieDirection > 0 ? checked((long)upperWide) : lower;
    }

    /// <summary>
    /// Snaps a signed coordinate to the project-zero grid. This is used when
    /// Segment content outside its active crop window maps before Project tick
    /// zero; the content remains valid even though its projected tick is
    /// negative.
    /// </summary>
    public static long SnapSigned(long tick, long gridStep, int tieDirection)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(gridStep, 1);
        if (tieDirection is < -1 or > 1)
            throw new ArgumentOutOfRangeException(nameof(tieDirection));
        if (tick >= 0)
            return Snap(tick, gridStep, tieDirection);

        // Integer division truncates toward zero, so this is the grid point
        // immediately above a negative tick. Compare in Int128 so the lower
        // point may fall outside Int64 without overflowing an unselected path.
        Int128 upperWide = (Int128)(tick / gridStep) * gridStep;
        if (upperWide == tick)
            return tick;
        Int128 lowerWide = upperWide - gridStep;
        Int128 lowerDistance = (Int128)tick - lowerWide;
        Int128 upperDistance = upperWide - tick;
        if (lowerDistance < upperDistance)
            return checked((long)lowerWide);
        if (upperDistance < lowerDistance)
            return checked((long)upperWide);
        return tieDirection > 0
            ? checked((long)upperWide)
            : checked((long)lowerWide);
    }

    public static long SnapAbsolute(
        long tick,
        long fixedStepTicks,
        bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap,
        int tieDirection)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        if (!useBars || timeSignatureMap is null)
            return Snap(tick, Math.Max(1, fixedStepTicks), tieDirection);

        if (tieDirection is < -1 or > 1)
            throw new ArgumentOutOfRangeException(nameof(tieDirection));
        ProjectBarInfo bar = timeSignatureMap.GetBarContaining(tick);
        long lowerDistance = tick - bar.StartTick;
        long upperDistance = bar.EndTick - tick;
        if (lowerDistance < upperDistance)
            return bar.StartTick;
        if (upperDistance < lowerDistance)
            return bar.EndTick;
        return tieDirection > 0 ? bar.EndTick : bar.StartTick;
    }

    public static long SnapDelta(
        long delta,
        long targetTick,
        long fixedStepTicks,
        bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap)
    {
        if (delta == 0)
            return 0;

        long step = Math.Max(1, fixedStepTicks);
        if (useBars && timeSignatureMap is not null)
        {
            ProjectBarInfo bar = timeSignatureMap.GetBarContaining(Math.Max(0, targetTick));
            step = Math.Max(1, checked(bar.EndTick - bar.StartTick));
        }

        long magnitude = delta == long.MinValue ? long.MaxValue : Math.Abs(delta);
        long snapped = Snap(magnitude, step, Math.Sign(delta));
        return delta < 0 ? -snapped : snapped;
    }

    public static long GetGridTickAtOrAfter(
        long tick,
        long fixedStepTicks,
        bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        if (useBars && timeSignatureMap is not null)
        {
            ProjectBarInfo bar = timeSignatureMap.GetBarContaining(tick);
            return bar.StartTick == tick ? tick : bar.EndTick;
        }

        long step = Math.Max(1, fixedStepTicks);
        long lower = tick / step * step;
        return lower == tick ? tick : checked(lower + step);
    }

    public static long GetNextGridTick(
        long tick,
        long fixedStepTicks,
        bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        if (useBars && timeSignatureMap is not null)
        {
            ProjectBarInfo bar = timeSignatureMap.GetBarContaining(tick);
            if (bar.EndTick > tick)
                return bar.EndTick;

            return timeSignatureMap.GetBarContaining(checked(tick + 1)).EndTick;
        }

        return checked(tick + Math.Max(1, fixedStepTicks));
    }

    public static long GetPreviousGridTick(
        long tick,
        long fixedStepTicks,
        bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        if (tick == 0)
            return 0;
        if (useBars && timeSignatureMap is not null)
            return timeSignatureMap.GetBarContaining(tick - 1).StartTick;

        return Math.Max(0, tick - Math.Max(1, fixedStepTicks));
    }

    public static SnappedRange SnapRangeFromAnchor(
        long rawAnchorTick,
        long rawMovingTick,
        long fixedStepTicks,
        bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rawAnchorTick);
        ArgumentOutOfRangeException.ThrowIfNegative(rawMovingTick);
        bool movesRight = rawMovingTick >= rawAnchorTick;
        long anchor = SnapAbsolute(
            rawAnchorTick,
            fixedStepTicks,
            useBars,
            timeSignatureMap,
            0);
        long moving = SnapAbsolute(
            rawMovingTick,
            fixedStepTicks,
            useBars,
            timeSignatureMap,
            movesRight ? 1 : -1);

        if (movesRight)
        {
            long end = moving > anchor
                ? moving
                : GetNextGridTick(anchor, fixedStepTicks, useBars, timeSignatureMap);
            return new(anchor, end);
        }

        long start = moving < anchor
            ? moving
            : GetPreviousGridTick(anchor, fixedStepTicks, useBars, timeSignatureMap);
        if (start < anchor)
            return new(start, anchor);

        long fallbackEnd = GetNextGridTick(anchor, fixedStepTicks, useBars, timeSignatureMap);
        return new(anchor, fallbackEnd);
    }

    public static SnappedRange SnapPositiveRange(
        long rawStartTick,
        long rawEndTick,
        long fixedStepTicks,
        bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rawStartTick);
        if (rawEndTick <= rawStartTick)
            throw new ArgumentOutOfRangeException(nameof(rawEndTick));
        return SnapRangeFromAnchor(
            rawStartTick,
            rawEndTick,
            fixedStepTicks,
            useBars,
            timeSignatureMap);
    }
}
