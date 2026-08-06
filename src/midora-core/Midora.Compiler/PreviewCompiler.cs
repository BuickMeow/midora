using Midora.Domain;

namespace Midora.Compiler;

public sealed record EventInstrumentPreviewRequest(
    MidoraId EventInstrumentId,
    MidoraId? SubVoiceId = null,
    int? Pitch = null,
    int Velocity = 100,
    long? GateLengthTicks = null,
    decimal? Tempo = null,
    long CursorTick = 0);

public sealed class PreviewCompiler
{
    public CanonicalCompiledResult CompileEventInstrument(
        MidoraProject source,
        EventInstrumentPreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);

        EventInstrument instrument = source.EventInstruments.FirstOrDefault(value => value.Id == request.EventInstrumentId)
            ?? throw new ArgumentException("The Event Instrument does not belong to the Project.", nameof(request));
        SubVoice? selectedVoice = request.SubVoiceId.HasValue
            ? instrument.SubVoices.FirstOrDefault(value => value.Id == request.SubVoiceId.Value)
            : null;
        if (request.SubVoiceId.HasValue && selectedVoice is null)
        {
            throw new ArgumentException("The SubVoice does not belong to the Event Instrument.", nameof(request));
        }

        int pitch = request.Pitch ?? selectedVoice?.RootNoteOverride ?? instrument.RootNote;
        long gateLength = request.GateLengthTicks ?? instrument.TemplateLengthTicks;
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

        long maximumRelease = instrument.Envelopes.Count == 0
            ? 0
            : instrument.Envelopes.Max(value => value.ReleaseTicks);
        long previewLength = checked(gateLength + instrument.TemplateLengthTicks + maximumRelease + 1);
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
            IncludedSubVoiceIds = selectedVoice is null ? null : [selectedVoice.Id]
        });
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
