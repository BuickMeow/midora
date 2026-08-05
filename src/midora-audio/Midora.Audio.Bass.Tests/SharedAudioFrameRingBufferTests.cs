using Midora.AudioDevice;
using System.Runtime.Versioning;

namespace Midora.Audio.Bass.Tests;

[SupportedOSPlatform("windows")]
public sealed class SharedAudioFrameRingBufferTests
{
    [Fact]
    public unsafe void SharesBoundedFramesAndStateWithoutManagedAllocation()
    {
        string name = $"Midora.Audio.Test.{Guid.NewGuid():N}";
        AudioFormat format = new(48_000, 2, AudioSampleFormat.Float32);
        using SharedAudioFrameRingBuffer producer = SharedAudioFrameRingBuffer.Create(name, format, 32);
        using SharedAudioFrameRingBuffer consumer = SharedAudioFrameRingBuffer.Open(name);
        float* source = stackalloc float[40];
        float* destination = stackalloc float[40];
        for (int i = 0; i < 40; i++)
        {
            source[i] = i * 0.25f;
        }

        _ = producer.TryWriteFrames(source, 0);
        _ = consumer.PullFrames(destination, 0);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        bool wrote = producer.TryWriteFrames(source, 20);
        AudioPullResult result = consumer.PullFrames(destination, 20);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.True(wrote);
        Assert.Equal(0, allocated);
        Assert.Equal(20, result.FrameCount);
        for (int i = 0; i < 40; i++)
        {
            Assert.Equal(source[i], destination[i]);
        }

        producer.MarkProducerReady();
        producer.CompleteProducer(123);
        Assert.True(consumer.ProducerReady);
        Assert.True(consumer.ProducerCompleted);
        Assert.Equal(123, consumer.ProducerAllocatedBytes);
    }
}
