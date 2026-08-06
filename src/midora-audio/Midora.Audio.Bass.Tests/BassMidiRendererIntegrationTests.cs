using Midora.AudioDevice;
using Midora.Midi;
using System.Runtime.InteropServices;

namespace Midora.Audio.Bass.Tests;

public sealed class BassMidiRendererIntegrationTests
{
    private const int SampleRate = 48_000;
    private static string SoundFontPath =>
        NativeAudioIntegrationEnvironment.RequireSoundFontPath();

    [Fact]
    public void ProducesIdenticalSamplesAcrossDifferentBlocksWithinConfiguredVoiceLimit()
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
    public void StreamFlagsAlwaysDisableEffectsAndReleaseOnlyOldestMatchingNote()
    {
        BassMidiRendererSettings settings = new(
            BassMidiPolyphonyConfiguration.DefaultMaximumSampleVoiceCount,
            256);

        uint flags = BassMidiRenderer.BuildStreamFlags(settings);

        Assert.NotEqual(0u, flags & Midora.NativeInterops.BassMidi.BASSMIDI.BASS_MIDI_NOFX);
        Assert.NotEqual(0u, flags & Midora.NativeInterops.BassMidi.BASSMIDI.BASS_MIDI_NOTEOFF1);
        Assert.Equal(0u, flags & Midora.NativeInterops.BassMidi.BASSMIDI.BASS_MIDI_SINCINTER);
    }

    [Fact]
    public unsafe void EveryPortUsesEightPointSincCpuZeroAndTheSameConfiguredVoiceLimit()
    {
        EnsureEnvironment();
        MidiRenderPlan single = CreateSingleNotePlan(channel: 0);
        MidiRenderPlan plan = new(
            SampleRate,
            single.TotalFrameCount,
            [single.Ports[0], new MidiPortRenderPlan(1, single.Ports[0].Events)]);
        const int customVoiceLimit = 901;
        using BassMidiRenderer renderer = new(
            plan,
            SoundFontPath,
            new BassMidiRendererSettings(customVoiceLimit, 256),
            AudioMasterSettings.LimiterV1);

        foreach (int port in new[] { 0, 1 })
        {
            Assert.Equal(
                1f,
                renderer.GetStreamAttributeForDiagnostics(
                    port,
                    Midora.NativeInterops.BassMidi.BASSMIDI.BASS_ATTRIB_MIDI_SRC));
            Assert.Equal(
                customVoiceLimit,
                renderer.GetStreamAttributeForDiagnostics(
                    port,
                    Midora.NativeInterops.BassMidi.BASSMIDI.BASS_ATTRIB_MIDI_VOICES));
            Assert.Equal(
                0f,
                renderer.GetStreamAttributeForDiagnostics(
                    port,
                    Midora.NativeInterops.BassMidi.BASSMIDI.BASS_ATTRIB_MIDI_CPU));
        }

        float* samples = stackalloc float[256 * 2];
        Assert.Equal(256, renderer.PullFrames(samples, 256).FrameCount);
        Assert.Equal(AudioRenderFaultCode.None, renderer.Fault.Code);
    }

    [Fact]
    public void RealtimeAndOfflineVoicePreferencesAreIndependentAndDefaultTo750()
    {
        BassMidiPolyphonyConfiguration defaults = BassMidiPolyphonyConfiguration.Default;
        Assert.Equal(750, defaults.RealtimeMaximumSampleVoiceCount);
        Assert.Equal(750, defaults.OfflineMaximumSampleVoiceCount);

        BassMidiPolyphonyConfiguration custom = new(321, 654);
        Assert.Equal(321, custom.CreateRealtimeRendererSettings(256).MaximumSampleVoiceCount);
        Assert.Equal(654, custom.CreateOfflineRendererSettings(256).MaximumSampleVoiceCount);
    }

    [Fact]
    public void VoicePreferencesRejectValuesThatCannotBeTransferredExactlyToBass()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BassMidiPolyphonyConfiguration(0, 750));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BassMidiPolyphonyConfiguration(750, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BassMidiPolyphonyConfiguration(
            BassMidiPolyphonyConfiguration.MaximumSampleVoiceCount + 1,
            750));
    }

    [Fact]
    public void ReferencedPresetPreloadPlanTracksBankProgramAndNoteStateDeterministically()
    {
        MidiPortRenderPlan first = new(0,
        [
            new ScheduledMidiMessage(0, MidiMessage.NoteOn(1, 60, 100)),
            new ScheduledMidiMessage(0, MidiMessage.ControlChange(0, 0, 2)),
            new ScheduledMidiMessage(0, MidiMessage.ProgramChange(0, 5)),
            new ScheduledMidiMessage(0, MidiMessage.NoteOn(0, 60, 100)),
            new ScheduledMidiMessage(1, MidiMessage.ProgramChange(0, 7)),
            new ScheduledMidiMessage(1, MidiMessage.NoteOn(0, 61, 100)),
            new ScheduledMidiMessage(2, MidiMessage.NoteOn(0, 62, 0))
        ]);
        MidiPortRenderPlan second = new(1, first.Events);
        MidiRenderPlan plan = new(SampleRate, 128, [first, second]);

        Assert.Equal([0, (2 * 128) + 5, (2 * 128) + 7], BassMidiRenderer.CollectReferencedPresetKeys(plan));
    }

    [Fact]
    public unsafe void MissingPresetKeepsBassFallbackAvailableAfterPreparing()
    {
        EnsureEnvironment();
        ScheduledMidiMessage[] events =
        [
            new(0, MidiMessage.ControlChange(0, 0, 127)),
            new(0, MidiMessage.ProgramChange(0, 127)),
            new(0, MidiMessage.NoteOn(0, 60, 100)),
            new(256, MidiMessage.NoteOff(0, 60, 0))
        ];
        MidiRenderPlan plan = new(SampleRate, 512, [new MidiPortRenderPlan(0, events)]);
        using BassMidiRenderer renderer = CreateRenderer(plan, 256);
        float* samples = stackalloc float[256 * 2];

        Assert.Equal(256, renderer.PullFrames(samples, 256).FrameCount);
        Assert.Equal(AudioRenderFaultCode.None, renderer.Fault.Code);
    }

    [Fact]
    public unsafe void ZeroVelocityNoteOffReleasesOldestSamePitchInstanceOneAtATime()
    {
        EnsureEnvironment();
        ScheduledMidiMessage[] events =
        [
            new(0, MidiMessage.NoteOn(0, 60, 100)),
            new(128, MidiMessage.NoteOn(0, 60, 80)),
            new(256, MidiMessage.NoteOff(0, 60, 0)),
            new(512, MidiMessage.NoteOff(0, 60, 0))
        ];
        MidiRenderPlan plan = new(SampleRate, 768, [new MidiPortRenderPlan(0, events)]);
        using BassMidiRenderer renderer = CreateRenderer(plan, 256);
        float* samples = stackalloc float[513 * 2];

        Assert.Equal(257, renderer.PullFrames(samples, 257).FrameCount);
        Assert.Equal(1u, renderer.GetPressedKeyCountForDiagnostics(0, 0));
        Assert.Contains(new ReadOnlySpan<float>(samples, 257 * 2).ToArray(), static sample => sample != 0);

        Assert.Equal(256, renderer.PullFrames(samples, 256).FrameCount);
        Assert.Equal(0u, renderer.GetPressedKeyCountForDiagnostics(0, 0));
        Assert.Equal(AudioRenderFaultCode.None, renderer.Fault.Code);
    }

    [Fact]
    public unsafe void CutPreviousReleaseNoteOffLeavesTheOverlappingReplacementPressed()
    {
        EnsureEnvironment();
        ScheduledMidiMessage[] events =
        [
            new(0, MidiMessage.NoteOn(0, 60, 100)),
            new(256, MidiMessage.NoteOn(0, 60, 90)),
            // The previous instance reaches its release NoteOff after the replacement starts.
            new(512, MidiMessage.NoteOff(0, 60, 0)),
            new(768, MidiMessage.NoteOff(0, 60, 0))
        ];
        MidiRenderPlan plan = new(SampleRate, 1_024, [new MidiPortRenderPlan(0, events)]);
        using BassMidiRenderer renderer = CreateRenderer(plan, 256);
        float* samples = stackalloc float[769 * 2];

        Assert.Equal(513, renderer.PullFrames(samples, 513).FrameCount);
        Assert.Equal(1u, renderer.GetPressedKeyCountForDiagnostics(0, 0));

        Assert.Equal(256, renderer.PullFrames(samples, 256).FrameCount);
        Assert.Equal(0u, renderer.GetPressedKeyCountForDiagnostics(0, 0));
        Assert.Equal(AudioRenderFaultCode.None, renderer.Fault.Code);
    }

    [Fact]
    public unsafe void HardBoundaryPairedNoteOffsThenControllerResetReleaseAllOverlapInstances()
    {
        EnsureEnvironment();
        ScheduledMidiMessage[] events =
        [
            new(0, MidiMessage.NoteOn(0, 60, 100)),
            new(128, MidiMessage.NoteOn(0, 60, 80)),
            new(512, MidiMessage.NoteOff(0, 60, 0)),
            new(512, MidiMessage.NoteOff(0, 60, 0)),
            new(512, MidiMessage.ControlChange(0, 121, 0))
        ];
        MidiRenderPlan plan = new(SampleRate, 768, [new MidiPortRenderPlan(0, events)]);
        using BassMidiRenderer renderer = CreateRenderer(plan, 256);
        float* samples = stackalloc float[513 * 2];

        Assert.Equal(513, renderer.PullFrames(samples, 513).FrameCount);
        Assert.Equal(0u, renderer.GetPressedKeyCountForDiagnostics(0, 0));
        Assert.Equal(AudioRenderFaultCode.None, renderer.Fault.Code);
    }

    [Fact]
    public void ContinuesAfterSuccessfulPartialRawBatchSubmission()
    {
        EnsureEnvironment();
        ScheduledMidiMessage[] events =
        [
            new(0, MidiMessage.ControlChange(0, 0, 0)),
            new(0, MidiMessage.ControlChange(0, 32, 0)),
            new(0, MidiMessage.ProgramChange(0, 0)),
            new(0, MidiMessage.ControlChange(0, 101, 0)),
            new(0, MidiMessage.ControlChange(0, 100, 0)),
            new(0, MidiMessage.ControlChange(0, 6, 2)),
            new(0, MidiMessage.ControlChange(0, 38, 0)),
            new(0, MidiMessage.PitchWheelChange(0, 8_192)),
            new(0, MidiMessage.NoteOn(0, 60, 100)),
            new(512, MidiMessage.NoteOff(0, 60, 0))
        ];
        MidiRenderPlan plan = new(SampleRate, 1_024, [new MidiPortRenderPlan(0, events)]);

        float[] samples = Render(plan, 257, 333, out long allocated);

        Assert.Equal(0, allocated);
        Assert.Contains(samples, static sample => sample != 0);
    }

    [Fact]
    public unsafe void MonitoringEnableDoesNotRetriggerSkippedNoteAndAllowsFutureEventsWithoutAllocating()
    {
        EnsureEnvironment();
        Guid sourceId = Guid.Parse("78cdf55d-d33e-42f3-8ea9-286526bd3be4");
        ScheduledMidiMessage[] events =
        [
            new(0, MidiMessage.ProgramChange(0, 0), 0),
            new(0, MidiMessage.NoteOn(0, 60, 100), 0),
            new(1_024, MidiMessage.NoteOff(0, 60, 0), 0),
            new(2_048, MidiMessage.NoteOn(0, 67, 100), 0),
            new(3_072, MidiMessage.NoteOff(0, 67, 0), 0)
        ];
        MidiRenderPlan plan = new(
            SampleRate,
            4_096,
            [new MidiPortRenderPlan(0, events)],
            [sourceId],
            [0]);
        BassMidiRendererSettings settings = new(
            BassMidiPolyphonyConfiguration.DefaultMaximumSampleVoiceCount,
            256);
        using BassMidiRenderer renderer = new(
            plan,
            SoundFontPath,
            settings,
            AudioMasterSettings.LimiterV1);
        float[] samples = new float[checked((int)plan.TotalFrameCount * 2)];

        fixed (float* destination = samples)
        {
            AudioPullResult first = renderer.PullFrames(destination, 1_536);
            Assert.Equal(1_536, first.FrameCount);
            MidiMonitoringCommand command = MidiMonitoringCommand.EnableSource(0);
            ReadOnlySpan<MidiMonitoringCommand> commands = MemoryMarshal.CreateReadOnlySpan(ref command, 1);
            long enqueueBefore = GC.GetAllocatedBytesForCurrentThread();
            renderer.EnqueueMonitoringCommands(commands);
            long enqueueAllocated = GC.GetAllocatedBytesForCurrentThread() - enqueueBefore;
            long before = GC.GetAllocatedBytesForCurrentThread();
            AudioPullResult second = renderer.PullFrames(destination + (1_536 * 2), 2_560);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(2_560, second.FrameCount);
            Assert.Equal(0, enqueueAllocated);
            Assert.Equal(0, allocated);
        }

        Assert.All(samples.AsSpan(0, 2_048 * 2).ToArray(), value => Assert.Equal(0f, value));
        Assert.Contains(samples.AsSpan(2_048 * 2).ToArray(), value => value != 0f);
        Assert.Equal(AudioRenderFaultCode.None, renderer.Fault.Code);
    }

    private static unsafe float[] Render(
        MidiRenderPlan plan,
        int internalBlockFrames,
        int pullBlockFrames,
        out long allocatedBytes)
    {
        BassMidiRendererSettings settings = new(
            BassMidiPolyphonyConfiguration.DefaultMaximumSampleVoiceCount,
            maximumWorkFrameCount: internalBlockFrames);
        using BassMidiRenderer renderer = new(
            plan,
            SoundFontPath,
            settings,
            AudioMasterSettings.LimiterV1);

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

    private static BassMidiRenderer CreateRenderer(MidiRenderPlan plan, int maximumWorkFrameCount)
    {
        BassMidiRendererSettings settings = new(
            BassMidiPolyphonyConfiguration.DefaultMaximumSampleVoiceCount,
            maximumWorkFrameCount);
        return new BassMidiRenderer(
            plan,
            SoundFontPath,
            settings,
            AudioMasterSettings.LimiterV1);
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
        NativeAudioIntegrationEnvironment.LoadBassMidi();
        _ = SoundFontPath;
    }
}
