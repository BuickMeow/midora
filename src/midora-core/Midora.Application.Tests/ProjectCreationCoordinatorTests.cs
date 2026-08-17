using Midora.Audio;
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
        ProjectCreationCoordinator coordinator = CreateCoordinator(clock: clock);

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
        Assert.Null(result.EffectiveSoundFontPath);
        Assert.False(result.UsedCaseInsensitiveSoundFontPathFallback);
        Assert.Null(result.EmbeddedSoundFontResource);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(960, project.TicksPerQuarterNote);
        Assert.Equal(CreatedAt, project.Metadata.CreatedAtUtc);
        Assert.Equal(CreatedAt, project.Metadata.ModifiedAtUtc);
        Assert.Equal("", project.Metadata.ProjectName);
        Assert.Equal("draft", project.Metadata.ProjectVersion);
        Assert.Equal("Team", project.Metadata.AuthorOrTeam);
        Assert.Equal("Original", project.Metadata.OriginalWork);
        Assert.Equal("Copyright", project.Metadata.Copyright);
        Assert.Equal(0, project.Metadata.TotalEditingTimeMilliseconds);
        Assert.Null(project.SoundFont.Reference);
        Assert.Empty(project.EventInstruments);
        Assert.Empty(project.Tracks);
        Assert.Equal(3, project.NextStableId);
        Assert.Equal(-0.1, project.Playback.MasterVolumeDecibels);
        Assert.True(project.Playback.LimiterEnabled);
        Assert.Equal(
            StopCursorBehavior.ReturnToPlaybackStart,
            project.Playback.StopCursorBehavior);
        Assert.Equal(AudioRenderMode.WholeMix, project.AudioRender.Mode);
        Assert.Equal(ProjectRangeMode.ProjectDefaultRange, project.AudioRender.RangeMode);
        Assert.Equal(
            ProjectTrackSelectionMode.AllValidLogicalTracks,
            project.AudioRender.TrackSelectionMode);
        Assert.Equal(48_000, project.AudioRender.SampleRate);
        Assert.Equal(500, project.AudioRender.MaximumSampleVoicesPerUnitStream);
        Assert.Empty(project.Conductor.KeySignatures);
        Assert.Empty(project.Conductor.Markers);
        Assert.Null(project.Conductor.EndMarker);
        TempoChange tempo = Assert.Single(project.Conductor.Tempos);
        Assert.Equal(0, tempo.Tick);
        Assert.Equal(120m, tempo.BeatsPerMinute);
        TimeSignatureChange signature = Assert.Single(project.Conductor.TimeSignatures);
        Assert.Equal(0, signature.Tick);
        Assert.Equal(4, signature.Numerator);
        Assert.Equal(4, signature.Denominator);

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
        ProjectCreationCoordinator coordinator = new(
            packages,
            new RecordingValidator(),
            clock);
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
        Assert.Equal(CreatedAt, result.Project.Metadata.ModifiedAtUtc);
        await using MidoraProjectOpenResultV1 reopened = await packages.OpenAsync(target);
        Assert.Equal("Created", reopened.Project.Metadata.ProjectName);
        Assert.Equal(192, reopened.Project.TicksPerQuarterNote);

        using ProjectCompilationSession compilation = new(
            result.Project,
            editingTimeProvider: clock);
        ProjectDocumentSession document = new(compilation, result.Origin);
        Assert.Empty(document.History);
        Assert.False(document.IsModified);
        Assert.False(document.NeedsSaveBeforeClose);
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

        MidoraPackageExceptionV1 collision =
            await Assert.ThrowsAsync<MidoraPackageExceptionV1>(() =>
                coordinator.CreateAsync(request));

        Assert.Equal(MidoraPackageStageV1.Preflight, collision.Stage);
        Assert.Equal("preserve", await File.ReadAllTextAsync(target));

        await using NewProjectCreationResult result = await coordinator.CreateAsync(
            request with { OverwriteAuthorized = true });
        Assert.Equal(ProjectDocumentOrigin.Persisted, result.Origin);
        Assert.NotEqual("preserve", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task InvalidInputIsRejectedBeforeSoundFontValidationOrFileWrite()
    {
        using TemporaryDirectory temporary = new();
        RecordingValidator validator = new();
        ProjectCreationCoordinator coordinator = CreateCoordinator(validator);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            coordinator.CreateAsync(new() { TicksPerQuarterNote = 0 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            coordinator.CreateAsync(new() { TicksPerQuarterNote = 32_768 }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            coordinator.CreateAsync(new() { ProjectName = "bad\nname" }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            coordinator.CreateAsync(new()
            {
                TargetPath = temporary.PathFor("not-allowed.midora")
            }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            coordinator.CreateAsync(new()
            {
                PersistenceMode = NewProjectPersistenceMode.CreateAndSave,
                TargetPath = "relative.midora"
            }));

        Assert.Empty(validator.ValidatedPaths);
        Assert.Empty(Directory.EnumerateFiles(temporary.Path));
    }

    [Fact]
    public async Task ExternalSoundFontRequiresCreateAndSave()
    {
        using TemporaryDirectory temporary = new();
        string soundFont = temporary.PathFor("Piano.sf2");
        await File.WriteAllBytesAsync(soundFont, [1, 2, 3]);
        RecordingValidator validator = new();
        ProjectCreationCoordinator coordinator = CreateCoordinator(validator);

        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.CreateAsync(new()
        {
            SoundFont = new(NewProjectSoundFontMode.ExternalRelative, soundFont)
        }));

        Assert.Empty(validator.ValidatedPaths);
    }

    [Fact]
    public async Task ExternalSoundFontIsValidatedBoundAndPersistedRelativeToTarget()
    {
        using TemporaryDirectory temporary = new();
        string soundFont = temporary.PathFor("Piano.sf2");
        await File.WriteAllBytesAsync(soundFont, [1, 2, 3, 4]);
        string differentlyCasedSelection = temporary.PathFor("pIaNo.SF2");
        string target = temporary.PathFor("Song.midora");
        RecordingValidator validator = new();
        MidoraProjectPackageV1 packages = new("1.0.0");
        ProjectCreationCoordinator coordinator = new(packages, validator);

        await using NewProjectCreationResult result = await coordinator.CreateAsync(new()
        {
            PersistenceMode = NewProjectPersistenceMode.CreateAndSave,
            TargetPath = target,
            SoundFont = new(
                NewProjectSoundFontMode.ExternalRelative,
                differentlyCasedSelection)
        });

        ExternalProjectSoundFontReference reference =
            Assert.IsType<ExternalProjectSoundFontReference>(result.Project.SoundFont.Reference);
        Assert.Equal("Piano.sf2", reference.RelativePath);
        Assert.Equal(4, reference.FileSizeBytes);
        Assert.Equal(Path.GetFullPath(soundFont), result.EffectiveSoundFontPath);
        Assert.True(result.UsedCaseInsensitiveSoundFontPathFallback);
        Assert.Equal([Path.GetFullPath(soundFont)], validator.ValidatedPaths);
        await using MidoraProjectOpenResultV1 reopened = await packages.OpenAsync(target);
        Assert.Equal(reference, reopened.Project.SoundFont.Reference);
    }

    [Fact]
    public async Task ExternalSoundFontOutsideAllowedProjectLocationsFailsAtomically()
    {
        using TemporaryDirectory projectDirectory = new();
        using TemporaryDirectory resourceDirectory = new();
        string soundFont = resourceDirectory.PathFor("Piano.sf2");
        await File.WriteAllBytesAsync(soundFont, [1, 2, 3]);
        string target = projectDirectory.PathFor("Song.midora");
        RecordingValidator validator = new();
        ProjectCreationCoordinator coordinator = CreateCoordinator(validator);

        await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.CreateAsync(new()
        {
            PersistenceMode = NewProjectPersistenceMode.CreateAndSave,
            TargetPath = target,
            SoundFont = new(NewProjectSoundFontMode.ExternalRelative, soundFont)
        }));

        Assert.Empty(validator.ValidatedPaths);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task ExternalSoundFontContentRaceRejectsCandidateAndTarget()
    {
        using TemporaryDirectory temporary = new();
        string soundFont = temporary.PathFor("Changing.sf2");
        await File.WriteAllBytesAsync(soundFont, [1, 2, 3]);
        string target = temporary.PathFor("Song.midora");
        ProjectCreationCoordinator coordinator = CreateCoordinator(new RecordingValidator(
            onValidate: path => File.WriteAllBytes(path, [9, 8, 7, 6])));

        ProjectSoundFontSelectionException failure =
            await Assert.ThrowsAsync<ProjectSoundFontSelectionException>(() =>
                coordinator.CreateAsync(new()
                {
                    PersistenceMode = NewProjectPersistenceMode.CreateAndSave,
                    TargetPath = target,
                    SoundFont = new(NewProjectSoundFontMode.ExternalRelative, soundFont)
                }));

        Assert.Equal(
            ProjectSoundFontSelectionFailure.ContentChangedDuringValidation,
            failure.Failure);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task EmbeddedSoundFontCanCreateUnsavedAndOwnsValidatedSnapshot()
    {
        using TemporaryDirectory temporary = new();
        string soundFont = temporary.PathFor("Embedded.sf2");
        await File.WriteAllBytesAsync(soundFont, [4, 3, 2, 1]);
        RecordingValidator validator = new();
        ProjectCreationCoordinator coordinator = CreateCoordinator(validator);

        NewProjectCreationResult result = await coordinator.CreateAsync(new()
        {
            SoundFont = new(NewProjectSoundFontMode.Embedded, soundFont)
        });
        string runtimePath = result.EffectiveSoundFontPath!;

        EmbeddedProjectSoundFontReference reference =
            Assert.IsType<EmbeddedProjectSoundFontReference>(result.Project.SoundFont.Reference);
        Assert.NotEqual(default, reference.ResourceId);
        Assert.Equal(4, reference.FileSizeBytes);
        Assert.True(result.EmbeddedSoundFontResource!.IsAvailable);
        Assert.True(File.Exists(runtimePath));
        Assert.Equal([runtimePath], validator.ValidatedPaths);

        await result.DisposeAsync();
        Assert.False(File.Exists(runtimePath));
    }

    [Fact]
    public async Task EmbeddedCreateAndSavePublishesResourceAndRemainsReopenable()
    {
        using TemporaryDirectory temporary = new();
        string soundFont = temporary.PathFor("Embedded.sf2");
        await File.WriteAllBytesAsync(soundFont, [4, 3, 2, 1]);
        string target = temporary.PathFor("Embedded.midora");
        MidoraProjectPackageV1 packages = new("1.0.0");
        ProjectCreationCoordinator coordinator = new(
            packages,
            new RecordingValidator());

        await using (NewProjectCreationResult result = await coordinator.CreateAsync(new()
        {
            PersistenceMode = NewProjectPersistenceMode.CreateAndSave,
            TargetPath = target,
            SoundFont = new(NewProjectSoundFontMode.Embedded, soundFont)
        }))
        {
            Assert.NotNull(result.EmbeddedSoundFontResource);
            Assert.True(File.Exists(result.EffectiveSoundFontPath));
        }

        await using MidoraProjectOpenResultV1 reopened = await packages.OpenAsync(target);
        Assert.IsType<EmbeddedProjectSoundFontReference>(reopened.Project.SoundFont.Reference);
        Assert.True(reopened.EmbeddedSoundFontResource!.IsAvailable);
        Assert.Equal([4, 3, 2, 1], await File.ReadAllBytesAsync(
            reopened.EmbeddedSoundFontResource.ResolvedAbsolutePath!));
    }

    [Fact]
    public async Task EmbeddedValidationFailureCleansSnapshotAndDoesNotPublish()
    {
        using TemporaryDirectory temporary = new();
        string soundFont = temporary.PathFor("Invalid.sf2");
        await File.WriteAllBytesAsync(soundFont, [1, 2, 3]);
        string target = temporary.PathFor("Invalid.midora");
        RecordingValidator validator = new(new SoundFontLoadabilityException(
            SoundFontLoadabilityFailure.UnsupportedOrCorrupt,
            "Rejected for test."));
        ProjectCreationCoordinator coordinator = CreateCoordinator(validator);

        await Assert.ThrowsAsync<SoundFontLoadabilityException>(() =>
            coordinator.CreateAsync(new()
            {
                PersistenceMode = NewProjectPersistenceMode.CreateAndSave,
                TargetPath = target,
                SoundFont = new(NewProjectSoundFontMode.Embedded, soundFont)
            }));

        string validatedSnapshot = Assert.Single(validator.ValidatedPaths);
        Assert.False(File.Exists(validatedSnapshot));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task EmbeddedSavePreflightFailureCleansValidatedSnapshot()
    {
        using TemporaryDirectory temporary = new();
        string soundFont = temporary.PathFor("Embedded.sf2");
        await File.WriteAllBytesAsync(soundFont, [1, 2, 3]);
        string target = temporary.PathFor("Existing.midora");
        await File.WriteAllTextAsync(target, "preserve");
        RecordingValidator validator = new();
        ProjectCreationCoordinator coordinator = CreateCoordinator(validator);

        MidoraPackageExceptionV1 failure =
            await Assert.ThrowsAsync<MidoraPackageExceptionV1>(() =>
                coordinator.CreateAsync(new()
                {
                    PersistenceMode = NewProjectPersistenceMode.CreateAndSave,
                    TargetPath = target,
                    SoundFont = new(NewProjectSoundFontMode.Embedded, soundFont)
                }));

        Assert.Equal(MidoraPackageStageV1.Preflight, failure.Stage);
        string validatedSnapshot = Assert.Single(validator.ValidatedPaths);
        Assert.False(File.Exists(validatedSnapshot));
        Assert.Equal("preserve", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public void SoundFontSelectionRejectsInvalidModeAndPathCombinations()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NewProjectSoundFontSelection((NewProjectSoundFontMode)999));
        Assert.Throws<ArgumentException>(() =>
            new NewProjectSoundFontSelection(
                NewProjectSoundFontMode.None,
                Path.GetFullPath("unexpected.sf2")));
        Assert.Throws<ArgumentNullException>(() =>
            new NewProjectSoundFontSelection(
                NewProjectSoundFontMode.Embedded));
        Assert.Throws<ArgumentException>(() =>
            new NewProjectSoundFontSelection(
                NewProjectSoundFontMode.Embedded,
                "relative.sf2"));
    }

    [Fact]
    public async Task CreationWorkDoesNotStartProjectEditingTimeBeforeCommit()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider clock = new(CreatedAt);
        string soundFont = temporary.PathFor("Slow.sf2");
        await File.WriteAllBytesAsync(soundFont, [1]);
        RecordingValidator validator = new(onValidate: _ =>
            clock.Advance(TimeSpan.FromHours(2)));
        ProjectCreationCoordinator coordinator = CreateCoordinator(validator, clock);

        await using NewProjectCreationResult result = await coordinator.CreateAsync(new()
        {
            SoundFont = new(NewProjectSoundFontMode.Embedded, soundFont)
        });

        Assert.Equal(CreatedAt, result.Project.Metadata.CreatedAtUtc);
        Assert.Equal(0, result.Project.Metadata.TotalEditingTimeMilliseconds);
        using ProjectCompilationSession compilation = new(
            result.Project,
            editingTimeProvider: clock);
        clock.Advance(TimeSpan.FromMilliseconds(1250));
        Assert.Equal(1250, compilation.SnapshotTotalEditingTimeMilliseconds());
    }

    private static ProjectCreationCoordinator CreateCoordinator(
        RecordingValidator? validator = null,
        ManualTimeProvider? clock = null) =>
        new(
            new MidoraProjectPackageV1("1.0.0", clock),
            validator ?? new RecordingValidator(),
            clock);

    private sealed class RecordingValidator(
        Exception? failure = null,
        Action<string>? onValidate = null) : ISoundFontLoadabilityValidator
    {
        public List<string> ValidatedPaths { get; } = [];

        public ValueTask ValidateAsync(
            string soundFontPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Path.GetFullPath(soundFontPath);
            ValidatedPaths.Add(path);
            onValidate?.Invoke(path);
            if (failure is not null)
            {
                throw failure;
            }
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
            $"midora-create-project-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(_path);

        public string Path => _path;
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
