using System.Text.Json;
using Midora.Domain;

namespace Midora.Persistence;

internal static class SoundFontSettingsCodecV1
{
    private const string NoneMode = "none";
    private const string ExternalMode = "external";
    private const string EmbeddedMode = "embedded";

    public static ProjectSoundFontReference? Parse(ReadOnlySpan<byte> utf8)
    {
        StrictJsonV1.ValidateInput(utf8);
        SoundFontSettingsJsonV1 value = JsonSerializer.Deserialize(
            utf8,
            MidoraJsonSerializerContextV1.Default.SoundFontSettingsJsonV1)
            ?? throw new InvalidDataException("soundfont-settings.json cannot be null.");
        return ToDomain(value);
    }

    public static byte[] Serialize(ProjectSoundFontSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        SoundFontSettingsJsonV1 value = settings.Reference switch
        {
            null => new SoundFontSettingsJsonV1
            {
                SchemaVersion = PersistenceContractV1.SchemaVersion,
                Mode = NoneMode
            },
            ExternalProjectSoundFontReference external => new SoundFontSettingsJsonV1
            {
                SchemaVersion = PersistenceContractV1.SchemaVersion,
                Mode = ExternalMode,
                RelativePath = external.RelativePath,
                OriginalFileName = external.OriginalFileName,
                Sha256 = external.Sha256,
                FileSizeBytes = external.FileSizeBytes
            },
            EmbeddedProjectSoundFontReference embedded => new SoundFontSettingsJsonV1
            {
                SchemaVersion = PersistenceContractV1.SchemaVersion,
                Mode = EmbeddedMode,
                ResourceId = new StableIdJsonV1(embedded.ResourceId.Value),
                OriginalFileName = embedded.OriginalFileName,
                Sha256 = embedded.Sha256,
                FileSizeBytes = embedded.FileSizeBytes
            },
            _ => throw new InvalidDataException("Unknown Project SoundFont reference type.")
        };
        return StrictJsonV1.SerializeWithFinalLf(
            value,
            MidoraJsonSerializerContextV1.Default.SoundFontSettingsJsonV1);
    }

    private static ProjectSoundFontReference? ToDomain(SoundFontSettingsJsonV1 value)
    {
        if (value.SchemaVersion != PersistenceContractV1.SchemaVersion)
        {
            throw new InvalidDataException("soundfont-settings.json schemaVersion is not v1.");
        }

        return value.Mode switch
        {
            NoneMode when AllPayloadFieldsAreNull(value) => null,
            ExternalMode when value.RelativePath is not null
                && value.ResourceId is null
                && RequiredContentFieldsExist(value) =>
                CreateExternal(value),
            EmbeddedMode when value.RelativePath is null
                && value.ResourceId is not null
                && RequiredContentFieldsExist(value) =>
                CreateEmbedded(value),
            _ => throw new InvalidDataException(
                "soundfont-settings.json mode and payload fields are inconsistent.")
        };
    }

    private static ExternalProjectSoundFontReference CreateExternal(SoundFontSettingsJsonV1 value)
    {
        try
        {
            return new ExternalProjectSoundFontReference(
                value.RelativePath!,
                value.OriginalFileName!,
                value.Sha256!,
                value.FileSizeBytes!.Value);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Invalid external SoundFont reference.", exception);
        }
    }

    private static EmbeddedProjectSoundFontReference CreateEmbedded(SoundFontSettingsJsonV1 value)
    {
        if (value.ResourceId is not StableIdJsonV1 resourceIdValue || resourceIdValue.Value <= 0)
        {
            throw new InvalidDataException("Embedded SoundFont resourceId is not canonical.");
        }
        MidoraId resourceId = resourceIdValue.ToDomain();
        try
        {
            return new EmbeddedProjectSoundFontReference(
                resourceId,
                value.OriginalFileName!,
                value.Sha256!,
                value.FileSizeBytes!.Value);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Invalid embedded SoundFont reference.", exception);
        }
    }

    private static bool RequiredContentFieldsExist(SoundFontSettingsJsonV1 value) =>
        value.OriginalFileName is not null
        && value.Sha256 is not null
        && value.FileSizeBytes is >= 0;

    private static bool AllPayloadFieldsAreNull(SoundFontSettingsJsonV1 value) =>
        value.RelativePath is null
        && value.ResourceId is null
        && value.OriginalFileName is null
        && value.Sha256 is null
        && value.FileSizeBytes is null;
}
