using Midora.AudioRender;
using Midora.Domain;
using Midora.Persistence;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectCreationCoordinatorTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 6, 6, 7, 8, TimeSpan.Zero);

    [Fact]
    public async Task CreateUnsavedBuildsCompleteEmptyDefaultsWithoutHistory()
    {
        ManualTimeProvider clock = new(CreatedAt);
        ProjectCreationCoordinator coordinator = CreateCoordinator(clock);

        await using NewProjectCreationResult result = await coordinator.CreateAsync(new()
        {
            TicksPerQuarterNote = 960,
            ProjectName = "",
            ProjectVersion = "draft",
            AuthorOrTeam = "Team",
            OriginalWork = "Original",
            Copyright = "Copyright"
        });

        MidoraProject project = result.Project;
        Assert.Equal(ProjectDocumentOrigin.Unsaved, result.Origin);
        Assert.Null(result.CurrentProjectPath);
        Assert.Null(result.FileInformation);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(960, project.TicksPerQuarterNote);
        Assert.Equal(CreatedAt, project.Metadata.CreatedAtUtc);
        Assert.Equal(CreatedAt, project.Metadata.ModifiedAtUtc);
        Assert.Equal(string.Empty, project.Metadata.ProjectName);
        Assert.Equal("draft", project.Metadata.ProjectVersion);
        Assert.Equal("Team", project.Metadata.AuthorOrTeam);
        Assert.Equal("Original", project.Metadata.OriginalWork);
        Assert.Equal("Copyright", project.Metadata.Copyright);
        Assert.Equal(0, project.Metadata.TotalEditingTimeMilliseconds);
        Assert.Empty(project.EventInstruments);
        Assert.Empty(project.Tracks);
        Assert.Empty(project.PureMidiTracks);
        Assert.Empty(project.Conductor.KeySignatures);
        Assert.Empty(project.Conductor.Markers);
        Assert.Null(project.Conductor.EndMarker);
        Assert.Equal(120m, Assert.Single(project.Conductor.Tempos).BeatsPerMinute);
        TimeSignatureChange signature = Assert.Single(project.Conductor.TimeSignatures);
        Assert.Equal((0L, 4, 4), (signature.Tick, signature.Numerator, signature.Denominator));

        using ProjectCompilationSession compilation = new(project, editingTimeProvider: clock);
        ProjectDocumentSession document = new(compilation, result.Origin);
        Assert.True(compilation.LastAttempt.IsConsumable);
        Assert.Empty(document.History);
        Assert.False(document.IsModified);
        Assert.True(document.NeedsSaveBeforeClose);
    }

    [Fact]
    public async Task CreateAndSavePublishesBeforeReturningPersistedCandidate()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider clock = new(CreatedAt);
        MidoraProjectPackageV1 packages = new("1.2.3", clock);
        ProjectCreationCoordinator coordinator = new(packages, clock);
        string target = temporary.PathFor("created.midora");

        await using NewProjectCreationResult result = await coordinator.CreateAsync(new()
        {
            PersistenceMode = NewProjectPersistenceMode.CreateAndSave,
            TargetPath = target,
            ProjectName = "Created"
        });

        Assert.True(File.Exists(target));
        Assert.Equal(ProjectDocumentOrigin.Persisted, result.Origin);
        Assert.Equal(Path.GetFullPath(target), result.CurrentProjectPath);
        Assert.Equal(new("1.2.3", "1.2.3"), result.FileInformation);
        await using MidoraProjectOpenResultV1 reopened = await packages.OpenAsync(target);
        Assert.Equal("Created", reopened.Project.Metadata.ProjectName);
        Assert.Equal(192, reopened.Project.TicksPerQuarterNote);
    }

    [Fact]
    public async Task ExistingCreateAndSaveTargetRequiresExplicitOverwriteAuthorization()
    {
        using TemporaryDirectory temporary = new();
        ProjectCreationCoordinator coordinator = CreateCoordinator();
        string target = temporary.PathFor("existing.midora");
        await File.WriteAllTextAsync(target, "preserve");
        NewProjectCreationRequest request = new()
        {
            PersistenceMode = NewProjectPersistenceMode.CreateAndSave,
            TargetPath = target
        };

        MidoraPackageExceptionV1 collision = await Assert.ThrowsAsync<MidoraPackageExceptionV1>(
            () => coordinator.CreateAsync(request));

        Assert.Equal(MidoraPackageStageV1.Preflight, collision.Stage);
        Assert.Equal("preserve", await File.ReadAllTextAsync(target));
        await using NewProjectCreationResult result = await coordinator.CreateAsync(
            request with { OverwriteAuthorized = true });
        Assert.Equal(ProjectDocumentOrigin.Persisted, result.Origin);
        Assert.NotEqual("preserve", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task InvalidInputIsRejectedBeforeFileWrite()
    {
        using TemporaryDirectory temporary = new();
        ProjectCreationCoordinator coordinator = CreateCoordinator();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => coordinator.CreateAsync(new() { TicksPerQuarterNote = 0 }));
        await Assert.ThrowsAsync<ArgumentException>(
            () => coordinator.CreateAsync(new() { TargetPath = temporary.PathFor("unexpected.midora") }));
        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.CreateAsync(new()
        {
            PersistenceMode = NewProjectPersistenceMode.CreateAndSave,
            TargetPath = "relative.midora"
        }));

        Assert.Empty(Directory.EnumerateFiles(temporary.Path));
    }

    [Fact]
    public void AdoptImportedProjectKeepsTheProjectUnsavedAndDoesNotAttachAudioResources()
    {
        ProjectCreationCoordinator coordinator = CreateCoordinator();
        MidoraProject imported = new(480);

        using NewProjectCreationResult result = coordinator.AdoptImportedProject(imported);

        Assert.Same(imported, result.Project);
        Assert.Equal(ProjectDocumentOrigin.Unsaved, result.Origin);
        Assert.Null(result.CurrentProjectPath);
        Assert.Null(result.FileInformation);
        Assert.Empty(result.Diagnostics);
    }

    private static ProjectCreationCoordinator CreateCoordinator(ManualTimeProvider? clock = null) =>
        new(new MidoraProjectPackageV1("1.0.0", clock), clock);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        private long _timestamp = 0;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public override long GetTimestamp() => _timestamp;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"midora-create-project-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(_path);
        public string Path => _path;
        public string PathFor(string fileName) => System.IO.Path.Combine(_path, fileName);

        public void Dispose()
        {
            if (Directory.Exists(_path)) Directory.Delete(_path, recursive: true);
        }
    }
}
