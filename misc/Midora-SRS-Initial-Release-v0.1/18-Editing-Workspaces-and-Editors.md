# 第 18 章 编辑工作区与编辑器

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章定义 Arrangement、Segment、Event Instrument、SubVoice、Mapping、Lifecycle、Conductor、Library、Settings 和 Diagnostics 等主要工作区的职责与布局。

## 18.1 Arrangement Workspace
### 18.1.1 布局
```text
+--------------------------------------------------------------------------+
| [A] Arrangement Toolbar                                                  |
+----------------------+---------------------------------------------------+
| [B] Track Header Area| [C] Timeline Header                              |
+----------------------+---------------------------------------------------+
| [E] Track List       | [D] Ruler and Marker Area                        |
|                      +---------------------------------------------------+
|                      | [F] Arrangement Timeline                          |
|                      |                                                   |
+----------------------+---------------------------------------------------+
| [G] Horizontal Scroll and Overview                                       |
+--------------------------------------------------------------------------+
```
### 18.1.2 职责
Arrangement 只负责：
```text
Logical Track order and visibility
Segment creation, movement, copy, crop, split and deletion
Project timeline navigation
Segment-level arrangement overview
Track Mute and Solo runtime controls
```
Logical Note 和 Logical Parameter 细节进入 Segment Editor。
Tempo、Time Signature、Key Signature、Marker 和 Project End Marker 只显示概览；精确编辑进入 Conductor Track Editor。
### 18.1.3 Track Header
显示：
```text
Track display name
Bound Event Instrument or Unassigned
Track color
Mute
Solo
Track validation summary
```
Mute / Solo 固定可见，属于播放期运行状态：
- 不保存；
- 不进入 Undo / Redo；
- 不标记 Project Modified；
- 不影响 MIDI Export；
- 不影响 Audio Render。
Track 高度属于 UI 状态；Track 正式顺序属于 Project Content。
### 18.1.4 Segment 显示
Segment 没有名称。矩形显示：
```text
Track color context
Content summary
Active crop window
Broken or validation state
Logical Note preview
```
初版必须在 Segment 矩形内显示简化 piano-roll Note Preview，表达音高、相对位置和长度。该预览只用于概览，不允许在 Arrangement 内直接精细编辑 Note。
### 18.1.5 Segment 重叠
正式规则：
```text
Same Logical Track      -> Segment overlap is not allowed
Different Logical Tracks -> time overlap is allowed
Adjacent Segments       -> allowed; no automatic join
```
移动、复制、粘贴、绘制或调整边界若造成同一 Track Segment 重叠：
- 显示非法预览；
- 整体拒绝提交；
- 不自动缩短；
- 不移动邻近 Segment；
- 不寻找最近空位；
- 不自动创建 Track。
该规则只约束 Segment 容器，不等同于 Logical Note 或编译后 Event Instrument Instance 的重叠规则。
### 18.1.6 Segment 操作
```text
Drag body  -> move Segment
Drag edge  -> change active crop window
Ctrl+Drag  -> copy Segment when target is valid
Split Tool -> split at target tick
```
Segment 可纵向移动到其他 Logical Track。跨 Track 时保留所有 Note、Lane、Broken / Inapplicable 数据和 crop 外内容；不得自动匹配、修复或删除 Logical Parameter Lane。
### 18.1.7 Timeline
支持：
```text
Select
Draw
Split
Grid
Snap
Zoom
Playback Cursor
Edit Cursor
Time Range Selection
```
Project End Marker 后区域弱化，但仍显示并允许编辑。End Marker 不是右编辑边界。
无显式 End Marker 时可显示 Natural End 参考，但它不是 Project 对象。
---
## 18.2 Segment Editor
### 18.2.1 布局
```text
+--------------------------------------------------------------------------+
| [A] Segment Toolbar                                                      |
+--------------------------------------------------------------------------+
| [B] Segment Summary                                                      |
+-------------+------------------------------------------------------------+
| [D] Pitch   | [C] Local Timeline Header                                 |
|     Ruler   +------------------------------------------------------------+
|             | [E] Note Editor                                            |
+-------------+------------------------------------------------------------+
| [F] Lane List| [G] Logical Parameter Editor                              |
+-------------+------------------------------------------------------------+
| [H] Horizontal Scroll and Segment Range Overview                         |
+--------------------------------------------------------------------------+
```
### 18.2.2 时间坐标
Segment Editor 以 Segment local tick 为主，同时可显示映射后的 Project Position。
不得把 local tick 伪装成 Project `Bar:Beat:Tick`。
### 18.2.3 Pitch Ruler
- 使用钢琴键盘样式；
- MIDI Note 60 显示为 C4；
- 黑键使用升号；
- 初版不在键位上写 MIDI Note 编号；
- 鼠标左键按下键位发起 held Preview，松开或取消结束 Gate；
- 该预览不创建 Project Note，并严格使用第 13.22.7、13.24.5 节的因果 Gate 与当前 Segment 绑定的 Event Instrument。
### 18.2.4 Note Editor
使用 piano roll 编辑 Logical Notes。

初版在每次新建单个 Logical Note 的放置手势中预览当前草稿音符。预览从合法放置手势开始持续到成功提交、取消或失败清理；最终 Note Length 在手势结束前未知，因此严格复用第 13.22.7、13.24.5 节的因果 Gate，不得另行猜测固定 Gate Length 或直接发送 MIDI。

预览是编辑手势的临时运行时副作用，不是 Project 数据。预览不可用或失败不得阻止原本合法的 Note 提交，也不得增加额外 Undo entry。移动、缩放已有 Note、批量 Paste、Duplicate 和批量编辑不因本条要求自动发起预览。

active crop window 外内容：
- 保留；
- 可见但弱化；
- 可选择和编辑；
- 不因被编辑而自动进入编译范围；
- Snap 不强制把它拉回 active 区。
初版不支持 Legato，但保留统一 Logical Note 批量编辑入口，便于未来扩展。
### 18.2.5 Logical Parameter Lanes
Lane List 只允许添加当前绑定 Event Instrument 暴露的 Logical Parameters。
支持：
```text
Integer points and curves
Double points and curves
Enum step states
```
Broken Lane 保留数据并明确显示，不按名称自动重绑。
必须区分：
```text
Hide Lane
Delete Lane Data
```
Hide 不删除 Project 数据；Delete Lane Data 删除该 Lane 的用户内容，并按破坏性规则确认。
### 18.2.6 同步
Segment 编辑后：
- Arrangement Note Preview 实时更新；
- Inspector 显示当前 Note、Point、Lane 或 Segment 摘要；
- 相关 Validation 和 Diagnostics 更新；
- 一次用户手势形成一次 Project Undo。
---
## 18.3 Event Instrument Editor 总体框架
### 18.3.1 布局
```text
+--------------------------------------------------------------------------+
| [A] Instrument Header                                                    |
+----------------------+---------------------------------------------------+
| [B] Section         | [D] Section Toolbar                              |
|     Navigation      +---------------------------------------------------+
|                     | [E] Active Section Editor                          |
| [C] Structure Panel|                                                   |
+----------------------+---------------------------------------------------+
| [F] Preview Panel                                                       |
+--------------------------------------------------------------------------+
```
内部固定分区：
```text
Overview
SubVoices
Parameters
Lifecycle
```
### 18.3.2 Header
显示：
```text
Instrument name
Color
Usage count
Validation state
Preview state
```
一个 Event Instrument 对应一个 Workspace Tab。
### 18.3.3 Structure Panel
根据当前分区显示：
```text
SubVoices
Logical Parameters
Mappings
Mapping Functions
Envelope Presets
Lifecycle objects
```
### 18.3.4 Preview Panel
位于底部，可折叠。
预览模式：
```text
Full Instrument
Selected SubVoice
```
预览键盘与 Segment Editor Pitch Ruler 采用统一琴键规则，不显示 MIDI Note 编号。
Preview Mute / Solo 只影响预览任务，不属于 Project。
### 18.3.5 引用更新
修改 Event Instrument 后：
- 所有引用它的 Logical Track 使用最新定义；
- Segment Logical Parameter Lane 状态立即更新；
- Broken / Incompatible 数据保留；
- 不按名称自动修复；
- 相关编译和播放缓存失效。
UI 不显示或允许用户指定固定 Port / Channel。
---
## 18.4 SubVoice Event Editor
### 18.4.1 布局
```text
+--------------------------------------------------------------------------+
| [A] SubVoice Context Header                                              |
+--------------------------------------------------------------------------+
| [B] View Switch and Event Toolbar                                        |
+-------------+------------------------------------------------------------+
| [C] Pitch   | [D] Note Timeline                                         |
|     Keyboard|                                                            |
+-------------+------------------------------------------------------------+
| [E] Event Lane List | [F] Event Lane Stack                              |
+--------------------------------------------------------------------------+
| [G] Timeline Overview and Horizontal Scroll                              |
+--------------------------------------------------------------------------+
```
视图：
```text
Timeline
Initial State
```
### 18.4.2 Note 与事件 Lane
- Note 使用独立 piano roll；
- CC 和 Pitch Bend 使用连续点或曲线；
- Program、Bank、RPN、NRPN 和 Pitch Bend Range 使用离散高级事件；
- 不展开 RPN / NRPN 底层 CC 序列；
- 不显示 GM Program 名称或 SF2 preset 名称；
- Program 只显示用户侧 1～128 编号。
### 18.4.3 Initial State
Initial State 与 tick 0 普通事件严格分离：
```text
Initial State has no tick.
Initial State does not affect Template Length.
A user event at tick 0 may override the corresponding Initial State.
```
### 18.4.4 Template 与 Root Note
Template Length 属于 Event Instrument，不是每条 SubVoice 独立长度。
SubVoice 显示 Root Note 的 inherited / override 状态和 Effective Value。
Loop 区域可以只读显示，但在 Lifecycle Editor 中编辑。
### 18.4.5 Lane 生命周期
空 Lane 不持久化。
必须区分：
```text
Hide Lane
Delete All Events
```
Mapping Chain 标记与 Logical Parameter Mapping 必须使用不同名称和视觉语义，不能混为同一种“Mapping”。
---
## 18.5 Logical Parameter、Mapping 与 Function Editor
### 18.5.1 布局
```text
+--------------------------------------------------------------------------+
| [A] Parameter Section Toolbar                                            |
+----------------------+---------------------------------------------------+
| [B] Parameter       | [C] Editor Mode Tabs                              |
|     Structure       +---------------------------------------------------+
|                     | [D] Active Parameter Editor                        |
+----------------------+---------------------------------------------------+
| [E] Mapping Target Summary                                               |
+--------------------------------------------------------------------------+
| [F] Validation and Reference Panel                                       |
+--------------------------------------------------------------------------+
```
模式：
```text
Definition
Mapping
Function
```
### 18.5.2 Logical Parameter Definition
编辑：
```text
Name
Input type
Default value
Legal range
Display range
Enum items when applicable
```
Logical Parameter 名称在单个 Event Instrument 内必填且唯一，比较时去除首尾空白并忽略大小写。
### 18.5.3 Logical Parameter Mapping
初版使用有序列表，不做自由节点图。
目标选择路径：
```text
SubVoice
-> non-Note MIDI or advanced event object
-> target field
```
初版不允许 Logical Parameter Mapping 指向 Note 参数。
多个 Logical Parameter 指向同一目标时，必须显示正式执行顺序。
Broken source、target 或 function reference 均保留，不自动重绑。
Mapping Editor 提供单值测试；该测试不启动播放、不修改 Project 内容，也不进入 Undo / Redo。
### 18.5.4 C# Mapping Function
C# Mapping Function 使用内置代码编辑器和独立 Draft Buffer。
Draft Buffer：
- 文本 Undo / Redo 独立；
- Cut / Copy / Paste 独立；
- 输入不实时写入 Project；
- 未 Apply 草稿不进入 `.midora`；
- Project Save 不自动 Apply 草稿。
```text
Ctrl+S while Function Code Editor has focus -> Apply Function Draft
```
Apply：
- 将完整源码作为一次 Project 编辑写入；
- 形成一次 Project Undo；
- 标记 Project Modified；
- 不写磁盘。
Global Save Button 和 `File > Save Project` 永远保存 Project，不执行 Apply。
关闭 Function Workspace、切换 Project 或退出时，有 Draft 必须选择：
```text
Apply
Discard Draft
Cancel
```
Function 名称修改与 Draft 内容是两个独立编辑状态。重命名不自动 Apply Draft。
### 18.5.5 Function 诊断
Code Editor 内的 Draft 错误只对应 Draft。
Project Diagnostics 对应最后 Apply 的版本，并在存在 Draft 时明确提示：
```text
Diagnostics refer to the applied version.
An unapplied draft is currently open.
```
---
## 18.6 Lifecycle、Loop、Envelope 与 Overlap Editor
### 18.6.1 布局
```text
+--------------------------------------------------------------------------+
| [A] Lifecycle Toolbar                                                    |
+----------------------+---------------------------------------------------+
| [B] Lifecycle       | [C] Lifecycle Context Summary                     |
|     Navigation      +---------------------------------------------------+
|                     | [D] Active Lifecycle Editor                        |
+----------------------+---------------------------------------------------+
| [E] Lifecycle Preview Timeline                                           |
+--------------------------------------------------------------------------+
| [F] Scenario Preview and Validation                                      |
+--------------------------------------------------------------------------+
```
页面：
```text
Strategies
Loop
Envelope Presets
Overlap
```
### 18.6.2 职责
集中编辑：
```text
Short Note Strategy
Long Note Strategy
Loop
Envelope Presets
Overlap Strategy
```
本界面不重新定义 第 10 章《实例生命周期、Loop、Envelope 与重叠》 已确定的生命周期语义。
### 18.6.3 Per-Note Instance Isolation
关闭后，依赖该能力的数据：
- 保留；
- 显示为 Incompatible；
- 不可编辑；
- 不生效；
- 重新启用后恢复。
不得显示为空集合或删除数据。
### 18.6.4 Loop
初版 Loop 是固定单一设置，不是多个对象。
关闭 Loop 时保留边界数据。
### 18.6.5 Envelope
初版 Envelope Preset 编辑器必须直接编辑第 10.11 节规定的固定 ADSR-like 结构：
```text
阶段时长：Delay / Attack / Hold / Decay / Release
可编辑值：Start Value / Peak Value / Sustain Level / End Value
```
本编辑器可以显示上述固定阶段的边界，但不得允许用户新增、删除或重排任意阶段，也不得把 Envelope 扩展为任意 Ordered Points 或 Curve Segments 数据模型。
本节只规定编辑器呈现与操作入口；阶段语义、输出值域、时长单位、插值和 Gate End 后行为均以第 10.11 节为准。
Release Start 是相对生命周期参考，不是固定 Project tick。
### 18.6.6 Scenario Preview
支持输入：
```text
Gate Length
Pitch
Velocity
Optional hard boundary
```
Lifecycle Preview Timeline 和 Scenario Preview 只用于解释和测试，不创建 Project 事件。
声音预览与所有其他播放任务互斥。
---
## 18.7 Conductor Track Editor
### 18.7.1 布局
```text
+--------------------------------------------------------------------------+
| [A] Conductor Toolbar                                                    |
+----------------------+---------------------------------------------------+
| [B] Event Type Panel| [C] Timeline Header                              |
+----------------------+---------------------------------------------------+
|                      | [D] Global Event Timeline                         |
+----------------------+---------------------------------------------------+
| [E] Event List and Details                                               |
+--------------------------------------------------------------------------+
| [F] Timeline Overview and Horizontal Scroll                              |
+--------------------------------------------------------------------------+
```
Lane：
```text
Tempo
Time Signature
Key Signature
Markers
Project End Marker
```
### 18.7.2 Tempo
初版只支持离散 Tempo 事件，不支持 Tempo Ramp 或连续 Tempo Automation。
Tempo 大于 0，允许小数，不设置 20～300 等经验型限制。
### 18.7.3 Time Signature 与 Key Signature
Time Signature 修改只更新网格和音乐位置显示，不移动任何内容 tick。
Key Signature 不自动转调或修改 Note 音高。
### 18.7.4 Marker
普通 Marker 名称可空、可重复，同一 tick 可以存在多个。编辑器必须按稳定 ID 维持各 Marker 的独立身份，并使同 tick 或密集 Marker 在选择和编辑时可区分。
Project End Marker 使用贯穿 Lane 的特殊竖线：
- 不可重命名；
- 最多一个；
- 后方区域弱化但仍可编辑；
- 不等同于普通 Marker。
无显式 End Marker 时可以显示 Natural End 只读参考。
### 18.7.5 Event List
Event List 与 Timeline Selection 同步。
播放期间允许查看和导航，不允许编辑 Conductor 事件。
---
## 18.8 Event Instrument Library Workspace
### 18.8.1 布局
```text
+--------------------------------------------------------------------------+
| [A] Library Toolbar                                                      |
+----------------------+---------------------------------------------------+
| [B] Folder Panel   | [C] Instrument Collection                         |
+----------------------+---------------------------------------------------+
| [D] Summary Panel                                                        |
+--------------------------------------------------------------------------+
| [E] Usage and Validation Panel                                           |
+--------------------------------------------------------------------------+
```
### 18.8.2 Library 功能
支持：
```text
Search by Instrument or Folder name
One-level folders
Manual order
Instrument colors
List and card views
Create
Duplicate
Rename
Delete
Preview
Show references
```
List / Cards 视图属于 Application Preference。
### 18.8.3 Folder
初版只支持单层 Folder：
```text
Event Instrument Library
├─ Unfiled
├─ Folder A
├─ Folder B
└─ Folder C
```
不支持 Subfolder。
Folder 名称：
- 必填；
- Library 内唯一；
- 去除首尾空白；
- 大小写不敏感比较。
`Unfiled` 是固定系统节点，不可重命名、删除或移动。
初版 Folder 不支持颜色。
删除 Folder：
```text
Move contained Event Instruments to Unfiled
Delete only the Folder object
```
不删除内部 Event Instrument。
### 18.8.4 手动排序与临时排序
Library 的正式手动顺序属于 Project Content。
临时按名称或状态排序只改变当前视图，不修改 Project。
搜索或临时排序期间禁用拖动正式排序。
### 18.8.5 Duplicate
Event Instrument Duplicate 是深拷贝：
- 生成新 Event Instrument 稳定 ID；
- 所有内部用户对象生成新稳定 ID；
- 内部引用重映射；
- 后续编辑互不影响；
- 一次 Duplicate 形成一次 Project Undo。
### 18.8.6 Delete
删除 Event Instrument 时必须显示引用影响：
- 引用 Track 变为 Unassigned；
- Track、Segment、Note 和 Logical Parameter Lane 数据保留；
- Last Known Instrument Name 保留；
- 不按名称重绑；
- Broken / Inapplicable Lane 数据保留。
未引用本身不是 Warning。未引用 Event Instrument 内部错误不阻止整曲编译，但 Library 中仍显示其验证状态。
### 18.8.7 Drag
Event Instrument 在 Library 内拖动只用于 Move / Reorder；Duplicate 使用显式命令，不使用 Ctrl+Drag。
拖到 Arrangement 空 Track 区域可创建绑定 Track。
拖到已有 Track 时必须确认重绑影响。
初版不要求拖到时间线空白处同时创建 Track 和 Segment。
---
## 18.9 Project Settings Workspace
### 18.9.1 布局
```text
+--------------------------------------------------------------------------+
| [A] Settings Header                                                      |
+----------------------+---------------------------------------------------+
| [B] Category        | [C] Category Toolbar                              |
|     Navigation      +---------------------------------------------------+
|                     | [D] Active Settings Page                           |
+----------------------+---------------------------------------------------+
| [E] Change and Validation Summary                                        |
+--------------------------------------------------------------------------+
```
分类：
```text
General
Metadata
SoundFont
Playback
MIDI Export
Audio Render
Reset Defaults
```
### 18.9.2 General 与 Metadata
TPQ 创建 Project 后只读。
Project Name 可空。Project Version 是用户自由文本，与 File Format Version 严格区分。
Metadata 普通字段直接提交 Project，不使用 Function Draft 机制。
用户 Metadata：
```text
Project Name
Project Version
Author or Team
Original Work
Copyright
Notes
```
File Information 只读显示：
```text
Created Time
Modified Time
Accumulated Project Time
Created With Midora
Last Saved With Midora
File Format Version
```
`Created With Midora` 和 `Last Saved With Midora` 来自 manifest，不属于用户 Metadata。
### 18.9.3 SoundFont
模式：
```text
Embedded in Project
External Relative Reference
```
External Relative Reference 只允许：
```text
Same directory as the .midora file
soundfonts\ under the Project directory
```
无 SF2 是合法 Project 状态。
状态必须区分：
```text
No SoundFont
SoundFont Configured
SoundFont Missing
SoundFont Changed
Loading SoundFont
SoundFont Loaded
SoundFont Load Failed
```
Configured 不等于 Loaded。打开 Project 时不创建 BASSMIDI Stream，也不加载 SF2。
### 18.9.4 Playback
Project Playback Settings 只包含：
```text
Playback Master Volume
Playback Limiter
Stop Cursor Behavior
```
Master Volume 和 Limiter 同时影响实时播放与音频渲染。
Playback Device 属于 Application Preference，不放入 Project Settings。正式入口为：
```text
Playback > Output Device
```
该入口必须列出全部 enabled output device，标记 System Default，并排除输入、loopback input、disabled、unplugged 和 not-present 端点。

同一 Application Preference 页面还应提供：
```text
Render-Ahead Buffer: 20–2000 ms, default 100 ms
Device Buffer Request: 5–200 ms, default 50 ms
```

页面只读显示设备实际采样率、实际 buffer 和 callback period。设备及 buffer 设置只能在 Stopped 状态提交。
### 18.9.5 MIDI Export 与 Audio Render
Settings Workspace 保存 Project 默认值，不保存绝对输出路径。
Export / Render Dialog 中的本次参数默认不修改 Project；只有显式 `Save as Project Defaults` 才形成一次 Project 编辑。

Audio Render Settings 必须允许保存默认文件采样率：
```text
整数
8,000–192,000 Hz
默认 48,000 Hz
```
RIFF/WAVE、Stereo、Interleaved IEEE 32-bit Float 和 Little-endian 是只读固定格式字段。
### 18.9.6 Reset 与固定 Event Scope
Reset Defaults 必须与以下内容明确区分：
```text
SubVoice Initial State
Timeline Event
Instance-end Reset
```
初版不显示或编辑 `Global Event Scope Defaults`。Note 为逐实例事件；Bank、Program、CC、Pitch Bend、RPN、NRPN 与 Pitch Bend Range 等状态类事件的 Channel-Wide 作用域由各专项章节固定，不提供用户覆盖入口。
### 18.9.7 字段提交
设置字段提交后进入 Project Undo / Redo。
非法值：
- 不提交；
- 不静默 Clamp；
- 不创建 Undo；
- 失焦时恢复最后合法值。
---
## 18.10 Diagnostics Workspace
### 18.10.1 布局
```text
+--------------------------------------------------------------------------+
| [A] Diagnostics Toolbar                                                  |
+----------------------+---------------------------------------------------+
| [B] Filter Panel   | [C] Diagnostic List                               |
+----------------------+---------------------------------------------------+
| [D] Summary Panel                                                        |
+--------------------------------------------------------------------------+
| [E] Diagnostic Details and Source Path                                  |
+--------------------------------------------------------------------------+
```
### 18.10.2 严重级别
```text
Error
Warning
Information
```
Warning 即使因用户策略阻止当前操作，也仍显示为 Warning。
### 18.10.3 类别与上下文
至少区分：
```text
Validate
Compile
Runtime
Open
Save
Export
Audio Render
Resource
```
Compile Diagnostic 必须显示 CompileContext，例如：
```text
Whole Project Compile
Playback Compile
Segment Preview
Event Instrument Preview
MIDI Export
Audio Render
Logical Track Render
```
### 18.10.4 状态
```text
Active
Resolved
Runtime History
```
过期诊断必须明确显示它属于较早的 Project 状态，不得继续决定当前 Play、Export 或 Render 是否允许开始。
Diagnostics：
- 不属于 Project；
- 不保存；
- 不进入 Undo / Redo。
### 18.10.5 内容
每条诊断应显示：
```text
Severity
Category
Message
Source Object
Source Path
Context
Current or Historical Status
```
完整 Details 可显示原因、相关值、可执行的下一步和诊断代码，但不默认暴露原始异常堆栈或内部类名。
### 18.10.6 来源导航
Source Path 使用稳定 ID 定位并显示最新名称。
`Go to Source`：
- 激活或打开对应 Workspace；
- 选择来源对象；
- 滚动到可见位置；
- 更新 Inspector；
- 不修改 Project。
来源已删除时显示 Last Known Path，不按名称寻找替代对象。
多来源问题显示 Primary Source 和 Related Sources。
### 18.10.7 自动修复
自动修复只允许同时满足：
```text
Unique result
Deterministic result
No change to music semantics
No deletion of user data
```
不得自动：
```text
Rebind by name
Delete Broken Lane
Clamp values without an explicit Clamp rule
Change Event Instrument
Change lifecycle strategy
Reduce isolation semantics
Steal voices or remove notes
Ignore errors and continue compiling
```
### 18.10.8 聚合
同一来源、同一问题、同一上下文可以聚合次数；不同来源且需要独立修复时必须保留可导航的具体项。
### 18.10.9 音频分轨结果
Per Logical Track Audio Render 诊断必须按 Track 显示独立结果，并区分：
```text
Completed
Completed With Errors
Failed
Cancelled
Cancelled With Completed Outputs
```
---
