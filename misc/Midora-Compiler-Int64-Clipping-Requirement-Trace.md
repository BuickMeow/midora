# Compiler Int64 裁剪边界需求追踪

状态：已实现  
日期：2026-08-06

## 1. 需求依据

- SRS 11.11.2、11.11.6：Logical Note 的 `startTick >= 0`、`length > 0`，允许跨出 Segment 当前有效裁剪窗口；超出部分保存但不参与编译。
- SRS 11.5、12.3、12.6.5、12.10：Segment、编译范围与实例结束统一使用左闭右开硬边界，必须先裁剪再生成输出。
- SRS 12.18～12.20：合法源数据不得因实现算术顺序抛出未分类异常；失败应由语义诊断表达。

## 2. 输入与正式输出

- 输入：Segment 的有效 Project 范围可表示但接近 `Int64.MaxValue`，Logical Note 或生命周期自然时长远大于 Segment 剩余范围。
- 正式输出：Gate End 与自然结束先限制到 `segmentEnd - projectStart`，再执行可证明安全的加法；实例、分配、NoteOff/Reset 均精确结束于 Segment 硬边界。
- 普通范围内输入的 tick、事件顺序、生命周期与资源分配语义不变。

## 3. 边界与失败条件

- Segment 自身的 Project/Content 范围不可表示时仍由 `MIDORA1310` Error 拒绝。
- Logical Note 自身的 Content-domain `start + length` 不可表示时仍由 `MIDORA1320` Error 拒绝。
- 本次只处理“各源值本身合法，但翻译到靠近 Int64 上界的 Project-domain 后应被 Segment 裁剪”的情况。
- Gate + Release、Gate + Loop Tail、OneShot/Template Length 与 Template Note Off 都使用同一有界时长计算，避免先溢出后取 `Min`。
- Project/content tick 翻译先计算同一坐标域内的差值，再加另一坐标域的基点；裁剪窗外的 Template Event 在 Project tick 加法前排除。
- Loop 内迭代以“距 Gate 剩余量”判断终止，Loop 后 tail occurrence 使用饱和到 `Int64.MaxValue` 的局部 tick；达到右边界即停止，不执行越界的下一次递增。

## 4. 诊断、持久化与运行时归属

- 无新增 Project 字段或持久化版本；隐藏裁剪区源数据原样保留。
- 不产生新诊断，因为该输入按 SRS 是合法的；canonical 消费者只看到已裁剪的可表示 tick。
- MIDI、Playback 与 Audio Render 无需各自补偿，继续只消费 canonical 结果。

## 5. 自动验证

- Segment 为 `[Int64.MaxValue-10, Int64.MaxValue)`，Logical/Template Note Length 与 OneShot Template Length 均为 `Int64.MaxValue`，并包含一个远在裁剪窗外的 Template Event。
- 断言编译不抛异常、结果可消费、统一 `endTick=Int64.MaxValue`，且 allocation 精确限制为最后 10 ticks。
- `ContentOffsetTick` 同样靠近 `Int64.MaxValue` 时，断言差值优先的坐标翻译生成精确 Project tick。
- Gate、Template Length 与 Loop 边界共同接近 `Int64.MaxValue` 时，断言 Loop 内有效 occurrence 保留、边界外 tail 丢弃且迭代正常终止。
- 既有测试继续覆盖 Segment 本身不可表示时返回 `MIDORA1310` 而不是抛异常。
