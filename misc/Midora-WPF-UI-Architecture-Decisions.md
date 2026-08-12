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
- 决定：Segment / Note 的 `Alt` 强制 Move、`Ctrl+Alt` 强制 Copy+Move，以及 Velocity 的 `Alt` 强制轨迹均在 Pointer Down 时解析并冻结；拖动途中修饰键变化不改变操作类型。Alt 不再承担临时绕过 Snap 的语义。
- 决定：只有 Timeline 已消费 Alt 强制手势时，主窗口才锁存来源 surface，并在对应 Alt KeyUp 的 preview 阶段阻止主菜单访问模式、随后恢复来源焦点；`Alt+F4`、普通 Alt 和窗口失焦不共享该锁存。Draw hover 外轮廓始终属于单对象 transient overlay，不进入或失效 raster cache。
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
- 决定：框选的视觉矩形与最终 interval-index 查询必须共享同一份吸附后 tick/lane 边界。时间范围按拖动方向解析：Pointer Down 锚点独立吸附并固定，只吸附移动端；左右方向不足一个 operation step 时均向各自拖动方向覆盖一个完整有效 operation step，不得用吸附后的起点加吸附长度反算固定锚点。一次框选通过批量 selection mutation 只推进一次 selection revision。
- 决定：`ProjectDocumentSession` 分离 History 状态通知与携带 `ProjectChangeSet` 的内容通知。Desktop 只重建受 Track、Event Instrument 或 Conductor 变更影响的 Workspace；保存点等纯 History 变化只刷新状态属性。编辑一个 Track 不得重建其他 Track 的已打开 Segment/SubVoice 大型快照。
- 边界：正式编译仍保持现有同步、原子和 canonical 结果语义；本决定不把 UI 响应速度问题转化为延迟编译或未验证 Project 状态。

## ADR-UI-020：最终设备像素坐标栅格化，不再缩放 Piano tile

- 修正 ADR-UI-018 的离散 LOD 部分：Piano Roll tile 仍为 `256 × 256` device-pixel 核心和四边 1 device-pixel 保护区，但 tile 必须按当前实际 `devicePixelsPerTick` 与 `devicePixelsPerLane` 生成，并以该精确缩放的 IEEE 754 bit pattern 作为 cache key。WPF 组合阶段只允许 1:1 device-pixel 映射，不再把量化 LOD bitmap 二次放大或缩小。
- 同一 tick 的左右边界必须由同一表达式直接换算并执行一次最近像素舍入；禁止用 `floor(start)` 与 `ceil(end)` 两套方向相反的规则，也禁止用“已舍入 start + width”推导 end。相邻 Note 的共享 tick 因而得到完全相同的像素边界。
- 同一 pitch lane 的 top/bottom 必须从全局 lane 边界计算并舍入，再换算到 tile 局部坐标；不得按每个 tile 单独缩放已栅格化的行，从而避免横向 tile 之间发生 1 device-pixel 的纵向相位差。
- Arrangement Segment preview 继续使用固定 `512 × 64` bitmap，但不再增加并拉伸透明 gutter。normalized start/end 使用相同的最近像素边界规则，bitmap 精确映射到完整 Segment 世界矩形后裁剪。单个 Note 在源 bitmap 中覆盖相邻两行，以避免 `64 px` 预览缩小到常规轨道高度时，最近邻采样完整跳过只有一行的首音符。
- 依据：实机复现确认离散 LOD bitmap 的 WPF 二次采样会让 1-pixel border 在特定缩放下坍缩，并让相邻 tile 出现不同采样相位。该修正只改变 UI runtime cache 与像素覆盖，不改变 Note/Segment 语义、命中、编辑、持久化或可听结果。
- 后续边界：此实现吸收了高性能 MIDI 编辑器常见的“语义实例 + 统一最终像素变换”原则，但没有复制或引入 yinhe 的 AGPL 源码，仓库许可证因此不变。若将来改用 GPU instance renderer，需另立 ADR、性能门和许可证审计。

## ADR-UI-021：Piano tile 完整帧保留与原子切换

- 决定：Segment/SubVoice Piano Roll 的 Note 层和缓存 Selection 层分别记录上一组“可视 tile 全部已完成”的 cache key。编辑或精确缩放导致当前可视集合存在未完成 tile 时，继续绘制上一完整集合；当前集合全部可用后一次性切换，禁止在同一过渡帧中混合空白块和零散的新块。
- 坐标：编辑时旧 tile 保持原比例；缩放时旧 tile 按其原始 device-pixel scale 反算世界 tick/lane 边界，再映射到当前 viewport。该临时映射只持续到精确比例的新 tile 全部完成；稳态仍严格执行 ADR-UI-020 的最终像素 1:1 组合。
- 资源：完整帧只保存 key，不复制 `BitmapSource` 或像素数组。只有全部 key 仍可从共享 LRU 取得时才使用旧帧；任何一项已回收即退回现有静态背景/当前已就绪块行为，因此不扩大 `256 MiB` raster cache 预算，也不阻塞 UI。
- 边界：grid、active range、cursor、primary outline 和 transient edit preview 始终使用当前状态；旧帧只是一层短暂视觉替身，不参与 hit test、Selection、Project、Undo/Redo、编译、播放或持久化。Arrangement preview 和 Velocity tile 不在本次改动范围。
- 依据：异步 tile key 在 Note 编辑和每个精确缩放级别都会轮换；原实现会在新 bitmap 完成前暴露背景，从而产生整块闪烁。保留旧完整集合能消除该空白窗口，同时不触碰 rasterizer、内容指纹、缓存键、后台 worker 或对象命中架构。

## ADR-UI-022：Velocity 固定柱与提交时栅格化

- 决定：Velocity 不再以 Note 的 `[startTick, endTick)` 画等长矩形。每个 Note 只在 start tick 投影当前横向 cache LOD 下固定 3-pixel 窄柱和 tile source 中 7-pixel 的方形 marker；pitch 写入 presentation Z key，同 tick 低 pitch 先画、高 pitch 后画，direct hit 使用相反顺序命中最上层。
- 手势：空白区域发起的左键自由绘制和右键直线插值在 capture 期间只保存、绘制指针轨迹，不查询并逐柱覆盖 Note。`Alt + Left Drag` 在 direct hit 之前强制选择自由轨迹路线，即使起点命中柱或 marker。MouseUp 才通过不可变 interval index 生成 stable-ID → velocity map，并调用既有批量命令形成一个 Undo。无 Alt 直接按住柱或 marker 时只维护一个 Note 的 transient value，不显示轨迹。
- 缓存：MouseMove 不改变 snapshot、Selection revision 或 Velocity tile key。批量命令提交后异步生成新 tile；新可视集合未完整前继续显示上一完整 Velocity frame，完整后原子切换。frame 只引用共享 LRU key，不复制像素。
- 原因：旧实现虽然只在 MouseUp 提交 Project，但每次 MouseMove 都枚举 `_velocityEdits` 并为所有已触及 Note 调用 WPF rectangle drawing；密集数据下覆盖层成本随手势长度持续增长。轨迹层把拖动期绘制成本改为只与鼠标采样点数相关。
- 边界：轨迹只是 transient UI state；取消或 capture 丢失不提交。最终 velocity、Selection 过滤、稳定 ID、Project command 原子性和 Undo 语义不变，Note 的 tick、length 与 pitch 不受影响。

## ADR-UI-023：Direct Timeline Select 优先框选

- 决定：Arrangement、Segment Piano Roll 与 SubVoice Piano Roll 的 Select 模式在单次左键按下时先于对象 hit test 进入 marquee capture；起点位于 Segment / Note 内部时也不发出 `ItemInvoked`，因此不再提供单对象点击选择。双击仍进入既有对象命中与导航路线。
- 原因：极端密集对象覆盖画布时，先命中对象会令用户无法从中间位置开始框选。Select 的明确职责改为区域选择；单对象选择仍可在 Draw 模式通过点击完成。
- 边界：有效 marquee 的集合运算固定为：无修饰键 Replace、Ctrl Add、Alt Remove、Ctrl+Alt Toggle；Shift 保留为 Add。任一修饰键路径都以现有选择为基础，空选区不改变选择；无修饰键的有效空选区执行 Replace 并清空原选择。小于 marquee 阈值的普通点击只设置 Edit Cursor，不改变 Object Selection。该决定不改变 Draw、Split、Erase、右键上下文命中、对象编辑、Project 数据或 Undo。

## ADR-UI-024：Arrangement 放置手势与 Track Header 直接操作

- 决定：Arrangement Draw 在空白区域按下时建立 transient Segment placement；未越过阈值使用 `1 × TPQ` 默认长度，向右拖动则按操作粒度改变结束 tick，MouseUp 只提交一次 `CreateSegment`。目标间隙不足时仍沿用新建 Segment 的可用间隙裁剪规则。
- 决定：Track Header 作为 Timeline 内容以外的独立命中区，维护 hover、pressed 和 reorder transient state；拖动完成只调用正式 `ReorderLogicalTrack`。上下文菜单调用既有 Rename、Bind、Delete 和 Reorder Project command，不另建 UI 业务模型。
- 决定：Arrangement snapshot 增加只读的 lane secondary label，投影绑定 Event Instrument 当前名称或明确的 Unbound / missing 状态。Event Instrument Library 的拖放 payload 只携带稳定 ID；drop 到 Track Header 调用正式 Bind command，覆盖不同绑定前确认。
- 边界：Track Header 的 hover、pressed、drag target 和菜单 target 不保存、不进入 Undo；正式 Track order、名称和 binding 仍只属于 Project Content。拖放不移动或复制 Event Instrument。

## ADR-UI-025：Note pitch 越界删除与损坏来源安全投影

- 决定：普通 Logical Note / Template Note 批量移动不再以选择集边界 clamp pitch。领域命令对全部 Note 应用同一请求 delta，删除结果 pitch 越出 `0..127` 的 Note，并移动其余 Note；两部分由一个 prepared command 原子 Apply / Undo，Undo 按原容器索引恢复被删除对象。
- 决定：复制拖动继续执行共同 pitch clamp，避免改变源对象或产生部分副本。时间负值、非法长度和 velocity 等其他无效结果仍在 mutation 前拒绝。
- 决定：Presentation 对已损坏 Project 中的非法 Note pitch 使用 `Math.Clamp(pitch, 0, 127)` 计算安全 lane，同时标记 `Invalid`；诊断导航先验证 Segment / Note 稳定 ID 仍存在，再构建 Selection 和 viewport。
- 原因：用户明确要求移动越界 Note 被丢弃而非存入非法 pitch；持久化损坏或旧缺陷留下的非法对象仍需可诊断、可定位且不能使 WPF projection 构造崩溃。
- 边界：该规则改变 Project 编辑结果但不改变 Compiler 对非法源数据的 Error，也不允许正式消费者接收非法 Note。删除可 Undo，不产生新稳定 ID。

## ADR-UI-026：Direct Timeline 边缘命中与 SubVoice Piano Roll 共用契约

- 决定：`TimelineViewport` 明确区分“定位 tick”和“包含 tick”。定位、Snap 与放置继续使用最近 tick；半开区间 hit test 使用对连续世界坐标向下取整的包含 tick。不得把四舍五入后的定位 tick 用作 `[startTick,endTick)` 内容归属，否则高缩放下每个 tick 的后半段会被错误归入右侧对象或空白。
- 决定：Arrangement Segment、Logical Note 与 Template Note 的 Draw 边缘命中先以固定 `5 DIP` 扩展 interval-index 候选，再在屏幕坐标中解析真实 Start/End 边缘；不得直接用半开区间 `[startTick,endTick)` 的零容差内容命中决定 Resize。共享边界左侧指向左对象 End、右侧指向右对象 Start；精确重合时依次优先 Primary、Selected、End。
- 决定：Direct Timeline 的 Select 单击仍先进入 marquee capture；Pointer Up 未达到框选阈值时发出背景定位并设置吸附后的 Edit Cursor，保留现有 Object Selection，不恢复单对象点击选择。
- 决定：Timeline 右键菜单提供 `Deselect All` 与 `Invert Selection`；前者清空当前 Workspace Selection，后者只 Toggle 当前 surface snapshot 中可命中的对象，并保留当前 surface 之外的既有选择。
- 决定：SubVoice Note Piano Roll 复用 Segment Piano Roll 的 `TimelineSurface` 交互与颜色路径：接入同一 `MarqueeCompleted`、Edit Cursor、Template 有效范围和蓝灰 Note palette；对象种类仍为 `TemplateNote`，编辑继续路由到正式 Template command。
- 原因：半开区间适合正式范围语义，但视觉 End 边缘本身位于区间外；高缩放时把连续坐标四舍五入成整数 tick，会让一个 tick 的后半段提前归入下一个 tick，形成边缘左侧的命中空洞。SubVoice 缺少事件/状态绑定则会使同一控件产生行为和颜色分叉。
- 边界：该决定只改变 UI hit resolution、session cursor 和 presentation binding；不改变 Project Note/Segment 范围、稳定 ID、Selection 数据模型、编译、播放、Undo/Redo 或持久化。

## ADR-UI-027：批量边缘调整采用共享请求量与逐对象长度饱和

- 决定：Arrangement Segment、Segment Logical Note 与 SubVoice Template Note 的批量边缘调整使用 Primary 对象吸附后得到的同一个请求 Edge Delta，但不再先按选择集中最短对象的剩余长度共同裁剪 delta。正式 Application command 对每个对象独立计算结果；缩短超过该对象可用长度时，仅该对象在 `1 tick` 处饱和，其他对象继续应用完整请求 delta。
- 决定：左边缘调整保持每个对象原右边缘不变，右边缘调整保持每个对象原左边缘不变。Segment 左边缘同时按实际应用量更新 `ContentOffsetTick`；时间非负、内容窗口合法、Segment 不重叠和整数溢出等硬约束仍在 mutation 前验证。任一结构性约束失败时整批拒绝，不产生部分修改。
- 原因：共同按最短对象裁剪会使一个短对象限制所有较长对象，无法表达用户请求的批量缩短量。共享请求量加逐对象最小长度饱和既保持非比例批量编辑语义，也使 `100 tick` 与 `20 tick` 对象共同缩短 `60 tick` 时确定地产生 `40 tick` 与 `1 tick`。
- 边界：一次手势仍只提交一个 Project command 和一个 Undo；不改变对象稳定 ID、编译/canonical 语义、持久化格式或 Snap 来源。该规则只对长度最小值做逐对象饱和，不把重叠、容器边界等结构性错误降级为部分成功。

## ADR-UI-028：Arrangement Bar Grid 的分母拍辅助线

- 决定：Arrangement 在可见 Grid 选择 `Bar` 时继续以实线绘制 Project Time Signature Map 的自然小节边界，并在每个小节内按当前拍号的 `TicksPerBeat` 绘制低强调虚线。拍号变化 tick 无论是否截断前一自然小节，都作为新小节的实线起点；旧小节只绘制变化点之前实际存在的拍边界。
- 决定：新建 Project 或重置编辑器时，Arrangement 默认 `Grid = Bar`、`Snap = 1/8` 且 Snap 开启。共享的 Piano Roll 设置继续保持既有默认值，Segment/SubVoice Piano Roll、Velocity 和 Event Lane 不增加 Bar 模式拍内虚线。
- 原因：只显示小节边界时，Arrangement 在 Bar Grid 下缺少拍级定位参照；直接复用正式 `ProjectTimeSignatureMap` 可正确覆盖 `3/4`、`6/8` 与变拍，而不在 UI 建立第二套时间语义。
- 边界：辅助线只是当前 viewport 的 transient 绘制，不进入 Project、Undo/Redo、`.midora`、编译或输出。初版仍不实现 additive meter、复合拍重音分组或钢琴卷帘同类增强。

## 小决定审计

以下均是局部、可替换且不改变可听结果/持久化/公共业务接口的小决定，按用户授权采用推荐方案：

- 自绘入口使用 WPF `FrameworkElement.OnRender`，不引入第三方 UI/图形框架。
- 时间坐标内部使用 `long tick`；像素换算使用 `double`，提交前回到 checked integer tick。
- interval index 采用 lane buckets + sorted arrays + hierarchical maximum-end tree，不采用 R-tree。
- 高性能列表使用 WPF recycling virtualization；只有时间线内容使用专用自绘。
- UI tests 使用独立 `Midora.Desktop.Presentation.Tests`，窗口 smoke/截图测试与纯逻辑测试分层。

若后续发现必须改变大范围工作流、公开业务接口、并发模型、持久化或可听结果，暂停受影响分支并另行登记决定问题；独立 UI 工作继续。
