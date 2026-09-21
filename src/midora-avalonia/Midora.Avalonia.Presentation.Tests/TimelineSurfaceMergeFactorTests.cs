using Midora.Avalonia.Presentation.Rendering;

namespace Midora.Avalonia.Presentation.Tests;

/// <summary>
/// The piano roll level of detail must not blur notes that are still individually visible: merging
/// starts at 16 items per envelope and each step is 2x, so a merged unit never exceeds the target
/// width and the ladder has no upper bound.
/// </summary>
public sealed class TimelineSurfaceMergeFactorTests
{
    [Theory]
    [InlineData(64.0, 1)]
    [InlineData(4.0, 1)]
    [InlineData(2.0, 1)]
    [InlineData(1.01, 1)]
    [InlineData(1.0, 16)]
    [InlineData(0.5, 32)]
    [InlineData(0.25, 64)]
    [InlineData(0.1, 128)]
    [InlineData(0.0625, 256)]
    [InlineData(0.01, 1024)]
    [InlineData(0.001, 8192)]
    public void SelectsTheLargestLevelWithinTheTargetWidth(double averageNoteWidth, int expected)
    {
        Assert.Equal(expected, TimelinePianoRollLod.SelectMergeFactor(averageNoteWidth));
    }

    [Fact]
    public void MergedUnitsStayWithinTheTargetWidth()
    {
        for (double width = 1.0; width > 1e-6; width /= 1.7)
        {
            int factor = TimelinePianoRollLod.SelectMergeFactor(width);
            if (factor == 1)
            {
                Assert.True(width > 1.0 - 1e-12);
                continue;
            }

            Assert.True(
                width * factor <= TimelinePianoRollLod.TargetPixels + 1e-9,
                $"width {width} factor {factor} exceeds the target");
            if (factor <= int.MaxValue / 2)
            {
                Assert.True(
                    width * factor > TimelinePianoRollLod.TargetPixels / 2.0,
                    $"width {width} factor {factor} is coarser than half a target");
            }
        }
    }

    [Fact]
    public void LevelNeverDecreasesWhenZoomingOut()
    {
        int previous = 1;
        for (double width = 8.0; width > 1e-9; width /= 1.5)
        {
            int factor = TimelinePianoRollLod.SelectMergeFactor(width);
            Assert.True(factor >= previous, $"factor dropped from {previous} to {factor}");
            previous = factor;
        }
    }

    [Fact]
    public void DegenerateWidthsDoNotMerge()
    {
        Assert.Equal(1, TimelinePianoRollLod.SelectMergeFactor(double.NaN));
        Assert.Equal(1, TimelinePianoRollLod.SelectMergeFactor(double.PositiveInfinity));
        Assert.Equal(1, TimelinePianoRollLod.SelectMergeFactor(0));
        Assert.Equal(1, TimelinePianoRollLod.SelectMergeFactor(-1));
    }

    [Fact]
    public void ExtremeZoomOutKeepsGrowingWithoutOverflow()
    {
        int factor = TimelinePianoRollLod.SelectMergeFactor(1e-12);
        Assert.True(factor > 1_000_000);
        Assert.True(factor <= int.MaxValue);
    }
}
