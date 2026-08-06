namespace Midora.Domain;

public sealed record DamagedInstrumentBindingSnapshot(
    MidoraId LogicalTrackId,
    MidoraId? EventInstrumentId,
    string? LastBoundEventInstrumentName);

public sealed record DamagedEventInstrumentDeletion(
    DamagedProjectObject Placeholder,
    IReadOnlyList<DamagedInstrumentBindingSnapshot> AffectedBindings);

public sealed record DamagedLogicalTrackDeletion(
    DamagedProjectObject Placeholder,
    bool WasExplicitlySelectedForAudioRender);

public static class DamagedProjectObjectEditing
{
    public static DamagedEventInstrumentDeletion DeleteEventInstrument(
        MidoraProject project,
        MidoraId placeholderId)
    {
        ArgumentNullException.ThrowIfNull(project);
        DamagedProjectObject placeholder = project.DamagedEventInstruments
            .FirstOrDefault(value => value.Id == placeholderId)
            ?? throw new ArgumentOutOfRangeException(nameof(placeholderId));
        LogicalTrack[] affectedTracks = project.Tracks
            .Where(value => value.EventInstrumentId == placeholderId)
            .ToArray();
        DamagedInstrumentBindingSnapshot[] snapshots = affectedTracks
            .Select(value => new DamagedInstrumentBindingSnapshot(
                value.Id,
                value.EventInstrumentId,
                value.LastBoundEventInstrumentName))
            .ToArray();
        foreach (LogicalTrack track in affectedTracks)
        {
            track.EventInstrumentId = null;
            track.LastBoundEventInstrumentName = placeholder.NameSnapshot;
        }
        _ = project.DamagedEventInstruments.Remove(placeholder);
        return new(placeholder, snapshots);
    }

    public static void UndoDeleteEventInstrument(
        MidoraProject project,
        DamagedEventInstrumentDeletion deletion)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(deletion);
        if (project.EventInstruments.Any(value => value.Id == deletion.Placeholder.Id)
            || project.DamagedEventInstruments.Any(value => value.Id == deletion.Placeholder.Id))
        {
            throw new InvalidOperationException("The damaged Event Instrument ID is already present.");
        }
        Dictionary<MidoraId, LogicalTrack> tracks = project.Tracks.ToDictionary(value => value.Id);
        foreach (DamagedInstrumentBindingSnapshot snapshot in deletion.AffectedBindings)
        {
            if (!tracks.ContainsKey(snapshot.LogicalTrackId))
            {
                throw new InvalidOperationException("An affected Logical Track no longer exists.");
            }
        }
        project.DamagedEventInstruments.Add(deletion.Placeholder);
        project.DamagedEventInstruments.Sort((left, right) =>
            left.OriginalIndex != right.OriginalIndex
                ? left.OriginalIndex.CompareTo(right.OriginalIndex)
                : left.Id.CompareTo(right.Id));
        foreach (DamagedInstrumentBindingSnapshot snapshot in deletion.AffectedBindings)
        {
            LogicalTrack track = tracks[snapshot.LogicalTrackId];
            track.EventInstrumentId = snapshot.EventInstrumentId;
            track.LastBoundEventInstrumentName = snapshot.LastBoundEventInstrumentName;
        }
    }

    public static DamagedLogicalTrackDeletion DeleteLogicalTrack(MidoraProject project, MidoraId placeholderId)
    {
        ArgumentNullException.ThrowIfNull(project);
        DamagedProjectObject placeholder = project.DamagedLogicalTracks
            .FirstOrDefault(value => value.Id == placeholderId)
            ?? throw new ArgumentOutOfRangeException(nameof(placeholderId));
        _ = project.DamagedLogicalTracks.Remove(placeholder);
        bool wasSelected = project.AudioRender.ExplicitLogicalTrackIds.Remove(placeholderId);
        return new(placeholder, wasSelected);
    }

    public static void UndoDeleteLogicalTrack(MidoraProject project, DamagedLogicalTrackDeletion deletion)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(deletion);
        if (project.Tracks.Any(value => value.Id == deletion.Placeholder.Id)
            || project.DamagedLogicalTracks.Any(value => value.Id == deletion.Placeholder.Id))
        {
            throw new InvalidOperationException("The damaged Logical Track ID is already present.");
        }
        project.DamagedLogicalTracks.Add(deletion.Placeholder);
        project.DamagedLogicalTracks.Sort((left, right) =>
            left.OriginalIndex != right.OriginalIndex
                ? left.OriginalIndex.CompareTo(right.OriginalIndex)
                : left.Id.CompareTo(right.Id));
        if (deletion.WasExplicitlySelectedForAudioRender)
        {
            project.AudioRender.ExplicitLogicalTrackIds.Add(deletion.Placeholder.Id);
        }
    }
}
