using System.Globalization;
using System.Text;

namespace Midora.Persistence;

internal static class PersistenceValueValidationV1
{
    public static void ValidateStableId(ulong high, ulong low, string fieldName)
    {
        if (high == 0 && low == 0)
        {
            throw new InvalidDataException($"{fieldName} stable ID zero is reserved.");
        }
    }

    public static void ValidateStableIdText(string value, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 32
            || value.All(character => character == '0')
            || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException($"{fieldName} is not a canonical nonzero stable ID.");
        }
    }

    public static void ValidateEditingDuration(long milliseconds, string fieldName)
    {
        if (milliseconds < 0)
        {
            throw new InvalidDataException($"{fieldName} must be a nonnegative Int64 millisecond value.");
        }
    }

    public static void ValidateShortText(string value, string fieldName, bool allowEmpty = true) =>
        ValidateSingleLine(value, fieldName, PersistenceContractV1.ShortTextMaximumScalars, allowEmpty);

    public static void ValidateMetadataText(string value, string fieldName, bool allowEmpty = true) =>
        ValidateSingleLine(value, fieldName, PersistenceContractV1.MetadataTextMaximumScalars, allowEmpty);

    public static void ValidateDescription(string value, string fieldName)
    {
        int count = PersistenceContractV1.ValidateUnicodeAndCountScalars(value, fieldName);
        if (count > PersistenceContractV1.DescriptionMaximumScalars)
        {
            throw new InvalidDataException(
                $"{fieldName} exceeds {PersistenceContractV1.DescriptionMaximumScalars} Unicode scalars.");
        }
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (rune.Value == 0 || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control
                && rune.Value is not '\t' and not '\n' and not '\r')
            {
                throw new InvalidDataException($"{fieldName} contains a forbidden control character.");
            }
        }
    }

    public static void ValidateMappingBody(string value, string fieldName)
    {
        int count = PersistenceContractV1.ValidateUnicodeAndCountScalars(value, fieldName);
        if (count > PersistenceContractV1.MappingBodyMaximumScalars)
        {
            throw new InvalidDataException(
                $"{fieldName} exceeds {PersistenceContractV1.MappingBodyMaximumScalars} Unicode scalars.");
        }
    }

    public static void ValidateRelativePath(string value, string fieldName)
    {
        int count = PersistenceContractV1.ValidateUnicodeAndCountScalars(value, fieldName);
        if (count == 0 || count > PersistenceContractV1.RelativePathMaximumScalars)
        {
            throw new InvalidDataException(
                $"{fieldName} must contain 1 to {PersistenceContractV1.RelativePathMaximumScalars} Unicode scalars.");
        }
        if (value[0] == '/' || value.Contains('\\')
            || value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':')
        {
            throw new InvalidDataException($"{fieldName} must be a canonical relative path using '/'.");
        }
        foreach (string segment in value.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                throw new InvalidDataException($"{fieldName} contains an empty or traversal segment.");
            }
        }
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (rune.Value == 0 || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control)
            {
                throw new InvalidDataException($"{fieldName} contains a forbidden control character.");
            }
        }
    }

    public static void ValidateRgb(uint red, uint green, uint blue, string fieldName)
    {
        if (red > byte.MaxValue || green > byte.MaxValue || blue > byte.MaxValue)
        {
            throw new InvalidDataException($"{fieldName} RGB channels must be in [0, 255].");
        }
    }

    private static void ValidateSingleLine(string value, string fieldName, int maximum, bool allowEmpty)
    {
        int count = PersistenceContractV1.ValidateUnicodeAndCountScalars(value, fieldName);
        if (!allowEmpty && count == 0 || count > maximum)
        {
            throw new InvalidDataException(
                $"{fieldName} must contain {(allowEmpty ? 0 : 1)} to {maximum} Unicode scalars.");
        }
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control)
            {
                throw new InvalidDataException($"{fieldName} contains a forbidden control character.");
            }
        }
    }
}
