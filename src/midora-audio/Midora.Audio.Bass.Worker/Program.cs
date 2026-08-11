using Midora.AudioDevice;
using Midora.AudioDevice.BassWasapi.Internals;
using Midora.AudioDevice.BassWasapi.Settings;
using Midora.AudioDevice.Wave;
using Midora.Midi;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using NativeBass = Midora.NativeInterops.Bass.BASS;
using NativeBassMidi = Midora.NativeInterops.BassMidi.BASSMIDI;
using NativeBassWasapi = Midora.NativeInterops.BassWasapi.BASSWASAPI;

namespace Midora.Audio.Bass.Worker;

[SupportedOSPlatform("windows")]
public static class Program
{
    private const int LegacyMonitoringProtocolMagic = 0x4d43444d;
    private const int LegacyMonitoringProtocolVersion = 1;
    private const int MaximumLegacyMonitoringCommandCount = 1_000_000;

    public static unsafe int Main(string[] args)
    {
        SharedAudioWorkerControl? control = null;
        try
        {
            if (args.Length == 0)
            {
                throw new ArgumentException("A Midora audio worker mode is required.");
            }

            if (string.Equals(args[0], "probe", StringComparison.Ordinal))
            {
                if (args.Length != 5)
                {
                    throw new ArgumentException("Invalid probe argument count.");
                }
                control = SharedAudioWorkerControl.Open(args[1]);
                return RunProbe(args, control);
            }

            if (string.Equals(args[0], "list-output-devices", StringComparison.Ordinal))
            {
                if (args.Length != 2)
                {
                    throw new ArgumentException("Invalid output-device enumeration argument count.");
                }
                return ListOutputDevices(args[1]);
            }

            if (string.Equals(args[0], "play", StringComparison.Ordinal))
            {
                if (args.Length != 22)
                {
                    throw new ArgumentException("Invalid playback argument count.");
                }
                control = SharedAudioWorkerControl.Open(args[1]);
                return RunPlayback(args, control);
            }

            if (string.Equals(args[0], "file-probe", StringComparison.Ordinal))
            {
                if (args.Length != 10)
                {
                    throw new ArgumentException("Invalid file-render probe argument count.");
                }
                control = SharedAudioWorkerControl.Open(args[1]);
                return RunFileProbe(args, control);
            }

            if (string.Equals(args[0], "file-render", StringComparison.Ordinal))
            {
                if (args.Length != 12)
                {
                    throw new ArgumentException("Invalid file-render argument count.");
                }
                control = SharedAudioWorkerControl.Open(args[1]);
                return RunFileRender(args, control);
            }

            // Kept only for the existing sample-equivalence harness. Formal realtime playback
            // exclusively uses the shared-control "play" mode above and never transports PCM.
            if (args.Length == 11)
            {
                return RunLegacyPcmRenderer(args);
            }

            throw new ArgumentException("Unknown Midora audio worker mode.");
        }
        catch (Exception exception)
        {
            control?.PublishFault(1);
            global::System.Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            control?.Dispose();
        }
    }

    private static int RunProbe(string[] args, SharedAudioWorkerControl control)
    {
        control.PublishState(AudioWorkerState.Preparing);
        string nativeDirectory = InitialReleaseAudioWorkerProtocolPolicy.RequireExistingDirectory(
            args[2],
            "native library");
        string? requestedDeviceId = EmptyToNull(args[3]);
        int deviceBufferRequestMilliseconds = ParseInt32(args[4]);
        if (deviceBufferRequestMilliseconds is < 5 or > 200)
        {
            throw new InvalidDataException("Device Buffer Request must be 5-200 ms.");
        }
        LoadBassLibraries(nativeDirectory, includeWasapi: true);

        BassWasapiOutputDeviceFactory factory = new(
            new BassWasapiAudioOutputDeviceSettings(deviceBufferRequestMilliseconds));
        AudioOutputDeviceInfo device = SelectDevice(factory, requestedDeviceId);
        using SilentSource source = new(device.AudioFormat);
        BassWasapiOutputDevice probe = (BassWasapiOutputDevice)factory.Open(device, source);
        int actualSampleRate = probe.Info.AudioFormat.SampleRate;
        int actualBufferFrames = checked((int)probe.ActualBufferFrameCount);
        probe.Dispose();
        if (probe.CleanupFaulted)
        {
            throw new MidoraAudioDeviceException(
                $"BASSWASAPI probe cleanup failed with BASS error {probe.CleanupErrorCode}.");
        }

        control.PublishPrepared(actualSampleRate, actualBufferFrames);
        return 0;
    }

    private static int RunPlayback(string[] args, SharedAudioWorkerControl control)
    {
        control.PublishState(AudioWorkerState.Preparing);
        string planPath = InitialReleaseAudioWorkerProtocolPolicy.RequireExistingFile(
            args[2],
            "render plan");
        string soundFontPath = InitialReleaseAudioWorkerProtocolPolicy.RequireExistingFile(
            args[3],
            "SoundFont");
        string nativeDirectory = InitialReleaseAudioWorkerProtocolPolicy.RequireExistingDirectory(
            args[4],
            "native library");
        string? requestedDeviceId = EmptyToNull(args[5]);
        int renderAheadMilliseconds = ParseInt32(args[6]);
        int deviceBufferRequestMilliseconds = ParseInt32(args[7]);
        BassMidiRendererSettings rendererSettings = new(
            ParseInt32(args[8]),
            ParseInt32(args[9]));
        AudioMasterSettings masterSettings = new(
            ParseSingle(args[10]),
            ParseSingle(args[11]),
            ParseSingle(args[12]),
            InitialReleaseAudioWorkerProtocolPolicy.ParseBoolean(args[13], "limiterEnabled"));
        InitialReleaseAudioWorkerProtocolPolicy.ValidateRealtimeSettings(
            rendererSettings,
            masterSettings,
            renderAheadMilliseconds,
            deviceBufferRequestMilliseconds);
        int expectedSampleRate = ParseInt32(args[14]);
        if (expectedSampleRate <= 0)
        {
            throw new InvalidDataException("The expected realtime sample rate must be positive.");
        }

        MidiRenderPlan plan = MidiRenderPlanFile.Read(planPath);
        string? cacheStagingPath = EmptyToNull(args[15]);
        if (cacheStagingPath is not null)
        {
            cacheStagingPath = InitialReleaseAudioWorkerProtocolPolicy.RequireExistingFile(
                cacheStagingPath,
                "Unit PCM cache staging");
        }
        string? bufferingRecoverySpoolPath = EmptyToNull(args[16]);
        if (bufferingRecoverySpoolPath is not null)
        {
            bufferingRecoverySpoolPath = InitialReleaseAudioWorkerProtocolPolicy.RequireExistingFile(
                bufferingRecoverySpoolPath,
                "Buffering recovery spool");
        }
        long bufferingRecoveryMemoryFrameCapacity = ParseInt64(args[17]);
        if (bufferingRecoveryMemoryFrameCapacity < 0
            || bufferingRecoveryMemoryFrameCapacity > plan.TotalFrameCount)
        {
            throw new InvalidDataException(
                "The Buffering recovery memory capacity is incompatible with the render plan.");
        }
        string? playbackSpanCacheStagingPath = EmptyToNull(args[18]);
        if (playbackSpanCacheStagingPath is not null)
        {
            playbackSpanCacheStagingPath = InitialReleaseAudioWorkerProtocolPolicy.RequireExistingFile(
                playbackSpanCacheStagingPath,
                "playback-span cache staging");
        }
        bool playbackSpanCacheHit = InitialReleaseAudioWorkerProtocolPolicy.ParseBoolean(
            args[19],
            "playbackSpanCacheHit");
        if (playbackSpanCacheHit && playbackSpanCacheStagingPath is null)
        {
            throw new InvalidDataException(
                "A playback-span cache hit requires a staging payload.");
        }
        bool rollingPreparationEnabled = InitialReleaseAudioWorkerProtocolPolicy.ParseBoolean(
            args[20],
            "rollingPreparationEnabled");
        long measuredCacheWriteBytesPerSecond = ParseInt64(args[21]);
        if (measuredCacheWriteBytesPerSecond < 0)
        {
            throw new InvalidDataException(
                "The measured cache writer bandwidth cannot be negative.");
        }
        int segmentProducerConcurrency =
            RollingAudioPreparationPolicy.ComputeSegmentProducerConcurrency(
                Environment.ProcessorCount,
                plan.SampleRate,
                measuredCacheWriteBytesPerSecond);
        string planDirectory = Path.GetDirectoryName(planPath)
            ?? throw new InvalidDataException("The render plan path has no parent directory.");
        if (plan.SampleRate != expectedSampleRate)
        {
            throw new InvalidDataException("The frozen render plan does not match the probed device rate.");
        }
        LoadBassLibraries(nativeDirectory, includeWasapi: true);

        using BassMidiRenderer renderer = new(
            plan,
            soundFontPath,
            rendererSettings,
            masterSettings,
            cacheStagingPath,
            segmentProducerConcurrency);
        using PlaybackSpanRenderSource? playbackSpanSource =
            playbackSpanCacheStagingPath is null
                ? null
                : new PlaybackSpanRenderSource(
                    renderer,
                    playbackSpanCacheStagingPath,
                    playbackSpanCacheHit);
        IAudioRenderSource unpreparedRenderSource =
            (IAudioRenderSource?)playbackSpanSource ?? renderer;
        using RollingPreparationRenderSource? rollingSource = rollingPreparationEnabled
            ? new RollingPreparationRenderSource(
                unpreparedRenderSource,
                plan.TotalFrameCount,
                TimeSpan.FromSeconds(30))
            : null;
        IAudioRenderSource primaryRenderSource =
            (IAudioRenderSource?)rollingSource ?? unpreparedRenderSource;
        BufferingRecoveryRenderSource? createdRecoverySource = null;
        Exception? recoveryStorageFailure = null;
        if (bufferingRecoverySpoolPath is not null)
        {
            try
            {
                createdRecoverySource = new BufferingRecoveryRenderSource(
                    primaryRenderSource,
                    bufferingRecoverySpoolPath,
                    InitialReleaseAudioRuntimePolicy.WorkFrameCount);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or InvalidDataException
                or OutOfMemoryException
                or OverflowException)
            {
                recoveryStorageFailure = exception;
            }
        }

        if (createdRecoverySource is null && bufferingRecoveryMemoryFrameCapacity > 0)
        {
            try
            {
                createdRecoverySource = new BufferingRecoveryRenderSource(
                    primaryRenderSource,
                    bufferingRecoveryMemoryFrameCapacity,
                    InitialReleaseAudioRuntimePolicy.WorkFrameCount);
            }
            catch (Exception exception) when (exception is OutOfMemoryException
                or OverflowException)
            {
                recoveryStorageFailure = recoveryStorageFailure is null
                    ? exception
                    : new AggregateException(
                        "Both disk and in-memory Buffering recovery reservations failed.",
                        recoveryStorageFailure,
                        exception);
            }
        }
        using BufferingRecoveryRenderSource? recoverySource = createdRecoverySource;
        IAudioRenderSource renderSource = (IAudioRenderSource?)recoverySource ?? primaryRenderSource;
        int ringCapacityFrames = InitialReleaseAudioRuntimePolicy.BufferMillisecondsToFrameCapacity(
            plan.SampleRate,
            renderAheadMilliseconds);
        using AudioFrameRingBuffer ring = new(renderer.Format, ringCapacityFrames);
        int workFrameCount = InitialReleaseAudioRuntimePolicy.WorkFramesForRingCapacity(ringCapacityFrames);
        using AudioRenderAheadWorker renderWorker = new(renderSource, ring, workFrameCount);
        renderWorker.Start();

        int prefillThreshold = ringCapacityFrames * 3 / 4;
        while (ring.AvailableFrameCount < prefillThreshold
            && !ring.ProducerCompleted
            && !ring.ProducerFaulted)
        {
            Thread.Sleep(1);
        }
        if (ring.ProducerFaulted)
        {
            throw new MidoraAudioException($"Render-ahead Preparing failed: {renderer.Fault}");
        }

        BassWasapiOutputDeviceFactory factory = new(
            new BassWasapiAudioOutputDeviceSettings(deviceBufferRequestMilliseconds));
        AudioOutputDeviceInfo device = SelectDevice(factory, requestedDeviceId);
        using BassWasapiOutputDevice output = (BassWasapiOutputDevice)factory.Open(device, ring);
        if (output.Info.AudioFormat.SampleRate != expectedSampleRate)
        {
            throw new MidoraAudioDeviceException(
                "Device actual sample rate changed after probing; sample-domain state must be rebuilt.");
        }

        control.PublishPrepared(expectedSampleRate, checked((int)output.ActualBufferFrameCount));
        output.Start();
        control.PublishState(AudioWorkerState.Playing);

        bool stopRequested = false;
        bool flushOnStop = true;
        bool completed = false;
        bool heldPreviewPaused = false;
        long heldPreviewPlanGeneration = 0;
        while (!stopRequested && !completed)
        {
            while (control.TryDequeue(out AudioWorkerControlCommand command))
            {
                if (command.Kind == AudioWorkerControlCommandKind.Stop)
                {
                    stopRequested = true;
                    flushOnStop = command.MonitoringCommand.SourceEnabled;
                    break;
                }
                if (command.Kind == AudioWorkerControlCommandKind.HeldPreviewPause)
                {
                    if (heldPreviewPaused)
                    {
                        throw new InvalidDataException("The held-preview producer is already paused.");
                    }
                    renderWorker.PauseAtProducerFrontier(TimeSpan.FromSeconds(5));
                    heldPreviewPaused = true;
                    control.PublishHeldPreviewStatus(
                        AudioWorkerState.HeldPreviewPaused,
                        output.ConsumedFrameCount,
                        GetRenderPosition(renderer, playbackSpanSource),
                        ring.UnderrunCount,
                        output.CallbackAllocatedBytes,
                        GetRenderingAllocatedBytes(renderWorker, rollingSource, renderer),
                        heldPreviewPlanGeneration);
                    continue;
                }
                if (command.Kind == AudioWorkerControlCommandKind.HeldPreviewApplyPlan)
                {
                    if (!heldPreviewPaused)
                    {
                        throw new InvalidDataException(
                            "A held-preview plan can only be applied while the producer is paused.");
                    }
                    string replacementPath = Path.Combine(
                        planDirectory,
                        HeldPreviewPlanExchange.GetFileName(command.Payload));
                    MidiRenderPlan replacement = MidiRenderPlanFile.Read(replacementPath);
                    renderer.ReplaceFuturePlan(replacement, renderer.RenderPositionFrames);
                    heldPreviewPlanGeneration = command.Payload;
                    renderWorker.ResumeFromProducerFrontier();
                    heldPreviewPaused = false;
                    control.PublishHeldPreviewStatus(
                        AudioWorkerState.Playing,
                        output.ConsumedFrameCount,
                        GetRenderPosition(renderer, playbackSpanSource),
                        ring.UnderrunCount,
                        output.CallbackAllocatedBytes,
                        GetRenderingAllocatedBytes(renderWorker, rollingSource, renderer),
                        heldPreviewPlanGeneration);
                    continue;
                }
                if (command.Kind == AudioWorkerControlCommandKind.HeldPreviewResume)
                {
                    if (!heldPreviewPaused)
                    {
                        throw new InvalidDataException(
                            "The held-preview producer is not paused.");
                    }
                    renderWorker.ResumeFromProducerFrontier();
                    heldPreviewPaused = false;
                    continue;
                }
                if (command.Kind == AudioWorkerControlCommandKind.BufferingRecoveryPrepare)
                {
                    if (recoverySource is null)
                    {
                        throw new AudioRecoveryStorageUnavailableException(
                            "The complete Buffering recovery interval has neither a disk spool nor an in-memory reservation.",
                            recoveryStorageFailure ?? new OutOfMemoryException(
                                "The Worker could not reserve the in-memory fallback."));
                    }
                    if (!ring.IsBuffering || heldPreviewPaused)
                    {
                        throw new InvalidDataException(
                            "A Buffering recovery command requires a configured spool and latched underrun.");
                    }
                    renderWorker.PauseAtProducerFrontier(TimeSpan.FromSeconds(5));
                    recoverySource.PrepareRecovery(
                        ring,
                        command.Payload);
                    renderWorker.ResumeFromProducerFrontier();
                    while (ring.AvailableFrameCount < prefillThreshold
                        && !ring.ProducerCompleted
                        && !ring.ProducerFaulted)
                    {
                        Thread.Sleep(1);
                    }
                    if (ring.ProducerFaulted)
                    {
                        throw new MidoraAudioException(
                            "The render-ahead producer faulted while publishing the recovery span.");
                    }
                    ring.ReleaseBuffering();
                    continue;
                }
                if (command.Kind != AudioWorkerControlCommandKind.Monitoring)
                {
                    throw new InvalidDataException("The shared audio command kind is invalid.");
                }

                MidiMonitoringCommand monitoring = command.MonitoringCommand;
                if (rollingSource is not null)
                {
                    renderWorker.PauseAtProducerFrontier(TimeSpan.FromSeconds(5));
                    try
                    {
                        rollingSource.ResetForMonitoringColdStart(
                            TimeSpan.FromSeconds(5),
                            () => renderer.EnqueueMonitoringCommands(
                                MemoryMarshal.CreateReadOnlySpan(ref monitoring, 1)));
                    }
                    finally
                    {
                        renderWorker.ResumeFromProducerFrontier();
                    }
                }
                else
                {
                    playbackSpanSource?.RequestMonitoringFallback();
                    renderer.EnqueueMonitoringCommands(
                        MemoryMarshal.CreateReadOnlySpan(ref monitoring, 1));
                }
            }

            if (output.DeviceLost)
            {
                // A removed/disabled active endpoint cannot be stopped through that endpoint again.
                // Dispose first to detach the native callback and release every owned handle, then
                // publish a non-fault terminal state that requires an explicit main-process choice.
                output.Dispose();
                renderWorker.Stop();
                rollingSource?.StopPreparation();
                FinalizeAndMarkCacheCaptures(
                    cacheStagingPath,
                    playbackSpanCacheStagingPath,
                    renderer,
                    playbackSpanSource);
                control.PublishRuntimeStatus(
                    AudioWorkerState.OutputDeviceUnavailable,
                    output.ConsumedFrameCount,
                    GetRenderPosition(renderer, playbackSpanSource),
                    ring.UnderrunCount,
                    output.CallbackAllocatedBytes,
                    GetRenderingAllocatedBytes(renderWorker, rollingSource, renderer));
                return 0;
            }

            bool outputSelectionInvalidated = BassWasapiOutputDevice.IsOutputSelectionInvalidated(
                requestedDeviceId is null,
                output.DefaultDeviceChanged,
                deviceLost: false);
            if (output.CallbackFaulted || outputSelectionInvalidated || ring.ProducerFaulted
                || renderer.Fault.Code != AudioRenderFaultCode.None)
            {
                throw new MidoraAudioException(
                    $"Audio worker fault: callback={output.CallbackFaulted}; deviceLost={output.DeviceLost}; "
                    + $"defaultMappingChanged={requestedDeviceId is null && output.DefaultDeviceChanged}; "
                    + $"ring={ring.ProducerFaulted}; renderer={renderer.Fault}.");
            }

            if (heldPreviewPaused)
            {
                control.PublishHeldPreviewStatus(
                    AudioWorkerState.HeldPreviewPaused,
                    output.ConsumedFrameCount,
                    GetRenderPosition(renderer, playbackSpanSource),
                    ring.UnderrunCount,
                    output.CallbackAllocatedBytes,
                    GetRenderingAllocatedBytes(renderWorker, rollingSource, renderer),
                    heldPreviewPlanGeneration);
                Thread.Sleep(1);
                continue;
            }

            completed = ring.ProducerCompleted && ring.AvailableFrameCount == 0;
            AudioWorkerState state = completed
                ? AudioWorkerState.Completed
                : ring.IsBuffering
                    ? AudioWorkerState.Buffering
                    : AudioWorkerState.Playing;
            control.PublishRuntimeStatus(
                state,
                output.ConsumedFrameCount,
                GetRenderPosition(renderer, playbackSpanSource),
                ring.UnderrunCount,
                output.CallbackAllocatedBytes,
                GetRenderingAllocatedBytes(renderWorker, rollingSource, renderer));
            if (!stopRequested && !completed)
            {
                Thread.Sleep(1);
            }
        }

        control.PublishState(stopRequested ? AudioWorkerState.Stopping : AudioWorkerState.Completed);
        output.Stop(flushOnStop);
        renderWorker.Stop();
        rollingSource?.StopPreparation();
        FinalizeAndMarkCacheCaptures(
            cacheStagingPath,
            playbackSpanCacheStagingPath,
            renderer,
            playbackSpanSource);
        control.PublishRuntimeStatus(
            stopRequested ? AudioWorkerState.Stopped : AudioWorkerState.Completed,
            output.ConsumedFrameCount,
            GetRenderPosition(renderer, playbackSpanSource),
            ring.UnderrunCount,
            output.CallbackAllocatedBytes,
            GetRenderingAllocatedBytes(renderWorker, rollingSource, renderer));
        return 0;
    }

    private static void FinalizeAndMarkCacheCaptures(
        string? cacheStagingPath,
        string? playbackSpanCacheStagingPath,
        BassMidiRenderer renderer,
        PlaybackSpanRenderSource? playbackSpanSource)
    {
        playbackSpanSource?.FinalizeCapture();
        renderer.FinalizeCacheCapture();
        if (cacheStagingPath is not null && renderer.CacheCaptureInvalidated)
        {
            File.WriteAllText(cacheStagingPath + ".invalidated", "monitoring-generation\n");
        }
        if (playbackSpanCacheStagingPath is not null
            && playbackSpanSource?.CacheCaptureInvalidated == true)
        {
            File.WriteAllText(
                playbackSpanCacheStagingPath + ".invalidated",
                "cache-io-failure\n");
        }
    }

    private static int RunFileProbe(string[] args, SharedAudioWorkerControl control)
    {
        control.PublishState(AudioWorkerState.Preparing);
        string soundFontPath = InitialReleaseAudioWorkerProtocolPolicy.RequireExistingFile(
            args[2],
            "SoundFont");
        string nativeDirectory = InitialReleaseAudioWorkerProtocolPolicy.RequireExistingDirectory(
            args[3],
            "native library");
        int sampleRate = ParseInt32(args[4]);
        BassMidiRendererSettings rendererSettings = new(
            ParseInt32(args[5]),
            InitialReleaseAudioRuntimePolicy.WorkFrameCount);
        AudioMasterSettings masterSettings = ParseRequiredFileMasterSettings(args, 6);
        InitialReleaseAudioWorkerProtocolPolicy.ValidateFileSettings(
            rendererSettings,
            masterSettings,
            sampleRate);

        LoadBassLibraries(nativeDirectory, includeWasapi: false);
        MidiRenderPlan plan = new(sampleRate, 0, []);
        using BassMidiRenderer renderer = new(
            plan,
            soundFontPath,
            rendererSettings,
            masterSettings);
        control.PublishPrepared(sampleRate, 0);
        control.PublishRuntimeStatus(AudioWorkerState.Completed, 0, 0, 0, 0, 0);
        return 0;
    }

    private static int RunFileRender(string[] args, SharedAudioWorkerControl control)
    {
        control.PublishState(AudioWorkerState.Preparing);
        string planPath = InitialReleaseAudioWorkerProtocolPolicy.RequireExistingFile(
            args[2],
            "render plan");
        string soundFontPath = InitialReleaseAudioWorkerProtocolPolicy.RequireExistingFile(
            args[3],
            "SoundFont");
        string nativeDirectory = InitialReleaseAudioWorkerProtocolPolicy.RequireExistingDirectory(
            args[4],
            "native library");
        string temporaryOutputPath = InitialReleaseAudioWorkerProtocolPolicy.RequireNewFileTarget(
            args[5],
            "temporary WAV");
        BassMidiRendererSettings rendererSettings = new(
            ParseInt32(args[6]),
            InitialReleaseAudioRuntimePolicy.WorkFrameCount);
        AudioMasterSettings masterSettings = ParseRequiredFileMasterSettings(args, 7);
        string? cacheStagingPath = EmptyToNull(args[11]);
        if (cacheStagingPath is not null)
        {
            cacheStagingPath = InitialReleaseAudioWorkerProtocolPolicy.RequireExistingFile(
                cacheStagingPath,
                "Unit PCM cache staging");
        }

        MidiRenderPlan plan = MidiRenderPlanFile.Read(planPath);
        InitialReleaseAudioWorkerProtocolPolicy.ValidateFileSettings(
            rendererSettings,
            masterSettings,
            plan.SampleRate);
        LoadBassLibraries(nativeDirectory, includeWasapi: false);
        using BassMidiRenderer renderer = new(
            plan,
            soundFontPath,
            rendererSettings,
            masterSettings,
            cacheStagingPath);
        FileRenderMonitor monitor = new(control, plan.TotalFrameCount);
        control.PublishPrepared(plan.SampleRate, 0);
        control.PublishRuntimeStatus(AudioWorkerState.Rendering, 0, 0, 0, 0, 0);
        try
        {
            WaveFileRenderResult rendered = WaveFileOutput.Render(
                renderer,
                plan.TotalFrameCount,
                temporaryOutputPath,
                InitialReleaseAudioRuntimePolicy.WorkFrameCount,
                overwrite: false,
                monitor: monitor);
            if (renderer.Fault.Code != AudioRenderFaultCode.None)
            {
                throw new MidoraAudioException($"The BASSMIDI renderer failed: {renderer.Fault}.");
            }
            if (rendered.RenderingThreadAllocatedBytes != 0)
            {
                File.Delete(temporaryOutputPath);
                throw new MidoraAudioException(
                    $"The audio file Rendering hot path allocated {rendered.RenderingThreadAllocatedBytes} managed bytes.");
            }
            FinalizeAndMarkCacheCaptures(
                cacheStagingPath,
                playbackSpanCacheStagingPath: null,
                renderer,
                playbackSpanSource: null);
            control.PublishRuntimeStatus(
                AudioWorkerState.Completed,
                rendered.FrameCount,
                rendered.FrameCount,
                0,
                0,
                rendered.RenderingThreadAllocatedBytes);
            return 0;
        }
        catch (OperationCanceledException) when (monitor.CancellationWasRequested)
        {
            control.PublishRuntimeStatus(
                AudioWorkerState.Cancelled,
                renderer.PositionFrames,
                renderer.PositionFrames,
                0,
                0,
                0);
            return 0;
        }
    }

    private static AudioMasterSettings ParseRequiredFileMasterSettings(
        string[] args,
        int offset)
    {
        AudioMasterSettings result = new(
            ParseSingle(args[offset]),
            ParseSingle(args[offset + 1]),
            ParseSingle(args[offset + 2]),
            InitialReleaseAudioWorkerProtocolPolicy.ParseBoolean(
                args[offset + 3],
                "limiterEnabled"));
        return result;
    }

    private static AudioOutputDeviceInfo SelectDevice(
        BassWasapiOutputDeviceFactory factory,
        string? requestedDeviceId)
    {
        IReadOnlyList<AudioOutputDeviceInfo> devices = factory.GetDevices();
        AudioOutputDeviceInfo? device = requestedDeviceId is null
            ? devices.FirstOrDefault(value => value.IsSystemDefault) ?? devices.FirstOrDefault()
            : devices.FirstOrDefault(value =>
                string.Equals(value.Id, requestedDeviceId, StringComparison.Ordinal));
        return device
            ?? throw new MidoraAudioDeviceException("No enabled output device satisfies the selection.");
    }

    private static int ListOutputDevices(string nativeDirectoryArgument)
    {
        string nativeDirectory = InitialReleaseAudioWorkerProtocolPolicy.RequireExistingDirectory(
            nativeDirectoryArgument,
            "native library");
        LoadBassLibraries(nativeDirectory, includeWasapi: true);
        BassWasapiOutputDeviceFactory factory = new(
            new BassWasapiAudioOutputDeviceSettings(50));
        IReadOnlyList<AudioOutputDeviceInfo> devices = factory.GetDevices();

        // Private, versioned, line-oriented protocol. Base64 prevents device names and endpoint IDs
        // from changing field boundaries; the main process never loads a BASS native library.
        global::System.Console.Out.WriteLine("MIDORA-AUDIO-DEVICES-V1");
        foreach (AudioOutputDeviceInfo device in devices)
        {
            string id = Convert.ToBase64String(Encoding.UTF8.GetBytes(device.Id));
            string name = Convert.ToBase64String(Encoding.UTF8.GetBytes(device.Name ?? string.Empty));
            global::System.Console.Out.WriteLine(string.Join(
                '\t',
                device.IsSystemDefault ? "1" : "0",
                device.AudioFormat.SampleRate.ToString(CultureInfo.InvariantCulture),
                device.AudioFormat.ChannelCount.ToString(CultureInfo.InvariantCulture),
                id,
                name));
        }
        return 0;
    }

    private static unsafe int RunLegacyPcmRenderer(string[] args)
    {
        SharedAudioFrameRingBuffer? ring = null;
        NamedPipeClientStream? controlPipe = null;
        Thread? controlThread = null;
        LegacyControlThreadState? controlState = null;
        try
        {
            string mapName = args[0];
            string controlPipeName = args[1];
            string planPath = args[2];
            string soundFontPath = args[3];
            string nativeDirectory = args[4];
            BassMidiRendererSettings rendererSettings = new(
                ParseInt32(args[5]),
                ParseInt32(args[6]));
            AudioMasterSettings masterSettings = new(
                ParseSingle(args[7]),
                ParseSingle(args[8]),
                ParseSingle(args[9]),
                ParseInt32(args[10]) != 0);

            ring = SharedAudioFrameRingBuffer.Open(mapName);
            controlPipe = new NamedPipeClientStream(
                ".",
                controlPipeName,
                PipeDirection.In,
                PipeOptions.Asynchronous);
            controlPipe.Connect(checked((int)TimeSpan.FromSeconds(30).TotalMilliseconds));
            LoadBassLibraries(nativeDirectory, includeWasapi: false);
            MidiRenderPlan plan = MidiRenderPlanFile.Read(planPath);
            if (ring.Format != new AudioFormat(plan.SampleRate, 2, AudioSampleFormat.Float32))
            {
                throw new InvalidDataException("The legacy IPC plan and shared audio ring formats do not match.");
            }

            using BassMidiRenderer renderer = new(plan, soundFontPath, rendererSettings, masterSettings);
            controlState = new LegacyControlThreadState();
            controlThread = new Thread(() => RunLegacyMonitoringControl(controlPipe, renderer, ring, controlState))
            {
                IsBackground = true,
                Name = "Midora Legacy Audio Monitoring Control"
            };
            controlThread.Start();
            int workFrameCount = Math.Min(rendererSettings.MaximumWorkFrameCount, ring.CapacityFrameCount);
            float* workBuffer = (float*)NativeMemory.Alloc(
                checked((nuint)workFrameCount * (nuint)renderer.Format.BytesPerFrame));
            try
            {
                _ = renderer.PullFrames(workBuffer, 0);
                _ = ring.TryWriteFrames(workBuffer, 0);
                ring.MarkProducerReady();
                long allocatedBeforeRendering = GC.GetAllocatedBytesForCurrentThread();
                while (true)
                {
                    if (ring.ProducerFaulted)
                    {
                        break;
                    }
                    if (ring.FreeFrameCount < workFrameCount)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    AudioPullResult result = renderer.PullFrames(workBuffer, workFrameCount);
                    if (result.Status == AudioPullStatus.Fault
                        || result.FrameCount < 0
                        || result.FrameCount > workFrameCount)
                    {
                        ring.FaultProducer();
                        break;
                    }
                    if (result.FrameCount != 0 && !ring.TryWriteFrames(workBuffer, result.FrameCount))
                    {
                        ring.FaultProducer();
                        break;
                    }
                    if (result.Status == AudioPullStatus.EndOfStream)
                    {
                        ring.CompleteProducer(
                            GC.GetAllocatedBytesForCurrentThread() - allocatedBeforeRendering);
                        break;
                    }
                }
            }
            finally
            {
                NativeMemory.Free(workBuffer);
                Volatile.Write(ref controlState.StopRequested, 1);
                controlPipe.Dispose();
                controlPipe = null;
                controlThread.Join();
                controlThread = null;
            }
            return ring.ProducerFaulted ? 1 : 0;
        }
        catch (Exception exception)
        {
            ring?.FaultProducer();
            global::System.Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            if (controlState is not null)
            {
                Volatile.Write(ref controlState.StopRequested, 1);
            }
            controlPipe?.Dispose();
            controlThread?.Join();
            ring?.Dispose();
        }
    }

    private static void RunLegacyMonitoringControl(
        NamedPipeClientStream pipe,
        BassMidiRenderer renderer,
        SharedAudioFrameRingBuffer ring,
        LegacyControlThreadState state)
    {
        try
        {
            using BinaryReader reader = new(pipe, System.Text.Encoding.UTF8, leaveOpen: true);
            while (Volatile.Read(ref state.StopRequested) == 0)
            {
                if (reader.ReadInt32() != LegacyMonitoringProtocolMagic
                    || reader.ReadInt32() != LegacyMonitoringProtocolVersion)
                {
                    throw new InvalidDataException("The legacy monitoring protocol header is invalid.");
                }
                int count = reader.ReadInt32();
                if (count is <= 0 or > MaximumLegacyMonitoringCommandCount)
                {
                    throw new InvalidDataException("The legacy monitoring command count is invalid.");
                }

                MidiMonitoringCommand[] commands = new MidiMonitoringCommand[count];
                for (int i = 0; i < commands.Length; i++)
                {
                    MidiMonitoringCommandKind kind = (MidiMonitoringCommandKind)reader.ReadByte();
                    byte port = reader.ReadByte();
                    bool enabled = reader.ReadBoolean();
                    _ = reader.ReadByte();
                    int sourceIndex = reader.ReadInt32();
                    uint packedMessage = reader.ReadUInt32();
                    commands[i] = new(
                        kind,
                        sourceIndex,
                        port,
                        kind == MidiMonitoringCommandKind.SendMessage
                            ? MidiMessage.FromPackedValue(packedMessage)
                            : default,
                        enabled);
                }
                renderer.EnqueueMonitoringCommands(commands);
            }
        }
        catch (Exception exception) when (
            Volatile.Read(ref state.StopRequested) != 0
            && exception is EndOfStreamException or IOException or ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            global::System.Console.Error.WriteLine(exception);
            ring.FaultProducer();
        }
    }

    private static int ParseInt32(string value) =>
        int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static long ParseInt64(string value) =>
        long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static long GetRenderPosition(
        BassMidiRenderer renderer,
        PlaybackSpanRenderSource? playbackSpanSource) =>
        playbackSpanSource?.PositionFrames ?? renderer.RenderPositionFrames;

    private static long GetRenderingAllocatedBytes(
        AudioRenderAheadWorker realtimeWorker,
        RollingPreparationRenderSource? rollingSource,
        BassMidiRenderer renderer) => checked(
            realtimeWorker.RenderingThreadAllocatedBytes
            + (rollingSource?.RenderingThreadAllocatedBytes ?? 0)
            + renderer.ParallelDecodeAllocatedBytesForDiagnostics);

    private static float ParseSingle(string value) =>
        float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static string? EmptyToNull(string value) => string.IsNullOrEmpty(value) ? null : value;

    private static void LoadBassLibraries(string nativeDirectory, bool includeWasapi)
    {
        nint bassHandle = NativeLibrary.Load(Path.Combine(nativeDirectory, "bass.dll"));
        nint bassMidiHandle = NativeLibrary.Load(Path.Combine(nativeDirectory, "bassmidi.dll"));
        NativeLibrary.SetDllImportResolver(
            typeof(NativeBass).Assembly,
            (libraryName, _, _) => string.Equals(
                libraryName,
                NativeBass.LibraryName,
                StringComparison.OrdinalIgnoreCase)
                    ? bassHandle
                    : 0);
        NativeLibrary.SetDllImportResolver(
            typeof(NativeBassMidi).Assembly,
            (libraryName, _, _) => string.Equals(
                libraryName,
                NativeBassMidi.LibraryName,
                StringComparison.OrdinalIgnoreCase)
                    ? bassMidiHandle
                    : 0);
        if (includeWasapi)
        {
            nint bassWasapiHandle = NativeLibrary.Load(Path.Combine(nativeDirectory, "basswasapi.dll"));
            NativeLibrary.SetDllImportResolver(
                typeof(NativeBassWasapi).Assembly,
                (libraryName, _, _) => string.Equals(
                    libraryName,
                    NativeBassWasapi.LibraryName,
                    StringComparison.OrdinalIgnoreCase)
                        ? bassWasapiHandle
                        : 0);
        }
    }

    private sealed class LegacyControlThreadState
    {
        public int StopRequested;
    }

    private sealed class FileRenderMonitor(
        SharedAudioWorkerControl control,
        long totalFrameCount) : IWaveFileRenderMonitor
    {
        private bool _cancelled;

        public bool CancellationWasRequested => _cancelled;

        public bool IsCancellationRequested
        {
            get
            {
                while (control.TryDequeue(out AudioWorkerControlCommand command))
                {
                    if (command.Kind != AudioWorkerControlCommandKind.Stop)
                    {
                        throw new InvalidDataException(
                            "File rendering received a command other than cancellation.");
                    }
                    _cancelled = true;
                    control.PublishState(AudioWorkerState.Cancelling);
                }
                return _cancelled;
            }
        }

        public void ReportRenderedFrames(long renderedFrameCount) =>
            control.PublishRuntimeStatus(
                AudioWorkerState.Rendering,
                renderedFrameCount,
                renderedFrameCount,
                0,
                0,
                0);

        public void BeginFinalizing() =>
            control.PublishRuntimeStatus(
                AudioWorkerState.Finalizing,
                totalFrameCount,
                totalFrameCount,
                0,
                0,
                0);
    }

    private sealed unsafe class SilentSource(AudioFormat format) : IAudioRenderSource, IDisposable
    {
        public AudioFormat Format { get; } = format;

        public AudioPullResult PullFrames(float* destination, int requestedFrameCount)
        {
            NativeMemory.Clear(
                destination,
                checked((nuint)requestedFrameCount * (nuint)Format.BytesPerFrame));
            return AudioPullResult.Continue(requestedFrameCount);
        }

        public void Dispose()
        {
        }
    }
}
