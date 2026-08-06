# Midora 编译器 Library Folder 诊断作用域 Requirement Trace

状态：已实现并通过完整非 UI 发布门  
上位规范：SRS 7.12–7.15、12.6.3、12.17、22 INV-003/INV-009

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
