using System.IO.Compression;
using Midora.Audio;
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
        string path = temporary.PathFor("Project.zip");
        MidoraProject source = new(480, CreatedAt);
        source.Metadata.ProjectName = "Opened";
        _ = await packages.SaveProjectAsync(source, path);
        RecordingProgress progress = new();
        ProjectOpenCoordinator coordinator = new(packages);

        await using ProjectOpenCandidate candidate = await coordinator.OpenAsync(
            path,
            progress);

        Assert.Equal(ProjectDocumentOrigin.Persisted, candidate.Origin);
        Assert.Equal(Path.GetFullPath(path), candidate.CurrentProjectPath);
        Assert.Equal(new("1.2.3", "1.2.3"), candidate.FileInformation);
        Assert.Equal("Opened", candidate.Project.Metadata.ProjectName);
        Assert.False(candidate.RequiresSave);
        Assert.False(candidate.HasDamagedProjectObjects);
        Assert.True(candidate.CanSaveProject);
        Assert.Empty(candidate.Diagnostics);
        Assert.Equal(
            new(ProjectSoundFontAvailability.NoReference, null),
            candidate.InitialSoundFontState);
        Assert.Equal(
            [
                ProjectOpenCandidateStage.ValidatingInput,
                ProjectOpenCandidateStage.ReadingAndValidatingPackage,
                ProjectOpenCandidateStage.CandidateReady
            ],
            progress.Values.Select(value => value.Stage));

        using FileStream exclusive = new(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    [Fact]
    public async Task CommitFactoriesStartTimeAndBindDocumentPersistenceToCandidate()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider clock = new(CreatedAt);
        MidoraProjectPackageV1 packages = new("1.0.0", clock);
        string path = temporary.PathFor("Project.midora");
        _ = await packages.SaveProjectAsync(new MidoraProject(192, CreatedAt), path);
        ProjectOpenCoordinator coordinator = new(packages);
        await using ProjectOpenCandidate candidate = await coordinator.OpenAsync(path);
        clock.Advance(TimeSpan.FromHours(3));
        Assert.Equal(0, candidate.Project.Metadata.TotalEditingTimeMilliseconds);

        using ProjectCompilationSession compilation = new(
            candidate.Project,
            editingTimeProvider: clock);
        ProjectDocumentSession document = candidate.CreateDocumentSession(compilation);
        ProjectPersistenceCoordinator persistence =
            candidate.CreatePersistenceCoordinator(document);
        using ProjectSoundFontRuntimeSession soundFont =
            candidate.CreateSoundFontRuntimeSession(
                compilation,
                new RecordingValidator());
        clock.Advance(TimeSpan.FromMilliseconds(1_250));

        Assert.Equal(1_250, compilation.SnapshotTotalEditingTimeMilliseconds());
        Assert.False(document.IsModified);
        Assert.False(document.NeedsSaveBeforeClose);
        Assert.Equal(candidate.CurrentProjectPath, persistence.CurrentProjectPath);
        Assert.Equal(candidate.FileInformation, persistence.FileInformation);
        ProjectSoundFontRuntimeSnapshot soundFontState = await soundFont.RefreshAsync(
            candidate.CurrentProjectPath,
            candidate.EmbeddedSoundFontResource);
        Assert.Equal(ProjectSoundFontAvailability.NoReference, soundFontState.Availability);
    }

    [Fact]
    public async Task RecoveredMetadataMarksDocumentModifiedUntilSuccessfulSave()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider clock = new(CreatedAt);
        MidoraProjectPackageV1 packages = new("1.0.0", clock);
        string path = temporary.PathFor("Recovered.midora");
        MidoraProject source = new(192, CreatedAt);
        source.Metadata.ProjectName = "Will be recovered";
        _ = await packages.SaveProjectAsync(source, path);
        DeleteEntry(path, "metadata.json");
        ProjectOpenCoordinator coordinator = new(packages);
        await using ProjectOpenCandidate candidate = await coordinator.OpenAsync(path);

        Assert.True(candidate.RequiresSave);
        Assert.Empty(candidate.Project.Metadata.ProjectName);
        MidoraPackageDiagnosticV1 diagnostic = Assert.Single(candidate.Diagnostics);
        Assert.Equal(MidoraPackageDiagnosticSeverityV1.Error, diagnostic.Severity);
        Assert.Equal("metadata.json", diagnostic.PackagePath);
        using ProjectCompilationSession compilation = new(
            candidate.Project,
            editingTimeProvider: clock);
        ProjectDocumentSession document = candidate.CreateDocumentSession(compilation);
        Assert.True(document.IsModified);
        Assert.Equal(
            [ProjectOpenCandidate.RecoveredSourceDirtyReason],
            document.ExternalDirtyReasons);
        ProjectPersistenceCoordinator persistence =
            candidate.CreatePersistenceCoordinator(document);

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
        _ = await packages.SaveProjectAsync(new MidoraProject(192), path);
        AddEntry(path, "unknown/readme.txt", "ignored");
        ProjectOpenCoordinator coordinator = new(packages);

        await using ProjectOpenCandidate candidate = await coordinator.OpenAsync(path);

        Assert.False(candidate.RequiresSave);
        MidoraPackageDiagnosticV1 diagnostic = Assert.Single(candidate.Diagnostics);
        Assert.Equal(MidoraPackageDiagnosticSeverityV1.Information, diagnostic.Severity);
        Assert.Equal("unknown/readme.txt", diagnostic.PackagePath);
    }

    [Fact]
    public async Task DamagedObjectCandidateIsOpenButCannotBeSaved()
    {
        using TemporaryDirectory temporary = new();
        MidoraProjectPackageV1 packages = new("1.0.0");
        MidoraProject source = new(192);
        EventInstrument instrument = EventInstrumentLibrary.Create(
            source,
            "Damaged");
        source.ArrangementParents.Add(new(
            ArrangementParentKind.EventInstrument,
            instrument.Id));
        string path = temporary.PathFor("Damaged.midora");
        _ = await packages.SaveProjectAsync(source, path);
        DeleteEntry(path, $"event-instruments/ei_{instrument.Id}.pb");
        ProjectOpenCoordinator coordinator = new(packages);
        await using ProjectOpenCandidate candidate = await coordinator.OpenAsync(path);

        Assert.True(candidate.HasDamagedProjectObjects);
        Assert.False(candidate.CanSaveProject);
        Assert.False(candidate.RequiresSave);
        DamagedProjectObject damaged = Assert.Single(
            candidate.Project.DamagedEventInstruments);
        Assert.Equal(instrument.Id, damaged.Id);
        Assert.Equal("Damaged", damaged.NameSnapshot);
        using ProjectCompilationSession compilation = new(candidate.Project);
        ProjectDocumentSession document = candidate.CreateDocumentSession(compilation);
        ProjectPersistenceCoordinator persistence =
            candidate.CreatePersistenceCoordinator(document);
        Assert.False(persistence.CanSaveProject);
    }

    [Fact]
    public async Task EmbeddedResourceOwnershipAndRuntimeVerificationFollowCandidate()
    {
        using TemporaryDirectory temporary = new();
        MidoraProjectPackageV1 packages = new("1.0.0");
        MidoraProject source = new(192);
        string soundFont = temporary.PathFor("Embedded.sf2");
        await File.WriteAllBytesAsync(soundFont, [1, 2, 3, 4]);
        await using EmbeddedSoundFontResourceV1 imported =
            await SoundFontBindingV1.BindEmbeddedAsync(source, soundFont);
        string path = temporary.PathFor("Embedded.midora");
        _ = await packages.SaveProjectAsync(
            source,
            path,
            embeddedSoundFontResource: imported);
        ProjectOpenCoordinator coordinator = new(packages);
        ProjectOpenCandidate candidate = await coordinator.OpenAsync(path);
        string extractedPath = candidate.EmbeddedSoundFontResource!.ResolvedAbsolutePath!;

        Assert.True(candidate.CanSaveProject);
        Assert.Equal(
            ProjectSoundFontAvailability.VerificationRequired,
            candidate.InitialSoundFontState.Availability);
        Assert.True(File.Exists(extractedPath));
        using (ProjectCompilationSession compilation = new(candidate.Project))
        using (ProjectSoundFontRuntimeSession runtime =
            candidate.CreateSoundFontRuntimeSession(
                compilation,
                new RecordingValidator()))
        {
            ProjectSoundFontRuntimeSnapshot verified = await runtime.RefreshAsync(
                candidate.CurrentProjectPath,
                candidate.EmbeddedSoundFontResource);
            Assert.True(verified.IsAvailable);
            Assert.Equal(extractedPath, verified.ResolvedAbsolutePath);
        }

        await candidate.DisposeAsync();
        Assert.False(File.Exists(extractedPath));
    }

    [Fact]
    public async Task MissingExternalSoundFontDoesNotBlockSourceOpen()
    {
        using TemporaryDirectory temporary = new();
        MidoraProjectPackageV1 packages = new("1.0.0");
        MidoraProject source = new(192);
        source.SoundFont.SetExternal(
            "Missing.sf2",
            "Missing.sf2",
            new string('0', 64),
            123);
        string path = temporary.PathFor("External.midora");
        _ = await packages.SaveProjectAsync(source, path);
        ProjectOpenCoordinator coordinator = new(packages);
        await using ProjectOpenCandidate candidate = await coordinator.OpenAsync(path);

        Assert.Equal(
            ProjectSoundFontAvailability.VerificationRequired,
            candidate.InitialSoundFontState.Availability);
        Assert.True(candidate.CanSaveProject);
        using ProjectCompilationSession compilation = new(candidate.Project);
        using ProjectSoundFontRuntimeSession runtime =
            candidate.CreateSoundFontRuntimeSession(
                compilation,
                new RecordingValidator());
        ProjectSoundFontRuntimeSnapshot verified = await runtime.RefreshAsync(
            candidate.CurrentProjectPath);
        Assert.Equal(ProjectSoundFontAvailability.Missing, verified.Availability);
        Assert.False(verified.IsAvailable);
    }

    [Fact]
    public async Task InvalidPathPackageAndCancellationDoNotProduceCandidate()
    {
        using TemporaryDirectory temporary = new();
        MidoraProjectPackageV1 packages = new("1.0.0");
        ProjectOpenCoordinator coordinator = new(packages);
        string invalid = temporary.PathFor("invalid.midora");
        await File.WriteAllTextAsync(invalid, "not a zip");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            coordinator.OpenAsync("relative.midora"));
        MidoraPackageExceptionV1 packageError =
            await Assert.ThrowsAsync<MidoraPackageExceptionV1>(() =>
                coordinator.OpenAsync(invalid));
        Assert.Equal(MidoraPackageStageV1.Container, packageError.Stage);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.OpenAsync(invalid, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task CandidateFactoriesRejectDifferentProjectAndDisposedCandidate()
    {
        using TemporaryDirectory temporary = new();
        MidoraProjectPackageV1 packages = new("1.0.0");
        string path = temporary.PathFor("Project.midora");
        _ = await packages.SaveProjectAsync(new MidoraProject(192), path);
        ProjectOpenCandidate candidate = await new ProjectOpenCoordinator(packages)
            .OpenAsync(path);
        using ProjectCompilationSession other = new(new MidoraProject(192));

        Assert.Throws<InvalidOperationException>(() =>
            candidate.CreateDocumentSession(other));
        await candidate.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() =>
            candidate.CreateDocumentSession(other));
    }

    private static void DeleteEntry(string packagePath, string entryPath)
    {
        using FileStream stream = new(
            packagePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        using ZipArchive archive = new(stream, ZipArchiveMode.Update);
        ZipArchiveEntry entry = archive.GetEntry(entryPath)
            ?? throw new InvalidOperationException($"Missing test package entry: {entryPath}");
        entry.Delete();
    }

    private static void AddEntry(
        string packagePath,
        string entryPath,
        string contents)
    {
        using FileStream stream = new(
            packagePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
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

    private sealed class RecordingValidator : ISoundFontLoadabilityValidator
    {
        public ValueTask ValidateAsync(
            string soundFontPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
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
            if (value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            _utcNow = _utcNow.Add(value);
            _timestamp = checked(_timestamp + value.Ticks);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"midora-open-project-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(_path);

        public string PathFor(string fileName) => System.IO.Path.Combine(_path, fileName);

        public void Dispose()
        {
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, recursive: true);
            }
        }
    }
}
