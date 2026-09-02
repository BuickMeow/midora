using System.Text;
using Midora.Common;

namespace Midora.Common.Tests;

public sealed class MidoraOwnedTemporaryDirectoryLeaseTests
{
    [Fact]
    public void CleanupDeletesOnlyManifestedInactiveDirectChildren()
    {
        using TemporaryDirectory root = new();
        string staleName = "compiler-run-" + Guid.NewGuid().ToString("N");
        string stale = Path.Combine(root.Path, staleName);
        Directory.CreateDirectory(stale);
        File.WriteAllText(
            Path.Combine(stale, "midora-temp.manifest"),
            "MIDORA_OWNED_TEMPORARY_V1\n" + staleName + "\ncompiler-run\n",
            new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(stale, "midora-temp.active.lock"), string.Empty);
        string unknown = Path.Combine(root.Path, "user-content");
        Directory.CreateDirectory(unknown);
        File.WriteAllText(Path.Combine(unknown, "keep.txt"), "keep");

        int removed = MidoraOwnedTemporaryDirectoryLease.ClearInactiveDirectories(root.Path);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(unknown));
    }

    [Fact]
    public void ActiveLeaseSurvivesCleanupAndDisposeRemovesIt()
    {
        using TemporaryDirectory root = new();
        string path;
        using (MidoraOwnedTemporaryDirectoryLease lease =
            MidoraOwnedTemporaryDirectoryLease.Create(root.Path, "audio-worker"))
        {
            path = lease.DirectoryPath;
            Assert.Equal(0, MidoraOwnedTemporaryDirectoryLease.ClearInactiveDirectories(root.Path));
            Assert.True(Directory.Exists(path));
        }

        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void RelativeRootsAndInvalidPurposesAreRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            MidoraOwnedTemporaryDirectoryLease.Create("relative", "compiler-run"));
        using TemporaryDirectory root = new();
        Assert.Throws<ArgumentException>(() =>
            MidoraOwnedTemporaryDirectoryLease.Create(root.Path, "Compiler_Run"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "midora-owned-temp-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
