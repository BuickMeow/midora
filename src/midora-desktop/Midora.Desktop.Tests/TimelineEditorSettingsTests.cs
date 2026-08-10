using Midora.Domain;
using Midora.Desktop.Presentation.Interaction;
using Xunit;

namespace Midora.Desktop.Tests;

public sealed class TimelineEditorSettingsTests
{
    [Fact]
    public void SubdivisionUsesWholeNoteFractionsAndCeiling()
    {
        Assert.Equal(480, new TimelineSubdivision(1, 4, "1/4").ToTicks(480));
        Assert.Equal(640, new TimelineSubdivision(1, 3, "1/3").ToTicks(480));
        Assert.Equal(23, new TimelineSubdivision(3, 256, "3/256").ToTicks(480));
        Assert.Equal(1, new TimelineSubdivision(1, 256, "1/256").ToTicks(1));
    }

    [Theory]
    [InlineData("1/16", 1, 16, false)]
    [InlineData("1/16 · Sixteenth", 1, 16, false)]
    [InlineData("256", 1, 256, false)]
    [InlineData("Bar", 1, 1, true)]
    public void SubdivisionParserAcceptsEditableValuesAndPresetLabels(
        string text,
        int numerator,
        int denominator,
        bool isBar)
    {
        Assert.True(TimelineSubdivision.TryParse(text, out TimelineSubdivision result));
        Assert.Equal((numerator, denominator, isBar), (result.Numerator, result.Denominator, result.IsBar));
    }

    [Fact]
    public void SubdivisionSelectionUsesCompactText()
    {
        TimelineSubdivision eighth = TimelineSubdivision.Presets.Single(item =>
            !item.IsBar && item.Numerator == 1 && item.Denominator == 8);

        Assert.Equal("1/8", eighth.ShortLabel);
        Assert.Equal("1/8", eighth.ToString());
        Assert.Equal("Bar", TimelineSubdivision.Presets[0].ToString());
    }

    [Fact]
    public void ArrangementAndPianoSettingsRemainIndependentWhileSegmentAndSubVoiceShare()
    {
        TimelineEditorSettings arrangement = new();
        TimelineEditorSettings piano = new();
        MidoraProject project = new(480);
        arrangement.Reset(arrangement: true, project.TicksPerQuarterNote);
        piano.Reset(arrangement: false, project.TicksPerQuarterNote);
        TimelineWorkspaceViewModel arrangementWorkspace = new(
            WorkspaceKey.ForType(WorkspaceKind.Arrangement),
            "Arrangement",
            TimelineWorkspaceMode.Arrangement,
            arrangement);
        TimelineWorkspaceViewModel segmentWorkspace = new(
            WorkspaceKey.ForObject(WorkspaceKind.SegmentEditor, project.AllocateStableId()),
            "Segment",
            TimelineWorkspaceMode.Segment,
            piano);
        InstrumentWorkspaceViewModel instrumentWorkspace = new(
            project.AllocateStableId(),
            "Instrument",
            piano);

        piano.OperationSubdivisionText = "1/24";
        piano.GridVisible = false;

        Assert.Equal(80, segmentWorkspace.OperationStepTicks);
        Assert.Same(piano, segmentWorkspace.EditorSettings);
        Assert.Same(piano, instrumentWorkspace.EditorSettings);
        Assert.False(segmentWorkspace.GridVisible);
        Assert.NotEqual(segmentWorkspace.OperationStepTicks, arrangementWorkspace.OperationStepTicks);
        Assert.True(arrangementWorkspace.GridVisible);
        Assert.Equal(1_920, arrangement.DefaultLengthTicks);
        Assert.Equal(480, piano.DefaultLengthTicks);
    }

    [Fact]
    public void SnapDisabledKeepsDisplayGridAndUsesOneTickOperations()
    {
        TimelineEditorSettings settings = new();
        settings.Reset(arrangement: false, ticksPerQuarterNote: 480);
        settings.DisplaySubdivisionText = "1/8";
        settings.OperationSubdivisionText = "1/24";
        settings.SnapEnabled = false;

        Assert.Equal(240, settings.DisplayGridStepTicks);
        Assert.Equal(80, settings.OperationStepTicks);
        Assert.Equal(1, settings.EffectiveOperationStepTicks);
    }

    [Fact]
    public void SegmentLowerEditorVisibilityAndHeightRemainWorkspaceLocal()
    {
        TimelineWorkspaceViewModel segment = new(
            WorkspaceKey.ForObject(WorkspaceKind.SegmentEditor, new MidoraId(1)),
            "Segment",
            TimelineWorkspaceMode.Segment);
        TimelineWorkspaceViewModel arrangement = new(
            WorkspaceKey.ForType(WorkspaceKind.Arrangement),
            "Arrangement",
            TimelineWorkspaceMode.Arrangement);

        segment.BottomEditorRowHeight = new System.Windows.GridLength(260);
        segment.IsLowerEditorVisible = false;

        Assert.Equal(0, segment.BottomEditorRowHeight.Value);
        segment.IsLowerEditorVisible = true;
        Assert.Equal(260, segment.BottomEditorRowHeight.Value);
        Assert.Equal(0, arrangement.BottomEditorRowHeight.Value);
    }
}
