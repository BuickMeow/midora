using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Midora.Domain;

namespace Midora.Persistence;

[JsonConverter(typeof(StableIdJsonConverterV1))]
internal readonly record struct StableIdJsonV1
{
    public StableIdJsonV1(long value)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Stable IDs must be positive.");
        }

        Value = value;
    }

    public long Value { get; }

    public MidoraId ToDomain() => new(Value);

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

internal sealed class StableIdJsonConverterV1 : JsonConverter<StableIdJsonV1>
{
    public override StableIdJsonV1 Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number)
        {
            throw new JsonException("A stable ID must be a JSON integer.");
        }

        string token = reader.HasValueSequence
            ? Encoding.UTF8.GetString(reader.ValueSequence.ToArray())
            : Encoding.UTF8.GetString(reader.ValueSpan);
        if (token.Length == 0 || token[0] is < '1' or > '9')
        {
            throw new JsonException("A stable ID must use canonical positive decimal notation.");
        }
        for (int index = 1; index < token.Length; index++)
        {
            if (token[index] is < '0' or > '9')
            {
                throw new JsonException("A stable ID must use canonical positive decimal notation.");
            }
        }
        if (!reader.TryGetInt64(out long value) || value <= 0)
        {
            throw new JsonException("A stable ID is outside the supported positive Int64 range.");
        }

        return new StableIdJsonV1(value);
    }

    public override void Write(
        Utf8JsonWriter writer,
        StableIdJsonV1 value,
        JsonSerializerOptions options) => writer.WriteNumberValue(value.Value);
}
