using Midora.Domain;
using Midora.Midi;

namespace Midora.Compiler;

public sealed partial class MidoraCompiler
{
    private sealed record PureMidiPlan(
        PureMidiRootPlan[] Roots,
        CanonicalSmfTrackDescriptor[] TrackDescriptors,
        CanonicalOpaqueMidiEvent[] OpaqueEvents)
    {
        public static PureMidiPlan Empty { get; } = new([], [], []);
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

    private readonly record struct PureMidiCompatibilityEvent(
        long Tick,
        int TrackOrder,
        MidoraId TrackId,
        MidiMessage Message);

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

        Dictionary<MidoraId, PureMidiTrack> tracksById = project.PureMidiTracks
            .ToDictionary(value => value.Id);
        List<PureMidiRootPlan> roots = [];
        List<CanonicalSmfTrackDescriptor> descriptors = [];
        List<CanonicalOpaqueMidiEvent> opaque = [];
        int rootOrder = 0;
        foreach (MidiChannelRoot root in project.MidiChannelRootsInOrder())
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool participatesInRequest = request.IncludedTrackIds is null
                || root.MidiTrackIds.Any(request.IncludedTrackIds.Contains);
            List<PureMidiTrackPlan> tracks = [];
            int childOrder = 0;
            foreach (MidoraId trackId in root.MidiTrackIds)
            {
                if (!tracksById.TryGetValue(trackId, out PureMidiTrack? track))
                {
                    childOrder++;
                    continue;
                }
                if (request.IncludedTrackIds is not null
                    && !request.IncludedTrackIds.Contains(track.Id))
                {
                    childOrder++;
                    continue;
                }

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
                childOrder++;
            }

            PureMidiRootInterval[] intervals = BuildRootIntervals(tracks, cancellationToken);
            roots.Add(new(root, tracks.ToArray(), intervals, participatesInRequest));
            rootOrder++;
        }

        // Opaque events are frozen here because they do not participate in the
        // execution projection, but their Track/tick/order identity is canonical.
        foreach (PureMidiRootPlan rootPlan in roots)
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
                .ToArray());

        static bool IsRepresentable(MidiSegment segment) =>
            segment.ProjectStartTick >= 0
            && segment.LengthTicks > 0
            && segment.ProjectStartTick <= long.MaxValue - segment.LengthTicks
            && segment.ContentOffsetTick >= 0
            && segment.ContentOffsetTick <= long.MaxValue - segment.LengthTicks;
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
            List<PureMidiCompatibilityEvent> events = [];
            foreach (PureMidiTrackPlan trackPlan in rootPlan.Tracks)
            {
                foreach (MidiSegment segment in trackPlan.Segments)
                {
                    long contentEnd = checked(segment.ContentOffsetTick + segment.LengthTicks);
                    long segmentEnd = checked(segment.ProjectStartTick + segment.LengthTicks);
                    foreach (DirectMidiNote note in segment.Notes)
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
                                checked((byte)note.NoteOnVelocity)));
                        Add(
                            absoluteEnd,
                            MidiMessage.NoteOff(
                                0,
                                checked((byte)note.Key),
                                checked((byte)note.NoteOffVelocity)));
                    }
                    foreach (DirectMidiChannelEvent value in segment.ChannelEvents)
                    {
                        if (value.Tick < segment.ContentOffsetTick || value.Tick >= contentEnd)
                        {
                            continue;
                        }
                        long absoluteTick = checked(
                            segment.ProjectStartTick + value.Tick - segment.ContentOffsetTick);
                        Add(absoluteTick, ToMidiMessage(value, channel: 0));
                    }

                    void Add(long tick, MidiMessage message)
                    {
                        if (tick < startTick || tick > endTick)
                        {
                            return;
                        }
                        events.Add(new(
                            tick,
                            trackPlan.TrackOrder,
                            trackPlan.Track.Id,
                            message));
                    }
                }
            }

            int conflictCount = 0;
            long? firstTick = null;
            foreach (IGrouping<long, PureMidiCompatibilityEvent> tickGroup in events
                .GroupBy(value => value.Tick)
                .OrderBy(value => value.Key))
            {
                PureMidiCompatibilityEvent[] values = tickGroup
                    .OrderBy(value => value.TrackOrder)
                    .ToArray();
                for (int leftIndex = 0; leftIndex < values.Length; leftIndex++)
                {
                    for (int rightIndex = leftIndex + 1; rightIndex < values.Length; rightIndex++)
                    {
                        PureMidiCompatibilityEvent left = values[leftIndex];
                        PureMidiCompatibilityEvent right = values[rightIndex];
                        if (left.TrackId == right.TrackId
                            || !IsCrossTrackOrderSensitive(left.Message, right.Message))
                        {
                            continue;
                        }
                        conflictCount++;
                        firstTick ??= tickGroup.Key;
                    }
                }
            }
            if (conflictCount == 0)
            {
                continue;
            }
            diagnostics.Add(new(
                "MIDORA2251",
                DiagnosticSeverity.Warning,
                $"MIDI Channel Root '{rootPlan.Root.Name}' has {conflictCount} cross-Track same-tick order-sensitive event combination(s); the first is at tick {firstTick}. SMF players may not preserve Midora's cross-MTrk order.",
                new(MidiChannelRootId: rootPlan.Root.Id, Tick: firstTick!.Value)));
        }
    }

    private static bool IsCrossTrackOrderSensitive(MidiMessage left, MidiMessage right)
    {
        if (IsChannelMode(left) || IsChannelMode(right))
        {
            return true;
        }
        if ((IsBankProgramOrReset(left) && IsNoteOn(right))
            || (IsBankProgramOrReset(right) && IsNoteOn(left)))
        {
            return true;
        }
        if ((IsNoteOn(left) && IsNoteOff(right)
                || IsNoteOff(left) && IsNoteOn(right))
            && left.Byte1 == right.Byte1)
        {
            return true;
        }
        return TryGetDirectStateTarget(left, out int leftTarget)
            && TryGetDirectStateTarget(right, out int rightTarget)
            && leftTarget == rightTarget
            && left.PackedValue != right.PackedValue;

        static bool IsNoteOn(MidiMessage value) =>
            value.MessageType == MidiMessageType.NoteOn && value.Byte2 != 0;

        static bool IsNoteOff(MidiMessage value) =>
            value.MessageType == MidiMessageType.NoteOff
            || value.MessageType == MidiMessageType.NoteOn && value.Byte2 == 0;

        static bool IsChannelMode(MidiMessage value) =>
            value.MessageType == MidiMessageType.ControlChange && value.Byte1 >= 120;

        static bool IsBankProgramOrReset(MidiMessage value) =>
            value.MessageType == MidiMessageType.ProgramChange
            || value.MessageType == MidiMessageType.ControlChange
                && (value.Byte1 is 0 or 32 or 121 || value.Byte1 >= 120);
    }

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

            long stableOrder = 1L << 40;
            foreach (PureMidiPendingEvent value in pending)
            {
                result.Add(new(
                    value.Tick,
                    port,
                    channel,
                    value.Message,
                    value.Role,
                    stableOrder++,
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
