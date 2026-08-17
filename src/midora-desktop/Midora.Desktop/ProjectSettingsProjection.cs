using System.Globalization;
using Midora.Application;
using Midora.Domain;

namespace Midora.Desktop;

internal static class ProjectSettingsProjection
{
    public static IProjectEditCommand CreateEditCommand(
        MidoraProject project,
        InspectorField field)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(field);
        string value = field.Value.Trim();

        if (field.Key.StartsWith("settings.project.", StringComparison.Ordinal))
        {
            ProjectMetadata metadata = project.Metadata;
            return ProjectDomainEditCommands.UpdateProjectMetadata(
                field.Key == "settings.project.name" ? field.Value : metadata.ProjectName,
                field.Key == "settings.project.version" ? field.Value : metadata.ProjectVersion,
                field.Key == "settings.project.author" ? field.Value : metadata.AuthorOrTeam,
                field.Key == "settings.project.originalWork" ? field.Value : metadata.OriginalWork,
                field.Key == "settings.project.copyright" ? field.Value : metadata.Copyright);
        }

        if (field.Key.StartsWith("settings.playback.", StringComparison.Ordinal))
        {
            PlaybackProjectSettings settings = project.Playback;
            return ProjectDomainEditCommands.UpdatePlaybackSettings(
                field.Key == "settings.playback.master" ? Double(value, field.Label) : settings.MasterVolumeDecibels,
                field.Key == "settings.playback.limiter" ? Bool(value, field.Label) : settings.LimiterEnabled,
                field.Key == "settings.playback.stopCursor"
                    ? EnumValue<StopCursorBehavior>(value, field.Label)
                    : settings.StopCursorBehavior);
        }

        if (field.Key.StartsWith("settings.midi.", StringComparison.Ordinal))
        {
            ExportProjectSettings settings = project.Export;
            return ProjectDomainEditCommands.UpdateMidiExportSettings(
                field.Key == "settings.midi.mode" ? EnumValue<ProjectMidiExportMode>(value, field.Label) : settings.Mode,
                field.Key == "settings.midi.rangeMode" ? EnumValue<ProjectRangeMode>(value, field.Label) : settings.RangeMode,
                field.Key == "settings.midi.start" ? NullableLong(value, field.Label) : settings.ManualStartTick,
                field.Key == "settings.midi.end" ? NullableLong(value, field.Label) : settings.ManualEndTick,
                field.Key == "settings.midi.trackSelection" ? EnumValue<ProjectMidiExportTrackSelectionMode>(value, field.Label) : settings.TrackSelectionMode,
                field.Key == "settings.midi.routing" ? EnumValue<ProjectMidiExportRoutingStrategy>(value, field.Label) : settings.Routing,
                field.Key == "settings.midi.readme" ? Bool(value, field.Label) : settings.IncludeReadme,
                field.Key == "settings.midi.warnings" ? Bool(value, field.Label) : settings.TreatWarningsAsErrors);
        }

        if (field.Key.StartsWith("settings.audio.", StringComparison.Ordinal))
        {
            AudioRenderProjectSettings settings = project.AudioRender;
            return ProjectDomainEditCommands.UpdateAudioRenderSettings(
                field.Key == "settings.audio.mode" ? EnumValue<AudioRenderMode>(value, field.Label) : settings.Mode,
                field.Key == "settings.audio.rangeMode" ? EnumValue<ProjectRangeMode>(value, field.Label) : settings.RangeMode,
                field.Key == "settings.audio.start" ? NullableLong(value, field.Label) : settings.ManualStartTick,
                field.Key == "settings.audio.end" ? NullableLong(value, field.Label) : settings.ManualEndTick,
                field.Key == "settings.audio.trackSelection" ? EnumValue<ProjectTrackSelectionMode>(value, field.Label) : settings.TrackSelectionMode,
                settings.ExplicitLogicalTrackIds,
                field.Key == "settings.audio.sampleRate" ? Int(value, field.Label) : settings.SampleRate,
                field.Key == "settings.audio.voices" ? Int(value, field.Label) : settings.MaximumSampleVoicesPerUnitStream);
        }

        if (field.Key.StartsWith("settings.initial.", StringComparison.Ordinal))
        {
            return ProjectDomainEditCommands.UpdateProjectInitialStateValue(
                ParseStateTarget(field.Key["settings.initial.".Length..]),
                NullableInt(value, field.Label));
        }

        if (field.Key.StartsWith("settings.reset.", StringComparison.Ordinal))
        {
            return ProjectDomainEditCommands.UpdateProjectResetDefaultValue(
                ParseStateTarget(field.Key["settings.reset.".Length..]),
                NullableInt(value, field.Label));
        }

        throw new InvalidOperationException("This Project Settings field is read-only.");
    }

    private static bool Bool(string value, string label) => bool.TryParse(value, out bool result)
        ? result : throw new FormatException($"{label} must be True or False.");

    private static int Int(string value, string label) => int.TryParse(
        value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
        ? result : throw new FormatException($"{label} must be a base-10 integer.");

    private static long? NullableLong(string value, string label)
    {
        if (value.Length == 0) return null;
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result)
            ? result : throw new FormatException($"{label} must be blank or a base-10 integer.");
    }

    private static int? NullableInt(string value, string label)
    {
        if (value.Length == 0) return null;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result : throw new FormatException($"{label} must be blank or a base-10 integer.");
    }

    private static MidiValueTarget ParseStateTarget(string key)
    {
        if (TryNumberedTarget(key, "cc.", MidiValueKind.ControlChange, out MidiValueTarget target)
            || TryNumberedTarget(key, "rpn.", MidiValueKind.RegisteredParameter, out target)
            || TryNumberedTarget(key, "nrpn.", MidiValueKind.NonRegisteredParameter, out target))
        {
            return target;
        }
        return key switch
        {
            "bankMsb" => new(MidiValueKind.BankMsb),
            "bankLsb" => new(MidiValueKind.BankLsb),
            "program" => new(MidiValueKind.Program),
            "pitchBend" => new(MidiValueKind.PitchBend),
            "pitchRangeSemitones" => new(MidiValueKind.PitchBendRangeSemitones),
            "pitchRangeCents" => new(MidiValueKind.PitchBendRangeCents),
            _ => throw new InvalidOperationException("This MIDI State target is unsupported.")
        };
    }

    private static bool TryNumberedTarget(
        string key,
        string prefix,
        MidiValueKind kind,
        out MidiValueTarget target)
    {
        target = default;
        if (!key.StartsWith(prefix, StringComparison.Ordinal)
            || !int.TryParse(key[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int number))
        {
            return false;
        }
        target = new(kind, number);
        return true;
    }

    private static double Double(string value, string label) => double.TryParse(
        value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result)
        ? result : throw new FormatException($"{label} must be a decimal number using '.'.");

    private static T EnumValue<T>(string value, string label) where T : struct, Enum =>
        Enum.TryParse(value, ignoreCase: true, out T result) && Enum.IsDefined(result)
            ? result
            : throw new FormatException($"{label} is not a supported {typeof(T).Name} value.");
}
