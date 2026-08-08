using Midora.AudioDevice;
using System.Runtime.InteropServices;

namespace Midora.Audio.Bass.Tests;

public sealed class AudioFrameRingBufferTests
{
    [Fact]
    public unsafe void PreservesFramesAcrossWrapWithoutAllocating()
    {
        AudioFormat format = new(48_000, 2, AudioSampleFormat.Float32);
        using AudioFrameRingBuffer ring = new(format, 16);
        float* source = stackalloc float[24 * 2];
        float* destination = stackalloc float[24 * 2];

        FillFrames(source, 12, 0);
        Assert.True(ring.TryWriteFrames(source, 12));
        AudioPullResult firstPull = ring.PullFrames(destination, 8);
        Assert.Equal(8, firstPull.FrameCount);

        FillFrames(source, 10, 100);
        Assert.True(ring.TryWriteFrames(source, 10));

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        AudioPullResult secondPull = ring.PullFrames(destination, 14);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(0, allocated);
        Assert.Equal(14, secondPull.FrameCount);
        for (int frame = 0; frame < 4; frame++)
        {
            Assert.Equal(8 + frame, destination[frame * 2]);
        }

        for (int frame = 0; frame < 10; frame++)
        {
            Assert.Equal(100 + frame, destination[(frame + 4) * 2]);
        }
    }

    [Fact]
    public unsafe void UnderrunOutputsSilenceWithoutAdvancingMusicalFrames()
    {
        using AudioFrameRingBuffer ring = new(
            new AudioFormat(48_000, 2, AudioSampleFormat.Float32),
            16);
        float* source = stackalloc float[4] { 0.25f, -0.25f, 0.5f, -0.5f };
        Assert.True(ring.TryWriteFrames(source, 2));
        float* destination = stackalloc float[8];
        for (int i = 0; i < 8; i++)
        {
            destination[i] = 1;
        }

        AudioPullResult result = ring.PullFrames(destination, 4);

        Assert.Equal(AudioPullStatus.Buffering, result.Status);
        Assert.Equal(0, result.FrameCount);
        Assert.Equal(1, ring.UnderrunCount);
        Assert.Equal(2, ring.AvailableFrameCount);
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(0, destination[i]);
        }

        AudioPullResult stillLatched = ring.PullFrames(destination, 2);
        Assert.Equal(AudioPullStatus.Buffering, stillLatched.Status);
        Assert.Equal(0, stillLatched.FrameCount);
        Assert.Equal(2, ring.AvailableFrameCount);

        ring.ReleaseBuffering();
        AudioPullResult resumed = ring.PullFrames(destination, 2);
        Assert.Equal(AudioPullStatus.Continue, resumed.Status);
        Assert.Equal(2, resumed.FrameCount);
        Assert.Equal(0, ring.AvailableFrameCount);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(source[i], destination[i]);
        }
    }

    [Fact]
    public unsafe void RenderAheadWorkerFinishesWithNoManagedAllocation()
    {
        const int totalFrames = 1_024;
        CountingSource source = new(totalFrames);
        using AudioFrameRingBuffer ring = new(source.Format, totalFrames);
        using AudioRenderAheadWorker worker = new(source, ring, 64);

        worker.Start();
        Assert.True(SpinWait.SpinUntil(() => worker.IsFinished, TimeSpan.FromSeconds(5)));

        Assert.False(ring.ProducerFaulted);
        Assert.True(ring.ProducerCompleted);
        Assert.Equal(totalFrames, ring.AvailableFrameCount);
        Assert.Equal(0, worker.RenderingThreadAllocatedBytes);
    }

    [Fact]
    public void RenderAheadWorkerRetriesBufferingWithoutAdvancingOrAllocating()
    {
        BufferOnceSource source = new();
        using AudioFrameRingBuffer ring = new(source.Format, 64);
        using AudioRenderAheadWorker worker = new(source, ring, 64);

        worker.Start();
        Assert.True(SpinWait.SpinUntil(() => worker.IsFinished, TimeSpan.FromSeconds(5)));

        Assert.Equal(2, source.PullCount);
        Assert.False(ring.ProducerFaulted);
        Assert.True(ring.ProducerCompleted);
        Assert.Equal(64, ring.AvailableFrameCount);
        Assert.Equal(0, worker.RenderingThreadAllocatedBytes);
    }

    [Fact]
    public void RenderAheadWorkerFaultsOnUnknownPullStatusWithoutAllocating()
    {
        UnknownStatusSource source = new();
        using AudioFrameRingBuffer ring = new(source.Format, 64);
        using AudioRenderAheadWorker worker = new(source, ring, 64);

        worker.Start();
        Assert.True(SpinWait.SpinUntil(() => worker.IsFinished, TimeSpan.FromSeconds(5)));

        Assert.True(ring.ProducerFaulted);
        Assert.False(ring.ProducerCompleted);
        Assert.Equal(0, ring.AvailableFrameCount);
        Assert.Equal(0, worker.RenderingThreadAllocatedBytes);
    }

    [Fact]
    public unsafe void RecoverySourcePreparesCompleteSpanBeforeRingIsReleased()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"midora-recovery-source-{Guid.NewGuid():N}.spool");
        try
        {
            using (FileStream spool = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                spool.SetLength(32 * 2 * sizeof(float));
            }
            CountingSource source = new(64);
            using BufferingRecoveryRenderSource recovery = new(source, path, 16);
            using AudioFrameRingBuffer ring = new(source.Format, 16);
            float* work = stackalloc float[32 * 2];

            Assert.Equal(8, recovery.PullFrames(work, 8).FrameCount);
            Assert.True(ring.TryWriteFrames(work, 8));
            Assert.Equal(6, ring.PullFrames(work, 6).FrameCount);
            Assert.Equal(AudioPullStatus.Buffering, ring.PullFrames(work, 4).Status);
            Assert.Equal(6, ring.ReadPositionFrame);
            Assert.Equal(8, source.Position);

            long before = GC.GetAllocatedBytesForCurrentThread();
            recovery.PrepareRecovery(ring, recoveryEndFrame: 20);
            AudioPullResult replay = recovery.PullFrames(work, 14);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, allocated);
            Assert.Equal(20, source.Position);
            Assert.False(recovery.IsReplaying);
            Assert.True(ring.IsBuffering);
            Assert.Equal(0, ring.AvailableFrameCount);
            Assert.Equal(14, replay.FrameCount);
            for (int frame = 0; frame < 14; frame++)
            {
                Assert.Equal(6 + frame, work[frame * 2]);
                Assert.Equal(-(6 + frame), work[(frame * 2) + 1]);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public unsafe void InMemoryRecoveryFallbackPreservesTheSameCompleteSpanSemantics()
    {
        CountingSource source = new(64);
        using BufferingRecoveryRenderSource recovery = new(
            source,
            capacityFrameCount: 32,
            workFrameCount: 16);
        using AudioFrameRingBuffer ring = new(source.Format, 16);
        float* work = stackalloc float[32 * 2];

        Assert.Equal(8, recovery.PullFrames(work, 8).FrameCount);
        Assert.True(ring.TryWriteFrames(work, 8));
        Assert.Equal(6, ring.PullFrames(work, 6).FrameCount);
        Assert.Equal(AudioPullStatus.Buffering, ring.PullFrames(work, 4).Status);

        long before = GC.GetAllocatedBytesForCurrentThread();
        recovery.PrepareRecovery(ring, recoveryEndFrame: 20);
        AudioPullResult replay = recovery.PullFrames(work, 14);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(14, replay.FrameCount);
        Assert.Equal(20, source.Position);
        for (int frame = 0; frame < 14; frame++)
        {
            Assert.Equal(6 + frame, work[frame * 2]);
            Assert.Equal(-(6 + frame), work[(frame * 2) + 1]);
        }
    }

    [Fact]
    public unsafe void RenderAheadWorkerPausesAtAStableProducerFrontierAndResumes()
    {
        CountingSource source = new(1_024);
        using AudioFrameRingBuffer ring = new(source.Format, 128);
        using AudioRenderAheadWorker worker = new(source, ring, 64);
        worker.Start();
        Assert.True(SpinWait.SpinUntil(() => ring.AvailableFrameCount == 128, TimeSpan.FromSeconds(5)));

        worker.PauseAtProducerFrontier(TimeSpan.FromSeconds(5));
        long pausedPosition = source.Position;
        Thread.Sleep(20);

        Assert.True(worker.IsPaused);
        Assert.Equal(pausedPosition, source.Position);
        float* consumed = stackalloc float[64 * 2];
        Assert.Equal(64, ring.PullFrames(consumed, 64).FrameCount);
        Assert.Equal(pausedPosition, source.Position);

        worker.ResumeFromProducerFrontier();
        Assert.True(SpinWait.SpinUntil(() => source.Position > pausedPosition, TimeSpan.FromSeconds(5)));
        Assert.False(worker.IsPaused);
    }

    [Fact]
    public void RenderAheadWorkerRejectsInvalidOrRepeatedPauseTransitions()
    {
        CountingSource source = new(1_024);
        using AudioFrameRingBuffer ring = new(source.Format, 128);
        using AudioRenderAheadWorker worker = new(source, ring, 64);

        Assert.Throws<ArgumentOutOfRangeException>(() => worker.PauseAtProducerFrontier(TimeSpan.Zero));
        Assert.Throws<InvalidOperationException>(() => worker.PauseAtProducerFrontier(TimeSpan.FromSeconds(1)));
        worker.Start();
        worker.PauseAtProducerFrontier(TimeSpan.FromSeconds(5));
        Assert.Throws<InvalidOperationException>(() => worker.PauseAtProducerFrontier(TimeSpan.FromSeconds(1)));
        worker.ResumeFromProducerFrontier();
        Assert.Throws<InvalidOperationException>(() => worker.ResumeFromProducerFrontier());
    }

    private static unsafe void FillFrames(float* destination, int frameCount, int startValue)
    {
        for (int frame = 0; frame < frameCount; frame++)
        {
            destination[frame * 2] = startValue + frame;
            destination[(frame * 2) + 1] = -(startValue + frame);
        }
    }

    private sealed unsafe class CountingSource(long totalFrames) : IAudioRenderSource
    {
        private long _position;

        public AudioFormat Format => new(48_000, 2, AudioSampleFormat.Float32);

        public long Position => Volatile.Read(ref _position);

        public AudioPullResult PullFrames(float* destination, int requestedFrameCount)
        {
            int frames = (int)Math.Min(requestedFrameCount, totalFrames - _position);
            for (int frame = 0; frame < frames; frame++)
            {
                destination[frame * 2] = _position + frame;
                destination[(frame * 2) + 1] = -(_position + frame);
            }

            _position += frames;
            return _position == totalFrames
                ? AudioPullResult.EndOfStream(frames)
                : AudioPullResult.Continue(frames);
        }
    }

    private sealed unsafe class BufferOnceSource : IAudioRenderSource
    {
        public AudioFormat Format => new(48_000, 2, AudioSampleFormat.Float32);
        public int PullCount { get; private set; }

        public AudioPullResult PullFrames(float* destination, int requestedFrameCount)
        {
            PullCount++;
            if (PullCount == 1)
            {
                return AudioPullResult.Buffering();
            }

            NativeMemory.Clear(
                destination,
                checked((nuint)requestedFrameCount * (nuint)Format.BytesPerFrame));
            return AudioPullResult.EndOfStream(requestedFrameCount);
        }
    }

    private sealed unsafe class UnknownStatusSource : IAudioRenderSource
    {
        public AudioFormat Format => new(48_000, 2, AudioSampleFormat.Float32);

        public AudioPullResult PullFrames(float* destination, int requestedFrameCount) =>
            new(0, (AudioPullStatus)byte.MaxValue);
    }
}
