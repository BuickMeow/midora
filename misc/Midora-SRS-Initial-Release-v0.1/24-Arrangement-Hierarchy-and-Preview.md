# 第 24 章 Arrangement 层级、父子轨道与概览渲染

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章定义 Arrangement 的唯一外层对象层级、Event Instrument / MIDI Channel Root 父节点、Logical / Pure MIDI 子轨道、层级 Mute/Solo、复制删除、跨类型 Note 剪贴板，以及 Pure MIDI Segment 与 Conductor 的高性能概览渲染。本章是这些主题的专项规范；与旧章节中的 Project Panel、Event Instrument Library Folder、独立全局 Track 列表、Unbound Logical Track 或平铺 Arrangement 描述冲突时，以本章与第 22 章不变量为准。

## 24.1 正式对象层级

Project 的正式 Arrangement 结构固定为：

```text
Project
├─ Conductor Track                         fixed first row
└─ Arrangement Parents                    explicit mixed order
   ├─ Event Instrument
   │  └─ Logical Tracks                   explicit child order
   └─ MIDI Channel Root
      └─ Pure MIDI Tracks                 explicit child order
```

强制规则：

```text
Conductor Track 必须且只能有一个，并且始终位于第一行。
Event Instrument 与 MIDI Channel Root 共用一个可混排的顶层顺序。
每个 Logical Track 必须且只能属于一个 Event Instrument。
每个 Pure MIDI Track 必须且只能属于一个 MIDI Channel Root。
不存在 Project 顶层的独立 Logical Track 顺序。
不存在 Project 顶层的独立 MIDI Channel Root 顺序；Root 顺序由混合父节点顺序中过滤 Root 得到。
不存在 Unbound / Unassigned Logical Track 正常状态。
```

Event Instrument Library 可以作为领域实现中的按稳定 ID 查找索引，但不再是用户可见 Workspace、独立手动顺序或 Folder 组织模型。Event Instrument 的唯一正式外层位置就是 Arrangement parent list；Library index 不得形成第二套所有权或顺序。

## 24.2 顺序与身份语义

### 24.2.1 顶层父节点顺序

顶层父节点顺序属于 Project Source Data，必须持久化、进入 Undo/Redo 并标记 Project Modified。Event Instrument 与 Root 可以任意交错，例如：

```text
Event Instrument A
MIDI Channel Root 1
Event Instrument B
MIDI Channel Root 2
```

从该混合顺序派生：

```text
Root allocation/export order = 过滤并保留所有 MIDI Channel Root 的相对顺序
Logical display/output auxiliary order = 依父节点顺序，再依各 Event Instrument child order 展平
Pure MIDI Track order = 依 Root 过滤顺序，再依各 Root child order展平
```

移动 Event Instrument 只改变正式展示、导航与依赖显示顺序，不得成为 Logical/Event Instrument Channel Unit 抢占优先级。移动 Root 会改变 Auto Root 的确定性分配顺序、Pure MIDI SMF Track 顺序和同 Root 相关诊断顺序，必须使相应 canonical 投影失效。

### 24.2.2 子轨道顺序

Logical Track 顺序只在其 Event Instrument 内定义；Pure MIDI Track 顺序只在其 Root 内定义。两者均为 Project Source Data。

Pure MIDI Track 顺序继续是同 Root 同 tick 总序的一部分。Logical Track 顺序只用于展示、确定性辅助顺序和按 Track 的输出命名，不得替代编译期资源分配语义。

### 24.2.3 稳定身份

父子关系、复制、移动、诊断与持久化必须使用稳定 ID。名称、显示序号、Port.Channel、文件路径或当前位置不得替代身份。重排和跨父移动保持原对象稳定 ID；复制产生全新稳定 ID。

## 24.3 创建、移动与改绑

### 24.3.1 创建入口

Arrangement Toolbar 左侧必须提供一个 Fluent System Icons `Add` 图标按钮。按钮弹出菜单：

```text
New Event Instrument
New MIDI Channel Root
```

主菜单 `Project` 必须提供相同两项。新建父节点追加到顶层父节点顺序末尾；新建 Event Instrument 是最小合法定义且 child list 为空；新建 Root 的默认 Routing Mode 为 `Auto` 且 child list 为空。

Logical Track 只能从某个 Event Instrument 的 Header / context menu 创建；Pure MIDI Track 只能从某个 Root 的 Header / context menu 创建。不存在脱离父节点创建子轨道的入口。

### 24.3.2 父节点移动

Event Instrument 与 Root Header 可拖动并在同一个顶层列表中混排。移动父节点必须携带整个 child subtree；不得改变任何 child 的稳定 ID、内容或父子关系。

### 24.3.3 子轨道移动

Pure MIDI Track 可在 Root 内重排，也可拖到其他 Root。跨 Root 移动提交正式 parent 变更，保持 Track、Segment、direct/opaque 内容和全部稳定 ID。

Logical Track 可在 Event Instrument 内重排，也可拖到其他 Event Instrument。跨 Event Instrument 移动沿用 Rebind 影响审查：保留 Segment、Logical Note、Logical Parameter Lane、隐藏内容和稳定 ID；不得按名称自动匹配、删除或重建 Logical Parameter Lane。确认并成功提交后，目标 Event Instrument 成为唯一父节点；不保留 Unbound 中间状态。

拖动只在越过通用控件拖动阈值后开始；Pointer Down 的轻微移动不得触发排序。插入线必须明确显示顶层、同父或跨父目标。

## 24.4 复制、剪切、粘贴、Duplicate 与删除

### 24.4.1 父节点完整复制

对 Event Instrument 或 Root 执行 `Copy`/`Paste` 或普通 `Duplicate` 时，复制完整 subtree：

```text
父定义与配置
全部 child Tracks
全部 Segments
全部 Notes / Points / direct / opaque data
父子内部引用
```

复制必须深拷贝全部可变 Project 对象，为每个复制对象生成新稳定 ID，并把 subtree 内引用重映射到新 ID。外部共享引用只在其正式语义要求共享时保留；不得让副本与原对象共享可变定义对象。

复制 Root 后，副本 Routing Mode 必须强制改为 `Auto`，不得复制为占用相同 Fixed Port.Channel。复制 Event Instrument 保留定义配置，但不会改动原 subtree。

`Duplicate` 把完整副本插入原父节点之后。Paste 在当前兼容插入目标之后插入；没有兼容目标时追加到对应顶层或目标父节点末尾。一次完整复制形成一个原子 Project Undo。

### 24.4.2 Duplicate Instrument Only

Event Instrument 菜单必须额外提供 `Duplicate Instrument Only`。该命令：

```text
深拷贝 Event Instrument 定义及全部内部对象；
生成全新稳定 ID 并重映射内部引用；
不复制任何 Logical Track；
创建一个 child list 为空的新顶层 Event Instrument；
把副本插入原 Event Instrument 之后；
形成一个 Project Undo。
```

### 24.4.3 子轨道复制

复制 Logical Track 时深拷贝其全部 Segment 和内容，并默认保留在同一 Event Instrument 下；复制 Pure MIDI Track 时深拷贝全部 Midi Segment 和 direct/opaque 内容，并默认保留在同一 Root 下。跨父 Paste 必须使用第 24.3.3 节的 parent/rebind 规则。

### 24.4.4 Cut 与拖动

Cut/Paste 和拖动是移动原对象，不是复制：对象及其 subtree 保持稳定 ID。跨父移动不得先创建副本再删除原对象；失败必须保持原结构不变。

### 24.4.5 删除

删除空父节点可直接提交。删除包含 child Track 的 Event Instrument 或 Root 必须先显示明确确认，列出将级联删除的 child Track 和主要内容摘要；确认后原子删除整个 subtree，并形成一个 Undo。不得把 child 降级为 Unbound、移到隐藏集合或留下孤立文件。

删除 child Track 时沿用 Track 删除确认和 Undo 规则。Conductor Track 不允许删除、剪切、复制、Duplicate、重排或更换父节点。

## 24.5 层级 Mute / Solo

Event Instrument / Root 父节点与 Logical / Pure MIDI 子轨道分别拥有独立的运行期 `Mute` 和 `Solo` 状态。它们：

```text
不属于 Project Source Data；
不持久化；
不进入 Undo/Redo；
不标记 Project Modified；
不影响 canonical、MIDI Export 或 Audio Render；
新建、打开或替换 Project 后全部为 false。
```

父节点开关不得改写 child 开关；child 开关也不得改写父节点开关。播放候选 Track 集合严格按以下顺序计算：

1. 若至少一个父节点 `Solo = true`：只考虑 `Solo = true && Mute = false` 的父节点；这些父节点内完全忽略所有 child `Solo` 状态，最终只输出 `child Mute = false` 的有效 child Track。
2. 否则，若任意 child Track `Solo = true`：在全 Project 范围只考虑 `child Solo = true` 的 Track，再排除 `parent Mute = true` 或 `child Mute = true` 的 Track。
3. 否则：输出 `parent Mute = false && child Mute = false` 的全部有效 child Track。

`Mute` 始终胜过同层或另一层的 `Solo`。可以同时 Solo 多个父节点或多个 child Track。Conductor Track 永远不受这些开关影响。

播放中改变任一层状态时，播放消费者必须在稳定 producer frontier 原子替换未来后缀，按来源精确释放新被过滤 Track 的活动 Note，并为重新进入候选集的 Track 恢复必要状态；不得修改 Project/canonical、不得清空无关 Track 或使 Playing 指针停滞。

## 24.6 跨 Logical / Pure MIDI 的 Note 剪贴板

Logical Note 与 Direct MIDI Note 允许通过 Copy/Cut/Paste 在 Logical Segment 与 Midi Segment 之间转换。只转换双方共同字段：

```text
relative Tick
Gate Length
Key Number
NoteOn / Instance Velocity
```

转换规则：

```text
Logical → Direct：Direct NoteOff Velocity = 0。
Direct → Logical：丢弃 Direct NoteOff Velocity。
Direct → Direct：保留导入或用户数据中的 NoteOff Velocity。
Logical → Logical：保持 Logical Note 的既有共同字段语义。
```

Direct MIDI Note 的 NoteOff Velocity 虽然可被当前 BASS 音频后端忽略，但必须在 Project、canonical SMF projection、Direct Note 移动/Resize/复制和 MIDI 导出中保留。

本能力不转换 Segment、Logical Parameter、MIDI Channel Event、opaque event、Event Instrument 定义或 Root。Paste 仍服从目标 Segment 暴露范围、pitch/tick 边界、精确同 Tick+Key newcomer 冲突和一个操作一个 Undo 的既有规则。

## 24.7 Arrangement Workspace

### 24.7.1 唯一外层入口

现有软件左侧 Project Panel 删除。Arrangement 是唯一的：

```text
外层对象创建入口；
父节点与子轨道正式顺序定义处；
父子关系编辑入口；
轨道 Segment 总览；
Event Instrument / Root / Track context menu 入口。
```

Project Settings、Diagnostics、Conductor Editor 和其他 Workspace 继续通过主菜单、状态栏或明确导航命令打开。删除 Project Panel 不删除这些 Workspace。

Arrangement Workspace 在 Project 打开期间常驻、始终为第一个 Tab、不可关闭、不可重排。顶层父节点与 child 展开/折叠只改变当前会话显示，不改变 Project。

### 24.7.2 行层级与布局

Arrangement 行顺序固定为：

```text
Conductor Track row
each mixed parent row
    zero or more child Track rows when expanded
```

父节点行比普通 Track 行紧凑，因为父节点不承载 Segment。父节点只在左侧 Header 提供展开箭头、名称、摘要、Mute/Solo 与 context menu；右侧 Timeline 内容区保持留白和非交互，但 Bar/Grid 线必须连续绘制，不能制造断层。

Event Instrument 与 Root child Track 使用相同的二级缩进；所有 Header 的右边界保持对齐。Pure MIDI Track 名称左侧显示 MIDI 图标。Event Instrument Header 显示名称和定义状态；Root Header 显示名称、`Auto`/`Fixed`、Fixed 时的 Port.Channel，以及 `Melodic`/`Percussion`。

Logical Track Header 不再显示 Bind/Unbind；它位于当前 Event Instrument 下即表示唯一绑定。可以显示父 Event Instrument 的次级摘要。MIDI Track Header 显示其 Track 名称和 MIDI 图标，不重复 Root 名称。

### 24.7.3 打开与菜单

双击 Event Instrument Header 打开或激活该 Event Instrument Editor。双击 child Track 或 Segment 使用既有 Arrangement/Segment 导航语义。展开/折叠只由左侧 disclosure target 触发，双击 Header 不应意外折叠。

Event Instrument 菜单至少包含：

```text
New Logical Track
Open Editor
Copy / Cut / Paste
Duplicate
Duplicate Instrument Only
Rename
Move Up / Move Down
Delete
```

Root 菜单至少包含：

```text
New MIDI Track
Copy / Cut / Paste
Duplicate
Rename
Settings (Name, Routing, Port, Channel, Channel Mode)
Move Up / Move Down
Delete
```

Logical/Pure MIDI Track 菜单至少包含既有的 Copy/Cut/Paste/Duplicate/Rename/Delete/Move、选择其全部 Segment（替换或追加）和适用的 Segment 命令。Logical Track 菜单不提供 Unbind；Pure MIDI Track 菜单不提供 Event Instrument binding。

通用快捷键：`Ctrl+C`、`Ctrl+X`、`Ctrl+V`、`Ctrl+D`、`F2`、`Delete` 按当前 Header 焦点和兼容目标路由。命令不得依赖已经失效的 Project Panel selection。

## 24.8 Pure MIDI Segment 概览

### 24.8.1 图层与视觉

Logical Segment 继续只显示 Note Preview。Pure MIDI Segment 必须显示两个独立概览图层：

```text
lower layer: Direct MIDI Note graphics
upper layer: non-Note MIDI event vertical lines
```

所有 non-Note event 线使用同一种与 Note 明确区分的颜色，不按 event type 改色。Event 线透明度固定为 `50%`，并绘制在 Note 图形上层，使极密 Note 与 Event 仍可同时辨认。

每个 event 线：

```text
横向位于 event tick；
从 Segment 可视内容底部向上绘制；
高度 = normalizedValue × 100%；
最小宽度 = 1 device pixel；
value = 0 时仍至少显示 1 device pixel 高度。
```

数值规范化：

```text
7-bit CC / Program / Poly Pressure / Channel Pressure / Channel Mode: value / 127
Pitch Bend: unsigned14 / 16383
无标量值的 opaque SysEx/Meta: full height presence line
```

如果当前缩放下多个 event 落到同一 device-pixel column，以该列最大高度聚合；不得通过叠画数量提高不受控亮度。聚合只属于概览 LOD，不改变命中、编辑、canonical 或导出事件。

### 24.8.2 坐标和裁剪

Note 与 event 必须使用同一个 Segment-local tick 到 device pixel 变换，并严格裁剪到当前 Segment 暴露 Content Window。视图平移时是内容在世界坐标中的连续平移，不得把每个可见片段重新当作 Segment tick 0，也不得水平压缩整张缓存。Segment 本地 tick 0 的 Note/Event 不得因边界查询丢失。

### 24.8.3 缓存与性能

Pure MIDI Segment 概览必须使用手工渲染、分块缓存和可视范围查询，不得为每个 Note/Event 创建 WPF Control。Note 与 Event 使用独立内容指纹和独立 tile/layer：

```text
Note 编辑只失效受影响 Note tiles；
Event 编辑只失效受影响 Event tiles；
Segment 内容窗口改变只失效受影响投影范围；
Segment 移动、选择、播放指针、Grid、普通 pan 不重建未变化内容；
折叠父节点只停止组合不可见 tiles，不要求无条件销毁可复用缓存。
```

cache key 至少包含：Segment 稳定 ID、相应内容 fingerprint、精确 tick-to-device transform/LOD、DPI 与 style revision。Grid、play/edit cursor、selection outline、hover 和 drag preview 不得烘焙进稳定内容 tile。

在极端内容下，UI 线程只组合可见 tile 和少量 transient overlay；后台 tile 失败不得阻塞输入或回退到逐对象 WPF 绘制。对象命中和编辑使用原始稳定 ID 及空间索引，不从 bitmap 反推对象。

## 24.9 Conductor Arrangement 概览

Conductor Track 不使用 Segment。Arrangement 第一行直接按 absolute Project tick 显示 Conductor 事件概览：

```text
Tempo
Time Signature
Key Signature
Marker
other supported Conductor point events
```

不同事件类型使用不同且稳定的颜色。普通事件使用与缩放无关的固定 device-size 圆点；同 tick 多类型可使用固定的纵向 band/offset 保持可辨。Project End Marker 继续使用专用竖线，不转换为圆点。

该行只提供概览和导航；精确编辑仍进入 Conductor Editor。概览必须使用独立的分块缓存、可视 tick 查询和空间索引；不得为每个事件创建 WPF Control。极端缩小时按 `(tile, device-pixel column, event type)` 聚合，一个类型在同列最多绘制一个点。

Conductor 内容变更只失效覆盖受影响 tick/type 的 tiles。Grid、play/edit cursor、selection/hover 与 Project End Marker transient state 不进入稳定点缓存。任何命中或导航都使用正式 Conductor event ID/index，不从像素颜色反推。

## 24.10 持久化

`project.json` 必须保存一个有序 tagged union `Arrangement Parent` 索引，每项至少冻结：

```text
Parent Kind: Event Instrument | MIDI Channel Root
Parent Stable ID
Object file path
Display-name snapshot
```

Event Instrument protobuf 保存其 ordered Logical Track stable ID references；Logical Track protobuf 保存唯一 parent Event Instrument ID。Root protobuf 与 Pure MIDI Track protobuf 同理保存双向 parent/child 关系。`project.json`、parent object、child object 三方必须一致。

以下内容不再持久化：

```text
Event Instrument Library folders
independent Event Instrument manual order
independent global Logical Track order
independent global Root order
Unbound/Unassigned Logical Track state
Project Panel width/collapse/selection
```

父节点展开状态、Arrangement viewport、selection、Header focus 与 Mute/Solo 属于 Project Session UI State 或 Runtime State，不进入 `.midora`。

父子索引缺失、重复、多重归属、kind 不匹配或顺序不一致属于结构损坏。单个 parent object 文件损坏但 `project.json` 仍能可信确定 subtree 时，可以创建同位置的 Damaged Parent Placeholder 并保留 child 归属；不得把 child 降级为 Unbound。无法可信确定唯一父子关系时打开失败。

本次是未发布开发格式的破坏性基线替换：产品与 SRS 版本继续为 `v0.1`，但实现必须更新内部 schema/descriptor/file-format 基线并明确拒绝旧开发布局；不得双写或静默迁移旧 Event Instrument Folder / Unbound Track 布局。

## 24.11 SMF 导入与导出结构

`Open MIDI as New Project` 产生的 Root 按导入确定顺序写入混合父节点列表；其 Pure MIDI Tracks 写入各 Root child order。新建 Project 不自动创建 Event Instrument，因此导入结果可以只包含 Root parents。

标准 SMF Track Name 只写 Pure MIDI child Track 名称，不写 Root 名称。可忽略的版本化 Midora Sequencer-Specific Meta 可以保存 Root 名称、Root filtered order、child order、routing 和 channel mode，用于 Midora → MIDI → Midora 的结构恢复；它不得影响普通 MIDI 播放，也不得替代标准 Track Name、MIDI Port 或 Channel status。

## 24.12 失败、诊断与原子性

以下操作必须整体拒绝且不留下部分结构：

```text
创建无父 Logical/Pure MIDI Track；
同一 child 同时属于多个 parent；
跨 kind parent 归属；
复制后出现重复稳定 ID 或未重映射 subtree 引用；
Fixed Root copy 保留冲突路由；
删除 non-empty parent 未获得确认；
跨父移动/改绑验证失败；
无法可信恢复父子关系的项目打开。
```

结构错误产生可定位到 parent/child stable ID 的英文 Error。普通空 Event Instrument 或空 Root 合法且不产生诊断。未引用 Event Instrument 不再是独立概念：顶层 Event Instrument 即使没有 child Track 仍是合法空 parent。

## 24.13 明确非目标

初版不提供：

```text
可见 Event Instrument Library Workspace；
Event Instrument Folder / Unfiled 分组；
Project Panel；
Unbound Logical Track；
父节点右侧直接承载 Segment；
Root 名称作为标准 SMF Track Name；
Logical Segment 的 non-Note event 概览；
从概览 bitmap 反推命中或音乐语义；
跨类型 Segment / Parameter / Channel Event 自动转换。
```

## 24.14 验收与验证门

至少覆盖：

```text
混排父节点保存/重开/Undo/Redo 与确定性顺序；
过滤 Root 顺序后的 Auto allocation 与 SMF Track order；
Logical/Pure child 同父重排与跨父移动；
Rebind 失败原子性和隐藏 Segment 内容保留；
完整 subtree Copy/Paste/Duplicate 的 ID/reference remap；
Duplicate Instrument Only 不复制 Track；
Fixed Root duplicate 强制 Auto；
non-empty parent cascade delete、确认和单 Undo；
三层父/子 Mute-Solo 分支及播放中原子切换；
Logical↔Direct Note clipboard 和 Direct NoteOff Velocity 保留；
Project Panel/Folder/Unbound schema 明确拒绝；
Pure MIDI preview 的 Event-above-Note、50% opacity、1px 最小线与缩放/平移正确性；
Note/Event 独立 tile invalidation 和百万级可视内容性能；
Conductor 多类型颜色、固定设备圆点、极端事件聚合和局部失效；
Grid/cursor/selection 变化不重建稳定内容 tile；
损坏 parent placeholder 不制造 Unbound child。
```
