namespace Midora.Domain;

/// <summary>
/// Small atomic construction helpers for callers that build a Project graph
/// outside the Application command layer (importers, fixtures, and detached
/// Project creation). Interactive edits must continue to use undoable
/// Application commands.
/// </summary>
public static class ProjectGraphConstruction
{
    public static EventInstrumentUsage AddIndependentLogicalTrack(
        MidoraProject project,
        LogicalTrack track,
        MidoraId eventInstrumentId,
        int? arrangementIndex = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(track);
        if (!project.EventInstruments.Any(value => value.Id == eventInstrumentId))
        {
            throw new ArgumentOutOfRangeException(nameof(eventInstrumentId));
        }
        EventInstrumentUsage usage = new(project)
        {
            EventInstrumentId = eventInstrumentId
        };
        track.LastBoundEventInstrumentName = project.EventInstruments
            .Single(value => value.Id == eventInstrumentId)
            .Name;
        AddLogicalTrack(project, track, usage, arrangementIndex, addUsage: true);
        return usage;
    }

    public static void AddLogicalTrack(
        MidoraProject project,
        LogicalTrack track,
        EventInstrumentUsage usage,
        int? arrangementIndex = null,
        bool addUsage = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(usage);
        if (addUsage)
        {
            if (project.EventInstrumentUsages.Any(value => value.Id == usage.Id))
            {
                throw new InvalidOperationException(
                    "The Event Instrument Usage already belongs to the Project.");
            }
            project.EventInstrumentUsages.Add(usage);
        }
        else if (!project.EventInstrumentUsages.Contains(usage))
        {
            throw new InvalidOperationException(
                "The Event Instrument Usage must already belong to the Project.");
        }
        if (project.Tracks.Any(value => value.Id == track.Id))
        {
            throw new InvalidOperationException("The Logical Track already belongs to the Project.");
        }
        int index = arrangementIndex ?? project.ArrangementTracks.Count;
        if ((uint)index > (uint)project.ArrangementTracks.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(arrangementIndex));
        }
        track.EventInstrumentUsageId = usage.Id;
        project.Tracks.Add(track);
        project.ArrangementTracks.Insert(
            index,
            new(ArrangementTrackKind.LogicalTrack, track.Id));
    }

    public static void AddUnboundLogicalTrack(
        MidoraProject project,
        LogicalTrack track,
        int? arrangementIndex = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(track);
        if (track.Segments.Count != 0)
        {
            throw new InvalidOperationException("An unbound Logical Track must be empty.");
        }
        if (project.Tracks.Any(value => value.Id == track.Id))
        {
            throw new InvalidOperationException("The Logical Track already belongs to the Project.");
        }
        int index = arrangementIndex ?? project.ArrangementTracks.Count;
        if ((uint)index > (uint)project.ArrangementTracks.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(arrangementIndex));
        }
        track.EventInstrumentUsageId = null;
        project.Tracks.Add(track);
        project.ArrangementTracks.Insert(
            index,
            new(ArrangementTrackKind.LogicalTrack, track.Id));
    }

    public static void AddPureMidiTrack(
        MidoraProject project,
        MidiChannelRoot root,
        PureMidiTrack track,
        int? arrangementIndex = null,
        bool addRoot = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(track);
        if (addRoot)
        {
            if (project.MidiChannelRoots.Any(value => value.Id == root.Id))
            {
                throw new InvalidOperationException("The MIDI Channel Root already belongs to the Project.");
            }
            project.MidiChannelRoots.Add(root);
        }
        else if (!project.MidiChannelRoots.Contains(root))
        {
            throw new InvalidOperationException("The MIDI Channel Root must already belong to the Project.");
        }
        if (project.PureMidiTracks.Any(value => value.Id == track.Id))
        {
            throw new InvalidOperationException("The Pure MIDI Track already belongs to the Project.");
        }
        int index = arrangementIndex ?? project.ArrangementTracks.Count;
        if ((uint)index > (uint)project.ArrangementTracks.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(arrangementIndex));
        }
        track.MidiChannelRootId = root.Id;
        project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Insert(
            index,
            new(ArrangementTrackKind.PureMidiTrack, track.Id));
    }
}
