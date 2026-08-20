# 第 24 章 Arrangement 平铺轨道、共享执行组与概览渲染

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 本章最近破坏性修订：**2026-08-20**

## 24.1 目的与优先级

本章定义 Arrangement 的正式平铺顺序、Event Instrument Definition、Event Instrument Usage、MIDI Channel Root、共享状态组、轨道拖放、Event Instruments 辅助栏，以及 Pure MIDI / Conductor 高性能概览。

本章替代 2026-08-18 建立的可见两级 parent/child Arrangement。与旧章节中的以下描述冲突时，以本章与第 22 章不变量为准：

```text
Event Instrument / MIDI Channel Root 占据可见 parent row；
parent child list 决定 Track 顺序；
Event Instrument 普通 Duplicate 级联复制 child subtree；
空 Fixed Root 合法并预留 Unit；
Logical Track 直接以 Event Instrument ID 表示独立 binding；
删除 Event Instrument 级联删除其全部 Logical Tracks。
```

本次仍是未发布开发期破坏性格式替换，不更新产品/SRS 版本，不兼容读取旧开发布局。

## 24.2 正式对象与权威顺序

### 24.2.1 Event Instrument Definition

Event Instrument 是可复用、可独立保存和编辑的重型声音定义。Project 保存独立的、有序 `Event Instrument Definition Index`。Definition：

```text
可以没有任何 Usage；
不会因为 Track 或 Usage 被删除而自动删除；
不直接占用 Channel Unit；
不直接出现在 Arrangement 轨道行；
不拥有 ordered Logical Track child list。
```

### 24.2.2 Event Instrument Usage

`Event Instrument Usage` 是 Project 内部、无用户名称的共享执行身份。每个 Usage：

```text
有稳定 ID；
引用且只引用一个 Event Instrument Definition；
被一个或多个 Logical Track 引用；
定义共享 Channel 状态、Segment 活动连通区间、Overlap 域、编译 dirty 域和缓存 owner；
不进入普通 Event Instrument 管理列表；
成员数降为 0 时必须在同一原子编辑中删除。
```

Usage 不是 Event Instrument Instance。Instance 仍表示一次 Logical Note 触发形成的编译/运行时实例。

一个成员的 Usage 在 UI 中表现为普通独立 Logical Track；两个及以上成员的 Usage 表现为 Shared Logical Track block。

### 24.2.3 MIDI Channel Root

MIDI Channel Root 仍是 Pure MIDI 的持久 Channel Unit、共享状态和生命周期身份。每个 Pure MIDI Track 必须引用且只引用一个 Root。

所有 Root 必须至少有一个 Track。Auto/Fixed Root 的最后一条 Track 被删除、剪切、改路由或移走时，Root 必须在同一原子编辑中自动删除；Undo 必须以原稳定 ID、路由、模式和成员关系恢复它。

Fixed Root 不具有独立用户可见对象生命周期：

```text
没有“创建空 Fixed Root”入口；
没有独立 Fixed Root row；
没有独立 Fixed Root 排序或删除命令；
Port、Channel、Routing Mode 和 Channel Mode 在 UI 中表现为 MIDI Track 的 Route 属性；
底层仍只在 Root 保存一份权威值，Track 不复制这些字段。
```

Auto Root 只有在多个 Track 共享时，才以 Shared MIDI Track block 的形式显式表现其组身份。

### 24.2.4 全局 Arrangement Track Order

Project 保存唯一、有序、tagged 的 `Arrangement Track Order`：

```text
Logical Track | Pure MIDI Track
```

它是以下语义的唯一权威：

```text
Arrangement 可见轨道顺序；
SMF 导入后的用户轨道顺序；
Pure MIDI SMF Track Projection 顺序；
同 Usage / Root 的跨 Track 同 tick 确定性顺序；
Auto Root 与 Usage 的首次出现顺序；
Track 编号、Move Up/Down 和拖放插入位置。
```

Conductor 固定显示为第一行，但不进入该集合。Event Instrument Definition order、Usage membership 和 Root membership 不得替代或重复保存 Track order。

## 24.3 Logical Track 绑定与空壳状态

Logical Track 保存可空的 `Event Instrument Usage ID`，并通过 Usage 间接引用 Definition。

`New Logical Track` 可以创建未指定 Usage 的空壳 Track。该状态仅用于安排和后续指定乐器：

```text
允许命名、排序、复制和删除；
没有 Segment/Note/Parameter 内容时不产生诊断；
在指定 Event Instrument 前禁止创建或粘贴音乐内容；
若结构损坏或非法路径形成“有内容但无 Usage”，编译为 Error。
```

`New Logical Track with Instrument...` 或 `Add Logical Track Using This Instrument` 必须创建新的独立 Usage，再创建并绑定 Track；不得默认把新 Track 加入同 Definition 的既有 Usage。刚创建新 Definition 时，Definition + Usage + Track 是一次原子编辑，成功后打开 Definition Editor。

Track 菜单必须提供：

```text
Assign / Change Event Instrument...
Share Instrument State With...
Make Independent
```

`Share Instrument State With...` 选择目标 Logical Track/Usage。目标使用相同 Definition 时直接加入；Definition 不同时属于 Rebind，必须使用既有影响审查，取消或失败不得留下部分修改。

`Make Independent` 创建引用同一 Definition 的新 Usage，并只迁出当前 Track。旧 Usage 无成员时自动删除；变为单成员时保留但隐藏 block brace。

## 24.4 共享执行语义

### 24.4.1 Usage 活动连通区间

对一个 Usage 的全部参与 Logical Tracks，将其 Segment Project ranges 求并集；重叠或首尾相接的 ranges 形成一个 `[startTick, endTick)` 活动连通区间。该区间是真正的共享 Channel Group 生命周期。

当 Event Instrument 未启用 Per-Note Instance Isolation：

```text
一个 Usage 活动区间按 SubVoice 分配一个共享 Channel Unit；
Usage 内所有成员 Track 的实例共享该 SubVoice 的 Channel 状态；
同 tick 顺序使用全局 Arrangement Track Order，再使用对象显式顺序/稳定顺序；
Overlap Policy / Scope 在整个 Usage 内验证，不能通过拆成多条 Track 绕过；
成员 Segment End 只精确关闭该 Segment 拥有的 Note/Instance；
成员 Segment End 不发送 Usage 级 CC120/Reset，不杀死 sibling Track 的 Note；
Usage 活动区间结束才执行精确 NoteOff、CC120、最终 Reset 和 Unit 释放。
```

启用 Per-Note Instance Isolation 时，每个 Note 仍使用独立 Channel Group；Usage 继续承担 Definition 绑定、顺序、Overlap 域、监控来源与缓存 dirty owner，但不把并发 Note 合并到同一 Unit。

### 24.4.2 Usage 缓存与增量编译

Logical Segment 可继续产生规范化 fragment，但共享状态的正式合并层必须以 Usage 为 owner：

```text
Segment normalized fragment
→ Usage merged event/checkpoint stream
→ Usage/SubVoice raw PCM fragment
```

编辑一个成员 Track 时，dirty 起点至少回退到该 Usage 中最早受影响 tick；后续只可在状态与活动 Note 集均收敛后复用。不得错误失效其他 Usage/Root，也不得按成员 Track 分别合成后求和。

## 24.5 Root 路由与 Track 属性 UX

### 24.5.1 Fixed 路由

编辑某 MIDI Track 的 Fixed route 实际执行 Root membership 变更：

```text
目标 Port.Channel 未使用：创建新 Fixed Root并迁入 Track；
目标 Port.Channel 已由 Fixed Root 使用：加入该 Root；
离开后的旧 Root 无成员：自动删除；
整个变更是一个失败原子 Undo。
```

同一个 Port.Channel 只能有一个 Fixed Root。Fixed Track 可以在全局 Arrangement 中任意分散，不能强制连续；这用于保持导入 SMF 的 MTrk 顺序。

每条 Fixed Track Header 显示 route chip。多个 Track 共享同一 Fixed Root 时，chip tooltip/settings 显示 `Shared with N tracks`，悬停可低强调高亮可见成员。修改共享 Root 的 Channel Mode 会影响同 Root 全部 Track，必须在多成员时明确提示并确认；同一 Port.Channel 不允许同时拥有 Melodic 与 Percussion 两种模式。

### 24.5.2 Auto 路由

一个 Track 的独立 Auto Root 表现为 `Auto`。多个 Track 共享一个 Auto Root 时，它们必须在全局 Track Order 中连续，形成 Shared MIDI Track block。

Auto→Fixed 时解除连续约束且不自动改动全局顺序。Fixed→既有 Auto block 时加入 block；Fixed→新独立 Auto 时创建新 Auto Root。将若干非连续 Fixed members 作为一个整体改为 Auto 前，必须预览并确认将它们收拢为连续 block 的原子重排。

### 24.5.3 新建 MIDI Track

Arrangement `+ → New MIDI Track...` 提供：

```text
New Auto MIDI Channel
New Fixed MIDI Channel (Port, Channel, Melodic/Percussion)
Use Existing MIDI Channel
```

选择未使用 Fixed Port.Channel 时原子创建 Root + 首条 Track；选择已使用 Port.Channel 时加入现有 Root，并禁用会与其 Root 配置矛盾的字段。不存在仅创建 Root 的提交结果。

## 24.6 平铺 Arrangement UI

### 24.6.1 行与工具栏

Arrangement 行顺序固定为：

```text
Conductor
Arrangement Track Order[0]
Arrangement Track Order[1]
...
```

不显示 Event Instrument 或 Root 空白 parent row。Logical/Pure MIDI Track 均直接承载 Segment，继续使用手工渲染、可视 tile 和范围查询，不得为 Segment/Note/Event 堆 WPF Control。

工具栏从左至右至少包括：

```text
Event Instruments pane toggle（Fluent chevron + “Event Instruments”）
red Fluent + creation menu
其他 Timeline 工具
```

创建菜单固定提供：

```text
New Logical Track
New Logical Track with Instrument...
New MIDI Track...
```

主菜单 Project 也提供等价入口。播放或其他 Project 编辑锁期间创建/结构编辑命令禁用。

### 24.6.2 Event Instruments 辅助栏

Event Instruments pane 是 Arrangement 内的 Definition Browser，不是旧 Project Panel。它显示全部 Definition，包含未使用项及 usage/track count，并保存独立 Definition order。

支持：

```text
New / Copy / Cut / Paste / Duplicate / Rename / Edit / Move / Delete
Add Logical Track Using This Instrument
拖 Definition 到 Arrangement 空隙以创建独立 Usage + Logical Track
```

删除被引用 Definition 不得级联删除 Track。默认必须阻止，并列出引用 Usage/Track；用户须先改绑或删除相关 Track。删除最后一个 Track/Usage也绝不删除 Definition。

### 24.6.3 Header 与块视觉

所有 Track Header 预留同宽的左侧 group gutter，使独立 Track 与 block member 的标题对齐。

以下对象在成员数至少为 2 时形成连续 block：

```text
同一 Event Instrument Usage 的 Logical Tracks；
同一 Auto MIDI Channel Root 的 Pure MIDI Tracks。
```

block gutter 绘制跨全部成员的大括号。括号区域是独立 hit target：hover 高亮，按下后越过通用拖动阈值才拖动整个 block；右键提供共享状态/route、Mute/Solo group、Make Independent 等适用命令。Fixed Root members 不绘制 block。

Track Header 保留类型图标、名称、route/instrument 摘要、Mute/Solo、hover/pressed 和菜单。Header 点击不形成持久单选；右键菜单目标必须来自本次指针 hit test，空白右键不得复用旧目标。

## 24.7 Track 拖放与组变更

### 24.7.1 一般规则

拖放开始必须越过通用总移动阈值。所有排序、Usage/Root 创建删除、Definition rebind 和 global order 修改构成一次原子 Project Edit；失败或取消时 Project 完全不变。

Logical 与 Pure MIDI Track 不允许跨类型成组。brace drag 只整体重排 block，永不合并到另一个 block。

### 24.7.2 目标区域

共享 block 的上、下边缘内侧各提供约 8 DIP 的外部插入 hit zone，实际绘制 3–4 DIP 的低强调实线；中间 body 是“加入 block”目标。目标切换使用约 4 DIP hysteresis，避免边缘抖动。

```text
外部 Track → block body：加入目标并追加为最后成员，目标 block 全体显示虚线外框；
外部 Track → block top/bottom strip：放在 block 前/后，保持或变为独立；
其他 block member → target body：离开旧组并加入目标末尾；
同 block member → member insertion gap：精确内部重排；
同 block member → block exterior strip：脱离为新的独立 Usage/Auto Root；
brace → global gap：整体移动 block。
```

同 block 内第一行上半部和最后一行下半部必须分别能定位到第一/最后成员。相邻 block 间归一化为一个全局插入 gap，不得出现两个竞争目标。

### 24.7.3 singleton 与 Fixed chip drop

singleton Usage/Auto Root 没有 brace，因此其 Instrument/Auto chip 是显式 join target。Fixed Track 可分散排列，只有其 Fixed route chip（而非整行 body）是“加入该 Fixed Root”的目标；整行其余区域继续表示普通排序。

不同 Definition 的 Logical Track 拖入 Usage 时，使用琥珀色虚线预览并执行 Rebind 影响审查。取消时不移动 Track。

## 24.8 Copy / Cut / Paste / Duplicate / Delete

### 24.8.1 Event Instrument Definition

Definition Copy/Paste/Duplicate 只深拷贝 Definition 与全部内部对象，生成并重映射全部稳定 ID，不复制任何 Track 或 Usage。`Duplicate Instrument Only` 与 Definition Browser 的普通 Duplicate 语义相同；保留该命令名称用于 Track/Usage 上下文中的明确入口。

### 24.8.2 Logical Track

Logical Track Duplicate 深拷贝 Track、Segments 和内容，默认保留同一 Usage，并插入源 Track 后。若源为独立 singleton，这会形成 Shared block。Paste 到明确 Usage target 时加入目标；普通空白 Paste 创建引用同一 Definition 的新独立 Usage。未绑定空壳 Track 的副本仍未绑定且必须保持无内容约束。

### 24.8.3 Pure MIDI Track

Pure MIDI Track Duplicate 深拷贝 Track/Segments/direct/opaque 内容并保留可见 route：Fixed 副本加入同一 Fixed Root；Auto 副本加入同一 Auto Root。普通 Paste 到 route/block target 时加入目标；空白 Paste 使用来源 route，Fixed route 已存在时加入现有 Fixed Root，不创建冲突 Root。

### 24.8.4 Cut / Delete 与自动 owner 清理

Header/brace Drag Move 在一个原子命令中保持对象稳定 ID。Clipboard Cut 先冻结不可变快照再执行删除；后续 Paste 与普通 Copy 一样创建新的 Track/内容稳定 ID，避免 Cut 被 Undo 后再 Paste 时发生身份冲突。删除 Track 继续按内容确认规则执行。任一删除/移动后 Usage/Root 成员数为 0 时自动删除 owner；该删除命令的 Undo 必须恢复原 Track、owner、stable ID、global order、membership 和配置。

## 24.9 Mute / Solo

Track Mute/Solo 仍只属于运行期，不持久化、不进入 Undo、不影响 canonical、MIDI Export 或 Audio Render。Shared block/root 可以有独立 group Mute/Solo runtime state；它不改写成员 Track 开关。

运行期过滤必须按来源精确释放被过滤 Track 的活动 Note并恢复重新进入成员所需状态。不得因单个 Track Mute 对整个 Usage/Root发送CC120/Reset，不得杀死 sibling Note、卡住 producer 或无限 Buffering。

## 24.10 跨 Logical / Pure MIDI Note 剪贴板

Logical Note 与 Direct MIDI Note 只转换共同字段：relative Tick、Gate Length、Key、NoteOn/Instance Velocity。Logical→Direct 的 NoteOff Velocity 为 0；Direct→Logical 丢弃 NoteOff Velocity；Direct→Direct 保留它。该能力不转换 Segment、参数、Channel Event、Definition、Usage 或 Root。

## 24.11 Pure MIDI Segment 与 Conductor 概览

Pure MIDI Segment 的 Note 与 non-Note event 使用独立 tile/layer。event 线位于 Note 上层、透明度 50%、至少 1 device pixel，高度按正式值域归一化；同 device column 使用最大高度聚合。Logical Segment 继续只显示 Note。

Conductor 固定第一行，直接显示按类型着色且大小不随缩放变化的圆点；End Marker 仍为专用竖线。

两者均必须：

```text
只查询可见 source pages/range；
使用分块缓存、LOD 和局部失效；
不创建逐对象 WPF Controls；
不把 Grid/cursor/selection/hover 烘焙进稳定 tile；
不从 bitmap 反推 hit test 或音乐语义；
在数千万对象时不建立全 Segment render array/dictionary/index。
```

## 24.12 SMF 导入与导出顺序

`Open MIDI as New Project` 按源 MTrk index 排列导入 Track；单 MTrk 被 Port/Channel 拆分时，派生 Track 按该 MTrk 中 effective Port.Channel 首次出现顺序紧随排列。标准 MIDI 无法表达 Auto Root，因此普通外部导入产生 Fixed Roots。

Pure MIDI SMF Track Projection 过滤全局 Arrangement Track Order 中的 Pure MIDI Tracks；Root 不再决定 MTrk 分组顺序。同 Root 同 tick canonical merge 也使用该全局顺序。Logical Unit MTrk 继续按分配后的 Port/Channel 顺序位于 Pure MIDI MTrks 之后。

Midora Sequencer-Specific Meta 可以保存 Track/Root stable ID、membership、routing、mode 和 Auto identity，以支持 Midora→MIDI→Midora 结构恢复。若第三方重排使恢复出的 Auto members 不连续，导入必须保留文件 Track order、放弃该 Auto 恢复并按实际 Fixed Port.Channel 重建，同时给出一次性 Warning。

## 24.13 持久化

`project.json` 必须保存：

```text
ordered Event Instrument Definition index；
ordered Event Instrument Usage index；
ordered MIDI Channel Root index；
ordered tagged Arrangement Track index；
各对象文件路径与名称快照。
```

对象文件必须保存：

```text
Event Instrument：Definition 本体，不保存 Track child list；
Event Instrument Usage：ID + Definition ID；
Logical Track：可空 Usage ID + Track/Segment 内容；
MIDI Channel Root：Routing/Port/Channel/Mode，不保存 Track child order；
Pure MIDI Track：Root ID + Track/Segment/direct/opaque 内容。
```

membership 由 Track 的 owner ID 表达，顺序仅由 Arrangement Track index 表达；不得再保存 parent ordered child arrays 形成第二套顺序。以下结构均拒绝打开/编译：

```text
Usage 或 Root 无成员；
非空 Logical Track 无 Usage；
Track owner 缺失或 kind 错误；
同一 Track 在 Arrangement index 缺失或重复；
Shared Usage / Auto Root 成员在 Track order 中不连续；
两个 Fixed Root 使用同一 Port.Channel；
对象 ID、manifest、index 与文件 path 不一致。
```

Definition pane 展开、Arrangement viewport、selection、header focus、Mute/Solo 和 drag preview 属于 session/runtime state，不进入 `.midora`。

## 24.14 失败原子性与验收门

至少覆盖：

```text
混合 Logical/Pure global track order 保存、重开、Undo/Redo；
SMF 导入/导出保持源 Track 顺序与多 Channel bucket 次序；
Definition 0 usage 持久化，最后 Track 删除不删除 Definition；
Usage/Auto/Fixed Root 最后成员离开时同事务删除且 Undo 恢复原 ID；
Fixed route UI 改属、既有 P.C 合并及共享 Mode 确认；
shared Usage 活动连通区间、跨 Track overlap、同 tick 顺序与 Unit 数；
成员 Segment End 不清空 sibling state/note，Usage end 才最终 cleanup；
Auto/Usage block 连续性、brace reorder、body join、edge detach 和 hysteresis；
Fixed Track 任意分散且 route chip join；
不同 Definition rebind 取消/失败原子性；
未绑定空壳限制与非法非空无 Usage诊断；
Definition/Track/Usage/Root Copy/Paste/Duplicate 的 ID/remap/membership；
Pure MIDI event-above-note preview、Conductor tile 和极端范围查询性能；
后台 Full/Incremental 对共享 Usage 完全等价。
```

## 24.15 明确非目标

初版不提供：

```text
可见 Event Instrument Usage 管理器或 Usage 命名；
空 MIDI Channel Root 或 Unit 预留 UI；
Event Instrument/Root 的 Arrangement 空白 parent row；
按 Definition 自动让所有 Logical Tracks 共享状态；
Fixed Root members 强制连续；
从概览 bitmap 反推命中；
把 Route 字段复制到每个 Pure MIDI Track 形成多份权威值；
旧树形开发布局的迁移、双写或兼容读取。
```
