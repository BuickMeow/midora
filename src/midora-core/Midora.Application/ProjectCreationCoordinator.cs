using Midora.Domain;
using Midora.Persistence;

namespace Midora.Application;

public enum NewProjectPersistenceMode
{
    CreateUnsaved,
    CreateAndSave
}

public sealed record NewProjectCreationRequest
{
    public int TicksPerQuarterNote { get; init; } = 192;
    public string ProjectName { get; init; } = string.Empty;
    public string ProjectVersion { get; init; } = string.Empty;
    public string AuthorOrTeam { get; init; } = string.Empty;
    public string OriginalWork { get; init; } = string.Empty;
    public string Copyright { get; init; } = string.Empty;
    public NewProjectPersistenceMode PersistenceMode { get; init; } =
        NewProjectPersistenceMode.CreateUnsaved;
    public string? TargetPath { get; init; }
    public bool OverwriteAuthorized { get; init; }
}

public sealed class NewProjectCreationResult : IDisposable, IAsyncDisposable
{
    internal NewProjectCreationResult(
        MidoraProject project,
        ProjectDocumentOrigin origin,
        string? currentProjectPath,
        MidoraProjectFileInformationV1? fileInformation,
        IReadOnlyList<MidoraPackageDiagnosticV1> diagnostics)
    {
        Project = project;
        Origin = origin;
        CurrentProjectPath = currentProjectPath;
        FileInformation = fileInformation;
        Diagnostics = diagnostics;
    }

    public MidoraProject Project { get; }
    public ProjectDocumentOrigin Origin { get; }
    public string? CurrentProjectPath { get; }
    public MidoraProjectFileInformationV1? FileInformation { get; }
    public IReadOnlyList<MidoraPackageDiagnosticV1> Diagnostics { get; }

    public void Dispose() => Project.Dispose();

    public ValueTask DisposeAsync()
    {
        Project.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class ProjectCreationCoordinator
{
    private readonly MidoraProjectPackageV1 _packages;
    private readonly TimeProvider _timeProvider;

    public ProjectCreationCoordinator(
        MidoraProjectPackageV1 packages,
        TimeProvider? timeProvider = null)
    {
        _packages = packages ?? throw new ArgumentNullException(nameof(packages));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<NewProjectCreationResult> CreateAsync(
        NewProjectCreationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatedRequest input = ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();

        MidoraProject project = new(input.TicksPerQuarterNote, _timeProvider.GetUtcNow());
        ApplyMetadata(project.Metadata, input);

        if (input.PersistenceMode == NewProjectPersistenceMode.CreateUnsaved)
        {
            return new(
                project,
                ProjectDocumentOrigin.Unsaved,
                currentProjectPath: null,
                fileInformation: null,
                Array.Empty<MidoraPackageDiagnosticV1>());
        }

        try
        {
            MidoraProjectSaveResultV1 save = await _packages.SaveProjectAsync(
                project,
                input.TargetPath!,
                overwriteAuthorized: input.OverwriteAuthorized,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new(
                project,
                ProjectDocumentOrigin.Persisted,
                save.TargetPath,
                save.FileInformation,
                save.Diagnostics);
        }
        catch
        {
            project.Dispose();
            throw;
        }
    }

    public NewProjectCreationResult AdoptImportedProject(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new(
            project,
            ProjectDocumentOrigin.Unsaved,
            currentProjectPath: null,
            fileInformation: null,
            Array.Empty<MidoraPackageDiagnosticV1>());
    }

    private static ValidatedRequest ValidateRequest(NewProjectCreationRequest request)
    {
        if (request.TicksPerQuarterNote <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.TicksPerQuarterNote));
        }
        if (!Enum.IsDefined(request.PersistenceMode))
        {
            throw new ArgumentOutOfRangeException(nameof(request.PersistenceMode));
        }

        string? targetPath = request.TargetPath;
        if (request.PersistenceMode == NewProjectPersistenceMode.CreateAndSave)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
            if (!Path.IsPathFullyQualified(targetPath))
            {
                throw new ArgumentException(
                    "The initial Project path must be fully qualified.",
                    nameof(request.TargetPath));
            }
            targetPath = Path.GetFullPath(targetPath);
        }
        else if (targetPath is not null)
        {
            throw new ArgumentException(
                "An unsaved Project cannot have an initial Project path.",
                nameof(request.TargetPath));
        }

        return new(
            request.TicksPerQuarterNote,
            NormalizeText(request.ProjectName),
            NormalizeText(request.ProjectVersion),
            NormalizeText(request.AuthorOrTeam),
            NormalizeText(request.OriginalWork),
            NormalizeText(request.Copyright),
            request.PersistenceMode,
            targetPath,
            request.OverwriteAuthorized);
    }

    private static void ApplyMetadata(ProjectMetadata metadata, ValidatedRequest input)
    {
        metadata.ProjectName = input.ProjectName;
        metadata.ProjectVersion = input.ProjectVersion;
        metadata.AuthorOrTeam = input.AuthorOrTeam;
        metadata.OriginalWork = input.OriginalWork;
        metadata.Copyright = input.Copyright;
    }

    private static string NormalizeText(string? value) => value?.Trim() ?? string.Empty;

    private sealed record ValidatedRequest(
        int TicksPerQuarterNote,
        string ProjectName,
        string ProjectVersion,
        string AuthorOrTeam,
        string OriginalWork,
        string Copyright,
        NewProjectPersistenceMode PersistenceMode,
        string? TargetPath,
        bool OverwriteAuthorized);
}
