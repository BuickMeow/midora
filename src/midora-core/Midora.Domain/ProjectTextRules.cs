using System.Buffers;
using System.Globalization;
using System.Text;

namespace Midora.Domain;

internal static class ProjectTextRules
{
    public const int ShortTextMaximumScalars = 256;
    public const int MetadataTextMaximumScalars = 4_096;
    public const int DescriptionMaximumScalars = 65_536;
    public const int MappingBodyMaximumScalars = 1_048_576;

    public static string NormalizeShortText(
        string value,
        bool allowEmpty,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        ValidateUnicodeAndControls(value, parameterName);
        string normalized = value.Trim();
        int scalarCount = CountScalars(normalized);
        if (!allowEmpty && scalarCount == 0 || scalarCount > ShortTextMaximumScalars)
        {
            throw new ArgumentException(
                $"The value must contain {(allowEmpty ? 0 : 1)} to {ShortTextMaximumScalars} Unicode scalars.",
                parameterName);
        }
        return normalized;
    }

    public static string? ValidateDescription(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        int scalarCount = 0;
        ReadOnlySpan<char> remaining = value;
        while (!remaining.IsEmpty)
        {
            OperationStatus status = Rune.DecodeFromUtf16(
                remaining,
                out Rune rune,
                out int charactersConsumed);
            if (status != OperationStatus.Done)
            {
                throw new ArgumentException("The value contains invalid Unicode.", parameterName);
            }
            if (rune.Value == 0
                || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control
                && rune.Value is not '\t' and not '\n' and not '\r')
            {
                throw new ArgumentException(
                    "The value contains a forbidden control character.",
                    parameterName);
            }
            scalarCount++;
            remaining = remaining[charactersConsumed..];
        }
        if (scalarCount > DescriptionMaximumScalars)
        {
            throw new ArgumentException(
                $"The value must contain at most {DescriptionMaximumScalars} Unicode scalars.",
                parameterName);
        }
        return value;
    }

    public static string ValidateShortTextContent(
        string value,
        string parameterName) =>
        ValidateSingleLineContent(
            value,
            ShortTextMaximumScalars,
            parameterName);

    public static string ValidateMetadataText(
        string value,
        string parameterName) =>
        ValidateSingleLineContent(
            value,
            MetadataTextMaximumScalars,
            parameterName);

    public static string ValidateMappingBody(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        int scalarCount = CountScalars(value, parameterName);
        if (scalarCount > MappingBodyMaximumScalars)
        {
            throw new ArgumentException(
                $"The value must contain at most {MappingBodyMaximumScalars} Unicode scalars.",
                parameterName);
        }
        return value;
    }

    private static void ValidateUnicodeAndControls(string value, string parameterName)
    {
        ReadOnlySpan<char> remaining = value;
        while (!remaining.IsEmpty)
        {
            OperationStatus status = Rune.DecodeFromUtf16(
                remaining,
                out Rune rune,
                out int charactersConsumed);
            if (status != OperationStatus.Done)
            {
                throw new ArgumentException("The value contains invalid Unicode.", parameterName);
            }
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control)
            {
                throw new ArgumentException(
                    "The value contains a forbidden control character.",
                    parameterName);
            }
            remaining = remaining[charactersConsumed..];
        }
    }

    private static string ValidateSingleLineContent(
        string value,
        int maximumScalars,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        ValidateUnicodeAndControls(value, parameterName);
        if (CountScalars(value) > maximumScalars)
        {
            throw new ArgumentException(
                $"The value must contain at most {maximumScalars} Unicode scalars.",
                parameterName);
        }
        return value;
    }

    private static int CountScalars(string value)
    {
        int count = 0;
        foreach (Rune _ in value.EnumerateRunes())
        {
            count++;
        }
        return count;
    }

    private static int CountScalars(string value, string parameterName)
    {
        int count = 0;
        ReadOnlySpan<char> remaining = value;
        while (!remaining.IsEmpty)
        {
            OperationStatus status = Rune.DecodeFromUtf16(
                remaining,
                out _,
                out int charactersConsumed);
            if (status != OperationStatus.Done)
            {
                throw new ArgumentException("The value contains invalid Unicode.", parameterName);
            }
            count++;
            remaining = remaining[charactersConsumed..];
        }
        return count;
    }
}
