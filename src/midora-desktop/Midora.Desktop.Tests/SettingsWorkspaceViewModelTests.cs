using Midora.Compiler;
using Midora.Domain;
using System.Globalization;
using Xunit;

namespace Midora.Desktop.Tests;

public sealed class SettingsWorkspaceViewModelTests
{
    [Fact]
    public void RuntimeProjectStatisticsAreReadOnlyAndRefreshInPlace()
    {
        MidoraProject project = new(192);
        SettingsWorkspaceViewModel workspace = new();
        workspace.Rebuild(project, revision: 1);

        workspace.UpdateRuntimeInformation(
            new CompilationStatistics(0, 0, 67_890, 0)
            {
                NoteOnEventCount = 12_345
            },
            totalEditingTimeMilliseconds: ((2L * 86_400 + 3_661) * 1_000));

        InspectorField notes = Assert.Single(
            workspace.GeneralFields,
            value => value.Key == "settings.project.noteCount");
        InspectorField events = Assert.Single(
            workspace.GeneralFields,
            value => value.Key == "settings.project.eventCount");
        InspectorField workTime = Assert.Single(
            workspace.GeneralFields,
            value => value.Key == "settings.project.totalWorkTime");

        Assert.False(notes.IsEditable);
        Assert.False(events.IsEditable);
        Assert.False(workTime.IsEditable);
        Assert.Equal(
            12_345,
            long.Parse(notes.Value, NumberStyles.Number, CultureInfo.CurrentCulture));
        Assert.Equal(
            67_890,
            long.Parse(events.Value, NumberStyles.Number, CultureInfo.CurrentCulture));
        Assert.Equal("2d 01:01:01", workTime.Value);
    }
}
