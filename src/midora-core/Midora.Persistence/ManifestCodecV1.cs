using System.Text.Json;

namespace Midora.Persistence;

internal static class ManifestCodecV1
{
    private const string Magic = "midora-project";
    private static readonly HashSet<string> StructuralKinds = new(StringComparer.Ordinal)
    {
        "core-json",
        "settings-json",
        "conductor-json",
        "event-instrument-pb",
        "logical-track-pb",
        "midi-channel-root-pb",
        "pure-midi-track-pb",
        "pure-midi-content-pack"
    };

    public static ManifestJsonV1 Parse(ReadOnlySpan<byte> utf8)
    {
        StrictJsonV1.ValidateInput(utf8);
        ManifestJsonV1 result = JsonSerializer.Deserialize(
            utf8,
            MidoraJsonSerializerContextV1.Default.ManifestJsonV1)
            ?? throw new InvalidDataException("manifest.json cannot be null.");
        Validate(result, allowUnknownFileKinds: true);
        return result;
    }

    public static ManifestVersionHeaderV1 ReadVersionHeader(ReadOnlySpan<byte> utf8)
    {
        StrictJsonV1.ValidateInput(utf8);
        using JsonDocument document = JsonDocument.Parse(utf8.ToArray());
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("manifest.json root must be an object.");
        }

        string magic = ReadRequiredString(root, "magic");
        if (magic != Magic)
        {
            throw new InvalidDataException("manifest.json magic is invalid.");
        }
        return new ManifestVersionHeaderV1(
            ReadRequiredInt32(root, "fileFormatVersion"),
            ReadRequiredInt32(root, "minimumReadableVersion"),
            ReadRequiredInt32(root, "manifestSchemaVersion"));
    }

    public static byte[] Serialize(ManifestJsonV1 manifest)
    {
        Validate(manifest, allowUnknownFileKinds: false);
        ManifestJsonV1 canonical = new()
        {
            Magic = manifest.Magic,
            FileFormatVersion = manifest.FileFormatVersion,
            MinimumReadableVersion = manifest.MinimumReadableVersion,
            ManifestSchemaVersion = manifest.ManifestSchemaVersion,
            CreatedWithSoftwareVersion = manifest.CreatedWithSoftwareVersion,
            LastSavedWithSoftwareVersion = manifest.LastSavedWithSoftwareVersion,
            Files = manifest.Files
                .OrderBy(file => file.Path, StringComparer.Ordinal)
                .Select(file => new ManifestFileEntryJsonV1
                {
                    Path = file.Path,
                    Kind = file.Kind,
                    SchemaVersion = file.SchemaVersion,
                    Sha256 = file.Sha256
                })
                .ToArray()
        };
        return StrictJsonV1.SerializeWithFinalLf(
            canonical,
            MidoraJsonSerializerContextV1.Default.ManifestJsonV1);
    }

    public static void Validate(ManifestJsonV1 manifest, bool allowUnknownFileKinds = false)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Magic != Magic
            || manifest.FileFormatVersion != PersistenceContractV1.FileFormatVersion
            || manifest.MinimumReadableVersion != PersistenceContractV1.FileFormatVersion
            || manifest.ManifestSchemaVersion != PersistenceContractV1.SchemaVersion)
        {
            throw new InvalidDataException("manifest.json magic or v1 version fields are invalid.");
        }
        PersistenceValueValidationV1.ValidateShortText(
            manifest.CreatedWithSoftwareVersion, nameof(manifest.CreatedWithSoftwareVersion), allowEmpty: false);
        PersistenceValueValidationV1.ValidateShortText(
            manifest.LastSavedWithSoftwareVersion, nameof(manifest.LastSavedWithSoftwareVersion), allowEmpty: false);

        if (manifest.Files is null)
        {
            throw new InvalidDataException("manifest.json files cannot be null.");
        }

        HashSet<string> paths = new(StringComparer.Ordinal);
        foreach (ManifestFileEntryJsonV1 file in manifest.Files)
        {
            if (file is null)
            {
                throw new InvalidDataException("manifest.json files cannot contain null entries.");
            }
            PersistenceValueValidationV1.ValidateRelativePath(file.Path, nameof(file.Path));
            if (!paths.Add(file.Path))
            {
                throw new InvalidDataException($"manifest.json contains duplicate path '{file.Path}'.");
            }
            PersistenceValueValidationV1.ValidateShortText(
                file.Kind, nameof(file.Kind), allowEmpty: false);
            bool structural = StructuralKinds.Contains(file.Kind);
            bool embeddedResource = file.Kind == "embedded-resource";
            bool unknown = !structural && !embeddedResource;
            if (structural && file.SchemaVersion != PersistenceContractV1.SchemaVersion
                || embeddedResource && file.SchemaVersion.HasValue
                || unknown && !allowUnknownFileKinds
                || unknown && file.SchemaVersion is <= 0)
            {
                throw new InvalidDataException($"manifest.json file kind/schemaVersion is invalid for '{file.Path}'.");
            }
            if (file.Sha256.Length != 64 || file.Sha256.Any(character =>
                    character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            {
                throw new InvalidDataException($"manifest.json SHA-256 is not canonical for '{file.Path}'.");
            }
        }
    }

    private static string ReadRequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"manifest.json {name} must be a string.");
        }
        return value.GetString()
            ?? throw new InvalidDataException($"manifest.json {name} cannot be null.");
    }

    private static int ReadRequiredInt32(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out int result))
        {
            throw new InvalidDataException($"manifest.json {name} must be a 32-bit integer.");
        }
        return result;
    }

}

internal readonly record struct ManifestVersionHeaderV1(
    int FileFormatVersion,
    int MinimumReadableVersion,
    int ManifestSchemaVersion);
