using Midora.Audio;
using Midora.Audio.Bass;
using Midora.AudioDevice;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Midora.Audio.Bass.Worker;

[SupportedOSPlatform("windows")]
public static class Program
{
    public static unsafe int Main(string[] args)
    {
        SharedAudioFrameRingBuffer? ring = null;
        try
        {
            if (args.Length != 13)
            {
                throw new ArgumentException("Invalid Midora audio worker argument count.");
            }

            string mapName = args[0];
            string planPath = args[1];
            string soundFontPath = args[2];
            string nativeDirectory = args[3];
            BassMidiRendererSettings rendererSettings = new(
                (BassMidiNoteOffPolicy)ParseInt32(args[4]),
                (BassMidiInterpolation)ParseInt32(args[5]),
                (BassMidiSampleLoading)ParseInt32(args[6]),
                ParseInt32(args[7]),
                ParseSingle(args[8]),
                ParseInt32(args[9]));
            AudioMasterSettings masterSettings = new(
                ParseSingle(args[10]),
                ParseSingle(args[11]),
                ParseSingle(args[12]));

            ring = SharedAudioFrameRingBuffer.Open(mapName);
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
            ring?.Dispose();
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
}
