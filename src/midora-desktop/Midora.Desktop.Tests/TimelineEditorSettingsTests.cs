using Midora.Domain;
using Midora.Desktop.Presentation.Controls;
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
    public void SubdivisionSelectionUpdatesCompactBindableText()
    {
        TimelineEditorSettings settings = new();
        List<string?> notifications = [];
        settings.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        settings.DisplaySubdivision = TimelineSubdivision.Presets.Single(item =>
            !item.IsBar && item.Numerator == 1 && item.Denominator == 8);
        settings.OperationSubdivision = TimelineSubdivision.Presets.Single(item =>
            !item.IsBar && item.Numerator == 1 && item.Denominator == 24);

        Assert.Equal("1/8", settings.DisplaySubdivisionText);
        Assert.Equal("1/24", settings.OperationSubdivisionText);
        Assert.Contains(nameof(TimelineEditorSettings.DisplaySubdivisionText), notifications);
        Assert.Contains(nameof(TimelineEditorSettings.OperationSubdivisionText), notifications);
    }

    [Fact]
    public void ArrangementSegmentAndSubVoiceSettingsUseTheirRequiredScopes()
    {
        TimelineEditorSettings arrangement = new();
        TimelineEditorSettings piano = new();
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument" };
        SubVoice firstVoice = new(project) { Name = "First" };
        SubVoice secondVoice = new(project) { Name = "Second" };
        instrument.SubVoices.AddRange([firstVoice, secondVoice]);
        project.EventInstruments.Add(instrument);
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
            instrument.Id,
            "Instrument");
        instrumentWorkspace.Rebuild(project, revision: 1);

        piano.OperationSubdivisionText = "1/24";
        piano.GridVisible = false;

        Assert.Equal(80, segmentWorkspace.OperationStepTicks);
        Assert.Same(piano, segmentWorkspace.EditorSettings);
        Assert.NotSame(piano, instrumentWorkspace.EditorSettings);
        Assert.NotSame(instrumentWorkspace.EditorSettings, instrumentWorkspace.EventLaneEditorSettings);
        Assert.False(segmentWorkspace.GridVisible);
        Assert.NotEqual(segmentWorkspace.OperationStepTicks, arrangementWorkspace.OperationStepTicks);
        Assert.True(arrangementWorkspace.GridVisible);
        Assert.True(arrangement.DisplayGridUsesBars);
        Assert.Equal("Bar", arrangement.DisplaySubdivisionText);
        Assert.Equal("1/8", arrangement.OperationSubdivisionText);
        Assert.Equal(240, arrangement.OperationStepTicks);
        Assert.False(piano.DisplayGridUsesBars);
        Assert.Equal("1/4", piano.DisplaySubdivisionText);
        Assert.Equal(480, arrangement.DefaultLengthTicks);
        Assert.Equal(480, piano.DefaultLengthTicks);

        TimelineEditorSettings firstPiano = instrumentWorkspace.EditorSettings;
        TimelineEditorSettings firstEventLane = instrumentWorkspace.EventLaneEditorSettings;
        firstPiano.DisplaySubdivisionText = "1/8";
        firstPiano.OperationSubdivisionText = "1/12";
        firstEventLane.OperationSubdivisionText = "1/32";
        instrumentWorkspace.Selection.Replace(secondVoice.Id);
        instrumentWorkspace.Rebuild(project, revision: 2);

        Assert.NotSame(firstPiano, instrumentWorkspace.EditorSettings);
        Assert.NotSame(firstEventLane, instrumentWorkspace.EventLaneEditorSettings);
        Assert.Equal("1/4", instrumentWorkspace.EditorSettings.DisplaySubdivisionText);
        Assert.Equal("1/16", instrumentWorkspace.EditorSettings.OperationSubdivisionText);
        Assert.Equal("1/16", instrumentWorkspace.EventLaneEditorSettings.OperationSubdivisionText);

        instrumentWorkspace.Selection.Replace(firstVoice.Id);
        instrumentWorkspace.Rebuild(project, revision: 3);
        Assert.Same(firstPiano, instrumentWorkspace.EditorSettings);
        Assert.Same(firstEventLane, instrumentWorkspace.EventLaneEditorSettings);
        Assert.Equal("1/8", instrumentWorkspace.EditorSettings.DisplaySubdivisionText);
        Assert.Equal("1/12", instrumentWorkspace.EditorSettings.OperationSubdivisionText);
        Assert.Equal("1/32", instrumentWorkspace.EventLaneEditorSettings.OperationSubdivisionText);
    }

    [Fact]
    public void SegmentLaneSnapIsIndependentFromItsPianoRollSnap()
    {
        TimelineWorkspaceViewModel segment = new(
            WorkspaceKey.ForObject(WorkspaceKind.SegmentEditor, new MidoraId(1)),
            "Segment",
            TimelineWorkspaceMode.Segment);

        segment.EditorSettings.OperationSubdivisionText = "1/8";
        segment.LaneEditorSettings.OperationSubdivisionText = "1/32";
        segment.LaneEditorSettings.SnapEnabled = false;

        Assert.Equal("1/8", segment.EditorSettings.OperationSubdivisionText);
        Assert.True(segment.EditorSettings.SnapEnabled);
        Assert.Equal("1/32", segment.LaneEditorSettings.OperationSubdivisionText);
        Assert.Equal(1, segment.LaneEditorSettings.EffectiveOperationStepTicks);
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

    [Fact]
    public void TimelineToolModeAlwaysExposesExactlyOneActiveTool()
    {
        TimelineWorkspaceViewModel workspace = new(
            WorkspaceKey.ForType(WorkspaceKind.Arrangement),
            "Arrangement",
            TimelineWorkspaceMode.Arrangement);

        workspace.ToolMode = TimelineToolMode.Draw;
        Assert.True(workspace.IsDrawTool);
        Assert.False(workspace.IsSelectTool);
        Assert.False(workspace.IsSplitTool);
        Assert.False(workspace.IsEraseTool);

        workspace.ToolMode = TimelineToolMode.Erase;
        Assert.True(workspace.IsEraseTool);
        Assert.False(workspace.IsDrawTool);
        Assert.False(workspace.IsSelectTool);
        Assert.False(workspace.IsSplitTool);
    }
}
