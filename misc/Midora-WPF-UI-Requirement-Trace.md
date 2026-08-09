# Midora WPF UI Requirement Trace

状态：正式实现基线
创建日期：2026-08-08
适用范围：`src/midora-desktop/` 正式 WPF 主应用、共享呈现基础设施及 UI 自动验收

## 1. 权威来源

- `AGENTS.md`：仓库主线、初版范围、音频边界、验证门及已批准视觉基线。
- `misc/Midora-SRS-Initial-Release-v0.1/00-Table-of-Contents-and-Document-Control.md`：规格控制。
- `misc/Midora-SRS-Initial-Release-v0.1/22-Requirement-Locator-and-Cross-System-Invariants.md`：跨系统不变量。
- SRS 第 17、18、20 章：UI 框架、编辑器、通用交互和验收边界。
- SRS 第 3、13、19 章：Project 生命周期、播放/预览、文件和任务工作流。
- 其余 SRS 专项章节：UI 只呈现和编辑其正式模型，不另建业务语义。
- `misc/Midora-WPF-Style-Gallery-Requirement-Trace.md`：已通过人工视觉检查的黑红暗色主题和控件基线。

本文不是 SRS，不修改需求；它把正式 UI 的输入、输出、失败边界和验证证据映射到实现。

## 2. Requirement trace

| 范围 | 正式输入 | UI 输出/行为 | 主要依据 | 验收重点 |
|---|---|---|---|---|
| 应用生命周期 | 启动参数、单实例转发、Project create/open result | 单持久主窗口；无 Project 空状态；第二次启动转发 | 3.3；17.1；19.1～19.4；20.10.4 | 单实例、候选打开失败不替换当前 Project、关闭 guard |
| 主窗口 | 当前 Project、会话状态、任务状态 | Title、八个固定一级菜单、Command Bar、Project Panel、Workspace Tabs、Inspector、Bottom Panel、Status Bar | 17.1、17.7；20.15 | 最小尺寸、最大化工作区、Toolbar overflow、无全局二维滚动 |
| 状态所有权 | Project、Preferences、Session、Transient | 四类状态分别存储；只有 Project edit 进入统一 History | 3.10～3.12；17.2；20.14 | `.midora` 无 UI 状态；Preference 失败不影响 Project |
| 命令路由 | 焦点、Selection、锁定级别、Modal | 同一命令服务于菜单、Toolbar、Context Menu 和快捷键；焦点优先 | 19.10；20.2、20.7、20.12 | 文本焦点不误删对象；Ctrl+S/Undo/Clipboard 按上下文路由 |
| Workspace | 类型或对象稳定 ID | 类型唯一、对象 ID 唯一、单行 Tab、会话内次序/焦点恢复 | 17.3 | 重复打开激活已有 Tab；对象删除自动关闭；Draft guard |
| Project Panel | 正式 Project 顶层结构 | 五个固定根；受限搜索；稳定排序；打开/绑定导航 | 17.6；20.9.6 | 不展开完整对象图；搜索期间禁用正式拖动 |
| Inspector | Active Workspace Primary Selection | 单选、多选共同字段、值来源、验证和 Go to Source | 17.4；20.3～20.4 | 本地字段缓冲；一次手势一次 Undo；Broken 不按名称修复 |
| Arrangement | Logical Tracks、Segments、Conductor 概览、runtime Mute/Solo | 自绘 Track/Segment timeline、简化 note preview、工具、拖动、框选和范围 | 18.1；20.1、20.3～20.5 | 同 Track 不重叠；跨 Track 原子；大量 Segment 只渲染可视范围 |
| Segment Editor | Segment local notes/lanes、绑定 Instrument、Project Tempo context | 自绘 piano roll、Pitch Ruler、Logical Parameter lanes、crop 外弱化内容 | 13.22.7、13.24.5；18.2；20.1 | local/project time 不混淆；held preview 清理；预览失败不阻止合法放置 |
| SubVoice Editor | SubVoice template events、Initial State、effective Root Note | 自绘 note/event lanes；独立 Initial State；高级事件语义呈现 | 8；18.4 | 不展开 RPN/NRPN CC；空 lane 不持久化；大量事件可视裁剪 |
| Event Instrument | Instrument/Structure/Lifecycle/preview inputs | 一个对象 Workspace、四个 section、折叠 Preview、虚拟键盘 | 7～10；13.21～13.23；18.3～18.6 | Preview 走 canonical 管线；不暴露 Port/Channel；Incompatible 保留 |
| Mapping/Function | Logical Parameter、Mapping Chain、Applied source、Draft | 有序 Mapping 编辑、单值测试、独立代码草稿和 Draft 诊断 | 9；18.5；20.12.2～20.12.3 | Global Save 不 Apply；关闭/切换/退出处理 Draft |
| Conductor | Tempo/Time Signature/Key/Marker/End Marker | 自绘 lanes、同步事件列表、稳定 ID 密集 Marker 命中 | 4；18.7；20.13.3 | Tempo 离散；End Marker 特殊；不移动内容 tick |
| Library | Folder/Instrument 手动顺序和引用 | Folder、list/card 虚拟化集合、搜索、Duplicate、引用影响 | 18.8；20.8～20.10 | 单层 Folder；搜索/临时排序不改正式顺序 |
| Settings/Preferences | Project settings、Application preferences、runtime device info | 七个 Project pages；独立 Preference pages；只读 Derived 数据 | 17.2；18.9；20.14 | 非法值不 clamp；设备/音频设置仅 Stopped 提交 |
| Diagnostics/Tasks | 正式 Diagnostics、Task snapshot/history | Bottom compact view 和完整 Workspace 共用身份；Details 显式更新 | 17.5；18.10；20.11 | 后台刷新不抢焦点/Selection；历史诊断不决定当前任务 |
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

- `Midora.Desktop` 已实现单实例入口、Project create/open/save/close guard、主窗口固定区域、统一命令路由、Workspace identity/navigation、Project Tree、Inspector、Bottom Panel、Diagnostics/Tasks、Preferences、MIDI Export 和 Audio Render owned workflows。
- `TimelineSurface`、`TimelineOverviewSurface`、`PianoKeyboardSurface` 和 `LifecyclePreviewSurface` 均为专用渲染控件；Arrangement、Segment、Logical Parameter、Conductor 与 SubVoice 编辑不创建逐 Note/Event WPF 控件。
- Timeline snapshot 使用 lane interval index、可视裁剪、稳定 ID hit test、overlap cycle、marquee、drag/resize/draw/erase/split、共享 delta、grid/snap、time range 和 revision 重建。
- Event Instrument 已按 Overview / SubVoices / Parameters / Lifecycle 固定分区；SubVoice Note 与事件层为共享时间视口的两个渲染表面；Initial State 与 tick 0 事件分离；Preview 可折叠且支持 Full Instrument / Selected SubVoice 会话模式。
- Diagnostics Workspace 与 Bottom compact panel 共用诊断对象身份但保持独立 filters/selection；Conductor overview 是 Arrangement ruler 的只读确定投影。
- Project Tree 是浅层语义导航，不使用不可靠的 WPF hierarchy virtualization；普通大列表使用 recycling virtualization；Workspace Tabs 支持重排、单行横向滚动、列表入口和新选中项实现。
- 实际 100% DPI 窗口检查已覆盖 1440×900、1100×720、最大化工作区、caption、树导航、Arrangement、Project Settings、Event Instrument Overview/SubVoice、Bottom Diagnostics filters。Event Instrument 延迟实例化检查发现并修复了只读属性默认 TwoWay binding 导致的 XAML runtime crash。
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

- Project Panel 或 Event Instrument Library 中的 Create Event Instrument、Create Folder、Create Logical Track；
- Project Tree 的双击/Enter/Context Menu 打开，包括 Conductor Track；
- Embedded / External Project SoundFont 选择及其后台复制、SHA-256、原生 Worker loadability 校验；
- 使用自定义 WindowChrome 的 owned dialogs 标题栏 Close；
- Compiler、Project History、Playback 和 SoundFont 后台路径发布的状态通知。

要求输出：

- 一次用户命令只产生一次正式 Project edit；创建结果按稳定 ID 进入正式模型、History、Project Tree 和对应 Workspace；
- Popup/Menu/DoubleClick 当前输入路由先完成，随后才允许重建 ItemsSource-backed collection 或切换 Workspace；
- 所有 WPF `ObservableCollection`、Workspace、Inspector、Diagnostics 和属性通知只在其 Dispatcher 上刷新；后台 SoundFont 提交不得直接触碰 WPF 投影；
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
