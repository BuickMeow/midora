using Midora.Domain;

namespace Midora.Desktop.Presentation.Interaction;

public readonly record struct TimelineValueTracePoint(
    double Tick,
    double NormalizedValue);

public static class TimelineValueTraceSampler
{
    /// <summary>Stream samples in gesture order; the command reduces revisited ticks.</summary>
    public static IEnumerable<TimelineValueTracePoint> EnumerateSamples(
        IReadOnlyList<TimelineValueTracePoint> trace, long fixedStepTicks, bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap, long? rangeStartTick = null,
        long? rangeEndTick = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fixedStepTicks);
        if (rangeStartTick is < 0 || rangeEndTick is < 0 || rangeEndTick < rangeStartTick)
            throw new ArgumentOutOfRangeException(nameof(rangeStartTick));
        foreach (TimelineValueTracePoint point in trace) Validate(point);
        if (trace.Count == 0) yield break;
        cancellationToken.ThrowIfCancellationRequested();
        long firstTick = Quantize(trace[0].Tick);
        if (InRange(firstTick)) yield return new(firstTick, Math.Clamp(trace[0].NormalizedValue, 0, 1));
        for (int index = 1; index < trace.Count; index++)
        {
            TimelineValueTracePoint from = trace[index - 1], to = trace[index];
            long fromTick = Quantize(from.Tick), toTick = Quantize(to.Tick);
            long tick = fromTick;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double ratio = toTick == fromTick ? 1
                    : Math.Clamp((tick - (double)fromTick) / (toTick - (double)fromTick), 0, 1);
                if (InRange(tick)) yield return new(tick, Math.Clamp(from.NormalizedValue
                    + (to.NormalizedValue - from.NormalizedValue) * ratio, 0, 1));
                if (tick == toTick) break;
                long next;
                try
                {
                    next = toTick > fromTick
                        ? TimelineGridQuantization.GetNextGridTick(tick, fixedStepTicks, useBars, timeSignatureMap)
                        : TimelineGridQuantization.GetPreviousGridTick(tick, fixedStepTicks, useBars, timeSignatureMap);
                }
                catch (OverflowException) { break; }
                if (toTick > fromTick) { if (next <= tick) break; tick = Math.Min(next, toTick); }
                else { if (next >= tick) break; tick = Math.Max(next, toTick); }
            }
        }
        long Quantize(double tick) => TimelineGridQuantization.SnapAbsolute(
            checked((long)Math.Round(tick, MidpointRounding.AwayFromZero)),
            fixedStepTicks, useBars, timeSignatureMap, movementDirection: 0);
        bool InRange(long tick) => (!rangeStartTick.HasValue || tick >= rangeStartTick)
            && (!rangeEndTick.HasValue || tick < rangeEndTick);
    }

    public static void SampleInto(
        IReadOnlyList<TimelineValueTracePoint> trace,
        IDictionary<long, double> destination,
        long fixedStepTicks,
        bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap,
        long? rangeStartTick = null,
        long? rangeEndTick = null)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(destination);
        if (fixedStepTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fixedStepTicks));
        }
        if (rangeStartTick is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rangeStartTick));
        }
        if (rangeEndTick is < 0
            || rangeStartTick is long start
                && rangeEndTick is long end
                && end < start)
        {
            throw new ArgumentOutOfRangeException(nameof(rangeEndTick));
        }

        destination.Clear();
        if (trace.Count == 0) return;
        foreach (TimelineValueTracePoint point in trace)
        {
            Validate(point);
        }

        if (trace.Count == 1)
        {
            AddSample(Quantize(trace[0].Tick), trace[0].NormalizedValue);
            return;
        }

        for (int index = 1; index < trace.Count; index++)
        {
            TimelineValueTracePoint from = trace[index - 1];
            TimelineValueTracePoint to = trace[index];
            long fromTick = Quantize(from.Tick);
            long toTick = Quantize(to.Tick);
            if (fromTick == toTick)
            {
                AddSample(toTick, to.NormalizedValue);
                continue;
            }

            int direction = toTick > fromTick ? 1 : -1;
            long tick = fromTick;
            while (true)
            {
                double ratio = Math.Clamp(
                    (tick - (double)fromTick) / (toTick - (double)fromTick),
                    0,
                    1);
                AddSample(
                    tick,
                    from.NormalizedValue
                        + (to.NormalizedValue - from.NormalizedValue) * ratio);
                if (tick == toTick) break;

                long next;
                try
                {
                    next = direction > 0
                        ? TimelineGridQuantization.GetNextGridTick(
                            tick,
                            fixedStepTicks,
                            useBars,
                            timeSignatureMap)
                        : TimelineGridQuantization.GetPreviousGridTick(
                            tick,
                            fixedStepTicks,
                            useBars,
                            timeSignatureMap);
                }
                catch (OverflowException)
                {
                    break;
                }
                if (direction > 0)
                {
                    if (next <= tick) break;
                    tick = Math.Min(next, toTick);
                }
                else
                {
                    if (next >= tick) break;
                    tick = Math.Max(next, toTick);
                }
            }
        }

        return;

        long Quantize(double tick)
        {
            long rounded = checked((long)Math.Round(tick, MidpointRounding.AwayFromZero));
            return TimelineGridQuantization.SnapAbsolute(
                rounded,
                fixedStepTicks,
                useBars,
                timeSignatureMap,
                movementDirection: 0);
        }

        void AddSample(long tick, double value)
        {
            if (rangeStartTick is long rangeStart && tick < rangeStart) return;
            if (rangeEndTick is long rangeEnd && tick >= rangeEnd) return;
            destination[tick] = Math.Clamp(value, 0, 1);
        }
    }

    private static void Validate(TimelineValueTracePoint point)
    {
        if (!double.IsFinite(point.Tick)
            || point.Tick < 0
            || point.Tick > long.MaxValue
            || !double.IsFinite(point.NormalizedValue))
        {
            throw new ArgumentOutOfRangeException(nameof(point));
        }
    }
}
