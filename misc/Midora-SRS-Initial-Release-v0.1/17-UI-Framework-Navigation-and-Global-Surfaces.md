# 第 17 章 UI 框架、导航与全局界面

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章定义单主窗口、全局框架、Workspace Tabs、对象所属属性编辑器、Diagnostics Workspace、状态栏、Notice 和 UI 状态所有权。初版不提供 Global Inspector、Bottom Panel 或左侧 Project Panel；外层对象导航与层级编辑统一进入常驻 Arrangement，见第 24 章。

## 17.1 全局界面架构
### 17.1.1 单主窗口
初版只有一个持久主窗口。
允许：
```text
One persistent Main Window
Owned modal dialogs
Windows file and folder pickers
Context menus
Popups
Tooltips
```
不允许：
```text
Multiple main windows
Floating workspace
Detached panel
Independent project window
Dockable floating tool window
Full-screen mode
Always-on-top mode
Tray mode
```
### 17.1.2 主窗口粗略布局
```text
+--------------------------------------------------------------------------+
| [A] Window Title Bar                                                     |
+--------------------------------------------------------------------------+
| [B] Main Menu                                                            |
+--------------------------------------------------------------------------+
| [C] Global Command Bar and Transport                                     |
+--------------------------------------------------------------------------+
| [D] Workspace Tabs                                                       |
+--------------------------------------------------------------------------+
| [E] Active Workspace                                                     |
|                                                                          |
+--------------------------------------------------------------------------+
| [F] Global Status Bar                                                    |
+--------------------------------------------------------------------------+
```
### 17.1.3 区域职责
#### 17.1.3.1 [A] Window Title Bar
显示：
```text
Application name
Derived project display name
Project Modified state
Current file path summary when useful
```
Project Name 为空时：
```text
Saved Project   -> current file name
Unsaved Project -> Untitled Project
```
Project Name 与 `.midora` 文件名相互独立；修改 Project Name 不重命名磁盘文件。
#### 17.1.3.2 [B] Main Menu
固定一级菜单：
```text
File
Edit
View
Project
Playback
Compile
Export
Help
```
`Project` 菜单必须提供 `New Event Instrument`、`New Logical Track`、`New Logical Track with Instrument...` 与 `New Raw MIDI Track...`，并与 Arrangement 左侧 ruler header 的 `Add` 菜单调用同一 Project command。播放或前台任务持有 Project 编辑锁时，`Project` 一级菜单本身仍保持可用，`Project Settings` 仍可打开；只禁用其中会创建或编辑 Project 对象的命令。
#### 17.1.3.3 [C] Global Command Bar and Transport
常驻入口：
```text
File commands
Project Undo and Redo
Play and Stop
Compile
MIDI Export
Audio Render
Global context and task state
```
Global Undo / Redo 永远操作 Project History。Global Save 永远执行 Save Project。
#### 17.1.3.4 [D] Workspace Tabs
中央编辑区域使用多 Workspace Tab。同一功能 Workspace 按类型唯一；对象 Workspace 按稳定 ID 唯一。
Arrangement 固定为第一个 Tab、常驻、不可关闭、不可重排。它提供 Conductor 与 Logical / Pure MIDI Track 的唯一全局平铺顺序入口，并可切换显示独立的 Event Instruments 管理栏。
#### 17.1.3.5 [E] Active Workspace
承载当前编辑器或完整功能 Workspace。
#### 17.1.3.6 [F] Global Status Bar
显示：
```text
Issues
Compile State
SoundFont Resource
Project Save State
Playback State
Transient Message
```
### 17.1.4 面板尺寸与折叠

Workspace 内部的显式侧栏、下部 Lane 编辑区和 Preview 区可按各自编辑器规格调整或折叠；主窗口不设置跨 Workspace 的 Bottom Panel。布局仍属于 Application Preference 或 Project Session UI State，不属于 Project Content。
---
## 17.2 Project 内容与 UI 状态边界
初版统一分为四层。
### 17.2.1 Project Content
进入 `.midora`，影响作品、编译或项目默认输出语义：
```text
Conductor events
Event Instruments and internal definitions
Logical Tracks, Segments, Notes and Logical Parameter data
Manual object order
Project object colors
Project Metadata
SoundFont Settings
Playback Settings
MIDI Export Settings
Audio Render Settings
Reset Defaults
```
`Global Event Scope Defaults` 在初版只是持久化兼容所需的不可编辑空 marker，不属于用户可修改 Project 内容，也不提供独立设置入口。
### 17.2.2 Application Preferences
保存于当前 Windows 用户本机，跨 Project 共享：
```text
Normal window bounds and maximized state
Major splitters
Follow Playback preference
Default lane heights
List or card view preferences
File picker recent directories by purpose
Selected playback output device ID or System Default choice
Render-Ahead Buffer
Device Buffer Request
Realtime Maximum Sample Voices per Unit Stream
Audio Cache Root
Maximum Reusable Audio Cache Bytes
Ordered application SoundFont list: absolute local path + Enabled
```
这些状态：
- 不进入 Project Undo / Redo；
- 不标记 Project Modified；
- 不进入 `.midora`；
- 不做账号、云端或设备同步。

设备实际采样率、实际 buffer、callback period、当前设备枚举结果和 IPC 运行状态属于 Derived / Runtime Data，不作为 Application Preference 保存。音频缓存的 reusable 当前占用、transient 当前/峰值、session 目录、retention 状态与 Warning 同样是运行时派生状态；只保存配置 root 和 reusable byte quota。

SoundFont 列表对所有 Project 和从 MIDI 导入的新 Project 共用，不属于 Project 创建参数。列表支持新增、删除、启用/禁用和排序；顺序是正式 BASSMIDI 优先顺序。Apply 只保存路径结构，不读取、复制、hash 或调用 BASS 验证文件；真正的存在性、可读性和 BASS 加载失败在音频任务 Preparing 报告。列表不得进入 `.midora`、Project Modified 或 Undo/Redo。
### 17.2.3 Project Session UI State
只存在于当前 Project 会话：
```text
Workspace Tabs and order
Active Workspace
Workspace selection
Zoom and scroll
Playback and edit cursors
Time range selection
Navigation history
Search queries and filters
Tree expansion
Active subpage
Workspace-local lane height and focus history
Workspace-local lower editor visibility and height
Arrangement Grid / Snap session settings
Shared Segment and SubVoice piano-roll Grid / Snap session settings
Default Segment creation length
Shared piano-roll default Note length and velocity
```
关闭或替换 Project 后清除，不跨应用重启恢复。

状态栏 transient message 必须提供直接“已读”操作；该操作只清除当前 transient message，不清除 Diagnostics、不修改 Project，也不创建 Undo。
### 17.2.4 Transient Interaction State
只存在于当前交互：
```text
Drag preview
Marquee selection gesture
Inline Rename buffer
Uncommitted ordinary field text
Popup target
Function Draft text buffer
```
Function Draft 可持续到 Function Workspace 关闭，但仍不属于 Project Content、Application Preference 或恢复文件。
### 17.2.5 `.midora` 明确不保存
```text
Window and panel layout
Workspace Tabs
Selection
Zoom and scroll
Playback cursor
Search and filter state
Mute and Solo
Task History
Runtime Diagnostics
Undo and Redo history
Function Draft
Compiled result and playback buffer
```
---
## 17.3 Workspace Tabs 与导航
### 17.3.1 Workspace 唯一性
固定功能 Workspace 按类型唯一，例如：
```text
Arrangement
Project Settings
Diagnostics
Conductor Track
```
对象 Workspace 按稳定 ID 唯一，例如：
```text
Segment Editor
Event Instrument Editor
Mapping Function Editor
```
重复打开同一对象时激活已有 Tab。
### 17.3.2 复杂对象子页面
同一复杂对象的不同子页面通常共用一个顶层 Workspace，通过内部 Section Navigation 切换。C# Mapping Function 可以使用独立对象 Workspace。
### 17.3.3 Tab 标题
使用用户可识别语义：
```text
Arrangement
Project Settings
Diagnostics
<Instrument Name>
Function: <Function Name>
Segment: <Track Display Name> @ <Start Position>
```
Segment 没有持久化名称；必要时在标题中补充 Track 顺序以消除同名歧义。
普通 Tab 不显示独立“未保存文件”星号。Project Modified 是全局状态。Function 未 Apply 草稿使用独立 `[Draft]` 标记。
### 17.3.4 关闭 Tab
关闭 Tab：
- 只关闭界面；
- 不删除对象；
- 不修改 Project；
- 不清除 Diagnostics。
有 Function Draft 时，关闭前必须选择：
```text
Apply
Discard Draft
Cancel
```
对象被删除后，其 Workspace 自动关闭；Undo 恢复对象时不自动重开。
Workspace 使用可见关闭按钮和 Tab Context Menu 关闭。初版不为关闭 Workspace 注册默认快捷键。Arrangement 隐藏关闭按钮，Tab Context Menu 的 Close 禁用，并始终固定为第一项。
### 17.3.5 Tab Strip
- 单行显示；
- 支持滚动；
- 支持 Tab List；
- 不使用多行 Tab；
- Tab 只可在主窗口内重排，不可拖出成为浮动窗口。
```text
Ctrl+Tab       -> Next Workspace Tab
Ctrl+Shift+Tab -> Previous Workspace Tab
```
### 17.3.6 导航历史
Back / Forward 是 UI 导航历史，不是 Project Undo / Redo。初版提供可见按钮，但不注册 `Alt+Left / Alt+Right`。
### 17.3.7 打开 Project 后
成功打开或创建 Project 后默认打开 Arrangement Workspace，不恢复上一应用会话的全部 Tabs。
---
## 17.4 对象所属 Properties 对话框
### 17.4.1 不设置 Global Inspector

主窗口不得设置自动跟随 Active Workspace、Primary Selection、hover 或后台状态的 Global Inspector。`View` 菜单不提供 Inspector 开关，主窗口布局和 Application Preferences 不保存 Inspector 可见性或宽度。

对象属性入口必须归属于对象的正式编辑上下文：

```text
Timeline object exact values -> context menu > Properties...
Conductor event exact values -> Event List double-click / Properties...
SubVoice definition/state    -> Structure list double-click / Properties...
Logical Parameter definition -> definition-and-migration dialog
Parameter Mapping            -> unified Properties dialog
Mapping Chain / Step         -> Structure list double-click / Properties...
Envelope Preset              -> Structure list double-click / Properties...
Project settings             -> Project Settings Workspace
```

复杂对象继续使用其专用编辑器，不在通用属性表中复制 Timeline、Mapping Function source、Loop 图形、曲线编辑器或完整事件列表。

### 17.4.2 显式目标与稳定上下文

属性编辑器只在用户显式打开对应 Section、Dialog 或命令后成为编辑目标。普通 Selection、hover、诊断刷新或 Workspace 切换不得使另一块全局 UI 自动变成不同对象的属性编辑器。

`Properties...` 在打开时冻结 Workspace、Selection 与原始字段值；Modal 存续期间外部 Project 编辑被阻止。Event Instrument 不设置 Properties Tab 或自动跟随结构选择的属性面板；内部对象只能由双击或显式 `Properties...` 打开其模态对话框。

### 17.4.3 单选与多选

单选属性编辑器可显示用户可理解的对象类型、名称/摘要和可编辑或只读字段。UI 不显示 Stable ID、内部引用编号或要求用户复制内部身份。多选只显示所有对象语义完全相同、可安全批量修改的共同字段，且必须区分：

```text
Same Value
Mixed
Unavailable
```

`Unavailable` 不得伪装成 `Mixed`。`Mixed` 字段初始 Disabled，并在右侧提供单图标“编辑为统一值”入口；启用后清空或使用安全默认值。每个已经修改或启用统一值的字段都提供单图标“恢复原值”入口；原始状态为 `Mixed` 时，恢复后重新 Disabled 并重新显示统一值入口。没有可展示字段时 `Properties...` Disabled；仅有只读字段时仍允许打开只读 Properties 对话框。

### 17.4.4 属性提交

Properties 对话框中的文本、Toggle、Combo、枚举与引用选择全部只修改本地 Draft，不得因键入、选择、Enter 或失焦直接改 Project。底部固定提供 `OK` 与 `Cancel`：

```text
OK      -> validate all changed fields, apply one atomic Project command, create one Undo entry
Cancel  -> discard every Draft change, Project remains byte-for-byte unchanged
```

所有带确认提交的模态对话框必须复用同一 Dialog action contract：主操作使用统一宽度的红色 Primary Button 并作为 Enter 默认动作，Cancel 使用同一宽度的普通按钮并作为 Escape 取消动作。焦点控件拥有自身 Enter 语义时仍按第 20.12.8 节优先处理；除此之外不得要求每个对话框各自重复实现键盘路由。

任一字段无效或命令失败时，对话框保持打开、显示具体错误且 Project 不发生部分修改。播放或前台任务锁定期间允许打开只读 Properties，但全部 Project-backed 输入、统一值与恢复按钮 Disabled。

### 17.4.5 值来源、Broken 与锁定

存在继承语义的字段必须区分 Default、Inherited、Explicit Override 和 Effective Value；`Use Inherited` 表示删除 Override，不是复制当前有效值。Broken Reference 只显示 Last Known Name、Expected object type 和可执行的重绑/删除动作；内部 ID 仍参与诊断定位，但不显示给用户，也不得按名称自动修复。

### 17.4.6 Direct MIDI 包装

Direct MIDI Note/Event 的 Properties 必须按音乐语义包装字段，不暴露 `Data1`、`Data2` 等 wire/storage 字段。Note 显示 start、gate、key、NoteOn velocity 与 NoteOff velocity；Event 按事件类型显示 Controller/Value、Program、Pressure、Pitch Bend 等适用字段。事件类型在既有 Event Lane 的 Properties 中只读；需要改变类型时使用创建/删除流程，避免在一个模态事务中产生含义不明确的数据迁移。

---
## 17.5 Diagnostics Workspace 与前台任务表面

Diagnostics 是独立 Workspace，不在主窗口底部复制紧凑列表。它使用正式诊断数据源，保存当前会话内的筛选、排序、选中行、列宽和滚动。单击 Diagnostic 只选择该行；`Go to Source` 才导航、选择并滚动来源对象。Diagnostics 可在 Workspace 内显示完整 Message、Source Path、Code 与下一步，但不设置通用 `Show Details` 菜单或 Bottom Details 快照。

主动切换到 Diagnostics Workspace 时，键盘焦点必须落在非编辑的 Workspace 表面，不得自动进入搜索框、筛选下拉框或其他命令控件。用户主动 Compile、Play 或 Preview 失败时可激活 Diagnostics，但不得抢键盘焦点或自动跳转来源；后台 Information、Warning 和普通非阻塞 Error 只更新状态栏计数。

主窗口不设置 Tasks Tab 或 Task History 表。一次只存在一个前台任务；必要时由模态 Task overlay 展示当前任务。只有任务明确支持安全取消时才显示可响应的 Cancel。没有可靠总量时使用 indeterminate 动画；有可靠当前值与总量时才显示 determinate 进度。Save / Save Copy 进入不可取消事务后不得显示不可响应的 Cancel 控件。任务状态属于 Runtime Data，不保存、不进入 Undo/Redo。
---
## 17.6 外层对象导航

初版删除 Project Panel。Conductor、Logical / Pure MIDI Track 的创建、全局排序、共享组操作、打开与 context menu 全部统一到常驻 Arrangement；Event Instrument Definition 由 Arrangement 内可切换的 Event Instruments 管理栏维护。Usage 与 Root 是内部共享执行身份，不占独立时间线行。完整规则见第 24 章。

Project Settings 继续从主菜单 `Project` 打开；Diagnostics 可从主菜单和 Status Bar Issues 导航；删除 Project Panel 与 Bottom Panel 不删除任何功能 Workspace。

播放期间允许在 Arrangement 浏览、切换 Event Instruments 管理栏、打开 Workspace 和查看诊断；Project 编辑锁仍禁止创建、删除、重命名、排序、复制与共享组改绑。管理栏可见性、选择和 viewport 是 session state。
---
## 17.7 Global Command Bar、Notice Bar 与 Status Bar
### 17.7.1 Global Command Bar
核心命令在宽度不足时进入 Overflow，但以下命令必须保持可达：
```text
Save Project
Project Undo and Redo
Play and Stop
Compile
MIDI Export
Audio Render
```

播放位置文本的 Tick-in-beat 字段使用 Project TPQ 十进制位数作为最小零填充宽度；例如 TPQ `1920` 使用四位 Tick 字段。该字段宽度不得因播放中的 Tick 值跨越固定三位边界而左右抖动。
### 17.7.2 Global Notice Bar
只用于持续、重要且影响全局工作流，或必须由用户关注才能继续的状态：
```text
Damaged Objects Prevent Saving
Migrated Project Requires Saving
Enabled SoundFont Missing or Load Failed
Save Failed
Uncommitted Function Drafts
Preference Storage Failed
```
正常状态不占空间。
成功操作、普通信息、短暂锁定原因和可恢复的小错误不得打开 Notice Bar；它们使用 Status Bar 的瞬时消息区域。只有阻止安全继续、要求用户决策或持续影响全局工作流的问题才进入 Notice Bar 或 Blocking Dialog。
同时存在多个 Notice 时显示最高优先级项和 `View All`，不堆满窗口。
关闭或折叠 Notice 只改变 UI 显示，不清除问题或诊断。
### 17.7.3 Status Bar
示例格式：
```text
2 Errors, 3 Warnings | Compile Outdated | SoundFont Configured | Modified | Playing       <Transient Message>
```
左侧状态单元固定按 `Issues → Compile State → SoundFont Resource → Project Save State → Playback State` 排列；Issues 左侧显示同一诊断状态圆点。最右侧只用于瞬时消息；非错误消息使用次要文本色，错误消息使用错误色。该区域不得显示 CPU RID 或 .NET 运行时版本。
`Playing` 使用成功/绿色文本；`Buffering` 使用纯黄色文本并可附带有界进度百分比。颜色只表达运行状态，不改变 Transport 可用性。
Issues 显示 Whole Project 当前诊断计数，不受 Diagnostics 当前搜索和筛选影响。计数文本是显式导航入口：鼠标悬停时提亮并显示 Hand 指针，单击后激活 Diagnostics Workspace；激活后的键盘焦点仍遵循 17.5.2 的非编辑表面规则。
Compile State：
```text
Not Compiled
Compiling
Compile Succeeded
Compile Failed
Compile Result Outdated
```
Project 编辑后，上次结果不再显示为当前成功。
Activity：
```text
Preparing
Playing
Buffering
Stopping
Saving
Exporting
Rendering
```
Buffering 是播放状态，不自动显示为 Error。
Buffering 状态文本使用纯黄色。只有音频后端提供同一 recovery 区间内单调、可验证的已准备 frame 与目标 frame 时，才追加 `(<N>%)`；不得根据经过时间猜测百分比。`100%` 表示该 recovery 区间准备完成并即将恢复 Playing。Buffering 期间主播放/停止按钮继续执行 Stop，但图标显示动态加载指示；恢复 Playing、Stopped 或 Error 后立即恢复停止/播放图标。
---
