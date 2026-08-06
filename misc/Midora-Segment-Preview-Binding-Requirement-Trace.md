# Segment Preview 绑定前置条件需求追踪

状态：已实现  
日期：2026-08-06

## 1. 需求依据

- SRS 13.24.1～13.24.3：Segment Preview 只播放指定 Segment，并明确禁止未绑定 Event Instrument 或绑定引用断裂的 Track 进入预览。
- SRS 13.1.2～13.1.3：预览编译失败时不得进入 Playing，也不得产生 partial 播放结果；专用 CompileContext 可以改变预览对象与范围，但共享正式编译语义。
- SRS 12.6.1：主时间线、MIDI 导出与音频渲染中的未绑定 Track 仍按 Info 和无输出处理；该一般规则不能覆盖 Segment Preview 的更严格前置条件。

## 2. 输入与正式输出

- 输入：`SegmentPreview` CompileContext 选择的正常 Logical Track 未设置 Event Instrument ID，或 ID 不解析到正常对象且也不是 Damaged placeholder。
- 正式输出：语义验证增加 `MIDORA1306` Error，保留 Track ID 来源；canonical 结果不可消费且 `IsPartial=true`。
- 普通断裂引用仍同时保留 `MIDORA1303` Info，用于说明源绑定状态；未绑定非空内容仍可保留 `MIDORA1304` Info。

## 3. 边界与失败条件

- Damaged Event Instrument 绑定继续使用更具体的 `MIDORA1305` Error，不重复产生 `MIDORA1306`。
- Playback、MIDI Export、Audio Render 等非 Segment Preview 上下文不受 `MIDORA1306` 影响，未绑定/普通断裂语义保持原样。
- Segment 可以为空；只要 Track 没有可用绑定，仍不允许预览。

## 4. 运行时与持久化归属

- Playback Controller 在调用 Backend `Prepare` 前检查 Preview canonical 可消费性，因此 `MIDORA1306` 会释放 Project edit lock、进入 Error 状态且不初始化后端；该顺序同时适用于 Event Instrument、SubVoice 与 Segment Preview。
- 本次不修改 Project、不修复引用、不进入 Undo/Redo，也不改变 `.midora` 格式。

## 5. 自动验证

- Compiler：未绑定与普通断裂两种输入均得到精确 `MIDORA1306`；只有断裂输入包含 `MIDORA1303`。
- Playback：未绑定 Segment Preview 在 Backend Prepare 前失败，任务身份与编辑锁被清理，错误消息保留诊断码。
- 损坏占位由相邻 `MIDORA1305` Preview 测试继续覆盖。
