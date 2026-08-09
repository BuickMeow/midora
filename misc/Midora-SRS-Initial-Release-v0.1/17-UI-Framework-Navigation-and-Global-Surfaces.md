# 第 17 章 UI 框架、导航与全局界面

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章定义单主窗口、全局框架、Project Panel、Workspace Tabs、Inspector、Bottom Panel、状态栏、Notice 和 UI 状态所有权。

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
+------------------+--------------------------------------+----------------+
| [D] Project      | [E] Workspace Tabs                  | [G] Inspector  |
|     Panel        +--------------------------------------+                |
|                  | [F] Active Workspace                 |                |
|                  |                                      |                |
+------------------+--------------------------------------+----------------+
| [H] Bottom Panel: Diagnostics | Details | Tasks                          |
+--------------------------------------------------------------------------+
| [I] Global Status Bar                                                    |
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
#### 17.1.3.4 [D] Project Panel
固定顶层节点：
```text
Conductor Track
Event Instrument Library
Logical Tracks
Project Settings
Diagnostics
```
#### 17.1.3.5 [E] Workspace Tabs
中央编辑区域使用多 Workspace Tab。同一功能 Workspace 按类型唯一；对象 Workspace 按稳定 ID 唯一。
#### 17.1.3.6 [F] Active Workspace
承载当前编辑器或完整功能 Workspace。
#### 17.1.3.7 [G] Inspector
显示 Active Workspace 的 Primary Selection 或 Workspace Context Object。
#### 17.1.3.8 [H] Bottom Panel
固定 Tab：
```text
Diagnostics
Details
Tasks
```
#### 17.1.3.9 [I] Global Status Bar
显示：
```text
Project State
Issues
SoundFont Resource
Compile State
Activity
Position
```
### 17.1.4 面板尺寸与折叠
Project Panel、Inspector 和 Bottom Panel：
- 可调整尺寸；
- 可折叠；
- 有最小可用尺寸；
- 不覆盖 Active Workspace；
- 布局属于 Application Preference，不属于 Project。
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
Project Panel and Inspector width and collapsed state
Bottom Panel height, state and last active tab
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
```
这些状态：
- 不进入 Project Undo / Redo；
- 不标记 Project Modified；
- 不进入 `.midora`；
- 不做账号、云端或设备同步。

设备实际采样率、实际 buffer、callback period、当前设备枚举结果和 IPC 运行状态属于 Derived / Runtime Data，不作为 Application Preference 保存。音频缓存的 reusable 当前占用、transient 当前/峰值、session 目录、retention 状态与 Warning 同样是运行时派生状态；只保存配置 root 和 reusable byte quota。
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
Arrangement Grid / Snap session settings
Shared Segment and SubVoice piano-roll Grid / Snap session settings
Default Segment creation length
Shared piano-roll default Note length and velocity
```
关闭或替换 Project 后清除，不跨应用重启恢复。
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
Event Instrument Library
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
Event Instrument Library
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
Workspace 使用可见关闭按钮和 Tab Context Menu 关闭。初版不为关闭 Workspace 注册默认快捷键。
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
## 17.4 Global Inspector
### 17.4.1 布局
```text
+--------------------------------------+
| [A] Inspector Context Header         |
| [B] Selection Summary               |
| [C] Property Sections               |
| [D] Value Source and References     |
| [E] Validation and Navigation       |
+--------------------------------------+
```
### 17.4.2 上下文
Inspector 默认跟随 Active Workspace 的 Primary Selection。初版不提供 Pin Inspector。
无子对象选择时显示 Workspace Context Object。
Project Panel、Diagnostics、Tasks 和其他辅助区域的选择不自动替换 Active Workspace 的 Inspector 上下文；只有明确导航或激活对应 Workspace 后才更新。
### 17.4.3 单选与多选
单选显示：
```text
Object type
Name or generated summary
Source path
Editable or read-only state
Lock reason
```
多选只显示所有对象语义完全相同、可安全批量修改的共同字段。
多选字段状态：
```text
Same Value
Mixed
Unavailable
```
`Unavailable` 不得伪装成 `Mixed`。
### 17.4.4 属性提交
Toggle、Combo、枚举、引用选择、Reset、Use Inherited 等操作立即提交，一次操作形成一次 Project Undo。
普通文本字段使用本地编辑缓冲：
```text
Enter or valid focus loss -> Commit
Escape                   -> Restore old value
Invalid focus loss       -> Restore old value
```
一次连续编辑形成一次 Project Undo。
数值拖动：
```text
Pointer Down -> record initial value
Drag         -> preview
Pointer Up   -> one Project commit
Escape       -> cancel and restore
```
### 17.4.5 值来源
必须区分：
```text
Default
Inherited
Explicit Override
Effective Value
```
`Use Inherited` 表示删除 Override，不是把继承值复制成显式值。
### 17.4.6 复杂编辑器边界
Inspector 不复制以下复杂编辑器：
```text
SubVoice timeline
Mapping Chain
C# source editor
Envelope and Loop editor
Logical Parameter curve editor
Complete Conductor event list
```
只显示摘要和 `Open in Editor`。
### 17.4.7 Broken 与只读
Broken Reference 显示：
```text
Stable ID
Last Known Name
Expected object type
```
不得按名称自动修复。
只读字段仍显示值、来源和原因，并在适用时提供 `Go to Source`。
播放或任务锁定期间 Inspector 可查看、复制和导航，但 Project 属性只读。
---
## 17.5 Bottom Panel
### 17.5.1 状态
```text
Expanded
Compact
Collapsed
```
高度、状态和最后活动 Tab 属于 Application Preference。
### 17.5.2 Diagnostics Tab
与完整 Diagnostics Workspace 使用同一诊断数据源和身份，但可以独立保存当前会话内的筛选、排序、选中行、列宽和滚动。
紧凑筛选：
```text
Severity
Active / Resolved / Runtime History
Whole Project / Current Workspace / Current Selection / Current Task
```
用户主动 Compile、Play 或 Preview 失败时，可以自动展开 Diagnostics 并选中首个相关 Error，但不得抢键盘焦点或自动跳转来源。
后台 Information、Warning 和普通非阻塞 Error 只更新 Badge。
### 17.5.3 Details Tab
只读扩展信息区，不是第二个 Inspector。通过显式 `Show Details` 更新，不随普通对象单击持续跳动。
### 17.5.4 Tasks Tab
显示当前和最近运行期任务，但不复制 MIDI Export 或 Audio Render 模态窗口的完整控制能力。
只有任务明确支持安全取消时才显示 Cancel。
Task History：
- 不属于 Project；
- 不保存；
- 不进入 Undo / Redo；
- 不跨应用重启。
### 17.5.5 导航
单击 Diagnostic 不自动改变 Workspace Selection。只有 `Go to Source` 才导航并更新 Inspector。
Global Notice 被关闭或折叠时，不清除对应 Diagnostic 或 Task。
---
## 17.6 Project Panel
### 17.6.1 定位
Project Panel 是 Project 语义导航树，不是 `.midora` ZIP 浏览器。
### 17.6.2 固定结构
```text
Conductor Track
Event Instrument Library
Logical Tracks
Project Settings
Diagnostics
```
顶层节点不可删除、重命名、重排或建立自定义顶层分组。
只展开 Event Instrument Library 和 Logical Tracks；不展开完整 SubVoice、Mapping、Envelope、Segment、Note 或 Diagnostic 对象图。
### 17.6.3 选择与打开
```text
Single Click       -> select navigation node
Double Click/Enter -> open or activate Workspace
```
Tree Selection 与 Workspace Content Selection 独立。Project Panel 单击不直接替换 Inspector。
### 17.6.4 Filter
Project Panel 只搜索：
```text
Event Instrument Name
Library Folder Name
Logical Track Name
Last Known Instrument Name
```
不搜索内部 Note、Segment、SubVoice、Mapping、Envelope 或诊断消息。
筛选保持正式排序；筛选期间禁用拖动排序和绑定拖放。
匹配子对象时显示必要祖先；清除筛选后恢复筛选前的 Tree 展开状态。
### 17.6.5 Event Instrument 节点
显示：
```text
Color
Usage count
Validation state
Unused state
```
Unused 不是 Warning。
Instrument 可拖到 Logical Tracks 区创建绑定 Track；拖到已有 Track 时必须显示重绑影响并确认。
### 17.6.6 Logical Track 节点
顺序与 Arrangement 完全一致。
Project Panel 不复制 Arrangement 的 Mute / Solo。
未绑定 Track 显示：
```text
Unassigned
Last Known Instrument Name
```
Last Known Name 不构成有效引用，也不按名称自动恢复绑定。
### 17.6.7 播放期间
允许浏览、筛选、打开 Workspace、查看引用和诊断；禁止创建、删除、重命名、排序和绑定修改。
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
### 17.7.2 Global Notice Bar
只用于持续、重要且影响全局工作流，或必须由用户关注才能继续的状态：
```text
Damaged Objects Prevent Saving
Migrated Project Requires Saving
External SoundFont Missing
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
Modified | SoundFont Configured | Compile Outdated | 2 Errors, 3 Warnings                  <Transient Message>
```
SoundFont Resource 位于左侧第二个状态单元。最右侧只用于瞬时消息；非错误消息使用次要文本色，错误消息使用错误色。该区域不得显示 CPU RID 或 .NET 运行时版本。
Issues 显示 Whole Project 当前诊断计数，不受 Diagnostics 当前搜索和筛选影响。
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
---
