using Midora.AudioDevice;
using Midora.Midi;
using System.Runtime.InteropServices;

namespace Midora.Audio.Bass.Tests;

public sealed class RollingPreparationRenderSourceTests
{
    [Fact]
    public unsafe void LongSegmentStartsAfterStartupWatermarkWithoutWaitingForWholeRange()
    {
        const int sampleRate = 1_000;
        const long totalFrames = 100_000;
        using CountingSource underlying = new(sampleRate, totalFrames);
        using RollingPreparationRenderSource source = new(
            underlying,
            totalFrames,
            TimeSpan.FromSeconds(5));

        Assert.True(source.PreparedFrameCount >= 2_000);
        Assert.True(underlying.PositionFrames < totalFrames);

        float* frames = stackalloc float[256 * 2];
        AudioPullResult result = source.PullFrames(frames, 256);

        Assert.Equal(AudioPullStatus.Continue, result.Status);
        Assert.Equal(256, result.FrameCount);
        Assert.Equal(256, source.PositionFrames);
    }

    [Theory]
    [InlineData(8L * 1024 * 1024 * 1024, 128L * 1024 * 1024)]
    [InlineData(16L * 1024 * 1024 * 1024, 256L * 1024 * 1024)]
    [InlineData(128L * 1024 * 1024 * 1024, 512L * 1024 * 1024)]
    public void RamBacklogUsesOneSixtyFourthWithApprovedClamp(
        long physicalBytes,
        long expectedBytes)
    {
        Assert.Equal(
            expectedBytes,
            RollingAudioPreparationPolicy.ComputeRamBlockPoolBytes(physicalBytes));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 2)]
    [InlineData(8, 4)]
    [InlineData(64, 4)]
    public void ProducerConcurrencyUsesApprovedCpuBound(int logicalProcessors, int expected)
    {
        Assert.Equal(
            expected,
            RollingAudioPreparationPolicy.ComputeInitialSegmentProducerConcurrency(
                logicalProcessors));
    }

    [Theory]
    [InlineData(8, 48_000, 100_000_000, 4)]
    [InlineData(8, 192_000, 1_000_000, 1)]
    [InlineData(4, 48_000, 0, 2)]
    public void ProducerConcurrencyHonorsHalfMeasuredWriterBandwidth(
        int logicalProcessors,
        int sampleRate,
        long writeBytesPerSecond,
        int expected)
    {
        Assert.Equal(
            expected,
            RollingAudioPreparationPolicy.ComputeSegmentProducerConcurrency(
                logicalProcessors,
                sampleRate,
                writeBytesPerSecond));
    }

    [Fact]
    public unsafe void MonitoringResetFadesInOnlyTheReplacementGeneration()
    {
        const int sampleRate = 1_000;
        using ResettableSource underlying = new(sampleRate, totalFrames: 20_000);
        using RollingPreparationRenderSource source = new(
            underlying,
            totalFrameCount: 20_000,
            TimeSpan.FromSeconds(5));

        float* before = stackalloc float[16 * 2];
        Assert.Equal(16, source.PullFrames(before, 16).FrameCount);
        source.ResetForMonitoringColdStart(TimeSpan.FromSeconds(5));
        Assert.True(SpinWait.SpinUntil(
            () => source.PreparedFrameCount >= 2_000,
            TimeSpan.FromSeconds(5)));

        float* after = stackalloc float[4 * 2];
        Assert.Equal(4, source.PullFrames(after, 4).FrameCount);
        Assert.Equal(0f, after[0]);
        Assert.Equal(2f / 3f, after[2]);
        Assert.Equal(4f / 3f, after[4]);
        Assert.Equal(2f, after[6]);
    }

    [Fact]
    public unsafe void MonitoringBatchUsesOneColdStartAndReachesUnderlyingBeforeResume()
    {
        const int sampleRate = 1_000;
        using ResettableSource underlying = new(sampleRate, totalFrames: 20_000);
        using RollingPreparationRenderSource source = new(
            underlying,
            totalFrameCount: 20_000,
            TimeSpan.FromSeconds(5));
        float* before = stackalloc float[16 * 2];
        Assert.Equal(16, source.PullFrames(before, 16).FrameCount);
        MidiMonitoringCommand[] commands =
        [
            MidiMonitoringCommand.DisableSource(0),
            MidiMonitoringCommand.Send(0, MidiMessage.ControlChange(0, 123, 0)),
            MidiMonitoringCommand.Send(0, MidiMessage.ControlChange(0, 120, 0))
        ];

        Assert.True(source.TryResetForMonitoringColdStart(
            TimeSpan.FromSeconds(5),
            commands,
            static () => false));

        Assert.Equal(1, underlying.ResetCount);
        Assert.Equal(commands, underlying.LastMonitoringCommands);
    }

    [Fact]
    public unsafe void ExplicitMonitoringConsumerFrontierDropsSpeculativePreparedFuture()
    {
        const int sampleRate = 1_000;
        using ResettableSource underlying = new(sampleRate, totalFrames: 20_000);
        using RollingPreparationRenderSource source = new(
            underlying,
            totalFrameCount: 20_000,
            TimeSpan.FromSeconds(5));
        float* consumed = stackalloc float[16 * 2];
        Assert.Equal(16, source.PullFrames(consumed, 16).FrameCount);
        Assert.True(underlying.PositionFrames > source.PositionFrames);

        Assert.True(source.TryResetForMonitoringColdStart(
            consumerFrontierFrame: 4,
            TimeSpan.FromSeconds(5),
            [],
            static () => false));

        Assert.Equal(4, source.PositionFrames);
        Assert.Equal(4, underlying.LastResetFrontier);
    }

    [Fact]
    public unsafe void MonitoringResetRestartsProducerThatAlreadyPreparedTheWholeRange()
    {
        const int sampleRate = 1_000;
        const long totalFrames = 1_000;
        using ResettableSource underlying = new(sampleRate, totalFrames);
        using RollingPreparationRenderSource source = new(
            underlying,
            totalFrames,
            TimeSpan.FromSeconds(5));
        Assert.True(SpinWait.SpinUntil(
            () => source.PreparedFrameCount == totalFrames,
            TimeSpan.FromSeconds(5)));

        float* before = stackalloc float[16 * 2];
        Assert.Equal(16, source.PullFrames(before, 16).FrameCount);

        source.ResetForMonitoringColdStart(TimeSpan.FromSeconds(5));
        Assert.True(SpinWait.SpinUntil(
            () => source.PreparedFrameCount == totalFrames - 16,
            TimeSpan.FromSeconds(5)));
        float* afterFirstReset = stackalloc float[4 * 2];
        Assert.Equal(4, source.PullFrames(afterFirstReset, 4).FrameCount);
        Assert.Equal(0f, afterFirstReset[0]);
        Assert.Equal(2f / 3f, afterFirstReset[2]);
        Assert.Equal(4f / 3f, afterFirstReset[4]);
        Assert.Equal(2f, afterFirstReset[6]);

        source.ResetForMonitoringColdStart(TimeSpan.FromSeconds(5));
        Assert.True(SpinWait.SpinUntil(
            () => source.PreparedFrameCount == totalFrames - 20,
            TimeSpan.FromSeconds(5)));
        float* afterSecondReset = stackalloc float[4 * 2];
        Assert.Equal(4, source.PullFrames(afterSecondReset, 4).FrameCount);
        Assert.Equal(0f, afterSecondReset[0]);
        Assert.Equal(1f, afterSecondReset[2]);
        Assert.Equal(2f, afterSecondReset[4]);
        Assert.Equal(3f, afterSecondReset[6]);
    }

    private sealed unsafe class CountingSource(int sampleRate, long totalFrames)
        : IAudioRenderSource, IDisposable
    {
        private long _positionFrames;

        public AudioFormat Format { get; } = new(
            sampleRate,
            2,
            AudioSampleFormat.Float32);

        public long PositionFrames => Volatile.Read(ref _positionFrames);

        public AudioPullResult PullFrames(float* destination, int requestedFrameCount)
        {
            long remaining = totalFrames - _positionFrames;
            int count = (int)Math.Min(requestedFrameCount, remaining);
            NativeMemory.Clear(
                destination,
                checked((nuint)count * (nuint)Format.BytesPerFrame));
            _positionFrames += count;
            return _positionFrames == totalFrames
                ? AudioPullResult.EndOfStream(count)
                : AudioPullResult.Continue(count);
        }

        public void Dispose()
        {
        }
    }

    private sealed unsafe class ResettableSource(int sampleRate, long totalFrames)
        : IAudioRenderSource, IMonitoringResettableRenderSource, IDisposable
    {
        private long _positionFrames;
        private int _generation = 1;

        public AudioFormat Format { get; } = new(sampleRate, 2, AudioSampleFormat.Float32);
        public long PositionFrames => Volatile.Read(ref _positionFrames);

        public int ResetCount { get; private set; }

        public MidiMonitoringCommand[] LastMonitoringCommands { get; private set; } = [];
        public long LastResetFrontier { get; private set; }

        public AudioPullResult PullFrames(float* destination, int requestedFrameCount)
        {
            int count = (int)Math.Min(requestedFrameCount, totalFrames - _positionFrames);
            float value = Volatile.Read(ref _generation);
            for (int i = 0; i < count * 2; i++)
            {
                destination[i] = value;
            }
            _positionFrames += count;
            return _positionFrames == totalFrames
                ? AudioPullResult.EndOfStream(count)
                : AudioPullResult.Continue(count);
        }

        public void ResetForMonitoringColdStart(
            long producerFrontierFrame,
            ReadOnlySpan<MidiMonitoringCommand> commands)
        {
            _positionFrames = producerFrontierFrame;
            LastResetFrontier = producerFrontierFrame;
            Interlocked.Increment(ref _generation);
            ResetCount++;
            LastMonitoringCommands = commands.ToArray();
        }

        public void Dispose()
        {
        }
    }
}
