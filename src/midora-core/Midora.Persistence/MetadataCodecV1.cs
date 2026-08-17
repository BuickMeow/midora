using System.Text.Json;
using Midora.Domain;

namespace Midora.Persistence;

internal static class MetadataCodecV1
{
    public static ProjectMetadataSnapshot Parse(ReadOnlySpan<byte> utf8)
    {
        StrictJsonV1.ValidateInput(utf8);
        MetadataJsonV1 value = JsonSerializer.Deserialize(
            utf8,
            MidoraJsonSerializerContextV1.Default.MetadataJsonV1)
            ?? throw new InvalidDataException("metadata.json cannot be null.");
        return ToDomain(value);
    }

    public static void Restore(ProjectMetadata metadata, ReadOnlySpan<byte> utf8)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        metadata.Restore(Parse(utf8));
    }

    public static byte[] Serialize(ProjectMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return Serialize(metadata.Snapshot());
    }

    public static byte[] Serialize(ProjectMetadataSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Validate(snapshot);
        MetadataJsonV1 value = new()
        {
            SchemaVersion = PersistenceContractV1.SchemaVersion,
            ProjectName = snapshot.ProjectName,
            ProjectVersion = snapshot.ProjectVersion,
            AuthorOrTeam = snapshot.AuthorOrTeam,
            OriginalWork = snapshot.OriginalWork,
            Copyright = snapshot.Copyright,
            CreatedAtUtc = PersistenceContractV1.FormatUtcTimestamp(snapshot.CreatedAtUtc),
            ModifiedAtUtc = PersistenceContractV1.FormatUtcTimestamp(snapshot.ModifiedAtUtc),
            TotalEditingTimeMilliseconds = snapshot.TotalEditingTimeMilliseconds
        };
        return StrictJsonV1.SerializeWithFinalLf(
            value,
            MidoraJsonSerializerContextV1.Default.MetadataJsonV1);
    }

    private static ProjectMetadataSnapshot ToDomain(MetadataJsonV1 value)
    {
        if (value.SchemaVersion != PersistenceContractV1.SchemaVersion)
        {
            throw new InvalidDataException("metadata.json schemaVersion is not v1.");
        }
        if (!PersistenceContractV1.TryParseUtcTimestamp(value.CreatedAtUtc, out DateTimeOffset createdAtUtc)
            || !PersistenceContractV1.TryParseUtcTimestamp(value.ModifiedAtUtc, out DateTimeOffset modifiedAtUtc))
        {
            throw new InvalidDataException("metadata.json contains a non-canonical UTC timestamp.");
        }

        ProjectMetadataSnapshot snapshot = new(
            Require(value.ProjectName, "projectName"),
            Require(value.ProjectVersion, "projectVersion"),
            Require(value.AuthorOrTeam, "authorOrTeam"),
            Require(value.OriginalWork, "originalWork"),
            Require(value.Copyright, "copyright"),
            createdAtUtc,
            modifiedAtUtc,
            value.TotalEditingTimeMilliseconds);
        Validate(snapshot);
        return snapshot;
    }

    private static void Validate(ProjectMetadataSnapshot snapshot)
    {
        PersistenceValueValidationV1.ValidateShortText(snapshot.ProjectName, "projectName");
        PersistenceValueValidationV1.ValidateShortText(snapshot.ProjectVersion, "projectVersion");
        PersistenceValueValidationV1.ValidateMetadataText(snapshot.AuthorOrTeam, "authorOrTeam");
        PersistenceValueValidationV1.ValidateMetadataText(snapshot.OriginalWork, "originalWork");
        PersistenceValueValidationV1.ValidateMetadataText(snapshot.Copyright, "copyright");
        PersistenceValueValidationV1.ValidateEditingDuration(
            snapshot.TotalEditingTimeMilliseconds,
            "totalEditingTimeMilliseconds");
        if (snapshot.ModifiedAtUtc < snapshot.CreatedAtUtc)
        {
            throw new InvalidDataException("modifiedAtUtc cannot precede createdAtUtc.");
        }
    }

    private static string Require(string? value, string fieldName) =>
        value ?? throw new InvalidDataException($"metadata.json {fieldName} cannot be null.");
}
