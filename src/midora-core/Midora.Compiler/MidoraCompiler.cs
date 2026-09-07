using Midora.Domain;
using Midora.Mapping.Contract.V2;
using Midora.Midi;

namespace Midora.Compiler;

public sealed partial class MidoraCompiler : IDisposable
{
    private readonly Dictionary<MidoraId, TrackCacheEntry> _trackCache = [];
    private readonly MappingEngine _mapping = new();
    private MidoraProject? _cacheOwner;
    private bool _disposed;

    public CompilerRunTelemetry LastTelemetry { get; private set; }

    public CanonicalCompiledResult CompileFull(
        MidoraProject project,
        CompilationRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        request ??= new CompilationRequest();
        return CompileTransactional(
            project,
            request,
            false,
            ProjectChangeSet.Everything,
            cancellationToken);
    }

    public CanonicalCompiledResult CompileIncremental(
        MidoraProject project,
        ProjectChangeSet changes,
        CompilationRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(changes);
        request ??= new CompilationRequest();
        return CompileTransactional(project, request, true, changes, cancellationToken);
    }

    private CanonicalCompiledResult CompileTransactional(
        MidoraProject project,
        CompilationRequest request,
        bool incremental,
        ProjectChangeSet changes,
        CancellationToken cancellationToken)
    {
        KeyValuePair<MidoraId, TrackCacheEntry>[] committedCache = _trackCache.ToArray();
        MidoraProject? committedOwner = _cacheOwner;
        try
        {
            return CompileCore(
                project,
                request,
                incremental,
                changes,
                cancellationToken);
        }
        catch
        {
            _trackCache.Clear();
            foreach (KeyValuePair<MidoraId, TrackCacheEntry> item in committedCache)
            {
                _trackCache.Add(item.Key, item.Value);
            }
            _cacheOwner = committedOwner;
            throw;
        }
    }

    public void ClearCache()
    {
        _trackCache.Clear();
        _mapping.ClearCache();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        ClearCache();
        _mapping.Dispose();
        _cacheOwner = null;
        _disposed = true;
    }

    private CanonicalCompiledResult CompileCore(
        MidoraProject project,
        CompilationRequest request,
        bool incremental,
        ProjectChangeSet changes,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.HeldPreviewGateOpen
            && (request.Purpose != CompilationPurpose.EventInstrumentPreview
                || request.StartTick < 0
                || !request.EndTick.HasValue
                || request.EndTick.Value <= request.StartTick
                || request.HeldPreviewFinalGateLengthTicks.HasValue))
        {
            throw new ArgumentException(
                "An open held-preview gate requires a non-empty Event Instrument Preview window [start,end).",
                nameof(request));
        }
        if (request.HeldPreviewFinalGateLengthTicks is long finalGateLength
            && (request.HeldPreviewGateOpen
                || request.Purpose != CompilationPurpose.EventInstrumentPreview
                || request.StartTick < 0
                || !request.EndTick.HasValue
                || request.EndTick.Value <= request.StartTick
                || finalGateLength <= 0))
        {
            throw new ArgumentException(
                "A held-preview Gate End requires a positive final Gate Length and a non-empty Event Instrument Preview window [start,end).",
                nameof(request));
        }
        if (!ReferenceEquals(_cacheOwner, project))
        {
            ClearCache();
            _cacheOwner = project;
        }
        cancellationToken.ThrowIfCancellationRequested();
        _mapping.SynchronizeFunctions(
            project.EventInstruments.SelectMany(instrument => instrument.MappingFunctions),
            cancellationToken);
        LastTelemetry = default;
        List<CompilerDiagnostic> diagnostics = SemanticValidator.Validate(
            project,
            request,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateMappingFunctions(project, request, diagnostics, cancellationToken);
        long naturalEnd = GetNaturalEnd(project, request.IncludedTrackIds);
        long endTick = request.EndTick ?? project.Conductor.EndMarkerTick ?? naturalEnd;
        if (endTick < request.StartTick)
        {
            diagnostics.Add(new("MIDORA2001", DiagnosticSeverity.Error,
                "The effective compilation end tick is earlier than the range start.", new(Tick: endTick)));
            endTick = request.StartTick;
        }

        CanonicalConductor conductor = FreezeConductor(project.Conductor, request.StartTick, endTick);
        cancellationToken.ThrowIfCancellationRequested();
        if (diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            return Failure(
                project,
                request,
                endTick,
                conductor,
                diagnostics,
                CompilationFailureStage.SemanticValidation);
        }

        HashSet<MidoraId> participatingInstrumentIds =
            SemanticValidator.GetParticipatingInstrumentIds(project, request);
        Dictionary<MidoraId, EventInstrument> instruments = project.EventInstruments
            .Where(instrument => request.IncludedTrackIds is null
                || participatingInstrumentIds.Contains(instrument.Id))
            .ToDictionary(value => value.Id);
        List<RawInstance> instances = [];
        int instanceExpansionDiagnosticStart = diagnostics.Count;
        int recompiledTracks = 0;
        int reusedTracks = 0;
        int recompiledSegments = 0;
        int reusedSegments = 0;
        int stateConvergences = 0;
        long? earliestDirtyTick = null;
        IReadOnlyList<LogicalTrack> selectedTracks = project.LogicalTracksInArrangementOrder()
            .Where(track => request.IncludedTrackIds is null || request.IncludedTrackIds.Contains(track.Id))
            .ToArray();
        IReadOnlyList<PureMidiTrack> selectedPureMidiTracks = project.PureMidiTracksInArrangementOrder()
            .Where(track => request.IncludedTrackIds is null || request.IncludedTrackIds.Contains(track.Id))
            .ToArray();

        HashSet<MidoraId> liveTrackIds = project.Tracks.Select(track => track.Id).ToHashSet();
        foreach (MidoraId cachedId in _trackCache.Keys.Where(id => !liveTrackIds.Contains(id)).ToArray())
        {
            _trackCache.Remove(cachedId);
        }

        foreach (LogicalTrack track in selectedTracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TrackExpansion expansion = ExpandTrackIncrementally(
                project,
                track,
                instruments,
                incremental,
                changes,
                request.HeldPreviewGateOpen,
                request.HeldPreviewFinalGateLengthTicks,
                cancellationToken);
            instances.AddRange(expansion.Instances);
            diagnostics.AddRange(expansion.Diagnostics);
            recompiledTracks += expansion.Recompiled ? 1 : 0;
            reusedTracks += expansion.Recompiled ? 0 : 1;
            recompiledSegments += expansion.RecompiledSegmentCount;
            reusedSegments += expansion.ReusedSegmentCount;
            stateConvergences += expansion.StateConvergenceCount;
            if (expansion.EarliestDirtyTick.HasValue
                && (!earliestDirtyTick.HasValue || expansion.EarliestDirtyTick.Value < earliestDirtyTick.Value))
            {
                earliestDirtyTick = expansion.EarliestDirtyTick;
            }
        }

        ApplyDestructiveOverlapPolicies(
            project,
            instances,
            diagnostics,
            instanceExpansionDiagnosticStart,
            request.HeldPreviewGateOpen,
            request.HeldPreviewFinalGateLengthTicks,
            cancellationToken);

        CompilerRunTelemetry telemetry = new(recompiledTracks, reusedTracks)
        {
            RecompiledSegmentCount = recompiledSegments,
            ReusedSegmentCount = reusedSegments,
            StateConvergenceCount = stateConvergences,
            EarliestDirtyTick = earliestDirtyTick
        };

        if (!request.EndTick.HasValue && !project.Conductor.EndMarkerTick.HasValue)
        {
            endTick = Math.Max(
                instances.Select(static value => value.SegmentEndTick).DefaultIfEmpty(0).Max(),
                GetPureMidiNaturalEnd(project, request.IncludedTrackIds));
            if (endTick < request.StartTick)
            {
                diagnostics.Add(new(
                    "MIDORA2001",
                    DiagnosticSeverity.Error,
                    "The effective compilation end tick is earlier than the range start.",
                    new(Tick: endTick)));
                endTick = request.StartTick;
            }
            conductor = FreezeConductor(project.Conductor, request.StartTick, endTick);
        }

        PureMidiPlan pureMidiPlan = BuildPureMidiPlan(
            project,
            request,
            request.StartTick,
            endTick,
            cancellationToken);
        AppendPureMidiExportCompatibilityDiagnostics(
            pureMidiPlan,
            request,
            request.StartTick,
            endTick,
            diagnostics,
            cancellationToken);

        bool expansionErrors = diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        if (request.IncludedSubVoiceIds is not null)
        {
            instances = instances.Select(instance => instance with
            {
                Voices = instance.Voices
                        .Where(voice => request.IncludedSubVoiceIds.Contains(voice.SubVoiceId))
                        .ToArray()
            })
                .Where(instance => instance.Voices.Length != 0)
                .ToList();
        }

        LogicalUsageInterval[] logicalUsageIntervals = BuildLogicalUsageIntervals(
            project,
            instances,
            cancellationToken);
        instances = instances
            .Where(instance =>
                instance.StartTick < endTick && instance.EndTick > request.StartTick
                || IsHistoricalSharedUsageStateSource(
                    instance,
                    request.StartTick,
                    logicalUsageIntervals))
            .ToList();
        AppendEmptySubVoiceDiagnostics(instances, instruments, diagnostics);
        cancellationToken.ThrowIfCancellationRequested();

        int overlapDiagnosticStart = diagnostics.Count;
        ValidateOverlap(project, instances, diagnostics, cancellationToken);
        bool overlapErrors = diagnostics
            .Skip(overlapDiagnosticStart)
            .Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        int allocationDiagnosticStart = diagnostics.Count;
        AllocationResult allocation = Allocate(
            project,
            instances,
            pureMidiPlan,
            diagnostics,
            cancellationToken);
        bool allocationErrors = diagnostics
            .Skip(allocationDiagnosticStart)
            .Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        bool warningsFail = request.TreatWarningsAsErrors
            && diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning);
        bool errors = diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if (errors || warningsFail)
        {
            CompilationFailureStage failureStage = expansionErrors
                ? CompilationFailureStage.InstanceExpansion
                : overlapErrors
                    ? CompilationFailureStage.OverlapValidation
                    : allocationErrors
                        ? CompilationFailureStage.ResourceAllocation
                        : CompilationFailureStage.WarningPolicy;
            LastTelemetry = telemetry;
            CompilationStatistics failureStatistics = CreateStatistics(
                selectedTracks.Count + selectedPureMidiTracks.Count,
                instances,
                0,
                0,
                allocation,
                pureMidiPlan);
            AppendDebugDiagnostics(
                request,
                endTick,
                diagnostics,
                failureStatistics,
                failureStage);
            return new CanonicalCompiledResult(
                project.TicksPerQuarterNote, CreateContextSummary(project, request, endTick),
                [], conductor, [], diagnostics.ToArray(),
                true, false, failureStage, 0,
                failureStatistics);
        }

        bool usesPagedPureMidi = pureMidiPlan.Roots
            .SelectMany(value => value.Tracks)
            .SelectMany(value => value.Segments)
            .Any(value => value.UsesPagedContent);
        List<CanonicalMidiEvent> allEvents = MaterializeEvents(
            project,
            instances,
            allocation.Groups,
            allocation.UnitBySubVoice,
            project.GlobalResetDefaults,
            cancellationToken);
        if (!usesPagedPureMidi)
        {
            allEvents.AddRange(MaterializePureMidiEvents(
                pureMidiPlan,
                allocation.UnitByRoot,
                project.GlobalResetDefaults,
                request.StartTick,
                cancellationToken));
        }
        allEvents.Sort(CanonicalComparer.Instance);
        allEvents = FoldSameTickStates(allEvents, cancellationToken);
        ChannelUnitAllocation[] rangeSourceAllocations = usesPagedPureMidi
            ? allocation.Allocations.Where(value => value.MidiChannelRootId == default).ToArray()
            : allocation.Allocations;
        CanonicalMidiEvent[] ranged = ApplyRange(
            allEvents,
            rangeSourceAllocations,
            request.StartTick,
            endTick,
            project.GlobalResetDefaults,
            request.HeldPreviewGateOpen,
            cancellationToken);
        if (!usesPagedPureMidi)
        {
            ranged = AssignPureMidiRangeBoundaryOwnership(
                ranged,
                pureMidiPlan,
                allocation.UnitByRoot,
                endTick);
        }
        ChannelUnitAllocation[] rangedAllocations = allocation.Allocations
            .Where(value => value.StartTick < endTick && value.EndTick > request.StartTick)
            .ToArray();
        if (allocation.PeakUnits >= 248)
        {
            diagnostics.Add(new("MIDORA2250", DiagnosticSeverity.Info,
                $"The Channel Unit peak is {allocation.PeakUnits}/256.", new()));
        }

        CanonicalSmfTrackDescriptor[] smfTracks = FreezeSmfTrackDescriptors(
            pureMidiPlan,
            allocation.UnitByRoot);
        CanonicalOpaqueMidiEvent[] opaqueMidiEvents = pureMidiPlan.OpaqueEvents;
        CanonicalMidiChannelModeSystemExclusiveEvent[] channelModeSystemExclusiveEvents =
            MaterializePureMidiChannelModeSystemExclusiveEvents(
                pureMidiPlan,
                allocation.UnitByRoot);
        ICanonicalMidiEventPageSource? pagedEventSource = usesPagedPureMidi
            ? new PureMidiPagedCanonicalSource(
                pureMidiPlan,
                allocation.UnitByRoot,
                project.GlobalResetDefaults,
                request.StartTick,
                endTick,
                cancellationToken)
            : null;
        long resultFingerprint = SourceFingerprint.ForResult(
            request.StartTick,
            endTick,
            ranged,
            conductor,
            smfTracks,
            opaqueMidiEvents,
            cancellationToken);
        if (pagedEventSource is not null)
        {
            resultFingerprint = SourceFingerprint.CombinePaged(
                resultFingerprint,
                pagedEventSource.ContentFingerprint);
        }
        cancellationToken.ThrowIfCancellationRequested();
        LastTelemetry = telemetry;
        long noteOnEventCount = checked(
            CountNoteOnEvents(ranged) + (pagedEventSource?.NoteOnEventCount ?? 0));
        long eventCount = checked(ranged.LongLength + (pagedEventSource?.EventCount ?? 0));
        CompilationStatistics successStatistics = CreateStatistics(
            selectedTracks.Count + selectedPureMidiTracks.Count,
            instances,
            eventCount,
            noteOnEventCount,
            allocation,
            pureMidiPlan);
        AppendDebugDiagnostics(
            request,
            endTick,
            diagnostics,
            successStatistics,
            null);
        return new CanonicalCompiledResult(
            project.TicksPerQuarterNote, CreateContextSummary(project, request, endTick),
            ranged, conductor, rangedAllocations, diagnostics.ToArray(),
            false, true, null, resultFingerprint,
            successStatistics,
            smfTracks,
            opaqueMidiEvents,
            pagedEventSource,
            channelModeSystemExclusiveEvents);
    }

    private static CanonicalCompiledResult Failure(
        MidoraProject project,
        CompilationRequest request,
        long endTick,
        CanonicalConductor conductor,
        List<CompilerDiagnostic> diagnostics,
        CompilationFailureStage failureStage)
    {
        CompilationStatistics statistics = new(CountSelectedTracks(project, request), 0, 0, 0);
        AppendDebugDiagnostics(request, endTick, diagnostics, statistics, failureStage);
        return new(
            project.TicksPerQuarterNote, CreateContextSummary(project, request, endTick),
            [], conductor, [], diagnostics.ToArray(),
            true, false, failureStage, 0,
            statistics);
    }

    private static CompilationContextSummary CreateContextSummary(
        MidoraProject project,
        CompilationRequest request,
        long resolvedEndTick)
    {
        CompilationEndTickSource endTickSource = request.EndTick.HasValue
            ? CompilationEndTickSource.ExplicitRequest
            : project.Conductor.EndMarkerTick.HasValue
                ? CompilationEndTickSource.ProjectEndMarker
                : CompilationEndTickSource.NaturalContent;
        return new(request, resolvedEndTick, endTickSource);
    }

    private static int CountSelectedTracks(MidoraProject project, CompilationRequest request) =>
        request.IncludedTrackIds is null
            ? project.Tracks.Count + project.PureMidiTracks.Count
            : project.Tracks.Count(track => request.IncludedTrackIds.Contains(track.Id))
                + project.PureMidiTracks.Count(track => request.IncludedTrackIds.Contains(track.Id));

    private static CompilationStatistics CreateStatistics(
        int selectedTrackCount,
        IReadOnlyCollection<RawInstance> instances,
        long eventCount,
        long noteOnEventCount,
        AllocationResult allocation,
        PureMidiPlan? pureMidiPlan = null) => new(
            selectedTrackCount,
            instances.Count,
            eventCount,
            allocation.PeakUnits)
        {
            ExpandedSegmentCount = instances.Select(instance => instance.SegmentId).Distinct().Count(),
            ParticipatingEventInstrumentCount = instances
                .Select(instance => instance.InstrumentId)
                .Distinct()
                .Count(),
            ParticipatingSubVoiceCount = instances
                .SelectMany(instance => instance.Voices.Select(voice => (instance.InstrumentId, voice.SubVoiceId)))
                .Distinct()
                .Count(),
            UsedPortCount = allocation.Allocations
                .Select(value => value.ZeroBasedPort)
                .Distinct()
                .Count(),
            NoteOnEventCount = noteOnEventCount,
            ResourceShortage = allocation.ResourceShortage,
            ReservedMidiRootUnitCount = allocation.ReservedRootUnitCount,
            AllocatedMidiRootUnitCount = allocation.AllocatedRootUnitCount,
            LogicalPeakChannelUnitCount = allocation.LogicalPeakUnits
        };

    private static long CountNoteOnEvents(ReadOnlySpan<CanonicalMidiEvent> events)
    {
        long count = 0;
        foreach (CanonicalMidiEvent value in events)
        {
            if (value.Message.MessageType == MidiMessageType.NoteOn && value.Message.Byte2 != 0)
            {
                count++;
            }
        }
        return count;
    }

    private static void AppendDebugDiagnostics(
        CompilationRequest request,
        long endTick,
        List<CompilerDiagnostic> diagnostics,
        CompilationStatistics statistics,
        CompilationFailureStage? failureStage)
    {
        if (!request.CollectDebugDiagnostics)
        {
            return;
        }
        diagnostics.Add(new(
            "MIDORA2900",
            DiagnosticSeverity.Debug,
            $"CompileContext purpose={request.Purpose}; range=[{request.StartTick},{endTick}); "
                + $"tracks={(request.IncludedTrackIds is null ? "all" : request.IncludedTrackIds.Count)}; "
                + $"subVoices={(request.IncludedSubVoiceIds is null ? "all" : request.IncludedSubVoiceIds.Count)}; "
                + $"warningPolicy={(request.TreatWarningsAsErrors ? "fail" : "preserve")}.",
            new(Tick: request.StartTick)));
        diagnostics.Add(new(
            "MIDORA2901",
            DiagnosticSeverity.Debug,
            $"CompileResult outcome={(failureStage.HasValue ? $"partial/{failureStage.Value}" : "success")}; "
                + $"tracks={statistics.SourceTrackCount}; segments={statistics.ExpandedSegmentCount}; "
                + $"instances={statistics.ExpandedInstanceCount}; instruments={statistics.ParticipatingEventInstrumentCount}; "
                + $"subVoices={statistics.ParticipatingSubVoiceCount}; events={statistics.EventCount}; "
                + $"peakUnits={statistics.PeakChannelUnitCount}; ports={statistics.UsedPortCount}.",
            new(Tick: request.StartTick)));
    }

    private void ValidateMappingFunctions(
        MidoraProject project,
        CompilationRequest request,
        List<CompilerDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        HashSet<MidoraId> selectedInstrumentIds = SemanticValidator.GetParticipatingInstrumentIds(project, request);
        foreach (EventInstrument instrument in project.EventInstruments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.IncludedTrackIds is not null
                && !selectedInstrumentIds.Contains(instrument.Id))
            {
                continue;
            }
            HashSet<MidoraId> activeReferences = GetActiveMappingSteps(instrument)
                .Where(step => step.Operation == MappingOperation.CustomCSharp && step.MappingFunctionId.HasValue)
                .Select(step => step.MappingFunctionId!.Value)
                .ToHashSet();
            foreach (CSharpMappingFunction function in instrument.MappingFunctions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? error = _mapping.ValidateFunction(function, cancellationToken);
                if (error is null)
                {
                    continue;
                }
                bool participates = selectedInstrumentIds.Contains(instrument.Id)
                    && activeReferences.Contains(function.Id);
                diagnostics.Add(new(
                    participates ? "MIDORA2103" : "MIDORA2104",
                    participates ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                    error,
                    new(EventInstrumentId: instrument.Id, MappingFunctionId: function.Id)));
            }
        }
    }

    private static IEnumerable<ValueMappingStep> GetActiveMappingSteps(EventInstrument instrument) =>
        instrument.ParameterMappings.SelectMany(value => ActiveSteps(value.Steps))
            .Concat(instrument.SubVoices
                .SelectMany(value => value.EventMappings)
                .SelectMany(value => ActiveSteps(value.Steps)));

    private TrackExpansion ExpandTrackIncrementally(
        MidoraProject project,
        LogicalTrack track,
        IReadOnlyDictionary<MidoraId, EventInstrument> instruments,
        bool incremental,
        ProjectChangeSet changes,
        bool heldPreviewGateOpen,
        long? heldPreviewFinalGateLengthTicks,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long contextFingerprint = SourceFingerprint.ForTrackContext(
            track,
            instruments,
            project,
            cancellationToken);
        if (heldPreviewGateOpen || heldPreviewFinalGateLengthTicks.HasValue)
        {
            contextFingerprint ^= heldPreviewGateOpen
                ? unchecked((long)0x9e3779b97f4a7c15UL)
                : heldPreviewFinalGateLengthTicks.GetValueOrDefault();
            incremental = false;
        }
        CurrentSegment[] currentSegments = track.Segments
            .OrderBy(value => value.ProjectStartTick)
            .ThenBy(value => value.Id)
            .Select(value => new CurrentSegment(
                value,
                SourceFingerprint.ForSegment(value, cancellationToken)))
            .ToArray();
        bool explicitlyDirty = changes.AffectsEverything
            || changes.TrackIds.Contains(track.Id)
            || changes.EventInstrumentUsageIds.Contains(track.EventInstrumentUsageId ?? default)
            || changes.EventInstrumentIds.Any(id => TrackReferences(project, track, id));
        _trackCache.TryGetValue(track.Id, out TrackCacheEntry? cached);
        bool hasCompatibleCache = incremental
            && cached is not null
            && cached.ContextFingerprint == contextFingerprint;
        bool sourceSequenceUnchanged = hasCompatibleCache
            && SegmentSourcesEqual(currentSegments, 0, cached!.Segments, 0);

        if (hasCompatibleCache
            && !explicitlyDirty
            && sourceSequenceUnchanged)
        {
            return CreateTrackExpansion(
                track, instruments, cached!.Segments, false,
                0, cached.Segments.Length, 0, null);
        }

        int reusablePrefixCount = 0;
        if (hasCompatibleCache)
        {
            int limit = Math.Min(currentSegments.Length, cached!.Segments.Length);
            while (reusablePrefixCount < limit
                && SegmentSourceEquals(currentSegments[reusablePrefixCount], cached.Segments[reusablePrefixCount]))
            {
                reusablePrefixCount++;
            }

            if (explicitlyDirty && reusablePrefixCount == currentSegments.Length
                && reusablePrefixCount == cached.Segments.Length)
            {
                reusablePrefixCount = 0;
            }
        }

        List<SegmentCacheEntry> newEntries = [];
        int reusedSegmentCount = 0;
        int recompiledSegmentCount = 0;
        int stateConvergenceCount = 0;
        int nextSourceOrder = 0;
        if (hasCompatibleCache && reusablePrefixCount != 0)
        {
            for (int index = 0; index < reusablePrefixCount; index++)
            {
                newEntries.Add(cached!.Segments[index]);
            }
            reusedSegmentCount = reusablePrefixCount;
            nextSourceOrder = cached!.Segments[reusablePrefixCount - 1].NextSourceOrder;
        }

        long? earliestDirtyTick = FindDirtyTick(currentSegments, cached, reusablePrefixCount);
        int currentIndex = reusablePrefixCount;
        if (hasCompatibleCache
            && (!explicitlyDirty || !sourceSequenceUnchanged)
            && TryReuseUnchangedSuffix(
                currentSegments, currentIndex, cached!.Segments,
                nextSourceOrder, contextFingerprint, newEntries, out int initiallyReused))
        {
            reusedSegmentCount += initiallyReused;
            stateConvergenceCount++;
            currentIndex = currentSegments.Length;
        }

        while (currentIndex < currentSegments.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CurrentSegment current = currentSegments[currentIndex];
            ExpansionCheckpoint entryCheckpoint = ExpansionCheckpoint.Create(
                current.Segment.ProjectStartTick, nextSourceOrder, contextFingerprint);
            List<CompilerDiagnostic> segmentDiagnostics = [];
            RawInstance[] segmentInstances = ExpandSegment(
                project, track, current.Segment, instruments, nextSourceOrder,
                segmentDiagnostics,
                heldPreviewGateOpen,
                heldPreviewFinalGateLengthTicks,
                cancellationToken,
                out nextSourceOrder);
            newEntries.Add(new SegmentCacheEntry(
                current.Segment.Id,
                current.SourceFingerprint,
                entryCheckpoint,
                nextSourceOrder,
                segmentInstances,
                segmentDiagnostics.ToArray()));
            recompiledSegmentCount++;
            currentIndex++;

            if (hasCompatibleCache
                && TryReuseUnchangedSuffix(
                    currentSegments, currentIndex, cached!.Segments,
                    nextSourceOrder, contextFingerprint, newEntries, out int suffixReused))
            {
                reusedSegmentCount += suffixReused;
                stateConvergenceCount++;
                currentIndex = currentSegments.Length;
            }
        }

        long terminalTick = currentSegments.Length == 0
            ? 0
            : checked(currentSegments[^1].Segment.ProjectStartTick + currentSegments[^1].Segment.LengthTicks);
        TrackCacheEntry replacement = new(
            contextFingerprint,
            newEntries.ToArray(),
            ExpansionCheckpoint.Create(terminalTick, nextSourceOrder, contextFingerprint));
        _trackCache[track.Id] = replacement;
        return CreateTrackExpansion(
            track, instruments, replacement.Segments, true,
            recompiledSegmentCount, reusedSegmentCount, stateConvergenceCount, earliestDirtyTick);
    }

    private static TrackExpansion CreateTrackExpansion(
        LogicalTrack track,
        IReadOnlyDictionary<MidoraId, EventInstrument> instruments,
        IReadOnlyList<SegmentCacheEntry> segments,
        bool recompiled,
        int recompiledSegmentCount,
        int reusedSegmentCount,
        int stateConvergenceCount,
        long? earliestDirtyTick)
    {
        RawInstance[] instances = segments.SelectMany(value => value.Instances).ToArray();
        List<CompilerDiagnostic> diagnostics = segments
            .SelectMany(value => value.Diagnostics)
            .ToList();
        return new TrackExpansion(
            instances,
            diagnostics.ToArray(),
            recompiled,
            recompiledSegmentCount,
            reusedSegmentCount,
            stateConvergenceCount,
            earliestDirtyTick);
    }

    private static void AppendEmptySubVoiceDiagnostics(
        IReadOnlyCollection<RawInstance> instances,
        IReadOnlyDictionary<MidoraId, EventInstrument> instruments,
        List<CompilerDiagnostic> diagnostics)
    {
        foreach (IGrouping<(MidoraId TrackId, MidoraId InstrumentId), RawInstance> binding in instances
            .GroupBy(instance => (instance.TrackId, instance.InstrumentId)))
        {
            if (!instruments.TryGetValue(binding.Key.InstrumentId, out EventInstrument? instrument))
            {
                continue;
            }
            HashSet<MidoraId> participatingVoiceIds = binding
                .SelectMany(instance => instance.Voices)
                .Select(voice => voice.SubVoiceId)
                .ToHashSet();
            foreach (SubVoice emptyVoice in instrument.SubVoices.Where(voice =>
                participatingVoiceIds.Contains(voice.Id)
                && voice.Events.Count == 0
                && voice.Curves.Count == 0
                && !instrument.ParameterMappings.Any(mapping =>
                    mapping.SubVoiceId == voice.Id && mapping.Steps.IsEnabled)))
            {
                diagnostics.Add(new(
                    "MIDORA1225",
                    DiagnosticSeverity.Info,
                    "A participating SubVoice has no ordinary MIDI output content.",
                    new(binding.Key.TrackId,
                        EventInstrumentId: instrument.Id,
                        SubVoiceId: emptyVoice.Id)));
            }
        }
    }

    private static bool TryReuseUnchangedSuffix(
        IReadOnlyList<CurrentSegment> current,
        int currentStart,
        IReadOnlyList<SegmentCacheEntry> cached,
        int nextSourceOrder,
        long contextFingerprint,
        List<SegmentCacheEntry> destination,
        out int reusedCount)
    {
        reusedCount = current.Count - currentStart;
        if (reusedCount <= 0 || reusedCount > cached.Count)
        {
            return false;
        }

        int cachedStart = cached.Count - reusedCount;
        if (!SegmentSourcesEqual(current, currentStart, cached, cachedStart))
        {
            return false;
        }

        ExpansionCheckpoint currentCheckpoint = ExpansionCheckpoint.Create(
            current[currentStart].Segment.ProjectStartTick,
            nextSourceOrder,
            contextFingerprint);
        if (!currentCheckpoint.IsEquivalentTo(cached[cachedStart].EntryCheckpoint))
        {
            return false;
        }

        for (int index = cachedStart; index < cached.Count; index++)
        {
            destination.Add(cached[index]);
        }
        return true;
    }

    private static bool SegmentSourcesEqual(
        IReadOnlyList<CurrentSegment> current,
        int currentStart,
        IReadOnlyList<SegmentCacheEntry> cached,
        int cachedStart)
    {
        if (current.Count - currentStart != cached.Count - cachedStart)
        {
            return false;
        }
        for (int offset = 0; offset < current.Count - currentStart; offset++)
        {
            if (!SegmentSourceEquals(current[currentStart + offset], cached[cachedStart + offset]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool SegmentSourceEquals(CurrentSegment current, SegmentCacheEntry cached) =>
        current.Segment.Id == cached.SegmentId
        && current.SourceFingerprint == cached.SourceFingerprint;

    private static long? FindDirtyTick(
        IReadOnlyList<CurrentSegment> current,
        TrackCacheEntry? cached,
        int dirtyIndex)
    {
        long? currentTick = dirtyIndex < current.Count
            ? current[dirtyIndex].Segment.ProjectStartTick
            : null;
        long? cachedTick = cached is not null && dirtyIndex < cached.Segments.Length
            ? cached.Segments[dirtyIndex].EntryCheckpoint.Tick
            : null;
        if (currentTick.HasValue && cachedTick.HasValue)
        {
            return Math.Min(currentTick.Value, cachedTick.Value);
        }
        return currentTick ?? cachedTick ?? (current.Count == 0 && cached is null ? 0 : null);
    }

    private RawInstance[] ExpandSegment(
        MidoraProject project,
        LogicalTrack track,
        Segment requestedSegment,
        IReadOnlyDictionary<MidoraId, EventInstrument> instruments,
        int initialSourceOrder,
        List<CompilerDiagnostic> diagnostics,
        bool heldPreviewGateOpen,
        long? heldPreviewFinalGateLengthTicks,
        CancellationToken cancellationToken,
        out int nextSourceOrder)
    {
        List<RawInstance> result = [];
        if (!TryResolveEventInstrument(
                project,
                track,
                instruments,
                out EventInstrument instrument,
                out _))
        {
            nextSourceOrder = initialSourceOrder;
            return [];
        }
        Dictionary<MidoraId, LogicalParameterDefinition> definitions = instrument.LogicalParameters
            .ToDictionary(value => value.Id);
        Dictionary<MidoraId, CSharpMappingFunction> functions = instrument.MappingFunctions
            .ToDictionary(value => value.Id);
        Dictionary<TemplateEventMappingTarget, SubVoiceEventMapping>[] eventMappingsByVoice =
            instrument.SubVoices
                .Select(voice => voice.EventMappings.ToDictionary(value => value.Target))
                .ToArray();
        int sourceOrder = initialSourceOrder;
        foreach (Segment segment in new[] { requestedSegment })
        {
            Dictionary<MidoraId, LogicalParameterLane> lanes = segment.ParameterLanes
                .Where(value => definitions.ContainsKey(value.ParameterId))
                .ToDictionary(value => value.ParameterId);
            foreach (LogicalNote note in segment.Notes.OrderBy(value => value.StartTick))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (note.StartTick < segment.ContentOffsetTick || note.StartTick >= segment.ContentEndTick)
                {
                    continue;
                }

                long logicalGateStart = checked(
                    segment.ProjectStartTick + (note.StartTick - segment.ContentOffsetTick));
                long instanceStart = checked(logicalGateStart - instrument.PreRollTicks);
                long segmentEnd = checked(segment.ProjectStartTick + segment.LengthTicks);
                long gateEnd = AddDurationClamped(logicalGateStart, note.LengthTicks, segmentEnd);
                Dictionary<MidoraId, double> parameterValues = EvaluateParameters(
                    definitions,
                    lanes,
                    checked(note.StartTick - instrument.PreRollTicks));
                int instanceSourceOrder = sourceOrder++;
                RawInstance instance = ExpandInstance(
                    project, track, segment, note, instrument,
                    instanceStart, logicalGateStart, gateEnd, segmentEnd,
                    parameterValues, definitions, lanes, functions, eventMappingsByVoice,
                    instanceSourceOrder, diagnostics,
                    heldPreviewGateOpen,
                    heldPreviewFinalGateLengthTicks,
                    cancellationToken);
                result.Add(instance);
            }
        }
        nextSourceOrder = sourceOrder;
        return result.ToArray();
    }

    private void ApplyDestructiveOverlapPolicies(
        MidoraProject project,
        List<RawInstance> instances,
        List<CompilerDiagnostic> diagnostics,
        int instanceExpansionDiagnosticStart,
        bool heldPreviewGateOpen,
        long? heldPreviewFinalGateLengthTicks,
        CancellationToken cancellationToken)
    {
        if (instances.Count == 0)
        {
            return;
        }

        Dictionary<MidoraId, int> trackOrder = project.LogicalTracksInArrangementOrder()
            .Select((track, index) => (track.Id, index))
            .ToDictionary(value => value.Id, value => value.index);
        foreach (LogicalTrack track in project.Tracks)
        {
            trackOrder.TryAdd(track.Id, trackOrder.Count);
        }

        int[] deterministicOrder = Enumerable.Range(0, instances.Count)
            .OrderBy(index => instances[index].StartTick)
            .ThenBy(index => trackOrder.GetValueOrDefault(instances[index].TrackId, int.MaxValue))
            .ThenBy(index => instances[index].SourceOrder)
            .ThenBy(index => instances[index].TrackId)
            .ThenBy(index => instances[index].SegmentId)
            .ThenBy(index => instances[index].InstanceId)
            .ToArray();
        Dictionary<(MidoraId UsageId, MidoraId InstrumentId), List<int>> activeByUsage = [];
        bool[] retained = Enumerable.Repeat(true, instances.Count).ToArray();
        HashSet<OverlapInstanceKey> regenerated = [];
        HashSet<OverlapInstanceKey> rejected = [];
        List<CompilerDiagnostic> regeneratedDiagnostics = [];
        List<CompilerDiagnostic> overlapDiagnostics = [];

        foreach (int currentIndex in deterministicOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RawInstance current = instances[currentIndex];
            if (current.OverlapPolicy is not OverlapPolicy.CutPrevious
                and not OverlapPolicy.CutNewRejectNew)
            {
                continue;
            }

            (MidoraId UsageId, MidoraId InstrumentId) usageKey =
                (current.UsageId, current.InstrumentId);
            if (!activeByUsage.TryGetValue(usageKey, out List<int>? active))
            {
                active = [];
                activeByUsage.Add(usageKey, active);
            }
            active.RemoveAll(index => instances[index].EndTick <= current.StartTick);
            int[] conflicts = active.Where(index =>
                    instances[index].EndTick > current.StartTick
                    && (current.OverlapScope == OverlapScope.AnyPitch
                        || instances[index].Pitch == current.Pitch))
                .ToArray();
            if (conflicts.Length == 0)
            {
                active.Add(currentIndex);
                continue;
            }

            if (current.OverlapPolicy == OverlapPolicy.CutNewRejectNew)
            {
                retained[currentIndex] = false;
                rejected.Add(OverlapInstanceKey.For(current));
                overlapDiagnostics.Add(new(
                    "MIDORA2203",
                    DiagnosticSeverity.Warning,
                    "The new instance overlaps an active instance and was not generated under the Cut New / Reject New policy.",
                    new(
                        current.TrackId,
                        current.SegmentId,
                        current.InstanceId,
                        current.InstrumentId,
                        Tick: current.StartTick)));
                continue;
            }

            if (conflicts.Any(index => instances[index].StartTick == current.StartTick))
            {
                overlapDiagnostics.Add(new(
                    "MIDORA2204",
                    DiagnosticSeverity.Error,
                    "Multiple same-tick instances do not have a deterministic truncation order under Cut Previous.",
                    new(
                        current.TrackId,
                        current.SegmentId,
                        current.InstanceId,
                        current.InstrumentId,
                        Tick: current.StartTick)));
                active.Add(currentIndex);
                continue;
            }

            foreach (int previousIndex in conflicts)
            {
                RawInstance previous = instances[previousIndex];
                regenerated.Add(OverlapInstanceKey.For(previous));
                instances[previousIndex] = ReexpandForCutPrevious(
                    project,
                    previous,
                    current.StartTick,
                    regeneratedDiagnostics,
                    heldPreviewGateOpen,
                    heldPreviewFinalGateLengthTicks,
                    cancellationToken);
                active.Remove(previousIndex);
            }
            active.Add(currentIndex);
        }

        if (regenerated.Count != 0 || rejected.Count != 0)
        {
            for (int index = diagnostics.Count - 1;
                index >= instanceExpansionDiagnosticStart;
                index--)
            {
                CompilerDiagnostic diagnostic = diagnostics[index];
                if (diagnostic.Code is not "MIDORA2101" and not "MIDORA2102")
                {
                    continue;
                }
                OverlapInstanceKey key = OverlapInstanceKey.For(diagnostic.Source);
                if (regenerated.Contains(key) || rejected.Contains(key))
                {
                    diagnostics.RemoveAt(index);
                }
            }
        }
        diagnostics.AddRange(regeneratedDiagnostics);
        diagnostics.AddRange(overlapDiagnostics);

        int destination = 0;
        for (int source = 0; source < instances.Count; source++)
        {
            if (!retained[source])
            {
                continue;
            }
            if (destination != source)
            {
                instances[destination] = instances[source];
            }
            destination++;
        }
        if (destination != instances.Count)
        {
            instances.RemoveRange(destination, instances.Count - destination);
        }
    }

    private RawInstance ReexpandForCutPrevious(
        MidoraProject project,
        RawInstance previous,
        long replacementGateEnd,
        List<CompilerDiagnostic> diagnostics,
        bool heldPreviewGateOpen,
        long? heldPreviewFinalGateLengthTicks,
        CancellationToken cancellationToken)
    {
        EventInstrument instrument = previous.SourceInstrument;
        Segment segment = previous.SourceSegment;
        LogicalNote note = previous.SourceNote;
        Dictionary<MidoraId, LogicalParameterDefinition> definitions = instrument.LogicalParameters
            .ToDictionary(value => value.Id);
        Dictionary<MidoraId, CSharpMappingFunction> functions = instrument.MappingFunctions
            .ToDictionary(value => value.Id);
        Dictionary<TemplateEventMappingTarget, SubVoiceEventMapping>[] eventMappingsByVoice =
            instrument.SubVoices
                .Select(voice => voice.EventMappings.ToDictionary(value => value.Target))
                .ToArray();
        Dictionary<MidoraId, LogicalParameterLane> lanes = segment.ParameterLanes
            .Where(value => definitions.ContainsKey(value.ParameterId))
            .ToDictionary(value => value.ParameterId);
        Dictionary<MidoraId, double> parameterValues = EvaluateParameters(
            definitions,
            lanes,
            checked(note.StartTick - instrument.PreRollTicks));
        long logicalGateStart = checked(
            segment.ProjectStartTick + (note.StartTick - segment.ContentOffsetTick));
        long segmentEnd = checked(segment.ProjectStartTick + segment.LengthTicks);
        return ExpandInstance(
            project,
            previous.SourceTrack,
            segment,
            note,
            instrument,
            previous.StartTick,
            logicalGateStart,
            replacementGateEnd,
            segmentEnd,
            parameterValues,
            definitions,
            lanes,
            functions,
            eventMappingsByVoice,
            previous.SourceOrder,
            diagnostics,
            heldPreviewGateOpen,
            heldPreviewFinalGateLengthTicks,
            cancellationToken);
    }

    private static long AddDurationClamped(long startTick, long duration, long endTick) =>
        startTick + Math.Min(duration, endTick - startTick);

    private static long AddDurationsClamped(long left, long right, long maximum)
    {
        if (left >= maximum || right >= maximum - left)
        {
            return maximum;
        }
        return left + right;
    }

    private RawInstance ExpandInstance(
        MidoraProject project,
        LogicalTrack track,
        Segment segment,
        LogicalNote note,
        EventInstrument instrument,
        long instanceStart,
        long logicalGateStart,
        long gateEnd,
        long segmentEnd,
        Dictionary<MidoraId, double> initialParameters,
        IReadOnlyDictionary<MidoraId, LogicalParameterDefinition> definitions,
        IReadOnlyDictionary<MidoraId, LogicalParameterLane> lanes,
        IReadOnlyDictionary<MidoraId, CSharpMappingFunction> functions,
        IReadOnlyList<Dictionary<TemplateEventMappingTarget, SubVoiceEventMapping>> eventMappingsByVoice,
        int sourceOrder,
        List<CompilerDiagnostic> diagnostics,
        bool heldPreviewGateOpen = false,
        long? heldPreviewFinalGateLengthTicks = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long instanceGateEndLocalTick = heldPreviewGateOpen
            ? long.MaxValue
            : gateEnd - instanceStart;
        long logicalGateLength = heldPreviewGateOpen
            ? long.MaxValue
            // Cut Previous may terminate an instance while it is still inside its
            // Pre-Roll prefix, before the Logical Gate Start. Zero is the explicit
            // truncation-only MappingContext sentinel for that case; ordinary source
            // Logical Notes are still required to have a positive Gate Length.
            : Math.Max(0, gateEnd - logicalGateStart);
        long mappingGateLength = heldPreviewGateOpen
            ? long.MaxValue
            : heldPreviewFinalGateLengthTicks ?? logicalGateLength;
        bool shortNote = !heldPreviewGateOpen
            && mappingGateLength < instrument.TemplateLengthTicks;
        bool longNote = heldPreviewGateOpen
            || mappingGateLength > instrument.TemplateLengthTicks;
        // Loop entry follows the template clock reaching Loop End with an open
        // gate, not the short/long classification against Template Length.
        // Pre-Roll belongs to this clock; a short One-Shot still plays once.
        bool looping = instrument.LoopStartTick.HasValue
            && instrument.LoopEndTick is long loopEnd
            && instanceGateEndLocalTick > loopEnd
            && !(shortNote && instrument.ShortLifecycle == ShortNoteLifecycle.OneShot);
        long loopTailLength = looping
            ? instrument.TemplateLengthTicks - instrument.LoopEndTick!.Value
            : 0;
        HashSet<MidoraId> usedEnvelopeIds = GetUsedEnvelopeIds(instrument);
        long release = instrument.Envelopes.Where(value => usedEnvelopeIds.Contains(value.Id))
            .Select(value => value.ReleaseTicks).DefaultIfEmpty().Max();
        bool releaseTriggered = !heldPreviewGateOpen && (shortNote
            ? instrument.ShortLifecycle != ShortNoteLifecycle.OneShot
            : longNote && instrument.LongLifecycle != LongNoteLifecycle.EndAtTemplate);
        long? releaseStartLocalTick = releaseTriggered ? instanceGateEndLocalTick : null;
        long maximumDuration = segmentEnd - instanceStart;
        long naturalDuration;
        if (heldPreviewGateOpen)
        {
            naturalDuration = maximumDuration;
        }
        else if (shortNote)
        {
            naturalDuration = instrument.ShortLifecycle switch
            {
                ShortNoteLifecycle.CutAtNoteOff => AddDurationsClamped(
                    instanceGateEndLocalTick,
                    release,
                    maximumDuration),
                ShortNoteLifecycle.OneShot => Math.Min(
                    instrument.TemplateLengthTicks,
                    maximumDuration),
                ShortNoteLifecycle.Tail => Math.Max(
                    looping
                        ? AddDurationsClamped(instanceGateEndLocalTick, loopTailLength, maximumDuration)
                        : Math.Min(instrument.TemplateLengthTicks, maximumDuration),
                    AddDurationsClamped(instanceGateEndLocalTick, release, maximumDuration)),
                _ => instanceGateEndLocalTick
            };
        }
        else if (!longNote || instrument.LongLifecycle == LongNoteLifecycle.EndAtTemplate)
        {
            naturalDuration = Math.Min(instrument.TemplateLengthTicks, maximumDuration);
        }
        else
        {
            naturalDuration = AddDurationsClamped(
                instanceGateEndLocalTick,
                Math.Max(release, loopTailLength),
                maximumDuration);
        }
        long actualEnd = instanceStart + naturalDuration;
        if (actualEnd < instanceStart)
        {
            actualEnd = instanceStart;
        }

        RawSubVoice[] voices = new RawSubVoice[instrument.SubVoices.Count];
        long sequence = ((long)sourceOrder) << 32;
        for (int voiceIndex = 0; voiceIndex < instrument.SubVoices.Count; voiceIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubVoice voice = instrument.SubVoices[voiceIndex];
            IReadOnlyDictionary<TemplateEventMappingTarget, SubVoiceEventMapping> eventMappings =
                eventMappingsByVoice[voiceIndex];
            SourceReference source = new(
                track.Id,
                segment.Id,
                note.Id,
                instrument.Id,
                voice.Id,
                Tick: instanceStart,
                EventInstrumentUsageId: track.EventInstrumentUsageId ?? default);
            List<RawMidiEvent> events = [];
            MidiInitialState state = MergeState(project.GlobalInitialState, instrument.InitialState, voice.InitialState);
            HashSet<MidiValueTarget> usedTargets = CollectUsedTargets(instrument, voice, state);
            HashSet<MidiValueTarget> tickZeroTargets = GetTickZeroTargets(voice);
            EmitReset(
                events,
                instanceStart,
                usedTargets.Where(static target => target.Kind != MidiValueKind.ControlChange
                    || target.Number != AllSoundOffController),
                project.GlobalResetDefaults,
                source with { Origin = SourceOrigin.ProjectResetDefaults },
                ref sequence);
            EmitInitialState(
                events,
                instanceStart,
                state,
                tickZeroTargets,
                source with { Origin = SourceOrigin.MergedInitialState },
                ref sequence);
            int root = voice.RootNoteOverride ?? instrument.RootNote;
            int pitchDelta = note.Note - root;
            EmitVoiceCurves(
                events,
                instrument,
                voice,
                instanceStart,
                instanceGateEndLocalTick,
                looping,
                actualEnd,
                source,
                ref sequence);
            foreach (TemplateEvent templateEvent in voice.Events.OrderBy(value => value.Tick))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (EventOccurrence occurrence in EnumerateOccurrences(
                    instrument,
                    templateEvent.Tick,
                    instanceGateEndLocalTick,
                    looping,
                    actualEnd - instanceStart))
                {
                    if (occurrence.LocalTick >= actualEnd - instanceStart)
                    {
                        continue;
                    }
                    long tick = instanceStart + occurrence.LocalTick;
                    bool afterGate = tick >= gateEnd;
                    if (tick >= actualEnd
                        || (shortNote && instrument.ShortLifecycle == ShortNoteLifecycle.CutAtNoteOff && afterGate)
                        || (releaseTriggered && afterGate && templateEvent.Kind == TemplateEventKind.Note))
                    {
                        continue;
                    }
                    Dictionary<MidoraId, double> parametersAtTick = EvaluateParameters(
                        definitions,
                        lanes,
                        checked(segment.ContentOffsetTick + (tick - segment.ProjectStartTick)));
                    Dictionary<MidoraId, double> envelopesAtTick = EvaluateEnvelopes(
                        instrument, occurrence.LocalTick, releaseStartLocalTick, usedEnvelopeIds);
                    MappingContextV2 context = new(
                        templateEvent.Value,
                        note.Note,
                        note.Velocity,
                        mappingGateLength,
                        pitchDelta,
                        occurrence.TemplateTick, tick,
                        templateEvent.Kind == TemplateEventKind.Note ? templateEvent.Number : 0,
                        templateEvent.Kind == TemplateEventKind.Note ? templateEvent.Value : 0)
                    {
                        EffectiveRootNote = root,
                        CurrentEventId = ToMappingId(templateEvent.Id),
                        CurrentEventKind = ToMappingEventKind(templateEvent.Kind),
                        SegmentLocalTick = checked(
                            segment.ContentOffsetTick + (tick - segment.ProjectStartTick)),
                        TrackId = ToMappingId(track.Id),
                        SegmentId = ToMappingId(segment.Id),
                        SubVoiceId = ToMappingId(voice.Id),
                        SubVoiceName = voice.Name,
                        SubVoiceIndex = voiceIndex,
                        SubVoiceEffectiveRootNote = root,
                        EventInstrumentId = ToMappingId(instrument.Id),
                        EventInstrumentName = instrument.Name,
                        EventInstrumentRootNote = instrument.RootNote
                    };
                    try
                    {
                        bool sustainAcrossLoop = ShouldSustainNoteAcrossLoop(
                            instrument,
                            templateEvent,
                            looping);
                        EmitTemplateEvent(events, eventMappings, templateEvent, tick, gateEnd, actualEnd,
                            releaseTriggered, pitchDelta, note.Velocity,
                            context, parametersAtTick, envelopesAtTick, functions,
                            source with
                            {
                                SourceEventId = templateEvent.Id,
                                Tick = tick,
                                Origin = SourceOrigin.TemplateEvent
                            },
                            ref sequence,
                            heldPreviewGateOpen,
                            sustainAcrossLoop);
                    }
                    catch (Exception exception) when (exception is MappingException or OverflowException)
                    {
                        diagnostics.Add(new("MIDORA2101", DiagnosticSeverity.Error,
                            exception.Message,
                            AddMappingSource(
                                source with
                                {
                                    SourceEventId = templateEvent.Id,
                                    Tick = tick,
                                    Origin = SourceOrigin.TemplateEvent
                                },
                                exception)));
                    }
                }
            }

            EmitEnvelopeEventMappings(
                events,
                instrument,
                voice,
                voiceIndex,
                segment,
                note,
                state,
                instanceStart,
                gateEnd,
                actualEnd,
                instanceGateEndLocalTick,
                mappingGateLength,
                shortNote,
                looping,
                releaseStartLocalTick,
                usedEnvelopeIds,
                definitions,
                lanes,
                functions,
                source,
                ref sequence,
                diagnostics);
            EmitParameterMappings(events, instrument, segment, instanceStart, actualEnd, note, pitchDelta,
                instanceGateEndLocalTick,
                mappingGateLength,
                looping,
                releaseStartLocalTick,
                usedEnvelopeIds,
                definitions,
                lanes,
                initialParameters,
                functions, source, ref sequence, diagnostics);
            voices[voiceIndex] = new RawSubVoice(
                voice.Id,
                events.ToArray(),
                usedTargets
                    .Where(static target => target.Kind != MidiValueKind.ControlChange
                        || target.Number != AllSoundOffController)
                    .OrderBy(static target => target.Kind)
                    .ThenBy(static target => target.Number)
                    .ToArray(),
                events.Any(static value => value.Kind == RawMessageKind.NoteOn && value.Data2 != 0));
        }

        MidoraId usageId = track.EventInstrumentUsageId ?? track.Id;
        return new RawInstance(
            note.Id, track.Id, segment.Id, instrument.Id, usageId, note.Note, instanceStart, actualEnd, segmentEnd,
            instrument.RequiresChannelIsolation,
            instrument.OverlapPolicy, instrument.OverlapScope,
            sourceOrder, voices,
            track, segment, note, instrument);
    }

    private void EmitTemplateEvent(
        List<RawMidiEvent> output,
        IReadOnlyDictionary<TemplateEventMappingTarget, SubVoiceEventMapping> eventMappings,
        TemplateEvent value,
        long tick,
        long gateEnd,
        long actualEnd,
        bool releaseTriggered,
        int pitchDelta,
        int triggerVelocity,
        MappingContextV2 context,
        IReadOnlyDictionary<MidoraId, double> parameters,
        IReadOnlyDictionary<MidoraId, double> envelopes,
        IReadOnlyDictionary<MidoraId, CSharpMappingFunction> functions,
        SourceReference source,
        ref long sequence,
        bool heldPreviewGateOpen,
        bool sustainAcrossLoop)
    {
        SubVoiceEventMapping? numberMapping = FindEventMapping(
            eventMappings,
            value,
            TemplateEventMappingParameter.Number);
        SubVoiceEventMapping? valueMapping = FindEventMapping(
            eventMappings,
            value,
            TemplateEventMappingParameter.Value);
        SubVoiceEventMapping? secondaryMapping = FindEventMapping(
            eventMappings,
            value,
            TemplateEventMappingParameter.SecondaryValue);
        int number = ApplyMappedInt(value.Number, numberMapping,
            context with { CurrentParameter = MappingTargetParameterV2.Number, TargetOriginalValue = value.Number },
            parameters, envelopes, functions, 0, 127, value.Number, false);
        int eventValue = ApplyMappedInt(value.Value, valueMapping,
            context with { CurrentParameter = MappingTargetParameterV2.Value, TargetOriginalValue = value.Value },
            parameters, envelopes, functions,
            value.Kind switch
            {
                TemplateEventKind.PitchBend => -8192,
                TemplateEventKind.Note => 1,
                _ => 0
            },
            value.Kind is TemplateEventKind.RegisteredParameter or TemplateEventKind.NonRegisteredParameter ? 16383 :
            value.Kind == TemplateEventKind.PitchBend ? 8191 : 127,
            DefaultTemplateValue(value.Kind), true);
        int secondary = ApplyMappedInt(value.SecondaryValue, secondaryMapping,
            context with { CurrentParameter = MappingTargetParameterV2.SecondaryValue, TargetOriginalValue = value.SecondaryValue },
            parameters, envelopes, functions, 0,
            value.Kind == TemplateEventKind.PitchBendRange ? 99 : 127,
            value.Kind == TemplateEventKind.PitchBendRange ? 0 : value.SecondaryValue, true);
        SourceReference numberSource = AddFinalMappingStepSource(source, numberMapping?.Steps);
        SourceReference valueSource = AddFinalMappingStepSource(source, valueMapping?.Steps);
        SourceReference secondarySource = AddFinalMappingStepSource(source, secondaryMapping?.Steps);
        switch (value.Kind)
        {
            case TemplateEventKind.Note:
                number = value.FollowPitchDelta ? checked(number + pitchDelta) : number;
                if (number is < 0 or > 127)
                {
                    throw new MappingException("Transposed Note number is outside 0–127; Note number cannot clamp.")
                    {
                        MappingStepId = numberSource.MappingStepId == default
                            ? null
                            : numberSource.MappingStepId,
                        MappingFunctionId = numberSource.MappingFunctionId == default
                            ? null
                            : numberSource.MappingFunctionId,
                        LogicalParameterId = numberSource.LogicalParameterId == default
                            ? null
                            : numberSource.LogicalParameterId,
                        EnvelopeId = numberSource.EnvelopeId == default
                            ? null
                            : numberSource.EnvelopeId
                    };
                }
                SourceReference noteSource = numberMapping is { Steps.IsEnabled: true }
                    && numberMapping.Steps.Any(step => step.IsEnabled)
                    ? numberSource
                    : valueSource;
                output.Add(RawMidiEvent.NoteOn(tick, number, eventValue, sequence++, noteSource));
                long unclippedNaturalOffTick = value.LengthTicks >= long.MaxValue - tick
                    ? long.MaxValue
                    : tick + value.LengthTicks;
                long naturalOffTick = Math.Min(unclippedNaturalOffTick, actualEnd);
                long offTick = sustainAcrossLoop
                    ? actualEnd
                    : releaseTriggered && naturalOffTick > gateEnd && actualEnd > gateEnd
                        ? actualEnd
                        : Math.Min(naturalOffTick, actualEnd);
                if (!heldPreviewGateOpen
                    || (!sustainAcrossLoop && unclippedNaturalOffTick < actualEnd))
                {
                    output.Add(RawMidiEvent.NoteOff(offTick, number, sequence++, noteSource with { Tick = offTick }));
                }
                break;
            case TemplateEventKind.ControlChange:
                output.Add(RawMidiEvent.Control(tick, number, eventValue, CanonicalEventRole.ControlChange, sequence++, valueSource));
                break;
            case TemplateEventKind.Bank:
                if (value.HasBankMsb)
                {
                    output.Add(RawMidiEvent.Control(tick, 0, eventValue, CanonicalEventRole.Bank, sequence++, valueSource));
                }
                if (value.HasBankLsb)
                {
                    output.Add(RawMidiEvent.Control(tick, 32, secondary, CanonicalEventRole.Bank, sequence++, secondarySource));
                }
                break;
            case TemplateEventKind.Program:
                output.Add(RawMidiEvent.Program(tick, eventValue, sequence++, valueSource));
                break;
            case TemplateEventKind.PitchBend:
                output.Add(RawMidiEvent.PitchBend(tick, eventValue, sequence++, valueSource));
                break;
            case TemplateEventKind.RegisteredParameter:
                EmitParameter(output, tick, true, number, eventValue, valueSource, ref sequence);
                break;
            case TemplateEventKind.NonRegisteredParameter:
                EmitParameter(output, tick, false, number, eventValue, valueSource, ref sequence);
                break;
            case TemplateEventKind.PitchBendRange:
                EmitPitchBendRange(
                    output,
                    tick,
                    eventValue,
                    Math.Clamp(secondary, 0, 99),
                    valueSource,
                    ref sequence);
                break;
        }
    }

    private static bool ShouldSustainNoteAcrossLoop(
        EventInstrument instrument,
        TemplateEvent value,
        bool looping)
    {
        if (value.Kind != TemplateEventKind.Note
            || !looping
            || instrument.LoopStartTick is not long loopStart
            || instrument.LoopEndTick is not long loopEnd
            || value.Tick >= loopStart
            || value.Tick >= loopEnd)
        {
            return false;
        }
        return value.LengthTicks > loopEnd - value.Tick;
    }

    private int ApplyMappedInt(
        int value,
        SubVoiceEventMapping? mapping,
        in MappingContextV2 context,
        IReadOnlyDictionary<MidoraId, double> parameters,
        IReadOnlyDictionary<MidoraId, double> envelopes,
        IReadOnlyDictionary<MidoraId, CSharpMappingFunction> functions,
        int minimum,
        int maximum,
        int targetDefault,
        bool allowClamp)
    {
        MappingChain? steps = mapping?.Steps;
        if (steps is null
            || !steps.IsEnabled
            || steps.Count == 0
            || !steps.Any(item => item.IsEnabled))
        {
            return value;
        }
        double result = _mapping.Apply(
            value, steps, context, parameters, envelopes, functions,
            minimum, maximum, targetDefault, mapping!.TargetSettings.Overflow, allowClamp);
        return MappingEngine.Round(result, mapping.TargetSettings.Rounding);
    }

    private static SubVoiceEventMapping? FindEventMapping(
        IReadOnlyDictionary<TemplateEventMappingTarget, SubVoiceEventMapping> eventMappings,
        TemplateEvent value,
        TemplateEventMappingParameter parameter)
    {
        TemplateEventMappingTarget target = TemplateEventMappingTarget.Create(
            value.Kind,
            value.Number,
            parameter);
        return TemplateEventMappingTarget.IsSupported(target)
            && eventMappings.TryGetValue(target, out SubVoiceEventMapping? mapping)
            ? mapping
            : null;
    }

    private static SourceReference AddFinalMappingStepSource(
        SourceReference source,
        MappingChain? chain)
    {
        ValueMappingStep? step = chain is { IsEnabled: true }
            ? chain.LastOrDefault(value => value.IsEnabled)
            : null;
        return step is null
            ? source
            : source with
            {
                MappingStepId = step.Id,
                MappingFunctionId = step.MappingFunctionId ?? default,
                LogicalParameterId = step.LogicalParameterId ?? source.LogicalParameterId,
                EnvelopeId = step.EnvelopeId ?? default
            };
    }

    private static int DefaultTemplateValue(TemplateEventKind kind) => kind switch
    {
        TemplateEventKind.PitchBendRange => 2,
        _ => 0
    };

    private static IEnumerable<EventOccurrence> EnumerateOccurrences(
        EventInstrument instrument,
        long eventTick,
        long instanceGateEndLocalTick,
        bool looping,
        long maximumLocalTick)
    {
        if (!looping || !instrument.LoopStartTick.HasValue || !instrument.LoopEndTick.HasValue)
        {
            if (eventTick < maximumLocalTick)
            {
                yield return new(eventTick, eventTick);
            }
            yield break;
        }
        long loopStart = instrument.LoopStartTick.Value;
        long loopEnd = instrument.LoopEndTick.Value;
        if (eventTick < loopStart)
        {
            if (eventTick < maximumLocalTick)
            {
                yield return new(eventTick, eventTick);
            }
            yield break;
        }
        if (eventTick >= loopEnd)
        {
            long occurrence = AddDurationsClamped(
                instanceGateEndLocalTick,
                eventTick - loopEnd,
                long.MaxValue);
            if (occurrence < maximumLocalTick)
            {
                yield return new(occurrence, eventTick);
            }
            yield break;
        }
        long loopLength = loopEnd - loopStart;
        long relative = eventTick - loopStart;
        for (long iterationStart = loopStart;
            iterationStart < instanceGateEndLocalTick && iterationStart < maximumLocalTick;)
        {
            long remaining = instanceGateEndLocalTick - iterationStart;
            if (relative >= remaining)
            {
                yield break;
            }
            yield return new(iterationStart + relative, eventTick);
            if (loopLength >= remaining)
            {
                yield break;
            }
            iterationStart += loopLength;
        }
    }

    private static void EmitInitialState(
        List<RawMidiEvent> output,
        long tick,
        MidiInitialState state,
        IReadOnlySet<MidiValueTarget> suppressedTargets,
        SourceReference source,
        ref long sequence)
    {
        if (!suppressedTargets.Contains(MidiValueTarget.BankMsb))
        {
            output.Add(RawMidiEvent.Control(tick, 0, state.BankMsb ?? 0, CanonicalEventRole.InitialState, sequence++, source));
        }
        if (!suppressedTargets.Contains(MidiValueTarget.BankLsb))
        {
            output.Add(RawMidiEvent.Control(tick, 32, state.BankLsb ?? 0, CanonicalEventRole.InitialState, sequence++, source));
        }
        if (!suppressedTargets.Contains(MidiValueTarget.Program))
        {
            output.Add(RawMidiEvent.Program(tick, state.Program ?? 0, sequence++, source, CanonicalEventRole.InitialState));
        }
        bool suppressSemitones = suppressedTargets.Contains(MidiValueTarget.PitchBendRangeSemitones);
        bool suppressCents = suppressedTargets.Contains(MidiValueTarget.PitchBendRangeCents);
        if (!suppressSemitones && !suppressCents)
        {
            EmitPitchBendRange(output, tick, state.PitchBendRangeSemitones ?? 2, state.PitchBendRangeCents ?? 0, source, ref sequence, CanonicalEventRole.InitialState);
        }
        else
        {
            if (!suppressSemitones)
            {
                EmitPitchBendRangeComponent(output, tick, true, state.PitchBendRangeSemitones ?? 2,
                    source, ref sequence, CanonicalEventRole.InitialState);
            }
            if (!suppressCents)
            {
                EmitPitchBendRangeComponent(output, tick, false, state.PitchBendRangeCents ?? 0,
                    source, ref sequence, CanonicalEventRole.InitialState);
            }
        }
        foreach ((int controller, int value) in state.Controllers.OrderBy(value => value.Key))
        {
            if (!suppressedTargets.Contains(MidiValueTarget.ControlChange(controller)))
            {
                output.Add(RawMidiEvent.Control(tick, controller, value, CanonicalEventRole.InitialState, sequence++, source));
            }
        }
        foreach ((int parameter, int value) in state.RegisteredParameters.OrderBy(value => value.Key))
        {
            if (!suppressedTargets.Contains(MidiValueTarget.Rpn(parameter)))
            {
                EmitParameter(output, tick, true, parameter, value, source, ref sequence, CanonicalEventRole.InitialState);
            }
        }
        foreach ((int parameter, int value) in state.NonRegisteredParameters.OrderBy(value => value.Key))
        {
            if (!suppressedTargets.Contains(MidiValueTarget.Nrpn(parameter)))
            {
                EmitParameter(output, tick, false, parameter, value, source, ref sequence, CanonicalEventRole.InitialState);
            }
        }
        if (!suppressedTargets.Contains(MidiValueTarget.PitchBend))
        {
            output.Add(RawMidiEvent.PitchBend(tick, state.PitchBend ?? 0, sequence++, source, CanonicalEventRole.InitialState));
        }
    }

    private static MidiInitialState MergeState(params MidiInitialState[] states)
    {
        MidiInitialState result = new();
        foreach (MidiInitialState state in states)
        {
            result.BankMsb = state.BankMsb ?? result.BankMsb;
            result.BankLsb = state.BankLsb ?? result.BankLsb;
            result.Program = state.Program ?? result.Program;
            result.PitchBend = state.PitchBend ?? result.PitchBend;
            result.PitchBendRangeSemitones = state.PitchBendRangeSemitones ?? result.PitchBendRangeSemitones;
            result.PitchBendRangeCents = state.PitchBendRangeCents ?? result.PitchBendRangeCents;
            foreach ((int controller, int value) in state.Controllers)
            {
                result.Controllers[controller] = value;
            }
            foreach ((int parameter, int value) in state.RegisteredParameters)
            {
                result.RegisteredParameters[parameter] = value;
            }
            foreach ((int parameter, int value) in state.NonRegisteredParameters)
            {
                result.NonRegisteredParameters[parameter] = value;
            }
        }
        return result;
    }

    private static void EmitVoiceCurves(
        List<RawMidiEvent> output,
        EventInstrument instrument,
        SubVoice voice,
        long projectStart,
        long instanceGateEndLocalTick,
        bool looping,
        long actualEnd,
        SourceReference source,
        ref long sequence)
    {
        foreach (ValueCurve curve in voice.Curves.OrderBy(value => value.Target.Kind).ThenBy(value => value.Target.Number))
        {
            CurvePoint[] points = curve.Points.OrderBy(value => value.Tick).ToArray();
            if (points.Length == 0)
            {
                continue;
            }
            int? previousOutputValue = null;
            for (long localTick = 0; projectStart + localTick < actualEnd; localTick++)
            {
                long templateTick = MapInstanceTickToTemplate(
                    instrument,
                    localTick,
                    instanceGateEndLocalTick,
                    looping);
                if (templateTick < points[0].Tick || templateTick >= instrument.TemplateLengthTicks)
                {
                    continue;
                }
                double value = EvaluateCurve(points, templateTick, 0);
                int normalized = NormalizeTargetValue(curve.Target, value, curve.TargetSettings);
                if (previousOutputValue == normalized)
                {
                    continue;
                }
                previousOutputValue = normalized;
                EmitTarget(output, projectStart + localTick, curve.Target, normalized,
                    source with
                    {
                        Tick = projectStart + localTick,
                        ValueCurveId = curve.Id,
                        Origin = SourceOrigin.ValueCurve
                    },
                    ref sequence);
            }
        }
    }

    private void EmitEnvelopeEventMappings(
        List<RawMidiEvent> output,
        EventInstrument instrument,
        SubVoice voice,
        int voiceIndex,
        Segment segment,
        LogicalNote note,
        MidiInitialState initialState,
        long projectStart,
        long gateEnd,
        long actualEnd,
        long instanceGateEndLocalTick,
        long mappingGateLength,
        bool shortNote,
        bool looping,
        long? releaseStartLocalTick,
        IReadOnlySet<MidoraId> usedEnvelopeIds,
        IReadOnlyDictionary<MidoraId, LogicalParameterDefinition> definitions,
        IReadOnlyDictionary<MidoraId, LogicalParameterLane> lanes,
        IReadOnlyDictionary<MidoraId, CSharpMappingFunction> functions,
        SourceReference source,
        ref long sequence,
        List<CompilerDiagnostic> diagnostics)
    {
        int root = voice.RootNoteOverride ?? instrument.RootNote;
        int pitchDelta = note.Note - root;
        foreach (SubVoiceEventMapping mapping in voice.EventMappings
            .Where(static value => value.Steps.IsEnabled
                && value.Steps.Any(step => step.IsEnabled
                    && step.Source == MappingSource.Envelope))
            .OrderBy(static value => value.Target.EventKind)
            .ThenBy(static value => value.Target.EventNumber)
            .ThenBy(static value => value.Target.Parameter))
        {
            if (!TryGetStatefulEventMappingTarget(mapping.Target, out MidiValueTarget target))
            {
                continue;
            }

            List<EventMappingStatePoint> rawState = [];
            int eventOrder = 0;
            foreach (TemplateEvent templateEvent in voice.Events)
            {
                int currentEventOrder = eventOrder++;
                if (!TemplateEventMappingTarget.Enumerate(templateEvent).Contains(mapping.Target))
                {
                    continue;
                }
                foreach (EventOccurrence occurrence in EnumerateOccurrences(
                    instrument,
                    templateEvent.Tick,
                    instanceGateEndLocalTick,
                    looping,
                    actualEnd - projectStart))
                {
                    if (occurrence.LocalTick >= actualEnd - projectStart)
                    {
                        continue;
                    }
                    long tick = projectStart + occurrence.LocalTick;
                    if (shortNote
                        && instrument.ShortLifecycle == ShortNoteLifecycle.CutAtNoteOff
                        && tick >= gateEnd)
                    {
                        continue;
                    }
                    rawState.Add(new(
                        occurrence.LocalTick,
                        GetOriginalEventMappingValue(mapping.Target, templateEvent),
                        templateEvent.Id,
                        currentEventOrder));
                }
            }
            rawState.Sort(static (left, right) =>
            {
                int byTick = left.LocalTick.CompareTo(right.LocalTick);
                return byTick != 0 ? byTick : left.EventOrder.CompareTo(right.EventOrder);
            });

            double currentRawValue = GetInitialTargetValue(initialState, target);
            MidoraId currentEventId = default;
            int rawStateIndex = 0;
            int? previousOutputValue = null;
            ValueMappingStep? outputStep = mapping.Steps.LastOrDefault(static step => step.IsEnabled);
            for (long tick = projectStart; tick < actualEnd; tick++)
            {
                long localTick = tick - projectStart;
                while (rawStateIndex < rawState.Count
                    && rawState[rawStateIndex].LocalTick <= localTick)
                {
                    EventMappingStatePoint point = rawState[rawStateIndex++];
                    currentRawValue = point.Value;
                    currentEventId = point.SourceEventId;
                }
                long contentTick = checked(
                    segment.ContentOffsetTick + (tick - segment.ProjectStartTick));
                IReadOnlyDictionary<MidoraId, double> parameters = EvaluateParameters(
                    definitions,
                    lanes,
                    contentTick);
                IReadOnlyDictionary<MidoraId, double> envelopes = EvaluateEnvelopes(
                    instrument,
                    localTick,
                    releaseStartLocalTick,
                    usedEnvelopeIds);
                long templateTick = MapInstanceTickToTemplate(
                    instrument,
                    localTick,
                    instanceGateEndLocalTick,
                    looping);
                MappingContextV2 context = new(
                    currentRawValue,
                    note.Note,
                    note.Velocity,
                    mappingGateLength,
                    pitchDelta,
                    templateTick,
                    tick,
                    mapping.Target.EventNumber,
                    MappingEngine.Round(currentRawValue, MappingRounding.Round))
                {
                    EffectiveRootNote = root,
                    CurrentParameter = mapping.Target.Parameter switch
                    {
                        TemplateEventMappingParameter.SecondaryValue =>
                            MappingTargetParameterV2.SecondaryValue,
                        _ => MappingTargetParameterV2.Value
                    },
                    CurrentEventId = currentEventId == default
                        ? default
                        : ToMappingId(currentEventId),
                    CurrentEventKind = ToMappingEventKind(mapping.Target.EventKind),
                    TargetOriginalValue = currentRawValue,
                    SegmentLocalTick = contentTick,
                    TrackId = ToMappingId(source.TrackId),
                    SegmentId = ToMappingId(source.SegmentId),
                    SubVoiceId = ToMappingId(voice.Id),
                    SubVoiceName = voice.Name,
                    SubVoiceIndex = voiceIndex,
                    SubVoiceEffectiveRootNote = root,
                    EventInstrumentId = ToMappingId(instrument.Id),
                    EventInstrumentName = instrument.Name,
                    EventInstrumentRootNote = instrument.RootNote
                };
                try
                {
                    double mapped = _mapping.Apply(
                        currentRawValue,
                        mapping.Steps,
                        context,
                        parameters,
                        envelopes,
                        functions,
                        TargetMinimum(target),
                        TargetMaximum(target),
                        DefaultTargetValue(target),
                        mapping.TargetSettings.Overflow,
                        allowClamp: true);
                    int normalized = NormalizeTargetValue(target, mapped, mapping.TargetSettings);
                    if (previousOutputValue == normalized)
                    {
                        continue;
                    }
                    previousOutputValue = normalized;
                    EmitTarget(output, tick, target, normalized, source with
                    {
                        Tick = tick,
                        SourceEventId = currentEventId,
                        MappingStepId = outputStep?.Id ?? default,
                        MappingFunctionId = outputStep?.MappingFunctionId ?? default,
                        LogicalParameterId = outputStep?.LogicalParameterId ?? default,
                        EnvelopeId = outputStep?.EnvelopeId ?? default,
                        Origin = currentEventId == default
                            ? SourceOrigin.MergedInitialState
                            : SourceOrigin.TemplateEvent
                    }, ref sequence);
                }
                catch (Exception exception) when (exception is MappingException or OverflowException)
                {
                    SourceReference failureSource = source with
                    {
                        Tick = tick,
                        SourceEventId = currentEventId,
                        Origin = SourceOrigin.TemplateEvent
                    };
                    diagnostics.Add(new(
                        "MIDORA2101",
                        DiagnosticSeverity.Error,
                        exception.Message,
                        AddMappingSource(failureSource, exception)));
                    break;
                }
            }
        }
    }

    private void EmitParameterMappings(
        List<RawMidiEvent> output,
        EventInstrument instrument,
        Segment segment,
        long projectStart,
        long actualEnd,
        LogicalNote note,
        int pitchDelta,
        long instanceGateEndLocalTick,
        long mappingGateLength,
        bool looping,
        long? releaseStartLocalTick,
        IReadOnlySet<MidoraId> usedEnvelopeIds,
        IReadOnlyDictionary<MidoraId, LogicalParameterDefinition> definitions,
        IReadOnlyDictionary<MidoraId, LogicalParameterLane> lanes,
        IReadOnlyDictionary<MidoraId, double> initialParameters,
        IReadOnlyDictionary<MidoraId, CSharpMappingFunction> functions,
        SourceReference source,
        ref long sequence,
        List<CompilerDiagnostic> diagnostics)
    {
        _ = initialParameters;
        int targetVoiceIndex = instrument.SubVoices.FindIndex(voice => voice.Id == source.SubVoiceId);
        SubVoice targetVoice = instrument.SubVoices[targetVoiceIndex];
        int targetVoiceRoot = targetVoice.RootNoteOverride ?? instrument.RootNote;
        foreach (IGrouping<MidiValueTarget, LogicalParameterMapping> group in instrument.ParameterMappings
            .Where(value => value.Steps.IsEnabled && value.SubVoiceId == source.SubVoiceId)
            .GroupBy(value => value.Target)
            .OrderBy(value => value.Key.Kind).ThenBy(value => value.Key.Number))
        {
            LogicalParameterMapping[] mappings = group.ToArray();
            MidiIntegerTargetSettings targetSettings = mappings[0].TargetSettings;
            double baseValue = DefaultTargetValue(group.Key);
            TargetStatePoint[] rawState = BuildTargetStateTimeline(output, group.Key);
            int rawStateIndex = 0;
            double currentRawValue = baseValue;
            int? previousOutputValue = null;
            LogicalParameterMapping? currentMapping = null;
            for (long tick = projectStart; tick < actualEnd; tick++)
            {
                while (rawStateIndex < rawState.Length && rawState[rawStateIndex].Tick <= tick)
                {
                    currentRawValue = rawState[rawStateIndex++].Value;
                }
                long contentTick = checked(
                    segment.ContentOffsetTick + (tick - segment.ProjectStartTick));
                Dictionary<MidoraId, double> parameters = EvaluateParameters(definitions, lanes, contentTick);
                Dictionary<MidoraId, double> envelopes = EvaluateEnvelopes(
                    instrument, tick - projectStart, releaseStartLocalTick, usedEnvelopeIds);
                double current = currentRawValue;
                currentMapping = null;
                try
                {
                    foreach (LogicalParameterMapping mapping in mappings)
                    {
                        currentMapping = mapping;
                        double logical = parameters[mapping.ParameterId];
                        long instanceTick = tick - projectStart;
                        long templateTick = MapInstanceTickToTemplate(
                            instrument,
                            instanceTick,
                            instanceGateEndLocalTick,
                            looping);
                        MappingContextV2 context = new(
                            current,
                            note.Note,
                            note.Velocity,
                            mappingGateLength,
                            pitchDelta,
                            templateTick,
                            tick,
                            0,
                            0)
                        {
                            EffectiveRootNote = targetVoiceRoot,
                            CurrentParameter = MappingTargetParameterV2.LogicalParameterOutput,
                            CurrentEventKind = ToMappingEventKind(group.Key.Kind),
                            LogicalParameterId = ToMappingId(mapping.ParameterId),
                            LogicalParameterName = definitions[mapping.ParameterId].Name,
                            LogicalParameterValue = logical,
                            TargetOriginalValue = currentRawValue,
                            SegmentLocalTick = contentTick,
                            TrackId = ToMappingId(source.TrackId),
                            SegmentId = ToMappingId(source.SegmentId),
                            SubVoiceId = ToMappingId(source.SubVoiceId),
                            SubVoiceName = targetVoice.Name,
                            SubVoiceIndex = targetVoiceIndex,
                            SubVoiceEffectiveRootNote = targetVoiceRoot,
                            EventInstrumentId = ToMappingId(instrument.Id),
                            EventInstrumentName = instrument.Name,
                            EventInstrumentRootNote = instrument.RootNote
                        };
                        IReadOnlyList<ValueMappingStep> steps = mapping.Steps;
                        current = steps.Count == 0 || !steps.Any(item => item.IsEnabled)
                            ? logical
                            : _mapping.Apply(current, steps, context, parameters, envelopes, functions,
                                TargetMinimum(group.Key), TargetMaximum(group.Key),
                                DefaultTargetValue(group.Key), mapping.TargetSettings.Overflow, true);
                    }
                    int normalized = NormalizeTargetValue(group.Key, current, targetSettings);
                    if (previousOutputValue != normalized)
                    {
                        previousOutputValue = normalized;
                        LogicalParameterMapping outputMapping = mappings[^1];
                        ValueMappingStep? outputStep = outputMapping.Steps
                            .LastOrDefault(step => step.IsEnabled);
                        EmitTarget(output, tick, group.Key, normalized, source with
                        {
                            Tick = tick,
                            LogicalParameterId = outputMapping.ParameterId,
                            LogicalParameterMappingId = outputMapping.Id,
                            MappingStepId = outputStep?.Id ?? default,
                            MappingFunctionId = outputStep?.MappingFunctionId ?? default,
                            Origin = SourceOrigin.LogicalParameterMapping
                        }, ref sequence,
                            CanonicalEventRole.LogicalParameter);
                    }
                }
                catch (Exception exception) when (exception is MappingException or OverflowException)
                {
                    SourceReference failureSource = source with
                    {
                        Tick = tick,
                        LogicalParameterId = currentMapping?.ParameterId ?? default,
                        LogicalParameterMappingId = currentMapping?.Id ?? default,
                        Origin = SourceOrigin.LogicalParameterMapping
                    };
                    diagnostics.Add(new(
                        "MIDORA2102",
                        DiagnosticSeverity.Error,
                        exception.Message,
                        AddMappingSource(failureSource, exception)));
                    break;
                }
            }
        }
    }

    private static TargetStatePoint[] BuildTargetStateTimeline(
        IReadOnlyList<RawMidiEvent> events,
        MidiValueTarget target)
    {
        long targetKey = SemanticTargetForTarget(target);
        return events.Where(value => value.SemanticTargetKey == targetKey)
            .GroupBy(value => value.SemanticGroup)
            .Select(group => new
            {
                Tick = group.First().Tick,
                Role = group.Max(value => value.Role),
                Sequence = group.Max(value => value.Sequence),
                Value = DecodeTargetGroup(target, group)
            })
            .OrderBy(value => value.Tick)
            .ThenBy(value => value.Role)
            .ThenBy(value => value.Sequence)
            .Select(value => new TargetStatePoint(value.Tick, value.Value))
            .ToArray();
    }

    private static SourceReference AddMappingSource(
        SourceReference source,
        Exception exception) => exception is MappingException mapping
            ? source with
            {
                MappingStepId = mapping.MappingStepId ?? source.MappingStepId,
                MappingFunctionId = mapping.MappingFunctionId ?? source.MappingFunctionId,
                LogicalParameterId = mapping.LogicalParameterId ?? source.LogicalParameterId,
                EnvelopeId = mapping.EnvelopeId ?? source.EnvelopeId
            }
            : source;

    private static double DecodeTargetGroup(MidiValueTarget target, IEnumerable<RawMidiEvent> group) =>
        target.Kind switch
        {
            MidiValueKind.ControlChange or MidiValueKind.BankMsb or MidiValueKind.BankLsb =>
                group.Last(value => value.Kind == RawMessageKind.ControlChange).Data2,
            MidiValueKind.Program => group.Last(value => value.Kind == RawMessageKind.ProgramChange).Data1,
            MidiValueKind.PitchBend => group.Last(value => value.Kind == RawMessageKind.PitchBend).Data1,
            MidiValueKind.RegisteredParameter or MidiValueKind.NonRegisteredParameter =>
                (group.Last(value => value.Data1 == 6).Data2 << 7)
                | group.Last(value => value.Data1 == 38).Data2,
            MidiValueKind.PitchBendRangeSemitones => group.Last(value => value.Data1 == 6).Data2,
            MidiValueKind.PitchBendRangeCents => group.Last(value => value.Data1 == 38).Data2,
            _ => throw new InvalidOperationException($"Unsupported target {target}.")
        };

    private static HashSet<MidoraId> GetUsedEnvelopeIds(EventInstrument instrument)
    {
        HashSet<MidoraId> result = [];
        IEnumerable<ValueMappingStep> steps = instrument.ParameterMappings
            .Where(value => value.Steps.IsEnabled)
            .SelectMany(value => value.Steps.Where(step => step.IsEnabled))
            .Concat(instrument.SubVoices
                .SelectMany(value => value.EventMappings)
                .SelectMany(value => ActiveSteps(value.Steps)));
        foreach (ValueMappingStep step in steps)
        {
            if (step.Source == MappingSource.Envelope && step.EnvelopeId.HasValue)
            {
                result.Add(step.EnvelopeId.Value);
            }
        }
        return result;
    }

    private static IEnumerable<ValueMappingStep> ActiveSteps(MappingChain chain) =>
        chain.IsEnabled ? chain.Where(value => value.IsEnabled) : [];

    private static Dictionary<MidoraId, double> EvaluateEnvelopes(
        EventInstrument instrument,
        long localTick,
        long? releaseStartLocalTick,
        IReadOnlySet<MidoraId> usedEnvelopeIds)
    {
        Dictionary<MidoraId, double> result = new(usedEnvelopeIds.Count);
        foreach (InstrumentEnvelope envelope in instrument.Envelopes)
        {
            if (!usedEnvelopeIds.Contains(envelope.Id))
            {
                continue;
            }
            double value = !releaseStartLocalTick.HasValue || localTick < releaseStartLocalTick.Value
                ? EnvelopePreReleaseValue(envelope, localTick)
                : InterpolateRelease(
                    EnvelopePreReleaseValue(envelope, releaseStartLocalTick.Value),
                    envelope.EndValue,
                    localTick - releaseStartLocalTick.Value,
                    envelope.ReleaseTicks);
            result.Add(envelope.Id, value);
        }
        return result;
    }

    private static double EnvelopePreReleaseValue(InstrumentEnvelope envelope, long tick)
    {
        if (tick < envelope.DelayTicks)
        {
            return envelope.StartValue;
        }
        tick -= envelope.DelayTicks;
        if (tick < envelope.AttackTicks)
        {
            return Interpolate(envelope.StartValue, envelope.PeakValue, tick, envelope.AttackTicks);
        }
        tick -= envelope.AttackTicks;
        if (tick < envelope.HoldTicks)
        {
            return envelope.PeakValue;
        }
        tick -= envelope.HoldTicks;
        if (tick < envelope.DecayTicks)
        {
            return Interpolate(envelope.PeakValue, envelope.SustainValue, tick, envelope.DecayTicks);
        }
        return envelope.SustainValue;
    }

    private static double Interpolate(double start, double end, long elapsed, long duration) =>
        duration <= 0 ? end : start + ((end - start) * Math.Clamp(elapsed / (double)duration, 0, 1));

    private static double InterpolateRelease(double start, double end, long elapsed, long duration) =>
        duration <= 1
            ? end
            : Interpolate(start, end, elapsed, duration - 1);

    private static long MapInstanceTickToTemplate(
        EventInstrument instrument,
        long localTick,
        long instanceGateEndLocalTick,
        bool looping)
    {
        if (!looping
            || !instrument.LoopStartTick.HasValue
            || !instrument.LoopEndTick.HasValue
            || localTick < instrument.LoopStartTick.Value)
        {
            return localTick;
        }
        if (localTick >= instanceGateEndLocalTick)
        {
            return checked(instrument.LoopEndTick.Value + (localTick - instanceGateEndLocalTick));
        }
        long loopLength = instrument.LoopEndTick.Value - instrument.LoopStartTick.Value;
        return instrument.LoopStartTick.Value + ((localTick - instrument.LoopStartTick.Value) % loopLength);
    }

    private static Dictionary<MidoraId, double> EvaluateParameters(
        IReadOnlyDictionary<MidoraId, LogicalParameterDefinition> definitions,
        IReadOnlyDictionary<MidoraId, LogicalParameterLane> lanes,
        long contentTick)
    {
        Dictionary<MidoraId, double> result = new(definitions.Count);
        foreach ((MidoraId id, LogicalParameterDefinition definition) in definitions)
        {
            double value = lanes.TryGetValue(id, out LogicalParameterLane? lane)
                ? EvaluateStepCurve(lane.Points, contentTick, definition.DefaultValue)
                : definition.DefaultValue;
            value = definition.Type switch
            {
                LogicalParameterType.Integer or LogicalParameterType.Enum =>
                    MappingEngine.Round(value, MappingRounding.Round),
                _ => value
            };
            result.Add(id, Math.Clamp(value, definition.Minimum, definition.Maximum));
        }
        return result;
    }

    private static double EvaluateStepCurve(
        IReadOnlyList<CurvePoint> unsorted,
        long tick,
        double defaultValue)
    {
        if (unsorted.Count == 0) return defaultValue;
        CurvePoint? winner = null;
        foreach (CurvePoint point in unsorted)
        {
            if (point.Tick <= tick
                && (winner is null || point.Tick > winner.Tick))
            {
                winner = point;
            }
        }
        return winner?.Value ?? defaultValue;
    }

    private static double EvaluateCurve(IReadOnlyList<CurvePoint> unsorted, long tick, double defaultValue)
    {
        if (unsorted.Count == 0)
        {
            return defaultValue;
        }
        IReadOnlyList<CurvePoint> points = unsorted;
        for (int i = 1; i < unsorted.Count; i++)
        {
            if (unsorted[i].Tick < unsorted[i - 1].Tick)
            {
                points = unsorted.OrderBy(value => value.Tick).ToArray();
                break;
            }
        }
        if (tick < points[0].Tick)
        {
            return defaultValue;
        }
        int low = 0;
        int high = points.Count - 1;
        while (low < high)
        {
            int middle = (low + high + 1) >> 1;
            if (points[middle].Tick <= tick)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }
        CurvePoint left = points[low];
        if (low == points.Count - 1 || left.Interpolation == CurveInterpolation.Step)
        {
            return left.Value;
        }
        CurvePoint right = points[low + 1];
        return Interpolate(left.Value, right.Value, tick - left.Tick, right.Tick - left.Tick);
    }

    private static void EmitTarget(
        List<RawMidiEvent> output,
        long tick,
        MidiValueTarget target,
        int value,
        SourceReference source,
        ref long sequence,
        CanonicalEventRole? roleOverride = null)
    {
        switch (target.Kind)
        {
            case MidiValueKind.ControlChange:
                output.Add(RawMidiEvent.Control(tick, target.Number, value, roleOverride ?? CanonicalEventRole.ControlChange, sequence++, source));
                break;
            case MidiValueKind.BankMsb:
                output.Add(RawMidiEvent.Control(tick, 0, value, roleOverride ?? CanonicalEventRole.Bank, sequence++, source));
                break;
            case MidiValueKind.BankLsb:
                output.Add(RawMidiEvent.Control(tick, 32, value, roleOverride ?? CanonicalEventRole.Bank, sequence++, source));
                break;
            case MidiValueKind.Program:
                output.Add(RawMidiEvent.Program(tick, value, sequence++, source, roleOverride ?? CanonicalEventRole.Program));
                break;
            case MidiValueKind.PitchBend:
                output.Add(RawMidiEvent.PitchBend(tick, value, sequence++, source, roleOverride ?? CanonicalEventRole.PitchBend));
                break;
            case MidiValueKind.RegisteredParameter:
                EmitParameter(output, tick, true, target.Number, value, source, ref sequence, roleOverride ?? CanonicalEventRole.Parameter);
                break;
            case MidiValueKind.NonRegisteredParameter:
                EmitParameter(output, tick, false, target.Number, value, source, ref sequence, roleOverride ?? CanonicalEventRole.Parameter);
                break;
            case MidiValueKind.PitchBendRangeSemitones:
                EmitPitchBendRangeComponent(output, tick, true, value, source, ref sequence, roleOverride ?? CanonicalEventRole.Parameter);
                break;
            case MidiValueKind.PitchBendRangeCents:
                EmitPitchBendRangeComponent(output, tick, false, value, source, ref sequence, roleOverride ?? CanonicalEventRole.Parameter);
                break;
        }
    }

    private static int NormalizeTargetValue(
        MidiValueTarget target,
        double value,
        MidiIntegerTargetSettings settings)
    {
        double minimum = TargetMinimum(target);
        double maximum = TargetMaximum(target);
        if (!double.IsFinite(value))
        {
            throw new MappingException("Target value is NaN or Infinity.");
        }
        if (value < minimum || value > maximum)
        {
            if (settings.Overflow == MappingOverflow.Clamp)
            {
                value = Math.Clamp(value, minimum, maximum);
            }
            else
            {
                throw new MappingException($"Target value {value} is outside [{minimum}, {maximum}].");
            }
        }
        return MappingEngine.Round(value, settings.Rounding);
    }

    private static double DefaultTargetValue(MidiValueTarget target) =>
        MidiValueTargetDefaults.GetDefaultValue(target);

    private static double GetInitialTargetValue(
        MidiInitialState state,
        MidiValueTarget target) => target.Kind switch
        {
            MidiValueKind.ControlChange => state.Controllers.TryGetValue(target.Number, out int value)
                ? value
                : DefaultTargetValue(target),
            MidiValueKind.BankMsb => state.BankMsb ?? DefaultTargetValue(target),
            MidiValueKind.BankLsb => state.BankLsb ?? DefaultTargetValue(target),
            MidiValueKind.Program => state.Program ?? DefaultTargetValue(target),
            MidiValueKind.PitchBend => state.PitchBend ?? DefaultTargetValue(target),
            MidiValueKind.RegisteredParameter => state.RegisteredParameters.TryGetValue(target.Number, out int value)
                ? value
                : DefaultTargetValue(target),
            MidiValueKind.NonRegisteredParameter => state.NonRegisteredParameters.TryGetValue(target.Number, out int value)
                ? value
                : DefaultTargetValue(target),
            MidiValueKind.PitchBendRangeSemitones =>
                state.PitchBendRangeSemitones ?? DefaultTargetValue(target),
            MidiValueKind.PitchBendRangeCents =>
                state.PitchBendRangeCents ?? DefaultTargetValue(target),
            _ => DefaultTargetValue(target)
        };

    private static bool TryGetStatefulEventMappingTarget(
        TemplateEventMappingTarget mappingTarget,
        out MidiValueTarget target)
    {
        switch (mappingTarget)
        {
            case
            {
                EventKind: TemplateEventKind.ControlChange,
                Parameter: TemplateEventMappingParameter.Value
            }:
                target = MidiValueTarget.ControlChange(mappingTarget.EventNumber);
                return true;
            case
            {
                EventKind: TemplateEventKind.Bank,
                Parameter: TemplateEventMappingParameter.Value
            }:
                target = MidiValueTarget.BankMsb;
                return true;
            case
            {
                EventKind: TemplateEventKind.Bank,
                Parameter: TemplateEventMappingParameter.SecondaryValue
            }:
                target = MidiValueTarget.BankLsb;
                return true;
            case
            {
                EventKind: TemplateEventKind.Program,
                Parameter: TemplateEventMappingParameter.Value
            }:
                target = MidiValueTarget.Program;
                return true;
            case
            {
                EventKind: TemplateEventKind.PitchBend,
                Parameter: TemplateEventMappingParameter.Value
            }:
                target = MidiValueTarget.PitchBend;
                return true;
            case
            {
                EventKind: TemplateEventKind.RegisteredParameter,
                Parameter: TemplateEventMappingParameter.Value
            }:
                target = MidiValueTarget.Rpn(mappingTarget.EventNumber);
                return true;
            case
            {
                EventKind: TemplateEventKind.NonRegisteredParameter,
                Parameter: TemplateEventMappingParameter.Value
            }:
                target = MidiValueTarget.Nrpn(mappingTarget.EventNumber);
                return true;
            case
            {
                EventKind: TemplateEventKind.PitchBendRange,
                Parameter: TemplateEventMappingParameter.Value
            }:
                target = MidiValueTarget.PitchBendRangeSemitones;
                return true;
            case
            {
                EventKind: TemplateEventKind.PitchBendRange,
                Parameter: TemplateEventMappingParameter.SecondaryValue
            }:
                target = MidiValueTarget.PitchBendRangeCents;
                return true;
            default:
                target = default;
                return false;
        }
    }

    private static int GetOriginalEventMappingValue(
        TemplateEventMappingTarget mappingTarget,
        TemplateEvent value) => mappingTarget.Parameter switch
        {
            TemplateEventMappingParameter.Value => value.Value,
            TemplateEventMappingParameter.SecondaryValue => value.SecondaryValue,
            TemplateEventMappingParameter.Number => value.Number,
            _ => throw new ArgumentOutOfRangeException(nameof(mappingTarget))
        };

    private static double TargetMinimum(MidiValueTarget target) => target.Kind == MidiValueKind.PitchBend ? -8192 : 0;
    private static double TargetMaximum(MidiValueTarget target) => target.Kind switch
    {
        MidiValueKind.PitchBend => 8191,
        MidiValueKind.RegisteredParameter or MidiValueKind.NonRegisteredParameter => 16383,
        MidiValueKind.PitchBendRangeCents => 99,
        _ => 127
    };

    private static void EmitParameter(
        List<RawMidiEvent> output,
        long tick,
        bool registered,
        int parameter,
        int value,
        SourceReference source,
        ref long sequence,
        CanonicalEventRole role = CanonicalEventRole.Parameter)
    {
        long group = sequence;
        long targetKey = (registered ? RpnTargetKeyBase : NrpnTargetKeyBase) + parameter;
        output.Add(RawMidiEvent.Control(tick, registered ? 101 : 99, (parameter >> 7) & 127, role, sequence++, source, targetKey, group));
        output.Add(RawMidiEvent.Control(tick, registered ? 100 : 98, parameter & 127, role, sequence++, source, targetKey, group));
        output.Add(RawMidiEvent.Control(tick, 6, (value >> 7) & 127, role, sequence++, source, targetKey, group));
        output.Add(RawMidiEvent.Control(tick, 38, value & 127, role, sequence++, source, targetKey, group));
        output.Add(RawMidiEvent.Control(tick, registered ? 101 : 99, 127, role, sequence++, source, targetKey, group));
        output.Add(RawMidiEvent.Control(tick, registered ? 100 : 98, 127, role, sequence++, source, targetKey, group));
    }

    private static void EmitPitchBendRange(
        List<RawMidiEvent> output,
        long tick,
        int semitones,
        int cents,
        SourceReference source,
        ref long sequence,
        CanonicalEventRole role = CanonicalEventRole.Parameter)
    {
        long group = sequence;
        output.Add(RawMidiEvent.Control(tick, 101, 0, role, sequence++, source, PitchBendRangeTargetKey, group));
        output.Add(RawMidiEvent.Control(tick, 100, 0, role, sequence++, source, PitchBendRangeTargetKey, group));
        output.Add(RawMidiEvent.Control(tick, 6, semitones, role, sequence++, source, PitchBendRangeTargetKey, group));
        output.Add(RawMidiEvent.Control(tick, 38, cents, role, sequence++, source, PitchBendRangeTargetKey, group));
        output.Add(RawMidiEvent.Control(tick, 101, 127, role, sequence++, source, PitchBendRangeTargetKey, group));
        output.Add(RawMidiEvent.Control(tick, 100, 127, role, sequence++, source, PitchBendRangeTargetKey, group));
    }

    private static void EmitPitchBendRangeComponent(
        List<RawMidiEvent> output,
        long tick,
        bool semitones,
        int value,
        SourceReference source,
        ref long sequence,
        CanonicalEventRole role = CanonicalEventRole.Parameter)
    {
        long group = sequence;
        long targetKey = semitones ? PitchBendRangeSemitoneTargetKey : PitchBendRangeCentsTargetKey;
        output.Add(RawMidiEvent.Control(tick, 101, 0, role, sequence++, source, targetKey, group));
        output.Add(RawMidiEvent.Control(tick, 100, 0, role, sequence++, source, targetKey, group));
        output.Add(RawMidiEvent.Control(tick, semitones ? 6 : 38, value, role, sequence++, source, targetKey, group));
        output.Add(RawMidiEvent.Control(tick, 101, 127, role, sequence++, source, targetKey, group));
        output.Add(RawMidiEvent.Control(tick, 100, 127, role, sequence++, source, targetKey, group));
    }

    private const long ControlTargetKeyBase = 0x1_0000;
    private const long ProgramTargetKey = 0x2_0000;
    private const long PitchBendTargetKey = 0x3_0000;
    private const long RpnTargetKeyBase = 0x4_0000;
    private const long NrpnTargetKeyBase = 0x5_0000;
    private const long PitchBendRangeSemitoneTargetKey = 0x6_0000;
    private const long PitchBendRangeCentsTargetKey = 0x6_0001;
    private const long PitchBendRangeTargetKey = 0x6_0002;
    private const int AllSoundOffController = 120;

    private static long ControlTargetKey(int controller) => ControlTargetKeyBase + controller;

    private static long SemanticTargetForTarget(MidiValueTarget target) => target.Kind switch
    {
        MidiValueKind.ControlChange => ControlTargetKey(target.Number),
        MidiValueKind.BankMsb => ControlTargetKey(0),
        MidiValueKind.BankLsb => ControlTargetKey(32),
        MidiValueKind.Program => ProgramTargetKey,
        MidiValueKind.PitchBend => PitchBendTargetKey,
        MidiValueKind.RegisteredParameter => RpnTargetKeyBase + target.Number,
        MidiValueKind.NonRegisteredParameter => NrpnTargetKeyBase + target.Number,
        MidiValueKind.PitchBendRangeSemitones => PitchBendRangeSemitoneTargetKey,
        MidiValueKind.PitchBendRangeCents => PitchBendRangeCentsTargetKey,
        _ => long.MinValue
    };

    private static long SemanticTargetForMessage(MidiMessage message) => message.MessageType switch
    {
        MidiMessageType.ControlChange => ControlTargetKey(message.Byte1),
        MidiMessageType.ProgramChange => ProgramTargetKey,
        MidiMessageType.PitchWheelChange => PitchBendTargetKey,
        _ => long.MinValue
    };

    private static HashSet<MidiValueTarget> GetTickZeroTargets(SubVoice voice)
    {
        HashSet<MidiValueTarget> result = [];
        foreach (TemplateEvent value in voice.Events.Where(value => value.Tick == 0))
        {
            AddTemplateTargets(value, result);
        }
        foreach (ValueCurve curve in voice.Curves)
        {
            if (curve.Points.Any(value => value.Tick == 0))
            {
                result.Add(curve.Target);
            }
        }
        return result;
    }

    private static HashSet<MidiValueTarget> CollectUsedTargets(
        EventInstrument instrument,
        SubVoice voice,
        MidiInitialState explicitState)
    {
        HashSet<MidiValueTarget> result = [];
        if (explicitState.BankMsb.HasValue) result.Add(MidiValueTarget.BankMsb);
        if (explicitState.BankLsb.HasValue) result.Add(MidiValueTarget.BankLsb);
        if (explicitState.Program.HasValue) result.Add(MidiValueTarget.Program);
        if (explicitState.PitchBend.HasValue) result.Add(MidiValueTarget.PitchBend);
        if (explicitState.PitchBendRangeSemitones.HasValue) result.Add(MidiValueTarget.PitchBendRangeSemitones);
        if (explicitState.PitchBendRangeCents.HasValue) result.Add(MidiValueTarget.PitchBendRangeCents);
        foreach (int controller in explicitState.Controllers.Keys) result.Add(MidiValueTarget.ControlChange(controller));
        foreach (int parameter in explicitState.RegisteredParameters.Keys) result.Add(MidiValueTarget.Rpn(parameter));
        foreach (int parameter in explicitState.NonRegisteredParameters.Keys) result.Add(MidiValueTarget.Nrpn(parameter));
        foreach (TemplateEvent value in voice.Events) AddTemplateTargets(value, result);
        foreach (ValueCurve curve in voice.Curves) result.Add(curve.Target);
        foreach (LogicalParameterMapping mapping in instrument.ParameterMappings
            .Where(value => value.Steps.IsEnabled && value.SubVoiceId == voice.Id))
        {
            result.Add(mapping.Target);
        }
        return result;
    }

    private static void AddTemplateTargets(TemplateEvent value, HashSet<MidiValueTarget> targets)
    {
        switch (value.Kind)
        {
            case TemplateEventKind.Note:
                break;
            case TemplateEventKind.ControlChange:
                targets.Add(MidiValueTarget.ControlChange(value.Number));
                break;
            case TemplateEventKind.Bank:
                if (value.HasBankMsb) targets.Add(MidiValueTarget.BankMsb);
                if (value.HasBankLsb) targets.Add(MidiValueTarget.BankLsb);
                break;
            case TemplateEventKind.Program:
                targets.Add(MidiValueTarget.Program);
                break;
            case TemplateEventKind.PitchBend:
                targets.Add(MidiValueTarget.PitchBend);
                break;
            case TemplateEventKind.RegisteredParameter:
                targets.Add(MidiValueTarget.Rpn(value.Number));
                break;
            case TemplateEventKind.NonRegisteredParameter:
                targets.Add(MidiValueTarget.Nrpn(value.Number));
                break;
            case TemplateEventKind.PitchBendRange:
                targets.Add(MidiValueTarget.PitchBendRangeSemitones);
                targets.Add(MidiValueTarget.PitchBendRangeCents);
                break;
        }
    }

    private static void EmitReset(
        List<RawMidiEvent> output,
        long tick,
        IEnumerable<MidiValueTarget> targets,
        MidiInitialState resetDefaults,
        SourceReference source,
        ref long sequence)
    {
        foreach (MidiValueTarget target in targets.OrderBy(value => value.Kind).ThenBy(value => value.Number))
        {
            switch (target.Kind)
            {
                case MidiValueKind.ControlChange:
                    int controllerValue = resetDefaults.Controllers.GetValueOrDefault(
                        target.Number, BuiltInControllerReset(target.Number));
                    output.Add(RawMidiEvent.Control(tick, target.Number, controllerValue,
                        CanonicalEventRole.Reset, sequence++, source));
                    break;
                case MidiValueKind.BankMsb:
                    output.Add(RawMidiEvent.Control(tick, 0, resetDefaults.BankMsb ?? 0,
                        CanonicalEventRole.Reset, sequence++, source));
                    break;
                case MidiValueKind.BankLsb:
                    output.Add(RawMidiEvent.Control(tick, 32, resetDefaults.BankLsb ?? 0,
                        CanonicalEventRole.Reset, sequence++, source));
                    break;
                case MidiValueKind.Program:
                    output.Add(RawMidiEvent.Program(tick, resetDefaults.Program ?? 0, sequence++, source, CanonicalEventRole.Reset));
                    break;
                case MidiValueKind.PitchBend:
                    output.Add(RawMidiEvent.PitchBend(tick, resetDefaults.PitchBend ?? 0, sequence++, source, CanonicalEventRole.Reset));
                    break;
                case MidiValueKind.RegisteredParameter:
                    EmitParameter(output, tick, true, target.Number,
                        resetDefaults.RegisteredParameters.GetValueOrDefault(target.Number), source, ref sequence, CanonicalEventRole.Reset);
                    break;
                case MidiValueKind.NonRegisteredParameter:
                    EmitParameter(output, tick, false, target.Number,
                        resetDefaults.NonRegisteredParameters.GetValueOrDefault(target.Number), source, ref sequence, CanonicalEventRole.Reset);
                    break;
                case MidiValueKind.PitchBendRangeSemitones:
                    EmitPitchBendRangeComponent(output, tick, true,
                        resetDefaults.PitchBendRangeSemitones ?? 2, source, ref sequence, CanonicalEventRole.Reset);
                    break;
                case MidiValueKind.PitchBendRangeCents:
                    EmitPitchBendRangeComponent(output, tick, false,
                        resetDefaults.PitchBendRangeCents ?? 0, source, ref sequence, CanonicalEventRole.Reset);
                    break;
            }
        }
    }

    private static int BuiltInControllerReset(int controller) =>
        MidiValueTargetDefaults.GetControllerDefaultValue(controller);

    private static LogicalUsageInterval[] BuildLogicalUsageIntervals(
        MidoraProject project,
        IReadOnlyCollection<RawInstance> instances,
        CancellationToken cancellationToken)
    {
        HashSet<MidoraId> participatingUsageIds = instances
            .Where(value => !value.Isolated)
            .Select(value => value.UsageId)
            .ToHashSet();
        Dictionary<MidoraId, int> trackOrder = project.LogicalTracksInArrangementOrder()
            .Select((track, index) => (track.Id, index))
            .ToDictionary(value => value.Id, value => value.index);
        List<LogicalUsageInterval> result = [];
        foreach (IGrouping<MidoraId, (LogicalTrack Track, Segment Segment)> usageSegments in project.Tracks
            .SelectMany(track => track.Segments.Select(segment => (Track: track, Segment: segment)))
            .Where(value => value.Segment.LengthTicks > 0)
            .GroupBy(value => value.Track.EventInstrumentUsageId ?? value.Track.Id)
            .Where(value => participatingUsageIds.Contains(value.Key)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            (LogicalTrack Track, Segment Segment)[] ordered = usageSegments
                .OrderBy(value => value.Segment.ProjectStartTick)
                .ThenBy(value => trackOrder.GetValueOrDefault(value.Track.Id, int.MaxValue))
                .ThenBy(value => value.Segment.Id)
                .ToArray();
            if (ordered.Length == 0)
            {
                continue;
            }
            MidoraId groupId = ordered[0].Segment.Id;
            long start = ordered[0].Segment.ProjectStartTick;
            long end = checked(start + ordered[0].Segment.LengthTicks);
            for (int index = 1; index < ordered.Length; index++)
            {
                Segment segment = ordered[index].Segment;
                long segmentEnd = checked(segment.ProjectStartTick + segment.LengthTicks);
                if (segment.ProjectStartTick <= end)
                {
                    end = Math.Max(end, segmentEnd);
                    continue;
                }
                result.Add(new(usageSegments.Key, groupId, start, end));
                groupId = segment.Id;
                start = segment.ProjectStartTick;
                end = segmentEnd;
            }
            result.Add(new(usageSegments.Key, groupId, start, end));
        }
        return result.ToArray();
    }

    private static bool IsHistoricalSharedUsageStateSource(
        RawInstance instance,
        long startTick,
        IReadOnlyList<LogicalUsageInterval> intervals)
    {
        if (startTick <= 0 || instance.Isolated || instance.StartTick >= startTick)
        {
            return false;
        }

        return intervals.Any(value =>
            value.UsageId == instance.UsageId
            && value.StartTick < startTick
            && value.EndTick > startTick
            && instance.StartTick >= value.StartTick
            && instance.StartTick < value.EndTick);
    }

    private static void ValidateOverlap(
        MidoraProject project,
        List<RawInstance> instances,
        List<CompilerDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        foreach (IGrouping<(MidoraId UsageId, MidoraId InstrumentId), RawInstance> binding in instances
            .GroupBy(value => (value.UsageId, value.InstrumentId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RawInstance[] ordered = binding.OrderBy(value => value.StartTick).ThenBy(value => value.SourceOrder).ToArray();
            for (int i = 0; i < ordered.Length; i++)
            {
                if ((i & 255) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                for (int j = i + 1; j < ordered.Length && ordered[j].StartTick < ordered[i].EndTick; j++)
                {
                    bool inScope = ordered[i].OverlapScope == OverlapScope.AnyPitch || ordered[i].Pitch == ordered[j].Pitch;
                    if (!inScope || ordered[i].OverlapPolicy is OverlapPolicy.LetOverlap
                        or OverlapPolicy.CutPrevious or OverlapPolicy.CutNewRejectNew)
                    {
                        continue;
                    }
                    DiagnosticSeverity severity = ordered[i].OverlapPolicy == OverlapPolicy.Reject
                        ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning;
                    diagnostics.Add(new("MIDORA2201", severity,
                        "An Event Instrument Instance has a policy-constrained overlap.",
                        new(ordered[j].TrackId, ordered[j].SegmentId, ordered[j].InstanceId,
                            ordered[j].InstrumentId, Tick: ordered[j].StartTick)));
                }
            }
        }
    }

    private static AllocationResult Allocate(
        MidoraProject project,
        List<RawInstance> instances,
        PureMidiPlan pureMidiPlan,
        List<CompilerDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        List<AllocationGroup> groups = [];
        Dictionary<MidoraId, LogicalUsageInterval[]> intervalsByUsage =
            BuildLogicalUsageIntervals(project, instances, cancellationToken)
                .GroupBy(value => value.UsageId)
                .ToDictionary(value => value.Key, value => value.ToArray());

        foreach (IGrouping<(MidoraId UsageId, MidoraId InstrumentId), RawInstance> binding in instances
            .Where(value => !value.Isolated)
            .GroupBy(value => (value.UsageId, value.InstrumentId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RawInstance[] ordered = binding.OrderBy(value => value.StartTick).ThenBy(value => value.SourceOrder).ToArray();
            if (!intervalsByUsage.TryGetValue(binding.Key.UsageId, out LogicalUsageInterval[]? intervals))
            {
                intervals = ordered
                    .Select(value => new LogicalUsageInterval(
                        value.UsageId,
                        value.SegmentId,
                        value.StartTick,
                        value.SegmentEndTick))
                    .ToArray();
            }
            foreach (LogicalUsageInterval interval in intervals)
            {
                RawInstance[] members = ordered
                    .Where(value => value.StartTick >= interval.StartTick
                        && value.StartTick < interval.EndTick)
                    .ToArray();
                if (members.Length == 0)
                {
                    continue;
                }
                AllocationGroup shared = new(
                    members[0],
                    interval.GroupId,
                    interval.StartTick,
                    interval.EndTick,
                    isSharedUsageGroup: true);
                for (int instanceIndex = 1; instanceIndex < members.Length; instanceIndex++)
                {
                    shared.Add(members[instanceIndex]);
                }
                groups.Add(shared);
            }
        }

        foreach (IGrouping<(MidoraId TrackId, MidoraId InstrumentId, MidoraId SegmentId), RawInstance> binding in instances
            .Where(value => value.Isolated)
            .GroupBy(value => (value.TrackId, value.InstrumentId, value.SegmentId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RawInstance[] ordered = binding.OrderBy(value => value.StartTick).ThenBy(value => value.SourceOrder).ToArray();
            List<AllocationGroup> isolatedLanes = [];
            foreach (RawInstance instance in ordered)
            {
                AllocationGroup? reusable = isolatedLanes
                    .Where(value => value.LastLifecycleEndTick <= instance.StartTick)
                    .OrderBy(value => value.SourceOrder)
                    .FirstOrDefault();
                if (reusable is null)
                {
                    isolatedLanes.Add(new(instance));
                }
                else
                {
                    reusable.Add(instance);
                }
            }
            groups.AddRange(isolatedLanes);
        }
        Dictionary<MidoraId, int> trackOrder = project.LogicalTracksInArrangementOrder()
            .Select((track, index) => (track.Id, index)).ToDictionary(value => value.Id, value => value.index);
        foreach (LogicalTrack track in project.Tracks)
        {
            trackOrder.TryAdd(track.Id, trackOrder.Count);
        }
        groups.Sort((left, right) =>
        {
            int byStart = left.StartTick.CompareTo(right.StartTick);
            if (byStart != 0) return byStart;
            int byTrack = trackOrder[left.TrackId].CompareTo(trackOrder[right.TrackId]);
            return byTrack != 0 ? byTrack : left.SourceOrder.CompareTo(right.SourceOrder);
        });

        bool[] used = new bool[256];
        Dictionary<MidoraId, int> unitByRoot = [];
        int reservedRootUnitCount = 0;
        foreach (MidiChannelRoot root in pureMidiPlan.Roots
            .Where(value => value.ParticipatesInRequest
                && value.Root.RoutingMode == MidiChannelRootRoutingMode.Fixed)
            .Select(value => value.Root))
        {
            int unit = (root.FixedZeroBasedPort * 16) + root.FixedZeroBasedChannel;
            if (used[unit])
            {
                continue;
            }
            used[unit] = true;
            unitByRoot.Add(root.Id, unit);
            reservedRootUnitCount++;
        }
        foreach (PureMidiRootPlan rootPlan in pureMidiPlan.Roots
            .Where(value => value.Root.RoutingMode == MidiChannelRootRoutingMode.Auto
                && value.HasParticipatingSegments))
        {
            int unit = Array.FindIndex(used, static value => !value);
            if (unit < 0)
            {
                diagnostics.Add(new(
                    "MIDORA2203",
                    DiagnosticSeverity.Error,
                    "A non-empty Auto MIDI Channel Root cannot be allocated; the global limit is 256 Channel Units.",
                    new(MidiChannelRootId: rootPlan.Root.Id)));
                continue;
            }
            used[unit] = true;
            unitByRoot.Add(rootPlan.Root.Id, unit);
        }
        List<ActiveAllocation> active = [];
        Dictionary<(MidoraId InstanceId, MidoraId SubVoiceId), int> unitByVoice = [];
        List<ChannelUnitAllocation> allocations = [];
        foreach (PureMidiRootPlan rootPlan in pureMidiPlan.Roots)
        {
            if (!unitByRoot.TryGetValue(rootPlan.Root.Id, out int unit))
            {
                continue;
            }
            foreach (PureMidiRootInterval interval in rootPlan.Intervals)
            {
                allocations.Add(new(
                    interval.StartOwnerTrackId,
                    interval.StartOwnerSegmentId,
                    default,
                    interval.GroupId,
                    interval.GroupId,
                    rootPlan.Root.Id,
                    interval.StartTick,
                    interval.EndTick,
                    checked((byte)(unit >> 4)),
                    checked((byte)(unit & 15)),
                    rootPlan.Root.Id,
                    interval.StartOwnerTrackId,
                    rootPlan.Root.ChannelMode));
            }
        }
        int rootUnitCount = unitByRoot.Count;
        int peak = rootUnitCount;
        int logicalPeak = 0;
        ResourceShortageDetails? resourceShortage = null;
        foreach (AllocationGroup group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int i = active.Count - 1; i >= 0; i--)
            {
                if (active[i].EndTick <= group.StartTick)
                {
                    foreach (int unit in active[i].Units)
                    {
                        used[unit] = false;
                    }
                    active.RemoveAt(i);
                }
            }
            int count = group.Instances[0].Voices.Length;
            int[] units = new int[count];
            int found = 0;
            for (int unit = 0; unit < used.Length && found < count; unit++)
            {
                if (!used[unit])
                {
                    units[found++] = unit;
                }
            }
            if (found != count)
            {
                if (resourceShortage is null)
                {
                    AllocationGroup[] relatedGroups = active
                        .Select(value => value.Group)
                        .Append(group)
                        .ToArray();
                    RawInstance[] relatedInstances = relatedGroups
                        .SelectMany(value => value.Instances)
                        .ToArray();
                    resourceShortage = new ResourceShortageDetails(
                        new TickRange(
                            group.StartTick,
                            relatedGroups.Min(value => value.EndTick)),
                        count,
                        used.Count(value => !value),
                        relatedInstances.Select(value => value.TrackId),
                        relatedInstances.Select(value => value.SegmentId),
                        relatedInstances.Select(value => value.InstanceId),
                        relatedInstances.Select(value => value.InstrumentId),
                        relatedInstances.SelectMany(value => value.Voices.Select(voice => voice.SubVoiceId)));
                }
                diagnostics.Add(new("MIDORA2202", DiagnosticSeverity.Error,
                    $"A Channel Group for {count} SubVoices cannot be allocated atomically; the global limit is 256 Channel Units.",
                    new(group.Instances[0].TrackId, group.Instances[0].SegmentId,
                        group.Instances[0].InstanceId, group.Instances[0].InstrumentId, Tick: group.StartTick)));
                continue;
            }
            foreach (int unit in units)
            {
                used[unit] = true;
            }
            active.Add(new(group, units));
            int activeLogicalCount = active.Sum(value => value.Units.Length);
            logicalPeak = Math.Max(logicalPeak, activeLogicalCount);
            peak = Math.Max(peak, rootUnitCount + activeLogicalCount);
            foreach (RawInstance instance in group.Instances)
            {
                for (int i = 0; i < instance.Voices.Length; i++)
                {
                    RawSubVoice voice = instance.Voices[i];
                    int unit = units[i];
                    unitByVoice[(instance.InstanceId, voice.SubVoiceId)] = unit;
                    allocations.Add(new(instance.TrackId, instance.SegmentId, instance.InstrumentId, instance.InstanceId,
                        group.GroupId, voice.SubVoiceId,
                        group.StartTick, group.EndTick, (byte)(unit >> 4), (byte)(unit & 15),
                        EventInstrumentUsageId: instance.UsageId));
                }
            }
        }
        return new(
            unitByVoice,
            unitByRoot,
            groups.ToArray(),
            allocations.ToArray(),
            peak,
            logicalPeak,
            reservedRootUnitCount,
            rootUnitCount,
            resourceShortage);
    }

    public static CanonicalCompiledResult CreateDefaultPlaybackView(
        CanonicalCompiledResult fullProjectResult)
    {
        ArgumentNullException.ThrowIfNull(fullProjectResult);
        return fullProjectResult.CreatePlaybackView();
    }

    private static List<CanonicalMidiEvent> MaterializeEvents(
        MidoraProject project,
        List<RawInstance> instances,
        ReadOnlySpan<AllocationGroup> groups,
        IReadOnlyDictionary<(MidoraId InstanceId, MidoraId SubVoiceId), int> unitByVoice,
        MidiInitialState resetDefaults,
        CancellationToken cancellationToken)
    {
        List<CanonicalMidiEvent> result = [];
        HashSet<MidoraId> logicalTrackIds = project.Tracks
            .Select(value => value.Id)
            .ToHashSet();
        Dictionary<MidoraId, int> arrangementTrackOrder = project.TracksInArrangementOrder()
            .Select((value, index) => (value.TrackId, index))
            .Where(value => logicalTrackIds.Contains(value.TrackId))
            .GroupBy(value => value.TrackId)
            .ToDictionary(value => value.Key, value => value.First().index);
        HashSet<MidoraId> laneActivationInstances = GetLaneActivationInstances(
            groups,
            cancellationToken);
        foreach (RawInstance instance in instances)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool includeLaneActivationState = laneActivationInstances.Contains(instance.InstanceId);
            foreach (RawSubVoice voice in instance.Voices)
            {
                if (!unitByVoice.TryGetValue((instance.InstanceId, voice.SubVoiceId), out int unit))
                {
                    continue;
                }
                byte channel = (byte)(unit & 15);
                foreach (RawMidiEvent value in voice.Events)
                {
                    if (!includeLaneActivationState
                        && value.Tick == instance.StartTick
                        && value.Role is CanonicalEventRole.Reset or CanonicalEventRole.InitialState)
                    {
                        continue;
                    }
                    result.Add(new(value.Tick, (byte)(unit >> 4), channel, value.ToMidiMessage(channel),
                        value.Role, value.Sequence, value.SemanticTargetKey, value.SemanticGroup, value.Source,
                        SmfTrackOrder: arrangementTrackOrder.GetValueOrDefault(
                            instance.TrackId,
                            int.MaxValue)));
                }
            }
        }

        long cleanupSequence = long.MaxValue / 2;
        foreach (AllocationGroup group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RawInstance[] orderedInstances = group.Instances
                .OrderBy(static value => value.StartTick)
                .ThenBy(static value => value.SourceOrder)
                .ToArray();
            RawInstance sourceInstance = orderedInstances
                .OrderByDescending(static value => value.EndTick)
                .ThenByDescending(static value => value.SourceOrder)
                .First();
            for (int voiceIndex = 0; voiceIndex < sourceInstance.Voices.Length; voiceIndex++)
            {
                RawSubVoice sourceVoice = sourceInstance.Voices[voiceIndex];
                if (!unitByVoice.TryGetValue(
                    (sourceInstance.InstanceId, sourceVoice.SubVoiceId),
                    out int unit))
                {
                    continue;
                }

                EmitAllocationCleanup(
                    result,
                    orderedInstances,
                    voiceIndex,
                    group.EndTick,
                    unit,
                    resetDefaults,
                    includeAllSoundOff: orderedInstances.Any(
                        instance => instance.Voices[voiceIndex].HasSoundingNotes),
                    arrangementTrackOrder.GetValueOrDefault(
                        sourceInstance.TrackId,
                        int.MaxValue),
                    ref cleanupSequence);
            }
        }

        result.Sort(CanonicalComparer.Instance);
        cancellationToken.ThrowIfCancellationRequested();
        return FoldSameTickStates(result, cancellationToken);
    }

    private static HashSet<MidoraId> GetLaneActivationInstances(
        ReadOnlySpan<AllocationGroup> groups,
        CancellationToken cancellationToken)
    {
        // A Segment-owned lane remains allocated through Segment End, but its
        // formal state ownership restarts after the preceding instance cluster
        // no longer overlaps. Followers inside one shared cluster must not reset
        // channel-wide state underneath instances that are still active.
        HashSet<MidoraId> result = [];
        foreach (AllocationGroup group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RawInstance[] orderedInstances = group.Instances
                .OrderBy(static value => value.StartTick)
                .ThenBy(static value => value.SourceOrder)
                .ToArray();
            result.Add(orderedInstances[0].InstanceId);
            long activeClusterEndTick = orderedInstances[0].EndTick;
            for (int instanceIndex = 1; instanceIndex < orderedInstances.Length; instanceIndex++)
            {
                RawInstance instance = orderedInstances[instanceIndex];
                if (instance.StartTick >= activeClusterEndTick)
                {
                    result.Add(instance.InstanceId);
                    activeClusterEndTick = instance.EndTick;
                }
                else
                {
                    activeClusterEndTick = Math.Max(activeClusterEndTick, instance.EndTick);
                }
            }
        }
        return result;
    }

    private static void EmitAllocationCleanup(
        List<CanonicalMidiEvent> output,
        ReadOnlySpan<RawInstance> instances,
        int voiceIndex,
        long tick,
        int unit,
        MidiInitialState resetDefaults,
        bool includeAllSoundOff,
        int arrangementTrackOrder,
        ref long sequence)
    {
        RawInstance sourceInstance = instances[0];
        for (int instanceIndex = 1; instanceIndex < instances.Length; instanceIndex++)
        {
            RawInstance candidate = instances[instanceIndex];
            if (candidate.EndTick > sourceInstance.EndTick
                || (candidate.EndTick == sourceInstance.EndTick
                    && candidate.SourceOrder > sourceInstance.SourceOrder))
            {
                sourceInstance = candidate;
            }
        }
        RawSubVoice sourceVoice = sourceInstance.Voices[voiceIndex];
        SourceReference source = new(
            sourceInstance.TrackId,
            sourceInstance.SegmentId,
            sourceInstance.InstanceId,
            sourceInstance.InstrumentId,
            sourceVoice.SubVoiceId,
            Tick: tick,
            Origin: SourceOrigin.ProjectResetDefaults);
        List<RawMidiEvent> cleanup = [];
        if (includeAllSoundOff)
        {
            cleanup.Add(RawMidiEvent.Control(
                tick,
                AllSoundOffController,
                0,
                CanonicalEventRole.Reset,
                sequence++,
                source));
        }

        HashSet<MidiValueTarget> usedTargetSet = [];
        foreach (RawInstance instance in instances)
        {
            usedTargetSet.UnionWith(instance.Voices[voiceIndex].UsedTargets);
        }
        MidiValueTarget[] usedTargets = usedTargetSet
            .OrderBy(static target => target.Kind)
            .ThenBy(static target => target.Number)
            .ToArray();
        EmitReset(
            cleanup,
            tick,
            usedTargets,
            resetDefaults,
            source,
            ref sequence);
        byte channel = (byte)(unit & 15);
        foreach (RawMidiEvent value in cleanup)
        {
            output.Add(new(
                value.Tick,
                (byte)(unit >> 4),
                channel,
                value.ToMidiMessage(channel),
                value.Role,
                value.Sequence,
                value.SemanticTargetKey,
                value.SemanticGroup,
                value.Source,
                SmfTrackOrder: arrangementTrackOrder));
        }
    }

    private static List<CanonicalMidiEvent> FoldSameTickStates(
        List<CanonicalMidiEvent> sorted,
        CancellationToken cancellationToken)
    {
        Dictionary<(byte Port, byte Channel, long Target), long> selectedGroups = [];
        List<CanonicalMidiEvent> reversed = new(sorted.Count);
        long currentTick = long.MinValue;
        for (int i = sorted.Count - 1; i >= 0; i--)
        {
            if ((i & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            CanonicalMidiEvent value = sorted[i];
            if (value.Tick != currentTick)
            {
                selectedGroups.Clear();
                currentTick = value.Tick;
            }
            if (value.SemanticTargetKey == long.MinValue
                || value.Role == CanonicalEventRole.DirectMidi)
            {
                reversed.Add(value);
                continue;
            }
            (byte, byte, long) key = (
                value.ZeroBasedPort,
                value.ZeroBasedChannel,
                value.SemanticTargetKey);
            if (!selectedGroups.TryGetValue(key, out long selectedGroup))
            {
                selectedGroups.Add(key, value.SemanticGroup);
                reversed.Add(value);
            }
            else if (selectedGroup == value.SemanticGroup)
            {
                reversed.Add(value);
            }
        }
        // This pass only removes entries from an already sorted sequence. Reversing
        // the backward traversal restores the original canonical order; sorting the
        // surviving high-volume event list again is both redundant and expensive.
        reversed.Reverse();
        return reversed;
    }

    private static CanonicalMidiEvent[] ApplyRange(
        List<CanonicalMidiEvent> source,
        ReadOnlySpan<ChannelUnitAllocation> allocations,
        long startTick,
        long endTick,
        MidiInitialState resetDefaults,
        bool suppressEndCleanup = false,
        CancellationToken cancellationToken = default)
    {
        List<CanonicalMidiEvent> result = [];
        Dictionary<(byte Port, byte Channel, long Target), CanonicalStateGroup> state = [];
        Dictionary<(byte Port, byte Channel, byte Note), Queue<SourceReference>> activeNotes = [];
        HashSet<(byte Port, byte Channel, long Target)> pollutedTargets = [];
        HashSet<(byte Port, byte Channel)> channelsNeedingSoundOff = [];
        HashSet<(byte Port, byte Channel)> activeAtStart = [];
        foreach (ChannelUnitAllocation allocation in allocations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (allocation.StartTick < startTick && allocation.EndTick > startTick)
            {
                activeAtStart.Add((allocation.ZeroBasedPort, allocation.ZeroBasedChannel));
            }
        }
        for (int sourceIndex = 0; sourceIndex < source.Count; sourceIndex++)
        {
            if ((sourceIndex & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            CanonicalMidiEvent value = source[sourceIndex];
            MidiMessage message = value.Message;
            if (value.Tick < endTick
                && message.MessageType == MidiMessageType.NoteOn
                && message.Byte2 != 0)
            {
                channelsNeedingSoundOff.Add((value.ZeroBasedPort, value.ZeroBasedChannel));
            }
            else if (value.Tick < endTick
                && message.MessageType == MidiMessageType.ControlChange
                && message.Byte1 == AllSoundOffController)
            {
                channelsNeedingSoundOff.Remove((value.ZeroBasedPort, value.ZeroBasedChannel));
            }
            if (value.Tick < startTick)
            {
                if (message.MessageType == MidiMessageType.NoteOn && message.Byte2 != 0)
                {
                    (byte, byte, byte) key = (value.ZeroBasedPort, value.ZeroBasedChannel, message.Byte1);
                    if (!activeNotes.TryGetValue(key, out Queue<SourceReference>? sources))
                    {
                        sources = new Queue<SourceReference>();
                        activeNotes.Add(key, sources);
                    }
                    sources.Enqueue(value.Source);
                }
                else if (message.MessageType == MidiMessageType.NoteOff
                    || (message.MessageType == MidiMessageType.NoteOn && message.Byte2 == 0))
                {
                    (byte, byte, byte) key = (value.ZeroBasedPort, value.ZeroBasedChannel, message.Byte1);
                    if (activeNotes.TryGetValue(key, out Queue<SourceReference>? sources)
                        && sources.Count > 0)
                    {
                        _ = sources.Dequeue();
                    }
                }
                else if (value.SemanticTargetKey != long.MinValue)
                {
                    (byte, byte, long) key = (
                        value.ZeroBasedPort, value.ZeroBasedChannel, value.SemanticTargetKey);
                    if (!state.TryGetValue(key, out CanonicalStateGroup? group)
                        || group.SemanticGroup != value.SemanticGroup)
                    {
                        group = new CanonicalStateGroup(
                            value.Tick, value.Role, value.StableOrder, value.SemanticGroup);
                        state[key] = group;
                    }
                    group.Events.Add(value);
                }
                continue;
            }
            if (value.Tick >= endTick)
            {
                if (value.Tick == endTick
                    && value.Role == CanonicalEventRole.DirectMidi
                    && value.ExportTrackId != default
                    && (message.MessageType == MidiMessageType.NoteOff
                        || message.MessageType == MidiMessageType.NoteOn && message.Byte2 == 0))
                {
                    // A Direct MIDI Note endpoint is source data, including its
                    // NoteOff velocity. Keep that exact endpoint at the hard
                    // range boundary instead of replacing it with the generic
                    // velocity-0 cleanup below.
                    result.Add(value);
                    (byte, byte, byte) key = (
                        value.ZeroBasedPort,
                        value.ZeroBasedChannel,
                        message.Byte1);
                    if (activeNotes.TryGetValue(key, out Queue<SourceReference>? sources)
                        && sources.Count > 0)
                    {
                        _ = sources.Dequeue();
                    }
                }
                continue;
            }
            result.Add(value);
            if (message.MessageType is not MidiMessageType.NoteOn and not MidiMessageType.NoteOff
                && value.Role != CanonicalEventRole.Reset
                && value.SemanticTargetKey != long.MinValue
                && value.SemanticTargetKey != ControlTargetKey(AllSoundOffController))
            {
                pollutedTargets.Add((value.ZeroBasedPort, value.ZeroBasedChannel, value.SemanticTargetKey));
            }
            if (message.MessageType == MidiMessageType.NoteOn && message.Byte2 != 0)
            {
                (byte, byte, byte) key = (value.ZeroBasedPort, value.ZeroBasedChannel, message.Byte1);
                if (!activeNotes.TryGetValue(key, out Queue<SourceReference>? sources))
                {
                    sources = new Queue<SourceReference>();
                    activeNotes.Add(key, sources);
                }
                sources.Enqueue(value.Source);
            }
            else if (message.MessageType == MidiMessageType.NoteOff
                || (message.MessageType == MidiMessageType.NoteOn && message.Byte2 == 0))
            {
                (byte, byte, byte) key = (value.ZeroBasedPort, value.ZeroBasedChannel, message.Byte1);
                if (activeNotes.TryGetValue(key, out Queue<SourceReference>? sources)
                    && sources.Count > 0)
                {
                    _ = sources.Dequeue();
                }
            }
        }

        long restoreOrder = long.MinValue;
        foreach (((byte port, byte channel, long target), CanonicalStateGroup group) in state
            .Where(value => activeAtStart.Contains((value.Key.Port, value.Key.Channel)))
            .OrderBy(value => value.Key.Port).ThenBy(value => value.Key.Channel)
            .ThenBy(value => RangeRestoreSortCategory(value.Key.Target, value.Value))
            .ThenBy(value => value.Key.Target)
            .ThenBy(value => value.Value.Tick)
            .ThenBy(value => value.Value.StableOrder))
        {
            pollutedTargets.Add((port, channel, target));
            foreach (CanonicalMidiEvent previous in group.Events.OrderBy(value => value.StableOrder))
            {
                result.Add(previous with
                {
                    Tick = startTick,
                    Role = CanonicalEventRole.RangeRestore,
                    StableOrder = restoreOrder++,
                    Source = previous.Source with { Origin = SourceOrigin.RangeRestore },
                    SmfTrackOrder = int.MinValue + 1,
                    SmfEventOrder = restoreOrder
                });
            }
        }

        if (suppressEndCleanup)
        {
            result.Sort(CanonicalComparer.Instance);
            cancellationToken.ThrowIfCancellationRequested();
            return FoldSameTickStates(result, cancellationToken).ToArray();
        }

        HashSet<(byte Port, byte Channel)> cleanupChannels = [];
        long boundaryNoteOffOrder = long.MaxValue / 2;
        foreach (((byte port, byte channel, byte note), Queue<SourceReference> sources) in activeNotes
            .Where(value => value.Value.Count != 0)
            .OrderBy(value => value.Key.Port)
            .ThenBy(value => value.Key.Channel)
            .ThenBy(value => value.Key.Note))
        {
            foreach (SourceReference sourceReference in sources)
            {
                result.Add(new(endTick, port, channel, MidiMessage.NoteOff(channel, note, 0),
                    CanonicalEventRole.NoteOff, boundaryNoteOffOrder++, long.MinValue, long.MinValue,
                    sourceReference with
                    {
                        Tick = endTick,
                        Origin = SourceOrigin.CompilerBoundaryCleanup
                    }));
            }
            cleanupChannels.Add((port, channel));
        }
        foreach (ChannelUnitAllocation allocation in allocations)
        {
            // Events at endTick are excluded by the half-open range. An allocation
            // ending exactly there therefore still needs the synthetic boundary
            // cleanup that replaces its filtered source Reset group.
            if (allocation.StartTick < endTick && allocation.EndTick >= endTick)
            {
                cleanupChannels.Add((allocation.ZeroBasedPort, allocation.ZeroBasedChannel));
            }
        }
        long resetOrder = long.MaxValue / 2;
        foreach ((byte port, byte channel) in cleanupChannels
            .OrderBy(value => value.Port)
            .ThenBy(value => value.Channel))
        {
            if (channelsNeedingSoundOff.Contains((port, channel)))
            {
                long soundOffOrder = resetOrder++;
                result.Add(new(
                    endTick,
                    port,
                    channel,
                    MidiMessage.ControlChange(channel, AllSoundOffController, 0),
                    CanonicalEventRole.Reset,
                    soundOffOrder,
                    ControlTargetKey(AllSoundOffController),
                    soundOffOrder,
                    new(Tick: endTick, Origin: SourceOrigin.CompilerBoundaryCleanup)));
            }

            foreach ((byte statePort, byte stateChannel, long target) in pollutedTargets
                .OrderBy(value => value.Port)
                .ThenBy(value => value.Channel)
                .ThenBy(value => value.Target))
            {
                if (statePort != port || stateChannel != channel)
                {
                    continue;
                }
                AppendCanonicalReset(
                    result, endTick, port, channel, target, resetDefaults, ref resetOrder);
            }
        }
        result.Sort(CanonicalComparer.Instance);
        cancellationToken.ThrowIfCancellationRequested();
        return FoldSameTickStates(result, cancellationToken).ToArray();
    }

    private static int RangeRestoreSortCategory(
        long target,
        CanonicalStateGroup group)
    {
        if (group.Role == CanonicalEventRole.Reset)
        {
            return 0;
        }
        if (target == ControlTargetKey(0)) return 1;
        if (target == ControlTargetKey(32)) return 2;
        if (target == ProgramTargetKey) return 3;
        if (target >= RpnTargetKeyBase && target < RpnTargetKeyBase + 16_384) return 4;
        if (target >= NrpnTargetKeyBase && target < NrpnTargetKeyBase + 16_384) return 5;
        if (target >= ControlTargetKeyBase && target < ControlTargetKeyBase + 128) return 6;
        if (target == PitchBendTargetKey) return 7;
        return 8;
    }

    private static void AppendCanonicalReset(
        List<CanonicalMidiEvent> output,
        long tick,
        byte port,
        byte channel,
        long target,
        MidiInitialState defaults,
        ref long order)
    {
        long currentOrder = order;
        long group = currentOrder;
        if (target >= ControlTargetKeyBase && target < ControlTargetKeyBase + 128)
        {
            int controller = checked((int)(target - ControlTargetKeyBase));
            int value = controller switch
            {
                0 => defaults.BankMsb ?? 0,
                32 => defaults.BankLsb ?? 0,
                _ => defaults.Controllers.GetValueOrDefault(controller, BuiltInControllerReset(controller))
            };
            Add(MidiMessage.ControlChange(channel, checked((byte)controller), checked((byte)value)));
        }
        else if (target == ProgramTargetKey)
        {
            Add(MidiMessage.ProgramChange(channel, checked((byte)(defaults.Program ?? 0))));
        }
        else if (target == PitchBendTargetKey)
        {
            Add(MidiMessage.PitchWheelChange(channel, checked((ushort)((defaults.PitchBend ?? 0) + 8192))));
        }
        else if (target >= RpnTargetKeyBase && target < RpnTargetKeyBase + 16_384)
        {
            int parameter = checked((int)(target - RpnTargetKeyBase));
            AddParameter(true, parameter, defaults.RegisteredParameters.GetValueOrDefault(parameter));
        }
        else if (target >= NrpnTargetKeyBase && target < NrpnTargetKeyBase + 16_384)
        {
            int parameter = checked((int)(target - NrpnTargetKeyBase));
            AddParameter(false, parameter, defaults.NonRegisteredParameters.GetValueOrDefault(parameter));
        }
        else if (target == PitchBendRangeTargetKey)
        {
            Add(MidiMessage.ControlChange(channel, 101, 0));
            Add(MidiMessage.ControlChange(channel, 100, 0));
            Add(MidiMessage.ControlChange(channel, 6, checked((byte)(defaults.PitchBendRangeSemitones ?? 2))));
            Add(MidiMessage.ControlChange(channel, 38, checked((byte)(defaults.PitchBendRangeCents ?? 0))));
            Add(MidiMessage.ControlChange(channel, 101, 127));
            Add(MidiMessage.ControlChange(channel, 100, 127));
        }
        else if (target is PitchBendRangeSemitoneTargetKey or PitchBendRangeCentsTargetKey)
        {
            bool semitones = target == PitchBendRangeSemitoneTargetKey;
            Add(MidiMessage.ControlChange(channel, 101, 0));
            Add(MidiMessage.ControlChange(channel, 100, 0));
            Add(MidiMessage.ControlChange(channel, semitones ? (byte)6 : (byte)38,
                checked((byte)(semitones
                    ? defaults.PitchBendRangeSemitones ?? 2
                    : defaults.PitchBendRangeCents ?? 0))));
            Add(MidiMessage.ControlChange(channel, 101, 127));
            Add(MidiMessage.ControlChange(channel, 100, 127));
        }

        void AddParameter(bool registered, int parameter, int value)
        {
            Add(MidiMessage.ControlChange(channel, registered ? (byte)101 : (byte)99, checked((byte)(parameter >> 7))));
            Add(MidiMessage.ControlChange(channel, registered ? (byte)100 : (byte)98, checked((byte)(parameter & 127))));
            Add(MidiMessage.ControlChange(channel, 6, checked((byte)(value >> 7))));
            Add(MidiMessage.ControlChange(channel, 38, checked((byte)(value & 127))));
            Add(MidiMessage.ControlChange(channel, registered ? (byte)101 : (byte)99, 127));
            Add(MidiMessage.ControlChange(channel, registered ? (byte)100 : (byte)98, 127));
        }

        void Add(MidiMessage message) => output.Add(new(
            tick, port, channel, message, CanonicalEventRole.Reset,
            currentOrder++, target, group,
            new(Origin: SourceOrigin.ProjectResetDefaults)));

        order = currentOrder;
    }

    private static long GetNaturalEnd(
        MidoraProject project,
        IReadOnlySet<MidoraId>? includedTrackIds)
    {
        long end = 0;
        foreach (LogicalTrack track in project.Tracks)
        {
            if (includedTrackIds is not null && !includedTrackIds.Contains(track.Id))
            {
                continue;
            }
            foreach (Segment segment in track.Segments)
            {
                if (segment.Notes.Count != 0
                    && segment.ProjectStartTick >= 0 && segment.LengthTicks > 0
                    && segment.ProjectStartTick <= long.MaxValue - segment.LengthTicks)
                {
                    end = Math.Max(end, segment.ProjectStartTick + segment.LengthTicks);
                }
            }
        }
        foreach (PureMidiTrack track in project.PureMidiTracks)
        {
            if (includedTrackIds is not null && !includedTrackIds.Contains(track.Id))
            {
                continue;
            }
            foreach (MidiSegment segment in track.Segments)
            {
                if (segment.ProjectStartTick >= 0
                    && segment.LengthTicks > 0
                    && segment.ProjectStartTick <= long.MaxValue - segment.LengthTicks)
                {
                    end = Math.Max(end, segment.ProjectStartTick + segment.LengthTicks);
                }
            }
        }
        return end;
    }

    private static long GetPureMidiNaturalEnd(
        MidoraProject project,
        IReadOnlySet<MidoraId>? includedTrackIds)
    {
        long end = 0;
        foreach (PureMidiTrack track in project.PureMidiTracks)
        {
            if (includedTrackIds is not null && !includedTrackIds.Contains(track.Id))
            {
                continue;
            }
            foreach (MidiSegment segment in track.Segments)
            {
                if (segment.ProjectStartTick >= 0
                    && segment.LengthTicks > 0
                    && segment.ProjectStartTick <= long.MaxValue - segment.LengthTicks)
                {
                    end = Math.Max(end, segment.ProjectStartTick + segment.LengthTicks);
                }
            }
        }
        return end;
    }

    private static CanonicalConductor FreezeConductor(ConductorTrack source, long startTick, long endTick) => new(
        RangeStateful(
            source.Tempos.CaptureQuerySnapshot(), startTick, endTick,
            value => value.Tick,
            (value, tick, restored) => new CanonicalTempo(value.Id, tick, value.BeatsPerMinute, restored)),
        RangeStateful(
            source.TimeSignatures.CaptureQuerySnapshot(), startTick, endTick,
            value => value.Tick,
            (value, tick, restored) => new CanonicalTimeSignature(
                value.Id, tick, value.Numerator, value.Denominator, restored)),
        source.TimeSignatures
            .Where(value => value.Tick < endTick || value.Tick == 0)
            .Select(value => new CanonicalTimeSignature(
                value.Id,
                value.Tick,
                value.Numerator,
                value.Denominator))
            .ToArray(),
        RangeStateful(
            source.KeySignatures.CaptureQuerySnapshot(), startTick, endTick,
            value => value.Tick,
            (value, tick, restored) => new CanonicalKeySignature(
                value.Id, tick, value.SharpsFlats, value.IsMinor, restored)),
        source.Markers.CaptureQuerySnapshot().QueryTickRange(startTick, endTick)
            .Select(value => new CanonicalMarker(value.Id, value.Tick, value.Name)).ToArray(),
        source.EndMarker is null ? null : new CanonicalEndMarker(source.EndMarker.Id, source.EndMarker.Tick));

    private static TResult[] RangeStateful<TSource, TResult>(
        ConductorQuerySnapshot<TSource> ordered,
        long startTick,
        long endTick,
        Func<TSource, long> getTick,
        Func<TSource, long, bool, TResult> convert)
        where TSource : class
    {
        List<TResult> result = [];
        int first = ordered.LowerBoundTick(startTick);
        bool hasAtStart = first < ordered.Count && getTick(ordered.GetByOrdinal(first)) == startTick;
        if (startTick > 0 && !hasAtStart)
        {
            if (ordered.TryGetBeforeTick(startTick, out TSource previous))
            {
                result.Add(convert(previous, startTick, true));
            }
        }
        for (int ordinal = first; ordinal < ordered.Count; ordinal++)
        {
            TSource value = ordered.GetByOrdinal(ordinal);
            long tick = getTick(value);
            if (tick >= startTick && (tick < endTick || endTick == startTick && tick == startTick))
            {
                result.Add(convert(value, tick, false));
            }
            else break;
        }
        return result.ToArray();
    }

    private static bool TrackReferences(
        MidoraProject project,
        LogicalTrack track,
        MidoraId instrumentId) =>
        ResolveEventInstrumentId(project, track) == instrumentId;

    private static MidoraId? ResolveEventInstrumentId(
        MidoraProject project,
        LogicalTrack track) => project.ResolveEventInstrumentDefinitionId(track);

    private static bool TryResolveEventInstrument(
        MidoraProject project,
        LogicalTrack track,
        IReadOnlyDictionary<MidoraId, EventInstrument> instruments,
        out EventInstrument instrument,
        out MidoraId usageId)
    {
        usageId = track.EventInstrumentUsageId ?? track.Id;
        MidoraId? instrumentId = ResolveEventInstrumentId(project, track);
        if (instrumentId is MidoraId id
            && instruments.TryGetValue(id, out EventInstrument? resolved))
        {
            instrument = resolved;
            return true;
        }
        instrument = null!;
        return false;
    }

    private readonly record struct CurrentSegment(Segment Segment, long SourceFingerprint);

    private sealed record TrackCacheEntry(
        long ContextFingerprint,
        SegmentCacheEntry[] Segments,
        ExpansionCheckpoint TerminalCheckpoint);

    private sealed record SegmentCacheEntry(
        MidoraId SegmentId,
        long SourceFingerprint,
        ExpansionCheckpoint EntryCheckpoint,
        int NextSourceOrder,
        RawInstance[] Instances,
        CompilerDiagnostic[] Diagnostics);

    private readonly record struct ExpansionCheckpoint(
        long Tick,
        int NextSourceOrder,
        long ContextFingerprint,
        long StateHash)
    {
        public static ExpansionCheckpoint Create(long tick, int nextSourceOrder, long contextFingerprint)
        {
            long hash = unchecked(contextFingerprint ^ (tick * -7046029254386353131L));
            hash = unchecked((hash * 1099511628211L) ^ nextSourceOrder);
            return new(tick, nextSourceOrder, contextFingerprint, hash);
        }

        public bool IsEquivalentTo(ExpansionCheckpoint other) =>
            StateHash == other.StateHash
            && Tick == other.Tick
            && NextSourceOrder == other.NextSourceOrder
            && ContextFingerprint == other.ContextFingerprint;
    }

    private sealed record TrackExpansion(
        RawInstance[] Instances,
        CompilerDiagnostic[] Diagnostics,
        bool Recompiled,
        int RecompiledSegmentCount,
        int ReusedSegmentCount,
        int StateConvergenceCount,
        long? EarliestDirtyTick);

    private sealed class CanonicalStateGroup(
        long tick,
        CanonicalEventRole role,
        long stableOrder,
        long semanticGroup)
    {
        public long Tick { get; } = tick;
        public CanonicalEventRole Role { get; } = role;
        public long StableOrder { get; } = stableOrder;
        public long SemanticGroup { get; } = semanticGroup;
        public List<CanonicalMidiEvent> Events { get; } = [];
    }

    private sealed record RawInstance(
        MidoraId InstanceId,
        MidoraId TrackId,
        MidoraId SegmentId,
        MidoraId InstrumentId,
        MidoraId UsageId,
        int Pitch,
        long StartTick,
        long EndTick,
        long SegmentEndTick,
        bool Isolated,
        OverlapPolicy OverlapPolicy,
        OverlapScope OverlapScope,
        int SourceOrder,
        RawSubVoice[] Voices,
        LogicalTrack SourceTrack,
        Segment SourceSegment,
        LogicalNote SourceNote,
        EventInstrument SourceInstrument);

    private readonly record struct EventMappingStatePoint(
        long LocalTick,
        int Value,
        MidoraId SourceEventId,
        int EventOrder);

    private sealed record RawSubVoice(
        MidoraId SubVoiceId,
        RawMidiEvent[] Events,
        MidiValueTarget[] UsedTargets,
        bool HasSoundingNotes);

    private readonly record struct LogicalUsageInterval(
        MidoraId UsageId,
        MidoraId GroupId,
        long StartTick,
        long EndTick);

    private readonly record struct OverlapInstanceKey(
        MidoraId TrackId,
        MidoraId SegmentId,
        MidoraId InstanceId)
    {
        public static OverlapInstanceKey For(RawInstance instance) => new(
            instance.TrackId,
            instance.SegmentId,
            instance.InstanceId);

        public static OverlapInstanceKey For(SourceReference source) => new(
            source.TrackId,
            source.SegmentId,
            source.LogicalNoteId);
    }

    private readonly record struct EventOccurrence(long LocalTick, long TemplateTick);
    private readonly record struct TargetStatePoint(long Tick, double Value);

    private enum RawMessageKind : byte
    {
        NoteOff,
        NoteOn,
        ControlChange,
        ProgramChange,
        PitchBend
    }

    private readonly record struct RawMidiEvent(
        long Tick,
        RawMessageKind Kind,
        int Data1,
        int Data2,
        CanonicalEventRole Role,
        long Sequence,
        long SemanticTargetKey,
        long SemanticGroup,
        SourceReference Source)
    {
        public static RawMidiEvent NoteOff(long tick, int note, long sequence, SourceReference source) =>
            new(tick, RawMessageKind.NoteOff, note, 0, CanonicalEventRole.NoteOff,
                sequence, long.MinValue, long.MinValue, source);
        public static RawMidiEvent NoteOn(long tick, int note, int velocity, long sequence, SourceReference source) =>
            new(tick, RawMessageKind.NoteOn, note, velocity, CanonicalEventRole.NoteOn,
                sequence, long.MinValue, long.MinValue, source);
        public static RawMidiEvent Control(
            long tick, int controller, int value, CanonicalEventRole role, long sequence,
            SourceReference source, long semanticTargetKey = long.MinValue, long semanticGroup = long.MinValue) =>
            new(tick, RawMessageKind.ControlChange, controller, value, role, sequence,
                semanticTargetKey == long.MinValue ? ControlTargetKey(controller) : semanticTargetKey,
                semanticGroup == long.MinValue ? sequence : semanticGroup,
                source);
        public static RawMidiEvent Program(long tick, int program, long sequence, SourceReference source, CanonicalEventRole role = CanonicalEventRole.Program) =>
            new(tick, RawMessageKind.ProgramChange, program, 0, role, sequence,
                ProgramTargetKey, sequence, source);
        public static RawMidiEvent PitchBend(long tick, int value, long sequence, SourceReference source, CanonicalEventRole role = CanonicalEventRole.PitchBend) =>
            new(tick, RawMessageKind.PitchBend, value, 0, role, sequence,
                PitchBendTargetKey, sequence, source);

        public MidiMessage ToMidiMessage(byte channel) => Kind switch
        {
            RawMessageKind.NoteOff => MidiMessage.NoteOff(channel, checked((byte)Data1), 0),
            RawMessageKind.NoteOn => MidiMessage.NoteOn(channel, checked((byte)Data1), checked((byte)Data2)),
            RawMessageKind.ControlChange => MidiMessage.ControlChange(channel, checked((byte)Data1), checked((byte)Data2)),
            RawMessageKind.ProgramChange => MidiMessage.ProgramChange(channel, checked((byte)Data1)),
            RawMessageKind.PitchBend => MidiMessage.PitchWheelChange(channel, checked((ushort)(Data1 + 8192))),
            _ => throw new InvalidOperationException()
        };
    }

    private sealed class AllocationGroup
    {
        public AllocationGroup(RawInstance instance)
            : this(
                instance,
                instance.InstanceId,
                instance.StartTick,
                instance.SegmentEndTick,
                isSharedUsageGroup: false)
        {
        }

        public AllocationGroup(
            RawInstance instance,
            MidoraId groupId,
            long startTick,
            long endTick,
            bool isSharedUsageGroup)
        {
            GroupId = groupId;
            StartTick = startTick;
            EndTick = endTick;
            LastLifecycleEndTick = instance.EndTick;
            SourceOrder = instance.SourceOrder;
            IsSharedUsageGroup = isSharedUsageGroup;
            Instances.Add(instance);
        }
        public MidoraId GroupId { get; }
        public MidoraId TrackId => Instances[0].TrackId;
        public long StartTick { get; }
        public long EndTick { get; }
        public long LastLifecycleEndTick { get; private set; }
        public int SourceOrder { get; }
        public bool IsSharedUsageGroup { get; }
        public List<RawInstance> Instances { get; } = [];
        public void Add(RawInstance instance)
        {
            if (instance.InstrumentId != Instances[0].InstrumentId
                || instance.UsageId != Instances[0].UsageId
                || !IsSharedUsageGroup
                    && (instance.TrackId != Instances[0].TrackId
                        || instance.SegmentId != Instances[0].SegmentId
                        || instance.SegmentEndTick != EndTick)
                || IsSharedUsageGroup
                    && (instance.StartTick < StartTick || instance.StartTick >= EndTick))
            {
                throw new InvalidOperationException(
                    "An allocation lane cannot contain an instance outside its binding or lifecycle interval.");
            }
            Instances.Add(instance);
            LastLifecycleEndTick = Math.Max(LastLifecycleEndTick, instance.EndTick);
        }
    }

    private sealed record ActiveAllocation(AllocationGroup Group, int[] Units)
    {
        public long EndTick => Group.EndTick;
    }
    private sealed record AllocationResult(
        Dictionary<(MidoraId InstanceId, MidoraId SubVoiceId), int> UnitBySubVoice,
        Dictionary<MidoraId, int> UnitByRoot,
        AllocationGroup[] Groups,
        ChannelUnitAllocation[] Allocations,
        int PeakUnits,
        int LogicalPeakUnits,
        int ReservedRootUnitCount,
        int AllocatedRootUnitCount,
        ResourceShortageDetails? ResourceShortage);

    private static MappingStableIdV2 ToMappingId(MidoraId id) => new(id.Value);

    private static MappingEventKindV2 ToMappingEventKind(TemplateEventKind kind) => kind switch
    {
        TemplateEventKind.Note => MappingEventKindV2.Note,
        TemplateEventKind.ControlChange => MappingEventKindV2.ControlChange,
        TemplateEventKind.Bank => MappingEventKindV2.BankSelect,
        TemplateEventKind.Program => MappingEventKindV2.ProgramChange,
        TemplateEventKind.PitchBend => MappingEventKindV2.PitchBend,
        TemplateEventKind.RegisteredParameter => MappingEventKindV2.Rpn,
        TemplateEventKind.NonRegisteredParameter => MappingEventKindV2.Nrpn,
        TemplateEventKind.PitchBendRange => MappingEventKindV2.PitchBendRange,
        _ => MappingEventKindV2.Unknown
    };

    private static MappingEventKindV2 ToMappingEventKind(MidiValueKind kind) => kind switch
    {
        MidiValueKind.ControlChange => MappingEventKindV2.ControlChange,
        MidiValueKind.BankMsb or MidiValueKind.BankLsb => MappingEventKindV2.BankSelect,
        MidiValueKind.Program => MappingEventKindV2.ProgramChange,
        MidiValueKind.PitchBend => MappingEventKindV2.PitchBend,
        MidiValueKind.RegisteredParameter => MappingEventKindV2.Rpn,
        MidiValueKind.NonRegisteredParameter => MappingEventKindV2.Nrpn,
        MidiValueKind.PitchBendRangeSemitones or MidiValueKind.PitchBendRangeCents => MappingEventKindV2.PitchBendRange,
        _ => MappingEventKindV2.Unknown
    };

    private sealed class CanonicalComparer : IComparer<CanonicalMidiEvent>
    {
        public static CanonicalComparer Instance { get; } = new();
        public int Compare(CanonicalMidiEvent x, CanonicalMidiEvent y)
        {
            int value = x.Tick.CompareTo(y.Tick);
            if (value != 0) return value;
            value = x.Role.CompareTo(y.Role);
            if (value != 0) return value;
            value = x.ZeroBasedPort.CompareTo(y.ZeroBasedPort);
            if (value != 0) return value;
            value = x.ZeroBasedChannel.CompareTo(y.ZeroBasedChannel);
            if (value != 0) return value;
            value = x.SmfTrackOrder.CompareTo(y.SmfTrackOrder);
            if (value != 0) return value;
            if (x.Role == CanonicalEventRole.DirectMidi
                && y.Role == CanonicalEventRole.DirectMidi)
            {
                value = x.SmfEventOrder.CompareTo(y.SmfEventOrder);
                if (value != 0) return value;
                value = CanonicalMidiOrdering.DirectEndpointOrder(x.Message).CompareTo(
                    CanonicalMidiOrdering.DirectEndpointOrder(y.Message));
                if (value != 0) return value;
            }
            value = x.StableOrder.CompareTo(y.StableOrder);
            if (value != 0) return value;
            value = x.Source.TrackId.CompareTo(y.Source.TrackId);
            if (value != 0) return value;
            value = x.Source.SegmentId.CompareTo(y.Source.SegmentId);
            if (value != 0) return value;
            value = x.Source.LogicalNoteId.CompareTo(y.Source.LogicalNoteId);
            if (value != 0) return value;
            value = x.Source.EventInstrumentId.CompareTo(y.Source.EventInstrumentId);
            if (value != 0) return value;
            value = x.Source.SubVoiceId.CompareTo(y.Source.SubVoiceId);
            if (value != 0) return value;
            value = x.Source.SourceEventId.CompareTo(y.Source.SourceEventId);
            if (value != 0) return value;
            value = x.Source.LogicalParameterId.CompareTo(y.Source.LogicalParameterId);
            if (value != 0) return value;
            value = x.Source.LogicalParameterMappingId.CompareTo(y.Source.LogicalParameterMappingId);
            if (value != 0) return value;
            value = x.Source.MappingStepId.CompareTo(y.Source.MappingStepId);
            if (value != 0) return value;
            value = x.Source.MappingFunctionId.CompareTo(y.Source.MappingFunctionId);
            if (value != 0) return value;
            value = x.Source.ValueCurveId.CompareTo(y.Source.ValueCurveId);
            if (value != 0) return value;
            value = x.Source.EnvelopeId.CompareTo(y.Source.EnvelopeId);
            if (value != 0) return value;
            value = x.Source.MidiChannelRootId.CompareTo(y.Source.MidiChannelRootId);
            if (value != 0) return value;
            value = x.Source.PureMidiTrackId.CompareTo(y.Source.PureMidiTrackId);
            if (value != 0) return value;
            value = x.Source.MidiSegmentId.CompareTo(y.Source.MidiSegmentId);
            if (value != 0) return value;
            value = x.Source.DirectMidiObjectId.CompareTo(y.Source.DirectMidiObjectId);
            if (value != 0) return value;
            value = x.ExportTrackId.CompareTo(y.ExportTrackId);
            if (value != 0) return value;
            value = x.SemanticTargetKey.CompareTo(y.SemanticTargetKey);
            if (value != 0) return value;
            value = x.SemanticGroup.CompareTo(y.SemanticGroup);
            if (value != 0) return value;
            value = x.Message.PackedValue.CompareTo(y.Message.PackedValue);
            if (value != 0) return value;
            value = x.Source.Tick.CompareTo(y.Source.Tick);
            if (value != 0) return value;
            return x.Source.Origin.CompareTo(y.Source.Origin);
        }
    }
}

internal static class SourceFingerprint
{
    private const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static long ForTrackContext(
        LogicalTrack track,
        IReadOnlyDictionary<MidoraId, EventInstrument> instruments,
        MidoraProject project,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ulong hash = Offset;
        Add(ref hash, project.TicksPerQuarterNote);
        AddState(ref hash, project.GlobalInitialState);
        AddState(ref hash, project.GlobalResetDefaults);
        Add(ref hash, track.Id);
        Add(ref hash, track.EventInstrumentUsageId ?? default);
        MidoraId? eventInstrumentId = project.ResolveEventInstrumentDefinitionId(track);
        Add(ref hash, eventInstrumentId ?? default);
        if (eventInstrumentId.HasValue
            && instruments.TryGetValue(eventInstrumentId.Value, out EventInstrument? instrument))
        {
            AddInstrument(ref hash, instrument, cancellationToken);
        }
        return unchecked((long)hash);
    }

    public static long ForSegment(
        Segment segment,
        CancellationToken cancellationToken = default)
    {
        ulong hash = Offset;
        Add(ref hash, segment.Id);
        Add(ref hash, segment.ProjectStartTick);
        Add(ref hash, segment.LengthTicks);
        Add(ref hash, segment.ContentOffsetTick);
        foreach (LogicalNote note in segment.Notes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add(ref hash, note.Id);
            Add(ref hash, note.StartTick);
            Add(ref hash, note.LengthTicks);
            Add(ref hash, note.Note);
            Add(ref hash, note.Velocity);
        }
        foreach (LogicalParameterLane lane in segment.ParameterLanes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add(ref hash, lane.Id);
            Add(ref hash, lane.ParameterId);
            foreach (CurvePoint point in lane.Points)
            {
                Add(ref hash, point.Id);
                Add(ref hash, point.Tick);
                Add(ref hash, BitConverter.DoubleToInt64Bits(point.Value));
                Add(ref hash, (int)point.Interpolation);
            }
        }
        return unchecked((long)hash);
    }

    public static long ForResult(
        long start,
        long end,
        ReadOnlySpan<CanonicalMidiEvent> events,
        CanonicalConductor conductor,
        ReadOnlySpan<CanonicalSmfTrackDescriptor> smfTracks = default,
        ReadOnlySpan<CanonicalOpaqueMidiEvent> opaqueMidiEvents = default,
        CancellationToken cancellationToken = default)
    {
        ulong hash = Offset;
        Add(ref hash, start);
        Add(ref hash, end);
        Add(ref hash, conductor.EndMarkerTick ?? -1);
        Add(ref hash, conductor.EndMarker?.Id ?? default);
        foreach (CanonicalTempo value in conductor.Tempos)
        {
            Add(ref hash, value.SourceId);
            Add(ref hash, value.Tick);
            Add(ref hash, value.BeatsPerMinute);
            Add(ref hash, value.IsRangeRestore ? 1 : 0);
        }
        foreach (CanonicalTimeSignature value in conductor.TimeSignatures)
        {
            Add(ref hash, value.SourceId);
            Add(ref hash, value.Tick);
            Add(ref hash, value.Numerator);
            Add(ref hash, value.Denominator);
            Add(ref hash, value.IsRangeRestore ? 1 : 0);
        }
        foreach (CanonicalTimeSignature value in conductor.SourceTimeSignatureMap)
        {
            Add(ref hash, value.SourceId);
            Add(ref hash, value.Tick);
            Add(ref hash, value.Numerator);
            Add(ref hash, value.Denominator);
        }
        foreach (CanonicalKeySignature value in conductor.KeySignatures)
        {
            Add(ref hash, value.SourceId);
            Add(ref hash, value.Tick);
            Add(ref hash, value.SharpsFlats);
            Add(ref hash, value.IsMinor ? 1 : 0);
            Add(ref hash, value.IsRangeRestore ? 1 : 0);
        }
        foreach (CanonicalMarker value in conductor.Markers)
        {
            Add(ref hash, value.Id);
            Add(ref hash, value.Tick);
            Add(ref hash, value.Name);
        }
        foreach (CanonicalSmfTrackDescriptor value in smfTracks)
        {
            Add(ref hash, value.ExportTrackId);
            Add(ref hash, (int)value.Kind);
            Add(ref hash, value.Name);
            Add(ref hash, value.ZeroBasedPort);
            Add(ref hash, value.ZeroBasedChannel);
            Add(ref hash, value.EndTick);
            Add(ref hash, value.MidiChannelRootId);
            Add(ref hash, value.SourceTrackId);
            Add(ref hash, (int)value.ChannelMode);
            Add(ref hash, value.MidiChannelRootName);
            Add(ref hash, value.MidiChannelRootOrder);
            Add(ref hash, value.SourceTrackOrder);
            Add(ref hash, (int)value.RoutingMode);
        }
        foreach (CanonicalOpaqueMidiEvent value in opaqueMidiEvents)
        {
            Add(ref hash, value.ExportTrackId);
            Add(ref hash, value.Tick);
            Add(ref hash, (int)value.Kind);
            Add(ref hash, value.MetaType);
            foreach (byte item in value.Payload.Span)
            {
                Add(ref hash, item);
            }
            Add(ref hash, value.StableOrder);
            Add(ref hash, value.SmfTrackOrder);
        }
        for (int eventIndex = 0; eventIndex < events.Length; eventIndex++)
        {
            if ((eventIndex & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            CanonicalMidiEvent value = events[eventIndex];
            Add(ref hash, value.Tick);
            Add(ref hash, value.ZeroBasedPort);
            Add(ref hash, value.ZeroBasedChannel);
            Add(ref hash, unchecked((long)value.Message.PackedValue));
            Add(ref hash, (int)value.Role);
            Add(ref hash, value.StableOrder);
            Add(ref hash, value.SemanticTargetKey);
            Add(ref hash, value.SemanticGroup);
            Add(ref hash, value.Source.TrackId);
            Add(ref hash, value.Source.SegmentId);
            Add(ref hash, value.Source.LogicalNoteId);
            Add(ref hash, value.Source.EventInstrumentId);
            Add(ref hash, value.Source.SubVoiceId);
            Add(ref hash, value.Source.SourceEventId);
            Add(ref hash, value.Source.Tick);
            Add(ref hash, value.Source.LogicalParameterId);
            Add(ref hash, value.Source.LogicalParameterMappingId);
            Add(ref hash, value.Source.MappingStepId);
            Add(ref hash, value.Source.MappingFunctionId);
            Add(ref hash, value.Source.ValueCurveId);
            Add(ref hash, value.Source.EnvelopeId);
            Add(ref hash, (int)value.Source.Origin);
            Add(ref hash, value.Source.MidiChannelRootId);
            Add(ref hash, value.Source.PureMidiTrackId);
            Add(ref hash, value.Source.MidiSegmentId);
            Add(ref hash, value.Source.DirectMidiObjectId);
            Add(ref hash, value.ExportTrackId);
            Add(ref hash, value.SmfTrackOrder);
            Add(ref hash, value.SmfEventOrder);
        }
        return unchecked((long)hash);
    }

    public static long CombinePaged(long canonicalFingerprint, string pageFingerprint)
    {
        ArgumentNullException.ThrowIfNull(pageFingerprint);
        ulong hash = Offset;
        Add(ref hash, canonicalFingerprint);
        Add(ref hash, pageFingerprint);
        return unchecked((long)hash);
    }

    private static void AddInstrument(
        ref ulong hash,
        EventInstrument instrument,
        CancellationToken cancellationToken)
    {
        Add(ref hash, instrument.Id);
        // These display names are part of MappingContextV2 and can therefore affect
        // a bounded Mapping Function expression's returned value.
        Add(ref hash, instrument.Name);
        Add(ref hash, instrument.RootNote);
        Add(ref hash, instrument.TemplateLengthTicks);
        Add(ref hash, instrument.PreRollTicks);
        Add(ref hash, instrument.RequiresChannelIsolation ? 1 : 0);
        Add(ref hash, (int)instrument.OverlapPolicy);
        Add(ref hash, (int)instrument.OverlapScope);
        Add(ref hash, (int)instrument.ShortLifecycle);
        Add(ref hash, (int)instrument.LongLifecycle);
        Add(ref hash, instrument.LoopStartTick ?? -1);
        Add(ref hash, instrument.LoopEndTick ?? -1);
        AddState(ref hash, instrument.InitialState);
        foreach (LogicalParameterDefinition definition in instrument.LogicalParameters.OrderBy(value => value.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add(ref hash, definition.Id);
            Add(ref hash, definition.Name);
            Add(ref hash, (int)definition.Type);
            Add(ref hash, definition.Minimum);
            Add(ref hash, definition.Maximum);
            Add(ref hash, definition.DefaultValue);
            Add(ref hash, definition.UsesExplicitEnumValues ? 1 : 0);
            foreach (LogicalParameterEnumItem value in definition.EnumItems)
            {
                Add(ref hash, value.Id);
                Add(ref hash, value.Name);
                Add(ref hash, value.Value);
            }
        }
        foreach (SubVoice voice in instrument.SubVoices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add(ref hash, voice.Id);
            Add(ref hash, voice.Name ?? string.Empty);
            Add(ref hash, voice.RootNoteOverride ?? -1);
            AddState(ref hash, voice.InitialState);
            foreach (TemplateEvent value in voice.Events)
            {
                Add(ref hash, value.Id);
                Add(ref hash, (int)value.Kind);
                Add(ref hash, value.Tick);
                Add(ref hash, value.LengthTicks);
                Add(ref hash, value.Number);
                Add(ref hash, value.Value);
                Add(ref hash, value.SecondaryValue);
                if (value.Kind == TemplateEventKind.Bank)
                {
                    Add(ref hash, value.HasBankMsb ? 1 : 0);
                    Add(ref hash, value.HasBankLsb ? 1 : 0);
                }
                Add(ref hash, value.FollowPitchDelta ? 1 : 0);
            }
            foreach (SubVoiceEventMapping mapping in voice.EventMappings
                .OrderBy(value => value.Target.EventKind)
                .ThenBy(value => value.Target.EventNumber)
                .ThenBy(value => value.Target.Parameter))
            {
                Add(ref hash, (int)mapping.Target.EventKind);
                Add(ref hash, mapping.Target.EventNumber);
                Add(ref hash, (int)mapping.Target.Parameter);
                AddChain(ref hash, mapping.Steps);
                AddTargetSettings(ref hash, mapping.TargetSettings);
            }
            foreach (ValueCurve curve in voice.Curves)
            {
                Add(ref hash, curve.Id);
                Add(ref hash, (int)curve.Target.Kind);
                Add(ref hash, curve.Target.Number);
                AddTargetSettings(ref hash, curve.TargetSettings);
                foreach (CurvePoint point in curve.Points)
                {
                    Add(ref hash, point.Id);
                    Add(ref hash, point.Tick);
                    Add(ref hash, point.Value);
                    Add(ref hash, (int)point.Interpolation);
                }
            }
        }
        foreach (LogicalParameterMapping mapping in instrument.ParameterMappings)
        {
            Add(ref hash, mapping.Id);
            Add(ref hash, mapping.ParameterId);
            Add(ref hash, mapping.SubVoiceId);
            Add(ref hash, (int)mapping.Target.Kind);
            Add(ref hash, mapping.Target.Number);
            AddChain(ref hash, mapping.Steps);
            AddTargetSettings(ref hash, mapping.TargetSettings);
        }
        foreach (CSharpMappingFunction function in instrument.MappingFunctions.OrderBy(value => value.Id))
        {
            Add(ref hash, function.Id);
            Add(ref hash, function.Name);
            Add(ref hash, function.AbiVersion);
            Add(ref hash, function.Body);
            foreach (string field in function.DeclaredContextFields.Order(StringComparer.Ordinal)) Add(ref hash, field);
        }
        foreach (InstrumentEnvelope envelope in instrument.Envelopes)
        {
            Add(ref hash, envelope.Id);
            Add(ref hash, envelope.Name ?? string.Empty);
            Add(ref hash, envelope.DelayTicks);
            Add(ref hash, envelope.AttackTicks);
            Add(ref hash, envelope.HoldTicks);
            Add(ref hash, envelope.DecayTicks);
            Add(ref hash, envelope.ReleaseTicks);
            Add(ref hash, envelope.StartValue);
            Add(ref hash, envelope.PeakValue);
            Add(ref hash, envelope.SustainValue);
            Add(ref hash, envelope.EndValue);
        }
    }

    private static void AddState(ref ulong hash, MidiInitialState state)
    {
        Add(ref hash, state.BankMsb ?? -1);
        Add(ref hash, state.BankLsb ?? -1);
        Add(ref hash, state.Program ?? -1);
        Add(ref hash, state.PitchBend ?? int.MinValue);
        Add(ref hash, state.PitchBendRangeSemitones ?? -1);
        Add(ref hash, state.PitchBendRangeCents ?? -1);
        foreach ((int key, int value) in state.Controllers.OrderBy(value => value.Key))
        {
            Add(ref hash, key); Add(ref hash, value);
        }
        foreach ((int key, int value) in state.RegisteredParameters.OrderBy(value => value.Key))
        {
            Add(ref hash, key); Add(ref hash, value);
        }
        foreach ((int key, int value) in state.NonRegisteredParameters.OrderBy(value => value.Key))
        {
            Add(ref hash, key); Add(ref hash, value);
        }
    }

    private static void AddMapping(ref ulong hash, ValueMappingStep step)
    {
        Add(ref hash, step.Id);
        Add(ref hash, step.IsEnabled ? 1 : 0);
        Add(ref hash, (int)step.Source);
        Add(ref hash, (int)step.Operation);
        Add(ref hash, step.LogicalParameterId ?? default);
        Add(ref hash, step.EnvelopeId ?? default);
        Add(ref hash, step.MappingFunctionId ?? default);
        Add(ref hash, step.Constant);
        Add(ref hash, step.SourceMinimum);
        Add(ref hash, step.SourceMaximum);
        Add(ref hash, step.TargetMinimum);
        Add(ref hash, step.TargetMaximum);
        Add(ref hash, (int)step.InputOverflow);
        Add(ref hash, (int)step.DivideByZero);
    }

    private static void AddTargetSettings(ref ulong hash, MidiIntegerTargetSettings settings)
    {
        Add(ref hash, (int)settings.Rounding);
        Add(ref hash, (int)settings.Overflow);
    }

    private static void AddChain(ref ulong hash, MappingChain chain)
    {
        Add(ref hash, chain.Id);
        Add(ref hash, chain.IsEnabled ? 1 : 0);
        foreach (ValueMappingStep step in chain) AddMapping(ref hash, step);
    }

    private static void Add(ref ulong hash, MidoraId value)
    {
        Add(ref hash, value.Value);
    }
    private static void Add(ref ulong hash, string value)
    {
        foreach (char item in value)
        {
            Add(ref hash, item);
        }
        Add(ref hash, 0);
    }
    private static void Add(ref ulong hash, long value)
    {
        ulong bits = unchecked((ulong)value);
        for (int i = 0; i < 8; i++)
        {
            Add(ref hash, (byte)(bits >> (i * 8)));
        }
    }
    private static void Add(ref ulong hash, int value) => Add(ref hash, (long)value);
    private static void Add(ref ulong hash, double value) => Add(ref hash, BitConverter.DoubleToInt64Bits(value));
    private static void Add(ref ulong hash, decimal value)
    {
        foreach (int bits in decimal.GetBits(value))
        {
            Add(ref hash, bits);
        }
    }
    private static void Add(ref ulong hash, byte value)
    {
        hash ^= value;
        hash *= Prime;
    }
}
