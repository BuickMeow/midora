using Midora.Audio;
using Midora.Midi;
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
}
