using System.Buffers;
using System.Globalization;
using System.Text;

namespace Midora.Domain;

internal static class ProjectTextRules
{
    public const int ShortTextMaximumScalars = 256;

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

    private static int CountScalars(string value)
    {
        int count = 0;
        foreach (Rune _ in value.EnumerateRunes())
        {
            count++;
        }
        return count;
    }
}
