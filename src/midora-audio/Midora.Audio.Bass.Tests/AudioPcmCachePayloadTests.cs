using Midora.AudioDevice;
using System.Buffers.Binary;

namespace Midora.Audio.Bass.Tests;

public sealed class AudioPcmCachePayloadTests
{
    [Fact]
    public unsafe void RoundTripsAndPullsCachedFramesWithoutManagedAllocation()
    {
        AudioFormat format = new(48_000, 2, AudioSampleFormat.Float32);
        float[] samples = [0.25f, -0.25f, 0.5f, -0.5f, 0.75f, -0.75f];
        byte[] payload = AudioPcmCachePayload.Encode(format, samples);
        CachedPcmRenderSource source = AudioPcmCachePayload.Decode(payload);
        float* output = stackalloc float[8];

        long before = GC.GetAllocatedBytesForCurrentThread();
        AudioPullResult first = source.PullFrames(output, 2);
        AudioPullResult second = source.PullFrames(output + 4, 2);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(AudioPullStatus.Continue, first.Status);
        Assert.Equal(2, first.FrameCount);
        Assert.Equal(AudioPullStatus.EndOfStream, second.Status);
        Assert.Equal(1, second.FrameCount);
        Assert.Equal(samples, new ReadOnlySpan<float>(output, samples.Length).ToArray());
        Assert.Equal(3, source.PositionFrames);
    }

    [Fact]
    public void RejectsMalformedLengthFormatAndNonFiniteSamples()
    {
        AudioFormat format = new(48_000, 2, AudioSampleFormat.Float32);
        Assert.Throws<ArgumentException>(() => AudioPcmCachePayload.Encode(format, [0f]));
        Assert.Throws<ArgumentException>(() => AudioPcmCachePayload.Encode(format, [float.NaN, 0f]));

        byte[] payload = AudioPcmCachePayload.Encode(format, [0f, 0f]);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(20), 2);
        Assert.Throws<InvalidDataException>(() => AudioPcmCachePayload.Decode(payload));

        payload = AudioPcmCachePayload.Encode(format, [0f, 0f]);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(12), 1);
        Assert.Throws<InvalidDataException>(() => AudioPcmCachePayload.Decode(payload));

        payload = AudioPcmCachePayload.Encode(format, [0f, 0f]);
        BinaryPrimitives.WriteInt32LittleEndian(
            payload.AsSpan(32),
            BitConverter.SingleToInt32Bits(float.PositiveInfinity));
        Assert.Throws<InvalidDataException>(() => AudioPcmCachePayload.Decode(payload));
    }

    [Fact]
    public unsafe void CollectorCopiesACompleteSourceWithoutHotPathAllocationAndHonorsItsBound()
    {
        using CountingSource underlying = new(totalFrameCount: 4);
        CachingPcmRenderSource collector = Assert.IsType<CachingPcmRenderSource>(
            CachingPcmRenderSource.TryCreate(underlying, 4, 1024));
        float* output = stackalloc float[8];

        long before = GC.GetAllocatedBytesForCurrentThread();
        AudioPullResult first = collector.PullFrames(output, 2);
        AudioPullResult second = collector.PullFrames(output + 4, 2);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(AudioPullStatus.Continue, first.Status);
        Assert.Equal(AudioPullStatus.EndOfStream, second.Status);
        Assert.True(collector.IsComplete);
        CachedPcmRenderSource restored = AudioPcmCachePayload.Decode(collector.CreatePayload());
        Assert.Equal(4, restored.TotalFrameCount);
        Assert.Null(CachingPcmRenderSource.TryCreate(
            underlying,
            CachingPcmRenderSource.MaximumCollectedPayloadBytes,
            CachingPcmRenderSource.MaximumCollectedPayloadBytes));
    }

    private sealed unsafe class CountingSource(long totalFrameCount) : IAudioRenderSource, IDisposable
    {
        private long _position;

        public AudioFormat Format { get; } = new(48_000, 2, AudioSampleFormat.Float32);

        public AudioPullResult PullFrames(float* destination, int requestedFrameCount)
        {
            int frames = (int)Math.Min(requestedFrameCount, totalFrameCount - _position);
            for (int frame = 0; frame < frames; frame++)
            {
                destination[frame * 2] = (_position + frame) / 10f;
                destination[(frame * 2) + 1] = -(_position + frame) / 10f;
            }
            _position += frames;
            return _position == totalFrameCount
                ? AudioPullResult.EndOfStream(frames)
                : AudioPullResult.Continue(frames);
        }

        public void Dispose()
        {
        }
    }
}
