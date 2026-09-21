using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace Midora.AudioDevice;

public sealed unsafe class SharedAudioFrameRingBuffer : IAudioRenderSource, IDisposable
{
    private const int HeaderByteCount = 72;
    private const int Magic = 0x5241444d;
    private const int ProtocolVersion = 1;
    private const int MagicOffset = 0;
    private const int VersionOffset = 4;
    private const int SampleRateOffset = 8;
    private const int ChannelCountOffset = 12;
    private const int SampleFormatOffset = 16;
    private const int CapacityFramesOffset = 20;
    private const int ReadPositionOffset = 24;
    private const int WritePositionOffset = 32;
    private const int UnderrunCountOffset = 40;
    private const int ProducerAllocatedBytesOffset = 48;
    private const int ProducerReadyOffset = 56;
    private const int ProducerCompletedOffset = 60;
    private const int ProducerFaultedOffset = 64;
    private readonly MemoryMappedFile _memoryMappedFile;
    private readonly MemoryMappedViewAccessor _view;
    private readonly bool _ownsBackingFile;
    private byte* _basePointer;
    private bool _disposed;

    private SharedAudioFrameRingBuffer(
        string path,
        MemoryMappedFile memoryMappedFile,
        MemoryMappedViewAccessor view,
        bool ownsBackingFile)
    {
        Path = path;
        _memoryMappedFile = memoryMappedFile;
        _view = view;
        _ownsBackingFile = ownsBackingFile;
        byte* pointer = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        _basePointer = pointer + view.PointerOffset;
    }

    /// <summary>
    /// Absolute path of the file that backs this ring. The worker receives it on its command line
    /// and maps the same file.
    /// </summary>
    public string Path { get; }

    public AudioFormat Format => new(
        ReadInt32(SampleRateOffset),
        ReadInt32(ChannelCountOffset),
        (AudioSampleFormat)ReadInt32(SampleFormatOffset));

    public int CapacityFrameCount => ReadInt32(CapacityFramesOffset);

    public int AvailableFrameCount
    {
        get
        {
            long written = Volatile.Read(ref Int64At(WritePositionOffset));
            long read = Volatile.Read(ref Int64At(ReadPositionOffset));
            return (int)(written - read);
        }
    }

    public int FreeFrameCount => CapacityFrameCount - AvailableFrameCount;

    public long ProducedFrameCount => Volatile.Read(ref Int64At(WritePositionOffset));

    public long UnderrunCount => Volatile.Read(ref Int64At(UnderrunCountOffset));

    public long ProducerAllocatedBytes => Volatile.Read(ref Int64At(ProducerAllocatedBytesOffset));

    public bool ProducerReady => Volatile.Read(ref Int32At(ProducerReadyOffset)) != 0;

    public bool ProducerCompleted => Volatile.Read(ref Int32At(ProducerCompletedOffset)) != 0;

    public bool ProducerFaulted => Volatile.Read(ref Int32At(ProducerFaultedOffset)) != 0;

    public static SharedAudioFrameRingBuffer Create(
        string path,
        AudioFormat format,
        int capacityFrameCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        format.Validate();
        if (format.ChannelCount != 2 || format.SampleFormat != AudioSampleFormat.Float32)
        {
            throw new ArgumentException("The shared audio ring requires stereo float32 frames.", nameof(format));
        }

        if (capacityFrameCount < 16)
        {
            throw new ArgumentOutOfRangeException(nameof(capacityFrameCount));
        }

        long totalBytes = checked(HeaderByteCount + ((long)capacityFrameCount * format.BytesPerFrame));
        MemoryMappedFile mapping = SharedMemoryMapping.Create(path, totalBytes);
        MemoryMappedViewAccessor view = mapping.CreateViewAccessor(
            0,
            totalBytes,
            MemoryMappedFileAccess.ReadWrite);
        SharedAudioFrameRingBuffer result = new(path, mapping, view, ownsBackingFile: true);
        NativeMemory.Clear(result._basePointer, (nuint)totalBytes);
        result.WriteInt32(MagicOffset, Magic);
        result.WriteInt32(VersionOffset, ProtocolVersion);
        result.WriteInt32(SampleRateOffset, format.SampleRate);
        result.WriteInt32(ChannelCountOffset, format.ChannelCount);
        result.WriteInt32(SampleFormatOffset, (int)format.SampleFormat);
        result.WriteInt32(CapacityFramesOffset, capacityFrameCount);
        return result;
    }

    public static SharedAudioFrameRingBuffer Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        MemoryMappedFile mapping = SharedMemoryMapping.Open(path);
        MemoryMappedViewAccessor view = mapping.CreateViewAccessor(
            0,
            0,
            MemoryMappedFileAccess.ReadWrite);
        SharedAudioFrameRingBuffer result = new(path, mapping, view, ownsBackingFile: false);
        try
        {
            result.ValidateHeader();
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    public bool TryWriteFrames(float* source, int frameCount)
    {
        if (_disposed || source == null || frameCount < 0 || frameCount > CapacityFrameCount)
        {
            FaultProducer();
            return false;
        }

        long read = Volatile.Read(ref Int64At(ReadPositionOffset));
        long write = Int64At(WritePositionOffset);
        if (frameCount > CapacityFrameCount - (write - read))
        {
            return false;
        }

        CopyIntoRing(source, write, frameCount);
        Volatile.Write(ref Int64At(WritePositionOffset), write + frameCount);
        return true;
    }

    public AudioPullResult PullFrames(float* destination, int requestedFrameCount)
    {
        if (_disposed || destination == null || requestedFrameCount < 0)
        {
            return AudioPullResult.Fault();
        }

        if (requestedFrameCount == 0)
        {
            return ProducerFaulted
                ? AudioPullResult.Fault()
                : AudioPullResult.Continue(0);
        }

        long read = Int64At(ReadPositionOffset);
        long write = Volatile.Read(ref Int64At(WritePositionOffset));
        int availableFrames = (int)(write - read);
        if (availableFrames < requestedFrameCount && !ProducerCompleted && !ProducerFaulted)
        {
            NativeMemory.Clear(
                destination,
                checked((nuint)requestedFrameCount * (nuint)Format.BytesPerFrame));
            Interlocked.Increment(ref Int64At(UnderrunCountOffset));
            return AudioPullResult.Buffering();
        }

        int copiedFrames = Math.Min(availableFrames, requestedFrameCount);
        CopyFromRing(destination, read, copiedFrames);
        if (copiedFrames < requestedFrameCount)
        {
            NativeMemory.Clear(
                destination + (copiedFrames * 2),
                checked((nuint)(requestedFrameCount - copiedFrames) * (nuint)Format.BytesPerFrame));
        }

        Volatile.Write(ref Int64At(ReadPositionOffset), read + copiedFrames);
        if (ProducerFaulted)
        {
            return AudioPullResult.Fault(copiedFrames);
        }

        return ProducerCompleted && copiedFrames == availableFrames
            ? AudioPullResult.EndOfStream(copiedFrames)
            : AudioPullResult.Continue(copiedFrames);
    }

    public void MarkProducerReady()
    {
        Volatile.Write(ref Int32At(ProducerReadyOffset), 1);
    }

    public void CompleteProducer(long allocatedBytes)
    {
        Volatile.Write(ref Int64At(ProducerAllocatedBytesOffset), allocatedBytes);
        Volatile.Write(ref Int32At(ProducerCompletedOffset), 1);
    }

    public void FaultProducer()
    {
        Volatile.Write(ref Int32At(ProducerFaultedOffset), 1);
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
        _memoryMappedFile.Dispose();
        if (_ownsBackingFile)
        {
            SharedMemoryMapping.TryDelete(Path);
        }
    }

    private void ValidateHeader()
    {
        if (ReadInt32(MagicOffset) != Magic || ReadInt32(VersionOffset) != ProtocolVersion)
        {
            throw new InvalidDataException("The shared audio ring header or protocol version is invalid.");
        }

        AudioFormat format = Format;
        format.Validate();
        if (format.ChannelCount != 2
            || format.SampleFormat != AudioSampleFormat.Float32
            || CapacityFrameCount < 16)
        {
            throw new InvalidDataException("The shared audio ring format is invalid.");
        }
    }

    private void CopyIntoRing(float* source, long writePosition, int frameCount)
    {
        int writeIndex = (int)(writePosition % CapacityFrameCount);
        int firstFrames = Math.Min(frameCount, CapacityFrameCount - writeIndex);
        CopyFrames(source, AudioPointer + (writeIndex * 2), firstFrames);
        int remainingFrames = frameCount - firstFrames;
        if (remainingFrames != 0)
        {
            CopyFrames(source + (firstFrames * 2), AudioPointer, remainingFrames);
        }
    }

    private void CopyFromRing(float* destination, long readPosition, int frameCount)
    {
        int readIndex = (int)(readPosition % CapacityFrameCount);
        int firstFrames = Math.Min(frameCount, CapacityFrameCount - readIndex);
        CopyFrames(AudioPointer + (readIndex * 2), destination, firstFrames);
        int remainingFrames = frameCount - firstFrames;
        if (remainingFrames != 0)
        {
            CopyFrames(AudioPointer, destination + (firstFrames * 2), remainingFrames);
        }
    }

    private static void CopyFrames(float* source, float* destination, int frameCount)
    {
        NativeMemory.Copy(
            source,
            destination,
            checked((nuint)frameCount * 2 * sizeof(float)));
    }

    private float* AudioPointer => (float*)(_basePointer + HeaderByteCount);

    private int ReadInt32(int offset) => Int32At(offset);

    private void WriteInt32(int offset, int value) => Int32At(offset) = value;

    private ref int Int32At(int offset) => ref *(int*)(_basePointer + offset);

    private ref long Int64At(int offset) => ref *(long*)(_basePointer + offset);
}
