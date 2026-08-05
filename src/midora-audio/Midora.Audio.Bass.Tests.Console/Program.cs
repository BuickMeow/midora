using Midora.Audio.Bass;
using Midora.AudioDevice;
using Midora.AudioDevice.BassWasapi.Internals;
using Midora.AudioDevice.BassWasapi.Settings;
using Midora.AudioDevice.Wave;
using Midora.Midi;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Midora.Audio.Bass.Tests.Console;

[SupportedOSPlatform("windows")]
public static partial class Program
{
    private const int OfflineSampleRate = 48_000;
    private const string DefaultSoundFontPath = @"D:\Soundfonts\sf2\sDetrimental Concert Grand Piano.sf2";

    public static int Main(string[] args)
    {
        try
        {
            string repositoryRoot = FindRepositoryRoot();
            LoadBassLibraries();
            string mode = args.Length >= 1 ? args[0].ToLowerInvariant() : "offline";
            string soundFontPath = args.Length >= 2 ? args[1] : DefaultSoundFontPath;

            return mode switch
            {
                "offline" => RunOffline(
                    repositoryRoot,
                    soundFontPath,
                    args.Length >= 3 ? args[2] : null),
                "realtime" => RunRealtime(soundFontPath),
                "offline-child" => RunOfflineChild(
                    repositoryRoot,
                    soundFontPath,
                    args.Length >= 3 ? args[2] : null),
                "realtime-child" => RunRealtimeChild(repositoryRoot, soundFontPath),
                "wasapi-probe" => RunWasapiProbe(),
                "logic-examples" => RunLogicalExampleSuite(
                    repositoryRoot,
                    soundFontPath,
                    args.Length >= 3 ? args[2] : null),
                "logic-offline" => RunLogicalOffline(
                    repositoryRoot,
                    soundFontPath,
                    args.Length >= 3 ? args[2] : "segments",
                    args.Length >= 4 ? args[3] : null),
                "logic-offline-child" => RunLogicalOfflineChild(
                    repositoryRoot,
                    soundFontPath,
                    args.Length >= 3 ? args[2] : "segments",
                    args.Length >= 4 ? args[3] : null),
                "logic-realtime" => RunLogicalRealtime(
                    soundFontPath,
                    args.Length >= 3 ? args[2] : "subvoices"),
                "logic-realtime-child" => RunLogicalRealtimeChild(
                    repositoryRoot,
                    soundFontPath,
                    args.Length >= 3 ? args[2] : "subvoices"),
                _ => throw new ArgumentException(
                    "用法：offline|offline-child [SF2] [WAV]、realtime|realtime-child [SF2]、logic-examples [SF2] [目录]、logic-offline|logic-offline-child [SF2] [segments|subvoices|tempo-loop] [WAV]、logic-realtime|logic-realtime-child [SF2] [示例] 或 wasapi-probe")
            };
        }
        catch (Exception exception)
        {
            global::System.Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static int RunOffline(string repositoryRoot, string soundFontPath, string? requestedOutputPath)
    {
        string outputPath = requestedOutputPath is not null
            ? Path.GetFullPath(requestedOutputPath)
            : Path.Combine(repositoryRoot, "artifacts", "audio", "midora-bass-offline-smoke.wav");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        MidiRenderPlan plan = CreateTestPlan(OfflineSampleRate);
        using BassMidiRenderer renderer = CreateRenderer(plan, soundFontPath, maximumWorkFrames: 2_048);

        WaveFileRenderResult result = WaveFileOutput.Render(
            renderer,
            plan.TotalFrameCount,
            outputPath,
            workFrameCount: 1_003,
            overwrite: true);

        global::System.Console.WriteLine($"离线渲染完成：{outputPath}");
        global::System.Console.WriteLine($"采样率：{plan.SampleRate} Hz；frames：{result.FrameCount}；文件字节：{result.FileByteCount}");
        global::System.Console.WriteLine($"Rendering 热循环当前线程托管分配：{result.RenderingThreadAllocatedBytes} bytes");
        global::System.Console.WriteLine($"其中 source pull：{result.SourcePullAllocatedBytes} bytes；sample write：{result.SampleWriteAllocatedBytes} bytes");
        global::System.Console.WriteLine($"渲染器故障：{renderer.Fault}");
        return renderer.Fault.Code == AudioRenderFaultCode.None ? 0 : 1;
    }

    private static int RunRealtime(string soundFontPath)
    {
        const int renderAheadMilliseconds = 100;
        const int deviceBufferRequestMilliseconds = 50;
        const int workFrameCount = 256;

        BassWasapiOutputDeviceFactory deviceFactory = new(
            new BassWasapiAudioOutputDeviceSettings(deviceBufferRequestMilliseconds));
        IReadOnlyList<AudioOutputDeviceInfo> devices = deviceFactory.GetDevices();
        if (devices.Count == 0)
        {
            throw new MidoraAudioDeviceException("没有可用的 enabled output device。");
        }

        global::System.Console.WriteLine("可用输出设备：");
        for (int i = 0; i < devices.Count; i++)
        {
            AudioOutputDeviceInfo item = devices[i];
            global::System.Console.WriteLine(
                $"  [{i}] {(item.IsSystemDefault ? "[系统默认] " : string.Empty)}{item.Name}；mix={item.AudioFormat.SampleRate} Hz；id={item.Id}");
        }

        AudioOutputDeviceInfo selected = devices.FirstOrDefault(static item => item.IsSystemDefault) ?? devices[0];
        int sampleRate = selected.AudioFormat.SampleRate;
        MidiRenderPlan plan = CreateTestPlan(sampleRate);
        using BassMidiRenderer renderer = CreateRenderer(plan, soundFontPath, workFrameCount);

        int ringCapacityFrames = checked(sampleRate * renderAheadMilliseconds / 1_000);
        using AudioFrameRingBuffer ring = new(renderer.Format, ringCapacityFrames);
        using AudioRenderAheadWorker worker = new(renderer, ring, workFrameCount);
        worker.Start();

        int startThresholdFrames = ringCapacityFrames * 3 / 4;
        while (ring.AvailableFrameCount < startThresholdFrames
            && !ring.ProducerCompleted
            && !ring.ProducerFaulted)
        {
            Thread.Sleep(1);
        }

        if (ring.ProducerFaulted)
        {
            throw new MidoraAudioDeviceException($"Render-ahead Preparing 失败：{renderer.Fault}");
        }

        using BassWasapiOutputDevice output = (BassWasapiOutputDevice)deviceFactory.Open(selected, ring);
        global::System.Console.WriteLine(
            $"实时播放：{selected.Name}；actual={output.Info.AudioFormat.SampleRate} Hz；actual device buffer={output.ActualBufferFrameCount} frames");
        global::System.Console.WriteLine(
            $"Render-Ahead={renderAheadMilliseconds} ms；Device Request={deviceBufferRequestMilliseconds} ms；事件按 sample-frame 推进，不使用 Thread.Sleep 调度 MIDI。");

        output.Start();
        while (!ring.ProducerFaulted
            && !(ring.ProducerCompleted && ring.AvailableFrameCount == 0))
        {
            Thread.Sleep(10);
        }

        Thread.Sleep(100);
        output.Stop(flush: true);
        worker.Stop();

        global::System.Console.WriteLine(
            $"播放结束：callbacks={output.CallbackCount}；callback allocations={output.CallbackAllocatedBytes} B；underruns={ring.UnderrunCount}；render-thread allocations={worker.RenderingThreadAllocatedBytes} B");
        global::System.Console.WriteLine($"callback fault={output.CallbackFaulted}；producer fault={ring.ProducerFaulted}；renderer fault={renderer.Fault}");

        return !output.CallbackFaulted
            && !ring.ProducerFaulted
            && renderer.Fault.Code == AudioRenderFaultCode.None
            && output.CallbackAllocatedBytes == 0
            && worker.RenderingThreadAllocatedBytes == 0
            ? 0
            : 1;
    }

    private static int RunOfflineChild(
        string repositoryRoot,
        string soundFontPath,
        string? requestedOutputPath)
    {
        string outputPath = requestedOutputPath is not null
            ? Path.GetFullPath(requestedOutputPath)
            : Path.Combine(repositoryRoot, "artifacts", "audio", "midora-bass-child-offline-smoke.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        MidiRenderPlan plan = CreateTestPlan(OfflineSampleRate);
        BassMidiRendererSettings settings = CreateRendererSettings(maximumWorkFrames: 256);
        using BassMidiChildProcessSession session = new(
            plan,
            soundFontPath,
            settings,
            AudioMasterSettings.LimiterV1Candidate,
            ipcAudioBufferMilliseconds: 100,
            GetWorkerPath(repositoryRoot),
            GetBassNativeDirectory(),
            BassMidiChildConsumptionMode.OfflineBlocking,
            preparingTimeout: TimeSpan.FromSeconds(30));

        WaveFileRenderResult result = WaveFileOutput.Render(
            session,
            plan.TotalFrameCount,
            outputPath,
            workFrameCount: 1_003,
            overwrite: true);

        global::System.Console.WriteLine($"独立进程离线渲染完成：{outputPath}");
        global::System.Console.WriteLine(
            $"frames={result.FrameCount}；WAVE Rendering allocations={result.RenderingThreadAllocatedBytes} B；child Rendering allocations={session.RenderingThreadAllocatedBytes} B；child fault={session.ProducerFaulted}");
        return result.RenderingThreadAllocatedBytes == 0
            && session.RenderingThreadAllocatedBytes == 0
            && !session.ProducerFaulted
            ? 0
            : 1;
    }

    private static int RunRealtimeChild(string repositoryRoot, string soundFontPath)
    {
        const int ipcAudioBufferMilliseconds = 100;
        const int deviceBufferRequestMilliseconds = 50;
        const int workFrameCount = 256;

        BassWasapiOutputDeviceFactory deviceFactory = new(
            new BassWasapiAudioOutputDeviceSettings(deviceBufferRequestMilliseconds));
        IReadOnlyList<AudioOutputDeviceInfo> devices = deviceFactory.GetDevices();
        AudioOutputDeviceInfo selected = devices.FirstOrDefault(static item => item.IsSystemDefault)
            ?? devices.FirstOrDefault()
            ?? throw new MidoraAudioDeviceException("没有可用的 enabled output device。");
        MidiRenderPlan plan = CreateTestPlan(selected.AudioFormat.SampleRate);
        BassMidiRendererSettings settings = CreateRendererSettings(workFrameCount);

        using BassMidiChildProcessSession session = new(
            plan,
            soundFontPath,
            settings,
            AudioMasterSettings.LimiterV1Candidate,
            ipcAudioBufferMilliseconds,
            GetWorkerPath(repositoryRoot),
            GetBassNativeDirectory(),
            BassMidiChildConsumptionMode.RealtimeNonBlocking,
            preparingTimeout: TimeSpan.FromSeconds(30));

        int startThresholdFrames = selected.AudioFormat.SampleRate * 75 / 1_000;
        while (session.AvailableFrameCount < startThresholdFrames
            && !session.ProducerCompleted
            && !session.ProducerFaulted)
        {
            Thread.Sleep(1);
        }

        using BassWasapiOutputDevice output = (BassWasapiOutputDevice)deviceFactory.Open(selected, session);
        global::System.Console.WriteLine(
            $"独立进程实时播放：{selected.Name}；actual={output.Info.AudioFormat.SampleRate} Hz；IPC Audio Buffer={ipcAudioBufferMilliseconds} ms");
        output.Start();
        while (!session.ProducerFaulted
            && !(session.ProducerCompleted && session.AvailableFrameCount == 0))
        {
            Thread.Sleep(10);
        }

        Thread.Sleep(100);
        output.Stop(flush: true);
        global::System.Console.WriteLine(
            $"播放结束：callbacks={output.CallbackCount}；callback allocations={output.CallbackAllocatedBytes} B；IPC underruns={session.UnderrunCount}；child allocations={session.RenderingThreadAllocatedBytes} B；child fault={session.ProducerFaulted}");

        return !output.CallbackFaulted
            && !session.ProducerFaulted
            && output.CallbackAllocatedBytes == 0
            && session.RenderingThreadAllocatedBytes == 0
            ? 0
            : 1;
    }

    private static int RunWasapiProbe()
    {
        BassWasapiOutputDeviceFactory factory = new(new BassWasapiAudioOutputDeviceSettings(50));
        IReadOnlyList<AudioOutputDeviceInfo> devices = factory.GetDevices();
        global::System.Console.WriteLine(
            $"BASSWASAPI enabled output devices（API=0x{factory.ApiVersion:x8}；enumeration terminal error={factory.LastEnumerationErrorCode}）：");
        for (int i = 0; i < devices.Count; i++)
        {
            AudioOutputDeviceInfo item = devices[i];
            global::System.Console.WriteLine(
                $"  [{i}] default={item.IsSystemDefault}；{item.Name}；{item.AudioFormat.SampleRate} Hz；{item.Id}");
        }

        AudioOutputDeviceInfo selected = devices.FirstOrDefault(static item => item.IsSystemDefault)
            ?? devices.FirstOrDefault()
            ?? throw new MidoraAudioDeviceException("没有可用的 enabled output device。");
        int capacityFrames = selected.AudioFormat.SampleRate / 10;
        using AudioFrameRingBuffer silentRing = new(selected.AudioFormat, capacityFrames);
        using BassWasapiOutputDevice output = (BassWasapiOutputDevice)factory.Open(selected, silentRing);
        output.Start();
        Thread.Sleep(250);
        output.Stop(flush: true);

        global::System.Console.WriteLine(
            $"静音 probe 完成：actual={output.Info.AudioFormat}；device buffer={output.ActualBufferFrameCount} frames；callbacks={output.CallbackCount}；callback allocations={output.CallbackAllocatedBytes} B；callback fault={output.CallbackFaulted}");
        return output.CallbackCount > 0
            && output.CallbackAllocatedBytes == 0
            && !output.CallbackFaulted
            ? 0
            : 1;
    }

    private static BassMidiRenderer CreateRenderer(
        MidiRenderPlan plan,
        string soundFontPath,
        int maximumWorkFrames)
    {
        BassMidiRendererSettings rendererSettings = CreateRendererSettings(maximumWorkFrames);

        return new BassMidiRenderer(
            plan,
            soundFontPath,
            rendererSettings,
            AudioMasterSettings.LimiterV1Candidate);
    }

    private static BassMidiRendererSettings CreateRendererSettings(int maximumWorkFrames)
    {
        return new BassMidiRendererSettings(
            noteOffPolicy: BassMidiNoteOffPolicy.ReleaseAllMatchingNotes,
            interpolation: BassMidiInterpolation.BassDefault,
            sampleLoading: BassMidiSampleLoading.OnDemand,
            maximumVoices: 0,
            cpuLimitPercent: 0,
            maximumWorkFrameCount: maximumWorkFrames);
    }

    private static MidiRenderPlan CreateTestPlan(int sampleRate)
    {
        List<ScheduledMidiMessage> events = [];

        Add(0, MidiMessage.ControlChange(0, 121, 0));
        Add(0, MidiMessage.ControlChange(0, 0, 0));
        Add(0, MidiMessage.ControlChange(0, 32, 0));
        Add(0, MidiMessage.ProgramChange(0, 0));
        Add(0, MidiMessage.ControlChange(0, 7, 100));
        Add(0, MidiMessage.ControlChange(0, 10, 64));
        Add(0, MidiMessage.ControlChange(0, 11, 127));
        Add(0, MidiMessage.ControlChange(0, 64, 127));

        AddChord(0, 2_000, 48, 55, 60, 64);
        AddChord(2_000, 4_000, 45, 52, 57, 60);
        AddChord(4_000, 6_000, 41, 48, 53, 57);
        AddChord(6_000, 8_000, 43, 50, 55, 59);

        int[] melody = [72, 76, 79, 84, 79, 76, 74, 71, 69, 72, 77, 81, 79, 74, 71, 67];
        for (int i = 0; i < melody.Length; i++)
        {
            int start = i * 500;
            Add(start, MidiMessage.NoteOn(0, (byte)melody[i], (byte)(82 + ((i % 4) * 7))));
            Add(start + 430, MidiMessage.NoteOff(0, (byte)melody[i], 23));
        }

        Add(8_000, MidiMessage.ControlChange(0, 64, 0));
        Add(8_000, MidiMessage.ControlChange(0, 123, 0));

        ScheduledMidiMessage[] orderedEvents = events
            .OrderBy(static item => item.SampleFrame)
            .ToArray();
        MidiPortRenderPlan port = new(0, orderedEvents);
        return new MidiRenderPlan(sampleRate, MillisecondsToFrames(8_000, sampleRate), [port]);

        void Add(int milliseconds, MidiMessage message)
        {
            events.Add(new ScheduledMidiMessage(
                MillisecondsToFrames(milliseconds, sampleRate),
                message));
        }

        void AddChord(int startMilliseconds, int endMilliseconds, params int[] keys)
        {
            for (int i = 0; i < keys.Length; i++)
            {
                Add(startMilliseconds, MidiMessage.NoteOn(0, (byte)keys[i], (byte)(72 + (i * 4))));
                Add(endMilliseconds, MidiMessage.NoteOff(0, (byte)keys[i], 18));
            }
        }
    }

    private static long MillisecondsToFrames(int milliseconds, int sampleRate)
    {
        return checked((long)milliseconds * sampleRate / 1_000);
    }

    private static void LoadBassLibraries()
    {
        string bassPath = GetBassNativeDirectory();

        _ = NativeLibrary.Load(Path.Combine(bassPath, "bass.dll"));
        _ = NativeLibrary.Load(Path.Combine(bassPath, "bassmidi.dll"));
        _ = NativeLibrary.Load(Path.Combine(bassPath, "basswasapi.dll"));
    }

    private static string GetBassNativeDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Midora",
            "Native",
            "BASS",
            "win-x64");
    }

    private static string GetWorkerPath(string repositoryRoot)
    {
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

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Build.props")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the Midora repository root.");
    }
}
