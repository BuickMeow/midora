using Midora.Midi;

namespace Midora.Audio.Bass.Tests;

public sealed class MidiRenderPlanTests
{
    [Fact]
    public void RejectsForbiddenEffectsControllers()
    {
        ScheduledMidiMessage[] cc91 = [new(0, MidiMessage.ControlChange(0, 91, 1))];
        ScheduledMidiMessage[] cc93 = [new(0, MidiMessage.ControlChange(0, 93, 1))];

        _ = Assert.Throws<ArgumentException>(() => new MidiPortRenderPlan(0, cc91));
        _ = Assert.Throws<ArgumentException>(() => new MidiPortRenderPlan(0, cc93));
    }

    [Fact]
    public void PreservesSameFrameEventOrder()
    {
        ScheduledMidiMessage[] events =
        [
            new(10, MidiMessage.ControlChange(0, 0, 1)),
            new(10, MidiMessage.ProgramChange(0, 7)),
            new(10, MidiMessage.NoteOn(0, 60, 100))
        ];

        MidiPortRenderPlan plan = new(0, events);

        Assert.Equal(0, plan.Events[0].Message.Byte1);
        Assert.Equal(7, plan.Events[1].Message.Byte1);
        Assert.Equal(60, plan.Events[2].Message.Byte1);
    }

    [Fact]
    public void ChannelModeSystemExclusiveCarrierDoesNotChangeReferencedPresetState()
    {
        MidiChannelModeSystemExclusive systemExclusive = new(
            MidiChannelModeSystemExclusiveKind.YamahaXgPartMode,
            TargetChannel: 0,
            DeviceId: 0x10,
            ModeValue: 0);
        MidiRenderPlan plan = new(
            48_000,
            100,
            [
                new MidiPortRenderPlan(
                    0,
                    [
                        new(0, MidiMessage.ControlChange(0, 0, 1)),
                        new(0, MidiMessage.ProgramChange(0, 5)),
                        ScheduledMidiMessage.CreateChannelModeSystemExclusive(0, 0, systemExclusive),
                        new(10, MidiMessage.NoteOn(0, 60, 100))
                    ])
            ]);

        Assert.Contains((1 << 7) | 5, plan.ReferencedPresetKeys.ToArray());
    }

    [Fact]
    public void DerivesOneChannelZeroStreamPlanPerActuallyUsedCanonicalUnit()
    {
        MidiRenderPlan plan = new(
            48_000,
            100,
            [
                new MidiPortRenderPlan(
                    0,
                    [
                        new(0, MidiMessage.ProgramChange(7, 9), 1),
                        new(10, MidiMessage.NoteOn(2, 60, 100), 0),
                        new(20, MidiMessage.NoteOff(2, 60), 0),
                        new(30, MidiMessage.NoteOn(7, 64, 90), 1)
                    ]),
                new MidiPortRenderPlan(
                    3,
                    [new(40, MidiMessage.NoteOn(5, 67, 80), 0)])
            ],
            [101, 102]);

        Assert.Equal(3, plan.Units.Length);
        Assert.Equal([2, 7, 53], plan.Units.ToArray().Select(value => value.CanonicalUnitNumber));
        Assert.Equal([10L, 20L], plan.Units[0].Events.ToArray().Select(value => value.SampleFrame));
        Assert.Equal([0L, 30L], plan.Units[1].Events.ToArray().Select(value => value.SampleFrame));
        Assert.All(
            plan.Units.ToArray().SelectMany(value => value.Events.ToArray()),
            scheduled => Assert.Equal(0, scheduled.Message.ChannelNumber));
        Assert.Equal([0, 0], plan.Units[0].Events.ToArray().Select(value => value.SourceIndex));
        Assert.Equal([1, 1], plan.Units[1].Events.ToArray().Select(value => value.SourceIndex));
    }

    [Fact]
    public void RejectsUnsortedEventsAndPorts()
    {
        ScheduledMidiMessage[] events =
        [
            new(20, MidiMessage.NoteOn(0, 60, 100)),
            new(10, MidiMessage.NoteOff(0, 60))
        ];

        _ = Assert.Throws<ArgumentException>(() => new MidiPortRenderPlan(0, events));

        MidiPortRenderPlan port1 = new(1, ReadOnlySpan<ScheduledMidiMessage>.Empty);
        MidiPortRenderPlan port0 = new(0, ReadOnlySpan<ScheduledMidiMessage>.Empty);
        _ = Assert.Throws<ArgumentException>(() => new MidiRenderPlan(48_000, 100, [port1, port0]));
    }

    [Fact]
    public void SourceIdsArePositiveInt64AndUnique()
    {
        MidiRenderPlan plan = new(48_000, 0, [], [1, long.MaxValue]);

        Assert.Equal(0, plan.FindSourceIndex(1));
        Assert.Equal(1, plan.FindSourceIndex(long.MaxValue));
        Assert.Equal(-1, plan.FindSourceIndex(2));
        Assert.Throws<ArgumentException>(() => new MidiRenderPlan(48_000, 0, [], [0]));
        Assert.Throws<ArgumentException>(() => new MidiRenderPlan(48_000, 0, [], [-1]));
        Assert.Throws<ArgumentException>(() => new MidiRenderPlan(48_000, 0, [], [1, 1]));
    }

    [Fact]
    public void HeldPreviewSpliceKeepsOnlyTheRenderedPrefixAndReplacementFuture()
    {
        MidiRenderPlan causal = new(
            48_000,
            1_000,
            [
                new MidiPortRenderPlan(
                    0,
                    [
                        new(100, MidiMessage.NoteOn(0, 60, 100), 0),
                        new(500, MidiMessage.NoteOff(0, 60), 0),
                        new(700, MidiMessage.NoteOn(0, 62, 100), 0)
                    ])
            ],
            [101],
            [0]);
        MidiRenderPlan continuation = new(
            48_000,
            1_200,
            [
                new MidiPortRenderPlan(
                    0,
                    [
                        new(100, MidiMessage.NoteOn(0, 64, 100), 0),
                        new(500, MidiMessage.NoteOff(0, 64), 0),
                        new(650, MidiMessage.NoteOn(0, 67, 90), 0),
                        new(900, MidiMessage.NoteOff(0, 67), 0)
                    ])
            ],
            [101],
            [0]);

        MidiRenderPlan result = MidiRenderPlanSplicer.SpliceAtProducerFrontier(
            causal,
            continuation,
            500);

        Assert.Equal(1_200, result.TotalFrameCount);
        Assert.Equal([100L, 500L, 650L, 900L], result.Ports[0].Events.ToArray().Select(x => x.SampleFrame));
        Assert.Equal(60, result.Ports[0].Events[0].Message.Byte1);
        Assert.Equal(64, result.Ports[0].Events[1].Message.Byte1);
        Assert.Equal(67, result.Ports[0].Events[2].Message.Byte1);
        Assert.Equal([101L], result.SourceIds.ToArray());
        Assert.Equal([0], result.InitiallyDisabledSourceIndices.ToArray());
    }

    [Fact]
    public void HeldPreviewSpliceRejectsAnInvalidFrontierOrIncompatiblePlans()
    {
        MidiRenderPlan causal = new(
            48_000,
            1_000,
            [new MidiPortRenderPlan(0, [])],
            [101]);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => MidiRenderPlanSplicer.SpliceAtProducerFrontier(causal, causal, -1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MidiRenderPlanSplicer.SpliceAtProducerFrontier(causal, causal, 1_001));
        Assert.Throws<ArgumentException>(
            () => MidiRenderPlanSplicer.SpliceAtProducerFrontier(
                causal,
                new MidiRenderPlan(44_100, 1_000, [new MidiPortRenderPlan(0, [])], [101]),
                0));
        Assert.Throws<ArgumentException>(
            () => MidiRenderPlanSplicer.SpliceAtProducerFrontier(
                causal,
                new MidiRenderPlan(48_000, 1_000, [new MidiPortRenderPlan(0, [])], [102]),
                0));
        Assert.Throws<ArgumentException>(
            () => MidiRenderPlanSplicer.SpliceAtProducerFrontier(
                causal,
                new MidiRenderPlan(48_000, 1_000, [new MidiPortRenderPlan(1, [])], [101]),
                0));
    }

    [Fact]
    public void EventLowerBoundReturnsTheFirstEventAtOrAfterTheFrame()
    {
        ScheduledMidiMessage[] events =
        [
            new(10, MidiMessage.NoteOn(0, 60, 100)),
            new(20, MidiMessage.NoteOn(0, 61, 100)),
            new(20, MidiMessage.NoteOn(0, 62, 100)),
            new(30, MidiMessage.NoteOn(0, 63, 100))
        ];

        Assert.Equal(0, MidiRenderPlanSplicer.FindFirstEventAtOrAfter(events, 0));
        Assert.Equal(1, MidiRenderPlanSplicer.FindFirstEventAtOrAfter(events, 20));
        Assert.Equal(3, MidiRenderPlanSplicer.FindFirstEventAtOrAfter(events, 21));
        Assert.Equal(4, MidiRenderPlanSplicer.FindFirstEventAtOrAfter(events, 31));
    }

    [Fact]
    public void HeldGateEndAddsOneExactFrontierNoteOffPerCausallyActiveInstance()
    {
        MidiRenderPlan causal = new(
            48_000,
            1_000,
            [
                new MidiPortRenderPlan(
                    0,
                    [
                        new(10, MidiMessage.NoteOn(0, 60, 100)),
                        new(20, MidiMessage.NoteOn(0, 60, 90)),
                        new(30, MidiMessage.NoteOff(0, 60, 0)),
                        new(40, MidiMessage.NoteOn(2, 67, 80)),
                        new(700, MidiMessage.NoteOff(2, 67, 0))
                    ])
            ]);
        MidiRenderPlan continuation = new(
            48_000,
            1_200,
            [
                new MidiPortRenderPlan(
                    0,
                    [
                        new(500, MidiMessage.ControlChange(0, 11, 64)),
                        new(800, MidiMessage.NoteOff(0, 72, 0))
                    ])
            ]);

        MidiRenderPlan result = MidiRenderPlanSplicer.SpliceHeldGateEndAtProducerFrontier(
            causal,
            continuation,
            500);

        ScheduledMidiMessage[] atFrontier = result.Ports[0].Events.ToArray()
            .Where(value => value.SampleFrame == 500)
            .ToArray();
        Assert.Equal(3, atFrontier.Length);
        Assert.Equal(MidiMessageType.NoteOff, atFrontier[0].Message.MessageType);
        Assert.Equal(0, atFrontier[0].Message.ChannelNumber);
        Assert.Equal(60, atFrontier[0].Message.Byte1);
        Assert.Equal(MidiMessageType.NoteOff, atFrontier[1].Message.MessageType);
        Assert.Equal(2, atFrontier[1].Message.ChannelNumber);
        Assert.Equal(67, atFrontier[1].Message.Byte1);
        Assert.Equal(MidiMessageType.ControlChange, atFrontier[2].Message.MessageType);
        Assert.DoesNotContain(result.Ports[0].Events.ToArray(), value => value.SampleFrame == 700);
    }

    [Fact]
    public void HeldGateEndReusesAContinuationNoteOffAndPreservesItsUnitFragments()
    {
        MidiRenderPlan causal = new(
            48_000,
            1_000,
            [new MidiPortRenderPlan(
                0,
                [
                    new(10, MidiMessage.NoteOn(0, 60, 100), 0),
                    new(700, MidiMessage.NoteOff(0, 60, 0), 0)
                ])],
            [101]);
        MidiUnitFragmentRenderPlan continuationFragment = new(
            0,
            0,
            trackId: 1,
            segmentId: 2,
            eventInstrumentId: 3,
            instanceGroupId: 4,
            subVoiceId: 5,
            sourceIndex: 0,
            startFrame: 0,
            endFrame: 1_200,
            semanticFingerprint: new string('a', 64),
            [
                new(10, MidiMessage.NoteOn(0, 60, 100), 0),
                new(500, MidiMessage.NoteOff(0, 60, 0), 0),
                new(500, MidiMessage.ControlChange(0, 120, 0), 0)
            ]);
        MidiRenderPlan continuation = new(
            48_000,
            1_200,
            [new MidiPortRenderPlan(
                0,
                [
                    new(10, MidiMessage.NoteOn(0, 60, 100), 0),
                    new(500, MidiMessage.NoteOff(0, 60, 0), 0),
                    new(500, MidiMessage.ControlChange(0, 120, 0), 0)
                ])],
            [101],
            unitFragments: [continuationFragment]);

        MidiRenderPlan result = MidiRenderPlanSplicer.SpliceHeldGateEndAtProducerFrontier(
            causal,
            continuation,
            500);

        Assert.Single(result.Ports[0].Events.ToArray(), value =>
            value.SampleFrame == 500
            && value.Message.MessageType == MidiMessageType.NoteOff
            && value.Message.Byte1 == 60);
        Assert.Same(continuationFragment, Assert.Single(result.UnitFragments.ToArray()));
    }
}
