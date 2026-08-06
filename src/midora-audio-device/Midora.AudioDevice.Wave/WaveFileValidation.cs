using System.Buffers.Binary;

namespace Midora.AudioDevice.Wave;

public static class WaveFileValidation
{
    public static WaveFileSize ValidateInitialReleaseFile(
        string path,
        int expectedSampleRate,
        long expectedFrameCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (expectedSampleRate is < 8_000 or > 192_000)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedSampleRate));
        }

        WaveFileSize expected = WaveFileSize.Calculate(expectedFrameCount);
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: WaveFileSize.HeaderByteCount,
            FileOptions.SequentialScan);
        if (stream.Length != expected.FileByteCount)
        {
            throw new InvalidDataException(
                $"The WAVE file length is {stream.Length}; expected {expected.FileByteCount} bytes.");
        }

        Span<byte> header = stackalloc byte[WaveFileSize.HeaderByteCount];
        stream.ReadExactly(header);
        RequireTag(header[0..4], "RIFF"u8, "RIFF");
        RequireTag(header[8..12], "WAVE"u8, "WAVE");
        RequireTag(header[12..16], "fmt "u8, "fmt");
        RequireTag(header[36..40], "fact"u8, "fact");
        RequireTag(header[48..52], "data"u8, "data");
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]) != expected.RiffChunkSize
            || BinaryPrimitives.ReadUInt32LittleEndian(header[16..20]) != 16
            || BinaryPrimitives.ReadUInt16LittleEndian(header[20..22]) != 3
            || BinaryPrimitives.ReadUInt16LittleEndian(header[22..24]) != 2
            || BinaryPrimitives.ReadUInt32LittleEndian(header[24..28]) != expectedSampleRate
            || BinaryPrimitives.ReadUInt32LittleEndian(header[28..32])
                != checked((uint)(expectedSampleRate * WaveFileSize.StereoFloat32BytesPerFrame))
            || BinaryPrimitives.ReadUInt16LittleEndian(header[32..34])
                != WaveFileSize.StereoFloat32BytesPerFrame
            || BinaryPrimitives.ReadUInt16LittleEndian(header[34..36]) != 32
            || BinaryPrimitives.ReadUInt32LittleEndian(header[40..44]) != 4
            || BinaryPrimitives.ReadUInt32LittleEndian(header[44..48]) != expected.FrameCount
            || BinaryPrimitives.ReadUInt32LittleEndian(header[52..56]) != expected.DataByteCount)
        {
            throw new InvalidDataException("The WAVE header does not match the frozen initial-release format.");
        }

        return expected;
    }

    private static void RequireTag(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected, string name)
    {
        if (!actual.SequenceEqual(expected))
        {
            throw new InvalidDataException($"The required {name} WAVE chunk is missing or malformed.");
        }
    }
}
