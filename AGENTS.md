# Midora 仓库工作约束

本文件是未来在本仓库工作的默认提示词。除非用户明确修改需求，否则必须遵守。

## 1. 事实来源与语言

- 默认使用简体中文沟通、写开发说明和交付结论；代码标识符遵循项目既有风格。
- 完整需求基线位于 `misc/Midora-SRS-Initial-Release-v0.1/`。开始任务前必须阅读 `00-Table-of-Contents-and-Document-Control.md`、`22-Requirement-Locator-and-Cross-System-Invariants.md`，以及与任务直接相关的章节；不得只依据本提示词替代 SRS 原文。
- `misc/Midora-Implementation-Roadmap.md` 是实施建议和现状记录，不是需求规范。它与 SRS 冲突时以 SRS 为准。
- 明确区分：SRS 已规定的事实、从源码或文档确认的现状、设计推断、尚待决定的问题。不得把建议写成既定需求。
- SRS 没有规定、且选择会影响可听结果、文件兼容性、确定性或用户工作流时，先记录 ADR/规格问题并请求决定，不得静默选择。
- 不得擅自修改 SRS。发现冲突时记录准确章节、影响范围和候选解释。

## 2. 不可破坏的系统主线

所有正式消费者必须遵循：

```text
Project Source Data
→ Semantic Validation
→ Compilation
→ Canonical Compiled Result
→ Playback / Preview / MIDI Export / Audio Rendering
```

- Project 是完整语义上下文；消费者不得重新解释 Event Instrument、Mapping、Lifecycle、Segment 或资源分配规则。
- Canonical Compiled Result 是播放、预览、MIDI 导出和音频渲染的唯一正式输入。
- Full Compile 和 Incremental Compile 对同一输入必须生成语义及形式完全一致的结果。
- 稳定 ID 是身份；名称、列表位置、tick、MIDI Track/Port/Channel 均不得代替身份。
- 所有正式结果必须确定：不得依赖未规定的集合遍历顺序、线程竞争、历史分配、随机数、缓存命中或当前 UI 状态。
- `[startTick, endTick)` 是统一范围语义。Segment、End Marker、播放范围和渲染范围的硬边界必须精确执行 NoteOff 与 Reset。
- Mute/Solo 只属于运行时消费过滤；不得修改 Project、Canonical Compiled Result、MIDI 导出或音频渲染正式内容。
- `.midora` 只保存源数据；不得保存编译结果、运行缓存、导出结果、Undo/Redo 或会话 UI 状态。
- UI 只能展示、编辑和导航正式模型及诊断，不得另建一套业务语义。

## 3. 初版范围护栏

- 目标是 Windows Desktop、.NET 10、WPF、MIDI 1.0、`win-x64`、单个用户可启动的应用实例、单 Project、单个 Project SoundFont、最多 16 Port × 16 Channel Unit。主应用、Native AOT 音频子进程及 BASS/BASSMIDI/BASSWASAPI 必须同为 x64；初版不发布 x86、Arm64 或 AnyCPU 正式产物。
- Channel 10 在所有 Port 上都必须按 melodic channel 初始化，不能使用 BASSMIDI 默认鼓通道语义。
- 不得顺手加入 MIDI 2.0、VST/DAW host、传统实时 MIDI OUT、录音、由 Compiler/Overlap/Channel Group 实施的语义级 Voice Stealing、多 SoundFont、SFZ/DLS、Pause/Scrub、多 Project 或 SRS 明确排除的能力。BASSMIDI 每 Stream sample voice 上限是已确认的后端资源配置，不属于该禁止项。
- Event Instrument、SubVoice、Mapping、Lifecycle、Logical Track、Segment 等正式语义以各自 SRS 章节为准，不以当前原型类结构为准。
- 满足正确性、确定性、失败原子性和资源上限的候选实现中，时间性能优先于最小内存占用；允许用更多但有明确上限和释放时机的内存换取速度。

## 4. 音频后端的严格约束

当前 BASS/BASSMIDI/BASSWASAPI 代码只是可发声验证，可以重写。不得把“测试能听到声音”等同于时序、状态、资源和容错正确。

- BASS 是渲染实现细节，不是 Project/Compiler 领域模型。领域层不得暴露原生 handle、BASS 常量或设备回调约束。
- 每个实际使用的 Port 创建一个干净的 BASSMIDI decode stream；不得预建 16 个永久 Port stream。
- 所有实际 Port 使用同一正式生效的 Project SF2。无有效 SF2 时允许打开、编译和 MIDI 导出，但必须阻止播放、预览和音频渲染。
- 每次 stream 创建、重建和复用前都要显式建立 melodic Channel 10 和规范要求的初始状态。只有完成精确 NoteOff、Reset 与状态清理后才允许复用。
- 不得用 `Thread.Sleep`、UI 定时器或“调用 API 的瞬间”承担正式 MIDI 时序。事件必须从 Canonical Compiled Result 经统一 tick→sample 映射后做采样级调度；同 tick 顺序必须保留。
- 所有正式 BASSMIDI Stream 必须启用 `BASS_MIDI_NOFX | BASS_MIDI_NOTEOFF1`。初版完全不支持 Reverb / Chorus；CC91 / CC93 不得进入 Project、Mapping、Canonical Result、播放调度或 MIDI 导出。后端收到它们时必须报告一致性 Error。同 Port、Channel、pitch 的重叠 Note 实例按 FIFO 与逐个 NoteOff 配对，硬边界必须按活动实例数完整释放。
- 所有正式 BASSMIDI Stream 固定 `BASS_ATTRIB_MIDI_SRC = 1`（8-point sinc）和 `BASS_ATTRIB_MIDI_CPU = 0`。实时与离线每 Stream sample voice 上限分别配置，默认均为 750；同一任务全部实际 Port Stream 使用同一冻结值。Preparing 必须用 `BASS_MIDI_FontLoad` 预加载计划引用的 presets/fallback，不得对实时事件 Stream 调用 `BASS_MIDI_StreamLoadSamples`。
- 实时链固定为：实际 Port stereo 输出求和 → Playback Master Volume → Limiter → WASAPI；预览也走该链。离线整曲链语义相同，但不依赖 WASAPI 或物理设备。
- WASAPI 回调不得编译、分配常规托管对象、阻塞、等待锁、做文件/网络 I/O 或让异常越过 native 边界。回调只消费已准备好的连续 float32 frame，正确处理短读、静音、停止和设备丢失。
- Playing、Buffering、实时预览和文件 Rendering 阶段的 callback、调度、合成协调、混音、buffer 搬运及文件采样写入线程不得产生托管堆分配。Preparing / Finalizing 可以分配；同进程其他非音频线程可以分配和触发 GC。
- 音频缓冲协议以 frame 为基本单位，显式携带采样率、声道数、sample format、frame count；不得混淆 byte count、sample count 和 frame count。
- 必须列出全部 enabled output device 并排除输入、loopback input、disabled、unplugged 和 not-present 端点。实时音频按设备初始化后报告的实际采样率生成；设备或实际采样率变化时丢弃全部 sample-domain 缓存。
- Application Preferences 的可调实时参数为：Render-Ahead 20–2000 ms（默认 100）、Device Request 5–200 ms（默认 50）、Realtime Maximum Sample Voices per Stream 1–16,777,216（默认 750）。离线 sample voice 上限属于 Project 的 Audio Render Settings，取值范围相同、默认 750。实时 PCM 不跨进程，不提供 IPC Audio Buffer 设置；设备实际 buffer、callback period 和工作 block 只读。
- BASS/BASSMIDI/BASSWASAPI 的全局初始化、线程相关 device context、原生 handle、callback delegate/GCHandle 和卸载顺序必须集中管理。所有原生调用都要检查返回值，并立即读取当前线程的错误码。
- 正式原生基线固定为 BASS `2.4.18.3 / 0x02041203`、BASSMIDI `2.4.16.0 / 0x02041000`、BASSWASAPI `2.4.4.1 / 0x02040401` 以及 `bass-native-baseline.win-x64.json` 中的 SHA-256。仓库不保存 DLL；正式构建只接受操作员提供且逐文件匹配 manifest 的二进制，运行时校验完整版本码，不得只校验 API 主版本或自动采用 vendor current/latest。
- 音频文件渲染输出普通 RIFF/WAVE、stereo、interleaved IEEE float32 little-endian；采样率是用户选择的 8,000–192,000 Hz 整数，默认 48,000 Hz。文件专用 OutputDevice 直接按目标采样率生成，不依赖 WASAPI。超过 RIFF 大小上限时 Preparing 失败，不拆分、不回退 RF64。流式分块写入并使用临时文件—校验—原子发布事务。
- 约 200 ms 端到端实时延迟只是性能测试和架构选择基准，不是 Target Latency 设置，也不决定播放成败。若采用内部音频子进程，IPC 延迟必须计入。
- 尚未由规格/ADR确定的音频语义或发布参数不得隐藏在实现默认值里；已确认的 Limiter、tick→sample 取整、WASAPI 模式、工作 block、进程拓扑和 `win-x64` 架构不得重新开放为可选分支。
- 商业发布前必须核实并取得与实际产品/平台匹配的 BASS 许可证；技术可行不代表已具备分发授权。

## 5. 实施顺序

1. 读取需求章节、相关源码、测试和当前 `git status`；保护用户已有改动。
2. 写一份简短 requirement trace：输入、正式输出、边界、失败条件、诊断、持久化归属、运行时归属和明确非目标。
3. 若涉及可听语义、持久化格式、公共接口或并发模型，先更新/新增 ADR 或设计记录。
4. 优先做能贯穿 Domain → Compiler → Canonical Result → Consumer 的最小垂直切片，再扩展覆盖面。
5. 每个增量必须构建并运行与风险相称的自动测试；console 发声程序只能作为人工 smoke test。
6. 完成后报告：改动文件、需求依据、测试证据、未解决风险；不得把未运行的验证写成已通过。

## 6. 必须设置的验证门

- 编译器：golden tests、同输入重复编译、乱序集合输入、Full/Incremental 等价、范围起点状态恢复、同 tick 排序、资源峰值和来源追踪。
- C# Mapping ABI：v1 公共契约快照、固定 Roslyn/C# profile、.NET 10 核心引用允许、Midora/第三方引用拒绝、ABI/源码缓存键、当前修订失效、collectible ALC 卸载和 Project 关闭释放。
- MIDI：0/127 边界、真实 NoteOff velocity 0、Bank/Program、RPN/NRPN、Pitch Bend Range、同音高重叠、Segment/End Marker 精确截断。
- Native interop：结构布局、calling convention、32/64 位宽度、错误返回、版本不匹配、重复 init/free、handle/delegate 泄漏。
- 实时音频：不同 callback block size、短读、underrun/Buffering、设备移除/默认设备变化、连续 start/stop/reset、无回调线程异常。
- 音频语义：多 Port 求和、Channel 10 melodic、CC91/CC93 被完整拒绝、NOFX、SF2 更换、设备采样率变化、Master/Limiter 顺序、实时与离线共享语义。
- 实时音频性能：三类 buffer 边界、活动音频线程零托管分配、callback deadline、underrun、约 200 ms 基准，以及可选 IPC 吞吐和延迟。
- 渲染：8,000/44,100/48,000/192,000 Hz 与自定义整数值、固定长度、非零起点、Tempo 变化、硬结束无 tail、NaN/Infinity 失败、RIFF/fmt/data size 与 frame 对齐、RIFF 上限拒绝、取消与原子发布。
- 持久化：Draft 2020-12 JSON schema/source-generated DTO、Edition 2024 protobuf descriptor/golden bytes、deterministic JSON/protobuf/ZIP、重复及未知字段拒绝、损坏隔离、版本迁移、安全 Save/Save Copy、重开校验。

## 7. 已确认且不得重新引入的规则

1. `Channel Unit >= 248` 是 `Info`，不是 Warning，不受“Warning 视为 Error”策略影响。
2. Segment Split 必须为右侧 Segment 保留或生成分割 tick 的必要参数起点状态，使参数状态及相关曲线的听感不因分割而变化；一般 Segment 边界仍不做隐式跨 Segment 状态继承，跨分割点 Logical Note 仍按提前结束规则处理。
3. 初版正式音频后端启用 `BASS_MIDI_NOFX`，完整拒绝 CC91 / CC93。
4. 普通 RIFF/WAVE 取代 RF64；文件采样率可选，实时采样率跟随设备实际值。
5. 正式 BASSMIDI Stream 启用 `BASS_MIDI_NOTEOFF1`；同 Port、Channel、pitch 的重叠 Note 实例按最早开始者优先逐个释放。
6. 正式 BASSMIDI Stream 使用 8-point sinc、CPU 属性 0；实时/离线 sample voice 上限分别配置且默认均为每 Stream 750，完美音频一致性测试以未触顶为前提。
7. 初版产品 CPU 架构固定为 `win-x64`；音频 Worker 只允许以该 RID Native AOT 发布。
8. 初版三项 BASS DLL 的完整版本和 SHA-256 固定；仓库保存 manifest 而不提交 DLL，升级必须显式变更基线并完成全回归。
9. 初版 C# Mapping 固定 ABI v1、Roslyn 5.3.0/C# 14/`Microsoft.NETCore.App.Ref 10.0.10` 和独立只读 Mapping 契约；每 Project 只缓存当前源码修订并使用 collectible ALC，编译产物不持久化。该机制不是 sandbox。
10. 初版持久化固定 JSON Schema Draft 2020-12、内部版本化 System.Text.Json source-generated DTO、protobuf Edition 2024、Google.Protobuf 3.35.1 与 Grpc.Tools 2.83.0；未知/重复字段严格拒绝，已发布 descriptor/字段号/golden bytes 必须保持兼容。文本、路径、opaque sRGB、UTC 七位小数秒和非负 int64 毫秒表示按 SRS 16.13 固定。
11. 外部 Project SF2 只允许项目根目录或直属 `soundfonts/` 相对引用；路径精确大小写优先、唯一 ignore-case 回退并 Warning、歧义拒绝。SHA-256 对完整原始字节流式计算，只在用户明确选择/替换/重绑定/接受变化时更新；被动变化不改 Project。绝对解析路径和验证缓存只属于运行时。
