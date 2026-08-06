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
        "logical-track-pb"
    };

    public static ManifestJsonV1 Parse(ReadOnlySpan<byte> utf8)
    {
        RejectBomAndDuplicateProperties(utf8);
        ManifestJsonV1 result = JsonSerializer.Deserialize(
            utf8,
            MidoraJsonSerializerContextV1.Default.ManifestJsonV1)
            ?? throw new InvalidDataException("manifest.json cannot be null.");
        Validate(result, allowUnknownFileKinds: true);
        return result;
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
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(
            canonical,
            MidoraJsonSerializerContextV1.Default.ManifestJsonV1);
        int carriageReturnCount = body.Count(value => value == (byte)'\r');
        byte[] result = GC.AllocateUninitializedArray<byte>(body.Length - carriageReturnCount + 1);
        int destination = 0;
        foreach (byte value in body)
        {
            if (value != (byte)'\r')
            {
                result[destination++] = value;
            }
        }
        result[^1] = (byte)'\n';
        return result;
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

    private static void RejectBomAndDuplicateProperties(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length >= 3
            && utf8[0] == 0xef
            && utf8[1] == 0xbb
            && utf8[2] == 0xbf)
        {
            throw new InvalidDataException("Midora JSON must be UTF-8 without BOM.");
        }
        using JsonDocument document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 100
        });
        ValidateNoDuplicateProperties(document.RootElement, "$");
    }

    private static void ValidateNoDuplicateProperties(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException($"Duplicate JSON property '{property.Name}' at {path}.");
                }
                ValidateNoDuplicateProperties(property.Value, $"{path}.{property.Name}");
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in value.EnumerateArray())
            {
                ValidateNoDuplicateProperties(item, $"{path}[{index++}]");
            }
        }
    }
}
