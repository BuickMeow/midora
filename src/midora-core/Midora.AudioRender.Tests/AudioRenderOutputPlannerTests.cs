using Midora.Compiler;
using Midora.Domain;

namespace Midora.AudioRender.Tests;

public sealed class AudioRenderOutputPlannerTests
{
    [Fact]
    public void WholeMixAppendsWaveExtensionAndLegalizesTheFrozenFileName()
    {
        string directory = AudioRenderTestProject.CreateOwnedDirectory();
        try
        {
            AudioRenderFrozenOutputPlan plan = AudioRenderOutputPlanner.PlanWholeMix(
                Path.Combine(directory, "CON"));

            AudioRenderPlannedTarget target = Assert.Single(plan.Targets);
            Assert.True(plan.Succeeded);
            Assert.Equal("_CON.wav", target.FileName);
            Assert.Equal(Path.Combine(directory, "_CON.wav"), target.FullPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WholeMixRejectsMisleadingExtensionAndForbiddenProjectPath()
    {
        string directory = AudioRenderTestProject.CreateOwnedDirectory();
        try
        {
            string projectPath = Path.Combine(directory, "Project.midora");
            AudioRenderFrozenOutputPlan badExtension = AudioRenderOutputPlanner.PlanWholeMix(
                Path.Combine(directory, "audio.flac"));
            AudioRenderFrozenOutputPlan forbidden = AudioRenderOutputPlanner.PlanWholeMix(
                projectPath + ".wav",
                [projectPath + ".wav"]);

            Assert.False(badExtension.Succeeded);
            Assert.False(forbidden.Succeeded);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PerTrackUsesWholeProjectOrderAndStableCollisionSuffixes()
    {
        MidoraProject project = AudioRenderTestProject.Create(
            ("Piano", 192, 60),
            ("Skipped", 192, 62),
            ("Piano", 192, 64));
        using MidoraCompiler compiler = new();
        AudioRenderCompilationResult compilation = new AudioRenderCompilationCoordinator(compiler).Compile(new()
        {
            Project = project,
            Mode = AudioRenderMode.PerLogicalTrack,
            EndTick = 192,
            SelectedTrackIds = new HashSet<MidoraId>
            {
                project.Tracks[0].Id,
                project.Tracks[2].Id
            }
        });
        string directory = Path.Combine(Path.GetTempPath(), $"midora-missing-output-{Guid.NewGuid():N}");

        AudioRenderFrozenOutputPlan plan = AudioRenderOutputPlanner.PlanLogicalTracks(
            directory,
            compilation.Tracks,
            project.Tracks.Count);

        Assert.True(plan.Succeeded);
        Assert.False(plan.OutputDirectoryExistedAtFreeze);
        Assert.Collection(
            plan.Targets,
            first => Assert.Equal("01 - Piano.wav", first.FileName),
            third => Assert.Equal("03 - Piano.wav", third.FileName));
    }

    [Fact]
    public void ExistingTargetsAreFrozenSeparatelyFromOverwriteAuthorization()
    {
        MidoraProject project = AudioRenderTestProject.Create(("Track", 192, 60));
        using MidoraCompiler compiler = new();
        AudioRenderCompilationResult compilation = new AudioRenderCompilationCoordinator(compiler).Compile(new()
        {
            Project = project,
            Mode = AudioRenderMode.PerLogicalTrack,
            EndTick = 192
        });
        string directory = AudioRenderTestProject.CreateOwnedDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "01 - Track.wav"), "existing");

            AudioRenderFrozenOutputPlan plan = AudioRenderOutputPlanner.PlanLogicalTracks(
                directory,
                compilation.Tracks,
                project.Tracks.Count);

            Assert.True(plan.RequiresOverwriteAuthorization);
            Assert.True(Assert.Single(plan.Targets).ExistedAtFreeze);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
