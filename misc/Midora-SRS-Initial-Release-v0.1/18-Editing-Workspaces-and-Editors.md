# 第 18 章 编辑工作区与编辑器

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章定义 Arrangement、Segment、Event Instrument、SubVoice、Mapping、Lifecycle、Conductor、Settings 和 Diagnostics 等主要工作区的职责与布局。初版不再提供独立 Event Instrument Library Workspace。

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
Conductor-first Event Instrument / MIDI Channel Root mixed parent order
Event Instrument→Logical Track and Root→Pure MIDI Track hierarchy
parent/child order, visibility, rebind and routing
Segment creation, movement, copy, crop, split and deletion
Project timeline navigation
Segment-level arrangement overview
parent and child Track Mute/Solo runtime controls
```
Logical Note/Parameter 与 Direct MIDI Note/Event 的细节进入对应 Segment Editor。
Tempo、Time Signature、Key Signature、Marker 和 Project End Marker 只显示概览；精确编辑进入 Conductor Track Editor。
### 18.1.3 Track Header
显示：
```text
parent/Track display name
Event Instrument / Root parent summary
Pure MIDI Track 的 MIDI 类型标识
Track color
Mute
Solo
Track validation summary
```
parent 与 child 的 Mute / Solo 固定可见，属于播放期运行状态：
- 不保存；
- 不进入 Undo / Redo；
- 不标记 Project Modified；
- 不影响 MIDI Export；
- 不影响 Audio Render。
Track 高度属于 UI 状态；Track 正式顺序属于 Project Content。

Header 是独立交互目标。鼠标悬停时使用低强调高亮，按下时背景变暗，松开恢复；越过通用拖动阈值后才开始重排并显示准确插入线。Event Instrument / Root 是可混排 parent Header；Logical / Pure MIDI Track 是带统一缩进的 child Header。父节点右侧不承载 Segment，但 Grid/Bar 线连续绘制。完整 Header 字段、菜单、复制删除、拖动和层级见第 24.3～24.7 节。

Logical Track 不显示 Bind/Unbind，所在 Event Instrument parent 即唯一绑定；拖到另一个 Event Instrument 执行 rebind 审查。Pure MIDI Track 名称左侧必须显示 MIDI 图标，可跨 Root 移动。Event Instrument/Root parent 与 child Track 的 Mute/Solo 相互独立。
### 18.1.4 Segment 显示
Segment 没有名称。矩形显示：
```text
Track color context
Content summary
Active crop window
Broken or validation state
Logical Note preview
或 Direct MIDI Note preview
```
初版必须在 Segment 矩形内显示简化 piano-roll Note Preview，表达音高、相对位置和长度。该预览只用于概览，不允许在 Arrangement 内直接精细编辑 Note。

Preview 使用固定 MIDI pitch `0..127` 的二维投影；Note 最小可见高度为 1 px，位置与边界执行布局取整，Segment 本地 tick 0 的 Note 不得因左边界或可见范围查询而遗漏。Preview 必须在 Arrangement 的手工渲染面内绘制，不得为每个 Note 创建 WPF Control。实现应按 Segment 稳定 ID 与 preview 相关内容指纹复用缓存；缩放、平移、选择或播放指针变化不得重建未变化 Segment 的 preview 内容。

Pure MIDI Segment 还必须在 Note 上层绘制独立缓存的 non-Note event 线，统一 50% 透明度、最小 1 device pixel，并按正式事件值域归一化高度；Logical Segment 不绘制该层。详细 LOD、同列聚合、裁剪和独立失效规则见第 24.8 节。
### 18.1.5 Segment 重叠
正式规则：
```text
Same owning Track        -> Segment overlap is not allowed
Different Tracks         -> time overlap is allowed
Adjacent Segments       -> allowed; no automatic join
```
移动、复制、粘贴、绘制或调整边界若造成同一 Track Segment 重叠：
- 显示非法预览；
- 整体拒绝提交；
- 不自动缩短；
- 不移动邻近 Segment；
- 不寻找最近空位；
- 不自动创建 Track。
该规则只约束 Segment 容器，不等同于 Logical Note、Direct MIDI Note 或编译后 Event Instrument Instance 的重叠规则。
### 18.1.6 Segment 操作
```text
Drag body  -> move Segment
Drag edge  -> change active crop window
Ctrl+Drag  -> copy Segment when target is valid
Split Tool -> split at target tick
```
Logical Segment 可纵向移动到其他 Logical Track，保留所有 Note、Lane、Broken / Inapplicable 数据和 crop 外内容；不得自动匹配、修复或删除 Logical Parameter Lane。Midi Segment 可移动到其他 Pure MIDI Track，包括跨 Root，并保留全部 direct/opaque 数据；目标 Track 不得重叠。两类 Segment 之间不允许隐式移动或转换。
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

Arrangement Toolbar 必须分别提供可见分割线粒度、操作粒度与 Snap 开关，以及默认 Segment 创建长度（tick，输入即生效）；新建 Project / 重置编辑器时默认可见分割线粒度为 `Bar`、默认操作粒度为 `1/8`、Snap 开启、默认 Segment 创建长度为 `1 × TPQ`。Arrangement 的可见分割线粒度为 `Bar` 时，小节边界使用主实线，每个分母拍的内部边界使用颜色更浅的低强调实线；拍线必须读取完整 Project Time Signature Map，因此 `3/4` 每小节显示 2 条四分音符间隔实线，`6/8` 每小节显示 5 条八分音符间隔实线，拍号变化 tick 立即作为新的主实线小节边界。该拍内辅助线增强不应用到 Segment 或 SubVoice 钢琴卷帘。Draw 模式下，鼠标所在 Track 必须显示按当前操作粒度定位、按默认长度计算的虚线创建预览。空白处按下左键后进入 Segment 放置手势：未越过拖动阈值时按默认长度创建；向右拖动时按当前操作粒度实时调整结束 tick，松开后一次性提交。若请求长度超出当前可用间隙，创建命令静默缩短为从目标 tick 起可容纳的最大正长度；预览显示实际将提交的长度。不存在正长度空隙时预览为错误色并拒绝创建。该规则只适用于新建 Segment；已有 Segment 的移动和 Resize 仍不得因重叠而被静默缩短。

Arrangement 中只有 Draw 模式允许拖动 Segment 主体或调整边缘；Select 模式的单次左键按下始终发起框选，即使起点位于 Segment 上也不得先命中或单独选择该 Segment，并且不得直接移动、Resize 或双击创建 Segment。既有双击导航不受该单击规则影响。拖动和 Resize 期间必须显示位置与长度预览，并隐藏同位置的创建预览。工具互斥、指针和快捷键规则见第 20.1.6、20.12 节。

Draw 模式下，`Alt + Left Drag` 在 Segment 的任意命中位置强制执行 Move，即使指针位于左/右 Resize 边界；`Ctrl + Alt + Left Drag` 强制执行复制并移动。该替代手势仍服从当前 Snap 设置。操作类型及复制意图在按下时冻结，拖动途中改变修饰键不得在 Move、Copy 与 Resize 之间切换。

Draw 模式下在 Segment 主体执行 `Ctrl+Drag` 时，复制当前 Segment 选择集并以一个共同时间/Track delta 放置完整副本；原 Segment 不移动。复制成功后只选择副本，一次完整手势形成一个 Project Undo。
---
## 18.2 共享 Segment Editor
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
Logical Segment Editor 与 Midi Segment Editor 均以 Segment local tick 为主，同时可显示映射后的 Project Position。
不得把 local tick 伪装成 Project `Bar:Beat:Tick`。
### 18.2.3 Pitch Ruler
- 使用完整白键底板与较短黑键叠层构成的真实横向钢琴键样式；
- MIDI Note 60 显示为 C4；
- 音名只在每个八度的 C 键显示，其他键不显示音名；
- 初版不在键位上写 MIDI Note 编号；若其他 UI 必须显示黑键音名，仍使用升号；
- 鼠标左键按下键位发起 held Preview，松开或取消结束 Gate；
- 该预览不创建 Project Note，并严格使用第 13.22.7、13.24.5 节的因果 Gate 与当前 Segment 绑定的 Event Instrument。
### 18.2.4 Note Editor
使用共享 piano roll 编辑 Logical Note 或 Direct MIDI Note；数据访问、命令提交和诊断通过领域 adapter 区分，不得复制第三套渲染/命中测试/选择/手势实现。

新建单个 Logical Note 的放置手势不得启动声音预览。Draw 模式只显示当前 pitch、位置和默认长度的虚线视觉预览；该视觉预览不是 Project 数据。点击已有 Note 时，将该 Note 的长度复制为后续创建的默认 Note 长度，但不修改该 Note。

只有 Draw 模式允许拖动或 Resize Logical Note；Select 模式的单次左键按下始终发起框选，即使起点位于 Note 上也不得先命中或单独选择该 Note，并且不得直接移动、Resize 或双击创建 Note。移动和 Resize 期间必须显示位置与长度预览，并隐藏创建预览。

Draw 模式下在 Logical Note 主体执行 `Ctrl+Drag` 时，复制当前 Note 选择集并以一个共同时间/pitch delta 放置副本；原 Note 不移动，相对时间、音程、长度和 velocity 保持不变。复制成功后只选择副本，一次完整手势形成一个 Project Undo。边缘 `Ctrl+Drag` 仍按 Resize 处理，不隐式复制。

Draw 模式下，`Alt + Left Drag` 在 Logical Note 的任意命中位置强制执行 Move，`Ctrl + Alt + Left Drag` 强制执行复制并移动；这两种手势均仍服从当前 Snap。操作类型及复制意图在按下时冻结。SubVoice Template Note 复用同一规则。

Segment Toolbar 必须提供共享 piano-roll 的可见分割线粒度、操作粒度、Snap、默认 Note 长度（tick）与默认 velocity。默认长度和 velocity 独立于 Grid；默认长度允许小于操作粒度。

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

下部编辑区使用“Velocity + Lane 列表 + 单个活动 Lane 编辑器”，不得把所有参数 Lane 垂直压缩堆叠。该区域可隐藏、恢复和调整高度；显隐与高度只属于当前 Project Session UI State。高度分隔条必须位于“Piano Roll + Timeline Overview”整体上部区域与下部编辑区之间；拖动必须实际改变下部编辑区高度，不得只调整固定高度的 Timeline Overview。

Velocity 视图按 Note start tick 绘制固定窄柱，高度表示 velocity；柱宽不表达 Note 长度，柱顶必须显示明显大于柱宽的方形 onset marker，以同时明确 Note start tick 和 velocity 顶点。同 tick 存在多个 pitch 时，按 pitch 从低到高绘制，使高 pitch 对应柱位于最上层；pitch 相同时按稳定 ID 确定顺序。

左键在空白处按下并拖动形成自由轨迹，右键拖动使用起止点直线轨迹；无选择时手势作用于轨迹经过的全部柱，存在选择时只作用于经过且已选择的柱。按住期间只显示轻量轨迹覆盖层，不逐柱重绘、不更新 Velocity tile，也不提交 Project；松开时根据完整轨迹一次性计算最终值、提交一次 Project Undo，并异步重建受影响 tile。单击而未移动仍以该点作为单点轨迹，包括 tick 0。Escape 或 mouse capture 丢失取消轨迹且不提交。

左键直接按住单柱或其 onset marker 上下拖动时，只调整命中的一个 Note，不显示轨迹；同 tick 重叠柱按上述最上层顺序命中。该单柱 transient 允许只覆盖一个柱，松开时提交。所有 Velocity 手势都不得改变 Note 的位置、长度或 pitch。

`Alt + Left Drag` 必须强制使用自由轨迹手势：起点即使命中单柱或 onset marker，也不得进入单 Note 调整。该修饰键只覆盖 direct-hit 分流，不改变“存在选择时仅作用于已选择 Note”的过滤规则。

单个参数 Lane 编辑器左侧显示值标尺，右侧显示对应水平参考线；Lane 具有独立于 piano roll 的纵向缩放。参考值密度随纵向缩放调整。纵向缩放、滚轮平移、中键平移和右侧滚动条必须操作同一有界数值视口，标尺随视口更新，不得越过参数合法范围；顶部与底部标签保持在可视区域内。Integer 参数由指针纵坐标得到的值必须先按 `AwayFromZero` 取到最近整数，再执行合法范围验证。
### 18.2.6 同步
Segment 编辑后：
- Arrangement Note Preview 实时更新；
- Inspector 显示当前 Note、Point、Lane 或 Segment 摘要；
- 相关 Validation 和 Diagnostics 更新；
- 一次用户手势形成一次 Project Undo。

### 18.2.7 Logical 与 Pure MIDI 变体

Logical Segment 变体的下部 Lane 编辑 Logical Parameter，并通过 Track 绑定的 Event Instrument 解释 Note。Pure MIDI 变体的 Velocity 直接编辑 Direct MIDI NoteOn velocity，下部 Event Lane 直接编辑完整 Channel Voice Event；不显示 Logical Parameter 或 Event Instrument 绑定控件。

Pure MIDI 的 opaque SysEx/Meta 只在 Event List/Inspector 中查看、移动和删除，不提供自由 payload 编辑。两种变体必须共享 Grid/Snap/zoom/pan/scroll、tile cache、临时编辑覆盖层、批量选择与 Segment Content Window 行为；修复共享交互缺陷不得要求分别修改复制实现。
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

SubVoice Timeline 与 Segment Editor 共用当前 Project 会话的 piano-roll Grid / Snap、默认 Note 长度和默认 velocity。下部编辑区同样使用 Velocity 与单个活动事件/曲线 Lane 切换，不保留多 Lane 垂直堆叠模式。

SubVoice Note piano roll 复用第 18.2.3～18.2.4 节的 Pitch Ruler 琴键与 C 音名规则、Draw / Select 直接编辑边界、拖动预览和第 20 章的工具互斥、指针及快捷键规则。
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

### 18.7.6 Arrangement 概览

Conductor 在 Arrangement 第一行直接按 absolute Project tick 显示事件圆点，不使用 Segment。不同类型使用稳定不同颜色，Project End Marker 保持专用竖线。极端内容必须使用固定 device-size glyph、可视 tile、按 event type/device-pixel column 聚合和局部失效；完整规则见第 24.9 节。
---
## 18.8 Event Instrument Library Workspace（删除）

初版不提供该 Workspace、Folder Panel、Unfiled、List/Card Library 或独立 Library order。Event Instrument 作为 Arrangement parent 的创建、打开、排序、复制、删除、颜色摘要、验证状态和 child Logical Track 管理由常驻 Arrangement 统一承担，见第 24 章。Event Instrument 详细定义仍在对象 Editor 中编辑。
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
General 只读显示：
```text
Notes
Events
Project Work Time
TPQ
```
`Notes` 与 `Events` 来自最近一次正式全 Project 编译结果：前者显示正 velocity `NoteOn` 数量，后者显示 canonical MIDI channel event 总数；不可消费结果显示 `0`。它们不允许编辑，不持久化，也不进入 Undo / Redo。`Project Work Time` 显示当前持久化累计值加本次打开会话截至刷新瞬间的累计值。

用户 Metadata：
```text
Project Name
Project Version
Author or Team
Original Work
Copyright
```
File Information 只读显示：
```text
Created Time
Modified Time
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
Lane activation Reset
Segment / consumer hard-boundary Reset
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

正式诊断的 `Message` 使用英文。Status Bar 的 `N Errors, N Warnings` 必须在每次后台验证或编译完成事件中，以同一最后尝试结果立即刷新；不得滞后一轮或在当前失败时仍显示前一次计数。
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
来源对象包含非法位置或 pitch 时，导航必须使用安全的显示投影并仍尽量定位该稳定 ID；不得因构建 viewport、lane 或 Selection 而使应用崩溃。
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
