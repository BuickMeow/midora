using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectLogicalParameterDefinitionMigrationCommandsTests
{
    [Fact]
    public void DoubleToIntegerClampMigratesEveryReferencingLaneAtomically()
    {
        using Fixture fixture = CreateDoubleFixture();
        CurvePoint[][] oldPoints = fixture.Lanes
            .Select(lane => lane.Points.ToArray())
            .ToArray();

        fixture.Document.Execute(ProjectDomainEditCommands.MigrateLogicalParameterDefinition(
            fixture.Instrument.Id,
            fixture.Parameter.Id,
            new LogicalParameterDefinitionEdit(
                LogicalParameterType.Integer,
                0,
                5,
                0,
                5,
                0,
                UsesExplicitEnumValues: false,
                EnumItems: []),
            LogicalParameterLaneRebindMode.Clamp,
            enumSemanticWarningAcknowledged: false));

        Assert.Equal(LogicalParameterType.Integer, fixture.Parameter.Type);
        foreach (LogicalParameterLane lane in fixture.Lanes)
        {
            Assert.Equal([0d, 1d, 5d], lane.Points.Select(value => value.Value));
            Assert.All(lane.Points, point => Assert.Equal(CurveInterpolation.Linear, point.Interpolation));
        }
        for (int laneIndex = 0; laneIndex < fixture.Lanes.Length; laneIndex++)
        {
            Assert.Equal(
                oldPoints[laneIndex].Select(value => value.Id),
                fixture.Lanes[laneIndex].Points.Select(value => value.Id));
        }
        AssertMatchesFull(fixture.Compilation);

        CurvePoint[][] replacements = fixture.Lanes
            .Select(lane => lane.Points.ToArray())
            .ToArray();
        fixture.Document.Undo();

        Assert.Equal(LogicalParameterType.Double, fixture.Parameter.Type);
        for (int laneIndex = 0; laneIndex < fixture.Lanes.Length; laneIndex++)
        {
            Assert.Equal([-3.5d, 0.5d, 4.9d], fixture.Lanes[laneIndex].Points.Select(value => value.Value));
            Assert.True(fixture.Lanes[laneIndex].Points.SequenceEqual(oldPoints[laneIndex]));
        }
        AssertMatchesFull(fixture.Compilation);

        fixture.Document.Redo();
        for (int laneIndex = 0; laneIndex < fixture.Lanes.Length; laneIndex++)
        {
            Assert.True(fixture.Lanes[laneIndex].Points.SequenceEqual(replacements[laneIndex]));
        }
        AssertMatchesFull(fixture.Compilation);
    }

    [Fact]
    public void EnumReorderDeleteAndNewItemPreserveExistingIdsAndRedoNewId()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Mode",
            Type = LogicalParameterType.Enum,
            Minimum = 0,
            Maximum = 2,
            DisplayMinimum = 0,
            DisplayMaximum = 2,
            DefaultValue = 1
        };
        LogicalParameterEnumItem a = new(project) { Name = "A", Value = 0 };
        LogicalParameterEnumItem b = new(project) { Name = "B", Value = 1 };
        LogicalParameterEnumItem c = new(project) { Name = "C", Value = 2 };
        parameter.EnumItems.AddRange([a, b, c]);
        instrument.LogicalParameters.Add(parameter);
        LogicalTrack track = CreateTrackWithLane(project, instrument, parameter, [0, 1, 2]);
        LogicalParameterLane lane = track.Segments[0].ParameterLanes[0];
        CurvePoint[] oldPoints = lane.Points.ToArray();
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        long before = project.NextStableId;

        document.Execute(ProjectDomainEditCommands.MigrateLogicalParameterDefinition(
            instrument.Id,
            parameter.Id,
            new LogicalParameterDefinitionEdit(
                LogicalParameterType.Enum,
                0,
                20,
                0,
                20,
                10,
                UsesExplicitEnumValues: true,
                EnumItems:
                [
                    new(c.Id, "C", 10),
                    new(a.Id, "A", 0),
                    new(null, "D", 20)
                ]),
            LogicalParameterLaneRebindMode.Clamp,
            enumSemanticWarningAcknowledged: true));

        Assert.Equal([c.Id, a.Id], parameter.EnumItems.Take(2).Select(value => value.Id));
        LogicalParameterEnumItem added = parameter.EnumItems[2];
        Assert.True(added.Id.Value >= before);
        Assert.Equal([10, 0, 20], parameter.EnumItems.Select(value => value.Value));
        Assert.Equal([0d, 0d, 0d], lane.Points.Select(value => value.Value));
        Assert.All(lane.Points, point => Assert.Equal(CurveInterpolation.Step, point.Interpolation));
        long highWater = project.NextStableId;
        AssertMatchesFull(compilation);

        document.Undo();
        Assert.Equal([a, b, c], parameter.EnumItems);
        Assert.True(lane.Points.SequenceEqual(oldPoints));
        Assert.Equal(highWater, project.NextStableId);
        AssertMatchesFull(compilation);

        document.Redo();
        Assert.Same(added, parameter.EnumItems[2]);
        Assert.Equal(highWater, project.NextStableId);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void DiscardMigrationDropsOnlyInvalidPointsAndKeepsPointIdsForSurvivors()
    {
        using Fixture fixture = CreateDoubleFixture();
        CurvePoint[] firstOld = fixture.Lanes[0].Points.ToArray();

        fixture.Document.Execute(ProjectDomainEditCommands.MigrateLogicalParameterDefinition(
            fixture.Instrument.Id,
            fixture.Parameter.Id,
            new LogicalParameterDefinitionEdit(
                LogicalParameterType.Integer,
                -4,
                1,
                -4,
                1,
                0,
                UsesExplicitEnumValues: false,
                EnumItems: []),
            LogicalParameterLaneRebindMode.DiscardInvalidValues,
            enumSemanticWarningAcknowledged: false));

        Assert.Equal([-4d, 1d], fixture.Lanes[0].Points.Select(value => value.Value));
        Assert.Equal(
            [firstOld[0].Id, firstOld[1].Id],
            fixture.Lanes[0].Points.Select(value => value.Id));
        AssertMatchesFull(fixture.Compilation);
    }

    [Fact]
    public void EnumMigrationRequiresAcknowledgementAndRejectsInvalidPlanBeforeAllocation()
    {
        using Fixture fixture = CreateDoubleFixture();
        long highWater = fixture.Project.NextStableId;
        LogicalParameterDefinitionEdit edit = new(
            LogicalParameterType.Enum,
            0,
            1,
            0,
            1,
            0,
            UsesExplicitEnumValues: false,
            EnumItems: [new(null, "Off"), new(null, "On")]);

        Assert.Throws<InvalidOperationException>(() => fixture.Document.Execute(
            ProjectDomainEditCommands.MigrateLogicalParameterDefinition(
                fixture.Instrument.Id,
                fixture.Parameter.Id,
                edit,
                LogicalParameterLaneRebindMode.Clamp,
                enumSemanticWarningAcknowledged: false)));
        Assert.Equal(highWater, fixture.Project.NextStableId);
        Assert.False(fixture.Document.CanUndo);

        LogicalParameterEnumItemDefinitionEdit invalid = new(
            new MidoraId(999_999),
            "Missing");
        Assert.Throws<ArgumentException>(() => fixture.Document.Execute(
            ProjectDomainEditCommands.MigrateLogicalParameterDefinition(
                fixture.Instrument.Id,
                fixture.Parameter.Id,
                edit with { EnumItems = [invalid] },
                LogicalParameterLaneRebindMode.Clamp,
                enumSemanticWarningAcknowledged: true)));
        Assert.Equal(highWater, fixture.Project.NextStableId);
        Assert.False(fixture.Document.CanUndo);
    }

    private static Fixture CreateDoubleFixture()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Amount",
            Type = LogicalParameterType.Double,
            Minimum = -10,
            Maximum = 10,
            DisplayMinimum = -10,
            DisplayMaximum = 10,
            DefaultValue = 0
        };
        instrument.LogicalParameters.Add(parameter);
        LogicalTrack first = CreateTrackWithLane(project, instrument, parameter, [-3.5, 0.5, 4.9]);
        LogicalTrack second = CreateTrackWithLane(project, instrument, parameter, [-3.5, 0.5, 4.9]);
        ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        return new(
            project,
            instrument,
            parameter,
            [
                first.Segments[0].ParameterLanes[0],
                second.Segments[0].ParameterLanes[0]
            ],
            compilation,
            document);
    }

    private static LogicalTrack CreateTrackWithLane(
        MidoraProject project,
        EventInstrument instrument,
        LogicalParameterDefinition parameter,
        IReadOnlyList<double> values)
    {
        LogicalTrack track = new(project) {
            Name = $"Track {project.Tracks.Count + 1}",
            LastBoundEventInstrumentName = instrument.Name
        };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { ProjectStartTick = 0, LengthTicks = 480 };
        LogicalParameterLane lane = new(project) { ParameterId = parameter.Id };
        for (int index = 0; index < values.Count; index++)
        {
            lane.Points.Add(new CurvePoint(
                project,
                index * 120,
                values[index],
                parameter.Type == LogicalParameterType.Enum
                    ? CurveInterpolation.Step
                    : CurveInterpolation.Linear));
        }
        segment.ParameterLanes.Add(lane);
        track.Segments.Add(segment);
        return track;
    }

    private static void AssertMatchesFull(ProjectCompilationSession compilation)
    {
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult expected = compiler.CompileFull(compilation.Project);
        Assert.Equal(expected.Fingerprint, compilation.LastAttempt.Fingerprint);
        Assert.Equal(expected.IsConsumable, compilation.LastAttempt.IsConsumable);
        Assert.Equal(expected.Events, compilation.LastAttempt.Events);
        Assert.Equal(expected.Diagnostics, compilation.LastAttempt.Diagnostics);
    }

    private sealed record Fixture(
        MidoraProject Project,
        EventInstrument Instrument,
        LogicalParameterDefinition Parameter,
        LogicalParameterLane[] Lanes,
        ProjectCompilationSession Compilation,
        ProjectDocumentSession Document) : IDisposable
    {
        public void Dispose() => Compilation.Dispose();
    }
}
