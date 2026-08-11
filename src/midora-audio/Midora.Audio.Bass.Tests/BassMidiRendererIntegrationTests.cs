using Midora.AudioDevice;
using Midora.Audio.Bass.Tests.Console;
using Midora.Compiler;
using Midora.Midi;
using Midora.Playback;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

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
    public void ProducesIdenticalComplexCanonicalSamplesAcrossDifferentBlocksWithinConfiguredVoiceLimit()
    {
        EnsureEnvironment();
        MidiRenderPlan plan = CreateComplexTempoLoopPlan();
        Assert.Equal(106, plan.Ports[0].Events.Length);

        float[] first = Render(plan, internalBlockFrames: 2_048, pullBlockFrames: 1_003, out long firstAllocated);
        float[] second = Render(plan, internalBlockFrames: 256, pullBlockFrames: 1_003, out long secondAllocated);

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
    public void ParallelSegmentProducersAreSampleIdenticalToSequentialDecode()
    {
        EnsureEnvironment();
        MidiRenderPlan single = CreateSingleNotePlan(channel: 0, velocity: 20);
        MidiRenderPlan plan = new(
            SampleRate,
            single.TotalFrameCount,
            [single.Ports[0], new MidiPortRenderPlan(1, single.Ports[0].Events)]);

        float[] sequential = Render(plan, 257, 333, out _, segmentProducerConcurrency: 1);
        float[] parallel = Render(plan, 257, 333, out _, segmentProducerConcurrency: 4);

        Assert.True(MemoryMarshal.AsBytes(sequential.AsSpan()).SequenceEqual(
            MemoryMarshal.AsBytes(parallel.AsSpan())));
    }

    [Fact]
    public void StreamFlagsAlwaysDisableEffectsAndReleaseOnlyOldestMatchingNote()
    {
        BassMidiRendererSettings settings = new(
            BassMidiPolyphonyConfiguration.DefaultMaximumSampleVoicesPerUnitStream,
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
            AudioMasterSettings.LimiterV1,
            segmentProducerConcurrency: 4);

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
    public void RealtimeAndOfflineVoicePreferencesAreIndependentAndDefaultTo500()
    {
        BassMidiPolyphonyConfiguration defaults = BassMidiPolyphonyConfiguration.Default;
        Assert.Equal(500, defaults.RealtimeMaximumSampleVoicesPerUnitStream);
        Assert.Equal(500, defaults.OfflineMaximumSampleVoicesPerUnitStream);

        BassMidiPolyphonyConfiguration custom = new(321, 654);
        Assert.Equal(321, custom.CreateRealtimeRendererSettings(256).MaximumSampleVoicesPerUnitStream);
        Assert.Equal(654, custom.CreateOfflineRendererSettings(256).MaximumSampleVoicesPerUnitStream);
    }

    [Fact]
    public void VoicePreferencesRejectValuesThatCannotBeTransferredExactlyToBass()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BassMidiPolyphonyConfiguration(0, 750));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BassMidiPolyphonyConfiguration(750, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BassMidiPolyphonyConfiguration(
            BassMidiPolyphonyConfiguration.MaximumSampleVoicesPerUnitStream + 1,
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
    public unsafe void HeldPreviewPlanCanReplaceOnlyTheUnrenderedFutureAtTheProducerFrontier()
    {
        EnsureEnvironment();
        MidiRenderPlan causal = new(
            SampleRate,
            1_024,
            [
                new MidiPortRenderPlan(
                    0,
                    [
                        new(0, MidiMessage.NoteOn(0, 60, 100)),
                        new(512, MidiMessage.NoteOff(0, 60, 0))
                    ])
            ]);
        MidiRenderPlan replacement = new(
            SampleRate,
            1_280,
            [
                new MidiPortRenderPlan(
                    0,
                    [
                        new(0, MidiMessage.NoteOn(0, 72, 100)),
                        new(256, MidiMessage.NoteOff(0, 60, 0)),
                        new(256, MidiMessage.NoteOn(0, 64, 90)),
                        new(768, MidiMessage.NoteOff(0, 64, 0))
                    ])
            ]);
        MidiRenderPlan spliced = MidiRenderPlanSplicer.SpliceAtProducerFrontier(
            causal,
            replacement,
            256);
        using BassMidiRenderer renderer = CreateRenderer(causal, 256);
        float* samples = stackalloc float[512 * 2];

        Assert.Equal(256, renderer.PullFrames(samples, 256).FrameCount);
        Assert.Equal(1u, renderer.GetPressedKeyCountForDiagnostics(0, 0));

        renderer.ReplaceFuturePlan(spliced, 256);
        Assert.Equal(256, renderer.PullFrames(samples, 256).FrameCount);
        Assert.Equal(1u, renderer.GetPressedKeyCountForDiagnostics(0, 0));
        Assert.Equal(AudioRenderFaultCode.None, renderer.Fault.Code);

        Assert.Equal(256, renderer.PullFrames(samples, 256).FrameCount);
        Assert.Equal(1, renderer.PullFrames(samples, 1).FrameCount);
        Assert.Equal(0u, renderer.GetPressedKeyCountForDiagnostics(0, 0));
    }

    [Fact]
    public unsafe void HeldPreviewPlanReplacementPreservesAlreadyRenderedStagedFrames()
    {
        EnsureEnvironment();
        MidiRenderPlan plan = new(
            SampleRate,
            1_024,
            [new MidiPortRenderPlan(0, [new(0, MidiMessage.NoteOn(0, 60, 100))])]);
        using BassMidiRenderer renderer = CreateRenderer(plan, 256);
        float* samples = stackalloc float[256 * 2];

        Assert.Equal(1, renderer.PullFrames(samples, 1).FrameCount);

        Assert.Equal(256, renderer.RenderPositionFrames);
        renderer.ReplaceFuturePlan(plan, 256);
        Assert.Equal(255, renderer.PullFrames(samples, 255).FrameCount);
        Assert.Equal(256, renderer.PositionFrames);
        Assert.Throws<InvalidOperationException>(() => renderer.ReplaceFuturePlan(plan, 255));
    }

    [Fact]
    public unsafe void HeldPreviewGateEndCanReplaceAtTheProducerFrontierWithoutFaulting()
    {
        EnsureEnvironment();
        MidiRenderPlan causal = new(
            SampleRate,
            16_000,
            [new MidiPortRenderPlan(
                0,
                [
                    new(0, MidiMessage.NoteOn(0, 0, 100)),
                    new(14_000, MidiMessage.NoteOff(0, 0, 0))
                ])]);
        MidiRenderPlan continuation = new(
            SampleRate,
            20_000,
            [new MidiPortRenderPlan(
                0,
                [
                    new(0, MidiMessage.NoteOn(0, 0, 100)),
                    new(7_936, MidiMessage.NoteOff(0, 0, 0)),
                    new(7_936, MidiMessage.ControlChange(0, 120, 0)),
                    new(7_936, MidiMessage.ControlChange(0, 7, 100)),
                    new(7_936, MidiMessage.PitchWheelChange(0, 0)),
                    new(7_936, MidiMessage.ControlChange(0, 101, 0)),
                    new(7_936, MidiMessage.ControlChange(0, 100, 0)),
                    new(7_936, MidiMessage.ControlChange(0, 6, 2)),
                    new(7_936, MidiMessage.ControlChange(0, 101, 127)),
                    new(7_936, MidiMessage.ControlChange(0, 100, 127))
                ])]);
        MidiRenderPlan replacement =
            MidiRenderPlanSplicer.SpliceHeldGateEndAtProducerFrontier(
                causal,
                continuation,
                7_936);
        using BassMidiRenderer renderer = CreateRenderer(causal, 256);
        float* samples = stackalloc float[8_000 * 2];

        Assert.Equal(7_936, renderer.PullFrames(samples, 7_936).FrameCount);
        renderer.ReplaceFuturePlan(replacement, 7_936);

        Assert.Equal(256, renderer.PullFrames(samples, 256).FrameCount);
        Assert.Equal(AudioRenderFaultCode.None, renderer.Fault.Code);
        Assert.Equal(0u, renderer.GetPressedKeyCountForDiagnostics(0, 0));
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
    public void AllSoundOffCutsTheSoundFontReleaseAndLeavesTheChannelSilent()
    {
        EnsureEnvironment();
        const long soundOffFrame = 6_000;
        const long maximumDeClickRampFrames = 256;
        MidiRenderPlan noteOffOnly = CreateAllSoundOffComparisonPlan(includeAllSoundOff: false);
        MidiRenderPlan withAllSoundOff = CreateAllSoundOffComparisonPlan(includeAllSoundOff: true);

        float[] releaseSamples = Render(noteOffOnly, 257, 333, out _);
        float[] cutSamples = Render(withAllSoundOff, 257, 333, out _);
        ReadOnlySpan<float> releaseTail = releaseSamples.AsSpan(checked((int)soundOffFrame * 2));
        ReadOnlySpan<float> cutTail = cutSamples.AsSpan(checked((int)soundOffFrame * 2));
        float releasePeak = MaximumAbsoluteSample(releaseTail);
        long releaseLastNonZeroFrame = LastNonZeroFrame(releaseSamples);
        long cutLastNonZeroFrame = LastNonZeroFrame(cutSamples);

        Assert.True(
            releasePeak > 0.000_001f,
            $"The comparison SoundFont produced no measurable release after NoteOff; peak={releasePeak:R}.");
        Assert.InRange(
            cutLastNonZeroFrame,
            soundOffFrame,
            soundOffFrame + maximumDeClickRampFrames - 1);
        Assert.True(
            releaseLastNonZeroFrame > soundOffFrame + maximumDeClickRampFrames,
            $"CC120 did not measurably shorten the release: cut last frame={cutLastNonZeroFrame}; "
            + $"natural-release last frame={releaseLastNonZeroFrame}.");
        Assert.All(
            cutSamples.AsSpan(checked((int)(soundOffFrame + maximumDeClickRampFrames) * 2)).ToArray(),
            static sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void AllSoundOffAffectsOnlyTheAddressedMidiChannel()
    {
        EnsureEnvironment();
        const int soundOffFrame = 6_000;
        const int maximumDeClickRampFrames = 256;
        const int comparisonEndFrame = 22_000;
        MidiRenderPlan otherChannelOnly = CreateAllSoundOffComparisonPlan(
            includeAllSoundOff: false,
            includeReleaseChannel: false,
            includeOtherChannel: true);
        MidiRenderPlan bothChannels = CreateAllSoundOffComparisonPlan(
            includeAllSoundOff: true,
            includeReleaseChannel: true,
            includeOtherChannel: true);

        float[] reference = Render(otherChannelOnly, 257, 333, out _);
        float[] actual = Render(bothChannels, 257, 333, out _);
        int comparisonStartFrame = soundOffFrame + maximumDeClickRampFrames;
        ReadOnlySpan<byte> referenceBytes = MemoryMarshal.AsBytes(
            reference.AsSpan(
                comparisonStartFrame * 2,
                (comparisonEndFrame - comparisonStartFrame) * 2));
        ReadOnlySpan<byte> actualBytes = MemoryMarshal.AsBytes(
            actual.AsSpan(
                comparisonStartFrame * 2,
                (comparisonEndFrame - comparisonStartFrame) * 2));

        Assert.Contains(
            reference.AsSpan(
                comparisonStartFrame * 2,
                (comparisonEndFrame - comparisonStartFrame) * 2).ToArray(),
            static sample => sample != 0f);
        Assert.True(
            referenceBytes.SequenceEqual(actualBytes),
            $"CC120 changed output from the non-addressed channel; maximum difference={MaximumAbsoluteDifference(referenceBytes, actualBytes):R}.");
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void CompiledSubVoiceExampleIsSilentAfterEveryAllocationGroupDeClickRamp()
    {
        EnsureEnvironment();
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult compiled = compiler.CompileFull(
            Program.CreateLogicalExample("subvoices"),
            new CompilationRequest { Purpose = CompilationPurpose.AudioRender });
        CanonicalMidiEvent[] soundOffs = compiled.Events.ToArray()
            .Where(value => value.Message.MessageType == MidiMessageType.ControlChange
                && value.Message.Byte1 == 120)
            .ToArray();
        MidiRenderPlan plan = MidiRenderPlanAdapter.Create(compiled, SampleRate);

        float[] samples = Render(plan, 2_048, 1_003, out long allocated);

        Assert.True(compiled.IsConsumable);
        Assert.Equal(12, soundOffs.Length);
        Assert.Equal(0, allocated);
        foreach ((long endTick, long nextStartTick) in new[]
        {
            (900L, 960L),
            (1_860L, 1_920L),
            (2_820L, 2_880L),
            (3_780L, 3_840L)
        })
        {
            int firstStableSilentFrame = checked((int)(endTick * 50 + 256));
            int nextStartFrame = checked((int)(nextStartTick * 50));
            Assert.All(
                samples.AsSpan(
                    firstStableSilentFrame * 2,
                    (nextStartFrame - firstStableSilentFrame) * 2).ToArray(),
                static sample => Assert.Equal(0f, sample));
        }
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
            new(0, MidiMessage.ControlChange(0, 101, 127)),
            new(0, MidiMessage.ControlChange(0, 100, 127)),
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
        const long sourceId = 7_003;
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
            BassMidiPolyphonyConfiguration.DefaultMaximumSampleVoicesPerUnitStream,
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

    [Fact]
    public void ExactSegmentPcmHitMatchesTheNativeMissWithoutRepeatingBassSynthesis()
    {
        EnsureEnvironment();
        string nativeDirectory = NativeAudioIntegrationEnvironment.RequireNativeDirectory();
        string cacheRoot = Path.Combine(
            Path.GetTempPath(),
            $"midora-native-unit-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cacheRoot);
        try
        {
            MidiRenderPlan sourcePlan = CreateCacheableSingleNotePlan();
            using AudioCacheSessionStore store = new(cacheRoot, 1024 * 1024);
            CacheAccess cache = new(store);
            const int maximumSampleVoices = 500;

            float[] missSamples;
            long missNativeFrames;
            using (AudioSegmentCacheStaging miss = Assert.IsType<AudioSegmentCacheStaging>(
                AudioSegmentCacheStaging.Create(
                    sourcePlan,
                    cache,
                    SoundFontPath,
                    nativeDirectory,
                    maximumSampleVoices)))
            {
                Assert.False(miss.Plan.Segments[0].PcmCacheHit);
                missSamples = RenderCachePlan(
                    miss.Plan,
                    miss.FilePath,
                    maximumSampleVoices,
                    out missNativeFrames);
                miss.PublishCompleted(cache, miss.Plan.TotalFrameCount);
            }

            float[] hitSamples;
            long hitNativeFrames;
            using (AudioSegmentCacheStaging hit = Assert.IsType<AudioSegmentCacheStaging>(
                AudioSegmentCacheStaging.Create(
                    sourcePlan,
                    cache,
                    SoundFontPath,
                    nativeDirectory,
                    maximumSampleVoices)))
            {
                Assert.True(hit.Plan.Segments[0].PcmCacheHit);
                hitSamples = RenderCachePlan(
                    hit.Plan,
                    hit.FilePath,
                    maximumSampleVoices,
                    out hitNativeFrames);
            }

            Assert.Equal(sourcePlan.TotalFrameCount, missNativeFrames);
            Assert.Equal(0, hitNativeFrames);
            Assert.True(MemoryMarshal.AsBytes(missSamples.AsSpan()).SequenceEqual(
                MemoryMarshal.AsBytes(hitSamples.AsSpan())));
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, recursive: true);
            }
        }
    }

    [Fact]
    public unsafe void MaximumCanonicalUnitCountUses256StreamsWithoutHotPathAllocation()
    {
        EnsureEnvironment();
        MidiRenderPlan plan = CreateMaximumUnitPlan();
        using BassMidiRenderer renderer = new(
            plan,
            SoundFontPath,
            new BassMidiRendererSettings(500, 256),
            AudioMasterSettings.LimiterV1,
            segmentProducerConcurrency: 4);
        float[] samples = new float[checked((int)plan.TotalFrameCount * 2)];

        fixed (float* destination = samples)
        {
            _ = renderer.PullFrames(destination, 0);
            int completed = 0;
            bool invalidPull = false;
            long before = GC.GetAllocatedBytesForCurrentThread();
            while (completed < plan.TotalFrameCount)
            {
                int requested = (int)Math.Min(257, plan.TotalFrameCount - completed);
                AudioPullResult result = renderer.PullFrames(destination + (completed * 2), requested);
                if (result.Status == AudioPullStatus.Fault || result.FrameCount <= 0)
                {
                    invalidPull = true;
                    break;
                }
                completed += result.FrameCount;
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.False(invalidPull);
            Assert.Equal(plan.TotalFrameCount, completed);
            Assert.Equal(0, allocated);
        }

        Assert.Equal(256, renderer.UnitStreamCountForDiagnostics);
        Assert.Equal(plan.TotalFrameCount * 256, renderer.NativeSynthesisFrameCountForDiagnostics);
        Assert.Equal(0, renderer.ParallelDecodeAllocatedBytesForDiagnostics);
        Assert.Equal(AudioRenderFaultCode.None, renderer.Fault.Code);
        Assert.Contains(samples, static sample => sample != 0f);
    }

    private static unsafe float[] Render(
        MidiRenderPlan plan,
        int internalBlockFrames,
        int pullBlockFrames,
        out long allocatedBytes,
        int segmentProducerConcurrency = 1)
    {
        BassMidiRendererSettings settings = new(
            BassMidiPolyphonyConfiguration.DefaultMaximumSampleVoicesPerUnitStream,
            maximumWorkFrameCount: internalBlockFrames);
        using BassMidiRenderer renderer = new(
            plan,
            SoundFontPath,
            settings,
            AudioMasterSettings.LimiterV1,
            segmentProducerConcurrency: segmentProducerConcurrency);

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
            BassMidiPolyphonyConfiguration.DefaultMaximumSampleVoicesPerUnitStream,
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

    private static MidiRenderPlan CreateCacheableSingleNotePlan()
    {
        ScheduledMidiMessage[] events =
        [
            new(0, MidiMessage.ProgramChange(0, 0), 0),
            new(256, MidiMessage.NoteOn(0, 60, 80), 0),
            new(2_048, MidiMessage.NoteOff(0, 60, 0), 0)
        ];
        MidiUnitFragmentRenderPlan fragment = new(
            0,
            0,
            trackId: 1,
            segmentId: 2,
            eventInstrumentId: 3,
            instanceGroupId: 4,
            subVoiceId: 5,
            sourceIndex: 0,
            startFrame: 0,
            endFrame: 4_096,
            semanticFingerprint: new string('a', 64),
            events);
        MidiSegmentRenderPlan segment = new(
            trackId: 1,
            segmentId: 2,
            sourceIndex: 0,
            startFrame: 0,
            endFrame: 4_096,
            semanticFingerprint: new string('c', 64));
        return new MidiRenderPlan(
            SampleRate,
            4_096,
            [new MidiPortRenderPlan(0, events)],
            sourceIds: [1],
            unitFragments: [fragment],
            segments: [segment]);
    }

    private static MidiRenderPlan CreateMaximumUnitPlan()
    {
        List<MidiPortRenderPlan> ports = [];
        List<MidiUnitFragmentRenderPlan> fragments = [];
        long[] sourceIds = new long[256];
        for (byte port = 0; port < 16; port++)
        {
            List<ScheduledMidiMessage> portEvents = [];
            for (byte channel = 0; channel < 16; channel++)
            {
                int sourceIndex = (port * 16) + channel;
                sourceIds[sourceIndex] = sourceIndex + 1L;
                byte pitch = checked((byte)(36 + (sourceIndex % 48)));
                ScheduledMidiMessage[] unitEvents =
                [
                    new(0, MidiMessage.ProgramChange(0, 0), sourceIndex),
                    new(0, MidiMessage.NoteOn(0, pitch, 8), sourceIndex),
                    new(512, MidiMessage.NoteOff(0, pitch, 0), sourceIndex)
                ];
                portEvents.Add(new(0, MidiMessage.ProgramChange(channel, 0), sourceIndex));
                portEvents.Add(new(0, MidiMessage.NoteOn(channel, pitch, 8), sourceIndex));
                portEvents.Add(new(512, MidiMessage.NoteOff(channel, pitch, 0), sourceIndex));
                fragments.Add(new(
                    port,
                    channel,
                    trackId: sourceIndex + 1L,
                    segmentId: sourceIndex + 257L,
                    eventInstrumentId: sourceIndex + 513L,
                    instanceGroupId: sourceIndex + 769L,
                    subVoiceId: sourceIndex + 1_025L,
                    sourceIndex,
                    startFrame: 0,
                    endFrame: 1_024,
                    semanticFingerprint: sourceIndex.ToString("x64"),
                    unitEvents));
            }
            ports.Add(new(
                port,
                portEvents.OrderBy(static value => value.SampleFrame).ToArray()));
        }
        return new MidiRenderPlan(
            SampleRate,
            1_024,
            ports.ToArray(),
            sourceIds,
            unitFragments: fragments.ToArray());
    }

    private static unsafe float[] RenderCachePlan(
        MidiRenderPlan plan,
        string cacheStagingPath,
        int maximumSampleVoices,
        out long nativeSynthesisFrames)
    {
        float[] samples = new float[checked((int)plan.TotalFrameCount * 2)];
        using BassMidiRenderer renderer = new(
            plan,
            SoundFontPath,
            new BassMidiRendererSettings(maximumSampleVoices, 256),
            AudioMasterSettings.LimiterV1,
            cacheStagingPath);
        fixed (float* destination = samples)
        {
            int completed = 0;
            long deadline = Environment.TickCount64 + 10_000;
            while (completed < plan.TotalFrameCount)
            {
                int requested = (int)Math.Min(257, plan.TotalFrameCount - completed);
                AudioPullResult result = renderer.PullFrames(destination + (completed * 2), requested);
                Assert.True(result.IsValidForRequest(requested));
                if (result.Status == AudioPullStatus.Buffering)
                {
                    Assert.True(Environment.TickCount64 < deadline, "PCM cache I/O remained Buffering.");
                    Thread.Yield();
                    continue;
                }
                Assert.NotEqual(AudioPullStatus.Fault, result.Status);
                Assert.True(result.FrameCount > 0);
                completed += result.FrameCount;
            }
        }
        renderer.FinalizeCacheCapture();
        Assert.False(renderer.CacheCaptureInvalidated);
        Assert.Equal(AudioRenderFaultCode.None, renderer.Fault.Code);
        nativeSynthesisFrames = renderer.NativeSynthesisFrameCountForDiagnostics;
        return samples;
    }

    private sealed class CacheAccess(AudioCacheSessionStore store) : IAudioPcmCacheSessionAccess
    {
        public AudioCacheSessionSnapshot? AudioCacheSnapshot => store.GetSnapshot();

        public bool TryCopyReusableAudio(
            string key,
            Stream destination,
            out long payloadLength) => store.TryCopyReusable(key, destination, out payloadLength);

        public AudioCachePublishResult PublishReusableAudio(
            string key,
            Stream source,
            long payloadLength) => store.PublishReusable(key, source, payloadLength);

        public void InvalidateReusableAudio(string key) => store.InvalidateReusable(key);

        public AudioCacheSessionStore.AudioRecoverySpool CreateTransientAudioSpool(
            long lengthBytes) => store.CreateRecoverySpool(lengthBytes);

        public void DisableReusableAudioRetention(string reason) =>
            store.DisableReusableRetention(reason);
    }

    private static MidiRenderPlan CreateAllSoundOffComparisonPlan(
        bool includeAllSoundOff,
        bool includeReleaseChannel = true,
        bool includeOtherChannel = false)
    {
        List<ScheduledMidiMessage> events = [];
        if (includeReleaseChannel)
        {
            events.Add(new ScheduledMidiMessage(0, MidiMessage.ProgramChange(0, 0)));
            events.Add(new ScheduledMidiMessage(256, MidiMessage.NoteOn(0, 60, 32)));
            events.Add(new ScheduledMidiMessage(4_800, MidiMessage.NoteOff(0, 60, 0)));
            if (includeAllSoundOff)
            {
                events.Add(new ScheduledMidiMessage(6_000, MidiMessage.ControlChange(0, 120, 0)));
            }
        }

        if (includeOtherChannel)
        {
            events.Add(new ScheduledMidiMessage(0, MidiMessage.ProgramChange(1, 0)));
            events.Add(new ScheduledMidiMessage(256, MidiMessage.NoteOn(1, 67, 32)));
            events.Add(new ScheduledMidiMessage(22_000, MidiMessage.NoteOff(1, 67, 0)));
        }

        return new MidiRenderPlan(
            SampleRate,
            24_000,
            [new MidiPortRenderPlan(0, events.OrderBy(static item => item.SampleFrame).ToArray())]);
    }

    private static float MaximumAbsoluteSample(ReadOnlySpan<float> samples)
    {
        float maximum = 0f;
        foreach (float sample in samples)
        {
            maximum = Math.Max(maximum, Math.Abs(sample));
        }

        return maximum;
    }

    private static float MaximumAbsoluteDifference(
        ReadOnlySpan<byte> firstBytes,
        ReadOnlySpan<byte> secondBytes)
    {
        ReadOnlySpan<float> first = MemoryMarshal.Cast<byte, float>(firstBytes);
        ReadOnlySpan<float> second = MemoryMarshal.Cast<byte, float>(secondBytes);
        float maximum = 0f;
        for (int index = 0; index < first.Length; index++)
        {
            maximum = Math.Max(maximum, Math.Abs(first[index] - second[index]));
        }

        return maximum;
    }

    private static long LastNonZeroFrame(ReadOnlySpan<float> samples)
    {
        for (int sampleIndex = samples.Length - 1; sampleIndex >= 0; sampleIndex--)
        {
            if (samples[sampleIndex] != 0f)
            {
                return sampleIndex / 2;
            }
        }

        return -1;
    }

    private static MidiRenderPlan CreateComplexTempoLoopPlan()
    {
        List<ScheduledMidiMessage> events = [];
        AddInitialState(events, 0, 0);
        AddAlternatingNotes(events, 0, 6_000, 90_000, 6_000, 5_000, 48, 55);
        AddNote(events, 0, 96_000, 6_667, 55, 84);
        AddAlternatingNotes(events, 0, 104_000, 120_000, 8_000, 6_667, 48, 55);

        AddInitialState(events, 1, 128_000);
        AddAlternatingNotes(events, 1, 136_000, 184_000, 8_000, 6_667, 53, 60);
        AddNote(events, 1, 192_000, 4_000, 60, 84);
        AddAlternatingNotes(events, 1, 196_800, 244_800, 4_800, 4_000, 53, 60);
        AddHardEndState(events, 1, 249_600);
        return new MidiRenderPlan(
            SampleRate,
            249_600,
            [new MidiPortRenderPlan(0, events.ToArray())]);

        static void AddAlternatingNotes(
            List<ScheduledMidiMessage> destination,
            byte channel,
            long firstFrame,
            long lastFrame,
            long spacingFrames,
            long lengthFrames,
            byte firstNote,
            byte secondNote)
        {
            int index = 0;
            for (long frame = firstFrame; frame <= lastFrame; frame += spacingFrames, index++)
            {
                bool first = (index & 1) == 0;
                AddNote(
                    destination,
                    channel,
                    frame,
                    lengthFrames,
                    first ? firstNote : secondNote,
                    first ? (byte)92 : (byte)84);
            }
        }

        static void AddNote(
            List<ScheduledMidiMessage> destination,
            byte channel,
            long frame,
            long lengthFrames,
            byte note,
            byte velocity)
        {
            destination.Add(new ScheduledMidiMessage(frame, MidiMessage.NoteOn(channel, note, velocity)));
            destination.Add(new ScheduledMidiMessage(
                frame + lengthFrames,
                MidiMessage.NoteOff(channel, note, 0)));
        }

        static void AddInitialState(
            List<ScheduledMidiMessage> destination,
            byte channel,
            long frame)
        {
            destination.Add(new ScheduledMidiMessage(frame, MidiMessage.ControlChange(channel, 0, 0)));
            destination.Add(new ScheduledMidiMessage(frame, MidiMessage.ControlChange(channel, 32, 0)));
            destination.Add(new ScheduledMidiMessage(frame, MidiMessage.ProgramChange(channel, 0)));
            destination.Add(new ScheduledMidiMessage(frame, MidiMessage.ControlChange(channel, 101, 0)));
            destination.Add(new ScheduledMidiMessage(frame, MidiMessage.ControlChange(channel, 100, 0)));
            destination.Add(new ScheduledMidiMessage(frame, MidiMessage.ControlChange(channel, 6, 2)));
            destination.Add(new ScheduledMidiMessage(frame, MidiMessage.ControlChange(channel, 38, 0)));
            destination.Add(new ScheduledMidiMessage(frame, MidiMessage.ControlChange(channel, 101, 127)));
            destination.Add(new ScheduledMidiMessage(frame, MidiMessage.ControlChange(channel, 100, 127)));
            destination.Add(new ScheduledMidiMessage(frame, MidiMessage.PitchWheelChange(channel, 8_192)));
        }

        static void AddHardEndState(
            List<ScheduledMidiMessage> destination,
            byte channel,
            long frame)
        {
            destination.Add(new ScheduledMidiMessage(frame, MidiMessage.ControlChange(channel, 0, 0)));
            destination.Add(new ScheduledMidiMessage(frame, MidiMessage.ControlChange(channel, 32, 0)));
            destination.Add(new ScheduledMidiMessage(frame, MidiMessage.ProgramChange(channel, 0)));
            destination.Add(new ScheduledMidiMessage(frame, MidiMessage.PitchWheelChange(channel, 8_192)));
            destination.Add(new ScheduledMidiMessage(frame, MidiMessage.ControlChange(channel, 101, 0)));
            destination.Add(new ScheduledMidiMessage(frame, MidiMessage.ControlChange(channel, 100, 0)));
            destination.Add(new ScheduledMidiMessage(frame, MidiMessage.ControlChange(channel, 6, 2)));
            destination.Add(new ScheduledMidiMessage(frame, MidiMessage.ControlChange(channel, 38, 0)));
            destination.Add(new ScheduledMidiMessage(frame, MidiMessage.ControlChange(channel, 101, 127)));
            destination.Add(new ScheduledMidiMessage(frame, MidiMessage.ControlChange(channel, 100, 127)));
        }
    }

    private static void EnsureEnvironment()
    {
        NativeAudioIntegrationEnvironment.LoadBassMidi();
        _ = SoundFontPath;
    }
}
