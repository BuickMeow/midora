# 第 22 章 主题索引与跨系统不变量

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章提供快速定位表与跨章节不变量，便于开发、讨论、测试和评审时稳定引用。

## 22.1 跨系统不变量
| 编号 | 不变量 |
|---|---|
| INV-001 | Project 是编译、播放、预览、MIDI 导出、音频渲染与持久化的完整上下文。 |
| INV-002 | Conductor Track 固定存在且不参与 Channel Unit 分配。 |
| INV-003 | Event Instrument 与 Logical Track 通过稳定 ID 引用；名称不构成身份。 |
| INV-004 | Logical Track 不直接代表 MIDI Track、Port 或 Channel。 |
| INV-005 | SubVoice 在单个 Event Instrument Instance 中原则上需要独立 Channel Unit。 |
| INV-006 | 初版最多 16 Ports × 16 Channels = 256 Channel Units。 |
| INV-007 | 所有 Port 的 Channel 10 均按 melodic Channel 使用。 |
| INV-008 | SoundFont 不改变编译和 MIDI 导出语义，只影响实际发声。 |
| INV-009 | Canonical Compiled Result 是所有正式输出消费者的唯一音乐语义来源。 |
| INV-010 | 增量编译结果必须等价于同一上下文的确定性全量编译。 |
| INV-011 | Mute/Solo 是运行期监听状态，不属于 Project，也不影响成品输出。 |
| INV-012 | `.midora` 保存 Project 源数据，不保存编译结果、播放缓存、输出产物或 Undo/Redo 历史。 |
| INV-013 | 保存从当前内存 Project 重建完整 package，不保留未知或孤立文件。 |
| INV-014 | UI 只呈现和操作系统语义，不得重新解释编译、生命周期、资源或输出规则。 |
| INV-015 | 同一输入、上下文与有效资源状态必须产生确定一致的正式结果。 |
| INV-016 | 初版不支持 Reverb / Chorus；正式 BASSMIDI Stream 启用 `BASS_MIDI_NOFX`，Project、编译结果和 MIDI 导出不得包含 CC91 / CC93。 |
| INV-017 | 实时播放按所选输出设备的实际采样率生成音频；文件渲染按本次选择的 8,000–192,000 Hz 整数采样率生成音频。 |
| INV-018 | 音频活动线程在 Playing、Buffering、Preview Playing 和文件 Rendering 阶段不得产生托管堆分配；Preparing / Finalizing 不受此限制。 |
| INV-019 | 初版只允许一个用户可启动并打开 Project 的应用实例；正式音频后端固定为由该实例管理的无 UI、不能独立打开或解释 Project 的 Native AOT 内部音频子进程。 |
| INV-020 | 满足正确性、确定性和资源上限的候选实现中，时间性能优先于最小空间占用。 |
| INV-021 | 实时播放、预览和音频渲染的 tick→sample 映射使用完整 Tempo Map 的 decimal 区间积分，乘采样率后只执行一次 `AwayFromZero`，不得逐 Tempo 段取整。 |
| INV-022 | 初版 Limiter 版本 1 固定为 stereo-linked、sample-peak、瞬时 attack、zero-look-ahead、线性 ceiling 1.0、50 ms 单极指数 release；实时与离线使用同一算法。 |
| INV-023 | 初版正式 WASAPI 输出固定为 Shared Mode、event-driven、stereo interleaved float32；采样率、实际 buffer 与 callback period 由端点初始化结果决定，不得静默回退到其他模式或格式。 |
| INV-024 | 初版正式实时音频工作 block 固定为最多 256 frames；子进程内 Render-Ahead PCM ring 容量按实际采样率和用户毫秒设置向上取整为 frame，不固定 ring block 数；实时 PCM 不跨进程，运行时控制 IPC 使用固定版本二进制共享内存且热路径零分配。 |
| INV-025 | 正式 BASSMIDI Stream 启用 `BASS_MIDI_NOTEOFF1`；同 Port、Channel、pitch 的重叠实例按 FIFO 与逐个 NoteOff 配对，硬边界必须按活动实例数完整释放。 |
| INV-026 | 正式 BASSMIDI Stream 固定 8-point sinc 和 CPU 属性 0；音频在 canonical 成功后按抽象 Unit 使用 1-channel Stream 语义和有界复用 pool。实时/离线分别配置 Maximum Sample Voices per Unit Stream，默认均为 500，同一任务所有 Unit 使用同一冻结值。完美音频一致性测试以未触顶为前提。 |
| INV-027 | Midora 初版只发布 `win-x64`；主应用、Native AOT 音频子进程及 BASS/BASSMIDI/BASSWASAPI 必须同为 x64，不发布 x86、Arm64 或 AnyCPU 正式产物。 |
| INV-028 | 初版正式 BASS 原生基线固定为 BASS 2.4.18.3、BASSMIDI 2.4.16.0、BASSWASAPI 2.4.4.1 及第 13.30 节列出的 win-x64 DLL SHA-256；正式构建和运行时必须分别校验文件 hash 与完整版本码，不得自动跟随 vendor current/latest。 |
| INV-029 | 初版 C# Mapping ABI v2 固定 `double Transform(double value, in MappingContextV2 context)`、单 `long` 的 `MappingStableIdV2`、C# 14、`Microsoft.NETCore.App.Ref 10.0.10` 与独立只读契约；每 Project 仅缓存当前源码修订并用 collectible AssemblyLoadContext 卸载旧项，编译产物不得持久化。引用白名单不是 sandbox。 |
| INV-030 | 初版持久化兼容基线固定为 JSON Schema Draft 2020-12 与 protobuf Edition 2024；结构性 JSON/protobuf 严格拒绝未知字段，JSON 还拒绝重复属性。已发布 `.proto` 字段号、descriptor、golden bytes 与固定 runtime/codegen profile属于兼容承诺。 |
| INV-031 | 外部 Project SF2 只保存项目根目录或直属 `soundfonts/` 中的相对路径；精确大小写优先，唯一 ignore-case 回退产生 Warning，歧义时不可用。SHA-256 基于完整原始字节且仅由用户明确绑定/接受更新；被动资源变化不修改 Project。 |
| INV-032 | 工程总耗时按 Project 成功打开后的完整会话单调累计，包含空闲、最小化、失焦、Buffering、MIDI 导出与音频渲染；系统睡眠 / 休眠和关闭流程暂停。自动累计不单独标记 Modified，也不影响 canonical 语义。 |
| INV-033 | 初版 SMF Type 1 兼容档固定：Tempo 用 `60,000,000 / BPM` 后一次 `AwayFromZero`；拍号 `cc=24`、`bb=8`；Bank 顺序为 CC0→CC32→Program；文本 Meta 为严格 UTF-8；每个 Channel Event 显式 status；导出器不在 canonical 之外追加 Channel 清理；所有 Track 的 EOT 对齐统一 endTick。 |
| INV-034 | MIDI 导出中，每个实际包含 Channel 10 canonical 事件的事件 Track 在相对 tick 0、Port Meta 后、canonical 事件前固定写一次 GS Normal Part 与一次 XG Normal Part SysEx，顺序 GS→XG；不得发送 GS/XG/GM Reset，不得改变 canonical Bank/Program；不相关事件 Track 与 Conductor 不写。 |
| INV-035 | MIDI 导出与音频文件渲染必须共用同一确定性 Windows 安全文件名合法化和冲突检测服务：NFC、固定不安全字符集合、设备保留名前缀 `_`、文件名部分最多 255 UTF-16 code unit、text-element 安全截断、NFC + OrdinalIgnoreCase 冲突键和稳定 ` (n)` 后缀。合法化后的完整最终路径必须在任务开始前预览并冻结；不修改 Project 源名称，已有目标不参与后缀分配且仍需明确覆盖授权。 |
| INV-036 | 初版输出模板固定：整曲 MIDI / 音频为 `<ProjectStem>.mid/.wav`，来源依次为 Project 名称、当前 `.midora` stem、模式 fallback；分 Track 为 `<NN> - <LogicalTrackDisplayName>.mid/.wav`，逐 Port MIDI 为 `Port <PP>.mid`，Readme 为 `README.md`。MIDI Conductor Track Name 使用任务准备时冻结的 Project Name，空白时回退 `Conductor`；事件 Track Name 为一基 `Port <P> / Channel <C>`，且不经过文件名合法化。多文件模式选择完整输出目录，不自动增加嵌套目录。 |
| INV-037 | Midora 初版定位为免费、开源、非商业软件，但 BASS/BASSMIDI/BASSWASAPI 不属于 Midora 的开源许可范围。正式分发第三方二进制前必须按实际发布主体、收入方式、平台、分发方式和发布时有效条款完成许可核验并提供 notices；条件不明或商业化时不得沿用免费非商业结论。 |
| INV-038 | Midora 自有源代码固定使用根目录 `LICENSE` 中未经自定义修改的标准 MIT License，版权署名为 `Copyright (c) 2026 Midora contributors`；项目自身的非商业发布定位不得转化为限制下游商业使用的附加许可条件。 |
| INV-039 | Event Instrument / SubVoice 虚拟键盘、Segment Editor Pitch Ruler 和单个 Logical Note 放置预览必须复用同一 held Preview 因果 Gate：Gate End 前 `MappingContext.gateLength = Int64.MaxValue`，Gate End 从 producer 尚未渲染的第一个 frame 起生效，不回写已消费或已缓冲 PCM；钢琴卷帘不得另建裸 MIDI 试听路径。 |
| INV-040 | Project 内全部稳定 ID 共享一个持久化单调正 `long` 分配器，合法范围为 `1..long.MaxValue`，不补缺、不复用且不具业务排序语义；JSON 使用 canonical 十进制 integer，对象文件名使用无符号无前导零十进制 ASCII，protobuf 在既有外层字段号上使用标量 `int64`。 |
| INV-041 | 开发期 v1 中每个 Time Signature 必须满足 `4 × TPQ % denominator == 0`。变化 tick 立即开启新 Bar；若截断旧小节则产生 Warning。Domain、编译、持久化、`Bar:Beat:Tick` 与自然拍网格必须共用该整数、可逆语义。 |
| INV-042 | 音频缓存分为 canonical range、Segment/Unit fragment、Unit raw PCM、playback span 与短 Render-Ahead ring；exact replay 的完整命中不得重复语义编译或 BASSMIDI 合成。 |
| INV-043 | underrun 在失败位置锁存，完整准备“当前自然小节剩余 + 下一完整小节”（若位于小节起点则当前完整小节），并以播放终点与 16 个四分音符裁剪后才恢复；不得短块断续推进。 |
| INV-044 | session 音频缓存不进入 `.midora`，默认 root `%LOCALAPPDATA%\Midora\AudioCache`、reusable quota 16 GiB 且允许 0；transient recovery spool 独立，无法取得 spool/RAM 时受控 Stop。 |
| INV-045 | MIDI 导出中，每个实际有 Channel Event 的 Channel Unit（原始 Port + Channel）在同一文件内严格对应一个事件 Track；每个事件 Track 只含一个 Channel。同一 Unit 被不同 Logical Track / Instance 先后复用时仍合并为一个 Track，按原始 Port→Channel 排序。 |
| INV-046 | 状态型非 Note Event Mapping 的原始值按最近原始事件或有效 Initial State/default 持有；Envelope/连续源在实例与 Release 的整数 tick 上从该值求值，非零 Release 的最后有效 tick 达到 End Value。普通 Gate/Release/Tail 结束不发送 CC120；CC120 只用于 Segment/消费者范围硬边界。 |
| INV-047 | Note Number/Velocity Mapping 是强制共享目标；非 Note Event Mapping 与 Logical Parameter Mapping 是可删除 owner。缺少可选 Mapping 表示原始值直通，普通事件编辑和打开修复不得静默重建已删除 owner。 |
| INV-048 | 发声 Segment 的 Channel Unit lane/audio fragment 从首次使用持续到 Segment End；同 Segment 的非重叠 instance 可复用 lane，但跨 Segment 不得提前复用。普通 instance NoteOff 后的 SoundFont 原生 release 必须进入实时、离线和缓存 PCM，只有 Segment/消费者范围硬边界可以硬裁剪。 |
| INV-049 | 普通 Gate/Release/Tail 结束只执行精确 NoteOff，不执行通用目标 Reset。lane 首次启用或无重叠 instance 后被非重叠复用时，按实际目标闭包执行 Reset Defaults → Initial State/用户状态 → NoteOn；共享 lane 内仍重叠的后续 Gate 不重复初始化。Segment/消费者范围硬边界仍执行 CC120 与最终目标 Reset。 |
## 22.2 常用主题定位
| 需要查找的主题 | 主要章节 |
|---|---|
| 软件定位、技术边界 | 第 1 章 |
| 术语、编号、身份、确定性 | 第 2 章 |
| Project、保存入口、修改状态 | 第 3 章 |
| tick、TPQ、Tempo、拍号、Marker | 第 4 章 |
| Port、Channel Unit、资源不足 | 第 5 章 |
| SF2、无 SF2、资源引用 | 第 6 章 |
| Event Instrument 库和定义 | 第 7 章 |
| SubVoice、Note/CC/RPN 等事件 | 第 8 章 |
| Logical Parameter、映射和 C# 函数 | 第 9 章 |
| Release、Loop、Envelope、Overlap | 第 10 章 |
| Track、Segment、裁剪与 Logical Note | 第 11 章 |
| CompileContext、资源分配、Compiled Result | 第 12 章 |
| 播放、预览、held Preview 因果 Gate、BASSMIDI、输出设备、采样率、buffer、Limiter | 第 9、12、13、18、20 章 |
| MIDI 文件结构与导出 | 第 14 章 |
| 普通 RIFF/WAVE、自定义采样率与离线渲染 | 第 15 章 |
| `.midora` package、schema、损坏与事务 | 第 16 章 |
| 主窗口、导航和全局面板 | 第 17 章 |
| 各编辑器工作区 | 第 18 章 |
| New/Open/Save/Export/Render 工作流 | 第 19 章 |
| 选择、拖放、验证、快捷键和 UI 验收 | 第 20 章 |
| 初版排除项、实现自由度和变更控制 | 第 21 章 |
## 22.3 推荐引用方式
在讨论、设计记录、Issue 和代码评审中，应使用：
```text
《Midora SRS》第 12 章“编译系统与 Canonical Compiled Result”
《Midora SRS》§16.21“打开流程”
《Midora SRS》INV-009
```
章节号和小节标题共同构成引用。仅引用标题而不引用章节号时，应避免使用容易重复的泛化名称。
