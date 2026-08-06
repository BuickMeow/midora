# Midora 编译器无序输入确定性 Requirement Trace

状态：已实现并通过完整非 UI 发布门  
上位规范：SRS 2.6、8.10–8.11、11.4.4、12.14.6–12.14.8、22 INV-009/INV-010

## 范围

- 输入：Project Global / Event Instrument / SubVoice 的 MIDI Initial State 与 Project Reset Defaults 字典；C# Mapping Function 的声明 Context 字段集合；CompileContext 的 Track / SubVoice 选择集合。
- 正式输出：Canonical Compiled Result 的上下文摘要、Conductor、事件、Channel Unit 分配、诊断、统计和 fingerprint。
- 边界：Track、SubVoice、Segment、Logical Note、Template Event、Mapping Step 等显式 `List` 顺序属于项目内容或用户结构顺序，不纳入“仅改变容器插入顺序”的等价变换。
- 失败条件：仅改变上述无序容器的插入顺序后，任一正式输出字段发生变化。
- 诊断：两次编译的诊断内容和顺序必须一致；本门不引入新诊断。
- 持久化归属：这些源字段继续按既有 v1 契约确定性排序写出；不改变文件格式。
- 运行时归属：编译缓存和历史执行顺序不得进入正式结果。
- 非目标：不把显式用户排序改写为稳定 ID 排序；不改变资源分配优先级或同 tick 语义。

## 验证

使用同一合法 Project 构造跨两个 Logical Track、两个 SubVoice、三层 Initial State、Reset Defaults 和参与执行的 C# Mapping。第一次按正序插入全部无序集合，第二次仅反转插入顺序，并反转 CompileContext 集合构造顺序。逐字段比较两次全量编译的全部正式结果。

验证结果：`Midora.Compiler.Tests` 204/204 通过；其中新增门逐字段覆盖 context、events、allocations、conductor、diagnostics、statistics 与 fingerprint。完整非 UI 发布门 853/853、0 Skip、0 warning、0 error；Native AOT 产物位于 `artifacts/non-ui-release-gate-101f16d56a684539bb404186989bbc72/`。
