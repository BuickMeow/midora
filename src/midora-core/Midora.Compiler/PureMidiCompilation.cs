using Midora.Domain;
using Midora.Midi;

namespace Midora.Compiler;

public sealed partial class MidoraCompiler
{
    internal const string PureMidiAudioFragmentFingerprintAbi =
        "MIDORA_PURE_MIDI_AUDIO_FRAGMENT_V3";

    private sealed record PureMidiPlan(
        PureMidiRootPlan[] Roots,
        CanonicalSmfTrackDescriptor[] TrackDescriptors,
        CanonicalOpaqueMidiEvent[] OpaqueEvents,
        PureMidiChannelModeSystemExclusiveEvent[] ChannelModeSystemExclusiveEvents)
    {
        public static PureMidiPlan Empty { get; } = new([], [], [], []);
    }

    private sealed record PureMidiRootPlan(
        MidiChannelRoot Root,
        PureMidiTrackPlan[] Tracks,
        PureMidiRootInterval[] Intervals,
        bool ParticipatesInRequest)
    {
        public bool HasParticipatingSegments => Intervals.Length != 0;
    }

    private sealed record PureMidiTrackPlan(
        PureMidiTrack Track,
        int RootOrder,
        int TrackOrder,
        MidiSegment[] Segments,
        long ExportEndTick);

    private sealed record PureMidiRootInterval(
        long StartTick,
        long EndTick,
        MidoraId GroupId,
        MidoraId StartOwnerTrackId,
        MidoraId StartOwnerSegmentId,
        MidoraId EndOwnerTrackId,
        MidoraId EndOwnerSegmentId);

    private readonly record struct PureMidiPendingEvent(
        long Tick,
        int TrackOrder,
        long ExplicitOrder,
        int EndpointOrder,
        MidoraId StableId,
        MidiMessage Message,
        SourceReference Source,
        MidoraId ExportTrackId,
        CanonicalEventRole Role,
        long SemanticTargetKey);

    private readonly record struct PureMidiHistoricalStateCandidate(
        long AbsoluteTick,
        PureMidiTrackPlan TrackPlan,
        MidiSegment Segment,
        DirectMidiChannelEventValue Value,
        MidiMessage Message,
        long SemanticTargetKey);

    private readonly record struct PureMidiChannelModeSystemExclusiveEvent(
        MidoraId RootId,
        MidoraId TrackId,
        MidoraId SegmentId,
        MidoraId ObjectId,
        long Tick,
        MidiChannelModeSystemExclusive Value,
        CanonicalEventRole Role,
        long StableOrder,
        int SmfTrackOrder,
        long SmfEventOrder);

    private static PureMidiPlan BuildPureMidiPlan(
        MidoraProject project,
        CompilationRequest request,
        long startTick,
        long endTick,
        CancellationToken cancellationToken)
    {
        if (project.MidiChannelRoots.Count == 0)
        {
            return PureMidiPlan.Empty;
        }

        PureMidiTrack[] globalTracks = project.PureMidiTracksInArrangementOrder().ToArray();
        Dictionary<MidoraId, int> globalTrackOrder = globalTracks
            .Select((track, index) => (track.Id, index))
            .ToDictionary(value => value.Id, value => value.index);
        List<PureMidiRootPlan> roots = [];
        List<CanonicalSmfTrackDescriptor> descriptors = [];
        List<CanonicalOpaqueMidiEvent> opaque = [];
        int rootOrder = 0;
        foreach (MidiChannelRoot root in project.MidiChannelRootsInOrder())
        {
            cancellationToken.ThrowIfCancellationRequested();
            PureMidiTrack[] rootTracks = globalTracks
                .Where(value => value.MidiChannelRootId == root.Id)
                .ToArray();
            bool participatesInRequest = request.IncludedTrackIds is null
                || rootTracks.Any(value => request.IncludedTrackIds.Contains(value.Id));
            List<PureMidiTrackPlan> tracks = [];
            foreach (PureMidiTrack track in rootTracks)
            {
                if (request.IncludedTrackIds is not null
                    && !request.IncludedTrackIds.Contains(track.Id))
                {
                    continue;
                }
                int childOrder = globalTrackOrder[track.Id];

                MidiSegment[] segments = track.Segments
                    .Where(segment => IsRepresentable(segment)
                        && segment.ProjectStartTick < endTick
                        && segment.ProjectStartTick + segment.LengthTicks > startTick)
                    .OrderBy(segment => segment.ProjectStartTick)
                    .ThenBy(segment => segment.Id)
                    .ToArray();
                long trackEnd = track.Segments
                    .Where(IsRepresentable)
                    .Select(segment => segment.ProjectStartTick + segment.LengthTicks)
                    .DefaultIfEmpty(0)
                    .Max();
                long exportEnd = Math.Max(0, Math.Min(trackEnd, endTick) - startTick);
                PureMidiTrackPlan trackPlan = new(
                    track,
                    rootOrder,
                    childOrder,
                    segments,
                    exportEnd);
                tracks.Add(trackPlan);
                descriptors.Add(new(
                    track.Id,
                    CanonicalSmfTrackKind.PureMidiTrack,
                    track.Name,
                    root.FixedZeroBasedPort,
                    root.FixedZeroBasedChannel,
                    exportEnd,
                    root.Id,
                    track.Id,
                    root.ChannelMode,
                    root.Name,
                    rootOrder,
                    childOrder,
                    root.RoutingMode));
            }

            PureMidiTrackPlan[] rangeTracks = tracks.ToArray();
            PureMidiTrackPlan[] fullTracks = rangeTracks
                .Select(value => value with
                {
                    Segments = value.Track.Segments
                        .Where(IsRepresentable)
                        .OrderBy(segment => segment.ProjectStartTick)
                        .ThenBy(segment => segment.Id)
                        .ToArray()
                })
                .ToArray();
            PureMidiRootInterval[] intervals = BuildRootIntervals(fullTracks, cancellationToken)
                .Where(value => value.StartTick < endTick && value.EndTick > startTick)
                .ToArray();
            roots.Add(new(root, rangeTracks, intervals, participatesInRequest));
            rootOrder++;
        }

        bool usesPagedContent = roots
            .SelectMany(value => value.Tracks)
            .SelectMany(value => value.Segments)
            .Any(value => value.UsesPagedContent);

        // Small in-memory Projects freeze opaque events here. Paged Projects keep
        // them in the immutable source pack and project them by SMF Track on demand.
        foreach (PureMidiRootPlan rootPlan in usesPagedContent
            ? Enumerable.Empty<PureMidiRootPlan>()
            : roots)
        {
            foreach (PureMidiTrackPlan trackPlan in rootPlan.Tracks)
            {
                foreach (MidiSegment segment in trackPlan.Segments)
                {
                    long contentEnd = checked(segment.ContentOffsetTick + segment.LengthTicks);
                    foreach (OpaqueMidiEvent value in segment.OpaqueEvents
                        .Where(value => value.Tick >= segment.ContentOffsetTick
                            && value.Tick < contentEnd)
                        .OrderBy(value => value.Tick)
                        .ThenBy(value => value.Order)
                        .ThenBy(value => value.Id))
                    {
                        long absoluteTick = checked(
                            segment.ProjectStartTick + value.Tick - segment.ContentOffsetTick);
                        if (absoluteTick < startTick || absoluteTick >= endTick)
                        {
                            continue;
                        }
                        SourceReference source = new(
                            TrackId: trackPlan.Track.Id,
                            SegmentId: segment.Id,
                            SourceEventId: value.Id,
                            Tick: absoluteTick,
                            Origin: SourceOrigin.OpaqueMidiEvent,
                            MidiChannelRootId: rootPlan.Root.Id,
                            PureMidiTrackId: trackPlan.Track.Id,
                            MidiSegmentId: segment.Id,
                            DirectMidiObjectId: value.Id,
                            ExportTrackId: trackPlan.Track.Id);
                        opaque.Add(new(
                            trackPlan.Track.Id,
                            absoluteTick,
                            value.Kind,
                            value.MetaType,
                            value.Payload.ToArray(),
                            value.Order,
                            source,
                            trackPlan.TrackOrder));
                    }
                }
            }
        }

        return new(
            roots.ToArray(),
            descriptors.ToArray(),
            opaque.OrderBy(value => value.Tick)
                .ThenBy(value => value.ExportTrackId)
                .ThenBy(value => value.StableOrder)
                .ToArray(),
            BuildChannelModeSystemExclusiveEvents(
                roots,
                startTick,
                endTick,
                cancellationToken));

        static bool IsRepresentable(MidiSegment segment) =>
            segment.ProjectStartTick >= 0
            && segment.LengthTicks > 0
            && segment.ProjectStartTick <= long.MaxValue - segment.LengthTicks
            && segment.ContentOffsetTick >= 0
            && segment.ContentOffsetTick <= long.MaxValue - segment.LengthTicks;
    }

    private static PureMidiChannelModeSystemExclusiveEvent[]
        BuildChannelModeSystemExclusiveEvents(
            IReadOnlyList<PureMidiRootPlan> roots,
            long startTick,
            long endTick,
            CancellationToken cancellationToken)
    {
        List<PureMidiChannelModeSystemExclusiveEvent> result = [];
        foreach (PureMidiRootPlan rootPlan in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<PureMidiChannelModeSystemExclusiveEvent> all = [];
            foreach (PureMidiTrackPlan trackPlan in rootPlan.Tracks)
            {
                foreach (MidiSegment segment in trackPlan.Track.Segments
                    .Where(IsRepresentableMidiSegment))
                {
                    long contentStart = segment.ContentOffsetTick;
                    long contentEnd = checked(contentStart + segment.LengthTicks);
                    foreach (OpaqueMidiEventValue opaque in segment.OpaqueEvents.QueryValues(
                        contentStart,
                        contentEnd))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (opaque.Kind != OpaqueMidiEventKind.SystemExclusive
                            || !MidiChannelModeSystemExclusive.TryParseF0Payload(
                                opaque.Payload.Span,
                                out MidiChannelModeSystemExclusive parsed))
                        {
                            continue;
                        }
                        long tick = checked(
                            segment.ProjectStartTick + opaque.Tick - contentStart);
                        all.Add(new(
                            rootPlan.Root.Id,
                            trackPlan.Track.Id,
                            segment.Id,
                            opaque.Id,
                            tick,
                            parsed,
                            CanonicalEventRole.DirectMidi,
                            opaque.Order,
                            trackPlan.TrackOrder,
                            opaque.Order));
                    }
                }
            }

            all.Sort(CompareChannelModeSystemExclusive);
            result.AddRange(all.Where(value => value.Tick >= startTick && value.Tick < endTick));

            if (startTick <= 0) continue;
            PureMidiRootInterval? activeInterval = BuildFullRootIntervals(rootPlan)
                .FirstOrDefault(value => value.StartTick < startTick && value.EndTick > startTick);
            if (activeInterval is null) continue;
            PureMidiChannelModeSystemExclusiveEvent[] restoreCandidates = all
                .Where(value => value.Tick >= activeInterval.StartTick && value.Tick < startTick)
                .ToArray();
            if (restoreCandidates.Length != 0)
            {
                PureMidiChannelModeSystemExclusiveEvent restored = restoreCandidates[^1];
                result.Add(restored with
                {
                    Tick = startTick,
                    Role = CanonicalEventRole.RangeRestore
                });
            }
        }
        result.Sort(CompareChannelModeSystemExclusive);
        return result.ToArray();
    }

    private static PureMidiRootInterval[] BuildFullRootIntervals(PureMidiRootPlan rootPlan)
    {
        PureMidiTrackPlan[] fullTracks = rootPlan.Tracks
            .Select(value => value with
            {
                Segments = value.Track.Segments
                    .Where(IsRepresentableMidiSegment)
                    .OrderBy(segment => segment.ProjectStartTick)
                    .ThenBy(segment => segment.Id)
                    .ToArray()
            })
            .ToArray();
        return BuildRootIntervals(fullTracks, CancellationToken.None);
    }

    private static int CompareChannelModeSystemExclusive(
        PureMidiChannelModeSystemExclusiveEvent left,
        PureMidiChannelModeSystemExclusiveEvent right)
    {
        int value = left.Tick.CompareTo(right.Tick);
        if (value != 0) return value;
        value = left.Role.CompareTo(right.Role);
        if (value != 0) return value;
        value = left.SmfTrackOrder.CompareTo(right.SmfTrackOrder);
        if (value != 0) return value;
        value = left.SmfEventOrder.CompareTo(right.SmfEventOrder);
        if (value != 0) return value;
        return left.ObjectId.CompareTo(right.ObjectId);
    }

    private static bool IsRepresentableMidiSegment(MidiSegment segment) =>
        segment.ProjectStartTick >= 0
        && segment.LengthTicks > 0
        && segment.ProjectStartTick <= long.MaxValue - segment.LengthTicks
        && segment.ContentOffsetTick >= 0
        && segment.ContentOffsetTick <= long.MaxValue - segment.LengthTicks;

    private static CanonicalMidiChannelModeSystemExclusiveEvent[]
        MaterializePureMidiChannelModeSystemExclusiveEvents(
            PureMidiPlan plan,
            IReadOnlyDictionary<MidoraId, int> unitByRoot)
    {
        CanonicalMidiChannelModeSystemExclusiveEvent[] result = new
            CanonicalMidiChannelModeSystemExclusiveEvent[
                plan.ChannelModeSystemExclusiveEvents.Length];
        for (int index = 0; index < result.Length; index++)
        {
            PureMidiChannelModeSystemExclusiveEvent value =
                plan.ChannelModeSystemExclusiveEvents[index];
            int unit = unitByRoot[value.RootId];
            byte port = checked((byte)(unit >> 4));
            byte channel = checked((byte)(unit & 15));
            SourceReference source = new(
                TrackId: value.TrackId,
                SegmentId: value.SegmentId,
                SourceEventId: value.ObjectId,
                Tick: value.Tick,
                Origin: value.Role == CanonicalEventRole.RangeRestore
                    ? SourceOrigin.RangeRestore
                    : SourceOrigin.OpaqueMidiEvent,
                MidiChannelRootId: value.RootId,
                PureMidiTrackId: value.TrackId,
                MidiSegmentId: value.SegmentId,
                DirectMidiObjectId: value.ObjectId,
                ExportTrackId: value.TrackId);
            result[index] = new(
                value.Tick,
                port,
                channel,
                value.Value,
                value.Role,
                value.StableOrder,
                source,
                value.TrackId,
                value.SmfTrackOrder,
                value.SmfEventOrder);
        }
        return result;
    }

    private static void AppendPureMidiExportCompatibilityDiagnostics(
        PureMidiPlan plan,
        CompilationRequest request,
        long startTick,
        long endTick,
        ICollection<CompilerDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (request.Purpose != CompilationPurpose.MidiExport)
        {
            return;
        }

        foreach (PureMidiRootPlan rootPlan in plan.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Cross-MTrk ordering loss is impossible when the Root exports one MTrk.
            // This is also the dominant imported-MIDI case and must not materialize
            // two compatibility records for every Note in an extreme Track.
            if (rootPlan.Tracks.Length < 2) continue;
            using BoundedCanonicalSmfEventSorter sorter = new();
            foreach (PureMidiTrackPlan trackPlan in rootPlan.Tracks)
            {
                foreach (MidiSegment segment in trackPlan.Segments)
                {
                    long contentEnd = checked(segment.ContentOffsetTick + segment.LengthTicks);
                    long segmentEnd = checked(segment.ProjectStartTick + segment.LengthTicks);
                    foreach (DirectMidiNoteValue note in segment.Notes.QueryValues(
                        segment.ContentOffsetTick,
                        contentEnd))
                    {
                        if (note.StartTick < segment.ContentOffsetTick || note.StartTick >= contentEnd)
                        {
                            continue;
                        }
                        long absoluteStart = checked(
                            segment.ProjectStartTick + note.StartTick - segment.ContentOffsetTick);
                        long absoluteEnd = Math.Min(
                            segmentEnd,
                            checked(absoluteStart + note.LengthTicks));
                        Add(
                            absoluteStart,
                            MidiMessage.NoteOn(
                                0,
                                checked((byte)note.Key),
                                checked((byte)note.NoteOnVelocity)),
                            note.Id);
                        Add(
                            absoluteEnd,
                            MidiMessage.NoteOff(
                                0,
                                checked((byte)note.Key),
                                checked((byte)note.NoteOffVelocity)),
                            note.Id);
                    }
                    foreach (DirectMidiChannelEventValue value in segment.ChannelEvents.QueryValues(
                        segment.ContentOffsetTick,
                        contentEnd))
                    {
                        if (value.Tick < segment.ContentOffsetTick || value.Tick >= contentEnd)
                        {
                            continue;
                        }
                        long absoluteTick = checked(
                            segment.ProjectStartTick + value.Tick - segment.ContentOffsetTick);
                        Add(absoluteTick, ToMidiMessage(value, channel: 0), value.Id);
                    }

                    void Add(long tick, MidiMessage message, MidoraId objectId)
                    {
                        if (tick < startTick || tick > endTick)
                        {
                            return;
                        }
                        sorter.Add(new(
                            trackPlan.Track.Id,
                            tick,
                            0,
                            0,
                            message,
                            CanonicalEventRole.DirectMidi,
                            trackPlan.TrackOrder,
                            objectId));
                    }
                }
            }

            long? firstTick = null;
            long currentTick = long.MinValue;
            PureMidiCompatibilityTickState tickState = new();
            foreach (CanonicalSmfTrackChannelEvent value in sorter
                .ReadPages(cancellationToken)
                .SelectMany(page => page.Items))
            {
                if (value.Tick != currentTick)
                {
                    currentTick = value.Tick;
                    tickState.Reset();
                }
                if (!tickState.Add(value.ExportTrackId, value.Message)) continue;
                firstTick = value.Tick;
                break;
            }
            if (!firstTick.HasValue)
            {
                continue;
            }
            diagnostics.Add(new(
                "MIDORA2251",
                DiagnosticSeverity.Warning,
                $"MIDI Channel Root '{rootPlan.Root.Name}' has cross-Track same-tick order-sensitive events; the first combination is at tick {firstTick}. SMF players may not preserve Midora's cross-MTrk order.",
                new(MidiChannelRootId: rootPlan.Root.Id, Tick: firstTick.Value)));
        }
    }

    private sealed class PureMidiCompatibilityTickState
    {
        private readonly TrackWitness[] _noteOns = new TrackWitness[128];
        private readonly TrackWitness[] _noteOffs = new TrackWitness[128];
        private readonly Dictionary<int, StateTargetWitness> _stateTargets = [];
        private TrackWitness _any;
        private TrackWitness _channelMode;
        private TrackWitness _bankProgramOrReset;
        private TrackWitness _anyNoteOn;

        public void Reset()
        {
            _any = default;
            _channelMode = default;
            _bankProgramOrReset = default;
            _anyNoteOn = default;
            Array.Clear(_noteOns);
            Array.Clear(_noteOffs);
            _stateTargets.Clear();
        }

        public bool Add(MidoraId trackId, MidiMessage message)
        {
            bool channelMode = IsChannelModeMessage(message);
            bool bankProgramOrReset = IsBankProgramOrResetMessage(message);
            bool noteOn = IsNoteOnMessage(message);
            bool noteOff = IsNoteOffMessage(message);
            if (channelMode ? _any.HasOther(trackId) : _channelMode.HasOther(trackId))
                return true;
            if (bankProgramOrReset && _anyNoteOn.HasOther(trackId)
                || noteOn && _bankProgramOrReset.HasOther(trackId))
            {
                return true;
            }
            if (noteOn && _noteOffs[message.Byte1].HasOther(trackId)
                || noteOff && _noteOns[message.Byte1].HasOther(trackId))
            {
                return true;
            }
            if (TryGetDirectStateTarget(message, out int target))
            {
                if (!_stateTargets.TryGetValue(target, out StateTargetWitness? witness))
                {
                    witness = new();
                    _stateTargets.Add(target, witness);
                }
                if (witness.Add(trackId, message.PackedValue)) return true;
            }

            _any.Add(trackId);
            if (channelMode) _channelMode.Add(trackId);
            if (bankProgramOrReset) _bankProgramOrReset.Add(trackId);
            if (noteOn)
            {
                _anyNoteOn.Add(trackId);
                _noteOns[message.Byte1].Add(trackId);
            }
            if (noteOff) _noteOffs[message.Byte1].Add(trackId);
            return false;
        }
    }

    private struct TrackWitness
    {
        private MidoraId _firstTrackId;
        private bool _hasMultipleTracks;

        public bool HasOther(MidoraId trackId) => _firstTrackId != default
            && (_hasMultipleTracks || _firstTrackId != trackId);

        public void Add(MidoraId trackId)
        {
            if (_firstTrackId == default) _firstTrackId = trackId;
            else if (_firstTrackId != trackId) _hasMultipleTracks = true;
        }
    }

    private sealed class StateTargetWitness
    {
        private MidoraId _firstTrackId;
        private uint _firstValue;
        private bool _firstTrackHasMultipleValues;
        private bool _hasMultipleTracks;

        public bool Add(MidoraId trackId, uint value)
        {
            if (_firstTrackId == default)
            {
                _firstTrackId = trackId;
                _firstValue = value;
                return false;
            }
            if (_hasMultipleTracks) return value != _firstValue;
            if (trackId == _firstTrackId)
            {
                _firstTrackHasMultipleValues |= value != _firstValue;
                return false;
            }
            if (_firstTrackHasMultipleValues || value != _firstValue) return true;
            _hasMultipleTracks = true;
            return false;
        }
    }

    private static bool IsNoteOnMessage(MidiMessage value) =>
        value.MessageType == MidiMessageType.NoteOn && value.Byte2 != 0;

    private static bool IsNoteOffMessage(MidiMessage value) =>
        value.MessageType == MidiMessageType.NoteOff
        || value.MessageType == MidiMessageType.NoteOn && value.Byte2 == 0;

    private static bool IsChannelModeMessage(MidiMessage value) =>
        value.MessageType == MidiMessageType.ControlChange && value.Byte1 >= 120;

    private static bool IsBankProgramOrResetMessage(MidiMessage value) =>
        value.MessageType == MidiMessageType.ProgramChange
        || value.MessageType == MidiMessageType.ControlChange
            && (value.Byte1 is 0 or 32 or 121 || value.Byte1 >= 120);

    private static bool TryGetDirectStateTarget(MidiMessage value, out int target)
    {
        switch (value.MessageType)
        {
            case MidiMessageType.ControlChange:
                target = 0x10000 + value.Byte1;
                return true;
            case MidiMessageType.ProgramChange:
                target = 0x20000;
                return true;
            case MidiMessageType.PitchWheelChange:
                target = 0x30000;
                return true;
            case MidiMessageType.ChannelPressure:
                target = 0x40000;
                return true;
            case MidiMessageType.PolyphonicKeyPressure:
                target = 0x50000 + value.Byte1;
                return true;
            default:
                target = 0;
                return false;
        }
    }

    private static PureMidiRootInterval[] BuildRootIntervals(
        IReadOnlyList<PureMidiTrackPlan> tracks,
        CancellationToken cancellationToken)
    {
        List<(long Start, long End, int TrackOrder, MidoraId TrackId, MidoraId SegmentId)> ranges = [];
        foreach (PureMidiTrackPlan track in tracks)
        {
            foreach (MidiSegment segment in track.Segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ranges.Add((
                    segment.ProjectStartTick,
                    checked(segment.ProjectStartTick + segment.LengthTicks),
                    track.TrackOrder,
                    track.Track.Id,
                    segment.Id));
            }
        }
        if (ranges.Count == 0)
        {
            return [];
        }

        ranges.Sort(static (left, right) =>
        {
            int value = left.Start.CompareTo(right.Start);
            if (value != 0) return value;
            value = left.TrackOrder.CompareTo(right.TrackOrder);
            return value != 0 ? value : left.SegmentId.CompareTo(right.SegmentId);
        });
        List<PureMidiRootInterval> result = [];
        int index = 0;
        while (index < ranges.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = ranges[index];
            long start = first.Start;
            long end = first.End;
            List<(long Start, long End, int TrackOrder, MidoraId TrackId, MidoraId SegmentId)> members = [first];
            index++;
            while (index < ranges.Count && ranges[index].Start <= end)
            {
                var current = ranges[index++];
                end = Math.Max(end, current.End);
                members.Add(current);
            }
            var startOwner = members
                .Where(value => value.Start == start)
                .OrderBy(value => value.TrackOrder)
                .ThenBy(value => value.SegmentId)
                .First();
            var endOwner = members
                .Where(value => value.End == end)
                .OrderBy(value => value.TrackOrder)
                .ThenBy(value => value.SegmentId)
                .First();
            result.Add(new(
                start,
                end,
                startOwner.SegmentId,
                startOwner.TrackId,
                startOwner.SegmentId,
                endOwner.TrackId,
                endOwner.SegmentId));
        }
        return result.ToArray();
    }

    private static List<CanonicalMidiEvent> MaterializePureMidiEvents(
        PureMidiPlan plan,
        IReadOnlyDictionary<MidoraId, int> unitByRoot,
        MidiInitialState resetDefaults,
        long startTick,
        CancellationToken cancellationToken)
    {
        List<CanonicalMidiEvent> result = [];
        foreach (PureMidiRootPlan rootPlan in plan.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!unitByRoot.TryGetValue(rootPlan.Root.Id, out int unit))
            {
                continue;
            }
            byte port = checked((byte)(unit >> 4));
            byte channel = checked((byte)(unit & 15));
            List<PureMidiPendingEvent> pending = [];
            foreach (PureMidiTrackPlan trackPlan in rootPlan.Tracks)
            {
                foreach (MidiSegment segment in trackPlan.Segments)
                {
                    MaterializePureMidiSegment(
                        rootPlan.Root,
                        trackPlan,
                        segment,
                        channel,
                        pending,
                        cancellationToken);
                }
            }
            AppendPureMidiHistoricalStateEvents(
                rootPlan,
                startTick,
                channel,
                pending,
                cancellationToken);

            pending.Sort(static (left, right) =>
            {
                int value = left.Tick.CompareTo(right.Tick);
                if (value != 0) return value;
                value = left.TrackOrder.CompareTo(right.TrackOrder);
                if (value != 0) return value;
                value = left.ExplicitOrder.CompareTo(right.ExplicitOrder);
                if (value != 0) return value;
                value = left.EndpointOrder.CompareTo(right.EndpointOrder);
                return value != 0 ? value : left.StableId.CompareTo(right.StableId);
            });

            foreach (PureMidiPendingEvent value in pending)
            {
                long stableOrder = EncodeDirectMidiStableOrder(value.Message, value.StableId);
                result.Add(new(
                    value.Tick,
                    port,
                    channel,
                    value.Message,
                    value.Role,
                    stableOrder,
                    value.SemanticTargetKey,
                    stableOrder,
                    value.Source,
                    value.ExportTrackId,
                    value.TrackOrder,
                    value.ExplicitOrder));
            }

            foreach (PureMidiRootInterval interval in rootPlan.Intervals)
            {
                long[] usedTargets = pending
                    .Where(value => value.Tick >= interval.StartTick
                        && value.Tick < interval.EndTick
                        && value.SemanticTargetKey != long.MinValue)
                    .Select(value => value.SemanticTargetKey)
                    .Distinct()
                    .Order()
                    .ToArray();
                SourceReference startSource = RootLifecycleSource(
                    rootPlan.Root.Id,
                    interval.StartOwnerTrackId,
                    interval.StartOwnerSegmentId,
                    interval.StartTick,
                    interval.StartOwnerTrackId);
                int startTrackOrder = rootPlan.Tracks
                    .Single(value => value.Track.Id == interval.StartOwnerTrackId)
                    .TrackOrder;
                long startOrder = long.MinValue / 4 + interval.StartTick;
                result.Add(new(
                    interval.StartTick,
                    port,
                    channel,
                    MidiMessage.ControlChange(channel, 121, 0),
                    CanonicalEventRole.Reset,
                    startOrder++,
                    ControlTargetKey(121),
                    startOrder,
                    startSource,
                    interval.StartOwnerTrackId,
                    startTrackOrder,
                    long.MinValue));
                AppendPureRootDefaults(
                    result,
                    interval.StartTick,
                    port,
                    channel,
                    usedTargets,
                    resetDefaults,
                    startSource,
                    interval.StartOwnerTrackId,
                    startTrackOrder,
                    ref startOrder);

                SourceReference endSource = RootLifecycleSource(
                    rootPlan.Root.Id,
                    interval.EndOwnerTrackId,
                    interval.EndOwnerSegmentId,
                    interval.EndTick,
                    interval.EndOwnerTrackId);
                int endTrackOrder = rootPlan.Tracks
                    .Single(value => value.Track.Id == interval.EndOwnerTrackId)
                    .TrackOrder;
                long endOrder = long.MaxValue / 4 + interval.EndTick;
                result.Add(new(
                    interval.EndTick,
                    port,
                    channel,
                    MidiMessage.ControlChange(channel, AllSoundOffController, 0),
                    CanonicalEventRole.RootBoundaryCleanup,
                    endOrder++,
                    ControlTargetKey(AllSoundOffController),
                    endOrder,
                    endSource,
                    interval.EndOwnerTrackId,
                    endTrackOrder,
                    long.MaxValue));
                AppendPureRootDefaults(
                    result,
                    interval.EndTick,
                    port,
                    channel,
                    usedTargets,
                    resetDefaults,
                    endSource,
                    interval.EndOwnerTrackId,
                    endTrackOrder,
                    ref endOrder,
                    CanonicalEventRole.RootBoundaryCleanup);
            }
        }
        result.Sort(CanonicalComparer.Instance);
        return result;
    }

    private static void AppendPureMidiHistoricalStateEvents(
        PureMidiRootPlan rootPlan,
        long startTick,
        byte channel,
        ICollection<PureMidiPendingEvent> output,
        CancellationToken cancellationToken)
    {
        if (startTick <= 0)
        {
            return;
        }

        PureMidiRootInterval? activeInterval = rootPlan.Intervals
            .SingleOrDefault(value => value.StartTick < startTick && value.EndTick > startTick);
        if (activeInterval is null)
        {
            return;
        }

        Dictionary<long, PureMidiHistoricalStateCandidate> state = [];
        foreach (PureMidiTrackPlan trackPlan in rootPlan.Tracks)
        {
            HashSet<MidoraId> materializedSegmentIds = trackPlan.Segments
                .Select(value => value.Id)
                .ToHashSet();
            foreach (MidiSegment segment in trackPlan.Track.Segments
                .Where(IsRepresentableMidiSegment)
                .Where(value => !materializedSegmentIds.Contains(value.Id))
                .Where(value => value.ProjectStartTick < startTick
                    && checked(value.ProjectStartTick + value.LengthTicks) > activeInterval.StartTick)
                .OrderBy(value => value.ProjectStartTick)
                .ThenBy(value => value.Id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                long contentStart = segment.ContentOffsetTick;
                long contentEnd = checked(contentStart + segment.LengthTicks);
                long absoluteHistoryEnd = Math.Min(
                    startTick,
                    checked(segment.ProjectStartTick + segment.LengthTicks));
                long historyContentEnd = checked(
                    contentStart + absoluteHistoryEnd - segment.ProjectStartTick);
                foreach (DirectMidiChannelEventValue value in segment.ChannelEvents.QueryOrderedValues(
                    contentStart,
                    Math.Min(contentEnd, historyContentEnd)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long absoluteTick = checked(
                        segment.ProjectStartTick + value.Tick - contentStart);
                    if (absoluteTick < activeInterval.StartTick || absoluteTick >= startTick)
                    {
                        continue;
                    }
                    MidiMessage message = ToMidiMessage(value, channel);
                    long target = SemanticTargetForMessage(message);
                    if (target == long.MinValue)
                    {
                        continue;
                    }
                    PureMidiHistoricalStateCandidate candidate = new(
                        absoluteTick,
                        trackPlan,
                        segment,
                        value,
                        message,
                        target);
                    if (!state.TryGetValue(target, out PureMidiHistoricalStateCandidate current)
                        || CompareHistoricalStateCandidate(candidate, current) > 0)
                    {
                        state[target] = candidate;
                    }
                }
            }
        }

        foreach (PureMidiHistoricalStateCandidate candidate in state.Values)
        {
            DirectMidiChannelEventValue value = candidate.Value;
            SourceReference source = DirectSource(
                rootPlan.Root.Id,
                candidate.TrackPlan.Track.Id,
                candidate.Segment.Id,
                value.Id,
                candidate.AbsoluteTick,
                SourceOrigin.DirectMidiChannelEvent);
            output.Add(new(
                candidate.AbsoluteTick,
                candidate.TrackPlan.TrackOrder,
                value.Order,
                1,
                value.Id,
                candidate.Message,
                source,
                candidate.TrackPlan.Track.Id,
                CanonicalEventRole.DirectMidi,
                candidate.SemanticTargetKey));
        }
    }

    private static int CompareHistoricalStateCandidate(
        PureMidiHistoricalStateCandidate left,
        PureMidiHistoricalStateCandidate right)
    {
        int value = left.AbsoluteTick.CompareTo(right.AbsoluteTick);
        if (value != 0) return value;
        value = left.TrackPlan.TrackOrder.CompareTo(right.TrackPlan.TrackOrder);
        if (value != 0) return value;
        value = left.Value.Order.CompareTo(right.Value.Order);
        return value != 0 ? value : left.Value.Id.CompareTo(right.Value.Id);
    }

    private static CanonicalSmfTrackDescriptor[] FreezeSmfTrackDescriptors(
        PureMidiPlan plan,
        IReadOnlyDictionary<MidoraId, int> unitByRoot)
    {
        return plan.TrackDescriptors.Select(value =>
        {
            if (!unitByRoot.TryGetValue(value.MidiChannelRootId, out int unit))
            {
                return value;
            }
            return value with
            {
                ZeroBasedPort = checked((byte)(unit >> 4)),
                ZeroBasedChannel = checked((byte)(unit & 15))
            };
        }).ToArray();
    }

    private static CanonicalMidiEvent[] AssignPureMidiRangeBoundaryOwnership(
        CanonicalMidiEvent[] events,
        PureMidiPlan plan,
        IReadOnlyDictionary<MidoraId, int> unitByRoot,
        long endTick)
    {
        Dictionary<(byte Port, byte Channel), (MidiChannelRoot Root, PureMidiRootInterval Interval)>
            boundaries = [];
        foreach (PureMidiRootPlan rootPlan in plan.Roots)
        {
            if (!unitByRoot.TryGetValue(rootPlan.Root.Id, out int unit))
            {
                continue;
            }
            PureMidiRootInterval? interval = rootPlan.Intervals
                .Where(value => value.StartTick < endTick && value.EndTick >= endTick)
                .OrderByDescending(value => value.StartTick)
                .FirstOrDefault();
            if (interval is not null)
            {
                boundaries[(checked((byte)(unit >> 4)), checked((byte)(unit & 15)))] =
                    (rootPlan.Root, interval);
            }
        }
        for (int index = 0; index < events.Length; index++)
        {
            CanonicalMidiEvent value = events[index];
            if (value.Tick != endTick
                || value.Source.MidiChannelRootId != default
                || value.Role != CanonicalEventRole.Reset
                || !boundaries.TryGetValue(
                    (value.ZeroBasedPort, value.ZeroBasedChannel),
                    out var boundary))
            {
                continue;
            }
            SourceReference source = RootLifecycleSource(
                boundary.Root.Id,
                boundary.Interval.EndOwnerTrackId,
                boundary.Interval.EndOwnerSegmentId,
                endTick,
                boundary.Interval.EndOwnerTrackId);
            int smfTrackOrder = plan.Roots
                .Single(root => root.Root.Id == boundary.Root.Id)
                .Tracks.Single(track => track.Track.Id == boundary.Interval.EndOwnerTrackId)
                .TrackOrder;
            events[index] = value with
            {
                Role = CanonicalEventRole.RootBoundaryCleanup,
                Source = source,
                ExportTrackId = boundary.Interval.EndOwnerTrackId,
                SmfTrackOrder = smfTrackOrder,
                SmfEventOrder = long.MaxValue
            };
        }
        return events;
    }

    private static void MaterializePureMidiSegment(
        MidiChannelRoot root,
        PureMidiTrackPlan trackPlan,
        MidiSegment segment,
        byte channel,
        List<PureMidiPendingEvent> output,
        CancellationToken cancellationToken)
    {
        long contentEnd = checked(segment.ContentOffsetTick + segment.LengthTicks);
        long segmentEnd = checked(segment.ProjectStartTick + segment.LengthTicks);
        foreach (DirectMidiNote note in segment.Notes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (note.StartTick < segment.ContentOffsetTick || note.StartTick >= contentEnd)
            {
                continue;
            }
            long absoluteStart = checked(
                segment.ProjectStartTick + note.StartTick - segment.ContentOffsetTick);
            long absoluteEnd = Math.Min(
                segmentEnd,
                checked(absoluteStart + note.LengthTicks));
            SourceReference source = DirectSource(
                root.Id,
                trackPlan.Track.Id,
                segment.Id,
                note.Id,
                absoluteStart,
                SourceOrigin.DirectMidiNote);
            output.Add(new(
                absoluteStart,
                trackPlan.TrackOrder,
                note.NoteOnOrder,
                1,
                note.Id,
                MidiMessage.NoteOn(channel, checked((byte)note.Key), checked((byte)note.NoteOnVelocity)),
                source,
                trackPlan.Track.Id,
                CanonicalEventRole.DirectMidi,
                long.MinValue));
            output.Add(new(
                absoluteEnd,
                trackPlan.TrackOrder,
                note.NoteOffOrder,
                0,
                note.Id,
                MidiMessage.NoteOff(channel, checked((byte)note.Key), checked((byte)note.NoteOffVelocity)),
                source with { Tick = absoluteEnd },
                trackPlan.Track.Id,
                CanonicalEventRole.DirectMidi,
                long.MinValue));
        }

        Dictionary<byte, Queue<SourceReference>> rawNotes = [];
        foreach (DirectMidiChannelEvent value in segment.ChannelEvents
            .Where(value => value.Tick >= segment.ContentOffsetTick && value.Tick < contentEnd)
            .OrderBy(value => value.Tick)
            .ThenBy(value => value.Order)
            .ThenBy(value => value.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            long absoluteTick = checked(
                segment.ProjectStartTick + value.Tick - segment.ContentOffsetTick);
            MidiMessage message = ToMidiMessage(value, channel);
            SourceReference source = DirectSource(
                root.Id,
                trackPlan.Track.Id,
                segment.Id,
                value.Id,
                absoluteTick,
                SourceOrigin.DirectMidiChannelEvent);
            long target = SemanticTargetForMessage(message);
            output.Add(new(
                absoluteTick,
                trackPlan.TrackOrder,
                value.Order,
                1,
                value.Id,
                message,
                source,
                trackPlan.Track.Id,
                CanonicalEventRole.DirectMidi,
                target));

            if (message.MessageType == MidiMessageType.NoteOn && message.Byte2 != 0)
            {
                if (!rawNotes.TryGetValue(message.Byte1, out Queue<SourceReference>? queue))
                {
                    queue = new();
                    rawNotes.Add(message.Byte1, queue);
                }
                queue.Enqueue(source);
            }
            else if (message.MessageType == MidiMessageType.NoteOff
                || message.MessageType == MidiMessageType.NoteOn && message.Byte2 == 0)
            {
                if (rawNotes.TryGetValue(message.Byte1, out Queue<SourceReference>? queue)
                    && queue.Count != 0)
                {
                    _ = queue.Dequeue();
                }
            }
        }

        // Unpaired raw NoteOns remain raw source data, but the Segment hard
        // boundary still closes the currently active instances exactly.
        long boundaryOrder = long.MaxValue / 8;
        foreach ((byte key, Queue<SourceReference> sources) in rawNotes.OrderBy(value => value.Key))
        {
            while (sources.Count != 0)
            {
                SourceReference source = sources.Dequeue() with
                {
                    Tick = segmentEnd,
                    Origin = SourceOrigin.CompilerBoundaryCleanup
                };
                output.Add(new(
                    segmentEnd,
                    trackPlan.TrackOrder,
                    boundaryOrder++,
                    0,
                    source.DirectMidiObjectId,
                    MidiMessage.NoteOff(channel, key, 0),
                    source,
                    trackPlan.Track.Id,
                    CanonicalEventRole.DirectMidi,
                    long.MinValue));
            }
        }
    }

    private static MidiMessage ToMidiMessage(DirectMidiChannelEvent value, byte channel) =>
        value.Kind switch
        {
            DirectMidiChannelEventKind.NoteOff => MidiMessage.NoteOff(
                channel, checked((byte)value.Data1), checked((byte)value.Data2)),
            DirectMidiChannelEventKind.NoteOn => MidiMessage.NoteOn(
                channel, checked((byte)value.Data1), checked((byte)value.Data2)),
            DirectMidiChannelEventKind.PolyphonicKeyPressure => MidiMessage.PolyphonicKeyPressure(
                channel, checked((byte)value.Data1), checked((byte)value.Data2)),
            DirectMidiChannelEventKind.ControlChange => MidiMessage.ControlChange(
                channel, checked((byte)value.Data1), checked((byte)value.Data2)),
            DirectMidiChannelEventKind.ProgramChange => MidiMessage.ProgramChange(
                channel, checked((byte)value.Data1)),
            DirectMidiChannelEventKind.ChannelPressure => MidiMessage.ChannelPressure(
                channel, checked((byte)value.Data1)),
            DirectMidiChannelEventKind.PitchBend => MidiMessage.PitchWheelChange(
                channel, checked((ushort)((value.Data2 << 7) | value.Data1))),
            _ => throw new InvalidDataException($"Unsupported direct MIDI event kind {value.Kind}.")
        };

    private static MidiMessage ToMidiMessage(DirectMidiChannelEventValue value, byte channel) =>
        value.Kind switch
        {
            DirectMidiChannelEventKind.NoteOff => MidiMessage.NoteOff(
                channel, checked((byte)value.Data1), checked((byte)value.Data2)),
            DirectMidiChannelEventKind.NoteOn => MidiMessage.NoteOn(
                channel, checked((byte)value.Data1), checked((byte)value.Data2)),
            DirectMidiChannelEventKind.PolyphonicKeyPressure => MidiMessage.PolyphonicKeyPressure(
                channel, checked((byte)value.Data1), checked((byte)value.Data2)),
            DirectMidiChannelEventKind.ControlChange => MidiMessage.ControlChange(
                channel, checked((byte)value.Data1), checked((byte)value.Data2)),
            DirectMidiChannelEventKind.ProgramChange => MidiMessage.ProgramChange(
                channel, checked((byte)value.Data1)),
            DirectMidiChannelEventKind.ChannelPressure => MidiMessage.ChannelPressure(
                channel, checked((byte)value.Data1)),
            DirectMidiChannelEventKind.PitchBend => MidiMessage.PitchWheelChange(
                channel, checked((ushort)((value.Data2 << 7) | value.Data1))),
            _ => throw new InvalidDataException($"Unsupported direct MIDI event kind {value.Kind}.")
        };

    private static long EncodeDirectMidiStableOrder(MidiMessage message, MidoraId stableId)
    {
        if (stableId == default)
        {
            throw new ArgumentOutOfRangeException(nameof(stableId));
        }

        // SmfEventOrder already carries the complete, non-negative Int64 explicit
        // order. StableOrder therefore only has to preserve the endpoint class and
        // the stable-object tie break. Mapping NoteOff endpoints to the negative
        // half and all other events to the positive half is injective for every
        // valid MidoraId and cannot overflow, unlike `(explicitOrder << 1)`.
        return CanonicalMidiOrdering.DirectEndpointOrder(message) == 0
            ? checked(long.MinValue + stableId.Value)
            : stableId.Value;
    }

    private static SourceReference DirectSource(
        MidoraId rootId,
        MidoraId trackId,
        MidoraId segmentId,
        MidoraId objectId,
        long tick,
        SourceOrigin origin) => new(
            TrackId: trackId,
            SegmentId: segmentId,
            SourceEventId: objectId,
            Tick: tick,
            Origin: origin,
            MidiChannelRootId: rootId,
            PureMidiTrackId: trackId,
            MidiSegmentId: segmentId,
            DirectMidiObjectId: objectId,
            ExportTrackId: trackId);

    private static SourceReference RootLifecycleSource(
        MidoraId rootId,
        MidoraId trackId,
        MidoraId segmentId,
        long tick,
        MidoraId exportTrackId) => new(
            TrackId: trackId,
            SegmentId: segmentId,
            Tick: tick,
            Origin: SourceOrigin.MidiChannelRootLifecycle,
            MidiChannelRootId: rootId,
            PureMidiTrackId: trackId,
            MidiSegmentId: segmentId,
            ExportTrackId: exportTrackId);

    private static void AppendPureRootDefaults(
        List<CanonicalMidiEvent> output,
        long tick,
        byte port,
        byte channel,
        IEnumerable<long> targets,
        MidiInitialState defaults,
        SourceReference source,
        MidoraId exportTrackId,
        int smfTrackOrder,
        ref long order,
        CanonicalEventRole role = CanonicalEventRole.Reset)
    {
        foreach (long target in targets.Order())
        {
            int before = output.Count;
            AppendCanonicalReset(output, tick, port, channel, target, defaults, ref order);
            for (int index = before; index < output.Count; index++)
            {
                CanonicalMidiEvent generated = output[index];
                output[index] = generated with
                {
                    Role = role,
                    Source = source,
                    ExportTrackId = exportTrackId,
                    SmfTrackOrder = smfTrackOrder,
                    SmfEventOrder = role == CanonicalEventRole.RootBoundaryCleanup
                        ? long.MaxValue
                        : long.MinValue
                };
            }
        }
    }
}
