using System.Diagnostics;

namespace Midora.Audio.Bass.Tests;

public sealed class AudioWorkerProcessGroupTests
{
    [Fact]
    public void EmptyOrActiveProcessGroupCanBeObservedWithoutChangingAudioState()
    {
        AudioWorkerResourceSnapshot snapshot = AudioWorkerProcessGroup.CaptureResources();

        Assert.True(snapshot.ActiveProcessCount >= 0);
        Assert.True(snapshot.TotalProcessorTime >= TimeSpan.Zero);
        Assert.True(snapshot.WorkingSetBytes >= 0);
        Assert.True(snapshot.PrivateMemoryBytes >= 0);
    }

    [Fact]
    public void StartedWorkerIsTrackedUntilItExits()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string commandInterpreter = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        ProcessStartInfo startInfo = new(commandInterpreter)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("ping 127.0.0.1 -n 4 > nul");

        using Process process = AudioWorkerProcessGroup.Start(startInfo, "Test child did not start.");
        Assert.True(AudioWorkerProcessGroup.IsTracked(process.Id));
        Assert.True(AudioWorkerProcessGroup.CaptureResources().ActiveProcessCount >= 1);

        process.Kill(entireProcessTree: true);
        Assert.True(process.WaitForExit(5_000));
        Assert.True(SpinWait.SpinUntil(
            () => !AudioWorkerProcessGroup.IsTracked(process.Id),
            TimeSpan.FromSeconds(5)));
    }
}
