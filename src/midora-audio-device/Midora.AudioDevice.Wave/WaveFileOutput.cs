using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;

namespace Midora.AudioDevice.Wave;

public static unsafe partial class WaveFileOutput
{
    private const uint GenericWrite = 0x40000000;
    private const uint CreateNew = 1;
    private const uint FileAttributeNormal = 0x80;
    private const uint FileFlagSequentialScan = 0x08000000;
    private static readonly nint InvalidHandleValue = -1;

    public static WaveFileRenderResult Render(
        IAudioRenderSource source,
        long frameCount,
        string targetPath,
        int workFrameCount,
        bool overwrite,
        CancellationToken cancellationToken = default,
        IWaveFileRenderMonitor? monitor = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        AudioFormat format = source.Format;
        format.Validate();
        if (format.ChannelCount != 2 || format.SampleFormat != AudioSampleFormat.Float32)
        {
            throw new ArgumentException("WAVE output requires stereo interleaved float32 input.", nameof(source));
        }

        if (format.SampleRate is < 8_000 or > 192_000)
        {
            throw new ArgumentOutOfRangeException(nameof(source), "The WAVE sample rate must be 8,000-192,000 Hz.");
        }

        if (workFrameCount is < 16 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(workFrameCount));
        }

        WaveFileSize size = WaveFileSize.Calculate(frameCount);
        string fullTargetPath = Path.GetFullPath(targetPath);
        string? directory = Path.GetDirectoryName(fullTargetPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(directory);
        }

        if (File.Exists(fullTargetPath) && !overwrite)
        {
            throw new IOException($"The target file already exists: {fullTargetPath}");
        }

        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullTargetPath)}.{Guid.NewGuid():N}.tmp");
        nuint bufferByteCount = checked((nuint)workFrameCount * WaveFileSize.StereoFloat32BytesPerFrame);
        float* buffer = (float*)NativeMemory.Alloc(bufferByteCount);
        nint fileHandle = InvalidHandleValue;
        long renderedFrames = 0;
        int failureCode = 0;
        WaveRenderFailure failure = WaveRenderFailure.None;
        long renderingAllocatedBytes = 0;
        long sourcePullAllocatedBytes = 0;
        long sampleWriteAllocatedBytes = 0;
        AudioPullStatus shortReadStatus = AudioPullStatus.Continue;
        int shortReadFrameCount = 0;
        int shortReadRequestedFrameCount = 0;
        Exception? unexpectedFailure = null;
        Exception? cleanupFailure = null;

        try
        {
            fileHandle = CreateFile(
                temporaryPath,
                GenericWrite,
                0,
                null,
                CreateNew,
                FileAttributeNormal | FileFlagSequentialScan,
                0);

            if (fileHandle == InvalidHandleValue)
            {
                throw new IOException(
                    $"Could not create the temporary WAVE file; Win32 error {Marshal.GetLastPInvokeError()}.");
            }

            Span<byte> header = stackalloc byte[WaveFileSize.HeaderByteCount];
            WriteHeader(header, format.SampleRate, size);
            fixed (byte* headerPointer = header)
            {
                if (!TryWriteAll(fileHandle, headerPointer, WaveFileSize.HeaderByteCount, out failureCode))
                {
                    throw new IOException($"Could not write the WAVE header; Win32 error {failureCode}.");
                }
            }

            AudioPullResult warmupPull = source.PullFrames(buffer, 0);
            if (warmupPull.FrameCount != 0 || warmupPull.Status == AudioPullStatus.Fault)
            {
                throw new MidoraAudioDeviceException("The audio source failed its zero-frame Preparing warm-up.");
            }

            long allocatedBeforeRendering = GC.GetAllocatedBytesForCurrentThread();
            while (renderedFrames < frameCount)
            {
                if (cancellationToken.IsCancellationRequested
                    || monitor?.IsCancellationRequested == true)
                {
                    failure = WaveRenderFailure.Cancelled;
                    break;
                }

                int requestedFrames = (int)Math.Min(workFrameCount, frameCount - renderedFrames);
                long allocatedBeforePull = GC.GetAllocatedBytesForCurrentThread();
                AudioPullResult pull = source.PullFrames(buffer, requestedFrames);
                sourcePullAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocatedBeforePull;
                if (pull.Status == AudioPullStatus.Buffering)
                {
                    // A reusable PCM cache is fed by a dedicated bounded I/O
                    // thread. Offline rendering has no device deadline, so wait
                    // without advancing the output frame rather than treating
                    // cache read-ahead backpressure as a semantic source failure.
                    Thread.Sleep(1);
                    continue;
                }

                if (!pull.IsValidForRequest(requestedFrames)
                    || pull.Status == AudioPullStatus.Fault)
                {
                    failure = WaveRenderFailure.SourceFault;
                    shortReadStatus = pull.Status;
                    shortReadFrameCount = pull.FrameCount;
                    shortReadRequestedFrameCount = requestedFrames;
                    break;
                }

                if (pull.FrameCount == 0)
                {
                    failure = WaveRenderFailure.SourceShortRead;
                    shortReadStatus = pull.Status;
                    shortReadFrameCount = 0;
                    shortReadRequestedFrameCount = requestedFrames;
                    break;
                }

                if (ContainsNonFinite(buffer, pull.FrameCount * 2))
                {
                    failure = WaveRenderFailure.NonFiniteSample;
                    break;
                }

                uint byteCount = checked((uint)(pull.FrameCount * WaveFileSize.StereoFloat32BytesPerFrame));
                long allocatedBeforeWrite = GC.GetAllocatedBytesForCurrentThread();
                if (!TryWriteAll(fileHandle, buffer, byteCount, out failureCode))
                {
                    failure = WaveRenderFailure.WriteFailed;
                    break;
                }
                sampleWriteAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocatedBeforeWrite;

                renderedFrames += pull.FrameCount;
                monitor?.ReportRenderedFrames(renderedFrames);

                if (pull.Status == AudioPullStatus.EndOfStream && renderedFrames < frameCount)
                {
                    failure = WaveRenderFailure.SourceShortRead;
                    shortReadStatus = pull.Status;
                    shortReadFrameCount = pull.FrameCount;
                    shortReadRequestedFrameCount = requestedFrames;
                    break;
                }
            }
            renderingAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBeforeRendering;

            if (failure == WaveRenderFailure.None
                && (cancellationToken.IsCancellationRequested
                    || monitor?.IsCancellationRequested == true))
            {
                failure = WaveRenderFailure.Cancelled;
            }

            if (failure == WaveRenderFailure.None)
            {
                monitor?.BeginFinalizing();
            }

            if (failure == WaveRenderFailure.None && FlushFileBuffers(fileHandle) == 0)
            {
                failureCode = Marshal.GetLastPInvokeError();
                failure = WaveRenderFailure.FlushFailed;
            }
        }
        catch (Exception exception)
        {
            unexpectedFailure = exception;
        }
        finally
        {
            NativeMemory.Free(buffer);
            if (fileHandle != InvalidHandleValue)
            {
                if (CloseHandle(fileHandle) == 0)
                {
                    int error = Marshal.GetLastPInvokeError();
                    cleanupFailure = new IOException(
                        $"Closing the temporary WAVE file failed with Win32 error {error}.");
                }
            }
        }

        if (unexpectedFailure is not null)
        {
            TryDeleteTemporaryFile(temporaryPath);
            if (cleanupFailure is not null)
            {
                throw new AggregateException(
                    "WAVE rendering and temporary file cleanup both failed.",
                    unexpectedFailure,
                    cleanupFailure);
            }
            ExceptionDispatchInfo.Throw(unexpectedFailure);
        }

        if (failure != WaveRenderFailure.None)
        {
            TryDeleteTemporaryFile(temporaryPath);
            Exception renderFailure = CreateFinalizingException(
                failure,
                failureCode,
                renderedFrames,
                shortReadStatus,
                shortReadFrameCount,
                shortReadRequestedFrameCount);
            if (cleanupFailure is not null)
            {
                throw new AggregateException(
                    "WAVE rendering and temporary file cleanup both failed.",
                    renderFailure,
                    cleanupFailure);
            }
            throw renderFailure;
        }

        if (cleanupFailure is not null)
        {
            TryDeleteTemporaryFile(temporaryPath);
            throw cleanupFailure;
        }

        long actualLength = new FileInfo(temporaryPath).Length;
        if (actualLength != size.FileByteCount)
        {
            TryDeleteTemporaryFile(temporaryPath);
            throw new IOException(
                $"WAVE verification failed: expected {size.FileByteCount} bytes, got {actualLength} bytes.");
        }

        try
        {
            File.Move(temporaryPath, fullTargetPath, overwrite);
        }
        catch
        {
            TryDeleteTemporaryFile(temporaryPath);
            throw;
        }

        return new WaveFileRenderResult(
            renderedFrames,
            actualLength,
            renderingAllocatedBytes,
            sourcePullAllocatedBytes,
            sampleWriteAllocatedBytes);
    }

    private static void WriteHeader(Span<byte> header, int sampleRate, WaveFileSize size)
    {
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], size.RiffChunkSize);
        "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 3);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[28..],
            checked((uint)(sampleRate * WaveFileSize.StereoFloat32BytesPerFrame)));
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], WaveFileSize.StereoFloat32BytesPerFrame);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 32);
        "fact"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(header[44..], checked((uint)size.FrameCount));
        "data"u8.CopyTo(header[48..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[52..], size.DataByteCount);
    }

    private static bool TryWriteAll(nint fileHandle, void* buffer, uint byteCount, out int errorCode)
    {
        byte* current = (byte*)buffer;
        uint remaining = byteCount;

        while (remaining != 0)
        {
            uint written;
            if (WriteFile(fileHandle, current, remaining, &written, null) == 0 || written == 0)
            {
                errorCode = Marshal.GetLastPInvokeError();
                return false;
            }

            current += written;
            remaining -= written;
        }

        errorCode = 0;
        return true;
    }

    private static bool ContainsNonFinite(float* samples, int sampleCount)
    {
        for (int i = 0; i < sampleCount; i++)
        {
            if (!float.IsFinite(samples[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static Exception CreateFinalizingException(
        WaveRenderFailure failure,
        int nativeErrorCode,
        long renderedFrames,
        AudioPullStatus shortReadStatus,
        int shortReadFrameCount,
        int shortReadRequestedFrameCount)
    {
        return failure switch
        {
            WaveRenderFailure.Cancelled => new OperationCanceledException(
                $"WAVE rendering was cancelled after {renderedFrames} frames."),
            WaveRenderFailure.SourceFault => new MidoraAudioDeviceException(
                $"The audio source failed after {renderedFrames} frames."),
            WaveRenderFailure.SourceShortRead => new MidoraAudioDeviceException(
                $"The audio source ended early after {renderedFrames} frames; "
                + $"status={shortReadStatus}; requestedFrames={shortReadRequestedFrameCount}; "
                + $"returnedFrames={shortReadFrameCount}."),
            WaveRenderFailure.SourceBuffering => new MidoraAudioDeviceException(
                $"The audio source entered Buffering during file rendering after {renderedFrames} frames."),
            WaveRenderFailure.NonFiniteSample => new MidoraAudioDeviceException(
                $"The audio source produced NaN or Infinity after {renderedFrames} frames."),
            WaveRenderFailure.WriteFailed => new IOException(
                $"Writing WAVE samples failed with Win32 error {nativeErrorCode} after {renderedFrames} frames."),
            WaveRenderFailure.FlushFailed => new IOException(
                $"Flushing the WAVE file failed with Win32 error {nativeErrorCode}."),
            _ => new UnreachableException()
        };
    }

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch
        {
            // The original rendering failure remains primary. A later task cleanup can retry.
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        void* securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int WriteFile(
        nint file,
        void* buffer,
        uint numberOfBytesToWrite,
        uint* numberOfBytesWritten,
        void* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int FlushFileBuffers(nint file);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int CloseHandle(nint handle);

    private enum WaveRenderFailure : byte
    {
        None,
        Cancelled,
        SourceFault,
        SourceShortRead,
        SourceBuffering,
        NonFiniteSample,
        WriteFailed,
        FlushFailed
    }
}
