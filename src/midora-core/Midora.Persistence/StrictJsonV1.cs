using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Midora.Persistence;

internal static class StrictJsonV1
{
    public static void ValidateInput(ReadOnlySpan<byte> utf8)
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

    public static byte[] SerializeWithFinalLf<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        int carriageReturnCount = body.Count(item => item == (byte)'\r');
        byte[] result = GC.AllocateUninitializedArray<byte>(body.Length - carriageReturnCount + 1);
        int destination = 0;
        foreach (byte item in body)
        {
            if (item != (byte)'\r')
            {
                result[destination++] = item;
            }
        }
        result[^1] = (byte)'\n';
        return result;
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
