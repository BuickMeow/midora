# Midora Mapping 编辑与 Note 放置优化 Requirement Trace

状态：Implemented
日期：2026-08-15
范围：Event Instrument Mapping 编辑、Instance Velocity 语义例外、Segment/SubVoice Piano Roll Note 放置

## 1. 需求依据与冲突记录

- 用户明确要求：Step 的 Parameter/Envelope/Function 引用使用对象下拉；Parameter Mapping 可修改 route 并重排；Mapping Chain 有完整 Inspector；Add Step 只显示当前链上下文合法的 Source。
- 用户明确要求：无 Per-Note Instance Isolation 时允许 `TriggerVelocity → Note Velocity`，并新增默认开启的 `Follow Instance Velocity`。
- 用户明确要求：Draw 空白创建 Note 时，按住后可上下拖动改变 Key，并通过既有独立 pure-Note preview route 试听。
- SRS 8.53.3 的 fixed template velocity 默认、SRS 9.9 的 blanket isolation、SRS 18.2.4 的 placement 不发声规则与本次明确决定冲突。本轮按产品所有者的新决定实施并记录 ADR-UI-031/032；不静默修改 SRS 原文。

## 2. Requirement Trace

| 项目 | 正式约束 |
|---|---|
| 输入 | Event Instrument、精确 Mapping Chain owner/target、SubVoice、Mapping Step、Logical Note instance velocity；Piano Roll pointer position 与 Editor Snap。 |
| 正式输出 | 所有 Mapping 修改仍通过 `IProjectEditCommand` 修改 Project Source Data；Compiler 产生 Canonical Compiled Result；Note 放置在 MouseUp 产生一个 Note 创建 command。 |
| UI 投影 | Inspector choice 的 value 保存 Stable ID，label 显示对象名称；UI 不按名称建立身份。Parameter Mapping route 修改与重排分别是原子 History 项。 |
| Source 过滤 | 由精确 chain target 和 Isolation 状态计算；UI 不提供已知必定非法的 Source/Function，但 Semantic Validator仍是正式门。 |
| Velocity 例外 | 只允许共享 `TemplateEventKind.Note + Value` 链直接使用 `TriggerVelocity`；其他 per-note source/target 组合保持 `MIDORA1214`。 |
| 默认 Follow | 新建默认 SubVoice/new SubVoice 创建普通 `TriggerVelocity / Override` Step；复制与加载保留源链，不重置默认。 |
| 边界 | Note Number 有任何 active Step 且无 Isolation 仍失败；Envelope、per-note C# context、Loop、Let Overlap 等既有 Isolation 约束不变。 |
| 放置范围 | Segment Logical Note 与 SubVoice Template Note Piano Roll；pitch 始终 clamp 为 0..127，最终 length 至少 1 tick。 |
| 试听运行时 | 复用持久音频 Worker 内独立 1-channel pure-Note stream；pitch 变化执行 All Sound Off + NoteOn，结束执行 NoteOff + All Sound Off。 |
| 失败条件 | 引用对象消失、目标非法、Note Number overflow policy 非 Fail、Stable ID 重复、音频 preview 不可用；Project command必须失败原子。 |
| 诊断 | 非法持久数据继续由现有 Mapping/Isolation diagnostics报告；UI 过滤不新增伪 canonical 诊断。试听失败属于 runtime error，不改变 Note 创建数据。 |
| 持久化归属 | Mapping Chain/Step 属于 `.midora` Project Source Data；Follow 不新增字段。对象下拉、hover、draft Note 和 audition 状态不持久化。 |
| 非目标 | 不放宽 TriggerVelocity 到 CC/Program/RPN/NRPN/Pitch/Note Number；不改变 Channel Unit 分配；不让 UI 绕过 Compiler；不引入每事件 Mapping Chain。 |

## 3. 验证门

- Compiler：无 Isolation 的 TriggerVelocity→Note Velocity 可消费并输出 instance velocity；同 Source→CC 仍产生 `MIDORA1214`。
- Application：Parameter Mapping route 一次原子修改/Undo；Mapping Chain target settings；Follow preset enable/disable/Undo；默认创建与复制保持确定 Stable ID。
- Desktop：Release build/XAML compile；Inspector choice 使用 Stable ID value + display label；Source policy 覆盖 Note Number、Note Velocity、非 Note 与 Logical Parameter chain。
- Piano Roll：最终创建使用 MouseUp 时的 pitch；跨 lane 只在 pitch 实际变化时发试听更新；取消路径结束试听且不提交。
