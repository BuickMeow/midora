using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand UpdateMidiExportSettings(
        ProjectMidiExportMode mode,
        ProjectRangeMode rangeMode,
        long? manualStartTick,
        long? manualEndTick,
        ProjectMidiExportTrackSelectionMode trackSelectionMode,
        ProjectMidiExportRoutingStrategy routing,
        bool includeReadme,
        bool treatWarningsAsErrors) =>
        Command("Change MIDI export settings", project =>
        {
            ValidateMidiExportSettings(
                mode,
                rangeMode,
                manualStartTick,
                manualEndTick,
                trackSelectionMode,
                routing);
            MidiExportSettingsValue old = SnapshotMidiExportSettings(project.Export);
            MidiExportSettingsValue replacement = new(
                mode,
                rangeMode,
                manualStartTick,
                manualEndTick,
                trackSelectionMode,
                routing,
                includeReadme,
                treatWarningsAsErrors);
            return Prepared(
                old != replacement,
                NoCompilationChange(),
                value => RestoreMidiExportSettings(value.Export, replacement),
                value => RestoreMidiExportSettings(value.Export, old));
        });

    public static IProjectEditCommand UpdatePlaybackSettings(
        double masterVolumeDecibels,
        bool limiterEnabled,
        StopCursorBehavior stopCursorBehavior) =>
        Command("Change playback settings", project =>
        {
            if (!double.IsFinite(masterVolumeDecibels)
                || masterVolumeDecibels is < -float.MaxValue or > 0)
            {
                throw new ArgumentOutOfRangeException(nameof(masterVolumeDecibels));
            }
            if (!Enum.IsDefined(stopCursorBehavior))
            {
                throw new ArgumentOutOfRangeException(nameof(stopCursorBehavior));
            }
            PlaybackSettingsValue old = new(
                project.Playback.MasterVolumeDecibels,
                project.Playback.LimiterEnabled,
                project.Playback.StopCursorBehavior);
            PlaybackSettingsValue replacement = new(
                masterVolumeDecibels,
                limiterEnabled,
                stopCursorBehavior);
            return Prepared(
                old != replacement,
                NoCompilationChange(),
                value => RestorePlaybackSettings(value.Playback, replacement),
                value => RestorePlaybackSettings(value.Playback, old));
        });

    public static IProjectEditCommand UpdateAudioRenderSettings(
        AudioRenderMode mode,
        ProjectRangeMode rangeMode,
        long? manualStartTick,
        long? manualEndTick,
        ProjectTrackSelectionMode trackSelectionMode,
        IEnumerable<MidoraId> explicitLogicalTrackIds,
        int sampleRate,
        int maximumSampleVoicesPerUnitStream)
    {
        ArgumentNullException.ThrowIfNull(explicitLogicalTrackIds);
        List<MidoraId> requestedIds = [];
        HashSet<MidoraId> uniqueIds = [];
        foreach (MidoraId id in explicitLogicalTrackIds)
        {
            if (id == default || !uniqueIds.Add(id))
            {
                throw new ArgumentException(
                    "Explicit Logical Track IDs must be unique and nonzero.",
                    nameof(explicitLogicalTrackIds));
            }
            requestedIds.Add(id);
        }
        requestedIds.Sort();

        return Command("Change audio render settings", project =>
        {
            ValidateAudioRenderSettings(
                project,
                mode,
                rangeMode,
                manualStartTick,
                manualEndTick,
                trackSelectionMode,
                requestedIds,
                sampleRate,
                maximumSampleVoicesPerUnitStream);
            AudioRenderSettingsValue old = SnapshotAudioRenderSettings(project.AudioRender);
            AudioRenderSettingsValue replacement = new(
                mode,
                rangeMode,
                manualStartTick,
                manualEndTick,
                trackSelectionMode,
                requestedIds.ToArray(),
                sampleRate,
                maximumSampleVoicesPerUnitStream);
            ProjectChangeSet changes = new()
            {
                AffectsAudioPcmCacheGeneration = old.SampleRate != replacement.SampleRate
                    || old.MaximumSampleVoicesPerUnitStream
                        != replacement.MaximumSampleVoicesPerUnitStream
            };
            return Prepared(
                !AudioRenderSettingsEqual(old, replacement),
                changes,
                value => RestoreAudioRenderSettings(value.AudioRender, replacement),
                value => RestoreAudioRenderSettings(value.AudioRender, old));
        });
    }

    private static void ValidateAudioRenderSettings(
        MidoraProject project,
        AudioRenderMode mode,
        ProjectRangeMode rangeMode,
        long? manualStartTick,
        long? manualEndTick,
        ProjectTrackSelectionMode trackSelectionMode,
        IReadOnlyCollection<MidoraId> explicitLogicalTrackIds,
        int sampleRate,
        int maximumSampleVoicesPerUnitStream)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        if (!Enum.IsDefined(rangeMode))
        {
            throw new ArgumentOutOfRangeException(nameof(rangeMode));
        }
        if (!Enum.IsDefined(trackSelectionMode))
        {
            throw new ArgumentOutOfRangeException(nameof(trackSelectionMode));
        }
        bool manualRange = rangeMode == ProjectRangeMode.ManualRange;
        if (manualRange != (manualStartTick.HasValue && manualEndTick.HasValue)
            || manualRange && (manualStartTick < 0 || manualEndTick <= manualStartTick)
            || !manualRange && (manualStartTick.HasValue || manualEndTick.HasValue))
        {
            throw new ArgumentException("Audio Render range fields are inconsistent.");
        }
        if (trackSelectionMode == ProjectTrackSelectionMode.AllValidLogicalTracks
            && explicitLogicalTrackIds.Count != 0)
        {
            throw new ArgumentException(
                "All-valid Track selection cannot carry explicit Logical Track IDs.",
                nameof(explicitLogicalTrackIds));
        }
        HashSet<MidoraId> liveTrackIds = project.Tracks.Select(value => value.Id).ToHashSet();
        if (explicitLogicalTrackIds.Any(id => !liveTrackIds.Contains(id)))
        {
            throw new ArgumentException(
                "Audio Render settings contain a missing Logical Track ID.",
                nameof(explicitLogicalTrackIds));
        }
        if (sampleRate is < AudioRenderProjectSettings.MinimumSampleRate
            or > AudioRenderProjectSettings.MaximumSampleRate)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }
        if (maximumSampleVoicesPerUnitStream
            is < AudioRenderProjectSettings.MinimumSampleVoicesPerUnitStream
            or > AudioRenderProjectSettings.MaximumSampleVoicesPerUnitStreamLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSampleVoicesPerUnitStream));
        }
    }

    private static void ValidateMidiExportSettings(
        ProjectMidiExportMode mode,
        ProjectRangeMode rangeMode,
        long? manualStartTick,
        long? manualEndTick,
        ProjectMidiExportTrackSelectionMode trackSelectionMode,
        ProjectMidiExportRoutingStrategy routing)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        if (!Enum.IsDefined(rangeMode))
        {
            throw new ArgumentOutOfRangeException(nameof(rangeMode));
        }
        if (!Enum.IsDefined(trackSelectionMode))
        {
            throw new ArgumentOutOfRangeException(nameof(trackSelectionMode));
        }
        if (!Enum.IsDefined(routing))
        {
            throw new ArgumentOutOfRangeException(nameof(routing));
        }
        bool manualRange = rangeMode == ProjectRangeMode.ManualRange;
        if (manualRange != (manualStartTick.HasValue && manualEndTick.HasValue)
            || manualRange && (manualStartTick < 0 || manualEndTick <= manualStartTick)
            || !manualRange && (manualStartTick.HasValue || manualEndTick.HasValue))
        {
            throw new ArgumentException("MIDI Export range fields are inconsistent.");
        }
    }

    private static MidiExportSettingsValue SnapshotMidiExportSettings(
        ExportProjectSettings settings) =>
        new(
            settings.Mode,
            settings.RangeMode,
            settings.ManualStartTick,
            settings.ManualEndTick,
            settings.TrackSelectionMode,
            settings.Routing,
            settings.IncludeReadme,
            settings.TreatWarningsAsErrors);

    private static void RestoreMidiExportSettings(
        ExportProjectSettings settings,
        MidiExportSettingsValue value)
    {
        settings.Mode = value.Mode;
        settings.RangeMode = value.RangeMode;
        settings.ManualStartTick = value.ManualStartTick;
        settings.ManualEndTick = value.ManualEndTick;
        settings.TrackSelectionMode = value.TrackSelectionMode;
        settings.Routing = value.Routing;
        settings.IncludeReadme = value.IncludeReadme;
        settings.TreatWarningsAsErrors = value.TreatWarningsAsErrors;
    }

    private static void RestorePlaybackSettings(
        PlaybackProjectSettings settings,
        PlaybackSettingsValue value)
    {
        settings.MasterVolumeDecibels = value.MasterVolumeDecibels;
        settings.LimiterEnabled = value.LimiterEnabled;
        settings.StopCursorBehavior = value.StopCursorBehavior;
    }

    private static AudioRenderSettingsValue SnapshotAudioRenderSettings(
        AudioRenderProjectSettings settings) =>
        new(
            settings.Mode,
            settings.RangeMode,
            settings.ManualStartTick,
            settings.ManualEndTick,
            settings.TrackSelectionMode,
            settings.ExplicitLogicalTrackIds.Order().ToArray(),
            settings.SampleRate,
            settings.MaximumSampleVoicesPerUnitStream);

    private static void RestoreAudioRenderSettings(
        AudioRenderProjectSettings settings,
        AudioRenderSettingsValue value)
    {
        settings.Mode = value.Mode;
        settings.RangeMode = value.RangeMode;
        settings.ManualStartTick = value.ManualStartTick;
        settings.ManualEndTick = value.ManualEndTick;
        settings.TrackSelectionMode = value.TrackSelectionMode;
        settings.ExplicitLogicalTrackIds.Clear();
        settings.ExplicitLogicalTrackIds.UnionWith(value.ExplicitLogicalTrackIds);
        settings.SampleRate = value.SampleRate;
        settings.MaximumSampleVoicesPerUnitStream = value.MaximumSampleVoicesPerUnitStream;
    }

    private static bool AudioRenderSettingsEqual(
        AudioRenderSettingsValue left,
        AudioRenderSettingsValue right) =>
        left.Mode == right.Mode
        && left.RangeMode == right.RangeMode
        && left.ManualStartTick == right.ManualStartTick
        && left.ManualEndTick == right.ManualEndTick
        && left.TrackSelectionMode == right.TrackSelectionMode
        && left.ExplicitLogicalTrackIds.SequenceEqual(right.ExplicitLogicalTrackIds)
        && left.SampleRate == right.SampleRate
        && left.MaximumSampleVoicesPerUnitStream == right.MaximumSampleVoicesPerUnitStream;

    private readonly record struct PlaybackSettingsValue(
        double MasterVolumeDecibels,
        bool LimiterEnabled,
        StopCursorBehavior StopCursorBehavior);

    private readonly record struct MidiExportSettingsValue(
        ProjectMidiExportMode Mode,
        ProjectRangeMode RangeMode,
        long? ManualStartTick,
        long? ManualEndTick,
        ProjectMidiExportTrackSelectionMode TrackSelectionMode,
        ProjectMidiExportRoutingStrategy Routing,
        bool IncludeReadme,
        bool TreatWarningsAsErrors);

    private sealed record AudioRenderSettingsValue(
        AudioRenderMode Mode,
        ProjectRangeMode RangeMode,
        long? ManualStartTick,
        long? ManualEndTick,
        ProjectTrackSelectionMode TrackSelectionMode,
        MidoraId[] ExplicitLogicalTrackIds,
        int SampleRate,
        int MaximumSampleVoicesPerUnitStream);
}
