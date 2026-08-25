using Midora.Mapping.Contract.V2;
using Xunit;

namespace Midora.Desktop.Tests;

public sealed class MappingFunctionCodeEditorTests
{
    [Fact]
    public void ContextCompletionIsRestrictedToTheAbiV3Whitelist()
    {
        IReadOnlyList<MappingFunctionCompletionItem> completion =
            MappingFunctionCompletionProvider.GetCompletions("context.", "context.".Length);

        string[] expected = MappingExpressionLanguageV3.NumericContextFields
            .Concat(MappingExpressionLanguageV3.EnumContextFields)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected, completion.Select(item => item.Text).Order(StringComparer.Ordinal));
        Assert.All(completion, item =>
        {
            Assert.Equal("Property", item.Kind);
            Assert.Contains("inferred automatically", item.Description, StringComparison.Ordinal);
        });
        Assert.DoesNotContain(completion, item => item.Text.EndsWith("Id", StringComparison.Ordinal));
        Assert.DoesNotContain(completion, item => item.Text.EndsWith("Name", StringComparison.Ordinal));
    }

    [Fact]
    public void CompletionProvidesFixedParametersMathAndContractEnums()
    {
        IReadOnlyList<MappingFunctionCompletionItem> globals =
            MappingFunctionCompletionProvider.GetCompletions("", 0);
        Assert.Contains(globals, item => item.Text == "value" && item.Kind == "Parameter");
        Assert.Contains(globals, item => item.Text == "context" && item.Kind == "Parameter");
        Assert.Contains(globals, item => item.Text == "Sin" && item.Kind == "Method");
        Assert.Contains(globals, item => item.Text == "PI" && item.Kind == "Constant");
        Assert.DoesNotContain(globals, item => item.Text == nameof(MappingContextV2));
        Assert.DoesNotContain(globals, item => item.Text == "return");

        MappingFunctionCompletionItem sine = Assert.Single(
            MappingFunctionCompletionProvider.GetCompletions("Math.Si", "Math.Si".Length),
            item => item.Text == "Sin");
        Assert.Equal("Sin()", sine.InsertText);
        Assert.Equal(1, sine.CaretBacktrack);
        Assert.NotEmpty(sine.Signatures!);

        MappingFunctionCompletionItem implicitSine = Assert.Single(
            MappingFunctionCompletionProvider.GetCompletions("Si", "Si".Length),
            item => item.Text == "Sin");
        Assert.Equal("Sin()", implicitSine.InsertText);
        Assert.All(implicitSine.Signatures!, signature =>
            Assert.DoesNotContain("Math.", signature, StringComparison.Ordinal));

        IReadOnlyList<MappingFunctionCompletionItem> eventKinds =
            MappingFunctionCompletionProvider.GetCompletions(
                "MappingEventKindV2.P",
                "MappingEventKindV2.P".Length);
        Assert.Contains(eventKinds, item => item.Text == nameof(MappingEventKindV2.ProgramChange));
        Assert.Contains(eventKinds, item => item.Text == nameof(MappingEventKindV2.PitchBend));

        IReadOnlyList<MappingFunctionCompletionItem> eventKindValues =
            MappingFunctionCompletionProvider.GetCompletions(
                "context.CurrentEventKind.P",
                "context.CurrentEventKind.P".Length);
        Assert.Contains(eventKindValues, item => item.Text == nameof(MappingEventKindV2.ProgramChange));
    }

    [Theory]
    [InlineData("return \"context.Cur\";", 19)]
    [InlineData("// context.Cur", 14)]
    [InlineData("/* Math.Si */", 10)]
    public void CompletionDoesNotOpenInsideStringsOrComments(string text, int caret)
    {
        Assert.False(MappingFunctionCompletionProvider.IsCodePosition(text, caret));
        Assert.Empty(MappingFunctionCompletionProvider.GetCompletions(text, caret));
    }

    [Fact]
    public void BracketMatcherUsesExpressionTokensAndIgnoresBracketsInComments()
    {
        const string body = "Math.Max(value, (1 + 2)) // )";
        int open = body.IndexOf('(', StringComparison.Ordinal);
        int close = body.IndexOf("))", StringComparison.Ordinal) + 1;

        Assert.True(MappingFunctionBracketMatcher.TryFind(
            body,
            open + 1,
            out MappingFunctionBracketMatch match));
        Assert.True(match.IsMatched);
        Assert.Equal(close, match.MatchingOffset);

        string stringBody = "\"(\"";
        Assert.False(MappingFunctionBracketMatcher.TryFind(
            stringBody,
            stringBody.IndexOf('(', StringComparison.Ordinal) + 1,
            out _));
    }

    [Fact]
    public void BracketMatcherReportsAnUnmatchedCodeBracket()
    {
        const string body = "Math.Max(value, (1 + 2)";
        int open = body.IndexOf('(', StringComparison.Ordinal);

        Assert.True(MappingFunctionBracketMatcher.TryFind(
            body,
            open + 1,
            out MappingFunctionBracketMatch match));
        Assert.False(match.IsMatched);
        Assert.Equal(-1, match.MatchingOffset);
    }
}
