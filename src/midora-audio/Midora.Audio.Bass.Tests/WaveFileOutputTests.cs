using Midora.AudioDevice;
using Midora.AudioDevice.Wave;
using System.Buffers.Binary;

namespace Midora.Audio.Bass.Tests;

public sealed class WaveFileOutputTests
{
    public static TheoryData<int> ValidSampleRates => new()
    {
        8_000,
        44_100,
        48_000,
        50_123,
        192_000
    };

    [Fact]
    public void CalculatesExactRiffLimitBeforeCreatingAFile()
    {
        const long maximumFrameCount = (uint.MaxValue - 48L) / 8;

        WaveFileSize maximum = WaveFileSize.Calculate(maximumFrameCount);

        Assert.Equal(uint.MaxValue - 7, maximum.RiffChunkSize);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => WaveFileSize.Calculate(maximumFrameCount + 1));
    }

    [Theory]
    [MemberData(nameof(ValidSampleRates))]
    public void WritesFloat32StereoWaveWithExactHeaderAndNoRenderingAllocation(int sampleRate)
    {
        string directory = CreateOwnedTemporaryDirectory();
        string target = Path.Combine(directory, "test.wav");
        try
        {
            const int frames = 257;
            RampSource source = new(sampleRate, frames);

            WaveFileRenderResult result = WaveFileOutput.Render(
                source,
                frames,
                target,
                workFrameCount: 37,
                overwrite: false);

            byte[] bytes = File.ReadAllBytes(target);
            Assert.Equal(0, result.RenderingThreadAllocatedBytes);
            Assert.Equal(0, result.SourcePullAllocatedBytes);
            Assert.Equal(0, result.SampleWriteAllocatedBytes);
            Assert.Equal(WaveFileSize.HeaderByteCount + (frames * 8), bytes.Length);
            Assert.Equal("RIFF"u8.ToArray(), bytes.AsSpan(0, 4).ToArray());
            Assert.Equal("WAVE"u8.ToArray(), bytes.AsSpan(8, 4).ToArray());
            Assert.Equal("fmt "u8.ToArray(), bytes.AsSpan(12, 4).ToArray());
            Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(20, 2)));
            Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22, 2)));
            Assert.Equal(sampleRate, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24, 4)));
            Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(34, 2)));
            Assert.Equal("fact"u8.ToArray(), bytes.AsSpan(36, 4).ToArray());
            Assert.Equal(frames, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(44, 4)));
            Assert.Equal("data"u8.ToArray(), bytes.AsSpan(48, 4).ToArray());
            Assert.Equal(frames * 8, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(52, 4)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void RejectsNonFiniteSamplesAndDoesNotPublish(float invalidSample)
    {
        string directory = CreateOwnedTemporaryDirectory();
        string target = Path.Combine(directory, "invalid.wav");
        try
        {
            NonFiniteSource source = new(invalidSample);

            _ = Assert.Throws<MidoraAudioDeviceException>(() => WaveFileOutput.Render(
                source,
                32,
                target,
                workFrameCount: 16,
                overwrite: false));

            Assert.False(File.Exists(target));
            Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CancellationDoesNotPublishOrLeaveATemporaryFile()
    {
        string directory = CreateOwnedTemporaryDirectory();
        string target = Path.Combine(directory, "cancelled.wav");
        try
        {
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();
            RampSource source = new(48_000, 128);

            _ = Assert.Throws<OperationCanceledException>(() => WaveFileOutput.Render(
                source,
                128,
                target,
                workFrameCount: 16,
                overwrite: false,
                cancellation.Token));

            Assert.False(File.Exists(target));
            Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AllocationFreeMonitorReceivesProgressAndFinalizingWithoutChangingHotPathBudget()
    {
        string directory = CreateOwnedTemporaryDirectory();
        string target = Path.Combine(directory, "monitored.wav");
        try
        {
            const int frames = 257;
            RampSource source = new(48_000, frames);
            RecordingMonitor monitor = new();

            WaveFileRenderResult result = WaveFileOutput.Render(
                source,
                frames,
                target,
                workFrameCount: 37,
                overwrite: false,
                monitor: monitor);

            Assert.Equal(0, result.RenderingThreadAllocatedBytes);
            Assert.Equal(frames, monitor.LastRenderedFrame);
            Assert.True(monitor.FinalizingBegan);
            WaveFileValidation.ValidateInitialReleaseFile(target, 48_000, frames);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MonitorCancellationStopsBeforeFinalizingAndLeavesNoOutput()
    {
        string directory = CreateOwnedTemporaryDirectory();
        string target = Path.Combine(directory, "monitor-cancelled.wav");
        try
        {
            CancelAfterFirstBlockMonitor monitor = new();

            _ = Assert.Throws<OperationCanceledException>(() => WaveFileOutput.Render(
                new RampSource(48_000, 512),
                512,
                target,
                workFrameCount: 64,
                overwrite: false,
                monitor: monitor));

            Assert.False(monitor.FinalizingBegan);
            Assert.False(File.Exists(target));
            Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void StructuralValidatorRejectsMalformedOrUnexpectedWaveHeader()
    {
        string directory = CreateOwnedTemporaryDirectory();
        string target = Path.Combine(directory, "malformed.wav");
        try
        {
            File.WriteAllBytes(target, new byte[WaveFileSize.HeaderByteCount + 8]);

            _ = Assert.Throws<InvalidDataException>(() =>
                WaveFileValidation.ValidateInitialReleaseFile(target, 48_000, 1));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateOwnedTemporaryDirectory()
    {
        string result = Path.Combine(Path.GetTempPath(), $"midora-wave-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(result);
        return result;
    }

    private sealed unsafe class RampSource(int sampleRate, long totalFrames) : IAudioRenderSource
    {
        private long _position;

        public AudioFormat Format { get; } = new(sampleRate, 2, AudioSampleFormat.Float32);

        public AudioPullResult PullFrames(float* destination, int requestedFrameCount)
        {
            int count = (int)Math.Min(requestedFrameCount, totalFrames - _position);
            for (int i = 0; i < count; i++)
            {
                float value = (float)(_position + i) / Math.Max(1, totalFrames);
                destination[i * 2] = value;
                destination[(i * 2) + 1] = -value;
            }

            _position += count;
            return _position == totalFrames
                ? AudioPullResult.EndOfStream(count)
                : AudioPullResult.Continue(count);
        }
    }

    private sealed unsafe class NonFiniteSource(float value) : IAudioRenderSource
    {
        public AudioFormat Format => new(48_000, 2, AudioSampleFormat.Float32);

        public AudioPullResult PullFrames(float* destination, int requestedFrameCount)
        {
            for (int i = 0; i < requestedFrameCount * 2; i++)
            {
                destination[i] = i == 3 ? value : 0;
            }

            return AudioPullResult.Continue(requestedFrameCount);
        }
    }

    private sealed class RecordingMonitor : IWaveFileRenderMonitor
    {
        public long LastRenderedFrame { get; private set; }
        public bool FinalizingBegan { get; private set; }
        public bool IsCancellationRequested => false;
        public void ReportRenderedFrames(long renderedFrameCount) => LastRenderedFrame = renderedFrameCount;
        public void BeginFinalizing() => FinalizingBegan = true;
    }

    private sealed class CancelAfterFirstBlockMonitor : IWaveFileRenderMonitor
    {
        private bool _cancel;
        public bool FinalizingBegan { get; private set; }
        public bool IsCancellationRequested => _cancel;
        public void ReportRenderedFrames(long renderedFrameCount) => _cancel = renderedFrameCount != 0;
        public void BeginFinalizing() => FinalizingBegan = true;
    }
}
