using Midora.Audio;
using Midora.Midi;
using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;

namespace Midora.Audio.Bass.Tests;

[SupportedOSPlatform("windows")]
public sealed class SharedAudioWorkerControlTests
{
    [Fact]
    public void CurrentRealtimeControlAbiIsVersionSix()
    {
        Assert.Equal(6, SharedAudioWorkerControl.ProtocolVersion);
    }

    [Fact]
    public void OutputDeviceUnavailableIsAValidNonFaultTerminalStatus()
    {
        string name = $"Midora.Audio.Control.Tests.{Guid.NewGuid():N}";
        using SharedAudioWorkerControl producer = SharedAudioWorkerControl.Create(name);
        using SharedAudioWorkerControl consumer = SharedAudioWorkerControl.Open(name);

        producer.PublishRuntimeStatus(
            AudioWorkerState.OutputDeviceUnavailable,
            10,
            20,
            0,
            0,
            0);

        AudioWorkerStatus status = consumer.ReadStatus();
        Assert.Equal(AudioWorkerState.OutputDeviceUnavailable, status.State);
        Assert.Equal(0, status.FaultCode);
        Assert.Equal(10, status.PositionFrame);
        Assert.Equal(20, status.RenderPositionFrame);
    }

    [Fact]
    public void FixedSharedMemoryAbiTransfersStatusAndCommands()
    {
        string name = $"Midora.Audio.Control.Test.{Guid.NewGuid():N}";
        using SharedAudioWorkerControl producer = SharedAudioWorkerControl.Create(name);
        using SharedAudioWorkerControl consumer = SharedAudioWorkerControl.Open(name);

        consumer.PublishPrepared(48_000, 2_400);
        AudioWorkerStatus status = producer.ReadStatus();
        Assert.Equal(AudioWorkerState.Prepared, status.State);
        Assert.Equal(48_000, status.ActualSampleRate);
        Assert.Equal(2_400, status.ActualDeviceBufferFrameCount);

        MidiMonitoringCommand[] expected =
        [
            MidiMonitoringCommand.EnableSource(7),
            MidiMonitoringCommand.Send(3, MidiMessage.NoteOn(2, 64, 100))
        ];
        Assert.True(producer.TryEnqueueMonitoringCommands(expected));
        foreach (MidiMonitoringCommand value in expected)
        {
            Assert.True(consumer.TryDequeue(out AudioWorkerControlCommand command));
            Assert.Equal(AudioWorkerControlCommandKind.Monitoring, command.Kind);
            Assert.Equal(value, command.MonitoringCommand);
        }
        Assert.False(consumer.TryDequeue(out _));
        Assert.True(producer.TryEnqueueStop(flush: false));
        Assert.True(consumer.TryDequeue(out AudioWorkerControlCommand stop));
        Assert.Equal(AudioWorkerControlCommandKind.Stop, stop.Kind);
        Assert.False(stop.MonitoringCommand.SourceEnabled);

        Assert.True(producer.TryEnqueueHeldPreviewPause());
        Assert.True(producer.TryEnqueueHeldPreviewApplyPlan(7));
        Assert.True(producer.TryEnqueueHeldPreviewResume());
        Assert.True(producer.TryEnqueueBufferingRecovery(12_345));
        Assert.True(consumer.TryDequeue(out AudioWorkerControlCommand pause));
        Assert.Equal(AudioWorkerControlCommandKind.HeldPreviewPause, pause.Kind);
        Assert.Equal(0, pause.Payload);
        Assert.True(consumer.TryDequeue(out AudioWorkerControlCommand apply));
        Assert.Equal(AudioWorkerControlCommandKind.HeldPreviewApplyPlan, apply.Kind);
        Assert.Equal(7, apply.Payload);
        Assert.True(consumer.TryDequeue(out AudioWorkerControlCommand resume));
        Assert.Equal(AudioWorkerControlCommandKind.HeldPreviewResume, resume.Kind);
        Assert.Equal(0, resume.Payload);
        Assert.True(consumer.TryDequeue(out AudioWorkerControlCommand recovery));
        Assert.Equal(AudioWorkerControlCommandKind.BufferingRecoveryPrepare, recovery.Kind);
        Assert.Equal(12_345, recovery.Payload);

        Assert.True(producer.TryEnqueuePersistentProbe(101));
        Assert.True(producer.TryEnqueuePersistentStartPlayback(102));
        Assert.True(producer.TryEnqueuePitchAuditionNoteOn(103));
        Assert.True(producer.TryEnqueuePitchAuditionNoteOff(104));
        Assert.True(producer.TryEnqueuePersistentShutdown(105));
        AssertPersistentCommand(
            consumer,
            AudioWorkerControlCommandKind.PersistentProbe,
            101);
        AssertPersistentCommand(
            consumer,
            AudioWorkerControlCommandKind.PersistentStartPlayback,
            102);
        AssertPersistentCommand(
            consumer,
            AudioWorkerControlCommandKind.PitchAuditionNoteOn,
            103);
        AssertPersistentCommand(
            consumer,
            AudioWorkerControlCommandKind.PitchAuditionNoteOff,
            104);
        AssertPersistentCommand(
            consumer,
            AudioWorkerControlCommandKind.PersistentShutdown,
            105);
    }

    private static void AssertPersistentCommand(
        SharedAudioWorkerControl consumer,
        AudioWorkerControlCommandKind expectedKind,
        long expectedGeneration)
    {
        Assert.True(consumer.TryDequeue(out AudioWorkerControlCommand command));
        Assert.Equal(expectedKind, command.Kind);
        Assert.Equal(expectedGeneration, command.Payload);
    }

    [Fact]
    public void MonitoringBatchDequeueStopsAtTheNextControlCommand()
    {
        string name = $"Midora.Audio.Control.Test.{Guid.NewGuid():N}";
        using SharedAudioWorkerControl producer = SharedAudioWorkerControl.Create(name);
        using SharedAudioWorkerControl consumer = SharedAudioWorkerControl.Open(name);
        MidiMonitoringCommand[] expected =
        [
            MidiMonitoringCommand.DisableSource(1),
            MidiMonitoringCommand.Send(0, MidiMessage.ControlChange(0, 123, 0)),
            MidiMonitoringCommand.EnableSource(1)
        ];
        Assert.True(producer.TryEnqueueMonitoringCommands(expected));
        Assert.True(producer.TryEnqueueBufferingRecovery(9_999));

        Span<MidiMonitoringCommand> actual = stackalloc MidiMonitoringCommand[8];
        Assert.True(consumer.TryDequeueMonitoringCommands(actual, out int commandCount));

        Assert.Equal(expected.Length, commandCount);
        Assert.True(expected.AsSpan().SequenceEqual(actual[..commandCount]));
        Assert.True(consumer.TryDequeue(out AudioWorkerControlCommand recovery));
        Assert.Equal(AudioWorkerControlCommandKind.BufferingRecoveryPrepare, recovery.Kind);
        Assert.Equal(9_999, recovery.Payload);
    }

    [Fact]
    public void PendingStopTakesPriorityOverOlderMonitoringCommands()
    {
        string name = $"Midora.Audio.Control.Test.{Guid.NewGuid():N}";
        using SharedAudioWorkerControl producer = SharedAudioWorkerControl.Create(name);
        using SharedAudioWorkerControl consumer = SharedAudioWorkerControl.Open(name);
        Assert.True(producer.TryEnqueueMonitoringCommands(
        [
            MidiMonitoringCommand.DisableSource(0),
            MidiMonitoringCommand.Send(0, MidiMessage.ControlChange(0, 123, 0))
        ]));
        Assert.True(producer.TryEnqueueStop(flush: false));

        Assert.True(consumer.HasPendingStopCommand());
        Assert.True(consumer.TryDequeuePendingStop(out bool flush));
        Assert.False(flush);
        Assert.False(consumer.HasPendingStopCommand());
        Assert.False(consumer.TryDequeue(out _));
    }

    [Fact]
    public void HeldPreviewStatusPublishesFrontierAndAcknowledgedPlanGenerationAtomically()
    {
        string name = $"Midora.Audio.Control.Test.{Guid.NewGuid():N}";
        using SharedAudioWorkerControl producer = SharedAudioWorkerControl.Create(name);
        using SharedAudioWorkerControl consumer = SharedAudioWorkerControl.Open(name);

        producer.PublishHeldPreviewStatus(
            AudioWorkerState.HeldPreviewPaused,
            positionFrame: 10_000,
            producerFrontierFrame: 12_345,
            underrunCount: 1,
            callbackAllocatedBytes: 2,
            renderingAllocatedBytes: 3,
            planGeneration: 9);

        AudioWorkerStatus status = consumer.ReadStatus();
        Assert.Equal(AudioWorkerState.HeldPreviewPaused, status.State);
        Assert.Equal(10_000, status.PositionFrame);
        Assert.Equal(12_345, status.RenderPositionFrame);
        Assert.Equal(1, status.UnderrunCount);
        Assert.Equal(2, status.CallbackAllocatedBytes);
        Assert.Equal(3, status.RenderingAllocatedBytes);
        Assert.Equal(9, status.HeldPreviewPlanGeneration);
    }

    [Fact]
    public void PersistentPlaybackAcceptancePublishesGenerationAndPreparingAtomically()
    {
        string name = $"Midora.Audio.Control.Test.{Guid.NewGuid():N}";
        using SharedAudioWorkerControl producer = SharedAudioWorkerControl.Create(name);
        using SharedAudioWorkerControl consumer = SharedAudioWorkerControl.Open(name);

        producer.PublishPersistentPlaybackAcceptance(37);

        AudioWorkerStatus status = consumer.ReadStatus();
        Assert.Equal(AudioWorkerState.Preparing, status.State);
        Assert.Equal(37, status.PersistentPlaybackAcceptedGeneration);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, SharedAudioWorkerControl.ProtocolVersion + 1)]
    [InlineData(8, SharedAudioWorkerControl.CommandCapacity - 1)]
    [InlineData(104, 1)]
    public void OpenRejectsCorruptFixedHeaderAndReservedFields(int offset, int value)
    {
        string name = $"Midora.Audio.Control.Test.{Guid.NewGuid():N}";
        using SharedAudioWorkerControl owner = SharedAudioWorkerControl.Create(name);
        using MemoryMappedFile mapping = MemoryMappedFile.OpenExisting(
            name,
            MemoryMappedFileRights.ReadWrite);
        using MemoryMappedViewAccessor view = mapping.CreateViewAccessor();
        view.Write(offset, value);

        Assert.Throws<InvalidDataException>(() => SharedAudioWorkerControl.Open(name));
    }

    [Fact]
    public void CommandRingAppliesBoundedBackpressureWithoutPartialBatch()
    {
        string name = $"Midora.Audio.Control.Test.{Guid.NewGuid():N}";
        using SharedAudioWorkerControl producer = SharedAudioWorkerControl.Create(name);
        using SharedAudioWorkerControl consumer = SharedAudioWorkerControl.Open(name);
        MidiMonitoringCommand[] full = new MidiMonitoringCommand[
            SharedAudioWorkerControl.CommandCapacity];
        Array.Fill(full, MidiMonitoringCommand.EnableSource(0));

        Assert.True(producer.TryEnqueueMonitoringCommands(full));
        Assert.False(producer.TryEnqueueMonitoringCommands(full.AsSpan(0, 1)));
        for (int i = 0; i < full.Length; i++)
        {
            Assert.True(consumer.TryDequeue(out AudioWorkerControlCommand command));
            Assert.Equal(AudioWorkerControlCommandKind.Monitoring, command.Kind);
        }
        Assert.False(consumer.TryDequeue(out _));
    }

    [Fact]
    public void RuntimeCommandTransferAllocatesNoManagedMemoryAfterWarmup()
    {
        string name = $"Midora.Audio.Control.Test.{Guid.NewGuid():N}";
        using SharedAudioWorkerControl producer = SharedAudioWorkerControl.Create(name);
        using SharedAudioWorkerControl consumer = SharedAudioWorkerControl.Open(name);
        MidiMonitoringCommand command = MidiMonitoringCommand.Send(
            0,
            MidiMessage.ControlChange(0, 7, 100));
        ReadOnlySpan<MidiMonitoringCommand> commands =
            System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref command, 1);

        Assert.True(producer.TryEnqueueMonitoringCommands(commands));
        Assert.True(consumer.TryDequeue(out _));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            if (!producer.TryEnqueueMonitoringCommands(commands)
                || !consumer.TryDequeue(out AudioWorkerControlCommand actual)
                || actual.Kind != AudioWorkerControlCommandKind.Monitoring
                || actual.MonitoringCommand.Kind != command.Kind
                || actual.MonitoringCommand.SourceIndex != command.SourceIndex
                || actual.MonitoringCommand.ZeroBasedPortNumber != command.ZeroBasedPortNumber
                || actual.MonitoringCommand.SourceEnabled != command.SourceEnabled
                || actual.MonitoringCommand.Message.PackedValue != command.Message.PackedValue)
            {
                throw new InvalidDataException("Shared command payload changed during transfer.");
            }
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void InvalidMonitoringBatchIsRejectedBeforePublishingAnyCommand()
    {
        string name = $"Midora.Audio.Control.Test.{Guid.NewGuid():N}";
        using SharedAudioWorkerControl producer = SharedAudioWorkerControl.Create(name);
        using SharedAudioWorkerControl consumer = SharedAudioWorkerControl.Open(name);
        MidiMonitoringCommand[] commands =
        [
            MidiMonitoringCommand.EnableSource(0),
            new((MidiMonitoringCommandKind)byte.MaxValue, 0, 0, default, false)
        ];

        Assert.Throws<ArgumentException>(() => producer.TryEnqueueMonitoringCommands(commands));
        Assert.False(consumer.TryDequeue(out _));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, SharedAudioWorkerControl.CommandCapacity + 1)]
    public void CorruptRingPositionsAreRejectedBeforePointerArithmetic(long read, long write)
    {
        string name = $"Midora.Audio.Control.Test.{Guid.NewGuid():N}";
        using SharedAudioWorkerControl producer = SharedAudioWorkerControl.Create(name);
        using SharedAudioWorkerControl consumer = SharedAudioWorkerControl.Open(name);
        using MemoryMappedFile mapping = MemoryMappedFile.OpenExisting(
            name,
            MemoryMappedFileRights.ReadWrite);
        using MemoryMappedViewAccessor view = mapping.CreateViewAccessor();
        view.Write(72, read);
        view.Write(80, write);

        Assert.Throws<InvalidDataException>(() => producer.TryEnqueueStop());
        Assert.Throws<InvalidDataException>(() => consumer.TryDequeue(out _));
    }

    [Fact]
    public void MaximumRingPositionIsRejectedBeforeIncrementWrapsNegative()
    {
        string name = $"Midora.Audio.Control.Test.{Guid.NewGuid():N}";
        using SharedAudioWorkerControl control = SharedAudioWorkerControl.Create(name);
        using MemoryMappedFile mapping = MemoryMappedFile.OpenExisting(
            name,
            MemoryMappedFileRights.ReadWrite);
        using MemoryMappedViewAccessor view = mapping.CreateViewAccessor();
        view.Write(72, long.MaxValue);
        view.Write(80, long.MaxValue);

        Assert.Throws<InvalidDataException>(() => control.TryEnqueueStop());
    }

    [Theory]
    [InlineData(128, (byte)byte.MaxValue)]
    [InlineData(140, (byte)1)]
    public void CorruptCommandKindAndReservedPayloadAreRejected(int offset, byte value)
    {
        string name = $"Midora.Audio.Control.Test.{Guid.NewGuid():N}";
        using SharedAudioWorkerControl producer = SharedAudioWorkerControl.Create(name);
        using SharedAudioWorkerControl consumer = SharedAudioWorkerControl.Open(name);
        Assert.True(producer.TryEnqueueStop());
        using MemoryMappedFile mapping = MemoryMappedFile.OpenExisting(
            name,
            MemoryMappedFileRights.ReadWrite);
        using MemoryMappedViewAccessor view = mapping.CreateViewAccessor();
        view.Write(offset, value);

        Assert.Throws<InvalidDataException>(() => consumer.TryDequeue(out _));
    }

    [Theory]
    [InlineData(12, 255L)]
    [InlineData(24, -1L)]
    [InlineData(88, -1L)]
    public void CorruptStatusStateAndCountersAreRejected(int offset, long value)
    {
        string name = $"Midora.Audio.Control.Test.{Guid.NewGuid():N}";
        using SharedAudioWorkerControl control = SharedAudioWorkerControl.Create(name);
        using MemoryMappedFile mapping = MemoryMappedFile.OpenExisting(
            name,
            MemoryMappedFileRights.ReadWrite);
        using MemoryMappedViewAccessor view = mapping.CreateViewAccessor();
        if (offset == 12)
        {
            view.Write(offset, checked((int)value));
        }
        else
        {
            view.Write(offset, value);
        }

        Assert.Throws<InvalidDataException>(() => _ = control.ReadStatus());
    }

    [Fact]
    public void OddStatusSequenceFailsAfterBoundedRetryInsteadOfReturningMixedData()
    {
        string name = $"Midora.Audio.Control.Test.{Guid.NewGuid():N}";
        using SharedAudioWorkerControl producer = SharedAudioWorkerControl.Create(name);
        using MemoryMappedFile mapping = MemoryMappedFile.OpenExisting(
            name,
            MemoryMappedFileRights.ReadWrite);
        using MemoryMappedViewAccessor view = mapping.CreateViewAccessor();
        view.Write(68, 1);
        using SharedAudioWorkerControl consumer = SharedAudioWorkerControl.Open(name);

        Assert.Throws<InvalidDataException>(() => consumer.ReadStatus());
        Assert.Throws<InvalidOperationException>(() => producer.PublishState(AudioWorkerState.Playing));
    }

    [Fact]
    public void StatusSequenceWrapPreservesEvenPublicationAndReadableSnapshot()
    {
        string name = $"Midora.Audio.Control.Test.{Guid.NewGuid():N}";
        using SharedAudioWorkerControl producer = SharedAudioWorkerControl.Create(name);
        using SharedAudioWorkerControl consumer = SharedAudioWorkerControl.Open(name);
        using MemoryMappedFile mapping = MemoryMappedFile.OpenExisting(
            name,
            MemoryMappedFileRights.ReadWrite);
        using MemoryMappedViewAccessor view = mapping.CreateViewAccessor();
        view.Write(68, int.MaxValue - 1);

        producer.PublishRuntimeStatus(AudioWorkerState.Playing, 11, 22, 33, 44, 55);

        Assert.Equal(int.MinValue, view.ReadInt32(68));
        AudioWorkerStatus status = consumer.ReadStatus();
        Assert.Equal(AudioWorkerState.Playing, status.State);
        Assert.Equal(11, status.PositionFrame);
        Assert.Equal(22, status.RenderPositionFrame);
        Assert.Equal(33, status.UnderrunCount);
        Assert.Equal(44, status.CallbackAllocatedBytes);
        Assert.Equal(55, status.RenderingAllocatedBytes);
    }

    [Fact]
    public async Task ConcurrentStatusStressNeverReturnsCrossPublicationSnapshot()
    {
        string name = $"Midora.Audio.Control.Test.{Guid.NewGuid():N}";
        using SharedAudioWorkerControl producer = SharedAudioWorkerControl.Create(name);
        using SharedAudioWorkerControl consumer = SharedAudioWorkerControl.Open(name);
        const int publicationCount = 100_000;
        producer.PublishRuntimeStatus(AudioWorkerState.Playing, 0, 0, 0, 0, 0);

        Task writer = Task.Run(() =>
        {
            for (int generation = 1; generation <= publicationCount; generation++)
            {
                producer.PublishRuntimeStatus(
                    generation % 2 == 0 ? AudioWorkerState.Playing : AudioWorkerState.Buffering,
                    generation,
                    generation * 2L,
                    generation * 3L,
                    generation * 4L,
                    generation * 5L);
            }
        });

        while (!writer.IsCompleted)
        {
            AudioWorkerStatus status;
            try
            {
                status = consumer.ReadStatus();
            }
            catch (InvalidDataException)
            {
                continue;
            }
            long generation = status.PositionFrame;
            Assert.Equal(generation * 2, status.RenderPositionFrame);
            Assert.Equal(generation * 3, status.UnderrunCount);
            Assert.Equal(generation * 4, status.CallbackAllocatedBytes);
            Assert.Equal(generation * 5, status.RenderingAllocatedBytes);
            Assert.Equal(
                generation % 2 == 0 ? AudioWorkerState.Playing : AudioWorkerState.Buffering,
                status.State);
        }
        await writer;
    }

    [Fact]
    public void StatusReadAndPublicationAllocateNoManagedMemoryAfterWarmup()
    {
        string name = $"Midora.Audio.Control.Test.{Guid.NewGuid():N}";
        using SharedAudioWorkerControl producer = SharedAudioWorkerControl.Create(name);
        using SharedAudioWorkerControl consumer = SharedAudioWorkerControl.Open(name);
        producer.PublishRuntimeStatus(AudioWorkerState.Playing, 1, 2, 3, 4, 5);
        _ = consumer.ReadStatus();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int generation = 1; generation <= 10_000; generation++)
        {
            producer.PublishRuntimeStatus(
                AudioWorkerState.Playing,
                generation,
                generation,
                generation,
                generation,
                generation);
            AudioWorkerStatus status = consumer.ReadStatus();
            if (status.PositionFrame != generation)
            {
                throw new InvalidDataException("Status publication was not visible.");
            }
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void DisposedControlRejectsAllPointerBackedEntryPoints()
    {
        SharedAudioWorkerControl control = SharedAudioWorkerControl.Create(
            $"Midora.Audio.Control.Test.{Guid.NewGuid():N}");
        control.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = control.ReadStatus());
        Assert.Throws<ObjectDisposedException>(() => control.PublishState(AudioWorkerState.Playing));
        Assert.Throws<ObjectDisposedException>(() => control.PublishPrepared(48_000, 2_400));
        Assert.Throws<ObjectDisposedException>(() => control.PublishRuntimeStatus(
            AudioWorkerState.Playing, 0, 0, 0, 0, 0));
        Assert.Throws<ObjectDisposedException>(() => control.PublishFault(1));
        Assert.Throws<ObjectDisposedException>(() => control.TryEnqueueStop());
        Assert.Throws<ObjectDisposedException>(() => control.TryDequeue(out _));
    }
}
