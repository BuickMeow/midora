# Preview Int64 范围需求追踪

状态：已实现  
日期：2026-08-06

## 1. 需求依据

- SRS 12.2.4、13.21～13.22：Event Instrument Preview 必须通过临时 Preview CompileContext 和 canonical compiled result，并覆盖 Gate End 后的 Release、Tail 与 Reset。
- SRS 12.3：所有编译范围采用可表示的 `[startTick, endTick)`，显式范围边界必须确定。
- SRS 12.18～12.20：语义错误应形成编译诊断；合法源值不能因实现的中间加法顺序泄漏裸算术异常。

## 2. 输入与正式输出

- 输入：合法的正 Preview Gate Length、Event Instrument Template Length 和非负 Envelope Release 分别都可由 `Int64` 表示，但为预览容器预留 `gate + template + maxRelease + 1` 时总和超过 `Int64.MaxValue`。
- 正式输出：临时 Segment 与 CompileContext 的右边界饱和到 `Int64.MaxValue`，编译器继续按该硬边界裁剪生命周期；canonical 事件、来源、资源分配及消费者语义不变。

## 3. 边界、失败与诊断

- 用户请求的 Gate Length 仍必须为正；Pitch、Velocity 与 Tempo 继续沿用原有输入检查。
- Template Length 或 Envelope 本身非法时，构造预览容器只把负时长按零预留，随后由统一 Semantic Validation 产生原有诊断；不以容器算术替代领域验证。
- `Int64.MaxValue` 是可表示的最终右边界；不尝试生成不可表示的 one-past tick，也不回绕为负数。

## 4. 持久化与运行时归属

- 该范围只属于一次 Event Instrument Preview 临时上下文，不修改 Project，不持久化，也不进入 Undo/Redo。
- Playback 继续只消费 canonical result，不自行补偿或重新计算 Preview 生命周期。

## 5. 自动验证

- `GateLengthTicks = Int64.MaxValue` 时预览不抛出 `OverflowException`，结果可消费且 `EndTick = Int64.MaxValue`。
- 未使用 Envelope 的 `ReleaseTicks = Int64.MaxValue` 时同样饱和，证明预留计算的每个加数都不会使临时范围回绕。
