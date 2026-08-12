# 第 20 章 通用交互、验证与 UI 验收边界

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章定义 Timeline、焦点、选择、批量编辑、拖放、剪贴板、Context Menu、命名、搜索、状态呈现、快捷键、语言格式、偏好保存、窗口边界和初版基础可用性。

## 20.1 Shared Timeline Interaction Rules
### 20.1.1 时间上下文
必须区分：
```text
Project Timeline
Segment Local Timeline
Event Instrument Template Timeline
Envelope or non-tick axis
```
Project Timeline 使用 absolute tick；Segment Editor 使用 local tick；Template Editor 使用 template tick；Envelope 等非 tick 横轴不得伪装为 Project tick。
### 20.1.2 Cursor 与 Time Range
必须区分：
```text
Playback Cursor
Edit Cursor
Time Range Selection
```
三者可以共存，不能使用同一种视觉标记。
### 20.1.3 Ruler
Stopped 时：
```text
Click ruler -> set Playback Cursor
Drag ruler  -> create Time Range Selection
```
Playing 时，单击 Ruler 按播放系统规则跳转。
初版不支持 Scrubbing；拖动 Ruler 不连续试听。
### 20.1.4 Grid 与 Snap
每个 tick 时间线同时具有相互独立的：
```text
Display Grid Subdivision
Operation Subdivision
Snap Enabled
```
Arrangement 使用独立设置；所有 Segment Editor 与所有 SubVoice piano roll 在同一 Project 会话内共用另一套设置。两套设置均不进入 `.midora`、Application Preferences 或 Project Undo；关闭或替换 Project 后恢复默认值：Display Grid `1/4`、Operation `1/16`、Snap Enabled。

分数相对于全音符：`1/1 = 4 × TPQ`、`1/2 = 2 × TPQ`、`1/4 = TPQ`。实际 tick 步长统一为：
```text
max(1, Ceiling(TPQ * 4 * numerator / denominator))
```
用户可输入任意正整数分母的 `1/n`。预置至少包括：Bar、`1/1`、`1/2`、`1/3`（三连二分）、`1/4`、`1/6`（三连四分）、`3/16`（附点八分）、`1/8`、`1/12`（三连八分）、`3/32`（附点十六分）、`1/16`、`1/24`（三连十六分）、`3/64`（附点三十二分）、`1/32`、`1/48`（三连三十二分）、`1/64`、`1/128`、`1/256`。`Bar` 按目标 tick 的有效 Time Signature 计算当前小节长度。

Display Grid 只控制分割线。Snap 应用 Operation Subdivision；Snap Disabled 时有效操作步长为 1 tick。改变任一设置不修改或重新量化已有对象。

有效操作步长用于 Marquee 与 Time Range 的开始和长度、Edit Cursor 与 Playback Cursor 定位、Segment / Note 放置位置、Segment / Note Resize 的 delta（而不是最终总长度），以及 Segment / Note Move 的共享 delta（而不是最终绝对位置）。

`Alt` 在 Timeline 对象拖动中临时绕过 Snap，此时有效操作步长为 1 tick。
### 20.1.5 Zoom 与 Pan
```text
Ctrl + Mouse Wheel  -> horizontal zoom around pointer time
Shift + Mouse Wheel -> horizontal scroll
Middle-button Drag  -> pan
```
不使用 `Space + Drag` 平移，因为 Space 在 Timeline Context 中用于 Play / Stop。
同一 Workspace 中共享时间坐标的 Ruler、Lane 和 Canvas 必须同步横向缩放和滚动。
在 piano roll 左侧 Pitch Ruler 上使用 `Ctrl + Mouse Wheel` 时只改变垂直音高缩放，并以指针下音高为锚点；不得同时改变横向缩放。

Piano Roll 的纵向视口必须限制在 MIDI pitch `0..127`，不得滚动到范围外。Velocity、Parameter 和 Event Lane 的纵向缩放与平移必须限制在其数值范围内；缩放后的滚轮步长按当前可见范围计算，不能使用固定大跨度。上述编辑器右侧必须提供与当前纵向视口双向同步的 Scrollbar。
### 20.1.6 工具模式
通用工具：
```text
Select
Draw
Split
Erase
```
只在支持的编辑器中显示。
Current Tool 不跨应用重启保存；新 Workspace 默认 Select。

Arrangement、Segment Piano Roll 与 SubVoice Piano Roll 的工具按钮必须互斥，且始终恰有一个激活；再次点击当前工具不得清空工具状态。上述直接编辑视图中的工具语义固定为：

- `Draw`：空白处创建；已有 Segment 或 Note 的主体可移动、左右边缘可调整长度或 active crop window；
- `Select`：在 Arrangement、Segment Piano Roll 与 SubVoice Piano Roll 中，单次左键按下始终从当前位置发起框选，即使起点位于对象上也不执行单对象点击选择；不得直接移动、Resize 或双击创建对象；既有双击导航不受该单击规则影响；
- `Split`：只在支持的对象上执行分割；
- `Erase`：删除命中的可删除对象。

`Draw` 在空白处使用默认指针，在可直接编辑对象的主体和边缘分别使用移动与水平 Resize 指针；`Select` 使用十字指针，但命中对象时不改变为移动或 Resize 指针；`Split` 命中 Segment 时始终使用文本选择形 I-beam 指针。`Draw` 命中已有对象或执行移动/Resize 时隐藏创建预览；移动/Resize 的 transient 预览必须显示提交后的位置与长度。
### 20.1.7 Drag Preview
拖动必须显示：
```text
Original position
Preview position
Delta
Length
Snap target
Target Lane or Track
Validity
Important semantic impact
```
Pointer Up 一次提交；Escape 取消且不产生 Undo。
拖到视图边缘时自动滚动。
Draw 模式在可创建 Segment 或 Note 的空白位置悬停时，也必须显示包含位置和默认长度的虚线创建预览；非法 Segment overlap 预览使用错误色。
### 20.1.8 边界
```text
Tick 0            -> hard left boundary
Project End Marker -> not an edit boundary
```
active crop window 外内容仍可查看、选择和编辑。
### 20.1.9 Follow Playback
Follow Playback 可由用户手动滚动或缩放临时中断。
### 20.1.10 播放期间
允许：
```text
Scroll
Pan
Zoom
Selection for inspection
Project Ruler jump
```
禁止：
```text
Draw
Move
Resize
Split
Delete
Cut
Paste Project objects
```
---
## 20.2 Global Focus Rules
### 20.2.1 核心概念
严格区分：
```text
Keyboard Focus
Selection
Primary Selection
Active Workspace
```
Active Workspace 由活动 Tab 决定，不因焦点进入 Inspector、Project Panel 或 Bottom Panel 而改变。
### 20.2.2 命令路由优先级
```text
1. Full Application Task Lock
2. Active Modal Dialog
3. Active Popup, Menu or Inline Editing Session
4. Focused Text Field or Function Code Editor
5. Focused Workspace or Panel
6. Active Workspace
7. Global Project Command
8. Application Command
```
命令被前一级接收后不得继续传播。
例如：
```text
Delete in an Inspector text field
    -> delete text
    -> do not delete an Arrangement Segment
```
### 20.2.3 F6 区域导航
```text
F6       -> next main UI region
Shift+F6 -> previous main UI region
```
顺序：
```text
Global Toolbar
Project Panel
Active Workspace
Inspector
Bottom Panel
```
隐藏或折叠区域跳过。
### 20.2.4 Tab
Tab / Shift+Tab 只在当前焦点范围内导航普通控件。海量 Timeline 对象不逐个成为 Tab Stop。
Function Code Editor 中 Tab 用于代码缩进；离开编辑器使用鼠标或 F6。
### 20.2.5 焦点恢复
Workspace 记住当前 Project 会话中的最近焦点位置。目标失效时回退到最近有效父区域。
后台验证、编译、播放、任务和 Status Bar 更新不得抢夺焦点。
模态窗口关闭后恢复到：
1. 启动 Dialog 的安全控件；
2. 原 Active Workspace；
3. 主窗口安全默认区域。
不得恢复到已删除或失效对象。
---
## 20.3 Selection Model
### 20.3.1 Workspace Selection
每个 Workspace 独立保存：
```text
Object Selection Set
Primary Selection
Selection Anchor
Time Range Selection
Active Selection Scope
```
Active Workspace Selection 是 Global Inspector 的默认 Project 对象上下文。
Project Panel、Diagnostics、Tasks 和 Library List 等保留自己的选择，不与 Workspace Selection 混合。
### 20.3.2 Context Object
Workspace Context Object 与子对象选择分离。
无子对象选择时 Inspector 显示 Context Object。
### 20.3.3 祖先与后代
同一 Selection Set 中不允许同时包含祖先对象与其后代。
### 20.3.4 异类对象
允许异类对象同时选择，但只有所有对象共同支持且语义一致的操作可用。不得静默忽略不兼容对象。
### 20.3.5 单击
```text
Click unselected object -> replace Selection
Click selected object   -> preserve multi-selection and make it Primary
```
### 20.3.6 修饰键
Timeline Canvas：
```text
Ctrl+Click  -> toggle item
Shift+Click -> add item when range semantics are not defined
```
一维有序列表使用标准连续范围选择。
### 20.3.7 空白区域
```text
Left-click empty canvas  -> clear Object Selection only
Right-click empty canvas -> preserve Selection; menu targets container and click position
```
Time Range 不因空白左键自动清除。
### 20.3.8 Marquee
初版支持矩形框选，不要求 Lasso。
命中规则：与矩形相交即命中。
```text
No modifier -> replace
Shift       -> add
Ctrl        -> toggle
Alt         -> remove
```
### 20.3.9 Select All
`Ctrl+A` 只选择当前 Active Selection Scope，不存在“选择整个 Project”。
### 20.3.10 不可见对象
| 不可见原因 | Selection 行为 |
|---|---|
| Off-screen | 保留 |
| Collapsed parent | 保留 |
| Collection Search 隐藏 | 保留并提示 |
| Editor Content Filter 隐藏 | 从 Active Selection 移除 |
| 对象真正删除 | Selection 失效 |
active crop window 外对象保持可选择和编辑。
### 20.3.11 重叠对象
重叠对象默认选顶层。
```text
Alt+Click -> cycle overlapping objects
```
Context Menu 可以列出重叠对象供用户选择。
### 20.3.12 创建与删除后的 Selection
Create、Duplicate 和 Paste 成功后选择新对象。
Timeline Delete 后默认清空当前 Object Selection。
### 20.3.13 Object Selection 与 Time Range
二者独立共存，并提供显式相互转换命令；不得隐式互相覆盖。
### 20.3.14 播放期间
允许完整选择、框选和查看，但不允许修改 Project。
所有 Selection 状态均属于 Project Session UI State。
---
## 20.4 Multi-selection 与 Batch Editing
### 20.4.1 原子原则
```text
One user gesture
-> one valid batch result
-> one Project Undo operation
```
任一对象不兼容或结果非法时整体拒绝，不允许部分成功；第 20.4.4 节明确规定的 Note pitch 越界删除属于该移动命令的正式批量结果，不视为静默跳过或部分失败。
### 20.4.2 Primary Selection
Primary Selection 是 Snap、对齐和直接拖动的参考对象。
批量 Snap：
```text
Snap Primary Selection
-> apply one shared delta to all selected objects
```
不逐对象独立吸附。
### 20.4.3 时间移动
同一时间坐标系对象可统一移动。
异类选择需要显式 `Move in Time`，不得通过拖动某个对象主体隐式混合移动。
### 20.4.4 Logical Notes
多选移动保持：
```text
Relative time
Pitch intervals
Length
Velocity
```
时间、Track/Lane 与所属容器边界仍使用整组共同合法边界。普通移动 Logical Note 或 Template Note 时，pitch 使用用户请求的共同 delta；结果 pitch 小于 0 或大于 127 的 Note 直接从 Project 删除，其余 Note 保持共同 delta 和相对关系继续移动。该删除与移动构成一个原子 Project command 和一个 Undo；Undo 必须按原顺序恢复被删除 Note。不得把越界 Note 存入模型，也不得弹出逐 Note 错误 Dialog。

Note 的 `Ctrl+Drag` 复制仍按第 20.5.6.1 节使用选择集共同 pitch clamp，不删除源对象或生成部分副本。其他可移动 Timeline 选择若请求 delta 越过时间、Track/Lane 或所属容器硬边界，使用共同 clamp 后的 delta 使整个选择贴合边界。
多选边缘调整采用同一 Edge Delta；初版不做比例时间伸缩。
### 20.4.5 数值编辑
必须区分：
```text
Exact Set
Relative Adjust
```
Enum 只支持统一设值，不支持相对数值 Delta。
Parameter Point 只有同 Definition、同类型、同值域和同语义时才能共同纵向调整。
### 20.4.6 Segment 跨 Track
保持 Track 相对间距，并保留全部内容和 crop 外数据。
可能断裂的 Lane：
- 不自动修复；
- 不按名称匹配；
- 不删除；
- 在预览中显示影响。
### 20.4.7 对齐命令
初版有限支持：
```text
Align Starts to Primary
Align Ends to Primary
Set Same Length as Primary
```
初版不做：
```text
General Batch Rename
Proportional Stretch
Normalize
Randomize
Humanize
Independent Snap
```
### 20.4.8 Delete
批量 Delete 只弹一次汇总确认。任何不可删除对象都使整体命令 Disabled。
### 20.4.9 Undo
一次批量手势只形成一个 Undo。Undo 恢复每个对象各自原状态；Redo 不根据当前 Grid 或 Snap 重新计算。
---
## 20.5 Drag and Drop Conventions
### 20.5.1 分类
```text
Direct Edit Drag
Reorder Drag
Move Drag
Copy Drag
Semantic Drop
External File Drop
View-only Drag
```
### 20.5.2 默认语义
普通内部对象：
```text
Drag      -> Move
Ctrl+Drag -> Copy only when target explicitly supports Copy
Alt       -> temporarily bypass Timeline Snap
```
Alt 不承担复制或引用语义。
### 20.5.3 Feedback
拖动反馈显示：
```text
Object count
Operation type
Target
Position or order
Validity
Important semantic impact
```
### 20.5.4 原子性
多对象拖放原子完成，不允许：
```text
Partial success
Skipping incompatible objects
Automatically finding a nearby legal position
Automatically repairing references
```
Escape、Pointer Capture 丢失、源对象失效等取消拖动并完整恢复，不产生 Undo。
### 20.5.5 自动滚动
Timeline 和列表支持边缘自动滚动。Folder 可停留后临时展开。
初版不通过悬停 Workspace Tab 自动切页，也不直接跨 Workspace 拖动内部内容。
### 20.5.6 具体对象
#### 20.5.6.1 Segment 与 Piano Roll Note
```text
Arrangement Segment body Ctrl+Drag -> copy complete selected Segments
Segment Note body Ctrl+Drag       -> copy selected Logical Notes
SubVoice Note body Ctrl+Drag      -> copy selected Template Notes
```
Segment 全部子对象获得新稳定 ID；Logical Note 获得新稳定 ID；Template Note 及其 Mapping Chain/Step 获得新稳定 ID。跨 Track 不自动重绑或删除 Lane。Note 副本使用一个共同时间/pitch delta，并保持相对时间、音程、长度、velocity、Mapping 与目标设置。成功后只选择副本，一次完整手势只形成一个 Project Undo。

复制意图在 Draw 模式的主体拖动越过阈值时确认；未越过阈值的 `Ctrl+Click` 仍按选择切换处理，边缘 `Ctrl+Drag` 仍是 Resize。时间、pitch 与 Track 越界请求使用选择集共同 clamp；Escape、Pointer Capture 丢失、Segment overlap、选择集不兼容或共同 clamp 后仍非法时整体取消，不创建部分副本。
#### 20.5.6.2 Event Instrument
Library 内只支持 Move / Reorder；Duplicate 使用显式命令。
拖到 Logical Tracks 空白目标表示创建 Track；拖到既有 Track Header 表示绑定该 Track，不移动 Instrument 本身。覆盖已有不同绑定前必须明确确认 rebind。
#### 20.5.6.3 Logical Track
Track Header 通过拖动重排并显示插入线；初版不使用 Ctrl+Drag Track 复制。Track Header 右键菜单提供 Rename、Bind / Unbind、Delete、Move Up / Down；hover 与 pressed 只属于 transient UI state。
#### 20.5.6.4 SubVoice、Mapping 等有序结构
使用插入线重排，保持稳定 ID。
#### 20.5.6.5 Workspace Tab
只在主窗口内重排，不支持拖出。
### 20.5.7 外部文件
```text
.midora or candidate .zip dropped on Main Window -> Open Project flow
SF2 dropped on explicit SoundFont target          -> Embed / External Relative Reference flow
```
初版不通过拖放导入：
```text
MIDI
Audio
Code
Arbitrary files
Folder
Standalone Event Instrument file
```
初版不支持把 Project 对象拖出到 Windows Explorer。
### 20.5.8 播放期间
只允许 View-only Drag。所有 Project 修改拖放 Disabled。
重要拖放必须有可见按钮、菜单或其他正常键鼠入口，但不要求纯键盘完整替代。
---
## 20.6 Clipboard Object Formats
### 20.6.1 Clipboard 上下文
```text
Text Clipboard Context
Code Clipboard Context
Project Object Clipboard Context
Read-only Information Clipboard Context
```
### 20.6.2 Project Object Payload
Project 对象 Copy 可以同时写入：
```text
Midora Internal Object Payload
Plain Text Summary
```
初版只使用当前 Windows Clipboard，不提供内部历史或多槽。
### 20.6.3 Project 会话有效性
Project Object Payload 只在当前来源 Project 会话有效。
关闭或替换 Project 后失效，不支持跨 Project Paste。
### 20.6.4 快照与 ID
Clipboard 是 Copy 时的不可变快照。
每次 Paste：
- 生成新的稳定 ID；
- 内部引用重映射到新副本；
- 允许的外部引用保留原稳定 ID；
- Context Reference 改为目标上下文；
- Broken Reference 保留原 ID 和 Last Known Name。
不按名称自动修复。
### 20.6.5 初版支持的普通对象
```text
Segment
Logical Note
Logical Parameter Lane, Point and Curve content
SubVoice timeline events
Ordinary Conductor events
Compatible ordered content
```
以下高层定义使用显式 Duplicate 或专用命令，不进入普通 Clipboard：
```text
Event Instrument
Logical Track
SubVoice Definition
Logical Parameter Definition
Mapping Function Definition
Project Settings
SoundFont
```
### 20.6.6 Segment Payload
包含：
```text
Active crop window
All Logical Notes
All Logical Parameter Lanes, Points and Curves
Hidden content outside the crop window
Broken and inapplicable data
```
### 20.6.7 Arrangement Paste
```text
Earliest source Segment -> align to Edit Cursor
Primary source Track    -> map to Active Target Track
Other source Tracks     -> preserve relative track offsets
```
任一目标 Track 不存在或 Segment 约束冲突时整体失败，不自动找空位。
### 20.6.8 Logical Parameter 内容
不按名称、类型或显示范围自动匹配。
独立 Parameter 内容只有 exact target 有效时才可 Paste。
必须区分：
```text
Whole Lane
Lane Content
```
### 20.6.9 SubVoice
跨 SubVoice Paste 初版仅限同一个 Event Instrument，并保持合法 Mapping Function 外部引用。
### 20.6.10 Cut
Cut = 成功写入 Clipboard 后删除源对象。
Cut + Paste 是 Delete + Create，不保持对象身份。
Cut 和 Paste 分别形成独立 Undo；Clipboard 本身不受 Undo 影响。
### 20.6.11 Timeline 对齐
Timeline Payload 最早 tick 对齐 Edit Cursor；`Paste Here` 使用右键位置。
Paste 后 Clipboard 保留；不自动增加 Grid Step 偏移。
### 20.6.12 Code Editor
C# Code Editor 只粘贴 Plain Text 到 Draft Buffer，不自动 Apply。
外部文本、文件和未知格式不被 Workspace 猜测转换成 Midora 对象。
### 20.6.13 播放期间
允许 Copy；禁止 Cut 和 Project Paste。
---
## 20.7 Context Menus
### 20.7.1 定位
Context Menu 是现有命令的辅助入口，不建立第二套命令语义。
```text
Context Menu Command
-> Existing Routed Command
-> Same Validation
-> Same Undo / Redo semantics
-> Same locking rules
```
### 20.7.2 统一分组
```text
[A] Primary Object Actions
[B] Edit and Clipboard Actions
[C] Navigation and Inspection
[D] Destructive Actions
```
Delete 等危险命令固定放在底部并与普通编辑命令分组隔开。
### 20.7.3 右键与 Selection
```text
Right-click selected object   -> preserve current multi-selection
Right-click unselected object -> replace with single clicked object
Right-click empty area        -> preserve Selection; target container and click position
```
右键空白区域的菜单不默认作用于旧 Selection。
### 20.7.4 固定目标
菜单打开时固定：
```text
Clicked object or Selection
Clicked container
Clicked timeline position
Active Workspace
```
Hover、Inspector 刷新或后台诊断更新不得改变命令目标。
时间位置命令使用打开菜单时记录的 tick。
### 20.7.5 Hide 与 Disabled
语义完全无关的命令隐藏。
通常存在但因播放、锁定、剪贴板或对象状态暂时不可用的命令保持可见并 Disabled，必要时说明原因。
固定顶层节点永远不支持的 Delete / Rename 不显示。
### 20.7.6 非唯一入口
重要功能不能只存在于 Context Menu，例如：
```text
Save Project
Compile
MIDI Export
Audio Render
Create Event Instrument
Create Logical Track
Project Settings
Open Diagnostics
Resolve Broken Reference
```
### 20.7.7 多选
只显示对全部对象都合法的共同命令。
初版不采用“处理能执行的对象并跳过其他对象”的隐式部分成功模式。
### 20.7.8 Clipboard
Paste 是否可用由以下共同决定：
```text
Clipboard object type
Target container
Target timeline position
Current Project state
Task and playback lock
```
不提供目标不可预测的通用 Paste。
### 20.7.9 危险文案
使用具体名称：
```text
Delete Event Instrument
Remove Instrument Binding
Delete All Lane Events
Reset Property to Inherited
Clear Runtime History
```
不得用模糊 `Remove / Clear / Reset` 混淆不同语义。
### 20.7.10 主要菜单边界
#### 20.7.10.1 Event Instrument
```text
Open
Preview
Create Logical Track
Duplicate
Rename
Show References
Show Details
Delete
```
#### 20.7.10.2 Logical Track
```text
Open in Arrangement
Open Instrument
Duplicate
Rename
Change Instrument
Show Details
Delete
```
#### 20.7.10.3 Segment
```text
Open in Segment Editor
Cut
Copy
Duplicate
Split at Cursor
Show Details
Delete
```
Segment 无 `Rename`。
#### 20.7.10.4 Logical Note
```text
Cut
Copy
Duplicate
Edit Properties
Show Details
Delete
```
不显示 Legato 命令。
#### 20.7.10.5 Logical Parameter Lane
```text
Show Parameter Definition
Show Mapping
Hide Lane
Delete Lane Data
Show Details
```
#### 20.7.10.6 Broken Lane
```text
Show Broken Reference
Choose Replacement
Go to Last Known Instrument
Delete Lane Data
```
不得按名称自动修复。
#### 20.7.10.7 SubVoice
```text
Open
Preview Selected SubVoice
Duplicate
Rename
Show References
Show Details
Delete
```
最后一条 SubVoice 的 Delete 不显示。
#### 20.7.10.8 Mapping Function
```text
Open Function
Apply Draft
Discard Draft
Duplicate
Rename
Show References
Delete
```
删除有 Draft 的 Function 前必须先处理 Draft。
#### 20.7.10.9 Diagnostic
```text
Go to Source
Show Details
Copy Message
Copy Source Path
Mark as Reviewed
```
初版不提供永久 Suppress 或 Delete Diagnostic。
#### 20.7.10.10 Workspace Tab
```text
Close Tab
Close Other Tabs
```
关闭 Tab 只关闭界面。
### 20.7.11 播放与任务锁定
播放期间允许 Open、Show Details、Go to Source、Copy 和纯查看命令；Project 编辑命令 Disabled。
模态任务和 Audio Render 完全锁定期间，主窗口 Context Menu 不可用。
---
## 20.8 Naming and Inline Rename
### 20.8.1 Inline Rename
```text
+--------------------------------------+
| [A] Object Icon                      |
| [B] Inline Name Field                |
| [C] Validation Indicator             |
+--------------------------------------+
```
入口：
```text
F2
Context Menu > Rename
Inspector Name Field
Visible Rename command
```
双击保留给 Open，不用于 Rename；不使用慢速第二次单击进入 Rename。
多选时 Rename Disabled。
### 20.8.2 本地缓冲
```text
Begin Rename
-> edit local text buffer
-> validate
-> commit or cancel
```
输入过程中不实时写入 Project。
```text
Enter  -> validate and commit
Escape -> cancel and restore
Valid focus loss   -> commit
Invalid focus loss -> restore old name
```
无实际变化不创建 Undo，也不标记 Modified。
### 20.8.3 字符规则
提交时去除首尾空白；内部连续空格保留。
允许 Unicode。
对象名称不受 Windows 文件名字符规则限制。
不允许：
```text
Newline
NUL
Control characters that break single-line or persistence semantics
```
第 17～20 章的 UI 与交互规格 不任意规定最大长度；若最终 schema 有上限，所有入口统一验证并禁止静默截断。
### 20.8.4 名称表
| 对象 | 是否可空 | 唯一性 | Rename |
|---|---:|---|---:|
| Project Name | 是 | 不适用 | Project Settings |
| Event Instrument | 否 | Project 内唯一 | 是 |
| Logical Track | 是 | 允许重复 | 是 |
| Segment | 无名称 | 不适用 | 否 |
| SubVoice | 是 | 允许重复 | 是 |
| Logical Parameter | 否 | Event Instrument 内唯一 | 是 |
| Mapping Function | 否 | Event Instrument 内唯一 | 是 |
| Envelope Preset | 是 | 允许重复 | 是 |
| Marker | 是 | 允许重复 | 是 |
| Project End Marker | 固定名称 | 不适用 | 否 |
| Library Folder | 否 | Library 内唯一 | 是 |
| Fixed top-level node | 固定名称 | 不适用 | 否 |
| Damaged Placeholder | 名称快照只读 | 不适用 | 否 |
| Broken Last Known Name | 只读提示 | 不适用 | 否 |
要求唯一的名称比较：
```text
Trimmed
Case-insensitive
```
### 20.8.5 自动显示标签
Logical Track 名称为空：
```text
Logical Track <Display Index>
```
SubVoice 名称为空：
```text
SubVoice <Display Index>
```
Envelope 名称为空：
```text
Envelope <Display Index>
```
Marker 名称为空可显示位置摘要。
自动标签不持久化，不作为身份，顺序变化时可更新。
### 20.8.6 唯一冲突
冲突时：
- 不提交；
- 保持 Inline Rename；
- 显示具体作用域；
- 不自动追加数字；
- 不覆盖、交换或合并对象。
### 20.8.7 Duplicate 名称
要求唯一的对象 Duplicate 时自动生成合法名称，例如：
```text
Kick
Kick Copy
Kick Copy 2
```
自动名称与 Duplicate 属于同一个 Project Undo。
允许重名的对象可以保留原名称。
### 20.8.8 全局同步
Rename 后同步更新：
```text
Project Panel
Workspace Tab
Editor Header
Inspector
Library
Reference List
Diagnostics Source Path
Search Index
Picker
Export and Render name preview
```
引用继续基于稳定 ID。
临时 Name Sort 下对象可以移动到新显示位置，但正式手动顺序不变。
任务开始后使用冻结名称快照；任务中途不改变输出名。
### 20.8.9 Undo
一次成功 Rename 形成一次 Project Undo。
Inline 字段有焦点时 Ctrl+Z / Ctrl+Y 操作本地文本；提交后由 Project Undo 撤销名称。
---
## 20.9 Search Behavior
### 20.9.1 初版搜索类型
```text
Collection Filter
Diagnostics Search
Find in current C# Function
```
初版不提供：
```text
Global Project Search
Full-text Project Search
Search all MIDI events
Search Notes by pitch or velocity
Search Segments by content
Search Logical Parameter Points
Find in all Mapping Functions
Saved Search
Fuzzy Search
Regular Expression Project Search
Project-wide Replace
```
### 20.9.2 Collection Search
统一规则：
```text
Case-insensitive
Literal substring match
Trim query leading and trailing whitespace
Preserve current view order
No relevance reordering
```
不解释通配符、逻辑运算符、字段语法或正则表达式。
支持 Unicode。
### 20.9.3 状态归属
搜索词和筛选属于 Project Session UI State：
- 不保存进 `.midora`；
- 不进入 Undo / Redo；
- 不标记 Modified；
- 不跨应用重启；
- 不保存搜索历史或建议。
### 20.9.4 Selection
搜索不自动选择第一项，不自动打开 Workspace，不改变播放光标。
已选对象被 Collection Search 隐藏时：
- 通过稳定 ID 保持 Selection；
- 不自动选择其他结果；
- Inspector 可继续显示；
- 显示 Hidden Selection Notice；
- `Show Selected` 可清除文字查询以重新显示对象。
### 20.9.5 键盘
```text
Ctrl+F in Function Editor -> Find in Draft Buffer
Ctrl+F in searchable collection -> focus local Search field
Ctrl+F in Diagnostics -> focus Diagnostics Search
Other context -> No Action
```
Search Field：
```text
Escape with query -> clear text query
Enter with selected result -> open or activate
Enter with no selected result -> no automatic open
Down -> first visible result
Up   -> last visible result
```
### 20.9.6 Project Panel 与 Library
Project Panel 搜索：
```text
Instrument Name
Folder Name
Track Name
Last Known Instrument Name
```
Library 搜索：
```text
Instrument Name
Folder Name
```
Folder 本身匹配时显示完整子内容；子对象匹配时显示必要祖先。
筛选期间禁用正式 Reorder、Folder Move 和 Drag to Rebind。
### 20.9.7 Diagnostics Search
匹配：
```text
Diagnostic Message
Diagnostic Code
Source Object Name
Source Path
Last Known Source Name
```
与 Severity、Source 和 Status Filter 取交集。
Global Status Bar 的 Whole Project 计数不受搜索影响。
### 20.9.8 Object Picker
Picker 搜索只作用于当前合法对象类型。
名称可能重复时显示足够上下文，例如 Track 顺序、绑定 Instrument 或 Envelope 所属 Instrument。
多选 Picker 中，被搜索隐藏的已勾选对象保持选中，并显示总选择数量。
### 20.9.9 Function Find
作用于当前 Draft Buffer：
```text
Ctrl+F       -> open Find
Enter        -> next match
Shift+Enter  -> previous match
Escape       -> close Find
```
初版至少支持 Plain Text、Case-sensitive Toggle 和 Whole Word Toggle，不要求 Regex。
### 20.9.10 播放与任务
播放期间允许搜索和导航结果，但 Project 编辑命令仍 Disabled。
模态任务期间主窗口搜索不可用；Dialog 可以拥有自己的本地搜索。
Audio Render 完全锁定期间不允许主窗口搜索。
---
## 20.10 Empty、Disabled、Loading、Error 与 Damaged States
### 20.10.1 状态分类
必须严格区分：
| 状态 | 含义 |
|---|---|
| Empty | 合法对象或集合当前无内容 |
| Disabled | 功能存在但当前上下文不可操作 |
| Loading | 正在读取、编译或准备 |
| Error | 操作或资源失败 |
| Damaged | 持久化内容无法正常读取 |
| No Results | 搜索或筛选无匹配 |
同一空白画面不得同时表达这些不同状态。
### 20.10.2 Empty State 统一结构
```text
+------------------------------------------------------------+
| [A] Empty State Title                                      |
| [B] Explanation                                            |
| [C] Primary Action                                         |
| [D] Secondary Navigation                                   |
+------------------------------------------------------------+
```
窄面板使用紧凑提示，不强制大面积居中。
### 20.10.3 全局规则
- 不自动创建 Event Instrument、Track、Segment、Note、Lane、Envelope 或 Function；
- 不使用虚假示例内容；
- Primary Action 调用正式创建命令，保持相同默认值、验证、Undo 和锁定规则；
- 播放锁定时按钮保持可见但 Disabled，并说明原因；
- Empty State 不是 Project 对象，不进入 Selection、Inspector 或 Undo。
### 20.10.4 无 Project
```text
No Project Open
Create a new project or open an existing one.
[ Create Project ]
Open Project
```
Transport、Compile、Save、Export、Render 和 Undo / Redo Disabled。
### 20.10.5 无 Workspace
```text
No Workspace Open
Open an item from the Project Panel to edit it.
[ Open Arrangement ]
Browse Project Panel
```
Project 仍然打开，Save、Compile 和输出命令仍按 Project 状态可用。
### 20.10.6 Arrangement 无 Track
```text
No Logical Tracks
Create a track to begin arranging your project.
[ Create Logical Track ]
Open Event Instrument Library
```
允许创建未绑定 Event Instrument 的 Track，不强制先创建 Instrument。
### 20.10.7 Library 为空
```text
No Event Instruments
Create an event instrument to define reusable events.
[ Create Event Instrument ]
```
创建最小合法 Event Instrument：唯一名称、一条空 SubVoice、不自动生成 MIDI 事件。
### 20.10.8 Track 无 Segment
Track Timeline 内显示紧凑提示，不覆盖整个 Arrangement。
```text
No segments
Double-click or use Draw to create
```
未绑定时补充 `This track has no event instrument assigned.`
### 20.10.9 Segment 无内容
Note 区与 Parameter 区分别显示自己的空状态。
如果 Event Instrument 无 Logical Parameter：
```text
No logical parameters are exposed by this event instrument.
```
空 Segment 仍是合法对象，不自动删除。
### 20.10.10 Event Instrument 可选集合为空
分别显示：
```text
No logical parameters
No mapping functions
No envelope presets
No mappings
```
空集合不是 Warning。
Incompatible 数据不是 Empty，必须保留并显示 Disabled / Incompatible。
### 20.10.11 Diagnostics 无项目
当前集合为空：
```text
No diagnostics
No issues are currently reported.
```
筛选无结果：
```text
No matching diagnostics
[ Clear Filters ]
```
不得用绝对语句保证 Project 一定可编译。
### 20.10.12 Tasks 为空
```text
No tasks
Compile, export, and render activity will appear here.
```
不提供启动任务的 Primary Action。
### 20.10.13 无 SoundFont
```text
No SoundFont Selected
MIDI editing, compilation, and MIDI export remain usable.
Playback, preview, and audio rendering are unavailable.
[ Select SF2 ]
```
这是合法状态，不使用 Error 图标。
### 20.10.14 无启用音频输出设备
```text
No Enabled Audio Output Device
Playback and preview are unavailable. Audio file rendering remains available.
[ Refresh Devices ]
```
这是本机运行环境状态，不是 Project Error，不影响编辑、编译、保存、MIDI 导出或文件音频渲染。

### 20.10.15 Search 无结果
```text
No results for "query"
[ Clear Search ]
```
不自动放宽条件或模糊替换。
### 20.10.16 Damaged Content
```text
Content Cannot Be Opened
This object could not be loaded as a normal project item.
[ Open Diagnostics ]
Remove Object
```
不得显示普通空编辑器，也不允许在 Placeholder 中创建正常子对象。
---
## 20.11 Error and Validation Presentation
### 20.11.1 呈现层级
```text
[A] Field Validation
[B] Object Status
[C] Workspace Validation Summary
[D] Global Notice Bar
[E] Global Status Bar
[F] Diagnostics Panel or Workspace
[G] Task Result Presentation
[H] Blocking Dialog
```
原则：使用最小充分层级，不对普通字段错误滥用弹窗。

Global Status Bar 允许为布局而省略长文本，但 Error 状态必须同时提供详情入口。详情窗显示该状态消息的完整原文，文本可选择并可通过 `Ctrl+C` 或显式 Copy 操作复制；打开详情不清除状态消息，也不改变 Project。
### 20.11.2 Field Validation
未提交输入错误：
- 显示在字段附近；
- 保留输入供修正；
- 不写入 Project；
- 不产生 Undo；
- 不生成 Whole Project Diagnostic；
- 不弹 Blocking Dialog。
提交失败必须说明具体约束，例如：
```text
Value must be between 0 and 127.
End Tick must be greater than Start Tick.
An event instrument named "Kick" already exists.
```
跨字段关系错误同时标记直接相关字段，并显示一条统一说明。
### 20.11.3 Object Status
对象状态至少区分：
```text
Error
Warning
Information
Broken Reference
Incompatible
Damaged
Unused
Draft
```
不得只依赖颜色；至少使用图标、短标签和 Tooltip / Details。
父对象可聚合子对象问题，但不复制所有完整消息。
Timeline 对象的问题不能用整块纯红色遮挡内容、Selection 或曲线形状。
### 20.11.4 Workspace Summary
复杂 Workspace 提供当前对象范围的验证摘要：
```text
2 Errors   1 Warning
[ Open Diagnostics ]
```
无问题时使用：
```text
No issues are currently reported.
```
不得保证“对象完全正确”或“必然编译成功”。
### 20.11.5 Severity 与 Outcome
严重级别：
```text
Error
Warning
Information
```
任务结果：
```text
Succeeded
Succeeded with Warnings
Blocked
Failed
Cancelled
Completed With Errors
```
两者严格分离。
例如 Warning 因策略阻止编译时：
```text
Severity: Warning
Outcome: Compile Blocked by Policy
```
### 20.11.6 诊断过期
Project 发生相关修改后，旧结果必须显示为 Outdated、Resolved、Previous Compile 或 Runtime History，不能继续伪装为当前结果。
旧结果不得决定当前 Play、Export 或 Render 是否允许开始。
### 20.11.7 C# Draft 与 Applied Version
Draft 本地错误显示在 Code Editor，并标注 Draft。
Project Diagnostics 对应 Applied Version。
未引用 Function 的代码错误不阻止整曲编译；实际编译路径使用的 Function 错误按编译规则成为 Error；运行时异常中止当前流程并进入 Runtime History。
### 20.11.8 Broken、Incompatible 与 Damaged
#### 20.11.8.1 Broken Reference
目标不存在；显示 Last Known Name 和预期类型，可选择 Replacement 或删除 Broken Data。
#### 20.11.8.2 Incompatible
数据存在，但当前设置使其不可用；保留并 Disabled，兼容设置恢复后继续使用。
#### 20.11.8.3 Damaged
持久化内容无法读取；使用专用 Damaged 页面，不能显示为空对象。
### 20.11.9 Task Validation
配置阶段：
- 字段错误就地显示；
- Review 汇总阻止开始的问题；
- Start Disabled；
- 提供 Go to Setting；
- 不连续弹多条错误 Dialog。
正式任务阶段：问题进入 Task Log、Task Diagnostics 和 Output Item Status。
### 20.11.10 Blocking Dialog
仅用于无法安全继续且用户必须确认或选择的情况，例如：
```text
Project cannot be opened
Save failed
Current project cannot be closed safely
File format is newer than this Midora version
An unrecoverable playback error stopped playback
```
按钮使用具体动作：
```text
Retry Save
Choose Another Location
Open Diagnostics
Return to Project
Close
```
不使用模糊 `Yes / No / Continue`。
### 20.11.11 Runtime Presentation
性能和状态提示：
```text
Buffer Underrun
Clipping
Limiter Activity
Temporary device issue
Audio cache retention disabled
Audio cache entry corrupt and rebuilt
```
显示于 Status Bar、Bottom Runtime Panel 或 Runtime History；不自动成为 Project Error。
不可恢复错误执行 Stop 类清理，保留来源和上下文，不修改 Project。
Buffering 是可恢复播放状态，不弹 Error Dialog。
### 20.11.12 非干扰
后台验证、编译完成和诊断刷新不得：
```text
Steal keyboard focus
Open Diagnostics Workspace automatically
Change Active Workspace
Change Selection
Scroll current editor
Expand every error node
```
### 20.11.13 声音与 Toast
初版：
```text
No error sounds
No success sounds
No diagnostic audio cues
No celebration animations
No general-purpose toast system requirement
```
短暂成功信息使用 Status Bar；持续状态使用 Notice Bar。
---
## 20.12 Shortcut Conflict Resolution
### 20.12.1 初版快捷键表
| 快捷键 | 命令 | 作用范围 |
|---|---|---|
| `Ctrl+N` | New Project | Global |
| `Ctrl+O` | Open Project | Global |
| `Ctrl+S` | Apply Function Draft / Save Project | Focus-sensitive |
| `Ctrl+Shift+S` | Save Copy | Global |
| `Ctrl+Z` | Undo | Focus-sensitive |
| `Ctrl+Y` | Redo | Focus-sensitive |
| `Ctrl+X` | Cut | Focus-sensitive |
| `Ctrl+C` | Copy | Focus-sensitive |
| `Ctrl+V` | Paste | Focus-sensitive |
| `Ctrl+A` | Select All | Focus-sensitive |
| `Ctrl+D` | Duplicate Selection | Focused object scope |
| `Ctrl+F` | Local Search / Find | Focus-sensitive |
| `Ctrl+Tab` | Next Workspace Tab | Main Window |
| `Ctrl+Shift+Tab` | Previous Workspace Tab | Main Window |
| `F2` | Rename Focused Object | Focused object scope |
| `Delete` | Delete Focused Selection | Focused object scope |
| `F4` | Next Active Diagnostic | Main Window |
| `Shift+F4` | Previous Active Diagnostic | Main Window |
| `F6` | Next Main UI Region | Main Window |
| `Shift+F6` | Previous Main UI Region | Main Window |
| `D` | Draw Tool | Active Arrangement / Segment / SubVoice Timeline Workspace |
| `S` | Select Tool | Active Arrangement / Segment / SubVoice Timeline Workspace |
| `E` | Erase Tool | Active Arrangement / Segment / SubVoice Timeline Workspace |
| `Space` | Play / Stop | Explicit Timeline Context only |
| `Escape` | Cancel innermost temporary state | Context-sensitive |
| `Alt+F4` | Close active window / Exit request | Application or active Dialog |
初版不增加表外的其他默认全局快捷键。
### 20.12.2 Ctrl+S
```text
Function Code Editor focus -> Apply Function Draft
Other Project context      -> Save Project
```
Function Editor 接收 Ctrl+S 后，即使 Apply 无法执行，也不得回退为 Save Project。
Global Save Button 和 File > Save Project 始终保存 Project。
### 20.12.3 Undo / Redo
```text
Function Code Editor -> Draft Undo / Redo
Ordinary text field  -> field-local Undo / Redo
Other Project area   -> Project Undo / Redo
```
本地 Undo Stack 为空时不向 Project Undo 回退。
Global Undo / Redo 按钮只操作 Project History，并显示当前操作名称。
### 20.12.4 Clipboard 与 Ctrl+A
Text / Code 焦点下操作文本缓冲，不得误操作背景 Project Selection。
Project 对象区域使用 Project Clipboard 命令。
`Ctrl+A` 只选择当前 Active Selection Scope；无明确定义时 No Action。
### 20.12.5 Ctrl+D、Delete、F2
只针对当前焦点区域。
Text / Code Editor 不触发 Project Duplicate、Delete 或 Rename。
F2 在 Segment、Project End Marker、固定顶层节点、Damaged Placeholder、多选或文本编辑器中 No Action。
### 20.12.6 Space
主窗口没有活动 Modal、Popup、Menu 或本地编辑会话，且焦点不在 TextBox、PasswordBox、RichTextBox 或代码编辑器时：
```text
Stopped                    -> Play
Preparing / Playing / Buffering -> Stop
```
Button、Checkbox、Tree、List、Project Panel、Inspector、Diagnostics、Tasks、Library、Settings 与 Status Bar 不再优先消费 Space；这些区域的 Space 执行全局 Play / Stop。文本输入、代码输入、打开的菜单/Popup 和 Modal Dialog 仍优先处理 Space。
初版没有 Pause。
### 20.12.7 Escape
优先顺序：
```text
1. Close popup or context menu
2. Cancel drag, marquee, numeric drag or inline gesture
3. Cancel Inline Rename or ordinary field edit
4. Close local Search / Find or clear active search text
5. Cancel or close active cancellable Dialog
6. No Action
```
Escape 不默认：
```text
Clear stable Selection
Close Workspace
Close Project
Exit application
Discard Function Draft
Stop playback
Cancel Save transaction
```
Audio Render 中 Escape 打开 Cancel Rendering Confirmation。
### 20.12.8 Enter 与 Tab
```text
Text field      -> validate and commit
Inline Rename   -> validate and commit
List or tree    -> open or activate focused item
Function Editor -> insert new line
Dialog          -> enabled default action when focus control has no own Enter behavior
```
Tab / Shift+Tab 在当前焦点范围内移动；Function Editor 中 Tab 用于缩进。
### 20.12.9 Diagnostics
```text
F4       -> Next Active Diagnostic
Shift+F4 -> Previous Active Diagnostic
```
使用 Global Active Diagnostics，不受 Bottom Panel 当前搜索隐藏影响。
### 20.12.10 Alt
```text
Alt during Timeline Drag -> temporarily bypass Snap
Alt+F4                  -> system close or task-specific close request
```
初版不定义 Alt-letter Access Keys，也不注册 `Alt+Left / Alt+Right` 导航历史快捷键。
### 20.12.11 鼠标修饰键
```text
Click       -> replace Selection
Ctrl+Click  -> toggle
Shift+Click -> range or add where defined
Normal Drag -> move or reorder
Ctrl+Drag   -> copy only where explicitly supported
Alt+Drag    -> bypass Timeline Snap
```
未支持的复杂组合不猜测用户意图；无法安全解释时 Drop Invalid。
### 20.12.12 Backspace
Text / Code 中删除前一字符；其他上下文 No Action。
Backspace 不删除 Project 对象、不返回导航、不关闭 Tab。
### 20.12.13 明确不注册
```text
Ctrl+W
Ctrl+Shift+W
Ctrl+Q
Ctrl+P
Ctrl+R
Ctrl+E
Ctrl+M
Ctrl+Shift+F
Alt+Left
Alt+Right
Other letter-only tool shortcuts
Number-key tool shortcuts
Global compile shortcut
Global MIDI Export shortcut
Global Audio Render shortcut
Global Reset Playback Engine shortcut
```
特别是 `Ctrl+W` 在任何 Workspace、Dialog 或 Project 状态都完全不注册。

`D`、`S`、`E` 是上一表批准的唯一 letter-only tool shortcut。它们只在无修饰键、主窗口无活动 Modal / Popup / Menu / Inline Editing Session，且焦点不在 TextBox、PasswordBox、RichTextBox、ComboBox 或代码编辑器时生效；否则按焦点控件的文本输入或本地交互处理，不得切换背景 Workspace 工具。
### 20.12.14 无效快捷键反馈
可预期的 No Action 不弹窗、不播放声音。
持续锁定导致命令不可用时，可在 Status Bar 短暂显示原因。
---
## 20.13 UI Language and Numeric Formatting
### 20.13.1 界面语言
初版固定 English UI：
- 不提供语言选择；
- 不提供语言包；
- 不支持运行时切换。
用户名称和 Metadata 允许完整 Unicode，不自动翻译、转写、大小写或全半角转换。
### 20.13.2 数值输入
固定使用：
```text
Decimal point: .
Negative sign: -
Digits: ASCII 0-9
Thousands separator: none
```
不跟随 Windows Locale，不接受逗号小数。
普通数值字段不支持：
```text
Expressions
Fractions
Scientific notation
Unit suffixes
NaN
Infinity
```
Integer 和 Double 是明确字段类型；Integer 不接受 `1.0` 并静默转整数。
非法输入不提交、不 Clamp、不创建 Undo；失焦恢复最后合法值。
Double 显示去除无意义尾随零并避免浮点噪声，但显示格式不得修改内部值。
单位显示在字段外。
### 20.13.3 时间格式
Project 音乐位置：
```text
Bar:Beat:Tick
```
- Bar 1-based；
- Beat 1-based；
- Tick offset 0-based；
- Project 起点 `1:1:0`。
Beat 按当前 Time Signature 分母单位计算；初版不推断复合拍大拍。
每个 Time Signature 必须满足 `4 × TPQ % denominator == 0`，因此 Beat 长度和 Tick offset 均使用整数 Project tick。Time Signature 变化 tick 立即显示为新 Bar 的 `Beat 1:Tick 0`；若旧小节被截断，不存在的旧小节尾部坐标不得解析或吸附。
Segment local time 和 Template time 使用独立 tick。
长度和 Delta 使用 tick，不使用绝对位置格式。
### 20.13.4 音高与编号
```text
MIDI Note 60 -> C4
Black keys   -> sharps
Port         -> 1-16
Channel      -> 1-16
Program      -> 1-128
```
用户侧 Track 和 SubVoice 显示顺序编号使用 1-based。
### 20.13.5 Enum
主要显示 item name；显式整数值可以在 Details 中补充。
### 20.13.6 日期时间
固定：
```text
yyyy-MM-dd HH:mm:ss
```
使用本机时间和 24 小时制。
### 20.13.7 Copy 文本
Copy 出的文本使用同一固定数值和音乐位置格式。
Mixed、Inherited、Default 和 Override 使用状态文字，不用特殊数值冒充。
### 20.13.8 排序
数值列按真实数值排序；音乐位置按 absolute tick 排序。
UI 格式与 `.midora` 序列化格式严格分离。
---
## 20.14 UI Preference Persistence
### 20.14.1 保存位置
Application Preferences 自动保存于当前 Windows 用户本机。
Preference 自动保存不等于 Project Autosave。
### 20.14.2 持久化内容
```text
Window position and maximized state
Project Panel width and collapsed state
Inspector width and collapsed state
Bottom Panel height, state and last active tab
Major splitters
Follow Playback preference
Default lane height
Library list or card view
Recent directories by picker purpose
Selected playback output device ID or System Default choice
Render-Ahead Buffer
Device Buffer Request
Realtime Maximum Sample Voices per Unit Stream
Audio Cache Root
Maximum Reusable Audio Cache Bytes
```
### 20.14.3 不持久化内容
```text
Current Tool
Zoom
Scroll
Playback Cursor
Edit Cursor
Time Range
Curve and Tempo view range
Workspace Tabs
Active Workspace
Navigation History
Selection
Tree expansion
Search and filter
List scroll
Mute and Solo
Playback state
Task History
Runtime Diagnostics
Function Draft
Arrangement Grid / Snap and default creation values
Shared piano-roll Grid / Snap and default creation values
当前设备枚举结果
设备实际采样率
设备实际 buffer 和 callback period
IPC 连接与队列运行状态
Audio cache reusable 当前占用
Audio cache transient 当前与峰值占用
Audio cache session 路径、retention 状态和 Warning
```
### 20.14.4 Recent Directories
分别保存：
```text
Open Project
Save and Save Copy
SoundFont
MIDI Export
Audio Render
```
仅作为 File Picker 起始位置，不是 Project 默认输出路径。
### 20.14.5 禁止持久化的决定
以下决定绝不保存：
```text
Overwrite authorization
Close without Saving
Discard Draft
Delete confirmation
Cancel rendering confirmation
```
### 20.14.6 Preference 失败
Preference 读写失败：
- 不影响 Project；
- 使用安全默认值；
- 显示非模态 Notice；
- 不标记 Project Modified。
Preference 版本与 `.midora` File Format 版本独立。
### 20.14.7 Reset 命令
```text
View > Reset Layout
View > Reset Editor View Preferences
View > Reset All UI Preferences
```
Reset Layout：
- 不关闭 Workspace；
- 不清除 Selection；
- 不修改 Project。
Reset All UI Preferences 需要简短确认。
### 20.14.8 初版不提供
```text
Preference Import or Export
Preference Sync
Preference Profiles
Theme preference
Language preference
Accessibility preference
DPI override
```
### 20.14.9 初版默认值
```text
Project Panel: Visible
Inspector: Visible
Bottom Panel: Collapsed with Diagnostics active
Follow Playback: Enabled
Current Tool: Select
Playback Output Device: System Default
Render-Ahead Buffer: 100 ms
Device Buffer Request: 50 ms
```
---
## 20.15 Window Sizing and 100% DPI Boundary
### 20.15.1 正式验收环境
```text
Windows Desktop
100% Display Scaling
Midora Built-in Theme
```
### 20.15.2 主窗口行为
支持标准：
```text
Move
Resize
Minimize
Maximize
Restore
Close
```
首次启动使用安全普通尺寸，不默认最大化。具体像素值在原型阶段决定。
必须有正式最小尺寸；达到最小时仍保证：
```text
Main Menu
Core Transport
At least one Workspace Tab
Usable Active Workspace area
```
### 20.15.3 滚动与面板
整个应用不出现全局二维 Scrollbar；滚动只存在于具体内容区域。
Project Panel、Inspector、Bottom Panel 和 Active Workspace 均有最小可用尺寸。
窗口缩小时不自动永久改变用户折叠偏好。
### 20.15.4 Toolbar 与 Tabs
Toolbar 宽度不足时使用 Overflow。
Workspace Tabs 单行，使用滚动和 Tab List。

Timeline Toolbar 的 Grid / Snap 选择框只显示 `Bar` 或简写分数（例如 `1/8`），选择后显示文本必须立即更新并与实际生效值一致；不得因可编辑文本与选择项绑定冲突而显示额外错误色块、空选择或完整说明文字。Arrangement、Segment 和 SubVoice 的顺序统一为 `Grid + 下拉 | Snap + 下拉 | Length [Vel] | - + | 工具`，其中 Arrangement 不显示不适用的 Vel；各组之间显示分割线。

Arrangement Segment 使用较深的低饱和蓝灰色；选中 Segment 使用同色系强调边框和更深背景，Note Preview 使用高亮但低饱和的蓝灰色。Segment Piano Roll 的 active range 保留基础键位底色，界外范围进一步压暗；未选中 Note 使用高亮蓝灰色，选中 Note 的红色填充与红色边框保持不变。Velocity 未选中柱使用相同蓝灰色，选中 Note 对应柱使用红色；每个 Note 只在 start tick 显示固定窄柱，柱顶显示明显更宽的方形 onset marker，柱宽不得随 Note 长度变化。Piano Roll 白键行使用较亮底色、黑键行使用较暗底色；Segment 与 SubVoice Pitch Ruler 使用完整白键和较短黑键的钢琴外观，并且只在每个八度 C 键显示符合 MIDI 60 = C4 的音名。空 Timeline 不显示覆盖画布的 `No timeline content` 卡片。Disabled Ghost Button 不保留背景或边框。Transport 的位置与 BPM 使用亮色并以竖向分割线分隔；Play 图标不得裁切。Parameter / Event Lane 不显示额外白色外框。数值标尺顶部和底部标签不得被视口裁切。

ComboBox 的可编辑文本和下拉指示必须分别在内容区与按钮区垂直居中；下拉指示使用同一 Fluent 图标体系，不得使用字体符号代替。显式垂直 ScrollBar 的 Track 必须完整铺满可用高度；Thumb 长度必须按当前可见范围相对完整有界范围的比例计算，不得使用与视口无关的固定值。Bottom Panel Diagnostics 的筛选 ComboBox 和 Segment Piano Roll 顶部左侧文本不得裁切或偏离垂直中心。
### 20.15.5 Resize 语义
Resize 只改变视图，不：
```text
Move Project objects
Scale Project data
Fit content automatically
Change Selection
Switch Workspace
Commit field edits
```
Pointer Capture 因 Resize 丢失时，取消当前编辑手势并恢复原状态，不创建 Undo。

Segment 下部 Lane 编辑区的分隔条调整下部编辑区与“Piano Roll + Timeline Overview”整体上部区域之间的高度分配；不得把 Timeline Overview 单独作为相邻 Resize 目标。该操作只改变 Project Session UI State，不修改 Project 内容或 Timeline zoom。
### 20.15.6 窗口状态持久化
保存 Normal Window Bounds 和 Maximized State，不恢复 Minimized State。
显示器或工作区变化时，恢复必须保证 Title Bar 和主窗口可见、可操作。
最大化不改变 Panel、Zoom、Selection 或 Tabs。
最小化不自动停止播放、Preview、Compile 或长任务。
### 20.15.7 Dialog
复杂 Configuration、Progress 和 Result Dialog 可调整尺寸；普通 Confirmation 通常内容自适应且不可调。
所有模态 Dialog 必须有明确 Owner。
Dialog 主操作始终可达，长内容内部滚动。
Dialog 尺寸和位置不跨重启持久化。
Context Menu、Dropdown、Popup、Tooltip 和 Drag Tooltip 必须保持在当前工作区内。
### 20.15.8 DPI
初版只正式验收 100% DPI。
WPF 自然缩放能力可以保留，但初版不承诺：
```text
Per-monitor DPI
Mixed DPI
Non-100% DPI validation
Special layout tuning for scaled displays
```
---
## 20.16 初版基础交互可用性边界
初版正式范围：
```text
Mouse
Keyboard and mouse combination
Clear keyboard focus visual
100% DPI
One Midora built-in theme
English UI
```
键盘焦点必须与以下状态明确区分：
```text
Selection
Primary Selection
Active Workspace
Hover
Read-only
Disabled
```
常用鼠标目标必须有合理命中区域；极端精度需求可通过 Inspector 数值输入完成。
无效操作必须提供明确视觉反馈，但不需要语音或自定义音效。
初版明确不承诺：
```text
Complete keyboard-only workflow
Screen reader support
Accessibility announcements
High Contrast theme support
Access Keys or mnemonics
Custom UI sound effects
Reduce Motion setting
Large Text mode
Non-100% DPI validation
Per-monitor DPI handling
Touch-first interaction
Voice control
Braille or sonification support
```
现有 F6、Tab、常规快捷键和清晰焦点属于基础桌面交互能力，不代表完整辅助功能支持。
---
## 20.17 初版明确不支持的 UI 与工作流能力
```text
Multiple open Projects
Multiple Midora processes
Multiple Main Windows
Floating or detachable Workspaces
Docking system
Traditional Save As
Autosave
Crash recovery
Project repair scanner
Global Project Search
Project-wide Replace
Project object clipboard across Projects
Workspace restoration across application restart
Pause
Scrubbing
Count-in
Legato editing
Tempo Ramp editor
Take Lanes
Segment names
Same-Track Segment overlap
Full keyboard-only operation
Screen reader-specific UI
High Contrast and theme system
Access Keys
Custom UI sounds
Non-100% DPI acceptance commitment
Touch-first controls
Drag-out export to Windows Explorer
MIDI, audio or arbitrary-file import by generic drag-and-drop
```
---
## 20.18 跨界面一致性规则
### 20.18.1 Segment
- 同一 Logical Track 内不允许 Segment 重叠；
- 不同 Track 可处于相同时间；
- Segment 没有名称；
- active crop window 外内容保留并可编辑；
- Arrangement 显示 Note Preview；
- 详细内容只在 Segment Editor 编辑。
### 20.18.2 Function Draft
- Draft 与 Project Applied Version 分离；
- Ctrl+S 在 Code Editor 中 Apply；
- Global Save 保存 Project；
- Save 不自动 Apply；
- Draft 不进入 `.midora`；
- Close / Switch / Exit 时必须处理 Draft。
### 20.18.3 Save
- 初版只有 Save 和 Save Copy；
- Save Copy 不切换当前路径；
- Save / Save Copy 在事务开始后不可取消；
- Damaged Placeholder 存在时两者均禁止。
### 20.18.4 Mute / Solo
- 只属于播放运行期；
- 不保存；
- 不进入 Undo / Redo；
- 不影响 MIDI Export 或 Audio Render；
- 正式输出只服从显式 Track Selection。
### 20.18.5 Search 与 Selection
Collection Search 隐藏对象时保留稳定 Selection；Editor Content Filter 隐藏对象时可从 Active Selection 移除。
### 20.18.6 Broken / Incompatible / Damaged
三者不得合并：
```text
Broken      -> target reference no longer exists
Incompatible -> data exists but current setting disables its semantics
Damaged     -> persistent source content could not be loaded
```
### 20.18.7 SoundFont
```text
No SoundFont       -> legal Project state
Configured         -> reference exists; backend not loaded
Loaded             -> current backend loaded successfully
Missing or Failed  -> playback, preview and render unavailable
```
Compile 和 MIDI Export 不依赖 SF2 加载。
### 20.18.8 English UI 与 Unicode
内置标签和消息为 English；用户作品文本允许 Unicode。
### 20.18.9 自动修复
不得通过 UI 自动改变音乐语义、删除数据或按名称重绑。
---
## 20.19 实现与验收边界
本章要求实现时至少保证：
1. 主要 Workspace 的职责不重叠；
2. 同一对象重复打开时激活已有 Workspace；
3. Project 编辑、UI Preference、Session State 和 Draft 状态严格分离；
4. 播放、编译、保存、导出和渲染遵守锁定矩阵；
5. 焦点敏感快捷键不会误操作背景 Selection；
6. 右键、工具栏、菜单和快捷键调用同一命令语义；
7. 批量编辑、Paste 和 Drag 的 Project 修改保持原子；
8. Broken 和 Damaged 数据不被静默删除或按名称修复；
9. MIDI Export 和 Audio Render 显示各自不同的结果原子性；
10. 无 SF2、空 Library、空 Track 和空 Segment 均显示为合法空状态；
11. 状态和错误不只依赖颜色；
12. 后台更新不抢焦点或改变 Selection；
13. `.midora` 不保存 UI View State；
14. 初版所有正式 UI 文案使用 English；
15. 初版正式尺寸与布局验收以 Windows 100% DPI 为准；
16. Segment Editor 左侧 Pitch Ruler 按下即开始、松开即结束单键 held Preview，且不创建 Project Note；
17. 新建单个 Logical Note 的放置手势只提供虚线视觉预览，不启动音频 Preview；
18. Pitch Ruler 与 Event Instrument / SubVoice 虚拟键盘复用同一因果 Gate、`Int64.MaxValue` 哨兵、未渲染 frontier、互斥、零分配和清理规则，不存在独立裸 MIDI 路径；
19. 无 SF2、已有播放任务或输出不可用时，合法单音符放置仍可提交，并且只形成一个 Project Undo。
具体像素、控件类、颜色值、动画参数和内部实现应在 UI 原型、实现设计或实现设计继续确定，但不得改变本章已经明确的交互语义和系统边界。
