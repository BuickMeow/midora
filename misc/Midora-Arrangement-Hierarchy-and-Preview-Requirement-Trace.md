# Midora Flat Arrangement and Preview Requirement Trace

状态：2026-08-20 设计已定案并完成破坏性实施，自动化回归已通过；旧开发期格式不兼容。
正式依据：SRS 第 24 章、INV-050、INV-052、INV-055、INV-058～064、INV-073～074，以及 `Midora-Flat-Arrangement-and-Shared-Usage-Architecture-Decisions.md`。

## 1. 输入

```text
Event Instrument Definitions and independent definition order
Event Instrument Usages and Usage→Definition references
Logical Tracks with optional Usage references
MIDI Channel Roots and Pure MIDI Track→Root references
one global mixed Arrangement Track order
Logical/Midi Segment exposed content windows
Direct MIDI Notes / non-Note events / opaque events
Conductor events
runtime Shared Usage/Root and Track Mute/Solo states
viewport, DPI, style revision and presentation snapshots
clipboard payload and destination context
```

## 2. 正式输出

```text
Conductor-first flat Arrangement projection
deterministic global mixed Track order
validated shared Usage/Root membership
Usage/Root connected active intervals and Channel Units
runtime audible Track set
atomic copy/move/share/detach commands
Logical↔Direct Note common-field conversion
cached Pure MIDI Note/Event overview layers, Event above Note at 50% opacity
cached Conductor point overview layer
strict .midora definition/usage/root/track index
```

## 3. 核心边界

- Conductor 固定第一行；其后只显示实际 Logical / Pure MIDI Track，不显示 Usage/Root 空行。
- Definition 可有零个 Usage；仍被 Usage 引用的 Definition 不得删除。
- 未绑定 Logical Track 只允许作为无 Segment、无音乐内容的空壳；开始承载内容前必须建立 Usage。
- Usage 与 Root 必须非空。最后一个成员删除或移出时，与该共享身份原子删除；Undo 恢复原 stable ID 与成员关系。
- 同 Usage 的 Logical Tracks 共享 Event Instrument 状态与一个 Unit；同 Root 的 Pure MIDI Tracks 共享 Channel 状态与一个 Unit。真正生命周期边界是成员 Segment 区间并集的活动连通区间。
- 共享 Usage 与 Auto Root 的成员必须在 global order 中连续，以 brace block 表示；Fixed Root 成员允许分散，UI 将路由表现为 Track 属性。
- Fixed Port.Channel 重复时，新 Track 加入已有 Root，不创建冲突 Root。空 Fixed Root 没有用户入口，也不得持久化。
- SMF 导入生成的 Track 顺序直接成为 global order；SMF 导出按 global order 过滤 Pure MIDI Track。
- Shared Usage/Root 与实际 Track 的 Mute/Solo 相互独立且只属于运行时监听，不进入 Project/canonical/导出/音频渲染。Shared group Mute 始终过滤成员；Shared group Solo 激活时忽略成员 Solo，但成员 Mute 仍生效。
- preview bitmap/tile 只属于 UI session；不得从 bitmap 反推命中或音乐语义。

## 4. Arrangement 交互

- 顶部 `< Event Instruments` / `> Event Instruments` 切换 Definition 管理栏；Definition order 与 Track order 独立。
- `+` 菜单提供 `New Logical Track`、`New Logical Track with Instrument...`、`New Raw MIDI Track...`。
- Track Header 拖动越过统一阈值后才生效。普通插入线表示 global reorder；共享块虚线轮廓表示加入组。
- 共享块上下内侧保留清晰的外部插入条；块体表示加入并追加到组末；同组成员拖动仍可精确重排。
- brace gutter 是独立命中区，用于整体移动共享块。单成员组使用可见 chip，允许其他 Track 拖入。
- 从组中移出到外部位置时创建独立 Usage/Auto Root；移入其他组时切换成员关系；全部操作单事务、可撤销。

## 5. 持久化归属

进入 `.midora`：Definition order、Usage/Root 对象、Track→Usage/Root 引用、global Track order、Track/Segment 内容、Direct NoteOff Velocity。
不进入 `.midora`：Definition 管理栏可见性、brace hover、drag overlay、selection、viewport、tiles、Mute/Solo、clipboard。

`project.json` 不保存 parent/child tree 或第二套 Root/Usage child order。Event Instrument Definition protobuf 不反向保存 Usage/Track IDs；Root protobuf 不反向保存 Track IDs。

## 6. 失败条件与诊断

- duplicate/missing Track order entry、断裂 Usage/Definition/Root 引用、空 Usage/Root、非连续 shared Usage/Auto Root、重复 Fixed Port.Channel：Error。
- 未绑定且含音乐内容的 Logical Track：Error；未绑定空壳允许存在但不参与编译。
- Definition 删除仍有 Usage：命令拒绝，不留下部分状态。
- 共享组拖放、复制、删除或重路由验证失败：整个命令失败并保持原 Project。
- UI tile 失败只进入 runtime trace/受控降级，不污染编译诊断。

## 7. 验证矩阵

| 风险 | 必要验证 |
|---|---|
| global order 漂移 | mixed-order save/open golden、SMF import/export order、乱序集合输入、Full/Incremental 等价 |
| Usage 语义错误 | 同 Definition 独立 Usage 隔离、共享 Usage 状态共享、跨 Track 连通区间、硬边界 Reset |
| Root 生命周期错误 | Fixed/Auto allocation、最后成员移出删除、Undo 恢复 stable ID、空 Root 拒绝 |
| 拖放歧义 | block body join、top/bottom outside strip、same-block reorder、brace block move、singleton chip |
| Copy/Delete 数据损坏 | complete ID remap、Definition reference policy、cancel/failure atomicity、Undo/Redo |
| Mute/Solo 错误可听集合 | shared group 单 Track mute/solo、快速切换、来源精确 Note 清理和状态恢复 |
| 跨类型 Note 丢字段 | Logical→Direct off velocity 0、Direct→Logical drop、Direct→Direct preserve、冲突/Undo |
| Segment preview 错位 | crop、pan、zoom、DPI、tile seam、Event-above-Note 50% opacity screenshot golden |
| UI 卡顿 | 极端 Note/Event Segment、scroll/zoom、独立 layer invalidation、UI-thread work budget |
| 持久化不一致 | JSON schema/protobuf descriptor/golden bytes、deterministic ZIP、旧树形布局严格拒绝 |

## 8. 明确非目标

```text
legacy development-format migration or dual write
visible empty Root/Usage row
user-visible Usage name
empty Fixed Root reservation
automatic conversion of arbitrary MIDI data into Event Instrument semantics
bitmap-based hit testing
cross-type event/parameter conversion
```
