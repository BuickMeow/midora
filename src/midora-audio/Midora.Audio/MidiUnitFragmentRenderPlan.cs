namespace Midora.Audio;

public sealed class MidiUnitFragmentRenderPlan
{
    private readonly ScheduledMidiMessage[] _events;

    public MidiUnitFragmentRenderPlan(
        byte canonicalZeroBasedPortNumber,
        byte canonicalZeroBasedChannelNumber,
        long trackId,
        long segmentId,
        long eventInstrumentId,
        long instanceGroupId,
        long subVoiceId,
        int sourceIndex,
        long startFrame,
        long endFrame,
        string semanticFingerprint,
        ReadOnlySpan<ScheduledMidiMessage> events,
        string? pcmCacheKey = null,
        long pcmCachePayloadOffset = -1,
        bool pcmCacheHit = false,
        long midiChannelRootId = 0,
        bool isPercussion = false)
    {
        if (canonicalZeroBasedPortNumber >= 16)
        {
            throw new ArgumentOutOfRangeException(nameof(canonicalZeroBasedPortNumber));
        }
        if (canonicalZeroBasedChannelNumber >= 16)
        {
            throw new ArgumentOutOfRangeException(nameof(canonicalZeroBasedChannelNumber));
        }
        bool logicalIdentity = midiChannelRootId == 0
            && trackId > 0 && segmentId > 0 && eventInstrumentId > 0
            && instanceGroupId > 0 && subVoiceId > 0;
        bool pureMidiIdentity = midiChannelRootId > 0
            && trackId > 0 && segmentId > 0 && eventInstrumentId == 0
            && instanceGroupId > 0 && subVoiceId > 0;
        if (!logicalIdentity && !pureMidiIdentity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(trackId),
                "A formal Unit fragment requires positive stable identities.");
        }
        if (sourceIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceIndex));
        }
        if (startFrame < 0 || endFrame < startFrame)
        {
            throw new ArgumentOutOfRangeException(nameof(startFrame));
        }
        if (semanticFingerprint.Length != 64
            || semanticFingerprint.Any(value =>
                value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "The Unit semantic fingerprint must be lowercase SHA-256 hexadecimal.",
                nameof(semanticFingerprint));
        }
        if (pcmCacheKey is null)
        {
            if (pcmCachePayloadOffset != -1 || pcmCacheHit)
            {
                throw new ArgumentException(
                    "A Unit fragment without a PCM cache key cannot have a cache binding.",
                    nameof(pcmCachePayloadOffset));
            }
        }
        else if (pcmCacheKey.Length != 64
            || pcmCacheKey.Any(value =>
                value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            || pcmCachePayloadOffset < 0)
        {
            throw new ArgumentException(
                "A PCM cache binding requires a lowercase SHA-256 key and non-negative payload offset.",
                nameof(pcmCacheKey));
        }

        CanonicalZeroBasedPortNumber = canonicalZeroBasedPortNumber;
        CanonicalZeroBasedChannelNumber = canonicalZeroBasedChannelNumber;
        TrackId = trackId;
        SegmentId = segmentId;
        EventInstrumentId = eventInstrumentId;
        InstanceGroupId = instanceGroupId;
        SubVoiceId = subVoiceId;
        SourceIndex = sourceIndex;
        StartFrame = startFrame;
        EndFrame = endFrame;
        SemanticFingerprint = semanticFingerprint;
        PcmCacheKey = pcmCacheKey;
        PcmCachePayloadOffset = pcmCachePayloadOffset;
        PcmCacheHit = pcmCacheHit;
        MidiChannelRootId = midiChannelRootId;
        IsPercussion = isPercussion;
        _events = events.ToArray();
        ValidateEvents(_events);
    }

    public byte CanonicalZeroBasedPortNumber { get; }
    public byte CanonicalZeroBasedChannelNumber { get; }
    public int CanonicalUnitNumber =>
        (CanonicalZeroBasedPortNumber * 16) + CanonicalZeroBasedChannelNumber;
    public long TrackId { get; }
    public long SegmentId { get; }
    public long EventInstrumentId { get; }
    public long InstanceGroupId { get; }
    public long SubVoiceId { get; }
    public int SourceIndex { get; }
    public long StartFrame { get; }
    public long EndFrame { get; }
    public string SemanticFingerprint { get; }
    public string? PcmCacheKey { get; }
    public long PcmCachePayloadOffset { get; }
    public bool PcmCacheHit { get; }
    public long MidiChannelRootId { get; }
    public bool IsPercussion { get; }
    public ReadOnlySpan<ScheduledMidiMessage> Events => _events;

    public long PcmPayloadByteCount => checked(
        AudioPcmCachePayload.HeaderByteCount
        + ((EndFrame - StartFrame) * 2 * sizeof(float)));

    private void ValidateEvents(ReadOnlySpan<ScheduledMidiMessage> events)
    {
        long previous = StartFrame;
        for (int i = 0; i < events.Length; i++)
        {
            ScheduledMidiMessage value = events[i];
            value.ValidatePayload();
            if (value.SampleFrame < StartFrame
                || value.SampleFrame > EndFrame
                || i != 0 && value.SampleFrame < previous
                || value.Message.ChannelNumber != 0
                || value.SourceIndex < 0)
            {
                throw new ArgumentException(
                    "Unit fragment events must be ordered, use channel 0, carry a valid source, and stay inside the fragment boundary.",
                    nameof(events));
            }
            previous = value.SampleFrame;
        }
    }
}
