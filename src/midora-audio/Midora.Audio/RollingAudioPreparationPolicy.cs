using System.Runtime.InteropServices;

namespace Midora.Audio;

public static class RollingAudioPreparationPolicy
{
    public const int SegmentBlockFrameCount = 16_384;
    public const int StartupMilliseconds = 2_000;
    public const int LowWatermarkMilliseconds = 750;
    public const int ResumeWatermarkMilliseconds = 2_000;
    public const int TargetHighWatermarkMilliseconds = 6_000;
    public const long MinimumRamBlockPoolBytes = 128L * 1024 * 1024;
    public const long MaximumRamBlockPoolBytes = 512L * 1024 * 1024;
    public const long MinimumSequentialReadBytesPerSecond = 200L * 1024 * 1024;
    public const long MinimumSequentialWriteBytesPerSecond = 100L * 1024 * 1024;

    public static long GetPhysicalMemoryBytes()
    {
        if (OperatingSystem.IsWindows())
        {
            MemoryStatusEx status = new()
            {
                Length = checked((uint)Marshal.SizeOf<MemoryStatusEx>())
            };
            if (GlobalMemoryStatusEx(ref status) && status.TotalPhysicalBytes != 0)
            {
                return status.TotalPhysicalBytes > long.MaxValue
                    ? long.MaxValue
                    : checked((long)status.TotalPhysicalBytes);
            }
        }

        long availableBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (availableBytes <= 0)
        {
            throw new InvalidOperationException(
                "The operating system did not report a usable physical-memory size.");
        }
        return availableBytes;
    }

    public static long ComputeRamBlockPoolBytesForCurrentMachine() =>
        ComputeRamBlockPoolBytes(GetPhysicalMemoryBytes());

    public static long ComputeRamBlockPoolBytes(long physicalMemoryBytes)
    {
        if (physicalMemoryBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalMemoryBytes));
        }
        return Math.Clamp(
            physicalMemoryBytes / 64,
            MinimumRamBlockPoolBytes,
            MaximumRamBlockPoolBytes);
    }

    public static int ComputeInitialSegmentProducerConcurrency(int logicalProcessorCount)
    {
        if (logicalProcessorCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(logicalProcessorCount));
        }
        return Math.Min(4, Math.Max(1, logicalProcessorCount / 2));
    }

    public static int ComputeSegmentProducerConcurrency(
        int logicalProcessorCount,
        int sampleRate,
        long measuredSequentialWriteBytesPerSecond)
    {
        if (sampleRate <= 0 || measuredSequentialWriteBytesPerSecond < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }
        int cpuBound = ComputeInitialSegmentProducerConcurrency(logicalProcessorCount);
        if (measuredSequentialWriteBytesPerSecond == 0)
        {
            return cpuBound;
        }
        long bytesPerProducerSecond = checked((long)sampleRate * 2 * sizeof(float));
        long writerBudget = measuredSequentialWriteBytesPerSecond / 2;
        int bandwidthBound = checked((int)Math.Min(
            int.MaxValue,
            Math.Max(1, writerBudget / bytesPerProducerSecond)));
        return Math.Min(cpuBound, bandwidthBound);
    }

    public static int MillisecondsToFrames(int sampleRate, int milliseconds)
    {
        if (sampleRate <= 0 || milliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }
        return checked((int)Math.Min(
            int.MaxValue,
            ((long)sampleRate * milliseconds + 999) / 1_000));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysicalBytes;
        public ulong AvailablePhysicalBytes;
        public ulong TotalPageFileBytes;
        public ulong AvailablePageFileBytes;
        public ulong TotalVirtualBytes;
        public ulong AvailableVirtualBytes;
        public ulong AvailableExtendedVirtualBytes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
