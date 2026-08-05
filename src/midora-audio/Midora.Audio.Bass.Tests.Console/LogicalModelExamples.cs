using Midora.Audio.Bass;
using Midora.AudioDevice.Wave;
using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;
using Midora.Playback.BassWasapi;

namespace Midora.Audio.Bass.Tests.Console;

public static partial class Program
{
    private const int LogicalOfflineSampleRate = 48_000;

    private static int RunLogicalExampleSuite(string repositoryRoot, string soundFontPath, string? requestedDirectory)
    {
        string directory = requestedDirectory is null
            ? Path.Combine(repositoryRoot, "artifacts", "audio", "logical-examples")
            : Path.GetFullPath(requestedDirectory);
        Directory.CreateDirectory(directory);
        string[] names = ["segments", "subvoices", "tempo-loop"];
        foreach (string name in names)
        {
            string path = Path.Combine(directory, $"midora-{name}.wav");
            int code = RenderLogicalProject(CreateLogicalExample(name), soundFontPath, path);
            if (code != 0)
            {
                return code;
            }
        }
        global::System.Console.WriteLine($"逻辑模型试听套件已生成：{directory}");
        return 0;
    }

    private static int RunLogicalOffline(
        string repositoryRoot,
        string soundFontPath,
        string example,
        string? requestedOutputPath)
    {
        string outputPath = requestedOutputPath is null
            ? Path.Combine(repositoryRoot, "artifacts", "audio", $"midora-{example}.wav")
            : Path.GetFullPath(requestedOutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        return RenderLogicalProject(CreateLogicalExample(example), soundFontPath, outputPath);
    }

    private static int RunLogicalOfflineChild(
        string repositoryRoot,
        string soundFontPath,
        string example,
        string? requestedOutputPath)
    {
        string outputPath = requestedOutputPath is null
            ? Path.Combine(repositoryRoot, "artifacts", "audio", $"midora-{example}-child.wav")
            : Path.GetFullPath(requestedOutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        MidoraProject project = CreateLogicalExample(example);
        CanonicalCompiledResult compiled = new MidoraCompiler().CompileFull(project, new CompilationRequest
        {
            Purpose = CompilationPurpose.AudioRender
        });
        PrintCompilation(compiled);
        if (!compiled.IsConsumable)
        {
            return 1;
        }
        MidiRenderPlan plan = MidiRenderPlanAdapter.Create(compiled, LogicalOfflineSampleRate);
        using BassMidiChildProcessSession child = new(
            plan,
            soundFontPath,
            CreateOfflineRendererSettings(InitialReleaseAudioRuntimePolicy.WorkFrameCount),
            AudioMasterSettings.LimiterV1,
            100,
            GetWorkerPath(repositoryRoot),
            GetBassNativeDirectory(),
            BassMidiChildConsumptionMode.OfflineBlocking,
            TimeSpan.FromSeconds(30));
        WaveFileRenderResult rendered = WaveFileOutput.Render(
            child, plan.TotalFrameCount, outputPath, workFrameCount: 1_003, overwrite: true);
        global::System.Console.WriteLine(
            $"逻辑模型子进程离线渲染完成：{outputPath}\n" +
            $"  frames={rendered.FrameCount}；WAVE allocations={rendered.RenderingThreadAllocatedBytes} B；child allocations={child.RenderingThreadAllocatedBytes} B；child fault={child.ProducerFaulted}");
        return rendered.RenderingThreadAllocatedBytes == 0
            && child.RenderingThreadAllocatedBytes == 0
            && !child.ProducerFaulted ? 0 : 1;
    }

    private static int RenderLogicalProject(MidoraProject project, string soundFontPath, string outputPath)
    {
        MidoraCompiler compiler = new();
        CanonicalCompiledResult compiled = compiler.CompileFull(project, new CompilationRequest
        {
            Purpose = CompilationPurpose.AudioRender
        });
        PrintCompilation(compiled);
        if (!compiled.IsConsumable)
        {
            return 1;
        }
        MidiRenderPlan plan = MidiRenderPlanAdapter.Create(compiled, LogicalOfflineSampleRate);
        using BassMidiRenderer renderer = CreateOfflineRenderer(plan, soundFontPath, maximumWorkFrames: 2_048);
        WaveFileRenderResult rendered;
        try
        {
            rendered = WaveFileOutput.Render(
                renderer, plan.TotalFrameCount, outputPath, workFrameCount: 1_003, overwrite: true);
        }
        catch
        {
            global::System.Console.Error.WriteLine($"逻辑模型渲染失败：{renderer.Fault}");
            foreach (ScheduledMidiMessage value in plan.Ports[0].Events)
            {
                if (value.SampleFrame != renderer.Fault.SampleFrame)
                {
                    continue;
                }
                global::System.Console.Error.WriteLine(
                    $"  frame={value.SampleFrame} status=0x{value.Message.Byte0:X2} data={value.Message.Byte1},{value.Message.Byte2} length={value.Message.Length}");
            }
            throw;
        }
        global::System.Console.WriteLine(
            $"逻辑模型离线渲染完成：{outputPath}\n" +
            $"  ticks=[{compiled.StartTick},{compiled.EndTick})；canonical events={compiled.Events.Length}；peak units={compiled.Statistics.PeakChannelUnitCount}\n" +
            $"  frames={rendered.FrameCount}；WAVE bytes={rendered.FileByteCount}；Rendering allocations={rendered.RenderingThreadAllocatedBytes} B；renderer fault={renderer.Fault}");
        return rendered.RenderingThreadAllocatedBytes == 0
            && renderer.Fault.Code == AudioRenderFaultCode.None ? 0 : 1;
    }

    private static int RunLogicalRealtime(string soundFontPath, string example)
    {
        MidoraProject project = CreateLogicalExample(example);
        project.SoundFontPath = soundFontPath;
        ProjectCompilationSession session = new(project);
        PrintCompilation(session.LastAttempt);
        if (!session.LastAttempt.IsConsumable)
        {
            return 1;
        }
        using BassWasapiPlaybackBackend backend = new(BassWasapiPlaybackOptions.PrototypeCandidate);
        using PlaybackController controller = new(session, backend);
        controller.Start();
        global::System.Console.WriteLine(
            $"逻辑模型实时播放：{example}；设备={backend.SelectedDevice?.Name}；actual={backend.ActualSampleRate} Hz");
        WaitForRealtimeCompletion(
            () => backend.IsCompleted,
            () => backend.CallbackFaulted || backend.RendererFault.Code != AudioRenderFaultCode.None);
        controller.Stop();
        global::System.Console.WriteLine(
            $"播放结束：callback allocations={backend.CallbackAllocatedBytes} B；render-thread allocations={backend.RenderingThreadAllocatedBytes} B；underruns={backend.UnderrunCount}；callback fault={backend.CallbackFaulted}；renderer fault={backend.RendererFault}");
        return backend.CallbackAllocatedBytes == 0
            && backend.RenderingThreadAllocatedBytes == 0
            && backend.UnderrunCount == 0
            && !backend.CallbackFaulted
            && backend.RendererFault.Code == AudioRenderFaultCode.None ? 0 : 1;
    }

    private static int RunLogicalRealtimeChild(string repositoryRoot, string soundFontPath, string example)
    {
        MidoraProject project = CreateLogicalExample(example);
        project.SoundFontPath = soundFontPath;
        ProjectCompilationSession session = new(project);
        PrintCompilation(session.LastAttempt);
        if (!session.LastAttempt.IsConsumable)
        {
            return 1;
        }
        BassWasapiChildPlaybackOptions options = new(
            GetWorkerPath(repositoryRoot),
            GetBassNativeDirectory(),
            null,
            100,
            50,
            CreateRealtimeRendererSettings(InitialReleaseAudioRuntimePolicy.WorkFrameCount),
            AudioMasterSettings.LimiterV1,
            TimeSpan.FromSeconds(30));
        using BassWasapiChildPlaybackBackend backend = new(options);
        using PlaybackController controller = new(session, backend);
        controller.Start();
        global::System.Console.WriteLine($"逻辑模型子进程实时播放：{example}；actual={backend.ActualSampleRate} Hz");
        WaitForRealtimeCompletion(() => backend.IsCompleted, () => backend.ChildFaulted);
        controller.Stop();
        global::System.Console.WriteLine(
            $"播放结束：callback allocations={backend.CallbackAllocatedBytes} B；child allocations={backend.ChildRenderingAllocatedBytes} B；IPC underruns={backend.UnderrunCount}；child fault={backend.ChildFaulted}");
        return backend.CallbackAllocatedBytes == 0
            && backend.ChildRenderingAllocatedBytes == 0
            && backend.UnderrunCount == 0
            && !backend.ChildFaulted ? 0 : 1;
    }

    private static void WaitForRealtimeCompletion(Func<bool> completed, Func<bool> faulted)
    {
        long deadline = Environment.TickCount64 + 120_000;
        while (!completed() && !faulted())
        {
            if (Environment.TickCount64 >= deadline)
            {
                throw new TimeoutException("逻辑模型实时播放未在 120 秒内结束。");
            }
            Thread.Sleep(10);
        }
        Thread.Sleep(100);
    }

    private static void PrintCompilation(CanonicalCompiledResult result)
    {
        foreach (CompilerDiagnostic diagnostic in result.Diagnostics)
        {
            global::System.Console.WriteLine(
                $"[{diagnostic.Severity}] {diagnostic.Code}: {diagnostic.Message} tick={diagnostic.Source.Tick}");
        }
        global::System.Console.WriteLine(
            $"编译：consumable={result.IsConsumable}；events={result.Events.Length}；instances={result.Statistics.ExpandedInstanceCount}；peak units={result.Statistics.PeakChannelUnitCount}");
    }

    private static MidoraProject CreateLogicalExample(string name) => name.ToLowerInvariant() switch
    {
        "segments" => CreateSegmentLifecycleExample(),
        "subvoices" => CreateSubVoiceMappingExample(),
        "tempo-loop" => CreateTempoLoopExample(),
        _ => throw new ArgumentException("示例必须是 segments、subvoices 或 tempo-loop。", nameof(name))
    };

    private static MidoraProject CreateSegmentLifecycleExample()
    {
        MidoraProject project = new(480);
        project.SetEndMarker(3_840);
        EventInstrument piano = CreatePianoInstrument(project, "分段旋律", 480, ShortNoteLifecycle.CutAtNoteOff);
        piano.SubVoices[0].Events.Add(TemplateEvent.Note(project, 0, 430, 60, 102));
        project.EventInstruments.Add(piano);
        LogicalTrack track = new(project) { Name = "两个相邻 Segment", EventInstrumentId = piano.Id };
        Segment left = new(project) { ProjectStartTick = 0, LengthTicks = 1_920, ContentOffsetTick = 0 };
        AddNotes(project, left, 0, [60, 64, 67, 72], 360);
        Segment right = new(project) { ProjectStartTick = 1_920, LengthTicks = 1_920, ContentOffsetTick = 480 };
        right.Notes.Add(new LogicalNote(project)
        {
            StartTick = 0,
            LengthTicks = 360,
            Note = 36,
            Velocity = 127
        });
        AddNotes(project, right, 480, [71, 67, 64, 60], 360);
        track.Segments.Add(left);
        track.Segments.Add(right);
        project.Tracks.Add(track);
        return project;
    }

    private static MidoraProject CreateSubVoiceMappingExample()
    {
        MidoraProject project = new(480);
        project.SetEndMarker(3_840);
        LogicalParameterDefinition expressionParameter = new(project)
        {
            Name = "力度包络",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1,
            DefaultValue = 0.35
        };
        EventInstrument piano = new(project)
        {
            Name = "三 SubVoice 和弦",
            RootNote = 60,
            TemplateLengthTicks = 480,
            RequiresChannelIsolation = true,
            OverlapPolicy = OverlapPolicy.LetOverlap,
            LongLifecycle = LongNoteLifecycle.HoldLastState
        };
        foreach (int interval in new[] { 0, 4, 7 })
        {
            SubVoice voice = new(project) { Name = $"interval {interval}" };
            voice.Events.Add(TemplateEvent.Note(project, 0, 430, 60 + interval, 94));
            piano.SubVoices.Add(voice);
        }
        piano.LogicalParameters.Add(expressionParameter);
        foreach (SubVoice voice in piano.SubVoices)
        {
            LogicalParameterMapping expression = new(project)
            {
                ParameterId = expressionParameter.Id,
                SubVoiceId = voice.Id,
                Target = MidiValueTarget.ControlChange(11)
            };
            expression.Steps.Add(new ValueMappingStep(project)
            {
                Source = MappingSource.LogicalParameter,
                LogicalParameterId = expressionParameter.Id,
                Operation = MappingOperation.Remap,
                SourceMinimum = 0,
                SourceMaximum = 1,
                TargetMinimum = 32,
                TargetMaximum = 127
            });
            expression.TargetSettings.Overflow = MappingOverflow.Clamp;
            piano.ParameterMappings.Add(expression);
        }
        project.EventInstruments.Add(piano);
        LogicalTrack track = new(project) { Name = "参数自动化", EventInstrumentId = piano.Id };
        Segment segment = new(project) { LengthTicks = 3_840 };
        LogicalParameterLane lane = new(project) { ParameterId = expressionParameter.Id };
        lane.Points.Add(new(project, 0, 0.25));
        lane.Points.Add(new(project, 1_920, 1));
        lane.Points.Add(new(project, 3_839, 0.45));
        segment.ParameterLanes.Add(lane);
        AddNotes(project, segment, 0, [48, 53, 55, 48], 900, spacing: 960);
        track.Segments.Add(segment);
        project.Tracks.Add(track);
        return project;
    }

    private static MidoraProject CreateTempoLoopExample()
    {
        MidoraProject project = new(480);
        project.Conductor.Tempos.Add(new(project, 1_920, 90m));
        project.Conductor.Tempos.Add(new(project, 3_360, 150m));
        project.SetEndMarker(4_800);
        EventInstrument piano = CreatePianoInstrument(project, "长音循环琶音", 480, ShortNoteLifecycle.CutAtNoteOff);
        piano.RequiresChannelIsolation = true;
        piano.LoopStartTick = 120;
        piano.LoopEndTick = 360;
        piano.SubVoices[0].Events.Add(TemplateEvent.Note(project, 120, 100, 60, 92));
        piano.SubVoices[0].Events.Add(TemplateEvent.Note(project, 240, 100, 67, 84));
        project.EventInstruments.Add(piano);
        LogicalTrack track = new(project) { Name = "Tempo + Loop", EventInstrumentId = piano.Id };
        Segment segment = new(project) { LengthTicks = 4_800 };
        segment.Notes.Add(new LogicalNote(project)
        {
            StartTick = 0,
            LengthTicks = 2_400,
            Note = 48,
            Velocity = 100
        });
        segment.Notes.Add(new LogicalNote(project)
        {
            StartTick = 2_400,
            LengthTicks = 2_400,
            Note = 53,
            Velocity = 100
        });
        track.Segments.Add(segment);
        project.Tracks.Add(track);
        return project;
    }

    private static EventInstrument CreatePianoInstrument(
        MidoraProject project,
        string name,
        long templateLength,
        ShortNoteLifecycle lifecycle)
    {
        EventInstrument result = new(project)
        {
            Name = name,
            RootNote = 60,
            TemplateLengthTicks = templateLength,
            ShortLifecycle = lifecycle,
            OverlapPolicy = OverlapPolicy.Warn
        };
        result.SubVoices.Add(new SubVoice(project) { Name = "Piano" });
        return result;
    }

    private static void AddNotes(
        MidoraProject project,
        Segment segment,
        long firstTick,
        IReadOnlyList<int> notes,
        long length,
        long spacing = 480)
    {
        for (int i = 0; i < notes.Count; i++)
        {
            segment.Notes.Add(new LogicalNote(project)
            {
                StartTick = checked(firstTick + (i * spacing)),
                LengthTicks = length,
                Note = notes[i],
                Velocity = 100
            });
        }
    }
}
