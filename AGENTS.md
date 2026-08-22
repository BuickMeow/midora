# Midora 仓库工作约束

本文件是未来在本仓库工作的默认提示词。除非用户明确修改需求，否则必须遵守。

## 1. 事实来源与语言

- 默认使用简体中文沟通、写开发说明和交付结论；代码标识符遵循项目既有风格。
- `computer-use` 是显式 opt-in 工具：除非用户在当前请求中明确要求使用，否则默认不得调用；过去请求中的授权不得延续到后续请求。用户明确说明不使用时，不得以调试、验收或便利为由调用。
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

- Project 是完整语义上下文；消费者不得重新解释 Event Instrument、Mapping、Lifecycle、MIDI Channel Root、Pure MIDI Track、Segment、SMF Track 投影或资源分配规则。
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
- Logical/Event Instrument 获配的 Channel 10 必须按 melodic 初始化；Pure MIDI Root 的 Channel 10 由正式 Root Channel Mode 决定，不能把 BASSMIDI 默认鼓通道行为当作隐式语义。
- 不得顺手加入 MIDI 2.0、VST/DAW host、传统实时 MIDI OUT、录音、由 Compiler/Overlap/Channel Group 实施的语义级 Voice Stealing、多 SoundFont、SFZ/DLS、Pause/Scrub、多 Project 或 SRS 明确排除的能力。BASSMIDI 每 Stream sample voice 上限是已确认的后端资源配置，不属于该禁止项。
- Event Instrument、SubVoice、Mapping、Lifecycle、Logical Track、MIDI Channel Root、Pure MIDI Track、Segment 等正式语义以各自 SRS 章节为准，不以当前原型类结构为准。
- 满足正确性、确定性、失败原子性和资源上限的候选实现中，时间性能优先于最小内存占用；允许用更多但有明确上限和释放时机的内存换取速度。

## 4. 音频后端的严格约束

当前 BASS/BASSMIDI/BASSWASAPI 代码只是可发声验证，可以重写。不得把“测试能听到声音”等同于时序、状态、资源和容错正确。

- BASS 是渲染实现细节，不是 Project/Compiler 领域模型。领域层不得暴露原生 handle、BASS 常量或设备回调约束。
- 正式 canonical 仍保留物理 Port / Channel 分配；音频投影在 canonical 成功后按抽象 Channel Unit 拆分。每个 Unit 以干净的 1-channel BASSMIDI decode stream 语义渲染，由有界可复用 stream pool 执行；不得为 Project 中每个 Unit 永久保留原生 stream。
- 所有实际 Port 使用同一正式生效的 Project SF2。无有效 SF2 时允许打开、编译和 MIDI 导出，但必须阻止播放、预览和音频渲染。
- 每次 stream 创建、重建和复用前都要清除旧 mode/state，并按 canonical Unit descriptor 显式建立 Melodic 或 Percussion 状态。只有完成精确 NoteOff、Reset 与状态清理后才允许复用。
- 不得用 `Thread.Sleep`、UI 定时器或“调用 API 的瞬间”承担正式 MIDI 时序。事件必须从 Canonical Compiled Result 经统一 tick→sample 映射后做采样级调度；同 tick 顺序必须保留。
- 所有正式 BASSMIDI Stream 必须启用 `BASS_MIDI_NOFX | BASS_MIDI_NOTEOFF1`。Event Instrument/SubVoice 不得创建或映射 CC91/CC93；Pure MIDI Track 必须允许它们进入 Project、canonical 与 MIDI 导出，音频投影确定性忽略其 Reverb/Chorus 效果且不报一致性 Error。同 Port、Channel、pitch 的重叠 Note 实例按 FIFO 与逐个 NoteOff 配对，硬边界必须按活动实例数完整释放。
- 所有正式 BASSMIDI Stream 固定 `BASS_ATTRIB_MIDI_SRC = 1`（8-point sinc）和 `BASS_ATTRIB_MIDI_CPU = 0`。实时与离线 `Maximum Sample Voices per Unit Stream` 分别配置，默认均为 500；同一任务全部 Unit Stream 使用同一冻结值。Preparing 必须用 `BASS_MIDI_FontLoad` 预加载计划引用的 presets/fallback，不得对实时事件 Stream 调用 `BASS_MIDI_StreamLoadSamples`。
- 实时链固定为：实际 Port stereo 输出求和 → Playback Master Volume → Limiter → WASAPI；预览也走该链。离线整曲链语义相同，但不依赖 WASAPI 或物理设备。
- WASAPI 回调不得编译、分配常规托管对象、阻塞、等待锁、做文件/网络 I/O 或让异常越过 native 边界。回调只消费已准备好的连续 float32 frame，正确处理短读、静音、停止和设备丢失。
- Playing、Buffering、实时预览和文件 Rendering 阶段的 callback、调度、合成协调、混音、buffer 搬运及文件采样写入线程不得产生托管堆分配。Preparing / Finalizing 可以分配；同进程其他非音频线程可以分配和触发 GC。
- 音频缓冲协议以 frame 为基本单位，显式携带采样率、声道数、sample format、frame count；不得混淆 byte count、sample count 和 frame count。
- 必须列出全部 enabled output device 并排除输入、loopback input、disabled、unplugged 和 not-present 端点。实时音频按设备初始化后报告的实际采样率生成；设备或实际采样率变化时丢弃全部 sample-domain 缓存。
- Application Preferences 的可调实时参数为：Render-Ahead 20–2000 ms（默认 100）、Device Request 5–200 ms（默认 50）、Realtime Maximum Sample Voices per Unit Stream 1–16,777,216（默认 500）。离线 sample voice 上限属于 Project 的 Audio Render Settings，取值范围相同、默认 500。Application Preferences 还保存 session 音频缓存的本机绝对 root（默认 `%LOCALAPPDATA%\Midora\AudioCache`）和 reusable 上限（默认 16 GiB，允许 0）；实时 PCM 不跨进程，不提供 IPC Audio Buffer 设置；设备实际 buffer、callback period 和工作 block 只读。
- BASS/BASSMIDI/BASSWASAPI 的全局初始化、线程相关 device context、原生 handle、callback delegate/GCHandle 和卸载顺序必须集中管理。所有原生调用都要检查返回值，并立即读取当前线程的错误码。
- 正式原生基线固定为 BASS `2.4.18.3 / 0x02041203`、BASSMIDI `2.4.16.0 / 0x02041000`、BASSWASAPI `2.4.4.1 / 0x02040401` 以及 `bass-native-baseline.win-x64.json` 中的 SHA-256。仓库不保存 DLL；正式构建只接受操作员提供且逐文件匹配 manifest 的二进制，运行时校验完整版本码，不得只校验 API 主版本或自动采用 vendor current/latest。
- 音频文件渲染输出普通 RIFF/WAVE、stereo、interleaved IEEE float32 little-endian；采样率是用户选择的 8,000–192,000 Hz 整数，默认 48,000 Hz。文件专用 OutputDevice 直接按目标采样率生成，不依赖 WASAPI。超过 RIFF 大小上限时 Preparing 失败，不拆分、不回退 RF64。流式分块写入并使用临时文件—校验—原子发布事务。
- 约 200 ms 端到端实时延迟只是性能测试和架构选择基准，不是 Target Latency 设置，也不决定播放成败。若采用内部音频子进程，IPC 延迟必须计入。
- 尚未由规格/ADR确定的音频语义或发布参数不得隐藏在实现默认值里；已确认的 Limiter、tick→sample 取整、WASAPI 模式、工作 block、进程拓扑和 `win-x64` 架构不得重新开放为可选分支。
- Midora 初版是免费、开源、非商业软件，但该定位不把 BASS/BASSMIDI/BASSWASAPI 纳入 Midora 的开源许可证，也不自动满足其免费使用条件。正式分发第三方二进制前必须按实际发布主体、收入方式、平台、分发方式和发布时有效条款完成核验并提供 notices；条件不明或商业化时必须先联系权利人确认或取得适用许可。仓库不提交 BASS DLL。

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
- MIDI：0/127 边界、真实 NoteOff、Bank/Program、RPN/NRPN、Pitch Bend Range、同音高重叠、Segment/Root/End Marker 精确截断、Pure MIDI Track 拓扑与自身 EOT、Format 0/1 + TPQN 导入、Running Status、多 Channel MTrk 拆分和 opaque event round-trip。
- 输出命名：MIDI/音频公共合法化 golden、非法字符、保留设备名、尾部空格/句点、控制与不可见字符、Unicode 规范化别名、长度预算、大小写冲突、同批唯一性、最终路径预览冻结和覆盖授权分离。
- Native interop：结构布局、calling convention、32/64 位宽度、错误返回、版本不匹配、重复 init/free、handle/delegate 泄漏。
- 实时音频：不同 callback block size、短读、underrun/Buffering、设备移除/默认设备变化、连续 start/stop/reset、无回调线程异常。
- 音频语义：多 Port 求和、Logical Channel 10 melodic、Pure Root Melodic/Percussion、SubVoice CC91/CC93 拒绝与 Pure MIDI 音频忽略、NOFX、SF2 更换、设备采样率变化、Master/Limiter 顺序、实时与离线共享语义。
- 实时音频性能：三类 buffer 边界、活动音频线程零托管分配、callback deadline、underrun、约 200 ms 基准，以及可选 IPC 吞吐和延迟。
- 渲染：8,000/44,100/48,000/192,000 Hz 与自定义整数值、固定长度、非零起点、Tempo 变化、硬结束无 tail、NaN/Infinity 失败、RIFF/fmt/data size 与 frame 对齐、RIFF 上限拒绝、取消与原子发布。
- 持久化：Draft 2020-12 JSON schema/source-generated DTO、Edition 2024 protobuf descriptor/golden bytes、Root/Track 分文件、deterministic JSON/protobuf/ZIP、重复及未知字段拒绝、损坏隔离、开发期破坏格式拒绝、安全 Save/Save Copy、重开校验。

## 7. 已确认且不得重新引入的规则

1. `Channel Unit >= 248` 是 `Info`，不是 Warning，不受“Warning 视为 Error”策略影响。
2. Segment Split 必须为右侧 Segment 保留或生成分割 tick 的必要参数起点状态，使参数状态及相关曲线的听感不因分割而变化；一般 Segment 边界仍不做隐式跨 Segment 状态继承，跨分割点 Logical Note 仍按提前结束规则处理。
3. 初版正式音频后端启用 `BASS_MIDI_NOFX`；Event Instrument/SubVoice 完整拒绝 CC91/CC93，Pure MIDI Project/canonical/MIDI 导出保留它们，音频投影不解释其效果且不报错。
4. 普通 RIFF/WAVE 取代 RF64；文件采样率可选，实时采样率跟随设备实际值。
5. 正式 BASSMIDI Stream 启用 `BASS_MIDI_NOTEOFF1`；同 Port、Channel、pitch 的重叠 Note 实例按最早开始者优先逐个释放。
6. 正式 BASSMIDI Stream 使用 8-point sinc、CPU 属性 0；Realtime/Offline Maximum Sample Voices per Unit Stream 分别配置且默认均为 500，完美音频一致性测试以未触顶为前提。
7. 初版产品 CPU 架构固定为 `win-x64`；音频 Worker 只允许以该 RID Native AOT 发布。
8. 初版三项 BASS DLL 的完整版本和 SHA-256 固定；仓库保存 manifest 而不提交 DLL，升级必须显式变更基线并完成全回归。
9. 初版 C# Mapping 当前固定 ABI v2、`MappingStableIdV2(long)`、Roslyn 5.3.0/C# 14/`Microsoft.NETCore.App.Ref 10.0.10` 和独立只读 Mapping 契约；每 Project 只缓存当前源码修订并使用 collectible ALC，编译产物不持久化。ABI v1 已在开发期被 v2 取代且不提供并行回退；该机制不是 sandbox。
10. 初版持久化固定 JSON Schema Draft 2020-12、内部版本化 System.Text.Json source-generated DTO、protobuf Edition 2024、Google.Protobuf 3.35.1 与 Grpc.Tools 2.83.0；未知/重复字段严格拒绝，已发布 descriptor/字段号/golden bytes 必须保持兼容。文本、路径、opaque sRGB、UTC 七位小数秒和非负 int64 毫秒表示按 SRS 16.13 固定。
11. 外部 Project SF2 只允许项目根目录或直属 `soundfonts/` 相对引用；路径精确大小写优先、唯一 ignore-case 回退并 Warning、歧义拒绝。SHA-256 对完整原始字节流式计算，只在用户明确选择/替换/重绑定/接受变化时更新；被动变化不改 Project。绝对解析路径和验证缓存只属于运行时。
12. 工程总耗时按 Project 成功打开后的完整会话时间累计，包括空闲、最小化、失焦、Buffering、MIDI 导出和音频渲染；系统睡眠 / 休眠及关闭流程暂停。会话使用单调时钟；自动累计不单独标记 Modified，不进入 Undo / Redo，不影响编译或 canonical fingerprint。
13. 初版 SMF Type 1 导出固定：Tempo 以十进制 `60,000,000 / BPM` 后只执行一次 `AwayFromZero`，24-bit 越界即失败；Time Signature 固定 `cc=24`、`bb=8`；Bank 顺序固定 CC0→CC32→Program；Channel Event 显式 status；文本 Meta 严格 UTF-8；导出器不得在 canonical 之外追加 Channel 清理。Track 顺序为 Conductor→global Arrangement order 过滤出的 Pure MIDI Tracks→Logical Unit Tracks（Port/Channel 顺序）；Pure MIDI 每 Track 一个 MTrk 并保留名称与自身 EOT，Logical 每 Unit 一个 MTrk 并使用统一 endTick。导入单独支持合法 Running Status，但不保存其 wire 表达。
14. MIDI 导出的 Channel 10 melodic 初始化固定为每个 Logical Channel 10 Unit MTrk 与 Melodic Channel 10 Pure MIDI MTrk，在相对 tick 0、Track Name/MIDI Port/结构 Meta 后、canonical 事件前各写一次 Roland GS Normal Part `F0 41 10 42 12 40 10 15 00 1B F7` 与 Yamaha XG Normal Part `F0 43 10 4C 08 09 07 00 F7`，顺序 GS→XG。Percussion Root、不使用 Channel 10 的 MTrk 和 Conductor 不写；不得发送 GS/XG/GM Reset 或改变 canonical Bank/Program。
15. MIDI 导出和音频文件渲染必须共用确定性的 Windows 安全文件名合法化与冲突检测服务：NFC、SRS 14.17.4 固定不安全字符表连续段替换为 `_`、设备保留名前缀 `_`、255 UTF-16 code unit、text-element 安全截断、NFC + OrdinalIgnoreCase 冲突键、稳定 ` (n)` 后缀。任务开始前预览并冻结全部最终路径；合法化不修改 Project 源名称、不进入 Undo / Redo，已有目标不参与后缀分配且仍须明确覆盖授权。
16. 初版输出模板固定：整曲 MIDI / 音频为 `<ProjectStem>.mid/.wav`，ProjectStem 依 Project 名称、当前 `.midora` stem、模式 fallback 选择；分 Track 为 `<NN> - <LogicalTrackDisplayName>.mid/.wav`，NN 使用整个 Project 的一基手动顺序且至少两位；逐 Port MIDI 为 `Port <PP>.mid`；Readme 为 `README.md`。MIDI Conductor Track Name 使用任务准备时冻结的 Project Name，空白时回退 `Conductor`；事件 Track Name 为一基 `Port <P> / Channel <C>`，不经过文件名合法化。多文件模式选择完整输出目录，不自动增加嵌套目录。
17. Note Number/Velocity Mapping 是强制共享目标；非 Note Event Mapping 与 Logical Parameter Mapping 是可删除 owner，缺少时原始值直通且普通事件编辑不得静默重建。状态型非 Note 原始值按最近事件或 Initial State/default 持有，Envelope 从该状态连续求值。普通 Gate/Release/Tail 结束只执行精确 NoteOff，不做通用目标 Reset，也不得发送 CC120；目标闭包的 Reset Defaults 在 lane 首次启用或非重叠复用时、Initial State/用户事件/NoteOn 之前建立，共享 lane 内仍重叠的后续 Gate 不重复重置。CC120 和最终目标 Reset 只允许用于 Segment/消费者范围硬边界。发声 Segment 的 Unit lane/audio fragment 必须持续到 Segment End，使普通 NoteOff 后的 SoundFont 原生 release 进入实时、离线和缓存 PCM；不得以 instance end 停止解码作为隐式硬裁剪。
18. Midora 初版定位为免费、开源、非商业软件；Midora 自有源代码固定使用根目录 `LICENSE` 中未经自定义修改的标准 MIT License，版权署名固定为 `Copyright (c) 2026 Midora contributors`。项目自身非商业不得转化为限制下游商业使用的附加条款。该许可证不覆盖 BASS/BASSMIDI/BASSWASAPI，也不自动证明满足其免费使用条件；正式分发前必须按实际主体、收入、平台、分发方式和届时有效条款完成核验并提供第三方 notices。
19. Pure MIDI 正式模型固定为 `MIDI Channel Root → Pure MIDI Track → Midi Segment → Direct MIDI Note/Event`。Root 是持久 Unit 身份且结构上必须非空；子 Segment End 只关闭本 Segment Note，Root 活动连通区间结束才做 CC120/最终 Reset。分配顺序为上下文内 Fixed Roots→含参与 Segment 内容的 Auto Roots→剩余 Logical Usage groups，combined peak 不得超过 256。
20. `Open MIDI as New Project` 只接受 SMF 1.0 Format 0/1 + TPQN，支持 Running Status、MIDI Port、多 Channel MTrk 拆分与 opaque SysEx/Meta 保留，并 detached/原子提交；不支持 Import into Current Project、Format 2、SMPTE division 或字节级 round-trip。
21. SMF MTrk 按 Port/Channel 拆分时，opaque SysEx/Meta 必须按 source MTrk/effective Port 恰好保留一次；没有 Channel bucket 但需要保留 opaque/空结构时使用确定的 structure-only Pure MIDI Track。不得把 opaque 复制到每个派生 Track、静默丢弃或虚构 Channel Event。
22. Arrangement 正式结构固定为 Conductor 第一行与全局混排的 Logical / Pure MIDI Track 平铺顺序；Event Instrument Definition 由 Arrangement 内独立管理栏维护，Event Instrument Usage 与 MIDI Channel Root 只是不可见共享执行身份。删除 Project Panel 与独立 parent/child 顺序。完整规则见 SRS 第 24 章与 INV-058～064、INV-073～074。
23. Logical Track 可以是未绑定且无内容的空壳，也可引用独立或共享 Event Instrument Usage；Usage 引用一个 Definition。Pure MIDI Track 必须引用非空 Root，最后一条成员移走或删除时必须原子删除 Root，Undo 同时恢复。Definition 可有零个 Usage，仍被引用时禁止删除。
24. Arrangement 的实际 Track 与共享 Usage/Root block 可分别拥有独立的运行时 Mute/Solo；这些状态不得持久化、进入 Undo 或修改 Project/canonical，且 group 开关不得改写成员 Track 开关。Logical/Direct Note 跨类型剪贴板只转换共同字段；Direct NoteOff velocity 在 Direct 数据链和 SMF 中必须保留。
25. Pure MIDI Segment preview 的 event 线绘制在 Note 上层、固定 50% 透明度、至少 1 device pixel，并与 Note 使用独立 tile cache；Conductor 第一行使用按类型着色的固定 device-size point tile。两者不得堆 WPF Controls、不得从 bitmap 反推命中或音乐语义。
26. Logical Track 普通 `Duplicate` 深拷贝 Track subtree 并创建引用同一 Definition 的新独立 Usage；只有显式 `Duplicate and Share State` 保留源 Usage。普通副本位于完整源 Shared block 之后，共享副本位于源 Track 之后且留在 block 内。Definition Browser 的普通 Duplicate 只复制 Definition；Track/Usage 上下文不得恢复 `Duplicate Instrument Only`。

## 8. 已批准的 UI 样式基线

- 产品所有者已于 2026-08-08 完成 WPF 样式样例的三轮视觉评审，并批准其作为后续 Midora 正式 UI 的默认视觉基线。除非用户明确修改视觉方向，正式 UI 默认采用暗色、黑色与红色主色、简约且低视觉噪声的样式。
- 基线实现位于 `src/midora-desktop/Midora.Desktop.StyleGallery/`；需求、边界和验收记录位于 `misc/Midora-WPF-Style-Gallery-Requirement-Trace.md`。正式 UI 应复用或迁移其中的 Palette、通用 ControlTemplate、Fluent System Icons Geometry 和 WindowChrome 行为，不得另建一套视觉 token 或复制后静默分叉。
- 标题栏最小化、最大化、还原和关闭图标使用样例中的四个窗口控制 SVG/Geometry；其布局为 `10 × 10`、启用 Layout Rounding，按钮为直角方形，三个图标视觉亮度一致，悬停时指针保持 Arrow。按产品所有者要求，不记录这四个文件的来源，也不为其建立单独授权核验项。
- 通用控件必须保持样例已经通过评审的状态区分和布局边界：文本不得裁切；输入框内边距不得重复；图标、数字徽标和增减符号视觉居中；焦点使用低强调虚线轮廓；Pressed 与静止态可辨；一级菜单文字居中并贴合底部分割线；滚动条完整显示；最大化时窗口严格使用当前显示器工作区、移除外框和 resize border。
- Fluent System Icons 的上游 revision 与 MIT notice 继续由样例的 `THIRD-PARTY-NOTICES.md` 记录。正式 UI 若增加图标，应优先沿用同一图标体系并同步 notices。
- 该批准只固定视觉与控件行为基线，不把 Style Gallery 的静态展示数据、布局占位或交互假实现提升为正式业务需求。正式 UI 仍必须遵循 SRS 第 17～20 章和本文件规定的领域、状态、持久化与消费者边界。
- Arrangement 的当前正式导航与高性能概览额外服从 SRS 第 24 章：无 Project Panel，Conductor 固定第一行，Logical/Pure MIDI Track 按唯一全局顺序平铺，共享 Usage/Auto Root 以连续 brace block 呈现而不占空行；Pure MIDI event preview 必须位于 Note 上层且为 50% 透明度，Conductor/Segment 极端内容使用可视瓦片缓存和局部失效。
