using Midora.AudioDevice;
using Midora.Midi;

namespace Midora.Audio.Bass.Tests;

public sealed class UnitPcmCacheIoBridgeTests
{
    [Fact]
    public unsafe void ReadAheadSwitchesSequentialFragmentsWithoutRenderThreadFileIoOrAllocation()
    {
        AudioFormat format = new(48_000, 2, AudioSampleFormat.Float32);
        float[] firstSamples = [1, -1, 2, -2, 3, -3, 4, -4];
        float[] secondSamples = [5, -5, 6, -6, 7, -7, 8, -8];
        byte[] firstPayload = AudioPcmCachePayload.Encode(format, firstSamples);
        byte[] secondPayload = AudioPcmCachePayload.Encode(format, secondSamples);
        using TemporaryFile file = new(firstPayload.Concat(secondPayload).ToArray());
        MidiRenderPlan plan = CreateTwoFragmentPlan(
            firstPayload.LongLength,
            cacheHit: true);
        using UnitPcmCacheIoBridge bridge = new(file.Path, plan);
        float* output = stackalloc float[16];
        MidiUnitFragmentRenderPlan first = plan.UnitFragments[0];
        MidiUnitFragmentRenderPlan second = plan.UnitFragments[1];

        WaitUntilReady(bridge, first, globalFrame: 0, frameCount: 4);
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool firstRead = bridge.TryReadFrames(first, 0, output, 4);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        WaitUntilReady(bridge, second, globalFrame: 4, frameCount: 4);
        bool secondRead = bridge.TryReadFrames(second, 4, output + 8, 4);

        Assert.True(firstRead);
        Assert.True(secondRead);
        Assert.Equal(0, allocated);
        Assert.Equal(
            firstSamples.Concat(secondSamples).ToArray(),
            new ReadOnlySpan<float>(output, 16).ToArray());
        Assert.False(bridge.ReadFaulted);
    }

    [Fact]
    public unsafe void BoundedWriterDrainsAcrossQueueWrapWithoutProducerAllocation()
    {
        const int frameCount = 5_000;
        AudioFormat format = new(48_000, 2, AudioSampleFormat.Float32);
        byte[] payload = AudioPcmCachePayload.Encode(format, new float[frameCount * 2]);
        using TemporaryFile file = new(payload);
        MidiRenderPlan plan = CreateSingleFragmentPlan(frameCount, cacheHit: false);
        using UnitPcmCacheIoBridge bridge = new(file.Path, plan);
        MidiUnitFragmentRenderPlan fragment = plan.UnitFragments[0];
        float* sample = stackalloc float[2];

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 0; frame < frameCount; frame++)
        {
            sample[0] = frame;
            sample[1] = -frame;
            while (!bridge.CanWriteFrames(fragment, frame, 1))
            {
                Thread.Yield();
            }
            Assert.True(bridge.TryQueueWrite(
                fragment,
                frame,
                sample,
                1));
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        bridge.CompleteWritesAndWait();

        Assert.Equal(0, allocated);
        Assert.False(bridge.WriteFaulted);
        byte[] written = new byte[payload.Length];
        using (FileStream reader = new(
            file.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite))
        {
            reader.ReadExactly(written);
        }
        float[] samples = new float[frameCount * 2];
        Buffer.BlockCopy(
            written,
            AudioPcmCachePayload.HeaderByteCount,
            samples,
            0,
            samples.Length * sizeof(float));
        Assert.Equal(0f, samples[0]);
        Assert.Equal(1_234f, samples[1_234 * 2]);
        Assert.Equal(-1_234f, samples[(1_234 * 2) + 1]);
        Assert.Equal(4_999f, samples[^2]);
        Assert.Equal(-4_999f, samples[^1]);
    }

    private static void WaitUntilReady(
        UnitPcmCacheIoBridge bridge,
        MidiUnitFragmentRenderPlan fragment,
        long globalFrame,
        int frameCount)
    {
        for (int attempt = 0; attempt < 100_000; attempt++)
        {
            if (bridge.IsReadReady(fragment, globalFrame, frameCount))
            {
                return;
            }
            Thread.Yield();
        }
        throw new TimeoutException("The Unit PCM read-ahead test did not become ready.");
    }

    private static MidiRenderPlan CreateTwoFragmentPlan(long secondPayloadOffset, bool cacheHit)
    {
        MidiUnitFragmentRenderPlan first = CreateFragment(
            start: 0,
            end: 4,
            instanceId: 201,
            subVoiceId: 301,
            payloadOffset: 0,
            cacheHit);
        MidiUnitFragmentRenderPlan second = CreateFragment(
            start: 4,
            end: 8,
            instanceId: 202,
            subVoiceId: 302,
            payloadOffset: secondPayloadOffset,
            cacheHit);
        MidiPortRenderPlan port = new(0,
        [
            new ScheduledMidiMessage(0, MidiMessage.NoteOn(0, 60, 100), 0),
            new ScheduledMidiMessage(8, MidiMessage.NoteOff(0, 60, 0), 0)
        ]);
        return new MidiRenderPlan(48_000, 8, [port], [101], [], [first, second]);
    }

    private static MidiRenderPlan CreateSingleFragmentPlan(long frameCount, bool cacheHit)
    {
        MidiUnitFragmentRenderPlan fragment = CreateFragment(
            0,
            frameCount,
            201,
            301,
            0,
            cacheHit);
        MidiPortRenderPlan port = new(0,
            [new ScheduledMidiMessage(0, MidiMessage.NoteOn(0, 60, 100), 0)]);
        return new MidiRenderPlan(48_000, frameCount, [port], [101], [], [fragment]);
    }

    private static MidiUnitFragmentRenderPlan CreateFragment(
        long start,
        long end,
        long instanceId,
        long subVoiceId,
        long payloadOffset,
        bool cacheHit) => new(
            canonicalZeroBasedPortNumber: 0,
            canonicalZeroBasedChannelNumber: 0,
            trackId: 101,
            segmentId: 102,
            eventInstrumentId: 103,
            instanceGroupId: instanceId,
            subVoiceId,
            sourceIndex: 0,
            startFrame: start,
            endFrame: end,
            semanticFingerprint: new string('a', 64),
            [new ScheduledMidiMessage(start, MidiMessage.NoteOn(0, 60, 100), 0)],
            pcmCacheKey: new string('b', 64),
            pcmCachePayloadOffset: payloadOffset,
            pcmCacheHit: cacheHit);

    private sealed class TemporaryFile : IDisposable
    {
        public TemporaryFile(byte[] bytes)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"midora-unit-io-{Guid.NewGuid():N}.bin");
            File.WriteAllBytes(Path, bytes);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }
}
