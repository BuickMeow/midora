# Midora Damaged Event Instrument 编译边界 Requirement Trace

状态：已实现并通过正式非 UI 发布门。
日期：2026-08-06

上位规范：《Midora SRS》§3.9.5、§12.6.1、§16.19.1～16.19.3、§19.3.8，INV-001、INV-003、INV-009。

## 1. 输入与正式输出

- 输入：包含正常 Logical Track、正常 Event Instrument、可隔离 `DamagedEventInstruments` 占位及可选显式 Track 选择的 Project。
- Damaged Event Instrument 占位本身不作为正常定义参与编译。
- 正常 Track 若保留对 Damaged Event Instrument Stable ID 的绑定，本次 Track 不产生实例、Channel Unit 或 canonical 事件，并产生可定位 Error。
- 普通“引用不存在且没有对应 Damaged Placeholder”的断裂绑定继续按 SRS §12.6.1 作为未绑定 Track：产生 Info、保留数据、不阻止编译。

## 2. 诊断与失败边界

- 损坏绑定使用 `MIDORA1305`、`DiagnosticSeverity.Error`，来源同时包含 Track ID 和 Damaged Event Instrument ID。
- 该 Error 使结果不可消费，`FailureStage=SemanticValidation`，并遵循既有失败结果 `IsPartial=true` 契约。
- 显式 Track 编译只在损坏绑定 Track 被选择时失败；未选择该 Track 时不把其打开诊断重新注入任务结果。
- 损坏绑定不额外产生普通断裂 `MIDORA1303` 或未绑定非空 Track `MIDORA1304`，避免同一原因被降级描述两次。

## 3. 持久化与运行时归属

- Placeholder 的 ID、名称快照、包路径、错误和原排序位置仍由 `.midora` 打开/当前 Project 会话管理；本变更不新增持久化字段。
- 打开诊断与编译诊断保持分离；编译器只根据当前 Project 中的占位身份判断绑定不可用。
- 播放、预览、MIDI 导出和音频渲染继续只接受可消费 canonical result，因此无需各自重新解释 Damaged Placeholder。

## 4. 明确非目标

- 不实现 Project Repair Mode，不尝试从名称、路径或内容猜测替代 Event Instrument。
- 不允许 Damaged Placeholder 参与普通编辑或保存；删除/Undo 与保存禁止门沿用既有实现。
- 不把所有普通断裂引用升级为 Error；只有当前 Project 中存在同 Stable ID 的 Damaged Event Instrument 占位时使用本规则。

## 5. 自动验证门

- Whole Project 和显式选择损坏绑定 Track 均返回 `MIDORA1305` Error、不可消费 partial semantic failure。
- 显式只选择健康 Track 时成功，且没有损坏 Track/Instrument 来源诊断。
- 普通断裂 Event Instrument 引用仍为 `MIDORA1303` Info、结果可消费且无 Channel 事件。
- Compiler 全集和正式非 UI 发布门必须保持通过。
- 2026-08-06 正式门结果：Compiler 191/191、全仓 835/835、零 Skip；六个 Release solution 0 warning / 0 error；固定 BASS baseline 与 win-x64 Native AOT Worker 发布均通过。证据目录为 `artifacts/non-ui-release-gate-c10b79e97ef94f2f80d5d3b168f264d0/`。
