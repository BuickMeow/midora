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

    private static BassMidiRendererSettings CreateRendererSettings(int workFrameCount) => new(
        BassMidiNoteOffPolicy.ReleaseAllMatchingNotes,
        BassMidiInterpolation.BassDefault,
        BassMidiSampleLoading.OnDemand,
        0,
        0,
        workFrameCount);
}
