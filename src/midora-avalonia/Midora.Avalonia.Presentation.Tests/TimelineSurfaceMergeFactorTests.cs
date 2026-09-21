using Midora.Avalonia.Presentation.Rendering;

namespace Midora.Avalonia.Presentation.Tests;

/// <summary>
/// The piano roll level of detail merges notes per fixed tick block and only while a block stays a
/// few pixels wide on screen, so a merged segment can never smear across a wide tick range. The
/// ladder is fixed in ticks with 4x steps and has no upper bound, so no content is capped.
/// </summary>
public sealed class TimelinePianoRollLodTests
{
    [Theory]
    [InlineData(1.0, 0)]
    [InlineData(0.3, 0)]
    [InlineData(0.25, 16)]
    [InlineData(0.1, 16)]
    [InlineData(0.05, 64)]
    [InlineData(0.01, 256)]
    [InlineData(0.001, 1024)]
    [InlineData(0.0001, 16384)]
    public void SelectsTheLargestBlockWithinTheMaximumWidth(double pixelsPerTick, int expected)
    {
        Assert.Equal(expected, TimelinePianoRollLod.SelectBlockTicks(pixelsPerTick));
    }

    [Fact]
    public void DegenerateZoomNeverMerges()
    {
        Assert.Equal(0, TimelinePianoRollLod.SelectBlockTicks(double.NaN));
        Assert.Equal(0, TimelinePianoRollLod.SelectBlockTicks(double.PositiveInfinity));
        Assert.Equal(0, TimelinePianoRollLod.SelectBlockTicks(0));
        Assert.Equal(0, TimelinePianoRollLod.SelectBlockTicks(-1));
    }

    [Fact]
    public void MergedBlocksStayWithinTheMaximumWidthAndGrowWhenZoomingOut()
    {
        int previous = 0;
        for (double pixelsPerTick = 0.5; pixelsPerTick > 1e-9; pixelsPerTick /= 1.6)
        {
            int block = TimelinePianoRollLod.SelectBlockTicks(pixelsPerTick);
            if (block == 0)
            {
                continue;
            }

            Assert.True(
                block * pixelsPerTick <= TimelinePianoRollLod.MaxBlockPixels + 1e-9,
                $"block {block} at {pixelsPerTick} px/tick exceeds the maximum width");
            if (block > TimelinePianoRollLod.FinestBlockTicks)
            {
                Assert.True(
                    block * pixelsPerTick > TimelinePianoRollLod.MaxBlockPixels / 4.0,
                    $"block {block} at {pixelsPerTick} px/tick is coarser than a quarter maximum");
            }

            Assert.True(block >= previous, $"block dropped from {previous} to {block}");
            previous = block;
        }

        Assert.True(previous > 1024);
    }
}
