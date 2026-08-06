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
    IReadOnlyList<MidoraPackageDiagnosticV1> Diagnostics);

public sealed record MidoraProjectSaveResultV1(
    string TargetPath,
    MidoraProjectFileInformationV1 FileInformation,
    IReadOnlyList<MidoraPackageDiagnosticV1> Diagnostics);

public sealed class MidoraPackageExceptionV1 : IOException
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

public sealed class MidoraProjectPackageV1
{
    private static readonly DateTimeOffset CanonicalZipTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _softwareVersion;
    private readonly TimeProvider _timeProvider;

    public MidoraProjectPackageV1(string softwareVersion, TimeProvider? timeProvider = null)
    {
        PersistenceValueValidationV1.ValidateShortText(
            softwareVersion, nameof(softwareVersion), allowEmpty: false);
        _softwareVersion = softwareVersion;
        _timeProvider = timeProvider ?? TimeProvider.System;
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
        CancellationToken cancellationToken = default) =>
        SaveCoreAsync(
            project,
            targetPath,
            fileInformation,
            editingTimeSession,
            overwriteAuthorized,
            updateCurrentProject: true,
            cancellationToken);

    public Task<MidoraProjectSaveResultV1> SaveCopyAsync(
        MidoraProject project,
        string targetPath,
        MidoraProjectFileInformationV1? fileInformation = null,
        ProjectEditingTimeSession? editingTimeSession = null,
        bool overwriteAuthorized = false,
        CancellationToken cancellationToken = default) =>
        SaveCoreAsync(
            project,
            targetPath,
            fileInformation,
            editingTimeSession,
            overwriteAuthorized,
            updateCurrentProject: false,
            cancellationToken);

    private async Task<MidoraProjectSaveResultV1> SaveCoreAsync(
        MidoraProject project,
        string targetPath,
        MidoraProjectFileInformationV1? fileInformation,
        ProjectEditingTimeSession? editingTimeSession,
        bool overwriteAuthorized,
        bool updateCurrentProject,
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

        DateTimeOffset savedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        _ = editingTimeSession?.SnapshotTotalEditingTimeMilliseconds();
        ProjectMetadataSnapshot metadata = project.Metadata.Snapshot() with { ModifiedAtUtc = savedAtUtc };
        MidoraProjectFileInformationV1 outputFileInformation = new(
            fileInformation?.CreatedWithSoftwareVersion ?? _softwareVersion,
            _softwareVersion);

        Dictionary<string, byte[]> content;
        try
        {
            ValidateSupportedProject(project);
            content = BuildContent(project, metadata, outputFileInformation);
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

        try
        {
            if (backupPath is not null)
            {
                try
                {
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
                Directory.CreateDirectory(temporaryDirectory);
                File.SetAttributes(
                    temporaryDirectory,
                    File.GetAttributes(temporaryDirectory) | FileAttributes.Hidden);
                await WriteContentDirectoryAsync(temporaryDirectory, content, cancellationToken)
                    .ConfigureAwait(false);
                await WriteZipAsync(temporaryDirectory, temporaryPackage, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
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
                reopened = await OpenAsync(temporaryPackage, cancellationToken).ConfigureAwait(false);
                if (reopened.IsModified || reopened.Diagnostics.Count != 0)
                {
                    throw new InvalidDataException("The temporary package reopened with recovery diagnostics.");
                }
                Dictionary<string, byte[]> reopenedContent = BuildContent(
                    reopened.Project,
                    reopened.Project.Metadata.Snapshot(),
                    reopened.FileInformation);
                RequireEqualContent(content, reopenedContent);
            }
            catch (Exception exception) when (exception is MidoraPackageExceptionV1 or InvalidDataException)
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

            TryDeleteFile(backupPath, cleanupDiagnostics);
            TryDeleteDirectory(temporaryDirectory, cleanupDiagnostics);
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

        ManifestJsonV1 manifest;
        try
        {
            manifest = ManifestCodecV1.Parse(await ReadEntryAsync(manifestEntry, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException)
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
        if (projectIndex.EventInstruments.Length != 0 || projectIndex.LogicalTracks.Length != 0)
        {
            throw StructureFailure(
                path,
                MidoraPackagePathsV1.Project,
                "This package uses Event Instrument or Logical Track object files not yet supported by the JSON package slice.");
        }

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

        UInt128 storedNextStableId = ProjectCodecV1.GetNextStableId(projectIndex);
        MidoraProject project = new(projectSettings.TicksPerQuarterNote, storedNextStableId);
        MidiStateCodecV1.Restore(project.GlobalInitialState, projectSettings.GlobalInitialState);

        foreach (ProjectFolderIndexJsonV1 folder in projectIndex.EventInstrumentFolders)
        {
            project.EventInstrumentFolders.Add(new EventInstrumentLibraryFolder(
                ParseId(folder.Id, "project.json folder ID"))
            {
                Name = folder.Name
            });
        }

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

        ValidateLoadedStableIds(projectIndex, conductorFallback ? null : project.Conductor, storedNextStableId);
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

        _ = await RestoreOrdinarySettingsAsync(
            MidoraPackagePathsV1.ExportSettings,
            bytes =>
            {
                ExportSettingsCodecV1.Parse(bytes);
                return true;
            },
            entries, index, path, diagnostics, cancellationToken,
            () => isModified = true).ConfigureAwait(false);

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
            if (project.AudioRender.ExplicitLogicalTrackIds.Count != 0)
            {
                project.AudioRender.ExplicitLogicalTrackIds.Clear();
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

        ProjectSoundFontReference? soundFont = await RestoreOrdinarySettingsAsync(
            MidoraPackagePathsV1.SoundFontSettings,
            bytes => SoundFontSettingsCodecV1.Parse(bytes),
            entries, index, path, diagnostics, cancellationToken,
            () => isModified = true).ConfigureAwait(false);
        if (soundFont is EmbeddedProjectSoundFontReference)
        {
            throw StructureFailure(
                path,
                MidoraPackagePathsV1.SoundFontSettings,
                "Embedded SoundFont resources are not yet supported by the JSON package slice.");
        }
        project.SoundFont.Restore(soundFont);

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

        return new MidoraProjectOpenResultV1(
            project,
            new MidoraProjectFileInformationV1(
                manifest.CreatedWithSoftwareVersion,
                manifest.LastSavedWithSoftwareVersion),
            isModified,
            diagnostics);
    }

    private static Dictionary<string, byte[]> BuildContent(
        MidoraProject project,
        ProjectMetadataSnapshot metadata,
        MidoraProjectFileInformationV1 fileInformation)
    {
        Dictionary<string, byte[]> content = new(StringComparer.Ordinal)
        {
            [MidoraPackagePathsV1.Project] = ProjectCodecV1.Serialize(project),
            [MidoraPackagePathsV1.Metadata] = MetadataCodecV1.Serialize(metadata),
            [MidoraPackagePathsV1.ConductorTrack] = ConductorTrackCodecV1.Serialize(project.Conductor),
            [MidoraPackagePathsV1.ProjectSettings] = ProjectSettingsCodecV1.Serialize(project),
            [MidoraPackagePathsV1.ExportSettings] = ExportSettingsCodecV1.Serialize(),
            [MidoraPackagePathsV1.PlaybackSettings] = PlaybackSettingsCodecV1.Serialize(project.Playback),
            [MidoraPackagePathsV1.AudioRenderSettings] = AudioRenderSettingsCodecV1.Serialize(project.AudioRender),
            [MidoraPackagePathsV1.SoundFontSettings] = SoundFontSettingsCodecV1.Serialize(project.SoundFont),
            [MidoraPackagePathsV1.GlobalResetDefaults] = GlobalResetDefaultsCodecV1.Serialize(
                project.GlobalResetDefaults),
            [MidoraPackagePathsV1.GlobalEventScopeDefaults] = GlobalEventScopeDefaultsCodecV1.Serialize()
        };
        ManifestJsonV1 manifest = new()
        {
            Magic = "midora-project",
            FileFormatVersion = PersistenceContractV1.FileFormatVersion,
            MinimumReadableVersion = PersistenceContractV1.FileFormatVersion,
            ManifestSchemaVersion = PersistenceContractV1.SchemaVersion,
            CreatedWithSoftwareVersion = fileInformation.CreatedWithSoftwareVersion,
            LastSavedWithSoftwareVersion = fileInformation.LastSavedWithSoftwareVersion,
            Files = content.Select(item => new ManifestFileEntryJsonV1
            {
                Path = item.Key,
                Kind = GetExpectedKind(item.Key),
                SchemaVersion = PersistenceContractV1.SchemaVersion,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(item.Value))
            }).ToArray()
        };
        content.Add(MidoraPackagePathsV1.Manifest, ManifestCodecV1.Serialize(manifest));
        return content;
    }

    private static void ValidateSupportedProject(MidoraProject project)
    {
        if (project.EventInstruments.Count != 0 || project.Tracks.Count != 0)
        {
            throw new InvalidDataException(
                "Event Instrument and Logical Track protobuf persistence is outside this package slice.");
        }
        if (project.SoundFont.Reference is EmbeddedProjectSoundFontReference)
        {
            throw new InvalidDataException("Embedded SoundFont persistence is outside this package slice.");
        }
        if (project.AudioRender.ExplicitLogicalTrackIds.Count != 0)
        {
            throw new InvalidDataException("Audio Render Settings reference Logical Tracks absent from this slice.");
        }

        HashSet<MidoraId> ids = [];
        foreach (EventInstrumentLibraryFolder folder in project.EventInstrumentFolders)
        {
            AddId(folder.Id, project.NextStableId, ids, "Event Instrument folder");
        }
        foreach (MidoraId id in EnumerateConductorIds(project.Conductor))
        {
            AddId(id, project.NextStableId, ids, "Conductor event");
        }
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
            else if ((item.Kind is "event-instrument-pb" or "logical-track-pb")
                && !IsIndexedObjectPath(item.Path))
            {
                result.Add(new(
                    MidoraPackageDiagnosticSeverityV1.Information,
                    MidoraPackageDiagnosticCategoryV1.FileFormat,
                    "MIDORA-PERSIST-INFO-ORPHAN-OBJECT",
                    "The object entry is not referenced by this package slice and will not be preserved on save.",
                    item.Path));
            }
        }
        return result;
    }

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
        IReadOnlyDictionary<string, byte[]> content,
        CancellationToken cancellationToken)
    {
        foreach (string packagePath in GetStableEntryOrder(content.Keys).Skip(1))
        {
            string filePath = Path.Combine(root, packagePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            await File.WriteAllBytesAsync(filePath, content[packagePath], cancellationToken).ConfigureAwait(false);
        }
        await File.WriteAllBytesAsync(
            Path.Combine(root, MidoraPackagePathsV1.Manifest),
            content[MidoraPackagePathsV1.Manifest],
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
        foreach (string entryName in GetStableEntryOrder(MidoraPackagePathsV1.FixedContentPaths.Prepend(
            MidoraPackagePathsV1.Manifest)))
        {
            ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
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
        _ => throw new InvalidDataException($"No v1 manifest kind is defined for '{path}'.")
    };

    private static bool IsKnownKind(string kind) => kind is
        "core-json" or "settings-json" or "conductor-json" or
        "event-instrument-pb" or "logical-track-pb" or "embedded-resource";

    private static bool IsIndexedObjectPath(string path) => false;

    private static void ValidateLoadedStableIds(
        ProjectJsonV1 projectIndex,
        ConductorTrack? conductor,
        UInt128 nextStableId)
    {
        HashSet<MidoraId> ids = [];
        foreach (ProjectFolderIndexJsonV1 folder in projectIndex.EventInstrumentFolders)
        {
            AddId(ParseId(folder.Id, "project.json folder ID"), nextStableId, ids, "Event Instrument folder");
        }
        if (conductor is not null)
        {
            foreach (MidoraId id in EnumerateConductorIds(conductor))
            {
                AddId(id, nextStableId, ids, "Conductor event");
            }
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

    private static void AddId(MidoraId id, UInt128 nextStableId, ISet<MidoraId> ids, string source)
    {
        if (id == default || id.ToSequence() >= nextStableId || !ids.Add(id))
        {
            throw new InvalidDataException($"{source} stable ID is zero, duplicated, or not below nextStableId.");
        }
    }

    private static MidoraId ParseId(string value, string fieldName)
    {
        if (!MidoraId.TryParseCanonical(value, out MidoraId id))
        {
            throw new InvalidDataException($"{fieldName} is not canonical.");
        }
        return id;
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

    private static void TryDeleteFile(
        string? path,
        ICollection<MidoraPackageDiagnosticV1> diagnostics)
    {
        if (path is null || !File.Exists(path)) return;
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(CleanupWarning(path, exception.Message));
        }
    }

    private static void TryDeleteDirectory(
        string path,
        ICollection<MidoraPackageDiagnosticV1> diagnostics)
    {
        if (!Directory.Exists(path)) return;
        try
        {
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
}
