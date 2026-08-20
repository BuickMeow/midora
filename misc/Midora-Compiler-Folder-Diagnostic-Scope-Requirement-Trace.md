# Midora 编译器 Library Folder 诊断作用域 Requirement Trace

状态：历史实现记录；其 Library Folder 正式模型已由 SRS 第 24 章、INV-058/INV-062 与 ADR-CORE-046 取代，不得作为当前实现需求
上位规范：SRS 7.12–7.15、12.6.3、12.17、22 INV-003/INV-009

> 本文保留 2026-08-06 的测试证据。当前开发格式已删除 Event Instrument Folder、Unfiled、Library manual order 及对应诊断作用域；现行编译器必须验证 Definition index、Event Instrument Usage、MIDI Channel Root、Track membership 与 global mixed Track order 的一致性。

## 范围

- 输入：CompileContext 的显式 Track 集合、参与 Track 绑定的 Event Instrument，以及 Event Instrument Library Folder 定义。
- 正式输出：Folder 结构/名称诊断、断裂引用诊断、稳定 ID 诊断与 canonical 可消费性。
- 边界：Folder 不产生音乐事件；显式 Track 编译只需要参与 Event Instrument 所引用的 Folder 依赖闭包。
- 失败条件：依赖闭包内 Folder 的 ID、名称或全局身份冲突非法；Whole Project 中任意 Folder 非法。
- 诊断：沿用 `MIDORA1020`、`MIDORA1021`、`MIDORA1003`。
- 持久化归属：不改变 Folder 持久化或 Project 手动顺序。
- 运行时归属：无。
- 非目标：不改变 Whole Project 诊断；不把 Folder 顺序或名称加入 canonical；不隐藏参与 Instrument 的断裂 Folder Warning。

## 作用域规则

- Whole Project：全部 Folder 参与结构、引用与稳定 ID 验证。
- 显式 Track 集合：先求参与 Event Instrument ID，再求其非空 `LibraryFolderId` 集合；只验证该 Folder 集合及其引用。
- 同 ID 的多个 Folder 在依赖闭包中全部参与，以便稳定报告重复身份。

## 验证

- 未参与 Folder 同时具有保留名称和跨类型重复 ID：Whole Project 报 `MIDORA1020` / `MIDORA1003`，健康 Track 选择不受污染。
- 参与 Instrument 引用保留名称 Folder：显式 Track 编译在 Semantic Validation 阶段失败。
- `Midora.Compiler.Tests` 213/213 通过。
- 完整非 UI 发布门 862/862、0 Skip、0 warning、0 error；Native AOT 产物位于 `artifacts/non-ui-release-gate-c7ab8b35c36e489ab813d7508e09eba8/`。
