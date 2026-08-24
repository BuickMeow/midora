# Midora Software Requirements Specification — Initial Release Scope

> 中文引用名：**《Midora 软件需求规格说明书（初版范围）》**  
> 日常简称：**《Midora SRS》**  
> 规格版本：**v0.1**  
> 生成日期：**2026-07-15**  
> 最近修订日期：**2026-08-24**
> 文档形态：**按章节拆分的 Markdown 规格书**

## 文档定位

本规格定义 Midora 初版的产品范围、领域语义、编译与输出规则、持久化格式、UI 工作流、错误边界和明确不支持内容。它是初版实现、测试、评审和需求变更的产品基线。

本规格不固定最终 C# 类型、具体算法、第三方 API 调用、线程模型或控件实现；这些实现选择必须满足本规格的外部语义和不变量。

## 阅读顺序

首次阅读建议按章节顺序进行。开发中可通过第 22 章快速定位主题和跨系统不变量。

## 目录

1. [产品范围、定位与总体目标](01-Product-Scope-and-Positioning.md)
2. [系统模型、术语与符合性约定](02-System-Model-Terms-and-Conformance.md)
3. [Project 模型与应用生命周期](03-Project-Model-and-Application-Lifecycle.md)
4. [时间、Conductor Track 与全局音乐事件](04-Time-Conductor-and-Global-Musical-Events.md)
5. [Port、Channel 与资源模型](05-Port-Channel-and-Resource-Model.md)
6. [SoundFont 与声音资源](06-SoundFont-and-Sound-Resources.md)
7. [Event Instrument Library 与 Event Instrument 定义](07-Event-Instrument-Library-and-Definition.md)
8. [SubVoice 与 MIDI 事件编辑](08-SubVoice-and-MIDI-Event-Editing.md)
9. [曲线、Logical Parameter 与映射](09-Curves-Logical-Parameters-and-Mapping.md)
10. [实例生命周期、Loop、Envelope 与重叠](10-Instance-Lifecycle-Loop-Envelope-and-Overlap.md)
11. [Logical Track、Segment 与编曲语义](11-Logical-Tracks-Segments-and-Arrangement-Semantics.md)
12. [编译系统与 Canonical Compiled Result](12-Compilation-and-Canonical-Compiled-Result.md)
13. [播放与预览](13-Playback-and-Preview.md)
14. [MIDI 导出](14-MIDI-Export.md)
15. [音频文件渲染](15-Audio-File-Rendering.md)
16. [.midora 文件格式与持久化](16-Midora-File-Format-and-Persistence.md)
17. [UI 框架、导航与全局界面](17-UI-Framework-Navigation-and-Global-Surfaces.md)
18. [编辑工作区与编辑器](18-Editing-Workspaces-and-Editors.md)
19. [Project、文件、输出与任务工作流](19-Project-File-Output-and-Task-Workflows.md)
20. [通用交互、验证与 UI 验收边界](20-Common-Interaction-Validation-and-UI-Acceptance.md)
21. [初版范围边界、实现自由度与变更控制](21-Initial-Release-Scope-Boundaries-and-Change-Control.md)
22. [主题索引与跨系统不变量](22-Requirement-Locator-and-Cross-System-Invariants.md)
23. [Pure MIDI Track 与 Standard MIDI File 导入](23-Pure-MIDI-Tracks-and-SMF-Import.md)
24. [Arrangement 平铺轨道、共享执行组与概览渲染](24-Arrangement-Hierarchy-and-Preview.md)

## 文档版本规则

- **Initial Release Scope** 表示产品范围，不表示文档草稿序号。
- `v0.x` 表示整合和审查阶段；成为正式开发基线后可升级为 `v1.0`。
- 后续修订必须说明受影响章节，避免在实现中静默改变需求。

## 2026-08-24 修订摘要

- 程序级 SoundFont 列表扩展为本机 SF2/SFZ：每项保存 Enabled、原绝对路径和可选目标 Bank MSB/LSB/Program；SFZ 目标必填，SF2 可保持原映射。Midora 不解析、快照或监控 SFZ sample/include 依赖，直接把原 SFZ 路径交给 BASSMIDI。
- 目标映射使用 BASSMIDI `BASS_MIDI_FONTEX2`，三项必须整体出现且均为 0～127；配置、顺序与主文件元数据共同进入 sample-domain 缓存身份。SF2 继续使用 MMAP，SFZ 不使用只适用于 SF2 的 MMAP。
- New Project、Open Project、命令行打开、Open MIDI as New Project 和 Reset Playback Engine 在存在 Enabled SoundFont 时，必须在对应前台任务结束前接管或预热持久 Worker；不得把首次 Worker 创建推迟到 Play/Preview。Project 已成功切换后的预热失败保留 Project，并作为独立音频运行时错误报告。
- Application Preferences 的 SoundFont、实时音频或音频缓存配置变化在成功持久化后立即显示 `Saving Settings` 模态任务，销毁旧持久 Worker、直接加载 Enabled SF2/SFZ、探测设备并保留新 Worker供后续播放/预览复用；无 Project 时允许由后续 Project 会话接管预热后端。加载失败保持已保存设置并明确报告，不静默回退旧设置。
- 主菜单 `Help` 更名为 `Application`，`Application Preferences...` 从 `Edit` 移入该菜单；SoundFont 列表操作按钮移至列表顶部并采用较小的列表专用滚轮步进。
- SoundFont 破坏性脱离 Project：`.midora` 不再保存 SF2 字节、Embedded/External 引用或任何 SoundFont metadata，New/Open/Save/Save Copy 不访问 SF2。
- Application Preferences 改为有序 `{Enabled, absolute local .sf2/.sfz path, optional target}` 列表；所有实时播放、预览和离线渲染冻结同一 Enabled 顺序与映射，BASS Worker 直接打开原路径，不复制、不执行完整内容 hash 或预验证。
- SoundFont 列表变更只允许在 Stopped/Idle 提交，并重建持久音频 Worker、失效 sample-domain cache。缓存键使用有序路径与 length/last-write-time 小型元数据描述符；该描述符不是内容完整性验证。
- 本次为开发期格式破坏，旧 Project SoundFont 字段、settings 与内嵌资源不迁移、不兼容读取、不双写。受影响章节：2、3、6、8、12～23 及 ADR-CORE-047。

## 2026-08-23 修订摘要

- Arrangement 的 Event Instruments 管理栏为 Definition 项固定显示正式颜色竖线；该列表的选中态只改变背景，不得用通用红色左边框覆盖颜色语义。
- New Project Dialog 将 SoundFont 改为正式创建参数，明确提供 None、Embedded 与 External Relative；External 只在立即保存时开放。Application Preferences 的 Default Embedded SF2 以可见初始选择进入对话框，不再在关闭对话框后静默注入。
- Monitoring generation 的 `Seek` 下界必须持续作用于该 generation 后续追加的记录；Producer 从更早 rewind frame 渐进追赶时，Reader 静默跳过晚到但早于 audible frontier 的记录，禁止把它们重新送入 renderer。render-ahead fault 必须保留 source exception/invalid-result 原因并连同 renderer fault 上报。
- Arrangement Segment 概览的最高精度从错误的“每 Segment 固定 512 pixel”更正为 `96 pixels / quarter note`、256-pixel tile；较低精度只使用固定半八度 `1 / 2^(n/2)` LOD。Segment 长度、TPQN 与固定 LOD 决定 tile 数，精确 viewport zoom 不直接进入缓存身份。
- `Open MIDI as New Project` 的模态任务使用确定进度：第一遍以已解析源字节计量，第二遍显示已处理事件/总事件；成功兼容报告的完整 Info/Warning 文本必须与状态栏摘要共同保留，后续 `View` 仍显示首次报告全文。
- Arrangement Segment 概览在最多两个后台 worker 上为全部 Segment 预热完整且不超过四个 tile 的固定 LOD；缩放选择第一个不会向下采样 source pixel 的固定层，不产生任意 exact-scale cache generation，也不得遍历大量被压成亚像素的最高精度 tile。最终合成必须使用浮点目标宽度换算，并从一次 device-pixel-snapped 的完整 Segment 变换推导全部 tile 边界；禁止整数除法零宽和随 pan 改变最近邻采样相位。
- 当前 viewport 的 Segment 概览先原子发布同内容版本的完整粗略 fallback；该 fallback 比最多四个 tile 的后台预热层提高一倍水平分辨率，且自身最多八个 tile。fallback LOD 只由 Segment 长度与 TPQN 决定，不随 viewport 缩放改变；当前显示层更粗时允许临时缩放既有 fallback，禁止因缩小视图重新进入空白。全部可见 fallback 就绪后才启动新的当前 LOD 细化；目标 tile 就绪后独占其横向范围，禁止继续在该范围下绘制 fallback，但粗缓存仍保留供其余范围和后续视图复用。
- Monitoring generation 切换时，event Reader 对 published generation 与 generation-local loaded counters 的读取必须与 `Seek` 使用同一 feeder lock，禁止把旧 generation 的 committed count 与新 generation 的 loaded count 混合。
- 对象 `Properties...` 的 `Ctrl+P` 入口正式纳入焦点敏感快捷键；快捷键必须直接调用命令核心，不得构造无 `RoutedEvent` 的事件参数再调用 UI 事件处理器。
- 所有带确认提交的自定义模态对话框统一使用同宽度的红色 Primary 确定按钮与普通取消按钮；除焦点控件自身消费 Enter 的情况外，Enter 执行确定、Escape 执行取消，不再由每个对话框各自定义不一致行为。每窗只允许一个 Escape Cancel target；标题栏关闭按钮执行显式取消，但不得再次注册 `IsCancel`。
- Conductor 下部事件列表默认占可用编辑区的一半，并允许在硬性最小/最大高度内双向调整；Arrangement Draw 模式下，只有未形成移动/Resize 的 Segment 单击才在 MouseUp 替换选择，实际拖动不得在 MouseDown 清空多选。

## 2026-08-22 修订摘要

- Arrangement shared block 的 Track 拖放预览必须与最终 drop 语义使用同一目标：exterior strip 始终把插入线固定在 block 的真实外边界，不得短暂显示在成员间隙；同组成员明确脱离时使用更粗的强调边界线。该变更只涉及会话 UI 反馈，不改变 global Track order、Usage/Root membership 或持久化模型。
- 删除主窗口 Global Inspector、Bottom Panel、Details/Tasks Tab 及其 View/菜单入口。Diagnostics 只保留独立 Workspace；一次只显示当前前台任务的模态表面，不保存可见 Task History。
- 所有对象精确属性统一迁移到所属 Workspace 的显式 `Properties...` 模态对话框：打开时冻结目标，所有控件只编辑 Draft，`OK` 以一个原子 Project command 提交、`Cancel` 全量丢弃。多选 Mixed 字段必须先显式启用统一值，并可逐字段恢复原始 Same/Mixed 状态；Logical/Pure MIDI Segment、Logical/Direct MIDI Note/Event 和跨类型 Segment 选择均纳入统一包装。
- Event Instrument 删除 Properties/Parameters Tab；结构栏对象以双击或右键 `Properties...` 打开事务式编辑器。Parameter Mapping 的 Source、SubVoice、Target kind 与适用 CC/RPN/NRPN 在同一对话框创建或编辑，不再拆成 Route 与 Properties 两步。
- UI 不显示 Stable ID 或内部引用编号；Broken/引用选择只用可识别名称、对象类型和显式修复动作。Logical Parameter Lane 固定为离散 Step 点集；Direct MIDI 编辑时 exact point 碰撞采用后来编辑者覆盖，exact Note start/key 碰撞丢弃后来对象，同时保留未经相关编辑的导入重复数据。

## 2026-08-21 修订摘要

- Logical Track 的普通 `Duplicate` 改为深拷贝 Track/Segment/内容并创建引用同一 Event Instrument Definition 的新独立 Usage；显式 `Duplicate and Share State` 才保留源 Usage。普通副本位于源 Shared Usage block 之后，共享副本位于源 Track 之后且留在 block 内；两者均以一次原子 Undo 创建全部新稳定 ID。
- Event Instruments pane 中的 Definition `Duplicate` 继续只深拷贝 Definition 及其内部对象，不复制 Track/Usage；Track/Usage 上下文不再提供 `Duplicate Instrument Only`。Logical Track 菜单增加可直接打开有效 Definition 的 `Edit Event Instrument...`，未绑定 Track 不执行该命令。
- 第 5、7、10、11、12 章中旧的“Logical Track / Event Instrument Binding”共享与 Overlap 边界统一改为 Event Instrument Usage：同一 Usage 可以跨多个 Logical Track 共享状态、Overlap 域和活动连通区间；不同 Usage 即使引用同一 Definition 也保持隔离。
- 任一共享 Fixed Root 成员 Track 的 `MIDI Route Settings...` 均可修改 Root 唯一的 Channel Mode；多成员时必须明确提示影响范围并确认，确认后一次性更新全部成员，不要求用户寻找独立的 `Shared MIDI Route Settings` 入口。
- Arrangement ruler 增加从 Conductor Marker 派生的只读标签投影；Segment horizontal overview 以真实 NoteOn/GateStart tick 和 non-Note event/parameter tick 绘制独立缓存线，不得把持续范围、页摘要跨度或空洞错误填满。
- 本次是 `v0.1` 未发布开发期规格的全面同步，不改变产品/SRS 版本，也不恢复已被第 24 章取代的旧 parent/child Arrangement 或旧开发格式兼容路径。

## 2026-08-20 修订摘要

- Arrangement 从可见 parent/child 树替换为 `Conductor + global mixed Track order`。Event Instrument Definition 独立有序保存；新增无名称 Event Instrument Usage 作为可被多条 Logical Track 共享的执行/状态/生命周期身份。Shared Usage 与 Auto Root 以连续大括号 block 表现，Fixed Root 作为 Track route 属性表现且 members 可分散。
- 所有 Usage/Root 必须非空；最后成员 Track 离开时 owner 在同一个 Undo 中自动删除。Event Instrument Definition 可以零 Usage，删除 Track 永不删除 Definition。Fixed Root 不再提供空 Root 创建/预留入口。
- Pure MIDI SMF 投影和同 Root 同 tick 顺序改用 global Arrangement Track order；普通 SMF 导入保持源 MTrk 顺序。Logical shared Usage 在未启用逐音符隔离时按跨 Track Segment 活动连通区间共享 Channel state 与 Unit，成员 Segment End 不做 Usage 级 reset。
- Application Preferences 增加可清空的 Default Embedded SoundFont 本机路径，只用于 `New Project` 与 `Open MIDI as New Project`。有效文件按 Embedded snapshot 流程复制、哈希和验证；路径不进入 `.midora`，启动时缺失自动清空，任务开始时缺失按未设置处理。
- 播放期间 `Project` 一级菜单和 `Project Settings` 保持可用，只禁用受编辑锁约束的 `New Event Instrument` / `New Logical Track` / `New Logical Track with Instrument...` / `New Raw MIDI Track...` 等对象创建命令；Status Bar 的 `Playing` 使用绿色文本。
- Arrangement 与共享 piano roll 的可见 Grid 固定为 Bar/分母拍子线，Ruler 显示一基小节号；Segment local tick 通过 Project offset 对齐完整 Time Signature Map，极端水平缩小时按 device-pixel 密度上限跳过不可辨识竖线。
- 2026-08-19 的 parent-row 遮罩与折叠 UI 已被本次平铺 Arrangement 取代；Track 行直接承载内容，Shared Usage / Auto Root 只以 header gutter 大括号和显式 drop target 表现，不再占空白时间线行。
- piano roll 纵向缩放固定为不小于 3 的整数 device pixels/key，Note 顶边与 Key 上分割线重合且总高度等于 Key 高度。Pure MIDI Segment 的 Overview 通过 page summary 聚合音符密度，Add Lane 使用分步目标选择器并支持全部 CC 0..127 的统一名称格式。

## 2026-08-19 修订摘要

- Exact Root/Unit PCM 命中现在必须在 source range query 前建立 demand schedule；完整命中范围跳过对应 MIDI page 查询、排序和 IPC，混合命中仅生产 miss owner。Monitoring 使 cache bypass 后，rolling event stream 必须从实际可听 frame 发布新的 append-only generation，Worker 显式 Seek 后才切换到该 suffix；旧 generation 在切换完成前仍保持可读，并为本次 playback generation 单调保持 synthesis。
- Pure MIDI content pack 增加局部有序的 NoteOn、NoteOff 与 Channel endpoint pages，以及中途起播使用的 Channel-state checkpoint/active-note 查询；窗口消费使用有界 k-way merge，不再反复扫描巨型 Segment 的历史 Note pages。本次为开发期破坏性 pack version 替换。
- Reusable PCM miss 改为把16,384-frame blocks直接顺序追加到generation journal；完整 generation 原子提交，未完成 journal 保持不可命中并由重整回收，删除完整 sparse spool 到 Pack 的二次 payload 复制。
- 播放 UI 每个timer tick冻结一次CurrentTick，Tempo使用有序数组与递增/二分索引且只在实际变化时通知；播放指针移入独立轻量overlay，不再因30 Hz指针更新使Timeline内容层完整重绘。
- SoundFont 策略保持持久 `BASS_MIDI_FONT_MMAP` 与只预载计划引用 Preset；不把完整 SF2 复制到私有内存，也不默认解码全部 `.mpk` 到 RAM。
- Pure MIDI 极端规模基线改为 out-of-core page pack：Direct Note/Event/opaque source、canonical execution/SMF projection 与音频 sample-domain event plan 均不得要求整 Project 连续数组或按对象逐项常驻；页同时受 record count 与 decoded byte count 双重限制，页缓存必须有界。
- `Open MIDI as New Project` 改为两遍流式读取与事务性 page-pack 构建，不得先把完整 `.mid`、全部 parsed events、全部配对集合和完整 imported Project 的第二份深拷贝同时驻留内存。开发期 `.midora` Pure MIDI Track 格式改为小型 protobuf 元数据加单 Track page pack，不提供旧开发格式兼容读取。
- Canonical 保持一个正式事件集和两个冻结投影，但物理表示允许共享 immutable pages、descriptor/index 与去重来源表；Full/Incremental 等价比较的是逻辑事件序列、投影、诊断与 fingerprint，不要求连续数组或对象布局相同。
- 实时播放的 rolling preparation 前移到 canonical→sample 事件计划和 IPC：启动只发布当前光标恢复状态与 Startup 2 s 窗口，随后按 Low 0.75 s / Target High 6 s 水位请求页面；远处 Segment/事件数量不得决定启动等待。IPC 以有界 page generation 传输，删除整项目 MDAP event-count/file-size 作为正常项目容量上限的做法。
- Pure MIDI Arrangement preview、Piano Roll、Velocity 与 Event Lane 必须通过 source page range query/LOD 聚合读取可见范围；不得为打开视图建立全 Segment `TimelineRenderItem[]`、全 ID dictionary 或全量 interval tree。编辑使用 immutable base pages + copy-on-write overlay，并只失效相交页/tile。
- rolling event reader 在单个committed窗口超过固定ring时必须公布排他的partial safe frontier，使renderer可推进至最后已装载record frame并分批排空同frame事件；不得因reader等待ring空间、renderer等待完整窗口而形成永久Buffering。
- 从Arrangement显式打开Segment时，若Edit Cursor位于该Segment的Project范围内，Logical/Pure MIDI Segment Editor统一映射到local tick并将该位置水平居中；仅切换已有Tab时仍保留原viewport。
- `Open MIDI as New Project` 在 detached candidate 验证前增加确定性导入兼容归一化：缺失 tick 0 Tempo / Time Signature 时分别补齐 120 BPM / 4/4，同 tick 重复 Tempo 按源 MTrk 与事件顺序使用后来者。
- SMF Track Name 缺失、trim 后为空或非严格 UTF-8 不再使整个导入失败；非法名称事件被丢弃，需要的 Pure MIDI Track 获得确定性回退名称。所有兼容处理只进入一次性、可复制的导入报告，不放松 Project 内部不变量或导出的严格 UTF-8 要求。

## 2026-08-18 修订摘要（其中 Arrangement 树、parent subtree Duplicate 与 `Duplicate Instrument Only` 已由 2026-08-20～21 修订取代）

- Arrangement 改为 `Conductor → 混排的 Event Instrument / MIDI Channel Root → 各自 child Track` 两级正式结构；删除 Project Panel、可见 Event Instrument Library Workspace、Library Folder、独立全局 Track/Root 顺序和 Unbound Logical Track。Event Instrument / Root 的混排顺序及 parent/child 关系进入 Project、Undo/Redo 和严格持久化索引。
- Event Instrument / Root 支持携带完整 subtree 的复制、剪切、粘贴与 Duplicate；Root 副本强制改为 Auto。Event Instrument 另提供 `Duplicate Instrument Only`；删除 non-empty parent 必须确认并原子级联 child。Logical Track 跨 Event Instrument 继续执行 rebind 影响审查，Pure MIDI Track 可跨 Root 移动。
- 父节点与 child Track 各自拥有独立运行期 Mute/Solo；父 Solo 激活时忽略 child Solo，父/child Mute 始终生效。Logical Note 与 Direct MIDI Note 允许只按共同字段跨类型复制；Direct NoteOff Velocity 在 Direct 数据链和 SMF 中继续保留。
- Pure MIDI Segment 概览增加独立缓存的 non-Note event 线层：event 线位于 Note 图形上层、统一 50% 透明度、至少 1 device pixel，并按值归一化高度。Conductor 第一行直接显示按类型着色、固定设备尺寸的圆点概览；两者都采用可视分块缓存、空间索引与局部失效，不以 WPF Control 堆对象。
- 增加 `MIDI Channel Root → Pure MIDI Track → Midi Segment` 正式模型。一个 Root 固定表达一个共享 Channel Unit、Channel-wide 状态、Melodic/Percussion 模式和 Root 活动连通区间；子 Segment End 只关闭自身 Note，Root 连通区间结束才执行 CC120 与最终 Reset。
- 增加 Root `Auto` / `Fixed(Port, Channel)` 路由。Fixed Root 先预留、非空 Auto Root 后按显式 Root 顺序低号分配、Logical/Event Instrument 分配必须绕开全部 Root Unit；Root Units 与 Logical 峰值合计仍受 256 Unit 上限约束。
- Pure MIDI Track 使用直接 MIDI Note 与完整 Channel Voice Event；允许 CC91 / CC93、Channel Mode、Poly Pressure 与 Channel Pressure。Event Instrument SubVoice 的创建/Mapping 面保持受限；BASSMIDI 继续启用 `NOFX`，因此 CC91 / CC93 保留到 canonical/SMF，但不产生 Midora Reverb/Chorus 听感。
- 增加 SMF Format 0 / 1、TPQN division 的 `Open MIDI as New Project`。导入支持 Running Status、单 MTrk 多 Channel、MIDI Port 中途变化和 opaque SysEx/Meta 保留；按 effective Port.Channel 拆为 Root/Track，不提供导入当前 Project、Format 2 或 SMPTE division。
- SMF Type 1 导出改为同时保存 Pure MIDI Track 拓扑与 Logical Unit 拓扑：每个 Pure MIDI Track 独立单 Channel MTrk 并保留名称、顺序和自身 EOT；Logical 内容继续一 Unit 一 MTrk。Root 精确往返使用可忽略的版本化 Midora Sequencer-Specific Meta，跨 MTrk 同 tick 顺序风险在导出阶段汇总 Warning。
- Canonical Compiled Result 增加 Unit execution projection 与 SMF Track projection；音频与缓存按 Root 合并，禁止同 Root 子 Track 分别合成后求和。`.midora` 新增 Root/Track protobuf 对象文件，作为开发期破坏性格式修订，不提供旧布局迁移或兼容读取。

## 2026-08-16 修订摘要

- SubVoice 的 Note Number / Velocity Mapping 保持强制共享目标；非 Note Event Mapping 与 Logical Parameter Mapping 改为可物理删除的可选 owner。非 Note 事件点继续存在时，缺少 Mapping 表示原始值直通，普通事件编辑不得静默重建已删除 Mapping。
- 非 Note 状态目标上的 Envelope Mapping 以最近原始事件值或有效 Initial State/default 为持有基值，并按实例/Release 的整数 tick 连续求值；非零 Release 的最后一个有效 tick 达到 End Value 后才进入 NoteOff/Reset。
- Loop Start 前开始并完整跨越 Loop End 的模板 Note 在循环中保持发声且不重触发；普通 Gate/Release/Tail 结束不发送 CC120，CC120 只用于 Segment End 等明确硬边界。
- 修正普通 instance 结束后的 SoundFont release：发声 Segment 的 Unit lane 从首次使用持续保留到 Segment End；普通 NoteOff 后的原生 release 继续进入 Segment PCM，只有 Segment End/消费者范围硬边界可以硬裁剪。自然编译范围按实际生成实例所属 Segment End 结束。
- 将普通 instance 的通用状态清理从生命周期结束移动到 lane 启用/非重叠复用起点：先按实际目标闭包建立 Reset Defaults，再应用 Initial State、tick 0 用户状态和 NoteOn；普通结束只执行精确 NoteOff，状态保持到下一次 lane 激活或 Segment/消费者硬边界。共享 lane 内仍重叠的后续 Gate 不重复重置。
- MIDI Track 0 的 Track Name 改为任务准备时冻结的 Project Name，空白防御性输入回退为 `Conductor`；`Conductor` 仍是 Track 0 的结构角色名称。

## 2026-08-15 修订摘要

- MIDI 导出事件 Track 的组织从 `Logical Track × Port` 改为严格的 `Channel Unit (Port + Channel) × 1 MIDI Track`。同一文件内一个实际有事件的 Unit 只出现一次，每个事件 Track 只含一个 Channel；按原始 Port→Channel 排序，Track Name 固定显示一基 `Port <P> / Channel <C>`。该变化用于兼容不支持单 Track 多 Channel 的 MIDI 编辑器，不改变 canonical 路由或事件语义。

## 2026-08-12 修订摘要

- Arrangement 新建 Project / 重置编辑器的默认可见 Grid 改为 `Bar`、Snap 操作粒度改为 `1/8`；Bar Grid 按完整 Time Signature Map 以主实线绘制小节边界、以更浅的低强调实线绘制分母拍内部边界。Segment/SubVoice 钢琴卷帘不采用该拍内辅助线增强。
- Arrangement Draw 空白放置改为按下并向右拖动确定 Segment 长度，单击使用默认长度；Arrangement 默认 Segment 长度固定为 `1 × TPQ`，相邻 Segment 仍按可用间隙缩短或拒绝。
- Arrangement Track Header 曾增加独立 hover / pressed、拖动重排与 Bind / Unbind；其中交互反馈继续有效，层级、绑定与拖放语义先由 2026-08-18 两级模型取代，并最终由 2026-08-20 的 global flat Track order + Usage/Root shared block 模型再次取代。
- 普通 Logical / Template Note 多选移动使用共同 pitch delta，并删除结果 pitch 越出 `0..127` 的个别 Note；移动与删除属于一个 Undo。Note `Ctrl+Drag` 复制仍使用整组共同 clamp，不生成部分副本。
- 正式 Compiler Diagnostic message 统一为英文；Error / Warning 计数在每次编译完成时同步刷新（其显示入口已于 2026-08-18 从删除的 Project Panel 收敛到 Status Bar）。非法 pitch 来源的诊断导航使用安全 lane 投影，不得使应用崩溃。
- Velocity 视图改为每个 Note 在 start tick 对应一根固定窄柱，柱宽不再表达 Note 长度；顶部使用较大的方形 onset marker，同 tick 多音按高 pitch 覆盖低 pitch。
- Velocity 自由绘制与直线插值手势在按住期间只显示轻量轨迹，不逐柱重绘或提交；松开时一次性计算、提交并刷新 tile。直接按住单柱或其 marker 上下拖动仍只调整该 Note，并且不显示轨迹。
- `Alt + Left Drag` 统一为强制替代手势：Draw 模式的 Segment / Logical Note / Template Note 无视边界命中并强制 Move，`Ctrl + Alt` 强制 Copy+Move；Velocity 无视柱体 direct hit 并强制自由轨迹。操作类型在 Pointer Down 时冻结，Alt 不再绕过 Snap；已消费的 Alt KeyUp 不再激活主菜单并恢复来源 Timeline 焦点，普通 Alt 与 `Alt+F4` 不变。Draw 模式悬停可直接编辑对象时始终显示低强调 transient 外轮廓，不失效 raster tile。Arrangement、Segment Piano Roll 与 SubVoice Piano Roll 的 Select 模式仍从单次左键按下点发起框选，不再以单独左键点击命中对象。

## 2026-08-10 修订摘要

- 明确 Arrangement、Segment Piano Roll 与 SubVoice Piano Roll 的 Draw / Select / Split / Erase 互斥工具状态、直接编辑边界、对象命中指针以及移动/Resize transient 预览；Select 不再直接移动、Resize 或双击创建 Segment / Note。
- 将无修饰键 `D` / `S` / `E` 固定为活动 Timeline Workspace 的 Draw / Select / Erase 快捷键，并明确文本、代码、ComboBox、菜单、Popup、内联编辑和 Modal 的焦点例外；其余单字母工具快捷键仍不注册。
- 补充 Grid / Snap 显示同步、ComboBox 可编辑文本与 Fluent 下拉图标居中、显式垂直 ScrollBar、Diagnostics 筛选框和 Segment 标题布局验收；固定 Arrangement / Segment Piano Roll / Velocity 的蓝灰色层级与 Velocity onset marker、Segment/SubVoice 真实黑白 Pitch Ruler 与逐八度 C 标签、空 Timeline 无覆盖卡片，以及可复制的 Status Error 详情入口。
- 明确 Arrangement Segment Note Preview 使用固定 MIDI pitch `0..127`、最小 1 px Note 高度、布局取整和按 Segment 稳定 ID/内容指纹复用的手工渲染缓存；tick 0 Note 不得遗漏。
- 明确 Velocity 普通点击、自由拖动、右键直线插值、选择集过滤和单柱顶部边缘调整语义；所有手势在按下时立即生效并保持一次手势一次 Undo。
- 明确 Piano Roll 与数值 Lane 的纵向视口边界、右侧滚动条、随平移更新的标尺和边界标签可见性；显式滚动条 Thumb 按实际可见范围计算。Segment 下部编辑区的显隐与高度属于 Project Session UI State，其分隔条在完整上部 Timeline 区域与下部编辑区之间调整高度。
- 明确 Draw 模式下 Arrangement Segment、Segment Logical Note 与 SubVoice Template Note 的主体 `Ctrl+Drag` 使用原子“复制并拖拽”语义；复制集使用共同 delta、生成新稳定 ID、成功后只选择副本并只形成一个 Undo，普通 `Ctrl+Click` 与边缘 Resize 语义不变。
- 补充 Timeline 工具栏、状态栏已读、选择边框、键位明暗、Disabled Ghost Button 和顶部 Transport 信息的 WPF 验收要求。

## 2026-08-08 修订摘要

- 解决整数 Project tick 与 `Bar:Beat:Tick` 在部分 TPQ/拍号组合下不可逆的冲突：开发期 v1 中每个 Time Signature 必须满足 `4 × TPQ % denominator == 0`。Domain 创建/编辑、语义验证和持久化统一拒绝不兼容组合，不创建 v2 或迁移器。
- Time Signature 变化 tick 立即成为新小节 Beat 1；若该 tick 不是旧拍号下的自然小节边界，旧小节被截断并产生 Warning。统一的 Project Time Signature Map 负责可逆 `Bar:Beat:Tick`、自然小节/拍网格和 Snap 基础。
- Q-NUI-034～042 固定五层缓存：canonical range、Segment/Unit fragment、Unit raw PCM、playback span、短 Render-Ahead ring。相同完整 key 的 exact replay 命中不得重复语义编译或 BASSMIDI 合成；缓存只在 Project session 内有效，不进入 `.midora`。
- underrun 在失败位置锁存，按自然小节准备连续恢复区间；小节中途为“当前剩余 + 下一完整小节”，小节起点为当前完整小节，并以播放终点和 16 个四分音符裁剪。完整区间准备好之前不得短块断续推进。
- 音频在 canonical 成功后按抽象 Unit 使用 1-channel BASSMIDI Stream 语义和有界 stream pool。Realtime/Offline Maximum Sample Voices per Unit Stream 分别保存，默认均由 750 改为 500。
- Application Preferences 增加 session 音频缓存 root（默认 `%LOCALAPPDATA%\Midora\AudioCache`）和 reusable byte quota（默认 16 GiB，可为 0）。Transient recovery spool 独立于 quota；spool/RAM 均不可用时受控 Stop。

## 2026-08-07 修订摘要

- 明确 Segment Editor 钢琴卷帘的两种初版交互预览：左侧 Pitch Ruler 琴键按住预览，以及放置单个 Logical Note 时的草稿音符预览。两者统一复用 held Preview 的因果 Gate、`Int64.MaxValue` 未结束哨兵和未渲染 frontier 生效规则；预览不写入 Project，预览不可用或失败不得阻止合法音符编辑提交。
- Project 内稳定 ID 的核心与分配器统一改为单个正 `long`，合法范围为 `1..long.MaxValue`；JSON 使用 canonical 十进制 integer，对象文件名使用无符号、无前导零的十进制 ASCII，protobuf 在各既有外层字段号上直接使用标量 `int64`。这是尚未冻结的开发期 v1 直接修订，不提供旧 128-bit 布局迁移器。
- C# Mapping 内部 ABI 升级为 v2：签名改为 `double Transform(double value, in MappingContextV2 context)`，`MappingStableIdV2` 只承载单个正 `long`；语言、引用面、确定性编译、collectible AssemblyLoadContext 和非 sandbox 边界保持不变。

## 2026-08-06 修订摘要

- Midora 初版产品定位固定为免费、开源、非商业软件，Midora 自有源代码采用根目录 `LICENSE` 中未经自定义修改的标准 MIT License，版权署名为 `Copyright (c) 2026 Midora contributors`；项目自身非商业不限制下游商业使用。该定位不把 BASS/BASSMIDI/BASSWASAPI 纳入 Midora 的开源许可证；正式发布仍须按实际主体、收入方式、平台、分发方式和届时有效条款执行许可核验并提供第三方声明。
- MIDI 导出的 Channel 10 melodic 初始化固定为：每个 Logical Channel 10 Unit MTrk 与 Melodic Channel 10 Pure MIDI MTrk 在相对 tick 0 按 GS→XG 写入两条 Normal Part SysEx；Percussion Root MTrk 不写。初始化使用固定默认设备编号，不发送任何 GS/XG/GM Reset，也不改写 canonical Bank/Program；Readme 必须说明不识别 vendor SysEx 的兼容边界。
- MIDI 导出与音频文件渲染统一使用确定性 Windows 安全文件名合法化和冲突检测；精确算法固定为 NFC、固定不安全字符集合、设备保留名前缀 `_`、255 UTF-16 code unit、text-element 安全截断、NFC + OrdinalIgnoreCase 冲突键和稳定 ` (n)` 后缀。任务开始前必须预览并冻结全部最终路径，源名称不被修改，已有目标不参与后缀分配且仍需明确覆盖授权。整曲、分 Track、逐 Port、Readme 与 MIDI Track Name 模板已经固定，多文件模式不自动增加嵌套目录。
- MIDI 导出兼容档固定为 SMF Type 1、显式 status（不使用 Running Status）、严格 UTF-8 文本 Meta、Track Name + MIDI Port 最小组合、CC0→CC32→Program、Time Signature `cc=24` / `bb=8`、Tempo 十进制换算后一次 `AwayFromZero`，并禁止导出器在 canonical 之外追加 Channel 清理；Conductor/Logical Unit 使用统一 endTick，Pure MIDI Track 保留自身 EOT。SMF 导入单独支持合法 Running Status。
- 工程总耗时固定按 Project 成功打开后的完整会话时间累计，包含空闲、最小化、失焦、Buffering、导出和渲染；系统睡眠 / 休眠及关闭流程暂停。当前会话使用单调时钟，自动累计不单独标记 Project Modified，也不影响编译语义。
- Project 外部 SoundFont 固定为项目根目录或直属 `soundfonts/` 的相对 SF2；路径精确大小写优先、唯一 ignore-case 回退并警告、歧义拒绝。原始字节 SHA-256 只在用户明确绑定/接受时更新，被动变化不修改 Project；内嵌资源的 settings/manifest/hash 必须一致。
- 初版持久化兼容基线固定为 JSON Schema Draft 2020-12、内部版本化 `System.Text.Json` source-generated DTO、protobuf Edition 2024、Google.Protobuf 3.35.1 与 Grpc.Tools 2.83.0；严格拒绝重复/未知 JSON 属性和未知 protobuf tag，已发布 schema 以 descriptor/golden bytes 锁定。
- 持久化文本、相对路径、opaque sRGB 颜色、UTC 时间和工程总耗时的 v1 表示已固定；路径保留大小写与原 Unicode，不做 normalization，时间使用七位小数秒 UTC `Z` 格式，总耗时使用非负 int64 毫秒。
- 初版 C# Mapping 最初采用 ABI v1；该内部 ABI 已于 2026-08-07 因稳定 ID 改为单 `long` 而升级为 ABI v2，当前有效规则见同日修订摘要。版本化函数体、只读独立 MappingContext 契约、C# 14/`Microsoft.NETCore.App.Ref 10.0.10`，以及按 Project 当前源码修订管理的 collectible AssemblyLoadContext 缓存等边界保持不变。
- 初版产品发布架构固定为 `win-x64`；主应用、Native AOT 音频子进程和三项 BASS 原生库必须同为 x64，不发布 x86、Arm64 或 AnyCPU 正式产物。
- 初版正式原生基线固定为 BASS 2.4.18.3、BASSMIDI 2.4.16.0、BASSWASAPI 2.4.4.1 及三项 win-x64 DLL 的明确 SHA-256；仓库只保存 manifest，正式构建由操作员提供并校验二进制，vendor current/latest 只能生成开发候选。

## 2026-08-05 修订摘要

- 初版正式实时音频工作 block 固定为最多 `256 frames`；音频子进程内 Render-Ahead 使用单个有界 SPSC PCM ring，容量按 `ceil(actualSampleRate × RenderAheadMilliseconds / 1000)` 计算，不固定 ring block 数；实时 PCM 不跨进程。
- 初版 WASAPI 输出固定为 Shared Mode、event-driven、stereo interleaved float32；采样率采用端点初始化后的实际混音采样率，buffer 请求不等于实际值，period 与 callback frame 数由设备决定。
- 初版 Limiter 版本 1 固定为 stereo-linked sample-peak 算法：瞬时 attack、zero-look-ahead、线性 ceiling `1.0`、50 ms 单极指数 release；实时与离线输出共用逐样本状态语义。
- tick→sample frame 固定为完整 Tempo Map 的 decimal 区间积分乘采样率后只执行一次 `AwayFromZero`；实时播放、预览与音频渲染共用该语义，不得逐 Tempo 段取整。
- 连续值源离散化固定以每个整数 tick 的最终目标值为参考语义；canonical 输出首次有效值及后续整数变化值，任何跳跃求值或缓存优化都必须与逐 tick 参考结果完全一致。
- 整数目标参数默认使用 `Round / Away From Zero`，只在完整映射链最终输出时取整一次；取整与最终越界策略属于目标参数，不属于 Mapping Step。
- 新建 Event Instrument 的 Overlap 策略固定默认为 `Reject`；`Reject` 重叠产生 Error，`Warn` 重叠产生 Warning，并服从全局“强制 Warning 导致编译失败”策略而不改变诊断级别。
- 稳定 ID 文件布局最初采用 32 位小写十六进制文本和 protobuf high/low；该开发期布局已于 2026-08-07 在首版冻结前由单 `long` 契约直接取代，不构成已发布兼容承诺。
- 同一 tick 允许多个普通 Marker；它们不按名称或 tick 去重，以稳定 ID 区分，并在 canonical 结果中按稳定 ID 确定同 tick 顺序。
- 初版 Envelope Preset 统一为第 10.11 节规定的固定 ADSR-like 结构；第 18.6.5 节编辑器不得扩展为任意有序点或曲线段模型。
- `Channel Unit >= 248` 的诊断级别统一为 `Info`，不受“Warning 视为 Error”策略影响。
- Segment Split 必须为右侧 Segment 保留或生成必要参数起点状态，维持参数状态及相关曲线在分割前后的听感；不改变跨分割点 Logical Note 的提前结束规则。
- 正式 BASSMIDI 后端启用 `BASS_MIDI_NOFX`，初版不承诺 Reverb / Chorus 音频效果。Event Instrument/SubVoice 不允许创建或映射 CC91 / CC93；Pure MIDI Track 允许保留、编译和导出它们，音频投影确定性忽略其效果且不报错。
- 正式 BASSMIDI 后端启用 `BASS_MIDI_NOTEOFF1`；同 Port、Channel、pitch 的重叠 Note 实例按 FIFO 逐个释放，Cut Previous 的释放重叠与硬边界 Reset 必须保持精确 NoteOff 配对。
- 正式 BASSMIDI Stream 固定使用 8-point sinc 和 CPU 属性 `0`，并在 Preparing 预加载计划引用的 SF2 presets；Realtime/Offline Maximum Sample Voices per Unit Stream 分别配置，默认均为 `500`，同一任务所有 Unit Stream 使用同一冻结值。
- 实时播放跟随所选输出设备的实际采样率；音频文件渲染使用用户选择的 `8,000–192,000 Hz` 整数采样率。
- 音频文件输出改为普通 RIFF/WAVE、Stereo、Interleaved IEEE 32-bit Float；超过 RIFF 大小上限时在 Preparing 阶段失败。
- 增加启用输出设备枚举、可调 buffer、约 200 ms 性能基准、音频活动线程零托管分配和固定内部音频子进程要求。
- 性能选择在满足正确性、确定性和资源上限的前提下优先时间性能，可用受控内存换取速度。

## 文件命名规则

文件名前两位数字是稳定阅读顺序。章节内标题采用 `章.节.小节` 编号，可用于 Issue、ADR、测试和代码评审引用。
