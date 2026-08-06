using System.Runtime.Versioning;
using Midora.Audio;

namespace Midora.Audio.Bass.Tests;

[SupportedOSPlatform("windows")]
public sealed class BassMidiAudioWorkerSessionPolicyTests
{
    [Fact]
    public void FormalRealtimeClientAcceptsOnlyNativeExecutableWorker()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"midora-realtime-worker-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string nativeWorker = Path.Combine(directory, "Midora.Audio.Bass.Worker.exe");
        string managedWorker = Path.Combine(directory, "Midora.Audio.Bass.Worker.dll");
        string otherWorker = Path.Combine(directory, "Midora.Audio.Bass.Worker.bin");
        File.WriteAllBytes(nativeWorker, [0]);
        File.WriteAllBytes(managedWorker, [0]);
        File.WriteAllBytes(otherWorker, [0]);
        try
        {
            BassMidiAudioWorkerSession.ValidateWorkerLaunchPath(
                nativeWorker,
                allowManagedTestWorker: false);
            Assert.Throws<InvalidDataException>(() =>
                BassMidiAudioWorkerSession.ValidateWorkerLaunchPath(
                    managedWorker,
                    allowManagedTestWorker: false));
            BassMidiAudioWorkerSession.ValidateWorkerLaunchPath(
                managedWorker,
                allowManagedTestWorker: true);
            Assert.Throws<InvalidDataException>(() =>
                BassMidiAudioWorkerSession.ValidateWorkerLaunchPath(
                    otherWorker,
                    allowManagedTestWorker: true));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(AudioWorkerState.Stopped, 0, true)]
    [InlineData(AudioWorkerState.Completed, 0, true)]
    [InlineData(AudioWorkerState.Playing, 0, false)]
    [InlineData(AudioWorkerState.Stopped, 1, false)]
    [InlineData(AudioWorkerState.Faulted, 1, false)]
    public void TerminalExitRequiresSuccessCodeAndTerminalState(
        AudioWorkerState state,
        int exitCode,
        bool expected)
    {
        Assert.Equal(
            expected,
            BassMidiAudioWorkerSession.IsSuccessfulTerminalExit(state, exitCode));
    }
}
