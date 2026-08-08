using ConsoleProgram = Midora.Audio.Bass.Tests.Console.Program;
using System.Runtime.Versioning;

namespace Midora.Audio.Bass.Tests;

[SupportedOSPlatform("windows")]
public sealed class ManualAudioWorkerPathTests
{
    [Fact]
    public void ExplicitPublishedExeTakesPrecedenceOverConventionalPath()
    {
        using TemporaryDirectory temporary = new();
        string explicitPath = temporary.CreateFile("operator-worker.exe");

        string actual = ConsoleProgram.ResolveWorkerPath(
            temporary.Path,
            "Release",
            explicitPath);

        Assert.Equal(Path.GetFullPath(explicitPath), actual);
    }

    [Fact]
    public void MissingExplicitPathDoesNotFallBackToConventionalWorker()
    {
        using TemporaryDirectory temporary = new();
        _ = temporary.CreateConventionalWorker("Release");
        string missing = Path.Combine(temporary.Path, "missing-worker.exe");

        FileNotFoundException exception = Assert.Throws<FileNotFoundException>(() =>
            ConsoleProgram.ResolveWorkerPath(temporary.Path, "Release", missing));

        Assert.Equal(Path.GetFullPath(missing), exception.FileName);
    }

    [Fact]
    public void ConventionalPathTargetsRidSpecificNativeAotPublish()
    {
        using TemporaryDirectory temporary = new();
        string expected = temporary.CreateConventionalWorker("Debug");

        string actual = ConsoleProgram.ResolveWorkerPath(temporary.Path, "Debug", null);

        Assert.Equal(Path.GetFullPath(expected), actual);
    }

    [Fact]
    public void ManagedWorkerIsRejectedWithoutFallback()
    {
        using TemporaryDirectory temporary = new();
        string managedWorker = temporary.CreateFile("Midora.Audio.Bass.Worker.dll");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            ConsoleProgram.ResolveWorkerPath(temporary.Path, "Release", managedWorker));

        Assert.Contains("Native AOT Worker .exe", exception.Message, StringComparison.Ordinal);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"midora-manual-worker-path-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateConventionalWorker(string configuration) => CreateFile(
            System.IO.Path.Combine(
                "src",
                "midora-audio",
                "Midora.Audio.Bass.Worker",
                "bin",
                configuration,
                "net10.0",
                "win-x64",
                "publish",
                "Midora.Audio.Bass.Worker.exe"));

        public string CreateFile(string relativePath)
        {
            string fullPath = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, []);
            return fullPath;
        }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
