using Midora.Audio;
using Midora.Midi;
using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;

namespace Midora.Audio.Bass.Tests;

[SupportedOSPlatform("windows")]
public sealed class SharedAudioWorkerControlTests
{
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
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, SharedAudioWorkerControl.ProtocolVersion + 1)]
    [InlineData(8, SharedAudioWorkerControl.CommandCapacity - 1)]
    [InlineData(68, 1)]
    [InlineData(88, 1)]
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
