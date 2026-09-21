# Midora macOS 音频后端架构决策（ADR）

状态：产品所有者于 2026-09-21 确认 **macOS 优先接入、WPF 弃用、Windows 仅保留后路**；本文记录由此产生的基线变更、库设计与待办门。未决项见 §6。
适用范围：`src/midora-audio*`、`src/midora-audio-device*`、`src/midora-native-interops*`、`src/midora-core/Midora.Playback*` 及其与 Avalonia 的接入。
上位规范：`misc/Midora-SRS-Initial-Release-v0.1` 与根 `AGENTS.md`。本文是实现决策；与 SRS 原文冲突时需要按 §6 的产品决定更新 SRS/基线。

## 1. 背景与依据

- 2026-09-21 产品所有者决定：WPF 将被弃用，`src/midora-desktop/**` 不再要求可构建；macOS 音频直接开始接入，但**必须保留 Windows 后路**（接口、进程模型、基线清单都留缝）。
- 事实核查（un4seen 官方页面，2026-09-21）：BASS 2.4.18.3 与 BASSMIDI 2.4.16 均提供 macOS 版本，版本号与仓库冻结的 Windows 基线一致；**BASSWASAPI 仅 Win32**，macOS 上由 BASS 自身通过 CoreAudio 输出。
- 现有 macOS 侧为零音频：Avalonia 未引用任何音频项目；`Midora.Audio.Bass.Worker` 只 publish win-x64。
- `Midora.Audio`、`Midora.AudioRender`、`Midora.AudioDevice`（契约）、`Midora.AudioDevice.Wave`、`Midora.Playback`（控制器/投影）已验证为平台中立托管代码，可整体复用。

## 2. Requirement trace（macOS 音频接入）

### 2.1 输入

- canonical compiled result、`[startTick, endTick)`、Tempo Map、Unit/Port/Channel 分配、冻结的 Enabled SoundFont 有序列表与目标 Bank/Program。
- 操作员提供的 macOS BASS/BASSMIDI 二进制；应用偏好中的 Render-Ahead、Device Request、实时 sample voice 上限、Master/Limiter 配置。

### 2.2 正式输出

- 实时链：实际 Port stereo 求和 → Master Volume → Limiter v2 → BASS 原生输出（macOS CoreAudio）。
- 离线链：同语义但补偿前瞻、精确 frame 数，普通 RIFF/WAVE stereo interleaved IEEE float32。
- 两条链共用同一套 Unit stream 语义（1-channel BASSMIDI decode stream、有界 stream pool、`BASS_MIDI_NOFX | BASS_MIDI_NOTEOFF1`、`BASS_ATTRIB_MIDI_SRC = 1`、`BASS_ATTRIB_MIDI_CPU = 0`）。

### 2.3 边界

- Windows 后路：BASS 输出设备工厂以 `IAudioOutputDeviceFactory` 契约接入；`Midora.AudioDevice.BassWasapi` / `Midora.Playback.BassWasapi` / `Midora.NativeInterops.BassWasapi` 保留在仓库中休眠，不删除；子进程控制面与原生基线都是平台参数化设计，Windows 恢复时补实现即可。
- 不在中立层写平台常量：库名解析（`libbass.dylib`/`libbassmidi.dylib`）在 interop 项目内按平台解析，Windows 仍走默认探测。
- 不改变 tick-domain canonical 语义与诊断；音频投影只在 canonical 成功后进行。

### 2.4 失败条件

- 原生版本码不等于冻结值、缺少 dylib、设备初始化失败、回调线程发生托管分配/异常、回调短读未按静音补齐、Limiter 链顺序错误、RIFF 上限超限、NaN/Infinity。
- 无 Enabled SoundFont 时允许打开/保存/编译/导出，但阻止播放/预览/渲染（沿用 SRS）。

### 2.5 诊断

- 原生版本不匹配、设备枚举终止错误、格式不支持、回调 fault/pull failure 计数、设备丢失/默认设备变化，都要有明确错误与计数，供 UI 与测试读取。

### 2.6 持久化归属

- 音频不进入 `.midora`、Project Domain 或 canonical；SoundFont 列表与音频参数只属于 Application Preferences。

### 2.7 运行时归属

- 实时音频按设备初始化后报告的实际采样率生成；设备或实际采样率变化时丢弃全部 sample-domain 缓存。
- 子进程拓扑保留；macOS 进程宿主必须是平台库（进程组/父死子清/资源快照），不得把 Job Object 语义硬编码进中立层。

### 2.8 明确非目标

- 本轮不做 Windows 端音频实现、不做加密/ASIO/多设备聚合、不引入 BASS 之外的引擎、不改 Limiter/NOFX/NOTEOFF1 等已确认语义。
- 本轮不删除 `Midora.Desktop*` 源码（弃用≠删除；删除另行决定）。

## 3. 决策

### ADR-MAC-AUDIO-01（已确认）：平台基线改为 macOS 优先

- 正式平台从 win-x64 改为 osx-arm64 优先；Windows 保留后路，不再是合入门槛。
- `Directory.Build.props` 的全局 `RuntimeIdentifiers=win-x64` 后续改为按项目/平台指定（改动随首个 macOS 发布脚本落地）。
- `bass-native-baseline.win-x64.json` 保留（Windows 后路的基线）；新增 `bass-native-baseline.osx-arm64.json`，在操作员提供二进制后按实际 SHA-256 填写。仓库仍不提交任何原生二进制。
- 待办：更新 `AGENTS.md` §3/§4/§7.7 与 SRS 的平台表述（见 §6 Q1）。

### ADR-MAC-AUDIO-02（已实施并通过设备层 spike）：新增 `Midora.AudioDevice.Bass`

- 位置：`src/midora-audio-device/Midora.AudioDevice.Bass`，实现 `IAudioOutputDeviceFactory`/`IAudioOutputDevice`。
- 内容：设备枚举（跳过设备 0 "No sound"、只收 `BASS_DEVICE_ENABLED`、按需 scoped `BASS_Init/BASS_Free` 读取**实际**采样率、标记系统默认、读不到格式的设备不猜测而是计入 `LastUnreadableFormatDeviceCount`）、`BASS_Init`、`BASS_StreamCreate` + `STREAMPROC` 拉取 `IAudioRenderSource`、Start/Stop/Flush、回调分配与 pull failure 计数。
- 与 Windows 的关系：BassWasapi 设备路径保持休眠；两者实现同一契约，替换点在 worker 的设备选择。
- 实测（2026-09-21，Apple Silicon + macOS 真机）：6 个设备全部枚举成功，实际采样率分别为 44100/48000（不再有静默 48k 兜底）；默认设备打开为 44100 Hz stereo float32；拉流 1 秒 = 21 次回调、47187 帧、**回调线程托管分配 0 字节**、无 fault、无 pull failure。

### ADR-MAC-AUDIO-03（已实施）：原生库名按平台解析

- `Midora.NativeInterops.Bass.BassNativeLibrary` / `BassMidiNativeLibrary` 使用 `NativeLibrary.SetDllImportResolver`，macOS 加载 `libbass.dylib`/`libbassmidi.dylib`，Linux 加载 `libbass.so`/`libbassmidi.so`，Windows 返回零句柄走默认探测（保留后路）。
- 调用方必须在首个 P/Invoke 前调用 `EnsureRegistered()`（设备工厂构造函数已调用；引擎侧接入时补）。

### ADR-MAC-AUDIO-04（设计确定，待实现）：子进程与 worker

- 目标：`Midora.Audio.WorkerHost`（契约）+ `Midora.Audio.WorkerHost.Mac`（进程组、SIGKILL 回收、`libproc`/`getrusage` 资源快照）；`Midora.Audio.Bass` 内的 Job Object 实现保留为 Windows 路径。
- Worker：`Midora.Audio.Bass.Worker` 扩 `osx-arm64` publish，设备工厂按平台选择（macOS → `Midora.AudioDevice.Bass`，Windows → 现有 BassWasapi）；Native AOT 仍是硬要求。
- 播放后端：`Midora.Playback` 侧新增 macOS 子进程后端（去 WASAPI 命名），`Midora.Playback.BassWasapi` 休眠保留。

### ADR-MAC-AUDIO-05（已实测映射，待 underrun/延迟曲线复测）：参数映射

- **实测结论（2026-09-21）**：macOS 上 `BASS_Init(freq = 0)` 一律失败（`BASS_ERROR_FORMAT`），且 BASS 跟随设备原生采样率、忽略请求值（请求 48000 得到 44100）。因此：初始化必须先请求一个合法显式频率（当前 48000），再以 `BASS_GetInfo().freq` 回读的实际值创建 stream；不得把请求值当实际值。
- 当前映射：`BASS_CONFIG_UPDATEPERIOD` ← `max(Device Request, 5)` ms；`BASS_CONFIG_BUFFER` ← `max(2×period, 100)` ms。实测 Device Request = 50 ms 时：`minbuf = 10 ms`、`latency = 17 ms`、回调帧数 2205（= 50 ms @ 44100），分配 0 字节。
- 待办：在不同 Device Request（5/50/200 ms）下复测 underrun、回调帧数分布与端到端延迟，固定验收口径；Render-Ahead、实时 sample voice 上限、工作 block 语义保持 SRS 值不变。

### ADR-MAC-AUDIO-06（设计确定）：Windows 后路保留清单

- 保留且不删除：`Midora.NativeInterops.BassWasapi`、`Midora.AudioDevice.BassWasapi`、`Midora.Playback.BassWasapi`、`AudioWorkerProcessGroup` 的 Job Object 实现、`bass-native-baseline.win-x64.json`。
- 新代码不得引用它们的类型（除平台选择点）；中立层不得出现 `OperatingSystem.IsWindows()` 之外的分支。

## 4. 库改动清单（macOS 接入）

| 库 | 状态 | 内容 |
|---|---|---|
| `Midora.AudioDevice.Bass` | **新增（已编译）** | BASS/CoreAudio 设备枚举与 pull stream 输出 |
| `Midora.NativeInterops.Bass`/`.BassMidi` | **增量（已编译）** | 平台库名解析 |
| `Midora.Audio.Bass.Worker` | 待实现 | `osx-arm64` publish、设备工厂平台选择、原生内容/manifest 按平台 |
| `Midora.Audio.WorkerHost(.Mac)` | 待实现 | 进程组/回收/资源快照 |
| `Midora.Playback` + macOS 后端 | 待实现 | 子进程播放后端与设备选择注入 |
| `Midora.Audio.Bass` | 待实现 | 引擎侧调用 `BassNativeLibrary.EnsureRegistered()`、平台可注入的 worker 启动/baseline |
| `Midora.Audio`/`AudioRender`/`AudioDevice`/`AudioDevice.Wave`/`Midora.Playback` | 不改 | 平台中立复用 |
| `Midora.NativeInterops.BassWasapi`/`AudioDevice.BassWasapi`/`Playback.BassWasapi` | 不改（休眠） | Windows 后路 |
| `Directory.Build.props`/脚本/notices | 待实现 | RID 平台化、macOS 校验/打包脚本、THIRD-PARTY-NOTICES |

## 5. 验证门（macOS）

1. **spike（需 dylib）**：加载 `libbass.dylib`/`libbassmidi.dylib`（完整版本码校验）→ 设备枚举与 macOS Audio MIDI Setup 一致 → `BASS_Init` 实际采样率 == `BASS_GetInfo().freq` → 用 SF2 播放一段 canonical 事件听到声音 → 回调分配计数为零 → Limiter 链在环。
2. **参数校准**：Device Request/Render-Ahead 与实测延迟、underrun 曲线；工作 block 与回调帧数分布。
3. **语义回归**：NOFX/NOTEOFF1/SRC=1/CPU=0、同音高 FIFO、Channel 10 melodic、Pure Root Melodic/Percussion、多 Port 求和、Master→Limiter 顺序。
4. **离线渲染门**：8k–192k、固定长度、非零起点、Tempo 变化、硬结束无 tail、NaN/Inf 失败、RIFF 上限、原子发布。
5. **设备事件**：设备移除/默认设备变化 → 停止或重建并按 SRS 处理；连续 start/stop/reset 无回调线程异常。
6. **跨平台一致性口径**：是否要求 macOS 与 Windows 逐样本一致（见 §6 Q3）。

## 6. 未决问题

1. **Q1（2026-09-21 已解决）**：平台基线定义为"macOS 初版（Avalonia，`osx-arm64`）+ Windows 后续（Avalonia，`win-x64`）；WPF 弃用但 Windows 保留"；已按 SRS 21.4 更新 SRS/AGENTS，见 §10。
2. **Q2（部分解决）**：macOS 二进制已按官方地址下载并核验（manifest 已填写 SHA-256），仓库不提交；构建期的操作员目录属性（`MIDORA_BASS_NATIVE_DIR` 的 macOS 等价物）与"发布脚本是否允许自动下载"仍待随 worker 任务确定。
3. **Q3（2026-09-21 已解决）**：跨平台不要求逐样本/逐字节一致；canonical/SMF 必须一致，同平台严格 golden，跨平台语义 + 容差；BASS 用法跨平台一致，差异仅在平台输出后端。见 §10 与 SRS 15.19.4。
4. **Q4（未决）**：Windows 后路的恢复成本目标（是否需要 CI 定期编译 Windows 目标，避免腐化）——Windows 已确认为后续正式平台，因此该问题重新表述为"Windows 目标何时恢复构建与门"。建议随第一个 Windows 里程碑排期，而不是现在就建 CI。

## 7. 2026-09-21 设备层 spike 记录

**环境**：Apple Silicon（arm64）+ 本机 macOS；BASS/BASSMIDI 官方 macOS 包（universal：x86_64/i386/arm64，arm64 切片 minos 11.0）。

**基线**：新增 `src/midora-audio/bass-native-baseline.osx-arm64.json`（版本码 `0x02041203` / `0x02041000`、SHA-256、官方来源）与 `src/midora-audio/Test-BassNative.osx.sh`（校验 schema、SHA-256、arm64 切片；运行时版本码仍由引擎校验）。二进制仍不进仓库。

**已证明**：

1. `libbass.dylib`/`libbassmidi.dylib` 在 arm64 原生加载（`NativeLibrary.SetDllImportResolver` 平台解析），`BASS_GetVersion`/`BASS_MIDI_GetVersion` 与冻结值一致。
2. `BassAudioOutputDeviceFactory.GetDevices()`：6 个真实设备（含默认、外置耳机、扬声器、BlackHole、Teams、录音用），实际采样率正确读出；终止错误为 `BASS_ERROR_DEVICE`（正确结束）；`unreadable=0`。
3. `BassAudioOutputDevice`：`BASS_Init` → `BASS_StreamCreate(STREAMPROC)` → `ChannelPlay`，1 秒 21 次回调、47187 帧；**回调线程分配 0 字节**；`Stop(flush)` 正常；无 fault/pull failure。
4. `freq = 0` 与 `BASS_DEVICE_FREQ` 标志在 macOS 均失败（error 6），已改为显式请求 + 回读实际值。

**未证明（下一批 spike）**：BASSMIDI stream + SF2 实际出声与 `BASS_MIDI_NOFX | NOTEOFF1 | SRC=1 | CPU=0` 语义；Limiter v2 在环；worker Native AOT（osx-arm64）publish 与子进程生命周期；离线渲染与缓存；设备移除/默认设备变化。

**工程状态**：`Midora.AudioDevice.Bass` 已加入 `midora-core.slnx`，与 `Midora.Audio.Bass` 一起 0 警告 0 错误；spike 探针位于仓库外（临时目录），不提交。

## 8. 2026-09-21 BASSMIDI + SF2 语义 spike 记录

**输入**：官方 ChoriumRevA.SF2（un4seen 免费音色库）+ `midora-review.mid`（约 2 分钟真实编曲）。
**路径**：不接输出设备（`BASS_Init(0 /* no sound */, 48000)`），直接用 pinned 语义离线解码：`BASS_SAMPLE_FLOAT | BASS_STREAM_DECODE | BASS_MIDI_NOFX | BASS_MIDI_NOTEOFF1`，SF2 以 `BASS_MIDI_FONT_MMAP` 加载，`BASS_MIDI_StreamSetFonts` 用 `BASS_MIDI_FONTEX2`（spreset/sbank = -1，无 target 映射），并设置 `BASS_ATTRIB_MIDI_SRC = 1`、`BASS_ATTRIB_MIDI_CPU = 0`。

**结果**：`decodedFrames=5,703,855`（118.83 s @ 48 kHz）、`peak=0.6808`、`rms=0.10075`（有真实音频，非静音），无错误。

**结论与后续发现**：

1. pinned 的 NOFX/NOTEOFF1/SRC=1/CPU=0 组合与 `BASS_MIDI_FONT_MMAP` 在 macOS arm64 上工作正常；离线渲染可以完全不需要输出设备（对 CI/自动化有利）。
2. **`BASS_MIDI_FontInit` 路径编码需要平台化**：引擎 `BassMidiPort` 现在传 `char*` + `BASS_UNICODE`（Windows 习惯）；macOS 必须传 UTF-8 字节且不带 `BASS_UNICODE`。这是引擎接入 macOS 的第一处必改点。
3. **`BASS_ChannelGetData` 结束时返回 `-1` 且 `BASS_ERROR_ENDED`**，不是失败；离线渲染循环必须这样判定（测试与 worker 的 render 路径都要按此实现）。
4. 下一批 spike：把上述流程改走真实引擎 `Midora.Audio.Bass`（render plan → Unit stream pool → limiter），验证 Master→Limiter 顺序与 NOFX 在引擎层的实际输出；以及 worker osx-arm64 AOT publish 与子进程生命周期。

## 9. 2026-09-21 引擎侧 macOS 适配（已实施，编译通过）

spike 发现的引擎必改点已落地（不影响 Windows 行为，Windows 分支保留）：

1. `BassNativeRuntime.Acquire()` 在首个 P/Invoke 前调用 `BassNativeLibrary.EnsureRegistered()` / `BassMidiNativeLibrary.EnsureRegistered()`；`BASS_CONFIG_UNICODE` 的设置改为仅 Windows 执行（macOS 上该配置返回 `BASS_ERROR_NOTAVAIL`，`GetConfig` 为 `0xFFFFFFFF`）。
2. 新增 `Internals/BassMidiFontPath.cs`：Windows 继续 UTF-16 + `BASS_UNICODE`，macOS/Linux 传 UTF-8 字节且不带 `BASS_UNICODE`；`PersistentBassMidiSoundFont` 与 `BassMidiPort` 两处 `FontInit` 统一走该helper。
3. `BassNativeRuntime` 仍以 "no sound" 设备 0 + 显式 48000 Hz 初始化（macOS 实测可用；解码不需要输出设备）。

引擎中剩余的 Windows 假设集中在 worker 启动/进程组（`BassMidiAudioWorkerSession.cs:754`、`BassMidiAudioFileRenderWorker.cs:39` 的 win-x64 文案与 `AudioWorkerProcessGroup` 的 Job Object），属于下一批 worker/进程宿主任务。

## 10. 2026-09-21 平台基线与跨平台一致性决议（按 SRS 21.4 记录）

### 10.1 决议

- **平台矩阵**：初版正式平台 = **macOS（Apple Silicon，`osx-arm64`，Avalonia）**；**Windows（`win-x64`，Avalonia）** 为计划中的后续平台。WPF 应用弃用（仅作参考实现保留），但 Windows 仍是正式目标，不缩减为"仅供恢复的休眠代码"。
- **架构规则**：每个平台的应用程序、Native AOT 音频子进程与随包原生库必须与目标平台同架构；不发布 x86 或 AnyCPU；不做运行时跨架构回退。
- **后端映射**：Windows 使用 BASS/BASSMIDI + BASSWASAPI Shared/event-driven；macOS 使用 BASS/BASSMIDI + BASS 原生 CoreAudio 输出，保持与 WASAPI 路径等价的语义（stereo interleaved float32、实际设备采样率、实际 buffer/period 由设备决定、事件驱动回调）。BASS/BASSMIDI 的用法（flags、SRC、CPU、voice 上限、Limiter 与调度语义）跨平台一致。
- **原生基线**：BASS `2.4.18.3 / 0x02041203`、BASSMIDI `2.4.16.0 / 0x02041000` 两平台共用版本码，SHA-256 按平台 manifest 固定（`bass-native-baseline.osx-arm64.json` 初版、`bass-native-baseline.win-x64.json` 后续）；BASSWASAPI `2.4.4.1` 仅 Windows。
- **跨平台一致性口径（Q3 = B）**：canonical/SMF 语义跨平台必须一致（硬门）；同平台在相同原生基线与设置下允许逐样本/逐字节 golden；跨平台只要求语义与容差一致（帧数/时长、事件时序、非静音与峰值范围、无 NaN/Infinity、Limiter ceiling 不变）。

### 10.2 受影响条款（已更新）

| 文件/条款 | 变更 |
|---|---|
| SRS 01 §1.4.1 目标平台 | 从"只面向 Windows/WPF/x64"改为双平台矩阵（macOS 初版 + Windows 后续，Avalonia；每平台同架构） |
| SRS 01 §1.4.1 术语解释（新增） | 其余章节的 WPF/Windows 控件/API 术语按"Avalonia 平台等价能力、Windows 条目保持 Windows 作用域"解释，映射见实现 ADR |
| SRS 02 §2.7 Application Preferences | "Windows 用户本机偏好" → "当前平台用户本机偏好" |
| SRS 13（8-point sinc 段） | 明确 `win-x64` SSE2 与 `osx-arm64` 已通过设备层/BASSMIDI spike，逐采样回归在发布前补做 |
| SRS 13 §13.30 | 子进程 AOT 句子改为"与当前发布平台同架构"；原生基线表拆为 Windows/macOS 两张并加入 macOS dylib SHA-256；混合链措辞去 WASAPI 专指 |
| SRS 15（音频文件渲染子进程段） | `win-x64` Native AOT → 与当前平台同架构的 Native AOT（初版 osx-arm64） |
| SRS 21 §21.3 实现自由度 | "具体 WPF 控件…" → "具体 UI 控件…" |
| SRS 15 §15.19.4 结果稳定性 | 新增跨平台一致性口径（canonical/SMF 一致、同平台 golden、跨平台容差；BASS 用法一致） |
| SRS 17 §17.1.1 | "Windows file and folder pickers" → "Platform-native file and folder pickers" |
| SRS 00 文档控制（基线摘要） | 发布架构与原生基线两条改为双平台表述 |
| SRS 22 INV-023 / INV-027 / INV-028 | WASAPI 作用域限定为 Windows；平台矩阵与双 manifest 基线 |
| AGENTS.md §3、§4（实时链/回调/基线/冻结项）、§7.7、§7.8 | 同步为 macOS 初版 + Windows 后续、Avalonia、双 manifest、跨平台一致性口径 |

### 10.3 用户可见影响

- 初版只提供 macOS 应用；Windows 应用在其里程碑达成前不存在正式产物。
- 跨平台工程文件仍然可用（canonical/SMF），音频渲染在不同平台不保证逐样本一致，但音乐语义、时长、事件时序、静音/峰值范围与 Limiter ceiling 一致。
- Windows 用户恢复可用时需重新通过该平台的音频/interop/渲染门，并核验 BASS 的 Windows 分发许可。

### 10.4 工程后续（随对应任务落地，不在本轮）

- `Directory.Build.props` 的全局 `RuntimeIdentifiers=win-x64` 改为按项目/平台声明（涉及各项目 `packages.lock.json` 的 RID 段重算）。
- 发布脚本与测试脚本按平台拆分（macOS 初版；Windows 里程碑恢复）。
- Avalonia 应用的 Windows 目标（BASS/BASSMIDI/BASSWASAPI 打包、设备枚举、进程宿主）作为 Windows 里程碑内容。

## 11. 2026-09-21 正式音频接入（Win/Mac 通用）进展

目标拓扑（两平台同一套代码，差异只在三个平台缝）：

```text
App → Midora.Playback (PlaybackController/投影/render plan)
        → 子进程后端（中立）
             ├─ 进程宿主：Windows Job Object / macOS 进程组 + tree kill + 父死看门狗
             └─ Worker（各平台 Native AOT）
                  ├─ Midora.Audio.Bass（引擎，中立）
                  ├─ WorkerAudioOutputDeviceFactory（平台缝：BASSWASAPI / CoreAudio）
                  └─ Midora.AudioDevice.Wave（离线，已平台化）
```

**已完成并验证**

1. Worker 平台缝：`Midora.Audio.Bass.Worker` 新增 `WorkerAudioOutputDevice` / `WorkerAudioOutputDeviceFactory`，worker 主体只见中立外观；设备诊断/端点刷新抽为 `IAudioOutputDeviceDiagnostics` / `IAudioOutputDeviceFlushController`（Windows 行为不变，macOS 用 `Stop(flush)` 等价实现）。设备选择判定改为 `AudioOutputDeviceSelection.IsInvalidated`。
2. Worker 发布矩阵：csproj 支持 `win-x64` 与 `osx-arm64`（各自原生库内容、manifest 与校验脚本），RID 校验与文案去掉 win-x64 专指。
3. 原生目录：interop 新增 `SetSearchDirectory`（worker 参数显式注册），`MIDORA_BASS_NATIVE_DIR` 保留为回退。
4. macOS 父死保护：`WorkerParentWatchdog`（`getppid` 变化视为所有者死亡）接入宿主循环与播放循环；Windows 继续由 Job kill-on-close 承担。
5. 验证：worker 在 macOS 直接运行 `list-output-devices` 输出 `MIDORA-AUDIO-DEVICES-V1` 与 6 个真实设备（44.1k/48k）；引擎离线与实时出声已在 §7/§8 验证。

**待办（按顺序）**

1. W3：子进程后端**本身已是参数化且平台中立**（`WorkerPath`/`BassNativeDirectory` 由选项注入，进程宿主用 `AudioWorkerProcessGroup` 的跨平台回退路径），真正的阻塞点是 **IPC 共享内存层**：`Midora.Audio/SharedAudioWorkerControl`、`Midora.AudioDevice/SharedAudioFrameRingBuffer` 仍是 Windows 专用实现（并且 `BassMidiChildProcessSession`/`PersistentBassMidiAudioWorkerHost`/`BassMidiAudioWorkerSession`/`BassMidiAudioFileRenderWorker` 都带 `[SupportedOSPlatform("windows")]`）。W3 = 把这两层按平台实现（POSIX `shm_open`/`mmap` 或 `MemoryMappedFile` 具名映射 + 现有 seqlock 协议不变），再移除这些标注；Windows 行为不变。
2. W4：App 接线——偏好持久化（SoundFont 列表/设备/Render-Ahead）→ 打开/编译工程 → canonical → render plan → `PlaybackController` → Worker；Play/Stop/位置/设备切换走正式链路。
3. W5：App 内离线渲染（worker `file-render` + `.tmp/AudioCache` + 原子发布）。
4. 未验证：父死看门狗的端到端（应用崩溃后 worker 退出）需在 W3/W4 接线后实测；macOS 设备丢失/默认设备变化检测尚未实现（当前 `DeviceLost`/`DefaultDeviceChanged` 恒为 false，由工厂级重新枚举兜底）。

## 12. 2026-09-21 Windows 独占代码跨平台化（除 WPF）

产品所有者要求：除弃用的 WPF 外，所有 Windows 独占代码改为跨平台。已完成并验证：

| 原 Windows 独占点 | 跨平台方案 | macOS 验证 |
| --- | --- | --- |
| 命名内存映射（`SharedAudioWorkerControl` / `SharedAudioFrameRingBuffer` / `MidiRenderEventStreamControl`） | .NET 命名映射在 macOS 直接 `PlatformNotSupportedException`，改为**文件支撑映射**：`SharedMemoryMapping` 在各自 owned 交换目录下创建 `.bin`，创建者释放时删除 | 控制块/音频环/MIDI 事件流协议测试 46/46；子进程 IPC 集成测试 2/2（样本与进程内一致） |
| 命名管道名过长 | Unix 管道是 `Path.GetTempPath()/CoreFxPipe_<name>` 的 Unix domain socket，macOS 上限 104 字节（含 NUL），新增 `MidoraInterprocessPipeName` 按临时目录长度预算并在必要时改用短哈希名 | 子进程集成测试通过；单实例协调 14/14 |
| 单实例协调显式拒绝非 Windows | `Mutex` + 命名管道在 Unix 上可用；转发改为按调用方超时预算重试（Unix 替换监听器会丢弃排队连接） | 14/14 含 8 路并发竞争 |
| 原子发布的独占性只在 Windows 生效 | Unix `rename` 忽略建议锁，新增 `AtomicStorePublish`：Windows 依赖 sharing mode，Unix 在替换期间持有目标独占句柄，使并发写入 fail closed | RecentProjects/Preferences/Catalog 失败替换测试 4/4 |
| `kernel32` 物理内存查询 | 统一 `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`（两平台语义一致） | 构建 + 测试通过 |
| `FSCTL_SET_SPARSE` | `TryEnableSparseFile`：仅 Windows 需要显式标记，Unix 扩展文件即稀疏 | 缓存暂存测试通过 |
| `shell32` AppUserModelID | 重命名 `MidoraApplicationIdentity`，Unix 明确 no-op | 81/81 |
| 原生库文件名/缓存身份/测试环境写死 `bass.dll` | 新增 `BassNativeFiles`（RID、`libbass.dylib`/`libbassmidi.dylib`、仅 Windows 的 wasapi），缓存身份标签按 RID | 缓存身份测试通过 |
| `SupportedOSPlatform("windows")` 陈旧标注 | 从 worker host/session、子进程后端、设备诊断接口移除；Windows 专用 ABI 测试改为 64 位通用断言 | 全量构建 0 警告 0 错误 |

音频测试在 macOS 上（`MIDORA_BASS_NATIVE_DIR` + `MIDORA_TEST_SOUNDFONT_PATH` 提供时）**246/257 通过**。剩余 11 项：

1. 9 项需要 `MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER`（Native AOT worker 产物），属环境未提供；xUnit 动态跳过未被 runner 识别为 skip，故计为失败。
2. 1 项需要 `MIDORA_TEST_SECOND_SOUNDFONT_PATH`（多 SoundFont 优先级）。
3. 1 项真实差异待查：`MonitoringColdStartKillsPreRenderedFutureKeysBeforeRewindingTheEventCursor` 期望同 tick 128 个 NoteOn 后 pressed-key 诊断 = 128，macOS 实测 109（19 个未登记），疑与 BASS 在 macOS 的按键/发声状态跟踪或同 tick 提交上限有关，尚属诊断路径而非正式消费者语义。

## 13. 关键实现修复（macOS 实测发现）

- **环形缓冲 underrun latch**：CoreAudio 经 BASS 的拉取块大于启动预填充，导致首次回调即 latch `Buffering` 并永久静音（主进程恢复命令未发送）。worker 现在在生产者存活且环已回填过半时自行 `ReleaseBuffering`（监视循环与监视变更后重启输出两处）。修复后持久化实时路径 3/3 通过。
- **实时采样率**：实时播放按设备实际采样率生成（CoreAudio 保持自身时钟，请求 48k 常得 44.1k）。`BassMidiAudioWorkerSession.Probe` 增加 `allowManagedTestWorker`，测试改为先探测设备实际速率再构建 plan；正式 app 侧同样必须如此（W4）。

## 14. 2026-09-21 App 正式接线（可听验证通过）

应用层不再有任何占位音频实现：

- `Midora.Session/Audio`：`FormalAudioWorkerLocator`（按平台定位 Native AOT worker 与原生目录，支持 `MIDORA_AUDIO_WORKER_PATH`/`_DIR`/`MIDORA_BASS_NATIVE_DIR` 与 app 旁 `audio-worker/`）、`FormalAudioOutputDeviceEnumerator`（经 worker 枚举，`MIDORA-AUDIO-DEVICES-V1`）、`RealtimePlaybackSession`（拥有子进程后端 + `PlaybackController`）。
- `ProjectSessionHost`：可选实时播放（启用时用 `Background` 编译模式、设置有效 SoundFont 集与音频缓存），`ApplyPreferencesAsync` 在音频相关设置变化时销毁并重建 Worker。
- Avalonia shell：Play/Stop 由引擎真实驱动（`Update()` 泵 + `CurrentTick`），状态栏显示真实 SoundFont 数量，主窗口创建前执行 `EnsureReadyAndProbe`（fail closed），偏好经 `ApplicationPreferencesStore` 读写。
- `ApplicationPreferencesDialog`：真实 SoundFont 行（稳定 `SoundFontEntryId`、target 映射）、真实 `*.sf2;*.sfz` 多选文件选择器（对齐 WPF，含去重与起始目录）、经 worker 的真实设备列表与实际采样率。
- worker 路径校验：macOS 的 AOT worker 无扩展名，`Path.GetExtension` 会返回 `.Worker`，因此改为“仅托管 `.dll` 需显式允许 + Unix 校验可执行位”。

**实测（macOS，无任何环境变量）**：`MIDORA_MIDI_OPEN` + `MIDORA_AUTOPLAY` 下 `Playback started` → `Playing`，tick 连续前进，正式 AOT worker 以 `realtime-host` 运行（控制文件 + SoundFont 集 + 原生目录），产品所有者确认可听见音乐；`SHELL-SMOKE failures=0`、`WINDOW-SMOKE total=44 failures=0`。

**剩余**：App 内音频渲染导出（worker `file-render` + 缓存 + 原子发布，W5）；macOS 设备丢失/默认设备变化检测（当前由工厂级重新枚举兜底）；AOT worker 集成测试需 `MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER`；`MonitoringColdStartKillsPreRenderedFutureKeys...` 的 pressed-key 诊断差异（109 vs 128）。
