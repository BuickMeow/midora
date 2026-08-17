using System.Text.Json.Serialization;

namespace Midora.Persistence;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class MetadataJsonV1
{
    [JsonPropertyOrder(0)]
    public required int SchemaVersion { get; init; }

    [JsonPropertyOrder(1)]
    public required string ProjectName { get; init; }

    [JsonPropertyOrder(2)]
    public required string ProjectVersion { get; init; }

    [JsonPropertyOrder(3)]
    public required string AuthorOrTeam { get; init; }

    [JsonPropertyOrder(4)]
    public required string OriginalWork { get; init; }

    [JsonPropertyOrder(5)]
    public required string Copyright { get; init; }

    [JsonPropertyOrder(6)]
    public required string CreatedAtUtc { get; init; }

    [JsonPropertyOrder(7)]
    public required string ModifiedAtUtc { get; init; }

    [JsonPropertyOrder(8)]
    public required long TotalEditingTimeMilliseconds { get; init; }
}
