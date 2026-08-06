using System.Text;
using System.Text.Json;
using Midora.Domain;

namespace Midora.Persistence.Tests;

public sealed class ProjectMetadataPersistenceV1Tests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 6, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public void MetadataRoundTripsAndUsesDeterministicGoldenJson()
    {
        MidoraProject project = new(480, CreatedAt);
        project.Metadata.ProjectName = "Midora Test";
        project.Metadata.ProjectVersion = "v1";
        project.Metadata.AuthorOrTeam = "Team";
        project.Metadata.OriginalWork = "Original";
        project.Metadata.Copyright = "Copyright";
        project.Metadata.Notes = "Line 1\nLine 2";
        ProjectMetadataSnapshot snapshot = project.Metadata.Snapshot() with
        {
            ModifiedAtUtc = CreatedAt.AddSeconds(1),
            TotalEditingTimeMilliseconds = 12_345
        };

        byte[] bytes = MetadataCodecV1.Serialize(snapshot);

        string expected = """
            {
              "schemaVersion": 1,
              "projectName": "Midora Test",
              "projectVersion": "v1",
              "authorOrTeam": "Team",
              "originalWork": "Original",
              "copyright": "Copyright",
              "notes": "Line 1\nLine 2",
              "createdAtUtc": "2026-08-06T01:02:03.0000000Z",
              "modifiedAtUtc": "2026-08-06T01:02:04.0000000Z",
              "totalEditingTimeMilliseconds": 12345
            }

            """;
        Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
        Assert.Equal(snapshot, MetadataCodecV1.Parse(bytes));

        MetadataCodecV1.Restore(project.Metadata, bytes);
        Assert.Equal(snapshot, project.Metadata.Snapshot());
    }

    [Fact]
    public void MetadataRejectsUnknownDuplicateNullInvalidTimeAndNegativeDuration()
    {
        string valid = Encoding.UTF8.GetString(MetadataCodecV1.Serialize(
            new ProjectMetadataSnapshot(
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                CreatedAt,
                CreatedAt,
                0)));

        Assert.Throws<JsonException>(() => MetadataCodecV1.Parse(Encoding.UTF8.GetBytes(
            valid.Replace("\"projectName\": \"\"", "\"unknown\": true", StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => MetadataCodecV1.Parse(Encoding.UTF8.GetBytes(
            valid.Replace("\"projectName\": \"\"", "\"projectName\": \"\",\n  \"projectName\": \"x\"", StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => MetadataCodecV1.Parse(Encoding.UTF8.GetBytes(
            valid.Replace("\"projectName\": \"\"", "\"projectName\": null", StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => MetadataCodecV1.Parse(Encoding.UTF8.GetBytes(
            valid.Replace("2026-08-06T01:02:03.0000000Z", "2026-08-06T01:02:03Z", StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => MetadataCodecV1.Parse(Encoding.UTF8.GetBytes(
            valid.Replace("\"totalEditingTimeMilliseconds\": 0", "\"totalEditingTimeMilliseconds\": -1", StringComparison.Ordinal))));
    }

    [Fact]
    public void MetadataRejectsModifiedTimeBeforeCreationAndScalarOverflow()
    {
        ProjectMetadataSnapshot reversed = new(
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            CreatedAt,
            CreatedAt.AddTicks(-1),
            0);
        Assert.Throws<InvalidDataException>(() => MetadataCodecV1.Serialize(reversed));

        ProjectMetadataSnapshot tooLong = reversed with
        {
            ProjectName = string.Concat(Enumerable.Repeat("\U0001f3b5", 257)),
            ModifiedAtUtc = CreatedAt
        };
        Assert.Throws<InvalidDataException>(() => MetadataCodecV1.Serialize(tooLong));
    }

    [Fact]
    public void CheckedInMetadataSchemaIsStrictDraft202012()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory, "Schemas", "Json", "metadata-v1.schema.json");
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllBytes(path));

        Assert.Equal(
            PersistenceContractV1.JsonSchemaDialect,
            schema.RootElement.GetProperty("$schema").GetString());
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains(
            schema.RootElement.GetProperty("required").EnumerateArray(),
            value => value.GetString() == "totalEditingTimeMilliseconds");
    }
}
