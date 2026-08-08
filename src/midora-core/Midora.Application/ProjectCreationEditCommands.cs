using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand CreateLogicalTrack(
        string? name = null,
        MidoraId? eventInstrumentId = null,
        int? insertionIndex = null) =>
        Command("Create logical track", project =>
        {
            EventInstrument? instrument = eventInstrumentId.HasValue
                ? FindEventInstrument(project, eventInstrumentId.Value)
                : null;
            string normalizedName = ProjectTextRules.NormalizeShortText(
                name ?? instrument?.Name ?? string.Empty,
                allowEmpty: true,
                nameof(name));
            int index = insertionIndex ?? project.Tracks.Count;
            ValidateInsertionIndex(index, project.Tracks.Count, nameof(insertionIndex));
            return DeferredCreate(
                EverythingChange(),
                value =>
                {
                    LogicalTrack track = new(value)
                    {
                        Name = normalizedName,
                        EventInstrumentId = instrument?.Id,
                        LastBoundEventInstrumentName = instrument?.Name
                    };
                    value.Tracks.Insert(index, track);
                    return track;
                },
                (value, track) =>
                {
                    EnsureLogicalTrackIdAvailable(value, track.Id);
                    InsertAt(value.Tracks, index, track, "Logical Track");
                },
                (value, track) => RemoveRequired(value.Tracks, track, "Logical Track"));
        });

    public static IProjectEditCommand DuplicateLogicalTrack(
        MidoraId trackId,
        string? name = null) =>
        Command("Duplicate logical track", project =>
        {
            LogicalTrack source = FindTrack(project, trackId);
            string copyName = ProjectTextRules.NormalizeShortText(
                name ?? $"{source.Name} Copy",
                allowEmpty: true,
                nameof(name));
            int index = project.Tracks.IndexOf(source) + 1;
            return DeferredCreate(
                EverythingChange(),
                value =>
                {
                    LogicalTrack copy = CloneLogicalTrack(value, source, copyName);
                    value.Tracks.Insert(index, copy);
                    return copy;
                },
                (value, copy) =>
                {
                    EnsureLogicalTrackIdAvailable(value, copy.Id);
                    InsertAt(value.Tracks, index, copy, "Logical Track");
                },
                (value, copy) => RemoveRequired(value.Tracks, copy, "Logical Track"));
        });

    public static IProjectEditCommand CreateEventInstrumentFolder(
        string name,
        int? insertionIndex = null) =>
        Command("Create event instrument folder", project =>
        {
            string normalized = EventInstrumentLibrary.ValidateFolderName(project, name, default);
            int index = insertionIndex ?? project.EventInstrumentFolders.Count;
            ValidateInsertionIndex(index, project.EventInstrumentFolders.Count, nameof(insertionIndex));
            return DeferredCreate(
                NoCompilationChange(),
                value =>
                {
                    EventInstrumentLibraryFolder folder =
                        EventInstrumentLibrary.CreateFolder(value, normalized);
                    Move(value.EventInstrumentFolders, folder, index);
                    return folder;
                },
                (value, folder) =>
                {
                    EnsureFolderIdAvailable(value, folder.Id);
                    InsertAt(value.EventInstrumentFolders, index, folder, "Event Instrument folder");
                },
                (value, folder) => RemoveRequired(
                    value.EventInstrumentFolders,
                    folder,
                    "Event Instrument folder"));
        });

    public static IProjectEditCommand CreateEventInstrument(
        string? name = null,
        MidoraId? folderId = null,
        int? insertionIndex = null) =>
        Command("Create event instrument", project =>
        {
            if (folderId.HasValue)
            {
                _ = FindFolder(project, folderId.Value);
            }
            string? normalized = name is null
                ? null
                : EventInstrumentLibrary.ValidateUniqueName(project, name, default);
            int index = insertionIndex ?? project.EventInstruments.Count;
            ValidateInsertionIndex(index, project.EventInstruments.Count, nameof(insertionIndex));
            return DeferredCreate(
                EverythingChange(),
                value =>
                {
                    EventInstrument instrument = EventInstrumentLibrary.Create(value, normalized);
                    instrument.LibraryFolderId = folderId;
                    Move(value.EventInstruments, instrument, index);
                    return instrument;
                },
                (value, instrument) =>
                {
                    EnsureEventInstrumentIdAvailable(value, instrument.Id);
                    InsertAt(value.EventInstruments, index, instrument, "Event Instrument");
                },
                (value, instrument) => RemoveRequired(
                    value.EventInstruments,
                    instrument,
                    "Event Instrument"));
        });

    public static IProjectEditCommand DuplicateEventInstrument(
        MidoraId eventInstrumentId,
        string? name = null) =>
        Command("Duplicate event instrument", project =>
        {
            EventInstrument source = FindEventInstrument(project, eventInstrumentId);
            string? normalized = name is null
                ? null
                : EventInstrumentLibrary.ValidateUniqueName(project, name, default);
            int index = project.EventInstruments.IndexOf(source) + 1;
            return DeferredCreate(
                EverythingChange(),
                value =>
                {
                    EventInstrument copy = EventInstrumentLibrary.Duplicate(
                        value,
                        source.Id,
                        normalized);
                    Move(value.EventInstruments, copy, index);
                    return copy;
                },
                (value, copy) =>
                {
                    EnsureEventInstrumentIdAvailable(value, copy.Id);
                    InsertAt(value.EventInstruments, index, copy, "Event Instrument");
                },
                (value, copy) => RemoveRequired(
                    value.EventInstruments,
                    copy,
                    "Event Instrument"));
        });

    public static IProjectEditCommand CreateSegment(
        MidoraId trackId,
        long projectStartTick,
        long lengthTicks,
        long contentOffsetTick = 0) =>
        Command("Create segment", project =>
        {
            LogicalTrack track = FindTrack(project, trackId);
            ValidateSegmentRange(projectStartTick, lengthTicks, contentOffsetTick);
            EnsureNoSegmentOverlap(track, null, projectStartTick, lengthTicks);
            return DeferredCreate(
                TrackChange(track.Id),
                value =>
                {
                    Segment segment = new(value)
                    {
                        ProjectStartTick = projectStartTick,
                        LengthTicks = lengthTicks,
                        ContentOffsetTick = contentOffsetTick
                    };
                    InsertSegmentByTime(track.Segments, segment);
                    return segment;
                },
                (_, segment) => InsertSegmentByTime(track.Segments, segment),
                (_, segment) => RemoveRequired(track.Segments, segment, "Segment"));
        });

    public static IProjectEditCommand DuplicateSegment(
        MidoraId segmentId,
        MidoraId targetTrackId,
        long newProjectStartTick) =>
        Command("Duplicate segment", project =>
        {
            SegmentLocation source = FindSegment(project, segmentId);
            LogicalTrack target = FindTrack(project, targetTrackId);
            ValidateSegmentRange(
                newProjectStartTick,
                source.Segment.LengthTicks,
                source.Segment.ContentOffsetTick);
            EnsureNoSegmentOverlap(target, null, newProjectStartTick, source.Segment.LengthTicks);
            return DeferredCreate(
                TrackChange(source.Track.Id, target.Id),
                value =>
                {
                    Segment copy = SegmentEditing.Duplicate(value, source.Segment);
                    copy.ProjectStartTick = newProjectStartTick;
                    InsertSegmentByTime(target.Segments, copy);
                    return copy;
                },
                (_, copy) => InsertSegmentByTime(target.Segments, copy),
                (_, copy) => RemoveRequired(target.Segments, copy, "Segment"));
        });

    public static IProjectEditCommand SplitSegment(
        MidoraId segmentId,
        long projectSplitTick) =>
        Command("Split segment", project =>
        {
            SegmentLocation source = FindSegment(project, segmentId);
            if (projectSplitTick <= source.Segment.ProjectStartTick
                || projectSplitTick >= source.Segment.ProjectRange.EndTick)
            {
                throw new ArgumentOutOfRangeException(nameof(projectSplitTick));
            }
            SegmentSplitResult? result = null;
            return Prepared(
                hasChanges: true,
                TrackChange(source.Track.Id),
                value =>
                {
                    if (result is null)
                    {
                        result = SegmentEditing.Split(value, source.Segment, projectSplitTick);
                    }
                    RequireContains(source.Track.Segments, source.Segment, "Segment");
                    source.Track.Segments.RemoveAt(source.Track.Segments.IndexOf(source.Segment));
                    source.Track.Segments.Insert(source.Index, result.Value.Left);
                    source.Track.Segments.Insert(source.Index + 1, result.Value.Right);
                },
                _ =>
                {
                    if (result is null)
                    {
                        throw new InvalidOperationException(
                            "A Segment split cannot be undone before its first Apply.");
                    }
                    RemoveRequired(source.Track.Segments, result.Value.Right, "right Segment");
                    RemoveRequired(source.Track.Segments, result.Value.Left, "left Segment");
                    InsertAt(source.Track.Segments, source.Index, source.Segment, "Segment");
                });
        });

    public static IProjectEditCommand CreateLogicalNote(
        MidoraId segmentId,
        long startTick,
        long lengthTicks,
        int note,
        int velocity,
        int? insertionIndex = null) =>
        Command("Create logical note", project =>
        {
            SegmentLocation segment = FindSegment(project, segmentId);
            ValidateLogicalNote(startTick, lengthTicks, note, velocity);
            int index = insertionIndex ?? segment.Segment.Notes.Count;
            ValidateInsertionIndex(index, segment.Segment.Notes.Count, nameof(insertionIndex));
            return DeferredCreate(
                TrackChange(segment.Track.Id),
                value =>
                {
                    LogicalNote logicalNote = new(value)
                    {
                        StartTick = startTick,
                        LengthTicks = lengthTicks,
                        Note = note,
                        Velocity = velocity
                    };
                    segment.Segment.Notes.Insert(index, logicalNote);
                    return logicalNote;
                },
                (_, logicalNote) => InsertAt(
                    segment.Segment.Notes,
                    index,
                    logicalNote,
                    "Logical Note"),
                (_, logicalNote) => RemoveRequired(
                    segment.Segment.Notes,
                    logicalNote,
                    "Logical Note"));
        });

    public static IProjectEditCommand CreateLogicalParameterLane(
        MidoraId segmentId,
        MidoraId parameterId) =>
        Command("Create logical parameter lane", project =>
        {
            SegmentLocation segment = FindSegment(project, segmentId);
            _ = FindBoundLogicalParameter(project, segment.Track, parameterId);
            LogicalParameterLane? existing = segment.Segment.ParameterLanes
                .SingleOrDefault(value => value.ParameterId == parameterId);
            if (existing is not null)
            {
                return Prepared(
                    hasChanges: false,
                    TrackChange(segment.Track.Id),
                    _ => { },
                    _ => { });
            }
            int index = segment.Segment.ParameterLanes.Count;
            return DeferredCreate(
                TrackChange(segment.Track.Id),
                value =>
                {
                    LogicalParameterLane lane = new(value) { ParameterId = parameterId };
                    segment.Segment.ParameterLanes.Add(lane);
                    return lane;
                },
                (_, lane) => InsertAt(
                    segment.Segment.ParameterLanes,
                    index,
                    lane,
                    "Logical Parameter Lane"),
                (_, lane) => RemoveRequired(
                    segment.Segment.ParameterLanes,
                    lane,
                    "Logical Parameter Lane"));
        });

    public static IProjectEditCommand CreateLogicalParameterPoint(
        MidoraId segmentId,
        MidoraId laneId,
        long tick,
        double value,
        CurveInterpolation interpolation) =>
        Command("Create logical parameter point", project =>
        {
            SegmentLocation segment = FindSegment(project, segmentId);
            LogicalParameterLane lane = FindLogicalParameterLane(segment.Segment, laneId);
            if (tick < 0 || !double.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(tick < 0 ? nameof(tick) : nameof(value));
            }
            if (!Enum.IsDefined(interpolation))
            {
                throw new ArgumentOutOfRangeException(nameof(interpolation));
            }
            if (lane.Points.Any(candidate => candidate.Tick == tick))
            {
                throw new InvalidOperationException(
                    "Only one Logical Parameter point is allowed at a tick.");
            }
            LogicalParameterDefinition definition = FindBoundLogicalParameter(
                project,
                segment.Track,
                lane.ParameterId);
            ValidatePointValue(definition, value, interpolation);
            return DeferredCreate(
                TrackChange(segment.Track.Id),
                owner =>
                {
                    CurvePoint point = new(owner, tick, value, interpolation);
                    InsertCurvePoint(lane.Points, point);
                    return point;
                },
                (_, point) => InsertCurvePoint(lane.Points, point),
                (_, point) => RemoveRequired(lane.Points, point, "Logical Parameter point"));
        });

    public static IProjectEditCommand CreateTempo(long tick, decimal beatsPerMinute) =>
        Command("Create tempo", project =>
        {
            ValidateConductorTick(tick, nameof(tick));
            ValidateTempo(beatsPerMinute);
            EnsureUniqueTick(project.Conductor.Tempos, default, tick, value => value.Id, value => value.Tick);
            return DeferredCreate(
                ConductorChange(),
                value =>
                {
                    TempoChange tempo = new(value, tick, beatsPerMinute);
                    InsertConductorEvent(project.Conductor.Tempos, tempo, item => item.Tick, item => item.Id);
                    return tempo;
                },
                (_, tempo) => InsertConductorEvent(
                    project.Conductor.Tempos,
                    tempo,
                    item => item.Tick,
                    item => item.Id),
                (_, tempo) => RemoveRequired(project.Conductor.Tempos, tempo, "Tempo"));
        });

    public static IProjectEditCommand CreateTimeSignature(
        long tick,
        int numerator,
        int denominator) =>
        Command("Create time signature", project =>
        {
            ValidateConductorTick(tick, nameof(tick));
            if (numerator is < 1 or > 99)
            {
                throw new ArgumentOutOfRangeException(nameof(numerator));
            }
            if (denominator is not (1 or 2 or 4 or 8 or 16 or 32 or 64))
            {
                throw new ArgumentOutOfRangeException(nameof(denominator));
            }
            ProjectTimeSignatureRules.ValidateCompatibility(
                project.TicksPerQuarterNote,
                denominator,
                nameof(denominator));
            EnsureUniqueTick(
                project.Conductor.TimeSignatures,
                default,
                tick,
                value => value.Id,
                value => value.Tick);
            return DeferredCreate(
                ConductorChange(),
                value =>
                {
                    TimeSignatureChange signature = new(value, tick, numerator, denominator);
                    InsertConductorEvent(
                        project.Conductor.TimeSignatures,
                        signature,
                        item => item.Tick,
                        item => item.Id);
                    return signature;
                },
                (_, signature) => InsertConductorEvent(
                    project.Conductor.TimeSignatures,
                    signature,
                    item => item.Tick,
                    item => item.Id),
                (_, signature) => RemoveRequired(
                    project.Conductor.TimeSignatures,
                    signature,
                    "Time Signature"));
        });

    public static IProjectEditCommand CreateKeySignature(
        long tick,
        int sharpsFlats,
        bool isMinor) =>
        Command("Create key signature", project =>
        {
            ValidateConductorTick(tick, nameof(tick));
            if (sharpsFlats is < -7 or > 7)
            {
                throw new ArgumentOutOfRangeException(nameof(sharpsFlats));
            }
            EnsureUniqueTick(
                project.Conductor.KeySignatures,
                default,
                tick,
                value => value.Id,
                value => value.Tick);
            return DeferredCreate(
                ConductorChange(),
                value =>
                {
                    KeySignatureChange signature = new(value, tick, sharpsFlats, isMinor);
                    InsertConductorEvent(
                        project.Conductor.KeySignatures,
                        signature,
                        item => item.Tick,
                        item => item.Id);
                    return signature;
                },
                (_, signature) => InsertConductorEvent(
                    project.Conductor.KeySignatures,
                    signature,
                    item => item.Tick,
                    item => item.Id),
                (_, signature) => RemoveRequired(
                    project.Conductor.KeySignatures,
                    signature,
                    "Key Signature"));
        });

    public static IProjectEditCommand CreateProjectMarker(long tick, string name) =>
        Command("Create project marker", project =>
        {
            ValidateConductorTick(tick, nameof(tick));
            string normalized = ProjectTextRules.NormalizeShortText(
                name,
                allowEmpty: true,
                nameof(name));
            return DeferredCreate(
                ConductorChange(),
                value =>
                {
                    ProjectMarker marker = new(value, tick, normalized);
                    InsertConductorEvent(
                        project.Conductor.Markers,
                        marker,
                        item => item.Tick,
                        item => item.Id);
                    return marker;
                },
                (_, marker) => InsertConductorEvent(
                    project.Conductor.Markers,
                    marker,
                    item => item.Tick,
                    item => item.Id),
                (_, marker) => RemoveRequired(
                    project.Conductor.Markers,
                    marker,
                    "Project Marker"));
        });

    public static IProjectEditCommand CreateProjectEndMarker(long tick) =>
        Command("Create project end marker", project =>
        {
            ValidateConductorTick(tick, nameof(tick));
            if (project.Conductor.EndMarker is not null)
            {
                throw new InvalidOperationException("The Project End Marker already exists.");
            }
            return DeferredCreate(
                ConductorChange(),
                value =>
                {
                    ProjectEndMarker marker = new(value, tick);
                    value.Conductor.EndMarker = marker;
                    return marker;
                },
                (value, marker) =>
                {
                    if (value.Conductor.EndMarker is not null)
                    {
                        throw new InvalidOperationException(
                            "The Project End Marker already exists.");
                    }
                    value.Conductor.EndMarker = marker;
                },
                (value, marker) =>
                {
                    if (!ReferenceEquals(value.Conductor.EndMarker, marker))
                    {
                        throw new InvalidOperationException(
                            "The Project End Marker is no longer present.");
                    }
                    value.Conductor.EndMarker = null;
                });
        });

    private static LogicalTrack CloneLogicalTrack(
        MidoraProject project,
        LogicalTrack source,
        string name)
    {
        LogicalTrack copy = new(project)
        {
            Name = name,
            EventInstrumentId = source.EventInstrumentId,
            LastBoundEventInstrumentName = source.LastBoundEventInstrumentName,
            ColorOverride = source.ColorOverride
        };
        foreach (Segment segment in source.Segments)
        {
            copy.Segments.Add(SegmentEditing.Duplicate(project, segment));
        }
        return copy;
    }

    private static void InsertCurvePoint(List<CurvePoint> points, CurvePoint point)
    {
        if (points.Any(value => value.Id == point.Id || value.Tick == point.Tick))
        {
            throw new InvalidOperationException(
                "The Logical Parameter point ID or tick is already present.");
        }
        int index = points.FindIndex(value =>
            value.Tick > point.Tick
            || value.Tick == point.Tick && value.Id.CompareTo(point.Id) > 0);
        points.Insert(index < 0 ? points.Count : index, point);
    }

    private static void InsertConductorEvent<T>(
        List<T> events,
        T item,
        Func<T, long> getTick,
        Func<T, MidoraId> getId)
        where T : class
    {
        MidoraId id = getId(item);
        if (events.Any(value => getId(value) == id))
        {
            throw new InvalidOperationException("The Conductor event ID is already present.");
        }
        int index = events.FindIndex(value =>
            getTick(value) > getTick(item)
            || getTick(value) == getTick(item) && getId(value).CompareTo(id) > 0);
        events.Insert(index < 0 ? events.Count : index, item);
    }
}
