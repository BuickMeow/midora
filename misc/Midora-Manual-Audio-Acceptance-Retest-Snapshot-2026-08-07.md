# Midora 人工音频验收复测快照（2026-08-07）

状态：不可改写的原始结果快照
范围：Q-NUI-026 / Q-NUI-027 修复后的 M-AUD-002、005、007～009、011～012 复测

> 本文件保存用户返回的原始人工结果和失败输出。后续分析、修复和再次复测不得覆盖或改写本快照。

## 人工结果

```text
M-AUD-002：通过；备注： 中间有短暂截断，但不爆音。整体听感符合预期。
M-AUD-005：通过；
M-AUD-007：通过；
M-AUD-008：通过；
M-AUD-009：通过；
M-AUD-011：通过；
M-AUD-012：失败；备注：在播放中途拔掉/禁用测试使用中的设备，终端输出均为：
```

用户同时确认：如果拔掉或禁用的设备不是本次播放正在使用的设备，当前播放不受影响；该行为符合预期。

## M-AUD-012 完整失败输出

```text
dotnet run --project $ConsoleProject -c Release --no-restore -- logic-realtime-child $Sf2 tempo-loop
编译：consumable=True；events=108；instances=2；peak units=2
逻辑模型子进程实时播放：tempo-loop；actual=48000 Hz
Midora.Audio.MidoraAudioException: The audio worker did not stop cleanly; state=Faulted; fault=1; exitCode=1; stderr=Midora.Audio.MidoraAudioException: Audio worker fault: callback=False; deviceLost=True; defaultMappingChanged=False; ring=False; renderer=AudioRenderFault { Code = None, NativeErrorCode = 0, ZeroBasedPortNumber = -1, SampleFrame = -1 }.
   at Midora.Audio.Bass.Worker.Program.RunPlayback(String[], SharedAudioWorkerControl) + 0xed5
   at Midora.Audio.Bass.Worker.Program.Main(String[] args) + 0x1c7

   at Midora.Playback.BassWasapi.BassWasapiChildPlaybackBackend.Stop(Boolean flush) in D:\Programing\midora\src\midora-core\Midora.Playback.BassWasapi\BassWasapiChildPlaybackBackend.cs:line 196
   at Midora.Playback.PlaybackController.StopCore(Boolean applyCursorBehavior, Boolean releaseEditLock) in D:\Programing\midora\src\midora-core\Midora.Playback\PlaybackController.cs:line 505
   at Midora.Playback.PlaybackController.Stop() in D:\Programing\midora\src\midora-core\Midora.Playback\PlaybackController.cs:line 146
   at Midora.Audio.Bass.Tests.Console.Program.RunLogicalRealtimeChild(String repositoryRoot, String soundFontPath, String example) in D:\Programing\midora\src\midora-audio\Midora.Audio.Bass.Tests.Console\LogicalModelExamples.cs:line 180
   at Midora.Audio.Bass.Tests.Console.Program.Main(String[] args) in D:\Programing\midora\src\midora-audio\Midora.Audio.Bass.Tests.Console\Program.cs:line 57
```

## 用户补充的正式行为决定

- 活动输出设备在播放途中被拔出或禁用时，主应用进程不得崩溃。
- 检测到活动设备不可用后，必须断开当前输出端并受控停止当前播放或预览。
- 运行时必须明确要求用户手动重新指定输出设备。
- 在用户完成显式选择前，不得自动或静默切换到任何其他设备。
- 非活动设备被拔出或禁用不得中断当前播放。
