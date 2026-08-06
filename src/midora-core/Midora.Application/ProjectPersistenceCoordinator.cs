using Midora.Domain;
using Midora.Persistence;

namespace Midora.Application;

public enum ProjectPersistenceUnavailability
{
    DamagedProjectObjects
}

public sealed class ProjectPersistenceUnavailableException : InvalidOperationException
{
    public ProjectPersistenceUnavailableException(
        ProjectPersistenceUnavailability unavailability,
        string message)
        : base(message)
    {
        Unavailability = unavailability;
    }

    public ProjectPersistenceUnavailability Unavailability { get; }
}

public sealed class ProjectPersistenceCoordinator
{
    private readonly object _sync = new();
    private readonly ProjectDocumentSession _document;
    private readonly MidoraProjectPackageV1 _packages;
    private readonly Func<EmbeddedSoundFontResourceV1?> _embeddedSoundFontResourceProvider;
    private string? _currentProjectPath;
    private MidoraProjectFileInformationV1? _fileInformation;
    private bool _operationActive;

    public ProjectPersistenceCoordinator(
        ProjectDocumentSession document,
        MidoraProjectPackageV1 packages,
        string? currentProjectPath = null,
        MidoraProjectFileInformationV1? fileInformation = null,
        Func<EmbeddedSoundFontResourceV1?>? embeddedSoundFontResourceProvider = null)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _packages = packages ?? throw new ArgumentNullException(nameof(packages));
        bool hasPath = currentProjectPath is not null;
        if (hasPath != document.HasPersistentOrigin)
        {
            throw new ArgumentException(
                "The Project path must agree with the document's persistent origin.",
                nameof(currentProjectPath));
        }
        if (hasPath != (fileInformation is not null))
        {
            throw new ArgumentException(
                "Persisted Projects require file-version information and unsaved Projects cannot have it.",
                nameof(fileInformation));
        }

        _currentProjectPath = currentProjectPath is null
            ? null
            : NormalizePath(currentProjectPath, nameof(currentProjectPath));
        _fileInformation = fileInformation;
        _embeddedSoundFontResourceProvider = embeddedSoundFontResourceProvider
            ?? (static () => null);
    }

    public ProjectDocumentSession Document => _document;

    public string? CurrentProjectPath
    {
        get
        {
            lock (_sync)
            {
                return _currentProjectPath;
            }
        }
    }

    public MidoraProjectFileInformationV1? FileInformation
    {
        get
        {
            lock (_sync)
            {
                return _fileInformation;
            }
        }
    }

    public bool IsOperationActive
    {
        get
        {
            lock (_sync)
            {
                return _operationActive;
            }
        }
    }

    public bool CanSaveProject
    {
        get
        {
            MidoraProject project = _document.Project;
            if (project.DamagedEventInstruments.Count != 0
                || project.DamagedLogicalTracks.Count != 0)
            {
                return false;
            }
            if (project.SoundFont.Reference is not EmbeddedProjectSoundFontReference embedded)
            {
                return true;
            }
            EmbeddedSoundFontResourceV1? resource = _embeddedSoundFontResourceProvider();
            return resource is not null
                && resource.IsAvailable
                && resource.Reference == embedded
                && resource.ResolvedAbsolutePath is not null
                && File.Exists(resource.ResolvedAbsolutePath);
        }
    }

    public async Task<MidoraProjectSaveResultV1> SaveProjectAsync(
        string? firstSaveTargetPath = null,
        bool overwriteAuthorized = false,
        CancellationToken cancellationToken = default)
    {
        OperationSnapshot operation = BeginOperation();
        try
        {
            RequireNoDamagedProjectObjects();
            string targetPath;
            bool effectiveOverwriteAuthorization;
            if (operation.CurrentProjectPath is null)
            {
                if (firstSaveTargetPath is null)
                {
                    throw new InvalidOperationException(
                        "An unsaved Project requires a target path for its first save.");
                }
                targetPath = NormalizePath(firstSaveTargetPath, nameof(firstSaveTargetPath));
                effectiveOverwriteAuthorization = overwriteAuthorized;
            }
            else
            {
                if (firstSaveTargetPath is not null
                    && !PathsEqual(
                        operation.CurrentProjectPath,
                        NormalizePath(firstSaveTargetPath, nameof(firstSaveTargetPath))))
                {
                    throw new InvalidOperationException(
                        "Initial release Save Project cannot change the current Project path; use Save Copy for a separate file.");
                }
                targetPath = operation.CurrentProjectPath;
                effectiveOverwriteAuthorization = true;
            }

            MidoraProjectSaveResultV1 result = await _packages.SaveProjectAsync(
                _document.Project,
                targetPath,
                operation.FileInformation,
                _document.Compilation.EditingTimeSession,
                effectiveOverwriteAuthorization,
                cancellationToken,
                _embeddedSoundFontResourceProvider()).ConfigureAwait(false);
            lock (_sync)
            {
                _currentProjectPath = result.TargetPath;
                _fileInformation = result.FileInformation;
            }
            _document.MarkSaveSucceeded();
            return result;
        }
        finally
        {
            CompleteOperation();
        }
    }

    public async Task<MidoraProjectSaveResultV1> SaveCopyAsync(
        string targetPath,
        bool overwriteAuthorized = false,
        CancellationToken cancellationToken = default)
    {
        string normalizedTarget = NormalizePath(targetPath, nameof(targetPath));
        OperationSnapshot operation = BeginOperation();
        try
        {
            RequireNoDamagedProjectObjects();
            if (operation.CurrentProjectPath is not null
                && PathsEqual(operation.CurrentProjectPath, normalizedTarget))
            {
                throw new InvalidOperationException(
                    "Save Copy cannot overwrite the current Project file; use Save Project.");
            }

            return await _packages.SaveCopyAsync(
                _document.Project,
                normalizedTarget,
                operation.FileInformation,
                _document.Compilation.EditingTimeSession,
                overwriteAuthorized,
                cancellationToken,
                _embeddedSoundFontResourceProvider()).ConfigureAwait(false);
        }
        finally
        {
            CompleteOperation();
        }
    }

    private OperationSnapshot BeginOperation()
    {
        lock (_sync)
        {
            if (_operationActive)
            {
                throw new InvalidOperationException(
                    "Another Project persistence operation is already active.");
            }
            _operationActive = true;
            return new(_currentProjectPath, _fileInformation);
        }
    }

    private void CompleteOperation()
    {
        lock (_sync)
        {
            _operationActive = false;
        }
    }

    private void RequireNoDamagedProjectObjects()
    {
        if (_document.Project.DamagedEventInstruments.Count != 0
            || _document.Project.DamagedLogicalTracks.Count != 0)
        {
            throw new ProjectPersistenceUnavailableException(
                ProjectPersistenceUnavailability.DamagedProjectObjects,
                "Projects containing damaged object placeholders cannot be saved or copied.");
        }
    }

    private static string NormalizePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Project file paths must be fully qualified.", parameterName);
        }
        return Path.GetFullPath(path);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private readonly record struct OperationSnapshot(
        string? CurrentProjectPath,
        MidoraProjectFileInformationV1? FileInformation);
}
