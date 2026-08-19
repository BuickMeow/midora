# Preview Event Instrument 上下文完整性需求追踪

状态：历史实现记录；其 Library Folder 上下文规则已由 SRS 第 24 章与 ADR-CORE-045 取代，不得作为当前 Preview 依赖
日期：2026-08-06

> 当前 Preview 仍必须复制完整的 Event Instrument 正式定义上下文，但不再存在 Folder/Unfiled/Library order。本文只保留旧实现的回归历史。

## 1. 需求依据

- SRS 13.1.1～13.1.3：Event Instrument、SubVoice 与 Segment Preview 只能改变临时输入、范围和选择，不得改变事件展开或诊断所依据的正式语义。
- SRS 7.3、7.5：Library Folder 是 Event Instrument Library 的正式归属引用；有效 Folder 绑定不应在临时上下文中变成断裂引用。
- SRS 12.6.3、12.19：选择型 CompileContext 只报告参与对象相关诊断，并保留真实严重级别与来源。

## 2. 输入与正式输出

- 输入：被预览的 Event Instrument 具有有效 `LibraryFolderId`，且源 Project 中存在对应 Folder。
- 正式输出：临时 Preview Project shell 同时复制所选 Instrument 和该 Folder 的稳定身份；语义验证不产生虚假的 `MIDORA1021` Folder 断裂 Warning。
- 若源 Project 中 Folder 本来就不存在，则仍保留现有 Warning；Preview 不替源数据修复断裂引用。

## 3. 边界与运行时归属

- 只复制当前参与预览 Instrument 实际引用且可解析的 Folder，不把未选择 Instrument 或无关 Folder 引入诊断作用域。
- Folder 只提供结构上下文，不参与 MIDI 事件展开、资源分配、播放过滤或持久化写回。
- 临时 shell 不修改源 Project，也不改变源 Project 的 `nextStableId`、Modified 或 Undo/Redo。

## 4. 自动验证

- 同一有效 Folder 绑定分别通过 Event Instrument Preview 与 Segment Preview 编译。
- 两种结果均可消费，且都不包含 `MIDORA1021`。
- 损坏绑定、未绑定和真实 Folder 断裂由相邻测试与语义验证测试继续覆盖。
