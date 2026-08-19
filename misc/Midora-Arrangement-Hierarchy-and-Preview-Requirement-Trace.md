# Midora Arrangement Hierarchy and Preview Requirement Trace

状态：2026-08-19 已实施并通过自动回归；WPF 实机视觉与拖放验收待产品所有者执行。  
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

自动证据：Presentation 123/123、Desktop 60/60；Core 中 hierarchy、clipboard、persistence、compiler、playback 与 MIDI export 相关测试随 `midora-core.slnx` 991/991 一并通过。未执行 computer-use、截图 golden 或人工拖放/高 DPI 验收。
