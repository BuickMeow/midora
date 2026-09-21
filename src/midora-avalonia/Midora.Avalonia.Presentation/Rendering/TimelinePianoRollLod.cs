namespace Midora.Avalonia.Presentation.Rendering;

/// <summary>
/// Level of detail policy for the piano roll. It is a pure function of the average note width so it
/// can be tested and reasoned about without a control, and it never caps displayed content: every
/// level merges consecutive notes into one conservative envelope, so no note is dropped.
/// </summary>
public static class TimelinePianoRollLod
{
    /// <summary>Target width in pixels of one drawn unit.</summary>
    public const double TargetPixels = 16.0;

    /// <summary>First merged level: 16 consecutive notes share one envelope.</summary>
    public const int FirstMergeLevel = 16;

    /// <summary>
    /// Largest ladder value (1, 16, 32, 64, 128, ... with 2x steps) whose merged unit stays within
    /// <see cref="TargetPixels"/>. Notes at least a pixel wide are drawn exactly, merging starts at
    /// 16 items per envelope, each merged unit stays between half and the full target width, and
    /// there is no upper level: fully zoomed out content merges into a few large blocks.
    /// </summary>
    public static int SelectMergeFactor(double averageNoteWidthPixels)
    {
        if (!double.IsFinite(averageNoteWidthPixels) || averageNoteWidthPixels <= 0)
        {
            return 1;
        }

        int factor = 1;
        if (averageNoteWidthPixels * FirstMergeLevel <= TargetPixels)
        {
            factor = FirstMergeLevel;
            while (factor <= int.MaxValue / 2
                && averageNoteWidthPixels * (factor * 2L) <= TargetPixels)
            {
                factor *= 2;
            }
        }

        return factor;
    }
}
