# Midora Project MIDI Export Settings Requirement Trace

日期：2026-08-07
状态：领域、History 与持久化 v1 已实施并通过 Core 全解测试
决定依据：Q-NUI-003、ADR-CORE-037
规格依据：《Midora SRS》14.7、14.15、14.16、16.7.3、19.5.7～19.5.8

## 输入与正式输出

- 输入：用户明确保存为 Project Defaults 的 MIDI Export 参数，或 `.midora` v1 `settings/export-settings.json`。
- 正式输出：Mode、Range、Track Selection 策略、Routing、Include Readme、Treat Warnings As Errors 的完整 Project 默认快照。
- 默认：Whole Project / Project Default Range / All Valid Logical and Pure MIDI Tracks / Compact / Readme 开 / Warning-as-error 关。

## 边界、失败与诊断

- 只有 Manual Range 保存 `manualStartTick` 与 `manualEndTick`，且 `0 <= start < end`；其他范围模式禁止携带这两个字段。
- 未知枚举、未知/重复/缺失 JSON 字段及不一致范围严格拒绝。作为 ordinary settings 打开失败时恢复当前默认、产生 Error 并标记 Modified。
- 具体显式 Track ID、输出路径、最近目录、覆盖授权、任务进度和产物不持久化。

## History 与运行时归属

- 设置变更作为单个 History entry 原子执行；Undo 精确恢复全部字段；no-op 不进入 History。
- Export Settings 属于 Project 源数据，但不改变 Project 音乐语义和当前 canonical fingerprint。
- 具体 Track 勾选、输出规划和编译结果只属于一次性冻结任务。

## 验证证据

- Application 219/219：完整设置原子提交/Undo、默认恢复、非法范围和未知枚举、canonical fingerprint 不变。
- Persistence 87/87：默认与 Manual 完整往返、未知值拒绝、package round-trip、ordinary settings 损坏恢复。
- Compiler 219/219 继续通过；完整发布门按累计基线统一执行。
