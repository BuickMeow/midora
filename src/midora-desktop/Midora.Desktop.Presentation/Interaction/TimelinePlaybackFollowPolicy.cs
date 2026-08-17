namespace Midora.Desktop.Presentation.Interaction;

public static class TimelinePlaybackFollowPolicy
{
    public static long? ResolveStartTick(
        bool followEnabled,
        bool playbackActive,
        bool viewportInteractionActive,
        long currentStartTick,
        long tickSpan,
        long? playbackCursorTick,
        bool force)
    {
        if (!followEnabled
            || !playbackActive
            || viewportInteractionActive
            || playbackCursorTick is not long cursorTick)
        {
            return null;
        }

        long startTick = Math.Max(0, currentStartTick);
        long span = Math.Max(1, tickSpan);
        long cursor = Math.Max(0, cursorTick);
        long followStart = SaturatingAdd(startTick, span / 10);
        long followEndOffset = span - span / 10 - (span % 10 == 0 ? 0 : 1);
        long followEnd = SaturatingAdd(startTick, followEndOffset);
        if (!force && cursor >= followStart && cursor <= followEnd)
        {
            return null;
        }

        long anchorOffset = span / 5;
        return cursor <= anchorOffset ? 0 : cursor - anchorOffset;
    }

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;
}
