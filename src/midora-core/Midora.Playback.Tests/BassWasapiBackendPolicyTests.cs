using Midora.Audio;
using Midora.Audio.Bass;
using Midora.Playback.BassWasapi;
using System.Runtime.Versioning;

namespace Midora.Playback.Tests;

[SupportedOSPlatform("windows")]
public sealed class BassWasapiBackendPolicyTests
{
    [Fact]
    public void InProcessBackendRejectsNonFormalWorkBlock()
    {
        BassWasapiPlaybackOptions options = BassWasapiPlaybackOptions.PrototypeCandidate with
        {
            WorkFrameCount = 512
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new BassWasapiPlaybackBackend(options));
    }

    [Fact]
    public void ChildBackendRejectsNonFormalWorkBlock()
    {
        BassWasapiChildPlaybackOptions options = new(
            string.Empty,
            string.Empty,
            null,
            100,
            50,
            CreateRendererSettings(512),
            AudioMasterSettings.LimiterV1,
            TimeSpan.FromSeconds(1));

        Assert.Throws<ArgumentOutOfRangeException>(() => new BassWasapiChildPlaybackBackend(options));
    }

    [Fact]
    public void BothRealtimeBackendsAcceptFormalWorkBlock()
    {
        using BassWasapiPlaybackBackend inProcess = new(BassWasapiPlaybackOptions.PrototypeCandidate);
        BassWasapiChildPlaybackOptions options = new(
            string.Empty,
            string.Empty,
            null,
            100,
            50,
            CreateRendererSettings(InitialReleaseAudioRuntimePolicy.WorkFrameCount),
            AudioMasterSettings.LimiterV1,
            TimeSpan.FromSeconds(1));
        using BassWasapiChildPlaybackBackend child = new(options);
    }

    [Theory]
    [InlineData(AudioWorkerState.Playing, null, false)]
    [InlineData(AudioWorkerState.Playing, 0, true)]
    [InlineData(AudioWorkerState.Completed, 0, false)]
    [InlineData(AudioWorkerState.Stopped, 0, false)]
    [InlineData(AudioWorkerState.OutputDeviceUnavailable, 0, false)]
    [InlineData(AudioWorkerState.Completed, 1, true)]
    [InlineData(AudioWorkerState.Stopped, 1, true)]
    [InlineData(AudioWorkerState.OutputDeviceUnavailable, 1, true)]
    public void ChildExitIsFaultUnlessCodeAndSharedStateAreBothTerminal(
        AudioWorkerState state,
        int? exitCode,
        bool expected)
    {
        Assert.Equal(
            expected,
            BassWasapiChildPlaybackBackend.IsUnexpectedWorkerTermination(state, exitCode));
    }

    [Fact]
    public void ChildCleanupStillReleasesWhenStatusCaptureFails()
    {
        bool released = false;
        InvalidDataException captureFailure = new("corrupt shared status");

        Exception? failure = BassWasapiChildPlaybackBackend.ExecuteGuaranteedRelease(
            () => throw captureFailure,
            () => released = true);

        Assert.Same(captureFailure, failure);
        Assert.True(released);
    }

    [Fact]
    public void ChildCleanupAggregatesCaptureAndReleaseFailures()
    {
        InvalidDataException captureFailure = new("corrupt shared status");
        IOException releaseFailure = new("mapping release failed");

        AggregateException failure = Assert.IsType<AggregateException>(
            BassWasapiChildPlaybackBackend.ExecuteGuaranteedRelease(
                () => throw captureFailure,
                () => throw releaseFailure));

        Assert.Equal([captureFailure, releaseFailure], failure.InnerExceptions);
    }

    [Fact]
    public void ChildBackendRejectsNonFormalRuntimePolicyOptions()
    {
        BassWasapiChildPlaybackOptions baseline = new(
            string.Empty,
            string.Empty,
            null,
            100,
            50,
            CreateRendererSettings(InitialReleaseAudioRuntimePolicy.WorkFrameCount),
            AudioMasterSettings.LimiterV1,
            TimeSpan.FromSeconds(1));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BassWasapiChildPlaybackBackend(baseline with
            {
                DeviceBufferRequestMilliseconds = 4
            }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BassWasapiChildPlaybackBackend(baseline with
            {
                PreparingTimeout = TimeSpan.Zero
            }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BassWasapiChildPlaybackBackend(baseline with
            {
                MasterSettings = new AudioMasterSettings(-3, 0.9f, 50)
            }));
    }

    private static BassMidiRendererSettings CreateRendererSettings(int workFrameCount) => new(
        BassMidiPolyphonyConfiguration.DefaultMaximumSampleVoicesPerUnitStream,
        workFrameCount);
}
