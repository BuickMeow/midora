using System.Diagnostics;
using System.Runtime.Versioning;
using Midora.Audio;

namespace Midora.Audio.Bass.Tests;

[SupportedOSPlatform("windows")]
public sealed class InitialReleaseAudioWorkerProtocolPolicyTests
{
    [Theory]
    [InlineData("0", false)]
    [InlineData("1", true)]
    public void ProtocolBooleanAcceptsOnlyCanonicalValues(string value, bool expected)
    {
        Assert.Equal(
            expected,
            InitialReleaseAudioWorkerProtocolPolicy.ParseBoolean(value, "test"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("2")]
    [InlineData("-1")]
    [InlineData("true")]
    public void ProtocolBooleanRejectsNonCanonicalValues(string value)
    {
        Assert.Throws<InvalidDataException>(() =>
            InitialReleaseAudioWorkerProtocolPolicy.ParseBoolean(value, "test"));
    }

    [Fact]
    public void RealtimePolicyRequiresFixedWorkBlockAndLimiterAlgorithm()
    {
        InitialReleaseAudioWorkerProtocolPolicy.ValidateRealtimeSettings(
            new BassMidiRendererSettings(750, 256),
            new AudioMasterSettings(-3, 1, 50, limiterEnabled: false),
            20,
            5);

        Assert.Throws<InvalidDataException>(() =>
            InitialReleaseAudioWorkerProtocolPolicy.ValidateRealtimeSettings(
                new BassMidiRendererSettings(750, 128),
                AudioMasterSettings.LimiterV1,
                100,
                50));
        Assert.Throws<InvalidDataException>(() =>
            InitialReleaseAudioWorkerProtocolPolicy.ValidateRealtimeSettings(
                new BassMidiRendererSettings(750, 256),
                new AudioMasterSettings(-3, 0.9f, 50),
                100,
                50));
        Assert.Throws<InvalidDataException>(() =>
            InitialReleaseAudioWorkerProtocolPolicy.ValidateRealtimeSettings(
                new BassMidiRendererSettings(750, 256),
                new AudioMasterSettings(-3, 1, 25),
                100,
                50));
    }

    [Theory]
    [InlineData(19, 50)]
    [InlineData(2_001, 50)]
    [InlineData(100, 4)]
    [InlineData(100, 201)]
    public void RealtimePolicyRejectsOutOfRangeBuffers(
        int renderAheadMilliseconds,
        int deviceBufferRequestMilliseconds)
    {
        Assert.Throws<InvalidDataException>(() =>
            InitialReleaseAudioWorkerProtocolPolicy.ValidateRealtimeSettings(
                new BassMidiRendererSettings(750, 256),
                AudioMasterSettings.LimiterV1,
                renderAheadMilliseconds,
                deviceBufferRequestMilliseconds));
    }

    [Fact]
    public void FilePolicyRequiresRateWorkBlockAndEnabledLimiterV1()
    {
        InitialReleaseAudioWorkerProtocolPolicy.ValidateFileSettings(
            new BassMidiRendererSettings(750, 256),
            AudioMasterSettings.LimiterV1,
            8_000);
        InitialReleaseAudioWorkerProtocolPolicy.ValidateFileSettings(
            new BassMidiRendererSettings(750, 256),
            AudioMasterSettings.LimiterV1,
            192_000);

        Assert.Throws<InvalidDataException>(() =>
            InitialReleaseAudioWorkerProtocolPolicy.ValidateFileSettings(
                new BassMidiRendererSettings(750, 256),
                AudioMasterSettings.LimiterV1,
                7_999));
        Assert.Throws<InvalidDataException>(() =>
            InitialReleaseAudioWorkerProtocolPolicy.ValidateFileSettings(
                new BassMidiRendererSettings(750, 128),
                AudioMasterSettings.LimiterV1,
                48_000));
        Assert.Throws<InvalidDataException>(() =>
            InitialReleaseAudioWorkerProtocolPolicy.ValidateFileSettings(
                new BassMidiRendererSettings(750, 256),
                new AudioMasterSettings(-3, 1, 50, limiterEnabled: false),
                48_000));
    }

    [Fact]
    public void PathPolicyRequiresAbsoluteExistingInputsAndFreshOutput()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"midora-worker-protocol-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string input = Path.Combine(directory, "input.bin");
        string output = Path.Combine(directory, "output.wav");
        File.WriteAllBytes(input, [1]);
        try
        {
            Assert.Equal(
                input,
                InitialReleaseAudioWorkerProtocolPolicy.RequireExistingFile(input, "input"));
            Assert.Equal(
                directory,
                InitialReleaseAudioWorkerProtocolPolicy.RequireExistingDirectory(directory, "directory"));
            Assert.Equal(
                output,
                InitialReleaseAudioWorkerProtocolPolicy.RequireNewFileTarget(output, "output"));

            Assert.Throws<InvalidDataException>(() =>
                InitialReleaseAudioWorkerProtocolPolicy.RequireExistingFile("relative.bin", "input"));
            Assert.Throws<FileNotFoundException>(() =>
                InitialReleaseAudioWorkerProtocolPolicy.RequireExistingFile(
                    Path.Combine(directory, "missing.bin"),
                    "input"));
            Assert.Throws<InvalidDataException>(() =>
                InitialReleaseAudioWorkerProtocolPolicy.RequireNewFileTarget("relative.wav", "output"));

            File.WriteAllBytes(output, [1]);
            Assert.Throws<IOException>(() =>
                InitialReleaseAudioWorkerProtocolPolicy.RequireNewFileTarget(output, "output"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ManagedWorkerPublishesFaultForRelativeFormalInputPath()
    {
        string workerPath = FindManagedWorkerPath();
        string controlName = $"Midora.Audio.Control.Test.{Guid.NewGuid():N}";
        using SharedAudioWorkerControl control = SharedAudioWorkerControl.Create(controlName);
        ProcessStartInfo startInfo = new()
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add(workerPath);
        startInfo.ArgumentList.Add("file-probe");
        startInfo.ArgumentList.Add(controlName);
        startInfo.ArgumentList.Add("relative.sf2");
        startInfo.ArgumentList.Add(Path.GetTempPath());
        startInfo.ArgumentList.Add("48000");
        startInfo.ArgumentList.Add("750");
        startInfo.ArgumentList.Add("-0.1");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("50");
        startInfo.ArgumentList.Add("1");

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the managed test Worker.");
        Task<string> error = process.StandardError.ReadToEndAsync();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        bool exited = true;
        using (CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10)))
        {
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                exited = false;
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        string standardError = await error;
        _ = await output;

        Assert.True(exited);
        Assert.Equal(1, process.ExitCode);
        Assert.Equal(AudioWorkerState.Faulted, control.ReadStatus().State);
        Assert.Contains("fully qualified", standardError, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindManagedWorkerPath()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null
            && !File.Exists(Path.Combine(current.FullName, "Directory.Build.props")))
        {
            current = current.Parent;
        }
        string repositoryRoot = current?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        return Path.Combine(
            repositoryRoot,
            "src",
            "midora-audio",
            "Midora.Audio.Bass.Worker",
            "bin",
            configuration,
            "net10.0",
            "Midora.Audio.Bass.Worker.dll");
    }
}
