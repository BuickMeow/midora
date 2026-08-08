# Midora 人工音频阻塞项修复 Requirement Trace

状态：Q-NUI-026～Q-NUI-028 已实施；完整自动门和 M-AUD-001～012 人工验收全部通过
日期：2026-08-07
需求基线：SRS 第 10.15～10.16、12.10～12.11、13.27～13.30、14.13～14.14、15.1、15.4、21.7 章及 INV-009、INV-015、INV-018、INV-019、INV-025、INV-027、INV-028。

## 1. 输入与正式输出

- 输入是首轮人工快照中的 M-AUD-002/M-AUD-005 可听失败、M-AUD-007～009/011/012 启动阻塞，以及对应的 Project、canonical event、48 kHz PCM 和 Worker 启动路径证据。
- 正式音乐输出仍只能来自 `Project → Semantic Validation → Compilation → Canonical Compiled Result → Consumer`；验收 Console 不得改写 Reset、生命周期或同 tick 顺序。
- 正式子进程验收只允许现存的 `win-x64` Native AOT `Midora.Audio.Bass.Worker.exe`。托管 `.dll` 仍只允许程序集内部测试入口显式放行。
- 人工清单输出是可复制的发布准备命令、重新试听命令、预期技术指标和不可覆盖的结果快照；它不是 Project 源数据或产品配置。

## 2. 已确认根因与边界

### 2.1 SubVoice/Mapping 音量突增

- 四个实例范围是 `[0,900)`、`[960,1860)`、`[1920,2820)`、`[2880,3780)`；每个实例后存在 60 tick 间隔。
- 三个 SubVoice 的模板 Note 在实例局部 tick 430 发出 NoteOff；SoundFont release 在实例结束时仍可听。
- 每个实例结束时，编译器按当前 Reset 规则把该 SubVoice 使用过的 CC11 恢复为内建值 127。第一、第三、第四实例结束前的 CC11 明显低于 127，第二实例接近 127。
- 48 kHz PCM 在 tick 900、2820、3780 后的短窗 RMS/peak 明显上升，而 tick 1860 没有同类上升；模式与人工报告精确一致。因此“CC11 Reset 放大仍可听的 SF2 release”已从推断提升为确认根因。
- SRS 要求普通生命周期按“Release/Tail 完成 → 必要精确 NoteOff → Reset → Channel Unit 释放”，并允许 All Notes Off/All Sound Off 作为安全清理，但没有固定是否在每个正常 allocation group 结束时无条件发送 All Sound Off。Q-NUI-026 已明确采用 allocation group 级 CC120 方案。
- 真实 BASSMIDI/SF2 最小对比已确认：仅 NoteOff 的自然 release 在测试缓冲末端仍非零；frame 6000 发 CC120 后最后一个非零 frame 为 6191，48 kHz 下对应 4 ms 的 BASSMIDI 防爆音衰减，之后严格全零。该 4 ms 不是 SF2 自然 release，也不是 Limiter look-ahead（Limiter v1 是 zero-look-ahead）。
- 同一 BASSMIDI Stream 的双 Channel 对比中，目标 Channel 的 4 ms 衰减结束后，混合输出与仅保留非目标 Channel 的参考 PCM 逐字节相同；CC120 的作用域已实测确认为目标 MIDI Channel，不是全局或整 Stream。
- 编译器现按实际 allocation group 结束生成清理；精确 NoteOff 的 `CanonicalEventRole.NoteOff` 先于 CC120，CC120 再先于已使用状态目标的 Project/Global Reset。非隔离重叠实例中途结束不清理；相邻 group 同 tick 复用时先清旧组再初始化新组；硬范围结束同序；空 SubVoice 不发 CC120。

### 2.2 Native AOT Worker 入口

- 旧 Console 固定返回 `bin/<Configuration>/net10.0/Midora.Audio.Bass.Worker.dll`，因此正式实时 backend 在 Prepare 前正确拒绝启动。
- 修复后的解析顺序是：先使用 `MIDORA_AUDIO_WORKER_PATH` 指定的绝对 `.exe`；未指定时使用标准 `bin/<Configuration>/net10.0/win-x64/publish/Midora.Audio.Bass.Worker.exe`。
- 路径不存在时在 Console 边界给出包含发布准备动作的明确 `FileNotFoundException`；不回退到 managed Worker，也不搜索或选择历史 `artifacts/` 目录中的不确定产物。
- Worker 会话原有的现存文件、`.exe`、协议、原生基线、架构和启动握手校验继续生效；Console 路径解析不替代这些正式门。

### 2.3 活动输出设备丢失

- 第二轮人工测试确认，原实现能准确识别活动设备 `deviceLost=True`，且 callback、ring、renderer 没有伴随故障；问题是 Worker 把该已知终态作为普通 Faulted/exit 1，主进程 Stop 又把它作为清理失败再次抛出。完整输出保存在 `Midora-Manual-Audio-Acceptance-Retest-Snapshot-2026-08-07.md`。
- Q-NUI-028 固定运行时行为：活动设备丢失不是可继续播放的 Buffering，也不是可以自动选替代设备的恢复点。必须立即断开 WASAPI 输出、停止当前任务、释放锁和 sample-domain 状态，并要求显式设备选择。
- Worker 现发布 `AudioWorkerState.OutputDeviceUnavailable`、`faultCode=0` 并正常退出；该状态只由选中端点的 Disabled/Fail 通知产生。非活动设备变化仍不影响当前播放。
- PlaybackController 现设置 `OutputDeviceSelectionRequired` 并回到 Stopped。Start、Segment Preview、Event Instrument/SubVoice Preview 在门关闭前统一拒绝；显式 `SelectOutputDevice(...)` 是唯一恢复入口。选择 `null` 只表示用户主动选了 System Default，不是设备丢失处理的自动回退。
- 设备已不可用时不向旧端点执行 flush；Worker 先 Dispose 输出以卸载 callback/notify/native handle，再停止 render-ahead。未知 Worker、callback、ring、renderer 和 IPC 错误仍按普通 Error 处理。
- 新终态改变内部共享内存协议语义，`SharedAudioWorkerControl.ProtocolVersion` 从 1 升到 2；与 Project、Mapping ABI、持久化格式和 canonical fingerprint 无关。

## 3. 失败条件与诊断

- Worker 未发布、显式路径不存在、不是 `.exe`、协议/ABI 不匹配、原生 DLL/manifest/版本/hash 不匹配或 Worker 准备失败时，验收必须在播放前失败并保留完整异常。
- 子进程实时播放仍必须报告 callback/child 零托管分配、零 IPC underrun 和无 fault；设备切换/移除项的预期受控非零退出不能误判为普通播放成功。
- `OutputDeviceUnavailable` 必须是 exit 0 的非故障终态；相同状态配非零 exit、普通 Faulted、未知终态或损坏状态仍必须失败。控制台对设备丢失返回专用代码 2 并打印人工重选提示，不得打印未处理异常堆栈。
- 不得以修改人工预期、删除实例间隔、静默更换 Reset 默认值或只处理 BASS 消费端来掩盖 M-AUD-002/M-AUD-005。本次没有修改示例间隔、CC11 Reset default、Limiter 或 BASS consumer 的事件解释。

## 4. 持久化与运行时归属

- Worker 绝对路径、BASS 本机目录、SF2 测试路径、WAV、PCM 测量和人工结果只属于开发/验收运行时，不写入 `.midora`、Application Preferences、Undo/Redo 或 canonical fingerprint。
- `MIDORA_AUDIO_WORKER_PATH` 只属于人工 Console 进程；正式产品 Worker 路径仍由未来主应用安装/composition 边界提供。
- 首轮原始输出继续保存在 `Midora-Manual-Audio-Acceptance-Snapshot-2026-08-07.md`；复测只能追加新记录。

## 5. 明确非目标与验证门

- 本轮不改变 Limiter、tick→sample、WASAPI、BASS flags、sample voice 上限、Project Reset Defaults、MIDI exporter 自行清理规则或 UI；MIDI exporter 只编码新增的 canonical CC120。
- Worker 入口自动门：Release build；标准路径和显式路径解析；Native AOT publish 必需文件检查；正式子进程离线渲染成功且与进程内结果一致；managed `.dll` 继续被正式入口拒绝。
- SubVoice 分支自动门已补：allocation group 结束、相邻复用、非隔离重叠、硬范围顺序、空 SubVoice、来源追踪、Full/Incremental 既有形式等价、MIDI 导出 canonical CC120、真实 SF2 release/Channel 作用域、原 M-AUD-002 四个间隔稳定静音，以及进程内/子进程逐 sample 一致。M-AUD-002/M-AUD-005/M-AUD-008 仍需人耳复测。
- Worker 分支验证结果：4 项路径解析测试覆盖显式 `.exe`、显式缺失不回退、标准 RID publish 路径和 managed `.dll` 拒绝；BASS 测试 124/124。标准路径和环境变量路径分别完成真实 Native AOT 子进程离线渲染，0 B/无 fault；Segment 子进程与进程内 WAVE SHA-256 同为 `7D3001050F0EE44B7C19B50A83AFC486EEE415E0EEDF469255F4FAF0219ADDFF`。完整非 UI Release 门 866/866、0 Skip、6 solution 0 warning/0 error，产物为 `artifacts/non-ui-release-gate-516234b029e8474d8a415958155f62c9`。
- Q-NUI-026 定向结果：Compiler 217/217、MIDI Export 30/30、BASS renderer integration 19/19；新 SubVoice 例为 528 canonical events、0 B、无 renderer/child fault。三例进程内/子进程 WAVE 分别逐字节一致：Segments `A8BA7E2D9777DAB21B90E67BB4B6B233F96632249D575288ADC578E717DECD44`，SubVoice `8579C9CB460BE825FBF74A28ABEF4CF8FE390B7EB16773D36C576262985C8C85`，Tempo/Loop `542086EF417D9B1DDCDA47960B8AE2F53293031D71CB6FC55A6C51A3FFBE4AD9`。
- 最终自动门：`Test-NonUIRelease.ps1` 通过 873/873、0 Skip；六个 Release solution 均 0 warning/0 error；固定 BASS baseline、locked restore、当前源码 Native AOT `win-x64` Worker、manifest、MIT License 与 notices 全部通过。证据位于 `artifacts/non-ui-release-gate-q026-20260807`。
- 第二轮人工结论：M-AUD-002、005、007～009、011 通过；M-AUD-012 旧行为失败。Q-NUI-028 回归新增 8 个 test cases，Playback 57/57、BASS 131/131、BassWasapi device 35/35；完整非 UI Release 门通过 881/881、0 Skip，六个 solution 0 warning/0 error，固定 BASS baseline、locked restore、当前源码 Native AOT Worker、manifest、MIT License 与 notices 全部通过。证据位于 `artifacts/non-ui-release-gate-q028-20260807`。
- M-AUD-012 修复后物理复测通过：callback/child allocations 0 B、IPC underrun 0、`child fault=False`，无异常堆栈，显示人工重选提示并返回约定代码 2。通过快照为 `Midora-Manual-Audio-Acceptance-M-AUD-012-Pass-Snapshot-2026-08-07.md`；M-AUD-001～012 全部关闭。
