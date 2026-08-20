# Midora Arrangement Hierarchy and Preview Requirement Trace

状态：2026-08-20 已实施并通过自动回归；WPF 实机视觉与拖放验收待产品所有者执行。
正式依据：SRS 第 24 章、INV-058～INV-064、ADR-CORE-045、ADR-UI-039～040、ADR-PMIDI-009。

## 1. 输入

```text
Project mixed Arrangement parent order
Event Instrument definitions and ordered Logical Track children
MIDI Channel Roots and ordered Pure MIDI Track children
Logical/Midi Segment exposed content windows
Direct MIDI Notes / non-Note events / opaque events
Conductor events
runtime parent/child Mute-Solo states
viewport, DPI, style revision and presentation snapshots
clipboard payload and destination context
```

## 2. 正式输出

```text
唯一的 Conductor-first two-level Arrangement projection
deterministic parent/child Project order
validated parent ownership and rebind commands
runtime audible child Track set
deep-copy/remapped Project subtree
Logical↔Direct Note conversion result
cached Pure MIDI Note/Event overview layers, with Event above Note at fixed 50% opacity
cached Conductor point overview layer
strict .midora hierarchy index
```

## 3. 边界与失败条件

- Conductor 固定第一行；Event Instrument/Root 混排；Logical/Pure Track 必有且仅有一个正确类型父节点。
- 父节点复制深拷贝 subtree；Root 副本强制 Auto；`Duplicate Instrument Only` 不复制 Logical Tracks。
- non-empty parent 删除先确认并级联；失败、取消或验证错误不得留下部分删除、孤儿或 Unbound Track。
- Logical Track 跨 Event Instrument 沿用 rebind 影响审查；Pure Track 跨 Root 保留 direct/opaque 数据。
- Mute/Solo 只在播放消费者运行期过滤，不进入 Project/canonical/成品输出。
- Pure MIDI non-Note event 线固定绘制在 Note 图形上层，透明度为 50%；Note/Event 分层缓存和聚合不得改变该合成顺序或通过重复叠画提高亮度。
- preview bitmap/tile 只属于 UI session；缓存失败不阻塞编辑，也不成为业务语义来源。
- 父子索引无法可信建立时打开失败；可隔离的单 parent 文件损坏使用原位置 Damaged Parent Placeholder。

## 4. 诊断

结构错误使用英文 Error，并携带 parent/child stable ID 与 object kind。空 parent 不产生诊断。Runtime Mute/Solo 或 tile 失败只进入 runtime trace/受控状态，不污染 Project 编译诊断。

## 5. 持久化归属

进入 `.midora`：混合 parent order、parent/child stable-ID references、Event Instrument/Root/Track/Segment 内容、Direct NoteOff Velocity。  
不进入 `.midora`：Project Panel、Library Folder、Unbound 状态、展开折叠、选择、viewport、tiles、Mute/Solo、clipboard、hover/drag overlay。

## 6. 运行时归属

Parent/child Mute-Solo、audible-set generation、producer-frontier replacement、render snapshot、tile cache、LOD aggregation、spatial index、selection、focus 和 drag state 均为运行时/会话状态。

## 7. 明确非目标

```text
visible Event Instrument Library Workspace
Event Instrument folders
Unbound Logical Tracks
Root name as standard SMF Track Name
Logical Segment non-Note preview
bitmap-based hit testing
cross-type Segment/event/parameter conversion
old development-format migration or dual write
```

## 8. 验证矩阵

| 风险 | 必要验证 |
|---|---|
| 顺序/归属漂移 | mixed-order save/open golden、乱序输入、重复/多重 parent 拒绝、Full/Incremental 等价 |
| Copy/Delete 数据损坏 | complete subtree ID remap、external-reference boundary、cancel/failure atomicity、Undo/Redo |
| Mute/Solo 错误可听集合 | parent-solo、child-solo、双层 mute、多个 solo、播放中快速切换和来源精确 Note 清理 |
| 跨类型 Note 丢字段 | Logical→Direct off velocity 0、Direct→Logical drop、Direct→Direct preserve、边界/冲突/Undo |
| Segment preview 错位 | tick 0、crop、pan、zoom、DPI、tile seam、同列 aggregation、Event-above-Note 50% opacity screenshot golden |
| UI 卡顿 | 极端 Note/Event Segment 可见/不可见、scroll/zoom、独立 layer invalidation、UI-thread work budget |
| Conductor 卡顿 | 百万级 point index/tile/aggregation、固定 glyph size、局部更新和多类型同 tick |
| 持久化不一致 | JSON schema/protobuf descriptor/golden bytes、deterministic ZIP、damaged parent placeholder、旧布局拒绝 |

## 9. Requirement Trace

输入为 Project mixed parent/child source data、Conductor/direct content、runtime monitor state 和 UI viewport；正式输出为确定父子顺序、原子 Project command、audible Track set 与只读高性能概览。Project 只保存源层级和音乐数据；Mute/Solo、tiles、索引和 viewport 不持久化。消费者仍只能使用 Canonical Compiled Result，Arrangement 概览不得重建编译语义。

## 10. 实施结果（2026-08-19）

- 可见 Project Panel 已移除：布局列固定为零且内容折叠，菜单入口同时隐藏；Arrangement 是唯一外层对象创建、排序与导航界面。旧控件代码暂留为不可达兼容壳，不参与可见 UI 或正式顺序。
- Arrangement 已实现固定第一行 Conductor、Event Instrument/Root 混排父节点、Logical/Pure MIDI 子轨道、父节点折叠、二级缩进、Pure MIDI 图标、Root compact header、parent/child 独立 Mute/Solo，以及标题栏 `+` 创建菜单。
- Header 右键菜单与拖放已覆盖重命名、复制/剪切/粘贴/duplicate、删除、上下移、Root 设置、新增 child Track、跨父节点移动；Logical Track 跨 Instrument 继续走 rebind review，Pure MIDI Track 跨 Root 保留 direct/opaque 内容。
- Event Instrument subtree duplicate 与 `Duplicate Instrument Only` 已分离；Root duplicate/paste 强制 Auto，避免复制 Fixed Port.Channel 冲突。
- Logical Note 与 Direct MIDI Note 已支持共同字段的跨类型复制；Direct NoteOff velocity 在 Direct→Direct 中保留，在跨到 Logical 时按模型边界丢弃。
- Pure MIDI Segment 预览使用独立 Note/Event device-pixel tiles；event 线固定在 Note 上层、50% opacity、最小一设备像素宽，并按列聚合。Conductor 使用按事件类型着色的固定像素 point tiles，Project End Marker 单独绘制。
- 预览缓存 key 包含内容 fingerprint、viewport/DPI/style/layer，Note/Event 独立失效；hit testing 继续使用 tick/key/value 空间索引，不读取 bitmap。

## 11. Arrangement 紧凑布局与交互增量（2026-08-20）

- 父节点采用独立的缩放后紧凑行高；Header 使用较亮 Surface，右侧内容区以纯黑不透明覆盖 Bar/Grid 层。Conductor 与 child Track 继续使用完整音乐行高。
- Header 宽度扩展；Root 次级摘要压缩为 `Auto <Mode> <N> Tracks` / `P.<Port> Ch.<Channel> <Mode> <N> Tracks`；Logical 与 Pure MIDI Track 使用不同 Fluent 图标。
- Header 区域 `Ctrl + Wheel` 调整垂直缩放。可变行高的绘制、命中、滚动范围、选择、拖放和播放光标 overlay 共用同一累计行偏移。
- 父节点拖放预览与提交都规范化到顶层父节点边界；空白处右键只使用当前 hit test，不复用旧 Header 上下文。
- 工具栏新增 Expand All / Collapse All，并移除重复静态标题。展开状态仍只属当前 Project session。
- 本轮输入为 Project Arrangement projection、viewport 和鼠标交互；输出仅为 UI 行布局、命中与会话展开状态，不改变 Project/canonical/导出/音频语义。布局和展开状态不持久化，缩放受既有 session-state 边界约束。

## 12. 固定 Bar 网格、父行命中与极端内容增量（2026-08-20）

- Arrangement 和共享 piano roll 的可见 Grid 固定为 `Bar`；Toolbar 只保留 Grid 开关，不再暴露显示粒度选择。主线来自完整 Time Signature Map，每个分母拍绘制低强调子线；Ruler 显示一基小节号。Segment local tick 通过 `ProjectStartTick - ContentOffsetTick` 映射到 Project 拍号图，保证与 Arrangement 对齐。
- 极端水平缩小时按最小 device-pixel 间距跳过不可辨识的 Bar/beat 竖线，绘制工作量由可视宽度约束，不随不可见 tick 数量线性增长。
- 父节点右侧纯黑内容区不响应 Draw 创建、hover 创建预览或 Segment 命中；单击父 Header 任意非 Mute/Solo 区域即可展开/折叠。行高不足时隐藏次级摘要而不裁切文本。
- Arrangement 的最大垂直滚动位置按累计可变行高计算，最后一行完整停在水平 Overview 上方。播放或其他编辑锁期间红色 Fluent `+` 禁用。
- Pure MIDI Segment 的水平 Overview 通过 page summary/range provider 聚合 Note density，不要求解码整个 Segment 或建立全量 render item array；普通 materialized source 继续走既有 bounded-column 聚合。
- piano roll 的每 Key 高度被量化为不小于 3 的 device-pixel 整数；Note 顶边精确覆盖 Key 上分割线，总高度等于该 Key 高度，保证最小缩放下边框和填充均可辨。
- 本增量只改变 UI 投影、LOD 与会话 viewport；不改变 tick、Note、Segment、Time Signature、canonical、缓存音频或持久化语义。

自动证据：Presentation 129/129、Desktop 68/68；Core `midora-core.slnx` 1020/1020（含 Application 389/389）全部通过。未执行 computer-use、截图 golden 或人工拖放/高 DPI 验收。

## 13. Segment 几何、概览导航与对象识别增量（2026-08-20）

- Arrangement 的 Logical/Pure MIDI Segment（含边框）使用所属 child Track 的完整可见行高，顶边和底边分别贴合相邻轨道分割线；不改变 Segment 的 tick 边界、命中范围或缓存预览内容。
- Arrangement parent 内容区在 Draw 和 Select 等全部工具模式下都不启动 Segment 创建、对象拖动或 marquee 预览；parent Header 的折叠、菜单和排序交互保持独立。
- MIDI Segment 批量移动/复制以 primary Track 为锚点，按 Pure MIDI Track 的 Arrangement 相对顺序逐轨映射目标并逐目标 Track 验证 overlap。选区跨多个 Track 时不再把所有 Segment 错误汇入 primary 目标 Track；越过可用 Track 范围或真实重叠仍原子拒绝。
- Segment 水平 Overview 的 Note density 只统计 NoteOn 起点；长 Gate 不再把 Note 起点之后的空白列伪装成持续存在的 Note。分页 pack、materialized overlay 与普通新建内容使用同一投影语义。
- Overview 不再在 viewport thumb 左缘绘制固定红线。Playback cursor 使用红色实线、Edit cursor 使用蓝色虚线，按完整 overview extent 映射；tick 不在可表示范围时不绘制。
- Arrangement Header 图标和 Workspace Tab 图标统一使用已固定 revision 的 Fluent System Icons：Conductor/Wrench、Pure MIDI/MIDI、Logical/Music Note 2、Event Instrument/Guitar、Settings、Arrangement/Movies and TV。Track Header 以 20×20 原始坐标直接平移绘制，不再二次缩放到分数 device pixel；Toolbar `+` 使用 20×20 像素取整布局和有边框圆角按钮，展开/折叠使用 Arrow Expand All / Arrow Collapse All。
- Segment Workspace 内部左侧重复标题隐藏，但右侧 context 明细保留；Tab Header 继续展示 `Segment: <Name> @ <Tick>` 或 `MIDI Segment: <Name> @ <Tick>`。
- ComboBox popup 以 `MaxDropDownHeight` 参与实际 measure，并把 ScrollViewer 留在有界 Border 内，避免长 Controller 列表的垂直 ScrollBar thumb 被 popup 底边裁切。

本增量的 Project 输入只有 Segment 所属 Track、tick/length 与 Direct Note 起点；正式 Project 输出只发生于明确的批量移动/复制命令。图标、标题、overview density/cursor 和 popup 尺寸均为 UI runtime，不持久化。明确非目标是从 overview bitmap 反推命中、改变 Note Gate、放宽 Segment overlap 不变量或改变 canonical/audio 语义。音频运行期 Monitoring 代际修复另见 ADR-AUDIO-016。

自动证据：Application 390/390、Presentation 130/130、Desktop 69/69；event-stream/rolling 24/24；分页持久 Worker 的 Disable→Enable 持续推进用例分别通过托管测试 Worker 与本轮重新发布的正式 win-x64 Native AOT Worker。Desktop Release build 为 0 warning / 0 error。未执行 computer-use 或 WPF 实机视觉验收。
