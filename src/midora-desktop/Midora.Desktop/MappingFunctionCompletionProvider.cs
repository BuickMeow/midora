using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Midora.Mapping.Contract.V2;

namespace Midora.Desktop;

internal sealed record MappingFunctionCompletionItem(
    string Text,
    string InsertText,
    int CaretBacktrack,
    string Kind,
    string Description,
    IReadOnlyList<string>? Signatures = null);

internal readonly record struct MappingFunctionBracketMatch(
    int BracketOffset,
    int MatchingOffset,
    bool IsMatched);

internal static class MappingFunctionCompletionProvider
{
    private static readonly CSharpParseOptions ParseOptions = new(
        LanguageVersion.CSharp14,
        DocumentationMode.None,
        SourceCodeKind.Regular);

    private static readonly Lazy<IReadOnlyList<MappingFunctionCompletionItem>> ContextMembers =
        new(CreateContextMembers, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly IReadOnlyList<MappingFunctionCompletionItem> MathMembers =
        CreateMathMembers();
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<MappingFunctionCompletionItem>>
        EnumMembers = new Dictionary<string, IReadOnlyList<MappingFunctionCompletionItem>>(StringComparer.Ordinal)
        {
            [nameof(MappingEventKindV2)] = CreateEnumMembers<MappingEventKindV2>(),
            [nameof(MappingTargetParameterV2)] = CreateEnumMembers<MappingTargetParameterV2>()
        };
    private static readonly IReadOnlyDictionary<string, string> ContextDescriptions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(MappingContextV2.CurrentValue)] = "The value at the current Mapping Step (double).",
            [nameof(MappingContextV2.TriggerNote)] = "The triggering Logical Note number (int).",
            [nameof(MappingContextV2.TriggerVelocity)] = "The triggering Logical Note velocity (int).",
            [nameof(MappingContextV2.GateLength)] = "The triggering Logical Note gate length in ticks (long).",
            [nameof(MappingContextV2.PitchDelta)] = "The pitch offset applied by the current mapping context (int).",
            [nameof(MappingContextV2.TemplateTick)] = "The source Template Event tick (long).",
            [nameof(MappingContextV2.ProjectTick)] = "The absolute Project tick being compiled (long).",
            [nameof(MappingContextV2.TemplateNote)] = "The source SubVoice Template Note number (int).",
            [nameof(MappingContextV2.TemplateVelocity)] = "The source SubVoice Template Note velocity (int).",
            [nameof(MappingContextV2.EffectiveRootNote)] = "The effective Event Instrument root note (int).",
            [nameof(MappingContextV2.CurrentEventId)] = "The current event's stable ID.",
            [nameof(MappingContextV2.CurrentParameter)] = "The current mapping target parameter.",
            [nameof(MappingContextV2.CurrentEventKind)] = "The current mapping event kind.",
            [nameof(MappingContextV2.LogicalParameterId)] = "The source Logical Parameter stable ID, if applicable.",
            [nameof(MappingContextV2.LogicalParameterName)] = "The source Logical Parameter display name, if applicable.",
            [nameof(MappingContextV2.LogicalParameterValue)] = "The held Logical Parameter value (double).",
            [nameof(MappingContextV2.TargetOriginalValue)] = "The target value before the current Mapping Chain (double).",
            [nameof(MappingContextV2.SegmentLocalTick)] = "The current tick relative to the Segment start (long).",
            [nameof(MappingContextV2.TrackId)] = "The current Logical Track stable ID.",
            [nameof(MappingContextV2.SegmentId)] = "The current Segment stable ID.",
            [nameof(MappingContextV2.SubVoiceId)] = "The current SubVoice stable ID.",
            [nameof(MappingContextV2.SubVoiceName)] = "The current SubVoice display name.",
            [nameof(MappingContextV2.SubVoiceIndex)] = "The zero-based SubVoice index (int).",
            [nameof(MappingContextV2.SubVoiceEffectiveRootNote)] = "The effective root note of the current SubVoice (int).",
            [nameof(MappingContextV2.EventInstrumentId)] = "The current Event Instrument stable ID.",
            [nameof(MappingContextV2.EventInstrumentName)] = "The current Event Instrument display name.",
            [nameof(MappingContextV2.EventInstrumentRootNote)] = "The configured Event Instrument root note (int)."
        };

    internal static IReadOnlySet<string> ContextMemberNames { get; } =
        ContextMembers.Value.Select(item => item.Text).ToHashSet(StringComparer.Ordinal);
    internal static IReadOnlySet<string> MathMethodNames { get; } =
        MathMembers.Where(item => item.Kind == "Method")
            .Select(item => item.Text)
            .ToHashSet(StringComparer.Ordinal);
    internal static IReadOnlySet<string> MathConstantNames { get; } =
        MathMembers.Where(item => item.Kind == "Constant")
            .Select(item => item.Text)
            .ToHashSet(StringComparer.Ordinal);
    internal static IReadOnlySet<string> ContractTypeNames { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(MappingEventKindV2),
            nameof(MappingTargetParameterV2)
        };

    public static IReadOnlyList<MappingFunctionCompletionItem> GetCompletions(
        string text,
        int caretOffset)
    {
        text ??= string.Empty;
        caretOffset = Math.Clamp(caretOffset, 0, text.Length);
        if (!IsCodePosition(text, caretOffset)) return [];

        int prefixStart = caretOffset;
        while (prefixStart > 0 && IsIdentifierPart(text[prefixStart - 1])) prefixStart--;
        string prefix = text[prefixStart..caretOffset];

        IEnumerable<MappingFunctionCompletionItem> candidates =
            TryGetMemberReceiver(text, prefixStart, out string receiver)
                ? GetMemberItems(receiver)
                : CreateGlobalItems();
        return candidates
            .Where(item => item.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.Text, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => Priority(item.Kind))
            .ThenBy(item => item.Text, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool IsCodePosition(string text, int caretOffset)
    {
        text ??= string.Empty;
        caretOffset = Math.Clamp(caretOffset, 0, text.Length);
        foreach (SyntaxToken token in SyntaxFactory.ParseTokens(
                     text,
                     options: ParseOptions))
        {
            if (token.Span.Start < caretOffset
                && caretOffset <= token.Span.End
                && IsStringOrCharacterToken(token))
            {
                return false;
            }

            foreach (SyntaxTrivia trivia in token.LeadingTrivia.Concat(token.TrailingTrivia))
            {
                if (trivia.Span.Start < caretOffset
                    && caretOffset <= trivia.Span.End
                    && IsCommentOrDisabledText(trivia))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static IEnumerable<MappingFunctionCompletionItem> GetMemberItems(string receiver)
    {
        if (string.Equals(receiver, "context", StringComparison.Ordinal)) return ContextMembers.Value;
        if (string.Equals(receiver, "Math", StringComparison.Ordinal)) return MathMembers;
        if (EnumMembers.TryGetValue(receiver, out IReadOnlyList<MappingFunctionCompletionItem>? items))
        {
            return items;
        }
        if (receiver == nameof(MappingContextV2.CurrentEventKind))
        {
            return EnumMembers[nameof(MappingEventKindV2)];
        }
        if (receiver == nameof(MappingContextV2.CurrentParameter))
        {
            return EnumMembers[nameof(MappingTargetParameterV2)];
        }
        return [];
    }

    private static IEnumerable<MappingFunctionCompletionItem> CreateGlobalItems()
    {
        yield return new(
            "value",
            "value",
            0,
            "Parameter",
            "The current accumulated Mapping Chain value (double).");
        yield return new(
            "context",
            "context",
            0,
            "Parameter",
            "The read-only bounded Mapping Expression context.");
        yield return new("Math", "Math", 0, "Type", "System.Math static methods and constants.");
        yield return new(nameof(MappingEventKindV2), nameof(MappingEventKindV2), 0, "Enum", "The approved ABI v3 event-kind enum.");
        yield return new(nameof(MappingTargetParameterV2), nameof(MappingTargetParameterV2), 0, "Enum", "The approved ABI v3 target-parameter enum.");

        foreach (MappingFunctionCompletionItem item in MathMembers)
        {
            yield return item;
        }

        foreach ((string text, string description) in new (string, string)[]
                 {
                     ("true", "Boolean true literal."),
                     ("false", "Boolean false literal.")
                 })
        {
            yield return new(text, text, 0, "Keyword", description);
        }
    }

    private static IReadOnlyList<MappingFunctionCompletionItem> CreateContextMembers()
    {
        return typeof(MappingContextV2)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => MappingExpressionLanguageV3.NumericContextFields.Contains(property.Name)
                || MappingExpressionLanguageV3.EnumContextFields.Contains(property.Name))
            .OrderBy(property => property.MetadataToken)
            .Select(property => new MappingFunctionCompletionItem(
                property.Name,
                property.Name,
                0,
                "Property",
                ContextDescriptions.TryGetValue(property.Name, out string? description)
                    ? description + " Its dependency is inferred automatically."
                    : $"{FormatType(property.PropertyType)} context property."))
            .ToArray();
    }

    private static IReadOnlyList<MappingFunctionCompletionItem> CreateMathMembers()
    {
        IEnumerable<MappingFunctionCompletionItem> methods = typeof(Math)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => MappingExpressionLanguageV3.MathMethodNames.Contains(method.Name)
                && method.ReturnType == typeof(double)
                && method.GetParameters().All(parameter => parameter.ParameterType == typeof(double)))
            .GroupBy(method => method.Name, StringComparer.Ordinal)
            .Select(group =>
            {
                string[] signatures = group
                    .Select(FormatSignature)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                return new MappingFunctionCompletionItem(
                    group.Key,
                    group.Key + "()",
                    1,
                    "Method",
                    signatures.Length == 1
                        ? "Implicitly imported System.Math method; the Math. prefix is optional."
                        : $"Implicitly imported System.Math method ({signatures.Length} overloads); the Math. prefix is optional.",
                    signatures);
            });
        return methods
            .Concat(MappingExpressionLanguageV3.MathConstantNames.Select(name =>
                new MappingFunctionCompletionItem(
                    name,
                    name,
                    0,
                    "Constant",
                    $"System.Math.{name}.")))
            .OrderBy(item => item.Text, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<MappingFunctionCompletionItem> CreateEnumMembers<T>()
        where T : struct, Enum => Enum.GetNames<T>()
        .Select(name => new MappingFunctionCompletionItem(
            name,
            name,
            0,
            "Enum member",
            $"{typeof(T).Name}.{name}."))
        .ToArray();

    private static string FormatSignature(MethodInfo method)
    {
        string parameters = string.Join(
            ", ",
            method.GetParameters().Select(parameter =>
                $"{FormatParameterModifier(parameter)}{FormatType(parameter.ParameterType)} {parameter.Name}"));
        return $"{FormatType(method.ReturnType)} {method.Name}({parameters})";
    }

    private static string FormatParameterModifier(ParameterInfo parameter) =>
        parameter.IsOut ? "out " : parameter.ParameterType.IsByRef ? "ref " : string.Empty;

    private static string FormatType(Type type)
    {
        Type effective = type.IsByRef ? type.GetElementType()! : type;
        string suffix = Nullable.GetUnderlyingType(effective) is Type underlying
            ? "?"
            : string.Empty;
        if (Nullable.GetUnderlyingType(effective) is Type nullable) effective = nullable;
        string name = effective == typeof(void) ? "void"
            : effective == typeof(double) ? "double"
            : effective == typeof(float) ? "float"
            : effective == typeof(decimal) ? "decimal"
            : effective == typeof(int) ? "int"
            : effective == typeof(uint) ? "uint"
            : effective == typeof(long) ? "long"
            : effective == typeof(ulong) ? "ulong"
            : effective == typeof(short) ? "short"
            : effective == typeof(ushort) ? "ushort"
            : effective == typeof(byte) ? "byte"
            : effective == typeof(sbyte) ? "sbyte"
            : effective == typeof(bool) ? "bool"
            : effective == typeof(string) ? "string"
            : effective.Name;
        return name + suffix;
    }

    private static bool TryGetMemberReceiver(string text, int prefixStart, out string receiver)
    {
        receiver = string.Empty;
        int dot = prefixStart - 1;
        if (dot < 0 || text[dot] != '.') return false;
        int end = dot;
        int start = end;
        while (start > 0 && IsIdentifierPart(text[start - 1])) start--;
        if (start == end) return false;
        receiver = text[start..end];
        return true;
    }

    private static bool IsStringOrCharacterToken(SyntaxToken token)
    {
        string kind = token.Kind().ToString();
        return kind.Contains("String", StringComparison.Ordinal)
               || kind.Contains("CharacterLiteral", StringComparison.Ordinal)
               || kind.Contains("Interpolated", StringComparison.Ordinal)
                  && kind.Contains("Text", StringComparison.Ordinal);
    }

    private static bool IsCommentOrDisabledText(SyntaxTrivia trivia) => trivia.Kind() is
        SyntaxKind.SingleLineCommentTrivia
        or SyntaxKind.MultiLineCommentTrivia
        or SyntaxKind.SingleLineDocumentationCommentTrivia
        or SyntaxKind.MultiLineDocumentationCommentTrivia
        or SyntaxKind.DisabledTextTrivia;

    private static bool IsIdentifierPart(char value) => char.IsLetterOrDigit(value) || value == '_';

    private static int Priority(string kind) => kind switch
    {
        "Parameter" => 0,
        "Property" => 1,
        "Method" => 2,
        "Constant" => 3,
        "Enum member" => 4,
        "Type" or "Enum" => 5,
        "Keyword" => 6,
        _ => 7
    };
}

internal static class MappingFunctionBracketMatcher
{
    private static readonly CSharpParseOptions ParseOptions = new(
        LanguageVersion.CSharp14,
        DocumentationMode.None,
        SourceCodeKind.Regular);

    public static bool TryFind(
        string text,
        int caretOffset,
        out MappingFunctionBracketMatch match)
    {
        text ??= string.Empty;
        caretOffset = Math.Clamp(caretOffset, 0, text.Length);
        int bracketOffset = IsBracketAt(text, caretOffset - 1)
            ? caretOffset - 1
            : IsBracketAt(text, caretOffset)
                ? caretOffset
                : -1;
        if (bracketOffset < 0)
        {
            match = default;
            return false;
        }

        SyntaxToken[] brackets = SyntaxFactory.ParseTokens(
                text,
                options: ParseOptions)
            .Where(token => IsBracketToken(token.Kind()))
            .ToArray();
        if (!brackets.Any(token => token.SpanStart == bracketOffset))
        {
            match = default;
            return false;
        }

        Dictionary<int, int> pairs = [];
        Stack<SyntaxToken> stack = [];
        foreach (SyntaxToken token in brackets)
        {
            if (IsOpen(token.Kind()))
            {
                stack.Push(token);
                continue;
            }
            if (stack.TryPeek(out SyntaxToken open)
                && IsPair(open.Kind(), token.Kind()))
            {
                _ = stack.Pop();
                pairs[open.SpanStart] = token.SpanStart;
                pairs[token.SpanStart] = open.SpanStart;
            }
        }

        match = pairs.TryGetValue(bracketOffset, out int counterpart)
            ? new(bracketOffset, counterpart, true)
            : new(bracketOffset, -1, false);
        return true;
    }

    private static bool IsBracketAt(string text, int offset) =>
        (uint)offset < (uint)text.Length && text[offset] is '(' or ')' or '[' or ']' or '{' or '}';

    private static bool IsBracketToken(SyntaxKind kind) => IsOpen(kind) || kind is
        SyntaxKind.CloseParenToken or SyntaxKind.CloseBracketToken or SyntaxKind.CloseBraceToken;

    private static bool IsOpen(SyntaxKind kind) => kind is
        SyntaxKind.OpenParenToken or SyntaxKind.OpenBracketToken or SyntaxKind.OpenBraceToken;

    private static bool IsPair(SyntaxKind open, SyntaxKind close) => (open, close) is
        (SyntaxKind.OpenParenToken, SyntaxKind.CloseParenToken)
        or (SyntaxKind.OpenBracketToken, SyntaxKind.CloseBracketToken)
        or (SyntaxKind.OpenBraceToken, SyntaxKind.CloseBraceToken);
}
