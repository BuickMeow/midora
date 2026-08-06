using Midora.Domain;

namespace Midora.Persistence.Tests;

public sealed class MidoraProjectPackageFaultInjectionV1Tests
{
    private static readonly DateTimeOffset SavedAt =
        new(2026, 8, 6, 9, 10, 11, TimeSpan.Zero);

    [Theory]
    [InlineData((int)MidoraPackageFaultPointV1.BeforeContainerOpen, MidoraPackageStageV1.Container)]
    [InlineData((int)MidoraPackageFaultPointV1.BeforeManifestRead, MidoraPackageStageV1.Manifest)]
    public async Task OpenIoFailureReportsTheExactFailedStage(
        int faultPointValue,
        MidoraPackageStageV1 expectedStage)
    {
        using TemporaryDirectory temporary = new();
        string targetPath = temporary.PathFor("source.midora");
        await CreateService().SaveProjectAsync(CreateProject(), targetPath);
        MidoraProjectPackageV1 packages = CreateService((MidoraPackageFaultPointV1)faultPointValue);

        MidoraPackageExceptionV1 failure = await Assert.ThrowsAsync<MidoraPackageExceptionV1>(() =>
            packages.OpenAsync(targetPath));

        Assert.Equal(expectedStage, failure.Stage);
        Assert.Equal(targetPath, failure.TargetPath);
    }

    [Theory]
    [InlineData((int)MidoraPackageFaultPointV1.BeforeBackup, MidoraPackageStageV1.Backup)]
    [InlineData((int)MidoraPackageFaultPointV1.BeforeContentWrite, MidoraPackageStageV1.Staging)]
    [InlineData((int)MidoraPackageFaultPointV1.BeforeZipWrite, MidoraPackageStageV1.Staging)]
    [InlineData((int)MidoraPackageFaultPointV1.BeforeSelfValidation, MidoraPackageStageV1.SelfValidation)]
    public async Task FailureBeforePublishPreservesTargetAndCleansTransactionArtifacts(
        int faultPointValue,
        MidoraPackageStageV1 expectedStage)
    {
        using TemporaryDirectory temporary = new();
        string targetPath = temporary.PathFor("target.midora");
        byte[] original = [1, 3, 5, 7, 9];
        await File.WriteAllBytesAsync(targetPath, original);
        MidoraProject project = CreateProject();
        DateTimeOffset originalModifiedAt = project.Metadata.ModifiedAtUtc;
        MidoraProjectPackageV1 packages = CreateService((MidoraPackageFaultPointV1)faultPointValue);

        MidoraPackageExceptionV1 failure = await Assert.ThrowsAsync<MidoraPackageExceptionV1>(() =>
            packages.SaveProjectAsync(project, targetPath, overwriteAuthorized: true));

        Assert.Equal(expectedStage, failure.Stage);
        Assert.Equal(original, await File.ReadAllBytesAsync(targetPath));
        Assert.Equal(originalModifiedAt, project.Metadata.ModifiedAtUtc);
        AssertNoTransactionArtifacts(temporary.Path);
    }

    [Fact]
    public async Task PublishFailurePreservesRecoveryArtifactsAndOriginalTarget()
    {
        using TemporaryDirectory temporary = new();
        string targetPath = temporary.PathFor("target.midora");
        byte[] original = [2, 4, 6, 8];
        await File.WriteAllBytesAsync(targetPath, original);
        MidoraProject project = CreateProject();
        DateTimeOffset originalModifiedAt = project.Metadata.ModifiedAtUtc;
        MidoraProjectPackageV1 packages = CreateService(MidoraPackageFaultPointV1.BeforePublish);

        MidoraPackageExceptionV1 failure = await Assert.ThrowsAsync<MidoraPackageExceptionV1>(() =>
            packages.SaveProjectAsync(project, targetPath, overwriteAuthorized: true));

        Assert.Equal(MidoraPackageStageV1.Publish, failure.Stage);
        Assert.Equal(original, await File.ReadAllBytesAsync(targetPath));
        Assert.Equal(originalModifiedAt, project.Metadata.ModifiedAtUtc);
        Assert.NotNull(failure.BackupPath);
        Assert.NotNull(failure.TemporaryPath);
        Assert.True(File.Exists(failure.BackupPath));
        Assert.True(File.Exists(failure.TemporaryPath));
        Assert.Equal(original, await File.ReadAllBytesAsync(failure.BackupPath));
        Assert.Empty(Directory.GetFileSystemEntries(temporary.Path, ".midora-save-*"));
    }

    [Theory]
    [InlineData((int)MidoraPackageFaultPointV1.BeforeBackupCleanup, ".*.midora-backup-*")]
    [InlineData((int)MidoraPackageFaultPointV1.BeforeStagingDirectoryCleanup, ".midora-save-*")]
    public async Task CleanupFailureDoesNotTurnPublishedSaveIntoFailure(
        int faultPointValue,
        string retainedPattern)
    {
        using TemporaryDirectory temporary = new();
        string targetPath = temporary.PathFor("target.midora");
        await File.WriteAllBytesAsync(targetPath, [9, 9, 9]);
        MidoraProject project = CreateProject();
        MidoraProjectPackageV1 packages = CreateService((MidoraPackageFaultPointV1)faultPointValue);

        MidoraProjectSaveResultV1 result = await packages.SaveProjectAsync(
            project,
            targetPath,
            overwriteAuthorized: true);

        MidoraPackageDiagnosticV1 warning = Assert.Single(result.Diagnostics);
        Assert.Equal(MidoraPackageDiagnosticSeverityV1.Warning, warning.Severity);
        Assert.Equal(MidoraPackageDiagnosticCategoryV1.SaveTransaction, warning.Category);
        Assert.Equal("MIDORA-PERSIST-CLEANUP-FAILED", warning.Code);
        Assert.NotEmpty(Directory.GetFileSystemEntries(temporary.Path, retainedPattern));
        Assert.Equal(SavedAt, project.Metadata.ModifiedAtUtc);
        await using MidoraProjectOpenResultV1 opened = await CreateService().OpenAsync(targetPath);
        Assert.False(opened.IsModified);
        Assert.Empty(opened.Diagnostics);
    }

    private static MidoraProjectPackageV1 CreateService(MidoraPackageFaultPointV1? point = null) =>
        new(
            "0.1.0-test",
            new FixedTimeProvider(SavedAt),
            point.HasValue
                ? new OneShotFaultInjector(point.Value)
                : NoOpMidoraPackageFaultInjectorV1.Instance);

    private static MidoraProject CreateProject()
    {
        MidoraProject project = new(480, SavedAt.AddDays(-1));
        project.Metadata.ProjectName = "Fault Injection";
        project.Conductor.Markers.Add(new ProjectMarker(project, 240, "Marker"));
        return project;
    }

    private static void AssertNoTransactionArtifacts(string directory)
    {
        Assert.Empty(Directory.GetFileSystemEntries(directory, ".midora-save-*"));
        Assert.Empty(Directory.GetFileSystemEntries(directory, ".*.midora-temp-*"));
        Assert.Empty(Directory.GetFileSystemEntries(directory, ".*.midora-backup-*"));
    }

    private sealed class OneShotFaultInjector(MidoraPackageFaultPointV1 requestedPoint)
        : IMidoraPackageFaultInjectorV1
    {
        private int _thrown;

        public void ThrowIfRequested(MidoraPackageFaultPointV1 point, string path)
        {
            if (point == requestedPoint && Interlocked.Exchange(ref _thrown, 1) == 0)
            {
                throw new IOException($"Injected package fault at {point} for '{path}'.");
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"midora-package-fault-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string PathFor(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
