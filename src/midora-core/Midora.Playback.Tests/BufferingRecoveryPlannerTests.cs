using Midora.Compiler;
using Midora.Domain;

namespace Midora.Playback.Tests;

public sealed class BufferingRecoveryPlannerTests
{
    [Fact]
    public void FailureAtBarStartWaitsForCurrentCompleteNaturalBar()
    {
        CanonicalCompiledResult compiled = Compile(new MidoraProject(192), 10_000);

        BufferingRecoveryInterval interval = BufferingRecoveryPlanner.Plan(
            compiled,
            failureTick: 0,
            playbackEndTick: 10_000);

        Assert.Equal(new TickRange(0, 768), interval.Range);
        Assert.False(interval.IncludesFollowingCompleteBar);
        Assert.False(interval.WasQuarterNoteCapped);
        Assert.False(interval.WasPlaybackEndClipped);
    }

    [Fact]
    public void FailureInsideBarWaitsForRemainderAndNextCompleteBar()
    {
        CanonicalCompiledResult compiled = Compile(new MidoraProject(192), 10_000);

        BufferingRecoveryInterval interval = BufferingRecoveryPlanner.Plan(
            compiled,
            failureTick: 192,
            playbackEndTick: 10_000);

        Assert.Equal(new TickRange(192, 1536), interval.Range);
        Assert.True(interval.IncludesFollowingCompleteBar);
    }

    [Fact]
    public void MidBarTimeSignatureChangeDefinesTruncatedAndFollowingBars()
    {
        MidoraProject project = new(192);
        project.Conductor.TimeSignatures.Add(new TimeSignatureChange(project, 300, 3, 4));
        CanonicalCompiledResult compiled = Compile(project, 10_000);

        BufferingRecoveryInterval beforeChange = BufferingRecoveryPlanner.Plan(
            compiled,
            failureTick: 200,
            playbackEndTick: 10_000);
        BufferingRecoveryInterval atChange = BufferingRecoveryPlanner.Plan(
            compiled,
            failureTick: 300,
            playbackEndTick: 10_000);

        Assert.Equal(new TickRange(200, 876), beforeChange.Range);
        Assert.True(beforeChange.FailureBar.IsTruncatedByTimeSignatureChange);
        Assert.Equal(new TickRange(300, 876), atChange.Range);
        Assert.False(atChange.IncludesFollowingCompleteBar);
    }

    [Fact]
    public void SixteenQuarterNoteCapHasPriorityOverVeryLongBar()
    {
        MidoraProject project = new(192);
        project.Conductor.TimeSignatures.Clear();
        project.Conductor.TimeSignatures.Add(new TimeSignatureChange(project, 0, 99, 4));
        CanonicalCompiledResult compiled = Compile(project, 100_000);

        BufferingRecoveryInterval interval = BufferingRecoveryPlanner.Plan(
            compiled,
            failureTick: 0,
            playbackEndTick: 100_000);

        Assert.Equal(new TickRange(0, 3072), interval.Range);
        Assert.True(interval.WasQuarterNoteCapped);
        Assert.False(interval.WasPlaybackEndClipped);
    }

    [Fact]
    public void PlaybackEndClipsRecoveryInterval()
    {
        CanonicalCompiledResult compiled = Compile(new MidoraProject(192), 600);

        BufferingRecoveryInterval interval = BufferingRecoveryPlanner.Plan(
            compiled,
            failureTick: 100,
            playbackEndTick: 600);

        Assert.Equal(new TickRange(100, 600), interval.Range);
        Assert.True(interval.WasPlaybackEndClipped);
    }

    [Fact]
    public void RecoveryRejectsRangeOutsideCanonicalResult()
    {
        CanonicalCompiledResult compiled = Compile(new MidoraProject(192), 600);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BufferingRecoveryPlanner.Plan(compiled, 600, 601));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BufferingRecoveryPlanner.Plan(compiled, -1, 100));
    }

    private static CanonicalCompiledResult Compile(MidoraProject project, long endTick)
    {
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult result = compiler.CompileFull(project, new CompilationRequest
        {
            Purpose = CompilationPurpose.Playback,
            StartTick = 0,
            EndTick = endTick
        });
        Assert.True(result.IsConsumable);
        return result;
    }
}
