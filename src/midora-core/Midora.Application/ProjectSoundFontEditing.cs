using Midora.Audio;
using Midora.Compiler;
using Midora.Domain;
using Midora.Persistence;
using Midora.Playback;

namespace Midora.Application;

public enum ProjectSoundFontSelectionFailure
{
    ContentChangedDuringValidation
}

public sealed class ProjectSoundFontSelectionException : Exception
{
    public ProjectSoundFontSelectionException(
        ProjectSoundFontSelectionFailure failure,
        string message)
        : base(message)
    {
        Failure = failure;
    }

    public ProjectSoundFontSelectionFailure Failure { get; }
}

public sealed record ExternalSoundFontEditExecution(
    ProjectEditExecution ProjectEdit,
    ExternalProjectSoundFontReference Reference,
    string ResolvedAbsolutePath,
    bool UsedCaseInsensitiveFallback);

public sealed class ProjectSoundFontEditing
{
    private readonly ProjectDocumentSession _document;
    private readonly ISoundFontLoadabilityValidator _loadabilityValidator;

    public ProjectSoundFontEditing(
        ProjectDocumentSession document,
        ISoundFontLoadabilityValidator loadabilityValidator)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _loadabilityValidator = loadabilityValidator
            ?? throw new ArgumentNullException(nameof(loadabilityValidator));
    }

    public async Task<ExternalSoundFontEditExecution> SelectExternalAsync(
        string currentProjectFilePath,
        string selectedSoundFontPath,
        CancellationToken cancellationToken = default)
    {
        if (_document.Compilation.EditsLocked)
        {
            throw new InvalidOperationException(
                "The Project SoundFont cannot change while a Project edit lock is active.");
        }

        ExternalSoundFontBindingV1 binding = await SoundFontBindingV1.BindExternalAsync(
            currentProjectFilePath,
            selectedSoundFontPath,
            cancellationToken).ConfigureAwait(false);
        await _loadabilityValidator.ValidateAsync(
            binding.ResolvedAbsolutePath,
            cancellationToken).ConfigureAwait(false);

        ExternalSoundFontVerificationV1 verification = await SoundFontBindingV1.VerifyExternalAsync(
            currentProjectFilePath,
            binding.Reference,
            cancellationToken).ConfigureAwait(false);
        if (!verification.IsReadable
            || verification.HashMatches != true
            || verification.ResolvedAbsolutePath is null
            || !string.Equals(
                Path.GetFullPath(verification.ResolvedAbsolutePath),
                Path.GetFullPath(binding.ResolvedAbsolutePath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ProjectSoundFontSelectionException(
                ProjectSoundFontSelectionFailure.ContentChangedDuringValidation,
                "The selected external SoundFont changed or became unavailable during validation.");
        }

        ProjectEditExecution execution = _document.Execute(
            CreateReplaceCommand(binding.Reference, binding.ResolvedAbsolutePath));
        return new(
            execution,
            binding.Reference,
            binding.ResolvedAbsolutePath,
            binding.UsedCaseInsensitiveFallback);
    }

    public ProjectEditExecution Clear() =>
        _document.Execute(CreateReplaceCommand(reference: null, effectivePath: null));

    private IProjectEditCommand CreateReplaceCommand(
        ProjectSoundFontReference? reference,
        string? effectivePath) =>
        new ReplaceProjectSoundFontCommand(
            _document.Compilation,
            reference,
            effectivePath);

    private sealed class ReplaceProjectSoundFontCommand : IProjectEditCommand
    {
        private readonly ProjectCompilationSession _compilation;
        private readonly ProjectSoundFontReference? _newReference;
        private readonly string? _newEffectivePath;

        public ReplaceProjectSoundFontCommand(
            ProjectCompilationSession compilation,
            ProjectSoundFontReference? newReference,
            string? newEffectivePath)
        {
            _compilation = compilation;
            _newReference = newReference;
            _newEffectivePath = newEffectivePath is null
                ? null
                : Path.GetFullPath(newEffectivePath);
        }

        public string Name => _newReference is null
            ? "Clear Project SoundFont"
            : "Select External Project SoundFont";

        public IPreparedProjectEdit Prepare(MidoraProject project)
        {
            if (!ReferenceEquals(project, _compilation.Project))
            {
                throw new InvalidOperationException(
                    "The SoundFont edit belongs to a different Project compilation session.");
            }

            ProjectSoundFontReference? oldReference = project.SoundFont.Reference;
            string? oldEffectivePath = _compilation.EffectiveSoundFontPath;
            bool hasChanges = oldReference != _newReference
                || !string.Equals(
                    oldEffectivePath,
                    _newEffectivePath,
                    StringComparison.OrdinalIgnoreCase);
            return new Prepared(
                _compilation,
                oldReference,
                oldEffectivePath,
                _newReference,
                _newEffectivePath,
                hasChanges);
        }
    }

    private sealed class Prepared(
        ProjectCompilationSession compilation,
        ProjectSoundFontReference? oldReference,
        string? oldEffectivePath,
        ProjectSoundFontReference? newReference,
        string? newEffectivePath,
        bool hasChanges) : IPreparedProjectEdit
    {
        public bool HasChanges { get; } = hasChanges;
        public ProjectChangeSet Changes { get; } = new();

        public void Apply(MidoraProject project) =>
            Replace(project, newReference, newEffectivePath);

        public void Undo(MidoraProject project) =>
            Replace(project, oldReference, oldEffectivePath);

        private void Replace(
            MidoraProject project,
            ProjectSoundFontReference? reference,
            string? effectivePath)
        {
            project.SoundFont.SetReference(reference);
            compilation.SetEffectiveSoundFontPath(effectivePath);
        }
    }
}
