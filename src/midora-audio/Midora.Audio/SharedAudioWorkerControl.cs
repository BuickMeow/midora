using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Midora.Midi;

namespace Midora.Audio;

public enum AudioWorkerState : int
{
    Created,
    Preparing,
    Prepared,
    Playing,
    Buffering,
    Completed,
    Stopping,
    Stopped,
    Faulted,
    Rendering,
    Cancelling,
    Finalizing,
    Cancelled,
    OutputDeviceUnavailable,
    HeldPreviewPaused
}

public enum AudioWorkerControlCommandKind : byte
{
    Stop,
    Monitoring,
    HeldPreviewPause,
    HeldPreviewApplyPlan,
    HeldPreviewResume,
    BufferingRecoveryPrepare,
    PersistentProbe,
    PersistentStartPlayback,
    PitchAuditionNoteOn,
    PitchAuditionNoteOff,
    PersistentShutdown,
    PitchAuditionUpdate,
    PitchAuditionEnd
}

public readonly record struct AudioWorkerControlCommand(
    AudioWorkerControlCommandKind Kind,
    MidiMonitoringCommand MonitoringCommand,
    long Payload = 0);

public readonly record struct AudioWorkerStatus(
    AudioWorkerState State,
    int ActualSampleRate,
    int ActualDeviceBufferFrameCount,
    long PositionFrame,
    long RenderPositionFrame,
    long UnderrunCount,
    long CallbackAllocatedBytes,
    long RenderingAllocatedBytes,
    int FaultCode,
    long HeldPreviewPlanGeneration,
    long PersistentPlaybackAcceptedGeneration,
    long PersistentResponseGeneration);

/// <summary>
/// Fixed-version, bounded, allocation-free runtime IPC between the UI process and the audio worker.
/// The mapping contains status scalars and a single-producer/single-consumer command ring; it never
/// carries PCM frames or serialized object graphs.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class SharedAudioWorkerControl : IDisposable
{
    public const int ProtocolVersion = 7;
    public const int CommandCapacity = 1_024;
    public const int MaximumStatusReadAttempts = 1_024;

    private const int Magic = 0x4357414d;
    private const int HeaderByteCount = 128;
    private const int CommandByteCount = 16;
    private const int TotalByteCount = HeaderByteCount + (CommandCapacity * CommandByteCount);
    private const int MagicOffset = 0;
    private const int VersionOffset = 4;
    private const int CapacityOffset = 8;
    private const int StateOffset = 12;
    private const int ActualSampleRateOffset = 16;
    private const int ActualDeviceBufferFrameCountOffset = 20;
    private const int PositionFrameOffset = 24;
    private const int RenderPositionFrameOffset = 32;
    private const int UnderrunCountOffset = 40;
    private const int CallbackAllocatedBytesOffset = 48;
    private const int RenderingAllocatedBytesOffset = 56;
    private const int FaultCodeOffset = 64;
    private const int StatusSequenceOffset = 68;
    private const int CommandReadPositionOffset = 72;
    private const int CommandWritePositionOffset = 80;
    private const int HeldPreviewPlanGenerationOffset = 88;
    private const int PersistentPlaybackAcceptedGenerationOffset = 96;
    private const int PersistentResponseGenerationOffset = 104;
    private const int HeaderReservedOffset = 112;

    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private byte* _basePointer;
    private bool _disposed;

    private SharedAudioWorkerControl(
        string name,
        MemoryMappedFile mapping,
        MemoryMappedViewAccessor view)
    {
        Name = name;
        _mapping = mapping;
        _view = view;
        byte* pointer = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        _basePointer = pointer + view.PointerOffset;
    }

    public string Name { get; }

    public static SharedAudioWorkerControl Create(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        MemoryMappedFile mapping = MemoryMappedFile.CreateNew(
            name,
            TotalByteCount,
            MemoryMappedFileAccess.ReadWrite);
        MemoryMappedViewAccessor? view = null;
        SharedAudioWorkerControl? result = null;
        try
        {
            view = mapping.CreateViewAccessor(
                0,
                TotalByteCount,
                MemoryMappedFileAccess.ReadWrite);
            result = new(name, mapping, view);
            NativeMemory.Clear(result._basePointer, (nuint)TotalByteCount);
            result.Int32At(MagicOffset) = Magic;
            result.Int32At(VersionOffset) = ProtocolVersion;
            result.Int32At(CapacityOffset) = CommandCapacity;
            result.Int64At(PersistentResponseGenerationOffset) = -1;
            return result;
        }
        catch
        {
            if (result is not null)
            {
                result.Dispose();
            }
            else
            {
                view?.Dispose();
                mapping.Dispose();
            }
            throw;
        }
    }

    public static SharedAudioWorkerControl Open(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        MemoryMappedFile mapping = MemoryMappedFile.OpenExisting(
            name,
            MemoryMappedFileRights.ReadWrite);
        MemoryMappedViewAccessor? view = null;
        SharedAudioWorkerControl? result = null;
        try
        {
            view = mapping.CreateViewAccessor(
                0,
                TotalByteCount,
                MemoryMappedFileAccess.ReadWrite);
            result = new(name, mapping, view);
            if (result.Int32At(MagicOffset) != Magic
                || result.Int32At(VersionOffset) != ProtocolVersion
                || result.Int32At(CapacityOffset) != CommandCapacity
                || !result.IsZeroedRange(HeaderReservedOffset, HeaderByteCount))
            {
                throw new InvalidDataException("The audio worker shared-memory ABI is incompatible.");
            }
            return result;
        }
        catch
        {
            if (result is not null)
            {
                result.Dispose();
            }
            else
            {
                view?.Dispose();
                mapping.Dispose();
            }
            throw;
        }
    }

    public AudioWorkerStatus ReadStatus()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (int attempt = 0; attempt < MaximumStatusReadAttempts; attempt++)
        {
            int before = Volatile.Read(ref Int32At(StatusSequenceOffset));
            if ((before & 1) != 0)
            {
                continue;
            }
            AudioWorkerStatus result = new(
                (AudioWorkerState)Volatile.Read(ref Int32At(StateOffset)),
                Volatile.Read(ref Int32At(ActualSampleRateOffset)),
                Volatile.Read(ref Int32At(ActualDeviceBufferFrameCountOffset)),
                Volatile.Read(ref Int64At(PositionFrameOffset)),
                Volatile.Read(ref Int64At(RenderPositionFrameOffset)),
                Volatile.Read(ref Int64At(UnderrunCountOffset)),
                Volatile.Read(ref Int64At(CallbackAllocatedBytesOffset)),
                Volatile.Read(ref Int64At(RenderingAllocatedBytesOffset)),
                Volatile.Read(ref Int32At(FaultCodeOffset)),
                Volatile.Read(ref Int64At(HeldPreviewPlanGenerationOffset)),
                Volatile.Read(ref Int64At(PersistentPlaybackAcceptedGenerationOffset)),
                Volatile.Read(ref Int64At(PersistentResponseGenerationOffset)));
            int after = Volatile.Read(ref Int32At(StatusSequenceOffset));
            if (before != after || (after & 1) != 0)
            {
                continue;
            }
            if (!IsValidStatus(result))
            {
                throw new InvalidDataException("The audio worker shared-memory status is invalid.");
            }
            return result;
        }
        throw new InvalidDataException(
            "The audio worker shared-memory status did not stabilize within the bounded retry limit.");
    }

    public void PublishPrepared(int actualSampleRate, int actualDeviceBufferFrameCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (actualSampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(actualSampleRate));
        }
        if (actualDeviceBufferFrameCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(actualDeviceBufferFrameCount));
        }
        int sequence = BeginStatusPublication();
        Volatile.Write(ref Int32At(ActualSampleRateOffset), actualSampleRate);
        Volatile.Write(ref Int32At(ActualDeviceBufferFrameCountOffset), actualDeviceBufferFrameCount);
        Volatile.Write(ref Int32At(StateOffset), (int)AudioWorkerState.Prepared);
        EndStatusPublication(sequence);
    }

    public void PublishState(AudioWorkerState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateState(state);
        int sequence = BeginStatusPublication();
        Volatile.Write(ref Int32At(StateOffset), (int)state);
        EndStatusPublication(sequence);
    }

    public void PublishRuntimeStatus(
        AudioWorkerState state,
        long positionFrame,
        long renderPositionFrame,
        long underrunCount,
        long callbackAllocatedBytes,
        long renderingAllocatedBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateState(state);
        if (positionFrame < 0
            || renderPositionFrame < 0
            || underrunCount < 0
            || callbackAllocatedBytes < 0
            || renderingAllocatedBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(positionFrame));
        }
        int sequence = BeginStatusPublication();
        Volatile.Write(ref Int64At(PositionFrameOffset), positionFrame);
        Volatile.Write(ref Int64At(RenderPositionFrameOffset), renderPositionFrame);
        Volatile.Write(ref Int64At(UnderrunCountOffset), underrunCount);
        Volatile.Write(ref Int64At(CallbackAllocatedBytesOffset), callbackAllocatedBytes);
        Volatile.Write(ref Int64At(RenderingAllocatedBytesOffset), renderingAllocatedBytes);
        Volatile.Write(ref Int32At(StateOffset), (int)state);
        EndStatusPublication(sequence);
    }

    /// <summary>
    /// Publishes deterministic Buffering-recovery progress without changing the
    /// shared-memory ABI. PositionFrame remains the frozen consumer frontier and
    /// RenderPositionFrame becomes the absolute prepared recovery frontier until
    /// normal runtime status publication resumes.
    /// </summary>
    public void PublishBufferingRecoveryProgress(
        long positionFrame,
        long preparedThroughFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (positionFrame < 0 || preparedThroughFrame < positionFrame)
        {
            throw new ArgumentOutOfRangeException(nameof(preparedThroughFrame));
        }
        int sequence = BeginStatusPublication();
        Volatile.Write(ref Int64At(PositionFrameOffset), positionFrame);
        Volatile.Write(ref Int64At(RenderPositionFrameOffset), preparedThroughFrame);
        Volatile.Write(ref Int32At(StateOffset), (int)AudioWorkerState.Buffering);
        EndStatusPublication(sequence);
    }

    public void PublishPersistentPlaybackAcceptance(long generation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (generation <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }
        int sequence = BeginStatusPublication();
        Volatile.Write(
            ref Int64At(PersistentPlaybackAcceptedGenerationOffset),
            generation);
        Volatile.Write(ref Int32At(StateOffset), (int)AudioWorkerState.Preparing);
        EndStatusPublication(sequence);
    }

    public void PublishPersistentResponse(long generation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (generation < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }
        long previous = Volatile.Read(ref Int64At(PersistentResponseGenerationOffset));
        if (generation <= previous)
        {
            throw new InvalidOperationException(
                "Persistent audio worker response generations must increase monotonically.");
        }
        int sequence = BeginStatusPublication();
        Volatile.Write(ref Int64At(PersistentResponseGenerationOffset), generation);
        EndStatusPublication(sequence);
    }

    public void PublishHeldPreviewStatus(
        AudioWorkerState state,
        long positionFrame,
        long producerFrontierFrame,
        long underrunCount,
        long callbackAllocatedBytes,
        long renderingAllocatedBytes,
        long planGeneration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (state is not AudioWorkerState.HeldPreviewPaused
            and not AudioWorkerState.Playing
            and not AudioWorkerState.Buffering)
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }
        if (positionFrame < 0
            || producerFrontierFrame < 0
            || underrunCount < 0
            || callbackAllocatedBytes < 0
            || renderingAllocatedBytes < 0
            || planGeneration < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(producerFrontierFrame));
        }
        int sequence = BeginStatusPublication();
        Volatile.Write(ref Int64At(PositionFrameOffset), positionFrame);
        Volatile.Write(ref Int64At(RenderPositionFrameOffset), producerFrontierFrame);
        Volatile.Write(ref Int64At(UnderrunCountOffset), underrunCount);
        Volatile.Write(ref Int64At(CallbackAllocatedBytesOffset), callbackAllocatedBytes);
        Volatile.Write(ref Int64At(RenderingAllocatedBytesOffset), renderingAllocatedBytes);
        Volatile.Write(ref Int64At(HeldPreviewPlanGenerationOffset), planGeneration);
        Volatile.Write(ref Int32At(StateOffset), (int)state);
        EndStatusPublication(sequence);
    }

    public void PublishFault(int faultCode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (faultCode <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(faultCode));
        }
        int sequence = BeginStatusPublication();
        Volatile.Write(ref Int32At(FaultCodeOffset), faultCode);
        Volatile.Write(ref Int32At(StateOffset), (int)AudioWorkerState.Faulted);
        EndStatusPublication(sequence);
    }

    public bool TryEnqueueStop(bool flush = true)
    {
        MidiMonitoringCommand payload = new(default, 0, 0, default, flush);
        AudioWorkerControlCommand command = new(AudioWorkerControlCommandKind.Stop, payload);
        return TryEnqueue(command);
    }

    public bool TryEnqueueHeldPreviewPause() => TryEnqueue(
        new(AudioWorkerControlCommandKind.HeldPreviewPause, default));

    public bool TryEnqueueHeldPreviewApplyPlan(long planGeneration)
    {
        if (planGeneration <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(planGeneration));
        }
        return TryEnqueue(new(
            AudioWorkerControlCommandKind.HeldPreviewApplyPlan,
            default,
            planGeneration));
    }

    public bool TryEnqueueHeldPreviewResume() => TryEnqueue(
        new(AudioWorkerControlCommandKind.HeldPreviewResume, default));

    public bool TryEnqueueBufferingRecovery(long recoveryEndFrame)
    {
        if (recoveryEndFrame <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(recoveryEndFrame));
        }
        return TryEnqueue(new(
            AudioWorkerControlCommandKind.BufferingRecoveryPrepare,
            default,
            recoveryEndFrame));
    }

    public bool TryEnqueuePersistentProbe(long generation) =>
        TryEnqueuePersistentCommand(AudioWorkerControlCommandKind.PersistentProbe, generation);

    public bool TryEnqueuePersistentStartPlayback(long generation) =>
        TryEnqueuePersistentCommand(AudioWorkerControlCommandKind.PersistentStartPlayback, generation);

    public bool TryEnqueuePitchAuditionNoteOn(long generation) =>
        TryEnqueuePersistentCommand(AudioWorkerControlCommandKind.PitchAuditionNoteOn, generation);

    public bool TryEnqueuePitchAuditionNoteOff(long generation) =>
        TryEnqueuePersistentCommand(AudioWorkerControlCommandKind.PitchAuditionNoteOff, generation);

    public bool TryEnqueuePitchAuditionUpdate(int pitch, int velocity)
    {
        if (pitch is < 0 or > 127 || velocity is < 1 or > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(pitch));
        }
        long payload = (long)(uint)pitch | ((long)(uint)velocity << 8);
        return TryEnqueue(new(AudioWorkerControlCommandKind.PitchAuditionUpdate, default, payload));
    }

    public bool TryEnqueuePitchAuditionEnd() =>
        TryEnqueue(new(AudioWorkerControlCommandKind.PitchAuditionEnd, default));

    public bool TryEnqueuePersistentShutdown(long generation) =>
        TryEnqueuePersistentCommand(AudioWorkerControlCommandKind.PersistentShutdown, generation);

    private bool TryEnqueuePersistentCommand(
        AudioWorkerControlCommandKind kind,
        long generation)
    {
        if (generation <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }
        return TryEnqueue(new(kind, default, generation));
    }

    public bool TryEnqueueMonitoringCommands(ReadOnlySpan<MidiMonitoringCommand> commands)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (commands.IsEmpty)
        {
            return true;
        }

        long write = Int64At(CommandWritePositionOffset);
        long read = Volatile.Read(ref Int64At(CommandReadPositionOffset));
        ValidateCommandRingPositions(read, write);
        if (commands.Length > CommandCapacity - (write - read))
        {
            return false;
        }
        if (write > long.MaxValue - commands.Length)
        {
            throw new InvalidDataException("The audio worker command ring position overflowed.");
        }

        for (int i = 0; i < commands.Length; i++)
        {
            ValidateMonitoringCommandForWrite(commands[i]);
        }
        for (int i = 0; i < commands.Length; i++)
        {
            WriteCommand(write + i, new(AudioWorkerControlCommandKind.Monitoring, commands[i]));
        }
        Volatile.Write(ref Int64At(CommandWritePositionOffset), write + commands.Length);
        return true;
    }

    public bool TryDequeue(out AudioWorkerControlCommand command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long read = Int64At(CommandReadPositionOffset);
        long write = Volatile.Read(ref Int64At(CommandWritePositionOffset));
        ValidateCommandRingPositions(read, write);
        if (read == write)
        {
            command = default;
            return false;
        }

        command = ReadCommand(read);
        Volatile.Write(ref Int64At(CommandReadPositionOffset), read + 1);
        return true;
    }

    /// <summary>
    /// Gives Stop priority over commands that were queued before it. Stop ends the
    /// active session, so older monitoring/recovery commands no longer have an
    /// observable result and must not delay shutdown.
    /// </summary>
    public bool TryDequeuePendingStop(out bool flush)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long read = Int64At(CommandReadPositionOffset);
        long write = Volatile.Read(ref Int64At(CommandWritePositionOffset));
        ValidateCommandRingPositions(read, write);
        for (long position = read; position < write; position++)
        {
            AudioWorkerControlCommand command = ReadCommand(position);
            if (command.Kind != AudioWorkerControlCommandKind.Stop)
            {
                continue;
            }

            flush = command.MonitoringCommand.SourceEnabled;
            Volatile.Write(ref Int64At(CommandReadPositionOffset), position + 1);
            return true;
        }

        flush = true;
        return false;
    }

    public bool HasPendingStopCommand()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long read = Int64At(CommandReadPositionOffset);
        long write = Volatile.Read(ref Int64At(CommandWritePositionOffset));
        ValidateCommandRingPositions(read, write);
        for (long position = read; position < write; position++)
        {
            if (ReadCommand(position).Kind == AudioWorkerControlCommandKind.Stop)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Dequeues contiguous monitoring commands without consuming the following
    /// non-monitoring command. A snapshot may contain one or more atomically
    /// published monitoring changes; their original order is preserved so they
    /// can share one renderer cold start.
    /// </summary>
    public bool TryDequeueMonitoringCommands(
        Span<MidiMonitoringCommand> destination,
        out int commandCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (destination.IsEmpty)
        {
            throw new ArgumentException(
                "A monitoring dequeue destination must not be empty.",
                nameof(destination));
        }

        long read = Int64At(CommandReadPositionOffset);
        long write = Volatile.Read(ref Int64At(CommandWritePositionOffset));
        ValidateCommandRingPositions(read, write);
        commandCount = 0;
        while (read + commandCount < write
            && commandCount < destination.Length)
        {
            AudioWorkerControlCommand command = ReadCommand(read + commandCount);
            if (command.Kind != AudioWorkerControlCommandKind.Monitoring)
            {
                break;
            }
            destination[commandCount++] = command.MonitoringCommand;
        }
        if (commandCount == 0)
        {
            return false;
        }

        Volatile.Write(ref Int64At(CommandReadPositionOffset), read + commandCount);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_basePointer != null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _basePointer = null;
        }
        _view.Dispose();
        _mapping.Dispose();
    }

    private bool TryEnqueue(AudioWorkerControlCommand command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long write = Int64At(CommandWritePositionOffset);
        long read = Volatile.Read(ref Int64At(CommandReadPositionOffset));
        ValidateCommandRingPositions(read, write);
        if (write - read >= CommandCapacity)
        {
            return false;
        }
        if (write == long.MaxValue)
        {
            throw new InvalidDataException("The audio worker command ring position overflowed.");
        }

        WriteCommand(write, command);
        Volatile.Write(ref Int64At(CommandWritePositionOffset), write + 1);
        return true;
    }

    private void WriteCommand(long position, AudioWorkerControlCommand command)
    {
        byte* target = CommandPointer(position);
        NativeMemory.Clear(target, CommandByteCount);
        target[0] = (byte)command.Kind;
        if (command.Kind is AudioWorkerControlCommandKind.HeldPreviewPause
            or AudioWorkerControlCommandKind.HeldPreviewApplyPlan
            or AudioWorkerControlCommandKind.HeldPreviewResume
            or AudioWorkerControlCommandKind.BufferingRecoveryPrepare
            or AudioWorkerControlCommandKind.PersistentProbe
            or AudioWorkerControlCommandKind.PersistentStartPlayback
            or AudioWorkerControlCommandKind.PitchAuditionNoteOn
            or AudioWorkerControlCommandKind.PitchAuditionNoteOff
            or AudioWorkerControlCommandKind.PersistentShutdown
            or AudioWorkerControlCommandKind.PitchAuditionUpdate
            or AudioWorkerControlCommandKind.PitchAuditionEnd)
        {
            *(long*)(target + 4) = command.Payload;
            return;
        }
        MidiMonitoringCommand monitoring = command.MonitoringCommand;
        target[1] = (byte)monitoring.Kind;
        target[2] = monitoring.ZeroBasedPortNumber;
        target[3] = monitoring.SourceEnabled ? (byte)1 : (byte)0;
        *(int*)(target + 4) = monitoring.SourceIndex;
        *(uint*)(target + 8) = monitoring.Message.PackedValue;
    }

    private AudioWorkerControlCommand ReadCommand(long position)
    {
        byte* source = CommandPointer(position);
        AudioWorkerControlCommandKind kind = (AudioWorkerControlCommandKind)source[0];
        MidiMonitoringCommandKind monitoringKind = (MidiMonitoringCommandKind)source[1];
        byte zeroBasedPortNumber = source[2];
        byte sourceEnabled = source[3];
        int sourceIndex = *(int*)(source + 4);
        uint packedMessage = *(uint*)(source + 8);
        uint reserved = *(uint*)(source + 12);
        if (reserved != 0 || sourceEnabled > 1)
        {
            throw new InvalidDataException("The audio worker command payload or reserved field is invalid.");
        }

        if (kind is AudioWorkerControlCommandKind.HeldPreviewPause
            or AudioWorkerControlCommandKind.HeldPreviewApplyPlan
            or AudioWorkerControlCommandKind.HeldPreviewResume)
        {
            long generation = *(long*)(source + 4);
            if (monitoringKind != 0
                || zeroBasedPortNumber != 0
                || sourceEnabled != 0
                || kind == AudioWorkerControlCommandKind.HeldPreviewApplyPlan && generation <= 0
                || kind != AudioWorkerControlCommandKind.HeldPreviewApplyPlan && generation != 0)
            {
                throw new InvalidDataException(
                    "The audio worker held-preview command payload is invalid.");
            }
            return new(kind, default, generation);
        }

        if (kind == AudioWorkerControlCommandKind.BufferingRecoveryPrepare)
        {
            long recoveryEndFrame = *(long*)(source + 4);
            if (monitoringKind != 0
                || zeroBasedPortNumber != 0
                || sourceEnabled != 0
                || recoveryEndFrame <= 0)
            {
                throw new InvalidDataException(
                    "The audio worker Buffering recovery command payload is invalid.");
            }
            return new(kind, default, recoveryEndFrame);
        }

        if (kind is AudioWorkerControlCommandKind.PersistentProbe
            or AudioWorkerControlCommandKind.PersistentStartPlayback
            or AudioWorkerControlCommandKind.PitchAuditionNoteOn
            or AudioWorkerControlCommandKind.PitchAuditionNoteOff
            or AudioWorkerControlCommandKind.PersistentShutdown)
        {
            long generation = *(long*)(source + 4);
            if (monitoringKind != 0
                || zeroBasedPortNumber != 0
                || sourceEnabled != 0
                || generation <= 0)
            {
                throw new InvalidDataException(
                    "The persistent audio worker command payload is invalid.");
            }
            return new(kind, default, generation);
        }

        if (kind == AudioWorkerControlCommandKind.PitchAuditionUpdate)
        {
            long payload = *(long*)(source + 4);
            int pitch = (int)(payload & 0xff);
            int velocity = (int)((payload >> 8) & 0xff);
            if (monitoringKind != 0
                || zeroBasedPortNumber != 0
                || sourceEnabled != 0
                || pitch is < 0 or > 127
                || velocity is < 1 or > 127
                || (payload >> 16) != 0)
            {
                throw new InvalidDataException(
                    "The pitch audition update command payload is invalid.");
            }
            return new(kind, default, payload);
        }

        if (kind == AudioWorkerControlCommandKind.PitchAuditionEnd)
        {
            long payload = *(long*)(source + 4);
            if (monitoringKind != 0
                || zeroBasedPortNumber != 0
                || sourceEnabled != 0
                || payload != 0)
            {
                throw new InvalidDataException(
                    "The pitch audition end command payload is invalid.");
            }
            return new(kind, default);
        }

        if (kind == AudioWorkerControlCommandKind.Stop)
        {
            if (monitoringKind != MidiMonitoringCommandKind.SetSourceEnabled
                || zeroBasedPortNumber != 0
                || sourceIndex != 0
                || packedMessage != 0)
            {
                throw new InvalidDataException("The audio worker Stop command payload is invalid.");
            }
            return new(kind, new(default, 0, 0, default, sourceEnabled != 0));
        }
        if (kind != AudioWorkerControlCommandKind.Monitoring)
        {
            throw new InvalidDataException("The audio worker command kind is invalid.");
        }

        MidiMessage message = default;
        if (monitoringKind == MidiMonitoringCommandKind.SetSourceEnabled)
        {
            if (sourceIndex < 0 || zeroBasedPortNumber != 0 || packedMessage != 0)
            {
                throw new InvalidDataException("The audio worker source command payload is invalid.");
            }
        }
        else if (monitoringKind == MidiMonitoringCommandKind.SendMessage)
        {
            if (sourceIndex != -1 || zeroBasedPortNumber >= 16 || sourceEnabled != 0)
            {
                throw new InvalidDataException("The audio worker MIDI command payload is invalid.");
            }
            try
            {
                message = MidiMessage.FromPackedValue(packedMessage);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("The audio worker MIDI command is malformed.", exception);
            }
            if (message.MessageType == MidiMessageType.ControlChange && message.Byte1 is 91 or 93)
            {
                throw new InvalidDataException("CC91 and CC93 are forbidden in audio worker commands.");
            }
        }
        else
        {
            throw new InvalidDataException("The audio worker monitoring command kind is invalid.");
        }

        MidiMonitoringCommand monitoring = new(
            monitoringKind,
            sourceIndex,
            zeroBasedPortNumber,
            message,
            sourceEnabled != 0);
        return new(kind, monitoring);
    }

    private byte* CommandPointer(long position) =>
        _basePointer + HeaderByteCount + ((position % CommandCapacity) * CommandByteCount);

    private ref int Int32At(int offset) => ref *(int*)(_basePointer + offset);

    private ref long Int64At(int offset) => ref *(long*)(_basePointer + offset);

    private int BeginStatusPublication()
    {
        ref int sequence = ref Int32At(StatusSequenceOffset);
        int even = Volatile.Read(ref sequence);
        if ((even & 1) != 0)
        {
            throw new InvalidOperationException(
                "The audio worker status has a concurrent or interrupted writer.");
        }
        int odd = unchecked(even + 1);
        if (Interlocked.CompareExchange(ref sequence, odd, even) != even)
        {
            throw new InvalidOperationException(
                "The audio worker status supports only one writer.");
        }
        return odd;
    }

    private void EndStatusPublication(int oddSequence) =>
        Volatile.Write(
            ref Int32At(StatusSequenceOffset),
            unchecked(oddSequence + 1));

    private static void ValidateCommandRingPositions(long read, long write)
    {
        if (read < 0 || write < read || write - read > CommandCapacity)
        {
            throw new InvalidDataException("The audio worker command ring positions are invalid.");
        }
    }

    private static void ValidateMonitoringCommandForWrite(MidiMonitoringCommand command)
    {
        if (command.Kind == MidiMonitoringCommandKind.SetSourceEnabled)
        {
            if (command.SourceIndex < 0
                || command.ZeroBasedPortNumber != 0
                || command.Message.PackedValue != 0)
            {
                throw new ArgumentException("The audio worker source command payload is invalid.", nameof(command));
            }
            return;
        }

        MidiMessage message = command.Message;
        if (command.Kind != MidiMonitoringCommandKind.SendMessage
            || command.SourceIndex != -1
            || command.ZeroBasedPortNumber >= 16
            || command.SourceEnabled
            || !message.IsChannelVoiceMessage
            || message.Length is < 2 or > 3
            || message.Byte1 > 127
            || message.Byte2 > 127
            || message.MessageType == MidiMessageType.ControlChange && message.Byte1 is 91 or 93)
        {
            throw new ArgumentException("The audio worker MIDI command payload is invalid.", nameof(command));
        }
    }

    private static bool IsValidStatus(AudioWorkerStatus status) =>
        (uint)status.State <= (uint)AudioWorkerState.HeldPreviewPaused
        && status.ActualSampleRate >= 0
        && status.ActualDeviceBufferFrameCount >= 0
        && status.PositionFrame >= 0
        && status.RenderPositionFrame >= 0
        && status.UnderrunCount >= 0
        && status.CallbackAllocatedBytes >= 0
        && status.RenderingAllocatedBytes >= 0
        && status.FaultCode >= 0
        && status.HeldPreviewPlanGeneration >= 0
        && status.PersistentPlaybackAcceptedGeneration >= 0
        && status.PersistentResponseGeneration >= -1;

    private static void ValidateState(AudioWorkerState state)
    {
        if ((uint)state > (uint)AudioWorkerState.HeldPreviewPaused)
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }
    }

    private bool IsZeroedRange(int startOffset, int endOffset)
    {
        for (int offset = startOffset; offset < endOffset; offset++)
        {
            if (_basePointer[offset] != 0)
            {
                return false;
            }
        }
        return true;
    }
}
