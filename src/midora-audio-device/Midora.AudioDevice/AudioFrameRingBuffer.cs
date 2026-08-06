using System.Runtime.InteropServices;

namespace Midora.AudioDevice;

public sealed unsafe class AudioFrameRingBuffer : IAudioRenderSource, IDisposable
{
    private readonly int _capacityFrameCount;
    private float* _buffer;
    private long _readPosition;
    private long _writePosition;
    private long _underrunCount;
    private int _producerCompleted;
    private int _producerFaulted;
    private bool _disposed;

    public AudioFrameRingBuffer(AudioFormat format, int capacityFrameCount)
    {
        format.Validate();
        if (format.ChannelCount != 2 || format.SampleFormat != AudioSampleFormat.Float32)
        {
            throw new ArgumentException("The initial-release ring requires stereo float32 frames.", nameof(format));
        }

        if (capacityFrameCount < 16)
        {
            throw new ArgumentOutOfRangeException(nameof(capacityFrameCount));
        }

        Format = format;
        _capacityFrameCount = capacityFrameCount;
        nuint byteCount = checked((nuint)capacityFrameCount * (nuint)format.BytesPerFrame);
        _buffer = (float*)NativeMemory.Alloc(byteCount);
    }

    public AudioFormat Format { get; }

    public int CapacityFrameCount => _capacityFrameCount;

    public int AvailableFrameCount
    {
        get
        {
            long written = Volatile.Read(ref _writePosition);
            long read = Volatile.Read(ref _readPosition);
            return (int)(written - read);
        }
    }

    public int FreeFrameCount => _capacityFrameCount - AvailableFrameCount;

    public long UnderrunCount => Volatile.Read(ref _underrunCount);

    public bool ProducerCompleted => Volatile.Read(ref _producerCompleted) != 0;

    public bool ProducerFaulted => Volatile.Read(ref _producerFaulted) != 0;

    public bool TryWriteFrames(float* source, int frameCount)
    {
        if (_disposed || source == null || frameCount < 0 || frameCount > _capacityFrameCount)
        {
            Volatile.Write(ref _producerFaulted, 1);
            return false;
        }

        long read = Volatile.Read(ref _readPosition);
        long write = _writePosition;
        if (frameCount > _capacityFrameCount - (write - read))
        {
            return false;
        }

        CopyIntoRing(source, write, frameCount);
        Volatile.Write(ref _writePosition, write + frameCount);
        return true;
    }

    public AudioPullResult PullFrames(float* destination, int requestedFrameCount)
    {
        if (_disposed || destination == null || requestedFrameCount < 0)
        {
            Volatile.Write(ref _producerFaulted, 1);
            return AudioPullResult.Fault();
        }

        if (requestedFrameCount == 0)
        {
            return ProducerFaulted
                ? AudioPullResult.Fault()
                : AudioPullResult.Continue(0);
        }

        long read = _readPosition;
        long write = Volatile.Read(ref _writePosition);
        int availableFrames = (int)(write - read);

        if (availableFrames < requestedFrameCount && !ProducerCompleted && !ProducerFaulted)
        {
            NativeMemory.Clear(
                destination,
                checked((nuint)requestedFrameCount * (nuint)Format.BytesPerFrame));
            Interlocked.Increment(ref _underrunCount);
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

        Volatile.Write(ref _readPosition, read + copiedFrames);
        if (ProducerFaulted)
        {
            return AudioPullResult.Fault(copiedFrames);
        }

        return ProducerCompleted && copiedFrames == availableFrames
            ? AudioPullResult.EndOfStream(copiedFrames)
            : AudioPullResult.Continue(copiedFrames);
    }

    public void CompleteProducer()
    {
        Volatile.Write(ref _producerCompleted, 1);
    }

    public void FaultProducer()
    {
        Volatile.Write(ref _producerFaulted, 1);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_buffer != null)
        {
            NativeMemory.Free(_buffer);
            _buffer = null;
        }
    }

    private void CopyIntoRing(float* source, long writePosition, int frameCount)
    {
        int writeIndex = (int)(writePosition % _capacityFrameCount);
        int firstFrames = Math.Min(frameCount, _capacityFrameCount - writeIndex);
        CopyFrames(source, _buffer + (writeIndex * 2), firstFrames);
        int remainingFrames = frameCount - firstFrames;
        if (remainingFrames != 0)
        {
            CopyFrames(source + (firstFrames * 2), _buffer, remainingFrames);
        }
    }

    private void CopyFromRing(float* destination, long readPosition, int frameCount)
    {
        int readIndex = (int)(readPosition % _capacityFrameCount);
        int firstFrames = Math.Min(frameCount, _capacityFrameCount - readIndex);
        CopyFrames(_buffer + (readIndex * 2), destination, firstFrames);
        int remainingFrames = frameCount - firstFrames;
        if (remainingFrames != 0)
        {
            CopyFrames(_buffer, destination + (firstFrames * 2), remainingFrames);
        }
    }

    private static void CopyFrames(float* source, float* destination, int frameCount)
    {
        nuint byteCount = checked((nuint)frameCount * 2 * sizeof(float));
        NativeMemory.Copy(source, destination, byteCount);
    }
}
