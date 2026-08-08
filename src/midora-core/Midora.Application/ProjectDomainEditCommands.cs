using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand RenameLogicalTrack(MidoraId trackId, string name) =>
        Command("Rename logical track", project =>
        {
            LogicalTrack track = FindTrack(project, trackId);
            string normalized = ProjectTextRules.NormalizeShortText(
                name,
                allowEmpty: true,
                nameof(name));
            string oldName = track.Name;
            return Prepared(
                !string.Equals(oldName, normalized, StringComparison.Ordinal),
                TrackChange(trackId),
                _ => track.Name = normalized,
                _ => track.Name = oldName);
        });

    public static IProjectEditCommand BindLogicalTrack(
        MidoraId trackId,
        MidoraId? eventInstrumentId) =>
        Command("Change logical track binding", project =>
        {
            LogicalTrack track = FindTrack(project, trackId);
            EventInstrument? target = eventInstrumentId.HasValue
                ? FindEventInstrument(project, eventInstrumentId.Value)
                : null;
            MidoraId? oldInstrumentId = track.EventInstrumentId;
            string? oldLastBoundName = track.LastBoundEventInstrumentName;
            string? newLastBoundName = target?.Name;
            if (target is null && oldInstrumentId.HasValue)
            {
                newLastBoundName = project.EventInstruments
                    .FirstOrDefault(value => value.Id == oldInstrumentId.Value)?.Name
                    ?? oldLastBoundName;
            }
            else if (target is null)
            {
                newLastBoundName = oldLastBoundName;
            }

            ProjectChangeSet changes = TrackChange(trackId);
            if (oldInstrumentId.HasValue)
            {
                changes.EventInstrumentIds.Add(oldInstrumentId.Value);
            }
            if (eventInstrumentId.HasValue)
            {
                changes.EventInstrumentIds.Add(eventInstrumentId.Value);
            }
            return Prepared(
                oldInstrumentId != eventInstrumentId,
                changes,
                _ =>
                {
                    track.EventInstrumentId = eventInstrumentId;
                    track.LastBoundEventInstrumentName = newLastBoundName;
                },
                _ =>
                {
                    track.EventInstrumentId = oldInstrumentId;
                    track.LastBoundEventInstrumentName = oldLastBoundName;
                });
        });

    public static IProjectEditCommand ReorderLogicalTrack(MidoraId trackId, int newIndex) =>
        Command("Reorder logical track", project =>
        {
            LogicalTrack track = FindTrack(project, trackId);
            int oldIndex = project.Tracks.IndexOf(track);
            ValidateExistingIndex(newIndex, project.Tracks.Count, nameof(newIndex));
            return Prepared(
                oldIndex != newIndex,
                EverythingChange(),
                value => Move(value.Tracks, track, newIndex),
                value => Move(value.Tracks, track, oldIndex));
        });

    public static IProjectEditCommand DeleteLogicalTrack(
        MidoraId trackId,
        bool nonEmptyDeletionConfirmed) =>
        Command("Delete logical track", project =>
        {
            LogicalTrack track = FindTrack(project, trackId);
            if (track.Segments.Count != 0 && !nonEmptyDeletionConfirmed)
            {
                throw new InvalidOperationException(
                    "Deleting a non-empty Logical Track requires explicit confirmation.");
            }
            int originalIndex = project.Tracks.IndexOf(track);
            bool wasExplicitlySelected = project.AudioRender.ExplicitLogicalTrackIds.Contains(trackId);
            return Prepared(
                hasChanges: true,
                EverythingChange(),
                value =>
                {
                    RemoveRequired(value.Tracks, track, "Logical Track");
                    value.AudioRender.ExplicitLogicalTrackIds.Remove(trackId);
                },
                value =>
                {
                    EnsureLogicalTrackIdAvailable(value, trackId);
                    InsertAt(value.Tracks, originalIndex, track, "Logical Track");
                    if (wasExplicitlySelected)
                    {
                        value.AudioRender.ExplicitLogicalTrackIds.Add(trackId);
                    }
                });
        });

    public static IProjectEditCommand RenameEventInstrument(
        MidoraId eventInstrumentId,
        string name) =>
        Command("Rename event instrument", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            string normalized = EventInstrumentLibrary.ValidateUniqueName(
                project,
                name,
                eventInstrumentId);
            string oldName = instrument.Name;
            InstrumentBinding[] bindings = project.Tracks
                .Where(value => value.EventInstrumentId == eventInstrumentId)
                .Select(value => new InstrumentBinding(
                    value,
                    value.EventInstrumentId,
                    value.LastBoundEventInstrumentName))
                .ToArray();
            return Prepared(
                !string.Equals(oldName, normalized, StringComparison.Ordinal),
                EventInstrumentChange(eventInstrumentId),
                _ =>
                {
                    instrument.Name = normalized;
                    foreach (InstrumentBinding binding in bindings)
                    {
                        binding.Track.LastBoundEventInstrumentName = normalized;
                    }
                },
                _ =>
                {
                    instrument.Name = oldName;
                    RestoreBindings(bindings);
                });
        });

    public static IProjectEditCommand ReorderEventInstrument(
        MidoraId eventInstrumentId,
        int newIndex) =>
        Command("Reorder event instrument", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            int oldIndex = project.EventInstruments.IndexOf(instrument);
            ValidateExistingIndex(newIndex, project.EventInstruments.Count, nameof(newIndex));
            return Prepared(
                oldIndex != newIndex,
                NoCompilationChange(),
                value => Move(value.EventInstruments, instrument, newIndex),
                value => Move(value.EventInstruments, instrument, oldIndex));
        });

    public static IProjectEditCommand MoveEventInstrumentToFolder(
        MidoraId eventInstrumentId,
        MidoraId? folderId) =>
        Command("Move event instrument to folder", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            if (folderId.HasValue)
            {
                _ = FindFolder(project, folderId.Value);
            }
            MidoraId? oldFolderId = instrument.LibraryFolderId;
            return Prepared(
                oldFolderId != folderId,
                NoCompilationChange(),
                _ => instrument.LibraryFolderId = folderId,
                _ => instrument.LibraryFolderId = oldFolderId);
        });

    public static IProjectEditCommand DeleteEventInstrument(
        MidoraId eventInstrumentId,
        bool referencedDeletionConfirmed) =>
        Command("Delete event instrument", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            InstrumentBinding[] bindings = project.Tracks
                .Where(value => value.EventInstrumentId == eventInstrumentId)
                .Select(value => new InstrumentBinding(
                    value,
                    value.EventInstrumentId,
                    value.LastBoundEventInstrumentName))
                .ToArray();
            if (bindings.Length != 0 && !referencedDeletionConfirmed)
            {
                throw new InvalidOperationException(
                    "Deleting a referenced Event Instrument requires explicit confirmation.");
            }
            int originalIndex = project.EventInstruments.IndexOf(instrument);
            return Prepared(
                hasChanges: true,
                EverythingChange(),
                value =>
                {
                    RequireContains(value.EventInstruments, instrument, "Event Instrument");
                    foreach (InstrumentBinding binding in bindings)
                    {
                        binding.Track.LastBoundEventInstrumentName = instrument.Name;
                        binding.Track.EventInstrumentId = null;
                    }
                    value.EventInstruments.Remove(instrument);
                },
                value =>
                {
                    EnsureEventInstrumentIdAvailable(value, eventInstrumentId);
                    InsertAt(
                        value.EventInstruments,
                        originalIndex,
                        instrument,
                        "Event Instrument");
                    RestoreBindings(bindings);
                });
        });

    public static IProjectEditCommand RenameEventInstrumentFolder(
        MidoraId folderId,
        string name) =>
        Command("Rename event instrument folder", project =>
        {
            EventInstrumentLibraryFolder folder = FindFolder(project, folderId);
            string normalized = EventInstrumentLibrary.ValidateFolderName(
                project,
                name,
                folderId);
            string oldName = folder.Name;
            return Prepared(
                !string.Equals(oldName, normalized, StringComparison.Ordinal),
                NoCompilationChange(),
                _ => folder.Name = normalized,
                _ => folder.Name = oldName);
        });

    public static IProjectEditCommand ReorderEventInstrumentFolder(
        MidoraId folderId,
        int newIndex) =>
        Command("Reorder event instrument folder", project =>
        {
            EventInstrumentLibraryFolder folder = FindFolder(project, folderId);
            int oldIndex = project.EventInstrumentFolders.IndexOf(folder);
            ValidateExistingIndex(newIndex, project.EventInstrumentFolders.Count, nameof(newIndex));
            return Prepared(
                oldIndex != newIndex,
                NoCompilationChange(),
                value => Move(value.EventInstrumentFolders, folder, newIndex),
                value => Move(value.EventInstrumentFolders, folder, oldIndex));
        });

    public static IProjectEditCommand DeleteEventInstrumentFolder(MidoraId folderId) =>
        Command("Delete event instrument folder", project =>
        {
            EventInstrumentLibraryFolder folder = FindFolder(project, folderId);
            int originalIndex = project.EventInstrumentFolders.IndexOf(folder);
            EventInstrument[] affected = project.EventInstruments
                .Where(value => value.LibraryFolderId == folderId)
                .ToArray();
            return Prepared(
                hasChanges: true,
                NoCompilationChange(),
                value =>
                {
                    RequireContains(value.EventInstrumentFolders, folder, "Event Instrument folder");
                    foreach (EventInstrument instrument in affected)
                    {
                        instrument.LibraryFolderId = null;
                    }
                    value.EventInstrumentFolders.Remove(folder);
                },
                value =>
                {
                    EnsureFolderIdAvailable(value, folderId);
                    InsertAt(
                        value.EventInstrumentFolders,
                        originalIndex,
                        folder,
                        "Event Instrument folder");
                    foreach (EventInstrument instrument in affected)
                    {
                        instrument.LibraryFolderId = folderId;
                    }
                });
        });

    public static IProjectEditCommand DeleteDamagedEventInstrument(MidoraId placeholderId) =>
        Command("Delete damaged event instrument", project =>
        {
            DamagedProjectObject placeholder = project.DamagedEventInstruments
                .SingleOrDefault(value => value.Id == placeholderId)
                ?? throw new ArgumentOutOfRangeException(nameof(placeholderId));
            DamagedInstrumentBindingSnapshot[] bindings = project.Tracks
                .Where(value => value.EventInstrumentId == placeholderId)
                .Select(value => new DamagedInstrumentBindingSnapshot(
                    value.Id,
                    value.EventInstrumentId,
                    value.LastBoundEventInstrumentName))
                .ToArray();
            DamagedEventInstrumentDeletion deletion = new(placeholder, bindings);
            return Prepared(
                hasChanges: true,
                EverythingChange(),
                value => DamagedProjectObjectEditing.DeleteEventInstrument(value, placeholderId),
                value => DamagedProjectObjectEditing.UndoDeleteEventInstrument(value, deletion));
        });

    public static IProjectEditCommand DeleteDamagedLogicalTrack(MidoraId placeholderId) =>
        Command("Delete damaged logical track", project =>
        {
            DamagedProjectObject placeholder = project.DamagedLogicalTracks
                .SingleOrDefault(value => value.Id == placeholderId)
                ?? throw new ArgumentOutOfRangeException(nameof(placeholderId));
            DamagedLogicalTrackDeletion deletion = new(
                placeholder,
                project.AudioRender.ExplicitLogicalTrackIds.Contains(placeholderId));
            return Prepared(
                hasChanges: true,
                EverythingChange(),
                value => DamagedProjectObjectEditing.DeleteLogicalTrack(value, placeholderId),
                value => DamagedProjectObjectEditing.UndoDeleteLogicalTrack(value, deletion));
        });

    public static IProjectEditCommand MoveSegment(
        MidoraId segmentId,
        MidoraId targetTrackId,
        long newProjectStartTick) =>
        Command("Move segment", project =>
        {
            SegmentLocation source = FindSegment(project, segmentId);
            LogicalTrack targetTrack = FindTrack(project, targetTrackId);
            ValidateSegmentRange(newProjectStartTick, source.Segment.LengthTicks, source.Segment.ContentOffsetTick);
            EnsureNoSegmentOverlap(
                targetTrack,
                source.Segment,
                newProjectStartTick,
                source.Segment.LengthTicks);
            long oldProjectStartTick = source.Segment.ProjectStartTick;
            return Prepared(
                source.Track.Id != targetTrackId || oldProjectStartTick != newProjectStartTick,
                TrackChange(source.Track.Id, targetTrackId),
                _ =>
                {
                    RemoveRequired(source.Track.Segments, source.Segment, "Segment");
                    source.Segment.ProjectStartTick = newProjectStartTick;
                    InsertSegmentByTime(targetTrack.Segments, source.Segment);
                },
                _ =>
                {
                    RemoveRequired(targetTrack.Segments, source.Segment, "Segment");
                    source.Segment.ProjectStartTick = oldProjectStartTick;
                    InsertAt(source.Track.Segments, source.Index, source.Segment, "Segment");
                });
        });

    public static IProjectEditCommand SetSegmentWindow(
        MidoraId segmentId,
        long projectStartTick,
        long lengthTicks,
        long contentOffsetTick) =>
        Command("Change segment window", project =>
        {
            SegmentLocation location = FindSegment(project, segmentId);
            ValidateSegmentRange(projectStartTick, lengthTicks, contentOffsetTick);
            EnsureNoSegmentOverlap(
                location.Track,
                location.Segment,
                projectStartTick,
                lengthTicks);
            SegmentWindow old = new(
                location.Segment.ProjectStartTick,
                location.Segment.LengthTicks,
                location.Segment.ContentOffsetTick);
            SegmentWindow replacement = new(projectStartTick, lengthTicks, contentOffsetTick);
            return Prepared(
                old != replacement,
                TrackChange(location.Track.Id),
                _ => SetWindow(location.Segment, replacement),
                _ => SetWindow(location.Segment, old));
        });

    public static IProjectEditCommand DeleteSegment(
        MidoraId segmentId,
        bool nonEmptyDeletionConfirmed) =>
        Command("Delete segment", project =>
        {
            SegmentLocation location = FindSegment(project, segmentId);
            bool nonEmpty = location.Segment.Notes.Count != 0
                || location.Segment.ParameterLanes.Count != 0;
            if (nonEmpty && !nonEmptyDeletionConfirmed)
            {
                throw new InvalidOperationException(
                    "Deleting a non-empty Segment requires explicit confirmation.");
            }
            return Prepared(
                hasChanges: true,
                TrackChange(location.Track.Id),
                _ => RemoveRequired(location.Track.Segments, location.Segment, "Segment"),
                _ => InsertAt(
                    location.Track.Segments,
                    location.Index,
                    location.Segment,
                    "Segment"));
        });

    public static IProjectEditCommand JoinSegments(MidoraId firstSegmentId, MidoraId secondSegmentId) =>
        Command("Join segments", project =>
        {
            SegmentLocation first = FindSegment(project, firstSegmentId);
            SegmentLocation second = FindSegment(project, secondSegmentId);
            if (!ReferenceEquals(first.Track, second.Track))
            {
                throw new InvalidOperationException("Only Segments on the same Logical Track can be joined.");
            }
            long nextStableId = project.NextStableId;
            Segment joined = SegmentEditing.Join(project, first.Segment, second.Segment);
            if (project.NextStableId != nextStableId)
            {
                throw new InvalidOperationException(
                    "Joining Segments unexpectedly allocated a stable ID.");
            }
            EnsureNoSegmentOverlap(
                first.Track,
                first.Segment,
                joined.ProjectStartTick,
                joined.LengthTicks,
                second.Segment);
            int insertionIndex = Math.Min(first.Index, second.Index);
            return Prepared(
                hasChanges: true,
                TrackChange(first.Track.Id),
                _ =>
                {
                    RequireContains(first.Track.Segments, first.Segment, "Segment");
                    RequireContains(first.Track.Segments, second.Segment, "Segment");
                    int high = Math.Max(
                        first.Track.Segments.IndexOf(first.Segment),
                        first.Track.Segments.IndexOf(second.Segment));
                    int low = Math.Min(
                        first.Track.Segments.IndexOf(first.Segment),
                        first.Track.Segments.IndexOf(second.Segment));
                    first.Track.Segments.RemoveAt(high);
                    first.Track.Segments.RemoveAt(low);
                    first.Track.Segments.Insert(insertionIndex, joined);
                },
                _ =>
                {
                    RemoveRequired(first.Track.Segments, joined, "joined Segment");
                    if (first.Index < second.Index)
                    {
                        InsertAt(first.Track.Segments, first.Index, first.Segment, "Segment");
                        InsertAt(first.Track.Segments, second.Index, second.Segment, "Segment");
                    }
                    else
                    {
                        InsertAt(first.Track.Segments, second.Index, second.Segment, "Segment");
                        InsertAt(first.Track.Segments, first.Index, first.Segment, "Segment");
                    }
                });
        });

    private static IProjectEditCommand Command(
        string name,
        Func<MidoraProject, IPreparedProjectEdit> prepare) =>
        new DelegateCommand(name, prepare);

    private static IPreparedProjectEdit Prepared(
        bool hasChanges,
        ProjectChangeSet changes,
        Action<MidoraProject> apply,
        Action<MidoraProject> undo) =>
        new DelegatePreparedEdit(hasChanges, changes, apply, undo);

    private static IPreparedProjectEdit DeferredCreate<T>(
        ProjectChangeSet changes,
        Func<MidoraProject, T> createAndAttach,
        Action<MidoraProject, T> reattach,
        Action<MidoraProject, T> detach)
        where T : class
    {
        T? created = null;
        return Prepared(
            hasChanges: true,
            changes,
            project =>
            {
                if (created is null)
                {
                    created = createAndAttach(project)
                        ?? throw new InvalidOperationException(
                            "A Project creation command returned no object.");
                }
                else
                {
                    reattach(project, created);
                }
            },
            project =>
            {
                if (created is null)
                {
                    throw new InvalidOperationException(
                        "A Project creation command cannot be undone before its first Apply.");
                }
                detach(project, created);
            });
    }

    private static LogicalTrack FindTrack(MidoraProject project, MidoraId trackId) =>
        project.Tracks.SingleOrDefault(value => value.Id == trackId)
        ?? throw new ArgumentOutOfRangeException(nameof(trackId));

    private static EventInstrument FindEventInstrument(
        MidoraProject project,
        MidoraId eventInstrumentId) =>
        project.EventInstruments.SingleOrDefault(value => value.Id == eventInstrumentId)
        ?? throw new ArgumentOutOfRangeException(nameof(eventInstrumentId));

    private static EventInstrumentLibraryFolder FindFolder(MidoraProject project, MidoraId folderId) =>
        project.EventInstrumentFolders.SingleOrDefault(value => value.Id == folderId)
        ?? throw new ArgumentOutOfRangeException(nameof(folderId));

    private static SegmentLocation FindSegment(MidoraProject project, MidoraId segmentId)
    {
        SegmentLocation? result = null;
        foreach (LogicalTrack track in project.Tracks)
        {
            for (int index = 0; index < track.Segments.Count; index++)
            {
                Segment segment = track.Segments[index];
                if (segment.Id != segmentId)
                {
                    continue;
                }
                if (result.HasValue)
                {
                    throw new InvalidOperationException("The Segment stable ID is duplicated.");
                }
                result = new(track, segment, index);
            }
        }
        return result ?? throw new ArgumentOutOfRangeException(nameof(segmentId));
    }

    private static void ValidateSegmentRange(
        long projectStartTick,
        long lengthTicks,
        long contentOffsetTick)
    {
        if (projectStartTick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(projectStartTick));
        }
        if (lengthTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lengthTicks));
        }
        if (contentOffsetTick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contentOffsetTick));
        }
        _ = checked(projectStartTick + lengthTicks);
        _ = checked(contentOffsetTick + lengthTicks);
    }

    private static void EnsureNoSegmentOverlap(
        LogicalTrack track,
        Segment? primaryExcluded,
        long projectStartTick,
        long lengthTicks,
        Segment? secondaryExcluded = null)
    {
        TickRange candidate = new(projectStartTick, checked(projectStartTick + lengthTicks));
        if (track.Segments.Any(value =>
            !ReferenceEquals(value, primaryExcluded)
            && !ReferenceEquals(value, secondaryExcluded)
            && candidate.Intersects(value.ProjectRange)))
        {
            throw new InvalidOperationException(
                "Segments on the same Logical Track cannot overlap.");
        }
    }

    private static void SetWindow(Segment segment, SegmentWindow window)
    {
        segment.ProjectStartTick = window.ProjectStartTick;
        segment.LengthTicks = window.LengthTicks;
        segment.ContentOffsetTick = window.ContentOffsetTick;
    }

    private static void RestoreBindings(IEnumerable<InstrumentBinding> bindings)
    {
        foreach (InstrumentBinding binding in bindings)
        {
            binding.Track.EventInstrumentId = binding.EventInstrumentId;
            binding.Track.LastBoundEventInstrumentName = binding.LastBoundEventInstrumentName;
        }
    }

    private static void ValidateExistingIndex(int index, int count, string parameterName)
    {
        if ((uint)index >= (uint)count)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateInsertionIndex(int index, int count, string parameterName)
    {
        if ((uint)index > (uint)count)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void Move<T>(List<T> values, T value, int targetIndex)
        where T : class
    {
        int currentIndex = values.IndexOf(value);
        if (currentIndex < 0)
        {
            throw new InvalidOperationException("The Project object is no longer present.");
        }
        values.RemoveAt(currentIndex);
        values.Insert(targetIndex, value);
    }

    private static void InsertSegmentByTime(List<Segment> segments, Segment segment)
    {
        int index = 0;
        while (index < segments.Count
            && (segments[index].ProjectStartTick < segment.ProjectStartTick
                || segments[index].ProjectStartTick == segment.ProjectStartTick
                && segments[index].Id.CompareTo(segment.Id) < 0))
        {
            index++;
        }
        segments.Insert(index, segment);
    }

    private static void RequireContains<T>(List<T> values, T value, string objectName)
        where T : class
    {
        if (!values.Contains(value))
        {
            throw new InvalidOperationException($"The {objectName} is no longer present.");
        }
    }

    private static void RemoveRequired<T>(List<T> values, T value, string objectName)
        where T : class
    {
        if (!values.Remove(value))
        {
            throw new InvalidOperationException($"The {objectName} is no longer present.");
        }
    }

    private static void InsertAt<T>(List<T> values, int index, T value, string objectName)
        where T : class
    {
        if ((uint)index > (uint)values.Count)
        {
            throw new InvalidOperationException(
                $"The original {objectName} index can no longer be restored.");
        }
        values.Insert(index, value);
    }

    private static void EnsureLogicalTrackIdAvailable(MidoraProject project, MidoraId id)
    {
        if (project.Tracks.Any(value => value.Id == id)
            || project.DamagedLogicalTracks.Any(value => value.Id == id))
        {
            throw new InvalidOperationException("The Logical Track stable ID is already present.");
        }
    }

    private static void EnsureEventInstrumentIdAvailable(MidoraProject project, MidoraId id)
    {
        if (project.EventInstruments.Any(value => value.Id == id)
            || project.DamagedEventInstruments.Any(value => value.Id == id))
        {
            throw new InvalidOperationException("The Event Instrument stable ID is already present.");
        }
    }

    private static void EnsureFolderIdAvailable(MidoraProject project, MidoraId id)
    {
        if (project.EventInstrumentFolders.Any(value => value.Id == id))
        {
            throw new InvalidOperationException(
                "The Event Instrument folder stable ID is already present.");
        }
    }

    private static ProjectChangeSet NoCompilationChange() => new();

    private static ProjectChangeSet EverythingChange() => new() { AffectsEverything = true };

    private static ProjectChangeSet TrackChange(params MidoraId[] trackIds)
    {
        ProjectChangeSet result = new();
        result.TrackIds.UnionWith(trackIds);
        return result;
    }

    private static ProjectChangeSet EventInstrumentChange(MidoraId eventInstrumentId)
    {
        ProjectChangeSet result = new();
        result.EventInstrumentIds.Add(eventInstrumentId);
        return result;
    }

    private static ProjectChangeSet ConductorChange() => new() { AffectsConductor = true };

    private sealed class DelegateCommand(
        string name,
        Func<MidoraProject, IPreparedProjectEdit> prepare) : IProjectEditCommand
    {
        public string Name { get; } = name;
        public IPreparedProjectEdit Prepare(MidoraProject project) => prepare(project);
    }

    private sealed class DelegatePreparedEdit(
        bool hasChanges,
        ProjectChangeSet changes,
        Action<MidoraProject> apply,
        Action<MidoraProject> undo) : IPreparedProjectEdit
    {
        public bool HasChanges { get; } = hasChanges;
        public ProjectChangeSet Changes { get; } = changes;
        public void Apply(MidoraProject project) => apply(project);
        public void Undo(MidoraProject project) => undo(project);
    }

    private readonly record struct InstrumentBinding(
        LogicalTrack Track,
        MidoraId? EventInstrumentId,
        string? LastBoundEventInstrumentName);

    private readonly record struct SegmentLocation(
        LogicalTrack Track,
        Segment Segment,
        int Index);

    private readonly record struct SegmentWindow(
        long ProjectStartTick,
        long LengthTicks,
        long ContentOffsetTick);
}
