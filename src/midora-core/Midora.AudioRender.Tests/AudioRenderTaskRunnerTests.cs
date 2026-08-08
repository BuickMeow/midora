using Midora.Audio;
using Midora.AudioDevice;
using Midora.AudioDevice.Wave;
using Midora.Compiler;
using Midora.Domain;
using Midora.Persistence;
using System.Runtime.InteropServices;

namespace Midora.AudioRender.Tests;

public sealed class AudioRenderTaskRunnerTests
{
    [Theory]
    [InlineData(8_000)]
    [InlineData(44_100)]
    [InlineData(48_000)]
    [InlineData(50_123)]
    [InlineData(192_000)]
    public async Task WholeMixPublishesValidatedFloatWaveAtEveryRequiredSampleRate(int sampleRate)
    {
        await using TestContext context = await TestContext.CreateAsync(
            AudioRenderMode.WholeMix,
            sampleRate,
            ("Track", 192, 60));
        FakeAudioFileRenderWorker worker = new();

        AudioRenderTaskResult result = await new AudioRenderTaskRunner(worker).ExecuteAsync(
            context.CreateRequest());

        AudioRenderOutputResult output = Assert.Single(result.Outputs);
        Assert.Equal(AudioRenderTaskStatus.Completed, result.Status);
        Assert.Equal(AudioRenderOutputStatus.Succeeded, output.Status);
        Assert.True(File.Exists(output.Target.FullPath));
        WaveFileSize validated = WaveFileValidation.ValidateInitialReleaseFile(
            output.Target.FullPath,
            sampleRate,
            output.FrameCount);
        Assert.Equal(validated.FileByteCount, output.FileByteCount);
        Assert.Equal(1, worker.PrepareCount);
        Assert.Equal(1, worker.RenderCount);
        Assert.Equal(sampleRate, worker.LastPreparation!.SampleRate);
        Assert.Equal(500, worker.LastPreparation.MaximumSampleVoicesPerUnitStream);
        Assert.Equal(-0.1f, worker.LastPreparation.MasterVolumeDecibels);
    }

    [Fact]
    public async Task FormalRunnerForwardsProjectSessionUnitCacheToOfflineWorker()
    {
        await using TestContext context = await TestContext.CreateAsync(
            AudioRenderMode.WholeMix,
            48_000,
            ("Track", 192, 60));
        FakeAudioFileRenderWorker worker = new();
        StubCache cache = new();

        AudioRenderTaskResult result = await new AudioRenderTaskRunner(worker).ExecuteAsync(
            context.CreateRequest(),
            cache);

        Assert.Equal(AudioRenderTaskStatus.Completed, result.Status);
        Assert.Same(cache, worker.LastRequest!.AudioCache);
    }

    [Fact]
    public async Task ProgressReportsPreparingRenderingAndFinalizingWithMonotonicElapsedTime()
    {
        await using TestContext context = await TestContext.CreateAsync(
            AudioRenderMode.WholeMix,
            48_000,
            ("Track", 192, 60));
        List<AudioRenderTaskProgress> updates = [];
        InlineProgress<AudioRenderTaskProgress> progress = new(updates.Add);

        AudioRenderTaskResult result = await new AudioRenderTaskRunner(
            new FakeAudioFileRenderWorker()).ExecuteAsync(
                context.CreateRequest(),
                progress);

        Assert.Equal(AudioRenderTaskStatus.Completed, result.Status);
        Assert.Contains(updates, value => value.Status == AudioRenderTaskStatus.Preparing);
        Assert.Contains(updates, value => value.Status == AudioRenderTaskStatus.Rendering);
        Assert.Contains(updates, value => value.Status == AudioRenderTaskStatus.Finalizing);
        Assert.True(updates.Zip(updates.Skip(1), (left, right) => left.Elapsed <= right.Elapsed).All(value => value));
        Assert.All(updates, value => Assert.True(value.Elapsed <= result.Elapsed));
    }

    [Fact]
    public async Task PerTrackKeepsSuccessfulFilesAndContinuesAfterOneRenderFailure()
    {
        await using TestContext context = await TestContext.CreateAsync(
            AudioRenderMode.PerLogicalTrack,
            48_000,
            ("One", 192, 60),
            ("Two", 192, 62),
            ("Three", 192, 64));
        FakeAudioFileRenderWorker worker = new() { FailRenderCalls = { 2 } };

        AudioRenderTaskResult result = await new AudioRenderTaskRunner(worker).ExecuteAsync(
            context.CreateRequest());

        Assert.True(
            result.Status == AudioRenderTaskStatus.CompletedWithErrors,
            string.Join(" | ", result.Diagnostics.Select(value => $"{value.Code}:{value.Message}"))
                + " outputs="
                + string.Join(",", result.Outputs.Select(value => value.Status)));
        Assert.Equal(2, result.SuccessCount);
        Assert.Equal(1, result.FailureCount);
        Assert.Equal(3, worker.RenderCount);
        Assert.Collection(
            result.Outputs,
            value => Assert.Equal(AudioRenderOutputStatus.Succeeded, value.Status),
            value => Assert.Equal(AudioRenderOutputStatus.Failed, value.Status),
            value => Assert.Equal(AudioRenderOutputStatus.Succeeded, value.Status));
        Assert.True(File.Exists(result.Outputs[0].Target.FullPath));
        Assert.False(File.Exists(result.Outputs[1].Target.FullPath));
        Assert.True(File.Exists(result.Outputs[2].Target.FullPath));
    }

    [Fact]
    public async Task PerTrackCompileFailureDoesNotPreventOtherTrackOutput()
    {
        MidoraProject project = AudioRenderTestProject.Create(
            ("Good", 192, 60),
            ("Bad", 192, 62));
        project.Tracks[1].Segments[0].Notes[0].Velocity = 200;
        await using TestContext context = await TestContext.CreateAsync(
            project,
            AudioRenderMode.PerLogicalTrack,
            48_000,
            endTick: 192);
        FakeAudioFileRenderWorker worker = new();

        AudioRenderTaskResult result = await new AudioRenderTaskRunner(worker).ExecuteAsync(
            context.CreateRequest());

        Assert.True(
            result.Status == AudioRenderTaskStatus.CompletedWithErrors,
            string.Join(" | ", result.Diagnostics.Select(value => $"{value.Code}:{value.Message}"))
                + " outputs="
                + string.Join(",", result.Outputs.Select(value => value.Status)));
        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(1, result.FailureCount);
        Assert.Equal(1, worker.RenderCount);
        Assert.Contains(result.Outputs.Single(value => value.Status == AudioRenderOutputStatus.Failed)
            .CompilerDiagnostics, value => value.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task ExistingTargetBlocksBeforeWorkerWithoutOverwriteAuthorization()
    {
        await using TestContext context = await TestContext.CreateAsync(
            AudioRenderMode.WholeMix,
            48_000,
            ("Track", 192, 60));
        File.WriteAllText(Assert.Single(context.OutputPlan.Targets).FullPath, "old");
        context.Replan();
        FakeAudioFileRenderWorker worker = new();

        AudioRenderTaskResult result = await new AudioRenderTaskRunner(worker).ExecuteAsync(
            context.CreateRequest(overwriteAuthorized: false));

        Assert.Equal(AudioRenderTaskStatus.Failed, result.Status);
        Assert.Equal(0, worker.PrepareCount);
        Assert.Equal("old", File.ReadAllText(Assert.Single(context.OutputPlan.Targets).FullPath));
    }

    [Fact]
    public async Task AuthorizedOverwriteAtomicallyReplacesExistingTarget()
    {
        await using TestContext context = await TestContext.CreateAsync(
            AudioRenderMode.WholeMix,
            48_000,
            ("Track", 192, 60));
        string target = Assert.Single(context.OutputPlan.Targets).FullPath;
        File.WriteAllText(target, "old");
        context.Replan();
        FakeAudioFileRenderWorker worker = new();

        AudioRenderTaskResult result = await new AudioRenderTaskRunner(worker).ExecuteAsync(
            context.CreateRequest(overwriteAuthorized: true));

        Assert.Equal(AudioRenderTaskStatus.Completed, result.Status);
        Assert.NotEqual("old", File.ReadAllText(target));
        Assert.Empty(Directory.EnumerateFiles(context.OutputDirectory, "*.tmp"));
    }

    [Fact]
    public async Task TargetAppearingAfterFreezeIsNeverSilentlyOverwritten()
    {
        await using TestContext context = await TestContext.CreateAsync(
            AudioRenderMode.WholeMix,
            48_000,
            ("Track", 192, 60));
        string target = Assert.Single(context.OutputPlan.Targets).FullPath;
        File.WriteAllText(target, "appeared");
        FakeAudioFileRenderWorker worker = new();

        AudioRenderTaskResult result = await new AudioRenderTaskRunner(worker).ExecuteAsync(
            context.CreateRequest(overwriteAuthorized: true));

        Assert.Equal(AudioRenderTaskStatus.Failed, result.Status);
        Assert.Equal(1, worker.PrepareCount);
        Assert.Equal(0, worker.RenderCount);
        Assert.Equal("appeared", File.ReadAllText(target));
    }

    [Fact]
    public async Task CancellationRetainsCompletedOutputDeletesCurrentAndDoesNotStartLaterTracks()
    {
        using CancellationTokenSource cancellation = new();
        await using TestContext context = await TestContext.CreateAsync(
            AudioRenderMode.PerLogicalTrack,
            48_000,
            ("One", 192, 60),
            ("Two", 192, 62),
            ("Three", 192, 64));
        FakeAudioFileRenderWorker worker = new()
        {
            CancelOnRenderCall = 2,
            CancellationSource = cancellation
        };

        AudioRenderTaskResult result = await new AudioRenderTaskRunner(worker).ExecuteAsync(
            context.CreateRequest(),
            cancellationToken: cancellation.Token);

        Assert.Equal(AudioRenderTaskStatus.Cancelled, result.Status);
        Assert.True(result.HasCompletedOutputs);
        Assert.Equal(2, worker.RenderCount);
        Assert.Collection(
            result.Outputs,
            value => Assert.Equal(AudioRenderOutputStatus.Succeeded, value.Status),
            value => Assert.Equal(AudioRenderOutputStatus.Cancelled, value.Status),
            value => Assert.Equal(AudioRenderOutputStatus.NotStarted, value.Status));
        Assert.True(File.Exists(result.Outputs[0].Target.FullPath));
        Assert.False(File.Exists(result.Outputs[1].Target.FullPath));
        Assert.False(File.Exists(result.Outputs[2].Target.FullPath));
    }

    [Fact]
    public async Task CorruptWorkerWaveFailsValidationAndIsNotPublished()
    {
        await using TestContext context = await TestContext.CreateAsync(
            AudioRenderMode.WholeMix,
            48_000,
            ("Track", 192, 60));
        FakeAudioFileRenderWorker worker = new() { WriteCorruptWave = true };

        AudioRenderTaskResult result = await new AudioRenderTaskRunner(worker).ExecuteAsync(
            context.CreateRequest());

        AudioRenderOutputResult output = Assert.Single(result.Outputs);
        Assert.Equal(AudioRenderTaskStatus.Failed, result.Status);
        Assert.Equal(AudioRenderOutputStatus.Failed, output.Status);
        Assert.False(File.Exists(output.Target.FullPath));
        Assert.Empty(Directory.EnumerateFiles(context.OutputDirectory));
    }

    [Fact]
    public async Task PerTrackPublicationFailureIsIndependentAndLaterTrackStillRuns()
    {
        await using TestContext context = await TestContext.CreateAsync(
            AudioRenderMode.PerLogicalTrack,
            48_000,
            ("One", 192, 60),
            ("Two", 192, 62),
            ("Three", 192, 64));
        FakeAudioFileRenderWorker worker = new();
        FaultingFileOperations files = new() { FailMoveCall = 2 };

        AudioRenderTaskResult result = await new AudioRenderTaskRunner(worker, files).ExecuteAsync(
            context.CreateRequest());

        Assert.Equal(AudioRenderTaskStatus.CompletedWithErrors, result.Status);
        Assert.Equal(3, worker.RenderCount);
        Assert.Collection(
            result.Outputs,
            value => Assert.Equal(AudioRenderOutputStatus.Succeeded, value.Status),
            value => Assert.Equal(AudioRenderOutputStatus.Failed, value.Status),
            value => Assert.Equal(AudioRenderOutputStatus.Succeeded, value.Status));
        Assert.False(File.Exists(result.Outputs[1].Target.FullPath));
    }

    [Fact]
    public async Task FailedReplacementPreservesOriginalTargetAndCleansNewTemporaryFile()
    {
        await using TestContext context = await TestContext.CreateAsync(
            AudioRenderMode.WholeMix,
            48_000,
            ("Track", 192, 60));
        string target = Assert.Single(context.OutputPlan.Targets).FullPath;
        File.WriteAllText(target, "original");
        context.Replan();
        FaultingFileOperations files = new() { FailReplace = true };

        AudioRenderTaskResult result = await new AudioRenderTaskRunner(
            new FakeAudioFileRenderWorker(),
            files).ExecuteAsync(context.CreateRequest(overwriteAuthorized: true));

        Assert.Equal(AudioRenderTaskStatus.Failed, result.Status);
        Assert.Equal("original", File.ReadAllText(target));
        Assert.Single(Directory.EnumerateFiles(context.OutputDirectory));
    }

    [Fact]
    public async Task CleanupFailureReportsResidualAsInvalidWhileOutputRemainsFailed()
    {
        await using TestContext context = await TestContext.CreateAsync(
            AudioRenderMode.WholeMix,
            48_000,
            ("Track", 192, 60));
        FaultingFileOperations files = new()
        {
            FailDelete = path => path.Contains("midora-render", StringComparison.Ordinal)
        };

        AudioRenderTaskResult result = await new AudioRenderTaskRunner(
            new FakeAudioFileRenderWorker { WriteCorruptWave = true },
            files).ExecuteAsync(context.CreateRequest());

        AudioRenderOutputResult output = Assert.Single(result.Outputs);
        Assert.Equal(AudioRenderTaskStatus.Failed, result.Status);
        AudioRenderDiagnostic cleanup = Assert.Single(
            output.Diagnostics,
            value => value.Code == "MIDORA-AUDIO-RENDER-TEMP-CLEANUP");
        Assert.NotNull(cleanup.TemporaryPath);
        Assert.True(File.Exists(cleanup.TemporaryPath));
        Assert.False(File.Exists(output.Target.FullPath));
    }

    [Fact]
    public async Task BackupCleanupFailureIsWarningAfterSuccessfulAtomicReplacement()
    {
        await using TestContext context = await TestContext.CreateAsync(
            AudioRenderMode.WholeMix,
            48_000,
            ("Track", 192, 60));
        string target = Assert.Single(context.OutputPlan.Targets).FullPath;
        File.WriteAllText(target, "original");
        context.Replan();
        FaultingFileOperations files = new()
        {
            FailDelete = path => path.Contains("midora-backup", StringComparison.Ordinal)
        };

        AudioRenderTaskResult result = await new AudioRenderTaskRunner(
            new FakeAudioFileRenderWorker(),
            files).ExecuteAsync(context.CreateRequest(overwriteAuthorized: true));

        AudioRenderOutputResult output = Assert.Single(result.Outputs);
        Assert.Equal(AudioRenderTaskStatus.Completed, result.Status);
        AudioRenderDiagnostic warning = Assert.Single(
            output.Diagnostics,
            value => value.Code == "MIDORA-AUDIO-RENDER-BACKUP-CLEANUP");
        Assert.True(File.Exists(warning.TemporaryPath));
        WaveFileValidation.ValidateInitialReleaseFile(target, 48_000, output.FrameCount);
    }

    [Fact]
    public async Task RiffLimitBlocksEntireTaskBeforeDirectoryOrWorkerPreparation()
    {
        MidoraProject project = AudioRenderTestProject.Create(("Long", 192, 60));
        string root = AudioRenderTestProject.CreateOwnedDirectory();
        string missingOutput = Path.Combine(root, "output");
        await using AudioRenderSoundFontSnapshot soundFont = await CreateSoundFontAsync(project, root);
        using MidoraCompiler compiler = new();
        AudioRenderCompilationResult compilation = new AudioRenderCompilationCoordinator(compiler).Compile(new()
        {
            Project = project,
            Mode = AudioRenderMode.WholeMix,
            EndTick = 10_000_000
        });
        AudioRenderFrozenOutputPlan outputPlan = AudioRenderOutputPlanner.PlanWholeMix(
            Path.Combine(missingOutput, "too-long.wav"));
        FakeAudioFileRenderWorker worker = new();
        try
        {
            AudioRenderTaskResult result = await new AudioRenderTaskRunner(worker).ExecuteAsync(new()
            {
                Compilation = compilation,
                OutputPlan = outputPlan,
                SoundFont = soundFont,
                SampleRate = 192_000,
                MaximumSampleVoicesPerUnitStream = 500,
                MasterVolumeDecibels = -0.1
            });

            Assert.Equal(AudioRenderTaskStatus.Failed, result.Status);
            Assert.Contains(result.Diagnostics, value => value.Code == "MIDORA-AUDIO-RENDER-RIFF-LIMIT");
            Assert.Equal(0, worker.PrepareCount);
            Assert.False(Directory.Exists(missingOutput));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<AudioRenderSoundFontSnapshot> CreateSoundFontAsync(
        MidoraProject project,
        string directory)
    {
        string projectPath = Path.Combine(directory, "Project.midora");
        string sf2 = Path.Combine(directory, "Project.sf2");
        await File.WriteAllBytesAsync(sf2, Enumerable.Range(0, 1024).Select(value => (byte)value).ToArray());
        ExternalSoundFontBindingV1 binding = await SoundFontBindingV1.BindExternalAsync(projectPath, sf2);
        project.SoundFont.SetExternal(
            binding.Reference.RelativePath,
            binding.Reference.OriginalFileName,
            binding.Reference.Sha256,
            binding.Reference.FileSizeBytes);
        return await AudioRenderSoundFontSnapshot.CreateAsync(
            project,
            projectPath,
            embeddedResource: null,
            acceptExternalHashChange: false);
    }

    private sealed class TestContext : IAsyncDisposable
    {
        private readonly string _projectPath;
        private readonly AudioRenderMode _mode;

        private TestContext(
            string rootDirectory,
            string projectPath,
            MidoraProject project,
            AudioRenderMode mode,
            int sampleRate,
            AudioRenderCompilationResult compilation,
            AudioRenderFrozenOutputPlan outputPlan,
            AudioRenderSoundFontSnapshot soundFont)
        {
            RootDirectory = rootDirectory;
            _projectPath = projectPath;
            Project = project;
            _mode = mode;
            SampleRate = sampleRate;
            Compilation = compilation;
            OutputPlan = outputPlan;
            SoundFont = soundFont;
        }

        public string RootDirectory { get; }
        public string OutputDirectory => Path.Combine(RootDirectory, "output");
        public MidoraProject Project { get; }
        public int SampleRate { get; }
        public AudioRenderCompilationResult Compilation { get; }
        public AudioRenderFrozenOutputPlan OutputPlan { get; private set; }
        public AudioRenderSoundFontSnapshot SoundFont { get; }

        public static Task<TestContext> CreateAsync(
            AudioRenderMode mode,
            int sampleRate,
            params (string Name, long LengthTicks, byte Note)[] tracks) =>
            CreateAsync(
                AudioRenderTestProject.Create(tracks),
                mode,
                sampleRate,
                endTick: tracks.Max(value => value.LengthTicks));

        public static async Task<TestContext> CreateAsync(
            MidoraProject project,
            AudioRenderMode mode,
            int sampleRate,
            long endTick)
        {
            string root = AudioRenderTestProject.CreateOwnedDirectory();
            string projectPath = Path.Combine(root, "Project.midora");
            AudioRenderSoundFontSnapshot soundFont = await CreateSoundFontAsync(project, root);
            using MidoraCompiler compiler = new();
            AudioRenderCompilationResult compilation = new AudioRenderCompilationCoordinator(compiler).Compile(new()
            {
                Project = project,
                Mode = mode,
                EndTick = endTick
            });
            string outputDirectory = Path.Combine(root, "output");
            Directory.CreateDirectory(outputDirectory);
            AudioRenderFrozenOutputPlan outputPlan = mode == AudioRenderMode.WholeMix
                ? AudioRenderOutputPlanner.PlanWholeMix(Path.Combine(outputDirectory, "mix.wav"))
                : AudioRenderOutputPlanner.PlanLogicalTracks(
                    outputDirectory,
                    compilation.Tracks,
                    project.Tracks.Count);
            return new(
                root,
                projectPath,
                project,
                mode,
                sampleRate,
                compilation,
                outputPlan,
                soundFont);
        }

        public void Replan()
        {
            OutputPlan = _mode == AudioRenderMode.WholeMix
                ? AudioRenderOutputPlanner.PlanWholeMix(Assert.Single(OutputPlan.Targets).FullPath)
                : AudioRenderOutputPlanner.PlanLogicalTracks(
                    OutputDirectory,
                    Compilation.Tracks,
                    Project.Tracks.Count);
        }

        public AudioRenderTaskRequest CreateRequest(bool overwriteAuthorized = false) => new()
        {
            Compilation = Compilation,
            OutputPlan = OutputPlan,
            SoundFont = SoundFont,
            SampleRate = SampleRate,
            MaximumSampleVoicesPerUnitStream = 500,
            MasterVolumeDecibels = -0.1,
            OverwriteAuthorized = overwriteAuthorized
        };

        public ValueTask DisposeAsync()
        {
            SoundFont.Dispose();
            Directory.Delete(RootDirectory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed unsafe class FakeAudioFileRenderWorker : IAudioFileRenderWorker
    {
        public int PrepareCount { get; private set; }
        public int RenderCount { get; private set; }
        public AudioFileRenderWorkerPreparation? LastPreparation { get; private set; }
        public AudioFileRenderWorkerRequest? LastRequest { get; private set; }
        public HashSet<int> FailRenderCalls { get; } = [];
        public int CancelOnRenderCall { get; init; }
        public CancellationTokenSource? CancellationSource { get; init; }
        public bool WriteCorruptWave { get; init; }

        public Task PrepareAsync(
            AudioFileRenderWorkerPreparation preparation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrepareCount++;
            LastPreparation = preparation;
            return Task.CompletedTask;
        }

        public Task<AudioFileRenderWorkerResult> RenderAsync(
            AudioFileRenderWorkerRequest request,
            IProgress<AudioFileRenderWorkerProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            RenderCount++;
            LastRequest = request;
            if (RenderCount == CancelOnRenderCall)
            {
                CancellationSource!.Cancel();
                throw new AudioFileRenderCancelledException(CancellationSource.Token);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (FailRenderCalls.Contains(RenderCount))
            {
                throw new MidoraAudioException("Injected render failure.");
            }
            if (WriteCorruptWave)
            {
                File.WriteAllBytes(request.TemporaryOutputPath, "not-wave"u8.ToArray());
                return Task.FromResult(new AudioFileRenderWorkerResult(
                    request.Plan.TotalFrameCount,
                    8,
                    0));
            }

            progress?.Report(new(
                AudioWorkerState.Rendering,
                0,
                request.Plan.TotalFrameCount));
            SilentSource source = new(request.Plan.SampleRate, request.Plan.TotalFrameCount);
            WaveFileRenderResult rendered = WaveFileOutput.Render(
                source,
                request.Plan.TotalFrameCount,
                request.TemporaryOutputPath,
                workFrameCount: 37,
                overwrite: false,
                cancellationToken);
            progress?.Report(new(
                AudioWorkerState.Finalizing,
                rendered.FrameCount,
                request.Plan.TotalFrameCount));
            progress?.Report(new(
                AudioWorkerState.Completed,
                rendered.FrameCount,
                request.Plan.TotalFrameCount));
            return Task.FromResult(new AudioFileRenderWorkerResult(
                rendered.FrameCount,
                rendered.FileByteCount,
                rendered.RenderingThreadAllocatedBytes));
        }

        private sealed unsafe class SilentSource(int sampleRate, long totalFrames) : IAudioRenderSource
        {
            private long _position;

            public AudioFormat Format { get; } = new(sampleRate, 2, AudioSampleFormat.Float32);

            public AudioPullResult PullFrames(float* destination, int requestedFrameCount)
            {
                int count = (int)Math.Min(requestedFrameCount, totalFrames - _position);
                NativeMemory.Clear(destination, checked((nuint)count * 2 * sizeof(float)));
                _position += count;
                return _position == totalFrames
                    ? AudioPullResult.EndOfStream(count)
                    : AudioPullResult.Continue(count);
            }
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class FaultingFileOperations : IAudioRenderFileOperations
    {
        private readonly AudioRenderFileOperations _inner = new();
        private int _moveCount;

        public int FailMoveCall { get; init; }
        public bool FailReplace { get; init; }
        public Func<string, bool>? FailDelete { get; init; }

        public bool FileExists(string path) => _inner.FileExists(path);
        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
        public void CreateDirectory(string path) => _inner.CreateDirectory(path);
        public string CreateTemporaryPath(string finalPath) => _inner.CreateTemporaryPath(finalPath);
        public string CreateBackupPath(string finalPath) => _inner.CreateBackupPath(finalPath);

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
        {
            _moveCount++;
            if (_moveCount == FailMoveCall)
            {
                throw new IOException("Injected publication move failure.");
            }
            _inner.MoveFile(sourcePath, destinationPath, overwrite);
        }

        public void ReplaceFile(string sourcePath, string destinationPath, string backupPath)
        {
            if (FailReplace)
            {
                throw new IOException("Injected atomic replacement failure.");
            }
            _inner.ReplaceFile(sourcePath, destinationPath, backupPath);
        }

        public void DeleteFile(string path)
        {
            if (FailDelete?.Invoke(path) == true)
            {
                throw new IOException("Injected cleanup failure.");
            }
            _inner.DeleteFile(path);
        }
    }

    private sealed class StubCache : IAudioPcmCacheSessionAccess
    {
        public AudioCacheSessionSnapshot? AudioCacheSnapshot => null;

        public bool TryCopyReusableAudio(
            string key,
            Stream destination,
            out long payloadLength)
        {
            payloadLength = 0;
            return false;
        }

        public AudioCachePublishResult PublishReusableAudio(
            string key,
            Stream source,
            long payloadLength) => default;

        public void InvalidateReusableAudio(string key)
        {
        }

        public AudioCacheSessionStore.AudioRecoverySpool CreateTransientAudioSpool(
            long lengthBytes) => throw new NotSupportedException();

        public void DisableReusableAudioRetention(string reason)
        {
        }
    }
}
