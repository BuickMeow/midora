using Midora.Audio;
using Midora.Domain;
using Midora.Persistence;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectSoundFontRuntimeSessionTests
{
    [Fact]
    public async Task NoReferenceClearsStaleEffectivePathWithoutBlockingProjectUse()
    {
        MidoraProject project = CreateProject();
        using ProjectCompilationSession compilation = new(
            project,
            Path.GetFullPath("stale.sf2"));
        using ProjectSoundFontRuntimeSession runtime = new(
            compilation,
            new RecordingValidator());

        ProjectSoundFontRuntimeSnapshot result = await runtime.RefreshAsync(
            currentProjectFilePath: null);

        Assert.Equal(ProjectSoundFontAvailability.NoReference, result.Availability);
        Assert.False(result.IsAvailable);
        Assert.Null(compilation.EffectiveSoundFontPath);
        Assert.True(compilation.LastAttempt.IsConsumable);
    }

    [Fact]
    public async Task ExternalOpenVerificationMonitorInvalidationAndHashWarningPreserveSource()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string projectPath = Path.Combine(directory, "Song.midora");
            string soundFontPath = Path.Combine(directory, "Piano.sf2");
            await File.WriteAllBytesAsync(soundFontPath, [1, 2, 3, 4]);
            ExternalSoundFontBindingV1 binding = await SoundFontBindingV1.BindExternalAsync(
                projectPath,
                soundFontPath);
            MidoraProject project = CreateProject();
            project.SoundFont.SetReference(binding.Reference);
            using ProjectCompilationSession compilation = new(project);
            RecordingValidator validator = new();
            using ProjectSoundFontRuntimeSession runtime = new(compilation, validator);

            ProjectSoundFontRuntimeSnapshot initial = await runtime.RefreshAsync(projectPath);

            Assert.True(initial.IsAvailable);
            Assert.True(initial.HashMatches);
            Assert.False(initial.RequiresWarning);
            Assert.Equal(Path.GetFullPath(soundFontPath), compilation.EffectiveSoundFontPath);
            Assert.Equal(1, validator.CallCount);
            Assert.True(runtime.TryConfirmReadyForAudioStart());
            string storedHash = binding.Reference.Sha256;
            TaskCompletionSource invalidated = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.AvailabilityChanged += (_, _) =>
            {
                if (runtime.Current.Availability
                    == ProjectSoundFontAvailability.VerificationRequired)
                {
                    invalidated.TrySetResult();
                }
            };

            await File.WriteAllBytesAsync(soundFontPath, [5, 6, 7, 8]);
            File.SetLastWriteTimeUtc(soundFontPath, DateTime.UtcNow.AddSeconds(5));
            Assert.False(runtime.TryConfirmReadyForAudioStart());
            await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Null(compilation.EffectiveSoundFontPath);
            ProjectSoundFontRuntimeSnapshot changed = await runtime.RefreshAsync(
                projectPath,
                forceFullExternalVerification: true);
            Assert.True(changed.IsAvailable);
            Assert.False(changed.HashMatches);
            Assert.True(changed.RequiresWarning);
            Assert.Equal(storedHash, binding.Reference.Sha256);
            Assert.Equal(Path.GetFullPath(soundFontPath), compilation.EffectiveSoundFontPath);
            Assert.Equal(2, validator.CallCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MissingAndUnsupportedExternalResourcesRemainNonConsumableWithoutProjectMutation()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string projectPath = Path.Combine(directory, "Song.midora");
            ExternalProjectSoundFontReference missingReference = new(
                "Missing.sf2",
                "Missing.sf2",
                new string('0', 64),
                1);
            MidoraProject missingProject = CreateProject();
            missingProject.SoundFont.SetReference(missingReference);
            using ProjectCompilationSession missingCompilation = new(missingProject);
            using ProjectSoundFontRuntimeSession missingRuntime = new(
                missingCompilation,
                new RecordingValidator());

            ProjectSoundFontRuntimeSnapshot missing = await missingRuntime.RefreshAsync(projectPath);

            Assert.Equal(ProjectSoundFontAvailability.Missing, missing.Availability);
            Assert.Null(missingCompilation.EffectiveSoundFontPath);
            Assert.Equal(missingReference, missingProject.SoundFont.Reference);

            string invalidPath = Path.Combine(directory, "Invalid.sf2");
            await File.WriteAllBytesAsync(invalidPath, [1, 2, 3]);
            ExternalSoundFontBindingV1 invalidBinding = await SoundFontBindingV1.BindExternalAsync(
                projectPath,
                invalidPath);
            MidoraProject invalidProject = CreateProject();
            invalidProject.SoundFont.SetReference(invalidBinding.Reference);
            using ProjectCompilationSession invalidCompilation = new(invalidProject);
            using ProjectSoundFontRuntimeSession invalidRuntime = new(
                invalidCompilation,
                new RecordingValidator(new SoundFontLoadabilityException(
                    SoundFontLoadabilityFailure.UnsupportedOrCorrupt,
                    "Rejected for test.")));

            ProjectSoundFontRuntimeSnapshot unsupported = await invalidRuntime.RefreshAsync(
                projectPath);

            Assert.Equal(
                ProjectSoundFontAvailability.UnsupportedOrCorrupt,
                unsupported.Availability);
            Assert.Equal(
                SoundFontLoadabilityFailure.UnsupportedOrCorrupt,
                unsupported.LoadabilityFailure);
            Assert.Null(invalidCompilation.EffectiveSoundFontPath);
            Assert.Equal(invalidBinding.Reference, invalidProject.SoundFont.Reference);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task EmbeddedOpenResourceIsLoadValidatedWithoutTakingLeaseOwnership()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string soundFontPath = Path.Combine(directory, "Embedded.sf2");
            await File.WriteAllBytesAsync(soundFontPath, [1, 2, 3, 4]);
            MidoraProject project = CreateProject();
            await using EmbeddedSoundFontResourceV1 resource =
                await SoundFontBindingV1.BindEmbeddedAsync(project, soundFontPath);
            EmbeddedProjectSoundFontReference reference =
                Assert.IsType<EmbeddedProjectSoundFontReference>(project.SoundFont.Reference);
            using ProjectCompilationSession compilation = new(project);
            RecordingValidator validator = new();
            using ProjectSoundFontRuntimeSession runtime = new(compilation, validator);

            ProjectSoundFontRuntimeSnapshot result = await runtime.RefreshAsync(
                currentProjectFilePath: null,
                resource);

            Assert.True(result.IsAvailable);
            Assert.Equal(reference, result.Reference);
            Assert.Equal(resource.ResolvedAbsolutePath, compilation.EffectiveSoundFontPath);
            Assert.Equal(1, validator.CallCount);
            Assert.True(resource.IsAvailable);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ProjectEditLockRejectsRefreshBeforeChangingAvailability()
    {
        MidoraProject project = CreateProject();
        using ProjectCompilationSession compilation = new(project);
        using ProjectSoundFontRuntimeSession runtime = new(
            compilation,
            new RecordingValidator());
        using IDisposable editLock = compilation.AcquireProjectEditLock();

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RefreshAsync(null));

        Assert.Equal(ProjectSoundFontAvailability.NotVerified, runtime.Current.Availability);
        Assert.Null(compilation.EffectiveSoundFontPath);
    }

    [Fact]
    public async Task SourceBindingHistoryChangeRequiresRuntimeRefreshBeforePlaybackConsumption()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string projectPath = Path.Combine(directory, "Song.midora");
            string soundFontPath = Path.Combine(directory, "Piano.sf2");
            await File.WriteAllBytesAsync(soundFontPath, [1, 2, 3, 4]);
            MidoraProject project = CreateProject();
            using ProjectCompilationSession compilation = new(project);
            ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
            RecordingValidator validator = new();
            using ProjectSoundFontRuntimeSession runtime = new(compilation, validator);
            ProjectSoundFontEditing editing = new(document, validator);
            Assert.Equal(
                ProjectSoundFontAvailability.NoReference,
                (await runtime.RefreshAsync(projectPath)).Availability);

            ExternalSoundFontEditExecution selected = await editing.SelectExternalAsync(
                projectPath,
                soundFontPath);

            Assert.True(selected.ProjectEdit.Changed);
            Assert.Equal(
                ProjectSoundFontAvailability.VerificationRequired,
                runtime.Current.Availability);
            Assert.Null(compilation.EffectiveSoundFontPath);

            Assert.True((await runtime.RefreshAsync(projectPath)).IsAvailable);
            Assert.Equal(Path.GetFullPath(soundFontPath), compilation.EffectiveSoundFontPath);

            Assert.True(editing.Clear().Changed);
            Assert.Equal(ProjectSoundFontAvailability.NoReference, runtime.Current.Availability);
            Assert.Null(compilation.EffectiveSoundFontPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task StaleVerificationCannotOverwriteAConcurrentSourceBinding()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string projectPath = Path.Combine(directory, "Song.midora");
            string firstPath = Path.Combine(directory, "First.sf2");
            string secondPath = Path.Combine(directory, "Second.sf2");
            await File.WriteAllBytesAsync(firstPath, [1, 2, 3]);
            await File.WriteAllBytesAsync(secondPath, [4, 5, 6]);
            ExternalSoundFontBindingV1 first = await SoundFontBindingV1.BindExternalAsync(
                projectPath,
                firstPath);
            ExternalSoundFontBindingV1 second = await SoundFontBindingV1.BindExternalAsync(
                projectPath,
                secondPath);
            MidoraProject project = CreateProject();
            project.SoundFont.SetReference(first.Reference);
            using ProjectCompilationSession compilation = new(project);
            RecordingValidator validator = new(onValidate: _ =>
            {
                project.SoundFont.SetReference(second.Reference);
                compilation.SetEffectiveSoundFontPath(second.ResolvedAbsolutePath);
            });
            using ProjectSoundFontRuntimeSession runtime = new(compilation, validator);

            ProjectSoundFontRuntimeSnapshot result = await runtime.RefreshAsync(projectPath);

            Assert.Equal(
                ProjectSoundFontAvailability.VerificationRequired,
                result.Availability);
            Assert.Equal(second.Reference, project.SoundFont.Reference);
            Assert.Equal(
                Path.GetFullPath(second.ResolvedAbsolutePath),
                compilation.EffectiveSoundFontPath);

            await File.WriteAllBytesAsync(firstPath, [7, 8, 9]);
            await Task.Delay(300);
            Assert.Equal(
                Path.GetFullPath(second.ResolvedAbsolutePath),
                compilation.EffectiveSoundFontPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DisposalIsIdempotent()
    {
        using ProjectCompilationSession compilation = new(CreateProject());
        ProjectSoundFontRuntimeSession runtime = new(
            compilation,
            new RecordingValidator());

        runtime.Dispose();
        runtime.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = runtime.Current);
    }

    private static MidoraProject CreateProject() =>
        new(480, new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero));

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"midora-sf2-runtime-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class RecordingValidator(
        Exception? failure = null,
        Action<string>? onValidate = null) : ISoundFontLoadabilityValidator
    {
        public int CallCount { get; private set; }

        public ValueTask ValidateAsync(
            string soundFontPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            onValidate?.Invoke(soundFontPath);
            if (failure is not null)
            {
                throw failure;
            }
            return ValueTask.CompletedTask;
        }
    }
}
