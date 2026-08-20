using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Midora.Domain;

namespace Midora.Persistence;

public enum MidoraPackageDiagnosticSeverityV1
{
    Error,
    Warning,
    Information
}

public enum MidoraPackageDiagnosticCategoryV1
{
    FileFormat,
    FileDamage,
    VersionCompatibility,
    Resource,
    SaveTransaction
}

public enum MidoraPackageStageV1
{
    Preflight,
    Staging,
    Container,
    Manifest,
    VersionPreflight,
    HashValidation,
    Structure,
    Serialization,
    SelfValidation,
    Backup,
    Publish,
    Cleanup
}

public sealed record MidoraPackageDiagnosticV1(
    MidoraPackageDiagnosticSeverityV1 Severity,
    MidoraPackageDiagnosticCategoryV1 Category,
    string Code,
    string Message,
    string? PackagePath = null);

public sealed record MidoraProjectFileInformationV1(
    string CreatedWithSoftwareVersion,
    string LastSavedWithSoftwareVersion);

public sealed record MidoraProjectOpenResultV1(
    MidoraProject Project,
    MidoraProjectFileInformationV1 FileInformation,
    bool IsModified,
    IReadOnlyList<MidoraPackageDiagnosticV1> Diagnostics) : IDisposable, IAsyncDisposable
{
    public EmbeddedSoundFontResourceV1? EmbeddedSoundFontResource { get; init; }

    public void Dispose()
    {
        EmbeddedSoundFontResource?.Dispose();
        Project.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (EmbeddedSoundFontResource is not null)
        {
            await EmbeddedSoundFontResource.DisposeAsync().ConfigureAwait(false);
        }
        Project.Dispose();
    }
}

public sealed record MidoraProjectSaveResultV1(
    string TargetPath,
    MidoraProjectFileInformationV1 FileInformation,
    IReadOnlyList<MidoraPackageDiagnosticV1> Diagnostics);

public class MidoraPackageExceptionV1 : IOException
{
    public MidoraPackageExceptionV1(
        MidoraPackageStageV1 stage,
        string message,
        string? targetPath = null,
        string? packagePath = null,
        string? backupPath = null,
        string? temporaryPath = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Stage = stage;
        TargetPath = targetPath;
        PackagePath = packagePath;
        BackupPath = backupPath;
        TemporaryPath = temporaryPath;
    }

    public MidoraPackageStageV1 Stage { get; }
    public string? TargetPath { get; }
    public string? PackagePath { get; }
    public string? BackupPath { get; }
    public string? TemporaryPath { get; }
}

public enum EmbeddedSoundFontRepairActionV1
{
    ReplaceOrRebind,
    ClearReference
}

public sealed class MidoraEmbeddedSoundFontRepairRequiredExceptionV1 : MidoraPackageExceptionV1
{
    private static readonly IReadOnlyList<EmbeddedSoundFontRepairActionV1> AllowedActions =
        Array.AsReadOnly<EmbeddedSoundFontRepairActionV1>(
        [
            EmbeddedSoundFontRepairActionV1.ReplaceOrRebind,
            EmbeddedSoundFontRepairActionV1.ClearReference
        ]);

    internal MidoraEmbeddedSoundFontRepairRequiredExceptionV1(
        MidoraPackageStageV1 stage,
        string targetPath,
        EmbeddedProjectSoundFontReference reference,
        EmbeddedSoundFontResourceStatusV1 resourceStatus,
        string? actualSha256 = null,
        long? actualFileSizeBytes = null,
        Exception? innerException = null)
        : base(
            stage,
            "The Embedded SoundFont must be explicitly replaced, rebound, or cleared before this Project can be saved.",
            targetPath,
            MidoraPackagePathsV1.EmbeddedSoundFont(reference.ResourceId),
            innerException: innerException)
    {
        Reference = reference;
        ResourceStatus = resourceStatus;
        ActualSha256 = actualSha256;
        ActualFileSizeBytes = actualFileSizeBytes;
    }

    public EmbeddedProjectSoundFontReference Reference { get; }
    public EmbeddedSoundFontResourceStatusV1 ResourceStatus { get; }
    public string? ActualSha256 { get; }
    public long? ActualFileSizeBytes { get; }
    public IReadOnlyList<EmbeddedSoundFontRepairActionV1> RepairActions => AllowedActions;
}

public sealed class MidoraPackageVersionCompatibilityExceptionV1 : MidoraPackageExceptionV1
{
    internal MidoraPackageVersionCompatibilityExceptionV1(
        string targetPath,
        ManifestVersionHeaderV1 header)
        : base(
            MidoraPackageStageV1.VersionPreflight,
            "The Midora package requires a newer file-format or manifest-schema reader.",
            targetPath,
            MidoraPackagePathsV1.Manifest)
    {
        FileFormatVersion = header.FileFormatVersion;
        MinimumReadableVersion = header.MinimumReadableVersion;
        ManifestSchemaVersion = header.ManifestSchemaVersion;
    }

    public int FileFormatVersion { get; }
    public int MinimumReadableVersion { get; }
    public int ManifestSchemaVersion { get; }
    public int SupportedFileFormatVersion => PersistenceContractV1.FileFormatVersion;
    public int SupportedManifestSchemaVersion => PersistenceContractV1.SchemaVersion;
}

public sealed class MidoraProjectPackageV1
{
    private static readonly DateTimeOffset CanonicalZipTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _softwareVersion;
    private readonly TimeProvider _timeProvider;
    private readonly IMidoraPackageFaultInjectorV1 _faultInjector;

    public MidoraProjectPackageV1(string softwareVersion, TimeProvider? timeProvider = null)
        : this(softwareVersion, timeProvider, NoOpMidoraPackageFaultInjectorV1.Instance)
    {
    }

    internal MidoraProjectPackageV1(
        string softwareVersion,
        TimeProvider? timeProvider,
        IMidoraPackageFaultInjectorV1 faultInjector)
    {
        PersistenceValueValidationV1.ValidateShortText(
            softwareVersion, nameof(softwareVersion), allowEmpty: false);
        _softwareVersion = softwareVersion;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _faultInjector = faultInjector ?? throw new ArgumentNullException(nameof(faultInjector));
    }

    public async Task<MidoraProjectOpenResultV1> OpenAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        string path = NormalizeFilePath(packagePath, nameof(packagePath));
        if (!File.Exists(path))
        {
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.Container,
                "The Midora package does not exist.",
                targetPath: path);
        }

        try
        {
            _faultInjector.ThrowIfRequested(MidoraPackageFaultPointV1.BeforeContainerOpen, path);
            return await OpenCoreAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (MidoraPackageExceptionV1)
        {
            throw;
        }
        catch (InvalidDataException exception)
        {
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.Structure,
                "The Midora package contains invalid v1 Project data.",
                targetPath: path,
                innerException: exception);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.Container,
                "The Midora package could not be read.",
                targetPath: path,
                innerException: exception);
        }
    }

    public Task<MidoraProjectSaveResultV1> SaveProjectAsync(
        MidoraProject project,
        string targetPath,
        MidoraProjectFileInformationV1? fileInformation = null,
        ProjectEditingTimeSession? editingTimeSession = null,
        bool overwriteAuthorized = false,
        CancellationToken cancellationToken = default,
        EmbeddedSoundFontResourceV1? embeddedSoundFontResource = null) =>
        SaveCoreAsync(
            project,
            targetPath,
            fileInformation,
            editingTimeSession,
            overwriteAuthorized,
            updateCurrentProject: true,
            embeddedSoundFontResource,
            cancellationToken);

    public Task<MidoraProjectSaveResultV1> SaveCopyAsync(
        MidoraProject project,
        string targetPath,
        MidoraProjectFileInformationV1? fileInformation = null,
        ProjectEditingTimeSession? editingTimeSession = null,
        bool overwriteAuthorized = false,
        CancellationToken cancellationToken = default,
        EmbeddedSoundFontResourceV1? embeddedSoundFontResource = null) =>
        SaveCoreAsync(
            project,
            targetPath,
            fileInformation,
            editingTimeSession,
            overwriteAuthorized,
            updateCurrentProject: false,
            embeddedSoundFontResource,
            cancellationToken);

    private async Task<MidoraProjectSaveResultV1> SaveCoreAsync(
        MidoraProject project,
        string targetPath,
        MidoraProjectFileInformationV1? fileInformation,
        ProjectEditingTimeSession? editingTimeSession,
        bool overwriteAuthorized,
        bool updateCurrentProject,
        EmbeddedSoundFontResourceV1? embeddedSoundFontResource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        string target = NormalizeFilePath(targetPath, nameof(targetPath));
        string? directory = Path.GetDirectoryName(target);
        if (directory is null || !Directory.Exists(directory))
        {
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.Preflight,
                "The target directory does not exist.",
                targetPath: target);
        }
        bool targetExisted = File.Exists(target);
        if (targetExisted && !overwriteAuthorized)
        {
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.Preflight,
                "The target exists but overwrite was not explicitly authorized.",
                targetPath: target);
        }
        RequireEmbeddedSoundFontReadyForSave(
            project,
            embeddedSoundFontResource,
            target);

        DateTimeOffset savedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        _ = editingTimeSession?.SnapshotTotalEditingTimeMilliseconds();
        ProjectMetadataSnapshot metadata = project.Metadata.Snapshot() with { ModifiedAtUtc = savedAtUtc };
        MidoraProjectFileInformationV1 outputFileInformation = new(
            fileInformation?.CreatedWithSoftwareVersion ?? _softwareVersion,
            _softwareVersion);

        try
        {
            ValidateSupportedProject(project);
        }
        catch (Exception exception) when (exception is InvalidDataException
            or ArgumentException
            or OverflowException)
        {
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.Serialization,
                "The current Project cannot be serialized as the supported v1 package slice.",
                targetPath: target,
                innerException: exception);
        }

        string transactionId = Guid.NewGuid().ToString("N");
        string temporaryDirectory = Path.Combine(directory, $".midora-save-{transactionId}");
        string temporaryPackage = Path.Combine(directory, $".{Path.GetFileName(target)}.midora-temp-{transactionId}");
        string? backupPath = targetExisted
            ? Path.Combine(directory, $".{Path.GetFileName(target)}.midora-backup-{transactionId}.bak")
            : null;
        bool publishAttempted = false;
        List<MidoraPackageDiagnosticV1> cleanupDiagnostics = [];
        PackageContentV1? content = null;

        try
        {
            if (backupPath is not null)
            {
                try
                {
                    _faultInjector.ThrowIfRequested(MidoraPackageFaultPointV1.BeforeBackup, backupPath);
                    File.Copy(target, backupPath, overwrite: false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new MidoraPackageExceptionV1(
                        MidoraPackageStageV1.Backup,
                        "The existing target could not be copied to the transaction backup.",
                        target,
                        backupPath: backupPath,
                        temporaryPath: temporaryPackage,
                        innerException: exception);
                }
            }

            try
            {
                _faultInjector.ThrowIfRequested(
                    MidoraPackageFaultPointV1.BeforeContentWrite,
                    temporaryDirectory);
                Directory.CreateDirectory(temporaryDirectory);
                File.SetAttributes(
                    temporaryDirectory,
                    File.GetAttributes(temporaryDirectory) | FileAttributes.Hidden);
                try
                {
                    content = BuildContent(
                        project,
                        metadata,
                        outputFileInformation,
                        embeddedSoundFontResource,
                        temporaryDirectory,
                        knownPureMidiPackEntries: null,
                        cancellationToken);
                }
                catch (Exception exception) when (exception is InvalidDataException
                    or ArgumentException
                    or OverflowException)
                {
                    throw new MidoraPackageExceptionV1(
                        MidoraPackageStageV1.Serialization,
                        "The current Project cannot be serialized as the supported v1 package slice.",
                        targetPath: target,
                        temporaryPath: temporaryDirectory,
                        innerException: exception);
                }
                await WriteContentDirectoryAsync(temporaryDirectory, content, cancellationToken)
                    .ConfigureAwait(false);
                _faultInjector.ThrowIfRequested(
                    MidoraPackageFaultPointV1.BeforeZipWrite,
                    temporaryPackage);
                await WriteZipAsync(temporaryDirectory, temporaryPackage, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (EmbeddedSoundFontResourceUnavailableExceptionV1 exception)
            {
                throw CreateEmbeddedSoundFontRepairRequired(
                    MidoraPackageStageV1.Staging,
                    target,
                    project,
                    exception);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or InvalidDataException)
            {
                throw new MidoraPackageExceptionV1(
                    MidoraPackageStageV1.Staging,
                    "The save transaction could not build its temporary package.",
                    target,
                    backupPath: backupPath,
                    temporaryPath: temporaryPackage,
                    innerException: exception);
            }

            MidoraProjectOpenResultV1 reopened;
            try
            {
                _faultInjector.ThrowIfRequested(
                    MidoraPackageFaultPointV1.BeforeSelfValidation,
                    temporaryPackage);
                reopened = await OpenAsync(temporaryPackage, cancellationToken).ConfigureAwait(false);
                using (reopened)
                {
                    if (reopened.IsModified || reopened.Diagnostics.Count != 0)
                    {
                        throw new InvalidDataException("The temporary package reopened with recovery diagnostics.");
                    }
                    PackageContentV1 reopenedContent = BuildContent(
                        reopened.Project,
                        reopened.Project.Metadata.Snapshot(),
                        reopened.FileInformation,
                        reopened.EmbeddedSoundFontResource,
                        contentRoot: null,
                        content.PureMidiPackEntries,
                        cancellationToken);
                    RequireEqualContent(content.MemoryFiles, reopenedContent.MemoryFiles);
                }
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or InvalidDataException)
            {
                throw new MidoraPackageExceptionV1(
                    MidoraPackageStageV1.SelfValidation,
                    "The temporary package failed strict reopen self-validation.",
                    target,
                    backupPath: backupPath,
                    temporaryPath: temporaryPackage,
                    innerException: exception);
            }

            publishAttempted = true;
            try
            {
                _faultInjector.ThrowIfRequested(MidoraPackageFaultPointV1.BeforePublish, target);
                if (!targetExisted && File.Exists(target))
                {
                    throw new IOException(
                        "The target appeared after preflight; overwrite authorization and backup were not frozen for it.");
                }
                if (File.Exists(target))
                {
                    File.Replace(temporaryPackage, target, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(temporaryPackage, target);
                }
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
            {
                throw new MidoraPackageExceptionV1(
                    MidoraPackageStageV1.Publish,
                    "The validated temporary package could not atomically replace or create the target.",
                    target,
                    backupPath: backupPath,
                    temporaryPath: temporaryPackage,
                    innerException: exception);
            }

            TryDeleteFile(
                backupPath,
                cleanupDiagnostics,
                MidoraPackageFaultPointV1.BeforeBackupCleanup);
            TryDeleteDirectory(
                temporaryDirectory,
                cleanupDiagnostics,
                MidoraPackageFaultPointV1.BeforeStagingDirectoryCleanup);
            if (updateCurrentProject)
            {
                project.Metadata.CommitSuccessfulSave(savedAtUtc);
            }
            return new MidoraProjectSaveResultV1(target, outputFileInformation, cleanupDiagnostics);
        }
        catch
        {
            if (publishAttempted)
            {
                TryDeleteDirectory(temporaryDirectory, cleanupDiagnostics);
            }
            else
            {
                TryDeleteFile(temporaryPackage, cleanupDiagnostics);
                TryDeleteDirectory(temporaryDirectory, cleanupDiagnostics);
                TryDeleteFile(backupPath, cleanupDiagnostics);
            }
            throw;
        }
    }

    private async Task<MidoraProjectOpenResultV1> OpenCoreAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        using ZipArchive archive = OpenZipArchive(stream, path);
        Dictionary<string, ZipArchiveEntry> entries = ValidateContainer(archive, path);
        if (!entries.TryGetValue(MidoraPackagePathsV1.Manifest, out ZipArchiveEntry? manifestEntry))
        {
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.Manifest,
                "manifest.json is missing from the package root.",
                path,
                MidoraPackagePathsV1.Manifest);
        }

        byte[] manifestBytes;
        try
        {
            _faultInjector.ThrowIfRequested(
                MidoraPackageFaultPointV1.BeforeManifestRead,
                MidoraPackagePathsV1.Manifest);
            manifestBytes = await ReadEntryAsync(manifestEntry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.Manifest,
                "manifest.json is invalid.",
                path,
                MidoraPackagePathsV1.Manifest,
                innerException: exception);
        }

        ManifestVersionHeaderV1 versionHeader;
        try
        {
            versionHeader = ManifestCodecV1.ReadVersionHeader(manifestBytes);
        }
        catch (Exception exception) when (exception is InvalidDataException
            or System.Text.Json.JsonException)
        {
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.Manifest,
                "manifest.json is invalid.",
                path,
                MidoraPackagePathsV1.Manifest,
                innerException: exception);
        }
        if (versionHeader.FileFormatVersion > PersistenceContractV1.FileFormatVersion
            || versionHeader.MinimumReadableVersion > PersistenceContractV1.FileFormatVersion
            || versionHeader.ManifestSchemaVersion > PersistenceContractV1.SchemaVersion)
        {
            throw new MidoraPackageVersionCompatibilityExceptionV1(path, versionHeader);
        }

        ManifestJsonV1 manifest;
        try
        {
            manifest = ManifestCodecV1.Parse(manifestBytes);
        }
        catch (Exception exception) when (exception is InvalidDataException
            or System.Text.Json.JsonException)
        {
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.Manifest,
                "manifest.json is invalid.",
                path,
                MidoraPackagePathsV1.Manifest,
                innerException: exception);
        }

        Dictionary<string, ManifestFileEntryJsonV1> index = ValidateManifestIndex(manifest, path);
        List<MidoraPackageDiagnosticV1> diagnostics = CollectExtraEntryDiagnostics(entries, index);

        byte[] projectBytes = await ReadRequiredValidatedAsync(
            MidoraPackagePathsV1.Project, "core-json", entries, index, path, cancellationToken)
            .ConfigureAwait(false);
        ProjectJsonV1 projectIndex;
        try
        {
            projectIndex = ProjectCodecV1.Parse(projectBytes);
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException)
        {
            throw StructureFailure(path, MidoraPackagePathsV1.Project, "project.json is invalid.", exception);
        }
        diagnostics.AddRange(CollectOrphanObjectDiagnostics(index, projectIndex));
        bool isModified = false;
        ProjectSettingsJsonV1 projectSettings;
        try
        {
            byte[]? projectSettingsBytes = await TryReadValidatedAsync(
                MidoraPackagePathsV1.ProjectSettings, "settings-json", entries, index, path, cancellationToken)
                .ConfigureAwait(false);
            projectSettings = projectSettingsBytes is null
                ? throw new InvalidDataException("project-settings.json is missing.")
                : ProjectSettingsCodecV1.Parse(projectSettingsBytes);
        }
        catch (Exception exception) when (exception is InvalidDataException
            or System.Text.Json.JsonException
            or MidoraPackageExceptionV1
        {
            Stage: MidoraPackageStageV1.HashValidation or MidoraPackageStageV1.Structure
        })
        {
            projectSettings = new ProjectSettingsJsonV1
            {
                SchemaVersion = PersistenceContractV1.SchemaVersion,
                TicksPerQuarterNote = 192,
                GlobalInitialState = MidiStateCodecV1.FromDomain(new MidiInitialState())
            };
            AddRecoveryDiagnostic(diagnostics, MidoraPackagePathsV1.ProjectSettings, exception.Message);
            isModified = true;
        }

        long storedNextStableId = ProjectCodecV1.GetNextStableId(projectIndex);
        MidoraProject project = new(
            projectSettings.TicksPerQuarterNote,
            storedNextStableId,
            _timeProvider.GetUtcNow());
        PureMidiContentPackExtractionV1 pureMidiContent = new(project);
        try
        {
            MidiStateCodecV1.Restore(project.GlobalInitialState, projectSettings.GlobalInitialState);

            await RestoreProjectObjectsAsync(
                project,
                projectIndex,
                entries,
                index,
                path,
                diagnostics,
                pureMidiContent,
                cancellationToken).ConfigureAwait(false);

            ProjectSoundFontReference? soundFont = await RestoreOrdinarySettingsAsync(
                MidoraPackagePathsV1.SoundFontSettings,
                bytes => SoundFontSettingsCodecV1.Parse(bytes),
                entries, index, path, diagnostics, cancellationToken,
                () => isModified = true).ConfigureAwait(false);
            project.SoundFont.Restore(soundFont);
            diagnostics.AddRange(CollectOrphanEmbeddedResourceDiagnostics(index, soundFont));

            bool conductorFallback = false;
            try
            {
                byte[]? conductorBytes = await TryReadValidatedAsync(
                    MidoraPackagePathsV1.ConductorTrack, "conductor-json", entries, index, path, cancellationToken)
                    .ConfigureAwait(false);
                if (conductorBytes is null) throw new InvalidDataException("conductor-track.json is missing.");
                ConductorTrackCodecV1.Restore(project, ConductorTrackCodecV1.Parse(conductorBytes));
            }
            catch (Exception exception) when (exception is InvalidDataException
                or System.Text.Json.JsonException
                or MidoraPackageExceptionV1
            {
                Stage: MidoraPackageStageV1.HashValidation or MidoraPackageStageV1.Structure
            })
            {
                conductorFallback = true;
                AddRecoveryDiagnostic(diagnostics, MidoraPackagePathsV1.ConductorTrack, exception.Message);
                isModified = true;
            }

            ValidateLoadedStableIds(
                projectIndex,
                project,
                conductorFallback ? null : project.Conductor,
                soundFont,
                storedNextStableId);
            project.RestoreNextStableId(storedNextStableId);
            if (conductorFallback)
            {
                try
                {
                    project.Conductor.Tempos.Add(new TempoChange(project, 0, 120m));
                    project.Conductor.TimeSignatures.Add(new TimeSignatureChange(project, 0, 4, 4));
                }
                catch (InvalidOperationException exception)
                {
                    throw StructureFailure(
                        path,
                        MidoraPackagePathsV1.ConductorTrack,
                        "The damaged Conductor Track cannot be replaced because the stable ID space is exhausted.",
                        exception);
                }
            }

            byte[]? metadataBytes = await TryReadValidatedAsync(
                MidoraPackagePathsV1.Metadata, "core-json", entries, index, path, cancellationToken)
                .ConfigureAwait(false);
            if (metadataBytes is null)
            {
                AddRecoveryDiagnostic(diagnostics, MidoraPackagePathsV1.Metadata, "metadata.json is missing.");
                isModified = true;
            }
            else
            {
                try
                {
                    MetadataCodecV1.Restore(project.Metadata, metadataBytes);
                }
                catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException)
                {
                    throw StructureFailure(path, MidoraPackagePathsV1.Metadata, "metadata.json is present but invalid.", exception);
                }
            }

            ExportSettingsJsonV1? export = await RestoreOrdinarySettingsAsync(
                MidoraPackagePathsV1.ExportSettings,
                bytes => ExportSettingsCodecV1.Parse(bytes),
                entries, index, path, diagnostics, cancellationToken,
                () => isModified = true).ConfigureAwait(false);
            if (export is not null) ExportSettingsCodecV1.Restore(project.Export, export);

            PlaybackSettingsJsonV1? playback = await RestoreOrdinarySettingsAsync(
                MidoraPackagePathsV1.PlaybackSettings,
                bytes => PlaybackSettingsCodecV1.Parse(bytes),
                entries, index, path, diagnostics, cancellationToken,
                () => isModified = true).ConfigureAwait(false);
            if (playback is not null) PlaybackSettingsCodecV1.Restore(project.Playback, playback);

            byte[] audioRenderBytes = await ReadRequiredValidatedAsync(
                MidoraPackagePathsV1.AudioRenderSettings, "settings-json", entries, index, path, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                AudioRenderSettingsJsonV1 audio = AudioRenderSettingsCodecV1.Parse(audioRenderBytes);
                AudioRenderSettingsCodecV1.Restore(project.AudioRender, audio);
                HashSet<MidoraId> trackIds = project.Tracks.Select(track => track.Id)
                    .Concat(project.DamagedLogicalTracks.Select(track => track.Id))
                    .ToHashSet();
                MidoraId[] missingTrackIds = project.AudioRender.ExplicitLogicalTrackIds
                    .Where(id => !trackIds.Contains(id))
                    .ToArray();
                if (missingTrackIds.Length != 0)
                {
                    project.AudioRender.ExplicitLogicalTrackIds.ExceptWith(missingTrackIds);
                    AddRecoveryDiagnostic(
                        diagnostics,
                        MidoraPackagePathsV1.AudioRenderSettings,
                        "Explicit Track IDs not present in the Project were removed.");
                    isModified = true;
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException)
            {
                throw StructureFailure(
                    path,
                    MidoraPackagePathsV1.AudioRenderSettings,
                    "Required current-format Audio Render Settings are invalid.",
                    exception);
            }

            GlobalResetDefaultsJsonV1? reset = await RestoreOrdinarySettingsAsync(
                MidoraPackagePathsV1.GlobalResetDefaults,
                bytes => GlobalResetDefaultsCodecV1.Parse(bytes),
                entries, index, path, diagnostics, cancellationToken,
                () => isModified = true).ConfigureAwait(false);
            if (reset is not null) MidiStateCodecV1.Restore(project.GlobalResetDefaults, reset.State);

            _ = await RestoreOrdinarySettingsAsync(
                MidoraPackagePathsV1.GlobalEventScopeDefaults,
                bytes =>
                {
                    GlobalEventScopeDefaultsCodecV1.Parse(bytes);
                    return true;
                },
                entries, index, path, diagnostics, cancellationToken,
                () => isModified = true).ConfigureAwait(false);

            EmbeddedSoundFontResourceV1? embeddedSoundFontResource = soundFont is
                EmbeddedProjectSoundFontReference embedded
                    ? await RestoreEmbeddedSoundFontResourceAsync(
                        embedded,
                        entries,
                        index,
                        diagnostics,
                        cancellationToken).ConfigureAwait(false)
                    : null;

            return new MidoraProjectOpenResultV1(
                project,
                new MidoraProjectFileInformationV1(
                    manifest.CreatedWithSoftwareVersion,
                    manifest.LastSavedWithSoftwareVersion),
                isModified,
                diagnostics)
            {
                EmbeddedSoundFontResource = embeddedSoundFontResource
            };
        }
        catch
        {
            project.Dispose();
            throw;
        }
    }

    private static PackageContentV1 BuildContent(
        MidoraProject project,
        ProjectMetadataSnapshot metadata,
        MidoraProjectFileInformationV1 fileInformation,
        EmbeddedSoundFontResourceV1? embeddedSoundFontResource,
        string? contentRoot,
        IReadOnlyList<ManifestFileEntryJsonV1>? knownPureMidiPackEntries,
        CancellationToken cancellationToken)
    {
        Dictionary<string, byte[]> content = new(StringComparer.Ordinal)
        {
            [MidoraPackagePathsV1.Project] = ProjectCodecV1.Serialize(project),
            [MidoraPackagePathsV1.Metadata] = MetadataCodecV1.Serialize(metadata),
            [MidoraPackagePathsV1.ConductorTrack] = ConductorTrackCodecV1.Serialize(project),
            [MidoraPackagePathsV1.ProjectSettings] = ProjectSettingsCodecV1.Serialize(project),
            [MidoraPackagePathsV1.ExportSettings] = ExportSettingsCodecV1.Serialize(project.Export),
            [MidoraPackagePathsV1.PlaybackSettings] = PlaybackSettingsCodecV1.Serialize(project.Playback),
            [MidoraPackagePathsV1.AudioRenderSettings] = AudioRenderSettingsCodecV1.Serialize(project.AudioRender),
            [MidoraPackagePathsV1.SoundFontSettings] = SoundFontSettingsCodecV1.Serialize(project.SoundFont),
            [MidoraPackagePathsV1.GlobalResetDefaults] = GlobalResetDefaultsCodecV1.Serialize(
                project.GlobalResetDefaults),
            [MidoraPackagePathsV1.GlobalEventScopeDefaults] = GlobalEventScopeDefaultsCodecV1.Serialize()
        };
        foreach (EventInstrument instrument in project.EventInstruments)
        {
            content.Add(
                $"event-instruments/ei_{instrument.Id}.pb",
                EventInstrumentProtobufCodecV1.Serialize(instrument));
        }
        foreach (LogicalTrack track in project.Tracks)
        {
            content.Add(
                $"logical-tracks/lt_{track.Id}.pb",
                LogicalTrackProtobufCodecV1.Serialize(track));
        }
        foreach (MidiChannelRoot root in project.MidiChannelRoots)
        {
            content.Add(
                $"midi-channel-roots/mcr_{root.Id}.pb",
                MidiChannelRootProtobufCodecV1.Serialize(root));
        }
        foreach (PureMidiTrack track in project.PureMidiTracks)
        {
            string contentPackPath = MidoraPackagePathsV1.PureMidiContentPack(track.Id);
            content.Add(
                $"midi-tracks/mt_{track.Id}.pb",
                PureMidiTrackProtobufCodecV1.Serialize(track, contentPackPath));
        }
        ManifestFileEntryJsonV1[] pureMidiPackEntries;
        if (knownPureMidiPackEntries is not null)
        {
            pureMidiPackEntries = knownPureMidiPackEntries
                .OrderBy(value => value.Path, StringComparer.Ordinal)
                .ToArray();
            string[] expected = project.PureMidiTracks
                .Select(value => MidoraPackagePathsV1.PureMidiContentPack(value.Id))
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            if (!pureMidiPackEntries.Select(value => value.Path).SequenceEqual(expected))
                throw new InvalidDataException("Pure MIDI content-pack entries do not match Project tracks.");
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
            pureMidiPackEntries = project.PureMidiTracks
                .OrderBy(value => value.Id)
                .Select(track => PureMidiContentPackPersistenceV1.Materialize(
                    track,
                    contentRoot,
                    cancellationToken))
                .ToArray();
        }
        EmbeddedProjectSoundFontReference? embeddedReference =
            project.SoundFont.Reference as EmbeddedProjectSoundFontReference;
        string? embeddedPath = embeddedReference is null
            ? null
            : MidoraPackagePathsV1.EmbeddedSoundFont(embeddedReference.ResourceId);
        ManifestFileEntryJsonV1[] manifestFiles = content.Select(item => new ManifestFileEntryJsonV1
        {
            Path = item.Key,
            Kind = GetExpectedKind(item.Key),
            SchemaVersion = PersistenceContractV1.SchemaVersion,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(item.Value))
        })
            .Concat(pureMidiPackEntries)
            .Concat(embeddedReference is null
                ? []
                :
                [
                    new ManifestFileEntryJsonV1
                    {
                        Path = embeddedPath!,
                        Kind = "embedded-resource",
                        SchemaVersion = null,
                        Sha256 = embeddedReference.Sha256
                    }
                ])
            .ToArray();
        ManifestJsonV1 manifest = new()
        {
            Magic = "midora-project",
            FileFormatVersion = PersistenceContractV1.FileFormatVersion,
            MinimumReadableVersion = PersistenceContractV1.FileFormatVersion,
            ManifestSchemaVersion = PersistenceContractV1.SchemaVersion,
            CreatedWithSoftwareVersion = fileInformation.CreatedWithSoftwareVersion,
            LastSavedWithSoftwareVersion = fileInformation.LastSavedWithSoftwareVersion,
            Files = manifestFiles
        };
        content.Add(MidoraPackagePathsV1.Manifest, ManifestCodecV1.Serialize(manifest));
        return new(
            content,
            pureMidiPackEntries,
            embeddedPath,
            embeddedReference,
            embeddedSoundFontResource);
    }

    private static void ValidateSupportedProject(MidoraProject project)
    {
        if (project.DamagedEventInstruments.Count != 0
            || project.DamagedLogicalTracks.Count != 0
            || project.DamagedMidiChannelRoots.Count != 0
            || project.DamagedPureMidiTracks.Count != 0)
        {
            throw new InvalidDataException(
                "Projects containing damaged object placeholders cannot be saved.");
        }
        HashSet<MidoraId> trackIds = project.Tracks.Select(track => track.Id).ToHashSet();
        if (!project.AudioRender.ExplicitLogicalTrackIds.IsSubsetOf(trackIds))
        {
            throw new InvalidDataException("Audio Render Settings reference a Logical Track absent from the Project.");
        }

        StableIdSetV1 ids = new();
        foreach (MidoraId id in EnumerateConductorIds(project.Conductor))
        {
            AddId(id, project.NextStableId, ids, "Conductor event");
        }
        foreach (EventInstrument instrument in project.EventInstruments)
        {
            foreach (MidoraId id in EnumerateEventInstrumentIds(instrument))
            {
                AddId(id, project.NextStableId, ids, "Event Instrument object");
            }
        }
        foreach (LogicalTrack track in project.Tracks)
        {
            foreach (MidoraId id in EnumerateLogicalTrackIds(track))
            {
                AddId(id, project.NextStableId, ids, "Logical Track object");
            }
        }
        foreach (MidiChannelRoot root in project.MidiChannelRoots)
        {
            AddId(root.Id, project.NextStableId, ids, "MIDI Channel Root");
        }
        foreach (PureMidiTrack track in project.PureMidiTracks)
        {
            foreach (MidoraId id in EnumeratePureMidiTrackIds(track))
            {
                AddId(id, project.NextStableId, ids, "Pure MIDI Track object");
            }
        }
        if (project.SoundFont.Reference is EmbeddedProjectSoundFontReference embedded)
        {
            AddId(embedded.ResourceId, project.NextStableId, ids, "Embedded SoundFont resource");
        }
    }

    private static void RequireEmbeddedSoundFontReadyForSave(
        MidoraProject project,
        EmbeddedSoundFontResourceV1? embeddedSoundFontResource,
        string targetPath)
    {
        if (project.SoundFont.Reference is not EmbeddedProjectSoundFontReference embeddedReference)
        {
            return;
        }
        try
        {
            if (embeddedSoundFontResource is null)
            {
                throw new EmbeddedSoundFontResourceUnavailableExceptionV1(
                    EmbeddedSoundFontResourceStatusV1.RuntimeResourceMissing);
            }
            _ = embeddedSoundFontResource.RequireReadablePath(embeddedReference);
        }
        catch (EmbeddedSoundFontResourceUnavailableExceptionV1 exception)
        {
            throw new MidoraEmbeddedSoundFontRepairRequiredExceptionV1(
                MidoraPackageStageV1.Preflight,
                targetPath,
                embeddedReference,
                exception.Status,
                exception.ActualSha256,
                exception.ActualFileSizeBytes,
                exception);
        }
    }

    private static MidoraEmbeddedSoundFontRepairRequiredExceptionV1
        CreateEmbeddedSoundFontRepairRequired(
            MidoraPackageStageV1 stage,
            string targetPath,
            MidoraProject project,
            EmbeddedSoundFontResourceUnavailableExceptionV1 exception)
    {
        EmbeddedProjectSoundFontReference reference =
            project.SoundFont.Reference as EmbeddedProjectSoundFontReference
            ?? throw new InvalidOperationException(
                "An Embedded SoundFont repair failure requires an Embedded Project reference.");
        return new(
            stage,
            targetPath,
            reference,
            exception.Status,
            exception.ActualSha256,
            exception.ActualFileSizeBytes,
            exception);
    }

    private static Dictionary<string, ZipArchiveEntry> ValidateContainer(ZipArchive archive, string targetPath)
    {
        Dictionary<string, ZipArchiveEntry> entries = new(StringComparer.Ordinal);
        HashSet<string> insensitivePaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName;
            bool isDirectory = name.EndsWith("/", StringComparison.Ordinal);
            string validatedName = isDirectory ? name[..^1] : name;
            try
            {
                PersistenceValueValidationV1.ValidateRelativePath(validatedName, "Zip entry path");
            }
            catch (InvalidDataException exception)
            {
                throw new MidoraPackageExceptionV1(
                    MidoraPackageStageV1.Container,
                    "The package contains an invalid Zip entry path.",
                    targetPath,
                    name,
                    innerException: exception);
            }
            if (!insensitivePaths.Add(validatedName))
            {
                throw new MidoraPackageExceptionV1(
                    MidoraPackageStageV1.Container,
                    "The package contains duplicate or case-conflicting Zip entry paths.",
                    targetPath,
                    name);
            }
            int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            int expectedUnixType = isDirectory ? 0x4000 : 0x8000;
            if (unixType != 0 && unixType != expectedUnixType)
            {
                throw new MidoraPackageExceptionV1(
                    MidoraPackageStageV1.Container,
                    "The package contains a non-regular Zip entry.",
                    targetPath,
                    name);
            }
            EnsureZipEntryCanBeOpened(entry, targetPath);
            if (!isDirectory)
            {
                entries.Add(name, entry);
            }
        }
        return entries;
    }

    private static void EnsureZipEntryCanBeOpened(ZipArchiveEntry entry, string targetPath)
    {
        try
        {
            using Stream probe = entry.Open();
            _ = probe.ReadByte();
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.Container,
                "The package contains an encrypted, corrupt, or unsupported Zip entry.",
                targetPath,
                entry.FullName,
                innerException: exception);
        }
    }

    private static ZipArchive OpenZipArchive(Stream stream, string targetPath)
    {
        try
        {
            ValidateZipEncryptionFlags(stream);
            return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException exception)
        {
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.Container,
                "The file is not a valid Zip container.",
                targetPath,
                innerException: exception);
        }
    }

    private static void ValidateZipEncryptionFlags(Stream stream)
    {
        const uint endOfCentralDirectorySignature = 0x06054b50;
        const uint zip64EndOfCentralDirectorySignature = 0x06064b50;
        const uint zip64LocatorSignature = 0x07064b50;
        const uint centralDirectoryEntrySignature = 0x02014b50;
        const int endOfCentralDirectoryMinimumSize = 22;
        const int maximumCommentSize = ushort.MaxValue;

        long originalPosition = stream.Position;
        try
        {
            int tailLength = checked((int)Math.Min(
                stream.Length,
                endOfCentralDirectoryMinimumSize + maximumCommentSize));
            byte[] tail = new byte[tailLength];
            long tailOffset = stream.Length - tailLength;
            stream.Position = tailOffset;
            stream.ReadExactly(tail);

            int endIndex = -1;
            for (int index = tail.Length - endOfCentralDirectoryMinimumSize; index >= 0; index--)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index, 4))
                        == endOfCentralDirectorySignature
                    && index + endOfCentralDirectoryMinimumSize
                        + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 20, 2)) == tail.Length)
                {
                    endIndex = index;
                    break;
                }
            }
            if (endIndex < 0)
            {
                throw new InvalidDataException("The Zip end-of-central-directory record is missing.");
            }

            ulong entryCount = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(endIndex + 10, 2));
            ulong centralDirectoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(endIndex + 16, 4));
            if (entryCount == ushort.MaxValue || centralDirectoryOffset == uint.MaxValue)
            {
                long endRecordOffset = tailOffset + endIndex;
                if (endRecordOffset < 20)
                {
                    throw new InvalidDataException("The Zip64 locator is missing.");
                }
                Span<byte> locator = stackalloc byte[20];
                stream.Position = endRecordOffset - locator.Length;
                stream.ReadExactly(locator);
                if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != zip64LocatorSignature)
                {
                    throw new InvalidDataException("The Zip64 locator is invalid.");
                }
                ulong zip64RecordOffset = BinaryPrimitives.ReadUInt64LittleEndian(locator[8..]);
                if (zip64RecordOffset > long.MaxValue || zip64RecordOffset > (ulong)(stream.Length - 56))
                {
                    throw new InvalidDataException("The Zip64 end record offset is invalid.");
                }
                Span<byte> zip64Record = stackalloc byte[56];
                stream.Position = (long)zip64RecordOffset;
                stream.ReadExactly(zip64Record);
                if (BinaryPrimitives.ReadUInt32LittleEndian(zip64Record) != zip64EndOfCentralDirectorySignature)
                {
                    throw new InvalidDataException("The Zip64 end record is invalid.");
                }
                entryCount = BinaryPrimitives.ReadUInt64LittleEndian(zip64Record[32..]);
                centralDirectoryOffset = BinaryPrimitives.ReadUInt64LittleEndian(zip64Record[48..]);
            }

            if (centralDirectoryOffset > long.MaxValue || centralDirectoryOffset > (ulong)stream.Length)
            {
                throw new InvalidDataException("The Zip central-directory offset is invalid.");
            }
            stream.Position = (long)centralDirectoryOffset;
            Span<byte> header = stackalloc byte[46];
            for (ulong index = 0; index < entryCount; index++)
            {
                stream.ReadExactly(header);
                if (BinaryPrimitives.ReadUInt32LittleEndian(header) != centralDirectoryEntrySignature)
                {
                    throw new InvalidDataException("The Zip central directory is invalid.");
                }
                ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
                if ((flags & 0x2041) != 0)
                {
                    throw new InvalidDataException("Encrypted Zip entries are not supported.");
                }
                int variableLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..])
                    + BinaryPrimitives.ReadUInt16LittleEndian(header[30..])
                    + BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
                if (stream.Position > stream.Length - variableLength)
                {
                    throw new InvalidDataException("The Zip central-directory entry length is invalid.");
                }
                stream.Position += variableLength;
            }
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    private static Dictionary<string, ManifestFileEntryJsonV1> ValidateManifestIndex(
        ManifestJsonV1 manifest,
        string targetPath)
    {
        Dictionary<string, ManifestFileEntryJsonV1> result = new(StringComparer.Ordinal);
        HashSet<string> insensitivePaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (ManifestFileEntryJsonV1 item in manifest.Files)
        {
            if (item.Path == MidoraPackagePathsV1.Manifest
                || !result.TryAdd(item.Path, item)
                || !insensitivePaths.Add(item.Path))
            {
                throw new MidoraPackageExceptionV1(
                    MidoraPackageStageV1.Manifest,
                    "The manifest contains a self-entry, duplicate path, or case-conflicting path.",
                    targetPath,
                    item.Path);
            }
        }
        return result;
    }

    private static List<MidoraPackageDiagnosticV1> CollectExtraEntryDiagnostics(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        IReadOnlyDictionary<string, ManifestFileEntryJsonV1> index)
    {
        List<MidoraPackageDiagnosticV1> result = [];
        foreach (string entry in entries.Keys.OrderBy(value => value, StringComparer.Ordinal))
        {
            if (entry != MidoraPackagePathsV1.Manifest && !index.ContainsKey(entry))
            {
                result.Add(new(
                    MidoraPackageDiagnosticSeverityV1.Information,
                    MidoraPackageDiagnosticCategoryV1.FileFormat,
                    "MIDORA-PERSIST-INFO-UNINDEXED",
                    "The unindexed package entry is not Project content and will not be preserved on save.",
                    entry));
            }
        }
        foreach (ManifestFileEntryJsonV1 item in index.Values.OrderBy(value => value.Path, StringComparer.Ordinal))
        {
            if (!IsKnownKind(item.Kind))
            {
                result.Add(new(
                    MidoraPackageDiagnosticSeverityV1.Information,
                    MidoraPackageDiagnosticCategoryV1.FileFormat,
                    "MIDORA-PERSIST-INFO-UNKNOWN-KIND",
                    "The manifest entry uses an unknown file kind and is not Project content.",
                    item.Path));
            }
        }
        return result;
    }

    private static IEnumerable<MidoraPackageDiagnosticV1> CollectOrphanObjectDiagnostics(
        IReadOnlyDictionary<string, ManifestFileEntryJsonV1> manifestIndex,
        ProjectJsonV1 projectIndex)
    {
        HashSet<string> referencedPaths = projectIndex.ArrangementParents
            .Select(value => value.Path)
            .Concat(projectIndex.ArrangementParents.SelectMany(value => value.Children.Select(child => child.Path)))
            .Concat(projectIndex.ArrangementParents
                .SelectMany(value => value.Children)
                .Where(value => value.Kind == "pure-midi-track")
                .Select(value => MidoraPackagePathsV1.PureMidiContentPack(
                    ParseId(value.Id, "project.json Pure MIDI Track ID"))))
            .ToHashSet(StringComparer.Ordinal);
        foreach (ManifestFileEntryJsonV1 item in manifestIndex.Values
            .Where(value => value.Kind is "event-instrument-pb" or "logical-track-pb"
                or "midi-channel-root-pb" or "pure-midi-track-pb" or "pure-midi-content-pack")
            .Where(value => !referencedPaths.Contains(value.Path))
            .OrderBy(value => value.Path, StringComparer.Ordinal))
        {
            yield return new(
                MidoraPackageDiagnosticSeverityV1.Information,
                MidoraPackageDiagnosticCategoryV1.FileFormat,
                "MIDORA-PERSIST-INFO-ORPHAN-OBJECT",
                "The manifest object/content entry is not referenced by project.json and will not be preserved on save.",
                item.Path);
        }
    }

    private static IEnumerable<MidoraPackageDiagnosticV1> CollectOrphanEmbeddedResourceDiagnostics(
        IReadOnlyDictionary<string, ManifestFileEntryJsonV1> manifestIndex,
        ProjectSoundFontReference? soundFont)
    {
        string? referencedPath = soundFont is EmbeddedProjectSoundFontReference embedded
            ? MidoraPackagePathsV1.EmbeddedSoundFont(embedded.ResourceId)
            : null;
        foreach (ManifestFileEntryJsonV1 item in manifestIndex.Values
            .Where(value => value.Kind == "embedded-resource")
            .Where(value => !string.Equals(value.Path, referencedPath, StringComparison.Ordinal))
            .OrderBy(value => value.Path, StringComparer.Ordinal))
        {
            yield return new(
                MidoraPackageDiagnosticSeverityV1.Information,
                MidoraPackageDiagnosticCategoryV1.FileFormat,
                "MIDORA-PERSIST-INFO-ORPHAN-RESOURCE",
                "The embedded resource is not referenced by SoundFont Settings and will not be preserved on save.",
                item.Path);
        }
    }

    private static async Task<EmbeddedSoundFontResourceV1> RestoreEmbeddedSoundFontResourceAsync(
        EmbeddedProjectSoundFontReference reference,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        IReadOnlyDictionary<string, ManifestFileEntryJsonV1> manifestIndex,
        ICollection<MidoraPackageDiagnosticV1> diagnostics,
        CancellationToken cancellationToken)
    {
        string packagePath = MidoraPackagePathsV1.EmbeddedSoundFont(reference.ResourceId);
        if (!manifestIndex.TryGetValue(packagePath, out ManifestFileEntryJsonV1? manifestEntry))
        {
            return DamagedEmbeddedSoundFont(
                reference,
                EmbeddedSoundFontResourceStatusV1.MissingManifestEntry,
                packagePath,
                "SoundFont Settings references a resource absent from manifest.json.",
                diagnostics);
        }
        if (manifestEntry.Kind != "embedded-resource" || manifestEntry.SchemaVersion.HasValue)
        {
            return DamagedEmbeddedSoundFont(
                reference,
                EmbeddedSoundFontResourceStatusV1.InvalidManifestEntry,
                packagePath,
                "The Embedded SoundFont manifest kind or schemaVersion is invalid.",
                diagnostics);
        }
        if (!entries.TryGetValue(packagePath, out ZipArchiveEntry? archiveEntry))
        {
            return DamagedEmbeddedSoundFont(
                reference,
                EmbeddedSoundFontResourceStatusV1.MissingPackageEntry,
                packagePath,
                "The Embedded SoundFont is indexed by manifest.json but absent from the Zip container.",
                diagnostics);
        }
        if (archiveEntry.Length != reference.FileSizeBytes)
        {
            return DamagedEmbeddedSoundFont(
                reference,
                EmbeddedSoundFontResourceStatusV1.SizeMismatch,
                packagePath,
                "The Embedded SoundFont uncompressed size does not match SoundFont Settings.",
                diagnostics,
                actualFileSizeBytes: archiveEntry.Length);
        }
        if (!string.Equals(manifestEntry.Sha256, reference.Sha256, StringComparison.Ordinal))
        {
            return DamagedEmbeddedSoundFont(
                reference,
                EmbeddedSoundFontResourceStatusV1.HashMismatch,
                packagePath,
                "The Embedded SoundFont hashes in manifest.json and SoundFont Settings do not match.",
                diagnostics,
                actualFileSizeBytes: archiveEntry.Length);
        }

        string directory;
        try
        {
            directory = EmbeddedSoundFontResourceV1.CreateRuntimeDirectory();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return DamagedEmbeddedSoundFont(
                reference,
                EmbeddedSoundFontResourceStatusV1.Unreadable,
                packagePath,
                $"The Embedded SoundFont runtime extraction directory could not be created. {exception.Message}",
                diagnostics);
        }

        string extractedPath = Path.Combine(directory, "soundfont.sf2");
        try
        {
            await using Stream source = archiveEntry.Open();
            await using FileStream destination = new(
                extractedPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            StreamCopyIdentityV1 actual = await EmbeddedSoundFontResourceV1.CopyAndHashAsync(
                source,
                destination,
                cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (actual.FileSizeBytes != reference.FileSizeBytes
                || !string.Equals(actual.Sha256, reference.Sha256, StringComparison.Ordinal))
            {
                EmbeddedSoundFontResourceV1.TryDeleteDirectory(directory);
                return DamagedEmbeddedSoundFont(
                    reference,
                    EmbeddedSoundFontResourceStatusV1.HashMismatch,
                    packagePath,
                    "The Embedded SoundFont bytes do not match manifest.json and SoundFont Settings.",
                    diagnostics,
                    actual.Sha256,
                    actual.FileSizeBytes);
            }
            return new(
                reference,
                EmbeddedSoundFontResourceStatusV1.Available,
                extractedPath,
                directory,
                actual.Sha256,
                actual.FileSizeBytes);
        }
        catch (OperationCanceledException)
        {
            EmbeddedSoundFontResourceV1.TryDeleteDirectory(directory);
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or CryptographicException)
        {
            EmbeddedSoundFontResourceV1.TryDeleteDirectory(directory);
            return DamagedEmbeddedSoundFont(
                reference,
                EmbeddedSoundFontResourceStatusV1.Unreadable,
                packagePath,
                $"The Embedded SoundFont could not be extracted and verified. {exception.Message}",
                diagnostics);
        }
    }

    private static EmbeddedSoundFontResourceV1 DamagedEmbeddedSoundFont(
        EmbeddedProjectSoundFontReference reference,
        EmbeddedSoundFontResourceStatusV1 status,
        string packagePath,
        string detail,
        ICollection<MidoraPackageDiagnosticV1> diagnostics,
        string? actualSha256 = null,
        long? actualFileSizeBytes = null)
    {
        diagnostics.Add(new(
            MidoraPackageDiagnosticSeverityV1.Error,
            MidoraPackageDiagnosticCategoryV1.Resource,
            "MIDORA-PERSIST-EMBEDDED-SF2-DAMAGED",
            detail,
            packagePath));
        return new(
            reference,
            status,
            resolvedAbsolutePath: null,
            ownedDirectory: null,
            actualSha256,
            actualFileSizeBytes);
    }

    private static async Task RestoreProjectObjectsAsync(
        MidoraProject project,
        ProjectJsonV1 projectIndex,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        IReadOnlyDictionary<string, ManifestFileEntryJsonV1> manifestIndex,
        string targetPath,
        ICollection<MidoraPackageDiagnosticV1> diagnostics,
        PureMidiContentPackExtractionV1 pureMidiContent,
        CancellationToken cancellationToken)
    {
        for (int parentIndex = 0; parentIndex < projectIndex.ArrangementParents.Length; parentIndex++)
        {
            ArrangementParentIndexJsonV1 parent = projectIndex.ArrangementParents[parentIndex];
            MidoraId expectedParentId = ParseId(parent.Id, "project.json Arrangement parent ID");
            ArrangementParentKind parentKind = parent.Kind == "event-instrument"
                ? ArrangementParentKind.EventInstrument
                : ArrangementParentKind.MidiChannelRoot;
            project.ArrangementParents.Add(new(parentKind, expectedParentId));
            string parentManifestKind = parentKind == ArrangementParentKind.EventInstrument
                ? "event-instrument-pb"
                : "midi-channel-root-pb";
            ObjectPayloadV1 payload = await ReadObjectPayloadAsync(
                parent.Path,
                parentManifestKind,
                entries,
                manifestIndex,
                targetPath,
                cancellationToken).ConfigureAwait(false);
            if (payload.Bytes is null)
            {
                AddDamagedObject(
                    parentKind == ArrangementParentKind.EventInstrument
                        ? project.DamagedEventInstruments
                        : project.DamagedMidiChannelRoots,
                    expectedParentId,
                    parent.NameSnapshot,
                    parent.Path,
                    parentIndex,
                    payload.Error!,
                    diagnostics,
                    childIds: parent.Children.Select(value => ParseId(
                        value.Id,
                        "project.json Arrangement child ID")).ToArray());
            }
            else
            {
                try
                {
                    if (parentKind == ArrangementParentKind.EventInstrument)
                    {
                        EventInstrument instrument = EventInstrumentProtobufCodecV1.Restore(
                            project,
                            payload.Bytes);
                        if (instrument.Id != expectedParentId
                            || !instrument.LogicalTrackIds.SequenceEqual(
                                parent.Children.Select(value => ParseId(
                                    value.Id,
                                    "project.json Logical Track ID"))))
                        {
                            throw new InvalidDataException(
                                "The Event Instrument identity or child order does not match project.json.");
                        }
                        project.EventInstruments.Add(instrument);
                    }
                    else
                    {
                        MidiChannelRoot root = MidiChannelRootProtobufCodecV1.Restore(
                            project,
                            payload.Bytes);
                        if (root.Id != expectedParentId
                            || !root.MidiTrackIds.SequenceEqual(
                                parent.Children.Select(value => ParseId(
                                    value.Id,
                                    "project.json Pure MIDI Track ID"))))
                        {
                            throw new InvalidDataException(
                                "The MIDI Channel Root identity or child order does not match project.json.");
                        }
                        project.MidiChannelRoots.Add(root);
                    }
                }
                catch (ProtobufObjectHeaderExceptionV1 exception)
                {
                    throw StructureFailure(targetPath, parent.Path, exception.Message, exception);
                }
                catch (InvalidDataException exception)
                {
                    AddDamagedObject(
                        parentKind == ArrangementParentKind.EventInstrument
                            ? project.DamagedEventInstruments
                            : project.DamagedMidiChannelRoots,
                        expectedParentId,
                        parent.NameSnapshot,
                        parent.Path,
                        parentIndex,
                        exception.Message,
                        diagnostics,
                        childIds: parent.Children.Select(value => ParseId(
                            value.Id,
                            "project.json Arrangement child ID")).ToArray());
                }
            }

            for (int childIndex = 0; childIndex < parent.Children.Length; childIndex++)
            {
                ArrangementChildIndexJsonV1 child = parent.Children[childIndex];
                MidoraId expectedChildId = ParseId(
                    child.Id,
                    "project.json Arrangement child ID");
                bool logical = child.Kind == "logical-track";
                ObjectPayloadV1 childPayload = await ReadObjectPayloadAsync(
                    child.Path,
                    logical ? "logical-track-pb" : "pure-midi-track-pb",
                    entries,
                    manifestIndex,
                    targetPath,
                    cancellationToken).ConfigureAwait(false);
                if (childPayload.Bytes is null)
                {
                    AddDamagedObject(
                        logical ? project.DamagedLogicalTracks : project.DamagedPureMidiTracks,
                        expectedChildId,
                        child.NameSnapshot,
                        child.Path,
                        childIndex,
                        childPayload.Error!,
                        diagnostics,
                        parentId: expectedParentId);
                    continue;
                }
                try
                {
                    if (logical)
                    {
                        LogicalTrack track = LogicalTrackProtobufCodecV1.Restore(
                            project,
                            childPayload.Bytes);
                        if (track.Id != expectedChildId
                            || track.EventInstrumentId != expectedParentId)
                        {
                            throw new InvalidDataException(
                                "The Logical Track identity or parent does not match project.json.");
                        }
                        project.Tracks.Add(track);
                    }
                    else
                    {
                        RestoredPureMidiTrackV1 restored = PureMidiTrackProtobufCodecV1.Restore(
                            project,
                            childPayload.Bytes);
                        PureMidiTrack track = restored.Track;
                        if (track.Id != expectedChildId
                            || track.MidiChannelRootId != expectedParentId)
                        {
                            throw new InvalidDataException(
                                "The Pure MIDI Track identity or parent does not match project.json.");
                        }
                        await pureMidiContent.AttachAsync(
                            track,
                            restored.ContentPackPath,
                            entries,
                            manifestIndex,
                            cancellationToken).ConfigureAwait(false);
                        project.PureMidiTracks.Add(track);
                    }
                }
                catch (ProtobufObjectHeaderExceptionV1 exception)
                {
                    throw StructureFailure(targetPath, child.Path, exception.Message, exception);
                }
                catch (InvalidDataException exception)
                {
                    AddDamagedObject(
                        logical ? project.DamagedLogicalTracks : project.DamagedPureMidiTracks,
                        expectedChildId,
                        child.NameSnapshot,
                        child.Path,
                        childIndex,
                        exception.Message,
                        diagnostics,
                        parentId: expectedParentId);
                }
            }
        }
    }

    private static async Task<ObjectPayloadV1> ReadObjectPayloadAsync(
        string packagePath,
        string expectedKind,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        IReadOnlyDictionary<string, ManifestFileEntryJsonV1> manifestIndex,
        string targetPath,
        CancellationToken cancellationToken)
    {
        if (!manifestIndex.TryGetValue(packagePath, out ManifestFileEntryJsonV1? manifestEntry))
        {
            throw StructureFailure(
                targetPath,
                packagePath,
                "project.json references an object that is absent from manifest.json.");
        }
        if (manifestEntry.Kind != expectedKind
            || manifestEntry.SchemaVersion != PersistenceContractV1.SchemaVersion)
        {
            throw StructureFailure(
                targetPath,
                packagePath,
                "Object file kind or schemaVersion is inconsistent with project.json.");
        }
        if (!entries.TryGetValue(packagePath, out ZipArchiveEntry? archiveEntry))
        {
            return new(null, "The object is indexed by project.json and manifest.json but its Zip entry is missing.");
        }
        byte[] bytes = await ReadEntryAsync(archiveEntry, cancellationToken).ConfigureAwait(false);
        string actualHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(actualHash, manifestEntry.Sha256, StringComparison.Ordinal))
        {
            return new(null, "The object SHA-256 does not match manifest.json.");
        }
        return new(bytes, null);
    }

    private static void AddDamagedObject(
        ICollection<DamagedProjectObject> target,
        MidoraId id,
        string nameSnapshot,
        string packagePath,
        int originalIndex,
        string error,
        ICollection<MidoraPackageDiagnosticV1> diagnostics,
        MidoraId? parentId = null,
        IReadOnlyList<MidoraId>? childIds = null)
    {
        target.Add(new(
            id,
            nameSnapshot,
            packagePath,
            error,
            originalIndex,
            parentId,
            childIds is null ? null : Array.AsReadOnly(childIds.ToArray())));
        diagnostics.Add(new(
            MidoraPackageDiagnosticSeverityV1.Error,
            MidoraPackageDiagnosticCategoryV1.FileDamage,
            "MIDORA-PERSIST-DAMAGED-OBJECT",
            $"The object could not be loaded and is represented by a damaged placeholder. {error}",
            packagePath));
    }

    private sealed record ObjectPayloadV1(byte[]? Bytes, string? Error);

    private static async Task<byte[]> ReadRequiredValidatedAsync(
        string packagePath,
        string expectedKind,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        IReadOnlyDictionary<string, ManifestFileEntryJsonV1> index,
        string targetPath,
        CancellationToken cancellationToken)
    {
        byte[]? result = await TryReadValidatedAsync(
            packagePath, expectedKind, entries, index, targetPath, cancellationToken).ConfigureAwait(false);
        return result ?? throw StructureFailure(targetPath, packagePath, $"Required package file '{packagePath}' is missing.");
    }

    private static async Task<byte[]?> TryReadValidatedAsync(
        string packagePath,
        string expectedKind,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        IReadOnlyDictionary<string, ManifestFileEntryJsonV1> index,
        string targetPath,
        CancellationToken cancellationToken)
    {
        if (!index.TryGetValue(packagePath, out ManifestFileEntryJsonV1? manifestEntry)
            || !entries.TryGetValue(packagePath, out ZipArchiveEntry? archiveEntry))
        {
            return null;
        }
        if (manifestEntry.Kind != expectedKind
            || manifestEntry.SchemaVersion != PersistenceContractV1.SchemaVersion)
        {
            throw StructureFailure(targetPath, packagePath, "Package file kind or schemaVersion is inconsistent.");
        }
        byte[] bytes = await ReadEntryAsync(archiveEntry, cancellationToken).ConfigureAwait(false);
        string actualHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(actualHash, manifestEntry.Sha256, StringComparison.Ordinal))
        {
            throw new MidoraPackageExceptionV1(
                MidoraPackageStageV1.HashValidation,
                "Package file SHA-256 does not match manifest.json.",
                targetPath,
                packagePath);
        }
        return bytes;
    }

    private static async Task<T?> RestoreOrdinarySettingsAsync<T>(
        string packagePath,
        Func<ReadOnlySpan<byte>, T> parse,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        IReadOnlyDictionary<string, ManifestFileEntryJsonV1> index,
        string targetPath,
        ICollection<MidoraPackageDiagnosticV1> diagnostics,
        CancellationToken cancellationToken,
        Action markModified)
    {
        try
        {
            byte[]? bytes = await TryReadValidatedAsync(
                packagePath, "settings-json", entries, index, targetPath, cancellationToken)
                .ConfigureAwait(false);
            if (bytes is null) throw new InvalidDataException($"{packagePath} is missing.");
            return parse(bytes);
        }
        catch (Exception exception) when (exception is InvalidDataException
            or System.Text.Json.JsonException
            or MidoraPackageExceptionV1
        {
            Stage: MidoraPackageStageV1.HashValidation or MidoraPackageStageV1.Structure
        })
        {
            AddRecoveryDiagnostic(diagnostics, packagePath, exception.Message);
            markModified();
            return default;
        }
    }

    private static async Task<byte[]> ReadEntryAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        await using Stream input = entry.Open();
        using MemoryStream output = entry.Length <= int.MaxValue
            ? new MemoryStream((int)entry.Length)
            : new MemoryStream();
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }

    private static async Task WriteContentDirectoryAsync(
        string root,
        PackageContentV1 content,
        CancellationToken cancellationToken)
    {
        foreach (string packagePath in GetStableEntryOrder(content.MemoryFiles.Keys).Skip(1))
        {
            string filePath = Path.Combine(root, packagePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            await File.WriteAllBytesAsync(
                filePath,
                content.MemoryFiles[packagePath],
                cancellationToken).ConfigureAwait(false);
        }

        if (content.EmbeddedPackagePath is not null)
        {
            EmbeddedProjectSoundFontReference reference = content.EmbeddedReference
                ?? throw new InvalidDataException("Embedded SoundFont package content has no reference.");
            EmbeddedSoundFontResourceV1 resource = content.EmbeddedResource
                ?? throw new EmbeddedSoundFontResourceUnavailableExceptionV1(
                    EmbeddedSoundFontResourceStatusV1.RuntimeResourceMissing);
            string destinationPath = Path.Combine(
                root,
                content.EmbeddedPackagePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using FileStream source = resource.OpenReadForSave(reference);
            await using FileStream destination = new(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            StreamCopyIdentityV1 actual = await EmbeddedSoundFontResourceV1.CopyAndHashAsync(
                source,
                destination,
                cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (actual.FileSizeBytes != reference.FileSizeBytes
                || !string.Equals(actual.Sha256, reference.Sha256, StringComparison.Ordinal))
            {
                throw new EmbeddedSoundFontResourceUnavailableExceptionV1(
                    actual.FileSizeBytes != reference.FileSizeBytes
                        ? EmbeddedSoundFontResourceStatusV1.SizeMismatch
                        : EmbeddedSoundFontResourceStatusV1.HashMismatch,
                    actual.Sha256,
                    actual.FileSizeBytes);
            }
        }
        await File.WriteAllBytesAsync(
            Path.Combine(root, MidoraPackagePathsV1.Manifest),
            content.MemoryFiles[MidoraPackagePathsV1.Manifest],
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteZipAsync(
        string contentRoot,
        string packagePath,
        CancellationToken cancellationToken)
    {
        await using FileStream output = new(
            packagePath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using ZipArchive archive = new(output, ZipArchiveMode.Create, leaveOpen: true);
        string[] contentPaths = Directory.GetFiles(contentRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(contentRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
            .ToArray();
        foreach (string entryName in GetStableEntryOrder(contentPaths))
        {
            CompressionLevel compressionLevel =
                entryName.StartsWith("resources/soundfonts/", StringComparison.Ordinal)
                && entryName.EndsWith(".sf2", StringComparison.OrdinalIgnoreCase)
                || entryName.StartsWith("midi-content/", StringComparison.Ordinal)
                && entryName.EndsWith(".mpk", StringComparison.OrdinalIgnoreCase)
                    ? CompressionLevel.NoCompression
                    : CompressionLevel.Optimal;
            ZipArchiveEntry entry = archive.CreateEntry(entryName, compressionLevel);
            entry.LastWriteTime = CanonicalZipTimestamp;
            await using Stream target = entry.Open();
            string sourcePath = Path.Combine(contentRoot, entryName.Replace('/', Path.DirectorySeparatorChar));
            await using FileStream source = new(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }
    }

    private static IEnumerable<string> GetStableEntryOrder(IEnumerable<string> paths)
    {
        HashSet<string> available = paths.ToHashSet(StringComparer.Ordinal);
        string[] fixedOrder =
        [
            MidoraPackagePathsV1.Manifest,
            MidoraPackagePathsV1.Project,
            MidoraPackagePathsV1.Metadata,
            MidoraPackagePathsV1.ConductorTrack,
            MidoraPackagePathsV1.ProjectSettings,
            MidoraPackagePathsV1.ExportSettings,
            MidoraPackagePathsV1.PlaybackSettings,
            MidoraPackagePathsV1.AudioRenderSettings,
            MidoraPackagePathsV1.SoundFontSettings,
            MidoraPackagePathsV1.GlobalResetDefaults,
            MidoraPackagePathsV1.GlobalEventScopeDefaults
        ];
        foreach (string path in fixedOrder)
        {
            if (available.Remove(path)) yield return path;
        }
        foreach (string path in available.OrderBy(value => value, StringComparer.Ordinal)) yield return path;
    }

    private static string GetExpectedKind(string path) => path switch
    {
        MidoraPackagePathsV1.Project or MidoraPackagePathsV1.Metadata => "core-json",
        MidoraPackagePathsV1.ConductorTrack => "conductor-json",
        _ when path.StartsWith("settings/", StringComparison.Ordinal) => "settings-json",
        _ when path.StartsWith("event-instruments/", StringComparison.Ordinal) => "event-instrument-pb",
        _ when path.StartsWith("logical-tracks/", StringComparison.Ordinal) => "logical-track-pb",
        _ when path.StartsWith("midi-channel-roots/", StringComparison.Ordinal) => "midi-channel-root-pb",
        _ when path.StartsWith("midi-tracks/", StringComparison.Ordinal) => "pure-midi-track-pb",
        _ when path.StartsWith("midi-content/", StringComparison.Ordinal) => "pure-midi-content-pack",
        _ when path.StartsWith("resources/soundfonts/", StringComparison.Ordinal) => "embedded-resource",
        _ => throw new InvalidDataException($"No v1 manifest kind is defined for '{path}'.")
    };

    private static bool IsKnownKind(string kind) => kind is
        "core-json" or "settings-json" or "conductor-json" or
        "event-instrument-pb" or "logical-track-pb" or
        "midi-channel-root-pb" or "pure-midi-track-pb" or
        "pure-midi-content-pack" or "embedded-resource";

    private static void ValidateLoadedStableIds(
        ProjectJsonV1 projectIndex,
        MidoraProject project,
        ConductorTrack? conductor,
        ProjectSoundFontReference? soundFont,
        long nextStableId)
    {
        StableIdSetV1 ids = new();
        if (conductor is not null)
        {
            foreach (MidoraId id in EnumerateConductorIds(conductor))
            {
                AddId(id, nextStableId, ids, "Conductor event");
            }
        }
        Dictionary<MidoraId, EventInstrument> instruments = project.EventInstruments.ToDictionary(value => value.Id);
        Dictionary<MidoraId, LogicalTrack> tracks = project.Tracks.ToDictionary(value => value.Id);
        Dictionary<MidoraId, MidiChannelRoot> roots = project.MidiChannelRoots.ToDictionary(value => value.Id);
        Dictionary<MidoraId, PureMidiTrack> midiTracks = project.PureMidiTracks.ToDictionary(value => value.Id);
        HashSet<MidoraId> damagedInstrumentIds = project.DamagedEventInstruments.Select(value => value.Id).ToHashSet();
        HashSet<MidoraId> damagedTrackIds = project.DamagedLogicalTracks.Select(value => value.Id).ToHashSet();
        HashSet<MidoraId> damagedRootIds = project.DamagedMidiChannelRoots.Select(value => value.Id).ToHashSet();
        HashSet<MidoraId> damagedMidiTrackIds = project.DamagedPureMidiTracks.Select(value => value.Id).ToHashSet();
        foreach (ArrangementParentIndexJsonV1 parent in projectIndex.ArrangementParents)
        {
            MidoraId parentId = ParseId(parent.Id, "project.json Arrangement parent ID");
            AddId(parentId, nextStableId, ids, "Arrangement parent");
            if (parent.Kind == "event-instrument"
                && instruments.TryGetValue(parentId, out EventInstrument? instrument))
            {
                foreach (MidoraId nestedId in EnumerateEventInstrumentIds(instrument).Skip(1))
                {
                    AddId(nestedId, nextStableId, ids, "Event Instrument nested object");
                }
            }
            else if (parent.Kind == "midi-channel-root" && !roots.ContainsKey(parentId)
                && !damagedRootIds.Contains(parentId)
                || parent.Kind == "event-instrument" && !instruments.ContainsKey(parentId)
                && !damagedInstrumentIds.Contains(parentId))
            {
                throw new InvalidDataException(
                    "An indexed Arrangement parent was neither loaded nor isolated as damaged.");
            }
            foreach (ArrangementChildIndexJsonV1 child in parent.Children)
            {
                MidoraId childId = ParseId(child.Id, "project.json Arrangement child ID");
                AddId(childId, nextStableId, ids, "Arrangement child");
                if (child.Kind == "logical-track" && tracks.TryGetValue(childId, out LogicalTrack? track))
                {
                    foreach (MidoraId nestedId in EnumerateLogicalTrackIds(track).Skip(1))
                    {
                        AddId(nestedId, nextStableId, ids, "Logical Track nested object");
                    }
                }
                else if (child.Kind == "pure-midi-track"
                    && midiTracks.TryGetValue(childId, out PureMidiTrack? midiTrack))
                {
                    foreach (MidoraId nestedId in EnumeratePureMidiTrackIds(midiTrack).Skip(1))
                    {
                        AddId(nestedId, nextStableId, ids, "Pure MIDI Track nested object");
                    }
                }
                else if (child.Kind == "logical-track" && !damagedTrackIds.Contains(childId)
                    || child.Kind == "pure-midi-track" && !damagedMidiTrackIds.Contains(childId))
                {
                    throw new InvalidDataException(
                        "An indexed Arrangement child was neither loaded nor isolated as damaged.");
                }
            }
        }
        if (soundFont is EmbeddedProjectSoundFontReference embedded)
        {
            AddId(embedded.ResourceId, nextStableId, ids, "Embedded SoundFont resource");
        }
    }

    private static IEnumerable<MidoraId> EnumerateConductorIds(ConductorTrack conductor)
    {
        foreach (TempoChange item in conductor.Tempos) yield return item.Id;
        foreach (TimeSignatureChange item in conductor.TimeSignatures) yield return item.Id;
        foreach (KeySignatureChange item in conductor.KeySignatures) yield return item.Id;
        foreach (ProjectMarker item in conductor.Markers) yield return item.Id;
        if (conductor.EndMarker is not null) yield return conductor.EndMarker.Id;
    }

    private static IEnumerable<MidoraId> EnumerateEventInstrumentIds(EventInstrument instrument)
    {
        yield return instrument.Id;
        foreach (LogicalParameterDefinition parameter in instrument.LogicalParameters)
        {
            yield return parameter.Id;
            foreach (LogicalParameterEnumItem item in parameter.EnumItems) yield return item.Id;
        }
        foreach (SubVoice voice in instrument.SubVoices)
        {
            yield return voice.Id;
            foreach (SubVoiceEventMapping mapping in voice.EventMappings)
            {
                foreach (MidoraId id in EnumerateMappingChainIds(mapping.Steps)) yield return id;
            }
            foreach (TemplateEvent templateEvent in voice.Events)
            {
                yield return templateEvent.Id;
            }
            foreach (ValueCurve curve in voice.Curves)
            {
                yield return curve.Id;
                foreach (CurvePoint point in curve.Points) yield return point.Id;
            }
        }
        foreach (InstrumentEnvelope envelope in instrument.Envelopes) yield return envelope.Id;
        foreach (CSharpMappingFunction function in instrument.MappingFunctions) yield return function.Id;
        foreach (LogicalParameterMapping mapping in instrument.ParameterMappings)
        {
            yield return mapping.Id;
            foreach (MidoraId id in EnumerateMappingChainIds(mapping.Steps)) yield return id;
        }
    }

    private static IEnumerable<MidoraId> EnumerateMappingChainIds(MappingChain chain)
    {
        yield return chain.Id;
        foreach (ValueMappingStep step in chain) yield return step.Id;
    }

    private static IEnumerable<MidoraId> EnumerateLogicalTrackIds(LogicalTrack track)
    {
        yield return track.Id;
        foreach (Segment segment in track.Segments)
        {
            yield return segment.Id;
            foreach (LogicalNote note in segment.Notes) yield return note.Id;
            foreach (LogicalParameterLane lane in segment.ParameterLanes)
            {
                yield return lane.Id;
                foreach (CurvePoint point in lane.Points) yield return point.Id;
            }
        }
    }

    private static IEnumerable<MidoraId> EnumeratePureMidiTrackIds(PureMidiTrack track)
    {
        yield return track.Id;
        foreach (MidiSegment segment in track.Segments)
        {
            yield return segment.Id;
            foreach (DirectMidiNote note in segment.Notes) yield return note.Id;
            foreach (DirectMidiChannelEvent directEvent in segment.ChannelEvents)
            {
                yield return directEvent.Id;
            }
            foreach (OpaqueMidiEvent opaque in segment.OpaqueEvents) yield return opaque.Id;
        }
    }

    private static void AddId(MidoraId id, long nextStableId, StableIdSetV1 ids, string source)
    {
        if (id == default || id.Value >= nextStableId || !ids.Add(id))
        {
            throw new InvalidDataException($"{source} stable ID is zero, duplicated, or not below nextStableId.");
        }
    }

    private sealed class StableIdSetV1
    {
        private const int BlockBitShift = 20;
        private const int BitsPerBlock = 1 << BlockBitShift;
        private const int WordsPerBlock = BitsPerBlock / 64;
        private readonly Dictionary<long, ulong[]> _blocks = [];

        public bool Add(MidoraId id)
        {
            long value = id.Value;
            long blockIndex = value >> BlockBitShift;
            int bitInBlock = (int)(value & (BitsPerBlock - 1));
            if (!_blocks.TryGetValue(blockIndex, out ulong[]? block))
            {
                block = new ulong[WordsPerBlock];
                _blocks.Add(blockIndex, block);
            }
            int wordIndex = bitInBlock >> 6;
            ulong mask = 1UL << (bitInBlock & 63);
            if ((block[wordIndex] & mask) != 0) return false;
            block[wordIndex] |= mask;
            return true;
        }
    }

    private static MidoraId ParseId(StableIdJsonV1? value, string fieldName)
    {
        if (value is not StableIdJsonV1 id || id.Value <= 0)
        {
            throw new InvalidDataException($"{fieldName} is not canonical.");
        }
        return id.ToDomain();
    }

    private static void RequireEqualContent(
        IReadOnlyDictionary<string, byte[]> expected,
        IReadOnlyDictionary<string, byte[]> actual)
    {
        if (expected.Count != actual.Count)
        {
            throw new InvalidDataException("Reopened package content count changed.");
        }
        foreach ((string path, byte[] expectedBytes) in expected)
        {
            if (!actual.TryGetValue(path, out byte[]? actualBytes)
                || !expectedBytes.AsSpan().SequenceEqual(actualBytes))
            {
                throw new InvalidDataException($"Reopened package content changed at '{path}'.");
            }
        }
    }

    private static void AddRecoveryDiagnostic(
        ICollection<MidoraPackageDiagnosticV1> diagnostics,
        string packagePath,
        string detail) => diagnostics.Add(new(
            MidoraPackageDiagnosticSeverityV1.Error,
            MidoraPackageDiagnosticCategoryV1.FileDamage,
            "MIDORA-PERSIST-RECOVERED-DEFAULT",
            $"The file was missing or invalid and the current default was used. {detail}",
            packagePath));

    private static MidoraPackageExceptionV1 StructureFailure(
        string targetPath,
        string packagePath,
        string message,
        Exception? exception = null) => new(
            MidoraPackageStageV1.Structure,
            message,
            targetPath,
            packagePath,
            innerException: exception);

    private static string NormalizeFilePath(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            throw new ArgumentException("Path is not a valid file-system path.", parameterName, exception);
        }
    }

    private void TryDeleteFile(
        string? path,
        ICollection<MidoraPackageDiagnosticV1> diagnostics,
        MidoraPackageFaultPointV1? faultPoint = null)
    {
        if (path is null || !File.Exists(path)) return;
        try
        {
            if (faultPoint.HasValue)
            {
                _faultInjector.ThrowIfRequested(faultPoint.Value, path);
            }
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(CleanupWarning(path, exception.Message));
        }
    }

    private void TryDeleteDirectory(
        string path,
        ICollection<MidoraPackageDiagnosticV1> diagnostics,
        MidoraPackageFaultPointV1? faultPoint = null)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            if (faultPoint.HasValue)
            {
                _faultInjector.ThrowIfRequested(faultPoint.Value, path);
            }
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(CleanupWarning(path, exception.Message));
        }
    }

    private static MidoraPackageDiagnosticV1 CleanupWarning(string path, string detail) => new(
        MidoraPackageDiagnosticSeverityV1.Warning,
        MidoraPackageDiagnosticCategoryV1.SaveTransaction,
        "MIDORA-PERSIST-CLEANUP-FAILED",
        $"The save completed but a transaction artifact could not be removed. {detail}",
        path);

    private sealed record PackageContentV1(
        Dictionary<string, byte[]> MemoryFiles,
        IReadOnlyList<ManifestFileEntryJsonV1> PureMidiPackEntries,
        string? EmbeddedPackagePath,
        EmbeddedProjectSoundFontReference? EmbeddedReference,
        EmbeddedSoundFontResourceV1? EmbeddedResource);
}
