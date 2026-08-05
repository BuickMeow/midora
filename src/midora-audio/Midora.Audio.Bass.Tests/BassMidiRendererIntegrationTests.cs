using Midora.AudioDevice;
using Midora.Midi;
using System.Runtime.InteropServices;

namespace Midora.Audio.Bass.Tests;

public sealed class BassMidiRendererIntegrationTests
{
    private const int SampleRate = 48_000;
    private const string SoundFontPath = @"D:\Soundfonts\sf2\sDetrimental Concert Grand Piano.sf2";

    static BassMidiRendererIntegrationTests()
    {
        string bassPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Midora",
            "Native",
            "BASS",
            "win-x64");
        _ = NativeLibrary.Load(Path.Combine(bassPath, "bass.dll"));
        _ = NativeLibrary.Load(Path.Combine(bassPath, "bassmidi.dll"));
    }

    [Fact]
    public void ProducesIdenticalSamplesAcrossDifferentPullAndInternalBlockSizes()
    {
        EnsureEnvironment();
        MidiRenderPlan plan = CreateSingleNotePlan(channel: 0);

        float[] first = Render(plan, internalBlockFrames: 97, pullBlockFrames: 127, out long firstAllocated);
        float[] second = Render(plan, internalBlockFrames: 509, pullBlockFrames: 1_003, out long secondAllocated);

        Assert.Equal(0, firstAllocated);
        Assert.Equal(0, secondAllocated);
        Assert.True(MemoryMarshal.AsBytes(first.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(second.AsSpan())));
    }

    [Fact]
    public void IsSilentBeforeTheExactEventFrameAndProducesAudioAfterIt()
    {
        EnsureEnvironment();
        MidiRenderPlan plan = CreateSingleNotePlan(channel: 0);

        float[] samples = Render(plan, internalBlockFrames: 257, pullBlockFrames: 333, out _);

        Assert.All(samples.AsSpan(0, 256 * 2).ToArray(), static sample => Assert.Equal(0, sample));
        Assert.Contains(samples.AsSpan(256 * 2).ToArray(), static sample => sample != 0);
    }

    [Fact]
    public void InitializesMidiChannel10AsMelodic()
    {
        EnsureEnvironment();

        float[] channel1 = Render(CreateSingleNotePlan(channel: 0), 257, 333, out _);
        float[] channel10 = Render(CreateSingleNotePlan(channel: 9), 257, 333, out _);

        Assert.True(MemoryMarshal.AsBytes(channel1.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(channel10.AsSpan())));
    }

    [Fact]
    public void SumsAllActualPortsBeforeTheMasterChain()
    {
        EnsureEnvironment();
        MidiRenderPlan singlePlan = CreateSingleNotePlan(channel: 0, velocity: 20);
        MidiPortRenderPlan secondPort = new(1, singlePlan.Ports[0].Events);
        MidiRenderPlan doublePlan = new(
            SampleRate,
            singlePlan.TotalFrameCount,
            [singlePlan.Ports[0], secondPort]);

        float[] single = Render(singlePlan, 257, 333, out _);
        float[] doubled = Render(doublePlan, 257, 333, out _);
        double singleEnergy = single.Sum(static value => Math.Abs(value));
        double doubledEnergy = doubled.Sum(static value => Math.Abs(value));

        Assert.InRange(doubledEnergy / singleEnergy, 1.999, 2.001);
    }

    [Fact]
    public void StreamFlagsAlwaysDisableEffectsAndKeepNoteOffPolicyExplicit()
    {
        BassMidiRendererSettings releaseAll = new(
            BassMidiNoteOffPolicy.ReleaseAllMatchingNotes,
            BassMidiInterpolation.BassDefault,
            BassMidiSampleLoading.OnDemand,
            0,
            0,
            256);
        BassMidiRendererSettings releaseOldest = new(
            BassMidiNoteOffPolicy.ReleaseOldestMatchingNote,
            BassMidiInterpolation.BassDefault,
            BassMidiSampleLoading.OnDemand,
            0,
            0,
            256);

        uint releaseAllFlags = BassMidiRenderer.BuildStreamFlags(releaseAll);
        uint releaseOldestFlags = BassMidiRenderer.BuildStreamFlags(releaseOldest);

        Assert.NotEqual(0u, releaseAllFlags & Midora.NativeInterops.BassMidi.BASSMIDI.BASS_MIDI_NOFX);
        Assert.Equal(0u, releaseAllFlags & Midora.NativeInterops.BassMidi.BASSMIDI.BASS_MIDI_NOTEOFF1);
        Assert.NotEqual(0u, releaseOldestFlags & Midora.NativeInterops.BassMidi.BASSMIDI.BASS_MIDI_NOTEOFF1);
    }

    private static unsafe float[] Render(
        MidiRenderPlan plan,
        int internalBlockFrames,
        int pullBlockFrames,
        out long allocatedBytes)
    {
        BassMidiRendererSettings settings = new(
            BassMidiNoteOffPolicy.ReleaseAllMatchingNotes,
            BassMidiInterpolation.BassDefault,
            BassMidiSampleLoading.OnDemand,
            maximumVoices: 0,
            cpuLimitPercent: 0,
            maximumWorkFrameCount: internalBlockFrames);
        using BassMidiRenderer renderer = new(
            plan,
            SoundFontPath,
            settings,
            AudioMasterSettings.InitialReleaseDefault);

        float[] samples = new float[checked((int)plan.TotalFrameCount * 2)];
        fixed (float* destination = samples)
        {
            _ = renderer.PullFrames(destination, 0);
            int completed = 0;
            long before = GC.GetAllocatedBytesForCurrentThread();
            while (completed < plan.TotalFrameCount)
            {
                int request = (int)Math.Min(pullBlockFrames, plan.TotalFrameCount - completed);
                AudioPullResult result = renderer.PullFrames(destination + (completed * 2), request);
                if (result.Status == AudioPullStatus.Fault || result.FrameCount != request)
                {
                    break;
                }

                completed += result.FrameCount;
            }

            allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(plan.TotalFrameCount, completed);
            Assert.Equal(AudioRenderFaultCode.None, renderer.Fault.Code);
        }

        return samples;
    }

    private static MidiRenderPlan CreateSingleNotePlan(byte channel, byte velocity = 80)
    {
        ScheduledMidiMessage[] events =
        [
            new(0, MidiMessage.ControlChange(channel, 0, 0)),
            new(0, MidiMessage.ControlChange(channel, 32, 0)),
            new(0, MidiMessage.ProgramChange(channel, 0)),
            new(256, MidiMessage.NoteOn(channel, 60, velocity)),
            new(2_048, MidiMessage.NoteOff(channel, 60, 31))
        ];
        MidiPortRenderPlan port = new(0, events);
        return new MidiRenderPlan(SampleRate, 4_096, [port]);
    }

    private static void EnsureEnvironment()
    {
        Assert.True(File.Exists(SoundFontPath), $"Missing integration-test SoundFont: {SoundFontPath}");
    }
}
