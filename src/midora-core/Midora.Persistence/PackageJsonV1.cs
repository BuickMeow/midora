using System.Text.Json.Serialization;

namespace Midora.Persistence;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ProjectJsonV1
{
    [JsonPropertyOrder(0)]
    public required int SchemaVersion { get; init; }

    [JsonPropertyOrder(1)]
    public required StableIdJsonV1 NextStableId { get; init; }

    [JsonPropertyOrder(2)]
    public required string MetadataPath { get; init; }

    [JsonPropertyOrder(3)]
    public required string ConductorTrackPath { get; init; }

    [JsonPropertyOrder(4)]
    public required ProjectSettingsPathsJsonV1 Settings { get; init; }

    [JsonPropertyOrder(5)]
    public required ArrangementParentIndexJsonV1[] ArrangementParents { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ProjectSettingsPathsJsonV1
{
    [JsonPropertyOrder(0)]
    public required string Project { get; init; }

    [JsonPropertyOrder(1)]
    public required string Export { get; init; }

    [JsonPropertyOrder(2)]
    public required string Playback { get; init; }

    [JsonPropertyOrder(3)]
    public required string AudioRender { get; init; }

    [JsonPropertyOrder(4)]
    public required string SoundFont { get; init; }

    [JsonPropertyOrder(5)]
    public required string GlobalResetDefaults { get; init; }

    [JsonPropertyOrder(6)]
    public required string GlobalEventScopeDefaults { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ArrangementParentIndexJsonV1
{
    [JsonPropertyOrder(0)]
    public required string Kind { get; init; }

    [JsonPropertyOrder(1)]
    public required StableIdJsonV1 Id { get; init; }

    [JsonPropertyOrder(2)]
    public required string Path { get; init; }

    [JsonPropertyOrder(3)]
    public required string NameSnapshot { get; init; }

    [JsonPropertyOrder(4)]
    public required ArrangementChildIndexJsonV1[] Children { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ArrangementChildIndexJsonV1
{
    [JsonPropertyOrder(0)]
    public required string Kind { get; init; }

    [JsonPropertyOrder(1)]
    public required StableIdJsonV1 Id { get; init; }

    [JsonPropertyOrder(2)]
    public required StableIdJsonV1 ParentId { get; init; }

    [JsonPropertyOrder(3)]
    public required string Path { get; init; }

    [JsonPropertyOrder(4)]
    public required string NameSnapshot { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ProjectSettingsJsonV1
{
    [JsonPropertyOrder(0)]
    public required int SchemaVersion { get; init; }

    [JsonPropertyOrder(1)]
    public required int TicksPerQuarterNote { get; init; }

    [JsonPropertyOrder(2)]
    public required MidiStateJsonV1 GlobalInitialState { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ExportSettingsJsonV1
{
    [JsonPropertyOrder(0)]
    public required int SchemaVersion { get; init; }

    [JsonPropertyOrder(1)]
    public required string Mode { get; init; }

    [JsonPropertyOrder(2)]
    public required string RangeMode { get; init; }

    [JsonPropertyOrder(3)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ManualStartTick { get; init; }

    [JsonPropertyOrder(4)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ManualEndTick { get; init; }

    [JsonPropertyOrder(5)]
    public required string TrackSelectionMode { get; init; }

    [JsonPropertyOrder(6)]
    public required string Routing { get; init; }

    [JsonPropertyOrder(7)]
    public required bool IncludeReadme { get; init; }

    [JsonPropertyOrder(8)]
    public required bool TreatWarningsAsErrors { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class PlaybackSettingsJsonV1
{
    [JsonPropertyOrder(0)]
    public required int SchemaVersion { get; init; }

    [JsonPropertyOrder(1)]
    public required double MasterVolumeDecibels { get; init; }

    [JsonPropertyOrder(2)]
    public required bool LimiterEnabled { get; init; }

    [JsonPropertyOrder(3)]
    public required string StopCursorBehavior { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class AudioRenderSettingsJsonV1
{
    [JsonPropertyOrder(0)]
    public required int SchemaVersion { get; init; }

    [JsonPropertyOrder(1)]
    public required string Mode { get; init; }

    [JsonPropertyOrder(2)]
    public required string RangeMode { get; init; }

    [JsonPropertyOrder(3)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ManualStartTick { get; init; }

    [JsonPropertyOrder(4)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ManualEndTick { get; init; }

    [JsonPropertyOrder(5)]
    public required string TrackSelectionMode { get; init; }

    [JsonPropertyOrder(6)]
    public required StableIdJsonV1[] ExplicitLogicalTrackIds { get; init; }

    [JsonPropertyOrder(7)]
    public required string Container { get; init; }

    [JsonPropertyOrder(8)]
    public required string ChannelLayout { get; init; }

    [JsonPropertyOrder(9)]
    public required string SampleFormat { get; init; }

    [JsonPropertyOrder(10)]
    public required string Endianness { get; init; }

    [JsonPropertyOrder(11)]
    public required int SampleRate { get; init; }

    [JsonPropertyOrder(12)]
    public required int MaximumSampleVoicesPerUnitStream { get; init; }

    [JsonPropertyOrder(13)]
    public required string TrackFileNamePattern { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class GlobalResetDefaultsJsonV1
{
    [JsonPropertyOrder(0)]
    public required int SchemaVersion { get; init; }

    [JsonPropertyOrder(1)]
    public required MidiStateJsonV1 State { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class GlobalEventScopeDefaultsJsonV1
{
    [JsonPropertyOrder(0)]
    public required int SchemaVersion { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class MidiStateJsonV1
{
    [JsonPropertyOrder(0)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BankMsb { get; init; }

    [JsonPropertyOrder(1)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BankLsb { get; init; }

    [JsonPropertyOrder(2)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Program { get; init; }

    [JsonPropertyOrder(3)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PitchBend { get; init; }

    [JsonPropertyOrder(4)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PitchBendRangeSemitones { get; init; }

    [JsonPropertyOrder(5)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PitchBendRangeCents { get; init; }

    [JsonPropertyOrder(6)]
    public required MidiStateEntryJsonV1[] Controllers { get; init; }

    [JsonPropertyOrder(7)]
    public required MidiStateEntryJsonV1[] RegisteredParameters { get; init; }

    [JsonPropertyOrder(8)]
    public required MidiStateEntryJsonV1[] NonRegisteredParameters { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class MidiStateEntryJsonV1
{
    [JsonPropertyOrder(0)]
    public required int Number { get; init; }

    [JsonPropertyOrder(1)]
    public required int Value { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ConductorTrackJsonV1
{
    [JsonPropertyOrder(0)]
    public required int SchemaVersion { get; init; }

    [JsonPropertyOrder(1)]
    public required TempoChangeJsonV1[] Tempos { get; init; }

    [JsonPropertyOrder(2)]
    public required TimeSignatureChangeJsonV1[] TimeSignatures { get; init; }

    [JsonPropertyOrder(3)]
    public required KeySignatureChangeJsonV1[] KeySignatures { get; init; }

    [JsonPropertyOrder(4)]
    public required ProjectMarkerJsonV1[] Markers { get; init; }

    [JsonPropertyOrder(5)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProjectEndMarkerJsonV1? EndMarker { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class TempoChangeJsonV1
{
    [JsonPropertyOrder(0)]
    public required StableIdJsonV1 Id { get; init; }

    [JsonPropertyOrder(1)]
    public required long Tick { get; init; }

    [JsonPropertyOrder(2)]
    public required decimal BeatsPerMinute { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class TimeSignatureChangeJsonV1
{
    [JsonPropertyOrder(0)]
    public required StableIdJsonV1 Id { get; init; }

    [JsonPropertyOrder(1)]
    public required long Tick { get; init; }

    [JsonPropertyOrder(2)]
    public required int Numerator { get; init; }

    [JsonPropertyOrder(3)]
    public required int Denominator { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class KeySignatureChangeJsonV1
{
    [JsonPropertyOrder(0)]
    public required StableIdJsonV1 Id { get; init; }

    [JsonPropertyOrder(1)]
    public required long Tick { get; init; }

    [JsonPropertyOrder(2)]
    public required int SharpsFlats { get; init; }

    [JsonPropertyOrder(3)]
    public required bool IsMinor { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ProjectMarkerJsonV1
{
    [JsonPropertyOrder(0)]
    public required StableIdJsonV1 Id { get; init; }

    [JsonPropertyOrder(1)]
    public required long Tick { get; init; }

    [JsonPropertyOrder(2)]
    public required string Name { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ProjectEndMarkerJsonV1
{
    [JsonPropertyOrder(0)]
    public required StableIdJsonV1 Id { get; init; }

    [JsonPropertyOrder(1)]
    public required long Tick { get; init; }
}
