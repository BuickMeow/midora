using Midora.Audio;
using Midora.Audio.Bass;
using Midora.AudioDevice;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Midora.Midi;

namespace Midora.Audio.Bass.Worker;

[SupportedOSPlatform("windows")]
public static class Program
{
    private const int MonitoringProtocolMagic = 0x4d43444d;
    private const int MonitoringProtocolVersion = 1;
    private const int MaximumMonitoringCommandCount = 1_000_000;

    public static unsafe int Main(string[] args)
    {
        SharedAudioFrameRingBuffer? ring = null;
        NamedPipeClientStream? controlPipe = null;
        Thread? controlThread = null;
        ControlThreadState? controlState = null;
        try
        {
            if (args.Length != 15)
            {
                throw new ArgumentException("Invalid Midora audio worker argument count.");
            }

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
            LoadBassLibraries(nativeDirectory);
            MidiRenderPlan plan = MidiRenderPlanFile.Read(planPath);
            if (ring.Format != new AudioFormat(plan.SampleRate, 2, AudioSampleFormat.Float32))
            {
                throw new InvalidDataException("The IPC plan and shared audio ring formats do not match.");
            }

            using BassMidiRenderer renderer = new(
                plan,
                soundFontPath,
                rendererSettings,
                masterSettings);
            controlState = new ControlThreadState();
            controlThread = new Thread(() => RunMonitoringControl(controlPipe, renderer, ring, controlState))
            {
                IsBackground = true,
                Name = "Midora Audio Monitoring Control"
            };
            controlThread.Start();
            int workFrameCount = rendererSettings.MaximumWorkFrameCount;
            float* workBuffer = (float*)NativeMemory.Alloc(
                checked((nuint)workFrameCount * (nuint)renderer.Format.BytesPerFrame));

            try
            {
                _ = renderer.PullFrames(workBuffer, 0);
                _ = ring.FreeFrameCount;
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
                        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBeforeRendering;
                        ring.CompleteProducer(allocated);
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

    private static void RunMonitoringControl(
        NamedPipeClientStream pipe,
        BassMidiRenderer renderer,
        SharedAudioFrameRingBuffer ring,
        ControlThreadState state)
    {
        try
        {
            using BinaryReader reader = new(pipe, System.Text.Encoding.UTF8, leaveOpen: true);
            while (Volatile.Read(ref state.StopRequested) == 0)
            {
                if (reader.ReadInt32() != MonitoringProtocolMagic
                    || reader.ReadInt32() != MonitoringProtocolVersion)
                {
                    throw new InvalidDataException("The monitoring control protocol header is invalid.");
                }
                int count = reader.ReadInt32();
                if (count is <= 0 or > MaximumMonitoringCommandCount)
                {
                    throw new InvalidDataException("The monitoring control command count is invalid.");
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
                    MidiMessage message = kind == MidiMonitoringCommandKind.SendMessage
                        ? MidiMessage.FromPackedValue(packedMessage)
                        : default;
                    commands[i] = new(kind, sourceIndex, port, message, enabled);
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

    private static void LoadBassLibraries(string nativeDirectory)
    {
        _ = NativeLibrary.Load(Path.Combine(nativeDirectory, "bass.dll"));
        _ = NativeLibrary.Load(Path.Combine(nativeDirectory, "bassmidi.dll"));
    }

    private sealed class ControlThreadState
    {
        public int StopRequested;
    }
}
