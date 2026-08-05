using Midora.AudioDevice;
using Midora.AudioDevice.BassWasapi.Internals;
using Midora.AudioDevice.BassWasapi.Settings;
using Midora.Midi;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

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

            if (string.Equals(args[0], "play", StringComparison.Ordinal))
            {
                if (args.Length != 19)
                {
                    throw new ArgumentException("Invalid playback argument count.");
                }
                control = SharedAudioWorkerControl.Open(args[1]);
                return RunPlayback(args, control);
            }

            // Kept only for the existing sample-equivalence harness. Formal realtime playback
            // exclusively uses the shared-control "play" mode above and never transports PCM.
            if (args.Length == 15)
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
        string nativeDirectory = args[2];
        string? requestedDeviceId = EmptyToNull(args[3]);
        int deviceBufferRequestMilliseconds = ParseInt32(args[4]);
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
        string planPath = args[2];
        string soundFontPath = args[3];
        string nativeDirectory = args[4];
        string? requestedDeviceId = EmptyToNull(args[5]);
        int renderAheadMilliseconds = ParseInt32(args[6]);
        int deviceBufferRequestMilliseconds = ParseInt32(args[7]);
        BassMidiRendererSettings rendererSettings = new(
            (BassMidiNoteOffPolicy)ParseInt32(args[8]),
            (BassMidiInterpolation)ParseInt32(args[9]),
            (BassMidiSampleLoading)ParseInt32(args[10]),
            ParseInt32(args[11]),
            ParseSingle(args[12]),
            ParseInt32(args[13]));
        AudioMasterSettings masterSettings = new(
            ParseSingle(args[14]),
            ParseSingle(args[15]),
            ParseSingle(args[16]),
            ParseInt32(args[17]) != 0);
        int expectedSampleRate = ParseInt32(args[18]);

        LoadBassLibraries(nativeDirectory, includeWasapi: true);
        MidiRenderPlan plan = MidiRenderPlanFile.Read(planPath);
        if (plan.SampleRate != expectedSampleRate)
        {
            throw new InvalidDataException("The frozen render plan does not match the probed device rate.");
        }

        using BassMidiRenderer renderer = new(
            plan,
            soundFontPath,
            rendererSettings,
            masterSettings);
        int ringCapacityFrames = InitialReleaseAudioRuntimePolicy.BufferMillisecondsToFrameCapacity(
            plan.SampleRate,
            renderAheadMilliseconds);
        using AudioFrameRingBuffer ring = new(renderer.Format, ringCapacityFrames);
        int workFrameCount = InitialReleaseAudioRuntimePolicy.WorkFramesForRingCapacity(ringCapacityFrames);
        using AudioRenderAheadWorker renderWorker = new(renderer, ring, workFrameCount);
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
                if (command.Kind != AudioWorkerControlCommandKind.Monitoring)
                {
                    throw new InvalidDataException("The shared audio command kind is invalid.");
                }

                MidiMonitoringCommand monitoring = command.MonitoringCommand;
                renderer.EnqueueMonitoringCommands(
                    MemoryMarshal.CreateReadOnlySpan(ref monitoring, 1));
            }

            if (output.CallbackFaulted || ring.ProducerFaulted
                || renderer.Fault.Code != AudioRenderFaultCode.None)
            {
                throw new MidoraAudioException(
                    $"Audio worker fault: callback={output.CallbackFaulted}; ring={ring.ProducerFaulted}; renderer={renderer.Fault}.");
            }

            completed = ring.ProducerCompleted && ring.AvailableFrameCount == 0;
            AudioWorkerState state = completed
                ? AudioWorkerState.Completed
                : ring.AvailableFrameCount < workFrameCount
                    ? AudioWorkerState.Buffering
                    : AudioWorkerState.Playing;
            control.PublishRuntimeStatus(
                state,
                output.ConsumedFrameCount,
                renderer.PositionFrames,
                ring.UnderrunCount,
                output.CallbackAllocatedBytes,
                renderWorker.RenderingThreadAllocatedBytes);
            if (!stopRequested && !completed)
            {
                Thread.Sleep(1);
            }
        }

        control.PublishState(stopRequested ? AudioWorkerState.Stopping : AudioWorkerState.Completed);
        output.Stop(flushOnStop);
        renderWorker.Stop();
        control.PublishRuntimeStatus(
            stopRequested ? AudioWorkerState.Stopped : AudioWorkerState.Completed,
            output.ConsumedFrameCount,
            renderer.PositionFrames,
            ring.UnderrunCount,
            output.CallbackAllocatedBytes,
            renderWorker.RenderingThreadAllocatedBytes);
        return 0;
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
                (BassMidiNoteOffPolicy)ParseInt32(args[5]),
                (BassMidiInterpolation)ParseInt32(args[6]),
                (BassMidiSampleLoading)ParseInt32(args[7]),
                ParseInt32(args[8]),
                ParseSingle(args[9]),
                ParseInt32(args[10]));
            AudioMasterSettings masterSettings = new(
                ParseSingle(args[11]),
                ParseSingle(args[12]),
                ParseSingle(args[13]),
                ParseInt32(args[14]) != 0);

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

    private static float ParseSingle(string value) =>
        float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static string? EmptyToNull(string value) => string.IsNullOrEmpty(value) ? null : value;

    private static void LoadBassLibraries(string nativeDirectory, bool includeWasapi)
    {
        _ = NativeLibrary.Load(Path.Combine(nativeDirectory, "bass.dll"));
        _ = NativeLibrary.Load(Path.Combine(nativeDirectory, "bassmidi.dll"));
        if (includeWasapi)
        {
            _ = NativeLibrary.Load(Path.Combine(nativeDirectory, "basswasapi.dll"));
        }
    }

    private sealed class LegacyControlThreadState
    {
        public int StopRequested;
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
