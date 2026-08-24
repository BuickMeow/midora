# Snap-aware Resize Minimum — Requirement Trace

## Input

- 对 Arrangement 中的 Logical/MIDI Segment，以及 Logical Segment、MIDI Segment、SubVoice 三类 piano roll 中的 Note 执行左右边界拖动 Resize。
- 当前编辑器会话的 Snap Enabled 与 Operation Subdivision。
- 单个或批量选择对象在手势开始时的 start、end 与 length。

## Formal output

- Snap Enabled：每个被 Resize 对象的最小结果长度为当前有效 Operation Subdivision。
- Snap Disabled：最小结果长度为 `1 tick`。
- 手势开始前已经短于该步长的既有对象，以其原长度作为本次最小值，不因约束被反向扩长。
- 批量 Resize 对每个对象独立饱和；一次手势仍形成一个原子 Project Undo。

## Boundaries and failure conditions

- 左边界 Resize 固定原 end；除 Project/local tick 0 下界外，start 不得越过 `end - effective minimum`。
- 右边界 Resize 固定原 start；end 不得小于 `start + effective minimum`。
- `minimumLengthTicks < 1` 是调用方错误，正式编辑命令拒绝。
- Segment overlap、无效对象身份及其他既有领域验证保持不变；本需求不以静默移动邻居或修改其他对象来规避失败。

## Presentation/runtime ownership

- `TimelineSurface` 的 transient drag preview 使用绑定到 Surface 的有效 Operation Step 计算同一最小长度。
- Desktop 提交手势时把当前 `TimelineEditorSettings.EffectiveOperationStepTicks` 显式传给 Application edit command。
- Application command 对每个对象独立执行饱和并保留既有 Undo/Redo 与碰撞处理。

## Persistence and diagnostics

- Snap、Operation Subdivision 与本次 Resize 最小值均为 Project session UI state，不进入 `.midora`、canonical、导出或音频缓存。
- 成功饱和不产生诊断；既有 overlap/身份/范围错误继续使用原诊断与原子失败路径。

## Non-goals

- 不重新量化既有对象。
- 不改变新建 Segment/Note 的默认长度或拖动创建规则。
- 不改变 Properties、Batch Edit、Scale 等非边界拖动操作的长度规则。
- 不改变 Event/Parameter point，因为点没有 length。

## Verification

- Application tests：Logical/Direct/Template Note、Logical/MIDI/mixed Segment 的显式最小长度与逐对象饱和。
- Presentation tests：Snap step、Snap Disabled 的 `1 tick` 与既有短对象不被反向扩长。
- Desktop Release build：验证 UI → Application API 贯通。
