using Midora.Domain;

namespace Midora.Compiler.Tests;

public sealed class ProjectTimeSignatureMapTests
{
    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(1, 2, true)]
    [InlineData(1, 4, true)]
    [InlineData(1, 8, false)]
    [InlineData(192, 64, true)]
    [InlineData(480, 64, true)]
    [InlineData(32_767, 4, true)]
    [InlineData(32_767, 8, false)]
    public void CompatibilityRequiresIntegralDenominatorBeat(
        int ticksPerQuarterNote,
        int denominator,
        bool expected)
    {
        Assert.Equal(expected,
            ProjectTimeSignatureRules.IsCompatible(ticksPerQuarterNote, denominator));
        if (expected)
        {
            Assert.True(ProjectTimeSignatureRules.GetTicksPerBeat(
                ticksPerQuarterNote,
                denominator) > 0);
        }
        else
        {
            Assert.Throws<ArgumentException>(() =>
                ProjectTimeSignatureRules.GetTicksPerBeat(ticksPerQuarterNote, denominator));
        }
    }

    [Fact]
    public void MusicalPositionRoundTripsAcrossNaturalAndTruncatedBars()
    {
        MidoraProject project = new(480);
        project.Conductor.TimeSignatures.Add(new(project, 1_000, 3, 4));
        project.Conductor.TimeSignatures.Add(new(project, 2_440, 5, 8));
        ProjectTimeSignatureMap map = new(project);

        Assert.Equal(new ProjectMusicalPosition(1, 1, 0), map.GetPosition(0));
        Assert.Equal(new ProjectMusicalPosition(1, 3, 39), map.GetPosition(999));
        Assert.Equal(new ProjectMusicalPosition(2, 1, 0), map.GetPosition(1_000));
        Assert.Equal(new ProjectMusicalPosition(2, 3, 479), map.GetPosition(2_439));
        Assert.Equal(new ProjectMusicalPosition(3, 1, 0), map.GetPosition(2_440));

        foreach (long tick in new long[] { 0, 479, 999, 1_000, 2_439, 2_440, 10_000 })
        {
            Assert.Equal(tick, map.GetTick(map.GetPosition(tick)));
        }
        for (long tick = 0; tick <= 10_000; tick++)
        {
            Assert.Equal(tick, map.GetTick(map.GetPosition(tick)));
        }

        ProjectBarInfo truncated = map.GetBarContaining(999);
        Assert.Equal((1UL, 0L, 1_000L, true),
            (truncated.Bar, truncated.StartTick, truncated.EndTick,
                truncated.IsTruncatedByTimeSignatureChange));
        ProjectBarInfo complete = map.GetBarContaining(2_000);
        Assert.Equal((2UL, 1_000L, 2_440L, false),
            (complete.Bar, complete.StartTick, complete.EndTick,
                complete.IsTruncatedByTimeSignatureChange));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            map.GetTick(new ProjectMusicalPosition(1, 4, 0)));
    }

    [Fact]
    public void MusicalPositionFormatIsFixedAndBarCanExceedSignedLong()
    {
        Assert.Equal("12:3:45", new ProjectMusicalPosition(12, 3, 45).ToString());
        Assert.Equal(new ProjectMusicalPosition(12, 3, 45),
            ProjectMusicalPosition.Parse("12:3:45"));
        Assert.False(ProjectMusicalPosition.TryParse("0:1:0", out _));
        Assert.False(ProjectMusicalPosition.TryParse("1:0:0", out _));
        Assert.False(ProjectMusicalPosition.TryParse("1:1:-1", out _));

        MidoraProject project = new(1);
        project.Conductor.TimeSignatures[0] = project.Conductor.TimeSignatures[0] with
        {
            Numerator = 1,
            Denominator = 4
        };
        ProjectTimeSignatureMap map = new(project);
        ProjectMusicalPosition maximum = map.GetPosition(long.MaxValue);

        Assert.Equal((ulong)long.MaxValue + 1, maximum.Bar);
        Assert.Equal(long.MaxValue, map.GetTick(maximum));
    }

    [Fact]
    public void BeatGridResetsAtTimeSignatureChangeAndTieSnapsForward()
    {
        MidoraProject project = new(480);
        project.Conductor.TimeSignatures.Add(new(project, 1_000, 3, 4));
        ProjectTimeSignatureMap map = new(project);

        Assert.Equal(960, map.GetBeatGridTickAtOrBefore(980));
        Assert.Equal(1_000, map.GetBeatGridTickAtOrAfter(980));
        Assert.Equal(1_000, map.SnapToNearestBeatGrid(980));
        Assert.Equal(1_000, map.GetBeatGridTickAtOrBefore(1_001));
        Assert.Equal(1_480, map.GetBeatGridTickAtOrAfter(1_001));
        Assert.Equal(1_000, map.SnapToNearestBeatGrid(1_001));
    }
}
