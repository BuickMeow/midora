# Midora 非 UI 人工试听与物理音频设备验收

状态：待产品所有者执行
日期：2026-08-06

本清单只包含无法由自动测试可靠替代的人耳听音和物理设备操作。WAVE 结构、sample-frame 时序、工作块确定性、零托管分配、短读、underrun 状态机、设备通知逻辑及故障清理已经进入自动测试，不要求人工重复解析文件或读取二进制。

## 1. 前置条件

使用 PowerShell 7，从仓库根目录执行：

```powershell
Set-Location 'D:\Programing\midora'
$Sf2 = 'D:\Programing\midora-spike\.testdata\test.sf2'
$ConsoleProject = 'src/midora-audio/Midora.Audio.Bass.Tests.Console/Midora.Audio.Bass.Tests.Console.csproj'
$BassDirectory = Join-Path $env:LOCALAPPDATA 'Midora\Native\BASS\win-x64'

Test-Path -LiteralPath $Sf2
Get-ChildItem -LiteralPath $BassDirectory -File
dotnet build 'src/midora-audio/midora-audio.slnx' -c Release --no-restore
```

前两个检查必须分别得到 `True`，并能看到 `bass.dll`、`bassmidi.dll`、`basswasapi.dll`。开始前把系统输出音量调到安全水平；以下示例使用 Limiter v1，但这不代替安全音量设置。

## 2. 离线 WAVE 人耳试听

先重新生成本次机器上的三份文件：

```powershell
$ListenDirectory = 'D:\Programing\midora\artifacts\manual-listening-current'
dotnet run --project $ConsoleProject -c Release --no-restore -- logic-examples $Sf2 $ListenDirectory
```

命令应退出为 0；三项输出都应显示 `Rendering allocations=0 B`，且 `renderer fault` 中的 `Code = None`。

2026-08-06 已自动确认同一三项计划的进程内与子进程离线结果逐字节相同：

| 示例 | SHA-256 |
|---|---|
| segments | `7D3001050F0EE44B7C19B50A83AFC486EEE415E0EEDF469255F4FAF0219ADDFF` |
| subvoices | `1B5497B25A8FD229198E65B7FAE5061EA61E658318B50C7CE074C6CB37DC3DBD` |
| tempo-loop | `D08C2D4D1256B9C7B90914608BF030251CE761522C178268CAAB9C499EE8E87A` |

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
- 末尾不得悬挂、爆音或继续自然 tail。

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

播放开始后拔出或在 Windows 中禁用该输出设备。预期与 M-AUD-011 相同：任务受控失败、返回非 0，无崩溃、卡死或悬挂音，并保存完整终端输出。若当前没有可安全操作的设备，记录“环境不具备”，不要模拟拔线。

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

人工结果只关闭对应的人耳/物理设备门，不替代 849 项自动测试、Native AOT 发布门或 Q-NUI-013 的第三方二进制许可放行。
