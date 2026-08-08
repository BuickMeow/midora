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

    [Fact]
    public void WholeMixRejectsInvalidPathDirectoryTargetAndInvalidForbiddenPath()
    {
        string directory = AudioRenderTestProject.CreateOwnedDirectory();
        try
        {
            string directoryTarget = Path.Combine(directory, "mix.wav");
            Directory.CreateDirectory(directoryTarget);

            AudioRenderFrozenOutputPlan invalidPath = AudioRenderOutputPlanner.PlanWholeMix("\0");
            AudioRenderFrozenOutputPlan targetIsDirectory =
                AudioRenderOutputPlanner.PlanWholeMix(directoryTarget);
            AudioRenderFrozenOutputPlan invalidForbidden = AudioRenderOutputPlanner.PlanWholeMix(
                Path.Combine(directory, "valid.wav"),
                ["\0"]);

            Assert.False(invalidPath.Succeeded);
            Assert.Empty(invalidPath.Targets);
            Assert.False(targetIsDirectory.Succeeded);
            Assert.Contains(targetIsDirectory.Diagnostics,
                value => value.Message.Contains("existing directory", StringComparison.Ordinal));
            Assert.False(invalidForbidden.Succeeded);
            Assert.Contains(invalidForbidden.Diagnostics,
                value => value.Message.Contains("forbidden output target path is invalid", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PerTrackRejectsInvalidDirectoryTopologyAndNoParticipatingTargets()
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
            string fileInsteadOfDirectory = Path.Combine(directory, "not-a-directory");
            File.WriteAllBytes(fileInsteadOfDirectory, [1]);
            string directoryTargetRoot = Path.Combine(directory, "target-is-directory");
            Directory.CreateDirectory(Path.Combine(directoryTargetRoot, "01 - Track.wav"));

            AudioRenderFrozenOutputPlan filePlan = AudioRenderOutputPlanner.PlanLogicalTracks(
                fileInsteadOfDirectory,
                compilation.Tracks,
                project.Tracks.Count);
            AudioRenderFrozenOutputPlan directoryTargetPlan = AudioRenderOutputPlanner.PlanLogicalTracks(
                directoryTargetRoot,
                compilation.Tracks,
                project.Tracks.Count);
            AudioRenderFrozenOutputPlan emptyPlan = AudioRenderOutputPlanner.PlanLogicalTracks(
                Path.Combine(directory, "empty"),
                compilation.Tracks.Select(value => value with { Participates = false }),
                project.Tracks.Count);
            AudioRenderFrozenOutputPlan invalidPathPlan = AudioRenderOutputPlanner.PlanLogicalTracks(
                "\0",
                compilation.Tracks,
                project.Tracks.Count);

            Assert.False(filePlan.Succeeded);
            Assert.Contains(filePlan.Diagnostics,
                value => value.Message.Contains("existing file", StringComparison.Ordinal));
            Assert.False(directoryTargetPlan.Succeeded);
            Assert.Contains(directoryTargetPlan.Diagnostics,
                value => value.Message.Contains("existing directory", StringComparison.Ordinal));
            Assert.False(emptyPlan.Succeeded);
            Assert.Empty(emptyPlan.Targets);
            Assert.Contains(emptyPlan.Diagnostics,
                value => value.Message.Contains("no valid WAV target", StringComparison.Ordinal));
            Assert.False(invalidPathPlan.Succeeded);
            Assert.Empty(invalidPathPlan.Targets);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
