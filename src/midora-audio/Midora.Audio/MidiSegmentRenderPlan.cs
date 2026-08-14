namespace Midora.Audio;

/// <summary>
/// A deterministic pre-Master/pre-Limiter PCM cache boundary for one logical Segment
/// inside the frozen realtime compilation range.
/// </summary>
public sealed class MidiSegmentRenderPlan
{
    public MidiSegmentRenderPlan(
        long trackId,
        long segmentId,
        int sourceIndex,
        long startFrame,
        long endFrame,
        string semanticFingerprint,
        string? pcmCacheKey = null,
        long pcmCachePayloadOffset = -1,
        bool pcmCacheHit = false)
    {
        if (trackId <= 0 || segmentId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(trackId),
                "A Segment render plan requires positive stable identities.");
        }
        if (sourceIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceIndex));
        }
        if (startFrame < 0 || endFrame <= startFrame)
        {
            throw new ArgumentOutOfRangeException(nameof(startFrame));
        }
        if (semanticFingerprint is null
            || semanticFingerprint.Length != 64
            || semanticFingerprint.Any(value =>
                value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "The Segment semantic fingerprint must be lowercase SHA-256 hexadecimal.",
                nameof(semanticFingerprint));
        }
        if (pcmCacheKey is null)
        {
            if (pcmCachePayloadOffset != -1 || pcmCacheHit)
            {
                throw new ArgumentException(
                    "A Segment without a PCM cache key cannot have a cache binding.",
                    nameof(pcmCachePayloadOffset));
            }
        }
        else if (pcmCacheKey.Length != 64
            || pcmCacheKey.Any(value =>
                value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            || pcmCachePayloadOffset < -2
            || (pcmCachePayloadOffset < 0 && !pcmCacheHit))
        {
            throw new ArgumentException(
                "A Segment PCM cache binding requires a lowercase SHA-256 key and a valid staged or direct-read offset.",
                nameof(pcmCacheKey));
        }

        TrackId = trackId;
        SegmentId = segmentId;
        SourceIndex = sourceIndex;
        StartFrame = startFrame;
        EndFrame = endFrame;
        SemanticFingerprint = semanticFingerprint;
        PcmCacheKey = pcmCacheKey;
        PcmCachePayloadOffset = pcmCachePayloadOffset;
        PcmCacheHit = pcmCacheHit;
    }

    public long TrackId { get; }
    public long SegmentId { get; }
    public int SourceIndex { get; }
    public long StartFrame { get; }
    public long EndFrame { get; }
    public string SemanticFingerprint { get; }
    public string? PcmCacheKey { get; }
    public long PcmCachePayloadOffset { get; }
    public bool PcmCacheHit { get; }

    public long FrameCount => EndFrame - StartFrame;

    public long PcmPayloadByteCount => checked(
        AudioPcmCachePayload.HeaderByteCount
        + (FrameCount * 2 * sizeof(float)));
}
