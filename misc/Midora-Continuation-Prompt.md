# Midora 跨聊天续接提示词

用途：把下方整个 `text` 代码块原样复制到新的 Codex 聊天。它是接手索引，不替代仓库内 SRS、ADR、源码、测试和实时 Git 状态。

```text
你现在接续 Midora 项目的领域层、编译器、播放控制、MIDI 导出、音频全链路、持久化与后续 WPF 工作。

仓库绝对路径：
D:\Programing\midora

默认使用简体中文。必须严谨、直接、以事实为先。回答和交付中明确区分：

- SRS 已规定的正式需求；
- 从当前源码、测试、构建和 Git 状态确认的实现事实；
- ADR 已接受的实现决定；
- 合理设计推断；
- 未实现能力；
- 尚待产品所有者决定的问题。

不得把路线图、建议、原型行为、测试能发声、合理猜测或本提示词本身冒充 SRS 原文。

============================================================
一、接手后必须先做的只读检查
============================================================

1. 完整读取下列文件：

   - D:\Programing\midora\AGENTS.md
   - D:\Programing\midora\misc\Midora-SRS-Initial-Release-v0.1\00-Table-of-Contents-and-Document-Control.md
   - D:\Programing\midora\misc\Midora-SRS-Initial-Release-v0.1\22-Requirement-Locator-and-Cross-System-Invariants.md
   - D:\Programing\midora\misc\Midora-SRS-Code-Conformance-Audit-2026-08-05.md
   - D:\Programing\midora\misc\Midora-Core-Architecture-Decisions.md
   - D:\Programing\midora\misc\Midora-Audio-Backend-Architecture-Decisions.md
   - D:\Programing\midora\misc\Midora-Implementation-Roadmap.md
   - 与本次具体任务直接相关的所有 SRS 章节。

2. 需求优先级：

   - `misc/Midora-SRS-Initial-Release-v0.1/` 是正式需求基线。
   - `AGENTS.md` 是仓库工作约束和已经确认规则的摘要。
   - Core/Audio Architecture Decisions 是已接受设计记录。
   - Code Conformance Audit 是全量核对、修正和未实现项记录。
   - Implementation Roadmap 是实施建议与历史现状，不是需求规范；冲突时以 SRS 为准。
   - 本续接提示只用于导航，任何细节仍须回到正式文件和源码核验。

3. 立即执行只读 Git 检查：

   git status --short --branch
   git log -8 --oneline --decorate
   git diff --stat
   git diff --cached --stat

4. 所有工作树现有修改均先视为用户改动。不得 reset、checkout、覆盖、删除、移动、格式化或顺手提交无关文件。只有能够从任务、diff 和提交历史明确证明归属后，才能纳入本轮。

5. 在修改前检查相关 solution、project、源码和测试，并写一份简短 requirement trace，至少包括：

   - 输入；
   - 正式输出；
   - 时间、范围、资源和文件边界；
   - 失败条件与失败原子性；
   - 诊断类别和严重级别；
   - 持久化归属；
   - 运行时归属；
   - 明确非目标。

6. 如果选择会影响可听结果、确定性、公共接口、`.midora` 兼容性、文件产物、并发所有权或用户工作流，而 SRS/ADR 没有闭合，先记录准确冲突和候选方案，再请产品所有者决定。一般实现细节不应包装成产品决定。

============================================================
二、交接时 Git 与验证快照（必须重新核验，不能盲信）
============================================================

交接生成时的已确认事实：

- 日期：2026-08-06。
- 分支：`main`。
- HEAD 与 `origin/main`：`3578e82 docs: 落地MIT版权署名`。
- 工作树：干净。
- 最近关键提交：
  - `3578e82 docs: 落地MIT版权署名`
  - `561bee7 docs(srs): 固定MIT开源许可证`
  - `a3db8b5 docs(srs): 固定非商业开源发布定位`
  - `df8960e feat(common): 固定初版输出命名模板`
  - `ed9ab5c feat(common): 固定输出文件名合法化`
  - `3d51485 docs(srs): 统一输出文件名合法化边界`
  - `c72d282 feat(midi): 固定Channel 10导出初始化`
  - `80da779 feat(midi): 固定SMF兼容编码档`

以上只是交接快照。新聊天必须以当时真实的 Git 状态为准；若不同，先查明新增提交或改动归属，不得回退用户工作。

最近一次代码全量验证证据：

- `midora-common.slnx` Release：0 warning / 0 error。
- `midora-core.slnx` Release：0 warning / 0 error。
- `midora-midi.slnx` Release：0 warning / 0 error。
- `midora-audio.slnx` Release：0 warning / 0 error，全部列出的依赖输出位于 `bin/Release`。
- 8 个测试项目合计 310/310：
  - Midora.Common.Tests：68；
  - Midora.Compiler.Tests：88；
  - Midora.Persistence.Tests：21；
  - Midora.MidiExport.Tests：9；
  - Midora.Playback.Tests：27；
  - Midora.Midi.Tests：18；
  - Midora.Audio.Bass.Tests：58；
  - Midora.AudioDevice.BassWasapi.Tests：21。
- `dotnet format src\midora-common\midora-common.slnx --no-restore --verify-no-changes` 通过。
- 此后直到本提示生成，只提交了 SRS、许可和续接文档，没有代码语义变化。

注意：不要把历史通过当作本轮修改后的验证结果。只报告实际在本轮运行且成功的检查。

============================================================
三、不可破坏的系统主线和范围语义
============================================================

所有正式消费者必须遵循：

Project Source Data
→ Semantic Validation
→ Compilation
→ Canonical Compiled Result
→ Playback / Preview / MIDI Export / Audio Rendering

硬约束：

1. Project 是完整语义上下文；消费者不得重新解释 Event Instrument、Mapping、Lifecycle、Segment、Logical Track、资源分配或 Reset 规则。
2. Canonical Compiled Result 是播放、预览、MIDI 导出和音频渲染唯一正式音乐语义输入。
3. Full Compile 与 Incremental Compile 对同一输入必须语义和形式逐字段完全一致。
4. 稳定 ID 是身份；名称、列表位置、tick、MIDI Track/Port/Channel 都不是身份。
5. 正式结果不得依赖集合枚举顺序、线程竞争、历史分配、随机数、缓存命中或 UI 临时状态。
6. 所有统一范围使用 `[startTick, endTick)`。Segment、End Marker、播放范围和渲染范围的硬边界必须精确 NoteOff、Reset 和清理。
7. Mute/Solo 只属于实时消费过滤；不得修改 Project、Canonical Result、MIDI 导出或音频渲染正式内容。
8. `.midora` 只保存源数据；不保存 canonical、编译缓存、运行缓存、导出结果、Undo/Redo、播放位置或会话 UI 状态。
9. UI 只展示、编辑和导航正式模型及诊断，不得重建第二套业务语义。

初版范围固定为 Windows Desktop、.NET 10、WPF、MIDI 1.0、`win-x64`、单用户可启动应用实例、单 Project、单 Project SoundFont、最多 16 Port × 16 Channel Unit。不得顺手加入 MIDI 2.0、MPE、VST/DAW host、传统实时 MIDI OUT、录音、语义级 Voice Stealing、多 SoundFont、SFZ/DLS、Pause/Scrub、多 Project 或其他 SRS 排除项。

============================================================
四、产品所有者已经确认的全部决定
============================================================

以下决定已经写入 SRS/ADR/代码或测试。不得重新询问、恢复旧分支或另设可配置选项。

1A. Envelope Preset 固定为 SRS 10.11 的 ADSR-like 结构，不采用任意有序点/曲线段模型。

2A. 同一 tick 可有多个普通 Marker；名称可空、可重复；以稳定 ID 区分和确定同 tick 次序。

3A. 稳定 ID 的 JSON、对象文件名和 `nextStableId` 使用 32 位小写十六进制字符串，高 64-bit 在前；protobuf 使用 `StableId { fixed64 high = 1; fixed64 low = 2; }`。

4A. 新建 Event Instrument 默认 Overlap=`Reject`；命中产生 Error。用户显式 `Warn` 时产生 Warning，默认可消费；Warning-as-error 只改变成功判定，不改变诊断级别。

5A. 整数目标默认 `Round`，midpoint=`AwayFromZero`；可选 `Floor`/`Ceil`。只在完整 Mapping 链最终输出取整一次；越界策略归属最终目标配置。

6A. 连续值源以有效范围内每个整数 tick 的最终目标值为参考语义；canonical 输出首值和后续变化值。优化必须与逐 tick 参考结果完全一致，不允许误差阈值或自适应近似。

7. SRS 已闭合：最终完全无输出的实例不分配 Channel Group、不占 Channel Unit，无需再次确认。

8A. tick→sample frame 使用完整 Tempo Map 在 `[originTick, targetTick)` 上做 decimal 分段积分，总时长乘采样率后只执行一次 `AwayFromZero`。不得逐段取整或用两个已取整绝对位置相减。

9A. Limiter v1 固定为 stereo-linked sample-peak、瞬时 attack、zero-look-ahead、linear ceiling=1.0、50 ms 单极指数 release；按实际采样率算系数，跨 block 保持 gain，新任务/Reset 恢复 1.0；不做 true-peak。

10A. WASAPI 固定 Shared Mode、event-driven、stereo interleaved float32；采样率跟随初始化后的设备实际混音采样率。Device Buffer Request 只是请求值，period 请求为 0；不回退 Exclusive、轮询、整数格式或其他声道/采样率。

11A. 实时合成和 Render-Ahead producer 最大工作块固定 256 frames；事件边界和末尾允许短块。子进程内部使用单个有界 SPSC PCM ring，容量为 `ceil(actualSampleRate × RenderAheadMilliseconds / 1000)` frames；实时 PCM 不跨进程。

12C. 唯一正式拓扑为完整内部音频子进程：Worker 独占 BASS/BASSMIDI/Limiter/Render-Ahead/BASSWASAPI/device callback/文件 OutputDevice；主进程持有 Project、Compiler、Canonical Result、UI 和任务协调。Worker 仅 `win-x64` Native AOT、自包含发布。控制/状态使用固定版本、固定布局、有界二进制共享内存 ABI；热路径禁止托管分配，禁止 JSON/文本逐消息反序列化。

13A. 正式 BASSMIDI stream 固定启用 `BASS_MIDI_NOTEOFF1`。同 Port/Channel/pitch 重叠 Note 按最早开始者优先、逐个 NoteOff FIFO 配对；硬边界按活动实例数完整释放。

14A. 正式 stream 固定 `BASS_ATTRIB_MIDI_SRC=1`（8-point sinc）和 `BASS_ATTRIB_MIDI_CPU=0`。Preparing 使用 `BASS_MIDI_FontLoad` 预加载冻结计划引用的 presets 和 fallback，不对实时事件 stream 调 `StreamLoadSamples`。实时/离线 sample voice 上限分别配置，范围 1–16,777,216，默认均为每 stream 750，同一任务各 Port 使用同一冻结值；完美一致性测试假定未触顶。

15A. 初版只发布 `win-x64`；主应用、Native AOT Worker 和 BASS/BASSMIDI/BASSWASAPI 同为 x64，不发布 x86、Arm64 或 AnyCPU 正式产物。

16A. 原生基线固定：

- BASS 2.4.18.3 / `0x02041203`；
- BASSMIDI 2.4.16.0 / `0x02041000`；
- BASSWASAPI 2.4.4.1 / `0x02040401`；
- SHA-256 以 `src/midora-audio/bass-native-baseline.win-x64.json` 为准。

仓库不提交 DLL。正式构建只接受操作员提供且逐文件匹配 manifest 的二进制，运行时校验完整版本码；vendor current/latest 只能生成显式开发候选，不能自动升级正式基线。

17A. C# Mapping ABI v1 固定签名 `double Transform(double value, in MappingContextV1 context)`；独立只读契约，Roslyn 5.3.0、C# 14、`Microsoft.NETCore.App.Ref 10.0.10`，不开放 Midora/WPF/第三方引用。缓存键包含 ABI/compiler profile/函数体 UTF-8 SHA-256；每 Project 只保留当前源码修订并用 collectible ALC 卸载。该机制不是 sandbox。

18A/18.1A. 持久化基线：

- JSON Schema Draft 2020-12；
- 内部版本化 `System.Text.Json` source-generated DTO；
- 严格拒绝重复和未知 JSON 属性；
- protobuf Edition 2024、Google.Protobuf 3.35.1、Grpc.Tools 2.83.0；
- `.proto` 和 descriptor SHA-256/golden bytes 进入兼容门；
- 未知 protobuf tag 严格拒绝；
- 生成 C# 只在 `obj`；
- 文本上限按 Unicode scalar 固定为 256 / 4,096 / 65,536 / 1,048,576；
- 相对路径最多 4,096 scalars，使用 `/`，保留大小写和原 Unicode，不 normalization；
- 颜色为 opaque sRGB，JSON `#rrggbb`；
- UTC 为七位小数秒 `yyyy-MM-ddTHH:mm:ss.fffffffZ`；
- 总编辑时长为非负 int64 毫秒。

19A. Project SoundFont 使用严格 External/Embedded union。External 只允许 Project 根目录或直属 `soundfonts/` 相对 SF2；逐分量精确 case 优先，唯一 ignore-case 回退并 Warning，歧义拒绝。SHA-256 对完整原始字节流式计算，只在用户明确选择、替换、重绑定或接受当前内容时更新；被动缺失/hash mismatch/fallback 不修改 Project。绝对路径和验证缓存只属于运行时。

20A. 工程总耗时从 Project 成功新建/打开到开始关闭，以单调时钟累计整个打开会话，包括空闲、最小化、失焦、模态 UI、Buffering、MIDI 导出和音频渲染；系统睡眠/休眠和 closing 暂停。关闭取消后只恢复后续累计。自动累计不进入 Undo/Redo、不单独标记 Modified、不更新 metadata 修改时间、不影响 canonical。

21A. SMF Type 1 兼容档固定：

- Tempo 为十进制 `60,000,000 / BPM` 后一次 `AwayFromZero`，结果限 1..0xFFFFFF；
- Time Signature 固定 `cc=24`、`bb=8`；
- Bank 顺序 CC0→CC32→Program；
- 事件 Track 只写 Track Name + MIDI Port，不写 Device/Program Name；
- 文本 Meta 严格 UTF-8；
- 不用 Running Status，每条 Channel Event 显式 status；
- 导出器不在 canonical 外追加 Channel 清理；
- 所有 Track EOT 对齐统一 endTick。

22A. 每个实际包含 Channel 10 canonical 事件的事件 Track，在相对 tick 0、MIDI Port Meta 后、canonical Channel Event 前固定写 GS→XG 两条 Normal Part SysEx：

- Roland GS：`F0 41 10 42 12 40 10 15 00 1B F7`；
- Yamaha XG：`F0 43 10 4C 08 09 07 00 F7`。

不发送 GS/XG/GM Reset，不改 canonical Bank/Program；不相关 Track 和 Conductor 不写。Readme 必须说明接收端忽略 vendor SysEx 或设备号不同时仍可能把 Channel 10 当鼓通道。

23A/23.1A. MIDI 与音频共用唯一 Windows 安全文件名合法化和冲突服务：

- stem/extension NFC；非法 UTF-16 原子失败；
- Win32 `< > : " / \ | ? *`、Unicode Control 及 SRS 14.17.4 固定风险字符表的连续段替换为单个 `_`；
- 保留 ZWNJ、ZWJ、Variation Selector、emoji tag；
- 清除 stem 首尾 ASCII space 和尾部 period，截断后重做尾部清理；
- Windows 设备保留名，包括 CON/CONIN$/CONOUT$/PRN/AUX/NUL、COM1–9/上标 1–3、LPT1–9/上标 1–3，统一加 `_` 前缀；
- 最终文件名部分最多 255 UTF-16 code units，按 .NET text element 边界截断；
- 同目录以 NFC + OrdinalIgnoreCase 判断冲突；
- 按稳定源顺序、再按稳定 source key 分配无后缀、` (2)`、` (3)`；
- 已有文件不参加后缀分配，覆盖仍需单独明确授权；
- 公共规划器不读文件系统，任务开始前预览并冻结全部最终路径；
- 不修改 Project 源名称，不进入 Undo/Redo。

23.2A. 初版模板固定：

- 整曲 MIDI：`<ProjectStem>.mid`，Project name → 当前 `.midora` stem → `Midora MIDI Export`；
- 整曲音频：`<ProjectStem>.wav`，Project name → 当前 `.midora` stem → `Midora Render`；
- 分 Track MIDI/音频：`<NN> - <LogicalTrackDisplayName>.mid/.wav`；
- NN 是整个 Project 当前一基手动顺序，至少两位，宽度随总 Track 数增长，不按选择子集重编号；
- 空、仅空白或合法化后空的 Track 名称 fallback 为 `Logical Track <N>`；
- 逐 Port MIDI：`Port <PP>.mid`，PP=01..16；
- Readme：`README.md`；
- MIDI Conductor Track Name：`Conductor`；
- 事件 Track Name：`<原始 Track 名称或 fallback> / Port <P>`，P=1..16 不补零；
- MIDI Track Name 不经过文件名合法化，保留原始文本并严格 UTF-8；
- 多文件模式选择完整输出目录，不自动再创建嵌套目录。

24A. Midora 初版产品定位为免费、开源、非商业软件。BASS/BASSMIDI/BASSWASAPI 不纳入 Midora 开源许可证。正式分发仍按实际主体、收入方式、平台、分发方式和届时有效条款核验 BASS 非商业免费使用条件并提供第三方 notices；条件不明或商业化时先联系权利人或取得适用许可。

25B/25.1A. Midora 自有源代码采用根目录 `LICENSE` 中未经修改的标准 MIT License，版权署名为 `Copyright (c) 2026 Midora contributors`。项目自身非商业不限制下游商业使用。BASS、用户 SoundFont 和其他第三方材料不纳入 MIT 许可。根目录 `THIRD-PARTY-NOTICES.md` 已建立。

当前没有剩余的产品所有者技术或发布语义决定。只有新实现发现新的 SRS 内部冲突或未闭合外部语义时才追加决定；不得重新询问 1A–25.1A。

============================================================
五、音频链的强制实现边界
============================================================

1. BASS 是实现细节，不得出现在 Project/Compiler 领域模型中。
2. 每个实际使用的 Port 创建一个干净 BASSMIDI decode stream；不预建 16 个永久 stream。
3. 所有实际 Port 使用同一正式生效的 Project SF2。无有效 SF2 可以打开、编译、MIDI 导出，但必须阻止播放、预览和音频渲染。
4. stream 创建、重建和复用前显式 Reset 后重建 melodic Channel 10 和规范初始状态；只在精确 NoteOff/Reset/清理后复用。
5. 不用 `Thread.Sleep`、UI timer 或 API 调用瞬间承担 MIDI 时序。Canonical 事件经统一 tick→sample 后做 sample-accurate 调度并保持同 tick 顺序。
6. 正式实时链：实际 Port stereo 求和 → Playback Master Volume → Limiter → WASAPI。预览同链。离线整曲链语义相同但不依赖 WASAPI。
7. WASAPI callback 不编译、不分配普通托管对象、不阻塞、不等待锁、不做文件/网络 I/O；只消费已准备好的连续 float32 frames。异常不得越过 native callback 边界。
8. Playing、Buffering、Preview Playing、Rendering 的 callback、调度、合成协调、混音、buffer 搬运和文件 sample 写入线程不得产生托管堆分配。Preparing/Finalizing 可以分配。
9. 协议以 frame 为单位，显式携带 sample rate、channel count、sample format、frame count；不得混淆 byte/sample/frame。
10. 可调实时参数只有：Render-Ahead 20–2000 ms 默认 100；Device Request 5–200 ms 默认 50；Realtime Maximum Sample Voices per Stream 1–16,777,216 默认 750。离线 voice 上限属于 Project Audio Render Settings，范围相同默认 750。
11. 实时音频跟随设备实际采样率；文件输出采样率 8,000–192,000 任意整数，默认 48,000。
12. 文件输出固定普通 RIFF/WAVE、stereo、interleaved IEEE float32 LE。超过 RIFF 大小上限在 Preparing 原子失败，不拆分、不 RF64。使用临时文件→校验→原子发布事务。
13. 原生全局初始化、线程 device context、handle、delegate/GCHandle 和卸载顺序集中管理；每个原生返回值都检查，错误码立即在同线程读取。
14. 约 200 ms 端到端延迟只是性能基准，不是设置或成功条件。

============================================================
六、当前代码实际已经做到什么
============================================================

以下是实现事实，不等于初版全部完成：

1. Domain/Compiler 已有可运行的垂直切片和较完整确定性测试；已修正 Project ID、稳定 ID、End Marker、Marker/Track 空名、Template Note 边界、Bank Select 部分存在性、Compile Purpose、partial 语义、canonical telemetry 等早期 SRS 冲突。

2. 当前增量编译按 Track 缓存展开片段，并始终重新执行全局确定性资源分配、排序、范围恢复和裁剪；它仍未满足 SRS 12.21 强制的 Segment checkpoint + dirty range + input/output state hash 收敛模型。Full Compile 仍是 oracle，现有增量只能视为过渡实现。

3. C# Mapping ABI v1 已有独立契约、固定编译 profile、source hash identity、collectible ALC 当前修订缓存和测试。

4. Persistence 已实现：

- v1 common/manifest/metadata/soundfont-settings schema/codec；
- StrictJsonV1；
- protobuf descriptor-aware StrictProtobufWireV1；
- descriptor SHA-256 和代表性 golden/契约测试；
- SoundFont External/Embedded 领域引用、相对路径解析和流式 SHA-256 基础；
- Project Metadata 和单 owner 单调会话计时基础。

5. MIDI 已实现：

- 低层 SMF Type 1 writer/parser validator；
- 整曲 CanonicalMidiFileExporter；
- 严格 UTF-8、无 running status、Tempo/拍号/Bank/EOT 固定兼容档；
- Channel 10 GS→XG Normal Part 初始化；
- 结构自校验和 golden/一致性测试。

6. 公共输出命名已实现：

- `Midora.OutputPlanning.WindowsOutputFileNamePlanner`：纯 Preparing 公共合法化/冲突规划器；
- `Midora.OutputPlanning.InitialReleaseOutputNaming`：整曲、分 Track、逐 Port、README 和 MIDI Track Name 模板；
- 相关测试覆盖精确字符表、设备名、Unicode、长度、冲突、乱序、fallback 和原子失败。

7. Audio 已有正式方向的垂直切片：

- 完整内部音频 Worker 和共享内存控制 ABI；
- Render-Ahead ring、WASAPI 回调消费、BASSMIDI Port stream、Master/Limiter；
- win-x64 Native AOT publish 约束；
- 固定版本/hash native manifest；
- Wave float32 writer；
- overlap/NoteOff/NOFX/Channel 10/voice limit/零分配等测试基础。

8. 根目录已有：

- `LICENSE`：MIT，`Copyright (c) 2026 Midora contributors`；
- `THIRD-PARTY-NOTICES.md`；
- 仓库不含 BASS DLL。

============================================================
七、尚未完成的初版模块
============================================================

这些是实现缺口，不是待重新决定的产品语义：

1. 完整 `.midora`：ZIP package、所有剩余 schema、Project object graph round-trip、迁移、安全 Save/Save Copy、重开校验、损坏隔离、未知内容拒绝、内嵌 SF2 复制和事务发布。
2. Application Preferences、完整 Project Defaults、Modified/Undo/Redo、WPF 电源/suspend/closing 生命周期接线。
3. Event Instrument、SubVoice、Mapping、Lifecycle、Logical Track/Segment/曲线的完整编辑器级领域对象与命令。
4. SRS 12.21 Segment checkpoint、dirty propagation、state hash 收敛和正式 incremental compiler。
5. Canonical CompileContext 摘要、失败阶段、完整来源追踪和可选 Debug 诊断。
6. MIDI 按 Logical Track、按 Port、多文件目录、Compact Routing、Readme 内容、事务、覆盖确认、取消/进度和完整报告。
7. 正式 Audio Render workflow：Whole Mix/Per Logical Track、任务快照、子进程内文件 OutputDevice、取消、独立/原子发布语义、结果报告。
8. SoundFont 的 BASSMIDI 可加载性验证、监控、Embedded package 完整实现。
9. 完整 WPF UI、导航、编辑器、对话框、Project 打开/关闭和任务工作流。
10. Native interop 的结构布局、calling convention、位宽、重复 init/free、handle/delegate 泄漏完整自动化门。
11. WASAPI 设备移除/默认设备变化、不同 callback block、deadline、underrun、长期运行、故障恢复和正式硬件矩阵。
12. BASS 非商业免费使用条件在实际发布主体/收入/平台/分发方式上的最终发布核验，以及 SF2 内容许可和最终 notices/package 验收。

建议优先级以 `misc/Midora-Implementation-Roadmap.md` 和用户本次指令为准。若用户没有指定下一任务，可以提出以下候选但不要自动开工：

- 完整 `.midora` 持久化最小垂直切片；
- SRS 12.21 正式 incremental compiler；
- MIDI 多文件导出 workflow；
- 正式 Audio Render workflow；
- WPF 应用生命周期与 Project 工作流。

============================================================
八、关键代码和文档定位
============================================================

正式文档：

- `misc/Midora-SRS-Initial-Release-v0.1/`
- `misc/Midora-SRS-Code-Conformance-Audit-2026-08-05.md`
- `misc/Midora-Core-Architecture-Decisions.md`
- `misc/Midora-Audio-Backend-Architecture-Decisions.md`
- `misc/Midora-Implementation-Roadmap.md`

主要 solutions：

- `src/midora-common/midora-common.slnx`
- `src/midora-core/midora-core.slnx`
- `src/midora-midi/midora-midi.slnx`
- `src/midora-native-interops/midora-native-interops.slnx`
- `src/midora-audio-device/midora-audio-device.slnx`
- `src/midora-audio/midora-audio.slnx`

关键代码：

- Domain：`src/midora-core/Midora.Domain/`
- Compiler：`src/midora-core/Midora.Compiler/`
- Compiler tests：`src/midora-core/Midora.Compiler.Tests/`
- Persistence：`src/midora-core/Midora.Persistence/`
- Persistence tests：`src/midora-core/Midora.Persistence.Tests/`
- Mapping ABI：`src/midora-core/Midora.Mapping.Contract.V1/`
- Playback：`src/midora-core/Midora.Playback/`
- Playback/BASS-WASAPI adapter：`src/midora-core/Midora.Playback.BassWasapi/`
- MIDI primitives：`src/midora-midi/Midora.Midi/`
- MIDI canonical exporter：`src/midora-core/Midora.MidiExport/`
- Output filename planner：`src/midora-common/Midora.Common/WindowsOutputFileNamePlanner.cs`
- Initial output templates：`src/midora-common/Midora.Common/InitialReleaseOutputNaming.cs`
- Audio abstractions：`src/midora-audio/Midora.Audio/`
- BASS renderer/worker client：`src/midora-audio/Midora.Audio.Bass/`
- Native AOT Worker：`src/midora-audio/Midora.Audio.Bass.Worker/`
- Audio device abstractions：`src/midora-audio-device/Midora.AudioDevice/`
- WASAPI device：`src/midora-audio-device/Midora.AudioDevice.BassWasapi/`
- WAVE device：`src/midora-audio-device/Midora.AudioDevice.Wave/`
- Native interop：`src/midora-native-interops/`
- BASS native baseline：`src/midora-audio/bass-native-baseline.win-x64.json`

============================================================
九、实现与编辑规则
============================================================

1. 使用 `rg`/`rg --files` 搜索；用 `apply_patch` 修改文本文件。不要用脚本重写用户文件，不要顺手格式化无关项目。
2. 保护 dirty worktree。严禁 `git reset --hard`、`git checkout -- <file>` 或其他丢弃用户内容的操作，除非用户明确要求且目标精确。
3. 涉及可听语义、持久化格式、公共接口或并发所有权时，先更新/新增 ADR 或 requirement trace，再实现。
4. 优先完成 Domain → Compiler → Canonical Result → Consumer 的最小垂直切片，不要先造与正式链脱离的大型框架。
5. 测试 console 只能作为人工 smoke test，不能代替自动化、语义和资源验收。
6. 任何正式原生调用都检查返回值并立即读取当前线程错误码；清理失败同样要进入诊断。
7. 不因为 BASS“能发声”就认为时序、状态、资源、错误和零分配合规。
8. 不擅自修改已确认 SRS 语义。发现新的内部冲突时记录具体章节、源码影响和候选解释，请产品所有者决定。
9. SRS 没规定且不影响外部语义的一般类型名、局部数据结构或测试组织，可依据现有风格直接实现，无需产品确认。

============================================================
十、构建、格式和测试方法
============================================================

常用 Release 构建：

dotnet build 'src\midora-common\midora-common.slnx' -c Release --no-restore
dotnet build 'src\midora-core\midora-core.slnx' -c Release --no-restore
dotnet build 'src\midora-midi\midora-midi.slnx' -c Release --no-restore
dotnet build 'src\midora-audio\midora-audio.slnx' -c Release --no-restore

重要陷阱：这些 solution 共享跨目录 ProjectReference 和同一 `obj/Release`。不要并行构建四个 solution，否则可能出现 CS2012 文件锁；应串行构建。`midora-audio.slnx` 已显式包含跨目录依赖，检查输出必须全部在 `bin/Release`。

自动测试项目：

dotnet test 'src\midora-common\Midora.Common.Tests\Midora.Common.Tests.csproj' -c Release --no-build
dotnet test 'src\midora-core\Midora.Compiler.Tests\Midora.Compiler.Tests.csproj' -c Release --no-build
dotnet test 'src\midora-core\Midora.Persistence.Tests\Midora.Persistence.Tests.csproj' -c Release --no-build
dotnet test 'src\midora-core\Midora.MidiExport.Tests\Midora.MidiExport.Tests.csproj' -c Release --no-build
dotnet test 'src\midora-core\Midora.Playback.Tests\Midora.Playback.Tests.csproj' -c Release --no-build
dotnet test 'src\midora-midi\Midora.Midi.Tests\Midora.Midi.Tests.csproj' -c Release --no-build
dotnet test 'src\midora-audio\Midora.Audio.Bass.Tests\Midora.Audio.Bass.Tests.csproj' -c Release --no-build
dotnet test 'src\midora-audio-device\Midora.AudioDevice.BassWasapi.Tests\Midora.AudioDevice.BassWasapi.Tests.csproj' -c Release --no-build

先完成串行构建后，互不写 `obj` 的 `--no-build` 测试可并行。报告各项目实际通过数量，不只报告总退出码。

格式：

- 修改 C# 后对相关 solution 运行 `dotnet format ... --no-restore`。
- 再运行 `dotnet format ... --no-restore --verify-no-changes`。
- 仓库 C# 使用 CRLF；`apply_patch` 可能产生局部 LF，formatter 会报告 ENDOFLINE，必须实际运行 format 修复后再 verify。
- 提交前运行 `git diff --check` 和 `git diff --cached --check`。

PowerShell profile 偶尔会输出与仓库无关的 Terminal-Icons 初始化消息。只有命令退出码和具体项目测试结果能证明成功，不要把 profile 噪声当成仓库失败或忽略真实失败。

涉及真实 BASS 的测试要求操作员提供匹配 manifest 的 DLL 和适用环境。DLL 缺失时明确区分“测试未运行/环境缺失”与“测试失败”，不得下载或采用 current/latest 替代正式基线。

============================================================
十一、Git 提交与推送约定
============================================================

原用户已经要求“提交并推动 git”。因此在每个完整、已验证、范围清晰的增量完成后：

1. 再检查 `git status --short`，只 stage 本轮文件。
2. 运行 staged diff 检查。
3. 使用仓库现有 Conventional Commits 风格，示例：
   - `feat(compiler): 中文说明`
   - `feat(midi): 中文说明`
   - `feat(audio): 中文说明`
   - `feat(persistence): 中文说明`
   - `docs(srs): 中文说明`
4. 提交当前分支；当前通常是 `main`，但必须以实时状态为准。
5. 推送对应远端分支；推送失败必须报告真实原因，不声称已成功。
6. 推送后确认工作树状态和 `HEAD`/upstream。

不得把未通过验证、partial、与用户改动混杂或存在未解决 SRS 冲突的内容强行提交为完成状态。

============================================================
十二、新聊天第一条回复的要求
============================================================

在用户尚未给出新的具体实现任务时，只做只读接手检查，然后用简洁中文报告：

1. 已完整读取哪些正式规格、ADR 和工作约束；
2. 实时 Git 分支、HEAD、upstream 与工作树状态；
3. 是否存在必须保护的已有改动；
4. 当前没有未确认的产品所有者技术/发布语义决定；
5. 已实现和未实现边界仍以审计第 2、3 节为准；
6. 请用户指定下一项具体实现任务，或从持久化、正式增量编译、MIDI 多文件导出、Audio Render workflow、WPF 生命周期候选中选择。

在用户给出具体任务前，不修改代码、SRS、Git 或外部系统。收到任务后自主读取相关 SRS、源码和测试，完成实现、验证、提交和推送；除非缺少会显著改变结果的产品决定，否则不要用不必要的澄清阻塞工作。
```
