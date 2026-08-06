# Midora 损坏占位稳定 ID Requirement Trace

状态：已实现并通过完整非 UI 发布门  
上位规范：SRS 2.5、12.6.3、16.5.3、16.19、16.21.1、22 INV-003

## 范围

- 输入：正常 Project 对象、`DamagedEventInstruments`、`DamagedLogicalTracks` 与 CompileContext Track 选择。
- 正式输出：Semantic Validation 诊断与 canonical 可消费性。
- 边界：损坏占位不参与音乐实例展开，但必须保留原稳定 ID；Project 内稳定 ID 跨对象类型全局唯一。
- 失败条件：参与当前编译作用域的损坏占位 ID 为零、超出 `nextStableId`，或与任何已纳入作用域的对象重复。
- 诊断：沿用 `MIDORA1003`；来源定位到对应损坏 Event Instrument 或 Logical Track ID。
- 持久化归属：不改变 `.midora` 格式；保存继续由既有 Damaged Placeholder 禁止门控制。
- 运行时归属：无。
- 非目标：不解析损坏 `.pb`；不让损坏占位参与实例展开；不让显式 Track 编译被未选且未引用的占位污染。

## 作用域规则

- Whole Project：检查全部损坏 Event Instrument 与 Logical Track 占位 ID。
- 显式 Track 集合：检查被所选正常 Track 引用的损坏 Event Instrument，以及 ID 位于显式 Track 集合中的损坏 Logical Track；其他占位不进入本次编译诊断。

## 验证

- 两类占位分别覆盖：与正常对象撞 ID、零 ID、`id == nextStableId`。
- 显式 Track 编译覆盖：未选 Track 与未参与 Event Instrument 的占位碰撞不污染健康选择。
- `Midora.Compiler.Tests` 211/211 通过。
- 完整非 UI 发布门 860/860、0 Skip、0 warning、0 error；Native AOT 产物位于 `artifacts/non-ui-release-gate-86a76d7a595949789295a381823d306b/`。
