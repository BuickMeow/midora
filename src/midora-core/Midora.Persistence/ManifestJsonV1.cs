using System.Text.Json.Serialization;

namespace Midora.Persistence;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ManifestJsonV1
{
    [JsonPropertyOrder(0)]
    public required string Magic { get; init; }

    [JsonPropertyOrder(1)]
    public required int FileFormatVersion { get; init; }

    [JsonPropertyOrder(2)]
    public required int MinimumReadableVersion { get; init; }

    [JsonPropertyOrder(3)]
    public required int ManifestSchemaVersion { get; init; }

    [JsonPropertyOrder(4)]
    public required string CreatedWithSoftwareVersion { get; init; }

    [JsonPropertyOrder(5)]
    public required string LastSavedWithSoftwareVersion { get; init; }

    [JsonPropertyOrder(6)]
    public required ManifestFileEntryJsonV1[] Files { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ManifestFileEntryJsonV1
{
    [JsonPropertyOrder(0)]
    public required string Path { get; init; }

    [JsonPropertyOrder(1)]
    public required string Kind { get; init; }

    [JsonPropertyOrder(2)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SchemaVersion { get; init; }

    [JsonPropertyOrder(3)]
    public required string Sha256 { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ManifestJsonV1))]
[JsonSerializable(typeof(MetadataJsonV1))]
[JsonSerializable(typeof(SoundFontSettingsJsonV1))]
[JsonSerializable(typeof(ProjectJsonV1))]
[JsonSerializable(typeof(ProjectSettingsJsonV1))]
[JsonSerializable(typeof(ExportSettingsJsonV1))]
[JsonSerializable(typeof(PlaybackSettingsJsonV1))]
[JsonSerializable(typeof(AudioRenderSettingsJsonV1))]
[JsonSerializable(typeof(GlobalResetDefaultsJsonV1))]
[JsonSerializable(typeof(GlobalEventScopeDefaultsJsonV1))]
[JsonSerializable(typeof(ConductorTrackJsonV1))]
internal sealed partial class MidoraJsonSerializerContextV1 : JsonSerializerContext;
