using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace Midora.AudioDevice;

/// <summary>
/// Moves sequential PCM file access onto a dedicated I/O thread while exposing a
/// bounded, unmanaged SPSC frame ring to the realtime render path.
/// </summary>
public sealed unsafe class AsyncPcmFileStream : IDisposable
{
    public const int DefaultBufferFrameCount = 16_384;
    private readonly FileStream _file;
    private readonly SafeFileHandle _handle;
    private readonly Thread _thread;
    private readonly long _payloadOffset;
    private readonly long _totalFrameCount;
    private readonly int _capacityFrameCount;
    private readonly bool _readMode;
    private byte* _buffer;
    private long _consumerPosition;
    private long _producerPosition;
    private int _producerCompleted;
    private int _stopRequested;
    private int _threadFinished;
    private Exception? _fault;
    private bool _disposed;

    public AsyncPcmFileStream(
        string path,
        long payloadOffset,
        long totalFrameCount,
        AudioFormat format,
        bool readMode,
        int bufferFrameCount = DefaultBufferFrameCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        format.Validate();
        if (payloadOffset < 0 || totalFrameCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(payloadOffset));
        }
        if (bufferFrameCount < 256)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferFrameCount));
        }

        string fullPath = Path.GetFullPath(path);
        long payloadBytes = checked(totalFrameCount * format.BytesPerFrame);
        long requiredLength = checked(payloadOffset + payloadBytes);
        if (!File.Exists(fullPath) || new FileInfo(fullPath).Length < requiredLength)
        {
            throw new InvalidDataException("The asynchronous PCM payload is truncated.");
        }

        Format = format;
        _payloadOffset = payloadOffset;
        _totalFrameCount = totalFrameCount;
        _capacityFrameCount = bufferFrameCount;
        _readMode = readMode;
        _buffer = (byte*)NativeMemory.Alloc(
            checked((nuint)bufferFrameCount * (nuint)format.BytesPerFrame));
        if (_buffer is null)
        {
            throw new OutOfMemoryException("The asynchronous PCM hot-set buffer could not be allocated.");
        }

        try
        {
            _file = new FileStream(
                fullPath,
                FileMode.Open,
                readMode ? FileAccess.Read : FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 1,
                FileOptions.RandomAccess);
            _handle = _file.SafeFileHandle;
            _thread = new Thread(readMode ? RunReader : RunWriter)
            {
                IsBackground = true,
                Name = readMode
                    ? "Midora PCM Cache Read-Ahead"
                    : "Midora PCM Cache Writer",
                Priority = ThreadPriority.BelowNormal
            };
            _thread.Start();
        }
        catch
        {
            NativeMemory.Free(_buffer);
            _buffer = null;
            throw;
        }
    }

    public AudioFormat Format { get; }

    public bool IsFaulted => Volatile.Read(ref _fault) is not null;

    public Exception? Fault => Volatile.Read(ref _fault);

    public bool TryReadFrames(float* destination, int frameCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_readMode || destination == null || frameCount < 0 || frameCount > _capacityFrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        }
        long read = _consumerPosition;
        long write = Volatile.Read(ref _producerPosition);
        if (write - read < frameCount)
        {
            return false;
        }
        CopyFromRing(destination, read, frameCount);
        Volatile.Write(ref _consumerPosition, read + frameCount);
        return true;
    }

    public bool CanWriteFrames(int frameCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_readMode || frameCount < 0 || frameCount > _capacityFrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        }
        long consumed = Volatile.Read(ref _consumerPosition);
        long produced = _producerPosition;
        return frameCount <= _capacityFrameCount - (produced - consumed);
    }

    public bool TryWriteFrames(float* source, int frameCount)
    {
        if (!CanWriteFrames(frameCount) || source == null)
        {
            return false;
        }
        long write = _producerPosition;
        if (write + frameCount > _totalFrameCount)
        {
            return false;
        }
        CopyIntoRing(source, write, frameCount);
        Volatile.Write(ref _producerPosition, write + frameCount);
        return true;
    }

    public void CompleteWriting()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_readMode)
        {
            throw new InvalidOperationException("A PCM read-ahead stream cannot be completed by its consumer.");
        }
        Volatile.Write(ref _producerCompleted, 1);
    }

    public void WaitForWritingCompletion()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_readMode)
        {
            throw new InvalidOperationException("A PCM read-ahead stream has no writer to await.");
        }
        CompleteWriting();
        if (Volatile.Read(ref _threadFinished) == 0)
        {
            _thread.Join();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (!_readMode)
        {
            Volatile.Write(ref _producerCompleted, 1);
        }
        else
        {
            Volatile.Write(ref _stopRequested, 1);
        }
        if (Volatile.Read(ref _threadFinished) == 0)
        {
            _thread.Join();
        }
        _file.Dispose();
        if (_buffer != null)
        {
            NativeMemory.Free(_buffer);
            _buffer = null;
        }
    }

    private void RunReader()
    {
        try
        {
            while (Volatile.Read(ref _stopRequested) == 0)
            {
                long read = Volatile.Read(ref _consumerPosition);
                long write = _producerPosition;
                if (write == _totalFrameCount)
                {
                    Thread.Sleep(1);
                    continue;
                }
                int free = checked((int)(_capacityFrameCount - (write - read)));
                if (free == 0)
                {
                    Thread.Sleep(1);
                    continue;
                }
                int writeIndex = (int)(write % _capacityFrameCount);
                int frames = (int)Math.Min(
                    Math.Min(free, _capacityFrameCount - writeIndex),
                    _totalFrameCount - write);
                Span<byte> destination = new(
                    _buffer + (writeIndex * Format.BytesPerFrame),
                    checked(frames * Format.BytesPerFrame));
                ReadExactly(destination, checked(_payloadOffset + (write * Format.BytesPerFrame)));
                Volatile.Write(ref _producerPosition, write + frames);
            }
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _fault, exception);
        }
        finally
        {
            Volatile.Write(ref _threadFinished, 1);
        }
    }

    private void RunWriter()
    {
        try
        {
            while (true)
            {
                long read = _consumerPosition;
                long write = Volatile.Read(ref _producerPosition);
                if (read == write)
                {
                    if (Volatile.Read(ref _producerCompleted) != 0
                        || Volatile.Read(ref _stopRequested) != 0)
                    {
                        break;
                    }
                    Thread.Sleep(1);
                    continue;
                }
                int readIndex = (int)(read % _capacityFrameCount);
                int frames = (int)Math.Min(write - read, _capacityFrameCount - readIndex);
                ReadOnlySpan<byte> source = new(
                    _buffer + (readIndex * Format.BytesPerFrame),
                    checked(frames * Format.BytesPerFrame));
                RandomAccess.Write(
                    _handle,
                    source,
                    checked(_payloadOffset + (read * Format.BytesPerFrame)));
                Volatile.Write(ref _consumerPosition, read + frames);
            }
            _file.Flush(flushToDisk: true);
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _fault, exception);
        }
        finally
        {
            Volatile.Write(ref _threadFinished, 1);
        }
    }

    private void ReadExactly(Span<byte> destination, long fileOffset)
    {
        int completed = 0;
        while (completed < destination.Length)
        {
            int read = RandomAccess.Read(
                _handle,
                destination[completed..],
                checked(fileOffset + completed));
            if (read == 0)
            {
                throw new EndOfStreamException("The asynchronous PCM cache payload ended early.");
            }
            completed += read;
        }
    }

    private void CopyFromRing(float* destination, long position, int frameCount)
    {
        int index = (int)(position % _capacityFrameCount);
        int first = Math.Min(frameCount, _capacityFrameCount - index);
        CopyFrames(_buffer + (index * Format.BytesPerFrame), (byte*)destination, first);
        int remaining = frameCount - first;
        if (remaining != 0)
        {
            CopyFrames(_buffer, (byte*)(destination + (first * Format.ChannelCount)), remaining);
        }
    }

    private void CopyIntoRing(float* source, long position, int frameCount)
    {
        int index = (int)(position % _capacityFrameCount);
        int first = Math.Min(frameCount, _capacityFrameCount - index);
        CopyFrames((byte*)source, _buffer + (index * Format.BytesPerFrame), first);
        int remaining = frameCount - first;
        if (remaining != 0)
        {
            CopyFrames(
                (byte*)(source + (first * Format.ChannelCount)),
                _buffer,
                remaining);
        }
    }

    private void CopyFrames(byte* source, byte* destination, int frameCount)
    {
        NativeMemory.Copy(
            source,
            destination,
            checked((nuint)frameCount * (nuint)Format.BytesPerFrame));
    }
}
