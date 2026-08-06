# Tempo Sample Map Int64 反向换算需求追踪

状态：已实现  
日期：2026-08-06

## 1. 需求依据

- SRS 4.1.4：Tempo Map 必须以 decimal 累加完整区间秒数，乘采样率后只执行一次 AwayFromZero 取整；实时播放、预览和音频渲染共用该语义。
- SRS 4.1.4：只有 decimal 运算或最终整数结果超出可安全表示范围时，当前任务才明确失败。
- SRS 12.3：合法编译 tick 范围使用非负 `Int64` 左闭右开边界。

## 2. 输入与正式输出

- 输入：`originTick = 0`、`maximumTick = Int64.MaxValue`，且 TPQ、Tempo、sample rate 的组合使最大 tick 对应的 sample frame 仍可由 `Int64` 表示。
- 正式输出：`SampleFrameToTick` 在完整非负 `Int64` tick 范围执行确定性上界二分，返回不晚于目标 frame 的最大 tick；正向 tick→frame 公式和单次取整不变。

## 3. 缺陷与修正边界

- 原上取整中点 `(high - low + 1) >> 1` 在首次搜索 `[0, Int64.MaxValue]` 时因 `+1` 回绕为负数，随后把非法负 tick 传入正向映射。
- 修正将上取整写为 `distance / 2 + distance % 2`；`distance = high - low` 在 `0 <= low <= high <= Int64.MaxValue` 下总是可表示，最终中点不超过 `high`。
- 正向 decimal 或最终 frame 的真实溢出仍按 SRS 明确失败，不做饱和、不改变任务失败策略。

## 4. 持久化与消费者边界

- 无 Project 字段或持久化变更；该服务只属于 canonical 消费者的 sample-domain 换算。
- Playback 光标、实时调度与离线计划继续复用同一 `TempoSampleMap`。

## 5. 自动验证

- 使用 `TPQ = Int32.MaxValue`、Tempo 60,000,000 BPM、sample rate 1 Hz，使 `Int64.MaxValue` tick 对应 frame 仍可表示。
- 断言最大 frame 的反向搜索返回 `Int64.MaxValue`，并与正向映射一致；测试会直接覆盖旧实现的首轮回绕路径。
