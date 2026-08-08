using Midora.Audio;
using Midora.Compiler;
using Midora.Domain;
using Midora.Persistence;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectSoundFontEditingTests
{
    [Fact]
    public async Task ExternalSelectionAtomicallyUpdatesSourceRuntimeHistoryAndUndoRedo()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string projectPath = Path.Combine(directory, "Project.midora");
            string soundFontPath = Path.Combine(directory, "Piano.sf2");
            await File.WriteAllBytesAsync(soundFontPath, [1, 2, 3, 4]);
            MidoraProject project = CreateProject();
            using ProjectCompilationSession compilation = new(project);
            ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
            RecordingValidator validator = new();
            ProjectSoundFontEditing editing = new(document, validator);
            long fingerprint = compilation.LastAttempt.Fingerprint;

            ExternalSoundFontEditExecution selected = await editing.SelectExternalAsync(
                projectPath,
                soundFontPath);

            ExternalProjectSoundFontReference reference = Assert.IsType<ExternalProjectSoundFontReference>(
                project.SoundFont.Reference);
            Assert.True(selected.ProjectEdit.Changed);
            Assert.Equal("Piano.sf2", reference.RelativePath);
            Assert.Equal(4, reference.FileSizeBytes);
            Assert.Equal(Path.GetFullPath(soundFontPath), compilation.EffectiveSoundFontPath);
            Assert.Equal([Path.GetFullPath(soundFontPath)], validator.ValidatedPaths);
            Assert.True(document.IsModified);
            Assert.Equal(fingerprint, compilation.LastAttempt.Fingerprint);
            Assert.Equal(0, compilation.LastCompilationTelemetry.RecompiledTrackCount);
            AssertCurrentCompilationMatchesFull(compilation);

            document.Undo();

            Assert.Null(project.SoundFont.Reference);
            Assert.Null(compilation.EffectiveSoundFontPath);
            Assert.False(document.IsModified);
            AssertCurrentCompilationMatchesFull(compilation);

            document.Redo();

            Assert.Equal(reference, project.SoundFont.Reference);
            Assert.Equal(Path.GetFullPath(soundFontPath), compilation.EffectiveSoundFontPath);
            Assert.True(document.IsModified);
            AssertCurrentCompilationMatchesFull(compilation);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ValidationFailureDoesNotMutateProjectOrHistory()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string projectPath = Path.Combine(directory, "Project.midora");
            string soundFontPath = Path.Combine(directory, "Invalid.sf2");
            await File.WriteAllBytesAsync(soundFontPath, [5, 6, 7]);
            MidoraProject project = CreateProject();
            using ProjectCompilationSession compilation = new(project);
            ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
            ProjectSoundFontEditing editing = new(
                document,
                new RecordingValidator(new SoundFontLoadabilityException(
                    SoundFontLoadabilityFailure.UnsupportedOrCorrupt,
                    "Rejected for test.")));
            CanonicalCompiledResult before = compilation.LastAttempt;

            SoundFontLoadabilityException error = await Assert.ThrowsAsync<SoundFontLoadabilityException>(
                () => editing.SelectExternalAsync(projectPath, soundFontPath));

            Assert.Equal(SoundFontLoadabilityFailure.UnsupportedOrCorrupt, error.Failure);
            Assert.Null(project.SoundFont.Reference);
            Assert.Null(compilation.EffectiveSoundFontPath);
            Assert.False(document.CanUndo);
            Assert.False(document.IsModified);
            Assert.Same(before, compilation.LastAttempt);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ContentChangeDuringLoadabilityValidationRejectsSelection()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string projectPath = Path.Combine(directory, "Project.midora");
            string soundFontPath = Path.Combine(directory, "Changing.sf2");
            await File.WriteAllBytesAsync(soundFontPath, [1, 2, 3]);
            MidoraProject project = CreateProject();
            using ProjectCompilationSession compilation = new(project);
            ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
            RecordingValidator validator = new(onValidate: path =>
                File.WriteAllBytes(path, [9, 8, 7, 6]));
            ProjectSoundFontEditing editing = new(document, validator);

            ProjectSoundFontSelectionException error =
                await Assert.ThrowsAsync<ProjectSoundFontSelectionException>(
                    () => editing.SelectExternalAsync(projectPath, soundFontPath));

            Assert.Equal(
                ProjectSoundFontSelectionFailure.ContentChangedDuringValidation,
                error.Failure);
            Assert.Null(project.SoundFont.Reference);
            Assert.Null(compilation.EffectiveSoundFontPath);
            Assert.False(document.CanUndo);
            Assert.False(document.IsModified);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ClearIsReversibleAndRepeatedSelectionOrClearIsANoOperation()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string projectPath = Path.Combine(directory, "Project.midora");
            string soundFontPath = Path.Combine(directory, "Piano.sf2");
            await File.WriteAllBytesAsync(soundFontPath, [1, 2, 3, 4]);
            MidoraProject project = CreateProject();
            using ProjectCompilationSession compilation = new(project);
            ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
            ProjectSoundFontEditing editing = new(document, new RecordingValidator());
            ExternalSoundFontEditExecution first = await editing.SelectExternalAsync(
                projectPath,
                soundFontPath);
            document.MarkSaveSucceeded();

            ExternalSoundFontEditExecution repeated = await editing.SelectExternalAsync(
                projectPath,
                soundFontPath);
            ProjectEditExecution cleared = editing.Clear();

            Assert.False(repeated.ProjectEdit.Changed);
            Assert.True(cleared.Changed);
            Assert.Null(project.SoundFont.Reference);
            Assert.Null(compilation.EffectiveSoundFontPath);
            Assert.True(document.IsModified);

            document.Undo();

            Assert.Equal(first.Reference, project.SoundFont.Reference);
            Assert.Equal(Path.GetFullPath(soundFontPath), compilation.EffectiveSoundFontPath);
            Assert.False(document.IsModified);

            ProjectEditExecution repeatedClearAfterUndo = editing.Clear();
            Assert.True(repeatedClearAfterUndo.Changed);
            Assert.False(editing.Clear().Changed);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ActiveProjectEditLockRejectsSelectionBeforeFileValidation()
    {
        MidoraProject project = CreateProject();
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        RecordingValidator validator = new();
        ProjectSoundFontEditing editing = new(document, validator);
        using IDisposable editLock = compilation.AcquireProjectEditLock();

        await Assert.ThrowsAsync<InvalidOperationException>(() => editing.SelectExternalAsync(
            Path.GetFullPath("Project.midora"),
            Path.GetFullPath("Piano.sf2")));

        Assert.Empty(validator.ValidatedPaths);
        Assert.False(document.CanUndo);
    }

    [Fact]
    public async Task EmbeddedSelectionStagesBeforeAllocationAndUndoRedoKeepLeaseAndIdentity()
    {
        string directory = CreateTemporaryDirectory();
        string? stagedPath = null;
        try
        {
            string sourcePath = Path.Combine(directory, "Embedded.sf2");
            await File.WriteAllBytesAsync(sourcePath, [4, 3, 2, 1]);
            MidoraProject project = CreateProject();
            using ProjectCompilationSession compilation = new(project);
            ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Unsaved);
            long before = project.NextStableId;

            await using (ProjectSoundFontEditing editing = new(document, new RecordingValidator()))
            {
                EmbeddedSoundFontEditExecution selected =
                    await editing.SelectEmbeddedAsync(sourcePath);
                stagedPath = selected.Resource.ResolvedAbsolutePath;

                Assert.Equal(before, selected.Reference.ResourceId.Value);
                Assert.Equal(before + 1, project.NextStableId);
                Assert.Equal(selected.Reference, project.SoundFont.Reference);
                Assert.Same(selected.Resource, editing.CurrentEmbeddedSoundFontResource);
                Assert.Equal(stagedPath, compilation.EffectiveSoundFontPath);
                Assert.True(File.Exists(stagedPath));
                AssertCurrentCompilationMatchesFull(compilation);

                document.Undo();
                Assert.Null(project.SoundFont.Reference);
                Assert.Null(editing.CurrentEmbeddedSoundFontResource);
                Assert.Null(compilation.EffectiveSoundFontPath);
                Assert.Equal(before + 1, project.NextStableId);
                Assert.True(File.Exists(stagedPath));

                document.Redo();
                Assert.Equal(selected.Reference, project.SoundFont.Reference);
                Assert.Same(selected.Resource, editing.CurrentEmbeddedSoundFontResource);
                Assert.Equal(before + 1, project.NextStableId);
                AssertCurrentCompilationMatchesFull(compilation);

                MidoraProjectPackageV1 packages = new("1.0.0");
                ProjectPersistenceCoordinator persistence = new(
                    document,
                    packages,
                    embeddedSoundFontResourceProvider: () =>
                        editing.CurrentEmbeddedSoundFontResource);
                string target = Path.Combine(directory, "Project.midora");
                _ = await persistence.SaveProjectAsync(target);
                await using MidoraProjectOpenResultV1 reopened = await packages.OpenAsync(target);
                Assert.Equal(selected.Reference, reopened.Project.SoundFont.Reference);
                Assert.True(reopened.EmbeddedSoundFontResource!.IsAvailable);
            }

            Assert.False(File.Exists(stagedPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task EmbeddedValidationFailureDoesNotAllocateOrRetainStagedSnapshot()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string sourcePath = Path.Combine(directory, "Changing.sf2");
            await File.WriteAllBytesAsync(sourcePath, [1, 2, 3]);
            MidoraProject project = CreateProject();
            using ProjectCompilationSession compilation = new(project);
            ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
            string? stagedPath = null;
            await using ProjectSoundFontEditing editing = new(
                document,
                new RecordingValidator(onValidate: path =>
                {
                    stagedPath = path;
                    File.WriteAllBytes(path, [9, 8, 7, 6]);
                }));
            long highWater = project.NextStableId;

            ProjectSoundFontSelectionException failure =
                await Assert.ThrowsAsync<ProjectSoundFontSelectionException>(() =>
                    editing.SelectEmbeddedAsync(sourcePath));

            Assert.Equal(
                ProjectSoundFontSelectionFailure.ContentChangedDuringValidation,
                failure.Failure);
            Assert.NotNull(stagedPath);
            Assert.False(File.Exists(stagedPath));
            Assert.Equal(highWater, project.NextStableId);
            Assert.Null(project.SoundFont.Reference);
            Assert.Null(editing.CurrentEmbeddedSoundFontResource);
            Assert.False(document.CanUndo);
            Assert.False(document.IsModified);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task EmbeddedReplacementClearAndUndoRestoreMatchingRuntimeLease()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string firstPath = Path.Combine(directory, "First.sf2");
            string secondPath = Path.Combine(directory, "Second.sf2");
            await File.WriteAllBytesAsync(firstPath, [1]);
            await File.WriteAllBytesAsync(secondPath, [2]);
            MidoraProject project = CreateProject();
            using ProjectCompilationSession compilation = new(project);
            ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
            await using ProjectSoundFontEditing editing = new(document, new RecordingValidator());

            EmbeddedSoundFontEditExecution first = await editing.SelectEmbeddedAsync(firstPath);
            EmbeddedSoundFontEditExecution second = await editing.SelectEmbeddedAsync(secondPath);
            Assert.Equal(second.Reference, project.SoundFont.Reference);
            Assert.Same(second.Resource, editing.CurrentEmbeddedSoundFontResource);

            document.Undo();
            Assert.Equal(first.Reference, project.SoundFont.Reference);
            Assert.Same(first.Resource, editing.CurrentEmbeddedSoundFontResource);
            Assert.Equal(first.Resource.ResolvedAbsolutePath, compilation.EffectiveSoundFontPath);

            document.Redo();
            Assert.Equal(second.Reference, project.SoundFont.Reference);
            Assert.Same(second.Resource, editing.CurrentEmbeddedSoundFontResource);

            editing.Clear();
            Assert.Null(project.SoundFont.Reference);
            Assert.Null(editing.CurrentEmbeddedSoundFontResource);
            document.Undo();
            Assert.Equal(second.Reference, project.SoundFont.Reference);
            Assert.Same(second.Resource, editing.CurrentEmbeddedSoundFontResource);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static MidoraProject CreateProject() =>
        new(480, new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero));

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"midora-sf2-edit-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void AssertCurrentCompilationMatchesFull(ProjectCompilationSession compilation)
    {
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult expected = compiler.CompileFull(compilation.Project);
        CanonicalCompiledResult actual = compilation.LastAttempt;
        Assert.Equal(expected.IsConsumable, actual.IsConsumable);
        Assert.Equal(expected.IsPartial, actual.IsPartial);
        Assert.Equal(expected.FailureStage, actual.FailureStage);
        Assert.Equal(expected.StartTick, actual.StartTick);
        Assert.Equal(expected.EndTick, actual.EndTick);
        Assert.Equal(expected.Fingerprint, actual.Fingerprint);
        Assert.Equal(expected.Statistics, actual.Statistics);
        Assert.Equal(expected.Events.ToArray(), actual.Events.ToArray());
        Assert.Equal(expected.Allocations.ToArray(), actual.Allocations.ToArray());
        Assert.Equal(expected.Diagnostics, actual.Diagnostics);
    }

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
            ValidatedPaths.Add(Path.GetFullPath(soundFontPath));
            onValidate?.Invoke(soundFontPath);
            if (failure is not null)
            {
                throw failure;
            }
            return ValueTask.CompletedTask;
        }
    }
}
