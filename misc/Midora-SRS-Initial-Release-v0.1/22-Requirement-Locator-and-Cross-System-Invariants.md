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
| INV-003 | Event Instrument Definition、Event Instrument Usage、Logical Track、MIDI Channel Root、Pure MIDI Track 与 Segment 均以稳定 ID 构成身份和引用；名称、顺序、tick、Port.Channel 与源 MTrk index 均不得替代身份。 |
| INV-004 | Logical Track 通过可空 Usage ID 间接引用 Event Instrument Definition；有内容的 Logical Track 必须拥有 Usage。Pure MIDI Track 必须属于一个非空 MIDI Channel Root，并由该 Root 代表单一 Channel Unit。 |
| INV-005 | SubVoice 在单个 Event Instrument Instance 中原则上需要独立 Channel Unit。 |
| INV-006 | 初版最多 16 Ports × 16 Channels = 256 Channel Units。 |
| INV-007 | Logical/Event Instrument 获配的 Channel 10 始终为 melodic；Pure MIDI Root 的 Channel 10 由 Root 的 Melodic/Percussion 模式决定，SMF 导入的 Channel 10 Root 默认 Percussion。 |
| INV-008 | SoundFont 不改变编译和 MIDI 导出语义，只影响实际发声。 |
| INV-009 | Canonical Compiled Result 是所有正式输出消费者的唯一音乐语义来源。 |
| INV-010 | 增量编译结果必须等价于同一上下文的确定性全量编译。 |
| INV-011 | Mute/Solo 是运行期监听状态，不属于 Project，也不影响成品输出。 |
| INV-012 | `.midora` 保存 Project 源数据，不保存编译结果、播放缓存、输出产物或 Undo/Redo 历史。 |
| INV-013 | 保存从当前内存 Project 重建完整 package，不保留未知或孤立文件。 |
| INV-014 | UI 只呈现和操作系统语义，不得重新解释编译、生命周期、资源或输出规则。 |
| INV-015 | 同一输入、上下文与有效资源状态必须产生确定一致的正式结果。 |
| INV-016 | 正式 BASSMIDI Stream 启用 `BASS_MIDI_NOFX`，Midora 不承诺 Reverb/Chorus 音频效果；Event Instrument/SubVoice 不得创建或映射 CC91/CC93，Pure MIDI Track 则必须允许、编译并在 SMF 中原样导出，音频投影确定性忽略其效果且不报错。 |
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
| INV-030 | 初版持久化兼容基线固定为 JSON Schema Draft 2020-12 与 protobuf Edition 2024；结构性 JSON/protobuf 严格拒绝未知字段，JSON 还拒绝重复属性。已发布 `.proto` 字段号、descriptor、golden bytes 与固定 runtime/codegen profile 属于兼容承诺。 |
| INV-031 | 外部 Project SF2 只保存项目根目录或直属 `soundfonts/` 中的相对路径；精确大小写优先，唯一 ignore-case 回退产生 Warning，歧义时不可用。SHA-256 基于完整原始字节且仅由用户明确绑定/接受更新；被动资源变化不修改 Project。 |
| INV-032 | 工程总耗时按 Project 成功打开后的完整会话单调累计，包含空闲、最小化、失焦、Buffering、MIDI 导出与音频渲染；系统睡眠 / 休眠和关闭流程暂停。自动累计不单独标记 Modified，也不影响 canonical 语义。 |
| INV-033 | 初版 SMF Type 1 导出兼容档固定：Tempo 用 `60,000,000 / BPM` 后一次 `AwayFromZero`；拍号 `cc=24`、`bb=8`；Bank 顺序为 CC0→CC32→Program；文本 Meta 为严格 UTF-8；每个 Channel Event 显式 status；导出器不在 canonical 之外追加 Channel 清理。Conductor/Logical Unit MTrk 的 EOT 使用统一 endTick；Pure MIDI MTrk 使用冻结的自身 EOT。 |
| INV-034 | MIDI 导出中，Logical Channel 10 Unit MTrk 与 Melodic Channel 10 Pure MIDI MTrk 在相对 tick 0、Track Name/Port/结构 Meta 后、canonical Channel Event 前固定写一次 GS Normal Part 与一次 XG Normal Part SysEx，顺序 GS→XG；Percussion Root、不相关 MTrk 与 Conductor 不写；不得发送 GS/XG/GM Reset 或改变 canonical Bank/Program。 |
| INV-035 | MIDI 导出与音频文件渲染必须共用同一确定性 Windows 安全文件名合法化和冲突检测服务：NFC、固定不安全字符集合、设备保留名前缀 `_`、文件名部分最多 255 UTF-16 code unit、text-element 安全截断、NFC + OrdinalIgnoreCase 冲突键和稳定 ` (n)` 后缀。合法化后的完整最终路径必须在任务开始前预览并冻结；不修改 Project 源名称，已有目标不参与后缀分配且仍需明确覆盖授权。 |
| INV-036 | 初版输出模板固定：整曲 MIDI / 音频为 `<ProjectStem>.mid/.wav`，来源依次为 Project 名称、当前 `.midora` stem、模式 fallback；分 Logical Track 为 `<NN> - <LogicalTrackDisplayName>.mid/.wav`，逐 Port MIDI 为 `Port <PP>.mid`，Readme 为 `README.md`。MIDI Conductor Track Name 使用冻结 Project Name；Logical Unit MTrk 为 `Port <P> / Channel <C>`，Pure MIDI MTrk 保持冻结用户 Track Name。多文件模式选择完整输出目录，不自动增加嵌套目录。 |
| INV-037 | Midora 初版定位为免费、开源、非商业软件，但 BASS/BASSMIDI/BASSWASAPI 不属于 Midora 的开源许可范围。正式分发第三方二进制前必须按实际发布主体、收入方式、平台、分发方式和发布时有效条款完成许可核验并提供 notices；条件不明或商业化时不得沿用免费非商业结论。 |
| INV-038 | Midora 自有源代码固定使用根目录 `LICENSE` 中未经自定义修改的标准 MIT License，版权署名为 `Copyright (c) 2026 Midora contributors`；项目自身的非商业发布定位不得转化为限制下游商业使用的附加许可条件。 |
| INV-039 | Event Instrument / SubVoice 虚拟键盘、Segment Editor Pitch Ruler 和单个 Logical/Direct MIDI Note 放置预览必须复用正式预览管线；Event Instrument held Preview 使用因果 Gate：Gate End 前 `MappingContext.gateLength = Int64.MaxValue`，Gate End 从 producer 尚未渲染的第一个 frame 起生效，不回写已消费或已缓冲 PCM。 |
| INV-040 | Project 内全部稳定 ID 共享一个持久化单调正 `long` 分配器，合法范围为 `1..long.MaxValue`，不补缺、不复用且不具业务排序语义；JSON 使用 canonical 十进制 integer，对象文件名使用无符号无前导零十进制 ASCII，protobuf 在既有外层字段号上使用标量 `int64`。 |
| INV-041 | 开发期 v1 中每个 Time Signature 必须满足 `4 × TPQ % denominator == 0`。变化 tick 立即开启新 Bar；若截断旧小节则产生 Warning。Domain、编译、持久化、`Bar:Beat:Tick` 与自然拍网格必须共用该整数、可逆语义。 |
| INV-042 | 音频缓存分为 canonical range、Logical Segment/Unit fragment、Pure MidiSegment normalized fragment、Root merged checkpoint、Unit/Root raw PCM、playback span 与短 Render-Ahead ring；exact replay 的完整命中不得重复语义编译或 BASSMIDI 合成。 |
| INV-043 | underrun 在失败位置锁存，完整准备“当前自然小节剩余 + 下一完整小节”（若位于小节起点则当前完整小节），并以播放终点与 16 个四分音符裁剪后才恢复；不得短块断续推进。 |
| INV-044 | session 音频缓存不进入 `.midora`，默认 root `%LOCALAPPDATA%\Midora\AudioCache`、reusable quota 16 GiB 且允许 0；transient recovery spool 独立，无法取得 spool/RAM 时受控 Stop。 |
| INV-045 | MIDI 导出中，Logical/Event Instrument 的每个实际有事件 Unit 在同一文件内严格对应一个单 Channel MTrk；Pure MIDI 的每个被选择 Track 对应一个独立单 Channel MTrk，同一 Root 的多个 MTrk 可以共享 Port.Channel，名称、global Arrangement Track 顺序和自身 EOT 必须保留。 |
| INV-046 | 状态型非 Note Event Mapping 的原始值按最近原始事件或有效 Initial State/default 持有；Envelope/连续源在实例与 Release 的整数 tick 上从该值求值，非零 Release 的最后有效 tick 达到 End Value。普通 Gate/Release/Tail 结束不发送 CC120；CC120 只用于 Segment/消费者范围硬边界。 |
| INV-047 | Note Number/Velocity Mapping 是强制共享目标；非 Note Event Mapping 与 Logical Parameter Mapping 是可删除 owner。缺少可选 Mapping 表示原始值直通，普通事件编辑和打开修复不得静默重建已删除 owner。 |
| INV-048 | 发声 Segment 的 Channel Unit lane/audio fragment 从首次使用持续到 Segment End；同 Segment 的非重叠 instance 可复用 lane，但跨 Segment 不得提前复用。普通 instance NoteOff 后的 SoundFont 原生 release 必须进入实时、离线和缓存 PCM，只有 Segment/消费者范围硬边界可以硬裁剪。 |
| INV-049 | 普通 Gate/Release/Tail 结束只执行精确 NoteOff，不执行通用目标 Reset。lane 首次启用或无重叠 instance 后被非重叠复用时，按实际目标闭包执行 Reset Defaults → Initial State/用户状态 → NoteOn；共享 lane 内仍重叠的后续 Gate 不重复初始化。Segment/消费者范围硬边界仍执行 CC120 与最终目标 Reset。 |
| INV-050 | 一个 MIDI Channel Root 是一个持久 Channel Unit 身份；同 Root Pure MIDI Track 的正式同 tick 合并语义固定为 absolute tick → global Arrangement Track order → event explicit order。重复状态事件、重叠 Note 和跨 Track 冲突不得折叠或拒绝。 |
| INV-051 | Pure MIDI Root 的活动连通区间是真正 Channel 生命周期：子 Segment End 只关闭该 Segment 拥有的 Note，不重置共享状态或 sibling Note；Root 连通区间/Project/消费者硬边界才执行 Root 级精确 NoteOff、CC120、最终 Reset 与释放。 |
| INV-052 | Unit 分配顺序固定为：验证本次上下文全部非空 Fixed Roots → 按最早成员 global Track order 低号分配含参与 Segment 内容的 Auto Roots → 在剩余 Units 分配 Logical Usage groups。任何 Root/Usage 均不得为空；Root 不与 Logical instance 做时间复用，combined peak 不得超过 256。 |
| INV-053 | Canonical Compiled Result 必须从同一事件集冻结 Execution Projection 与 SMF Track Projection；消费者不得回读 Project 重建 Pure MIDI Track Name、Root membership、EOT、事件归属或顺序。 |
| INV-054 | `Open MIDI as New Project` 只接受 SMF 1.0 Format 0/1 + TPQN，支持合法 Running Status、MIDI Port 和源 MTrk 多 Channel 拆分；导入边界在验证前对缺失 tick 0 Tempo/Time Signature、同 tick 重复 Tempo 和不可用 Track Name 执行第 23.11.5 节的确定性兼容归一化；候选必须 detached 且原子提交，失败保留当前 Project，初版不支持 Import into Current Project。 |
| INV-055 | SMF 导出固定 Type 1 且不使用 Running Status；Track 顺序为 Conductor → global Arrangement Track order 过滤出的 Pure MIDI Tracks → Logical Unit Tracks（Port/Channel 顺序）。同 Root 跨 Pure MTrk 的顺序敏感同 tick 组合产生汇总 Warning，不得为兼容性改 tick、合并 Track 或改序。 |
| INV-056 | `.midora` 分别以 `midi-channel-roots/mcr_<id>.pb`、`midi-tracks/mt_<id>.pb` 与单 Track `midi-content/mt_<id>.mpk` 保存 Root/Track metadata 和 Direct/Opaque source pages；Auto 分配、canonical 投影、运行期 checkpoint 和 PCM 不持久化。本次未发布开发格式为破坏性替换，不提供旧开发格式迁移或双写。 |
| INV-057 | SMF 多 Channel/Port MTrk 拆分时，每个 opaque SysEx/Meta 必须恰好归属一个同 source MTrk/effective Port 的派生 Track，不得复制或丢弃；无 Channel bucket 但必须保留 opaque/空结构时创建确定的 structure-only Track，纯 Conductor 的 Format 1 MTrk 0 除外。 |
| INV-058 | Arrangement 的正式结构固定为唯一 Conductor 第一行与一个混排 Logical/Pure MIDI Track 的 global tagged order。Event Instrument Definition order、Usage/Root membership 与 Track order 正交；Arrangement 不显示 Event Instrument/Root 空白 parent row。 |
| INV-059 | Event Instrument Definition 的 Copy/Paste/Duplicate 只深拷贝定义及内部对象，不复制 Track/Usage。Logical Track 普通 Duplicate 深拷贝 Track subtree、创建引用同一 Definition 的新独立 Usage并插入完整源 block 之后；只有显式 `Duplicate and Share State` 保留源 Usage并紧邻源 Track 插入。Pure MIDI Track Duplicate 按第 24.8.3 节保留 route membership。Copy/Paste 与 Clipboard Cut 后 Paste 的新对象使用新稳定 ID；只有单命令 Header/brace Drag Move 保持 ID。owner 自动创建/删除与对应 Track 变更必须是一个 Undo。 |
| INV-060 | Track 与共享 Usage/Root block 的 Mute/Solo 仅属运行期且相互独立；过滤必须按来源精确释放和恢复，不得因单 Track 变化向整个共享 Unit 发送 CC120/Reset、杀死 sibling Note 或改变 Project/canonical。 |
| INV-061 | Logical Note 与 Direct MIDI Note 跨类型剪贴板只转换 relative tick、gate、key 和 NoteOn/instance velocity；Logical→Direct 的 NoteOff velocity 为 0，Direct→Logical 丢弃 NoteOff velocity，Direct→Direct 以及 Project/canonical SMF/export 必须保留原 Direct NoteOff velocity。 |
| INV-062 | `.midora` 分别保存 ordered Definition/Usage/Root indexes 与唯一 ordered tagged Arrangement Track index；Track 保存唯一 owner ID，Definition/Root 不再保存第二套 child order。Usage/Root 无成员、Track order 缺失/重复或 owner 不一致时打开失败。 |
| INV-063 | Pure MIDI Segment 概览的 Note 与 non-Note event 使用独立手工渲染缓存；event 线固定绘制在 Note 上层、透明度 50%、最小宽度 1 device pixel，并按正式值域归一化高度。概览 tile/LOD 不得成为命中、编译或导出语义来源。 |
| INV-064 | Conductor 在 Arrangement 固定第一行直接显示按事件类型着色、固定 device-size 的圆点概览，不使用 Segment；极端内容必须用可视 tile、按类型/像素列聚合与局部失效，Project End Marker 仍是专用竖线。 |
| INV-065 | Pure MIDI source 使用 immutable out-of-core pages 与 copy-on-write edit overlay；正常打开、编译、播放准备和 UI 浏览的常驻内存由活动页/可见范围决定，而不得与 Project 总 Direct Note/Event 数线性增长。页必须同时受 record count 与 decoded bytes 双重上限约束，并有明确 owner、checksum、generation、LRU 上限和释放时机。 |
| INV-066 | Canonical Compiled Result 的“一个正式事件集、Execution/SMF 两个冻结投影”是逻辑契约，不是连续数组契约。Pure MIDI 实现必须允许共享 immutable source pages、延迟 canonical range source、紧凑 consumer pages 与有界外部归并；任何正式消费者不得以全量 `ToArray()` 或第二套逐事件对象图作为入口。 |
| INV-067 | 实时播放的滚动准备必须覆盖 source/canonical range query、tick→sample event batches、IPC committed-prefix publication 与 PCM 水位。启动只等待当前光标的状态恢复和 Startup 2 s 连续窗口；超过 Target High 6 s 的远处事件不得被完整物化、hash、复制或传给 Worker 后才允许播放。 |
| INV-068 | 音频 IPC 的容量边界由 24-byte 追加事件记录、最多 16,384-record producer/reader batch、262,144-record Worker ring 和单调 committed prefix 定义，不按整 Project event count 或整计划文件大小定义。合法超大 Project 通过一个 session 私有事件文件与 seqlock control 增量传输；committed prefix大于ring时必须以最后已装载record frame公布排他的partial safe frontier，使renderer可推进并释放ring但不得越过不完整同frame后缀。截断、倒序、非法 committed snapshot、越界或 producer fault必须显式失败；reader/renderer不得互锁，也不能静默截断或提高无界上限。 |
| INV-069 | Pure MIDI Arrangement/Piano Roll/Velocity/Event Lane 的正式 UI 数据入口是按 tick/pitch/lane 的范围查询与 LOD 聚合。打开视图、平移或缩放不得建立全 Segment render item array、全量 ID dictionary 或全量 interval index；命中与编辑必须查询 source/index，不能从 bitmap 反推语义。 |
| INV-070 | Exact Root/Unit PCM 命中必须在 canonical/source range query 前抑制该 owner 覆盖范围内的 MIDI event demand；混合命中只生产 miss owner。Monitoring 使缓存失效时须从实际可听 frame 追加并原子发布新的 event generation，旧 generation 在 Worker 显式 Seek 前保持可读；切换后对本次 playback generation 单调保持 synthesis bypass，不得留下已跳过事件的未来缺口，也不得改变 Mute/Solo 或可听语义。 |
| INV-071 | Pure MIDI source pack 的播放端点索引由局部有序、最多 16,384-record 的 NoteOn/NoteOff/Channel pages 构成；NoteOn目录携带页内最大end tick。窗口查询使用目录裁剪、有界 k-way merge、Channel-state checkpoint和只读取候选endpoint页的active-note查询，不得反复扫描从Segment起点到光标的全部历史或全部相交原始Note page。索引是 source 的确定性派生物，不能改变 canonical fingerprint与事件顺序。 |
| INV-072 | Reusable PCM miss 必须以16,384-frame block直接顺序写入generation journal；仅完整、结构校验通过的generation可原子进入可命中索引。未完成/损坏journal只形成不可命中的dead bytes并由重整回收，不得将部分PCM block当作可恢复的SoundFont/BASS voice状态。 |
| INV-073 | Event Instrument Usage 是无用户名称的持久共享执行身份。Definition 可以零 Usage；Usage 必须至少一个 Logical Track。未启用逐音符隔离时，Usage 的跨 Track Segment 活动连通区间是真正 Channel Group 生命周期，成员 Segment End 不重置 sibling 状态。 |
| INV-074 | 所有 MIDI Channel Root 必须非空。Fixed Root 没有独立 UI 生命周期，route 只在 Root 保存一份但作为 Track 属性编辑；最后 Track 离开时同事务删除 Root，Undo 恢复原 ID。Fixed members 可分散，Auto shared members 必须连续。 |
## 22.2 常用主题定位
| 需要查找的主题 | 主要章节 |
|---|---|
| 软件定位、技术边界 | 第 1 章 |
| 术语、编号、身份、确定性 | 第 2 章 |
| Project、保存入口、修改状态 | 第 3 章 |
| tick、TPQ、Tempo、拍号、Marker | 第 4 章 |
| Port、Channel Unit、资源不足 | 第 5 章 |
| SF2、无 SF2、资源引用、新建 Project 默认 Embedded SF2 本机偏好 | 第 6、17、19 章 |
| Event Instrument 定义与内部索引 | 第 7、24 章 |
| SubVoice、Note/CC/RPN 等事件 | 第 8 章 |
| Logical Parameter、映射和 C# 函数 | 第 9 章 |
| Release、Loop、Envelope、Overlap | 第 10 章 |
| Logical Track、Logical Segment、裁剪与 Logical Note | 第 11 章 |
| CompileContext、资源分配、Compiled Result | 第 12 章 |
| 播放、预览、held Preview 因果 Gate、BASSMIDI、输出设备、采样率、buffer、Limiter | 第 9、12、13、18、20 章 |
| MIDI 文件结构与导出 | 第 14、23 章 |
| 普通 RIFF/WAVE、自定义采样率与离线渲染 | 第 15 章 |
| `.midora` package、schema、损坏与事务 | 第 16 章 |
| 主窗口、导航和全局面板 | 第 17、24 章 |
| 各编辑器工作区 | 第 18、24 章 |
| New/Open/Open MIDI as New Project/Save/Export/Render 工作流 | 第 17、19、23 章 |
| 选择、拖放、验证、快捷键和 UI 验收 | 第 20 章 |
| 初版排除项、实现自由度和变更控制 | 第 21 章 |
| MIDI Channel Root、Pure MIDI Track、Midi Segment、SMF 导入、Running Status、Pure MIDI 导出拓扑 | 第 23 章 |
| Arrangement 平铺 Track order、Event Instrument Usage、隐式 Root、独立/共享 Duplicate、共享块、跨类型 Note 剪贴板、Pure MIDI/Conductor 概览缓存 | 第 24 章 |
| 极端 Pure MIDI page pack、分页 canonical、滚动事件 IPC、范围查询 UI | 第 12、13、16、18、23、24 章 |
## 22.3 推荐引用方式
在讨论、设计记录、Issue 和代码评审中，应使用：
```text
《Midora SRS》第 12 章“编译系统与 Canonical Compiled Result”
《Midora SRS》§16.21“打开流程”
《Midora SRS》INV-009
```
章节号和小节标题共同构成引用。仅引用标题而不引用章节号时，应避免使用容易重复的泛化名称。
