using Midora.AudioDevice;

namespace Midora.Audio.Bass.Tests;

public sealed class PlaybackSpanRenderSourceTests
{
    [Fact]
    public unsafe void MissCapturesExactPostMasterFramesWithoutHotPathAllocation()
    {
        using TemporaryFile file = TemporaryFile.CreatePayload(frameCount: 6, samples: null);
        TestFallbackSource underlying = new(totalFrameCount: 6, sampleOffset: 10f);
        using (PlaybackSpanRenderSource source = new(underlying, file.Path, cacheHit: false))
        {
            float* output = stackalloc float[12];
            long before = GC.GetAllocatedBytesForCurrentThread();
            AudioPullResult first = source.PullFrames(output, 2);
            AudioPullResult second = source.PullFrames(output + 4, 4);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, allocated);
            Assert.Equal(AudioPullStatus.Continue, first.Status);
            Assert.Equal(AudioPullStatus.EndOfStream, second.Status);
            Assert.Equal(6, source.PositionFrames);
        }

        Assert.Equal(
            TestFallbackSource.CreateSamples(0, 6, 10f),
            file.ReadSamples());
    }

    [Fact]
    public unsafe void HitReplaysExactFramesWithoutPullingUnderlying()
    {
        float[] cached = TestFallbackSource.CreateSamples(0, 5, 20f);
        using TemporaryFile file = TemporaryFile.CreatePayload(5, cached);
        TestFallbackSource underlying = new(totalFrameCount: 5, sampleOffset: 100f);
        using PlaybackSpanRenderSource source = new(underlying, file.Path, cacheHit: true);
        float* output = stackalloc float[10];

        long before = GC.GetAllocatedBytesForCurrentThread();
        AudioPullResult first = PullEventually(source, output, 3);
        AudioPullResult second = PullEventually(source, output + 6, 3);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(AudioPullStatus.Continue, first.Status);
        Assert.Equal(AudioPullStatus.EndOfStream, second.Status);
        Assert.Equal(cached, new ReadOnlySpan<float>(output, 10).ToArray());
        Assert.Equal(0, underlying.PullCount);
        Assert.Null(underlying.SeekFrame);
    }

    [Fact]
    public unsafe void MonitoringFallbackColdStartsAtFrontierAndCrossfadesFourMilliseconds()
    {
        const int sampleRate = 1_000;
        const int totalFrames = 12;
        float[] cached = Enumerable.Repeat(1f, totalFrames * 2).ToArray();
        using TemporaryFile file = TemporaryFile.CreatePayload(
            totalFrames,
            cached,
            sampleRate);
        TestFallbackSource underlying = new(
            totalFrames,
            sampleOffset: -1f,
            sampleRate);
        using PlaybackSpanRenderSource source = new(underlying, file.Path, cacheHit: true);
        float* output = stackalloc float[totalFrames * 2];

        Assert.Equal(AudioPullStatus.Continue, PullEventually(source, output, 3).Status);
        source.RequestMonitoringFallback();
        Assert.Equal(AudioPullStatus.Continue, PullEventually(source, output + 6, 5).Status);
        Assert.Equal(AudioPullStatus.EndOfStream, PullEventually(source, output + 16, 4).Status);

        Assert.Equal(3, underlying.SeekFrame);
        Assert.Equal([1f, 1f, 1f, 1f, 1f, 1f],
            new ReadOnlySpan<float>(output, 6).ToArray());
        // At 1 kHz the fixed transition is four frames: old, 75/25, 50/50, 25/75.
        Assert.Equal(1f, output[6], 6);
        Assert.Equal(0.5f, output[8], 6);
        Assert.Equal(0f, output[10], 6);
        Assert.Equal(-0.5f, output[12], 6);
        Assert.Equal(-1f, output[14], 6);
    }

    [Fact]
    public void InvalidHeaderReleasesTheMappedFile()
    {
        using TemporaryFile file = TemporaryFile.CreatePayload(frameCount: 2, samples: null);
        using (FileStream stream = new(file.Path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.WriteByte(0);
        }
        TestFallbackSource underlying = new(totalFrameCount: 2, sampleOffset: 0f);

        Assert.Throws<InvalidDataException>(() =>
            new PlaybackSpanRenderSource(underlying, file.Path, cacheHit: true));

        File.Delete(file.Path);
        Assert.False(File.Exists(file.Path));
    }

    private sealed unsafe class TestFallbackSource(
        long totalFrameCount,
        float sampleOffset,
        int sampleRate = 48_000) : IPlaybackSpanFallbackSource
    {
        private long _position;

        public AudioFormat Format { get; } =
            new(sampleRate, 2, AudioSampleFormat.Float32);

        public long PositionFrames => _position;

        public long TotalFrameCount => totalFrameCount;

        public int PullCount { get; private set; }

        public long? SeekFrame { get; private set; }

        public AudioPullResult PullFrames(float* destination, int requestedFrameCount)
        {
            PullCount++;
            int frames = (int)Math.Min(requestedFrameCount, totalFrameCount - _position);
            for (int frame = 0; frame < frames; frame++)
            {
                float sample = sampleOffset;
                destination[frame * 2] = sample;
                destination[(frame * 2) + 1] = sample;
            }
            _position += frames;
            return _position == totalFrameCount
                ? AudioPullResult.EndOfStream(frames)
                : AudioPullResult.Continue(frames);
        }

        public void ResetForMonitoringColdStart(
            long producerFrontierFrame,
            ReadOnlySpan<MidiMonitoringCommand> commands)
        {
            SeekFrame = producerFrontierFrame;
            _position = producerFrontierFrame;
        }

        public static float[] CreateSamples(
            long startFrame,
            int frameCount,
            float sampleOffset)
        {
            float[] result = new float[frameCount * 2];
            for (int frame = 0; frame < frameCount; frame++)
            {
                float sample = sampleOffset;
                result[frame * 2] = sample;
                result[(frame * 2) + 1] = sample;
            }
            return result;
        }
    }

    private static unsafe AudioPullResult PullEventually(
        PlaybackSpanRenderSource source,
        float* destination,
        int requestedFrameCount)
    {
        for (int attempt = 0; attempt < 10_000; attempt++)
        {
            AudioPullResult result = source.PullFrames(destination, requestedFrameCount);
            if (result.Status != AudioPullStatus.Buffering)
            {
                return result;
            }
            Thread.Yield();
        }
        throw new TimeoutException("The test PCM read-ahead did not become ready.");
    }

    private sealed class TemporaryFile : IDisposable
    {
        private TemporaryFile(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryFile CreatePayload(
            long frameCount,
            float[]? samples,
            int sampleRate = 48_000)
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"midora-span-cache-{Guid.NewGuid():N}.bin");
            AudioFormat format = new(sampleRate, 2, AudioSampleFormat.Float32);
            byte[] payload = AudioPcmCachePayload.Encode(
                format,
                samples ?? new float[checked((int)frameCount * 2)]);
            File.WriteAllBytes(path, payload);
            return new(path);
        }

        public float[] ReadSamples()
        {
            byte[] payload = File.ReadAllBytes(Path);
            byte[] sampleBytes = payload[AudioPcmCachePayload.HeaderByteCount..];
            float[] result = new float[sampleBytes.Length / sizeof(float)];
            Buffer.BlockCopy(sampleBytes, 0, result, 0, sampleBytes.Length);
            return result;
        }

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }
}
