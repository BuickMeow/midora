# Midora 人工音频验收结果快照 — 2026-08-07

状态：已记录；存在听感失败和测试阻塞；本次不修改代码

来源清单：`misc/Midora-Manual-Audio-Acceptance.md`

本文件保存产品所有者返回的 M-AUD-001～012 首轮人工结果。第 6 节原始记录不得在后续修复时改写；后续复测应追加新快照，不覆盖本次失败现象或终端输出。分析与计划不等同于已确认根因或已完成修复。

## 1. 结果摘要

| 项目 | 本轮判定 | 记录 |
|---|---|---|
| M-AUD-001 | 通过 | Segment 边界与隐藏内容符合预期。 |
| M-AUD-002 | 失败 | 四个三和弦均可听见，整体先增强后回落；第一、第三、第四和弦末尾出现音量突然增大。其他听感符合预期。 |
| M-AUD-003 | 通过，已澄清预期 | 可听到两个换调实例及实例边界附近的间隔；当前示例确实会产生该可听间隔，详见第 2 节。 |
| M-AUD-004 | 通过 | 听感与 M-AUD-001 一致；Beats Flex、48 kHz；callback/render-thread allocations 均为 0 B、underruns=0、callback fault=False、renderer fault=None。 |
| M-AUD-005 | 失败；技术指标通过 | 听感与 M-AUD-002 一致，即同样存在第一、第三、第四和弦末尾音量突然增大；Beats Flex、48 kHz；零分配、零 underrun 且无 callback/renderer fault。技术指标不抵消听感失败。 |
| M-AUD-006 | 通过 | 听感与 M-AUD-003 一致，实例边界附近间隔符合当前示例；Beats Flex、48 kHz；callback/render-thread allocations 均为 0 B、underruns=0、callback fault=False、renderer fault=None。 |
| M-AUD-007 | 阻塞，未进入播放 | 正式子进程启动前拒绝 managed `.dll` Worker；没有形成子进程 Segment 人耳结果。 |
| M-AUD-008 | 阻塞，未进入播放 | 同一 Worker 路径错误；没有形成子进程 SubVoice 人耳结果。 |
| M-AUD-009 | 阻塞，未进入播放 | 同一 Worker 路径错误；没有形成子进程 Tempo/Loop 人耳结果。 |
| M-AUD-010 | 通过 | 只枚举一个 enabled output endpoint：系统默认 Beats Flex 耳机，48 kHz；静音 probe 27 callbacks、0 B callback allocation、无 callback fault。 |
| M-AUD-011 | 阻塞，未执行目标设备操作 | 在播放开始前被 managed `.dll` Worker 门拒绝；不能据此判断默认设备切换行为。 |
| M-AUD-012 | 阻塞，未执行目标设备操作 | 在播放开始前被 managed `.dll` Worker 门拒绝；不能据此判断活动设备移除/禁用行为。 |

M-AUD-004～006 的首次返回只包含“与 001～003 听感一致”；随后补充的完整控制台输出见第 7 节，已经关闭三项零分配、underrun、callback fault 与 renderer fault 技术指标。M-AUD-005 仍因人耳音量突增判定失败。

## 2. 对 M-AUD-003 的确认答复

确认结果分为两层：

- 两个 Logical Note 实例的 Project 范围分别为 `[0,2400)` 与 `[2400,4800)`，在 tick 2400 首尾相接；实例范围本身没有间隔。
- 当前 Event Instrument 的 Loop 是 `[120,360)`，音符位于 local tick 120、240，长度均为 100 tick。第一实例最后一个可听音符位于 Project tick `[2280,2380)`；第二实例的第一个音符位于 `[2520,2620)`。因此两个实例边界附近确实存在 `2520 - 2380 = 140` tick 的音符静音区间。
- 该区间处于 90 BPM、TPQ 480，理论时长约为 `140 / 480 × 60 / 90 = 0.19444` 秒。

结论：产品所有者听到的间隔与当前示例构造一致。准确表述应是“实例首尾相接，但跨实例边界存在约 140 tick 的可听音符空隙”。原清单中的“持续听到”和“不得产生空洞”容易被理解为完全无静音，需要在后续文档修订中改成不禁止该示例有意产生的音符空隙。

## 3. M-AUD-002 / M-AUD-005 初步分析

已确认源码事实：

- 四个实例从 Project tick `0`、`960`、`1920`、`2880` 开始，每个 Gate Length 是 900 tick。
- CC11 从 Logical Parameter 映射到 `32..127`；参数曲线从 tick 0 的 0.25 上升到 tick 1920 的 1.0，再下降到 tick 3839 的 0.45。
- Compiler 在每个实例结束 tick 为所有使用过的目标发出 Reset；CC11 没有 Project 自定义 Reset 时，内置 Reset 值是 127。
- 第二个实例结束时曲线本来已经接近峰值，Reset 到 127 的幅度很小；第一、第三、第四实例结束时的 CC11 明显低于 127。

高可信但尚未通过专门诊断测试确认的推断：和弦 NoteOff 后仍存在 SF2 release 声音，实例末尾 CC11 突然 Reset 到 127，使 release 尾部音量上跳。该推断与“第一、第三、第四明显，第二不明显”的观察精确吻合。仍需用 canonical 事件 trace 与实例末尾 PCM 包络测量确认，不能在本快照中写成最终根因。

本轮不判断应修改 Compiler Reset 语义、BASSMIDI 边界处理，还是只修改人工示例构造；该选择必须在自动复现并重新核对 SRS 的硬边界/Reset 规则后作出。

## 4. M-AUD-007～009、011～012 阻塞分析

已确认直接原因：Console 的 `GetWorkerPath` 当前固定返回：

```text
src/midora-audio/Midora.Audio.Bass.Worker/bin/<Configuration>/net10.0/Midora.Audio.Bass.Worker.dll
```

正式 `BassWasapiChildPlaybackBackend` 会调用不允许 test-only managed Worker 的 Probe；`BassMidiAudioWorkerSession.ValidateWorkerLaunchPath` 正确拒绝该 `.dll`。因此当前清单中的 `logic-realtime-child` 命令无法进入正式实时子进程播放。

判定：这是人工验收入口/Worker 发布路径未对齐，不是 M-AUD-007～009 音频语义失败的证据，也不是 M-AUD-011/012 设备故障处理失败的证据。五项均属于前置条件阻塞，正式 Native AOT `.exe` 拓扑仍未完成本轮人工验证。保护门本身按设计生效。

## 5. 后续计划（本次不实施）

1. 为 M-AUD-002/M-AUD-005 建立自动复现：冻结 canonical CC11/Reset tick，测量四个实例结束前后的 PCM 峰值或包络，验证 Reset→release 音量跳变推断。
2. 依据复现结果重新核对实例硬边界、Reset、SF2 release 的 SRS 语义，决定修复 Compiler/renderer，或调整会制造误判的人工示例；新增离线与实时一致性回归及再次人耳试听命令。
3. 让人工 Console 使用发布门生成并校验的 `win-x64` Native AOT Worker `.exe`，或接受显式 Worker `.exe` 参数；同步修订人工验收前置发布命令和路径检查。
4. 修复验收入口后先重跑 M-AUD-007～009，并保存 callback/child allocation、IPC underrun 与 fault 指标。
5. 子进程正式播放成立后再执行 M-AUD-011/012；保存设备切换/移除的完整终端输出，确认受控失败、无崩溃、卡死、旧设备继续发声或悬挂音。
6. M-AUD-004～006 的 callback/render-thread allocation、underrun 与 fault 指标已由本次补充闭合；后续只需随相关修复复测 M-AUD-005，不需要为缺失终端输出单独复测 M-AUD-004/006。

## 6. 产品所有者原始结果与终端输出

以下内容按本次返回原样保存。

### M-AUD-001：Segment 边界与隐藏内容

```text
符合预期
```

### M-AUD-002：SubVoice 和 Logical Parameter Mapping

```text
能听到四个三和弦，整体应先增强、后回落。
但是第一个和弦 -> 第二个和弦，第一个和弦末尾出现了音量突然增大；
第三个和弦 -> 第四个和弦，第三个和弦末尾出现了音量突然增大；
第四个和弦末尾出现了音量突然增大。
除了这些，其他均符合预期
```

### M-AUD-003：Tempo Map、Loop 与实例换调

```text
符合预期，但需要你确认一下：
我听起来确实是两个实例，且两个实例间有间隔。你确认两个实例间是否有间隔。
```

### M-AUD-004～006

```text
与001~003听感一致。
```

### M-AUD-007：子进程 Segment

```text
报错：
dotnet run --project $ConsoleProject -c Release --no-restore -- logic-realtime-child $Sf2 segments
编译：consumable=True；events=96；instances=8；peak units=1
System.IO.InvalidDataException: Formal realtime playback requires the win-x64 Native AOT .exe worker; managed .dll launch is test-only.
   at Midora.Audio.Bass.BassMidiAudioWorkerSession.ValidateWorkerLaunchPath(String workerPath, Boolean allowManagedTestWorker) in D:\Programing\midora\src\midora-audio\Midora.Audio.Bass\BassMidiAudioWorkerSession.cs:line 465
   at Midora.Audio.Bass.BassMidiAudioWorkerSession.ValidateCommon(String workerPath, String nativeDirectory, Int32 deviceBufferRequestMilliseconds, TimeSpan timeout, Boolean allowManagedTestWorker) in D:\Programing\midora\src\midora-audio\Midora.Audio.Bass\BassMidiAudioWorkerSession.cs:line 436
   at Midora.Audio.Bass.BassMidiAudioWorkerSession.Probe(String workerPath, String bassNativeDirectory, String deviceId, Int32 deviceBufferRequestMilliseconds, TimeSpan timeout) in D:\Programing\midora\src\midora-audio\Midora.Audio.Bass\BassMidiAudioWorkerSession.cs:line 165
   at Midora.Playback.BassWasapi.BassWasapiChildPlaybackBackend.Prepare() in D:\Programing\midora\src\midora-core\Midora.Playback.BassWasapi\BassWasapiChildPlaybackBackend.cs:line 122
   at Midora.Playback.PlaybackController.StartPreparedRange(Int64 cursorTick, Nullable`1 endTick, Boolean acquireEditLock) in D:\Programing\midora\src\midora-core\Midora.Playback\PlaybackController.cs:line 369
   at Midora.Playback.PlaybackController.Start(Nullable`1 cursorTick, Nullable`1 endTick) in D:\Programing\midora\src\midora-core\Midora.Playback\PlaybackController.cs:line 121
   at Midora.Audio.Bass.Tests.Console.Program.RunLogicalRealtimeChild(String repositoryRoot, String soundFontPath, String example) in D:\Programing\midora\src\midora-audio\Midora.Audio.Bass.Tests.Console\LogicalModelExamples.cs:line 177
   at Midora.Audio.Bass.Tests.Console.Program.Main(String[] args) in D:\Programing\midora\src\midora-audio\Midora.Audio.Bass.Tests.Console\Program.cs:line 57
```

### M-AUD-008：子进程 SubVoice

```text
报错：
dotnet run --project $ConsoleProject -c Release --no-restore -- logic-realtime-child $Sf2 subvoices
编译：consumable=True；events=516；instances=4；peak units=3
System.IO.InvalidDataException: Formal realtime playback requires the win-x64 Native AOT .exe worker; managed .dll launch is test-only.
   at Midora.Audio.Bass.BassMidiAudioWorkerSession.ValidateWorkerLaunchPath(String workerPath, Boolean allowManagedTestWorker) in D:\Programing\midora\src\midora-audio\Midora.Audio.Bass\BassMidiAudioWorkerSession.cs:line 465
   at Midora.Audio.Bass.BassMidiAudioWorkerSession.ValidateCommon(String workerPath, String nativeDirectory, Int32 deviceBufferRequestMilliseconds, TimeSpan timeout, Boolean allowManagedTestWorker) in D:\Programing\midora\src\midora-audio\Midora.Audio.Bass\BassMidiAudioWorkerSession.cs:line 436
   at Midora.Audio.Bass.BassMidiAudioWorkerSession.Probe(String workerPath, String bassNativeDirectory, String deviceId, Int32 deviceBufferRequestMilliseconds, TimeSpan timeout) in D:\Programing\midora\src\midora-audio\Midora.Audio.Bass\BassMidiAudioWorkerSession.cs:line 165
   at Midora.Playback.BassWasapi.BassWasapiChildPlaybackBackend.Prepare() in D:\Programing\midora\src\midora-core\Midora.Playback.BassWasapi\BassWasapiChildPlaybackBackend.cs:line 122
   at Midora.Playback.PlaybackController.StartPreparedRange(Int64 cursorTick, Nullable`1 endTick, Boolean acquireEditLock) in D:\Programing\midora\src\midora-core\Midora.Playback\PlaybackController.cs:line 369
   at Midora.Playback.PlaybackController.Start(Nullable`1 cursorTick, Nullable`1 endTick) in D:\Programing\midora\src\midora-core\Midora.Playback\PlaybackController.cs:line 121
   at Midora.Audio.Bass.Tests.Console.Program.RunLogicalRealtimeChild(String repositoryRoot, String soundFontPath, String example) in D:\Programing\midora\src\midora-audio\Midora.Audio.Bass.Tests.Console\LogicalModelExamples.cs:line 177
   at Midora.Audio.Bass.Tests.Console.Program.Main(String[] args) in D:\Programing\midora\src\midora-audio\Midora.Audio.Bass.Tests.Console\Program.cs:line 57
```

### M-AUD-009：子进程 Tempo/Loop

```text
报错：
dotnet run --project $ConsoleProject -c Release --no-restore -- logic-realtime-child $Sf2 tempo-loop
编译：consumable=True；events=106；instances=2；peak units=2
System.IO.InvalidDataException: Formal realtime playback requires the win-x64 Native AOT .exe worker; managed .dll launch is test-only.
   at Midora.Audio.Bass.BassMidiAudioWorkerSession.ValidateWorkerLaunchPath(String workerPath, Boolean allowManagedTestWorker) in D:\Programing\midora\src\midora-audio\Midora.Audio.Bass\BassMidiAudioWorkerSession.cs:line 465
   at Midora.Audio.Bass.BassMidiAudioWorkerSession.ValidateCommon(String workerPath, String nativeDirectory, Int32 deviceBufferRequestMilliseconds, TimeSpan timeout, Boolean allowManagedTestWorker) in D:\Programing\midora\src\midora-audio\Midora.Audio.Bass\BassMidiAudioWorkerSession.cs:line 436
   at Midora.Audio.Bass.BassMidiAudioWorkerSession.Probe(String workerPath, String bassNativeDirectory, String deviceId, Int32 deviceBufferRequestMilliseconds, TimeSpan timeout) in D:\Programing\midora\src\midora-audio\Midora.Audio.Bass\BassMidiAudioWorkerSession.cs:line 165
   at Midora.Playback.BassWasapi.BassWasapiChildPlaybackBackend.Prepare() in D:\Programing\midora\src\midora-core\Midora.Playback.BassWasapi\BassWasapiChildPlaybackBackend.cs:line 122
   at Midora.Playback.PlaybackController.StartPreparedRange(Int64 cursorTick, Nullable`1 endTick, Boolean acquireEditLock) in D:\Programing\midora\src\midora-core\Midora.Playback\PlaybackController.cs:line 369
   at Midora.Playback.PlaybackController.Start(Nullable`1 cursorTick, Nullable`1 endTick) in D:\Programing\midora\src\midora-core\Midora.Playback\PlaybackController.cs:line 121
   at Midora.Audio.Bass.Tests.Console.Program.RunLogicalRealtimeChild(String repositoryRoot, String soundFontPath, String example) in D:\Programing\midora\src\midora-audio\Midora.Audio.Bass.Tests.Console\LogicalModelExamples.cs:line 177
   at Midora.Audio.Bass.Tests.Console.Program.Main(String[] args) in D:\Programing\midora\src\midora-audio\Midora.Audio.Bass.Tests.Console\Program.cs:line 57
```

### M-AUD-010：输出端点枚举

```text
符合预期。
dotnet run --project $ConsoleProject -c Release --no-restore -- wasapi-probe
BASSWASAPI enabled output devices（API=0x02040401；enumeration terminal error=23）：
  [0] default=True；耳机 (Beats Flex)；48000 Hz；{0.0.0.00000000}.{a5500e50-f4d9-4f40-8c8a-edebccabe430}
静音 probe 完成：actual=AudioFormat { SampleRate = 48000, ChannelCount = 2, SampleFormat = Float32, BytesPerSample = 4, BytesPerFrame = 8 }；device buffer=2400 frames；callbacks=27；callback allocations=0 B；callback fault=False
```

### M-AUD-011：跟随系统默认时切换默认输出

```text
报错：
dotnet run --project $ConsoleProject -c Release --no-restore -- logic-realtime-child $Sf2 tempo-loop
编译：consumable=True；events=106；instances=2；peak units=2
System.IO.InvalidDataException: Formal realtime playback requires the win-x64 Native AOT .exe worker; managed .dll launch is test-only.
   at Midora.Audio.Bass.BassMidiAudioWorkerSession.ValidateWorkerLaunchPath(String workerPath, Boolean allowManagedTestWorker) in D:\Programing\midora\src\midora-audio\Midora.Audio.Bass\BassMidiAudioWorkerSession.cs:line 465
   at Midora.Audio.Bass.BassMidiAudioWorkerSession.ValidateCommon(String workerPath, String nativeDirectory, Int32 deviceBufferRequestMilliseconds, TimeSpan timeout, Boolean allowManagedTestWorker) in D:\Programing\midora\src\midora-audio\Midora.Audio.Bass\BassMidiAudioWorkerSession.cs:line 436
   at Midora.Audio.Bass.BassMidiAudioWorkerSession.Probe(String workerPath, String bassNativeDirectory, String deviceId, Int32 deviceBufferRequestMilliseconds, TimeSpan timeout) in D:\Programing\midora\src\midora-audio\Midora.Audio.Bass\BassMidiAudioWorkerSession.cs:line 165
   at Midora.Playback.BassWasapi.BassWasapiChildPlaybackBackend.Prepare() in D:\Programing\midora\src\midora-core\Midora.Playback.BassWasapi\BassWasapiChildPlaybackBackend.cs:line 122
   at Midora.Playback.PlaybackController.StartPreparedRange(Int64 cursorTick, Nullable`1 endTick, Boolean acquireEditLock) in D:\Programing\midora\src\midora-core\Midora.Playback\PlaybackController.cs:line 369
   at Midora.Playback.PlaybackController.Start(Nullable`1 cursorTick, Nullable`1 endTick) in D:\Programing\midora\src\midora-core\Midora.Playback\PlaybackController.cs:line 121
   at Midora.Audio.Bass.Tests.Console.Program.RunLogicalRealtimeChild(String repositoryRoot, String soundFontPath, String example) in D:\Programing\midora\src\midora-audio\Midora.Audio.Bass.Tests.Console\LogicalModelExamples.cs:line 177
   at Midora.Audio.Bass.Tests.Console.Program.Main(String[] args) in D:\Programing\midora\src\midora-audio\Midora.Audio.Bass.Tests.Console\Program.cs:line 57
```

### M-AUD-012：活动设备移除或禁用

```text
dotnet run --project $ConsoleProject -c Release --no-restore -- logic-realtime-child $Sf2 tempo-loop
编译：consumable=True；events=106；instances=2；peak units=2
System.IO.InvalidDataException: Formal realtime playback requires the win-x64 Native AOT .exe worker; managed .dll launch is test-only.
   at Midora.Audio.Bass.BassMidiAudioWorkerSession.ValidateWorkerLaunchPath(String workerPath, Boolean allowManagedTestWorker) in D:\Programing\midora\src\midora-audio\Midora.Audio.Bass\BassMidiAudioWorkerSession.cs:line 465
   at Midora.Audio.Bass.BassMidiAudioWorkerSession.ValidateCommon(String workerPath, String nativeDirectory, Int32 deviceBufferRequestMilliseconds, TimeSpan timeout, Boolean allowManagedTestWorker) in D:\Programing\midora\src\midora-audio\Midora.Audio.Bass\BassMidiAudioWorkerSession.cs:line 436
   at Midora.Audio.Bass.BassMidiAudioWorkerSession.Probe(String workerPath, String bassNativeDirectory, String deviceId, Int32 deviceBufferRequestMilliseconds, TimeSpan timeout) in D:\Programing\midora\src\midora-audio\Midora.Audio.Bass\BassMidiAudioWorkerSession.cs:line 165
   at Midora.Playback.BassWasapi.BassWasapiChildPlaybackBackend.Prepare() in D:\Programing\midora\src\midora-core\Midora.Playback.BassWasapi\BassWasapiChildPlaybackBackend.cs:line 122
   at Midora.Playback.PlaybackController.StartPreparedRange(Int64 cursorTick, Nullable`1 endTick, Boolean acquireEditLock) in D:\Programing\midora\src\midora-core\Midora.Playback\PlaybackController.cs:line 369
   at Midora.Playback.PlaybackController.Start(Nullable`1 cursorTick, Nullable`1 endTick) in D:\Programing\midora\src\midora-core\Midora.Playback\PlaybackController.cs:line 121
   at Midora.Audio.Bass.Tests.Console.Program.RunLogicalRealtimeChild(String repositoryRoot, String soundFontPath, String example) in D:\Programing\midora\src\midora-audio\Midora.Audio.Bass.Tests.Console\LogicalModelExamples.cs:line 177
   at Midora.Audio.Bass.Tests.Console.Program.Main(String[] args) in D:\Programing\midora\src\midora-audio\Midora.Audio.Bass.Tests.Console\Program.cs:line 57
```

## 7. 产品所有者补充的 M-AUD-004～006 完整控制台输出

以下内容按后续补充原样追加，不覆盖第 6 节首次返回。

### M-AUD-004

```text
dotnet run --project $ConsoleProject -c Release --no-restore -- logic-realtime $Sf2 segments
编译：consumable=True；events=96；instances=8；peak units=1
逻辑模型实时播放：segments；设备=耳机 (Beats Flex)；actual=48000 Hz
播放结束：callback allocations=0 B；render-thread allocations=0 B；underruns=0；callback fault=False；renderer fault=AudioRenderFault { Code = None, NativeErrorCode = 0, ZeroBasedPortNumber = -1, SampleFrame = -1 }
```

### M-AUD-005

```text
dotnet run --project $ConsoleProject -c Release --no-restore -- logic-realtime $Sf2 subvoices
编译：consumable=True；events=516；instances=4；peak units=3
逻辑模型实时播放：subvoices；设备=耳机 (Beats Flex)；actual=48000 Hz
播放结束：callback allocations=0 B；render-thread allocations=0 B；underruns=0；callback fault=False；renderer fault=AudioRenderFault { Code = None, NativeErrorCode = 0, ZeroBasedPortNumber = -1, SampleFrame = -1 }
```

### M-AUD-006

```text
dotnet run --project $ConsoleProject -c Release --no-restore -- logic-realtime $Sf2 tempo-loop
编译：consumable=True；events=106；instances=2；peak units=2
逻辑模型实时播放：tempo-loop；设备=耳机 (Beats Flex)；actual=48000 Hz
播放结束：callback allocations=0 B；render-thread allocations=0 B；underruns=0；callback fault=False；renderer fault=AudioRenderFault { Code = None, NativeErrorCode = 0, ZeroBasedPortNumber = -1, SampleFrame = -1 }
```
