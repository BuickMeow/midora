using System.Text;
using Midora.OutputPlanning;

namespace Midora.Common.Tests;

public sealed class WindowsOutputFileNamePlannerTests
{
    [Fact]
    public void GoldenLegalizesToReadableNfcWhilePreservingTextShapingCharacters()
    {
        string original = " Cafe\u0301:<\u0001\u202e>👩\u200d💻️. ";

        OutputFileNamePlan plan = Plan(Candidate("track", 0, original, ".wav"));

        Assert.True(plan.Succeeded);
        PlannedOutputFileName target = Assert.Single(plan.Targets);
        Assert.Equal("Café_👩‍💻️.wav", target.FileName);
        Assert.True(target.FileName.IsNormalized(NormalizationForm.FormC));
        Assert.True(target.WasChanged);
        Assert.Equal(1, target.CollisionOrdinal);
    }

    [Fact]
    public void ReplacesEveryWin32ReservedCharacterAsOneContinuousRun()
    {
        OutputFileNamePlan plan = Plan(Candidate("source", 0, "A<>:\"/\\|?*B", ".wav"));

        Assert.True(plan.Succeeded);
        Assert.Equal("A_B.wav", Assert.Single(plan.Targets).FileName);
    }

    [Fact]
    public void RemovesOnlyOuterAsciiSpacesAndTrailingPeriods()
    {
        OutputFileNamePlan plan = Plan(Candidate("source", 0, "  .Name.  ", ".wav"));

        Assert.True(plan.Succeeded);
        Assert.Equal(".Name.wav", Assert.Single(plan.Targets).FileName);
    }

    [Theory]
    [InlineData("CON", "_CON.mid")]
    [InlineData("CONIN$", "_CONIN$.mid")]
    [InlineData("conout$.log", "_conout$.log.mid")]
    [InlineData("con.txt", "_con.txt.mid")]
    [InlineData("AUX", "_AUX.mid")]
    [InlineData("NUL.tar", "_NUL.tar.mid")]
    [InlineData("COM1", "_COM1.mid")]
    [InlineData("com¹", "_com¹.mid")]
    [InlineData("LPT9", "_LPT9.mid")]
    [InlineData("lpt³.data", "_lpt³.data.mid")]
    [InlineData("COM0", "COM0.mid")]
    [InlineData("COM10", "COM10.mid")]
    [InlineData("CONSOLE", "CONSOLE.mid")]
    public void AppliesTheExactWindowsReservedDeviceNameProfile(string stem, string expected)
    {
        OutputFileNamePlan plan = Plan(Candidate("source", 0, stem, ".mid"));

        Assert.True(plan.Succeeded);
        Assert.Equal(expected, Assert.Single(plan.Targets).FileName);
    }

    [Theory]
    [InlineData("\u0000")]
    [InlineData("\u001f")]
    [InlineData("\u007f")]
    [InlineData("\u0085")]
    [InlineData("\u00ad")]
    [InlineData("\u061c")]
    [InlineData("\u180e")]
    [InlineData("\u200b")]
    [InlineData("\u200e")]
    [InlineData("\u200f")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    [InlineData("\u202a")]
    [InlineData("\u202e")]
    [InlineData("\u2060")]
    [InlineData("\u206f")]
    [InlineData("\ufeff")]
    [InlineData("\ufff9")]
    [InlineData("\ufffb")]
    public void ReplacesTheFixedControlAndSecurityInvisibleProfile(string unsafeValue)
    {
        OutputFileNamePlan plan = Plan(Candidate("source", 0, $"A{unsafeValue}B", ".wav"));

        Assert.True(plan.Succeeded);
        Assert.Equal("A_B.wav", Assert.Single(plan.Targets).FileName);
    }

    [Fact]
    public void ReplacesEveryCodePointInTheFixedSecurityInvisibleRanges()
    {
        int[] codePoints =
        [
            0x00ad, 0x061c, 0x180e, 0x200b, 0x200e, 0x200f,
            0x2028, 0x2029, 0xfeff,
            .. Enumerable.Range(0x202a, 0x202e - 0x202a + 1),
            .. Enumerable.Range(0x2060, 0x206f - 0x2060 + 1),
            .. Enumerable.Range(0xfff9, 0xfffb - 0xfff9 + 1)
        ];

        foreach (int codePoint in codePoints)
        {
            string unsafeValue = char.ConvertFromUtf32(codePoint);
            OutputFileNamePlan plan = Plan(Candidate("source", 0, $"A{unsafeValue}B", ".wav"));

            Assert.True(plan.Succeeded);
            Assert.Equal("A_B.wav", Assert.Single(plan.Targets).FileName);
        }
    }

    [Fact]
    public void PreservesZwnjZwjVariationSelectorsAndEmojiTagCharacters()
    {
        string preserved = "A\u200cB\u200dC\ufe0f\U000e0067\U000e007f";

        OutputFileNamePlan plan = Plan(Candidate("source", 0, preserved, ".wav"));

        Assert.True(plan.Succeeded);
        Assert.Equal(preserved + ".wav", Assert.Single(plan.Targets).FileName);
    }

    [Fact]
    public void CollisionsUseStableSourceOrderAndIgnoreInputEnumerationOrder()
    {
        OutputFileNameCandidate[] candidates =
        [
            Candidate("first", 0, "Piano", ".wav"),
            Candidate("second", 1, "piano", ".wav"),
            Candidate("literal-suffix", 2, "Piano (2)", ".wav"),
            Candidate("cafe-first", 3, "Cafe\u0301", ".wav"),
            Candidate("cafe-second", 4, "Café", ".wav")
        ];

        OutputFileNamePlan forward = Plan(candidates);
        OutputFileNamePlan reverse = Plan(candidates.Reverse().ToArray());

        Assert.True(forward.Succeeded);
        Assert.True(reverse.Succeeded);
        string[] expected =
        [
            "Piano.wav",
            "piano (2).wav",
            "Piano (2) (2).wav",
            "Café.wav",
            "Café (2).wav"
        ];
        Assert.Equal(expected, forward.Targets.Select(value => value.FileName));
        Assert.Equal(
            forward.Targets.Select(value => (value.SourceKey, value.FileName)),
            reverse.Targets.Select(value => (value.SourceKey, value.FileName)));
    }

    [Fact]
    public void TruncatesAtTextElementBoundariesAndBudgetsForCollisionSuffixes()
    {
        string longStem = new string('a', 250) + "👩\u200d💻";

        OutputFileNamePlan plan = Plan(
            Candidate("first", 0, longStem, ".wav"),
            Candidate("second", 1, longStem, ".wav"));

        Assert.True(plan.Succeeded);
        Assert.Equal(new string('a', 250) + ".wav", plan.Targets[0].FileName);
        Assert.Equal(new string('a', 247) + " (2).wav", plan.Targets[1].FileName);
        Assert.All(
            plan.Targets,
            value => Assert.True(value.FileName.Length <= WindowsOutputFileNamePlanner.MaximumFileNameCodeUnits));
    }

    [Fact]
    public void ReappliesTrailingSpaceAndPeriodRemovalAfterLengthTruncation()
    {
        string stem = new string('a', 250) + "." + new string('z', 20);

        OutputFileNamePlan plan = Plan(Candidate("source", 0, stem, ".wav"));

        Assert.True(plan.Succeeded);
        Assert.Equal(new string('a', 250) + ".wav", Assert.Single(plan.Targets).FileName);
    }

    [Fact]
    public void FailsWhenNoCompleteTextElementFitsTheExtensionBudget()
    {
        string extension = "." + new string('x', 250);

        OutputFileNamePlan plan = Plan(Candidate("source", 0, "👩\u200d💻", extension));

        Assert.False(plan.Succeeded);
        Assert.Empty(plan.Targets);
        Assert.Contains(plan.Diagnostics, value => value.Code == "MIDORA-OUTPUT-NAME-CAPACITY");
    }

    [Fact]
    public void UsesSourceKeyAsDeterministicTieBreakerForEqualSourceOrder()
    {
        OutputFileNamePlan plan = Plan(
            Candidate("b", 0, "Name", ".mid"),
            Candidate("a", 0, "name", ".mid"));

        Assert.True(plan.Succeeded);
        Assert.Collection(
            plan.Targets,
            value =>
            {
                Assert.Equal("a", value.SourceKey);
                Assert.Equal("name.mid", value.FileName);
            },
            value =>
            {
                Assert.Equal("b", value.SourceKey);
                Assert.Equal("Name (2).mid", value.FileName);
            });
    }

    [Theory]
    [InlineData(" . ", ".wav", "MIDORA-OUTPUT-NAME-EMPTY-STEM")]
    [InlineData("Name", "wav", "MIDORA-OUTPUT-NAME-INVALID-EXTENSION")]
    [InlineData("Name", ".wa?", "MIDORA-OUTPUT-NAME-INVALID-EXTENSION")]
    public void InvalidCandidatesFailAtomically(string stem, string extension, string expectedCode)
    {
        OutputFileNamePlan plan = Plan(
            Candidate("valid", 0, "Valid", ".wav"),
            Candidate("invalid", 1, stem, extension));

        Assert.False(plan.Succeeded);
        Assert.Empty(plan.Targets);
        Assert.Contains(plan.Diagnostics, value => value.Code == expectedCode && value.SourceKey == "invalid");
    }

    [Fact]
    public void InvalidUtf16AndDuplicateStableKeysFailAtomically()
    {
        OutputFileNamePlan invalidUnicode = Plan(Candidate("bad", 0, "\ud800", ".wav"));
        OutputFileNamePlan duplicateKeys = Plan(
            Candidate("same", 0, "One", ".wav"),
            Candidate("same", 1, "Two", ".wav"));

        Assert.False(invalidUnicode.Succeeded);
        Assert.Empty(invalidUnicode.Targets);
        Assert.Contains(invalidUnicode.Diagnostics, value => value.Code == "MIDORA-OUTPUT-NAME-INVALID-UNICODE");
        Assert.False(duplicateKeys.Succeeded);
        Assert.Empty(duplicateKeys.Targets);
        Assert.Contains(duplicateKeys.Diagnostics, value => value.Code == "MIDORA-OUTPUT-NAME-INVALID-REQUEST");
    }

    [Fact]
    public void InvalidDiagnosticsUseStableSourceOrderInsteadOfInputEnumerationOrder()
    {
        OutputFileNameCandidate[] candidates =
        [
            Candidate("later", 2, "Name", "wav"),
            Candidate("earlier", 1, " . ", ".wav")
        ];

        OutputFileNamePlan forward = Plan(candidates);
        OutputFileNamePlan reverse = Plan(candidates.Reverse().ToArray());

        Assert.False(forward.Succeeded);
        Assert.Equal(
            new[] { "earlier", "later" },
            forward.Diagnostics.Select(value => value.SourceKey));
        Assert.Equal(forward.Diagnostics, reverse.Diagnostics);
    }

    [Fact]
    public void ExistingFilesystemStateIsNotAnInputToThePureFilenamePlan()
    {
        OutputFileNameCandidate candidate = Candidate("source", 0, "Existing", ".wav");

        OutputFileNamePlan first = Plan(candidate);
        OutputFileNamePlan second = Plan(candidate);

        Assert.True(first.Succeeded);
        Assert.Equal("Existing.wav", Assert.Single(first.Targets).FileName);
        Assert.Equal(first.Targets, second.Targets);
    }

    private static OutputFileNameCandidate Candidate(
        string sourceKey,
        long sourceOrder,
        string stem,
        string extension) => new(sourceKey, sourceOrder, stem, extension);

    private static OutputFileNamePlan Plan(params OutputFileNameCandidate[] candidates) =>
        WindowsOutputFileNamePlanner.PlanSingleDirectory(candidates);
}
