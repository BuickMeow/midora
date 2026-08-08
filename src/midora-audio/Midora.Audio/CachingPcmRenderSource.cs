using Midora.AudioDevice;
using System.Runtime.InteropServices;

namespace Midora.Audio;

public sealed unsafe class CachingPcmRenderSource : IAudioRenderSource
{
    public const long MaximumCollectedPayloadBytes = 256L * 1024 * 1024;
    private readonly IAudioRenderSource _source;
    private readonly float[] _samples;
    private long _collectedFrameCount;
    private bool _faulted;

    private CachingPcmRenderSource(IAudioRenderSource source, float[] samples)
    {
        _source = source;
        _samples = samples;
    }

    public AudioFormat Format => _source.Format;
    public long TotalFrameCount => _samples.LongLength / Format.ChannelCount;
    public bool IsComplete => !_faulted && _collectedFrameCount == TotalFrameCount;

    public static CachingPcmRenderSource? TryCreate(
        IAudioRenderSource source,
        long totalFrameCount,
        long maximumPayloadBytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.Format.Validate();
        if (totalFrameCount < 0 || maximumPayloadBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalFrameCount));
        }
        long sampleCount;
        long payloadBytes;
        try
        {
            sampleCount = checked(totalFrameCount * source.Format.ChannelCount);
            payloadBytes = checked(32 + (sampleCount * sizeof(float)));
        }
        catch (OverflowException)
        {
            return null;
        }
        long effectiveLimit = Math.Min(maximumPayloadBytes, MaximumCollectedPayloadBytes);
        if (sampleCount > int.MaxValue || payloadBytes > effectiveLimit)
        {
            return null;
        }
        return new CachingPcmRenderSource(source, new float[(int)sampleCount]);
    }

    public AudioPullResult PullFrames(float* destination, int requestedFrameCount)
    {
        AudioPullResult result = _source.PullFrames(destination, requestedFrameCount);
        if (result.Status == AudioPullStatus.Fault
            || !result.IsValidForRequest(requestedFrameCount))
        {
            _faulted = true;
            return result;
        }
        if (result.FrameCount == 0)
        {
            return result;
        }
        long nextFrameCount = _collectedFrameCount + result.FrameCount;
        if (nextFrameCount > TotalFrameCount)
        {
            _faulted = true;
            return AudioPullResult.Fault();
        }
        int sampleOffset = checked((int)_collectedFrameCount * Format.ChannelCount);
        nuint byteCount = checked((nuint)result.FrameCount * (nuint)Format.BytesPerFrame);
        fixed (float* target = &_samples[sampleOffset])
        {
            NativeMemory.Copy(destination, target, byteCount);
        }
        _collectedFrameCount = nextFrameCount;
        return result;
    }

    public byte[] CreatePayload()
    {
        if (!IsComplete)
        {
            throw new InvalidOperationException(
                "A reusable PCM cache payload is available only after the complete source was rendered.");
        }
        return AudioPcmCachePayload.Encode(Format, _samples);
    }
}
