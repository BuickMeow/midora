using Midora.Domain;
using Midora.MidiExport;
using Xunit;

namespace Midora.Desktop.Tests;

public sealed class MidiExportServicesTests
{
    [Fact]
    public async Task PreparesFrozenPlanAndPublishesWholeProjectWithReadme()
    {
        using TemporaryDirectory temporary = new();
        MidoraProject project = CreateProject();
        PreparedDesktopMidiExport prepared = DesktopMidiExportService.Prepare(
            project,
            currentProjectPath: null,
            createdWithSoftwareVersion: null,
            lastSavedWithSoftwareVersion: null,
            new(
                MidiExportMode.WholeProject,
                MidiExportRoutingStrategy.Preserve,
                temporary.Path,
                0,
                null,
                IncludeReadme: true,
                TreatWarningsAsErrors: false));

        Assert.True(prepared.Succeeded);
        Assert.Equal(["Test Project.mid", "README.md"],
            prepared.OutputPlan.Targets.Select(item => item.FileName).ToArray());
        Assert.NotNull(prepared.Readme);
        Assert.Equal(
            MidoraSoftwareVersion.InformationalVersion,
            prepared.Readme.CreatedWithSoftwareVersion);
        Assert.Equal(
            MidoraSoftwareVersion.InformationalVersion,
            prepared.Readme.LastSavedWithSoftwareVersion);
        Assert.Equal(
            MidoraSoftwareVersion.InformationalVersion,
            prepared.Readme.ExportSoftwareVersion);

        MidiExportTaskResult result = await new MidiExportTaskRunner().ExecuteAsync(new()
        {
            Compilation = prepared.Compilation,
            OutputPlan = prepared.OutputPlan,
            Readme = prepared.Readme,
            OverwriteAuthorized = false
        });

        Assert.Equal(MidiExportTaskStatus.Succeeded, result.Status);
        Assert.True(File.Exists(System.IO.Path.Combine(temporary.Path, "Test Project.mid")));
        Assert.Contains("# Midora MIDI Export",
            await File.ReadAllTextAsync(System.IO.Path.Combine(temporary.Path, "README.md")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingTargetIsFrozenAsRequiringExplicitAuthorization()
    {
        using TemporaryDirectory temporary = new();
        File.WriteAllBytes(System.IO.Path.Combine(temporary.Path, "Test Project.mid"), [1]);

        PreparedDesktopMidiExport prepared = DesktopMidiExportService.Prepare(
            CreateProject(),
            null,
            null,
            null,
            new(
                MidiExportMode.WholeProject,
                MidiExportRoutingStrategy.Preserve,
                temporary.Path,
                0,
                null,
                IncludeReadme: false,
                TreatWarningsAsErrors: false));

        Assert.True(prepared.OutputPlan.RequiresOverwriteAuthorization);
        Assert.True(prepared.OutputPlan.Targets.Single().ExistedAtFreeze);
    }

    private static MidoraProject CreateProject()
    {
        MidoraProject project = new(192);
        project.Metadata.ProjectName = "Test Project";
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
        LogicalTrack track = new(project) { Name = "Track"};
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 192 };
        segment.Notes.Add(new(project) { LengthTicks = 192, Note = 60, Velocity = 100 });
        track.Segments.Add(segment);
        return project;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"midora-desktop-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
