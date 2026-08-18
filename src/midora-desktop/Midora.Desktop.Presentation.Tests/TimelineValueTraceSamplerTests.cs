using Midora.Desktop.Presentation.Interaction;
using Midora.Domain;

namespace Midora.Desktop.Presentation.Tests;

public sealed class TimelineValueTraceSamplerTests
{
    [Fact]
    public void DisabledSnapSamplesEveryTickRegardlessOfPixelDensity()
    {
        Dictionary<long, double> result = [];

        TimelineValueTraceSampler.SampleInto(
            [new(10, 0), new(18, 1)],
            result,
            fixedStepTicks: 1,
            useBars: false,
            timeSignatureMap: null);

        Assert.Equal(Enumerable.Range(10, 9).Select(static value => (long)value), result.Keys);
        Assert.Equal(0, result[10]);
        Assert.Equal(0.5, result[14], precision: 12);
        Assert.Equal(1, result[18]);
    }

    [Fact]
    public void EnabledSnapSamplesOnlyConfiguredGridTicks()
    {
        Dictionary<long, double> result = [];

        TimelineValueTraceSampler.SampleInto(
            [new(0, 0.2), new(16, 0.8)],
            result,
            fixedStepTicks: 4,
            useBars: false,
            timeSignatureMap: null);

        Assert.Equal(new long[] { 0, 4, 8, 12, 16 }, result.Keys);
    }

    [Fact]
    public void SnappedEndpointsRetainThePointerEndpointValues()
    {
        Dictionary<long, double> result = [];

        TimelineValueTraceSampler.SampleInto(
            [new(1, 0.2), new(15, 0.8)],
            result,
            fixedStepTicks: 4,
            useBars: false,
            timeSignatureMap: null);

        Assert.Equal(new long[] { 0, 4, 8, 12, 16 }, result.Keys);
        Assert.Equal(0.2, result[0], precision: 12);
        Assert.Equal(0.8, result[16], precision: 12);
    }

    [Fact]
    public void LaterFreehandSegmentsOverwriteEarlierValuesWithoutLeavingTickGaps()
    {
        Dictionary<long, double> result = [];

        TimelineValueTraceSampler.SampleInto(
            [new(0, 0), new(4, 1), new(2, 0.25)],
            result,
            fixedStepTicks: 1,
            useBars: false,
            timeSignatureMap: null);

        Assert.Equal(new long[] { 0, 1, 2, 3, 4 }, result.Keys.Order());
        Assert.Equal(0.25, result[2], precision: 12);
        Assert.Equal(0.625, result[3], precision: 12);
    }

    [Fact]
    public void HorizontalTraceKeepsTheStartingValueAcrossAllTicks()
    {
        Dictionary<long, double> result = [];

        TimelineValueTraceSampler.SampleInto(
            [new(3, 0.42), new(9, 0.42)],
            result,
            fixedStepTicks: 1,
            useBars: false,
            timeSignatureMap: null);

        Assert.Equal(7, result.Count);
        Assert.All(result.Values, value => Assert.Equal(0.42, value, precision: 12));
    }

    [Fact]
    public void ExclusiveEditRangeFiltersSamplesAtTheLaneBoundary()
    {
        Dictionary<long, double> result = [];

        TimelineValueTraceSampler.SampleInto(
            [new(0, 0), new(8, 1)],
            result,
            fixedStepTicks: 1,
            useBars: false,
            timeSignatureMap: null,
            rangeStartTick: 2,
            rangeEndTick: 5);

        Assert.Equal(new long[] { 2, 3, 4 }, result.Keys);
    }

    [Fact]
    public void BarSnapFollowsTimeSignatureChangesInsteadOfAConstantStep()
    {
        ProjectTimeSignatureMap map = new(
            192,
            [
                new ProjectTimeSignaturePoint(new(1), 0, 4, 4),
                new ProjectTimeSignaturePoint(new(2), 1536, 3, 4)
            ]);
        Dictionary<long, double> result = [];

        TimelineValueTraceSampler.SampleInto(
            [new(0, 0), new(2688, 1)],
            result,
            fixedStepTicks: 1,
            useBars: true,
            timeSignatureMap: map);

        Assert.Equal(new long[] { 0, 768, 1536, 2112, 2688 }, result.Keys);
    }
}
