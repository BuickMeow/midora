using System.Globalization;
using System.Text;

namespace Midora.Persistence;

public static class PersistenceContractV1
{
    public const int FileFormatVersion = 1;
    public const int SchemaVersion = 1;
    public const string JsonSchemaDialect = "https://json-schema.org/draft/2020-12/schema";
    public const string ProtobufEdition = "2024";
    public const string GoogleProtobufVersion = "3.35.1";
    public const string GrpcToolsVersion = "2.83.0";

    public const int ShortTextMaximumScalars = 256;
    public const int MetadataTextMaximumScalars = 4_096;
    public const int DescriptionMaximumScalars = 65_536;
    public const int MappingBodyMaximumScalars = 8_192;
    public const int RelativePathMaximumScalars = 4_096;

    public const string UtcTimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string FormatUtcTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(UtcTimestampFormat, CultureInfo.InvariantCulture);

    public static bool TryParseUtcTimestamp(string? value, out DateTimeOffset result) =>
        DateTimeOffset.TryParseExact(
            value,
            UtcTimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out result);

    internal static int ValidateUnicodeAndCountScalars(string value, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidDataException($"{fieldName} must contain valid Unicode.", exception);
        }

        return value.EnumerateRunes().Count();
    }
}
