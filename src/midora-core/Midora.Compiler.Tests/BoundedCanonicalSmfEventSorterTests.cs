using Midora.Domain;
using Midora.Midi;

namespace Midora.Compiler.Tests;

public sealed class BoundedCanonicalSmfEventSorterTests
{
    [Fact]
    public void ReadPagesMergesMultipleDiskRunsInCanonicalSmfOrder()
    {
        const int recordCount = 300_000;
        using BoundedCanonicalSmfEventSorter sorter = new();
        for (int index = recordCount; index >= 1; index--)
        {
            long tick = index / 5;
            long order = index % 5;
            sorter.Add(new(
                MidoraId.FromSequence(1),
                tick,
                0,
                0,
                MidiMessage.ControlChange(0, 1, checked((byte)(index % 128))),
                CanonicalEventRole.DirectMidi,
                order,
                MidoraId.FromSequence(index)));
        }

        CanonicalSmfTrackChannelEvent? previous = null;
        int actualCount = 0;
        foreach (CanonicalSmfTrackChannelEvent value in sorter
            .ReadPages(CancellationToken.None)
            .SelectMany(page => page.Items))
        {
            if (previous is CanonicalSmfTrackChannelEvent prior)
            {
                Assert.True(
                    prior.Tick < value.Tick
                    || prior.Tick == value.Tick && prior.EventOrder < value.EventOrder
                    || prior.Tick == value.Tick
                        && prior.EventOrder == value.EventOrder
                        && prior.SourceObjectId.CompareTo(value.SourceObjectId) < 0);
            }
            previous = value;
            actualCount++;
        }

        Assert.Equal(recordCount, actualCount);
    }

    [Fact]
    public void SmfSorterBoundsFanInWithMultiPassMerge()
    {
        using BoundedCanonicalSmfEventSorter sorter = new(
            sortRunRecordCount: 3,
            maximumMergeFanIn: 2);
        for (int index = 20; index >= 1; index--)
        {
            sorter.Add(new(
                MidoraId.FromSequence(1),
                index,
                0,
                0,
                MidiMessage.ProgramChange(0, checked((byte)index)),
                CanonicalEventRole.DirectMidi,
                0,
                MidoraId.FromSequence(index)));
        }

        Assert.Equal(
            Enumerable.Range(1, 20).Select(value => (long)value),
            sorter.ReadPages(CancellationToken.None)
                .SelectMany(page => page.Items)
                .Select(value => value.Tick));
    }

    [Fact]
    public void OpaqueSorterUsesBoundedMultiPassMergeWithoutLosingPayloadOrSource()
    {
        using BoundedCanonicalOpaqueEventSorter sorter = new(
            maximumRunRecordCount: 3,
            maximumRunPayloadByteCount: 16,
            maximumMergeFanIn: 2);
        for (int index = 20; index >= 1; index--)
        {
            MidoraId id = MidoraId.FromSequence(index);
            sorter.Add(new(
                MidoraId.FromSequence(100),
                index / 2,
                OpaqueMidiEventKind.Meta,
                1,
                new byte[] { checked((byte)index), 0x5a },
                index % 2,
                new SourceReference(
                    TrackId: MidoraId.FromSequence(200),
                    SegmentId: MidoraId.FromSequence(300),
                    SourceEventId: id,
                    Tick: index / 2,
                    Origin: SourceOrigin.OpaqueMidiEvent,
                    MidiChannelRootId: MidoraId.FromSequence(400),
                    PureMidiTrackId: MidoraId.FromSequence(200),
                    MidiSegmentId: MidoraId.FromSequence(300),
                    DirectMidiObjectId: id,
                    ExportTrackId: MidoraId.FromSequence(100)),
                7));
        }

        CanonicalOpaqueMidiEvent[] values = sorter
            .ReadPages(CancellationToken.None)
            .SelectMany(page => page.Items)
            .ToArray();

        Assert.Equal(20, values.Length);
        Assert.Equal(Enumerable.Range(1, 20).Select(value => (byte)value),
            values.Select(value => value.Payload.Span[0]));
        Assert.All(values, value =>
        {
            Assert.Equal(MidoraId.FromSequence(200), value.Source.TrackId);
            Assert.Equal(MidoraId.FromSequence(300), value.Source.MidiSegmentId);
            Assert.Equal(SourceOrigin.OpaqueMidiEvent, value.Source.Origin);
        });
    }

    [Fact]
    public void MidiRenderSorterMergesMultipleRunsWithoutLosingMonitoringIdentity()
    {
        const int recordCount = 300_000;
        MidoraId trackId = MidoraId.FromSequence(10);
        using BoundedCanonicalMidiRenderEventSorter sorter = new();
        for (int index = recordCount; index >= 1; index--)
        {
            MidoraId objectId = MidoraId.FromSequence(index);
            sorter.Add(new(
                index / 4,
                0,
                0,
                MidiMessage.NoteOn(0, checked((byte)(index % 128)), 100),
                CanonicalEventRole.DirectMidi,
                index % 4,
                long.MinValue,
                index % 4,
                new SourceReference(
                    TrackId: trackId,
                    SegmentId: MidoraId.FromSequence(20),
                    Tick: index / 4,
                    Origin: SourceOrigin.DirectMidiNote,
                    DirectMidiObjectId: objectId),
                trackId,
                0,
                index % 4));
        }

        long previousTick = long.MinValue;
        int actualCount = 0;
        foreach (CanonicalMidiRenderEvent value in sorter
            .ReadPages(CancellationToken.None)
            .SelectMany(page => page.Items))
        {
            Assert.True(value.Tick >= previousTick);
            Assert.Equal(trackId, value.TrackId);
            Assert.Equal(trackId, value.MonitoringSourceId);
            previousTick = value.Tick;
            actualCount++;
        }
        Assert.Equal(recordCount, actualCount);
    }

    [Fact]
    public void MidiRenderSorterBoundsFanInWithMultiPassMerge()
    {
        MidoraId trackId = MidoraId.FromSequence(10);
        using BoundedCanonicalMidiRenderEventSorter sorter = new(
            sortRunRecordCount: 3,
            maximumMergeFanIn: 2);
        for (int index = 20; index >= 1; index--)
        {
            sorter.Add(new(
                index,
                0,
                0,
                MidiMessage.ProgramChange(0, checked((byte)index)),
                CanonicalEventRole.DirectMidi,
                0,
                long.MinValue,
                0,
                new SourceReference(
                    TrackId: trackId,
                    SegmentId: MidoraId.FromSequence(20),
                    Origin: SourceOrigin.DirectMidiChannelEvent,
                    DirectMidiObjectId: MidoraId.FromSequence(index)),
                trackId,
                0,
                0));
        }

        Assert.Equal(
            Enumerable.Range(1, 20).Select(value => (long)value),
            sorter.ReadPages(CancellationToken.None)
                .SelectMany(page => page.Items)
                .Select(value => value.Tick));
    }
}
