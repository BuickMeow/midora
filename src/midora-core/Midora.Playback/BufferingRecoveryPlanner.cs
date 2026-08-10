using Midora.Compiler;
using Midora.Domain;

namespace Midora.Playback;

public readonly record struct BufferingRecoveryInterval(
    long FailureTick,
    long EndTick,
    ProjectBarInfo FailureBar,
    bool IncludesFollowingCompleteBar,
    bool WasQuarterNoteCapped,
    bool WasPlaybackEndClipped)
{
    public TickRange Range => new(FailureTick, EndTick);
}

public static class BufferingRecoveryPlanner
{
    public const int MaximumQuarterNoteCount = 16;

    public static BufferingRecoveryInterval Plan(
        CanonicalCompiledResult compiled,
        long failureTick,
        long playbackEndTick)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        if (!compiled.IsConsumable || compiled.IsPartial)
        {
            throw new ArgumentException(
                "Buffering recovery requires a consumable canonical compiled result.",
                nameof(compiled));
        }
        if (failureTick < compiled.StartTick
            || playbackEndTick > compiled.EndTick
            || playbackEndTick <= failureTick)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failureTick),
                "The failure tick and playback end must define a non-empty interval inside the canonical range.");
        }

        ProjectTimeSignatureMap map = new(
            compiled.TicksPerQuarterNote,
            compiled.Conductor.SourceTimeSignatureMap.ToArray().Select(value =>
                new ProjectTimeSignaturePoint(
                    value.SourceId,
                    value.Tick,
                    value.Numerator,
                    value.Denominator)));
        ProjectBarInfo failureBar = map.GetBarContaining(failureTick);
        bool includesFollowingBar = failureTick != failureBar.StartTick;
        long naturalEnd = failureBar.EndTick;
        if (includesFollowingBar && naturalEnd < playbackEndTick)
        {
            naturalEnd = map.GetBarContaining(naturalEnd).EndTick;
        }

        long capLength = checked((long)MaximumQuarterNoteCount * compiled.TicksPerQuarterNote);
        long capEnd = failureTick > long.MaxValue - capLength
            ? long.MaxValue
            : failureTick + capLength;
        long endTick = Math.Min(naturalEnd, Math.Min(capEnd, playbackEndTick));
        return new(
            failureTick,
            endTick,
            failureBar,
            includesFollowingBar,
            capEnd < naturalEnd && capEnd <= playbackEndTick,
            playbackEndTick < naturalEnd && playbackEndTick <= capEnd);
    }
}
