using System.Diagnostics;

namespace Midora.Audio;

public readonly record struct AudioCacheStorageBenchmarkResult(
    long SequentialReadBytesPerSecond,
    long SequentialWriteBytesPerSecond,
    bool MeetsMinimumRequirement)
{
    public static AudioCacheStorageBenchmarkResult NotMeasured => default;
}

public static class AudioCacheStorageBenchmark
{
    private const int BenchmarkByteCount = 64 * 1024 * 1024;
    private const int BufferByteCount = 1024 * 1024;

    public static AudioCacheStorageBenchmarkResult Measure(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string fullPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullPath);
        string path = Path.Combine(fullPath, $"cache-benchmark-{Guid.NewGuid():N}.tmp");
        byte[] buffer = new byte[BufferByteCount];
        Random.Shared.NextBytes(buffer);
        try
        {
            long writeStart = Stopwatch.GetTimestamp();
            using (FileStream writer = new(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferByteCount,
                FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                for (int offset = 0; offset < BenchmarkByteCount; offset += buffer.Length)
                {
                    writer.Write(buffer);
                }
                writer.Flush(flushToDisk: true);
            }
            TimeSpan writeElapsed = Stopwatch.GetElapsedTime(writeStart);

            long readStart = Stopwatch.GetTimestamp();
            using (FileStream reader = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferByteCount,
                FileOptions.SequentialScan))
            {
                int total = 0;
                while (total < BenchmarkByteCount)
                {
                    int read = reader.Read(buffer);
                    if (read == 0)
                    {
                        throw new EndOfStreamException(
                            "The audio cache storage benchmark file ended early.");
                    }
                    total += read;
                }
            }
            TimeSpan readElapsed = Stopwatch.GetElapsedTime(readStart);
            long writeBytesPerSecond = ToBytesPerSecond(BenchmarkByteCount, writeElapsed);
            long readBytesPerSecond = ToBytesPerSecond(BenchmarkByteCount, readElapsed);
            return new(
                readBytesPerSecond,
                writeBytesPerSecond,
                readBytesPerSecond
                    >= RollingAudioPreparationPolicy.MinimumSequentialReadBytesPerSecond
                && writeBytesPerSecond
                    >= RollingAudioPreparationPolicy.MinimumSequentialWriteBytesPerSecond);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static long ToBytesPerSecond(long bytes, TimeSpan elapsed) =>
        elapsed <= TimeSpan.Zero
            ? long.MaxValue
            : checked((long)Math.Round(bytes / elapsed.TotalSeconds));
}
