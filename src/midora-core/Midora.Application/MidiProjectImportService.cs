using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;

namespace Midora.Application;

public sealed record MidiProjectImportDiagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    int? SourceTrackIndex = null,
    int? SourceByteOffset = null,
    long? Tick = null,
    byte? ZeroBasedPort = null,
    byte? ZeroBasedChannel = null);

public sealed class MidiProjectImportResult
{
    internal MidiProjectImportResult(
        MidoraProject project,
        MidiProjectImportDiagnostic[] diagnostics)
    {
        Project = project;
        Diagnostics = diagnostics;
    }

    public MidoraProject Project { get; }
    public IReadOnlyList<MidiProjectImportDiagnostic> Diagnostics { get; }
}

public sealed class MidiImportPortMappingRequiredException : IOException
{
    public MidiImportPortMappingRequiredException(IEnumerable<byte> sourcePorts)
        : base("The MIDI file uses Port values outside Midora's 1..16 Port range and requires an explicit one-to-one Port mapping review.")
    {
        SourcePorts = sourcePorts.Distinct().Order().ToArray();
    }

    public IReadOnlyList<byte> SourcePorts { get; }
}

public static class MidiProjectImportService
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static ReadOnlySpan<byte> RolandGsChannel10NormalPart =>
        [0x41, 0x10, 0x42, 0x12, 0x40, 0x10, 0x15, 0x00, 0x1b, 0xf7];
    private static ReadOnlySpan<byte> YamahaXgChannel10NormalPart =>
        [0x43, 0x10, 0x4c, 0x08, 0x09, 0x07, 0x00, 0xf7];

    public static MidiProjectImportResult Import(
        ReadOnlySpan<byte> file,
        string projectName,
        IReadOnlyDictionary<byte, byte>? zeroBasedPortMapping = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectName);
        ParsedStandardMidiFile parsed = StandardMidiFile.ParseType0Or1(file);
        cancellationToken.ThrowIfCancellationRequested();

        List<MidiProjectImportDiagnostic> diagnostics = [];
        TrackScan[] scans = NormalizeMidoraMetadata(
            parsed.Tracks.Select(ScanTrack).ToArray());
        AddInvalidTrackNameDiagnostic(scans, diagnostics);
        byte[] sourcePorts = scans
            .SelectMany(value => value.ChannelEvents.Select(item => item.SourcePort)
                .Concat(value.OpaqueEvents.Select(item => item.SourcePort))
                .Concat(value.MidoraMetadata?.SourcePort is byte metadataPort
                    ? [metadataPort]
                    : Array.Empty<byte>())
                .Concat(NeedsStructureOnlyBucket(value, parsed.Format)
                    ? [value.FinalSourcePort]
                    : Array.Empty<byte>()))
            .Distinct()
            .Order()
            .ToArray();
        Dictionary<byte, byte> portMap = ValidatePortMap(sourcePorts, zeroBasedPortMapping);

        MidoraProject project = new(parsed.TicksPerQuarterNote);
        project.Metadata.ProjectName = projectName.Trim();
        ImportConductor(project, scans, diagnostics, cancellationToken);

        Dictionary<BucketKey, ImportBucket> buckets = [];
        foreach (TrackScan scan in scans)
        {
            foreach (ScannedChannelEvent value in scan.ChannelEvents)
            {
                BucketKey key = new(scan.Track.SourceTrackIndex, value.SourcePort, value.Message.ChannelNumber);
                GetBucket(key, scan, value.Order).ChannelEvents.Add(value);
            }
        }

        foreach (TrackScan scan in scans)
        {
            foreach (ScannedOpaqueEvent value in scan.OpaqueEvents)
            {
                ImportBucket? owner = buckets.Values
                    .Where(bucket => bucket.Key.SourceTrackIndex == scan.Track.SourceTrackIndex
                        && bucket.Key.SourcePort == value.SourcePort)
                    .OrderBy(bucket => bucket.FirstOrder)
                    .ThenBy(bucket => bucket.Key.Channel)
                    .FirstOrDefault();
                if (owner is null)
                {
                    byte channel = scan.MidoraMetadata is { SourcePort: byte metadataPort, SourceChannel: byte metadataChannel }
                        && metadataPort == value.SourcePort
                            ? metadataChannel
                            : FindStructureChannel(buckets.Values, value.SourcePort);
                    owner = GetBucket(
                        new(scan.Track.SourceTrackIndex, value.SourcePort, channel),
                        scan,
                        value.Order);
                }
                owner.OpaqueEvents.Add(value);
            }
        }

        foreach (TrackScan scan in scans)
        {
            if (scan.Track.SourceTrackIndex == 0
                && parsed.Format == 1
                && !buckets.Keys.Any(key => key.SourceTrackIndex == 0))
            {
                continue;
            }
            if (!buckets.Keys.Any(key => key.SourceTrackIndex == scan.Track.SourceTrackIndex))
            {
                byte sourcePort = scan.MidoraMetadata?.SourcePort ?? scan.FinalSourcePort;
                byte channel = scan.MidoraMetadata?.SourceChannel
                    ?? FindStructureChannel(buckets.Values, sourcePort);
                _ = GetBucket(
                    new(scan.Track.SourceTrackIndex, sourcePort, channel),
                    scan,
                    long.MaxValue);
            }
        }

        Dictionary<(byte Port, byte Channel), MidiChannelRoot> roots = [];
        HashSet<int> fallbackNameSourceTracks = [];
        int fallbackNameTrackCount = 0;
        ImportBucket[] orderedBuckets = buckets.Values
            .OrderBy(value => value.Scan.MidoraMetadata is null ? 1 : 0)
            .ThenBy(value => value.Scan.MidoraMetadata?.RootOrder ?? value.Key.SourceTrackIndex)
            .ThenBy(value => value.Scan.MidoraMetadata?.TrackOrder ?? 0)
            .ThenBy(value => value.Key.SourceTrackIndex)
            .ThenBy(value => value.FirstOrder)
            .ThenBy(value => value.Key.SourcePort)
            .ThenBy(value => value.Key.Channel)
            .ToArray();
        Dictionary<int, int> bucketCountBySourceTrack = orderedBuckets
            .GroupBy(value => value.Key.SourceTrackIndex)
            .ToDictionary(value => value.Key, value => value.Count());
        foreach (ImportBucket bucket in orderedBuckets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte targetPort = portMap[bucket.Key.SourcePort];
            (byte Port, byte Channel) route = (targetPort, bucket.Key.Channel);
            if (!roots.TryGetValue(route, out MidiChannelRoot? root))
            {
                root = new(project)
                {
                    Name = bucket.Scan.MidoraMetadata?.RootName
                        ?? $"Port {targetPort + 1} / Channel {bucket.Key.Channel + 1}",
                    RoutingMode = bucket.Scan.MidoraMetadata?.RoutingMode
                        ?? MidiChannelRootRoutingMode.Fixed,
                    FixedZeroBasedPort = targetPort,
                    FixedZeroBasedChannel = bucket.Key.Channel,
                    ChannelMode = bucket.Scan.MidoraMetadata?.ChannelMode
                        ?? (bucket.Key.Channel == 9
                            ? MidiChannelMode.Percussion
                            : MidiChannelMode.Melodic)
                };
                roots.Add(route, root);
                project.MidiChannelRoots.Add(root);
                project.ArrangementParents.Add(new(
                    ArrangementParentKind.MidiChannelRoot,
                    root.Id));
            }

            bool usesFallbackName = string.IsNullOrWhiteSpace(bucket.Scan.TrackName);
            string baseName = usesFallbackName
                ? $"MIDI Track {bucket.Key.SourceTrackIndex + 1}"
                : bucket.Scan.TrackName;
            if (usesFallbackName)
            {
                fallbackNameSourceTracks.Add(bucket.Key.SourceTrackIndex);
                fallbackNameTrackCount++;
            }
            string name = bucketCountBySourceTrack[bucket.Key.SourceTrackIndex] == 1
                ? baseName
                : $"{baseName} [Port {targetPort + 1}, Channel {bucket.Key.Channel + 1}]";
            PureMidiTrack track = new(project)
            {
                Name = name,
                MidiChannelRootId = root.Id
            };
            project.PureMidiTracks.Add(track);
            root.MidiTrackIds.Add(track.Id);

            if (bucket.Scan.Track.EndTick <= 0)
            {
                continue;
            }
            MidiSegment segment = new(project)
            {
                ProjectStartTick = 0,
                LengthTicks = bucket.Scan.Track.EndTick,
                ContentOffsetTick = 0
            };
            track.Segments.Add(segment);
            ImportBucketEvents(project, segment, bucket, targetPort, diagnostics);
        }
        if (fallbackNameTrackCount != 0)
        {
            int firstTrackIndex = fallbackNameSourceTracks.Min();
            diagnostics.Add(new(
                "MIDORA-MIDI-IMPORT-FALLBACK-TRACK-NAME",
                DiagnosticSeverity.Info,
                $"Assigned deterministic fallback names to {fallbackNameTrackCount} Pure MIDI Track(s) "
                + $"derived from {fallbackNameSourceTracks.Count} source MTrk(s) with no usable non-blank Track Name.",
                firstTrackIndex));
        }

        using MidoraCompiler compiler = new();
        CanonicalCompiledResult validation = compiler.CompileFull(project);
        if (!validation.IsConsumable)
        {
            string detail = string.Join(
                Environment.NewLine,
                validation.Diagnostics
                    .Where(value => value.Severity == DiagnosticSeverity.Error)
                    .Select(value => $"{value.Code}: {value.Message}"));
            throw new InvalidDataException(
                "The imported MIDI cannot form a valid Midora Project."
                + (detail.Length == 0 ? string.Empty : Environment.NewLine + detail));
        }
        return new(project, diagnostics.ToArray());

        ImportBucket GetBucket(BucketKey key, TrackScan scan, long firstOrder)
        {
            if (!buckets.TryGetValue(key, out ImportBucket? bucket))
            {
                bucket = new(key, scan, firstOrder);
                buckets.Add(key, bucket);
            }
            else
            {
                bucket.FirstOrder = Math.Min(bucket.FirstOrder, firstOrder);
            }
            return bucket;
        }
    }

    private static TrackScan ScanTrack(ParsedStandardMidiFileTrack track)
    {
        byte port = 0;
        string trackName = string.Empty;
        int invalidTrackNameCount = 0;
        int? firstInvalidTrackNameByteOffset = null;
        List<ScannedChannelEvent> channel = [];
        List<ScannedOpaqueEvent> opaque = [];
        List<ParsedStandardMidiFileEvent> conductor = [];
        List<MidoraMetadataCandidate> midoraMetadataCandidates = [];
        foreach (ParsedStandardMidiFileEvent value in track.Events)
        {
            if (value.Kind == StandardMidiFileEventKind.Meta
                && value.Type == StandardMidiFile.MidiPortMetaType)
            {
                if (value.Data.Length != 1)
                {
                    throw new InvalidDataException(
                        $"SMF MTrk {track.SourceTrackIndex} has an invalid MIDI Port Meta payload at byte {value.SourceByteOffset}.");
                }
                port = value.Data.Span[0];
                continue;
            }
            if (value.Kind == StandardMidiFileEventKind.Meta
                && value.Type == StandardMidiFile.TrackNameMetaType)
            {
                if (TryDecodeUtf8(value.Data.Span, out string decodedTrackName))
                {
                    string normalizedTrackName = decodedTrackName.Trim();
                    if (normalizedTrackName.Length != 0)
                    {
                        trackName = normalizedTrackName;
                    }
                }
                else
                {
                    invalidTrackNameCount++;
                    firstInvalidTrackNameByteOffset ??= value.SourceByteOffset;
                }
                continue;
            }
            if (value.Kind == StandardMidiFileEventKind.Meta
                && value.Type == 0x7f
                && TryParseMidoraMetadata(value.Data.Span, out MidoraTrackMetadata? metadata))
            {
                midoraMetadataCandidates.Add(new(metadata!, value, port));
                continue;
            }
            if (IsConductor(value))
            {
                conductor.Add(value);
                continue;
            }
            if (value.Kind == StandardMidiFileEventKind.ChannelVoice)
            {
                channel.Add(new(value.Tick, value.Order, port, value.Message, value.SourceByteOffset));
            }
            else
            {
                opaque.Add(new(value.Tick, value.Order, port, value, value.SourceByteOffset));
            }
        }
        return new(
            track,
            trackName,
            port,
            channel,
            opaque,
            conductor,
            midoraMetadataCandidates,
            MidoraMetadata: null,
            invalidTrackNameCount,
            firstInvalidTrackNameByteOffset);
    }

    private static void AddInvalidTrackNameDiagnostic(
        IReadOnlyList<TrackScan> scans,
        ICollection<MidiProjectImportDiagnostic> diagnostics)
    {
        TrackScan[] affected = scans
            .Where(value => value.InvalidTrackNameCount != 0)
            .OrderBy(value => value.Track.SourceTrackIndex)
            .ToArray();
        if (affected.Length == 0)
        {
            return;
        }

        TrackScan first = affected[0];
        int count = affected.Sum(value => value.InvalidTrackNameCount);
        diagnostics.Add(new(
            "MIDORA-MIDI-IMPORT-INVALID-TRACK-NAME",
            DiagnosticSeverity.Info,
            $"Discarded {count} Track Name Meta event(s) from {affected.Length} source MTrk(s) because the payload was not strict UTF-8. "
            + "A remaining valid name was used when available; otherwise a deterministic fallback name was assigned.",
            first.Track.SourceTrackIndex,
            first.FirstInvalidTrackNameByteOffset));
    }

    private static TrackScan[] NormalizeMidoraMetadata(TrackScan[] scans)
    {
        Dictionary<int, MidoraTrackMetadata> normalized = [];
        HashSet<long> invalidRootIds = [];
        foreach (TrackScan scan in scans)
        {
            if (scan.MidoraMetadataCandidates.Count != 1)
            {
                foreach (MidoraMetadataCandidate metadataCandidate in scan.MidoraMetadataCandidates)
                {
                    invalidRootIds.Add(metadataCandidate.Metadata.SourceRootId);
                }
                continue;
            }

            MidoraMetadataCandidate candidate = scan.MidoraMetadataCandidates[0];
            (byte Port, byte Channel)[] routes = scan.ChannelEvents
                .Select(value => (value.SourcePort, value.Message.ChannelNumber))
                .Distinct()
                .ToArray();
            byte? sourcePort = candidate.Metadata.SourcePort;
            byte? sourceChannel = candidate.Metadata.SourceChannel;
            bool valid = routes.Length <= 1;
            if (sourcePort.HasValue != sourceChannel.HasValue)
            {
                valid = false;
            }
            else if (sourcePort.HasValue)
            {
                valid &= candidate.SourcePort == sourcePort.Value;
                valid &= routes.Length == 0
                    || routes[0] == (sourcePort.Value, sourceChannel!.Value);
            }
            else if (routes.Length == 1)
            {
                sourcePort = routes[0].Port;
                sourceChannel = routes[0].Channel;
                valid &= candidate.SourcePort == sourcePort.Value;
            }
            else
            {
                valid = false;
            }

            if (sourcePort.HasValue)
            {
                valid &= scan.OpaqueEvents.All(value => value.SourcePort == sourcePort.Value);
            }
            if (!valid)
            {
                invalidRootIds.Add(candidate.Metadata.SourceRootId);
                continue;
            }

            normalized.Add(
                scan.Track.SourceTrackIndex,
                candidate.Metadata with
                {
                    SourcePort = sourcePort,
                    SourceChannel = sourceChannel
                });
        }

        foreach (IGrouping<long, MidoraTrackMetadata> group in normalized.Values
            .GroupBy(value => value.SourceRootId))
        {
            MidoraTrackMetadata first = group.First();
            bool consistent = group.All(value =>
                value.RootOrder == first.RootOrder
                && value.RoutingMode == first.RoutingMode
                && value.ChannelMode == first.ChannelMode
                && value.SourcePort == first.SourcePort
                && value.SourceChannel == first.SourceChannel
                && string.Equals(value.RootName, first.RootName, StringComparison.Ordinal));
            bool uniqueTrackIds = group.Select(value => value.SourceTrackId).Distinct().Count()
                == group.Count();
            bool uniqueTrackOrders = group.Select(value => value.TrackOrder).Distinct().Count()
                == group.Count();
            if (!consistent || !uniqueTrackIds || !uniqueTrackOrders)
            {
                invalidRootIds.Add(group.Key);
            }
        }

        foreach (IGrouping<(byte? Port, byte? Channel), MidoraTrackMetadata> group in normalized.Values
            .GroupBy(value => (value.SourcePort, value.SourceChannel))
            .Where(value => value.Select(item => item.SourceRootId).Distinct().Count() > 1))
        {
            invalidRootIds.UnionWith(group.Select(value => value.SourceRootId));
        }
        foreach (IGrouping<int, MidoraTrackMetadata> group in normalized.Values
            .GroupBy(value => value.RootOrder)
            .Where(value => value.Select(item => item.SourceRootId).Distinct().Count() > 1))
        {
            invalidRootIds.UnionWith(group.Select(value => value.SourceRootId));
        }
        foreach (IGrouping<long, MidoraTrackMetadata> group in normalized.Values
            .GroupBy(value => value.SourceTrackId)
            .Where(value => value.Select(item => item.SourceRootId).Distinct().Count() > 1))
        {
            invalidRootIds.UnionWith(group.Select(value => value.SourceRootId));
        }

        TrackScan[] result = new TrackScan[scans.Length];
        for (int index = 0; index < scans.Length; index++)
        {
            TrackScan scan = scans[index];
            MidoraTrackMetadata? metadata = normalized.GetValueOrDefault(
                scan.Track.SourceTrackIndex);
            if (metadata is not null && !invalidRootIds.Contains(metadata.SourceRootId))
            {
                if (metadata is
                    {
                        ChannelMode: MidiChannelMode.Melodic,
                        SourceChannel: 9
                    })
                {
                    scan.OpaqueEvents.RemoveAll(IsMidoraChannel10NormalPartInitialization);
                }
                result[index] = scan with { MidoraMetadata = metadata };
                continue;
            }

            foreach (MidoraMetadataCandidate candidate in scan.MidoraMetadataCandidates)
            {
                scan.OpaqueEvents.Add(new(
                    candidate.Event.Tick,
                    candidate.Event.Order,
                    candidate.SourcePort,
                    candidate.Event,
                    candidate.Event.SourceByteOffset));
            }
            result[index] = scan with { MidoraMetadata = null };
        }
        return result;
    }

    private static bool IsMidoraChannel10NormalPartInitialization(ScannedOpaqueEvent value) =>
        value.Tick == 0
        && value.Event.Kind == StandardMidiFileEventKind.SystemExclusive
        && value.Event.Type == 0xf0
        && (value.Event.Data.Span.SequenceEqual(RolandGsChannel10NormalPart)
            || value.Event.Data.Span.SequenceEqual(YamahaXgChannel10NormalPart));

    private static void ImportConductor(
        MidoraProject project,
        IReadOnlyList<TrackScan> scans,
        ICollection<MidiProjectImportDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        project.Conductor.Tempos.Clear();
        project.Conductor.TimeSignatures.Clear();
        project.Conductor.KeySignatures.Clear();
        project.Conductor.Markers.Clear();
        Dictionary<long, ImportedTempo> temposByTick = [];
        int identicalTempoDuplicateCount = 0;
        int conflictingTempoDuplicateCount = 0;
        HashSet<long> identicalTempoDuplicateTicks = [];
        HashSet<long> conflictingTempoDuplicateTicks = [];
        ImportedTempo? firstIdenticalTempoDuplicate = null;
        (ImportedTempo Previous, ImportedTempo Replacement)? firstConflictingTempoDuplicate = null;
        foreach (TrackScan scan in scans.OrderBy(value => value.Track.SourceTrackIndex))
        {
            foreach (ParsedStandardMidiFileEvent value in scan.ConductorEvents
                .OrderBy(item => item.Order))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadOnlySpan<byte> data = value.Data.Span;
                switch (value.Type)
                {
                    case StandardMidiFile.SetTempoMetaType:
                        RequireLength(scan, value, 3, "Set Tempo");
                        int microseconds = (data[0] << 16) | (data[1] << 8) | data[2];
                        if (microseconds == 0)
                        {
                            throw new InvalidDataException(
                                $"SMF MTrk {scan.Track.SourceTrackIndex} contains a zero Set Tempo value at byte {value.SourceByteOffset}.");
                        }
                        ImportedTempo importedTempo = new(
                            value.Tick,
                            microseconds,
                            60_000_000m / microseconds,
                            scan.Track.SourceTrackIndex,
                            value.Order,
                            value.SourceByteOffset);
                        if (temposByTick.TryGetValue(value.Tick, out ImportedTempo previousTempo))
                        {
                            if (previousTempo.MicrosecondsPerQuarterNote == microseconds)
                            {
                                identicalTempoDuplicateCount++;
                                identicalTempoDuplicateTicks.Add(value.Tick);
                                firstIdenticalTempoDuplicate ??= importedTempo;
                            }
                            else
                            {
                                conflictingTempoDuplicateCount++;
                                conflictingTempoDuplicateTicks.Add(value.Tick);
                                firstConflictingTempoDuplicate ??= (previousTempo, importedTempo);
                            }
                        }
                        temposByTick[value.Tick] = importedTempo;
                        break;
                    case StandardMidiFile.TimeSignatureMetaType:
                        RequireLength(scan, value, 4, "Time Signature");
                        if (data[1] > 30)
                        {
                            throw new InvalidDataException(
                                $"SMF MTrk {scan.Track.SourceTrackIndex} has an unrepresentable Time Signature denominator at byte {value.SourceByteOffset}.");
                        }
                        project.Conductor.TimeSignatures.Add(new(
                            project,
                            value.Tick,
                            data[0],
                            checked(1 << data[1])));
                        break;
                    case StandardMidiFile.KeySignatureMetaType:
                        RequireLength(scan, value, 2, "Key Signature");
                        project.Conductor.KeySignatures.Add(new(
                            project,
                            value.Tick,
                            unchecked((sbyte)data[0]),
                            data[1] != 0));
                        break;
                    case StandardMidiFile.MarkerMetaType:
                        project.Conductor.Markers.Add(new(
                            project,
                            value.Tick,
                            DecodeUtf8(value, scan.Track.SourceTrackIndex, "Marker")));
                        break;
                }
            }
        }
        foreach (ImportedTempo value in temposByTick.Values.OrderBy(value => value.Tick))
        {
            project.Conductor.Tempos.Add(new(project, value.Tick, value.BeatsPerMinute));
        }
        if (!temposByTick.ContainsKey(0))
        {
            project.Conductor.Tempos.Insert(0, new(project, 0, 120m));
            diagnostics.Add(new(
                "MIDORA-MIDI-IMPORT-DEFAULT-TEMPO",
                DiagnosticSeverity.Info,
                "The source had no Set Tempo event at tick 0; Midora added an explicit 120 BPM Tempo state."));
        }
        if (!project.Conductor.TimeSignatures.Any(value => value.Tick == 0))
        {
            project.Conductor.TimeSignatures.Insert(0, new(project, 0, 4, 4));
            diagnostics.Add(new(
                "MIDORA-MIDI-IMPORT-DEFAULT-TIME-SIGNATURE",
                DiagnosticSeverity.Info,
                "The source had no Time Signature event at tick 0; Midora added an explicit 4/4 Time Signature state."));
        }
        if (identicalTempoDuplicateCount != 0)
        {
            ImportedTempo first = firstIdenticalTempoDuplicate
                ?? throw new InvalidOperationException("A duplicate Tempo count has no source event.");
            diagnostics.Add(new(
                "MIDORA-MIDI-IMPORT-REDUNDANT-TEMPO",
                DiagnosticSeverity.Info,
                $"Removed {identicalTempoDuplicateCount} redundant Set Tempo event(s) at "
                + $"{identicalTempoDuplicateTicks.Count} tick position(s); the last event in source MTrk/event order was retained.",
                first.SourceTrackIndex,
                first.SourceByteOffset,
                first.Tick));
        }
        if (conflictingTempoDuplicateCount != 0)
        {
            (ImportedTempo previous, ImportedTempo replacement) = firstConflictingTempoDuplicate
                ?? throw new InvalidOperationException("A conflicting Tempo count has no source event.");
            diagnostics.Add(new(
                "MIDORA-MIDI-IMPORT-CONFLICTING-TEMPO",
                DiagnosticSeverity.Warning,
                $"Resolved {conflictingTempoDuplicateCount} conflicting Set Tempo event(s) at "
                + $"{conflictingTempoDuplicateTicks.Count} tick position(s) by retaining the last event in source MTrk/event order. "
                + $"First conflict at tick {replacement.Tick}: MTrk {previous.SourceTrackIndex} "
                + $"({FormatBpm(previous.BeatsPerMinute)} BPM) was replaced by MTrk {replacement.SourceTrackIndex} "
                + $"({FormatBpm(replacement.BeatsPerMinute)} BPM).",
                replacement.SourceTrackIndex,
                replacement.SourceByteOffset,
                replacement.Tick));
        }
    }

    private static void ImportBucketEvents(
        MidoraProject project,
        MidiSegment segment,
        ImportBucket bucket,
        byte targetPort,
        ICollection<MidiProjectImportDiagnostic> diagnostics)
    {
        Dictionary<byte, Queue<ScannedChannelEvent>> active = [];
        HashSet<long> pairedOrders = [];
        List<DirectMidiNote> notes = [];
        bool hasUnpaired = false;
        ScannedChannelEvent? firstUnpaired = null;
        foreach (ScannedChannelEvent value in bucket.ChannelEvents
            .OrderBy(value => value.Tick)
            .ThenBy(value => value.Order))
        {
            MidiMessage message = value.Message;
            if (message.MessageType == MidiMessageType.NoteOn && message.Byte2 != 0)
            {
                if (!active.TryGetValue(message.Byte1, out Queue<ScannedChannelEvent>? queue))
                {
                    queue = new();
                    active.Add(message.Byte1, queue);
                }
                queue.Enqueue(value);
                continue;
            }
            if (message.MessageType != MidiMessageType.NoteOff
                && !(message.MessageType == MidiMessageType.NoteOn && message.Byte2 == 0))
            {
                continue;
            }
            if (!active.TryGetValue(message.Byte1, out Queue<ScannedChannelEvent>? starts)
                || starts.Count == 0)
            {
                hasUnpaired = true;
                firstUnpaired ??= value;
                continue;
            }
            ScannedChannelEvent start = starts.Dequeue();
            if (value.Tick <= start.Tick)
            {
                hasUnpaired = true;
                firstUnpaired ??= start;
                continue;
            }
            pairedOrders.Add(start.Order);
            pairedOrders.Add(value.Order);
            notes.Add(new(project)
            {
                StartTick = start.Tick,
                LengthTicks = value.Tick - start.Tick,
                Key = start.Message.Byte1,
                NoteOnVelocity = start.Message.Byte2,
                NoteOffVelocity = message.MessageType == MidiMessageType.NoteOff
                    ? message.Byte2
                    : 0,
                NoteOnOrder = start.Order,
                NoteOffOrder = value.Order
            });
        }
        if (active.Values.Any(value => value.Count != 0))
        {
            hasUnpaired = true;
            firstUnpaired ??= active.Values
                .SelectMany(value => value)
                .OrderBy(value => value.Order)
                .First();
        }
        segment.Notes.AddRange(notes.OrderBy(value => value.StartTick).ThenBy(value => value.NoteOnOrder));
        foreach (ScannedChannelEvent value in bucket.ChannelEvents
            .Where(value => !pairedOrders.Contains(value.Order))
            .OrderBy(value => value.Tick)
            .ThenBy(value => value.Order))
        {
            MidiMessage message = value.Message;
            DirectMidiChannelEventKind kind = message.MessageType switch
            {
                MidiMessageType.NoteOff => DirectMidiChannelEventKind.NoteOff,
                MidiMessageType.NoteOn when message.Byte2 == 0 => DirectMidiChannelEventKind.NoteOff,
                MidiMessageType.NoteOn => DirectMidiChannelEventKind.NoteOn,
                MidiMessageType.PolyphonicKeyPressure => DirectMidiChannelEventKind.PolyphonicKeyPressure,
                MidiMessageType.ControlChange => DirectMidiChannelEventKind.ControlChange,
                MidiMessageType.ProgramChange => DirectMidiChannelEventKind.ProgramChange,
                MidiMessageType.ChannelPressure => DirectMidiChannelEventKind.ChannelPressure,
                MidiMessageType.PitchWheelChange => DirectMidiChannelEventKind.PitchBend,
                _ => throw new InvalidDataException(
                    $"Unsupported MIDI channel event type {message.MessageType}.")
            };
            segment.ChannelEvents.Add(new(project)
            {
                Tick = value.Tick,
                Kind = kind,
                Data1 = message.Byte1,
                Data2 = kind == DirectMidiChannelEventKind.NoteOff
                    && message.MessageType == MidiMessageType.NoteOn
                    ? 0
                    : message.Byte2,
                Order = value.Order
            });
        }
        foreach (ScannedOpaqueEvent value in bucket.OpaqueEvents
            .OrderBy(value => value.Tick)
            .ThenBy(value => value.Order))
        {
            segment.OpaqueEvents.Add(new(project)
            {
                Tick = value.Tick,
                Kind = value.Event.Kind == StandardMidiFileEventKind.Meta
                    ? OpaqueMidiEventKind.Meta
                    : value.Event.Type == 0xf7
                        ? OpaqueMidiEventKind.SystemExclusiveContinuation
                        : OpaqueMidiEventKind.SystemExclusive,
                MetaType = value.Event.Kind == StandardMidiFileEventKind.Meta
                    ? value.Event.Type
                    : (byte)0,
                Payload = value.Event.Data.ToArray(),
                Order = value.Order
            });
        }
        if (hasUnpaired)
        {
            ScannedChannelEvent location = firstUnpaired
                ?? throw new InvalidOperationException(
                    "An unmatched Note diagnostic has no source event.");
            diagnostics.Add(new(
                "MIDORA-MIDI-IMPORT-UNPAIRED-NOTE",
                DiagnosticSeverity.Warning,
                $"The source Track contains unmatched Note messages at tick {location.Tick}, "
                + $"Port {targetPort + 1}, Channel {bucket.Key.Channel + 1}; "
                + "they were preserved as raw direct MIDI events.",
                bucket.Key.SourceTrackIndex,
                location.SourceByteOffset,
                location.Tick,
                targetPort,
                bucket.Key.Channel));
        }
    }

    private static Dictionary<byte, byte> ValidatePortMap(
        IReadOnlyList<byte> sourcePorts,
        IReadOnlyDictionary<byte, byte>? requested)
    {
        byte[] outOfRange = sourcePorts.Where(value => value > 15).ToArray();
        if (outOfRange.Length != 0 && requested is null)
        {
            throw new MidiImportPortMappingRequiredException(sourcePorts);
        }
        Dictionary<byte, byte> result = [];
        foreach (byte source in sourcePorts)
        {
            byte target = requested is not null && requested.TryGetValue(source, out byte mapped)
                ? mapped
                : source;
            if (target > 15)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(requested),
                    "Every imported MIDI Port must map to a Midora Port in the range 1..16.");
            }
            result.Add(source, target);
        }
        if (result.Values.Distinct().Count() != result.Count)
        {
            throw new ArgumentException(
                "MIDI import Port mapping must be one-to-one; source Ports cannot be silently merged.",
                nameof(requested));
        }
        return result;
    }

    private static byte FindStructureChannel(
        IEnumerable<ImportBucket> buckets,
        byte sourcePort) => buckets
        .Where(value => value.Key.SourcePort == sourcePort)
        .Select(value => value.Key.Channel)
        .DefaultIfEmpty((byte)0)
        .Min();

    private static bool NeedsStructureOnlyBucket(TrackScan scan, ushort format) =>
        scan.ChannelEvents.Count == 0
        && scan.OpaqueEvents.Count == 0
        && !(format == 1 && scan.Track.SourceTrackIndex == 0);

    private static bool IsConductor(ParsedStandardMidiFileEvent value) =>
        value.Kind == StandardMidiFileEventKind.Meta
        && value.Type is StandardMidiFile.SetTempoMetaType
            or StandardMidiFile.TimeSignatureMetaType
            or StandardMidiFile.KeySignatureMetaType
            or StandardMidiFile.MarkerMetaType;

    private static string DecodeUtf8(
        ParsedStandardMidiFileEvent value,
        int trackIndex,
        string kind)
    {
        try
        {
            return StrictUtf8.GetString(value.Data.Span);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                $"SMF MTrk {trackIndex} {kind} is not strict UTF-8 at byte {value.SourceByteOffset}.",
                exception);
        }
    }

    private static bool TryDecodeUtf8(ReadOnlySpan<byte> value, out string result)
    {
        try
        {
            result = StrictUtf8.GetString(value);
            return true;
        }
        catch (DecoderFallbackException)
        {
            result = string.Empty;
            return false;
        }
    }

    private static string FormatBpm(decimal value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);

    private static bool TryParseMidoraMetadata(
        ReadOnlySpan<byte> data,
        out MidoraTrackMetadata? metadata)
    {
        metadata = null;
        const int v1FixedLength = 6 + 1 + 8 + 8 + 4 + 4 + 1 + 1 + 2;
        const int v2FixedLength = v1FixedLength + 2;
        if (data.Length < 7 || !data[..6].SequenceEqual("MIDORA"u8))
        {
            return false;
        }
        int offset = 6;
        byte version = data[offset++];
        int fixedLength = version switch
        {
            1 => v1FixedLength,
            2 => v2FixedLength,
            _ => int.MaxValue
        };
        if (data.Length < fixedLength)
        {
            return false;
        }
        long rootId = BinaryPrimitives.ReadInt64LittleEndian(data[offset..]);
        offset += 8;
        long trackId = BinaryPrimitives.ReadInt64LittleEndian(data[offset..]);
        offset += 8;
        int rootOrder = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
        offset += 4;
        int trackOrder = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
        offset += 4;
        MidiChannelRootRoutingMode routing = (MidiChannelRootRoutingMode)data[offset++];
        MidiChannelMode channelMode = (MidiChannelMode)data[offset++];
        byte? sourcePort = null;
        byte? sourceChannel = null;
        if (version == 2)
        {
            sourcePort = data[offset++];
            sourceChannel = data[offset++];
        }
        ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        offset += 2;
        if (rootId <= 0
            || trackId <= 0
            || rootOrder < 0
            || trackOrder < 0
            || !Enum.IsDefined(routing)
            || !Enum.IsDefined(channelMode)
            || sourcePort > 15
            || sourceChannel > 15
            || data.Length - offset != nameLength)
        {
            return false;
        }
        try
        {
            string rootName = StrictUtf8.GetString(data.Slice(offset, nameLength));
            metadata = new(
                rootId,
                trackId,
                rootOrder,
                trackOrder,
                routing,
                channelMode,
                sourcePort,
                sourceChannel,
                rootName);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static void RequireLength(
        TrackScan scan,
        ParsedStandardMidiFileEvent value,
        int expected,
        string kind)
    {
        if (value.Data.Length != expected)
        {
            throw new InvalidDataException(
                $"SMF MTrk {scan.Track.SourceTrackIndex} {kind} payload has length {value.Data.Length}, expected {expected}, at byte {value.SourceByteOffset}.");
        }
    }

    private readonly record struct BucketKey(
        int SourceTrackIndex,
        byte SourcePort,
        byte Channel);

    private sealed class ImportBucket(BucketKey key, TrackScan scan, long firstOrder)
    {
        public BucketKey Key { get; } = key;
        public TrackScan Scan { get; } = scan;
        public long FirstOrder { get; set; } = firstOrder;
        public List<ScannedChannelEvent> ChannelEvents { get; } = [];
        public List<ScannedOpaqueEvent> OpaqueEvents { get; } = [];
    }

    private sealed record TrackScan(
        ParsedStandardMidiFileTrack Track,
        string TrackName,
        byte FinalSourcePort,
        List<ScannedChannelEvent> ChannelEvents,
        List<ScannedOpaqueEvent> OpaqueEvents,
        List<ParsedStandardMidiFileEvent> ConductorEvents,
        List<MidoraMetadataCandidate> MidoraMetadataCandidates,
        MidoraTrackMetadata? MidoraMetadata,
        int InvalidTrackNameCount,
        int? FirstInvalidTrackNameByteOffset);

    private readonly record struct ImportedTempo(
        long Tick,
        int MicrosecondsPerQuarterNote,
        decimal BeatsPerMinute,
        int SourceTrackIndex,
        long SourceOrder,
        int SourceByteOffset);

    private sealed record MidoraTrackMetadata(
        long SourceRootId,
        long SourceTrackId,
        int RootOrder,
        int TrackOrder,
        MidiChannelRootRoutingMode RoutingMode,
        MidiChannelMode ChannelMode,
        byte? SourcePort,
        byte? SourceChannel,
        string RootName);

    private sealed record MidoraMetadataCandidate(
        MidoraTrackMetadata Metadata,
        ParsedStandardMidiFileEvent Event,
        byte SourcePort);

    private readonly record struct ScannedChannelEvent(
        long Tick,
        long Order,
        byte SourcePort,
        MidiMessage Message,
        int SourceByteOffset);

    private readonly record struct ScannedOpaqueEvent(
        long Tick,
        long Order,
        byte SourcePort,
        ParsedStandardMidiFileEvent Event,
        int SourceByteOffset);
}
