using System.Collections;
using System.Runtime.CompilerServices;
using Midora.Domain;

namespace Midora.Compiler;

public sealed partial class MidoraCompiler
{
    // Pattern identity includes every event/source field. Relative arithmetic is
    // deliberately reversible even for long.MinValue semantic sentinels.
    private readonly record struct CompactRawEvent(
        long Tick, long Sequence, long Target, long Group, long SourceTick,
        int Data1, int Data2, int Source, RawMessageKind Kind, CanonicalEventRole Role, bool GroupIsRelative);

    private readonly record struct RawSourcePattern(SourceReference Source, byte Inherited);

    private sealed record RawVoiceState(MidiInitialState InitialState,
        HashSet<MidiValueTarget> UsedTargets, HashSet<MidiValueTarget> TickZeroTargets,
        MidiValueTarget[] RetainedTargets);

    private sealed class RawEventPatternPool
    {
        private const int MaximumPatterns = 4096;
        private const long MaximumInternedBytes = 16L * 1024 * 1024;
        private readonly Dictionary<RawSourcePattern, int> _sourceIndices = [];
        private readonly List<RawSourcePattern> _sources = [];
        private readonly Dictionary<int, List<CompactRawEvent[]>> _patterns = [];
        private readonly Dictionary<MidoraId, RawVoiceState> _voiceStates = [];
        private long _internedBytes;
        private int _patternCount;

        public RawVoiceState GetVoiceState(MidoraProject project, EventInstrument instrument, SubVoice voice)
        {
            if (_voiceStates.TryGetValue(voice.Id, out RawVoiceState? prepared)) return prepared;
            MidiInitialState state = MergeState(project.GlobalInitialState, instrument.InitialState, voice.InitialState);
            HashSet<MidiValueTarget> targets = CollectUsedTargets(instrument, voice, state);
            prepared = new(state, targets, GetTickZeroTargets(voice), targets
                .Where(static target => target.Kind != MidiValueKind.ControlChange || target.Number != AllSoundOffController)
                .OrderBy(static target => target.Kind).ThenBy(static target => target.Number).ToArray());
            _voiceStates.Add(voice.Id, prepared);
            return prepared;
        }

        public RawEventSequence Freeze(IReadOnlyList<RawMidiEvent> events, SourceReference context, long sequence,
            CancellationToken cancellationToken)
        {
            CompactRawEvent[] records = new CompactRawEvent[events.Count];
            HashCode hash = new();
            for (int i = 0; i < records.Length; i++)
            {
                if ((i & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                RawMidiEvent value = events[i];
                RawSourcePattern source = NormalizeSource(value.Source, context);
                if (!_sourceIndices.TryGetValue(source, out int sourceIndex))
                {
                    sourceIndex = _sources.Count;
                    _sources.Add(source);
                    _sourceIndices.Add(source, sourceIndex);
                }
                records[i] = new(
                    unchecked(value.Tick - context.Tick), unchecked(value.Sequence - sequence),
                    value.SemanticTargetKey, value.SemanticGroup == long.MinValue
                        ? long.MinValue : unchecked(value.SemanticGroup - sequence),
                    unchecked(value.Source.Tick - context.Tick), value.Data1, value.Data2,
                    sourceIndex, value.Kind, value.Role, value.SemanticGroup != long.MinValue);
                hash.Add(records[i]);
            }
            int key = hash.ToHashCode();
            if (_patterns.TryGetValue(key, out List<CompactRawEvent[]>? candidates))
            {
                foreach (CompactRawEvent[] candidate in candidates)
                {
                    if (records.AsSpan().SequenceEqual(candidate))
                        return new(this, candidate, context, sequence);
                }
            }
            long bytes = (long)records.Length * Unsafe.SizeOf<CompactRawEvent>() + 24;
            if (_patternCount < MaximumPatterns && bytes <= MaximumInternedBytes - _internedBytes)
            {
                if (candidates is null) _patterns.Add(key, candidates = []);
                candidates.Add(records);
                _internedBytes += bytes;
                _patternCount++;
            }
            return new(this, records, context, sequence);
        }

        public void Seal()
        {
            // Readers need the immutable source table, not either discovery index.
            _sourceIndices.Clear();
            _sourceIndices.TrimExcess();
            _patterns.Clear();
            _patterns.TrimExcess();
            _voiceStates.Clear();
            _voiceStates.TrimExcess();
        }

        public SourceReference RestoreSource(int index, RawEventSequence sequence, long tick)
        {
            RawSourcePattern pattern = _sources[index];
            SourceReference source = pattern.Source;
            return source with
            {
                TrackId = (pattern.Inherited & 1) != 0 ? sequence.TrackId : source.TrackId,
                SegmentId = (pattern.Inherited & 2) != 0 ? sequence.SegmentId : source.SegmentId,
                LogicalNoteId = (pattern.Inherited & 4) != 0 ? sequence.NoteId : source.LogicalNoteId,
                EventInstrumentId = (pattern.Inherited & 8) != 0 ? sequence.InstrumentId : source.EventInstrumentId,
                SubVoiceId = (pattern.Inherited & 16) != 0 ? sequence.VoiceId : source.SubVoiceId,
                EventInstrumentUsageId = (pattern.Inherited & 32) != 0 ? sequence.UsageId : source.EventInstrumentUsageId,
                Tick = tick
            };
        }

        private static RawSourcePattern NormalizeSource(SourceReference source, SourceReference context)
        {
            byte inherited = 0;
            if (source.TrackId == context.TrackId) { inherited |= 1; source = source with { TrackId = default }; }
            if (source.SegmentId == context.SegmentId) { inherited |= 2; source = source with { SegmentId = default }; }
            if (source.LogicalNoteId == context.LogicalNoteId) { inherited |= 4; source = source with { LogicalNoteId = default }; }
            if (source.EventInstrumentId == context.EventInstrumentId) { inherited |= 8; source = source with { EventInstrumentId = default }; }
            if (source.SubVoiceId == context.SubVoiceId) { inherited |= 16; source = source with { SubVoiceId = default }; }
            if (source.EventInstrumentUsageId == context.EventInstrumentUsageId) { inherited |= 32; source = source with { EventInstrumentUsageId = default }; }
            return new(source with { Tick = 0 }, inherited);
        }
    }

    private sealed class RawEventSequence : IReadOnlyList<RawMidiEvent>
    {
        private readonly RawEventPatternPool _pool;
        private readonly CompactRawEvent[] _events;
        private readonly long _tick, _sequence;
        public readonly MidoraId TrackId, SegmentId, NoteId, InstrumentId, VoiceId, UsageId;

        public RawEventSequence(RawEventPatternPool pool, CompactRawEvent[] events, SourceReference context, long sequence)
        {
            _pool = pool; _events = events; _tick = context.Tick; _sequence = sequence;
            TrackId = context.TrackId; SegmentId = context.SegmentId; NoteId = context.LogicalNoteId;
            InstrumentId = context.EventInstrumentId; VoiceId = context.SubVoiceId; UsageId = context.EventInstrumentUsageId;
        }

        public int Count => _events.Length;
        public RawMidiEvent this[int index]
        {
            get
            {
                CompactRawEvent value = _events[index];
                return new(unchecked(value.Tick + _tick), value.Kind, value.Data1, value.Data2,
                    value.Role, unchecked(value.Sequence + _sequence), value.Target,
                    value.GroupIsRelative ? unchecked(value.Group + _sequence) : value.Group,
                    _pool.RestoreSource(value.Source, this, unchecked(value.SourceTick + _tick)));
            }
        }
        public IEnumerator<RawMidiEvent> GetEnumerator()
        {
            for (int i = 0; i < Count; i++) yield return this[i];
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
