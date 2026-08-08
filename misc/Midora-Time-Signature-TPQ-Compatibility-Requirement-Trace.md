# Midora TPQ / Time Signature 兼容性与 Project 音乐位置 Requirement Trace

状态：Q-NUI-030 已确认；本记录先于实现建立，完成后补充验证证据

记录日期：2026-08-08

## 1. 需求依据

- 《Midora SRS》§4.1.2、§4.4、§4.9、§4.14～4.15：Project tick、TPQ、Time Signature、Bar/Beat 网格与诊断。
- 《Midora SRS》§12.5、§12.18～12.22：Conductor 编译、语义验证、Warning-as-error 与缓存失效。
- 《Midora SRS》§16.7.2、§16.8、§16.25.5：开发期 v1 Project Settings、Conductor 持久化和保存前一致性。
- 《Midora SRS》§20.1.4、§20.13.3：Project Timeline Grid/Snap 与 `Bar:Beat:Tick` 固定格式。
- Q-NUI-019：Time Signature 变化 tick 立即开启新 Bar；中途截断旧小节时必须产生 Warning。
- Q-NUI-030：每个 Time Signature 必须满足 `4 × TPQ % denominator == 0`；开发期 v1 直接修订，不创建迁移版本。

## 2. 输入

- Project `TicksPerQuarterNote`，合法范围 `1..32767`。
- Conductor Time Signature 集合；tick 非负、同 tick 唯一、tick 0 恰有一个，分子 `1..99`，分母属于 `1/2/4/8/16/32/64`。
- 待格式化、解析或吸附的非负 Project absolute tick。

## 3. 正式输出

- 每个有效拍号的整数 `ticksPerBeat = 4 × TPQ / denominator` 与 `ticksPerBar = numerator × ticksPerBeat`。
- 可逆 Project `Bar:Beat:Tick`：Bar/Beat 为 1-based，Tick offset 为 0-based；Project 起点为 `1:1:0`。
- 确定性的自然小节范围、拍边界、前后拍边界和最近拍 Snap 结果。
- 中途拍号截断 Warning；该 Warning 仍服从 CompileContext 的 Warning-as-error 策略。

## 4. 边界与算法

- Time Signature 变化从自身 tick 立即生效，并无条件从新的 Bar / Beat 1 开始；绝对 tick 不移动。
- 若变化 tick 不是前一拍号段起点加整数个旧 `ticksPerBar`，旧小节在该 tick 被截断，并产生一条 Warning。
- 每次拍号变化都重置自然拍网格；变化 tick 本身是拍边界和 Snap target。
- 最近拍 Snap 比较同一 tick 前后的实际 Project tick 距离；完全等距时选择后一个边界。该规则属于本次先实施、后确认的小决定 Q-NUI-043。
- 所有加法、乘法和 Bar 计数必须检查溢出；Project tick 仍为非负 `long`。Bar 使用 `ulong`，以覆盖最小一 tick 小节在 `long.MaxValue` 附近可能得到的 `long.MaxValue + 1` 个 1-based Bar 编号。
- Segment local tick、Template tick 和长度/Delta 不使用 Project `Bar:Beat:Tick`。

## 5. 失败条件与诊断

- `4 × TPQ % denominator != 0`：Domain 创建/编辑拒绝；semantic validation 产生 Error；持久化读取/保存拒绝该跨文件组合。
- Error 必须包含 TPQ、分母和 Time Signature 稳定 ID/tick 来源。
- `Bar:Beat:Tick` 中 Bar/Beat 非正、Tick offset 为负、Beat 超过当前分子、Tick offset 超过当前一拍，或位置落入被截断小节不可达尾部：解析/换算拒绝。
- 缺失 tick 0 拍号、同 tick 冲突、非法拍号或算术溢出：时间映射构造或操作失败，不产生近似结果。

## 6. 持久化与运行时归属

- `.midora` 继续只保存 TPQ 和 Time Signature 源数据，不保存 Bar index、网格、Snap 结果或派生时间映射。
- 兼容性是 `project-settings.json` 与 `conductor-track.json` 的跨文件 v1 一致性约束；不创建 v2 或迁移器。
- Project Time Signature Map 是可重建的运行时派生对象；TPQ 固定，Conductor 拍号编辑使其失效。

## 7. 明确非目标

- 不引入分数 tick、sub-tick 或拍边界量化。
- 不修改已有内容 absolute tick，不按拍号重排音乐。
- 不在本增量定义任意细分 subdivision 的 UI 列表或像素绘制策略；只提供无漂移的自然拍网格/Snap 基础。
- 不改变 Tempo、tick→seconds/sample、canonical MIDI 事件或声音语义。

## 8. 验证门

- TPQ/分母兼容与不兼容矩阵，包含 TPQ 1、192、480、32767 和全部合法分母。
- Domain 创建、Application Create/Update、semantic validation、Warning-as-error、persistence serialize/restore 的拒绝边界。
- tick 0、自然小节边界、中途截断、连续变拍、拍号变化 tick、`long.MaxValue` 邻域。
- `tick → Bar:Beat:Tick → tick` 往返；非法/被截断坐标拒绝。
- Beat grid 的 previous/next/nearest、跨变拍和等距向后选择。
- Full/Incremental 对同一 Conductor 修改的结果和诊断完全一致。
