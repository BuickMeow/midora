using Midora.Domain;
using Midora.Persistence;

namespace Midora.Application;

/// <summary>
/// Owns Project presentation state independently from Project source history. Presentation
/// edits are deliberately not Project commands and never affect compilation or Project Modified.
/// </summary>
public sealed class ProjectPresentationSessionV3
{
    private readonly object _sync = new();
    private ProjectPresentationStateV3 _state;
    private long _revision;
    private long _savedRevision;
    private bool _recoveryDirty;

    public ProjectPresentationSessionV3(
        ProjectPresentationStateV3? initialState = null,
        bool recoveryDirty = false)
    {
        _state = initialState ?? ProjectPresentationStateV3.Empty;
        _recoveryDirty = recoveryDirty;
    }

    public ProjectPresentationStateV3 Current
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public bool IsModified
    {
        get
        {
            lock (_sync)
            {
                return _recoveryDirty || _revision != _savedRevision;
            }
        }
    }

    public long Revision
    {
        get
        {
            lock (_sync)
            {
                return _revision;
            }
        }
    }

    public event EventHandler? Changed;

    public void Replace(ProjectPresentationStateV3 state)
    {
        ArgumentNullException.ThrowIfNull(state);
        bool changed;
        lock (_sync)
        {
            changed = !Equals(_state, state);
            if (!changed)
            {
                return;
            }
            if (_revision == long.MaxValue)
            {
                throw new InvalidOperationException(
                    "The Project presentation revision counter is exhausted.");
            }
            _state = state;
            _revision++;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ProjectPresentationSaveSnapshotV3 CreateSaveSnapshot(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        lock (_sync)
        {
            ProjectPresentationStateV3 filtered = FilterDormantReferences(_state, project);
            return new(filtered, _revision);
        }
    }

    public void MarkSaveSucceeded(ProjectPresentationSaveSnapshotV3 snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        bool changed;
        lock (_sync)
        {
            changed = _recoveryDirty || _savedRevision != snapshot.Revision;
            _savedRevision = snapshot.Revision;
            _recoveryDirty = false;
        }
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static ProjectPresentationStateV3 FilterDormantReferences(
        ProjectPresentationStateV3 state,
        MidoraProject project)
    {
        HashSet<MidoraId> tracks = project.ArrangementTracks
            .Select(value => value.TrackId)
            .ToHashSet();
        TrackOnionPresetV3[] trackPresets = state.TrackOnionPresets
            .Where(value => tracks.Contains(value.TargetTrackId))
            .Select(value => value with
            {
                SourceTrackIds = value.SourceTrackIds
                    .Where(id => id != value.TargetTrackId && tracks.Contains(id))
                    .Distinct()
                    .ToArray()
            })
            .ToArray();

        Dictionary<MidoraId, HashSet<MidoraId>> subVoices = project.EventInstruments
            .ToDictionary(
                value => value.Id,
                value => value.SubVoices.Select(subVoice => subVoice.Id).ToHashSet());
        SubVoiceOnionPresetV3[] subVoicePresets = state.SubVoiceOnionPresets
            .Where(value => subVoices.TryGetValue(value.EventInstrumentId, out HashSet<MidoraId>? ids)
                && ids.Contains(value.TargetSubVoiceId))
            .Select(value => value with
            {
                SourceSubVoiceIds = value.SourceSubVoiceIds
                    .Where(id => id != value.TargetSubVoiceId
                        && subVoices[value.EventInstrumentId].Contains(id))
                    .Distinct()
                    .ToArray()
            })
            .ToArray();
        return new(state.AllTracksMode, trackPresets, subVoicePresets);
    }
}

public sealed record ProjectPresentationSaveSnapshotV3(
    ProjectPresentationStateV3 State,
    long Revision);
