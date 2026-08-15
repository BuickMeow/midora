using System.IO.Compression;
using System.Security.Cryptography;
using Midora.Domain;

namespace Midora.Persistence.Tests;

public sealed class EmbeddedSoundFontPackageV1Tests
{
    private static readonly DateTimeOffset SavedAt =
        new(2026, 8, 6, 7, 8, 9, TimeSpan.Zero);

    [Fact]
    public async Task EmbeddedSoundFontStreamsThroughImportPackageOpenAndSaveCopy()
    {
        using TemporaryDirectory temporary = new();
        byte[] expectedBytes = CreateContent(2 * 1024 * 1024 + 17);
        string sourcePath = temporary.PathFor("Orchestra.sf2");
        string firstPath = temporary.PathFor("first.midora");
        string secondPath = temporary.PathFor("second.midora");
        await File.WriteAllBytesAsync(sourcePath, expectedBytes);
        MidoraProject project = new(480, SavedAt);
        MidoraProjectPackageV1 packages = CreateService();

        await using EmbeddedSoundFontResourceV1 imported =
            await SoundFontBindingV1.BindEmbeddedAsync(project, sourcePath);
        EmbeddedProjectSoundFontReference reference = Assert.IsType<EmbeddedProjectSoundFontReference>(
            project.SoundFont.Reference);
        Assert.True(imported.IsAvailable);
        Assert.NotEqual(sourcePath, imported.ResolvedAbsolutePath);
        Assert.Equal(expectedBytes.LongLength, reference.FileSizeBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(expectedBytes)), reference.Sha256);

        await packages.SaveCopyAsync(
            project,
            firstPath,
            embeddedSoundFontResource: imported);

        string packageResourcePath = $"resources/soundfonts/{reference.ResourceId}.sf2";
        using (ZipArchive archive = ZipFile.OpenRead(firstPath))
        {
            ZipArchiveEntry resourceEntry = Assert.Single(
                archive.Entries,
                entry => entry.FullName == packageResourcePath);
            Assert.Equal(resourceEntry.Length, resourceEntry.CompressedLength);
            await using Stream resource = resourceEntry.Open();
            using MemoryStream bytes = new();
            await resource.CopyToAsync(bytes);
            Assert.Equal(expectedBytes, bytes.ToArray());

            ManifestJsonV1 manifest = ReadManifest(archive);
            ManifestFileEntryJsonV1 manifestEntry = Assert.Single(
                manifest.Files,
                item => item.Path == packageResourcePath);
            Assert.Equal("embedded-resource", manifestEntry.Kind);
            Assert.Null(manifestEntry.SchemaVersion);
            Assert.Equal(reference.Sha256, manifestEntry.Sha256);
        }

        string extractedPath;
        await using (MidoraProjectOpenResultV1 opened = await packages.OpenAsync(firstPath))
        {
            Assert.False(opened.IsModified);
            Assert.Empty(opened.Diagnostics);
            EmbeddedSoundFontResourceV1 extracted = Assert.IsType<EmbeddedSoundFontResourceV1>(
                opened.EmbeddedSoundFontResource);
            Assert.True(extracted.IsAvailable);
            extractedPath = Assert.IsType<string>(extracted.ResolvedAbsolutePath);
            Assert.True(File.Exists(extractedPath));
            Assert.Equal(reference, opened.Project.SoundFont.Reference);
            Assert.Equal(expectedBytes, await File.ReadAllBytesAsync(extractedPath));

            await packages.SaveCopyAsync(
                opened.Project,
                secondPath,
                opened.FileInformation,
                embeddedSoundFontResource: extracted);
        }

        Assert.False(File.Exists(extractedPath));
        Assert.Equal(await File.ReadAllBytesAsync(firstPath), await File.ReadAllBytesAsync(secondPath));
        using FileStream exclusive = new(firstPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.Length > 0);
    }

    [Fact]
    public async Task EmbeddedBindingFailureDoesNotMutateProjectOrConsumeStableId()
    {
        using TemporaryDirectory temporary = new();
        MidoraProject project = new(480, SavedAt);
        long nextStableId = project.NextStableId;

        await Assert.ThrowsAsync<FileNotFoundException>(() => SoundFontBindingV1.BindEmbeddedAsync(
            project,
            temporary.PathFor("missing.sf2")));

        Assert.Null(project.SoundFont.Reference);
        Assert.Equal(nextStableId, project.NextStableId);
    }

    [Fact]
    public async Task RuntimeResourceChangeFailsSaveAtomically()
    {
        using TemporaryDirectory temporary = new();
        string sourcePath = temporary.PathFor("Piano.sf2");
        string targetPath = temporary.PathFor("existing.midora");
        await File.WriteAllBytesAsync(sourcePath, CreateContent(512 * 1024));
        byte[] originalTarget = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(targetPath, originalTarget);
        MidoraProject project = new(480, SavedAt);
        await using EmbeddedSoundFontResourceV1 resource =
            await SoundFontBindingV1.BindEmbeddedAsync(project, sourcePath);
        await File.WriteAllBytesAsync(resource.ResolvedAbsolutePath!, [9, 8, 7]);

        MidoraEmbeddedSoundFontRepairRequiredExceptionV1 failure =
            await Assert.ThrowsAsync<MidoraEmbeddedSoundFontRepairRequiredExceptionV1>(() =>
                CreateService().SaveProjectAsync(
                    project,
                    targetPath,
                    overwriteAuthorized: true,
                    embeddedSoundFontResource: resource));

        Assert.Equal(MidoraPackageStageV1.Staging, failure.Stage);
        Assert.Equal(EmbeddedSoundFontResourceStatusV1.SizeMismatch, failure.ResourceStatus);
        Assert.Equal(3L, failure.ActualFileSizeBytes);
        Assert.Equal(originalTarget, await File.ReadAllBytesAsync(targetPath));
        Assert.Empty(Directory.GetFileSystemEntries(temporary.Path, ".midora-save-*"));
        Assert.Empty(Directory.GetFileSystemEntries(temporary.Path, ".*.midora-temp-*"));
        Assert.Empty(Directory.GetFileSystemEntries(temporary.Path, ".*.midora-backup-*"));
    }

    [Fact]
    public async Task CorruptEmbeddedBytesOpenAsUnavailableWithoutModifyingProject()
    {
        using TemporaryDirectory temporary = new();
        string sourcePath = temporary.PathFor("Piano.sf2");
        string packagePath = temporary.PathFor("corrupt.midora");
        byte[] bytes = CreateContent(256 * 1024);
        await File.WriteAllBytesAsync(sourcePath, bytes);
        MidoraProject project = new(480, SavedAt);
        await using EmbeddedSoundFontResourceV1 imported =
            await SoundFontBindingV1.BindEmbeddedAsync(project, sourcePath);
        EmbeddedProjectSoundFontReference reference =
            (EmbeddedProjectSoundFontReference)project.SoundFont.Reference!;
        await CreateService().SaveCopyAsync(
            project,
            packagePath,
            embeddedSoundFontResource: imported);
        bytes[bytes.Length / 2] ^= 0x7f;
        ReplaceEntry(
            packagePath,
            $"resources/soundfonts/{reference.ResourceId}.sf2",
            bytes);

        await using MidoraProjectOpenResultV1 opened = await CreateService().OpenAsync(packagePath);

        Assert.False(opened.IsModified);
        MidoraPackageDiagnosticV1 diagnostic = Assert.Single(opened.Diagnostics);
        Assert.Equal(MidoraPackageDiagnosticSeverityV1.Error, diagnostic.Severity);
        Assert.Equal(MidoraPackageDiagnosticCategoryV1.Resource, diagnostic.Category);
        Assert.Equal("MIDORA-PERSIST-EMBEDDED-SF2-DAMAGED", diagnostic.Code);
        EmbeddedSoundFontResourceV1 damaged = Assert.IsType<EmbeddedSoundFontResourceV1>(
            opened.EmbeddedSoundFontResource);
        Assert.Equal(EmbeddedSoundFontResourceStatusV1.HashMismatch, damaged.Status);
        Assert.False(damaged.IsAvailable);
        Assert.Null(damaged.ResolvedAbsolutePath);
        Assert.Equal(reference, opened.Project.SoundFont.Reference);

        string rejectedCopy = temporary.PathFor("rejected-copy.midora");
        MidoraEmbeddedSoundFontRepairRequiredExceptionV1 saveFailure =
            await Assert.ThrowsAsync<MidoraEmbeddedSoundFontRepairRequiredExceptionV1>(() =>
                CreateService().SaveCopyAsync(
                    opened.Project,
                    rejectedCopy,
                    opened.FileInformation,
                    embeddedSoundFontResource: damaged));
        Assert.Equal(MidoraPackageStageV1.Preflight, saveFailure.Stage);
        Assert.Equal(EmbeddedSoundFontResourceStatusV1.HashMismatch, saveFailure.ResourceStatus);
        Assert.False(File.Exists(rejectedCopy));
    }

    [Fact]
    public async Task ManifestAndSettingsEmbeddedHashesMustMatchBeforeExtraction()
    {
        using TemporaryDirectory temporary = new();
        string sourcePath = temporary.PathFor("Piano.sf2");
        string packagePath = temporary.PathFor("manifest-mismatch.midora");
        await File.WriteAllBytesAsync(sourcePath, CreateContent(48 * 1024));
        MidoraProject project = new(480, SavedAt);
        await using EmbeddedSoundFontResourceV1 imported =
            await SoundFontBindingV1.BindEmbeddedAsync(project, sourcePath);
        EmbeddedProjectSoundFontReference reference =
            (EmbeddedProjectSoundFontReference)project.SoundFont.Reference!;
        await CreateService().SaveCopyAsync(
            project,
            packagePath,
            embeddedSoundFontResource: imported);
        string resourcePath = $"resources/soundfonts/{reference.ResourceId}.sf2";
        RewriteManifestResourceHash(packagePath, resourcePath, new string('b', 64));

        await using MidoraProjectOpenResultV1 opened = await CreateService().OpenAsync(packagePath);

        Assert.False(opened.IsModified);
        Assert.Equal(EmbeddedSoundFontResourceStatusV1.HashMismatch, opened.EmbeddedSoundFontResource!.Status);
        Assert.Null(opened.EmbeddedSoundFontResource.ResolvedAbsolutePath);
        Assert.Contains(opened.Diagnostics, item => item.Code == "MIDORA-PERSIST-EMBEDDED-SF2-DAMAGED");
    }

    [Fact]
    public async Task EmbeddedUncompressedSizeMismatchIsDetectedWithoutFullExtraction()
    {
        using TemporaryDirectory temporary = new();
        string sourcePath = temporary.PathFor("Piano.sf2");
        string packagePath = temporary.PathFor("size-mismatch.midora");
        await File.WriteAllBytesAsync(sourcePath, CreateContent(48 * 1024));
        MidoraProject project = new(480, SavedAt);
        await using EmbeddedSoundFontResourceV1 imported =
            await SoundFontBindingV1.BindEmbeddedAsync(project, sourcePath);
        EmbeddedProjectSoundFontReference reference =
            (EmbeddedProjectSoundFontReference)project.SoundFont.Reference!;
        await CreateService().SaveCopyAsync(
            project,
            packagePath,
            embeddedSoundFontResource: imported);
        ReplaceEntry(
            packagePath,
            $"resources/soundfonts/{reference.ResourceId}.sf2",
            CreateContent(1_024));

        await using MidoraProjectOpenResultV1 opened = await CreateService().OpenAsync(packagePath);

        Assert.False(opened.IsModified);
        Assert.Equal(EmbeddedSoundFontResourceStatusV1.SizeMismatch, opened.EmbeddedSoundFontResource!.Status);
        Assert.Equal(1_024L, opened.EmbeddedSoundFontResource.ActualFileSizeBytes);
        Assert.Null(opened.EmbeddedSoundFontResource.ResolvedAbsolutePath);
    }

    [Theory]
    [InlineData(true, EmbeddedSoundFontResourceStatusV1.MissingManifestEntry)]
    [InlineData(false, EmbeddedSoundFontResourceStatusV1.MissingPackageEntry)]
    public async Task MissingEmbeddedResourceIndexOrEntryIsIsolated(
        bool removeManifestRecord,
        EmbeddedSoundFontResourceStatusV1 expectedStatus)
    {
        using TemporaryDirectory temporary = new();
        string sourcePath = temporary.PathFor("Piano.sf2");
        string packagePath = temporary.PathFor("missing.midora");
        await File.WriteAllBytesAsync(sourcePath, CreateContent(64 * 1024));
        MidoraProject project = new(480, SavedAt);
        await using EmbeddedSoundFontResourceV1 imported =
            await SoundFontBindingV1.BindEmbeddedAsync(project, sourcePath);
        EmbeddedProjectSoundFontReference reference =
            (EmbeddedProjectSoundFontReference)project.SoundFont.Reference!;
        await CreateService().SaveCopyAsync(
            project,
            packagePath,
            embeddedSoundFontResource: imported);
        string resourcePath = $"resources/soundfonts/{reference.ResourceId}.sf2";
        if (removeManifestRecord)
        {
            RemoveManifestRecord(packagePath, resourcePath);
        }
        else
        {
            DeleteEntry(packagePath, resourcePath);
        }

        await using MidoraProjectOpenResultV1 opened = await CreateService().OpenAsync(packagePath);

        Assert.False(opened.IsModified);
        Assert.Contains(opened.Diagnostics, item => item.Code == "MIDORA-PERSIST-EMBEDDED-SF2-DAMAGED");
        Assert.Equal(expectedStatus, opened.EmbeddedSoundFontResource!.Status);
    }

    [Fact]
    public async Task UnreferencedEmbeddedResourceIsReportedAndRemovedOnSaveCopy()
    {
        using TemporaryDirectory temporary = new();
        string sourcePath = temporary.PathFor("Piano.sf2");
        string sourcePackage = temporary.PathFor("source.midora");
        string copyPackage = temporary.PathFor("copy.midora");
        await File.WriteAllBytesAsync(sourcePath, CreateContent(32 * 1024));
        MidoraProject project = new(480, SavedAt);
        await using EmbeddedSoundFontResourceV1 imported =
            await SoundFontBindingV1.BindEmbeddedAsync(project, sourcePath);
        EmbeddedProjectSoundFontReference reference =
            (EmbeddedProjectSoundFontReference)project.SoundFont.Reference!;
        await CreateService().SaveCopyAsync(
            project,
            sourcePackage,
            embeddedSoundFontResource: imported);
        RewriteSoundFontSettingsAsNone(sourcePackage);

        await using MidoraProjectOpenResultV1 opened = await CreateService().OpenAsync(sourcePackage);

        Assert.False(opened.IsModified);
        Assert.Null(opened.Project.SoundFont.Reference);
        Assert.Null(opened.EmbeddedSoundFontResource);
        MidoraPackageDiagnosticV1 diagnostic = Assert.Single(opened.Diagnostics);
        Assert.Equal("MIDORA-PERSIST-INFO-ORPHAN-RESOURCE", diagnostic.Code);
        await CreateService().SaveCopyAsync(opened.Project, copyPackage, opened.FileInformation);
        using ZipArchive copy = ZipFile.OpenRead(copyPackage);
        Assert.DoesNotContain(
            copy.Entries,
            entry => entry.FullName == $"resources/soundfonts/{reference.ResourceId}.sf2");
    }

    private static MidoraProjectPackageV1 CreateService() =>
        new("0.1.0-test", new FixedTimeProvider(SavedAt));

    private static byte[] CreateContent(int length)
    {
        byte[] result = new byte[length];
        for (int index = 0; index < result.Length; index++)
        {
            result[index] = (byte)((index * 37 + index / 251) & 0xff);
        }
        return result;
    }

    private static ManifestJsonV1 ReadManifest(ZipArchive archive)
    {
        using Stream stream = archive.GetEntry("manifest.json")!.Open();
        using MemoryStream bytes = new();
        stream.CopyTo(bytes);
        return ManifestCodecV1.Parse(bytes.ToArray());
    }

    private static void ReplaceEntry(string packagePath, string entryName, byte[] bytes)
    {
        using ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Update);
        archive.GetEntry(entryName)!.Delete();
        ZipArchiveEntry replacement = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using Stream output = replacement.Open();
        output.Write(bytes);
    }

    private static void DeleteEntry(string packagePath, string entryName)
    {
        using ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Update);
        archive.GetEntry(entryName)!.Delete();
    }

    private static void RemoveManifestRecord(string packagePath, string resourcePath)
    {
        using ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Update);
        ManifestJsonV1 manifest = ReadManifest(archive);
        archive.GetEntry("manifest.json")!.Delete();
        ManifestJsonV1 replacement = new()
        {
            Magic = manifest.Magic,
            FileFormatVersion = manifest.FileFormatVersion,
            MinimumReadableVersion = manifest.MinimumReadableVersion,
            ManifestSchemaVersion = manifest.ManifestSchemaVersion,
            CreatedWithSoftwareVersion = manifest.CreatedWithSoftwareVersion,
            LastSavedWithSoftwareVersion = manifest.LastSavedWithSoftwareVersion,
            Files = manifest.Files.Where(item => item.Path != resourcePath).ToArray()
        };
        ZipArchiveEntry entry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
        using Stream output = entry.Open();
        output.Write(ManifestCodecV1.Serialize(replacement));
    }

    private static void RewriteManifestResourceHash(
        string packagePath,
        string resourcePath,
        string sha256)
    {
        using ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Update);
        ManifestJsonV1 manifest = ReadManifest(archive);
        archive.GetEntry("manifest.json")!.Delete();
        ManifestJsonV1 replacement = new()
        {
            Magic = manifest.Magic,
            FileFormatVersion = manifest.FileFormatVersion,
            MinimumReadableVersion = manifest.MinimumReadableVersion,
            ManifestSchemaVersion = manifest.ManifestSchemaVersion,
            CreatedWithSoftwareVersion = manifest.CreatedWithSoftwareVersion,
            LastSavedWithSoftwareVersion = manifest.LastSavedWithSoftwareVersion,
            Files = manifest.Files.Select(item => item.Path == resourcePath
                ? new ManifestFileEntryJsonV1
                {
                    Path = item.Path,
                    Kind = item.Kind,
                    SchemaVersion = item.SchemaVersion,
                    Sha256 = sha256
                }
                : item).ToArray()
        };
        ZipArchiveEntry entry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
        using Stream output = entry.Open();
        output.Write(ManifestCodecV1.Serialize(replacement));
    }

    private static void RewriteSoundFontSettingsAsNone(string packagePath)
    {
        byte[] settings = "{\n  \"schemaVersion\": 1,\n  \"mode\": \"none\"\n}\n"u8.ToArray();
        using ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Update);
        ManifestJsonV1 manifest = ReadManifest(archive);
        archive.GetEntry("settings/soundfont-settings.json")!.Delete();
        ZipArchiveEntry settingsEntry = archive.CreateEntry(
            "settings/soundfont-settings.json",
            CompressionLevel.Optimal);
        using (Stream output = settingsEntry.Open())
        {
            output.Write(settings);
        }
        archive.GetEntry("manifest.json")!.Delete();
        ManifestJsonV1 replacement = new()
        {
            Magic = manifest.Magic,
            FileFormatVersion = manifest.FileFormatVersion,
            MinimumReadableVersion = manifest.MinimumReadableVersion,
            ManifestSchemaVersion = manifest.ManifestSchemaVersion,
            CreatedWithSoftwareVersion = manifest.CreatedWithSoftwareVersion,
            LastSavedWithSoftwareVersion = manifest.LastSavedWithSoftwareVersion,
            Files = manifest.Files.Select(item => item.Path == "settings/soundfont-settings.json"
                ? new ManifestFileEntryJsonV1
                {
                    Path = item.Path,
                    Kind = item.Kind,
                    SchemaVersion = item.SchemaVersion,
                    Sha256 = Convert.ToHexStringLower(SHA256.HashData(settings))
                }
                : item).ToArray()
        };
        ZipArchiveEntry manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
        using Stream manifestOutput = manifestEntry.Open();
        manifestOutput.Write(ManifestCodecV1.Serialize(replacement));
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"midora-embedded-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string PathFor(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
