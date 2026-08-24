# Midora 非 UI 人工试听与物理音频设备验收

状态：2026-08-07 全部完成；M-AUD-001～012 均通过
日期：2026-08-06

> 后续状态（2026-08-16）：本文记录的 2026-08-07 allocation-group CC120/目标 Reset 行为是历史验收基线，其适用范围已由 ADR-CORE-043 取代。当前普通 Gate/Release/Tail/group 结束只做必要的精确 NoteOff；Project Reset Defaults 在 Segment lane 首次激活或非重叠复用激活时建立基线，并只在 Segment/消费者范围硬边界完成最终清理。CC120 也只在这些硬边界发送；原 192-frame BASSMIDI 实测仍用于证明硬边界清理特性。

首轮不可改写结果快照：`misc/Midora-Manual-Audio-Acceptance-Snapshot-2026-08-07.md`

第二轮不可改写结果快照：`misc/Midora-Manual-Audio-Acceptance-Retest-Snapshot-2026-08-07.md`

M-AUD-012 修复后通过快照：`misc/Midora-Manual-Audio-Acceptance-M-AUD-012-Pass-Snapshot-2026-08-07.md`

本清单只包含无法由自动测试可靠替代的人耳听音和物理设备操作。WAVE 结构、sample-frame 时序、工作块确定性、零托管分配、短读、underrun 状态机、设备通知逻辑及故障清理已经进入自动测试，不要求人工重复解析文件或读取二进制。

首轮摘要：M-AUD-001、003、004、006、010 通过；M-AUD-002/M-AUD-005 在第一、第三、第四和弦末尾出现音量突增，其中 M-AUD-005 的零分配、underrun 与 fault 技术指标通过但不抵消听感失败；M-AUD-003/M-AUD-006 的实例边界可听间隔已确认符合当前示例构造；M-AUD-007～009、011～012 因 Console 仍传入 managed Worker `.dll` 而在正式播放前被保护门阻止。后续不得覆盖首轮快照，只能追加复测记录。

2026-08-07 修复进度：Console 已只解析显式或标准 publish 位置的 Native AOT `.exe`，标准路径和环境变量路径均通过实际子进程离线链，M-AUD-007～009/011/012 的启动前置阻塞已解除。Q-NUI-026 已采用并实施 allocation group 级清理：精确 NoteOff → CC120 All Sound Off → 状态 Reset。真实 `test.sf2` 证明 CC120 在 48 kHz 下经过 192 frames / 4 ms 的 BASSMIDI 防爆音衰减后严格静音，且只影响目标 Channel。原 SubVoice 例四个实例间隔在防爆音衰减后均为逐 sample 全零；示例时长、实例间隔与 CC11 包络未被修改。

修复后完整非 UI Release 门已通过 873/873、0 Skip、六个 solution 0 warning/0 error；当前源码的 Native AOT Worker 验证产物位于 `artifacts/non-ui-release-gate-q026-20260807`。该自动证据不替代以下人耳和物理设备复测。

第二轮结果：M-AUD-002、005、007～009、011 均通过；M-AUD-012 确认旧 Worker 能识别 `deviceLost=True`，但把设备丢失发布为普通 Faulted 并在主进程 Stop 清理时再次抛出，造成未处理异常堆栈。Q-NUI-028 已固定新语义：活动设备丢失时 Worker 先断开 WASAPI 输出并发布 `OutputDeviceUnavailable` 非故障终态，主进程受控停止、释放编辑锁、丢弃 sample-domain 缓存并设置显式重选设备门；用户调用 `SelectOutputDevice(...)` 前，播放与预览均被阻止，且不自动切换设备。共享控制内部 ABI 因新增终态升级为 v2。自动回归基线更新为 881 项；M-AUD-012 仍需物理设备单项复测。

Q-NUI-028 修复后完整非 UI Release 门已通过 881/881、0 Skip、六个 solution 0 warning/0 error；当前源码 Native AOT Worker 位于 `artifacts/non-ui-release-gate-q028-20260807/worker-win-x64/Midora.Audio.Bass.Worker.exe`。M-AUD-012 复测必须使用该 Worker，不能继续使用 Q-NUI-026 的旧产物。

M-AUD-012 修复后人工复测通过：活动设备丢失时 callback/child allocations 均为 0 B、IPC underrun=0、`child fault=False`，没有异常堆栈；输出端受控断开并明确要求人工重选，进程返回约定代码 2。至此 M-AUD-001～012 全部通过，当前没有遗留人耳或物理设备音频验收项。

## 1. 前置条件

使用 PowerShell 7，从仓库根目录执行：

```powershell
Set-Location 'D:\Programing\midora'
$Sf2 = 'D:\Programing\midora-spike\.testdata\test.sf2'
$ConsoleProject = 'src/midora-audio/Midora.Audio.Bass.Tests.Console/Midora.Audio.Bass.Tests.Console.csproj'
$WorkerProject = 'src/midora-audio/Midora.Audio.Bass.Worker/Midora.Audio.Bass.Worker.csproj'
$BassDirectory = Join-Path $env:LOCALAPPDATA 'Midora\Native\BASS\win-x64'
$WorkerPath = 'src/midora-audio/Midora.Audio.Bass.Worker/bin/Release/net10.0/win-x64/publish/Midora.Audio.Bass.Worker.exe'

Test-Path -LiteralPath $Sf2
Get-ChildItem -LiteralPath $BassDirectory -File
dotnet restore $WorkerProject -r win-x64 --locked-mode
dotnet publish $WorkerProject -c Release -r win-x64 --no-restore "-p:BassNativeDirectory=$BassDirectory"
Test-Path -LiteralPath $WorkerPath
Get-ChildItem -LiteralPath (Split-Path -Parent $WorkerPath) -File
$env:MIDORA_AUDIO_WORKER_PATH = (Resolve-Path -LiteralPath $WorkerPath).Path
dotnet build 'src/midora-audio/midora-audio.slnx' -c Release --no-restore
```

两个 `Test-Path` 必须分别得到 `True`。本机 BASS 目录和 Worker publish 目录都必须能看到 `bass.dll`、`bassmidi.dll`、`basswasapi.dll`；publish 目录还必须包含 `native-manifest.json`、`LICENSE` 与 `THIRD-PARTY-NOTICES.md`。开始前把系统输出音量调到安全水平；以下示例使用正式 Limiter，但这不代替安全音量设置。

## 2. 离线 WAVE 人耳试听

先重新生成本次机器上的三份文件：

```powershell
$ListenDirectory = 'D:\Programing\midora\artifacts\manual-listening-current'
dotnet run --project $ConsoleProject -c Release --no-restore -- logic-examples $Sf2 $ListenDirectory
```

命令应退出为 0；三项输出都应显示 `Rendering allocations=0 B`，且 `renderer fault` 中的 `Code = None`。

2026-08-07 Q-NUI-026 修复后已自动确认同一三项计划的进程内与子进程离线结果逐字节相同：

| 示例 | SHA-256 |
|---|---|
| segments | `A8BA7E2D9777DAB21B90E67BB4B6B233F96632249D575288ADC578E717DECD44` |
| subvoices | `8579C9CB460BE825FBF74A28ABEF4CF8FE390B7EB16773D36C576262985C8C85` |
| tempo-loop | `542086EF417D9B1DDCDA47960B8AE2F53293031D71CB6FC55A6C51A3FFBE4AD9` |

### M-AUD-001：Segment 边界与隐藏内容

```powershell
Invoke-Item "$ListenDirectory\midora-segments.wav"
```

预期听感：

- 只听到八个清晰钢琴音，音高顺序为 MIDI `60, 64, 67, 72, 71, 67, 64, 60`，即先上行后下行。
- 右侧 Segment 的 content offset 之前有一个 MIDI 36 的测试音；它必须完全听不到。
- Segment 切换和文件硬结束处不得有悬挂音、额外低音、爆音或结束后的 tail。

### M-AUD-002：SubVoice 和 Logical Parameter Mapping

```powershell
Invoke-Item "$ListenDirectory\midora-subvoices.wav"
```

预期听感：

- 听到四个三和弦；每个和弦都应同时包含根音、+4 和 +7 三个 SubVoice，不得间歇缺声部。
- 根音顺序为 MIDI `48, 53, 55, 48`。
- CC11 表情包络从约 25% 上升到中点的 100%，再下降到末尾约 45%；整体应先增强、后回落，不能出现块边界处突然归零或跳回初值。
- 每个和弦的 allocation group 结束时允许最多约 4 ms 的 BASSMIDI 防爆音衰减，随后必须静音；不得再出现第一、第三、第四和弦末尾的音量突然增大，也不得继续 SoundFont 自然 tail、悬挂或爆音。

### M-AUD-003：Tempo Map、Loop 与实例换调

```powershell
Invoke-Item "$ListenDirectory\midora-tempo-loop.wav"
```

预期听感：

- 持续听到循环的双音型；前半实例以 MIDI 48/55 为基准，后半整体上移 5 个半音到 53/60。
- 速度应从 120 BPM 变慢到 90 BPM，再加快到 150 BPM；变化点不得重触发错误音、丢音或产生空洞。
- 文件在硬结束处立即结束，不得有悬挂音或额外 tail。

## 3. 实时进程内对照链试听

以下三条命令逐条执行，每条都应自然播放完并退出为 0：

### M-AUD-004：进程内 Segment

```powershell
dotnet run --project $ConsoleProject -c Release --no-restore -- logic-realtime $Sf2 segments
```

### M-AUD-005：进程内 SubVoice

```powershell
dotnet run --project $ConsoleProject -c Release --no-restore -- logic-realtime $Sf2 subvoices
```

### M-AUD-006：进程内 Tempo/Loop

```powershell
dotnet run --project $ConsoleProject -c Release --no-restore -- logic-realtime $Sf2 tempo-loop
```

三项听感分别应与 M-AUD-001～003 相同。每条命令末尾还必须同时满足：`callback allocations=0 B`、`render-thread allocations=0 B`、`underruns=0`、`callback fault=False`，且 `renderer fault` 中的 `Code = None`。任何断续、重复、悬挂、爆音或明显音色/音量差异都记录为失败。

## 4. 独立音频子进程正式拓扑试听

以下三条命令逐条执行，每条都应自然播放完并退出为 0：

### M-AUD-007：子进程 Segment

```powershell
dotnet run --project $ConsoleProject -c Release --no-restore -- logic-realtime-child $Sf2 segments
```

### M-AUD-008：子进程 SubVoice

```powershell
dotnet run --project $ConsoleProject -c Release --no-restore -- logic-realtime-child $Sf2 subvoices
```

### M-AUD-009：子进程 Tempo/Loop

```powershell
dotnet run --project $ConsoleProject -c Release --no-restore -- logic-realtime-child $Sf2 tempo-loop
```

三项听感分别应与离线文件和进程内对照链相同。每条命令末尾还必须同时满足：`callback allocations=0 B`、`child allocations=0 B`、`IPC underruns=0`、`child fault=False`。自动测试已经证明进程内/子进程离线 WAVE 的字节一致性；本项只确认物理实时输出没有设备相关异常。

## 5. 物理 WASAPI 设备验收

### M-AUD-010：输出端点枚举

```powershell
dotnet run --project $ConsoleProject -c Release --no-restore -- wasapi-probe
```

预期：列出当前 Windows 中全部 enabled output endpoints，系统默认项标记正确；不得出现麦克风等输入端点、loopback input、已禁用、已拔出或 not-present 端点。结尾必须为 `callback allocations=0 B`、`callback fault=False`，且 callback 数大于 0。

### M-AUD-011：跟随系统默认时切换默认输出

先打开 Windows“声音输出”设置并准备切换到另一台可用输出设备，然后立即执行：

```powershell
dotnet run --project $ConsoleProject -c Release --no-restore -- logic-realtime-child $Sf2 tempo-loop
```

播放开始后立刻切换系统默认输出。预期当前任务受控停止并报告设备选择失效/子进程故障；此项预期进程返回非 0，不应把非 0 本身记录为缺陷。不得崩溃、卡死、继续向旧设备发声或留下悬挂音。请保存完整终端输出。

### M-AUD-012：活动设备移除或禁用

仅在有可安全拔出的 USB/蓝牙输出设备时执行。先将其设为系统默认并确认 M-AUD-010 能枚举，再运行：

```powershell
dotnet run --project $ConsoleProject -c Release --no-restore -- logic-realtime-child $Sf2 tempo-loop
```

播放开始后拔出或在 Windows 中禁用该输出设备。预期：

- 当前声音立即停止，旧输出端断开；不得崩溃、卡死、继续发声或留下悬挂音。
- Worker 以 `OutputDeviceUnavailable` 非故障终态受控退出；控制台不得打印 `MidoraAudioException` 堆栈或 `state=Faulted`。
- 控制台明确输出“请手动重新指定输出设备；Midora 不会自动切换设备”，并以专用返回码 `2` 结束本次无 UI 验收命令。
- 主应用运行时状态为 `PlaybackState.Stopped` 且 `OutputDeviceSelectionRequired=True`；显式调用 `SelectOutputDevice(...)` 前，后续播放和预览必须被阻止。
- 拔出或禁用非活动设备时，当前播放不得受影响。

若当前没有可安全操作的设备，记录“环境不具备”，不要模拟拔线。

## 6. 返回结果模板

请按以下格式返回；失败项附终端输出，并描述时间点、听到的现象和使用的输出设备：

```text
M-AUD-001：通过 / 失败 / 未执行；备注：
M-AUD-002：通过 / 失败 / 未执行；备注：
M-AUD-003：通过 / 失败 / 未执行；备注：
M-AUD-004：通过 / 失败 / 未执行；备注：
M-AUD-005：通过 / 失败 / 未执行；备注：
M-AUD-006：通过 / 失败 / 未执行；备注：
M-AUD-007：通过 / 失败 / 未执行；备注：
M-AUD-008：通过 / 失败 / 未执行；备注：
M-AUD-009：通过 / 失败 / 未执行；备注：
M-AUD-010：通过 / 失败 / 未执行；备注：
M-AUD-011：通过 / 失败 / 未执行；备注：
M-AUD-012：通过 / 失败 / 环境不具备；备注：
```

人工结果只关闭对应的人耳/物理设备门，不替代 881 项自动测试、Native AOT 发布门或 Q-NUI-013 的第三方二进制许可放行。
