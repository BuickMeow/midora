using Midora.Desktop.Presentation.Interaction;

namespace Midora.Desktop.Presentation.Tests;

public sealed class TimelinePlaybackFollowPolicyTests
{
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void InactiveConditionsDoNotChangeTheViewport(
        bool followEnabled,
        bool playbackActive,
        bool viewportInteractionActive)
    {
        long? result = TimelinePlaybackFollowPolicy.ResolveStartTick(
            followEnabled,
            playbackActive,
            viewportInteractionActive,
            currentStartTick: 1_000,
            tickSpan: 1_000,
            playbackCursorTick: 1_950,
            force: true);

        Assert.Null(result);
    }

    [Fact]
    public void MissingPlaybackCursorDoesNotChangeTheViewport()
    {
        long? result = TimelinePlaybackFollowPolicy.ResolveStartTick(
            followEnabled: true,
            playbackActive: true,
            viewportInteractionActive: false,
            currentStartTick: 1_000,
            tickSpan: 1_000,
            playbackCursorTick: null,
            force: true);

        Assert.Null(result);
    }

    [Fact]
    public void VisibleCursorInsideFollowBandDoesNotMoveDuringNormalFollow()
    {
        long? result = TimelinePlaybackFollowPolicy.ResolveStartTick(
            followEnabled: true,
            playbackActive: true,
            viewportInteractionActive: false,
            currentStartTick: 1_000,
            tickSpan: 1_000,
            playbackCursorTick: 1_500,
            force: false);

        Assert.Null(result);
    }

    [Fact]
    public void CursorOutsideFollowBandMovesToTwentyPercentAnchor()
    {
        long? result = TimelinePlaybackFollowPolicy.ResolveStartTick(
            followEnabled: true,
            playbackActive: true,
            viewportInteractionActive: false,
            currentStartTick: 1_000,
            tickSpan: 1_000,
            playbackCursorTick: 1_950,
            force: false);

        Assert.Equal(1_750, result);
    }

    [Fact]
    public void ForcedFollowReturnsToCursorEvenWhenItIsAlreadyVisible()
    {
        long? result = TimelinePlaybackFollowPolicy.ResolveStartTick(
            followEnabled: true,
            playbackActive: true,
            viewportInteractionActive: false,
            currentStartTick: 1_000,
            tickSpan: 1_000,
            playbackCursorTick: 1_500,
            force: true);

        Assert.Equal(1_300, result);
    }

    [Fact]
    public void ForcedFollowClampsViewportAtTimelineStart()
    {
        long? result = TimelinePlaybackFollowPolicy.ResolveStartTick(
            followEnabled: true,
            playbackActive: true,
            viewportInteractionActive: false,
            currentStartTick: 500,
            tickSpan: 1_000,
            playbackCursorTick: 100,
            force: true);

        Assert.Equal(0, result);
    }
}
