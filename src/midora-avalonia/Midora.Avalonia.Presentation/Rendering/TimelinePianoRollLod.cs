namespace Midora.Avalonia.Presentation.Rendering;

/// <summary>
/// Level of detail policy for the piano roll, mirroring the reference implementation: notes are
/// merged per fixed tick block, and a block is only merged while it is at most
/// <see cref="MaxBlockPixels"/> wide on screen. Inside such a block the remaining gaps are
/// sub-pixel, so merging is visually equivalent to drawing the notes, while a merged segment can
/// never smear across a wide tick range the way note-count grouping does. The ladder is fixed in
/// ticks (16, 64, 256, 1024, ... with 4x steps) and has no upper bound, so no displayed content is
/// ever capped; the segment count follows the block width, not a budget.
/// </summary>
public static class TimelinePianoRollLod
{
    /// <summary>Maximum width in pixels of one merged block; above this the exact layer is used.</summary>
    public const double MaxBlockPixels = 4.0;

    /// <summary>Finest merged block, in ticks.</summary>
    public const int FinestBlockTicks = 16;

    /// <summary>
    /// Returns the tick block to merge by, or 0 when the exact note layer must be used because even
    /// the finest block would be wider than <see cref="MaxBlockPixels"/> (the view is zoomed in far
    /// enough that individual notes are distinguishable).
    /// </summary>
    public static int SelectBlockTicks(double pixelsPerTick)
    {
        if (!double.IsFinite(pixelsPerTick) || pixelsPerTick <= 0)
        {
            return 0;
        }

        int block = FinestBlockTicks;
        if (block * pixelsPerTick > MaxBlockPixels)
        {
            return 0;
        }

        while (block <= int.MaxValue / 4 && block * 4L * pixelsPerTick <= MaxBlockPixels)
        {
            block *= 4;
        }

        return block;
    }
}
