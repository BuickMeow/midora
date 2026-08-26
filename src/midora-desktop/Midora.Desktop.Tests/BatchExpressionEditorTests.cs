using Midora.Compiler;
using System.Xml.Linq;
using Xunit;

namespace Midora.Desktop.Tests;

public sealed class BatchExpressionEditorTests
{
    [Theory]
    [InlineData("BatchExpressionEditor.xaml")]
    [InlineData("MappingFunctionCodeEditor.xaml")]
    public void CompletionListsCommitOnOnePointerClick(string fileName)
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "midora-desktop",
            "Midora.Desktop",
            fileName);
        XDocument document = XDocument.Load(path);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement list = document.Descendants(presentation + "ListBox").Single(element =>
            string.Equals((string?)element.Attribute(x + "Name"), "CompletionList", StringComparison.Ordinal));

        Assert.Equal(
            "OnCompletionMouseLeftButtonDown",
            (string?)list.Attribute("PreviewMouseLeftButtonDown"));
        Assert.Null(list.Attribute("MouseDoubleClick"));
    }

    [Fact]
    public void NoteCompletionExposesContextVariablesButRejectsTheCurrentResultVariable()
    {
        IReadOnlyList<BatchExpressionVariable> variables =
            BatchExpressionCompletionProvider.CreateVariables(
                BatchEditPresetKind.Note,
                BatchEditField.Velocity);

        Assert.Contains(variables, item => item.Name == "v0");
        Assert.DoesNotContain(variables, item => item.Name == "v1");
        Assert.Contains(variables, item => item.Name == "k0");
        Assert.Contains(variables, item => item.Name == "k1");
        Assert.Contains(variables, item => item.Name == "g0");
        Assert.Contains(variables, item => item.Name == "g1");
        Assert.Contains(variables, item => item.Name == "t0");
        Assert.Contains(variables, item => item.Name == "t1");
        Assert.Contains(variables, item => item.Name == "tr");
        Assert.DoesNotContain(variables, item => item.Name is "p0" or "p1");
    }

    [Fact]
    public void PointCompletionOnlyExposesPointAndTickContext()
    {
        IReadOnlyList<BatchExpressionVariable> variables =
            BatchExpressionCompletionProvider.CreateVariables(
                BatchEditPresetKind.Event,
                BatchEditField.PointValue);

        Assert.Contains(variables, item => item.Name == "p0");
        Assert.DoesNotContain(variables, item => item.Name == "p1");
        Assert.Contains(variables, item => item.Name == "t0");
        Assert.Contains(variables, item => item.Name == "t1");
        Assert.Contains(variables, item => item.Name == "tr");
        Assert.DoesNotContain(variables, item => item.Name is "v0" or "k0" or "g0");
    }

    [Fact]
    public void CompletionSupportsStaticImportAndMathMemberAccess()
    {
        IReadOnlyList<BatchExpressionVariable> variables =
            BatchExpressionCompletionProvider.CreateVariables(
                BatchEditPresetKind.Note,
                BatchEditField.Velocity);

        BatchExpressionCompletionItem direct = Assert.Single(
            BatchExpressionCompletionProvider.GetCompletions("=Cl", 3, variables),
            item => item.Text == "Clamp");
        BatchExpressionCompletionItem qualified = Assert.Single(
            BatchExpressionCompletionProvider.GetCompletions("=Math.Cl", 8, variables),
            item => item.Text == "Clamp");

        Assert.Equal("Clamp()", direct.InsertText);
        Assert.Equal(1, direct.CaretBacktrack);
        Assert.NotEmpty(direct.Signatures!);
        Assert.Equal(direct, qualified);
    }

    [Fact]
    public void CompletionIsDisabledOutsideExpressionModeAndExcludesNonNumericMathResults()
    {
        IReadOnlyList<BatchExpressionVariable> variables =
            BatchExpressionCompletionProvider.CreateVariables(
                BatchEditPresetKind.Note,
                BatchEditField.Tick);

        Assert.Empty(BatchExpressionCompletionProvider.GetCompletions("Cl", 2, variables));
        IReadOnlyList<BatchExpressionCompletionItem> all =
            BatchExpressionCompletionProvider.GetCompletions("=", 1, variables);
        Assert.Contains(all, item => item.Text == "Clamp");
        Assert.DoesNotContain(all, item => item.Text is "DivRem" or "SinCos");
    }

    [Fact]
    public void CompletionRefreshPublishesDetachedSnapshotsInsteadOfMutatingTheBoundList()
    {
        IReadOnlyList<BatchExpressionVariable> variables =
            BatchExpressionCompletionProvider.CreateVariables(
                BatchEditPresetKind.Note,
                BatchEditField.Tick);
        IReadOnlyList<BatchExpressionCompletionItem> all =
            BatchExpressionCompletionProvider.GetCompletions("=", 1, variables);
        IReadOnlyList<BatchExpressionCompletionItem> filtered =
            BatchExpressionCompletionProvider.GetCompletions("=Cl", 3, variables);

        IReadOnlyList<BatchExpressionCompletionItem> first =
            BatchExpressionCompletionSnapshot.Create(all);
        IReadOnlyList<BatchExpressionCompletionItem> second =
            BatchExpressionCompletionSnapshot.Create(filtered);

        Assert.NotSame(all, first);
        Assert.NotSame(filtered, second);
        Assert.NotSame(first, second);
        Assert.True(first.Count > second.Count);
        Assert.Equal("Clamp", Assert.Single(second).Text);
    }

    [Theory]
    [InlineData("=Clamp((v0 + k0) * 2, 1, 127)", 8, 15)]
    [InlineData("=Math.Max(v0, (k0 + 1))", 15, 21)]
    public void BracketMatcherFindsNestedPairs(string text, int caretOffset, int expectedMatch)
    {
        Assert.True(BatchBracketMatcher.TryFind(text, caretOffset, out BatchBracketMatch match));
        Assert.True(match.IsMatched);
        Assert.Equal(expectedMatch, match.MatchingOffset);
    }

    [Fact]
    public void BracketMatcherReportsUnmatchedBracket()
    {
        const string text = "=Clamp(v0, 1, 127";

        Assert.True(BatchBracketMatcher.TryFind(text, 7, out BatchBracketMatch match));
        Assert.False(match.IsMatched);
        Assert.Equal(6, match.BracketOffset);
        Assert.Equal(-1, match.MatchingOffset);
    }

    [Fact]
    public void HelpPresentsSixIncreasingExamplesThatAllCompile()
    {
        IReadOnlyList<BatchEditHelpExample> examples = BatchEditHelpDialog.ExampleDefinitions;

        Assert.Equal(6, examples.Count);
        Assert.Equal(["1", "2", "3", "4", "5", "6"], examples.Select(example => example.Number));
        Assert.Equal("BASIC", examples[0].Level);
        Assert.StartsWith("ADVANCED", examples[^1].Level, StringComparison.Ordinal);
        Assert.Contains("Sin", examples[^1].Code, StringComparison.Ordinal);
        Assert.Contains("tr", examples[^1].Code, StringComparison.Ordinal);

        foreach (BatchEditHelpExample example in examples)
        {
            bool point = example.Field == "Point Value";
            Dictionary<BatchEditField, string?> formulas = point
                ? new()
                {
                    [BatchEditField.PointValue] = string.Empty,
                    [BatchEditField.Tick] = string.Empty
                }
                : new()
                {
                    [BatchEditField.Velocity] = string.Empty,
                    [BatchEditField.KeyNumber] = string.Empty,
                    [BatchEditField.Gate] = string.Empty,
                    [BatchEditField.Tick] = string.Empty
                };
            formulas[example.Field switch
            {
                "Velocity" => BatchEditField.Velocity,
                "Point Value" => BatchEditField.PointValue,
                "Gate" => BatchEditField.Gate,
                "Tick" => BatchEditField.Tick,
                _ => throw new InvalidOperationException($"Unknown help field '{example.Field}'.")
            }] = example.Code;

            using BatchEditExpressionProgram program = BatchEditExpressionProgram.Compile(formulas);
            Assert.NotNull(program);
        }

        Dictionary<BatchEditField, string?> sineFormula = new()
        {
            [BatchEditField.PointValue] = examples[^1].Code,
            [BatchEditField.Tick] = string.Empty
        };
        using BatchEditExpressionProgram sineProgram = BatchEditExpressionProgram.Compile(sineFormula);
        System.Diagnostics.Stopwatch timeout = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(-64, EvaluateSine(0), 8);
        Assert.Equal(0, EvaluateSine(96), 8);
        Assert.Equal(64, EvaluateSine(192), 8);

        double EvaluateSine(double relativeTick) => sineProgram.Evaluate(
            new BatchEditValues(0, 0, 0, 0, relativeTick, relativeTick),
            timeout,
            TimeSpan.FromSeconds(10)).PointValue;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(
                    current.FullName,
                    "src",
                    "midora-desktop",
                    "Midora.Desktop")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Could not locate the Midora repository above '{AppContext.BaseDirectory}'.");
    }
}
