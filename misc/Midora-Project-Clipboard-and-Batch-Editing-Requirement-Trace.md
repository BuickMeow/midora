# Midora Project Clipboard 与批量编辑非 UI Requirement Trace

日期：2026-08-07
状态：已实现并通过 Application 自动回归；Windows Clipboard/WPF composition 留到 UI 阶段
需求依据：《Midora SRS》第 20.4、20.5.4、20.6、20.7.7～20.7.8 节，INV-003、INV-012、INV-014、INV-015

## 1. 输入与正式输出

- 输入：当前 `ProjectDocumentSession`、稳定 ID 选择集、Primary Selection、目标容器、Edit Cursor、共享时间/数值 delta，以及明确的非空替换确认。
- 正式输出：一个经完整预检的 `IProjectEditCommand`；成功执行时形成一个 Project History entry，并同步刷新 incremental canonical。Clipboard Copy 输出不透明、只读的会话内 payload 与 Plain Text Summary，不修改 Project。
- Cut 输出分为两阶段：先生成 payload；只有 Windows Clipboard 写入成功后，调用方才执行 `DeleteAfterSuccessfulClipboardWrite`。Cut 的 Delete 和后续 Paste 分别形成独立 Undo。

## 2. 已实现对象范围

- Segment：跨 Track 相对映射、最早 Segment 对齐 Edit Cursor、Primary Track 对齐目标 Track；完整复制 crop、隐藏 Note、Lane、Point、断裂/不适用 Parameter ID。
- Logical Note：最早 tick 对齐；批量移动、共享 edge delta、velocity Exact Set/Relative Adjust、Align Start/End/Same Length、Delete、Duplicate。
- Logical Parameter：Whole Lane 与 Lane Content 分离；只接受 exact Parameter ID 和有效目标 Definition；批量 Point 时间移动、Exact Set/Relative Adjust、Delete。
- SubVoice：同一 Event Instrument 内复制 Template Event；三条 Mapping Chain、Target Settings 和 Step 全部深复制；合法外部 Parameter/Envelope/Function ID 保留。
- Value Curve：同一 Event Instrument、exact MIDI target 的 Point content。
- Conductor：Tempo、Time Signature、Key Signature、Marker；Project End Marker 不进入普通 Clipboard；多个同 tick Marker 保留，其他类型的同 tick 唯一性保持。
- Compatible ordered content：完整 Mapping Chain；目标非空替换要求显式确认。

## 3. 边界与失败条件

- payload 绑定来源 `ProjectDocumentSession` 身份；关闭/替换 Project 后，新 Document 必定拒绝旧 payload。路径相同或 Project 内容相同不构成同一会话。
- Copy 时冻结深快照；源对象随后删除或修改不改变 payload。每次新 Paste 命令首次 Apply 都分配全新 owned stable ID；Undo 不回退 allocator，Redo 重新挂接同一复制对象。
- 允许的外部引用保留稳定 ID，不按名称、显示范围或位置自动修复。目标 Context Reference 由显式目标容器决定。
- 全部 batch/Paste 在可见对象修改前完成集合、值域、时间溢出、Track 范围、Segment overlap、Point tick、Conductor tick、Template Event conflict、exact target 和确认门检查。失败不留下 Project 修改或 History entry；依照已确认 Q-NUI-005，若首次 Apply 的对象构造或后续编译失败已消耗 ID，高水位不回退。
- 同一次批量手势只形成一个 Undo；Redo 使用准备时冻结的结果，不重新读取 Grid/Snap。

## 4. 持久化与运行时归属

- Clipboard payload、Plain Text Summary、来源会话身份、Selection、Primary Selection、Edit Cursor 和 Windows Clipboard 内容都属于运行时/UI 会话，不写入 `.midora`，不参与 canonical fingerprint。
- Project 修改只通过统一 History 命令落入源模型；canonical 仍由同一次增量编译生成，消费者不读取 Clipboard。
- WPF 后续只负责 Windows Clipboard 读写、焦点/Selection、播放/任务 CanExecute 和成功后选择新对象；不得复制业务验证或在失败时部分粘贴。

## 5. 明确未包含内容

- Event Instrument、Logical Track、SubVoice Definition、Logical Parameter Definition、Mapping Function Definition、Project Settings 和 SoundFont 不进入普通 Clipboard；继续使用 Duplicate 或专用命令。
- 不支持跨 Project、跨应用重启、外部文件/文本猜测导入、内部 Clipboard 历史或多槽。
- Q-NUI-030 尚未决定前，不实现拍网格 Snap。当前批量命令接收 UI 已算出的共享 delta，未伪造 Grid 语义。

## 6. 自动证据

- `ProjectBatchTimelineEditCommandsTests`：10 项，覆盖 Note/Segment/Parameter Point 的原子批量语义、非法整批拒绝、一个 Undo、跨 Track 相对映射与 Full/Incremental oracle。
- `ProjectObjectClipboardTests`：12 项，覆盖全部 8 类 payload kind、深快照、fresh ID、外部引用、exact target、跨会话拒绝、全部 8 类 Cut 两阶段、Conductor 冲突与 Mapping Chain。
- 完整 `Midora.Application.Tests`：260/260 通过，0 Skip。
- 完整非 UI Release 门：964/964 通过，0 Skip；6 个 solution 的 Release build 均为 0 warning / 0 error；当前源码发布的 win-x64 Native AOT Worker 同时通过文件渲染与 held Preview 实时集成。证据目录：`artifacts/non-ui-release-gate-clipboard-20260807`。
