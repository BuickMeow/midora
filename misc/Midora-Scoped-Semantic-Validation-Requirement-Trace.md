# Midora 显式 Track 编译诊断作用域 Requirement Trace

状态：历史实施追踪；scoped validation 原则仍有效，但 Library Folder 参与规则已由 SRS 第 24 章和 ADR-CORE-045 取代。
日期：2026-08-06

> 当前显式 Track 编译的结构依赖闭包必须包含其唯一 Event Instrument/Root parent，并验证 mixed parent union 与 parent-child 三方一致性；不再验证 Folder/Unfiled/Library order。本文中的 Folder 断裂用例只保留为旧实现证据。

上位规范：《Midora SRS》§12.6.3、§12.18、§12.19，INV-009、INV-010、INV-015。

## 1. 输入与正式输出

- 输入：完整 Project Source Data 与可选的 `CompilationRequest.IncludedTrackIds`。
- Whole Project 输入未给出 Track 选择集合时，语义验证继续检查全部 Track、全部 Event Instrument 与全 Project Stable ID 唯一性。
- 显式 Track 输入只为被选择 Track、其引用的 Event Instrument 和始终参与的 Project/Conductor/Global 上下文产生本次编译诊断；未参与 Instrument 的断裂 Library Folder 引用不产生本次 Warning。
- 正式输出仍是同一 `CanonicalCompiledResult`；作用域变化不改写 Project，也不把未选内容复制或删除。

## 2. 边界与失败条件

- 被选择 Track 中的 Stable ID 空值、重复值或超出 `nextStableId` 仍是 `MIDORA1003` Error。
- 被选择 Track ID 不存在仍是 `MIDORA1302` Error；重复且因此选择歧义的 Track ID 仍是 `MIDORA1301` Error。
- 未选择 Track 及未被任何选择 Track 引用的 Event Instrument，其内部 Stable ID 损坏或彼此重复的 Instrument ID 不进入本次 scoped compilation 诊断；对同一 Project 执行 Whole Project compile 时必须完整报告。
- Conductor、全局 Initial/Reset、Library Folder 等非 Track 选择对象仍属于正式全局上下文，不被 Track 选择静默排除。

## 3. 诊断、持久化与运行时归属

- 本变更只修正编译诊断作用域；诊断仍保留原代码、严重度与 Stable ID 来源。
- `.midora` 格式、JSON/protobuf schema、Project Stable ID 分配、Undo/Redo 与 Modified 状态均不改变。
- 播放、MIDI 导出、音频渲染和分 Track 渲染只消费其冻结 CompileContext 的结果，不允许额外重新验证未选 Track 并污染任务结果。

## 4. 明确非目标

- 不把 scoped compile 替代 Whole Project Diagnostics；全项目验证仍是发现未选内容损坏的正式入口。
- 不放宽被选内容、参与 Event Instrument、Conductor 或全局状态的任何结构和值域规则。
- 不改变 `IncludedSubVoiceIds` 的既有语义，也不涉及 Q-NUI-005 的 Stable ID 分配/Undo 决定。

## 5. 自动验证门

- 同一 Project 的 Whole Project compile 必须捕获未选 Track 内重复 Stable ID，scoped compile 必须成功且不含该来源诊断。
- 同一 Project 的 Whole Project compile 必须捕获未参与 Event Instrument 内重复 Stable ID，scoped compile 必须成功且不含该 Instrument 来源诊断。
- Whole Project compile 必须保留未参与 Event Instrument 的断裂 Folder Warning；启用 Warning-as-error 的 scoped compile 必须忽略该未参与来源并成功。
- 两个未参与 Event Instrument 使用相同 ID 时，Whole Project compile 必须报告 `MIDORA1201`；scoped compile 必须既不误诊，也不得因构建全 Project dictionary 而抛异常。
- Compiler 全集、全仓正式非 UI 发布门及 Full/Incremental 既有 oracle 必须保持通过。
- 2026-08-06 正式门结果：Compiler 189/189、全仓 833/833、零 Skip；六个 Release solution 0 warning / 0 error；固定 BASS baseline 与 win-x64 Native AOT Worker 发布均通过。证据目录为 `artifacts/non-ui-release-gate-d998705683cc4fcf953c6a9e0269919f/`。
