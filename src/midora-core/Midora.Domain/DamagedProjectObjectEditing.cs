namespace Midora.Domain;

public sealed record DamagedLogicalTrackSnapshot(
    LogicalTrack Track,
    int ProjectIndex,
    int ArrangementIndex,
    bool WasExplicitlySelectedForAudioRender);

public sealed record DamagedPureMidiTrackSnapshot(
    PureMidiTrack Track,
    int ProjectIndex,
    int ArrangementIndex);

public sealed record DamagedChildPlaceholderSnapshot(
    DamagedProjectObject Placeholder,
    int ArrangementIndex,
    bool WasExplicitlySelectedForAudioRender = false);

public sealed record DamagedUsageSnapshot(EventInstrumentUsage Usage, int ProjectIndex);

public sealed record DamagedEventInstrumentDeletion(
    DamagedProjectObject Placeholder,
    IReadOnlyList<DamagedUsageSnapshot> RemovedUsages,
    IReadOnlyList<DamagedProjectObject> RemovedDamagedUsages,
    IReadOnlyList<DamagedLogicalTrackSnapshot> RemovedTracks,
    IReadOnlyList<DamagedChildPlaceholderSnapshot> RemovedDamagedTracks);

public sealed record DamagedMidiChannelRootDeletion(
    DamagedProjectObject Placeholder,
    IReadOnlyList<DamagedPureMidiTrackSnapshot> RemovedTracks,
    IReadOnlyList<DamagedChildPlaceholderSnapshot> RemovedDamagedTracks);

public sealed record DamagedLogicalTrackDeletion(
    DamagedProjectObject Placeholder,
    int ArrangementIndex,
    bool WasExplicitlySelectedForAudioRender);

public sealed record DamagedPureMidiTrackDeletion(
    DamagedProjectObject Placeholder,
    int ArrangementIndex);

/// <summary>
/// Recovery edits for the current flat Arrangement package format. These
/// operations deliberately use the authoritative Usage/Root references and
/// global Arrangement Track order; the removed tree model is not reconstructed.
/// </summary>
public static class DamagedProjectObjectEditing
{
    public static DamagedEventInstrumentDeletion DeleteEventInstrument(
        MidoraProject project,
        MidoraId placeholderId)
    {
        ArgumentNullException.ThrowIfNull(project);
        DamagedProjectObject placeholder = RequirePlaceholder(
            project.DamagedEventInstruments,
            placeholderId,
            nameof(placeholderId));
        DamagedUsageSnapshot[] usages = project.EventInstrumentUsages
            .Where(value => value.EventInstrumentId == placeholderId)
            .Select(value => new DamagedUsageSnapshot(
                value,
                project.EventInstrumentUsages.IndexOf(value)))
            .ToArray();
        DamagedProjectObject[] damagedUsages = project.DamagedEventInstrumentUsages
            .Where(value => value.ParentId == placeholderId)
            .ToArray();
        HashSet<MidoraId> usageIds = usages.Select(value => value.Usage.Id)
            .Concat(damagedUsages.Select(value => value.Id))
            .ToHashSet();
        DamagedLogicalTrackSnapshot[] tracks = project.Tracks
            .Where(value => value.EventInstrumentUsageId is MidoraId usageId
                && usageIds.Contains(usageId))
            .Select(value => Snapshot(project, value))
            .ToArray();
        DamagedChildPlaceholderSnapshot[] damagedTracks = project.DamagedLogicalTracks
            .Where(value => value.ParentId is MidoraId usageId && usageIds.Contains(usageId))
            .Select(value => Snapshot(project, value, ArrangementTrackKind.LogicalTrack))
            .ToArray();

        RemoveLogicalTracks(project, tracks, damagedTracks);
        foreach (DamagedUsageSnapshot usage in usages)
            _ = project.EventInstrumentUsages.Remove(usage.Usage);
        foreach (DamagedProjectObject usage in damagedUsages)
            _ = project.DamagedEventInstrumentUsages.Remove(usage);
        _ = project.DamagedEventInstruments.Remove(placeholder);
        return new(placeholder, usages, damagedUsages, tracks, damagedTracks);
    }

    public static void UndoDeleteEventInstrument(
        MidoraProject project,
        DamagedEventInstrumentDeletion deletion)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(deletion);
        EnsureDefinitionIdAvailable(project, deletion.Placeholder.Id);
        AddSorted(project.DamagedEventInstruments, deletion.Placeholder);
        foreach (DamagedUsageSnapshot snapshot in deletion.RemovedUsages.OrderBy(value => value.ProjectIndex))
        {
            EnsureUsageIdAvailable(project, snapshot.Usage.Id);
            project.EventInstrumentUsages.Insert(
                Math.Clamp(snapshot.ProjectIndex, 0, project.EventInstrumentUsages.Count),
                snapshot.Usage);
        }
        foreach (DamagedProjectObject usage in deletion.RemovedDamagedUsages)
        {
            EnsureUsageIdAvailable(project, usage.Id);
            AddSorted(project.DamagedEventInstrumentUsages, usage);
        }
        RestoreLogicalTracks(project, deletion.RemovedTracks, deletion.RemovedDamagedTracks);
    }

    public static DamagedMidiChannelRootDeletion DeleteMidiChannelRoot(
        MidoraProject project,
        MidoraId placeholderId)
    {
        ArgumentNullException.ThrowIfNull(project);
        DamagedProjectObject placeholder = RequirePlaceholder(
            project.DamagedMidiChannelRoots,
            placeholderId,
            nameof(placeholderId));
        DamagedPureMidiTrackSnapshot[] tracks = project.PureMidiTracks
            .Where(value => value.MidiChannelRootId == placeholderId)
            .Select(value => Snapshot(project, value))
            .ToArray();
        DamagedChildPlaceholderSnapshot[] damagedTracks = project.DamagedPureMidiTracks
            .Where(value => value.ParentId == placeholderId)
            .Select(value => Snapshot(project, value, ArrangementTrackKind.PureMidiTrack))
            .ToArray();

        RemovePureMidiTracks(project, tracks, damagedTracks);
        _ = project.DamagedMidiChannelRoots.Remove(placeholder);
        return new(placeholder, tracks, damagedTracks);
    }

    public static void UndoDeleteMidiChannelRoot(
        MidoraProject project,
        DamagedMidiChannelRootDeletion deletion)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(deletion);
        EnsureRootIdAvailable(project, deletion.Placeholder.Id);
        AddSorted(project.DamagedMidiChannelRoots, deletion.Placeholder);
        RestorePureMidiTracks(project, deletion.RemovedTracks, deletion.RemovedDamagedTracks);
    }

    public static DamagedLogicalTrackDeletion DeleteLogicalTrack(
        MidoraProject project,
        MidoraId placeholderId)
    {
        ArgumentNullException.ThrowIfNull(project);
        DamagedProjectObject placeholder = RequirePlaceholder(
            project.DamagedLogicalTracks,
            placeholderId,
            nameof(placeholderId));
        ArrangementTrackReference reference = new(ArrangementTrackKind.LogicalTrack, placeholderId);
        int arrangementIndex = project.ArrangementTracks.IndexOf(reference);
        if (arrangementIndex >= 0) project.ArrangementTracks.RemoveAt(arrangementIndex);
        _ = project.DamagedLogicalTracks.Remove(placeholder);
        bool selected = project.AudioRender.ExplicitLogicalTrackIds.Remove(placeholderId);
        return new(placeholder, arrangementIndex, selected);
    }

    public static void UndoDeleteLogicalTrack(
        MidoraProject project,
        DamagedLogicalTrackDeletion deletion)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(deletion);
        EnsureLogicalTrackIdAvailable(project, deletion.Placeholder.Id);
        AddSorted(project.DamagedLogicalTracks, deletion.Placeholder);
        if (deletion.ArrangementIndex >= 0)
        {
            project.ArrangementTracks.Insert(
                Math.Clamp(deletion.ArrangementIndex, 0, project.ArrangementTracks.Count),
                new(ArrangementTrackKind.LogicalTrack, deletion.Placeholder.Id));
        }
        if (deletion.WasExplicitlySelectedForAudioRender)
            project.AudioRender.ExplicitLogicalTrackIds.Add(deletion.Placeholder.Id);
    }

    public static DamagedPureMidiTrackDeletion DeletePureMidiTrack(
        MidoraProject project,
        MidoraId placeholderId)
    {
        ArgumentNullException.ThrowIfNull(project);
        DamagedProjectObject placeholder = RequirePlaceholder(
            project.DamagedPureMidiTracks,
            placeholderId,
            nameof(placeholderId));
        ArrangementTrackReference reference = new(ArrangementTrackKind.PureMidiTrack, placeholderId);
        int arrangementIndex = project.ArrangementTracks.IndexOf(reference);
        if (arrangementIndex >= 0) project.ArrangementTracks.RemoveAt(arrangementIndex);
        _ = project.DamagedPureMidiTracks.Remove(placeholder);
        return new(placeholder, arrangementIndex);
    }

    public static void UndoDeletePureMidiTrack(
        MidoraProject project,
        DamagedPureMidiTrackDeletion deletion)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(deletion);
        EnsurePureMidiTrackIdAvailable(project, deletion.Placeholder.Id);
        AddSorted(project.DamagedPureMidiTracks, deletion.Placeholder);
        if (deletion.ArrangementIndex >= 0)
        {
            project.ArrangementTracks.Insert(
                Math.Clamp(deletion.ArrangementIndex, 0, project.ArrangementTracks.Count),
                new(ArrangementTrackKind.PureMidiTrack, deletion.Placeholder.Id));
        }
    }

    private static DamagedLogicalTrackSnapshot Snapshot(MidoraProject project, LogicalTrack track) =>
        new(
            track,
            project.Tracks.IndexOf(track),
            project.ArrangementTracks.IndexOf(new(ArrangementTrackKind.LogicalTrack, track.Id)),
            project.AudioRender.ExplicitLogicalTrackIds.Contains(track.Id));

    private static DamagedPureMidiTrackSnapshot Snapshot(MidoraProject project, PureMidiTrack track) =>
        new(
            track,
            project.PureMidiTracks.IndexOf(track),
            project.ArrangementTracks.IndexOf(new(ArrangementTrackKind.PureMidiTrack, track.Id)));

    private static DamagedChildPlaceholderSnapshot Snapshot(
        MidoraProject project,
        DamagedProjectObject placeholder,
        ArrangementTrackKind kind) =>
        new(
            placeholder,
            project.ArrangementTracks.IndexOf(new(kind, placeholder.Id)),
            kind == ArrangementTrackKind.LogicalTrack
                && project.AudioRender.ExplicitLogicalTrackIds.Contains(placeholder.Id));

    private static void RemoveLogicalTracks(
        MidoraProject project,
        IEnumerable<DamagedLogicalTrackSnapshot> tracks,
        IEnumerable<DamagedChildPlaceholderSnapshot> damagedTracks)
    {
        foreach (DamagedLogicalTrackSnapshot snapshot in tracks)
        {
            _ = project.Tracks.Remove(snapshot.Track);
            project.ArrangementTracks.Remove(
                new(ArrangementTrackKind.LogicalTrack, snapshot.Track.Id));
            project.AudioRender.ExplicitLogicalTrackIds.Remove(snapshot.Track.Id);
        }
        foreach (DamagedChildPlaceholderSnapshot snapshot in damagedTracks)
        {
            _ = project.DamagedLogicalTracks.Remove(snapshot.Placeholder);
            project.ArrangementTracks.Remove(
                new(ArrangementTrackKind.LogicalTrack, snapshot.Placeholder.Id));
            project.AudioRender.ExplicitLogicalTrackIds.Remove(snapshot.Placeholder.Id);
        }
    }

    private static void RestoreLogicalTracks(
        MidoraProject project,
        IEnumerable<DamagedLogicalTrackSnapshot> tracks,
        IEnumerable<DamagedChildPlaceholderSnapshot> damagedTracks)
    {
        foreach (DamagedLogicalTrackSnapshot snapshot in tracks.OrderBy(value => value.ProjectIndex))
        {
            EnsureLogicalTrackIdAvailable(project, snapshot.Track.Id);
            project.Tracks.Insert(Math.Clamp(snapshot.ProjectIndex, 0, project.Tracks.Count), snapshot.Track);
            RestoreArrangementReference(
                project,
                snapshot.ArrangementIndex,
                new(ArrangementTrackKind.LogicalTrack, snapshot.Track.Id));
            if (snapshot.WasExplicitlySelectedForAudioRender)
                project.AudioRender.ExplicitLogicalTrackIds.Add(snapshot.Track.Id);
        }
        foreach (DamagedChildPlaceholderSnapshot snapshot in damagedTracks)
        {
            EnsureLogicalTrackIdAvailable(project, snapshot.Placeholder.Id);
            AddSorted(project.DamagedLogicalTracks, snapshot.Placeholder);
            RestoreArrangementReference(
                project,
                snapshot.ArrangementIndex,
                new(ArrangementTrackKind.LogicalTrack, snapshot.Placeholder.Id));
            if (snapshot.WasExplicitlySelectedForAudioRender)
                project.AudioRender.ExplicitLogicalTrackIds.Add(snapshot.Placeholder.Id);
        }
    }

    private static void RemovePureMidiTracks(
        MidoraProject project,
        IEnumerable<DamagedPureMidiTrackSnapshot> tracks,
        IEnumerable<DamagedChildPlaceholderSnapshot> damagedTracks)
    {
        foreach (DamagedPureMidiTrackSnapshot snapshot in tracks)
        {
            _ = project.PureMidiTracks.Remove(snapshot.Track);
            project.ArrangementTracks.Remove(
                new(ArrangementTrackKind.PureMidiTrack, snapshot.Track.Id));
        }
        foreach (DamagedChildPlaceholderSnapshot snapshot in damagedTracks)
        {
            _ = project.DamagedPureMidiTracks.Remove(snapshot.Placeholder);
            project.ArrangementTracks.Remove(
                new(ArrangementTrackKind.PureMidiTrack, snapshot.Placeholder.Id));
        }
    }

    private static void RestorePureMidiTracks(
        MidoraProject project,
        IEnumerable<DamagedPureMidiTrackSnapshot> tracks,
        IEnumerable<DamagedChildPlaceholderSnapshot> damagedTracks)
    {
        foreach (DamagedPureMidiTrackSnapshot snapshot in tracks.OrderBy(value => value.ProjectIndex))
        {
            EnsurePureMidiTrackIdAvailable(project, snapshot.Track.Id);
            project.PureMidiTracks.Insert(
                Math.Clamp(snapshot.ProjectIndex, 0, project.PureMidiTracks.Count),
                snapshot.Track);
            RestoreArrangementReference(
                project,
                snapshot.ArrangementIndex,
                new(ArrangementTrackKind.PureMidiTrack, snapshot.Track.Id));
        }
        foreach (DamagedChildPlaceholderSnapshot snapshot in damagedTracks)
        {
            EnsurePureMidiTrackIdAvailable(project, snapshot.Placeholder.Id);
            AddSorted(project.DamagedPureMidiTracks, snapshot.Placeholder);
            RestoreArrangementReference(
                project,
                snapshot.ArrangementIndex,
                new(ArrangementTrackKind.PureMidiTrack, snapshot.Placeholder.Id));
        }
    }

    private static void RestoreArrangementReference(
        MidoraProject project,
        int index,
        ArrangementTrackReference reference)
    {
        if (index < 0) return;
        if (project.ArrangementTracks.Any(value => value.TrackId == reference.TrackId))
            throw new InvalidOperationException("The damaged Arrangement Track is already present.");
        project.ArrangementTracks.Insert(Math.Clamp(index, 0, project.ArrangementTracks.Count), reference);
    }

    private static DamagedProjectObject RequirePlaceholder(
        IEnumerable<DamagedProjectObject> placeholders,
        MidoraId id,
        string parameterName) =>
        placeholders.FirstOrDefault(value => value.Id == id)
        ?? throw new ArgumentOutOfRangeException(parameterName);

    private static void EnsureDefinitionIdAvailable(MidoraProject project, MidoraId id)
    {
        if (project.EventInstruments.Any(value => value.Id == id)
            || project.DamagedEventInstruments.Any(value => value.Id == id))
            throw new InvalidOperationException("The damaged Event Instrument ID is already present.");
    }

    private static void EnsureUsageIdAvailable(MidoraProject project, MidoraId id)
    {
        if (project.EventInstrumentUsages.Any(value => value.Id == id)
            || project.DamagedEventInstrumentUsages.Any(value => value.Id == id))
            throw new InvalidOperationException("The damaged Event Instrument Usage ID is already present.");
    }

    private static void EnsureRootIdAvailable(MidoraProject project, MidoraId id)
    {
        if (project.MidiChannelRoots.Any(value => value.Id == id)
            || project.DamagedMidiChannelRoots.Any(value => value.Id == id))
            throw new InvalidOperationException("The damaged MIDI Channel Root ID is already present.");
    }

    private static void EnsureLogicalTrackIdAvailable(MidoraProject project, MidoraId id)
    {
        if (project.Tracks.Any(value => value.Id == id)
            || project.DamagedLogicalTracks.Any(value => value.Id == id))
            throw new InvalidOperationException("The damaged Logical Track ID is already present.");
    }

    private static void EnsurePureMidiTrackIdAvailable(MidoraProject project, MidoraId id)
    {
        if (project.PureMidiTracks.Any(value => value.Id == id)
            || project.DamagedPureMidiTracks.Any(value => value.Id == id))
            throw new InvalidOperationException("The damaged Pure MIDI Track ID is already present.");
    }

    private static void AddSorted(List<DamagedProjectObject> target, DamagedProjectObject value)
    {
        target.Add(value);
        target.Sort((left, right) => left.OriginalIndex != right.OriginalIndex
            ? left.OriginalIndex.CompareTo(right.OriginalIndex)
            : left.Id.CompareTo(right.Id));
    }
}
