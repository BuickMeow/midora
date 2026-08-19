using Midora.Domain;
using Midora.Persistence;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectPersistenceCoordinatorTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 6, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public async Task FirstSaveCommitsPathFileInformationAndDocumentBaseline()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider clock = new(CreatedAt);
        MidoraProjectPackageV1 packages = new("1.2.3", clock);
        MidoraProject project = new(192, CreatedAt);
        using ProjectCompilationSession compilation = new(
            project,
            editingTimeProvider: clock);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Unsaved);
        ProjectPersistenceCoordinator persistence = new(document, packages);
        string target = temporary.PathFor("first.midora");

        MidoraProjectSaveResultV1 result = await persistence.SaveProjectAsync(target);

        Assert.Equal(Path.GetFullPath(target), persistence.CurrentProjectPath);
        Assert.Equal(result.FileInformation, persistence.FileInformation);
        Assert.True(document.HasPersistentOrigin);
        Assert.False(document.IsModified);
        Assert.False(document.NeedsSaveBeforeClose);
        Assert.Empty(document.History);
        await using MidoraProjectOpenResultV1 opened = await packages.OpenAsync(target);
        Assert.Equal(192, opened.Project.TicksPerQuarterNote);
        Assert.Equal(result.FileInformation, opened.FileInformation);
    }

    [Fact]
    public async Task FirstSaveRequiresTargetAndExplicitOverwriteAuthorization()
    {
        using TemporaryDirectory temporary = new();
        MidoraProjectPackageV1 packages = new("1.0.0");
        using ProjectCompilationSession compilation = new(new MidoraProject(192));
        ProjectDocumentSession document = new(compilation);
        ProjectPersistenceCoordinator persistence = new(document, packages);
        string target = temporary.PathFor("existing.midora");
        await File.WriteAllTextAsync(target, "preserve");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            persistence.SaveProjectAsync());
        MidoraPackageExceptionV1 collision =
            await Assert.ThrowsAsync<MidoraPackageExceptionV1>(() =>
                persistence.SaveProjectAsync(target));
        Assert.Equal(MidoraPackageStageV1.Preflight, collision.Stage);
        Assert.Equal("preserve", await File.ReadAllTextAsync(target));
        Assert.Null(persistence.CurrentProjectPath);
        Assert.False(document.HasPersistentOrigin);

        _ = await persistence.SaveProjectAsync(target, overwriteAuthorized: true);
        Assert.Equal(Path.GetFullPath(target), persistence.CurrentProjectPath);
        Assert.True(document.HasPersistentOrigin);
    }

    [Fact]
    public async Task PersistedSaveCannotChangePathAndCommitsCurrentEdits()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider clock = new(CreatedAt);
        MidoraProjectPackageV1 packages = new("1.0.0", clock);
        MidoraProject project = new(192, CreatedAt);
        using ProjectCompilationSession compilation = new(
            project,
            editingTimeProvider: clock);
        ProjectDocumentSession document = new(compilation);
        ProjectPersistenceCoordinator persistence = new(document, packages);
        string current = temporary.PathFor("current.midora");
        _ = await persistence.SaveProjectAsync(current);
        _ = document.Execute(ProjectDomainEditCommands.UpdateProjectMetadata(
            "Changed",
            "",
            "",
            "",
            ""));
        Assert.True(document.IsModified);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            persistence.SaveProjectAsync(temporary.PathFor("other.midora")));
        Assert.True(document.IsModified);
        Assert.False(File.Exists(temporary.PathFor("other.midora")));

        clock.Advance(TimeSpan.FromMinutes(1));
        _ = await persistence.SaveProjectAsync();
        Assert.False(document.IsModified);
        Assert.Equal(CreatedAt.AddMinutes(1), project.Metadata.ModifiedAtUtc);
        await using MidoraProjectOpenResultV1 opened = await packages.OpenAsync(current);
        Assert.Equal("Changed", opened.Project.Metadata.ProjectName);
    }

    [Fact]
    public async Task SaveCopyPreservesCurrentPathModifiedStateHistoryAndMetadataTime()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider clock = new(CreatedAt);
        MidoraProjectPackageV1 packages = new("1.0.0", clock);
        MidoraProject project = new(192, CreatedAt);
        using ProjectCompilationSession compilation = new(
            project,
            editingTimeProvider: clock);
        ProjectDocumentSession document = new(compilation);
        ProjectPersistenceCoordinator persistence = new(document, packages);
        string current = temporary.PathFor("current.midora");
        _ = await persistence.SaveProjectAsync(current);
        _ = document.Execute(ProjectDomainEditCommands.UpdateProjectMetadata(
            "Copy content",
            "",
            "",
            "",
            ""));
        IReadOnlyList<ProjectHistoryEntryInfo> history = document.History;
        MidoraProjectFileInformationV1 fileInformation = persistence.FileInformation!;
        DateTimeOffset currentModifiedAt = project.Metadata.ModifiedAtUtc;
        clock.Advance(TimeSpan.FromHours(1));
        string copy = temporary.PathFor("copy.midora");

        MidoraProjectSaveResultV1 copyResult = await persistence.SaveCopyAsync(copy);

        Assert.Equal(Path.GetFullPath(current), persistence.CurrentProjectPath);
        Assert.Equal(fileInformation, persistence.FileInformation);
        Assert.True(document.IsModified);
        Assert.Equal(history, document.History);
        Assert.Equal(currentModifiedAt, project.Metadata.ModifiedAtUtc);
        Assert.Equal(Path.GetFullPath(copy), copyResult.TargetPath);
        await using MidoraProjectOpenResultV1 opened = await packages.OpenAsync(copy);
        Assert.Equal("Copy content", opened.Project.Metadata.ProjectName);
        Assert.Equal(CreatedAt.AddHours(1), opened.Project.Metadata.ModifiedAtUtc);
    }

    [Fact]
    public async Task SaveCopySnapshotsEditingTimeWithoutMarkingProjectModified()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider clock = new(CreatedAt);
        MidoraProjectPackageV1 packages = new("1.0.0", clock);
        MidoraProject project = new(192, CreatedAt);
        using ProjectCompilationSession compilation = new(
            project,
            editingTimeProvider: clock);
        ProjectDocumentSession document = new(compilation);
        ProjectPersistenceCoordinator persistence = new(document, packages);
        _ = await persistence.SaveProjectAsync(temporary.PathFor("current.midora"));
        clock.Advance(TimeSpan.FromMilliseconds(2_500));

        string copy = temporary.PathFor("time-copy.midora");
        _ = await persistence.SaveCopyAsync(copy);

        Assert.Equal(2_500, project.Metadata.TotalEditingTimeMilliseconds);
        Assert.False(document.IsModified);
        await using MidoraProjectOpenResultV1 opened = await packages.OpenAsync(copy);
        Assert.Equal(2_500, opened.Project.Metadata.TotalEditingTimeMilliseconds);
    }

    [Fact]
    public async Task SaveCopyCannotOverwriteCurrentProjectFile()
    {
        using TemporaryDirectory temporary = new();
        MidoraProjectPackageV1 packages = new("1.0.0");
        using ProjectCompilationSession compilation = new(new MidoraProject(192));
        ProjectDocumentSession document = new(compilation);
        ProjectPersistenceCoordinator persistence = new(document, packages);
        string current = temporary.PathFor("current.midora");
        _ = await persistence.SaveProjectAsync(current);
        byte[] before = await File.ReadAllBytesAsync(current);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            persistence.SaveCopyAsync(current, overwriteAuthorized: true));

        Assert.Equal(before, await File.ReadAllBytesAsync(current));
        Assert.Equal(Path.GetFullPath(current), persistence.CurrentProjectPath);
    }

    [Fact]
    public async Task FailedSaveDoesNotCommitRuntimeDocumentState()
    {
        using TemporaryDirectory temporary = new();
        MidoraProjectPackageV1 packages = new("1.0.0");
        MidoraProject project = new(192);
        project.DamagedEventInstruments.Add(new(
            project.AllocateStableId(),
            "Damaged",
            "instruments/damaged.pb",
            "broken",
            0));
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation);
        ProjectPersistenceCoordinator persistence = new(document, packages);
        Assert.False(persistence.CanSaveProject);

        ProjectPersistenceUnavailableException failure =
            await Assert.ThrowsAsync<ProjectPersistenceUnavailableException>(() =>
                persistence.SaveProjectAsync(temporary.PathFor("blocked.midora")));

        Assert.Equal(
            ProjectPersistenceUnavailability.DamagedProjectObjects,
            failure.Unavailability);
        Assert.Null(persistence.CurrentProjectPath);
        Assert.Null(persistence.FileInformation);
        Assert.False(document.HasPersistentOrigin);
        Assert.True(document.NeedsSaveBeforeClose);
    }

    [Fact]
    public async Task DamagedPureMidiPlaceholderAlsoBlocksSave()
    {
        using TemporaryDirectory temporary = new();
        MidoraProjectPackageV1 packages = new("1.0.0");
        MidoraProject project = new(192);
        project.DamagedMidiChannelRoots.Add(new(
            project.AllocateStableId(),
            "Damaged Root",
            "midi-channel-roots/damaged.pb",
            "broken",
            0));
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation);
        ProjectPersistenceCoordinator persistence = new(document, packages);

        Assert.False(persistence.CanSaveProject);
        ProjectPersistenceUnavailableException failure =
            await Assert.ThrowsAsync<ProjectPersistenceUnavailableException>(() =>
                persistence.SaveProjectAsync(temporary.PathFor("blocked-midi.midora")));

        Assert.Equal(
            ProjectPersistenceUnavailability.DamagedProjectObjects,
            failure.Unavailability);
    }

    [Fact]
    public async Task EmbeddedSoundFontProviderParticipatesInSaveReadinessAndPackageWrite()
    {
        using TemporaryDirectory temporary = new();
        MidoraProjectPackageV1 packages = new("1.0.0");
        MidoraProject project = new(192);
        string soundFont = temporary.PathFor("source.sf2");
        await File.WriteAllBytesAsync(soundFont, [1, 2, 3, 4]);
        await using EmbeddedSoundFontResourceV1 resource =
            await SoundFontBindingV1.BindEmbeddedAsync(project, soundFont);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation);
        ProjectPersistenceCoordinator persistence = new(
            document,
            packages,
            embeddedSoundFontResourceProvider: () => resource);
        Assert.True(persistence.CanSaveProject);

        string target = temporary.PathFor("embedded.midora");
        _ = await persistence.SaveProjectAsync(target);

        await using MidoraProjectOpenResultV1 opened = await packages.OpenAsync(target);
        Assert.NotNull(opened.EmbeddedSoundFontResource);
        Assert.True(opened.EmbeddedSoundFontResource.IsAvailable);
    }

    [Fact]
    public async Task MissingEmbeddedSoundFontResourceIsNotSaveable()
    {
        using TemporaryDirectory temporary = new();
        MidoraProjectPackageV1 packages = new("1.0.0");
        MidoraProject project = new(192);
        _ = project.SoundFont.SetEmbedded(
            project,
            "missing.sf2",
            new string('0', 64),
            1);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation);
        ProjectPersistenceCoordinator persistence = new(document, packages);
        Assert.False(persistence.CanSaveProject);

        MidoraEmbeddedSoundFontRepairRequiredExceptionV1 failure =
            await Assert.ThrowsAsync<MidoraEmbeddedSoundFontRepairRequiredExceptionV1>(() =>
                persistence.SaveProjectAsync(temporary.PathFor("missing.midora")));
        Assert.Equal(EmbeddedSoundFontResourceStatusV1.RuntimeResourceMissing, failure.ResourceStatus);
        Assert.Null(persistence.CurrentProjectPath);
    }

    [Fact]
    public async Task ConcurrentPersistenceOperationIsRejectedWithoutQueueing()
    {
        using TemporaryDirectory temporary = new();
        using ManualResetEventSlim providerEntered = new();
        using ManualResetEventSlim releaseProvider = new();
        MidoraProjectPackageV1 packages = new("1.0.0");
        using ProjectCompilationSession compilation = new(new MidoraProject(192));
        ProjectDocumentSession document = new(compilation);
        ProjectPersistenceCoordinator persistence = new(
            document,
            packages,
            embeddedSoundFontResourceProvider: () =>
            {
                providerEntered.Set();
                releaseProvider.Wait(TimeSpan.FromSeconds(10));
                return null;
            });
        Task<MidoraProjectSaveResultV1> active = Task.Run(() =>
            persistence.SaveCopyAsync(temporary.PathFor("first.midora")));
        Assert.True(providerEntered.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(persistence.IsOperationActive);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                persistence.SaveCopyAsync(temporary.PathFor("second.midora")));
        }
        finally
        {
            releaseProvider.Set();
        }
        _ = await active;
        Assert.False(persistence.IsOperationActive);
        Assert.False(File.Exists(temporary.PathFor("second.midora")));
    }

    [Fact]
    public async Task CancelledFirstSaveReleasesOperationAndDoesNotCommitPath()
    {
        using TemporaryDirectory temporary = new();
        MidoraProjectPackageV1 packages = new("1.0.0");
        using ProjectCompilationSession compilation = new(new MidoraProject(192));
        ProjectDocumentSession document = new(compilation);
        ProjectPersistenceCoordinator persistence = new(document, packages);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            persistence.SaveProjectAsync(
                temporary.PathFor("cancelled.midora"),
                cancellationToken: cancellation.Token));

        Assert.False(persistence.IsOperationActive);
        Assert.Null(persistence.CurrentProjectPath);
        Assert.Null(persistence.FileInformation);
        Assert.False(document.HasPersistentOrigin);
        Assert.False(File.Exists(temporary.PathFor("cancelled.midora")));
    }

    [Fact]
    public void ConstructorRejectsOriginPathAndFileInformationMismatch()
    {
        MidoraProjectPackageV1 packages = new("1.0.0");
        using ProjectCompilationSession compilation = new(new MidoraProject(192));
        ProjectDocumentSession unsaved = new(compilation);
        Assert.Throws<ArgumentException>(() =>
            new ProjectPersistenceCoordinator(
                unsaved,
                packages,
                Path.GetFullPath("project.midora"),
                new("1.0.0", "1.0.0")));
        Assert.Throws<ArgumentException>(() =>
            new ProjectPersistenceCoordinator(
                unsaved,
                packages,
                fileInformation: new("1.0.0", "1.0.0")));
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
        private readonly string _path = Path.Combine(
            Path.GetTempPath(),
            $"midora-application-persistence-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(_path);

        public string PathFor(string fileName) => Path.Combine(_path, fileName);

        public void Dispose()
        {
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, recursive: true);
            }
        }
    }
}
