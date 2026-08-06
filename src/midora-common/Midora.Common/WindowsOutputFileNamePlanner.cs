using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace Midora.OutputPlanning;

public sealed record OutputFileNameCandidate(
    string SourceKey,
    long SourceOrder,
    string CandidateStem,
    string Extension);

public sealed record PlannedOutputFileName(
    string SourceKey,
    long SourceOrder,
    string OriginalStem,
    string Extension,
    string FileName,
    int CollisionOrdinal,
    bool WasChanged);

public sealed record OutputFileNameDiagnostic(
    string Code,
    string Message,
    string? SourceKey = null);

public sealed class OutputFileNamePlan
{
    internal OutputFileNamePlan(
        PlannedOutputFileName[] targets,
        OutputFileNameDiagnostic[] diagnostics)
    {
        Targets = Array.AsReadOnly(targets);
        Diagnostics = Array.AsReadOnly(diagnostics);
    }

    public bool Succeeded => Diagnostics.Count == 0;

    public ReadOnlyCollection<PlannedOutputFileName> Targets { get; }

    public ReadOnlyCollection<OutputFileNameDiagnostic> Diagnostics { get; }
}

public static class WindowsOutputFileNamePlanner
{
    public const int MaximumFileNameCodeUnits = 255;

    private const string InvalidRequestCode = "MIDORA-OUTPUT-NAME-INVALID-REQUEST";
    private const string InvalidUnicodeCode = "MIDORA-OUTPUT-NAME-INVALID-UNICODE";
    private const string EmptyStemCode = "MIDORA-OUTPUT-NAME-EMPTY-STEM";
    private const string InvalidExtensionCode = "MIDORA-OUTPUT-NAME-INVALID-EXTENSION";
    private const string CapacityCode = "MIDORA-OUTPUT-NAME-CAPACITY";

    public static OutputFileNamePlan PlanSingleDirectory(
        IEnumerable<OutputFileNameCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        OutputFileNameCandidate?[] materialized = candidates
            .Cast<OutputFileNameCandidate?>()
            .ToArray();
        List<OutputFileNameDiagnostic> diagnostics = [];
        HashSet<string> sourceKeys = new(StringComparer.Ordinal);
        List<PreparedCandidate> prepared = new(materialized.Length);

        int nullCount = materialized.Count(value => value is null);
        for (int index = 0; index < nullCount; index++)
        {
            diagnostics.Add(new(
                InvalidRequestCode,
                "The output filename candidate collection contains a null entry."));
        }
        OutputFileNameCandidate[] stableCandidates = materialized
            .Where(value => value is not null)
            .Select(value => value!)
            .OrderBy(value => value.SourceOrder)
            .ThenBy(value => value.SourceKey, StringComparer.Ordinal)
            .ToArray();

        foreach (OutputFileNameCandidate candidate in stableCandidates)
        {
            if (string.IsNullOrEmpty(candidate.SourceKey))
            {
                diagnostics.Add(new(
                    InvalidRequestCode,
                    "Every output filename candidate must have a non-empty stable source key."));
                continue;
            }
            if (!sourceKeys.Add(candidate.SourceKey))
            {
                diagnostics.Add(new(
                    InvalidRequestCode,
                    "Output filename candidate source keys must be unique within one directory plan.",
                    candidate.SourceKey));
                continue;
            }
            if (!TryNormalizeExtension(candidate.Extension, out string normalizedExtension))
            {
                diagnostics.Add(new(
                    InvalidExtensionCode,
                    "The extension must be a valid NFC Windows filename suffix beginning with a period.",
                    candidate.SourceKey));
                continue;
            }
            if (!TrySanitizeStem(candidate.CandidateStem, out string sanitizedStem, out string? errorCode))
            {
                diagnostics.Add(new(
                    errorCode!,
                    errorCode == InvalidUnicodeCode
                        ? "The candidate stem contains invalid UTF-16 and cannot be normalized to NFC."
                        : "The candidate stem is empty after deterministic Windows filename legalization.",
                    candidate.SourceKey));
                continue;
            }
            prepared.Add(new(candidate, sanitizedStem, normalizedExtension));
        }

        if (diagnostics.Count != 0)
        {
            return Failure(diagnostics);
        }

        PreparedCandidate[] ordered = prepared.ToArray();
        HashSet<string> usedFileNames = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> nextCollisionOrdinals = new(StringComparer.OrdinalIgnoreCase);
        PlannedOutputFileName[] targets = new PlannedOutputFileName[ordered.Length];

        for (int index = 0; index < ordered.Length; index++)
        {
            PreparedCandidate value = ordered[index];
            string collisionKey = value.SanitizedStem + '\0' + value.Extension;
            int collisionOrdinal = 1;

            if (!TryBuildFileName(value.SanitizedStem, value.Extension, string.Empty, out string fileName))
            {
                diagnostics.Add(new(
                    CapacityCode,
                    "The extension and filename budget leave no complete Unicode text element for the stem.",
                    value.Candidate.SourceKey));
                continue;
            }

            if (!usedFileNames.Add(fileName))
            {
                collisionOrdinal = nextCollisionOrdinals.GetValueOrDefault(collisionKey, 2);
                while (true)
                {
                    string suffix = string.Create(
                        CultureInfo.InvariantCulture,
                        $" ({collisionOrdinal})");
                    if (!TryBuildFileName(value.SanitizedStem, value.Extension, suffix, out fileName))
                    {
                        diagnostics.Add(new(
                            CapacityCode,
                            "The collision suffix and extension leave no complete Unicode text element for the stem.",
                            value.Candidate.SourceKey));
                        break;
                    }
                    if (usedFileNames.Add(fileName))
                    {
                        nextCollisionOrdinals[collisionKey] = checked(collisionOrdinal + 1);
                        break;
                    }
                    collisionOrdinal = checked(collisionOrdinal + 1);
                }
                if (diagnostics.Count != 0)
                {
                    continue;
                }
            }
            else
            {
                nextCollisionOrdinals.TryAdd(collisionKey, 2);
            }

            OutputFileNameCandidate original = value.Candidate;
            targets[index] = new(
                original.SourceKey,
                original.SourceOrder,
                original.CandidateStem,
                value.Extension,
                fileName,
                collisionOrdinal,
                !string.Equals(original.CandidateStem + original.Extension, fileName, StringComparison.Ordinal));
        }

        return diagnostics.Count == 0
            ? new(targets, [])
            : Failure(diagnostics);
    }

    internal static OutputStemCandidateState ClassifyCandidateStem(string? candidateStem)
    {
        if (string.IsNullOrWhiteSpace(candidateStem))
        {
            return OutputStemCandidateState.EmptyAfterLegalization;
        }

        return TrySanitizeStem(candidateStem, out _, out string? errorCode)
            ? OutputStemCandidateState.Usable
            : errorCode == InvalidUnicodeCode
                ? OutputStemCandidateState.InvalidUnicode
                : OutputStemCandidateState.EmptyAfterLegalization;
    }

    private static OutputFileNamePlan Failure(List<OutputFileNameDiagnostic> diagnostics) =>
        new([], diagnostics.ToArray());

    private static bool TrySanitizeStem(
        string? candidateStem,
        out string sanitizedStem,
        out string? errorCode)
    {
        sanitizedStem = string.Empty;
        errorCode = null;
        if (candidateStem is null)
        {
            errorCode = EmptyStemCode;
            return false;
        }

        string normalized;
        try
        {
            normalized = candidateStem.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            errorCode = InvalidUnicodeCode;
            return false;
        }

        StringBuilder builder = new(normalized.Length);
        bool pendingReplacement = false;
        foreach (Rune rune in normalized.EnumerateRunes())
        {
            if (IsUnsafe(rune))
            {
                pendingReplacement = true;
                continue;
            }
            if (pendingReplacement)
            {
                builder.Append('_');
                pendingReplacement = false;
            }
            builder.Append(rune.ToString());
        }
        if (pendingReplacement)
        {
            builder.Append('_');
        }

        sanitizedStem = builder.ToString().TrimStart(' ').TrimEnd(' ', '.');
        if (sanitizedStem.Length == 0)
        {
            errorCode = EmptyStemCode;
            return false;
        }
        if (IsReservedDeviceName(sanitizedStem))
        {
            sanitizedStem = "_" + sanitizedStem;
        }
        return true;
    }

    private static bool TryNormalizeExtension(string? extension, out string normalizedExtension)
    {
        normalizedExtension = string.Empty;
        if (string.IsNullOrEmpty(extension))
        {
            return false;
        }
        try
        {
            normalizedExtension = extension.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return false;
        }
        if (normalizedExtension.Length < 2
            || normalizedExtension[0] != '.'
            || normalizedExtension.EndsWith(' ')
            || normalizedExtension.EndsWith('.'))
        {
            return false;
        }
        foreach (Rune rune in normalizedExtension.EnumerateRunes())
        {
            if (IsUnsafe(rune))
            {
                return false;
            }
        }
        return normalizedExtension.Length < MaximumFileNameCodeUnits;
    }

    private static bool TryBuildFileName(
        string sanitizedStem,
        string extension,
        string suffix,
        out string fileName)
    {
        fileName = string.Empty;
        int stemBudget = MaximumFileNameCodeUnits - extension.Length - suffix.Length;
        if (stemBudget <= 0)
        {
            return false;
        }

        string stem = TruncateAtTextElementBoundary(sanitizedStem, stemBudget)
            .TrimEnd(' ', '.');
        if (stem.Length == 0)
        {
            return false;
        }
        if (IsReservedDeviceName(stem))
        {
            stemBudget--;
            if (stemBudget <= 0)
            {
                return false;
            }
            stem = TruncateAtTextElementBoundary(sanitizedStem, stemBudget)
                .TrimEnd(' ', '.');
            if (stem.Length == 0)
            {
                return false;
            }
            stem = "_" + stem;
        }
        fileName = stem + suffix + extension;
        return fileName.Length <= MaximumFileNameCodeUnits;
    }

    private static string TruncateAtTextElementBoundary(string value, int maximumCodeUnits)
    {
        if (value.Length <= maximumCodeUnits)
        {
            return value;
        }

        int[] starts = StringInfo.ParseCombiningCharacters(value);
        int end = 0;
        for (int index = 0; index < starts.Length; index++)
        {
            int candidateEnd = index + 1 < starts.Length ? starts[index + 1] : value.Length;
            if (candidateEnd > maximumCodeUnits)
            {
                break;
            }
            end = candidateEnd;
        }
        return value[..end];
    }

    private static bool IsReservedDeviceName(string stem)
    {
        int period = stem.IndexOf('.');
        ReadOnlySpan<char> prefix = period < 0 ? stem : stem.AsSpan(0, period);
        if (prefix.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || prefix.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || prefix.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
            || prefix.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || prefix.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || prefix.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (prefix.Length != 4
            || (!prefix[..3].Equals("COM", StringComparison.OrdinalIgnoreCase)
                && !prefix[..3].Equals("LPT", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        return prefix[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3';
    }

    private static bool IsUnsafe(Rune rune)
    {
        int value = rune.Value;
        if (value is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')
        {
            return true;
        }
        if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control)
        {
            return true;
        }
        return value is 0x00ad or 0x061c or 0x180e or 0x200b
            or 0x200e or 0x200f or 0x2028 or 0x2029 or 0xfeff
            or >= 0x202a and <= 0x202e
            or >= 0x2060 and <= 0x206f
            or >= 0xfff9 and <= 0xfffb;
    }

    private sealed record PreparedCandidate(
        OutputFileNameCandidate Candidate,
        string SanitizedStem,
        string Extension);
}

internal enum OutputStemCandidateState
{
    Usable,
    EmptyAfterLegalization,
    InvalidUnicode
}
