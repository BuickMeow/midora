using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Midora.Domain;
using Midora.Midi;

namespace Midora.Compiler;

public sealed partial class MidoraCompiler
{
    private sealed class PureMidiPagedCanonicalSource :
        ICanonicalMidiEventPageSource,
        ICanonicalMidiRenderPageSource,
        ICanonicalDemandFilteredMidiRenderPageSource,
        ICanonicalSmfTrackPageSource,
        ICanonicalPureMidiAudioMetadataSource
    {
        private readonly PureMidiPlan _plan;
        private readonly IReadOnlyDictionary<MidoraId, int> _unitByRoot;
        private readonly MidiInitialState _resetDefaults;
        private readonly long _compiledStartTick;
        private readonly long _compiledEndTick;
        private readonly Dictionary<(MidoraId RootId, MidoraId GroupId), RootIntervalAnalysis> _analysis = [];
        private readonly Dictionary<MidoraId, SegmentChannelAnalysis> _channelAnalysis = [];

        public PureMidiPagedCanonicalSource(
            PureMidiPlan plan,
            IReadOnlyDictionary<MidoraId, int> unitByRoot,
            MidiInitialState resetDefaults,
            long compiledStartTick,
            long compiledEndTick,
            CancellationToken cancellationToken)
        {
            _plan = plan;
            _unitByRoot = new Dictionary<MidoraId, int>(unitByRoot);
            _resetDefaults = resetDefaults.Clone();
            _compiledStartTick = compiledStartTick;
            _compiledEndTick = compiledEndTick;
            BuildAnalysis(cancellationToken);
            (EventCount, NoteOnEventCount) = CountEvents(cancellationToken);
            ContentFingerprint = CreateContentFingerprint(cancellationToken);
            PureMidiAudioFragments = BuildAudioFragments(cancellationToken);
            PureMidiPresetReferences = BuildPresetReferences(cancellationToken);
        }

        public long EventCount { get; }
        public long NoteOnEventCount { get; }
        public string ContentFingerprint { get; }
        public IReadOnlyList<CanonicalPureMidiAudioFragmentDescriptor> PureMidiAudioFragments { get; }
        public IReadOnlyList<CanonicalMidiPresetReference> PureMidiPresetReferences { get; }

        private CanonicalPureMidiAudioFragmentDescriptor[] BuildAudioFragments(
            CancellationToken cancellationToken)
        {
            List<CanonicalPureMidiAudioFragmentDescriptor> result = [];
            foreach (PureMidiRootPlan rootPlan in _plan.Roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_unitByRoot.TryGetValue(rootPlan.Root.Id, out int unit)) continue;
                foreach (PureMidiRootInterval interval in rootPlan.Intervals)
                {
                    long start = Math.Max(_compiledStartTick, interval.StartTick);
                    long end = Math.Min(_compiledEndTick, interval.EndTick);
                    if (end <= start) continue;
                    MidoraId[] contributingTrackIds = rootPlan.Tracks
                        .Where(track => track.Segments.Any(segment =>
                            segment.ProjectStartTick < interval.EndTick
                            && checked(segment.ProjectStartTick + segment.LengthTicks) > interval.StartTick))
                        .Select(track => track.Track.Id)
                        .Order()
                        .ToArray();
                    result.Add(new(
                        rootPlan.Root.Id,
                        interval.GroupId,
                        start,
                        end,
                        checked((byte)(unit >> 4)),
                        checked((byte)(unit & 15)),
                        rootPlan.Root.ChannelMode,
                        CreateAudioFragmentFingerprint(rootPlan, interval),
                        contributingTrackIds));
                }
            }
            return result.ToArray();
        }

        private CanonicalMidiPresetReference[] BuildPresetReferences(
            CancellationToken cancellationToken)
        {
            HashSet<CanonicalMidiPresetReference> result = [];
            foreach (PureMidiRootPlan rootPlan in _plan.Roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_unitByRoot.TryGetValue(rootPlan.Root.Id, out int unit)) continue;
                HashSet<byte> banks = [0];
                HashSet<byte> programs = [0];
                foreach (PureMidiTrackPlan trackPlan in rootPlan.Tracks)
                {
                    foreach (MidiSegment segment in trackPlan.Segments)
                    {
                        SegmentChannelAnalysis analysis = _channelAnalysis[segment.Id];
                        banks.UnionWith(analysis.ReferencedBanks);
                        programs.UnionWith(analysis.ReferencedPrograms);
                    }
                }
                byte port = checked((byte)(unit >> 4));
                byte channel = checked((byte)(unit & 15));
                foreach (byte bank in banks)
                    foreach (byte program in programs)
                        result.Add(new(port, channel, bank, program));
            }
            return result
                .OrderBy(value => value.ZeroBasedPort)
                .ThenBy(value => value.ZeroBasedChannel)
                .ThenBy(value => value.Bank)
                .ThenBy(value => value.Program)
                .ToArray();
        }

        private string CreateAudioFragmentFingerprint(
            PureMidiRootPlan rootPlan,
            PureMidiRootInterval interval)
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendText("MIDORA_PURE_MIDI_AUDIO_FRAGMENT_V2");
            AppendLong(rootPlan.Root.Id.Value);
            AppendLong((int)rootPlan.Root.ChannelMode);
            AppendLong(interval.GroupId.Value);
            AppendLong(interval.StartTick);
            AppendLong(interval.EndTick);
            AppendInitialState(_resetDefaults);
            foreach (PureMidiTrackPlan trackPlan in rootPlan.Tracks.OrderBy(value => value.TrackOrder))
            {
                foreach (MidiSegment segment in trackPlan.Segments
                    .Where(value => value.ProjectStartTick < interval.EndTick
                        && checked(value.ProjectStartTick + value.LengthTicks) > interval.StartTick)
                    .OrderBy(value => value.ProjectStartTick)
                    .ThenBy(value => value.Id))
                {
                    AppendLong(trackPlan.Track.Id.Value);
                    AppendLong(trackPlan.TrackOrder);
                    AppendLong(segment.Id.Value);
                    AppendLong(segment.ProjectStartTick);
                    AppendLong(segment.LengthTicks);
                    AppendLong(segment.ContentOffsetTick);
                    AppendText(segment.PagedContentFingerprint ?? string.Empty);
                    AppendDirectNoteContent(segment.Notes);
                    AppendDirectChannelEventContent(segment.ChannelEvents);
                    AppendOpaqueMidiContent(segment.OpaqueEvents);
                }
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());

            void AppendDirectNoteContent(DirectMidiNoteCollection notes)
            {
                AppendLong(notes.HasPagedSource ? 1 : 0);
                AppendLong(notes.ClearsPagedSource ? 1 : 0);
                MidoraId[] removed = notes.RemovedSourceIds.Order().ToArray();
                AppendLong(removed.Length);
                foreach (MidoraId id in removed)
                    AppendLong(id.Value);
                DirectMidiNote[] edited = notes.EditedItems
                    .OrderBy(value => value.Id)
                    .ToArray();
                AppendLong(edited.Length);
                foreach (DirectMidiNote value in edited)
                {
                    AppendLong(value.Id.Value);
                    AppendLong(value.StartTick);
                    AppendLong(value.LengthTicks);
                    AppendLong(value.Key);
                    AppendLong(value.NoteOnVelocity);
                    AppendLong(value.NoteOffVelocity);
                    AppendLong(value.NoteOnOrder);
                    AppendLong(value.NoteOffOrder);
                }
            }

            void AppendDirectChannelEventContent(DirectMidiChannelEventCollection events)
            {
                AppendLong(events.HasPagedSource ? 1 : 0);
                AppendLong(events.ClearsPagedSource ? 1 : 0);
                MidoraId[] removed = events.RemovedSourceIds.Order().ToArray();
                AppendLong(removed.Length);
                foreach (MidoraId id in removed)
                    AppendLong(id.Value);
                DirectMidiChannelEvent[] edited = events.EditedItems
                    .OrderBy(value => value.Id)
                    .ToArray();
                AppendLong(edited.Length);
                foreach (DirectMidiChannelEvent value in edited)
                {
                    AppendLong(value.Id.Value);
                    AppendLong(value.Tick);
                    AppendLong((int)value.Kind);
                    AppendLong(value.Data1);
                    AppendLong(value.Data2);
                    AppendLong(value.Order);
                }
            }

            void AppendOpaqueMidiContent(OpaqueMidiEventCollection events)
            {
                AppendLong(events.HasPagedSource ? 1 : 0);
                AppendLong(events.ClearsPagedSource ? 1 : 0);
                MidoraId[] removed = events.RemovedSourceIds.Order().ToArray();
                AppendLong(removed.Length);
                foreach (MidoraId id in removed)
                    AppendLong(id.Value);
                OpaqueMidiEvent[] edited = events.EditedItems
                    .OrderBy(value => value.Id)
                    .ToArray();
                AppendLong(edited.Length);
                foreach (OpaqueMidiEvent value in edited)
                {
                    AppendLong(value.Id.Value);
                    AppendLong(value.Tick);
                    AppendLong((int)value.Kind);
                    AppendLong(value.MetaType);
                    AppendLong(value.Order);
                    AppendLong(value.Payload.Length);
                    hash.AppendData(value.Payload);
                }
            }

            void AppendInitialState(MidiInitialState value)
            {
                AppendLong(value.BankMsb ?? -1);
                AppendLong(value.BankLsb ?? -1);
                AppendLong(value.Program ?? -1);
                AppendLong(value.PitchBend ?? -1);
                AppendLong(value.PitchBendRangeSemitones ?? -1);
                AppendLong(value.PitchBendRangeCents ?? -1);
                foreach ((int key, int item) in value.Controllers.OrderBy(item => item.Key))
                {
                    AppendLong(key);
                    AppendLong(item);
                }
                foreach ((int key, int item) in value.RegisteredParameters.OrderBy(item => item.Key))
                {
                    AppendLong(key);
                    AppendLong(item);
                }
                foreach ((int key, int item) in value.NonRegisteredParameters.OrderBy(item => item.Key))
                {
                    AppendLong(key);
                    AppendLong(item);
                }
            }

            void AppendLong(long value)
            {
                Span<byte> bytes = stackalloc byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
                hash.AppendData(bytes);
            }

            void AppendText(string value)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                AppendLong(bytes.Length);
                hash.AppendData(bytes);
            }
        }

        public IEnumerable<CanonicalMidiEventPage> QueryPages(
            long startTick,
            long endTick,
            bool includeStateAtStart,
            CancellationToken cancellationToken = default)
        {
            if (startTick < _compiledStartTick || endTick > _compiledEndTick || endTick <= startTick)
                throw new ArgumentOutOfRangeException(nameof(startTick));

            List<CanonicalMidiEvent> events = [];
            foreach (PureMidiRootPlan rootPlan in _plan.Roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_unitByRoot.TryGetValue(rootPlan.Root.Id, out int unit)) continue;
                byte port = checked((byte)(unit >> 4));
                byte channel = checked((byte)(unit & 15));
                foreach (PureMidiTrackPlan trackPlan in rootPlan.Tracks)
                {
                    foreach (MidiSegment segment in trackPlan.Segments)
                    {
                        AppendSegmentRange(
                            rootPlan.Root,
                            trackPlan,
                            segment,
                            port,
                            channel,
                            startTick,
                            endTick,
                            includeStateAtStart,
                            events,
                            cancellationToken);
                    }
                }
                AppendRootRangeState(
                    rootPlan,
                    port,
                    channel,
                    startTick,
                    includeStateAtStart,
                    demandedMonitoringSourceIds: null,
                    events,
                    cancellationToken);
                AppendLifecycleRange(
                    rootPlan,
                    port,
                    channel,
                    startTick,
                    endTick,
                    includeStateAtStart,
                    events,
                    cancellationToken);
            }

            events.Sort(CanonicalComparer.Instance);
            List<CanonicalMidiEvent> page = new(CanonicalMidiEventPage.MaximumRecordCount);
            foreach (CanonicalMidiEvent value in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                page.Add(value);
                if (page.Count == CanonicalMidiEventPage.MaximumRecordCount)
                {
                    yield return new(page.ToArray());
                    page.Clear();
                }
            }
            if (page.Count != 0) yield return new(page.ToArray());
        }

        public IEnumerable<CanonicalMidiRenderEventPage> QueryRenderPages(
            long startTick,
            long endTick,
            bool includeStateAtStart,
            CancellationToken cancellationToken = default) =>
            QueryRenderPagesCore(
                startTick,
                endTick,
                includeStateAtStart,
                demandedMonitoringSourceIds: null,
                cancellationToken);

        public IEnumerable<CanonicalMidiRenderEventPage> QueryRenderPages(
            long startTick,
            long endTick,
            bool includeStateAtStart,
            IReadOnlySet<MidoraId> demandedMonitoringSourceIds,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(demandedMonitoringSourceIds);
            return QueryRenderPagesCore(
                startTick,
                endTick,
                includeStateAtStart,
                demandedMonitoringSourceIds,
                cancellationToken);
        }

        private IEnumerable<CanonicalMidiRenderEventPage> QueryRenderPagesCore(
            long startTick,
            long endTick,
            bool includeStateAtStart,
            IReadOnlySet<MidoraId>? demandedMonitoringSourceIds,
            CancellationToken cancellationToken)
        {
            if (startTick < _compiledStartTick || endTick > _compiledEndTick || endTick <= startTick)
                throw new ArgumentOutOfRangeException(nameof(startTick));

            using BoundedCanonicalMidiRenderEventSorter sorter = new();
            CanonicalMidiRenderSortSink sink = new(sorter);
            foreach (PureMidiRootPlan rootPlan in _plan.Roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_unitByRoot.TryGetValue(rootPlan.Root.Id, out int unit)) continue;
                bool lifecycleDemanded = demandedMonitoringSourceIds is null
                    || demandedMonitoringSourceIds.Contains(rootPlan.Root.Id);
                if (!lifecycleDemanded
                    && demandedMonitoringSourceIds is not null
                    && !rootPlan.Tracks.Any(value =>
                        demandedMonitoringSourceIds.Contains(value.Track.Id)))
                {
                    continue;
                }
                byte port = checked((byte)(unit >> 4));
                byte channel = checked((byte)(unit & 15));
                foreach (PureMidiTrackPlan trackPlan in rootPlan.Tracks)
                {
                    if (demandedMonitoringSourceIds is not null
                        && !demandedMonitoringSourceIds.Contains(trackPlan.Track.Id))
                    {
                        continue;
                    }
                    foreach (MidiSegment segment in trackPlan.Segments)
                    {
                        AppendSegmentRange(
                            rootPlan.Root,
                            trackPlan,
                            segment,
                            port,
                            channel,
                            startTick,
                            endTick,
                            includeStateAtStart,
                            sink,
                            cancellationToken);
                    }
                }
                AppendRootRangeState(
                    rootPlan,
                    port,
                    channel,
                    startTick,
                    includeStateAtStart,
                    demandedMonitoringSourceIds,
                    sink,
                    cancellationToken);
                if (lifecycleDemanded)
                {
                    List<CanonicalMidiEvent> lifecycle = [];
                    AppendLifecycleRange(
                        rootPlan,
                        port,
                        channel,
                        startTick,
                        endTick,
                        includeStateAtStart,
                        lifecycle,
                        cancellationToken);
                    foreach (CanonicalMidiEvent value in lifecycle) sink.Add(value);
                }
            }
            foreach (CanonicalMidiRenderEventPage page in sorter.ReadPages(cancellationToken))
                yield return page;
        }

        public IEnumerable<CanonicalSmfTrackChannelEventPage> QueryTrackChannelEventPages(
            MidoraId exportTrackId,
            long startTick,
            long endTick,
            CancellationToken cancellationToken = default)
        {
            if (exportTrackId == default
                || startTick < _compiledStartTick
                || endTick > _compiledEndTick
                || endTick <= startTick)
            {
                throw new ArgumentOutOfRangeException(nameof(exportTrackId));
            }

            using BoundedCanonicalSmfEventSorter sorter = new();
            CanonicalSmfSortSink sink = new(sorter);
            foreach (PureMidiRootPlan rootPlan in _plan.Roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PureMidiTrackPlan? trackPlan = rootPlan.Tracks
                    .FirstOrDefault(value => value.Track.Id == exportTrackId);
                if (trackPlan is null || !_unitByRoot.TryGetValue(rootPlan.Root.Id, out int unit))
                    continue;
                byte port = checked((byte)(unit >> 4));
                byte channel = checked((byte)(unit & 15));
                foreach (MidiSegment segment in trackPlan.Segments)
                {
                    AppendSegmentRange(
                        rootPlan.Root,
                        trackPlan,
                        segment,
                        port,
                        channel,
                        startTick,
                        endTick,
                        includeStateAtStart: false,
                        sink,
                        cancellationToken);
                }
                List<CanonicalMidiEvent> lifecycle = [];
                AppendLifecycleRange(
                    rootPlan,
                    port,
                    channel,
                    startTick,
                    endTick,
                    includeStateAtStart: false,
                    lifecycle,
                    cancellationToken);
                foreach (CanonicalMidiEvent value in lifecycle)
                    if (value.ExportTrackId == exportTrackId) sink.Add(value);
                break;
            }
            foreach (CanonicalSmfTrackChannelEventPage page in sorter.ReadPages(cancellationToken))
                yield return page;
        }

        public IEnumerable<CanonicalOpaqueMidiEventPage> QueryTrackOpaqueEventPages(
            MidoraId exportTrackId,
            long startTick,
            long endTick,
            CancellationToken cancellationToken = default)
        {
            if (exportTrackId == default
                || startTick < _compiledStartTick
                || endTick > _compiledEndTick
                || endTick <= startTick)
            {
                throw new ArgumentOutOfRangeException(nameof(exportTrackId));
            }

            using BoundedCanonicalOpaqueEventSorter sorter = new();
            foreach (PureMidiRootPlan rootPlan in _plan.Roots)
            {
                PureMidiTrackPlan? trackPlan = rootPlan.Tracks
                    .FirstOrDefault(value => value.Track.Id == exportTrackId);
                if (trackPlan is null) continue;
                foreach (MidiSegment segment in trackPlan.Segments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long contentStart = segment.ContentOffsetTick;
                    long contentEnd = checked(contentStart + segment.LengthTicks);
                    foreach (OpaqueMidiEventValue value in segment.OpaqueEvents.QueryValues(
                        contentStart,
                        contentEnd))
                    {
                        long absoluteTick = checked(
                            segment.ProjectStartTick + value.Tick - contentStart);
                        if (absoluteTick < startTick || absoluteTick >= endTick) continue;
                        SourceReference source = DirectSource(
                            rootPlan.Root.Id,
                            trackPlan.Track.Id,
                            segment.Id,
                            value.Id,
                            absoluteTick,
                            SourceOrigin.OpaqueMidiEvent);
                        sorter.Add(new(
                            trackPlan.Track.Id,
                            absoluteTick,
                            value.Kind,
                            value.MetaType,
                            value.Payload,
                            value.Order,
                            source,
                            trackPlan.TrackOrder));
                    }
                }
                break;
            }
            foreach (CanonicalOpaqueMidiEventPage page in sorter.ReadPages(cancellationToken))
                yield return page;
        }

        private void AppendSegmentRange(
            MidiChannelRoot root,
            PureMidiTrackPlan trackPlan,
            MidiSegment segment,
            byte port,
            byte channel,
            long startTick,
            long endTick,
            bool includeStateAtStart,
            ICollection<CanonicalMidiEvent> output,
            CancellationToken cancellationToken)
        {
            long segmentEnd = checked(segment.ProjectStartTick + segment.LengthTicks);
            if (segment.ProjectStartTick >= endTick || segmentEnd < startTick) return;
            long contentStart = segment.ContentOffsetTick;
            long contentEnd = checked(contentStart + segment.LengthTicks);
            long queryContentStart = Math.Max(
                contentStart,
                checked(startTick - segment.ProjectStartTick + contentStart));
            long queryContentEnd = Math.Min(
                contentEnd,
                checked(endTick - segment.ProjectStartTick + contentStart));

            long endpointQueryEnd = Math.Max(queryContentEnd, queryContentStart + 1);
            foreach (DirectMidiNoteValue note in segment.Notes.QueryStartValues(
                queryContentStart,
                endpointQueryEnd))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (note.StartTick < contentStart || note.StartTick >= contentEnd) continue;
                long absoluteStart = checked(
                    segment.ProjectStartTick + note.StartTick - contentStart);
                SourceReference source = DirectSource(
                    root.Id,
                    trackPlan.Track.Id,
                    segment.Id,
                    note.Id,
                    absoluteStart,
                    SourceOrigin.DirectMidiNote);
                AddDirect(
                    absoluteStart,
                    note.NoteOnOrder,
                    endpointOrder: 1,
                    note.Id,
                    MidiMessage.NoteOn(
                        channel,
                        checked((byte)note.Key),
                        checked((byte)note.NoteOnVelocity)),
                    source,
                    trackPlan,
                    output,
                    port,
                    channel);
            }

            if (includeStateAtStart && queryContentStart > contentStart)
            {
                foreach (DirectMidiNoteValue note in segment.Notes.QueryActiveValues(
                    queryContentStart))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (note.StartTick < contentStart || note.StartTick >= contentEnd) continue;
                    long absoluteStart = checked(
                        segment.ProjectStartTick + note.StartTick - contentStart);
                    long absoluteEnd = Math.Min(
                        segmentEnd,
                        SaturatingAdd(absoluteStart, note.LengthTicks));
                    if (absoluteStart >= startTick || absoluteEnd <= startTick) continue;
                    SourceReference source = DirectSource(
                        root.Id,
                        trackPlan.Track.Id,
                        segment.Id,
                        note.Id,
                        startTick,
                        SourceOrigin.RangeRestore);
                    AddDirect(
                        startTick,
                        note.NoteOnOrder,
                        endpointOrder: 1,
                        note.Id,
                        MidiMessage.NoteOn(
                            channel,
                            checked((byte)note.Key),
                            checked((byte)note.NoteOnVelocity)),
                        source,
                        trackPlan,
                        output,
                        port,
                        channel,
                        CanonicalEventRole.RangeRestore);
                }
            }

            long noteEndQueryEnd = endpointQueryEnd;
            if (endTick == _compiledEndTick && noteEndQueryEnd < long.MaxValue)
            {
                noteEndQueryEnd++;
            }
            foreach (DirectMidiNoteValue note in segment.Notes.QueryEndValues(
                queryContentStart,
                noteEndQueryEnd))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (note.StartTick < contentStart || note.StartTick >= contentEnd) continue;
                long absoluteStart = checked(
                    segment.ProjectStartTick + note.StartTick - contentStart);
                long absoluteEnd = Math.Min(segmentEnd, SaturatingAdd(absoluteStart, note.LengthTicks));
                if (absoluteEnd < startTick
                    || absoluteEnd > endTick
                    || absoluteEnd == endTick && endTick != _compiledEndTick)
                {
                    continue;
                }
                SourceReference source = DirectSource(
                    root.Id,
                    trackPlan.Track.Id,
                    segment.Id,
                    note.Id,
                    absoluteEnd,
                    SourceOrigin.DirectMidiNote);
                AddDirect(
                    absoluteEnd,
                    note.NoteOffOrder,
                    endpointOrder: 0,
                    note.Id,
                    MidiMessage.NoteOff(
                        channel,
                        checked((byte)note.Key),
                        checked((byte)note.NoteOffVelocity)),
                    source,
                    trackPlan,
                    output,
                    port,
                    channel);
            }

            bool includeSegmentEnd = segmentEnd >= startTick
                && (segmentEnd < endTick
                    || endTick == _compiledEndTick && segmentEnd == endTick);
            if (includeSegmentEnd)
            {
                foreach (DirectMidiNoteValue note in segment.Notes.QueryActiveValues(contentEnd))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (note.StartTick < contentStart || note.StartTick >= contentEnd) continue;
                    SourceReference source = DirectSource(
                        root.Id,
                        trackPlan.Track.Id,
                        segment.Id,
                        note.Id,
                        segmentEnd,
                        SourceOrigin.CompilerBoundaryCleanup);
                    AddDirect(
                        segmentEnd,
                        note.NoteOffOrder,
                        endpointOrder: 0,
                        note.Id,
                        MidiMessage.NoteOff(
                            channel,
                            checked((byte)note.Key),
                            checked((byte)note.NoteOffVelocity)),
                        source,
                        trackPlan,
                        output,
                        port,
                        channel,
                        CanonicalEventRole.DirectMidi);
                }
            }

            foreach (DirectMidiChannelEventValue value in segment.ChannelEvents.QueryOrderedValues(
                queryContentStart,
                Math.Max(queryContentEnd, queryContentStart + 1)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (value.Tick < contentStart || value.Tick >= contentEnd) continue;
                long absoluteTick = checked(segment.ProjectStartTick + value.Tick - contentStart);
                MidiMessage message = ToMidiMessage(value, channel);
                long target = SemanticTargetForMessage(message);
                if (absoluteTick < startTick) continue;
                if (absoluteTick >= endTick) continue;
                SourceReference source = DirectSource(
                    root.Id,
                    trackPlan.Track.Id,
                    segment.Id,
                    value.Id,
                    absoluteTick,
                    SourceOrigin.DirectMidiChannelEvent);
                AddDirect(
                    absoluteTick,
                    value.Order,
                    endpointOrder: 1,
                    value.Id,
                    message,
                    source,
                    trackPlan,
                    output,
                    port,
                    channel,
                    CanonicalEventRole.DirectMidi,
                    target);
            }
            if ((segmentEnd >= startTick
                    && (segmentEnd < endTick
                        || endTick == _compiledEndTick && segmentEnd == endTick))
                && FindAnalysis(root.Id, segment.Id) is RootIntervalAnalysis analysis
                && analysis.RawBoundaryNotes.TryGetValue(segment.Id, out byte[]? rawKeys))
            {
                long order = long.MaxValue / 8;
                foreach (byte key in rawKeys)
                {
                    SourceReference source = DirectSource(
                        root.Id,
                        trackPlan.Track.Id,
                        segment.Id,
                        segment.Id,
                        segmentEnd,
                        SourceOrigin.CompilerBoundaryCleanup);
                    AddDirect(
                        segmentEnd,
                        order++,
                        endpointOrder: 0,
                        segment.Id,
                        MidiMessage.NoteOff(channel, key, 0),
                        source,
                        trackPlan,
                        output,
                        port,
                        channel);
                }
            }
        }

        private void AppendLifecycleRange(
            PureMidiRootPlan rootPlan,
            byte port,
            byte channel,
            long startTick,
            long endTick,
            bool includeStateAtStart,
            ICollection<CanonicalMidiEvent> output,
            CancellationToken cancellationToken)
        {
            foreach (PureMidiRootInterval interval in rootPlan.Intervals)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RootIntervalAnalysis analysis = _analysis[(rootPlan.Root.Id, interval.GroupId)];
                long lifecycleStart = interval.StartTick;
                bool restoredStart = includeStateAtStart
                    && interval.StartTick < startTick
                    && interval.EndTick > startTick;
                if (restoredStart) lifecycleStart = startTick;
                if (lifecycleStart >= startTick && lifecycleStart < endTick)
                {
                    SourceReference source = RootLifecycleSource(
                        rootPlan.Root.Id,
                        interval.StartOwnerTrackId,
                        interval.StartOwnerSegmentId,
                        lifecycleStart,
                        interval.StartOwnerTrackId);
                    int trackOrder = restoredStart
                        ? int.MinValue
                        : rootPlan.Tracks
                            .Single(value => value.Track.Id == interval.StartOwnerTrackId)
                            .TrackOrder;
                    long order = long.MinValue / 4 + lifecycleStart;
                    output.Add(new(
                        lifecycleStart,
                        port,
                        channel,
                        MidiMessage.ControlChange(channel, 121, 0),
                        restoredStart ? CanonicalEventRole.RangeRestore : CanonicalEventRole.Reset,
                        order++,
                        ControlTargetKey(121),
                        order,
                        source with
                        {
                            Origin = restoredStart
                                ? SourceOrigin.RangeRestore
                                : source.Origin
                        },
                        interval.StartOwnerTrackId,
                        trackOrder,
                        long.MinValue));
                    AppendPureRootDefaults(
                        (List<CanonicalMidiEvent>)output,
                        lifecycleStart,
                        port,
                        channel,
                        analysis.UsedTargets,
                        _resetDefaults,
                        source,
                        interval.StartOwnerTrackId,
                        trackOrder,
                        ref order,
                        restoredStart ? CanonicalEventRole.RangeRestore : CanonicalEventRole.Reset);
                }

                bool includeEnd = interval.EndTick >= startTick
                    && (interval.EndTick < endTick
                        || endTick == _compiledEndTick && interval.EndTick == endTick);
                if (!includeEnd) continue;
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
                output.Add(new(
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
                    (List<CanonicalMidiEvent>)output,
                    interval.EndTick,
                    port,
                    channel,
                    analysis.UsedTargets,
                    _resetDefaults,
                    endSource,
                    interval.EndOwnerTrackId,
                    endTrackOrder,
                    ref endOrder,
                    CanonicalEventRole.RootBoundaryCleanup);
            }
        }

        private void AppendRootRangeState(
            PureMidiRootPlan rootPlan,
            byte port,
            byte channel,
            long startTick,
            bool includeStateAtStart,
            IReadOnlySet<MidoraId>? demandedMonitoringSourceIds,
            ICollection<CanonicalMidiEvent> output,
            CancellationToken cancellationToken)
        {
            if (!includeStateAtStart || startTick <= 0)
            {
                return;
            }

            PureMidiRootInterval? activeInterval = rootPlan.Intervals
                .SingleOrDefault(value => value.StartTick < startTick && value.EndTick > startTick);
            if (activeInterval is null)
            {
                return;
            }

            Dictionary<long, RootStateCandidate> state = [];
            foreach (PureMidiTrackPlan trackPlan in rootPlan.Tracks)
            {
                if (demandedMonitoringSourceIds is not null
                    && !demandedMonitoringSourceIds.Contains(trackPlan.Track.Id))
                {
                    continue;
                }

                foreach (MidiSegment segment in trackPlan.Track.Segments
                    .Where(IsRepresentableMidiSegment)
                    .Where(value => value.ProjectStartTick < startTick
                        && checked(value.ProjectStartTick + value.LengthTicks) > activeInterval.StartTick)
                    .OrderBy(value => value.ProjectStartTick)
                    .ThenBy(value => value.Id))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!_channelAnalysis.TryGetValue(
                            segment.Id,
                            out SegmentChannelAnalysis? segmentAnalysis))
                    {
                        continue;
                    }

                    long contentStart = segment.ContentOffsetTick;
                    long contentEnd = checked(contentStart + segment.LengthTicks);
                    long absoluteStateEnd = Math.Min(
                        startTick,
                        checked(segment.ProjectStartTick + segment.LengthTicks));
                    long stateContentEnd = checked(
                        contentStart + absoluteStateEnd - segment.ProjectStartTick);
                    foreach ((long target, DirectMidiChannelEventValue value) in segmentAnalysis
                        .GetStateAt(segment.ChannelEvents, Math.Min(contentEnd, stateContentEnd)))
                    {
                        long absoluteTick = checked(
                            segment.ProjectStartTick + value.Tick - contentStart);
                        if (absoluteTick < activeInterval.StartTick || absoluteTick >= startTick)
                        {
                            continue;
                        }

                        RootStateCandidate candidate = new(
                            trackPlan,
                            segment,
                            value,
                            absoluteTick);
                        if (!state.TryGetValue(target, out RootStateCandidate current)
                            || CompareRootStateCandidate(candidate, current) > 0)
                        {
                            state[target] = candidate;
                        }
                    }
                }
            }

            long restoreOrder = long.MinValue / 8;
            foreach ((long target, RootStateCandidate candidate) in state
                .OrderBy(value => RangeRestoreCategory(value.Value.Value))
                .ThenBy(value => value.Key))
            {
                DirectMidiChannelEventValue value = candidate.Value;
                MidiMessage message = ToMidiMessage(value, channel);
                SourceReference source = DirectSource(
                    rootPlan.Root.Id,
                    candidate.TrackPlan.Track.Id,
                    candidate.Segment.Id,
                    value.Id,
                    startTick,
                    SourceOrigin.RangeRestore);
                long order = restoreOrder++;
                output.Add(new(
                    startTick,
                    port,
                    channel,
                    message,
                    CanonicalEventRole.RangeRestore,
                    order,
                    target,
                    order,
                    source,
                    candidate.TrackPlan.Track.Id,
                    int.MinValue + 1,
                    order));
            }
        }

        private static int CompareRootStateCandidate(
            RootStateCandidate left,
            RootStateCandidate right)
        {
            int value = left.AbsoluteTick.CompareTo(right.AbsoluteTick);
            if (value != 0) return value;
            value = left.TrackPlan.TrackOrder.CompareTo(right.TrackPlan.TrackOrder);
            if (value != 0) return value;
            value = left.Value.Order.CompareTo(right.Value.Order);
            return value != 0 ? value : left.Value.Id.CompareTo(right.Value.Id);
        }

        private static int RangeRestoreCategory(DirectMidiChannelEventValue value) =>
            value.Kind switch
            {
                DirectMidiChannelEventKind.ControlChange when value.Data1 == 0 => 0,
                DirectMidiChannelEventKind.ControlChange when value.Data1 == 32 => 1,
                DirectMidiChannelEventKind.ProgramChange => 2,
                DirectMidiChannelEventKind.ControlChange => 3,
                DirectMidiChannelEventKind.PolyphonicKeyPressure => 4,
                DirectMidiChannelEventKind.ChannelPressure => 5,
                DirectMidiChannelEventKind.PitchBend => 6,
                _ => 7
            };

        private void BuildAnalysis(CancellationToken cancellationToken)
        {
            foreach (PureMidiRootPlan rootPlan in _plan.Roots)
            {
                foreach ((PureMidiTrackPlan _, MidiSegment segment) in
                    EnumerateAnalysisSegments(rootPlan))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _channelAnalysis.TryAdd(
                        segment.Id,
                        BuildSegmentChannelAnalysis(segment, cancellationToken));
                }
            }

            foreach (PureMidiRootPlan rootPlan in _plan.Roots)
            {
                foreach (PureMidiRootInterval interval in rootPlan.Intervals)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    HashSet<long> targets = [];
                    Dictionary<MidoraId, byte[]> rawBoundaryNotes = [];
                    foreach ((PureMidiTrackPlan _, MidiSegment segment) in
                        EnumerateAnalysisSegments(rootPlan))
                    {
                        long segmentEnd = checked(segment.ProjectStartTick + segment.LengthTicks);
                        if (segment.ProjectStartTick >= interval.EndTick
                            || segmentEnd <= interval.StartTick)
                        {
                            continue;
                        }
                        SegmentChannelAnalysis segmentAnalysis = _channelAnalysis[segment.Id];
                        targets.UnionWith(segmentAnalysis.UsedTargets);
                        byte[] activeKeys = segmentAnalysis.RawBoundaryNoteKeys;
                        if (activeKeys.Length != 0) rawBoundaryNotes[segment.Id] = activeKeys;
                    }
                    _analysis.Add(
                        (rootPlan.Root.Id, interval.GroupId),
                        new(targets.Order().ToArray(), rawBoundaryNotes));
                }
            }
        }

        private IEnumerable<(PureMidiTrackPlan TrackPlan, MidiSegment Segment)>
            EnumerateAnalysisSegments(PureMidiRootPlan rootPlan)
        {
            foreach (PureMidiTrackPlan trackPlan in rootPlan.Tracks)
            {
                foreach (MidiSegment segment in trackPlan.Track.Segments
                    .Where(IsRepresentableMidiSegment)
                    .Where(value => value.ProjectStartTick < _compiledEndTick)
                    .OrderBy(value => value.ProjectStartTick)
                    .ThenBy(value => value.Id))
                {
                    long segmentEnd = checked(segment.ProjectStartTick + segment.LengthTicks);
                    if (rootPlan.Intervals.Any(interval =>
                        segment.ProjectStartTick < Math.Min(interval.EndTick, _compiledEndTick)
                        && segmentEnd > interval.StartTick))
                    {
                        yield return (trackPlan, segment);
                    }
                }
            }
        }

        private static SegmentChannelAnalysis BuildSegmentChannelAnalysis(
            MidiSegment segment,
            CancellationToken cancellationToken)
        {
            const int CheckpointInterval = 16_384;
            long contentStart = segment.ContentOffsetTick;
            long contentEnd = checked(contentStart + segment.LengthTicks);
            HashSet<long> targets = [];
            HashSet<byte> banks = [0];
            HashSet<byte> programs = [0];
            int[] rawActiveNoteCounts = new int[128];
            Dictionary<long, DirectMidiChannelEventValue> state = [];
            List<ChannelStateCheckpoint> checkpoints = [];
            long rawNoteOnCount = 0;
            int eventCount = 0;
            foreach (DirectMidiChannelEventValue value in segment.ChannelEvents.QueryOrderedValues(
                contentStart,
                contentEnd))
            {
                cancellationToken.ThrowIfCancellationRequested();
                MidiMessage message = ToMidiMessage(value, channel: 0);
                long target = SemanticTargetForMessage(message);
                if (target != long.MinValue)
                {
                    targets.Add(target);
                    state[target] = value;
                }
                if (value.Kind == DirectMidiChannelEventKind.ControlChange && value.Data1 == 0)
                {
                    banks.Add(checked((byte)value.Data2));
                }
                else if (value.Kind == DirectMidiChannelEventKind.ProgramChange)
                {
                    programs.Add(checked((byte)value.Data1));
                }
                if (message.MessageType == MidiMessageType.NoteOn && message.Byte2 != 0)
                {
                    rawActiveNoteCounts[message.Byte1]++;
                    rawNoteOnCount++;
                }
                else if (message.MessageType == MidiMessageType.NoteOff
                    || message.MessageType == MidiMessageType.NoteOn && message.Byte2 == 0)
                {
                    if (rawActiveNoteCounts[message.Byte1] != 0)
                    {
                        rawActiveNoteCounts[message.Byte1]--;
                    }
                }
                eventCount++;
                if (eventCount % CheckpointInterval == 0 && state.Count != 0)
                {
                    checkpoints.Add(new(
                        value.Tick,
                        state.ToDictionary(entry => entry.Key, entry => entry.Value)));
                }
            }
            byte[] activeKeys = Enumerable.Range(0, rawActiveNoteCounts.Length)
                .SelectMany(key => Enumerable.Repeat((byte)key, rawActiveNoteCounts[key]))
                .ToArray();
            return new(
                contentStart,
                targets.Order().ToArray(),
                activeKeys,
                rawNoteOnCount,
                banks.Order().ToArray(),
                programs.Order().ToArray(),
                checkpoints.ToArray());
        }

        private (long Events, long NoteOns) CountEvents(CancellationToken cancellationToken)
        {
            long eventCount = 0;
            long noteOns = 0;
            foreach (PureMidiRootPlan rootPlan in _plan.Roots)
            {
                if (!_unitByRoot.ContainsKey(rootPlan.Root.Id)) continue;
                foreach (PureMidiTrackPlan trackPlan in rootPlan.Tracks)
                {
                    foreach (MidiSegment segment in trackPlan.Segments)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        eventCount = checked(eventCount + (long)segment.Notes.Count * 2L);
                        noteOns = checked(noteOns + segment.Notes.Count);
                        eventCount = checked(eventCount + segment.ChannelEvents.Count);
                        noteOns = checked(
                            noteOns + _channelAnalysis[segment.Id].RawNoteOnCount);
                    }
                }
                foreach (PureMidiRootInterval interval in rootPlan.Intervals)
                {
                    RootIntervalAnalysis analysis = _analysis[(rootPlan.Root.Id, interval.GroupId)];
                    long defaults = CountDefaultEvents(analysis.UsedTargets);
                    eventCount = checked(eventCount + 2 + defaults * 2);
                    eventCount = checked(eventCount
                        + analysis.RawBoundaryNotes.Values.Sum(value => (long)value.Length));
                }
            }
            return (eventCount, noteOns);
        }

        private long CountDefaultEvents(IEnumerable<long> targets)
        {
            List<CanonicalMidiEvent> events = [];
            long order = 0;
            AppendPureRootDefaults(
                events,
                0,
                0,
                0,
                targets,
                _resetDefaults,
                default,
                default,
                0,
                ref order);
            return events.Count;
        }

        private string CreateContentFingerprint(CancellationToken cancellationToken)
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendLong(_compiledStartTick);
            AppendLong(_compiledEndTick);
            foreach (PureMidiRootPlan rootPlan in _plan.Roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppendLong(rootPlan.Root.Id.Value);
                AppendLong(_unitByRoot.GetValueOrDefault(rootPlan.Root.Id, -1));
                foreach (PureMidiTrackPlan trackPlan in rootPlan.Tracks)
                {
                    AppendLong(trackPlan.Track.Id.Value);
                    AppendLong(trackPlan.TrackOrder);
                    foreach (MidiSegment segment in trackPlan.Segments)
                    {
                        AppendLong(segment.Id.Value);
                        AppendLong(segment.ProjectStartTick);
                        AppendLong(segment.LengthTicks);
                        AppendLong(segment.ContentOffsetTick);
                        AppendText(segment.PagedContentFingerprint ?? string.Empty);
                        AppendLong(segment.Notes.ClearsPagedSource ? 1 : 0);
                        foreach (MidoraId id in segment.Notes.RemovedSourceIds.Order())
                            AppendLong(id.Value);
                        AppendLong(segment.ChannelEvents.ClearsPagedSource ? 1 : 0);
                        foreach (MidoraId id in segment.ChannelEvents.RemovedSourceIds.Order())
                            AppendLong(id.Value);
                        AppendLong(segment.OpaqueEvents.ClearsPagedSource ? 1 : 0);
                        foreach (MidoraId id in segment.OpaqueEvents.RemovedSourceIds.Order())
                            AppendLong(id.Value);
                        foreach (DirectMidiNote value in segment.Notes.EditedItems.OrderBy(value => value.Id))
                        {
                            AppendLong(value.Id.Value);
                            AppendLong(value.StartTick);
                            AppendLong(value.LengthTicks);
                            AppendLong(value.Key);
                            AppendLong(value.NoteOnVelocity);
                            AppendLong(value.NoteOffVelocity);
                            AppendLong(value.NoteOnOrder);
                            AppendLong(value.NoteOffOrder);
                        }
                        foreach (DirectMidiChannelEvent value in segment.ChannelEvents.EditedItems.OrderBy(value => value.Id))
                        {
                            AppendLong(value.Id.Value);
                            AppendLong(value.Tick);
                            AppendLong((int)value.Kind);
                            AppendLong(value.Data1);
                            AppendLong(value.Data2);
                            AppendLong(value.Order);
                        }
                        foreach (OpaqueMidiEvent value in segment.OpaqueEvents.EditedItems.OrderBy(value => value.Id))
                        {
                            AppendLong(value.Id.Value);
                            AppendLong(value.Tick);
                            AppendLong((int)value.Kind);
                            AppendLong(value.MetaType);
                            AppendLong(value.Order);
                            AppendLong(value.Payload.Length);
                            hash.AppendData(value.Payload);
                        }
                    }
                }
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());

            void AppendLong(long value)
            {
                Span<byte> bytes = stackalloc byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
                hash.AppendData(bytes);
            }

            void AppendText(string value)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                AppendLong(bytes.Length);
                hash.AppendData(bytes);
            }
        }

        private RootIntervalAnalysis? FindAnalysis(MidoraId rootId, MidoraId segmentId) =>
            _analysis
                .Where(value => value.Key.RootId == rootId
                    && value.Value.RawBoundaryNotes.ContainsKey(segmentId))
                .Select(value => value.Value)
                .FirstOrDefault();

        private static void AddDirect(
            long tick,
            long explicitOrder,
            int endpointOrder,
            MidoraId stableId,
            MidiMessage message,
            SourceReference source,
            PureMidiTrackPlan trackPlan,
            ICollection<CanonicalMidiEvent> output,
            byte port,
            byte channel,
            CanonicalEventRole role = CanonicalEventRole.DirectMidi,
            long semanticTarget = long.MinValue)
        {
            long stableOrder = checked((explicitOrder << 1) + endpointOrder);
            output.Add(new(
                tick,
                port,
                channel,
                message,
                role,
                stableOrder,
                semanticTarget,
                stableOrder,
                source,
                trackPlan.Track.Id,
                trackPlan.TrackOrder,
                explicitOrder));
        }

        private static long SaturatingAdd(long left, long right) =>
            right <= 0 || left > long.MaxValue - right ? long.MaxValue : left + right;

        private sealed record RootIntervalAnalysis(
            long[] UsedTargets,
            IReadOnlyDictionary<MidoraId, byte[]> RawBoundaryNotes);

        private readonly record struct RootStateCandidate(
            PureMidiTrackPlan TrackPlan,
            MidiSegment Segment,
            DirectMidiChannelEventValue Value,
            long AbsoluteTick);

        private sealed record SegmentChannelAnalysis(
            long ContentStartTick,
            long[] UsedTargets,
            byte[] RawBoundaryNoteKeys,
            long RawNoteOnCount,
            byte[] ReferencedBanks,
            byte[] ReferencedPrograms,
            ChannelStateCheckpoint[] StateCheckpoints)
        {
            public Dictionary<long, DirectMidiChannelEventValue> GetStateAt(
                DirectMidiChannelEventCollection events,
                long tick)
            {
                Dictionary<long, DirectMidiChannelEventValue> result = [];
                long scanStart = ContentStartTick;
                int low = 0;
                int high = StateCheckpoints.Length;
                while (low < high)
                {
                    int middle = low + ((high - low) >> 1);
                    if (StateCheckpoints[middle].Tick < tick) low = middle + 1;
                    else high = middle;
                }
                int checkpointIndex = low - 1;
                if (checkpointIndex >= 0)
                {
                    ChannelStateCheckpoint checkpoint = StateCheckpoints[checkpointIndex];
                    foreach ((long target, DirectMidiChannelEventValue value) in checkpoint.State)
                    {
                        result.Add(target, value);
                    }
                    // Re-reading the checkpoint tick is intentional. A checkpoint
                    // may split one same-tick suffix; deterministic assignment
                    // makes replay idempotent and completes that suffix.
                    scanStart = checkpoint.Tick;
                }
                foreach (DirectMidiChannelEventValue value in events.QueryOrderedValues(
                    scanStart,
                    tick))
                {
                    MidiMessage message = ToMidiMessage(value, channel: 0);
                    long target = SemanticTargetForMessage(message);
                    if (target != long.MinValue)
                    {
                        result[target] = value;
                    }
                }
                return result;
            }
        }

        private sealed record ChannelStateCheckpoint(
            long Tick,
            IReadOnlyDictionary<long, DirectMidiChannelEventValue> State);

        private sealed class CanonicalSmfSortSink(BoundedCanonicalSmfEventSorter sorter) :
            ICollection<CanonicalMidiEvent>
        {
            public int Count => throw new NotSupportedException();
            public bool IsReadOnly => false;

            public void Add(CanonicalMidiEvent item) => sorter.Add(new(
                item.ExportTrackId,
                item.Tick,
                item.ZeroBasedPort,
                item.ZeroBasedChannel,
                item.Message,
                item.Role,
                item.SmfEventOrder,
                item.Source.DirectMidiObjectId));

            public void Clear() => throw new NotSupportedException();
            public bool Contains(CanonicalMidiEvent item) => throw new NotSupportedException();
            public void CopyTo(CanonicalMidiEvent[] array, int arrayIndex) =>
                throw new NotSupportedException();
            public bool Remove(CanonicalMidiEvent item) => throw new NotSupportedException();
            public IEnumerator<CanonicalMidiEvent> GetEnumerator() =>
                throw new NotSupportedException();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
                GetEnumerator();
        }

        private sealed class CanonicalMidiRenderSortSink(
            BoundedCanonicalMidiRenderEventSorter sorter) : ICollection<CanonicalMidiEvent>
        {
            public int Count => throw new NotSupportedException();
            public bool IsReadOnly => false;
            public void Add(CanonicalMidiEvent item) => sorter.Add(item);
            public void Clear() => throw new NotSupportedException();
            public bool Contains(CanonicalMidiEvent item) => throw new NotSupportedException();
            public void CopyTo(CanonicalMidiEvent[] array, int arrayIndex) =>
                throw new NotSupportedException();
            public bool Remove(CanonicalMidiEvent item) => throw new NotSupportedException();
            public IEnumerator<CanonicalMidiEvent> GetEnumerator() =>
                throw new NotSupportedException();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
                GetEnumerator();
        }
    }
}
