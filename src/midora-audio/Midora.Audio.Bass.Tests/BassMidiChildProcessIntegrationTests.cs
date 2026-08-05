using Midora.AudioDevice;
using Midora.Midi;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Midora.Audio.Bass.Tests;

[SupportedOSPlatform("windows")]
public sealed class BassMidiChildProcessIntegrationTests
{
    private const string SoundFontPath = @"D:\Soundfonts\sf2\sDetrimental Concert Grand Piano.sf2";

    [Fact]
    public unsafe void ChildProcessMatchesInProcessSamplesAndAllocatesNothingWhileRendering()
    {
        string repositoryRoot = FindRepositoryRoot();
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        string workerPath = Path.Combine(
            repositoryRoot,
            "src",
            "midora-audio",
            "Midora.Audio.Bass.Worker",
            "bin",
            configuration,
            "net10.0",
            "Midora.Audio.Bass.Worker.dll");
        string nativeDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Midora",
            "Native",
            "BASS",
            "win-x64");
        _ = NativeLibrary.Load(Path.Combine(nativeDirectory, "bass.dll"));
        _ = NativeLibrary.Load(Path.Combine(nativeDirectory, "bassmidi.dll"));

        MidiRenderPlan plan = CreatePlan();
        BassMidiRendererSettings settings = new(
            BassMidiNoteOffPolicy.ReleaseAllMatchingNotes,
            BassMidiInterpolation.BassDefault,
            BassMidiSampleLoading.OnDemand,
            maximumVoices: 0,
            cpuLimitPercent: 0,
            maximumWorkFrameCount: 257);
        float[] expected = RenderInProcess(plan, settings);
        float[] actual = new float[expected.Length];

        using BassMidiChildProcessSession session = new(
            plan,
            SoundFontPath,
            settings,
            AudioMasterSettings.LimiterV1Candidate,
            ipcAudioBufferMilliseconds: 100,
            workerPath,
            nativeDirectory,
            BassMidiChildConsumptionMode.OfflineBlocking,
            preparingTimeout: TimeSpan.FromSeconds(30));

        fixed (float* destination = actual)
        {
            int completed = 0;
            while (completed < plan.TotalFrameCount)
            {
                int request = (int)Math.Min(333, plan.TotalFrameCount - completed);
                AudioPullResult result = session.PullFrames(destination + (completed * 2), request);
                Assert.NotEqual(AudioPullStatus.Fault, result.Status);
                Assert.Equal(request, result.FrameCount);
                completed += result.FrameCount;
            }
        }

        Assert.True(SpinWait.SpinUntil(() => session.ProducerCompleted, TimeSpan.FromSeconds(5)));
        Assert.False(session.ProducerFaulted, session.StandardError);
        Assert.Equal(0, session.RenderingThreadAllocatedBytes);
        Assert.True(MemoryMarshal.AsBytes(expected.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual.AsSpan())));
    }

    [Fact]
    public unsafe void ChildControlPipeEnablesFutureSourceEventsWithoutAudioThreadAllocation()
    {
        string repositoryRoot = FindRepositoryRoot();
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        string workerPath = Path.Combine(
            repositoryRoot,
            "src",
            "midora-audio",
            "Midora.Audio.Bass.Worker",
            "bin",
            configuration,
            "net10.0",
            "Midora.Audio.Bass.Worker.dll");
        string nativeDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Midora",
            "Native",
            "BASS",
            "win-x64");
        Guid sourceId = Guid.Parse("bf0726a6-93e8-46ef-b4bb-d0952c750998");
        MidiRenderPlan plan = new(
            48_000,
            16_384,
            [new MidiPortRenderPlan(0,
            [
                new ScheduledMidiMessage(0, MidiMessage.ProgramChange(0, 0), 0),
                new ScheduledMidiMessage(8_192, MidiMessage.NoteOn(0, 64, 100), 0),
                new ScheduledMidiMessage(12_288, MidiMessage.NoteOff(0, 64, 0), 0)
            ])],
            [sourceId],
            [0]);
        BassMidiRendererSettings settings = new(
            BassMidiNoteOffPolicy.ReleaseAllMatchingNotes,
            BassMidiInterpolation.BassDefault,
            BassMidiSampleLoading.OnDemand,
            0,
            0,
            256);
        float[] samples = new float[checked((int)plan.TotalFrameCount * 2)];

        using BassMidiChildProcessSession session = new(
            plan,
            SoundFontPath,
            settings,
            AudioMasterSettings.LimiterV1Candidate,
            20,
            workerPath,
            nativeDirectory,
            BassMidiChildConsumptionMode.OfflineBlocking,
            TimeSpan.FromSeconds(30));
        session.EnqueueMonitoringCommands([
            MidiMonitoringCommand.EnableSource(0),
            MidiMonitoringCommand.Send(0, MidiMessage.ProgramChange(0, 0))
        ]);
        fixed (float* destination = samples)
        {
            int completed = 0;
            while (completed < plan.TotalFrameCount)
            {
                int request = (int)Math.Min(333, plan.TotalFrameCount - completed);
                AudioPullResult pull = session.PullFrames(destination + (completed * 2), request);
                Assert.NotEqual(AudioPullStatus.Fault, pull.Status);
                Assert.Equal(request, pull.FrameCount);
                completed += pull.FrameCount;
            }
        }

        Assert.True(SpinWait.SpinUntil(() => session.ProducerCompleted, TimeSpan.FromSeconds(5)));
        Assert.False(session.ProducerFaulted, session.StandardError);
        Assert.Equal(0, session.RenderingThreadAllocatedBytes);
        Assert.All(samples.AsSpan(0, 8_192 * 2).ToArray(), value => Assert.Equal(0f, value));
        Assert.Contains(samples.AsSpan(8_192 * 2).ToArray(), value => value != 0f);
    }

    private static unsafe float[] RenderInProcess(
        MidiRenderPlan plan,
        BassMidiRendererSettings settings)
    {
        using BassMidiRenderer renderer = new(
            plan,
            SoundFontPath,
            settings,
            AudioMasterSettings.LimiterV1Candidate);
        float[] result = new float[checked((int)plan.TotalFrameCount * 2)];
        fixed (float* destination = result)
        {
            AudioPullResult pull = renderer.PullFrames(destination, checked((int)plan.TotalFrameCount));
            Assert.Equal(plan.TotalFrameCount, pull.FrameCount);
            Assert.NotEqual(AudioPullStatus.Fault, pull.Status);
        }

        return result;
    }

    private static MidiRenderPlan CreatePlan()
    {
        MidiPortRenderPlan port = new(0,
        [
            new ScheduledMidiMessage(0, MidiMessage.ProgramChange(0, 0)),
            new ScheduledMidiMessage(128, MidiMessage.NoteOn(0, 60, 80)),
            new ScheduledMidiMessage(2_048, MidiMessage.NoteOff(0, 60, 19))
        ]);
        return new MidiRenderPlan(48_000, 4_096, [port]);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Build.props")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
