using System.Text.Json;
using Midora.Domain;

namespace Midora.Persistence;

internal static class MidoraPackagePathsV1
{
    public const string Manifest = "manifest.json";
    public const string Project = "project.json";
    public const string Metadata = "metadata.json";
    public const string ConductorTrack = "conductor-track.json";
    public const string ProjectSettings = "settings/project-settings.json";
    public const string ExportSettings = "settings/export-settings.json";
    public const string PlaybackSettings = "settings/playback-settings.json";
    public const string AudioRenderSettings = "settings/audio-render-settings.json";
    public const string GlobalResetDefaults = "settings/global-reset-defaults.json";
    public const string GlobalEventScopeDefaults = "settings/global-event-scope-defaults.json";

    public static string PureMidiContentPack(MidoraId trackId) =>
        $"midi-content/mt_{trackId}.mpk";

    public static readonly string[] FixedContentPaths =
    [
        Project,
        Metadata,
        ConductorTrack,
        ProjectSettings,
        ExportSettings,
        PlaybackSettings,
        AudioRenderSettings,
        GlobalResetDefaults,
        GlobalEventScopeDefaults
    ];
}

internal static class ProjectCodecV1
{
    public static ProjectJsonV1 Parse(ReadOnlySpan<byte> utf8)
    {
        StrictJsonV1.ValidateInput(utf8);
        ProjectJsonV1 value = JsonSerializer.Deserialize(
            utf8,
            MidoraJsonSerializerContextV1.Default.ProjectJsonV1)
            ?? throw new InvalidDataException("project.json cannot be null.");
        Validate(value);
        return value;
    }

    public static byte[] Serialize(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        ProjectJsonV1 value = new()
        {
            SchemaVersion = PersistenceContractV1.SchemaVersion,
            NextStableId = new StableIdJsonV1(project.NextStableId),
            MetadataPath = MidoraPackagePathsV1.Metadata,
            ConductorTrackPath = MidoraPackagePathsV1.ConductorTrack,
            Settings = new ProjectSettingsPathsJsonV1
            {
                Project = MidoraPackagePathsV1.ProjectSettings,
                Export = MidoraPackagePathsV1.ExportSettings,
                Playback = MidoraPackagePathsV1.PlaybackSettings,
                AudioRender = MidoraPackagePathsV1.AudioRenderSettings,
                GlobalResetDefaults = MidoraPackagePathsV1.GlobalResetDefaults,
                GlobalEventScopeDefaults = MidoraPackagePathsV1.GlobalEventScopeDefaults
            },
            EventInstruments = project.EventInstruments
                .Select(value => new ProjectObjectIndexJsonV1
                {
                    Id = new(value.Id.Value),
                    Path = $"event-instruments/ei_{value.Id}.pb",
                    NameSnapshot = value.Name
                })
                .ToArray(),
            EventInstrumentUsages = project.EventInstrumentUsages
                .Select(value => new EventInstrumentUsageIndexJsonV1
                {
                    Id = new(value.Id.Value),
                    Path = $"event-instrument-usages/eiu_{value.Id}.pb",
                    EventInstrumentId = new(value.EventInstrumentId.Value)
                })
                .ToArray(),
            MidiChannelRoots = project.MidiChannelRootsInOrder()
                .Select(value => new ProjectObjectIndexJsonV1
                {
                    Id = new(value.Id.Value),
                    Path = $"midi-channel-roots/mcr_{value.Id}.pb",
                    NameSnapshot = value.Name
                })
                .ToArray(),
            ArrangementTracks = project.ArrangementTracks
                .Select(reference => CreateTrackIndex(project, reference))
                .ToArray()
        };
        Validate(value);
        return StrictJsonV1.SerializeWithFinalLf(
            value,
            MidoraJsonSerializerContextV1.Default.ProjectJsonV1);
    }

    public static long GetNextStableId(ProjectJsonV1 value)
    {
        if (value.NextStableId.Value <= 0)
        {
            throw new InvalidDataException("project.json nextStableId is not canonical.");
        }
        return value.NextStableId.Value;
    }

    private static void Validate(ProjectJsonV1 value)
    {
        if (value.SchemaVersion != PersistenceContractV1.SchemaVersion)
        {
            throw new InvalidDataException("project.json schemaVersion is not v1.");
        }
        _ = GetNextStableId(value);
        RequirePath(value.MetadataPath, MidoraPackagePathsV1.Metadata, "metadataPath");
        RequirePath(value.ConductorTrackPath, MidoraPackagePathsV1.ConductorTrack, "conductorTrackPath");
        if (value.Settings is null)
        {
            throw new InvalidDataException("project.json settings cannot be null.");
        }
        RequirePath(value.Settings.Project, MidoraPackagePathsV1.ProjectSettings, "settings.project");
        RequirePath(value.Settings.Export, MidoraPackagePathsV1.ExportSettings, "settings.export");
        RequirePath(value.Settings.Playback, MidoraPackagePathsV1.PlaybackSettings, "settings.playback");
        RequirePath(value.Settings.AudioRender, MidoraPackagePathsV1.AudioRenderSettings, "settings.audioRender");
        RequirePath(value.Settings.GlobalResetDefaults, MidoraPackagePathsV1.GlobalResetDefaults,
            "settings.globalResetDefaults");
        RequirePath(value.Settings.GlobalEventScopeDefaults, MidoraPackagePathsV1.GlobalEventScopeDefaults,
            "settings.globalEventScopeDefaults");

        if (value.EventInstruments is null
            || value.EventInstrumentUsages is null
            || value.MidiChannelRoots is null
            || value.ArrangementTracks is null)
        {
            throw new InvalidDataException(
                "project.json object indexes and arrangementTracks cannot be null.");
        }
        HashSet<StableIdJsonV1> ids = [];
        HashSet<StableIdJsonV1> usageIds = [];
        HashSet<StableIdJsonV1> rootIds = [];
        foreach (ProjectObjectIndexJsonV1 instrument in value.EventInstruments)
        {
            if (instrument is null)
            {
                throw new InvalidDataException("project.json contains a null Event Instrument index.");
            }
            StableIdJsonV1 id = RequireIndexId(instrument.Id, "eventInstruments.id", ids);
            RequireObjectPath(
                instrument.Path,
                $"event-instruments/ei_{id}.pb",
                "eventInstruments.path");
            PersistenceValueValidationV1.ValidateShortText(
                instrument.NameSnapshot,
                "eventInstruments.nameSnapshot",
                allowEmpty: false);
        }
        foreach (EventInstrumentUsageIndexJsonV1 usage in value.EventInstrumentUsages)
        {
            if (usage is null)
            {
                throw new InvalidDataException("project.json contains a null Event Instrument Usage index.");
            }
            StableIdJsonV1 id = RequireIndexId(usage.Id, "eventInstrumentUsages.id", ids);
            usageIds.Add(id);
            RequireObjectPath(
                usage.Path,
                $"event-instrument-usages/eiu_{id}.pb",
                "eventInstrumentUsages.path");
            if (usage.EventInstrumentId.Value <= 0)
            {
                throw new InvalidDataException(
                    "project.json Event Instrument Usage Definition ID is invalid.");
            }
        }
        foreach (ProjectObjectIndexJsonV1 root in value.MidiChannelRoots)
        {
            if (root is null)
            {
                throw new InvalidDataException("project.json contains a null MIDI Channel Root index.");
            }
            StableIdJsonV1 id = RequireIndexId(root.Id, "midiChannelRoots.id", ids);
            rootIds.Add(id);
            RequireObjectPath(
                root.Path,
                $"midi-channel-roots/mcr_{id}.pb",
                "midiChannelRoots.path");
            PersistenceValueValidationV1.ValidateShortText(
                root.NameSnapshot,
                "midiChannelRoots.nameSnapshot",
                allowEmpty: false);
        }
        foreach (ArrangementTrackIndexJsonV1 track in value.ArrangementTracks)
        {
            if (track is null || track.Kind is not "logical-track" and not "pure-midi-track")
            {
                throw new InvalidDataException("project.json contains an invalid Arrangement Track kind.");
            }
            StableIdJsonV1 id = RequireIndexId(track.Id, "arrangementTracks.id", ids);
            RequireObjectPath(
                track.Path,
                track.Kind == "logical-track"
                    ? $"logical-tracks/lt_{id}.pb"
                    : $"midi-tracks/mt_{id}.pb",
                "arrangementTracks.path");
            PersistenceValueValidationV1.ValidateShortText(
                track.NameSnapshot,
                "arrangementTracks.nameSnapshot");
            if (track.Kind == "logical-track")
            {
                if (track.SharedGroupId is StableIdJsonV1 usageId
                    && !usageIds.Contains(usageId))
                {
                    throw new InvalidDataException(
                        "project.json Logical Track sharedGroupId must reference an indexed Event Instrument Usage.");
                }
            }
            else if (track.SharedGroupId is not StableIdJsonV1 rootId
                || !rootIds.Contains(rootId))
            {
                throw new InvalidDataException(
                    "project.json Pure MIDI Track sharedGroupId must reference an indexed MIDI Channel Root.");
            }
        }
    }

    private static ArrangementTrackIndexJsonV1 CreateTrackIndex(
        MidoraProject project,
        ArrangementTrackReference reference) => reference.Kind switch
        {
            ArrangementTrackKind.LogicalTrack => CreateLogicalTrackIndex(
                project.Tracks.Single(value => value.Id == reference.TrackId)),
            ArrangementTrackKind.PureMidiTrack => CreatePureMidiTrackIndex(
                project.PureMidiTracks.Single(value => value.Id == reference.TrackId)),
            _ => throw new InvalidDataException("Unknown Arrangement Track kind.")
        };

    private static ArrangementTrackIndexJsonV1 CreateLogicalTrackIndex(LogicalTrack track) => new()
    {
        Kind = "logical-track",
        Id = new(track.Id.Value),
        Path = $"logical-tracks/lt_{track.Id}.pb",
        NameSnapshot = track.Name,
        SharedGroupId = track.EventInstrumentUsageId is MidoraId usageId
            ? new(usageId.Value)
            : null
    };

    private static ArrangementTrackIndexJsonV1 CreatePureMidiTrackIndex(PureMidiTrack track) => new()
    {
        Kind = "pure-midi-track",
        Id = new(track.Id.Value),
        Path = $"midi-tracks/mt_{track.Id}.pb",
        NameSnapshot = track.Name,
        SharedGroupId = new(track.MidiChannelRootId.Value)
    };

    private static StableIdJsonV1 RequireIndexId(
        StableIdJsonV1? value,
        string fieldName,
        ISet<StableIdJsonV1> ids)
    {
        if (value is not StableIdJsonV1 id || id.Value <= 0 || !ids.Add(id))
        {
            throw new InvalidDataException($"project.json {fieldName} is invalid or duplicated.");
        }
        return id;
    }

    private static void RequirePath(string? actual, string expected, string fieldName)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"project.json {fieldName} must be '{expected}'.");
        }
    }

    private static void RequireObjectPath(string? actual, string expected, string fieldName)
    {
        if (actual is null)
        {
            throw new InvalidDataException($"project.json {fieldName} cannot be null.");
        }
        PersistenceValueValidationV1.ValidateRelativePath(actual, fieldName);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"project.json {fieldName} does not match its stable ID.");
        }
    }
}

internal static class ProjectSettingsCodecV1
{
    public static ProjectSettingsJsonV1 Parse(ReadOnlySpan<byte> utf8)
    {
        StrictJsonV1.ValidateInput(utf8);
        ProjectSettingsJsonV1 value = JsonSerializer.Deserialize(
            utf8,
            MidoraJsonSerializerContextV1.Default.ProjectSettingsJsonV1)
            ?? throw new InvalidDataException("project-settings.json cannot be null.");
        Validate(value);
        return value;
    }

    public static byte[] Serialize(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        ProjectSettingsJsonV1 value = new()
        {
            SchemaVersion = PersistenceContractV1.SchemaVersion,
            TicksPerQuarterNote = project.TicksPerQuarterNote,
            GlobalInitialState = MidiStateCodecV1.FromDomain(project.GlobalInitialState)
        };
        Validate(value);
        return StrictJsonV1.SerializeWithFinalLf(
            value,
            MidoraJsonSerializerContextV1.Default.ProjectSettingsJsonV1);
    }

    private static void Validate(ProjectSettingsJsonV1 value)
    {
        if (value.SchemaVersion != PersistenceContractV1.SchemaVersion
            || value.TicksPerQuarterNote is < MidoraProject.MinimumTicksPerQuarterNote
                or > MidoraProject.MaximumTicksPerQuarterNote)
        {
            throw new InvalidDataException("project-settings.json version or TPQ is invalid.");
        }
        MidiStateCodecV1.Validate(value.GlobalInitialState, "globalInitialState");
    }
}

internal static class ExportSettingsCodecV1
{
    private const string WholeProject = "whole-project";
    private const string PerLogicalTrack = "per-logical-track";
    private const string PerPort = "per-port";
    private const string ProjectDefaultRange = "project-default-range";
    private const string ManualRange = "manual-range";
    private const string AllValidLogicalTracks = "all-valid-logical-tracks";
    private const string ExplicitAtTaskStart = "explicit-at-task-start";
    private const string Compact = "compact";
    private const string Preserve = "preserve";

    public static ExportSettingsJsonV1 Parse(ReadOnlySpan<byte> utf8)
    {
        StrictJsonV1.ValidateInput(utf8);
        ExportSettingsJsonV1 value = JsonSerializer.Deserialize(
            utf8,
            MidoraJsonSerializerContextV1.Default.ExportSettingsJsonV1)
            ?? throw new InvalidDataException("export-settings.json cannot be null.");
        Validate(value);
        return value;
    }

    public static byte[] Serialize(ExportProjectSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ExportSettingsJsonV1 value = new()
        {
            SchemaVersion = PersistenceContractV1.SchemaVersion,
            Mode = settings.Mode switch
            {
                ProjectMidiExportMode.WholeProject => WholeProject,
                ProjectMidiExportMode.PerLogicalTrack => PerLogicalTrack,
                ProjectMidiExportMode.PerPort => PerPort,
                _ => throw new InvalidDataException("Unknown MIDI Export mode.")
            },
            RangeMode = settings.RangeMode switch
            {
                ProjectRangeMode.ProjectDefaultRange => ProjectDefaultRange,
                ProjectRangeMode.ManualRange => ManualRange,
                _ => throw new InvalidDataException("Unknown MIDI Export range mode.")
            },
            ManualStartTick = settings.ManualStartTick,
            ManualEndTick = settings.ManualEndTick,
            TrackSelectionMode = settings.TrackSelectionMode switch
            {
                ProjectMidiExportTrackSelectionMode.AllValidLogicalTracks => AllValidLogicalTracks,
                ProjectMidiExportTrackSelectionMode.ExplicitAtTaskStart => ExplicitAtTaskStart,
                _ => throw new InvalidDataException("Unknown MIDI Export Track selection mode.")
            },
            Routing = settings.Routing switch
            {
                ProjectMidiExportRoutingStrategy.Compact => Compact,
                ProjectMidiExportRoutingStrategy.Preserve => Preserve,
                _ => throw new InvalidDataException("Unknown MIDI Export routing strategy.")
            },
            IncludeReadme = settings.IncludeReadme,
            TreatWarningsAsErrors = settings.TreatWarningsAsErrors
        };
        Validate(value);
        return StrictJsonV1.SerializeWithFinalLf(
            value,
            MidoraJsonSerializerContextV1.Default.ExportSettingsJsonV1);
    }

    public static void Restore(ExportProjectSettings settings, ExportSettingsJsonV1 value)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(value);
        Validate(value);
        settings.Mode = value.Mode switch
        {
            WholeProject => ProjectMidiExportMode.WholeProject,
            PerLogicalTrack => ProjectMidiExportMode.PerLogicalTrack,
            _ => ProjectMidiExportMode.PerPort
        };
        settings.RangeMode = value.RangeMode == ProjectDefaultRange
            ? ProjectRangeMode.ProjectDefaultRange
            : ProjectRangeMode.ManualRange;
        settings.ManualStartTick = value.ManualStartTick;
        settings.ManualEndTick = value.ManualEndTick;
        settings.TrackSelectionMode = value.TrackSelectionMode == AllValidLogicalTracks
            ? ProjectMidiExportTrackSelectionMode.AllValidLogicalTracks
            : ProjectMidiExportTrackSelectionMode.ExplicitAtTaskStart;
        settings.Routing = value.Routing == Compact
            ? ProjectMidiExportRoutingStrategy.Compact
            : ProjectMidiExportRoutingStrategy.Preserve;
        settings.IncludeReadme = value.IncludeReadme;
        settings.TreatWarningsAsErrors = value.TreatWarningsAsErrors;
    }

    private static void Validate(ExportSettingsJsonV1 value)
    {
        if (value.SchemaVersion != PersistenceContractV1.SchemaVersion
            || value.Mode is not WholeProject and not PerLogicalTrack and not PerPort
            || value.RangeMode is not ProjectDefaultRange and not ManualRange
            || value.TrackSelectionMode is not AllValidLogicalTracks and not ExplicitAtTaskStart
            || value.Routing is not Compact and not Preserve)
        {
            throw new InvalidDataException("export-settings.json contains an invalid fixed value.");
        }
        bool manualRange = value.RangeMode == ManualRange;
        if (manualRange != (value.ManualStartTick.HasValue && value.ManualEndTick.HasValue)
            || manualRange && (value.ManualStartTick < 0 || value.ManualEndTick <= value.ManualStartTick)
            || !manualRange && (value.ManualStartTick.HasValue || value.ManualEndTick.HasValue))
        {
            throw new InvalidDataException("export-settings.json range fields are inconsistent.");
        }
    }
}

internal static class PlaybackSettingsCodecV1
{
    private const string ReturnToPlaybackStart = "return-to-playback-start";
    private const string StayAtStoppedTick = "stay-at-stopped-tick";

    public static PlaybackSettingsJsonV1 Parse(ReadOnlySpan<byte> utf8)
    {
        StrictJsonV1.ValidateInput(utf8);
        PlaybackSettingsJsonV1 value = JsonSerializer.Deserialize(
            utf8,
            MidoraJsonSerializerContextV1.Default.PlaybackSettingsJsonV1)
            ?? throw new InvalidDataException("playback-settings.json cannot be null.");
        Validate(value);
        return value;
    }

    public static byte[] Serialize(PlaybackProjectSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        PlaybackSettingsJsonV1 value = new()
        {
            SchemaVersion = PersistenceContractV1.SchemaVersion,
            MasterVolumeDecibels = settings.MasterVolumeDecibels,
            LimiterEnabled = settings.LimiterEnabled,
            StopCursorBehavior = settings.StopCursorBehavior switch
            {
                StopCursorBehavior.ReturnToPlaybackStart => ReturnToPlaybackStart,
                StopCursorBehavior.StayAtStoppedTick => StayAtStoppedTick,
                _ => throw new InvalidDataException("Unknown Stop Cursor Behavior.")
            }
        };
        Validate(value);
        return StrictJsonV1.SerializeWithFinalLf(
            value,
            MidoraJsonSerializerContextV1.Default.PlaybackSettingsJsonV1);
    }

    public static void Restore(PlaybackProjectSettings settings, PlaybackSettingsJsonV1 value)
    {
        settings.MasterVolumeDecibels = value.MasterVolumeDecibels;
        settings.LimiterEnabled = value.LimiterEnabled;
        settings.StopCursorBehavior = value.StopCursorBehavior switch
        {
            ReturnToPlaybackStart => StopCursorBehavior.ReturnToPlaybackStart,
            StayAtStoppedTick => StopCursorBehavior.StayAtStoppedTick,
            _ => throw new InvalidDataException("Unknown Stop Cursor Behavior.")
        };
    }

    private static void Validate(PlaybackSettingsJsonV1 value)
    {
        if (value.SchemaVersion != PersistenceContractV1.SchemaVersion
            || !double.IsFinite(value.MasterVolumeDecibels)
            || value.MasterVolumeDecibels is < -float.MaxValue or > 0
            || value.StopCursorBehavior is not ReturnToPlaybackStart and not StayAtStoppedTick)
        {
            throw new InvalidDataException("playback-settings.json contains an invalid value.");
        }
    }
}

internal static class AudioRenderSettingsCodecV1
{
    private const string WholeMix = "whole-mix";
    private const string PerLogicalTrack = "per-logical-track";
    private const string ProjectDefaultRange = "project-default-range";
    private const string ManualRange = "manual-range";
    private const string AllValidLogicalTracks = "all-valid-logical-tracks";
    private const string ExplicitLogicalTrackIds = "explicit-logical-track-ids";
    private const string ProjectOrderNumberAndTrackName = "project-order-number-and-track-name";

    public static AudioRenderSettingsJsonV1 Parse(ReadOnlySpan<byte> utf8)
    {
        StrictJsonV1.ValidateInput(utf8);
        AudioRenderSettingsJsonV1 value = JsonSerializer.Deserialize(
            utf8,
            MidoraJsonSerializerContextV1.Default.AudioRenderSettingsJsonV1)
            ?? throw new InvalidDataException("audio-render-settings.json cannot be null.");
        Validate(value);
        return value;
    }

    public static byte[] Serialize(AudioRenderProjectSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AudioRenderSettingsJsonV1 value = new()
        {
            SchemaVersion = PersistenceContractV1.SchemaVersion,
            Mode = settings.Mode switch
            {
                AudioRenderMode.WholeMix => WholeMix,
                AudioRenderMode.PerLogicalTrack => PerLogicalTrack,
                _ => throw new InvalidDataException("Unknown Audio Render mode.")
            },
            RangeMode = settings.RangeMode switch
            {
                ProjectRangeMode.ProjectDefaultRange => ProjectDefaultRange,
                ProjectRangeMode.ManualRange => ManualRange,
                _ => throw new InvalidDataException("Unknown Audio Render range mode.")
            },
            ManualStartTick = settings.ManualStartTick,
            ManualEndTick = settings.ManualEndTick,
            TrackSelectionMode = settings.TrackSelectionMode switch
            {
                ProjectTrackSelectionMode.AllValidLogicalTracks => AllValidLogicalTracks,
                ProjectTrackSelectionMode.ExplicitLogicalTrackIds => ExplicitLogicalTrackIds,
                _ => throw new InvalidDataException("Unknown Audio Render Track selection mode.")
            },
            ExplicitLogicalTrackIds = settings.ExplicitLogicalTrackIds
                .OrderBy(id => id)
                .Select(id => new StableIdJsonV1(id.Value))
                .ToArray(),
            Container = "riff-wave",
            ChannelLayout = "stereo",
            SampleFormat = "interleaved-ieee-float32",
            Endianness = "little-endian",
            SampleRate = settings.SampleRate,
            MaximumSampleVoicesPerUnitStream = settings.MaximumSampleVoicesPerUnitStream,
            TrackFileNamePattern = ProjectOrderNumberAndTrackName
        };
        Validate(value);
        return StrictJsonV1.SerializeWithFinalLf(
            value,
            MidoraJsonSerializerContextV1.Default.AudioRenderSettingsJsonV1);
    }

    public static void Restore(AudioRenderProjectSettings settings, AudioRenderSettingsJsonV1 value)
    {
        settings.Mode = value.Mode == WholeMix ? AudioRenderMode.WholeMix : AudioRenderMode.PerLogicalTrack;
        settings.RangeMode = value.RangeMode == ProjectDefaultRange
            ? ProjectRangeMode.ProjectDefaultRange
            : ProjectRangeMode.ManualRange;
        settings.ManualStartTick = value.ManualStartTick;
        settings.ManualEndTick = value.ManualEndTick;
        settings.TrackSelectionMode = value.TrackSelectionMode == AllValidLogicalTracks
            ? ProjectTrackSelectionMode.AllValidLogicalTracks
            : ProjectTrackSelectionMode.ExplicitLogicalTrackIds;
        settings.ExplicitLogicalTrackIds.Clear();
        foreach (StableIdJsonV1 idValue in value.ExplicitLogicalTrackIds)
        {
            settings.ExplicitLogicalTrackIds.Add(idValue.ToDomain());
        }
        settings.SampleRate = value.SampleRate;
        settings.MaximumSampleVoicesPerUnitStream = value.MaximumSampleVoicesPerUnitStream;
    }

    private static void Validate(AudioRenderSettingsJsonV1 value)
    {
        if (value.SchemaVersion != PersistenceContractV1.SchemaVersion
            || value.Mode is not WholeMix and not PerLogicalTrack
            || value.RangeMode is not ProjectDefaultRange and not ManualRange
            || value.TrackSelectionMode is not AllValidLogicalTracks and not ExplicitLogicalTrackIds
            || value.Container != "riff-wave"
            || value.ChannelLayout != "stereo"
            || value.SampleFormat != "interleaved-ieee-float32"
            || value.Endianness != "little-endian"
            || value.TrackFileNamePattern != ProjectOrderNumberAndTrackName
            || value.SampleRate is < AudioRenderProjectSettings.MinimumSampleRate
                or > AudioRenderProjectSettings.MaximumSampleRate
            || value.MaximumSampleVoicesPerUnitStream is < AudioRenderProjectSettings.MinimumSampleVoicesPerUnitStream
                or > AudioRenderProjectSettings.MaximumSampleVoicesPerUnitStreamLimit)
        {
            throw new InvalidDataException("audio-render-settings.json contains an invalid fixed or ranged value.");
        }
        bool manualRange = value.RangeMode == ManualRange;
        if (manualRange != (value.ManualStartTick.HasValue && value.ManualEndTick.HasValue)
            || manualRange && (value.ManualStartTick < 0 || value.ManualEndTick <= value.ManualStartTick)
            || !manualRange && (value.ManualStartTick.HasValue || value.ManualEndTick.HasValue))
        {
            throw new InvalidDataException("audio-render-settings.json range fields are inconsistent.");
        }
        if (value.ExplicitLogicalTrackIds is null)
        {
            throw new InvalidDataException("audio-render-settings.json explicitLogicalTrackIds cannot be null.");
        }
        HashSet<StableIdJsonV1> ids = [];
        foreach (StableIdJsonV1 id in value.ExplicitLogicalTrackIds)
        {
            if (id.Value <= 0 || !ids.Add(id))
            {
                throw new InvalidDataException("audio-render-settings.json contains an invalid or duplicate Track ID.");
            }
        }
        if (value.TrackSelectionMode == AllValidLogicalTracks && ids.Count != 0)
        {
            throw new InvalidDataException("All-valid Track selection cannot carry explicit Track IDs.");
        }
    }
}

internal static class GlobalResetDefaultsCodecV1
{
    public static GlobalResetDefaultsJsonV1 Parse(ReadOnlySpan<byte> utf8)
    {
        StrictJsonV1.ValidateInput(utf8);
        GlobalResetDefaultsJsonV1 value = JsonSerializer.Deserialize(
            utf8,
            MidoraJsonSerializerContextV1.Default.GlobalResetDefaultsJsonV1)
            ?? throw new InvalidDataException("global-reset-defaults.json cannot be null.");
        if (value.SchemaVersion != PersistenceContractV1.SchemaVersion)
        {
            throw new InvalidDataException("global-reset-defaults.json schemaVersion is not v1.");
        }
        MidiStateCodecV1.Validate(value.State, "state");
        return value;
    }

    public static byte[] Serialize(MidiInitialState state) => StrictJsonV1.SerializeWithFinalLf(
        new GlobalResetDefaultsJsonV1
        {
            SchemaVersion = PersistenceContractV1.SchemaVersion,
            State = MidiStateCodecV1.FromDomain(state)
        },
        MidoraJsonSerializerContextV1.Default.GlobalResetDefaultsJsonV1);
}

internal static class GlobalEventScopeDefaultsCodecV1
{
    public static void Parse(ReadOnlySpan<byte> utf8)
    {
        StrictJsonV1.ValidateInput(utf8);
        GlobalEventScopeDefaultsJsonV1 value = JsonSerializer.Deserialize(
            utf8,
            MidoraJsonSerializerContextV1.Default.GlobalEventScopeDefaultsJsonV1)
            ?? throw new InvalidDataException("global-event-scope-defaults.json cannot be null.");
        if (value.SchemaVersion != PersistenceContractV1.SchemaVersion)
        {
            throw new InvalidDataException("global-event-scope-defaults.json schemaVersion is not v1.");
        }
    }

    public static byte[] Serialize() => StrictJsonV1.SerializeWithFinalLf(
        new GlobalEventScopeDefaultsJsonV1 { SchemaVersion = PersistenceContractV1.SchemaVersion },
        MidoraJsonSerializerContextV1.Default.GlobalEventScopeDefaultsJsonV1);
}

internal static class MidiStateCodecV1
{
    public static MidiStateJsonV1 FromDomain(MidiInitialState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        MidiStateJsonV1 value = new()
        {
            BankMsb = state.BankMsb,
            BankLsb = state.BankLsb,
            Program = state.Program,
            PitchBend = state.PitchBend,
            PitchBendRangeSemitones = state.PitchBendRangeSemitones,
            PitchBendRangeCents = state.PitchBendRangeCents,
            Controllers = ToEntries(state.Controllers),
            RegisteredParameters = ToEntries(state.RegisteredParameters),
            NonRegisteredParameters = ToEntries(state.NonRegisteredParameters)
        };
        Validate(value, "state");
        return value;
    }

    public static void Restore(MidiInitialState target, MidiStateJsonV1 value)
    {
        ArgumentNullException.ThrowIfNull(target);
        Validate(value, "state");
        target.BankMsb = value.BankMsb;
        target.BankLsb = value.BankLsb;
        target.Program = value.Program;
        target.PitchBend = value.PitchBend;
        target.PitchBendRangeSemitones = value.PitchBendRangeSemitones;
        target.PitchBendRangeCents = value.PitchBendRangeCents;
        target.Controllers.Clear();
        target.RegisteredParameters.Clear();
        target.NonRegisteredParameters.Clear();
        AddEntries(target.Controllers, value.Controllers);
        AddEntries(target.RegisteredParameters, value.RegisteredParameters);
        AddEntries(target.NonRegisteredParameters, value.NonRegisteredParameters);
    }

    public static void Validate(MidiStateJsonV1? value, string fieldName)
    {
        if (value is null)
        {
            throw new InvalidDataException($"{fieldName} cannot be null.");
        }
        ValidateOptional7Bit(value.BankMsb, $"{fieldName}.bankMsb");
        ValidateOptional7Bit(value.BankLsb, $"{fieldName}.bankLsb");
        ValidateOptional7Bit(value.Program, $"{fieldName}.program");
        ValidateOptionalPitchBend(value.PitchBend, $"{fieldName}.pitchBend");
        ValidateOptional7Bit(value.PitchBendRangeSemitones, $"{fieldName}.pitchBendRangeSemitones");
        ValidateOptionalRange(value.PitchBendRangeCents, 0, 99, $"{fieldName}.pitchBendRangeCents");
        ValidateEntries(value.Controllers, 119, 127, $"{fieldName}.controllers", rejectEffects: true);
        ValidateEntries(value.RegisteredParameters, 16_383, 16_383, $"{fieldName}.registeredParameters");
        ValidateEntries(value.NonRegisteredParameters, 16_383, 16_383, $"{fieldName}.nonRegisteredParameters");
    }

    private static MidiStateEntryJsonV1[] ToEntries(IReadOnlyDictionary<int, int> values) => values
        .OrderBy(item => item.Key)
        .Select(item => new MidiStateEntryJsonV1 { Number = item.Key, Value = item.Value })
        .ToArray();

    private static void AddEntries(IDictionary<int, int> target, IEnumerable<MidiStateEntryJsonV1> values)
    {
        foreach (MidiStateEntryJsonV1 value in values)
        {
            target.Add(value.Number, value.Value);
        }
    }

    private static void ValidateEntries(
        MidiStateEntryJsonV1[]? values,
        int maximumNumber,
        int maximumValue,
        string fieldName,
        bool rejectEffects = false)
    {
        if (values is null)
        {
            throw new InvalidDataException($"{fieldName} cannot be null.");
        }
        HashSet<int> numbers = [];
        foreach (MidiStateEntryJsonV1 value in values)
        {
            if (value is null
                || value.Number is < 0 || value.Number > maximumNumber
                || value.Value is < 0 || value.Value > maximumValue
                || rejectEffects && value.Number is 91 or 93
                || !numbers.Add(value.Number))
            {
                throw new InvalidDataException($"{fieldName} contains an invalid or duplicate entry.");
            }
        }
    }

    private static void ValidateOptional7Bit(int? value, string fieldName)
    {
        if (value is < 0 or > 127)
        {
            throw new InvalidDataException($"{fieldName} must be in [0, 127].");
        }
    }

    private static void ValidateOptionalPitchBend(int? value, string fieldName)
    {
        if (value is < -8_192 or > 8_191)
        {
            throw new InvalidDataException($"{fieldName} must be in [-8192, 8191].");
        }
    }

    private static void ValidateOptionalRange(int? value, int minimum, int maximum, string fieldName)
    {
        if (value < minimum || value > maximum)
        {
            throw new InvalidDataException($"{fieldName} must be in [{minimum}, {maximum}].");
        }
    }
}
