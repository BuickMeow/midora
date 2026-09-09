using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;
using System.Security.Cryptography;
using System.Text;

namespace Midora.Playback;

public readonly record struct CanonicalAudioUnitEvent(
    long RelativeTick,
    MidiMessage Message,
    CanonicalEventRole Role,
    long StableOrder,
    long SemanticTargetKey,
    long SemanticGroup,
    SourceReference Source,
    int SmfTrackOrder,
    long SmfEventOrder,
    MidoraId StableObjectId);

public sealed class CanonicalAudioUnitFragment
{
    private readonly CanonicalAudioUnitEvent[] _events;

    internal CanonicalAudioUnitFragment(
        MidoraId trackId,
        MidoraId segmentId,
        MidoraId eventInstrumentId,
        MidoraId instanceGroupId,
        MidoraId subVoiceId,
        MidoraId midiChannelRootId,
        MidiChannelMode channelMode,
        long groupStartTick,
        long groupEndTick,
        long effectiveStartTick,
        long effectiveEndTick,
        CanonicalAudioUnitEvent[] events)
    {
        TrackId = trackId;
        SegmentId = segmentId;
        EventInstrumentId = eventInstrumentId;
        InstanceGroupId = instanceGroupId;
        SubVoiceId = subVoiceId;
        MidiChannelRootId = midiChannelRootId;
        ChannelMode = channelMode;
        GroupStartTick = groupStartTick;
        GroupEndTick = groupEndTick;
        EffectiveStartTick = effectiveStartTick;
        EffectiveEndTick = effectiveEndTick;
        _events = events;
        SemanticFingerprint = ComputeSemanticFingerprint(this);
    }

    public MidoraId TrackId { get; }
    public MidoraId SegmentId { get; }
    public MidoraId EventInstrumentId { get; }
    public MidoraId InstanceGroupId { get; }
    public MidoraId SubVoiceId { get; }
    public MidoraId MidiChannelRootId { get; }
    public MidiChannelMode ChannelMode { get; }
    public long GroupStartTick { get; }
    public long GroupEndTick { get; }
    public long EffectiveStartTick { get; }
    public long EffectiveEndTick { get; }
    public string SemanticFingerprint { get; }
    public ReadOnlySpan<CanonicalAudioUnitEvent> Events => _events;

    private static string ComputeSemanticFingerprint(CanonicalAudioUnitFragment fragment)
    {
        using MemoryStream payload = new();
        using (BinaryWriter writer = new(payload, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("MIDORA_CANONICAL_AUDIO_UNIT_V1");
            writer.Write(fragment.TrackId.Value);
            writer.Write(fragment.SegmentId.Value);
            writer.Write(fragment.EventInstrumentId.Value);
            writer.Write(fragment.InstanceGroupId.Value);
            writer.Write(fragment.SubVoiceId.Value);
            writer.Write(fragment.MidiChannelRootId.Value);
            writer.Write((int)fragment.ChannelMode);
            writer.Write(fragment.GroupEndTick - fragment.GroupStartTick);
            writer.Write(fragment.EffectiveStartTick - fragment.GroupStartTick);
            writer.Write(fragment.EffectiveEndTick - fragment.GroupStartTick);
            writer.Write(fragment._events.Length);
            foreach (CanonicalAudioUnitEvent value in fragment._events)
            {
                writer.Write(value.RelativeTick);
                writer.Write(value.Message.PackedValue);
                writer.Write((byte)value.Role);
                writer.Write(value.SemanticTargetKey);
                WriteSource(writer, value.Source, fragment.GroupStartTick);
            }
        }
        return Convert.ToHexStringLower(SHA256.HashData(payload.GetBuffer().AsSpan(
            0,
            checked((int)payload.Length))));
    }

    private static void WriteSource(BinaryWriter writer, SourceReference source, long originTick)
    {
        writer.Write(source.TrackId.Value);
        writer.Write(source.SegmentId.Value);
        writer.Write(source.LogicalNoteId.Value);
        writer.Write(source.EventInstrumentId.Value);
        writer.Write(source.SubVoiceId.Value);
        writer.Write(source.SourceEventId.Value);
        writer.Write(source.Tick < 0 ? source.Tick : source.Tick - originTick);
        writer.Write(source.LogicalParameterId.Value);
        writer.Write(source.LogicalParameterMappingId.Value);
        writer.Write(source.MappingStepId.Value);
        writer.Write(source.MappingFunctionId.Value);
        writer.Write(source.ValueCurveId.Value);
        writer.Write(source.EnvelopeId.Value);
        writer.Write(source.MidiChannelRootId.Value);
        writer.Write(source.PureMidiTrackId.Value);
        writer.Write(source.MidiSegmentId.Value);
        writer.Write(source.DirectMidiObjectId.Value);
        writer.Write((byte)source.Origin);
    }
}

public sealed class CanonicalAudioUnitProjection
{
    private readonly CanonicalAudioUnitFragment[] _fragments;

    private CanonicalAudioUnitProjection(CanonicalAudioUnitFragment[] fragments)
    {
        _fragments = fragments;
    }

    public ReadOnlySpan<CanonicalAudioUnitFragment> Fragments => _fragments;

    public static CanonicalAudioUnitProjection Create(
        CanonicalCompiledResult compiled,
        IEnumerable<CanonicalMidiEvent>? eventSubset = null)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        if (!compiled.IsConsumable || compiled.IsPartial)
        {
            throw new ArgumentException(
                "Only a consumable canonical result can produce an audio Unit projection.",
                nameof(compiled));
        }

        Dictionary<(MidoraId GroupId, MidoraId SubVoiceId), FragmentBuilder> builders = [];
        Dictionary<(MidoraId InstanceId, MidoraId SubVoiceId), FragmentBuilder> byInstance = [];
        foreach (ChannelUnitAllocation allocation in compiled.Allocations)
        {
            (MidoraId, MidoraId) key = (allocation.InstanceGroupId, allocation.SubVoiceId);
            if (!builders.TryGetValue(key, out FragmentBuilder? builder))
            {
                builder = new FragmentBuilder(allocation, compiled.StartTick, compiled.EndTick);
                builders.Add(key, builder);
            }
            else
            {
                builder.ValidateCompatible(allocation);
            }
            byInstance.Add((allocation.InstanceId, allocation.SubVoiceId), builder);
        }

        if (eventSubset is null)
        {
            foreach (CanonicalMidiEvent value in compiled.Events) AppendEvent(value);
        }
        else
        {
            foreach (CanonicalMidiEvent value in eventSubset) AppendEvent(value);
        }

        void AppendEvent(CanonicalMidiEvent value)
        {
            FragmentBuilder? builder = ResolveBuilder(value, byInstance, builders.Values);
            if (builder is null)
            {
                throw new InvalidDataException(
                    "A canonical MIDI event could not be assigned to a Segment/Unit audio fragment.");
            }
            uint unitPackedMessage = value.Message.PackedValue & ~MidiMessage.ChannelNumberMask;
            builder.Events.Add(new(
                value.Tick - builder.GroupStartTick,
                MidiMessage.FromPackedValue(unitPackedMessage),
                value.Role,
                value.StableOrder,
                value.SemanticTargetKey,
                value.SemanticGroup,
                value.Source,
                value.SmfTrackOrder,
                value.SmfEventOrder,
                value.Source.DirectMidiObjectId));
        }

        CanonicalAudioUnitFragment[] fragments = builders.Values
            .OrderBy(value => value.EffectiveStartTick)
            .ThenBy(value => value.TrackId)
            .ThenBy(value => value.SegmentId)
            .ThenBy(value => value.InstanceGroupId)
            .ThenBy(value => value.SubVoiceId)
            .Select(value => value.Build())
            .ToArray();
        return new CanonicalAudioUnitProjection(fragments);
    }

    private static FragmentBuilder? ResolveBuilder(
        CanonicalMidiEvent value,
        IReadOnlyDictionary<(MidoraId InstanceId, MidoraId SubVoiceId), FragmentBuilder> byInstance,
        IEnumerable<FragmentBuilder> builders)
    {
        if (value.Source.LogicalNoteId != default
            && value.Source.SubVoiceId != default
            && byInstance.TryGetValue(
                (value.Source.LogicalNoteId, value.Source.SubVoiceId),
                out FragmentBuilder? exact))
        {
            return exact;
        }

        if (value.Source.MidiChannelRootId != default)
        {
            return builders.SingleOrDefault(candidate =>
                candidate.MidiChannelRootId == value.Source.MidiChannelRootId
                && candidate.ZeroBasedPort == value.ZeroBasedPort
                && candidate.ZeroBasedChannel == value.ZeroBasedChannel
                && value.Tick >= candidate.EffectiveStartTick
                && value.Tick <= candidate.EffectiveEndTick);
        }

        FragmentBuilder[] candidates = builders
            .Where(candidate =>
                candidate.ZeroBasedPort == value.ZeroBasedPort
                && candidate.ZeroBasedChannel == value.ZeroBasedChannel
                && value.Tick >= candidate.EffectiveStartTick
                && value.Tick <= candidate.EffectiveEndTick
                && (value.Source.SegmentId == default || value.Source.SegmentId == candidate.SegmentId)
                && (value.Source.SubVoiceId == default || value.Source.SubVoiceId == candidate.SubVoiceId))
            .ToArray();
        if (candidates.Length == 1)
        {
            return candidates[0];
        }
        if (value.Source.Origin == SourceOrigin.CompilerBoundaryCleanup)
        {
            return candidates.SingleOrDefault(candidate => candidate.EffectiveEndTick == value.Tick);
        }
        return candidates.SingleOrDefault(candidate => candidate.EffectiveStartTick == value.Tick);
    }

    private sealed class FragmentBuilder
    {
        public FragmentBuilder(
            ChannelUnitAllocation allocation,
            long compilationStartTick,
            long compilationEndTick)
        {
            TrackId = allocation.TrackId;
            SegmentId = allocation.SegmentId;
            EventInstrumentId = allocation.EventInstrumentId;
            InstanceGroupId = allocation.InstanceGroupId;
            SubVoiceId = allocation.SubVoiceId;
            MidiChannelRootId = allocation.MidiChannelRootId;
            EventInstrumentUsageId = allocation.EventInstrumentUsageId;
            ChannelMode = allocation.ChannelMode;
            GroupStartTick = allocation.StartTick;
            GroupEndTick = allocation.EndTick;
            EffectiveStartTick = Math.Max(allocation.StartTick, compilationStartTick);
            EffectiveEndTick = Math.Min(allocation.EndTick, compilationEndTick);
            ZeroBasedPort = allocation.ZeroBasedPort;
            ZeroBasedChannel = allocation.ZeroBasedChannel;
        }

        public MidoraId TrackId { get; }
        public MidoraId SegmentId { get; }
        public MidoraId EventInstrumentId { get; }
        public MidoraId InstanceGroupId { get; }
        public MidoraId SubVoiceId { get; }
        public MidoraId MidiChannelRootId { get; }
        public MidoraId EventInstrumentUsageId { get; }
        public MidiChannelMode ChannelMode { get; }
        public long GroupStartTick { get; }
        public long GroupEndTick { get; }
        public long EffectiveStartTick { get; }
        public long EffectiveEndTick { get; }
        public byte ZeroBasedPort { get; }
        public byte ZeroBasedChannel { get; }
        public List<CanonicalAudioUnitEvent> Events { get; } = [];

        public void ValidateCompatible(ChannelUnitAllocation value)
        {
            bool sharedLogicalUsage = EventInstrumentUsageId != default;
            if ((!sharedLogicalUsage
                    && (TrackId != value.TrackId || SegmentId != value.SegmentId))
                || EventInstrumentId != value.EventInstrumentId
                || InstanceGroupId != value.InstanceGroupId
                || SubVoiceId != value.SubVoiceId
                || MidiChannelRootId != value.MidiChannelRootId
                || EventInstrumentUsageId != value.EventInstrumentUsageId
                || ChannelMode != value.ChannelMode
                || GroupStartTick != value.StartTick
                || GroupEndTick != value.EndTick
                || ZeroBasedPort != value.ZeroBasedPort
                || ZeroBasedChannel != value.ZeroBasedChannel)
            {
                throw new InvalidDataException(
                    "Canonical allocation records disagree about one abstract Segment/Unit fragment.");
            }
        }

        public CanonicalAudioUnitFragment Build() => new(
            TrackId,
            SegmentId,
            EventInstrumentId,
            InstanceGroupId,
            SubVoiceId,
            MidiChannelRootId,
            ChannelMode,
            GroupStartTick,
            GroupEndTick,
            EffectiveStartTick,
            EffectiveEndTick,
            Events.ToArray());
    }
}
