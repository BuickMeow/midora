# Event Instrument Loop Draft Requirement Trace

## 输入与源数据

- `Loop Start` 与 `Loop End` 是两个独立的可空 Event Instrument 源字段。
- 两端都空表示 Loop disabled；两端齐全表示完整 Loop；只有一端表示暂时不完整的编辑 Draft。
- 单个已存在端点必须仍处于 Template Length 内，完整 Loop 还必须满足 `Start < End`。

## 正式输出与失败边界

- 不完整 Draft 可以保存、打开和 Undo/Redo，但没有可听或导出语义。
- Full Compile 与 Incremental Compile 对不完整 Draft 都产生 `MIDORA1212` Error，canonical result 不可消费。
- Playback、Preview、MIDI Export 与 Audio Render 只消费成功 canonical result，因此不得直接解释部分 Loop。

## 持久化与运行时归属

- 两个可空端点继续由 Event Instrument protobuf 独立保存，不增加运行时或会话专用状态。
- 不完整 Draft 属于 Project 源数据和 Project History；编译诊断属于派生结果。

## 明确非目标

- 不改变完整 Loop 的生命周期、循环边界或可听语义。
- 不允许端点越出 Template Length，也不允许完整 Loop 的 `Start >= End`。
- 不放宽 Loop 对 Per-Note Instance Isolation 的要求。
