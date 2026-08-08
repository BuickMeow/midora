using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;
using Midora.OutputPlanning;

namespace Midora.MidiExport.Tests;

public sealed class MidiExportTaskRunnerTests
{
    [Theory]
    [InlineData((int)MidiExportMode.WholeProject, "Project.mid")]
    [InlineData((int)MidiExportMode.PerLogicalTrack, "01 - Track.mid")]
    [InlineData((int)MidiExportMode.PerPort, "Port 01.mid")]
    public async Task RunsFrozenCompilationEncodingAndPublicationForEveryMode(
        int modeValue,
        string expectedFileName)
    {
        using TemporaryDirectory temporary = new();
        MidiExportMode mode = (MidiExportMode)modeValue;
        (MidoraProject project, LogicalTrack track) = CreateProject();
        using MidoraCompiler compiler = new();
        MidiExportCompilationResult compilation = new MidiExportCompilationCoordinator(compiler).Compile(new()
        {
            Project = project,
            Mode = mode,
            Routing = MidiExportRoutingStrategy.Compact,
            EndTick = 192
        });
        string outputDirectory = temporary.PathFor(mode.ToString());
        MidiExportFrozenOutputPlan plan = mode switch
        {
            MidiExportMode.WholeProject => MidiExportOutputPlanner.PlanWholeProject(
                outputDirectory,
                "Project",
                null,
                includeReadme: false),
            MidiExportMode.PerLogicalTrack => MidiExportOutputPlanner.PlanLogicalTracks(
                outputDirectory,
                [new LogicalTrackOutputName(track.Id.ToString(), 1, track.Name)],
                1,
                includeReadme: false),
            MidiExportMode.PerPort => MidiExportOutputPlanner.PlanPorts(
                outputDirectory,
                [1],
                includeReadme: false),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

        MidiExportTaskResult result = await new MidiExportTaskRunner().ExecuteAsync(new()
        {
            Compilation = compilation,
            OutputPlan = plan,
            OverwriteAuthorized = false
        });

        Assert.Equal(MidiExportTaskStatus.Succeeded, result.Status);
        Assert.NotNull(result.Output);
        Assert.True(result.Output.Succeeded);
        Assert.True(File.Exists(Path.Combine(outputDirectory, expectedFileName)));
    }

    [Fact]
    public async Task ReportsCancelledWithoutPublishingOrTreatingItAsAnError()
    {
        using TemporaryDirectory temporary = new();
        (MidoraProject project, _) = CreateProject();
        using MidoraCompiler compiler = new();
        MidiExportCompilationResult compilation = new MidiExportCompilationCoordinator(compiler).Compile(new()
        {
            Project = project,
            Mode = MidiExportMode.WholeProject,
            Routing = MidiExportRoutingStrategy.Preserve,
            EndTick = 192
        });
        string outputDirectory = temporary.PathFor("cancelled");
        MidiExportFrozenOutputPlan plan = MidiExportOutputPlanner.PlanWholeProject(
            outputDirectory,
            "Project",
            null,
            includeReadme: false);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        MidiExportTaskResult result = await new MidiExportTaskRunner().ExecuteAsync(
            new()
            {
                Compilation = compilation,
                OutputPlan = plan
            },
            cancellation.Token);

        Assert.Equal(MidiExportTaskStatus.Cancelled, result.Status);
        Assert.Null(result.OutputFailure);
        Assert.False(Directory.Exists(outputDirectory));
    }

    [Fact]
    public async Task SmfDeltaOverflowFailsBeforePublishingAnyOutput()
    {
        using TemporaryDirectory temporary = new();
        MidoraProject project = new(192);
        using MidoraCompiler compiler = new();
        MidiExportCompilationResult compilation = new MidiExportCompilationCoordinator(compiler).Compile(new()
        {
            Project = project,
            Mode = MidiExportMode.WholeProject,
            Routing = MidiExportRoutingStrategy.Preserve,
            EndTick = (long)StandardMidiFile.MaximumVariableLengthValue + 1
        });
        string outputDirectory = temporary.PathFor("vlq-overflow");
        MidiExportFrozenOutputPlan plan = MidiExportOutputPlanner.PlanWholeProject(
            outputDirectory,
            "Project",
            null,
            includeReadme: false);

        MidiExportTaskResult result = await new MidiExportTaskRunner().ExecuteAsync(new()
        {
            Compilation = compilation,
            OutputPlan = plan
        });

        Assert.True(compilation.Succeeded);
        Assert.Equal(MidiExportTaskStatus.Failed, result.Status);
        Assert.Null(result.Output);
        Assert.Null(result.OutputFailure);
        MidiExportArtifactDiagnostic diagnostic = Assert.Single(result.ArtifactDiagnostics);
        Assert.Equal("MIDORA-MIDI-EXPORT-ENCODING", diagnostic.Diagnostic.Code);
        Assert.Contains("delta time", diagnostic.Diagnostic.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(outputDirectory));
    }

    [Fact]
    public async Task PublishesGeneratedReadmeAsPartOfTheSameWholeProjectTask()
    {
        using TemporaryDirectory temporary = new();
        (MidoraProject project, _) = CreateProject();
        using MidoraCompiler compiler = new();
        MidiExportCompilationResult compilation = new MidiExportCompilationCoordinator(compiler).Compile(new()
        {
            Project = project,
            Mode = MidiExportMode.WholeProject,
            Routing = MidiExportRoutingStrategy.Compact,
            EndTick = 192
        });
        string outputDirectory = temporary.PathFor("with-readme");
        MidiExportFrozenOutputPlan plan = MidiExportOutputPlanner.PlanWholeProject(
            outputDirectory,
            "Project",
            null,
            includeReadme: true);
        MidiExportReadmeRequest readme = MidiExportReadmeFactory.Create(
            project,
            compilation,
            plan,
            MidiExportRangeSource.NaturalContentEnd,
            "0.1.0",
            "0.1.0",
            "0.1.0",
            new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero));

        MidiExportTaskResult result = await new MidiExportTaskRunner().ExecuteAsync(new()
        {
            Compilation = compilation,
            OutputPlan = plan,
            Readme = readme
        });

        Assert.Equal(MidiExportTaskStatus.Succeeded, result.Status);
        Assert.True(File.Exists(Path.Combine(outputDirectory, "Project.mid")));
        Assert.Contains(
            "# Midora MIDI Export",
            await File.ReadAllTextAsync(Path.Combine(outputDirectory, "README.md")),
            StringComparison.Ordinal);
    }

    private static (MidoraProject Project, LogicalTrack Track) CreateProject()
    {
        MidoraProject project = new(192);
        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            RootNote = 60,
            TemplateLengthTicks = 192,
            OverlapPolicy = OverlapPolicy.Warn
        };
        SubVoice voice = new(project) { Name = "Voice" };
        voice.Events.Add(TemplateEvent.Note(project, 0, 96, 60, 100));
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project) { Name = "Track", EventInstrumentId = instrument.Id };
        Segment segment = new(project) { LengthTicks = 192 };
        segment.Notes.Add(new(project)
        {
            LengthTicks = 192,
            Note = 60,
            Velocity = 100
        });
        track.Segments.Add(segment);
        project.Tracks.Add(track);
        return (project, track);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"midora-midi-task-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string PathFor(string value) => System.IO.Path.Combine(Path, value);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
