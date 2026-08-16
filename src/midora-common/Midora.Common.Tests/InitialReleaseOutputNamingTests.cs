using System.Text;
using Midora.OutputPlanning;

namespace Midora.Common.Tests;

public sealed class InitialReleaseOutputNamingTests
{
    [Fact]
    public void WholeProjectMidiUsesProjectThenSavedStemThenFixedFallback()
    {
        Assert.Equal(
            new[] { "Project.mid", "README.md" },
            FileNames(InitialReleaseOutputNaming.PlanWholeProjectMidi("Project", "Saved", includeReadme: true)));
        Assert.Equal(
            new[] { "Saved.mid" },
            FileNames(InitialReleaseOutputNaming.PlanWholeProjectMidi(" . ", "Saved", includeReadme: false)));
        Assert.Equal(
            new[] { InitialReleaseOutputNaming.WholeProjectMidiFallbackFileName },
            FileNames(InitialReleaseOutputNaming.PlanWholeProjectMidi(null, "...", includeReadme: false)));
    }

    [Fact]
    public void WholeProjectAudioUsesProjectThenSavedStemThenFixedFallback()
    {
        Assert.Equal(
            new[] { "Project.wav" },
            FileNames(InitialReleaseOutputNaming.PlanWholeProjectAudio("Project", "Saved")));
        Assert.Equal(
            new[] { "Saved.wav" },
            FileNames(InitialReleaseOutputNaming.PlanWholeProjectAudio("\t", "Saved")));
        Assert.Equal(
            new[] { InitialReleaseOutputNaming.WholeProjectAudioFallbackFileName },
            FileNames(InitialReleaseOutputNaming.PlanWholeProjectAudio(".", null)));
    }

    [Fact]
    public void WholeProjectNamesUseTheCommonLegalizerInsteadOfTreatingWindowsCharactersAsMissing()
    {
        OutputFileNamePlan midi = InitialReleaseOutputNaming.PlanWholeProjectMidi(
            "Project:Name",
            "Saved",
            includeReadme: false);
        OutputFileNamePlan audio = InitialReleaseOutputNaming.PlanWholeProjectAudio("Project:Name", "Saved");

        Assert.Equal(new[] { "Project_Name.mid" }, FileNames(midi));
        Assert.Equal(new[] { "Project_Name.wav" }, FileNames(audio));
    }

    [Fact]
    public void InvalidUtf16FailsPlanningInsteadOfSilentlyChoosingAnotherSource()
    {
        OutputFileNamePlan plan = InitialReleaseOutputNaming.PlanWholeProjectMidi(
            "\ud800",
            "Saved",
            includeReadme: false);

        Assert.False(plan.Succeeded);
        Assert.Empty(plan.Targets);
        Assert.Contains(
            plan.Diagnostics,
            value => value.Code == "MIDORA-OUTPUT-NAME-INVALID-UNICODE");
    }

    [Fact]
    public void WholeProjectFileNameIsNormalizedButTheSourceIsNotChanged()
    {
        const string decomposed = "Cafe\u0301";

        OutputFileNamePlan plan = InitialReleaseOutputNaming.PlanWholeProjectAudio(decomposed, null);

        Assert.Equal("Café.wav", Assert.Single(plan.Targets).FileName);
        Assert.Equal(decomposed, Assert.Single(plan.Targets).OriginalStem);
        Assert.False(decomposed.IsNormalized(NormalizationForm.FormC));
    }

    [Fact]
    public void LogicalTrackMidiAndAudioUseTheWholeProjectDisplayOrder()
    {
        LogicalTrackOutputName[] selected =
        [
            new("track-2", 2, "Piano"),
            new("track-8", 8, "Strings")
        ];

        OutputFileNamePlan midi = InitialReleaseOutputNaming.PlanLogicalTrackMidi(
            selected,
            totalProjectTrackCount: 8,
            includeReadme: true);
        OutputFileNamePlan audio = InitialReleaseOutputNaming.PlanLogicalTrackAudio(
            selected,
            totalProjectTrackCount: 8);

        Assert.Equal(
            new[] { "02 - Piano.mid", "08 - Strings.mid", "README.md" },
            FileNames(midi));
        Assert.Equal(
            new[] { "02 - Piano.wav", "08 - Strings.wav" },
            FileNames(audio));
    }

    [Theory]
    [InlineData(8, 7, "07 - Track.wav")]
    [InlineData(120, 7, "007 - Track.wav")]
    [InlineData(1000, 7, "0007 - Track.wav")]
    public void LogicalTrackOrderWidthIsAtLeastTwoAndGrowsWithProjectTrackCount(
        int totalTrackCount,
        int displayOrder,
        string expected)
    {
        OutputFileNamePlan plan = InitialReleaseOutputNaming.PlanLogicalTrackAudio(
            [new("track", displayOrder, "Track")],
            totalTrackCount);

        Assert.Equal(expected, Assert.Single(plan.Targets).FileName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData("...")]
    public void MissingTrackNameUsesTheLogicalTrackFallback(string? trackName)
    {
        OutputFileNamePlan plan = InitialReleaseOutputNaming.PlanLogicalTrackAudio(
            [new("track", 4, trackName)],
            totalProjectTrackCount: 10);

        Assert.Equal("04 - Logical Track 4.wav", Assert.Single(plan.Targets).FileName);
    }

    [Fact]
    public void MidiTrackNameUsesOneBasedPortAndChannelRouting()
    {
        const string rawName = "Cafe\u0301: Lead";

        string trackName = InitialReleaseOutputNaming.GetEventTrackName(16, 10);
        OutputFileNamePlan filePlan = InitialReleaseOutputNaming.PlanLogicalTrackMidi(
            [new("track", 1, rawName)],
            totalProjectTrackCount: 1,
            includeReadme: false);

        Assert.Equal("Port 16 / Channel 10", trackName);
        Assert.Equal("01 - Café_ Lead.mid", Assert.Single(filePlan.Targets).FileName);
    }

    [Fact]
    public void MidiConductorNameUsesProjectNameWithDefensiveFallback()
    {
        Assert.Equal("Project Name", InitialReleaseOutputNaming.GetConductorTrackName("Project Name"));
        Assert.Equal("Conductor", InitialReleaseOutputNaming.GetConductorTrackName(" \t "));
        Assert.Equal("README.md", InitialReleaseOutputNaming.ReadmeFileName);
    }

    [Fact]
    public void PerPortMidiUsesOriginalOneBasedPortWithTwoDigits()
    {
        OutputFileNamePlan plan = InitialReleaseOutputNaming.PlanPortMidi(
            [16, 1, 3],
            includeReadme: true);

        Assert.Equal(
            new[] { "Port 01.mid", "Port 03.mid", "Port 16.mid", "README.md" },
            FileNames(plan));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void RejectsPortsOutsideTheInitialReleaseRange(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => InitialReleaseOutputNaming.PlanPortMidi([port], includeReadme: false));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => InitialReleaseOutputNaming.GetEventTrackName(port, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void RejectsChannelsOutsideTheInitialReleaseRange(int channel)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => InitialReleaseOutputNaming.GetEventTrackName(1, channel));
    }

    [Fact]
    public void DuplicateStableTrackKeysFailThroughTheAtomicCommonPlanner()
    {
        OutputFileNamePlan plan = InitialReleaseOutputNaming.PlanLogicalTrackMidi(
            [new("same", 1, "One"), new("same", 2, "Two")],
            totalProjectTrackCount: 2,
            includeReadme: false);

        Assert.False(plan.Succeeded);
        Assert.Empty(plan.Targets);
        Assert.Contains(
            plan.Diagnostics,
            value => value.Code == "MIDORA-OUTPUT-NAME-INVALID-REQUEST");
    }

    [Fact]
    public void RejectsASelectedTrackOrderOutsideTheWholeProject()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => InitialReleaseOutputNaming.PlanLogicalTrackAudio(
                [new("track", 3, "Track")],
                totalProjectTrackCount: 2));
    }

    private static string[] FileNames(OutputFileNamePlan plan)
    {
        Assert.True(plan.Succeeded);
        return plan.Targets.Select(value => value.FileName).ToArray();
    }
}
