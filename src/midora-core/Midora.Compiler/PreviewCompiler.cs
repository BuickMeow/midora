using Midora.Domain;

namespace Midora.Compiler;

public sealed record EventInstrumentPreviewRequest(
    MidoraId EventInstrumentId,
    MidoraId? SubVoiceId = null,
    int? Pitch = null,
    int Velocity = 100,
    long? GateLengthTicks = null,
    decimal? Tempo = null,
    long CursorTick = 0)
{
    public IReadOnlyCollection<MidoraId>? MutedSubVoiceIds { get; init; }

    public IReadOnlyCollection<MidoraId>? SoloSubVoiceIds { get; init; }
}

public sealed record SegmentNotePreviewRequest(
    MidoraId TrackId,
    MidoraId SegmentId,
    long StartTick,
    int Pitch,
    int Velocity = 100);

public sealed class PreviewCompiler
{
    public CanonicalCompiledResult CompileHeldSegmentNoteGateOpen(
        MidoraProject source,
        SegmentNotePreviewRequest request,
        long windowLengthTicks)
    {
        if (windowLengthTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowLengthTicks));
        }
        return CompileHeldSegmentNoteCore(
            source,
            request,
            noteGateLengthTicks: long.MaxValue,
            previewLengthTicks: windowLengthTicks,
            heldGateOpen: true,
            finalMappingGateLength: null);
    }

    public CanonicalCompiledResult CompileHeldSegmentNoteGateEnd(
        MidoraProject source,
        SegmentNotePreviewRequest request,
        long finalGateLengthTicks,
        long effectiveGateEndTick)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        if (finalGateLengthTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(finalGateLengthTicks));
        }
        if (effectiveGateEndTick <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(effectiveGateEndTick));
        }
        (_, _, EventInstrument instrument) = RequireSegmentNoteContext(source, request);
        long maximumRelease = instrument.Envelopes.Count == 0
            ? 0
            : instrument.Envelopes.Max(value => value.ReleaseTicks);
        long previewLength = AddPreviewDurationClamped(
            AddPreviewDurationClamped(
                AddPreviewDurationClamped(
                    effectiveGateEndTick,
                    Math.Max(instrument.TemplateLengthTicks, 0)),
                Math.Max(maximumRelease, 0)),
            1);
        return CompileHeldSegmentNoteCore(
            source,
            request,
            effectiveGateEndTick,
            previewLength,
            heldGateOpen: false,
            finalMappingGateLength: finalGateLengthTicks);
    }

    public CanonicalCompiledResult CompileHeldEventInstrumentGateOpen(
        MidoraProject source,
        EventInstrumentPreviewRequest request,
        long windowEndTick)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        if (request.GateLengthTicks.HasValue)
        {
            throw new ArgumentException(
                "A held-preview Gate Start cannot carry a final Gate Length.",
                nameof(request));
        }
        if (windowEndTick <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowEndTick));
        }

        return CompileEventInstrumentCore(
            source,
            request,
            long.MaxValue,
            windowEndTick,
            heldGateOpen: true,
            finalMappingGateLength: null);
    }

    public CanonicalCompiledResult CompileHeldEventInstrumentGateEnd(
        MidoraProject source,
        EventInstrumentPreviewRequest request,
        long finalGateLengthTicks,
        long effectiveGateEndTick)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        if (request.GateLengthTicks.HasValue)
        {
            throw new ArgumentException(
                "A held-preview request must not carry a second final Gate Length.",
                nameof(request));
        }
        if (finalGateLengthTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(finalGateLengthTicks));
        }
        if (effectiveGateEndTick <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(effectiveGateEndTick));
        }

        EventInstrument instrument = RequireInstrument(source, request, out _);
        long maximumRelease = instrument.Envelopes.Count == 0
            ? 0
            : instrument.Envelopes.Max(value => value.ReleaseTicks);
        long previewLength = AddPreviewDurationClamped(
            AddPreviewDurationClamped(
                AddPreviewDurationClamped(
                    effectiveGateEndTick,
                    Math.Max(instrument.TemplateLengthTicks, 0)),
                Math.Max(maximumRelease, 0)),
            1);
        return CompileEventInstrumentCore(
            source,
            request,
            effectiveGateEndTick,
            previewLength,
            heldGateOpen: false,
            finalMappingGateLength: finalGateLengthTicks);
    }

    public CanonicalCompiledResult CompileEventInstrument(
        MidoraProject source,
        EventInstrumentPreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);

        EventInstrument instrument = RequireInstrument(source, request, out _);
        long gateLength = request.GateLengthTicks ?? instrument.TemplateLengthTicks;
        long maximumRelease = instrument.Envelopes.Count == 0
            ? 0
            : instrument.Envelopes.Max(value => value.ReleaseTicks);
        long previewLength = AddPreviewDurationClamped(
            AddPreviewDurationClamped(
                AddPreviewDurationClamped(gateLength, Math.Max(instrument.TemplateLengthTicks, 0)),
                Math.Max(maximumRelease, 0)),
            1);
        return CompileEventInstrumentCore(
            source,
            request,
            gateLength,
            previewLength,
            heldGateOpen: false,
            finalMappingGateLength: null);
    }

    private CanonicalCompiledResult CompileEventInstrumentCore(
        MidoraProject source,
        EventInstrumentPreviewRequest request,
        long gateLength,
        long previewLength,
        bool heldGateOpen,
        long? finalMappingGateLength)
    {
        EventInstrument instrument = RequireInstrument(source, request, out SubVoice? selectedVoice);

        HashSet<MidoraId>? includedSubVoiceIds = ResolvePreviewSubVoices(instrument, request);

        int pitch = request.Pitch ?? selectedVoice?.RootNoteOverride ?? instrument.RootNote;
        decimal tempo = request.Tempo ?? GetTempoAt(source.Conductor, request.CursorTick);
        if (pitch is < 0 or > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Preview pitch must be in 0..127.");
        }
        if (request.Velocity is < 1 or > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Preview velocity must be in 1..127.");
        }
        if (gateLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Preview Gate Length must be positive.");
        }
        if (tempo <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Preview Tempo must be positive.");
        }

        MidoraProject context = CreateContextShell(source);
        context.Conductor.Tempos.Clear();
        context.Conductor.Tempos.Add(new TempoChange(context, 0, tempo));
        context.Conductor.TimeSignatures.Add(new TimeSignatureChange(context, 0, 4, 4));
        AddInstrumentContext(source, context, instrument);

        LogicalTrack track = new(context) { Name = "Event Instrument Preview", EventInstrumentId = instrument.Id };
        Segment segment = new(context) { ProjectStartTick = 0, ContentOffsetTick = 0, LengthTicks = previewLength };
        segment.Notes.Add(new LogicalNote(context)
        {
            StartTick = 0,
            LengthTicks = gateLength,
            Note = pitch,
            Velocity = request.Velocity
        });
        track.Segments.Add(segment);
        context.Tracks.Add(track);

        using MidoraCompiler compiler = new();
        return compiler.CompileFull(context, new CompilationRequest
        {
            Purpose = CompilationPurpose.EventInstrumentPreview,
            StartTick = 0,
            EndTick = previewLength,
            HeldPreviewGateOpen = heldGateOpen,
            HeldPreviewFinalGateLengthTicks = finalMappingGateLength,
            IncludedSubVoiceIds = selectedVoice is null
                ? includedSubVoiceIds
                : new HashSet<MidoraId> { selectedVoice.Id }
        });
    }

    private static EventInstrument RequireInstrument(
        MidoraProject source,
        EventInstrumentPreviewRequest request,
        out SubVoice? selectedVoice)
    {
        EventInstrument instrument = source.EventInstruments.FirstOrDefault(
            value => value.Id == request.EventInstrumentId)
            ?? throw new ArgumentException(
                "The Event Instrument does not belong to the Project.",
                nameof(request));
        selectedVoice = request.SubVoiceId.HasValue
            ? instrument.SubVoices.FirstOrDefault(value => value.Id == request.SubVoiceId.Value)
            : null;
        if (request.SubVoiceId.HasValue && selectedVoice is null)
        {
            throw new ArgumentException(
                "The SubVoice does not belong to the Event Instrument.",
                nameof(request));
        }
        return instrument;
    }

    private static (LogicalTrack Track, Segment Segment, EventInstrument Instrument)
        RequireSegmentNoteContext(
            MidoraProject source,
            SegmentNotePreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        LogicalTrack track = source.Tracks.FirstOrDefault(value => value.Id == request.TrackId)
            ?? throw new ArgumentException(
                "The Logical Track does not belong to the Project.",
                nameof(request));
        Segment segment = track.Segments.FirstOrDefault(value => value.Id == request.SegmentId)
            ?? throw new ArgumentException(
                "The Segment does not belong to the Logical Track.",
                nameof(request));
        if (!track.EventInstrumentId.HasValue)
        {
            throw new InvalidOperationException(
                "Segment Note preview requires a bound Event Instrument.");
        }
        EventInstrument instrument = source.EventInstruments.FirstOrDefault(
            value => value.Id == track.EventInstrumentId.Value)
            ?? throw new InvalidOperationException(
                "The Segment Note preview Event Instrument binding is missing or damaged.");
        if (request.StartTick < segment.ContentOffsetTick
            || request.StartTick >= segment.ContentEndTick)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "The draft Note start must be inside the Segment content range.");
        }
        if (request.Pitch is < 0 or > 127 || request.Velocity is < 1 or > 127)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Draft Note pitch must be 0..127 and velocity must be 1..127.");
        }
        return (track, segment, instrument);
    }

    private CanonicalCompiledResult CompileHeldSegmentNoteCore(
        MidoraProject source,
        SegmentNotePreviewRequest request,
        long noteGateLengthTicks,
        long previewLengthTicks,
        bool heldGateOpen,
        long? finalMappingGateLength)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        if (noteGateLengthTicks <= 0 || previewLengthTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(noteGateLengthTicks));
        }
        (LogicalTrack sourceTrack, Segment sourceSegment, EventInstrument instrument) =
            RequireSegmentNoteContext(source, request);
        long projectStartTick = checked(
            sourceSegment.ProjectStartTick
            + (request.StartTick - sourceSegment.ContentOffsetTick));
        long projectEndTick = projectStartTick >= long.MaxValue - previewLengthTicks
            ? long.MaxValue
            : projectStartTick + previewLengthTicks;
        long availableLength = projectEndTick - projectStartTick;
        if (availableLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(previewLengthTicks));
        }

        MidoraProject context = CreateContextShell(source);
        context.Conductor.Tempos.Clear();
        context.Conductor.Tempos.Add(new TempoChange(
            context,
            0,
            GetTempoAt(source.Conductor, projectStartTick)));
        context.Conductor.TimeSignatures.Add(new TimeSignatureChange(context, 0, 4, 4));
        AddInstrumentContext(source, context, instrument);
        LogicalTrack track = new(context)
        {
            Id = sourceTrack.Id,
            Name = sourceTrack.Name,
            EventInstrumentId = instrument.Id,
            LastBoundEventInstrumentName = sourceTrack.LastBoundEventInstrumentName
        };
        Segment segment = new(context)
        {
            Id = sourceSegment.Id,
            ProjectStartTick = projectStartTick,
            ContentOffsetTick = request.StartTick,
            LengthTicks = availableLength
        };
        segment.ParameterLanes.AddRange(sourceSegment.ParameterLanes);
        segment.Notes.Add(new LogicalNote(context)
        {
            StartTick = request.StartTick,
            LengthTicks = heldGateOpen ? availableLength : noteGateLengthTicks,
            Note = request.Pitch,
            Velocity = request.Velocity
        });
        track.Segments.Add(segment);
        context.Tracks.Add(track);

        using MidoraCompiler compiler = new();
        return compiler.CompileFull(context, new CompilationRequest
        {
            Purpose = CompilationPurpose.EventInstrumentPreview,
            StartTick = projectStartTick,
            EndTick = projectEndTick,
            HeldPreviewGateOpen = heldGateOpen,
            HeldPreviewFinalGateLengthTicks = finalMappingGateLength,
            IncludedTrackIds = [track.Id]
        });
    }

    private static HashSet<MidoraId>? ResolvePreviewSubVoices(
        EventInstrument instrument,
        EventInstrumentPreviewRequest request)
    {
        if (request.SubVoiceId.HasValue
            && (request.MutedSubVoiceIds is not null || request.SoloSubVoiceIds is not null))
        {
            throw new ArgumentException(
                "Dedicated SubVoice preview cannot also carry Event Instrument preview Mute/Solo state.",
                nameof(request));
        }
        if (request.SubVoiceId.HasValue
            || request.MutedSubVoiceIds is null && request.SoloSubVoiceIds is null)
        {
            return null;
        }

        HashSet<MidoraId> knownIds = instrument.SubVoices.Select(value => value.Id).ToHashSet();
        HashSet<MidoraId> muted = ValidatePreviewStateIds(
            request.MutedSubVoiceIds,
            knownIds,
            "Mute",
            request);
        HashSet<MidoraId> soloed = ValidatePreviewStateIds(
            request.SoloSubVoiceIds,
            knownIds,
            "Solo",
            request);
        bool hasSolo = soloed.Count > 0;
        return instrument.SubVoices
            .Where(value => !muted.Contains(value.Id) && (!hasSolo || soloed.Contains(value.Id)))
            .Select(value => value.Id)
            .ToHashSet();
    }

    private static HashSet<MidoraId> ValidatePreviewStateIds(
        IReadOnlyCollection<MidoraId>? ids,
        HashSet<MidoraId> knownIds,
        string stateName,
        EventInstrumentPreviewRequest request)
    {
        if (ids is null)
        {
            return [];
        }

        MidoraId[] snapshot = ids.ToArray();
        HashSet<MidoraId> result = new(snapshot);
        if (result.Count != snapshot.Length)
        {
            throw new ArgumentException(
                $"The SubVoice {stateName} state contains duplicate IDs.",
                nameof(request));
        }
        if (result.Any(value => !knownIds.Contains(value)))
        {
            throw new ArgumentException(
                $"The SubVoice {stateName} state references another Event Instrument.",
                nameof(request));
        }
        return result;
    }

    public CanonicalCompiledResult CompileSegment(
        MidoraProject source,
        MidoraId trackId,
        MidoraId segmentId)
    {
        ArgumentNullException.ThrowIfNull(source);
        LogicalTrack sourceTrack = source.Tracks.FirstOrDefault(value => value.Id == trackId)
            ?? throw new ArgumentException("The Logical Track does not belong to the Project.", nameof(trackId));
        Segment sourceSegment = sourceTrack.Segments.FirstOrDefault(value => value.Id == segmentId)
            ?? throw new ArgumentException("The Segment does not belong to the Logical Track.", nameof(segmentId));

        MidoraProject context = CreateContextShell(source);
        CopyConductor(source.Conductor, context);
        if (sourceTrack.EventInstrumentId.HasValue)
        {
            EventInstrument? instrument = source.EventInstruments.FirstOrDefault(
                value => value.Id == sourceTrack.EventInstrumentId.Value);
            if (instrument is not null)
            {
                AddInstrumentContext(source, context, instrument);
            }
            context.DamagedEventInstruments.AddRange(source.DamagedEventInstruments.Where(
                value => value.Id == sourceTrack.EventInstrumentId.Value));
        }
        LogicalTrack track = new(context)
        {
            Id = sourceTrack.Id,
            Name = sourceTrack.Name,
            EventInstrumentId = sourceTrack.EventInstrumentId,
            LastBoundEventInstrumentName = sourceTrack.LastBoundEventInstrumentName
        };
        track.Segments.Add(sourceSegment);
        context.Tracks.Add(track);

        long endTick = sourceSegment.LengthTicks > 0
            && sourceSegment.ProjectStartTick > long.MaxValue - sourceSegment.LengthTicks
                ? long.MaxValue
                : sourceSegment.ProjectStartTick + sourceSegment.LengthTicks;
        using MidoraCompiler compiler = new();
        return compiler.CompileFull(context, new CompilationRequest
        {
            Purpose = CompilationPurpose.SegmentPreview,
            StartTick = sourceSegment.ProjectStartTick,
            EndTick = endTick,
            IncludedTrackIds = [track.Id]
        });
    }

    private static MidoraProject CreateContextShell(MidoraProject source)
    {
        MidoraProject context = new(source.TicksPerQuarterNote, source.NextStableId);
        CopyState(source.GlobalInitialState, context.GlobalInitialState);
        CopyState(source.GlobalResetDefaults, context.GlobalResetDefaults);
        context.Playback.MasterVolumeDecibels = source.Playback.MasterVolumeDecibels;
        context.Playback.LimiterEnabled = source.Playback.LimiterEnabled;
        context.Playback.StopCursorBehavior = source.Playback.StopCursorBehavior;
        return context;
    }

    private static void AddInstrumentContext(
        MidoraProject source,
        MidoraProject context,
        EventInstrument instrument)
    {
        context.EventInstruments.Add(instrument);
        if (!instrument.LibraryFolderId.HasValue)
        {
            return;
        }
        EventInstrumentLibraryFolder? folder = source.EventInstrumentFolders.FirstOrDefault(
            value => value.Id == instrument.LibraryFolderId.Value);
        if (folder is not null)
        {
            context.EventInstrumentFolders.Add(folder);
        }
    }

    private static void CopyConductor(ConductorTrack source, MidoraProject targetProject)
    {
        ConductorTrack target = targetProject.Conductor;
        target.Tempos.Clear();
        target.Tempos.AddRange(source.Tempos);
        target.TimeSignatures.Clear();
        target.TimeSignatures.AddRange(source.TimeSignatures);
        target.KeySignatures.AddRange(source.KeySignatures);
        target.Markers.AddRange(source.Markers);
        target.EndMarker = source.EndMarker is null
            ? null
            : new ProjectEndMarker(targetProject, source.EndMarker.Tick) { Id = source.EndMarker.Id };
    }

    private static decimal GetTempoAt(ConductorTrack conductor, long tick) => conductor.Tempos
        .Where(value => value.Tick <= tick)
        .OrderBy(value => value.Tick)
        .LastOrDefault()?.BeatsPerMinute
        ?? conductor.Tempos.OrderBy(value => value.Tick).FirstOrDefault()?.BeatsPerMinute
        ?? 120m;

    private static long AddPreviewDurationClamped(long left, long right) =>
        right >= long.MaxValue - left
            ? long.MaxValue
            : left + right;

    private static void CopyState(MidiInitialState source, MidiInitialState target)
    {
        target.BankMsb = source.BankMsb;
        target.BankLsb = source.BankLsb;
        target.Program = source.Program;
        target.PitchBend = source.PitchBend;
        target.PitchBendRangeSemitones = source.PitchBendRangeSemitones;
        target.PitchBendRangeCents = source.PitchBendRangeCents;
        foreach ((int key, int value) in source.Controllers)
        {
            target.Controllers.Add(key, value);
        }
        foreach ((int key, int value) in source.RegisteredParameters)
        {
            target.RegisteredParameters.Add(key, value);
        }
        foreach ((int key, int value) in source.NonRegisteredParameters)
        {
            target.NonRegisteredParameters.Add(key, value);
        }
    }
}
