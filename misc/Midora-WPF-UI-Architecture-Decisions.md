# Midora WPF UI Architecture Decisions

状态：Accepted for implementation
创建日期：2026-08-08
范围：正式 WPF UI，不改变 SRS、Domain、Compiler、Canonical Result 或消费者语义

## ADR-UI-001：生产应用与共享呈现程序集

- 决定：建立 `Midora.Desktop` 生产 WPF 可执行程序和 `Midora.Desktop.Presentation` 共享 WPF 类库；Style Gallery 改为消费共享类库，不再持有可分叉的主题副本。
- 原因：`AGENTS.md` §8 要求生产 UI 复用已批准的 Palette、ControlTemplates、icons 和 Chrome，不能长期维护平行 token。
- 边界：Style Gallery 不是生产依赖；Domain/Application 不引用 WPF。

## ADR-UI-002：呈现状态与业务状态分离

- 决定：`DesktopSession` 只组合当前 `ProjectDocumentSession`、运行协调器、Workspace Session State、Application Preferences 和 Transient Interaction；Project 修改只能通过现有 `IProjectEditCommand` 执行。
- 原因：SRS 17.2、20.19 要求四类状态所有权和统一 Project History。
- 结果：ViewModel/Presenter 不直接把 Selection、zoom、Draft、Mute/Solo 或 Task History 写入 Domain。

## ADR-UI-003：渲染型时间线

- 决定：Arrangement、Piano Roll、SubVoice/Logical Parameter/Conductor 事件视图采用少量 `FrameworkElement` 自绘表面；每个表面通过 `OnRender(DrawingContext)` 绘制，不为每个数据对象创建 `Control`、`Shape`、Binding 或 RoutedEvent handler。
- 原因：用户明确要求应对大量 Note/Event；WPF 大量视觉树对象会放大 measure/arrange、binding、input 和 GC 成本。
- 结果：标准控件仅用于 Toolbar、字段、列表外壳、ScrollBar 和可访问的主要命令；海量音乐对象是绘制 primitive。

## ADR-UI-004：不可变渲染快照与 revision 门

- 决定：UI 线程从正式模型构建按稳定 ID 排序的不可变 `TimelineRenderSnapshot`；snapshot 携带 Project semantic revision 和 workspace projection key。后台可准备纯数据索引，但只在 UI 线程按 revision 原子替换。
- 原因：避免绘制/命中过程中枚举正在改变的集合，并保证命中目标与提交 revision 一致。
- 失败语义：revision 不匹配时丢弃旧结果；手势提交前重新验证目标，不进行部分提交。

## ADR-UI-005：分层索引、裁剪与命中

- 决定：时间轴对象按 lane/pitch 分桶，桶内按 start tick 排序并建立分层 maximum-end 区间索引；viewport query 使用 start-tick 二分上界和 maximum-end 子树裁剪，hit test 只查询指针邻域。密集重叠按 z-order 与稳定 ID 产生确定顺序。
- 原因：兼顾长对象跨入 viewport 与大量短对象；避免每帧扫描全 Project。
- 限制：结构改变时重建受影响桶；小型快照允许线性路径，但公开行为和排序相同。

## ADR-UI-006：绘图资源与失效策略

- 决定：冻结并复用 Brushes/Pens/Geometries；文本使用按 DPI/font/text key 的有界 `FormattedText` cache；只在 model、viewport、theme、selection 或 transient overlay 改变时 `InvalidateVisual`。
- 决定：静态 grid/content/overlay 分层缓存；播放 cursor 等高频 overlay 不强制重建静态 content snapshot。
- 限制：所有 cache 有显式容量和清空条件；不把缓存放入 Project 或 Application Preferences。

## ADR-UI-007：输入手势状态机

- 决定：所有自绘表面复用明确状态机：Idle、Pointing、Dragging、Marquee、Drawing、Resizing、Panning、ContextTarget；Pointer capture、Escape、deactivation 和 revision 变化都有确定取消路径。
- 决定：拖动期间只更新 transient preview；Pointer Up 后通过 Application command 做一次原子提交。Snap 以 Primary Selection 计算一个 shared delta。
- 原因：落实 SRS 20.1、20.3～20.5 的一次手势一次 Undo 和无 partial success。

## ADR-UI-008：线程与任务边界

- 决定：WPF visual tree、Workspace 状态以及由 WPF 直接发起的 `ProjectDocumentSession.Execute/Undo/Redo` 只由 Dispatcher UI 线程访问。文件、编译、输出和可安全索引构建使用现有异步 coordinator；SoundFont 等 Application coordinator 可以在后台完成验证后通过其既有原子命令提交 Project，但不得直接刷新 WPF 投影。进度通过不可变 snapshot 回到 Dispatcher。
- 决定：`HistoryChanged`、`CompilationChanged` 和 playback state 通知不得在发布事件的同步调用栈内重建 WPF 投影。正式 WPF 会话只识别 `DispatcherSynchronizationContext`，统一把 collection/property refresh 排入该 Dispatcher；非 WPF 测试上下文不冒充 UI Dispatcher。SoundFont 等后台流程即使在 `ConfigureAwait(false)` 后原子提交 Project edit，也只能通过该队列刷新 UI。
- 决定：后台状态更新不得主动移动焦点、Selection 或 scroll；只有显式用户导航命令可以。
- 原因：避免跨线程模型变更、Project transaction 内布局重入和焦点竞争；保持现有 task/lock 语义。

## ADR-UI-009：主题、窗口 Chrome 与语言

- 决定：生产应用直接使用已批准的黑红暗色共享资源、Fluent System Icons geometry 和 10×10 caption geometry；caption 使用方形按钮、Arrow cursor、layout rounding 和最大化无外框策略。
- 决定：共享 `Button.Caption` style 必须设置 `WindowChrome.IsHitTestVisibleInChrome=True`。所有自定义 owned dialog 的标题栏按钮由这一共享契约成为可交互区域，Close 继续路由到安全 Cancel。
- 决定：初版所有内置 UI 文案固定 English；本仓库开发文档和交付说明继续使用简体中文。
- 原因：分别来自已批准视觉基线与 SRS 20.13.1。

## ADR-UI-010：可测试的 Presentation Core

- 决定：viewport math、interval index、selection、snap、workspace identity、command availability 和 lock routing 使用不依赖 live WPF Window 的纯 C# 类型；WPF controls 只做输入适配和绘制。
- 原因：使确定性、规模、边界和失败语义能在 CI 自动验证；实际窗口截图只承担视觉验收，不能替代逻辑测试。

## ADR-UI-011：C# Mapping Draft 复用正式编译配置

- 决定：由 `Midora.Compiler.CSharpMappingDraftCompiler` 暴露只验证未应用 Draft 的窄接口；内部直接复用正式 `CSharpMappingCompiler`，不得在 WPF 端另配 Roslyn、引用集、ABI、语言版本或缓存键。
- 边界：Draft 编译不修改 Project、不进入 History、不替换 Applied Version，也不是 canonical consumer 输入；Apply 仍通过 `ProjectDomainEditCommands.UpdateMappingFunction` 做一次原子 Project 编辑，并触发正式编译。
- 原因：SRS 18.5.4、20.11.7 要求 Draft 与 Applied Version 分离，同时禁止 UI 重建一套 Mapping 语义。若在 Desktop 复制 Roslyn 配置，会产生与正式编译不一致的接受结果。

## ADR-UI-012：Conductor 概览只读投影

- 决定：Arrangement ruler 使用独立 `RulerSnapshot` 投影 Tempo、Time Signature、Key、Marker 和 End Marker；它与 Arrangement Segment snapshot 分离，全部条目标记为不可命中、不可编辑。
- 决定：同 tick 的多个 Conductor 事件按正式类型顺序稳定合并为一个视觉标签，避免标签覆盖；编辑仍只在 Conductor Workspace 发生。
- 原因：SRS 18.1 要求 Arrangement 提供 Conductor 概览，但 UI 不得在概览中建立第二套编辑入口或改变同 tick 正式顺序。

## ADR-UI-013：Diagnostics 身份共享、过滤状态隔离

- 决定：Bottom compact Diagnostics 与完整 Diagnostics Workspace 共享同一 `DiagnosticRow` 身份；两者各自持有 Search/Severity/Status/Scope、Selection 和滚动状态。
- 原因：SRS 17.5、18.10、20.11 要求身份一致且后台刷新不抢焦点；共享集合视图会导致一个面板的过滤操作改变另一个面板。

## ADR-UI-014：Project Tree 与 Workspace Tab 的可靠实现边界

- 决定：Project Tree 保持浅层语义导航树，不对 `TreeViewItem` 启用 WPF hierarchical virtualization；大规模对象列表仍使用 recycling virtualization，海量音乐对象仍使用自绘。
- 决定：Workspace Tab header 使用水平像素滚动，选中内容直接绑定 `SelectedItem`；选中新 Workspace 后显式 `BringIntoView`。
- 原因：WPF 层级虚拟化在增量重建后会破坏子节点实现和 UI Automation；Project Tree 只有五个固定根和有限顶层对象，不是完整对象图。Tab 的逻辑滚动会让新选中的容器停留在未布局状态。

## ADR-UI-015：SubVoice 双渲染表面与预览会话状态

- 决定：一个 Active SubVoice 使用共享时间视口的两个自绘表面：128 音高 Note piano roll，以及 CC/Pitch Bend 连续层和 Program/Bank/RPN/NRPN/Pitch Bend Range 离散事件层；overview 合并两者只用于导航。
- 决定：Initial State 是独立视图，不投影到 tick 0；Program 在 UI 显示为 1～128，正式模型仍保存 0～127。
- 决定：Preview 展开状态、Full Instrument/Selected SubVoice 模式、Mute 和 Solo Selected 全部属于 Workspace Session State；它们只改变 preview request，不修改 Project、canonical result、导出或渲染。
- 原因：SRS 18.3～18.4 明确要求独立 Note piano roll、Timeline/Initial State 分离和可折叠 Preview；用户明确要求逻辑层和 SubVoice 层都不得堆逐事件 WPF 控件。

## ADR-UI-016：主窗口关闭请求异步门与非重入关闭

- 决定：第一次 WPF `Closing` 事件始终取消当前关闭，串行执行 Stop、Draft 与 Save Guard；确认成功后只把第二次 `Close()` 排入 Dispatcher，不在仍处于 `Closing` 调用栈时重入窗口关闭。第二次事件由已批准标志放行。
- 决定：关闭 Guard 执行期间拒绝并发关闭请求；退出流程中的异常在窗口仍可用时显示并保留应用，不允许从 `async void` 事件处理器逸出为进程级未处理异常。
- 原因：SRS 19.2.2、19.2.5 要求标题栏关闭、File > Exit 和 Alt+F4 共用 Stop/Draft/Save Guard；WPF 禁止在 `Closing` 事件仍执行时再次调用 `Close()`。即使一个返回 `Task` 的 Guard 同步完成，也必须遵守此非重入边界。
- 边界：该门只协调 WPF 窗口生命周期，不改变 Project 关闭语义、保存事务、播放 cleanup 或 Application Preferences 的所有权。

## ADR-UI-017：输入路由完成后再做视觉树结构变更

- 决定：来自 Popup、Menu、Tree/List 双击或 Enter 的创建与 Workspace 导航，先结束当前 transient interaction；Popup 必须先关闭，然后把结构性 edit/navigation 排入 Dispatcher。Project/Compiler 的同步通知同样不在当前 routed input 调用栈内重建绑定集合。
- 决定：禁止通过投递不改变尺寸的 `WM_SIZE`、同步强制 `UpdateLayout` 或其他伪造窗口消息来“提交” WPF container layout。
- 原因：Popup 使用独立 HWND；在其 Click 路由或 TreeView 双击路由尚未退栈时清空/重建 ItemsSource、Tab 或 Workspace，会把 mouse capture、selection 和 measure/arrange 带入重入状态，表现为整个主窗口不再处理输入。
- 边界：排队只改变 UI 提交时机，不改变 Project command 的原子性、History 顺序、稳定 ID、编译输入或持久化结果。

## ADR-UI-018：分层栅格缓存、离散 LOD 与独立命中

- 决定：Arrangement 的 Segment Note Preview 不再在每帧逐 Note 调用 `DrawingContext`。每个 Segment 以稳定 ID、preview 内容指纹、调色板 revision 和 DPI 为键，生成一张固定分辨率的冻结 `BitmapSource`；缩放、平移、Selection、hover 和播放指针只拉伸或复用该位图，不重建 preview 内容。
- 坐标不变量：Arrangement preview 始终映射到完整、未按 viewport 裁剪的 Segment 世界矩形，再由可视 Segment 矩形裁剪；平移只能平移该目标矩形，禁止把缓存图像重新拉伸到当前可见切片。piano tile 的 LOD/tile 坐标必须按值绑定到同一个 raster request、cache key 和屏幕目标矩形，后台任务不得捕获随后变化的循环变量。
- 决定：Segment 与 SubVoice 的 piano roll 共用二维 tile renderer。基础 Note 层以 `256 × 256` device-pixel、Pbgra32 tile 缓存；grid/active range、Selection/primary、hover/drag 和 cursor 保持独立覆盖层。平移复用相同 LOD tile，缩放切换量化的水平/垂直 LOD；小于一个 device pixel 的 Note 以覆盖像素聚合，而命中仍使用原始 Note 区间。
- 决定：tile 只从不可变 `TimelineRenderSnapshot` 和按 lane/pitch 分桶的 interval index 读取。每个 tile 使用忽略 Selection/Primary 的局部视觉内容指纹；编辑一个 Note 只轮换与该 Note 相交的 tile key，其他 tile 跨 workspace revision 复用。后台最多两个 raster worker，不同 in-flight raster key 最多 64 个；结果携带 projection/tile-content generation，过期结果不得替换当前画面。UI 只在 tile 完成后原子接收冻结 bitmap。
- 决定：Selection 使用独立、按稳定 ID 查询的不可变 presentation snapshot。单纯 Selection/Primary 变化不得重建 Note 基础 snapshot、interval index、Segment preview 或 piano-roll tile；精确 selection border、selected fill 和 transient gesture 在前景层绘制。
- 决定：共享 UI raster cache 的内存预算为 `256 MiB`，使用 LRU 回收；可视 tile 外最多预取一圈。缓存只属于当前进程和 Project session，Project 关闭/替换时整体清空，不写磁盘、不进入 `.midora`、Undo/Redo、Project fingerprint 或 Application Preferences。
- 失败语义：后台 raster 异常记录为 UI runtime trace 并丢弃对应 tile；不得修改 Project、阻塞输入或让过期 bitmap 覆盖新 revision。tile 未就绪只允许暂时显示静态背景与已就绪覆盖层，不允许回退到每帧逐 Note 绘制。
- 边界：本阶段使用纯 WPF `BitmapSource` 与 CPU 像素栅格，不引入 SkiaSharp、D3DImage 或自建 Direct3D surface。raster backend 保持可替换；只有基准证明纯 WPF 路径仍不能满足正式性能门时，才另行评估 native/GPU backend。

## ADR-UI-019：带保护区的瓦片、批量选择层与定向工作区刷新

- 决定：Piano Roll 的每个 `256 × 256` 核心瓦片在四边各增加 1 device-pixel 保护区；相邻瓦片按相同世界坐标重复栅格化保护区并重叠组合。被瓦片边缘截断的 Note 不生成伪边框，只有 Note 的真实起点、终点和上下边缘生成轮廓，避免瓦片缝隙及长 Note 内部的人工分界线。
- 决定：Piano Selection 使用独立的 selection-revision 瓦片层；Velocity 的普通与选中柱状统一进入横向瓦片层。Primary、drag、正在编辑的 Velocity 值和 cursor 仍是小规模 transient overlay。大选区不得退回逐 Note WPF primitive 路径。
- 决定：框选的视觉矩形与最终 interval-index 查询必须共享同一份吸附后 tick/lane 边界；小于半个 operation step 的正向拖动至少覆盖一个完整有效 operation step，不得退化成 1 tick。一次框选通过批量 selection mutation 只推进一次 selection revision。
- 决定：`ProjectDocumentSession` 分离 History 状态通知与携带 `ProjectChangeSet` 的内容通知。Desktop 只重建受 Track、Event Instrument 或 Conductor 变更影响的 Workspace；保存点等纯 History 变化只刷新状态属性。编辑一个 Track 不得重建其他 Track 的已打开 Segment/SubVoice 大型快照。
- 边界：正式编译仍保持现有同步、原子和 canonical 结果语义；本决定不把 UI 响应速度问题转化为延迟编译或未验证 Project 状态。

## ADR-UI-020：最终设备像素坐标栅格化，不再缩放 Piano tile

- 修正 ADR-UI-018 的离散 LOD 部分：Piano Roll tile 仍为 `256 × 256` device-pixel 核心和四边 1 device-pixel 保护区，但 tile 必须按当前实际 `devicePixelsPerTick` 与 `devicePixelsPerLane` 生成，并以该精确缩放的 IEEE 754 bit pattern 作为 cache key。WPF 组合阶段只允许 1:1 device-pixel 映射，不再把量化 LOD bitmap 二次放大或缩小。
- 同一 tick 的左右边界必须由同一表达式直接换算并执行一次最近像素舍入；禁止用 `floor(start)` 与 `ceil(end)` 两套方向相反的规则，也禁止用“已舍入 start + width”推导 end。相邻 Note 的共享 tick 因而得到完全相同的像素边界。
- 同一 pitch lane 的 top/bottom 必须从全局 lane 边界计算并舍入，再换算到 tile 局部坐标；不得按每个 tile 单独缩放已栅格化的行，从而避免横向 tile 之间发生 1 device-pixel 的纵向相位差。
- Arrangement Segment preview 继续使用固定 `512 × 64` bitmap，但不再增加并拉伸透明 gutter。normalized start/end 使用相同的最近像素边界规则，bitmap 精确映射到完整 Segment 世界矩形后裁剪。单个 Note 在源 bitmap 中覆盖相邻两行，以避免 `64 px` 预览缩小到常规轨道高度时，最近邻采样完整跳过只有一行的首音符。
- 依据：实机复现确认离散 LOD bitmap 的 WPF 二次采样会让 1-pixel border 在特定缩放下坍缩，并让相邻 tile 出现不同采样相位。该修正只改变 UI runtime cache 与像素覆盖，不改变 Note/Segment 语义、命中、编辑、持久化或可听结果。
- 后续边界：此实现吸收了高性能 MIDI 编辑器常见的“语义实例 + 统一最终像素变换”原则，但没有复制或引入 yinhe 的 AGPL 源码，仓库许可证因此不变。若将来改用 GPU instance renderer，需另立 ADR、性能门和许可证审计。

## 小决定审计

以下均是局部、可替换且不改变可听结果/持久化/公共业务接口的小决定，按用户授权采用推荐方案：

- 自绘入口使用 WPF `FrameworkElement.OnRender`，不引入第三方 UI/图形框架。
- 时间坐标内部使用 `long tick`；像素换算使用 `double`，提交前回到 checked integer tick。
- interval index 采用 lane buckets + sorted arrays + hierarchical maximum-end tree，不采用 R-tree。
- 高性能列表使用 WPF recycling virtualization；只有时间线内容使用专用自绘。
- UI tests 使用独立 `Midora.Desktop.Presentation.Tests`，窗口 smoke/截图测试与纯逻辑测试分层。

若后续发现必须改变大范围工作流、公开业务接口、并发模型、持久化或可听结果，暂停受影响分支并另行登记决定问题；独立 UI 工作继续。
