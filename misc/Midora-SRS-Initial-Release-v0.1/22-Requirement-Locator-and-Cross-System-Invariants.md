# 第 22 章 主题索引与跨系统不变量

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章提供快速定位表与跨章节不变量，便于开发、讨论、测试和评审时稳定引用。

## 22.1 跨系统不变量
| 编号 | 不变量 |
|---|---|
| INV-001 | Project 是编译、MIDI 导出与所有音乐语义的完整上下文；播放、预览和音频渲染在 canonical 成功后额外冻结程序级音频设备与 SoundFont 列表设置，消费者不得据此改写 Project 语义。 |
| INV-002 | Conductor Track 固定存在且不参与 Channel Unit 分配。 |
| INV-003 | Event Instrument Definition、Event Instrument Usage、Logical Track、MIDI Channel Root、Pure MIDI Track 与 Segment 均以稳定 ID 构成身份和引用；名称、顺序、tick、Port.Channel 与源 MTrk index 均不得替代身份。 |
| INV-004 | Logical Track 通过可空 Usage ID 间接引用 Event Instrument Definition；有内容的 Logical Track 必须拥有 Usage。Pure MIDI Track 必须属于一个非空 MIDI Channel Root，并由该 Root 代表单一 Channel Unit。 |
| INV-005 | SubVoice 在单个 Event Instrument Instance 中原则上需要独立 Channel Unit。 |
| INV-006 | 初版最多 16 Ports × 16 Channels = 256 Channel Units。 |
| INV-007 | Logical/Event Instrument 获配的 Channel 10 始终为 melodic；Pure MIDI Root 的 Channel 10 由 Root 的 Melodic/Percussion 模式决定，SMF 导入的 Channel 10 Root 默认 Percussion。 |
| INV-008 | SoundFont 是程序级有序启停列表，不属于 Project；它不改变编译和 MIDI 导出语义，只影响本机实际发声。 |
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
| INV-022 | 初版 Limiter 版本 2 固定为 stereo-linked、5 ms look-ahead、4× 16-tap inter-sample peak detector、线性 ceiling `0.8912509`（-1 dBFS）、10 ms hold、100 ms 单极指数 release、无 makeup gain；状态跨 block 连续。实时首帧前预取，离线补偿前瞻并保持精确 frame 数；实时与离线使用同一算法，UI 只显示 `Limiter`。 |
| INV-023 | 初版正式 WASAPI 输出固定为 Shared Mode、event-driven、stereo interleaved float32；采样率、实际 buffer 与 callback period 由端点初始化结果决定，不得静默回退到其他模式或格式。 |
| INV-024 | 初版正式实时音频工作 block 固定为最多 256 frames；子进程内 Render-Ahead PCM ring 容量按实际采样率和用户毫秒设置向上取整为 frame，不固定 ring block 数；实时 PCM 不跨进程，运行时控制 IPC 使用固定版本二进制共享内存且热路径零分配。 |
| INV-025 | 正式 BASSMIDI Stream 启用 `BASS_MIDI_NOTEOFF1`；同 Port、Channel、pitch 的重叠实例按 FIFO 与逐个 NoteOff 配对，硬边界必须按活动实例数完整释放。 |
| INV-026 | 正式 BASSMIDI Stream 固定 8-point sinc 和 CPU 属性 0；音频在 canonical 成功后按抽象 Unit 使用 1-channel Stream 语义和有界复用 pool。实时/离线分别配置 Maximum Sample Voices per Unit Stream，默认均为 500，同一任务所有 Unit 使用同一冻结值。完美音频一致性测试以未触顶为前提。 |
| INV-027 | Midora 初版只发布 `win-x64`；主应用、Native AOT 音频子进程及 BASS/BASSMIDI/BASSWASAPI 必须同为 x64，不发布 x86、Arm64 或 AnyCPU 正式产物。 |
| INV-028 | 初版正式 BASS 原生基线固定为 BASS 2.4.18.3、BASSMIDI 2.4.16.0、BASSWASAPI 2.4.4.1 及第 13.30 节列出的 win-x64 DLL SHA-256；正式构建和运行时必须分别校验文件 hash 与完整版本码，不得自动跟随 vendor current/latest。 |
| INV-029 | 初版 C# Mapping ABI v2 固定 `double Transform(double value, in MappingContextV2 context)`、单 `long` 的 `MappingStableIdV2`、C# 14、`Microsoft.NETCore.App.Ref 10.0.10` 与独立只读契约；每 Project 仅缓存当前源码修订并用 collectible AssemblyLoadContext 卸载旧项，编译产物不得持久化。引用白名单不是 sandbox。 |
| INV-030 | 初版持久化兼容基线固定为 JSON Schema Draft 2020-12 与 protobuf Edition 2024；结构性 JSON/protobuf 严格拒绝未知字段，JSON 还拒绝重复属性。已发布 `.proto` 字段号、descriptor、golden bytes 与固定 runtime/codegen profile 属于兼容承诺。 |
| INV-031 | `.midora`、Project Domain 与 canonical 不得保存或引用 SoundFont。Application Preferences 只保存有序 `{absolute local .sf2/.sfz path, enabled, optional target Bank MSB/LSB/Program}` 列表；target 三项整体出现且均为 0～127，SFZ target 必填。Draft/持久化阶段不复制、不完整读取、不计算内容 hash，也不解析/快照/监控 SFZ 依赖。音频相关设置持久化后必须在 `Saving Settings` 运行时阶段销毁旧 Worker、直接由 BASSMIDI 加载原路径并保留新 Worker；失败保持已保存设置并明确报告。 |
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
| INV-042 | 音频缓存分为 canonical range、Logical Segment/Unit fragment、Pure MidiSegment normalized fragment、Root merged checkpoint、Unit/Root raw PCM、playback span 与短 Render-Ahead ring；exact replay 的完整命中不得重复语义编译或 BASSMIDI 合成。Pure MIDI 可听内容 identity 必须覆盖实际 Direct Note/Channel Event、分页源 fingerprint 与 COW delta，不得以集合 Generation、编辑次数或仅 stable ID 代替。 |
| INV-043 | underrun 在失败位置锁存，完整准备“当前自然小节剩余 + 下一完整小节”（若位于小节起点则当前完整小节），并以播放终点与 16 个四分音符裁剪后才恢复；不得短块断续推进。 |
| INV-044 | session 音频缓存不进入 `.midora`，默认 root `%LOCALAPPDATA%\Midora\AudioCache`、reusable quota 16 GiB 且允许 0；quota 满只停止新 reusable retention，既有命中继续可读且 miss 必须现场合成，不得静音或阻止播放；初始/运行期 Monitoring bypass 产生的不完整 entry 不得发布或禁用 retention，既有 failure state 不得被后续队列伪装成 quota-full；transient recovery spool 独立，无法取得 spool/RAM 时受控 Stop。 |
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
| INV-063 | Pure MIDI Segment 概览的 non-Note event 线与 Logical Segment 概览的 Logical Parameter point 线固定绘制在 Note 上层、透明度 50%、最小宽度 1 device pixel，并按各自正式值域归一化高度。Arrangement Segment 概览最高精度使用 `96 pixels / quarter note` 固定基准、256-pixel tile、64-pixel 高度；较低精度只允许固定半八度 `1 / 2^(n/2)` LOD，精确 viewport zoom 不得直接形成 cache identity。正式目标显示层必须选择不向下采样 source pixel 的固定层，全部 tile 从同一 device-pixel-snapped 完整 Segment 变换派生。最多两个后台 worker 为每个 Segment 预热完整且不超过 4 tile 的固定 LOD；可见 Segment fallback 比该预热层提高一倍水平分辨率且最多 8 tile，必须整帧发布，其 LOD 不得依赖 viewport。当前显示层更粗时允许缩放复用该 fallback，禁止缩小视图时清空概览。全部可见 fallback 就绪后才准入新的当前 LOD 请求；目标 tile 就绪后独占其横向范围，禁止在其下叠画 fallback，粗缓存不得因此删除。缓存不得成为命中、编译或导出语义来源。 |
| INV-064 | Conductor 在 Arrangement 固定第一行直接显示按事件类型着色、固定 device-size 的圆点概览，不使用 Segment；极端内容必须用可视 tile、按类型/像素列聚合与局部失效，Project End Marker 仍是专用竖线。 |
| INV-065 | Pure MIDI source 使用 immutable out-of-core pages 与 copy-on-write edit overlay；正常打开、编译、播放准备和 UI 浏览的常驻内存由活动页/可见范围决定，而不得与 Project 总 Direct Note/Event 数线性增长。页必须同时受 record count 与 decoded bytes 双重上限约束，并有明确 owner、checksum、generation、LRU 上限和释放时机。 |
| INV-066 | Canonical Compiled Result 的“一个正式事件集、Execution/SMF 两个冻结投影”是逻辑契约，不是连续数组契约。Pure MIDI 实现必须允许共享 immutable source pages、延迟 canonical range source、紧凑 consumer pages 与有界外部归并；任何正式消费者不得以全量 `ToArray()` 或第二套逐事件对象图作为入口。 |
| INV-067 | 实时播放的滚动准备必须覆盖 source/canonical range query、tick→sample event batches、IPC committed-prefix publication 与 PCM 水位。启动只等待当前光标的状态恢复和 Startup 2 s 连续窗口；超过 Target High 6 s 的远处事件不得被完整物化、hash、复制或传给 Worker 后才允许播放。 |
| INV-068 | 音频 IPC 的容量边界由 24-byte 追加事件记录、最多 16,384-record producer/reader batch、262,144-record Worker ring 和单调 committed prefix 定义，不按整 Project event count 或整计划文件大小定义。合法超大 Project 通过一个 session 私有事件文件与 seqlock control 增量传输；committed prefix大于ring时必须以最后已装载record frame公布排他的partial safe frontier，使renderer可推进并释放ring但不得越过不完整同frame后缀。截断、倒序、非法 committed snapshot、越界或 producer fault必须显式失败；reader/renderer不得互锁，也不能静默截断或提高无界上限。 |
| INV-069 | Pure MIDI Arrangement/Piano Roll/Velocity/Event Lane 的正式 UI 数据入口是按 tick/pitch/lane 的范围查询与 LOD 聚合。打开视图、平移或缩放不得建立全 Segment render item array、全量 ID dictionary 或全量 interval index；命中与编辑必须查询 source/index，不能从 bitmap 反推语义。 |
| INV-070 | Exact Root/Unit PCM 命中必须在 canonical/source range query 前抑制该 owner 覆盖范围内的 MIDI event demand；混合命中只生产 miss owner。Monitoring 使缓存失效时须从实际可听 frame 追加并原子发布新的 event generation，旧 generation 在 Worker 显式 Seek 前保持可读；Reader 的 published snapshot 与 generation-local counters 必须在 `Seek` 的同一 feeder lock 内读取，禁止跨 generation 混算。`Seek` frame 还是新 generation 后续 append 的持续下界，晚到但更早的 rewind-prefix 记录不得进入 ring。切换后对本次 playback generation 单调保持 synthesis bypass，不得留下已跳过事件的未来缺口，也不得改变 Mute/Solo 或可听语义。 |
| INV-071 | Pure MIDI source pack 的播放端点索引由局部有序、最多 16,384-record 的 NoteOn/NoteOff/Channel pages 构成；NoteOn目录携带页内最大end tick。窗口查询使用目录裁剪、有界 k-way merge、Channel-state checkpoint和只读取候选endpoint页的active-note查询，不得反复扫描从Segment起点到光标的全部历史或全部相交原始Note page。索引是 source 的确定性派生物，不能改变 canonical fingerprint与事件顺序。 |
| INV-072 | Reusable PCM miss 必须以16,384-frame block直接顺序写入generation journal；仅完整、结构校验通过的generation可原子进入可命中索引。未完成/损坏journal只形成不可命中的dead bytes并由重整回收，不得将部分PCM block当作可恢复的SoundFont/BASS voice状态。 |
| INV-073 | Event Instrument Usage 是无用户名称的持久共享执行身份。Definition 可以零 Usage；Usage 必须至少一个 Logical Track。未启用逐音符隔离时，Usage 的跨 Track Segment 活动连通区间是真正 Channel Group 生命周期，成员 Segment End 不重置 sibling 状态。 |
| INV-074 | 所有 MIDI Channel Root 必须非空。Fixed Root 没有独立 UI 生命周期，route 只在 Root 保存一份但作为 Track 属性编辑；最后 Track 离开时同事务删除 Root，Undo 恢复原 ID。Fixed members 可分散，Auto shared members 必须连续。 |
| INV-075 | 主窗口不得设置 Global Inspector、Bottom Panel、Details/Tasks Tab 或可见 Task History。Project-backed 属性只由对象所属 Workspace 或固定 `Properties...` 模态对话框呈现；对话框必须编辑 Draft，`OK` 以一个正式原子 Project command 提交，`Cancel` 不改变 Project。所有确认对话框复用同宽度红色 Primary / Cancel action contract，Enter / Escape 分别执行确定 / 取消（焦点控件自身消费 Enter 时除外）。多选 Mixed 字段默认禁用，只有显式开始统一值后才能编辑，并可逐字段还原；UI 不显示 Stable ID 或内部引用编号。 |
| INV-076 | Logical Parameter Lane 是离散 Step 点集：点值自该 tick 起保持到下一点，不存在 Linear/Step 用户选择。创建、复制、粘贴、变换、持久化校验、Full/Incremental Compile 与所有消费者必须保持该语义。Value Curve、Envelope 等其他正式曲线不受此规则替代。 |
| INV-077 | 用户编辑造成 exact collision 时：Logical/Direct/Template Note 的同 start tick + key 后来对象静默丢弃；Logical Parameter、Direct MIDI Channel Event 与 Template MIDI Event 的同 tick + 同正式事件类型由后来编辑对象覆盖原对象。未触及该 exact key 的导入重复 Direct MIDI 数据必须原样保留；碰撞归并属于编辑命令事务并可 Undo，不得由打开、浏览或编译静默改写源数据。 |
| INV-078 | 一次只允许一个可见前台任务表面；主窗口不保留历史任务列表。任务进度只有在有可靠 current/total 时才使用 determinate；MIDI 导入第一遍以源字节、第二遍以 processed/total events 计量。取消仅在任务仍处于安全可取消阶段时可用。导入完整兼容报告必须保留到 Dismiss/替换。该运行时状态不持久化、不进入 Undo/Redo。 |
| INV-079 | 所有实时/预览/离线音频任务使用任务开始时冻结的同一程序级 Enabled SF2/SFZ 有序配置；Worker 直接打开原绝对路径（仅 SF2 使用 `BASS_MIDI_FONT_MMAP`），并用 `BASS_MIDI_FONTEX2` 对每个 Unit 一次性设置完整 Font handle 与目标映射。列表、target 或其他音频配置变化只允许在 Stopped/Idle 提交，并在持久化后立即销毁旧 Worker、失效相关 sample-domain 缓存、重建并预热新 Worker；新建、打开、命令行打开、MIDI 导入形成 Project 会话和 Reset Playback Engine 也必须在其前台任务结束前接管或预热 Worker，不得推迟到首次 Play/Preview。成功后的 Worker 必须保留供后续音频操作复用；Project 已提交后的预热失败保留 Project 并作为独立音频运行时错误报告。缓存身份只可基于有序配置与主文件元数据的小型描述符；SFZ 依赖不进入身份，不得重新读取完整 SoundFont 或把该指纹描述为内容校验。 |
| INV-080 | Playback Master Volume、Limiter 与 Stop Cursor Behavior 是 Application Preferences；MIDI Export 与 Audio Render 的模式、范围、选择及输出参数只属于当前任务 Draft。`.midora` 与 Project Domain 不得保存 Playback、Export defaults 或 Audio Render defaults；当前开发格式不包含对应三个 settings 文件，导出/渲染对话框每次使用规格固定初始值。 |
| INV-081 | Event Instrument 可以持久化只存在 Loop Start 或 Loop End 的不完整 Loop Draft，以支持独立字段逐项编辑；该状态必须可 Undo/Redo 和确定性重开，但 Full/Incremental Compile 都必须产生 Error MIDORA1212 且结果不可消费，所有正式消费者不得解释它。两端都空表示禁用；两端齐全时必须满足 `0 <= Start < End <= Template Length`，单个已存在端点也必须位于 Template Length 内。 |
| INV-082 | 离开或关闭 Event Instrument Workspace 时，只自动停止由该 Workspace 底部 Preview Keyboard 启动且仍活动的 Held Preview（包括 Gate-open 与 release-tail）；判定必须基于该键盘预览的显式任务所有权，不得按宽泛 Playback/Preview 状态停止主时间线播放、普通 Preview、Segment/Pitch Ruler Preview 或其他音频任务。该行为是 Runtime/UI 清理，不修改 Project、canonical 或播放光标。 |
| INV-083 | Arrangement Segment 与 Logical/Direct/Template Note 的边界 Resize 在 Snap 开启时以当前有效 Operation Subdivision、Snap 关闭时以 `1 tick` 作为最小长度；批量对象逐项独立饱和。手势前已短于有效步长的对象以原长度为本次最小值，不能被约束反向扩长。交互预览与原子编辑命令必须采用同一最小长度。 |
| INV-084 | MIDI 导出 `README.md` 不记录程序级 SoundFont；其 `Notes` 单行字段必须直接使用本次冻结 Canonical Compiled Result 的准确 MIDI Note On 事件总数，并以 invariant 十进制输出，不得重新统计源对象、估算或使用 Markdown 引用块。 |
| INV-085 | MIDI 导出的每个实际单 Channel 事件 MTrk 都必须在相对 tick 0、结构 Meta 和可选 Channel 10 GS/XG 初始化之后、canonical/opaque 事件之前，依次写入 CC91=0、CC93=0；Conductor 不写。该初始化只属于 SMF 编码结果，不进入 Project/canonical，且不得删除或覆盖随后按冻结顺序写出的 Pure MIDI 用户 CC91/CC93。 |
| INV-086 | 主应用取得单实例所有权后自动回收上次异常退出遗留的 audio-cache session 与 Pure MIDI session backing directory；当前目录必须同时满足直接子项、版本 manifest 和活动锁已释放才可删除。`SessionContent` 旧裸 GUID 目录只在名称及 `mt_<positive id>.mpk` 内容结构均严格可识别时兼容回收。活动、未知、清单不匹配、越界或 reparse-point 路径必须保留；逐项删除失败不得阻止启动、新 Project 或新 session。 |
| INV-087 | Pure MIDI opaque SysEx 的唯一音频特权是可识别且校验有效的 Roland GS DT1 Part Mode 与 Yamaha XG Part Mode。导入必须按 payload target Channel 归属派生 Track；Compiler 保留原 opaque/SMF 数据并额外产生有类型、带来源和正式顺序的 canonical audio event，范围中途起播恢复 Root 当前活动连通区间内最近状态；音频投影重定向到 1-channel Unit channel 0 并以完整规范化 SysEx 发送，随后在同一顺序点显式建立 BASSMIDI Unit 的等价 Melodic/Percussion mode。任意其他 SysEx/Meta、Reset、无效校验和及 continuation 仍不进入音频后端。 |
## 22.2 常用主题定位
| 需要查找的主题 | 主要章节 |
|---|---|
| 软件定位、技术边界 | 第 1 章 |
| 术语、编号、身份、确定性 | 第 2 章 |
| Project、保存入口、修改状态 | 第 3 章 |
| tick、TPQ、Tempo、拍号、Marker | 第 4 章 |
| Port、Channel Unit、资源不足 | 第 5 章 |
| 程序级多 SF2/SFZ 列表、目标 Bank/Program 映射、无 Enabled SoundFont、BASS 直接读取与缓存身份 | 第 6、13、15、17 章 |
| Event Instrument 定义与内部索引 | 第 7、24 章 |
| SubVoice、Note/CC/RPN 等事件 | 第 8 章 |
| Logical Parameter、映射和 C# 函数 | 第 9 章 |
| Release、Loop、Envelope、Overlap | 第 10 章 |
| Logical Track、Logical Segment、裁剪与 Logical Note | 第 11 章 |
| CompileContext、资源分配、Compiled Result | 第 12 章 |
| 播放、预览、held Preview 因果 Gate、BASSMIDI、程序级 Playback Preferences、输出设备、采样率、buffer、Limiter | 第 9、12、13、17、20 章 |
| MIDI 文件结构与导出 | 第 14、23 章 |
| 普通 RIFF/WAVE、自定义采样率与离线渲染 | 第 15 章 |
| `.midora` package、schema、损坏与事务 | 第 16 章 |
| 主窗口、导航、对象所属属性编辑器和全局面板 | 第 17、24 章 |
| 各编辑器工作区、Timeline 精确属性与事务式 Properties | 第 17、18、20、24 章 |
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
