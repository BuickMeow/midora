using Midora.Audio;
using Midora.Domain;
using Midora.Persistence;

namespace Midora.Application;

public enum NewProjectPersistenceMode
{
    CreateUnsaved,
    CreateAndSave
}

public enum NewProjectSoundFontMode
{
    None,
    Embedded,
    ExternalRelative
}

public sealed record NewProjectSoundFontSelection
{
    public NewProjectSoundFontSelection(
        NewProjectSoundFontMode mode,
        string? selectedPath = null)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        if (mode == NewProjectSoundFontMode.None)
        {
            if (selectedPath is not null)
            {
                throw new ArgumentException(
                    "A Project without a SoundFont cannot have a selected SoundFont path.",
                    nameof(selectedPath));
            }
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
            if (!Path.IsPathFullyQualified(selectedPath))
            {
                throw new ArgumentException(
                    "Selected SoundFont paths must be fully qualified.",
                    nameof(selectedPath));
            }
        }

        Mode = mode;
        SelectedPath = selectedPath is null ? null : Path.GetFullPath(selectedPath);
    }

    public static NewProjectSoundFontSelection NoSoundFont { get; } =
        new(NewProjectSoundFontMode.None);

    public NewProjectSoundFontMode Mode { get; }
    public string? SelectedPath { get; }
}

public sealed record NewProjectCreationRequest
{
    public int TicksPerQuarterNote { get; init; } = 192;
    public string ProjectName { get; init; } = string.Empty;
    public string ProjectVersion { get; init; } = string.Empty;
    public string AuthorOrTeam { get; init; } = string.Empty;
    public string OriginalWork { get; init; } = string.Empty;
    public string Copyright { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
    public NewProjectPersistenceMode PersistenceMode { get; init; } =
        NewProjectPersistenceMode.CreateUnsaved;
    public string? TargetPath { get; init; }
    public bool OverwriteAuthorized { get; init; }
    public NewProjectSoundFontSelection SoundFont { get; init; } =
        NewProjectSoundFontSelection.NoSoundFont;
}

public sealed class NewProjectCreationResult : IDisposable, IAsyncDisposable
{
    internal NewProjectCreationResult(
        MidoraProject project,
        ProjectDocumentOrigin origin,
        string? currentProjectPath,
        MidoraProjectFileInformationV1? fileInformation,
        string? effectiveSoundFontPath,
        bool usedCaseInsensitiveSoundFontPathFallback,
        EmbeddedSoundFontResourceV1? embeddedSoundFontResource,
        IReadOnlyList<MidoraPackageDiagnosticV1> diagnostics)
    {
        Project = project;
        Origin = origin;
        CurrentProjectPath = currentProjectPath;
        FileInformation = fileInformation;
        EffectiveSoundFontPath = effectiveSoundFontPath;
        UsedCaseInsensitiveSoundFontPathFallback = usedCaseInsensitiveSoundFontPathFallback;
        EmbeddedSoundFontResource = embeddedSoundFontResource;
        Diagnostics = diagnostics;
    }

    public MidoraProject Project { get; }
    public ProjectDocumentOrigin Origin { get; }
    public string? CurrentProjectPath { get; }
    public MidoraProjectFileInformationV1? FileInformation { get; }
    public string? EffectiveSoundFontPath { get; }
    public bool UsedCaseInsensitiveSoundFontPathFallback { get; }
    public EmbeddedSoundFontResourceV1? EmbeddedSoundFontResource { get; }
    public IReadOnlyList<MidoraPackageDiagnosticV1> Diagnostics { get; }

    public void Dispose() => EmbeddedSoundFontResource?.Dispose();

    public async ValueTask DisposeAsync()
    {
        if (EmbeddedSoundFontResource is not null)
        {
            await EmbeddedSoundFontResource.DisposeAsync().ConfigureAwait(false);
        }
    }
}

public sealed class ProjectCreationCoordinator
{
    private readonly MidoraProjectPackageV1 _packages;
    private readonly ISoundFontLoadabilityValidator _soundFontValidator;
    private readonly TimeProvider _timeProvider;

    public ProjectCreationCoordinator(
        MidoraProjectPackageV1 packages,
        ISoundFontLoadabilityValidator soundFontValidator,
        TimeProvider? timeProvider = null)
    {
        _packages = packages ?? throw new ArgumentNullException(nameof(packages));
        _soundFontValidator = soundFontValidator
            ?? throw new ArgumentNullException(nameof(soundFontValidator));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<NewProjectCreationResult> CreateAsync(
        NewProjectCreationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatedRequest input = ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();

        MidoraProject project = new(
            input.TicksPerQuarterNote,
            _timeProvider.GetUtcNow());
        ApplyMetadata(project.Metadata, input);

        EmbeddedSoundFontResourceV1? embeddedResource = null;
        string? effectiveSoundFontPath = null;
        bool usedCaseInsensitiveSoundFontPathFallback = false;
        try
        {
            switch (input.SoundFont.Mode)
            {
                case NewProjectSoundFontMode.None:
                    break;
                case NewProjectSoundFontMode.Embedded:
                    embeddedResource = await SoundFontBindingV1.BindEmbeddedAsync(
                        project,
                        input.SoundFont.SelectedPath!,
                        cancellationToken).ConfigureAwait(false);
                    effectiveSoundFontPath = embeddedResource.ResolvedAbsolutePath
                        ?? throw new InvalidOperationException(
                            "A newly imported Embedded SoundFont has no runtime path.");
                    await _soundFontValidator.ValidateAsync(
                        effectiveSoundFontPath,
                        cancellationToken).ConfigureAwait(false);
                    break;
                case NewProjectSoundFontMode.ExternalRelative:
                    ExternalSoundFontBindingV1 binding =
                        await SoundFontBindingV1.BindExternalAsync(
                            input.TargetPath!,
                            input.SoundFont.SelectedPath!,
                            cancellationToken).ConfigureAwait(false);
                    await _soundFontValidator.ValidateAsync(
                        binding.ResolvedAbsolutePath,
                        cancellationToken).ConfigureAwait(false);
                    ExternalSoundFontVerificationV1 verification =
                        await SoundFontBindingV1.VerifyExternalAsync(
                            input.TargetPath!,
                            binding.Reference,
                            cancellationToken).ConfigureAwait(false);
                    if (!verification.IsReadable
                        || verification.HashMatches != true
                        || verification.ResolvedAbsolutePath is null
                        || !PathsEqual(
                            verification.ResolvedAbsolutePath,
                            binding.ResolvedAbsolutePath))
                    {
                        throw new ProjectSoundFontSelectionException(
                            ProjectSoundFontSelectionFailure.ContentChangedDuringValidation,
                            "The selected external SoundFont changed or became unavailable during validation.");
                    }
                    project.SoundFont.SetReference(binding.Reference);
                    effectiveSoundFontPath = binding.ResolvedAbsolutePath;
                    usedCaseInsensitiveSoundFontPathFallback =
                        binding.UsedCaseInsensitiveFallback;
                    break;
                default:
                    throw new InvalidOperationException("Unknown new Project SoundFont mode.");
            }

            if (input.PersistenceMode == NewProjectPersistenceMode.CreateUnsaved)
            {
                return new(
                    project,
                    ProjectDocumentOrigin.Unsaved,
                    currentProjectPath: null,
                    fileInformation: null,
                    effectiveSoundFontPath,
                    usedCaseInsensitiveSoundFontPathFallback,
                    embeddedResource,
                    Array.Empty<MidoraPackageDiagnosticV1>());
            }

            MidoraProjectSaveResultV1 save = await _packages.SaveProjectAsync(
                project,
                input.TargetPath!,
                overwriteAuthorized: input.OverwriteAuthorized,
                cancellationToken: cancellationToken,
                embeddedSoundFontResource: embeddedResource).ConfigureAwait(false);
            return new(
                project,
                ProjectDocumentOrigin.Persisted,
                save.TargetPath,
                save.FileInformation,
                effectiveSoundFontPath,
                usedCaseInsensitiveSoundFontPathFallback,
                embeddedResource,
                save.Diagnostics);
        }
        catch
        {
            if (embeddedResource is not null)
            {
                await embeddedResource.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
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
        ArgumentNullException.ThrowIfNull(request.SoundFont);

        string? targetPath;
        switch (request.PersistenceMode)
        {
            case NewProjectPersistenceMode.CreateUnsaved:
                if (request.TargetPath is not null || request.OverwriteAuthorized)
                {
                    throw new ArgumentException(
                        "Create Unsaved cannot have a target path or overwrite authorization.",
                        nameof(request));
                }
                if (request.SoundFont.Mode == NewProjectSoundFontMode.ExternalRelative)
                {
                    throw new ArgumentException(
                        "An external relative SoundFont requires Create and Save so its Project-relative path is defined.",
                        nameof(request));
                }
                targetPath = null;
                break;
            case NewProjectPersistenceMode.CreateAndSave:
                ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetPath);
                if (!Path.IsPathFullyQualified(request.TargetPath))
                {
                    throw new ArgumentException(
                        "Create and Save requires a fully qualified target path.",
                        nameof(request));
                }
                targetPath = Path.GetFullPath(request.TargetPath);
                break;
            default:
                throw new InvalidOperationException("Unknown new Project persistence mode.");
        }

        return new(
            request.TicksPerQuarterNote,
            ProjectTextRules.ValidateShortTextContent(
                request.ProjectName,
                nameof(request.ProjectName)),
            ProjectTextRules.ValidateShortTextContent(
                request.ProjectVersion,
                nameof(request.ProjectVersion)),
            ProjectTextRules.ValidateMetadataText(
                request.AuthorOrTeam,
                nameof(request.AuthorOrTeam)),
            ProjectTextRules.ValidateMetadataText(
                request.OriginalWork,
                nameof(request.OriginalWork)),
            ProjectTextRules.ValidateMetadataText(
                request.Copyright,
                nameof(request.Copyright)),
            ProjectTextRules.ValidateDescription(
                request.Notes,
                nameof(request.Notes))
                ?? throw new ArgumentNullException(nameof(request.Notes)),
            request.PersistenceMode,
            targetPath,
            request.OverwriteAuthorized,
            request.SoundFont);
    }

    private static void ApplyMetadata(ProjectMetadata metadata, ValidatedRequest request)
    {
        metadata.ProjectName = request.ProjectName;
        metadata.ProjectVersion = request.ProjectVersion;
        metadata.AuthorOrTeam = request.AuthorOrTeam;
        metadata.OriginalWork = request.OriginalWork;
        metadata.Copyright = request.Copyright;
        metadata.Notes = request.Notes;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    private sealed record ValidatedRequest(
        int TicksPerQuarterNote,
        string ProjectName,
        string ProjectVersion,
        string AuthorOrTeam,
        string OriginalWork,
        string Copyright,
        string Notes,
        NewProjectPersistenceMode PersistenceMode,
        string? TargetPath,
        bool OverwriteAuthorized,
        NewProjectSoundFontSelection SoundFont);
}
