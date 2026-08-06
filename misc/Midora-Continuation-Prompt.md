# Midora 跨聊天续接提示词

将下面代码块中的内容完整复制到新聊天。

```text
你现在接续 Midora 项目的工作。

仓库路径：
D:\Programing\midora

默认使用简体中文。事实、SRS 正式需求、源码现状、设计推断和未决事项必须明确区分，不得把建议写成既定需求。

一、开始任何工作前必须执行

1. 完整阅读：
   - D:\Programing\midora\AGENTS.md
   - D:\Programing\midora\misc\Midora-SRS-Initial-Release-v0.1\00-Table-of-Contents-and-Document-Control.md
   - D:\Programing\midora\misc\Midora-SRS-Initial-Release-v0.1\22-Requirement-Locator-and-Cross-System-Invariants.md
   - 与本次任务直接相关的全部 SRS 章节，不得只依靠本提示词或 AGENTS.md 代替 SRS 原文。
   - D:\Programing\midora\misc\Midora-Implementation-Roadmap.md（这是实施建议和历史现状记录，不是需求规范；与 SRS 冲突时以 SRS 为准）。
2. 运行 `git status --short --branch`，保护工作树中已有的用户改动；不要 reset、checkout、覆盖、删除或顺手格式化无关文件。
3. 检查相关 solution、project、源码和测试后，再给出 requirement trace：输入、正式输出、边界、失败条件、诊断、持久化归属、运行时归属和明确非目标。
4. 未经我明确要求，不要提交、推送、创建分支或修改与当前任务无关的内容。提交信息如被要求，遵循仓库现有 Conventional Commits 格式，例如 `feat(scope): 中文说明`、`docs(srs): 中文说明`。

二、Git 与工作树基线

- 文档基线已提交并推送到 `origin/main`：
  - commit：`996109377adeff010e79860fcd9558affa6e2215`
  - subject：`docs(srs): 更新音频后端与输出约束`
- 该提交只包含 `AGENTS.md` 和 `misc/` 文档。
- 当前 `src/` 下仍有本次文档工作开始前就存在的未提交修改和删除项。它们属于用户已有工作，不属于上述文档提交；在明确审阅其内容和归属前不得丢弃、覆盖或自动纳入其他提交。
- 已知未提交路径如下，但仍应以新聊天开始时的实际 `git status` 为准：
  - `src/midora-audio-device/Midora.AudioDevice.BassWasapi.Tests.Console/Program.cs`
  - `src/midora-audio-device/Midora.AudioDevice.BassWasapi/Internals/BassWasapiOutputDevice.cs`
  - `src/midora-audio-device/Midora.AudioDevice/IAudioRenderSource.cs`
  - `src/midora-audio/Midora.Audio.Bass.Tests.Console/Program.cs`
  - `src/midora-audio/Midora.Audio.Bass/Internals/BassMidiPort.cs`
  - `src/midora-audio/Midora.Audio/IMidiPort.cs`
  - `src/midora-audio/Midora.Audio/Midora.Audio.csproj`
  - 删除：`src/midora-logical/Midora.Logical/EventInstrument.cs`
  - 删除：`src/midora-logical/Midora.Logical/Midora.Logical.csproj`
  - 删除：`src/midora-logical/midora-logical.slnx`

三、已确认的需求基线

以下决定已经写入 SRS，不得重新当作未决问题或改回旧规则：

1. 所有正式消费者严格遵循：
   `Project Source Data → Semantic Validation → Compilation → Canonical Compiled Result → Playback / Preview / MIDI Export / Audio Rendering`。
   Canonical Compiled Result 是所有正式消费者唯一的音乐语义输入；消费者不得重新解释 Project、Event Instrument、Mapping、Lifecycle、Segment 或资源分配规则。
2. `Channel Unit >= 248` 只产生 `Info`，不是 Warning，不受“Warning 视为 Error”影响；256 才是硬上限。
3. Segment Split 必须计算分割 tick 的参数有效状态，并在右 Segment 保留或生成显式参数起点状态，使参数状态和相关曲线的听感不因分割而变化。它不是跨 Segment 隐式继承；跨分割点 Logical Note 仍按既定规则提前结束，不自动续接。
4. 所有正式 BASSMIDI stream 必须启用 `BASS_MIDI_NOFX`。初版完全不支持 Reverb / Chorus；CC91 / CC93 不得出现在编辑、初始状态、Mapping、编译结果、播放调度或 MIDI 导出中。源数据出现时语义验证 Error；后端收到时为一致性 Error。
5. 正确性、确定性和资源上限满足后，时间性能优先于最小空间占用。允许使用有明确上限和所有权的预计算、缓存、固定 buffer、双缓冲、三缓冲或四缓冲换取速度。
6. Playing、Buffering、实时预览和文件 Rendering 阶段，参与音频活动的线程不得产生托管堆分配。Preparing、Finalizing、Stop 清理及其他非音频线程可以分配；同进程其他线程可以正常触发进程级 GC，但 callback deadline miss、underrun、断音或爆音仍是性能问题。
7. 只允许一个用户可启动、显示 UI、打开 Project 的 Midora 应用实例。初版正式音频后端固定为由主应用管理、无 UI、不能独立打开或解释 Project 的完整内部音频子进程；该 Worker 独占 BASS/BASSMIDI/Limiter/BASSWASAPI/callback，并固定按 `win-x64` Native AOT、自包含发布。不向用户提供拓扑切换设置。
8. 实时播放必须列出全部启用的音频输出设备并标记系统默认设备；排除输入、loopback input、disabled、unplugged 和 not-present 端点。实时采样率跟随所选设备初始化后报告的实际采样率；设备或实际采样率变化时丢弃 sample-domain 缓存并重建相关 stream。
9. 用户直接调整 buffer 大小，而不是设置 Target Latency：
   - Render-Ahead Buffer：20–2000 ms，默认 100 ms；
   - Device Buffer Request：5–200 ms，默认 50 ms；
   这些设置只允许在 Stopped 修改；设备实际 buffer、callback period 和内部工作 block 是只读运行时信息。
10. 从视觉上音符应播放到听到声音的端到端实时延迟，以约 200 ms 作为性能测试和架构选择基准。它不是运行时播放成败逻辑，不自动调参，也不是用户 Target Latency。若使用内部子进程，IPC 延迟计入该基准。
11. 音频文件输出是普通 RIFF/WAVE、stereo、interleaved IEEE float32 little-endian。文件采样率允许 8,000–192,000 Hz 的任意整数，默认 48,000 Hz；UI 提供 44,100、48,000、88,200、96,000、176,400、192,000 Hz 快捷值。
12. 文件渲染使用独立文件专用 `OutputDevice` 抽象，不依赖 WASAPI 或物理设备，直接按任务采样率渲染，不先固定 48 kHz 再重采样。
13. Preparing 必须在创建临时文件前精确预检每个 RIFF 输出的可表示大小。任一目标超限时，整个任务以 Error 阻止；不得拆分、回退 RF64/WAVE64、降低采样率或缩短内容。
14. 所有正式 BASSMIDI stream 必须启用 `BASS_MIDI_NOTEOFF1`。同 Port、Channel、pitch 的重叠 Note 实例按最早开始者优先与逐个 NoteOff 配对；硬边界必须按活动实例数完整释放。该策略不是用户设置。
15. 所有正式 BASSMIDI stream 固定 8-point sinc 和 CPU 属性 `0`；Preparing 使用 `BASS_MIDI_FontLoad` 预加载计划引用的 presets/fallback。实时与离线每 Stream sample voice 上限分别配置，默认均为 `750`，同一任务所有 Port 使用同一值；完美音频一致性测试以未触顶为前提。
16. 初版正式 BASS 原生基线固定为 `bass.dll 2.4.18.3 / 0x02041203`、`bassmidi.dll 2.4.16.0 / 0x02041000`、`basswasapi.dll 2.4.4.1 / 0x02040401` 及仓库 manifest 中的 win-x64 SHA-256。仓库不提交 DLL；正式发布只接受操作员提供且匹配 manifest 的文件，运行时校验完整版本码；vendor current/latest 只能生成开发候选。
17. 初版 C# Mapping 固定 ABI v1：签名为 `double Transform(double value, in MappingContextV1 context)`；独立只读契约，固定 Roslyn 5.3.0/C# 14/`Microsoft.NETCore.App.Ref 10.0.10`；每 Project 只缓存当前源码修订并用 collectible ALC 卸载旧项。`.midora` 只保存 ABI 版本、函数体和 Context 声明。该边界不是 sandbox。
18. 初版持久化兼容基线固定为 JSON Schema Draft 2020-12、内部版本化 `System.Text.Json` source-generated DTO、protobuf Edition 2024、Google.Protobuf 3.35.1 和 Grpc.Tools 2.83.0；严格拒绝 JSON 重复/未知属性及 protobuf 未知 tag，`.proto`/descriptor/golden bytes 进入兼容门。文本、路径、opaque sRGB、UTC 七位小数秒与非负 int64 毫秒表示已固定。

四、仍需 ADR 或实测决定的事项

这些不是当前 SRS 冲突，不能静默写成实现默认值：

- BASS 商业分发许可证与第三方 notices；技术版本、SHA-256 和升级回归策略已经固定，不得与授权问题混为一项；
- Project SoundFont 可移植引用与哈希策略；
- Project 工程总耗时累计规则；
- MIDI 导出兼容细节和 MIDI/音频输出文件名规则。

五、当前代码定位

- 已有正式实时垂直切片采用完整 Native AOT 音频子进程、子进程内 Render-Ahead/WASAPI 和固定共享内存控制 ABI；旧进程内与 PCM IPC 链只对测试程序集可见。设备移除、deadline、长期运行、故障恢复和正式硬件矩阵仍未完成发布验收。
- `Thread.Sleep` 和即时 API 调用不能承担正式 MIDI 时序。
- BASS 是音频实现细节，不得泄漏进 Project/Compiler 领域模型。
- 每个实际使用的 Port 创建干净的 BASSMIDI decode stream，不预建 16 个永久 stream；必须显式建立 melodic Channel 10、NOFX、统一 SF2 和规范初始状态。
- WASAPI callback 只消费已准备好的连续 float32 frame；不得编译、分配、阻塞、等待锁、执行 I/O，异常不得越过 native 边界。
- 音频协议以 frame 为基本单位，必须明确 sample rate、channel count、sample format 和 frame count，不能混淆 byte/sample/frame。

六、你在新聊天中的第一条回复和行动边界

先读取上述文件并检查 Git 状态。然后只回复：

1. 已确认读取的规格与仓库基线；
2. 当前工作树中需要保护的已有改动；
3. 仍待 ADR 的事项；
4. 询问我这次要继续哪一个具体实现或设计任务。

在我给出具体任务前，不修改代码、SRS、Git 状态或外部系统。
```
