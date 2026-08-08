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

public sealed record EmbeddedSoundFontEditExecution(
    ProjectEditExecution ProjectEdit,
    EmbeddedProjectSoundFontReference Reference,
    EmbeddedSoundFontResourceV1 Resource);

public sealed class ProjectSoundFontResourceSession : IDisposable, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly HashSet<EmbeddedSoundFontResourceV1> _owned =
        new(ReferenceEqualityComparer.Instance);
    private EmbeddedSoundFontResourceV1? _current;
    private bool _disposed;

    public ProjectSoundFontResourceSession(EmbeddedSoundFontResourceV1? initialResource = null) =>
        _current = initialResource;

    public EmbeddedSoundFontResourceV1? CurrentEmbeddedResource
    {
        get
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _current;
            }
        }
    }

    internal void Adopt(EmbeddedSoundFontResourceV1 resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _owned.Add(resource);
        }
    }

    internal void SetCurrent(EmbeddedSoundFontResourceV1? resource)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _current = resource;
        }
    }

    public void Dispose()
    {
        EmbeddedSoundFontResourceV1[] owned;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _current = null;
            owned = _owned.ToArray();
            _owned.Clear();
        }
        foreach (EmbeddedSoundFontResourceV1 resource in owned)
        {
            resource.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        EmbeddedSoundFontResourceV1[] owned;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _current = null;
            owned = _owned.ToArray();
            _owned.Clear();
        }
        foreach (EmbeddedSoundFontResourceV1 resource in owned)
        {
            await resource.DisposeAsync().ConfigureAwait(false);
        }
    }
}

public sealed class ProjectSoundFontEditing : IDisposable, IAsyncDisposable
{
    private readonly ProjectDocumentSession _document;
    private readonly ISoundFontLoadabilityValidator _loadabilityValidator;
    private readonly ProjectSoundFontResourceSession _resources;
    private readonly bool _ownsResources;

    public ProjectSoundFontEditing(
        ProjectDocumentSession document,
        ISoundFontLoadabilityValidator loadabilityValidator,
        ProjectSoundFontResourceSession? resources = null)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _loadabilityValidator = loadabilityValidator
            ?? throw new ArgumentNullException(nameof(loadabilityValidator));
        _resources = resources ?? new ProjectSoundFontResourceSession();
        _ownsResources = resources is null;
    }

    public EmbeddedSoundFontResourceV1? CurrentEmbeddedSoundFontResource =>
        _resources.CurrentEmbeddedResource;

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

    public async Task<EmbeddedSoundFontEditExecution> SelectEmbeddedAsync(
        string selectedSoundFontPath,
        CancellationToken cancellationToken = default)
    {
        if (_document.Compilation.EditsLocked)
        {
            throw new InvalidOperationException(
                "The Project SoundFont cannot change while a Project edit lock is active.");
        }

        await using EmbeddedSoundFontImportCandidateV1 candidate =
            await SoundFontBindingV1.StageEmbeddedAsync(
                selectedSoundFontPath,
                cancellationToken).ConfigureAwait(false);
        await _loadabilityValidator.ValidateAsync(
            candidate.ResolvedAbsolutePath,
            cancellationToken).ConfigureAwait(false);
        SoundFontContentIdentityV1 afterValidation =
            await SoundFontBindingV1.ReadContentIdentityAsync(
                candidate.ResolvedAbsolutePath,
                cancellationToken).ConfigureAwait(false);
        if (!string.Equals(afterValidation.Sha256, candidate.Sha256, StringComparison.Ordinal)
            || afterValidation.FileSizeBytes != candidate.FileSizeBytes)
        {
            throw new ProjectSoundFontSelectionException(
                ProjectSoundFontSelectionFailure.ContentChangedDuringValidation,
                "The staged Embedded SoundFont changed during validation.");
        }

        SelectEmbeddedProjectSoundFontCommand command = new(
            _document.Compilation,
            _resources,
            candidate);
        ProjectEditExecution execution = _document.Execute(command);
        EmbeddedSoundFontResourceV1 resource = command.CreatedResource
            ?? throw new InvalidOperationException(
                "The Embedded SoundFont selection did not create a resource lease.");
        return new(execution, resource.Reference, resource);
    }

    public ProjectEditExecution Clear() =>
        _document.Execute(CreateReplaceCommand(reference: null, effectivePath: null));

    private IProjectEditCommand CreateReplaceCommand(
        ProjectSoundFontReference? reference,
        string? effectivePath) =>
        new ReplaceProjectSoundFontCommand(
            _document.Compilation,
            _resources,
            reference,
            effectivePath);

    public void Dispose()
    {
        if (_ownsResources)
        {
            _resources.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsResources)
        {
            await _resources.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class ReplaceProjectSoundFontCommand : IProjectEditCommand
    {
        private readonly ProjectCompilationSession _compilation;
        private readonly ProjectSoundFontResourceSession _resources;
        private readonly ProjectSoundFontReference? _newReference;
        private readonly string? _newEffectivePath;

        public ReplaceProjectSoundFontCommand(
            ProjectCompilationSession compilation,
            ProjectSoundFontResourceSession resources,
            ProjectSoundFontReference? newReference,
            string? newEffectivePath)
        {
            _compilation = compilation;
            _resources = resources;
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
            EmbeddedSoundFontResourceV1? oldEmbeddedResource =
                _resources.CurrentEmbeddedResource;
            ValidateEmbeddedResourceMatch(oldReference, oldEmbeddedResource);
            bool hasChanges = oldReference != _newReference
                || !string.Equals(
                    oldEffectivePath,
                    _newEffectivePath,
                    StringComparison.OrdinalIgnoreCase);
            return new Prepared(
                _compilation,
                _resources,
                oldReference,
                oldEffectivePath,
                oldEmbeddedResource,
                _newReference,
                _newEffectivePath,
                hasChanges);
        }
    }

    private sealed class Prepared(
        ProjectCompilationSession compilation,
        ProjectSoundFontResourceSession resources,
        ProjectSoundFontReference? oldReference,
        string? oldEffectivePath,
        EmbeddedSoundFontResourceV1? oldEmbeddedResource,
        ProjectSoundFontReference? newReference,
        string? newEffectivePath,
        bool hasChanges) : IPreparedProjectEdit
    {
        public bool HasChanges { get; } = hasChanges;
        public ProjectChangeSet Changes { get; } = new();

        public void Apply(MidoraProject project) =>
            Replace(project, newReference, newEffectivePath, embeddedResource: null);

        public void Undo(MidoraProject project) =>
            Replace(project, oldReference, oldEffectivePath, oldEmbeddedResource);

        private void Replace(
            MidoraProject project,
            ProjectSoundFontReference? reference,
            string? effectivePath,
            EmbeddedSoundFontResourceV1? embeddedResource)
        {
            project.SoundFont.SetReference(reference);
            resources.SetCurrent(embeddedResource);
            compilation.SetEffectiveSoundFontPath(effectivePath);
        }
    }

    private sealed class SelectEmbeddedProjectSoundFontCommand(
        ProjectCompilationSession compilation,
        ProjectSoundFontResourceSession resources,
        EmbeddedSoundFontImportCandidateV1 candidate) : IProjectEditCommand
    {
        public string Name => "Select Embedded Project SoundFont";
        public EmbeddedSoundFontResourceV1? CreatedResource { get; private set; }

        public IPreparedProjectEdit Prepare(MidoraProject project)
        {
            if (!ReferenceEquals(project, compilation.Project))
            {
                throw new InvalidOperationException(
                    "The SoundFont edit belongs to a different Project compilation session.");
            }
            ProjectSoundFontReference? oldReference = project.SoundFont.Reference;
            string? oldEffectivePath = compilation.EffectiveSoundFontPath;
            EmbeddedSoundFontResourceV1? oldResource = resources.CurrentEmbeddedResource;
            ValidateEmbeddedResourceMatch(oldReference, oldResource);
            return new PreparedEmbedded(
                compilation,
                resources,
                candidate,
                oldReference,
                oldEffectivePath,
                oldResource,
                resource => CreatedResource = resource);
        }
    }

    private sealed class PreparedEmbedded(
        ProjectCompilationSession compilation,
        ProjectSoundFontResourceSession resources,
        EmbeddedSoundFontImportCandidateV1 candidate,
        ProjectSoundFontReference? oldReference,
        string? oldEffectivePath,
        EmbeddedSoundFontResourceV1? oldResource,
        Action<EmbeddedSoundFontResourceV1> onCreated) : IPreparedProjectEdit
    {
        private EmbeddedSoundFontResourceV1? _newResource;

        public bool HasChanges => true;
        public ProjectChangeSet Changes { get; } = new();

        public void Apply(MidoraProject project)
        {
            if (_newResource is null)
            {
                _newResource = candidate.Bind(project);
                resources.Adopt(_newResource);
                onCreated(_newResource);
            }
            project.SoundFont.SetReference(_newResource.Reference);
            resources.SetCurrent(_newResource);
            compilation.SetEffectiveSoundFontPath(_newResource.ResolvedAbsolutePath);
        }

        public void Undo(MidoraProject project)
        {
            project.SoundFont.SetReference(oldReference);
            resources.SetCurrent(oldResource);
            compilation.SetEffectiveSoundFontPath(oldEffectivePath);
        }
    }

    private static void ValidateEmbeddedResourceMatch(
        ProjectSoundFontReference? reference,
        EmbeddedSoundFontResourceV1? resource)
    {
        if (reference is EmbeddedProjectSoundFontReference embedded
            && (resource is null || resource.Reference != embedded))
        {
            throw new InvalidOperationException(
                "The current Embedded SoundFont reference has no matching runtime resource lease.");
        }
        if (reference is not EmbeddedProjectSoundFontReference && resource is not null)
        {
            throw new InvalidOperationException(
                "An Embedded SoundFont resource lease is active without a matching Project reference.");
        }
    }
}
