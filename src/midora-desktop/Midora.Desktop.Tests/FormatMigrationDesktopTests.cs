using System.IO.Compression;
using System.Text;
using Midora.Application;
using Midora.Domain;
using Midora.Persistence;
using Xunit;

namespace Midora.Desktop.Tests;

[Collection(DesktopSharedPresentationStateCollection.Name)]
public sealed class FormatMigrationDesktopTests
{
    [Fact]
    public async Task DesktopOpenKeepsTheMigratedFormatOneSourceProtected()
    {
        using TemporaryDirectory temporary = new();
        string legacyPath = Path.Combine(temporary.Path, "legacy.midora");
        using MidoraProject project = new(192);
        MidoraProjectPackageV1 packages = new("1.0.0-dev-test");
        await packages.SaveCopyAsync(project, legacyPath);
        DowngradeStructureOnlyPackageToFormatOne(legacyPath);

        await using DesktopSessionController session = new();
        await session.OpenProjectAsync(legacyPath);

        ProjectPersistenceCoordinator persistence = Assert.IsType<ProjectPersistenceCoordinator>(
            session.Persistence);
        Assert.Null(persistence.CurrentProjectPath);
        Assert.Equal(Path.GetFullPath(legacyPath), persistence.ProtectedSourceProjectPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            persistence.SaveProjectAsync(legacyPath, overwriteAuthorized: true));
    }

    private static void DowngradeStructureOnlyPackageToFormatOne(string path)
    {
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Update);
        ZipArchiveEntry manifest = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("The test package has no manifest.");
        string json;
        using (StreamReader reader = new(manifest.Open(), Encoding.UTF8, leaveOpen: false))
        {
            json = reader.ReadToEnd();
        }
        json = json
            .Replace("\"fileFormatVersion\": 2", "\"fileFormatVersion\": 1", StringComparison.Ordinal)
            .Replace("\"minimumReadableVersion\": 2", "\"minimumReadableVersion\": 1", StringComparison.Ordinal)
            .Replace("\"manifestSchemaVersion\": 2", "\"manifestSchemaVersion\": 1", StringComparison.Ordinal);
        manifest.Delete();
        using Stream output = archive.CreateEntry("manifest.json").Open();
        output.Write(Encoding.UTF8.GetBytes(json));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "midora-format-migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
