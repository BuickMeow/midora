using System.IO.Compression;
using Midora.Domain;
using Midora.Persistence;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectOpenCoordinatorTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 6, 10, 11, 12, TimeSpan.Zero);

    [Fact]
    public async Task ValidPackageReturnsPersistedCandidateWithoutHoldingSourceFile()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider clock = new(CreatedAt);
        MidoraProjectPackageV1 packages = new("1.2.3", clock);
        string path = temporary.PathFor("Project.midora");
        using MidoraProject source = new(480, CreatedAt);
        source.Metadata.ProjectName = "Opened";
        _ = await packages.SaveProjectAsync(source, path);
        RecordingProgress progress = new();

        await using ProjectOpenCandidate candidate = await new ProjectOpenCoordinator(packages)
            .OpenAsync(path, progress);

        Assert.Equal(ProjectDocumentOrigin.Persisted, candidate.Origin);
        Assert.Equal(Path.GetFullPath(path), candidate.CurrentProjectPath);
        Assert.Equal(new("1.2.3", "1.2.3"), candidate.FileInformation);
        Assert.Equal("Opened", candidate.Project.Metadata.ProjectName);
        Assert.False(candidate.RequiresSave);
        Assert.False(candidate.HasDamagedProjectObjects);
        Assert.True(candidate.CanSaveProject);
        Assert.Empty(candidate.Diagnostics);
        Assert.Equal(
            [
                ProjectOpenCandidateStage.ValidatingInput,
                ProjectOpenCandidateStage.ReadingAndValidatingPackage,
                ProjectOpenCandidateStage.CandidateReady
            ],
            progress.Values.Select(value => value.Stage));

        using FileStream exclusive = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    [Fact]
    public async Task CommitFactoriesStartTimeAndBindDocumentPersistenceToCandidate()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider clock = new(CreatedAt);
        MidoraProjectPackageV1 packages = new("1.0.0", clock);
        string path = temporary.PathFor("Project.midora");
        using (MidoraProject source = new(192, CreatedAt))
        {
            _ = await packages.SaveProjectAsync(source, path);
        }
        await using ProjectOpenCandidate candidate = await new ProjectOpenCoordinator(packages)
            .OpenAsync(path);
        clock.Advance(TimeSpan.FromHours(3));
        Assert.Equal(0, candidate.Project.Metadata.TotalEditingTimeMilliseconds);

        using ProjectCompilationSession compilation = new(
            candidate.Project,
            editingTimeProvider: clock);
        ProjectDocumentSession document = candidate.CreateDocumentSession(compilation);
        ProjectPersistenceCoordinator persistence = candidate.CreatePersistenceCoordinator(document);
        clock.Advance(TimeSpan.FromMilliseconds(1_250));

        Assert.Equal(1_250, compilation.SnapshotTotalEditingTimeMilliseconds());
        Assert.False(document.IsModified);
        Assert.False(document.NeedsSaveBeforeClose);
        Assert.Equal(candidate.CurrentProjectPath, persistence.CurrentProjectPath);
        Assert.Equal(candidate.FileInformation, persistence.FileInformation);
    }

    [Fact]
    public async Task RecoveredMetadataMarksDocumentModifiedUntilSuccessfulSave()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider clock = new(CreatedAt);
        MidoraProjectPackageV1 packages = new("1.0.0", clock);
        string path = temporary.PathFor("Recovered.midora");
        using (MidoraProject source = new(192, CreatedAt))
        {
            source.Metadata.ProjectName = "Will be recovered";
            _ = await packages.SaveProjectAsync(source, path);
        }
        DeleteEntry(path, "metadata.json");
        await using ProjectOpenCandidate candidate = await new ProjectOpenCoordinator(packages)
            .OpenAsync(path);

        Assert.True(candidate.RequiresSave);
        Assert.Empty(candidate.Project.Metadata.ProjectName);
        Assert.Equal(MidoraPackageDiagnosticSeverityV1.Error, Assert.Single(candidate.Diagnostics).Severity);
        using ProjectCompilationSession compilation = new(
            candidate.Project,
            editingTimeProvider: clock);
        ProjectDocumentSession document = candidate.CreateDocumentSession(compilation);
        Assert.True(document.IsModified);
        Assert.Equal([ProjectOpenCandidate.RecoveredSourceDirtyReason], document.ExternalDirtyReasons);
        ProjectPersistenceCoordinator persistence = candidate.CreatePersistenceCoordinator(document);

        _ = await persistence.SaveProjectAsync();

        Assert.False(document.IsModified);
        Assert.Empty(document.ExternalDirtyReasons);
        await using MidoraProjectOpenResultV1 reopened = await packages.OpenAsync(path);
        Assert.False(reopened.IsModified);
        Assert.Empty(reopened.Diagnostics);
    }

    [Fact]
    public async Task ExtraPackageEntryIsInformationWithoutModifiedState()
    {
        using TemporaryDirectory temporary = new();
        MidoraProjectPackageV1 packages = new("1.0.0");
        string path = temporary.PathFor("Extra.midora");
        using (MidoraProject source = new(192))
        {
            _ = await packages.SaveProjectAsync(source, path);
        }
        AddEntry(path, "unknown/readme.txt", "ignored");

        await using ProjectOpenCandidate candidate = await new ProjectOpenCoordinator(packages)
            .OpenAsync(path);

        Assert.False(candidate.RequiresSave);
        MidoraPackageDiagnosticV1 diagnostic = Assert.Single(candidate.Diagnostics);
        Assert.Equal(MidoraPackageDiagnosticSeverityV1.Information, diagnostic.Severity);
        Assert.Equal("unknown/readme.txt", diagnostic.PackagePath);
    }

    [Fact]
    public async Task InvalidPathPackageAndCancellationDoNotProduceCandidate()
    {
        using TemporaryDirectory temporary = new();
        ProjectOpenCoordinator coordinator = new(new MidoraProjectPackageV1("1.0.0"));
        string invalid = temporary.PathFor("invalid.midora");
        await File.WriteAllTextAsync(invalid, "not a zip");

        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.OpenAsync("relative.midora"));
        MidoraPackageExceptionV1 packageError = await Assert.ThrowsAsync<MidoraPackageExceptionV1>(
            () => coordinator.OpenAsync(invalid));
        Assert.Equal(MidoraPackageStageV1.Container, packageError.Stage);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.OpenAsync(invalid, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task CandidateFactoriesRejectDifferentProjectAndDisposedCandidate()
    {
        using TemporaryDirectory temporary = new();
        MidoraProjectPackageV1 packages = new("1.0.0");
        string path = temporary.PathFor("Project.midora");
        using (MidoraProject source = new(192))
        {
            _ = await packages.SaveProjectAsync(source, path);
        }
        ProjectOpenCandidate candidate = await new ProjectOpenCoordinator(packages).OpenAsync(path);
        using MidoraProject otherProject = new(192);
        using ProjectCompilationSession other = new(otherProject);

        Assert.Throws<InvalidOperationException>(() => candidate.CreateDocumentSession(other));
        await candidate.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => candidate.CreateDocumentSession(other));
    }

    private static void DeleteEntry(string packagePath, string entryPath)
    {
        using FileStream stream = new(packagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using ZipArchive archive = new(stream, ZipArchiveMode.Update);
        ZipArchiveEntry entry = archive.GetEntry(entryPath)
            ?? throw new InvalidOperationException($"Missing test package entry: {entryPath}");
        entry.Delete();
    }

    private static void AddEntry(string packagePath, string entryPath, string contents)
    {
        using FileStream stream = new(packagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using ZipArchive archive = new(stream, ZipArchiveMode.Update);
        ZipArchiveEntry entry = archive.CreateEntry(entryPath);
        using StreamWriter writer = new(entry.Open());
        writer.Write(contents);
    }

    private sealed class RecordingProgress : IProgress<ProjectOpenCandidateProgress>
    {
        public List<ProjectOpenCandidateProgress> Values { get; } = [];
        public void Report(ProjectOpenCandidateProgress value) => Values.Add(value);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan value)
        {
            _utcNow = _utcNow.Add(value);
            _timestamp = checked(_timestamp + value.Ticks);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(),
            $"midora-open-project-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(_path);
        public string PathFor(string fileName) => Path.Combine(_path, fileName);
        public void Dispose()
        {
            if (Directory.Exists(_path)) Directory.Delete(_path, recursive: true);
        }
    }
}
