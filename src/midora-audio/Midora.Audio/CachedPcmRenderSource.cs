using Midora.AudioDevice;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Midora.Audio;

public static class AudioPcmCachePayload
{
    private const uint Magic = 0x4D435043; // CPCM, little-endian.
    public const int Version = 1;
    public const int HeaderByteCount = 32;

    public static byte[] Encode(AudioFormat format, ReadOnlySpan<float> interleavedSamples)
    {
        ValidateFormat(format);
        if (interleavedSamples.Length % format.ChannelCount != 0)
        {
            throw new ArgumentException(
                "The PCM sample count is not aligned to complete audio frames.",
                nameof(interleavedSamples));
        }
        if (interleavedSamples.ContainsAnyExceptInRange(float.MinValue, float.MaxValue))
        {
            throw new ArgumentException("Cached PCM samples must all be finite.", nameof(interleavedSamples));
        }

        long frameCount = interleavedSamples.Length / format.ChannelCount;
        byte[] payload = new byte[checked(HeaderByteCount + (interleavedSamples.Length * sizeof(float)))];
        WriteHeader(payload.AsSpan(0, HeaderByteCount), format, frameCount);
        MemoryMarshal.AsBytes(interleavedSamples).CopyTo(payload.AsSpan(HeaderByteCount));
        return payload;
    }

    public static CachedPcmRenderSource Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderByteCount)
        {
            throw new InvalidDataException("The cached PCM payload is truncated.");
        }
        ReadOnlySpan<byte> header = payload[..HeaderByteCount];
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic
            || BinaryPrimitives.ReadInt32LittleEndian(header[4..]) != Version
            || BinaryPrimitives.ReadInt32LittleEndian(header[28..]) != 0)
        {
            throw new InvalidDataException("The cached PCM payload header is invalid.");
        }

        AudioFormat format = new(
            BinaryPrimitives.ReadInt32LittleEndian(header[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(header[12..]),
            (AudioSampleFormat)BinaryPrimitives.ReadInt32LittleEndian(header[16..]));
        try
        {
            ValidateFormat(format);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The cached PCM format is invalid.", exception);
        }
        long frameCount = BinaryPrimitives.ReadInt64LittleEndian(header[20..]);
        long expectedSampleCount;
        long expectedByteCount;
        try
        {
            expectedSampleCount = checked(frameCount * format.ChannelCount);
            expectedByteCount = checked(HeaderByteCount + (expectedSampleCount * sizeof(float)));
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("The cached PCM length overflows its bounded format.", exception);
        }
        if (frameCount < 0
            || expectedSampleCount > int.MaxValue
            || expectedByteCount != payload.Length)
        {
            throw new InvalidDataException("The cached PCM payload length is invalid.");
        }

        float[] samples = new float[(int)expectedSampleCount];
        payload[HeaderByteCount..].CopyTo(MemoryMarshal.AsBytes(samples.AsSpan()));
        if (samples.AsSpan().ContainsAnyExceptInRange(float.MinValue, float.MaxValue))
        {
            throw new InvalidDataException("The cached PCM payload contains a non-finite sample.");
        }
        return new CachedPcmRenderSource(format, samples);
    }

    public static void WriteHeader(Span<byte> destination, AudioFormat format, long frameCount)
    {
        ValidateFormat(format);
        if (destination.Length < HeaderByteCount)
        {
            throw new ArgumentException("The PCM cache header destination is too small.", nameof(destination));
        }
        if (frameCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        }
        _ = checked(HeaderByteCount + (frameCount * format.BytesPerFrame));
        BinaryPrimitives.WriteUInt32LittleEndian(destination, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(destination[4..], Version);
        BinaryPrimitives.WriteInt32LittleEndian(destination[8..], format.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(destination[12..], format.ChannelCount);
        BinaryPrimitives.WriteInt32LittleEndian(destination[16..], (int)format.SampleFormat);
        BinaryPrimitives.WriteInt64LittleEndian(destination[20..], frameCount);
        BinaryPrimitives.WriteInt32LittleEndian(destination[28..], 0);
    }

    public static long ValidateHeader(ReadOnlySpan<byte> header, AudioFormat expectedFormat)
    {
        if (header.Length < HeaderByteCount
            || BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic
            || BinaryPrimitives.ReadInt32LittleEndian(header[4..]) != Version
            || BinaryPrimitives.ReadInt32LittleEndian(header[28..]) != 0)
        {
            throw new InvalidDataException("The cached PCM payload header is invalid.");
        }
        AudioFormat actual = new(
            BinaryPrimitives.ReadInt32LittleEndian(header[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(header[12..]),
            (AudioSampleFormat)BinaryPrimitives.ReadInt32LittleEndian(header[16..]));
        try
        {
            ValidateFormat(actual);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The cached PCM format is invalid.", exception);
        }
        if (actual != expectedFormat)
        {
            throw new InvalidDataException("The cached PCM payload format does not match the render plan.");
        }
        long frameCount = BinaryPrimitives.ReadInt64LittleEndian(header[20..]);
        if (frameCount < 0)
        {
            throw new InvalidDataException("The cached PCM payload frame count is invalid.");
        }
        return frameCount;
    }

    private static void ValidateFormat(AudioFormat format)
    {
        format.Validate();
        if (format.ChannelCount != 2 || format.SampleFormat != AudioSampleFormat.Float32)
        {
            throw new ArgumentException(
                "The initial-release PCM cache requires stereo interleaved float32 audio.",
                nameof(format));
        }
    }
}

public sealed unsafe class CachedPcmRenderSource : IAudioRenderSource
{
    private readonly float[] _samples;
    private long _positionFrames;

    internal CachedPcmRenderSource(AudioFormat format, float[] samples)
    {
        Format = format;
        _samples = samples;
    }

    public AudioFormat Format { get; }
    public long PositionFrames => Volatile.Read(ref _positionFrames);
    public long TotalFrameCount => _samples.LongLength / Format.ChannelCount;

    public AudioPullResult PullFrames(float* destination, int requestedFrameCount)
    {
        if (destination == null || requestedFrameCount < 0)
        {
            return AudioPullResult.Fault();
        }
        long position = _positionFrames;
        int frameCount = (int)Math.Min(requestedFrameCount, TotalFrameCount - position);
        if (frameCount != 0)
        {
            int sampleOffset = checked((int)position * Format.ChannelCount);
            nuint byteCount = checked((nuint)frameCount * (nuint)Format.BytesPerFrame);
            fixed (float* source = &_samples[sampleOffset])
            {
                NativeMemory.Copy(source, destination, byteCount);
            }
            Volatile.Write(ref _positionFrames, position + frameCount);
        }
        return position + frameCount == TotalFrameCount
            ? AudioPullResult.EndOfStream(frameCount)
            : AudioPullResult.Continue(frameCount);
    }
}
