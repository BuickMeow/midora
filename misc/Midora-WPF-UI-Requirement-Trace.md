# Midora WPF UI Requirement Trace

状态：既有正式实现基线；2026-08-20 第 24 章已破坏性修订为 flat Arrangement / Shared Usage；2026-08-22 已删除 Global Inspector，旧 Project Panel、Library tree、mixed parent hierarchy 与 Inspector 证据不再表示当前符合
创建日期：2026-08-08
适用范围：`src/midora-desktop/` 正式 WPF 主应用、共享呈现基础设施及 UI 自动验收

## 1. 权威来源

- `AGENTS.md`：仓库主线、初版范围、音频边界、验证门及已批准视觉基线。
- `misc/Midora-SRS-Initial-Release-v0.1/00-Table-of-Contents-and-Document-Control.md`：规格控制。
- `misc/Midora-SRS-Initial-Release-v0.1/22-Requirement-Locator-and-Cross-System-Invariants.md`：跨系统不变量。
- SRS 第 17、18、20、24 章：UI 框架、编辑器、通用交互、Arrangement 层级/概览和验收边界。
- SRS 第 3、13、19 章：Project 生命周期、播放/预览、文件和任务工作流。
- 其余 SRS 专项章节：UI 只呈现和编辑其正式模型，不另建业务语义。
- `misc/Midora-WPF-Style-Gallery-Requirement-Trace.md`：已通过人工视觉检查的黑红暗色主题和控件基线。

本文不是 SRS，不修改需求；它把正式 UI 的输入、输出、失败边界和验证证据映射到实现。

## 2. Requirement trace

| 范围 | 正式输入 | UI 输出/行为 | 主要依据 | 验收重点 |
|---|---|---|---|---|
| 应用生命周期 | 启动参数、单实例转发、Project create/open result | 单持久主窗口；无 Project 空状态；第二次启动转发 | 3.3；17.1；19.1～19.4；20.10.4 | 单实例、候选打开失败不替换当前 Project、关闭 guard |
| 主窗口 | 当前 Project、会话状态、任务状态 | Title、八个固定一级菜单、Command Bar、Workspace Tabs、Active Workspace、Status Bar；无 Project Panel、Global Inspector 或 Bottom Panel | 17.1、17.7、24.7；20.15 | 最小尺寸、最大化工作区、Toolbar overflow、Arrangement 常驻第一 Tab |
| 状态所有权 | Project、Preferences、Session、Transient | 四类状态分别存储；只有 Project edit 进入统一 History | 3.10～3.12；17.2；20.14 | `.midora` 无 UI 状态；Preference 失败不影响 Project |
| 命令路由 | 焦点、Selection、锁定级别、Modal | 同一命令服务于菜单、Toolbar、Context Menu 和快捷键；焦点优先 | 19.10；20.2、20.7、20.12 | 文本焦点不误删对象；Ctrl+S/Undo/Clipboard 按上下文路由 |
| Workspace | 类型或对象稳定 ID | 类型唯一、对象 ID 唯一、单行 Tab、会话内次序/焦点恢复 | 17.3 | 重复打开激活已有 Tab；对象删除自动关闭；Draft guard |
| 对象 Properties | 显式 Workspace/Selection 目标 | 固定目标模态 `Properties...`；Draft + OK/Cancel；多选 Mixed 显式统一/逐项还原；Diagnostics 使用独立 Workspace | 17.4；18.2～18.7；20.2～20.4 | 无全局自动跟随；一次 OK 一次 Undo；Cancel/失败不改变 Project；不显示内部 ID |
| Arrangement | Conductor、global mixed Tracks、Definition index、Usage/Root membership、Segments、runtime Shared/Track Mute/Solo | 常驻平铺自绘 timeline、Shared brace/Fixed route property、Definition 管理栏、Note preview、Pure MIDI event-on-note preview、Conductor point preview | 18.1；24；20.1、20.3～20.5 | global order 唯一；Event 在 Note 上层 50%；极端内容可视 tile/局部失效 |
| Segment Editor | Segment local notes/lanes、绑定 Instrument、Project Tempo context | 自绘 piano roll、Pitch Ruler、Logical Parameter lanes、crop 外弱化内容 | 13.22.7、13.24.5；18.2；20.1 | local/project time 不混淆；held preview 清理；预览失败不阻止合法放置 |
| SubVoice Editor | SubVoice template events、Initial State、effective Root Note | 自绘 note/event lanes；独立 Initial State；高级事件语义呈现 | 8；18.4 | 不展开 RPN/NRPN CC；空 lane 不持久化；大量事件可视裁剪 |
| Event Instrument | Instrument/Structure/Lifecycle/preview inputs | 一个对象 Workspace、Configurations/SubVoice 两个 Tab、470 DIP Structure Panel、折叠 Preview、虚拟键盘、结构对象事务式 Properties | 7～10；13.21～13.23；18.3～18.6 | Preview 走 canonical 管线；不暴露 Port/Channel/内部 ID；Incompatible 保留；视图宽度/滚动保留 |
| Mapping/Function | Logical Parameter、Mapping Chain、Applied source、Draft | 有序 Mapping 编辑、单值测试、独立代码草稿和 Draft 诊断 | 9；18.5；20.12.2～20.12.3 | Global Save 不 Apply；关闭/切换/退出处理 Draft |
| Conductor | Tempo/Time Signature/Key/Marker/End Marker | 自绘 lanes、同步事件列表、稳定 ID 密集 Marker 命中 | 4；18.7；20.13.3 | Tempo 离散；End Marker 特殊；不移动内容 tick |
| Settings/Preferences | Project settings、Application preferences、runtime device info | 七个 Project pages；独立 Preference pages；只读 Derived 数据 | 17.2；18.9；20.14 | 非法值不 clamp；设备/音频设置仅 Stopped 提交 |
| Diagnostics/Task | 正式 Diagnostics、当前前台 Task snapshot | 独立 Diagnostics Workspace；单一模态 Task overlay；无 Bottom Panel/Tasks history | 17.5；18.10；20.11 | 后台刷新不抢焦点/Selection；取消只在安全可取消阶段可用 |
| New/Open/Save | Application coordinators、Windows picker selection | Owned modal configuration/progress/result；失败原子 | 19.1～19.4 | Save 事务不可取消；Damaged 时禁用；Save Copy 不改当前状态 |
| MIDI Export | 冻结的 Export request/review/result | 配置、最终路径预览、进度、原子 Result | 14；19.5～19.6 | Mute/Solo 忽略；全部路径先冻结；覆盖授权一次完成 |
| Audio Render | 冻结的 Render request/review/result | 配置、RIFF 上限预检、完全锁定进度、逐 Track Result | 15；19.7～19.8 | Preparing 起 Level 4；已完成文件保留；当前临时文件清理 |
| 本地完整启动 | 当前源码、固定且已校验的 win-x64 BASS 基线 | 当前 Native AOT Worker 发布到 WPF 输出目录的 `audio-worker/` 后启动桌面端 | 6.5.2；13.30；22.10 | 不下载或猜选 DLL；错误版本/哈希、缺文件或 AOT 发布失败时不启动；部署产物只属构建输出 |
| 视觉系统 | 已批准 Style Gallery resources | 黑红暗色、低噪声、统一模板、Fluent icon、正确窗口 Chrome | `AGENTS.md` §8；Style Gallery trace | 无文本裁切；一致 focus/pressed；caption 10×10；Scrollbar 完整 |

## 3. 渲染型编辑器的输入与输出契约

适用于 Arrangement、Segment piano roll、Logical Parameter lanes、SubVoice note/event lanes、Conductor timeline、Lifecycle preview 和 overview。

输入：

- 当前 `ProjectDocumentSession.Project` 的只读投影；
- Workspace Session State 中的 viewport、zoom、scroll、tool、selection、cursor 和 filters；
- Application Preferences 中相应 workspace type 的 Grid/Snap 默认值；
- Runtime playback/preview cursor、lock、Mute/Solo 和诊断摘要；
- 暂时手势的 drag/marquee/draft preview。

正式输出：

- 一个有界绘图表面内的像素输出；
- 基于稳定 ID 的命中结果、Selection 和导航目标；
- 只在 Pointer Up/Enter 等提交边界构造的 `IProjectEditCommand`；
- Viewport/Selection 等 Session State 更新；
- Preview 请求只进入现有 `ApplicationTaskCoordinator`，不发裸 MIDI。

边界和失败条件：

- 只绘制与可视时间和 lane/pitch 范围相交的对象；不得为每个 Note、Point 或 Event 创建 WPF `Control`/`FrameworkElement`。
- Snapshot 与当前 Project revision 不一致时丢弃并重建，不允许把旧命中提交到新模型。
- 命中目标在提交前失效、跨 Track 约束冲突、值越界或锁定变化时整次手势失败；不部分提交、不自动修复。
- Pointer capture 丢失、Escape、Window deactivation、Project 切换和对象删除必须取消手势并清理 held preview。
- 渲染异常转为 Runtime UI diagnostic，保护主窗口；不得修改 Project 或吞掉业务命令失败。

## 4. 诊断、持久化和运行时归属

- Project 内容和正式诊断继续由 Domain/Application/Compiler/consumer 层定义；UI 不复制验证规则。
- 未提交字段错误和 Function Draft 错误属于 UI/Transient，不进入 Whole Project diagnostics。
- Window/layout/Grid/Snap/audio environment preferences 使用 `ApplicationPreferencesService` 及 UI 扩展 preferences；写失败显示 Notice。
- Workspace、Selection、zoom/scroll、搜索、cursor、Mute/Solo、Task History 和 Draft 只存在当前会话。
- 任何自绘缓存、formatted text、geometry、spatial index、bitmap 或 drawing snapshot 都是可丢弃的 UI runtime cache，不持久化、不进入 fingerprint。

## 5. 明确非目标

- 不实现 SRS 20.17 排除的多 Project、多主窗口、Docking、浮动 Workspace、Pause、Scrubbing、Autosave、跨 Project clipboard、全局搜索或非 100% DPI 正式验收。
- 不以 UI 便利为理由添加业务默认值、自动修复、按名称重绑、自动量化、自动寻找合法位置或 silent clamp。
- 不让 Style Gallery 成为生产依赖；它只消费与正式应用相同的共享主题和控件资源。
- 不在 UI 重建 Compiler、Playback、MIDI Export、Audio Render 或 Persistence 语义。

## 6. 当前实现证据（2026-08-08）

- `Midora.Desktop` 已实现单实例入口、Project create/open/save/close guard、主窗口固定区域、统一命令路由、Workspace identity/navigation、事务式对象 Properties、独立 Diagnostics、单前台 Task overlay、Preferences、MIDI Export 和 Audio Render owned workflows；生产主窗口不包含 Global Inspector、Bottom Panel、Details/Tasks Tab。
- `TimelineSurface`、`TimelineOverviewSurface`、`PianoKeyboardSurface` 和 `LifecyclePreviewSurface` 均为专用渲染控件；Arrangement、Segment、Logical Parameter、Conductor 与 SubVoice 编辑不创建逐 Note/Event WPF 控件。
- Timeline snapshot 使用 lane interval index、可视裁剪、稳定 ID hit test、overlap cycle、marquee、drag/resize/draw/erase/split、共享 delta、grid/snap、time range 和 revision 重建。
- Event Instrument 固定为 Configurations / SubVoice；Logical Parameter、Parameter Mapping、MIDI output mapping、Mapping Step、Envelope Preset 与 Mapping Function Preset 由左侧 Structure Panel 管理，双击或右键打开事务式 Properties；SubVoice Note 与事件层为共享时间视口的两个渲染表面；Initial State 与 tick 0 事件分离；Preview 可折叠且支持 Full Instrument / Selected SubVoice 会话模式。
- Diagnostics 只在独立 Workspace 呈现；主窗口不维护 Tasks history。Conductor overview 是 Arrangement ruler 的只读确定投影。
- 可见的外层对象导航统一位于常驻 Arrangement 与其 Event Instruments 管理栏；普通大列表使用 recycling virtualization；Workspace Tabs 支持重排、单行横向滚动、列表入口和新选中项实现。
- 实际 100% DPI 窗口检查已覆盖 1440×900、1100×720、最大化工作区、caption、Arrangement、Project Settings、Event Instrument Configurations/SubVoice 与 Diagnostics filters。Event Instrument 延迟实例化检查发现并修复了只读属性默认 TwoWay binding 导致的 XAML runtime crash。
- 主窗口 Exit Guard 使用串行关闭请求门；确认成功后的第二次 `Close()` 由 Dispatcher 延迟到首次 `Closing` 返回后执行，避免同步完成的 Guard 在 WPF 关闭调用栈内重入。退出流程异常保持窗口并显式报告。
- Release 实际进程关闭验收覆盖无 Project 与成功打开 QA Project 两种状态：两次标准 `WM_CLOSE` 均在 20 秒内以进程退出码 0 结束，检查时间窗内没有新增对应的 `.NET Runtime` 1026 或 `Application Error` 1000 事件。
- 项目“+”入口使用普通 ToggleButton/Popup/Button。2026-08-09 的用户回归推翻了此前“创建流程可用”的人工检查结论：原实现仍在 Popup `Click` 路由内同步重建绑定集合，并额外投递伪造 `WM_SIZE`，可导致输入捕获和布局重入。现实现改为先关闭 Popup，再把编辑排入 Dispatcher；不再使用伪造窗口消息。
- 当前 Release 验证：Desktop Presentation 16/16、Desktop session/workflow 13/13、Application 279/279、Compiler 252/252；Desktop solution 构建为 0 warning / 0 error。

本节只记录已实现和已验证事实；临时 QA `.midora` 文件位于系统临时目录，不属于仓库产物。

## 7. 验证门

1. Desktop solution Release build 为 0 warning / 0 error。
2. Presentation unit tests 覆盖 viewport transform、可视裁剪、空间命中、重叠 cycling、snap、selection、revision invalidation 和一次手势一次提交。
3. 大数据渲染基准至少覆盖 100,000 Notes、100,000 events 和 10,000 Segments；对象总量增加但可视对象不变时，绘制批次数不得随总量线性增长。
4. 热路径分配测试覆盖 viewport query、hit test 和稳定帧 snapshot reuse；渲染模型不创建逐对象 WPF controls。
5. 命令路由、锁定矩阵、Draft、Clipboard、Project switch guard、Preferences 失败和后台不抢焦点均有自动测试。
6. 100% DPI 实际窗口验收覆盖 Normal/Min/Max、任务栏工作区、最小尺寸、文本裁切、Scrollbars、caption、Toolbar overflow、各主要 Workspace 和 owned dialogs。
7. 实际启动 smoke test 不要求仓库包含 BASS DLL；无 SF2/无设备必须进入规格定义的合法空状态。

## 8. 2026-08-09 致命交互故障稳定化 trace

问题输入：

- （历史输入，已由第 24 章取代）Project Panel 或 Event Instrument Library 中的 Create Event Instrument、Create Folder、Create Logical Track；
- Project Tree 的双击/Enter/Context Menu 打开，包括 Conductor Track；
- Embedded / External Project SoundFont 选择及其后台复制、SHA-256、原生 Worker loadability 校验；
- 使用自定义 WindowChrome 的 owned dialogs 标题栏 Close；
- Compiler、Project History、Playback 和 SoundFont 后台路径发布的状态通知。

要求输出：

- 一次用户命令只产生一次正式 Project edit；创建结果按稳定 ID 进入正式模型、History 和对应 Workspace；第 24 章实施后外层投影只进入 global mixed Arrangement Track order、Definition index 与内部 Usage/Root membership；
- Popup/Menu/DoubleClick 当前输入路由先完成，随后才允许重建 ItemsSource-backed collection 或切换 Workspace；
- 所有 WPF `ObservableCollection`、Workspace、所属 Properties、Details、Diagnostics 和属性通知只在其 Dispatcher 上刷新；后台 SoundFont 提交不得直接触碰 WPF 投影；
- SoundFont 长操作显示模态任务状态并允许 Cancel；取消在正式 Project edit 前生效，不留下部分 SoundFont 选择；
- owned dialog 的 Caption Close 是 WindowChrome 内明确可命中的安全 Cancel 动作；
- 不通过伪造 `WM_SIZE`、同步 `UpdateLayout`、阻塞等待或 PowerShell UI 自动化修补输入/布局状态。

失败条件与诊断：

- 创建或导航异常由现有显式错误/Notice 路径报告，Dispatcher 保持可继续处理输入；
- SoundFont 缺失、不可读、格式/Worker 失败继续按第 6 章失败原子语义处理；大文件仍需完整流式 hash，不以跳过验证换取表面响应；
- Cancel 只取消尚未提交的选择流程；已原子提交的 Project edit 不由取消回滚；
- 窗口 Close、Escape、Alt+F4 继续等同 dialog Cancel，不修改 Project。

归属与边界：

- 创建、Folder、Track 和 SoundFont 选择结果属于 Project Content，进入统一 Undo/Redo；
- Dispatcher 排队、Popup 状态、输入捕获、task overlay 和 dialog hit-test 只属于 UI runtime/session，不持久化；
- 本次不改变 Event Instrument、Folder、Track、Conductor 或 SoundFont 的业务语义、文件格式、canonical 结果和可听结果；样式细节缺陷不在本次修复范围。

自动验证：

- `WpfInteractionRegressionTests.ProjectEditsQueueBoundCollectionRefreshesOnTheDispatcher` 覆盖 Dispatcher 内编辑、后台线程编辑、Track/Instrument/Folder 创建、Instrument/Conductor Workspace 打开和集合线程归属；
- `WpfInteractionRegressionTests.CaptionButtonStyleIsInteractiveInsideWindowChrome` 覆盖共享 caption style 的 WindowChrome 命中声明；
- Worker 正式 file protocol 集成测试使用固定 Native AOT Worker 和真实 SF2 完成 prepare/render，证明 Worker 基线本身没有复现无限等待。

本地仅检查无音频资源的 UI 时可直接使用 `dotnet run`。需要选择/验证 SF2、枚举音频设备、播放、预览或渲染时，使用仓库根目录 `Run-MidoraDesktop.ps1`：脚本只接受通过固定 manifest 校验的 operator-supplied BASS 目录，发布当前 `win-x64` Native AOT Worker 到桌面构建输出旁的 `audio-worker/`，再启动 WPF。BASS 路径、Worker 路径和发布产物均不写入 Project、Application Preferences 或源码树正式资产。

## 9. 2026-08-21 Arrangement chrome、概览与任务进度修正 trace

输入与正式输出：

- Arrangement 左侧 ruler header 固定接收 Event Instruments 栏开关、Track 创建菜单和“清除全部 Track/共享组 Mute/Solo”命令；ruler 的正式小节标签仍只绘制在内容区。
- Mute/Solo 清除一次性清空 Track 与共享 Usage/Root 的四组运行时过滤状态，并在正在播放时只提交一次 monitoring 差量；失败时恢复调用前的运行时集合。
- Segment editor horizontal overview 使用两个独立的内容指纹缓存层：NoteOn/GateStart 为蓝灰色 1 device-pixel 竖线，任意非音符 MIDI event / logical parameter point 为红色系 1 device-pixel 竖线并绘制在其上（最终暗红色由第 10 节修订）。普通内存对象与分页 Direct MIDI 均按真实 onset/event tick 投影；raw `NoteOn` 进入 Note 通道，raw `NoteOff` 不冒充 non-Note event。
- Audio Render 继续以 `processed frames / total frames` 产生确定进度；WPF 仅合并显示最新样本，最高 10 次/秒，任务完成仍固定为 100%。

边界、失败条件与归属：

- 上述 Mute/Solo、pane visibility、overview density、scrollbar thumb 和进度合并器均为 UI/runtime session 状态；不修改 Project、canonical、Undo/Redo、持久化格式或可听内容。
- MIDI 分页 overview 的输出宽度受实际控件宽度约束，缓存键由内容指纹、extent 与 device-column 宽度组成；不得把 Note 的 Gate End/持续范围误当作新的 NoteOn，也不得根据页级 `min/max/count` 在二者之间补画或均匀猜测内容。只有当完整 endpoint page 的最小和最大 tick 明确落入同一 device column、且没有会话编辑排除项时，才可仅凭目录摘要命中该列；跨列页必须读取现有 endpoint index 的真实 tick。Copy-on-write 删除/替换先排除源 stable ID，再叠加当前编辑值，保证空洞、移动和删除后的概览准确。
- Audio Render 进度总数无效时保持 indeterminate；已知总帧数时必须显示 determinate indicator。取消、失败和输出原子发布语义不变。
- 滚动条视觉 Thumb 必须使用 Track 分配的实际长度，不得用最小视觉长度越过碰撞区域；菜单栏与右键菜单的分割线必须显式覆盖 `MenuItem.SeparatorStyleKey`，不得回退到明亮的系统默认模板。禁用的 Event Instrument 选择列表保持透明背景；Arrangement Event Instruments pane 开关的未选中态只在该按钮上复用相邻 Add 按钮的背景/前景，不修改全局 ToggleButton。

明确非目标：

- 不改变 MIDI Segment 内容、分页 pack 格式、Audio Render worker 协议、monitoring 的可听筛选规则或 Arrangement Track 模型；概览 endpoint page 解码继续受现有 project-wide decoded-page LRU 上限约束。
- 不把 Reset Monitoring 解释为重置 Playback Engine，也不修改任何持久化 Track 属性。

## 10. 2026-08-21 Arrangement Track 命令、共享路由与 Marker 标尺 trace

输入与正式输出：

- Logical Track 的普通 `Duplicate` 深拷贝 Track、Segment 及其内容，并创建引用同一 Event Instrument Definition 的独立 Usage；`Duplicate and Share State` 才保留源 Usage。两种命令均生成新的 Track/内容稳定 ID，Undo/Redo 原子恢复对应 Usage、global order 与对象身份。
- Logical Track 右键菜单可直接打开其有效绑定的 Event Instrument Definition；未绑定 Track 不提供可执行目标。Track 上下文不再显示 `Duplicate Instrument Only`，Definition Browser 的普通 Definition Duplicate 不受影响。
- 从单个 Pure MIDI Track 打开 route settings 时，如仅修改共享 Fixed Port.Channel 的 Channel Mode，UI 明确提示影响的 Track 数；确认后原子更新唯一 Root，所有成员同步生效。改到其他 route 时仍只移动当前 Track。旧的 `Shared MIDI Route Settings` 术语不再作为错误恢复指引。
- Arrangement ruler 从 Conductor Marker 建立只读、不可命中的专用投影；Marker 的左边界定位到正式 tick，以浅灰圆角边框和 secondary text 显示。小节号贴近底部刻度，Marker 使用其上方空间。
- Segment horizontal overview 的 event channel 使用与 Note 概览接近暗度的暗红色，继续与播放指针红色明确区分；Note/event tick 与既有内容指纹缓存不变。

UI/runtime 边界：

- shared brace 在 Track hover/pressed fill 之后绘制，保证组边界处于 Track header 视觉最上层；它不改变命中、排序或 membership。
- Arrangement、Logical Segment 与 MIDI Segment toolbar 的左侧 context text 使用与右侧工具相称的外边距；Conductor workspace 只隐藏 toolbar 中冗余的 `Conductor Track` header，不改变 Tab 标题或 Workspace identity。
- Event Instruments pane toggle 的 hover border 只在该按钮本地复用相邻 Add 按钮的 hover token；不修改全局 ToggleButton 模板。
- Arrangement 的 Event Instruments pane 为 Definition 项使用特化列表容器：左侧窄竖线始终显示该 Event Instrument 的正式颜色，选中态只改变背景而不得用公共红色左边框覆盖或替代该颜色线。
- `New Logical Track with Instrument...` 创建新 Definition 时，提交后激活新 Event Instrument Workspace；使用既有 Definition 时仍返回 Arrangement。

规格同步记录：

- 产品所有者于 2026-08-21 授权全面更新后，Logical Track 普通独立 Duplicate、显式 `Duplicate and Share State`、Definition-only Duplicate、共享 Fixed Root Channel Mode 编辑与 Arrangement chrome / Marker / overview 投影已同步进入 SRS 第 5、7、10～12、17、18、20、22、24 章及 ADR-CORE-046 / ADR-UI-041；本节不再记录未解决的规格差异。

## 11. 2026-08-21 Arrangement Track 单选与 Pure MIDI cache identity 修正 trace

输入与正式输出：

- Arrangement Track Header 点击产生至多一个、按 Track stable ID 标识的会话选择；选中态只驱动 Header 视觉与 Track 快捷键/命令目标，不替代 Segment、Note 或 Event 的 Workspace Selection。空白左/右键清除该选择，brace 仍是独立 group hit target且不参与单选。
- brace hover 优先于同位置 Track Header hover，并同时高亮 brace gutter 背景；最后一个 Track 内容底边补齐与行间一致的分割线。Event Instruments pane 在新建 Arrangement Workspace 时默认折叠。
- Pure MIDI reusable PCM key 的正式输入是实际 Direct Note / Channel Event 内容、分页内容 fingerprint、copy-on-write 删除/替换/新增 delta、Root/Track/Segment identity、生命周期范围及既有音频环境。集合编辑次数不再充当内容 identity；相同 stable ID 与相同编辑次数但内容不同的 Project 必须得到不同 key。
- 状态栏任何完整消息均提供 `Details`，包括 cache retention Warning；省略显示只影响紧凑状态栏文本，不得丢失完整消息。

边界、失败条件与归属：

- Track Header selection、brace hover、pane visibility 与状态消息展开只属于 UI/runtime session，不持久化、不进入 Undo/Redo、canonical 或 `.midora`。
- reusable quota 满只停止新 reusable retention；既有合法命中继续可读，miss 必须现场合成。它不得导致静音、阻止播放或改变 canonical。旧的、不完整内容寻址 key 通过 fingerprint schema 修订自然失效，不静默接受为新条目。
- 本轮不改变 Pure MIDI 可听语义、Root 生命周期、正式分配、SoundFont/renderer identity 或缓存容量策略；只修复错误缓存别名。

验证重点：

- 两个 stable ID 序列与集合 `Generation` 完全相同、但 Note/Channel Event 内容不同的 Pure MIDI Project，其 fragment fingerprint 和 PCM key 必须不同；内容完全相同则仍确定相等。
- quota 已满的 miss staging 不得伪造 hit 或移除 live-synthesis plan。
- Arrangement 选择按 stable ID 跨 Rebuild/重排保持，目标删除时清除；空白清除与 brace 命中不得留下旧快捷键目标。

## 12. 2026-08-21 Shared brace drag/context 与新建 Pure MIDI 首播修正 trace

输入与输出：

- brace 右键菜单的当前 shared-group stable ID 在菜单打开期间形成独立 context-highlight；菜单关闭时只清除视觉状态，不改变 Track selection、Project 或共享组身份。
- member 从本组 top/bottom exterior strip 脱离时，高亮实际生效的上/下边界；外部 Track 指向目标 block body 时，仅绘制 block 外框并压制成员 Header hover。
- 每次主播放在 Project edit lock 内按当前 Arrangement Track、Mute/Solo 与共享归属重建 audible set。Playback Controller 创建后新增的 Pure MIDI Track 不得沿用空的旧集合。
- 全部 Pure MIDI Track 均从 canonical SMF descriptor 建立 Track-to-Root cache ownership。初始不可听 owner 的不完整 journal 不发布；后续 playback-span batch 不得把既有 retention failure 重标为 quota-full。

归属与非目标：

- brace context/drag 高亮只属于 WPF session interaction，不持久化、不进入 Undo/Redo 或 canonical。
- audible set 只属于 realtime consumer；修正不改变 canonical、MIDI Export、Audio Render、Project 数据格式或正式 Mute/Solo 语义。
- 本条仅记录 2026-08-21 当轮边界；Global Inspector 已由 2026-08-22 的第 14 节迁移并删除。

验证重点：

- 在空 Project 已创建 Playback Controller 后新增 Auto/Fixed Pure MIDI Track 与 Note，首次播放计划必须包含其 NoteOn，Track source 不得初始禁用。
- 初始禁用 Pure MIDI child 时，Root miss 不得发布不完整 Segment cache，retention 保持 Enabled；已存在 write failure 的 Store 接收后续 batch 后仍保持原 failure 分类。
- 使用正式 Native AOT Worker 与真实 SF2 的新建 Pure MIDI 管线必须完成播放、保留非静音 PCM 且 cache retention 不被禁用。

## 13. 2026-08-22 Shared block exterior drop preview 修正 trace

输入与正式输出：

- 输入是单 Track Header 拖动期间的实时指针位置、来源/目标 Track 类型、来源/目标 shared-group stable ID，以及目标 block 的真实上/下边界。
- 外部 Track 尚处于 block top/bottom exterior strip 时，预览线固定显示在 block 的真实外边界；进入 body 后才切换为整 block 虚线外框。不得在这两个语义之间显示成员间隙插入线。
- 同组成员从 exterior strip 脱离时，预览固定在对应外边界，并使用 3 DIP 的高强调粗线叠加既有低强调边界带；组内插入线不得残留。
- 约 4 DIP 的 body/exterior hysteresis、通用 10 DIP 拖动启动阈值和最终原子 drop 行为保持不变。

边界、失败条件与诊断：

- 预览状态必须由本次命中解析结果显式携带，不能再从目标成员 lane 二次推断；Snapshot 在手势中失去对应 group 时只跳过该帧预览，不得抛出异常或提交错误目标。
- 本交互没有业务诊断；取消、失去鼠标捕获或手势完成时必须清除 exterior-boundary 会话状态，避免下一次拖动继承旧预览。

归属与明确非目标：

- pointer、hysteresis、hover、exterior-boundary 和粗线均只属于 WPF session/runtime，不持久化、不进入 Undo/Redo、Project、canonical、MIDI 或音频结果。
- 本轮不改变 Track 实际排序、Usage/Root 创建删除、Rebind 审查、block 连续性、Fixed route 或 brace 整组拖动语义。

验证重点：

- 自动测试覆盖外部进入时的 12 DIP 边缘带、已进入 body 后的 4 DIP hysteresis、同组成员的 8 DIP 脱离带，以及 top/bottom 外边界映射。
- 源码渲染分支必须对 exterior-boundary 使用 group top/bottom；只有普通 member insertion 才允许调用按 lane 计算的通用插入线。

## 14. 2026-08-22 Global Inspector 全量迁移 trace

输入与正式输出：

- 输入是当前 Project、显式 Workspace、稳定 ID Selection、对象类型、编辑锁与用户提交的属性文本/选项；Global hover、播放指针和后台诊断不构成属性目标。
- Logical/Midi Segment、Logical/Direct/Template Note、Logical Parameter Point、Direct/Template Event 与 Value Curve Point 的低频精确字段由 Timeline context menu 的 `Properties...` 模态窗口提交；混合 Segment 与同类多选只公开语义一致的共同字段。
- Conductor Event、Event Instrument 内部结构对象与 Parameter Mapping 都使用固定目标模态 Properties。Parameter Mapping 在一个窗口直接展示 Source、Target SubVoice、Target Event Kind、适用 Controller/RPN/NRPN 与其他设置，创建和编辑不再拆成 route/property 两步。
- Direct MIDI Note/Event 使用面向音乐语义的包装字段；不向用户暴露 raw `Data1`/`Data2`。Opaque imported payload 只读。内部 Stable ID、引用编号和序号不进入 UI。
- 所有 Project-backed 提交继续调用 `IProjectEditCommand`；成功一次提交形成一次 History 项，失败不修改 Project、不创建 Undo，并把输入控件恢复为最后合法投影。

焦点、任务与状态归属：

- 属性 Modal 关闭后恢复来源 Workspace 的安全焦点。播放/Preview/前台任务期间 Project-backed 输入 Disabled，只读 Properties 与 Diagnostics 继续可查看。
- 主窗口删除 Inspector column/splitter/View command、Bottom Panel、Details/Tasks Tab 和相关命令；Application Preferences 不再写这些布局字段。读取 schema v1 旧字段仅为本机偏好兼容并忽略，下一次保存不再输出；这不涉及 `.midora` 格式或音乐语义。
- 一次只呈现当前前台 Task overlay，不保存可见历史表；状态栏截断消息通过 `View Full Message` 查看和复制全文。

验证重点：

- XAML/Release build 必须证明不存在只读属性默认 TwoWay binding crash；Global Inspector production type、binding、菜单和布局引用均不得残留。
- 自动测试覆盖多 Note/Mixed Segment/Direct MIDI Properties 原子 Undo、Mixed 激活与还原、Event Instrument 本地属性投影、Conductor 精确属性提交/Undo、Logical Parameter Name 与 Definition migration 单一 Undo，以及旧偏好字段可读但不再写出。
- 所属属性投影只能按显式对象或已经打开的本地编辑区重建；不得遍历大型 Segment 全内容或使 Selection/hover 热路径产生全局属性表刷新。

## 15. 2026-08-25 应用图标资源 trace

输入与正式输出：

- 正式输入为仓库 LFS 资源 `assets/midora.ico` 与 `assets/midora-note-transparent-256x256.png`；构建不得复制并维护第二套源文件。
- `midora.ico` 作为 Win32 Application Icon 写入正式 `Midora.exe`，并作为主窗口 `Icon` 资源供 Windows 任务栏和窗口切换界面使用。
- 透明 PNG 作为标题栏 `Application mark`，替换旧的三矩形占位图形；Main Menu、Project display name、WindowChrome 命中与标题栏拖动语义保持不变。

边界与验证：

- 图标只属于构建资源和 WPF 视觉输出，不进入 Project、`.midora`、Undo/Redo、canonical、MIDI 或音频结果。
- 构建必须验证两个外部 LFS 文件可通过链接后的 WPF Resource URI 解析，且生成的 EXE 包含可提取的关联图标。
- UI 回归测试固定 ApplicationIcon、主窗口 Icon URI 和标题栏透明 PNG URI，防止后续标题栏重构恢复占位图形。

## 16. 2026-08-25 无 Project 欢迎界面 trace

- 标题栏 Application mark 与 Main Menu 的水平间距在原基线上减少 7 pixels；窗口拖动和菜单命中区域保持不变。
- 无 Project 时隐藏 Workspace Tab 条，只显示欢迎操作区；New Project、Open Project 与 Open MIDI as New Project 保持同排可达。
- 欢迎标题在每次应用会话创建时从固定英文文案集合中随机选择一次，字号为 24 pixels、普通字重，并相对操作按钮视觉上移 8 pixels；同一会话内 Project 打开/关闭或绑定刷新不得使文案跳变。
- 删除旧的 `Open an existing Project or create a new one.` 说明行。本改动只属于会话 UI，不进入 Project、Preferences、Undo/Redo 或持久化格式。

## 17. 2026-08-25 导出轨道列表与 SoundFont 计数 trace

- MIDI Export 与 Audio Export 的 Track 选择 ListBox 保持 UI virtualization/recycling；一个标准鼠标滚轮刻度只移动一个 Track item，不再采用 WPF 默认多行滚动量。
- Application Preferences 的 SoundFonts 工具栏在 Add/Remove/Up/Down 右侧显示低强调的已选择 SoundFont 数量；“选择”严格指 `Enabled=true` 的复选项，并随勾选、取消、增删立即更新，不统计 Disabled 项或 ListBox 单行高亮。
- 两项均只改变 Dialog 会话 UI，不修改 Export/Render Draft、Application Preferences 数据模型、Project 或正式消费者语义。

## 18. 2026-08-25 内嵌字体 trace

输入与正式输出：

- 一般 UI 固定使用仓库 `assets/fonts/Sora/` 中的 Sora Regular、SemiBold、Bold 静态 TTF；代码、公式、标识符、位置读数和其他等宽场景固定使用 `assets/fonts/JetBrainsMono/` 中的 JetBrains Mono 对应字重。
- 两组字体以 WPF `Resource` 嵌入共享 `Midora.Desktop.Presentation` 程序集；正式主应用和 Style Gallery 复用相同 `Font.UI` / `Font.Mono` 资源，不查询或要求用户安装同名字体。
- 自绘 Surface 的 `FormattedText` 与普通 WPF Controls 使用同一内嵌 UI 字体；生产 XAML / C# 不再以 Segoe UI、Cascadia Mono 或 Consolas 作为正式字体来源。

许可、边界与失败条件：

- Sora 固定 revision `7f9a9c5d0ccd1c099cfac420aa27133df1c5fdc4`；JetBrains Mono 固定 release `v2.304` / revision `cd5227bd1f61dff3bbd6c814ceaf7ffd95e947d9`。二者均按 SIL Open Font License 1.1 原样分发，并在各自资源目录保存上游完整 `OFL.txt`。
- 字体只属于程序视觉资源，不进入 Project、`.midora`、Undo/Redo、canonical、MIDI、音频或 Preferences。字体缺少某个用户输入 Unicode glyph 时仍由 WPF/Windows glyph fallback 处理；本轮不额外嵌入 CJK 字库。
- 构建与 UI 回归测试必须验证六个静态 TTF 均进入 Presentation 资源、两个字体族可从共享 Palette 解析，并防止生产代码重新引入系统字体硬编码。
- 每个应用 `Window` 根节点必须显式引用 `Font.UI`；共享控件样式必须为所有承载文字的普通控件、集合容器及与主视觉树断开的 `Popup` 内容（包括 `ContextMenu`、`MenuItem`、`ToolTip`、`ComboBoxItem`）显式建立字体来源。不得仅依赖一个可能被局部样式或独立 Popup 截断的隐式继承链。
- 自绘文本只允许通过共享 `EmbeddedFontFamilies` 创建 `Typeface`；代码/公式/标识符/位置读数使用 `Font.Mono`，其余文本使用 `Font.UI`。自动测试枚举全部应用 Window 与共享文字控件类型，防止新窗口或新控件静默落回系统默认字体。

## 19. 2026-08-25 Batch Edit 表达式编辑器 trace

输入与正式输出：

- 范围仅限 Batch Edit 各字段以 `=` 开头的受限数值表达式输入；直接数字、百分比和单步常量运算仍使用普通单行输入行为。
- 进入表达式模式后使用内嵌 JetBrains Mono、暗色 C# 语义配色、自动括号配对和光标邻近括号匹配高亮；未配对括号使用错误色提示。
- 补全候选严格来自当前 Batch Edit 上下文可用的 `v0/v1/p0/p1/k0/k1/g0/g1/t0/t1/tr` 变量、布尔/类型关键字，以及正式表达式编译器允许且返回数值的 `System.Math` 常量和方法。当前字段自己的结果变量不得出现在候选中。
- 支持输入触发和 `Ctrl+Space` 手动补全、上下键选择、`Tab/Enter` 插入、`Escape` 关闭以及 `F1` 轮换查看同名 `System.Math` overload signature；该提示列表不构成新的表达式语言或合法性判定来源。
- 补全 ListBox 的一个标准鼠标滚轮刻度只移动一个候选项，复用通用列表单步滚动实现；候选数量和虚拟化方式不变。
- 每次补全筛选刷新必须向 WPF `ItemsSource` 发布新的只读候选快照；不得原地修改仍被虚拟化 `ItemContainerGenerator` 观察的普通 `List<T>`，避免滚动测量期间出现集合计数/索引不一致并导致进程崩溃。
- Batch Edit `Help` 使用可调整尺寸的专用结构化窗口，不再把整份说明塞进 Message 文本。窗口按输入形式、编辑器快捷键、变量、常用 `System.Math`、结果规则分区，并由易到难固定展示 6 个可编译的实用例子；最后一例以 `tr`、`Sin` 和 `PI` 生成 `-64..64`、相对 `tr=0` 负四分之一周期相移的离散 Event Point 正弦序列。长内容只在窗口主体内滚动，Close 始终可达。

边界、失败条件与归属：

- 最终合法性、结果变量依赖、循环引用、数值范围、10 秒执行上限和原子 Batch Edit 提交仍完全由既有 `BatchEditExpressionProgram` 与 Project command 决定；编辑器不得静默改写表达式或接受编译器拒绝的输入。
- Roslyn 只负责 Expression 语法树解析；正式执行由自有 binder 建立 `System.Linq.Expressions` 委托。运行时不得 Emit 用户程序集、创建 collectible ALC、读取 `TRUSTED_PLATFORM_ASSEMBLIES` 或要求 reference pack 路径，因此 self-contained 压缩 single-file 产物与普通开发构建必须接受同一表达式。
- 编辑器继续保持单行语义；粘贴的 CR/LF 仅转为空格。补全 Popup、当前候选、括号高亮和光标状态只属于 Dialog session，不持久化到 Project、Preset schema、Undo/Redo 或 canonical。
- 编辑器基于固定 NuGet 包 `AvalonEdit 6.3.1.120`；其 MIT 许可和上游 revision 记录在根 `THIRD-PARTY-NOTICES.md`，正式发布继续随应用分发该 notices 文件。

验证重点：

- 自动测试覆盖 Note/Event 上下文变量过滤、当前结果变量排除、`Clamp` 的静态导入与 `Math.` 两种补全、非表达式不显示补全、非数值 `System.Math` 返回项排除、嵌套/未配对括号匹配，以及 Help 的 6 个例子都能由正式表达式编译器接受；正弦例在 `tr=0/96/192` 时必须分别得到 `-64/0/64`。Compiler 回归还必须在 TPA 路径不可用时覆盖变量、DAG、C# 数值提升、整数参数 Math 重载、拒绝面和求值结果。
- Release WPF build 必须验证 AvalonEdit XAML、内嵌等宽字体资源、Popup 和 Batch Edit Dialog 均可加载；表达式执行语义继续由 Compiler/Application 测试覆盖，并以 self-contained、压缩 single-file 临时 smoke 验证正式发布宿主不再依赖 TPA/reference assembly 文件路径。

## 20. 2026-08-26 单行代码输入与欢迎文案修复 trace

- 输入：欢迎页会话文案、Batch Edit 与 Mapping Function 补全候选的单次鼠标点击，以及从系统剪贴板进入两种 AvalonEdit 单行表达式编辑器的任意 CR/LF 文本。
- Presentation 输出：欢迎文案占满既有父容器宽度、居中并允许自动折行，不以省略号截断；鼠标单击一个实际候选项立即插入该项并恢复编辑器焦点，滚动条和候选列表空白点击不提交。
- 编辑边界：两种表达式编辑器在 AvalonEdit 默认 Paste 命令建立文档 Undo Group 之前拦截粘贴，将 CRLF/CR/LF 确定性替换为空格，再通过当前 Selection 的正式替换路径形成可撤销编辑。`TextChanged` 不得在第三方文档事务仍打开时重设整个 `Text` 或清空 Undo 栈。
- 失败与归属：临时 Clipboard ownership 失败只取消本次粘贴，不得逃出 UI 输入路由终止进程。欢迎文案、Popup、候选和表达式编辑 Undo 都是 Dialog/应用会话状态，不修改 Project、canonical、持久化、编译或音频语义。
- 验证：WPF STA 回归在已打开 Undo Group 时插入多行文本并确认无异常且结果为单行；XAML 回归固定欢迎文案布局与两处补全列表的单击提交入口。
