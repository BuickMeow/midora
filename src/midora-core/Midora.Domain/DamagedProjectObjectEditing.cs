namespace Midora.Domain;

public sealed record DamagedLogicalTrackSnapshot(
    LogicalTrack Track,
    int ProjectIndex,
    bool WasExplicitlySelectedForAudioRender);

public sealed record DamagedPureMidiTrackSnapshot(
    PureMidiTrack Track,
    int ProjectIndex);

public sealed record DamagedChildPlaceholderSnapshot(
    DamagedProjectObject Placeholder,
    bool WasExplicitlySelectedForAudioRender = false);

public sealed record DamagedEventInstrumentDeletion(
    DamagedProjectObject Placeholder,
    int ArrangementParentIndex,
    IReadOnlyList<DamagedLogicalTrackSnapshot> RemovedTracks,
    IReadOnlyList<DamagedChildPlaceholderSnapshot> RemovedDamagedTracks);

public sealed record DamagedMidiChannelRootDeletion(
    DamagedProjectObject Placeholder,
    int ArrangementParentIndex,
    IReadOnlyList<DamagedPureMidiTrackSnapshot> RemovedTracks,
    IReadOnlyList<DamagedChildPlaceholderSnapshot> RemovedDamagedTracks);

public sealed record DamagedLogicalTrackDeletion(
    DamagedProjectObject Placeholder,
    bool WasExplicitlySelectedForAudioRender,
    MidoraId? EventInstrumentId,
    int ChildIndex,
    DamagedProjectObject? DamagedParentBefore);

public sealed record DamagedPureMidiTrackDeletion(
    DamagedProjectObject Placeholder,
    MidoraId? MidiChannelRootId,
    int ChildIndex,
    DamagedProjectObject? DamagedParentBefore);

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
        ArrangementParentReference parent = new(
            ArrangementParentKind.EventInstrument,
            placeholderId);
        int parentIndex = RequireParentIndex(project, parent, "Event Instrument");
        HashSet<MidoraId> indexedChildren = placeholder.ChildIds?.ToHashSet() ?? [];
        DamagedLogicalTrackSnapshot[] affectedTracks = project.Tracks
            .Where(value => value.EventInstrumentId == placeholderId
                || indexedChildren.Contains(value.Id))
            .Select(value => new DamagedLogicalTrackSnapshot(
                value,
                project.Tracks.IndexOf(value),
                project.AudioRender.ExplicitLogicalTrackIds.Contains(value.Id)))
            .ToArray();
        DamagedChildPlaceholderSnapshot[] affectedDamagedTracks = project.DamagedLogicalTracks
            .Where(value => value.ParentId == placeholderId
                || indexedChildren.Contains(value.Id))
            .Select(value => new DamagedChildPlaceholderSnapshot(
                value,
                project.AudioRender.ExplicitLogicalTrackIds.Contains(value.Id)))
            .ToArray();

        project.ArrangementParents.RemoveAt(parentIndex);
        foreach (DamagedLogicalTrackSnapshot snapshot in affectedTracks)
        {
            _ = project.Tracks.Remove(snapshot.Track);
            project.AudioRender.ExplicitLogicalTrackIds.Remove(snapshot.Track.Id);
        }
        foreach (DamagedChildPlaceholderSnapshot snapshot in affectedDamagedTracks)
        {
            _ = project.DamagedLogicalTracks.Remove(snapshot.Placeholder);
            project.AudioRender.ExplicitLogicalTrackIds.Remove(snapshot.Placeholder.Id);
        }
        _ = project.DamagedEventInstruments.Remove(placeholder);
        return new(
            placeholder,
            parentIndex,
            affectedTracks,
            affectedDamagedTracks);
    }

    public static void UndoDeleteEventInstrument(
        MidoraProject project,
        DamagedEventInstrumentDeletion deletion)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(deletion);
        EnsureParentIdAvailable(project, deletion.Placeholder.Id);
        foreach (DamagedLogicalTrackSnapshot snapshot in deletion.RemovedTracks)
        {
            EnsureLogicalTrackIdAvailable(project, snapshot.Track.Id);
        }
        foreach (DamagedChildPlaceholderSnapshot snapshot in deletion.RemovedDamagedTracks)
        {
            EnsureLogicalTrackIdAvailable(project, snapshot.Placeholder.Id);
        }

        AddSorted(project.DamagedEventInstruments, deletion.Placeholder);
        project.ArrangementParents.Insert(
            Math.Clamp(deletion.ArrangementParentIndex, 0, project.ArrangementParents.Count),
            new ArrangementParentReference(
                ArrangementParentKind.EventInstrument,
                deletion.Placeholder.Id));
        foreach (DamagedLogicalTrackSnapshot snapshot in deletion.RemovedTracks
            .OrderBy(value => value.ProjectIndex))
        {
            project.Tracks.Insert(
                Math.Clamp(snapshot.ProjectIndex, 0, project.Tracks.Count),
                snapshot.Track);
            if (snapshot.WasExplicitlySelectedForAudioRender)
            {
                project.AudioRender.ExplicitLogicalTrackIds.Add(snapshot.Track.Id);
            }
        }
        foreach (DamagedChildPlaceholderSnapshot snapshot in deletion.RemovedDamagedTracks)
        {
            AddSorted(project.DamagedLogicalTracks, snapshot.Placeholder);
            if (snapshot.WasExplicitlySelectedForAudioRender)
            {
                project.AudioRender.ExplicitLogicalTrackIds.Add(snapshot.Placeholder.Id);
            }
        }
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
        ArrangementParentReference parent = new(
            ArrangementParentKind.MidiChannelRoot,
            placeholderId);
        int parentIndex = RequireParentIndex(project, parent, "MIDI Channel Root");
        HashSet<MidoraId> indexedChildren = placeholder.ChildIds?.ToHashSet() ?? [];
        DamagedPureMidiTrackSnapshot[] affectedTracks = project.PureMidiTracks
            .Where(value => value.MidiChannelRootId == placeholderId
                || indexedChildren.Contains(value.Id))
            .Select(value => new DamagedPureMidiTrackSnapshot(
                value,
                project.PureMidiTracks.IndexOf(value)))
            .ToArray();
        DamagedChildPlaceholderSnapshot[] affectedDamagedTracks = project.DamagedPureMidiTracks
            .Where(value => value.ParentId == placeholderId
                || indexedChildren.Contains(value.Id))
            .Select(value => new DamagedChildPlaceholderSnapshot(value))
            .ToArray();

        project.ArrangementParents.RemoveAt(parentIndex);
        foreach (DamagedPureMidiTrackSnapshot snapshot in affectedTracks)
        {
            _ = project.PureMidiTracks.Remove(snapshot.Track);
        }
        foreach (DamagedChildPlaceholderSnapshot snapshot in affectedDamagedTracks)
        {
            _ = project.DamagedPureMidiTracks.Remove(snapshot.Placeholder);
        }
        _ = project.DamagedMidiChannelRoots.Remove(placeholder);
        return new(
            placeholder,
            parentIndex,
            affectedTracks,
            affectedDamagedTracks);
    }

    public static void UndoDeleteMidiChannelRoot(
        MidoraProject project,
        DamagedMidiChannelRootDeletion deletion)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(deletion);
        EnsureParentIdAvailable(project, deletion.Placeholder.Id);
        foreach (DamagedPureMidiTrackSnapshot snapshot in deletion.RemovedTracks)
        {
            EnsurePureMidiTrackIdAvailable(project, snapshot.Track.Id);
        }
        foreach (DamagedChildPlaceholderSnapshot snapshot in deletion.RemovedDamagedTracks)
        {
            EnsurePureMidiTrackIdAvailable(project, snapshot.Placeholder.Id);
        }

        AddSorted(project.DamagedMidiChannelRoots, deletion.Placeholder);
        project.ArrangementParents.Insert(
            Math.Clamp(deletion.ArrangementParentIndex, 0, project.ArrangementParents.Count),
            new ArrangementParentReference(
                ArrangementParentKind.MidiChannelRoot,
                deletion.Placeholder.Id));
        foreach (DamagedPureMidiTrackSnapshot snapshot in deletion.RemovedTracks
            .OrderBy(value => value.ProjectIndex))
        {
            project.PureMidiTracks.Insert(
                Math.Clamp(snapshot.ProjectIndex, 0, project.PureMidiTracks.Count),
                snapshot.Track);
        }
        foreach (DamagedChildPlaceholderSnapshot snapshot in deletion.RemovedDamagedTracks)
        {
            AddSorted(project.DamagedPureMidiTracks, snapshot.Placeholder);
        }
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
        EventInstrument? parent = project.EventInstruments.SingleOrDefault(value =>
            value.LogicalTrackIds.Contains(placeholderId));
        int childIndex = parent?.LogicalTrackIds.IndexOf(placeholderId) ?? placeholder.OriginalIndex;
        DamagedProjectObject? damagedParent = parent is null && placeholder.ParentId is MidoraId parentId
            ? project.DamagedEventInstruments.SingleOrDefault(value => value.Id == parentId)
            : null;
        if (parent is not null)
        {
            parent.LogicalTrackIds.RemoveAt(childIndex);
        }
        else if (damagedParent is not null)
        {
            ReplaceDamagedParent(
                project.DamagedEventInstruments,
                damagedParent,
                RemoveChild(damagedParent, placeholderId));
        }
        _ = project.DamagedLogicalTracks.Remove(placeholder);
        bool wasSelected = project.AudioRender.ExplicitLogicalTrackIds.Remove(placeholderId);
        return new(
            placeholder,
            wasSelected,
            parent?.Id ?? damagedParent?.Id ?? placeholder.ParentId,
            childIndex,
            damagedParent);
    }

    public static void UndoDeleteLogicalTrack(
        MidoraProject project,
        DamagedLogicalTrackDeletion deletion)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(deletion);
        EnsureLogicalTrackIdAvailable(project, deletion.Placeholder.Id);
        AddSorted(project.DamagedLogicalTracks, deletion.Placeholder);
        if (deletion.DamagedParentBefore is not null)
        {
            DamagedProjectObject current = project.DamagedEventInstruments.Single(value =>
                value.Id == deletion.DamagedParentBefore.Id);
            ReplaceDamagedParent(
                project.DamagedEventInstruments,
                current,
                deletion.DamagedParentBefore);
        }
        else if (deletion.EventInstrumentId is MidoraId parentId)
        {
            EventInstrument parent = project.EventInstruments.SingleOrDefault(value => value.Id == parentId)
                ?? throw new InvalidOperationException(
                    "The parent Event Instrument no longer exists.");
            parent.LogicalTrackIds.Insert(
                Math.Clamp(deletion.ChildIndex, 0, parent.LogicalTrackIds.Count),
                deletion.Placeholder.Id);
        }
        if (deletion.WasExplicitlySelectedForAudioRender)
        {
            project.AudioRender.ExplicitLogicalTrackIds.Add(deletion.Placeholder.Id);
        }
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
        MidiChannelRoot? parent = project.MidiChannelRoots.SingleOrDefault(value =>
            value.MidiTrackIds.Contains(placeholderId));
        int childIndex = parent?.MidiTrackIds.IndexOf(placeholderId) ?? placeholder.OriginalIndex;
        DamagedProjectObject? damagedParent = parent is null && placeholder.ParentId is MidoraId parentId
            ? project.DamagedMidiChannelRoots.SingleOrDefault(value => value.Id == parentId)
            : null;
        if (parent is not null)
        {
            parent.MidiTrackIds.RemoveAt(childIndex);
        }
        else if (damagedParent is not null)
        {
            ReplaceDamagedParent(
                project.DamagedMidiChannelRoots,
                damagedParent,
                RemoveChild(damagedParent, placeholderId));
        }
        _ = project.DamagedPureMidiTracks.Remove(placeholder);
        return new(
            placeholder,
            parent?.Id ?? damagedParent?.Id ?? placeholder.ParentId,
            childIndex,
            damagedParent);
    }

    public static void UndoDeletePureMidiTrack(
        MidoraProject project,
        DamagedPureMidiTrackDeletion deletion)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(deletion);
        EnsurePureMidiTrackIdAvailable(project, deletion.Placeholder.Id);
        AddSorted(project.DamagedPureMidiTracks, deletion.Placeholder);
        if (deletion.DamagedParentBefore is not null)
        {
            DamagedProjectObject current = project.DamagedMidiChannelRoots.Single(value =>
                value.Id == deletion.DamagedParentBefore.Id);
            ReplaceDamagedParent(
                project.DamagedMidiChannelRoots,
                current,
                deletion.DamagedParentBefore);
        }
        else if (deletion.MidiChannelRootId is MidoraId rootId)
        {
            MidiChannelRoot root = project.MidiChannelRoots.SingleOrDefault(value => value.Id == rootId)
                ?? throw new InvalidOperationException(
                    "The parent MIDI Channel Root no longer exists.");
            root.MidiTrackIds.Insert(
                Math.Clamp(deletion.ChildIndex, 0, root.MidiTrackIds.Count),
                deletion.Placeholder.Id);
        }
    }

    private static DamagedProjectObject RequirePlaceholder(
        IEnumerable<DamagedProjectObject> placeholders,
        MidoraId id,
        string parameterName) =>
        placeholders.FirstOrDefault(value => value.Id == id)
        ?? throw new ArgumentOutOfRangeException(parameterName);

    private static int RequireParentIndex(
        MidoraProject project,
        ArrangementParentReference parent,
        string kind)
    {
        int index = project.ArrangementParents.IndexOf(parent);
        return index >= 0
            ? index
            : throw new InvalidOperationException(
                $"The damaged {kind} is missing from the Arrangement parent order.");
    }

    private static void EnsureParentIdAvailable(MidoraProject project, MidoraId id)
    {
        if (project.EventInstruments.Any(value => value.Id == id)
            || project.DamagedEventInstruments.Any(value => value.Id == id)
            || project.MidiChannelRoots.Any(value => value.Id == id)
            || project.DamagedMidiChannelRoots.Any(value => value.Id == id))
        {
            throw new InvalidOperationException("The damaged Arrangement parent ID is already present.");
        }
    }

    private static void EnsureLogicalTrackIdAvailable(MidoraProject project, MidoraId id)
    {
        if (project.Tracks.Any(value => value.Id == id)
            || project.DamagedLogicalTracks.Any(value => value.Id == id))
        {
            throw new InvalidOperationException("The damaged Logical Track ID is already present.");
        }
    }

    private static void EnsurePureMidiTrackIdAvailable(MidoraProject project, MidoraId id)
    {
        if (project.PureMidiTracks.Any(value => value.Id == id)
            || project.DamagedPureMidiTracks.Any(value => value.Id == id))
        {
            throw new InvalidOperationException("The damaged Pure MIDI Track ID is already present.");
        }
    }

    private static void AddSorted(
        List<DamagedProjectObject> target,
        DamagedProjectObject value)
    {
        target.Add(value);
        target.Sort((left, right) =>
            left.OriginalIndex != right.OriginalIndex
                ? left.OriginalIndex.CompareTo(right.OriginalIndex)
                : left.Id.CompareTo(right.Id));
    }

    private static DamagedProjectObject RemoveChild(
        DamagedProjectObject parent,
        MidoraId childId) =>
        parent.ChildIds is null
            ? parent
            : parent with
            {
                ChildIds = Array.AsReadOnly(parent.ChildIds.Where(value => value != childId).ToArray())
            };

    private static void ReplaceDamagedParent(
        List<DamagedProjectObject> target,
        DamagedProjectObject before,
        DamagedProjectObject after)
    {
        int index = target.IndexOf(before);
        if (index < 0)
        {
            throw new InvalidOperationException("The damaged Arrangement parent no longer exists.");
        }
        target[index] = after;
    }
}
