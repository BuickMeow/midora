using System.Runtime.Versioning;

namespace Midora.Audio.Bass.Tests;

[SupportedOSPlatform("windows")]
public sealed class BassMidiAudioFileRenderWorkerPolicyTests
{
    [Fact]
    public void FormalClientRejectsManagedWorkerDllButTestOnlyPathCanOptIn()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"midora-file-worker-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string managedWorker = Path.Combine(directory, "Midora.Audio.Bass.Worker.dll");
        File.WriteAllBytes(managedWorker, [0]);
        try
        {
            _ = Assert.Throws<InvalidDataException>(() => new BassMidiAudioFileRenderWorker(
                managedWorker,
                directory,
                TimeSpan.FromSeconds(5)));

            _ = new BassMidiAudioFileRenderWorker(
                managedWorker,
                directory,
                TimeSpan.FromSeconds(5),
                allowManagedTestWorker: true);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FormalClientAcceptsOnlyExecutableWorkerPathAtConstruction()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"midora-file-worker-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string nativeWorker = Path.Combine(directory, "Midora.Audio.Bass.Worker.exe");
        File.WriteAllBytes(nativeWorker, [0]);
        try
        {
            _ = new BassMidiAudioFileRenderWorker(
                nativeWorker,
                directory,
                TimeSpan.FromSeconds(5));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
